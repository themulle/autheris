#!/usr/bin/env bash
# ==============================================================================
# WP-SIMPL-10 / PLAN-SIMPL: Container Image & Runtime Footprint Measurement Script
# Measures uncompressed/compressed image size, cold-start latency, warm latency,
# and verifies lazy loading of DuckDB and Oracle native assemblies.
# ==============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

ALLOW_DIRTY=false
ITERATIONS=20
WARM_REQUESTS=2000
WARM_UP=200
PORT=18080

for arg in "$@"; do
  case "$arg" in
    --allow-dirty) ALLOW_DIRTY=true ;;
    --quick) ITERATIONS=5; WARM_REQUESTS=200; WARM_UP=20 ;;
  esac
done

if [ "$ALLOW_DIRTY" != "true" ]; then
  if ! git diff --quiet || ! git diff --cached --quiet; then
    echo "Error: working directory has uncommitted changes. Commit or stash them before measuring, or pass --allow-dirty." >&2
    exit 1
  fi
fi

CONTAINER_ENGINE="${CONTAINER_ENGINE:-}"
if [ -z "$CONTAINER_ENGINE" ]; then
  if command -v podman >/dev/null 2>&1; then
    CONTAINER_ENGINE="podman"
  elif command -v docker >/dev/null 2>&1; then
    CONTAINER_ENGINE="docker"
  else
    echo "Error: neither podman nor docker found on system." >&2
    exit 1
  fi
fi

DATE="$(date -u +%Y-%m-%d)"
SHA="$(git rev-parse --short=7 HEAD)"
OUT_DIR="$ROOT/benchmarks/results"
OUT_FILE="$OUT_DIR/footprint-$DATE-$SHA.json"
mkdir -p "$OUT_DIR"

echo "=== Autheris Footprint & Latency Measurement ==="
echo "Date: $DATE | Git SHA: $SHA | Engine: $CONTAINER_ENGINE"

# Step 1: Ensure publish output exists
if [ ! -f "$ROOT/dist/publish/Autheris.Api.dll" ]; then
  echo "Publishing Autheris.Api (Release, no test auth)..."
  dotnet publish "$ROOT/src/Autheris.Api/Autheris.Api.csproj" \
    -c Release \
    -p:AutherisIncludeTestAuth=false \
    -o "$ROOT/dist/publish"
fi

# Step 2: Build container image (the Dockerfile copies dist/modules; measure the image without modules)
mkdir -p "$ROOT/dist/modules"
echo "Building benchmark container image (autheris:measure)..."
$CONTAINER_ENGINE build -f Dockerfile -t autheris:measure .

# Step 3: Measure Image Sizes
echo "Inspecting image size..."
IMAGE_SIZE_BYTES=$($CONTAINER_ENGINE image inspect --format '{{.Size}}' autheris:measure)
IMAGE_SIZE_MB=$(awk -v b="$IMAGE_SIZE_BYTES" 'BEGIN { printf "%.2f", b / (1024 * 1024) }')
echo "Image Uncompressed Size: $IMAGE_SIZE_MB MB ($IMAGE_SIZE_BYTES bytes)"

echo "Measuring compressed image transfer footprint..."
COMPRESSED_SIZE_BYTES=$($CONTAINER_ENGINE save autheris:measure | gzip -c | wc -c)
COMPRESSED_SIZE_MB=$(awk -v b="$COMPRESSED_SIZE_BYTES" 'BEGIN { printf "%.2f", b / (1024 * 1024) }')
echo "Image Compressed Size: $COMPRESSED_SIZE_MB MB ($COMPRESSED_SIZE_BYTES bytes)"

# Step 4: Cold-Start Measurement Loop & Lazy Load Inspection
echo "Starting cold-start measurement loop ($ITERATIONS iterations)..."
COLD_STARTS=()
LAZY_DUCKDB="true"
LAZY_ORACLE="true"

# The gateway requires a governance database (PostgreSQL or SQL Server) and, outside Development, a verified-TLS connection to it.
# A throwaway PostgreSQL sidecar with a throwaway CA and server certificate (valid one day) provides that; the password is random per run.
RUN_ID="$$"
NET_NAME="footprint-net-$RUN_ID"
PG_NAME="footprint-pg-$RUN_ID"
# Digest-pinned like every other CI image (same major as deploy/podman-compose.yaml).
PG_IMAGE='docker.io/library/postgres:18-alpine@sha256:b6972e1115a4ff8eba5d7f7d19184926d1e16d7d1136ae1e1a7ebf286da0f015'
TLS_DIR="$(mktemp -d)"
CLEANUP_CONTAINER=""
NET_CREATED=false
PG_STARTED=false

