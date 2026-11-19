-- phase3: MIG-0031
-- Governance schema step 0031 (SQL Server): move the profile-wide masking mode and the target table entries of an access profile into
-- item rows (WP-SIMPL-52 follow-up b). ACCESS_PROFILE_ITEMS holds one row per table entry (verbatim text, 1-based first-occurrence order,
-- duplicates collapsed); ACCESS_PROFILE_CLASSIFICATION_ACCESS holds the masking mode as the PII level (Strict 0 DENY, Default 1 MASK, Unmasked 2 CLEAR).
-- Backfill is fail closed: a masking mode that is not Default, Unmasked or Strict, a target_tables value that is not a JSON array, and an entry that
-- is not a string, is blank or is longer than 512 characters abort the step (THROW 51030) with the first affected profile id only (never the entry text),
-- nothing is changed and no default is invented. Then the default constraints and the two columns are dropped.
-- The step runs once, in one transaction (a failed run changes nothing).
-- Rolling upgrade: all nodes must run this build before the step applies. Older builds read and write the dropped masking_mode and target_tables
-- columns and would fail with an error (fail closed) once the step ran, so no older node may be left running. No dual write, no read fallback to the old columns.

IF OBJECT_ID(N'dbo.ACCESS_PROFILE_ITEMS', N'U') IS NULL
CREATE TABLE dbo.ACCESS_PROFILE_ITEMS (
    tenant_id NVARCHAR(64) NOT NULL,
    profile_id NVARCHAR(128) NOT NULL,
    item_no INT NOT NULL,
    object_ref NVARCHAR(512) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CONSTRAINT PK_ACCESS_PROFILE_ITEMS PRIMARY KEY (tenant_id, profile_id, item_no),
    CONSTRAINT FK_ACCESS_PROFILE_ITEMS_PROFILE FOREIGN KEY (tenant_id, profile_id) REFERENCES dbo.ACCESS_PROFILES (tenant_id, profile_id) ON DELETE CASCADE
);

-- batch

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_ACCESS_PROFILE_ITEMS_REF' AND object_id = OBJECT_ID(N'dbo.ACCESS_PROFILE_ITEMS'))
    CREATE UNIQUE INDEX UX_ACCESS_PROFILE_ITEMS_REF ON dbo.ACCESS_PROFILE_ITEMS (tenant_id, profile_id, object_ref);

-- batch

IF OBJECT_ID(N'dbo.ACCESS_PROFILE_CLASSIFICATION_ACCESS', N'U') IS NULL
CREATE TABLE dbo.ACCESS_PROFILE_CLASSIFICATION_ACCESS (
    tenant_id NVARCHAR(64) NOT NULL,
    profile_id NVARCHAR(128) NOT NULL,
    code NVARCHAR(64) NOT NULL,
    access_level SMALLINT NOT NULL,
    CONSTRAINT PK_ACCESS_PROFILE_CLASSIFICATION_ACCESS PRIMARY KEY (tenant_id, profile_id, code),
    CONSTRAINT FK_ACCESS_PROFILE_CLASSIFICATION_ACCESS_PROFILE FOREIGN KEY (tenant_id, profile_id) REFERENCES dbo.ACCESS_PROFILES (tenant_id, profile_id) ON DELETE CASCADE,
    CONSTRAINT CK_ACCESS_PROFILE_CLASSIFICATION_ACCESS_LEVEL CHECK (access_level IN (0, 1, 2))
);

-- batch

IF EXISTS (SELECT 1 FROM dbo.ACCESS_PROFILES WHERE masking_mode IS NULL OR LOWER(masking_mode) NOT IN (N'default', N'unmasked', N'strict'))
BEGIN
    DECLARE @firstMode NVARCHAR(128) = (SELECT TOP (1) profile_id FROM dbo.ACCESS_PROFILES WHERE masking_mode IS NULL OR LOWER(masking_mode) NOT IN (N'default', N'unmasked', N'strict') ORDER BY tenant_id, profile_id);
    DECLARE @modeMessage NVARCHAR(400) = N'MIG-0031 aborted - unknown masking mode in access profile: ' + @firstMode;
    THROW 51030, @modeMessage, 1;
END;

-- batch

