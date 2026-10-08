#!/usr/bin/env bash
# ==============================================================================
# High-performance data seeding script for PostgreSQL
# Generates realistic enterprise data with sensitive columns for masking tests
# ==============================================================================
set -euo pipefail

ROW_COUNT="${SEED_ROW_COUNT:-200000}"
ITEMS_COUNT="${SEED_ITEMS_COUNT:-50000}"

echo "[seed-data] Starting data generation: target ${ROW_COUNT} rows in invoices..."
START_TIME=$(date +%s)

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<EOSQL
BEGIN;

-- High-performance bulk insert using generate_series
INSERT INTO public.invoices (id, name, amount, email, iban, salary, department, vendor, status, created_at)
SELECT
    s.id,
    'Invoice #' || s.id || ' - ' || (ARRAY['General Supplies', 'Cloud Infrastructure', 'Consulting Services', 'Software License', 'Hardware Maintenance', 'Logistics Services'])[1 + (s.id % 6)],
    ROUND((10.0 + (s.id * 137.42) % 49990.0)::numeric, 2),
    'employee.' || s.id || '@' || (ARRAY['corp.local', 'enterprise.de', 'global-finance.com', 'partner.org'])[1 + (s.id % 4)],
    'DE8937040044' || LPAD(s.id::text, 10, '0'),
    ROUND((42000.0 + (s.id * 73.19) % 98000.0)::numeric, 2),
    (ARRAY['Finance', 'Sales', 'Engineering', 'HR', 'Legal', 'Marketing', 'Logistics'])[1 + (s.id % 7)],
    (ARRAY['Siemens AG', 'SAP SE', 'Deutsche Telekom AG', 'Robert Bosch GmbH', 'Allianz SE', 'BASF SE', 'BMW Group', 'Bayer AG'])[1 + (s.id % 8)],
    (ARRAY['APPROVED', 'PENDING', 'PAID', 'REJECTED', 'PROCESSING'])[1 + (s.id % 5)],
    CURRENT_TIMESTAMP - ((s.id % 730) || ' days')::interval
FROM generate_series(1, ${ROW_COUNT}) AS s(id);

-- Reset sequence to continue after the inserted max id
SELECT setval(pg_get_serial_sequence('public.invoices', 'id'), coalesce(max(id), 1)) FROM public.invoices;

-- Seed child items for DataLoader and relation tests (for the first N invoices)
INSERT INTO public.finance_items (id, parent_id, product_name, price, sensitive_note)
SELECT
    'item-' || s.id,
    ((1 + (s.id % (${ROW_COUNT} / 10 + 1))))::text,
    'Line Item #' || s.id || ' for ' || (ARRAY['CPU Compute Unit', 'Memory Expansion', 'Storage Volume', 'Security Audit Service', 'API Gateway License'])[1 + (s.id % 5)],
    ROUND((5.0 + (s.id * 17.83) % 495.0)::numeric, 2),
    'Confidential internal discount code: DSC-' || LPAD((s.id % 9999)::text, 4, '0')
FROM generate_series(1, ${ITEMS_COUNT}) AS s(id);

COMMIT;

-- Analyze tables to generate fresh planner statistics
ANALYZE public.invoices;
ANALYZE public.finance_items;
EOSQL

END_TIME=$(date +%s)
DURATION=$((END_TIME - START_TIME))
echo "[seed-data] Seeding completed successfully: ${ROW_COUNT} invoices and ${ITEMS_COUNT} items in ${DURATION} seconds."
