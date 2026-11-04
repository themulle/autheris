using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Autheris.Infrastructure.Persistence.Catalog;

namespace Autheris.Infrastructure.Persistence;

public partial class SqliteGovernanceRepository
{
    private static class CatalogSql
    {
        public const string SelectTableEntitySql = @"SELECT id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, data_source_type, http_endpoint_json, plugin_name, description, long_description, doc_source, required_filter_columns_json, texts_json, min_group_size, non_personal
FROM TABLES
WHERE source_name = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE";

        public const string SelectTableForUpdateSql = @"SELECT id, source_type, sensitivity, requires_four_eyes, is_active, data_source_type, description, long_description, doc_source FROM TABLES
WHERE source_name = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE";

        public const string SelectActiveTableEntitiesSql = @"SELECT t.id, t.source_type, t.source_name, t.schema_name, t.table_name,
       t.display_name, t.sensitivity, t.requires_four_eyes, t.is_active, t.data_source_type, t.http_endpoint_json, t.plugin_name,
       t.description, t.long_description, t.doc_source, t.required_filter_columns_json, t.texts_json, t.min_group_size, t.non_personal
FROM TABLES t
WHERE t.is_active = 1";

        public const string SelectTableColumnsSql = @"SELECT c.id, c.table_id, c.column_name, c.data_type, c.is_sensitive,
       m.id, m.rule_type, m.pattern_or_format, m.replacement, m.hmac_key_id,
       c.description, c.long_description, c.meta_json, c.doc_source,
       c.display_name, c.texts_json
FROM TABLE_COLUMNS c
LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
WHERE c.table_id = @tableId
ORDER BY c.rowid"; // registration order, equal to the list read (a natural-key index scan would sort by name)

        public const string SelectActiveBatchTableColumnsSql = @"SELECT c.id, c.table_id, c.column_name, c.data_type, c.is_sensitive,
       m.id, m.rule_type, m.pattern_or_format, m.replacement, m.hmac_key_id,
       c.description, c.long_description, c.meta_json, c.doc_source,
       c.display_name, c.texts_json
FROM TABLE_COLUMNS c
LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
ORDER BY c.rowid";

        public const string UpsertTableSql = @"INSERT INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, data_source_type, http_endpoint_json, plugin_name, description, long_description, doc_source, required_filter_columns_json, texts_json, min_group_size, non_personal)
VALUES (@id, @sourceType, @domain, @schema, @tableName, @displayName, @sensitivity, @fourEyes, @active, @dst, @httpJson, @plugin, @desc, @longDesc, @docSource, @requiredFilterColumnsJson, @textsJson, @minGroupSize, @nonPersonal)
ON CONFLICT (id) DO UPDATE SET
    source_type = excluded.source_type,
    source_name = excluded.source_name,
    schema_name = excluded.schema_name,
    table_name = excluded.table_name,
    display_name = excluded.display_name,
    sensitivity = excluded.sensitivity,
    requires_four_eyes = excluded.requires_four_eyes,
    is_active = excluded.is_active,
    data_source_type = excluded.data_source_type,
    http_endpoint_json = excluded.http_endpoint_json,
    plugin_name = excluded.plugin_name,
    description = excluded.description,
    long_description = excluded.long_description,
    doc_source = excluded.doc_source,
    required_filter_columns_json = excluded.required_filter_columns_json,
    texts_json = excluded.texts_json,
    min_group_size = excluded.min_group_size,
    non_personal = excluded.non_personal";

        public const string InsertColumnSql = @"INSERT INTO TABLE_COLUMNS (id, table_id, column_name, data_type, is_sensitive, description, long_description, doc_source, meta_json, display_name, texts_json)
VALUES (@id, @tableId, @columnName, @dataType, @isSensitive, @description, @longDescription, @docSource, @metaJson, @displayName, @textsJson)
ON CONFLICT (id) DO UPDATE SET
    data_type = excluded.data_type,
    is_sensitive = excluded.is_sensitive,
    description = excluded.description,
    long_description = excluded.long_description,
    doc_source = excluded.doc_source,
    meta_json = COALESCE(excluded.meta_json, TABLE_COLUMNS.meta_json),
    display_name = excluded.display_name,
    texts_json = excluded.texts_json";

        public const string ActiveTableIdentifierSql = @"SELECT source_name, schema_name, table_name FROM TABLES
WHERE source_name = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE AND is_active = 1";

        public const string TableEpochSql = @"SELECT epoch FROM POLICY_EPOCHS
WHERE domain = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE";

        public const string RelationsForParentTableSql = @"SELECT r.id, r.parent_table_id, r.child_table_id, r.relation_name, r.cardinality,
       COALESCE(tp.source_name, 'default'), tp.schema_name, tp.table_name,
       COALESCE(tc.source_name, 'default'), tc.schema_name, tc.table_name,
       k.key_ordinal, k.parent_column, k.child_column,
       COALESCE(r.allow_cross_data_source, 0)
FROM TABLE_RELATIONS r
JOIN TABLES tp ON r.parent_table_id = tp.id
JOIN TABLES tc ON r.child_table_id = tc.id
LEFT JOIN TABLE_RELATION_KEYS k ON k.relation_id = r.id
WHERE tp.source_name = @pDomain COLLATE NOCASE AND tp.schema_name = @pSchema COLLATE NOCASE AND tp.table_name = @pTable COLLATE NOCASE
ORDER BY r.id, k.key_ordinal";

