-- Grant row-scope entries, virtual filter metadata columns, and replacement of legacy AccessProfile free-SQL predicate.

IF OBJECT_ID(N'dbo.CONSENT_ROW_SCOPES', N'U') IS NULL
CREATE TABLE dbo.CONSENT_ROW_SCOPES (
    tenant_id NVARCHAR(128) NOT NULL,
    consent_id NVARCHAR(128) NOT NULL,
    entry_no INT NOT NULL,
    policy_name NVARCHAR(256) NOT NULL,
    parameters_json NVARCHAR(MAX) NOT NULL,
    column_map_json NVARCHAR(MAX) NULL,
    uncovered NVARCHAR(16) NOT NULL CONSTRAINT DF_CONSENT_ROW_SCOPES_uncovered DEFAULT N'DENY',
    CONSTRAINT PK_CONSENT_ROW_SCOPES PRIMARY KEY (tenant_id, consent_id, entry_no),
    CONSTRAINT FK_CONSENT_ROW_SCOPES_CONSENT FOREIGN KEY (consent_id) REFERENCES dbo.CONSENTS (id) ON DELETE CASCADE,
    CONSTRAINT CK_CONSENT_ROW_SCOPES_uncovered CHECK (uncovered IN (N'DENY', N'SKIP'))
);
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CONSENT_ROW_SCOPES_TENANT_POLICY' AND object_id = OBJECT_ID(N'dbo.CONSENT_ROW_SCOPES'))
    CREATE INDEX IX_CONSENT_ROW_SCOPES_TENANT_POLICY ON dbo.CONSENT_ROW_SCOPES (tenant_id, policy_name);
-- batch
IF COL_LENGTH('dbo.VIRTUAL_FILTERS', 'parameters_json') IS NULL
    ALTER TABLE dbo.VIRTUAL_FILTERS ADD parameters_json NVARCHAR(MAX) NULL;
-- batch
IF COL_LENGTH('dbo.VIRTUAL_FILTERS', 'evaluation_mode') IS NULL
    ALTER TABLE dbo.VIRTUAL_FILTERS ADD evaluation_mode NVARCHAR(32) NOT NULL CONSTRAINT DF_VIRTUAL_FILTERS_evaluation_mode DEFAULT N'Live';
-- batch
IF COL_LENGTH('dbo.VIRTUAL_FILTERS', 'key_set_max_age_seconds') IS NULL
    ALTER TABLE dbo.VIRTUAL_FILTERS ADD key_set_max_age_seconds INT NULL;
-- batch
IF COL_LENGTH('dbo.VIRTUAL_FILTERS', 'status') IS NULL
    ALTER TABLE dbo.VIRTUAL_FILTERS ADD status NVARCHAR(32) NOT NULL CONSTRAINT DF_VIRTUAL_FILTERS_status DEFAULT N'Active';
-- batch
IF COL_LENGTH('dbo.VIRTUAL_FILTERS', 'approved_by_sid') IS NULL
    ALTER TABLE dbo.VIRTUAL_FILTERS ADD approved_by_sid NVARCHAR(256) NULL;
-- batch
IF COL_LENGTH('dbo.VIRTUAL_FILTERS', 'approved_at') IS NULL
    ALTER TABLE dbo.VIRTUAL_FILTERS ADD approved_at DATETIMEOFFSET NULL;
-- batch
IF COL_LENGTH('dbo.ACCESS_PROFILES', 'row_scope_json') IS NULL
    ALTER TABLE dbo.ACCESS_PROFILES ADD row_scope_json NVARCHAR(MAX) NULL;
-- batch
IF COL_LENGTH('dbo.ACCESS_PROFILES', 'row_filter_predicate') IS NOT NULL
    UPDATE dbo.ACCESS_PROFILES SET row_filter_predicate = NULL WHERE row_filter_predicate IS NOT NULL;
-- batch
IF COL_LENGTH('dbo.ACCESS_PROFILES', 'row_filter_predicate') IS NOT NULL
    ALTER TABLE dbo.ACCESS_PROFILES DROP COLUMN row_filter_predicate;
