-- phase3: MIG-0036
-- Governance schema step 0036 (PostgreSQL): governance domains, domain table assignments, object protection,
-- access policy settings, governance epochs, and idempotent seeding and mask-category backfill for active tenants.

CREATE TABLE IF NOT EXISTS governance_domains (
    tenant_id VARCHAR(128) NOT NULL,
    id VARCHAR(36) NOT NULL,
    name VARCHAR(128) NOT NULL,
    description VARCHAR(1024) NULL,
    contact VARCHAR(256) NULL,
    owner_group_sid VARCHAR(184) NULL,
    default_class VARCHAR(16) NULL,
    visibility VARCHAR(16) NOT NULL,
    is_system BOOLEAN NOT NULL,
    version INTEGER NOT NULL,
    created_by VARCHAR(256) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT pk_governance_domains PRIMARY KEY (tenant_id, id),
    CONSTRAINT ck_governance_domains_default_class CHECK (default_class IS NULL OR default_class IN ('PUBLIC', 'INTERNAL', 'CONFIDENTIAL', 'SECRET')),
    CONSTRAINT ck_governance_domains_visibility CHECK (visibility IN ('OPEN', 'ON_REQUEST'))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_governance_domains_name
    ON governance_domains (tenant_id, name);

CREATE TABLE IF NOT EXISTS governance_domain_tables (
    tenant_id VARCHAR(128) NOT NULL,
    table_id VARCHAR(256) NOT NULL,
    domain_id VARCHAR(36) NOT NULL,
    assigned_by VARCHAR(256) NOT NULL,
    assigned_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT pk_governance_domain_tables PRIMARY KEY (tenant_id, table_id),
    CONSTRAINT fk_governance_domain_tables_domain FOREIGN KEY (tenant_id, domain_id)
        REFERENCES governance_domains (tenant_id, id)
);

CREATE INDEX IF NOT EXISTS ix_governance_domain_tables_domain
    ON governance_domain_tables (tenant_id, domain_id);

CREATE TABLE IF NOT EXISTS object_protection (
    tenant_id VARCHAR(128) NOT NULL,
    table_id VARCHAR(256) NOT NULL,
    column_name VARCHAR(256) NOT NULL,
    protection_class VARCHAR(16) NULL,
    mask_category VARCHAR(64) NULL,
    updated_by VARCHAR(256) NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT pk_object_protection PRIMARY KEY (tenant_id, table_id, column_name),
    CONSTRAINT ck_object_protection_class CHECK (protection_class IS NULL OR protection_class IN ('PUBLIC', 'INTERNAL', 'CONFIDENTIAL', 'SECRET')),
    CONSTRAINT ck_object_protection_mask_category CHECK (mask_category IS NULL OR UPPER(mask_category) <> 'PUBLIC')
);

CREATE TABLE IF NOT EXISTS access_policy_settings (
    tenant_id VARCHAR(128) NOT NULL,
    settings_json TEXT NOT NULL,
    version INTEGER NOT NULL,
    updated_by VARCHAR(256) NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT pk_access_policy_settings PRIMARY KEY (tenant_id)
);

CREATE TABLE IF NOT EXISTS governance_epochs (
    tenant_id VARCHAR(128) NOT NULL,
    epoch BIGINT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT pk_governance_epochs PRIMARY KEY (tenant_id)
);

INSERT INTO governance_domains (
    tenant_id, id, name, description, contact, owner_group_sid,
    default_class, visibility, is_system, version, created_by, created_at, updated_at
)
SELECT DISTINCT
    tr.tenant_id,
    '00000000-0000-0000-0000-000000000000',
    'Unassigned',
    NULL,
    NULL,
    NULL,
    'CONFIDENTIAL',
    'ON_REQUEST',
    TRUE,
    1,
    'system:migration-0036',
    CURRENT_TIMESTAMP,
    CURRENT_TIMESTAMP
FROM tenant_registry tr
WHERE tr.collided = 0
  AND tr.tenant_id IS NOT NULL
  AND BTRIM(tr.tenant_id) <> ''
ON CONFLICT (tenant_id, id) DO NOTHING;

INSERT INTO governance_epochs (tenant_id, epoch, updated_at)
SELECT DISTINCT
    tr.tenant_id,
    1,
    CURRENT_TIMESTAMP
FROM tenant_registry tr
WHERE tr.collided = 0
  AND tr.tenant_id IS NOT NULL
  AND BTRIM(tr.tenant_id) <> ''
ON CONFLICT (tenant_id) DO NOTHING;

INSERT INTO object_protection (
    tenant_id, table_id, column_name, protection_class, mask_category, updated_by, updated_at
)
SELECT
    ranked.tenant_id,
    ranked.table_id,
    ranked.column_name,
    NULL,
    ranked.code,
    'system:migration-0036',
    CURRENT_TIMESTAMP
FROM (
    SELECT
        t.tenant_id,
        tc.table_id,
        tc.column_name,
        cc.code,
        ROW_NUMBER() OVER (
            PARTITION BY t.tenant_id, tc.table_id, tc.column_name
            ORDER BY COALESCE(dc_tenant.restriction_rank, dc_global.restriction_rank, 1) DESC,
                     CASE WHEN cc.scope = t.tenant_id THEN 0 ELSE 1 END ASC,
                     cc.code ASC
        ) AS rn
    FROM (
        SELECT DISTINCT tr.tenant_id
        FROM tenant_registry tr
        WHERE tr.collided = 0
          AND tr.tenant_id IS NOT NULL
          AND BTRIM(tr.tenant_id) <> ''
    ) t
    INNER JOIN column_classifications cc
            ON (cc.scope = t.tenant_id OR cc.scope = '*')
           AND UPPER(cc.code) <> 'PUBLIC'
    INNER JOIN table_columns tc
            ON tc.id = cc.table_column_id
    LEFT JOIN data_classifications dc_tenant
           ON dc_tenant.scope = t.tenant_id
          AND dc_tenant.code = cc.code
    LEFT JOIN data_classifications dc_global
           ON dc_global.scope = '*'
          AND dc_global.code = cc.code
) ranked
WHERE ranked.rn = 1
ON CONFLICT (tenant_id, table_id, column_name) DO NOTHING;