        public const string AllRelationsSql = @"SELECT r.id, r.parent_table_id, r.child_table_id, r.relation_name, r.cardinality,
       COALESCE(tp.source_name, 'default'), tp.schema_name, tp.table_name,
       COALESCE(tc.source_name, 'default'), tc.schema_name, tc.table_name,
       k.key_ordinal, k.parent_column, k.child_column,
       COALESCE(r.allow_cross_data_source, 0)
FROM TABLE_RELATIONS r
JOIN TABLES tp ON r.parent_table_id = tp.id
JOIN TABLES tc ON r.child_table_id = tc.id
LEFT JOIN TABLE_RELATION_KEYS k ON k.relation_id = r.id
ORDER BY r.id, k.key_ordinal";

        public const string CheckRelationExistsSql = "SELECT id FROM TABLE_RELATIONS WHERE parent_table_id = @pId AND child_table_id = @cId AND relation_name = @relName";

        public const string InsertRelationSql = @"INSERT INTO TABLE_RELATIONS (id, parent_table_id, child_table_id, relation_name, cardinality, allow_cross_data_source)
VALUES (@id, @pId, @cId, @relName, @card, @allowCross)
ON CONFLICT (parent_table_id, child_table_id, relation_name) DO UPDATE SET cardinality = excluded.cardinality
RETURNING id";

        public const string ResolveActiveTableIdSql = @"SELECT id FROM TABLES
WHERE source_name = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE AND is_active = 1";

        public const string DataOwnersForTableSql = @"SELECT o.id, o.ad_sid, o.ad_account, o.display_name, o.email, o.is_active
FROM DATA_OWNERS o
JOIN TABLE_OWNERS tow ON o.id = tow.data_owner_id
JOIN TABLES t ON tow.table_id = t.id
WHERE t.source_name = @domain COLLATE NOCASE AND t.schema_name = @schema COLLATE NOCASE AND t.table_name = @table COLLATE NOCASE";

        public const string ResolveDataOwnerIdsSql = @"SELECT id FROM DATA_OWNERS
WHERE ad_sid = @p COLLATE NOCASE OR ad_account = @p COLLATE NOCASE OR email = @p COLLATE NOCASE";

        public const string InsertDelegationSql = @"INSERT INTO DATA_OWNER_DELEGATIONS (id, data_owner_id, delegate_sid, valid_from, valid_to, reason)
VALUES (@id, @ownerId, @delSid, @from, @to, @reason)";

        public const string CatalogVersionSql = "SELECT structure_version, content_version FROM CATALOG_STATE WHERE id = 1;";

        public const string BumpCatalogVersionSql = @"UPDATE CATALOG_STATE
SET structure_version = structure_version + @structureDelta,
    content_version = content_version + @contentDelta,
    updated_at = @now
WHERE id = 1
RETURNING structure_version, content_version;";
    }

    private bool ShouldOverwriteDocumentation(string? existingSource, string? newSource)
    {
        if (string.IsNullOrWhiteSpace(existingSource))
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(newSource))
        {
            return false;
        }

        var precedence = _options?.Catalog?.DocumentationSourcePrecedence ??
            new List<string> { "Manual", "DataCatalog", "dbt", "OpenApi", "Database", "Default" };

        int existingRank = precedence.FindIndex(s => string.Equals(s, existingSource, StringComparison.OrdinalIgnoreCase));
        int newRank = precedence.FindIndex(s => string.Equals(s, newSource, StringComparison.OrdinalIgnoreCase));

        if (existingRank < 0) existingRank = int.MaxValue;
        if (newRank < 0) newRank = int.MaxValue;

