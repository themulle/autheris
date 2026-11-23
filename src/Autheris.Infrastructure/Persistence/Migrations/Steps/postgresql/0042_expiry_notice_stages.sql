-- phase3: MIG-0042
-- Governance schema step 0042 (PostgreSQL): staged expiry reminders. Replaces the single-shot expiry notice timestamp with a
-- reminder stage counter and drops the per-route notice lead time (reminder lead times come from the tenant access policy).

ALTER TABLE CONSENTS ADD COLUMN IF NOT EXISTS expiry_notice_stage INT NOT NULL DEFAULT 0;
ALTER TABLE CONSENTS DROP COLUMN IF EXISTS expiry_notice_sent_at;
ALTER TABLE CONSENT_WORKFLOW_ROUTES DROP COLUMN IF EXISTS expiry_notice_lead_hours;
