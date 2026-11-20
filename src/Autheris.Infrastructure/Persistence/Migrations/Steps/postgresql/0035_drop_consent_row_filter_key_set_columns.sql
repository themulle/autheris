-- Governance schema step 0035 (PostgreSQL): drop superseded key_set_* columns from consent_row_filters (WP-KS-3). Forward-only and idempotent.
ALTER TABLE consent_row_filters DROP COLUMN IF EXISTS key_set_strategy;
ALTER TABLE consent_row_filters DROP COLUMN IF EXISTS key_set_duration_seconds;
ALTER TABLE consent_row_filters DROP COLUMN IF EXISTS key_set_max_keys;
