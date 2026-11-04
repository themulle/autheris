-- Issue #448: Catalog-owned minimum group size and non-personal table flag. Additive.
IF COL_LENGTH(N'dbo.TABLES', N'min_group_size') IS NULL ALTER TABLE dbo.TABLES ADD min_group_size INT NULL;
-- batch
IF COL_LENGTH(N'dbo.TABLES', N'non_personal') IS NULL ALTER TABLE dbo.TABLES ADD non_personal BIT NOT NULL CONSTRAINT DF_TABLES_non_personal DEFAULT 0;
-- batch
IF COL_LENGTH(N'dbo.TABLE_CATALOG_SETTINGS', N'min_group_size') IS NULL ALTER TABLE dbo.TABLE_CATALOG_SETTINGS ADD min_group_size INT NULL;
-- batch
IF COL_LENGTH(N'dbo.TABLE_CATALOG_SETTINGS', N'non_personal') IS NULL ALTER TABLE dbo.TABLE_CATALOG_SETTINGS ADD non_personal BIT NULL;
