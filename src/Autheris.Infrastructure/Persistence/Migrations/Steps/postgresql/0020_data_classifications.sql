-- phase3: MIG-0020

CREATE TABLE IF NOT EXISTS data_classifications (
    scope VARCHAR(64) NOT NULL,
    code VARCHAR(64) NOT NULL,
    display_name VARCHAR(256) NOT NULL,
    description VARCHAR(1024) NULL,
    parent_code VARCHAR(64) NULL,
    restriction_rank SMALLINT NOT NULL,
    clear_grantable BOOLEAN NOT NULL,
    separate_approval BOOLEAN NOT NULL,
    approver_mode VARCHAR(24) NOT NULL,
    steward_group_sid VARCHAR(256) NULL,
    max_clear_days INTEGER NULL,
    is_active BOOLEAN NOT NULL,
    version INTEGER NOT NULL,
    updated_by VARCHAR(256) NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT pk_data_classifications PRIMARY KEY (scope, code),
    CONSTRAINT ck_data_classifications_rank CHECK (restriction_rank >= 0 AND restriction_rank <= 100),
    CONSTRAINT ck_data_classifications_approver_mode CHECK (approver_mode IN ('DATA_OWNER', 'STEWARD', 'DATA_OWNER_AND_STEWARD')),
    CONSTRAINT ck_data_classifications_steward CHECK (approver_mode = 'DATA_OWNER' OR steward_group_sid IS NOT NULL)
);

CREATE TABLE IF NOT EXISTS classification_mask_rules (
    scope VARCHAR(64) NOT NULL,
    code VARCHAR(64) NOT NULL,
    mask_function VARCHAR(32) NOT NULL,
    params_json TEXT NULL,
    key_ref VARCHAR(128) NULL,
    catalog_text VARCHAR(8) NOT NULL DEFAULT 'VISIBLE',
    version INTEGER NOT NULL,
    updated_by VARCHAR(256) NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT pk_classification_mask_rules PRIMARY KEY (scope, code),
    CONSTRAINT ck_classification_mask_rules_catalog_text CHECK (catalog_text IN ('VISIBLE', 'HIDDEN'))
);

CREATE TABLE IF NOT EXISTS column_classifications (
    scope VARCHAR(64) NOT NULL,
    table_column_id TEXT NOT NULL REFERENCES table_columns(id) ON DELETE CASCADE,
    code VARCHAR(64) NOT NULL,
    source VARCHAR(16) NOT NULL,
    assigned_by VARCHAR(256) NOT NULL,
    assigned_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT pk_column_classifications PRIMARY KEY (scope, table_column_id, code)
);

CREATE INDEX IF NOT EXISTS ix_column_classifications_col
    ON column_classifications (table_column_id);

