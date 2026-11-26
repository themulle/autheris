using System;
using System.Collections.Generic;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Configuration.Validation;

/// <summary>
/// Fails start-up on invalid WebSQL result cache settings: the global caps (<c>WebSql:ResultCache*</c>) and the per-source settings
/// (<c>DataSources:Connections:*:ResultCacheTtlSeconds</c>, <c>ResultCacheTables</c>). A time to live above zero is refused on sources that
/// are not <see cref="EnforcementMode.AutherisEnforced"/> or that use Databricks OAuth on-behalf-of credentials. The rules run even when the
/// cache is switched off. Failure messages name option paths only.
/// </summary>
public sealed class WebSqlResultCacheOptionsValidator : IValidateOptions<GatewayOptions>
{
    private const int MaxMegabytesCeiling = 4096;
    private const int MaxRowsCeiling = 100_000;
    private const int MaxEntriesCeiling = 10_000;

    public ValidateOptionsResult Validate(string? name, GatewayOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        var failures = new List<string>();
        var webSql = opts.WebSql;

        if (webSql.ResultCacheMaxMegabytes is < 0 or > MaxMegabytesCeiling)
        {
            failures.Add("WebSql:ResultCacheMaxMegabytes must be in the range 0 to 4096.");
        }

        if (webSql.ResultCacheMaxMegabytes > 0)
        {
            if (webSql.ResultCacheMaxEntryMegabytes < 1)
            {
                failures.Add("WebSql:ResultCacheMaxEntryMegabytes must be at least 1; values above WebSql:ResultCacheMaxMegabytes are clamped to it.");
            }

            if (webSql.ResultCacheMaxUserMegabytes < 1)
            {
                failures.Add("WebSql:ResultCacheMaxUserMegabytes must be at least 1; values above WebSql:ResultCacheMaxMegabytes are clamped to it.");
            }
        }

        if (webSql.ResultCacheMaxRowsPerEntry is < 1 or > MaxRowsCeiling)
        {
            failures.Add("WebSql:ResultCacheMaxRowsPerEntry must be in the range 1 to 100000.");
        }

        if (webSql.ResultCacheMaxEntriesPerUser is < 1 or > MaxEntriesCeiling)
        {
            failures.Add("WebSql:ResultCacheMaxEntriesPerUser must be in the range 1 to 10000.");
        }

        if (opts.DataSources?.Connections is { Count: > 0 } connections)
        {
            foreach (var (key, connection) in connections)
            {
                if (connection is null)
                {
                    continue;
                }

                ValidateConnection(key, connection, failures);
            }
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    private static void ValidateConnection(string key, DataSourceConnectionOptions connection, List<string> failures)
    {
        var path = $"DataSources:Connections:{key}";
        var ttl = connection.ResultCacheTtlSeconds;

        if (ttl is < 0 or > WebSqlOptions.ResultCacheHardMaxTtlSeconds)
        {
            failures.Add($"{path}:ResultCacheTtlSeconds must be in the range 0 to {WebSqlOptions.ResultCacheHardMaxTtlSeconds}.");
        }
        else if (ttl > 0)
        {
            if (connection.EnforcementMode != EnforcementMode.AutherisEnforced)
            {
                failures.Add($"{path}:ResultCacheTtlSeconds requires {path}:EnforcementMode AutherisEnforced.");
            }

            if (connection.Databricks?.Auth?.Mode == DatabricksAuthMode.OAuthObo)
            {
                failures.Add($"{path}:ResultCacheTtlSeconds is not supported with Databricks:Auth:Mode OAuthObo.");
            }
        }

        if (connection.ResultCacheTables is { } tables)
        {
            for (var i = 0; i < tables.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(tables[i]))
                {
                    failures.Add($"{path}:ResultCacheTables:{i} must not be blank.");
                }
            }
        }
    }
}
