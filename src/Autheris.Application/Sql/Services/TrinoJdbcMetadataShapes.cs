namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autheris.Domain.Model;

/// <summary>One visible object with the outcome of the access decision for the caller (input of the <c>system.jdbc</c> views).</summary>
internal sealed record TrinoJdbcEntry(DiscoveryObject Object, bool Accessible, IReadOnlySet<string> AllowedColumns);

internal enum TrinoJdbcView
{
    Catalogs,
    Schemas,
    Tables,
    TableTypes,
    Columns
}

internal enum TrinoJdbcOperator
{
    Eq,
    IsNull,
    Like,
    In
}

internal sealed record TrinoJdbcCondition(int Column, TrinoJdbcOperator Operator, string[] Values, TrinoLikePattern? Pattern);

internal sealed record TrinoJdbcOrderKey(int Column, bool Descending);

/// <summary>A parsed <c>system.jdbc</c> statement: output columns, conjunctive filters and ordering over the view columns.</summary>
internal sealed record TrinoJdbcQuery(
    TrinoJdbcView View,
    int[] Projection,
    IReadOnlyList<TrinoJdbcCondition> Conditions,
    IReadOnlyList<TrinoJdbcOrderKey> OrderBy);

/// <summary>
/// Equality and <c>IN</c> filters extracted for catalog, schema and table axes so discovery results can be pruned before
/// loading catalog metadata and deciding access. <c>LIKE</c> predicates are never pushed down.
/// </summary>
internal sealed record TrinoJdbcPushdownKeys(
    IReadOnlySet<string>? Catalogs,
    IReadOnlySet<string>? Schemas,
    IReadOnlySet<string>? Tables)
{
    public bool Matches(DiscoveryObject item) =>
        (Catalogs is null || Catalogs.Contains(item.Domain)) &&
        (Schemas is null || Schemas.Contains(item.Schema)) &&
        (Tables is null || Tables.Contains(item.Name));
}

/// <summary>
/// The grammar and the in-memory evaluation of the Trino <c>system.jdbc</c> metadata views. A statement outside the grammar
/// (any <c>OR</c>, function, join, <c>UNION</c>, subquery, aggregate, <c>LIMIT</c>, unknown view or schema) is rejected with
/// a fixed <see cref="NotSupportedException"/> and is never forwarded to a source.
/// </summary>
internal static class TrinoJdbcMetadataShapes
{
    internal const string NotSupportedMessage = "The metadata statement shape is not supported.";

