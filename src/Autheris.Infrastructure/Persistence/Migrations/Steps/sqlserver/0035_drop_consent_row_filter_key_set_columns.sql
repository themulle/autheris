-- Governance schema step 0035 (SQL Server): drop superseded key_set_* columns from dbo.CONSENT_ROW_FILTERS (WP-KS-3). Forward-only and idempotent.
IF COL_LENGTH(N'dbo.CONSENT_ROW_FILTERS', N'key_set_strategy') IS NOT NULL ALTER TABLE dbo.CONSENT_ROW_FILTERS DROP COLUMN key_set_strategy;
-- batch
IF COL_LENGTH(N'dbo.CONSENT_ROW_FILTERS', N'key_set_duration_seconds') IS NOT NULL ALTER TABLE dbo.CONSENT_ROW_FILTERS DROP COLUMN key_set_duration_seconds;
-- batch
IF COL_LENGTH(N'dbo.CONSENT_ROW_FILTERS', N'key_set_max_keys') IS NOT NULL ALTER TABLE dbo.CONSENT_ROW_FILTERS DROP COLUMN key_set_max_keys;
