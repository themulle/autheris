-- Step 0012: TABLES.dbt_unique_id (#666), the dbt node that owns an entry. Nullable, no backfill. Forward-only.
IF COL_LENGTH(N'dbo.TABLES', N'dbt_unique_id') IS NULL ALTER TABLE dbo.TABLES ADD dbt_unique_id NVARCHAR(256) NULL;
