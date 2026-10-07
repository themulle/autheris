namespace Autheris.Extensions.DataCatalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Models;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class DataCatalogSyncService : IDataCatalogSyncService
{
    private readonly IDataCatalogClientFactory _clientFactory;
    private readonly ITableMetadataRepository _metadataRepo;
    private readonly IEpochValidationService _epochService;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<DataCatalogSyncService> _logger;

    public DataCatalogSyncService(
        IDataCatalogClientFactory clientFactory,
        ITableMetadataRepository metadataRepo,
        IEpochValidationService epochService,
        IOptions<GatewayOptions> options,
        ILogger<DataCatalogSyncService> logger)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
        _epochService = epochService ?? throw new ArgumentNullException(nameof(epochService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CatalogSyncResult> SyncCatalogAsync(bool dryRun = false, CancellationToken ct = default)
    {
        var client = _clientFactory.GetActiveClient();
        _logger.LogInformation("Starting data catalog sync from provider {Provider} (DryRun: {DryRun})...", client.ProviderType, dryRun);

        var tables = await client.GetTablesAsync(null, ct).ConfigureAwait(false);
        var affectedTables = new List<TableIdentifier>();
        var warnings = new List<string>();

        int syncedTables = 0;
        int syncedColumns = 0;
        int maskedColumns = 0;
        int art9Tables = 0;

        var catalogOpts = _options.Value.Catalog;

        // SEC E-09: tables first seen through the OpenMetadata catalog provider are created inactive unless
        // OpenMetadata.ActivateNewTables is set (same rule as the OpenMetadata sync and webhook).
        var activateNewTables = client.ProviderType != DataCatalogProviderType.OpenMetadata || _options.Value.OpenMetadata.ActivateNewTables;

        foreach (var tableAsset in tables)
        {
            syncedTables++;
            affectedTables.Add(tableAsset.Identifier);

            var existing = await _metadataRepo.GetTableMetadataAsync(tableAsset.Identifier, ct).ConfigureAwait(false);

            bool isArt9 = tableAsset.Tags.Any(t => catalogOpts.GdprArticle9Tags.Contains(t, StringComparer.OrdinalIgnoreCase)) ||
                          tableAsset.Classifications.Any(c => catalogOpts.GdprArticle9Tags.Contains(c, StringComparer.OrdinalIgnoreCase));

            var maskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);
            var tableColumns = new List<TableColumn>();

            foreach (var col in tableAsset.Columns)
            {
                syncedColumns++;
                var matchedTag = col.Tags.FirstOrDefault(t => catalogOpts.TagToMaskingRuleMap.ContainsKey(t));

                // POL-5: Art. 9 tags or classifications on a column (e.g. HealthData) make the column sensitive and the table
                // Art. 9 (HIGH, four eyes), as in the OpenMetadata sync.
                var isArt9Column = col.Tags.Any(t => catalogOpts.GdprArticle9Tags.Contains(t, StringComparer.OrdinalIgnoreCase)) ||
                                   col.Classifications.Any(c => catalogOpts.GdprArticle9Tags.Contains(c, StringComparer.OrdinalIgnoreCase));
                isArt9 |= isArt9Column;

                var isSensitive = matchedTag != null || isArt9Column || col.Tags.Any(t => catalogOpts.PiiTags.Contains(t, StringComparer.OrdinalIgnoreCase));

                // Ratchet: Never downgrade sensitive status if existing column is already sensitive
                var existingCol = existing?.Columns?.FirstOrDefault(c => string.Equals(c.ColumnName, col.ColumnName, StringComparison.OrdinalIgnoreCase));
                if (existingCol?.IsSensitive == true)
                {
                    isSensitive = true;
                }

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

                tableColumns.Add(new TableColumn
                {
                    ColumnName = col.ColumnName,
                    DataType = col.DataType,
                    IsSensitive = isSensitive,
                    Description = col.Description,
                    DocumentationSource = "DataCatalog"
                });
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

            // Ratchet: Sensitivity must not be downgraded from HIGH/RESTRICTED to NORMAL
            string finalSensitivity = isArt9 ? "HIGH" : (existing?.Table?.Sensitivity ?? "NORMAL");
            if (existing?.Table != null &&
                (string.Equals(existing.Table.Sensitivity, "HIGH", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(existing.Table.Sensitivity, "RESTRICTED", StringComparison.OrdinalIgnoreCase)) &&
                !isArt9)
            {
                finalSensitivity = existing.Table.Sensitivity;
                warnings.Add($"Table '{tableAsset.Identifier}': Retained existing high sensitivity '{existing.Table.Sensitivity}'.");
            }

            bool finalRequiresFourEyes = isArt9 || (existing?.Table?.RequiresFourEyes == true);
            var finalDataSourceType = existing?.Table?.DataSourceType ?? DataSourceType.Sql;

            // D-1: catalogs such as Alation, Collibra or Purview report themselves as source type. A supported dialect on
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
                    Sensitivity = finalSensitivity,
                    RequiresFourEyes = finalRequiresFourEyes,
                    IsActive = existing != null || activateNewTables
                },
                Columns = tableColumns,
                ColumnMaskingRules = maskingRules
            };

            // SEC M-32: merge with the persisted state – security flags can only be tightened by a catalog sync.
            metadata = CatalogGovernanceRatchet.Merge(metadata, existing);

            if (!dryRun)
            {
                await _metadataRepo.UpsertTableMetadataAsync(metadata, ct).ConfigureAwait(false);
            }
        }

        if (!dryRun && affectedTables.Count > 0)
        {
            foreach (var affectedTable in affectedTables)
            {
                await _epochService.InvalidateEpochAsync(affectedTable, ct).ConfigureAwait(false);
            }
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

    public async Task<TableMetadata?> EnrichOrReferenceTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var client = _clientFactory.GetActiveClient();
        var asset = await client.GetTableAsync(table, ct).ConfigureAwait(false);
        if (asset == null) return null;

        var existing = await _metadataRepo.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
        return existing;
    }
}
