# PLAN-OI-786: Idle CPU and memory growth after a large dbt sync (issue #786)

Phase 2 architect plan. Issue: [#786](https://github.com/themulle/autheris/issues/786). Related: [#675](https://github.com/themulle/autheris/issues/675), [#678](https://github.com/themulle/autheris/issues/678), [#679](https://github.com/themulle/autheris/issues/679), plan [`2026-11-13-plan-issues-catalog-scale.md`](2026-11-13-plan-issues-catalog-scale.md) (`WP-OI-SCALE-*`). Code facts verified on `main` @ `cfbcb7b28`. Bug fix: no new options, no `freeze-exception` needed (default taken).

## 1. Root cause (with evidence)

The reporter's suspicion is half right: the periodic check does build a full candidate model on every tick, but that is the steady 6 % baseline that a restart does not remove. The 115 % comes from queued Hot Chocolate (HC) schema rebuilds, one per "GraphQL catalog changed" line, that run one after another long after the sync.

- **RC-1 Rebuilds are queued, never coalesced (main CPU and memory driver).** `CatalogGraphQlTypeModule.CheckForCatalogChangesAsync` (`src/Autheris.GraphQL/Catalog/CatalogGraphQlTypeModule.cs:277-290`) raises `TypesChanged` for every detected change. HC 16.7.5 `RequestExecutorManager.EvictExecutor` writes each event into an unbounded channel. `ConsumeExecutorEvictionsAsync` runs one full executor rebuild per entry, one after another, with no de-duplication. The new executor's change monitor is registered when the build starts, so changes seen during a rebuild queue more rebuilds. At 2,976 tables one build takes about 231 s and 350 CPU-s (the reporter's startup figure). Six change lines therefore mean six full builds in sequence (about 35 to 60 CPU-minutes with a 6 to 7 GB heap). They outlast the 40-minute observation, and each one allocates a full new schema while the old executor is still alive until its eviction timeout.
- **RC-2 Queued rebuilds re-read the whole catalog.** The first rebuild takes the pending model (`CatalogGraphQlTypeModule.cs:326`). Every later queued rebuild finds no pending model and calls `CatalogSchemaModel.BuildAsync` against the database again (`:346-370`). That is why the reporter saw `schema build progress` lines without a preceding `GraphQL catalog changed` line.
- **RC-3 The version short-circuit is dead in production.** The DI registration (`src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.GraphQL.cs:128-137`) never passes `versionProvider`, `epochService` or `timeProvider`. So `_versionProvider` is always null, the early exit at `CatalogGraphQlTypeModule.cs:223-235` never runs, and every 60 s tick (`GraphQlOptions.CatalogSchemaRefreshSeconds = 60`) builds a full candidate model. Only unit tests pass a version provider (`CatalogGraphQlTypeModuleScaleTests.StructureChange_BuildsModelOnce`), so the #679 PR 1 acceptance criteria hold only in tests. No commit ever wired it (`git log -S "versionProvider: sp."` is empty).
- **RC-4 Each tick is expensive at this size.** `GovernanceRepository.GetAllTablesAsync` caches metadata with a 30 s TTL (`GovernanceRepository.cs:25`), which is shorter than the 60 s tick. Each tick therefore reloads all 2,976 tables and 59,846 columns over TLS (`GovernanceRepository.Catalog.cs:159-230`). With zero relations, `GetAllRelationsAsync` returns an empty list, which triggers the per-table fallback: 2,976 single `GetRelationsForTableAsync` round-trips per tick (`CatalogSchemaModel.cs:413-441`). The reverse-relation pass scans every table for every table, O(n^2) (`CatalogSchemaModel.cs:500-560`).
- **RC-5 Timer re-entrancy and logging gaps.** `new Timer(_ => _ = CheckForCatalogChangesAsync(), …)` (`:304`) fires without waiting for the previous check. A check slower than the interval queues more waiters on `_checkGate`. The progress line (`CatalogSchemaModel.cs:647`) does not say whether it comes from a check or a rebuild. No line reports that a build finished or how long it took, and HC ignores rebuild failures silently (`catch { // Ignore }`).

**Local reproduction (synthetic, in-memory fakes, filtered unit run, test not committed):**

