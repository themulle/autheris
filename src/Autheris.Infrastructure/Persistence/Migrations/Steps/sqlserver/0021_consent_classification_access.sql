-- phase3: MIG-0021

IF OBJECT_ID(N'dbo.CONSENT_CLASSIFICATION_ACCESS', N'U') IS NULL
CREATE TABLE dbo.CONSENT_CLASSIFICATION_ACCESS (
    tenant_id NVARCHAR(128) NOT NULL,
    consent_id NVARCHAR(128) NOT NULL,
    code NVARCHAR(64) NOT NULL,
    access_level SMALLINT NOT NULL,
    valid_until DATETIMEOFFSET NULL,
    approved_by_sid NVARCHAR(256) NULL,
    approved_at DATETIMEOFFSET NOT NULL,
    CONSTRAINT PK_CONSENT_CLASSIFICATION_ACCESS PRIMARY KEY (tenant_id, consent_id, code),
    CONSTRAINT FK_CONSENT_CLASSIFICATION_ACCESS_CONSENT FOREIGN KEY (consent_id) REFERENCES dbo.CONSENTS (id) ON DELETE CASCADE,
    CONSTRAINT CK_CONSENT_CLASSIFICATION_ACCESS_LEVEL CHECK (access_level IN (0, 1, 2))
);

-- batch

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CONSENT_CLASSIFICATION_ACCESS_CONSENT' AND object_id = OBJECT_ID(N'dbo.CONSENT_CLASSIFICATION_ACCESS'))
    CREATE INDEX IX_CONSENT_CLASSIFICATION_ACCESS_CONSENT ON dbo.CONSENT_CLASSIFICATION_ACCESS (consent_id);

-- batch

INSERT INTO dbo.CONSENT_CLASSIFICATION_ACCESS (tenant_id, consent_id, code, access_level, valid_until, approved_by_sid, approved_at)
SELECT
    c.tenant_id,
    c.id,
    N'PII',
    CASE
        WHEN EXISTS (
            SELECT 1
            FROM dbo.CONSENT_COLUMN_RULES r
            INNER JOIN dbo.COLUMN_CLASSIFICATIONS cc
                ON cc.table_column_id = r.table_column_id
               AND cc.code = N'PII'
               AND (cc.scope = N'*' OR cc.scope = c.tenant_id)
            WHERE r.consent_id = c.id
              AND r.access_level = 2
        ) THEN CAST(2 AS SMALLINT)
        WHEN c.object_pattern IS NULL
             AND NOT EXISTS (SELECT 1 FROM dbo.CONSENT_COLUMN_RULES r0 WHERE r0.consent_id = c.id) THEN CAST(2 AS SMALLINT)
        ELSE CAST(1 AS SMALLINT)
    END,
    NULL,
    NULL,
    SYSUTCDATETIME()
FROM dbo.CONSENTS c
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.CONSENT_CLASSIFICATION_ACCESS existing
    WHERE existing.tenant_id = c.tenant_id
      AND existing.consent_id = c.id
      AND existing.code = N'PII'
);
