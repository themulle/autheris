namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed partial class McpDatasetCatalog
{
    /// <summary>
    /// Golden queries of one dataset for the caller. The caller view is built by the shared <see cref="GoldenQueryCallerView"/>: relation
    /// targets referenced by candidate queries must be visible, hidden columns of every reached dataset drop the example and an unresolvable field drops it.
    /// </summary>
    private async Task<IReadOnlyList<McpDatasetExample>> LoadExamplesAsync(
        ClaimsPrincipal principal,
        TableIdentifier owner,
        GraphQlTableMapping? graphQl,
        IReadOnlyDictionary<string, GraphQlColumnMapping> graphQlColumns,
        IReadOnlySet<string> hiddenColumns,
        CancellationToken ct)
    {
        if (goldenQueryService == null)
        {
            return [];
        }

        var startedAt = Stopwatch.GetTimestamp();
        var tenant = McpCatalogVisibility.TryTenantId(principal);
        var candidateSet = await goldenQueryService.GetCandidateQueriesAsync(tenant, owner.Domain, owner.TableName, ct).ConfigureAwait(false);
        if (candidateSet.HasKnownCandidates && candidateSet.Queries.Count == 0)
        {
            LogExampleResolution(candidateCount: 0, returnedCount: 0, referencedTargets: 0, startedAt);
            return [];
        }

        var preloadedColumns = new Dictionary<TableIdentifier, IReadOnlySet<string>?>();
        var view = await GoldenQueryCallerView.BuildAsync(
            [new GoldenQueryCallerView.Seed(owner.Domain, owner.TableName, owner, graphQl)],
            graphQlMap,
            (targets, token) => FilterVisibleTargetsWithColumnsAsync(principal, targets, preloadedColumns, token),
            async (id, domain, table, mapping, token) =>
            {
                if (string.Equals(domain, owner.Domain, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(table, owner.TableName, StringComparison.OrdinalIgnoreCase))
                {
                    return hiddenColumns;
                }

                if (id is not { } target)
                {
                    return null;
                }

                if (!preloadedColumns.TryGetValue(target, out var visibleColumns))
                {
                    visibleColumns = await VisibleTargetColumnsAsync(principal, target, token).ConfigureAwait(false);
                }

                return visibleColumns is null
                    ? null
                    : (mapping?.Columns.Select(c => c.ColumnName).Where(c => !visibleColumns.Contains(c)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            },
            candidateSet.HasKnownCandidates ? candidateSet.Queries : null,
            ct).ConfigureAwait(false);

        var context = view.ToContext(tenant);
        var goldens = await goldenQueryService.GetGoldenQueriesAsync(context, owner.Domain, owner.TableName, ct).ConfigureAwait(false);
        var hiddenIds = GoldenQueryService.BuildHiddenIdentifierSet(hiddenColumns, graphQlColumns);
        var examples = GoldenQueryService.ExcludeHiddenColumnQueries(goldens, hiddenIds)
            .Select(g => new McpDatasetExample(g.Title, g.Description, g.QueryText, g.VariablesJson))
            .ToList();

        var candidateCount = candidateSet.HasKnownCandidates ? candidateSet.Queries.Count : goldens.Count;
        var referencedTargets = Math.Max(0, view.ResolvedDatasetCount - 1);
        LogExampleResolution(candidateCount, examples.Count, referencedTargets, startedAt);
        return examples;
    }

    private async Task<IReadOnlySet<TableIdentifier>> FilterVisibleTargetsWithColumnsAsync(
        ClaimsPrincipal principal,
        IReadOnlyList<TableIdentifier> targets,
        Dictionary<TableIdentifier, IReadOnlySet<string>?> preloadedColumns,
        CancellationToken ct)
    {
        if (IsBypassed)
        {
            return targets.ToHashSet();
        }

        if (visibility == null)
        {
            return new HashSet<TableIdentifier>();
        }

        var resolved = await ResolveVisibleTargetsAndColumnsAsync(principal, targets, ct).ConfigureAwait(false);
        if (resolved.ColumnsByTarget is { } columnsByTarget)
        {
            foreach (var (targetId, cols) in columnsByTarget)
            {
                preloadedColumns[targetId] = cols;
            }
        }

        return resolved.VisibleTables;
    }

    private void LogExampleResolution(int candidateCount, int returnedCount, int referencedTargets, long startedAt)
    {
        if (logger is null)
        {
            return;
        }

        var elapsedMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        if (elapsedMs >= 1000)
        {
            logger.LogWarning(
                "describe_dataset example resolution completed slowly: candidates={CandidateCount} returned={ReturnedCount} referencedTargets={ReferencedTargets} elapsedMs={ElapsedMs}",
                candidateCount,
                returnedCount,
                referencedTargets,
                elapsedMs);
        }
        else
        {
            logger.LogDebug(
                "describe_dataset example resolution completed: candidates={CandidateCount} returned={ReturnedCount} referencedTargets={ReferencedTargets} elapsedMs={ElapsedMs}",
                candidateCount,
                returnedCount,
                referencedTargets,
                elapsedMs);
        }
    }

    /// <summary>Hidden-text columns plus every mapped column that discovery already withheld from the caller.</summary>
    private static IReadOnlySet<string> ExampleHiddenColumns(IReadOnlySet<string> hiddenText, GraphQlTableMapping? graphQl, Autheris.Domain.Model.DiscoveryObject obj)
    {
        var hidden = new HashSet<string>(hiddenText, StringComparer.OrdinalIgnoreCase);
        if (graphQl is not null)
        {
            hidden.UnionWith(graphQl.Columns.Select(c => c.ColumnName).Where(n => !obj.Columns.Any(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))));
        }

        return hidden;
    }
}
