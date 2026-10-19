namespace Autheris.Application.Policy;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Issue #107 (PLAN-GOV-107 D-15, SEC-RF107-16): startup validation of <see cref="RowFilterKeySetCacheOptions"/> (<c>ValidateOnStart</c>).
/// <c>1 s &lt;= DefaultDuration &lt;= MaxDuration &lt;= 24 h</c>, <c>1 &lt;= MaxKeys &lt;= 10,000</c>, <c>0 &lt;= BindParameterReserve &lt;= 1,000</c>,
/// <c>1 &lt;= MaxConcurrentKeyQueries &lt;= 64</c>, <c>SizeLimitBytes &gt; 0</c> and <c>0 &lt; KeyQueryTimeout &lt;=</c> the smallest statement timeout
/// of WebSQL and SQL endpoints. <c>DefaultStrategy = KeySetCache</c> is allowed but logs a startup Warning.
/// </summary>
public sealed class RowFilterKeySetCacheOptionsValidator : IValidateOptions<GatewayOptions>
{
    private const string Path = "Gateway:RowFilters:KeySetCache";
    private readonly ILogger<RowFilterKeySetCacheOptionsValidator>? _logger;

    public RowFilterKeySetCacheOptionsValidator(ILogger<RowFilterKeySetCacheOptionsValidator>? logger = null) => _logger = logger;

    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var o = options.RowFilters?.KeySetCache ?? new RowFilterKeySetCacheOptions();
        var failures = new List<string>();

        if (!Enum.IsDefined(o.DefaultStrategy))
        {
            failures.Add($"{Path}:DefaultStrategy must be 'Exists' or 'KeySetCache'.");
        }

        if (o.DefaultDuration < RowFilterKeySetCacheOptions.MinDuration)
        {
            failures.Add($"{Path}:DefaultDuration must be at least 1 second.");
        }

        if (o.MaxDuration < o.DefaultDuration)
        {
            failures.Add($"{Path}:MaxDuration must be greater than or equal to DefaultDuration.");
        }

        if (o.MaxDuration > RowFilterKeySetCacheOptions.DurationCeiling)
        {
            failures.Add($"{Path}:MaxDuration must not exceed 24 hours.");
        }

        if (o.MaxKeys is < 1 or > RowFilterKeySetCacheOptions.MaxKeysCeiling)
        {
            failures.Add($"{Path}:MaxKeys must be between 1 and {RowFilterKeySetCacheOptions.MaxKeysCeiling}.");
        }

        if (o.BindParameterReserve is < 0 or > RowFilterKeySetCacheOptions.BindParameterReserveCeiling)
        {
            failures.Add($"{Path}:BindParameterReserve must be between 0 and {RowFilterKeySetCacheOptions.BindParameterReserveCeiling}.");
        }

        if (o.MaxConcurrentKeyQueries is < 1 or > RowFilterKeySetCacheOptions.MaxConcurrentKeyQueriesCeiling)
        {
            failures.Add($"{Path}:MaxConcurrentKeyQueries must be between 1 and {RowFilterKeySetCacheOptions.MaxConcurrentKeyQueriesCeiling}.");
        }

        if (o.SizeLimitBytes <= 0)
        {
            failures.Add($"{Path}:SizeLimitBytes must be greater than 0.");
        }

        if (o.KeyQueryTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{Path}:KeyQueryTimeout must be greater than 0.");
        }
        else
        {
            var limit = SmallestStatementTimeout(options);
            if (limit is { } max && o.KeyQueryTimeout > max)
            {
                failures.Add($"{Path}:KeyQueryTimeout must not exceed the smallest statement timeout of Gateway:WebSql:ExecutionTimeoutSeconds and Gateway:SqlEndpoints:MaxQueryTimeoutSeconds ({(long)max.TotalSeconds} s).");
            }
        }

        if (failures.Count > 0)
        {
            return ValidateOptionsResult.Fail(failures);
        }

        if (o.DefaultStrategy == RowFilterEvaluationStrategy.KeySetCache)
        {
            _logger?.LogWarning(
                "{Path}:DefaultStrategy is 'KeySetCache': every correlated row filter without an explicit setting is evaluated from a cached key set (bounded staleness of dependent data).",
                Path);
        }

        return ValidateOptionsResult.Success;
    }

    private static TimeSpan? SmallestStatementTimeout(GatewayOptions options)
    {
        TimeSpan? smallest = null;
        foreach (var seconds in new[] { options.WebSql?.ExecutionTimeoutSeconds ?? 0, options.SqlEndpoints?.MaxQueryTimeoutSeconds ?? 0 })
        {
            if (seconds > 0 && (smallest is null || TimeSpan.FromSeconds(seconds) < smallest))
            {
                smallest = TimeSpan.FromSeconds(seconds);
            }
        }

        return smallest;
    }
}
