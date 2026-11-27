-- Governance schema step 0052 (SQL Server): provisioning of approved grants as Unity Catalog privileges (issue #1122).
-- UC_PROVISIONED_PRIVILEGES is the ledger of the privileges Autheris applied, or is about to apply, per Databricks data source. A row
-- is written before the privilege is added and deleted only after a verified removal, so no applied privilege is ever untracked.
-- UC_GRANT_PROVISIONING holds the provisioning feedback per consent, tenant scoped like the consent. UC_PROVISIONING_FENCES holds the
-- highest cluster fencing token per data source; every ledger or feedback write checks it first, so a reconciler that lost its lease
-- can no longer write.

IF OBJECT_ID(N'dbo.UC_PROVISIONED_PRIVILEGES', N'U') IS NULL
CREATE TABLE dbo.UC_PROVISIONED_PRIVILEGES (
    id NVARCHAR(36) NOT NULL CONSTRAINT PK_UC_PROVISIONED_PRIVILEGES PRIMARY KEY,
    data_source NVARCHAR(128) NOT NULL,
    securable_type NVARCHAR(16) NOT NULL,
    full_name NVARCHAR(400) NOT NULL,
    principal NVARCHAR(256) NOT NULL,
    principal_external_id NVARCHAR(64) NOT NULL,
    privilege NVARCHAR(32) NOT NULL,
    state NVARCHAR(16) NOT NULL,
    attempts INT NOT NULL,
    next_attempt_at DATETIMEOFFSET NULL,
    last_error_code NVARCHAR(64) NULL,
    applied_at DATETIMEOFFSET NULL,
    verified_at DATETIMEOFFSET NULL,
    created_at DATETIMEOFFSET NOT NULL,
    updated_at DATETIMEOFFSET NOT NULL
);
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_UC_PROVISIONED_PRIVILEGES_KEY' AND object_id = OBJECT_ID(N'dbo.UC_PROVISIONED_PRIVILEGES'))
    CREATE UNIQUE INDEX UX_UC_PROVISIONED_PRIVILEGES_KEY ON dbo.UC_PROVISIONED_PRIVILEGES (data_source, securable_type, full_name, principal_external_id, privilege);
-- batch
IF OBJECT_ID(N'dbo.UC_GRANT_PROVISIONING', N'U') IS NULL
CREATE TABLE dbo.UC_GRANT_PROVISIONING (
    tenant_id NVARCHAR(128) NOT NULL,
    consent_id NVARCHAR(36) NOT NULL,
    data_source NVARCHAR(128) NOT NULL,
    state NVARCHAR(16) NOT NULL,
    reason_code NVARCHAR(64) NULL,
    updated_at DATETIMEOFFSET NOT NULL,
    verified_at DATETIMEOFFSET NULL,
    CONSTRAINT PK_UC_GRANT_PROVISIONING PRIMARY KEY (tenant_id, consent_id)
);
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_UC_GRANT_PROVISIONING_SOURCE' AND object_id = OBJECT_ID(N'dbo.UC_GRANT_PROVISIONING'))
    CREATE INDEX IX_UC_GRANT_PROVISIONING_SOURCE ON dbo.UC_GRANT_PROVISIONING (tenant_id, data_source, state);
-- batch
IF OBJECT_ID(N'dbo.UC_PROVISIONING_FENCES', N'U') IS NULL
CREATE TABLE dbo.UC_PROVISIONING_FENCES (
    data_source NVARCHAR(128) NOT NULL CONSTRAINT PK_UC_PROVISIONING_FENCES PRIMARY KEY,
    fencing_token BIGINT NOT NULL,
    updated_at DATETIMEOFFSET NOT NULL
);
