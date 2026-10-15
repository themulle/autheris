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
        if (maxOffset is null or < 0 or > DatasetApiOptions.UpperBoundMaxOffset)
        {
            return ValidateOptionsResult.Fail(
                $"Analytics:DatasetApi:MaxOffset must be between 0 and {DatasetApiOptions.UpperBoundMaxOffset}.");
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
        if (options.Privacy.DefaultMinGroupSize < minAllowed && options.Privacy.DefaultMinGroupSize != 1)
        {
            return ValidateOptionsResult.Fail(
                $"Analytics:Privacy:DefaultMinGroupSize must be >= {minAllowed}.");
        }

        if (options.Privacy.DefaultMinGroupSize < 1)
        {
            return ValidateOptionsResult.Fail("Analytics:Privacy:DefaultMinGroupSize must be >= 1.");
        }

        return ValidateOptionsResult.Success;
    }
}

