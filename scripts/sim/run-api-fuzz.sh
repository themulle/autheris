#!/usr/bin/env bash
# Runs the Schemathesis API fuzz (REQ-SIMTEST P2) against the loopback sim host Autheris.Sim.ApiHost: a read phase, then a write phase
# against a freshly created host (separate process, fresh data dir). Called by .github/workflows/simulation-nightly.yml (job sim-api-fuzz).
#
# Usage:
#   run-api-fuzz.sh                         run both phases (needs RUNNER_TEMP, SCHEMATHESIS_IMAGE, a built host, docker)
#   run-api-fuzz.sh --check-url <url>       exit 0 only for a plain http(s) base URL whose parsed host is exactly 127.0.0.1 or localhost
#   run-api-fuzz.sh --strip-servers <in> <out>   copy an OpenAPI/Swagger file without any servers/host/basePath/schemes entry
#   run-api-fuzz.sh --self-test
#
# Environment: AUTHERIS_SIM_SEED (1 to 10 digits, default UTC date), SIM_API_PORT (1024-65535, default 5071),
# SIM_API_BASE_URL (default http://127.0.0.1:$SIM_API_PORT; validated like --check-url, SEC-SIM-02), SIM_API_MAX_EXAMPLES (1-3 digits, default 50).
#
# Outputs (never uploaded): raw HAR and JUnit in $RUNNER_TEMP/st/out, the canary list in $RUNNER_TEMP/sim-canaries.txt.
# The checker .github/scripts/sim-api-report.py judges them. Exit codes: non-zero for infrastructure problems (host did not start, a
# Schemathesis run ended with something other than "passed" or "found failures", a phase ran out of time); findings are the checker's job.
set -euo pipefail

SEED_REGEX='^[0-9]{1,10}$'
HOST_DLL="tests/Autheris.Sim.ApiHost/bin/Release/net10.0/Autheris.Sim.ApiHost.dll"
READ_CAP=1500   # 25 minutes
WRITE_CAP=900   # 15 minutes
CHECKS="not_a_server_error,response_schema_conformance,status_code_conformance"

die() {
  echo "run-api-fuzz: $*" >&2
  exit 1
}

# SEC-SIM-02: the base URL is parsed, never prefix-matched.
check_url() {
  python3 -I - "$1" <<'PY'
import sys
from urllib.parse import urlsplit
url = sys.argv[1]
if not url or any(c in url for c in " \t\r\n\\@") or any(ord(c) < 32 or ord(c) == 127 for c in url):
    sys.exit("base URL contains userinfo, backslash, whitespace or control characters")
try:
    parts = urlsplit(url)
    port = parts.port
except ValueError:
    sys.exit("base URL is not parseable")
if parts.scheme not in ("http", "https"):
    sys.exit("base URL must be http or https")
if parts.username is not None or parts.password is not None:
    sys.exit("base URL must not carry userinfo")
if parts.hostname not in ("127.0.0.1", "localhost"):
    sys.exit("base URL host must be exactly 127.0.0.1 or localhost")
if port is None or not 1024 <= port <= 65535:
    sys.exit("base URL needs an explicit port between 1024 and 65535")
if parts.path not in ("", "/") or parts.query or parts.fragment:
    sys.exit("base URL must not carry a path, query or fragment")
PY
}

# SEC-SIM-02: a fetched schema must not be able to redirect traffic; Schemathesis gets --url explicitly and every servers entry is removed.
strip_servers() {
  python3 -I - "$1" "$2" <<'PY'
import json, sys
try:
    with open(sys.argv[1], encoding="utf-8") as handle:
        doc = json.load(handle)
except (OSError, ValueError) as ex:
    sys.exit(f"schema unreadable: {ex}")
if not isinstance(doc, dict):
    sys.exit("schema must be a JSON object")
DROP = {"servers", "host", "basePath", "schemes"}
def strip(node, depth=0):
    if depth > 6 or not isinstance(node, dict):
        return
    for key in list(node):
        if key in DROP and depth <= 3:
            del node[key]
    if depth == 0:
        for item in (doc.get("paths") or {}).values():
            if isinstance(item, dict):
                strip(item, 2)
                for op in item.values():
                    strip(op, 3)
strip(doc)
with open(sys.argv[2], "w", encoding="utf-8") as handle:
    json.dump(doc, handle)
PY
}

