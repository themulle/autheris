namespace Autheris.Api.Security;

using System;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Autheris.Domain.Common;
using Autheris.Domain.Security;

/// <summary>
/// ADR-017 / K-K10: Canonical Normalizer for claims at the Gateway ingress edge.
/// Unifies Active Directory Windows-SIDs, OIDC/OAuth2 identity claims, and mTLS client certificates
/// into strongly-typed, canonical claims (ClaimTypes.PrimarySid, ClaimTypes.GroupSid, ClaimTypes.Role,
/// ClaimTypes.NameIdentifier, tenant_id).
/// </summary>
public static class ClaimsNormalizer
{
    public const string NormalizedMarkerClaimType = "__ClaimsNormalized";
    public const string LegacyMarkerClaimType = "__EnterpriseTransformed";

    public const string MiddlewareMarkerIssuer = "Autheris:ClaimsNormalizationMiddleware";
    public const string LegacyMarkerIssuer = "Autheris:EnterpriseClaimsTransformation";

    public static ClaimsPrincipal Normalize(ClaimsPrincipal principal, X509Certificate2? clientCertificate = null)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var originalIdentity = principal.Identity as ClaimsIdentity;
        bool isAuthenticated = originalIdentity?.IsAuthenticated == true;

        // If unauthenticated and no client certificate is present, return unchanged
        if (!isAuthenticated && clientCertificate == null)
        {
            return principal;
        }

        // Check if already normalized with trusted issuer
        if (originalIdentity != null &&
            (originalIdentity.HasClaim(c => c.Type == NormalizedMarkerClaimType && string.Equals(c.Issuer, MiddlewareMarkerIssuer, StringComparison.Ordinal)) ||
             originalIdentity.HasClaim(c => c.Type == LegacyMarkerClaimType && string.Equals(c.Issuer, LegacyMarkerIssuer, StringComparison.Ordinal))))
        {
            return principal;
        }

        // Clone the principal and identity to avoid mutating shared/cached ClaimsPrincipal (M-1)
        ClaimsPrincipal clonedPrincipal;
        ClaimsIdentity identity;

