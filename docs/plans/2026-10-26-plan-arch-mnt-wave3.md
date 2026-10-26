# Plan: ARCH-MNT wave 3 - G7 runtime fix and follow-ups

Parent: `docs/plans/2026-10-26-plan-architecture-maintainability.md` (sections 8 and 9.6/9.7).
Phase 2 (`csharp-architect`), base `origin/main` adb5f395. Owner decisions 2026-10-26: (A) fix the G7/Stryker nightly timeout;
(B) ARCH-MNT-06-FU-02, 06-FU-03, 06-FU-04, 15-FU-01, 23-FU-01, 08-FU-01. Out of scope: release tagging, DEF-01..DEF-04.
Defaults taken are marked "(default taken)". No PRD exists for these items; the parent plan rows are the requirement.

## 1. PR cut

| PR | Branch | Items | Files (main) | Phase 3 |
|---|---|---|---|---|
| PR-SQLGATE | `-12` | G7 fix, 15-FU-01 | `.github/workflows/sql-compiler-gate-nightly.yml`, `stryker-config.json`, `tools/sqlgate/g7-scope.sh`, `tools/sqlgate/g7-summary.py`, `tools/SqlGate.BenchCheck/*`, `tests/Autheris.Tests.SqlCompilerGate/*` | yes (gate integrity) |
| PR-STARTUP | `-13` | 06-FU-03, 06-FU-04 | `StartupChecks/*`, `GatewayStartupValidator.cs`, `deploy/helm/autheris/{templates/deployment.yaml,values*.yaml}`, `deploy/scripts/verify-hardening.sh`, `docs/features/f-ops-02-...md` 5.2, `docs/configuration-guide.md` | yes |
| PR-SCIM | `-14` | 06-FU-02 | `IdentityRealmsOptions.cs`, `ScimService.cs`, new `ScimMassChangeGuard.cs`, `IIdentityStoreRepository.cs`, `IdentityStore.cs`, repository partials, `ScimUsersEndpoints.cs` | yes (full) |
| PR-TIME | `-15` | 23-FU-01 | `InMemoryRebacStore.cs`, `RedisRebacStore.cs`, `ZanzibarRebacEvaluator.cs`, `GatewaySecurityServiceExtensions.cs`, `ConsentCacheService.cs`, 3 test files | yes (light) |
| PR-OLAP | `-16` | 08-FU-01 | `OlapSqlInspector.cs`, `OlapSqlPolicy.cs`, `OlapStagingService.cs`, OLAP tests | yes (light) |

15-FU-01 joins the G7 PR (proposed PR-BENCH) because both change `sql-compiler-gate-nightly.yml`. The PRs share no source file.
PR-STARTUP edits `docs/features/f-ops-02` only after the parallel docs commit has landed (rebase first).

## 2. PR-SQLGATE, part A: G7 within its time budget

**Root cause (verified).** Stryker.NET mutates every file of `TrinoSqlEngine.csproj` and applies `mutate` only afterwards as a status filter.
`CsharpMutationProcess.Mutate` runs the orchestrator over `projectInfo.GetAllFiles()` and compiles all mutants. `FilterMutants`
(the source of "Removed by mutate filter") runs after compilation. The run logs fit this: compile errors in out-of-scope files come
before "16722 mutants created", and 9,177 mutants are removed by the filter. So about 16.7k probes, including those in the hot
parser/builder paths (`SqlAstBuilder`, `SqlDialectGeneratorBase*`), run inside every test. That explains the ~12-min initial phase
and the 1h40 per-test coverage capture, because every test reports thousands of covered ids. `test-case-filter` worked (7,564 SqlGate
tests, not ~9,300). `ignore-mutations` and `mutation-level` act globally and would drop mutants from the security files, and
`ignore-methods` is also only a post-filter. A mutant that a `// Stryker disable all` comment marks at creation gets status `Ignored`,
and `MutationStore.StoreMutations` injects only `Pending` mutants, so it is never compiled in (source: stryker-net master).

