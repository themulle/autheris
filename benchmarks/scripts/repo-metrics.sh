#!/usr/bin/env bash
# ==============================================================================
# WP-SIMPL-10 / PLAN-SIMPL: Repository Metrics Baseline Measurement Script
# Measures codebase size, structure, dependency injection, and test hygiene metrics.
# ==============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

ALLOW_DIRTY=false
for arg in "$@"; do
  case "$arg" in
    --allow-dirty) ALLOW_DIRTY=true ;;
  esac
done

if [ "$ALLOW_DIRTY" != "true" ]; then
  if ! git diff --quiet || ! git diff --cached --quiet; then
    echo "Error: working directory has uncommitted changes. Commit or stash them before measuring, or pass --allow-dirty." >&2
    exit 1
  fi
fi

DATE="$(date -u +%Y-%m-%d)"
SHA="$(git rev-parse --short=7 HEAD)"
OUT_DIR="$ROOT/benchmarks/results"
OUT_FILE="$OUT_DIR/repo-metrics-$DATE-$SHA.json"
mkdir -p "$OUT_DIR"

echo "=== Autheris Repository Metrics Measurement ==="
echo "Date: $DATE | Git SHA: $SHA"

# 1. Source Lines of Code (src/ only)
SRC_LOC=$(git ls-files -z 'src/*.cs' | wc -l --files0-from=- | tail -n 1 | awk '{print $1}')
echo "Source LOC (src/): $SRC_LOC"

# 2. Modules LOC (modules/*.cs if exists, else 0)
MODULES_LOC=0
if [ -d "$ROOT/modules" ] && compgen -G "$ROOT/modules/**/*.cs" > /dev/null; then
  MODULES_LOC=$(git ls-files -z 'modules/*.cs' 2>/dev/null | wc -l --files0-from=- | tail -n 1 | awk '{print $1}')
fi
echo "Modules LOC: $MODULES_LOC"

# 3. Test Lines of Code (tests/ only)
TEST_LOC=$(git ls-files -z 'tests/*.cs' | wc -l --files0-from=- | tail -n 1 | awk '{print $1}')
echo "Test LOC (tests/): $TEST_LOC"

# 4. Map* Endpoint Count in src/
# Single-sourced regex matching ProgramRatchetTests: \.Map(Get|Post|Put|Delete|Patch|Methods|Group|Hub|GraphQL)\s*\(
MAP_ENDPOINTS=$(git grep -E -h '\.Map(Get|Post|Put|Delete|Patch|Methods|Group|Hub|GraphQL)\s*\(' src/ | grep -v '^[[:space:]]*//' | wc -l)
echo "Map* Endpoints: $MAP_ENDPOINTS"

# 5. Application Top-Level Folders (excluding bin, obj, Properties)
APP_FOLDERS=$(find src/Autheris.Application -mindepth 1 -maxdepth 1 -type d | sed 's|.*/||' | grep -vE '^(bin|obj|Properties)$' | wc -l)
echo "Application Top-Level Folders: $APP_FOLDERS"

# 6. Options Classes in src/
OPTIONS_CLASSES=$(git grep -E -h 'class\s+[A-Za-z0-9_]+Options\b' src/ | grep -v '^[[:space:]]*//' | wc -l)
echo "Options Classes: $OPTIONS_CLASSES"

# 7. *Enabled Flags in src/
ENABLED_FLAGS=$(git grep -E -h 'public\s+bool\s+[A-Za-z0-9_]*Enabled\b' src/ | grep -v '^[[:space:]]*//' | wc -l)
echo "Enabled Flags: $ENABLED_FLAGS"

# 8. Hosted Services in src/
HOSTED_SERVICES=$(git grep -E -h 'AddHostedService<' src/ | grep -v '^[[:space:]]*//' | wc -l)
echo "Hosted Services: $HOSTED_SERVICES"

# 9. Dependency Injection Registrations in src/
DI_REGISTRATIONS=$(git grep -E -h '\.(AddSingleton|AddScoped|AddTransient|TryAdd[A-Za-z0-9_]*)\s*(<|\()' src/ | grep -v '^[[:space:]]*//' | wc -l)
echo "DI Registrations: $DI_REGISTRATIONS"

# 10. Test Hygiene: Substitute.For Count in tests/
SUBSTITUTE_FOR=$(git grep -E -h 'Substitute\.For\b' tests/ | grep -v '^[[:space:]]*//' | wc -l)
echo "Substitute.For Calls: $SUBSTITUTE_FOR"

# 11. Test Hygiene: .Received( Count in tests/
RECEIVED=$(git grep -E -h '\.Received\(' tests/ | grep -v '^[[:space:]]*//' | wc -l)
echo "Received Calls: $RECEIVED"

