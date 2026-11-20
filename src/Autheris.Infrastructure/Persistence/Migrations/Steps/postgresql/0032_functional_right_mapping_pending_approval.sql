-- Governance schema step 0032 (PostgreSQL): make FUNCTIONAL_RIGHT_MAPPINGS approval columns nullable so pending proposals persist with NULL approved_by and approved_at. Additive and idempotent.
ALTER TABLE FUNCTIONAL_RIGHT_MAPPINGS
    ALTER COLUMN approved_by DROP NOT NULL,
    ALTER COLUMN approved_at DROP NOT NULL;
