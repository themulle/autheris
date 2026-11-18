-- phase3: MIG-0023
-- Governance schema step 0023 (PostgreSQL): drop the superseded column sensitivity flag and the legacy masking-rule table.
-- Sensitivity is derived from column_classifications and masks from column_mask_overrides (WP-SIMPL-52 steps 1-2).
-- The statements before the drops repeat the step 0020 backfill idempotently for rows that the pre-classification
-- writers stored after that step, so no sensitivity or mask is lost. A column that carries a global classification
-- (or an admin override) is never touched. A PUBLIC classification does not count as protection.
-- All nodes must run this build before the step applies: older builds still write the dropped objects.

-- Decision parity: an active consent with a Clear column rule on a column that receives PII below keeps that clearance.
-- Rows the 0021 backfill created as MASK (no approver, no expiry) are raised to CLEAR; missing rows are added as CLEAR.
INSERT INTO consent_classification_access (tenant_id, consent_id, code, access_level, valid_until, approved_by_sid, approved_at)
SELECT c.tenant_id, c.id, 'PII', 2, NULL, NULL, CURRENT_TIMESTAMP
FROM consents c
WHERE c.is_revoked = 0
  AND EXISTS (
      SELECT 1 FROM consent_column_rules r INNER JOIN table_columns tc ON tc.id = r.table_column_id
      WHERE r.consent_id = c.id AND r.access_level = 2
      AND (tc.is_sensitive = 1 OR EXISTS (SELECT 1 FROM column_masking_rules m WHERE m.table_column_id = tc.id))
      AND NOT EXISTS (SELECT 1 FROM column_classifications cc WHERE cc.scope = '*' AND cc.table_column_id = tc.id AND cc.code <> 'PUBLIC'))
ON CONFLICT (tenant_id, consent_id, code) DO NOTHING;

UPDATE consent_classification_access
SET access_level = 2
WHERE code = 'PII' AND access_level = 1 AND approved_by_sid IS NULL AND valid_until IS NULL
  AND NOT EXISTS (
      SELECT 1 FROM consent_column_rules r2 INNER JOIN column_classifications cc2 ON cc2.table_column_id = r2.table_column_id
      WHERE r2.consent_id = consent_classification_access.consent_id AND r2.access_level = 2 AND cc2.scope = '*' AND cc2.code <> 'PUBLIC')
  AND EXISTS (
      SELECT 1 FROM consents c
      INNER JOIN consent_column_rules r ON r.consent_id = c.id AND r.access_level = 2
      INNER JOIN table_columns tc ON tc.id = r.table_column_id
      WHERE c.id = consent_classification_access.consent_id AND c.tenant_id = consent_classification_access.tenant_id AND c.is_revoked = 0
      AND (tc.is_sensitive = 1 OR EXISTS (SELECT 1 FROM column_masking_rules m WHERE m.table_column_id = tc.id))
      AND NOT EXISTS (SELECT 1 FROM column_classifications cc WHERE cc.scope = '*' AND cc.table_column_id = tc.id AND cc.code <> 'PUBLIC'));

INSERT INTO column_classifications (scope, table_column_id, code, source, assigned_by, assigned_at)
SELECT '*', tc.id, 'PII', 'MIGRATION', 'system:migration-0023', CURRENT_TIMESTAMP
FROM table_columns tc
WHERE (tc.is_sensitive = 1 OR EXISTS (SELECT 1 FROM column_masking_rules m WHERE m.table_column_id = tc.id))
  AND NOT EXISTS (SELECT 1 FROM column_classifications cc WHERE cc.scope = '*' AND cc.table_column_id = tc.id AND cc.code <> 'PUBLIC')
ON CONFLICT (scope, table_column_id, code) DO NOTHING;

INSERT INTO column_mask_overrides (id, scope, table_column_id, code, mask_function, params_json, key_ref, catalog_text, updated_by, updated_at)
SELECT
    gen_random_uuid(),
    '*',
    ranked.table_column_id,
    NULL,
    CASE UPPER(COALESCE(ranked.rule_type, ''))
        WHEN 'NULLIFY' THEN 'NULLIFY'
        WHEN 'DENY' THEN 'NULLIFY'
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
        WHEN 'DENY' THEN '{"deny":true}'
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
    'system:migration-0023',
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

-- Repair: a DENY rule that the step 0020 backfill turned into a visible redaction stays denied.
UPDATE column_mask_overrides
SET mask_function = 'NULLIFY', params_json = '{"deny":true}', key_ref = NULL
WHERE scope = '*' AND code IS NULL AND updated_by = 'system:migration-0020'
  AND EXISTS (SELECT 1 FROM column_masking_rules m WHERE m.table_column_id = column_mask_overrides.table_column_id AND UPPER(m.rule_type) = 'DENY');

DROP TABLE IF EXISTS column_masking_rules;

ALTER TABLE table_columns DROP COLUMN IF EXISTS is_sensitive;
