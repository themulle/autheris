-- phase3: MIG-0045
-- Governance schema step 0045 (SQL Server): drop the per-table ownership model (#1009, D13). Per-table owners and their delegations
-- are not converted: Data Governance assigns owner groups to containers. Owner grants (grant source OWNER) end with the model and are
-- revoked (fail closed); the policy epochs are bumped so no cache keeps serving them. Forward-only and idempotent.

UPDATE dbo.CONSENTS SET is_revoked = 1, revoked_by_sid = N'system:migration-0045', revoked_at = FORMAT(SYSUTCDATETIME(), N'yyyy-MM-ddTHH:mm:ssZ'), revoke_reason = N'D13_TABLE_OWNERSHIP_REMOVED' WHERE grant_source = N'OWNER' AND is_revoked = 0;

-- batch

IF OBJECT_ID(N'dbo.DATA_OWNER_DELEGATIONS', N'U') IS NOT NULL DROP TABLE dbo.DATA_OWNER_DELEGATIONS;

-- batch

IF OBJECT_ID(N'dbo.TABLE_OWNERS', N'U') IS NOT NULL DROP TABLE dbo.TABLE_OWNERS;

-- batch

IF OBJECT_ID(N'dbo.DATA_OWNERS', N'U') IS NOT NULL DROP TABLE dbo.DATA_OWNERS;

-- batch

UPDATE dbo.GOVERNANCE_EPOCHS SET epoch = epoch + 1, updated_at = SYSUTCDATETIME();

-- batch

UPDATE dbo.POLICY_EPOCHS SET epoch = epoch + 1;
