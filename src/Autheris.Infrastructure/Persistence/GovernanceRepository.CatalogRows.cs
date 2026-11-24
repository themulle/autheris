namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Catalog;

public partial class GovernanceRepository
{
    internal const string EpochJoin = CatalogRowHydrator.EpochJoin;
    internal const string EpochByTableIdSql = CatalogRowHydrator.EpochByTableIdSql;
    internal const string ActiveTableByIdSql = CatalogRowHydrator.ActiveTableByIdSql;
    internal const string ActiveTableColumnNamesSql = CatalogRowHydrator.ActiveTableColumnNamesSql;

    internal static IReadOnlyDictionary<string, string> ParseColumnMetaJson(string? metaJson) =>
        CatalogRowHydrator.ParseColumnMetaJson(metaJson);

    internal static bool HasMaskingRulesChanged(
        IReadOnlyDictionary<string, MaskingRule> existing,
        IReadOnlyDictionary<string, MaskingRule>? incoming) =>
        CatalogRowHydrator.HasMaskingRulesChanged(existing, incoming);

    internal static void ValidateVersionDeltas(long structureDelta, long contentDelta) =>
        CatalogRowHydrator.ValidateVersionDeltas(structureDelta, contentDelta);

    internal static Task<long> ReadTableEpochAsync(DbConnection conn, Guid tableId, CancellationToken ct) =>
        CatalogRowHydrator.ReadTableEpochAsync(conn, tableId, ct);

    internal static Task<Dictionary<Guid, long>> ReadTableEpochsByIdsAsync(DbConnection conn, IReadOnlyList<Guid> tableIds, CancellationToken ct) =>
        CatalogRowHydrator.ReadTableEpochsByIdsAsync(conn, tableIds, ct);

    internal static Task<Dictionary<Guid, long>> ReadAllTableEpochsAsync(DbConnection conn, CancellationToken ct) =>
        CatalogRowHydrator.ReadAllTableEpochsAsync(conn, ct);

    internal static Task<TableIdentifier?> GetActiveTableIdentifierByIdAsync(DbConnection conn, Guid tableId, CancellationToken ct) =>
        CatalogRowHydrator.GetActiveTableIdentifierByIdAsync(conn, tableId, ct);

    internal static Task<bool> UpdateRelationCrossDataSourceAsync(
        DbConnection conn,
        DbTransaction tx,
        Guid relationId,
        object allowedValue,
        CancellationToken ct) =>
        CatalogRowHydrator.UpdateRelationCrossDataSourceAsync(conn, tx, relationId, allowedValue, ct);

    internal static Task<List<string>> LoadColumnNamesAsync(
        DbConnection conn,
        DbTransaction? tx,
        string tableId,
        CancellationToken ct) =>
        CatalogRowHydrator.LoadColumnNamesAsync(conn, tx, tableId, ct);

    internal async Task PublishCatalogChangedEventAsync(CatalogVersion version, CancellationToken ct)
    {
        _epochValidationService?.InvalidateAllTables(); // issue #1044: before the (asynchronous) event reaches this replica's caches
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

    internal async Task<List<(Guid Id, TableIdentifier Identifier, Table Table)>> LoadActiveTablesAsync(
        DbCommand cmd,
        CancellationToken ct)
    {
        var activeTables = new List<(Guid Id, TableIdentifier Identifier, Table Table)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var tableEntity = CatalogRowHydrator.ReadTableEntity(reader, _logger);
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

    internal async Task<(Dictionary<Guid, List<TableColumn>> ColumnsByTable, Dictionary<Guid, Dictionary<string, MaskingRule>> MaskingRulesByTable, HashSet<Guid> CorruptedTableIds)> LoadBatchTableColumnsAsync(
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

                var col = CatalogRowHydrator.ReadTableColumn(reader, out var tableId, out var rule, _logger);
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
                LogCatalogNotice("A catalog row in TABLE_COLUMNS or its column mask override is invalid; the table is marked unavailable (fail-closed).");
                if (tid.HasValue)
                {
                    corruptedTableIds.Add(tid.Value);
                }
            }
        }

        return (columnsByTable, maskingRulesByTable, corruptedTableIds);
    }
}
