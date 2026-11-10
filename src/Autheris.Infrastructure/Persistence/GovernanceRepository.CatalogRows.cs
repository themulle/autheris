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
    // Statement execution, binding and row hydration (engine neutral).

    // PERS-17: epoch rows are matched by the natural key (domain, schema, table): a write inserts the row with its own id, which is not TABLES.id.
    internal const string EpochJoin = "POLICY_EPOCHS p JOIN TABLES t ON LOWER(p.domain) = LOWER(t.source_name) AND LOWER(p.schema_name) = LOWER(t.schema_name) AND LOWER(p.table_name) = LOWER(t.table_name)";
    internal const string EpochByTableIdSql = "SELECT MAX(p.epoch) FROM POLICY_EPOCHS p JOIN TABLES t ON LOWER(p.domain) = LOWER(t.source_name) AND LOWER(p.schema_name) = LOWER(t.schema_name) AND LOWER(p.table_name) = LOWER(t.table_name) WHERE t.id = @tableId";
    internal const string AllEpochsSql = "SELECT t.id, MAX(p.epoch) FROM POLICY_EPOCHS p JOIN TABLES t ON LOWER(p.domain) = LOWER(t.source_name) AND LOWER(p.schema_name) = LOWER(t.schema_name) AND LOWER(p.table_name) = LOWER(t.table_name) GROUP BY t.id";

    internal const string ActiveTableByIdSql = "SELECT source_name, schema_name, table_name FROM TABLES WHERE id = @id AND is_active = 1";
    internal const string ActiveTableColumnNamesSql = "SELECT column_name FROM TABLE_COLUMNS WHERE table_id = @t";

    internal const string TransitiveRolesSql = @"SELECT r.role_name
