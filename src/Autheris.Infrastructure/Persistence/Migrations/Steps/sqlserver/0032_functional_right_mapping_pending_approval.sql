-- Governance schema step 0032 (SQL Server): make FUNCTIONAL_RIGHT_MAPPINGS approval columns nullable so pending proposals persist with NULL approved_by and approved_at. Additive and idempotent.
IF COLUMNPROPERTY(OBJECT_ID(N'dbo.FUNCTIONAL_RIGHT_MAPPINGS'), N'approved_by', N'AllowsNull') = 0
    ALTER TABLE dbo.FUNCTIONAL_RIGHT_MAPPINGS ALTER COLUMN approved_by NVARCHAR(128) NULL;

-- batch

IF COLUMNPROPERTY(OBJECT_ID(N'dbo.FUNCTIONAL_RIGHT_MAPPINGS'), N'approved_at', N'AllowsNull') = 0
    ALTER TABLE dbo.FUNCTIONAL_RIGHT_MAPPINGS ALTER COLUMN approved_at NVARCHAR(64) NULL;
