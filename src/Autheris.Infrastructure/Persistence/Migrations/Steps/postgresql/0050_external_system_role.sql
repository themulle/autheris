-- Governance schema step 0050 (PostgreSQL): role of an external workflow system (issue #1161). WORKFLOW is a system consent requests can be
-- routed to; OBSERVER receives every consent event of its tenant and reads requests, but is never a route target and never decides.
-- Existing systems keep WORKFLOW.

ALTER TABLE EXTERNAL_WORKFLOW_SYSTEMS ADD COLUMN IF NOT EXISTS system_role VARCHAR(16) NOT NULL DEFAULT 'WORKFLOW';

CREATE INDEX IF NOT EXISTS ix_external_workflow_systems_tenant_role
    ON EXTERNAL_WORKFLOW_SYSTEMS (tenant_id, system_role, enabled);
