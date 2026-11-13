# Phase 3 addendum: WP-OI-CI-8 (#611) and WP-OI-CI-1 (#518)

Reviewer: csharp-security-expert, 2026-11-13. Plan: `2026-11-13-plan-issues-ci.md` (branch `docs/plans-open-issues`). Owner defaults for Q-CI-1 and Q-CI-6 apply.

## Verdicts

- WP-OI-CI-8 (#611): APPROVED WITH CONDITIONS.
- WP-OI-CI-1 (#518): APPROVED WITH CONDITIONS (SEC-CI-1-2 is the important one).

## Verified facts

- `sim-api-fuzz` already has `permissions: contents: read` (top-level `permissions: {}`), persist-credentials false, SHA-pinned actions, a digest-pinned Schemathesis image, `--network host`, and uploads only the scrubbed `sim-report-api` directory. Raw HAR, JUnit and canaries stay in `$RUNNER_TEMP`.
- The sim host sets no environment name (Production), so `ConnectionTlsPolicy` demands VerifyFull/VerifyCA. The plan keeps the policy unchanged.
- Architecture-test project files read `docs/` (DocsInventoryTests, ConfigurationReferenceTests incl. generated `docs/reference/configuration.md`), `deploy/` (DeployHygieneTests, 10 refs), `benchmarks/` (BenchmarkStackHardeningTests), `README.md`, and `.github/CODEOWNERS`. A docs/deploy/benchmarks-only PR can therefore break tests that the `dotnet=false` path would skip.

## WP-OI-CI-8 binding conditions

- SEC-CI-8-1: The server key is generated in the job (`openssl genrsa`/`req`, 2048-bit RSA or P-256 at least). The CA key never leaves `$RUNNER_TEMP`, is deleted right after signing, and is never mounted. The CA cert and server cert/key live in a `mktemp -d` directory under `$RUNNER_TEMP`, `umask 077`, never in `$GITHUB_WORKSPACE` and never in an uploaded path.
- SEC-CI-8-2: Postgres requires `server.key` owned by uid 70 (alpine) or 999 (debian) with mode 0600 (or 0640 root-owned). Use `chown`/`chmod` on the copy, never `chmod 644` or `ssl=off`. Start with `-c ssl=on -c ssl_cert_file=... -c ssl_key_file=...`.
- SEC-CI-8-3: The server cert has `subjectAltName=IP:127.0.0.1,DNS:localhost` and `extendedKeyUsage=serverAuth`, validity at most 1 day. The connection string uses `Host=127.0.0.1;SSL Mode=VerifyFull;Root Certificate=<ca.crt>`. `Trust Server Certificate`, `SSL Mode=Require/Prefer/Disable` and `NoVerify` are forbidden.
- SEC-CI-8-4: The password is `openssl rand -hex 24`, masked with `echo "::add-mask::$pw"` before any other use, and handed over only through `$GITHUB_ENV` (after masking) or step-local env. It is never echoed, never part of `docker run` args visible in `ps`/logs (use `-e POSTGRES_PASSWORD` from env or a `--env-file` in the 0700 dir), and never a repository secret. The full connection string is also masked.
- SEC-CI-8-5: Bind with `-p 127.0.0.1:<port>:5432` only (never `-p 5432:5432`, no `--network host` for postgres). Pin the image as `postgres:16-alpine@sha256:<digest>`, not the bare tag (the tag pin is acceptable only with an inline TODO tied to WP-OI-CI-4; the digest is preferred now). Add a step that stops/removes the container with `if: always()`.
- SEC-CI-8-6: The job must not set `ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT` to Development (or anything else), must not modify `ConnectionTlsPolicy`, and the permissions block stays `contents: read` with no `packages`, `id-token` or `actions` scope.
- SEC-CI-8-7: The governance DB password and connection string are added to the canary/scrub path: the postgres container log and any host log tail printed to the job log are filtered, and the `sim-api-report.py` leak check receives the password as an additional canary. Postgres container logs are never uploaded.
- SEC-CI-8-8: The postgres DB is empty and per-run (no volume, `--rm`, no `--restart`). A readiness check (`pg_isready` with TLS) fails the step on timeout (fail-closed, no continue-on-error).

### Mandatory tests (`tests/Autheris.Tests.Architecture/SimulationIsolationArchitectureTests.cs`, all `[Trait("Category","Security")]`)

- `Sim_api_job_provides_governance_db_with_verify_full` (workflow has `AUTHERIS_SIM_GOVERNANCE_DB`, `SSL Mode=VerifyFull`, `Root Certificate=`, and none of `Development`, `Trust Server Certificate`, `NoVerify`, `SSL Mode=Disable|Prefer|Require|VerifyCA`).
- `Sim_api_job_masks_generated_password_and_binds_postgres_to_loopback` (`openssl rand`, `::add-mask::` precedes first use, `127.0.0.1:` port binding, no secrets reference, image contains `@sha256:`).
- `Sim_api_job_permissions_stay_contents_read_and_upload_only_scrubbed_report` (job `permissions` equals `contents: read`; upload path is only `sim-report-api`; no `*.key`, `*.crt`, HAR or canary path uploaded).

## WP-OI-CI-1 binding conditions

- SEC-CI-1-1: `ci-changes.py` is fail-closed: unknown event, empty diff, non-zero git exit, unreadable/ambiguous base, a path it cannot classify, and any exception all print `dotnet=true`. Match against a normalized path (reject `..`, absolute, quoted/renamed path variants: use `git diff --name-only -z --no-renames` so both sides of a rename are listed). `dotnet=false` only when every path matches the positive list. `.github/**` (except `.github/ISSUE_TEMPLATE/**`), `src/**`, `tests/**`, `deploy/helm/**`, `global.json`, `*.props/*.targets/*.sln/*.csproj`, `.editorconfig` always give `dotnet=true`. `**/*.md` must be evaluated only after these deny rules (a `.md` under `src/`, `tests/`, `deploy/helm/` or `.github/` counts as code; embedded resources exist in `src/Autheris.Api`, `TrinoSqlEngine`, `Autheris.Infrastructure`).
- SEC-CI-1-2: The doc glob list as written is unsafe because tests read docs, `deploy/`, `benchmarks/`, `README.md` and `CODEOWNERS` (see Verified facts). Required: `Autheris.Tests.Architecture` runs (build plus test) on every PR regardless of `changes.dotnet` (own job or an unconditional step), including all `Category=Security` tests; only the heavy suites (Unit, Integration, Spark, CodeQL analyze) are skipped. If the PM prefers to keep the Architecture job conditional, the doc-only list must exclude every path an architecture/unit test reads, enforced by SEC-CI-1-5.
- SEC-CI-1-3: Skipped jobs report "skipped", which counts as success for required checks. Required checks must not be configured on job names that exist only behind the `if`, unless the skip is intentional. `changes` itself has `permissions: contents: read`, a full-depth or sufficient `fetch-depth` for `<base>...<head>` and the base SHA from the event payload (not from PR-controlled strings); inputs go through `env:`, never `${{ }}` interpolation in `run:`.
- SEC-CI-1-4: Every workflow keeps running on `pull_request`; the `Category=Security` suites (Unit, Architecture, Integration shards, SqlCompilerGate) run on every PR with `dotnet=true`. `sql-compiler-gate` and `helm-render` stay unfiltered. `main` pushes (coalesced) classify the same way; `release.yml` and tag runs never use the filter.
- SEC-CI-1-5: Concurrency change: `cancel-in-progress` must be true only for `pull_request` events, never for `main` push, `workflow_run` or tag runs (`docker-publish`, `release`).

### Mandatory tests (`[Trait("Category","Security")]` in `CiWorkflowArchitectureTests.cs`; python unit tests in `test_ci_changes.py`)

- `Docs_only_filter_never_lists_src_tests_or_helm` (as planned; also asserts `.github/` only via `ISSUE_TEMPLATE` and no `*.csproj/props/sln`).
- `Docs_only_filter_excludes_every_path_read_by_architecture_tests_or_architecture_job_is_unconditional` (parses `RepositoryScan` references `docs`, `deploy`, `benchmarks`, `README.md`, CODEOWNERS versus the classifier, or asserts the Architecture job has no `if:`).
- `Sql_compiler_gate_has_no_path_condition` (as planned) and `Security_suites_have_no_changes_gate` (jobs running `Category=Security`/Architecture/Unit security tests carry no `needs.changes` condition, or the SEC-CI-1-2 exception).
- `Cancel_in_progress_is_pull_request_only_for_publish_workflows`.
- python: `test_md_under_src_runs_dotnet`, `test_md_under_deploy_helm_runs_dotnet`, `test_rename_from_src_to_docs_runs_dotnet`, `test_github_workflow_change_runs_dotnet`, `test_unknown_event_runs_dotnet`, `test_path_traversal_or_unnormalized_runs_dotnet`.
