-- Issue #404: physical (source-observed) non-unique indexes of a table. Additive; separate from the operator-declared TABLE_INDEXES. No index name and no timestamp column. Both tables cascade with the catalog table row, so a deleted table cannot leave stale rows behind.
CREATE TABLE IF NOT EXISTS TABLE_PHYSICAL_INDEXES (
    id TEXT PRIMARY KEY,
    table_id TEXT NOT NULL REFERENCES TABLES (id) ON DELETE CASCADE,
    index_ordinal INTEGER NOT NULL CHECK (index_ordinal >= 1)
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_TABLE_PHYSICAL_INDEXES_ORDINAL ON TABLE_PHYSICAL_INDEXES (table_id, index_ordinal);
CREATE TABLE IF NOT EXISTS TABLE_PHYSICAL_INDEX_COLUMNS (
    index_id TEXT NOT NULL REFERENCES TABLE_PHYSICAL_INDEXES (id) ON DELETE CASCADE,
    column_ordinal INTEGER NOT NULL CHECK (column_ordinal >= 1),
    table_column_id TEXT NOT NULL,
    is_descending BOOLEAN NOT NULL DEFAULT FALSE,
    PRIMARY KEY (index_id, column_ordinal)
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_TABLE_PHYSICAL_INDEX_COLUMNS_COLUMN ON TABLE_PHYSICAL_INDEX_COLUMNS (index_id, table_column_id);
