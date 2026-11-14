namespace Autheris.Infrastructure.Persistence;

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
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Catalog;
using Microsoft.Extensions.Logging;

public partial class GovernanceRepository
{
    private const int MaxBatchChunkSize = 500;
    private const int MaxBatchChunkAttempts = 3;

    private readonly record struct NormalizedIdentifierKey(string Domain, string Schema, string Table)
    {
        public static NormalizedIdentifierKey From(TableIdentifier id) =>
            new(id.Domain.ToLowerInvariant(), id.Schema.ToLowerInvariant(), id.TableName.ToLowerInvariant());

        public static NormalizedIdentifierKey From(Table table) =>
            new(table.SourceName.ToLowerInvariant(), table.SchemaName.ToLowerInvariant(), table.TableName.ToLowerInvariant());
    }

    private readonly record struct ChunkItemOutcome(Guid TableId, bool WasCreated, CatalogWriteClassification Classification);

    private readonly record struct ChunkColumnKeySnapshot(
        Dictionary<Guid, List<TableColumn>> ColumnsByTable,
        Dictionary<Guid, Dictionary<string, MaskingRule>> MasksByTable,
        HashSet<Guid> CorruptedIds,
        bool KeyTablesExist,
        Dictionary<Guid, CatalogKeyDefinitionStore.TableKeyRows>? Keys,
        Dictionary<Guid, List<CatalogPhysicalIndexStore.Row>>? PhysicalIndexes);

    /// <summary>
    /// Validates and upserts a batch of table metadata entries using parameter-budget chunking, deterministic lock ordering,
    /// per-chunk transactions, persisted-state change classification, and transient conflict retries.
    /// </summary>
    public async Task<IReadOnlyList<CatalogTableUpsertResult>> UpsertTableMetadataBatchAsync(
        IReadOnlyList<TableMetadata> batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return Array.Empty<CatalogTableUpsertResult>();
        }

        // Pre-flight validation fails before any DB connection is opened or any table row is touched.
        var validated = new TableMetadata[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            var item = batch[i] ?? throw new ArgumentNullException(nameof(batch));
            CatalogSourceNameValidator.Validate(item);
            item.ValidateRequiredFilterColumns();
            SemanticVirtualTables.EnsureNamespaceRespected(item);
            validated[i] = item.ReplaceKeyDefinition ? TableKeyDefinitionValidator.Validate(item) : item;
        }

        // Deduplicate by case-insensitive identifier (latest entry wins) while preserving input-to-slot mapping.
        var keyToSlot = new Dictionary<NormalizedIdentifierKey, int>();
        var distinct = new List<TableMetadata>();
        var inputToSlot = new int[validated.Length];
        for (var i = 0; i < validated.Length; i++)
        {
            var key = NormalizedIdentifierKey.From(validated[i].Identifier);
            if (keyToSlot.TryGetValue(key, out var slot))
            {
                distinct[slot] = validated[i];
                inputToSlot[i] = slot;
            }
            else
            {
                slot = distinct.Count;
                keyToSlot[key] = slot;
                distinct.Add(validated[i]);
                inputToSlot[i] = slot;
            }
        }

        // Deterministic sort by (Domain, Schema, TableName) avoids lock-order deadlocks across concurrent batches.
        var sortedSlots = Enumerable.Range(0, distinct.Count)
            .OrderBy(s => distinct[s].Identifier.Domain, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => distinct[s].Identifier.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => distinct[s].Identifier.TableName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var dbDialect = IsPostgreSql ? DatabaseDialect.PostgreSql : DatabaseDialect.SqlServer;
        var chunkSize = Math.Clamp(
            _dialect.ParameterBudget.CalculateEffectiveChunkSize(MaxBatchChunkSize, 3, 0, dbDialect, 50),
            1,
            MaxBatchChunkSize);

        var distinctResults = new CatalogTableUpsertResult[distinct.Count];
        for (var offset = 0; offset < sortedSlots.Length; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, sortedSlots.Length - offset);
            var chunkSlots = new int[count];
            var chunkItems = new TableMetadata[count];
            for (var i = 0; i < count; i++)
            {
                var slot = sortedSlots[offset + i];
                chunkSlots[i] = slot;
                chunkItems[i] = distinct[slot];
            }

            var outcomes = await UpsertChunkWithRetryAsync(chunkItems, ct).ConfigureAwait(false);
            var hydrated = await HydrateChunkResultsAsync(chunkItems, outcomes, ct).ConfigureAwait(false);
            for (var i = 0; i < count; i++)
            {
                distinctResults[chunkSlots[i]] = hydrated[i];
            }
        }

