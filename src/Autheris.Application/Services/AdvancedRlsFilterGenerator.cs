using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Services;

/// <summary>
/// Generator for advanced Row-Level Security (RLS) filters:
/// - Single-source correlated subqueries (EXISTS with temporal validity checks and multi-hop joins)
/// - Multi-source virtual set injection with chunked parameter budgeting
/// </summary>
public static partial class AdvancedRlsFilterGenerator
{
    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex SafeSimpleIdentifierRegex();

    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*(\.[a-zA-Z_][a-zA-Z0-9_]*)*$")]
    private static partial Regex SafeQualifiedIdentifierRegex();

    public static string BuildCorrelatedSubquery(ConsentRowFilter filter, DatabaseDialect dialect = DatabaseDialect.SqlServer)
    {
        if (filter.DependentTable == null)
        {
            throw new InvalidOperationException("DependentTable darf für SubqueryCorrelated nicht null sein.");
        }

        var targetAlias = string.IsNullOrWhiteSpace(filter.TargetTableAlias) ? "target" : filter.TargetTableAlias;
        var depAlias = string.IsNullOrWhiteSpace(filter.DependentTableAlias) ? "dep" : filter.DependentTableAlias;
        var targetFk = string.IsNullOrWhiteSpace(filter.ForeignKeyColumn) ? filter.ColumnName : filter.ForeignKeyColumn;
        var depPk = string.IsNullOrWhiteSpace(filter.PrimaryKeyColumn) ? "id" : filter.PrimaryKeyColumn;

        ValidateIdentifier(targetAlias, "TargetTableAlias");
        ValidateIdentifier(depAlias, "DependentTableAlias");
        ValidateIdentifier(targetFk, "ForeignKeyColumn");
        ValidateIdentifier(depPk, "PrimaryKeyColumn");

        var quotedDepTable = FormatTableIdentifier(filter.DependentTable.Value, dialect);
        var quotedDepAlias = QuoteSingleIdentifier(depAlias, dialect);
        var quotedTargetAlias = QuoteSingleIdentifier(targetAlias, dialect);
        var quotedDepPk = QuoteSingleIdentifier(depPk, dialect);
        var quotedTargetFk = QuoteSingleIdentifier(targetFk, dialect);

        var joinClauses = new List<string>();
        if (filter.AdditionalHops != null && filter.AdditionalHops.Count > 0)
        {
            foreach (var hop in filter.AdditionalHops)
            {
                ValidateIdentifier(hop.TableAlias, "SubqueryJoinHop.TableAlias");
                ValidateQualifiedIdentifier(hop.LeftJoinColumn, "SubqueryJoinHop.LeftJoinColumn");
                ValidateQualifiedIdentifier(hop.RightJoinColumn, "SubqueryJoinHop.RightJoinColumn");

                var quotedHopTable = FormatTableIdentifier(hop.Table, dialect);
                var quotedHopAlias = QuoteSingleIdentifier(hop.TableAlias, dialect);
                var quotedLeft = QuoteQualifiedColumn(hop.LeftJoinColumn, dialect);
                var quotedRight = QuoteQualifiedColumn(hop.RightJoinColumn, dialect);
                var asKeyword = dialect == DatabaseDialect.Oracle ? " " : " AS ";

                joinClauses.Add($"INNER JOIN {quotedHopTable}{asKeyword}{quotedHopAlias} ON {quotedLeft} = {quotedRight}");
            }
        }

        var whereConditions = new List<string>
        {
            $"{quotedDepAlias}.{quotedDepPk} = {quotedTargetAlias}.{quotedTargetFk}"
        };

        // Parse and append subquery predicates
        if (!string.IsNullOrWhiteSpace(filter.SubqueryFilterPredicateJson))
        {
            var parsedConditions = ParseSubqueryPredicates(filter.SubqueryFilterPredicateJson, dialect);
            whereConditions.AddRange(parsedConditions);
        }

        // Temporal interval checks
        if (!string.IsNullOrWhiteSpace(filter.TargetTemporalColumn) && !string.IsNullOrWhiteSpace(filter.DependentValidFromColumn))
        {
            ValidateQualifiedIdentifier(filter.TargetTemporalColumn, "TargetTemporalColumn");
            ValidateQualifiedIdentifier(filter.DependentValidFromColumn, "DependentValidFromColumn");

            var quotedTargetTemporal = filter.TargetTemporalColumn.Contains('.')
                ? QuoteQualifiedColumn(filter.TargetTemporalColumn, dialect)
                : $"{quotedTargetAlias}.{QuoteSingleIdentifier(filter.TargetTemporalColumn, dialect)}";

            var quotedValidFrom = filter.DependentValidFromColumn.Contains('.')
                ? QuoteQualifiedColumn(filter.DependentValidFromColumn, dialect)
                : $"{quotedDepAlias}.{QuoteSingleIdentifier(filter.DependentValidFromColumn, dialect)}";

            whereConditions.Add($"{quotedTargetTemporal} >= {quotedValidFrom}");

            if (!string.IsNullOrWhiteSpace(filter.DependentValidToColumn))
            {
                ValidateQualifiedIdentifier(filter.DependentValidToColumn, "DependentValidToColumn");
                var quotedValidTo = filter.DependentValidToColumn.Contains('.')
                    ? QuoteQualifiedColumn(filter.DependentValidToColumn, dialect)
                    : $"{quotedDepAlias}.{QuoteSingleIdentifier(filter.DependentValidToColumn, dialect)}";

                whereConditions.Add($"({quotedValidTo} IS NULL OR {quotedTargetTemporal} < {quotedValidTo})");
            }
        }

        var joinsStr = joinClauses.Count > 0 ? " " + string.Join(" ", joinClauses) : string.Empty;
        var whereStr = string.Join(" AND ", whereConditions);
        var fromAs = dialect == DatabaseDialect.Oracle ? " " : " AS ";

        return $"EXISTS (SELECT 1 FROM {quotedDepTable}{fromAs}{quotedDepAlias}{joinsStr} WHERE {whereStr})";
    }

