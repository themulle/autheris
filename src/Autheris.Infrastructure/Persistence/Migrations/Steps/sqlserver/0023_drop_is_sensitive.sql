-- phase3: MIG-0023
-- Governance schema step 0023 (SQL Server): drop the superseded column sensitivity flag and the legacy masking-rule table.
-- Sensitivity is derived from COLUMN_CLASSIFICATIONS and masks from COLUMN_MASK_OVERRIDES (WP-SIMPL-52 steps 1-2).
-- The statements before the drops repeat the step 0020 backfill idempotently for rows that the pre-classification
-- writers stored after that step, so no sensitivity or mask is lost. A column that carries a global classification
-- (or an admin override) is never touched.

INSERT INTO dbo.COLUMN_CLASSIFICATIONS (scope, table_column_id, code, source, assigned_by, assigned_at)
SELECT N'*', tc.id, N'PII', N'MIGRATION', N'system:migration-0023', SYSUTCDATETIME()
FROM dbo.TABLE_COLUMNS tc
WHERE (tc.is_sensitive = 1 OR EXISTS (SELECT 1 FROM dbo.COLUMN_MASKING_RULES m WHERE m.table_column_id = tc.id))
  AND NOT EXISTS (SELECT 1 FROM dbo.COLUMN_CLASSIFICATIONS cc WHERE cc.scope = N'*' AND cc.table_column_id = tc.id);

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
    N'system:migration-0023',
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

-- batch

IF OBJECT_ID(N'dbo.COLUMN_MASKING_RULES', N'U') IS NOT NULL DROP TABLE dbo.COLUMN_MASKING_RULES;

-- batch

IF COL_LENGTH('dbo.TABLE_COLUMNS', 'is_sensitive') IS NOT NULL
    ALTER TABLE dbo.TABLE_COLUMNS DROP COLUMN is_sensitive;
