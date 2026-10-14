namespace Autheris.Application.Data.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// WP-ARC-B-01: the shared table-read planner extracted from <c>GovernedDataQueryService</c> (column authorization,
/// oracle-inference guard on filter and order columns, SQL formulation). Stateless and thread-safe.
/// <para>
/// No policy text is spliced here: the decision authorizes the request and drives column access; the row policy and the
/// tenant restriction are applied exactly once, by the compiler on the governed path. Identifiers are emitted as quoted
/// catalog spellings and every value travels as a client parameter (or, for the REST text translator, as a literal that the
/// compiler binds).
/// </para>
/// </summary>
public sealed class GovernedTableReadPlanner : IGovernedTableReadPlanner
{
    /// <summary>Alias of the single column of <see cref="PlanCount"/>.</summary>
    public const string CountColumnAlias = "total_count";

    public PlannedTableRead Plan(TableReadIntent intent, TableMetadata metadata, TableAccessDecision decision)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(decision);
        if (intent.Limit <= 0)
        {
            throw new ArgumentException("The read limit must be positive.", nameof(intent));
        }

        EnsureAllowed(intent, decision);
        var columnsToSelect = ResolveColumns(intent, metadata, decision);
        var predicate = ResolveFilter(intent, metadata, decision);
        var orderBy = ResolveOrderBy(intent, metadata, decision);

        var sb = new StringBuilder();
        sb.Append("SELECT ")
          .Append(string.Join(", ", columnsToSelect.Select(c => DataApiFilterTranslator.QuoteIdentifier(metadata.GetColumn(c)!.ColumnName))))
          .Append(" FROM ")
          .Append(intent.Table.ToString());

        if (predicate != null)
        {
            sb.Append(" WHERE ").Append(predicate);
        }

        if (orderBy.Count > 0)
        {
            sb.Append(" ORDER BY ").Append(string.Join(", ", orderBy));
        }

        // Trino-form pagination: the governed compile path parses OFFSET n ROWS LIMIT m and every dialect generator emits its
        // native form (capability PaginationStyle).
        if (intent.Offset > 0)
        {
            sb.Append(" OFFSET ").Append(intent.Offset.ToString(CultureInfo.InvariantCulture)).Append(" ROWS");
        }

        sb.Append(" LIMIT ").Append(intent.Limit.ToString(CultureInfo.InvariantCulture));

