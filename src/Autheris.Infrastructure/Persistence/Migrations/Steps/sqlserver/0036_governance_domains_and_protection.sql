-- phase3: MIG-0036
-- Governance schema step 0036 (SQL Server): governance domains, domain table assignments, object protection,
-- access policy settings, governance epochs, and idempotent seeding and mask-category backfill for active tenants.

IF OBJECT_ID(N'dbo.GOVERNANCE_DOMAINS', N'U') IS NULL
CREATE TABLE dbo.GOVERNANCE_DOMAINS (
    tenant_id NVARCHAR(128) NOT NULL,
    id NVARCHAR(36) NOT NULL,
    name NVARCHAR(128) NOT NULL,
    description NVARCHAR(1024) NULL,
    contact NVARCHAR(256) NULL,
    owner_group_sid NVARCHAR(184) NULL,
    default_class NVARCHAR(16) NULL,
    visibility NVARCHAR(16) NOT NULL,
    is_system BIT NOT NULL,
    version INT NOT NULL,
    created_by NVARCHAR(256) NOT NULL,
    created_at DATETIMEOFFSET NOT NULL,
    updated_at DATETIMEOFFSET NOT NULL,
    CONSTRAINT PK_GOVERNANCE_DOMAINS PRIMARY KEY (tenant_id, id),
    CONSTRAINT CK_GOVERNANCE_DOMAINS_DEFAULT_CLASS CHECK (default_class IS NULL OR default_class IN (N'PUBLIC', N'INTERNAL', N'CONFIDENTIAL', N'SECRET')),
    CONSTRAINT CK_GOVERNANCE_DOMAINS_VISIBILITY CHECK (visibility IN (N'OPEN', N'ON_REQUEST'))
);

-- batch

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_GOVERNANCE_DOMAINS_NAME' AND object_id = OBJECT_ID(N'dbo.GOVERNANCE_DOMAINS'))
    CREATE UNIQUE INDEX UX_GOVERNANCE_DOMAINS_NAME ON dbo.GOVERNANCE_DOMAINS (tenant_id, name);

-- batch

IF OBJECT_ID(N'dbo.GOVERNANCE_DOMAIN_TABLES', N'U') IS NULL
CREATE TABLE dbo.GOVERNANCE_DOMAIN_TABLES (
    tenant_id NVARCHAR(128) NOT NULL,
    table_id NVARCHAR(256) NOT NULL,
    domain_id NVARCHAR(36) NOT NULL,
    assigned_by NVARCHAR(256) NOT NULL,
    assigned_at DATETIMEOFFSET NOT NULL,
    CONSTRAINT PK_GOVERNANCE_DOMAIN_TABLES PRIMARY KEY (tenant_id, table_id),
    CONSTRAINT FK_GOVERNANCE_DOMAIN_TABLES_DOMAIN FOREIGN KEY (tenant_id, domain_id)
        REFERENCES dbo.GOVERNANCE_DOMAINS (tenant_id, id)
);

-- batch

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_GOVERNANCE_DOMAIN_TABLES_DOMAIN' AND object_id = OBJECT_ID(N'dbo.GOVERNANCE_DOMAIN_TABLES'))
    CREATE INDEX IX_GOVERNANCE_DOMAIN_TABLES_DOMAIN ON dbo.GOVERNANCE_DOMAIN_TABLES (tenant_id, domain_id);

-- batch

-- Nonclustered PK keeps the 1280-byte composite key within SQL Server's 1700-byte nonclustered key limit.
IF OBJECT_ID(N'dbo.OBJECT_PROTECTION', N'U') IS NULL
CREATE TABLE dbo.OBJECT_PROTECTION (
    tenant_id NVARCHAR(128) NOT NULL,
    table_id NVARCHAR(256) NOT NULL,
    column_name NVARCHAR(256) NOT NULL,
    protection_class NVARCHAR(16) NULL,
    mask_category NVARCHAR(64) NULL,
    updated_by NVARCHAR(256) NOT NULL,
    updated_at DATETIMEOFFSET NOT NULL,
    CONSTRAINT PK_OBJECT_PROTECTION PRIMARY KEY NONCLUSTERED (tenant_id, table_id, column_name),
    CONSTRAINT CK_OBJECT_PROTECTION_CLASS CHECK (protection_class IS NULL OR protection_class IN (N'PUBLIC', N'INTERNAL', N'CONFIDENTIAL', N'SECRET')),
    CONSTRAINT CK_OBJECT_PROTECTION_MASK_CATEGORY CHECK (mask_category IS NULL OR UPPER(mask_category) <> N'PUBLIC')
);

