namespace Autheris.Application.Olap;

using System;
using System.Collections.Generic;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using DuckDB.NET.Data;

/// <summary>Closed set of reasons the OLAP endpoint rejects a client statement. Each maps to a fixed code, status and message.</summary>
public enum OlapSqlRejectionReason
{
    InvalidStatement,
    SingleSelectOnly,
    ProhibitedFunction,
    EscapeStringsProhibited,
    GatewayDialectRequired,
    ConstructNotAllowed,
    ColumnPolicy
}

/// <summary>
/// A client SQL text that the OLAP endpoint rejects. The message and <see cref="Code"/> come only from a fixed table keyed by
/// <see cref="Reason"/>; no caller text, parser text or inner exception text can enter them. An inner exception may be attached for
/// diagnostics, but callers must log its type only.
/// </summary>
public sealed class OlapSqlRejectedException : SecurityException
{
    public const string Invalid = "OLAP_SQL_INVALID";
    public const string NotAnalyzable = "OLAP_SQL_NOT_ANALYZABLE";
    public const string ConstructNotAllowed = "OLAP_SQL_CONSTRUCT_NOT_ALLOWED";
    public const string ColumnPolicy = "OLAP_COLUMN_POLICY";

    public OlapSqlRejectedException(OlapSqlRejectionReason reason)
        : base(Describe(reason).Message)
    {
        Reason = Describe(reason).Reason;
    }

    public OlapSqlRejectedException(OlapSqlRejectionReason reason, Exception innerException)
        : base(Describe(reason).Message, innerException)
    {
        Reason = Describe(reason).Reason;
    }

    public OlapSqlRejectionReason Reason { get; }

    public string Code => Describe(Reason).Code;

    /// <summary>403 for the column policy, 400 for every other reason.</summary>
    public int HttpStatus => Reason == OlapSqlRejectionReason.ColumnPolicy ? 403 : 400;

    // Unknown values fail closed to InvalidStatement.
    private static (OlapSqlRejectionReason Reason, string Code, string Message) Describe(OlapSqlRejectionReason reason) => reason switch
    {
        OlapSqlRejectionReason.SingleSelectOnly => (reason, Invalid, "Only a single SELECT statement is permitted in OLAP queries."),
        OlapSqlRejectionReason.ProhibitedFunction => (reason, Invalid, "The SQL statement uses a function that is prohibited in OLAP queries."),
        OlapSqlRejectionReason.EscapeStringsProhibited => (reason, Invalid, "Escape strings and backslashes are prohibited in OLAP queries."),
        OlapSqlRejectionReason.GatewayDialectRequired => (reason, NotAnalyzable, "Cross-source OLAP queries must use the gateway SQL dialect."),
        OlapSqlRejectionReason.ConstructNotAllowed => (reason, ConstructNotAllowed, "The SQL statement uses a construct that is not permitted in cross-source OLAP queries."),
        OlapSqlRejectionReason.ColumnPolicy => (reason, ColumnPolicy, "The SQL statement was rejected by the column security policy."),
        _ => (OlapSqlRejectionReason.InvalidStatement, Invalid, "The SQL statement is not valid for the OLAP endpoint.")
    };
}

/// <summary>
/// What DuckDB's own parser found in a client statement (taken from <c>json_serialize_sql</c>, so it is the grammar that later
/// executes the text): number of statements, called functions, table functions, referenced base tables and risky constructs.
/// </summary>
public sealed record OlapSqlInspection(
    int StatementCount,
    IReadOnlyList<string> Functions,
    IReadOnlyList<string> TableFunctions,
    IReadOnlyList<string> BaseTables,
    IReadOnlySet<string> Constructs,
    IReadOnlySet<string> CteNames);

