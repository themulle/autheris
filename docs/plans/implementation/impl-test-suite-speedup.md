# Implementation Plan: Test Suite Speed-Up

**Plan ID:** `IMPL-TEST-SPEEDUP-2026-10-14`
**Parent:** [`00-master-plan-overview.md`](../00-master-plan-overview.md)
**Scope:** CI wall-clock time of the `Build & Test All Suites` job and local test feedback time. No production code changes.

---

## 1. Baseline (measured 2026-10-14, `feat/impl-wave-3` @ `e2488a48`)

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

### Root cause of the container count

About 46 of the 58 containers are started **per test**, not per class: the PostgreSQL and SQL Server contract tests
(`PostgreSql*ContractTests`, `PostgreSqlWebSqlTransactionTests`, `SqlServer*ContractTests`, `*GovernedEndToEndTests`) implement
`IAsyncLifetime` on the test class itself, and xUnit creates a new class instance for every test. The remaining containers are
class fixtures (`AstCompilerPostgreSqlFixture`, `AstCompilerSqlServerFixture`, `PostgreSqlContainerFixture`, `MsSqlContainerFixture`)
and the shared Oracle and federated virtual filter collection fixtures.

---

## 2. Work Packages (ordered by impact)

### WP-TS-01: One database server per engine, one fresh database per consumer

- **Change:** a process-wide, lazily started PostgreSQL server and SQL Server server (`SharedDatabaseServers`). Every consumer
  (test class instance or class fixture) gets its own freshly created database on that server (`PostgreSqlTestDatabase`,
  `SqlServerTestDatabase`), so data, schemas, triggers and advisory locks stay isolated exactly as before.
- **Preserved behavior:** `AUTHERIS_TEST_MSSQL` still selects an external SQL Server; under `CI=true` or
  `SQLGATE_REQUIRE_DATABASES=1` a failed start still fails the run (`ContainerStartup`); without Docker the tests still skip.
- **Cleanup:** under CI the resource reaper (Ryuk) removes the containers. On developer machines Ryuk is disabled and the test host
  is killed before a process-exit handler completes, so the containers use Testcontainers reuse: at most one per engine stays
  running and later runs skip the start. Each test database is dropped when its consumer is disposed.
- **Risk:** server-wide state (roles, logins, server settings). Checked: the affected tests create no roles or logins and change no
  server settings. A test that needs server-wide state keeps its own container.
- **Acceptance:** at most one PostgreSQL and one SQL Server container per integration run (plus Oracle, Spark and the federated
  fixture); integration wall-clock time reduced by at least 6 minutes; no test changes its assertions.

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
| WP-TS-01 | Done: integration run locally 26:50 → 18:56 (1041/1041 green). Containers are reused across local runs (CI relies on Ryuk). |
| WP-TS-02 | Planned |
| WP-TS-03 | Planned |
| WP-TS-04 | Re-measure first: an uncontended local run took 20 s (the 5:07 baseline ran next to other test projects). Drop if CI confirms. |
| WP-TS-05 | Planned |
