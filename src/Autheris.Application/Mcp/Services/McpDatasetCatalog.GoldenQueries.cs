namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Model;

public sealed partial class McpDatasetCatalog
{
    /// <summary>
    /// Golden queries of one dataset for the caller. The owning dataset is the one being described; relation targets must be visible, and
    /// an example that mentions a hidden column of the dataset or of a reached relation target is dropped before the result cap.
    /// </summary>
    private async Task<IReadOnlyList<McpDatasetExample>> LoadExamplesAsync(
        ClaimsPrincipal principal,
        string domain,
        string tableName,
        GraphQlTableMapping? graphQl,
        IReadOnlyDictionary<string, GraphQlColumnMapping> graphQlColumns,
        IReadOnlySet<string> hiddenColumns,
        CancellationToken ct)
    {
        if (goldenQueryService == null)
        {
            return [];
        }

        var relations = graphQl?.Relations ?? [];
        var visibleTargets = new HashSet<(string, string)>();
        if (relations.Count > 0 && graphQl is not null)
        {
            var visibleIds = await VisibleRelationTargetsAsync(principal, graphQl, ct).ConfigureAwait(false);
            foreach (var relation in relations.Where(r => visibleIds.Contains(DatasetId(r.Target))))
            {
                visibleTargets.Add((relation.Target.Domain.ToLowerInvariant(), relation.Target.TableName.ToLowerInvariant()));
            }
        }

        var hiddenIds = GoldenQueryService.BuildHiddenIdentifierSet(hiddenColumns, graphQlColumns);
        bool IsOwner(string d, string t) =>
            string.Equals(d, domain, StringComparison.OrdinalIgnoreCase) && string.Equals(t, tableName, StringComparison.OrdinalIgnoreCase);

        var context = new GoldenQueryReadContext(
            McpCatalogVisibility.SafeTenantId(principal),
            IsDatasetVisible: (d, t) => IsOwner(d, t) || visibleTargets.Contains((d.ToLowerInvariant(), t.ToLowerInvariant())),
            HiddenIdentifiers: (d, t) => IsOwner(d, t) ? hiddenIds : null,
            ReferencedDatasets: q => relations
                .Where(r => GoldenQueryService.MentionsAnyIdentifier(q.QueryText, new HashSet<string>([r.Field], StringComparer.OrdinalIgnoreCase)))
                .Select(r => (r.Target.Domain, r.Target.TableName)));

        var goldens = await goldenQueryService.GetGoldenQueriesAsync(context, domain, tableName, ct).ConfigureAwait(false);
        return GoldenQueryService.ExcludeHiddenColumnQueries(goldens, hiddenIds).Select(g => new McpDatasetExample(g.Title, g.Description, g.QueryText, g.VariablesJson)).ToList();
    }
}
