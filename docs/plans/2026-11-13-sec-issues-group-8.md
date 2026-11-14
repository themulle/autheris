# Phase 3 security review: open-issue plans, priority group 8 (CI: test selection, live secrets, simulation DB)

- **Date:** 2026-11-14 · **Phase:** 3 (`csharp-security-expert`) · **Base verified:** `origin/main` @ `7a4df959`
- **Reviewed:** [`2026-11-13-plan-issues-ci.md`](2026-11-13-plan-issues-ci.md) sections #522 (WP-OI-CI-5), #609 (WP-OI-CI-7a/7b) and #611 (WP-OI-CI-8). Owner defaults `Q-CI-1..6` are binding (2026-11-13). Review only; no workflow, Dockerfile or `.trivyignore` is touched here.
- **Ids:** `SEC-OI-<issue>-<n>`. Every test below carries `[Trait("Category","Security")]`. A condition is binding for Phase 4 and Phase 5. Workflow-level conditions bind the `[WF]` PR author and the owner who approves it.

| Issue | WP | Verdict | Developer-ready |
|---|---|---|:---:|
| #522 | WP-OI-CI-5 | APPROVED WITH CONDITIONS (filter excludes Security tests that also carry Spark/Parity; nightly issue permissions) | yes, `[WF]` approval (Q-CI-3) |
| #609 | WP-OI-CI-7a tests | APPROVED WITH CONDITIONS (schema DROP guard, token and driver-message exposure) | yes, needs owner secrets |
| #609 | WP-OI-CI-7b workflow | APPROVED WITH CONDITIONS (job-level secrets reach every step) | yes, `[WF]` |
| #611 | WP-OI-CI-8 | APPROVED (already merged in #691; residual conditions only) | done |

Plan staleness: #611 is no longer open on `main`. `simulation-nightly.yml` `sim-api-fuzz` already starts a TLS PostgreSQL with a job-generated CA, `SSL Mode=VerifyFull`, masked per-run password, loopback port bind, `--rm`, and `SimulationGovernanceDbArchitectureTests` exists (verified). The plan's "no PostgreSQL service" state is obsolete; WP-OI-CI-8 should be marked delivered.

## #522 PR test selection and nightly

**Verified.** `integration-shard-filter.py:18` has `BASE_FILTER = "Category!=Spark&Category!=Parity"`; no test carries `Category=Oracle|Stress` yet; there is no `integration-nightly.yml`; `issues: write` appears only at job level in the existing nightly workflows.

**Threats.**
- The planned filter `Category!=Spark&Category!=Parity&((Category!=Oracle&Category!=Stress)|Category=Security)` keeps a Security-tagged Oracle/Stress test on PRs, but the **first two clauses already exclude** any test tagged both Security and Spark/Parity from the shard run, and the path-triggered Spark job runs only when paths match. Such a test silently leaves PR CI, which the plan's own fail-closed rule forbids. A trait on a method (not the class) or on a `[Theory]` is missed by a class-only architecture test.
- `changes` outputs that default to "skip" on a parse error would skip Oracle/Spark smoke jobs.
- The nightly workflow's issue step writes untrusted text (test names, messages) into a public issue; `issues: write` at workflow level or on the test job would let a compromised test step spam or edit issues.
- A nightly that runs on forks or from `workflow_dispatch` on any ref executes branch code with write permission.

