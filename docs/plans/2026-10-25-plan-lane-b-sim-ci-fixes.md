# Plan: Lane B - sim host shutdown, G8 allocation gate, SQL Server time-bucket parity

Branch `fix/lane-b-sim-ci-fixes` (from `origin/main` 8cdd91de). No Phase 1 PRD: the owner asked for a direct fix, and the items B1-B3 are the requirement. Workflows under `.github/workflows/` are not changed.

## B1. Deterministic sim host shutdown (SIGABRT 134 after SIGTERM)

### Root cause (analysis)
`SimApiHostMain` hosts `Autheris.Api` through `WebApplicationFactory<Program>`. With minimal hosting, `WebApplicationFactory` runs the app entry point on its own thread, and that thread sits in `app.Run()` -> `WaitForShutdownAsync`. On SIGTERM there are three shutdown owners: `ConsoleLifetime` (own SIGTERM registration -> `StopApplication`), the entry-point thread (`StopAsync` and then `DisposeAsync` of the same host once `ApplicationStopping` fires), and our `PosixSignalRegistration` -> `exit` TCS -> `factory.Dispose()` in `finally` (`StopAsync` + `Dispose` again). The lead's log (two concurrent "Hosting stopping" lines, one "Hosting stopped") fits this race. When the two dispose paths overlap, `factory.Dispose()` can throw (for example `ObjectDisposedException` from the root provider). An exception that escapes an async `Main` is unhandled, and on Linux the runtime calls `abort()`, which gives exit code 134. Startup exceptions (seeding, catalog registration) also escape `Main` today.

### Requirements
- REQ-LB-01: Exactly one component starts the host stop. `ConsoleLifetime` is removed from both WAF hosts (seeding and serving).
- REQ-LB-02: The shutdown is bounded: `HostOptions.ShutdownTimeout` is 10 s, and the whole sequence finishes in at most 25 s, below the 30 s SIGKILL window of `stop_host`.
- REQ-LB-03: No exception escapes `Main`. A shutdown exception writes `sim-api-host: shutdown failed: <Type>: <Message>` to stderr and returns **4**. A shutdown timeout returns **5**. An unexpected startup exception writes `sim-api-host: startup failed: <Type>: <Message>` and returns **1**. Codes 2 and 3 (config, loopback) are unchanged. Real crashes therefore still fail the job.
- REQ-LB-04: `AppDomain.CurrentDomain.UnhandledException` writes the type, message, stack and `IsTerminating` to stderr. `TaskScheduler.UnobservedTaskException` writes the type and message. Both are registered first in `Main`. This is a test-only host, so stack traces in CI logs are acceptable.
- REQ-LB-05: The loopback-only bind (SEC-SIM-03) and the data-dir wipe (SEC-SIM-23) are kept. The wipe runs on every exit path after the parse of `--data-dir`, including codes 1, 4 and 5.
- REQ-LB-06: `stop_host` accepts exit codes 0 and 143. On 143 it writes a warning: the host was killed by the default SIGTERM disposition, outside the handler window. Every other code fails the job, including 1-5, 134 and 137.

### Design decisions
1. **Shutdown coordinator** in the new file `tests/Autheris.Sim.ApiHost/SimHostShutdown.cs`. It uses only BCL types, so it can be linked into the test project. Its API is `Request()` (idempotent, so a second signal is only logged) and `Task<int> RunAsync(Func<CancellationToken, Task> waitStopped, Func<bool> hostDisposed, Func<Task> fallbackDispose, Action cleanup, TextWriter stderr, TimeSpan stopTimeout, TimeSpan disposeTimeout)`. The sequence:
   - (a) The caller has already called `IHostApplicationLifetime.StopApplication()`. The coordinator waits for `ApplicationStopped`, bounded by 15 s.
   - (b) It waits until the entry-point thread has disposed the host, bounded by 5 s. The probe `hostDisposed` checks whether `factory.Services.GetService<IHostApplicationLifetime>()` throws `ObjectDisposedException`, polled every 50 ms.
   - (c) Only when (a) or (b) times out does it call `fallbackDispose` (`factory.DisposeAsync()`) inside try/catch, and it returns 5. On the normal path `factory.Dispose` is never called, so nothing races the entry-point thread.
   - (d) `cleanup` (`SqliteConnection.ClearAllPools()` and `WipeDataDir`) always runs in `finally`.
   - Any exception in (a)-(c) returns 4 with `<Type>: <Message>`.
