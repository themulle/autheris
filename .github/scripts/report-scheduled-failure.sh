#!/usr/bin/env bash
# Opens or updates one GitHub issue per scheduled workflow when a scheduled run fails (report-failure job).
# The issue text carries only the run URL and the failed job ids: no logs, no environment, no credentials.
# A re-run reporting the same run URL and failed jobs as the newest entry adds no comment. Closing the issue stays manual.
# Pinned by ScheduledWorkflowFailureReportTests.
set -euo pipefail

: "${GH_TOKEN:?}" "${GITHUB_REPOSITORY:?}" "${GITHUB_WORKFLOW:?}" "${GITHUB_RUN_ID:?}" "${GITHUB_SERVER_URL:?}" "${NEEDS_JSON:?}"

label="ci-nightly-failure"
title="Scheduled workflow failed: ${GITHUB_WORKFLOW}"
run_url="${GITHUB_SERVER_URL}/${GITHUB_REPOSITORY}/actions/runs/${GITHUB_RUN_ID}"
failed_jobs="$(jq -r '[to_entries[] | select(.value.result == "failure") | .key] | join(", ")' <<<"$NEEDS_JSON")"
failed_jobs="${failed_jobs:-unknown}"
body="$(printf 'A scheduled run of this workflow failed.\n\nRun: %s\nFailed jobs: %s\n' "$run_url" "$failed_jobs")"

# Only an existing label is tolerated; any other failure is a warning (issue creation then reports the real error).
if ! label_error="$(gh label create "$label" -R "$GITHUB_REPOSITORY" --color B60205 --description "A scheduled workflow run failed" 2>&1 >/dev/null)"; then
  case "$label_error" in
    *"already exists"*) ;;
    *) echo "::warning::Could not create the ci-nightly-failure label." ;;
  esac
fi

issue="$(gh issue list -R "$GITHUB_REPOSITORY" --state open --label "$label" --limit 100 --json number,title \
  | jq -r --arg t "$title" '[.[] | select(.title == $t)][0].number // empty')"

if [ -z "$issue" ]; then
  gh issue create -R "$GITHUB_REPOSITORY" --title "$title" --label "$label" --body "$body"
  exit 0
fi

newest="$(gh issue view "$issue" -R "$GITHUB_REPOSITORY" --json body,comments \
  --jq 'if (.comments | length) > 0 then .comments[-1].body else .body end')"
if grep -qxF "Run: ${run_url}" <<<"$newest" && grep -qxF "Failed jobs: ${failed_jobs}" <<<"$newest"; then
  echo "Issue #${issue} already reports this run."
else
  gh issue comment "$issue" -R "$GITHUB_REPOSITORY" --body "$body"
fi