| Case | Result |
|---|---|
| 5 idle ticks, module built like the DI registration (50 tables) | `GetAllTablesAsync` 5 times, `GetRelationsForTableAsync` 250 times |
| Candidate model at 2,976 tables x 20 columns, in memory (no DB, no HC) | 0.3 to 0.7 s wall, 0.4 to 1.1 CPU-s, 36 to 47 MB allocated per tick |
| 6 catalog changes, 5 of them during one in-flight rebuild (counted with an `IRequestExecutorWarmupTask`) | 6 full executor builds; `GetAllTablesAsync` 11 times (4 queued rebuilds re-read the catalog) |

**Why a restart fixes it:** a restart drops HC's in-memory eviction queue, the retained executors and the heap they inflated. Startup builds exactly one schema. After that only RC-3 and RC-4 remain: the per-minute full candidate build, which matches the reporter's 6 % of one core and RSS growth from 2.8 to 4.0 GB after a restart. By the code, the queue is finite (one rebuild per change line), so CPU should drop to the baseline once it drains. If it stays high for more than 90 minutes, there is a further loop, and the finish line from WP-786-4 will show it.

## 2. Solution design

1. Wire the version short-circuit (RC-3). Add a 10-minute full-check backstop so a write that misses a version bump still reaches the schema.
2. Coalesce rebuild requests (RC-1, RC-2). At most one rebuild runs and at most one waits behind it, and the waiting one always uses the newest model. A leading-edge and quiet-period rule stops rebuilds during a long sync: the first change after a quiet period rebuilds immediately; later changes wait until one check sees no further change, or until 5 minutes have passed.
3. Make the timer non-reentrant and driven by `TimeProvider` (RC-5).
4. Logging (RC-5):
   - Every log line carries `phase=check|rebuild`.
   - One `GraphQL catalog schema build finished` line per executor build, with tables, omitted tables, `elapsedMs` and trigger.
   - A slow check (5 s or more) is logged at Information level.
   - Counts and durations only, no catalog names.
