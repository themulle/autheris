namespace Autheris.Infrastructure.Persistence.Catalog;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;

internal static class CatalogBatchSqlBuilder
{
    internal readonly record struct TableUpsertRow(
        Guid TableId,
        TableMetadata Metadata,
        CatalogRowHydrator.TableDocumentation Docs);

    internal readonly record struct ColumnUpsertRow(
        Guid Id,
        Guid TableId,
        string ColumnName,
        string DataType,
        string? Description,
        string? LongDescription,
        string? DocSource,
        string? MetaJson,
        string? DisplayName,
        string? TextsJson,
        string? Collation,
        string? LengthSemantics,
        object ColumnRole,
        int Ordinal);

    internal readonly record struct ClassificationInsertRow(
        Guid ColumnId,
        string Code,
        string Source,
        string AssignedBy);

    internal readonly record struct MaskOverrideInsertRow(
        Guid ColumnId,
        string MaskFunction,
        string? ParamsJson,
        string? KeyRef);

    internal readonly record struct PrimaryKeyInsertRow(
        Guid TableId,
        int Ordinal,
        string ColumnId,
        bool IsDescending);

    internal readonly record struct IndexInsertRow(
        string IndexId,
        Guid TableId,
        string IndexName,
        bool IsUnique);

    internal readonly record struct IndexColumnInsertRow(
        string IndexId,
        int Ordinal,
        string ColumnId,
        bool IsDescending);

    internal readonly record struct EpochUpsertRow(
        Guid TableId,
        TableIdentifier Identifier);

