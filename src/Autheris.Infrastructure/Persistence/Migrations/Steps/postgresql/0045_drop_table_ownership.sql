-- phase3: MIG-0045
-- Governance schema step 0045 (PostgreSQL): drop the per-table ownership model (#1009, D13). Per-table owners and their delegations
-- are not converted: Data Governance assigns owner groups to containers. Owner grants (grant source OWNER) end with the model and are
-- revoked (fail closed); the policy epochs are bumped so no cache keeps serving them. Forward-only and idempotent.

UPDATE CONSENTS SET is_revoked = 1, revoked_by_sid = 'system:migration-0045', revoked_at = to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'), revoke_reason = 'D13_TABLE_OWNERSHIP_REMOVED' WHERE grant_source = 'OWNER' AND is_revoked = 0;

DROP TABLE IF EXISTS DATA_OWNER_DELEGATIONS;
DROP TABLE IF EXISTS TABLE_OWNERS;
DROP TABLE IF EXISTS DATA_OWNERS;

UPDATE governance_epochs SET epoch = epoch + 1, updated_at = CURRENT_TIMESTAMP;
UPDATE POLICY_EPOCHS SET epoch = epoch + 1;