cleanup() {
  if [ -n "$CLEANUP_CONTAINER" ]; then
    $CONTAINER_ENGINE stop "$CLEANUP_CONTAINER" >/dev/null 2>&1 || true
    $CONTAINER_ENGINE rm -f "$CLEANUP_CONTAINER" >/dev/null 2>&1 || true
  fi
  if [ "$PG_STARTED" = "true" ]; then
    $CONTAINER_ENGINE rm -f "$PG_NAME" >/dev/null 2>&1 || true
  fi
  if [ "$NET_CREATED" = "true" ]; then
    $CONTAINER_ENGINE network rm "$NET_NAME" >/dev/null 2>&1 || true
  fi
  rm -rf "$TLS_DIR"
}
trap cleanup EXIT

dump_logs() {
  echo "--- app container logs ($1) ---" >&2
  $CONTAINER_ENGINE logs "$1" >&2 2>&1 || true
  echo "--- postgres sidecar logs ---" >&2
  $CONTAINER_ENGINE logs "$PG_NAME" >&2 2>&1 || true
}

echo "Starting PostgreSQL sidecar with verified TLS..."
(
  cd "$TLS_DIR"
  openssl genrsa -out ca.key 2048 2>/dev/null
  openssl req -x509 -new -key ca.key -sha256 -days 1 -subj "/CN=autheris-footprint-ca" -out ca.crt
  openssl genrsa -out server.key 2048 2>/dev/null
  openssl req -new -key server.key -subj "/CN=$PG_NAME" -out server.csr
  printf 'subjectAltName=DNS:%s\nextendedKeyUsage=serverAuth\nbasicConstraints=CA:FALSE\n' "$PG_NAME" > server.ext
  openssl x509 -req -in server.csr -CA ca.crt -CAkey ca.key -CAcreateserial -sha256 -days 1 -extfile server.ext -out server.crt 2>/dev/null
  rm -f ca.key ca.srl server.csr server.ext
  chmod 0644 ca.crt server.crt server.key
)
chmod 0755 "$TLS_DIR"

PG_PASSWORD="$(openssl rand -hex 24)"
$CONTAINER_ENGINE network create "$NET_NAME" >/dev/null
NET_CREATED=true
# The key must be owned by the postgres user with mode 0600; copy it inside the container (root at start) instead of chown-ing on the host.
PG_STARTED=true
$CONTAINER_ENGINE run -d --name "$PG_NAME" --network "$NET_NAME" \
  -e POSTGRES_USER=footprint -e POSTGRES_DB=governancedb -e POSTGRES_PASSWORD="$PG_PASSWORD" \
  -v "$TLS_DIR:/tls-src:ro,z" \
  --entrypoint sh "$PG_IMAGE" -c '
    set -e
    mkdir -p /tls
    cp /tls-src/server.crt /tls-src/server.key /tls/
    chown postgres:postgres /tls/server.crt /tls/server.key
    chmod 0600 /tls/server.key
    exec docker-entrypoint.sh postgres -c ssl=on -c ssl_cert_file=/tls/server.crt -c ssl_key_file=/tls/server.key' >/dev/null

PG_READY=false
for _ in $(seq 1 60); do
  if $CONTAINER_ENGINE exec "$PG_NAME" pg_isready -h 127.0.0.1 -U footprint -d governancedb -q >/dev/null 2>&1; then
    PG_READY=true
    break
  fi
  sleep 1
done
if [ "$PG_READY" != "true" ]; then
  echo "Error: PostgreSQL sidecar did not become ready within 60 seconds" >&2
  $CONTAINER_ENGINE logs "$PG_NAME" >&2 2>&1 || true
  exit 1
fi
# The entrypoint restarts the server once after initdb; wait for it to settle on the TCP socket with TLS.
sleep 2

PG_CONN="Host=$PG_NAME;Port=5432;Database=governancedb;Username=footprint;Password=$PG_PASSWORD;SSL Mode=VerifyFull;Root Certificate=/tls/ca.crt"

