namespace Autheris.Application.Sql.Federation.KeySet;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;

/// <summary>
/// WP-124-3: Builds parameterized TableFilterClause instances representing dynamic IN lists.
/// Uses isolated @k_<n> parameter markers and supports combining with existing single-table pushdown filters.
/// </summary>
public static class KeySetClauseBuilder
{
    public static TableFilterClause Build(
        string targetColumn,
        IReadOnlyList<object> batchKeys,
        TableFilterClause? existingFilter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetColumn);
        ArgumentNullException.ThrowIfNull(batchKeys);

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        var paramNames = new List<string>(batchKeys.Count);

        for (int i = 0; i < batchKeys.Count; i++)
        {
            var marker = "@k_" + i.ToString(CultureInfo.InvariantCulture);
            if (!parameters.TryAdd(marker, batchKeys[i]))
            {
                throw new SecurityException($"Duplicate key-set parameter marker '{marker}' encountered.");
            }
            paramNames.Add(marker);
        }

        var structuralPredicates = new List<PushdownPredicateInfo>
        {
            new PushdownPredicateInfo(targetColumn, "In")
        };

        string BuildInSql(Func<string, string> columnResolver)
        {
            var resolvedCol = columnResolver(targetColumn);
            var quotedCol = DataApiFilterTranslator.QuoteIdentifier(resolvedCol);
            if (paramNames.Count == 0)
            {
                return "1 = 0";
            }
            return $"{quotedCol} IN ({string.Join(", ", paramNames)})";
        }

        var keySetClause = new TableFilterClause(
            BuildInSql(static c => c),
            parameters,
            [targetColumn])
        {
            TrinoSqlFactory = columnResolver => BuildInSql(columnResolver ?? (static c => c)),
            PushdownPredicates = structuralPredicates
        };

        if (existingFilter == null)
        {
            return keySetClause;
        }

        return Combine(existingFilter, keySetClause);
    }

    private static TableFilterClause Combine(TableFilterClause existing, TableFilterClause keySet)
    {
        var combinedParams = new Dictionary<string, object?>(existing.Parameters, StringComparer.Ordinal);
        foreach (var (k, v) in keySet.Parameters)
        {
            if (!combinedParams.TryAdd(k, v))
            {
                throw new SecurityException($"Collision in key-set parameter '{k}'.");
            }
        }

        var combinedCols = existing.ReferencedColumns
            .Concat(keySet.ReferencedColumns)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var combinedPreds = new List<PushdownPredicateInfo>();
        if (existing.PushdownPredicates != null) combinedPreds.AddRange(existing.PushdownPredicates);
        if (keySet.PushdownPredicates != null) combinedPreds.AddRange(keySet.PushdownPredicates);

        string CombinedTrinoSql(Func<string, string> columnResolver)
        {
            var existingSql = existing.GetTrinoPredicate(columnResolver);
            var keySetSql = keySet.GetTrinoPredicate(columnResolver);
            return $"({existingSql}) AND ({keySetSql})";
        }

        var defaultPredicate = $"({existing.SqlPredicate}) AND ({keySet.SqlPredicate})";

        return new TableFilterClause(
            defaultPredicate,
            combinedParams,
            combinedCols)
        {
            TrinoSqlFactory = CombinedTrinoSql,
            PushdownPredicates = combinedPreds
        };
    }
}
