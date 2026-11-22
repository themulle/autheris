-- phase3: MIG-0038
-- Governance schema step 0038 (SQL Server): retire legacy table sensitivity and four-eyes flags.

IF COL_LENGTH('dbo.TABLES', 'sensitivity') IS NOT NULL
    ALTER TABLE dbo.TABLES DROP COLUMN sensitivity;

-- batch

IF COL_LENGTH('dbo.TABLES', 'requires_four_eyes') IS NOT NULL
    ALTER TABLE dbo.TABLES DROP COLUMN requires_four_eyes;
