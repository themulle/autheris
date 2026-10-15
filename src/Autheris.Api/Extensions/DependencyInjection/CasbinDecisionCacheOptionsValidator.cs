namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>Validates <see cref="CasbinDecisionCacheOptions"/> ranges.</summary>
public sealed class CasbinDecisionCacheOptionsValidator : IValidateOptions<CasbinDecisionCacheOptions>
{
    public ValidateOptionsResult Validate(string? name, CasbinDecisionCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (options.Capacity is < 100 or > 1_000_000)
        {
            failures.Add($"{CasbinDecisionCacheOptions.SectionName}:Capacity must be between 100 and 1000000.");
        }

        if (options.TtlSeconds is < 1 or > 3600)
        {
            failures.Add($"{CasbinDecisionCacheOptions.SectionName}:TtlSeconds must be between 1 and 3600.");
        }

        if (options.EpochCheckSeconds < 1 || options.EpochCheckSeconds > options.TtlSeconds)
        {
            failures.Add($"{CasbinDecisionCacheOptions.SectionName}:EpochCheckSeconds must be between 1 and TtlSeconds.");
        }

        if (options.UnavailableBackoffMilliseconds is < 100 or > 10_000)
        {
            failures.Add($"{CasbinDecisionCacheOptions.SectionName}:UnavailableBackoffMilliseconds must be between 100 and 10000.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