FROM ROLES r
JOIN ROLE_MEMBERS rm ON r.id = rm.role_id
WHERE rm.member_sid = @sid";

    internal static Table ReadTableEntity(DbDataReader reader, ILogger? logger = null, int o = 0)
    {
        var tableId = Guid.Parse(reader.GetString(o));
        var dstInt = reader.IsDBNull(o + 9) ? 0 : reader.GetInt32(o + 9);
        var httpEndpointJson = reader.IsDBNull(o + 10) ? null : reader.GetString(o + 10);
        var pluginName = reader.IsDBNull(o + 11) ? null : reader.GetString(o + 11);
        var httpEndpoint = !string.IsNullOrWhiteSpace(httpEndpointJson)
            ? JsonSerializer.Deserialize<HttpEndpointDescriptor>(httpEndpointJson)
            : null;
        var reqFiltersJson = reader.IsDBNull(o + 15) ? null : reader.GetString(o + 15);
        var reqFilters = string.IsNullOrWhiteSpace(reqFiltersJson)
            ? ImmutableArray<string>.Empty
            : JsonSerializer.Deserialize<ImmutableArray<string>>(reqFiltersJson);
        if (reqFilters.IsDefault) reqFilters = ImmutableArray<string>.Empty;
        var textsJson = reader.IsDBNull(o + 16) ? null : reader.GetString(o + 16);
        var texts = CatalogLocalizationPersistence.ParseLocalizedTexts(textsJson, tableId, logger);

        int? minGroupSize = null;
        if (reader.FieldCount > o + 17 && !reader.IsDBNull(o + 17))
        {
            minGroupSize = Convert.ToInt32(reader.GetValue(o + 17), CultureInfo.InvariantCulture);
        }

        bool nonPersonal = false;
        if (reader.FieldCount > o + 18 && !reader.IsDBNull(o + 18))
        {
            var val = reader.GetValue(o + 18);
            nonPersonal = val is bool b ? b : Convert.ToInt32(val, CultureInfo.InvariantCulture) == 1;
        }

        return new Table
        {
            Id = tableId,
            SourceType = reader.GetString(o + 1),
            SourceName = reader.GetString(o + 2),
            SchemaName = reader.GetString(o + 3),
            TableName = reader.GetString(o + 4),
            DisplayName = reader.GetString(o + 5),
            Sensitivity = reader.GetString(o + 6),
            RequiresFourEyes = reader.GetInt32(o + 7) == 1,
            IsActive = reader.GetInt32(o + 8) == 1,
            DataSourceType = (DataSourceType)dstInt,
            HttpEndpoint = httpEndpoint,
            PluginName = pluginName,
            Description = reader.IsDBNull(o + 12) ? null : reader.GetString(o + 12),
            LongDescription = reader.IsDBNull(o + 13) ? null : reader.GetString(o + 13),
            Texts = texts,
            DocumentationSource = reader.IsDBNull(o + 14) ? null : reader.GetString(o + 14),
            RequiredFilterColumns = reqFilters,
            MinGroupSize = minGroupSize,
            NonPersonal = nonPersonal
        };
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
                dict[prop.Name] = JsonValueText.From(prop.Value);
            }
            return dict;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static bool HasMaskingRulesChanged(
        IReadOnlyDictionary<string, MaskingRule> existing,
        IReadOnlyDictionary<string, MaskingRule>? incoming)
    {
        var incomingRules = incoming ?? (IReadOnlyDictionary<string, MaskingRule>)ImmutableDictionary<string, MaskingRule>.Empty;
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

    internal static TableColumn ReadTableColumn(
        DbDataReader reader,
        out Guid tableId,
        out MaskingRule? maskingRule,
        ILogger? logger = null,
        int o = 0)
    {
        var colId = Guid.Parse(reader.GetString(o));
        tableId = Guid.Parse(reader.GetString(o + 1));
        var colName = reader.GetString(o + 2);
        var dataType = reader.GetString(o + 3);
        var isSensitive = reader.GetInt32(o + 4) == 1;

        maskingRule = null;
        if (!reader.IsDBNull(o + 5))
        {
            var ruleType = reader.GetString(o + 6);
            if (string.IsNullOrWhiteSpace(ruleType))
            {
                throw new FormatException("Corrupted masking rule: rule_type is empty");
            }

            maskingRule = new MaskingRule
            {
                Id = Guid.Parse(reader.GetString(o + 5)),
                TableColumnId = colId,
                RuleType = ruleType,
                PatternOrFormat = reader.IsDBNull(o + 7) ? null : reader.GetString(o + 7),
                Replacement = reader.IsDBNull(o + 8) ? null : reader.GetString(o + 8),
                HmacKeyId = reader.IsDBNull(o + 9) ? null : reader.GetString(o + 9)
            };
        }

        var desc = reader.IsDBNull(o + 10) ? null : reader.GetString(o + 10);
        var longDesc = reader.IsDBNull(o + 11) ? null : reader.GetString(o + 11);
        var metaJson = reader.IsDBNull(o + 12) ? null : reader.GetString(o + 12);
        var docSource = reader.IsDBNull(o + 13) ? null : reader.GetString(o + 13);
        var colDisplayName = reader.IsDBNull(o + 14) ? null : reader.GetString(o + 14);
        var colTextsJson = reader.IsDBNull(o + 15) ? null : reader.GetString(o + 15);
        var colTexts = CatalogLocalizationPersistence.ParseLocalizedTexts(colTextsJson, colId, logger);
        var metaDict = ParseColumnMetaJson(metaJson);

        string? collation = null;
        if (reader.FieldCount > o + 16 && !reader.IsDBNull(o + 16))
        {
            collation = reader.GetString(o + 16);
        }

        string? lengthSemantics = null;
        if (reader.FieldCount > o + 17 && !reader.IsDBNull(o + 17))
        {
            lengthSemantics = reader.GetString(o + 17);
        }

        return new TableColumn
        {
            Id = colId,
            TableId = tableId,
            ColumnName = colName,
            DisplayName = colDisplayName,
            DataType = dataType,
            IsSensitive = isSensitive,
            Collation = collation,
            LengthSemantics = lengthSemantics,
            Description = desc,
            LongDescription = longDesc,
            Texts = colTexts,
            DocumentationSource = docSource,
            Meta = metaDict
        };
    }

    internal static async Task<(List<TableColumn> Columns, Dictionary<string, MaskingRule> MaskingRules)> LoadTableColumnsAsync(
        DbCommand cmd,
        ILogger? logger,
        CancellationToken ct)
    {
        var columns = new List<TableColumn>();
        var maskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var col = ReadTableColumn(reader, out _, out var rule, logger);
            columns.Add(col);
            if (rule != null)
            {
                maskingRules[col.ColumnName] = rule;
            }
        }

        return (columns, maskingRules);
    }

    internal static DataOwner ReadDataOwner(DbDataReader reader, int o = 0) => new()
    {
        Id = Guid.Parse(reader.GetString(o)),
        AdSid = new Sid(reader.GetString(o + 1)),
        AdAccount = reader.GetString(o + 2),
        DisplayName = reader.GetString(o + 3),
        Email = reader.GetString(o + 4),
        IsActive = reader.GetInt32(o + 5) == 1
    };

    internal static RelationRowData ReadRelationRow(DbDataReader reader, int o = 0) => new(
        reader.GetString(o),
        reader.GetString(o + 1),
        reader.GetString(o + 2),
        reader.GetString(o + 3),
        reader.GetString(o + 4),
        new TableIdentifier(reader.GetString(o + 5), reader.GetString(o + 6), reader.GetString(o + 7)),
        new TableIdentifier(reader.GetString(o + 8), reader.GetString(o + 9), reader.GetString(o + 10)),
        reader.IsDBNull(o + 11) ? null : reader.GetInt32(o + 11),
        reader.IsDBNull(o + 12) ? null : reader.GetString(o + 12),
        reader.IsDBNull(o + 13) ? null : reader.GetString(o + 13),
        !reader.IsDBNull(o + 14) && Convert.ToBoolean(reader.GetValue(o + 14), CultureInfo.InvariantCulture));

    internal static async Task<IReadOnlyList<TableRelation>> ReadRelationsAsync(
        DbCommand cmd,
        ILogger? logger,
        CancellationToken ct)
    {
        var rows = new List<RelationRowData>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(ReadRelationRow(reader));
        }

        return RelationRowAssembler.Assemble(rows, logger);
    }

    internal static async Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(
        DbConnection conn,
        string sql,
        TableIdentifier parentTable,
        ILogger? logger,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@pDomain", parentTable.Domain);
        cmd.AddBound("@pSchema", parentTable.Schema);
        cmd.AddBound("@pTable", parentTable.TableName);
        return await ReadRelationsAsync(cmd, logger, ct).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(
        DbConnection conn,
        string sql,
        ILogger? logger,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await ReadRelationsAsync(cmd, logger, ct).ConfigureAwait(false);
    }

    internal static async Task<long> ReadTableEpochAsync(DbConnection conn, Guid tableId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = EpochByTableIdSql;
        cmd.AddBound("@tableId", tableId.ToString());
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? -1 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    internal static async Task<Dictionary<Guid, long>> ReadAllTableEpochsAsync(DbConnection conn, CancellationToken ct)
    {
        var epochs = new Dictionary<Guid, long>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = AllEpochsSql;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (Guid.TryParse(reader.GetString(0), out var id))
            {
                epochs[id] = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            }
        }

        return epochs;
    }

    internal static async Task<long> GetTableEpochAsync(
        DbConnection conn,
        string sql,
        TableIdentifier table,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@domain", table.Domain);
        cmd.AddBound("@schema", table.Schema);
        cmd.AddBound("@table", table.TableName);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long l ? l : (result is int i ? i : 1L);
    }

    internal static async Task<TableIdentifier?> GetActiveTableIdentifierAsync(
        DbConnection conn,
        string sql,
        TableIdentifier table,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@domain", table.Domain);
        cmd.AddBound("@schema", table.Schema);
        cmd.AddBound("@table", table.TableName);
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

    internal static async Task<TableIdentifier?> GetActiveTableIdentifierByIdAsync(
        DbConnection conn,
        Guid tableId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ActiveTableByIdSql;
        cmd.AddBound("@id", tableId.ToString());
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

    internal static async Task<string> ResolveActiveTableIdAsync(
        DbConnection conn,
        DbTransaction? tx,
        string sql,
        string relationName,
        TableIdentifier table,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.AddBound("@domain", table.Domain);
        cmd.AddBound("@schema", table.Schema);
        cmd.AddBound("@table", table.TableName);
        var ids = new List<string>(2);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids.Count == 1 ? ids[0] : throw RelationWriteSupport.TableNotResolved(relationName, table, ids.Count);
    }

    internal static async Task<List<string>> LoadColumnNamesAsync(
        DbConnection conn,
        DbTransaction? tx,
        string tableId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = ActiveTableColumnNamesSql;
        cmd.AddBound("@t", tableId);
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    internal static async Task<bool> ReplaceRelationKeysAsync(
        DbConnection conn,
        DbTransaction tx,
        string storedId,
        bool created,
        IReadOnlyList<string> parentKeys,
        IReadOnlyList<string> childKeys,
        CancellationToken ct)
    {
        var stored = new List<(string Parent, string Child)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT parent_column, child_column FROM TABLE_RELATION_KEYS WHERE relation_id = @id ORDER BY key_ordinal";
            cmd.AddBound("@id", storedId);
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
            cmd.AddBound("@id", storedId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        for (var i = 0; i < parentKeys.Count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO TABLE_RELATION_KEYS (relation_id, key_ordinal, parent_column, child_column) VALUES (@id, @ord, @pc, @cc)";
            cmd.AddBound("@id", storedId);
            cmd.AddBound("@ord", i + 1);
            cmd.AddBound("@pc", parentKeys[i]);
            cmd.AddBound("@cc", childKeys[i]);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return keysChanged;
    }

    internal static async Task<bool> UpdateRelationCrossDataSourceAsync(
        DbConnection conn,
        DbTransaction tx,
        Guid relationId,
        object allowedValue,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE TABLE_RELATIONS SET allow_cross_data_source = @allowed WHERE id = @id";
        cmd.AddBound("@id", relationId.ToString());
        cmd.AddBound("@allowed", allowedValue);
        int rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    internal static async Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(
        DbConnection conn,
        string sql,
        TableIdentifier table,
        CancellationToken ct)
    {
        var owners = new List<DataOwner>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@domain", table.Domain);
        cmd.AddBound("@schema", table.Schema);
        cmd.AddBound("@table", table.TableName);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            owners.Add(ReadDataOwner(reader));
        }

        return owners;
    }

    internal static async Task<IReadOnlyCollection<string>> ResolveDataOwnerIdsInternalAsync(
        DbConnection conn,
        string sql,
        IEnumerable<string> identifiers,
        CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var identifier in identifiers.Where(i => !string.IsNullOrWhiteSpace(i)))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.AddBound("@p", identifier);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(reader.GetString(0));
            }
        }

        return ids;
    }

    internal static async Task<IReadOnlySet<string>> GetTransitiveRolesAsync(
        DbConnection conn,
        Sid subjectSid,
        CancellationToken ct)
    {
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = TransitiveRolesSql;
        cmd.AddBound("@sid", subjectSid.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            roles.Add(reader.GetString(0));
        }

        return roles;
    }

    internal static async Task<CatalogVersion> GetCatalogVersionAsync(
        DbConnection conn,
        string sql,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new CatalogVersion(reader.GetInt64(0), reader.GetInt64(1));
        }

        return new CatalogVersion(1, 1);
    }

    internal static void ValidateVersionDeltas(long structureDelta, long contentDelta)
    {
        if (structureDelta < 0 || contentDelta < 0 || (structureDelta == 0 && contentDelta == 0))
        {
            throw new ArgumentException("Version deltas must be non-negative with at least one positive delta.");
        }
    }

    private async Task PublishCatalogChangedEventAsync(CatalogVersion version, CancellationToken ct)
    {
        var timestamp = TimeProvider.System.GetUtcNow();
        if (_eventBus is { } eventBus)
        {
            try
            {
                await eventBus.PublishAsync(
                    ICatalogVersionProvider.CatalogChangedEventChannel,
                    new CatalogChangedEvent(version.StructureVersion, version.ContentVersion, timestamp),
                    ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                LogCatalogNotice("Failed to publish catalog changed event; polling will detect changes.");
            }
        }
    }

    private async Task<List<(Guid Id, TableIdentifier Identifier, Table Table)>> LoadActiveTablesAsync(
        DbCommand cmd,
        CancellationToken ct)
    {
        var activeTables = new List<(Guid Id, TableIdentifier Identifier, Table Table)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var tableEntity = ReadTableEntity(reader, _logger);
                var identifier = new TableIdentifier(tableEntity.SourceName, tableEntity.SchemaName, tableEntity.TableName);
                activeTables.Add((tableEntity.Id, identifier, tableEntity));
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or JsonException or ArgumentException)
            {
                LogCatalogNotice("A catalog row in TABLES is invalid and was skipped.");
            }
        }

        return activeTables;
    }

    private async Task<(Dictionary<Guid, List<TableColumn>> ColumnsByTable, Dictionary<Guid, Dictionary<string, MaskingRule>> MaskingRulesByTable, HashSet<Guid> CorruptedTableIds)> LoadBatchTableColumnsAsync(
        DbCommand cmd,
        CancellationToken ct)
    {
        var columnsByTable = new Dictionary<Guid, List<TableColumn>>();
        var maskingRulesByTable = new Dictionary<Guid, Dictionary<string, MaskingRule>>();
        var corruptedTableIds = new HashSet<Guid>();

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            Guid? tid = null;
            try
            {
                if (!reader.IsDBNull(1) && Guid.TryParse(reader.GetString(1), out var parsedTid))
                {
                    tid = parsedTid;
                }

                var col = ReadTableColumn(reader, out var tableId, out var rule, _logger);
                tid = tableId;
                if (!columnsByTable.TryGetValue(tableId, out var colList))
                {
                    colList = new List<TableColumn>();
                    columnsByTable[tableId] = colList;
                }
                colList.Add(col);

                if (rule != null)
                {
                    if (!maskingRulesByTable.TryGetValue(tableId, out var maskDict))
                    {
                        maskDict = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);
                        maskingRulesByTable[tableId] = maskDict;
                    }
                    maskDict[col.ColumnName] = rule;
                }
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or JsonException or ArgumentException)
            {
                LogCatalogNotice("A catalog row in TABLE_COLUMNS or COLUMN_MASKING_RULES is invalid; the table is marked unavailable (fail-closed).");
                if (tid.HasValue)
                {
                    corruptedTableIds.Add(tid.Value);
                }
            }
        }

        return (columnsByTable, maskingRulesByTable, corruptedTableIds);
    }

    internal static async Task<(Dictionary<string, (Guid Id, string DataType, bool IsSensitive, string? Description, string? LongDescription, string? DocSource, string? Collation, string? LengthSemantics)> ExistingColsByName, Dictionary<string, MaskingRule> ExistingMaskingRules, Dictionary<string, Guid> ColIdByName)> LoadExistingColumnsAndMaskingRulesAsync(
        DbConnection conn,
        DbTransaction tx,
        Guid tableId,
        CancellationToken ct)
    {
        var existingColsByName = new Dictionary<string, (Guid Id, string DataType, bool IsSensitive, string? Description, string? LongDescription, string? DocSource, string? Collation, string? LengthSemantics)>(StringComparer.OrdinalIgnoreCase);
        var existingMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);
        var colIdByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        var colCollation = conn.GetType().Name.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase) ? "c.\"collation\"" : "c.[collation]";
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $@"SELECT c.id, c.column_name, c.data_type, c.is_sensitive, c.description, c.long_description, c.doc_source,
                                   m.id, m.rule_type, m.pattern_or_format, m.replacement, m.hmac_key_id,
                                   {colCollation}, c.length_semantics
                            FROM TABLE_COLUMNS c
                            LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
                            WHERE c.table_id = @tableId";
        cmd.AddBound("@tableId", tableId.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (Guid.TryParse(reader.GetString(0), out var existingId))
            {
                var colName = reader.GetString(1);
                var colDataType = reader.GetString(2);
                var colSens = reader.GetInt32(3) == 1;
                var colDesc = reader.IsDBNull(4) ? null : reader.GetString(4);
                var colLongDesc = reader.IsDBNull(5) ? null : reader.GetString(5);
                var colDocSource = reader.IsDBNull(6) ? null : reader.GetString(6);
                var colCollation = reader.FieldCount > 12 && !reader.IsDBNull(12) ? reader.GetString(12) : null;
                var colLengthSemantics = reader.FieldCount > 13 && !reader.IsDBNull(13) ? reader.GetString(13) : null;
                existingColsByName[colName] = (existingId, colDataType, colSens, colDesc, colLongDesc, colDocSource, colCollation, colLengthSemantics);
                colIdByName[colName] = existingId;

                if (!reader.IsDBNull(7) && Guid.TryParse(reader.GetString(7), out var maskId))
                {
                    existingMaskingRules[colName] = new MaskingRule
                    {
                        Id = maskId,
                        TableColumnId = existingId,
                        RuleType = reader.GetString(8),
                        PatternOrFormat = reader.IsDBNull(9) ? null : reader.GetString(9),
                        Replacement = reader.IsDBNull(10) ? null : reader.GetString(10),
                        HmacKeyId = reader.IsDBNull(11) ? null : reader.GetString(11)
                    };
                }
            }
        }

        return (existingColsByName, existingMaskingRules, colIdByName);
    }

    /// <summary>The description texts and their documentation source that an upsert writes for a table.</summary>
    internal readonly record struct TableDocumentation(string? Description, string? LongDescription, string? Source);

    /// <summary>
    /// Reads the row of <c>SelectTableForUpdateSql</c> (columns 1 to 8) and returns whether the table changed structurally and which
    /// documentation texts the upsert writes (<see cref="ShouldOverwriteDocumentation"/>).
    /// </summary>
    internal static (bool StructuralChange, TableDocumentation Docs) ReadExistingTableForUpsert(DbDataReader reader, Table incoming, IReadOnlyList<string>? precedence)
    {
        var structuralChange =
            !string.Equals(reader.GetString(1), incoming.SourceType, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(reader.GetString(2), incoming.Sensitivity, StringComparison.OrdinalIgnoreCase) ||
            (reader.GetInt32(3) == 1) != incoming.RequiresFourEyes ||
            (reader.GetInt32(4) == 1) != incoming.IsActive ||
            (reader.IsDBNull(5) ? 0 : reader.GetInt32(5)) != (int)incoming.DataSourceType;
        var existingDesc = reader.IsDBNull(6) ? null : reader.GetString(6);
        var existingLongDesc = reader.IsDBNull(7) ? null : reader.GetString(7);
        var existingSource = reader.IsDBNull(8) ? null : reader.GetString(8);

        var canOverwriteDesc = !string.IsNullOrWhiteSpace(incoming.Description) &&
                               (string.IsNullOrWhiteSpace(existingDesc) || ShouldOverwriteDocumentation(precedence, existingSource, incoming.DocumentationSource));
        var canOverwriteLongDesc = !string.IsNullOrWhiteSpace(incoming.LongDescription) &&
                                   (string.IsNullOrWhiteSpace(existingLongDesc) || ShouldOverwriteDocumentation(precedence, existingSource, incoming.DocumentationSource));
        return (structuralChange, new TableDocumentation(
            canOverwriteDesc ? incoming.Description : existingDesc,
            canOverwriteLongDesc ? incoming.LongDescription : existingLongDesc,
            canOverwriteDesc || canOverwriteLongDesc ? incoming.DocumentationSource ?? existingSource : existingSource));
    }

    private static readonly string[] DefaultDocumentationSourcePrecedence = ["Manual", "DataCatalog", "dbt", "OpenApi", "Database", "Default"];

    /// <summary>
    /// Documentation source precedence (<c>Catalog:DocumentationSourcePrecedence</c>, highest first): an incoming source overwrites an existing
    /// description only when its rank is equal or higher. An existing row without a source is always overwritten, an incoming write without a
    /// source never overwrites; unlisted sources rank last.
    /// </summary>
    internal static bool ShouldOverwriteDocumentation(IReadOnlyList<string>? precedence, string? existingSource, string? newSource)
    {
        if (string.IsNullOrWhiteSpace(existingSource))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(newSource))
        {
            return false;
        }

        IReadOnlyList<string> order = precedence ?? DefaultDocumentationSourcePrecedence;
        var existingRank = RankOf(order, existingSource);
        var newRank = RankOf(order, newSource);
        return newRank <= existingRank;

        static int RankOf(IReadOnlyList<string> order, string source)
        {
            for (var i = 0; i < order.Count; i++)
            {
                if (string.Equals(order[i], source, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return int.MaxValue;
        }
    }

    internal static async Task<bool> UpsertColumnsAndMaskingRulesAsync(
        DbConnection conn,
        DbTransaction tx,
        string insertColumnSql,
        Guid tableId,
        TableMetadata metadata,
        Dictionary<string, (Guid Id, string DataType, bool IsSensitive, string? Description, string? LongDescription, string? DocSource, string? Collation, string? LengthSemantics)> existingColsByName,
        Dictionary<string, Guid> colIdByName,
        CancellationToken ct,
        Func<string?, string?, bool>? shouldOverwriteDoc = null)
    {
        var structuralChange = false;
        if (metadata.ReplaceColumns)
        {
            var incomingNames = new HashSet<string>(metadata.Columns.Select(c => c.ColumnName), StringComparer.OrdinalIgnoreCase);
            foreach (var (existingName, existingInfo) in existingColsByName)
            {
                if (!incomingNames.Contains(existingName))
                {
                    structuralChange = true;
                    await using var delColCmd = conn.CreateCommand();
                    delColCmd.Transaction = tx;
                    delColCmd.CommandText = "DELETE FROM COLUMN_MASKING_RULES WHERE table_column_id = @colId; DELETE FROM TABLE_COLUMNS WHERE id = @colId;";
                    delColCmd.AddBound("@colId", existingInfo.Id.ToString());
                    await delColCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
        }

        foreach (var col in metadata.Columns)
        {
            var colId = col.Id == Guid.Empty ? Guid.NewGuid() : col.Id;
            var incomingColSource = shouldOverwriteDoc != null
                ? (col.DocumentationSource ?? metadata.Table.DocumentationSource)
                : col.DocumentationSource;
            var targetColDesc = col.Description;
            var targetColLongDesc = col.LongDescription;
            var targetColDocSource = incomingColSource;

            if (existingColsByName.TryGetValue(col.ColumnName, out var existingCol))
            {
                colId = existingCol.Id;
                if (!string.Equals(existingCol.DataType, col.DataType, StringComparison.OrdinalIgnoreCase) ||
                    existingCol.IsSensitive != col.IsSensitive ||
                    !string.Equals(existingCol.Collation, col.Collation, StringComparison.Ordinal) ||
                    !string.Equals(existingCol.LengthSemantics, col.LengthSemantics, StringComparison.OrdinalIgnoreCase))
                {
                    structuralChange = true;
                }

                if (shouldOverwriteDoc != null)
                {
                    bool canOverwriteColDesc = !string.IsNullOrWhiteSpace(col.Description) &&
                                               (string.IsNullOrWhiteSpace(existingCol.Description) ||
                                                shouldOverwriteDoc(existingCol.DocSource, incomingColSource));

                    bool canOverwriteColLongDesc = !string.IsNullOrWhiteSpace(col.LongDescription) &&
                                                   (string.IsNullOrWhiteSpace(existingCol.LongDescription) ||
                                                    shouldOverwriteDoc(existingCol.DocSource, incomingColSource));

                    targetColDesc = canOverwriteColDesc ? col.Description : existingCol.Description;
                    targetColLongDesc = canOverwriteColLongDesc ? col.LongDescription : existingCol.LongDescription;
                    targetColDocSource = (canOverwriteColDesc || canOverwriteColLongDesc) ? (incomingColSource ?? existingCol.DocSource) : existingCol.DocSource;
                }
            }
            else
            {
                structuralChange = true;
            }

            IReadOnlyDictionary<string, string> effectiveMeta = metadata.ReplaceKeyDefinition
                ? CatalogKeyDefinitionStore.WithKeyMarkers(col.Meta, col.ColumnName, metadata.PrimaryKeyColumns)
                : col.Meta ?? new Dictionary<string, string>();

            var metaJson = effectiveMeta.Count > 0
                ? JsonSerializer.Serialize(effectiveMeta)
                : metadata.ReplaceKeyDefinition ? "{}" : null;

            await using (var insertColCmd = conn.CreateCommand())
            {
                insertColCmd.Transaction = tx;
                insertColCmd.CommandText = insertColumnSql;
                insertColCmd.AddBound("@id", colId.ToString());
                insertColCmd.AddBound("@tableId", tableId.ToString());
                insertColCmd.AddBound("@columnName", col.ColumnName);
                insertColCmd.AddBound("@dataType", col.DataType);
                insertColCmd.AddBound("@isSensitive", col.IsSensitive ? 1 : 0);
                insertColCmd.AddBound("@description", (object?)targetColDesc ?? DBNull.Value);
                insertColCmd.AddBound("@longDescription", (object?)targetColLongDesc ?? DBNull.Value);
                insertColCmd.AddBound("@docSource", (object?)targetColDocSource ?? DBNull.Value);
                insertColCmd.AddBound("@metaJson", (object?)metaJson ?? DBNull.Value);
                insertColCmd.AddBound("@displayName", (object?)col.DisplayName ?? DBNull.Value);
                insertColCmd.AddBound("@textsJson", (object?)CatalogLocalizationPersistence.SerializeLocalizedTexts(col.Texts) ?? DBNull.Value);
                insertColCmd.AddBound("@collation", (object?)col.Collation ?? DBNull.Value);
                insertColCmd.AddBound("@lengthSemantics", (object?)col.LengthSemantics ?? DBNull.Value);
                await insertColCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            colIdByName[col.ColumnName] = colId;

            await using (var delMaskCmd = conn.CreateCommand())
            {
                delMaskCmd.Transaction = tx;
                delMaskCmd.CommandText = "DELETE FROM COLUMN_MASKING_RULES WHERE table_column_id = @colId";
                delMaskCmd.AddBound("@colId", colId.ToString());
                await delMaskCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (metadata.ColumnMaskingRules.TryGetValue(col.ColumnName, out var maskRule))
            {
                await using (var insertMaskCmd = conn.CreateCommand())
                {
                    insertMaskCmd.Transaction = tx;
                    insertMaskCmd.CommandText = @"INSERT INTO COLUMN_MASKING_RULES (id, table_column_id, rule_type, pattern_or_format, replacement, hmac_key_id)
                                                  VALUES (@id, @colId, @ruleType, @pattern, @replacement, @hmacKeyId)";
                    insertMaskCmd.AddBound("@id", Guid.NewGuid().ToString());
                    insertMaskCmd.AddBound("@colId", colId.ToString());
                    insertMaskCmd.AddBound("@ruleType", maskRule.RuleType);
                    insertMaskCmd.AddBound("@pattern", (object?)maskRule.PatternOrFormat ?? DBNull.Value);
                    insertMaskCmd.AddBound("@replacement", (object?)maskRule.Replacement ?? DBNull.Value);
                    insertMaskCmd.AddBound("@hmacKeyId", (object?)maskRule.HmacKeyId ?? DBNull.Value);
                    if (await insertMaskCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                    {
                        throw new InvalidOperationException($"The masking rule of column '{col.ColumnName}' could not be stored.");
                    }
                }
            }
        }

        return structuralChange;
    }
}
