-- Governance schema step 0049 (PostgreSQL): table-level write policies (issue #1154).
-- Opts specific tables into governed writes (ReadOnly, BoundedSingleTable, Full) with allowed operations,
-- optional column allowlist, PK/row-filter requirements, row caps, and server-stamped columns.

CREATE TABLE IF NOT EXISTS table_write_policies (
    tenant_id VARCHAR(128) NOT NULL,
    table_id VARCHAR(36) NOT NULL,
    mode VARCHAR(32) NOT NULL,
    allowed_operations_json TEXT NOT NULL,
    writable_columns_json TEXT NULL,
    require_pk_predicate BOOLEAN NOT NULL,
    require_row_filter BOOLEAN NOT NULL,
    max_rows_per_statement BIGINT NULL,
    server_stamped_columns_json TEXT NULL,
    updated_by VARCHAR(256) NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (tenant_id, table_id)
);

CREATE INDEX IF NOT EXISTS ix_table_write_policies_tenant_mode
    ON table_write_policies (tenant_id, mode);
