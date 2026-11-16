namespace Autheris.Application.Usage;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>In-memory hot-path quota evaluator backed by periodic repository refreshes and live aggregator counters.</summary>
public sealed class UsageQuotaService : IUsageQuotaService
{
    private const int UnavailableRetryAfterSeconds = 30;

    private readonly IUsageRepository _repository;
    private readonly IUsageAggregator _aggregator;
    private readonly UsageAccountingOptions _options;
    private readonly byte[]? _hmacKey;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UsageQuotaService> _logger;
    private readonly ConcurrentDictionary<TenantId, CachedTenantSnapshot> _snapshots = new();
    private long _lastHealthyFullRefreshTicks;

    public UsageQuotaService(
        IUsageRepository repository,
        IUsageAggregator aggregator,
        IOptions<UsageAccountingOptions> options,
        byte[]? hmacKey = null,
        TimeProvider? timeProvider = null,
        ILogger<UsageQuotaService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(aggregator);
        ArgumentNullException.ThrowIfNull(options);

        _repository = repository;
        _aggregator = aggregator;
        _options = options.Value;
        _hmacKey = hmacKey;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<UsageQuotaService>.Instance;
    }

    public UsageQuotaDecision Evaluate(UsageIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var quotas = _options.Quotas;
        if (quotas is null || quotas.Count == 0)
        {
            return UsageQuotaDecision.AllowDecision;
        }

        var userKey = UsageAttribution.ComputeUserKey(identity.UserSid, _options.PseudonymizeUsers, _hmacKey);
        var (_, filteredGroups) = UsageAttribution.FilterAndComputeGroupSet(
            identity.GroupSids,
            _options.ExcludedGroupSidPrefixes,
            _options.IncludedGroupSidPrefixes,
            _options.MaxGroupsPerUser);
        var billingGroup = UsageAttribution.ResolveBillingGroup(identity.Tenant, filteredGroups, _options.BillingGroups);

        var applicable = CollectApplicableQuotas(quotas, identity.Tenant, identity.UserSid, filteredGroups, billingGroup);
        if (applicable.Count == 0)
        {
            return UsageQuotaDecision.AllowDecision;
        }

        var now = _timeProvider.GetUtcNow();
        var staleness = TimeSpan.FromSeconds(
            _options.QuotaStalenessSeconds > 0 ? _options.QuotaStalenessSeconds : UsageAccountingOptions.DefaultQuotaStalenessSeconds);

        if (!TryGetFreshSnapshot(identity.Tenant, now, staleness, out var dbSnapshot))
        {
            return new UsageQuotaDecision(UsageQuotaDecisionKind.Unavailable, RetryAfterSeconds: UnavailableRetryAfterSeconds);
        }

        var todayUtc = DateOnly.FromDateTime(now.UtcDateTime);
        var monthStartUtc = new DateOnly(todayUtc.Year, todayUtc.Month, 1);

        var mergedEntries = new List<UsageQuotaSeriesEntry>(dbSnapshot.Series.Count + 16);
        mergedEntries.AddRange(dbSnapshot.Series);
        var mergedGroupSets = new Dictionary<string, IReadOnlyList<Sid>>(dbSnapshot.GroupSets, StringComparer.Ordinal);
        _aggregator.CollectQuotaUsage(identity.Tenant, monthStartUtc, todayUtc, mergedEntries, mergedGroupSets);

        return EvaluateApplicableQuotas(
            identity.Tenant,
            userKey,
            filteredGroups,
            billingGroup,
            applicable,
            mergedEntries,
            mergedGroupSets,
            dbSnapshot.Tariffs,
            now,
            todayUtc,
            monthStartUtc);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var quotas = _options.Quotas;
        if (quotas is null || quotas.Count == 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var todayUtc = DateOnly.FromDateTime(now.UtcDateTime);
        var monthStartUtc = new DateOnly(todayUtc.Year, todayUtc.Month, 1);

        var targetTenants = new HashSet<TenantId>();
        bool hasWildcardTenant = false;
        foreach (var quota in quotas)
        {
            if (quota is null)
            {
                continue;
            }

            if (string.Equals(quota.Tenant, "*", StringComparison.Ordinal))
            {
                hasWildcardTenant = true;
            }
            else if (TenantId.TryParse(quota.Tenant, out var explicitTenant))
            {
                targetTenants.Add(explicitTenant);
            }

            if (quota.Scope == UsageQuotaScope.Tenant
                && !string.Equals(quota.Subject, "*", StringComparison.Ordinal)
                && TenantId.TryParse(quota.Subject, out var subjectTenant))
            {
                targetTenants.Add(subjectTenant);
            }
        }

        bool wildcardDiscoveryOk = true;
        if (hasWildcardTenant)
        {
            try
            {
                var active = await _repository.GetActiveUsageTenantsAsync(monthStartUtc, todayUtc, ct).ConfigureAwait(false);
                foreach (var t in active)
                {
                    targetTenants.Add(t);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                wildcardDiscoveryOk = false;
                _logger.LogWarning(ex, "Usage quota active-tenant discovery failed; keeping existing cached snapshots.");
            }
        }

        bool allTargetsOk = wildcardDiscoveryOk;
        foreach (var tenant in targetTenants)
        {
            try
            {
                var snap = await _repository.GetQuotaSnapshotAsync(tenant, monthStartUtc, todayUtc, ct).ConfigureAwait(false);
                _snapshots[tenant] = new CachedTenantSnapshot(snap, _timeProvider.GetUtcNow());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                allTargetsOk = false;
                _logger.LogWarning(ex, "Usage quota snapshot refresh failed for tenant {Tenant}.", tenant.Value);
            }
        }

        if (allTargetsOk)
        {
            Volatile.Write(ref _lastHealthyFullRefreshTicks, _timeProvider.GetUtcNow().UtcTicks);
        }
    }

    private bool TryGetFreshSnapshot(
        TenantId tenant,
        DateTimeOffset now,
        TimeSpan staleness,
        out UsageQuotaTenantSnapshot snapshot)
    {
        if (_snapshots.TryGetValue(tenant, out var cached) && now - cached.RefreshedAtUtc <= staleness)
        {
            snapshot = cached.Snapshot;
            return true;
        }

        long fullRefreshTicks = Volatile.Read(ref _lastHealthyFullRefreshTicks);
        if (fullRefreshTicks > 0 && cached is null)
        {
            var refreshedAt = new DateTimeOffset(fullRefreshTicks, TimeSpan.Zero);
            if (now - refreshedAt <= staleness)
            {
                snapshot = UsageQuotaTenantSnapshot.Empty;
                return true;
            }
        }

        snapshot = UsageQuotaTenantSnapshot.Empty;
        return false;
    }

    private static List<UsageQuota> CollectApplicableQuotas(
        List<UsageQuota> quotas,
        TenantId tenant,
        Sid userSid,
        IReadOnlyList<Sid> filteredGroups,
        string billingGroup)
    {
        var applicable = new List<UsageQuota>();
        foreach (var quota in quotas)
        {
            if (quota is null)
            {
                continue;
            }

            if (!string.Equals(quota.Tenant, "*", StringComparison.Ordinal)
                && !string.Equals(quota.Tenant, tenant.Value, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (MatchesSubject(quota, tenant, userSid, filteredGroups, billingGroup))
            {
                applicable.Add(quota);
            }
        }

        return applicable;
    }

    private static bool MatchesSubject(
        UsageQuota quota,
        TenantId tenant,
        Sid userSid,
        IReadOnlyList<Sid> filteredGroups,
        string billingGroup) =>
        quota.Scope switch
        {
            UsageQuotaScope.User =>
                string.Equals(quota.Subject, "*", StringComparison.Ordinal)
                || string.Equals(quota.Subject, userSid.Value, StringComparison.OrdinalIgnoreCase),
            UsageQuotaScope.Group =>
                string.Equals(quota.Subject, "*", StringComparison.Ordinal)
                    ? filteredGroups.Count > 0
                    : ContainsSid(filteredGroups, quota.Subject),
            UsageQuotaScope.BillingGroup =>
                !string.IsNullOrEmpty(billingGroup)
                && !string.Equals(billingGroup, UsageAttribution.UnassignedBillingGroup, StringComparison.Ordinal)
                && (string.Equals(quota.Subject, "*", StringComparison.Ordinal)
                    || string.Equals(billingGroup, quota.Subject, StringComparison.OrdinalIgnoreCase)),
            UsageQuotaScope.Tenant =>
                string.Equals(quota.Subject, "*", StringComparison.Ordinal)
                || string.Equals(quota.Subject, tenant.Value, StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private UsageQuotaDecision EvaluateApplicableQuotas(
        TenantId tenant,
        string callerUserKey,
        IReadOnlyList<Sid> callerGroups,
        string callerBillingGroup,
        List<UsageQuota> applicable,
        List<UsageQuotaSeriesEntry> entries,
        Dictionary<string, IReadOnlyList<Sid>> groupSets,
        IReadOnlyList<UsageTariffVersion> tariffs,
        DateTimeOffset now,
        DateOnly todayUtc,
        DateOnly monthStartUtc)
    {
        UsageQuota? violated = null;
        decimal violatedRatio = -1m;
        List<UsageQuotaWarning>? warnings = null;

        foreach (var quota in applicable)
        {
            decimal used = ComputeUsageForQuota(
                tenant,
                quota,
                callerUserKey,
                callerGroups,
                callerBillingGroup,
                entries,
                groupSets,
                tariffs,
                todayUtc,
                monthStartUtc);

            if (used >= quota.Limit)
            {
                decimal ratio = quota.Limit > 0m ? used / quota.Limit : decimal.MaxValue;
                if (violated is null
                    || quota.Limit < violated.Limit
                    || (quota.Limit == violated.Limit && ratio > violatedRatio))
                {
                    violated = quota;
                    violatedRatio = ratio;
                }
            }
            else if (violated is null && used >= quota.Limit * (decimal)quota.SoftLimitRatio)
            {
                warnings ??= [];
                var warning = new UsageQuotaWarning(quota.Scope, quota.Metric);
                if (!warnings.Contains(warning))
                {
                    warnings.Add(warning);
                }
            }
        }

        if (violated is not null)
        {
            int retryAfter = ComputeRetryAfterSeconds(violated.Period, now, todayUtc, monthStartUtc);
            return new UsageQuotaDecision(
                UsageQuotaDecisionKind.Exceeded,
                violated.Scope,
                violated.Metric,
                violated.Period,
                violated.Limit,
                retryAfter);
        }

        if (warnings is { Count: > 0 })
        {
            return new UsageQuotaDecision(UsageQuotaDecisionKind.AllowWithWarning, Warnings: warnings);
        }

        return UsageQuotaDecision.AllowDecision;
    }

    private decimal ComputeUsageForQuota(
        TenantId tenant,
        UsageQuota quota,
        string callerUserKey,
        IReadOnlyList<Sid> callerGroups,
        string callerBillingGroup,
        List<UsageQuotaSeriesEntry> entries,
        Dictionary<string, IReadOnlyList<Sid>> groupSets,
        IReadOnlyList<UsageTariffVersion> tariffs,
        DateOnly todayUtc,
        DateOnly monthStartUtc)
    {
        var windowStart = quota.Period == UsageQuotaPeriod.Day ? todayUtc : monthStartUtc;

        if (quota.Scope == UsageQuotaScope.Group && string.Equals(quota.Subject, "*", StringComparison.Ordinal))
        {
            decimal maxAcrossGroups = 0m;
            foreach (var groupSid in callerGroups)
            {
                decimal groupUsed = SumMatchingEntries(
                    tenant,
                    quota,
                    windowStart,
                    todayUtc,
                    targetUserKey: callerUserKey,
                    targetGroupSid: groupSid.Value,
                    targetBillingGroup: callerBillingGroup,
                    entries,
                    groupSets,
                    tariffs);
                if (groupUsed > maxAcrossGroups)
                {
                    maxAcrossGroups = groupUsed;
                }
            }

            return maxAcrossGroups;
        }

        string targetUserKey = quota.Scope == UsageQuotaScope.User && !string.Equals(quota.Subject, "*", StringComparison.Ordinal)
            ? UsageAttribution.ComputeUserKey(new Sid(quota.Subject), _options.PseudonymizeUsers, _hmacKey)
            : callerUserKey;
        string targetBillingGroup = quota.Scope == UsageQuotaScope.BillingGroup && !string.Equals(quota.Subject, "*", StringComparison.Ordinal)
            ? quota.Subject
            : callerBillingGroup;

        return SumMatchingEntries(
            tenant,
            quota,
            windowStart,
            todayUtc,
            targetUserKey,
            targetGroupSid: quota.Subject,
            targetBillingGroup,
            entries,
            groupSets,
            tariffs);
    }

    private static decimal SumMatchingEntries(
        TenantId tenant,
        UsageQuota quota,
        DateOnly windowStart,
        DateOnly todayUtc,
        string targetUserKey,
        string targetGroupSid,
        string targetBillingGroup,
        List<UsageQuotaSeriesEntry> entries,
        Dictionary<string, IReadOnlyList<Sid>> groupSets,
        IReadOnlyList<UsageTariffVersion> tariffs)
    {
        decimal total = 0m;
        foreach (var entry in entries)
        {
            if (entry.DayUtc < windowStart || entry.DayUtc > todayUtc)
            {
                continue;
            }

            if (!EntryMatchesQuota(quota, entry, targetUserKey, targetGroupSid, targetBillingGroup, groupSets))
            {
                continue;
            }

            total += ExtractMetricValue(tenant, quota, entry, tariffs);
        }

        return quota.Metric == UsageQuotaMetric.CostUnits
            ? UsageTariffCalculator.RoundCost(total)
            : total;
    }

    private static bool EntryMatchesQuota(
        UsageQuota quota,
        UsageQuotaSeriesEntry entry,
        string targetUserKey,
        string targetGroupSid,
        string targetBillingGroup,
        Dictionary<string, IReadOnlyList<Sid>> groupSets) =>
        quota.Scope switch
        {
            UsageQuotaScope.Tenant => true,
            UsageQuotaScope.User => string.Equals(entry.UserKey, targetUserKey, StringComparison.Ordinal),
            UsageQuotaScope.BillingGroup => string.Equals(entry.BillingGroup, targetBillingGroup, StringComparison.OrdinalIgnoreCase),
            UsageQuotaScope.Group =>
                groupSets.TryGetValue(entry.GroupSetId, out var sids) && ContainsSid(sids, targetGroupSid),
            _ => false
        };

    private static decimal ExtractMetricValue(
        TenantId tenant,
        UsageQuota quota,
        UsageQuotaSeriesEntry entry,
        IReadOnlyList<UsageTariffVersion> tariffs)
    {
        switch (quota.Metric)
        {
            case UsageQuotaMetric.Requests:
                return entry.Requests;
            case UsageQuotaMetric.Queries:
                return entry.Queries;
            case UsageQuotaMetric.Rows:
                return entry.RowsReturned;
            case UsageQuotaMetric.Bytes:
                return entry.Bytes;
            case UsageQuotaMetric.CostUnits:
                var cost = UsageTariffCalculator.CalculateRow(
                    new UsageTariffRowInput(
                        tenant.Value,
                        entry.DayUtc,
                        entry.DataSource,
                        entry.AllowCount > 0,
                        entry.Queries,
                        entry.RowsReturned,
                        entry.Bytes,
                        entry.DurationMs,
                        entry.AnalysisCost,
                        entry.SourceComputeMs,
                        entry.SourceComputeMeasured,
                        entry.SourceCostMicros),
                    tariffs);
                if (cost.UnitLabel is not null
                    && string.Equals(cost.UnitLabel, quota.UnitLabel, StringComparison.OrdinalIgnoreCase))
                {
                    return cost.RawCostUnits;
                }

                return 0m;
            default:
                return 0m;
        }
    }

    private static int ComputeRetryAfterSeconds(
        UsageQuotaPeriod period,
        DateTimeOffset now,
        DateOnly todayUtc,
        DateOnly monthStartUtc)
    {
        var resetDate = period == UsageQuotaPeriod.Day
            ? todayUtc.AddDays(1)
            : monthStartUtc.AddMonths(1);
        var resetUtc = new DateTimeOffset(resetDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var remainingSeconds = (int)Math.Ceiling((resetUtc - now).TotalSeconds);
        return Math.Max(1, remainingSeconds);
    }

    private static bool ContainsSid(IReadOnlyList<Sid> sids, string target)
    {
        foreach (var sid in sids)
        {
            if (string.Equals(sid.Value, target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record CachedTenantSnapshot(UsageQuotaTenantSnapshot Snapshot, DateTimeOffset RefreshedAtUtc);
}
