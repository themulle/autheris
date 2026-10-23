using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Autheris.Application.Olap;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Configuration.Validation;

/// <summary>
/// MISC-05: fail-closed start-up check that the OLAP memory configuration fits the container. The worst case is
/// <c>MaxConcurrentQueries x (MaxMemory + 3 x MaxTotalStagedBytes)</c> (3x = .NET object overhead of staged rows); it must not exceed the
/// budget (<c>MemoryBudgetBytes</c>, else 50% of the cgroup limit). OLAP enabled without any limit or budget fails outside Development
/// (warning in Development). Failure messages name option paths only.
/// </summary>
public sealed partial class DuckDbOlapOptionsValidator : IValidateOptions<GatewayOptions>
{
    private const long OneMb = 1024L * 1024;

    private readonly IHostEnvironment? _environment;
    private readonly Func<string, string?>? _readFile;
    private readonly ILogger<DuckDbOlapOptionsValidator>? _logger;

    public DuckDbOlapOptionsValidator(
        IHostEnvironment? environment = null,
        ILogger<DuckDbOlapOptionsValidator>? logger = null,
        Func<string, string?>? readFile = null)
    {
        _environment = environment;
        _logger = logger;
        _readFile = readFile;
    }

    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var olap = options.DuckDbOlap ?? new DuckDbOlapOptions();
        if (!olap.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        const string p = "Gateway:DuckDbOlap";
        var maxMemory = ParseSize(olap.MaxMemory);
        if (maxMemory == null)
        {
            return ValidateOptionsResult.Fail($"{p}:MaxMemory must be a size such as 512MB or 2GB.");
        }

        long? budget = olap.MemoryBudgetBytes > 0
            ? olap.MemoryBudgetBytes
            : ContainerMemoryLimit.Read(_readFile) is { } limit ? limit / 2 : null;
        if (budget == null)
        {
            if (_environment?.IsDevelopment() == true)
            {
                _logger?.LogWarning("{Path}: no container memory limit and no MemoryBudgetBytes; the OLAP memory budget is not checked (Development).", p);
                return ValidateOptionsResult.Success;
            }

            return ValidateOptionsResult.Fail($"{p}:MemoryBudgetBytes must be set when the container has no memory limit (OLAP is enabled).");
        }

        var perQuery = maxMemory.Value + (3 * olap.MaxTotalStagedBytes);
        var worstCase = Math.Max(1, olap.MaxConcurrentQueries) * perQuery;
        return worstCase > budget.Value
            ? ValidateOptionsResult.Fail(
                $"{p}: MaxConcurrentQueries x (MaxMemory + 3 x MaxTotalStagedBytes) = {worstCase / OneMb} MB exceeds the memory budget of {budget.Value / OneMb} MB; " +
                "lower the limits or raise MemoryBudgetBytes / the container memory limit.")
            : ValidateOptionsResult.Success;
    }

    /// <summary>Bytes of a size such as <c>256MB</c>; null when unparseable.</summary>
    internal static long? ParseSize(string? size)
    {
        var match = SizeRegex().Match(size?.Trim() ?? string.Empty);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var factor = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "B" => 1L,
            "MB" => OneMb,
            "GB" => 1024L * OneMb,
            "TB" => 1024L * 1024L * OneMb,
            _ => 0L
        };
        return factor == 0 ? null : number * factor;
    }

    [GeneratedRegex(@"^(\d+)\s*(MB|GB|TB|B)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizeRegex();
}
