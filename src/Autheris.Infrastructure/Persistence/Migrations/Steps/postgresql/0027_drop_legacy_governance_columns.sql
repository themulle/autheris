-- phase3: MIG-0027
-- Governance schema step 0027 (PostgreSQL): drop the superseded consent request ticket column and the superseded
-- catalog visibility tier column (WP-SIMPL-52 PR 6c). The external ticket system was removed, and the discovery
-- visibility tier is neither read nor interpreted any more (catalog visibility derives from consents and tiers).
-- Idempotent: the ticket index and both columns are dropped only when they exist.
-- All nodes must run this build before the step applies: older builds still write the dropped columns.

DROP INDEX IF EXISTS ux_consent_requests_ticket;

ALTER TABLE consent_requests DROP COLUMN IF EXISTS itsm_ticket_id;

ALTER TABLE table_catalog_settings DROP COLUMN IF EXISTS visibility;
