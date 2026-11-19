-- phase3: MIG-0029
-- Governance schema step 0029 (PostgreSQL): remove the role grantee from the consent model (WP-SIMPL-52 follow-up a).
-- Pre-check: an active (unrevoked) role grantee in consents, in a virtual filter access profile (hard row policy) or in an
-- access profile assignment aborts the step with the row id, nothing is changed. No conversion and no automatic retirement:
-- turning a role into a group or user would change who is covered, and retiring a Deny would remove a restriction.
-- Remediation is an explicit operator action before the upgrade (revoke the row, or recreate it as a group or user Deny).
-- After the pre-check the step deletes the already revoked role rows (child rows first) and drops the two role columns.
-- Idempotent and re-runnable. All nodes must run this build before the step applies: older builds still write the dropped columns.

SELECT CAST('MIG-0029 aborted - active role grantee in consent: ' || c.id AS INTEGER)
FROM consents c
WHERE LOWER(c.grantee_type) = 'role' AND c.is_revoked = 0
ORDER BY c.id
LIMIT 1;

SELECT CAST('MIG-0029 aborted - active role grantee in virtual filter access profile: ' || p.id AS INTEGER)
FROM virtual_filter_access_profiles p
WHERE p.definition_json LIKE '%"GranteeType":3%'
ORDER BY p.id
LIMIT 1;

SELECT CAST('MIG-0029 aborted - active role grantee in access profile assignment of profile: ' || a.profile_id AS INTEGER)
FROM access_profile_assignments a
WHERE LOWER(a.subject_type) = 'role' AND a.deactivated_reason IS NULL
ORDER BY a.profile_id
LIMIT 1;

DELETE FROM consent_column_rules
WHERE consent_id IN (SELECT c.id FROM consents c WHERE LOWER(c.grantee_type) = 'role');

DELETE FROM consent_row_filters
WHERE consent_id IN (SELECT c.id FROM consents c WHERE LOWER(c.grantee_type) = 'role');

DELETE FROM consents
WHERE LOWER(grantee_type) = 'role';

DELETE FROM access_profile_assignments WHERE LOWER(subject_type) = 'role';

DROP INDEX IF EXISTS ix_consents_tenant_role;

ALTER TABLE consents
    DROP COLUMN IF EXISTS role_id,
    DROP COLUMN IF EXISTS role_name;
