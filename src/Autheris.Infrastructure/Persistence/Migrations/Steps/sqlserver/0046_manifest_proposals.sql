-- Governance schema step 0046 (SQL Server): declarative manifest proposals (Amendment 6). IT administration submits a consent manifest
-- as a pending proposal; it has no effect until one Data Governance member confirms it. The row keeps the manifest, the computed diff and
-- the hash of the grant state the diff was computed against.

IF OBJECT_ID(N'dbo.GOVERNANCE_MANIFEST_PROPOSALS', N'U') IS NULL
CREATE TABLE dbo.GOVERNANCE_MANIFEST_PROPOSALS (
    id NVARCHAR(36) NOT NULL CONSTRAINT PK_GOVERNANCE_MANIFEST_PROPOSALS PRIMARY KEY,
    tenant_id NVARCHAR(128) NOT NULL,
    scope_name NVARCHAR(128) NOT NULL,
    submitted_by NVARCHAR(256) NOT NULL,
    submitted_at DATETIMEOFFSET NOT NULL,
    manifest_json NVARCHAR(MAX) NOT NULL,
    diff_json NVARCHAR(MAX) NOT NULL,
    state_hash NVARCHAR(64) NOT NULL,
    status NVARCHAR(16) NOT NULL,
    decided_by NVARCHAR(256) NULL,
    decided_at DATETIMEOFFSET NULL,
    reject_reason NVARCHAR(512) NULL,
    expires_at DATETIMEOFFSET NOT NULL
);
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_GOVERNANCE_MANIFEST_PROPOSALS_SCOPE' AND object_id = OBJECT_ID(N'dbo.GOVERNANCE_MANIFEST_PROPOSALS'))
    CREATE INDEX IX_GOVERNANCE_MANIFEST_PROPOSALS_SCOPE ON dbo.GOVERNANCE_MANIFEST_PROPOSALS (tenant_id, scope_name, status);
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_GOVERNANCE_MANIFEST_PROPOSALS_EXPIRY' AND object_id = OBJECT_ID(N'dbo.GOVERNANCE_MANIFEST_PROPOSALS'))
    CREATE INDEX IX_GOVERNANCE_MANIFEST_PROPOSALS_EXPIRY ON dbo.GOVERNANCE_MANIFEST_PROPOSALS (status, expires_at);
