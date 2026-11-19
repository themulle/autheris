-- phase3: MIG-0029
-- Governance schema step 0029 (SQL Server): remove the role grantee from the consent model (WP-SIMPL-52 follow-up a).
-- Pre-check: an active (unrevoked) role grantee in CONSENTS, in a virtual filter access profile (hard row policy) or in an
-- access profile assignment aborts the step with the row id, nothing is changed. No conversion and no automatic retirement:
-- turning a role into a group or user would change who is covered, and retiring a Deny would remove a restriction.
-- Remediation is an explicit operator action before the upgrade (revoke the row, or recreate it as a group or user Deny).
-- Pending consent requests for a role grantee carry no grant: the step sets them to the terminal REJECTED status (the request
-- table has no reason column, so the status is the whole record) so the request readers never meet the retired literal on an open request.
-- After the pre-check the step deletes the already revoked role rows (child rows first) and drops the two role columns.
-- Idempotent and re-runnable. All nodes must run this build before the step applies: older builds still write the dropped columns.

IF EXISTS (SELECT 1 FROM dbo.CONSENTS WHERE LOWER(grantee_type) = N'role' AND is_revoked = 0)
BEGIN
    DECLARE @firstConsent NVARCHAR(128) = (SELECT TOP (1) id FROM dbo.CONSENTS WHERE LOWER(grantee_type) = N'role' AND is_revoked = 0 ORDER BY id);
    DECLARE @consentMessage NVARCHAR(400) = N'MIG-0029 aborted - active role grantee in consent: ' + @firstConsent;
    THROW 51029, @consentMessage, 1;
END;

-- batch

IF EXISTS (SELECT 1 FROM dbo.VIRTUAL_FILTER_ACCESS_PROFILES WHERE definition_json LIKE N'%"GranteeType":3%')
BEGIN
    DECLARE @firstProfile NVARCHAR(128) = (SELECT TOP (1) id FROM dbo.VIRTUAL_FILTER_ACCESS_PROFILES WHERE definition_json LIKE N'%"GranteeType":3%' ORDER BY id);
    DECLARE @profileMessage NVARCHAR(400) = N'MIG-0029 aborted - active role grantee in virtual filter access profile: ' + @firstProfile;
    THROW 51029, @profileMessage, 1;
END;

-- batch

IF EXISTS (SELECT 1 FROM dbo.ACCESS_PROFILE_ASSIGNMENTS WHERE LOWER(subject_type) = N'role' AND deactivated_reason IS NULL)
BEGIN
    DECLARE @firstAssignment NVARCHAR(128) = (SELECT TOP (1) profile_id FROM dbo.ACCESS_PROFILE_ASSIGNMENTS WHERE LOWER(subject_type) = N'role' AND deactivated_reason IS NULL ORDER BY profile_id);
    DECLARE @assignmentMessage NVARCHAR(400) = N'MIG-0029 aborted - active role grantee in access profile assignment of profile: ' + @firstAssignment;
    THROW 51029, @assignmentMessage, 1;
END;

-- batch

DELETE FROM dbo.ACCESS_PROFILE_ASSIGNMENTS WHERE LOWER(subject_type) = N'role';

-- batch

UPDATE dbo.CONSENT_REQUESTS
SET status = N'REJECTED', version = version + 1
WHERE LOWER(requested_grantee_type) = N'role'
  AND status IN (N'PENDING', N'PENDING_SECOND_APPROVAL', N'PENDING_EXTERNAL_APPROVAL');

-- batch

DELETE FROM dbo.CONSENT_COLUMN_RULES
WHERE consent_id IN (SELECT id FROM dbo.CONSENTS WHERE LOWER(grantee_type) = N'role');

-- batch

DELETE FROM dbo.CONSENT_ROW_FILTERS
WHERE consent_id IN (SELECT id FROM dbo.CONSENTS WHERE LOWER(grantee_type) = N'role');

-- batch

DELETE FROM dbo.CONSENTS
WHERE LOWER(grantee_type) = N'role';

-- batch

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_consents_tenant_role' AND object_id = OBJECT_ID(N'dbo.CONSENTS'))
    DROP INDEX ix_consents_tenant_role ON dbo.CONSENTS;

-- batch

IF COL_LENGTH('dbo.CONSENTS', 'role_id') IS NOT NULL
    ALTER TABLE dbo.CONSENTS DROP COLUMN role_id;

-- batch

IF COL_LENGTH('dbo.CONSENTS', 'role_name') IS NOT NULL
    ALTER TABLE dbo.CONSENTS DROP COLUMN role_name;
