namespace Autheris.Application.Analytics;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Data;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Governed analytical aggregation service (#28, #37).
/// Enforces caller RLS row filters, rejects masked columns in <c>GROUP BY</c>, aggregate arguments, and <c>WHERE</c> filters
/// with <see cref="MaskedAggregationException"/> (<c>AGGREGATION_ON_MASKED_COLUMN</c>), and enforces k-anonymity minimum group size
/// thresholds with differencing-safe grand totals and opt-in differential privacy Laplace noise.
/// </summary>
public sealed partial class GovernedAggregationService : IGovernedAggregationService
{
    public const string OtherBucketLabel = "OTHER";

    /// <summary>Fixed caller-facing message for a denied table; carries neither the table name nor the denial reasons.</summary>
    internal const string DeniedMessage = "Access denied.";

    /// <summary>Test seam: replaces the Laplace sampler (argument is the scale) used for differential privacy noise.</summary>
    internal Func<double, double>? NoiseSampler { get; init; }

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IUnifiedPolicyDecisionPoint? _pdp;
    private readonly ITableAccessResolver? _accessResolver;
    private readonly IGatewayExecutionService? _gatewayExecutionService;
    private readonly IConsentRepository? _consentRepository;
    private readonly IConsentResolutionService? _resolutionService;
    private readonly IGovernedSqlExecutionService? _sqlExecutionService;
    private readonly IEnumerable<IDataSourceExecutor>? _dataSourceExecutors;
    private readonly IAuditWriter? _auditWriter;
    private readonly IGovernedTableReadPlanner _planner;
    private readonly AnalyticsOptions _analyticsOptions;
    private readonly bool _hasExplicitAnalyticsOptions;
    private readonly IDifferentialPrivacyEngine? _dpEngine;
    private readonly ConcurrentDictionary<string, (string UtcDay, AggregationResult Result)> _noisyResultCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _noisyInsertionOrder = new();
    private readonly object _noisyCacheLock = new();
    private readonly ICallerAttributeRegistry? _callerAttributeRegistry;

    [ActivatorUtilitiesConstructor]
    public GovernedAggregationService(
        ITableMetadataRepository metadataRepository,
        IAuditLogRepository auditLogRepository,
        IUnifiedPolicyDecisionPoint? pdp = null,
        ITableAccessResolver? accessResolver = null,
        IGatewayExecutionService? gatewayExecutionService = null,
        IConsentRepository? consentRepository = null,
        IConsentResolutionService? resolutionService = null,
        IGovernedSqlExecutionService? sqlExecutionService = null,
        IEnumerable<IDataSourceExecutor>? dataSourceExecutors = null,
        IAuditWriter? auditWriter = null,
        IOptions<AnalyticsOptions>? analyticsOptions = null,
        IGovernedTableReadPlanner? planner = null,
        IDifferentialPrivacyEngine? dpEngine = null,
        ICallerAttributeRegistry? callerAttributeRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(auditLogRepository);
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _pdp = pdp;
        _accessResolver = accessResolver ?? (gatewayExecutionService as ITableAccessResolver);
        _gatewayExecutionService = gatewayExecutionService;
        _consentRepository = consentRepository ?? (metadataRepository as IConsentRepository);
        _resolutionService = resolutionService;
        _sqlExecutionService = sqlExecutionService;
        _dataSourceExecutors = dataSourceExecutors;
        _auditWriter = auditWriter ?? new AuditWriter(auditLogRepository, NullLogger<AuditWriter>.Instance);
        _planner = planner ?? new GovernedTableReadPlanner();
        _hasExplicitAnalyticsOptions = analyticsOptions?.Value != null;
        _analyticsOptions = analyticsOptions?.Value ?? new AnalyticsOptions();
        _dpEngine = dpEngine ?? new DifferentialPrivacyEngine(auditLogRepository, analyticsOptions: analyticsOptions);
        _callerAttributeRegistry = callerAttributeRegistry;
    }

    public GovernedAggregationService(
        ITableMetadataRepository metadataRepository,
        IUnifiedPolicyDecisionPoint? pdp = null,
        ITableAccessResolver? accessResolver = null,
        IGatewayExecutionService? gatewayExecutionService = null,
        IConsentRepository? consentRepository = null,
        IConsentResolutionService? resolutionService = null,
        IGovernedSqlExecutionService? sqlExecutionService = null,
        IEnumerable<IDataSourceExecutor>? dataSourceExecutors = null,
        IAuditWriter? auditWriter = null,
        IOptions<AnalyticsOptions>? analyticsOptions = null,
        IGovernedTableReadPlanner? planner = null,
        IDifferentialPrivacyEngine? dpEngine = null,
        ICallerAttributeRegistry? callerAttributeRegistry = null)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _pdp = pdp;
        _accessResolver = accessResolver ?? (gatewayExecutionService as ITableAccessResolver);
        _gatewayExecutionService = gatewayExecutionService;
        _consentRepository = consentRepository ?? (metadataRepository as IConsentRepository);
        _resolutionService = resolutionService;
        _sqlExecutionService = sqlExecutionService;
        _dataSourceExecutors = dataSourceExecutors;
        _auditWriter = auditWriter ?? (metadataRepository is IAuditLogRepository repoAudit ? new AuditWriter(repoAudit, NullLogger<AuditWriter>.Instance) : null);
        _planner = planner ?? new GovernedTableReadPlanner();
        _hasExplicitAnalyticsOptions = analyticsOptions?.Value != null;
        _analyticsOptions = analyticsOptions?.Value ?? new AnalyticsOptions();
        _dpEngine = dpEngine ?? (metadataRepository is IAuditLogRepository dpAudit ? new DifferentialPrivacyEngine(dpAudit, analyticsOptions: analyticsOptions) : null);
        _callerAttributeRegistry = callerAttributeRegistry;
    }

    public async Task<AggregationResult> ExecuteAggregationAsync(
        ClaimsPrincipal principal,
        AggregationQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (principal == null || principal.Identity?.IsAuthenticated != true)
        {
            throw new GatewayUnauthorizedException("Authentication is required to execute governed aggregation queries.");
        }

        var userSid = principal.GetUserSid()
            ?? throw new GatewayUnauthorizedException("No valid user SID in the authentication token.");
        var tenantId = principal.GetTenantId();

        AggregationCostCalculator.ValidateBounds(query, _analyticsOptions.Aggregation);

        var metadata = await _metadataRepository.GetTableMetadataAsync(query.Table, ct).ConfigureAwait(false)
            ?? VirtualSystemTables.Get(query.Table);
        if (metadata == null || !metadata.Table.IsActive)
        {
            throw new TableNotFoundException(query.Table);
        }

        GovernedQueryStages.EnsureCanonicalClusterAdminClaim(principal, query.Table);

        var decision = await ResolveDecisionAsync(principal, userSid, tenantId, metadata, query, ct).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            await RecordAuditAsync(tenantId, userSid, query.Table, allowed: false, new Dictionary<string, object?>
            {
                ["is_allowed"] = false,
                ["reasons"] = decision.DeniedReasons
            }, ct).ConfigureAwait(false);

            // WP-0 (W0-4): the caller-facing message is fixed. The table name and the denial reasons stay in the audit entry above, so a denied
            // table is not distinguishable from a missing one by message content.
            throw new GatewayForbiddenException(DeniedMessage);
        }

        int effectiveMinGroupSize = ResolveEffectiveMinGroupSize(query, metadata);
        SmallGroupStrategy effectiveStrategy = ResolveEffectiveSmallGroupStrategy(query, metadata);

        IReadOnlyList<string> groupByColumns;
        IReadOnlyList<AggregateSelection> resolvedAggregates;
        try
        {
            groupByColumns = ValidateAndResolveGroupBy(query, metadata, decision);
            resolvedAggregates = ValidateAndResolveAggregates(query, metadata, decision);
            ValidateWhereClause(query.Where, metadata, decision);
            AggregationNegationGuard.Enforce(query.Where, groupByColumns, metadata, effectiveMinGroupSize, _analyticsOptions.Privacy.RestrictNegatedFilters);
            ValidateOrderByClause(query.OrderBy, groupByColumns, resolvedAggregates, metadata, decision);
            if (_analyticsOptions.Privacy.Noise.Enabled)
            {
                ValidateAggregatesForNoise(resolvedAggregates);
                ValidateMeasureBoundsForNoise(resolvedAggregates, metadata);
            }
        }
        catch (Exception ex)
        {
            await RecordAuditAsync(tenantId, userSid, query.Table, allowed: false, new Dictionary<string, object?>
            {
                ["is_allowed"] = false,
                ["reasons"] = new[] { ex.Message },
                ["group_by"] = query.GroupBy,
                ["filter"] = query.Where?.SqlPredicate
            }, ct).ConfigureAwait(false);
            throw;
        }

        int maxGroups = _analyticsOptions.Aggregation.MaxGroups > 0
            ? _analyticsOptions.Aggregation.MaxGroups
            : AggregationOptions.DefaultMaxGroups;
        int requestedLimit = query.Limit.HasValue
            ? Math.Min(query.Limit.Value, maxGroups)
            : maxGroups;

        string? noiseCacheKey = null;
        string utcDay = DateTimeOffset.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (_analyticsOptions.Privacy.Noise.Enabled)
        {
            // SEC-112-21/-26: the key carries the decision fingerprint (bound values + caller-attribute generation).
            // An attribute-resolution fault means no cache reuse: the request is neither served from nor stored in the cache.
            var generation = await TryGetAttributeGenerationAsync(ct).ConfigureAwait(false);
            if (generation.HasValue)
            {
                noiseCacheKey = NoisyCacheKey.Build(tenantId, userSid, metadata.Identifier, groupByColumns, resolvedAggregates, query.Where, query.OrderBy, requestedLimit, effectiveMinGroupSize, effectiveStrategy, utcDay, NoisyCacheKey.Fingerprint(decision, metadata, generation.Value), query.TimeBuckets);
                if (_noisyResultCache.TryGetValue(noiseCacheKey, out var cached) && string.Equals(cached.UtcDay, utcDay, StringComparison.Ordinal))
                {
                    return cached.Result;
                }
            }
        }

        var bucketRead = await ExecuteAndBuildBucketsAsync(
            principal,
            tenantId,
            metadata,
            decision,
            query,
            groupByColumns,
            resolvedAggregates,
            ct).ConfigureAwait(false);
        var rawBuckets = bucketRead.Buckets;

        var survivingBuckets = new List<ComputedBucket>(rawBuckets.Count);
        var smallBuckets = new List<ComputedBucket>();

        if (effectiveMinGroupSize > 1)
        {
            foreach (var bucket in rawBuckets)
            {
                if (bucket.Count >= effectiveMinGroupSize)
                {
                    survivingBuckets.Add(bucket);
                }
                else if (bucket.Count > 0 || groupByColumns.Count > 0)
                {
                    smallBuckets.Add(bucket);
                }
            }
        }
        else
        {
            survivingBuckets.AddRange(rawBuckets);
        }

        bool suppressed = false;
        int suppressedGroupsCount = 0;
        long suppressedRowsCount = 0;
        bool mergedIntoOther = false;
        ComputedBucket? mergedOtherBucket = null;

        if (effectiveMinGroupSize > 1 && smallBuckets.Count > 0)
        {
            if (effectiveStrategy == SmallGroupStrategy.MergeIntoOther && groupByColumns.Count > 0)
            {
                long combinedSmallCount = smallBuckets.Sum(b => b.Count);
                if (combinedSmallCount >= effectiveMinGroupSize)
                {
                    var otherKey = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var gCol in groupByColumns)
                    {
                        otherKey[gCol] = OtherBucketLabel;
                    }

                    var combinedRows = smallBuckets.SelectMany(b => b.Rows).ToList();
                    var mergedRaw = combinedRows.Count > 0
                        ? ComputeBucketFromRows(otherKey, combinedRows, resolvedAggregates, metadata, isMergedOther: true)
                        : MergePreAggregatedBuckets(otherKey, smallBuckets, resolvedAggregates, isMergedOther: true);
                    mergedOtherBucket = WithoutNonMergeableValues(mergedRaw, resolvedAggregates);

                    mergedIntoOther = true;
                }
                else
                {
                    suppressed = true;
                    suppressedGroupsCount = smallBuckets.Count;
                    suppressedRowsCount = combinedSmallCount;
                }
            }
            else
            {
                suppressed = true;
                suppressedGroupsCount = smallBuckets.Count;
                suppressedRowsCount = smallBuckets.Sum(b => b.Count);
            }
        }

        bool noiseApplied = false;
        double? epsilonConsumed = null;
        double? remainingEpsilon = null;

        if (_analyticsOptions.Privacy.Noise.Enabled)
        {
            // WP-0 (SEC-112-03): noise is applied before ordering and the limit cut, so order and cut follow the noised values and never
            // reveal the order of the true values.
            var toPerturb = new List<ComputedBucket>(survivingBuckets);
            if (mergedOtherBucket != null)
            {
                toPerturb.Add(mergedOtherBucket);
            }

            var (noisyBuckets, consumed, remaining) = await ApplyDifferentialPrivacyNoiseAsync(
                userSid,
                toPerturb,
                resolvedAggregates,
                metadata,
                effectiveMinGroupSize,
                ct).ConfigureAwait(false);
            survivingBuckets = noisyBuckets.Where(b => !b.IsMergedOther).ToList();
            mergedOtherBucket = noisyBuckets.FirstOrDefault(b => b.IsMergedOther);
            noiseApplied = true;
            epsilonConsumed = consumed;
            remainingEpsilon = remaining;
        }

        var orderedBuckets = OrderBuckets(survivingBuckets, query.OrderBy, groupByColumns, resolvedAggregates);
        if (mergedOtherBucket != null)
        {
            orderedBuckets.Add(mergedOtherBucket);
        }

        bool truncated = bucketRead.SourceTruncated || orderedBuckets.Count > requestedLimit;
        if (orderedBuckets.Count > requestedLimit)
        {
            orderedBuckets = orderedBuckets.Take(requestedLimit).ToList();
        }

        var visibleGroupRows = orderedBuckets.Select(b => b.ToGroupRow()).ToList();

        // Differencing protection (#37): Totals are computed strictly over the surviving visible groups.
        var totalsRow = ComputeDifferencingSafeTotals(orderedBuckets, resolvedAggregates, metadata);

        // WP-0 (SEC-112-04): the exact number of suppressed rows can be the exact size of a single group below k, so it is gated by the same
        // option as the groups count and is never returned next to noised values. The true values stay in the audit entry below.
        int exposedSuppressedGroups = _analyticsOptions.Privacy.ExposeSuppressedGroupsCount ? suppressedGroupsCount : 0;
        long exposedSuppressedRows = _analyticsOptions.Privacy.ExposeSuppressedGroupsCount && !noiseApplied ? suppressedRowsCount : 0;
        var resultMetadata = new AggregationMetadata(
            MinimumGroupSize: effectiveMinGroupSize,
            Strategy: effectiveStrategy,
            Suppressed: suppressed,
            SuppressedGroupsCount: exposedSuppressedGroups,
            SuppressedRowsCount: exposedSuppressedRows,
            MergedIntoOther: mergedIntoOther,
            Truncated: truncated,
            NoiseApplied: noiseApplied,
            EpsilonConsumed: epsilonConsumed,
            RemainingEpsilon: remainingEpsilon);

        await RecordAuditAsync(tenantId, userSid, query.Table, allowed: true, new Dictionary<string, object?>
        {
            ["is_allowed"] = true,
            ["group_by"] = groupByColumns,
            ["aggregates"] = resolvedAggregates.Select(a => a.EffectiveAlias).ToArray(),
            ["filter"] = query.Where?.SqlPredicate,
            ["minimum_group_size"] = effectiveMinGroupSize,
            ["strategy"] = effectiveStrategy.ToString(),
            ["suppressed"] = suppressed,
            ["suppressed_groups_count"] = suppressedGroupsCount,
            ["suppressed_rows_count"] = suppressedRowsCount,
            ["merged_into_other"] = mergedIntoOther,
            ["noise_applied"] = noiseApplied,
            ["returned_groups_count"] = visibleGroupRows.Count
        }, ct).ConfigureAwait(false);

        var finalResult = new AggregationResult(
            Table: query.Table,
            Groups: visibleGroupRows,
            Totals: totalsRow,
            Metadata: resultMetadata);

        if (noiseApplied && noiseCacheKey != null)
        {
            StoreNoisyResult(noiseCacheKey, utcDay, finalResult);
        }

        return finalResult;
    }

    private readonly ConcurrentDictionary<string, double> _fallbackBudgetConsumed = new(StringComparer.Ordinal);

    /// <summary>
    /// WP-0 (SEC-112-03): with noise enabled the client controls the aggregate list, so repeated identical aggregates (independent noise draws that average
    /// out) and un-noised <c>MIN</c>/<c>MAX</c> values are rejected before any data is read.
    /// </summary>
    private static void ValidateAggregatesForNoise(IReadOnlyList<AggregateSelection> aggregates)
    {
        var seen = new HashSet<(AggregateFunction Function, string Column)>(new FunctionColumnComparer());
        foreach (var agg in aggregates)
        {
            if (agg.Function is AggregateFunction.Min or AggregateFunction.Max)
            {
                throw new GatewayInvalidQueryException(
                    "MIN and MAX aggregates are not available while differential privacy noise is enabled.", "AGGREGATION_ARGUMENT_INVALID");
            }

            if (!seen.Add((agg.Function, agg.Column ?? string.Empty)))
            {
                throw new GatewayInvalidQueryException(
                    "Repeating an aggregate function over the same column is not available while differential privacy noise is enabled.", "AGGREGATION_ARGUMENT_INVALID");
            }
        }
    }

    private sealed class FunctionColumnComparer : IEqualityComparer<(AggregateFunction Function, string Column)>
    {
        public bool Equals((AggregateFunction Function, string Column) x, (AggregateFunction Function, string Column) y) =>
            x.Function == y.Function && string.Equals(x.Column, y.Column, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((AggregateFunction Function, string Column) obj) =>
            HashCode.Combine(obj.Function, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Column));
    }

    private void ValidateMeasureBoundsForNoise(IReadOnlyList<AggregateSelection> aggregates, TableMetadata metadata)
    {
        foreach (var agg in aggregates)
        {
            if (agg.Function is AggregateFunction.Sum or AggregateFunction.Avg && !string.IsNullOrWhiteSpace(agg.Column))
            {
                _ = ResolveMeasureBounds(metadata, agg.Column!);
            }
        }
    }

    private MeasureBoundsOptions ResolveMeasureBounds(TableMetadata metadata, string column)
    {
        var boundsMap = _analyticsOptions.Privacy.Noise.MeasureBounds;
        if (boundsMap != null)
        {
            string[] candidates = [$"{metadata.Identifier}.{column}", $"{metadata.Identifier.ToQualifiedName()}.{column}", $"{metadata.Table.TableName}.{column}", column];
            foreach (var key in candidates)
            {
                if (boundsMap.TryGetValue(key, out var b) && b is { Max: var max, Min: var min } && max > min)
                {
                    return b;
                }
            }
        }

        throw new GatewayInvalidQueryException($"Differential privacy noise requires configured MeasureBounds for column '{column}'.");
    }

    private async ValueTask<long?> TryGetAttributeGenerationAsync(CancellationToken ct)
    {
        if (_callerAttributeRegistry is null)
        {
            return 0L;
        }

        try
        {
            return (await _callerAttributeRegistry.GetAsync(ct).ConfigureAwait(false)).Generation;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Stores a noisy result; a full cache evicts only the oldest entry (never the whole cache).</summary>
    private void StoreNoisyResult(string key, string utcDay, AggregationResult result)
    {
        var max = Math.Max(1, _analyticsOptions.Privacy.Noise.CacheMaxEntries);
        lock (_noisyCacheLock)
        {
            if (!_noisyResultCache.ContainsKey(key))
            {
                while (_noisyInsertionOrder.Count >= max && _noisyInsertionOrder.TryDequeue(out var oldest))
                {
                    _noisyResultCache.TryRemove(oldest, out _);
                }

                _noisyInsertionOrder.Enqueue(key);
            }

            _noisyResultCache[key] = (utcDay, result);
        }
    }

    private async Task<(List<ComputedBucket> Buckets, double ConsumedEpsilon, double RemainingEpsilon)> ApplyDifferentialPrivacyNoiseAsync(
        Sid userSid, List<ComputedBucket> buckets, IReadOnlyList<AggregateSelection> aggregates,
        TableMetadata metadata, int effectiveMinGroupSize, CancellationToken ct)
    {
        double epsilon = _analyticsOptions.Privacy.Noise.Epsilon > 0 ? _analyticsOptions.Privacy.Noise.Epsilon : 1.0;
        double dailyBudget = _analyticsOptions.Privacy.Noise.DailyBudgetEpsilon > 0 ? _analyticsOptions.Privacy.Noise.DailyBudgetEpsilon : 10.0;

        // WP-0 (SEC-112-03): every noised value spends epsilon (sequential composition): one draw per column aggregate plus one for the row count.
        double charge = epsilon * (aggregates.Count(a => a.Column != null) + 1);

        double consumedEpsilon;
        double remainingEpsilon;
        if (_dpEngine != null)
        {
            var perturbRes = await _dpEngine.PerturbAsync(
                new DifferentialPrivacyPerturbationRequest(userSid.Value, 0.0, Epsilon: charge, Sensitivity: 1.0), ct).ConfigureAwait(false);
            consumedEpsilon = perturbRes.ConsumedEpsilon;
            remainingEpsilon = perturbRes.RemainingEpsilon;
        }
        else
        {
            var dayKey = $"{userSid.Value}:{DateTimeOffset.UtcNow:yyyyMMdd}";
            double newTotal = _fallbackBudgetConsumed.AddOrUpdate(dayKey, charge, (_, prev) => prev + charge);
            if (newTotal > dailyBudget + 1e-9)
            {
                _fallbackBudgetConsumed.AddOrUpdate(dayKey, 0.0, (_, prev) => Math.Max(0.0, prev - charge));
                throw new PrivacyBudgetExhaustedException(userSid.Value, newTotal, dailyBudget);
            }
            consumedEpsilon = newTotal;
            remainingEpsilon = Math.Max(0.0, dailyBudget - newTotal);
        }

        var noisyBuckets = new List<ComputedBucket>(buckets.Count);
        foreach (var bucket in buckets)
        {
            long countDelta = (long)Math.Round(SampleNoise(1.0 / epsilon));
            if (countDelta == 0) countDelta = 1L;
            long noisyCount = Math.Max(1L, bucket.Count + countDelta);

            var noisyAggs = new Dictionary<string, object?>(bucket.Aggregates, StringComparer.OrdinalIgnoreCase);
            var noisySums = new Dictionary<string, object?>(bucket.Sum, StringComparer.OrdinalIgnoreCase);
            var noisyAvgs = new Dictionary<string, object?>(bucket.Avg, StringComparer.OrdinalIgnoreCase);

            foreach (var agg in aggregates)
            {
                var alias = agg.EffectiveAlias;
                if (agg.Column == null)
                {
                    noisyAggs[alias] = noisyCount;
                    continue;
                }

                // WP-0 (W0-6): the merged OTHER row keeps null for the non-mergeable aggregates on every path, noise included.
                if (bucket.IsMergedOther && agg.Function is AggregateFunction.CountDistinct or AggregateFunction.Avg)
                {
                    noisyAggs[alias] = null;
                    continue;
                }

                switch (agg.Function)
                {
                    case AggregateFunction.Count:
                    case AggregateFunction.CountDistinct:
                        long rawCnt = noisyAggs.TryGetValue(alias, out var cv) && cv != null
                            ? Convert.ToInt64(cv, CultureInfo.InvariantCulture)
                            : bucket.Count;
                        long cDelta = (long)Math.Round(SampleNoise(1.0 / epsilon));
                        if (cDelta == 0) cDelta = 1L;
                        noisyAggs[alias] = Math.Max(0L, rawCnt + cDelta);
                        break;

                    case AggregateFunction.Sum:
                        var sumBounds = ResolveMeasureBounds(metadata, agg.Column);
                        double sumSens = Math.Max(0.01, sumBounds.Max - sumBounds.Min);
                        decimal rawSum = noisyAggs.TryGetValue(alias, out var sv) && sv != null ? ToDecimal(sv) : 0m;
                        decimal sumNoise = Math.Round((decimal)SampleNoise(sumSens / epsilon), 4);
                        if (sumNoise == 0m) sumNoise = 0.01m;
                        noisyAggs[alias] = rawSum + sumNoise;
                        noisySums[agg.Column] = rawSum + sumNoise;
                        break;

                    case AggregateFunction.Avg:
                        var avgBounds = ResolveMeasureBounds(metadata, agg.Column);
                        double avgSens = Math.Max(0.01, (avgBounds.Max - avgBounds.Min) / Math.Max(1, effectiveMinGroupSize));
                        decimal rawAvg = noisyAggs.TryGetValue(alias, out var av) && av != null ? ToDecimal(av) : 0m;
                        decimal avgNoise = Math.Round((decimal)SampleNoise(avgSens / epsilon), 4);
                        if (avgNoise == 0m) avgNoise = 0.01m;
                        noisyAggs[alias] = rawAvg + avgNoise;
                        noisyAvgs[agg.Column] = rawAvg + avgNoise;
                        break;
                }
            }

            noisyBuckets.Add(new ComputedBucket(
                bucket.GroupKey, noisyCount, noisyAggs, noisySums, noisyAvgs, bucket.Min, bucket.Max, [], bucket.IsMergedOther));
        }

        return (noisyBuckets, consumedEpsilon, remainingEpsilon);
    }

    private double SampleNoise(double scale) => NoiseSampler != null ? NoiseSampler(scale) : SampleLaplace(scale);

    private static double SampleLaplace(double scale)
    {
        int r = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        double u = ((double)r / int.MaxValue) - 0.5;
        if (Math.Abs(u) < 1e-12) u = 1e-12;
        return -scale * Math.Sign(u) * Math.Log(1.0 - (2.0 * Math.Abs(u)));
    }
}

