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
    /// SEC-RG-01: non-throwing form of <see cref="ResolveAllowedDataSource"/> (one shared decision, in memory only) so a statement that
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

        if (webSqlOptions.DataSourceMappings != null && webSqlOptions.DataSourceMappings.ContainsKey(requested))
        {
            resolved = requested;
            return true;
        }

        resolved = string.Empty;
        return false;
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

    public DataSourceConnectionOptions? ResolveConnectionOptions(string dataSourceName)
    {
        string effectiveName = dataSourceName;
        if (_options.Value.WebSql?.DataSourceMappings != null &&
            _options.Value.WebSql.DataSourceMappings.TryGetValue(dataSourceName, out var mapped) &&
            !string.IsNullOrWhiteSpace(mapped))
        {
            effectiveName = mapped;
        }

        var connections = _options.Value.DataSources?.Connections;
        if (connections != null && connections.TryGetValue(effectiveName, out var connOptions))
        {
            return connOptions;
        }

        return null;
    }

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
