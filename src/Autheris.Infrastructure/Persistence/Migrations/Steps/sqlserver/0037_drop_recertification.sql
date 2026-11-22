-- phase3: MIG-0037
-- Governance schema step 0037 (SQL Server): drop the retired recertification tables. Forward-only and idempotent.

IF OBJECT_ID(N'dbo.RECERT_ITEMS', N'U') IS NOT NULL DROP TABLE dbo.RECERT_ITEMS;

-- batch

IF OBJECT_ID(N'dbo.RECERT_CAMPAIGNS', N'U') IS NOT NULL DROP TABLE dbo.RECERT_CAMPAIGNS;
