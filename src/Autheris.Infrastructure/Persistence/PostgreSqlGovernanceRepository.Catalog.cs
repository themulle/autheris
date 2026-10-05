using System.Text.Json;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Npgsql;

namespace Autheris.Infrastructure.Persistence;

public partial class PostgreSqlGovernanceRepository
{
    public async Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var cacheKey = table.ToString().ToLowerInvariant();
        var nowTicks = DateTime.UtcNow.Ticks;
        if (_metadataCache.TryGetValue(cacheKey, out var entry) && (nowTicks - entry.CachedAtTicks < MetadataCacheTtlTicks))
        {
            return entry.Metadata;
        }

        Guid? tableId = null;
        Table? tableEntity = null;

        await using (var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, data_source_type, http_endpoint_json, plugin_name, description, long_description, doc_source
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

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT c.id, c.column_name, c.data_type, c.is_sensitive,
                                           m.id, m.rule_type, m.pattern_or_format, m.replacement, m.hmac_key_id,
                                           c.description, c.long_description, c.meta_json, c.doc_source
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

                    var metaDict = SqliteGovernanceRepository.ParseColumnMetaJson(metaJson);

                    columns.Add(new TableColumn
                    {
                        Id = colId,
                        TableId = tableId.Value,
                        ColumnName = colName,
                        DataType = dataType,
                        IsSensitive = isSensitive,
                        Description = desc,
                        LongDescription = longDesc,
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

            var result = new TableMetadata
            {
                Identifier = table,
                Table = tableEntity,
                Columns = columns,
                ColumnMaskingRules = maskingRules,
                PrimaryKeyColumns = new List<string> { "id" }
            };

            _metadataCache[cacheKey] = (result, DateTime.UtcNow.Ticks);
            return result;
        }
    }

    public async Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
    {
        var tables = new List<TableIdentifier>();

        await using (var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT source_name, schema_name, table_name FROM TABLES WHERE is_active = 1";
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    tables.Add(new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                }
            }
        }

        var result = new List<TableMetadata>();
        foreach (var t in tables)
        {
            var meta = await GetTableMetadataAsync(t, ct).ConfigureAwait(false);
            if (meta != null)
            {
                result.Add(meta);
            }
        }

        return result;
    }

