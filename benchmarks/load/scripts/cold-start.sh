#!/usr/bin/env bash
# PLAN-OPS-150 WP-A: manual cold-path measurement. NOT part of CI or publish output (SEC-OPS150-14).
#
# Starts the Release build of Autheris.Api on 127.0.0.1 (Development, test auth, anonymous dev access, SQLite governance db,
# no credentials), then for each restart:
#   1. measures process start -> first /health/ready 200 (50 ms polling),
#   2. waits SETTLE_SECONDS, then sends the first request and WARM_REQUESTS warm requests per probe (curl -w time_total):
#      GraphQL query, MCP describe_dataset, MCP describe_my_access (the probe order rotates per restart),
#   3. optionally records System.Runtime time-in-jit / methods-jitted-count with dotnet-counters (COUNTERS_BIN).
# Prerequisite: dotnet build src/Autheris.Api -c Release -p:AutherisIncludeTestAuth=true
# Output: one CSV line per request to OUT_DIR/cold-start.csv (restart,probe,kind,index,ms) and a ready-time line per restart.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
API_DIR="${API_DIR:-$ROOT/src/Autheris.Api/bin/Release/net10.0}"
PORT="${PORT:-18080}"
RESTARTS="${RESTARTS:-3}"
WARM_REQUESTS="${WARM_REQUESTS:-20}"
SETTLE_SECONDS="${SETTLE_SECONDS:-5}"
COLDPATH_TIMING="${COLDPATH_TIMING:-true}"
COUNTERS_BIN="${COUNTERS_BIN:-}"
COUNTERS_SECONDS="${COUNTERS_SECONDS:-30}" # collection window; must cover SETTLE_SECONDS plus all requests
OUT_DIR="${OUT_DIR:-$(mktemp -d)}"
BASE="http://127.0.0.1:$PORT"
DATASET="${DATASET:-billing.dbo.billing_table_1}"

mkdir -p "$OUT_DIR"
: > "$OUT_DIR/cold-start.csv"
echo "restart,probe,kind,index,ms" > "$OUT_DIR/cold-start.csv"

ms_since() { awk -v a="$1" -v b="$(date +%s.%N)" 'BEGIN { printf "%.0f", (b - a) * 1000 }'; }

probe_curl() {
  case "$1" in
    gql) curl -s -o /dev/null -w '%{time_total}' "$BASE/graphql" -H 'Content-Type: application/json' -H 'GraphQL-Preflight: 1' \
           -d "{\"query\":\"{ ${DATASET//./_}(first: 1) { __typename } }\"}" ;;
    describe_dataset|describe_my_access)
      curl -s -o /dev/null -w '%{time_total}' "$BASE/mcp" -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
        -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"$1\",\"arguments\":{\"dataset\":\"$DATASET\"}}}" ;;
  esac
}

ORDERS=("gql describe_dataset describe_my_access" "describe_dataset describe_my_access gql" "describe_my_access gql describe_dataset")

for ((r = 1; r <= RESTARTS; r++)); do
  rm -f "$OUT_DIR/gov-$r.db"*
  (
    cd "$API_DIR"
    export ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="$BASE"
    export Gateway__Authentication__EnableTestAuthHandler=true Gateway__Insecure__danger_bypass_mcp_auth=true
    export Gateway__Insecure__danger_allow_anonymous_access=true Gateway__Mcp__Enabled=true Gateway__Mcp__EndpointPath=/mcp
    export Gateway__GovernanceDb__Provider=Sqlite "Gateway__GovernanceDb__ConnectionString=Data Source=$OUT_DIR/gov-$r.db"
    export Gateway__Startup__ColdPathTiming__Enabled="$COLDPATH_TIMING"
    exec dotnet Autheris.Api.dll > "$OUT_DIR/api-$r.log" 2>&1
  ) &
  PID=$!
  T0="$(date +%s.%N)"
  until [ "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/health/ready" || true)" = "200" ]; do
    kill -0 "$PID" 2>/dev/null || { echo "API exited early, see $OUT_DIR/api-$r.log" >&2; exit 1; }
    sleep 0.05
  done
  echo "$r,ready,startup,0,$(ms_since "$T0")" >> "$OUT_DIR/cold-start.csv"

  if [ -n "$COUNTERS_BIN" ]; then
    "$COUNTERS_BIN" collect -p "$PID" --refresh-interval 1 --duration "00:00:$COUNTERS_SECONDS" --format csv -o "$OUT_DIR/counters-$r" \
      --counters 'System.Runtime[time-in-jit,methods-jitted-count]' > /dev/null 2>&1 &
    CPID=$!
  fi
  sleep "$SETTLE_SECONDS"
  date +%s > "$OUT_DIR/requests-start-$r.txt"

  for probe in ${ORDERS[$(((r - 1) % ${#ORDERS[@]}))]}; do
    echo "$r,$probe,first,0,$(awk -v t="$(probe_curl "$probe")" 'BEGIN { printf "%.1f", t * 1000 }')" >> "$OUT_DIR/cold-start.csv"
    for ((i = 1; i <= WARM_REQUESTS; i++)); do
      echo "$r,$probe,warm,$i,$(awk -v t="$(probe_curl "$probe")" 'BEGIN { printf "%.1f", t * 1000 }')" >> "$OUT_DIR/cold-start.csv"
    done
  done

  [ -n "$COUNTERS_BIN" ] && { wait "$CPID" 2>/dev/null || true; }
  kill "$PID" 2>/dev/null || true
  wait "$PID" 2>/dev/null || true
done

echo "Results: $OUT_DIR/cold-start.csv (API logs and counters in the same directory)"