wait_for_file() {
  local file="$1" seconds="$2" i
  for ((i = 0; i < seconds; i++)); do
    [ -f "$file" ] && return 0
    sleep 1
  done
  return 1
}

FAILED=0
HOST_PID=""
HOST_STATUS=0
stop_host() {
  # SIGTERM must end the host with exit code 0 (graceful shutdown, data dir wiped). Any other code is a crash or abort and keeps the job red.
  HOST_STATUS=0
  if [ -n "$HOST_PID" ]; then
    if kill -0 "$HOST_PID" 2>/dev/null; then
      kill -TERM "$HOST_PID" 2>/dev/null || true
      local i
      for ((i = 0; i < 30; i++)); do
        kill -0 "$HOST_PID" 2>/dev/null || break
        sleep 1
      done
      kill -KILL "$HOST_PID" 2>/dev/null || true
    else
      echo "run-api-fuzz: the sim host had already exited before it was stopped" >&2
      HOST_STATUS=1
    fi
    wait "$HOST_PID" 2>/dev/null || HOST_STATUS=$?
    if [ "$HOST_STATUS" -ne 0 ]; then
      echo "run-api-fuzz: the sim host exited with code $HOST_STATUS (see $RUNNER_TEMP/sim-host-*.log)" >&2
      FAILED=1
    fi
  fi
  HOST_PID=""
}

start_host() {
  local profile="$1"
  DATA_DIR="$RUNNER_TEMP/sim-data-$profile"
  READY_FILE="$RUNNER_TEMP/sim-ready-$profile"
  rm -rf "$DATA_DIR" "$READY_FILE"
  dotnet "$HOST_DLL" --port "$PORT" --data-dir "$DATA_DIR" --ready-file "$READY_FILE" --profile "$profile" >"$RUNNER_TEMP/sim-host-$profile.log" 2>&1 &
  HOST_PID=$!
  if ! wait_for_file "$READY_FILE" 180; then
    tail -n 20 "$RUNNER_TEMP/sim-host-$profile.log" | sed -E 's/SIMCANARY-[A-Za-z0-9-]+/<canary>/g' >&2 || true
    die "the sim host ($profile) did not become ready"
  fi
  cat "$DATA_DIR/canaries.txt" >>"$RUNNER_TEMP/sim-canaries.txt"
}

fetch_schema() {
  # fetch_schema <url> <outfile> <roles>; no redirects are followed (curl default), the fetched file is never trusted for the target.
  curl -fsS --max-time 60 --max-redirs 0 -H "X-Test-User-Sid: S-1-5-SIM" -H "X-Test-Roles: $3" -H "X-Test-Tenant: tenant_sim" -H "GraphQL-Preflight: 1" "$1" -o "$2.raw"
  strip_servers "$2.raw" "$2"
  rm -f "$2.raw"
}

PHASE_START=0
PHASE_CAP=0

