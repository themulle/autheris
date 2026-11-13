# PLAN-OI-CI: Open CI issues (#518-#523, #609, #611)

- Date: 2026-11-13 · Base: `origin/main` @ `10400ec4` · Plan status: PLANNED (Phase 2 done, Phase 3 pending for WP-OI-CI-5, -7, -8)
- Origin plan: [`00-master-plan-overview.md`](00-master-plan-overview.md) · WP specs: [`simplification/README.md`](simplification/README.md) · PRD [`2026-11-05-req-project-simplification.md`](2026-11-05-req-project-simplification.md) section 2.0 is binding (OD-1..OD-45; a later OD wins)
- Rules: PR limit 400 net added `src/` lines (PRD 5.3; tests, scripts and workflows do not count, but every PR stays one coherent change). Every PR that edits `.github/workflows/**` is `[WF]` and needs owner approval (OD-17 approved the bundles below). CI and test-infrastructure work is allowed under the feature freeze (PRD 5.1: "release/CI work for this program"); no PR here adds an endpoint, option or `*Enabled` flag.
- Binding inputs: OD-6/OD-37 (target: PR wall clock from push to last required check, median, queue included, < 30 min), OD-12 (Oracle and Databricks stay; full suites nightly, security-tagged smoke subset on PRs), Q18/Q21 (Spark container is the Databricks fallback until real-workspace secrets exist), simplification plan sequencing rules 1, 6 and 8.
- Fail-closed rule for this cluster: no `[Trait("Category","Security")]` test leaves PR CI, no path filter can skip a security-relevant change, and no PR lowers `Baselines/security-tests-base.txt` (`TestSuiteRatchetTests.Security_category_count_does_not_decrease`).

## Summary

| Issue | Verdict | WP / spec | Open on main | Size | Phase 3 |
|---|---|---|---|---|---|
| #518 | NEW PLAN | WP-OI-CI-1 `[WF]` | all of it: per-SHA groups, no changes job, 4 workflows without concurrency | S | no |
| #519 | PARTLY DONE, rest NEW PLAN | WP-OI-CI-2 `[WF]` (bundle B with #521) | weights refreshed by hand (`137c71b1`); script, default weight, 6 shards, weekly refresh open | S | no |
| #520 | PARTLY DONE, rest NEW PLAN | WP-OI-CI-3a..3c (tests only) | 49 classes still derive a host per test; no architecture test | M | no |
| #521 | NEW PLAN | WP-OI-CI-4 `[WF]` (bundle B) | no pre-pull; SQL Server `2022-latest` floating in 3 fixtures; `postgres:16-alpine` not digest-pinned | S | no |
| #522 | PARTLY COVERED | WP-SIMPL-21 step 4 and 8 (PR 21-4) + WP-OI-CI-5 `[WF]` | no `Oracle`/`Stress` traits, Spark job unconditional on PRs, no integration nightly | M | yes (PR security-test selection) |
| #523 | UMBRELLA, PARTLY DONE | WP-SIMPL-10 (`ci-duration.sh`), WP-OI-CI-6 | #516/#517 merged (#524); target not yet measured | S | no |
| #609 | PARTLY DONE, rest NEW PLAN | WP-SIMPL-37 / Q21, WP-OI-CI-7 | G9 job and `databricks-g9` environment exist; no secrets, no `Category=DatabricksLive` test, no `engine=` record | M | yes (secrets, live workspace) |
| #611 | NEW PLAN (WP-SIMPL-11 follow-up) | WP-OI-CI-8 `[WF]` | `simulation-nightly.yml` has no PostgreSQL service; Schemathesis job failed on each run 2026-11-10..12 | S | yes (TLS policy) |

Measured state (gh, 2026-11-13): of the last 30 `ci.yml` runs 8 succeeded, 17 failed, 4 were cancelled (failures not triaged into PR-code failures vs flakes). Successful runs took 31-58 min wall clock; in the latest green run the jobs took shard 1/4 31 min, shards 2-4 21-22 min, Spark 6, Build & Test 5, SQL gate 3. The critical path is now about 31 min plus queue, down from 50-83 min at issue intake.

