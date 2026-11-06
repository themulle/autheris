using System.Collections.Immutable;
using System.Text.Json;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using Npgsql;
using Autheris.Infrastructure.Persistence.Catalog;

namespace Autheris.Infrastructure.Persistence;

public partial class PostgreSqlGovernanceRepository
{
    private static class CatalogSql
    {
        public const string SelectTableEntitySql = @"SELECT id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, data_source_type, http_endpoint_json, plugin_name, description, long_description, doc_source, required_filter_columns_json, texts_json, min_group_size, non_personal
FROM TABLES
WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)";

        public const string SelectActiveTableSummariesSql = "SELECT id, source_name, schema_name, table_name FROM TABLES WHERE is_active = 1";

        public const string SelectActiveTableEntitiesSql = @"SELECT id, source_type, source_name, schema_name, table_name,
       display_name, sensitivity, requires_four_eyes, is_active,
       data_source_type, http_endpoint_json, plugin_name,
       description, long_description, doc_source,
       required_filter_columns_json, texts_json,
       min_group_size, non_personal
FROM TABLES
WHERE is_active = 1";

        public const string SelectTableColumnsSql = @"SELECT c.id, c.table_id, c.column_name, c.data_type, c.is_sensitive,
       m.id, m.rule_type, m.pattern_or_format, m.replacement, m.hmac_key_id,
       c.description, c.long_description, c.meta_json, c.doc_source,
       c.display_name, c.texts_json
FROM TABLE_COLUMNS c
LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
WHERE c.table_id = @tableId";

        public const string SelectActiveBatchTableColumnsSql = @"SELECT c.id, c.table_id, c.column_name, c.data_type, c.is_sensitive,
       m.id, m.rule_type, m.pattern_or_format, m.replacement, m.hmac_key_id,
       c.description, c.long_description, c.meta_json, c.doc_source,
       c.display_name, c.texts_json
FROM TABLE_COLUMNS c
LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
INNER JOIN TABLES t ON c.table_id = t.id
WHERE t.is_active = 1";

        public const string SelectTableForUpdateSql = @"SELECT id, source_type, sensitivity, requires_four_eyes, is_active, data_source_type FROM TABLES
WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)
FOR UPDATE";

        public const string UpsertTableSql = @"INSERT INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, data_source_type, http_endpoint_json, plugin_name, description, long_description, doc_source, required_filter_columns_json, texts_json, min_group_size, non_personal)
VALUES (@id, @sourceType, @domain, @schema, @tableName, @displayName, @sensitivity, @fourEyes, @active, @dst, @httpJson, @plugin, @desc, @longDesc, @docSource, @requiredFilterColumnsJson, @textsJson, @minGroupSize, @nonPersonal)
ON CONFLICT (id) DO UPDATE SET
    source_type = EXCLUDED.source_type,
    source_name = EXCLUDED.source_name,
    schema_name = EXCLUDED.schema_name,
    table_name = EXCLUDED.table_name,
    display_name = EXCLUDED.display_name,
    sensitivity = EXCLUDED.sensitivity,
    requires_four_eyes = EXCLUDED.requires_four_eyes,
    is_active = EXCLUDED.is_active,
    data_source_type = EXCLUDED.data_source_type,
    http_endpoint_json = EXCLUDED.http_endpoint_json,
    plugin_name = EXCLUDED.plugin_name,
    description = COALESCE(EXCLUDED.description, TABLES.description),
    long_description = COALESCE(EXCLUDED.long_description, TABLES.long_description),
    doc_source = COALESCE(EXCLUDED.doc_source, TABLES.doc_source),
    required_filter_columns_json = EXCLUDED.required_filter_columns_json,
    texts_json = EXCLUDED.texts_json,
    min_group_size = EXCLUDED.min_group_size,
    non_personal = EXCLUDED.non_personal";

        public const string InsertColumnSql = @"INSERT INTO TABLE_COLUMNS (id, table_id, column_name, data_type, is_sensitive, description, long_description, doc_source, meta_json, display_name, texts_json)