# Minimal Production configuration the gateway requires to start (same set as the signed-module container e2e in docker-publish.yml).
RUN_ENV=(
  --network "$NET_NAME"
  -v "$TLS_DIR/ca.crt:/tls/ca.crt:ro,z"
  -e ASPNETCORE_ENVIRONMENT=Production
  -e Gateway__Hosting__AllowedHosts__0=127.0.0.1
  -e Gateway__Hosting__AllowedHosts__1=localhost
  -e Gateway__DataMasking__HmacSecretKeyVaultRef=FOOTPRINT_HMAC_KEY
  -e FOOTPRINT_HMAC_KEY="$(openssl rand -hex 32)"
  -e Gateway__RestApi__BulkStream__SigningKey="$(openssl rand -hex 32)"
  -e Gateway__Authentication__Mode=Upstream
  -e Gateway__Authentication__ForwardAuth__Enabled=true
  -e Gateway__Authentication__ForwardAuth__TrustedProxies__0=127.0.0.1
  -e Gateway__Authentication__ForwardAuth__SharedSecret="$(openssl rand -hex 32)"
  -e Gateway__Audit__ChainAnchorPath=/app/data/audit-chain.anchor
  -e Authentication__Realms__Realms__proxy__Kind=ForwardAuth
  -e Authentication__Realms__Realms__proxy__FixedTenantId=footprint
  -e Authentication__Realms__Realms__proxy__Provisioning=None
  -e Gateway__Tenancy__DefaultTenantId=footprint
  -e Gateway__Governance__GrantRecords__SigningKey="$(openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt)"
  -e Gateway__SqlEndpoints__Directory=/app/data/queries
  -e Gateway__GovernanceDb__Provider=PostgreSql
  -e Gateway__GovernanceDb__ConnectionString="$PG_CONN"
)

# One-off schema migration (always applies); all measurements below run with Migrations:Mode=Verify so they measure the app, not a migration.
echo "Applying governance schema migrations (one-off job)..."
MIGRATE_NAME="footprint-migrate-$RUN_ID"
if ! $CONTAINER_ENGINE run --name "$MIGRATE_NAME" "${RUN_ENV[@]}" autheris:measure --migrate-governance; then
  echo "Error: governance migration job failed" >&2
  dump_logs "$MIGRATE_NAME"
  $CONTAINER_ENGINE rm -f "$MIGRATE_NAME" >/dev/null 2>&1 || true
  exit 1
fi
$CONTAINER_ENGINE rm -f "$MIGRATE_NAME" >/dev/null 2>&1 || true
RUN_ENV+=(-e Gateway__GovernanceDb__Migrations__Mode=Verify)

for i in $(seq 1 "$ITERATIONS"); do
  START_NS=$(date +%s%N)
  CLEANUP_CONTAINER=$($CONTAINER_ENGINE run -d -p "$PORT:8080" "${RUN_ENV[@]}" autheris:measure)
  
  READY=false
  TIMEOUT_SECS=60
  DEADLINE=$(( $(date +%s) + TIMEOUT_SECS ))
  
  while [ "$(date +%s)" -lt "$DEADLINE" ]; do
    if curl -s -f -o /dev/null "http://127.0.0.1:$PORT/health/ready" 2>/dev/null; then
      END_NS=$(date +%s%N)
      DURATION_MS=$(awk -v s="$START_NS" -v e="$END_NS" 'BEGIN { printf "%.2f", (e - s) / 1000000 }')
      COLD_STARTS+=("$DURATION_MS")
      READY=true
      break
    fi
    sleep 0.05
  done

  if [ "$READY" != "true" ]; then
    echo "Error: cold start timed out on iteration $i" >&2
    dump_logs "$CLEANUP_CONTAINER"
    exit 1
  fi

  # Check lazy loading on first iteration
  if [ "$i" -eq 1 ]; then
    DUCKDB_HITS=$($CONTAINER_ENGINE exec "$CLEANUP_CONTAINER" cat /proc/1/maps 2>/dev/null | grep -c -i duckdb || true)
    ORACLE_HITS=$($CONTAINER_ENGINE exec "$CLEANUP_CONTAINER" cat /proc/1/maps 2>/dev/null | grep -c -i oracle || true)
    if [ "$DUCKDB_HITS" -gt 0 ]; then
      LAZY_DUCKDB="false"
    fi
    if [ "$ORACLE_HITS" -gt 0 ]; then
      LAZY_ORACLE="false"
    fi
    echo "Lazy Load Check: DuckDB loaded=$DUCKDB_HITS (lazy: $LAZY_DUCKDB) | Oracle loaded=$ORACLE_HITS (lazy: $LAZY_ORACLE)"
  fi

  $CONTAINER_ENGINE stop "$CLEANUP_CONTAINER" >/dev/null 2>&1 || true
  $CONTAINER_ENGINE rm -f "$CLEANUP_CONTAINER" >/dev/null 2>&1 || true
  CLEANUP_CONTAINER=""
  echo "  Iteration $i: $DURATION_MS ms"
done

# Step 5: Warm Latency Measurement
echo "Measuring warm latency (Warmup: $WARM_UP, Requests: $WARM_REQUESTS)..."
CLEANUP_CONTAINER=$($CONTAINER_ENGINE run -d -p "$PORT:8080" "${RUN_ENV[@]}" autheris:measure)
DEADLINE=$(( $(date +%s) + 60 ))
while [ "$(date +%s)" -lt "$DEADLINE" ]; do
  if curl -s -f -o /dev/null "http://127.0.0.1:$PORT/health/ready" 2>/dev/null; then
    break
  fi
  sleep 0.05
done

