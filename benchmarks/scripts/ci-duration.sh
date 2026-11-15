#!/usr/bin/env bash
# ==============================================================================
# WP-SIMPL-10 / WP-SIMPL-35: CI Duration Measurement Script
# Computes the median wall-clock duration (in minutes) across recent completed
# ci.yml pull_request and push runs via the GitHub Actions API, plus the nearest-rank
# p90 (the #523 closure gate is p90 < 30 min over 10 PR runs and 10 main runs).
#
# Usage: ci-duration.sh [--since YYYY-MM-DD] [--runs N]
#   --since  only count runs created on or after this UTC date (default: no lower bound)
#   --runs   keep at most N most recent runs per event type (default: 20, range 1..200)
# ==============================================================================
set -euo pipefail

SINCE=""
RUNS=20
while [ "$#" -gt 0 ]; do
  case "$1" in
    --since)
      [ "$#" -ge 2 ] || { echo "ci-duration: --since needs a value" >&2; exit 2; }
      SINCE="$2"
      shift 2
      ;;
    --runs)
      [ "$#" -ge 2 ] || { echo "ci-duration: --runs needs a value" >&2; exit 2; }
      RUNS="$2"
      shift 2
      ;;
    *)
      echo "ci-duration: unknown argument: $1" >&2
      exit 2
      ;;
  esac
done
if [ -n "$SINCE" ] && ! [[ "$SINCE" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}$ ]]; then
  echo "ci-duration: --since must be YYYY-MM-DD" >&2
  exit 2
fi
if ! [[ "$RUNS" =~ ^[0-9]+$ ]] || [ "$RUNS" -lt 1 ] || [ "$RUNS" -gt 200 ]; then
  echo "ci-duration: --runs must be an integer between 1 and 200" >&2
  exit 2
fi

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

calc_p90() {
  local arr=("$@")
  local n="${#arr[@]}"
  if [ "$n" -eq 0 ]; then
    echo "0.0"
    return
  fi
  local sorted
  mapfile -t sorted < <(printf '%s\n' "${arr[@]}" | sort -n)
  local rank
  rank=$(awk -v count="$n" 'BEGIN { r = 0.9 * count; printf "%d", (r == int(r) ? r : int(r) + 1) }')
  local idx=$((rank - 1))
  if [ "$idx" -lt 0 ]; then idx=0; fi
  if [ "$idx" -ge "$n" ]; then idx=$((n - 1)); fi
  echo "${sorted[$idx]}"
}

PR_DURATIONS=()
MAIN_DURATIONS=()

if command -v gh >/dev/null 2>&1 && [ -n "${GH_TOKEN:-${GITHUB_TOKEN:-}}" ]; then
  export GH_TOKEN="${GH_TOKEN:-${GITHUB_TOKEN:-}}"
  RUNS_JSON="$(gh run list --workflow ci.yml --status success --limit 200 --json event,createdAt,updatedAt 2>/dev/null || echo "[]")"
  # gh lists newest first, so the first RUNS rows per event type are the most recent ones.
  while IFS=$'\t' read -r event mins; do
    [ -z "$event" ] && continue
    if [ "$event" = "pull_request" ] && [ "${#PR_DURATIONS[@]}" -lt "$RUNS" ]; then
      PR_DURATIONS+=("$mins")
    elif [ "$event" = "push" ] && [ "${#MAIN_DURATIONS[@]}" -lt "$RUNS" ]; then
      MAIN_DURATIONS+=("$mins")
    fi
  done < <(echo "$RUNS_JSON" | jq -r --arg since "$SINCE" '.[] | select(.createdAt != null and .updatedAt != null) | select($since == "" or (.createdAt[0:10] >= $since)) | [ .event, (((.updatedAt | fromdateiso8601) - (.createdAt | fromdateiso8601)) / 60.0 | .*100 | round / 100) ] | @tsv')
fi

PR_MEDIAN="$(calc_median "${PR_DURATIONS[@]}")"
MAIN_MEDIAN="$(calc_median "${MAIN_DURATIONS[@]}")"
PR_P90="$(calc_p90 "${PR_DURATIONS[@]}")"
MAIN_P90="$(calc_p90 "${MAIN_DURATIONS[@]}")"
PR_RUN_COUNT="${#PR_DURATIONS[@]}"
MAIN_RUN_COUNT="${#MAIN_DURATIONS[@]}"

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
  --argjson pr_p90 "$PR_P90" \
  --argjson main_p90 "$MAIN_P90" \
  --argjson pr_runs "$PR_RUN_COUNT" \
  --argjson main_runs "$MAIN_RUN_COUNT" \
  --arg since "$SINCE" \
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
      main_median_minutes: $main_median,
      pr_p90_minutes: $pr_p90,
      main_p90_minutes: $main_p90
    },
    sample: {
      since: (if $since == "" then null else $since end),
      pr_runs: $pr_runs,
      main_runs: $main_runs
    },
    method: $method
  }' > "$OUT_FILE"

chmod 644 "$OUT_FILE"
echo "Results written to: $OUT_FILE"
