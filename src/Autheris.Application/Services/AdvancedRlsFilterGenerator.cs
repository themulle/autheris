using System.Text.Json;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Application.Sql.Policy;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

namespace Autheris.Application.Services;

/// <summary>
/// Generator for cross-source set Row-Level Security (RLS) filters with chunked parameter budgeting.
/// </summary>
public static class AdvancedRlsFilterGenerator
{
    /// <summary>Typed form of <see cref="BuildCrossSourceSetFilter"/>: one parameter per value; an unknown dialect throws.</summary>
    public static PolicyPredicate BuildCrossSourceSetPredicate(ConsentRowFilter filter, DatabaseDialect dialect, string parameterPrefix = "__gql_rls_") =>
        AdvancedRlsPredicateBuilder.BuildCrossSourceSet(filter, PolicyParameterNamer.ToTargetDialect(dialect), parameterPrefix);

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
                        if (elem.ValueKind != JsonValueKind.Null)
                        {
                            values.Add(FormatLiteralValue(elem, dialect));
                        }
                    }
                }
                else if (doc.RootElement.ValueKind != JsonValueKind.Null)
                {
                    values.Add(FormatLiteralValue(doc.RootElement, dialect));
                }
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("ValueJson in CrossSourceSetFilter must be valid JSON.");
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

    private static string FormatLiteralValue(JsonElement elem, DatabaseDialect dialect)
    {
        return dialect.FormatSafeLiteral(elem);
    }

    public static string QuoteSingleIdentifier(string id, DatabaseDialect dialect) =>
        dialect.QuoteIdentifier(id);

    public static string QuoteQualifiedColumn(string qualifiedColumn, DatabaseDialect dialect) =>
        dialect.QuoteQualifiedColumn(qualifiedColumn);

    public static string FormatTableIdentifier(TableIdentifier table, DatabaseDialect dialect)
    {
        ValidateIdentifier(table.Schema, "Schema");
        ValidateIdentifier(table.TableName, "TableName");
        return dialect.FormatTableIdentifier(table);
    }

    internal static void ValidateIdentifier(string id, string context)
    {
        if (!SqlIdentifierGuard.IsValidIdentifier(id, maxLength: null))
        {
            throw new InvalidOperationException($"Invalid identifier in {context}: '{id}'. SQL injection protection triggered.");
        }
    }

    internal static void ValidateQualifiedIdentifier(string id, string context)
    {
        if (!SqlIdentifierGuard.IsValidQualifiedIdentifier(id, minSegments: 1, maxSegments: null, maxSegmentLength: null))
        {
            throw new InvalidOperationException($"Invalid qualified identifier in {context}: '{id}'. SQL injection protection triggered.");
        }
    }
}
