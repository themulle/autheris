-- Governance schema step 0043 (SQL Server): single approval cleanup. Pending (never approved) caller attributes, service sets,
-- functional-right mappings, virtual filters and access profiles never granted anything and are discarded (fail closed);
-- the draft and pending-deletion columns of the caller attribute tables are dropped; the generations are bumped so caches reload.

DELETE FROM dbo.CALLER_ATTRIBUTES WHERE approved_by IS NULL;

-- batch

DELETE FROM dbo.CALLER_ATTRIBUTE_SERVICE_SETS WHERE approved_by IS NULL;

-- batch

DELETE FROM dbo.FUNCTIONAL_RIGHT_MAPPINGS WHERE approved_by IS NULL;

-- batch

DELETE FROM dbo.VIRTUAL_FILTERS WHERE status <> N'Active';

-- batch

DELETE FROM dbo.VIRTUAL_FILTER_ACCESS_PROFILES WHERE definition_json LIKE N'%"Status":1%' OR definition_json LIKE N'%"Status":2%';

-- batch

DECLARE @df sysname = (SELECT dc.name FROM sys.default_constraints dc INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id WHERE dc.parent_object_id = OBJECT_ID(N'dbo.CALLER_ATTRIBUTES') AND c.name = N'pending_deletion');
DECLARE @sql NVARCHAR(MAX) = N'ALTER TABLE dbo.CALLER_ATTRIBUTES DROP CONSTRAINT ' + QUOTENAME(@df);
IF @df IS NOT NULL EXEC(@sql);

-- batch

IF COL_LENGTH(N'dbo.CALLER_ATTRIBUTES', N'draft_definition_json') IS NOT NULL ALTER TABLE dbo.CALLER_ATTRIBUTES DROP COLUMN draft_definition_json;

-- batch

IF COL_LENGTH(N'dbo.CALLER_ATTRIBUTES', N'draft_definition_hash') IS NOT NULL ALTER TABLE dbo.CALLER_ATTRIBUTES DROP COLUMN draft_definition_hash;

-- batch

IF COL_LENGTH(N'dbo.CALLER_ATTRIBUTES', N'draft_created_by') IS NOT NULL ALTER TABLE dbo.CALLER_ATTRIBUTES DROP COLUMN draft_created_by;

-- batch

IF COL_LENGTH(N'dbo.CALLER_ATTRIBUTES', N'pending_deletion') IS NOT NULL ALTER TABLE dbo.CALLER_ATTRIBUTES DROP COLUMN pending_deletion;

-- batch

IF COL_LENGTH(N'dbo.CALLER_ATTRIBUTES', N'deletion_requested_by') IS NOT NULL ALTER TABLE dbo.CALLER_ATTRIBUTES DROP COLUMN deletion_requested_by;

-- batch

IF COL_LENGTH(N'dbo.CALLER_ATTRIBUTE_SERVICE_SETS', N'draft_values_json') IS NOT NULL ALTER TABLE dbo.CALLER_ATTRIBUTE_SERVICE_SETS DROP COLUMN draft_values_json;

-- batch

IF COL_LENGTH(N'dbo.CALLER_ATTRIBUTE_SERVICE_SETS', N'draft_created_by') IS NOT NULL ALTER TABLE dbo.CALLER_ATTRIBUTE_SERVICE_SETS DROP COLUMN draft_created_by;

-- batch

UPDATE dbo.CALLER_ATTRIBUTE_GENERATION SET generation = generation + 1 WHERE id = 1;

-- batch

UPDATE dbo.VIRTUAL_FILTER_GENERATION SET generation = generation + 1 WHERE id = 1;
