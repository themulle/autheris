#!/usr/bin/env bash
# Opens or updates one GitHub issue per scheduled workflow when a scheduled run fails (report-failure job).
# The issue text carries only the run URL and the failed job ids: no logs, no environment, no credentials.
# Closing the issue stays manual. Pinned by ScheduledWorkflowFailureReportTests.
set -euo pipefail

: "${GH_TOKEN:?}" "${GITHUB_REPOSITORY:?}" "${GITHUB_WORKFLOW:?}" "${GITHUB_RUN_ID:?}" "${GITHUB_SERVER_URL:?}" "${NEEDS_JSON:?}"

label="ci-nightly-failure"
title="Scheduled workflow failed: ${GITHUB_WORKFLOW}"
run_url="${GITHUB_SERVER_URL}/${GITHUB_REPOSITORY}/actions/runs/${GITHUB_RUN_ID}"
failed_jobs="$(jq -r '[to_entries[] | select(.value.result == "failure") | .key] | join(", ")' <<<"$NEEDS_JSON")"
failed_jobs="${failed_jobs:-unknown}"
body="$(printf 'A scheduled run of this workflow failed.\n\nRun: %s\nFailed jobs: %s\n' "$run_url" "$failed_jobs")"

gh label create "$label" -R "$GITHUB_REPOSITORY" --color B60205 --description "A scheduled workflow run failed" >/dev/null 2>&1 || true

issue="$(gh issue list -R "$GITHUB_REPOSITORY" --state open --label "$label" --limit 100 --json number,title \
  | jq -r --arg t "$title" '[.[] | select(.title == $t)][0].number // empty')"

if [ -n "$issue" ]; then
  gh issue comment "$issue" -R "$GITHUB_REPOSITORY" --body "$body"
else
  gh issue create -R "$GITHUB_REPOSITORY" --title "$title" --label "$label" --body "$body"
fi
