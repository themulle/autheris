#!/usr/bin/env bash
# ==============================================================================
# GqlGateway Benchmark & Simulation Suite Runner
# Rootless Podman Compose Orchestrator
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${SCRIPT_DIR}"

SCENARIO="${SCENARIO:-full}"
VUS="${VUS:-50}"
DURATION_STEADY="${DURATION_STEADY:-3m}"
SEED_ROW_COUNT="${SEED_ROW_COUNT:-200000}"
CHAOS="${CHAOS:-true}"
KEEP_RUNNING="${KEEP_RUNNING:-false}"

print_help() {
    echo "Usage: ./run-benchmark.sh [options]"
    echo ""
    echo "Options:"
    echo "  --scenario <name>      Scenario: minimal, lakehouse, enterprise, relational, full (default: full)"
    echo "  --vus <number>         Virtual concurrent users (default: 50)"
    echo "  --duration <time>      Steady state duration, e.g. 1m, 3m, 5m (default: 3m)"
    echo "  --seed-rows <number>   Rows seeded in postgres invoices table (default: 200000)"
    echo "  --no-chaos             Disable the Redis outage chaos test"
    echo "  --keep-running         Leave containers running after benchmark"
    echo "  --help                 Show this help message"
    echo ""
    echo "Memory Profiles by Scenario:"
    echo "  minimal                ~1.5 GB RAM (PostgreSQL + SQLite + Redis + Gateway)"
    echo "  lakehouse              ~1.8 GB RAM (Minimal + MinIO Iceberg S3 Storage)"
    echo "  enterprise             ~1.8 GB RAM (Minimal + OpenMetadata / ITSM / dbt Mock)"
    echo "  relational             ~2.0 GB RAM (Minimal + Azure SQL Edge CRM Engine)"
    echo "  full                   ~3.5 GB RAM (All Services & Extensions + Grafana/Prometheus)"
    exit 0
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --scenario) SCENARIO="$2"; shift 2 ;;
        --vus) VUS="$2"; shift 2 ;;
        --duration) DURATION_STEADY="$2"; shift 2 ;;
        --seed-rows) SEED_ROW_COUNT="$2"; shift 2 ;;
        --no-chaos) CHAOS="false"; shift 1 ;;
        --keep-running) KEEP_RUNNING="true"; shift 1 ;;
        --help) print_help ;;
        *) echo "Unknown option: $1"; print_help ;;
    esac
done

SCENARIO="$(echo "${SCENARIO}" | tr '[:upper:]' '[:lower:]')"
if [[ "${SCENARIO}" == "core" ]]; then SCENARIO="minimal"; fi

case "${SCENARIO}" in
    minimal)
        RAM_EST="~1.5 GB"
        SERVICES="postgres redis governance-seed gqlgateway-api reverse-proxy"
        ENABLE_LAKEHOUSE="false"
        ENABLE_ENTERPRISE="false"
        ENABLE_SQLSERVER="false"
        export COMPOSE_PROFILES=""
        ;;
    lakehouse)
        RAM_EST="~1.8 GB"
        SERVICES="postgres redis governance-seed minio lakehouse-seed gqlgateway-api reverse-proxy"
        ENABLE_LAKEHOUSE="true"
        ENABLE_ENTERPRISE="false"
        ENABLE_SQLSERVER="false"
        export COMPOSE_PROFILES="lakehouse"
        ;;
    enterprise)
        RAM_EST="~1.8 GB"
        SERVICES="postgres redis governance-seed mock-extensions gqlgateway-api reverse-proxy"
        ENABLE_LAKEHOUSE="false"
        ENABLE_ENTERPRISE="true"
        ENABLE_SQLSERVER="false"
        export COMPOSE_PROFILES="enterprise"
        ;;
    azure)
        RAM_EST="~1.7 GB"
        SERVICES="postgres redis governance-seed azurite mock-extensions gqlgateway-api reverse-proxy"
        ENABLE_LAKEHOUSE="true"
        ENABLE_ENTERPRISE="false"
        ENABLE_SQLSERVER="false"
        export COMPOSE_PROFILES="azure,enterprise"
        ;;
    relational)
        RAM_EST="~2.0 GB"
        SERVICES="postgres redis governance-seed sqlserver gqlgateway-api reverse-proxy"
        ENABLE_LAKEHOUSE="false"
        ENABLE_ENTERPRISE="false"
        ENABLE_SQLSERVER="true"
        export COMPOSE_PROFILES="relational"
        ;;
    openmetadata-real)
        RAM_EST="~4.5 GB"
        SERVICES="postgres redis governance-seed opensearch openmetadata-server gqlgateway-api reverse-proxy"
        ENABLE_LAKEHOUSE="false"
        ENABLE_ENTERPRISE="true"
        ENABLE_SQLSERVER="false"
        export COMPOSE_PROFILES=""
        EXTRA_COMPOSE_FILE="podman-compose.openmetadata.yaml"
        ;;
    full|*)
        SCENARIO="full"
        RAM_EST="~3.5 GB"
        SERVICES="postgres redis governance-seed sqlserver minio lakehouse-seed mock-extensions gqlgateway-api reverse-proxy prometheus grafana"
        ENABLE_LAKEHOUSE="true"
        ENABLE_ENTERPRISE="true"
        ENABLE_SQLSERVER="true"
        export COMPOSE_PROFILES="full,relational,lakehouse,enterprise,monitoring"
        ;;
esac

