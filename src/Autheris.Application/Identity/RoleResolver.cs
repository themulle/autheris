namespace Autheris.Application.Identity;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Caching;
using Autheris.Domain.Identity;

public sealed class RoleResolver : IRoleResolver
{
    private readonly IEnumerable<IRoleProvider> _providers;
    private readonly IRoleResolverCache _cache;
    private readonly IIdentityRealmRegistry? _realmRegistry;

    public RoleResolver(IEnumerable<IRoleProvider> providers, IRoleResolverCache cache, IIdentityRealmRegistry? realmRegistry = null)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _realmRegistry = realmRegistry;
    }

    public RoleResolver(IIdentityRealmRegistry realmRegistry, IEnumerable<IRoleProvider> providers, IRoleResolverCache cache)
        : this(providers, cache, realmRegistry)
    {
    }

    public Task<RoleResolution> ResolveRolesAsync(
        ResolvedIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var realm = _realmRegistry?.GetRealm(identity.RealmId) ?? _realmRegistry?.GetDefaultRealm();
        if (realm == null)
        {
            return Task.FromResult(RoleResolution.Empty);
        }
        return ResolveAsync(identity, realm, cancellationToken);
    }

    public async Task<RoleResolution> ResolveAsync(
        ResolvedIdentity identity,
        IdentityRealm realm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(realm);

        // 1. Check L1 / L2 Cache
        var cached = await _cache.GetAsync(realm.Id, identity.UserKey, cancellationToken).ConfigureAwait(false);
        if (cached != null)
        {
            return cached;
        }

        // 2. Select providers configured in realm.ResolverChain in order
        var chain = realm.ResolverChain ?? [];
        var activeProviders = new List<IRoleProvider>();
        foreach (var providerId in chain)
        {
            var p = _providers.FirstOrDefault(x => string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (p != null)
            {
                activeProviders.Add(p);
            }
        }

        var mergedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mergedAppRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mergedAttributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        bool isWorkforceRealm = realm.Kind == IdentityRealmKind.EntraWorkforce ||
                               string.Equals(realm.Id, "workforce-entra", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(realm.Id, "workforce-entra-legacy", StringComparison.OrdinalIgnoreCase);

        // GOV-08: a provider that failed (or threw) makes the whole resolution degraded: the groups are unknown, not empty.
        var degraded = false;

        foreach (var provider in activeProviders)
        {
            try
            {
                var result = await provider.GetRolesAsync(identity, realm, cancellationToken).ConfigureAwait(false);
                if (result is { Failed: true })
                {
                    degraded = true;
                }

                if (result != null)
                {
                    foreach (var g in result.Groups)
                    {
                        if (string.IsNullOrWhiteSpace(g)) continue;

                        // Namespace isolation: <realmId>|<groupId>
                        var namespacedGroup = $"{realm.Id}|{g}";
                        mergedGroups.Add(namespacedGroup);

                        // Backward compatibility: preserve un-namespaced legacy SIDs for default workforce realm
                        if (isWorkforceRealm)
                        {
                            mergedGroups.Add(g);
                        }
                    }

                    foreach (var role in result.AppRoles)
                    {
                        if (!string.IsNullOrWhiteSpace(role))
                        {
                            mergedAppRoles.Add(role);
                        }
                    }

                    foreach (var (k, v) in result.Attributes)
                    {
                        // First-wins attribute merge
                        if (!mergedAttributes.ContainsKey(k))
                        {
                            mergedAttributes[k] = v;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // SEC-SC-12 / GOV-08: a provider failure marks the resolution degraded (never cached, the PDP denies on it).
                degraded = true;
            }
        }

        var ttlMinutes = isWorkforceRealm ? 15 : 5;
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(ttlMinutes);

        var resolution = new RoleResolution(
            mergedGroups,
            mergedAppRoles,
            mergedAttributes,
            now,
            expiresAt,
            degraded ? "degraded" : "resolved"
        )
        {
            Status = degraded ? RoleResolutionStatus.Degraded : RoleResolutionStatus.Resolved
        };

        // GOV-08: an incomplete answer is never cached: the next request asks the providers again instead of serving "no groups".
        if (!degraded)
        {
            await _cache.SetAsync(realm.Id, identity.UserKey, resolution, cancellationToken).ConfigureAwait(false);
        }

        return resolution;
    }
}
