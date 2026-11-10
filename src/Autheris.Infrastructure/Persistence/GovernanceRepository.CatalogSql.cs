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
    private bool IsPostgreSql => _dialect is PostgreSqlGovernanceDialect;

    internal KeyDefinitionDialect KeyDialect => IsPostgreSql ? CatalogKeyDefinitionStore.PostgreSql : CatalogKeyDefinitionStore.SqlServer;

    private string SelectTableColumnsSql => IsPostgreSql ? CatalogSql.SelectTableColumnsSqlPg : CatalogSql.SelectTableColumnsSqlSs;
    private string SelectActiveBatchTableColumnsSql => IsPostgreSql ? CatalogSql.SelectActiveBatchTableColumnsSqlPg : CatalogSql.SelectActiveBatchTableColumnsSqlSs;
    private string SelectTableForUpdateSql => IsPostgreSql ? CatalogSql.SelectTableForUpdateSqlPg : CatalogSql.SelectTableForUpdateSqlSs;
    private string UpsertTableSql => IsPostgreSql ? CatalogSql.UpsertTableSqlPg : CatalogSql.UpsertTableSqlSs;
    private string InsertColumnSql => IsPostgreSql ? CatalogSql.InsertColumnSqlPg : CatalogSql.InsertColumnSqlSs;
    private string RelationsForParentTableSql => IsPostgreSql ? CatalogSql.RelationsForParentTableSqlPg : CatalogSql.RelationsForParentTableSqlSs;
    private string AllRelationsSql => IsPostgreSql ? CatalogSql.AllRelationsSqlPg : CatalogSql.AllRelationsSqlSs;
    private string InsertRelationSql => IsPostgreSql ? CatalogSql.InsertRelationSqlPg : CatalogSql.InsertRelationSqlSs;
    private string InsertDelegationSql => IsPostgreSql ? CatalogSql.InsertDelegationSqlPg : CatalogSql.InsertDelegationSqlSs;
    private string CatalogVersionSql => IsPostgreSql ? CatalogSql.CatalogVersionSqlPg : CatalogSql.CatalogVersionSqlSs;
    private string BumpCatalogVersionSql => IsPostgreSql ? CatalogSql.BumpCatalogVersionSqlPg : CatalogSql.BumpCatalogVersionSqlSs;

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

        public const string SelectTableColumnsSqlPg = @"SELECT c.id, c.table_id, c.column_name, c.data_type, c.is_sensitive,
       m.id, m.rule_type, m.pattern_or_format, m.replacement, m.hmac_key_id,
       c.description, c.long_description, c.meta_json, c.doc_source,
       c.display_name, c.texts_json
FROM TABLE_COLUMNS c
LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
WHERE c.table_id = @tableId
ORDER BY c.ctid"; // TABLE_COLUMNS has no ordinal/serial/timestamp (id is a GUID): heap order is the only registration order until an ordinal column is added by a new migration step (follow-up); the natural-key unique index would otherwise return the columns by name

        public const string SelectTableColumnsSqlSs = @"SELECT c.id, c.table_id, c.column_name, c.data_type, c.is_sensitive,
       m.id, m.rule_type, m.pattern_or_format, m.replacement, m.hmac_key_id,
       c.description, c.long_description, c.meta_json, c.doc_source,
       c.display_name, c.texts_json
FROM TABLE_COLUMNS c
LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
WHERE c.table_id = @tableId";

        public const string SelectActiveBatchTableColumnsSqlPg = @"SELECT c.id, c.table_id, c.column_name, c.data_type, c.is_sensitive,
       m.id, m.rule_type, m.pattern_or_format, m.replacement, m.hmac_key_id,
       c.description, c.long_description, c.meta_json, c.doc_source,
       c.display_name, c.texts_json
FROM TABLE_COLUMNS c
LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
INNER JOIN TABLES t ON c.table_id = t.id
WHERE t.is_active = 1
ORDER BY c.ctid"; // registration (heap) order, see SelectTableColumnsSql

        public const string SelectActiveBatchTableColumnsSqlSs = @"SELECT c.id, c.table_id, c.column_name, c.data_type, c.is_sensitive,
       m.id, m.rule_type, m.pattern_or_format, m.replacement, m.hmac_key_id,
       c.description, c.long_description, c.meta_json, c.doc_source,
       c.display_name, c.texts_json
