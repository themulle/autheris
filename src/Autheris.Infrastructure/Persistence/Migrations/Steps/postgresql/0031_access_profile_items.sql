-- phase3: MIG-0031
-- Governance schema step 0031 (PostgreSQL): move the profile-wide masking mode and the target table entries of an access profile into
-- item rows (WP-SIMPL-52 follow-up b). ACCESS_PROFILE_ITEMS holds one row per table entry (verbatim text, 1-based first-occurrence order,
-- duplicates collapsed); ACCESS_PROFILE_CLASSIFICATION_ACCESS holds the masking mode as the PII level (Strict 0 DENY, Default 1 MASK, Unmasked 2 CLEAR).
-- Backfill is fail closed: a masking mode that is not Default, Unmasked or Strict, a target_tables value that is not a JSON array, and an entry that
-- is not a string, is blank or is longer than 512 characters abort the step with the first affected profile id only (never the entry text), nothing is
-- changed and no default is invented. The JSON checks need PostgreSQL 16 or later (IS JSON), so a malformed value never raises a text-echoing cast error.
-- Then the two columns are dropped. The step runs once, in one transaction (a failed run changes nothing).
-- Rolling upgrade: all nodes must run this build before the step applies. Older builds read and write the dropped masking_mode and target_tables
-- columns and would fail with an error (fail closed) once the step ran, so no older node may be left running. No dual write, no read fallback to the old columns.

CREATE TABLE IF NOT EXISTS access_profile_items (
    tenant_id VARCHAR(64) NOT NULL,
    profile_id VARCHAR(128) NOT NULL,
    item_no INTEGER NOT NULL,
    object_ref VARCHAR(512) NOT NULL,
    CONSTRAINT pk_access_profile_items PRIMARY KEY (tenant_id, profile_id, item_no),
    CONSTRAINT fk_access_profile_items_profile FOREIGN KEY (tenant_id, profile_id)
        REFERENCES access_profiles (tenant_id, profile_id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_access_profile_items_ref
    ON access_profile_items (tenant_id, profile_id, object_ref);

CREATE TABLE IF NOT EXISTS access_profile_classification_access (
    tenant_id VARCHAR(64) NOT NULL,
    profile_id VARCHAR(128) NOT NULL,
    code VARCHAR(64) NOT NULL,
    access_level SMALLINT NOT NULL,
    CONSTRAINT pk_access_profile_classification_access PRIMARY KEY (tenant_id, profile_id, code),
    CONSTRAINT fk_access_profile_classification_access_profile FOREIGN KEY (tenant_id, profile_id)
        REFERENCES access_profiles (tenant_id, profile_id) ON DELETE CASCADE,
    CONSTRAINT ck_access_profile_classification_access_level CHECK (access_level IN (0, 1, 2))
);

SELECT CAST('MIG-0031 aborted - unknown masking mode in access profile: ' || f.profile_id AS INTEGER)
FROM (
    SELECT p.profile_id
    FROM access_profiles p
    WHERE p.masking_mode IS NULL OR LOWER(p.masking_mode) NOT IN ('default', 'unmasked', 'strict')
    ORDER BY p.tenant_id, p.profile_id
    LIMIT 1
) f;

SELECT CAST('MIG-0031 aborted - target tables are not a JSON array in access profile: ' || f.profile_id AS INTEGER)
FROM (
    SELECT p.profile_id
    FROM access_profiles p
    WHERE p.target_tables IS NULL OR NOT (p.target_tables IS JSON ARRAY)
    ORDER BY p.tenant_id, p.profile_id
    LIMIT 1
) f;

SELECT CAST('MIG-0031 aborted - blank, overlong or non-string target table entry in access profile: ' || f.profile_id AS INTEGER)
FROM (
    SELECT p.profile_id
    FROM access_profiles p
    CROSS JOIN LATERAL jsonb_array_elements(p.target_tables::jsonb) e
    WHERE jsonb_typeof(e.value) <> 'string'
       OR (e.value #>> '{}') ~ '^\s*$'
       OR LENGTH(e.value #>> '{}') > 512
    ORDER BY p.tenant_id, p.profile_id
    LIMIT 1
) f;

INSERT INTO access_profile_classification_access (tenant_id, profile_id, code, access_level)
SELECT
    p.tenant_id,
    p.profile_id,
    'PII',
    CASE LOWER(p.masking_mode)
        WHEN 'unmasked' THEN 2
        WHEN 'default' THEN 1
        ELSE 0
    END
FROM access_profiles p;

INSERT INTO access_profile_items (tenant_id, profile_id, item_no, object_ref)
SELECT
    t.tenant_id,
    t.profile_id,
    ROW_NUMBER() OVER (PARTITION BY t.tenant_id, t.profile_id ORDER BY t.ordinal),
    t.object_ref
FROM (
    SELECT
        p.tenant_id,
        p.profile_id,
        e.value #>> '{}' AS object_ref,
        MIN(e.ordinal) AS ordinal
    FROM access_profiles p
    CROSS JOIN LATERAL jsonb_array_elements(p.target_tables::jsonb) WITH ORDINALITY AS e(value, ordinal)
    GROUP BY p.tenant_id, p.profile_id, e.value #>> '{}'
) t;

ALTER TABLE access_profiles
    DROP COLUMN IF EXISTS masking_mode,
    DROP COLUMN IF EXISTS target_tables;
