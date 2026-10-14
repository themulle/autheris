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

        return ValidateOptionsResult.Success;
    }
}
