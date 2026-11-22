-- Discovery artifact provenance, composite key (table_id, provenance), and fail-closed import constraint.
ALTER TABLE CATALOG_DISCOVERY_ARTIFACTS
    ADD COLUMN IF NOT EXISTS provenance VARCHAR(16) NOT NULL DEFAULT 'UNKNOWN';

ALTER TABLE CATALOG_DISCOVERY_ARTIFACTS
    DROP CONSTRAINT IF EXISTS catalog_discovery_artifacts_pkey;

ALTER TABLE CATALOG_DISCOVERY_ARTIFACTS
    DROP CONSTRAINT IF EXISTS pk_catalog_discovery_artifacts;

ALTER TABLE CATALOG_DISCOVERY_ARTIFACTS
    ADD CONSTRAINT pk_catalog_discovery_artifacts PRIMARY KEY (table_id, provenance);

ALTER TABLE CATALOG_DISCOVERY_ARTIFACTS
    DROP CONSTRAINT IF EXISTS ck_catalog_discovery_artifacts_import_no_stats;

ALTER TABLE CATALOG_DISCOVERY_ARTIFACTS
    ADD CONSTRAINT ck_catalog_discovery_artifacts_import_no_stats
    CHECK (provenance <> 'IMPORT' OR statistics_json IS NULL);