FROM TABLE_COLUMNS c
LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
INNER JOIN TABLES t ON c.table_id = t.id
WHERE t.is_active = 1";

        public const string SelectTableForUpdateSqlPg = @"SELECT id, source_type, sensitivity, requires_four_eyes, is_active, data_source_type, description, long_description, doc_source FROM TABLES
WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)
FOR UPDATE";

        public const string SelectTableForUpdateSqlSs = @"SELECT id, source_type, sensitivity, requires_four_eyes, is_active, data_source_type, description, long_description, doc_source FROM TABLES WITH (UPDLOCK, ROWLOCK)
WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)";

        public const string UpsertTableSqlPg = @"INSERT INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, data_source_type, http_endpoint_json, plugin_name, description, long_description, doc_source, required_filter_columns_json, texts_json, min_group_size, non_personal)
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

        public const string UpsertTableSqlSs = @"MERGE dbo.TABLES WITH (HOLDLOCK) AS t
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
    texts_json = @textsJson,
    min_group_size = @minGroupSize,
    non_personal = @nonPersonal
WHEN NOT MATCHED THEN INSERT (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, data_source_type, http_endpoint_json, plugin_name, description, long_description, doc_source, required_filter_columns_json, texts_json, min_group_size, non_personal)
    VALUES (@id, @sourceType, @domain, @schema, @tableName, @displayName, @sensitivity, @fourEyes, @active, @dst, @httpJson, @plugin, @desc, @longDesc, @docSource, @requiredFilterColumnsJson, @textsJson, @minGroupSize, @nonPersonal);";

        public const string InsertColumnSqlPg = @"INSERT INTO TABLE_COLUMNS (id, table_id, column_name, data_type, is_sensitive, description, long_description, doc_source, meta_json, display_name, texts_json)
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

        public const string InsertColumnSqlSs = @"MERGE dbo.TABLE_COLUMNS WITH (HOLDLOCK) AS t
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

        public const string ActiveTableIdentifierSql = "SELECT source_name, schema_name, table_name FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table) AND is_active = 1";

        public const string TableEpochSql = @"SELECT epoch FROM POLICY_EPOCHS
WHERE LOWER(domain) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)";

        public const string RelationsForParentTableSqlPg = @"SELECT r.id, r.parent_table_id, r.child_table_id, r.relation_name, r.cardinality,
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

        public const string RelationsForParentTableSqlSs = @"SELECT r.id, r.parent_table_id, r.child_table_id, r.relation_name, r.cardinality,
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

        public const string AllRelationsSqlPg = @"SELECT r.id, r.parent_table_id, r.child_table_id, r.relation_name, r.cardinality,
       COALESCE(tp.source_name, 'default'), tp.schema_name, tp.table_name,
       COALESCE(tc.source_name, 'default'), tc.schema_name, tc.table_name,
       k.key_ordinal, k.parent_column, k.child_column,
       COALESCE(r.allow_cross_data_source, FALSE)
FROM TABLE_RELATIONS r
JOIN TABLES tp ON r.parent_table_id = tp.id
JOIN TABLES tc ON r.child_table_id = tc.id
LEFT JOIN TABLE_RELATION_KEYS k ON k.relation_id = r.id
ORDER BY r.id, k.key_ordinal";

        public const string AllRelationsSqlSs = @"SELECT r.id, r.parent_table_id, r.child_table_id, r.relation_name, r.cardinality,
       COALESCE(tp.source_name, 'default'), tp.schema_name, tp.table_name,
       COALESCE(tc.source_name, 'default'), tc.schema_name, tc.table_name,
       k.key_ordinal, k.parent_column, k.child_column,
       COALESCE(r.allow_cross_data_source, 0)