2. **`SimHostLifetime : IHostLifetime`** (no-op `WaitForStartAsync` and `StopAsync`) is registered in `NewHost` through `b.ConfigureServices(s => s.AddSingleton<IHostLifetime, SimHostLifetime>())`. `WithWebHostBuilder` services are applied after `Program`'s, so this registration wins. `NewHost` also calls `b.UseShutdownTimeout(TimeSpan.FromSeconds(10))`.
3. **`Main`** keeps the `PosixSignalRegistration` handlers for SIGTERM and SIGINT (`ctx.Cancel = true; shutdown.Request()`). The body after option parsing is wrapped as `try { ... } catch (Exception ex) { return 1; } finally { cleanup }`. Startup exceptions in the startup window still stop a factory that was already created, through the fallback dispose (try/catch).
4. **Script**: the new function `host_exit_ok <code>` (0 or 143 returns success) is used in `stop_host`, and the comment at line 105 is updated. `self_test` gains cases: 0 and 143 accepted; 1, 2, 4, 5, 134 and 137 rejected.

### WP-B1 (files and tests)
- Files: `tests/Autheris.Sim.ApiHost/SimApiHostMain.cs`, `SimHostShutdown.cs` (new), `SimHostLifetime.cs` (new, internal sealed), `scripts/sim/run-api-fuzz.sh`, `tests/Autheris.Tests.Simulation/Autheris.Tests.Simulation.csproj` (link `SimHostShutdown.cs`, the same pattern as `SimApiHostOptions.cs` at line 35). `Tests.Simulation` cannot use a `ProjectReference` to the host (SEC-SIM-21, and the test-auth build switch). Linking the pure file is the established route.
- Unit tests, `tests/Autheris.Tests.Simulation/Meta/SimHostShutdownTests.cs` (real short timeouts of 100-300 ms, no host):
  - `Shutdown_StoppedAndDisposed_ReturnsZeroAndCleansUp`
  - `Shutdown_RequestTwice_StopsOnce`
  - `Shutdown_StopNeverSignalled_FallsBackToDispose_Returns5`
  - `Shutdown_HostNeverDisposed_FallsBackToDispose_Returns5`
  - `Shutdown_FallbackDisposeThrows_Returns4_WritesTypeAndMessage_CleansUp`
  - `Shutdown_WaitStoppedThrows_Returns4_CleansUp`
  - `Shutdown_CleanupRunsEvenWhenStderrWriteFails`
- Process tests (guarded by `AUTHERIS_SIM_RUN=1`) in `ApiFuzz/ApiHostTests.cs`. They use `SimApiHostProcess` and need a Release build with `-p:AutherisIncludeTestAuth=true`, which already exists in the worktree:
  - `ApiHost_SigtermUnderConcurrentTraffic_ExitsZero_Repeated`: 10 start/stop cycles with 8 concurrent request loops (OData, GraphQL, WebSQL) in flight. Each cycle asserts exit code 0, the data dir is gone, and stderr has no "unhandled" or "shutdown failed".
  - `ApiHost_SecondSigterm_DuringShutdown_ExitsZero`.
- Script: `bash scripts/sim/run-api-fuzz.sh --self-test` passes.
- Neither test set runs in CI today: the Simulation filters select only `Corpus` and `Meta.FuzzHarnessTests`. The developer and the lead run them locally. The nightly `sim-api-fuzz` job is the end-to-end check.

## B2. G8 allocation-ratio violation at 0.5 KB (1.26x, limit 1.25x)

