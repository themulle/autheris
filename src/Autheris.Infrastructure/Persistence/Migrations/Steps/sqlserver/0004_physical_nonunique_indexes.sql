-- Issue #404: physical (source-observed) non-unique indexes of a table. Additive; separate from the operator-declared TABLE_INDEXES. No index name and no timestamp column.
IF OBJECT_ID(N'dbo.TABLE_PHYSICAL_INDEXES', N'U') IS NULL CREATE TABLE dbo.TABLE_PHYSICAL_INDEXES (
    id NVARCHAR(128) NOT NULL PRIMARY KEY,
    table_id NVARCHAR(128) NOT NULL,
    index_ordinal INT NOT NULL CHECK (index_ordinal >= 1)
);
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_TABLE_PHYSICAL_INDEXES_ORDINAL' AND object_id = OBJECT_ID(N'dbo.TABLE_PHYSICAL_INDEXES')) CREATE UNIQUE INDEX UX_TABLE_PHYSICAL_INDEXES_ORDINAL ON dbo.TABLE_PHYSICAL_INDEXES (table_id, index_ordinal);
-- batch
IF OBJECT_ID(N'dbo.TABLE_PHYSICAL_INDEX_COLUMNS', N'U') IS NULL CREATE TABLE dbo.TABLE_PHYSICAL_INDEX_COLUMNS (
    index_id NVARCHAR(128) NOT NULL,
    column_ordinal INT NOT NULL CHECK (column_ordinal >= 1),
    table_column_id NVARCHAR(128) NOT NULL,
    is_descending BIT NOT NULL DEFAULT 0,
    PRIMARY KEY (index_id, column_ordinal)
);
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_TABLE_PHYSICAL_INDEX_COLUMNS_COLUMN' AND object_id = OBJECT_ID(N'dbo.TABLE_PHYSICAL_INDEX_COLUMNS')) CREATE UNIQUE INDEX UX_TABLE_PHYSICAL_INDEX_COLUMNS_COLUMN ON dbo.TABLE_PHYSICAL_INDEX_COLUMNS (index_id, table_column_id);
