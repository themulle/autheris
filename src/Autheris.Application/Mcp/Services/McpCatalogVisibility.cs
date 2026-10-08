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
    public static async Task<IReadOnlyList<TableMetadata>> VisibleTablesAsync(
        IReadOnlyList<TableMetadata> allTables,
        ClaimsPrincipal? principal,
        IConsentRepository? consentRepo,
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

        var roles = principal.GetUserRoles();
        bool isGlobalAdmin = roles.Contains("GovernanceAdmin") || roles.Contains("ClusterAdmin");
        if (isGlobalAdmin)
        {
            return allTables;
        }

        var groupSids = principal.GetGroupSids();
        var tenantId = principal.GetTenantId();
        var allSubjects = groupSids.Append(userSid!.Value).ToList();
        var activeConsents = await consentRepo.GetAllActiveConsentsForSubjectsAsync(
            allSubjects, roles, DateTimeOffset.UtcNow, tenantId, ct).ConfigureAwait(false);

        // MCP-1: same table and column visibility as the GraphQL catalog (Deny consents, column grants).
        return CatalogVisibility.FilterForSubject(allTables, activeConsents, tenantId);
    }
}
