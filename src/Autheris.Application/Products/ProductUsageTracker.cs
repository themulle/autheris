namespace Autheris.Application.Products;

using System;
using System.Collections.Concurrent;
using System.Threading;
using Autheris.Domain.Common;

/// <summary>A point-in-time copy of the usage counters of one product.</summary>
public sealed record ProductUsageSnapshot(
    long Reads,
    long RowsRead,
    long Loads,
    long FailedLoads,
    long LastLoadRows,
    long LastLoadDurationMs,
    DateTimeOffset? LastReadAt);

/// <summary>
/// Usage counters per tenant and product id: reads (with returned rows) and refresh runs (with the staged rows and duration). The number of
/// tracked products is capped, so a flood of products cannot grow the store without bound; what does not fit is counted as overflow. The
/// counters carry no caller, query or value information.
/// </summary>
public sealed class ProductUsageTracker(TimeProvider? timeProvider = null)
{
    /// <summary>Fixed cap of tracked products in this process.</summary>
    public const int MaxTrackedProducts = 5_000;

    private readonly ConcurrentDictionary<(string Tenant, Guid Product), Counters> _counters = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _overflow;

    /// <summary>Events that were not tracked because the cap was reached.</summary>
    public long Overflow => Interlocked.Read(ref _overflow);

    public int TrackedProducts => _counters.Count;

    public void RecordRead(TenantId tenant, Guid productId, long rows)
    {
        if (Slot(tenant, productId) is not { } c)
        {
            return;
        }

        Interlocked.Increment(ref c.Reads);
        Interlocked.Add(ref c.RowsRead, Math.Max(0, rows));
        Interlocked.Exchange(ref c.LastReadTicks, _time.GetUtcNow().UtcTicks);
    }

    public void RecordLoad(TenantId tenant, Guid productId, bool succeeded, long rows, long durationMs)
    {
        if (Slot(tenant, productId) is not { } c)
        {
            return;
        }

        if (succeeded)
        {
            Interlocked.Increment(ref c.Loads);
            Interlocked.Exchange(ref c.LastLoadRows, Math.Max(0, rows));
            Interlocked.Exchange(ref c.LastLoadDurationMs, Math.Max(0, durationMs));
        }
        else
        {
            Interlocked.Increment(ref c.FailedLoads);
        }
    }

    public ProductUsageSnapshot Snapshot(TenantId tenant, Guid productId)
    {
        if (string.IsNullOrWhiteSpace(tenant.Value) || !_counters.TryGetValue((tenant.Value, productId), out var c))
        {
            return new ProductUsageSnapshot(0, 0, 0, 0, 0, 0, null);
        }

        var ticks = Interlocked.Read(ref c.LastReadTicks);
        return new ProductUsageSnapshot(
            Interlocked.Read(ref c.Reads), Interlocked.Read(ref c.RowsRead), Interlocked.Read(ref c.Loads), Interlocked.Read(ref c.FailedLoads),
            Interlocked.Read(ref c.LastLoadRows), Interlocked.Read(ref c.LastLoadDurationMs), ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero));
    }

    private Counters? Slot(TenantId tenant, Guid productId)
    {
        if (string.IsNullOrWhiteSpace(tenant.Value))
        {
            return null;
        }

        var key = (tenant.Value, productId);
        if (_counters.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (_counters.Count >= MaxTrackedProducts)
        {
            Interlocked.Increment(ref _overflow);
            return null;
        }

        return _counters.GetOrAdd(key, static _ => new Counters());
    }

    private sealed class Counters
    {
        public long Reads;
        public long RowsRead;
        public long Loads;
        public long FailedLoads;
        public long LastLoadRows;
        public long LastLoadDurationMs;
        public long LastReadTicks;
    }
}
