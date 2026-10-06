#!/usr/bin/env bash
# ==============================================================================
# 02_init_database.sh
# Starts PostgreSQL and seeds/verifies the Chinook benchmark dataset
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BENCH_DIR="$(dirname "$SCRIPT_DIR")"
DOCKER_COMPOSE_FILE="${BENCH_DIR}/docker/docker-compose.yml"
# Per-run random credentials; DB_BIND_IP selects the interface Postgres is published on
# (default 127.0.0.1; the multi-host orchestrator sets the private IP).
. "$(dirname "${BASH_SOURCE[0]}")/lib_secrets.sh"

echo "================================================================================"
echo " [Step 2/5] Initializing Chinook Database on PostgreSQL"
echo "================================================================================"

echo "Starting PostgreSQL container..."
docker compose -f "${DOCKER_COMPOSE_FILE}" up -d postgres

echo "Waiting for PostgreSQL to be healthy..."
until docker compose -f "${DOCKER_COMPOSE_FILE}" exec -T postgres pg_isready -U postgres > /dev/null 2>&1; do
    echo "  Waiting for Postgres..."
    sleep 2
done

echo "Verifying database tables and row counts..."
docker compose -f "${DOCKER_COMPOSE_FILE}" exec -T postgres psql -U postgres -d postgres -c "
  SELECT 'albums' AS table_name, count(*) AS row_count FROM albums
  UNION ALL
  SELECT 'artists', count(*) FROM artists
  UNION ALL
  SELECT 'tracks', count(*) FROM tracks
  UNION ALL
  SELECT 'invoices', count(*) FROM invoices
  UNION ALL
  SELECT 'customers', count(*) FROM customers;
"

echo "Optimizing query planner statistics (ANALYZE)..."
docker compose -f "${DOCKER_COMPOSE_FILE}" exec -T postgres psql -U postgres -d postgres -c "VACUUM ANALYZE;"

echo "Database initialized and ready for high-load benchmarking!"
