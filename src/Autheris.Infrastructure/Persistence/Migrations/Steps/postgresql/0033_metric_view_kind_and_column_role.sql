-- Governance schema step 0033 (PostgreSQL): add 'metric_view' to TABLES.object_kind, TABLES.metric_view_count_measure, and TABLE_COLUMNS.column_role ('dimension' or 'measure'). Forward-only and idempotent.
ALTER TABLE TABLES DROP CONSTRAINT IF EXISTS ck_tables_object_kind;
ALTER TABLE TABLES ADD CONSTRAINT ck_tables_object_kind CHECK (object_kind IN ('table', 'view', 'metric_view'));
ALTER TABLE TABLES ADD COLUMN IF NOT EXISTS metric_view_count_measure VARCHAR(128) NULL;
ALTER TABLE TABLE_COLUMNS ADD COLUMN IF NOT EXISTS column_role VARCHAR(16) NULL;
ALTER TABLE TABLE_COLUMNS DROP CONSTRAINT IF EXISTS ck_table_columns_column_role;
ALTER TABLE TABLE_COLUMNS ADD CONSTRAINT ck_table_columns_column_role CHECK (column_role IS NULL OR column_role IN ('dimension', 'measure'));