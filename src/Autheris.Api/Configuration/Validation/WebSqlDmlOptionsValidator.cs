using System;
using System.Collections.Generic;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Configuration.Validation;

/// <summary>
/// Fails start-up when WebSQL DML is enabled without the scoping prerequisites: every table must be tenant scoped
/// (<c>DataSources:RequireTenantColumn</c>) and a DML statement must be bounded (<c>WebSql:MaxAffectedRows</c> greater than zero).
/// Failure messages name option paths only.
/// </summary>
public sealed class WebSqlDmlOptionsValidator : IValidateOptions<GatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        if (!opts.IsWebSqlDmlAllowed)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        if (opts.DataSources?.RequireTenantColumn != true)
        {
            failures.Add("WebSql:AllowDml requires DataSources:RequireTenantColumn=true.");
        }

        if (opts.WebSql.MaxAffectedRows <= 0)
        {
            failures.Add("WebSql:AllowDml requires WebSql:MaxAffectedRows greater than 0.");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
