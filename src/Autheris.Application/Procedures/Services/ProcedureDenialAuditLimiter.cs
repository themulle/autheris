namespace Autheris.Application.Procedures.Services;

using System;
using System.Collections.Generic;

/// <summary>
/// SEC-PROC152-05: bounds the audit rows an authenticated caller can cause by probing unknown procedure names
/// (per tenant and SID, fixed one-minute window). Excess is dropped by the caller silently. Register as a singleton.
/// The tracked key set has a hard bound: expired keys are swept at most once per window, and new keys beyond the
/// bound are dropped (not audited, counted in <see cref="Dropped"/>). All state changes happen under one short lock,
/// so the bound holds under concurrency.
/// </summary>
public sealed class ProcedureDenialAuditLimiter
{
    private const int DefaultMaxTrackedKeys = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly int _perMinute;
    private readonly int _maxTrackedKeys;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;
    private long _dropped;

    public ProcedureDenialAuditLimiter(int perMinute = 30, TimeProvider? time = null, int maxTrackedKeys = DefaultMaxTrackedKeys)
    {
        _perMinute = Math.Max(1, perMinute);
        _maxTrackedKeys = Math.Max(1, maxTrackedKeys);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Number of audit writes dropped by the limiter since start (one counter, no caller text).</summary>
    public long Dropped => System.Threading.Interlocked.Read(ref _dropped);

    /// <summary>Number of currently tracked tenant and SID keys (never above the configured bound).</summary>
    public int TrackedKeys
    {
        get
        {
            lock (_gate)
            {
                return _buckets.Count;
            }
        }
    }

    /// <summary>True if one more denial audit row may be written for this tenant and SID.</summary>
    public bool TryAcquire(string tenant, string sid)
    {
        var now = _time.GetUtcNow();
        var key = tenant + "|" + sid;
        bool allowed;
        lock (_gate)
        {
            if (now >= _nextSweep)
            {
                Sweep(now);
                _nextSweep = now + Window;
            }

            if (_buckets.TryGetValue(key, out var bucket))
            {
                bucket = now - bucket.WindowStart >= Window ? new Bucket(now, 1) : bucket with { Count = bucket.Count + 1 };
                _buckets[key] = bucket;
                allowed = bucket.Count <= _perMinute;
            }
            else if (_buckets.Count >= _maxTrackedKeys)
            {
                allowed = false;
            }
            else
            {
                _buckets[key] = new Bucket(now, 1);
                allowed = true;
            }
        }

        if (!allowed)
        {
            System.Threading.Interlocked.Increment(ref _dropped);
        }

        return allowed;
    }

    private void Sweep(DateTimeOffset now)
    {
        List<string>? expired = null;
        foreach (var (key, bucket) in _buckets)
        {
            if (now - bucket.WindowStart >= Window)
            {
                (expired ??= []).Add(key);
            }
        }

        if (expired == null)
        {
            return;
        }

        foreach (var key in expired)
        {
            _buckets.Remove(key);
        }
    }

    private sealed record Bucket(DateTimeOffset WindowStart, int Count);
}
