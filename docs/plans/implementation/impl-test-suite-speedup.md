# Implementation Plan: Test Suite Speed-Up

**Plan ID:** `IMPL-TEST-SPEEDUP-2026-10-14`
**Parent:** [`00-master-plan-overview.md`](../00-master-plan-overview.md)
**Scope:** CI wall-clock time of the `Build & Test All Suites` job and local test feedback time. No production code changes.
**State:** `main` at `a4137626` (2026-10-14). WP-TS-01 (one shared PostgreSQL / SQL Server server per run, `SharedDatabaseServers`) is merged; WP-TS-02..05 are open.

---

## 1. Baseline (measured 2026-10-14, `feat/impl-wave-3` @ `e2488a48`, before WP-TS-01)

| Measurement | Value |
|---|---|
| GitHub job `Build & Test All Suites` | 19:37 total, of which `Test Autheris Solution` (build + all test projects) 18:19 |
| Restore / package audit / Spark image pull | 11 s / 31 s / 19 s |
| Local per project | Extensions 1 s, SqlCompilerGate (no DB profile) 5 s, Trino 1:18, Unit 2:40, Architecture 5:07, Integration ~16 min |
| Integration tests | 1008 tests, 776 s summed test time, fully serial (`DisableTestParallelization = true`, `MaxParallelThreads = 1`) |
| Database containers per integration run | 58, started serially, ~10 s each until ready (~580 s) |
| Slowest integration classes | `AstCompilerDatabricksSparkDmlExecutionTests` 125 s, `EndToEndSqliteTests` 50 s, `WalkingSkeletonIntegrationTests` 41 s, `IntegrationGapAEndpointMetadataTests` 37 s, `DbtIntegrationTests` 36 s |
| Slowest unit class | `AstCompilerDuckDbExecutionTests` ~119 s of 357 s summed |
| Architecture tests | every test re-reads the assemblies (Mono.Cecil / NetArchTest), 30–58 s per test |

The integration test project is the critical path: the other test projects finish while it still runs.

### Open: CI effect of WP-TS-01 not confirmed

WP-TS-01 removed the per-test containers (local integration run 26:50 → 18:56). On GitHub, however, `Build & Test` took
23:14 for `f396e742` against 20:18 before WP-TS-01. Investigate before WP-TS-02: compare per-step timings of several runs
(runner variance, container reuse vs Ryuk under `CI=true`, image pulls, the Spark job) and confirm or refute the expected
CI gain. Re-baseline the table above with the result.

---

## 2. Open Work Packages (ordered by impact)

### WP-TS-00: Make CI test timings visible (prerequisite)

- **Change:** `dotnet test --logger trx` for every test project in `ci.yml` and upload the TRX files as a workflow artifact
  (`if: always()`). Job logs are not readable without repository access, so per-class durations are otherwise unknown on GitHub.
- **Use:** resolves the open CI question in section 1 and sizes the WP-TS-02 shards on measured class durations.
- **Acceptance:** each CI run publishes per-test durations for all test projects.

### WP-TS-02: Shard the integration tests across parallel CI jobs

- **Change:** a job matrix with three shards selected by `--filter` on namespace or trait (database contract tests, web host tests,
  Spark/Oracle/DuckDB execution tests). Each shard is a separate process, so no in-process thread safety is needed.
- **Build once:** one build job publishes the test binaries as an artifact; the shards run `dotnet test --no-build`.
- **Risk:** a test that matches no shard filter. Mitigation: a check step that fails when the union of the shard filters does not
  list every test (`dotnet test --list-tests`).
- **Acceptance:** critical path of the test stage at most 40 % of the baseline.

### WP-TS-03: Move the Spark DML execution tests to their own job

- **Change:** `AstCompilerDatabricksSparkDmlExecutionTests` (125 s) and `AstCompilerDatabricksSparkExecutionTests` get the trait
  `Category=Spark` and run in a dedicated job (parallel to the main test job). They stay PR-blocking.
- **Acceptance:** the main test job no longer pulls the Spark proxy image.

### WP-TS-04: Cache loaded modules in the architecture tests

- **Change:** one static, lazily loaded module/type set per assembly instead of a fresh `ModuleDefinition.ReadModule` or
  `Types.InAssembly` per test.
- **Acceptance:** architecture test project under 1 minute locally; same assertions.

### WP-TS-05: Selective in-process parallelization (after WP-TS-01..04)

- **Change:** re-enable xUnit parallelization for test collections that are proven isolated; keep a serial collection for the rest.
- **Known blockers:** `Environment.SetEnvironmentVariable` (`ItsmIntegrationTests`), static caches in production code, the global
  `SqliteConnection.ClearAllPools()`. Each blocker is either removed or its class stays in the serial collection.
- **Acceptance:** no new flaky test across 10 consecutive CI runs.

### Not pursued

- Separating build and test steps or extra NuGet caching: restore takes 11 s, the gain is marginal.
- Retrying failed tests: forbidden for security tests (plan M-5).

---

## 3. Status

| Work package | Status |
|---|---|
| CI check of WP-TS-01 | Resolved 2026-10-15: on `a2e4e4ee` the job took 23:09, of which `Autheris.Tests.Integration` took 17:51 (serial in-process, ~1071 s of class wall time over 93 classes, ~600 s of it fixture start-up). The integration assembly, not the database containers, is the critical path; addressed by WP-TS-02. |
| WP-TS-00 | Implemented (`--logger trx` + `test-results-*` artifacts in `ci.yml`) |
| WP-TS-02 | Implemented 2026-10-15: `integration-tests` matrix job (4 shards) in `ci.yml`; `.github/scripts/integration-shard-filter.py` assigns classes greedily by the measured wall times in `tests/Autheris.Tests.Integration/ci-shard-weights.json` (unknown classes: 10 s); the last shard is the exact complement (verified: 768 discovered tests, 768 unique across the 4 shards). Each shard builds itself (no artifact hand-off; build ≈ 3 min). The main job excludes `Autheris.Tests.Integration.`. Estimated shard load ≈ 4.5 min each. Also added `concurrency` with `cancel-in-progress` for pull requests in `ci.yml` and `codeql.yml`. Refresh the weights file from a CI log when shards drift apart. |
| WP-TS-03 | Implemented (`Category=Spark` trait on Spark execution tests + parallel `spark-integration-tests` job in `ci.yml`) |
| WP-TS-04 | Implemented (`CecilModuleCache` + cached production types and call graph in `Autheris.Tests.Architecture`) |
| WP-TS-05 | Planned |