-- batch

IF OBJECT_ID(N'dbo.ACCESS_POLICY_SETTINGS', N'U') IS NULL
CREATE TABLE dbo.ACCESS_POLICY_SETTINGS (
    tenant_id NVARCHAR(128) NOT NULL,
    settings_json NVARCHAR(MAX) NOT NULL,
    version INT NOT NULL,
    updated_by NVARCHAR(256) NOT NULL,
    updated_at DATETIMEOFFSET NOT NULL,
    CONSTRAINT PK_ACCESS_POLICY_SETTINGS PRIMARY KEY (tenant_id)
);

-- batch

IF OBJECT_ID(N'dbo.GOVERNANCE_EPOCHS', N'U') IS NULL
CREATE TABLE dbo.GOVERNANCE_EPOCHS (
    tenant_id NVARCHAR(128) NOT NULL,
    epoch BIGINT NOT NULL,
    updated_at DATETIMEOFFSET NOT NULL,
    CONSTRAINT PK_GOVERNANCE_EPOCHS PRIMARY KEY (tenant_id)
);

-- batch

INSERT INTO dbo.GOVERNANCE_DOMAINS (
    tenant_id, id, name, description, contact, owner_group_sid,
    default_class, visibility, is_system, version, created_by, created_at, updated_at
)
SELECT
    t.tenant_id,
    N'00000000-0000-0000-0000-000000000000',
    N'Unassigned',
    NULL,
    NULL,
    NULL,
    N'CONFIDENTIAL',
    N'ON_REQUEST',
    CAST(1 AS BIT),
    1,
    N'system:migration-0036',
    SYSUTCDATETIME(),
    SYSUTCDATETIME()
FROM (
    SELECT DISTINCT tr.tenant_id
    FROM dbo.TENANT_REGISTRY tr
    WHERE tr.collided = 0
      AND tr.tenant_id IS NOT NULL
      AND LTRIM(RTRIM(tr.tenant_id)) <> N''
) t
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.GOVERNANCE_DOMAINS d
    WHERE d.tenant_id = t.tenant_id
      AND d.id = N'00000000-0000-0000-0000-000000000000'
);

-- batch

INSERT INTO dbo.GOVERNANCE_EPOCHS (tenant_id, epoch, updated_at)
SELECT
    t.tenant_id,
    CAST(1 AS BIGINT),
    SYSUTCDATETIME()
FROM (
    SELECT DISTINCT tr.tenant_id
    FROM dbo.TENANT_REGISTRY tr
    WHERE tr.collided = 0
      AND tr.tenant_id IS NOT NULL
      AND LTRIM(RTRIM(tr.tenant_id)) <> N''
) t
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.GOVERNANCE_EPOCHS e
    WHERE e.tenant_id = t.tenant_id
);

-- batch

INSERT INTO dbo.OBJECT_PROTECTION (
    tenant_id, table_id, column_name, protection_class, mask_category, updated_by, updated_at
)
SELECT
    ranked.tenant_id,
    ranked.table_id,
    ranked.column_name,
    NULL,
    ranked.code,
    N'system:migration-0036',
    SYSUTCDATETIME()
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
        FROM dbo.TENANT_REGISTRY tr
        WHERE tr.collided = 0
          AND tr.tenant_id IS NOT NULL
          AND LTRIM(RTRIM(tr.tenant_id)) <> N''
    ) t
    INNER JOIN dbo.COLUMN_CLASSIFICATIONS cc
            ON (cc.scope = t.tenant_id OR cc.scope = N'*')
           AND UPPER(cc.code) <> N'PUBLIC'
    INNER JOIN dbo.TABLE_COLUMNS tc
            ON tc.id = cc.table_column_id
    LEFT JOIN dbo.DATA_CLASSIFICATIONS dc_tenant
           ON dc_tenant.scope = t.tenant_id
          AND dc_tenant.code = cc.code
    LEFT JOIN dbo.DATA_CLASSIFICATIONS dc_global
           ON dc_global.scope = N'*'
          AND dc_global.code = cc.code
) ranked
WHERE ranked.rn = 1
  AND NOT EXISTS (
      SELECT 1
      FROM dbo.OBJECT_PROTECTION p
      WHERE p.tenant_id = ranked.tenant_id
        AND p.table_id = ranked.table_id
        AND p.column_name = ranked.column_name
  );
