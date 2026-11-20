namespace Autheris.Infrastructure.Persistence.Catalog;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;

internal static class CatalogBatchUpsertHelper
{
    private const int MaxBatchChunkAttempts = 3;

    internal readonly record struct NormalizedIdentifierKey(string Domain, string Schema, string Table)
    {
        public static NormalizedIdentifierKey From(TableIdentifier id) =>
            new(id.Domain.ToLowerInvariant(), id.Schema.ToLowerInvariant(), id.TableName.ToLowerInvariant());

        public static NormalizedIdentifierKey From(Table table) =>
            new(table.SourceName.ToLowerInvariant(), table.SchemaName.ToLowerInvariant(), table.TableName.ToLowerInvariant());
    }

    internal readonly record struct ChunkItemOutcome(Guid TableId, bool WasCreated, CatalogWriteClassification Classification);

    private readonly record struct ChunkColumnKeySnapshot(
        Dictionary<Guid, List<TableColumn>> ColumnsByTable,
        Dictionary<Guid, Dictionary<string, MaskingRule>> MasksByTable,
        HashSet<Guid> CorruptedIds,
        Dictionary<Guid, CatalogKeyDefinitionStore.TableKeyRows>? Keys,
        Dictionary<Guid, Dictionary<string, IReadOnlyList<ColumnClassification>>> ClassificationsByTable);

