-- phase3: MIG-0028
-- Governance schema step 0028 (SQL Server): data products (OD-44): product definitions, their definition history and data-source owners.

IF OBJECT_ID(N'dbo.DATA_PRODUCTS', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DATA_PRODUCTS (
        tenant_id          NVARCHAR(128)    NOT NULL,
        product_schema     NVARCHAR(64)     NOT NULL,
        product_name       NVARCHAR(64)     NOT NULL,
        id                 NVARCHAR(36)     NOT NULL,
        kind               NVARCHAR(16)     NOT NULL,
        definition         NVARCHAR(MAX)    NOT NULL,
        definition_hash    NVARCHAR(64)     NOT NULL,
        base_objects_json  NVARCHAR(MAX)    NOT NULL,
        columns_json       NVARCHAR(MAX)    NOT NULL,
        created_by         NVARCHAR(256)    NOT NULL,
        authority          NVARCHAR(32)     NOT NULL,
        status             NVARCHAR(16)     NOT NULL,
        managed_by         NVARCHAR(64)     NULL,
        load_json          NVARCHAR(MAX)    NOT NULL,
        limits_json        NVARCHAR(MAX)    NOT NULL,
        description        NVARCHAR(1000)   NULL,
        product_owner      NVARCHAR(256)    NOT NULL,
        version            INT              NOT NULL,
        created_at         DATETIMEOFFSET   NOT NULL,
        updated_at         DATETIMEOFFSET   NOT NULL,
        CONSTRAINT PK_DATA_PRODUCTS PRIMARY KEY (tenant_id, product_schema, product_name)
    );
    CREATE UNIQUE INDEX uq_data_products_tenant_id ON dbo.DATA_PRODUCTS (tenant_id, id);
END;

-- batch

IF OBJECT_ID(N'dbo.DATA_PRODUCT_VERSIONS', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DATA_PRODUCT_VERSIONS (
        tenant_id          NVARCHAR(128)    NOT NULL,
        product_id         NVARCHAR(36)     NOT NULL,
        version            INT              NOT NULL,
        definition_hash    NVARCHAR(64)     NOT NULL,
        definition         NVARCHAR(MAX)    NOT NULL,
        author             NVARCHAR(256)    NOT NULL,
        authority          NVARCHAR(32)     NOT NULL,
        created_at         DATETIMEOFFSET   NOT NULL,
        CONSTRAINT PK_DATA_PRODUCT_VERSIONS PRIMARY KEY (tenant_id, product_id, version)
    );
END;

-- batch

IF OBJECT_ID(N'dbo.DATA_SOURCE_OWNERS', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DATA_SOURCE_OWNERS (
        tenant_id          NVARCHAR(128)    NOT NULL,
        data_source        NVARCHAR(128)    NOT NULL,
        owner_sid          NVARCHAR(128)    NOT NULL,
        assigned_by        NVARCHAR(256)    NOT NULL,
        assigned_at        DATETIMEOFFSET   NOT NULL,
        CONSTRAINT PK_DATA_SOURCE_OWNERS PRIMARY KEY (tenant_id, data_source, owner_sid)
    );
END;
