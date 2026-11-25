-- Governance schema step 0044 (SQL Server): retire classification clear grants (#1009 A-3). Who sees clear values is decided per
-- object by unmask grants; the classification taxonomy only says how to mask. The per-grant classification access table and the
-- clear-routing columns of the taxonomy (grantability, separate approval, approver mode, steward group, max clear days) are dropped,
-- after the check constraints that reference them. Forward-only and idempotent.

IF OBJECT_ID(N'dbo.CONSENT_CLASSIFICATION_ACCESS', N'U') IS NOT NULL DROP TABLE dbo.CONSENT_CLASSIFICATION_ACCESS;

-- batch

IF OBJECT_ID(N'dbo.CK_DATA_CLASSIFICATIONS_STEWARD', N'C') IS NOT NULL ALTER TABLE dbo.DATA_CLASSIFICATIONS DROP CONSTRAINT CK_DATA_CLASSIFICATIONS_STEWARD;

-- batch

IF OBJECT_ID(N'dbo.CK_DATA_CLASSIFICATIONS_APPROVER_MODE', N'C') IS NOT NULL ALTER TABLE dbo.DATA_CLASSIFICATIONS DROP CONSTRAINT CK_DATA_CLASSIFICATIONS_APPROVER_MODE;

-- batch

IF COL_LENGTH(N'dbo.DATA_CLASSIFICATIONS', N'clear_grantable') IS NOT NULL ALTER TABLE dbo.DATA_CLASSIFICATIONS DROP COLUMN clear_grantable;

-- batch

IF COL_LENGTH(N'dbo.DATA_CLASSIFICATIONS', N'separate_approval') IS NOT NULL ALTER TABLE dbo.DATA_CLASSIFICATIONS DROP COLUMN separate_approval;

-- batch

IF COL_LENGTH(N'dbo.DATA_CLASSIFICATIONS', N'approver_mode') IS NOT NULL ALTER TABLE dbo.DATA_CLASSIFICATIONS DROP COLUMN approver_mode;

-- batch

IF COL_LENGTH(N'dbo.DATA_CLASSIFICATIONS', N'steward_group_sid') IS NOT NULL ALTER TABLE dbo.DATA_CLASSIFICATIONS DROP COLUMN steward_group_sid;

-- batch

IF COL_LENGTH(N'dbo.DATA_CLASSIFICATIONS', N'max_clear_days') IS NOT NULL ALTER TABLE dbo.DATA_CLASSIFICATIONS DROP COLUMN max_clear_days;
