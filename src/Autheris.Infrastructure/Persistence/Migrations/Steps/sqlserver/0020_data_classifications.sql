-- phase3: MIG-0020

IF OBJECT_ID(N'dbo.DATA_CLASSIFICATIONS', N'U') IS NULL
CREATE TABLE dbo.DATA_CLASSIFICATIONS (
    scope NVARCHAR(64) NOT NULL,
    code NVARCHAR(64) NOT NULL,
    display_name NVARCHAR(256) NOT NULL,
    description NVARCHAR(1024) NULL,
    parent_code NVARCHAR(64) NULL,
    restriction_rank SMALLINT NOT NULL,
    clear_grantable BIT NOT NULL,
    separate_approval BIT NOT NULL,
    approver_mode NVARCHAR(24) NOT NULL,
    steward_group_sid NVARCHAR(256) NULL,
    max_clear_days INT NULL,
    is_active BIT NOT NULL,
    version INT NOT NULL,
    updated_by NVARCHAR(256) NOT NULL,
    updated_at DATETIMEOFFSET NOT NULL,
    CONSTRAINT PK_DATA_CLASSIFICATIONS PRIMARY KEY (scope, code),
    CONSTRAINT CK_DATA_CLASSIFICATIONS_RANK CHECK (restriction_rank >= 0 AND restriction_rank <= 100),
    CONSTRAINT CK_DATA_CLASSIFICATIONS_APPROVER_MODE CHECK (approver_mode IN (N'DATA_OWNER', N'STEWARD', N'DATA_OWNER_AND_STEWARD')),
    CONSTRAINT CK_DATA_CLASSIFICATIONS_STEWARD CHECK (approver_mode = N'DATA_OWNER' OR steward_group_sid IS NOT NULL)
);

-- batch

IF OBJECT_ID(N'dbo.CLASSIFICATION_MASK_RULES', N'U') IS NULL
CREATE TABLE dbo.CLASSIFICATION_MASK_RULES (
    scope NVARCHAR(64) NOT NULL,
    code NVARCHAR(64) NOT NULL,
    mask_function NVARCHAR(32) NOT NULL,
    params_json NVARCHAR(MAX) NULL,
    key_ref NVARCHAR(128) NULL,
    catalog_text NVARCHAR(8) NOT NULL CONSTRAINT DF_CLASSIFICATION_MASK_RULES_CATALOG_TEXT DEFAULT N'VISIBLE',
    version INT NOT NULL,
    updated_by NVARCHAR(256) NOT NULL,
    updated_at DATETIMEOFFSET NOT NULL,
    CONSTRAINT PK_CLASSIFICATION_MASK_RULES PRIMARY KEY (scope, code),
    CONSTRAINT CK_CLASSIFICATION_MASK_RULES_CATALOG_TEXT CHECK (catalog_text IN (N'VISIBLE', N'HIDDEN'))
);

-- batch

IF OBJECT_ID(N'dbo.COLUMN_CLASSIFICATIONS', N'U') IS NULL
CREATE TABLE dbo.COLUMN_CLASSIFICATIONS (
    scope NVARCHAR(64) NOT NULL,
    table_column_id NVARCHAR(128) NOT NULL,
    code NVARCHAR(64) NOT NULL,
    source NVARCHAR(16) NOT NULL,
    assigned_by NVARCHAR(256) NOT NULL,
    assigned_at DATETIMEOFFSET NOT NULL,
    CONSTRAINT PK_COLUMN_CLASSIFICATIONS PRIMARY KEY (scope, table_column_id, code),
    CONSTRAINT FK_COLUMN_CLASSIFICATIONS_COL FOREIGN KEY (table_column_id) REFERENCES dbo.TABLE_COLUMNS(id) ON DELETE CASCADE
);

-- batch

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_COLUMN_CLASSIFICATIONS_COL' AND object_id = OBJECT_ID(N'dbo.COLUMN_CLASSIFICATIONS'))
CREATE INDEX IX_COLUMN_CLASSIFICATIONS_COL
    ON dbo.COLUMN_CLASSIFICATIONS (table_column_id);

-- batch

IF OBJECT_ID(N'dbo.COLUMN_MASK_OVERRIDES', N'U') IS NULL
CREATE TABLE dbo.COLUMN_MASK_OVERRIDES (
    id UNIQUEIDENTIFIER NOT NULL,
    scope NVARCHAR(64) NOT NULL,
    table_column_id NVARCHAR(128) NOT NULL,
    code NVARCHAR(64) NULL,
    mask_function NVARCHAR(32) NOT NULL,
    params_json NVARCHAR(MAX) NULL,
    key_ref NVARCHAR(128) NULL,
    catalog_text NVARCHAR(8) NOT NULL CONSTRAINT DF_COLUMN_MASK_OVERRIDES_CATALOG_TEXT DEFAULT N'VISIBLE',
    updated_by NVARCHAR(256) NOT NULL,
    updated_at DATETIMEOFFSET NOT NULL,
    CONSTRAINT PK_COLUMN_MASK_OVERRIDES PRIMARY KEY (id),
    CONSTRAINT FK_COLUMN_MASK_OVERRIDES_COL FOREIGN KEY (table_column_id) REFERENCES dbo.TABLE_COLUMNS(id) ON DELETE CASCADE,
    CONSTRAINT CK_COLUMN_MASK_OVERRIDES_CATALOG_TEXT CHECK (catalog_text IN (N'VISIBLE', N'HIDDEN'))
);

