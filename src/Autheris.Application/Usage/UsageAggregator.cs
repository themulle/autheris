namespace Autheris.Application.Usage;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;

public sealed class UsageAggregator : IUsageAggregator
{
    public const int MaxPendingSourceQueries = 50_000;

    private readonly UsageAccountingOptions _options;
    private readonly byte[]? _hmacKey;
    private readonly Func<string, string?>? _expectedCostUnitResolver;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxPendingQueries;
    private readonly ConcurrentQueue<PendingSourceQuery> _pendingQueue = new();
    private int _pendingCount;
    private UsageGeneration _currentGeneration;

    public UsageAggregator(
        IOptions<UsageAccountingOptions>? options = null,
        byte[]? hmacKey = null,
        Func<string, string?>? expectedCostUnitResolver = null,
        TimeProvider? timeProvider = null,
        int maxPendingQueries = MaxPendingSourceQueries)
    {
        _options = options?.Value ?? new UsageAccountingOptions();
        _hmacKey = hmacKey;
        _expectedCostUnitResolver = expectedCostUnitResolver;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxPendingQueries = maxPendingQueries > 0 ? maxPendingQueries : MaxPendingSourceQueries;
        int maxSeries = _options.MaxSeries > 0 ? _options.MaxSeries : UsageAccountingOptions.DefaultMaxSeries;
        _currentGeneration = new UsageGeneration(maxSeries);
        UsageRequestScope.DefaultAggregator ??= this;
    }

    public int PendingSourceQueryCount => Math.Max(0, Volatile.Read(ref _pendingCount));

    public void Aggregate(
        in UsageSeriesKey key,
        long requests,
        long queries,
        long rows,
        bool rowsMeasured,
        long bytes,
        long durationMs,
        long analysisCost,
        long? sourceComputeMs,
        long? sourceCostMicros,
        UsageOutcome outcome)
    {
        var gen = _currentGeneration;
        gen.Enter();
        try
        {
            gen.AddSeries(
                key,
                requests,
                queries,
                rows,
                rowsMeasured,
                bytes,
                durationMs,
                analysisCost,
                sourceComputeMs,
                sourceCostMicros,
                outcome);
        }
        finally
        {
            gen.Exit();
        }
    }

    public void AggregateGrant(
        in UsageGrantSeriesKey key,
        long accesses,
        long queries,
        long rows,
        long bytes,
        long analysisCost,
        long? sourceComputeMs,
        long? sourceCostMicros)
    {
        var gen = _currentGeneration;
        gen.Enter();
        try
        {
            gen.AddGrantSeries(
                key,
                accesses,
                queries,
                rows,
                bytes,
                analysisCost,
                sourceComputeMs,
                sourceCostMicros);
        }
        finally
        {
            gen.Exit();
        }
    }

    public void Record(
        UsageIdentity identity,
        DataSourceTransportFlags transport,
        CallerContext? caller,
        long durationMs,
        long bytes,
        int statusCode,
        IReadOnlyList<UsageFact> facts,
        string? problemCode = null)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var userKey = UsageAttribution.ComputeUserKey(identity.UserSid, _options.PseudonymizeUsers, _hmacKey);
        var (groupSetId, filteredGroups) = UsageAttribution.FilterAndComputeGroupSet(
            identity.GroupSids,
            _options.ExcludedGroupSidPrefixes,
            _options.IncludedGroupSidPrefixes,
            _options.MaxGroupsPerUser);
        var billingGroup = UsageAttribution.ResolveBillingGroup(identity.Tenant, filteredGroups, _options.BillingGroups);
        var app = caller is { IsClientAsserted: true }
            ? CallerLabelNormalizer.UnknownSentinel
            : CallerLabelNormalizer.NormalizeApplication(caller?.Application);
        var clientId = CallerLabelNormalizer.NormalizeClientId(caller?.ClientId);
        var now = _timeProvider.GetUtcNow();
        var dayUtc = DateOnly.FromDateTime(now.UtcDateTime);

