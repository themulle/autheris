-- phase3: MIG-0021

CREATE TABLE IF NOT EXISTS consent_classification_access (
    tenant_id VARCHAR(128) NOT NULL,
    consent_id TEXT NOT NULL REFERENCES consents (id) ON DELETE CASCADE,
    code VARCHAR(64) NOT NULL,
    access_level SMALLINT NOT NULL,
    valid_until TIMESTAMPTZ NULL,
    approved_by_sid VARCHAR(256) NULL,
    approved_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT pk_consent_classification_access PRIMARY KEY (tenant_id, consent_id, code),
    CONSTRAINT ck_consent_classification_access_level CHECK (access_level IN (0, 1, 2))
);

CREATE INDEX IF NOT EXISTS ix_consent_classification_access_consent
    ON consent_classification_access (consent_id);

INSERT INTO consent_classification_access (tenant_id, consent_id, code, access_level, valid_until, approved_by_sid, approved_at)
SELECT
    c.tenant_id,
    c.id,
    'PII',
    CASE
        WHEN EXISTS (
            SELECT 1
            FROM consent_column_rules r
            INNER JOIN column_classifications cc
                ON cc.table_column_id = r.table_column_id
               AND cc.code = 'PII'
               AND (cc.scope = '*' OR cc.scope = c.tenant_id)
            WHERE r.consent_id = c.id
              AND r.access_level = 2
        ) THEN 2
        WHEN c.object_pattern IS NULL
             AND NOT EXISTS (SELECT 1 FROM consent_column_rules r0 WHERE r0.consent_id = c.id) THEN 2
        ELSE 1
    END,
    NULL,
    NULL,
    CURRENT_TIMESTAMP
FROM consents c
ON CONFLICT (tenant_id, consent_id, code) DO NOTHING;
