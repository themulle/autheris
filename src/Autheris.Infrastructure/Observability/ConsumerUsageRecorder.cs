namespace Autheris.Infrastructure.Observability;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Security;

/// <summary>
/// Architecture Phase 2 / Issue #114: High-throughput, lock-free in-memory accumulator for dataset consumer metrics.
/// Bounded buffer prevents unbounded memory growth (MaxSeriesCapacity = 20,000).
/// Actor SIDs are pseudorandomly hashed via HMAC-SHA256; raw personal SIDs are never retained.
/// </summary>
public sealed class ConsumerUsageRecorder : IConsumerUsageRecorder
{
    public const int MaxSeriesCapacity = 20_000;

    private readonly byte[] _hmacKey;
    private readonly ICallerContextProvider? _callerContextProvider;
    private long _overflowCount;

    private sealed record DailyUsageKey(
        string TenantId,
        string TableId,
        string Day,
        string Application,
        string ClientId,
        string Version
    );

    private sealed class DailyUsageMetrics
    {
        public long Calls;
        public long RowsReturned;
        public long RowsMeasuredCalls;
        public long RejAccess;
        public long RejRateLimit;
        public long RejCost;
        public long RejOther;
    }

    private sealed record QueryNameKey(
        string TenantId,
        string TableId,
        string Day,
        string Application,
        string QueryName
    );

    private sealed record ActorSampleKey(
        string TenantId,
        string TableId,
        string Day,
        string Application,
        string ActorHash
    );

    private readonly ConcurrentDictionary<DailyUsageKey, DailyUsageMetrics> _dailyUsage = new();
    private readonly ConcurrentDictionary<QueryNameKey, long> _queryCalls = new();
    private readonly ConcurrentDictionary<ActorSampleKey, byte> _actorSamples = new();

    public long OverflowCount => Volatile.Read(ref _overflowCount);
    public int ActiveSeriesCount => _dailyUsage.Count;

