-- phase3: MIG-0039
-- Governance schema step 0039 (SQL Server): promote data products to containers (owner group, default class, visibility) and remove legacy data-source owners.

IF COL_LENGTH(N'dbo.DATA_PRODUCTS', N'owner_group_sid') IS NULL
    ALTER TABLE dbo.DATA_PRODUCTS ADD owner_group_sid NVARCHAR(184) NULL;

-- batch

IF COL_LENGTH(N'dbo.DATA_PRODUCTS', N'default_class') IS NULL
    ALTER TABLE dbo.DATA_PRODUCTS ADD default_class NVARCHAR(16) NULL;

-- batch

IF COL_LENGTH(N'dbo.DATA_PRODUCTS', N'visibility') IS NULL
    ALTER TABLE dbo.DATA_PRODUCTS ADD visibility NVARCHAR(16) NOT NULL CONSTRAINT DF_DATA_PRODUCTS_VISIBILITY DEFAULT N'ON_REQUEST';

-- batch

IF COL_LENGTH(N'dbo.DATA_PRODUCTS', N'product_owner') IS NOT NULL
    ALTER TABLE dbo.DATA_PRODUCTS DROP COLUMN product_owner;

-- batch

IF OBJECT_ID(N'dbo.DATA_SOURCE_OWNERS', N'U') IS NOT NULL
    DROP TABLE dbo.DATA_SOURCE_OWNERS;
