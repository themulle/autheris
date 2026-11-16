namespace Autheris.Application.Usage;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

public sealed class UsageAccountingOptionsValidator : IValidateOptions<UsageAccountingOptions>
{
    public ValidateOptionsResult Validate(string? name, UsageAccountingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.FlushIntervalSeconds is < 5 or > 300)
        {
            failures.Add("Gateway:UsageAccounting:FlushIntervalSeconds must be between 5 and 300.");
        }

        if (options.MaxSeries is < 1_000 or > 1_000_000)
        {
            failures.Add("Gateway:UsageAccounting:MaxSeries must be between 1,000 and 1,000,000.");
        }

        if (options.RetentionDays is < 30 or > 1100)
        {
            failures.Add("Gateway:UsageAccounting:RetentionDays must be between 30 and 1100.");
        }

        if (options.MaxFlushRetries is < 1 or > 1000)
        {
            failures.Add("Gateway:UsageAccounting:MaxFlushRetries must be between 1 and 1000.");
        }

        if (options.MaxGroupsPerUser is < 1 or > 4096)
        {
            failures.Add("Gateway:UsageAccounting:MaxGroupsPerUser must be between 1 and 4096.");
        }

        if (options.MaxReportRangeDays is < 1 or > 1100)
        {
            failures.Add("Gateway:UsageAccounting:MaxReportRangeDays must be between 1 and 1100.");
        }

        if (options.MaxReportRows is < 1 or > 1_000_000)
        {
            failures.Add("Gateway:UsageAccounting:MaxReportRows must be between 1 and 1,000,000.");
        }

        if (options.QuotaStalenessSeconds is < 1 or > 3600)
        {
            failures.Add("Gateway:UsageAccounting:QuotaStalenessSeconds must be between 1 and 3600.");
        }