    internal static async Task UpsertTablesBatchAsync(
        GovernanceRepository repo,
        DbConnection conn,
        DbTransaction tx,
        IReadOnlyList<TableUpsertRow> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var dbDialect = repo.IsPostgreSql ? DatabaseDialect.PostgreSql : DatabaseDialect.SqlServer;
        var subChunkSize = DatabaseParameterBudgetProvider.Instance.CalculateEffectiveChunkSize(rows.Count, 22, 0, dbDialect, 50);

        for (var offset = 0; offset < rows.Count; offset += subChunkSize)
        {
            var count = Math.Min(subChunkSize, rows.Count - offset);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            var sb = new StringBuilder();
            if (repo.IsPostgreSql)
            {
                sb.Append(@"INSERT INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, data_source_type, http_endpoint_json, plugin_name, description, long_description, doc_source, required_filter_columns_json, texts_json, min_group_size, non_personal, dbt_unique_id, object_kind, metric_view_count_measure) VALUES ");
            }
            else
            {
                sb.Append(@"MERGE dbo.TABLES WITH (HOLDLOCK) AS t USING (VALUES ");
            }

            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                var suffix = i.ToString(CultureInfo.InvariantCulture);
                sb.Append("(@id").Append(suffix)
                  .Append(", @sourceType").Append(suffix)
                  .Append(", @domain").Append(suffix)
                  .Append(", @schema").Append(suffix)
                  .Append(", @tableName").Append(suffix)
                  .Append(", @displayName").Append(suffix)
                  .Append(", @sensitivity").Append(suffix)
                  .Append(", @fourEyes").Append(suffix)
                  .Append(", @active").Append(suffix)
                  .Append(", @dst").Append(suffix)
                  .Append(", @httpJson").Append(suffix)
                  .Append(", @plugin").Append(suffix)
                  .Append(", @desc").Append(suffix)
                  .Append(", @longDesc").Append(suffix)
                  .Append(", @docSource").Append(suffix)
                  .Append(", @requiredFilterColumnsJson").Append(suffix)
                  .Append(", @textsJson").Append(suffix)
                  .Append(", @minGroupSize").Append(suffix)
                  .Append(", @nonPersonal").Append(suffix)
                  .Append(", @dbtUniqueId").Append(suffix)
                  .Append(", @objectKind").Append(suffix)
                  .Append(", @metricViewCountMeasure").Append(suffix).Append(')');

                var row = rows[offset + i];
                var metadata = row.Metadata;
                var httpEndpointJson = metadata.Table.HttpEndpoint != null
                    ? JsonSerializer.Serialize(metadata.Table.HttpEndpoint)
                    : null;

                cmd.AddBound("@id" + suffix, row.TableId.ToString());
                cmd.AddBound("@sourceType" + suffix, metadata.Table.SourceType);
                cmd.AddBound("@domain" + suffix, metadata.Identifier.Domain);
                cmd.AddBound("@schema" + suffix, metadata.Identifier.Schema);
                cmd.AddBound("@tableName" + suffix, metadata.Identifier.TableName);
                cmd.AddBound("@displayName" + suffix, metadata.Table.DisplayName);
                cmd.AddBound("@sensitivity" + suffix, metadata.Table.Sensitivity);
                cmd.AddBound("@fourEyes" + suffix, metadata.Table.RequiresFourEyes ? 1 : 0);
                cmd.AddBound("@active" + suffix, metadata.Table.IsActive ? 1 : 0);
                cmd.AddBound("@dst" + suffix, (int)metadata.Table.DataSourceType);
                cmd.AddBound("@httpJson" + suffix, (object?)httpEndpointJson ?? DBNull.Value);
                cmd.AddBound("@plugin" + suffix, (object?)metadata.Table.PluginName ?? DBNull.Value);
                cmd.AddBound("@desc" + suffix, (object?)row.Docs.Description ?? DBNull.Value);
                cmd.AddBound("@longDesc" + suffix, (object?)row.Docs.LongDescription ?? DBNull.Value);
                cmd.AddBound("@docSource" + suffix, (object?)row.Docs.Source ?? DBNull.Value);
                cmd.AddBound("@requiredFilterColumnsJson" + suffix, metadata.RequiredFilterColumns.IsDefaultOrEmpty ? (object)DBNull.Value : JsonSerializer.Serialize(metadata.RequiredFilterColumns));
                cmd.AddBound("@textsJson" + suffix, (object?)CatalogLocalizationPersistence.SerializeLocalizedTexts(metadata.Table.Texts) ?? DBNull.Value);
                cmd.AddBound("@minGroupSize" + suffix, (object?)metadata.Table.MinGroupSize ?? DBNull.Value, DbType.Int32);
                cmd.AddBound("@nonPersonal" + suffix, metadata.Table.NonPersonal, DbType.Boolean);
                cmd.AddBound("@dbtUniqueId" + suffix, (object?)metadata.Table.DbtUniqueId ?? DBNull.Value);
                cmd.AddBound("@objectKind" + suffix, metadata.Table.ObjectKind switch
                {
                    DiscoveryObjectKind.Table => "table",
                    DiscoveryObjectKind.View => "view",
                    DiscoveryObjectKind.MetricView => "metric_view",
                    _ => throw new ArgumentOutOfRangeException(nameof(rows), metadata.Table.ObjectKind, "Unsupported DiscoveryObjectKind for persistence.")
                });
                cmd.AddBound("@metricViewCountMeasure" + suffix, (object?)metadata.Table.MetricViewCountMeasure ?? DBNull.Value);
            }

            if (repo.IsPostgreSql)
            {
                sb.Append(@" ON CONFLICT (id) DO UPDATE SET
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
    non_personal = EXCLUDED.non_personal,
    dbt_unique_id = COALESCE(EXCLUDED.dbt_unique_id, TABLES.dbt_unique_id),
    object_kind = EXCLUDED.object_kind,
    metric_view_count_measure = EXCLUDED.metric_view_count_measure");
            }
            else
            {
                sb.Append(@") AS s (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, data_source_type, http_endpoint_json, plugin_name, description, long_description, doc_source, required_filter_columns_json, texts_json, min_group_size, non_personal, dbt_unique_id, object_kind, metric_view_count_measure)
ON t.id = s.id
WHEN MATCHED THEN UPDATE SET
    source_type = s.source_type,
    source_name = s.source_name,
    schema_name = s.schema_name,
    table_name = s.table_name,
    display_name = s.display_name,
    sensitivity = s.sensitivity,
    requires_four_eyes = s.requires_four_eyes,
    is_active = s.is_active,
    data_source_type = s.data_source_type,
    http_endpoint_json = s.http_endpoint_json,
    plugin_name = s.plugin_name,
    description = COALESCE(s.description, t.description),
    long_description = COALESCE(s.long_description, t.long_description),
    doc_source = COALESCE(s.doc_source, t.doc_source),
    required_filter_columns_json = s.required_filter_columns_json,
    texts_json = s.texts_json,
    min_group_size = s.min_group_size,
    non_personal = s.non_personal,
    dbt_unique_id = COALESCE(s.dbt_unique_id, t.dbt_unique_id),
    object_kind = s.object_kind,
    metric_view_count_measure = s.metric_view_count_measure
WHEN NOT MATCHED THEN INSERT (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, data_source_type, http_endpoint_json, plugin_name, description, long_description, doc_source, required_filter_columns_json, texts_json, min_group_size, non_personal, dbt_unique_id, object_kind, metric_view_count_measure)
    VALUES (s.id, s.source_type, s.source_name, s.schema_name, s.table_name, s.display_name, s.sensitivity, s.requires_four_eyes, s.is_active, s.data_source_type, s.http_endpoint_json, s.plugin_name, s.description, s.long_description, s.doc_source, s.required_filter_columns_json, s.texts_json, s.min_group_size, s.non_personal, s.dbt_unique_id, s.object_kind, s.metric_view_count_measure);");
            }

            cmd.CommandText = sb.ToString();
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    internal static async Task DeleteRemovedColumnsBatchAsync(
        GovernanceRepository repo,
        DbConnection conn,
        DbTransaction tx,
        IReadOnlyList<Guid> columnIds,
        CancellationToken ct)
    {
        if (columnIds.Count == 0)
        {
            return;
        }

        var dbDialect = repo.IsPostgreSql ? DatabaseDialect.PostgreSql : DatabaseDialect.SqlServer;
        var subChunkSize = DatabaseParameterBudgetProvider.Instance.CalculateEffectiveChunkSize(columnIds.Count, 1, 0, dbDialect, 50);

        for (var offset = 0; offset < columnIds.Count; offset += subChunkSize)
        {
            var count = Math.Min(subChunkSize, columnIds.Count - offset);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            var inList = BindGuidInList(cmd, "cid", columnIds, offset, count);
            cmd.CommandText =
                "DELETE FROM COLUMN_CLASSIFICATIONS WHERE table_column_id IN (" + inList + "); " +
                "DELETE FROM COLUMN_MASK_OVERRIDES WHERE table_column_id IN (" + inList + "); " +
                "DELETE FROM TABLE_COLUMNS WHERE id IN (" + inList + ");";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    internal static async Task<Dictionary<Guid, int>> LoadMaxColumnOrdinalsAsync(
        GovernanceRepository repo,
        DbConnection conn,
        DbTransaction tx,
        IReadOnlyList<Guid> tableIds,
        CancellationToken ct)
    {
        var result = new Dictionary<Guid, int>(tableIds.Count);
        if (tableIds.Count == 0)
        {
            return result;
        }

        var dbDialect = repo.IsPostgreSql ? DatabaseDialect.PostgreSql : DatabaseDialect.SqlServer;
        var subChunkSize = DatabaseParameterBudgetProvider.Instance.CalculateEffectiveChunkSize(tableIds.Count, 1, 0, dbDialect, 50);

        for (var offset = 0; offset < tableIds.Count; offset += subChunkSize)
        {
            var count = Math.Min(subChunkSize, tableIds.Count - offset);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            var inList = BindGuidInList(cmd, "tid", tableIds, offset, count);
            cmd.CommandText = "SELECT table_id, COALESCE(MAX(ordinal), 0) FROM TABLE_COLUMNS WHERE table_id IN (" + inList + ") GROUP BY table_id";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (Guid.TryParse(reader.GetString(0), out var tid))
                {
                    result[tid] = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                }
            }
        }

        return result;
    }

    internal static async Task UpsertColumnsBatchAsync(
        GovernanceRepository repo,
        DbConnection conn,
        DbTransaction tx,
        IReadOnlyList<ColumnUpsertRow> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var dbDialect = repo.IsPostgreSql ? DatabaseDialect.PostgreSql : DatabaseDialect.SqlServer;
        var subChunkSize = DatabaseParameterBudgetProvider.Instance.CalculateEffectiveChunkSize(rows.Count, 14, 0, dbDialect, 50);

        for (var offset = 0; offset < rows.Count; offset += subChunkSize)
        {
            var count = Math.Min(subChunkSize, rows.Count - offset);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            var sb = new StringBuilder();
            if (repo.IsPostgreSql)
            {
                sb.Append(@"INSERT INTO TABLE_COLUMNS (id, table_id, column_name, data_type, description, long_description, doc_source, meta_json, display_name, texts_json, ""collation"", length_semantics, column_role, ordinal) VALUES ");
            }
            else
            {
                sb.Append(@"MERGE dbo.TABLE_COLUMNS WITH (HOLDLOCK) AS t USING (VALUES ");
            }

            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                var suffix = i.ToString(CultureInfo.InvariantCulture);
                sb.Append("(@id").Append(suffix)
                  .Append(", @tableId").Append(suffix)
                  .Append(", @columnName").Append(suffix)
                  .Append(", @dataType").Append(suffix)
                  .Append(", @description").Append(suffix)
                  .Append(", @longDescription").Append(suffix)
                  .Append(", @docSource").Append(suffix)
                  .Append(", @metaJson").Append(suffix)
                  .Append(", @displayName").Append(suffix)
                  .Append(", @textsJson").Append(suffix)
                  .Append(", @collation").Append(suffix)
                  .Append(", @lengthSemantics").Append(suffix)
                  .Append(", @columnRole").Append(suffix)
                  .Append(", @ordinal").Append(suffix).Append(')');

                var row = rows[offset + i];
                cmd.AddBound("@id" + suffix, row.Id.ToString());
                cmd.AddBound("@tableId" + suffix, row.TableId.ToString());
                cmd.AddBound("@columnName" + suffix, row.ColumnName);
                cmd.AddBound("@dataType" + suffix, row.DataType);
                cmd.AddBound("@description" + suffix, (object?)row.Description ?? DBNull.Value);
                cmd.AddBound("@longDescription" + suffix, (object?)row.LongDescription ?? DBNull.Value);
                cmd.AddBound("@docSource" + suffix, (object?)row.DocSource ?? DBNull.Value);
                cmd.AddBound("@metaJson" + suffix, (object?)row.MetaJson ?? DBNull.Value);
                cmd.AddBound("@displayName" + suffix, (object?)row.DisplayName ?? DBNull.Value);
                cmd.AddBound("@textsJson" + suffix, (object?)row.TextsJson ?? DBNull.Value);
                cmd.AddBound("@collation" + suffix, (object?)row.Collation ?? DBNull.Value);
                cmd.AddBound("@lengthSemantics" + suffix, (object?)row.LengthSemantics ?? DBNull.Value);
                cmd.AddBound("@columnRole" + suffix, row.ColumnRole);
                cmd.AddBound("@ordinal" + suffix, row.Ordinal, DbType.Int32);
            }

            if (repo.IsPostgreSql)
            {
                sb.Append(@" ON CONFLICT (id) DO UPDATE SET
    data_type = EXCLUDED.data_type,
    description = COALESCE(EXCLUDED.description, TABLE_COLUMNS.description),
    long_description = COALESCE(EXCLUDED.long_description, TABLE_COLUMNS.long_description),
    doc_source = COALESCE(EXCLUDED.doc_source, TABLE_COLUMNS.doc_source),
    meta_json = COALESCE(EXCLUDED.meta_json, TABLE_COLUMNS.meta_json),
    display_name = EXCLUDED.display_name,
    texts_json = EXCLUDED.texts_json,
    ""collation"" = EXCLUDED.""collation"",
    length_semantics = EXCLUDED.length_semantics,
    column_role = EXCLUDED.column_role");
            }
            else
            {
                sb.Append(@") AS s (id, table_id, column_name, data_type, description, long_description, doc_source, meta_json, display_name, texts_json, [collation], length_semantics, column_role, ordinal)
ON t.id = s.id
WHEN MATCHED THEN UPDATE SET
    data_type = s.data_type,
    description = COALESCE(s.description, t.description),
    long_description = COALESCE(s.long_description, t.long_description),
    doc_source = COALESCE(s.doc_source, t.doc_source),
    meta_json = COALESCE(s.meta_json, t.meta_json),
    display_name = s.display_name,
    texts_json = s.texts_json,
    [collation] = s.[collation],
    length_semantics = s.length_semantics,
    column_role = s.column_role
WHEN NOT MATCHED THEN INSERT (id, table_id, column_name, data_type, description, long_description, doc_source, meta_json, display_name, texts_json, [collation], length_semantics, column_role, ordinal)
    VALUES (s.id, s.table_id, s.column_name, s.data_type, s.description, s.long_description, s.doc_source, s.meta_json, s.display_name, s.texts_json, s.[collation], s.length_semantics, s.column_role, s.ordinal);");
            }

            cmd.CommandText = sb.ToString();
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    internal static async Task InsertGlobalClassificationsBatchAsync(
        GovernanceRepository repo,
        DbConnection conn,
        DbTransaction tx,
        IReadOnlyList<ClassificationInsertRow> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var dbDialect = repo.IsPostgreSql ? DatabaseDialect.PostgreSql : DatabaseDialect.SqlServer;
        var subChunkSize = DatabaseParameterBudgetProvider.Instance.CalculateEffectiveChunkSize(rows.Count, 4, 0, dbDialect, 50);

        for (var offset = 0; offset < rows.Count; offset += subChunkSize)
        {
            var count = Math.Min(subChunkSize, rows.Count - offset);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            var sb = new StringBuilder();
            if (repo.IsPostgreSql)
            {
                sb.Append("INSERT INTO COLUMN_CLASSIFICATIONS (scope, table_column_id, code, source, assigned_by, assigned_at) VALUES ");
            }
            else
            {
                sb.Append("INSERT INTO dbo.COLUMN_CLASSIFICATIONS (scope, table_column_id, code, source, assigned_by, assigned_at) SELECT '*', v.col_id, v.code, v.source, v.assigned_by, CURRENT_TIMESTAMP FROM (VALUES ");
            }

            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                var suffix = i.ToString(CultureInfo.InvariantCulture);
                if (repo.IsPostgreSql)
                {
                    sb.Append("('*', @colId").Append(suffix)
                      .Append(", @code").Append(suffix)
                      .Append(", @source").Append(suffix)
                      .Append(", @assignedBy").Append(suffix)
                      .Append(", CURRENT_TIMESTAMP)");
                }
                else
                {
                    sb.Append("(@colId").Append(suffix)
                      .Append(", @code").Append(suffix)
                      .Append(", @source").Append(suffix)
                      .Append(", @assignedBy").Append(suffix).Append(')');
                }

                var row = rows[offset + i];
                cmd.AddBound("@colId" + suffix, row.ColumnId.ToString());
                cmd.AddBound("@code" + suffix, row.Code);
                cmd.AddBound("@source" + suffix, row.Source);
                cmd.AddBound("@assignedBy" + suffix, row.AssignedBy);
            }

            if (repo.IsPostgreSql)
            {
                sb.Append(" ON CONFLICT (scope, table_column_id, code) DO NOTHING");
            }
            else
            {
                sb.Append(") AS v(col_id, code, source, assigned_by) WHERE NOT EXISTS (SELECT 1 FROM dbo.COLUMN_CLASSIFICATIONS cc WHERE cc.scope = '*' AND cc.table_column_id = v.col_id AND cc.code = v.code);");
            }

            cmd.CommandText = sb.ToString();
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    internal static async Task ReplaceGlobalMaskOverridesBatchAsync(
        GovernanceRepository repo,
        DbConnection conn,
        DbTransaction tx,
        IReadOnlyList<Guid> deleteColumnIds,
        IReadOnlyList<MaskOverrideInsertRow> insertRows,
        CancellationToken ct)
    {
        var dbDialect = repo.IsPostgreSql ? DatabaseDialect.PostgreSql : DatabaseDialect.SqlServer;

        if (deleteColumnIds.Count > 0)
        {
            var delChunkSize = DatabaseParameterBudgetProvider.Instance.CalculateEffectiveChunkSize(deleteColumnIds.Count, 1, 0, dbDialect, 50);
            for (var offset = 0; offset < deleteColumnIds.Count; offset += delChunkSize)
            {
                var count = Math.Min(delChunkSize, deleteColumnIds.Count - offset);
                await using var delCmd = conn.CreateCommand();
                delCmd.Transaction = tx;
                var inList = BindGuidInList(delCmd, "cid", deleteColumnIds, offset, count);
                delCmd.CommandText = "DELETE FROM COLUMN_MASK_OVERRIDES WHERE scope = '*' AND code IS NULL AND table_column_id IN (" + inList + ")";
                await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        if (insertRows.Count > 0)
        {
            var insChunkSize = DatabaseParameterBudgetProvider.Instance.CalculateEffectiveChunkSize(insertRows.Count, 5, 0, dbDialect, 50);
            for (var offset = 0; offset < insertRows.Count; offset += insChunkSize)
            {
                var count = Math.Min(insChunkSize, insertRows.Count - offset);
                await using var insCmd = conn.CreateCommand();
                insCmd.Transaction = tx;

                var sb = new StringBuilder();
                sb.Append("INSERT INTO COLUMN_MASK_OVERRIDES (id, scope, table_column_id, code, mask_function, params_json, key_ref, catalog_text, updated_by, updated_at) VALUES ");
                for (var i = 0; i < count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    var suffix = i.ToString(CultureInfo.InvariantCulture);
                    sb.Append("(@id").Append(suffix)
                      .Append(", '*', @colId").Append(suffix)
                      .Append(", NULL, @fn").Append(suffix)
                      .Append(", @params").Append(suffix)
                      .Append(", @keyRef").Append(suffix)
                      .Append(", 'VISIBLE', 'system:catalog-upsert', CURRENT_TIMESTAMP)");

                    var row = insertRows[offset + i];
                    insCmd.AddBound("@id" + suffix, Guid.NewGuid(), DbType.Guid);
                    insCmd.AddBound("@colId" + suffix, row.ColumnId.ToString());
                    insCmd.AddBound("@fn" + suffix, row.MaskFunction);
                    insCmd.AddBound("@params" + suffix, (object?)row.ParamsJson ?? DBNull.Value);
                    insCmd.AddBound("@keyRef" + suffix, (object?)row.KeyRef ?? DBNull.Value);
                }

                insCmd.CommandText = sb.ToString();
                await insCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    internal static async Task ReplaceKeyDefinitionsBatchAsync(
        GovernanceRepository repo,
        DbConnection conn,
        DbTransaction tx,
        IReadOnlyList<Guid> replaceTableIds,
        IReadOnlyList<PrimaryKeyInsertRow> pkRows,
        IReadOnlyList<IndexInsertRow> indexRows,
        IReadOnlyList<IndexColumnInsertRow> indexColumnRows,
        CancellationToken ct)
    {
        if (replaceTableIds.Count == 0)
        {
            return;
        }

        var dialect = repo.KeyDialect;
        var dbDialect = repo.IsPostgreSql ? DatabaseDialect.PostgreSql : DatabaseDialect.SqlServer;

        var delChunkSize = DatabaseParameterBudgetProvider.Instance.CalculateEffectiveChunkSize(replaceTableIds.Count, 1, 0, dbDialect, 50);
        for (var offset = 0; offset < replaceTableIds.Count; offset += delChunkSize)
        {
            var count = Math.Min(delChunkSize, replaceTableIds.Count - offset);
            await using var delCmd = conn.CreateCommand();
            delCmd.Transaction = tx;
            var inList = BindGuidInList(delCmd, "tid", replaceTableIds, offset, count);
            delCmd.CommandText =
                $"DELETE FROM {dialect.Prefix}{CatalogKeyDefinitionStore.IndexTable} WHERE table_id IN ({inList}); " +
                $"DELETE FROM {dialect.Prefix}{CatalogKeyDefinitionStore.PrimaryKeyTable} WHERE table_id IN ({inList});";
            await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (pkRows.Count > 0)
        {
            var pkChunkSize = DatabaseParameterBudgetProvider.Instance.CalculateEffectiveChunkSize(pkRows.Count, 4, 0, dbDialect, 50);
            for (var offset = 0; offset < pkRows.Count; offset += pkChunkSize)
            {
                var count = Math.Min(pkChunkSize, pkRows.Count - offset);
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                var sb = new StringBuilder();
                sb.Append($"INSERT INTO {dialect.Prefix}{CatalogKeyDefinitionStore.PrimaryKeyTable} (table_id, key_ordinal, table_column_id, is_descending) VALUES ");
                for (var i = 0; i < count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    var suffix = i.ToString(CultureInfo.InvariantCulture);
                    sb.Append("(@tid").Append(suffix)
                      .Append(", @ord").Append(suffix)
                      .Append(", @cid").Append(suffix)
                      .Append(", @desc").Append(suffix).Append(')');

                    var row = pkRows[offset + i];
                    cmd.AddBound("@tid" + suffix, row.TableId.ToString());
                    cmd.AddBound("@ord" + suffix, row.Ordinal, DbType.Int32);
                    cmd.AddBound("@cid" + suffix, row.ColumnId);
                    cmd.AddBound("@desc" + suffix, row.IsDescending, DbType.Boolean);
                }

                cmd.CommandText = sb.ToString();
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        if (indexRows.Count > 0)
        {
            var idxChunkSize = DatabaseParameterBudgetProvider.Instance.CalculateEffectiveChunkSize(indexRows.Count, 4, 0, dbDialect, 50);
            for (var offset = 0; offset < indexRows.Count; offset += idxChunkSize)
            {
                var count = Math.Min(idxChunkSize, indexRows.Count - offset);
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                var sb = new StringBuilder();
                sb.Append($"INSERT INTO {dialect.Prefix}{CatalogKeyDefinitionStore.IndexTable} (id, table_id, index_name, is_unique) VALUES ");
                for (var i = 0; i < count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    var suffix = i.ToString(CultureInfo.InvariantCulture);
                    sb.Append("(@id").Append(suffix)
                      .Append(", @tid").Append(suffix)
                      .Append(", @name").Append(suffix)
                      .Append(", @unique").Append(suffix).Append(')');

                    var row = indexRows[offset + i];
                    cmd.AddBound("@id" + suffix, row.IndexId);
                    cmd.AddBound("@tid" + suffix, row.TableId.ToString());
                    cmd.AddBound("@name" + suffix, row.IndexName);
                    cmd.AddBound("@unique" + suffix, row.IsUnique, DbType.Boolean);
                }

                cmd.CommandText = sb.ToString();
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        if (indexColumnRows.Count > 0)
        {
            var icChunkSize = DatabaseParameterBudgetProvider.Instance.CalculateEffectiveChunkSize(indexColumnRows.Count, 4, 0, dbDialect, 50);
            for (var offset = 0; offset < indexColumnRows.Count; offset += icChunkSize)
            {
                var count = Math.Min(icChunkSize, indexColumnRows.Count - offset);
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                var sb = new StringBuilder();
                sb.Append($"INSERT INTO {dialect.Prefix}{CatalogKeyDefinitionStore.IndexColumnTable} (index_id, column_ordinal, table_column_id, is_descending) VALUES ");
                for (var i = 0; i < count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    var suffix = i.ToString(CultureInfo.InvariantCulture);
                    sb.Append("(@idx").Append(suffix)
                      .Append(", @ord").Append(suffix)
                      .Append(", @cid").Append(suffix)
                      .Append(", @desc").Append(suffix).Append(')');

                    var row = indexColumnRows[offset + i];
                    cmd.AddBound("@idx" + suffix, row.IndexId);
                    cmd.AddBound("@ord" + suffix, row.Ordinal, DbType.Int32);
                    cmd.AddBound("@cid" + suffix, row.ColumnId);
                    cmd.AddBound("@desc" + suffix, row.IsDescending, DbType.Boolean);
                }

                cmd.CommandText = sb.ToString();
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    internal static async Task IncrementTableEpochsBatchAsync(
        GovernanceRepository repo,
        DbConnection conn,
        DbTransaction tx,
        IReadOnlyList<EpochUpsertRow> rows,
        long? changeVersion,
        CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var now = TimeProvider.System.GetUtcNow().ToString("O");
        var dbDialect = repo.IsPostgreSql ? DatabaseDialect.PostgreSql : DatabaseDialect.SqlServer;
        var subChunkSize = DatabaseParameterBudgetProvider.Instance.CalculateEffectiveChunkSize(rows.Count, 4, 2, dbDialect, 50);

        for (var offset = 0; offset < rows.Count; offset += subChunkSize)
        {
            var count = Math.Min(subChunkSize, rows.Count - offset);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            var sb = new StringBuilder();
            if (repo.IsPostgreSql)
            {
                sb.Append("INSERT INTO POLICY_EPOCHS (table_id, domain, schema_name, table_name, epoch, change_version, updated_at) VALUES ");
            }
            else
            {
                sb.Append("MERGE POLICY_EPOCHS WITH (HOLDLOCK) AS target USING (VALUES ");
            }

            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                var suffix = i.ToString(CultureInfo.InvariantCulture);
                if (repo.IsPostgreSql)
                {
                    sb.Append("(@tid").Append(suffix)
                      .Append(", @d").Append(suffix)
                      .Append(", @s").Append(suffix)
                      .Append(", @t").Append(suffix)
                      .Append(", 2, COALESCE(@changeVersion, 0), @now)");
                }
                else
                {
                    sb.Append("(@tid").Append(suffix)
                      .Append(", @d").Append(suffix)
                      .Append(", @s").Append(suffix)
                      .Append(", @t").Append(suffix).Append(')');
                }

                var row = rows[offset + i];
                repo.InvalidateTableMetadata(row.Identifier.ToString().ToLowerInvariant());
                cmd.AddBound("@tid" + suffix, row.TableId.ToString());
                cmd.AddBound("@d" + suffix, row.Identifier.Domain);
                cmd.AddBound("@s" + suffix, row.Identifier.Schema);
                cmd.AddBound("@t" + suffix, row.Identifier.TableName);
            }

            cmd.AddBound("@changeVersion", (object?)changeVersion ?? DBNull.Value, DbType.Int64);
            cmd.AddBound("@now", now);

            if (repo.IsPostgreSql)
            {
                sb.Append(@" ON CONFLICT (table_id) DO UPDATE SET
    epoch = POLICY_EPOCHS.epoch + 1,
    change_version = COALESCE(@changeVersion, POLICY_EPOCHS.change_version),
    updated_at = EXCLUDED.updated_at");
            }
            else
            {
                sb.Append(@") AS source (table_id, domain, schema_name, table_name)
ON target.table_id = source.table_id
WHEN MATCHED THEN UPDATE SET
    epoch = target.epoch + 1,
    change_version = COALESCE(@changeVersion, target.change_version),
    updated_at = @now
WHEN NOT MATCHED THEN INSERT (table_id, domain, schema_name, table_name, epoch, change_version, updated_at)
    VALUES (source.table_id, source.domain, source.schema_name, source.table_name, 2, COALESCE(@changeVersion, 0), @now);");
            }

            cmd.CommandText = sb.ToString();
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static string BindGuidInList(DbCommand cmd, string prefix, IReadOnlyList<Guid> ids, int offset, int count)
    {
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            var name = "@" + prefix + i.ToString(CultureInfo.InvariantCulture);
            names[i] = name;
            cmd.AddBound(name, ids[offset + i].ToString());
        }

        return string.Join(",", names);
    }
}
