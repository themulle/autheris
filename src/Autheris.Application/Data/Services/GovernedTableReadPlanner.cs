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

    public string BuildFullTableSelect(TableIdentifier table) => $"SELECT * FROM {table.ToQualifiedName()}";

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
        else if (TryResolveClearKey(metadata, decision, out var key))
        {
            items.AddRange(key.Select(DataApiFilterTranslator.QuoteIdentifier));
        }
        else if (intent.Offset > 0)
        {
            // OFFSET paging needs a deterministic ORDER BY (SQL Server and Oracle require one). Without a usable key, order by
            // every column that is readable in clear text and has an orderable type (never a masked or denied column: ordering
            // on it would leak its order).
            var composite = metadata.Columns
                .Where(c => decision.GetEffectiveColumnAccess(c.ColumnName, metadata) == ColumnAccessLevel.Clear && IsOrderableType(c.DataType))
                .Select(c => DataApiFilterTranslator.QuoteIdentifier(c.ColumnName))
                .ToList();
            if (composite.Count == 0)
            {
                throw new ArgumentException(
                    $"Offset paging on table '{intent.Table.ToQualifiedName()}' needs an explicit order, a primary key or columns readable in clear text to order by.",
                    nameof(intent));
            }

            items.AddRange(composite);
        }

        return items;
    }

    /// <summary>
    /// Resolves a deterministic clear-text ordering key when the caller did not specify an explicit ORDER BY:
    /// 1. Declared <see cref="TableMetadata.PrimaryKeyColumns"/> when all columns exist and are Clear.
    /// 2. Declared unique index in <see cref="TableMetadata.Indexes"/> with the fewest columns where all columns exist and are Clear.
    /// 3. Column <c>Meta</c> primary key markers (<c>primary_key_ordinal</c>, <c>is_primary_key=true</c>, <c>role=primary_key</c>) when all marked columns are Clear.
    /// 4. Conventional <c>(client_id, ts)</c> or <c>client_id</c> key columns when <see cref="TableMetadata.PrimaryKeyColumns"/> is empty and those columns exist and are Clear.
    /// </summary>
    public static bool TryResolveClearKey(TableMetadata metadata, TableAccessDecision decision, out List<string> key)
    {
        if (metadata.PrimaryKeyColumns.Count > 0 &&
            TryResolveAllClearColumns(metadata.PrimaryKeyColumns, metadata, decision, out key))
        {
            return true;
        }

        if (metadata.Indexes.Count > 0)
        {
            foreach (var index in metadata.Indexes
                         .Where(i => i is { IsUnique: true, Columns.Count: > 0 })
                         .OrderBy(i => i.Columns.Count))
            {
                if (TryResolveAllClearColumns(index.Columns, metadata, decision, out key))
                {
                    return true;
                }
            }
        }

        var metaKeyColumns = ExtractMetaPrimaryKeyColumns(metadata.Columns);
        if (metaKeyColumns.Count > 0 &&
            TryResolveAllClearColumns(metaKeyColumns, metadata, decision, out key))
        {
            return true;
        }

        if (metadata.PrimaryKeyColumns.Count == 0)
        {
            var clientIdCol = metadata.GetColumn("client_id");
            if (clientIdCol != null &&
                decision.GetEffectiveColumnAccess(clientIdCol.ColumnName, metadata) == ColumnAccessLevel.Clear)
            {
                var tsCol = metadata.GetColumn("ts");
                if (tsCol != null &&
                    decision.GetEffectiveColumnAccess(tsCol.ColumnName, metadata) == ColumnAccessLevel.Clear)
                {
                    key = [clientIdCol.ColumnName, tsCol.ColumnName];
                    return true;
                }

                key = [clientIdCol.ColumnName];
                return true;
            }
        }

        key = new List<string>();
        return false;
    }

    private static bool TryResolveAllClearColumns(
        IReadOnlyList<string> candidateColumns,
        TableMetadata metadata,
        TableAccessDecision decision,
        out List<string> key)
    {
        key = new List<string>(candidateColumns.Count);
        if (candidateColumns.Count == 0)
        {
            return false;
        }

        foreach (var candidate in candidateColumns)
        {
            var col = metadata.GetColumn(candidate);
            if (col == null || decision.GetEffectiveColumnAccess(col.ColumnName, metadata) != ColumnAccessLevel.Clear)
            {
                key.Clear();
                return false;
            }

            if (!key.Contains(col.ColumnName, StringComparer.OrdinalIgnoreCase))
            {
                key.Add(col.ColumnName);
            }
        }

        return key.Count > 0;
    }

    public static IReadOnlyList<string> ExtractMetaPrimaryKeyColumns(IReadOnlyList<TableColumn> columns)
    {
        if (columns == null || columns.Count == 0)
        {
            return Array.Empty<string>();
        }

        var ordinalCols = new List<(int Ordinal, int Index, string Name)>();
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            if (col.Meta != null &&
                col.Meta.TryGetValue("primary_key_ordinal", out var ordStr) &&
                int.TryParse(ordStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ord) &&
                ord >= 1)
            {
                ordinalCols.Add((ord, i, col.ColumnName));
            }
        }

        if (ordinalCols.Count > 0)
        {
            return ordinalCols
                .OrderBy(x => x.Ordinal)
                .ThenBy(x => x.Index)
                .Select(x => x.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var flagCols = new List<string>();
        foreach (var col in columns)
        {
            if (col.Meta == null) continue;
            if ((col.Meta.TryGetValue("is_primary_key", out var isPk) && string.Equals(isPk, "true", StringComparison.OrdinalIgnoreCase)) ||
                (col.Meta.TryGetValue("role", out var role) && string.Equals(role, "primary_key", StringComparison.OrdinalIgnoreCase)))
            {
                if (!flagCols.Contains(col.ColumnName, StringComparer.OrdinalIgnoreCase))
                {
                    flagCols.Add(col.ColumnName);
                }
            }
        }

        return flagCols.Count > 0 ? flagCols : Array.Empty<string>();
    }

    private static readonly string[] NonOrderableTypeMarkers =
    [
        "text", "image", "xml", "json", "geometry", "geography", "hierarchyid", "blob", "clob", "bytea", "(max)",
        "sql_variant", "variant", "array", "[]", "map", "row", "struct", "object", "sdo_", "raw", "binary", "bfile", "long", "lob", "cursor"
    ];

    /// <summary>False for LOB, document, spatial and structured types that cannot (or should not) appear in an ORDER BY.</summary>
    private static bool IsOrderableType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return true;
        }

        foreach (var marker in NonOrderableTypeMarkers)
        {
            if (dataType.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyDictionary<string, object?> ClientParameters(TableReadIntent intent) =>
        intent.Filter == null || intent.Filter.Parameters.Count == 0
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>((IEnumerable<KeyValuePair<string, object?>>)intent.Filter.Parameters, StringComparer.Ordinal);
}
