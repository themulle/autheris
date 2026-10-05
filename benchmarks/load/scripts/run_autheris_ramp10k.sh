#!/usr/bin/env bash
set -euo pipefail

GATEWAY_HOST="${1:-10.0.1.2}"
TARGET_URL="http://${GATEWAY_HOST}:5000/graphql"
RESULTS_DIR="/root/autheris/benchmarks/load/results"
SUMMARY_FILE="${RESULTS_DIR}/autheris_ramp_10000.json"
mkdir -p "${RESULTS_DIR}"

echo "================================================================================"
echo " Starting Ramp-Up Benchmark up to 10,000 RPS on Autheris (.NET 10)"
echo " Target URL: ${TARGET_URL}"
echo " Peak RPS:   10,000"
echo " Stages:     10s @ 1k -> 20s @ 3k -> 20s @ 6k -> 20s @ 10k -> 10s @ 1k"
echo " Duration:   80s total"
echo "================================================================================"

TARGET_URL="${TARGET_URL}" \
SCENARIO_TYPE="ramp" \
MAX_RAMP_RPS="10000" \
QUERY_TYPE="gql_table" \
CUSTOM_HEADERS_JSON='{"X-Test-User-Sid":"S-1-5-21-9999","GraphQL-Preflight":"1","X-Test-Tier":"Internal"}' \
SUMMARY_FILE="${SUMMARY_FILE}" \
k6 run /root/autheris/benchmarks/load/configs/k6/load_test.js

echo ""
echo "=== Test Completed! Results saved to ${SUMMARY_FILE} ==="
