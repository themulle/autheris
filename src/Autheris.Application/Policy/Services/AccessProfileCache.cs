namespace Autheris.Application.Policy.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy.Exceptions;
using Autheris.Application.Policy.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// AR-01: Multi-node cluster-consistent access profile cache with epoch-keying and fail-closed security invariants.
/// </summary>
public sealed class AccessProfileCache : IAccessProfileCache, IDisposable
{
    public const string InvalidationChannel = "autheris:profile:invalidate";
    private const string EpochKeyPrefix = "profile_epoch:";
    private static readonly TimeSpan ProfileEntryTtl = TimeSpan.FromMinutes(5);

    private readonly IAccessProfileRepository _accessProfileRepository;
    private readonly IDistributedClusterStateProvider? _clusterState;
    private readonly IMemoryCache _memoryCache;
    private readonly IOptions<GatewayOptions>? _options;
    private readonly IEventBus? _eventBus;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AccessProfileCache> _logger;
    private readonly IDisposable? _subscription;

    // Local epoch cache: Tenant -> (Epoch, ExpiryTimestamp)
    private readonly ConcurrentDictionary<string, (long Epoch, DateTimeOffset ExpiresAt)> _epochCache = new(StringComparer.OrdinalIgnoreCase);

    public AccessProfileCache(
        IAccessProfileRepository accessProfileRepository,
        IDistributedClusterStateProvider? clusterState,
        IMemoryCache memoryCache,
        IOptions<GatewayOptions>? options = null,
        IEventBus? eventBus = null,
        TimeProvider? timeProvider = null,
        ILogger<AccessProfileCache>? logger = null)
    {
        _accessProfileRepository = accessProfileRepository ?? throw new ArgumentNullException(nameof(accessProfileRepository));
        _clusterState = clusterState;
        _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
        _options = options;
        _eventBus = eventBus;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<AccessProfileCache>.Instance;

        if (_eventBus != null)
        {
            _eventBus.ConnectionRestored += OnConnectionRestored;
            _subscription = _eventBus.Subscribe<string>(InvalidationChannel, OnPeerInvalidationAsync);
        }
    }

    private void OnConnectionRestored()
    {
        _logger.LogInformation("Event bus connection restored. Evicting local access profile epoch cache to resynchronize with cluster (AR-03).");
        _epochCache.Clear();
    }

    private Task OnPeerInvalidationAsync(string tenantId)
    {
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            _epochCache.TryRemove(tenantId.Trim(), out _);
        }
        return Task.CompletedTask;
    }

    public async ValueTask<IReadOnlyList<AccessProfile>> GetProfilesAsync(TenantId tenantId, string subject, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject, nameof(subject));

        long? epoch = null;
        bool storeAvailable = false;

        if (_clusterState != null)
        {
            try
            {
                epoch = await GetEpochAsync(tenantId, ct).ConfigureAwait(false);
                storeAvailable = epoch.HasValue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to read access profile epoch from cluster store for tenant {Tenant}. Bypassing L1 cache.", tenantId.Value);
                storeAvailable = false;
            }
        }

        if (storeAvailable && epoch.HasValue)
        {
            // Epoch is readable: use epoch-keyed L1 cache entry
            var cacheKey = $"auth:profile:{tenantId.Value}:{epoch.Value}:{subject}";
            if (_memoryCache.TryGetValue(cacheKey, out var cachedObj) && cachedObj is IReadOnlyList<AccessProfile> cachedProfiles)
            {
                return cachedProfiles;
            }

            // L1 cache miss: load from DB and store under the exact epoch read before loading (prevents load-after-invalidate race)
            IReadOnlyList<AccessProfile> dbProfiles;
            try
            {
                dbProfiles = await _accessProfileRepository.GetProfilesForSubjectAsync(tenantId, subject, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to read access profiles from database for tenant {Tenant}, subject {Subject}.", tenantId.Value, subject);
                throw new AccessProfileSourceUnavailableException($"Access profile repository failed for tenant '{tenantId.Value}' and subject '{subject}'.", ex);
            }

            using var entry = _memoryCache.CreateEntry(cacheKey);
            entry.Value = dbProfiles;
            entry.Size = 1;
            entry.AbsoluteExpirationRelativeToNow = ProfileEntryTtl;

            return dbProfiles;
        }

        // Epoch store is unavailable or unconfigured: bypass L1 cache and read authoritative DB directly
        try
        {
            var dbProfiles = await _accessProfileRepository.GetProfilesForSubjectAsync(tenantId, subject, ct).ConfigureAwait(false);
            return dbProfiles;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to read access profiles from database (epoch store was unavailable) for tenant {Tenant}, subject {Subject}.", tenantId.Value, subject);
            throw new AccessProfileSourceUnavailableException($"Neither epoch store nor database is available to resolve access profiles for tenant '{tenantId.Value}'.", ex);
        }
    }

    public async Task InvalidateTenantAsync(TenantId tenantId, CancellationToken ct = default)
    {
        if (_clusterState == null)
        {
            _epochCache.TryRemove(tenantId.Value, out _);
            return;
        }

        long? newEpoch;
        try
        {
            // Monotonic cluster counter without expiration (prevents epoch reset to 1)
            newEpoch = await _clusterState.IncrementAsync(
                $"{EpochKeyPrefix}{tenantId.Value}",
                1,
                TimeSpan.Zero,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _epochCache.TryRemove(tenantId.Value, out _);
            _logger.LogError(ex, "Failed to bump cluster access profile epoch for tenant {Tenant}.", tenantId.Value);
            throw new InvalidOperationException($"Cluster state store failed to increment access profile epoch for tenant '{tenantId.Value}'.", ex);
        }

        if (!newEpoch.HasValue)
        {
            _epochCache.TryRemove(tenantId.Value, out _);
            _logger.LogError("Cluster state store returned null when incrementing access profile epoch for tenant {Tenant}.", tenantId.Value);
            throw new InvalidOperationException($"Cluster state store returned null when incrementing access profile epoch for tenant '{tenantId.Value}'.");
        }

        _epochCache[tenantId.Value] = (newEpoch.Value, _timeProvider.GetUtcNow().AddMilliseconds(GetEpochCacheTtlMs()));

        if (_eventBus != null)
        {
            try
            {
                await _eventBus.PublishAsync(InvalidationChannel, tenantId.Value, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Best-effort publish of access profile invalidation failed for tenant {Tenant}.", tenantId.Value);
            }
        }
    }

    private async ValueTask<long?> GetEpochAsync(TenantId tenantId, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        if (_epochCache.TryGetValue(tenantId.Value, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Epoch;
        }

        var storeKey = $"{EpochKeyPrefix}{tenantId.Value}";
        var epoch = await _clusterState!.GetAsync<long>(storeKey, ct).ConfigureAwait(false);
        if (epoch == 0)
        {
            // Initialize if not yet present
            var bumped = await _clusterState.IncrementAsync(storeKey, 1, TimeSpan.Zero, ct).ConfigureAwait(false);
            epoch = bumped ?? 1;
        }

        _epochCache[tenantId.Value] = (epoch, now.AddMilliseconds(GetEpochCacheTtlMs()));
        return epoch;
    }

    private int GetEpochCacheTtlMs()
    {
        var configured = _options?.Value?.Caching?.L1MemoryCache?.AccessProfileEpochCacheMilliseconds ?? 1000;
        return Math.Clamp(configured, 0, 5000);
    }

    public void Dispose()
    {
        if (_eventBus != null)
        {
            _eventBus.ConnectionRestored -= OnConnectionRestored;
        }
        _subscription?.Dispose();
    }
}
