# WP-SIMPL-10 Measurement protocol and Stage 0 baselines
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-10-measurement-protocol-and-stage-0-baselines) (AD-4, section 3) | Issues: #515, #509, CI #516-#523 (coordinated) | Owner decisions: OD-6 (CI time definition, binding), OD-12, OD-15, OD-17 | Security conditions: SEC-SIMPL-01 (no advisory details in results), SEC-SIMPL-73 (no workflow change in this WP)

## 1. Goal and outcome
Check in a reproducible measurement protocol and three scripts that produce the Stage 0 baselines for every metric in plan section 3: repo metrics, image size, cold start, warm latency and CI time (OD-6: PR wall-clock from push to last required check, median, queue time included, the #523 definition). Outcome: `benchmarks/results/` holds `repo-metrics-`, `footprint-` (before and after WP-SIMPL-01) and `ci-duration-` JSONs with date and sha7; every later stage exit cites one.

## 2. Preconditions and dependencies
- **#516 (CI hang regression) and #517 (job timeouts, hang dumps) must be merged before any CI measurement** (plan sequencing rule 1); CI runs taken while CI hangs are invalid. `ci-duration.sh` only counts runs created after the #517 merge commit date (parameter `--since`).
- #518-#522 are not preconditions; the baseline is taken after #516/#517 and before #519/#520/#521/#522 land, and re-run after each lands (results files are appended, never overwritten). #523 defines the CI metric.
- Footprint "before" run on the WP-SIMPL-01 base commit, "after" run on the WP-SIMPL-01 merge commit (lane L-RAT). Repo metrics need no dependency.
- No ci.yml change in this WP (CI budget job is WP-SIMPL-35 `[WF]`).

## 3. Scope
### In scope
- `benchmarks/protocol/README.md`, `benchmarks/scripts/{repo-metrics.sh,footprint.sh,ci-duration.sh}`, `benchmarks/results/*.json` (Stage 0 baselines).
### Out of scope
- BenchmarkDotNet projects (`benchmarks/Autheris.Benchmarks/`), load tests (`benchmarks/load/`), CI gating on budgets (WP-SIMPL-35), test-suite ratchets (WP-SIMPL-23).

## 4. Current state (verified on 63cbb893)
- `benchmarks/` has `Autheris.Benchmarks/`, `baselines/sql-compiler-legacy.json`, `load/`, `results/r2r-spike-report.md`; no `protocol/` or `scripts/`.
- R2R spike (`benchmarks/results/r2r-spike-report.md`): 10 cold restarts, 1,000 warm queries; image 373 MB (no R2R) vs 441 MB (R2R) uncompressed, 139 vs 170 MB compressed; cold-start P50 4,523.6 vs 5,576.9 ms; warm P99 3.47 ms. The new protocol uses 20 cold starts and 2,000 warm queries after 200 warm-up.
- Image: `.github/workflows/docker-publish.yml:100-101` builds `context: .`, `file: ./Dockerfile`. Readiness: `app.MapGet("/health/ready", ...)` at `src/Autheris.Api/Endpoints/HealthEndpoints.cs:54`.
- Plan section 3 baselines to reproduce: src LOC 302,461, Map* 275, Application folders 49, Options 173 / `*Enabled` 96, Substitute.For 4,159, Received 986, sleeps 92, hosted services 39, DI registrations 549, test LOC 382,125.

## 5. Implementation steps
1. `benchmarks/protocol/README.md`: hardware (CPU model, cores, RAM), OS/kernel, container runtime and version (podman), .NET SDK from `global.json`, exact commands, repetitions, percentile method nearest-rank (`rank = ceil(p/100 * n)`), JSON schema below, and the rule "results are append-only; a re-run creates a new file".
2. Common JSON schema (all scripts): `{"metric":"<name>","date":"yyyy-mm-dd","sha":"<sha7>","host":{...},"values":{...},"method":"<script+args>"}`; file `benchmarks/results/<metric>-<yyyy-mm-dd>-<sha7>.json`. Use `jq` if present, else `printf`; scripts `set -euo pipefail`, run from repo root, fail if the tree is dirty (`git diff --quiet`).
3. `repo-metrics.sh` (pure git/grep, deterministic): src LOC = `git ls-files 'src/*.cs' | xargs wc -l` total; modules LOC same for `modules/` (0 if absent); test LOC for `tests/*.cs`; Map* with the same regex as `ProgramRatchetTests` (WP-SIMPL-07) so both agree; Application folders = directories under `src/Autheris.Application` excl. bin/obj/Properties; Options classes `class\s+\w+Options\b`; `public bool \w*Enabled`; hosted services `AddHostedService<`; DI registrations `\.(AddSingleton|AddScoped|AddTransient|TryAdd\w*)\b`; `Substitute.For`; `.Received(`; sleeps `Thread.Sleep|Task.Delay` in tests; container-backed share = test files containing `Testcontainers` or `IClassFixture<.*(Postgres|SqlServer|Oracle)` divided by test files. Print each value and write `repo-metrics-*.json`. Document each regex in the README; deviations from section 3 numbers are recorded in the README, not "fixed".
4. `footprint.sh`: `podman build -f Dockerfile -t autheris:measure .` (no extra build args, as docker-publish.yml); uncompressed `podman image inspect --format '{{.Size}}'`; compressed `podman save autheris:measure | gzip -c | wc -c`. Cold start: 20 iterations, fresh container each (`podman run -d --rm` with a PostgreSQL container on a podman network and the minimal env from `benchmarks/load/configs` or the r2r spike), measure from `podman run` start (`date +%s%N`) to first HTTP 200 on `/health/ready` (poll every 50 ms, timeout 60 s -> run fails), P50/P95. Warm: 200 warm-up then 2,000 sequential requests of one fixed read query (same query as the r2r spike), P50/P99 from per-request wall times. Always remove containers in a `trap`. Write `footprint-*.json`.
5. `ci-duration.sh --since <iso-date> [--runs 10]`: list successful `ci.yml` runs on main (`gh run list --workflow ci.yml --branch main --status success --json databaseId,createdAt,updatedAt,headSha` or the REST endpoint `GET /repos/{owner}/{repo}/actions/workflows/ci.yml/runs` with `GITHUB_TOKEN`); per run wall time = last job `completed_at` minus run `created_at` (queue included, OD-6); per job duration from `gh run view --json jobs`; median overall and per job; also the same for the last 10 PR runs (`event=pull_request`) because OD-6 targets PRs. Token only from env, never echoed.
6. Run the three scripts on the Stage 0 baseline commit; footprint again after WP-SIMPL-01; commit the JSONs.
7. Add to the README a "DuckDB / Oracle lazy load" check: cold start with no federated query and no Oracle source configured must not load `DuckDB.NET` / `Oracle.ManagedDataAccess` (verify via `podman exec ... cat /proc/1/maps | grep -c -i duckdb` = 0); record the result in the footprint JSON (`values.lazy_duckdb`, `values.lazy_oracle`) per OD-12/OD-15.

## 6. Tests
- No production code. `tests/Autheris.Tests.Architecture/ProgramRatchetTests` (WP-SIMPL-07) is the agreement anchor: `MeasurementScriptTests.Repo_metrics_map_regex_matches_ratchet_regex` (Architecture project) reads `benchmarks/scripts/repo-metrics.sh` and asserts it contains the same Map* pattern string as `ProgramRatchetTests`. If WP-SIMPL-07 is not merged yet, add this test in the later PR.
- `MeasurementScriptTests.Results_files_follow_naming_scheme`: every file in `benchmarks/results/*.json` matches `^[a-z-]+-\d{4}-\d{2}-\d{2}-[0-9a-f]{7}\.json$` and parses as JSON with `metric`, `sha`, `values`.
- `MeasurementScriptTests.Scripts_contain_no_tokens` [Trait("Category","Security")]: scripts contain no `ghp_`, `github_pat_`, or `Authorization:` literal with a value.

## 7. Verification commands
- `bash benchmarks/scripts/repo-metrics.sh` (runs in seconds; compare with section 3, explain deltas in README).
- `bash benchmarks/scripts/footprint.sh` (needs podman; one build at a time, disk budget: `podman image prune -f` afterwards).
- `GITHUB_TOKEN=... bash benchmarks/scripts/ci-duration.sh --since <#517 merge date>`.
- `dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --filter "FullyQualifiedName~MeasurementScriptTests"`.

## 8. Acceptance criteria
- [ ] Protocol README and three scripts committed; scripts pass `shellcheck` if available.
- [ ] Results committed: `repo-metrics-<date>-<sha7>.json`, `footprint-...` before and after WP-SIMPL-01 (after: uncompressed <= 373 MB, plan WP-SIMPL-01 AC), `ci-duration-...` taken only from runs after #516/#517.
- [ ] Footprint JSON records lazy DuckDB/Oracle loading.
- [ ] `MeasurementScriptTests` green.

## 9. PR slicing
1. PR 1: README, `repo-metrics.sh`, `MeasurementScriptTests`, repo-metrics JSON, footprint "before" JSON (~+350 script/test lines).
2. PR 2 (after #516/#517 and WP-SIMPL-01): `ci-duration.sh`, ci-duration JSON, footprint "after" JSON.

## 10. Risks, rollback, must NOT
- Risk: measurements on a shared/noisy host; mitigate by recording host data and repeating on regression, never by trimming outliers silently.
- Must NOT measure CI before #516/#517 merge; must NOT edit workflows; must NOT overwrite older result files; must NOT print or commit tokens.
- Rollback: revert; no runtime effect.

## 11. Estimate
+~450 lines (scripts, README, test), 0 src. 2 pd.

## Decisions taken in this spec
- CI baseline reports both main runs (plan text) and PR runs (OD-6 target is PR); OD-6 governs the target.
- Map* counting is single-sourced with WP-SIMPL-07's regex; plan-table counts (e.g. 275) are reconciled in the README, not forced.
- Lazy-load verification uses process memory maps, no code change.