        if (originalIdentity?.IsAuthenticated == true)
        {
            clonedPrincipal = principal.Clone();
            identity = (ClaimsIdentity)clonedPrincipal.Identity!;
        }
        else if (clientCertificate != null)
        {
            var thumbprint = clientCertificate.Thumbprint ?? clientCertificate.GetCertHashString();
            var certName = clientCertificate.GetNameInfo(X509NameType.SimpleName, false) ?? clientCertificate.Subject;
            identity = new ClaimsIdentity("Certificate", ClaimTypes.Name, ClaimTypes.Role);
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, $"cert:{thumbprint}"));
            identity.AddClaim(new Claim(ClaimTypes.PrimarySid, $"cert:{thumbprint}"));
            identity.AddClaim(new Claim(ClaimTypes.Name, certName));
            identity.AddClaim(new Claim("x509_thumbprint", thumbprint));
            identity.AddClaim(new Claim("client_cert_thumbprint", thumbprint));
            identity.AddClaim(new Claim("x509_subject", clientCertificate.Subject));
            identity.AddClaim(new Claim("x509_issuer", clientCertificate.Issuer));
            clonedPrincipal = new ClaimsPrincipal(identity);
        }
        else
        {
            return principal;
        }

        // RR-L2-04: Strip any forged marker claims supplied by untrusted issuers
        foreach (var forged in identity.FindAll(c => c.Type is NormalizedMarkerClaimType or LegacyMarkerClaimType).ToList())
        {
            identity.RemoveClaim(forged);
        }

        // 1. Ensure ClaimTypes.PrimarySid & ClaimTypes.NameIdentifier are populated (Windows SID / AD / OIDC)
        var existingPrimarySid = identity.FindFirst(ClaimTypes.PrimarySid);
        if (existingPrimarySid == null)
        {
            var userSid = clonedPrincipal.GetUserSid();
            if (userSid != null)
            {
                identity.AddClaim(new Claim(ClaimTypes.PrimarySid, userSid.Value.Value));
            }
        }

        if (identity.FindFirst(ClaimTypes.NameIdentifier) == null)
        {
            var subVal = identity.FindFirst("sub")?.Value
                ?? identity.FindFirst("oid")?.Value
                ?? identity.FindFirst(ClaimTypes.PrimarySid)?.Value;
            if (!string.IsNullOrWhiteSpace(subVal))
            {
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subVal));
            }
        }

        // 2. Ensure Client Certificate claims are populated if cert is present on connection
        if (clientCertificate != null)
        {
            var thumbprint = clientCertificate.Thumbprint ?? clientCertificate.GetCertHashString();
            if (identity.FindFirst("x509_thumbprint") == null)
            {
                identity.AddClaim(new Claim("x509_thumbprint", thumbprint));
            }
            if (identity.FindFirst("client_cert_thumbprint") == null)
            {
                identity.AddClaim(new Claim("client_cert_thumbprint", thumbprint));
            }
            if (identity.FindFirst("x509_subject") == null && !string.IsNullOrWhiteSpace(clientCertificate.Subject))
            {
                identity.AddClaim(new Claim("x509_subject", clientCertificate.Subject));
            }
        }

        // 3. Ensure canonical tenant_id claim is populated
        if (identity.FindFirst("tenant_id") == null)
        {
            var resolvedTenant = clonedPrincipal.GetTenantId();
            if (resolvedTenant != TenantId.LegacySingleTenant)
            {
                identity.AddClaim(new Claim("tenant_id", resolvedTenant.Value));
            }
        }

        // 4. Ensure ClaimTypes.GroupSid is populated from any provider group claims (AD / Kerberos / OIDC)
        var existingGroups = identity.FindAll(ClaimTypes.GroupSid)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allGroupSids = clonedPrincipal.GetGroupSids();
        foreach (var groupSid in allGroupSids)
        {
            if (!existingGroups.Contains(groupSid.Value))
            {
                identity.AddClaim(new Claim(ClaimTypes.GroupSid, groupSid.Value));
                existingGroups.Add(groupSid.Value);
            }
        }

        // 5. Ensure ClaimTypes.Role is populated from any provider role claims and canonical GatewayRoles
        var existingRoles = identity.FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allRoles = clonedPrincipal.GetUserRoles();
        foreach (var role in allRoles)
        {
            if (!existingRoles.Contains(role))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
                existingRoles.Add(role);
            }

            var colonIdx = role.IndexOf(':');
            if (colonIdx > 0)
            {
                // Tenant-scoped role: "tenant-1:DataOwner"
                var roleTenant = role[..colonIdx];
                var roleName = role[(colonIdx + 1)..];

                if (GatewayRoleExtensions.TryParseRole(roleName, out var parsedRole))
                {
                    // Security Guard: ClusterAdmin and GovernanceAdmin are strictly global platform roles
                    if (parsedRole is not (GatewayRole.ClusterAdmin or GatewayRole.GovernanceAdmin))
                    {
                        var canonicalTenantRole = $"{roleTenant}:{parsedRole}";
                        if (!existingRoles.Contains(canonicalTenantRole))
                        {
                            identity.AddClaim(new Claim(ClaimTypes.Role, canonicalTenantRole));
                            existingRoles.Add(canonicalTenantRole);
                        }
                    }
                }
            }
            else
            {
                // Global role
                if (GatewayRoleExtensions.TryParseRole(role, out var canonicalRole))
                {
                    // RV-04: ClusterAdmin is the only cross-tenant role (ClusterAdminPolicy). It must be granted literally and
                    // is never synthesized from tenant-admin aliases such as GatewayAdmin / PlatformAdmin.
                    if (canonicalRole == GatewayRole.ClusterAdmin && !ClusterAdminPolicy.IsClusterAdminRole(role))
                    {
                        continue;
                    }

                    var canonicalName = canonicalRole.ToString();
                    if (!existingRoles.Contains(canonicalName))
                    {
                        identity.AddClaim(new Claim(ClaimTypes.Role, canonicalName));
                        existingRoles.Add(canonicalName);
                    }

                    if (!identity.HasClaim(c => c.Type == "gateway_role" && string.Equals(c.Value, canonicalName, StringComparison.OrdinalIgnoreCase)))
                    {
                        identity.AddClaim(new Claim("gateway_role", canonicalName));
                    }
                }
            }
        }

        // 6. Attach trusted marker claims
        identity.AddClaim(new Claim(NormalizedMarkerClaimType, "1", ClaimValueTypes.String, MiddlewareMarkerIssuer));
        identity.AddClaim(new Claim(LegacyMarkerClaimType, "1", ClaimValueTypes.String, LegacyMarkerIssuer));

        return clonedPrincipal;
    }
}
