-- phase3: MIG-0015
-- Add catalog change_version watermark and index to POLICY_EPOCHS for delta snapshot reloads.

IF COL_LENGTH(N'dbo.POLICY_EPOCHS', N'change_version') IS NULL
ALTER TABLE dbo.POLICY_EPOCHS ADD change_version BIGINT NOT NULL CONSTRAINT DF_POLICY_EPOCHS_change_version DEFAULT 0;
-- batch
UPDATE dbo.POLICY_EPOCHS
SET change_version = COALESCE((SELECT structure_version + content_version FROM dbo.CATALOG_STATE WHERE id = 1), 1)
WHERE change_version <= 0;
-- batch
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POLICY_EPOCHS_CHANGE_VERSION' AND object_id = OBJECT_ID(N'dbo.POLICY_EPOCHS'))
CREATE INDEX IX_POLICY_EPOCHS_CHANGE_VERSION ON dbo.POLICY_EPOCHS (change_version);
