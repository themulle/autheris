namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed partial class McpDatasetCatalog
{
    private const int MaxRelationTargets = 1024;
    private const int VisibilityBatchChunkSize = 256;

    private readonly record struct ResolvedTargetVisibility(
        HashSet<string> VisibleDatasetIds,
        HashSet<TableIdentifier> VisibleTables,
        Dictionary<TableIdentifier, IReadOnlySet<string>?>? ColumnsByTarget);

    private async Task<McpGraphQlAccess> GraphQlAccessAsync(
        ClaimsPrincipal principal,
        GraphQlTableMapping graphQl,
        IReadOnlyList<McpDatasetColumn> columns,
        CancellationToken ct,
        IReadOnlySet<string>? hiddenTextColumns = null,
        McpDescribeNarrowing.Request? narrowing = null)
    {
        IReadOnlyList<McpGraphQlRelation> relations;
        if (narrowing is { WantsRelations: false })
        {
            relations = [];
        }
        else
        {
            var candidateRelations = narrowing?.RelationTerm is null
                ? graphQl.Relations
                : graphQl.Relations.Where(r => narrowing.MatchesRelation(r.Field, DatasetId(r.Target))).ToList();
            var targets = candidateRelations.Select(r => r.Target).Distinct().ToList();
            var resolved = await ResolveVisibleTargetsAndColumnsAsync(principal, targets, ct).ConfigureAwait(false);
            relations = await new McpRelationColumnProjector(
                (id, token) => VisibleTargetColumnsAsync(principal, id, token),
                resolved.ColumnsByTarget is { } batchCols
                    ? (_, _) => ValueTask.FromResult<IReadOnlyDictionary<TableIdentifier, IReadOnlySet<string>?>?>(batchCols)
                    : null)
                .BuildAsync(candidateRelations, resolved.VisibleDatasetIds, DatasetId, columns, ct)
                .ConfigureAwait(false);
        }

        if (narrowing is { WantsGraphQl: false })
        {
            return McpGraphQlAccess.RelationsOnly(relations);
        }

        var topFields = columns.Where(c => c.GraphQlField != null).Take(ExampleColumns).ToList();
        string? example = hiddenTextColumns is { Count: > 0 } && topFields.Any(c => hiddenTextColumns.Contains(c.Name))
            ? null
            : $"query {{ {graphQl.QueryField}(first: 10) {{ {string.Join(' ', topFields.Select(c => c.GraphQlField))} }} }}";
        var arguments =
            $"where: {graphQl.FilterType} – per field eq, neq, gt, gte, lt, lte, in, nin, contains, startsWith, endsWith, isNull; " +
            $"combine with and/or (lists) and not. orderBy: [{graphQl.OrderByType}] – {{ field: ASC | DESC }}. " +
            "first: Int (default 100), offset: Int. Always pass a small first: queries above the complexity budget are rejected. " +
            "Filter and sort only on columns you can read in clear text.";

        return new McpGraphQlAccess(GraphQlEndpoint, graphQl.QueryField, graphQl.TypeName, graphQl.FilterType, graphQl.OrderByType, arguments, example, relations,
            Variables: graphQl.ListArguments,
            Aggregate: McpGraphQlBlock.Aggregate(graphQl),
            Connection: McpGraphQlBlock.Connection(graphQl, columns));
    }

    /// <summary>
    /// Resolves target dataset visibility and (when supported by <see cref="ICatalogVisibilityService.FindVisibleManyAsync"/>)
    /// visible target column names in bounded batches of 256 tables so hub tables avoid per-relation queries.
    /// </summary>
    private async Task<ResolvedTargetVisibility> ResolveVisibleTargetsAndColumnsAsync(
        ClaimsPrincipal principal,
        IReadOnlyList<TableIdentifier> targets,
        CancellationToken ct)
    {
        var distinctTargets = targets.Distinct().Take(MaxRelationTargets).ToList();
        if (distinctTargets.Count == 0)
        {
            return new ResolvedTargetVisibility(
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<TableIdentifier>(),
                new Dictionary<TableIdentifier, IReadOnlySet<string>?>());
        }

        if (IsBypassed)
        {
            var found = await _metadataRepository.GetTablesAsync(distinctTargets, ct).ConfigureAwait(false);
            var ids = found.Select(t => DatasetId(t.Identifier)).ToHashSet(StringComparer.Ordinal);
            var tableSet = found.Select(t => t.Identifier).ToHashSet();
            return new ResolvedTargetVisibility(ids, tableSet, ColumnsByTarget: null);
        }

        if (visibility == null)
        {
            return new ResolvedTargetVisibility(
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<TableIdentifier>(),
                new Dictionary<TableIdentifier, IReadOnlySet<string>?>());
        }

        var caller = CallerOf(principal);
        var visibleIds = new HashSet<string>(StringComparer.Ordinal);
        var visibleTables = new HashSet<TableIdentifier>();
        Dictionary<TableIdentifier, IReadOnlySet<string>?>? preloadedColumns = [];
        var allAuthoritative = true;

        foreach (var chunk in distinctTargets.Chunk(VisibilityBatchChunkSize))
        {
            var batch = await visibility.FindVisibleManyAsync(caller, chunk, ct).ConfigureAwait(false);
            if (batch.IsAuthoritative)
            {
                foreach (var target in chunk)
                {
                    if (batch.Rows.TryGetValue(target, out var visibleRow))
                    {
                        visibleTables.Add(target);
                        visibleIds.Add(DatasetId(target));
                        if (allAuthoritative)
                        {
                            preloadedColumns![target] = visibleRow.Shape == CatalogOutputShape.Full
                                ? visibleRow.Table.Columns.Select(c => c.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase)
                                : null;
                        }
                    }
                    else if (allAuthoritative)
                    {
                        preloadedColumns![target] = null;
                    }
                }
            }
            else
            {
                allAuthoritative = false;
                preloadedColumns = null;
                var visible = await visibility.FilterVisibleAsync(caller, chunk, ct).ConfigureAwait(false);
                foreach (var target in visible)
                {
                    visibleTables.Add(target);
                    visibleIds.Add(DatasetId(target));
                }
            }
        }

        return new ResolvedTargetVisibility(visibleIds, visibleTables, allAuthoritative ? preloadedColumns : null);
    }

    /// <summary>
    /// Columns of a relation target that the caller may see (Full shape only; the visibility engine already drops denied columns).
    /// Null when the target is not visible or only a summary (I392-SC-05). The bypass path reads the metadata repository, as elsewhere.
    /// </summary>
    private async ValueTask<IReadOnlySet<string>?> VisibleTargetColumnsAsync(ClaimsPrincipal principal, TableIdentifier target, CancellationToken ct)
    {
        TableMetadata? table;
        if (IsBypassed)
        {
            var found = await _metadataRepository.GetTableMetadataAsync(target, ct).ConfigureAwait(false);
            table = found is { Table.IsActive: true } && Shape(true, found) == CatalogOutputShape.Full ? found : null;
        }
        else if (visibility == null)
        {
            return null;
        }
        else
        {
            var row = await visibility.FindVisibleAsync(CallerOf(principal), target, ct).ConfigureAwait(false);
            table = row is { Shape: CatalogOutputShape.Full } full ? full.Table : null;
        }

        return table?.Columns.Select(c => c.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void LogDescribeTiming(bool brief, long examplesMs, long graphQlMs, long startedAt, int relationCount, int exampleCount)
    {
        if (logger is null)
        {
            return;
        }

        var totalMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        if (totalMs >= 1000)
        {
            logger.LogWarning(
                "describe_dataset completed slowly: brief={Brief} totalMs={TotalMs} examplesMs={ExamplesMs} graphQlMs={GraphQlMs} relations={RelationCount} examples={ExampleCount}",
                brief,
                totalMs,
                examplesMs,
                graphQlMs,
                relationCount,
                exampleCount);
        }
        else
        {
            logger.LogDebug(
                "describe_dataset timing: brief={Brief} totalMs={TotalMs} examplesMs={ExamplesMs} graphQlMs={GraphQlMs} relations={RelationCount} examples={ExampleCount}",
                brief,
                totalMs,
                examplesMs,
                graphQlMs,
                relationCount,
                exampleCount);
        }
    }
}
