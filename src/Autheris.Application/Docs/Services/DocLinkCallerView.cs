namespace Autheris.Application.Docs.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Document links as one caller may see them. A target counts as discoverable only when the caller's own discovery projection
/// of the table exists, is not hidden and, for a column target, lists the column with a state other than no-access.
/// </summary>
public interface IDocLinkCallerView
{
    /// <summary>Keys of the visible documents linked to the target; <c>null</c> when the target is not discoverable for the caller.</summary>
    Task<IReadOnlySet<DocKey>?> DocumentsLinkedToAsync(ClaimsPrincipal principal, string tenantId, TableIdentifier table, string? column, CancellationToken ct = default);

    /// <summary>The discoverable targets (as <c>domain.schema.table[.column]</c>) of each document; documents without one are absent.</summary>
    Task<IReadOnlyDictionary<DocKey, IReadOnlyList<string>>> VisibleTargetsOfAsync(ClaimsPrincipal principal, string tenantId, IReadOnlyList<DocKey> keys, CancellationToken ct = default);
}

public sealed class DocLinkCallerView : IDocLinkCallerView
{
    private readonly IDiscoveryService _discovery;
    private readonly IDocLinkDiscoveryReader _reader;

    public DocLinkCallerView(IDiscoveryService discovery, IDocLinkDiscoveryReader reader)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<IReadOnlySet<DocKey>?> DocumentsLinkedToAsync(ClaimsPrincipal principal, string tenantId, TableIdentifier table, string? column, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(principal, tenantId, new DocLinkTarget(column is null ? DocLinkTargetKind.Table : DocLinkTargetKind.Column, table.Domain, table.Schema, table.TableName, column), new Dictionary<TableIdentifier, DiscoveryObject?>(), ct).ConfigureAwait(false);
        return resolved is null ? null : await _reader.LinkedDocumentsAsync(tenantId, table, resolved.Value.Column, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<DocKey, IReadOnlyList<string>>> VisibleTargetsOfAsync(ClaimsPrincipal principal, string tenantId, IReadOnlyList<DocKey> keys, CancellationToken ct = default)
    {
        var result = new Dictionary<DocKey, IReadOnlyList<string>>();
        var cache = new Dictionary<TableIdentifier, DiscoveryObject?>();
        foreach (var key in keys.Distinct())
        {
            var names = new List<string>();
            foreach (var target in await _reader.TargetsOfAsync(tenantId, key, ct).ConfigureAwait(false))
            {
                if (await ResolveAsync(principal, tenantId, target, cache, ct).ConfigureAwait(false) is not { } r) continue;
                names.Add($"{r.Id.Domain}.{r.Id.Schema}.{r.Id.TableName}{(r.Column is null ? string.Empty : "." + r.Column)}");
            }

            if (names.Count > 0) result[key] = names;
        }

        return result;
    }

    private async Task<(TableIdentifier Id, string? Column)?> ResolveAsync(
        ClaimsPrincipal principal, string tenantId, DocLinkTarget target, Dictionary<TableIdentifier, DiscoveryObject?> cache, CancellationToken ct)
    {
        if (target.Kind == DocLinkTargetKind.Procedure || !TableIdentifier.TryCreate(target.Domain, target.Schema, target.Name, out var id)) return null;
        if (!cache.TryGetValue(id, out var obj))
        {
            obj = await DiscoverAsync(principal, tenantId, id, ct).ConfigureAwait(false);
            cache[id] = obj;
        }

        if (obj is null || obj.IsHidden || obj.Kind == DiscoveryObjectKind.Procedure) return null;
        if (target.Kind != DocLinkTargetKind.Column) return (id, null);
        var column = obj.Columns.FirstOrDefault(c => string.Equals(c.Name, target.Column, StringComparison.OrdinalIgnoreCase));
        return column is null || !DiscoveryColumnVisibility.IsVisible(column.State) ? null : (id, column.Name);
    }

    /// <summary>Any failure (rate limit, repository error) means "not discoverable": the caller then sees nothing.</summary>
    private async Task<DiscoveryObject?> DiscoverAsync(ClaimsPrincipal principal, string tenantId, TableIdentifier id, CancellationToken ct)
    {
        try
        {
            var tenant = new TenantId(tenantId);
            return await _discovery.GetAsync(id, new CatalogCaller(principal, tenant, CatalogChannel.Mcp), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
