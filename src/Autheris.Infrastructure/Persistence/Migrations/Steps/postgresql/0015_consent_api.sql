-- phase3: MIG-0014
-- External consent API schema and workflow foundation.

ALTER TABLE CONSENT_REQUESTS ADD COLUMN IF NOT EXISTS version INT NOT NULL DEFAULT 1;
ALTER TABLE CONSENT_REQUESTS ADD COLUMN IF NOT EXISTS workflow_mode VARCHAR(16) NOT NULL DEFAULT 'INTERNAL';
ALTER TABLE CONSENT_REQUESTS ADD COLUMN IF NOT EXISTS external_system_id VARCHAR(36) NULL;
ALTER TABLE CONSENT_REQUESTS ADD COLUMN IF NOT EXISTS external_reference VARCHAR(256) NULL;
ALTER TABLE CONSENT_REQUESTS ADD COLUMN IF NOT EXISTS requested_valid_from TIMESTAMPTZ NULL;
ALTER TABLE CONSENT_REQUESTS ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NULL;
ALTER TABLE CONSENT_REQUESTS ADD COLUMN IF NOT EXISTS request_kind VARCHAR(24) NOT NULL DEFAULT 'CONSENT';
ALTER TABLE CONSENT_REQUESTS ADD COLUMN IF NOT EXISTS profile_id VARCHAR(36) NULL;

UPDATE CONSENT_REQUESTS
SET
    external_reference = COALESCE(external_reference, itsm_ticket_id),
    requested_valid_from = COALESCE(requested_valid_from, NULLIF(BTRIM(requested_at), '')::timestamptz, CURRENT_TIMESTAMP),
    updated_at = COALESCE(updated_at, NULLIF(BTRIM(requested_at), '')::timestamptz, CURRENT_TIMESTAMP)
WHERE requested_valid_from IS NULL
   OR updated_at IS NULL
   OR (external_reference IS NULL AND itsm_ticket_id IS NOT NULL);

ALTER TABLE CONSENT_REQUESTS ALTER COLUMN requested_valid_from SET NOT NULL;
ALTER TABLE CONSENT_REQUESTS ALTER COLUMN updated_at SET NOT NULL;

CREATE INDEX IF NOT EXISTS ix_consent_requests_tenant_updated
    ON CONSENT_REQUESTS (tenant_id, updated_at, id);

ALTER TABLE CONSENTS ADD COLUMN IF NOT EXISTS expiry_notice_sent_at TIMESTAMPTZ NULL;
ALTER TABLE CONSENTS ADD COLUMN IF NOT EXISTS grant_record_seq BIGINT NOT NULL DEFAULT 0;
ALTER TABLE CONSENTS ADD COLUMN IF NOT EXISTS grant_source VARCHAR(24) NOT NULL DEFAULT 'CONSENT_REQUEST';
ALTER TABLE CONSENTS ADD COLUMN IF NOT EXISTS profile_assignment_id VARCHAR(36) NULL;

CREATE INDEX IF NOT EXISTS ix_consents_tenant_grant_source
    ON CONSENTS (tenant_id, grant_source);

CREATE TABLE IF NOT EXISTS PROFILE_ASSIGNMENT_ITEMS (
    tenant_id VARCHAR(128) NOT NULL,
    request_id VARCHAR(36) NOT NULL,
    item_no INT NOT NULL,
    object_ref VARCHAR(512) NOT NULL,
    owner_sid VARCHAR(256) NOT NULL,
    owner_decision VARCHAR(24) NOT NULL,
    decided_by VARCHAR(256) NULL,
    decided_at TIMESTAMPTZ NULL,
    PRIMARY KEY (tenant_id, request_id, item_no, owner_sid)
);

CREATE TABLE IF NOT EXISTS CONSENT_WORKFLOW_ROUTES (
    tenant_id VARCHAR(128) NOT NULL,
    scope_kind VARCHAR(24) NOT NULL,
    scope_ref VARCHAR(256) NOT NULL,
    mode VARCHAR(16) NOT NULL,
    external_system_id VARCHAR(36) NULL,
    max_duration_days INT NOT NULL,
    request_timeout_days INT NOT NULL DEFAULT 30,
    expiry_notice_lead_hours INT NOT NULL DEFAULT 72,
    require_resolvable_approver BOOLEAN NOT NULL DEFAULT TRUE,
    updated_by VARCHAR(256) NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (tenant_id, scope_kind, scope_ref)
);

