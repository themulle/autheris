-- Step 0012: TABLES.dbt_unique_id (#666), the dbt node that owns an entry. Nullable, no backfill. Forward-only.
ALTER TABLE TABLES ADD COLUMN IF NOT EXISTS dbt_unique_id VARCHAR(256) NULL;
