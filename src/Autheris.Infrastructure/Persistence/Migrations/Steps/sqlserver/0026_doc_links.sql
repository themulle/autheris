-- phase3: MIG-0026
-- Governance schema step 0026 (SQL Server): links from documents and applications to catalog objects, and the discovery-hidden flag on documents.

IF OBJECT_ID(N'dbo.DOC_LINKS', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DOC_LINKS (
        tenant_id      NVARCHAR(128)    NOT NULL,
        link_id        UNIQUEIDENTIFIER NOT NULL,
        source_kind    NVARCHAR(16)     NOT NULL,
        app_id         NVARCHAR(64)     NOT NULL,
        category       NVARCHAR(32)     NOT NULL,
        doc_id         NVARCHAR(128)    NOT NULL,
        doc_tenant_id  NVARCHAR(128)    NOT NULL,
        target_kind    NVARCHAR(16)     NOT NULL,
        target_domain  NVARCHAR(256)    NOT NULL,
        target_schema  NVARCHAR(256)    NOT NULL,
        target_name    NVARCHAR(256)    NOT NULL,
        target_column  NVARCHAR(256)    NOT NULL,
        created_by     NVARCHAR(256)    NOT NULL,
        created_at_utc DATETIMEOFFSET   NOT NULL,
        CONSTRAINT PK_DOC_LINKS PRIMARY KEY (link_id)
    );
    CREATE UNIQUE INDEX ux_doc_links_source_target
        ON dbo.DOC_LINKS (tenant_id, source_kind, app_id, category, doc_id, doc_tenant_id, target_kind, target_domain, target_schema, target_name, target_column);
    CREATE INDEX idx_doc_links_target
        ON dbo.DOC_LINKS (tenant_id, target_domain, target_schema, target_name);
END;

-- batch

IF COL_LENGTH(N'dbo.DOC_OFFICIAL_DOCUMENTS', N'discovery_hidden') IS NULL ALTER TABLE dbo.DOC_OFFICIAL_DOCUMENTS ADD discovery_hidden BIT NOT NULL CONSTRAINT DF_DOC_OFFICIAL_DOCUMENTS_discovery_hidden DEFAULT 0;

-- batch

IF COL_LENGTH(N'dbo.DOC_TENANT_DOCUMENTS', N'discovery_hidden') IS NULL ALTER TABLE dbo.DOC_TENANT_DOCUMENTS ADD discovery_hidden BIT NOT NULL CONSTRAINT DF_DOC_TENANT_DOCUMENTS_discovery_hidden DEFAULT 0;
