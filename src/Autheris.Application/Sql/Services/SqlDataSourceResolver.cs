namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using Autheris.Application.Sql;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;

/// <summary>
/// SEC C-03 / SEC P-05: Resolves data source configuration, dialects, connection options and table targets.
/// </summary>
public sealed class SqlDataSourceResolver
{
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger? _logger;

    public SqlDataSourceResolver(
        IOptions<GatewayOptions> options,
        ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public string ResolveAllowedDataSource(string? requested, TenantId tenantId)
    {
        string? denial = EvaluateAllowed(requested, tenantId, out string resolved);
        if (denial != null)
        {
            throw new WebSqlPolicyException(denial);
        }

        return resolved;
    }

    /// <summary>
    /// non-throwing form of<see cref="ResolveAllowedDataSource"/> (one shared decision, in memory only) so a statement that
    /// names a source the caller may not use can be answered exactly like an unknown table.
    /// </summary>
    public bool TryResolveAllowedDataSource(string? requested, TenantId tenantId, out string resolved) =>
        EvaluateAllowed(requested, tenantId, out resolved) == null;

    private string? EvaluateAllowed(string? requested, TenantId tenantId, out string resolved)
    {
        if (!TryResolveGloballyAllowedDataSource(_options.Value.WebSql, requested, out resolved))
        {
            return "The requested data source is not enabled for WebSQL.";
        }

        return GovernedSqlExecutionService.IsDataSourceAllowedForTenant(_options.Value.WebSql, tenantId, resolved)
            ? null
            : "The requested data source is not enabled for this tenant.";
    }

    public string ResolveGloballyAllowedDataSource(string? requested)
    {
        if (TryResolveGloballyAllowedDataSource(_options.Value.WebSql, requested, out var resolved))
        {
            return resolved;
        }

        throw new WebSqlPolicyException("The requested data source is not enabled for WebSQL.");
    }

    public static bool TryResolveGloballyAllowedDataSource(WebSqlOptions webSqlOptions, string? requested, out string resolved)
    {
        string defaultName = webSqlOptions.DefaultDataSourceName;
        if (string.IsNullOrWhiteSpace(requested) || string.Equals(requested, defaultName, StringComparison.OrdinalIgnoreCase))
        {
            resolved = defaultName;
            return true;
        }

        var allowed = webSqlOptions.AllowedDataSources;
        if (allowed != null)
        {
            foreach (var candidate in allowed)
            {
                if (string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase))
                {
                    resolved = candidate;
                    return true;
                }
            }
        }

        if (TryGetMapping(webSqlOptions, requested, out _))
        {
            resolved = requested;
            return true;
        }

        resolved = string.Empty;
        return false;
    }

    /// <summary>The <c>DataSources:Connections</c> key a logical data source name resolves to, or null (deny) when there is none.</summary>
    public string? ResolveConnectionKey(string? logicalName) => ResolveConnectionKey(_options.Value, logicalName);

    /// <summary>
    /// The one lookup of <c>WebSql:DataSourceMappings</c> (#668). A mapping is one hop to an existing key of <c>DataSources:Connections</c>
    /// (case-insensitive, returned as spelled there) that is not itself a mapping key. A chain, a cycle, an empty or unknown target, or a name
    /// without a connection resolves to null, which every caller treats as a denial. An unmapped name is its own connection key.
    /// </summary>
    public static string? ResolveConnectionKey(GatewayOptions options, string? logicalName)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(logicalName) || options.DataSources?.Connections is not { } connections)
        {
            return null;
        }

        string candidate = logicalName;
        if (TryGetMapping(options.WebSql, logicalName, out var target))
        {
            if (string.IsNullOrWhiteSpace(target) || TryGetMapping(options.WebSql, target, out _))
            {
                return null;
            }

            candidate = target;
        }

        foreach (var key in connections.Keys)
        {
            if (string.Equals(key, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>Whether <c>WebSql:DataSourceMappings</c> has an entry for <paramref name="name"/>; the entry's raw target (possibly blank) is returned unvalidated.</summary>
    public static bool TryGetMapping(WebSqlOptions? webSqlOptions, string? name, out string target)
    {
        target = string.Empty;
        if (webSqlOptions?.DataSourceMappings is not { } mappings || string.IsNullOrWhiteSpace(name) || !mappings.TryGetValue(name, out var raw))
        {
            return false;
        }

        target = raw ?? string.Empty;
        return true;
    }

    public DatabaseDialect? ResolveConnectionDialect(string dataSourceName)
    {
        DataSourceConnectionOptions? connOptions = ResolveConnectionOptions(dataSourceName);
        if (connOptions == null || string.IsNullOrWhiteSpace(connOptions.ConnectionString))
        {
            return null;
        }

        if (!GovernedSqlExecutionService.TryMapProviderToDialect(connOptions.Provider, out var dialect))
        {
            _logger?.LogWarning("WebSQL rejected data source {DataSource}: provider is not supported by WebSQL.", dataSourceName);
            throw new WebSqlPolicyException(SqlDialectMapper.UnsupportedProviderMessage);
        }

        return dialect;
    }

    public DataSourceConnectionOptions? ResolveConnectionOptions(string dataSourceName) =>
        ResolveConnectionOptions(_options.Value, dataSourceName);

    public static DataSourceConnectionOptions? ResolveConnectionOptions(GatewayOptions options, string? dataSourceName) =>
        ResolveConnectionKey(options, dataSourceName) is { } key ? options.DataSources.Connections[key] : null;

    public static TableIdentifier ResolveTableIdentifier(TableAccessTarget target, string? dataSourceName)
    {
        if (string.IsNullOrWhiteSpace(target.Catalog) && !string.IsNullOrWhiteSpace(target.Schema) &&
            !string.IsNullOrWhiteSpace(dataSourceName))
        {
            return new TableIdentifier(dataSourceName, target.Schema, target.TableName);
        }

        if (TableIdentifier.TryParse(target.FullName, out var parsed))
        {
            return parsed;
        }

        string domain = !string.IsNullOrWhiteSpace(target.Catalog) ? target.Catalog : "default";
        string schema = !string.IsNullOrWhiteSpace(target.Schema) ? target.Schema : "public";
        return new TableIdentifier(domain, schema, target.TableName);
    }

    public static bool ReferencesColumn(string? tableOrAlias, string referencedColumn, string columnName, TableAccessTarget target)
    {
        if (!string.Equals(referencedColumn, columnName, StringComparison.OrdinalIgnoreCase))
            return false;

        return tableOrAlias == null ||
               string.Equals(tableOrAlias, target.Alias, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tableOrAlias, target.TableName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tableOrAlias, target.FullName, StringComparison.OrdinalIgnoreCase);
    }

    public static WebSqlTableDeniedException TableDenied(TableAccessTarget target) =>
        new($"Access to table '{target.FullName}' is denied or the table is not registered in the governance catalog.");

    public static bool MatchesPostgreSqlCatalogName(TableAccessTarget target, TableIdentifier catalogTable)
    {
        var physicalTable = target.TableNameQuoted ? target.TableName : target.TableName.ToLowerInvariant();
        if (!string.Equals(physicalTable, catalogTable.TableName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(target.Schema))
        {
            var physicalSchema = target.SchemaQuoted ? target.Schema : target.Schema.ToLowerInvariant();
            if (!string.Equals(physicalSchema, catalogTable.Schema, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
