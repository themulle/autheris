-- phase3: MIG-0041
-- Governance schema step 0041 (PostgreSQL): unified permission requests, grant kinds, catalog visibility grants,
-- D13 start-fresh purge with archive and purge report, removed-scope workflow route cleanup, and epoch bump.

ALTER TABLE CONSENT_REQUESTS ADD COLUMN IF NOT EXISTS grant_kind VARCHAR(24) NOT NULL DEFAULT 'DATA';
ALTER TABLE CONSENT_REQUESTS ADD COLUMN IF NOT EXISTS target_columns TEXT NULL;
ALTER TABLE CONSENT_REQUESTS ADD COLUMN IF NOT EXISTS container_kind VARCHAR(24) NULL;
ALTER TABLE CONSENT_REQUESTS ADD COLUMN IF NOT EXISTS container_id VARCHAR(36) NULL;
ALTER TABLE CONSENT_REQUESTS ADD COLUMN IF NOT EXISTS approver_capacity VARCHAR(32) NULL;
ALTER TABLE CONSENT_REQUESTS ADD COLUMN IF NOT EXISTS self_grant BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE CONSENT_REQUESTS ADD COLUMN IF NOT EXISTS bundle_id VARCHAR(36) NULL;
ALTER TABLE CONSENT_REQUESTS ADD COLUMN IF NOT EXISTS decided_by VARCHAR(256) NULL;
ALTER TABLE CONSENT_REQUESTS ADD COLUMN IF NOT EXISTS decided_at TIMESTAMPTZ NULL;

UPDATE CONSENT_REQUESTS SET request_kind = 'DATA' WHERE UPPER(request_kind) = 'CONSENT';

CREATE INDEX IF NOT EXISTS ix_consent_requests_requester_status
    ON CONSENT_REQUESTS (tenant_id, requester_sid, status);

CREATE INDEX IF NOT EXISTS ix_consent_requests_bundle
    ON CONSENT_REQUESTS (tenant_id, bundle_id);

ALTER TABLE CONSENTS ADD COLUMN IF NOT EXISTS grant_kind VARCHAR(24) NOT NULL DEFAULT 'DATA';
ALTER TABLE CONSENTS ADD COLUMN IF NOT EXISTS approver_capacity VARCHAR(32) NULL;
ALTER TABLE CONSENTS ADD COLUMN IF NOT EXISTS self_grant BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE CONSENTS ADD COLUMN IF NOT EXISTS target_columns TEXT NULL;

CREATE INDEX IF NOT EXISTS ix_consents_grant_kind
    ON CONSENTS (tenant_id, grant_kind, is_revoked);

