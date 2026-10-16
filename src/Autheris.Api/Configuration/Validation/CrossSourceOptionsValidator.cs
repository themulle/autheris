using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Olap;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Configuration.Validation;

/// <summary>
/// PLAN-SEC-OLAP-01: fail-closed start-up validation of the federation and OLAP options. An <c>AllowedTransports</c> entry must be a
/// registered transport name (a typo would silently deny a transport, or, in a deny-minded list, silently allow one); limits must be
/// positive instead of falling back to silent defaults. Failure messages name option paths and the offending transport name only.
/// Warnings (never failures): legacy <c>CrossSource:MaxMemory</c> / <c>MaxTempDirectorySize</c> keys that have no effect, and
/// <c>Olap</c> listed while the federation switch is off.
/// </summary>
public sealed class CrossSourceOptionsValidator : IValidateOptions<GatewayOptions>
{
    private readonly IConfiguration? _configuration;
    private readonly ILogger<CrossSourceOptionsValidator>? _logger;

    public CrossSourceOptionsValidator(IConfiguration? configuration = null, ILogger<CrossSourceOptionsValidator>? logger = null)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var crossSource = options.WebSql?.CrossSource ?? new CrossSourceOptions();
        ValidateCrossSource(crossSource, failures);
        ValidateDuckDbOlap(options.DuckDbOlap ?? new DuckDbOlapOptions(), failures);
        LogWarnings(crossSource);
        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    private static void ValidateCrossSource(CrossSourceOptions o, List<string> failures)
    {
        const string p = "Gateway:WebSql:CrossSource";
        if (o.AllowedTransports != null)
        {
            foreach (var transport in o.AllowedTransports)
            {
                if (string.IsNullOrWhiteSpace(transport) ||
                    !SqlTransports.All.Contains(transport.Trim(), StringComparer.OrdinalIgnoreCase) ||
                    !string.Equals(transport, transport.Trim(), StringComparison.Ordinal))
                {
                    failures.Add($"{p}:AllowedTransports contains the unknown transport '{transport}'. Valid names: {string.Join(", ", SqlTransports.All)}.");
                }
            }
        }

        Positive(o.MaxTableCount, $"{p}:MaxTableCount", failures);
        Positive(o.MaxStagedRowsPerTable, $"{p}:MaxStagedRowsPerTable", failures);
        Positive(o.MaxTotalStagedRows, $"{p}:MaxTotalStagedRows", failures);
        Positive(o.MaxStagedBytesPerTable, $"{p}:MaxStagedBytesPerTable", failures);
        Positive(o.MaxTotalStagedBytes, $"{p}:MaxTotalStagedBytes", failures);
        Positive(o.TimeoutSeconds, $"{p}:TimeoutSeconds", failures);
        if (o.MaxParallelSourceReads is < 1 or > 16)
        {
            failures.Add($"{p}:MaxParallelSourceReads must be between 1 and 16.");
        }
    }

    private static void ValidateDuckDbOlap(DuckDbOlapOptions o, List<string> failures)
    {
        const string p = "Gateway:DuckDbOlap";
        Positive(o.MaxStagedRowsPerTable, $"{p}:MaxStagedRowsPerTable", failures);
        Positive(o.MaxResultRows, $"{p}:MaxResultRows", failures);
        Positive(o.MaxResultBytes, $"{p}:MaxResultBytes", failures);
        Positive(o.MaxTableCount, $"{p}:MaxTableCount", failures);
        Positive(o.MaxTotalStagedRows, $"{p}:MaxTotalStagedRows", failures);
        Positive(o.MaxStagedBytesPerTable, $"{p}:MaxStagedBytesPerTable", failures);
        Positive(o.MaxTotalStagedBytes, $"{p}:MaxTotalStagedBytes", failures);
        Positive(o.QueryTimeoutSeconds, $"{p}:QueryTimeoutSeconds", failures);
        Positive(o.MaxSqlLength, $"{p}:MaxSqlLength", failures);
        if (o.MaxConcurrentQueries < 1)
        {
            failures.Add($"{p}:MaxConcurrentQueries must be at least 1.");
        }

        if (o.MaxConcurrentQueriesPerTenant < 1)
        {
            failures.Add($"{p}:MaxConcurrentQueriesPerTenant must be at least 1.");
        }

        if (o.MaxThreads is < 1 or > 8)
        {
            failures.Add($"{p}:MaxThreads must be between 1 and 8.");
        }

        if (!DuckDbOlapEngine.IsValidMemorySize(o.MaxMemory))
        {
            failures.Add($"{p}:MaxMemory must be a size such as 512MB or 2GB.");
        }

        if (!DuckDbOlapEngine.IsValidMemorySize(o.MaxTempDirectorySize))
        {
            failures.Add($"{p}:MaxTempDirectorySize must be a size such as 256MB or 0B.");
        }
    }

    private void LogWarnings(CrossSourceOptions crossSource)
    {
        if (_logger == null)
        {
            return;
        }

        if (_configuration != null)
        {
            foreach (var legacy in new[] { "MaxMemory", "MaxTempDirectorySize" })
            {
                if (_configuration.GetSection($"{GatewayOptions.SectionName}:WebSql:CrossSource:{legacy}").Exists())
                {
                    _logger.LogWarning(
                        "Gateway:WebSql:CrossSource:{Key} is no longer an option and has no effect. Configure Gateway:DuckDbOlap:MaxMemory and MaxTempDirectorySize instead.",
                        legacy);
                }
            }
        }

        if (!crossSource.Enabled &&
            crossSource.AllowedTransports?.Any(t => string.Equals(t?.Trim(), SqlTransports.Olap, StringComparison.OrdinalIgnoreCase)) == true)
        {
            _logger.LogWarning(
                "Gateway:WebSql:CrossSource:AllowedTransports lists Olap while Gateway:WebSql:CrossSource:Enabled is false; cross-source OLAP requests stay denied.");
        }
    }

    private static void Positive(long value, string path, List<string> failures)
    {
        if (value <= 0)
        {
            failures.Add($"{path} must be greater than 0.");
        }
    }
}
