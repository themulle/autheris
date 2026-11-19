namespace Autheris.Infrastructure.Persistence.Catalog;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

internal static partial class CatalogRowHydrator
{
    internal static void SeedSensitiveColumn(DbConnection conn, DbTransaction trans, string colId, string ruleType, string replacement)
    {
        using (var cc = conn.CreateCommand())
        {
            cc.Transaction = trans;
            cc.CommandText = InsertGlobalClassificationIfAbsentSql;
            cc.AddBound("@colId", colId);
            cc.AddBound("@code", ClassificationScopes.PiiCode);
            cc.AddBound("@source", "SEED");
            cc.AddBound("@assignedBy", "system:seed");
            cc.ExecuteNonQuery();
        }

        using var mask = conn.CreateCommand();
        mask.Transaction = trans;
        mask.CommandText = @"INSERT INTO COLUMN_MASK_OVERRIDES (id, scope, table_column_id, code, mask_function, params_json, key_ref, catalog_text, updated_by, updated_at)
                             SELECT @id, '*', @cid, NULL, @fn, @params, NULL, 'VISIBLE', 'system:seed', CURRENT_TIMESTAMP
                             WHERE NOT EXISTS (SELECT 1 FROM COLUMN_MASK_OVERRIDES WHERE scope = '*' AND table_column_id = @cid AND code IS NULL);";
        mask.AddBound("@id", Guid.NewGuid());
        mask.AddBound("@cid", colId);
        var (function, parameters, _) = MaskingRuleOverrideMapper.FromRule(new MaskingRule { RuleType = ruleType, Replacement = replacement });
        mask.AddBound("@fn", function);
        mask.AddBound("@params", (object?)parameters ?? DBNull.Value);
        mask.ExecuteNonQuery();
    }

    internal static async Task<Dictionary<string, IReadOnlyList<ColumnClassification>>> ReadTableClassificationsAsync(DbConnection conn, Guid tableId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectGlobalClassificationsByTableSql;
        cmd.AddBound("@tableId", tableId.ToString());
        return ClassificationsOf(await ReadGlobalClassificationsAsync(cmd, ct).ConfigureAwait(false), tableId);
    }

    internal static async Task<Dictionary<Guid, Dictionary<string, IReadOnlyList<ColumnClassification>>>> ReadAllClassificationsAsync(DbConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectAllGlobalClassificationsSql;
        return await ReadGlobalClassificationsAsync(cmd, ct).ConfigureAwait(false);
    }

    internal static Dictionary<string, IReadOnlyList<ColumnClassification>> ClassificationsOf(
        Dictionary<Guid, Dictionary<string, IReadOnlyList<ColumnClassification>>> byTable, Guid tableId) =>
        byTable.TryGetValue(tableId, out var own) ? own : new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Additive only: an incoming global classification is stored when absent; an upsert never removes a classification.</summary>
    private static async Task<bool> WriteGlobalClassificationsAsync(
        DbConnection conn, DbTransaction tx, Guid colId, TableMetadata metadata, string columnName, CancellationToken ct)
    {
        if (!metadata.ColumnClassifications.TryGetValue(columnName, out var incoming))
        {
            return false;
        }

        var added = false;
        foreach (var classification in incoming.Where(static c => ClassificationScopes.IsGlobal(c.Scope)))
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = InsertGlobalClassificationIfAbsentSql;
            cmd.AddBound("@colId", colId.ToString());
            cmd.AddBound("@code", classification.Code);
            cmd.AddBound("@source", string.IsNullOrWhiteSpace(classification.Source) ? "INGEST" : classification.Source);
            cmd.AddBound("@assignedBy", string.IsNullOrWhiteSpace(classification.AssignedBy) ? "system:catalog-upsert" : classification.AssignedBy);
            added |= await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }

        return added;
    }

    private static async Task WriteGlobalMaskOverrideAsync(
        DbConnection conn, DbTransaction tx, Guid colId, string columnName, TableMetadata metadata, CancellationToken ct)
    {
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = DeleteGlobalMaskOverrideSql;
            del.AddBound("@colId", colId.ToString());
            await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (!metadata.ColumnMaskingRules.TryGetValue(columnName, out var rule))
        {
            return;
        }

        var (functionCode, paramsJson, keyRef) = MaskingRuleOverrideMapper.FromRule(rule);
        await using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = InsertGlobalMaskOverrideSql;
        ins.AddBound("@id", Guid.NewGuid());
        ins.AddBound("@colId", colId.ToString());
        ins.AddBound("@maskFunction", functionCode);
        ins.AddBound("@paramsJson", (object?)paramsJson ?? DBNull.Value);
        ins.AddBound("@keyRef", (object?)keyRef ?? DBNull.Value);
        if (await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException($"The masking rule of column '{columnName}' could not be stored.");
        }
    }

    internal static async Task<bool> UpsertColumnsAndMaskingRulesAsync(
        DbConnection conn,
        DbTransaction tx,
        string insertColumnSql,
        Guid tableId,
        TableMetadata metadata,
        Dictionary<string, (Guid Id, string DataType, string? Description, string? LongDescription, string? DocSource, string? Collation, string? LengthSemantics)> existingColsByName,
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
                    delColCmd.CommandText = "DELETE FROM COLUMN_CLASSIFICATIONS WHERE table_column_id = @colId; DELETE FROM COLUMN_MASK_OVERRIDES WHERE table_column_id = @colId; DELETE FROM TABLE_COLUMNS WHERE id = @colId;";
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
                insertColCmd.AddBound("@description", (object?)targetColDesc ?? DBNull.Value);
                insertColCmd.AddBound("@longDescription", (object?)targetColLongDesc ?? DBNull.Value);
                insertColCmd.AddBound("@docSource", (object?)targetColDocSource ?? DBNull.Value);
                insertColCmd.AddBound("@metaJson", (object?)metaJson ?? DBNull.Value);
                insertColCmd.AddBound("@displayName", (object?)col.DisplayName ?? DBNull.Value);
                insertColCmd.AddBound("@textsJson", (object?)CatalogLocalizationPersistence.SerializeLocalizedTexts(col.Texts) ?? DBNull.Value);
                insertColCmd.AddBound("@collation", (object?)col.Collation ?? DBNull.Value);
                insertColCmd.AddBound("@lengthSemantics", (object?)col.LengthSemantics ?? DBNull.Value);
                insertColCmd.AddBound("@columnRole", FormatColumnRole(col.Role));
                await insertColCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            colIdByName[col.ColumnName] = colId;

            if (await WriteGlobalClassificationsAsync(conn, tx, colId, metadata, col.ColumnName, ct).ConfigureAwait(false))
            {
                structuralChange = true;
            }

            await WriteGlobalMaskOverrideAsync(conn, tx, colId, col.ColumnName, metadata, ct).ConfigureAwait(false);
        }

        return structuralChange;
    }
}
