-- phase3: MIG-0023
-- Governance schema step 0023 (PostgreSQL): tenant-scoped golden queries maintained by data owners (dbt meta) and config.

CREATE TABLE IF NOT EXISTS golden_queries (
    id             VARCHAR(36)   NOT NULL,
    tenant_id      VARCHAR(128)  NOT NULL,
    table_id       TEXT          NOT NULL,
    source         VARCHAR(16)   NOT NULL,
    source_key     VARCHAR(256)  NOT NULL,
    title          VARCHAR(256)  NOT NULL,
    description    VARCHAR(2048) NULL,
    query_text     TEXT          NOT NULL,
    variables_json TEXT          NULL,
    content_hash   CHAR(64)      NOT NULL,
    updated_at     TIMESTAMPTZ   NOT NULL,
    CONSTRAINT pk_golden_queries PRIMARY KEY (id),
    CONSTRAINT ck_golden_queries_source CHECK (source IN ('DBT', 'CONFIG'))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_golden_queries_key
    ON golden_queries (tenant_id, table_id, source, source_key);
