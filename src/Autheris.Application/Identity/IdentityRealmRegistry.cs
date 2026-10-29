namespace Autheris.Application.Identity;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Security;
using Autheris.Domain.Security;

public sealed class IdentityRealmRegistry : IIdentityRealmRegistry
{
    private readonly ConcurrentDictionary<string, IdentityRealm> _realmsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IdentityRealm> _realmsByIssuer = new(StringComparer.OrdinalIgnoreCase);

    public IdentityRealmRegistry()
    {
        Sid.RealmLookup = GetRealm;
    }

    public IdentityRealm? ResolveByIssuer(string issuer)
    {
        if (string.IsNullOrWhiteSpace(issuer))
        {
            return null;
        }

        var normalized = NormalizeIssuer(issuer);
        if (_realmsByIssuer.TryGetValue(normalized, out var realm))
        {
            return realm;
        }

        // Check if any registered realm matches with prefix / authority or prefix trailing slash
        foreach (var r in _realmsById.Values)
        {
            foreach (var iss in r.Issuers)
            {
                var normIss = NormalizeIssuer(iss);
                if (string.Equals(normalized, normIss, StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith(normIss.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return r;
                }
            }
        }

        return null;
    }

    public IdentityRealm? GetRealm(string realmId)
    {
        if (string.IsNullOrWhiteSpace(realmId))
        {
            return null;
        }

        return _realmsById.TryGetValue(realmId, out var realm) ? realm : null;
    }

    public bool TryGetRealm(string realmId, out IdentityRealm? realm)
    {
        return _realmsById.TryGetValue(realmId, out realm);
    }

    public IdentityRealm? GetDefaultRealm()
    {
        if (_realmsById.TryGetValue("default", out var defaultRealm))
        {
            return defaultRealm;
        }

        if (_realmsById.TryGetValue("workforce-entra", out var entraRealm))
        {
            return entraRealm;
        }

        return _realmsById.Values.FirstOrDefault();
    }

    public IReadOnlyList<IdentityRealm> GetAllRealms() => _realmsById.Values.ToList();

    public void RegisterRealm(IdentityRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentException.ThrowIfNullOrWhiteSpace(realm.Id);

        _realmsById[realm.Id] = realm;
        foreach (var issuer in realm.Issuers)
        {
            if (!string.IsNullOrWhiteSpace(issuer))
            {
                _realmsByIssuer[NormalizeIssuer(issuer)] = realm;
            }
        }
    }

    /// <summary>
    /// Registers the explicit realms of <c>Authentication:Realms</c> (DT-A-11). No realm is ever synthesized from the enabled schemes;
    /// a scheme without a realm fails startup (<see cref="Validators.IdentityRealmSchemeValidator"/>).
    /// </summary>
    public void Initialize(IdentityRealmsOptions? realmsOptions)
    {
        // Register explicit realms from configuration if provided
        if (realmsOptions?.Realms != null)
        {
            foreach (var (realmId, config) in realmsOptions.Realms)
            {
                var binding = config.FixedTenantId != null
                    ? TenantBindingSpecification.Fixed(new TenantId(config.FixedTenantId))
                    : (config.TenantClaimName != null && config.TenantClaimMapping != null
                        ? TenantBindingSpecification.ClaimMapped(config.TenantClaimName, config.TenantClaimMapping.ToDictionary(k => k.Key, v => new TenantId(v.Value)))
                        : TenantBindingSpecification.Unbound());

                var allowedRoles = new HashSet<GatewayRole>();
                foreach (var r in config.AllowedGatewayRoles)
                {
                    if (Enum.TryParse<GatewayRole>(r, true, out var role))
                    {
                        allowedRoles.Add(role);
                    }
                }

                var realm = new IdentityRealm(
                    Id: realmId,
                    Kind: config.Kind,
                    Issuers: config.Issuers,
                    MetadataAddress: config.MetadataAddress,
                    Audiences: config.Audiences,
                    AllowedClientIds: config.AllowedClientIds,
                    UserKeyClaim: config.UserKeyClaim,
                    UpnClaim: config.UpnClaim,
                    TenantBinding: binding,
                    GroupSource: config.GroupSource,
                    ResolverChain: config.ResolverChain,
                    Provisioning: config.Provisioning,
                    AllowedGatewayRoles: allowedRoles,
                    Delegable: config.Delegable
                );

                RegisterRealm(realm);
            }
        }
    }

    private static string NormalizeIssuer(string issuer) =>
        issuer.Trim().TrimEnd('/');
}
