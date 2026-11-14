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
        Dictionary<Guid, List<CatalogPhysicalIndexStore.Row>>? PhysicalIndexes);

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

            var before = await LoadChunkDetailsAsync(repo, conn, tx, existingTableIds, includePhysical: false, ct).ConfigureAwait(false);
            var precedence = repo.OptionsSnapshot?.Catalog?.DocumentationSourcePrecedence;
            var anyStoredBefore = false;

            for (var i = 0; i < count; i++)
            {
                var incoming = chunk[i];
                var existed = !wasCreated[i];
                var docs = new GovernanceRepository.TableDocumentation(
                    incoming.Table.Description,
                    incoming.Table.LongDescription,
                    incoming.Table.DocumentationSource);

                Dictionary<string, (Guid Id, string DataType, bool IsSensitive, string? Description, string? LongDescription, string? DocSource, string? Collation, string? LengthSemantics)> existingColsByName;
                Dictionary<string, MaskingRule> existingMaskingRules;
                Dictionary<string, Guid> colIdByName;

                if (existed)
                {
                    var existingTable = existingTables[NormalizedIdentifierKey.From(incoming.Identifier)];
                    (structuralChanges[i], docs) = GovernanceRepository.ComputeTableUpsertState(existingTable, incoming.Table, precedence);

                    var cols = before.ColumnsByTable.TryGetValue(tableIds[i], out var loadedCols) ? loadedCols : new List<TableColumn>();
                    var masks = before.MasksByTable.TryGetValue(tableIds[i], out var loadedMasks)
                        ? loadedMasks
                        : new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);

                    storedBefore[i] = before.CorruptedIds.Contains(tableIds[i])
                        ? null
                        : await TryProjectFromSnapshotAsync(repo, conn, tx, incoming.Identifier, existingTable, cols, masks, before.Keys, ct).ConfigureAwait(false);
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

                await ExecuteTableUpsertRowAsync(repo, conn, tx, tableIds[i], incoming, docs, ct).ConfigureAwait(false);
                if (!existed && repo.OptionsSnapshot?.Catalog?.DefaultVisibility == DatasetVisibility.Summary)
                {
                    await DatasetCatalogStore.InsertDefaultAsync(conn, tx, repo.KeyDialect, tableIds[i], DatasetVisibility.Summary, ct).ConfigureAwait(false);
                }

                if (GovernanceRepository.HasMaskingRulesChanged(existingMaskingRules, incoming.ColumnMaskingRules))
                {
                    structuralChanges[i] = true;
                }

                if (await GovernanceRepository.UpsertColumnsAndMaskingRulesAsync(
                        conn, tx, repo.InsertColumnSql, tableIds[i], incoming, existingColsByName, colIdByName, ct,
                        (existing, inc) => GovernanceRepository.ShouldOverwriteDocumentation(precedence, existing, inc)).ConfigureAwait(false))
                {
                    structuralChanges[i] = true;
                }

                if (incoming.ReplaceKeyDefinition)
                {
                    structuralChanges[i] = true;
                    await CatalogKeyDefinitionStore.ReplaceAsync(conn, tx, repo.KeyDialect, tableIds[i], incoming, colIdByName, ct).ConfigureAwait(false);
                }
            }

            Dictionary<NormalizedIdentifierKey, Table>? afterTables = null;
            ChunkColumnKeySnapshot? after = null;
            if (anyStoredBefore)
            {
                var afterItems = existingLookupIndices.Select(idx => chunk[idx]).ToArray();
                afterTables = await SelectChunkTablesAsync(repo, conn, tx, afterItems, lockForUpdate: false, ct).ConfigureAwait(false);
                after = await LoadChunkDetailsAsync(repo, conn, tx, existingTableIds, includePhysical: false, ct).ConfigureAwait(false);
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
                            var afterProj = await TryProjectFromSnapshotAsync(repo, conn, tx, chunk[i].Identifier, afterTable, cols, masks, afterSnap.Keys, ct).ConfigureAwait(false);
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
                        await repo.IncrementTableEpochInternalAsync(conn, tx, chunk[i].Identifier, ct).ConfigureAwait(false);
                    }
                }

                deferVersionToScope = writeScope is not null && !anyRestrictive;
                CatalogVersion? newVersion = null;
                if (!deferVersionToScope)
                {
                    newVersion = await repo.BumpCatalogVersionInternalAsync(conn, tx, totalStructureDelta, totalContentDelta, ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);

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
        var details = await LoadChunkDetailsAsync(repo, conn, null, loadedIds, includePhysical: true, ct).ConfigureAwait(false);
        var rowFilters = await RowFilterEvaluationStore.ReadBatchObjectAsync(conn, null, repo.KeyDialect, ct).ConfigureAwait(false);

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
        bool includePhysical,
        CancellationToken ct)
    {
        if (tableIds.Count == 0)
        {
            return new ChunkColumnKeySnapshot(new(), new(), new(), null, null);
        }

        var (cols, masks, corrupted) = await LoadChunkColumnsAsync(repo, conn, tx, tableIds, ct).ConfigureAwait(false);
        var keyTablesExist = await CatalogKeyDefinitionStore.TablesExistAsync(conn, tx, repo.KeyDialect, ct, repo.KeyTableProbe).ConfigureAwait(false);
        Dictionary<Guid, CatalogKeyDefinitionStore.TableKeyRows>? keys = null;
        Dictionary<Guid, List<CatalogPhysicalIndexStore.Row>>? physical = null;
        if (keyTablesExist)
        {
            keys = await CatalogKeyDefinitionStore.ReadBatchAsync(conn, tx, repo.KeyDialect, tableIds, ct).ConfigureAwait(false);
            if (includePhysical)
            {
                physical = await CatalogPhysicalIndexStore.ReadBatchAsync(conn, tx, repo.KeyDialect, tableIds, ct).ConfigureAwait(false);
            }
        }

        return new ChunkColumnKeySnapshot(cols, masks, corrupted, keys, physical);
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

    private static async Task ExecuteTableUpsertRowAsync(
        GovernanceRepository repo,
        DbConnection conn,
        DbTransaction tx,
        Guid tableId,
        TableMetadata metadata,
        GovernanceRepository.TableDocumentation docs,
        CancellationToken ct)
    {
        var httpEndpointJson = metadata.Table.HttpEndpoint != null
            ? JsonSerializer.Serialize(metadata.Table.HttpEndpoint)
            : null;

        await using var upsertCmd = conn.CreateCommand();
        upsertCmd.Transaction = tx;
        upsertCmd.CommandText = repo.UpsertTableSql;
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
            var entity = GovernanceRepository.ReadTableEntity(reader, repo.CatalogLogger);
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