    private const int MaxConjuncts = 8;
    private const int MaxInValues = 64;
    private const int MaxClauseLength = 4096;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    internal static readonly Regex JdbcStatementRegex = new(
        @"^SELECT\s+(?<cols>.+?)\s+FROM\s+system\.jdbc\.(?<view>catalogs|schemas|tables|table_types|columns)\b(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex MixedShapeRegex = new(
        @"\b(?:join|union|intersect|except|select)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex FromKeywordRegex = new(
        @"\bfrom\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly string[] CatalogsColumns = ["TABLE_CAT"];

    private static readonly string[] SchemasColumns = ["TABLE_SCHEM", "TABLE_CATALOG"];

    private static readonly string[] TableTypesColumns = ["TABLE_TYPE"];

    private static readonly string[] TablesColumns =
    [
        "TABLE_CAT", "TABLE_SCHEM", "TABLE_NAME", "TABLE_TYPE", "REMARKS", "TYPE_CAT", "TYPE_SCHEM", "TYPE_NAME",
        "SELF_REFERENCING_COL_NAME", "REF_GENERATION"
    ];

    private static readonly string[] ColumnsColumns =
    [
        "TABLE_CAT", "TABLE_SCHEM", "TABLE_NAME", "COLUMN_NAME", "DATA_TYPE", "TYPE_NAME", "COLUMN_SIZE", "BUFFER_LENGTH",
        "DECIMAL_DIGITS", "NUM_PREC_RADIX", "NULLABLE", "REMARKS", "COLUMN_DEF", "SQL_DATA_TYPE", "SQL_DATETIME_SUB",
        "CHAR_OCTET_LENGTH", "ORDINAL_POSITION", "IS_NULLABLE", "SCOPE_CATALOG", "SCOPE_SCHEMA", "SCOPE_TABLE",
        "SOURCE_DATA_TYPE", "IS_AUTOINCREMENT", "IS_GENERATEDCOLUMN"
    ];

    internal const string AccessGranted = "access: granted";
    internal const string AccessNotAccessible = "access: not accessible";

    internal static NotSupportedException NotSupported() => new(NotSupportedMessage);

    internal static string[] ColumnNames(TrinoJdbcView view) => view switch
    {
        TrinoJdbcView.Catalogs => CatalogsColumns,
        TrinoJdbcView.Schemas => SchemasColumns,
        TrinoJdbcView.Tables => TablesColumns,
        TrinoJdbcView.TableTypes => TableTypesColumns,
        _ => ColumnsColumns
    };

    /// <summary>
    /// True when the statement references a <c>system.*</c> or <c>information_schema.*</c> relation. Comments are skipped
    /// and quoted identifiers decoded only outside string literals; a Unicode-escape identifier (<c>U&amp;"..."</c>) is
    /// always treated as a reference because its name cannot be read without decoding.
    /// </summary>
    internal static bool ReferencesMetadataRelation(string sql)
    {
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                SkipTrivia(sql, ref i);
            }
            else if (c == '\'')
            {
                i = SkipQuoted(sql, i, '\'', out _);
            }
            else if (c is '"' or '`')
            {
                if (c == '"' && i >= 2 && sql[i - 1] == '&' && (sql[i - 2] is 'U' or 'u'))
                {
                    return true;
                }

                i = SkipQuoted(sql, i, c, out var name);
                if (IsMetadataName(name) && FollowedByDot(sql, i))
                {
                    return true;
                }
            }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i;
                while (i < sql.Length && (char.IsAsciiLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$'))
                {
                    i++;
                }

                if (IsMetadataName(sql[start..i]) && FollowedByDot(sql, i))
                {
                    return true;
                }
            }
            else
            {
                i++;
            }
        }

        return false;
    }

    private static bool IsMetadataName(string name) =>
        string.Equals(name, "system", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "information_schema", StringComparison.OrdinalIgnoreCase);

    private static bool FollowedByDot(string sql, int index)
    {
        SkipTrivia(sql, ref index);
        return index < sql.Length && sql[index] == '.';
    }

    /// <summary>Skips whitespace and comments.</summary>
    private static void SkipTrivia(string sql, ref int i)
    {
        while (i < sql.Length)
        {
            if (char.IsWhiteSpace(sql[i]))
            {
                i++;
            }
            else if (sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }
            }
            else if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>Returns the index after the closing quote (a doubled quote is an escape); an unterminated quote runs to the end.</summary>
    private static int SkipQuoted(string sql, int start, char quote, out string content)
    {
        var sb = new StringBuilder();
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    sb.Append(quote);
                    i += 2;
                    continue;
                }

                content = sb.ToString();
                return i + 1;
            }

