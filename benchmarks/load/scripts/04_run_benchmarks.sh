#!/usr/bin/env bash
# ==============================================================================
# 04_run_benchmarks.sh
# Runs the automated benchmark matrix across all GraphQL gateways
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BENCH_DIR="$(dirname "$SCRIPT_DIR")"
RESULTS_DIR="${BENCH_DIR}/results"
K6_SCRIPT="${BENCH_DIR}/configs/k6/load_test.js"

DURATION="${DURATION:-30s}"
RPS="${RPS:-1000}"
VUS="${VUS:-50}"
SCENARIO="${SCENARIO:-rps}" # 'rps', 'vus', 'ramp'

mkdir -p "${RESULTS_DIR}"
TIMESTAMP=$(date +"%Y%m%d_%H%M%S")

echo "================================================================================"
echo " [Step 4/5] Executing GraphQL Gateway Benchmarks"
echo " Duration:    ${DURATION}"
echo " Strategy:    ${SCENARIO} (Target RPS: ${RPS}, VUs: ${VUS})"
echo " Output Dir:  ${RESULTS_DIR}"
echo " Timestamp:   ${TIMESTAMP}"
echo "================================================================================"

# Gateway endpoints definition (default: localhost, or remote IP via GATEWAY_HOST)
GATEWAY_HOST="${GATEWAY_HOST:-localhost}"
declare -A GATEWAYS=(
  ["hasura"]="http://${GATEWAY_HOST}:8085/v1/graphql"
  ["apollo"]="http://${GATEWAY_HOST}:4000/"
  ["postgraphile"]="http://${GATEWAY_HOST}:5001/graphql"
  ["autheris"]="http://${GATEWAY_HOST}:5000/graphql"
)

# Custom headers per gateway
declare -A HEADERS=(
  ["hasura"]='{"X-Hasura-Admin-Secret":"my-secret"}'
  ["apollo"]='{}'
  ["postgraphile"]='{}'
  ["autheris"]='{"X-Test-User-Sid":"S-1-5-21-9999","GraphQL-Preflight":"1"}'
)

# Query types to test
QUERY_TYPES=("pk" "filter" "join" "deep")

# Find k6 binary
K6_BIN="k6"
if ! command -v k6 &> /dev/null; then
    if [ -f "/root/lis-git/graphql-bench/app/queries/bin/k6/k6" ]; then
        K6_BIN="/root/lis-git/graphql-bench/app/queries/bin/k6/k6"
    else
        echo "ERROR: k6 binary not found!"
        exit 1
    fi
fi

for gw_name in "${!GATEWAYS[@]}"; do
    gw_url="${GATEWAYS[$gw_name]}"
    gw_headers="${HEADERS[$gw_name]}"

    echo ""
    echo "========================================================================"
    echo " Benchmarking Gateway: ${gw_name^^} (${gw_url})"
    echo "========================================================================"

    # Check if endpoint responds
    if ! curl -s -f -o /dev/null -X POST "${gw_url}" \
        -H "Content-Type: application/json" \
        -H "GraphQL-Preflight: 1" \
        -H "X-Test-User-Sid: S-1-5-21-9999" \
        -H "X-Hasura-Admin-Secret: REDACTED_HISTORICAL_BENCHMARK_SECRET" \
        -d '{"query":"query { __typename }"}' 2>/dev/null; then
        echo " [WARN] Gateway ${gw_name} is not responding at ${gw_url}. Skipping."
        continue
    fi

    for q_type in "${QUERY_TYPES[@]}"; do
        # For autheris, adjust query if standard Chinook schema isn't natively bound
        current_query_type="${q_type}"
        if [ "$gw_name" == "autheris" ] || [ "$gw_name" == "gqlgateway" ]; then
            if [ "$q_type" == "pk" ]; then
                current_query_type="gql_table"
            fi
        fi

        RESULT_FILE="${RESULTS_DIR}/${gw_name}_${q_type}_${TIMESTAMP}.json"

        echo ">>> Running Test: Gateway=[${gw_name}] Query=[${q_type}] Strategy=[${SCENARIO}]..."

        TARGET_URL="${gw_url}" \
        SCENARIO_TYPE="${SCENARIO}" \
        TEST_DURATION="${DURATION}" \
        TARGET_RPS="${RPS}" \
        TARGET_VUS="${VUS}" \
        QUERY_TYPE="${current_query_type}" \
        CUSTOM_HEADERS_JSON="${gw_headers}" \
        SUMMARY_FILE="${RESULT_FILE}" \
        "${K6_BIN}" run --quiet "${K6_SCRIPT}" || true

        echo "    Saved summary to: ${RESULT_FILE}"
        sleep 2
    done
done

echo ""
echo "================================================================================"
echo " Benchmarks Completed! Generating Consolidated Report..."
echo "================================================================================"
python3 "${SCRIPT_DIR}/05_analyze_results.py" --timestamp "${TIMESTAMP}"
