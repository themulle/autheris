namespace Autheris.Application.Procedures.Services;

using System;
using System.Collections.Concurrent;

/// <summary>
/// SEC-PROC152-05: bounds the audit rows an authenticated caller can cause by probing unknown procedure names
/// (per tenant and SID, fixed one-minute window). Excess is dropped by the caller silently. Register as a singleton.
/// </summary>
public sealed class ProcedureDenialAuditLimiter
{
    private const int MaxTrackedKeys = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly int _perMinute;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.OrdinalIgnoreCase);
    private long _dropped;

    public ProcedureDenialAuditLimiter(int perMinute = 30, TimeProvider? time = null)
    {
        _perMinute = Math.Max(1, perMinute);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Number of audit writes dropped by the limiter since start (one counter, no caller text).</summary>
    public long Dropped => System.Threading.Interlocked.Read(ref _dropped);

    /// <summary>True if one more denial audit row may be written for this tenant and SID.</summary>
    public bool TryAcquire(string tenant, string sid)
    {
        var now = _time.GetUtcNow();
        if (_buckets.Count >= MaxTrackedKeys)
        {
            foreach (var (key, bucket) in _buckets)
            {
                if (now - bucket.WindowStart >= Window)
                {
                    _buckets.TryRemove(key, out _);
                }
            }
        }

        if (_buckets.Count >= MaxTrackedKeys && !_buckets.ContainsKey(tenant + "|" + sid))
        {
            System.Threading.Interlocked.Increment(ref _dropped);
            return false;
        }

        var current = _buckets.AddOrUpdate(
            tenant + "|" + sid,
            _ => new Bucket(now, 1),
            (_, b) => now - b.WindowStart >= Window ? new Bucket(now, 1) : b with { Count = b.Count + 1 });

        if (current.Count > _perMinute)
        {
            System.Threading.Interlocked.Increment(ref _dropped);
            return false;
        }

        return true;
    }

    private sealed record Bucket(DateTimeOffset WindowStart, int Count);
}
