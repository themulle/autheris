#!/usr/bin/env bash
# ==============================================================================
# WP-SIMPL-10 / WP-SIMPL-35: CI Duration Measurement Script
# Computes the median wall-clock duration (in minutes) across recent completed
# ci.yml pull_request and push runs via the GitHub Actions API.
# ==============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

DATE="$(date -u +%Y-%m-%d)"
SHA="$(git rev-parse --short=7 HEAD)"
OUT_DIR="$ROOT/benchmarks/results"
OUT_FILE="$OUT_DIR/ci-duration-$DATE-$SHA.json"
mkdir -p "$OUT_DIR"

calc_median() {
  local arr=("$@")
  local n="${#arr[@]}"
  if [ "$n" -eq 0 ]; then
    echo "0.0"
    return
  fi
  local sorted
  mapfile -t sorted < <(printf '%s\n' "${arr[@]}" | sort -n)
  local rank
  rank=$(awk -v count="$n" 'BEGIN { r = 0.5 * count; printf "%d", (r == int(r) ? r : int(r) + 1) }')
  local idx=$((rank - 1))
  if [ "$idx" -lt 0 ]; then idx=0; fi
  if [ "$idx" -ge "$n" ]; then idx=$((n - 1)); fi
  echo "${sorted[$idx]}"
}

PR_DURATIONS=()
MAIN_DURATIONS=()

if command -v gh >/dev/null 2>&1 && [ -n "${GH_TOKEN:-${GITHUB_TOKEN:-}}" ]; then
  export GH_TOKEN="${GH_TOKEN:-${GITHUB_TOKEN:-}}"
  RUNS_JSON="$(gh run list --workflow ci.yml --status success --limit 20 --json event,createdAt,updatedAt 2>/dev/null || echo "[]")"
  while IFS=$'\t' read -r event mins; do
    [ -z "$event" ] && continue
    if [ "$event" = "pull_request" ]; then
      PR_DURATIONS+=("$mins")
    elif [ "$event" = "push" ]; then
      MAIN_DURATIONS+=("$mins")
    fi
  done < <(echo "$RUNS_JSON" | jq -r '.[] | select(.createdAt != null and .updatedAt != null) | [ .event, (((.updatedAt | fromdateiso8601) - (.createdAt | fromdateiso8601)) / 60.0 | .*100 | round / 100) ] | @tsv')
fi

PR_MEDIAN="$(calc_median "${PR_DURATIONS[@]}")"
MAIN_MEDIAN="$(calc_median "${MAIN_DURATIONS[@]}")"

HOSTNAME_VAL="$(hostname 2>/dev/null || echo "unknown")"
OS_VAL="$(uname -s 2>/dev/null || echo "unknown")"
KERNEL_VAL="$(uname -r 2>/dev/null || echo "unknown")"
ARCH_VAL="$(uname -m 2>/dev/null || echo "unknown")"
CPU_VAL="$(lscpu 2>/dev/null | grep 'Model name:' | sed 's/Model name:[[:space:]]*//' | head -n 1 || echo "unknown")"

jq -n \
  --arg metric "ci-duration" \
  --arg date "$DATE" \
  --arg sha "$SHA" \
  --arg hostname "$HOSTNAME_VAL" \
  --arg os "$OS_VAL" \
  --arg kernel "$KERNEL_VAL" \
  --arg arch "$ARCH_VAL" \
  --arg cpu "$CPU_VAL" \
  --arg method "bash benchmarks/scripts/ci-duration.sh" \
  --argjson pr_median "$PR_MEDIAN" \
  --argjson main_median "$MAIN_MEDIAN" \
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
      ciPrWallClockMedianMinutes: $pr_median,
      pr_median_minutes: $pr_median,
      main_median_minutes: $main_median
    },
    method: $method
  }' > "$OUT_FILE"

chmod 644 "$OUT_FILE"
echo "Results written to: $OUT_FILE"
