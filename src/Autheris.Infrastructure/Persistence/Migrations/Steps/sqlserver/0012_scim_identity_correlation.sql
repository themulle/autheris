-- 0012_scim_identity_correlation (SQL Server)
-- Forward-only migration: add hybrid AD / Entra correlation columns to IDENTITY_USERS
-- and IDENTITY_GROUPS, create IDENTITY_GROUP_NESTING and SCIM_REALM_SYNC_STATE tables,
-- and create per-(tenant_id, realm_id) filtered unique correlation indexes.

IF COL_LENGTH('dbo.IDENTITY_USERS', 'object_sid') IS NULL
    ALTER TABLE dbo.IDENTITY_USERS ADD object_sid NVARCHAR(184) NULL;
-- batch
IF COL_LENGTH('dbo.IDENTITY_USERS', 'immutable_id') IS NULL
    ALTER TABLE dbo.IDENTITY_USERS ADD immutable_id NVARCHAR(128) NULL;
-- batch
IF COL_LENGTH('dbo.IDENTITY_USERS', 'upn') IS NULL
    ALTER TABLE dbo.IDENTITY_USERS ADD upn NVARCHAR(256) NULL;
-- batch
IF COL_LENGTH('dbo.IDENTITY_USERS', 'entra_object_id') IS NULL
    ALTER TABLE dbo.IDENTITY_USERS ADD entra_object_id NVARCHAR(64) NULL;
-- batch
IF COL_LENGTH('dbo.IDENTITY_GROUPS', 'object_sid') IS NULL
    ALTER TABLE dbo.IDENTITY_GROUPS ADD object_sid NVARCHAR(184) NULL;
-- batch
IF OBJECT_ID(N'dbo.IDENTITY_GROUP_NESTING', N'U') IS NULL
CREATE TABLE dbo.IDENTITY_GROUP_NESTING (
    tenant_id NVARCHAR(128) NOT NULL DEFAULT 'default',
    realm_id NVARCHAR(128) NOT NULL,
    parent_group_id NVARCHAR(128) NOT NULL,
    child_group_id NVARCHAR(128) NOT NULL,
    PRIMARY KEY (tenant_id, realm_id, parent_group_id, child_group_id)
);
-- batch
IF OBJECT_ID(N'dbo.SCIM_REALM_SYNC_STATE', N'U') IS NULL
CREATE TABLE dbo.SCIM_REALM_SYNC_STATE (
    tenant_id NVARCHAR(128) NOT NULL DEFAULT 'default',
    realm_id NVARCHAR(128) NOT NULL,
    last_contact_at NVARCHAR(64) NULL,
    last_write_at NVARCHAR(64) NULL,
    deprovision_epoch BIGINT NOT NULL DEFAULT 0,
    PRIMARY KEY (tenant_id, realm_id)
);
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_identity_users_tenant_realm_object_sid' AND object_id = OBJECT_ID(N'dbo.IDENTITY_USERS'))
    CREATE UNIQUE INDEX ix_identity_users_tenant_realm_object_sid
        ON dbo.IDENTITY_USERS (tenant_id, realm_id, object_sid)
        WHERE object_sid IS NOT NULL;
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_identity_users_tenant_realm_immutable_id' AND object_id = OBJECT_ID(N'dbo.IDENTITY_USERS'))
    CREATE UNIQUE INDEX ix_identity_users_tenant_realm_immutable_id
        ON dbo.IDENTITY_USERS (tenant_id, realm_id, immutable_id)
        WHERE immutable_id IS NOT NULL;
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_identity_users_tenant_realm_entra_oid' AND object_id = OBJECT_ID(N'dbo.IDENTITY_USERS'))
    CREATE UNIQUE INDEX ix_identity_users_tenant_realm_entra_oid
        ON dbo.IDENTITY_USERS (tenant_id, realm_id, entra_object_id)
        WHERE entra_object_id IS NOT NULL;
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_identity_users_tenant_realm_upn' AND object_id = OBJECT_ID(N'dbo.IDENTITY_USERS'))
    CREATE UNIQUE INDEX ix_identity_users_tenant_realm_upn
        ON dbo.IDENTITY_USERS (tenant_id, realm_id, upn)
        WHERE upn IS NOT NULL;
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_identity_users_tenant_realm_external_id' AND object_id = OBJECT_ID(N'dbo.IDENTITY_USERS'))
    CREATE UNIQUE INDEX ix_identity_users_tenant_realm_external_id
        ON dbo.IDENTITY_USERS (tenant_id, realm_id, external_id)
        WHERE external_id IS NOT NULL;
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_identity_groups_tenant_realm_object_sid' AND object_id = OBJECT_ID(N'dbo.IDENTITY_GROUPS'))
    CREATE UNIQUE INDEX ix_identity_groups_tenant_realm_object_sid
        ON dbo.IDENTITY_GROUPS (tenant_id, realm_id, object_sid)
        WHERE object_sid IS NOT NULL;
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_identity_group_nesting_child' AND object_id = OBJECT_ID(N'dbo.IDENTITY_GROUP_NESTING'))
    CREATE INDEX ix_identity_group_nesting_child
        ON dbo.IDENTITY_GROUP_NESTING (tenant_id, realm_id, child_group_id);
