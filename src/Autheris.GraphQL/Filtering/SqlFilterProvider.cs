using System.Globalization;
using System.Security;
using System.Text.RegularExpressions;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using HotChocolate.Language;

namespace Autheris.GraphQL.Filtering;

public sealed partial class SqlFilterProvider : ISqlFilterProvider
{
    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex SafeIdentifierRegex();
    public const int DefaultMaxTotalParameters = 500;
    public const int DefaultMaxFilterDepth = 15;
    private readonly int _maxInClauseSize;
    private readonly int _maxTotalParameters;
    private readonly int _maxFilterDepth;

    public SqlFilterProvider(int maxInClauseSize = 1000, int maxTotalParameters = DefaultMaxTotalParameters, int maxFilterDepth = DefaultMaxFilterDepth)
    {
        _maxInClauseSize = maxInClauseSize > 0 ? maxInClauseSize : 1000;
        _maxTotalParameters = maxTotalParameters > 0 ? maxTotalParameters : DefaultMaxTotalParameters;
        _maxFilterDepth = maxFilterDepth > 0 ? maxFilterDepth : DefaultMaxFilterDepth;
    }

    private void AddParameter(Dictionary<string, object?> parameters, string rawParamName, object? value)
    {
        if (parameters.Count >= _maxTotalParameters)
        {
            throw new InvalidOperationException($"QUERY_TOO_COMPLEX: Die maximale Anzahl an Filterparametern ({_maxTotalParameters}) wurde überschritten.");
        }
        parameters[rawParamName] = value;
    }

    private sealed class Counter
    {
        public int Value = 1;
    }

