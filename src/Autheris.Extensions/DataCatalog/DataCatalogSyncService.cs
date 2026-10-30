namespace Autheris.Extensions.DataCatalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Models;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
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
    private readonly IPhysicalKeyCatalog _physicalKeys;
    private readonly IAuditWriter _auditWriter;
    private readonly IRebacStore? _rebacStore;
    private readonly ILogger<DataCatalogSyncService> _logger;

    public DataCatalogSyncService(
        IDataCatalogClientFactory clientFactory,
        ITableMetadataRepository metadataRepo,
        IEpochValidationService epochService,
        IOptions<GatewayOptions> options,
        IPhysicalKeyCatalog physicalKeys,
        IAuditWriter auditWriter,
        ILogger<DataCatalogSyncService> logger,
        IRebacStore? rebacStore = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
        _epochService = epochService ?? throw new ArgumentNullException(nameof(epochService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _physicalKeys = physicalKeys ?? throw new ArgumentNullException(nameof(physicalKeys));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _rebacStore = rebacStore;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CatalogSyncResult> SyncCatalogAsync(bool dryRun = false, TenantId? restrictToTenant = null, CancellationToken ct = default)
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

                // SEC-94-01: an unavailable table is never ingested as a new one (its stored masking rules would be lost).
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

            syncedTables++;
            affectedTables.Add(tableAsset.Identifier);

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
                    IsActive = existing != null ? existing.Table.IsActive : activateNewTables
                },
                Columns = tableColumns,
                ColumnMaskingRules = maskingRules
            };

            // Issue #94 (D-8): a declared primary key / indexes of the asset, else the IsPrimaryKey column flags (column order),
            // replace the stored definition; an asset that declares nothing leaves it untouched.
            IReadOnlyList<string> declaredPrimaryKey = tableAsset.PrimaryKeyColumns.Count > 0
                ? tableAsset.PrimaryKeyColumns
                : tableAsset.Columns.Where(c => c.IsPrimaryKey).Select(c => c.ColumnName).Take(TableKeyDefinitionValidator.MaxColumnsPerKey + 1).ToList();
            metadata = CatalogKeyDefinitionImport.Apply(
                metadata, declaredPrimaryKey, tableAsset.Indexes, _logger, "DataCatalog",
                tableAsset.PrimaryKeyColumns.Count > 0 ? tableAsset.PrimaryKeyDirections : null);

            // I403: key and index directions from the source catalog (SQL tables of a supported dialect only, I403-SC-11). Physical
            // data only fills in what neither the asset nor the stored definition declares (I403-SC-06); any read failure changes nothing.
            var physical = finalDataSourceType == DataSourceType.Sql && dialectSupported
                ? await _physicalKeys.ReadAsync(metadata, ct).ConfigureAwait(false)
                : PhysicalKeySnapshot.Unsupported;
            var reconciled = PhysicalKeyDirectionReconciler.Reconcile(metadata, existing, physical);
            foreach (var finding in reconciled.Findings)
            {
                warnings.Add($"Table '{tableAsset.Identifier}': {finding}");
            }

            // SEC M-32: merge with the persisted state – security flags can only be tightened by a catalog sync.
            var merged = CatalogGovernanceRatchet.Merge(reconciled.Metadata, existing);
            if (reconciled.PhysicalApplied && !dryRun &&
                !await TryAuditPhysicalDirectionsAsync(tableAsset.Identifier, existing, restrictToTenant, reconciled, ct).ConfigureAwait(false))
            {
                // I403-SC-07: without the audit record the physical directions are not persisted.
                warnings.Add($"Table '{tableAsset.Identifier}': KEY_DIRECTION_AUDIT_FAILED");
                merged = CatalogGovernanceRatchet.Merge(reconciled.WithoutPhysical, existing);
            }

            metadata = merged;

            if (!dryRun)
            {
                try
                {
                    await _metadataRepo.UpsertTableMetadataAsync(metadata, ct).ConfigureAwait(false);
                }
                finally
                {
                    // SEC-SYNC-EPOCH: the epoch of a table is invalidated right after its own upsert (also when the upsert
                    // fails part-way), not after the whole loop: an exception or cancellation later in the run must not leave
                    // an already written table (e.g. with tightened masking) behind cached decisions of the old metadata.
                    // CancellationToken.None: a cancelled run still invalidates the table it may already have written.
                    await _epochService.InvalidateEpochAsync(tableAsset.Identifier, CancellationToken.None).ConfigureAwait(false);
                }

                if (_rebacStore != null)
                {
                    var tableId = tableAsset.Identifier;
                    var domainObj = RebacTableGate.DomainObjectId(tableId.Domain);
                    var schemaObj = RebacTableGate.SchemaObjectId(tableId.Domain, tableId.Schema);
                    var tableObj = RebacTableGate.ObjectId(tableId);

                    await _rebacStore.AddTuplesAsync([
                        new RebacTuple(tableId.Domain, schemaObj, "parent", tableObj),
                        new RebacTuple(tableId.Domain, domainObj, "parent", schemaObj)
                    ], ct).ConfigureAwait(false);
                }
            }
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

    // I403-SC-07: the audit record (direction lists and table id only; no index or constraint names) is written strictly before the upsert.
    private async Task<bool> TryAuditPhysicalDirectionsAsync(
        TableIdentifier table, TableMetadata? existing, TenantId? restrictToTenant, PhysicalKeyDirectionReconciler.Result reconciled, CancellationToken ct)
    {
        try
        {
            // DT-A-6/DT-A-9: the owner tenant, else the sync's tenant, else the catalog owner (default tenant), else the platform bucket.
            var tenant = existing?.OwnerTenantId is { } owner ? new TenantId(owner)
                : restrictToTenant ?? Autheris.Application.Tenancy.TenantResolver.EffectiveDefaultOf(_options.Value.Tenancy) ?? TenantId.Platform;
            await _auditWriter.RecordRequiredAsync(new AuditLogEntry
            {
                TenantId = tenant,
                EventType = AuditEventTypes.CatalogKeyDefinitionChanged,
                ActorSid = new Sid("system:catalog-sync"),
                TargetTable = table.ToString(),
                Decision = "ALLOW",
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    datasetId = table.ToString(),
                    directionSource = "physical",
                    before = DirectionView(reconciled.WithoutPhysical),
                    after = DirectionView(reconciled.Metadata)
                })
            }, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                _logger.LogWarning("Key direction audit cancelled for table {Table}; the physical directions are not persisted.", table);
            }
            else
            {
                _logger.LogWarning("Key direction audit failed for table {Table} ({ExceptionType}); the physical directions are not persisted.", table, ex.GetType().Name);
            }

            return false;
        }
    }

    private static object DirectionView(TableMetadata metadata) => new
    {
        primaryKeyDirections = KeyDirections.ToWire(metadata.PrimaryKeyDirections),
        uniqueIndexDirections = metadata.Indexes.Where(static i => i.IsUnique).Select(static i => KeyDirections.ToWire(i.Directions)).ToArray()
    };

    public async Task<TableMetadata?> EnrichOrReferenceTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var client = _clientFactory.GetActiveClient();
        var asset = await client.GetTableAsync(table, ct).ConfigureAwait(false);
        if (asset == null) return null;

        var existing = await _metadataRepo.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
        return existing;
    }
}