export BENCH_SCENARIO="${SCENARIO}"
export ENABLE_LAKEHOUSE ENABLE_ENTERPRISE ENABLE_SQLSERVER
export VUS DURATION_STEADY SEED_ROW_COUNT
export COMPOSE_FILE="${COMPOSE_FILE:-podman-compose.yaml}"
export PACING_SLEEP="${PACING_SLEEP:-false}"

log() { echo -e "\033[1;36m[GqlGateway Bench]\033[0m $1"; }
log_success() { echo -e "\033[1;32m[GqlGateway Bench]\033[0m $1"; }
log_err() { echo -e "\033[1;31m[GqlGateway Bench] ERROR:\033[0m $1" >&2; }

# Determine compose command
if command -v podman &>/dev/null && podman compose version &>/dev/null; then
    COMPOSE_BASE="podman compose"
elif command -v podman-compose &>/dev/null; then
    COMPOSE_BASE="podman-compose"
else
    log_err "Neither 'podman compose' nor 'podman-compose' was found."
    exit 1
fi

if [[ -n "${EXTRA_COMPOSE_FILE:-}" ]]; then
    COMPOSE="${COMPOSE_BASE} -f ${COMPOSE_FILE} -f ${EXTRA_COMPOSE_FILE}"
    log_success "Using orchestrator with modular compose: ${COMPOSE}"
else
    COMPOSE="${COMPOSE_BASE} -f ${COMPOSE_FILE}"
    log_success "Using orchestrator: ${COMPOSE}"
fi

# 1. Stage Source Code
./scripts/stage_sources.sh

mkdir -p results

cleanup() {
    if [[ "${KEEP_RUNNING}" != "true" ]]; then
        log "Cleaning up benchmark containers..."
        ${COMPOSE} down || true
        log_success "Teardown complete."
    fi
}
trap cleanup EXIT

# 2. Build and Launch Services for active scenario
log "Scenario: ${SCENARIO} (Estimated RAM: ${RAM_EST})"
log "Starting services (${SERVICES})..."
${COMPOSE} up -d --build ${SERVICES}

# 3. Wait for Health Checks
log "Waiting for services to become healthy..."
MAX_RETRIES=60
READY=false
for ((i=1; i<=MAX_RETRIES; i++)); do
    if curl -fsS http://localhost:8080/health/ready 2>/dev/null | grep -q '"Ready"'; then
        READY=true
        break
    fi
    echo -n "."
    sleep 2
done
echo ""

if [[ "${READY}" != "true" ]]; then
    log_err "Services failed to become ready in time. Container logs:"
    ${COMPOSE} logs gqlgateway-api
    exit 1
fi
log_success "All services healthy! (Reverse Proxy: :8080, Gateway: :5050)"

# 4. Run Extensions Integration Tests (Lakehouse, OData, OpenMetadata, ITSM, dbt)
log "Executing GqlGateway Extensions integration tests (Scenario: ${SCENARIO})..."
export TARGET_PROXY="http://localhost:8080"
if command -v python3 &>/dev/null; then
    python3 scripts/test_extensions.py || true
else
    podman run --rm --net=gateway-bench-net -e TARGET_PROXY="http://reverse-proxy:8080" -e BENCH_SCENARIO="${SCENARIO}" -e ENABLE_LAKEHOUSE="${ENABLE_LAKEHOUSE}" -e ENABLE_ENTERPRISE="${ENABLE_ENTERPRISE}" -v "$(pwd)/results:/results:z" -v "$(pwd)/scripts:/scripts:z" docker.io/library/python:3.12-alpine python /scripts/test_extensions.py || true
fi

# 5. Run k6 Load Generator
log "Executing k6 benchmark (${VUS} VUs, Steady State: ${DURATION_STEADY})..."
${COMPOSE} run --rm load-generator

# 5. Run Chaos Test
if [[ "${CHAOS}" == "true" ]]; then
    log "Executing Chaos Engineering Test (Redis Outage)..."
    if command -v python3 &>/dev/null; then
        python3 scripts/chaos_test.py || true
    else
        podman run --rm --net=gateway-bench-net -v "$(pwd)/results:/results:z" -v "$(pwd)/scripts:/scripts:z" docker.io/library/python:3.12-alpine python /scripts/chaos_test.py || true
    fi
fi

# 6. Generate Report
log "Generating Markdown Benchmark Report..."
if command -v python3 &>/dev/null; then
    python3 scripts/generate_report.py
else
    podman run --rm -v "$(pwd)/results:/results:z" -v "$(pwd)/scripts:/scripts:z" docker.io/library/python:3.12-alpine python /scripts/generate_report.py
fi

if [[ -f results/benchmark-report.md ]]; then
    log_success "REPORT GENERATED: results/benchmark-report.md"
    echo ""
    echo -e "\033[1;32m==========================================================================\033[0m"
    echo -e "\033[1;32m                   GQLGATEWAY BENCHMARK COMPLETE                          \033[0m"
    echo -e "\033[1;32m==========================================================================\033[0m"
    echo "Markdown Report:    results/benchmark-report.md"
    echo "Metrics JSON:       results/benchmark-summary.json"
    echo "Metrics CSV:        results/benchmark-summary.csv"
    echo "Grafana Dashboard:  http://localhost:3000 (User: admin / Pass: admin)"
    echo "Prometheus Metrics: http://localhost:9090"
    echo -e "\033[1;32m==========================================================================\033[0m"
fi

if [[ "${KEEP_RUNNING}" == "true" ]]; then
    log "Containers left running (--keep-running specified). Run '${COMPOSE} down' when done."
fi
