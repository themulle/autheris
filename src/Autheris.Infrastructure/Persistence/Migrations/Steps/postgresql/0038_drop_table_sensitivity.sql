-- phase3: MIG-0038
-- Governance schema step 0038 (PostgreSQL): retire legacy table sensitivity and four-eyes flags.

ALTER TABLE tables DROP COLUMN IF EXISTS sensitivity;
ALTER TABLE tables DROP COLUMN IF EXISTS requires_four_eyes;
