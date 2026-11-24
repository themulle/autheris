namespace Autheris.Application.Catalog.External;

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
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
}
