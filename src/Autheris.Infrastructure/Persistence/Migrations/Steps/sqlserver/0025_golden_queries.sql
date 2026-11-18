-- phase3: MIG-0025
-- Governance schema step 0025 (SQL Server): tenant-scoped golden queries maintained by data owners (dbt meta) and config.

IF OBJECT_ID(N'dbo.GOLDEN_QUERIES', N'U') IS NULL
CREATE TABLE dbo.GOLDEN_QUERIES (
    id             NVARCHAR(36)   NOT NULL,
    tenant_id      NVARCHAR(128)  NOT NULL,
    table_id       NVARCHAR(128)  NOT NULL,
    source         NVARCHAR(16)   NOT NULL,
    source_key     NVARCHAR(256)  NOT NULL,
    title          NVARCHAR(256)  NOT NULL,
    description    NVARCHAR(2048) NULL,
    query_text     NVARCHAR(MAX)  NOT NULL,
    variables_json NVARCHAR(MAX)  NULL,
    content_hash   NVARCHAR(64)    NOT NULL,
    updated_at     DATETIMEOFFSET NOT NULL,
    CONSTRAINT PK_GOLDEN_QUERIES PRIMARY KEY (id),
    CONSTRAINT CK_GOLDEN_QUERIES_SOURCE CHECK (source IN (N'DBT', N'CONFIG'))
);

-- batch

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_GOLDEN_QUERIES_KEY' AND object_id = OBJECT_ID(N'dbo.GOLDEN_QUERIES'))
CREATE UNIQUE INDEX UX_GOLDEN_QUERIES_KEY
    ON dbo.GOLDEN_QUERIES (tenant_id, table_id, source, source_key);