        var gen = _currentGeneration;
        gen.Enter();
        try
        {
            gen.RecordGroupSet(identity.Tenant, groupSetId, filteredGroups);

            if (facts == null || facts.Count == 0)
            {
                var outcome = UsageAttribution.MapStatusCodeToOutcome(statusCode, problemCode);
                var key = new UsageSeriesKey(
                    identity.Tenant,
                    dayUtc,
                    userKey,
                    groupSetId,
                    billingGroup,
                    transport,
                    app,
                    clientId,
                    string.Empty,
                    string.Empty);

                gen.AddSeries(
                    key,
                    requests: 1,
                    queries: 0,
                    rows: 0,
                    rowsMeasured: false,
                    bytes: bytes,
                    durationMs: durationMs,
                    analysisCost: 0,
                    sourceComputeMs: null,
                    sourceCostMicros: null,
                    outcome: outcome);
                return;
            }

            for (int i = 0; i < facts.Count; i++)
            {
                var fact = facts[i];
                var tableId = fact.Table.ToString();
                var dataSource = fact.DataSource ?? string.Empty;
                var key = new UsageSeriesKey(
                    identity.Tenant,
                    dayUtc,
                    userKey,
                    groupSetId,
                    billingGroup,
                    transport,
                    app,
                    clientId,
                    dataSource,
                    tableId);

                long factRequests = (i == 0) ? 1 : 0;
                long factBytes = (i == 0) ? bytes : 0;
                long factDuration = (i == 0) ? durationMs : 0;
                long factQueries = 1;
                long factRows = fact.Rows;
                bool factRowsMeasured = fact.RowsMeasured;
                long factAnalysisCost = fact.AnalysisCost;
                long? computeMs = fact.Source.SourceComputeMs;
                long? costMicros = fact.Source.SourceCost.HasValue
                    ? (long)Math.Round(fact.Source.SourceCost.Value * 1_000_000m, MidpointRounding.AwayFromZero)
                    : null;

                if (costMicros.HasValue && !IsSourceUnitValid(dataSource, fact.Source.SourceCostUnit))
                {
                    UsageDiagnostics.SourceUnitMismatchCounter.Add(1);
                    costMicros = null;
                }

                gen.AddSeries(
                    key,
                    factRequests,
                    factQueries,
                    factRows,
                    factRowsMeasured,
                    factBytes,
                    factDuration,
                    factAnalysisCost,
                    computeMs,
                    costMicros,
                    fact.Outcome);

                IReadOnlyList<string> recordedGrantIds = Array.Empty<string>();
                if (fact.Outcome == UsageOutcome.Allow)
                {
                    if (fact.Grants == null || fact.Grants.Count == 0)
                    {
                        RecordGrantEntry(
                            gen,
                            identity.Tenant,
                            dayUtc,
                            dataSource,
                            tableId,
                            GrantRef.Invalid,
                            isInvalid: true,
                            factQueries,
                            factRows,
                            factBytes,
                            factAnalysisCost,
                            computeMs,
                            costMicros);
                        recordedGrantIds = [GrantRef.Invalid.GrantId];
                    }
                    else
                    {
                        var grantList = new List<string>(fact.Grants.Count);
                        foreach (var grant in fact.Grants)
                        {
                            bool isInvalid =
                                string.IsNullOrWhiteSpace(grant.GrantId) ||
                                string.Equals(grant.GrantId, GrantRef.Invalid.GrantId, StringComparison.OrdinalIgnoreCase) ||
                                (grant.ValidUntil.HasValue && grant.ValidUntil.Value < now) ||
                                (grant.ValidFrom.HasValue && grant.ValidFrom.Value > now);

                            var effectiveGrant = isInvalid ? GrantRef.Invalid : grant;
                            RecordGrantEntry(
                                gen,
                                identity.Tenant,
                                dayUtc,
                                dataSource,
                                tableId,
                                effectiveGrant,
                                isInvalid,
                                factQueries,
                                factRows,
                                factBytes,
                                factAnalysisCost,
                                computeMs,
                                costMicros);
                            grantList.Add(effectiveGrant.GrantId);
                        }

                        recordedGrantIds = grantList;
                    }
                }

                if (!string.IsNullOrWhiteSpace(fact.Source.SourceQueryId) &&
                    !string.IsNullOrWhiteSpace(dataSource) &&
                    (!computeMs.HasValue || !costMicros.HasValue))
                {
                    TryEnqueuePendingSourceQuery(new PendingSourceQuery(
                        identity.Tenant,
                        dataSource,
                        fact.Source.SourceQueryId,
                        dayUtc,
                        UsageSeriesHash.Compute(key),
                        key,
                        recordedGrantIds,
                        now));
                }
            }
        }
        finally
        {
            gen.Exit();
        }
    }

    public void RecordUnscoped(TenantId tenant, Sid actor, in UsageFact fact)
    {
        var identity = new UsageIdentity(tenant, actor, new HashSet<Sid>());
        Record(
            identity,
            DataSourceTransportFlags.None,
            caller: null,
            durationMs: 0,
            bytes: 0,
            statusCode: 200,
            facts: [fact]);
    }

    private static void RecordGrantEntry(
        UsageGeneration gen,
        TenantId tenant,
        DateOnly dayUtc,
        string dataSource,
        string tableId,
        GrantRef grant,
        bool isInvalid,
        long factQueries,
        long factRows,
        long factBytes,
        long factAnalysisCost,
        long? computeMs,
        long? costMicros)
    {
        if (isInvalid)
        {
            UsageDiagnostics.AllowWithoutGrantCounter.Add(1);
        }

        gen.RecordGrantRef(tenant, grant);
        var grantKey = new UsageGrantSeriesKey(
            tenant,
            dayUtc,
            grant.GrantId,
            dataSource,
            tableId);

        gen.AddGrantSeries(
            grantKey,
            accesses: 1,
            queries: factQueries,
            rows: factRows,
            bytes: factBytes,
            analysisCost: factAnalysisCost,
            sourceComputeMs: computeMs,
            sourceCostMicros: costMicros);
    }

    public UsageGenerationSnapshot Swap()
    {
        int maxSeries = _options.MaxSeries > 0 ? _options.MaxSeries : UsageAccountingOptions.DefaultMaxSeries;
        var next = new UsageGeneration(maxSeries);
        var old = Interlocked.Exchange(ref _currentGeneration, next);

        var sw = Stopwatch.StartNew();
        var spinner = new SpinWait();
        while (Volatile.Read(ref old.ActiveWriters) > 0 && sw.ElapsedMilliseconds < 100)
        {
            spinner.SpinOnce();
        }

        return old.ToSnapshot();
    }

    public void CollectQuotaUsage(
        TenantId tenant,
        DateOnly fromUtc,
        DateOnly toUtc,
        ICollection<UsageQuotaSeriesEntry> destination,
        IDictionary<string, IReadOnlyList<Sid>> groupSets)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(groupSets);
        var gen = _currentGeneration;
        gen.Enter();
        try
        {
            gen.CollectQuotaUsage(tenant, fromUtc, toUtc, destination, groupSets);
        }
        finally
        {
            gen.Exit();
        }
    }

    public void Merge(UsageGenerationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var gen = _currentGeneration;
        gen.Enter();
        try
        {
            foreach (var entry in snapshot.GroupSets)
            {
                gen.RecordGroupSet(entry.Key.Tenant, entry.Key.GroupSetId, entry.Value);
            }

            foreach (var entry in snapshot.GrantRefs)
            {
                gen.RecordGrantRef(entry.Key.Tenant, entry.Value);
            }

            foreach (var entry in snapshot.Series)
            {
                gen.MergeSeries(entry.Key, entry.Value);
            }

            foreach (var entry in snapshot.GrantSeries)
            {
                gen.MergeGrantSeries(entry.Key, entry.Value);
            }
        }
        finally
        {
            gen.Exit();
        }
    }

    public void AggregateSourceStats(
        in UsageSeriesKey key,
        IReadOnlyList<string>? grantIds,
        long? sourceComputeMs,
        long? sourceCostMicros)
    {
        if (!sourceComputeMs.HasValue && !sourceCostMicros.HasValue)
        {
            return;
        }

        var gen = _currentGeneration;
        gen.Enter();
        try
        {
            gen.AddSeriesSourceStats(key, sourceComputeMs, sourceCostMicros);
            if (grantIds is { Count: > 0 })
            {
                foreach (var grantId in grantIds)
                {
                    if (string.IsNullOrWhiteSpace(grantId))
                    {
                        continue;
                    }

                    var grantKey = new UsageGrantSeriesKey(
                        key.Tenant,
                        key.DayUtc,
                        grantId,
                        key.DataSource,
                        key.TableId);
                    gen.AddGrantSeriesSourceStats(grantKey, sourceComputeMs, sourceCostMicros);
                }
            }
        }
        finally
        {
            gen.Exit();
        }
    }

    public bool TryEnqueuePendingSourceQuery(PendingSourceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        while (true)
        {
            int current = Volatile.Read(ref _pendingCount);
            if (current >= _maxPendingQueries)
            {
                UsageDiagnostics.SourceCostQueueOverflowCounter.Add(1);
                return false;
            }

            if (Interlocked.CompareExchange(ref _pendingCount, current + 1, current) == current)
            {
                _pendingQueue.Enqueue(query);
                return true;
            }
        }
    }

    public IReadOnlyList<PendingSourceQuery> DrainPendingSourceQueries(int maxCount)
    {
        if (maxCount <= 0)
        {
            return Array.Empty<PendingSourceQuery>();
        }

        var drained = new List<PendingSourceQuery>();
        while (drained.Count < maxCount && _pendingQueue.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _pendingCount);
            drained.Add(item);
        }

        return drained;
    }

    public bool IsSourceCostUnitValid(string dataSource, string? sourceCostUnit) =>
        IsSourceUnitValid(dataSource, sourceCostUnit);

    private bool IsSourceUnitValid(string dataSource, string? sourceCostUnit)
    {
        if (_expectedCostUnitResolver == null)
        {
            return true;
        }

        string? expectedUnit = _expectedCostUnitResolver(dataSource);
        if (expectedUnit == null)
        {
            return true;
        }

        return string.Equals(expectedUnit, sourceCostUnit, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class UsageGeneration
    {
        private readonly int _maxSeries;
        private readonly ConcurrentDictionary<UsageSeriesKey, UsageSeriesCounters> _series = new();
        private readonly ConcurrentDictionary<UsageGrantSeriesKey, UsageGrantSeriesCounters> _grantSeries = new();
        private readonly ConcurrentDictionary<(TenantId Tenant, string GroupSetId), IReadOnlyList<Sid>> _groupSets = new();
        private readonly ConcurrentDictionary<(TenantId Tenant, string GrantId), GrantRef> _grantRefs = new();
        public int ActiveWriters;

        public UsageGeneration(int maxSeries)
        {
            _maxSeries = maxSeries;
        }

        public void Enter() => Interlocked.Increment(ref ActiveWriters);

        public void Exit() => Interlocked.Decrement(ref ActiveWriters);

        public void AddSeries(
            in UsageSeriesKey key,
            long requests,
            long queries,
            long rows,
            bool rowsMeasured,
            long bytes,
            long durationMs,
            long analysisCost,
            long? sourceComputeMs,
            long? sourceCostMicros,
            UsageOutcome outcome)
        {
            ResolveSeriesTarget(key, dropWeight: 1)?.Add(
                requests,
                queries,
                rows,
                rowsMeasured,
                bytes,
                durationMs,
                analysisCost,
                sourceComputeMs,
                sourceCostMicros,
                outcome);
        }

        public void MergeSeries(in UsageSeriesKey key, UsageSeriesCounters source)
        {
            long weight = Math.Max(1, source.Requests > 0 ? source.Requests : source.Queries);
            ResolveSeriesTarget(key, dropWeight: weight)?.MergeFrom(source);
        }

        public void AddGrantSeries(
            in UsageGrantSeriesKey key,
            long accesses,
            long queries,
            long rows,
            long bytes,
            long analysisCost,
            long? sourceComputeMs,
            long? sourceCostMicros)
        {
            ResolveGrantSeriesTarget(key)?.Add(accesses, queries, rows, bytes, analysisCost, sourceComputeMs, sourceCostMicros);
        }

        public void AddSeriesSourceStats(in UsageSeriesKey key, long? sourceComputeMs, long? sourceCostMicros)
        {
            ResolveSeriesTarget(key, dropWeight: 1)?.AddSourceStats(sourceComputeMs, sourceCostMicros);
        }

        public void AddGrantSeriesSourceStats(in UsageGrantSeriesKey key, long? sourceComputeMs, long? sourceCostMicros)
        {
            ResolveGrantSeriesTarget(key)?.AddSourceStats(sourceComputeMs, sourceCostMicros);
        }

        public void MergeGrantSeries(in UsageGrantSeriesKey key, UsageGrantSeriesCounters source)
        {
            ResolveGrantSeriesTarget(key)?.MergeFrom(source);
        }

        private UsageSeriesCounters? ResolveSeriesTarget(in UsageSeriesKey key, long dropWeight)
        {
            if (_series.TryGetValue(key, out var counters))
            {
                return counters;
            }

            if (_series.Count >= _maxSeries)
            {
                var foldedKey = key with
                {
                    TableId = "*other*",
                    Application = "*other*",
                    ClientId = string.Empty
                };

                if (_series.TryGetValue(foldedKey, out var foldedCounters))
                {
                    UsageDiagnostics.SeriesOverflowCounter.Add(1);
                    return foldedCounters;
                }

                int headroom = Math.Max(1, _maxSeries / 100);
                if (_series.Count >= _maxSeries + headroom)
                {
                    UsageDiagnostics.DroppedCounter.Add(dropWeight);
                    return null;
                }

                UsageDiagnostics.SeriesOverflowCounter.Add(1);
                return _series.GetOrAdd(foldedKey, _ => new UsageSeriesCounters());
            }

            return _series.GetOrAdd(key, _ => new UsageSeriesCounters());
        }

        private UsageGrantSeriesCounters? ResolveGrantSeriesTarget(in UsageGrantSeriesKey key)
        {
            if (_grantSeries.TryGetValue(key, out var counters))
            {
                return counters;
            }

            if (_grantSeries.Count >= _maxSeries)
            {
                var foldedKey = key with { TableId = "*other*" };
                if (_grantSeries.TryGetValue(foldedKey, out var foldedCounters))
                {
                    UsageDiagnostics.SeriesOverflowCounter.Add(1);
                    return foldedCounters;
                }

                int headroom = Math.Max(1, _maxSeries / 100);
                if (_grantSeries.Count >= _maxSeries + headroom)
                {
                    UsageDiagnostics.DroppedCounter.Add(1);
                    return null;
                }

                UsageDiagnostics.SeriesOverflowCounter.Add(1);
                return _grantSeries.GetOrAdd(foldedKey, _ => new UsageGrantSeriesCounters());
            }

            return _grantSeries.GetOrAdd(key, _ => new UsageGrantSeriesCounters());
        }

        public void RecordGroupSet(TenantId tenant, string groupSetId, IReadOnlyList<Sid> groups)
        {
            _groupSets.TryAdd((tenant, groupSetId), groups);
        }

        public void RecordGrantRef(TenantId tenant, GrantRef grant)
        {
            _grantRefs.TryAdd((tenant, grant.GrantId), grant);
        }

        public UsageGenerationSnapshot ToSnapshot()
        {
            return new UsageGenerationSnapshot(
                new Dictionary<UsageSeriesKey, UsageSeriesCounters>(_series),
                new Dictionary<UsageGrantSeriesKey, UsageGrantSeriesCounters>(_grantSeries),
                new Dictionary<(TenantId Tenant, string GroupSetId), IReadOnlyList<Sid>>(_groupSets),
                new Dictionary<(TenantId Tenant, string GrantId), GrantRef>(_grantRefs));
        }

        public void CollectQuotaUsage(
            TenantId tenant,
            DateOnly fromUtc,
            DateOnly toUtc,
            ICollection<UsageQuotaSeriesEntry> destination,
            IDictionary<string, IReadOnlyList<Sid>> groupSets)
        {
            foreach (var (key, counters) in _series)
            {
                if (key.Tenant == tenant && key.DayUtc >= fromUtc && key.DayUtc <= toUtc)
                {
                    destination.Add(new UsageQuotaSeriesEntry(
                        key.DayUtc,
                        key.UserKey,
                        key.GroupSetId,
                        key.BillingGroup,
                        key.DataSource,
                        counters.Requests,
                        counters.Queries,
                        counters.Rows,
                        counters.Bytes,
                        counters.DurationMs,
                        counters.AnalysisCost,
                        counters.SourceComputeMs,
                        counters.SourceComputeMeasured,
                        counters.SourceCostMicros,
                        counters.Allow));
                }
            }

            foreach (var ((t, groupSetId), sids) in _groupSets)
            {
                if (t == tenant && !groupSets.ContainsKey(groupSetId))
                {
                    groupSets[groupSetId] = sids;
                }
            }
        }
    }
}
