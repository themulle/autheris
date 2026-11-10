-- Step 0008: declared column collation and length semantics. Additive; nullable columns.
ALTER TABLE TABLE_COLUMNS ADD COLUMN IF NOT EXISTS "collation" TEXT NULL;
ALTER TABLE TABLE_COLUMNS ADD COLUMN IF NOT EXISTS "length_semantics" TEXT NULL;
