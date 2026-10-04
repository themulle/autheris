#!/usr/bin/env bash
# ==============================================================================
# 03_start_gateways.sh
# Builds, launches, configures, and warms up all GraphQL gateway targets
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BENCH_DIR="$(dirname "$SCRIPT_DIR")"
DOCKER_COMPOSE_FILE="${BENCH_DIR}/docker/docker-compose.yml"
METADATA_DIR="${BENCH_DIR}/configs/hasura-metadata"

echo "================================================================================"
echo " [Step 3/5] Starting & Configuring GraphQL Gateway Targets"
echo "================================================================================"

echo "Building and launching containers..."
docker compose -f "${DOCKER_COMPOSE_FILE}" up -d --build

echo "Waiting for services to become available..."

# Wait function
wait_for_url() {
    local url=$1
    local name=$2
    local max_retries=30
    local count=0
    echo -n "Waiting for ${name} (${url})..."
    until curl -s -f -o /dev/null "${url}" || [ $count -ge $max_retries ]; do
        echo -n "."
        sleep 2
        count=$((count+1))
    done
    if [ $count -ge $max_retries ]; then
        echo " FAILED (Timeout)"
        return 1
    else
        echo " READY"
        return 0
    fi
}

# 1. Hasura Health
wait_for_url "http://localhost:8085/healthz" "Hasura"

# Apply Hasura metadata tracking
echo "Tracking Chinook tables in Hasura..."
curl -s -X POST "http://localhost:8085/v1/metadata" \
     -H "Content-Type: application/json" \
     -H "X-Hasura-Admin-Secret: REDACTED_HISTORICAL_BENCHMARK_SECRET" \
     -d @"${METADATA_DIR}/psql_track_chinook_tables.json" > /dev/null || true

echo "Tracking Chinook relationships in Hasura..."
curl -s -X POST "http://localhost:8085/v1/metadata" \
     -H "Content-Type: application/json" \
     -H "X-Hasura-Admin-Secret: REDACTED_HISTORICAL_BENCHMARK_SECRET" \
     -d @"${METADATA_DIR}/psql_track_chinook_relationships.json" > /dev/null || true

# 2. Apollo Server
wait_for_url "http://localhost:4000" "Apollo Server"

# 3. PostGraphile
wait_for_url "http://localhost:5001/graphql" "PostGraphile"

# 4. GqlGateway
wait_for_url "http://localhost:5000/health" "GqlGateway"

echo ""
echo "================================================================================"
echo " Warming up JIT & Caches (Warmup Requests)"
echo "================================================================================"

WARMUP_QUERY='{"query":"query Warmup { albums_by_pk(id: 1) { id title } }"}'
GQL_WARMUP_QUERY='{"query":"query { table(domain: \"finance\", name: \"finance_table_1\", first: 1) { tableName totalCount } }"}'

echo "Warming up Hasura..."
for i in {1..20}; do
    curl -s -o /dev/null -X POST http://localhost:8085/v1/graphql \
         -H "Content-Type: application/json" \
         -H "X-Hasura-Admin-Secret: REDACTED_HISTORICAL_BENCHMARK_SECRET" \
         -d "$WARMUP_QUERY"
done

echo "Warming up Apollo Server..."
for i in {1..20}; do
    curl -s -o /dev/null -X POST http://localhost:4000/ \
         -H "Content-Type: application/json" \
         -d "$WARMUP_QUERY"
done

echo "Warming up GqlGateway..."
for i in {1..20}; do
    curl -s -o /dev/null -X POST http://localhost:5000/graphql \
         -H "Content-Type: application/json" \
         -H "X-Test-User-Sid: S-1-5-21-9999" \
         -H "GraphQL-Preflight: 1" \
         -d "$GQL_WARMUP_QUERY" || true
done

echo ""
echo "All GraphQL gateways are up, configured, and warmed up!"
echo "  - Hasura:       http://localhost:8085/v1/graphql"
echo "  - Apollo:       http://localhost:4000/"
echo "  - PostGraphile: http://localhost:5001/graphql"
echo "  - GqlGateway:   http://localhost:5000/graphql"
