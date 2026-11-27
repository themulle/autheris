namespace Autheris.Application.Catalog.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Tree.Federation;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Join relations of a search hit: the stored table relations (the ones GraphQL relation fields and <c>describe_dataset</c> use),
/// resolved at query time from the caller's own visibility. Every failure answers no relations, never an unfiltered list.
/// </summary>
public sealed partial class CatalogDiscoveryService
{
    /// <summary>Upper bound of the distinct target tables whose visibility one search request resolves.</summary>
    internal const int MaxJoinRelationTargets = 500;

    private static readonly IReadOnlySet<string> NoColumns = new HashSet<string>();

    private Task<List<CatalogSearchHit>> WithJoinRelationsAsync(
        IReadOnlyList<CatalogSearchHit> hits,
        IReadOnlyDictionary<TableIdentifier, TableMetadata> callerTables,
        CatalogSearchQuery query,
        RequestContext context,
        CancellationToken ct)
    {
        var own = callerTables.ToDictionary(
            static kv => kv.Key,
            static kv => (IReadOnlySet<string>)kv.Value.Columns.Select(static c => c.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase));
        return WithJoinRelationsAsync(hits, own, query, context, ct);
    }

    private Task<List<CatalogSearchHit>> WithJoinRelationsAsync(
        IReadOnlyList<CatalogSearchHit> hits,
        IReadOnlyDictionary<TableIdentifier, IReadOnlySet<string>> ownVisibleColumns,
        CatalogSearchQuery query,
        RequestContext context,
        CancellationToken ct) =>
        ResolveJoinRelationsForHitsAsync(_visibility, _graphQlMap, _logger, hits, ownVisibleColumns, query, CallerOf(context), ct);

