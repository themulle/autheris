-- WP-SIMPL-45: Governed write access (consent and access-profile write rules, requested write rules, write-protected columns). Additive.
CREATE TABLE IF NOT EXISTS CONSENT_WRITE_RULES (
    id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    consent_id TEXT NOT NULL REFERENCES CONSENTS (id) ON DELETE CASCADE,
    operation INTEGER NOT NULL,
    row_scope INTEGER NOT NULL,
    row_filter_json TEXT NULL,
    max_rows_per_statement BIGINT NULL,
    columns_json TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS ACCESS_PROFILE_WRITE_RULES (
    id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    profile_id TEXT NOT NULL,
    table_identifier TEXT NOT NULL,
    operation INTEGER NOT NULL,
    row_scope INTEGER NOT NULL,
    row_filter_json TEXT NULL,
    max_rows_per_statement BIGINT NULL,
    columns_json TEXT NOT NULL
);

ALTER TABLE CONSENT_REQUESTS ADD COLUMN IF NOT EXISTS requested_write_rules_json TEXT NULL;
ALTER TABLE TABLE_COLUMNS ADD COLUMN IF NOT EXISTS write_protected BOOLEAN NOT NULL DEFAULT FALSE;
