# WP-SIMPL-35 Footprint budget job and published numbers `[WF]`
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-35-footprint-budget-job-and-published-numbers-wf) | Issues: #509 | Owner decisions: OD-17(8) (`[WF]` bundles approved) | Security conditions: none specific (general workflow hardening applies)

## 1. Goal and outcome
A CI workflow measures the core image footprint, cold start, warm latency and CI duration nightly and on release, compares them with `benchmarks/budgets.json`, fails when any metric exceeds its budget by more than 10 %, uploads the results as an artifact, and commits the results JSON per release. Measurable: the workflow runs green on main against the post-Stage-3 core image and a deliberately lowered budget makes it fail.

## 2. Preconditions and dependencies
- WP-SIMPL-10 merged (provides `benchmarks/protocol/README.md`, `benchmarks/scripts/footprint.sh`, `ci-duration.sh`, results naming `benchmarks/results/<metric>-<yyyy-mm-dd>-<sha7>.json`). Verified 63cbb893: `benchmarks/scripts/` and `benchmarks/protocol/` do not exist yet; `benchmarks/results/` has only `r2r-spike-report.md`.
- WP-SIMPL-26..30 merged (core image is the module-free `autheris` image whose budget applies).
- Parallel with WP-SIMPL-34.

## 3. Scope
### In scope
- `benchmarks/budgets.json` (new) and `benchmarks/scripts/check-budgets.sh` (new comparator).
- `.github/workflows/footprint-budget.yml` (new): `schedule` nightly, `workflow_dispatch`, `workflow_call` from `release.yml`.
- `release.yml`: call the job; commit results to the release branch/PR.
- Architecture test for budgets file shape.
### Out of scope
- Changing measurement methods (owned by WP-SIMPL-10).
- PR-gating on footprint (nightly + release only).
- Module (`autheris-full`) image budgets.

## 4. Current state (verified)
- Workflows: `ci.yml, codeql.yml, docker-publish.yml, governance-immutability.yml, parity-nightly.yml, policy-lint.yml, release.yml, simulation-nightly.yml, sql-compiler-gate-nightly.yml, trivy.yml` (`.github/workflows/`); nightly pattern to copy: `parity-nightly.yml`.
- No `benchmarks/budgets.json`. Baselines (PRD REQ-SIMPL-09.4): image 373 MB non-R2R, cold P50 4.52 s, warm P99 3.47 ms.

## 5. Implementation steps
1. Create `benchmarks/budgets.json`:
   ```json
   { "schemaVersion": 1, "tolerancePercent": 10,
     "metrics": {
       "coreImageUncompressedMB": { "budget": 250, "unit": "MB", "source": "footprint" },
       "coldStartP50Seconds":     { "budget": 3.0, "unit": "s",  "source": "footprint" },
       "warmP99Milliseconds":     { "budget": 3.47,"unit": "ms", "source": "footprint" },
       "ciPrMinutes":             { "budget": 15,  "unit": "min","source": "ci-duration" },
       "ciFullMinutes":           { "budget": 30,  "unit": "min","source": "ci-duration" } } }
   ```
   Field names must match the keys emitted by WP-SIMPL-10 scripts; if they differ, rename here, not in the scripts.
2. `benchmarks/scripts/check-budgets.sh <budgets.json> <result.json>...`: `set -euo pipefail`, uses `jq`; for each metric, fail (exit 1) when `value > budget * (1 + tolerance/100)`, fail when a metric is missing in results (fail closed); print a markdown table to `$GITHUB_STEP_SUMMARY` when set.
3. `.github/workflows/footprint-budget.yml`: `permissions: contents: read` (job-level `actions: read` for `gh run list`); pinned action SHAs as in existing workflows; `concurrency` group; steps: checkout, setup .NET as `ci.yml`, install podman, `footprint.sh`, `ci-duration.sh` (`GH_TOKEN: ${{ github.token }}`), `check-budgets.sh`, `actions/upload-artifact` of `benchmarks/results/*.json` (retention 90 days) with `if: always()`.
4. `release.yml`: add job `footprint` with `uses: ./.github/workflows/footprint-budget.yml`; release job `needs: footprint`; a follow-up step commits `benchmarks/results/*-<sha7>.json` via the existing release PR/branch mechanism (no direct push to main with elevated token if release.yml does not already do so; otherwise open a PR).
5. README/protocol: `benchmarks/protocol/README.md` gets a "Budgets" section linking `budgets.json` and the latest results file (system description, no status text).

## 6. Tests
- `tests/Autheris.Tests.Architecture`: `FootprintBudgetTests.Budgets_file_has_all_required_metrics_with_positive_budgets` (parse JSON, five keys, tolerance 10).
- `FootprintBudgetTests.Footprint_workflow_runs_nightly_and_on_release` (YAML text: `schedule`, `workflow_call`; `release.yml` references `footprint-budget.yml`).
- `FootprintBudgetTests.Workflow_permissions_are_read_only` (no `write` in `footprint-budget.yml`).
- Script self-test `benchmarks/scripts/check-budgets.test.sh` (run in ci.yml unit job): fixture within budget -> 0, +11 % -> 1, missing metric -> 1.

## 7. Verification commands
- `bash benchmarks/scripts/check-budgets.test.sh`
- `dotnet test tests/Autheris.Tests.Architecture -c Release --filter "FullyQualifiedName~FootprintBudgetTests"`
- `actionlint .github/workflows/footprint-budget.yml` (if available locally)
- Manual `workflow_dispatch` run on the branch; then once with `budget` lowered in a scratch commit to see it fail.

## 8. Acceptance criteria
- [ ] Nightly and release runs execute `footprint.sh`, `ci-duration.sh` and `check-budgets.sh`.
- [ ] Any metric > budget + 10 % or missing fails the job.
- [ ] Results JSON uploaded as artifact on every run and committed per release.
- [ ] Architecture tests and script self-test green.
- [ ] No published number in README/docs without a link to a results file (existing `DocsClaimsArchitectureTests` stays green).

## 9. PR slicing
Single PR (scripts/workflow/json, ~+150 lines, no src); label `[WF]`.

## 10. Risks, rollback, and things the developer must NOT do
- Runner noise on cold start: keep 20 runs and nearest-rank P50 (protocol); do not raise tolerance above 10 % without owner decision.
- Do not grant `contents: write` to the nightly; do not print `GH_TOKEN`.
- Rollback: delete workflow and `needs:` in release.yml.

## 11. Estimate
+150 lines (scripts/workflow/json) +80 test lines; 0 src; 2 person-days.

## Decisions taken in this spec
- Footprint is not a PR gate (plan says nightly + release).
- Missing metric = failure (fail closed).
- Results commit goes through the release PR mechanism, never a direct push from the nightly.
