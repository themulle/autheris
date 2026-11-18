-- phase3: MIG-0026
-- Governance schema step 0026 (PostgreSQL): links from documents and applications to catalog objects, and the discovery-hidden flag on documents.

CREATE TABLE IF NOT EXISTS doc_links (
    tenant_id      VARCHAR(128) NOT NULL,
    link_id        UUID         NOT NULL,
    source_kind    VARCHAR(16)  NOT NULL,
    app_id         VARCHAR(64)  NOT NULL,
    category       VARCHAR(32)  NOT NULL,
    doc_id         VARCHAR(128) NOT NULL,
    doc_tenant_id  VARCHAR(128) NOT NULL,
    target_kind    VARCHAR(16)  NOT NULL,
    target_domain  VARCHAR(256) NOT NULL,
    target_schema  VARCHAR(256) NOT NULL,
    target_name    VARCHAR(256) NOT NULL,
    target_column  VARCHAR(256) NOT NULL,
    link_digest  BYTEA        NOT NULL,
    created_by     VARCHAR(256) NOT NULL,
    created_at_utc TIMESTAMPTZ  NOT NULL,
    PRIMARY KEY (link_id)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_doc_links_source_target
    ON doc_links (tenant_id, link_digest);

CREATE INDEX IF NOT EXISTS idx_doc_links_source
    ON doc_links (tenant_id, source_kind, app_id, category, doc_id);

CREATE INDEX IF NOT EXISTS idx_doc_links_target
    ON doc_links (tenant_id, target_domain, target_schema);

ALTER TABLE doc_official_documents ADD COLUMN IF NOT EXISTS discovery_hidden BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE doc_tenant_documents ADD COLUMN IF NOT EXISTS discovery_hidden BOOLEAN NOT NULL DEFAULT FALSE;
