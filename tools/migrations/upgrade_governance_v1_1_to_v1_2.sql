-- =============================================================================
-- Autheris Governance Database Forward Upgrade Script: v1.1.x -> v1.2.0
-- Strictly non-destructive: preserves existing consents, delegations, and audit log hash chains.
-- SEC-SEC210-04, SEC-SEC210-05 (REQ-SEC-210)
-- =============================================================================

-- 1. Create missing tables introduced between v1.1.x and v1.2.0
CREATE TABLE IF NOT EXISTS AUDIT_PURGE_CHECKPOINTS (
    id TEXT PRIMARY KEY,
    first_surviving_seq INTEGER NOT NULL,
    surviving_prev_hash TEXT NOT NULL,
    purged_count INTEGER NOT NULL,
    purged_through_utc TEXT NOT NULL,
    created_at TEXT NOT NULL,
    signature TEXT NOT NULL,
    external_signature TEXT
);

CREATE TABLE IF NOT EXISTS CALLER_ATTRIBUTES (
    id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    name TEXT NOT NULL,
    definition_json TEXT NOT NULL,
    definition_hash TEXT NOT NULL,
    status INTEGER NOT NULL,
    created_by TEXT NOT NULL,
    approved_by TEXT,
    approved_at TEXT,
    draft_definition_json TEXT,
    draft_definition_hash TEXT,
    draft_created_by TEXT,
    pending_deletion INTEGER NOT NULL DEFAULT 0,
    deletion_requested_by TEXT,
    updated_by TEXT,
    updated_at TEXT NOT NULL,
    UNIQUE (tenant_id, name)
);

CREATE TABLE IF NOT EXISTS CALLER_ATTRIBUTE_SERVICE_SETS (
    id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    application_id TEXT NOT NULL,
    attribute_name TEXT NOT NULL,
    values_json TEXT NOT NULL,
    values_hash TEXT NOT NULL,
    status INTEGER NOT NULL,
    created_by TEXT NOT NULL,
    approved_by TEXT,
    approved_at TEXT,
    draft_values_json TEXT,
    draft_created_by TEXT,
    updated_at TEXT NOT NULL,
    UNIQUE (tenant_id, application_id, attribute_name)
);

CREATE TABLE IF NOT EXISTS CALLER_ATTRIBUTE_GENERATION (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    generation BIGINT NOT NULL
);
INSERT INTO CALLER_ATTRIBUTE_GENERATION (id, generation) VALUES (1, 0) ON CONFLICT (id) DO NOTHING;

