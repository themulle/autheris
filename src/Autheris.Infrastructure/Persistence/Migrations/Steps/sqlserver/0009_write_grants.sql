-- WP-SIMPL-45: Governed write access (consent and access-profile write rules, requested write rules, write-protected columns). Additive.
IF OBJECT_ID(N'dbo.CONSENT_WRITE_RULES', N'U') IS NULL CREATE TABLE dbo.CONSENT_WRITE_RULES (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    tenant_id NVARCHAR(128) NOT NULL,
    consent_id NVARCHAR(128) NOT NULL REFERENCES dbo.CONSENTS (id) ON DELETE CASCADE,
    operation INT NOT NULL,
    row_scope INT NOT NULL,
    row_filter_json NVARCHAR(MAX) NULL,
    max_rows_per_statement BIGINT NULL,
    columns_json NVARCHAR(MAX) NOT NULL
);
-- batch
IF OBJECT_ID(N'dbo.ACCESS_PROFILE_WRITE_RULES', N'U') IS NULL CREATE TABLE dbo.ACCESS_PROFILE_WRITE_RULES (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    tenant_id NVARCHAR(128) NOT NULL,
    profile_id NVARCHAR(128) NOT NULL,
    table_identifier NVARCHAR(512) NOT NULL,
    operation INT NOT NULL,
    row_scope INT NOT NULL,
    row_filter_json NVARCHAR(MAX) NULL,
    max_rows_per_statement BIGINT NULL,
    columns_json NVARCHAR(MAX) NOT NULL
);
-- batch
IF COL_LENGTH(N'dbo.CONSENT_REQUESTS', N'requested_write_rules_json') IS NULL ALTER TABLE dbo.CONSENT_REQUESTS ADD requested_write_rules_json NVARCHAR(MAX) NULL;
-- batch
IF COL_LENGTH(N'dbo.TABLE_COLUMNS', N'write_protected') IS NULL ALTER TABLE dbo.TABLE_COLUMNS ADD write_protected BIT NOT NULL CONSTRAINT DF_TABLE_COLUMNS_write_protected DEFAULT 0;