    public static IReadOnlyDictionary<string, ColumnAccessLevel> UnrestrictedAccess(TableMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return metadata.Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase);
    }

    public (string SqlWhereClause, IReadOnlyDictionary<string, object?> Parameters) TranslateFilterAst(
        FieldNode filterAst,
        TableMetadata metadata,
        DatabaseDialect dialect,
        IReadOnlyDictionary<string, ColumnAccessLevel> columnAccess)
    {
        ArgumentNullException.ThrowIfNull(columnAccess);
        var parameters = new Dictionary<string, object?>();
        var counter = new Counter();

        var whereClause = ProcessArgumentOrDirective(filterAst, metadata, dialect, parameters, counter, columnAccess);
        return (whereClause, parameters);
    }

    public (string SqlWhereClause, IReadOnlyDictionary<string, object?> Parameters) TranslateObjectValue(
        IValueNode filterValueNode,
        TableMetadata metadata,
        DatabaseDialect dialect,
        IReadOnlyDictionary<string, ColumnAccessLevel> columnAccess)
    {
        ArgumentNullException.ThrowIfNull(columnAccess);
        var parameters = new Dictionary<string, object?>();
        var counter = new Counter();

        var whereClause = ProcessValueNode(filterValueNode, metadata, dialect, parameters, counter, columnAccess);
        return (whereClause, parameters);
    }

    private string ProcessArgumentOrDirective(
        FieldNode fieldNode,
        TableMetadata metadata,
        DatabaseDialect dialect,
        Dictionary<string, object?> parameters,
        Counter counter,
        IReadOnlyDictionary<string, ColumnAccessLevel> columnAccess)
    {
        var whereArg = fieldNode.Arguments.FirstOrDefault(a => string.Equals(a.Name.Value, "where", StringComparison.OrdinalIgnoreCase));
        if (whereArg == null || whereArg.Value is NullValueNode)
        {
            return string.Empty;
        }

        return ProcessValueNode(whereArg.Value, metadata, dialect, parameters, counter, columnAccess);
    }

    private string ProcessValueNode(
        IValueNode valueNode,
        TableMetadata metadata,
        DatabaseDialect dialect,
        Dictionary<string, object?> parameters,
        Counter counter,
        IReadOnlyDictionary<string, ColumnAccessLevel> columnAccess,
        int depth = 0)
    {
        if (depth > _maxFilterDepth)
        {
            throw new InvalidOperationException($"QUERY_TOO_COMPLEX: Die maximale Filter-Schachtelungstiefe ({_maxFilterDepth}) wurde überschritten.");
        }

        if (valueNode is ObjectValueNode objNode)
        {
            var clauses = new List<string>();

            foreach (var field in objNode.Fields)
            {
                var fieldName = field.Name.Value;

                if (string.Equals(fieldName, "and", StringComparison.OrdinalIgnoreCase))
                {
                    if (field.Value is ListValueNode listNode)
                    {
                        var andClauses = new List<string>();
                        foreach (var item in listNode.Items)
                        {
                            var s = ProcessValueNode(item, metadata, dialect, parameters, counter, columnAccess, depth + 1);
                            if (!string.IsNullOrEmpty(s))
                            {
                                andClauses.Add(s);
                            }
                        }

                        if (andClauses.Count > 0)
                        {
                            clauses.Add($"({string.Join(" AND ", andClauses)})");
                        }
                    }
                    continue;
                }

                if (string.Equals(fieldName, "or", StringComparison.OrdinalIgnoreCase))
                {
                    if (field.Value is ListValueNode listNode)
                    {
                        var orClauses = new List<string>();
                        foreach (var item in listNode.Items)
                        {
                            var s = ProcessValueNode(item, metadata, dialect, parameters, counter, columnAccess, depth + 1);
                            if (!string.IsNullOrEmpty(s))
                            {
                                orClauses.Add(s);
                            }
                        }

                        if (orClauses.Count > 0)
                        {
                            clauses.Add($"({string.Join(" OR ", orClauses)})");
                        }
                    }
                    continue;
                }

                // Strict catalog whitelist validation
                if (!SafeIdentifierRegex().IsMatch(fieldName) || !metadata.HasColumn(fieldName))
                {
                    throw new InvalidOperationException($"Ungültiger Spaltenname im Filter: '{fieldName}' existiert nicht in Tabelle '{metadata.Identifier}'. Potenzieller Injection-Angriff.");
                }

                // SEC-01: Zero-Trust rule: Filtering on columns without explicit Clear access (or with Mask/Deny) is strictly forbidden to prevent side-channel inference
                if (columnAccess == null)
                {
                    throw new SecurityException($"Zero-Trust-Verletzung: Spaltenberechtigungen (columnAccess) müssen für die Filterung auf Spalte '{fieldName}' zwingend übergeben werden.");
                }

                var access = columnAccess.TryGetValue(fieldName, out var explicitAccess)
                    ? explicitAccess
                    : ColumnAccessLevel.Deny;

                if (access != ColumnAccessLevel.Clear)
                {
                    throw new SecurityException($"Zero-Trust-Verletzung: Filtern auf Spalte '{fieldName}' ist nicht gestattet (Zugriffsebene: {access}).");
                }

                var quotedColumn = QuoteIdentifier(fieldName, dialect);

                if (field.Value is ObjectValueNode opObj)
                {
                    foreach (var opField in opObj.Fields)
                    {
                        var op = opField.Name.Value.ToLowerInvariant();
                        if (op == "in" && opField.Value is ListValueNode inListNode)
                        {
                            if (inListNode.Items.Count == 0)
                            {
                                clauses.Add("1 = 0");
                                continue;
                            }

                            if (inListNode.Items.Count <= _maxInClauseSize)
                            {
                                var paramNames = new List<string>(inListNode.Items.Count);
                                foreach (var itemNode in inListNode.Items)
                                {
                                    var itemVal = ExtractLiteralValue(itemNode);
                                    var pName = GetParamName(dialect, counter);
                                    AddParameter(parameters, pName.TrimStart('@', '$', ':'), itemVal);
                                    paramNames.Add(pName);
                                }
                                clauses.Add($"{quotedColumn} IN ({string.Join(", ", paramNames)})");
                            }
                            else
                            {
                                // Sonderfall: Wenn IN-Liste zu lang wird, teilen wir in OR-verknüpfte Chunks auf
                                var chunks = inListNode.Items.Chunk(_maxInClauseSize).ToList();
                                var orClauses = new List<string>(chunks.Count);
                                foreach (var chunk in chunks)
                                {
                                    var paramNames = new List<string>(chunk.Length);
                                    foreach (var itemNode in chunk)
                                    {
                                        var itemVal = ExtractLiteralValue(itemNode);
                                        var pName = GetParamName(dialect, counter);
                                        AddParameter(parameters, pName.TrimStart('@', '$', ':'), itemVal);
                                        paramNames.Add(pName);
                                    }
                                    orClauses.Add($"({quotedColumn} IN ({string.Join(", ", paramNames)}))");
                                }
                                clauses.Add($"({string.Join(" OR ", orClauses)})");
                            }
                            continue;
                        }

                        var opVal = ExtractLiteralValue(opField.Value);

                        if (opVal == null)
                        {
                            var nullCondition = op switch
                            {
                                "eq" => $"{quotedColumn} IS NULL",
                                "neq" => $"{quotedColumn} IS NOT NULL",
                                _ => throw new InvalidOperationException($"Operator '{op}' kann nicht mit NULL verglichen werden.")
                            };
                            clauses.Add(nullCondition);
                            continue;
                        }

                        var paramName = GetParamName(dialect, counter);
                        var rawParam = paramName.TrimStart('@', '$', ':');
                        AddParameter(parameters, rawParam, opVal);

                        var condition = op switch
                        {
                            "eq" => $"{quotedColumn} = {paramName}",
                            "neq" => $"{quotedColumn} <> {paramName}",
                            "gt" => $"{quotedColumn} > {paramName}",
                            "gte" => $"{quotedColumn} >= {paramName}",
                            "lt" => $"{quotedColumn} < {paramName}",
                            "lte" => $"{quotedColumn} <= {paramName}",
                            "contains" => $"{quotedColumn} LIKE {paramName} ESCAPE '\\'",
                            "startswith" => $"{quotedColumn} LIKE {paramName} ESCAPE '\\'",
                            "endswith" => $"{quotedColumn} LIKE {paramName} ESCAPE '\\'",
                            _ => $"{quotedColumn} = {paramName}"
                        };

                        if (op == "contains")
                        {
                            parameters[rawParam] = $"%{EscapeLikePattern(opVal)}%";
                        }
                        else if (op == "startswith")
                        {
                            parameters[rawParam] = $"{EscapeLikePattern(opVal)}%";
                        }
                        else if (op == "endswith")
                        {
                            parameters[rawParam] = $"%{EscapeLikePattern(opVal)}";
                        }

                        clauses.Add(condition);
                    }
                }
            }

            return clauses.Count > 0 ? string.Join(" AND ", clauses) : string.Empty;
        }

        return string.Empty;
    }

    private static string EscapeLikePattern(object? val)
    {
        var str = val?.ToString() ?? string.Empty;
        return str
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_")
            .Replace("[", "\\[");
    }

    private static string QuoteIdentifier(string identifier, DatabaseDialect dialect) =>
        dialect.QuoteIdentifier(identifier);

    private static string GetParamName(DatabaseDialect dialect, Counter counter)
    {
        var num = counter.Value++;
        return dialect switch
        {
            DatabaseDialect.PostgreSql => $"${num}",
            DatabaseDialect.SqlServer => $"@p{num}",
            DatabaseDialect.Sqlite => $"@p{num}",
            DatabaseDialect.Databricks => $"@p{num}",
            DatabaseDialect.Oracle => $":p{num}",
            _ => $"@p{num}"
        };
    }

    private static object? ExtractLiteralValue(IValueNode node)
    {
        return node switch
        {
            StringValueNode s => s.Value,
            IntValueNode i => long.Parse(i.Value, CultureInfo.InvariantCulture),
            FloatValueNode f => decimal.Parse(f.Value, CultureInfo.InvariantCulture),
            BooleanValueNode b => b.Value,
            NullValueNode => null,
            _ => node.ToString()
        };
    }
}