# run_st <label> <budget seconds> <roles> <schema or url> [extra st args...]
run_st() {
  local label="$1" budget="$2" roles="$3" schema="$4"
  shift 4
  local left=$((PHASE_CAP - (SECONDS - PHASE_START)))
  if [ "$left" -le 5 ]; then
    echo "run-api-fuzz: no time left for $label (phase cap reached)" >&2
    FAILED=1
    return 0
  fi
  [ "$budget" -gt "$left" ] && budget=$left
  local rc=0
  echo "run-api-fuzz: $label (seed $SEED, cap ${budget}s)"
  timeout -k 15 "$budget" docker run --rm --network host --read-only --cap-drop ALL --security-opt no-new-privileges \
    --tmpfs /tmp -e HOME=/tmp -w /work --user "$(id -u):$(id -g)" -v "$RUNNER_TEMP/st:/work" "$SCHEMATHESIS_IMAGE" run "$schema" "$@" \
    -H "X-Test-User-Sid: S-1-5-SIM" -H "X-Test-Roles: $roles" -H "X-Test-Tenant: tenant_sim" -H "GraphQL-Preflight: 1" \
    --seed "$SEED" --workers 1 --max-examples "$MAX_EXAMPLES" --request-timeout 10 --max-redirects 0 --checks "$CHECKS" --generation-database none \
    --report junit,har --report-dir /work/out --report-junit-path "/work/out/$label.xml" --report-har-path "/work/out/$label.har" \
    --no-color >"$RUNNER_TEMP/st/out/$label.log" 2>&1 || rc=$?
  # 0 = passed, 1 = Schemathesis found failures (the checker classifies them); anything else (usage error, timeout 124/137) is red here.
  if [ "$rc" -ne 0 ] && [ "$rc" -ne 1 ]; then
    echo "run-api-fuzz: $label ended with exit code $rc (infrastructure problem or phase cap)" >&2
    tail -n 8 "$RUNNER_TEMP/st/out/$label.log" | sed -E 's/SIMCANARY-[A-Za-z0-9-]+/<canary>/g' >&2 || true
    FAILED=1
  fi
}

READ_ONLY_FLAGS=(--exclude-method PUT --exclude-method PATCH --exclude-method DELETE)

phase_read() {
  PHASE_START=$SECONDS
  PHASE_CAP=$READ_CAP
  start_host read
  fetch_schema "$BASE_URL/odata/v4/\$openapi" "$RUNNER_TEMP/st/odata.json" Reader
  fetch_schema "$BASE_URL/api/v1/sql/openapi.json" "$RUNNER_TEMP/st/websql.json" Reader
  # Read phase: GET plus the read-only POST query endpoints (WebSQL query, GraphQL queries); never PUT/PATCH/DELETE or GraphQL mutations.
  run_st read-odata 600 Reader /work/odata.json --url "$BASE_URL/odata/v4" "${READ_ONLY_FLAGS[@]}"
  run_st read-websql 300 Reader /work/websql.json --url "$BASE_URL" "${READ_ONLY_FLAGS[@]}"
  run_st read-graphql 400 Reader "$BASE_URL/graphql" --exclude-name-regex '^Mutation\.' "${READ_ONLY_FLAGS[@]}"
  stop_host
}

phase_write() {
  PHASE_START=$SECONDS
  PHASE_CAP=$WRITE_CAP
  start_host write
  local admin="GovernanceAdmin,ClusterAdmin,DataOwner,Developer"
  fetch_schema "$BASE_URL/api/v1/sql/openapi.json" "$RUNNER_TEMP/st/websql-write.json" "$admin"
  cp tests/Autheris.Tests.Simulation/ApiFuzz/schemas/governance-write.openapi.json "$RUNNER_TEMP/st/governance-write.json"
  strip_servers "$RUNNER_TEMP/st/governance-write.json" "$RUNNER_TEMP/st/governance-write.json.tmp"
  mv "$RUNNER_TEMP/st/governance-write.json.tmp" "$RUNNER_TEMP/st/governance-write.json"
  # Write phase (SEC-SIM-23): all methods, only against this fresh host, which dies with its data dir at the end of the phase.
  run_st write-governance 300 "$admin" /work/governance-write.json --url "$BASE_URL"
  run_st write-websql 200 "$admin" /work/websql-write.json --url "$BASE_URL"
  run_st write-graphql 300 "$admin" "$BASE_URL/graphql"
  stop_host
}