CREATE TABLE IF NOT EXISTS catalog_visibility_grants (
    id VARCHAR(36) NOT NULL PRIMARY KEY,
    tenant_id VARCHAR(128) NOT NULL,
    container_kind VARCHAR(24) NOT NULL,
    container_id VARCHAR(36) NOT NULL,
    grantee_type VARCHAR(32) NOT NULL,
    grantee_sid VARCHAR(256) NOT NULL,
    grantee_sid_norm VARCHAR(256) NOT NULL,
    valid_from TIMESTAMPTZ NOT NULL,
    valid_to TIMESTAMPTZ NULL,
    granted_by_sid VARCHAR(256) NOT NULL,
    approver_capacity VARCHAR(32) NOT NULL,
    request_id VARCHAR(36) NULL,
    is_revoked BOOLEAN NOT NULL DEFAULT FALSE,
    revoked_by_sid VARCHAR(256) NULL,
    revoked_at TIMESTAMPTZ NULL,
    revoke_reason VARCHAR(512) NULL,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_catalog_visibility_grants_grantee
    ON catalog_visibility_grants (tenant_id, grantee_sid_norm, is_revoked);

CREATE INDEX IF NOT EXISTS ix_catalog_visibility_grants_container
    ON catalog_visibility_grants (tenant_id, container_kind, container_id, is_revoked);

CREATE TABLE IF NOT EXISTS migration_purge_report (
    id VARCHAR(36) NOT NULL PRIMARY KEY,
    step VARCHAR(64) NOT NULL,
    table_name VARCHAR(128) NOT NULL,
    row_count BIGINT NOT NULL,
    ledger_head_hash VARCHAR(128) NULL,
    purged_at TIMESTAMPTZ NOT NULL,
    audited_at TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS ix_migration_purge_report_step
    ON migration_purge_report (step, audited_at);

CREATE TABLE IF NOT EXISTS grant_record_heads_archive (
    tenant_id VARCHAR(128) NOT NULL,
    last_seq BIGINT NOT NULL,
    last_hash CHAR(64) NOT NULL,
    archived_at TIMESTAMPTZ NOT NULL,
    step VARCHAR(64) NOT NULL,
    PRIMARY KEY (step, tenant_id)
);

INSERT INTO grant_record_heads_archive (tenant_id, last_seq, last_hash, archived_at, step)
SELECT tenant_id, last_seq, last_hash, CURRENT_TIMESTAMP, '0041_permission_requests_and_grant_kinds'
FROM GRANT_RECORD_HEADS
ON CONFLICT (step, tenant_id) DO NOTHING;

INSERT INTO migration_purge_report (id, step, table_name, row_count, ledger_head_hash, purged_at)
SELECT '0041:CONSENT_COLUMN_RULES', '0041_permission_requests_and_grant_kinds', 'CONSENT_COLUMN_RULES', COUNT(*), NULL, CURRENT_TIMESTAMP FROM CONSENT_COLUMN_RULES
ON CONFLICT (id) DO NOTHING;
INSERT INTO migration_purge_report (id, step, table_name, row_count, ledger_head_hash, purged_at)
SELECT '0041:CONSENT_ROW_FILTERS', '0041_permission_requests_and_grant_kinds', 'CONSENT_ROW_FILTERS', COUNT(*), NULL, CURRENT_TIMESTAMP FROM CONSENT_ROW_FILTERS
ON CONFLICT (id) DO NOTHING;
INSERT INTO migration_purge_report (id, step, table_name, row_count, ledger_head_hash, purged_at)
SELECT '0041:CONSENT_ROW_SCOPES', '0041_permission_requests_and_grant_kinds', 'CONSENT_ROW_SCOPES', COUNT(*), NULL, CURRENT_TIMESTAMP FROM CONSENT_ROW_SCOPES
ON CONFLICT (id) DO NOTHING;
INSERT INTO migration_purge_report (id, step, table_name, row_count, ledger_head_hash, purged_at)
SELECT '0041:CONSENT_WRITE_RULES', '0041_permission_requests_and_grant_kinds', 'CONSENT_WRITE_RULES', COUNT(*), NULL, CURRENT_TIMESTAMP FROM CONSENT_WRITE_RULES
ON CONFLICT (id) DO NOTHING;
INSERT INTO migration_purge_report (id, step, table_name, row_count, ledger_head_hash, purged_at)
SELECT '0041:consent_classification_access', '0041_permission_requests_and_grant_kinds', 'consent_classification_access', COUNT(*), NULL, CURRENT_TIMESTAMP FROM consent_classification_access
ON CONFLICT (id) DO NOTHING;
INSERT INTO migration_purge_report (id, step, table_name, row_count, ledger_head_hash, purged_at)
SELECT '0041:consent_approving_owners', '0041_permission_requests_and_grant_kinds', 'consent_approving_owners', COUNT(*), NULL, CURRENT_TIMESTAMP FROM consent_approving_owners
ON CONFLICT (id) DO NOTHING;
INSERT INTO migration_purge_report (id, step, table_name, row_count, ledger_head_hash, purged_at)
SELECT '0041:CONSENTS', '0041_permission_requests_and_grant_kinds', 'CONSENTS', COUNT(*), NULL, CURRENT_TIMESTAMP FROM CONSENTS
ON CONFLICT (id) DO NOTHING;
INSERT INTO migration_purge_report (id, step, table_name, row_count, ledger_head_hash, purged_at)
SELECT '0041:GRANT_RECORDS', '0041_permission_requests_and_grant_kinds', 'GRANT_RECORDS', COUNT(*), NULL, CURRENT_TIMESTAMP FROM GRANT_RECORDS
ON CONFLICT (id) DO NOTHING;
INSERT INTO migration_purge_report (id, step, table_name, row_count, ledger_head_hash, purged_at)
SELECT '0041:GRANT_RECORD_HEADS', '0041_permission_requests_and_grant_kinds', 'GRANT_RECORD_HEADS', COUNT(*), MAX(last_hash), CURRENT_TIMESTAMP FROM GRANT_RECORD_HEADS
ON CONFLICT (id) DO NOTHING;
INSERT INTO migration_purge_report (id, step, table_name, row_count, ledger_head_hash, purged_at)
SELECT '0041:APPROVAL_STEPS', '0041_permission_requests_and_grant_kinds', 'APPROVAL_STEPS', COUNT(*), NULL, CURRENT_TIMESTAMP FROM APPROVAL_STEPS
ON CONFLICT (id) DO NOTHING;
INSERT INTO migration_purge_report (id, step, table_name, row_count, ledger_head_hash, purged_at)
SELECT '0041:PROFILE_ASSIGNMENT_ITEMS', '0041_permission_requests_and_grant_kinds', 'PROFILE_ASSIGNMENT_ITEMS', COUNT(*), NULL, CURRENT_TIMESTAMP FROM PROFILE_ASSIGNMENT_ITEMS
ON CONFLICT (id) DO NOTHING;
INSERT INTO migration_purge_report (id, step, table_name, row_count, ledger_head_hash, purged_at)
SELECT '0041:access_profile_assignments', '0041_permission_requests_and_grant_kinds', 'access_profile_assignments', COUNT(*), NULL, CURRENT_TIMESTAMP FROM access_profile_assignments
ON CONFLICT (id) DO NOTHING;
INSERT INTO migration_purge_report (id, step, table_name, row_count, ledger_head_hash, purged_at)
SELECT '0041:CONSENT_EVENT_DELIVERIES', '0041_permission_requests_and_grant_kinds', 'CONSENT_EVENT_DELIVERIES', COUNT(*), NULL, CURRENT_TIMESTAMP FROM CONSENT_EVENT_DELIVERIES
ON CONFLICT (id) DO NOTHING;
INSERT INTO migration_purge_report (id, step, table_name, row_count, ledger_head_hash, purged_at)
SELECT '0041:CONSENT_EVENTS', '0041_permission_requests_and_grant_kinds', 'CONSENT_EVENTS', COUNT(*), NULL, CURRENT_TIMESTAMP FROM CONSENT_EVENTS
ON CONFLICT (id) DO NOTHING;
INSERT INTO migration_purge_report (id, step, table_name, row_count, ledger_head_hash, purged_at)
SELECT '0041:CONSENT_REQUESTS', '0041_permission_requests_and_grant_kinds', 'CONSENT_REQUESTS', COUNT(*), NULL, CURRENT_TIMESTAMP FROM CONSENT_REQUESTS
ON CONFLICT (id) DO NOTHING;
INSERT INTO migration_purge_report (id, step, table_name, row_count, ledger_head_hash, purged_at)
SELECT '0041:DATA_OWNER_DELEGATIONS', '0041_permission_requests_and_grant_kinds', 'DATA_OWNER_DELEGATIONS', COUNT(*), NULL, CURRENT_TIMESTAMP FROM DATA_OWNER_DELEGATIONS
ON CONFLICT (id) DO NOTHING;
INSERT INTO migration_purge_report (id, step, table_name, row_count, ledger_head_hash, purged_at)
SELECT '0041:TABLE_OWNERS', '0041_permission_requests_and_grant_kinds', 'TABLE_OWNERS', COUNT(*), NULL, CURRENT_TIMESTAMP FROM TABLE_OWNERS
ON CONFLICT (id) DO NOTHING;
INSERT INTO migration_purge_report (id, step, table_name, row_count, ledger_head_hash, purged_at)
SELECT '0041:DATA_OWNERS', '0041_permission_requests_and_grant_kinds', 'DATA_OWNERS', COUNT(*), NULL, CURRENT_TIMESTAMP FROM DATA_OWNERS
ON CONFLICT (id) DO NOTHING;

DELETE FROM CONSENT_COLUMN_RULES;
DELETE FROM CONSENT_ROW_FILTERS;
DELETE FROM CONSENT_ROW_SCOPES;
DELETE FROM CONSENT_WRITE_RULES;
DELETE FROM consent_classification_access;
DELETE FROM consent_approving_owners;
DELETE FROM CONSENTS;
DELETE FROM GRANT_RECORDS;
DELETE FROM GRANT_RECORD_HEADS;
DELETE FROM APPROVAL_STEPS;
DELETE FROM PROFILE_ASSIGNMENT_ITEMS;
DELETE FROM access_profile_assignments;
DELETE FROM CONSENT_EVENT_DELIVERIES;
DELETE FROM CONSENT_EVENTS;
DELETE FROM CONSENT_REQUESTS;
DELETE FROM DATA_OWNER_DELEGATIONS;
DELETE FROM TABLE_OWNERS;
DELETE FROM DATA_OWNERS;

DELETE FROM CONSENT_WORKFLOW_ROUTES WHERE UPPER(scope_kind) IN ('DATA_OWNER', 'DATA_SOURCE');

UPDATE governance_epochs SET epoch = epoch + 1, updated_at = CURRENT_TIMESTAMP;
UPDATE POLICY_EPOCHS SET epoch = epoch + 1;
