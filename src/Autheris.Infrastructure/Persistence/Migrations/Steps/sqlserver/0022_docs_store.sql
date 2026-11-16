-- phase3: MIG-0022
-- Governance schema step 0022 (SQL Server): universal documentation store tables and per-tenant quota usage.

IF OBJECT_ID(N'dbo.DOC_OFFICIAL_DOCUMENTS', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DOC_OFFICIAL_DOCUMENTS (
        app_id          NVARCHAR(64)       NOT NULL,
        category        NVARCHAR(32)       NOT NULL,
        doc_id          NVARCHAR(128)      NOT NULL,
        title           NVARCHAR(200)      NOT NULL,
        content         NVARCHAR(MAX)      NOT NULL,
        content_sha256  NVARCHAR(64)       NOT NULL,
        byte_count      INT                NOT NULL,
        metadata_json   NVARCHAR(MAX)      NULL,
        version         BIGINT             NOT NULL,
        updated_at      DATETIMEOFFSET     NOT NULL,
        deleted_at      DATETIMEOFFSET     NULL,
        CONSTRAINT PK_DOC_OFFICIAL_DOCUMENTS PRIMARY KEY (app_id, category, doc_id)
    );
    CREATE INDEX idx_doc_official_updated ON dbo.DOC_OFFICIAL_DOCUMENTS (updated_at);
END;

-- batch

IF OBJECT_ID(N'dbo.DOC_TENANT_DOCUMENTS', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DOC_TENANT_DOCUMENTS (
        tenant_id       NVARCHAR(128)      NOT NULL,
        app_id          NVARCHAR(64)       NOT NULL,
        category        NVARCHAR(32)       NOT NULL,
        doc_id          NVARCHAR(128)      NOT NULL,
        title           NVARCHAR(200)      NOT NULL,
        content         NVARCHAR(MAX)      NOT NULL,
        content_sha256  NVARCHAR(64)       NOT NULL,
        byte_count      INT                NOT NULL,
        metadata_json   NVARCHAR(MAX)      NULL,
        version         BIGINT             NOT NULL,
        updated_at      DATETIMEOFFSET     NOT NULL,
        deleted_at      DATETIMEOFFSET     NULL,
        CONSTRAINT PK_DOC_TENANT_DOCUMENTS PRIMARY KEY (tenant_id, app_id, category, doc_id)
    );
    CREATE INDEX idx_doc_tenant_updated ON dbo.DOC_TENANT_DOCUMENTS (updated_at);
END;

-- batch

IF OBJECT_ID(N'dbo.DOC_TENANT_USAGE', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DOC_TENANT_USAGE (
        tenant_id   NVARCHAR(128)  NOT NULL,
        doc_count   INT            NOT NULL,
        total_bytes BIGINT         NOT NULL,
        updated_at  DATETIMEOFFSET NOT NULL,
        CONSTRAINT PK_DOC_TENANT_USAGE PRIMARY KEY (tenant_id)
    );
END;