## #518 CI: coalesce main-branch runs and skip .NET jobs for docs/benchmark-only changes

- Verdict: NEW PLAN. The assessment bundled it with #517, but #517 merged alone in #524.
- Problem: every push to `main` gets its own concurrency group, so merge bursts queue N full runs. Docs-only and benchmark-only commits run the full suite, and four PR workflows never cancel superseded runs.
- Verified state: `.github/workflows/ci.yml:12-14` and `codeql.yml:14-16` use `group: ...-${{ github.event.pull_request.number || github.sha }}`. `trivy.yml`, `docker-publish.yml`, `governance-immutability.yml` and `policy-lint.yml` have no `concurrency:` block. `ci.yml` has no `changes` job and no job-level path condition. `.github/dependabot.yml` groups only `github-actions` and `nuget`; the `docker` entries for `/benchmarks/load/docker` and `/deploy/containers/gqlgateway-api` and `docker-compose` are ungrouped.
- Root cause: the group key uses `github.sha` for pushes, and there is no change classification.
- Design:
  1. `ci.yml`, `codeql.yml`, `trivy.yml`: `group: ${{ github.workflow }}-${{ github.event.pull_request.number || github.ref }}`, `cancel-in-progress: ${{ github.event_name == 'pull_request' }}`. On `main` a started run is never cancelled; only pending runs are superseded (at most 1 running + 1 pending).
  2. A `changes` job runs `.github/scripts/ci-changes.py` on `git diff --name-only <base>...<head>` and outputs `dotnet=true|false`. It uses a positive list of doc-only globs: `docs/**`, `**/*.md`, `benchmarks/**`, `deploy/**` except `deploy/helm/**`, `.github/ISSUE_TEMPLATE/**`, `LICENSE`. Any other path, an empty diff, a diff error or an unknown event returns `dotnet=true` (fail-closed). `build-and-test`, `integration-tests`, `spark-integration-tests` and the CodeQL analyze job use a job-level `if: needs.changes.outputs.dotnet == 'true'`, so required checks still report "skipped = success". `sql-compiler-gate` and `helm-render` stay unfiltered.
  3. PR-only concurrency with `cancel-in-progress: true` in `docker-publish.yml` (only for `pull_request`; tag and `workflow_run` runs keep unique groups via `github.run_id`), `governance-immutability.yml` and `policy-lint.yml`.
  4. Dependabot: one weekly group per ecosystem for `/benchmarks/**` and `/deploy` (owner question Q-CI-2).
