namespace Autheris.Api.Security;

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Microsoft.AspNetCore.Authentication;

public sealed class EnterpriseClaimsTransformation : IClaimsTransformation
{
    internal const string TransformedMarkerClaimType = ClaimsNormalizer.LegacyMarkerClaimType;

    /// <summary>RR-L2-04: Issuer of the marker claim. Claims from tokens carry the token issuer and are never trusted.</summary>
    internal const string MarkerIssuer = ClaimsNormalizer.LegacyMarkerIssuer;

    private readonly IIdentityRealmRegistry? _realmRegistry;
    private readonly IRoleResolver? _roleResolver;
    private readonly IFunctionalRightRepository? _functionalRightRepo;

    public EnterpriseClaimsTransformation(
        IIdentityRealmRegistry? realmRegistry = null,
        IRoleResolver? roleResolver = null,
        IFunctionalRightRepository? functionalRightRepo = null)
    {
        _realmRegistry = realmRegistry;
        _roleResolver = roleResolver;
        _functionalRightRepo = functionalRightRepo;
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return principal;
        }

        // 1. Strip any client-supplied autheris:realm claim (AppSec requirement K-1)
        var clientRealmClaims = identity.FindAll(c => string.Equals(c.Type, "autheris:realm", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var c in clientRealmClaims)
        {
            identity.RemoveClaim(c);
        }

        // 2. Resolve authoritative realm
        var issuer = identity.FindFirst("iss")?.Value;
        IdentityRealm? realm = null;
        if (_realmRegistry != null)
        {
            if (!string.IsNullOrWhiteSpace(issuer))
            {
                realm = _realmRegistry.ResolveRealmByIssuer(issuer);
            }
            realm ??= _realmRegistry.GetDefaultRealm();
        }

        var realmId = realm?.RealmId ?? "workforce-entra";
        identity.AddClaim(new Claim("autheris:realm", realmId));

        // 3. Resolve groups/roles if resolver mode
        if (realm != null && realm.GroupSource == RealmGroupSource.Resolver && _roleResolver != null)
        {
            var userKey = identity.FindFirst("oid")?.Value
                ?? identity.FindFirst("sub")?.Value
                ?? principal.GetUserSid()?.Value
                ?? string.Empty;

            var tenantId = principal.GetTenantId();
            var upn = identity.FindFirst("upn")?.Value ?? identity.FindFirst(ClaimTypes.Upn)?.Value;
            var name = identity.FindFirst("name")?.Value ?? identity.FindFirst(ClaimTypes.Name)?.Value;
            var email = identity.FindFirst("email")?.Value ?? identity.FindFirst(ClaimTypes.Email)?.Value;

            var resolvedIdentity = new ResolvedIdentity(
                userKey,
                upn,
                name,
                email,
                tenantId,
                realmId,
                principal.Claims.ToList()
            );

            var rolesResolution = await _roleResolver.ResolveRolesAsync(resolvedIdentity).ConfigureAwait(false);

            foreach (var group in rolesResolution.Groups)
            {
                identity.AddClaim(new System.Security.Claims.Claim("autheris:group", group));
                identity.AddClaim(new System.Security.Claims.Claim(ClaimTypes.GroupSid, group));
            }

            foreach (var role in rolesResolution.Roles)
            {
                identity.AddClaim(new System.Security.Claims.Claim(ClaimTypes.Role, role));
            }

            // Map functional rights for the groups and roles
            if (_functionalRightRepo != null)
            {
                var groupsAndRoles = rolesResolution.Groups.Concat(rolesResolution.Roles).Distinct().ToList();
                var mappings = await _functionalRightRepo.GetApprovedMappingsForGroupsAndRolesAsync(tenantId, realmId, groupsAndRoles).ConfigureAwait(false);
                foreach (var m in mappings)
                {
                    identity.AddClaim(new System.Security.Claims.Claim(RequireFunctionalRightHandler.FunctionalRightClaimType, m.FunctionalRight.Name));
                }
            }
        }

        return ClaimsNormalizer.Normalize(principal);
    }
}
