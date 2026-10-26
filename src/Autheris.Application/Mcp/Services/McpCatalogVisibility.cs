namespace Autheris.Application.Mcp.Services;

using Autheris.Application.Tenancy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// Catalog tables and columns an MCP caller may discover. Shared by the MCP resources and the dataset tools.
/// </summary>
internal static class McpCatalogVisibility
{
    /// <summary>
    /// SEC M-28: anonymous callers (no principal, no SID, the synthetic MCP anonymous SID) see no tables at all;
    /// only explicit governance roles see the whole catalog; everybody else sees only tables with an active
    /// Allow consent within the caller's own tenant.
    /// </summary>
    public static Task<IReadOnlyList<TableMetadata>> VisibleTablesAsync(
        IReadOnlyList<TableMetadata> allTables,
        ClaimsPrincipal? principal,
        IConsentRepository? consentRepo,
        CancellationToken ct)
        => VisibleTablesAsync(allTables, principal, consentRepo, options: null, ct);

    /// <summary>
    /// Same rule with the deployment tenancy setting: <paramref name="options"/> supplies <c>Gateway:Tenancy:Mode</c> and
    /// <c>Gateway:Catalog:SharedTables</c>. Without options there is no catalog owner (confined view).
    /// </summary>
    public static async Task<IReadOnlyList<TableMetadata>> VisibleTablesAsync(
        IReadOnlyList<TableMetadata> allTables,
        ClaimsPrincipal? principal,
        IConsentRepository? consentRepo,
        GatewayOptions? options,
        CancellationToken ct)
    {
        if (principal == null || consentRepo == null)
        {
            return Array.Empty<TableMetadata>();
        }

        var userSid = principal.GetUserSid();
        bool isAnonymous = userSid == null || string.Equals(userSid.Value.Value, "ANONYMOUS_MCP_CLIENT", StringComparison.OrdinalIgnoreCase);
        if (isAnonymous)
        {
            return Array.Empty<TableMetadata>();
        }

        // MCP-1: same table and column visibility as the GraphQL catalog (admins, Deny consents, column grants).
        return await CatalogVisibility.VisibleTablesAsync(
            allTables,
            principal,
            SafeTenantId(principal),
            consentRepo,
            contract: null,
            TenantResolver.EffectiveDefaultOf(options?.Tenancy),
            options?.Catalog.SharedTables,
            CatalogSurface.DataAccess,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Consent, tier and column visibility (<see cref="VisibleTablesAsync(IReadOnlyList{TableMetadata}, ClaimsPrincipal?, IConsentRepository?, GatewayOptions?, CancellationToken)"/>)
    /// intersected with the catalog visibility service, which adds the ReBAC step (MCP-14). A table must pass both; without a
    /// service the first rule alone applies (the service is always registered in a running gateway). The intersection keeps the
    /// column filtering of the first rule and can only remove tables.
    /// </summary>
    public static async Task<IReadOnlyList<TableMetadata>> VisibleTablesAsync(
        IReadOnlyList<TableMetadata> allTables,
        ClaimsPrincipal? principal,
        IConsentRepository? consentRepo,
        GatewayOptions? options,
        Autheris.Application.Catalog.Interfaces.ICatalogVisibilityService? catalogVisibility,
        CancellationToken ct)
    {
        var tables = await VisibleTablesAsync(allTables, principal, consentRepo, options, ct).ConfigureAwait(false);
        if (catalogVisibility is null || tables.Count == 0)
        {
            return tables;
        }

        if (principal is null)
        {
            return Array.Empty<TableMetadata>();
        }

        var caller = new Autheris.Application.Catalog.Interfaces.CatalogCaller(
            principal, SafeTenantId(principal), Autheris.Application.Catalog.Interfaces.CatalogChannel.Mcp);
        var visible = await catalogVisibility.FilterVisibleAsync(caller, tables.Select(t => t.Identifier).ToList(), ct).ConfigureAwait(false);
        return tables.Where(t => visible.Contains(t.Identifier)).ToList();
    }

    // An invalid tenant claim resolves to "no caller" in the resolver; the consent lookup never sees it.
    internal static TenantId SafeTenantId(ClaimsPrincipal principal)
    {
        try
        {
            return principal.GetTenantId();
        }
        catch (System.Security.SecurityException)
        {
            return TenantId.LegacySingleTenant;
        }
    }
}