IF EXISTS (SELECT 1 FROM dbo.ACCESS_PROFILES WHERE target_tables IS NULL OR ISJSON(target_tables) = 0 OR LEFT(LTRIM(REPLACE(REPLACE(REPLACE(target_tables, CHAR(9), N' '), CHAR(10), N' '), CHAR(13), N' ')), 1) <> N'[')
BEGIN
    DECLARE @firstJson NVARCHAR(128) = (SELECT TOP (1) profile_id FROM dbo.ACCESS_PROFILES WHERE target_tables IS NULL OR ISJSON(target_tables) = 0 OR LEFT(LTRIM(REPLACE(REPLACE(REPLACE(target_tables, CHAR(9), N' '), CHAR(10), N' '), CHAR(13), N' ')), 1) <> N'[' ORDER BY tenant_id, profile_id);
    DECLARE @jsonMessage NVARCHAR(400) = N'MIG-0031 aborted - target tables are not a JSON array in access profile: ' + @firstJson;
    THROW 51030, @jsonMessage, 1;
END;

-- batch

IF EXISTS (SELECT 1 FROM dbo.ACCESS_PROFILES p CROSS APPLY OPENJSON(p.target_tables) e WHERE e.type <> 1 OR LTRIM(RTRIM(REPLACE(REPLACE(REPLACE(e.value, CHAR(9), N''), CHAR(10), N''), CHAR(13), N''))) = N'' OR DATALENGTH(e.value) > 1024)
BEGIN
    DECLARE @firstEntry NVARCHAR(128) = (SELECT TOP (1) p.profile_id FROM dbo.ACCESS_PROFILES p CROSS APPLY OPENJSON(p.target_tables) e WHERE e.type <> 1 OR LTRIM(RTRIM(REPLACE(REPLACE(REPLACE(e.value, CHAR(9), N''), CHAR(10), N''), CHAR(13), N''))) = N'' OR DATALENGTH(e.value) > 1024 ORDER BY p.tenant_id, p.profile_id);
    DECLARE @entryMessage NVARCHAR(400) = N'MIG-0031 aborted - blank, overlong or non-string target table entry in access profile: ' + @firstEntry;
    THROW 51030, @entryMessage, 1;
END;

-- batch

INSERT INTO dbo.ACCESS_PROFILE_CLASSIFICATION_ACCESS (tenant_id, profile_id, code, access_level)
SELECT
    p.tenant_id,
    p.profile_id,
    N'PII',
    CASE LOWER(p.masking_mode)
        WHEN N'unmasked' THEN CAST(2 AS SMALLINT)
        WHEN N'default' THEN CAST(1 AS SMALLINT)
        ELSE CAST(0 AS SMALLINT)
    END
FROM dbo.ACCESS_PROFILES p;

-- batch

INSERT INTO dbo.ACCESS_PROFILE_ITEMS (tenant_id, profile_id, item_no, object_ref)
SELECT
    f.tenant_id,
    f.profile_id,
    ROW_NUMBER() OVER (PARTITION BY f.tenant_id, f.profile_id ORDER BY f.ordinal),
    f.object_ref
FROM (
    SELECT
        p.tenant_id,
        p.profile_id,
        e.value AS object_ref,
        CAST(e.[key] AS INT) AS ordinal,
        ROW_NUMBER() OVER (PARTITION BY p.tenant_id, p.profile_id, e.value COLLATE Latin1_General_100_BIN2 ORDER BY CAST(e.[key] AS INT)) AS first_occurrence
    FROM dbo.ACCESS_PROFILES p
    CROSS APPLY OPENJSON(p.target_tables) e
) f
WHERE f.first_occurrence = 1;

-- batch

-- The baseline default constraints are named, but the name is looked up so a differently named one is dropped too (the one allowed dynamic form, see StepSqlLint).
DECLARE @df sysname = (SELECT dc.name FROM sys.default_constraints dc INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id WHERE dc.parent_object_id = OBJECT_ID(N'dbo.ACCESS_PROFILES') AND c.name = N'masking_mode');
DECLARE @sql NVARCHAR(MAX) = N'ALTER TABLE dbo.ACCESS_PROFILES DROP CONSTRAINT ' + QUOTENAME(@df);
IF @df IS NOT NULL EXEC(@sql);

-- batch

DECLARE @df sysname = (SELECT dc.name FROM sys.default_constraints dc INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id WHERE dc.parent_object_id = OBJECT_ID(N'dbo.ACCESS_PROFILES') AND c.name = N'target_tables');
DECLARE @sql NVARCHAR(MAX) = N'ALTER TABLE dbo.ACCESS_PROFILES DROP CONSTRAINT ' + QUOTENAME(@df);
IF @df IS NOT NULL EXEC(@sql);

-- batch

IF COL_LENGTH('dbo.ACCESS_PROFILES', 'masking_mode') IS NOT NULL
    ALTER TABLE dbo.ACCESS_PROFILES DROP COLUMN masking_mode;

-- batch

IF COL_LENGTH('dbo.ACCESS_PROFILES', 'target_tables') IS NOT NULL
    ALTER TABLE dbo.ACCESS_PROFILES DROP COLUMN target_tables;
