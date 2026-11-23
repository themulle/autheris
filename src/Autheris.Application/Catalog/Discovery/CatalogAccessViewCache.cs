namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;

/// <summary>
/// Issue #1044: the caller's catalog access view (grant candidates and allow-only Discover decisions of the caller's scoped catalog),
/// kept across requests so that list pages, facets and searches with <c>accessStatus</c> cost statements in proportion to the page.
/// <list type="bullet">
/// <item>Labels only: the view selects discovery set membership; page items and data requests are decided per request.</item>
/// <item>The key is the tenant, the user, the channel and a hash over the caller's full claim set (<see cref="DiscoveryService"/>).</item>
/// <item>An entry carries the stamp read before its load: the invalidation generation, the catalog version and the virtual filter
/// generation. A moved catalog or filter generation drops it; a moved invalidation generation re-decides the tables the invalidation log
/// names, or drops the entry when the log cannot prove the set (compare-and-set, RR-L4-06).</item>
/// <item>No stamp (degraded invalidation signal, authorization hooks) or an entry past its consent-derived
/// <see cref="AccessViewEntry.NotAfter"/> or its TTL means no read and no write.</item>
/// <item>Bounded by a byte size limit with compaction of the least recently used entries, and a per-entry cap.</item>
/// </list>
/// </summary>
public sealed class CatalogAccessViewCache : IDisposable
{
    private const int GateStripes = 64;

    private readonly MemoryCache _cache;
    private readonly SemaphoreSlim[] _gates;
    private readonly IEpochValidationService? _epochs;
    private readonly ICatalogVersionProvider? _catalogVersion;
    private readonly IVirtualFilterSnapshotProvider? _virtualFilters;
    private readonly Func<bool> _authorizationHooksActive;
    private readonly long _maxEntryBytes;