# 12. Test Hygiene: Thread.Sleep / Task.Delay Count in tests/
SLEEPS=$(git grep -E -h '(Thread\.Sleep|Task\.Delay)\s*\(' tests/ | grep -v '^[[:space:]]*//' | wc -l)
echo "Sleeps / Task.Delay: $SLEEPS"

# 13. Container-backed Test Share
TOTAL_TEST_FILES=$(git ls-files 'tests/*.cs' | wc -l)
CONTAINER_TEST_FILES=$(git grep -l -E 'Testcontainers|IClassFixture<.*(Postgres|SqlServer|Oracle)' tests/ | grep '\.cs$' | wc -l)
CONTAINER_SHARE_PCT=$(awk -v c="$CONTAINER_TEST_FILES" -v t="$TOTAL_TEST_FILES" 'BEGIN { printf "%.2f", (c / t) * 100 }')
echo "Container Test Files: $CONTAINER_TEST_FILES / $TOTAL_TEST_FILES ($CONTAINER_SHARE_PCT%)"

# Host metadata
HOSTNAME_VAL="$(hostname 2>/dev/null || echo "unknown")"
OS_VAL="$(uname -s 2>/dev/null || echo "unknown")"
KERNEL_VAL="$(uname -r 2>/dev/null || echo "unknown")"
ARCH_VAL="$(uname -m 2>/dev/null || echo "unknown")"
CPU_VAL="$(lscpu 2>/dev/null | grep 'Model name:' | sed 's/Model name:[[:space:]]*//' | head -n 1 || echo "unknown")"

# Produce JSON output
if command -v jq >/dev/null 2>&1; then
  jq -n \
    --arg metric "repo-metrics" \
    --arg date "$DATE" \
    --arg sha "$SHA" \
    --arg hostname "$HOSTNAME_VAL" \
    --arg os "$OS_VAL" \
    --arg kernel "$KERNEL_VAL" \
    --arg arch "$ARCH_VAL" \
    --arg cpu "$CPU_VAL" \
    --arg method "bash benchmarks/scripts/repo-metrics.sh" \
    --argjson src_loc "$SRC_LOC" \
    --argjson modules_loc "$MODULES_LOC" \
    --argjson test_loc "$TEST_LOC" \
    --argjson map_endpoints "$MAP_ENDPOINTS" \
    --argjson application_folders "$APP_FOLDERS" \
    --argjson options_classes "$OPTIONS_CLASSES" \
    --argjson enabled_flags "$ENABLED_FLAGS" \
    --argjson hosted_services "$HOSTED_SERVICES" \
    --argjson di_registrations "$DI_REGISTRATIONS" \
    --argjson substitute_for "$SUBSTITUTE_FOR" \
    --argjson received "$RECEIVED" \
    --argjson sleeps "$SLEEPS" \
    --argjson container_test_files "$CONTAINER_TEST_FILES" \
    --argjson total_test_files "$TOTAL_TEST_FILES" \
    --argjson container_share_pct "$CONTAINER_SHARE_PCT" \
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
        src_loc: $src_loc,
        modules_loc: $modules_loc,
        test_loc: $test_loc,
        map_endpoints: $map_endpoints,
        application_folders: $application_folders,
        options_classes: $options_classes,
        enabled_flags: $enabled_flags,
        hosted_services: $hosted_services,
        di_registrations: $di_registrations,
        substitute_for: $substitute_for,
        received: $received,
        sleeps: $sleeps,
        container_test_files: $container_test_files,
        total_test_files: $total_test_files,
        container_share_pct: $container_share_pct
      },
      method: $method
    }' > "$OUT_FILE"
else
  cat <<EOF > "$OUT_FILE"
{
  "metric": "repo-metrics",
  "date": "$DATE",
  "sha": "$SHA",
  "host": {
    "hostname": "$HOSTNAME_VAL",
    "os": "$OS_VAL",
    "kernel": "$KERNEL_VAL",
    "arch": "$ARCH_VAL",
    "cpu": "$CPU_VAL"
  },
  "values": {
    "src_loc": $SRC_LOC,
    "modules_loc": $MODULES_LOC,
    "test_loc": $TEST_LOC,
    "map_endpoints": $MAP_ENDPOINTS,
    "application_folders": $APP_FOLDERS,
    "options_classes": $OPTIONS_CLASSES,
    "enabled_flags": $ENABLED_FLAGS,
    "hosted_services": $HOSTED_SERVICES,
    "di_registrations": $DI_REGISTRATIONS,
    "substitute_for": $SUBSTITUTE_FOR,
    "received": $RECEIVED,
    "sleeps": $SLEEPS,
    "container_test_files": $CONTAINER_TEST_FILES,
    "total_test_files": $TOTAL_TEST_FILES,
    "container_share_pct": $CONTAINER_SHARE_PCT
  },
  "method": "bash benchmarks/scripts/repo-metrics.sh"
}
EOF
fi

chmod 644 "$OUT_FILE"
echo "Results written to: $OUT_FILE"