        var results = new CatalogTableUpsertResult[validated.Length];
        for (var i = 0; i < validated.Length; i++)
        {
            results[i] = distinctResults[inputToSlot[i]];
        }

        return results;
    }

    private async Task<ChunkItemOutcome[]> UpsertChunkWithRetryAsync(IReadOnlyList<TableMetadata> chunk, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await UpsertChunkAsync(chunk, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxBatchChunkAttempts && IsTransientChunkConflict(ex))
            {
                LogCatalogNotice($"Transient conflict while upserting catalog batch chunk (attempt {attempt} of {MaxBatchChunkAttempts}); retrying.");
            }
        }
    }

    private bool IsTransientChunkConflict(Exception ex) =>
        ex is DbException dbEx && (_dialect.IsTransient(dbEx) || dbEx.IsTransient || dbEx.SqlState is "40001" or "40P01");

    private async Task<ChunkItemOutcome[]> UpsertChunkAsync(IReadOnlyList<TableMetadata> chunk, CancellationToken ct)
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

        CatalogWriteScope? writeScope = CatalogWriteScopeContext.TryGetActive(this);
        var deferVersionToScope = false;
        long totalStructureDelta = 0;
        long totalContentDelta = 0;
        var anyChanged = false;

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var existingTables = await SelectChunkTablesAsync(conn, tx, chunk, lockForUpdate: true, ct).ConfigureAwait(false);
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

            var before = await LoadChunkDetailsAsync(conn, tx, existingTableIds, includePhysical: false, ct).ConfigureAwait(false);
            var precedence = _options?.Catalog?.DocumentationSourcePrecedence;
            var anyStoredBefore = false;

            for (var i = 0; i < count; i++)
            {
                var incoming = chunk[i];
                var existed = !wasCreated[i];
                var docs = new TableDocumentation(incoming.Table.Description, incoming.Table.LongDescription, incoming.Table.DocumentationSource);

                Dictionary<string, (Guid Id, string DataType, bool IsSensitive, string? Description, string? LongDescription, string? DocSource, string? Collation, string? LengthSemantics)> existingColsByName;
                Dictionary<string, MaskingRule> existingMaskingRules;
                Dictionary<string, Guid> colIdByName;

                if (existed)
                {
                    var existingTable = existingTables[NormalizedIdentifierKey.From(incoming.Identifier)];
                    (structuralChanges[i], docs) = ComputeTableUpsertState(existingTable, incoming.Table, precedence);

                    var cols = before.ColumnsByTable.TryGetValue(tableIds[i], out var loadedCols) ? loadedCols : new List<TableColumn>();
                    var masks = before.MasksByTable.TryGetValue(tableIds[i], out var loadedMasks)
                        ? loadedMasks
                        : new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);

                    storedBefore[i] = before.CorruptedIds.Contains(tableIds[i])
                        ? null
                        : await TryProjectFromSnapshotAsync(conn, tx, incoming.Identifier, existingTable, cols, masks, before.Keys, ct).ConfigureAwait(false);
                    if (storedBefore[i] is not null)
                    {
                        anyStoredBefore = true;
                    }

                    BuildColumnMaps(cols, masks, out existingColsByName, out existingMaskingRules, out colIdByName);
                }
                else
                {
                    structuralChanges[i] = true;
                    existingColsByName = new(StringComparer.OrdinalIgnoreCase);
                    existingMaskingRules = new(StringComparer.OrdinalIgnoreCase);
                    colIdByName = new(StringComparer.OrdinalIgnoreCase);
                }

                await ExecuteTableUpsertRowAsync(conn, tx, tableIds[i], incoming, docs, ct).ConfigureAwait(false);
                if (!existed && _options?.Catalog?.DefaultVisibility == DatasetVisibility.Summary)
                {
                    await DatasetCatalogStore.InsertDefaultAsync(conn, tx, KeyDialect, tableIds[i], DatasetVisibility.Summary, ct).ConfigureAwait(false);
                }

                if (HasMaskingRulesChanged(existingMaskingRules, incoming.ColumnMaskingRules))
                {
                    structuralChanges[i] = true;
                }

                if (await UpsertColumnsAndMaskingRulesAsync(
                        conn, tx, InsertColumnSql, tableIds[i], incoming, existingColsByName, colIdByName, ct,
                        (existing, inc) => ShouldOverwriteDocumentation(precedence, existing, inc)).ConfigureAwait(false))
                {
                    structuralChanges[i] = true;
                }

                if (incoming.ReplaceKeyDefinition)
                {
                    structuralChanges[i] = true;
                    await CatalogKeyDefinitionStore.ReplaceAsync(conn, tx, KeyDialect, tableIds[i], incoming, colIdByName, ct).ConfigureAwait(false);
                }
            }

            Dictionary<NormalizedIdentifierKey, Table>? afterTables = null;
            ChunkColumnKeySnapshot? after = null;
            if (anyStoredBefore)
            {
                var afterItems = existingLookupIndices.Select(idx => chunk[idx]).ToArray();
                afterTables = await SelectChunkTablesAsync(conn, tx, afterItems, lockForUpdate: false, ct).ConfigureAwait(false);
                after = await LoadChunkDetailsAsync(conn, tx, existingTableIds, includePhysical: false, ct).ConfigureAwait(false);
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
                            var afterProj = await TryProjectFromSnapshotAsync(conn, tx, chunk[i].Identifier, afterTable, cols, masks, afterSnap.Keys, ct).ConfigureAwait(false);
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
                for (var i = 0; i < count; i++)
                {
                    if (classifications[i] != CatalogWriteClassification.Unchanged)
                    {
                        await IncrementTableEpochInternalAsync(conn, tx, chunk[i].Identifier, ct).ConfigureAwait(false);
                    }
                }

                deferVersionToScope = writeScope is not null && !anyRestrictive;
                CatalogVersion? newVersion = null;
                if (!deferVersionToScope)
                {
                    newVersion = await BumpCatalogVersionInternalAsync(conn, tx, totalStructureDelta, totalContentDelta, ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);

                if (newVersion.HasValue)
                {
                    await PublishCatalogChangedEventAsync(newVersion.Value, ct).ConfigureAwait(false);
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
                InvalidateTableMetadata(chunk[i].Identifier.ToString().ToLowerInvariant());
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
                InvalidateTableMetadata(chunk[i].Identifier.ToString().ToLowerInvariant());
            }
            else
            {
                await InvalidateTableEpochAsync(chunk[i].Identifier, ct).ConfigureAwait(false);
            }
        }

        return outcomes;
    }

    private async Task<CatalogTableUpsertResult[]> HydrateChunkResultsAsync(
        IReadOnlyList<TableMetadata> chunk,
        ChunkItemOutcome[] outcomes,
        CancellationToken ct)
    {
        var results = new CatalogTableUpsertResult[chunk.Count];
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var tables = await SelectChunkTablesAsync(conn, null, chunk, lockForUpdate: false, ct).ConfigureAwait(false);
        var loadedIds = tables.Values.Select(e => e.Id).ToList();
        var details = await LoadChunkDetailsAsync(conn, null, loadedIds, includePhysical: true, ct).ConfigureAwait(false);
        var rowFilters = await RowFilterEvaluationStore.ReadBatchObjectAsync(conn, null, KeyDialect, ct).ConfigureAwait(false);
        Dictionary<Guid, long>? epochs = null;
        try
        {
            epochs = await ReadAllTableEpochsAsync(conn, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCatalogNotice("Table epochs could not be read after batch upsert; loaded metadata will not be cached.");
        }

        var overlays = await DatasetCatalogStore.ReadOverlayAsync(conn, null, KeyDialect, null, _logger, ct).ConfigureAwait(false);

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
                    conn, null, KeyDialect, incoming.Identifier, entity.Id, cols, ct,
                    _keyTables, details.Keys is not null && details.Keys.TryGetValue(entity.Id, out var kr) ? kr : null).ConfigureAwait(false);

                var physicalIndexes = details.PhysicalIndexes is not null && details.PhysicalIndexes.TryGetValue(entity.Id, out var physicalRows)
                    ? CatalogPhysicalIndexStore.Assemble(cols, physicalRows)
                    : Array.Empty<PhysicalIndexDefinition>();

                rowFilters.TryGetValue(entity.Id, out var rowFilterEval);

                var structural = new TableMetadata
                {
                    Identifier = incoming.Identifier,
                    Table = entity,
                    Columns = cols,
                    ColumnMaskingRules = masks,
                    PrimaryKeyColumns = pkCols,
                    PrimaryKeyDirections = pkDirs,
                    Indexes = indexes,
                    PhysicalIndexes = physicalIndexes,
                    RowFilterEvaluation = rowFilterEval
                };
                structural.ValidateRequiredFilterColumns();

                if (AfterMetadataSnapshotForTesting is { } hook)
                {
                    await hook(ct).ConfigureAwait(false);
                }

                if (epochs != null && epochs.TryGetValue(entity.Id, out var ep) && ep >= 0)
                {
                    _metadataCache[incoming.Identifier.ToString().ToLowerInvariant()] =
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

    private async Task<ChunkColumnKeySnapshot> LoadChunkDetailsAsync(
        DbConnection conn, DbTransaction? tx, IReadOnlyList<Guid> tableIds, bool includePhysical, CancellationToken ct)
    {
        if (tableIds.Count == 0)
        {
            return new ChunkColumnKeySnapshot(new(), new(), new(), false, null, null);
        }

        var (cols, masks, corrupted) = await LoadChunkColumnsAsync(conn, tx, tableIds, ct).ConfigureAwait(false);
        var keyTablesExist = await CatalogKeyDefinitionStore.TablesExistAsync(conn, tx, KeyDialect, ct, _keyTables).ConfigureAwait(false);
        Dictionary<Guid, CatalogKeyDefinitionStore.TableKeyRows>? keys = null;
        Dictionary<Guid, List<CatalogPhysicalIndexStore.Row>>? physical = null;
        if (keyTablesExist)
        {
            keys = await CatalogKeyDefinitionStore.ReadBatchAsync(conn, tx, KeyDialect, tableIds, ct).ConfigureAwait(false);
            if (includePhysical)
            {
                physical = await CatalogPhysicalIndexStore.ReadBatchAsync(conn, tx, KeyDialect, tableIds, ct).ConfigureAwait(false);
            }
        }

        return new ChunkColumnKeySnapshot(cols, masks, corrupted, keyTablesExist, keys, physical);
    }

    private async Task<CatalogTablePersistedProjection?> TryProjectFromSnapshotAsync(
        DbConnection conn,
        DbTransaction tx,
        TableIdentifier identifier,
        Table tableEntity,
        List<TableColumn> columns,
        Dictionary<string, MaskingRule> maskingRules,
        IReadOnlyDictionary<Guid, CatalogKeyDefinitionStore.TableKeyRows>? prefetchedKeys,
        CancellationToken ct)
    {
        try
        {
            var (pkCols, pkDirs, indexes) = await CatalogKeyDefinitionStore.ReadAsync(
                conn, tx, KeyDialect, identifier, tableEntity.Id, columns, ct,
                _keyTables, prefetchedKeys is not null && prefetchedKeys.TryGetValue(tableEntity.Id, out var kr) ? kr : null).ConfigureAwait(false);

            return CatalogTablePersistedProjection.From(new TableMetadata
            {
                Identifier = identifier,
                Table = tableEntity,
                Columns = columns,
                ColumnMaskingRules = maskingRules,
                PrimaryKeyColumns = pkCols,
                PrimaryKeyDirections = pkDirs,
                Indexes = indexes
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCatalogNotice("The stored table state could not be projected for no-op detection; the write proceeds.");
            return null;
        }
    }

    private async Task ExecuteTableUpsertRowAsync(
        DbConnection conn,
        DbTransaction tx,
        Guid tableId,
        TableMetadata metadata,
        TableDocumentation docs,
        CancellationToken ct)
    {
        var httpEndpointJson = metadata.Table.HttpEndpoint != null
            ? JsonSerializer.Serialize(metadata.Table.HttpEndpoint)
            : null;

        await using var upsertCmd = conn.CreateCommand();
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

    private async Task<Dictionary<NormalizedIdentifierKey, Table>> SelectChunkTablesAsync(
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
            ? (IsPostgreSql ? CatalogSql.SelectTablesWherePrefix : CatalogSql.SelectTablesWithUpdLockWherePrefix)
            : CatalogSql.SelectTablesWherePrefix;
        var suffix = lockForUpdate && IsPostgreSql
            ? " ORDER BY LOWER(source_name), LOWER(schema_name), LOWER(table_name) FOR UPDATE"
            : " ORDER BY LOWER(source_name), LOWER(schema_name), LOWER(table_name)";
        cmd.CommandText = prefix + where + suffix;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var entity = ReadTableEntity(reader, _logger);
            result[NormalizedIdentifierKey.From(entity)] = entity;
        }

        return result;
    }

    private async Task<(Dictionary<Guid, List<TableColumn>> ColumnsByTable, Dictionary<Guid, Dictionary<string, MaskingRule>> MaskingRulesByTable, HashSet<Guid> CorruptedTableIds)>
        LoadChunkColumnsAsync(DbConnection conn, DbTransaction? tx, IReadOnlyList<Guid> tableIds, CancellationToken ct)
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
        var prefix = IsPostgreSql ? CatalogSql.SelectColumnsByTableIdsPrefixPg : CatalogSql.SelectColumnsByTableIdsPrefixSs;
        cmd.CommandText = prefix + ConsentSqlCore.BindIdInClause(cmd, "tid", idStrings) + ") ORDER BY c.table_id, c.ordinal, c.column_name";
        return await LoadBatchTableColumnsAsync(cmd, ct).ConfigureAwait(false);
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
        out Dictionary<string, (Guid Id, string DataType, bool IsSensitive, string? Description, string? LongDescription, string? DocSource, string? Collation, string? LengthSemantics)> existingColsByName,
        out Dictionary<string, MaskingRule> existingMaskingRules,
        out Dictionary<string, Guid> colIdByName)
    {
        existingColsByName = new Dictionary<string, (Guid, string, bool, string?, string?, string?, string?, string?)>(StringComparer.OrdinalIgnoreCase);
        existingMaskingRules = new Dictionary<string, MaskingRule>(maskingRules, StringComparer.OrdinalIgnoreCase);
        colIdByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var col in columns)
        {
            existingColsByName[col.ColumnName] = (
                col.Id, col.DataType, col.IsSensitive,
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
