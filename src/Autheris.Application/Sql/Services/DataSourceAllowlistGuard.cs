namespace Autheris.Application.Sql.Services;

using System;
using Autheris.Domain.Common;
using Autheris.Domain.Options;

/// <summary>
/// SR-H-06 / SEC-HIGH-13: the single enforcement point for <c>WebSql.AllowedDataSources</c> and
/// <c>WebSql.TenantDataSourceAllowlist</c>. Every path that reads a data source (single-catalog SQL,
/// cross-source routing, HTTP-declarative tables, OLAP staging) must call this before any data is read.
/// Fails closed when the data source name is empty or unknown.
/// </summary>
public static class DataSourceAllowlistGuard
{
    public static string EnsureAllowed(WebSqlOptions webSqlOptions, TenantId tenantId, string? dataSourceName)
    {
        ArgumentNullException.ThrowIfNull(webSqlOptions);

        if (string.IsNullOrWhiteSpace(dataSourceName))
        {
            throw new WebSqlPolicyException("The requested data source is not enabled for WebSQL.");
        }

        // Governance domains of staged/federated tables are not necessarily WebSQL data source names, so the global
        // allowlist is only a restriction once the operator has configured one (AllowedDataSources / mappings).
        bool globalRestricted = webSqlOptions.AllowedDataSources is { Count: > 0 } ||
                                webSqlOptions.DataSourceMappings is { Count: > 0 };
        string resolved = dataSourceName;
        if (globalRestricted &&
            !SqlDataSourceResolver.TryResolveGloballyAllowedDataSource(webSqlOptions, dataSourceName, out resolved))
        {
            throw new WebSqlPolicyException("The requested data source is not enabled for WebSQL.");
        }

        if (!GovernedSqlExecutionService.IsDataSourceAllowedForTenant(webSqlOptions, tenantId, resolved))
        {
            throw new WebSqlPolicyException("The requested data source is not enabled for this tenant.");
        }

        return resolved;
    }

    /// <summary>
    /// SEC-DS05-03: Enforces transport isolation for a data source based on granular per-data-source configuration.
    /// Fails closed if the data source profile excludes the requested transport protocol.
    /// </summary>
    public static void EnsureTransportAllowed(
        Autheris.Application.DataSources.IDataSourceOptionsResolver? resolver,
        string? dataSourceName,
        Autheris.Domain.Options.DataSources.DataSourceTransportFlags transport)
    {
        if (resolver != null && !resolver.IsTransportAllowed(dataSourceName, transport))
        {
            throw new WebSqlPolicyException($"Access to data source '{dataSourceName}' is not allowed over transport '{transport}'.");
        }
    }
}