    public ConsumerUsageRecorder(
        string? clusterSecret = null,
        ICallerContextProvider? callerContextProvider = null)
    {
        _callerContextProvider = callerContextProvider;
        var secret = string.IsNullOrWhiteSpace(clusterSecret)
            ? "autheris-default-cluster-telemetry-secret-key-2026"
            : clusterSecret;
        _hmacKey = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    public void RecordUsage(
        TableIdentifier tableId,
        TenantId tenantId,
        Sid actorSid,
        ConsumerOutcome outcome,
        long rowsReturned,
        bool rowsMeasured,
        CallerContext? caller = null)
    {
        try
        {
            var effectiveCaller = caller ?? _callerContextProvider?.ResolveCallerContext();

            string app = CallerLabelNormalizer.NormalizeApplication(effectiveCaller?.Application);
            string ver = CallerLabelNormalizer.NormalizeVersion(effectiveCaller?.Version);
            string qry = CallerLabelNormalizer.NormalizeQueryName(effectiveCaller?.QueryName);
            string clientId = CallerLabelNormalizer.NormalizeClientId(effectiveCaller?.ClientId);
            string day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

            var key = new DailyUsageKey(tenantId.Value, tableId.ToString(), day, app, clientId, ver);

            // SEC-AUD114-05: Enforce MaxSeriesCapacity
            if (_dailyUsage.Count >= MaxSeriesCapacity && !_dailyUsage.ContainsKey(key))
            {
                Interlocked.Increment(ref _overflowCount);
                return;
            }

            var metrics = _dailyUsage.GetOrAdd(key, _ =>
            {
                if (_dailyUsage.Count >= MaxSeriesCapacity)
                {
                    Interlocked.Increment(ref _overflowCount);
                }
                return new DailyUsageMetrics();
            });

            Interlocked.Increment(ref metrics.Calls);
            if (rowsMeasured)
            {
                Interlocked.Add(ref metrics.RowsReturned, Math.Max(0, rowsReturned));
                Interlocked.Increment(ref metrics.RowsMeasuredCalls);
            }

            switch (outcome)
            {
                case ConsumerOutcome.DenyAccess:
                    Interlocked.Increment(ref metrics.RejAccess);
                    break;
                case ConsumerOutcome.DenyRateLimit:
                    Interlocked.Increment(ref metrics.RejRateLimit);
                    break;
                case ConsumerOutcome.DenyCost:
                    Interlocked.Increment(ref metrics.RejCost);
                    break;
                case ConsumerOutcome.DenyOther:
                    Interlocked.Increment(ref metrics.RejOther);
                    break;
                case ConsumerOutcome.Allow:
                default:
                    break;
            }

            // Query name tracking
            if (!string.IsNullOrWhiteSpace(qry) &&
                qry != CallerLabelNormalizer.UnknownSentinel &&
                qry != CallerLabelNormalizer.InvalidSentinel)
            {
                var qKey = new QueryNameKey(tenantId.Value, tableId.ToString(), day, app, qry);
                if (_queryCalls.Count < MaxSeriesCapacity || _queryCalls.ContainsKey(qKey))
                {
                    _queryCalls.AddOrUpdate(qKey, 1, (_, c) => c + 1);
                }
            }

            // SEC-AUD114-04: HMAC-SHA256 pseudonymized actor sample (Raw SID is NEVER stored)
            if (!string.IsNullOrWhiteSpace(actorSid.Value))
            {
                string actorHash = ComputeActorHmac(actorSid.Value);
                var aKey = new ActorSampleKey(tenantId.Value, tableId.ToString(), day, app, actorHash);
                if (_actorSamples.Count < MaxSeriesCapacity || _actorSamples.ContainsKey(aKey))
                {
                    _actorSamples.TryAdd(aKey, 0);
                }
            }
        }
        catch
        {
            // Telemetry must never throw or disrupt hot path query execution
        }
    }

    public async Task FlushAsync(IConsumerAggregationRepository repository, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        // Snapshot and clear daily usage
        var usageSnapshots = new List<KeyValuePair<DailyUsageKey, DailyUsageMetrics>>();
        foreach (var key in _dailyUsage.Keys)
        {
            if (_dailyUsage.TryRemove(key, out var metrics))
            {
                usageSnapshots.Add(new KeyValuePair<DailyUsageKey, DailyUsageMetrics>(key, metrics));
            }
        }

        foreach (var (k, m) in usageSnapshots)
        {
            if (!TableIdentifier.TryParse(k.TableId, out var tableId)) continue;
            var tenantId = new TenantId(k.TenantId);
            await repository.RecordDailyUsageAsync(
                tableId, tenantId, k.Day, k.Application, k.ClientId, k.Version,
                calls: Volatile.Read(ref m.Calls),
                rowsReturned: Volatile.Read(ref m.RowsReturned),
                rowsMeasuredCalls: Volatile.Read(ref m.RowsMeasuredCalls),
                rejAccess: Volatile.Read(ref m.RejAccess),
                rejRateLimit: Volatile.Read(ref m.RejRateLimit),
                rejCost: Volatile.Read(ref m.RejCost),
                rejOther: Volatile.Read(ref m.RejOther),
                ct: ct).ConfigureAwait(false);
        }

        // Snapshot and clear query names
        var querySnapshots = new List<KeyValuePair<QueryNameKey, long>>();
        foreach (var key in _queryCalls.Keys)
        {
            if (_queryCalls.TryRemove(key, out var calls))
            {
                querySnapshots.Add(new KeyValuePair<QueryNameKey, long>(key, calls));
            }
        }

        foreach (var (k, calls) in querySnapshots)
        {
            if (!TableIdentifier.TryParse(k.TableId, out var tableId)) continue;
            var tenantId = new TenantId(k.TenantId);
            await repository.RecordQueryNameAsync(tableId, tenantId, k.Day, k.Application, k.QueryName, calls, ct).ConfigureAwait(false);
        }

        // Snapshot and clear actor samples
        var actorSnapshots = new List<ActorSampleKey>();
        foreach (var key in _actorSamples.Keys)
        {
            if (_actorSamples.TryRemove(key, out _))
            {
                actorSnapshots.Add(key);
            }
        }

        foreach (var a in actorSnapshots)
        {
            if (!TableIdentifier.TryParse(a.TableId, out var tableId)) continue;
            var tenantId = new TenantId(a.TenantId);
            await repository.RecordActorSampleAsync(tableId, tenantId, a.Day, a.Application, a.ActorHash, ct).ConfigureAwait(false);
        }
    }

    public string ComputeActorHmac(string actorSid)
    {
        using var hmac = new HMACSHA256(_hmacKey);
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(actorSid));
        return Convert.ToHexStringLower(hash);
    }
}
