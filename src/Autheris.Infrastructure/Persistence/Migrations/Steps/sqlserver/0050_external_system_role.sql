-- Governance schema step 0050 (SQL Server): role of an external workflow system (issue #1161). WORKFLOW is a system consent requests can be
-- routed to; OBSERVER receives every consent event of its tenant and reads requests, but is never a route target and never decides.
-- Existing systems keep WORKFLOW.

IF COL_LENGTH(N'dbo.EXTERNAL_WORKFLOW_SYSTEMS', N'system_role') IS NULL
    ALTER TABLE dbo.EXTERNAL_WORKFLOW_SYSTEMS ADD system_role NVARCHAR(16) NOT NULL CONSTRAINT DF_EXTERNAL_WORKFLOW_SYSTEMS_system_role DEFAULT N'WORKFLOW';
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EXTERNAL_WORKFLOW_SYSTEMS_TENANT_ROLE' AND object_id = OBJECT_ID(N'dbo.EXTERNAL_WORKFLOW_SYSTEMS'))
    CREATE INDEX IX_EXTERNAL_WORKFLOW_SYSTEMS_TENANT_ROLE ON dbo.EXTERNAL_WORKFLOW_SYSTEMS (tenant_id, system_role, enabled);
