namespace Autheris.Api.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Application.Governance.Rights;
using Autheris.Domain.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;

public sealed class EnterpriseClaimsTransformation : IClaimsTransformation
{
    internal const string TransformedMarkerClaimType = ClaimsNormalizer.LegacyMarkerClaimType;

    /// <summary>RR-L2-04: Issuer of the marker claim. Claims from tokens carry the token issuer and are never trusted.</summary>
    internal const string MarkerIssuer = ClaimsNormalizer.LegacyMarkerIssuer;

    private readonly IIdentityRealmRegistry? _realmRegistry;
    private readonly IRoleResolver? _roleResolver;
    private readonly IFunctionalRightMapper? _functionalRightMapper;
    private readonly ILogger<EnterpriseClaimsTransformation>? _logger;

    public EnterpriseClaimsTransformation(
        IIdentityRealmRegistry? realmRegistry = null,
        IRoleResolver? roleResolver = null,
        IFunctionalRightMapper? functionalRightMapper = null,
        ILogger<EnterpriseClaimsTransformation>? logger = null)
    {
        _logger = logger;
        _realmRegistry = realmRegistry;
        _roleResolver = roleResolver;
        _functionalRightMapper = functionalRightMapper;
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

            // GOV-08: without a user key there is nothing to resolve; an empty key would otherwise share one cache entry between
            // all such callers. The request is marked degraded and the PDP denies data access.
            if (string.IsNullOrWhiteSpace(userKey))
            {
                MarkDegraded(identity, realmId, "no user key");
                return ClaimsNormalizer.Normalize(principal);
            }

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
            if (rolesResolution.IsDegraded)
            {
                MarkDegraded(identity, realmId, "role provider unavailable");
            }

            foreach (var group in rolesResolution.Groups)
            {
                identity.AddClaim(new System.Security.Claims.Claim("autheris:group", group));
                identity.AddClaim(new System.Security.Claims.Claim(ClaimTypes.GroupSid, group));
            }

            foreach (var role in rolesResolution.Roles)
            {
                identity.AddClaim(new System.Security.Claims.Claim(ClaimTypes.Role, role));
            }

            // PERS-09: functional-right claims come only from FunctionalRightMapper (four-eyes for privileged rights, customer-realm
            // ceiling); a mapper failure yields no functional-right claims (fail closed).
            if (_functionalRightMapper != null)
            {
                var groupsAndRoles = rolesResolution.Groups.Concat(rolesResolution.Roles).Distinct().ToList();
                try
                {
                    var rights = await _functionalRightMapper.GetRightsForGroupsAndRolesAsync(tenantId, realmId, groupsAndRoles).ConfigureAwait(false);
                    foreach (var right in rights)
                    {
                        identity.AddClaim(new System.Security.Claims.Claim(RequireFunctionalRightHandler.FunctionalRightClaimType, right.Name));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogError(ex, "Functional right mapping failed for realm {RealmId}; no functional-right claims are issued (fail-closed).", realmId);
                }
            }
        }

        return ClaimsNormalizer.Normalize(principal);
    }

    private void MarkDegraded(ClaimsIdentity identity, string realmId, string reason)
    {
        // Any client-supplied value is replaced: the claim only ever restricts access.
        foreach (var existing in identity.FindAll(RoleResolutionClaims.ClaimType).ToList())
        {
            identity.RemoveClaim(existing);
        }

        identity.AddClaim(new Claim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded));
        Autheris.Domain.Diagnostics.GatewayDiagnostics.RoleResolutionDegradedCounter.Add(
            1, new KeyValuePair<string, object?>("realm", realmId));
        _logger?.LogWarning("Group and role resolution is degraded for realm {RealmId} ({Reason}); data access is denied for this request (fail-closed).", realmId, reason);
    }
}
