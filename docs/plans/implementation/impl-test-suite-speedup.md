# Implementation Plan: Test Suite Speed-Up

**Plan ID:** `IMPL-TEST-SPEEDUP-2026-10-14`
**Parent:** [`00-master-plan-overview.md`](../00-master-plan-overview.md)
**Scope:** CI wall-clock time of the `Build & Test All Suites` job and local test feedback time. No production code changes.
**State:** **COMPLETED & VERIFIED ✅** (`WP-TS-00..06` delivered and verified).

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

### Resolved: CI effect of WP-TS-01 confirmed & sharded in WP-TS-02

WP-TS-01 removed the per-test containers (local integration run 26:50 → 18:56). On GitHub, `Autheris.Tests.Integration` took 17:51 due to in-process serial fixture start-up across 93 classes; `WP-TS-02` sharded the integration suite across 4 parallel jobs (~4.5 min each).

---

## 2. Work Packages (Delivered)

### WP-TS-00: Make CI test timings visible (prerequisite) — **COMPLETED & VERIFIED ✅**

- [x] **Change:** `dotnet test --logger trx` for every test project in `ci.yml` and upload the TRX files as a workflow artifact (`if: always()`).
- [x] **Acceptance:** each CI run publishes per-test durations for all test projects.

### WP-TS-01: Shared database containers per test run — **COMPLETED & VERIFIED ✅**

- [x] **Change:** `SharedDatabaseServers` provides shared PostgreSQL and SQL Server containers per test run instead of 58 per-class containers.

### WP-TS-02: Shard the integration tests across parallel CI jobs — **COMPLETED & VERIFIED ✅**

- [x] **Change:** a job matrix with four shards selected by `--filter` via `.github/scripts/integration-shard-filter.py` and `tests/Autheris.Tests.Integration/ci-shard-weights.json`.
- [x] **Acceptance:** critical path of the test stage at most 40 % of the baseline (~4.5 min per shard).

### WP-TS-03: Move the Spark DML execution tests to their own job — **COMPLETED & VERIFIED ✅**

- [x] **Change:** `AstCompilerDatabricksSparkDmlExecutionTests` (125 s) and `AstCompilerDatabricksSparkExecutionTests` get the trait `Category=Spark` and run in a dedicated job (`spark-integration-tests`, parallel to the main test job).
- [x] **Acceptance:** the main test job no longer pulls the Spark proxy image.

### WP-TS-04: Cache loaded modules in the architecture tests — **COMPLETED & VERIFIED ✅**

- [x] **Change:** `CecilModuleCache` caches loaded `ModuleDefinition`, production types, and call graphs across architecture tests.
- [x] **Acceptance:** architecture test project under 1 minute locally; same assertions.

### WP-TS-05: Selective in-process parallelization isolation — **COMPLETED & VERIFIED ✅**

- [x] **Change:** `SerialIntegrationCollection` (`[CollectionDefinition(Name, DisableParallelization = true)]` in `tests/Autheris.Tests.Integration/SerialIntegrationCollection.cs`) isolates integration test classes that mutate process-wide state (`ItsmIntegrationTests` mutating `Environment.SetEnvironmentVariable("ITSM__WEBHOOK_SECRET", ...)`), allowing isolated test collections to safely run in parallel.
- [x] **Acceptance:** process-wide state mutators are isolated in `SerialIntegrationCollection`.

### Not pursued

- Separating build and test steps or extra NuGet caching: restore takes 11 s, the gain is marginal.
- Retrying failed tests: forbidden for security tests (plan M-5).

---

## 3. Status

| Work package | Status |
|---|---|
| CI check of WP-TS-01 | **COMPLETED & VERIFIED ✅** (Resolved 2026-10-15: on `a2e4e4ee` the job took 23:09, of which `Autheris.Tests.Integration` took 17:51; addressed by `WP-TS-02`.) |
| WP-TS-00 | **COMPLETED & VERIFIED ✅** (`--logger trx` + `test-results-*` artifacts in `ci.yml`) |
| WP-TS-01 | **COMPLETED & VERIFIED ✅** (`SharedDatabaseServers` shared PostgreSQL / SQL Server containers) |
| WP-TS-02 | **COMPLETED & VERIFIED ✅** (`integration-tests` 4-shard matrix job in `ci.yml`; `.github/scripts/integration-shard-filter.py` + `tests/Autheris.Tests.Integration/ci-shard-weights.json`; `concurrency` with `cancel-in-progress`) |
| WP-TS-03 | **COMPLETED & VERIFIED ✅** (`Category=Spark` trait on Spark execution tests + parallel `spark-integration-tests` job in `ci.yml`) |
| WP-TS-04 | **COMPLETED & VERIFIED ✅** (`CecilModuleCache` + cached production types and call graph in `Autheris.Tests.Architecture`) |
| WP-TS-05 | **COMPLETED & VERIFIED ✅** (`SerialIntegrationCollection` in `tests/Autheris.Tests.Integration/SerialIntegrationCollection.cs` applied to `ItsmIntegrationTests`) |
| WP-TS-06 | **COMPLETED & VERIFIED ✅** (PR `sql-compiler-gate` job runs in parallel without waiting for `build-and-test`, with `WorkflowPolicyTests` enforcing Release guarantee on `build-and-test`) |
