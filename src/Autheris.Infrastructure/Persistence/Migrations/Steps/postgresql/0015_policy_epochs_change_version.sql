-- phase3: MIG-0015
-- Add catalog change_version watermark and index to POLICY_EPOCHS for delta snapshot reloads.

ALTER TABLE POLICY_EPOCHS ADD COLUMN IF NOT EXISTS change_version BIGINT NOT NULL DEFAULT 0;

UPDATE POLICY_EPOCHS
SET change_version = COALESCE((SELECT structure_version + content_version FROM CATALOG_STATE WHERE id = 1), 1)
WHERE change_version <= 0;

CREATE INDEX IF NOT EXISTS ix_policy_epochs_change_version
    ON POLICY_EPOCHS (change_version);
