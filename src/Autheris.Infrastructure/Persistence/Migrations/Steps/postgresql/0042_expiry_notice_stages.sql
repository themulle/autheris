-- phase3: MIG-0042
-- Governance schema step 0042 (PostgreSQL): staged expiry reminders. Replaces the single-shot expiry notice timestamp with a
-- reminder stage counter and drops the per-route notice lead time (reminder lead times come from the tenant access policy).

ALTER TABLE CONSENTS ADD COLUMN IF NOT EXISTS expiry_notice_stage INT NOT NULL DEFAULT 0;
-- A notice that was already sent counts as stage 1 so the first reminder is not sent a second time. Reading the row as JSON keeps the
-- statement valid (and a no-op) when the old column is already gone.
UPDATE CONSENTS SET expiry_notice_stage = 1 WHERE expiry_notice_stage = 0 AND (to_jsonb(CONSENTS) ->> 'expiry_notice_sent_at') IS NOT NULL;
ALTER TABLE CONSENTS DROP COLUMN IF EXISTS expiry_notice_sent_at;
ALTER TABLE CONSENT_WORKFLOW_ROUTES DROP COLUMN IF EXISTS expiry_notice_lead_hours;
