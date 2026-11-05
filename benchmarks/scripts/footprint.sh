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

# Step 2: Build container image
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

CLEANUP_CONTAINER=""
cleanup() {
  if [ -n "$CLEANUP_CONTAINER" ]; then
    $CONTAINER_ENGINE stop "$CLEANUP_CONTAINER" >/dev/null 2>&1 || true
    $CONTAINER_ENGINE rm -f "$CLEANUP_CONTAINER" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

for i in $(seq 1 "$ITERATIONS"); do
  START_NS=$(date +%s%N)
  CLEANUP_CONTAINER=$($CONTAINER_ENGINE run -d --rm -p "$PORT:8080" -e ASPNETCORE_ENVIRONMENT=Production autheris:measure)
  
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
    $CONTAINER_ENGINE logs "$CLEANUP_CONTAINER" >&2 || true
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
  CLEANUP_CONTAINER=""
  echo "  Iteration $i: $DURATION_MS ms"
done

# Step 5: Warm Latency Measurement
echo "Measuring warm latency (Warmup: $WARM_UP, Requests: $WARM_REQUESTS)..."
CLEANUP_CONTAINER=$($CONTAINER_ENGINE run -d --rm -p "$PORT:8080" -e ASPNETCORE_ENVIRONMENT=Production autheris:measure)
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
