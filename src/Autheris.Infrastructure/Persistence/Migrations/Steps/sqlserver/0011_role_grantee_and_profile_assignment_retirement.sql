-- phase3: ADR020-MIG-0011
-- Step 0011: retire role grantees and pre-approval profile assignments, drop persisted clear-text functional rights. Forward-only.
IF COL_LENGTH(N'dbo.ACCESS_PROFILE_ASSIGNMENTS', N'deactivated_reason') IS NULL ALTER TABLE dbo.ACCESS_PROFILE_ASSIGNMENTS ADD deactivated_reason NVARCHAR(64) NULL;
-- batch
UPDATE dbo.CONSENTS SET is_revoked = 1, revoked_by_sid = N'system:migration-0011', revoked_at = FORMAT(SYSUTCDATETIME(), N'yyyy-MM-ddTHH:mm:ssZ'), revoke_reason = N'ADR020_ROLE_GRANTEE_UNSUPPORTED' WHERE grantee_type = N'Role' AND effect <> N'Deny' AND is_revoked = 0;
-- batch
UPDATE dbo.ACCESS_PROFILE_ASSIGNMENTS SET deactivated_reason = N'ADR020_ROLE_GRANTEE_UNSUPPORTED' WHERE subject_type = N'Role' AND deactivated_reason IS NULL;
-- batch
UPDATE dbo.ACCESS_PROFILE_ASSIGNMENTS SET deactivated_reason = N'OD32_PROFILE_ASSIGNMENT_REQUIRES_APPROVAL' WHERE subject_type <> N'Role' AND deactivated_reason IS NULL;
-- batch
DELETE FROM dbo.FUNCTIONAL_RIGHT_MAPPINGS WHERE functional_right IN (N'Data.UnmaskSensitive', N'Data.ExportClearText');