        return newRank <= existingRank;
    }

    internal static IReadOnlyDictionary<string, string> ParseColumnMetaJson(string? metaJson)
        => CatalogSqlCore.ParseColumnMetaJson(metaJson);

    internal static bool HasMaskingRulesChanged(
        IReadOnlyDictionary<string, MaskingRule> existing,
        IReadOnlyDictionary<string, MaskingRule>? incoming)
        => CatalogSqlCore.HasMaskingRulesChanged(existing, incoming);

    public async Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var structural = await GetStructuralTableMetadataAsync(table, ct).ConfigureAwait(false);
        if (structural is null)
        {
            return null;
        }

        // Issue #111 (A-6): tier and tags are never part of the cached entry; a fresh read is overlaid on every call, without mutating the cached record.
        await using var scope = await ReadScopeAsync(ct, snapshot: false).ConfigureAwait(false);
        var overlay = await DatasetCatalogStore.ReadOverlayAsync(
            scope.Connection, null, CatalogKeyDefinitionStore.Sqlite, structural.Table.Id, _logger, ct).ConfigureAwait(false);
        return DatasetCatalogStore.Apply(structural, overlay);
    }

    private async Task<TableMetadata?> GetStructuralTableMetadataAsync(TableIdentifier table, CancellationToken ct)
    {
        var cacheKey = table.ToString().ToLowerInvariant();
        var nowTicks = DateTime.UtcNow.Ticks;
        if (_metadataCache.TryGetValue(cacheKey, out var entry) && (nowTicks - entry.CachedAtTicks < MetadataCacheTtlTicks))
        {
            return entry.Metadata;
        }

        // Captured before the snapshot is taken: a commit that invalidates the cache after this point makes this read stale.
        var cacheGeneration = Volatile.Read(ref _metadataCacheGeneration);
        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            nowTicks = DateTime.UtcNow.Ticks;
            if (_metadataCache.TryGetValue(cacheKey, out entry) && (nowTicks - entry.CachedAtTicks < MetadataCacheTtlTicks))
            {
                return entry.Metadata;
            }

            Guid? tableId = null;
            Table? tableEntity = null;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = CatalogSql.SelectTableEntitySql;
                cmd.AddBound("@domain", table.Domain);
                cmd.AddBound("@schema", table.Schema);
                cmd.AddBound("@table", table.TableName);

                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    tableEntity = CatalogSqlCore.ReadTableEntity(reader, _logger);
                    tableId = tableEntity.Id;
                }
            }

            if (tableEntity == null || !tableId.HasValue) return null;

            List<TableColumn> columns;
            Dictionary<string, MaskingRule> maskingRules;

            using (var cmd = conn.CreateCommand())
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
                    conn, null, CatalogKeyDefinitionStore.Sqlite, table, tableId.Value, columns, ct, _keyTables, null).ConfigureAwait(false);
            }
            catch (TableMetadataUnavailableException)
            {
                _logger?.LogWarning("Table metadata is unavailable: a declared key or index definition is invalid (the table is named by the rethrown TableMetadataUnavailableException).");
                throw;
            }

            var physicalIndexes = await CatalogPhysicalIndexStore.ReadAsync(
                conn, null, CatalogKeyDefinitionStore.Sqlite, tableId.Value, columns, ct).ConfigureAwait(false);

            var rowFilterEvaluation = await RowFilterEvaluationStore.ReadObjectAsync(
                conn, null, CatalogKeyDefinitionStore.Sqlite, tableId.Value, ct).ConfigureAwait(false);

            var result = new TableMetadata
            {
                Table = tableEntity,
                Identifier = table,
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

            // A reader whose snapshot predates a commit must not re-insert the stale metadata after the invalidation.
            if (Volatile.Read(ref _metadataCacheGeneration) == cacheGeneration)
            {
                _metadataCache[cacheKey] = (result, DateTime.UtcNow.Ticks);
            }

            return result;
        }
    }

    public async Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            List<(Guid Id, TableIdentifier Identifier, Table Table)> activeTables;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = CatalogSql.SelectActiveTableEntitiesSql;
                activeTables = await CatalogSqlCore.LoadActiveTablesAsync(cmd, _logger, ct).ConfigureAwait(false);
            }

            Dictionary<Guid, List<TableColumn>> columnsByTable;
            Dictionary<Guid, Dictionary<string, MaskingRule>> maskingRulesByTable;
            HashSet<Guid> corruptedTables;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = CatalogSql.SelectActiveBatchTableColumnsSql;
                (columnsByTable, maskingRulesByTable, corruptedTables) = await CatalogSqlCore.LoadBatchTableColumnsAsync(cmd, _logger, ct).ConfigureAwait(false);
            }

            Dictionary<Guid, CatalogKeyDefinitionStore.TableKeyRows>? prefetched = null;
            Dictionary<Guid, List<CatalogPhysicalIndexStore.Row>>? prefetchedPhysical = null;
            var allActiveTableIds = activeTables.Select(t => t.Id).ToList();
            if (await CatalogKeyDefinitionStore.TablesExistAsync(conn, null, CatalogKeyDefinitionStore.Sqlite, ct, _keyTables).ConfigureAwait(false))
            {
                prefetched = await CatalogKeyDefinitionStore.ReadBatchAsync(conn, null, CatalogKeyDefinitionStore.Sqlite, allActiveTableIds, ct).ConfigureAwait(false);
                prefetchedPhysical = await CatalogPhysicalIndexStore.ReadBatchAsync(conn, null, CatalogKeyDefinitionStore.Sqlite, allActiveTableIds, ct).ConfigureAwait(false);
            }

            var results = new List<TableMetadata>(activeTables.Count);
            foreach (var (id, identifier, table) in activeTables)
            {
                if (corruptedTables.Contains(id))
                {
                    _logger?.LogWarning("Catalog table {Table} was omitted: its column or masking rule definition is invalid (fail-closed).", identifier);
                    continue;
                }

                var columns = columnsByTable.TryGetValue(id, out var cols) ? cols : new List<TableColumn>();
                var maskingRules = maskingRulesByTable.TryGetValue(id, out var rules)
                    ? rules
                    : new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);

                IReadOnlyList<string> pkColumns;
                IReadOnlyList<KeyColumnDirection> pkDirections;
                IReadOnlyList<TableIndexDefinition> indexes;
                try
                {
                    (pkColumns, pkDirections, indexes) = await CatalogKeyDefinitionStore.ReadAsync(
                        conn, null, CatalogKeyDefinitionStore.Sqlite, identifier, id, columns, ct, _keyTables,
                        prefetched is not null && prefetched.TryGetValue(id, out var keyRows) ? keyRows : null).ConfigureAwait(false);
                }
                catch (TableMetadataUnavailableException)
                {
                    _logger?.LogWarning("Catalog table {Table} was omitted: its declared key or index definition is invalid.", identifier);
                    continue;
                }

                var meta = new TableMetadata
                {
                    Table = table,
                    Identifier = identifier,
                    Columns = columns,
                    ColumnMaskingRules = maskingRules,
                    PrimaryKeyColumns = pkColumns,
                    PrimaryKeyDirections = pkDirections,
                    Indexes = indexes,
                    PhysicalIndexes = prefetchedPhysical is not null && prefetchedPhysical.TryGetValue(id, out var physicalRows) ? CatalogPhysicalIndexStore.Assemble(columns, physicalRows) : Array.Empty<PhysicalIndexDefinition>()
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

                results.Add(meta);
            }

            // Issue #111: tier and tags are read fresh inside the same snapshot (two set-based queries) and degrade to Hidden per row.
            var overlay = await DatasetCatalogStore.ReadOverlayAsync(conn, null, CatalogKeyDefinitionStore.Sqlite, null, _logger, ct).ConfigureAwait(false);
            for (var i = 0; i < results.Count; i++)
            {
                results[i] = DatasetCatalogStore.Apply(results[i], overlay);
            }

            return results;
        }
    }

    public async Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
    {
        metadata.ValidateRequiredFilterColumns();
        Autheris.Application.Dbt.Services.SemanticVirtualTables.EnsureNamespaceRespected(metadata);
        if (metadata.ReplaceKeyDefinition)
        {
            metadata = TableKeyDefinitionValidator.Validate(metadata);
        }

        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        var tableId = metadata.Table.Id == Guid.Empty ? Guid.NewGuid() : metadata.Table.Id;
        var tableExisted = false;
        var structuralChange = false;
        var targetDesc = metadata.Table.Description;
        var targetLongDesc = metadata.Table.LongDescription;
        var targetDocSource = metadata.Table.DocumentationSource;
        IReadOnlyList<string> persistedPks;
        IReadOnlyList<KeyColumnDirection> persistedPkDirections;
        IReadOnlyList<TableIndexDefinition> persistedIndexes;

        using var tx = conn.BeginTransaction();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = CatalogSql.SelectTableForUpdateSql;
                cmd.AddBound("@domain", metadata.Identifier.Domain);
                cmd.AddBound("@schema", metadata.Identifier.Schema);
                cmd.AddBound("@table", metadata.Identifier.TableName);

                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    tableId = Guid.Parse(reader.GetString(0));
                    tableExisted = true;
                    var existingSourceType = reader.GetString(1);
                    var existingSensitivity = reader.GetString(2);
                    var existingFourEyes = reader.GetInt32(3) == 1;
                    var existingActive = reader.GetInt32(4) == 1;
                    var existingDst = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
                    var existingDesc = reader.IsDBNull(6) ? null : reader.GetString(6);
                    var existingLongDesc = reader.IsDBNull(7) ? null : reader.GetString(7);
                    var existingDocSource = reader.IsDBNull(8) ? null : reader.GetString(8);

                    if (!string.Equals(existingSourceType, metadata.Table.SourceType, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(existingSensitivity, metadata.Table.Sensitivity, StringComparison.OrdinalIgnoreCase) ||
                        existingFourEyes != metadata.Table.RequiresFourEyes ||
                        existingActive != metadata.Table.IsActive ||
                        existingDst != (int)metadata.Table.DataSourceType)
                    {
                        structuralChange = true;
                    }

                    bool canOverwriteDesc = string.IsNullOrWhiteSpace(existingDesc) ||
                                            (!string.IsNullOrWhiteSpace(metadata.Table.Description) &&
                                             ShouldOverwriteDocumentation(existingDocSource, metadata.Table.DocumentationSource));

                    bool canOverwriteLongDesc = string.IsNullOrWhiteSpace(existingLongDesc) ||
                                                (!string.IsNullOrWhiteSpace(metadata.Table.LongDescription) &&
                                                 ShouldOverwriteDocumentation(existingDocSource, metadata.Table.DocumentationSource));

                    targetDesc = canOverwriteDesc ? metadata.Table.Description : existingDesc;
                    targetLongDesc = canOverwriteLongDesc ? metadata.Table.LongDescription : existingLongDesc;
                    targetDocSource = canOverwriteDesc ? (metadata.Table.DocumentationSource ?? existingDocSource) : existingDocSource;
                }
                else
                {
                    structuralChange = true;
                }
            }

            var httpEndpointJson = metadata.Table.HttpEndpoint != null
                ? JsonSerializer.Serialize(metadata.Table.HttpEndpoint)
                : null;

            using (var upsertCmd = conn.CreateCommand())
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
                upsertCmd.AddBound("@desc", (object?)targetDesc ?? DBNull.Value);
                upsertCmd.AddBound("@longDesc", (object?)targetLongDesc ?? DBNull.Value);
                upsertCmd.AddBound("@docSource", (object?)targetDocSource ?? DBNull.Value);
                upsertCmd.AddBound("@requiredFilterColumnsJson", metadata.RequiredFilterColumns.IsDefaultOrEmpty ? (object)DBNull.Value : JsonSerializer.Serialize(metadata.RequiredFilterColumns));
                upsertCmd.AddBound("@textsJson", (object?)CatalogLocalizationPersistence.SerializeLocalizedTexts(metadata.Table.Texts) ?? DBNull.Value);
                upsertCmd.AddBound("@minGroupSize", (object?)metadata.Table.MinGroupSize ?? DBNull.Value);
                upsertCmd.AddBound("@nonPersonal", metadata.Table.NonPersonal ? 1 : 0);
                await upsertCmd.ExecuteNonQueryAsync(ct);
            }

            // Issue #111 (A-10): the configured default tier is given to a NEW table only; the Hidden default writes no row.
            if (!tableExisted && _options?.Catalog?.DefaultVisibility == DatasetVisibility.Summary)
            {
                await DatasetCatalogStore.InsertDefaultAsync(conn, tx, CatalogKeyDefinitionStore.Sqlite, tableId, DatasetVisibility.Summary, ct).ConfigureAwait(false);
            }

            var (existingColsByName, existingMaskingRules, colIdByName) = await CatalogSqlCore.LoadExistingColumnsAndMaskingRulesAsync(conn, tx, tableId, ct).ConfigureAwait(false);
            if (CatalogSqlCore.HasMaskingRulesChanged(existingMaskingRules, metadata.ColumnMaskingRules))
            {
                structuralChange = true;
            }

            if (await CatalogSqlCore.UpsertColumnsAndMaskingRulesAsync(conn, tx, CatalogSql.InsertColumnSql, tableId, metadata, existingColsByName, colIdByName, ct, ShouldOverwriteDocumentation).ConfigureAwait(false))
            {
                structuralChange = true;
            }

            if (metadata.ReplaceKeyDefinition)
            {
                structuralChange = true;
                await CatalogKeyDefinitionStore.ReplaceAsync(conn, tx, CatalogKeyDefinitionStore.Sqlite, tableId, metadata, colIdByName, ct).ConfigureAwait(false);
                persistedPks = metadata.PrimaryKeyColumns;
                persistedPkDirections = metadata.PrimaryKeyDirections;
                persistedIndexes = metadata.Indexes;
            }
            else
            {
                try
                {
                    (persistedPks, persistedPkDirections, persistedIndexes) = await CatalogKeyDefinitionStore.ReadAsync(
                        conn, tx, CatalogKeyDefinitionStore.Sqlite, metadata.Identifier, tableId, metadata.Columns, ct).ConfigureAwait(false);
                }
                catch (TableMetadataUnavailableException)
                {
                    persistedPks = metadata.PrimaryKeyColumns;
                    persistedPkDirections = metadata.PrimaryKeyDirections;
                    persistedIndexes = metadata.Indexes;
                }
            }

            await IncrementTableEpochInternalAsync(conn, metadata.Identifier, tx, ct);
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

        QueueMetadataInvalidation(metadata.Identifier);
        await _epochValidationService.InvalidateEpochAsync(metadata.Identifier, ct).ConfigureAwait(false);

        var updatedTable = new Table
        {
            Id = tableId,
            SourceType = metadata.Table.SourceType,
            SourceName = metadata.Identifier.Domain,
            SchemaName = metadata.Identifier.Schema,
            TableName = metadata.Identifier.TableName,
            DisplayName = metadata.Table.DisplayName,
            Sensitivity = metadata.Table.Sensitivity,
            RequiresFourEyes = metadata.Table.RequiresFourEyes,
            IsActive = metadata.Table.IsActive,
            DataSourceType = metadata.Table.DataSourceType,
            HttpEndpoint = metadata.Table.HttpEndpoint,
            PluginName = metadata.Table.PluginName,
            Description = targetDesc,
            LongDescription = targetLongDesc,
            Texts = metadata.Table.Texts,
            DocumentationSource = targetDocSource
        };

        return new TableMetadata
        {
            Identifier = metadata.Identifier,
            Table = updatedTable,
            Columns = metadata.Columns,
            ColumnMaskingRules = metadata.ColumnMaskingRules,
            PrimaryKeyColumns = persistedPks,
            PrimaryKeyDirections = persistedPkDirections,
            Indexes = persistedIndexes
        };
    }

    public async Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
        TableIdentifier table,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<TableIndexDefinition> indexes,
        IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
        CancellationToken ct = default)
    {
        TableKeyDefinitionChange? change;
        await using (var scope = await WriteScopeAsync(ct).ConfigureAwait(false))
        {
            var conn = scope.Connection;
            using var tx = conn.BeginTransaction();
            try
            {
                change = await CatalogKeyDefinitionStore.ReplaceNarrowAsync(
                    conn, tx, CatalogKeyDefinitionStore.Sqlite, table, primaryKey, indexes, primaryKeyDirections,
                    () => BumpTableEpochAndReadAsync(conn, tx, table, ct), ct).ConfigureAwait(false);
                if (change is null)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return null;
                }

                var newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 1, 0, ct).ConfigureAwait(false);
                QueueMetadataInvalidation(table);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        await _epochValidationService.InvalidateEpochAsync(table, ct).ConfigureAwait(false);
        return change;
    }

    public async Task<RowFilterEvaluationChange?> SetRowFilterEvaluationAsync(
        TableIdentifier table, RowFilterEvaluationSettings? settings, Sid actor, CancellationToken ct = default)
    {
        RowFilterEvaluationChange? change;
        await using (var scope = await WriteScopeAsync(ct).ConfigureAwait(false))
        {
            var conn = scope.Connection;
            using var tx = conn.BeginTransaction();
            try
            {
                change = await RowFilterEvaluationStore.SetObjectAsync(
                    conn, tx, CatalogKeyDefinitionStore.Sqlite, table, settings, actor,
                    () => BumpTableEpochAndReadAsync(conn, tx, table, ct), ct).ConfigureAwait(false);
                if (change is null)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return null;
                }

                var newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 1, 0, ct).ConfigureAwait(false);
                QueueMetadataInvalidation(table);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        await _epochValidationService.InvalidateEpochAsync(table, ct).ConfigureAwait(false);
        return change;
    }

    public async Task<DatasetVisibilityCounts> GetDatasetVisibilityCountsAsync(CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        return await DatasetCatalogStore.CountByVisibilityAsync(scope.Connection, null, CatalogKeyDefinitionStore.Sqlite, ct).ConfigureAwait(false);
    }

    public async Task<DatasetCatalogSettings?> GetDatasetCatalogSettingsAsync(TableIdentifier table, bool strict = true, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        return await DatasetCatalogStore.ReadSettingsAsync(
            scope.Connection, null, CatalogKeyDefinitionStore.Sqlite, table, strict, _logger, ct).ConfigureAwait(false);
    }

    public Task<DatasetCatalogChange?> ReplaceDatasetTagsAsync(
        TableIdentifier table, IReadOnlyList<string> tags, DatasetTagSource source, Sid actor, CancellationToken ct = default) =>
        WriteCatalogSettingsAsync(table, (conn, tx) =>
            DatasetCatalogStore.ReplaceTagsAsync(conn, tx, CatalogKeyDefinitionStore.Sqlite, table, tags, source, actor, ct), ct);

    public Task<DatasetCatalogChange?> ChangeDatasetTagsAsync(
        TableIdentifier table, IReadOnlyList<string> add, IReadOnlyList<string> remove, Sid actor, CancellationToken ct = default) =>
        WriteCatalogSettingsAsync(table, (conn, tx) =>
            DatasetCatalogStore.ChangeTagsAsync(conn, tx, CatalogKeyDefinitionStore.Sqlite, table, add, remove, actor, ct), ct);

    public Task<DatasetCatalogChange?> SetDatasetVisibilityAsync(TableIdentifier table, DatasetVisibility visibility, Sid actor, CancellationToken ct = default) =>
        WriteCatalogSettingsAsync(table, (conn, tx) => DatasetCatalogStore.SetVisibilityAsync(conn, tx, CatalogKeyDefinitionStore.Sqlite, table, visibility, actor, ct), ct);

    public Task<DatasetCatalogChange?> SetDatasetPrivacyAsync(TableIdentifier table, int? minGroupSize, bool? nonPersonal, Sid actor, CancellationToken ct = default) =>
        WriteCatalogSettingsAsync(table, (conn, tx) => DatasetCatalogStore.SetPrivacyAsync(conn, tx, CatalogKeyDefinitionStore.Sqlite, table, minGroupSize, nonPersonal, actor, ct), ct);

    /// <summary>Issue #111: one transaction (locked before read, write, after read); no epoch; the local metadata entry is dropped after the commit.</summary>
    private async Task<DatasetCatalogChange?> WriteCatalogSettingsAsync(
        TableIdentifier table, Func<SqliteConnection, SqliteTransaction, Task<DatasetCatalogChange?>> write, CancellationToken ct)
    {
        DatasetCatalogChange? change;
        await using (var scope = await WriteScopeAsync(ct).ConfigureAwait(false))
        {
            var conn = scope.Connection;
            using var tx = conn.BeginTransaction();
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

                if (change.Changed)
                {
                    QueueMetadataInvalidation(table);
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


        return change;
    }

    public async Task<TableIdentifier?> GetActiveTableIdentifierAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct, snapshot: false).ConfigureAwait(false);
        return await CatalogSqlCore.GetActiveTableIdentifierAsync(scope.Connection, CatalogSql.ActiveTableIdentifierSql, table, ct).ConfigureAwait(false);
    }

    public async Task<TableIdentifier?> GetActiveTableIdentifierByIdAsync(Guid tableId, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct, snapshot: false).ConfigureAwait(false);
        return await CatalogSqlCore.GetActiveTableIdentifierByIdAsync(scope.Connection, tableId, ct).ConfigureAwait(false);
    }

    public async Task<long> GetTableEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        return await CatalogSqlCore.GetTableEpochAsync(scope.Connection, CatalogSql.TableEpochSql, table, ct).ConfigureAwait(false);
    }

    public async Task<long> IncrementTableEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            long newEpoch;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"UPDATE POLICY_EPOCHS
                                    SET epoch = epoch + 1, updated_at = @now
                                    WHERE domain = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE
                                    RETURNING epoch;";
                cmd.AddBound("@domain", table.Domain);
                cmd.AddBound("@schema", table.Schema);
                cmd.AddBound("@table", table.TableName);
                cmd.AddBound("@now", DateTimeOffset.UtcNow.ToString("O"));

                var result = await cmd.ExecuteScalarAsync(ct);
                newEpoch = result is long l ? l : (result is int i ? i : 2L);
            }

            QueueMetadataInvalidation(table);
            await _epochValidationService.InvalidateEpochAsync(table, ct);
            return newEpoch;
        }
    }

    private Task IncrementTableEpochInternalAsync(SqliteConnection conn, TableIdentifier table, CancellationToken ct) =>
        IncrementTableEpochInternalAsync(conn, table, null, ct);

    private async Task IncrementTableEpochInternalAsync(SqliteConnection conn, TableIdentifier table, SqliteTransaction? transaction, CancellationToken ct)
    {
        QueueMetadataInvalidation(table);
        using (var cmd = conn.CreateCommand())
        {
            if (transaction != null)
            {
                cmd.Transaction = transaction;
            }
            cmd.CommandText = @"UPDATE POLICY_EPOCHS
                                SET epoch = epoch + 1, updated_at = @now
                                WHERE domain = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE";
            cmd.AddBound("@domain", table.Domain);
            cmd.AddBound("@schema", table.Schema);
            cmd.AddBound("@table", table.TableName);
            cmd.AddBound("@now", DateTimeOffset.UtcNow.ToString("O"));
            int rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
            if (rowsAffected == 0)
            {
                using var insertCmd = conn.CreateCommand();
                if (transaction != null)
                {
                    insertCmd.Transaction = transaction;
                }
                insertCmd.CommandText = @"INSERT INTO POLICY_EPOCHS (table_id, domain, schema_name, table_name, epoch, updated_at)
                                          SELECT id, @domain, schema_name, table_name, 2, @now
                                          FROM TABLES
                                          WHERE source_name = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE";
                insertCmd.AddBound("@domain", table.Domain);
                insertCmd.AddBound("@schema", table.Schema);
                insertCmd.AddBound("@table", table.TableName);
                insertCmd.AddBound("@now", DateTimeOffset.UtcNow.ToString("O"));
                await insertCmd.ExecuteNonQueryAsync(ct);
            }
        }

        if (transaction == null)
        {
            await _epochValidationService.InvalidateEpochAsync(table, ct);
        }
    }

    /// <summary>Bumps the policy epoch inside the caller's transaction and returns the new value (issue #107).</summary>
    private async Task<long> BumpTableEpochAndReadAsync(SqliteConnection conn, SqliteTransaction tx, TableIdentifier table, CancellationToken ct)
    {
        await IncrementTableEpochInternalAsync(conn, table, tx, ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"SELECT epoch FROM POLICY_EPOCHS
                            WHERE domain = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE";
        cmd.AddBound("@domain", table.Domain);
        cmd.AddBound("@schema", table.Schema);
        cmd.AddBound("@table", table.TableName);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long l ? l : Convert.ToInt64(result ?? 1L, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task DeletePolicyEpochForTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"DELETE FROM POLICY_EPOCHS WHERE domain = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE";
            cmd.AddBound("@domain", table.Domain);
            cmd.AddBound("@schema", table.Schema);
            cmd.AddBound("@table", table.TableName);
            await cmd.ExecuteNonQueryAsync(ct);
            QueueMetadataInvalidation(table);
        }
    }

    public async Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation delegation, CancellationToken ct = default)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        using var tx = conn.BeginTransaction();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = CatalogSql.InsertDelegationSql;
            cmd.AddBound("@id", delegation.Id.ToString());
            cmd.AddBound("@ownerId", delegation.DataOwnerId.ToString());
            cmd.AddBound("@delSid", delegation.DelegateSid.Value);
            cmd.AddBound("@from", delegation.ValidFrom.ToString("O"));
            cmd.AddBound("@to", delegation.ValidTo.ToString("O"));
            cmd.AddBound("@reason", delegation.Reason);

            await cmd.ExecuteNonQueryAsync(ct);
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

    public async Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        return await CatalogSqlCore.GetDataOwnersForTableAsync(scope.Connection, CatalogSql.DataOwnersForTableSql, table, ct).ConfigureAwait(false);
    }

    public async Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        return await IsAuthorizedApproverForTableInternalAsync(scope.Connection, table, approverSid, ct);
    }

    private Task<bool> IsAuthorizedApproverForTableInternalAsync(SqliteConnection conn, TableIdentifier table, Sid approverSid, CancellationToken ct)
        => IsAuthorizedApproverForTableInternalAsync(conn, table, approverSid, null, ct);

    /// <summary>Review R4-4 (rest): resolves SID, account or e-mail spellings to stable DATA_OWNERS ids (caller holds the lock).</summary>
    private async Task<IReadOnlyCollection<string>> ResolveDataOwnerIdsInternalAsync(SqliteConnection conn, IEnumerable<string> identifiers, CancellationToken ct)
    {
        return await CatalogSqlCore.ResolveDataOwnerIdsInternalAsync(conn, CatalogSql.ResolveDataOwnerIdsSql, identifiers, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyCollection<string>> ResolveRequesterOwnerIdsInternalAsync(SqliteConnection conn, ConsentRequest req, CancellationToken ct)
    {
        var identifiers = ConsentApprovalPolicy.IdentifierCandidates(req.RequesterSid.Value).ToList();
        if (req.RequesterIdentifiers != null)
        {
            identifiers.AddRange(req.RequesterIdentifiers);
        }

        return await ResolveDataOwnerIdsInternalAsync(conn, identifiers, ct);
    }

    private Task<bool> IsAuthorizedApproverForTableInternalAsync(SqliteConnection conn, TableIdentifier table, Sid approverSid, string? itsmApproverAccount, CancellationToken ct)
        => IsAuthorizedApproverForTableInternalAsync(conn, null, table, approverSid, itsmApproverAccount, ct);

    public async Task<IReadOnlySet<string>> GetTransitiveRolesAsync(Sid subjectSid, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        return await CatalogSqlCore.GetTransitiveRolesAsync(scope.Connection, subjectSid, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        return await CatalogSqlCore.GetRelationsForTableAsync(scope.Connection, CatalogSql.RelationsForParentTableSql, parentTable, _logger, ct).ConfigureAwait(false);
    }

    public async Task<RelationWriteResult> CreateRelationAsync(TableRelation relation, CancellationToken ct = default)
    {
        RelationWriteSupport.ValidateKeysOrThrow(relation);

        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        using var tx = conn.BeginTransaction();

        var parentTableId = await ResolveActiveTableIdAsync(conn, tx, relation.RelationName, relation.ParentTableIdentifier, ct).ConfigureAwait(false);
        var childTableId = await ResolveActiveTableIdAsync(conn, tx, relation.RelationName, relation.ChildTableIdentifier, ct).ConfigureAwait(false);
        var parentKeys = RelationWriteSupport.Canonicalize(relation.RelationName, "parent", relation.JoinKeysParent, await LoadColumnNamesAsync(conn, tx, parentTableId, ct).ConfigureAwait(false));
        var childKeys = RelationWriteSupport.Canonicalize(relation.RelationName, "child", relation.JoinKeysChild, await LoadColumnNamesAsync(conn, tx, childTableId, ct).ConfigureAwait(false));

        string storedId;
        bool created;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = CatalogSql.CheckRelationExistsSql;
            cmd.AddBound("@pId", parentTableId);
            cmd.AddBound("@cId", childTableId);
            cmd.AddBound("@relName", relation.RelationName);
            created = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is null;
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = CatalogSql.InsertRelationSql;
            cmd.AddBound("@id", relation.Id.ToString());
            cmd.AddBound("@pId", parentTableId);
            cmd.AddBound("@cId", childTableId);
            cmd.AddBound("@relName", relation.RelationName);
            cmd.AddBound("@card", relation.Cardinality.ToString());
            cmd.AddBound("@allowCross", relation.AllowCrossDataSource ? 1 : 0);
            storedId = (string)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        var keysChanged = await CatalogSqlCore.ReplaceRelationKeysAsync(conn, tx, storedId, created, parentKeys, childKeys, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        var newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 1, 0, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
        return new RelationWriteResult(Guid.Parse(storedId), created, keysChanged);
    }

    /// <summary>Resolves an active table id from its identifier: zero or several matches throw (SEC-REL151-07, no LIMIT 1).</summary>
    private static Task<string> ResolveActiveTableIdAsync(SqliteConnection conn, SqliteTransaction tx, string relationName, TableIdentifier table, CancellationToken ct)
        => CatalogSqlCore.ResolveActiveTableIdAsync(conn, tx, CatalogSql.ResolveActiveTableIdSql, relationName, table, ct);

    private static Task<List<string>> LoadColumnNamesAsync(SqliteConnection conn, SqliteTransaction tx, string tableId, CancellationToken ct)
        => CatalogSqlCore.LoadColumnNamesAsync(conn, tx, tableId, ct);

    public async Task<bool> SetRelationCrossDataSourceOptInAsync(Guid relationId, bool allowCrossDataSource, CancellationToken ct = default)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        using var tx = conn.BeginTransaction();
        try
        {
            var updated = await CatalogSqlCore.UpdateRelationCrossDataSourceAsync(conn, tx, relationId, allowCrossDataSource ? 1 : 0, ct).ConfigureAwait(false);
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
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        return await CatalogSqlCore.GetCatalogVersionAsync(scope.Connection, CatalogSql.CatalogVersionSql, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        return await CatalogSqlCore.GetAllRelationsAsync(scope.Connection, CatalogSql.AllRelationsSql, _logger, ct).ConfigureAwait(false);
    }

    private async Task<Autheris.Application.Catalog.CatalogVersion> BumpCatalogVersionInternalAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        long structureDelta,
        long contentDelta,
        CancellationToken ct)
    {
        CatalogSqlCore.ValidateVersionDeltas(structureDelta, contentDelta);

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = CatalogSql.BumpCatalogVersionSql;
        cmd.AddBound("@structureDelta", structureDelta);
        cmd.AddBound("@contentDelta", contentDelta);
        cmd.AddBound("@now", DateTimeOffset.UtcNow.ToString("O"));

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new Autheris.Application.Catalog.CatalogVersion(reader.GetInt64(0), reader.GetInt64(1));
        }
        return new Autheris.Application.Catalog.CatalogVersion(1, 1);
    }

    private Task PublishCatalogChangedEventAsync(Autheris.Application.Catalog.CatalogVersion version, CancellationToken ct)
        => CatalogSqlCore.PublishCatalogChangedEventAsync(_eventBus, _logger, version, DateTimeOffset.UtcNow, ct);
}
