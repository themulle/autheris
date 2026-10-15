namespace Autheris.Application.Governance.Caching;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.State;
using Autheris.Domain.Diagnostics;
using Microsoft.Extensions.Logging;

internal enum EpochStatus
{
    /// <summary>The cluster epoch was read recently; cached decisions may be served.</summary>
    Fresh,

    /// <summary>A read of the cluster epoch is due.</summary>
    Due,

    /// <summary>The cluster state was unavailable recently; the cache must be bypassed until the backoff ends.</summary>
    Degraded
}

internal enum EpochCheckKind
{
    Unchanged,
    Changed,
    Unavailable
}

internal readonly record struct EpochCheckOutcome(EpochCheckKind Kind, bool TenantChanged = false, bool WildcardChanged = false)
{
    public static readonly EpochCheckOutcome Unchanged = new(EpochCheckKind.Unchanged);

    public static readonly EpochCheckOutcome Unavailable = new(EpochCheckKind.Unavailable);
}

/// <summary>
/// Cluster-wide policy epoch for the decision cache. A policy change on any node increments an atomic counter in the shared
/// state (<c>policy:epoch:{tenant}</c>, or <c>policy:epoch:*</c> for global policy); every node reads the counters at most
/// every <c>EpochCheckSeconds</c> and reloads its own policy source when the value differs from the last observed one (any
/// difference counts, so a counter reset also triggers a reload). While the epoch cannot be read the decision cache is
/// bypassed, so no cached decision is served on an unknown epoch.
/// </summary>
/// <remarks>
/// A cluster change makes this node's own policy source authoritative: the reload re-reads the node's policy file, so the file
/// content (not the other node's in-memory rules) becomes active. Tenant names are case-insensitive, matching policy storage.
/// </remarks>
internal sealed class PolicyEpochGuard : IDisposable
{
    public const string KeyPrefix = "policy:epoch:";
    public const string WildcardTenant = "*";
    public const string HintChannel = "policy:epoch:changed";

    private const int DefaultMaxTrackedTenants = 10_000;

    private sealed class KeyState(string key)
    {
        public string Key { get; } = key;

        public readonly object Gate = new();
        public bool Initialised;
        public long Observed;
        public long NextCheck;
        public long UnavailableUntil;
        public Task<EpochCheckOutcome>? InFlight;
        public long LastAccess;
    }

    private readonly IDistributedClusterStateProvider _state;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly long _checkInterval;
    private readonly long _backoff;
    private readonly ConcurrentDictionary<string, KeyState> _tenants = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _evictionGate = new();
    private readonly int _maxTrackedTenants;
    private readonly long _idleEviction;
    private readonly Func<long> _deltaSource;
    private int _trackedTenants;
    private readonly KeyState _wildcard = new(KeyPrefix + WildcardTenant);
    private readonly IAsyncDisposable? _hintSubscription;

    public PolicyEpochGuard(
        IDistributedClusterStateProvider? state,
        TimeProvider time,
        TimeSpan checkInterval,
        TimeSpan unavailableBackoff,
        ILogger? logger = null,
        int maxTrackedTenants = DefaultMaxTrackedTenants,
        TimeSpan? idleEvictionAfter = null,
        Func<long>? deltaSource = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(time);

        _state = state;
        _time = time;
        _logger = logger;
        _checkInterval = ToTicks(checkInterval);
        _backoff = ToTicks(unavailableBackoff);
        _maxTrackedTenants = maxTrackedTenants;
        _idleEviction = ToTicks(idleEvictionAfter ?? TimeSpan.FromMinutes(1));
        // A random positive delta (instead of always 1) keeps a counter reset to an equal value from hiding our own change.
        _deltaSource = deltaSource ?? (static () => Random.Shared.NextInt64(1, 1 << 20));

        try
        {
            _hintSubscription = state.SubscribeAsync<string>(HintChannel, OnHintAsync);
        }
        catch (Exception ex)
        {
            // The hint only shortens propagation latency; the periodic epoch check still converges.
            _logger?.LogWarning(ex, "Casbin policy epoch hint subscription failed; falling back to periodic epoch checks.");
        }
    }

    internal int TrackedTenantCount => Volatile.Read(ref _trackedTenants);

