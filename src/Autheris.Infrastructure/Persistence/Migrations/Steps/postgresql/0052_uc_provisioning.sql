-- Governance schema step 0052 (PostgreSQL): provisioning of approved grants as Unity Catalog privileges (issue #1122).
-- uc_provisioned_privileges is the ledger of the privileges Autheris applied, or is about to apply, per Databricks data source. A row
-- is written before the privilege is added and deleted only after a verified removal, so no applied privilege is ever untracked.
-- uc_grant_provisioning holds the provisioning feedback per consent, tenant scoped like the consent.

CREATE TABLE IF NOT EXISTS uc_provisioned_privileges (
    id VARCHAR(36) NOT NULL PRIMARY KEY,
    data_source VARCHAR(128) NOT NULL,
    securable_type VARCHAR(16) NOT NULL,
    full_name VARCHAR(400) NOT NULL,
    principal VARCHAR(256) NOT NULL,
    principal_external_id VARCHAR(64) NOT NULL,
    privilege VARCHAR(32) NOT NULL,
    state VARCHAR(16) NOT NULL,
    attempts INTEGER NOT NULL,
    next_attempt_at TIMESTAMPTZ NULL,
    last_error_code VARCHAR(64) NULL,
    applied_at TIMESTAMPTZ NULL,
    verified_at TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_uc_provisioned_privileges_key
    ON uc_provisioned_privileges (data_source, securable_type, full_name, principal_external_id, privilege);

CREATE TABLE IF NOT EXISTS uc_grant_provisioning (
    tenant_id VARCHAR(128) NOT NULL,
    consent_id VARCHAR(36) NOT NULL,
    data_source VARCHAR(128) NOT NULL,
    state VARCHAR(16) NOT NULL,
    reason_code VARCHAR(64) NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    verified_at TIMESTAMPTZ NULL,
    PRIMARY KEY (tenant_id, consent_id)
);

CREATE INDEX IF NOT EXISTS ix_uc_grant_provisioning_source
    ON uc_grant_provisioning (tenant_id, data_source, state);