    public static string BuildCrossSourceSetFilter(ConsentRowFilter filter, int maxBatchSize = 500, DatabaseDialect dialect = DatabaseDialect.SqlServer)
    {
        var col = filter.ColumnName;
        ValidateIdentifier(col, "ColumnName");
        var quotedCol = QuoteSingleIdentifier(col, dialect);

        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(filter.ValueJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(filter.ValueJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        values.Add(FormatLiteralValue(elem, dialect));
                    }
                }
                else
                {
                    values.Add(FormatLiteralValue(doc.RootElement, dialect));
                }
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("ValueJson im CrossSourceSetFilter muss valides JSON sein.");
            }
        }

        if (values.Count == 0)
        {
            return "1 = 0";
        }

        if (values.Count <= maxBatchSize)
        {
            return $"{quotedCol} IN ({string.Join(", ", values)})";
        }

        // Chunking with OR to respect parameter budgets
        var chunks = values.Chunk(maxBatchSize).ToList();
        var orClauses = new List<string>(chunks.Count);
        foreach (var chunk in chunks)
        {
            orClauses.Add($"({quotedCol} IN ({string.Join(", ", chunk)}))");
        }

        return $"({string.Join(" OR ", orClauses)})";
    }

    private static readonly HashSet<string> AllowedSubqueryOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "EQ", "NEQ", "LT", "GT", "LTE", "GTE", "LIKE"
    };

    private static string FormatLiteralValue(JsonElement elem, DatabaseDialect dialect)
    {
        return dialect.FormatSafeLiteral(elem);
    }

    private static List<string> ParseSubqueryPredicates(string json, DatabaseDialect dialect)
    {
        var conditions = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    ValidateQualifiedIdentifier(prop.Name, "SubqueryPredicate.Column");
                    var quotedCol = QuoteQualifiedColumn(prop.Name, dialect);
                    if (prop.Value.ValueKind == JsonValueKind.Null)
                    {
                        conditions.Add($"{quotedCol} IS NULL");
                    }
                    else
                    {
                        var val = FormatLiteralValue(prop.Value, dialect);
                        conditions.Add($"{quotedCol} = {val}");
                    }
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in doc.RootElement.EnumerateArray())
                {
                    if (elem.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidOperationException("Subquery-Prädikat-Array darf nur JSON-Objekte enthalten.");
                    }

                    if (!elem.TryGetProperty("column", out var colElem) || colElem.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidOperationException("Subquery-Prädikat muss ein gültiges 'column'-Property enthalten.");
                    }
                    var col = colElem.GetString()!;
                    ValidateQualifiedIdentifier(col, "SubqueryPredicate.Column");
                    var op = elem.TryGetProperty("op", out var opProp) ? opProp.GetString()?.ToUpperInvariant() ?? "EQ" : "EQ";
                    if (!AllowedSubqueryOperators.Contains(op))
                    {
                        throw new InvalidOperationException($"Nicht unterstützter Operator '{op}' im Subquery-Prädikat.");
                    }

                    if (!elem.TryGetProperty("value", out var rawVal))
                    {
                        throw new InvalidOperationException("Subquery-Prädikat muss ein 'value'-Property enthalten.");
                    }
                    var quotedCol = QuoteQualifiedColumn(col, dialect);

                    if (rawVal.ValueKind == JsonValueKind.Null)
                    {
                        conditions.Add(op == "NEQ" ? $"{quotedCol} IS NOT NULL" : $"{quotedCol} IS NULL");
                    }
                    else
                    {
                        var formattedVal = FormatLiteralValue(rawVal, dialect);
                        var cond = op switch
                        {
                            "EQ" => $"{quotedCol} = {formattedVal}",
                            "NEQ" => $"{quotedCol} <> {formattedVal}",
                            "LT" => $"{quotedCol} < {formattedVal}",
                            "GT" => $"{quotedCol} > {formattedVal}",
                            "LTE" => $"{quotedCol} <= {formattedVal}",
                            "GTE" => $"{quotedCol} >= {formattedVal}",
                            "LIKE" => $"{quotedCol} LIKE {formattedVal}",
                            _ => $"{quotedCol} = {formattedVal}"
                        };
                        conditions.Add(cond);
                    }
                }
            }
            else
            {
                throw new InvalidOperationException("SubqueryFilterPredicateJson muss ein JSON-Objekt oder JSON-Array sein.");
            }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("SubqueryFilterPredicateJson muss gültiges JSON sein.");
        }

        return conditions;
    }

    public static string QuoteSingleIdentifier(string id, DatabaseDialect dialect) =>
        dialect.QuoteIdentifier(id);

    public static string QuoteQualifiedColumn(string qualifiedColumn, DatabaseDialect dialect) =>
        dialect.QuoteQualifiedColumn(qualifiedColumn);

    public static string FormatTableIdentifier(TableIdentifier table, DatabaseDialect dialect)
    {
        ValidateIdentifier(table.Schema, "Schema");
        ValidateIdentifier(table.TableName, "TableName");

        return dialect switch
        {
            DatabaseDialect.SqlServer => $"[{table.Schema}].[{table.TableName}]",
            DatabaseDialect.PostgreSql => $"\"{table.Schema}\".\"{table.TableName}\"",
            DatabaseDialect.Sqlite => $"\"{table.TableName}\"",
            DatabaseDialect.Databricks => $"`{table.Schema}`.`{table.TableName}`",
            DatabaseDialect.Oracle => $"\"{table.Schema}\".\"{table.TableName}\"",
            _ => $"\"{table.Schema}\".\"{table.TableName}\""
        };
    }

    private static void ValidateIdentifier(string id, string context)
    {
        if (string.IsNullOrWhiteSpace(id) || !SafeSimpleIdentifierRegex().IsMatch(id))
        {
            throw new InvalidOperationException($"Ungültiger Bezeichner in {context}: '{id}'. SQL-Injection-Schutz greift ein.");
        }
    }

    private static void ValidateQualifiedIdentifier(string id, string context)
    {
        if (string.IsNullOrWhiteSpace(id) || !SafeQualifiedIdentifierRegex().IsMatch(id))
        {
            throw new InvalidOperationException($"Ungültiger qualifizierter Bezeichner in {context}: '{id}'. SQL-Injection-Schutz greift ein.");
        }
    }
}
