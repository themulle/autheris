namespace Autheris.Extensions.DataCatalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Models;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class DataCatalogSyncService : IDataCatalogSyncService
{
    private readonly IDataCatalogClient? _client;
    private readonly ITableMetadataRepository _metadataRepo;
    private readonly IEpochValidationService _epochService;
    private readonly IOptions<GatewayOptions> _options;
    private readonly IAuditWriter _auditWriter;
    private readonly ILogger<DataCatalogSyncService> _logger;
    private readonly Autheris.Application.Catalog.ICatalogWriteScopeFactory? _catalogWriteScopes;

    public DataCatalogSyncService(
        IDataCatalogClient client,
        ITableMetadataRepository metadataRepo,
        IEpochValidationService epochService,
        IOptions<GatewayOptions> options,
        IAuditWriter auditWriter,
        ILogger<DataCatalogSyncService> logger,
        Autheris.Application.Catalog.ICatalogWriteScopeFactory? catalogWriteScopeFactory = null)
        : this(metadataRepo, epochService, options, auditWriter, logger, catalogWriteScopeFactory)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public DataCatalogSyncService(
        ITableMetadataRepository metadataRepo,
        IEpochValidationService epochService,
        IOptions<GatewayOptions> options,
        IAuditWriter auditWriter,
        ILogger<DataCatalogSyncService> logger,
        Autheris.Application.Catalog.ICatalogWriteScopeFactory? catalogWriteScopeFactory = null)
    {
        _catalogWriteScopes = catalogWriteScopeFactory;
        _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
        _epochService = epochService ?? throw new ArgumentNullException(nameof(epochService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CatalogSyncResult> SyncCatalogAsync(bool dryRun = false, TenantId? restrictToTenant = null, CancellationToken ct = default)
    {
        var client = _client ?? throw new InvalidOperationException("No data catalog client is registered. Enable the 'openmetadata' module.");
        await using var catalogWriteScope = Autheris.Application.Catalog.CatalogWriteScopeFactoryExtensions.BeginOrNoop(_catalogWriteScopes, "data-catalog-sync");
        _logger.LogInformation("Starting data catalog sync from provider {Provider} (DryRun: {DryRun})...", client.ProviderType, dryRun);

        var tables = await client.GetTablesAsync(null, ct).ConfigureAwait(false);
        var affectedTables = new List<TableIdentifier>();
        var warnings = new List<string>();

        int syncedTables = 0;
        int syncedColumns = 0;
        int maskedColumns = 0;
        int art9Tables = 0;

        var catalogOpts = _options.Value.Catalog;
        var prepared = new List<TableMetadata>();

        // EXT-7: Newly discovered tables are created inactive unless ActivateNewTables is explicitly configured
        // (default: false, requiring data steward approval). For OpenMetadata, OpenMetadata.ActivateNewTables is also respected.
        var activateNewTables = catalogOpts.ActivateNewTables ||
            (client.ProviderType == DataCatalogProviderType.OpenMetadata && _options.Value.OpenMetadata.ActivateNewTables);

        foreach (var tableAsset in tables)
        {
            TableMetadata? existing;
            try
            {
                existing = await _metadataRepo.GetTableMetadataAsync(tableAsset.Identifier, ct).ConfigureAwait(false);
            }
            catch (TableMetadataUnavailableException)
            {
                if (restrictToTenant != null)
                {
                    // No name disclosure when restricted
                    continue;
                }

                // an unavailable table is never ingested as a new one (its stored masking rules would be lost).
                warnings.Add($"Table '{tableAsset.Identifier}': skipped, its stored metadata is unavailable (invalid key or index definition).");
                continue;
            }

            if (restrictToTenant != null)
            {
                if (existing == null || !string.Equals(existing.OwnerTenantId, restrictToTenant.Value.Value, StringComparison.Ordinal))
                {
                    continue;
                }
            }
            else if (existing != null && !string.IsNullOrEmpty(existing.OwnerTenantId))
            {
                // GHSA-jfrx-2mgc-pfh5: tenant scoping of synced assets
                if (tableAsset.CustomProperties.TryGetValue("tenant_id", out var assetTenant) &&
                    !string.IsNullOrWhiteSpace(assetTenant) &&
                    !string.Equals(existing.OwnerTenantId, assetTenant, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"Table '{tableAsset.Identifier}': skipped, existing table owned by '{existing.OwnerTenantId}' but asset belongs to '{assetTenant}'. Cross-tenant overwrite prevented.");
                    continue;
                }
            }

            syncedTables++;
            affectedTables.Add(tableAsset.Identifier);

            bool isArt9 = tableAsset.Tags.Any(t => catalogOpts.GdprArticle9Tags.Contains(t, StringComparer.OrdinalIgnoreCase)) ||
                          tableAsset.Classifications.Any(c => catalogOpts.GdprArticle9Tags.Contains(c, StringComparer.OrdinalIgnoreCase));

            var maskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);
            var tableColumns = new List<TableColumn>();
            var flaggedColumns = new List<TableColumn>();

            foreach (var col in tableAsset.Columns)
            {
                syncedColumns++;
                var matchedTag = col.Tags.FirstOrDefault(t => catalogOpts.TagToMaskingRuleMap.ContainsKey(t));

                // POL-5: Art. 9 tags or classifications on a column (e.g. HealthData) make the column sensitive and the table
                // Art. 9 (HIGH, four eyes), as in the OpenMetadata sync.
                var isArt9Column = col.Tags.Any(t => catalogOpts.GdprArticle9Tags.Contains(t, StringComparer.OrdinalIgnoreCase)) ||
                                   col.Classifications.Any(c => catalogOpts.GdprArticle9Tags.Contains(c, StringComparer.OrdinalIgnoreCase));
                isArt9 |= isArt9Column;

                var flagged = matchedTag != null || isArt9Column || col.Tags.Any(t => catalogOpts.PiiTags.Contains(t, StringComparer.OrdinalIgnoreCase));

                // Ratchet: a flagged column receives the PII classification; persisting classifications is additive, so an
                // existing classification of the column is never removed.
                if (matchedTag != null && catalogOpts.TagToMaskingRuleMap.TryGetValue(matchedTag, out var ruleType))
                {
                    maskedColumns++;
                    maskingRules[col.ColumnName] = new MaskingRule
                    {
                        RuleType = ruleType
                    };
                }
                else if (isArt9Column)
                {
                    // POL-5: without an explicit masking rule an Art. 9 column is redacted.
                    maskedColumns++;
                    maskingRules[col.ColumnName] = new MaskingRule
                    {
                        RuleType = "REDACT",
                        Replacement = "[REDACTED]"
                    };
                }

                var tableColumn = new TableColumn
                {
                    ColumnName = col.ColumnName,
                    DataType = col.DataType,
                    Description = col.Description,
                    DocumentationSource = "DataCatalog"
                };
                tableColumns.Add(tableColumn);
                if (flagged)
                {
                    flaggedColumns.Add(tableColumn);
                }
            }

            if (isArt9)
            {
                art9Tables++;
            }

            // Merge with existing masking rules so custom / manual rules are preserved
            if (existing?.ColumnMaskingRules != null)
            {
                foreach (var (colName, rule) in existing.ColumnMaskingRules)
                {
                    if (!maskingRules.ContainsKey(colName))
                    {
                        maskingRules[colName] = rule;
                    }
                }
            }

            var finalDataSourceType = existing?.Table?.DataSourceType ?? DataSourceType.Sql;

            // D-1: external catalogs may report themselves as source type. A supported dialect on
            // the table is kept; a SQL table without one stays unqueryable (fail-closed) until an administrator sets it.
            var finalSourceType = DatabaseDialectExtensions.ResolveCatalogSourceType(tableAsset.SourceType, existing?.Table?.SourceType, out var dialectSupported);
            if (!dialectSupported && finalDataSourceType == DataSourceType.Sql)
            {
                warnings.Add($"Table '{tableAsset.Identifier}': source type '{finalSourceType}' is not a supported SQL dialect; the table cannot be queried until its source type is set.");
            }

            var metadata = new TableMetadata
            {
                Identifier = tableAsset.Identifier,
                Table = new Table
                {
                    SourceName = !string.IsNullOrWhiteSpace(existing?.Table.SourceName) ? existing.Table.SourceName : tableAsset.Identifier.Domain,
                    SchemaName = tableAsset.Identifier.Schema,
                    TableName = tableAsset.Identifier.TableName,
                    DisplayName = tableAsset.DisplayName ?? tableAsset.Identifier.TableName,
                    Description = tableAsset.Description,
                    DocumentationSource = "DataCatalog",
                    DataSourceType = finalDataSourceType,
                    SourceType = finalSourceType,
                    IsActive = existing != null ? existing.Table.IsActive : activateNewTables
                },
                Columns = tableColumns,
                ColumnMaskingRules = maskingRules,
                ColumnClassifications = ClassificationMetadata.AssignPii(flaggedColumns, "DATACATALOG")
            };

            // Issue #94 (D-8): a declared primary key / indexes of the asset, else the IsPrimaryKey column flags (column order),
            // replace the stored definition; an asset that declares nothing leaves it untouched.
            IReadOnlyList<string> declaredPrimaryKey = tableAsset.PrimaryKeyColumns.Count > 0
                ? tableAsset.PrimaryKeyColumns
                : tableAsset.Columns.Where(c => c.IsPrimaryKey).Select(c => c.ColumnName).Take(TableKeyDefinitionValidator.MaxColumnsPerKey + 1).ToList();
            metadata = CatalogKeyDefinitionImport.Apply(
                metadata, declaredPrimaryKey, tableAsset.Indexes, _logger, "DataCatalog",
                tableAsset.PrimaryKeyColumns.Count > 0 ? tableAsset.PrimaryKeyDirections : null);

            // SEC M-32: merge with the persisted state – security flags can only be tightened by a catalog sync.
            metadata = CatalogGovernanceRatchet.Merge(metadata, existing);

            if (!dryRun)
            {
                prepared.Add(metadata);
            }
        }

        if (!dryRun && prepared.Count > 0)
        {
            await FlushPreparedSyncAsync(prepared, ct).ConfigureAwait(false);
        }

        if (!dryRun && affectedTables.Count > 0)
        {
            _logger.LogInformation("Invalidated governance epochs after syncing {Count} tables from catalog.", affectedTables.Count);
        }

        _logger.LogInformation("Data catalog sync complete. Synced {Tables} tables, {Columns} columns ({Masked} masked).",
            syncedTables, syncedColumns, maskedColumns);

        return new CatalogSyncResult(
            SyncedTablesCount: syncedTables,
            SyncedColumnsCount: syncedColumns,
            MaskedColumnsCount: maskedColumns,
            Art9ProtectedTablesCount: art9Tables,
            AffectedTables: affectedTables,
            Warnings: warnings,
            Success: true
        );
    }

    private async Task FlushPreparedSyncAsync(
        IReadOnlyList<TableMetadata> prepared,
        CancellationToken ct)
    {
        var batchResults = await _metadataRepo.UpsertTableMetadataBatchAsync(prepared, ct).ConfigureAwait(false);

        if (batchResults is { Count: var n } && n == prepared.Count)
        {
            for (var j = 0; j < prepared.Count; j++)
            {
                await _epochService.InvalidateEpochAsync(prepared[j].Identifier, CancellationToken.None).ConfigureAwait(false);
            }

            return;
        }

        foreach (var item in prepared)
        {
            try
            {
                await _metadataRepo.UpsertTableMetadataAsync(item, ct).ConfigureAwait(false);
            }
            finally
            {
                await _epochService.InvalidateEpochAsync(item.Identifier, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async Task<TableMetadata?> EnrichOrReferenceTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var client = _client ?? throw new InvalidOperationException("No data catalog client is registered. Enable the 'openmetadata' module.");
        var asset = await client.GetTableAsync(table, ct).ConfigureAwait(false);
        if (asset == null) return null;

        var existing = await _metadataRepo.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
        return existing;
    }
}