- Work package: WP-OI-CI-1, one `[WF]` PR (3 workflows + 4 concurrency blocks + script + dependabot), about 120 lines, no `src/`.
- Tests and verification: `.github/scripts/test_ci_changes.py` (`python3 -m unittest`, run in `build-and-test`): `test_docs_only_skips_dotnet`, `test_helm_chart_change_runs_dotnet`, `test_src_change_runs_dotnet`, `test_mixed_docs_and_src_runs_dotnet`, `test_empty_or_failed_diff_runs_dotnet` (fail-closed), `test_workflow_change_runs_dotnet`. New `tests/Autheris.Tests.Architecture/CiWorkflowArchitectureTests.cs` (uses `RepositoryScan.ReadText`): `Main_runs_coalesce_on_ref_not_sha`, `Every_pull_request_workflow_cancels_superseded_pr_runs`, `Sql_compiler_gate_has_no_path_condition` `[Security]`, `Docs_only_filter_never_lists_src_tests_or_helm` `[Security]`. Acceptance (issue): a burst of 5 merges produces at most 2 executing CI runs; a `docs/**`-only PR finishes CI in < 3 min with all required checks green.
- Dependencies: none (#516/#517 merged). Precedes WP-OI-CI-2 (6 shards need the slot headroom).
- Risks: a `main` failure can map to 2+ commits (each was green on its PR merge ref). `docker-publish` `workflow_run` publishes only the newest `main` commit (Q-CI-1). A wrongly broad glob could skip code; the positive list and the `[Security]` architecture tests guard against this.
- Size: S (~120 workflow/script lines, ~80 test lines, 1 PR).

## #519 CI: rebalance integration shards by measured durations and move to 6 shards

- Verdict: PARTLY DONE; the rest is a NEW PLAN. The assessment puts it in `[WF]` bundle B with #521, and WP-SIMPL-21 lists it as a precondition.
- Verified state: `tests/Autheris.Tests.Integration/ci-shard-weights.json` was recalibrated by hand in `137c71b1` (2026-11-07, "balanced ~16.6 minute shards"; 203 entries, up from 93). `.github/scripts/integration-shard-filter.py:17` still has `DEFAULT_WEIGHT = 10.0` and no coverage warning. `ci.yml:92-120` still uses `shard: [1, 2, 3, 4]` and `... "$((SHARD - 1))" 4`. There is no `update-shard-weights.py` and no scheduled refresh. Latest green run: shards at 31/21/21/22 min, so the shards are about 1.45x unbalanced and shard 1 is the critical path.
- Root cause: the weights are maintained by hand and drift with every deletion WP and with host-reuse changes (#520). Unweighted classes are under-priced.
- Design:
  1. `.github/scripts/update-shard-weights.py`: parses the TRX files of the `test-results-integration-*` artifacts. Per class, the weight is the wall-clock span from first test start to last test end (this includes the class fixture), averaged over the given runs and rounded. It writes `ci-shard-weights.json` deterministically (sorted keys).
  2. The filter's `DEFAULT_WEIGHT` becomes the mean measured weight read from the file (not a constant). The script prints a `::warning::` when unweighted classes exceed 10 % of the total weight.
  3. 6 shards in `ci.yml` (matrix `[1..6]`, `... 6`, job name `shard N/6`). The last shard stays the exact complement.
  4. Weekly refresh job in `parity-nightly.yml`: it downloads the last 3 green `main` artifacts, runs the script and opens a PR (`GITHUB_TOKEN` with `contents: write`, `pull-requests: write` scoped to that job) when a shard deviates more than 20 % from the mean.
- Work package: WP-OI-CI-2, in `[WF]` bundle B (one PR together with WP-OI-CI-4), after WP-OI-CI-1.
- Tests and verification: `.github/scripts/test_integration_shard_filter.py`: `test_every_class_in_exactly_one_shard_for_4_and_6`, `test_last_shard_is_exact_complement`, `test_unknown_class_lands_in_last_shard`, `test_default_weight_is_mean_of_file`, `test_warning_when_unweighted_share_above_10_percent`. `test_update_shard_weights.py` with 2 checked-in mini TRX fixtures: `test_weight_is_first_start_to_last_end_span`, `test_output_is_sorted_and_stable`. Acceptance: the weights cover >= 95 % of measured integration time; over 3 green runs the slowest shard is <= 1.3x the median and <= 30 min. The executed test count per run is unchanged (TRX totals before and after).
- Dependencies: WP-OI-CI-1 (job slots). Rerun the script after each deletion WP and after WP-OI-CI-3 (simplification rule 8).
- Risks: 2 extra shards cost about 2 x 1.5 min of restore and build runner time. Weights depend on a class's position in the shard until #520 is done.
- Size: S (~150 script lines, ~120 script-test lines, part of 1 PR).

## #520 Integration tests: reuse test hosts instead of one WebApplicationFactory per test

- Verdict: PARTLY DONE; the rest is a NEW PLAN. Simplification rule 6 made it a hard dependency of WP-SIMPL-11, but WP-SIMPL-11 merged anyway (PRs #567-#585). It is still a dependency of WP-SIMPL-22.
- Verified state: `137c71b1` moved the heaviest classes to a shared fixture: `Transports/TransportEnforcementTests.cs` (fixture with `ReloadableConfig` at :48-:109, :812) and `Transports/IngressStampingTests.cs`, plus OData and removal tests. Still open: 49 files take `IClassFixture<WebApplicationFactory<Program>>` and call `WithWebHostBuilder` (76 files use `WithWebHostBuilder`, 15 use `new WebApplicationFactory<Program>`). 12 of these files have no `Dispose`, `await using` or `IAsyncLifetime`. `tests/Autheris.Tests.Architecture` has no rule against per-test hosts.
- Root cause: the constructor-level `WithWebHostBuilder` creates one host per test instance, and the derived factories stay alive until the class ends. Background services pile up and slow later tests in the shard.
- Design (test code only, no `src/` change and no new production option, so the freeze ratchets do not grow):
  1. `tests/Autheris.Tests.Common/Hosting/IntegrationHost<TConfig>`: a `WebApplicationFactory<Program>` subclass that takes its configuration once in `ConfigureWebHost`. It uses `ConfigureTestServices` to remove `IHostedService` registrations listed in a test-side allow-list (search-index warm-up, recertification cycle, traffic drain), but never the startup validators or the security checks. It sets the console log level to Warning. One fixture per configuration replaces each constructor `WithWebHostBuilder`. Tests that vary configuration share `ReloadableConfig`, which moves from `TransportEnforcementTests` into Common.
  2. Per-test hosts that are really needed (startup fail-closed checks) use `await using`.
  3. Isolation: unique tenant and object names per test, no reliance on test order.
- Work packages (tests only; each PR <= ~1,500 test lines for reviewability):
  - WP-OI-CI-3a: `IntegrationHost`, `ReloadableConfig` in Common, the architecture test (allow-list baseline of the current 49 files, which may only shrink), and the 6 costliest remaining classes: `RowFilterEvaluationEndpointAuthTests`, `DefaultTenantIngressHttpTests`, `GovernedWebSqlIntegrationTests`, `Issue341GraphQlInternalErrorTests`, `WalkingSkeletonIntegrationTests`, `QueryDefenseStressTests` (verify the current costs from TRX first).
  - WP-OI-CI-3b, 3c: the remaining classes, about 20 per PR, ordered by the measured weight in `ci-shard-weights.json`. The allow-list baseline goes to 0 in 3c.
- Tests and verification: `CiWorkflowArchitectureTests.Integration_test_constructors_do_not_call_WithWebHostBuilder` (baseline ratchet `Baselines/per-test-host-allowlist.txt`), `.Created_factories_are_disposed` (every `new WebApplicationFactory<Program>` or `WithWebHostBuilder` result is in an `await using`, a `using` or a disposed field). `IntegrationHostTests.Startup_validators_and_security_checks_are_never_removed` `[Security]` (the removal list cannot contain an `IStartupFilter` or validator). Per PR: the TRX count of executed tests per class and of `Category=Security` is unchanged, compared in the PR description. `TestSuiteRatchetTests.Security_category_count_does_not_decrease` stays green. Acceptance: fewer than 200 `Node is ready for traffic` lines per CI run; summed integration test time <= 60 min; per-test time of a class varies < 2x between the start and the end of a shard.
- Dependencies: before WP-SIMPL-22. Rerun WP-OI-CI-2 weights afterwards. Independent of the `[WF]` bundles.
- Risks: state leaking between tests in a shared host (tenants, catalog, rate-limit counters). Mitigation: unique names, and the 12 security classes keep their deny assertions per test, unchanged. Removing a hosted service could hide a fail-closed startup behaviour; the allow-list and the `[Security]` test prevent this.
- Size: M (~3,000 test lines changed in total, net negative; 3 PRs; 0 `src/`).

## #521 CI: pre-pull Testcontainers images in parallel with the build and pin the SQL Server image

- Verdict: NEW PLAN (nothing merged). In `[WF]` bundle B with #519. WP-SIMPL-21 step 8 lists the PG, SQL Server and Oracle pre-pull and reuses this WP.
- Verified state: `ci.yml` pre-pulls only Spark (`:165`). `mcr.microsoft.com/mssql/server:2022-latest` is used in `tests/Autheris.Tests.Integration/SharedDatabaseServers.cs:47`, `tests/Autheris.Tests.SqlCompilerGate/Databases/Databases.cs:69` and `tests/Autheris.Tests.Parity/Dialects/ParitySqlServerHostFixture.cs:42`. `Governance/GovernanceSqlServerFixture.cs:40` pins `2022-CU14-ubuntu-22.04` by tag only. `postgres:16-alpine` (4 places) is pinned by tag only. Oracle is pinned by digest (`OracleTestFixtures.cs:24`, `Databases.cs:94`). There is no central image list.
- Design:
  1. `tests/Autheris.Tests.Common/TestImages.cs`: `const` strings `PostgreSql`, `SqlServer` (one CU tag plus `@sha256` digest, the same for governance and data fixtures), `Oracle`, `Spark`, `Ryuk`. All fixtures reference it.
  2. `tests/test-images.txt` is generated from `TestImages` (one image per line). The workflow step `Pre-pull Testcontainers images (background)` reads it, pulls in parallel into `$RUNNER_TEMP/prepull.log`, and a `Wait for image pre-pull` step runs before `dotnet test --no-build`, after an explicit `dotnet build` step.
  3. Drop `--verbosity normal` for the integration shards (TRX stays the artifact).
- Work package: WP-OI-CI-4 (bundle B, one `[WF]` PR with WP-OI-CI-2).
- Tests: `TestImagesArchitectureTests.Every_testcontainers_image_comes_from_TestImages` (no `Builder("...")` string literal outside `TestImages.cs`), `.Every_image_is_digest_pinned` (`@sha256:` required), `.Workflow_prepull_list_matches_TestImages` (compares `test-images.txt` with the constants). Acceptance: no `Docker image ... created` line in the test-step logs, shard wall time down by >= 1 min over 3 runs.
- Dependencies: WP-OI-CI-1. It coordinates with WP-SIMPL-21 PR 21-4 (whichever merges second rebases; no duplicate pull steps).
- Risks: the SQL Server CU bump can change engine behaviour in the parity and governance tests (pick the CU that `GovernanceSqlServerFixture` already uses). Background pulls compete for disk on 4-vCPU runners (check the step timings).
- Size: S (~60 test-infrastructure lines, ~40 workflow lines).

## #522 CI: move non-security Oracle, Stress and Spark suites to a nightly workflow with path-triggered PR smoke runs

- Verdict: PARTLY COVERED. WP-SIMPL-21 step 4 adds `[Trait("Category","Oracle")]` "so #522 can route them". Its step 8 (PR 21-4, `[WF]`) removes `spark-integration-tests` from `ci.yml` and adds the nightly `parity-oracle` and `parity-databricks` jobs. The Stress tag, the integration nightly, the PR smoke triggers and the security guard are not specified there and are planned here.
- Verified state: no test carries `Category=Oracle` or `Category=Stress`. `integration-shard-filter.py:18` has `BASE_FILTER = "Category!=Spark&Category!=Parity"`. `spark-integration-tests` (`ci.yml:143`) runs on every PR with no condition. There is no `integration-nightly.yml`. `parity-nightly.yml` has only `parity-sqlite` and `parity-dialects`. 105 integration files carry `Category=Security`.
- Design:
  1. Traits: tag the Oracle integration classes listed in the issue (`OracleGatewayPathTests`, `OracleGovernedEndToEndTests`, `AstCompilerOracleExecutionTests`, `AstCompilerOracleDmlExecutionTests`, `OracleIntegrationTests`, `RowFilterKeySetOracleTests`) `Oracle`, and `QueryDefenseStressTests` `Stress`. If WP-SIMPL-21 PR 21-1..21-3 has already added the Oracle traits, reuse them.
  2. PR filter: `BASE_FILTER = "Category!=Spark&Category!=Parity&((Category!=Oracle&Category!=Stress)|Category=Security)"`. A security-tagged Oracle or Stress test stays on PRs (OD-12 smoke subset).
  3. The `changes` job (WP-OI-CI-1) adds outputs `oracle` (`**/*Oracle*`, Oracle emitter and fixtures) and `spark` (`**/*Spark*`, `**/*Databricks*`, Databricks/Spark dialect files, Spark fixtures). Path-triggered PR jobs `oracle-smoke` (`Category=Oracle`) and `spark-integration-tests` run when those outputs are true and report skipped otherwise. Unknown classification means run (fail-closed). On `main` and nightly they run unconditionally.
  4. New `integration-nightly.yml` (cron `0 1 * * *` + `workflow_dispatch`): runs `Category=Oracle|Category=Stress|Category=Spark` with the same `timeout-minutes` and `--blame-hang-timeout` as `ci.yml`, uploads TRX, and opens or updates one issue labelled `nightly-failure` on failure (`issues: write` only in that job).
- Work package: WP-OI-CI-5, one `[WF]` PR, merged together with or right after WP-SIMPL-21 PR 21-4 (one owner approval for both; Q-CI-3).
- Tests: `CiWorkflowArchitectureTests.Security_tests_are_never_excluded_by_the_pr_filter` `[Security]`: it evaluates `BASE_FILTER` against every class with `Category=Security` (reflection over the Integration assembly) and fails if any is excluded. `.No_security_class_is_only_in_a_nightly_category` `[Security]`, `.Oracle_and_spark_pr_jobs_run_when_changes_unknown` `[Security]`, `.Integration_nightly_uploads_trx_and_reports_failures`. Acceptance: no `oracle-free` image in shard logs unless Oracle paths changed; the TRX count of `Category=Security` per PR run is unchanged before and after; an Oracle dialect change on a PR runs `oracle-smoke`.
- Dependencies: WP-OI-CI-1 (`changes` job); WP-SIMPL-21 PR 21-4 (Spark job removal; avoid two edits of the same job); WP-OI-CI-2 (rerun weights after the move).
- Risks: Oracle-only or Spark-only regressions surface up to 24 h later (mitigated by the path-triggered smoke jobs and by the SQL compiler gate G6, which keeps executing Oracle on PRs). A nightly failure without an owner rots; the CODEOWNERS Infrastructure/CI owner (PRD 5.2) triages the `nightly-failure` issue.
- Size: M (~30 trait lines, ~120 workflow lines, ~150 architecture-test lines; 1 PR).

## #523 CI under 30 minutes (umbrella)

- Verdict: UMBRELLA, PARTLY DONE. #516 and #517 merged in #524 (`4070d278`, timeouts in every `ci.yml` job: 20/15/90/85/15/12/10 min and `--blame-hang-timeout 5m`). The remaining children are WP-OI-CI-1..5. Measurement belongs to WP-SIMPL-10 (`benchmarks/scripts/ci-duration.sh` exists; `benchmarks/results/` has no `ci-duration-*.json` yet).
- Verified state: green runs take 31-58 min wall clock (queue included), and the critical shard takes about 31 min. 17 of the last 30 runs failed (not triaged).
- Design: WP-OI-CI-6 is the closure gate. After WP-OI-CI-1, -2 and -4: run `ci-duration.sh --since <merge date> --runs 10` for PR and `main` events and commit `benchmarks/results/ci-duration-<date>-<sha7>.json`. Repeat after WP-OI-CI-3c and -5. The issue closes when the p90 for 10 consecutive PR runs and 10 `main` runs is < 30 min and no job ends by timeout. WP-SIMPL-35 later enforces the budget.
- Tests: the JSON result is the evidence. `CiWorkflowArchitectureTests.Every_ci_job_has_a_timeout` (keeps the #517 guarantee).
- Dependencies: WP-OI-CI-1..5; WP-SIMPL-10 PR 2 owns the script.
- Risks: a high failure rate inflates wall clock through reruns. Recommend a separate flake-triage item (Q-CI-4).
- Size: S (measurement and 1 results commit; ~20 test lines).

## #609 CI: Databricks real-workspace parity suite and nightly secrets provisioning

- Verdict: PARTLY DONE (the workflow part); the test suite is a NEW PLAN, and secrets are an owner action. Specified by OD-17(3), Q21 and WP-SIMPL-37 (the real-workspace nightly comes with credentials). WP-SIMPL-21 step 8 names `parity-databricks`.
- Verified state: `sql-compiler-gate-nightly.yml:198-247` has job `sql-compiler-gate-databricks-g9` with `environment: databricks-g9` (required reviewers, `main` only, per the comment at :195). It skips with a notice when the secrets are missing (latest run "success" = skipped). It fails closed when no `Category=DatabricksLive` test matches. No test in `tests/` carries `DatabricksLive`, and `parity-report.py` writes no `engine=databricks|spark` field.
- Design:
  1. Owner: create the workspace, a service principal with OAuth M2M scoped to one test catalog, read-only on fixture schemas plus a scratch schema, and the 5 secrets in `databricks-g9` (Q-CI-5).
  2. WP-OI-CI-7a (tests only): `tests/Autheris.Tests.SqlCompilerGate/Databricks/DatabricksLiveGateTests.cs` `[Trait("Category","DatabricksLive")]`. It seeds an ephemeral schema named per run id, runs the G6 differential cases against the SQL warehouse through the production provider (OAuth M2M, WP-SIMPL-37), and drops the schema in `DisposeAsync`. Its security cells check RLS, masking, consent, DDL/DML denial and bind-by-name.
  3. WP-OI-CI-7b `[WF]`: the parity results file records `engine=databricks` when the G9 secrets are present and `engine=spark` otherwise; `parity-report.py` prints the engine.
- Tests: `DatabricksLiveGateTests.Differential_cases_match_reference(caseId)`, `.Row_filter_and_mask_are_applied_on_warehouse` `[Security]`, `.Ddl_and_dml_are_denied_before_reaching_the_warehouse` `[Security]`, `.Ephemeral_schema_is_dropped_even_on_failure`. `TestInfrastructureTests.No_databricks_spark_or_oracle_credentials_in_repo_and_secrets_only_in_nightly_workflow` `[Security]` (WP-SIMPL-21, reused). `CiWorkflowArchitectureTests.Databricks_secrets_only_in_environment_gated_job` `[Security]`.
- Dependencies: WP-SIMPL-37 (provider, merged PR B/C); the owner provisions the secrets. Independent of the PR CI work.
- Risks: workspace cost and flakiness (warehouse cold start: use a serverless warehouse with auto-stop, and `timeout-minutes: 90` already exists). Secret exposure (environment reviewers, no `pull_request` trigger, masked logs).
- Size: M (~400 test lines, ~20 workflow lines; 2 PRs; 0 `src/`).

## #611 CI: add PostgreSQL service and AUTHERIS_SIM_GOVERNANCE_DB to simulation-nightly.yml

- Verdict: NEW PLAN (a follow-up recorded in WP-SIMPL-11 section "Follow-ups recorded during delivery", with no PR).
- Verified state: `tests/Autheris.Sim.ApiHost/SimApiHostMain.cs:83-88` exits with code 2 when `AUTHERIS_SIM_GOVERNANCE_DB` is unset. The `sim-api-fuzz` job (`simulation-nightly.yml:108`) sets no such variable and has no `services:`. The Schemathesis job failed on each of its runs on 2026-11-10, -11 and -12 (log cause not re-read; consistent with exit code 2). `src/Autheris.Infrastructure/Persistence/ConnectionTlsPolicy.cs:34-40` requires `SSL Mode=VerifyFull`/`VerifyCA` for PostgreSQL outside Development, and the sim host sets no environment name, so it runs as Production.
- Design (default, Q-CI-6): the job starts `postgres:16-alpine` (digest from `TestImages`) as a step container on loopback with TLS. It generates a throwaway CA and server certificate in the job, mounts them, and sets `AUTHERIS_SIM_GOVERNANCE_DB="Host=127.0.0.1;...;SSL Mode=VerifyFull;Root Certificate=$RUNNER_TEMP/ca.crt"` with a random per-run password (`openssl rand`), never a secret. The host keeps its Production policy, so the fuzzer exercises the real configuration. `ConnectionTlsPolicy` stays unchanged.
- Work package: WP-OI-CI-8, one small `[WF]` PR, independent (can go first).
- Tests: `SimulationIsolationArchitectureTests.Sim_api_job_provides_governance_db_with_verify_full` `[Security]` (the workflow contains the variable, `VerifyFull` and no `ASPNETCORE_ENVIRONMENT=Development`). Verification: one green `workflow_dispatch` run with `job=api`.
- Dependencies: WP-OI-CI-4 (image constant) is optional; use the tag pin until then.
- Risks: certificate generation adds about 10 s. Running the sim host as Development instead would silently fuzz a different configuration (rejected as the default).
- Size: S (~40 workflow lines, ~30 test lines).

## Delivery order

1. WP-OI-CI-8 (#611) and WP-OI-CI-1 (#518): independent `[WF]` PRs.
2. WP-OI-CI-3a (#520, tests only), in parallel with step 1.
3. Bundle B `[WF]`: WP-OI-CI-2 (#519) + WP-OI-CI-4 (#521).
4. WP-OI-CI-5 (#522) with or right after WP-SIMPL-21 PR 21-4 `[WF]`.
5. WP-OI-CI-3b, 3c; rerun the shard weights.
6. WP-OI-CI-6 (#523 measurement and closure). WP-OI-CI-7 (#609) runs whenever the owner has provisioned the secrets.

## Open owner questions

| Id | Question | Recommended default |
|---|---|---|
| Q-CI-1 | Coalescing on `main`: only the newest commit of a burst gets a CI run and is published by `docker-publish` (`workflow_run`). Accept? | Yes. Every commit passed its PR run on the merge ref; release tags still run their own workflow |
| Q-CI-2 | Group the Dependabot `docker`/`docker-compose` updates for `/benchmarks/**` and `/deploy` into one weekly PR? | Yes, one group per ecosystem; the `nuget` and `github-actions` groups stay as they are |
| Q-CI-3 | Approve WP-OI-CI-5 (#522) and WP-SIMPL-21 PR 21-4 as one `[WF]` approval, including the new `integration-nightly.yml` and its `issues: write` permission? | Yes; failures go to one rolling `nightly-failure` issue owned by the CI CODEOWNER |
| Q-CI-4 | 17 of the last 30 CI runs failed. Open a separate flake-triage item before measuring #523? | Yes; #523 is measured on runs after the triage, and flaky tests are quarantined only with an issue link, never `Category=Security` tests |
| Q-CI-5 | #609: Databricks workspace, service principal scope and cost ceiling | One serverless SQL warehouse with auto-stop, an OAuth M2M principal limited to one test catalog (fixture schemas read-only, plus a scratch schema), nightly only; the Spark container stays the fallback (Q21) |
| Q-CI-6 | #611: TLS PostgreSQL with a job-generated CA, or `ASPNETCORE_ENVIRONMENT=Development` for the sim host? | TLS with `VerifyFull`; never relax `ConnectionTlsPolicy` and never run the fuzzed host as Development |

## Phase 3 review scope

- WP-OI-CI-5 (#522): the PR test selection (`BASE_FILTER`, path-triggered jobs, fail-closed `changes` outputs) and the architecture tests proving that no `Category=Security` test leaves PR CI.
- WP-OI-CI-7 (#609): secret handling (environment gate, no PR trigger, masked logs, least-privilege service principal), the ephemeral-schema clean-up and the security cells of the live suite.
- WP-OI-CI-8 (#611): the TLS setup of the sim governance database (no policy relaxation, no fixed credentials).
- WP-OI-CI-1 (#518): only a light check that the doc-only glob list cannot skip `src/`, `tests/`, `.github/` or `deploy/helm/`. Phase 3 can confirm this with the `[Security]` architecture tests; no full review is needed.

## Owner decisions (2026-11-13)

The owner confirmed the recommended default of every open question (`Q-*`) in this file. The defaults in the question tables are binding for the implementation; a later owner decision wins. Phase 3 (security review) still applies before implementation starts.
