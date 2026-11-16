-- phase3: MIG-0019

DROP INDEX IF EXISTS ix_consents_tenant_table ON dbo.CONSENTS;

-- batch

ALTER TABLE dbo.CONSENTS ALTER COLUMN table_id NVARCHAR(128) NULL;

-- batch

CREATE INDEX ix_consents_tenant_table ON dbo.CONSENTS (tenant_id, table_id, is_revoked);

-- batch

ALTER TABLE dbo.CONSENTS ADD
    object_pattern NVARCHAR(512) NULL,
    data_source NVARCHAR(128) NULL,
    column_set_mode NVARCHAR(16) NOT NULL CONSTRAINT DF_CONSENTS_column_set_mode DEFAULT N'EXPLICIT',
    workflow_mode NVARCHAR(16) NOT NULL CONSTRAINT DF_CONSENTS_workflow_mode DEFAULT N'INTERNAL',
    profile_item_no INT NULL;

-- batch

ALTER TABLE dbo.CONSENTS ADD
    CONSTRAINT CK_CONSENTS_target CHECK ((table_id IS NOT NULL AND object_pattern IS NULL) OR (table_id IS NULL AND object_pattern IS NOT NULL)),
    CONSTRAINT CK_CONSENTS_pattern_source CHECK (object_pattern IS NULL OR data_source IS NOT NULL),
    CONSTRAINT CK_CONSENTS_column_set_mode CHECK (column_set_mode IN (N'EXPLICIT', N'ALL_NON_HIDDEN')),
    CONSTRAINT CK_CONSENTS_workflow_mode CHECK (workflow_mode IN (N'INTERNAL', N'EXTERNAL'));

-- batch

CREATE INDEX IX_CONSENTS_PATTERN
    ON dbo.CONSENTS (tenant_id, data_source)
    WHERE object_pattern IS NOT NULL;

-- batch

IF OBJECT_ID(N'dbo.CONSENT_APPROVING_OWNERS', N'U') IS NULL
CREATE TABLE dbo.CONSENT_APPROVING_OWNERS (
    tenant_id NVARCHAR(128) NOT NULL,
    consent_id NVARCHAR(128) NOT NULL,
    owner_sid NVARCHAR(256) NOT NULL,
    owner_sid_norm NVARCHAR(256) NOT NULL,
    CONSTRAINT PK_CONSENT_APPROVING_OWNERS PRIMARY KEY (tenant_id, consent_id, owner_sid_norm),
    CONSTRAINT FK_CONSENT_APPROVING_OWNERS_CONSENT FOREIGN KEY (consent_id) REFERENCES dbo.CONSENTS(id) ON DELETE CASCADE
);
