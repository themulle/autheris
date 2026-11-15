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
    private readonly UsageAccountingOptions _options;
    private readonly byte[]? _hmacKey;
    private readonly Func<string, string?>? _expectedCostUnitResolver;
    private readonly TimeProvider _timeProvider;
    private UsageGeneration _currentGeneration;

    public UsageAggregator(
        IOptions<UsageAccountingOptions>? options = null,
        byte[]? hmacKey = null,
        Func<string, string?>? expectedCostUnitResolver = null,
        TimeProvider? timeProvider = null)
    {
        _options = options?.Value ?? new UsageAccountingOptions();
        _hmacKey = hmacKey;
        _expectedCostUnitResolver = expectedCostUnitResolver;
        _timeProvider = timeProvider ?? TimeProvider.System;
        int maxSeries = _options.MaxSeries > 0 ? _options.MaxSeries : UsageAccountingOptions.DefaultMaxSeries;
        _currentGeneration = new UsageGeneration(maxSeries);
        UsageRequestScope.DefaultAggregator ??= this;
    }

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
        IReadOnlyList<UsageFact> facts)
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
                var outcome = UsageAttribution.MapStatusCodeToOutcome(statusCode);
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
                    }
                    else
                    {
                        foreach (var grant in fact.Grants)
                        {
                            bool isInvalid =
                                string.IsNullOrWhiteSpace(grant.GrantId) ||
                                string.Equals(grant.GrantId, GrantRef.Invalid.GrantId, StringComparison.OrdinalIgnoreCase) ||
                                (grant.ValidUntil.HasValue && grant.ValidUntil.Value < now) ||
                                (grant.ValidFrom.HasValue && grant.ValidFrom.Value > now);

                            RecordGrantEntry(
                                gen,
                                identity.Tenant,
                                dayUtc,
                                dataSource,
                                tableId,
                                isInvalid ? GrantRef.Invalid : grant,
                                isInvalid,
                                factQueries,
                                factRows,
                                factBytes,
                                factAnalysisCost,
                                computeMs,
                                costMicros);
                        }
                    }
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
    }
}
