-- phase3: MIG-0023
-- Governance schema step 0023 (PostgreSQL): data products (OD-44): product definitions, their definition history and data-source owners.

CREATE TABLE IF NOT EXISTS data_products (
    tenant_id          VARCHAR(128)   NOT NULL,
    product_schema     VARCHAR(64)    NOT NULL,
    product_name       VARCHAR(64)    NOT NULL,
    id                 VARCHAR(36)    NOT NULL,
    kind               VARCHAR(16)    NOT NULL,
    definition         TEXT           NOT NULL,
    definition_hash    VARCHAR(64)    NOT NULL,
    base_objects_json  TEXT           NOT NULL,
    columns_json       TEXT           NOT NULL,
    created_by         VARCHAR(256)   NOT NULL,
    authority          VARCHAR(32)    NOT NULL,
    status             VARCHAR(16)    NOT NULL,
    managed_by         VARCHAR(64)    NULL,
    load_json          TEXT           NOT NULL,
    limits_json        TEXT           NOT NULL,
    description        VARCHAR(1000)  NULL,
    product_owner      VARCHAR(256)   NOT NULL,
    version            INTEGER        NOT NULL,
    created_at         TIMESTAMPTZ    NOT NULL,
    updated_at         TIMESTAMPTZ    NOT NULL,
    PRIMARY KEY (tenant_id, product_schema, product_name)
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_data_products_tenant_id
    ON data_products (tenant_id, id);

CREATE TABLE IF NOT EXISTS data_product_versions (
    tenant_id          VARCHAR(128)   NOT NULL,
    product_id         VARCHAR(36)    NOT NULL,
    version            INTEGER        NOT NULL,
    definition_hash    VARCHAR(64)    NOT NULL,
    definition         TEXT           NOT NULL,
    author             VARCHAR(256)   NOT NULL,
    authority          VARCHAR(32)    NOT NULL,
    created_at         TIMESTAMPTZ    NOT NULL,
    PRIMARY KEY (tenant_id, product_id, version)
);

CREATE TABLE IF NOT EXISTS data_source_owners (
    tenant_id          VARCHAR(128)   NOT NULL,
    data_source        VARCHAR(128)   NOT NULL,
    owner_sid          VARCHAR(128)   NOT NULL,
    assigned_by        VARCHAR(256)   NOT NULL,
    assigned_at        TIMESTAMPTZ    NOT NULL,
    PRIMARY KEY (tenant_id, data_source, owner_sid)
);
