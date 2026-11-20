-- Governance schema step 0034 (PostgreSQL): add nullable key_set_max_keys to VIRTUAL_FILTERS. Forward-only and idempotent.
ALTER TABLE VIRTUAL_FILTERS ADD COLUMN IF NOT EXISTS key_set_max_keys INTEGER NULL;
