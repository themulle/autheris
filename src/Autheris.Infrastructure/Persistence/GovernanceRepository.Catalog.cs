namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Catalog;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Logging;

public partial class GovernanceRepository
{
    public async Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var structural = await GetTableMetadataCoreAsync(table, null, ct).ConfigureAwait(false);
        if (structural is null)
        {
            return null;
        }

        // Issue #111 (A-6): tier and tags are never part of the cached entry; a fresh read is overlaid on every call, without mutating the cached record.
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var overlay = await DatasetCatalogStore.ReadOverlayAsync(
            conn, null, KeyDialect, structural.Table.Id, _logger, ct).ConfigureAwait(false);
        return DatasetCatalogStore.Apply(structural, overlay);
    }

    private async Task<TableMetadata?> GetTableMetadataCoreAsync(
        TableIdentifier table,
        IReadOnlyDictionary<Guid, CatalogKeyDefinitionStore.TableKeyRows>? prefetchedKeys,
        CancellationToken ct)
    {
        var cacheKey = table.ToString().ToLowerInvariant();
        var nowTicks = TimeProvider.System.GetUtcNow().UtcTicks;
        if (_metadataCache.TryGetValue(cacheKey, out var entry) && entry.Metadata != null && (nowTicks - entry.CachedAtTicks < MetadataCacheTtlTicks))
        {
            // PERS-17: a hit is served only while the table epoch is unchanged (a write on another replica bumps it); an unreadable
            // epoch or a mismatch reloads from the database.
            var currentEpoch = await TryReadTableEpochAsync(entry.Metadata.Table.Id, ct).ConfigureAwait(false);
            if (currentEpoch.HasValue && currentEpoch.Value == entry.Epoch)
            {
                return entry.Metadata;
            }
        }

        Guid? tableId = null;
        Table? tableEntity = null;

        await using (var conn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = CatalogSql.SelectTableEntitySql;
                cmd.AddBound("@domain", table.Domain);
                cmd.AddBound("@schema", table.Schema);
                cmd.AddBound("@table", table.TableName);

                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    tableEntity = ReadTableEntity(reader, _logger);
                    tableId = tableEntity.Id;
                }
            }

            if (tableEntity == null || !tableId.HasValue) return null;

            // PERS-17: the epoch is read before the metadata, so a concurrent write can only make the cached entry look older.
            long? loadedEpoch = null;
            try
            {
                loadedEpoch = await ReadTableEpochAsync(conn, tableId.Value, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCatalogNotice("The table epoch could not be read; the loaded metadata is not cached.");
            }

            List<TableColumn> columns;
            Dictionary<string, MaskingRule> maskingRules;

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SelectTableColumnsSql;
                cmd.AddBound("@tableId", tableId.Value.ToString());
                (columns, maskingRules) = await LoadTableColumnsAsync(cmd, _logger, ct).ConfigureAwait(false);
            }

            IReadOnlyList<string> pkColumns;
            IReadOnlyList<KeyColumnDirection> pkDirections;
            IReadOnlyList<TableIndexDefinition> indexes;
            try
            {
                (pkColumns, pkDirections, indexes) = await CatalogKeyDefinitionStore.ReadAsync(
                    conn, null, KeyDialect, table, tableId.Value, columns, ct,
                    _keyTables, prefetchedKeys is not null && prefetchedKeys.TryGetValue(tableId.Value, out var keyRows) ? keyRows : null).ConfigureAwait(false);
            }
            catch (TableMetadataUnavailableException)
            {
                LogCatalogNotice("Table metadata is unavailable: a declared key or index definition is invalid.");
                throw;
            }

            var physicalIndexes = await CatalogPhysicalIndexStore.ReadAsync(
                conn, null, KeyDialect, tableId.Value, columns, ct).ConfigureAwait(false);

            var rowFilterEvaluation = await RowFilterEvaluationStore.ReadObjectAsync(
                conn, null, KeyDialect, tableId.Value, ct).ConfigureAwait(false);

            var result = new TableMetadata
            {
                Identifier = table,
                Table = tableEntity,
                Columns = columns,
                ColumnMaskingRules = maskingRules,
                PrimaryKeyColumns = pkColumns,
                PrimaryKeyDirections = pkDirections,
                Indexes = indexes,
                PhysicalIndexes = physicalIndexes,
                RowFilterEvaluation = rowFilterEvaluation
            };
            result.ValidateRequiredFilterColumns();

            if (AfterMetadataSnapshotForTesting is { } hook)
            {
                await hook(ct).ConfigureAwait(false);
            }

            if (loadedEpoch.HasValue)
            {
                _metadataCache[cacheKey] = (result, TimeProvider.System.GetUtcNow().UtcTicks, loadedEpoch.Value);
            }

            return result;
        }
    }

    /// <summary>The current epoch, or null when it cannot be read (the cache is then bypassed and the table is loaded from the database).</summary>
    private async Task<long?> TryReadTableEpochAsync(Guid tableId, CancellationToken ct)
    {
        try
        {
            await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
            return await ReadTableEpochAsync(conn, tableId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCatalogNotice("The table epoch could not be read; the metadata cache is bypassed (fail-closed).");
            return null;
        }
    }

    public async Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
    {
        var tables = new List<(Guid Id, TableIdentifier Identifier)>();

        await using (var conn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = CatalogSql.SelectActiveTableSummariesSql;
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        tables.Add((Guid.Parse(reader.GetString(0)), new TableIdentifier(reader.GetString(1), reader.GetString(2), reader.GetString(3))));
                    }
                    catch (ArgumentException)
                    {
                        LogCatalogNotice("Catalog table has an invalid identifier and was skipped.");
                    }
                }
            }
        }

        // PERS-17: all epochs in one query; a cached entry is valid only while its epoch is unchanged (unreadable epochs bypass the cache).
        Dictionary<Guid, long>? epochs = null;
        try
        {
            await using var epochConn = await OpenConnectionAsync(ct).ConfigureAwait(false);
            epochs = await ReadAllTableEpochsAsync(epochConn, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCatalogNotice("The table epochs could not be read; the metadata cache is bypassed (fail-closed).");
        }

        bool TryGetValidCached(Guid tableId, string key, out TableMetadata? metadata)
        {
            metadata = null;
            if (epochs != null &&
                _metadataCache.TryGetValue(key, out var hit) &&
                hit.Metadata != null &&
                TimeProvider.System.GetUtcNow().UtcTicks - hit.CachedAtTicks < MetadataCacheTtlTicks &&
                (epochs.TryGetValue(tableId, out var current) ? current : -1) == hit.Epoch)
            {
                metadata = hit.Metadata;
                return true;
            }

            return false;
        }

        var uncached = tables
            .Where(t => !TryGetValidCached(t.Id, t.Identifier.ToString().ToLowerInvariant(), out _))
            .Select(t => t.Id)
            .ToList();

        List<TableMetadata> result;
        if (uncached.Count == 0)
        {
            result = new List<TableMetadata>(tables.Count);
            foreach (var (tid, t) in tables)
            {
                if (TryGetValidCached(tid, t.ToString().ToLowerInvariant(), out var cachedMetadata))
                {
                    result.Add(cachedMetadata!);
                }
            }
        }
        else
        {
            await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);

            List<(Guid Id, TableIdentifier Identifier, Table Table)> activeTables;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = CatalogSql.SelectActiveTableEntitiesSql;
                activeTables = await LoadActiveTablesAsync(cmd, ct).ConfigureAwait(false);
            }

            Dictionary<Guid, List<TableColumn>> columnsByTable;
            Dictionary<Guid, Dictionary<string, MaskingRule>> maskingRulesByTable;
            HashSet<Guid> corruptedTableIds;

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SelectActiveBatchTableColumnsSql;
                (columnsByTable, maskingRulesByTable, corruptedTableIds) = await LoadBatchTableColumnsAsync(cmd, ct).ConfigureAwait(false);
            }

            Dictionary<Guid, CatalogKeyDefinitionStore.TableKeyRows>? prefetchedKeys = null;
            Dictionary<Guid, List<CatalogPhysicalIndexStore.Row>>? prefetchedPhysical = null;
            var allActiveTableIds = activeTables.Select(t => t.Id).ToList();
            if (await CatalogKeyDefinitionStore.TablesExistAsync(conn, null, KeyDialect, ct, _keyTables).ConfigureAwait(false))
            {
                prefetchedKeys = await CatalogKeyDefinitionStore.ReadBatchAsync(conn, null, KeyDialect, allActiveTableIds, ct).ConfigureAwait(false);
                prefetchedPhysical = await CatalogPhysicalIndexStore.ReadBatchAsync(conn, null, KeyDialect, allActiveTableIds, ct).ConfigureAwait(false);
            }

            var rowFilterStrategies = await RowFilterEvaluationStore.ReadBatchObjectAsync(
                conn, null, KeyDialect, ct).ConfigureAwait(false);

            result = new List<TableMetadata>(activeTables.Count);
            foreach (var (tid, identifier, tableEntity) in activeTables)
            {
                if (corruptedTableIds.Contains(tid))
                {
                    LogCatalogNotice("A catalog table was omitted: its column or masking rule definition is invalid (fail-closed).");
                    continue;
                }

                var cacheKey = identifier.ToString().ToLowerInvariant();
                if (TryGetValidCached(tid, cacheKey, out var cachedMetadata))
                {
                    result.Add(cachedMetadata!);
                    continue;
                }

                var columns = columnsByTable.TryGetValue(tid, out var cols) ? cols : new List<TableColumn>();
                var maskingRules = maskingRulesByTable.TryGetValue(tid, out var rules)
                    ? rules
                    : new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);

                IReadOnlyList<string> pkColumns;
                IReadOnlyList<KeyColumnDirection> pkDirections;
                IReadOnlyList<TableIndexDefinition> indexes;
                try
                {
                    (pkColumns, pkDirections, indexes) = await CatalogKeyDefinitionStore.ReadAsync(
                        conn, null, KeyDialect, identifier, tid, columns, ct,
                        _keyTables, prefetchedKeys is not null && prefetchedKeys.TryGetValue(tid, out var keyRows) ? keyRows : null).ConfigureAwait(false);
                }
                catch (TableMetadataUnavailableException)
                {
                    LogCatalogNotice("A catalog table was omitted: its declared key or index definition is invalid.");
                    continue;
                }

                rowFilterStrategies.TryGetValue(tid, out var rowFilterEvaluation);

                var meta = new TableMetadata
                {
                    Identifier = identifier,
                    Table = tableEntity,
                    Columns = columns,
                    ColumnMaskingRules = maskingRules,
                    PrimaryKeyColumns = pkColumns,
                    PrimaryKeyDirections = pkDirections,
                    Indexes = indexes,
                    PhysicalIndexes = prefetchedPhysical is not null && prefetchedPhysical.TryGetValue(tid, out var physicalRows) ? CatalogPhysicalIndexStore.Assemble(columns, physicalRows) : Array.Empty<PhysicalIndexDefinition>(),
                    RowFilterEvaluation = rowFilterEvaluation
                };

                try
                {
                    meta.ValidateRequiredFilterColumns();
                }
                catch (CatalogValidationException)
                {
                    LogCatalogNotice("A catalog table was omitted: its required filter column definition is invalid.");
                    continue;
                }

                if (epochs != null)
                {
                    _metadataCache[cacheKey] = (meta, TimeProvider.System.GetUtcNow().UtcTicks, epochs.TryGetValue(tid, out var loaded) ? loaded : -1);
                }

                result.Add(meta);
            }
        }

        if (result.Count == 0)
        {
            return result;
        }

        // Issue #111 (F-C6): rows served from the structural cache and freshly loaded rows alike get a fresh tier/tags overlay (`with`, no mutation).
        await using (var overlayConn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            var overlay = await DatasetCatalogStore.ReadOverlayAsync(
                overlayConn, null, KeyDialect, null, _logger, ct).ConfigureAwait(false);
            for (var i = 0; i < result.Count; i++)
            {
                result[i] = DatasetCatalogStore.Apply(result[i], overlay);
            }
        }

        return result;
    }

    public async Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
    {
        Autheris.Application.Catalog.Services.CatalogSourceNameValidator.Validate(metadata);
        metadata.ValidateRequiredFilterColumns();
        Autheris.Application.Catalog.Semantic.SemanticVirtualTables.EnsureNamespaceRespected(metadata);
        // Only a replacing write validates (and later persists) the definition; any other upsert never reads or rewrites it.
        if (metadata.ReplaceKeyDefinition)
        {
            metadata = TableKeyDefinitionValidator.Validate(metadata);
        }

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var tableId = metadata.Table.Id == Guid.Empty ? Guid.NewGuid() : metadata.Table.Id;
            var tableExisted = false;
            var structuralChange = false;
            var docs = new TableDocumentation(metadata.Table.Description, metadata.Table.LongDescription, metadata.Table.DocumentationSource);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = SelectTableForUpdateSql;
                cmd.AddBound("@domain", metadata.Identifier.Domain);
                cmd.AddBound("@schema", metadata.Identifier.Schema);
                cmd.AddBound("@table", metadata.Identifier.TableName);

                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    tableId = Guid.Parse(reader.GetString(0));
                    tableExisted = true;
                    (structuralChange, docs) = ReadExistingTableForUpsert(reader, metadata.Table, _options?.Catalog?.DocumentationSourcePrecedence);
                }
                else
                {
                    structuralChange = true;
                }
            }

            var httpEndpointJson = metadata.Table.HttpEndpoint != null
                ? JsonSerializer.Serialize(metadata.Table.HttpEndpoint)
                : null;

            await using (var upsertCmd = conn.CreateCommand())
            {
                upsertCmd.Transaction = tx;
                upsertCmd.CommandText = UpsertTableSql;
                upsertCmd.AddBound("@id", tableId.ToString());
                upsertCmd.AddBound("@sourceType", metadata.Table.SourceType);
                upsertCmd.AddBound("@domain", metadata.Identifier.Domain);
                upsertCmd.AddBound("@schema", metadata.Identifier.Schema);
                upsertCmd.AddBound("@tableName", metadata.Identifier.TableName);
                upsertCmd.AddBound("@displayName", metadata.Table.DisplayName);
                upsertCmd.AddBound("@sensitivity", metadata.Table.Sensitivity);
                upsertCmd.AddBound("@fourEyes", metadata.Table.RequiresFourEyes ? 1 : 0);
                upsertCmd.AddBound("@active", metadata.Table.IsActive ? 1 : 0);
                upsertCmd.AddBound("@dst", (int)metadata.Table.DataSourceType);
                upsertCmd.AddBound("@httpJson", (object?)httpEndpointJson ?? DBNull.Value);
                upsertCmd.AddBound("@plugin", (object?)metadata.Table.PluginName ?? DBNull.Value);
                upsertCmd.AddBound("@desc", (object?)docs.Description ?? DBNull.Value);
                upsertCmd.AddBound("@longDesc", (object?)docs.LongDescription ?? DBNull.Value);
                upsertCmd.AddBound("@docSource", (object?)docs.Source ?? DBNull.Value);
                upsertCmd.AddBound("@requiredFilterColumnsJson", metadata.RequiredFilterColumns.IsDefaultOrEmpty ? (object)DBNull.Value : JsonSerializer.Serialize(metadata.RequiredFilterColumns));
                upsertCmd.AddBound("@textsJson", (object?)CatalogLocalizationPersistence.SerializeLocalizedTexts(metadata.Table.Texts) ?? DBNull.Value);
                upsertCmd.AddBound("@minGroupSize", (object?)metadata.Table.MinGroupSize ?? DBNull.Value);
                upsertCmd.AddBound("@nonPersonal", metadata.Table.NonPersonal);
                await upsertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // Issue #111 (A-10): the configured default tier is given to a NEW table only; the Hidden default writes no row.
            if (!tableExisted && _options?.Catalog?.DefaultVisibility == DatasetVisibility.Summary)
            {
                await DatasetCatalogStore.InsertDefaultAsync(conn, tx, KeyDialect, tableId, DatasetVisibility.Summary, ct).ConfigureAwait(false);
            }

            var (existingColsByName, existingMaskingRules, colIdByName) = await LoadExistingColumnsAndMaskingRulesAsync(conn, tx, tableId, ct).ConfigureAwait(false);
            if (HasMaskingRulesChanged(existingMaskingRules, metadata.ColumnMaskingRules))
            {
                structuralChange = true;
            }

            if (await UpsertColumnsAndMaskingRulesAsync(conn, tx, InsertColumnSql, tableId, metadata, existingColsByName, colIdByName, ct, (existing, incoming) => ShouldOverwriteDocumentation(_options?.Catalog?.DocumentationSourcePrecedence, existing, incoming)).ConfigureAwait(false))
            {
                structuralChange = true;
            }

            if (metadata.ReplaceKeyDefinition)
            {
                structuralChange = true;
                await CatalogKeyDefinitionStore.ReplaceAsync(conn, tx, KeyDialect, tableId, metadata, colIdByName, ct).ConfigureAwait(false);
            }

            await IncrementTableEpochInternalAsync(conn, tx, metadata.Identifier, ct).ConfigureAwait(false);
            var structureDelta = structuralChange ? 1L : 0L;
            var contentDelta = structuralChange ? 0L : 1L;
            var newVersion = await BumpCatalogVersionInternalAsync(conn, tx, structureDelta, contentDelta, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }

        await InvalidateTableEpochAsync(metadata.Identifier, ct).ConfigureAwait(false);

        try
        {
            return await GetTableMetadataAsync(metadata.Identifier, ct).ConfigureAwait(false) ?? metadata;
        }
        catch (TableMetadataUnavailableException)
        {
            // The stored definition is invalid and this write did not replace it: the upsert itself succeeded and stays untouched by it.
            return metadata;
        }
    }

    public async Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
        TableIdentifier table,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<TableIndexDefinition> indexes,
        IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
        CancellationToken ct = default)
    {
        TableKeyDefinitionChange? change;
        await using (var conn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                change = await CatalogKeyDefinitionStore.ReplaceNarrowAsync(
                    conn, tx, KeyDialect, table, primaryKey, indexes, primaryKeyDirections,
                    () => IncrementTableEpochInternalAsync(conn, tx, table, ct), ct).ConfigureAwait(false);
                if (change is null)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return null;
                }

                var newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 1, 0, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        await InvalidateTableEpochAsync(table, ct).ConfigureAwait(false);
        return change;
    }

    public async Task<RowFilterEvaluationChange?> SetRowFilterEvaluationAsync(
        TableIdentifier table, RowFilterEvaluationSettings? settings, Sid actor, CancellationToken ct = default)
    {
        RowFilterEvaluationChange? change;
        await using (var conn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                change = await RowFilterEvaluationStore.SetObjectAsync(
                    conn, tx, KeyDialect, table, settings, actor,
                    () => IncrementTableEpochInternalAsync(conn, tx, table, ct), ct).ConfigureAwait(false);
                if (change is null)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return null;
                }

                var newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 1, 0, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        await InvalidateTableEpochAsync(table, ct).ConfigureAwait(false);
        return change;
    }

    public async Task<DatasetVisibilityCounts> GetDatasetVisibilityCountsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await DatasetCatalogStore.CountByVisibilityAsync(conn, null, KeyDialect, ct).ConfigureAwait(false);
    }

    public async Task<DatasetCatalogSettings?> GetDatasetCatalogSettingsAsync(TableIdentifier table, bool strict = true, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await DatasetCatalogStore.ReadSettingsAsync(
            conn, null, KeyDialect, table, strict, _logger, ct).ConfigureAwait(false);
    }

    public Task<DatasetCatalogChange?> ReplaceDatasetTagsAsync(
        TableIdentifier table, IReadOnlyList<string> tags, DatasetTagSource source, Sid actor, CancellationToken ct = default) =>
        WriteCatalogSettingsAsync(table, (conn, tx) =>
            DatasetCatalogStore.ReplaceTagsAsync(conn, tx, KeyDialect, table, tags, source, actor, ct), ct);

    public Task<DatasetCatalogChange?> ChangeDatasetTagsAsync(
        TableIdentifier table, IReadOnlyList<string> add, IReadOnlyList<string> remove, Sid actor, CancellationToken ct = default) =>
        WriteCatalogSettingsAsync(table, (conn, tx) =>
            DatasetCatalogStore.ChangeTagsAsync(conn, tx, KeyDialect, table, add, remove, actor, ct), ct);

    public Task<DatasetCatalogChange?> SetDatasetVisibilityAsync(
        TableIdentifier table, DatasetVisibility visibility, Sid actor, CancellationToken ct = default) =>
        WriteCatalogSettingsAsync(table, (conn, tx) =>
            DatasetCatalogStore.SetVisibilityAsync(conn, tx, KeyDialect, table, visibility, actor, ct), ct);

    public Task<DatasetCatalogChange?> SetDatasetPrivacyAsync(
        TableIdentifier table, int? minGroupSize, bool? nonPersonal, Sid actor, CancellationToken ct = default) =>
        WriteCatalogSettingsAsync(table, (conn, tx) =>
            DatasetCatalogStore.SetPrivacyAsync(conn, tx, KeyDialect, table, minGroupSize, nonPersonal, actor, ct), ct);

    /// <summary>Issue #111: one transaction (locked before read, write, after read); no epoch (tags are not policy inputs); the local metadata entry is dropped after the commit.</summary>
    private async Task<DatasetCatalogChange?> WriteCatalogSettingsAsync(
        TableIdentifier table, Func<DbConnection, DbTransaction, Task<DatasetCatalogChange?>> write, CancellationToken ct)
    {
        DatasetCatalogChange? change;
        await using (var conn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                change = await write(conn, tx).ConfigureAwait(false);
                if (change is null)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return null;
                }

                Autheris.Application.Catalog.CatalogVersion? newVersion = null;
                if (change.Changed)
                {
                    newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 0, 1, ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                if (change.Changed && newVersion.HasValue)
                {
                    await PublishCatalogChangedEventAsync(newVersion.Value, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        if (change.Changed)
        {
            InvalidateTableMetadata(table.ToString().ToLowerInvariant());
        }

        return change;
    }

    public async Task<TableIdentifier?> GetActiveTableIdentifierAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await GetActiveTableIdentifierAsync(conn, CatalogSql.ActiveTableIdentifierSql, table, ct).ConfigureAwait(false);
    }

    public async Task<TableIdentifier?> GetActiveTableIdentifierByIdAsync(Guid tableId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await GetActiveTableIdentifierByIdAsync(conn, tableId, ct).ConfigureAwait(false);
    }

    public async Task<long> GetTableEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await GetTableEpochAsync(conn, CatalogSql.TableEpochSql, table, ct).ConfigureAwait(false);
    }

    public async Task<long> IncrementTableEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var newEpoch = await IncrementTableEpochInternalAsync(conn, tx, table, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            await InvalidateTableEpochAsync(table, ct).ConfigureAwait(false);
            return newEpoch;
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await GetRelationsForTableAsync(conn, RelationsForParentTableSql, parentTable, _logger, ct).ConfigureAwait(false);
    }

    public async Task<RelationWriteResult> CreateRelationAsync(TableRelation relation, CancellationToken ct = default)
    {
        // Rule check before any connection is opened.
        RelationWriteSupport.ValidateKeysOrThrow(relation);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        // One connection, one explicit transaction; disposing it uncommitted (failure or cancellation) rolls back all rows.
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var parentTableId = await ResolveActiveTableIdAsync(conn, tx, relation.RelationName, relation.ParentTableIdentifier, ct).ConfigureAwait(false);
        var childTableId = await ResolveActiveTableIdAsync(conn, tx, relation.RelationName, relation.ChildTableIdentifier, ct).ConfigureAwait(false);
        var parentKeys = RelationWriteSupport.Canonicalize(relation.RelationName, "parent", relation.JoinKeysParent, await LoadColumnNamesAsync(conn, tx, parentTableId, ct).ConfigureAwait(false));
        var childKeys = RelationWriteSupport.Canonicalize(relation.RelationName, "child", relation.JoinKeysChild, await LoadColumnNamesAsync(conn, tx, childTableId, ct).ConfigureAwait(false));

        string storedId;
        bool created;
        // Natural-key upsert: only discovery-owned columns are in the update list (I-18 keeps allow_cross_data_source out of it).
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = InsertRelationSql;
            cmd.AddBound("@id", relation.Id.ToString());
            cmd.AddBound("@pId", parentTableId);
            cmd.AddBound("@cId", childTableId);
            cmd.AddBound("@relName", relation.RelationName);
            cmd.AddBound("@card", relation.Cardinality.ToString());
            cmd.AddBound("@allowCross", IsPostgreSql ? relation.AllowCrossDataSource : (relation.AllowCrossDataSource ? 1 : 0));
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            storedId = reader.GetString(0);
            created = IsPostgreSql ? reader.GetBoolean(1) : string.Equals(reader.GetString(1), "INSERT", StringComparison.OrdinalIgnoreCase);
        }

        var keysChanged = await ReplaceRelationKeysAsync(conn, tx, storedId, created, parentKeys, childKeys, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        var newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 1, 0, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
        return new RelationWriteResult(Guid.Parse(storedId), created, keysChanged);
    }

    /// <summary>Resolves an active table id from its identifier: zero or several matches throw (no LIMIT 1).</summary>
    private static Task<string> ResolveActiveTableIdAsync(DbConnection conn, DbTransaction tx, string relationName, TableIdentifier table, CancellationToken ct)
        => ResolveActiveTableIdAsync(conn, tx, CatalogSql.ResolveActiveTableIdSql, relationName, table, ct);

    public async Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await GetDataOwnersForTableAsync(conn, CatalogSql.DataOwnersForTableSql, table, ct).ConfigureAwait(false);
    }

    public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default) =>
        IsAuthorizedApproverForTableInternalAsync(table, approverSid, null, ct);

    public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, string? itsmApproverAccount, CancellationToken ct = default) =>
        IsAuthorizedApproverForTableInternalAsync(table, approverSid, itsmApproverAccount, ct);

    public async Task<IReadOnlySet<string>> GetTransitiveRolesAsync(Sid subjectSid, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await GetTransitiveRolesAsync(conn, subjectSid, ct).ConfigureAwait(false);
    }

    public async Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation delegation, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = InsertDelegationSql;
            cmd.AddBound("@id", delegation.Id.ToString());
            cmd.AddBound("@ownerId", delegation.DataOwnerId.ToString());
            cmd.AddBound("@delegateSid", delegation.DelegateSid.Value);
            cmd.AddBound("@validFrom", delegation.ValidFrom.ToString("O"));
            cmd.AddBound("@validTo", delegation.ValidTo.ToString("O"));
            cmd.AddBound("@reason", delegation.Reason);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            var newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 0, 1, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
            return delegation;
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> SetRelationCrossDataSourceOptInAsync(Guid relationId, bool allowCrossDataSource, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var updated = await UpdateRelationCrossDataSourceAsync(conn, tx, relationId, IsPostgreSql ? allowCrossDataSource : (allowCrossDataSource ? 1 : 0), ct).ConfigureAwait(false);
            if (updated)
            {
                var newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 1, 0, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
                return true;
            }

            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<Autheris.Application.Catalog.CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await GetCatalogVersionAsync(conn, CatalogVersionSql, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await GetAllRelationsAsync(conn, AllRelationsSql, _logger, ct).ConfigureAwait(false);
    }

    private async Task<Autheris.Application.Catalog.CatalogVersion> BumpCatalogVersionInternalAsync(
        DbConnection conn,
        DbTransaction tx,
        long structureDelta,
        long contentDelta,
        CancellationToken ct)
    {
        ValidateVersionDeltas(structureDelta, contentDelta);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = BumpCatalogVersionSql;
        cmd.AddBound("@structureDelta", structureDelta);
        cmd.AddBound("@contentDelta", contentDelta);
        cmd.AddBound("@now", TimeProvider.System.GetUtcNow(), System.Data.DbType.DateTimeOffset);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new Autheris.Application.Catalog.CatalogVersion(reader.GetInt64(0), reader.GetInt64(1));
        }
        return new Autheris.Application.Catalog.CatalogVersion(1, 1);
    }

    private void LogCatalogNotice(string message) => _logger?.LogWarning("Catalog notice: {Notice}", message);
}