FROM TABLE_RELATIONS r
JOIN TABLES tp ON r.parent_table_id = tp.id
JOIN TABLES tc ON r.child_table_id = tc.id
LEFT JOIN TABLE_RELATION_KEYS k ON k.relation_id = r.id
ORDER BY r.id, k.key_ordinal";

        public const string InsertRelationSqlPg = @"INSERT INTO TABLE_RELATIONS (id, parent_table_id, child_table_id, relation_name, cardinality, allow_cross_data_source)
VALUES (@id, @pId, @cId, @relName, @card, @allowCross)
ON CONFLICT (parent_table_id, child_table_id, relation_name) DO UPDATE SET cardinality = EXCLUDED.cardinality
RETURNING id, (xmax = 0) AS inserted";

        public const string InsertRelationSqlSs = @"MERGE TABLE_RELATIONS WITH (HOLDLOCK) AS target
USING (SELECT @pId AS parent_table_id, @cId AS child_table_id, @relName AS relation_name) AS source
ON target.parent_table_id = source.parent_table_id AND target.child_table_id = source.child_table_id AND target.relation_name = source.relation_name
WHEN MATCHED THEN UPDATE SET cardinality = @card
WHEN NOT MATCHED THEN INSERT (id, parent_table_id, child_table_id, relation_name, cardinality, allow_cross_data_source)
    VALUES (@id, @pId, @cId, @relName, @card, @allowCross)
OUTPUT inserted.id, $action;";

        public const string ResolveActiveTableIdSql = "SELECT id FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table) AND is_active = 1";

        public const string DataOwnersForTableSql = @"SELECT o.id, o.ad_sid, o.ad_account, o.display_name, o.email, o.is_active
FROM DATA_OWNERS o
JOIN TABLE_OWNERS tow ON o.id = tow.data_owner_id
JOIN TABLES t ON tow.table_id = t.id
WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table) AND o.is_active = 1";

        public const string ResolveDataOwnerIdsSql = @"SELECT id FROM DATA_OWNERS
WHERE LOWER(ad_sid) = LOWER(@p) OR LOWER(ad_account) = LOWER(@p) OR LOWER(email) = LOWER(@p)";

        public const string InsertDelegationSqlPg = @"INSERT INTO DATA_OWNER_DELEGATIONS (id, data_owner_id, delegate_sid, valid_from, valid_to, reason)
VALUES (@id, @ownerId, @delegateSid, @validFrom, @validTo, @reason)
ON CONFLICT (id) DO UPDATE SET
    delegate_sid = EXCLUDED.delegate_sid,
    valid_from = EXCLUDED.valid_from,
    valid_to = EXCLUDED.valid_to,
    reason = EXCLUDED.reason";

        public const string InsertDelegationSqlSs = @"MERGE DATA_OWNER_DELEGATIONS WITH (HOLDLOCK) AS t
USING (SELECT @id AS id) AS s
ON t.id = s.id
WHEN MATCHED THEN UPDATE SET
    delegate_sid = @delegateSid,
    valid_from = @validFrom,
    valid_to = @validTo,
    reason = @reason
WHEN NOT MATCHED THEN INSERT (id, data_owner_id, delegate_sid, valid_from, valid_to, reason)
    VALUES (@id, @ownerId, @delegateSid, @validFrom, @validTo, @reason);";

        public const string CatalogVersionSqlPg = "SELECT structure_version, content_version FROM catalog_state WHERE id = 1;";

        public const string CatalogVersionSqlSs = "SELECT structure_version, content_version FROM CATALOG_STATE WHERE id = 1;";

        public const string BumpCatalogVersionSqlPg = @"UPDATE catalog_state
SET structure_version = structure_version + @structureDelta,
    content_version = content_version + @contentDelta,
    updated_at = @now::timestamptz
WHERE id = 1
RETURNING structure_version, content_version;";

        public const string BumpCatalogVersionSqlSs = @"UPDATE CATALOG_STATE
SET structure_version = structure_version + @structureDelta,
    content_version = content_version + @contentDelta,
    updated_at = @now
OUTPUT inserted.structure_version, inserted.content_version
WHERE id = 1;";
    }
}