### Analysis
- The `Ast` benchmark runs `new FastSqlEngine().Compile(...)` for every operation. Any per-engine construction cost and any per-compile fixed cost therefore shows up at full weight in the 0.5 KB bucket (about 5 KB of 127 KB, +4.2%) and barely registers at 4 KB and 64 KB. This matches the observed pattern.
- The frozen AST baseline is from 2026-10-11 (`72c767bc`). Candidates since then, from a static scan of `src/TrinoSqlEngine`:
  - `a4137626` (#82): an eager per-engine `SyntaxErrorNegativeCache` (`ConcurrentDictionary`, about 0.6-1 KB depending on processor count), only needed on the syntax-error path.
  - `3b301c61`: ICompilerHooks.
  - `6de4350b` (#164): `ColumnScopeTracker` and `ClientListExpander`. The time-zone validator is guarded by `Contains`.
  - `525e050d` (#198): post-compile table-target assertion.
  - `092da5a9` (#248): OpenTelemetry instrumentation in the compile path.
  - `24ca0473` (#287) and `72ae716e` (#278): security remediation; `AstSecurityVisitor` partials such as MaskGuards (+811 lines), RequiredFilters and AggregateTypeGuards.
  - `3dba661a` (#285): WITH RECURSIVE.
  - `b9fac204` (#317).
  - Static reading cannot attribute the 5 KB, so WP-B2 begins with a measurement.

### Requirements
- REQ-LB-07: The cause of the 0.5 KB increase is attributed to commits and allocation sites by measurement, and the result is recorded in this plan's changelog.
- REQ-LB-08 (preferred): The regression is removed in code. Acceptance: the 0.5 KB `Ast` `AllocatedBytesPerOp` is at most **128,000 B** (ratio <= 1.216, at least 3,600 B under the 131,612 B limit). 4 KB and 64 KB do not get worse, and every test in `Category=SqlGate` stays green.
- REQ-LB-09: A threshold or baseline change happens only with owner approval. It must be documented in `BenchModel.cs`, in the baseline `description`, and here. It is never silent, and the workflow is not touched.

### WP-B2 (method, files, tests)
1. **Measure (time box 2 h).** The benchmark gives deterministic allocation figures.
   - Command: `flock /work/wt/.build.lock env NUGET_PACKAGES=/work/nuget dotnet run -c Release --project benchmarks/Autheris.Benchmarks -- --filter "*SqlCompilerBenchmarks.Ast" --job short --inProcess --exporters json --artifacts <scratch>/g8-<sha>`.
   - Read `Memory.BytesAllocatedPerOperation` for `Bucket=0.5KB`. Then check with `dotnet run --project tools/SqlGate.BenchCheck -- <scratch>/g8-<sha>/results benchmarks/baselines`.
   - Measure HEAD first. If HEAD is not about 132 KB, the gap is runner-specific: record it and stop for the owner.
   - Then bisect, one temporary worktree under `/work/wt` at a time, removing `bin` and `obj` after each run (disk budget). Commits: `72c767bc` (expect about 126,969 B), `a4137626`, `6de4350b`, `525e050d`, `092da5a9`, `72ae716e`, `24ca0473`, `3dba661a`, `b9fac204`.
   - For the step that adds the most, attribute allocation sites with the BenchmarkDotNet EventPipe profiler (`--profiler EP`, allocation sampling) or a 1,000-iteration `GC.GetTotalAllocatedBytes(true)` probe around single passes.
2. **Fix (preferred).** Remove avoidable allocations on the path with no policy, no masks and no time zone, which is what the benchmark exercises. Candidate measures:
   - lazy `_syntaxErrors` (created on first `Remember`; lookups on a null cache return a miss)
   - skip passes and collections when the feature is unused (no masks, no required filters, no client lists, no recursive CTE)
   - avoid per-compile telemetry tag arrays or closures when no listener is attached (`ActivitySource.HasListeners()`)
   - static or cached option and visitor state
   Files: whatever the attribution names, most likely `src/TrinoSqlEngine/FastSqlEngine.cs`, `GovernedSqlCompiler.cs` and `Ast/Visitors/AstSecurityVisitor*.cs`. Every guard is a fast-path skip only and keeps fail-closed behaviour; Phase 3 must confirm that no security control is skipped when its input is present.
3. **Tests.** Existing behaviour tests must stay green. For each skip guard, add a unit test that the control still fires when its input is present, for example `SyntaxErrorCache_LazyInit_StillCachesRepeatedSyntaxError` and `MaskGuards_SkippedOnlyWhenNoMasks_MaskStillApplied` in `tests/TrinoSqlEngine.Tests`. Allocation is verified by the benchmark plus BenchCheck (step 1 command) against REQ-LB-08. No allocation-budget unit test is added, because process-wide allocation counters are noisy under parallel xunit.
4. **Re-freeze.** Once REQ-LB-08 holds, update `astAllocatedBytesPerOp` for all buckets in `benchmarks/baselines/sql-compiler-legacy.json` to the new measured values, add a dated note in `description`, and leave the legacy numbers unchanged.

## B3. SQL Server container parity cells for GraphQL time buckets (week, quarter, ISO week Dec/Jan)

### Findings
- The parity container job (`parity-nightly.yml`, filter `Category=Parity&Dialect=Container`) has only a PostgreSQL host (`Dialects/ParityPostgresHostFixture.cs`). SQL Server cells throw `PendingCellException(..., "sqlserver_host")`.
- A new class carrying `[Trait("Category","Parity")]` and `[Trait("Dialect","Container")]` is picked up without a workflow change, and Docker is available on that runner.
- The planner (`GovernedTableReadPlanner.BuildTimeBucketExpression`) emits Trino `date_trunc('<unit>', src)` for PostgreSQL and SQL Server, with `src = CAST(col AS timestamp)` for a `date` column and `convert_timezone('UTC', zone, src)` for a zone other than UTC. The SQL Server generator renders week as the ISO Monday (`DATEDIFF` from `'19000101'`, independent of `@@DATEFIRST`).
- SQLite renders week as `strftime('%Y-%W')`, which has a different key shape. That is a pre-existing divergence, so week cells are container-only (see the open questions).
- REST: `POST /api/v1/analytics/aggregate` (`AggregateRequestDto`) has no time-bucket input, so there is no REST cell. GraphQL is the only channel.
- Bucket keys come back as `yyyy-MM-ddTHH:mm:ss`. `RobinAssert` prefix-matches the expected date parts `yyyy`, `yyyy-MM` and `yyyy-MM-dd`.

### Requirements
- REQ-LB-10: A SQL Server Testcontainers parity host serves the 18-row `ParitySeed.Sales` table with `sale_date DATE` and `sold_at DATETIME2` (plain UTC wall time, like PostgreSQL `TIMESTAMP`), registered as `default.dbo.parity_sales`. The GraphQL field is `default_dbo_parity_sales_aggregate`.
- REQ-LB-11: The GraphQL time-bucket cells below pass on SQL Server, and on PostgreSQL with the same expected values. A 5xx is a hard failure (SEC-PAR-05). Unknown SQL Server behaviour is not pre-registered as a gap.

### Cells (expected, `orderBy: [{ column: <col> }]`, `ordered: true`, columns `[<col>, "n"]`)
1. `tb_week_sale_date`, `{ column: sale_date, bucket: WEEK }`: 2023-03-13:1, 2023-05-29:1, 2024-01-08:2, 2024-02-26:1, 2024-03-25:1, 2024-04-29:1, 2024-07-15:2, 2024-12-30:1, 2025-01-13:1, 2025-02-24:3, 2025-03-03:1, 2025-03-31:2, 2025-05-05:1 (18 rows). This includes Sunday rows 6, 7 and 14, which belong to the preceding Monday, and Monday rows 15 and 18, which are their own bucket.
2. `tb_quarter_sale_date`, `{ column: sale_date, bucket: QUARTER }`: 2023-01-01:1, 2023-04-01:1, 2024-01-01:4, 2024-04-01:1, 2024-07-01:2, 2024-10-01:1, 2025-01-01:5, 2025-04-01:3 (same as the SQLite `Robin_Proactive_GroupByQuarter`).
3. `tb_isoweek_year_boundary_utc`, `where: { id: { in: [10, 11] } }`, `{ column: sold_at, bucket: WEEK, timeZone: "UTC" }`: 2024-12-30:1, 2025-01-13:1. Row 10 is Tuesday 2024-12-31T23:30Z, which falls in ISO week 2025-W01 starting Monday 2024-12-30.
4. `tb_isoweek_year_boundary_berlin`, the same filter with `timeZone: "Europe/Berlin"`: 2024-12-30:1, 2025-01-13:1. Row 10 is local Wednesday 2025-01-01T00:30, so the week key stays in the previous year while the MONTH key moves to 2025-01.
5. `tb_hour_sold_at` (cheap extra coverage for the hour SQL that so far has only an exact-text test), `where: { id: { in: [1, 8] } }`, `{ column: sold_at, bucket: HOUR, timeZone: "UTC" }`: `2023-03-15T10:00:00`:1, `2024-07-20T15:00:00`:1. These are full keys with exact match.

### WP-B3 (files and tests)
- `tests/Autheris.Tests.Parity/Dialects/ParitySqlServerHostFixture.cs` (new) mirrors the PostgreSQL fixture:
  - `MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")`, the same image as `Integration/SharedDatabaseServers.cs`; `DATETRUNC` needs SQL Server 2022.
  - `ParitySqlServerCollection`.
  - seeds with `SqlCommand` parameters (`sale_date` as `DateOnly`, `sold_at` as `DateTime` of kind Unspecified, in UTC)
  - the same catalog registration as the PostgreSQL fixture
  - a WAF host with provider `SqlServer` and `DefaultSchema` `dbo`.
- `tests/Autheris.Tests.Parity/Dialects/DialectTimeBucketTests.cs` (new):
  - classes `DialectTimeBucketSqlServerTests` and `DialectTimeBucketPostgresTests`, each carrying Traits `Category=Parity` and `Dialect=Container`
  - one `[ParityTheory]` over the 5 cells, with `ParityCellInfo("timebucket", "GraphQl", <cell>, "cell", "sqlserver"|"postgres")`
  - shared expectations in a static `TimeBucketCells` table.
- `Autheris.Tests.Parity.csproj`: add `Testcontainers.MsSql` 4.16.0 (the Integration version) and run `dotnet restore` to refresh `packages.lock.json` (CI restores in locked mode).
- `DialectStructureTests.Dialect_EveryClassUsingAContainerHostCarriesTheContainerTrait`: extend the host-type detection to `ParitySqlServerHostFixture`, and add `Dialect_TimeBucketCellsCoverSqlServerAndPostgres`, which is a non-container structural test.
- Local check, without Docker: build and run `--filter "FullyQualifiedName~DialectStructureTests"`. CI runs the container cells nightly with the existing filter, and the first nightly run decides. A red SQL Server cell is a product defect, to be fixed in the planner or generator with a new issue; it is not baselined as a gap.

## Open questions (recommended defaults)
1. Should `stop_host` accept 143? **Default: yes** (owner statement), with a warning. 134 and 137 stay fatal.
2. Should `ApiHostTests` and `Meta` unit tests be added to a CI job? **Default: no** in this lane (no workflow edits). Record it as a backlog item in the master plan.
3. Is the B2 attribution inherent to security controls with no cheap removal (under 3.6 KB saved)? **Default: stop and ask the owner.** The recommended proposal is a per-bucket `MaxAllocationRatio` of 1.30 for 0.5 KB only, with the attribution as the documented reason. The 4 KB and 64 KB limits and the 10% regression rule stay unchanged.
4. Should the SQLite WEEK key shape (`%Y-%W`) be aligned with the ISO Monday date of PostgreSQL and SQL Server? **Default: out of scope.** File a follow-up issue (pre-existing divergence). The B3 week cells are container-only.
5. Should `Robin_280` and the pairwise SQL Server cells be enabled now that a SQL Server host exists? **Default: no** (scope). The `Dialect_SqlServerIsRegisteredPendingForEveryPairwiseCase` rule stays, and enabling them is a follow-up.

## Work packages
| WP | Files | Parallel |
|----|-------|----------|
| WP-B1 | `tests/Autheris.Sim.ApiHost/{SimApiHostMain,SimHostShutdown,SimHostLifetime}.cs`, `scripts/sim/run-api-fuzz.sh`, `tests/Autheris.Tests.Simulation/{Autheris.Tests.Simulation.csproj, Meta/SimHostShutdownTests.cs, ApiFuzz/ApiHostTests.cs}` | yes |
| WP-B2 | `benchmarks/baselines/sql-compiler-legacy.json`; engine files named by the attribution (`src/TrinoSqlEngine/...`); `tests/TrinoSqlEngine.Tests/...` | yes (shares the build lock) |
| WP-B3 | `tests/Autheris.Tests.Parity/{Autheris.Tests.Parity.csproj, packages.lock.json, Dialects/ParitySqlServerHostFixture.cs, Dialects/DialectTimeBucketTests.cs, Dialects/DialectStructureTests.cs}` | yes |

## Changelog (G-PLAN-REBASE drift log)
- 2026-10-25: plan written against `8cdd91de`.
- 2026-10-25 (WP-B2, measured at HEAD `570f9b48`): `Ast` 0.5 KB = 132,683 B/op (matches CI, ratio 1.260), 4 KB = 1,048,685, 64 KB = 16,212,168; the frozen AST values were 126,969 / 1,041,873 / 16,173,528. Drift: the plan assumed a fixed per-engine/per-compile cost of about 5 KB; attribution at HEAD (per-pass thread allocation hooks plus a GC allocation-tick type profile, no bisect) found the engine constructor at 2.9 KB (of which the eager `SyntaxErrorNegativeCache` 1.05 KB), cache key material 3.6 KB, and the dominant parse thread cost of about 90 KB, of which the token guards (`EnsureTokensAreSafe`, `EnsureNestingDepthWithinLimit`) materialized `IToken.Text` as a new string for every token including whitespace. Fix: both passes read the token characters as a slice of the statement (`TokenText`, same characters, no allocation; tokens outside the statement fall back to `IToken.Text`) and the syntax-error cache is created on the first remembered error (`LazyInitializer`, a null cache is a miss on lookup). Result: 0.5 KB 122,202 (ratio 1.161), 4 KB 946,614, 64 KB 14,614,414; baseline AST values re-frozen (tightening only, legacy untouched, threshold untouched).

- 2026-10-25 (Phase 4/5 status): WP-B1 (6d693323, 920a9bf1), WP-B3 (570f9b48, fae06322), WP-B2 (9920c964, 023f4ac6) implemented; Phase 5 APPROVED after one fix loop. Lead: `CapturedHost` moved to its own file (architecture rule `SimTests_NonMetaClassesDeriveFromBase`). Drift: the Berlin ISO-week cell cannot discriminate zone handling with the current seed; `tb_month_year_boundary_berlin` and `tb_isoweek_zone_shift_new_york` were added instead. Default taken: no Phase 1 PRD (owner: fix directly); Q1-Q5 defaults. B1 root cause is not reproduced locally (31 SIGTERM stops, all exit 0); the fix removes the concurrent shutdown paths seen in the host log. Container cells and sim process tests run only in the nightly jobs.

- 2026-10-25 (parity nightly 40224650412, WP-B3 container cells): both failures were test defects, no product change. (1) PostgreSQL `tb_hour_sold_at`: the GraphQL key contract is `...T10:00:00.0000000Z` (plan #348 item 5: planned-path text goes through `ConvertKey`, identical to SQLite); the cell expected the unsuffixed text and is not a date-only prefix, so the expectation now carries `.0000000Z`. (2) SQL Server `sold_at`: the fixture registered the column as DataType `timestamp`, which in T-SQL is `rowversion`; `PlainTimestampClassifier.IsPlain(SqlServer, "timestamp")` is false by design (SEC-PGP-02), so `GovernedTableReadPlanner.BuildTimeBucketExpression` threw `ArgumentException` ("Time bucketing requires a date or a timestamp without time zone column.", GovernedTableReadPlanner.cs:600-ish), mapped to `AGGREGATION_ARGUMENT_INVALID` by `AggregateFailureMapper`. SQL Server catalog discovery yields `datetime2`, so the fixture now registers `datetime2`. A unit Fact (`PlannedBucket_SqlServerTimestampIsRowversion_Throws400`) pins that SQL Server `timestamp` stays refused. The `DATE` cells passed because `date` is accepted. SQLite parity job failure was only a dispatch filter matching no tests.

## Phase 3 security conditions

Verdict: **APPROVED WITH CONDITIONS**. Open-question defaults Q1-Q5 accepted. All tests carry `[Trait("Category","Security")]` (bash self-test cases are the equivalent for the script).

### WP-B1
- SEC-LB-01: `ConsoleLifetime` removal and `SimHostLifetime` must not change the bind. The loopback checks (`IsLoopbackUrl`, `UseKestrel(Listen(IPAddress.Loopback))`, post-start address check, code 3) run before the ready file is written and are untouched; shutdown code must not rebind or add URLs.
- SEC-LB-02: The data-dir wipe runs in `finally` on every path after `--data-dir` parse (codes 0, 1, 3, 4, 5, and an exception thrown by the fallback dispose or by `cleanup` itself). A failing `ClearAllPools` must not skip `WipeDataDir` (nested try/finally). The wipe stays limited to the validated `dataDir` (never a parent, never a path from an exception or env).
- SEC-LB-03: Exception text in `startup failed` / `shutdown failed` lines and in the UnhandledException/Unobserved handlers goes to stderr only. It must pass the same canary scrub as `safe_tail` before the job log (`SIMCANARY-*` replaced, `^\s+at ` frames dropped), since the checker treats frames and canaries in the log as leaks. REQ-LB-04 stack traces are therefore allowed in the host's stderr file only when the script prints them exclusively via `safe_tail`; the host must never write request data, tokens, connection strings or headers into these lines.
- SEC-LB-04: `host_exit_ok` accepts exactly 0 and 143, by exact integer match (no range, no `>128` rule, no empty value). 143 is accepted only when `stop_host` itself sent SIGTERM to a live child and `wait` returned it; "already exited before stop" stays `HOST_STATUS=1` even if the code was 143. A host that needed SIGKILL (code 137, or still alive after 30 s) fails. 134, 137 and 1-5 never map to success. A non-numeric or unset `HOST_STATUS` fails closed.
- SEC-LB-05: Exit codes 4 and 5 must remain distinct from 0 and 143 and never be swallowed by the `Main` catch-all; the catch-all returns 1, never 0. A fallback-dispose exception must not turn into code 0.
- SEC-LB-06: Signal handlers stay idempotent and bounded (REQ-LB-02); a second SIGTERM or SIGINT must not skip cleanup or start a second dispose. No unbounded wait (every await in `RunAsync` has a timeout).
- Mandatory tests: `SimHostShutdown_ExceptionInWait_Returns4_AndCleanupRuns`, `SimHostShutdown_Timeout_Returns5_AndWipesDataDir`, `SimHostShutdown_CleanupThrows_StillWipesDataDir`, `SimHostShutdown_SecondRequest_DoesNotDisposeTwice`, `SimApiHost_StillRejectsNonLoopbackUrls_AfterLifetimeChange` (codes 2/3 unchanged, no ready file), `SimApiHost_Unhandled_ExceptionText_ContainsNoCanary`. Script `--self-test`: accept 0 and 143; reject 1, 2, 3, 4, 5, 134, 137, empty and "abc"; host-already-exited with 143 rejected.

### WP-B2
- SEC-LB-07: Every skip or lazy guard decides from the actual parsed input or policy object (AST node presence, non-empty mask/required-filter/client-list sets), never from SQL text. No keyword/regex/length/prefix heuristic on raw SQL may gate a security visitor, mask guard, required-filter injection, table-target assertion, time-zone validation or recursive-CTE gate.
- SEC-LB-08: Skip conditions are positive-only: a pass is skipped only when its input collection is provably empty; unknown, null or error state runs the pass. Lazy-init code must not return "no restriction" on a null/failed state (policy lookups, `ColumnScopeTracker`, `ClientListExpander`, `AstSecurityVisitor` partials stay fail-closed).
- SEC-LB-09: The `SyntaxErrorNegativeCache` lazy init must be thread-safe (`Interlocked.CompareExchange` or `Lazy`), keep its bounded size and its key containing no tenant-crossing result; a null cache is a miss only on lookup, never a cached "ok". The cache must never store a successful or policy-relevant outcome.
- SEC-LB-10: Telemetry short-circuits (`HasListeners()`) must not skip audit or security logging, and must not add SQL text or values to tags.
- SEC-LB-11: No change of the G8 threshold or baseline without owner approval (REQ-LB-09); re-freeze only after the REQ-LB-08 measurement, and only `astAllocatedBytesPerOp` values.
- Mandatory tests (in `tests/TrinoSqlEngine.Tests`, also under `Category=SqlGate`): `SyntaxErrorCache_LazyInit_ConcurrentFirstUse_NoLostOrCrossedEntries`, `SyntaxErrorCache_LazyInit_DoesNotCacheSuccess`, `MaskGuards_SkippedOnlyWhenNoMasks_MaskStillApplied`, `RequiredFilters_SkippedOnlyWhenNone_FilterStillInjected`, `AggregateTypeGuards_StillFireWhenInputPresent`, `RecursiveCte_GateStillFires_WhenDisabled`, `SecurityVisitor_MixedCaseAndCommentedKeywords_StillEnforced` (comment-split, case-variant, quoted-identifier inputs against masked/filtered tables), `TimeZoneValidator_StillRejectsInvalidZone`. A pre-optimization vs post-optimization differential over the existing SqlGate corpus (same accept/reject and same output SQL) is required in the Phase 4 report.

### WP-B3
- SEC-LB-12: Test-only. The MsSql container uses a generated per-run password (random, not a literal reused elsewhere), is bound to the Testcontainers default loopback mapping, is disposed in `DisposeAsync`, and the connection string is never written to logs, assertion messages or `ParityCellInfo`.
- SEC-LB-13: Seed via `SqlCommand` parameters only (no string-concatenated values); no tenant or principal bypass: the parity host uses the normal test-auth path and the same governed catalog registration as the PostgreSQL fixture, with no relaxed policy. A 5xx stays a hard failure (SEC-PAR-05); no cell is baselined as a gap.
- SEC-LB-14: No `.github/workflows` edit, no new secret; `Testcontainers.MsSql` pinned to 4.16.0 with refreshed `packages.lock.json`.
- Mandatory tests: `Dialect_EveryClassUsingAContainerHostCarriesTheContainerTrait` (extended to the SQL Server fixture), `Dialect_TimeBucketCellsCoverSqlServerAndPostgres`, `TimeBucket_InvalidTimeZone_Rejected_NoSqlLeak` (SQL Server cell: invalid `timeZone` returns a 4xx GraphQL error without SQL text or connection details).

- 2026-10-25 (parity nightly, PostgreSQL cells `max_day_param_date_postgres` and `min_year_like_date_postgres`, GraphQL channel; product defect, pre-existing on main): MIN/MAX over a DATE column answered `AGGREGATION_RESULT_TYPE_MISMATCH`. Evidence: a probe (throwaway Npgsql 10.0.3 client against a minimal fake PostgreSQL wire-protocol server, no Docker) shows `reader.GetValue` and `GetFieldType` return `System.DateOnly` for a `date` column and `System.DateTime` for `timestamp`; the executors read rows with `GetValue` (GovernedSqlExecutor.cs). A `date` column is declared kind DateTime (`ColumnValueKinds.FromDataType`), and `AggregateValueConverter.TryDateTime` accepted `DateTimeOffset`, `DateTime` and `string` but not `DateOnly`, so the aggregate value hit the mismatch. The planned bucket key is `date_trunc(.., CAST(col AS timestamp))`, a `DateTime`, which was already accepted. All mismatch sites are in AggregateResultMapper.cs (`AggregateValueConverter.Convert`, `ConvertKey`); the only one a DATE aggregate reaches is the DateTime branch via `TryDateTime`. Fix: `TryDateTime` accepts `DateOnly` as midnight UTC (the value of the SQLite date text), also for a group key on a raw date column. `TimeOnly` and other types still raise the mismatch (no value text). Unit tests in `AggregateResultMapperTests`. The two cells are not in the parity baseline, so no baseline change; the nightly container run is the end-to-end confirmation.
