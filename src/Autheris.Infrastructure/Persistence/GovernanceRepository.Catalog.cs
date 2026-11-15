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
                    tableEntity = CatalogRowHydrator.ReadTableEntity(reader, _logger);
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
                (columns, maskingRules) = await CatalogRowHydrator.LoadTableColumnsAsync(cmd, _logger, ct).ConfigureAwait(false);
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
            var allActiveTableIds = activeTables.Select(t => t.Id).ToList();
            if (await CatalogKeyDefinitionStore.TablesExistAsync(conn, null, KeyDialect, ct, _keyTables).ConfigureAwait(false))
            {
                prefetchedKeys = await CatalogKeyDefinitionStore.ReadBatchAsync(conn, null, KeyDialect, allActiveTableIds, ct).ConfigureAwait(false);
            }

            var rowFilterStrategies = await RowFilterEvaluationStore.ReadBatchObjectAsync(
                conn, null, KeyDialect, ct).ConfigureAwait(false);

            result = await AssembleTableMetadataListAsync(
                conn,
                activeTables,
                columnsByTable,
                maskingRulesByTable,
                corruptedTableIds,
                epochs,
                (tid, cacheKey) => TryGetValidCached(tid, cacheKey, out var hit) ? (true, hit) : (false, null),
                ct,
                rowFilterStrategies).ConfigureAwait(false);
        }

        return await ApplyDatasetCatalogOverlayAsync(result, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<TableIdentifier>> GetTablesChangedSinceAsync(long sinceVersion, CancellationToken ct = default)
        => CatalogDeltaQueryHelper.GetTablesChangedSinceAsync(this, sinceVersion, ct);

    public Task<IReadOnlyList<TableMetadata>> GetTablesAsync(IReadOnlyCollection<TableIdentifier> identifiers, CancellationToken ct = default)
        => CatalogDeltaQueryHelper.GetTablesAsync(this, identifiers, ct);

    internal async Task<List<TableMetadata>> AssembleTableMetadataListAsync(
        DbConnection conn,
        List<(Guid Id, TableIdentifier Identifier, Table Table)> activeTables,
        Dictionary<Guid, List<TableColumn>> columnsByTable,
        Dictionary<Guid, Dictionary<string, MaskingRule>> maskingRulesByTable,
        HashSet<Guid> corruptedTableIds,
        Dictionary<Guid, long>? epochs,
        Func<Guid, string, (bool Hit, TableMetadata? Metadata)>? tryGetCached,
        CancellationToken ct,
        Dictionary<Guid, RowFilterEvaluationSettings>? prefetchedRowFilterStrategies = null)
    {
        Dictionary<Guid, CatalogKeyDefinitionStore.TableKeyRows>? prefetchedKeys = null;
        var allActiveTableIds = activeTables.Select(t => t.Id).ToList();
        if (allActiveTableIds.Count > 0 && await CatalogKeyDefinitionStore.TablesExistAsync(conn, null, KeyDialect, ct, _keyTables).ConfigureAwait(false))
        {
            prefetchedKeys = await CatalogKeyDefinitionStore.ReadBatchAsync(conn, null, KeyDialect, allActiveTableIds, ct).ConfigureAwait(false);
        }

        var rowFilterStrategies = prefetchedRowFilterStrategies
            ?? await RowFilterEvaluationStore.ReadBatchObjectAsync(conn, null, KeyDialect, ct).ConfigureAwait(false);

        var result = new List<TableMetadata>(activeTables.Count);
        foreach (var (tid, identifier, tableEntity) in activeTables)
        {
            if (corruptedTableIds.Contains(tid))
            {
                LogCatalogNotice("A catalog table was omitted: its column or masking rule definition is invalid (fail-closed).");
                continue;
            }

            var cacheKey = identifier.ToString().ToLowerInvariant();
            if (tryGetCached is not null)
            {
                var (hit, cachedMetadata) = tryGetCached(tid, cacheKey);
                if (hit)
                {
                    result.Add(cachedMetadata!);
                    continue;
                }
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

        return result;
    }

    internal async Task<List<TableMetadata>> ApplyDatasetCatalogOverlayAsync(List<TableMetadata> result, CancellationToken ct)
    {
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

    public async Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) =>
        (await UpsertTableMetadataWithResultAsync(metadata, ct).ConfigureAwait(false)).Metadata;

    public async Task<CatalogTableUpsertResult> UpsertTableMetadataWithResultAsync(TableMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return (await UpsertTableMetadataBatchAsync([metadata], ct).ConfigureAwait(false))[0];
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
                await SetTableEpochChangeVersionInternalAsync(conn, tx, table, newVersion.StructureVersion + newVersion.ContentVersion, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
                await FlushScopePendingAsync(ct).ConfigureAwait(false);
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
                await SetTableEpochChangeVersionInternalAsync(conn, tx, table, newVersion.StructureVersion + newVersion.ContentVersion, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
                await FlushScopePendingAsync(ct).ConfigureAwait(false);
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
    public Task<DatasetCatalogChange?> SetDatasetDiscoveryAsync(
        TableIdentifier table, bool? discoveryHidden, bool? samplesDisabled, IReadOnlyList<string>? hiddenColumns, Sid actor, CancellationToken ct = default) =>
        WriteCatalogSettingsAsync(table, (conn, tx) =>
            DatasetCatalogStore.SetDiscoveryFlagsAsync(conn, tx, KeyDialect, table, discoveryHidden, samplesDisabled, hiddenColumns, actor, ct), ct);

    public async Task<DiscoveryArtifacts?> GetDiscoveryArtifactsAsync(Guid tableId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await DiscoveryArtifactStore.GetAsync(conn, null, KeyDialect, tableId, ct).ConfigureAwait(false);
    }

    public async Task<bool> UpsertDiscoveryArtifactsAsync(DiscoveryArtifacts artifacts, Sid actor, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await DiscoveryArtifactStore.UpsertAsync(conn, null, KeyDialect, artifacts, actor, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteDiscoveryArtifactsAsync(Guid tableId, Sid actor, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await DiscoveryArtifactStore.DeleteAsync(conn, null, KeyDialect, tableId, actor, ct).ConfigureAwait(false);
    }

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
                    var changeVersion = newVersion.Value.StructureVersion + newVersion.Value.ContentVersion;
                    await IncrementTableEpochInternalAsync(conn, tx, table, ct, changeVersion).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                if (change.Changed && newVersion.HasValue)
                {
                    await PublishCatalogChangedEventAsync(newVersion.Value, ct).ConfigureAwait(false);
                    await FlushScopePendingAsync(ct).ConfigureAwait(false);
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
            await InvalidateTableEpochAsync(table, ct).ConfigureAwait(false);
        }

        return change;
    }

    public async Task<TableIdentifier?> GetActiveTableIdentifierAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CatalogRowHydrator.GetActiveTableIdentifierAsync(conn, CatalogSql.ActiveTableIdentifierSql, table, ct).ConfigureAwait(false);
    }

    public async Task<TableIdentifier?> GetActiveTableIdentifierByIdAsync(Guid tableId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await GetActiveTableIdentifierByIdAsync(conn, tableId, ct).ConfigureAwait(false);
    }

    public async Task<long> GetTableEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CatalogRowHydrator.GetTableEpochAsync(conn, CatalogSql.TableEpochSql, table, ct).ConfigureAwait(false);
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

    internal void LogCatalogNotice(string message) => _logger?.LogWarning("Catalog notice: {Notice}", message);
}
