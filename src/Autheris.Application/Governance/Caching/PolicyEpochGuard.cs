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
/// difference counts, so a counter reset also triggers a reload). While the shared state is unavailable the cache is
/// bypassed, so a stale allow is never served.
/// </summary>
internal sealed class PolicyEpochGuard : IDisposable
{
    public const string KeyPrefix = "policy:epoch:";
    public const string WildcardTenant = "*";
    public const string HintChannel = "policy:epoch:changed";

    // Upper bound for tracked tenants so arbitrary tenant strings cannot grow the table without limit; further tenants are
    // evaluated uncached.
    private const int MaxTrackedTenants = 10_000;

    private sealed class KeyState(string key)
    {
        public string Key { get; } = key;

        public readonly object Gate = new();
        public bool Initialised;
        public long Observed;
        public long NextCheck;
        public long UnavailableUntil;
        public Task<EpochCheckOutcome>? InFlight;
    }

    private readonly IDistributedClusterStateProvider _state;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly long _checkInterval;
    private readonly long _backoff;
    private readonly ConcurrentDictionary<string, KeyState> _tenants = new(StringComparer.Ordinal);
    private readonly KeyState _wildcard = new(KeyPrefix + WildcardTenant);

    public PolicyEpochGuard(
        IDistributedClusterStateProvider? state,
        TimeProvider time,
        TimeSpan checkInterval,
        TimeSpan unavailableBackoff,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(time);

        _state = state;
        _time = time;
        _logger = logger;
        _checkInterval = ToTicks(checkInterval);
        _backoff = ToTicks(unavailableBackoff);
    }

    public EpochStatus GetStatus(string tenant)
    {
        var now = _time.GetTimestamp();
        if (now < Volatile.Read(ref _wildcard.UnavailableUntil))
        {
            return EpochStatus.Degraded;
        }

        if (!_tenants.TryGetValue(tenant, out var state))
        {
            return _tenants.Count >= MaxTrackedTenants ? EpochStatus.Degraded : EpochStatus.Due;
        }

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
        var state = _tenants.GetOrAdd(tenant, static t => new KeyState(KeyPrefix + t));
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
            var result = await _state.IncrementAsync(state.Key, 1, TimeSpan.Zero).ConfigureAwait(false);
            if (result is null)
            {
                ReportPropagationFailure(tenantOrWildcard, null);
                return;
            }

            lock (state.Gate)
            {
                // Only a clean +1 is our own change; anything else means another node also changed policy, which the next
                // check must still observe and reload for.
                if (state.Initialised && state.Observed + 1 == result.Value)
                {
                    state.Observed = result.Value;
                }
            }
        }
        catch (Exception ex)
        {
            ReportPropagationFailure(tenantOrWildcard, ex);
        }
    }

    public void Dispose()
    {
    }

    private KeyState StateFor(string tenantOrWildcard) =>
        tenantOrWildcard == WildcardTenant
            ? _wildcard
            : _tenants.GetOrAdd(tenantOrWildcard, static t => new KeyState(KeyPrefix + t));

    private async Task<EpochCheckOutcome> ReadAsync(KeyState tenantState, Func<bool, bool, bool> onChanged)
    {
        try
        {
            EpochCheckOutcome outcome;
            try
            {
                var tenantValue = await _state.GetAsync<long>(tenantState.Key).ConfigureAwait(false);
                var wildcardValue = await _state.GetAsync<long>(_wildcard.Key).ConfigureAwait(false);
                var now = _time.GetTimestamp();

                var tenantChanged = Observe(tenantState, tenantValue, now);
                var wildcardChanged = Observe(_wildcard, wildcardValue, now);
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
            }
            catch (ClusterStateUnavailableException ex)
            {
                var until = _time.GetTimestamp() + _backoff;
                Volatile.Write(ref tenantState.UnavailableUntil, until);
                _logger?.LogWarning(ex, "Casbin policy epoch unavailable; decision cache bypassed.");
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

    private bool Observe(KeyState state, long value, long now)
    {
        lock (state.Gate)
        {
            state.NextCheck = now + _checkInterval;
            state.UnavailableUntil = 0;
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
