-- phase3: MIG-0027
-- Governance schema step 0027 (SQL Server): drop the superseded consent request ticket column and the superseded
-- catalog visibility tier column (WP-SIMPL-52 PR 6c). The external ticket system was removed, and the discovery
-- visibility tier is neither read nor interpreted any more (catalog visibility derives from consents and tiers).
-- Idempotent: the ticket index and both columns are dropped only when they exist.
-- All nodes must run this build before the step applies: older builds still write the dropped columns.

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_consent_requests_ticket' AND object_id = OBJECT_ID(N'dbo.CONSENT_REQUESTS'))
    DROP INDEX ux_consent_requests_ticket ON dbo.CONSENT_REQUESTS;

-- batch

IF COL_LENGTH('dbo.CONSENT_REQUESTS', 'itsm_ticket_id') IS NOT NULL
    ALTER TABLE dbo.CONSENT_REQUESTS DROP COLUMN itsm_ticket_id;

-- batch

IF COL_LENGTH('dbo.TABLE_CATALOG_SETTINGS', 'visibility') IS NOT NULL
    ALTER TABLE dbo.TABLE_CATALOG_SETTINGS DROP COLUMN visibility;
