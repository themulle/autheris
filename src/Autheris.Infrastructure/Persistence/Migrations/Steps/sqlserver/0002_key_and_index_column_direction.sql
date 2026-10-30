-- Issue #390 (390a): declared sort direction of primary key and index columns. Additive; existing rows read as ascending.
IF COL_LENGTH(N'dbo.TABLE_PRIMARY_KEY_COLUMNS', N'is_descending') IS NULL ALTER TABLE dbo.TABLE_PRIMARY_KEY_COLUMNS ADD is_descending BIT NOT NULL CONSTRAINT DF_TABLE_PRIMARY_KEY_COLUMNS_is_descending DEFAULT 0;
-- batch
IF COL_LENGTH(N'dbo.TABLE_INDEX_COLUMNS', N'is_descending') IS NULL ALTER TABLE dbo.TABLE_INDEX_COLUMNS ADD is_descending BIT NOT NULL CONSTRAINT DF_TABLE_INDEX_COLUMNS_is_descending DEFAULT 0;
