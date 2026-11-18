-- phase3: MIG-0024
-- Governance schema step 0024 (SQL Server): drop the retired role tables, the unused access policy tables and the
-- superseded subquery columns of CONSENT_ROW_FILTERS (WP-SIMPL-52 step 4, converted by WP-SIMPL-46 and WP-SIMPL-48).
-- Pre-check: an unrevoked role-based consent aborts the step with its id, nothing is dropped and no data is lost.
-- Kept: the key set columns of CONSENT_ROW_FILTERS (still read and written by the row filter evaluation store).

IF EXISTS (SELECT 1 FROM dbo.CONSENTS WHERE (role_id IS NOT NULL OR role_name IS NOT NULL) AND is_revoked = 0)
BEGIN
    DECLARE @firstConsent NVARCHAR(128) = (SELECT TOP (1) id FROM dbo.CONSENTS WHERE (role_id IS NOT NULL OR role_name IS NOT NULL) AND is_revoked = 0 ORDER BY id);
    DECLARE @message NVARCHAR(400) = N'MIG-0024 aborted - unrevoked role-based consent: ' + @firstConsent;
    THROW 51024, @message, 1;
END;

-- batch

IF OBJECT_ID(N'dbo.ROLE_MEMBERS', N'U') IS NOT NULL DROP TABLE dbo.ROLE_MEMBERS;

-- batch

IF OBJECT_ID(N'dbo.ROLES', N'U') IS NOT NULL DROP TABLE dbo.ROLES;

-- batch

IF OBJECT_ID(N'dbo.ACCESS_POLICY_SELECTORS', N'U') IS NOT NULL DROP TABLE dbo.ACCESS_POLICY_SELECTORS;

-- batch

IF OBJECT_ID(N'dbo.ACCESS_POLICY_GRANTEES', N'U') IS NOT NULL DROP TABLE dbo.ACCESS_POLICY_GRANTEES;

-- batch

IF OBJECT_ID(N'dbo.ACCESS_POLICIES', N'U') IS NOT NULL DROP TABLE dbo.ACCESS_POLICIES;

-- batch

IF COL_LENGTH('dbo.CONSENT_ROW_FILTERS', 'dependent_table') IS NOT NULL
    ALTER TABLE dbo.CONSENT_ROW_FILTERS DROP COLUMN dependent_table;

-- batch

IF COL_LENGTH('dbo.CONSENT_ROW_FILTERS', 'dependent_table_alias') IS NOT NULL
    ALTER TABLE dbo.CONSENT_ROW_FILTERS DROP COLUMN dependent_table_alias;

-- batch

IF COL_LENGTH('dbo.CONSENT_ROW_FILTERS', 'foreign_key_column') IS NOT NULL
    ALTER TABLE dbo.CONSENT_ROW_FILTERS DROP COLUMN foreign_key_column;

-- batch

IF COL_LENGTH('dbo.CONSENT_ROW_FILTERS', 'primary_key_column') IS NOT NULL
    ALTER TABLE dbo.CONSENT_ROW_FILTERS DROP COLUMN primary_key_column;

-- batch

IF COL_LENGTH('dbo.CONSENT_ROW_FILTERS', 'subquery_predicate_json') IS NOT NULL
    ALTER TABLE dbo.CONSENT_ROW_FILTERS DROP COLUMN subquery_predicate_json;

-- batch

IF COL_LENGTH('dbo.CONSENT_ROW_FILTERS', 'target_temporal_column') IS NOT NULL
    ALTER TABLE dbo.CONSENT_ROW_FILTERS DROP COLUMN target_temporal_column;

-- batch

IF COL_LENGTH('dbo.CONSENT_ROW_FILTERS', 'dependent_valid_from_column') IS NOT NULL
    ALTER TABLE dbo.CONSENT_ROW_FILTERS DROP COLUMN dependent_valid_from_column;

-- batch

IF COL_LENGTH('dbo.CONSENT_ROW_FILTERS', 'dependent_valid_to_column') IS NOT NULL
    ALTER TABLE dbo.CONSENT_ROW_FILTERS DROP COLUMN dependent_valid_to_column;

-- batch

IF COL_LENGTH('dbo.CONSENT_ROW_FILTERS', 'target_table_alias') IS NOT NULL
    ALTER TABLE dbo.CONSENT_ROW_FILTERS DROP COLUMN target_table_alias;

-- batch

IF COL_LENGTH('dbo.CONSENT_ROW_FILTERS', 'additional_hops_json') IS NOT NULL
    ALTER TABLE dbo.CONSENT_ROW_FILTERS DROP COLUMN additional_hops_json;
