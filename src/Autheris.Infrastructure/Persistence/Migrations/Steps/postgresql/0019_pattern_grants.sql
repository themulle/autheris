-- phase3: MIG-0019

ALTER TABLE consents
    ALTER COLUMN table_id DROP NOT NULL,
    ADD COLUMN object_pattern VARCHAR(512) NULL,
    ADD COLUMN data_source VARCHAR(128) NULL,
    ADD COLUMN column_set_mode VARCHAR(16) NOT NULL DEFAULT 'EXPLICIT',
    ADD COLUMN workflow_mode VARCHAR(16) NOT NULL DEFAULT 'INTERNAL',
    ADD COLUMN profile_item_no INTEGER NULL;

ALTER TABLE consents
    ADD CONSTRAINT ck_consents_target CHECK ((table_id IS NOT NULL AND object_pattern IS NULL) OR (table_id IS NULL AND object_pattern IS NOT NULL)),
    ADD CONSTRAINT ck_consents_pattern_source CHECK (object_pattern IS NULL OR data_source IS NOT NULL),
    ADD CONSTRAINT ck_consents_column_set_mode CHECK (column_set_mode IN ('EXPLICIT', 'ALL_NON_HIDDEN')),
    ADD CONSTRAINT ck_consents_workflow_mode CHECK (workflow_mode IN ('INTERNAL', 'EXTERNAL'));

CREATE INDEX ix_consents_pattern
    ON consents (tenant_id, data_source)
    WHERE object_pattern IS NOT NULL;

CREATE TABLE IF NOT EXISTS consent_approving_owners (
    tenant_id VARCHAR(128) NOT NULL,
    consent_id VARCHAR(128) NOT NULL REFERENCES consents(id) ON DELETE CASCADE,
    owner_sid VARCHAR(256) NOT NULL,
    owner_sid_norm VARCHAR(256) NOT NULL,
    CONSTRAINT pk_consent_approving_owners PRIMARY KEY (tenant_id, consent_id, owner_sid_norm)
);