1. **Scope at injection, CI-only.** New `tools/sqlgate/g7-scope.sh --apply`:
   - It reads the `mutate` globs from `stryker-config.json` with `jq`.
   - In the ephemeral checkout it prepends `// Stryker disable all : outside the G7 mutate scope` to every `src/TrinoSqlEngine/**/*.cs` outside the globs. `obj/`, `bin/` and auto-generated files are skipped.
   - Fail closed: exit 1 when the in-scope set is empty, when an in-scope file would be touched, or when the tree is not clean.
   - It writes the in/out file counts to `$GITHUB_STEP_SUMMARY`. Locally the dev restores the tree with `git checkout -- src/TrinoSqlEngine`.
   - The mutate set, `mutation-level` (Standard), `coverage-analysis` (perTest, now explicit) and `break: 85` stay unchanged. Out-of-scope mutants were already `Ignored`, so the score formula is unchanged.
2. **Own job.** `sql-compiler-gate-g7` runs in the same workflow, parallel to the gate job, for both the schedule and the dispatch (cutover profile).
   - Steps: checkout, setup-dotnet, `restore --locked-mode`, `dotnet tool restore`, scope, G7, summary, upload. Concurrency is 4, set in `stryker-config.json`.
   - Timeouts: `timeout-minutes: 120` for the job and `timeout-minutes: 100` on the G7 step.
   - The gate job loses its G7 steps, so a G8 failure no longer hides G7. Its timeout drops to 60 (it took ~20 min before G7).
3. **No silent timeout.**
   - A step timeout is a step failure, so the job ends `failure`, not `cancelled`.
   - G7 runs with `--log-to-file`. `g7-summary.py` (`if: always()`, stdlib only) parses `StrykerOutput/*/reports/mutation-report.json` and writes score, status counts and mutants per file to the job summary.
   - The summary step exits 1 in three cases: the report is missing ("G7 did not complete: timeout or cancel", followed by the last progress lines of the Stryker log); fewer than 2,500 valid in-scope mutants (vacuous-scope guard, calibrated from the local run); any non-`Ignored` mutant outside the globs.
   - `StrykerOutput` (log plus any partial output) is uploaded on `always()`. Opening an issue on failure needs `issues: write`, which is not done (default taken).
4. **Config guard test.** `StrykerConfigGuardTests` in `Autheris.Tests.SqlCompilerGate` asserts the following in `stryker-config.json`: `break` == 85, the four security globs are present, there is no `!` exclusion of those globs, `mutation-level` is absent or Standard, `coverage-analysis` is perTest, and `ignore-mutations`/`ignore-methods` are absent. It also asserts that the workflow G7 step has a `timeout-minutes` below the job timeout.
5. **Rejected:** `since`/`with-baseline` (the nightly needs the full score), `coverage-analysis: off/all`, `disable-mix-mutants`, a lower `mutation-level`, and a weekly-only schedule (stays the fallback, see 7).
6. **Local verification** (dev, 16 cores, 11 GB RAM, one build at a time):
   - (a) Calibrate: compare `time dotnet test tests/TrinoSqlEngine.Tests -c Debug --filter Category=SqlGate` with the CI "Gate tests, Debug" step duration (`gh run view 40287212164 --log`). r = CI / local.
   - (b) In a clean scratch worktree, run `g7-scope.sh --apply`, then `flock /work/wt/.build.lock nice dotnet tool run dotnet-stryker --config-file ../../stryker-config.json --log-to-file` from `tests/TrinoSqlEngine.Tests`.
   - (c) Record the phase timestamps (initial run, mutants created, coverage capture, testing) and the status counts in the PR body.
   - (d) Check: in-scope created mutants equal the old 16,722 - 9,177 (±1%), and there are no Safe-Mode compile errors in out-of-scope files.
   - (e) Remove `StrykerOutput` and bin/obj afterwards.