CREATE TABLE IF NOT EXISTS CLOUDEVENT_SUBSCRIPTIONS (
    id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    target_url TEXT NOT NULL,
    filter_table TEXT NOT NULL,
    filter_operations TEXT NULL,
    hmac_secret_protected TEXT NULL,
    is_enabled INTEGER NOT NULL,
    custom_headers_json TEXT NULL,
    created_by_sid TEXT NULL,
    created_by_groups_json TEXT NULL,
    created_by_roles_json TEXT NULL,
    created_ms INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_cloudevent_subs_tenant ON CLOUDEVENT_SUBSCRIPTIONS (tenant_id, is_enabled);

CREATE TABLE IF NOT EXISTS SEMANTIC_MANIFESTS (
    tenant_id TEXT NOT NULL PRIMARY KEY,
    manifest_version TEXT NOT NULL,
    content_hash TEXT NOT NULL,
    format_version INTEGER NOT NULL,
    models_count INTEGER NOT NULL,
    metrics_count INTEGER NOT NULL,
    manifest_json TEXT NOT NULL,
    imported_by TEXT NOT NULL,
    imported_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS TABLE_CATALOG_SETTINGS (
    table_id TEXT PRIMARY KEY,
    visibility INTEGER NOT NULL,
    updated_by TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS TABLE_PRIMARY_KEY_COLUMNS (
    table_id TEXT NOT NULL,
    key_ordinal INTEGER NOT NULL CHECK (key_ordinal >= 1),
    table_column_id TEXT NOT NULL,
    PRIMARY KEY (table_id, key_ordinal)
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_TABLE_PK_COLUMNS_COLUMN ON TABLE_PRIMARY_KEY_COLUMNS (table_id, table_column_id);

CREATE TABLE IF NOT EXISTS TABLE_INDEXES (
    id TEXT PRIMARY KEY,
    table_id TEXT NOT NULL,
    index_name TEXT NOT NULL,
    is_unique INTEGER NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_TABLE_INDEXES_NAME ON TABLE_INDEXES (table_id, index_name);

CREATE TABLE IF NOT EXISTS TABLE_INDEX_COLUMNS (
    index_id TEXT NOT NULL,
    column_ordinal INTEGER NOT NULL CHECK (column_ordinal >= 1),
    table_column_id TEXT NOT NULL,
    PRIMARY KEY (index_id, column_ordinal)
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_TABLE_INDEX_COLUMNS_COLUMN ON TABLE_INDEX_COLUMNS (index_id, table_column_id);

CREATE TABLE IF NOT EXISTS TABLE_RELATION_KEYS (
    relation_id TEXT NOT NULL,
    key_ordinal INTEGER NOT NULL CHECK (key_ordinal >= 1),
    parent_column TEXT NOT NULL,
    child_column TEXT NOT NULL,
    PRIMARY KEY (relation_id, key_ordinal)
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_TABLE_RELATION_KEYS_PARENT ON TABLE_RELATION_KEYS (relation_id, parent_column);
CREATE UNIQUE INDEX IF NOT EXISTS UX_TABLE_RELATION_KEYS_CHILD ON TABLE_RELATION_KEYS (relation_id, child_column);

CREATE TABLE IF NOT EXISTS TABLE_ROW_FILTER_STRATEGIES (
    table_id TEXT PRIMARY KEY,
    strategy INTEGER NULL,
    duration_seconds INTEGER NULL,
    max_keys INTEGER NULL,
    updated_by TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS TABLE_TAGS (
    table_id TEXT NOT NULL,
    tag TEXT NOT NULL,
    source TEXT NOT NULL,
    created_by TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (table_id, tag)
);

CREATE TABLE IF NOT EXISTS TENANT_REGISTRY (
    normalized_key TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    collided INTEGER NOT NULL DEFAULT 0,
    first_seen_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS WEBHOOK_OUTBOX (
    id TEXT PRIMARY KEY,
    source INTEGER NOT NULL,
    subscription_id TEXT NOT NULL,
    owner_tenant_id TEXT NULL,
    event_tenant_id TEXT NULL,
    event_id TEXT NOT NULL,
    event_type TEXT NOT NULL,
    event_source TEXT NOT NULL,
    event_time_ms INTEGER NOT NULL,
    payload_json TEXT NOT NULL,
    attempt INTEGER NOT NULL,
    max_attempts INTEGER NOT NULL,
    state INTEGER NOT NULL,
    next_attempt_ms INTEGER NOT NULL,
    lease_until_ms INTEGER NULL,
    lease_owner TEXT NULL,
    last_status_code INTEGER NULL,
    last_error_code TEXT NULL,
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_webhook_outbox_dedupe ON WEBHOOK_OUTBOX (source, subscription_id, event_id);
CREATE INDEX IF NOT EXISTS ix_webhook_outbox_due ON WEBHOOK_OUTBOX (state, next_attempt_ms);
CREATE INDEX IF NOT EXISTS ix_webhook_outbox_lease ON WEBHOOK_OUTBOX (state, lease_until_ms);
CREATE INDEX IF NOT EXISTS ix_webhook_outbox_owner ON WEBHOOK_OUTBOX (owner_tenant_id, state);

CREATE TABLE IF NOT EXISTS autheris_cdc_watermarks (
    source TEXT NOT NULL,
    domain_name TEXT NOT NULL,
    schema_name TEXT NOT NULL,
    table_name TEXT NOT NULL,
    watermark INTEGER NOT NULL,
    fencing_token INTEGER NOT NULL,
    updated_at TEXT NOT NULL,
    updated_by TEXT NOT NULL,
    watermark_state TEXT NULL,
    PRIMARY KEY (source, domain_name, schema_name, table_name)
);

CREATE TABLE IF NOT EXISTS autheris_cdc_streams (
    stream_id TEXT NOT NULL,
    source TEXT NOT NULL,
    domain_name TEXT NOT NULL,
    schema_name TEXT NOT NULL,
    table_name TEXT NOT NULL,
    status INTEGER NOT NULL,
    activation_code TEXT NULL,
    head_offset INTEGER NOT NULL,
    tail_offset INTEGER NOT NULL,
    producer_fencing_token INTEGER NOT NULL,
    retention_fencing_token INTEGER NOT NULL,
    last_polled_ms INTEGER NULL,
    created_ms INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL,
    PRIMARY KEY (stream_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_cdc_streams_table ON autheris_cdc_streams (source, domain_name, schema_name, table_name);

CREATE TABLE IF NOT EXISTS autheris_cdc_stream_events (
    stream_id TEXT NOT NULL,
    event_offset INTEGER NOT NULL,
    change_version INTEGER NOT NULL,
    kind INTEGER NOT NULL,
    operation INTEGER NULL,
    tenant_scope INTEGER NOT NULL,
    tenant_id TEXT NULL,
    prior_tenant_id TEXT NULL,
    dek_id TEXT NOT NULL,
    payload BLOB NOT NULL,
    produced_ms INTEGER NOT NULL,
    PRIMARY KEY (stream_id, event_offset)
);
CREATE INDEX IF NOT EXISTS ix_cdc_stream_events_produced ON autheris_cdc_stream_events (stream_id, produced_ms);
CREATE INDEX IF NOT EXISTS ix_cdc_stream_events_dek ON autheris_cdc_stream_events (stream_id, dek_id);

CREATE TABLE IF NOT EXISTS autheris_cdc_row_images (
    stream_id TEXT NOT NULL,
    key_fingerprint BLOB NOT NULL,
    tenant_scope INTEGER NOT NULL,
    tenant_id TEXT NULL,
    dek_id TEXT NOT NULL,
    image BLOB NOT NULL,
    event_offset INTEGER NOT NULL,
    updated_ms INTEGER NOT NULL,
    PRIMARY KEY (stream_id, key_fingerprint)
);

CREATE TABLE IF NOT EXISTS autheris_cdc_stream_keys (
    stream_id TEXT NOT NULL,
    dek_id TEXT NOT NULL,
    purpose INTEGER NOT NULL,
    kek_id TEXT NOT NULL,
    wrapped_key BLOB NOT NULL,
    created_ms INTEGER NOT NULL,
    retired_ms INTEGER NULL,
    PRIMARY KEY (stream_id, dek_id)
);
CREATE INDEX IF NOT EXISTS ix_cdc_stream_keys_kek ON autheris_cdc_stream_keys (kek_id);

-- 2. Add missing columns on existing tables
ALTER TABLE TABLES ADD COLUMN required_filter_columns_json TEXT;
ALTER TABLE CONSENTS ADD COLUMN grantee_sid_norm TEXT;
ALTER TABLE CONSENTS ADD COLUMN renewal_count INTEGER NOT NULL DEFAULT 0;
ALTER TABLE CONSENTS ADD COLUMN description TEXT;
ALTER TABLE CONSENT_REQUESTS ADD COLUMN context_json TEXT;
ALTER TABLE CONSENT_ROW_FILTERS ADD COLUMN key_set_strategy INTEGER NULL;
ALTER TABLE CONSENT_ROW_FILTERS ADD COLUMN key_set_duration_seconds INTEGER NULL;
ALTER TABLE CONSENT_ROW_FILTERS ADD COLUMN key_set_max_keys INTEGER NULL;
ALTER TABLE AUDIT_LOG_ENTRIES ADD COLUMN seq INTEGER;
ALTER TABLE TABLE_RELATIONS ADD COLUMN allow_cross_data_source INTEGER NOT NULL DEFAULT 0;

-- 3. Upgrade ACCESS_PROFILE_ASSIGNMENTS primary key and columns
CREATE TABLE IF NOT EXISTS _upgrade_apa (
    profile_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    subject TEXT NOT NULL,
    subject_type TEXT NOT NULL DEFAULT 'User',
    assigned_at TEXT NOT NULL,
    expires_at TEXT,
    subject_norm TEXT,
    created_by TEXT,
    approved_by TEXT,
    approved_at TEXT,
    justification TEXT,
    kind TEXT NOT NULL DEFAULT 'Standard',
    renewal_count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (tenant_id, profile_id, subject_type, subject)
);

INSERT OR IGNORE INTO _upgrade_apa (
    profile_id, tenant_id, subject, subject_type, assigned_at, expires_at
)
SELECT profile_id, tenant_id, subject, subject_type, assigned_at, expires_at
FROM ACCESS_PROFILE_ASSIGNMENTS;

DROP TABLE ACCESS_PROFILE_ASSIGNMENTS;
ALTER TABLE _upgrade_apa RENAME TO ACCESS_PROFILE_ASSIGNMENTS;

-- 4. Backfill normalized subject and grantee identifiers
UPDATE CONSENTS
SET grantee_sid_norm = UPPER(TRIM(grantee_sid))
WHERE grantee_sid IS NOT NULL;

UPDATE ACCESS_PROFILE_ASSIGNMENTS
SET subject_norm = UPPER(TRIM(subject))
WHERE subject IS NOT NULL;

-- 5. Missing Indexes
CREATE UNIQUE INDEX IF NOT EXISTS idx_audit_seq ON AUDIT_LOG_ENTRIES (seq);
CREATE INDEX IF NOT EXISTS idx_audit_tenant_id ON AUDIT_LOG_ENTRIES (tenant_id, occurred_at);
CREATE INDEX IF NOT EXISTS ix_consents_tenant_table ON CONSENTS (tenant_id, table_id, is_revoked);
CREATE INDEX IF NOT EXISTS ix_consents_tenant_grantee ON CONSENTS (tenant_id, grantee_sid_norm, is_revoked);
CREATE INDEX IF NOT EXISTS ix_consents_tenant_role ON CONSENTS (tenant_id, role_name);
CREATE INDEX IF NOT EXISTS ix_consent_column_rules_consent ON CONSENT_COLUMN_RULES (consent_id);
CREATE INDEX IF NOT EXISTS ix_consent_row_filters_consent ON CONSENT_ROW_FILTERS (consent_id);
CREATE INDEX IF NOT EXISTS ix_column_masking_rules_column ON COLUMN_MASKING_RULES (table_column_id);
CREATE INDEX IF NOT EXISTS ix_role_members_member ON ROLE_MEMBERS (member_sid);
CREATE INDEX IF NOT EXISTS ix_table_owners_owner ON TABLE_OWNERS (data_owner_id);
CREATE INDEX IF NOT EXISTS ix_tables_natural_ci ON TABLES (source_name COLLATE NOCASE, schema_name COLLATE NOCASE, table_name COLLATE NOCASE);
CREATE UNIQUE INDEX IF NOT EXISTS UX_TABLE_RELATIONS_NATURAL ON TABLE_RELATIONS (parent_table_id, child_table_id, relation_name);
CREATE INDEX IF NOT EXISTS IX_ACCESS_PROFILE_ASSIGNMENTS_SUBJECT ON ACCESS_PROFILE_ASSIGNMENTS(tenant_id, subject_type, subject_norm);

-- 6. Migrate pre-existing relation keys into TABLE_RELATION_KEYS if present
INSERT OR IGNORE INTO TABLE_RELATION_KEYS (relation_id, key_ordinal, parent_column, child_column)
SELECT id, 1, join_key_parent, join_key_child
FROM TABLE_RELATIONS
WHERE join_key_parent IS NOT NULL AND join_key_child IS NOT NULL;

-- 7. Reconcile schema migration baseline
CREATE TABLE IF NOT EXISTS autheris_schema_migrations (
    version INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    checksum TEXT NOT NULL,
    applied_at TEXT NOT NULL,
    applied_by TEXT NOT NULL
);

INSERT INTO autheris_schema_migrations (version, name, checksum, applied_at, applied_by)
VALUES (1, 'baseline_governance_schema', '5B2722FAAB8D29BE6B490F9A1B18BA3BEF882260EBF6225706E55349647575AA', datetime('now'), 'upgrade_governance_v1_1_to_v1_2')
ON CONFLICT(version) DO UPDATE SET
    name = excluded.name,
    checksum = excluded.checksum,
    applied_at = excluded.applied_at,
    applied_by = excluded.applied_by;
