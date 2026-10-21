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
    {
        if (string.IsNullOrWhiteSpace(metaJson))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var doc = JsonDocument.Parse(metaJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = JsonValueText.From(prop.Value);            }
            return dict;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

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
                cmd.CommandText = @"SELECT id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, data_source_type, http_endpoint_json, plugin_name, description, long_description, doc_source, required_filter_columns_json, texts_json
                                    FROM TABLES
                                    WHERE source_name = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE";
                cmd.Parameters.AddWithValue("@domain", table.Domain);
                cmd.Parameters.AddWithValue("@schema", table.Schema);
                cmd.Parameters.AddWithValue("@table", table.TableName);

                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
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

            var columns = new List<TableColumn>();
            var maskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT c.id, c.column_name, c.data_type, c.is_sensitive,
                                           m.id, m.rule_type, m.pattern_or_format, m.replacement, m.hmac_key_id,
                                           c.description, c.long_description, c.meta_json, c.doc_source,
                                           c.display_name, c.texts_json
                                    FROM TABLE_COLUMNS c
                                    LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
                                    WHERE c.table_id = @tid
                                    ORDER BY c.rowid"; // registration order, equal to the list read (a natural-key index scan would sort by name)
                cmd.Parameters.AddWithValue("@tid", tableId.Value.ToString());

                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var colId = Guid.Parse(reader.GetString(0));
                    var colName = reader.GetString(1);
                    var metaJson = reader.IsDBNull(11) ? null : reader.GetString(11);
                    var metaDict = ParseColumnMetaJson(metaJson);
                    var colDisplayName = reader.IsDBNull(13) ? null : reader.GetString(13);
                    var colTextsJson = reader.IsDBNull(14) ? null : reader.GetString(14);
                    var colTexts = CatalogLocalizationPersistence.ParseLocalizedTexts(colTextsJson, colId, _logger);

                    var col = new TableColumn
                    {
                        Id = colId,
                        TableId = tableId.Value,
                        ColumnName = colName,
                        DisplayName = colDisplayName,
                        DataType = reader.GetString(2),
                        IsSensitive = reader.GetInt32(3) == 1,
                        Description = reader.IsDBNull(9) ? null : reader.GetString(9),
                        LongDescription = reader.IsDBNull(10) ? null : reader.GetString(10),
                        Texts = colTexts,
                        DocumentationSource = reader.IsDBNull(12) ? null : reader.GetString(12),
                        Meta = metaDict
                    };
                    columns.Add(col);

                    if (!reader.IsDBNull(4))
                    {
                        var maskRule = new MaskingRule
                        {
                            Id = Guid.Parse(reader.GetString(4)),
                            TableColumnId = colId,
                            RuleType = reader.GetString(5),
                            PatternOrFormat = reader.IsDBNull(6) ? null : reader.GetString(6),
                            Replacement = reader.IsDBNull(7) ? null : reader.GetString(7),
                            HmacKeyId = reader.IsDBNull(8) ? null : reader.GetString(8)
                        };
                        maskingRules[colName] = maskRule;
                    }
                }
            }

            IReadOnlyList<string> pkColumns;
            IReadOnlyList<TableIndexDefinition> indexes;
            try
            {
                (pkColumns, indexes) = await CatalogKeyDefinitionStore.ReadAsync(
                    conn, null, CatalogKeyDefinitionStore.Sqlite, table, tableId.Value, columns, ct, _keyTables).ConfigureAwait(false);
            }
            catch (TableMetadataUnavailableException)
            {
                _logger?.LogWarning("Metadata of table {Table} is unavailable: its declared key or index definition is invalid.", table);
                throw;
            }

            var rowFilterEvaluation = await RowFilterEvaluationStore.ReadObjectAsync(
                conn, null, CatalogKeyDefinitionStore.Sqlite, tableId.Value, ct).ConfigureAwait(false);

            var result = new TableMetadata
            {
                Table = tableEntity,
                Identifier = table,
                Columns = columns,
                ColumnMaskingRules = maskingRules,
                PrimaryKeyColumns = pkColumns,
                Indexes = indexes,
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
            var results = new List<TableMetadata>();
            var tableRows = new List<(Guid id, string schema, string name, Table table)>();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT t.id, t.source_type, t.source_name, t.schema_name, t.table_name,
                                           t.display_name, t.sensitivity, t.requires_four_eyes, t.is_active,
                                           COALESCE(t.source_name, p.domain, 'default') as domain,
                                           t.data_source_type, t.http_endpoint_json, t.plugin_name,
                                           t.description, t.long_description, t.doc_source,
                                           t.required_filter_columns_json, t.texts_json
                                    FROM TABLES t
                                    LEFT JOIN POLICY_EPOCHS p ON t.id = p.table_id
                                    WHERE t.is_active = 1";

                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                  try
                  {
                    var id = Guid.Parse(reader.GetString(0));
                    var domain = reader.GetString(9);
                    var schema = reader.GetString(3);
                    var name = reader.GetString(4);
                    var dstInt = reader.IsDBNull(10) ? 0 : reader.GetInt32(10);
                    var httpEndpointJson = reader.IsDBNull(11) ? null : reader.GetString(11);
                    var pluginName = reader.IsDBNull(12) ? null : reader.GetString(12);
                    var httpEndpoint = !string.IsNullOrWhiteSpace(httpEndpointJson)
                        ? JsonSerializer.Deserialize<HttpEndpointDescriptor>(httpEndpointJson)
                        : null;
                    var reqFiltersJson = reader.IsDBNull(16) ? null : reader.GetString(16);
                    var reqFilters = string.IsNullOrWhiteSpace(reqFiltersJson)
                        ? ImmutableArray<string>.Empty
                        : JsonSerializer.Deserialize<ImmutableArray<string>>(reqFiltersJson);
                    if (reqFilters.IsDefault) reqFilters = ImmutableArray<string>.Empty;
                    var textsJson = reader.IsDBNull(17) ? null : reader.GetString(17);
                    var texts = CatalogLocalizationPersistence.ParseLocalizedTexts(textsJson, id, _logger);

                    var table = new Table
                    {
                        Id = id,
                        SourceType = reader.GetString(1),
                        SourceName = reader.GetString(2),
                        SchemaName = schema,
                        TableName = name,
                        DisplayName = reader.GetString(5),
                        Sensitivity = reader.GetString(6),
                        RequiresFourEyes = reader.GetInt32(7) == 1,
                        IsActive = reader.GetInt32(8) == 1,
                        DataSourceType = (DataSourceType)dstInt,
                        HttpEndpoint = httpEndpoint,
                        PluginName = pluginName,
                        Description = reader.IsDBNull(13) ? null : reader.GetString(13),
                        LongDescription = reader.IsDBNull(14) ? null : reader.GetString(14),
                        Texts = texts,
                        DocumentationSource = reader.IsDBNull(15) ? null : reader.GetString(15),
                        RequiredFilterColumns = reqFilters
                    };
                    tableRows.Add((id, domain, name, table));
                  }
                  catch (Exception ex) when (ex is InvalidCastException or FormatException or JsonException or ArgumentException)
                  {
                    // One malformed catalog row must not make the whole catalog unavailable (fail-closed per row:
                    // the table is simply not offered, so no consent or masking can be bypassed).
                    _logger?.LogWarning(ex, "Catalog row {RowId} in TABLES is invalid and was skipped.", SafeValue(reader, 0));
                  }
                }
            }

            var columnsByTable = new Dictionary<Guid, List<TableColumn>>();
            var maskingRulesByTable = new Dictionary<Guid, Dictionary<string, MaskingRule>>();
            var corruptedTables = new HashSet<Guid>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT c.id, c.table_id, c.column_name, c.data_type, c.is_sensitive,
                                           c.description, c.long_description, c.meta_json, c.doc_source, c.display_name, c.texts_json,
                                           m.id, m.rule_type, m.pattern_or_format, m.replacement, m.hmac_key_id
                                    FROM TABLE_COLUMNS c
                                    LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
                                    ORDER BY c.rowid";
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                  Guid? currentTid = null;
                  try
                  {
                    var colId = Guid.Parse(reader.GetString(0));
                    var tid = Guid.Parse(reader.GetString(1));
                    currentTid = tid;
                    if (!columnsByTable.TryGetValue(tid, out var list))
                    {
                        list = new List<TableColumn>();
                        columnsByTable[tid] = list;
                    }
                    var colName = reader.GetString(2);
                    var metaJson = reader.IsDBNull(7) ? null : reader.GetString(7);
                    var metaDict = ParseColumnMetaJson(metaJson);
                    var colDisplayName = reader.IsDBNull(9) ? null : reader.GetString(9);
                    var colTextsJson = reader.IsDBNull(10) ? null : reader.GetString(10);
                    var colTexts = CatalogLocalizationPersistence.ParseLocalizedTexts(colTextsJson, colId, _logger);

                    list.Add(new TableColumn
                    {
                        Id = colId,
                        TableId = tid,
                        ColumnName = colName,
                        DisplayName = colDisplayName,
                        DataType = reader.GetString(3),
                        IsSensitive = reader.GetInt32(4) == 1,
                        Description = reader.IsDBNull(5) ? null : reader.GetString(5),
                        LongDescription = reader.IsDBNull(6) ? null : reader.GetString(6),
                        Texts = colTexts,
                        DocumentationSource = reader.IsDBNull(8) ? null : reader.GetString(8),
                        Meta = metaDict
                    });

                    if (!reader.IsDBNull(11))
                    {
                        var ruleType = reader.GetString(12);
                        if (string.IsNullOrWhiteSpace(ruleType))
                        {
                            throw new FormatException("Corrupted masking rule: rule_type is empty");
                        }

                        if (!maskingRulesByTable.TryGetValue(tid, out var ruleDict))
                        {
                            ruleDict = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);
                            maskingRulesByTable[tid] = ruleDict;
                        }

                        ruleDict[colName] = new MaskingRule
                        {
                            Id = Guid.Parse(reader.GetString(11)),
                            TableColumnId = colId,
                            RuleType = ruleType,
                            PatternOrFormat = reader.IsDBNull(13) ? null : reader.GetString(13),
                            Replacement = reader.IsDBNull(14) ? null : reader.GetString(14),
                            HmacKeyId = reader.IsDBNull(15) ? null : reader.GetString(15)
                        };
                    }
                  }
                  catch (Exception ex) when (ex is InvalidCastException or FormatException or JsonException or ArgumentException)
                  {
                    _logger?.LogWarning(ex, "Catalog row in TABLE_COLUMNS / COLUMN_MASKING_RULES is invalid and was skipped (fail-closed).");
                    if (currentTid.HasValue)
                    {
                        corruptedTables.Add(currentTid.Value);
                    }
                  }
                }
            }

            var keyTablesPresent = await CatalogKeyDefinitionStore.TablesExistAsync(conn, null, CatalogKeyDefinitionStore.Sqlite, ct, _keyTables).ConfigureAwait(false);
            // M4: one read per key table for all active tables (chunked) instead of two queries per table.
            var prefetched = keyTablesPresent
                ? await CatalogKeyDefinitionStore.ReadBatchAsync(conn, null, CatalogKeyDefinitionStore.Sqlite, tableRows.Select(r => r.id).ToList(), ct).ConfigureAwait(false)
                : null;
            foreach (var (id, domain, name, table) in tableRows)
            {
                TableIdentifier identifier;
                try
                {
                    identifier = new TableIdentifier(domain, table.SchemaName, name);
                }
                catch (ArgumentException ex)
                {
                    // e.g. a '.' in source_name/schema_name/table_name (TableIdentifier components must not contain dots)
                    _logger?.LogWarning(ex, "Catalog table {Id} ({Domain}/{Schema}/{Table}) has an invalid identifier and was skipped.", id, domain, table.SchemaName, name);
                    continue;
                }

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
                IReadOnlyList<TableIndexDefinition> indexes;
                try
                {
                    (pkColumns, indexes) = await CatalogKeyDefinitionStore.ReadAsync(
                        conn, null, CatalogKeyDefinitionStore.Sqlite, identifier, id, columns, ct, _keyTables,
                        prefetched is not null && prefetched.TryGetValue(id, out var keyRows) ? keyRows : null).ConfigureAwait(false);
                }
                catch (TableMetadataUnavailableException)
                {
                    // Fail closed: a table with an invalid persisted definition is not offered (never served with a partial key).
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
                    Indexes = indexes
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

    private static string SafeValue(SqliteDataReader reader, int ordinal)
    {
        try
        {
            return reader.IsDBNull(ordinal) ? "<null>" : Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture) ?? "<null>";
        }
        catch (Exception ex) when (ex is InvalidCastException or InvalidOperationException)
        {
            return "<unreadable>";
        }
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

        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            Guid tableId;
            bool structuralChange = false;
            string? targetDesc = metadata.Table.Description;
            string? targetLongDesc = metadata.Table.LongDescription;
            string? targetDocSource = metadata.Table.DocumentationSource;
            IReadOnlyList<string> persistedPks;
            IReadOnlyList<TableIndexDefinition> persistedIndexes;
            using var tx = conn.BeginTransaction();
            try
            {
                using (var selectCmd = conn.CreateCommand())
                {
                    selectCmd.Transaction = tx;
                    selectCmd.CommandText = @"SELECT id, source_type, sensitivity, requires_four_eyes, is_active, data_source_type, description, long_description, doc_source FROM TABLES 
                                              WHERE source_name = @domain COLLATE NOCASE 
                                                AND schema_name = @schema COLLATE NOCASE 
                                                AND table_name = @table COLLATE NOCASE";
                    selectCmd.Parameters.AddWithValue("@domain", metadata.Identifier.Domain);
                    selectCmd.Parameters.AddWithValue("@schema", metadata.Identifier.Schema);
                    selectCmd.Parameters.AddWithValue("@table", metadata.Identifier.TableName);
                    using var selectReader = await selectCmd.ExecuteReaderAsync(ct);
                    if (await selectReader.ReadAsync(ct))
                    {
                        tableId = Guid.Parse(selectReader.GetString(0));
                        var existingSourceType = selectReader.GetString(1);
                        var existingSensitivity = selectReader.GetString(2);
                        var existingFourEyes = selectReader.GetInt32(3) == 1;
                        var existingActive = selectReader.GetInt32(4) == 1;
                        var existingDst = selectReader.IsDBNull(5) ? 0 : selectReader.GetInt32(5);
                        var existingDesc = selectReader.IsDBNull(6) ? null : selectReader.GetString(6);
                        var existingLongDesc = selectReader.IsDBNull(7) ? null : selectReader.GetString(7);
                        var existingDocSource = selectReader.IsDBNull(8) ? null : selectReader.GetString(8);
                        await selectReader.CloseAsync();

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

                        using var updateCmd = conn.CreateCommand();
                        updateCmd.Transaction = tx;
                        updateCmd.CommandText = @"UPDATE TABLES 
                                                  SET source_type = @sourceType, 
                                                      display_name = @displayName, 
                                                      sensitivity = @sensitivity, 
                                                      requires_four_eyes = @requiresFourEyes, 
                                                      is_active = @isActive,
                                                      data_source_type = @dataSourceType,
                                                      http_endpoint_json = @httpEndpointJson,
                                                      plugin_name = @pluginName,
                                                      description = @description,
                                                      long_description = @longDescription,
                                                      doc_source = @docSource,
                                                      required_filter_columns_json = @requiredFilterColumnsJson,
                                                      texts_json = @textsJson
                                                  WHERE id = @id";
                        updateCmd.Parameters.AddWithValue("@sourceType", metadata.Table.SourceType);
                        updateCmd.Parameters.AddWithValue("@displayName", metadata.Table.DisplayName);
                        updateCmd.Parameters.AddWithValue("@sensitivity", metadata.Table.Sensitivity);
                        updateCmd.Parameters.AddWithValue("@requiresFourEyes", metadata.Table.RequiresFourEyes ? 1 : 0);
                        updateCmd.Parameters.AddWithValue("@isActive", metadata.Table.IsActive ? 1 : 0);
                        updateCmd.Parameters.AddWithValue("@dataSourceType", (int)metadata.Table.DataSourceType);
                        updateCmd.Parameters.AddWithValue("@httpEndpointJson", metadata.Table.HttpEndpoint != null ? JsonSerializer.Serialize(metadata.Table.HttpEndpoint) : (object)DBNull.Value);
                        updateCmd.Parameters.AddWithValue("@pluginName", (object?)metadata.Table.PluginName ?? DBNull.Value);
                        updateCmd.Parameters.AddWithValue("@description", (object?)targetDesc ?? DBNull.Value);
                        updateCmd.Parameters.AddWithValue("@longDescription", (object?)targetLongDesc ?? DBNull.Value);
                        updateCmd.Parameters.AddWithValue("@docSource", (object?)targetDocSource ?? DBNull.Value);
                        updateCmd.Parameters.AddWithValue("@requiredFilterColumnsJson", metadata.RequiredFilterColumns.IsDefaultOrEmpty ? (object)DBNull.Value : JsonSerializer.Serialize(metadata.RequiredFilterColumns));
                        updateCmd.Parameters.AddWithValue("@textsJson", (object?)CatalogLocalizationPersistence.SerializeLocalizedTexts(metadata.Table.Texts) ?? DBNull.Value);
                        updateCmd.Parameters.AddWithValue("@id", tableId.ToString());
                        await updateCmd.ExecuteNonQueryAsync(ct);
                    }
                    else
                    {
                        await selectReader.CloseAsync();
                        structuralChange = true;
                        tableId = metadata.Table.Id == Guid.Empty ? Guid.NewGuid() : metadata.Table.Id;
                        using var insertCmd = conn.CreateCommand();
                        insertCmd.Transaction = tx;
                        insertCmd.CommandText = @"INSERT INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, data_source_type, http_endpoint_json, plugin_name, description, long_description, doc_source, required_filter_columns_json, texts_json)
                                                  VALUES (@id, @sourceType, @sourceName, @schemaName, @tableName, @displayName, @sensitivity, @requiresFourEyes, @isActive, @dataSourceType, @httpEndpointJson, @pluginName, @description, @longDescription, @docSource, @requiredFilterColumnsJson, @textsJson)";
                        insertCmd.Parameters.AddWithValue("@id", tableId.ToString());
                        insertCmd.Parameters.AddWithValue("@sourceType", metadata.Table.SourceType);
                        insertCmd.Parameters.AddWithValue("@sourceName", metadata.Identifier.Domain);
                        insertCmd.Parameters.AddWithValue("@schemaName", metadata.Identifier.Schema);
                        insertCmd.Parameters.AddWithValue("@tableName", metadata.Identifier.TableName);
                        insertCmd.Parameters.AddWithValue("@displayName", metadata.Table.DisplayName);
                        insertCmd.Parameters.AddWithValue("@sensitivity", metadata.Table.Sensitivity);
                        insertCmd.Parameters.AddWithValue("@requiresFourEyes", metadata.Table.RequiresFourEyes ? 1 : 0);
                        insertCmd.Parameters.AddWithValue("@isActive", metadata.Table.IsActive ? 1 : 0);
                        insertCmd.Parameters.AddWithValue("@dataSourceType", (int)metadata.Table.DataSourceType);
                        insertCmd.Parameters.AddWithValue("@httpEndpointJson", metadata.Table.HttpEndpoint != null ? JsonSerializer.Serialize(metadata.Table.HttpEndpoint) : (object)DBNull.Value);
                        insertCmd.Parameters.AddWithValue("@pluginName", (object?)metadata.Table.PluginName ?? DBNull.Value);
                        insertCmd.Parameters.AddWithValue("@description", (object?)metadata.Table.Description ?? DBNull.Value);
                        insertCmd.Parameters.AddWithValue("@longDescription", (object?)metadata.Table.LongDescription ?? DBNull.Value);
                        insertCmd.Parameters.AddWithValue("@docSource", (object?)metadata.Table.DocumentationSource ?? DBNull.Value);
                        insertCmd.Parameters.AddWithValue("@requiredFilterColumnsJson", metadata.RequiredFilterColumns.IsDefaultOrEmpty ? (object)DBNull.Value : JsonSerializer.Serialize(metadata.RequiredFilterColumns));
                        insertCmd.Parameters.AddWithValue("@textsJson", (object?)CatalogLocalizationPersistence.SerializeLocalizedTexts(metadata.Table.Texts) ?? DBNull.Value);
                        await insertCmd.ExecuteNonQueryAsync(ct);

                        // Issue #111 (A-10): the configured default tier is given to a NEW table only; the Hidden default writes no row.
                        if (_options?.Catalog?.DefaultVisibility == DatasetVisibility.Summary)
                        {
                            await DatasetCatalogStore.InsertDefaultAsync(conn, tx, CatalogKeyDefinitionStore.Sqlite, tableId, DatasetVisibility.Summary, ct).ConfigureAwait(false);
                        }
                    }
                }

                var existingColsByName = new Dictionary<string, (Guid Id, string DataType, bool IsSensitive, string? Description, string? LongDescription, string? DocSource)>(StringComparer.OrdinalIgnoreCase);
                var existingMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);
                var colIdByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
                using (var getColsCmd = conn.CreateCommand())
                {
                    getColsCmd.Transaction = tx;
                    getColsCmd.CommandText = @"SELECT c.id, c.column_name, c.data_type, c.is_sensitive, c.description, c.long_description, c.doc_source,
                                                      m.id, m.rule_type, m.pattern_or_format, m.replacement, m.hmac_key_id
                                               FROM TABLE_COLUMNS c
                                               LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
                                               WHERE c.table_id = @tableId";
                    getColsCmd.Parameters.AddWithValue("@tableId", tableId.ToString());
                    using var reader = await getColsCmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        var cId = Guid.Parse(reader.GetString(0));
                        var cName = reader.GetString(1);
                        var cType = reader.GetString(2);
                        var cSens = reader.GetInt32(3) == 1;
                        var cDesc = reader.IsDBNull(4) ? null : reader.GetString(4);
                        var cLongDesc = reader.IsDBNull(5) ? null : reader.GetString(5);
                        var cDocSource = reader.IsDBNull(6) ? null : reader.GetString(6);
                        existingColsByName[cName] = (cId, cType, cSens, cDesc, cLongDesc, cDocSource);
                        colIdByName[cName] = cId;

                        if (!reader.IsDBNull(7))
                        {
                            existingMaskingRules[cName] = new MaskingRule
                            {
                                Id = Guid.Parse(reader.GetString(7)),
                                TableColumnId = cId,
                                RuleType = reader.GetString(8),
                                PatternOrFormat = reader.IsDBNull(9) ? null : reader.GetString(9),
                                Replacement = reader.IsDBNull(10) ? null : reader.GetString(10),
                                HmacKeyId = reader.IsDBNull(11) ? null : reader.GetString(11)
                            };
                        }
                    }
                }

                if (HasMaskingRulesChanged(existingMaskingRules, metadata.ColumnMaskingRules))
                {
                    structuralChange = true;
                }

                var updatedColumns = new List<TableColumn>(metadata.Columns.Count);
                foreach (var col in metadata.Columns)
                {
                    Guid colId;
                    IReadOnlyDictionary<string, string> effectiveMeta = metadata.ReplaceKeyDefinition
                        ? CatalogKeyDefinitionStore.WithKeyMarkers(col.Meta, col.ColumnName, metadata.PrimaryKeyColumns)
                        : col.Meta ?? new Dictionary<string, string>();
                    var metaJson = effectiveMeta.Count > 0 ? JsonSerializer.Serialize(effectiveMeta) : null;
                    var incomingColSource = col.DocumentationSource ?? metadata.Table.DocumentationSource;

                    if (existingColsByName.TryGetValue(col.ColumnName, out var existingCol))
                    {
                        colId = existingCol.Id;

                        if (!string.Equals(existingCol.DataType, col.DataType, StringComparison.OrdinalIgnoreCase) ||
                            existingCol.IsSensitive != col.IsSensitive)
                        {
                            structuralChange = true;
                        }

                        bool canOverwriteColDesc = string.IsNullOrWhiteSpace(existingCol.Description) ||
                                                   (!string.IsNullOrWhiteSpace(col.Description) &&
                                                    ShouldOverwriteDocumentation(existingCol.DocSource, incomingColSource));

                        bool canOverwriteColLongDesc = string.IsNullOrWhiteSpace(existingCol.LongDescription) ||
                                                       (!string.IsNullOrWhiteSpace(col.LongDescription) &&
                                                        ShouldOverwriteDocumentation(existingCol.DocSource, incomingColSource));

                        var targetColDesc = canOverwriteColDesc ? col.Description : existingCol.Description;
                        var targetColLongDesc = canOverwriteColLongDesc ? col.LongDescription : existingCol.LongDescription;
                        var targetColDocSource = canOverwriteColDesc ? (incomingColSource ?? existingCol.DocSource) : existingCol.DocSource;

                        using var updateColCmd = conn.CreateCommand();
                        updateColCmd.Transaction = tx;
                        updateColCmd.CommandText = @"UPDATE TABLE_COLUMNS 
                                                    SET data_type = @dataType, is_sensitive = @isSensitive, description = @description, long_description = @longDescription, doc_source = @docSource, meta_json = @metaJson, display_name = @displayName, texts_json = @textsJson
                                                    WHERE id = @id";
                        updateColCmd.Parameters.AddWithValue("@dataType", col.DataType);
                        updateColCmd.Parameters.AddWithValue("@isSensitive", col.IsSensitive ? 1 : 0);
                        updateColCmd.Parameters.AddWithValue("@description", (object?)targetColDesc ?? DBNull.Value);
                        updateColCmd.Parameters.AddWithValue("@longDescription", (object?)targetColLongDesc ?? DBNull.Value);
                        updateColCmd.Parameters.AddWithValue("@docSource", (object?)targetColDocSource ?? DBNull.Value);
                        updateColCmd.Parameters.AddWithValue("@metaJson", (object?)metaJson ?? DBNull.Value);
                        updateColCmd.Parameters.AddWithValue("@displayName", (object?)col.DisplayName ?? DBNull.Value);
                        updateColCmd.Parameters.AddWithValue("@textsJson", (object?)CatalogLocalizationPersistence.SerializeLocalizedTexts(col.Texts) ?? DBNull.Value);
                        updateColCmd.Parameters.AddWithValue("@id", colId.ToString());
                        await updateColCmd.ExecuteNonQueryAsync(ct);
                    }
                    else
                    {
                        structuralChange = true;
                        colId = col.Id == Guid.Empty ? Guid.NewGuid() : col.Id;
                        using var insertColCmd = conn.CreateCommand();
                        insertColCmd.Transaction = tx;
                        insertColCmd.CommandText = @"INSERT INTO TABLE_COLUMNS (id, table_id, column_name, data_type, is_sensitive, description, long_description, doc_source, meta_json, display_name, texts_json)
                                                    VALUES (@id, @tableId, @columnName, @dataType, @isSensitive, @description, @longDescription, @docSource, @metaJson, @displayName, @textsJson)";
                        insertColCmd.Parameters.AddWithValue("@id", colId.ToString());
                        insertColCmd.Parameters.AddWithValue("@tableId", tableId.ToString());
                        insertColCmd.Parameters.AddWithValue("@columnName", col.ColumnName);
                        insertColCmd.Parameters.AddWithValue("@dataType", col.DataType);
                        insertColCmd.Parameters.AddWithValue("@isSensitive", col.IsSensitive ? 1 : 0);
                        insertColCmd.Parameters.AddWithValue("@description", (object?)col.Description ?? DBNull.Value);
                        insertColCmd.Parameters.AddWithValue("@longDescription", (object?)col.LongDescription ?? DBNull.Value);
                        insertColCmd.Parameters.AddWithValue("@docSource", (object?)incomingColSource ?? DBNull.Value);
                        insertColCmd.Parameters.AddWithValue("@metaJson", (object?)metaJson ?? DBNull.Value);
                        insertColCmd.Parameters.AddWithValue("@displayName", (object?)col.DisplayName ?? DBNull.Value);
                        insertColCmd.Parameters.AddWithValue("@textsJson", (object?)CatalogLocalizationPersistence.SerializeLocalizedTexts(col.Texts) ?? DBNull.Value);
                        await insertColCmd.ExecuteNonQueryAsync(ct);
                    }

                    colIdByName[col.ColumnName] = colId;
                    updatedColumns.Add(new TableColumn
                    {
                        Id = colId,
                        TableId = tableId,
                        ColumnName = col.ColumnName,
                        DisplayName = col.DisplayName,
                        DataType = col.DataType,
                        IsSensitive = col.IsSensitive,
                        Description = col.Description,
                        LongDescription = col.LongDescription,
                        Texts = col.Texts,
                        DocumentationSource = incomingColSource,
                        Meta = effectiveMeta
                    });

                    using (var delMaskCmd = conn.CreateCommand())
                    {
                        delMaskCmd.Transaction = tx;
                        delMaskCmd.CommandText = "DELETE FROM COLUMN_MASKING_RULES WHERE table_column_id = @colId";
                        delMaskCmd.Parameters.AddWithValue("@colId", colId.ToString());
                        await delMaskCmd.ExecuteNonQueryAsync(ct);
                    }

                    if (metadata.ColumnMaskingRules.TryGetValue(col.ColumnName, out var maskRule))
                    {
                        using var insertMaskCmd = conn.CreateCommand();
                        insertMaskCmd.Transaction = tx;
                        insertMaskCmd.CommandText = @"INSERT INTO COLUMN_MASKING_RULES (id, table_column_id, rule_type, pattern_or_format, replacement, hmac_key_id)
                                                      VALUES (@id, @colId, @ruleType, @pattern, @replacement, @hmacKeyId)";
                        insertMaskCmd.Parameters.AddWithValue("@id", (maskRule.Id == Guid.Empty ? Guid.NewGuid() : maskRule.Id).ToString());
                        insertMaskCmd.Parameters.AddWithValue("@colId", colId.ToString());
                        insertMaskCmd.Parameters.AddWithValue("@ruleType", maskRule.RuleType);
                        insertMaskCmd.Parameters.AddWithValue("@pattern", (object?)maskRule.PatternOrFormat ?? DBNull.Value);
                        insertMaskCmd.Parameters.AddWithValue("@replacement", (object?)maskRule.Replacement ?? DBNull.Value);
                        insertMaskCmd.Parameters.AddWithValue("@hmacKeyId", (object?)maskRule.HmacKeyId ?? DBNull.Value);
                        await insertMaskCmd.ExecuteNonQueryAsync(ct);
                    }
                }

                if (metadata.ReplaceKeyDefinition)
                {
                    structuralChange = true;
                    await CatalogKeyDefinitionStore.ReplaceAsync(conn, tx, CatalogKeyDefinitionStore.Sqlite, tableId, metadata, colIdByName, ct).ConfigureAwait(false);
                    persistedPks = metadata.PrimaryKeyColumns;
                    persistedIndexes = metadata.Indexes;
                }
                else
                {
                    try
                    {
                        (persistedPks, persistedIndexes) = await CatalogKeyDefinitionStore.ReadAsync(
                            conn, tx, CatalogKeyDefinitionStore.Sqlite, metadata.Identifier, tableId, updatedColumns, ct).ConfigureAwait(false);
                    }
                    catch (TableMetadataUnavailableException)
                    {
                        // The stored definition is invalid or not covered by the columns of this write: the upsert neither fails nor touches it.
                        persistedPks = metadata.PrimaryKeyColumns;
                        persistedIndexes = metadata.Indexes;
                    }
                }

                await IncrementTableEpochInternalAsync(conn, metadata.Identifier, tx, ct);
                var structureDelta = structuralChange ? 1L : 0L;
                var contentDelta = structuralChange ? 0L : 1L;
                var newVersion = await BumpCatalogVersionInternalAsync(conn, tx, structureDelta, contentDelta, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct);
                await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }

            QueueMetadataInvalidation(metadata.Identifier);
            await _epochValidationService.InvalidateEpochAsync(metadata.Identifier, ct);

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
                Indexes = persistedIndexes
            };
        }
    }

    public async Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
        TableIdentifier table,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<TableIndexDefinition> indexes,
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
                    conn, tx, CatalogKeyDefinitionStore.Sqlite, table, primaryKey, indexes,
                    () => IncrementTableEpochInternalAsync(conn, table, tx, ct), ct).ConfigureAwait(false);
                if (change is null)
                {
                    await tx.RollbackAsync(ct);
                    return null;
                }

                var newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 1, 0, ct).ConfigureAwait(false);
                // Queued inside the write scope: the scope disposal applies it under the write gate.
                QueueMetadataInvalidation(table);
                await tx.CommitAsync(ct);
                await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        await _epochValidationService.InvalidateEpochAsync(table, ct);
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
                    await tx.RollbackAsync(ct);
                    return null;
                }

                var newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 1, 0, ct).ConfigureAwait(false);
                // Queued inside the write scope: the scope disposal applies it under the write gate.
                QueueMetadataInvalidation(table);
                await tx.CommitAsync(ct);
                await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        await _epochValidationService.InvalidateEpochAsync(table, ct);
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

    public Task<DatasetCatalogChange?> SetDatasetVisibilityAsync(
        TableIdentifier table, DatasetVisibility visibility, Sid actor, CancellationToken ct = default) =>
        WriteCatalogSettingsAsync(table, (conn, tx) =>
            DatasetCatalogStore.SetVisibilityAsync(conn, tx, CatalogKeyDefinitionStore.Sqlite, table, visibility, actor, ct), ct);

    /// <summary>Issue #111: one transaction (before read, write, after read); no epoch (tags are not policy inputs); the local metadata entry is dropped after the commit.</summary>
    private async Task<DatasetCatalogChange?> WriteCatalogSettingsAsync(
        TableIdentifier table, Func<SqliteConnection, SqliteTransaction, Task<DatasetCatalogChange?>> write, CancellationToken ct)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        using var tx = conn.BeginTransaction();
        try
        {
            var change = await write(conn, tx).ConfigureAwait(false);
            if (change is null)
            {
                await tx.RollbackAsync(ct);
                return null;
            }

            Autheris.Application.Catalog.CatalogVersion? newVersion = null;
            if (change.Changed)
            {
                newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 0, 1, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct);
            if (change.Changed)
            {
                QueueMetadataInvalidation(table);
                if (newVersion.HasValue)
                {
                    await PublishCatalogChangedEventAsync(newVersion.Value, ct).ConfigureAwait(false);
                }
            }

            return change;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
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
        cmd.Parameters.AddWithValue("@domain", table.Domain);
        cmd.Parameters.AddWithValue("@schema", table.Schema);
        cmd.Parameters.AddWithValue("@table", table.TableName);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long l ? l : Convert.ToInt64(result ?? 1L, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<TableIdentifier?> GetActiveTableIdentifierAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct, snapshot: false).ConfigureAwait(false);
        using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT source_name, schema_name, table_name FROM TABLES WHERE source_name = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE AND is_active = 1";
        cmd.Parameters.AddWithValue("@domain", table.Domain);
        cmd.Parameters.AddWithValue("@schema", table.Schema);
        cmd.Parameters.AddWithValue("@table", table.TableName);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
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
        await using var scope = await ReadScopeAsync(ct, snapshot: false).ConfigureAwait(false);
        using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT source_name, schema_name, table_name FROM TABLES WHERE id = @id AND is_active = 1";
        cmd.Parameters.AddWithValue("@id", tableId.ToString());
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
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
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT epoch FROM POLICY_EPOCHS
                                WHERE domain = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@domain", table.Domain);
            cmd.Parameters.AddWithValue("@schema", table.Schema);
            cmd.Parameters.AddWithValue("@table", table.TableName);

            var result = await cmd.ExecuteScalarAsync(ct);
            return result is long l ? l : (result is int i ? i : 1L);
        }
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
                cmd.Parameters.AddWithValue("@domain", table.Domain);
                cmd.Parameters.AddWithValue("@schema", table.Schema);
                cmd.Parameters.AddWithValue("@table", table.TableName);
                cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));

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
            cmd.Parameters.AddWithValue("@domain", table.Domain);
            cmd.Parameters.AddWithValue("@schema", table.Schema);
            cmd.Parameters.AddWithValue("@table", table.TableName);
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
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
                insertCmd.Parameters.AddWithValue("@domain", table.Domain);
                insertCmd.Parameters.AddWithValue("@schema", table.Schema);
                insertCmd.Parameters.AddWithValue("@table", table.TableName);
                insertCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                await insertCmd.ExecuteNonQueryAsync(ct);
            }
        }

        if (transaction == null)
        {
            await _epochValidationService.InvalidateEpochAsync(table, ct);
        }
    }

    public async Task DeletePolicyEpochForTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"DELETE FROM POLICY_EPOCHS WHERE domain = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@domain", table.Domain);
            cmd.Parameters.AddWithValue("@schema", table.Schema);
            cmd.Parameters.AddWithValue("@table", table.TableName);
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
            cmd.CommandText = @"INSERT INTO DATA_OWNER_DELEGATIONS (id, data_owner_id, delegate_sid, valid_from, valid_to, reason)
                                VALUES (@id, @ownerId, @delSid, @from, @to, @reason)";
            cmd.Parameters.AddWithValue("@id", delegation.Id.ToString());
            cmd.Parameters.AddWithValue("@ownerId", delegation.DataOwnerId.ToString());
            cmd.Parameters.AddWithValue("@delSid", delegation.DelegateSid.Value);
            cmd.Parameters.AddWithValue("@from", delegation.ValidFrom.ToString("O"));
            cmd.Parameters.AddWithValue("@to", delegation.ValidTo.ToString("O"));
            cmd.Parameters.AddWithValue("@reason", delegation.Reason);

            await cmd.ExecuteNonQueryAsync(ct);
            var newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 0, 1, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct);
            await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
            return delegation;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            var owners = new List<DataOwner>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT o.id, o.ad_sid, o.ad_account, o.display_name, o.email, o.is_active
                                FROM DATA_OWNERS o
                                JOIN TABLE_OWNERS tow ON o.id = tow.data_owner_id
                                JOIN TABLES t ON tow.table_id = t.id
                                WHERE t.source_name = @domain COLLATE NOCASE AND t.schema_name = @schema COLLATE NOCASE AND t.table_name = @table COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@domain", table.Domain);
            cmd.Parameters.AddWithValue("@schema", table.Schema);
            cmd.Parameters.AddWithValue("@table", table.TableName);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
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
    }

    public async Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            return await IsAuthorizedApproverForTableInternalAsync(conn, table, approverSid, ct);
        }
    }

    private Task<bool> IsAuthorizedApproverForTableInternalAsync(SqliteConnection conn, TableIdentifier table, Sid approverSid, CancellationToken ct)
        => IsAuthorizedApproverForTableInternalAsync(conn, table, approverSid, null, ct);

    /// <summary>Review R4-4 (rest): resolves SID, account or e-mail spellings to stable DATA_OWNERS ids (caller holds the lock).</summary>
    private async Task<IReadOnlyCollection<string>> ResolveDataOwnerIdsInternalAsync(SqliteConnection conn, IEnumerable<string> identifiers, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var identifier in identifiers.Where(i => !string.IsNullOrWhiteSpace(i)))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT id FROM DATA_OWNERS
                                WHERE ad_sid = @p COLLATE NOCASE OR ad_account = @p COLLATE NOCASE OR email = @p COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@p", identifier);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                ids.Add(reader.GetString(0));
            }
        }

        return ids;
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

    private async Task<bool> IsAuthorizedApproverForTableInternalAsync(SqliteConnection conn, TableIdentifier table, Sid approverSid, string? itsmApproverAccount, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 1
            FROM TABLE_OWNERS tow
            JOIN DATA_OWNERS o ON tow.data_owner_id = o.id
            JOIN TABLES t ON tow.table_id = t.id
            WHERE t.source_name = @domain COLLATE NOCASE AND t.schema_name = @schema COLLATE NOCASE AND t.table_name = @table COLLATE NOCASE
              AND (o.ad_sid = @apprSid OR (@itsmAccount IS NOT NULL AND (o.ad_account = @itsmAccount COLLATE NOCASE OR o.email = @itsmAccount COLLATE NOCASE))) AND o.is_active = 1
            UNION
            SELECT 1
            FROM DATA_OWNER_DELEGATIONS del
            JOIN TABLE_OWNERS tow ON del.data_owner_id = tow.data_owner_id
            JOIN TABLES t ON tow.table_id = t.id
            WHERE t.source_name = @domain COLLATE NOCASE AND t.schema_name = @schema COLLATE NOCASE AND t.table_name = @table COLLATE NOCASE
              AND (del.delegate_sid = @apprSid OR (@itsmAccount IS NOT NULL AND del.delegate_sid = @itsmAccount)) AND del.valid_from <= @now AND @now < del.valid_to
            UNION
            SELECT 1
            FROM ROLE_MEMBERS rm
            JOIN ROLES r ON rm.role_id = r.id
            WHERE (rm.member_sid = @apprSid OR (@itsmAccount IS NOT NULL AND rm.member_sid = @itsmAccount)) AND r.role_name IN ('GovernanceAdmin', 'ClusterAdmin')
            LIMIT 1;";
        cmd.Parameters.AddWithValue("@domain", table.Domain);
        cmd.Parameters.AddWithValue("@schema", table.Schema);
        cmd.Parameters.AddWithValue("@table", table.TableName);
        cmd.Parameters.AddWithValue("@apprSid", approverSid.Value);
        cmd.Parameters.AddWithValue("@itsmAccount", (object?)itsmApproverAccount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));

        var result = await cmd.ExecuteScalarAsync(ct);
        return result != null && result != DBNull.Value;
    }

    public async Task<IReadOnlySet<string>> GetTransitiveRolesAsync(Sid subjectSid, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT r.role_name
                                FROM ROLES r
                                JOIN ROLE_MEMBERS m ON r.id = m.role_id
                                WHERE m.member_sid = @sid";
            cmd.Parameters.AddWithValue("@sid", subjectSid.Value);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                roles.Add(reader.GetString(0));
            }
            return roles;
        }
    }

    public async Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;

        // One statement: a reader never sees a mixed key set (SEC-REL151-06). Key rows are only reached through the join (SEC-REL151-08).
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT r.id, r.parent_table_id, r.child_table_id, r.relation_name, r.cardinality,
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
        cmd.Parameters.AddWithValue("@pDomain", parentTable.Domain);
        cmd.Parameters.AddWithValue("@pSchema", parentTable.Schema);
        cmd.Parameters.AddWithValue("@pTable", parentTable.TableName);

        var rows = new List<RelationRowData>();
        using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
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
                    !reader.IsDBNull(14) && reader.GetInt64(14) != 0));
            }
        }

        return RelationRowAssembler.Assemble(rows, _logger);
    }

    public async Task<RelationWriteResult> CreateRelationAsync(TableRelation relation, CancellationToken ct = default)
    {
        RelationWriteSupport.ValidateKeysOrThrow(relation);

        // The write scope holds the single-writer gate; one explicit transaction covers relation row and key rows (SEC-REL151-06).
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
            cmd.CommandText = @"SELECT id FROM TABLE_RELATIONS WHERE parent_table_id = @pId AND child_table_id = @cId AND relation_name = @relName";
            cmd.Parameters.AddWithValue("@pId", parentTableId);
            cmd.Parameters.AddWithValue("@cId", childTableId);
            cmd.Parameters.AddWithValue("@relName", relation.RelationName);
            created = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is null;
        }

        // Natural-key upsert: only discovery-owned columns are in the SET list (I-18 keeps allow_cross_data_source out of it).
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO TABLE_RELATIONS (id, parent_table_id, child_table_id, relation_name, cardinality, allow_cross_data_source)
                                VALUES (@id, @pId, @cId, @relName, @card, @allowCross)
                                ON CONFLICT (parent_table_id, child_table_id, relation_name) DO UPDATE SET cardinality = excluded.cardinality
                                RETURNING id";
            cmd.Parameters.AddWithValue("@id", relation.Id.ToString());
            cmd.Parameters.AddWithValue("@pId", parentTableId);
            cmd.Parameters.AddWithValue("@cId", childTableId);
            cmd.Parameters.AddWithValue("@relName", relation.RelationName);
            cmd.Parameters.AddWithValue("@card", relation.Cardinality.ToString());
            cmd.Parameters.AddWithValue("@allowCross", relation.AllowCrossDataSource ? 1 : 0);
            storedId = (string)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        var stored = new List<(string Parent, string Child)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT parent_column, child_column FROM TABLE_RELATION_KEYS WHERE relation_id = @id ORDER BY key_ordinal";
            cmd.Parameters.AddWithValue("@id", storedId);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                stored.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var keysChanged = !created && !RelationWriteSupport.SamePairs(stored, parentKeys, childKeys);

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM TABLE_RELATION_KEYS WHERE relation_id = @id";
            cmd.Parameters.AddWithValue("@id", storedId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        for (var i = 0; i < parentKeys.Count; i++)
        {
            using var cmd = conn.CreateCommand();
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
        tx.Commit();
        await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
        return new RelationWriteResult(Guid.Parse(storedId), created, keysChanged);
    }

    /// <summary>Resolves an active table id from its identifier: zero or several matches throw (SEC-REL151-07, no LIMIT 1).</summary>
    private static async Task<string> ResolveActiveTableIdAsync(SqliteConnection conn, SqliteTransaction tx, string relationName, TableIdentifier table, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"SELECT id FROM TABLES
                            WHERE source_name = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE AND is_active = 1";
        cmd.Parameters.AddWithValue("@domain", table.Domain);
        cmd.Parameters.AddWithValue("@schema", table.Schema);
        cmd.Parameters.AddWithValue("@table", table.TableName);
        var ids = new List<string>(2);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids.Count == 1 ? ids[0] : throw RelationWriteSupport.TableNotResolved(relationName, table, ids.Count);
    }

    private static async Task<List<string>> LoadColumnNamesAsync(SqliteConnection conn, SqliteTransaction tx, string tableId, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT column_name FROM TABLE_COLUMNS WHERE table_id = @t";
        cmd.Parameters.AddWithValue("@t", tableId);
        var names = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    public async Task<bool> SetRelationCrossDataSourceOptInAsync(Guid relationId, bool allowCrossDataSource, CancellationToken ct = default)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        using var tx = conn.BeginTransaction();
        try
        {
            using var cmd = conn.CreateCommand();
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
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT structure_version, content_version FROM CATALOG_STATE WHERE id = 1;";
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new Autheris.Application.Catalog.CatalogVersion(reader.GetInt64(0), reader.GetInt64(1));
        }
        return new Autheris.Application.Catalog.CatalogVersion(1, 1);
    }

    public async Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;

        using var cmd = conn.CreateCommand();
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
        using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
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
                    !reader.IsDBNull(14) && reader.GetInt64(14) != 0));
            }
        }

        return RelationRowAssembler.Assemble(rows, _logger);
    }

    private async Task<Autheris.Application.Catalog.CatalogVersion> BumpCatalogVersionInternalAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        long structureDelta,
        long contentDelta,
        CancellationToken ct)
    {
        if (structureDelta < 0 || contentDelta < 0 || (structureDelta == 0 && contentDelta == 0))
        {
            throw new ArgumentException("Version deltas must be non-negative with at least one positive delta.");
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"UPDATE CATALOG_STATE
                            SET structure_version = structure_version + @structureDelta,
                                content_version = content_version + @contentDelta,
                                updated_at = @now
                            WHERE id = 1
                            RETURNING structure_version, content_version;";
        cmd.Parameters.AddWithValue("@structureDelta", structureDelta);
        cmd.Parameters.AddWithValue("@contentDelta", contentDelta);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
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

    internal static bool HasMaskingRulesChanged(
        IReadOnlyDictionary<string, MaskingRule> existing,
        IReadOnlyDictionary<string, MaskingRule>? incoming)
    {
        var incomingRules = incoming ?? (IReadOnlyDictionary<string, MaskingRule>)System.Collections.Immutable.ImmutableDictionary<string, MaskingRule>.Empty;
        if (existing.Count != incomingRules.Count)
        {
            return true;
        }

        foreach (var (colName, incomingRule) in incomingRules)
        {
            if (!existing.TryGetValue(colName, out var existingRule))
            {
                return true;
            }

            if (!string.Equals(existingRule.RuleType, incomingRule.RuleType, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existingRule.PatternOrFormat, incomingRule.PatternOrFormat, StringComparison.Ordinal) ||
                !string.Equals(existingRule.Replacement, incomingRule.Replacement, StringComparison.Ordinal) ||
                !string.Equals(existingRule.HmacKeyId, incomingRule.HmacKeyId, StringComparison.Ordinal) ||
                existingRule.IsHmac != incomingRule.IsHmac)
            {
                return true;
            }
        }

        return false;
    }
}
