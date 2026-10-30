-- phase3: I388-SC-06
-- WP-DT-2b (default tenant): the code always writes an explicit tenant, so the dead column defaults go and CONSENT_REQUESTS.tenant_id becomes NOT NULL.
-- Fail closed: ALTER COLUMN ... NOT NULL aborts the step (and the transaction) when a row still has a NULL tenant. There is no backfill; the operator assigns the tenant first.
-- The filtered unique index on (tenant_id, itsm_ticket_id) blocks the nullability change, so it is dropped and recreated with the baseline definition.
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_consent_requests_ticket' AND object_id = OBJECT_ID(N'dbo.CONSENT_REQUESTS')) DROP INDEX ux_consent_requests_ticket ON dbo.CONSENT_REQUESTS;
-- batch
ALTER TABLE dbo.CONSENT_REQUESTS ALTER COLUMN tenant_id NVARCHAR(128) NOT NULL;
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_consent_requests_ticket' AND object_id = OBJECT_ID(N'dbo.CONSENT_REQUESTS')) CREATE UNIQUE INDEX ux_consent_requests_ticket ON dbo.CONSENT_REQUESTS (tenant_id, itsm_ticket_id) WHERE itsm_ticket_id IS NOT NULL;
-- batch
-- The baseline default constraints are unnamed (system generated names), so the name is looked up and dropped (the one allowed dynamic form, see StepSqlLint).
DECLARE @df sysname = (SELECT dc.name FROM sys.default_constraints dc INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id WHERE dc.parent_object_id = OBJECT_ID(N'dbo.CONSENTS') AND c.name = N'tenant_id');
IF @df IS NOT NULL EXEC(N'ALTER TABLE dbo.CONSENTS DROP CONSTRAINT ' + QUOTENAME(@df));
-- batch
DECLARE @df sysname = (SELECT dc.name FROM sys.default_constraints dc INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id WHERE dc.parent_object_id = OBJECT_ID(N'dbo.AUDIT_LOG_ENTRIES') AND c.name = N'tenant_id');
IF @df IS NOT NULL EXEC(N'ALTER TABLE dbo.AUDIT_LOG_ENTRIES DROP CONSTRAINT ' + QUOTENAME(@df));