-- batch

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_COLUMN_MASK_OVERRIDES_CODE' AND object_id = OBJECT_ID(N'dbo.COLUMN_MASK_OVERRIDES'))
CREATE UNIQUE INDEX UX_COLUMN_MASK_OVERRIDES_CODE
    ON dbo.COLUMN_MASK_OVERRIDES (scope, table_column_id, code)
    WHERE code IS NOT NULL;

-- batch

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_COLUMN_MASK_OVERRIDES_ALL' AND object_id = OBJECT_ID(N'dbo.COLUMN_MASK_OVERRIDES'))
CREATE UNIQUE INDEX UX_COLUMN_MASK_OVERRIDES_ALL
    ON dbo.COLUMN_MASK_OVERRIDES (scope, table_column_id)
    WHERE code IS NULL;

-- batch

INSERT INTO dbo.DATA_CLASSIFICATIONS (scope, code, display_name, description, parent_code, restriction_rank, clear_grantable, separate_approval, approver_mode, steward_group_sid, max_clear_days, is_active, version, updated_by, updated_at)
SELECT v.scope, v.code, v.display_name, v.description, v.parent_code, v.restriction_rank, v.clear_grantable, v.separate_approval, v.approver_mode, v.steward_group_sid, v.max_clear_days, v.is_active, v.version, N'system:migration-0020', SYSUTCDATETIME()
FROM (VALUES
    (N'*', N'PUBLIC', N'Public', N'Public non-sensitive data', CAST(NULL AS NVARCHAR(64)), CAST(0 AS SMALLINT), CAST(1 AS BIT), CAST(0 AS BIT), N'DATA_OWNER', CAST(NULL AS NVARCHAR(256)), CAST(NULL AS INT), CAST(1 AS BIT), 1),
    (N'*', N'CONFIDENTIAL', N'Confidential', N'Internal confidential business data', CAST(NULL AS NVARCHAR(64)), CAST(20 AS SMALLINT), CAST(1 AS BIT), CAST(1 AS BIT), N'DATA_OWNER', CAST(NULL AS NVARCHAR(256)), CAST(NULL AS INT), CAST(1 AS BIT), 1),
    (N'*', N'FINANCIAL', N'Financial', N'Financial and payment information', CAST(NULL AS NVARCHAR(64)), CAST(30 AS SMALLINT), CAST(1 AS BIT), CAST(1 AS BIT), N'DATA_OWNER', CAST(NULL AS NVARCHAR(256)), CAST(NULL AS INT), CAST(1 AS BIT), 1),
    (N'*', N'PII', N'Personal Data (PII)', N'Personally identifiable information', CAST(NULL AS NVARCHAR(64)), CAST(40 AS SMALLINT), CAST(1 AS BIT), CAST(1 AS BIT), N'DATA_OWNER', CAST(NULL AS NVARCHAR(256)), CAST(NULL AS INT), CAST(1 AS BIT), 1),
    (N'*', N'PII_SPECIAL', N'Special Category PII', N'Special category personal data requiring four-eyes approval', N'PII', CAST(60 AS SMALLINT), CAST(1 AS BIT), CAST(1 AS BIT), N'DATA_OWNER_AND_STEWARD', N'STEWARD_UNCONFIGURED', CAST(NULL AS INT), CAST(1 AS BIT), 1),
    (N'*', N'CREDENTIALS', N'Credentials & Secrets', N'Authentication secrets, keys, and tokens (never clearable)', CAST(NULL AS NVARCHAR(64)), CAST(90 AS SMALLINT), CAST(0 AS BIT), CAST(1 AS BIT), N'DATA_OWNER', CAST(NULL AS NVARCHAR(256)), CAST(NULL AS INT), CAST(1 AS BIT), 1)
) AS v(scope, code, display_name, description, parent_code, restriction_rank, clear_grantable, separate_approval, approver_mode, steward_group_sid, max_clear_days, is_active, version)
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.DATA_CLASSIFICATIONS existing
    WHERE existing.scope = v.scope
      AND existing.code = v.code
);

-- batch

INSERT INTO dbo.CLASSIFICATION_MASK_RULES (scope, code, mask_function, params_json, key_ref, catalog_text, version, updated_by, updated_at)
SELECT v.scope, v.code, N'TYPE_SAFE_REDACT', N'{"constant":"***"}', CAST(NULL AS NVARCHAR(128)), N'VISIBLE', 1, N'system:migration-0020', SYSUTCDATETIME()
FROM (VALUES
    (N'*', N'CONFIDENTIAL'),
    (N'*', N'FINANCIAL'),
    (N'*', N'PII'),
    (N'*', N'PII_SPECIAL'),
    (N'*', N'CREDENTIALS'),
    (N'*', N'_DEFAULT')
) AS v(scope, code)
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.CLASSIFICATION_MASK_RULES existing
    WHERE existing.scope = v.scope
      AND existing.code = v.code
);

