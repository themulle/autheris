-- guard: SELECT CASE WHEN (SELECT COUNT(*) FROM pragma_table_info('TABLE_PRIMARY_KEY_COLUMNS') WHERE name = 'is_descending') = 1 AND (SELECT COUNT(*) FROM pragma_table_info('TABLE_INDEX_COLUMNS') WHERE name = 'is_descending') = 1 THEN 1 ELSE 0 END
-- Issue #390 (390a): declared sort direction of primary key and index columns. Additive; existing rows read as ascending.
ALTER TABLE TABLE_PRIMARY_KEY_COLUMNS ADD COLUMN is_descending INTEGER NOT NULL DEFAULT 0 CHECK (is_descending IN (0, 1));
ALTER TABLE TABLE_INDEX_COLUMNS ADD COLUMN is_descending INTEGER NOT NULL DEFAULT 0 CHECK (is_descending IN (0, 1));
