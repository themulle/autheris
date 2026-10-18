namespace Autheris.Api.Hosting.Warmup;

using Microsoft.Extensions.Options;

/// <summary>SEC-OPS150-12: rejects out-of-range values and unknown step names (including <c>GraphQlExecutor</c>) at start.</summary>
public sealed class StartupWarmupOptionsValidator : IValidateOptions<StartupWarmupOptions>
{
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 300;

    public ValidateOptionsResult Validate(string? name, StartupWarmupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (options.TimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
        {
            failures.Add($"{StartupWarmupOptions.SectionName}:TimeoutSeconds must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds}.");
        }

        foreach (var step in options.Steps.Keys)
        {
            if (!string.Equals(step, StartupWarmupOptions.McpSerializationStep, StringComparison.OrdinalIgnoreCase))
            {
                // The step name is not echoed: it is operator input and must not reach logs verbatim.
                failures.Add($"{StartupWarmupOptions.SectionName}:Steps contains an unknown or non-configurable step name; allowed: {StartupWarmupOptions.McpSerializationStep}.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
