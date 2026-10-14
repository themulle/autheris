namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>Validates <see cref="RevocationWatchOptions"/> (SR-L-18).</summary>
public sealed class RevocationWatchOptionsValidator : IValidateOptions<RevocationWatchOptions>
{
    public ValidateOptionsResult Validate(string? name, RevocationWatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.RevocationCheckMaxConsecutiveFailures < 1)
        {
            return ValidateOptionsResult.Fail(
                $"{RevocationWatchOptions.SectionName}:RevocationCheckMaxConsecutiveFailures must be at least 1.");
        }

        return ValidateOptionsResult.Success;
    }
}
