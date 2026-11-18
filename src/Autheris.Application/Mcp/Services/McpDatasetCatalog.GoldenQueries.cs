namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed partial class McpDatasetCatalog
{
    /// <summary>
    /// Golden queries of one dataset for the caller. The caller view is built by the shared <see cref="GoldenQueryCallerView"/>: relation
    /// targets (transitively) must be visible, hidden columns of every reached dataset drop the example and an unresolvable field drops it.
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

        var view = await GoldenQueryCallerView.BuildAsync(
            [new GoldenQueryCallerView.Seed(owner.Domain, owner.TableName, owner, graphQl)],
            graphQlMap,
            (targets, token) => FilterVisibleTargetsAsync(principal, targets, token),
            async (id, domain, table, mapping, token) =>
            {
                if (string.Equals(domain, owner.Domain, StringComparison.OrdinalIgnoreCase) && string.Equals(table, owner.TableName, StringComparison.OrdinalIgnoreCase))
                {
                    return hiddenColumns;
                }

                var visibleColumns = id is { } target ? await VisibleTargetColumnsAsync(principal, target, token).ConfigureAwait(false) : null;
                return visibleColumns is null
                    ? null
                    : (mapping?.Columns.Select(c => c.ColumnName).Where(c => !visibleColumns.Contains(c)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            },
            ct).ConfigureAwait(false);

        var context = view.ToContext(McpCatalogVisibility.TryTenantId(principal));
        var goldens = await goldenQueryService.GetGoldenQueriesAsync(context, owner.Domain, owner.TableName, ct).ConfigureAwait(false);
        var hiddenIds = GoldenQueryService.BuildHiddenIdentifierSet(hiddenColumns, graphQlColumns);
        return GoldenQueryService.ExcludeHiddenColumnQueries(goldens, hiddenIds)
            .Select(g => new McpDatasetExample(g.Title, g.Description, g.QueryText, g.VariablesJson))
            .ToList();
    }

    private async Task<IReadOnlySet<TableIdentifier>> FilterVisibleTargetsAsync(ClaimsPrincipal principal, IReadOnlyList<TableIdentifier> targets, CancellationToken ct)
    {
        if (IsBypassed)
        {
            return targets.ToHashSet();
        }

        if (visibility == null)
        {
            return new HashSet<TableIdentifier>();
        }

        var visible = await visibility.FilterVisibleAsync(CallerOf(principal), targets, ct).ConfigureAwait(false);
        return visible.ToHashSet();
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
