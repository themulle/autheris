-- phase3: ADR020-MIG-0011
-- Step 0011: retire role grantees and pre-approval profile assignments, drop persisted clear-text functional rights. Forward-only.
ALTER TABLE ACCESS_PROFILE_ASSIGNMENTS ADD COLUMN IF NOT EXISTS deactivated_reason VARCHAR(64) NULL;
UPDATE CONSENTS SET is_revoked = 1, revoked_by_sid = 'system:migration-0011', revoked_at = to_char(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'), revoke_reason = 'ADR020_ROLE_GRANTEE_UNSUPPORTED' WHERE grantee_type = 'Role' AND effect <> 'Deny' AND is_revoked = 0;
UPDATE ACCESS_PROFILE_ASSIGNMENTS SET deactivated_reason = 'ADR020_ROLE_GRANTEE_UNSUPPORTED' WHERE subject_type = 'Role' AND deactivated_reason IS NULL;
UPDATE ACCESS_PROFILE_ASSIGNMENTS SET deactivated_reason = 'OD32_PROFILE_ASSIGNMENT_REQUIRES_APPROVAL' WHERE subject_type <> 'Role' AND deactivated_reason IS NULL;
DELETE FROM FUNCTIONAL_RIGHT_MAPPINGS WHERE functional_right IN ('Data.UnmaskSensitive', 'Data.ExportClearText');
