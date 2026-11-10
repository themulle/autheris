-- Step 0008: declared column collation and length semantics. Additive; nullable columns.
ALTER TABLE TABLE_COLUMNS ADD COLUMN IF NOT EXISTS collation VARCHAR(128) NULL;
ALTER TABLE TABLE_COLUMNS ADD COLUMN IF NOT EXISTS length_semantics VARCHAR(16) NULL;
