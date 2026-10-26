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

    public void Initialize(GatewayOptions gatewayOptions, IdentityRealmsOptions? realmsOptions = null)
    {
        ArgumentNullException.ThrowIfNull(gatewayOptions);

        // 1. Register explicit realms from configuration if provided
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
                    Delegable: config.Delegable,
                    IsImplicitLegacy: false
                );

                RegisterRealm(realm);
            }
        }

        // 2. Synthesize implicit legacy realms (R-SC13-03)
        var auth = gatewayOptions.Authentication;
        if (auth.EntraId.Enabled && !_realmsById.ContainsKey("workforce-entra-legacy") && !_realmsById.ContainsKey("workforce-entra"))
        {
            var entraIssuers = new List<string>();
            if (!string.IsNullOrWhiteSpace(auth.EntraId.Authority))
            {
                entraIssuers.Add(auth.EntraId.Authority);
                var tenant = auth.EntraId.TenantId;
                if (!string.IsNullOrWhiteSpace(tenant))
                {
                    entraIssuers.Add($"https://login.microsoftonline.com/{tenant}/v2.0");
                    entraIssuers.Add($"https://sts.windows.net/{tenant}/");
                }
            }

            var audiences = new List<string>();
            if (!string.IsNullOrWhiteSpace(auth.EntraId.Audience)) audiences.Add(auth.EntraId.Audience);
            if (!string.IsNullOrWhiteSpace(auth.EntraId.ClientId)) audiences.Add(auth.EntraId.ClientId);

            var legacyEntraRealm = new IdentityRealm(
                Id: "workforce-entra-legacy",
                Kind: IdentityRealmKind.EntraWorkforce,
                Issuers: entraIssuers,
                MetadataAddress: auth.EntraId.MetadataAddress,
                Audiences: audiences,
                AllowedClientIds: null,
                UserKeyClaim: "oid",
                UpnClaim: "upn",
                TenantBinding: TenantBindingSpecification.Unbound(),
                GroupSource: RealmGroupSource.Token,
                ResolverChain: ["scim-store", "entra-app-roles"],
                Provisioning: RealmProvisioningMode.None,
                AllowedGatewayRoles: Enum.GetValues<GatewayRole>().ToHashSet(),
                Delegable: true,
                IsImplicitLegacy: true
            );
            RegisterRealm(legacyEntraRealm);
        }

        if (auth.Adfs.Enabled && !_realmsById.ContainsKey("corp-adfs-legacy"))
        {
            var adfsIssuers = new List<string>();
            if (!string.IsNullOrWhiteSpace(auth.Adfs.Authority))
            {
                adfsIssuers.Add(auth.Adfs.Authority);
            }

            var legacyAdfsRealm = new IdentityRealm(
                Id: "corp-adfs-legacy",
                Kind: IdentityRealmKind.Adfs,
                Issuers: adfsIssuers,
                MetadataAddress: auth.Adfs.MetadataAddress,
                Audiences: string.IsNullOrWhiteSpace(auth.Adfs.Audience) ? [] : [auth.Adfs.Audience],
                AllowedClientIds: null,
                UserKeyClaim: "sub",
                UpnClaim: "upn",
                TenantBinding: TenantBindingSpecification.Unbound(),
                GroupSource: RealmGroupSource.Token,
                ResolverChain: ["static-mapping"],
                Provisioning: RealmProvisioningMode.None,
                AllowedGatewayRoles: Enum.GetValues<GatewayRole>().ToHashSet(),
                Delegable: false,
                IsImplicitLegacy: true
            );
            RegisterRealm(legacyAdfsRealm);
        }

        if (auth.BasicAuth.Enabled && !_realmsById.ContainsKey("legacy-basic"))
        {
            var legacyBasicRealm = new IdentityRealm(
                Id: "legacy-basic",
                Kind: IdentityRealmKind.Basic,
                Issuers: ["autheris:basic"],
                MetadataAddress: null,
                Audiences: ["autheris:gateway"],
                AllowedClientIds: null,
                UserKeyClaim: "sub",
                UpnClaim: "name",
                TenantBinding: TenantBindingSpecification.Unbound(),
                GroupSource: RealmGroupSource.Token,
                ResolverChain: ["static-mapping"],
                Provisioning: RealmProvisioningMode.None,
                AllowedGatewayRoles: Enum.GetValues<GatewayRole>().ToHashSet(),
                Delegable: false,
                IsImplicitLegacy: true
            );
            RegisterRealm(legacyBasicRealm);
        }

        if (auth.ForwardAuth.Enabled && !_realmsById.ContainsKey("legacy-forwardauth"))
        {
            var legacyForwardAuthRealm = new IdentityRealm(
                Id: "legacy-forwardauth",
                Kind: IdentityRealmKind.ForwardAuth,
                Issuers: ["autheris:forwardauth"],
                MetadataAddress: null,
                Audiences: ["autheris:gateway"],
                AllowedClientIds: null,
                UserKeyClaim: "sub",
                UpnClaim: "name",
                TenantBinding: TenantBindingSpecification.Unbound(),
                GroupSource: RealmGroupSource.Token,
                ResolverChain: ["static-mapping"],
                Provisioning: RealmProvisioningMode.None,
                AllowedGatewayRoles: Enum.GetValues<GatewayRole>().ToHashSet(),
                Delegable: false,
                IsImplicitLegacy: true
            );
            RegisterRealm(legacyForwardAuthRealm);
        }

        if (auth.IsKerberosConfigured && !_realmsById.ContainsKey("legacy-kerberos"))
        {
            var legacyKerberosRealm = new IdentityRealm(
                Id: "legacy-kerberos",
                Kind: IdentityRealmKind.Kerberos,
                Issuers: ["autheris:kerberos"],
                MetadataAddress: null,
                Audiences: ["autheris:gateway"],
                AllowedClientIds: null,
                UserKeyClaim: "sub",
                UpnClaim: "upn",
                TenantBinding: TenantBindingSpecification.Unbound(),
                GroupSource: RealmGroupSource.Token,
                ResolverChain: ["static-mapping"],
                Provisioning: RealmProvisioningMode.None,
                AllowedGatewayRoles: Enum.GetValues<GatewayRole>().ToHashSet(),
                Delegable: false,
                IsImplicitLegacy: true
            );
            RegisterRealm(legacyKerberosRealm);
        }
    }

    private static string NormalizeIssuer(string issuer) =>
        issuer.Trim().TrimEnd('/');
}