7. **Acceptance.** Projected CI G7 step = local G7 wall time × r must be ≤ 75 min (limit 100, job 120). If the projection is above 75 min, add stage 2 in the same PR:
   - a 2-shard matrix (`Ast/Emit/*` | Security plus visitors), each shard with `--break-at 0` and its own scope apply;
   - an aggregate job that merges both JSON reports and enforces score ≥ 85 with the Stryker formula (killed+timeout)/(killed+timeout+survived+nocoverage), which is equivalent to today's single gate.
   - The first nightly after merge must finish green or red, never cancelled. Its timings are recorded in parent section 8.

## 3. PR-SQLGATE, part B: 15-FU-01 cold-cache gate (S-M)

State:
- `Program.cs:28` globs only `*SqlCompilerBenchmarks-report-full*.json`.
- `BenchResultReader.cs:23-27` keeps only `BenchEngine` methods.
- `SqlParserCacheBenchmarks` (`ParseWarm` baseline, `ParseCold` with cap 1) is not run in CI.
- The text of ruling D1 is not recorded in `docs/plans`. SC-11 ("cold-cache latency, cap forced low, gated with BenchCheck") is taken as its content (default taken).

Design:
- New `ParserCacheResultReader.ReadSamples(json)` returns `(Warm, Cold)` P99 from `Statistics.OriginalValues`, using the same percentile helper as G8.
- `BenchThresholds.MaxColdWarmP99Ratio` is a ratio, so it does not depend on the machine. Its value is the local median of 3 runs × 1.5, with a comment that cites the measurement.
- `Program` also evaluates `*SqlParserCacheBenchmarks-report-full*.json`. Fail closed: a missing report or a missing method is a `missing-data` violation (exit 1).
- The workflow G8 step adds `"*SqlParserCache*"` to `--filter`.
- Tests in `BenchCheckReaderTests`/`BenchCheckTests`: `ParserCache_ReadsWarmAndColdP99`, `ParserCache_RatioAboveLimit_Violation`, `ParserCache_ReportMissing_MissingData`, `ParserCache_MethodMissing_MissingData`.

## 4. PR-STARTUP: 06-FU-03 and 06-FU-04

**06-FU-03 (S).** State:
- `OtlpTransportOptionsValidator.cs:64-73` fails without the flag and only logs a warning when the flag is set.
- The startup report lists come from `GatewayOptions.GetAllActiveBypasses()` (`GatewayOptions.cs:215`). `TelemetryOptions` (`Gateway:Telemetry`) is not part of it.

Design (WARN, not DANGER, default taken):
- Reason: DANGER aborts outside Development (`GatewayOptions.cs:167`), but the flag exists as the explicit non-Development opt-in, and the validator already fails closed without it.
- `StartupCheckContext` gets an `ExtraWarnings` list. `GatewayStartupValidator.ValidateStartup` takes an optional `TelemetryOptions`, which the call site `GatewayServiceCollectionExtensions.cs:420` binds.
- When `Enabled && Otlp.AllowInsecureTransport`, the list gets `WARN:otlp_allow_insecure_transport (Gateway:Telemetry:Otlp:AllowInsecureTransport)`. The warnings banner in `GraphQlAndSwitchStartupChecks.cs:130` prints it.
- Tests in `GatewayStartupChecksTests`: `InsecureOtlp_FlagSet_ReportedAsWarning`, `InsecureOtlp_FlagUnset_NotReported`, `InsecureOtlp_TelemetryDisabled_NotReported`. Update the startup golden (`GatewayStartupValidatorGoldenTests`).

**06-FU-04 (M).** State:
- `deployment.yaml:62` uses `gateway.drainDelaySeconds` only for the `preStop` sleep. `gateway.shutdownTimeoutSeconds` (`values.yaml:111`) is not rendered.
- The app validates NF-HA-01a/b (`GatewayResilienceOptionsValidator.cs:19-27`).
- The chart default (shutdown 30, query timeout default 30) would break 01a once it is wired.

