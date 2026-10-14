namespace Autheris.Application.Services;

using System;
using System.Collections.Generic;
using Autheris.Application.Policy;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Applies row-level security filters in memory for non-SQL or mock data sources (WP-ARC-B-04). A decision that carries a typed
/// <see cref="IRowPolicy"/> is evaluated from its typed predicate (<see cref="PolicyPredicateRowEvaluator"/>); no SQL text is parsed for
/// it. Only a decision that still has no typed policy (string-only legacy sources) falls back to the isolated
/// <see cref="LegacyTextRowFilterEvaluator"/>. Everything is fail closed.
/// </summary>
public static class InMemoryRowFilterEvaluator
{
    /// <summary>
    /// Virtual filters: an in-memory row filter cannot evaluate their subqueries (it would return nothing and look like
    /// "no data"). Sources that filter in memory refuse the request instead (403 with reason).
    /// </summary>
    public static void EnsureInMemoryFilterIsEnforceable(TableAccessDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.MandatoryRowPredicateSql != null)
        {
            throw new GatewayForbiddenException(
                $"Virtual filters ({string.Join(", ", decision.AppliedVirtualFilters ?? [])}) cannot be enforced on this data source; it does not filter in the database.");
        }
    }

    /// <summary>
    /// Filters <paramref name="rows"/> by the row filter of <paramref name="decision"/>. Typed policy first; a typed policy whose text
    /// projection no longer equals the decision's filter text (drift, see <see cref="TableAccessDecision.RequireTypedRowPolicy"/>) and
    /// a policy this evaluator does not know deny every row.
    /// </summary>
    public static List<IReadOnlyDictionary<string, object?>> FilterRows(
        List<IReadOnlyDictionary<string, object?>> rows,
        TableAccessDecision decision,
        TableMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(metadata);

        if (decision.RowPolicy is { } policy)
        {
            if (!string.Equals(decision.CombinedRowFilterSql, policy.Project().Sql, StringComparison.Ordinal))
            {
                return [];
            }

            return FilterRows(rows, policy, metadata);
        }

        return string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql)
            ? rows
            : LegacyTextRowFilterEvaluator.FilterRows(rows, decision.CombinedRowFilterSql, metadata, decision.RowFilterParameters);
    }

    /// <summary>Filters <paramref name="rows"/> by a typed row policy. An unknown policy kind or an unimplemented predicate node denies every row.</summary>
    public static List<IReadOnlyDictionary<string, object?>> FilterRows(
        List<IReadOnlyDictionary<string, object?>> rows,
        IRowPolicy policy,
        TableMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(metadata);

        return policy is PolicyPredicateRowPolicy typed
            ? PolicyPredicateRowEvaluator.Filter(rows, typed.Predicate, metadata)
            : [];
    }

    /// <summary>Text entry point for the listed text consumers and legacy string-only decisions; see <see cref="LegacyTextRowFilterEvaluator"/>.</summary>
    public static List<IReadOnlyDictionary<string, object?>> FilterRows(
        List<IReadOnlyDictionary<string, object?>> rows,
        string rowFilterSql,
        TableMetadata metadata,
        IReadOnlyDictionary<string, object?>? parameters = null) =>
        LegacyTextRowFilterEvaluator.FilterRows(rows, rowFilterSql, metadata, parameters);

    internal static bool IsTypeStrictRowFilter(string normalizedSql, System.Data.DataTable table) =>
        LegacyTextRowFilterEvaluator.IsTypeStrictRowFilter(normalizedSql, table);
}
