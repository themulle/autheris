-- Governance schema step 0048 (SQL Server): externally sourced deputy relations (issue #1126). An external system (absence, HR) states
-- "person A deputizes for person B from X to Y"; Autheris reconciles these person-to-person relations per source. Inside the window A is
-- an eligible decider wherever B is a container owner-group member; outside it the row grants nothing. The *_lookup columns hold the
-- normalized, realm-qualified lookup forms of the resolved identities.

IF OBJECT_ID(N'dbo.GOVERNANCE_DEPUTY_RELATIONS', N'U') IS NULL
CREATE TABLE dbo.GOVERNANCE_DEPUTY_RELATIONS (
    id NVARCHAR(36) NOT NULL CONSTRAINT PK_GOVERNANCE_DEPUTY_RELATIONS PRIMARY KEY,
    tenant_id NVARCHAR(128) NOT NULL,
    source NVARCHAR(64) NOT NULL,
    external_id NVARCHAR(256) NOT NULL,
    principal_ref NVARCHAR(256) NOT NULL,
    principal_lookup NVARCHAR(256) NOT NULL,
    principal_alt_lookup NVARCHAR(256) NULL,
    deputy_ref NVARCHAR(256) NOT NULL,
    deputy_lookup NVARCHAR(256) NOT NULL,
    deputy_alt_lookup NVARCHAR(256) NULL,
    valid_from DATETIMEOFFSET NOT NULL,
    valid_to DATETIMEOFFSET NOT NULL,
    created_by NVARCHAR(256) NOT NULL,
    created_at DATETIMEOFFSET NOT NULL,
    updated_by NVARCHAR(256) NULL,
    updated_at DATETIMEOFFSET NULL
);
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_GOVERNANCE_DEPUTY_RELATIONS_EXTERNAL' AND object_id = OBJECT_ID(N'dbo.GOVERNANCE_DEPUTY_RELATIONS'))
    CREATE UNIQUE INDEX UX_GOVERNANCE_DEPUTY_RELATIONS_EXTERNAL ON dbo.GOVERNANCE_DEPUTY_RELATIONS (tenant_id, source, external_id);
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_GOVERNANCE_DEPUTY_RELATIONS_DEPUTY' AND object_id = OBJECT_ID(N'dbo.GOVERNANCE_DEPUTY_RELATIONS'))
    CREATE INDEX IX_GOVERNANCE_DEPUTY_RELATIONS_DEPUTY ON dbo.GOVERNANCE_DEPUTY_RELATIONS (tenant_id, deputy_lookup, valid_to);
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_GOVERNANCE_DEPUTY_RELATIONS_DEPUTY_ALT' AND object_id = OBJECT_ID(N'dbo.GOVERNANCE_DEPUTY_RELATIONS'))
    CREATE INDEX IX_GOVERNANCE_DEPUTY_RELATIONS_DEPUTY_ALT ON dbo.GOVERNANCE_DEPUTY_RELATIONS (tenant_id, deputy_alt_lookup, valid_to);
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_GOVERNANCE_DEPUTY_RELATIONS_PRINCIPAL' AND object_id = OBJECT_ID(N'dbo.GOVERNANCE_DEPUTY_RELATIONS'))
    CREATE INDEX IX_GOVERNANCE_DEPUTY_RELATIONS_PRINCIPAL ON dbo.GOVERNANCE_DEPUTY_RELATIONS (tenant_id, principal_lookup);
