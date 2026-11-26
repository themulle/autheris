-- Governance schema step 0049 (SQL Server): table-level write policies (issue #1154).
-- Opts specific tables into governed writes (ReadOnly, BoundedSingleTable, Full) with allowed operations,
-- optional column allowlist, PK/row-filter requirements, row caps, and server-stamped columns.

IF OBJECT_ID(N'dbo.TABLE_WRITE_POLICIES', N'U') IS NULL
CREATE TABLE dbo.TABLE_WRITE_POLICIES (
    tenant_id NVARCHAR(128) NOT NULL,
    table_id NVARCHAR(36) NOT NULL,
    mode NVARCHAR(32) NOT NULL,
    allowed_operations_json NVARCHAR(MAX) NOT NULL,
    writable_columns_json NVARCHAR(MAX) NULL,
    require_pk_predicate BIT NOT NULL,
    require_row_filter BIT NOT NULL,
    max_rows_per_statement BIGINT NULL,
    server_stamped_columns_json NVARCHAR(MAX) NULL,
    updated_by NVARCHAR(256) NOT NULL,
    updated_at DATETIMEOFFSET NOT NULL,
    CONSTRAINT PK_TABLE_WRITE_POLICIES PRIMARY KEY (tenant_id, table_id)
);
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TABLE_WRITE_POLICIES_TENANT_MODE' AND object_id = OBJECT_ID(N'dbo.TABLE_WRITE_POLICIES'))
    CREATE INDEX IX_TABLE_WRITE_POLICIES_TENANT_MODE ON dbo.TABLE_WRITE_POLICIES (tenant_id, mode);
