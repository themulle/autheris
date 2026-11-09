namespace Autheris.Application.Usage;

using System;
using System.Collections.Generic;
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

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
