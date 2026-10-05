using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    _ => prop.Value.GetRawText()
                };
            }
            return dict;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var cacheKey = table.ToString().ToLowerInvariant();
        var nowTicks = DateTime.UtcNow.Ticks;
        if (_metadataCache.TryGetValue(cacheKey, out var entry) && (nowTicks - entry.CachedAtTicks < MetadataCacheTtlTicks))
        {
            return entry.Metadata;
        }

        await _lock.WaitAsync(ct);
        try
        {
            nowTicks = DateTime.UtcNow.Ticks;
            if (_metadataCache.TryGetValue(cacheKey, out entry) && (nowTicks - entry.CachedAtTicks < MetadataCacheTtlTicks))
            {
                return entry.Metadata;
            }

            Guid? tableId = null;
            Table? tableEntity = null;

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, data_source_type, http_endpoint_json, plugin_name, description, long_description, doc_source
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
                        DocumentationSource = reader.IsDBNull(14) ? null : reader.GetString(14)
                    };
                }
            }

            if (tableEntity == null || !tableId.HasValue) return null;

            var columns = new List<TableColumn>();
            var maskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT c.id, c.column_name, c.data_type, c.is_sensitive,
                                           m.id, m.rule_type, m.pattern_or_format, m.replacement, m.hmac_key_id,
                                           c.description, c.long_description, c.meta_json, c.doc_source
                                    FROM TABLE_COLUMNS c
                                    LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
                                    WHERE c.table_id = @tid";
                cmd.Parameters.AddWithValue("@tid", tableId.Value.ToString());

                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var colId = Guid.Parse(reader.GetString(0));
                    var colName = reader.GetString(1);
                    var metaJson = reader.IsDBNull(11) ? null : reader.GetString(11);
                    var metaDict = ParseColumnMetaJson(metaJson);

                    var col = new TableColumn
                    {
                        Id = colId,
                        TableId = tableId.Value,
                        ColumnName = colName,
                        DataType = reader.GetString(2),
                        IsSensitive = reader.GetInt32(3) == 1,
                        Description = reader.IsDBNull(9) ? null : reader.GetString(9),
                        LongDescription = reader.IsDBNull(10) ? null : reader.GetString(10),
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

            var result = new TableMetadata
            {
                Table = tableEntity,
                Identifier = table,
                Columns = columns,
                ColumnMaskingRules = maskingRules
            };

            _metadataCache[cacheKey] = (result, DateTime.UtcNow.Ticks);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var results = new List<TableMetadata>();
            var tableRows = new List<(Guid id, string schema, string name, Table table)>();

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT t.id, t.source_type, t.source_name, t.schema_name, t.table_name,
                                           t.display_name, t.sensitivity, t.requires_four_eyes, t.is_active,
                                           COALESCE(t.source_name, p.domain, 'default') as domain,
                                           t.data_source_type, t.http_endpoint_json, t.plugin_name,
                                           t.description, t.long_description, t.doc_source
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
                        DocumentationSource = reader.IsDBNull(15) ? null : reader.GetString(15)
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
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT id, table_id, column_name, data_type, is_sensitive, description, long_description, meta_json, doc_source FROM TABLE_COLUMNS";
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                  try
                  {
                    var tid = Guid.Parse(reader.GetString(1));
                    if (!columnsByTable.TryGetValue(tid, out var list))
                    {
                        list = new List<TableColumn>();
                        columnsByTable[tid] = list;
                    }
                    var metaJson = reader.IsDBNull(7) ? null : reader.GetString(7);
                    var metaDict = ParseColumnMetaJson(metaJson);

                    list.Add(new TableColumn
                    {
                        Id = Guid.Parse(reader.GetString(0)),
                        TableId = tid,
                        ColumnName = reader.GetString(2),
                        DataType = reader.GetString(3),
                        IsSensitive = reader.GetInt32(4) == 1,
                        Description = reader.IsDBNull(5) ? null : reader.GetString(5),
                        LongDescription = reader.IsDBNull(6) ? null : reader.GetString(6),
                        DocumentationSource = reader.IsDBNull(8) ? null : reader.GetString(8),
                        Meta = metaDict
                    });
                  }
                  catch (Exception ex) when (ex is InvalidCastException or FormatException or JsonException or ArgumentException)
                  {
                    _logger?.LogWarning(ex, "Catalog row {RowId} in TABLE_COLUMNS is invalid and was skipped.", SafeValue(reader, 0));
                  }
                }
            }

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

                var columns = columnsByTable.TryGetValue(id, out var cols) ? cols : new List<TableColumn>();

                results.Add(new TableMetadata
                {
                    Table = table,
                    Identifier = identifier,
                    Columns = columns,
                    ColumnMaskingRules = new Dictionary<string, MaskingRule>()
                });
            }

            return results;
        }
        finally
        {
            _lock.Release();
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
        await _lock.WaitAsync(ct);
        try
        {
            Guid tableId;
            using var tx = _connection.BeginTransaction();
            try
            {
                using (var selectCmd = _connection.CreateCommand())
                {
                    selectCmd.Transaction = tx;
                    selectCmd.CommandText = @"SELECT id, description, long_description, doc_source FROM TABLES 
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
                        var existingDesc = selectReader.IsDBNull(1) ? null : selectReader.GetString(1);
                        var existingLongDesc = selectReader.IsDBNull(2) ? null : selectReader.GetString(2);
                        var existingDocSource = selectReader.IsDBNull(3) ? null : selectReader.GetString(3);
                        await selectReader.CloseAsync();

                        bool canOverwriteDesc = string.IsNullOrWhiteSpace(existingDesc) ||
                                                (!string.IsNullOrWhiteSpace(metadata.Table.Description) &&
                                                 ShouldOverwriteDocumentation(existingDocSource, metadata.Table.DocumentationSource));

                        bool canOverwriteLongDesc = string.IsNullOrWhiteSpace(existingLongDesc) ||
                                                    (!string.IsNullOrWhiteSpace(metadata.Table.LongDescription) &&
                                                     ShouldOverwriteDocumentation(existingDocSource, metadata.Table.DocumentationSource));

                        var targetDesc = canOverwriteDesc ? metadata.Table.Description : existingDesc;
                        var targetLongDesc = canOverwriteLongDesc ? metadata.Table.LongDescription : existingLongDesc;
                        var targetDocSource = canOverwriteDesc ? (metadata.Table.DocumentationSource ?? existingDocSource) : existingDocSource;

                        using var updateCmd = _connection.CreateCommand();
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
                                                      doc_source = @docSource
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
                        updateCmd.Parameters.AddWithValue("@id", tableId.ToString());
                        await updateCmd.ExecuteNonQueryAsync(ct);
                    }
                    else
                    {
                        await selectReader.CloseAsync();
                        tableId = metadata.Table.Id == Guid.Empty ? Guid.NewGuid() : metadata.Table.Id;
                        using var insertCmd = _connection.CreateCommand();
                        insertCmd.Transaction = tx;
                        insertCmd.CommandText = @"INSERT INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, data_source_type, http_endpoint_json, plugin_name, description, long_description, doc_source)
                                                  VALUES (@id, @sourceType, @sourceName, @schemaName, @tableName, @displayName, @sensitivity, @requiresFourEyes, @isActive, @dataSourceType, @httpEndpointJson, @pluginName, @description, @longDescription, @docSource)";
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
                        await insertCmd.ExecuteNonQueryAsync(ct);
                    }
                }

                var existingColsByName = new Dictionary<string, (Guid Id, string? Description, string? LongDescription, string? DocSource)>(StringComparer.OrdinalIgnoreCase);
                using (var getColsCmd = _connection.CreateCommand())
                {
                    getColsCmd.Transaction = tx;
                    getColsCmd.CommandText = "SELECT id, column_name, description, long_description, doc_source FROM TABLE_COLUMNS WHERE table_id = @tableId";
                    getColsCmd.Parameters.AddWithValue("@tableId", tableId.ToString());
                    using var reader = await getColsCmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        var cId = Guid.Parse(reader.GetString(0));
                        var cName = reader.GetString(1);
                        var cDesc = reader.IsDBNull(2) ? null : reader.GetString(2);
                        var cLongDesc = reader.IsDBNull(3) ? null : reader.GetString(3);
                        var cDocSource = reader.IsDBNull(4) ? null : reader.GetString(4);
                        existingColsByName[cName] = (cId, cDesc, cLongDesc, cDocSource);
                    }
                }

                foreach (var col in metadata.Columns)
                {
                    Guid colId;
                    var metaJson = col.Meta.Count > 0 ? JsonSerializer.Serialize(col.Meta) : null;
                    var incomingColSource = col.DocumentationSource ?? metadata.Table.DocumentationSource;

                    if (existingColsByName.TryGetValue(col.ColumnName, out var existingCol))
                    {
                        colId = existingCol.Id;

                        bool canOverwriteColDesc = string.IsNullOrWhiteSpace(existingCol.Description) ||
                                                   (!string.IsNullOrWhiteSpace(col.Description) &&
                                                    ShouldOverwriteDocumentation(existingCol.DocSource, incomingColSource));

                        bool canOverwriteColLongDesc = string.IsNullOrWhiteSpace(existingCol.LongDescription) ||
                                                       (!string.IsNullOrWhiteSpace(col.LongDescription) &&
                                                        ShouldOverwriteDocumentation(existingCol.DocSource, incomingColSource));

                        var targetColDesc = canOverwriteColDesc ? col.Description : existingCol.Description;
                        var targetColLongDesc = canOverwriteColLongDesc ? col.LongDescription : existingCol.LongDescription;
                        var targetColDocSource = canOverwriteColDesc ? (incomingColSource ?? existingCol.DocSource) : existingCol.DocSource;

                        using var updateColCmd = _connection.CreateCommand();
                        updateColCmd.Transaction = tx;
                        updateColCmd.CommandText = @"UPDATE TABLE_COLUMNS 
                                                    SET data_type = @dataType, is_sensitive = @isSensitive, description = @description, long_description = @longDescription, doc_source = @docSource, meta_json = @metaJson
                                                    WHERE id = @id";
                        updateColCmd.Parameters.AddWithValue("@dataType", col.DataType);
                        updateColCmd.Parameters.AddWithValue("@isSensitive", col.IsSensitive ? 1 : 0);
                        updateColCmd.Parameters.AddWithValue("@description", (object?)targetColDesc ?? DBNull.Value);
                        updateColCmd.Parameters.AddWithValue("@longDescription", (object?)targetColLongDesc ?? DBNull.Value);
                        updateColCmd.Parameters.AddWithValue("@docSource", (object?)targetColDocSource ?? DBNull.Value);
                        updateColCmd.Parameters.AddWithValue("@metaJson", (object?)metaJson ?? DBNull.Value);
                        updateColCmd.Parameters.AddWithValue("@id", colId.ToString());
                        await updateColCmd.ExecuteNonQueryAsync(ct);
                    }
                    else
                    {
                        colId = col.Id == Guid.Empty ? Guid.NewGuid() : col.Id;
                        using var insertColCmd = _connection.CreateCommand();
                        insertColCmd.Transaction = tx;
                        insertColCmd.CommandText = @"INSERT INTO TABLE_COLUMNS (id, table_id, column_name, data_type, is_sensitive, description, long_description, doc_source, meta_json)
                                                    VALUES (@id, @tableId, @columnName, @dataType, @isSensitive, @description, @longDescription, @docSource, @metaJson)";
                        insertColCmd.Parameters.AddWithValue("@id", colId.ToString());
                        insertColCmd.Parameters.AddWithValue("@tableId", tableId.ToString());
                        insertColCmd.Parameters.AddWithValue("@columnName", col.ColumnName);
                        insertColCmd.Parameters.AddWithValue("@dataType", col.DataType);
                        insertColCmd.Parameters.AddWithValue("@isSensitive", col.IsSensitive ? 1 : 0);
                        insertColCmd.Parameters.AddWithValue("@description", (object?)col.Description ?? DBNull.Value);
                        insertColCmd.Parameters.AddWithValue("@longDescription", (object?)col.LongDescription ?? DBNull.Value);
                        insertColCmd.Parameters.AddWithValue("@docSource", (object?)incomingColSource ?? DBNull.Value);
                        insertColCmd.Parameters.AddWithValue("@metaJson", (object?)metaJson ?? DBNull.Value);
                        await insertColCmd.ExecuteNonQueryAsync(ct);
                    }

                    if (metadata.ColumnMaskingRules.TryGetValue(col.ColumnName, out var maskRule))
                    {
                        using var delMaskCmd = _connection.CreateCommand();
                        delMaskCmd.Transaction = tx;
                        delMaskCmd.CommandText = "DELETE FROM COLUMN_MASKING_RULES WHERE table_column_id = @colId";
                        delMaskCmd.Parameters.AddWithValue("@colId", colId.ToString());
                        await delMaskCmd.ExecuteNonQueryAsync(ct);

                        using var insertMaskCmd = _connection.CreateCommand();
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

                await IncrementTableEpochInternalAsync(metadata.Identifier, tx, ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }

            _metadataCache.TryRemove(metadata.Identifier.ToString().ToLowerInvariant(), out _);
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
                PluginName = metadata.Table.PluginName
            };

            return new TableMetadata
            {
                Identifier = metadata.Identifier,
                Table = updatedTable,
                Columns = metadata.Columns,
                ColumnMaskingRules = metadata.ColumnMaskingRules,
                PrimaryKeyColumns = metadata.PrimaryKeyColumns
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<long> GetTableEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"SELECT epoch FROM POLICY_EPOCHS
                                WHERE domain = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@domain", table.Domain);
            cmd.Parameters.AddWithValue("@schema", table.Schema);
            cmd.Parameters.AddWithValue("@table", table.TableName);

            var result = await cmd.ExecuteScalarAsync(ct);
            return result is long l ? l : (result is int i ? i : 1L);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<long> IncrementTableEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            long newEpoch;
            using (var cmd = _connection.CreateCommand())
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

            _metadataCache.TryRemove(table.ToString().ToLowerInvariant(), out _);
            await _epochValidationService.InvalidateEpochAsync(table, ct);
            return newEpoch;
        }
        finally
        {
            _lock.Release();
        }
    }


    private Task IncrementTableEpochInternalAsync(TableIdentifier table, CancellationToken ct) =>
        IncrementTableEpochInternalAsync(table, null, ct);

    private async Task IncrementTableEpochInternalAsync(TableIdentifier table, SqliteTransaction? transaction, CancellationToken ct)
    {
        _metadataCache.TryRemove(table.ToString().ToLowerInvariant(), out _);
        using (var cmd = _connection.CreateCommand())
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
                using var insertCmd = _connection.CreateCommand();
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
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"DELETE FROM POLICY_EPOCHS WHERE domain = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@domain", table.Domain);
            cmd.Parameters.AddWithValue("@schema", table.Schema);
            cmd.Parameters.AddWithValue("@table", table.TableName);
            await cmd.ExecuteNonQueryAsync(ct);
            _metadataCache.TryRemove(table.ToString().ToLowerInvariant(), out _);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation delegation, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"INSERT INTO DATA_OWNER_DELEGATIONS (id, data_owner_id, delegate_sid, valid_from, valid_to, reason)
                                VALUES (@id, @ownerId, @delSid, @from, @to, @reason)";
            cmd.Parameters.AddWithValue("@id", delegation.Id.ToString());
            cmd.Parameters.AddWithValue("@ownerId", delegation.DataOwnerId.ToString());
            cmd.Parameters.AddWithValue("@delSid", delegation.DelegateSid.Value);
            cmd.Parameters.AddWithValue("@from", delegation.ValidFrom.ToString("O"));
            cmd.Parameters.AddWithValue("@to", delegation.ValidTo.ToString("O"));
            cmd.Parameters.AddWithValue("@reason", delegation.Reason);

            await cmd.ExecuteNonQueryAsync(ct);
            return delegation;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var owners = new List<DataOwner>();
            using var cmd = _connection.CreateCommand();
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
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return await IsAuthorizedApproverForTableInternalAsync(table, approverSid, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private Task<bool> IsAuthorizedApproverForTableInternalAsync(TableIdentifier table, Sid approverSid, CancellationToken ct)
        => IsAuthorizedApproverForTableInternalAsync(table, approverSid, null, ct);

    private async Task<bool> IsAuthorizedApproverForTableInternalAsync(TableIdentifier table, Sid approverSid, string? itsmApproverAccount, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
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
        await _lock.WaitAsync(ct);
        try
        {
            var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = _connection.CreateCommand();
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
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var relations = new List<TableRelation>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"SELECT r.id, r.parent_table_id, COALESCE(tp.source_name, 'default'), tp.schema_name, tp.table_name,
                                       r.child_table_id, COALESCE(tc.source_name, 'default'), tc.schema_name, tc.table_name,
                                       r.relation_name, r.join_key_parent, r.join_key_child, r.cardinality
                                FROM TABLE_RELATIONS r
                                JOIN TABLES tp ON r.parent_table_id = tp.id
                                JOIN TABLES tc ON r.child_table_id = tc.id
                                WHERE tp.source_name = @pDomain COLLATE NOCASE AND tp.schema_name = @pSchema COLLATE NOCASE AND tp.table_name = @pTable COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@pDomain", parentTable.Domain);
            cmd.Parameters.AddWithValue("@pSchema", parentTable.Schema);
            cmd.Parameters.AddWithValue("@pTable", parentTable.TableName);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var relId = Guid.Parse(reader.GetString(0));
                var parentId = Guid.Parse(reader.GetString(1));
                var pId = new TableIdentifier(reader.GetString(2), reader.GetString(3), reader.GetString(4));
                var childId = Guid.Parse(reader.GetString(5));
                var cId = new TableIdentifier(reader.GetString(6), reader.GetString(7), reader.GetString(8));
                var relName = reader.GetString(9);
                var jkParent = reader.GetString(10);
                var jkChild = reader.GetString(11);
                var card = Enum.TryParse<RelationCardinality>(reader.GetString(12), true, out var c) ? c : RelationCardinality.OneToMany;

                relations.Add(new TableRelation
                {
                    Id = relId,
                    ParentTableId = parentId,
                    ParentTableIdentifier = pId,
                    ChildTableId = childId,
                    ChildTableIdentifier = cId,
                    RelationName = relName,
                    JoinKeyParent = jkParent,
                    JoinKeyChild = jkChild,
                    Cardinality = card
                });
            }
            return relations;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task CreateRelationAsync(TableRelation relation, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"INSERT OR REPLACE INTO TABLE_RELATIONS 
                                (id, parent_table_id, child_table_id, relation_name, join_key_parent, join_key_child, cardinality)
                                VALUES (@id, @pId, @cId, @relName, @jkP, @jkC, @card)";
            cmd.Parameters.AddWithValue("@id", relation.Id.ToString());
            cmd.Parameters.AddWithValue("@pId", relation.ParentTableId.ToString());
            cmd.Parameters.AddWithValue("@cId", relation.ChildTableId.ToString());
            cmd.Parameters.AddWithValue("@relName", relation.RelationName);
            cmd.Parameters.AddWithValue("@jkP", relation.JoinKeyParent);
            cmd.Parameters.AddWithValue("@jkC", relation.JoinKeyChild);
            cmd.Parameters.AddWithValue("@card", relation.Cardinality.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

}
