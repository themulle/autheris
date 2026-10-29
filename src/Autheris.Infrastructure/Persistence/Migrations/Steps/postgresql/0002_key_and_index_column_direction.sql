-- Issue #390 (390a): declared sort direction of primary key and index columns. Additive; existing rows read as ascending.
ALTER TABLE TABLE_PRIMARY_KEY_COLUMNS ADD COLUMN IF NOT EXISTS is_descending BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE TABLE_INDEX_COLUMNS ADD COLUMN IF NOT EXISTS is_descending BOOLEAN NOT NULL DEFAULT FALSE;