Design:
- `deployment.yaml` env renders `Gateway__HighAvailability__DrainDelaySeconds`, `__ShutdownTimeoutSeconds`, `__QueryTimeoutSeconds` (new value `gateway.queryTimeoutSeconds`) and `__TerminationGracePeriodSeconds` (from `.Values.terminationGracePeriodSeconds`).
- Defaults: `queryTimeoutSeconds` is 20 in `values.yaml` and 35 in `values.production.yaml` (default taken).
- The template fails the render (`fail`) on two conditions:
  - shutdown < query + 10 (01a);
  - grace < 2×drain + shutdown + 10. 01b plus the `preStop` sleep, because the kubelet grace covers the preStop sleep and the in-process drain (`TrafficDrainService.cs:91-95`). The current values give 50 ≤ 60 and 75 ≤ 75.
- `verify-hardening.sh` (CI `helm-render`) asserts the four env vars and a failing render with `--set terminationGracePeriodSeconds=30`.
- New `tests/Autheris.Tests.Unit/Deployment/HelmHighAvailabilityWiringTests.cs`: the text asserts follow `HelmGovernanceMigrationJobTests`, and the render asserts follow `HelmIngressAdminExposureTests` when `helm` is installed. Tests: `Deployment_RendersHighAvailabilityEnv`, `Values_DefaultsSatisfyNfHa01`, `ProductionValues_SatisfyNfHa01`, `Render_GraceTooShort_Fails`, `VerifyHardeningScript_AssertsHaWiring`.
- `ShippedConfigurationKeysTests` picks up the new `Gateway__` keys.
- Docs: f-ops-02 5.2 (line 211) and the `docs/configuration-guide.md` NF-HA-01 table, described as current behaviour.

## 5. PR-SCIM: 06-FU-02 mass-change protection (M; feature-sized)

State:
- `ScimOptions` (`IdentityRealmsOptions.cs:44`) has only `AllowedIpRanges`.
- Deactivation (PUT `ScimService.cs:74`, PATCH `:127`) and DELETE (`:148`) call `DeprovisionAndLockSubjectAsync`, which persists `ScimLock(tenant, realm, user, reason, source, now)` (`IdentityDeprovisioningService.cs:71`).

The item is feature-sized: an "approve to resume" workflow needs admin endpoints and UX. Following the owner decision, this PR ships only a **minimal rate guard**; the approval workflow goes to the backlog.

Design:
- Options `Gateway:IdentityRealms:Scim:MassChangeProtection { Enabled = true, MaxDestructiveOperations = 100, WindowMinutes = 60 }`. Ranges are [1, 100000] and [1, 1440], validated in `IdentityRealmsScimOptionsValidator`. Turning the guard off needs an explicit `Enabled=false` (default taken).
- New `ScimMassChangeGuard.EnsureAllowedAsync(tenant, realm, ct)` runs before every user deactivation or delete. It counts `ScimLock` rows with `LockedAt >= now - window` through the new `IIdentityStoreRepository.CountLocksSinceAsync`, with one shared SQL in `IdentityStore.cs:445` style and delegations in the dialect repositories. Because the lock table is shared, the count is cluster-wide. Time comes from `TimeProvider`.
- At or above the limit it throws `ScimMassChangeBlockedException`, which `ScimUsersEndpoints` maps to 429 plus `Retry-After` plus a SCIM error body without caller data. It also writes the audit event `SCIM_MASS_CHANGE_BLOCKED` (`RecordRequiredAsync`) and a warning log.
- A count failure propagates: the operation fails and nothing is deprovisioned (fail closed).
- Accepted race: concurrent nodes can exceed the limit by at most the number of in-flight requests.
- Group delete is not counted (default taken; question Q-S2).
- Tests:
  - `ScimMassChangeGuardTests`: `UnderLimit_Allows`, `AtLimit_Blocks429`, `WindowElapsed_AllowsAgain` (FakeTimeProvider), `Disabled_Allows`, `CountFails_NoDeprovision`, `OtherRealm_NotCounted`.
  - endpoint test `Delete_Blocked_ReturnsScimError429WithoutCallerData`.
  - integration `CountLocksSince_AllDialects`.

