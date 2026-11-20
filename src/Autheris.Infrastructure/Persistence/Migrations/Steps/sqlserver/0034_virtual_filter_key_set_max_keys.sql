-- Governance schema step 0034 (SQL Server): add nullable key_set_max_keys to dbo.VIRTUAL_FILTERS. Forward-only and idempotent.
IF COL_LENGTH(N'dbo.VIRTUAL_FILTERS', N'key_set_max_keys') IS NULL ALTER TABLE dbo.VIRTUAL_FILTERS ADD key_set_max_keys INT NULL;
