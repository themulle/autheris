#!/usr/bin/env bash
# ==============================================================================
# WP-SIMPL-35: Footprint & CI Duration Budget Comparator
# Compares measured footprint and CI duration JSON result files against
# benchmarks/budgets.json. Fails closed when any metric is missing or exceeds
# its allowed budget threshold.
# ==============================================================================
set -euo pipefail

if [ "$#" -lt 2 ]; then
  echo "Usage: $0 <budgets.json> <result.json>..." >&2
  exit 1
fi

BUDGETS_FILE="$1"
shift

if [ ! -f "$BUDGETS_FILE" ]; then
  echo "Error: budgets file not found: $BUDGETS_FILE" >&2
  exit 1
fi

for rf in "$@"; do
  if [ ! -f "$rf" ]; then
    echo "Error: result file not found: $rf" >&2
    exit 1
  fi
done

MERGED_JSON="$(jq -s '
  reduce .[] as $item ({};
    . * (
      if ($item | type) == "object" then
        (
          ($item | del(.metrics, .values, .host, .metric, .date, .sha, .method))
          + ($item.metrics // {})
          + ($item.values // {})
          + (
            if ($item.values.image_uncompressed_mb // null) != null and ($item.values.coreImageUncompressedMB // null) == null then
              { coreImageUncompressedMB: $item.values.image_uncompressed_mb }
            else {} end
          )
          + (
            if ($item.values.cold_p50_ms // null) != null and ($item.values.coldStartP50Seconds // null) == null then
              { coldStartP50Seconds: ($item.values.cold_p50_ms / 1000.0) }
            else {} end
          )
          + (
            if ($item.values.warm_p99_ms // null) != null and ($item.values.warmP99Milliseconds // null) == null then
              { warmP99Milliseconds: $item.values.warm_p99_ms }
            else {} end
          )
          + (
            if ($item.values.pr_median_minutes // null) != null and ($item.values.ciPrWallClockMedianMinutes // null) == null then
              { ciPrWallClockMedianMinutes: $item.values.pr_median_minutes }
            else {} end
          )
        )
      else {} end
    )
  )
' "$@")"

GLOBAL_TOLERANCE="$(jq -r '.tolerancePercent // 0' "$BUDGETS_FILE")"
METRIC_KEYS="$(jq -r '.metrics | keys[]' "$BUDGETS_FILE")"

SUMMARY_LINES=(
  "### Footprint & CI Budget Summary"
  ""
  "| Metric | Measured | Budget | Limit | Status |"
  "|---|---:|---:|---:|---|"
)

FAILED=0

for key in $METRIC_KEYS; do
  BUDGET="$(jq -r --arg k "$key" '.metrics[$k].budget' "$BUDGETS_FILE")"
  UNIT="$(jq -r --arg k "$key" '.metrics[$k].unit // ""' "$BUDGETS_FILE")"
  TOLERANCE="$(jq -r --arg k "$key" --arg def "$GLOBAL_TOLERANCE" '.metrics[$k].tolerancePercent // ($def | tonumber)' "$BUDGETS_FILE")"
  COMPARISON="$(jq -r --arg k "$key" '.metrics[$k].comparison // "maxWithTolerance"' "$BUDGETS_FILE")"

  HAS_VALUE="$(echo "$MERGED_JSON" | jq -r --arg k "$key" 'has($k) and (.[$k] != null)')"
  if [ "$HAS_VALUE" != "true" ]; then
    echo "FAIL: required metric '$key' is missing from result files." >&2
    SUMMARY_LINES+=("| \`$key\` | MISSING | ${BUDGET} ${UNIT} | - | FAIL (missing) |")
    FAILED=1
    continue
  fi

  VALUE="$(echo "$MERGED_JSON" | jq -r --arg k "$key" '.[$k]')"

  if [ "$COMPARISON" = "lessThan" ]; then
    LIMIT="$BUDGET"
    LIMIT_LABEL="< ${LIMIT} ${UNIT}"
    PASS="$(awk -v v="$VALUE" -v b="$BUDGET" 'BEGIN { print (v + 0 < b + 0) ? "1" : "0" }')"
  else
    LIMIT="$(awk -v b="$BUDGET" -v t="$TOLERANCE" 'BEGIN { printf "%.4f", b * (1.0 + (t / 100.0)) }')"
    LIMIT_LABEL="<= ${LIMIT} ${UNIT} (+${TOLERANCE}%)"
    PASS="$(awk -v v="$VALUE" -v l="$LIMIT" 'BEGIN { print (v + 0 <= l + 0) ? "1" : "0" }')"
  fi

  if [ "$PASS" = "1" ]; then
    echo "PASS: $key = ${VALUE} ${UNIT} (budget: ${BUDGET} ${UNIT}, limit: ${LIMIT_LABEL})"
    SUMMARY_LINES+=("| \`$key\` | ${VALUE} ${UNIT} | ${BUDGET} ${UNIT} | ${LIMIT_LABEL} | PASS |")
  else
    echo "FAIL: $key = ${VALUE} ${UNIT} exceeds budget ${BUDGET} ${UNIT} (limit: ${LIMIT_LABEL})" >&2
    SUMMARY_LINES+=("| \`$key\` | ${VALUE} ${UNIT} | ${BUDGET} ${UNIT} | ${LIMIT_LABEL} | FAIL |")
    FAILED=1
  fi
done

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  printf '%s\n' "${SUMMARY_LINES[@]}" >> "$GITHUB_STEP_SUMMARY"
fi

if [ "$FAILED" -ne 0 ]; then
  exit 1
fi