        if (options.ExcludedGroupSidPrefixes != null)
        {
            foreach (var prefix in options.ExcludedGroupSidPrefixes)
            {
                if (string.IsNullOrWhiteSpace(prefix) || !prefix.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"Gateway:UsageAccounting:ExcludedGroupSidPrefixes contains invalid prefix '{prefix}'. Prefixes must start with 'S-1-'.");
                }
            }
        }

        if (options.IncludedGroupSidPrefixes != null)
        {
            foreach (var prefix in options.IncludedGroupSidPrefixes)
            {
                if (string.IsNullOrWhiteSpace(prefix) || !prefix.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"Gateway:UsageAccounting:IncludedGroupSidPrefixes contains invalid prefix '{prefix}'. Prefixes must start with 'S-1-'.");
                }
            }
        }

        if (options.BillingGroups != null)
        {
            foreach (var (tenantKey, sids) in options.BillingGroups)
            {
                if (string.IsNullOrWhiteSpace(tenantKey))
                {
                    failures.Add("Gateway:UsageAccounting:BillingGroups contains empty tenant key.");
                }

                if (sids == null)
                {
                    failures.Add($"Gateway:UsageAccounting:BillingGroups contains null list for tenant '{tenantKey}'.");
                    continue;
                }

                var seenSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sid in sids)
                {
                    if (string.IsNullOrWhiteSpace(sid) || !sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add($"Gateway:UsageAccounting:BillingGroups for tenant '{tenantKey}' contains invalid SID '{sid}'. SIDs must start with 'S-1-'.");
                    }
                    else if (!seenSids.Add(sid))
                    {
                        failures.Add($"Gateway:UsageAccounting:BillingGroups for tenant '{tenantKey}' contains duplicate SID '{sid}'.");
                    }
                }
            }
        }

        if (options.GroupDisplayNames != null)
        {
            foreach (var (sid, displayName) in options.GroupDisplayNames)
            {
                if (string.IsNullOrWhiteSpace(sid) || !sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"Gateway:UsageAccounting:GroupDisplayNames contains invalid SID '{sid}'. SIDs must start with 'S-1-'.");
                }

                if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 256)
                {
                    failures.Add($"Gateway:UsageAccounting:GroupDisplayNames contains invalid display name for SID '{sid}'. Display name must be between 1 and 256 characters.");
                }
            }
        }

        if (options.PseudonymizeUsers && string.IsNullOrWhiteSpace(options.PseudonymizationKeyVaultRef))
        {
            failures.Add("Gateway:UsageAccounting:PseudonymizationKeyVaultRef is required when PseudonymizeUsers is enabled.");
        }

        _ = options.DataOwnersSeeUsers;

        if (options.InitialTariffs != null)
        {
            for (int i = 0; i < options.InitialTariffs.Count; i++)
            {
                var entry = options.InitialTariffs[i];
                if (entry is null)
                {
                    failures.Add($"Gateway:UsageAccounting:InitialTariffs[{i}] must not be null.");
                    continue;
                }

                UsageTariffValidation.ValidateRatesAndMetadata(
                    entry.TenantId,
                    entry.DataSource,
                    entry.BasePerQuery,
                    entry.PerRow,
                    entry.PerMib,
                    entry.PerComputeSecond,
                    entry.PerAnalysisCost,
                    entry.SourceCostFactor,
                    entry.SourceCostUnit,
                    entry.UnitLabel,
                    entry.ComputeBasis,
                    entry.Reason,
                    failures,
                    prefix: $"Gateway:UsageAccounting:InitialTariffs[{i}]:");
            }
        }

        ValidateQuotas(options.Quotas, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateQuotas(List<UsageQuota>? quotas, List<string> failures)
    {
        if (quotas is null)
        {
            return;
        }

        var seen = new HashSet<(UsageQuotaScope Scope, string Tenant, string Subject, UsageQuotaPeriod Period, UsageQuotaMetric Metric, string UnitLabel)>(
            UsageQuotaKeyComparer.Instance);

        for (int i = 0; i < quotas.Count; i++)
        {
            var quota = quotas[i];
            string prefix = $"Gateway:UsageAccounting:Quotas[{i}]:";
            if (quota is null)
            {
                failures.Add($"{prefix} entry must not be null.");
                continue;
            }

            if (!Enum.IsDefined(quota.Scope))
            {
                failures.Add($"{prefix}Scope '{quota.Scope}' is invalid.");
            }

            if (!Enum.IsDefined(quota.Period))
            {
                failures.Add($"{prefix}Period '{quota.Period}' is invalid.");
            }

            if (!Enum.IsDefined(quota.Metric))
            {
                failures.Add($"{prefix}Metric '{quota.Metric}' is invalid.");
            }

            if (string.IsNullOrWhiteSpace(quota.Tenant))
            {
                failures.Add($"{prefix}Tenant is required.");
            }
            else if (!string.Equals(quota.Tenant, "*", StringComparison.Ordinal) && !TenantId.TryParse(quota.Tenant, out _))
            {
                failures.Add($"{prefix}Tenant '{quota.Tenant}' must be '*' or a valid tenant identifier.");
            }

            ValidateQuotaSubject(quota, prefix, failures);

            if (quota.Limit <= 0m)
            {
                failures.Add($"{prefix}Limit must be positive.");
            }

            if (!(quota.SoftLimitRatio > 0.0 && quota.SoftLimitRatio <= 1.0))
            {
                failures.Add($"{prefix}SoftLimitRatio must be in (0, 1].");
            }

            if (quota.Metric == UsageQuotaMetric.CostUnits)
            {
                if (string.IsNullOrWhiteSpace(quota.UnitLabel) || !UsageTariffValidation.UnitLabelRegex().IsMatch(quota.UnitLabel))
                {
                    failures.Add($"{prefix}UnitLabel is required and must match '^[A-Za-z0-9 _.-]{{1,32}}$' when Metric is CostUnits.");
                }
            }
            else if (quota.UnitLabel is not null && !UsageTariffValidation.UnitLabelRegex().IsMatch(quota.UnitLabel))
            {
                failures.Add($"{prefix}UnitLabel must match '^[A-Za-z0-9 _.-]{{1,32}}$'.");
            }

            var key = (
                quota.Scope,
                quota.Tenant ?? string.Empty,
                quota.Subject ?? string.Empty,
                quota.Period,
                quota.Metric,
                quota.UnitLabel ?? string.Empty);
            if (!seen.Add(key))
            {
                failures.Add($"{prefix} duplicate quota rule for ({quota.Scope}, {quota.Tenant}, {quota.Subject}, {quota.Period}, {quota.Metric}).");
            }
        }
    }

    private static void ValidateQuotaSubject(UsageQuota quota, string prefix, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(quota.Subject) || quota.Subject.Length > 256)
        {
            failures.Add($"{prefix}Subject is required and must not exceed 256 characters.");
            return;
        }

        if (string.Equals(quota.Subject, "*", StringComparison.Ordinal))
        {
            return;
        }

        if (quota.Scope is UsageQuotaScope.User or UsageQuotaScope.Group
            && !quota.Subject.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{prefix}Subject '{quota.Subject}' for {quota.Scope} scope must be '*' or a valid SID starting with 'S-1-'.");
        }
        else if (quota.Scope == UsageQuotaScope.Tenant && !TenantId.TryParse(quota.Subject, out _))
        {
            failures.Add($"{prefix}Subject '{quota.Subject}' for Tenant scope must be '*' or a valid tenant identifier.");
        }
    }

    private sealed class UsageQuotaKeyComparer
        : IEqualityComparer<(UsageQuotaScope Scope, string Tenant, string Subject, UsageQuotaPeriod Period, UsageQuotaMetric Metric, string UnitLabel)>
    {
        public static readonly UsageQuotaKeyComparer Instance = new();

        public bool Equals(
            (UsageQuotaScope Scope, string Tenant, string Subject, UsageQuotaPeriod Period, UsageQuotaMetric Metric, string UnitLabel) x,
            (UsageQuotaScope Scope, string Tenant, string Subject, UsageQuotaPeriod Period, UsageQuotaMetric Metric, string UnitLabel) y) =>
            x.Scope == y.Scope
            && string.Equals(x.Tenant, y.Tenant, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Subject, y.Subject, StringComparison.OrdinalIgnoreCase)
            && x.Period == y.Period
            && x.Metric == y.Metric
            && string.Equals(x.UnitLabel, y.UnitLabel, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(
            (UsageQuotaScope Scope, string Tenant, string Subject, UsageQuotaPeriod Period, UsageQuotaMetric Metric, string UnitLabel) obj)
        {
            var hc = default(HashCode);
            hc.Add(obj.Scope);
            hc.Add(obj.Tenant, StringComparer.OrdinalIgnoreCase);
            hc.Add(obj.Subject, StringComparer.OrdinalIgnoreCase);
            hc.Add(obj.Period);
            hc.Add(obj.Metric);
            hc.Add(obj.UnitLabel, StringComparer.OrdinalIgnoreCase);
            return hc.ToHashCode();
        }
    }
}
