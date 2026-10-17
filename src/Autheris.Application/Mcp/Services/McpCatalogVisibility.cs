namespace Autheris.Application.Mcp.Services;

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
    /// <c>Gateway:Catalog:SharedTables</c>. Without options the mode is <see cref="TenancyMode.Multi"/> (confined view).
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
            options?.Tenancy.EffectiveMode ?? TenancyMode.Multi,
            options?.Catalog.SharedTables,
            ct).ConfigureAwait(false);
    }

    // An invalid tenant claim resolves to "no caller" in the resolver; the consent lookup never sees it.
    private static TenantId SafeTenantId(ClaimsPrincipal principal)
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