    public CatalogAccessViewCache(
        IOptions<CatalogDiscoveryOptions>? options,
        IEpochValidationService? epochs,
        ICatalogVersionProvider? catalogVersion,
        IVirtualFilterSnapshotProvider? virtualFilters,
        TimeProvider? timeProvider = null,
        IOptions<Autheris.Domain.Options.GatewayOptions>? gatewayOptions = null,
        Autheris.Application.Plugins.Auth.IAuthPluginHost? authPlugins = null)
    {
        // Authorization plugin hooks may decide on per-request inputs (client address) and audit every call: no cached view with hooks.
        var auth = gatewayOptions?.Value?.Plugins?.Auth;
        _authorizationHooksActive = () => auth is { Enabled: true, AllowAuthorizationHooks: true } && authPlugins is { HasAuthorizationHooks: true };
        var settings = options?.Value?.Discovery?.AccessViewCache ?? new CatalogAccessViewCacheSettings();
        Time = timeProvider ?? TimeProvider.System;
        Ttl = TimeSpan.FromSeconds(Math.Clamp(settings.TtlSeconds, 1, CatalogAccessViewCacheSettings.MaxTtlSeconds));
        _maxEntryBytes = Math.Max(1, settings.MaxEntryMegabytes) * 1024L * 1024L;
        _epochs = epochs;
        _catalogVersion = catalogVersion;
        _virtualFilters = virtualFilters;
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = Math.Max(1, settings.SizeLimitMegabytes) * 1024L * 1024L,
            CompactionPercentage = 0.25,
            TrackStatistics = true,
            Clock = new SystemClockAdapter(Time)
        });
        _gates = new SemaphoreSlim[GateStripes];
        for (var i = 0; i < GateStripes; i++)
        {
            _gates[i] = new SemaphoreSlim(1, 1);
        }
    }

    public TimeSpan Ttl { get; }

    internal TimeProvider Time { get; }

    /// <summary>Estimated bytes of all cached views.</summary>
    public long CurrentEstimatedSize => _cache.GetCurrentStatistics()?.CurrentEstimatedSize ?? 0;

    /// <summary>
    /// The current stamp, read before a view is loaded or used; null when the view must be neither read nor written (no
    /// invalidation signal or a degraded one, no catalog version source, active authorization plugin hooks).
    /// </summary>
    internal async ValueTask<AccessViewStamp?> ReadStampAsync(CancellationToken ct)
    {
        if (_authorizationHooksActive() || _epochs?.InvalidationGeneration is not { } generation || _catalogVersion is null)
        {
            return null;
        }

        // The local generation is read first and the cluster sequence last: a concurrent invalidation can only make the stamp look
        // older than the state (a rebuild), never newer.
        var catalog = await _catalogVersion.GetCatalogVersionAsync(ct).ConfigureAwait(false);
        var filters = _virtualFilters is null ? 0 : (await _virtualFilters.GetAsync(ct).ConfigureAwait(false)).Generation;
        if (await _epochs.GetClusterInvalidationSequenceAsync(ct).ConfigureAwait(false) is not { } cluster)
        {
            return null;
        }

        return new AccessViewStamp(generation, catalog, filters, cluster);
    }

    /// <summary>
    /// The lower-case table keys whose decisions made at <paramref name="loaded"/> are stale at <paramref name="current"/> (empty when
    /// none), or null when a value of <paramref name="loaded"/> must not be used: the catalog or the virtual filters changed, the local
    /// log cannot prove the invalidated set, or it knows fewer invalidations than were announced cluster-wide (a missed announcement).
    /// </summary>
    private IReadOnlySet<string>? StaleSince(AccessViewStamp loaded, AccessViewStamp current)
    {
        if (loaded.Catalog != current.Catalog ||
            loaded.VirtualFilterGeneration != current.VirtualFilterGeneration ||
            loaded.Generation > current.Generation ||
            current.Generation - loaded.Generation < current.ClusterSequence - loaded.ClusterSequence)
        {
            return null;
        }

        return loaded.Generation == current.Generation ? FrozenSet<string>.Empty : _epochs?.GetInvalidatedSince(loaded.Generation);
    }

    /// <summary>
    /// The usable view of <paramref name="key"/> at <paramref name="current"/>: null on a miss; otherwise the entry and the lower-case
    /// table keys whose cached decisions are stale (empty when none). An unusable entry is removed.
    /// </summary>
    internal (AccessViewEntry Entry, IReadOnlySet<string> Stale)? TryGet(string key, AccessViewStamp current)
    {
        if (!_cache.TryGetValue(key, out AccessViewEntry? entry) || entry is null)
        {
            return null;
        }

        var stale = Time.GetUtcNow() < entry.NotAfter ? StaleSince(entry.Stamp, current) : null;

        if (stale is null)
        {
            _cache.Remove(key);
            GatewayDiagnostics.DiscoveryAccessViewCacheCounter.Add(1, new KeyValuePair<string, object?>("result", "stale"));
            return null;
        }

        GatewayDiagnostics.DiscoveryAccessViewCacheCounter.Add(1, new KeyValuePair<string, object?>("result", stale.Count == 0 ? "hit" : "revalidate"));
        return (entry, stale);
    }

    /// <summary>
    /// Stores <paramref name="entry"/> under the stamp read before its load (compare-and-set, RR-L4-06): not when the catalog or the
    /// filters changed during the load or the invalidations since cannot be proven; invalidations the log does prove are re-decided by
    /// the next <see cref="TryGet"/>. Also not above the per-entry cap. Returns whether the cache holds the entry.
    /// </summary>
    internal async ValueTask<bool> TrySetAsync(string key, AccessViewEntry entry, CancellationToken ct)
    {
        var current = await ReadStampAsync(ct).ConfigureAwait(false);
        var size = entry.EstimateBytes();
        if (current is null || StaleSince(entry.Stamp, current) is null || size > _maxEntryBytes || entry.NotAfter <= Time.GetUtcNow())
        {
            return false;
        }

        _cache.Set(key, entry, new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = entry.NotAfter,
            Size = size
        });
        return _cache.TryGetValue(key, out _); // a set beyond the size limit is dropped
    }

    internal void Remove(string key) => _cache.Remove(key);

    /// <summary>Serializes the loads of one key (striped): concurrent requests of a caller wait for one load instead of repeating it.</summary>
    internal SemaphoreSlim GateFor(string key) => _gates[(StringComparer.Ordinal.GetHashCode(key) & int.MaxValue) % GateStripes];

    public void Dispose()
    {
        _cache.Dispose();
        foreach (var gate in _gates)
        {
            gate.Dispose();
        }
    }

    private sealed class SystemClockAdapter(TimeProvider time) : ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }
}

/// <summary>Issue #1044: what a cached access view was derived from (read before the load).</summary>
internal sealed record AccessViewStamp(long Generation, CatalogVersion Catalog, long VirtualFilterGeneration, long ClusterSequence);

/// <summary>Issue #1044: the caller's grant candidates and the allow bit of every decided candidate.</summary>
internal sealed record AccessViewEntry(
    ReadGrantCandidates Candidates,
    FrozenDictionary<TableIdentifier, bool> Allowed,
    AccessViewStamp Stamp,
    DateTimeOffset NotAfter)
{
    // Per decided table the frozen dictionary slot; per candidate the identifier with its own strings (read from the consents).
    internal long EstimateBytes() => 4096 + (Allowed.Count * 96L) + (Candidates.EstimatedCount * 320L);
}
