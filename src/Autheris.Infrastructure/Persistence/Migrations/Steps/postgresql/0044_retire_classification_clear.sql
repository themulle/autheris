-- Governance schema step 0044 (PostgreSQL): retire classification clear grants (#1009 A-3). Who sees clear values is decided per
-- object by unmask grants; the classification taxonomy only says how to mask. The per-grant classification access table and the
-- clear-routing columns of the taxonomy (grantability, separate approval, approver mode, steward group, max clear days) are dropped.
-- Dropping a column also drops the check constraints that reference it. Forward-only and idempotent.

DROP TABLE IF EXISTS consent_classification_access;

ALTER TABLE data_classifications DROP COLUMN IF EXISTS clear_grantable;
ALTER TABLE data_classifications DROP COLUMN IF EXISTS separate_approval;
ALTER TABLE data_classifications DROP COLUMN IF EXISTS approver_mode;
ALTER TABLE data_classifications DROP COLUMN IF EXISTS steward_group_sid;
ALTER TABLE data_classifications DROP COLUMN IF EXISTS max_clear_days;