/// <summary>
/// Validates client SQL with the DuckDB parser (<c>json_serialize_sql</c>: only a single <c>SELECT</c> can be serialized, every parse
/// failure is an error) instead of a text scanner, and extracts the AST facts that the cross-source policy needs.
/// </summary>
public static class OlapSqlInspector
{
    public const string ConstructNaturalJoin = "NATURAL_JOIN";
    public const string ConstructPositionalJoin = "POSITIONAL_JOIN";
    public const string ConstructLateral = "LATERAL";
    public const string ConstructTableSample = "TABLESAMPLE";
    public const string ConstructColumns = "COLUMNS";
    public const string ConstructStarExclude = "STAR_EXCLUDE";
    public const string ConstructStarReplace = "STAR_REPLACE";
    public const string ConstructStarOutsideSelectList = "STAR_OUTSIDE_SELECT_LIST";
    public const string ConstructPositionalReference = "POSITIONAL_REFERENCE";
    public const string ConstructGroupByAll = "GROUP_BY_ALL";
    public const string ConstructQualify = "QUALIFY";
    public const string ConstructCrossJoin = "CROSS_JOIN";
    public const string ConstructNonEquiJoin = "NON_EQUI_JOIN";

    private static readonly Regex LateralKeyword = new(
        @"\blateral\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// Text-level floor that runs before any parser: length, NUL, backslash, <c>E'..'</c> and <c>U&amp;'..'</c> strings are rejected, so
    /// no later text scan can be desynchronised from DuckDB's lexing by an escape sequence.
    /// </summary>
    public static void EnforceTextFloor(string sql, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (string.IsNullOrWhiteSpace(sql) || (maxLength > 0 && sql.Length > maxLength))
        {
            throw new OlapSqlRejectedException(OlapSqlRejectionReason.InvalidStatement);
        }

        if (sql.Contains('\0') || sql.Contains('\\') || HasEscapeStringPrefix(sql))
        {
            throw new OlapSqlRejectedException(OlapSqlRejectionReason.EscapeStringsProhibited);
        }
    }

    /// <summary>
    /// True when an <c>E'..'</c> or <c>U&amp;'..'</c> / <c>U&amp;".."</c> prefix appears OUTSIDE a quoted region. Quoted regions are
    /// skipped (a doubled quote stays inside), so literals such as <c>'e'</c> or <c>'Dr. E'</c> are not mistaken for prefixes.
    /// </summary>
    internal static bool HasEscapeStringPrefix(string sql)
    {
        char quote = '\0';
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    if (i + 1 < sql.Length && sql[i + 1] == quote)
                    {
                        i++; // doubled quote: still inside the literal
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (c is not ('\'' or '"'))
            {
                continue;
            }

            if (c == '\'' && i >= 1 && (sql[i - 1] is 'e' or 'E') && (i < 2 || !IsIdentifierChar(sql[i - 2])))
            {
                return true;
            }

            if (i >= 2 && sql[i - 1] == '&' && (sql[i - 2] is 'u' or 'U') && (i < 3 || !IsIdentifierChar(sql[i - 3])))
            {
                return true;
            }

            quote = c;
        }

        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    // One long-lived, sandboxed database used only to parse client SQL before admission. No extension install or autoload, no
    // external access, one thread and a small memory limit; every request parses on its own connection to this database.
    private static readonly Lazy<DuckDBConnection> ParseDatabase = new(CreateParseDatabase, LazyThreadSafetyMode.ExecutionAndPublication);

    private static DuckDBConnection CreateParseDatabase()
    {
        var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var setup = connection.CreateCommand();
        setup.CommandText =
            "SET autoinstall_known_extensions = false; SET autoload_known_extensions = false; SET enable_external_access = false; " +
            "PRAGMA max_memory = '64MB'; PRAGMA threads = 1; SET lock_configuration = true;";
        setup.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Parses <paramref name="sql"/> on a connection to the shared sandboxed parse database (nothing is executed or staged).</summary>
    public static OlapSqlInspection Inspect(string sql)
    {
        using var connection = ParseDatabase.Value.Duplicate();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        return Inspect(connection, sql);
    }

    /// <summary>Reads a setting of the parse database (used to prove the sandbox in tests).</summary>
    internal static string? ReadParseDatabaseSetting(string name)
    {
        using var connection = ParseDatabase.Value.Duplicate();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM duckdb_settings() WHERE name = '" + name.Replace("'", "''", StringComparison.Ordinal) + "'";
        return cmd.ExecuteScalar()?.ToString();
    }

    /// <summary>Parses <paramref name="sql"/> with the parser of the given connection; nothing is executed or staged.</summary>
    public static OlapSqlInspection Inspect(DuckDBConnection connection, string sql)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sql);

        string? json;
        try
        {
            using var cmd = connection.CreateCommand();

            // A plain literal: backslash is no escape in DuckDB (and the text floor already rejected it), only the quote is doubled.
            cmd.CommandText = "SELECT json_serialize_sql('" + sql.Replace("'", "''", StringComparison.Ordinal) + "')";
            json = cmd.ExecuteScalar() as string;
        }
        catch (DuckDBException)
        {
            throw new OlapSqlRejectedException(OlapSqlRejectionReason.InvalidStatement);
        }

        if (string.IsNullOrEmpty(json))
        {
            throw new OlapSqlRejectedException(OlapSqlRejectionReason.InvalidStatement);
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 512 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True) ||
                !root.TryGetProperty("statements", out var statements) ||
                statements.ValueKind != JsonValueKind.Array)
            {
                throw new OlapSqlRejectedException(OlapSqlRejectionReason.InvalidStatement);
            }

            int count = statements.GetArrayLength();
            var collector = new Collector();
            Walk(statements, null, collector);
            collector.Finish();

            // The serialized AST does not mark LATERAL (correlation is resolved at bind time), so the keyword is detected in the text.
            if (LateralKeyword.IsMatch(sql))
            {
                collector.Constructs.Add(ConstructLateral);
            }

            return new OlapSqlInspection(count, collector.Functions, collector.TableFunctions, collector.BaseTables, collector.Constructs, collector.CteNameSet);
        }
        catch (JsonException)
        {
            throw new OlapSqlRejectedException(OlapSqlRejectionReason.InvalidStatement);
        }
    }

    private sealed class Collector
    {
        private readonly HashSet<string> _cteNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _rawBaseTables = [];

        public List<string> Functions { get; } = [];

        public List<string> TableFunctions { get; } = [];

        public List<string> BaseTables { get; } = [];

        public HashSet<string> Constructs { get; } = new(StringComparer.Ordinal);

        public HashSet<string> CteNameSet => _cteNames;

        public void AddCte(string name) => _cteNames.Add(name);

        public void AddBaseTable(string qualifiedName) => _rawBaseTables.Add(qualifiedName);

        public void Finish()
        {
            foreach (var table in _rawBaseTables)
            {
                // A reference to a CTE of the statement is not a reference to a staged table.
                if (!_cteNames.Contains(table))
                {
                    BaseTables.Add(table.ToLowerInvariant());
                }
            }
        }
    }

    private static void Walk(JsonElement element, string? parentKey, Collector collector)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                InspectObject(element, parentKey, collector);
                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, property.Name, collector);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, parentKey, collector);
                }

                break;
        }
    }

    private static void InspectObject(JsonElement obj, string? parentKey, Collector c)
    {
        var cls = GetString(obj, "class");
        var type = GetString(obj, "type");

        // CTE map entries: {"key": "name", "value": {...}}
        if (parentKey == "map" && GetString(obj, "key") is { Length: > 0 } cteName && obj.TryGetProperty("value", out _))
        {
            c.AddCte(cteName);
        }

        if (cls is "FUNCTION" or "WINDOW")
        {
            var name = GetString(obj, "function_name");
            bool isOperator = cls == "FUNCTION" && GetBool(obj, "is_operator");
            if (!isOperator && name is { Length: > 0 })
            {
                var schema = GetString(obj, "schema");
                c.Functions.Add((string.IsNullOrEmpty(schema) ? name : schema + "." + name).ToLowerInvariant());
            }
        }
        else if (cls == "STAR")
        {
            if (GetBool(obj, "columns"))
            {
                c.Constructs.Add(ConstructColumns);
            }

            if (HasItems(obj, "exclude_list"))
            {
                c.Constructs.Add(ConstructStarExclude);
            }

            if (HasItems(obj, "replace_list"))
            {
                c.Constructs.Add(ConstructStarReplace);
            }

            if (parentKey is not ("select_list" or "children"))
            {
                c.Constructs.Add(ConstructStarOutsideSelectList);
            }
        }
        else if (cls == "POSITIONAL_REFERENCE")
        {
            c.Constructs.Add(ConstructPositionalReference);
        }

        switch (type)
        {
            case "BASE_TABLE":
                if (GetString(obj, "table_name") is { Length: > 0 } table)
                {
                    var schema = GetString(obj, "schema_name");
                    var catalog = GetString(obj, "catalog_name");
                    var qualified = string.IsNullOrEmpty(schema) ? table : schema + "." + table;
                    c.AddBaseTable(string.IsNullOrEmpty(catalog) ? qualified : catalog + "." + qualified);
                }

                break;
            case "TABLE_FUNCTION":
                if (obj.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object &&
                    GetString(fn, "function_name") is { Length: > 0 } tableFunction)
                {
                    c.TableFunctions.Add(tableFunction.ToLowerInvariant());
                }
                else
                {
                    c.TableFunctions.Add("?");
                }

                break;
            case "JOIN":
                InspectJoin(obj, c);
                break;
        }

        if (GetBool(obj, "lateral"))
        {
            c.Constructs.Add(ConstructLateral);
        }

        if (obj.TryGetProperty("sample", out var sample) && sample.ValueKind == JsonValueKind.Object)
        {
            c.Constructs.Add(ConstructTableSample);
        }

        if (obj.TryGetProperty("qualify", out var qualify) && qualify.ValueKind == JsonValueKind.Object)
        {
            c.Constructs.Add(ConstructQualify);
        }

        if (string.Equals(GetString(obj, "aggregate_handling"), "FORCE_AGGREGATES", StringComparison.Ordinal))
        {
            c.Constructs.Add(ConstructGroupByAll);
        }
    }

    private static void InspectJoin(JsonElement join, Collector c)
    {
        switch (GetString(join, "ref_type"))
        {
            case "NATURAL":
                c.Constructs.Add(ConstructNaturalJoin);
                break;
            case "POSITIONAL":
                c.Constructs.Add(ConstructPositionalJoin);
                break;
            case "DEPENDENT":
                c.Constructs.Add(ConstructLateral);
                break;
            case "CROSS":
                c.Constructs.Add(ConstructCrossJoin);
                break;
            case "REGULAR":
                if (join.TryGetProperty("condition", out var condition) && condition.ValueKind == JsonValueKind.Object &&
                    IsNonEquiCondition(condition))
                {
                    c.Constructs.Add(ConstructNonEquiJoin);
                }

                break;
        }
    }

    // An equi join condition is a conjunction (AND) of equality comparisons; anything else (range, OR, function) is non-equi.
    private static bool IsNonEquiCondition(JsonElement condition)
    {
        var cls = GetString(condition, "class");
        var type = GetString(condition, "type");
        if (cls == "CONJUNCTION")
        {
            if (type != "CONJUNCTION_AND" || !condition.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
            {
                return true;
            }

            foreach (var child in children.EnumerateArray())
            {
                if (child.ValueKind != JsonValueKind.Object || IsNonEquiCondition(child))
                {
                    return true;
                }
            }

            return false;
        }

        return !(cls == "COMPARISON" && type is "COMPARE_EQUAL" or "COMPARE_NOT_DISTINCT_FROM");
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool GetBool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool HasItems(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.Object => value.EnumerateObject().MoveNext(),
            _ => false
        };
    }
}
