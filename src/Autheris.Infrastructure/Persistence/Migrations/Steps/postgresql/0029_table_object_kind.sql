-- Governance schema step 0029 (PostgreSQL): descriptive object kind of a catalog table (table or view). Additive and idempotent; existing rows read as table until the next sync.
ALTER TABLE TABLES ADD COLUMN IF NOT EXISTS object_kind VARCHAR(16) NOT NULL DEFAULT 'table' CONSTRAINT ck_tables_object_kind CHECK (object_kind IN ('table', 'view'));