# Warmup queries
for _ in $(seq 1 "$WARM_UP"); do
  curl -s -f -o /dev/null "http://127.0.0.1:$PORT/health/ready" 2>/dev/null || true
done

# Measured sequential requests
WARM_TIMES=()
for _ in $(seq 1 "$WARM_REQUESTS"); do
  TIME_MS=$(curl -s -o /dev/null -w '%{time_total}' "http://127.0.0.1:$PORT/health/ready")
  TIME_MS=$(awk -v t="$TIME_MS" 'BEGIN { printf "%.2f", t * 1000 }')
  WARM_TIMES+=("$TIME_MS")
done

$CONTAINER_ENGINE stop "$CLEANUP_CONTAINER" >/dev/null 2>&1 || true
CLEANUP_CONTAINER=""

# Helper function to compute percentiles (nearest-rank)
calc_percentile() {
  local p="$1"
  shift
  local arr=("$@")
  local n="${#arr[@]}"
  if [ "$n" -eq 0 ]; then
    echo "0.0"
    return
  fi
  local sorted=($(printf '%s\n' "${arr[@]}" | sort -n))
  local rank=$(awk -v pct="$p" -v count="$n" 'BEGIN { r = (pct / 100.0) * count; printf "%d", (r == int(r) ? r : int(r) + 1) }')
  local idx=$((rank - 1))
  if [ "$idx" -lt 0 ]; then idx=0; fi
  if [ "$idx" -ge "$n" ]; then idx=$((n - 1)); fi
  echo "${sorted[$idx]}"
}

COLD_P50=$(calc_percentile 50 "${COLD_STARTS[@]}")
COLD_P95=$(calc_percentile 95 "${COLD_STARTS[@]}")
WARM_P50=$(calc_percentile 50 "${WARM_TIMES[@]}")
WARM_P99=$(calc_percentile 99 "${WARM_TIMES[@]}")

echo "Cold Start Latency: P50=$COLD_P50 ms | P95=$COLD_P95 ms"
echo "Warm Query Latency: P50=$WARM_P50 ms | P99=$WARM_P99 ms"

# Host metadata
HOSTNAME_VAL="$(hostname 2>/dev/null || echo "unknown")"
OS_VAL="$(uname -s 2>/dev/null || echo "unknown")"
KERNEL_VAL="$(uname -r 2>/dev/null || echo "unknown")"
ARCH_VAL="$(uname -m 2>/dev/null || echo "unknown")"
CPU_VAL="$(lscpu 2>/dev/null | grep 'Model name:' | sed 's/Model name:[[:space:]]*//' | head -n 1 || echo "unknown")"

# Produce JSON output
if command -v jq >/dev/null 2>&1; then
  jq -n \
    --arg metric "footprint" \
    --arg date "$DATE" \
    --arg sha "$SHA" \
    --arg hostname "$HOSTNAME_VAL" \
    --arg os "$OS_VAL" \
    --arg kernel "$KERNEL_VAL" \
    --arg arch "$ARCH_VAL" \
    --arg cpu "$CPU_VAL" \
    --arg method "bash benchmarks/scripts/footprint.sh" \
    --argjson image_uncompressed_bytes "$IMAGE_SIZE_BYTES" \
    --argjson image_uncompressed_mb "$IMAGE_SIZE_MB" \
    --argjson image_compressed_bytes "$COMPRESSED_SIZE_BYTES" \
    --argjson image_compressed_mb "$COMPRESSED_SIZE_MB" \
    --argjson cold_p50_ms "$COLD_P50" \
    --argjson cold_p95_ms "$COLD_P95" \
    --argjson warm_p50_ms "$WARM_P50" \
    --argjson warm_p99_ms "$WARM_P99" \
    --argjson lazy_duckdb "$LAZY_DUCKDB" \
    --argjson lazy_oracle "$LAZY_ORACLE" \
    '{
      metric: $metric,
      date: $date,
      sha: $sha,
      host: {
        hostname: $hostname,
        os: $os,
        kernel: $kernel,
        arch: $arch,
        cpu: $cpu
      },
      values: {
        image_uncompressed_bytes: $image_uncompressed_bytes,
        image_uncompressed_mb: $image_uncompressed_mb,
        image_compressed_bytes: $image_compressed_bytes,
        image_compressed_mb: $image_compressed_mb,
        cold_p50_ms: $cold_p50_ms,
        cold_p95_ms: $cold_p95_ms,
        warm_p50_ms: $warm_p50_ms,
        warm_p99_ms: $warm_p99_ms,
        lazy_duckdb: $lazy_duckdb,
        lazy_oracle: $lazy_oracle
      },
      method: $method
    }' > "$OUT_FILE"
fi

chmod 644 "$OUT_FILE"
echo "Results written to: $OUT_FILE"
