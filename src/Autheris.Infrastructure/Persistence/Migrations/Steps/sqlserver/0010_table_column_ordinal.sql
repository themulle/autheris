-- Step 0010: TABLE_COLUMNS registration ordinal (#610). Backfills by column name per table (no registration order exists), then NOT NULL with default 0.
IF COL_LENGTH(N'dbo.TABLE_COLUMNS', N'ordinal') IS NULL ALTER TABLE dbo.TABLE_COLUMNS ADD ordinal INT NULL;
-- batch
UPDATE c SET ordinal = r.rn FROM dbo.TABLE_COLUMNS c INNER JOIN (SELECT id, ROW_NUMBER() OVER (PARTITION BY table_id ORDER BY column_name) AS rn FROM dbo.TABLE_COLUMNS WHERE ordinal IS NULL) r ON c.id = r.id;
-- batch
ALTER TABLE dbo.TABLE_COLUMNS ALTER COLUMN ordinal INT NOT NULL;
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = N'DF_TABLE_COLUMNS_ordinal') ALTER TABLE dbo.TABLE_COLUMNS ADD CONSTRAINT DF_TABLE_COLUMNS_ordinal DEFAULT 0 FOR ordinal;
