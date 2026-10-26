using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;
using Autheris.Infrastructure.Persistence.Catalog;

namespace Autheris.Infrastructure.Persistence;

public partial class SqlServerGovernanceRepository
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
            conn, null, CatalogKeyDefinitionStore.SqlServer, structural.Table.Id, _logger, ct).ConfigureAwait(false);
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
                cmd.CommandText = @"SELECT id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, data_source_type, http_endpoint_json, plugin_name, description, long_description, doc_source, required_filter_columns_json, texts_json
                                    FROM TABLES
                                    WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)";
                cmd.Parameters.AddWithValue("@domain", table.Domain);
                cmd.Parameters.AddWithValue("@schema", table.Schema);
                cmd.Parameters.AddWithValue("@table", table.TableName);

                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    tableId = Guid.Parse(reader.GetString(0));
                    var dstInt = reader.IsDBNull(9) ? 0 : reader.GetInt32(9);
                    var httpEndpointJson = reader.IsDBNull(10) ? null : reader.GetString(10);
                    var pluginName = reader.IsDBNull(11) ? null : reader.GetString(11);
                    var httpEndpoint = !string.IsNullOrWhiteSpace(httpEndpointJson)
                        ? JsonSerializer.Deserialize<HttpEndpointDescriptor>(httpEndpointJson)
                        : null;
                    var reqFiltersJson = reader.IsDBNull(15) ? null : reader.GetString(15);
                    var reqFilters = string.IsNullOrWhiteSpace(reqFiltersJson)
                        ? ImmutableArray<string>.Empty
                        : JsonSerializer.Deserialize<ImmutableArray<string>>(reqFiltersJson);
                    if (reqFilters.IsDefault) reqFilters = ImmutableArray<string>.Empty;
                    var textsJson = reader.IsDBNull(16) ? null : reader.GetString(16);
                    var texts = CatalogLocalizationPersistence.ParseLocalizedTexts(textsJson, tableId.Value, _logger);

                    tableEntity = new Table
                    {
                        Id = tableId.Value,
                        SourceType = reader.GetString(1),
                        SourceName = reader.GetString(2),
                        SchemaName = reader.GetString(3),
                        TableName = reader.GetString(4),
                        DisplayName = reader.GetString(5),
                        Sensitivity = reader.GetString(6),
                        RequiresFourEyes = reader.GetInt32(7) == 1,
                        IsActive = reader.GetInt32(8) == 1,
                        DataSourceType = (DataSourceType)dstInt,
                        HttpEndpoint = httpEndpoint,
                        PluginName = pluginName,
                        Description = reader.IsDBNull(12) ? null : reader.GetString(12),
                        LongDescription = reader.IsDBNull(13) ? null : reader.GetString(13),
                        Texts = texts,
                        DocumentationSource = reader.IsDBNull(14) ? null : reader.GetString(14),
                        RequiredFilterColumns = reqFilters
                    };
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

            var columns = new List<TableColumn>();
            var maskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT c.id, c.column_name, c.data_type, c.is_sensitive,
                                           m.id, m.rule_type, m.pattern_or_format, m.replacement, m.hmac_key_id,
                                           c.description, c.long_description, c.meta_json, c.doc_source,
                                           c.display_name, c.texts_json
                                    FROM TABLE_COLUMNS c
                                    LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
                                    WHERE c.table_id = @tableId";
                cmd.Parameters.AddWithValue("@tableId", tableId.Value.ToString());

                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var colId = Guid.Parse(reader.GetString(0));
                    var colName = reader.GetString(1);
                    var dataType = reader.GetString(2);
                    var isSensitive = reader.GetInt32(3) == 1;

                    var desc = reader.IsDBNull(9) ? null : reader.GetString(9);
                    var longDesc = reader.IsDBNull(10) ? null : reader.GetString(10);
                    var metaJson = reader.IsDBNull(11) ? null : reader.GetString(11);
                    var docSource = reader.IsDBNull(12) ? null : reader.GetString(12);
                    var colDisplayName = reader.IsDBNull(13) ? null : reader.GetString(13);
                    var colTextsJson = reader.IsDBNull(14) ? null : reader.GetString(14);
                    var colTexts = CatalogLocalizationPersistence.ParseLocalizedTexts(colTextsJson, colId, _logger);

                    var metaDict = SqliteGovernanceRepository.ParseColumnMetaJson(metaJson);

                    columns.Add(new TableColumn
                    {
                        Id = colId,
                        TableId = tableId.Value,
                        ColumnName = colName,
                        DisplayName = colDisplayName,
                        DataType = dataType,
                        IsSensitive = isSensitive,
                        Description = desc,
                        LongDescription = longDesc,
                        Texts = colTexts,
                        DocumentationSource = docSource,
                        Meta = metaDict
                    });

                    if (!reader.IsDBNull(4))
                    {
                        maskingRules[colName] = new MaskingRule
                        {
                            Id = Guid.Parse(reader.GetString(4)),
                            TableColumnId = colId,
                            RuleType = reader.GetString(5),
                            PatternOrFormat = reader.IsDBNull(6) ? null : reader.GetString(6),
                            Replacement = reader.IsDBNull(7) ? null : reader.GetString(7),
                            HmacKeyId = reader.IsDBNull(8) ? null : reader.GetString(8)
                        };
                    }
                }
            }

            IReadOnlyList<string> pkColumns;
            IReadOnlyList<TableIndexDefinition> indexes;
            try
            {
                (pkColumns, indexes) = await CatalogKeyDefinitionStore.ReadAsync(
                    conn, null, CatalogKeyDefinitionStore.SqlServer, table, tableId.Value, columns, ct,
                    _keyTables, prefetchedKeys is not null && prefetchedKeys.TryGetValue(tableId.Value, out var keyRows) ? keyRows : null).ConfigureAwait(false);
            }
            catch (TableMetadataUnavailableException)
            {
                _logger?.LogWarning("Metadata of table {Table} is unavailable: its declared key or index definition is invalid.", table);
                throw;
            }

            var rowFilterEvaluation = await RowFilterEvaluationStore.ReadObjectAsync(
                conn, null, CatalogKeyDefinitionStore.SqlServer, tableId.Value, ct).ConfigureAwait(false);

            var result = new TableMetadata
            {
                Identifier = table,
                Table = tableEntity,
                Columns = columns,
                ColumnMaskingRules = maskingRules,
                PrimaryKeyColumns = pkColumns,
                Indexes = indexes,
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
    private const string EpochJoin = "POLICY_EPOCHS p JOIN TABLES t ON LOWER(p.domain) = LOWER(t.source_name) AND LOWER(p.schema_name) = LOWER(t.schema_name) AND LOWER(p.table_name) = LOWER(t.table_name)";
    internal const string EpochByTableIdSql = "SELECT MAX(p.epoch) FROM " + EpochJoin + " WHERE t.id = @tableId";
    internal const string AllEpochsSql = "SELECT t.id, MAX(p.epoch) FROM " + EpochJoin + " GROUP BY t.id";

    // PERS-17: the table epoch (POLICY_EPOCHS) authorises cache hits across replicas; -1 means the table has no epoch row yet.
    internal static async Task<long> ReadTableEpochAsync(System.Data.Common.DbConnection conn, Guid tableId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = EpochByTableIdSql;
        var p = cmd.CreateParameter();
        p.ParameterName = "@tableId";
        p.Value = tableId.ToString();
        cmd.Parameters.Add(p);
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? -1 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static async Task<Dictionary<Guid, long>> ReadAllTableEpochsAsync(System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        var epochs = new Dictionary<Guid, long>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = AllEpochsSql;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (Guid.TryParse(reader.GetString(0), out var id))
            {
                epochs[id] = Convert.ToInt64(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return epochs;
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
                cmd.CommandText = "SELECT id, source_name, schema_name, table_name FROM TABLES WHERE is_active = 1";
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

            var activeTables = new List<(Guid Id, TableIdentifier Identifier, Table Table)>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT id, source_type, source_name, schema_name, table_name,
                                           display_name, sensitivity, requires_four_eyes, is_active,
                                           data_source_type, http_endpoint_json, plugin_name,
                                           description, long_description, doc_source,
                                           required_filter_columns_json, texts_json
                                    FROM TABLES
                                    WHERE is_active = 1";
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        var tid = Guid.Parse(reader.GetString(0));
                        var domain = reader.GetString(2);
                        var schema = reader.GetString(3);
                        var tableName = reader.GetString(4);
                        var identifier = new TableIdentifier(domain, schema, tableName);

                        var dstInt = reader.IsDBNull(9) ? 0 : reader.GetInt32(9);
                        var httpEndpointJson = reader.IsDBNull(10) ? null : reader.GetString(10);
                        var pluginName = reader.IsDBNull(11) ? null : reader.GetString(11);
                        var httpEndpoint = !string.IsNullOrWhiteSpace(httpEndpointJson)
                            ? JsonSerializer.Deserialize<HttpEndpointDescriptor>(httpEndpointJson)
                            : null;
                        var reqFiltersJson = reader.IsDBNull(15) ? null : reader.GetString(15);
                        var reqFilters = string.IsNullOrWhiteSpace(reqFiltersJson)
                            ? ImmutableArray<string>.Empty
                            : JsonSerializer.Deserialize<ImmutableArray<string>>(reqFiltersJson);
                        if (reqFilters.IsDefault) reqFilters = ImmutableArray<string>.Empty;
                        var textsJson = reader.IsDBNull(16) ? null : reader.GetString(16);
                        var texts = CatalogLocalizationPersistence.ParseLocalizedTexts(textsJson, tid, _logger);

                        var tableEntity = new Table
                        {
                            Id = tid,
                            SourceType = reader.GetString(1),
                            SourceName = domain,
                            SchemaName = schema,
                            TableName = tableName,
                            DisplayName = reader.GetString(5),
                            Sensitivity = reader.GetString(6),
                            RequiresFourEyes = reader.GetInt32(7) == 1,
                            IsActive = reader.GetInt32(8) == 1,
                            DataSourceType = (DataSourceType)dstInt,
                            HttpEndpoint = httpEndpoint,
                            PluginName = pluginName,
                            Description = reader.IsDBNull(12) ? null : reader.GetString(12),
                            LongDescription = reader.IsDBNull(13) ? null : reader.GetString(13),
                            Texts = texts,
                            DocumentationSource = reader.IsDBNull(14) ? null : reader.GetString(14),
                            RequiredFilterColumns = reqFilters
                        };
                        activeTables.Add((tid, identifier, tableEntity));
                    }
                    catch (Exception ex) when (ex is InvalidCastException or FormatException or JsonException or ArgumentException)
                    {
                        _logger?.LogWarning(ex, "Catalog row in TABLES is invalid and was skipped.");
                    }
                }
            }

            var columnsByTable = new Dictionary<Guid, List<TableColumn>>();
            var maskingRulesByTable = new Dictionary<Guid, Dictionary<string, MaskingRule>>();
            var corruptedTableIds = new HashSet<Guid>();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT c.id, c.table_id, c.column_name, c.data_type, c.is_sensitive,
                                           m.id, m.rule_type, m.pattern_or_format, m.replacement, m.hmac_key_id,
                                           c.description, c.long_description, c.meta_json, c.doc_source,
                                           c.display_name, c.texts_json
                                    FROM TABLE_COLUMNS c
                                    LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
                                    INNER JOIN TABLES t ON c.table_id = t.id
                                    WHERE t.is_active = 1";
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    Guid? tid = null;
                    try
                    {
                        var colId = Guid.Parse(reader.GetString(0));
                        tid = Guid.Parse(reader.GetString(1));
                        var colName = reader.GetString(2);
                        var dataType = reader.GetString(3);
                        var isSensitive = reader.GetInt32(4) == 1;

                        var desc = reader.IsDBNull(10) ? null : reader.GetString(10);
                        var longDesc = reader.IsDBNull(11) ? null : reader.GetString(11);
                        var metaJson = reader.IsDBNull(12) ? null : reader.GetString(12);
                        var docSource = reader.IsDBNull(13) ? null : reader.GetString(13);
                        var colDisplayName = reader.IsDBNull(14) ? null : reader.GetString(14);
                        var colTextsJson = reader.IsDBNull(15) ? null : reader.GetString(15);
                        var colTexts = CatalogLocalizationPersistence.ParseLocalizedTexts(colTextsJson, colId, _logger);
                        var metaDict = SqliteGovernanceRepository.ParseColumnMetaJson(metaJson);

                        if (!columnsByTable.TryGetValue(tid.Value, out var colList))
                        {
                            colList = new List<TableColumn>();
                            columnsByTable[tid.Value] = colList;
                        }

                        colList.Add(new TableColumn
                        {
                            Id = colId,
                            TableId = tid.Value,
                            ColumnName = colName,
                            DisplayName = colDisplayName,
                            DataType = dataType,
                            IsSensitive = isSensitive,
                            Description = desc,
                            LongDescription = longDesc,
                            Texts = colTexts,
                            DocumentationSource = docSource,
                            Meta = metaDict
                        });

                        if (!reader.IsDBNull(5))
                        {
                            var ruleType = reader.GetString(6);
                            if (string.IsNullOrWhiteSpace(ruleType))
                            {
                                throw new FormatException("Corrupted masking rule: rule_type is empty");
                            }

                            if (!maskingRulesByTable.TryGetValue(tid.Value, out var maskDict))
                            {
                                maskDict = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);
                                maskingRulesByTable[tid.Value] = maskDict;
                            }

                            maskDict[colName] = new MaskingRule
                            {
                                Id = Guid.Parse(reader.GetString(5)),
                                TableColumnId = colId,
                                RuleType = ruleType,
                                PatternOrFormat = reader.IsDBNull(7) ? null : reader.GetString(7),
                                Replacement = reader.IsDBNull(8) ? null : reader.GetString(8),
                                HmacKeyId = reader.IsDBNull(9) ? null : reader.GetString(9)
                            };
                        }
                    }
                    catch (Exception ex) when (ex is InvalidCastException or FormatException or JsonException or ArgumentException)
                    {
                        _logger?.LogWarning(ex, "Catalog row in TABLE_COLUMNS / COLUMN_MASKING_RULES is invalid; marking table as unavailable (fail-closed).");
                        if (tid.HasValue)
                        {
                            corruptedTableIds.Add(tid.Value);
                        }
                    }
                }
            }

            Dictionary<Guid, CatalogKeyDefinitionStore.TableKeyRows>? prefetchedKeys = null;
            var allActiveTableIds = activeTables.Select(t => t.Id).ToList();
            if (await CatalogKeyDefinitionStore.TablesExistAsync(conn, null, CatalogKeyDefinitionStore.SqlServer, ct, _keyTables).ConfigureAwait(false))
            {
                prefetchedKeys = await CatalogKeyDefinitionStore.ReadBatchAsync(conn, null, CatalogKeyDefinitionStore.SqlServer, allActiveTableIds, ct).ConfigureAwait(false);
            }

            var rowFilterStrategies = await RowFilterEvaluationStore.ReadBatchObjectAsync(
                conn, null, CatalogKeyDefinitionStore.SqlServer, ct).ConfigureAwait(false);

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
                IReadOnlyList<TableIndexDefinition> indexes;
                try
                {
                    (pkColumns, indexes) = await CatalogKeyDefinitionStore.ReadAsync(
                        conn, null, CatalogKeyDefinitionStore.SqlServer, identifier, tid, columns, ct,
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
                    Indexes = indexes,
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
                overlayConn, null, CatalogKeyDefinitionStore.SqlServer, null, _logger, ct).ConfigureAwait(false);
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
        Autheris.Application.Dbt.Services.SemanticVirtualTables.EnsureNamespaceRespected(metadata);
        // Only a replacing write validates (and later persists) the definition; any other upsert never reads or rewrites it (SEC-94-01).
        if (metadata.ReplaceKeyDefinition)
        {
            metadata = TableKeyDefinitionValidator.Validate(metadata);
        }

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var tableId = metadata.Table.Id == Guid.Empty ? Guid.NewGuid() : metadata.Table.Id;
            var tableExisted = false;
            var structuralChange = false;

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                // UPDLOCK/ROWLOCK serialise concurrent upserts of the same table (SQL Server equivalent of FOR UPDATE).
                cmd.CommandText = @"SELECT id, source_type, sensitivity, requires_four_eyes, is_active, data_source_type FROM TABLES WITH (UPDLOCK, ROWLOCK)
                                    WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)";
                cmd.Parameters.AddWithValue("@domain", metadata.Identifier.Domain);
                cmd.Parameters.AddWithValue("@schema", metadata.Identifier.Schema);
                cmd.Parameters.AddWithValue("@table", metadata.Identifier.TableName);

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
                upsertCmd.CommandText = @"MERGE TABLES WITH (HOLDLOCK) AS t
                                          USING (SELECT @id AS id) AS s
                                          ON t.id = s.id
                                          WHEN MATCHED THEN UPDATE SET
                                              source_type = @sourceType,
                                              source_name = @domain,
                                              schema_name = @schema,
                                              table_name = @tableName,
                                              display_name = @displayName,
                                              sensitivity = @sensitivity,
                                              requires_four_eyes = @fourEyes,
                                              is_active = @active,
                                              data_source_type = @dst,
                                              http_endpoint_json = @httpJson,
                                              plugin_name = @plugin,
                                              description = COALESCE(@desc, t.description),
                                              long_description = COALESCE(@longDesc, t.long_description),
                                              doc_source = COALESCE(@docSource, t.doc_source),
                                              required_filter_columns_json = @requiredFilterColumnsJson,
                                              texts_json = @textsJson
                                          WHEN NOT MATCHED THEN INSERT (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, data_source_type, http_endpoint_json, plugin_name, description, long_description, doc_source, required_filter_columns_json, texts_json)
                                              VALUES (@id, @sourceType, @domain, @schema, @tableName, @displayName, @sensitivity, @fourEyes, @active, @dst, @httpJson, @plugin, @desc, @longDesc, @docSource, @requiredFilterColumnsJson, @textsJson);";
                upsertCmd.Parameters.AddWithValue("@id", tableId.ToString());
                upsertCmd.Parameters.AddWithValue("@sourceType", metadata.Table.SourceType);
                upsertCmd.Parameters.AddWithValue("@domain", metadata.Identifier.Domain);
                upsertCmd.Parameters.AddWithValue("@schema", metadata.Identifier.Schema);
                upsertCmd.Parameters.AddWithValue("@tableName", metadata.Identifier.TableName);
                upsertCmd.Parameters.AddWithValue("@displayName", metadata.Table.DisplayName);
                upsertCmd.Parameters.AddWithValue("@sensitivity", metadata.Table.Sensitivity);
                upsertCmd.Parameters.AddWithValue("@fourEyes", metadata.Table.RequiresFourEyes ? 1 : 0);
                upsertCmd.Parameters.AddWithValue("@active", metadata.Table.IsActive ? 1 : 0);
                upsertCmd.Parameters.AddWithValue("@dst", (int)metadata.Table.DataSourceType);
                upsertCmd.Parameters.AddWithValue("@httpJson", (object?)httpEndpointJson ?? DBNull.Value);
                upsertCmd.Parameters.AddWithValue("@plugin", (object?)metadata.Table.PluginName ?? DBNull.Value);
                upsertCmd.Parameters.AddWithValue("@desc", (object?)metadata.Table.Description ?? DBNull.Value);
                upsertCmd.Parameters.AddWithValue("@longDesc", (object?)metadata.Table.LongDescription ?? DBNull.Value);
                upsertCmd.Parameters.AddWithValue("@docSource", (object?)metadata.Table.DocumentationSource ?? DBNull.Value);
                upsertCmd.Parameters.AddWithValue("@requiredFilterColumnsJson", metadata.RequiredFilterColumns.IsDefaultOrEmpty ? (object)DBNull.Value : JsonSerializer.Serialize(metadata.RequiredFilterColumns));
                upsertCmd.Parameters.AddWithValue("@textsJson", (object?)CatalogLocalizationPersistence.SerializeLocalizedTexts(metadata.Table.Texts) ?? DBNull.Value);
                await upsertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // Issue #111 (A-10): the configured default tier is given to a NEW table only; the Hidden default writes no row.
            if (!tableExisted && _options?.Catalog?.DefaultVisibility == DatasetVisibility.Summary)
            {
                await DatasetCatalogStore.InsertDefaultAsync(conn, tx, CatalogKeyDefinitionStore.SqlServer, tableId, DatasetVisibility.Summary, ct).ConfigureAwait(false);
            }

            var existingColsByName = new Dictionary<string, (Guid Id, string DataType, bool IsSensitive)>(StringComparer.OrdinalIgnoreCase);
            var existingMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);
            var colIdByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            await using (var existingColsCmd = conn.CreateCommand())
            {
                existingColsCmd.Transaction = tx;
                existingColsCmd.CommandText = @"SELECT c.id, c.column_name, c.data_type, c.is_sensitive,
                                                       m.id, m.rule_type, m.pattern_or_format, m.replacement, m.hmac_key_id
                                                FROM TABLE_COLUMNS c
                                                LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
                                                WHERE c.table_id = @tableId";
                existingColsCmd.Parameters.AddWithValue("@tableId", tableId.ToString());
                await using var reader = await existingColsCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (Guid.TryParse(reader.GetString(0), out var existingId))
                    {
                        var colName = reader.GetString(1);
                        var colDataType = reader.GetString(2);
                        var colSens = reader.GetInt32(3) == 1;
                        existingColsByName[colName] = (existingId, colDataType, colSens);
                        colIdByName[colName] = existingId;

                        if (!reader.IsDBNull(4) && Guid.TryParse(reader.GetString(4), out var maskId))
                        {
                            existingMaskingRules[colName] = new MaskingRule
                            {
                                Id = maskId,
                                TableColumnId = existingId,
                                RuleType = reader.GetString(5),
                                PatternOrFormat = reader.IsDBNull(6) ? null : reader.GetString(6),
                                Replacement = reader.IsDBNull(7) ? null : reader.GetString(7),
                                HmacKeyId = reader.IsDBNull(8) ? null : reader.GetString(8)
                            };
                        }
                    }
                }
            }

            if (SqliteGovernanceRepository.HasMaskingRulesChanged(existingMaskingRules, metadata.ColumnMaskingRules))
            {
                structuralChange = true;
            }

            foreach (var col in metadata.Columns)
            {
                var colId = col.Id == Guid.Empty ? Guid.NewGuid() : col.Id;
                if (existingColsByName.TryGetValue(col.ColumnName, out var existingCol))
                {
                    colId = existingCol.Id;
                    if (!string.Equals(existingCol.DataType, col.DataType, StringComparison.OrdinalIgnoreCase) ||
                        existingCol.IsSensitive != col.IsSensitive)
                    {
                        structuralChange = true;
                    }
                }
                else
                {
                    structuralChange = true;
                }

                IReadOnlyDictionary<string, string> effectiveMeta = metadata.ReplaceKeyDefinition
                    ? CatalogKeyDefinitionStore.WithKeyMarkers(col.Meta, col.ColumnName, metadata.PrimaryKeyColumns)
                    : col.Meta ?? new Dictionary<string, string>();
                // A replacing write must overwrite stale key markers even when no other meta remains (the upsert coalesces NULL).
                var metaJson = effectiveMeta.Count > 0
                    ? JsonSerializer.Serialize(effectiveMeta)
                    : metadata.ReplaceKeyDefinition ? "{}" : null;

                await using (var insertColCmd = conn.CreateCommand())
                {
                    insertColCmd.Transaction = tx;
                    insertColCmd.CommandText = @"MERGE TABLE_COLUMNS WITH (HOLDLOCK) AS t
                                                USING (SELECT @id AS id) AS s
                                                ON t.id = s.id
                                                WHEN MATCHED THEN UPDATE SET
                                                    data_type = @dataType,
                                                    is_sensitive = @isSensitive,
                                                    description = COALESCE(@description, t.description),
                                                    long_description = COALESCE(@longDescription, t.long_description),
                                                    doc_source = COALESCE(@docSource, t.doc_source),
                                                    meta_json = COALESCE(@metaJson, t.meta_json),
                                                    display_name = @displayName,
                                                    texts_json = @textsJson
                                                WHEN NOT MATCHED THEN INSERT (id, table_id, column_name, data_type, is_sensitive, description, long_description, doc_source, meta_json, display_name, texts_json)
                                                    VALUES (@id, @tableId, @columnName, @dataType, @isSensitive, @description, @longDescription, @docSource, @metaJson, @displayName, @textsJson);";
                    insertColCmd.Parameters.AddWithValue("@id", colId.ToString());
                    insertColCmd.Parameters.AddWithValue("@tableId", tableId.ToString());
                    insertColCmd.Parameters.AddWithValue("@columnName", col.ColumnName);
                    insertColCmd.Parameters.AddWithValue("@dataType", col.DataType);
                    insertColCmd.Parameters.AddWithValue("@isSensitive", col.IsSensitive ? 1 : 0);
                    insertColCmd.Parameters.AddWithValue("@description", (object?)col.Description ?? DBNull.Value);
                    insertColCmd.Parameters.AddWithValue("@longDescription", (object?)col.LongDescription ?? DBNull.Value);
                    insertColCmd.Parameters.AddWithValue("@docSource", (object?)col.DocumentationSource ?? DBNull.Value);
                    insertColCmd.Parameters.AddWithValue("@metaJson", (object?)metaJson ?? DBNull.Value);
                    insertColCmd.Parameters.AddWithValue("@displayName", (object?)col.DisplayName ?? DBNull.Value);
                    insertColCmd.Parameters.AddWithValue("@textsJson", (object?)CatalogLocalizationPersistence.SerializeLocalizedTexts(col.Texts) ?? DBNull.Value);
                    await insertColCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                colIdByName[col.ColumnName] = colId;

                await using (var delMaskCmd = conn.CreateCommand())
                {
                    delMaskCmd.Transaction = tx;
                    delMaskCmd.CommandText = "DELETE FROM COLUMN_MASKING_RULES WHERE table_column_id = @colId";
                    delMaskCmd.Parameters.AddWithValue("@colId", colId.ToString());
                    await delMaskCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                if (metadata.ColumnMaskingRules.TryGetValue(col.ColumnName, out var maskRule))
                {
                    await using (var insertMaskCmd = conn.CreateCommand())
                    {
                        insertMaskCmd.Transaction = tx;
                        insertMaskCmd.CommandText = @"INSERT INTO COLUMN_MASKING_RULES (id, table_column_id, rule_type, pattern_or_format, replacement, hmac_key_id)
                                                      VALUES (@id, @colId, @ruleType, @pattern, @replacement, @hmacKeyId);";
                        // PERS-14: the rule id is always server-generated (a client id is ignored), see the PostgreSQL store.
                        insertMaskCmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                        insertMaskCmd.Parameters.AddWithValue("@colId", colId.ToString());
                        insertMaskCmd.Parameters.AddWithValue("@ruleType", maskRule.RuleType);
                        insertMaskCmd.Parameters.AddWithValue("@pattern", (object?)maskRule.PatternOrFormat ?? DBNull.Value);
                        insertMaskCmd.Parameters.AddWithValue("@replacement", (object?)maskRule.Replacement ?? DBNull.Value);
                        insertMaskCmd.Parameters.AddWithValue("@hmacKeyId", (object?)maskRule.HmacKeyId ?? DBNull.Value);
                        if (await insertMaskCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                        {
                            throw new InvalidOperationException($"The masking rule of column '{col.ColumnName}' could not be stored.");
                        }
                    }
                }
            }

            if (metadata.ReplaceKeyDefinition)
            {
                structuralChange = true;
                await CatalogKeyDefinitionStore.ReplaceAsync(conn, tx, CatalogKeyDefinitionStore.SqlServer, tableId, metadata, colIdByName, ct).ConfigureAwait(false);
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
        CancellationToken ct = default)
    {
        TableKeyDefinitionChange? change;
        await using (var conn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                change = await CatalogKeyDefinitionStore.ReplaceNarrowAsync(
                    conn, tx, CatalogKeyDefinitionStore.SqlServer, table, primaryKey, indexes,
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
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                change = await RowFilterEvaluationStore.SetObjectAsync(
                    conn, tx, CatalogKeyDefinitionStore.SqlServer, table, settings, actor,
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
        return await DatasetCatalogStore.CountByVisibilityAsync(conn, null, CatalogKeyDefinitionStore.SqlServer, ct).ConfigureAwait(false);
    }

    public async Task<DatasetCatalogSettings?> GetDatasetCatalogSettingsAsync(TableIdentifier table, bool strict = true, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await DatasetCatalogStore.ReadSettingsAsync(
            conn, null, CatalogKeyDefinitionStore.SqlServer, table, strict, _logger, ct).ConfigureAwait(false);
    }

    public Task<DatasetCatalogChange?> ReplaceDatasetTagsAsync(
        TableIdentifier table, IReadOnlyList<string> tags, DatasetTagSource source, Sid actor, CancellationToken ct = default) =>
        WriteCatalogSettingsAsync(table, (conn, tx) =>
            DatasetCatalogStore.ReplaceTagsAsync(conn, tx, CatalogKeyDefinitionStore.SqlServer, table, tags, source, actor, ct), ct);

    public Task<DatasetCatalogChange?> ChangeDatasetTagsAsync(
        TableIdentifier table, IReadOnlyList<string> add, IReadOnlyList<string> remove, Sid actor, CancellationToken ct = default) =>
        WriteCatalogSettingsAsync(table, (conn, tx) =>
            DatasetCatalogStore.ChangeTagsAsync(conn, tx, CatalogKeyDefinitionStore.SqlServer, table, add, remove, actor, ct), ct);

    public Task<DatasetCatalogChange?> SetDatasetVisibilityAsync(
        TableIdentifier table, DatasetVisibility visibility, Sid actor, CancellationToken ct = default) =>
        WriteCatalogSettingsAsync(table, (conn, tx) =>
            DatasetCatalogStore.SetVisibilityAsync(conn, tx, CatalogKeyDefinitionStore.SqlServer, table, visibility, actor, ct), ct);

    /// <summary>Issue #111: one transaction (locked before read, write, after read); no epoch (tags are not policy inputs); the local metadata entry is dropped after the commit.</summary>
    private async Task<DatasetCatalogChange?> WriteCatalogSettingsAsync(
        TableIdentifier table, Func<SqlConnection, SqlTransaction, Task<DatasetCatalogChange?>> write, CancellationToken ct)
    {
        DatasetCatalogChange? change;
        await using (var conn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
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
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT source_name, schema_name, table_name FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table) AND is_active = 1";
        cmd.Parameters.AddWithValue("@domain", table.Domain);
        cmd.Parameters.AddWithValue("@schema", table.Schema);
        cmd.Parameters.AddWithValue("@table", table.TableName);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            return new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public async Task<TableIdentifier?> GetActiveTableIdentifierByIdAsync(Guid tableId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT source_name, schema_name, table_name FROM TABLES WHERE id = @id AND is_active = 1";
        cmd.Parameters.AddWithValue("@id", tableId.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            return new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public async Task<long> GetTableEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT epoch FROM POLICY_EPOCHS
                            WHERE LOWER(domain) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)";
        cmd.Parameters.AddWithValue("@domain", table.Domain);
        cmd.Parameters.AddWithValue("@schema", table.Schema);
        cmd.Parameters.AddWithValue("@table", table.TableName);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long l ? l : (result is int i ? i : 1L);
    }

    public async Task<long> IncrementTableEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
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

    private async Task<long> IncrementTableEpochInternalAsync(SqlConnection conn, SqlTransaction tx, TableIdentifier table, CancellationToken ct)
    {
        _metadataCache.TryRemove(table.ToString().ToLowerInvariant(), out _);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"UPDATE POLICY_EPOCHS
                                SET epoch = epoch + 1, updated_at = @now
                                OUTPUT inserted.epoch
                                WHERE LOWER(domain) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)";
            cmd.Parameters.AddWithValue("@domain", table.Domain);
            cmd.Parameters.AddWithValue("@schema", table.Schema);
            cmd.Parameters.AddWithValue("@table", table.TableName);
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));

            var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (scalar != null)
            {
                return Convert.ToInt64(scalar);
            }

            // No row yet: insert, or bump when a concurrent writer inserted the same natural key (domain, schema, table).
            await using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = @"MERGE POLICY_EPOCHS WITH (HOLDLOCK) AS t
                                      USING (SELECT @domain AS domain, @schema AS schema_name, @table AS table_name) AS s
                                      ON t.domain = s.domain AND t.schema_name = s.schema_name AND t.table_name = s.table_name
                                      WHEN MATCHED THEN UPDATE SET epoch = t.epoch + 1, updated_at = @now
                                      WHEN NOT MATCHED THEN INSERT (table_id, domain, schema_name, table_name, epoch, updated_at)
                                          VALUES (@id, @domain, @schema, @table, 1, @now)
                                      OUTPUT inserted.epoch;";
            insertCmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
            insertCmd.Parameters.AddWithValue("@domain", table.Domain);
            insertCmd.Parameters.AddWithValue("@schema", table.Schema);
            insertCmd.Parameters.AddWithValue("@table", table.TableName);
            insertCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));

            var insScalar = await insertCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return insScalar != null ? Convert.ToInt64(insScalar) : 1L;
        }
    }

    public async Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        // One statement: a reader never sees a mixed key set (SEC-REL151-06). Key rows are only reached through the join (SEC-REL151-08).
        cmd.CommandText = @"SELECT r.id, r.parent_table_id, r.child_table_id, r.relation_name, r.cardinality,
                                   tp.source_name, tp.schema_name, tp.table_name,
                                   tc.source_name, tc.schema_name, tc.table_name,
                                   k.key_ordinal, k.parent_column, k.child_column,
                                   COALESCE(r.allow_cross_data_source, 0)
                            FROM TABLE_RELATIONS r
                            JOIN TABLES tp ON r.parent_table_id = tp.id
                            JOIN TABLES tc ON r.child_table_id = tc.id
                            LEFT JOIN TABLE_RELATION_KEYS k ON k.relation_id = r.id
                            WHERE LOWER(tp.source_name) = LOWER(@pDomain) AND LOWER(tp.schema_name) = LOWER(@pSchema) AND LOWER(tp.table_name) = LOWER(@pTable)
                            ORDER BY r.id, k.key_ordinal";
        cmd.Parameters.AddWithValue("@pDomain", parentTable.Domain);
        cmd.Parameters.AddWithValue("@pSchema", parentTable.Schema);
        cmd.Parameters.AddWithValue("@pTable", parentTable.TableName);

        var rows = new List<RelationRowData>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(new RelationRowData(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    new TableIdentifier(reader.GetString(5), reader.GetString(6), reader.GetString(7)),
                    new TableIdentifier(reader.GetString(8), reader.GetString(9), reader.GetString(10)),
                    reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    !reader.IsDBNull(14) && Convert.ToBoolean(reader.GetValue(14))));
            }
        }

        return RelationRowAssembler.Assemble(rows, _logger);
    }

    public async Task<RelationWriteResult> CreateRelationAsync(TableRelation relation, CancellationToken ct = default)
    {
        // Rule check before any connection is opened (SEC-REL151-03).
        RelationWriteSupport.ValidateKeysOrThrow(relation);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        // One connection, one explicit transaction; disposing it uncommitted (failure or cancellation) rolls back all rows (SEC-REL151-06).
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

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
            cmd.CommandText = @"MERGE TABLE_RELATIONS WITH (HOLDLOCK) AS t
                                USING (SELECT @pId AS p, @cId AS c, @relName AS n) AS s
                                ON t.parent_table_id = s.p AND t.child_table_id = s.c AND t.relation_name = s.n
                                WHEN MATCHED THEN UPDATE SET cardinality = @card
                                WHEN NOT MATCHED THEN INSERT (id, parent_table_id, child_table_id, relation_name, cardinality, allow_cross_data_source)
                                    VALUES (@id, @pId, @cId, @relName, @card, @allowCross)
                                OUTPUT inserted.id, $action;";
            cmd.Parameters.AddWithValue("@id", relation.Id.ToString());
            cmd.Parameters.AddWithValue("@pId", parentTableId);
            cmd.Parameters.AddWithValue("@cId", childTableId);
            cmd.Parameters.AddWithValue("@relName", relation.RelationName);
            cmd.Parameters.AddWithValue("@card", relation.Cardinality.ToString());
            cmd.Parameters.AddWithValue("@allowCross", relation.AllowCrossDataSource ? 1 : 0);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            storedId = reader.GetString(0);
            created = string.Equals(reader.GetString(1), "INSERT", StringComparison.Ordinal);
        }

        // The upsert holds the relation row (row lock / HOLDLOCK range), so the stored pairs read here are consistent with the replace below.
        var stored = new List<(string Parent, string Child)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT parent_column, child_column FROM TABLE_RELATION_KEYS WHERE relation_id = @id ORDER BY key_ordinal";
            cmd.Parameters.AddWithValue("@id", storedId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                stored.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var keysChanged = !created && !RelationWriteSupport.SamePairs(stored, parentKeys, childKeys);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM TABLE_RELATION_KEYS WHERE relation_id = @id";
            cmd.Parameters.AddWithValue("@id", storedId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        for (var i = 0; i < parentKeys.Count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO TABLE_RELATION_KEYS (relation_id, key_ordinal, parent_column, child_column) VALUES (@id, @ord, @pc, @cc)";
            cmd.Parameters.AddWithValue("@id", storedId);
            cmd.Parameters.AddWithValue("@ord", i + 1);
            cmd.Parameters.AddWithValue("@pc", parentKeys[i]);
            cmd.Parameters.AddWithValue("@cc", childKeys[i]);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        var newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 1, 0, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
        return new RelationWriteResult(Guid.Parse(storedId), created, keysChanged);
    }

    /// <summary>Resolves an active table id from its identifier: zero or several matches throw (SEC-REL151-07, no LIMIT 1).</summary>
    private static async Task<string> ResolveActiveTableIdAsync(SqlConnection conn, SqlTransaction tx, string relationName, TableIdentifier table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table) AND is_active = 1";
        cmd.Parameters.AddWithValue("@domain", table.Domain);
        cmd.Parameters.AddWithValue("@schema", table.Schema);
        cmd.Parameters.AddWithValue("@table", table.TableName);
        var ids = new List<string>(2);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids.Count == 1 ? ids[0] : throw RelationWriteSupport.TableNotResolved(relationName, table, ids.Count);
    }

    private static async Task<List<string>> LoadColumnNamesAsync(SqlConnection conn, SqlTransaction tx, string tableId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT column_name FROM TABLE_COLUMNS WHERE table_id = @t";
        cmd.Parameters.AddWithValue("@t", tableId);
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    public async Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var owners = new List<DataOwner>();
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT o.id, o.ad_sid, o.ad_account, o.display_name, o.email, o.is_active
                            FROM DATA_OWNERS o
                            JOIN TABLE_OWNERS tow ON o.id = tow.data_owner_id
                            JOIN TABLES t ON tow.table_id = t.id
                            WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table) AND o.is_active = 1";
        cmd.Parameters.AddWithValue("@domain", table.Domain);
        cmd.Parameters.AddWithValue("@schema", table.Schema);
        cmd.Parameters.AddWithValue("@table", table.TableName);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            owners.Add(new DataOwner
            {
                Id = Guid.Parse(reader.GetString(0)),
                AdSid = new Sid(reader.GetString(1)),
                AdAccount = reader.GetString(2),
                DisplayName = reader.GetString(3),
                Email = reader.GetString(4),
                IsActive = reader.GetInt32(5) == 1
            });
        }

        return owners;
    }

    public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default) =>
        IsAuthorizedApproverForTableInternalAsync(table, approverSid, null, ct);

    public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, string? itsmApproverAccount, CancellationToken ct = default) =>
        IsAuthorizedApproverForTableInternalAsync(table, approverSid, itsmApproverAccount, ct);

    /// <summary>Review R4-4 (rest): resolves SID, account or e-mail spellings to stable DATA_OWNERS ids.</summary>
    private async Task<IReadOnlyCollection<string>> ResolveDataOwnerIdsInternalAsync(IEnumerable<string> identifiers, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        foreach (var identifier in identifiers.Where(i => !string.IsNullOrWhiteSpace(i)))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT id FROM DATA_OWNERS
                                WHERE LOWER(ad_sid) = LOWER(@p) OR LOWER(ad_account) = LOWER(@p) OR LOWER(email) = LOWER(@p)";
            cmd.Parameters.AddWithValue("@p", identifier);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(reader.GetString(0));
            }
        }

        return ids;
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

    private static void AddNullableTextParameter(SqlCommand cmd, string name, string? value)
    {
        // Explicit type so "@x IS NOT NULL" checks work with a null value (same intent as NpgsqlDbType.Text in the PG port).
        cmd.Parameters.Add(name, SqlDbType.NVarChar, 256).Value = (object?)value ?? DBNull.Value;
    }

    private async Task<bool> IsAuthorizedApproverForTableInternalAsync(TableIdentifier table, Sid approverSid, string? itsmApproverAccount, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await IsAuthorizedApproverForTableInternalAsync(conn, null, table, approverSid, itsmApproverAccount, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<string>> GetTransitiveRolesAsync(Sid subjectSid, CancellationToken ct = default)
    {
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT r.role_name
                            FROM ROLES r
                            JOIN ROLE_MEMBERS rm ON r.id = rm.role_id
                            WHERE rm.member_sid = @sid";
        cmd.Parameters.AddWithValue("@sid", subjectSid.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            roles.Add(reader.GetString(0));
        }

        return roles;
    }

    public async Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation delegation, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"MERGE DATA_OWNER_DELEGATIONS WITH (HOLDLOCK) AS t
                                USING (SELECT @id AS id) AS s
                                ON t.id = s.id
                                WHEN MATCHED THEN UPDATE SET
                                    delegate_sid = @delegateSid,
                                    valid_from = @validFrom,
                                    valid_to = @validTo,
                                    reason = @reason
                                WHEN NOT MATCHED THEN INSERT (id, data_owner_id, delegate_sid, valid_from, valid_to, reason)
                                    VALUES (@id, @ownerId, @delegateSid, @validFrom, @validTo, @reason);";
            cmd.Parameters.AddWithValue("@id", delegation.Id.ToString());
            cmd.Parameters.AddWithValue("@ownerId", delegation.DataOwnerId.ToString());
            cmd.Parameters.AddWithValue("@delegateSid", delegation.DelegateSid.Value);
            cmd.Parameters.AddWithValue("@validFrom", delegation.ValidFrom.ToString("O"));
            cmd.Parameters.AddWithValue("@validTo", delegation.ValidTo.ToString("O"));
            cmd.Parameters.AddWithValue("@reason", delegation.Reason);
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
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"UPDATE TABLE_RELATIONS SET allow_cross_data_source = @allowed WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", relationId.ToString());
            cmd.Parameters.AddWithValue("@allowed", allowCrossDataSource ? 1 : 0);
            int rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (rows > 0)
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
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT structure_version, content_version FROM CATALOG_STATE WHERE id = 1;";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new Autheris.Application.Catalog.CatalogVersion(reader.GetInt64(0), reader.GetInt64(1));
        }
        return new Autheris.Application.Catalog.CatalogVersion(1, 1);
    }

    public async Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT r.id, r.parent_table_id, r.child_table_id, r.relation_name, r.cardinality,
                                   COALESCE(tp.source_name, 'default'), tp.schema_name, tp.table_name,
                                   COALESCE(tc.source_name, 'default'), tc.schema_name, tc.table_name,
                                   k.key_ordinal, k.parent_column, k.child_column,
                                   COALESCE(r.allow_cross_data_source, 0)
                            FROM TABLE_RELATIONS r
                            JOIN TABLES tp ON r.parent_table_id = tp.id
                            JOIN TABLES tc ON r.child_table_id = tc.id
                            LEFT JOIN TABLE_RELATION_KEYS k ON k.relation_id = r.id
                            ORDER BY r.id, k.key_ordinal";

        var rows = new List<RelationRowData>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(new RelationRowData(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    new TableIdentifier(reader.GetString(5), reader.GetString(6), reader.GetString(7)),
                    new TableIdentifier(reader.GetString(8), reader.GetString(9), reader.GetString(10)),
                    reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    !reader.IsDBNull(14) && Convert.ToBoolean(reader.GetValue(14))));
            }
        }

        return RelationRowAssembler.Assemble(rows, _logger);
    }

    private async Task<Autheris.Application.Catalog.CatalogVersion> BumpCatalogVersionInternalAsync(
        SqlConnection conn,
        SqlTransaction tx,
        long structureDelta,
        long contentDelta,
        CancellationToken ct)
    {
        if (structureDelta < 0 || contentDelta < 0 || (structureDelta == 0 && contentDelta == 0))
        {
            throw new ArgumentException("Version deltas must be non-negative with at least one positive delta.");
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"UPDATE CATALOG_STATE
                            SET structure_version = structure_version + @structureDelta,
                                content_version = content_version + @contentDelta,
                                updated_at = @now
                            OUTPUT inserted.structure_version, inserted.content_version
                            WHERE id = 1;";
        cmd.Parameters.AddWithValue("@structureDelta", structureDelta);
        cmd.Parameters.AddWithValue("@contentDelta", contentDelta);
        cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("now", System.Data.SqlDbType.DateTimeOffset) { Value = DateTimeOffset.UtcNow });

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new Autheris.Application.Catalog.CatalogVersion(reader.GetInt64(0), reader.GetInt64(1));
        }
        return new Autheris.Application.Catalog.CatalogVersion(1, 1);
    }

    private async Task PublishCatalogChangedEventAsync(Autheris.Application.Catalog.CatalogVersion version, CancellationToken ct)
    {
        if (_eventBus != null)
        {
            try
            {
                await _eventBus.PublishAsync(
                    Autheris.Application.Catalog.ICatalogVersionProvider.CatalogChangedEventChannel,
                    new Autheris.Application.Catalog.CatalogChangedEvent(version.StructureVersion, version.ContentVersion, DateTimeOffset.UtcNow),
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to publish catalog changed event; polling will detect changes.");
            }
        }
    }
}