    public async Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var tableId = metadata.Table.Id == Guid.Empty ? Guid.NewGuid() : metadata.Table.Id;

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"SELECT id, description, long_description, doc_source FROM TABLES
                                    WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)
                                    FOR UPDATE";
                cmd.Parameters.AddWithValue("@domain", metadata.Identifier.Domain);
                cmd.Parameters.AddWithValue("@schema", metadata.Identifier.Schema);
                cmd.Parameters.AddWithValue("@table", metadata.Identifier.TableName);

                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    tableId = Guid.Parse(reader.GetString(0));
                }
            }

            var httpEndpointJson = metadata.Table.HttpEndpoint != null
                ? JsonSerializer.Serialize(metadata.Table.HttpEndpoint)
                : null;

            await using (var upsertCmd = conn.CreateCommand())
            {
                upsertCmd.Transaction = tx;
                upsertCmd.CommandText = @"INSERT INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, data_source_type, http_endpoint_json, plugin_name, description, long_description, doc_source)
                                          VALUES (@id, @sourceType, @domain, @schema, @tableName, @displayName, @sensitivity, @fourEyes, @active, @dst, @httpJson, @plugin, @desc, @longDesc, @docSource)
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
                                              doc_source = COALESCE(EXCLUDED.doc_source, TABLES.doc_source)";
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
                await upsertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            foreach (var col in metadata.Columns)
            {
                var colId = col.Id == Guid.Empty ? Guid.NewGuid() : col.Id;
                await using (var findColCmd = conn.CreateCommand())
                {
                    findColCmd.Transaction = tx;
                    findColCmd.CommandText = "SELECT id FROM TABLE_COLUMNS WHERE table_id = @tableId AND LOWER(column_name) = LOWER(@columnName) LIMIT 1";
                    findColCmd.Parameters.AddWithValue("@tableId", tableId.ToString());
                    findColCmd.Parameters.AddWithValue("@columnName", col.ColumnName);
                    var existingColId = await findColCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    if (existingColId != null)
                    {
                        colId = Guid.Parse(existingColId.ToString()!);
                    }
                }

                var metaJson = col.Meta != null && col.Meta.Count > 0
                    ? JsonSerializer.Serialize(col.Meta)
                    : null;

                await using (var insertColCmd = conn.CreateCommand())
                {
                    insertColCmd.Transaction = tx;
                    insertColCmd.CommandText = @"INSERT INTO TABLE_COLUMNS (id, table_id, column_name, data_type, is_sensitive, description, long_description, doc_source, meta_json)
                                                VALUES (@id, @tableId, @columnName, @dataType, @isSensitive, @description, @longDescription, @docSource, @metaJson)
                                                ON CONFLICT (id) DO UPDATE SET
                                                    data_type = EXCLUDED.data_type,
                                                    is_sensitive = EXCLUDED.is_sensitive,
                                                    description = COALESCE(EXCLUDED.description, TABLE_COLUMNS.description),
                                                    long_description = COALESCE(EXCLUDED.long_description, TABLE_COLUMNS.long_description),
                                                    doc_source = COALESCE(EXCLUDED.doc_source, TABLE_COLUMNS.doc_source),
                                                    meta_json = COALESCE(EXCLUDED.meta_json, TABLE_COLUMNS.meta_json)";
                    insertColCmd.Parameters.AddWithValue("@id", colId.ToString());
                    insertColCmd.Parameters.AddWithValue("@tableId", tableId.ToString());
                    insertColCmd.Parameters.AddWithValue("@columnName", col.ColumnName);
                    insertColCmd.Parameters.AddWithValue("@dataType", col.DataType);
                    insertColCmd.Parameters.AddWithValue("@isSensitive", col.IsSensitive ? 1 : 0);
                    insertColCmd.Parameters.AddWithValue("@description", (object?)col.Description ?? DBNull.Value);
                    insertColCmd.Parameters.AddWithValue("@longDescription", (object?)col.LongDescription ?? DBNull.Value);
                    insertColCmd.Parameters.AddWithValue("@docSource", (object?)col.DocumentationSource ?? DBNull.Value);
                    insertColCmd.Parameters.AddWithValue("@metaJson", (object?)metaJson ?? DBNull.Value);
                    await insertColCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                if (metadata.ColumnMaskingRules.TryGetValue(col.ColumnName, out var maskRule))
                {
                    await using (var delMaskCmd = conn.CreateCommand())
                    {
                        delMaskCmd.Transaction = tx;
                        delMaskCmd.CommandText = "DELETE FROM COLUMN_MASKING_RULES WHERE table_column_id = @colId";
                        delMaskCmd.Parameters.AddWithValue("@colId", colId.ToString());
                        await delMaskCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }

                    await using (var insertMaskCmd = conn.CreateCommand())
                    {
                        insertMaskCmd.Transaction = tx;
                        insertMaskCmd.CommandText = @"INSERT INTO COLUMN_MASKING_RULES (id, table_column_id, rule_type, pattern_or_format, replacement, hmac_key_id)
                                                      VALUES (@id, @colId, @ruleType, @pattern, @replacement, @hmacKeyId)
                                                      ON CONFLICT (id) DO NOTHING";
                        insertMaskCmd.Parameters.AddWithValue("@id", (maskRule.Id == Guid.Empty ? Guid.NewGuid() : maskRule.Id).ToString());
                        insertMaskCmd.Parameters.AddWithValue("@colId", colId.ToString());
                        insertMaskCmd.Parameters.AddWithValue("@ruleType", maskRule.RuleType);
                        insertMaskCmd.Parameters.AddWithValue("@pattern", (object?)maskRule.PatternOrFormat ?? DBNull.Value);
                        insertMaskCmd.Parameters.AddWithValue("@replacement", (object?)maskRule.Replacement ?? DBNull.Value);
                        insertMaskCmd.Parameters.AddWithValue("@hmacKeyId", (object?)maskRule.HmacKeyId ?? DBNull.Value);
                        await insertMaskCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }
            }

            await IncrementTableEpochInternalAsync(conn, tx, metadata.Identifier, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }

        _metadataCache.TryRemove(metadata.Identifier.ToString().ToLowerInvariant(), out _);
        await _epochValidationService.InvalidateEpochAsync(metadata.Identifier, ct).ConfigureAwait(false);

        return await GetTableMetadataAsync(metadata.Identifier, ct).ConfigureAwait(false) ?? metadata;
    }

    public async Task<long> GetTableEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
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
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
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
            cmd.Parameters.AddWithValue("@domain", table.Domain);
            cmd.Parameters.AddWithValue("@schema", table.Schema);
            cmd.Parameters.AddWithValue("@table", table.TableName);
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));

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
        var list = new List<TableRelation>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT r.id, r.parent_table_id, r.child_table_id, r.relation_name, r.join_key_parent, r.join_key_child, r.cardinality,
                                   tp.source_name, tp.schema_name, tp.table_name,
                                   tc.source_name, tc.schema_name, tc.table_name
                            FROM TABLE_RELATIONS r
                            JOIN TABLES tp ON r.parent_table_id = tp.id
                            JOIN TABLES tc ON r.child_table_id = tc.id
                            WHERE LOWER(tp.source_name) = LOWER(@pDomain) AND LOWER(tp.schema_name) = LOWER(@pSchema) AND LOWER(tp.table_name) = LOWER(@pTable)";
        cmd.Parameters.AddWithValue("@pDomain", parentTable.Domain);
        cmd.Parameters.AddWithValue("@pSchema", parentTable.Schema);
        cmd.Parameters.AddWithValue("@pTable", parentTable.TableName);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var pId = new TableIdentifier(reader.GetString(7), reader.GetString(8), reader.GetString(9));
            var cId = new TableIdentifier(reader.GetString(10), reader.GetString(11), reader.GetString(12));
            var cardStr = reader.GetString(6);
            var card = Enum.TryParse<RelationCardinality>(cardStr, true, out var parsedCard) ? parsedCard : RelationCardinality.OneToMany;

            list.Add(new TableRelation
            {
                Id = Guid.Parse(reader.GetString(0)),
                ParentTableId = Guid.Parse(reader.GetString(1)),
                ParentTableIdentifier = pId,
                ChildTableId = Guid.Parse(reader.GetString(2)),
                ChildTableIdentifier = cId,
                RelationName = reader.GetString(3),
                JoinKeyParent = reader.GetString(4),
                JoinKeyChild = reader.GetString(5),
                Cardinality = card
            });
        }

        return list;
    }

    public async Task CreateRelationAsync(TableRelation relation, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        string? parentTableId = null;
        string? childTableId = null;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table) LIMIT 1";
            cmd.Parameters.AddWithValue("@domain", relation.ParentTableIdentifier.Domain);
            cmd.Parameters.AddWithValue("@schema", relation.ParentTableIdentifier.Schema);
            cmd.Parameters.AddWithValue("@table", relation.ParentTableIdentifier.TableName);
            parentTableId = (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString();
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table) LIMIT 1";
            cmd.Parameters.AddWithValue("@domain", relation.ChildTableIdentifier.Domain);
            cmd.Parameters.AddWithValue("@schema", relation.ChildTableIdentifier.Schema);
            cmd.Parameters.AddWithValue("@table", relation.ChildTableIdentifier.TableName);
            childTableId = (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString();
        }

        if (parentTableId == null || childTableId == null)
        {
            throw new InvalidOperationException($"Cannot create relation '{relation.RelationName}': One or both tables do not exist in the catalog.");
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO TABLE_RELATIONS (id, parent_table_id, child_table_id, relation_name, join_key_parent, join_key_child, cardinality)
                                VALUES (@id, @pId, @cId, @relName, @jkp, @jkc, @card)
                                ON CONFLICT (id) DO UPDATE SET
                                    parent_table_id = EXCLUDED.parent_table_id,
                                    child_table_id = EXCLUDED.child_table_id,
                                    relation_name = EXCLUDED.relation_name,
                                    join_key_parent = EXCLUDED.join_key_parent,
                                    join_key_child = EXCLUDED.join_key_child,
                                    cardinality = EXCLUDED.cardinality";
            cmd.Parameters.AddWithValue("@id", relation.Id.ToString());
            cmd.Parameters.AddWithValue("@pId", parentTableId);
            cmd.Parameters.AddWithValue("@cId", childTableId);
            cmd.Parameters.AddWithValue("@relName", relation.RelationName);
            cmd.Parameters.AddWithValue("@jkp", relation.JoinKeyParent);
            cmd.Parameters.AddWithValue("@jkc", relation.JoinKeyChild);
            cmd.Parameters.AddWithValue("@card", relation.Cardinality.ToString());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var owners = new List<DataOwner>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
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

    private async Task<bool> IsAuthorizedApproverForTableInternalAsync(TableIdentifier table, Sid approverSid, string? itsmApproverAccount, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*)
                            FROM TABLES t
                            JOIN TABLE_OWNERS tow ON t.id = tow.table_id
                            JOIN DATA_OWNERS o ON tow.data_owner_id = o.id
                            WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table)
                              AND (o.ad_sid = @apprSid OR (@itsmAccount IS NOT NULL AND (LOWER(o.ad_account) = LOWER(@itsmAccount) OR LOWER(o.email) = LOWER(@itsmAccount))))
                              AND o.is_active = 1";
        cmd.Parameters.AddWithValue("@domain", table.Domain);
        cmd.Parameters.AddWithValue("@schema", table.Schema);
        cmd.Parameters.AddWithValue("@table", table.TableName);
        cmd.Parameters.AddWithValue("@apprSid", approverSid.Value);
        cmd.Parameters.AddWithValue("@itsmAccount", (object?)itsmApproverAccount ?? DBNull.Value);

        var directOwnerCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        if (directOwnerCount > 0) return true;

        await using var delCmd = conn.CreateCommand();
        delCmd.CommandText = @"SELECT COUNT(*)
                               FROM DATA_OWNER_DELEGATIONS d
                               JOIN DATA_OWNERS o ON d.data_owner_id = o.id
                               JOIN TABLE_OWNERS tow ON o.id = tow.data_owner_id
                               JOIN TABLES t ON tow.table_id = t.id
                               WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table)
                                 AND (d.delegate_sid = @apprSid OR (@itsmAccount IS NOT NULL AND LOWER(d.delegate_sid) = LOWER(@itsmAccount)))
                                 AND d.valid_from <= @now AND d.valid_to >= @now";
        delCmd.Parameters.AddWithValue("@domain", table.Domain);
        delCmd.Parameters.AddWithValue("@schema", table.Schema);
        delCmd.Parameters.AddWithValue("@table", table.TableName);
        delCmd.Parameters.AddWithValue("@apprSid", approverSid.Value);
        delCmd.Parameters.AddWithValue("@itsmAccount", (object?)itsmApproverAccount ?? DBNull.Value);
        delCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));

        var delegateCount = Convert.ToInt64(await delCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        if (delegateCount > 0) return true;

        await using var roleCmd = conn.CreateCommand();
        roleCmd.CommandText = @"SELECT COUNT(*)
                                FROM ROLES r
                                JOIN ROLE_MEMBERS rm ON r.id = rm.role_id
                                WHERE (r.role_name = 'GovernanceAdmin' OR r.role_name = 'ClusterAdmin')
                                  AND (rm.member_sid = @apprSid OR (@itsmAccount IS NOT NULL AND LOWER(rm.member_sid) = LOWER(@itsmAccount)))";
        roleCmd.Parameters.AddWithValue("@apprSid", approverSid.Value);
        roleCmd.Parameters.AddWithValue("@itsmAccount", (object?)itsmApproverAccount ?? DBNull.Value);

        var roleAdminCount = Convert.ToInt64(await roleCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        return roleAdminCount > 0;
    }

    public async Task<IReadOnlySet<string>> GetTransitiveRolesAsync(Sid subjectSid, CancellationToken ct = default)
    {
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
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
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO DATA_OWNER_DELEGATIONS (id, data_owner_id, delegate_sid, valid_from, valid_to, reason)
                            VALUES (@id, @ownerId, @delegateSid, @validFrom, @validTo, @reason)
                            ON CONFLICT (id) DO UPDATE SET
                                delegate_sid = EXCLUDED.delegate_sid,
                                valid_from = EXCLUDED.valid_from,
                                valid_to = EXCLUDED.valid_to,
                                reason = EXCLUDED.reason";
        cmd.Parameters.AddWithValue("@id", delegation.Id.ToString());
        cmd.Parameters.AddWithValue("@ownerId", delegation.DataOwnerId.ToString());
        cmd.Parameters.AddWithValue("@delegateSid", delegation.DelegateSid.Value);
        cmd.Parameters.AddWithValue("@validFrom", delegation.ValidFrom.ToString("O"));
        cmd.Parameters.AddWithValue("@validTo", delegation.ValidTo.ToString("O"));
        cmd.Parameters.AddWithValue("@reason", delegation.Reason);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return delegation;
    }
}
