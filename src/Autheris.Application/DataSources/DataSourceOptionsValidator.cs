namespace Autheris.Application.DataSources;

using System.Collections.Generic;
using Autheris.Domain.Options.DataSources;
using Microsoft.Extensions.Options;

/// <summary>
/// SEC-DS05-08: Startup validator for <see cref="DataSourceConfigurationOptions"/>.
/// Ensures non-escalation invariants, valid numerical ranges, and pool configurations fail-fast at boot.
/// </summary>
public sealed class DataSourceOptionsValidator : IValidateOptions<DataSourceConfigurationOptions>
{
    public ValidateOptionsResult Validate(string? name, DataSourceConfigurationOptions options)
    {
        if (options == null)
        {
            return ValidateOptionsResult.Fail("DataSourceConfigurationOptions cannot be null.");
        }

        var failures = new List<string>();

        // 1. Validate Ceilings
        if (options.Ceilings != null)
        {
            if (options.Ceilings.MaxAllowedComplexityCeiling <= 0)
                failures.Add("Ceilings.MaxAllowedComplexityCeiling must be greater than zero.");

            if (options.Ceilings.MaxAggregateRowBudgetCeiling <= 0)
                failures.Add("Ceilings.MaxAggregateRowBudgetCeiling must be greater than zero.");

            if (options.Ceilings.QueryTimeoutCeilingSeconds <= 0)
                failures.Add("Ceilings.QueryTimeoutCeilingSeconds must be greater than zero.");

            if (options.Ceilings.MaxResponseRowsCeiling <= 0)
                failures.Add("Ceilings.MaxResponseRowsCeiling must be greater than zero.");

            if (options.Ceilings.MaxResponseBytesCeiling <= 0)
                failures.Add("Ceilings.MaxResponseBytesCeiling must be greater than zero.");

            if (options.Ceilings.CommandTimeoutCeilingSeconds <= 0)
                failures.Add("Ceilings.CommandTimeoutCeilingSeconds must be greater than zero.");

            if (options.Ceilings.StagingTtlCeilingSeconds <= 0)
                failures.Add("Ceilings.StagingTtlCeilingSeconds must be greater than zero.");

            if (options.Ceilings.MaxKeySetImportLimitCeiling <= 0)
                failures.Add("Ceilings.MaxKeySetImportLimitCeiling must be greater than zero.");
        }

        // 2. Validate Defaults
        ValidateProfile("Defaults", options.Defaults, failures);

        // 3. Validate Profiles
        if (options.Profiles != null)
        {
            foreach (var (sourceName, profile) in options.Profiles)
            {
                if (string.IsNullOrWhiteSpace(sourceName))
                {
                    failures.Add("DataSource profile name cannot be null or whitespace.");
                    continue;
                }

                ValidateProfile(sourceName, profile, failures);
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateProfile(string prefix, DataSourceProfileOptions? profile, List<string> failures)
    {
        if (profile == null) return;

        if (profile.QueryCostMultiplier.HasValue && (profile.QueryCostMultiplier.Value < 0.01 || profile.QueryCostMultiplier.Value > 1000.0))
            failures.Add($"{prefix}.QueryCostMultiplier must be between 0.01 and 1000.0.");

        if (profile.SamplingRatio.HasValue && (profile.SamplingRatio.Value < 0.0 || profile.SamplingRatio.Value > 1.0))
            failures.Add($"{prefix}.SamplingRatio must be between 0.0 and 1.0.");

        if (profile.MaxAllowedComplexity.HasValue && profile.MaxAllowedComplexity.Value <= 0)
            failures.Add($"{prefix}.MaxAllowedComplexity must be greater than zero.");

        if (profile.MaxAggregateRowBudget.HasValue && profile.MaxAggregateRowBudget.Value <= 0)
            failures.Add($"{prefix}.MaxAggregateRowBudget must be greater than zero.");

        if (profile.QueryTimeoutSeconds.HasValue && profile.QueryTimeoutSeconds.Value <= 0)
            failures.Add($"{prefix}.QueryTimeoutSeconds must be greater than zero.");

        if (profile.CommandTimeoutSeconds.HasValue && profile.CommandTimeoutSeconds.Value <= 0)
            failures.Add($"{prefix}.CommandTimeoutSeconds must be greater than zero.");

        if (profile.MaxResponseRows.HasValue && profile.MaxResponseRows.Value <= 0)
            failures.Add($"{prefix}.MaxResponseRows must be greater than zero.");

        if (profile.MaxResponseBytes.HasValue && profile.MaxResponseBytes.Value <= 0)
            failures.Add($"{prefix}.MaxResponseBytes must be greater than zero.");

        if (profile.MinPoolSize.HasValue && profile.MinPoolSize.Value < 0)
            failures.Add($"{prefix}.MinPoolSize cannot be negative.");

        if (profile.MaxPoolSize.HasValue && profile.MaxPoolSize.Value <= 0)
            failures.Add($"{prefix}.MaxPoolSize must be greater than zero.");

        if (profile.MinPoolSize.HasValue && profile.MaxPoolSize.HasValue && profile.MinPoolSize.Value > profile.MaxPoolSize.Value)
            failures.Add($"{prefix}.MinPoolSize cannot exceed MaxPoolSize.");

        if (profile.StagingTtlSeconds.HasValue && profile.StagingTtlSeconds.Value <= 0)
            failures.Add($"{prefix}.StagingTtlSeconds must be greater than zero.");

        if (profile.MaxKeySetImportLimit.HasValue && profile.MaxKeySetImportLimit.Value <= 0)
            failures.Add($"{prefix}.MaxKeySetImportLimit must be greater than zero.");
    }
}