    public EpochStatus GetStatus(string tenant)
    {
        var now = _time.GetTimestamp();
        if (!_tenants.TryGetValue(tenant, out var state))
        {
            return EpochStatus.Due; // not tracked yet: CheckAsync decides whether the tenant can be tracked
        }

        Volatile.Write(ref state.LastAccess, now);
        if (now < Volatile.Read(ref state.UnavailableUntil))
        {
            return EpochStatus.Degraded;
        }

        return now >= Volatile.Read(ref state.NextCheck) || now >= Volatile.Read(ref _wildcard.NextCheck)
            ? EpochStatus.Due
            : EpochStatus.Fresh;
    }

    /// <summary>
    /// Reads the tenant and wildcard counters. One read per tenant is in flight at a time; <paramref name="onChanged"/> runs
    /// exactly once per detected change, before any waiting caller is released, so no caller proceeds on pre-reload policy.
    /// </summary>
    public async ValueTask<EpochCheckOutcome> CheckAsync(
        string tenant,
        Func<bool, bool, bool> onChanged,
        CancellationToken ct)
    {
        var state = TrackTenant(tenant);
        if (state is null)
        {
            return EpochCheckOutcome.Unavailable; // table full of recently used tenants: evaluate uncached
        }

        Task<EpochCheckOutcome> flight;
        lock (state.Gate)
        {
            flight = state.InFlight ??= Task.Run(() => ReadAsync(state, onChanged), CancellationToken.None);
        }

        return await flight.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Called when the reload after a detected change failed: forces the next check to see the change again.</summary>
    public void MarkReloadFailed(string tenant, bool tenantChanged, bool wildcardChanged)
    {
        if (tenantChanged && _tenants.TryGetValue(tenant, out var state))
        {
            lock (state.Gate)
            {
                state.Observed = long.MinValue;
            }
        }

        if (wildcardChanged)
        {
            lock (_wildcard.Gate)
            {
                _wildcard.Observed = long.MinValue;
            }
        }
    }

    /// <summary>Increments the cluster epoch after a local policy change.</summary>
    public async ValueTask PropagateAsync(string tenantOrWildcard)
    {
        try
        {
            var state = StateFor(tenantOrWildcard);
            if (state is null)
            {
                ReportPropagationFailure(tenantOrWildcard, null);
                return;
            }

            var delta = _deltaSource();
            var result = await _state.IncrementAsync(state.Key, delta, TimeSpan.Zero).ConfigureAwait(false);
            if (result is null)
            {
                ReportPropagationFailure(tenantOrWildcard, null);
                return;
            }

            lock (state.Gate)
            {
                // Only a clean +delta is our own change; anything else means another node also changed policy, which the
                // next check must still observe and reload for. Updated before the hint so our own hint finds it unchanged.
                if (state.Initialised && state.Observed + delta == result.Value)
                {
                    state.Observed = result.Value;
                }
            }

            await PublishHintAsync(state.Key).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportPropagationFailure(tenantOrWildcard, ex);
        }
    }

    public void Dispose()
    {
        if (_hintSubscription is null)
        {
            return;
        }

        try
        {
            _ = _hintSubscription.DisposeAsync().AsTask().ContinueWith(
                static _ => { },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Casbin policy epoch hint unsubscribe failed.");
        }
    }

    // A hint names the epoch key that changed; it makes the next evaluation read the counter instead of waiting for the
    // interval. Anything that is not a policy epoch key is ignored, and a hint never changes the observed value itself.
    // A hint can race with a read that is just finishing: that read then schedules the next check from its own completion, so
    // the hint may be lost. That only costs latency; the periodic check still converges.
    private ValueTask OnHintAsync(string key)
    {
        if (string.IsNullOrEmpty(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return ValueTask.CompletedTask;
        }

        if (key.Length == KeyPrefix.Length + 1 && key[^1] == '*')
        {
            Volatile.Write(ref _wildcard.NextCheck, 0);
        }
        else if (_tenants.TryGetValue(key[KeyPrefix.Length..], out var state))
        {
            Volatile.Write(ref state.NextCheck, 0);
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask PublishHintAsync(string key)
    {
        try
        {
            await _state.PublishEventAsync(HintChannel, key).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Casbin policy epoch hint could not be published; peers converge on their next epoch check.");
        }
    }

    private KeyState? StateFor(string tenantOrWildcard) =>
        tenantOrWildcard == WildcardTenant ? _wildcard : TrackTenant(tenantOrWildcard);

    // Returns the tracked state of a tenant, creating it when there is room. At the cap, tenants idle for longer than the
    // idle threshold are evicted first (their cached decisions have expired by then); without room the result is null.
    private KeyState? TrackTenant(string tenant)
    {
        if (_tenants.TryGetValue(tenant, out var existing))
        {
            return existing;
        }

        if (Volatile.Read(ref _trackedTenants) >= _maxTrackedTenants && !EvictIdle())
        {
            return null;
        }

        var created = new KeyState(KeyPrefix + tenant.ToLowerInvariant());
        var state = _tenants.GetOrAdd(tenant, created);
        if (ReferenceEquals(state, created))
        {
            Interlocked.Increment(ref _trackedTenants);
            Volatile.Write(ref state.LastAccess, _time.GetTimestamp());
        }

        return state;
    }

    private bool EvictIdle()
    {
        lock (_evictionGate)
        {
            var now = _time.GetTimestamp();
            var evicted = 0;
            foreach (var (name, state) in _tenants)
            {
                if (now - Volatile.Read(ref state.LastAccess) < _idleEviction)
                {
                    continue;
                }

                lock (state.Gate)
                {
                    if (state.InFlight is null && _tenants.TryRemove(name, out _))
                    {
                        Interlocked.Decrement(ref _trackedTenants);
                        evicted++;
                    }
                }
            }

            return evicted > 0;
        }
    }

    private async Task<EpochCheckOutcome> ReadAsync(KeyState tenantState, Func<bool, bool, bool> onChanged)
    {
        try
        {
            EpochCheckOutcome outcome;
            try
            {
                var tenantValue = await _state.GetAsync<long>(tenantState.Key).ConfigureAwait(false);
                var wildcardValue = await _state.GetAsync<long>(_wildcard.Key).ConfigureAwait(false);

                var tenantChanged = Observe(tenantState, tenantValue);
                var wildcardChanged = false;
                lock (_wildcard.Gate)
                {
                    wildcardChanged = Observe(_wildcard, wildcardValue);
                    if (tenantChanged || wildcardChanged)
                    {
                        outcome = new EpochCheckOutcome(EpochCheckKind.Changed, tenantChanged, wildcardChanged);
                        if (!onChanged(tenantChanged, wildcardChanged))
                        {
                            MarkReloadFailed(tenantState.Key[KeyPrefix.Length..], tenantChanged, wildcardChanged);
                        }
                    }
                    else
                    {
                        outcome = EpochCheckOutcome.Unchanged;
                    }

                    // The next check is scheduled only after a reload finished: until then the status stays Due, so concurrent
                    // callers wait for the reload instead of serving cached decisions of the old policy.
                    var next = _time.GetTimestamp() + _checkInterval;
                    Volatile.Write(ref tenantState.NextCheck, next);
                    Volatile.Write(ref _wildcard.NextCheck, next);
                    Volatile.Write(ref tenantState.UnavailableUntil, 0);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unreachable store, corrupted value or any other failure: the epoch is unknown, so bypass the cache.
                var until = _time.GetTimestamp() + _backoff;
                Volatile.Write(ref tenantState.UnavailableUntil, until);
                _logger?.LogWarning(ex, "Casbin policy epoch could not be read; decision cache bypassed.");
                outcome = EpochCheckOutcome.Unavailable;
            }

            return outcome;
        }
        finally
        {
            lock (tenantState.Gate)
            {
                tenantState.InFlight = null;
            }
        }
    }

    private static bool Observe(KeyState state, long value)
    {
        lock (state.Gate)
        {
            if (!state.Initialised)
            {
                state.Initialised = true;
                state.Observed = value;
                return false;
            }

            if (state.Observed == value)
            {
                return false;
            }

            state.Observed = value;
            return true;
        }
    }

    private void ReportPropagationFailure(string key, Exception? ex)
    {
        GatewayDiagnostics.PolicyEpochPropagationFailedCounter.Add(1);
        _logger?.LogError(
            ex,
            "Casbin policy epoch for '{EpochKey}' could not be incremented; other nodes will pick up the change when their cached decisions expire.",
            key);
    }

    private long ToTicks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);
}
