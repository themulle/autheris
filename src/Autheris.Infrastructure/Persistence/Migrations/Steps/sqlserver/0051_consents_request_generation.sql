-- Governance schema step 0051 (SQL Server): consent request uniqueness per item and renewal generation (issue #1187).
-- Invariant: per consent request at most one consent exists per profile item and renewal generation; distinct items and successive
-- renewals are allowed. The OpenMetadata sync marker is shared by many sync consents and stays excluded. The new indexes are created
-- first and the superseded request-only index is dropped last, so no window is unprotected.

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_consents_request_generation' AND object_id = OBJECT_ID(N'dbo.CONSENTS'))
    CREATE UNIQUE INDEX ux_consents_request_generation ON dbo.CONSENTS (consent_request_id, renewal_count)
        WHERE consent_request_id IS NOT NULL AND consent_request_id <> '0e3d5c1a-7b2f-4c8e-9a61-5f0d2b7c4e19' AND profile_item_no IS NULL;
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_consents_request_item_generation' AND object_id = OBJECT_ID(N'dbo.CONSENTS'))
    CREATE UNIQUE INDEX ux_consents_request_item_generation ON dbo.CONSENTS (consent_request_id, profile_item_no, renewal_count)
        WHERE consent_request_id IS NOT NULL AND consent_request_id <> '0e3d5c1a-7b2f-4c8e-9a61-5f0d2b7c4e19' AND profile_item_no IS NOT NULL;
-- batch
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_consents_request' AND object_id = OBJECT_ID(N'dbo.CONSENTS'))
    DROP INDEX ux_consents_request ON dbo.CONSENTS;
