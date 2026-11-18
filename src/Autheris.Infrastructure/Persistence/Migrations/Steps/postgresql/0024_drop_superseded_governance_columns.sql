-- phase3: MIG-0024
-- Governance schema step 0024 (PostgreSQL): drop the retired role tables, the unused access policy tables and the
-- superseded subquery columns of consent_row_filters (WP-SIMPL-52 step 4, converted by WP-SIMPL-46 and WP-SIMPL-48).
-- Pre-check: an unrevoked role-based consent aborts the step with its id, nothing is dropped and no data is lost.
-- Kept: the key set columns of consent_row_filters (still read and written by the row filter evaluation store).

SELECT CAST('MIG-0024 aborted - unrevoked role-based consent: ' || c.id AS INTEGER)
FROM consents c
WHERE (c.role_id IS NOT NULL OR c.role_name IS NOT NULL) AND c.is_revoked = 0
LIMIT 1;

DROP TABLE IF EXISTS role_members;
DROP TABLE IF EXISTS roles;
DROP TABLE IF EXISTS access_policy_selectors;
DROP TABLE IF EXISTS access_policy_grantees;
DROP TABLE IF EXISTS access_policies;

ALTER TABLE consent_row_filters
    DROP COLUMN IF EXISTS dependent_table,
    DROP COLUMN IF EXISTS dependent_table_alias,
    DROP COLUMN IF EXISTS foreign_key_column,
    DROP COLUMN IF EXISTS primary_key_column,
    DROP COLUMN IF EXISTS subquery_predicate_json,
    DROP COLUMN IF EXISTS target_temporal_column,
    DROP COLUMN IF EXISTS dependent_valid_from_column,
    DROP COLUMN IF EXISTS dependent_valid_to_column,
    DROP COLUMN IF EXISTS target_table_alias,
    DROP COLUMN IF EXISTS additional_hops_json;
