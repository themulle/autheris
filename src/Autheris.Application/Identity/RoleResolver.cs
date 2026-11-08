namespace Autheris.Application.Identity;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

public sealed class RoleResolver : IRoleResolver
{
    private readonly IEnumerable<IRoleProvider> _providers;
    private readonly IRoleResolverCache _cache;
    private readonly IIdentityRealmRegistry? _realmRegistry;
    private readonly int _ttlMinutes;
    private readonly int _maxGroups;

    public RoleResolver(
        IEnumerable<IRoleProvider> providers,
        IRoleResolverCache cache,
        IIdentityRealmRegistry? realmRegistry = null,
        IOptions<GatewayOptions>? options = null)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _realmRegistry = realmRegistry;
        var auth = options?.Value.Authentication;
        _ttlMinutes = auth?.GroupCacheTtlMinutes is >= 1 and <= 60 ? auth.GroupCacheTtlMinutes : 5;
        _maxGroups = auth?.MaxGroupSidsPerCaller is >= 1 and <= 4096 ? auth.MaxGroupSidsPerCaller : 1024;
    }

    public RoleResolver(
        IIdentityRealmRegistry realmRegistry,
        IEnumerable<IRoleProvider> providers,
        IRoleResolverCache cache,
        IOptions<GatewayOptions>? options = null)
        : this(providers, cache, realmRegistry, options)
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
            // No realm to resolve against: unknown groups, never "no groups".
            return Task.FromResult(RoleResolution.EmptyWithExpiry(DateTimeOffset.MinValue) with
            {
                Status = RoleResolutionStatus.Degraded
            });
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
        // An empty chain on a Resolver realm or a chain naming a provider that is not registered cannot answer: degraded, never "no groups".
        var degraded = realm.GroupSource == RealmGroupSource.Resolver && chain.Count == 0;
        foreach (var providerId in chain)
        {
            var p = _providers.FirstOrDefault(x => string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (p != null)
            {
                activeProviders.Add(p);
            }
            else
            {
                degraded = true;
            }
        }

        var mergedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mergedAppRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mergedAttributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // GOV-08: a provider that failed (or threw) makes the whole resolution degraded: the groups are unknown, not empty.
        var rejected = 0;

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

                        // Canonical SIDs are globally unique, so no realm prefix: isolation is the cache key (realm, user) and the
                        // provider chain of the realm. A non-canonical value is dropped and the resolution is degraded.
                        if (!SidFormat.IsCanonical(g.Trim()))
                        {
                            rejected++;
                            degraded = true;
                            continue;
                        }

                        mergedGroups.Add(g.Trim());
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
                // 12 / GOV-08: a provider failure marks the resolution degraded (never cached, the PDP denies on it).
                degraded = true;
            }
        }

        IdentityGroupMetrics.RecordRejected(IdentityGroupMetrics.SourceResolver, rejected);
        if (mergedGroups.Count > _maxGroups)
        {
            // Never truncated: a dropped group could carry a deny.
            mergedGroups.Clear();
            degraded = true;
        }

        var ttlMinutes = _ttlMinutes;
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
