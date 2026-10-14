namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;

/// <summary>
/// Process-local, bounded <see cref="IAccessPlanStore"/> with TTL eviction through <see cref="TimeProvider"/>. Used when
/// the cluster state is not shared across replicas (single node, development, tests).
/// </summary>
public sealed class InMemoryAccessPlanStore : IAccessPlanStore
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _sweepInterval;
    private readonly object _saveLock = new();
    private readonly ConcurrentDictionary<string, StoredAccessPlan> _plans = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumedTokens = new(StringComparer.Ordinal);
    private long _lastSweepTicks;

    public InMemoryAccessPlanStore(TimeProvider? timeProvider = null, TimeSpan? sweepInterval = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _sweepInterval = sweepInterval is { } interval && interval > TimeSpan.Zero ? interval : TimeSpan.FromSeconds(60);
    }

    internal int PlanCount => _plans.Count;

    internal int ConsumedTokenCount => _consumedTokens.Count;

    public ValueTask<bool> TrySavePlanAsync(StoredAccessPlan plan, TimeSpan ttl, int maxPendingPerAdmin, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var now = _time.GetUtcNow();
        Sweep(force: false);

        lock (_saveLock)
        {
            var pending = _plans.Values.Count(p =>
                p.Result.ExpiresAt > now && string.Equals(p.AdminSid, plan.AdminSid, StringComparison.OrdinalIgnoreCase));
            if (pending >= maxPendingPerAdmin)
            {
                return ValueTask.FromResult(false);
            }

            _plans[plan.Result.PlanId] = plan;
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<StoredAccessPlan?> GetPlanAsync(string planId, CancellationToken ct = default)
    {
        Sweep(force: false);
        if (!string.IsNullOrWhiteSpace(planId) &&
            _plans.TryGetValue(planId, out var stored) &&
            stored.Result.ExpiresAt > _time.GetUtcNow())
        {
            return ValueTask.FromResult<StoredAccessPlan?>(stored);
        }

        return ValueTask.FromResult<StoredAccessPlan?>(null);
    }

    public ValueTask<bool> SetConfirmedTokenAsync(string planId, string tokenHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(planId))
        {
            return ValueTask.FromResult(false);
        }

        lock (_saveLock)
        {
            if (!_plans.TryGetValue(planId, out var stored) || stored.Result.ExpiresAt <= _time.GetUtcNow())
            {
                return ValueTask.FromResult(false);
            }

            _plans[planId] = stored with { ConfirmedTokenHash = tokenHash };
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<bool> TryConsumeTokenAsync(string tokenHash, TimeSpan ttl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        var expiry = _time.GetUtcNow().Add(ttl > TimeSpan.Zero ? ttl : TimeSpan.FromSeconds(1));
        return ValueTask.FromResult(_consumedTokens.TryAdd(tokenHash, expiry));
    }

    /// <summary>
    /// Removes expired plans and consumed-token entries. Throttled by the sweep interval unless
    /// <paramref name="force"/> is set.
    /// </summary>
    internal void Sweep(bool force)
    {
        var now = _time.GetUtcNow();
        var nowTicks = now.UtcTicks;
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (!force && last != 0 && nowTicks - last < _sweepInterval.Ticks)
        {
            return;
        }

        Interlocked.Exchange(ref _lastSweepTicks, nowTicks);

        foreach (var (id, plan) in _plans)
        {
            if (plan.Result.ExpiresAt <= now)
            {
                _plans.TryRemove(id, out _);
            }
        }

        foreach (var (token, expiry) in _consumedTokens)
        {
            if (expiry <= now)
            {
                _consumedTokens.TryRemove(token, out _);
            }
        }
    }
}
