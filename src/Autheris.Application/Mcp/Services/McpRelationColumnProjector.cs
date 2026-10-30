namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Issue #392: builds the relation entries of <c>describe_dataset</c> with their join column pairs. Pairs are disclosed all-or-nothing
/// (like <c>TableKeyVisibility</c>): only when every own column is among the columns listed for the caller and every target column is
/// among the target's visible columns; otherwise <see cref="McpGraphQlRelation.Columns"/> is null. The target-visibility gate is
/// evaluated first and independently (I392-SC-06); any lookup failure yields null (I392-SC-05).
/// </summary>
internal sealed class McpRelationColumnProjector(Func<TableIdentifier, CancellationToken, ValueTask<IReadOnlySet<string>?>> targetColumns)
{
    public async ValueTask<IReadOnlyList<McpGraphQlRelation>> BuildAsync(
        IReadOnlyList<GraphQlRelationMapping> relations,
        IReadOnlySet<string> visibleDatasetIds,
        Func<TableIdentifier, string> datasetId,
        IReadOnlyList<McpDatasetColumn> ownListedColumns,
        CancellationToken ct)
    {
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ownListedColumns)
        {
            own.Add(c.Name);
        }

        var cache = new Dictionary<TableIdentifier, IReadOnlySet<string>?>();
        var result = new List<McpGraphQlRelation>(relations.Count);
        foreach (var r in relations)
        {
            var dataset = datasetId(r.Target);
            if (!visibleDatasetIds.Contains(dataset))
            {
                continue;
            }

            result.Add(new McpGraphQlRelation(r.Field, dataset, r.IsList, await ProjectAsync(r, own, cache, ct).ConfigureAwait(false)));
        }

        return result;
    }

    private async ValueTask<IReadOnlyList<McpRelationColumnPair>?> ProjectAsync(
        GraphQlRelationMapping relation,
        HashSet<string> own,
        Dictionary<TableIdentifier, IReadOnlySet<string>?> cache,
        CancellationToken ct)
    {
        var mine = relation.Columns;
        var theirs = relation.TargetColumns;
        if (mine is not { Count: > 0 } || theirs is null || mine.Count != theirs.Count)
        {
            return null;
        }

        foreach (var column in mine)
        {
            if (!own.Contains(column))
            {
                return null;
            }
        }

        if (!cache.TryGetValue(relation.Target, out var visible))
        {
            try
            {
                visible = await targetColumns(relation.Target, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                visible = null;
            }

            cache[relation.Target] = visible;
        }

        if (visible is null)
        {
            return null;
        }

        var pairs = new List<McpRelationColumnPair>(mine.Count);
        for (var i = 0; i < mine.Count; i++)
        {
            if (!visible.Contains(theirs[i]))
            {
                return null;
            }

            pairs.Add(new McpRelationColumnPair(mine[i], theirs[i]));
        }

        return pairs;
    }
}
