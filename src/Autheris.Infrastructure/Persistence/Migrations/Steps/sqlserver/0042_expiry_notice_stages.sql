-- phase3: MIG-0042
-- Governance schema step 0042 (SQL Server): staged expiry reminders. Replaces the single-shot expiry notice timestamp with a
-- reminder stage counter and drops the per-route notice lead time (reminder lead times come from the tenant access policy).

IF COL_LENGTH(N'dbo.CONSENTS', N'expiry_notice_stage') IS NULL
    ALTER TABLE dbo.CONSENTS ADD expiry_notice_stage INT NOT NULL CONSTRAINT DF_CONSENTS_expiry_notice_stage DEFAULT 0;
-- batch
IF COL_LENGTH(N'dbo.CONSENTS', N'expiry_notice_sent_at') IS NOT NULL
    ALTER TABLE dbo.CONSENTS DROP COLUMN expiry_notice_sent_at;
-- batch
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = N'DF_CONSENT_WORKFLOW_ROUTES_expiry_notice_lead_hours')
    ALTER TABLE dbo.CONSENT_WORKFLOW_ROUTES DROP CONSTRAINT DF_CONSENT_WORKFLOW_ROUTES_expiry_notice_lead_hours;
-- batch
IF COL_LENGTH(N'dbo.CONSENT_WORKFLOW_ROUTES', N'expiry_notice_lead_hours') IS NOT NULL
    ALTER TABLE dbo.CONSENT_WORKFLOW_ROUTES DROP COLUMN expiry_notice_lead_hours;