CREATE TABLE IF NOT EXISTS EXTERNAL_WORKFLOW_SYSTEMS (
    id VARCHAR(36) NOT NULL PRIMARY KEY,
    tenant_id VARCHAR(128) NOT NULL,
    name VARCHAR(256) NOT NULL,
    client_id VARCHAR(256) NOT NULL,
    webhook_url VARCHAR(2048) NOT NULL,
    webhook_secret_protected TEXT NOT NULL,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    created_by VARCHAR(256) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    disabled_at TIMESTAMPTZ NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_external_workflow_systems_tenant_client
    ON EXTERNAL_WORKFLOW_SYSTEMS (tenant_id, client_id);

CREATE TABLE IF NOT EXISTS GRANT_RECORDS (
    tenant_id VARCHAR(128) NOT NULL,
    seq BIGINT NOT NULL,
    id VARCHAR(36) NOT NULL,
    record_type VARCHAR(24) NOT NULL,
    consent_id VARCHAR(36) NULL,
    request_id VARCHAR(36) NULL,
    grant_source VARCHAR(24) NOT NULL,
    profile_assignment_id VARCHAR(36) NULL,
    grantee_type VARCHAR(32) NOT NULL,
    grantee_ref VARCHAR(256) NOT NULL,
    object_id VARCHAR(512) NOT NULL,
    scope_json TEXT NOT NULL,
    valid_from TIMESTAMPTZ NULL,
    valid_until TIMESTAMPTZ NULL,
    requested_by VARCHAR(256) NOT NULL,
    decision_json TEXT NOT NULL,
    decided_at TIMESTAMPTZ NOT NULL,
    recorded_at TIMESTAMPTZ NOT NULL,
    prev_hash CHAR(64) NOT NULL,
    record_hash CHAR(64) NOT NULL,
    signature TEXT NOT NULL,
    key_id VARCHAR(64) NOT NULL,
    PRIMARY KEY (tenant_id, seq)
);

CREATE TABLE IF NOT EXISTS GRANT_RECORD_HEADS (
    tenant_id VARCHAR(128) NOT NULL PRIMARY KEY,
    last_seq BIGINT NOT NULL,
    last_hash CHAR(64) NOT NULL
);

CREATE TABLE IF NOT EXISTS CONSENT_EVENTS (
    tenant_id VARCHAR(128) NOT NULL,
    seq BIGINT NOT NULL,
    id VARCHAR(36) NOT NULL,
    type VARCHAR(64) NOT NULL,
    subject_id VARCHAR(256) NOT NULL,
    payload_json TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (tenant_id, seq)
);

CREATE TABLE IF NOT EXISTS CONSENT_EVENT_DELIVERIES (
    event_id VARCHAR(36) NOT NULL,
    external_system_id VARCHAR(36) NOT NULL,
    attempts INT NOT NULL DEFAULT 0,
    next_attempt_at TIMESTAMPTZ NOT NULL,
    delivered_at TIMESTAMPTZ NULL,
    dead_lettered_at TIMESTAMPTZ NULL,
    lease_owner VARCHAR(128) NULL,
    lease_until TIMESTAMPTZ NULL,
    PRIMARY KEY (event_id, external_system_id)
);

CREATE TABLE IF NOT EXISTS CATALOG_OBJECT_VERSIONS (
    tenant_id VARCHAR(128) NOT NULL,
    object_id VARCHAR(512) NOT NULL,
    content_hash CHAR(64) NOT NULL,
    seq BIGINT NOT NULL,
    removed BOOLEAN NOT NULL DEFAULT FALSE,
    PRIMARY KEY (tenant_id, object_id)
);

CREATE TABLE IF NOT EXISTS CATALOG_CHANGES (
    tenant_id VARCHAR(128) NOT NULL,
    seq BIGINT NOT NULL,
    object_id VARCHAR(512) NOT NULL,
    kind VARCHAR(16) NOT NULL,
    changed_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (tenant_id, seq)
);