            sb.Append(sql[i++]);
        }

        content = sb.ToString();
        return sql.Length;
    }

    /// <summary>
    /// An <c>information_schema</c> statement that also joins, unions or nests another relation is a mixed statement and
    /// is refused; a plain single-relation statement passes.
    /// </summary>
    internal static void EnsureNotMixed(string columnsAndRest)
    {
        if (MixedShapeRegex.IsMatch(columnsAndRest) || FromKeywordRegex.Count(columnsAndRest) > 1)
        {
            throw NotSupported();
        }
    }

    /// <exception cref="NotSupportedException">The statement is outside the supported grammar.</exception>
    internal static TrinoJdbcQuery Parse(Match match)
    {
        var view = match.Groups["view"].Value.ToLowerInvariant() switch
        {
            "catalogs" => TrinoJdbcView.Catalogs,
            "schemas" => TrinoJdbcView.Schemas,
            "tables" => TrinoJdbcView.Tables,
            "table_types" => TrinoJdbcView.TableTypes,
            "columns" => TrinoJdbcView.Columns,
            _ => throw NotSupported()
        };

        var names = ColumnNames(view);
        var projection = ParseProjection(match.Groups["cols"].Value, names);

        var rest = match.Groups["rest"].Value;
        if (rest.Length > MaxClauseLength)
        {
            throw NotSupported();
        }

        var tokens = Tokenize(rest);
        var projected = new HashSet<int>(projection);
        var pos = 0;
        var conditions = new List<TrinoJdbcCondition>();
        if (IsKeyword(tokens, pos, "WHERE"))
        {
            pos++;
            while (true)
            {
                conditions.Add(ParseCondition(tokens, ref pos, names));
                if (conditions.Count > MaxConjuncts)
                {
                    throw NotSupported();
                }

                if (IsKeyword(tokens, pos, "AND"))
                {
                    pos++;
                    continue;
                }

                break;
            }
        }

        var order = new List<TrinoJdbcOrderKey>();
        if (IsKeyword(tokens, pos, "ORDER"))
        {
            pos++;
            if (!IsKeyword(tokens, pos, "BY"))
            {
                throw NotSupported();
            }

            pos++;
            while (true)
            {
                var column = ColumnIndex(ExpectIdentifier(tokens, ref pos), names);
                if (!projected.Contains(column))
                {
                    throw NotSupported();
                }

                var descending = false;
                if (IsKeyword(tokens, pos, "DESC"))
                {
                    descending = true;
                    pos++;
                }
                else if (IsKeyword(tokens, pos, "ASC"))
                {
                    pos++;
                }

                order.Add(new TrinoJdbcOrderKey(column, descending));
                if (order.Count > names.Length)
                {
                    throw NotSupported();
                }

                if (pos < tokens.Count && tokens[pos].Kind == TokenKind.Comma)
                {
                    pos++;
                    continue;
                }

                break;
            }
        }

        if (pos != tokens.Count)
        {
            throw NotSupported();
        }

        return new TrinoJdbcQuery(view, projection, conditions, order);
    }

    /// <summary>
    /// Extracts conjunctive equality and <c>IN</c> sets per catalog, schema and table column from <paramref name="query"/>.
    /// <c>LIKE</c> and <c>IS NULL</c> predicates are never pushed down.
    /// </summary>
    internal static TrinoJdbcPushdownKeys PushdownKeys(TrinoJdbcQuery query)
    {
        var (catalogCol, schemaCol, tableCol) = query.View switch
        {
            TrinoJdbcView.Catalogs => (0, -1, -1),
            TrinoJdbcView.Schemas => (1, 0, -1),
            TrinoJdbcView.Tables or TrinoJdbcView.Columns => (0, 1, 2),
            _ => (-1, -1, -1)
        };

        HashSet<string>? catalogs = null;
        HashSet<string>? schemas = null;
        HashSet<string>? tables = null;

        foreach (var condition in query.Conditions)
        {
            if (condition.Operator is not (TrinoJdbcOperator.Eq or TrinoJdbcOperator.In))
            {
                continue;
            }

            if (condition.Column == catalogCol)
            {
                IntersectValues(ref catalogs, condition.Values);
            }
            else if (condition.Column == schemaCol)
            {
                IntersectValues(ref schemas, condition.Values);
            }
            else if (condition.Column == tableCol)
            {
                IntersectValues(ref tables, condition.Values);
            }
        }

        // system.jdbc.schemas synthesizes an information_schema row for every visible catalog,
        // so filtering discovery by schema when information_schema is requested would drop catalogs.
        if (query.View == TrinoJdbcView.Schemas && schemas is not null && schemas.Contains("information_schema"))
        {
            schemas = null;
        }

        return new TrinoJdbcPushdownKeys(catalogs, schemas, tables);
    }

    private static void IntersectValues(ref HashSet<string>? current, string[] values)
    {
        var next = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        if (current is null)
        {
            current = next;
        }
        else
        {
            current.IntersectWith(next);
        }
    }

    /// <summary>Evaluates the query over the visible entries; filtering happens before ordering and optional unordered row capping.</summary>
    internal static (string[] Columns, List<IReadOnlyDictionary<string, object?>> Rows) Execute(
        TrinoJdbcQuery query, IReadOnlyList<TrinoJdbcEntry> entries, int? rowLimit = null)
    {
        var names = ColumnNames(query.View);
        var source = BuildRows(query.View, entries);

        IEnumerable<object?[]> rows = source.Where(row => query.Conditions.All(c => Evaluate(c, row)));

        // Canonical order first (the stable tiebreak), then the requested keys.
        IOrderedEnumerable<object?[]>? ordered = null;
        foreach (var key in query.OrderBy)
        {
            ordered = ordered is null
                ? (key.Descending ? rows.OrderByDescending(r => r[key.Column], ValueComparer.Instance) : rows.OrderBy(r => r[key.Column], ValueComparer.Instance))
                : (key.Descending ? ordered.ThenByDescending(r => r[key.Column], ValueComparer.Instance) : ordered.ThenBy(r => r[key.Column], ValueComparer.Instance));
        }

        IEnumerable<object?[]> selected = ordered ?? rows;
        if (query.OrderBy.Count == 0 && rowLimit is >= 0 and var limit)
        {
            selected = selected.Take((int)Math.Min(int.MaxValue, (long)limit + 1));
        }

        var result = selected.ToList();

        var outputNames = query.Projection.Select(i => names[i]).ToArray();
        var outputRows = new List<IReadOnlyDictionary<string, object?>>(result.Count);
        foreach (var row in result)
        {
            var dict = new Dictionary<string, object?>(outputNames.Length, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < query.Projection.Length; i++)
            {
                dict[outputNames[i]] = row[query.Projection[i]];
            }

            outputRows.Add(dict);
        }

        return (outputNames, outputRows);
    }

    internal static List<object?[]> BuildForTest(TrinoJdbcView view, IReadOnlyList<TrinoJdbcEntry> entries) =>
        BuildRows(view, entries).ToList();

    private static IEnumerable<object?[]> BuildRows(TrinoJdbcView view, IReadOnlyList<TrinoJdbcEntry> entries)
    {
        var tables = entries.Where(static e => e.Object.Kind != DiscoveryObjectKind.Procedure);
        return view switch
        {
            TrinoJdbcView.Catalogs => BuildCatalogRows(tables),
            TrinoJdbcView.Schemas => BuildSchemaRows(tables),
            TrinoJdbcView.TableTypes => [["TABLE"], ["VIEW"]],
            TrinoJdbcView.Tables => BuildTableRows(tables),
            _ => BuildColumnRows(tables)
        };
    }

    private static IEnumerable<object?[]> BuildCatalogRows(IEnumerable<TrinoJdbcEntry> tables) =>
        tables.Select(static t => t.Object.Domain)
            .Append("system")
            .Where(static d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static d => d, StringComparer.OrdinalIgnoreCase)
            .Select(static catalog => new object?[] { catalog });

    private static IEnumerable<object?[]> BuildSchemaRows(IEnumerable<TrinoJdbcEntry> tables) =>
        tables.SelectMany(static t => new[] { (t.Object.Domain, t.Object.Schema), (t.Object.Domain, "information_schema") })
            .Where(static p => !string.IsNullOrWhiteSpace(p.Domain) && !string.IsNullOrWhiteSpace(p.Item2))
            .Distinct(PairComparer.Instance)
            .OrderBy(static p => p.Domain, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static p => p.Item2, StringComparer.OrdinalIgnoreCase)
            .Select(static p => new object?[] { p.Item2, p.Domain });

    private static IEnumerable<TrinoJdbcEntry> OrderEntries(IEnumerable<TrinoJdbcEntry> tables) =>
        tables.OrderBy(static e => e.Object.Domain, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static e => e.Object.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static e => e.Object.Name, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<object?[]> BuildTableRows(IEnumerable<TrinoJdbcEntry> tables)
    {
        foreach (var e in OrderEntries(tables))
        {
            var o = e.Object;
            var tableType = o.Kind switch
            {
                DiscoveryObjectKind.Table => "TABLE",
                DiscoveryObjectKind.View or DiscoveryObjectKind.MetricView => "VIEW",
                _ => throw new ArgumentOutOfRangeException(nameof(tables), o.Kind, "Unsupported DiscoveryObjectKind for JDBC TABLES.")
            };
            yield return
            [
                o.Domain, o.Schema, o.Name, tableType,
                e.Accessible ? AccessGranted : AccessNotAccessible, null, null, null, null, null
            ];
        }
    }

    private static IEnumerable<object?[]> BuildColumnRows(IEnumerable<TrinoJdbcEntry> tables)
    {
        foreach (var e in OrderEntries(tables.Where(static x => x.Accessible)))
        {
            var o = e.Object;
            var ordinal = 1;
            foreach (var column in o.Columns)
            {
                if (!Autheris.Application.Catalog.Discovery.DiscoveryColumnVisibility.IsVisible(column.State) || !e.AllowedColumns.Contains(column.Name))
                {
                    continue;
                }

                yield return ColumnRow(o, column, ordinal++);
            }
        }
    }

    private static object?[] ColumnRow(DiscoveryObject o, DiscoveryColumn column, int ordinal)
    {
        var typeName = string.IsNullOrWhiteSpace(column.DataType) ? "varchar" : column.DataType;
        return
        [
            o.Domain, o.Schema, o.Name, column.Name, JdbcSqlType(typeName), typeName, null, 0, null, 10, 1, null, null, null, null,
            null, ordinal, "YES", null, null, null, null, "NO", "NO"
        ];
    }

    private static int JdbcSqlType(string dataType)
    {
        var name = dataType.AsSpan();
        var paren = name.IndexOf('(');
        if (paren >= 0)
        {
            name = name[..paren];
        }

        return name.Trim().ToString().ToLowerInvariant() switch
        {
            "bigint" => -5,
            "integer" or "int" => 4,
            "smallint" => 5,
            "tinyint" => -6,
            "decimal" or "numeric" => 3,
            "double" => 8,
            "real" => 7,
            "boolean" => 16,
            "date" => 91,
            "time" => 92,
            "timestamp" => 93,
            "varbinary" => -3,
            _ => 12
        };
    }

    private static bool Evaluate(TrinoJdbcCondition condition, object?[] row)
    {
        var value = row[condition.Column];
        switch (condition.Operator)
        {
            case TrinoJdbcOperator.IsNull:
                return value is null;
            case TrinoJdbcOperator.Eq:
                return value is not null && string.Equals(value.ToString(), condition.Values[0], StringComparison.OrdinalIgnoreCase);
            case TrinoJdbcOperator.In:
                return value is not null && condition.Values.Any(v => string.Equals(value.ToString(), v, StringComparison.OrdinalIgnoreCase));
            default:
                return value is not null && condition.Pattern!.IsMatch(value.ToString() ?? string.Empty);
        }
    }

    private static int[] ParseProjection(string cols, string[] names)
    {
        var trimmed = cols.Trim();
        if (trimmed == "*")
        {
            return Enumerable.Range(0, names.Length).ToArray();
        }

        var parts = trimmed.Split(',');
        if (parts.Length > names.Length)
        {
            throw NotSupported();
        }

        var projection = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            projection[i] = ColumnIndex(parts[i].Trim(), names);
        }

        return projection;
    }

    private static int ColumnIndex(string identifier, string[] names)
    {
        for (var i = 0; i < names.Length; i++)
        {
            if (string.Equals(names[i], identifier, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw NotSupported();
    }

    private static TrinoJdbcCondition ParseCondition(List<Token> tokens, ref int pos, string[] names)
    {
        var column = ColumnIndex(ExpectIdentifier(tokens, ref pos), names);
        if (pos < tokens.Count && tokens[pos].Kind == TokenKind.EqualsSign)
        {
            pos++;
            return new TrinoJdbcCondition(column, TrinoJdbcOperator.Eq, [ExpectString(tokens, ref pos)], null);
        }

        if (IsKeyword(tokens, pos, "IS"))
        {
            pos++;
            if (!IsKeyword(tokens, pos, "NULL"))
            {
                throw NotSupported();
            }

            pos++;
            return new TrinoJdbcCondition(column, TrinoJdbcOperator.IsNull, [], null);
        }

        if (IsKeyword(tokens, pos, "LIKE"))
        {
            pos++;
            var pattern = ExpectString(tokens, ref pos);
            char? escape = null;
            if (IsKeyword(tokens, pos, "ESCAPE"))
            {
                pos++;
                var esc = ExpectString(tokens, ref pos);
                if (esc.Length != 1)
                {
                    throw NotSupported();
                }

                escape = esc[0];
            }

            return new TrinoJdbcCondition(column, TrinoJdbcOperator.Like, [], TrinoLikePattern.Compile(pattern, escape));
        }

        if (IsKeyword(tokens, pos, "IN"))
        {
            pos++;
            if (pos >= tokens.Count || tokens[pos].Kind != TokenKind.LeftParen)
            {
                throw NotSupported();
            }

            pos++;
            var values = new List<string>();
            while (true)
            {
                values.Add(ExpectString(tokens, ref pos));
                if (values.Count > MaxInValues)
                {
                    throw NotSupported();
                }

                if (pos < tokens.Count && tokens[pos].Kind == TokenKind.Comma)
                {
                    pos++;
                    continue;
                }

                break;
            }

            if (pos >= tokens.Count || tokens[pos].Kind != TokenKind.RightParen)
            {
                throw NotSupported();
            }

            pos++;
            return new TrinoJdbcCondition(column, TrinoJdbcOperator.In, [.. values], null);
        }

        throw NotSupported();
    }

    private enum TokenKind
    {
        Identifier,
        String,
        Comma,
        EqualsSign,
        LeftParen,
        RightParen
    }

    private readonly record struct Token(TokenKind Kind, string Text);

    private static bool IsKeyword(List<Token> tokens, int pos, string keyword) =>
        pos < tokens.Count && tokens[pos].Kind == TokenKind.Identifier &&
        string.Equals(tokens[pos].Text, keyword, StringComparison.OrdinalIgnoreCase);

    private static string ExpectIdentifier(List<Token> tokens, ref int pos)
    {
        if (pos >= tokens.Count || tokens[pos].Kind != TokenKind.Identifier)
        {
            throw NotSupported();
        }

        return tokens[pos++].Text;
    }

    private static string ExpectString(List<Token> tokens, ref int pos)
    {
        if (pos >= tokens.Count || tokens[pos].Kind != TokenKind.String)
        {
            throw NotSupported();
        }

        return tokens[pos++].Text;
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == ',')
            {
                tokens.Add(new Token(TokenKind.Comma, ","));
                i++;
            }
            else if (c == '=')
            {
                tokens.Add(new Token(TokenKind.EqualsSign, "="));
                i++;
            }
            else if (c == '(')
            {
                tokens.Add(new Token(TokenKind.LeftParen, "("));
                i++;
            }
            else if (c == ')')
            {
                tokens.Add(new Token(TokenKind.RightParen, ")"));
                i++;
            }
            else if (c == '\'')
            {
                var sb = new StringBuilder();
                i++;
                var closed = false;
                while (i < text.Length)
                {
                    if (text[i] == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'')
                        {
                            sb.Append('\'');
                            i += 2;
                            continue;
                        }

                        closed = true;
                        i++;
                        break;
                    }

                    sb.Append(text[i++]);
                }

                if (!closed)
                {
                    throw NotSupported();
                }

                tokens.Add(new Token(TokenKind.String, sb.ToString()));
            }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Identifier, text[start..i]));
            }
            else
            {
                // Anything else (operators, quotes, comments, numbers, semicolons) is outside the grammar.
                throw NotSupported();
            }
        }

        return tokens;
    }

    private sealed class PairComparer : IEqualityComparer<(string Domain, string Schema)>
    {
        public static readonly PairComparer Instance = new();

        public bool Equals((string Domain, string Schema) x, (string Domain, string Schema) y) =>
            string.Equals(x.Domain, y.Domain, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Schema, y.Schema, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Domain, string Schema) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Domain), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Schema));
    }

    private sealed class ValueComparer : IComparer<object?>
    {
        public static readonly ValueComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is null || y is null)
            {
                return (x is null ? 0 : 1) - (y is null ? 0 : 1);
            }

            if (x is int a && y is int b)
            {
                return a.CompareTo(b);
            }

            return StringComparer.OrdinalIgnoreCase.Compare(x.ToString(), y.ToString());
        }
    }
}
