using System.Collections.Concurrent;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Governance.Caching;

/// <summary>
/// Caches per-tenant governance epochs bounded by <see cref="GovernanceCacheSettings.EpochPollSeconds"/>
/// and invalidates local protection caches immediately on change notifications or epoch advances.
/// </summary>
public sealed class GovernanceEpochProvider : IGovernanceCacheInvalidator, IDisposable
{
    private readonly record struct CachedEpoch(long Epoch, long CachedAtTimestamp);

    private readonly IGovernanceEpochStore _epochStore;
    private readonly TimeSpan _pollInterval;
    private readonly TimeProvider _time;
    private readonly IDisposable? _subscription;
    private readonly ConcurrentDictionary<string, CachedEpoch> _epochs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _tenantGenerations = new(StringComparer.Ordinal);
    private readonly Lock _callbackLock = new();
    private readonly List<Action<string>> _tenantAdvancedCallbacks = [];
    private long _globalGeneration;
    private int _disposed;

    public GovernanceEpochProvider(
        IGovernanceEpochStore epochStore,
        IOptions<GovernanceCacheSettings>? cacheSettings = null,
        IGovernanceChangeNotifier? changeNotifier = null,
        TimeProvider? timeProvider = null)
    {
        _epochStore = epochStore ?? throw new ArgumentNullException(nameof(epochStore));
        var settings = cacheSettings?.Value ?? new GovernanceCacheSettings();
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.EpochPollSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.EpochPollSeconds, 60);

        _pollInterval = TimeSpan.FromSeconds(settings.EpochPollSeconds);
        _time = timeProvider ?? TimeProvider.System;
        _subscription = changeNotifier?.Subscribe(OnChange);
    }

    public IDisposable OnTenantEpochAdvanced(Action<string> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_callbackLock)
        {
            _tenantAdvancedCallbacks.Add(callback);
        }

        return new CallbackToken(this, callback);
    }

    public async ValueTask<long> GetEpochAsync(TenantId tenant, CancellationToken ct = default)
    {
        var tenantKey = RequireValidTenant(tenant);
        if (_epochs.TryGetValue(tenantKey, out var cached)
            && _time.GetElapsedTime(cached.CachedAtTimestamp) < _pollInterval)
        {
            return cached.Epoch;
        }

        var startGlobalGen = Volatile.Read(ref _globalGeneration);
        var startTenantGen = _tenantGenerations.GetValueOrDefault(tenantKey);

        var latest = await _epochStore.GetEpochAsync(tenant, ct).ConfigureAwait(false);
        if (latest < 0)
        {
            throw new InvalidOperationException("Governance epoch cannot be negative.");
        }

        var advanced = false;
        if (_epochs.TryGetValue(tenantKey, out var prior) && latest != prior.Epoch)
        {
            advanced = true;
        }

        // Only cache the read epoch if no invalidation occurred while the DB call was in flight.
        if (Volatile.Read(ref _globalGeneration) == startGlobalGen
            && _tenantGenerations.GetValueOrDefault(tenantKey) == startTenantGen)
        {
            _epochs[tenantKey] = new CachedEpoch(latest, _time.GetTimestamp());
        }
        else
        {
            _epochs.TryRemove(tenantKey, out _);
            advanced = true;
        }

        if (advanced)
        {
            NotifyEpochAdvanced(tenantKey);
        }

        return latest;
    }

    public void InvalidateTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            InvalidateAll();
            return;
        }

        _tenantGenerations.AddOrUpdate(tenantId, 1, static (_, g) => g + 1);
        _epochs.TryRemove(tenantId, out _);
        NotifyEpochAdvanced(tenantId);
    }

    public void InvalidateAll()
    {
        Interlocked.Increment(ref _globalGeneration);
        _epochs.Clear();
        NotifyEpochAdvanced(string.Empty);
    }

    private void OnChange(GovernanceChangeEvent change)
    {
        if (change.IsFullInvalidation)
        {
            InvalidateAll();
        }
        else
        {
            InvalidateTenant(change.TenantId);
        }
    }

    private void NotifyEpochAdvanced(string tenantId)
    {
        Action<string>[] snapshot;
        lock (_callbackLock)
        {
            if (_tenantAdvancedCallbacks.Count == 0)
            {
                return;
            }

            snapshot = _tenantAdvancedCallbacks.ToArray();
        }

        foreach (var callback in snapshot)
        {
            callback(tenantId);
        }
    }

    private static string RequireValidTenant(TenantId tenant) =>
        !string.IsNullOrWhiteSpace(tenant.Value) && TenantId.TryParse(tenant.Value, out _)
            ? tenant.Value
            : throw new ArgumentException("A valid non-reserved TenantId is required.", nameof(tenant));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _subscription?.Dispose();
        lock (_callbackLock)
        {
            _tenantAdvancedCallbacks.Clear();
        }
    }

    private sealed class CallbackToken(GovernanceEpochProvider owner, Action<string> callback) : IDisposable
    {
        private Action<string>? _callback = callback;

        public void Dispose()
        {
            var cb = Interlocked.Exchange(ref _callback, null);
            if (cb is null)
            {
                return;
            }

            lock (owner._callbackLock)
            {
                owner._tenantAdvancedCallbacks.Remove(cb);
            }
        }
    }
}