CREATE TABLE IF NOT EXISTS column_mask_overrides (
    id UUID NOT NULL,
    scope VARCHAR(64) NOT NULL,
    table_column_id TEXT NOT NULL REFERENCES table_columns(id) ON DELETE CASCADE,
    code VARCHAR(64) NULL,
    mask_function VARCHAR(32) NOT NULL,
    params_json TEXT NULL,
    key_ref VARCHAR(128) NULL,
    catalog_text VARCHAR(8) NOT NULL DEFAULT 'VISIBLE',
    updated_by VARCHAR(256) NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT pk_column_mask_overrides PRIMARY KEY (id),
    CONSTRAINT ck_column_mask_overrides_catalog_text CHECK (catalog_text IN ('VISIBLE', 'HIDDEN'))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_column_mask_overrides_code
    ON column_mask_overrides (scope, table_column_id, code)
    WHERE code IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_column_mask_overrides_all
    ON column_mask_overrides (scope, table_column_id)
    WHERE code IS NULL;

INSERT INTO data_classifications (scope, code, display_name, description, parent_code, restriction_rank, clear_grantable, separate_approval, approver_mode, steward_group_sid, max_clear_days, is_active, version, updated_by, updated_at)
VALUES
    ('*', 'PUBLIC', 'Public', 'Public non-sensitive data', NULL, 0, TRUE, FALSE, 'DATA_OWNER', NULL, NULL, TRUE, 1, 'system:migration-0020', CURRENT_TIMESTAMP),
    ('*', 'CONFIDENTIAL', 'Confidential', 'Internal confidential business data', NULL, 20, TRUE, TRUE, 'DATA_OWNER', NULL, NULL, TRUE, 1, 'system:migration-0020', CURRENT_TIMESTAMP),
    ('*', 'FINANCIAL', 'Financial', 'Financial and payment information', NULL, 30, TRUE, TRUE, 'DATA_OWNER', NULL, NULL, TRUE, 1, 'system:migration-0020', CURRENT_TIMESTAMP),
    ('*', 'PII', 'Personal Data (PII)', 'Personally identifiable information', NULL, 40, TRUE, TRUE, 'DATA_OWNER', NULL, NULL, TRUE, 1, 'system:migration-0020', CURRENT_TIMESTAMP),
    ('*', 'PII_SPECIAL', 'Special Category PII', 'Special category personal data requiring four-eyes approval', 'PII', 60, TRUE, TRUE, 'DATA_OWNER_AND_STEWARD', 'STEWARD_UNCONFIGURED', NULL, TRUE, 1, 'system:migration-0020', CURRENT_TIMESTAMP),
    ('*', 'CREDENTIALS', 'Credentials & Secrets', 'Authentication secrets, keys, and tokens (never clearable)', NULL, 90, FALSE, TRUE, 'DATA_OWNER', NULL, NULL, TRUE, 1, 'system:migration-0020', CURRENT_TIMESTAMP)
ON CONFLICT (scope, code) DO NOTHING;

INSERT INTO classification_mask_rules (scope, code, mask_function, params_json, key_ref, catalog_text, version, updated_by, updated_at)
VALUES
    ('*', 'CONFIDENTIAL', 'TYPE_SAFE_REDACT', '{"constant":"***"}', NULL, 'VISIBLE', 1, 'system:migration-0020', CURRENT_TIMESTAMP),
    ('*', 'FINANCIAL', 'TYPE_SAFE_REDACT', '{"constant":"***"}', NULL, 'VISIBLE', 1, 'system:migration-0020', CURRENT_TIMESTAMP),
    ('*', 'PII', 'TYPE_SAFE_REDACT', '{"constant":"***"}', NULL, 'VISIBLE', 1, 'system:migration-0020', CURRENT_TIMESTAMP),
    ('*', 'PII_SPECIAL', 'TYPE_SAFE_REDACT', '{"constant":"***"}', NULL, 'VISIBLE', 1, 'system:migration-0020', CURRENT_TIMESTAMP),
    ('*', 'CREDENTIALS', 'TYPE_SAFE_REDACT', '{"constant":"***"}', NULL, 'VISIBLE', 1, 'system:migration-0020', CURRENT_TIMESTAMP),
    ('*', '_DEFAULT', 'TYPE_SAFE_REDACT', '{"constant":"***"}', NULL, 'VISIBLE', 1, 'system:migration-0020', CURRENT_TIMESTAMP)
ON CONFLICT (scope, code) DO NOTHING;

INSERT INTO column_classifications (scope, table_column_id, code, source, assigned_by, assigned_at)
SELECT '*', tc.id, 'PII', 'MIGRATION', 'system:migration-0020', CURRENT_TIMESTAMP
FROM table_columns tc
WHERE tc.is_sensitive = 1
   OR EXISTS (
       SELECT 1
       FROM column_masking_rules m
       WHERE m.table_column_id = tc.id
   )
ON CONFLICT (scope, table_column_id, code) DO NOTHING;

INSERT INTO column_mask_overrides (id, scope, table_column_id, code, mask_function, params_json, key_ref, catalog_text, updated_by, updated_at)
SELECT
    gen_random_uuid(),
    '*',
    ranked.table_column_id,
    NULL,
    CASE UPPER(COALESCE(ranked.rule_type, ''))
        WHEN 'NULLIFY' THEN 'NULLIFY'
        WHEN 'HMAC' THEN 'HASH'
        WHEN 'HMAC_SHA256' THEN 'HASH'
        WHEN 'HASH' THEN 'HASH'
        WHEN 'MASK_EMAIL' THEN 'REDACT'
        WHEN 'MASK_IBAN' THEN 'REDACT'
        WHEN 'PARTIAL_MASK' THEN 'PARTIAL'
        WHEN 'GEO_JITTER' THEN 'GEO_JITTER'
        WHEN 'TOKENIZE' THEN 'TOKENIZE'
        ELSE 'TYPE_SAFE_REDACT'
    END,
    CASE UPPER(COALESCE(ranked.rule_type, ''))
        WHEN 'NULLIFY' THEN NULL
        WHEN 'HMAC' THEN NULL
        WHEN 'HMAC_SHA256' THEN NULL
        WHEN 'HASH' THEN NULL
        WHEN 'MASK_EMAIL' THEN '{"constant":"***@***"}'
        WHEN 'MASK_IBAN' THEN '{"constant":"****"}'
        WHEN 'PARTIAL_MASK' THEN CASE
            WHEN ranked.pattern_or_format IS NOT NULL THEN '{"keep_first":1,"keep_last":0,"mask_char":"*","pattern_or_format":' || to_json(ranked.pattern_or_format)::text || '}'
            ELSE '{"keep_first":1,"keep_last":0,"mask_char":"*"}'
        END
        WHEN 'GEO_JITTER' THEN '{"decimals":2}'
        WHEN 'TOKENIZE' THEN CASE
            WHEN ranked.pattern_or_format IS NOT NULL THEN '{"domain":' || to_json(ranked.pattern_or_format)::text || '}'
            ELSE '{"domain":"default"}'
        END
        ELSE CASE
            WHEN ranked.replacement IS NOT NULL THEN '{"constant":' || to_json(ranked.replacement)::text || '}'
            ELSE '{"constant":"***"}'
        END
    END,
    CASE UPPER(COALESCE(ranked.rule_type, ''))
        WHEN 'HMAC' THEN ranked.hmac_key_id
        WHEN 'HMAC_SHA256' THEN ranked.hmac_key_id
        WHEN 'HASH' THEN ranked.hmac_key_id
        WHEN 'TOKENIZE' THEN ranked.hmac_key_id
        ELSE NULL
    END,
    'VISIBLE',
    'system:migration-0020',
    CURRENT_TIMESTAMP
FROM (
    SELECT
        r.table_column_id,
        r.rule_type,
        r.pattern_or_format,
        r.replacement,
        r.hmac_key_id,
        ROW_NUMBER() OVER (PARTITION BY r.table_column_id ORDER BY r.id) AS rn
    FROM column_masking_rules r
    INNER JOIN table_columns tc ON tc.id = r.table_column_id
) ranked
WHERE ranked.rn = 1
  AND NOT EXISTS (
      SELECT 1
      FROM column_mask_overrides o
      WHERE o.scope = '*'
        AND o.table_column_id = ranked.table_column_id
        AND o.code IS NULL
  );
