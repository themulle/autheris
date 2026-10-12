namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using TrinoSqlEngine.Analysis;

public enum QuerySourceClass
{
    HomogeneousSql,
    CrossSource,
    Unsupported
}

public sealed record ResolvedSourceTable(
    TableAccessTarget Target,
    TableIdentifier ResolvedIdentifier,
    TableMetadata Metadata,
    string EffectiveDataSource);

public sealed record QueryRoutingDecision(
    QuerySourceClass SourceClass,
    IReadOnlyList<ResolvedSourceTable> Tables,
    string? RejectionReason = null);

public interface ICrossSourceQueryRouter
{
    Task<QueryRoutingDecision> RouteAsync(
        SqlQueryMetadata metadata,
        string? requestedDataSource,
        TenantId tenantId,
        bool isDml,
        CancellationToken ct = default);
}

public sealed class CrossSourceQueryRouter : ICrossSourceQueryRouter
{
    private readonly ITableMetadataRepository _tableRepository;
    private readonly CrossSourceOptions _crossSourceOptions;
    private readonly WebSqlOptions _webSqlOptions;

    public CrossSourceQueryRouter(
        ITableMetadataRepository tableRepository,
        Microsoft.Extensions.Options.IOptions<GatewayOptions> options)
        : this(
            tableRepository,
            (options ?? throw new ArgumentNullException(nameof(options))).Value.WebSql.CrossSource,
            options.Value.WebSql)
    {
    }

    public CrossSourceQueryRouter(
        ITableMetadataRepository tableRepository,
        CrossSourceOptions crossSourceOptions,
        WebSqlOptions webSqlOptions)
    {
        _tableRepository = tableRepository ?? throw new ArgumentNullException(nameof(tableRepository));
        _crossSourceOptions = crossSourceOptions ?? throw new ArgumentNullException(nameof(crossSourceOptions));
        _webSqlOptions = webSqlOptions ?? throw new ArgumentNullException(nameof(webSqlOptions));
    }

    public async Task<QueryRoutingDecision> RouteAsync(
        SqlQueryMetadata metadata,
        string? requestedDataSource,
        TenantId tenantId,
        bool isDml,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (metadata.ReferencedTables.Count == 0)
        {
            return new QueryRoutingDecision(QuerySourceClass.HomogeneousSql, Array.Empty<ResolvedSourceTable>());
        }

        var resolvedTables = new List<ResolvedSourceTable>(metadata.ReferencedTables.Count);
        bool hasNonSqlSource = false;
        var distinctDataSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in metadata.ReferencedTables)
        {
            var domain = !string.IsNullOrWhiteSpace(target.Catalog)
                ? target.Catalog
                : (requestedDataSource ?? _webSqlOptions.DefaultDataSourceName);
            var schema = !string.IsNullOrWhiteSpace(target.Schema) ? target.Schema : "public";
            var tableId = new TableIdentifier(domain, schema, target.TableName);

            var meta = await _tableRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
            if (meta == null && !string.Equals(tableId.Domain, "default", StringComparison.OrdinalIgnoreCase))
            {
                var fallbackId = new TableIdentifier("default", tableId.Schema, tableId.TableName);
                meta = await _tableRepository.GetTableMetadataAsync(fallbackId, ct).ConfigureAwait(false);
                if (meta != null) tableId = fallbackId;
            }

            if (meta == null || !meta.Table.IsActive)
            {
                throw new WebSqlPolicyException($"WebSQL access denied to table '{target.FullName}'.");
            }

            // Check unsupported types (Phase 0 / 2)
            if (meta.Table.DataSourceType is DataSourceType.HttpPlugin or DataSourceType.LakehouseIceberg or DataSourceType.LakehouseDelta)
            {
                throw new WebSqlPolicyException($"Data source type '{meta.Table.DataSourceType}' is not supported in WebSQL.");
            }

            if (meta.Table.DataSourceType == DataSourceType.HttpDeclarative)
            {
                hasNonSqlSource = true;
                var source = meta.Table.SourceName ?? meta.Identifier.Domain;
                distinctDataSources.Add(source);
                resolvedTables.Add(new ResolvedSourceTable(target, tableId, meta, source));
            }
            else if (meta.Table.DataSourceType == DataSourceType.Sql)
            {
                var source = meta.Table.SourceName ?? meta.Identifier.Domain;
                distinctDataSources.Add(source);
                resolvedTables.Add(new ResolvedSourceTable(target, tableId, meta, source));
            }
        }

        bool isCrossSource = hasNonSqlSource || distinctDataSources.Count > 1;

        if (isCrossSource)
        {
            if (isDml)
            {
                throw new WebSqlPolicyException("DML statements across multiple data sources or HTTP APIs are not permitted in WebSQL.");
            }

            if (!_crossSourceOptions.Enabled)
            {
                throw new WebSqlPolicyException("Cross-catalog queries across multiple data sources are not supported in WebSQL.");
            }

            if (!string.IsNullOrWhiteSpace(requestedDataSource))
            {
                foreach (var t in resolvedTables)
                {
                    if (!string.IsNullOrWhiteSpace(t.Target.Catalog) &&
                        !string.Equals(t.Target.Catalog, requestedDataSource, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new WebSqlPolicyException($"Query catalog '{t.Target.Catalog}' does not match requested data source '{requestedDataSource}'.");
                    }
                }
            }

            return new QueryRoutingDecision(QuerySourceClass.CrossSource, resolvedTables);
        }

        return new QueryRoutingDecision(QuerySourceClass.HomogeneousSql, resolvedTables);
    }
}
