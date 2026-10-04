using System.Security.Claims;
using Autheris.Domain.Common;
using Microsoft.AspNetCore.Authentication;

namespace Autheris.Api.Security;

public sealed class EnterpriseClaimsTransformation : IClaimsTransformation
{
    internal const string TransformedMarkerClaimType = "__EnterpriseTransformed";

    /// <summary>RR-L2-04: Issuer of the marker claim. Claims from tokens carry the token issuer and are never trusted.</summary>
    internal const string MarkerIssuer = "Autheris:EnterpriseClaimsTransformation";

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity originalIdentity || !originalIdentity.IsAuthenticated)
        {
            return Task.FromResult(principal);
        }

        if (originalIdentity.HasClaim(c => c.Type == TransformedMarkerClaimType &&
                                           string.Equals(c.Issuer, MarkerIssuer, StringComparison.Ordinal)))
        {
            return Task.FromResult(principal);
        }

        // Clone the principal and identity to avoid mutating shared/cached ClaimsPrincipal (M-1)
        var clonedPrincipal = principal.Clone();
        var identity = (ClaimsIdentity)clonedPrincipal.Identity!;

        // RR-L2-04: a marker supplied by the token issuer must not suppress normalization.
        foreach (var forged in identity.FindAll(TransformedMarkerClaimType).ToList())
        {
            identity.RemoveClaim(forged);
        }

        // 1. Ensure ClaimTypes.PrimarySid is populated
        var existingPrimarySid = identity.FindFirst(ClaimTypes.PrimarySid);
        if (existingPrimarySid == null)
        {
            var userSid = principal.GetUserSid();
            if (userSid != null)
            {
                identity.AddClaim(new Claim(ClaimTypes.PrimarySid, userSid.Value.Value));
            }
        }

        // 2. Ensure ClaimTypes.GroupSid is populated from any provider group claims
        var existingGroups = identity.FindAll(ClaimTypes.GroupSid)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allGroupSids = principal.GetGroupSids();
        foreach (var groupSid in allGroupSids)
        {
            if (!existingGroups.Contains(groupSid.Value))
            {
                identity.AddClaim(new Claim(ClaimTypes.GroupSid, groupSid.Value));
            }
        }

        // 3. Ensure ClaimTypes.Role is populated from any provider role claims
        var existingRoles = identity.FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allRoles = principal.GetUserRoles();
        foreach (var role in allRoles)
        {
            if (!existingRoles.Contains(role))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
                existingRoles.Add(role);
            }

            // K-K10: Add canonical GatewayRole claim if role is an alias.
            // Security Guard: Only add bare canonical name if the role was NOT tenant-prefixed,
            // preventing tenant-scoped roles (e.g. "tenant-1:DataOwner") from escalating to global roles.
            if (!role.Contains(':') && Autheris.Domain.Security.GatewayRoleExtensions.TryParseRole(role, out var canonicalRole))
            {
                // RV-04: ClusterAdmin is the only cross-tenant role (ClusterAdminPolicy). It must be granted literally and
                // is never synthesized from tenant-admin aliases such as GatewayAdmin / PlatformAdmin.
                if (canonicalRole == Autheris.Domain.Security.GatewayRole.ClusterAdmin &&
                    !Autheris.Domain.Security.ClusterAdminPolicy.IsClusterAdminRole(role))
                {
                    continue;
                }

                var canonicalName = canonicalRole.ToString();
                if (!existingRoles.Contains(canonicalName))
                {
                    identity.AddClaim(new Claim(ClaimTypes.Role, canonicalName));
                    existingRoles.Add(canonicalName);
                }
            }
        }

        identity.AddClaim(new Claim(TransformedMarkerClaimType, "1", ClaimValueTypes.String, MarkerIssuer));
        return Task.FromResult(clonedPrincipal);
    }
}