5. Cheaper ticks and bounded memory (RC-4):
   - The bulk relation read is authoritative, so an empty result means "no relations" instead of 2,976 fallback queries.
   - Reverse relations are found through an index (O(n)) instead of a scan of every table.
   - Memory becomes bounded because the churn stops: no queued full builds and no per-minute catalog reload. No new cache limits (those stay in #678).

## 3. Work packages (one PR, about 240 src lines; order WP-1, WP-2, WP-3, WP-4, WP-5)

Local tests: `NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~<Class>" -m:2 -nodeReuse:false`. Write the regression tests first; they must fail on `main`.

### WP-786-1 Wire version provider plus backstop (about 50 src)
- `GatewayServiceCollectionExtensions.GraphQL.cs:128-137`: move the construction into `internal static CatalogGraphQlTypeModule CreateCatalogGraphQlTypeModule(IServiceProvider sp, GatewayOptions o)`. Pass `versionProvider: sp.GetService<ICatalogVersionProvider>()`, `epochService: sp.GetService<IEpochValidationService>()` and `timeProvider: sp.GetService<TimeProvider>()`.
- `CatalogGraphQlTypeModule`: add `internal static readonly TimeSpan FullCheckBackstopInterval = TimeSpan.FromMinutes(10)` and a `_lastFullCheckTimestamp` field. The version early exit (`:228-234`) applies only while `now - _lastFullCheckTimestamp < FullCheckBackstopInterval`.
- Unit tests, in the new class `tests/Autheris.Tests.Unit/GraphQL/CatalogGraphQlIdleRebuildTests.cs` (synthetic 3,000-table fixture helper `MakeCatalog(int tables, int columns)`):
  - `DiComposedModule_IdleTicks_DoNotReadCatalog`: built through `CreateCatalogGraphQlTypeModule`, 5 checks at an unchanged version, `GetAllTablesAsync` count +0. This is the regression test that reproduces the loop.
  - `VersionUnchanged_BackstopElapsed_RunsOneFullCheck` (uses `FakeTimeProvider`).
  - `[Trait("Category","Security")] HiddenTableWithoutVersionBump_RemovedAfterBackstop`.
- Integration impact: with the short-circuit, fixtures that write catalog rows by direct SQL without a version bump no longer refresh the schema. Inspect and adapt `tests/Autheris.Tests.Integration/TestSupport/GraphQlSchemaReadiness.cs`, `McpDatasetToolsEndToEndTests.cs`, `RowFilterChannelParityTests.cs`, `Transports/TransportEnforcementTests.cs` and `Governance/Issue270CatalogGraphQlHmacIntegrationTests.cs`, `Issue270GovernanceSecurityVerificationTests.cs`. Bump the version through the repository, or call the version flusher.

### WP-786-2 Coalesce rebuild requests (about 60 src)
- `CatalogGraphQlTypeModule`: add `private int _rebuildRequested`.
  - In `CheckForCatalogChangesAsync`, after `_pendingBuild` is written (the newest model wins), raise `TypesChanged` only when `Interlocked.CompareExchange(ref _rebuildRequested, 1, 0) == 0` and the event has subscribers. Otherwise log at Debug: coalesced.
  - At the start of `CreateTypesAsync`, call `Interlocked.Exchange(ref _rebuildRequested, 0)` before the `Interlocked.Exchange(ref _pendingBuild, null)` at `:326`. Reset it as well in the catch block at `:372`.
  - If a request has waited longer than `RebuildRequestTimeout = 15 min` without a `CreateTypesAsync`, reset it and raise again.
- Tests:
  - `ChangesDuringRebuild_AtMostOneTrailingRebuild`: block the first rebuild in an `IRequestExecutorWarmupTask` with `ApplyOnlyOnStartup = false`, then make 5 more changes. Expect 2 executor builds (6 today), and the final schema contains the newest table.
  - `NoSubscriber_ChangeIsNotLatched`.
  - `[Security] HideTableDuringRebuild_FinalSchemaOmitsTable`.
  - `[Security] RebuildFailure_ResetsLatch_NextChangeRebuilds`.

### WP-786-3 Quiet period and non-reentrant timer (about 50 src)
- Replace `System.Threading.Timer` (`:295-306`) with `_timeProvider.CreateTimer`. The timer path takes `_checkGate.WaitAsync(0)` and skips the tick when the gate is busy; explicit callers keep waiting.
- Debounce rule in `CheckForCatalogChangesAsync`:
  - Raise immediately when the previous check saw no change (leading edge).
  - Otherwise defer, keeping the newest pending model, until one check sees no further change, or until `MaxRebuildDeferral = 5 min` since the first deferred change.
- Tests:
  - `TimerTicks_WhileCheckRuns_DoNotQueue` (a blocked repository plus 5 `FakeTimeProvider` ticks give 1 read).
  - `SingleChange_RebuildsWithoutAddedDelay`.
  - `ChangeEveryTick_RebuildsAtMostEveryFiveMinutes`.
  - `BurstEnds_TrailingRebuildOnFirstQuietTick`.

### WP-786-4 Build-finished and phase logging (about 50 src)
- `CatalogSchemaModel.MaybeLogBuildProgress` (`CatalogSchemaModel.cs:627-651`): add a `string phase` parameter and append ` phase={Phase}`. Call sites: `BuildAsync` gets `phase` from the caller ("check" or "rebuild"); `CatalogGraphQlTypeModule.cs:485` uses "rebuild".
- Module:
  - Information line `GraphQL catalog schema build started: trigger={Trigger}` (`startup`, `change` or `coalesced`) in `CreateTypesAsync`.
  - New `internal void ReportExecutorCreated()` that logs `GraphQL catalog schema build finished: tables={Tables} omitted={Omitted} elapsedMs={ElapsedMs} phase=rebuild`, timed from the start of `CreateTypesAsync`.
  - The check logs `GraphQL catalog check finished: changed={Changed} elapsedMs={ElapsedMs}` at Debug level, or at Information level when it took 5 s or more.
- Hook: a new `CatalogSchemaBuildDiagnosticListener : ExecutionDiagnosticEventListener` in `src/Autheris.GraphQL/Catalog/` overrides `ExecutorCreated(string name, IRequestExecutor executor)`; HC calls it at the end of every build (`RequestExecutorManager.cs:198`). Register it with `.AddDiagnosticEventListener(...)` in `GatewayServiceCollectionExtensions.GraphQL.cs`. Confirm the override exists in HC 16.7.5; if it does not, call it from `GraphQlExecutorWarmupTask.WarmupAsync` (`src/Autheris.Api/Hosting/Warmup/GraphQlExecutorWarmupTask.cs`) instead.
- Tests:
  - `Rebuild_LogsStartedAndFinishedWithDuration`.
  - `UnchangedCheck_LogsAtDebugOnly`.
  - `[Security] BuildLogs_CarryNoCatalogNames`.
  - Adapt `CatalogGraphQlTypeModuleScaleTests.StartupBuild_LogsProgress`; its `elapsedMs=5200` assertion stays.

### WP-786-5 Cheaper ticks: authoritative bulk relations, O(n) reverse relations (about 40 src)
- `ITableRelationRepository` (`src/Autheris.Application/Common/Interfaces/IGovernanceRepository.cs:595`): add the default member `bool BulkRelationsAuthoritative => false;`. The governance repository (`src/Autheris.Infrastructure/Persistence/GovernanceRepository.CatalogRelations.cs`) returns `true`.
- `CatalogSchemaModel.cs:413-441`: use the bulk result whenever it is authoritative, also when it is empty. Test doubles keep the fallback, so the 13+ unit tests that use `GetRelationsForTableAsync` stay unchanged.
- `CatalogSchemaModel.cs:500-560`: build `relationsByChild` once, iterating `tableBuilders` in their existing order, so reverse field names, suffixes and order stay byte-identical.
- Tests:
  - `CatalogSchemaModelRelationTests.AuthoritativeEmptyBulk_NoPerTableReads`.
  - `ReverseRelations_IndexedLookup_FieldNamesUnchanged`: a golden fixture with colliding reverse names; record its output on `main` first.
  - `CatalogSchemaModelTests` and `CatalogGraphQlSchemaTests` must stay green.
  - Parity: none (`tests/Autheris.Tests.Parity` has no GraphQL schema refresh path).

**Deferred (not in this PR):** have the GraphQL module read tables from `ICatalogSnapshotProvider` instead of `GovernanceRepository`, which removes the duplicate copy of 60k columns. This belongs to #678 / `WP-OI-SCALE-7` together with #677 PR 4b.

## 4. Security conditions (for Phase 3 to confirm)
- SEC-786-1: a skipped check (same version) never leaves the schema stale longer than `FullCheckBackstopInterval`. A transport visibility change still forces a rebuild with a version provider wired. Test: adapt `TransportVisibilityChange_ForcesFullRebuild` to pass `versionProvider`.
- SEC-786-2: coalescing never drops the newest change. The trailing rebuild uses the newest pending model. A failed or unsubscribed rebuild never leaves the latch set. If the build fails, the fail-closed empty catalog schema stays in place (`:372-380`).
- SEC-786-3: new log lines carry counts, durations and the trigger only; no table, column or source names.
- The data-path gates are unchanged; the schema is not an enforcement point.

## 5. Measurement (reporter scenario: 2,976 tables, 59,846 columns, 0 relations, 1,220 masking rules)
- Setup: a new `[Trait("Category","Scale")]` integration test with a synthetic 3,000 x 20 catalog in the Testcontainers PostgreSQL, plus 6 version bumps at 60 s intervals. On a host, repeat the reporter's sync and sample CPU (`utime + stime` from `/proc/<pid>/stat`) and `VmRSS` over the same intervals.
- Acceptance:
  - M1: idle CPU from minute 5 to minute 40 after the last change is at most 2 % of one core (today 113 to 117 %; 6 % after a restart).
  - M2: executor builds after the sync: at most 3 (today 6 or more); exactly one `build finished` line per build.
  - M3: RSS at minute 40 is at most RSS at minute 5 x 1.10, and at most 15 % above the restart baseline.
  - M4: `GetAllTablesAsync` from the GraphQL module during 40 idle minutes: at most 4 (backstop only).
  - M5: an unchanged tick makes no `GetRelationsForTableAsync` calls.
- Record the results in `benchmarks/results/catalog-scale.json` (SCALE-0 format) when the harness exists.

## 6. Risks
- A catalog write path that does not bump the version is now detected only by the 10-minute backstop. Mitigation: SEC-786-1 and the integration fixture review in WP-786-1.
- The quiet period delays the schema update during a burst by up to 5 minutes. Single edits are not delayed.
- HC internals (an unbounded eviction channel) may change in later versions. The module-side latch does not depend on them.
- Each single rebuild still takes about 4 minutes at 3,000 tables. Reducing that is #679 PR 3 (per-table descriptor cache).

## 7. Relation to #675, #678, #679
- #675: the snapshot coalescing (`CatalogSnapshotProvider`, PR 1/2) does not cover the GraphQL module, which polls on its own and raised `TypesChanged` without debounce. WP-786-2/3 apply the same single-flight and coalescing idea there.
- #678: the 7 GB is churn (queued full builds plus a per-minute full reload under Server GC), not an unbounded cache. WP-786 removes the churn, and the #678 cache limits stay separate.
- #679: PR 1 added the version short-circuit, the fingerprint cache and progress logs, but the DI wiring was missing (RC-3). WP-786-1 completes it, and the #679 scale measurement (PR 2) gains the finish line from WP-786-4.
