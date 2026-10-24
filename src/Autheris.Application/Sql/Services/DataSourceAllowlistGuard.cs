namespace Autheris.Application.Sql.Services;

using System;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
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
        string? denial = Evaluate(webSqlOptions, tenantId, dataSourceName, out string resolved);
        if (denial != null)
        {
            throw new WebSqlPolicyException(denial);
        }

        return resolved;
    }

    /// <summary>
    /// SEC-RG-01: non-throwing form of <see cref="EnsureAllowed"/> with the identical decision (shared core). In-memory only, never
    /// touches a repository, so callers can answer a denied source uniformly before any metadata lookup.
    /// </summary>
    public static bool IsAllowed(WebSqlOptions webSqlOptions, TenantId tenantId, string? dataSourceName) =>
        Evaluate(webSqlOptions, tenantId, dataSourceName, out _) == null;

    private static string? Evaluate(WebSqlOptions webSqlOptions, TenantId tenantId, string? dataSourceName, out string resolved)
    {
        ArgumentNullException.ThrowIfNull(webSqlOptions);
        resolved = string.Empty;

        if (string.IsNullOrWhiteSpace(dataSourceName))
        {
            return "The requested data source is not enabled for WebSQL.";
        }

        // Governance domains of staged/federated tables are not necessarily WebSQL data source names, so the global
        // allowlist is only a restriction once the operator has configured one (AllowedDataSources / mappings).
        bool globalRestricted = webSqlOptions.AllowedDataSources is { Count: > 0 } ||
                                webSqlOptions.DataSourceMappings is { Count: > 0 };
        resolved = dataSourceName;
        if (globalRestricted &&
            !SqlDataSourceResolver.TryResolveGloballyAllowedDataSource(webSqlOptions, dataSourceName, out resolved))
        {
            return "The requested data source is not enabled for WebSQL.";
        }

        if (!GovernedSqlExecutionService.IsDataSourceAllowedForTenant(webSqlOptions, tenantId, resolved))
        {
            return "The requested data source is not enabled for this tenant.";
        }

        return null;
    }

    /// <summary>
    /// SQLA-07: the data source a read of <paramref name="metadata"/> really uses: the catalog <c>SourceName</c> (else the identifier domain), mapped through
    /// <c>DataSourceMappings</c>. Single source of truth for every allowlist check that has table metadata; may be empty (the guard then denies).
    /// </summary>
    public static string EffectiveDataSource(WebSqlOptions webSqlOptions, TableMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(webSqlOptions);
        ArgumentNullException.ThrowIfNull(metadata);
        string source = !string.IsNullOrWhiteSpace(metadata.Table?.SourceName) ? metadata.Table!.SourceName! : metadata.Identifier.Domain;
        return MapDataSource(webSqlOptions, source);
    }

    /// <summary>Maps a data source name through <c>DataSourceMappings</c> (an unmapped or empty mapping keeps the name).</summary>
    public static string MapDataSource(WebSqlOptions webSqlOptions, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        return webSqlOptions.DataSourceMappings != null &&
               webSqlOptions.DataSourceMappings.TryGetValue(name, out var mapped) &&
               !string.IsNullOrWhiteSpace(mapped)
            ? mapped
            : name;
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