        return new PlannedTableRead(sb.ToString(), ClientParameters(intent), columnsToSelect);
    }

    public PlannedTableRead PlanCount(TableReadIntent intent, TableMetadata metadata, TableAccessDecision decision)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(decision);

        EnsureAllowed(intent, decision);
        var predicate = ResolveFilter(intent, metadata, decision);

        var sb = new StringBuilder();
        sb.Append("SELECT COUNT(*) AS ").Append(DataApiFilterTranslator.QuoteIdentifier(CountColumnAlias))
          .Append(" FROM ").Append(intent.Table.ToString());
        if (predicate != null)
        {
            sb.Append(" WHERE ").Append(predicate);
        }

        return new PlannedTableRead(sb.ToString(), ClientParameters(intent), Array.Empty<string>());
    }

    private static void EnsureAllowed(TableReadIntent intent, TableAccessDecision decision)
    {
        if (!decision.IsAllowed)
        {
            var reasons = decision.DeniedReasons.Count > 0 ? string.Join("; ", decision.DeniedReasons) : "Access denied by governance policy.";
            throw new UnauthorizedAccessException($"Access to table '{intent.Table.ToQualifiedName()}' is forbidden: {reasons}");
        }
    }

    private static IReadOnlyList<string> ResolveColumns(TableReadIntent intent, TableMetadata metadata, TableAccessDecision decision)
    {
        if (intent.SelectColumns is { Count: > 0 } requested)
        {
            foreach (var col in requested)
            {
                var colMeta = metadata.GetColumn(col);
                if (colMeta == null)
                {
                    throw new ArgumentException($"Column '{col}' does not exist on table '{intent.Table.ToQualifiedName()}'.", nameof(intent));
                }

                if (decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata) == ColumnAccessLevel.Deny)
                {
                    throw new ArgumentException($"Access to column '{col}' is denied on table '{intent.Table.ToQualifiedName()}'.", nameof(intent));
                }
            }

            return requested;
        }

        var accessible = metadata.Columns
            .Where(c => decision.GetEffectiveColumnAccess(c.ColumnName, metadata) != ColumnAccessLevel.Deny)
            .Select(c => c.ColumnName)
            .ToList();
        if (accessible.Count == 0)
        {
            throw new UnauthorizedAccessException($"No accessible columns available on table '{intent.Table.ToQualifiedName()}'.");
        }

        return accessible;
    }

    /// <summary>The Trino predicate of the filter, or null. Filter columns must exist and be readable in clear text.</summary>
    private static string? ResolveFilter(TableReadIntent intent, TableMetadata metadata, TableAccessDecision decision)
    {
        if (intent.Filter == null)
        {
            return null;
        }

        foreach (var col in intent.Filter.ReferencedColumns)
        {
            var colMeta = metadata.GetColumn(col);
            if (colMeta == null)
            {
                throw new ArgumentException($"Column '{col}' does not exist on table '{intent.Table}'.", nameof(intent));
            }

            if (decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata) != ColumnAccessLevel.Clear)
            {
                throw new ArgumentException(
                    $"Filtering on masked or denied column '{col}' is rejected to prevent oracle inference attacks.",
                    nameof(intent));
            }
        }

        var predicate = intent.Filter.GetTrinoPredicate(name => metadata.GetColumn(name)?.ColumnName ?? name);
        return string.IsNullOrWhiteSpace(predicate) ? null : predicate;
    }

    /// <summary>ORDER BY items (quoted catalog spellings); the primary key when the request names no ordering (first clear column for a paged read of a table without one).</summary>
    private static List<string> ResolveOrderBy(TableReadIntent intent, TableMetadata metadata, TableAccessDecision decision)
    {
        var items = new List<string>();
        if (intent.OrderBy is { Count: > 0 } requested)
        {
            foreach (var item in requested)
            {
                var colMeta = metadata.GetColumn(item.Column);
                if (colMeta == null)
                {
                    throw new ArgumentException($"Column '{item.Column}' does not exist on table '{intent.Table}'.", nameof(intent));
                }

                if (decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata) != ColumnAccessLevel.Clear)
                {
                    throw new ArgumentException(
                        $"Ordering on masked or denied column '{item.Column}' is rejected to prevent oracle inference attacks.",
                        nameof(intent));
                }

                var quoted = DataApiFilterTranslator.QuoteIdentifier(colMeta.ColumnName);
                items.Add(item.Descending ? quoted + " DESC" : quoted);
            }
        }
        else if (metadata.PrimaryKeyColumns.Count > 0)
        {
            items.AddRange(metadata.PrimaryKeyColumns.Select(DataApiFilterTranslator.QuoteIdentifier));
        }
        else if (intent.Offset > 0)
        {
            // OFFSET paging needs a deterministic ORDER BY (SQL Server and Oracle require one). A table without a primary key
            // falls back to its first column that is readable in clear text (ordering on a masked column would leak its order).
            var fallback = metadata.Columns.FirstOrDefault(c => decision.GetEffectiveColumnAccess(c.ColumnName, metadata) == ColumnAccessLevel.Clear);
            if (fallback == null)
            {
                throw new ArgumentException(
                    $"Offset paging on table '{intent.Table.ToQualifiedName()}' needs a primary key or a column readable in clear text to order by.",
                    nameof(intent));
            }

            items.Add(DataApiFilterTranslator.QuoteIdentifier(fallback.ColumnName));
        }

        return items;
    }

    private static IReadOnlyDictionary<string, object?> ClientParameters(TableReadIntent intent) =>
        intent.Filter == null || intent.Filter.Parameters.Count == 0
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>((IEnumerable<KeyValuePair<string, object?>>)intent.Filter.Parameters, StringComparer.Ordinal);
}