-- batch

INSERT INTO dbo.COLUMN_CLASSIFICATIONS (scope, table_column_id, code, source, assigned_by, assigned_at)
SELECT N'*', tc.id, N'PII', N'MIGRATION', N'system:migration-0020', SYSUTCDATETIME()
FROM dbo.TABLE_COLUMNS tc
WHERE (
    tc.is_sensitive = 1
    OR EXISTS (
        SELECT 1
        FROM dbo.COLUMN_MASKING_RULES m
        WHERE m.table_column_id = tc.id
    )
)
AND NOT EXISTS (
    SELECT 1
    FROM dbo.COLUMN_CLASSIFICATIONS cc
    WHERE cc.scope = N'*'
      AND cc.table_column_id = tc.id
      AND cc.code = N'PII'
);

-- batch

INSERT INTO dbo.COLUMN_MASK_OVERRIDES (id, scope, table_column_id, code, mask_function, params_json, key_ref, catalog_text, updated_by, updated_at)
SELECT
    NEWID(),
    N'*',
    ranked.table_column_id,
    NULL,
    CASE UPPER(COALESCE(ranked.rule_type, N''))
        WHEN N'NULLIFY' THEN N'NULLIFY'
        WHEN N'HMAC' THEN N'HASH'
        WHEN N'HMAC_SHA256' THEN N'HASH'
        WHEN N'HASH' THEN N'HASH'
        WHEN N'MASK_EMAIL' THEN N'REDACT'
        WHEN N'MASK_IBAN' THEN N'REDACT'
        WHEN N'PARTIAL_MASK' THEN N'PARTIAL'
        WHEN N'GEO_JITTER' THEN N'GEO_JITTER'
        WHEN N'TOKENIZE' THEN N'TOKENIZE'
        ELSE N'TYPE_SAFE_REDACT'
    END,
    CASE UPPER(COALESCE(ranked.rule_type, N''))
        WHEN N'NULLIFY' THEN NULL
        WHEN N'HMAC' THEN NULL
        WHEN N'HMAC_SHA256' THEN NULL
        WHEN N'HASH' THEN NULL
        WHEN N'MASK_EMAIL' THEN N'{"constant":"***@***"}'
        WHEN N'MASK_IBAN' THEN N'{"constant":"****"}'
        WHEN N'PARTIAL_MASK' THEN CASE
            WHEN ranked.pattern_or_format IS NOT NULL THEN N'{"keep_first":1,"keep_last":0,"mask_char":"*","pattern_or_format":"' + REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ranked.pattern_or_format, N'\', N'\\'), N'"', N'\"'), CHAR(13), N'\r'), CHAR(10), N'\n'), CHAR(9), N'\t') + N'"}'
            ELSE N'{"keep_first":1,"keep_last":0,"mask_char":"*"}'
        END
        WHEN N'GEO_JITTER' THEN N'{"decimals":2}'
        WHEN N'TOKENIZE' THEN CASE
            WHEN ranked.pattern_or_format IS NOT NULL THEN N'{"domain":"' + REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ranked.pattern_or_format, N'\', N'\\'), N'"', N'\"'), CHAR(13), N'\r'), CHAR(10), N'\n'), CHAR(9), N'\t') + N'"}'
            ELSE N'{"domain":"default"}'
        END
        ELSE CASE
            WHEN ranked.replacement IS NOT NULL THEN N'{"constant":"' + REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ranked.replacement, N'\', N'\\'), N'"', N'\"'), CHAR(13), N'\r'), CHAR(10), N'\n'), CHAR(9), N'\t') + N'"}'
            ELSE N'{"constant":"***"}'
        END
    END,
    CASE UPPER(COALESCE(ranked.rule_type, N''))
        WHEN N'HMAC' THEN ranked.hmac_key_id
        WHEN N'HMAC_SHA256' THEN ranked.hmac_key_id
        WHEN N'HASH' THEN ranked.hmac_key_id
        WHEN N'TOKENIZE' THEN ranked.hmac_key_id
        ELSE NULL
    END,
    N'VISIBLE',
    N'system:migration-0020',
    SYSUTCDATETIME()
FROM (
    SELECT
        r.table_column_id,
        r.rule_type,
        r.pattern_or_format,
        r.replacement,
        r.hmac_key_id,
        ROW_NUMBER() OVER (PARTITION BY r.table_column_id ORDER BY r.id) AS rn
    FROM dbo.COLUMN_MASKING_RULES r
    INNER JOIN dbo.TABLE_COLUMNS tc ON tc.id = r.table_column_id
) ranked
WHERE ranked.rn = 1
  AND NOT EXISTS (
      SELECT 1
      FROM dbo.COLUMN_MASK_OVERRIDES o
      WHERE o.scope = N'*'
        AND o.table_column_id = ranked.table_column_id
        AND o.code IS NULL
  );
