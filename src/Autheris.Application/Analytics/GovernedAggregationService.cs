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
using Microsoft.Extensions.Logging;
using Autheris.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Governed analytical aggregation service (#28, #37).
/// Enforces caller RLS row filters, rejects masked columns in <c>GROUP BY</c>, aggregate arguments, and <c>WHERE</c> filters
/// with <see cref="MaskedAggregationException"/> (<c>AGGREGATION_ON_MASKED_COLUMN</c>), and enforces k-anonymity minimum group size
/// thresholds with differencing-safe grand totals.
/// </summary>
public sealed partial class GovernedAggregationService : IGovernedAggregationService
{
    public const string OtherBucketLabel = "OTHER";

    /// <summary>Fixed caller-facing message for a denied table; carries neither the table name nor the denial reasons.</summary>
    internal const string DeniedMessage = "Access denied.";

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IUnifiedPolicyDecisionPoint? _pdp;
    private readonly ITableAccessResolver? _accessResolver;
    private readonly IGatewayExecutionService? _gatewayExecutionService;
    private readonly IConsentRepository? _consentRepository;
    private readonly IConsentResolutionService? _resolutionService;
    private readonly IGovernedSqlExecutionService? _sqlExecutionService;
    private readonly IEnumerable<IDataSourceExecutor>? _dataSourceExecutors;
    private readonly Autheris.Application.DataSources.IDataSourceTransportGate _transportGate;
    private readonly IAuditWriter? _auditWriter;
    private readonly IGovernedTableReadPlanner _planner;
    private readonly AnalyticsOptions _analyticsOptions;
    private readonly bool _hasExplicitAnalyticsOptions;
    private readonly ICallerAttributeRegistry? _callerAttributeRegistry;
    private readonly ILogger<GovernedAggregationService>? _logger;

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
        ICallerAttributeRegistry? callerAttributeRegistry = null,
        ILogger<GovernedAggregationService>? logger = null,
        Autheris.Application.DataSources.IDataSourceTransportGate? transportGate = null)
    {
        // Issue #447 SEC-ATR-01: no gate means deny, never pass-through.
        _transportGate = transportGate ?? Autheris.Application.DataSources.TransportGateFallback.Current;
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
        _callerAttributeRegistry = callerAttributeRegistry;
        _logger = logger;
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
        ICallerAttributeRegistry? callerAttributeRegistry = null,
        ILogger<GovernedAggregationService>? logger = null,
        Autheris.Application.DataSources.IDataSourceTransportGate? transportGate = null)
    {
        // Issue #447 SEC-ATR-01: no gate means deny, never pass-through.
        _transportGate = transportGate ?? Autheris.Application.DataSources.TransportGateFallback.Current;
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
        _callerAttributeRegistry = callerAttributeRegistry;
        _logger = logger;
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

        var effectiveMinGroup = ResolveEffectiveMinGroupSize(query, metadata);
        int effectiveMinGroupSize = effectiveMinGroup.Value;
        SmallGroupStrategy effectiveStrategy = ResolveEffectiveSmallGroupStrategy(query, metadata);

        IReadOnlyList<string> groupByColumns;
        IReadOnlyList<AggregateSelection> resolvedAggregates;
        try
        {
            groupByColumns = ValidateAndResolveGroupBy(query, metadata, decision);
            resolvedAggregates = ValidateAndResolveAggregates(query, metadata, decision);

            if (effectiveMinGroupSize > 1 && effectiveStrategy != SmallGroupStrategy.MergeIntoOther)
            {
                var groupSet = new HashSet<string>(groupByColumns, StringComparer.OrdinalIgnoreCase);
                foreach (var agg in resolvedAggregates)
                {
                    if (agg.Function is AggregateFunction.Min or AggregateFunction.Max or AggregateFunction.CountDistinct)
                    {
                        if (agg.Column != null && !groupSet.Contains(agg.Column))
                        {
                            throw new ArgumentException(
                                $"Aggregate function '{agg.Function}' on non-group column '{agg.Column}' is not permitted when minimum group size is greater than 1.",
                                nameof(query));
                        }
                    }
                }
            }

            ValidateWhereClause(query.Where, metadata, decision);
            AggregationNegationGuard.Enforce(query.Where, groupByColumns, metadata, effectiveMinGroupSize, _analyticsOptions.Privacy.RestrictNegatedFilters);
            ValidateOrderByClause(query.OrderBy, groupByColumns, resolvedAggregates, metadata, decision);

            // I405-SC-01: the group-key type is refused last, so every access, mask, aggregate, WHERE and noise error wins over it.
            EnsureGroupKeysGroupable(groupByColumns, metadata);
        }
        catch (Exception ex)
        {
            await RecordAuditAsync(tenantId, userSid, query.Table, allowed: false, BuildValidationDenyDetails(ex, query), ct).ConfigureAwait(false);
            throw;
        }

        int maxGroups = _analyticsOptions.Aggregation.MaxGroups > 0
            ? _analyticsOptions.Aggregation.MaxGroups
            : AggregationOptions.DefaultMaxGroups;
        int requestedLimit = query.Limit.HasValue
            ? Math.Min(query.Limit.Value, maxGroups)
            : maxGroups;

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

            // Complementary suppression:
            // If primary suppression occurred (smallBuckets.Count > 0), suppress at least one additional surviving bucket
            // so that subtracting surviving groups from marginal totals cannot isolate the suppressed bucket(s).
            if (smallBuckets.Count > 0 && groupByColumns.Count > 0 && effectiveStrategy != SmallGroupStrategy.MergeIntoOther)
            {
                if (survivingBuckets.Count > 1)
                {
                    var complementary = survivingBuckets.OrderBy(b => b.Count).First();
                    survivingBuckets.Remove(complementary);
                    smallBuckets.Add(complementary);
                }
                else if (survivingBuckets.Count <= 1)
                {
                    smallBuckets.AddRange(survivingBuckets);
                    survivingBuckets.Clear();
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
        long exposedSuppressedRows = _analyticsOptions.Privacy.ExposeSuppressedGroupsCount ? suppressedRowsCount : 0;
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
            RemainingEpsilon: remainingEpsilon,
            MinimumGroupSizeSource: effectiveMinGroup.Source);

        await RecordAuditAsync(tenantId, userSid, query.Table, allowed: true, new Dictionary<string, object?>
        {
            ["is_allowed"] = true,
            ["group_by"] = groupByColumns,
            ["aggregates"] = resolvedAggregates.Select(a => a.EffectiveAlias).ToArray(),
            ["filter"] = query.Where?.SqlPredicate,
            ["minimum_group_size"] = effectiveMinGroupSize,
            ["min_group_size_source"] = effectiveMinGroup.Source.ToString().ToLowerInvariant(),
            ["strategy"] = effectiveStrategy.ToString(),
            ["suppressed"] = suppressed,
            ["suppressed_groups_count"] = suppressedGroupsCount,
            ["suppressed_rows_count"] = suppressedRowsCount,
            ["merged_into_other"] = mergedIntoOther,
            ["noise_applied"] = noiseApplied,
            ["returned_groups_count"] = visibleGroupRows.Count
        }, ct).ConfigureAwait(false);

        return new AggregationResult(
            Table: query.Table,
            Groups: visibleGroupRows,
            Totals: totalsRow,
            Metadata: resultMetadata);
    }
}