self_test() {
  local ok bad tmp
  for ok in "http://127.0.0.1:5071" "http://localhost:5071" "http://127.0.0.1:5071/"; do
    ( check_url "$ok" ) 2>/dev/null || { echo "self-test: good URL rejected: $ok" >&2; return 1; }
  done
  for bad in "http://127.0.0.1.evil.com:5071" "http://user@127.0.0.1:5071" "http://127.0.0.1@evil.com:5071" "http://evil.com@127.0.0.1:5071" \
    "http://[::]:5071" "http://0.0.0.0:5071" "http://[::1]:5071" "http://127.0.0.2:5071" "http://localhost.evil.com:5071" "http://127.0.0.1" \
    "http://127.0.0.1:80" "ftp://127.0.0.1:5071" "http://127.0.0.1:5071/x" "" "http://127.0.0.1:5071\\@evil.com"; do
    if ( check_url "$bad" ) 2>/dev/null; then echo "self-test: bad URL accepted: $bad" >&2; return 1; fi
  done
  tmp="$(mktemp -d)"
  printf '%s' '{"openapi":"3.1.0","servers":[{"url":"http://evil.example"}],"paths":{"/a":{"servers":[{"url":"http://evil.example"}],"get":{"servers":[{"url":"http://evil.example"}],"responses":{}}}}}' >"$tmp/in.json"
  strip_servers "$tmp/in.json" "$tmp/out.json" || { rm -rf "$tmp"; echo "self-test: strip failed" >&2; return 1; }
  if grep -q 'servers\|evil' "$tmp/out.json"; then rm -rf "$tmp"; echo "self-test: servers entry survived" >&2; return 1; fi
  rm -rf "$tmp"
  echo "run-api-fuzz: self-test passed"
}

case "${1:-}" in
  --check-url)
    [ $# -eq 2 ] || die "usage: --check-url <url>"
    check_url "$2" || die "refusing target: $2"
    exit 0
    ;;
  --strip-servers)
    [ $# -eq 3 ] || die "usage: --strip-servers <in> <out>"
    strip_servers "$2" "$3"
    exit 0
    ;;
  --self-test)
    self_test
    exit $?
    ;;
  "") ;;
  *) die "unknown argument: $1" ;;
esac

: "${RUNNER_TEMP:?RUNNER_TEMP is required}"
: "${SCHEMATHESIS_IMAGE:?SCHEMATHESIS_IMAGE is required}"
SEED="${AUTHERIS_SIM_SEED:-$(date -u +%Y%m%d)}"
[[ "$SEED" =~ $SEED_REGEX ]] || die "AUTHERIS_SIM_SEED must be 1 to 10 digits"
PORT="${SIM_API_PORT:-5071}"
[[ "$PORT" =~ ^[0-9]{4,5}$ ]] && [ "$PORT" -ge 1024 ] && [ "$PORT" -le 65535 ] || die "SIM_API_PORT must be numeric between 1024 and 65535"
MAX_EXAMPLES="${SIM_API_MAX_EXAMPLES:-50}"
[[ "$MAX_EXAMPLES" =~ ^[1-9][0-9]{0,2}$ ]] || die "SIM_API_MAX_EXAMPLES must be 1 to 3 digits"
BASE_URL="${SIM_API_BASE_URL:-http://127.0.0.1:$PORT}"
check_url "$BASE_URL" || die "refusing target: $BASE_URL"
[ "$(python3 -I -c 'import sys; from urllib.parse import urlsplit; print(urlsplit(sys.argv[1]).port)' "$BASE_URL")" = "$PORT" ] || die "SIM_API_BASE_URL port must equal SIM_API_PORT"
[ -f "$HOST_DLL" ] || die "build the sim host first: $HOST_DLL not found"

echo "run-api-fuzz: seed $SEED, target $BASE_URL"
trap stop_host EXIT
mkdir -p "$RUNNER_TEMP/st/out"
: >"$RUNNER_TEMP/sim-canaries.txt"

phase_read
phase_write

# The same canaries appear in both phases; keep the list unique for the checker.
sort -u "$RUNNER_TEMP/sim-canaries.txt" -o "$RUNNER_TEMP/sim-canaries.txt"
[ "$FAILED" -eq 0 ] || die "at least one Schemathesis run did not complete (see messages above)"
echo "run-api-fuzz: runs complete; the checker judges the reports"
