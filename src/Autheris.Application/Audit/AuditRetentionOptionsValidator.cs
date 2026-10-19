namespace Autheris.Application.Audit;

using System;
using System.Collections.Generic;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// SEC-AUD161-01: Validates AuditRetentionOptions at application startup.
/// Enforces mandatory retention floors outside Development environments.
/// </summary>
public sealed class AuditRetentionOptionsValidator : IValidateOptions<AuditRetentionOptions>
{
    private readonly IHostEnvironment? _environment;
    private readonly ILogger<AuditRetentionOptionsValidator>? _logger;

    public AuditRetentionOptionsValidator(
        IHostEnvironment? environment = null,
        ILogger<AuditRetentionOptionsValidator>? logger = null)
    {
        _environment = environment;
        _logger = logger;
    }

    public ValidateOptionsResult Validate(string? name, AuditRetentionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        bool isDev = _environment != null && _environment.IsDevelopment();

        if (options.BatchSize <= 0 || options.BatchSize > 10000)
        {
            failures.Add("Audit:Retention:BatchSize must be between 1 and 10000.");
        }

        if (options.PurgeInterval <= TimeSpan.Zero)
        {
            failures.Add("Audit:Retention:PurgeInterval must be greater than zero.");
        }

        if (options.LockTimeout <= TimeSpan.Zero)
        {
            failures.Add("Audit:Retention:LockTimeout must be greater than zero.");
        }

        if (options.InterBatchDelay < TimeSpan.Zero)
        {
            failures.Add("Audit:Retention:InterBatchDelay must be non-negative.");
        }

        if (!isDev)
        {
            if (options.RetentionDays < 7)
            {
                failures.Add("SEC-AUD161-01 violation: Audit:Retention:RetentionDays must be at least 7 days outside Development.");
            }

            if (options.OperationsDays < 7)
            {
                failures.Add("SEC-AUD161-01 violation: Audit:Retention:OperationsDays must be at least 7 days outside Development.");
            }

            if (options.DataAccessDays < 30)
            {
                failures.Add("SEC-AUD161-01 violation: Audit:Retention:DataAccessDays must be at least 30 days outside Development.");
            }

            if (options.SecurityDays < 365)
            {
                failures.Add("SEC-AUD161-01 violation: Audit:Retention:SecurityDays must be at least 365 days outside Development.");
            }

            if (options.AggregatedDays < 30)
            {
                failures.Add("SEC-AUD161-01 violation: Audit:Retention:AggregatedDays must be at least 30 days outside Development.");
            }
        }
        else
        {
            if (options.RetentionDays < 7 || options.OperationsDays < 7 || options.DataAccessDays < 30 || options.SecurityDays < 365 || options.AggregatedDays < 30)
            {
                _logger?.LogWarning("Audit retention horizons are configured below production minimums in Development environment.");
            }

            if (options.RetentionDays <= 0)
            {
                failures.Add("Audit:Retention:RetentionDays must be greater than zero.");
            }
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
