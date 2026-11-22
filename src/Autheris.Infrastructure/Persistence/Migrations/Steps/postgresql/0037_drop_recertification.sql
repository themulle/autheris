-- phase3: MIG-0037
-- Governance schema step 0037 (PostgreSQL): drop the retired recertification tables. Forward-only and idempotent.

DROP TABLE IF EXISTS recert_items;
DROP TABLE IF EXISTS recert_campaigns;
