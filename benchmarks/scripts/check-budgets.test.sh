#!/usr/bin/env bash
# ==============================================================================
# WP-SIMPL-35: Self-test for benchmarks/scripts/check-budgets.sh
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
CHECK_SCRIPT="$SCRIPT_DIR/check-budgets.sh"
BUDGETS_FILE="$ROOT/benchmarks/budgets.json"

TMP_DIR="$(mktemp -d)"
cleanup() {
  rm -rf "$TMP_DIR"
}
trap cleanup EXIT

assert_exit() {
  local expected="$1"
  local label="$2"
  shift 2
  set +e
  "$@" >/dev/null 2>&1
  local actual=$?
  set -e
  if [ "$actual" -ne "$expected" ]; then
    echo "FAIL [$label]: expected exit $expected, got $actual" >&2
    exit 1
  fi
  echo "OK [$label] (exit $actual)"
}

# 1. Fixture within budget -> exit 0
cat > "$TMP_DIR/within.json" <<'EOF'
{
  "coreImageUncompressedMB": 325,
  "coldStartP50Seconds": 3.0,
  "warmP99Milliseconds": 3.47,
  "ciPrWallClockMedianMinutes": 25.0
}
EOF
SUMMARY_FILE="$TMP_DIR/summary.md"
GITHUB_STEP_SUMMARY="$SUMMARY_FILE" assert_exit 0 "fixture within budget" bash "$CHECK_SCRIPT" "$BUDGETS_FILE" "$TMP_DIR/within.json"
grep -q "coreImageUncompressedMB" "$SUMMARY_FILE"

# 2. Footprint metric +11% (325 * 1.11 = 360.75) -> exit 1
cat > "$TMP_DIR/over-footprint.json" <<'EOF'
{
  "coreImageUncompressedMB": 360.75,
  "coldStartP50Seconds": 3.0,
  "warmP99Milliseconds": 3.47,
  "ciPrWallClockMedianMinutes": 25.0
}
EOF
assert_exit 1 "footprint metric +11%" bash "$CHECK_SCRIPT" "$BUDGETS_FILE" "$TMP_DIR/over-footprint.json"

# 3. PR CI median 30.0 min (lessThan 30) -> exit 1
cat > "$TMP_DIR/ci-30-0.json" <<'EOF'
{
  "coreImageUncompressedMB": 325,
  "coldStartP50Seconds": 3.0,
  "warmP99Milliseconds": 3.47,
  "ciPrWallClockMedianMinutes": 30.0
}
EOF
assert_exit 1 "PR CI median 30.0 min" bash "$CHECK_SCRIPT" "$BUDGETS_FILE" "$TMP_DIR/ci-30-0.json"

# 4. PR CI median 29.9 min -> exit 0
cat > "$TMP_DIR/ci-29-9.json" <<'EOF'
{
  "coreImageUncompressedMB": 325,
  "coldStartP50Seconds": 3.0,
  "warmP99Milliseconds": 3.47,
  "ciPrWallClockMedianMinutes": 29.9
}
EOF
assert_exit 0 "PR CI median 29.9 min" bash "$CHECK_SCRIPT" "$BUDGETS_FILE" "$TMP_DIR/ci-29-9.json"

# 5. Missing metric -> exit 1
cat > "$TMP_DIR/missing.json" <<'EOF'
{
  "coreImageUncompressedMB": 325,
  "coldStartP50Seconds": 3.0,
  "warmP99Milliseconds": 3.47
}
EOF
assert_exit 1 "missing metric fails closed" bash "$CHECK_SCRIPT" "$BUDGETS_FILE" "$TMP_DIR/missing.json"

echo "All check-budgets.sh self-tests passed."
