namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public partial class GovernanceRepository
{
    internal static class ClassificationsSql
    {
        public const string SelectClassificationsActiveGlobalOnlySql = @"SELECT scope, code, display_name, description, parent_code, restriction_rank,
       clear_grantable, separate_approval, approver_mode, steward_group_sid,
       max_clear_days, is_active, version, updated_by, updated_at
FROM DATA_CLASSIFICATIONS
WHERE scope = '*' AND is_active = @active
ORDER BY restriction_rank ASC, code ASC, scope ASC";

        public const string SelectClassificationsAllGlobalOnlySql = @"SELECT scope, code, display_name, description, parent_code, restriction_rank,
       clear_grantable, separate_approval, approver_mode, steward_group_sid,
       max_clear_days, is_active, version, updated_by, updated_at
FROM DATA_CLASSIFICATIONS
WHERE scope = '*'
ORDER BY restriction_rank ASC, code ASC, scope ASC";

        public const string SelectClassificationsActiveScopedSql = @"SELECT scope, code, display_name, description, parent_code, restriction_rank,
       clear_grantable, separate_approval, approver_mode, steward_group_sid,
       max_clear_days, is_active, version, updated_by, updated_at
FROM DATA_CLASSIFICATIONS
WHERE (scope = '*' OR scope = @scope) AND is_active = @active
ORDER BY restriction_rank ASC, code ASC, scope ASC";

        public const string SelectClassificationsAllScopedSql = @"SELECT scope, code, display_name, description, parent_code, restriction_rank,
       clear_grantable, separate_approval, approver_mode, steward_group_sid,
       max_clear_days, is_active, version, updated_by, updated_at
FROM DATA_CLASSIFICATIONS
WHERE scope = '*' OR scope = @scope
ORDER BY restriction_rank ASC, code ASC, scope ASC";

        public const string SelectClassificationByKeySql = @"SELECT scope, code, display_name, description, parent_code, restriction_rank,
       clear_grantable, separate_approval, approver_mode, steward_group_sid,
       max_clear_days, is_active, version, updated_by, updated_at
FROM DATA_CLASSIFICATIONS
WHERE scope = @scope AND code = @code";

        public const string UpsertClassificationSqlPg = @"INSERT INTO DATA_CLASSIFICATIONS
(scope, code, display_name, description, parent_code, restriction_rank,
 clear_grantable, separate_approval, approver_mode, steward_group_sid,
 max_clear_days, is_active, version, updated_by, updated_at)
VALUES
(@scope, @code, @display_name, @description, @parent_code, @restriction_rank,
 @clear_grantable, @separate_approval, @approver_mode, @steward_group_sid,
 @max_clear_days, @is_active, @version, @updated_by, @updated_at)
ON CONFLICT (scope, code) DO UPDATE SET
    display_name = EXCLUDED.display_name,
    description = EXCLUDED.description,
    parent_code = EXCLUDED.parent_code,
    restriction_rank = EXCLUDED.restriction_rank,
    clear_grantable = EXCLUDED.clear_grantable,
    separate_approval = EXCLUDED.separate_approval,
    approver_mode = EXCLUDED.approver_mode,
    steward_group_sid = EXCLUDED.steward_group_sid,
    max_clear_days = EXCLUDED.max_clear_days,
    is_active = EXCLUDED.is_active,
    version = DATA_CLASSIFICATIONS.version + 1,
    updated_by = EXCLUDED.updated_by,
    updated_at = EXCLUDED.updated_at";

        public const string UpsertClassificationSqlSs = @"MERGE DATA_CLASSIFICATIONS WITH (HOLDLOCK) AS t
USING (SELECT @scope AS scope, @code AS code) AS s
ON t.scope = s.scope AND t.code = s.code
WHEN MATCHED THEN UPDATE SET
    display_name = @display_name,
    description = @description,
    parent_code = @parent_code,
    restriction_rank = @restriction_rank,
    clear_grantable = @clear_grantable,
    separate_approval = @separate_approval,
    approver_mode = @approver_mode,
    steward_group_sid = @steward_group_sid,
    max_clear_days = @max_clear_days,
    is_active = @is_active,
    version = t.version + 1,
    updated_by = @updated_by,
    updated_at = @updated_at
WHEN NOT MATCHED THEN INSERT
    (scope, code, display_name, description, parent_code, restriction_rank,
     clear_grantable, separate_approval, approver_mode, steward_group_sid,
     max_clear_days, is_active, version, updated_by, updated_at)
VALUES
    (@scope, @code, @display_name, @description, @parent_code, @restriction_rank,
     @clear_grantable, @separate_approval, @approver_mode, @steward_group_sid,
     @max_clear_days, @is_active, @version, @updated_by, @updated_at);";

        public const string SelectMaskRulesGlobalOnlySql = @"SELECT scope, code, mask_function, params_json, key_ref, catalog_text, version, updated_by, updated_at
FROM CLASSIFICATION_MASK_RULES
WHERE scope = '*'
ORDER BY code ASC, scope ASC";

        public const string SelectMaskRulesScopedSql = @"SELECT scope, code, mask_function, params_json, key_ref, catalog_text, version, updated_by, updated_at
FROM CLASSIFICATION_MASK_RULES
WHERE scope = '*' OR scope = @scope
ORDER BY code ASC, scope ASC";

        public const string SelectMaskRuleByKeySql = @"SELECT scope, code, mask_function, params_json, key_ref, catalog_text, version, updated_by, updated_at
FROM CLASSIFICATION_MASK_RULES
WHERE scope = @scope AND code = @code";

        public const string UpsertMaskRuleSqlPg = @"INSERT INTO CLASSIFICATION_MASK_RULES
(scope, code, mask_function, params_json, key_ref, catalog_text, version, updated_by, updated_at)
VALUES
(@scope, @code, @mask_function, @params_json, @key_ref, @catalog_text, @version, @updated_by, @updated_at)
ON CONFLICT (scope, code) DO UPDATE SET
    mask_function = EXCLUDED.mask_function,
    params_json = EXCLUDED.params_json,
    key_ref = EXCLUDED.key_ref,
    catalog_text = EXCLUDED.catalog_text,
    version = CLASSIFICATION_MASK_RULES.version + 1,
    updated_by = EXCLUDED.updated_by,
    updated_at = EXCLUDED.updated_at";

        public const string UpsertMaskRuleSqlSs = @"MERGE CLASSIFICATION_MASK_RULES WITH (HOLDLOCK) AS t
USING (SELECT @scope AS scope, @code AS code) AS s
ON t.scope = s.scope AND t.code = s.code
WHEN MATCHED THEN UPDATE SET
    mask_function = @mask_function,
    params_json = @params_json,
    key_ref = @key_ref,
    catalog_text = @catalog_text,
    version = t.version + 1,
    updated_by = @updated_by,
    updated_at = @updated_at
WHEN NOT MATCHED THEN INSERT
    (scope, code, mask_function, params_json, key_ref, catalog_text, version, updated_by, updated_at)
VALUES
    (@scope, @code, @mask_function, @params_json, @key_ref, @catalog_text, @version, @updated_by, @updated_at);";



        public const string UpsertColumnClassificationSqlPg = @"INSERT INTO COLUMN_CLASSIFICATIONS
(scope, table_column_id, code, source, assigned_by, assigned_at)
VALUES (@scope, @table_column_id, @code, @source, @assigned_by, @assigned_at)
ON CONFLICT (scope, table_column_id, code) DO UPDATE SET
    source = EXCLUDED.source,
    assigned_by = EXCLUDED.assigned_by,
    assigned_at = EXCLUDED.assigned_at";

        public const string UpsertColumnClassificationSqlSs = @"MERGE COLUMN_CLASSIFICATIONS WITH (HOLDLOCK) AS t
USING (SELECT @scope AS scope, @table_column_id AS table_column_id, @code AS code) AS s
ON t.scope = s.scope AND t.table_column_id = s.table_column_id AND t.code = s.code
WHEN MATCHED THEN UPDATE SET
    source = @source,
    assigned_by = @assigned_by,
    assigned_at = @assigned_at
WHEN NOT MATCHED THEN INSERT
    (scope, table_column_id, code, source, assigned_by, assigned_at)
VALUES
    (@scope, @table_column_id, @code, @source, @assigned_by, @assigned_at);";

        public const string DeleteColumnClassificationSql = @"DELETE FROM COLUMN_CLASSIFICATIONS
WHERE scope = @scope AND table_column_id = @table_column_id AND code = @code";



        // Issue #1043: classifications / mask overrides of a set of columns (one column = the former per-column read). @scope is the tenant,
        // or '*' for the global rows only; the caller appends BindIdInClause placeholders and SelectColumnBatchSuffix.
        public const string SelectColumnClassificationsBatchPrefix = "SELECT scope, table_column_id, code, source, assigned_by, assigned_at FROM COLUMN_CLASSIFICATIONS WHERE (scope = '*' OR scope = @scope) AND table_column_id IN (";
        public const string SelectColumnMaskOverridesBatchPrefix = "SELECT id, scope, table_column_id, code, mask_function, params_json, key_ref, catalog_text, updated_by, updated_at FROM COLUMN_MASK_OVERRIDES WHERE (scope = '*' OR scope = @scope) AND table_column_id IN (";
        public const string SelectColumnBatchSuffix = ") ORDER BY table_column_id ASC, code ASC, scope ASC";






        public const string UpsertColumnMaskOverrideWithCodeSqlPg = @"INSERT INTO COLUMN_MASK_OVERRIDES
(id, scope, table_column_id, code, mask_function, params_json, key_ref, catalog_text, updated_by, updated_at)
VALUES (@id, @scope, @table_column_id, @code, @mask_function, @params_json, @key_ref, @catalog_text, @updated_by, @updated_at)
ON CONFLICT (scope, table_column_id, code) WHERE code IS NOT NULL DO UPDATE SET
    mask_function = EXCLUDED.mask_function,
    params_json = EXCLUDED.params_json,
    key_ref = EXCLUDED.key_ref,
    catalog_text = EXCLUDED.catalog_text,
    updated_by = EXCLUDED.updated_by,
    updated_at = EXCLUDED.updated_at
RETURNING id";

        public const string UpsertColumnMaskOverrideAllSqlPg = @"INSERT INTO COLUMN_MASK_OVERRIDES
(id, scope, table_column_id, code, mask_function, params_json, key_ref, catalog_text, updated_by, updated_at)
VALUES (@id, @scope, @table_column_id, NULL, @mask_function, @params_json, @key_ref, @catalog_text, @updated_by, @updated_at)
ON CONFLICT (scope, table_column_id) WHERE code IS NULL DO UPDATE SET
    mask_function = EXCLUDED.mask_function,
    params_json = EXCLUDED.params_json,
    key_ref = EXCLUDED.key_ref,
    catalog_text = EXCLUDED.catalog_text,
    updated_by = EXCLUDED.updated_by,
    updated_at = EXCLUDED.updated_at
RETURNING id";

        public const string UpsertColumnMaskOverrideWithCodeSqlSs = @"MERGE COLUMN_MASK_OVERRIDES WITH (HOLDLOCK) AS t
USING (SELECT @scope AS scope, @table_column_id AS table_column_id, @code AS code) AS s
ON t.scope = s.scope AND t.table_column_id = s.table_column_id AND t.code = s.code
WHEN MATCHED THEN UPDATE SET
    mask_function = @mask_function,
    params_json = @params_json,
    key_ref = @key_ref,
    catalog_text = @catalog_text,
    updated_by = @updated_by,
    updated_at = @updated_at
WHEN NOT MATCHED THEN INSERT
    (id, scope, table_column_id, code, mask_function, params_json, key_ref, catalog_text, updated_by, updated_at)
VALUES
    (@id, @scope, @table_column_id, @code, @mask_function, @params_json, @key_ref, @catalog_text, @updated_by, @updated_at)
OUTPUT inserted.id;";

        public const string UpsertColumnMaskOverrideAllSqlSs = @"MERGE COLUMN_MASK_OVERRIDES WITH (HOLDLOCK) AS t
USING (SELECT @scope AS scope, @table_column_id AS table_column_id) AS s
ON t.scope = s.scope AND t.table_column_id = s.table_column_id AND t.code IS NULL
WHEN MATCHED THEN UPDATE SET
    mask_function = @mask_function,
    params_json = @params_json,
    key_ref = @key_ref,
    catalog_text = @catalog_text,
    updated_by = @updated_by,
    updated_at = @updated_at
WHEN NOT MATCHED THEN INSERT
    (id, scope, table_column_id, code, mask_function, params_json, key_ref, catalog_text, updated_by, updated_at)
VALUES
    (@id, @scope, @table_column_id, NULL, @mask_function, @params_json, @key_ref, @catalog_text, @updated_by, @updated_at)
OUTPUT inserted.id;";

        public const string DeleteColumnMaskOverrideWithCodeSql = @"DELETE FROM COLUMN_MASK_OVERRIDES
WHERE scope = @scope AND table_column_id = @table_column_id AND code = @code";

        public const string DeleteColumnMaskOverrideAllSql = @"DELETE FROM COLUMN_MASK_OVERRIDES
WHERE scope = @scope AND table_column_id = @table_column_id AND code IS NULL";

        public const string SelectTableIdentifierByColumnIdSql = @"SELECT t.source_name, t.schema_name, t.table_name
FROM TABLE_COLUMNS c
JOIN TABLES t ON c.table_id = t.id
WHERE c.id = @table_column_id";

        public static string? ValidateOptionalTenantFilter(string? tenantScope)
        {
            if (tenantScope is null || ClassificationScopes.IsGlobal(tenantScope))
            {
                return null;
            }

            return new TenantId(tenantScope).Value;
        }

        public static void ValidateClassificationCodeArg(string code, string paramName)
        {
            if (!ClassificationHierarchyValidator.IsValidClassificationCode(code))
            {
                throw new ArgumentException($"Invalid classification code '{code}'.", paramName);
            }
        }

        public static void ValidateMaskRuleCodeArg(string code, string paramName)
        {
            if (!ClassificationHierarchyValidator.IsValidMaskRuleCode(code))
            {
                throw new ArgumentException($"Invalid classification mask rule code '{code}'.", paramName);
            }
        }

        public static IReadOnlyDictionary<string, DataClassification> BuildEffectiveCatalog(
            IReadOnlyList<DataClassification> rows,
            string targetScope)
        {
            var map = new Dictionary<string, DataClassification>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (ClassificationScopes.IsGlobal(row.Scope))
                {
                    map[row.Code] = row;
                }
            }

            if (!ClassificationScopes.IsGlobal(targetScope))
            {
                foreach (var row in rows)
                {
                    if (string.Equals(row.Scope, targetScope, StringComparison.Ordinal))
                    {
                        map[row.Code] = row;
                    }
                }
            }

            return map;
        }

        public static DataClassification ReadClassification(DbDataReader reader)
        {
            var approverCode = reader.GetString(8);
            if (!ClassificationApproverModes.TryParse(approverCode, out var approverMode))
            {
                throw new InvalidOperationException($"Unknown approver mode '{approverCode}'.");
            }

            return new DataClassification
            {
                Scope = reader.GetString(0),
                Code = reader.GetString(1),
                DisplayName = reader.GetString(2),
                Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                ParentCode = reader.IsDBNull(4) ? null : reader.GetString(4),
                RestrictionRank = reader.GetInt16(5),
                ClearGrantable = reader.GetBoolean(6),
                SeparateApproval = reader.GetBoolean(7),
                ApproverMode = approverMode,
                StewardGroupSid = reader.IsDBNull(9) ? null : reader.GetString(9),
                MaxClearDays = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                IsActive = reader.GetBoolean(11),
                Version = reader.GetInt32(12),
                UpdatedBy = reader.GetString(13),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(14)
            };
        }

        public static ClassificationMaskRule ReadMaskRule(DbDataReader reader)
        {
            var fnCode = reader.GetString(2);
            if (!ClassificationMaskFunctions.TryParse(fnCode, out var maskFunction))
            {
                throw new InvalidOperationException($"Unknown mask function '{fnCode}'.");
            }

            var visibilityCode = reader.GetString(5);
            if (!ClassificationCatalogTextVisibilities.TryParse(visibilityCode, out var catalogText))
            {
                throw new InvalidOperationException($"Unknown catalog text visibility '{visibilityCode}'.");
            }

            return new ClassificationMaskRule
            {
                Scope = reader.GetString(0),
                Code = reader.GetString(1),
                MaskFunction = maskFunction,
                ParamsJson = reader.IsDBNull(3) ? null : reader.GetString(3),
                KeyRef = reader.IsDBNull(4) ? null : reader.GetString(4),
                CatalogText = catalogText,
                Version = reader.GetInt32(6),
                UpdatedBy = reader.GetString(7),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(8)
            };
        }

        public static ColumnMaskOverride ReadColumnMaskOverride(DbDataReader reader)
        {
            var fnCode = reader.GetString(4);
            if (!ClassificationMaskFunctions.TryParse(fnCode, out var maskFunction))
            {
                throw new InvalidOperationException($"Unknown mask function '{fnCode}'.");
            }

            var visibilityCode = reader.GetString(7);
            if (!ClassificationCatalogTextVisibilities.TryParse(visibilityCode, out var catalogText))
            {
                throw new InvalidOperationException($"Unknown catalog text visibility '{visibilityCode}'.");
            }

            return new ColumnMaskOverride
            {
                Id = reader.GetGuid(0),
                Scope = reader.GetString(1),
                TableColumnId = reader.GetString(2),
                Code = reader.IsDBNull(3) ? null : reader.GetString(3),
                MaskFunction = maskFunction,
                ParamsJson = reader.IsDBNull(5) ? null : reader.GetString(5),
                KeyRef = reader.IsDBNull(6) ? null : reader.GetString(6),
                CatalogText = catalogText,
                UpdatedBy = reader.GetString(8),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(9)
            };
        }
    }
}
