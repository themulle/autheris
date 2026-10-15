namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>Validates <see cref="AsyncJobOptions"/> at startup (parallelism, per-tenant caps and timeouts).</summary>
public sealed class AsyncJobOptionsValidator : IValidateOptions<AsyncJobOptions>
{
    public ValidateOptionsResult Validate(string? name, AsyncJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var section = AsyncJobOptions.SectionName;

        if (options.MaxConcurrentJobs is < 1 or > 64)
        {
            return ValidateOptionsResult.Fail($"{section}:MaxConcurrentJobs must be between 1 and 64.");
        }

        if (options.MaxConcurrentJobsPerTenant < 1 || options.MaxConcurrentJobsPerTenant > options.MaxConcurrentJobs)
        {
            return ValidateOptionsResult.Fail($"{section}:MaxConcurrentJobsPerTenant must be between 1 and MaxConcurrentJobs.");
        }

        if (options.MaxQueuedJobsPerTenant < 1)
        {
            return ValidateOptionsResult.Fail($"{section}:MaxQueuedJobsPerTenant must be at least 1.");
        }

        if (options.DefaultTimeoutSeconds < 1 || options.MaxTimeoutSeconds < 1 || options.DefaultTimeoutSeconds > options.MaxTimeoutSeconds)
        {
            return ValidateOptionsResult.Fail($"{section}:DefaultTimeoutSeconds and MaxTimeoutSeconds must be at least 1, and the default must not exceed the maximum.");
        }

        if (options.RetentionHours < 1 || options.PurgeIntervalMinutes < 1)
        {
            return ValidateOptionsResult.Fail($"{section}:RetentionHours and PurgeIntervalMinutes must be at least 1.");
        }

        if (options.SharedScratchDirectory && string.IsNullOrWhiteSpace(options.ScratchDirectory))
        {
            return ValidateOptionsResult.Fail($"{section}:ScratchDirectory is required when SharedScratchDirectory is true.");
        }

        if (!string.IsNullOrWhiteSpace(options.ScratchDirectory) && !System.IO.Path.IsPathRooted(options.ScratchDirectory))
        {
            return ValidateOptionsResult.Fail($"{section}:ScratchDirectory must be an absolute path when set.");
        }

        return ValidateOptionsResult.Success;
    }
}