VALUES (@id, @tableId, @columnName, @dataType, @isSensitive, @description, @longDescription, @docSource, @metaJson, @displayName, @textsJson)
ON CONFLICT (id) DO UPDATE SET
    data_type = EXCLUDED.data_type,
    is_sensitive = EXCLUDED.is_sensitive,
    description = COALESCE(EXCLUDED.description, TABLE_COLUMNS.description),
    long_description = COALESCE(EXCLUDED.long_description, TABLE_COLUMNS.long_description),
    doc_source = COALESCE(EXCLUDED.doc_source, TABLE_COLUMNS.doc_source),
    meta_json = COALESCE(EXCLUDED.meta_json, TABLE_COLUMNS.meta_json),
    display_name = EXCLUDED.display_name,
    texts_json = EXCLUDED.texts_json";

        public const string ActiveTableIdentifierSql = "SELECT source_name, schema_name, table_name FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table) AND is_active = 1";

        public const string TableEpochSql = @"SELECT epoch FROM POLICY_EPOCHS
WHERE LOWER(domain) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)";

        public const string RelationsForParentTableSql = @"SELECT r.id, r.parent_table_id, r.child_table_id, r.relation_name, r.cardinality,
       tp.source_name, tp.schema_name, tp.table_name,
       tc.source_name, tc.schema_name, tc.table_name,
       k.key_ordinal, k.parent_column, k.child_column,
       COALESCE(r.allow_cross_data_source, FALSE)
FROM TABLE_RELATIONS r
JOIN TABLES tp ON r.parent_table_id = tp.id
JOIN TABLES tc ON r.child_table_id = tc.id
LEFT JOIN TABLE_RELATION_KEYS k ON k.relation_id = r.id
WHERE LOWER(tp.source_name) = LOWER(@pDomain) AND LOWER(tp.schema_name) = LOWER(@pSchema) AND LOWER(tp.table_name) = LOWER(@pTable)
ORDER BY r.id, k.key_ordinal";

        public const string AllRelationsSql = @"SELECT r.id, r.parent_table_id, r.child_table_id, r.relation_name, r.cardinality,
       COALESCE(tp.source_name, 'default'), tp.schema_name, tp.table_name,
       COALESCE(tc.source_name, 'default'), tc.schema_name, tc.table_name,
       k.key_ordinal, k.parent_column, k.child_column,
       COALESCE(r.allow_cross_data_source, FALSE)
FROM TABLE_RELATIONS r
JOIN TABLES tp ON r.parent_table_id = tp.id
JOIN TABLES tc ON r.child_table_id = tc.id
LEFT JOIN TABLE_RELATION_KEYS k ON k.relation_id = r.id
ORDER BY r.id, k.key_ordinal";

        public const string InsertRelationSql = @"INSERT INTO TABLE_RELATIONS (id, parent_table_id, child_table_id, relation_name, cardinality, allow_cross_data_source)
VALUES (@id, @pId, @cId, @relName, @card, @allowCross)
ON CONFLICT (parent_table_id, child_table_id, relation_name) DO UPDATE SET cardinality = EXCLUDED.cardinality
RETURNING id, (xmax = 0) AS inserted";

        public const string ResolveActiveTableIdSql = "SELECT id FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table) AND is_active = 1";

        public const string DataOwnersForTableSql = @"SELECT o.id, o.ad_sid, o.ad_account, o.display_name, o.email, o.is_active
FROM DATA_OWNERS o
JOIN TABLE_OWNERS tow ON o.id = tow.data_owner_id
JOIN TABLES t ON tow.table_id = t.id
WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table) AND o.is_active = 1";

        public const string ResolveDataOwnerIdsSql = @"SELECT id FROM DATA_OWNERS
WHERE LOWER(ad_sid) = LOWER(@p) OR LOWER(ad_account) = LOWER(@p) OR LOWER(email) = LOWER(@p)";

        public const string InsertDelegationSql = @"INSERT INTO DATA_OWNER_DELEGATIONS (id, data_owner_id, delegate_sid, valid_from, valid_to, reason)
