-- Governance schema step 0051 (PostgreSQL): consent request uniqueness per item and renewal generation (issue #1187).
-- Invariant: per consent request at most one consent exists per profile item and renewal generation; distinct items and successive
-- renewals are allowed. The OpenMetadata sync marker is shared by many sync consents and stays excluded. The new indexes are created
-- first and the superseded request-only index is dropped last, so no window is unprotected.

CREATE UNIQUE INDEX IF NOT EXISTS ux_consents_request_generation
    ON CONSENTS (consent_request_id, renewal_count)
    WHERE consent_request_id IS NOT NULL AND consent_request_id <> '0e3d5c1a-7b2f-4c8e-9a61-5f0d2b7c4e19' AND profile_item_no IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_consents_request_item_generation
    ON CONSENTS (consent_request_id, profile_item_no, renewal_count)
    WHERE consent_request_id IS NOT NULL AND consent_request_id <> '0e3d5c1a-7b2f-4c8e-9a61-5f0d2b7c4e19' AND profile_item_no IS NOT NULL;

DROP INDEX IF EXISTS ux_consents_request;