    internal static async Task<ChunkItemOutcome[]> ExecuteChunkWithRetryAsync(
        GovernanceRepository repo,
        IReadOnlyList<TableMetadata> chunk,
        CatalogWriteScope? writeScope,
        Func<Exception, bool> isTransient,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ExecuteChunkAsync(repo, chunk, writeScope, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxBatchChunkAttempts && isTransient(ex))
            {
                repo.LogCatalogNotice($"Transient conflict while upserting catalog batch chunk (attempt {attempt} of {MaxBatchChunkAttempts}); retrying.");
            }
        }
    }

    private static async Task<ChunkItemOutcome[]> ExecuteChunkAsync(
        GovernanceRepository repo,
        IReadOnlyList<TableMetadata> chunk,
        CatalogWriteScope? writeScope,
        CancellationToken ct)
    {
        var count = chunk.Count;
        var outcomes = new ChunkItemOutcome[count];
        var tableIds = new Guid[count];
        var wasCreated = new bool[count];
        var structuralChanges = new bool[count];
        var classifications = new CatalogWriteClassification[count];
        var storedBefore = new CatalogTablePersistedProjection?[count];
        var existingLookupIndices = new List<int>();
        var existingTableIds = new List<Guid>();

        var deferVersionToScope = false;
        long totalStructureDelta = 0;
        long totalContentDelta = 0;
        var anyChanged = false;

        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var existingTables = await SelectChunkTablesAsync(repo, conn, tx, chunk, lockForUpdate: true, ct).ConfigureAwait(false);
            for (var i = 0; i < count; i++)
            {
                if (existingTables.TryGetValue(NormalizedIdentifierKey.From(chunk[i].Identifier), out var found))
                {
                    tableIds[i] = found.Id;
                    existingLookupIndices.Add(i);
                    existingTableIds.Add(found.Id);
                }
                else
                {
                    tableIds[i] = chunk[i].Table.Id == Guid.Empty ? Guid.NewGuid() : chunk[i].Table.Id;
                    wasCreated[i] = true;
                }
            }

            var before = await LoadChunkDetailsAsync(repo, conn, tx, existingTableIds, ct).ConfigureAwait(false);
            var precedence = repo.OptionsSnapshot?.Catalog?.DocumentationSourcePrecedence;
            var anyStoredBefore = false;

            var tableRows = new List<CatalogBatchSqlBuilder.TableUpsertRow>(count);
            var existingColsPerTable = new Dictionary<string, (Guid Id, string DataType, string? Description, string? LongDescription, string? DocSource, string? Collation, string? LengthSemantics)>[count];
            var existingMasksPerTable = new Dictionary<string, MaskingRule>[count];
            var colIdByNamePerTable = new Dictionary<string, Guid>[count];
            var removedColIds = new List<Guid>();
            var needMaxOrdinalTableIds = new List<Guid>();

            for (var i = 0; i < count; i++)
            {
                var incoming = chunk[i];
                var existed = !wasCreated[i];
                var docs = new CatalogRowHydrator.TableDocumentation(
                    incoming.Table.Description,
                    incoming.Table.LongDescription,
                    incoming.Table.DocumentationSource);

                if (existed)
                {
                    var existingTable = existingTables[NormalizedIdentifierKey.From(incoming.Identifier)];
                    (structuralChanges[i], docs) = CatalogRowHydrator.ComputeTableUpsertState(existingTable, incoming.Table, precedence);

                    var cols = before.ColumnsByTable.TryGetValue(tableIds[i], out var loadedCols) ? loadedCols : new List<TableColumn>();
                    var masks = before.MasksByTable.TryGetValue(tableIds[i], out var loadedMasks)
                        ? loadedMasks
                        : new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);

                    storedBefore[i] = before.CorruptedIds.Contains(tableIds[i])
                        ? null
                        : await TryProjectFromSnapshotAsync(repo, conn, tx, incoming.Identifier, existingTable, cols, masks, before.Keys, ClassificationsOf(before, tableIds[i]), ct).ConfigureAwait(false);
                    if (storedBefore[i] is not null)
                    {
                        anyStoredBefore = true;
                    }

                    BuildColumnMaps(cols, masks, out existingColsPerTable[i], out existingMasksPerTable[i], out colIdByNamePerTable[i]);

                    if (incoming.ReplaceColumns)
                    {
                        var incomingNames = new HashSet<string>(incoming.Columns.Select(c => c.ColumnName), StringComparer.OrdinalIgnoreCase);
                        foreach (var (existingName, existingInfo) in existingColsPerTable[i])
                        {
                            if (!incomingNames.Contains(existingName))
                            {
                                structuralChanges[i] = true;
                                removedColIds.Add(existingInfo.Id);
                            }
                        }
                    }

                    if (incoming.Columns.Any(c => !existingColsPerTable[i].ContainsKey(c.ColumnName)))
                    {
                        needMaxOrdinalTableIds.Add(tableIds[i]);
                    }
                }
                else
                {
                    structuralChanges[i] = true;
                    existingColsPerTable[i] = new(StringComparer.OrdinalIgnoreCase);
                    existingMasksPerTable[i] = new(StringComparer.OrdinalIgnoreCase);
                    colIdByNamePerTable[i] = new(StringComparer.OrdinalIgnoreCase);
                }

                tableRows.Add(new CatalogBatchSqlBuilder.TableUpsertRow(tableIds[i], incoming, docs));
            }

            var nextOrdinals = needMaxOrdinalTableIds.Count > 0
                ? await CatalogBatchSqlBuilder.LoadMaxColumnOrdinalsAsync(repo, conn, tx, needMaxOrdinalTableIds, ct).ConfigureAwait(false)
                : new Dictionary<Guid, int>();

            var columnRows = new List<CatalogBatchSqlBuilder.ColumnUpsertRow>();
            var classificationRows = new List<CatalogBatchSqlBuilder.ClassificationInsertRow>();
            var maskDeleteColIds = new List<Guid>();
            var maskInsertRows = new List<CatalogBatchSqlBuilder.MaskOverrideInsertRow>();
            var replaceKeyTableIds = new List<Guid>();
            var pkInsertRows = new List<CatalogBatchSqlBuilder.PrimaryKeyInsertRow>();
            var indexInsertRows = new List<CatalogBatchSqlBuilder.IndexInsertRow>();
            var indexColumnInsertRows = new List<CatalogBatchSqlBuilder.IndexColumnInsertRow>();
            var staleMarkerUpdates = new List<(string Id, string MetaJson)>();

            for (var i = 0; i < count; i++)
            {
                var incoming = chunk[i];
                var existingColsByName = existingColsPerTable[i];
                var existingMaskingRules = existingMasksPerTable[i];
                var colIdByName = colIdByNamePerTable[i];
                var existingClassifications = ClassificationsOf(before, tableIds[i]);
                var nextOrdinal = nextOrdinals.TryGetValue(tableIds[i], out var maxOrd) ? maxOrd : 0;

                if (CatalogRowHydrator.HasMaskingRulesChanged(existingMaskingRules, incoming.ColumnMaskingRules))
                {
                    structuralChanges[i] = true;
                }

                foreach (var col in incoming.Columns)
                {
                    var colId = col.Id == Guid.Empty ? Guid.NewGuid() : col.Id;
                    var incomingColSource = col.DocumentationSource ?? incoming.Table.DocumentationSource;
                    var targetColDesc = col.Description;
                    var targetColLongDesc = col.LongDescription;
                    var targetColDocSource = incomingColSource;
                    var colOrdinal = 0;

                    if (existingColsByName.TryGetValue(col.ColumnName, out var existingCol))
                    {
                        colId = existingCol.Id;
                        if (!string.Equals(existingCol.DataType, col.DataType, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(existingCol.Collation, col.Collation, StringComparison.Ordinal) ||
                            !string.Equals(existingCol.LengthSemantics, col.LengthSemantics, StringComparison.OrdinalIgnoreCase))
                        {
                            structuralChanges[i] = true;
                        }

                        var canOverwriteColDesc = !string.IsNullOrWhiteSpace(col.Description) &&
                            (string.IsNullOrWhiteSpace(existingCol.Description) ||
                             CatalogRowHydrator.ShouldOverwriteDocumentation(precedence, existingCol.DocSource, incomingColSource));

                        var canOverwriteColLongDesc = !string.IsNullOrWhiteSpace(col.LongDescription) &&
                            (string.IsNullOrWhiteSpace(existingCol.LongDescription) ||
                             CatalogRowHydrator.ShouldOverwriteDocumentation(precedence, existingCol.DocSource, incomingColSource));

                        targetColDesc = canOverwriteColDesc ? col.Description : existingCol.Description;
                        targetColLongDesc = canOverwriteColLongDesc ? col.LongDescription : existingCol.LongDescription;
                        targetColDocSource = (canOverwriteColDesc || canOverwriteColLongDesc) ? (incomingColSource ?? existingCol.DocSource) : existingCol.DocSource;
                    }
                    else
                    {
                        structuralChanges[i] = true;
                        colOrdinal = ++nextOrdinal;
                    }

                    IReadOnlyDictionary<string, string> effectiveMeta = incoming.ReplaceKeyDefinition
                        ? CatalogKeyDefinitionStore.WithKeyMarkers(col.Meta, col.ColumnName, incoming.PrimaryKeyColumns)
                        : col.Meta ?? new Dictionary<string, string>();

                    var metaJson = effectiveMeta.Count > 0
                        ? JsonSerializer.Serialize(effectiveMeta)
                        : incoming.ReplaceKeyDefinition ? "{}" : null;

                    columnRows.Add(new CatalogBatchSqlBuilder.ColumnUpsertRow(
                        colId,
                        tableIds[i],
                        col.ColumnName,
                        col.DataType,
                        targetColDesc,
                        targetColLongDesc,
                        targetColDocSource,
                        metaJson,
                        col.DisplayName,
                        CatalogLocalizationPersistence.SerializeLocalizedTexts(col.Texts),
                        col.Collation,
                        col.LengthSemantics,
                        CatalogRowHydrator.FormatColumnRole(col.Role),
                        colOrdinal));

                    colIdByName[col.ColumnName] = colId;

                    if (incoming.ColumnClassifications.TryGetValue(col.ColumnName, out var incomingClassifs))
                    {
                        existingClassifications.TryGetValue(col.ColumnName, out var existingColClassifs);
                        foreach (var classification in incomingClassifs.Where(static c => ClassificationScopes.IsGlobal(c.Scope)))
                        {
                            if (existingColClassifs == null || !existingColClassifs.Any(ec => string.Equals(ec.Code, classification.Code, StringComparison.OrdinalIgnoreCase)))
                            {
                                structuralChanges[i] = true;
                            }

                            classificationRows.Add(new CatalogBatchSqlBuilder.ClassificationInsertRow(
                                colId,
                                classification.Code,
                                string.IsNullOrWhiteSpace(classification.Source) ? "INGEST" : classification.Source,
                                string.IsNullOrWhiteSpace(classification.AssignedBy) ? "system:catalog-upsert" : classification.AssignedBy));
                        }
                    }

                    var hadMask = existingMaskingRules.ContainsKey(col.ColumnName);
                    if (incoming.ColumnMaskingRules.TryGetValue(col.ColumnName, out var rule))
                    {
                        if (hadMask)
                        {
                            maskDeleteColIds.Add(colId);
                        }

                        var (functionCode, paramsJson, keyRef) = MaskingRuleOverrideMapper.FromRule(rule);
                        maskInsertRows.Add(new CatalogBatchSqlBuilder.MaskOverrideInsertRow(colId, functionCode, paramsJson, keyRef));
                    }
                    else if (hadMask)
                    {
                        maskDeleteColIds.Add(colId);
                    }
                }

                if (incoming.ReplaceKeyDefinition)
                {
                    structuralChanges[i] = true;
                    replaceKeyTableIds.Add(tableIds[i]);

                    var validatedKeys = TableKeyDefinitionValidator.Validate(incoming);
                    for (var pkOrd = 0; pkOrd < validatedKeys.PrimaryKeyColumns.Count; pkOrd++)
                    {
                        var pkColName = validatedKeys.PrimaryKeyColumns[pkOrd];
                        if (!colIdByName.TryGetValue(pkColName, out var pkColId))
                        {
                            throw new InvalidOperationException($"Key column '{pkColName}' has no persisted column id for table '{incoming.Identifier}'.");
                        }

                        pkInsertRows.Add(new CatalogBatchSqlBuilder.PrimaryKeyInsertRow(
                            tableIds[i],
                            pkOrd + 1,
                            pkColId.ToString(),
                            KeyDirections.IsDescending(validatedKeys.PrimaryKeyDirections, pkOrd)));
                    }

                    foreach (var index in validatedKeys.Indexes)
                    {
                        var indexId = Guid.NewGuid().ToString();
                        indexInsertRows.Add(new CatalogBatchSqlBuilder.IndexInsertRow(indexId, tableIds[i], index.Name, index.IsUnique));
                        for (var icOrd = 0; icOrd < index.Columns.Count; icOrd++)
                        {
                            var idxColName = index.Columns[icOrd];
                            if (!colIdByName.TryGetValue(idxColName, out var idxColId))
                            {
                                throw new InvalidOperationException($"Key column '{idxColName}' has no persisted column id for table '{incoming.Identifier}'.");
                            }

                            indexColumnInsertRows.Add(new CatalogBatchSqlBuilder.IndexColumnInsertRow(
                                indexId,
                                icOrd + 1,
                                idxColId.ToString(),
                                KeyDirections.IsDescending(index.Directions, icOrd)));
                        }
                    }

                    if (!wasCreated[i] && before.ColumnsByTable.TryGetValue(tableIds[i], out var storedCols))
                    {
                        var incomingColNames = new HashSet<string>(incoming.Columns.Select(c => c.ColumnName), StringComparer.OrdinalIgnoreCase);
                        var removedSet = removedColIds.Count > 0 ? new HashSet<Guid>(removedColIds) : null;
                        foreach (var storedCol in storedCols)
                        {
                            if (incomingColNames.Contains(storedCol.ColumnName) || (removedSet != null && removedSet.Contains(storedCol.Id)))
                            {
                                continue;
                            }

                            var markers = CatalogKeyDefinitionStore.WithKeyMarkers(storedCol.Meta, storedCol.ColumnName, validatedKeys.PrimaryKeyColumns);
                            if (markers.Count != storedCol.Meta.Count || !markers.All(kv => storedCol.Meta.TryGetValue(kv.Key, out var v) && v == kv.Value))
                            {
                                staleMarkerUpdates.Add((storedCol.Id.ToString(), markers.Count > 0 ? JsonSerializer.Serialize(markers) : "{}"));
                            }
                        }
                    }
                }
            }

            await CatalogBatchSqlBuilder.UpsertTablesBatchAsync(repo, conn, tx, tableRows, ct).ConfigureAwait(false);
            await CatalogBatchSqlBuilder.DeleteRemovedColumnsBatchAsync(repo, conn, tx, removedColIds, ct).ConfigureAwait(false);
            await CatalogBatchSqlBuilder.UpsertColumnsBatchAsync(repo, conn, tx, columnRows, ct).ConfigureAwait(false);
            await CatalogBatchSqlBuilder.InsertGlobalClassificationsBatchAsync(repo, conn, tx, classificationRows, ct).ConfigureAwait(false);
            await CatalogBatchSqlBuilder.ReplaceGlobalMaskOverridesBatchAsync(repo, conn, tx, maskDeleteColIds, maskInsertRows, ct).ConfigureAwait(false);
            await CatalogBatchSqlBuilder.ReplaceKeyDefinitionsBatchAsync(repo, conn, tx, replaceKeyTableIds, pkInsertRows, indexInsertRows, indexColumnInsertRows, ct).ConfigureAwait(false);

            foreach (var (colIdStr, metaJson) in staleMarkerUpdates)
            {
                await using var markerCmd = conn.CreateCommand();
                markerCmd.Transaction = tx;
                markerCmd.CommandText = $"UPDATE {repo.KeyDialect.Prefix}TABLE_COLUMNS SET meta_json = @meta WHERE id = @id";
                markerCmd.AddBound("@meta", metaJson);
                markerCmd.AddBound("@id", colIdStr);
                await markerCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            Dictionary<NormalizedIdentifierKey, Table>? afterTables = null;
            ChunkColumnKeySnapshot? after = null;
            if (anyStoredBefore)
            {
                var afterItems = existingLookupIndices.Select(idx => chunk[idx]).ToArray();
                afterTables = await SelectChunkTablesAsync(repo, conn, tx, afterItems, lockForUpdate: false, ct).ConfigureAwait(false);
                after = await LoadChunkDetailsAsync(repo, conn, tx, existingTableIds, ct).ConfigureAwait(false);
            }

            var anyRestrictive = false;
            for (var i = 0; i < count; i++)
            {
                if (wasCreated[i])
                {
                    classifications[i] = CatalogWriteClassification.Additive;
                }
                else
                {
                    var classification = CatalogWriteClassification.Restrictive;
                    if (storedBefore[i] is not null && afterTables is not null && after is { } afterSnap)
                    {
                        var key = NormalizedIdentifierKey.From(chunk[i].Identifier);
                        if (afterTables.TryGetValue(key, out var afterTable) && !afterSnap.CorruptedIds.Contains(tableIds[i]))
                        {
                            var cols = afterSnap.ColumnsByTable.TryGetValue(tableIds[i], out var c) ? c : new List<TableColumn>();
                            var masks = afterSnap.MasksByTable.TryGetValue(tableIds[i], out var m) ? m : new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);
                            var afterProj = await TryProjectFromSnapshotAsync(repo, conn, tx, chunk[i].Identifier, afterTable, cols, masks, afterSnap.Keys, ClassificationsOf(afterSnap, tableIds[i]), ct).ConfigureAwait(false);
                            if (afterProj is not null)
                            {
                                classification = CatalogTablePersistedProjection.Compare(storedBefore[i]!, afterProj).Classification;
                            }
                        }
                    }

                    classifications[i] = classification;
                }

                if (classifications[i] != CatalogWriteClassification.Unchanged)
                {
                    anyChanged = true;
                    if (classifications[i] == CatalogWriteClassification.Restrictive)
                    {
                        anyRestrictive = true;
                    }

                    if (structuralChanges[i])
                    {
                        totalStructureDelta++;
                    }
                    else
                    {
                        totalContentDelta++;
                    }
                }

                outcomes[i] = new ChunkItemOutcome(tableIds[i], wasCreated[i], classifications[i]);
            }

            if (!anyChanged)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
            }
            else
            {
                deferVersionToScope = writeScope is not null && !anyRestrictive;
                CatalogVersion? newVersion = null;
                long? changeVersion = null;
                if (!deferVersionToScope)
                {
                    newVersion = await repo.BumpCatalogVersionInternalAsync(conn, tx, totalStructureDelta, totalContentDelta, ct).ConfigureAwait(false);
                    changeVersion = newVersion.Value.StructureVersion + newVersion.Value.ContentVersion;
                }

                var epochRows = new List<CatalogBatchSqlBuilder.EpochUpsertRow>(count);
                List<TableIdentifier>? deferredTables = deferVersionToScope ? new List<TableIdentifier>(count) : null;
                for (var i = 0; i < count; i++)
                {
                    if (classifications[i] != CatalogWriteClassification.Unchanged)
                    {
                        epochRows.Add(new CatalogBatchSqlBuilder.EpochUpsertRow(tableIds[i], chunk[i].Identifier));
                        deferredTables?.Add(chunk[i].Identifier);
                    }
                }

                await CatalogBatchSqlBuilder.IncrementTableEpochsBatchAsync(repo, conn, tx, epochRows, changeVersion, ct).ConfigureAwait(false);

                await tx.CommitAsync(ct).ConfigureAwait(false);

                if (deferredTables is { Count: > 0 })
                {
                    repo.RecordDeferredEpochTables(deferredTables);
                }

                if (newVersion.HasValue)
                {
                    await repo.PublishCatalogChangedEventAsync(newVersion.Value, ct).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            await SafeRollbackAsync(tx, ct).ConfigureAwait(false);
            throw;
        }

        if (!anyChanged)
        {
            for (var i = 0; i < count; i++)
            {
                repo.InvalidateTableMetadata(chunk[i].Identifier.ToString().ToLowerInvariant());
            }

            return outcomes;
        }

        if (writeScope is not null)
        {
            if (deferVersionToScope)
            {
                await writeScope.RecordAdditiveWriteAsync(totalStructureDelta, totalContentDelta, ct).ConfigureAwait(false);
            }
            else
            {
                await writeScope.FlushPendingAsync(ct).ConfigureAwait(false);
            }
        }

        for (var i = 0; i < count; i++)
        {
            if (classifications[i] == CatalogWriteClassification.Unchanged)
            {
                repo.InvalidateTableMetadata(chunk[i].Identifier.ToString().ToLowerInvariant());
            }
            else
            {
                await repo.InvalidateTableEpochAsync(chunk[i].Identifier, ct).ConfigureAwait(false);
            }
        }

        return outcomes;
    }

    internal static async Task<CatalogTableUpsertResult[]> HydrateChunkResultsAsync(
        GovernanceRepository repo,
        IReadOnlyList<TableMetadata> chunk,
        ChunkItemOutcome[] outcomes,
        CancellationToken ct)
    {
        var results = new CatalogTableUpsertResult[chunk.Count];
        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        var tables = await SelectChunkTablesAsync(repo, conn, null, chunk, lockForUpdate: false, ct).ConfigureAwait(false);
        var loadedIds = tables.Values.Select(e => e.Id).ToList();
        var details = await LoadChunkDetailsAsync(repo, conn, null, loadedIds, ct).ConfigureAwait(false);

        Dictionary<Guid, long>? epochs = null;
        try
        {
            epochs = await GovernanceRepository.ReadAllTableEpochsAsync(conn, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            repo.LogCatalogNotice("Table epochs could not be read after batch upsert; loaded metadata will not be cached.");
        }

        var overlays = await DatasetCatalogStore.ReadOverlayAsync(conn, null, repo.KeyDialect, null, repo.CatalogLogger, ct).ConfigureAwait(false);

        for (var i = 0; i < chunk.Count; i++)
        {
            var incoming = chunk[i];
            var outcome = outcomes[i];
            var kind = outcome.Classification == CatalogWriteClassification.Unchanged
                ? CatalogUpsertKind.Unchanged
                : outcome.WasCreated ? CatalogUpsertKind.Created : CatalogUpsertKind.Updated;

            try
            {
                if (!tables.TryGetValue(NormalizedIdentifierKey.From(incoming.Identifier), out var entity) || details.CorruptedIds.Contains(entity.Id))
                {
                    results[i] = new CatalogTableUpsertResult(incoming, kind, outcome.Classification);
                    continue;
                }

                var cols = details.ColumnsByTable.TryGetValue(entity.Id, out var c) ? c : new List<TableColumn>();
                var masks = details.MasksByTable.TryGetValue(entity.Id, out var m) ? m : new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);

                var (pkCols, pkDirs, indexes) = await CatalogKeyDefinitionStore.ReadAsync(
                    conn, null, repo.KeyDialect, incoming.Identifier, entity.Id, cols, ct,
                    repo.KeyTableProbe, details.Keys is not null && details.Keys.TryGetValue(entity.Id, out var kr) ? kr : null).ConfigureAwait(false);

                var structural = new TableMetadata
                {
                    Identifier = incoming.Identifier,
                    Table = entity,
                    Columns = cols,
                    ColumnMaskingRules = masks,
                    ColumnClassifications = ClassificationsOf(details, entity.Id),
                    PrimaryKeyColumns = pkCols,
                    PrimaryKeyDirections = pkDirs,
                    Indexes = indexes
                };
                structural.ValidateRequiredFilterColumns();

                if (repo.AfterMetadataSnapshotForTesting is { } hook)
                {
                    await hook(ct).ConfigureAwait(false);
                }

                if (epochs != null && epochs.TryGetValue(entity.Id, out var ep) && ep >= 0)
                {
                    repo.MetadataCache[incoming.Identifier.ToString().ToLowerInvariant()] =
                        (structural, TimeProvider.System.GetUtcNow().UtcTicks, ep);
                }

                results[i] = new CatalogTableUpsertResult(DatasetCatalogStore.Apply(structural, overlays), kind, outcome.Classification);
            }
            catch (TableMetadataUnavailableException)
            {
                results[i] = new CatalogTableUpsertResult(incoming, kind, outcome.Classification);
            }
        }

        return results;
    }

    private static async Task<ChunkColumnKeySnapshot> LoadChunkDetailsAsync(
        GovernanceRepository repo,
        DbConnection conn,
        DbTransaction? tx,
        IReadOnlyList<Guid> tableIds,
        CancellationToken ct)
    {
        if (tableIds.Count == 0)
        {
            return new ChunkColumnKeySnapshot(new(), new(), new(), null, new());
        }

        var (cols, masks, corrupted) = await LoadChunkColumnsAsync(repo, conn, tx, tableIds, ct).ConfigureAwait(false);
        var keyTablesExist = await CatalogKeyDefinitionStore.TablesExistAsync(conn, tx, repo.KeyDialect, ct, repo.KeyTableProbe).ConfigureAwait(false);
        Dictionary<Guid, CatalogKeyDefinitionStore.TableKeyRows>? keys = null;
        if (keyTablesExist)
        {
            keys = await CatalogKeyDefinitionStore.ReadBatchAsync(conn, tx, repo.KeyDialect, tableIds, ct).ConfigureAwait(false);
        }

        var classifications = await LoadChunkClassificationsAsync(repo, conn, tx, tableIds, ct).ConfigureAwait(false);
        return new ChunkColumnKeySnapshot(cols, masks, corrupted, keys, classifications);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ColumnClassification>> ClassificationsOf(ChunkColumnKeySnapshot snapshot, Guid tableId) =>
        snapshot.ClassificationsByTable.TryGetValue(tableId, out var found)
            ? found
            : new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase);

    private static async Task<Dictionary<Guid, Dictionary<string, IReadOnlyList<ColumnClassification>>>> LoadChunkClassificationsAsync(
        GovernanceRepository repo, DbConnection conn, DbTransaction? tx, IReadOnlyList<Guid> tableIds, CancellationToken ct)
    {
        var idStrings = new string[tableIds.Count];
        for (var i = 0; i < tableIds.Count; i++)
        {
            idStrings[i] = tableIds[i].ToString();
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = CatalogRowHydrator.SelectGlobalClassificationsByTableIdsPrefix
            + ConsentSqlCore.BindIdInClause(cmd, "tid", idStrings) + ") ORDER BY c.table_id, c.column_name, cc.code";
        return await CatalogRowHydrator.ReadGlobalClassificationsAsync(cmd, ct).ConfigureAwait(false);
    }

    private static async Task<CatalogTablePersistedProjection?> TryProjectFromSnapshotAsync(
        GovernanceRepository repo,
        DbConnection conn,
        DbTransaction tx,
        TableIdentifier identifier,
        Table tableEntity,
        List<TableColumn> columns,
        Dictionary<string, MaskingRule> maskingRules,
        IReadOnlyDictionary<Guid, CatalogKeyDefinitionStore.TableKeyRows>? prefetchedKeys,
        IReadOnlyDictionary<string, IReadOnlyList<ColumnClassification>> classifications,
        CancellationToken ct)
    {
        try
        {
            var (pkCols, pkDirs, indexes) = await CatalogKeyDefinitionStore.ReadAsync(
                conn, tx, repo.KeyDialect, identifier, tableEntity.Id, columns, ct,
                repo.KeyTableProbe, prefetchedKeys is not null && prefetchedKeys.TryGetValue(tableEntity.Id, out var kr) ? kr : null).ConfigureAwait(false);

            return CatalogTablePersistedProjection.From(new TableMetadata
            {
                Identifier = identifier,
                Table = tableEntity,
                Columns = columns,
                ColumnMaskingRules = maskingRules,
                ColumnClassifications = classifications,
                PrimaryKeyColumns = pkCols,
                PrimaryKeyDirections = pkDirs,
                Indexes = indexes
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            repo.LogCatalogNotice("The stored table state could not be projected for no-op detection; the write proceeds.");
            return null;
        }
    }

    private static async Task<Dictionary<NormalizedIdentifierKey, Table>> SelectChunkTablesAsync(
        GovernanceRepository repo,
        DbConnection conn,
        DbTransaction? tx,
        IReadOnlyList<TableMetadata> chunk,
        bool lockForUpdate,
        CancellationToken ct)
    {
        var result = new Dictionary<NormalizedIdentifierKey, Table>();
        if (chunk.Count == 0)
        {
            return result;
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        var where = BindTableTupleOrClause(cmd, chunk);
        var prefix = lockForUpdate
            ? (repo.IsPostgreSql ? GovernanceRepository.CatalogSql.SelectTablesWherePrefix : GovernanceRepository.CatalogSql.SelectTablesWithUpdLockWherePrefix)
            : GovernanceRepository.CatalogSql.SelectTablesWherePrefix;
        var suffix = lockForUpdate && repo.IsPostgreSql
            ? " ORDER BY LOWER(source_name), LOWER(schema_name), LOWER(table_name) FOR UPDATE"
            : " ORDER BY LOWER(source_name), LOWER(schema_name), LOWER(table_name)";
        cmd.CommandText = prefix + where + suffix;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var entity = CatalogRowHydrator.ReadTableEntity(reader, repo.CatalogLogger);
            result[NormalizedIdentifierKey.From(entity)] = entity;
        }

        return result;
    }

    private static async Task<(Dictionary<Guid, List<TableColumn>> ColumnsByTable, Dictionary<Guid, Dictionary<string, MaskingRule>> MaskingRulesByTable, HashSet<Guid> CorruptedTableIds)>
        LoadChunkColumnsAsync(GovernanceRepository repo, DbConnection conn, DbTransaction? tx, IReadOnlyList<Guid> tableIds, CancellationToken ct)
    {
        if (tableIds.Count == 0)
        {
            return (new(), new(), new());
        }

        var idStrings = new string[tableIds.Count];
        for (var i = 0; i < tableIds.Count; i++)
        {
            idStrings[i] = tableIds[i].ToString();
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        var prefix = repo.IsPostgreSql
            ? GovernanceRepository.CatalogSql.SelectColumnsByTableIdsPrefixPg
            : GovernanceRepository.CatalogSql.SelectColumnsByTableIdsPrefixSs;
        cmd.CommandText = prefix + ConsentSqlCore.BindIdInClause(cmd, "tid", idStrings) + ") ORDER BY c.table_id, c.ordinal, c.column_name";
        return await repo.LoadBatchTableColumnsAsync(cmd, ct).ConfigureAwait(false);
    }

    private static string BindTableTupleOrClause(DbCommand cmd, IReadOnlyList<TableMetadata> chunk)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < chunk.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(" OR ");
            }

            var dn = "@d" + i.ToString(CultureInfo.InvariantCulture);
            var sn = "@s" + i.ToString(CultureInfo.InvariantCulture);
            var tn = "@t" + i.ToString(CultureInfo.InvariantCulture);
            sb.Append("(LOWER(source_name) = LOWER(").Append(dn)
              .Append(") AND LOWER(schema_name) = LOWER(").Append(sn)
              .Append(") AND LOWER(table_name) = LOWER(").Append(tn).Append("))");
            cmd.AddBound(dn, chunk[i].Identifier.Domain);
            cmd.AddBound(sn, chunk[i].Identifier.Schema);
            cmd.AddBound(tn, chunk[i].Identifier.TableName);
        }

        return sb.ToString();
    }

    private static void BuildColumnMaps(
        List<TableColumn> columns,
        Dictionary<string, MaskingRule> maskingRules,
        out Dictionary<string, (Guid Id, string DataType, string? Description, string? LongDescription, string? DocSource, string? Collation, string? LengthSemantics)> existingColsByName,
        out Dictionary<string, MaskingRule> existingMaskingRules,
        out Dictionary<string, Guid> colIdByName)
    {
        existingColsByName = new Dictionary<string, (Guid, string, string?, string?, string?, string?, string?)>(StringComparer.OrdinalIgnoreCase);
        existingMaskingRules = new Dictionary<string, MaskingRule>(maskingRules, StringComparer.OrdinalIgnoreCase);
        colIdByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var col in columns)
        {
            existingColsByName[col.ColumnName] = (
                col.Id, col.DataType,
                col.Description, col.LongDescription, col.DocumentationSource,
                col.Collation, col.LengthSemantics);
            colIdByName[col.ColumnName] = col.Id;
        }
    }

    private static async Task SafeRollbackAsync(DbTransaction tx, CancellationToken ct)
    {
        try
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ignore rollback errors when a transient conflict has already aborted the transaction.
        }
    }
}
