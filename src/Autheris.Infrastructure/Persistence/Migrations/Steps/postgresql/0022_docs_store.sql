-- phase3: MIG-0022
-- Governance schema step 0022 (PostgreSQL): universal documentation store tables and per-tenant quota usage.

CREATE TABLE IF NOT EXISTS doc_official_documents (
    app_id          VARCHAR(64)  NOT NULL,
    category        VARCHAR(32)  NOT NULL,
    doc_id          VARCHAR(128) NOT NULL,
    title           VARCHAR(200) NOT NULL,
    content         TEXT         NOT NULL,
    content_sha256  VARCHAR(64)  NOT NULL,
    byte_count      INTEGER      NOT NULL,
    metadata_json   TEXT         NULL,
    version         BIGINT       NOT NULL,
    updated_at      TIMESTAMPTZ  NOT NULL,
    deleted_at      TIMESTAMPTZ  NULL,
    PRIMARY KEY (app_id, category, doc_id)
);

CREATE INDEX IF NOT EXISTS idx_doc_official_updated
    ON doc_official_documents (updated_at);

CREATE TABLE IF NOT EXISTS doc_tenant_documents (
    tenant_id       VARCHAR(128) NOT NULL,
    app_id          VARCHAR(64)  NOT NULL,
    category        VARCHAR(32)  NOT NULL,
    doc_id          VARCHAR(128) NOT NULL,
    title           VARCHAR(200) NOT NULL,
    content         TEXT         NOT NULL,
    content_sha256  VARCHAR(64)  NOT NULL,
    byte_count      INTEGER      NOT NULL,
    metadata_json   TEXT         NULL,
    version         BIGINT       NOT NULL,
    updated_at      TIMESTAMPTZ  NOT NULL,
    deleted_at      TIMESTAMPTZ  NULL,
    PRIMARY KEY (tenant_id, app_id, category, doc_id)
);

CREATE INDEX IF NOT EXISTS idx_doc_tenant_updated
    ON doc_tenant_documents (updated_at);

CREATE TABLE IF NOT EXISTS doc_tenant_usage (
    tenant_id   VARCHAR(128) NOT NULL PRIMARY KEY,
    doc_count   INTEGER      NOT NULL,
    total_bytes BIGINT       NOT NULL,
    updated_at  TIMESTAMPTZ  NOT NULL
);