    internal static async Task<List<CatalogSearchHit>> ResolveJoinRelationsForHitsAsync(
        ICatalogVisibilityService? visibility,
        IGraphQlCatalogMap? graphQlMap,
        ILogger logger,
        IReadOnlyList<CatalogSearchHit> hits,
        IReadOnlyDictionary<TableIdentifier, IReadOnlySet<string>> ownVisibleColumns,
        CatalogSearchQuery query,
        CatalogCaller caller,
        CancellationToken ct)
    {
        var cleared = hits.Select(static h => h with { RelatedJoinPaths = [] }).ToList();
        var tableIds = hits.Where(static h => h.Kind == CatalogEntryKey.KindTable).Select(static h => h.TableIdentifier).Distinct().ToList();
        if (!query.ExpandRelations || visibility is null || tableIds.Count == 0)
        {
            return cleared;
        }

        try
        {
            var stored = new Dictionary<TableIdentifier, IReadOnlyList<GraphQlRelationMapping>?>();
            foreach (var id in tableIds)
            {
                stored[id] = await StoredRelationsAsync(graphQlMap, logger, id, ct).ConfigureAwait(false);
            }

            var rows = new Dictionary<TableIdentifier, CatalogVisibleRow>();
            await AddVisibleRowsAsync(visibility, caller, tableIds, rows, ct).ConfigureAwait(false);

            var declared = new Dictionary<TableIdentifier, IReadOnlyList<TableRelationship>?>();
            var targets = new List<TableIdentifier>();
            var seen = new HashSet<TableIdentifier>(rows.Keys);
            foreach (var id in tableIds)
            {
                if (!rows.TryGetValue(id, out var row))
                {
                    continue;
                }

                declared[id] = CatalogSearchDocumentBuilder.BuildForeignKeys(row.Table);
                var candidates = (stored[id]?.Select(static r => r.Target) ?? [])
                    .Concat(declared[id]?.Select(static d => d.ToTable) ?? []);
                foreach (var target in candidates)
                {
                    if (targets.Count < MaxJoinRelationTargets && seen.Add(target))
                    {
                        targets.Add(target);
                    }
                }
            }

            await AddVisibleRowsAsync(visibility, caller, targets, rows, ct).ConfigureAwait(false);

            var result = new List<CatalogSearchHit>(hits.Count);
            foreach (var hit in hits)
            {
                var id = hit.TableIdentifier;
                var relations = hit.Kind == CatalogEntryKey.KindTable
                    ? ResolveJoinRelations(
                        id,
                        stored.GetValueOrDefault(id),
                        declared.GetValueOrDefault(id),
                        ownVisibleColumns.GetValueOrDefault(id) ?? NoColumns,
                        rows,
                        query.MaxRelationsPerHit)
                    : [];
                result.Add(hit with { RelatedJoinPaths = relations });
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Catalog search join relations lookup failed ({ExceptionType}); join relations omitted.", ex.GetType().Name);
            return cleared;
        }
    }

    private static async Task<IReadOnlyList<GraphQlRelationMapping>?> StoredRelationsAsync(
        IGraphQlCatalogMap? graphQlMap, ILogger logger, TableIdentifier id, CancellationToken ct)
    {
        if (graphQlMap is null)
        {
            return null;
        }

        try
        {
            return (await graphQlMap.GetTableAsync(id, ct).ConfigureAwait(false))?.Relations;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Catalog search stored relation lookup failed ({ExceptionType}); declared relations only.", ex.GetType().Name);
            return null;
        }
    }

    private static async Task AddVisibleRowsAsync(
        ICatalogVisibilityService visibility,
        CatalogCaller caller,
        IReadOnlyCollection<TableIdentifier> ids,
        Dictionary<TableIdentifier, CatalogVisibleRow> rows,
        CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var batch = await visibility.FindVisibleManyAsync(caller, ids, ct).ConfigureAwait(false);
        foreach (var id in ids)
        {
            if (batch.IsAuthoritative)
            {
                if (batch.Rows.TryGetValue(id, out var batched))
                {
                    rows[id] = batched;
                }
            }
            else if (await visibility.FindVisibleAsync(caller, id, ct).ConfigureAwait(false) is { } single)
            {
                rows[id] = single;
            }
        }
    }

    /// <summary>
    /// The join relations of one table hit, always from the hit's perspective. A relation is listed only when the caller sees the
    /// hit, the target (full shape) and both join columns; the naming rule applies only while no stored relation to a visible target exists.
    /// </summary>
    internal static IReadOnlyList<TableRelationship> ResolveJoinRelations(
        TableIdentifier hit,
        IReadOnlyList<GraphQlRelationMapping>? stored,
        IReadOnlyList<TableRelationship>? declared,
        IReadOnlySet<string> ownVisibleColumns,
        IReadOnlyDictionary<TableIdentifier, CatalogVisibleRow> rows,
        int maxRelations)
    {
        if (maxRelations <= 0 || !rows.TryGetValue(hit, out var hitRow) || hitRow.Shape != CatalogOutputShape.Full)
        {
            return [];
        }

        var candidates = new List<TableRelationship>();
        var storedToVisibleTarget = false;
        foreach (var r in stored ?? [])
        {
            storedToVisibleTarget |= rows.TryGetValue(r.Target, out var targetRow) && targetRow.Shape == CatalogOutputShape.Full;
            if (r.Columns is [var from] && r.TargetColumns is [var to])
            {
                candidates.Add(new TableRelationship(hit, from, r.Target, to, TableRelationshipType.ForeignKey, Incoming: r.IsReverse));
            }
        }

        foreach (var d in declared ?? [])
        {
            if (!storedToVisibleTarget || d.Type == TableRelationshipType.ForeignKey)
            {
                candidates.Add(d);
            }
        }

        var seen = new HashSet<(TableIdentifier, string, string)>();
        var result = new List<TableRelationship>();
        foreach (var c in candidates)
        {
            if (!ownVisibleColumns.Contains(c.FromColumn)
                || !rows.TryGetValue(c.ToTable, out var t)
                || t.Shape != CatalogOutputShape.Full
                || !t.Table.Columns.Any(col => !col.DiscoveryHidden && string.Equals(col.ColumnName, c.ToColumn, StringComparison.OrdinalIgnoreCase))
                || !RelationExecutability.IsSameExecutionSource(hitRow.Table, t.Table)
                || !seen.Add((c.ToTable, c.FromColumn.ToUpperInvariant(), c.ToColumn.ToUpperInvariant())))
            {
                continue;
            }

            result.Add(c);
            if (result.Count == maxRelations)
            {
                break;
            }
        }

        return result;
    }
}
