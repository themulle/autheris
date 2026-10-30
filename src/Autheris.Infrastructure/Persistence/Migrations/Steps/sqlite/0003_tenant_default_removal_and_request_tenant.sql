-- WP-DT-2b (default tenant), SQLite decision: SQLite cannot drop a column default or add NOT NULL without rebuilding the table, and the append-only
-- audit table is never rebuilt. The dead defaults and the nullable CONSENT_REQUESTS.tenant_id therefore stay (the code always writes an explicit tenant).
-- The step still fails closed on a NULL request tenant, like PostgreSQL and SQL Server: abs of the smallest integer raises "integer overflow". No backfill.
-- The overflow is the deliberate fail-closed signal for a NULL CONSENT_REQUESTS.tenant_id, not a defect. The operator log detail therefore reads
-- "integer overflow", while the governed error code of the failed step is GOVERNANCE_MIGRATION_STEP_FAILED.
SELECT CASE WHEN EXISTS (SELECT 1 FROM CONSENT_REQUESTS WHERE tenant_id IS NULL) THEN abs(-9223372036854775807 - 1) ELSE 0 END;
