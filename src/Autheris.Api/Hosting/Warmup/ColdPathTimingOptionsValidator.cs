namespace Autheris.Api.Hosting.Warmup;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

/// <summary>SEC-OPS150-12: the cold-path timing listener may only be enabled in the Development environment.</summary>
public sealed class ColdPathTimingOptionsValidator(IHostEnvironment environment) : IValidateOptions<ColdPathTimingOptions>
{
    public ValidateOptionsResult Validate(string? name, ColdPathTimingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Enabled && !environment.IsDevelopment())
        {
            return ValidateOptionsResult.Fail(
                $"{ColdPathTimingOptions.SectionName}:Enabled is only allowed in the Development environment.");
        }

        return ValidateOptionsResult.Success;
    }
}
