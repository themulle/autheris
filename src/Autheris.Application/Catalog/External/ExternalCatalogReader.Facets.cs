namespace Autheris.Application.Catalog.External;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Visibility;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed partial class ExternalCatalogReader
{
    public async Task<ExternalCatalogResult<ExternalCatalogFacetsDto>> GetFacetsAsync(
        ClaimsPrincipal user,
        TenantId tenantId,
        string? accessStatus = null,
        CancellationToken ct = default)
        => await GetFacetsAsync(user, tenantId, accessStatus, q: null, mode: null, language: null, ct).ConfigureAwait(false);

    public async Task<ExternalCatalogResult<ExternalCatalogFacetsDto>> GetFacetsAsync(
        ClaimsPrincipal user,
        TenantId tenantId,
        string? accessStatus,
        string? q,
        string? mode = null,
        string? language = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!CatalogAccessStatus.TryParseFilter(accessStatus, out var accessFilter))
        {
            return ExternalCatalogResult.Fail<ExternalCatalogFacetsDto>(400, "INVALID_QUERY", InvalidAccessStatusMessage);
        }

        var searchText = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (searchText is not null && searchText.Length > MaxSearchTextLength)
        {
            return ExternalCatalogResult.Fail<ExternalCatalogFacetsDto>(
                400,
                "INVALID_QUERY",
                $"The q filter exceeds the maximum length of {MaxSearchTextLength} characters.");
        }

        if (!TryParseSearchMode(mode, out var normalizedMode))
        {
            return ExternalCatalogResult.Fail<ExternalCatalogFacetsDto>(
                400,
                "INVALID_QUERY",
                "Invalid search mode. Allowed values: hybrid, keyword, semantic.");
        }

        bool isServicePrincipal = IsAppOnlyPrincipal(user);
        if (isServicePrincipal)
        {
            if (accessFilter is not null)
            {
                return ExternalCatalogResult.Fail<ExternalCatalogFacetsDto>(
                    400,
                    "INVALID_QUERY",
                    "The accessStatus filter applies to user callers only.");
            }

            if (searchText is not null)
            {
                return ExternalCatalogResult.Fail<ExternalCatalogFacetsDto>(
                    400,
                    "INVALID_QUERY",
                    "The q filter applies to user callers only.");
            }

            var allTables = await LoadActiveTablesAsync(ct).ConfigureAwait(false);
            var visibility = await ResolveCallerVisibilityAsync(user, tenantId, allTables, ct).ConfigureAwait(false);
            var inScope = await FilterInScopeTablesAsync(allTables, tenantId, ct).ConfigureAwait(false);
            if (_containerVisibility is not null)
            {
                var protectionsByTable = await ResolveProtectionsAsync(tenantId, inScope, ct).ConfigureAwait(false);
                inScope = await FilterContainerVisibleTablesAsync(user, tenantId, inScope, protectionsByTable, ct).ConfigureAwait(false);
            }

            var visible = inScope
                .Where(t => !t.Table.DiscoveryHidden || CanViewHiddenTable(t, visibility))
                .ToList();

            return ExternalCatalogResult.Ok(ToFacetsDto(DiscoveryService.BuildFacets(visible)));
        }

        try
        {
            var caller = new CatalogCaller(user, tenantId, CatalogChannel.Rest);
            var normalizedLang = string.IsNullOrWhiteSpace(language) ? null : language.Trim();
            var facets = searchText is null
                ? await _discoveryService.FacetsAsync(accessFilter, caller, ct).ConfigureAwait(false)
                : await _discoveryService.FacetsAsync(accessFilter, searchText, normalizedMode, normalizedLang, caller, ct).ConfigureAwait(false);
            return ExternalCatalogResult.Ok(ToFacetsDto(facets));
        }
        catch (DiscoveryStatusException ex)
        {
            return MapDiscoveryFailure<ExternalCatalogFacetsDto>(ex);
        }
    }

    private static ExternalCatalogFacetsDto ToFacetsDto(DiscoveryFacets facets)
    {
        var domains = facets.Domains
            .Select(static d => new ExternalCatalogDomainFacetDto(
                d.Domain,
                d.Count,
                d.Schemas.Select(static s => new ExternalCatalogSchemaFacetDto(s.Schema, s.Count)).ToArray()))
            .ToArray();
        return new ExternalCatalogFacetsDto(facets.TotalCount, domains);
    }

    private async Task<bool> IsAppTableContainerVisibleAsync(
        ClaimsPrincipal user,
        TenantId tenantId,
        TableIdentifier tableId,
        CancellationToken ct)
    {
        if (_containerVisibility is null)
        {
            return true;
        }

        if (_protectionProvider is null)
        {
            return false;
        }

        var snapshot = await _containerVisibility.GetAsync(tenantId, user, ct).ConfigureAwait(false);
        if (!snapshot.IsAvailable)
        {
            return false;
        }

        var protection = await _protectionProvider.GetAsync(tenantId, tableId, ct).ConfigureAwait(false);
        return IsTableVisibleInContainer(snapshot, tableId, protection);
    }

    private async Task<IReadOnlyList<TableMetadata>> FilterContainerVisibleTablesAsync(
        ClaimsPrincipal user,
        TenantId tenantId,
        IReadOnlyList<TableMetadata> tables,
        IReadOnlyDictionary<TableIdentifier, EffectiveProtection> protectionsByTable,
        CancellationToken ct)
    {
        if (_containerVisibility is null || tables.Count == 0)
        {
            return tables;
        }

        if (_protectionProvider is null)
        {
            return [];
        }

        var snapshot = await _containerVisibility.GetAsync(tenantId, user, ct).ConfigureAwait(false);
        if (!snapshot.IsAvailable)
        {
            return [];
        }

        var visible = new List<TableMetadata>(tables.Count);
        foreach (var table in tables)
        {
            if (protectionsByTable.TryGetValue(table.Identifier, out var protection)
                && IsTableVisibleInContainer(snapshot, table.Identifier, protection))
            {
                visible.Add(table);
            }
        }

        return visible;
    }

    private static bool IsTableVisibleInContainer(
        ContainerVisibilitySnapshot snapshot,
        TableIdentifier tableId,
        EffectiveProtection? protection) =>
        protection is not null
        && snapshot.IsAvailable
        && snapshot.Containers.TryGetValue(protection.Container, out var info)
        && !info.IsSystemUnassigned
        && snapshot.IsTableVisible(protection.Container, tableId);

    private static bool TryParseTableIdentifier(string rawId, out TableIdentifier identifier)
    {
        var parts = rawId.Trim().Split('.', 3, StringSplitOptions.TrimEntries);
        if (parts.Length == 3 && TableIdentifier.TryCreate(parts[0], parts[1], parts[2], out identifier))
        {
            return true;
        }

        identifier = default;
        return false;
    }
}
