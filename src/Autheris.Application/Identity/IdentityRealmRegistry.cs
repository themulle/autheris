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
    private string? _defaultRealmId;

    public IdentityRealm? ResolveRealmForScheme(string? issuer, string? authenticationType)
    {
        if (!string.IsNullOrWhiteSpace(issuer))
        {
            return ResolveByIssuer(issuer);
        }

        if (!string.IsNullOrWhiteSpace(authenticationType))
        {
            IdentityRealmKind? targetKind = authenticationType switch
            {
                "Basic" or "BasicSession" => IdentityRealmKind.Basic,
                "ForwardAuth" => IdentityRealmKind.ForwardAuth,
                "Negotiate" or "Kerberos" => IdentityRealmKind.Kerberos,
                _ => null
            };

            if (targetKind.HasValue)
            {
                var matchingRealms = _realmsById.Values.Where(r => r.Kind == targetKind.Value).ToList();
                if (matchingRealms.Count == 1)
                {
                    return matchingRealms[0];
                }

                if (matchingRealms.Count > 1 && !string.IsNullOrWhiteSpace(_defaultRealmId))
                {
                    var matchedDefault = matchingRealms.FirstOrDefault(r => string.Equals(r.Id, _defaultRealmId, StringComparison.OrdinalIgnoreCase));
                    if (matchedDefault != null)
                    {
                        return matchedDefault;
                    }
                }

                return matchingRealms.FirstOrDefault();
            }
        }

        return GetDefaultRealm();
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

        IdentityRealm? bestRealm = null;
        int bestPrefixLength = -1;

        // Check if any registered realm matches by normalized issuer prefix; select the longest matching prefix deterministically.
        foreach (var r in _realmsById.Values)
        {
            foreach (var iss in r.Issuers)
            {
                if (string.IsNullOrWhiteSpace(iss))
                {
                    continue;
                }

                var normIss = NormalizeIssuer(iss);
                if (string.IsNullOrEmpty(normIss))
                {
                    continue;
                }

                if (string.Equals(normalized, normIss, StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith(normIss + "/", StringComparison.OrdinalIgnoreCase))
                {
                    if (normIss.Length > bestPrefixLength ||
                        (normIss.Length == bestPrefixLength &&
                         (bestRealm is null || string.Compare(r.Id, bestRealm.Id, StringComparison.OrdinalIgnoreCase) < 0)))
                    {
                        bestRealm = r;
                        bestPrefixLength = normIss.Length;
                    }
                }
            }
        }

        return bestRealm;
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
        if (!string.IsNullOrWhiteSpace(_defaultRealmId) && _realmsById.TryGetValue(_defaultRealmId, out var configuredDefault))
        {
            return configuredDefault;
        }

        if (_realmsById.TryGetValue("default", out var defaultRealm))
        {
            return defaultRealm;
        }

        if (_realmsById.TryGetValue("workforce-entra", out var entraRealm))
        {
            return entraRealm;
        }

        return null;
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
        _defaultRealmId = realmsOptions?.DefaultRealm;

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

                var hasSubjectRoles = config.SubjectRoles is { Count: > 0 };
                var allowedRoles = new HashSet<GatewayRole>();
                foreach (var r in config.AllowedGatewayRoles)
                {
                    if (hasSubjectRoles)
                    {
                        // A realm that maps workload subjects never drops a role name silently (numbers and unknown names fail startup).
                        allowedRoles.Add(SubjectRoleMapper.TryParseRoleName(r, out var strictRole)
                            ? strictRole
                            : throw new InvalidOperationException($"Realm '{realmId}': AllowedGatewayRoles contains an unknown or numeric role name."));
                    }
                    else if (SubjectRoleMapper.TryParseRoleName(r, out var role))
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
                )
                {
                    SubjectRoles = hasSubjectRoles ? BuildSubjectRoles(realmId, config.SubjectRoles) : null,
                    AllowPrivilegedSubjectRoles = config.AllowPrivilegedSubjectRoles,
                    ScimSettings = new RealmScimSettings(
                        AllowUpnCorrelation: config.Scim?.AllowUpnCorrelation ?? false,
                        RequireCorrelation: config.Scim?.RequireCorrelation ?? true,
                        MaxSyncAgeMinutes: config.Scim?.MaxSyncAgeMinutes ?? ScimRealmSecretOptions.DefaultMaxSyncAgeMinutes,
                        StaleSyncBehavior: config.Scim?.StaleSyncBehavior ?? ScimStaleSyncBehavior.Degrade)
                };

                RegisterRealm(realm);
            }
        }
    }

    private static Dictionary<string, IReadOnlyList<GatewayRole>> BuildSubjectRoles(string realmId, List<SubjectRoleMapping> entries)
    {
        var snapshot = new Dictionary<string, IReadOnlyList<GatewayRole>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var roles = new List<GatewayRole>(entry.Roles.Count);
            foreach (var name in entry.Roles)
            {
                roles.Add(SubjectRoleMapper.TryParseRoleName(name, out var role)
                    ? role
                    : throw new InvalidOperationException($"Realm '{realmId}': SubjectRoles contains an unknown or numeric role name."));
            }

            if (string.IsNullOrEmpty(entry.Subject) || !snapshot.TryAdd(entry.Subject, roles.AsReadOnly()))
            {
                throw new InvalidOperationException($"Realm '{realmId}': SubjectRoles contains an empty or duplicate subject.");
            }
        }

        return snapshot;
    }

    private static string NormalizeIssuer(string issuer) =>
        issuer.Trim().TrimEnd('/');
}
