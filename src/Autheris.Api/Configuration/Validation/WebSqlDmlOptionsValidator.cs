using System;
using System.Collections.Generic;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Configuration.Validation;

/// <summary>
/// Fails start-up when WebSQL DML is enabled without the scoping prerequisites: every table must be tenant scoped
/// (<c>DataSources:RequireTenantColumn</c>), statements must be bounded (<c>WebSql:MaxAffectedRows</c> greater than zero),
/// and write budget/idempotency settings must be strictly positive; also fails when <c>WriteConnectionString</c> is configured while DML is disabled.
/// Failure messages name option paths only.
/// </summary>
public sealed class WebSqlDmlOptionsValidator : IValidateOptions<GatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        var failures = new List<string>();

        if (!opts.IsWebSqlDmlAllowed)
        {
            if (opts.DataSources?.Connections is { Count: > 0 } connections)
            {
                foreach (var kvp in connections)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Value?.WriteConnectionString))
                    {
                        failures.Add($"DataSources:Connections:{kvp.Key}:WriteConnectionString must not be configured when WebSql:AllowDml is false.");
                    }
                }
            }

            return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
        }

        if (opts.DataSources?.RequireTenantColumn != true)
        {
            failures.Add("WebSql:AllowDml requires DataSources:RequireTenantColumn=true.");
        }

        var webSql = opts.WebSql;
        if (webSql.MaxAffectedRows <= 0)
        {
            failures.Add("WebSql:AllowDml requires WebSql:MaxAffectedRows greater than 0.");
        }

        if (webSql.WriteStatementsPerMinute <= 0)
        {
            failures.Add("WebSql:AllowDml requires WebSql:WriteStatementsPerMinute greater than 0.");
        }

        if (webSql.WriteRowsPerHour <= 0)
        {
            failures.Add("WebSql:AllowDml requires WebSql:WriteRowsPerHour greater than 0.");
        }

        if (webSql.WriteIdempotencyTtlSeconds <= 0)
        {
            failures.Add("WebSql:AllowDml requires WebSql:WriteIdempotencyTtlSeconds greater than 0.");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
