-- Grant row-scope entries, virtual filter metadata columns, and replacement of legacy AccessProfile free-SQL predicate.

CREATE TABLE IF NOT EXISTS CONSENT_ROW_SCOPES (
    tenant_id VARCHAR(128) NOT NULL,
    consent_id TEXT NOT NULL REFERENCES CONSENTS (id) ON DELETE CASCADE,
    entry_no INTEGER NOT NULL,
    policy_name VARCHAR(256) NOT NULL,
    parameters_json TEXT NOT NULL,
    column_map_json TEXT NULL,
    uncovered VARCHAR(16) NOT NULL DEFAULT 'DENY',
    PRIMARY KEY (tenant_id, consent_id, entry_no),
    CONSTRAINT ck_consent_row_scopes_uncovered CHECK (uncovered IN ('DENY', 'SKIP'))
);

CREATE INDEX IF NOT EXISTS ix_consent_row_scopes_tenant_policy
    ON CONSENT_ROW_SCOPES (tenant_id, policy_name);

ALTER TABLE VIRTUAL_FILTERS ADD COLUMN IF NOT EXISTS parameters_json TEXT NULL;
ALTER TABLE VIRTUAL_FILTERS ADD COLUMN IF NOT EXISTS evaluation_mode VARCHAR(32) NOT NULL DEFAULT 'Live';
ALTER TABLE VIRTUAL_FILTERS ADD COLUMN IF NOT EXISTS key_set_max_age_seconds INTEGER NULL;
ALTER TABLE VIRTUAL_FILTERS ADD COLUMN IF NOT EXISTS status VARCHAR(32) NOT NULL DEFAULT 'Active';
ALTER TABLE VIRTUAL_FILTERS ADD COLUMN IF NOT EXISTS approved_by_sid VARCHAR(256) NULL;
ALTER TABLE VIRTUAL_FILTERS ADD COLUMN IF NOT EXISTS approved_at TIMESTAMPTZ NULL;

ALTER TABLE ACCESS_PROFILES ADD COLUMN IF NOT EXISTS row_scope_json TEXT NULL;
UPDATE ACCESS_PROFILES SET row_filter_predicate = NULL WHERE row_filter_predicate IS NOT NULL;
ALTER TABLE ACCESS_PROFILES DROP COLUMN IF EXISTS row_filter_predicate;
