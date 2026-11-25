namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Model;

/// <summary>
/// Helpers for building and validating <see cref="System.Security.Claims.ClaimsPrincipal"/> identities from
/// <see cref="McpSessionContext"/> callers.
/// </summary>
public static class McpProtocolHandler
{
    /// <summary>Claim that marks a principal as an application identity (no authenticated user in the session).</summary>
    public const string IdentityKindClaimType = "autheris_identity_kind";

    /// <summary>Value of <see cref="IdentityKindClaimType"/> for an app-only principal.</summary>
    public const string ApplicationIdentityKind = "application";

    /// <summary>
    /// Raw role and group claim types of a token (short JWT names and the long WS-Federation names that <c>GetUserRoles</c> /
    /// <c>GetGroupSids</c> also read). The session carries the vetted roles and group SIDs separately; letting the raw claims through
    /// as additional claims would re-add groups to an app-only principal and untrusted roles.
    /// </summary>
    private static readonly HashSet<string> RoleAndGroupClaimTypes = new(
        Autheris.Domain.Identity.GroupResolutionClaims.InboundGroupClaimTypes.Concat(
        [
            "roles",
            "role",
            "wids",
            System.Security.Claims.ClaimTypes.Role,
            "autheris:group",
            Autheris.Domain.Identity.GroupResolutionClaims.MarkerClaimType,
            Autheris.Domain.Identity.GroupResolutionClaims.StatusClaimType,
            Autheris.Domain.Identity.GroupResolutionClaims.ExpiresAtClaimType
        ]),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a token claim may be carried into the MCP session principal as an additional claim. Role and group claims never are
    /// (they come only from the vetted session roles and group SIDs), nor are the identity markers that only the gateway sets.
    /// </summary>
    public static bool IsAllowedAdditionalClaimType(string? claimType) =>
        !string.IsNullOrWhiteSpace(claimType) &&
        !RoleAndGroupClaimTypes.Contains(claimType) &&
        !string.Equals(claimType, IdentityKindClaimType, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(claimType, "idtyp", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(claimType, "autheris:subject_kind", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(claimType, Autheris.Domain.Security.TokenAccessScope.ClaimType, StringComparison.Ordinal) &&
        !string.Equals(claimType, "client_id", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(claimType, "azp", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(claimType, "appid", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The principal of an MCP caller as the catalog, resource and tool services expect it (SID, tenant, roles, groups, claims, read-only
    /// marker). The subject is the authenticated user of the session. A session without a user SID (app-only token) keeps the
    /// application identity, is marked with <see cref="IdentityKindClaimType"/> and carries no group SIDs, so no user-level consent applies.
    /// </summary>
    public static System.Security.Claims.ClaimsPrincipal? BuildPrincipalFromSession(McpSessionContext? session, TimeProvider? timeProvider = null)
    {
        if (session == null) return null;
        var identity = new System.Security.Claims.ClaimsIdentity("MCP");
        var isAppOnly = session.IsAppOnly || string.IsNullOrWhiteSpace(session.UserSid);
        var sid = !isAppOnly ? session.UserSid : session.ServicePrincipalId;
        if (!string.IsNullOrWhiteSpace(sid))
        {
            identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, sid));
            identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, sid));
        }
        if (isAppOnly)
        {
            identity.AddClaim(new System.Security.Claims.Claim(IdentityKindClaimType, ApplicationIdentityKind));
            identity.AddClaim(new System.Security.Claims.Claim("idtyp", "app"));
        }
        if (!string.IsNullOrWhiteSpace(session.TenantId))
        {
            identity.AddClaim(new System.Security.Claims.Claim("tenant_id", session.TenantId));
        }
        var emitClientId = !string.IsNullOrWhiteSpace(session.ServicePrincipalId)
            && (isAppOnly || !string.Equals(session.ServicePrincipalId, sid, StringComparison.Ordinal));
        if (emitClientId)
        {
            identity.AddClaim(new System.Security.Claims.Claim("client_id", session.ServicePrincipalId!));
            identity.AddClaim(new System.Security.Claims.Claim("azp", session.ServicePrincipalId!));
        }
        if (session.Roles != null)
        {
            foreach (var r in session.Roles)
            {
                identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, r));
            }
        }
        // The session carries the group snapshot of one pipeline resolution. After its expiry (or when it was degraded) no group claim is
        // minted from the stored strings; the principal is then marked Degraded and data access is denied until the next request
        // re-resolves through the pipeline. The sliding session lifetime never extends group validity.
        var groupsStale = session.GroupsDegraded || (session.GroupsExpireAt is { } groupsExpiry && groupsExpiry <= (timeProvider ?? TimeProvider.System).GetUtcNow());
        if (!isAppOnly && !groupsStale && session.GroupSids != null)
        {
            foreach (var g in session.GroupSids)
            {
                identity.AddClaim(Autheris.Domain.Common.IdentityClaimIssuers.CreateGroupSidClaim(g));
            }
        }

        identity.AddClaim(Autheris.Domain.Identity.GroupResolutionClaims.CreateMarker());
        identity.AddClaim(Autheris.Domain.Identity.GroupResolutionClaims.CreateStatus(
            groupsStale ? Autheris.Domain.Identity.GroupResolutionStatus.Degraded : Autheris.Domain.Identity.GroupResolutionStatus.Complete));
        if (session.GroupsExpireAt is { } expiresAt)
        {
            identity.AddClaim(Autheris.Domain.Identity.GroupResolutionClaims.CreateExpiresAt(expiresAt));
        }
        if (session.AdditionalClaims != null)
        {
            foreach (var (k, v) in session.AdditionalClaims)
            {
                // The identity markers are set by the gateway only; a token claim must not spoof or clear them. Raw role and group
                // claims must not re-add what the session deliberately left out (app-only sessions carry no groups).
                if (!IsAllowedAdditionalClaimType(k))
                {
                    continue;
                }
                identity.AddClaim(new System.Security.Claims.Claim(k, v));
            }
        }
        if (session.IsReadOnly)
        {
            Autheris.Domain.Security.TokenAccessScope.MarkReadOnly(identity);
        }
        return new System.Security.Claims.ClaimsPrincipal(identity);
    }
}