## 6. PR-TIME: 23-FU-01 (S-M)

State:
- `InMemoryRebacStore.cs:88`, `RedisRebacStore.cs:109`, `ZanzibarRebacEvaluator.cs:146,209,233,283` and `ConsentCacheService.cs:176,315,404` (the L2 envelope expiry) read `DateTimeOffset.UtcNow`.
- `RebacTupleExpiryTests.cs:60` waits 600 ms in real time.

Design:
- Each of these types gets an optional last constructor parameter `TimeProvider? timeProvider = null`, which falls back to `TimeProvider.System`. The code reads the time through `GetUtcNow()`.
- DI (`GatewaySecurityServiceExtensions.cs:87-97`, consent cache registration) passes `sp.GetService<TimeProvider>()`. `TryAddSingleton(TimeProvider.System)` is added there too.
- The L1 `MemoryCache` expiry keeps its own clock (out of scope).
- Tests: `RebacTupleExpiryTests` uses `FakeTimeProvider.Advance` instead of `Task.Delay`; new `ConsentCache_L2EnvelopeExpired_IsMiss`; `Evaluator_CachedDecisionPastExpiry_Reevaluated`.
- **Flake root cause:** `StartupWarmupDiagnostics` uses histograms of the process-global `GatewayDiagnostics.Meter`, and the `MeterListener` in `StartupWarmupTests.cs:312-327` filters by name only. Parallel classes that run the warm-up (`StartupWarmupExecutorTests`, host-based tests) add `step.duration` measurements with other step names, which breaks the exact `ShouldBe` on line 342.
- Fix (test-only): use unique step names (`Okay-{guid}`, `Bad-{guid}`), filter the recorded measurements to those names before the asserts, and keep the instrument-set assert as `ShouldContain`.

## 7. PR-OLAP: 08-FU-01 (S)

State:
- `OlapSqlRejectedException` (`OlapSqlInspector.cs:12-32`) takes a free `message` string.
- `ex.Message` reaches responses at `OlapStagingService.cs:122` and `:538`. There are 15 throw sites with 7 distinct messages.

Design:
- `enum OlapSqlRejectionReason { InvalidStatement, SingleSelectOnly, ProhibitedFunction, EscapeStringsProhibited, GatewayDialectRequired, ConstructNotAllowed, ColumnPolicy }`.
- A static table maps each reason to `(Code, fixed message)`. The only constructors are `(reason)` and `(reason, Exception inner)`, and there is no string parameter. `Code` is derived from the table, and responses keep using `Code` and `Message`.
- Tests: `EveryReason_HasCodeAndFixedMessage`, `Exception_HasNoStringMessageConstructor` (reflection), `Rejection_ResponseBody_EqualsFixedMessage`. The existing `OlapSqlInspectorTests` and `ConvertTimeZoneSurfaceTests` assertions on `Code` stay green.

## 8. Phase 3 questions

- Q-G1: Is the CI-only source rewrite (`// Stryker disable all` outside the globs) acceptable gate integrity, given the guards in 2.1, 2.3 and 2.4?
- Q-G2: Is the stage-2 sharding equivalent: shards at break 0 and the aggregate at ≥ 85?
- Q-G3: Is the 2,500-mutant floor adequate?
- Q-O1: WARN versus DANGER for `AllowInsecureTransport`. Should Production also require `AllowInsecureWarnFlagsInProduction`, and should the health status show "degraded"?
- Q-H1: Is the stricter Helm guard (2×drain) right, or should the `preStop` sleep and the in-process drain be merged?
- Q-S1: Is 429 + Retry-After (throttle) acceptable, or does mass deprovisioning need a hard stop (409) and admin reset? Blocking delays real revocations.
- Q-S2: Should group deletes count? Are the defaults 100 per 60 min right?
- Q-T1: Is an optional `TimeProvider` constructor parameter acceptable on security expiry paths (no configuration input)?
- Q-P1: Is the reason enum enough, or should the inner exception message also stay out of the logs (`OlapStagingService.cs:722`)?
