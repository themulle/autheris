-- Governance schema step 0043 (PostgreSQL): single approval cleanup. Pending (never approved) caller attributes, service sets,
-- functional-right mappings, virtual filters and access profiles never granted anything and are discarded (fail closed);
-- the draft and pending-deletion columns of the caller attribute tables are dropped; the generations are bumped so caches reload.

DELETE FROM CALLER_ATTRIBUTES WHERE approved_by IS NULL;

DELETE FROM CALLER_ATTRIBUTE_SERVICE_SETS WHERE approved_by IS NULL;

DELETE FROM FUNCTIONAL_RIGHT_MAPPINGS WHERE approved_by IS NULL;

DELETE FROM VIRTUAL_FILTERS WHERE status <> 'Active';

-- Only the top-level status of a profile counts (1 = pending approval, 2 = rejected): an active profile with a pending nested draft
-- stays. The definition is read as json (never jsonb, so an escape in another field never raises a value-echoing cast error); a value
-- that is not a JSON object is never treated as pending.
DELETE FROM VIRTUAL_FILTER_ACCESS_PROFILES
WHERE (CASE WHEN definition_json IS JSON OBJECT THEN definition_json::json ->> 'Status' END) IN ('1', '2');

ALTER TABLE CALLER_ATTRIBUTES DROP COLUMN IF EXISTS draft_definition_json;
ALTER TABLE CALLER_ATTRIBUTES DROP COLUMN IF EXISTS draft_definition_hash;
ALTER TABLE CALLER_ATTRIBUTES DROP COLUMN IF EXISTS draft_created_by;
ALTER TABLE CALLER_ATTRIBUTES DROP COLUMN IF EXISTS pending_deletion;
ALTER TABLE CALLER_ATTRIBUTES DROP COLUMN IF EXISTS deletion_requested_by;

ALTER TABLE CALLER_ATTRIBUTE_SERVICE_SETS DROP COLUMN IF EXISTS draft_values_json;
ALTER TABLE CALLER_ATTRIBUTE_SERVICE_SETS DROP COLUMN IF EXISTS draft_created_by;

UPDATE CALLER_ATTRIBUTE_GENERATION SET generation = generation + 1 WHERE id = 1;

UPDATE VIRTUAL_FILTER_GENERATION SET generation = generation + 1 WHERE id = 1;
