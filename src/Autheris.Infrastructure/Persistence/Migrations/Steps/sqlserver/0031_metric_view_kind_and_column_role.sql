-- Governance schema step 0031 (SQL Server): add 'metric_view' to dbo.TABLES.object_kind, dbo.TABLES.metric_view_count_measure, and dbo.TABLE_COLUMNS.column_role ('dimension' or 'measure'). Forward-only and idempotent.
IF OBJECT_ID(N'dbo.CK_TABLES_object_kind', N'C') IS NOT NULL ALTER TABLE dbo.TABLES DROP CONSTRAINT CK_TABLES_object_kind;
-- batch
ALTER TABLE dbo.TABLES ADD CONSTRAINT CK_TABLES_object_kind CHECK (object_kind IN (N'table', N'view', N'metric_view'));
-- batch
IF COL_LENGTH(N'dbo.TABLES', N'metric_view_count_measure') IS NULL ALTER TABLE dbo.TABLES ADD metric_view_count_measure NVARCHAR(128) NULL;
-- batch
IF COL_LENGTH(N'dbo.TABLE_COLUMNS', N'column_role') IS NULL ALTER TABLE dbo.TABLE_COLUMNS ADD column_role NVARCHAR(16) NULL;
-- batch
IF OBJECT_ID(N'dbo.CK_TABLE_COLUMNS_column_role', N'C') IS NULL ALTER TABLE dbo.TABLE_COLUMNS ADD CONSTRAINT CK_TABLE_COLUMNS_column_role CHECK (column_role IS NULL OR column_role IN (N'dimension', N'measure'));