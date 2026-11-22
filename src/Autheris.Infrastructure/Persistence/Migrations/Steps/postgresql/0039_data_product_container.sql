-- phase3: MIG-0039
-- Governance schema step 0039 (PostgreSQL): promote data products to containers (owner group, default class, visibility) and remove legacy data-source owners.

ALTER TABLE data_products
    ADD COLUMN IF NOT EXISTS owner_group_sid VARCHAR(184) NULL;

ALTER TABLE data_products
    ADD COLUMN IF NOT EXISTS default_class VARCHAR(16) NULL;

ALTER TABLE data_products
    ADD COLUMN IF NOT EXISTS visibility VARCHAR(16) NOT NULL DEFAULT 'ON_REQUEST';

ALTER TABLE data_products
    DROP COLUMN IF EXISTS product_owner;

DROP TABLE IF EXISTS data_source_owners;
