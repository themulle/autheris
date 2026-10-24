namespace Autheris.Application.Data.Services;

using System;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

public sealed class AnalyticsOptionsValidator : IValidateOptions<AnalyticsOptions>
{
    public ValidateOptionsResult Validate(string? name, AnalyticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var maxOffset = options.DatasetApi?.MaxOffset;
        if (maxOffset is null or <= 0 or > DatasetApiOptions.UpperBoundMaxOffset)
        {
            return ValidateOptionsResult.Fail(
                $"Analytics:DatasetApi:MaxOffset must be between 1 and {DatasetApiOptions.UpperBoundMaxOffset}.");
        }

        if (options.Aggregation is null)
        {
            return ValidateOptionsResult.Fail("Analytics:Aggregation must not be null.");
        }

        if (options.Aggregation.MaxGroupByColumns is < 0 or > 32)
        {
            return ValidateOptionsResult.Fail("Analytics:Aggregation:MaxGroupByColumns must be between 0 and 32.");
        }

        if (options.Aggregation.MaxAggregates is < 1 or > 128)
        {
            return ValidateOptionsResult.Fail("Analytics:Aggregation:MaxAggregates must be between 1 and 128.");
        }

        if (options.Aggregation.MaxGroups is < 1 or > AggregationOptions.UpperBoundMaxGroups)
        {
            return ValidateOptionsResult.Fail(
                $"Analytics:Aggregation:MaxGroups must be between 1 and {AggregationOptions.UpperBoundMaxGroups}.");
        }

        if (options.Privacy is null)
        {
            return ValidateOptionsResult.Fail("Analytics:Privacy must not be null.");
        }

        var minAllowed = options.Privacy.AllowDisabled ? 1 : 2;
        // MISC-10: a floor of 1 switches small-group suppression off, so it needs the explicit AllowDisabled opt-in. The per-table and
        // per-dataset overrides REPLACE the default and are held to the same minimum.
        if (options.Privacy.DefaultMinGroupSize < minAllowed)
        {
            return ValidateOptionsResult.Fail(
                $"Analytics:Privacy:DefaultMinGroupSize must be >= {minAllowed}.");
        }

        foreach (var (table, tableMin) in options.TableMinimumGroupSizes)
        {
            if (tableMin < minAllowed)
            {
                return ValidateOptionsResult.Fail(
                    $"Analytics:TableMinimumGroupSizes:{table} must be >= {minAllowed}.");
            }
        }

        foreach (var (dataset, overrides) in options.Privacy.Datasets)
        {
            if (overrides?.MinGroupSize is { } datasetMin && datasetMin < minAllowed)
            {
                return ValidateOptionsResult.Fail(
                    $"Analytics:Privacy:Datasets:{dataset}:MinGroupSize must be >= {minAllowed}.");
            }
        }

        if (options.Privacy.Noise is { Enabled: true } noise)
        {
            if (noise.Epsilon <= 0 || double.IsNaN(noise.Epsilon) || double.IsInfinity(noise.Epsilon))
            {
                return ValidateOptionsResult.Fail("Analytics:Privacy:Noise:Epsilon must be > 0 when Noise.Enabled is true.");
            }

            if (noise.DailyBudgetEpsilon < noise.Epsilon || double.IsNaN(noise.DailyBudgetEpsilon) || double.IsInfinity(noise.DailyBudgetEpsilon))
            {
                return ValidateOptionsResult.Fail("Analytics:Privacy:Noise:DailyBudgetEpsilon must be >= Epsilon when Noise.Enabled is true.");
            }

            if (noise.MeasureBounds is { Count: > 0 })
            {
                foreach (var (columnKey, bounds) in noise.MeasureBounds)
                {
                    if (bounds is null || bounds.Max <= bounds.Min || double.IsNaN(bounds.Min) || double.IsNaN(bounds.Max))
                    {
                        return ValidateOptionsResult.Fail(
                            $"Analytics:Privacy:Noise:MeasureBounds['{columnKey}'] must have Max > Min.");
                    }
                }
            }
        }

        return ValidateOptionsResult.Success;
    }
}


