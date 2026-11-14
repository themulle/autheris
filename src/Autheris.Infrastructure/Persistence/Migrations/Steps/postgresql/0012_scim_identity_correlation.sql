-- 0012_scim_identity_correlation (PostgreSQL)
-- Forward-only migration: add hybrid AD / Entra correlation columns to IDENTITY_USERS
-- and IDENTITY_GROUPS, create IDENTITY_GROUP_NESTING and SCIM_REALM_SYNC_STATE tables,
-- and create per-(tenant_id, realm_id) filtered unique correlation indexes.

ALTER TABLE IDENTITY_USERS ADD COLUMN IF NOT EXISTS object_sid VARCHAR(184) NULL;
ALTER TABLE IDENTITY_USERS ADD COLUMN IF NOT EXISTS immutable_id VARCHAR(128) NULL;
ALTER TABLE IDENTITY_USERS ADD COLUMN IF NOT EXISTS upn VARCHAR(256) NULL;
ALTER TABLE IDENTITY_USERS ADD COLUMN IF NOT EXISTS entra_object_id VARCHAR(64) NULL;

ALTER TABLE IDENTITY_GROUPS ADD COLUMN IF NOT EXISTS object_sid VARCHAR(184) NULL;

CREATE TABLE IF NOT EXISTS IDENTITY_GROUP_NESTING (
    tenant_id VARCHAR(128) NOT NULL DEFAULT 'default',
    realm_id VARCHAR(128) NOT NULL,
    parent_group_id VARCHAR(128) NOT NULL,
    child_group_id VARCHAR(128) NOT NULL,
    PRIMARY KEY (tenant_id, realm_id, parent_group_id, child_group_id)
);

CREATE TABLE IF NOT EXISTS SCIM_REALM_SYNC_STATE (
    tenant_id VARCHAR(128) NOT NULL DEFAULT 'default',
    realm_id VARCHAR(128) NOT NULL,
    last_contact_at TIMESTAMPTZ NULL,
    last_write_at TIMESTAMPTZ NULL,
    deprovision_epoch BIGINT NOT NULL DEFAULT 0,
    PRIMARY KEY (tenant_id, realm_id)
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_identity_users_tenant_realm_object_sid
    ON IDENTITY_USERS (tenant_id, realm_id, object_sid)
    WHERE object_sid IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ix_identity_users_tenant_realm_immutable_id
    ON IDENTITY_USERS (tenant_id, realm_id, immutable_id)
    WHERE immutable_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ix_identity_users_tenant_realm_entra_oid
    ON IDENTITY_USERS (tenant_id, realm_id, entra_object_id)
    WHERE entra_object_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ix_identity_users_tenant_realm_upn
    ON IDENTITY_USERS (tenant_id, realm_id, upn)
    WHERE upn IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ix_identity_users_tenant_realm_external_id
    ON IDENTITY_USERS (tenant_id, realm_id, external_id)
    WHERE external_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ix_identity_groups_tenant_realm_object_sid
    ON IDENTITY_GROUPS (tenant_id, realm_id, object_sid)
    WHERE object_sid IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_identity_group_nesting_child
    ON IDENTITY_GROUP_NESTING (tenant_id, realm_id, child_group_id);