**Conditions.**
- SEC-OI-522-1: the architecture test `Security_tests_are_never_excluded_by_the_pr_filter` evaluates the effective PR filter (shard filter plus the shard selector, plus the PR Spark/Oracle jobs) against **every method** carrying `Category=Security` at class **or** method level, theories included, and requires that at least one PR job selects it. A Security test that is also tagged `Spark`, `Parity`, `Oracle` or `Stress` must be selected by a PR job; today's set of such tests is recorded in a checked-in baseline that may only shrink, and a new combination fails the build with a message to either drop the extra category or add a PR lane.
- SEC-OI-522-2: the filter expression is built in one place (the script), unit-tested with a small evaluator for `&`, `|`, `!=`, `=` and parentheses (`test_security_always_selected`), and the architecture test reads the same constant.
- SEC-OI-522-3: the `changes` outputs `oracle` and `spark` are `true` on an empty diff, a diff error, an unknown event type or any path outside a positive list; `main`, nightly and `workflow_dispatch` run both jobs unconditionally; `sql-compiler-gate` (Oracle execution, G6) stays unfiltered.
- SEC-OI-522-4: `integration-nightly.yml` has top-level `permissions: {}`, the test job `contents: read` only, and a separate `needs`-chained alert job with `issues: write` only (`contents` none), triggers `schedule` and `workflow_dispatch` only (no `pull_request*`, `workflow_run`, `issue_comment`), `if: github.event.repository.fork == false`, all actions pinned by SHA, inputs passed through `env:`.
- SEC-OI-522-5: the alert job posts only the run URL and failed job ids (same as the existing #718 alerting), never TRX content, test names or messages; one rolling issue found by fixed title and label; TRX artifacts keep retention 7 days. No secret exists in this workflow (images are pulled by digest from `TestImages`).
- SEC-OI-522-6: the `[Security]` test count of a PR run (TRX totals) stays equal before and after the PR; `TestSuiteRatchetTests.Security_category_count_does_not_decrease` stays green.
- Tests: the plan names plus `CiWorkflowArchitectureTests.Method_level_security_traits_are_selected_by_a_pr_job`, `.Security_spark_oracle_stress_combinations_do_not_grow`, `.Nightly_alert_job_is_the_only_issues_write_scope`, `.Nightly_has_no_pull_request_trigger_and_is_fork_guarded`, `test_integration_shard_filter.py::test_security_always_selected` and `::test_unknown_changes_output_runs_jobs`.

## #609 Databricks live suite and secrets

**Verified.** `sql-compiler-gate-nightly.yml:194-247`: job `sql-compiler-gate-databricks-g9` with `environment: databricks-g9`, secrets declared in **job-level** `env:` (host, client id, client secret, warehouse id, catalog), `permissions: contents: read`, no `pull_request` trigger (header comment), `permissions: {}` at workflow level, skipped (green) while secrets are absent, empty selection fails closed. The test step pipes `dotnet test --verbosity normal | tee g9.log`.

**Threats.**
- Job-level secrets reach every step, including checkout, setup, restore and any third-party action; only the live test step needs them. `dotnet restore` and `dotnet test` run repository code with the credentials.
- The OAuth access token minted at runtime is not a registered secret and is not masked automatically; driver or provider exceptions can echo statements, hosts or data into `g9.log`.
- Scratch-schema clean-up: `DROP SCHEMA ... CASCADE` assembled from a run-id string could drop a fixture schema; `DisposeAsync` does not run when the job is killed by the timeout, leaving schemas and cost behind.
- A principal with more than "create schema in one test catalog plus read on fixtures" turns a leaked credential into workspace access.
- "Skipped = success" in the nightly summary reads as Databricks verified.
- Artifacts (TRX, logs) are readable by everyone who can read the repository if it is public.

**Conditions.**
- SEC-OI-609-1: secrets are declared only in the `env:` of the single live-test step (not at job level); checkout, setup and restore steps have none. The job additionally has `if: github.ref == 'refs/heads/main'`. The owner verifies the `databricks-g9` environment has required reviewers and a branch policy limited to `main` and records it in the PR (not verifiable from the repository).
- SEC-OI-609-2: `Databricks_secrets_only_in_environment_gated_job` parses all workflows: `secrets.DATABRICKS_*` occurs only in the job that sets `environment: databricks-g9`; no `pull_request`, `pull_request_target`, `workflow_run` or `issue_comment` trigger in that file; no `secrets: inherit`, no `toJSON(secrets)`, no secret inside a `run:` script body (only through `env:`).
- SEC-OI-609-3: the provider and the live tests never log the access token, the `Authorization` header or the client secret (unit test with a capturing logger and a fake transport). The job writes `g9.log` under `$RUNNER_TEMP`, never uploads it or TRX as an artifact, and does not use `--verbosity detailed`. Failure alerting posts the run URL only.
- SEC-OI-609-4: the schema name is `autheris_g9_<runid>` built from the numeric run id and validated against `^autheris_g9_[0-9]{1,20}$` before **every** DDL statement; the drop statement is `DROP SCHEMA <validated name>` for that exact name only, never `CASCADE` on a name that failed validation, never `DROP CATALOG`, never a pattern drop. A sweep at the start of each run drops only schemas that match the prefix and are older than 24 h (covers killed runs); the principal needs no other drop right.
- SEC-OI-609-5: the service principal (Q-CI-5) has CREATE SCHEMA in the one test catalog, read-only on the fixture schemas, no workspace or account admin, no access to any other catalog, token lifetime one hour; the warehouse has auto-stop, one cluster, a statement timeout and a row limit. Fixtures contain synthetic data only (stated in the suite header; no production data is loaded).
- SEC-OI-609-6: the security cells assert denial **before** the warehouse: `Ddl_and_dml_are_denied_before_reaching_the_warehouse` uses a recording transport and asserts zero statements sent, not only an exception; `Row_filter_and_mask_are_applied_on_warehouse` asserts the masked and filtered values from the warehouse, and a negative control fails when the policy is removed.
- SEC-OI-609-7: a skipped G9 (missing secrets) writes `engine=spark` in the results record and the summary prints "G9 not executed"; the parity report never presents a skipped G9 as a Databricks pass, and G9 stays outside `needs` of any release gate (SEC-CI-1).
- Tests: the plan names plus `DatabricksLiveGateTests.Schema_name_is_validated_before_every_ddl`, `.Sweep_drops_only_prefixed_schemas_older_than_24h`, `DatabricksTokenLoggingTests.Token_and_secret_never_logged`, `CiWorkflowArchitectureTests.Databricks_secrets_only_in_the_test_step_env`, `.G9_job_is_main_only`, `.G9_job_uploads_no_artifacts`.

## #611 Simulation governance database (merged)

**Verified on main** (`simulation-nightly.yml` `sim-api-fuzz`, #691): `permissions: contents: read`; SHA-pinned actions; image pinned by digest; throwaway CA (CA key deleted after signing, never mounted, 1-day validity), SAN `127.0.0.1`/`localhost`; server key `0600`; password from `openssl rand`, masked before use and passed through a `0600` env file (not process arguments); `-p 127.0.0.1:<port>`; `--rm`, no volume, no restart policy; readiness fail-closed after 60 s; TLS chain and SAN verified before the host starts; host keeps the Production TLS policy (no `Development`); cleanup `if: always()`; the report step scrubs by the connection string and the password. No pull-request or other untrusted trigger in the file (header comment, SEC-SIM-05). Nothing relaxes `ConnectionTlsPolicy`.

**Residual conditions.**
- SEC-OI-611-1: `SIM_POSTGRES_IMAGE` moves to the `TestImages.PostgreSql` digest when WP-OI-CI-4 lands (one digest source); until then the architecture test asserts the digest equals the one in `TestImages`/other fixtures or the comment remains the tracker.
- SEC-OI-611-2: the password and connection string stay in `$GITHUB_ENV` for the rest of the job (masked); no later step may echo them, and none may upload the TLS directory or logs from the database container (assert in `SimulationGovernanceDbArchitectureTests`: no `upload-artifact` path contains `sim-pg-tls` or container logs; existing `echo` rule kept).
- SEC-OI-611-3: the Schemathesis container runs with `--network host` (existing, SEC-SIM): it can reach the loopback PostgreSQL port but holds no credential and the image requires password authentication; accepted residual, recorded so a change to `trust` authentication or to a published port fails the architecture test (`-c ssl=on` and the `127.0.0.1:` bind are asserted).
- Tests: `SimulationGovernanceDbArchitectureTests.Database_logs_and_tls_dir_are_never_uploaded`, `.Port_is_loopback_bound_and_ssl_on`, `.Postgres_image_is_digest_pinned`.

## Anything the plans missed (summary)

1. Security tests that also carry Spark/Parity are excluded by the existing filter clauses and by a class-only architecture test (SEC-OI-522-1).
2. Nightly issue text from tests and fork or ref exposure (SEC-OI-522-4/5).
3. Job-level secrets, unmasked runtime token, uncontrolled scratch-schema DROP, skipped-as-green (SEC-OI-609-1/3/4/7).
4. #611 is already delivered; the plan is stale (residuals SEC-OI-611-1..3).

## New owner questions

| Id | Question | Recommended default |
|---|---|---|
| Q-SEC8-1 | #522: a test tagged Security plus Spark/Parity/Oracle/Stress must have a PR lane; allow existing combinations through a shrink-only baseline? | Yes; new combinations are rejected by the architecture test |
| Q-SEC8-2 | #609: restrict G9 to `refs/heads/main` in the job condition in addition to the environment branch policy? | Yes (defence in depth) |
| Q-SEC8-3 | #609: stale-schema sweep (24 h, prefix only) in the live suite? | Yes |