VALUES (@id, @ownerId, @delegateSid, @validFrom, @validTo, @reason)
ON CONFLICT (id) DO UPDATE SET
    delegate_sid = EXCLUDED.delegate_sid,
    valid_from = EXCLUDED.valid_from,
    valid_to = EXCLUDED.valid_to,
    reason = EXCLUDED.reason";

        public const string CatalogVersionSql = "SELECT structure_version, content_version FROM catalog_state WHERE id = 1;";

        public const string BumpCatalogVersionSql = @"UPDATE catalog_state
SET structure_version = structure_version + @structureDelta,
    content_version = content_version + @contentDelta,
    updated_at = @now::timestamptz
WHERE id = 1
RETURNING structure_version, content_version;";
    }

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
            conn, null, CatalogKeyDefinitionStore.PostgreSql, structural.Table.Id, _logger, ct).ConfigureAwait(false);
        return DatasetCatalogStore.Apply(structural, overlay);
    }

    private async Task<TableMetadata?> GetTableMetadataCoreAsync(
        TableIdentifier table,
        IReadOnlyDictionary<Guid, CatalogKeyDefinitionStore.TableKeyRows>? prefetchedKeys,
        CancellationToken ct)
    {
        var cacheKey = table.ToString().ToLowerInvariant();
        var nowTicks = DateTime.UtcNow.Ticks;
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
                    tableEntity = CatalogSqlCore.ReadTableEntity(reader, _logger);
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
                _logger?.LogWarning(ex, "The table epoch could not be read; the loaded metadata is not cached.");
            }

            List<TableColumn> columns;
            Dictionary<string, MaskingRule> maskingRules;

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = CatalogSql.SelectTableColumnsSql;
                cmd.AddBound("@tableId", tableId.Value.ToString());
                (columns, maskingRules) = await CatalogSqlCore.LoadTableColumnsAsync(cmd, _logger, ct).ConfigureAwait(false);
            }

            IReadOnlyList<string> pkColumns;
            IReadOnlyList<KeyColumnDirection> pkDirections;
            IReadOnlyList<TableIndexDefinition> indexes;
            try
            {
                (pkColumns, pkDirections, indexes) = await CatalogKeyDefinitionStore.ReadAsync(
                    conn, null, CatalogKeyDefinitionStore.PostgreSql, table, tableId.Value, columns, ct,
                    _keyTables, prefetchedKeys is not null && prefetchedKeys.TryGetValue(tableId.Value, out var keyRows) ? keyRows : null).ConfigureAwait(false);
            }
            catch (TableMetadataUnavailableException)
            {
                _logger?.LogWarning("Table metadata is unavailable: a declared key or index definition is invalid (the table is named by the rethrown TableMetadataUnavailableException).");
                throw;
            }

            var physicalIndexes = await CatalogPhysicalIndexStore.ReadAsync(
                conn, null, CatalogKeyDefinitionStore.PostgreSql, tableId.Value, columns, ct).ConfigureAwait(false);

            var rowFilterEvaluation = await RowFilterEvaluationStore.ReadObjectAsync(
                conn, null, CatalogKeyDefinitionStore.PostgreSql, tableId.Value, ct).ConfigureAwait(false);

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

            if (loadedEpoch.HasValue)
            {
                _metadataCache[cacheKey] = (result, DateTime.UtcNow.Ticks, loadedEpoch.Value);
            }

            return result;
        }
    }

    // PERS-17: epoch rows are matched by the natural key (domain, schema, table): a write inserts the row with its own id, which is not TABLES.id.
    public const string EpochJoin = CatalogSqlCore.EpochJoin;
    internal const string EpochByTableIdSql = CatalogSqlCore.EpochByTableIdSql;
    internal const string AllEpochsSql = CatalogSqlCore.AllEpochsSql;

    // PERS-17: the table epoch (POLICY_EPOCHS) authorises cache hits across replicas; -1 means the table has no epoch row yet.
    internal static Task<long> ReadTableEpochAsync(System.Data.Common.DbConnection conn, Guid tableId, CancellationToken ct) =>
        CatalogSqlCore.ReadTableEpochAsync(conn, tableId, ct);

    internal static Task<Dictionary<Guid, long>> ReadAllTableEpochsAsync(System.Data.Common.DbConnection conn, CancellationToken ct) =>
        CatalogSqlCore.ReadAllTableEpochsAsync(conn, ct);

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
            _logger?.LogWarning(ex, "The table epoch could not be read; the metadata cache is bypassed (fail-closed).");
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
                    catch (ArgumentException ex)
                    {
                        _logger?.LogWarning(ex, "Catalog table has an invalid identifier and was skipped.");
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
            _logger?.LogWarning(ex, "The table epochs could not be read; the metadata cache is bypassed (fail-closed).");
        }

        bool TryGetValidCached(Guid tableId, string key, out TableMetadata? metadata)
        {
            metadata = null;
            if (epochs != null &&
                _metadataCache.TryGetValue(key, out var hit) &&
                hit.Metadata != null &&
                DateTime.UtcNow.Ticks - hit.CachedAtTicks < MetadataCacheTtlTicks &&
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
                activeTables = await CatalogSqlCore.LoadActiveTablesAsync(cmd, _logger, ct).ConfigureAwait(false);
            }

            Dictionary<Guid, List<TableColumn>> columnsByTable;
            Dictionary<Guid, Dictionary<string, MaskingRule>> maskingRulesByTable;
            HashSet<Guid> corruptedTableIds;

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = CatalogSql.SelectActiveBatchTableColumnsSql;
                (columnsByTable, maskingRulesByTable, corruptedTableIds) = await CatalogSqlCore.LoadBatchTableColumnsAsync(cmd, _logger, ct).ConfigureAwait(false);
            }

            Dictionary<Guid, CatalogKeyDefinitionStore.TableKeyRows>? prefetchedKeys = null;
            Dictionary<Guid, List<CatalogPhysicalIndexStore.Row>>? prefetchedPhysical = null;
            var allActiveTableIds = activeTables.Select(t => t.Id).ToList();
            if (await CatalogKeyDefinitionStore.TablesExistAsync(conn, null, CatalogKeyDefinitionStore.PostgreSql, ct, _keyTables).ConfigureAwait(false))
            {
                prefetchedKeys = await CatalogKeyDefinitionStore.ReadBatchAsync(conn, null, CatalogKeyDefinitionStore.PostgreSql, allActiveTableIds, ct).ConfigureAwait(false);
                prefetchedPhysical = await CatalogPhysicalIndexStore.ReadBatchAsync(conn, null, CatalogKeyDefinitionStore.PostgreSql, allActiveTableIds, ct).ConfigureAwait(false);
            }

            var rowFilterStrategies = await RowFilterEvaluationStore.ReadBatchObjectAsync(
                conn, null, CatalogKeyDefinitionStore.PostgreSql, ct).ConfigureAwait(false);

            result = new List<TableMetadata>(activeTables.Count);
            foreach (var (tid, identifier, tableEntity) in activeTables)
            {
                if (corruptedTableIds.Contains(tid))
                {
                    _logger?.LogWarning("Catalog table {Table} was omitted: its column or masking rule definition is invalid (fail-closed).", identifier);
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
                        conn, null, CatalogKeyDefinitionStore.PostgreSql, identifier, tid, columns, ct,
                        _keyTables, prefetchedKeys is not null && prefetchedKeys.TryGetValue(tid, out var keyRows) ? keyRows : null).ConfigureAwait(false);
                }
                catch (TableMetadataUnavailableException)
                {
                    _logger?.LogWarning("Catalog table {Table} was omitted: its declared key or index definition is invalid.", identifier);
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
                catch (CatalogValidationException ex)
                {
                    _logger?.LogWarning(ex, "Catalog table {Table} was omitted: required filter column definition is invalid.", identifier);
                    continue;
                }

                if (epochs != null)
                {
                    _metadataCache[cacheKey] = (meta, DateTime.UtcNow.Ticks, epochs.TryGetValue(tid, out var loaded) ? loaded : -1);
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
                overlayConn, null, CatalogKeyDefinitionStore.PostgreSql, null, _logger, ct).ConfigureAwait(false);
            for (var i = 0; i < result.Count; i++)
            {
                result[i] = DatasetCatalogStore.Apply(result[i], overlay);
            }
        }

        return result;
    }

    public async Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
    {
        metadata.ValidateRequiredFilterColumns();
        Autheris.Application.Catalog.Semantic.SemanticVirtualTables.EnsureNamespaceRespected(metadata);
        // Only a replacing write validates (and later persists) the definition; any other upsert never reads or rewrites it (SEC-94-01).
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

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = CatalogSql.SelectTableForUpdateSql;
                cmd.AddBound("@domain", metadata.Identifier.Domain);
                cmd.AddBound("@schema", metadata.Identifier.Schema);
                cmd.AddBound("@table", metadata.Identifier.TableName);

                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    tableId = Guid.Parse(reader.GetString(0));
                    tableExisted = true;
                    var existingSourceType = reader.GetString(1);
                    var existingSensitivity = reader.GetString(2);
                    var existingFourEyes = reader.GetInt32(3) == 1;
                    var existingActive = reader.GetInt32(4) == 1;
                    var existingDst = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
                    if (!string.Equals(existingSourceType, metadata.Table.SourceType, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(existingSensitivity, metadata.Table.Sensitivity, StringComparison.OrdinalIgnoreCase) ||
                        existingFourEyes != metadata.Table.RequiresFourEyes ||
                        existingActive != metadata.Table.IsActive ||
                        existingDst != (int)metadata.Table.DataSourceType)
                    {
                        structuralChange = true;
                    }
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
                upsertCmd.CommandText = CatalogSql.UpsertTableSql;
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
                upsertCmd.AddBound("@desc", (object?)metadata.Table.Description ?? DBNull.Value);
                upsertCmd.AddBound("@longDesc", (object?)metadata.Table.LongDescription ?? DBNull.Value);
                upsertCmd.AddBound("@docSource", (object?)metadata.Table.DocumentationSource ?? DBNull.Value);
                upsertCmd.AddBound("@requiredFilterColumnsJson", metadata.RequiredFilterColumns.IsDefaultOrEmpty ? (object)DBNull.Value : JsonSerializer.Serialize(metadata.RequiredFilterColumns));
                upsertCmd.AddBound("@textsJson", (object?)CatalogLocalizationPersistence.SerializeLocalizedTexts(metadata.Table.Texts) ?? DBNull.Value);
                upsertCmd.AddBound("@minGroupSize", (object?)metadata.Table.MinGroupSize ?? DBNull.Value);
                upsertCmd.AddBound("@nonPersonal", metadata.Table.NonPersonal);
                await upsertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // Issue #111 (A-10): the configured default tier is given to a NEW table only; the Hidden default writes no row.
            if (!tableExisted && _options?.Catalog?.DefaultVisibility == DatasetVisibility.Summary)
            {
                await DatasetCatalogStore.InsertDefaultAsync(conn, tx, CatalogKeyDefinitionStore.PostgreSql, tableId, DatasetVisibility.Summary, ct).ConfigureAwait(false);
            }

            var (existingColsByName, existingMaskingRules, colIdByName) = await CatalogSqlCore.LoadExistingColumnsAndMaskingRulesAsync(conn, tx, tableId, ct).ConfigureAwait(false);
            if (CatalogSqlCore.HasMaskingRulesChanged(existingMaskingRules, metadata.ColumnMaskingRules))
            {
                structuralChange = true;
            }

            if (await CatalogSqlCore.UpsertColumnsAndMaskingRulesAsync(conn, tx, CatalogSql.InsertColumnSql, tableId, metadata, existingColsByName, colIdByName, ct).ConfigureAwait(false))
            {
                structuralChange = true;
            }

            if (metadata.ReplaceKeyDefinition)
            {
                structuralChange = true;
                await CatalogKeyDefinitionStore.ReplaceAsync(conn, tx, CatalogKeyDefinitionStore.PostgreSql, tableId, metadata, colIdByName, ct).ConfigureAwait(false);
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

        _metadataCache.TryRemove(metadata.Identifier.ToString().ToLowerInvariant(), out _);
        await _epochValidationService.InvalidateEpochAsync(metadata.Identifier, ct).ConfigureAwait(false);

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
                    conn, tx, CatalogKeyDefinitionStore.PostgreSql, table, primaryKey, indexes, primaryKeyDirections,
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

        _metadataCache.TryRemove(table.ToString().ToLowerInvariant(), out _);
        await _epochValidationService.InvalidateEpochAsync(table, ct).ConfigureAwait(false);
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
                    conn, tx, CatalogKeyDefinitionStore.PostgreSql, table, settings, actor,
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

        _metadataCache.TryRemove(table.ToString().ToLowerInvariant(), out _);
        await _epochValidationService.InvalidateEpochAsync(table, ct).ConfigureAwait(false);
        return change;
    }

    public async Task<DatasetVisibilityCounts> GetDatasetVisibilityCountsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await DatasetCatalogStore.CountByVisibilityAsync(conn, null, CatalogKeyDefinitionStore.PostgreSql, ct).ConfigureAwait(false);
    }

    public async Task<DatasetCatalogSettings?> GetDatasetCatalogSettingsAsync(TableIdentifier table, bool strict = true, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await DatasetCatalogStore.ReadSettingsAsync(
            conn, null, CatalogKeyDefinitionStore.PostgreSql, table, strict, _logger, ct).ConfigureAwait(false);
    }

    public Task<DatasetCatalogChange?> ReplaceDatasetTagsAsync(
        TableIdentifier table, IReadOnlyList<string> tags, DatasetTagSource source, Sid actor, CancellationToken ct = default) =>
        WriteCatalogSettingsAsync(table, (conn, tx) =>
            DatasetCatalogStore.ReplaceTagsAsync(conn, tx, CatalogKeyDefinitionStore.PostgreSql, table, tags, source, actor, ct), ct);

    public Task<DatasetCatalogChange?> ChangeDatasetTagsAsync(
        TableIdentifier table, IReadOnlyList<string> add, IReadOnlyList<string> remove, Sid actor, CancellationToken ct = default) =>
        WriteCatalogSettingsAsync(table, (conn, tx) =>
            DatasetCatalogStore.ChangeTagsAsync(conn, tx, CatalogKeyDefinitionStore.PostgreSql, table, add, remove, actor, ct), ct);

    public Task<DatasetCatalogChange?> SetDatasetVisibilityAsync(
        TableIdentifier table, DatasetVisibility visibility, Sid actor, CancellationToken ct = default) =>
        WriteCatalogSettingsAsync(table, (conn, tx) =>
            DatasetCatalogStore.SetVisibilityAsync(conn, tx, CatalogKeyDefinitionStore.PostgreSql, table, visibility, actor, ct), ct);

    public Task<DatasetCatalogChange?> SetDatasetPrivacyAsync(
        TableIdentifier table, int? minGroupSize, bool? nonPersonal, Sid actor, CancellationToken ct = default) =>
        WriteCatalogSettingsAsync(table, (conn, tx) =>
            DatasetCatalogStore.SetPrivacyAsync(conn, tx, CatalogKeyDefinitionStore.PostgreSql, table, minGroupSize, nonPersonal, actor, ct), ct);

    /// <summary>Issue #111: one transaction (locked before read, write, after read); no epoch (tags are not policy inputs); the local metadata entry is dropped after the commit.</summary>
    private async Task<DatasetCatalogChange?> WriteCatalogSettingsAsync(
        TableIdentifier table, Func<NpgsqlConnection, NpgsqlTransaction, Task<DatasetCatalogChange?>> write, CancellationToken ct)
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
            _metadataCache.TryRemove(table.ToString().ToLowerInvariant(), out _);
        }

        return change;
    }

    public async Task<TableIdentifier?> GetActiveTableIdentifierAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CatalogSqlCore.GetActiveTableIdentifierAsync(conn, CatalogSql.ActiveTableIdentifierSql, table, ct).ConfigureAwait(false);
    }

    public async Task<TableIdentifier?> GetActiveTableIdentifierByIdAsync(Guid tableId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CatalogSqlCore.GetActiveTableIdentifierByIdAsync(conn, tableId, ct).ConfigureAwait(false);
    }

    public async Task<long> GetTableEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CatalogSqlCore.GetTableEpochAsync(conn, CatalogSql.TableEpochSql, table, ct).ConfigureAwait(false);
    }

    public async Task<long> IncrementTableEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var newEpoch = await IncrementTableEpochInternalAsync(conn, tx, table, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            _metadataCache.TryRemove(table.ToString().ToLowerInvariant(), out _);
            await _epochValidationService.InvalidateEpochAsync(table, ct).ConfigureAwait(false);
            return newEpoch;
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<long> IncrementTableEpochInternalAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TableIdentifier table, CancellationToken ct)
    {
        _metadataCache.TryRemove(table.ToString().ToLowerInvariant(), out _);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"UPDATE POLICY_EPOCHS
                                SET epoch = epoch + 1, updated_at = @now
                                WHERE LOWER(domain) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)
                                RETURNING epoch";
            cmd.AddBound("@domain", table.Domain);
            cmd.AddBound("@schema", table.Schema);
            cmd.AddBound("@table", table.TableName);
            cmd.AddBound("@now", DateTimeOffset.UtcNow.ToString("O"));

            var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (scalar != null)
            {
                return Convert.ToInt64(scalar);
            }

            await using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = @"INSERT INTO POLICY_EPOCHS (table_id, domain, schema_name, table_name, epoch, updated_at)
                                      VALUES (@id, @domain, @schema, @table, 1, @now)
                                      ON CONFLICT (table_id) DO UPDATE SET epoch = POLICY_EPOCHS.epoch + 1, updated_at = EXCLUDED.updated_at
                                      RETURNING epoch";
            insertCmd.AddBound("@id", Guid.NewGuid().ToString());
            insertCmd.AddBound("@domain", table.Domain);
            insertCmd.AddBound("@schema", table.Schema);
            insertCmd.AddBound("@table", table.TableName);
            insertCmd.AddBound("@now", DateTimeOffset.UtcNow.ToString("O"));

            var insScalar = await insertCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return insScalar != null ? Convert.ToInt64(insScalar) : 1L;
        }
    }

    public async Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CatalogSqlCore.GetRelationsForTableAsync(conn, CatalogSql.RelationsForParentTableSql, parentTable, _logger, ct).ConfigureAwait(false);
    }

    public async Task<RelationWriteResult> CreateRelationAsync(TableRelation relation, CancellationToken ct = default)
    {
        // Rule check before any connection is opened (SEC-REL151-03).
        RelationWriteSupport.ValidateKeysOrThrow(relation);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        // One connection, one explicit transaction; disposing it uncommitted (failure or cancellation) rolls back all rows (SEC-REL151-06).
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
            cmd.CommandText = CatalogSql.InsertRelationSql;
            cmd.AddBound("@id", relation.Id.ToString());
            cmd.AddBound("@pId", parentTableId);
            cmd.AddBound("@cId", childTableId);
            cmd.AddBound("@relName", relation.RelationName);
            cmd.AddBound("@card", relation.Cardinality.ToString());
            cmd.AddBound("@allowCross", relation.AllowCrossDataSource);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            storedId = reader.GetString(0);
            created = reader.GetBoolean(1);
        }

        var keysChanged = await CatalogSqlCore.ReplaceRelationKeysAsync(conn, tx, storedId, created, parentKeys, childKeys, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        var newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 1, 0, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
        return new RelationWriteResult(Guid.Parse(storedId), created, keysChanged);
    }

    /// <summary>Resolves an active table id from its identifier: zero or several matches throw (SEC-REL151-07, no LIMIT 1).</summary>
    private static Task<string> ResolveActiveTableIdAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string relationName, TableIdentifier table, CancellationToken ct)
        => CatalogSqlCore.ResolveActiveTableIdAsync(conn, tx, CatalogSql.ResolveActiveTableIdSql, relationName, table, ct);

    private static Task<List<string>> LoadColumnNamesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string tableId, CancellationToken ct)
        => CatalogSqlCore.LoadColumnNamesAsync(conn, tx, tableId, ct);

    public async Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CatalogSqlCore.GetDataOwnersForTableAsync(conn, CatalogSql.DataOwnersForTableSql, table, ct).ConfigureAwait(false);
    }

    public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default) =>
        IsAuthorizedApproverForTableInternalAsync(table, approverSid, null, ct);

    public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, string? itsmApproverAccount, CancellationToken ct = default) =>
        IsAuthorizedApproverForTableInternalAsync(table, approverSid, itsmApproverAccount, ct);

    /// <summary>Review R4-4 (rest): resolves SID, account or e-mail spellings to stable DATA_OWNERS ids.</summary>
    private async Task<IReadOnlyCollection<string>> ResolveDataOwnerIdsInternalAsync(IEnumerable<string> identifiers, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CatalogSqlCore.ResolveDataOwnerIdsInternalAsync(conn, CatalogSql.ResolveDataOwnerIdsSql, identifiers, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyCollection<string>> ResolveRequesterOwnerIdsInternalAsync(ConsentRequest req, CancellationToken ct)
    {
        var identifiers = ConsentApprovalPolicy.IdentifierCandidates(req.RequesterSid.Value).ToList();
        if (req.RequesterIdentifiers != null)
        {
            identifiers.AddRange(req.RequesterIdentifiers);
        }

        return await ResolveDataOwnerIdsInternalAsync(identifiers, ct).ConfigureAwait(false);
    }

    private async Task<bool> IsAuthorizedApproverForTableInternalAsync(TableIdentifier table, Sid approverSid, string? itsmApproverAccount, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await IsAuthorizedApproverForTableInternalAsync(conn, null, table, approverSid, itsmApproverAccount, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<string>> GetTransitiveRolesAsync(Sid subjectSid, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CatalogSqlCore.GetTransitiveRolesAsync(conn, subjectSid, ct).ConfigureAwait(false);
    }

    public async Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation delegation, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = CatalogSql.InsertDelegationSql;
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
            var updated = await CatalogSqlCore.UpdateRelationCrossDataSourceAsync(conn, tx, relationId, allowCrossDataSource, ct).ConfigureAwait(false);
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
        return await CatalogSqlCore.GetCatalogVersionAsync(conn, CatalogSql.CatalogVersionSql, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CatalogSqlCore.GetAllRelationsAsync(conn, CatalogSql.AllRelationsSql, _logger, ct).ConfigureAwait(false);
    }

    private async Task<Autheris.Application.Catalog.CatalogVersion> BumpCatalogVersionInternalAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long structureDelta,
        long contentDelta,
        CancellationToken ct)
    {
        CatalogSqlCore.ValidateVersionDeltas(structureDelta, contentDelta);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = CatalogSql.BumpCatalogVersionSql;
        cmd.AddBound("@structureDelta", structureDelta);
        cmd.AddBound("@contentDelta", contentDelta);
        cmd.Parameters.Add(new NpgsqlParameter("now", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = DateTimeOffset.UtcNow });

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new Autheris.Application.Catalog.CatalogVersion(reader.GetInt64(0), reader.GetInt64(1));
        }
        return new Autheris.Application.Catalog.CatalogVersion(1, 1);
    }

    private Task PublishCatalogChangedEventAsync(Autheris.Application.Catalog.CatalogVersion version, CancellationToken ct)
        => CatalogSqlCore.PublishCatalogChangedEventAsync(_eventBus, _logger, version, DateTimeOffset.UtcNow, ct);
}
