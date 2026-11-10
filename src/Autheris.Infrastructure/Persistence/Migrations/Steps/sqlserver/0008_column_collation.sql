-- Step 0008: declared column collation and length semantics. Additive; nullable columns.
IF COL_LENGTH(N'dbo.TABLE_COLUMNS', N'collation') IS NULL ALTER TABLE dbo.TABLE_COLUMNS ADD collation NVARCHAR(128) NULL;
-- batch
IF COL_LENGTH(N'dbo.TABLE_COLUMNS', N'length_semantics') IS NULL ALTER TABLE dbo.TABLE_COLUMNS ADD length_semantics NVARCHAR(16) NULL;
