namespace Autheris.Infrastructure.RateLimiting;

using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Domain.Common;

/// <summary>
/// Process-local in-memory <see cref="IWriteBudgetStore"/> tracking fixed-window per-minute statement counts
/// and per-hour affected-row counts scoped by <c>(tenantId, subjectSid)</c>.
/// </summary>
public sealed class InMemoryWriteBudgetStore : IWriteBudgetStore
{
    public const int MaxEntries = 25_000;

    private sealed class CallerBudgetState
    {
        public readonly object Sync = new();
        public long MinuteBucket;
        public int StatementCount;
        public long HourBucket;
        public long RowCount;
        public long LastSeenUnixSeconds;
    }

    private readonly ConcurrentDictionary<string, CallerBudgetState> _states = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public InMemoryWriteBudgetStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<WriteBudgetAcquisition> TryAcquireStatementAsync(
        TenantId tenantId,
        string subjectSid,
        WriteBudgetLimit limit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectSid);
        ArgumentNullException.ThrowIfNull(limit);
        ct.ThrowIfCancellationRequested();

        long effectiveStatementCap = limit.EffectiveStatementMaxRows();
        if (limit.StatementsPerMinute <= 0 || limit.RowsPerHour <= 0)
        {
            return Task.FromResult(new WriteBudgetAcquisition(false, 0, effectiveStatementCap, 60, "write_budget_exhausted"));
        }

        long nowSec = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        long minuteBucket = nowSec / 60;
        long hourBucket = nowSec / 3600;
        string key = BuildCallerKey(tenantId, subjectSid);

        EvictStaleIfNeeded(nowSec);
        var state = _states.GetOrAdd(key, _ => new CallerBudgetState
        {
            MinuteBucket = minuteBucket,
            HourBucket = hourBucket,
            LastSeenUnixSeconds = nowSec
        });

        lock (state.Sync)
        {
            state.LastSeenUnixSeconds = nowSec;
            if (state.MinuteBucket != minuteBucket)
            {
                state.MinuteBucket = minuteBucket;
                state.StatementCount = 0;
            }

            if (state.HourBucket != hourBucket)
            {
                state.HourBucket = hourBucket;
                state.RowCount = 0;
            }

            long remainingRows = limit.RowsPerHour - state.RowCount;
            if (remainingRows <= 0)
            {
                int retryAfterHour = (int)Math.Max(1, 3600 - (nowSec % 3600));
                return Task.FromResult(new WriteBudgetAcquisition(false, 0, effectiveStatementCap, retryAfterHour, "write_budget_exhausted"));
            }

            if (state.StatementCount >= limit.StatementsPerMinute)
            {
                int retryAfterMinute = (int)Math.Max(1, 60 - (nowSec % 60));
                return Task.FromResult(new WriteBudgetAcquisition(false, remainingRows, effectiveStatementCap, retryAfterMinute, "write_budget_exhausted"));
            }

            state.StatementCount++;
            return Task.FromResult(new WriteBudgetAcquisition(true, remainingRows, effectiveStatementCap, 0));
        }
    }

    public Task<bool> TryRecordAffectedRowsAsync(
        TenantId tenantId,
        string subjectSid,
        long affectedRows,
        long rowsPerHourLimit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectSid);
        ct.ThrowIfCancellationRequested();

        if (rowsPerHourLimit <= 0)
        {
            return Task.FromResult(false);
        }

        if (affectedRows <= 0)
        {
            return Task.FromResult(true);
        }

        long nowSec = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        long hourBucket = nowSec / 3600;
        string key = BuildCallerKey(tenantId, subjectSid);

        var state = _states.GetOrAdd(key, _ => new CallerBudgetState
        {
            MinuteBucket = nowSec / 60,
            HourBucket = hourBucket,
            LastSeenUnixSeconds = nowSec
        });

        lock (state.Sync)
        {
            state.LastSeenUnixSeconds = nowSec;
            if (state.HourBucket != hourBucket)
            {
                state.HourBucket = hourBucket;
                state.RowCount = 0;
            }

            if (state.RowCount > rowsPerHourLimit - affectedRows)
            {
                return Task.FromResult(false);
            }

            state.RowCount += affectedRows;
            return Task.FromResult(true);
        }
    }

    internal static string BuildCallerKey(TenantId tenantId, string subjectSid)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId.Value}\0{subjectSid}"));
        return Convert.ToHexString(hash);
    }

    private void EvictStaleIfNeeded(long nowSec)
    {
        if (_states.Count < MaxEntries)
        {
            return;
        }

        long cutoff = nowSec - 7200;
        foreach (var kvp in _states)
        {
            if (Volatile.Read(ref kvp.Value.LastSeenUnixSeconds) < cutoff)
            {
                _states.TryRemove(kvp.Key, out _);
            }
        }
    }
}
