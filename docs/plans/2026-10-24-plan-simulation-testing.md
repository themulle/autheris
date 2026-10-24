# REQ-SIMTEST: Phase 2 architecture plan (simulation testing)

Input PRD: [`2026-10-24-req-simulation-testing.md`](2026-10-24-req-simulation-testing.md) (REQ-SIM-01..61). Branch `feat/simtest`. Code facts verified on `feat/simtest` (28c5fa8e); re-verified 2026-10-24 on `feat/simtest` over `origin/main` 5aa7db8f (sections 0a, 0b, 3a, 4-4c added in that pass).

## 0. Inputs and defaults taken

| Q | Decision |
|---|---|
| O1 | Owner: import sqllogictest only with confirmed terms. Verified 2026-10-24: `COPYRIGHT.md` (D. R. Hipp, sha256 `ff72613e...a5c9`) lets the user pick GPL, BSD, MIT or CC0, with no attribution required. Its scope ("this program") does not name the test files explicitly. Decision: **commit no sqllogictest file**. Fetch at job time from a pinned commit with per-file sha256 (no redistribution). Record the MIT election and provenance in `THIRD-PARTY-NOTICES.md`. Unit-test fixtures are written by us. |
| O2 | Owner: no TPC content committed. Optional job-time generation behind `AUTHERIS_SIM_TPC=1` (WP1b, deferred). Not part of the >=5000 count. |
| O3 | Owner: P2 read phase against the fixture host. Mutating methods run only in a separate write phase against a freshly created throwaway host. |
| O4 | CsCheck (Stage 2). |
| O5 | k6 external; no NBomber. |
| O6 | JOB out of scope. |
| O7 | Staggered crons (section 2). |
| O8 | SQLite reference for sqllogictest. Default taken: the TPC-shaped reference (DuckDB or PostgreSQL) is decided in WP1b. |
| O9 | Schemathesis image pinned to an exact version plus digest; the update is a manual monthly bump. |
| PRD REQ-SIM-20 | The workflow name `sim-api-fuzz-nightly.yml` is replaced by one `simulation-nightly.yml` with one job per package (lead brief). REQ-SIM-20 is otherwise unchanged. |
| P1 sample size | Owner acceptance is ">= 5000 new statements in the nightly", so the nightly default sample is 5000 (was 3000); the weekly run uses `all`. |
| P2 GraphQL tool | Schemathesis GraphQL mode (supported in 4.x; GraphQL fixes in 4.21 and 4.26). EvoMaster stays a deferred option (default taken), revisited only if Schemathesis GraphQL coverage is shown to be shallow in WP-INT. |
| P7 soak length | Default taken: 120 min wall clock weekly (existing `-weekly` guard limit) with an accelerated clock (section 4, P7). A longer run needs a guard change and a Phase 3 delta. |

## 0a. Binding programme constraints

Each constraint is enforced by a named mechanism; none is advisory.

1. **Targets.** Fuzzers, generators and load only hit the loopback sim host (`Autheris.Sim.ApiHost`, in-process gate/parity hosts, or loopback containers) with per-run throwaway data (SEC-SIM-01/02/04/23/24).
2. **Reports.** No secrets, tokens, auth headers or canary values in reports, drafts or artifacts (`IssueDraft`, `sim-report.py`, `sim-api-report.py`; SEC-SIM-14/15).
3. **Schedule.** Only `simulation-nightly.yml` (`schedule` and `workflow_dispatch`); never `ci.yml` or PR triggers (decision 1, SEC-SIM-05/21). Nightly jobs <= 60 min; container, soak and dialect jobs are weekly (<= 120 min).
4. **Determinism.** Every run takes `AUTHERIS_SIM_SEED` (date default), prints it first, and every finding carries a repro line (seed + minimal input + command) (decision 3).
5. **Finding management.** Findings are grouped and deduplicated by error class (decision 4); each class is a strict-xfail KnownGap with an issue draft.
6. **Strict policy.** 5xx, crash, hang, policy/masking leak and stack trace are never KnownGaps; they keep the job red until fixed or owner-quarantined with an issue number (SEC-SIM-17/18).
7. **No product fixes in Stage 1.** Real product defects become GitHub issues from the drafts (lead files them in WP-INT); fixes run in separate lanes. Stages 2-4 follow the same rule except for the P7 time seam (section 4).
8. **Success metric.** Per stage: number of confirmed product bugs (issues filed); a clean run with its seed and statement/request count is recorded as coverage evidence in this plan's stage log (section 4c).

## 0b. Reused base (verified 2026-10-24)

- Parity: `tests/Autheris.Tests.Parity` with `Seed/ParitySeed.cs`, `Infrastructure/ParityRunGuard.cs`, strict-xfail KnownGaps, `Pairwise/PairwiseTests.cs`, `Differential/Tlp.cs` (TLP already exists for filter shapes) and `Matrix/ParityOracle.cs`.
- Gate: `tests/Autheris.Tests.SqlCompilerGate` with `Classification/PolicyOracle.cs` and `Databases/Databases.cs` (PostgreSQL `postgres:16-alpine`, SQL Server `mcr.microsoft.com/mssql/server:2022-latest` (unpinned), Oracle `gvenzl/oracle-free:23-slim-faststart@sha256:d86d0979...`, DuckDB). The owner's "~3310 statements" figure was not re-counted; WP-INT records the real count in the first report.
- Workflows: `parity-nightly.yml` (cron 02:00, `seed` input validated `^[0-9]{1,10}$`, baseline report) and `sql-compiler-gate-nightly.yml` (cron 02:00).
- Product time: some classes already take an optional `TimeProvider` (for example `BasicAuthAttemptGuard`, `WebSocketAuthInterceptor`, CDC pollers), but about 170 `src` files still read `DateTime(Offset).UtcNow` directly. This drives the P7 seam work (section 4).

## 1. Architecture decisions (Stage 1)

1. **New project `tests/Autheris.Tests.Simulation`, not in `Autheris.sln`** (same pattern as Parity, D-1).
   - Justification: putting the corpus tests into `Autheris.Tests.SqlCompilerGate` would place them in normal CI. `ci.yml:197` runs that project without a filter, so the tests would be skipped noise there and would bloat its output with the corpus.
   - Normal CI stays unchanged, and REQ-SIM-02 reduces to "not in any solution, only `simulation-nightly.yml` targets it".
   - The project reuses the gate harness through `ProjectReference` to `tests/Autheris.Tests.SqlCompilerGate`. Fallback if the reference to a test project breaks the build: `<Compile Include=... Link=...>` of `Harness/*.cs`, `Databases/*.cs` and `Model/*.cs`. WP0 decides with one build.
   - The project also references `src/TrinoSqlEngine`, `tests/Autheris.Tests.Fuzz` (replay tests) and `tests/Autheris.Tests.Common`.
   - Packages: Microsoft.NET.Test.Sdk 18.11.0, xunit 2.9.3, xunit.runner.visualstudio 4.0.1, Shouldly 4.3.0, Microsoft.Data.Sqlite 10.0.13 (versions copied from the Parity and Gate projects); lock file enabled.
2. **Run guard (REQ-SIM-01).** `Infrastructure/SimRunGuard.cs`:
   - `EnvVar = "AUTHERIS_SIM_RUN"`; the suite runs only when the value is exactly `1`.
   - `SimulationTestBase` throws in its constructor otherwise (mirrors `ParityRunGuard` and `ParityTestBase`).
   - Pure unit tests (parsers, sampler, classifier, replay of the committed corpus) live in the namespace `Autheris.Tests.Simulation.Meta` and do not derive from the base. They run with a plain `dotnet test tests/Autheris.Tests.Simulation`.
   - The guard test enforces that every test class outside `Meta` derives from `SimulationTestBase`.
3. **Seed (REQ-SIM-03).** `Infrastructure/SimSeed.cs`:
   - Takes `AUTHERIS_SIM_SEED` (regex `^[0-9]{1,10}$`, otherwise it fails closed).
   - Default is the UTC date `yyyyMMdd` as an int. `SimSeed.Describe()` is printed first in every report.
   - `SimSeed.Hash(seed, id)` = first 8 bytes of SHA-256(`seed:id`) as a ulong. It is the only randomness source.
4. **Findings and KnownGaps (REQ-SIM-04/05).** Files under `Infrastructure/`:
   - `FindingKind`: `WrongResult`, `UnexpectedRejection`, `SchemaNonConformance`, `Crash`, `Hang`, `ServerError`, `PolicyLeak`.
   - `SimFinding(Kind, ErrorClass, MinimalInput, Expected, Actual, Repro)`.
   - The dedupe key `ErrorClass` = kind + status or exception type + message normalised (digits to `#`, quoted text to `'?'`, trimmed to 120 chars).
   - `SimKnownGaps.Register(id, errorClass)` throws `InvalidOperationException` for `Crash`, `Hang`, `ServerError` and `PolicyLeak`. Only an owner-approved quarantine is possible, via `findings/quarantine.json` (section 1.P3).
   - Strict xfail: a registered gap that no longer reproduces fails with "remove KnownGap <id>".
   - `IssueDraft.Write(dir, finding)` writes `<id>.md` with title, error class, minimal input, expected and actual (each cell capped at 200 chars, 20 rows), and a repro command. It never writes headers, tokens or canary values; canaries are replaced by `<canary>`.
   - Committed drafts go in `tests/Autheris.Tests.Simulation/KnownGaps/drafts/`.
5. **Reports (REQ-SIM-08).** `Infrastructure/SimResults.cs` appends JSONL to `$AUTHERIS_SIM_REPORT_DIR/<suite>-results.jsonl`. One line per error class carries count, kind, first id and repro.
6. **Architecture guard (REQ-SIM-02).** New `tests/Autheris.Tests.Architecture/SimulationIsolationArchitectureTests.cs` (in CI, `Category=Security`), modelled on `ParityIsolationArchitectureTests`, with self-checks on synthetic input where the Parity guard has them. Tests:
   - `SimIsolation_ProjectsAreNotInAnySolution`: Simulation, Fuzz and Sim.ApiHost.
   - `SimIsolation_NoProductionOrCiProjectReferencesSimProjects`
   - `SimIsolation_OnlySimulationWorkflowTargetsSimProjects`
   - `SimWorkflow_HasOnlyScheduleAndDispatchTriggers`
   - `SimWorkflow_PermissionsAreLeastPrivilege`: top-level `permissions: {}`, jobs `contents: read`.
   - `SimWorkflow_ActionsPinnedBySha`
   - `SimWorkflow_NoSecretsReferenced`
   - `SimWorkflow_InputsOnlyViaEnvAndValidated`
   - `SimWorkflow_SetsSimRunEnv`
   - `SimWorkflow_NightlyJobsTimeoutAtMost60`: jobs whose id ends in `-weekly` are allowed up to 120.
   - `SimWorkflow_SchemathesisImagePinnedByVersionAndDigest`: regex `schemathesis/schemathesis:\d+\.\d+\.\d+@sha256:[0-9a-f]{64}`.
   - `SimWorkflow_SharpFuzzInstrumentsOnlyTrinoSqlEngine`: the only `sharpfuzz` argument ends in `/TrinoSqlEngine.dll` under `$RUNNER_TEMP`.
   - `SimProjects_DoNotSetAutherisIncludeTestAuth`
   - `SimApiHost_IsNotPublishedByReleaseOrDockerWorkflows`
   - `SimCorpus_ManifestPinsCommitAndSha256`: 40-hex commit, 64-hex sha256 per file, sum of `queries` >= 5000.
   - `SimKnownGaps_EveryIdHasDraft`
   - `SimNotices_ListEveryThirdPartySource`: sqllogictest, Schemathesis, SharpFuzz, AFL++.
   - `SimTests_NonMetaClassesDeriveFromBase`
   - `SimFuzz_QuarantineEntriesHaveIssueNumber`

### P1: third-party SQL corpus (WP1)

7. **Source and vendoring.**
   - Manifest: `tests/Autheris.Tests.Simulation/Corpus/sqllogictest.manifest.json`.
   - Source repository: `github.com/gregrahn/sqllogictest` (mirror of the sqlite.org fossil repository), commit `c93ac416f05635ff5cc490009add936d9753d01d`. The original tree is not fetched because sqlite.org blocks non-browser clients.
   - Licence: `MIT (elected from GPL/BSD/MIT/CC0 author grant)`.

   | File | Queries | sha256 |
   |---|---|---|
   | `test/select1.test` | 1000 | `e93b83d64d06f78aee0e690455b6c604e86ad9a339f77d927a782cefb6b0e1d5` |
   | `test/select2.test` | 1000 | `a8ecc3d206c4d4b2cd6a154c18999e558ec97168cd7e327a4369e23aaf31be64` |
   | `test/select3.test` | 3320 | `d5c321683bfe903c9be95ebe80a8d23da4d8f4a39193b2087dbc34cb4e137623` |
   | `test/select4.test` | 2832 | `155ff6bb9bbf7c2dcf1e5659bb1688dec5dab58126f8dc66d23dcae6df43f59e` |
   | `test/select5.test` | 732 | `049a5d0bf90999c56db2d5880ef28febdc88906526f87069cc96af9f84c99869` |

   - Total: **8884 queries** (sort modes: 6152 rowsort, 1732 valuesort, 1000 nosort; no skipif or onlyif directives). The `index/` and `evidence/` groups are a later extension.
   - `scripts/sim/fetch-sqllogictest.sh <manifest> <outdir>` uses `curl --proto =https --fail` and `sha256sum -c`. Any mismatch exits non-zero with no partial directory left behind.
   - The fixture reads `AUTHERIS_SIM_CORPUS_DIR`. If the directory is missing or a hash mismatches, the test fails with the script command; there is no network access from C#.
8. **Parser.** `Corpus/SltParser.cs`: `Parse(string text, string file) -> IReadOnlyList<SltRecord>`.
   - Record types: `SltStatement(Ok|Error, Sql, Line)`, `SltQuery(Types, SortMode, Label, Sql, Expected: Values|Hash(count, md5), Line)`, `HashThreshold`, `Halt`, plus skipif/onlyif conditions.
   - An unknown directive throws (fail-closed).
   - `Id` = sha256(`file:line:sql`)[..12]; it uses the gate `CorpusStatement` id format.
9. **Expected results.**
   - The reference DB is a temp SQLite file per test file. All `statement` records run in file order through raw `Microsoft.Data.Sqlite`.
   - `SltReference.RunAsync` returns rows. `SltResultFormatter` applies the SLT type format: `I` = integer, `R` = `%.3f`, `T` = text, `NULL`, `(empty)`. It then applies the sort mode and the md5 of `value\n` per the SLT spec.
   - Reference sanity: the reference result must equal the recorded value or hash. A disagreement is `HarnessMismatch` (not a product finding). The suite fails when more than 1% of sampled queries mismatch.
   - Autheris is never the expected source.
10. **Engine path.**
   - `SltEngineRunner` compiles each query with the AST compiler and executes the emitted SQL on the same SQLite file. It reuses `GateRunner.RunAsync(EngineKind.Ast, CorpusStatement)` with an `SltGateDatabase : GateDatabase` (Sqlite dialect, temp file) and a catalog built from `PRAGMA table_info`.
   - If `GateDatabase.InitializeAsync` hard-loads the gate fixture, WP1 calls the same compiler and binder calls as `GateRunner.RunAstAsync` directly.
   - Scenarios:
     - **A**, permissive: no row filter, no mask. Applies to all files.
     - **B**, row filter `a > 120` on `t1`. Applies to select1-3, which use `t1` only. The reference for B is a DB copy with `DELETE FROM t1 WHERE NOT (a > 120)`.
11. **Classification.** `SltClassifier` returns one of:
   - `Equal`
   - `AcceptedDifference`: `Corpus/slt-accepted-differences.json`, keyed by error class with a reason, for example SQLite integer division and type affinity.
   - `UnsupportedByDesign`: `Corpus/slt-unsupported.json`; a governed or parse rejection of a documented unsupported construct, with a doc reference.
   - `Finding` (`WrongResult` or `UnexpectedRejection`, eligible for KnownGap).
   - `PolicyLeak`: scenario B returns a row outside the filter, or any masked value. Never a KnownGap.
   - `Crash`: a non-documented exception. Never a KnownGap.
   - `HarnessMismatch`
12. **Sampling (REQ-SIM-12).** `SltSampler.Select(queries, seed, size)`:
   - Takes the top-`size` queries ordered by `SimSeed.Hash(seed, id)`, then sorts them by (file, line) for execution.
   - `AUTHERIS_SIM_SAMPLE` (`^[0-9]{1,5}$|^all$`) defaults to 5000 nightly (owner acceptance), the weekly run uses `all`.
   - Setup statements always run in full and in order.
13. **Tests.** Guarded tests in `Corpus/SltCorpusTests.cs`:
   - `Corpus_SampledQueries_MatchReferenceOrKnownGap`
   - `Corpus_RowFilterScenario_NeverLeaksFilteredRows`
   - `Corpus_ReferenceAgreesWithRecordedResults`
   - `Corpus_ManifestProvidesAtLeast5000Queries`

   Meta tests (fixtures are our own `Corpus/fixtures/mini.test`):
   - `SltParser_ParsesStatementAndQueryRecords`, `SltParser_RejectsUnknownDirective`, `SltParser_IdsStableAcrossRuns`
   - `SltFormatter_HashMatchesSltSpecSample`, `SltFormatter_AppliesRowsortAndValuesort`
   - `SltSampler_SameSeedSameSample`, `SltSampler_DifferentSeedDifferentSample`, `SltSampler_RespectsSize`
   - `SltClassifier_PolicyLeakNeverKnownGap`, `SltClassifier_CrashNeverKnownGap`
   - `SimKnownGaps_RejectSecurityKinds`, `IssueDraft_RedactsCanariesAndCapsCells`, `SimSeed_RejectsMalformedValue`
14. **Report.** `.github/scripts/sim-report.py <results.jsonl> <baseline.json> <out.md>`:
   - Uses the `parity-report.py` style: stdlib only, run with `python3 -I`.
   - Rejects malformed lines and files over 10 MB, and escapes markdown cells.
   - Baseline: `tests/Autheris.Tests.Simulation/sim-baseline.json`.
15. **Product code:** none is changed in this lane. Findings become KnownGaps with drafts, or bugs that keep the job red.

### P2: Schemathesis (WP2)

16. **Standalone host.** New console project `tests/Autheris.Sim.ApiHost` (Exe, not in the solution), referencing `src/Autheris.Api`.
   - Entry class `SimApiHostMain` (not `Program`, to avoid a clash with `Autheris.Api`'s `Program`).
   - Uses `new WebApplicationFactory<Program>().WithWebHostBuilder(...)` with the settings copied from `ParityHostSettings`: `Gateway:Authentication:EnableTestAuthHandler=true` and SQLite connections. It then calls `.UseKestrel(port)` + `StartServer()` (ASP.NET Core 10) bound to `127.0.0.1` only.
   - Arguments: `--port 5071 --data-dir <tmp> --ready-file <path> --profile read|write`.
   - `SimApiSeed` creates the SQLite fixture with planted canaries: masked column value `SIMCANARY-MASK-7f3a91` and forbidden-tenant row marker `SIMCANARY-TENANT-9b1c42`. Canaries are written to `<data-dir>/canaries.txt`.
   - It registers the catalog and policies (mask on the canary column, tenant scoping), copying the approach of `ParityHostFixture.RegisterCatalogAsync`; it does not reference the Parity project.
   - It writes the ready file only after a self-probe (`GET /odata/v4/$metadata` returns 200) and exits on SIGTERM.
   - Built with `-p:AutherisIncludeTestAuth=true`. The csproj never sets the property; a guard test checks this.
   - Throwaway credentials: the test-auth headers `X-Test-Roles: Reader` and `X-Test-Tenant: tenant_sim` (admin read phase: `Admin`). They are valid only on this host build. No bearer secret exists.
17. **Schemas.**
   - REST/OData: `GET /odata/v4/$openapi`, plus the WebSQL OpenAPI spec (`WebSqlEndpoints.Governance.BuildOpenApiSpec`; WP2 resolves its route).
   - GraphQL: `/graphql`, from `Gateway:GraphQL:EndpointPath`, introspected by Schemathesis.
   - The schemas are fetched with curl plus the test headers into `$RUNNER_TEMP/st/` and then passed as files to `st run --url http://127.0.0.1:5071`.
18. **Run.** `scripts/sim/run-api-fuzz.sh`:
   - Runs `docker run --rm --network host -v $RUNNER_TEMP/st:/work $SCHEMATHESIS_IMAGE run ...`.
   - Options: `--seed $SEED --workers 1 --max-examples 50 --request-timeout 10 --checks not_a_server_error,response_schema_conformance,status_code_conformance --report junit,har --report-dir /work/out`.
   - Read phase: GET plus the read-only POST query endpoints (WebSQL query, GraphQL), with `--exclude-method PUT,PATCH,DELETE` and the admin and governance write paths excluded. Hard cap of 25 minutes per phase via `timeout`.
   - Write phase (O3): restart with a fresh `--data-dir` and `--profile write`, run all methods on the governance and catalog paths, 15-minute cap.
   - Nothing from Schemathesis is committed.
19. **Checker.** `.github/scripts/sim-api-report.py --har <files> --junit <files> --canaries <file> --gaps tests/Autheris.Tests.Simulation/ApiFuzz/api-known-gaps.json --out <dir>`:
   - Stdlib only, `python3 -I`, rejects input over 50 MB.
   - Fails on: status >= 500; any canary in a response body or header; stack-trace patterns (`--- End of stack trace`, `   at [A-Z][\w.]+\(`, `System\.\w+Exception`).
   - Groups 4xx by (status, JSON `code`/`error.code`) and lists them without failing.
   - Schema non-conformance groups may be KnownGaps. The script rejects a gaps file that lists a 5xx or leak class.
   - Each group gets a repro line: `st run <schema> --seed S --include-path P --include-method M`.
   - `--self-test` runs against `ApiFuzz/fixtures/{clean,planted-5xx,planted-canary,planted-stacktrace,malformed}.har`. The workflow runs the self-test before fuzzing (fail-closed).
   - Meta tests (they call `python3 -I`): `ApiReport_SelfTestDetectsPlantedFaults`, `ApiReport_RejectsMalformedAndOversizedInput`, `ApiReport_RejectsSecurityClassInGapsFile`, `ApiReport_DedupesByStatusAndCode`.
   - Guarded test: `ApiHost_StartsAndServesSchemasWithTestAuth` (starts the host in-process).

### P3: SharpFuzz on TrinoSqlEngine (WP3)

20. **Harness.** `tests/Autheris.Tests.Fuzz` (console Exe, not in the solution), referencing `src/TrinoSqlEngine`, package `SharpFuzz` (exact version, pinned by WP0).
   - `Program.cs` modes: `--afl` (`Fuzzer.OutOfProcess.Run(FuzzTarget.Run)`), `--replay <file|dir>...`, `--export-seeds <dir>`, `--export-dict <file>`. Without arguments it prints usage, including the local run commands.
   - `FuzzTarget.Run(Stream)`:
     - Decodes the input as UTF-8 and returns on invalid input.
     - Calls `FastSqlEngine.Parse(ReadOnlyMemory<char>)` (`src/TrinoSqlEngine/FastSqlEngine.cs:186`).
     - Then compiles with `FuzzCompileContext`: an in-memory catalog with one masked and one row-filtered table and no I/O.
     - Catches only accepted rejections.
   - `AcceptedRejections`:
     - `Antlr4.Runtime.Misc.ParseCanceledException` (documented syntax/policy/budget type), except when its inner exception is a `TimeoutException`; that counts as `Hang`.
     - `ArgumentOutOfRangeException` from the `MaxQueryLength` check.
     - `GovernedSqlException` and its subclasses, `SecurityException` subclasses declared in TrinoSqlEngine, `AstBuildException`, `SetOperationDegreeMismatchException`, `LiteralTokenRejectedException`, `SqlTimeZoneException`.
     - Anything else (NullReference, IndexOutOfRange, InvalidCast, InsufficientExecutionStack, OOM) is a `Crash`.
   - `FuzzOutcomeClassifier.Classify(Exception?, TimeSpan)` returns `Accepted`, `Crash` or `Hang` (`elapsed > 2 s`).
21. **Replay mode (libFuzzer-less, local).** `ReplayRunner.Run(paths)`:
   - Runs each input in-process on a dedicated thread (16 MB stack, as `FastSqlEngine.DefaultParseThreadStackSize`) with `Join(2 s)`.
   - Input order is sorted by file name.
   - Returns a `ReplayReport` and writes an `IssueDraft` per finding. Exit code 1 on any crash or hang.
   - Meta tests:
     - `Replay_SeedCorpus_AllAccepted`, `Replay_CommittedFindings_AllAccepted`
     - `Classifier_ParseCanceled_IsAccepted`, `Classifier_ParseCanceledWithTimeoutInner_IsHang`, `Classifier_NullReference_IsCrash`, `Classifier_OverTimeLimit_IsHang`
     - `AcceptedRejections_ContainOnlyTrinoSqlEngineOrDocumentedTypes`
     - `ExportSeeds_IsDeterministic`
22. **Corpus folders.**
   - `tests/Autheris.Tests.Fuzz/corpus/`: about 300 seeds, each 4 KB or less, exported from our own `tests/Autheris.Tests.SqlCompilerGate/Corpus/dql.sql` (1193 statements, project-licensed) plus hand-written grammar seeds. No sqllogictest text.
   - `corpus/sql.dict`: AFL dictionary from `SqlKeywords`.
   - `findings/crashes/`, `findings/hangs/` (`.gitkeep`) and `findings/quarantine.json` (`[{sha256, issue, reason, approved}]`, owner-only; entries expire after 90 days, SEC-SIM-17).
   - A finding input is committed only together with its product fix (another lane). Until then the nightly stays red, unless the owner quarantines it explicitly.
23. **Smoke run.** `scripts/sim/run-parser-fuzz.sh <seconds>`:
   - `dotnet publish tests/Autheris.Tests.Fuzz -c Release -o $RUNNER_TEMP/fuzz-bin`
   - `sharpfuzz $RUNNER_TEMP/fuzz-bin/TrinoSqlEngine.dll`: only this copy is instrumented, never a production build. The tool is installed with `dotnet tool install SharpFuzz.CommandLine --version <pin> --tool-path $RUNNER_TEMP/sf`.
   - AFL++ comes from `sudo apt-get install -y --no-install-recommends afl++` on `ubuntu-24.04`, with `AFL_SKIP_CPUFREQ=1 AFL_NO_UI=1 AFL_I_DONT_CARE_ABOUT_MISSING_CRASHES=1`.
   - `afl-fuzz -i corpus -x corpus/sql.dict -o $RUNNER_TEMP/afl -t 5000 -m 2048 -s $SEED -V <seconds> -- dotnet $RUNNER_TEMP/fuzz-bin/Autheris.Tests.Fuzz.dll --afl`
   - Then `--replay` over `afl/default/{crashes,hangs}`. The job fails if either is non-empty, apart from quarantined sha256 values.
   - Local run: `dotnet run --project tests/Autheris.Tests.Fuzz -- --replay tests/Autheris.Tests.Fuzz/corpus tests/Autheris.Tests.Fuzz/findings` (no AFL needed).

## 2. Workflow `.github/workflows/simulation-nightly.yml` (owned by WP0)

The workflow follows `parity-nightly.yml`:
- Triggers: `schedule` and `workflow_dispatch` only. Top-level `permissions: {}`; each job `permissions: contents: read`. No secrets, no retries. Fork guard `github.event.repository.fork == false` on every job; checkout with `persist-credentials: false` (SEC-SIM-05/22).
- Actions pinned by SHA, copied: checkout `3d3c42e5aac5ba805825da76410c181273ba90b1`, setup-dotnet `a98b56852c35b8e3190ac28c8c2271da59106c68`, upload-artifact `cf430e030ddbb5b0abf93d22962f4752f3646cd9`.
- Dispatch inputs `job` (choice: all|corpus|api|parser), `seed` and `sample` reach the shell only through `env:` and are regex-validated before use.
- Each job sets `AUTHERIS_SIM_RUN: '1'` and `AUTHERIS_SIM_REPORT_DIR`. Each selects itself with `if: github.event_name == 'workflow_dispatch' || github.event.schedule == '<its cron>'`.
- Jobs call only the scripts named in this plan, so the YAML stays stable while WP1-3 work.

| Job | Cron (UTC) | Timeout | Budget |
|---|---|---|---|
| `sim-corpus` | `0 3 * * *` | 45 | build 5, fetch 1, 5000 sampled queries over A+B about 17 |
| `sim-api-fuzz` | `30 3 * * *` | 60 | build 5, read phase 25, write phase 15 |
| `sim-parser-fuzz` | `30 4 * * *` | 30 | install and build 8, AFL `-V 300`, replay 2 |
| `sim-corpus-weekly` | `0 5 * * 0` | 90 | `sample=all` |
| `sim-parser-fuzz-weekly` | `0 6 * * 0` | 90 | AFL `-V 3600` |

Artifacts: reports, scrubbed HAR, issue drafts and the AFL crash/hang inputs, `retention-days: 7`; never the host data dir (SEC-SIM-14/15). The four non-blocking changes of section 6 ("Plan changes required" a-d) are applied in sections 1 and 2.

## 3. Work packages, file sets and effort (Stage 1)

Order: **WP0 -> (WP1 || WP2 || WP3) -> WP-INT**.

| WP | Files (exclusive) | Days |
|---|---|---|
| WP0 scaffold | `tests/Autheris.Tests.Simulation/{Autheris.Tests.Simulation.csproj, packages.lock.json, xunit.runner.json, Infrastructure/*, Meta/SimInfrastructureTests.cs, KnownGaps/drafts/.gitkeep}`; `tests/Autheris.Tests.Fuzz/{Autheris.Tests.Fuzz.csproj, packages.lock.json, Program.cs (usage stub)}`; `.github/workflows/simulation-nightly.yml`; `tests/Autheris.Tests.Architecture/SimulationIsolationArchitectureTests.cs`; `THIRD-PARTY-NOTICES.md` (new, root) | 1.5 |
| WP1 corpus | `tests/Autheris.Tests.Simulation/Corpus/**`, `Meta/Slt*Tests.cs`, `sim-baseline.json`, `scripts/sim/fetch-sqllogictest.sh`, `.github/scripts/sim-report.py` | 4 |
| WP2 API fuzz | `tests/Autheris.Sim.ApiHost/**`, `tests/Autheris.Tests.Simulation/ApiFuzz/**`, `Meta/ApiReport*Tests.cs`, `scripts/sim/run-api-fuzz.sh`, `.github/scripts/sim-api-report.py` | 3 |
| WP3 parser fuzz | `tests/Autheris.Tests.Fuzz/**` except the csproj and lock file (Program.cs is taken over from the WP0 stub), `tests/Autheris.Tests.Simulation/Fuzz/**`, `Meta/Fuzz*Tests.cs`, `scripts/sim/run-parser-fuzz.sh` | 3 |
| WP-INT (lead) | First dispatch run, triage into KnownGaps and drafts, `docs/plans/00-master-plan-overview.md` | 0.5 |
| WP1b (deferred, optional) | Job-time TPC behind `AUTHERIS_SIM_TPC=1` (pinned DuckDB CLI plus sha256, data and queries in `$RUNNER_TEMP` only) | 2 |

Shared files and their owners:
- WP0 owns the workflow, the architecture guard file, `THIRD-PARTY-NOTICES.md` and both csproj/lock pairs. Its notices entries: sqllogictest (fetched, MIT elected), Schemathesis (MIT, container), SharpFuzz (MIT), AFL++ (apt; licence of the installed version: Apache-2.0 for the `ubuntu-24.04` package, upstream AGPL-3.0-or-later since 2026-06, section 4b).
- WP0 sets every package and project reference up front, including the pinned SharpFuzz version and the Schemathesis image version plus digest. WP1-3 add no packages to the shared projects. If a WP needs a change, the lead makes it.
- `Autheris.sln`, `ci.yml` and `Directory.Build.props` are **not touched**.
- Stage 1 total: 12 PM-run days (14 with WP1b). Elapsed: WP0 1.5, then three parallel lanes of at most 4, then WP-INT 0.5, so about 6 working days (the owner's "about 1 week").

### 3a. Stage 1 package cards

| P | Days | Acceptance | Schedule | Risks |
|---|---|---|---|---|
| P1 corpus | 4 (WP1) + 2 optional (WP1b) | Manifest pins >= 5000 sqllogictest statements (commit + per-file sha256). The nightly executes a seeded sample of 5000 through the gate compile path against the SQLite reference. Every mismatch is a classified finding: KnownGap + draft, or red for security/crash classes. No third-party file is committed. | nightly `sim-corpus` (45 min); weekly `all` (90 min) | Licence (handled: fetched, not committed; TPC not committed). SQLite semantics noise (rules per error class). Runtime (seeded sample). |
| P2 API fuzz | 3 (WP2) | Zero 5xx, zero canary or stack trace in any response, in both the read and the write phase. Report deduplicated by (status, error code), each group with a repro line. Fixed seed. Checker self-test green before the run. | nightly `sim-api-fuzz` (60 min) | Schemathesis v4 flag drift (pinned image). The OData `$openapi` document may be incomplete (finding `SchemaNonConformance`, not a test tweak). Write-phase flakiness (fresh host). |
| P3 parser fuzz | 3 (WP3) | No crash and no hang (2 s per input in the harness, AFL `-t 5000`) in the 300 s nightly smoke and the 3600 s weekly run. The committed seed and finding replay is deterministic and green. | nightly `sim-parser-fuzz` (30 min); weekly (90 min) | AFL non-determinism (replay is the acceptance). Runner CPU. AFL++ licence (section 4b). |

## 4. Stages 2-4 (plan level; each gets an addendum and a Phase 3 delta before Phase 4)

Common shape: new code lives in `tests/Autheris.Tests.Simulation/{Generator,Oracles,Soak,Faults,Dialects,BiReplay}/`, plus new jobs in `simulation-nightly.yml`. It reuses `SimSeed`, `SimFinding`, `SimKnownGaps`, `IssueDraft`, `SimResults`, the guard and `Autheris.Sim.ApiHost`. Every addendum re-states SEC-SIM-24. Order: P4 -> P5/P6 -> P7 -> P8 -> P9 -> P10. Each stage starts only after the previous stage's findings are triaged.

**Stage 2: own generator with policy oracle (14 PM-run days, about 2-3 weeks elapsed).**

- **P4 generator (6 days, including a 1-day CsCheck spike).**
  - Architecture: `Generator/LogicalQuery.cs` is an immutable record tree:
    - source table and projections;
    - filters (comparison, `IS [NOT] NULL`, `IN`, `BETWEEN`, `LIKE`, AND/OR/NOT);
    - inner/left joins on declared keys;
    - `GROUP BY` with COUNT/SUM/MIN/MAX/AVG;
    - `TimeBucket(column, grain)`;
    - `IN`/`EXISTS`/scalar subqueries;
    - ORDER BY + LIMIT.
  - `Generator/SchemaModel.cs` reads the sim host catalog, the seed data and the policy description (masked columns, row filters, tenants, consent).
  - CsCheck `Gen<LogicalQuery>` is seeded from `SimSeed.Hash(seed, "gen")`.
  - Renderers `WebSqlRenderer`, `GraphQlRenderer` and `RestODataRenderer` each declare a capability set. A query is rendered only to the channels that support all of its features.
  - Acceptance: >= 20,000 queries per night; same seed, byte-identical query list (`Generator_SameSeed_SameQueries`); an injected bug shrinks to under 10 tokens (`Generator_InjectedBug_ShrinksBelow10Tokens`); round-trip tests per renderer feature.
  - Schedule: nightly `sim-generator`, 45 min.
  - Risks:
    - Renderer drift between channels: mitigated by capability sets and one contract test per feature.
    - Budget wasted on rejected queries: the acceptance ratio is reported, and the job fails below 50 %.
    - CsCheck fit: decided by the spike, with FsCheck as fallback.
- **P5 oracles (5 days).** `Oracles/IQueryOracle` implementations:
  - `ChannelEqualityOracle`: the same row multiset over all rendered channels, after type and order normalisation.
  - `TlpOracle`: the logic of Parity `Tlp.cs` lifted to generated queries. Q equals Q WHERE p, plus Q WHERE NOT p, plus Q WHERE p IS NULL, for rows and for COUNT/SUM.
  - `NoRecOracle`: the optimised filter count against the unoptimised `SUM(CASE WHEN p THEN 1 ELSE 0 END)`.
  - `SecurityOracle`: no canary, masked value or out-of-policy row in the result, error text, headers or `/metrics` delta. "Unknown" and "forbidden" objects get identical status, code and message (the #320 case).
  - Acceptance: each oracle has a mutant self-test with a deliberately injected bug in `Oracles/Mutants/`. If the oracle does not detect it, the job is red. Tests: `TlpOracle_DetectsDroppedNullPartition`, `NoRecOracle_DetectsWrongCount`, `ChannelEqualityOracle_DetectsRendererDrift`, `SecurityOracle_DetectsCanaryInErrorText`, `SecurityOracle_DetectsUnknownVsForbiddenDifference`. Security oracle hits are `PolicyLeak`, never KnownGaps.
  - Schedule: runs inside `sim-generator`.
  - Risks:
    - False positives on NULL ordering, collation and float rounding: a type-wise normaliser with documented tolerances.
    - Metrics scrape cost: scrape every N queries.
- **P6 policy variants (3 days).**
  - Architecture: `Generator/CallerMatrix.cs` crosses roles × tenants × consent on/off × masking profile, with a declared "restricts" order.
  - `PolicyMonotonicityOracle` checks that the stricter caller's result is a subset of the broader caller's result, modulo masking. A masked cell is the mask token or null, never the original value.
  - Acceptance: the property holds for every query and caller pair. Mutant self-test `PolicyMonotonicity_DetectsWidenedRowFilter`. A pairwise cover (Parity pairwise approach) keeps the matrix at <= 8 callers per night, rotated by seed.
  - Schedule: runs inside `sim-generator`.
  - Risks: policy explosion (pairwise cover). Intended non-subset semantics (for example consent) are allowed only as documented exceptions in the addendum, never as KnownGaps.

**Stage 3: operations simulation, "the mileage" (12 PM-run days including the seam, about 2 weeks elapsed).**

- **P7 soak with accelerated time (6 days + 2 days product time seam).**
  - Prerequisite (separate lane, own PRD addendum and Phase 3): token validation, caches, consent and schema reload read time only from the DI `TimeProvider` (default `TimeProvider.System`). There is no test-only code in the product (section 0b).
  - The sim host registers `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`, MIT). A seeded event schedule advances it (default 1 simulated hour per wall-clock minute, so >= 5 simulated days in 120 min) and fires token expiry, cache expiry, consent switches and schema reloads.
  - Load: k6 (image pinned by digest, loopback) replays mixed-caller requests exported from P4 as JSONL.
  - Watchdogs:
    - Working set, handles and threads are sampled per minute through EventPipe counters.
    - Deadlock watchdog: no completed request for 60 s under load means a dump plus a `Hang` finding.
    - P5 oracles are sampled during the run, always right after each cache or schema event.
  - Acceptance: working-set slope after warm-up below 10 % per simulated day (linear fit after forced GC samples); no hang or deadlock; zero wrong answers and leaks after every expiry or reload event; same seed, same event schedule.
  - Schedule: weekly `sim-soak-weekly`, 120 min.
  - Risks: the time seam is a product change (Phase 3 delta); GC noise (trend over many samples); runner variance.
- **P8 fault injection (4 days).**
  - Toxiproxy (MIT, digest-pinned, bound to `127.0.0.1`) sits between the sim host and PostgreSQL containers for the source and governance DBs. SQLite cannot be proxied, so the P8 host profile switches to PostgreSQL.
  - Seeded toxics: latency, bandwidth, `reset_peer`, timeout and `slow_close`, plus clock jumps through `FakeTimeProvider`.
  - Acceptance: under a fault, every request ends with either a correct answer or a 503/fail-closed documented error code, within the command timeout + 5 s. Never wrong data (P5), never a leak, never a hang. Full recovery within 30 s after the toxic is removed.
  - Schedule: weekly `sim-faults-weekly`, 90 min. The soak also runs once per week with injection on (`sim-soak-weekly` fault variant).
  - Risks: timing flakiness (assert outcomes, not latencies). Undefined governance-DB outage behaviour is a finding, not a test relaxation.

**Stage 4: environment breadth (ongoing, 8 PM-run days).**

- **P9 dialects and data shapes (6 days).**
  - Reuse the gate's `Databases/Databases.cs` containers (PostgreSQL, SQL Server, Oracle Free, DuckDB) as P4 sources and references.
  - Data shapes:
    - Zipf-skewed keys and 10^6-row tables;
    - special characters (quotes, combining marks, RTL, NUL);
    - extreme times (0001-01-01, 9999-12-31, DST edges).
  - Acceptance: P4 and P5 are green per dialect, and differences are classified per dialect error class. Every image is pinned by digest. The gate's SQL Server `2022-latest` tag must be digest-pinned for P9 (SEC-SIM-07/24).
  - Schedule: weekly `sim-dialects-weekly`; if 120 min is exceeded, one dialect per week by rotation.
  - Risks: container start time (Oracle Free about 2-3 min); image licence terms (section 4b); pull flakiness.
- **P10 BI query replay (optional, 2 days).**
  - Run Metabase OSS and dbt only against the sim host; Power BI patterns are captured manually against the same host. The host request log records the emitted SQL/REST.
  - Patterns are curated into `BiReplay/patterns/*.json` with a provenance header and replayed through P5. There are no customer captures.
  - Acceptance: >= 3 tools × >= 20 patterns replay green or as classified findings.
  - Schedule: weekly, inside `sim-dialects-weekly`.
  - Risks: provenance (only our own captures on synthetic data); tool version drift.

### 4a. Rejected alternatives

1. SQLsmith as code: GPL-3.0, and it targets DB crashes, not policy correctness. Its grammar-based random SQL idea is covered by P4.
2. SQLancer code (MIT, Java): ideas only (TLP, NoREC, PQS) to avoid a JVM dependency; P5 implements them in C#.
3. Jepsen-style consistency tests: Autheris is not a distributed store and makes no linearisability claim.
4. Everything at once: rejected. Stages run in order, each one triaged before the next.
5. NBomber, and TPC/IMDB content committed to the repo (section 4b).

### 4b. Tool verdicts (re-verified 2026-10-24 via GitHub API and vendor pages; complements PRD section 4)

| Tool | Licence | Last push | Runtime here | Verdict |
|---|---|---|---|---|
| sqllogictest | Runner: GPL/BSD/MIT/CC0 choice; test files under the same author terms | active | fetched at job time, parsed as text | USE P1, not committed, MIT elected |
| TPC-H / TPC-DS | TPC EULA | n/a | optional job-time generation (WP1b) | **BLOCKER for committing**; fallback in place, not a programme blocker |
| Schemathesis | MIT | 2026-10-23 (4.x supports GraphQL) | container, digest-pinned | USE P2 (REST, OData, GraphQL) |
| EvoMaster | LGPL-3.0 | 2026-10-24 | JVM, external | allowed, deferred |
| SharpFuzz | MIT | 2026-06-16 (stable) | .NET tool + package, pinned | USE P3 |
| AFL++ | Upstream AGPL-3.0-or-later since 2026-06-03 (harness-compiled parts stay Apache-2.0); the `ubuntu-24.04` package predates the relicence (Apache-2.0) | 2026-10-19 | apt, external, unmodified | USE P3. Not a blocker: external process, not distributed. The notices entry must name the licence of the installed version. |
| CsCheck / FsCheck | Apache-2.0 / BSD-3-Clause | 2026-10-21 | NuGet | USE CsCheck P4; FsCheck fallback |
| k6 | AGPL-3.0 | 2026-10-23 | container, external, unmodified | USE P7 |
| NBomber | Proprietary; free for personal use only (pricing page) | active | none | **EXCLUDED** (blocker without a paid subscription) |
| Toxiproxy | MIT | 2026-10-20 | container | USE P8 |
| TimeProvider.Testing | MIT (dotnet/extensions) | 2026-10-23 | NuGet, sim host only | USE P7 |
| gvenzl/oracle-free | Image scripts Apache-2.0. The Oracle Database Free engine is under the Oracle Free Use Terms: internal use for developing, testing, prototyping and demonstrating, unmodified redistribution allowed. | 2026-10-22 | container, already digest-pinned in the gate | USE P9; pulled, never committed |
| SQL Server image | Docker files MIT. Developer edition engine is free for non-production use; `ACCEPT_EULA=Y` is the organisation's EULA acceptance. | 2026-10-14 | container | USE P9, test-only, pin by digest |
| PostgreSQL / DuckDB | PostgreSQL licence / MIT | active | container / embedded | USE P9 |
| SQLancer | MIT | 2026-10-21 | none | ideas only |
| SQLsmith | GPL-3.0 | 2026-07-15 | none | **REJECTED** (not used) |
| Metabase / dbt-core / Power BI | AGPL-3.0 OSS edition (repo also holds enterprise code) / Apache-2.0 / proprietary | active | external, against the sim host only | P10 optional; only our own captures are committed |

### 4c. Stage log

After each stage, WP-INT (lead) appends one line here: stage, date, seed(s), statements or requests executed, confirmed bugs (issue numbers) and clean-run evidence.

## 5. Risks (Stage 1)

1. A `ProjectReference` to a test project can fail because of the generated entry point. Mitigation: `GenerateProgramFile=false` in the Simulation project, or the linked-file fallback (decision 1).
2. The gate's `SqliteGateDatabase` may hard-load the gate schema. Mitigation: the direct compiler path (decision 10).
3. SLT semantics are SQLite-specific (integer division, affinity) and may flood the accepted-difference list. Mitigation: rules are keyed by error class, so one rule covers a whole class.
4. AFL timing makes runs non-deterministic despite `-s`. Mitigation: the acceptance relies on the deterministic replay of the committed corpus.
5. Schemathesis v4 CLI flags may differ from those named here. WP2 verifies them against the pinned version, and the guard checks only the image pin.
6. `WebApplicationFactory.UseKestrel` is required. If it is unavailable, the fallback is `dotnet run src/Autheris.Api` with the same settings through environment variables.

## 6. Phase 3 security conditions (csharp-security-expert, 2026-10-24)

**Verdict: APPROVED WITH CONDITIONS.** 24 binding conditions. Code facts checked: `Directory.Build.props:18-21` defaults `AutherisIncludeTestAuth=false`; `ParityIsolationArchitectureTests.cs:451` already asserts the csproj never sets it; `parity-nightly.yml` uses `permissions: {}`, SHA-pinned actions, env-only inputs and `retention-days: 14`. Nothing in the plan weakens an existing control. Each condition below becomes a guard test or script check; "Test" names are `[Trait("Category","Security")]` (architecture ones run in CI, Meta ones in `dotnet test tests/Autheris.Tests.Simulation`).

### Conditions

| Id | WP | Binding condition |
|---|---|---|
| SEC-SIM-01 | WP2 | `SimApiHostMain` binds only to `127.0.0.1` (never `0.0.0.0`/`+`/`*`); it refuses to start when `--bind`/`ASPNETCORE_URLS`/`Urls` resolve to a non-loopback address (fail-closed). Port must be numeric 1024-65535. |
| SEC-SIM-02 | WP2 | `run-api-fuzz.sh` and the checker accept only a base URL whose host is exactly `127.0.0.1` or `localhost` (parsed, not prefix-matched; reject userinfo, `@`, redirects off-host). Anything else exits non-zero before Schemathesis starts. The fetched OpenAPI `servers` entries are stripped or overridden so the schema cannot redirect traffic; Schemathesis runs without `--base-url` pointing elsewhere and with redirects not followed off-host. |
| SEC-SIM-03 | WP2/WP0 | Test-auth is only in the `Autheris.Sim.ApiHost` build (`-p:AutherisIncludeTestAuth=true` on that project's command line); no sim csproj/props sets it, no release/Docker/publish workflow builds or publishes Sim projects, and `Autheris.Api` Release output must still pass `ReleaseBuildExcludesTestAuthTests`. The host also asserts at startup that the loopback bind holds before writing the ready file. |
| SEC-SIM-04 | WP2 | All fuzz credentials, tenants and data are synthetic and generated per run (`tenant_sim`, throwaway SQLite in `$RUNNER_TEMP`); no real tenant name, token, connection string or secret is read. The write phase uses a fresh data dir; the data dir is never uploaded. |
| SEC-SIM-05 | WP0 | Workflow: top-level `permissions: {}`, each job exactly `contents: read`; no `secrets.*`, no `GITHUB_TOKEN` passed to scripts, no `id-token`, `checkout` with `persist-credentials: false`; triggers only `schedule` and `workflow_dispatch` (no `pull_request`, `pull_request_target`, `push`, `workflow_run`, `issue_comment`). |
| SEC-SIM-06 | WP0 | All `uses:` pinned to a 40-hex SHA with version comment; no `uses:` of local or third-party actions beyond checkout, setup-dotnet, upload-artifact. |
| SEC-SIM-07 | WP0 | `docker run` images pinned `name:version@sha256:<64 hex>` (Schemathesis); the run uses `--read-only`, `--cap-drop ALL`, `--security-opt no-new-privileges`, no docker socket mount, and mounts only `$RUNNER_TEMP/st`. Any future container (P8/P9) follows the same rule. |
| SEC-SIM-08 | WP0 | Dispatch inputs (`job`, `seed`, `sample`) reach shell only via `env:`, never `${{ }}` inside `run:`; each is validated against an allowlist regex (`job` against the choice set, `seed` `^[0-9]{1,10}$`, `sample` `^[0-9]{1,5}$\|^all$`) and the step exits non-zero on mismatch. |
| SEC-SIM-09 | WP0/WP3 | Tool installs are pinned: `dotnet tool install SharpFuzz.CommandLine --version <exact>`, NuGet restore in locked mode (`packages.lock.json`, `--locked-mode`), AFL++ via apt on the runner only (distro-signed; the job logs `afl-fuzz` version). No `curl \| sh`, no `latest`, no unpinned `pip install`. |
| SEC-SIM-10 | WP1 | `fetch-sqllogictest.sh` verifies the 40-hex commit in the URL and the manifest sha256 of every file with `sha256sum -c`; any mismatch, missing file, empty file, non-https URL or host other than the manifest's pinned host fails closed and removes the output dir. Downloads cap at 20 MB per file. A manifest without hashes fails the guard. |
| SEC-SIM-11 | WP1/WP1b | Job-time downloads (sqllogictest, later DuckDB CLI/TPC tools) go only to `$RUNNER_TEMP`, never into the repo tree or any uploaded artifact; no download is executed (sqllogictest `.test` files are parsed as text only; DuckDB CLI only after sha256 verification and only in WP1b). |
| SEC-SIM-12 | WP1 | Downloaded corpus is untrusted data: `SltParser` enforces limits (file <= 20 MB, line <= 64 KB, record count bound, no `include`/shell/`halt`-driven side effects), never evals content, and treats unknown directives as errors. Reference SQL runs only against a temp SQLite file with no extension loading (`load_extension` disabled), no `ATTACH` of paths outside the temp dir, and a per-query timeout. |
| SEC-SIM-13 | WP0/WP1 | Licence hygiene: no sqllogictest file, TPC data or query text, nor any non-permissive third-party content is committed (`SimCorpus_NoThirdPartyCorpusCommitted` scans the tree for the manifest sha256 values and the SLT header banner). `THIRD-PARTY-NOTICES.md` lists sqllogictest (MIT elected, provenance, commit), Schemathesis, SharpFuzz, AFL++, with licence and how obtained. Seeds in `tests/Autheris.Tests.Fuzz/corpus` derive only from our own `dql.sql`. |
| SEC-SIM-14 | WP2 | Canaries are synthetic only (`SIMCANARY-*` on a throwaway DB); no real-tenant or seed data is ever used as canary. `IssueDraft`, `sim-report.py` and `sim-api-report.py` replace canaries with `<canary>`, drop request/response headers and auth headers (`X-Test-*`, `Authorization`, `Cookie`), and cap response excerpts at 200 chars. HAR files are uploaded only after a scrub step that removes headers and bodies of 2xx responses, or the HAR is not uploaded and only the checker's output is. |
| SEC-SIM-15 | WP0 | Artifacts: `retention-days` <= 7 on every upload; upload paths limited to the report dir, drafts, and AFL `crashes`/`hangs`; never the data dir, `$RUNNER_TEMP` root, `canaries.txt` or `~/.nuget`. `if-no-files-found: error` is not required, but the upload must not use globs outside the report dir. |
| SEC-SIM-16 | WP3 | SharpFuzz instruments only the copy `$RUNNER_TEMP/fuzz-bin/TrinoSqlEngine.dll`; the path must resolve under `$RUNNER_TEMP`; the instrumented output is never uploaded, packed, published or referenced by product/Docker/release projects. Guard test enforces exactly one `sharpfuzz` call. |
| SEC-SIM-17 | WP0/WP3 | Crash, hang, `ServerError`, `PolicyLeak` can never be KnownGaps (`SimKnownGaps.Register` throws, python gap loaders reject the classes). The only path is `quarantine.json`; each entry requires `{sha256, issue (positive integer), reason, owner-approved date}`, the guard fails entries older than 90 days, and a quarantined input still runs in replay with its outcome logged. A crash or hang input that is not quarantined keeps the job red. |
| SEC-SIM-18 | WP2 | Any 5xx, any canary in a body or header, and any stack trace is a hard failure with no KnownGap path; `api-known-gaps.json` entries may only be `SchemaNonConformance` or documented 4xx classes, and the loader rejects unknown keys and unknown classes (fail-closed). |
| SEC-SIM-19 | WP1/WP2 | `sim-report.py` and `sim-api-report.py` fail closed: non-zero exit on parse error, truncated JSON/HAR, missing input file, zero entries when the run executed (empty results = failure, not green), unknown schema/version field, over-size input (10/50 MB), or missing `--canaries` file. The workflow step that runs them has no `continue-on-error` and no `\|\| true`. `--self-test` runs before the fuzz and failure aborts the job. |
| SEC-SIM-20 | WP3 | Fuzz input handling is bounded: replay and AFL cap input at 64 KB (`MaxQueryLength`), per-input 2 s hang limit via `Join`, `-m` memory limit set to a finite value (replace `-m none` with e.g. `-m 2048`) or the job-level 60/90 min timeout plus `ulimit -v`; hung threads are abandoned only inside a process that then exits. The harness has no file/network/process I/O (`FuzzCompileContext` is in-memory) and replay reads only from the given dirs, rejecting symlinks. |
| SEC-SIM-21 | WP0 | Architecture guard: `Autheris.Tests.Simulation`, `Autheris.Tests.Fuzz`, `Autheris.Sim.ApiHost` are in no `.sln`/`.slnx`, referenced by no `src/**` or CI test csproj, not in `Dockerfile`/release workflows, and do not appear in `ci.yml`; also no `src/**` file has `InternalsVisibleTo` to them. |
| SEC-SIM-22 | WP0 | Scheduled jobs are skipped on forks (`if: github.repository == github.repository_owner/...` equivalent: guard `github.event.repository.fork == false`) so a fork cannot burn quota or run with a changed workflow's own permissions; job timeouts per section 2 remain hard limits. |
| SEC-SIM-23 | WP2 | Write phase isolation: separate process, fresh `--data-dir`, `--profile write` reachable only with the loopback guard of SEC-SIM-01/02; mutating Schemathesis methods never run against the read-phase host or any non-sim host. Shutdown on SIGTERM wipes the data dir. |
| SEC-SIM-24 | Stage 2-4 | Each addendum (P4-P10) must re-state: loopback-only targets; no real endpoints (P8/P9 containers pinned by digest and bound to `127.0.0.1`, throwaway credentials); no secrets or tokens in workflow; synthetic data only (P10 patterns anonymised and licence-cleared before commit); k6/Toxiproxy images by digest; P7 `TimeProvider` seam: superseded by SEC-SIM-25 (DI seam accepted instead of compile-out, own PRD addendum and Phase 3 before Stage 3). Generators and oracles must never put raw generated SQL/values with canaries into reports (reuse `IssueDraft`). |

### Mandatory tests per WP

- **WP0** (`SimulationIsolationArchitectureTests`, extending the section 1.6 list; add): `SimWorkflow_NoPullRequestPushOrWorkflowRunTrigger`, `SimWorkflow_ActionsPinnedBySha` (also asserts no other `uses:`), `SimWorkflow_PermissionsAreLeastPrivilege`, `SimWorkflow_CheckoutDoesNotPersistCredentials`, `SimWorkflow_InputsOnlyViaEnvAndValidated` (scans `run:` blocks for `${{ inputs`/`github.event.inputs`), `SimWorkflow_NoSecretsReferenced`, `SimWorkflow_ArtifactRetentionAtMost7Days`, `SimWorkflow_UploadPathsExcludeDataDirAndCanaries`, `SimWorkflow_SchemathesisRunsHardenedAndPinned`, `SimWorkflow_ForkGuardPresent`, `SimWorkflow_NoUnpinnedToolInstalls`, `SimWorkflow_SharpFuzzInstrumentsOnlyTrinoSqlEngine`, `SimIsolation_ProjectsAreNotInAnySolution`, `SimIsolation_NoProductionOrCiProjectReferencesSimProjects`, `SimProjects_DoNotSetAutherisIncludeTestAuth`, `SimApiHost_IsNotPublishedByReleaseOrDockerWorkflows`, `SimNotices_ListEveryThirdPartySource`, `SimCorpus_NoThirdPartyCorpusCommitted`; Meta: `SimKnownGaps_RejectSecurityKinds`, `SimQuarantine_RequiresIssueAndExpires`, `IssueDraft_RedactsCanariesAndCapsCells`, `SimSeed_RejectsMalformedValue`, `SimRunGuard_FailsClosedWhenEnvNotOne`. Each guard test carries a synthetic-negative self-check.
- **WP1**: `SltFetch_FailsClosedOnShaMismatch`, `SltFetch_RejectsNonHttpsOrUnpinnedHost`, `SltFetch_LeavesNoPartialDirectory` (script tested with a local `file://`-free fixture or `--self-test`), `SltParser_RejectsOversizedFileAndLine`, `SltParser_TreatsContentAsDataOnly`, `SltReference_DisablesExtensionLoadingAndOutsideAttach`, `SltClassifier_PolicyLeakNeverKnownGap`, `Corpus_RowFilterScenario_NeverLeaksFilteredRows`, `SimReport_FailsClosedOnEmptyMalformedOrOversizedInput`, `SimReport_EscapesMarkdownAndRedactsCanaries`.
- **WP2**: `ApiHost_BindsLoopbackOnlyAndRefusesNonLoopbackUrls`, `ApiHost_StartsAndServesSchemasWithTestAuth`, `ApiFuzz_RefusesNonLoopbackTargetUrl` (`run-api-fuzz.sh` URL validation incl. `127.0.0.1.evil.com`, `user@host`, `[::]`, `0.0.0.0`), `ApiFuzz_SchemaServersOverrideStripped`, `ApiReport_SelfTestDetectsPlantedFaults`, `ApiReport_RejectsSecurityClassInGapsFile`, `ApiReport_RejectsMalformedAndOversizedInput`, `ApiReport_FailsOnEmptyHar`, `ApiReport_ScrubsHeadersAndCanariesFromOutput`, `ApiHost_CanariesNeverInResponsesToReaderProfile`.
- **WP3**: `Replay_CommittedFindings_AllAccepted`, `Classifier_NullReference_IsCrash`, `Classifier_OverTimeLimit_IsHang`, `AcceptedRejections_ContainOnlyTrinoSqlEngineOrDocumentedTypes`, `Replay_RejectsOversizedInputAndSymlinks`, `FuzzTarget_PerformsNoIoOrNetwork` (architecture check over Fuzz project namespaces), `Fuzz_QuarantineEntriesHaveIssueNumberAndAge`, `FuzzScript_RefusesInstrumentingOutsideRunnerTemp`.
- **Stage 2-4**: per SEC-SIM-24 each addendum lists its own tests; none may be merged before Phase 3 on that addendum.

### Phase 3 delta review 2026-10-24 (csharp-security-expert, head 1aaecc48)

Verdict: **APPROVED WITH CONDITIONS** (Stages 2-4 plan level; each addendum still needs its own Phase 3 delta before Phase 4).

1. **P7 time seam: ACCEPT the DI `TimeProvider` seam, compile-out requirement dropped.** A DI seam that defaults to `TimeProvider.System` is behaviourally identical in production and adds no test-only code path; compile-out would test a different binary than the one shipped. The residual risk is a non-System provider in a production process (token expiry, consent and cache TTL bypass). Binding conditions SEC-SIM-25 and SEC-SIM-26 below close it.
2. **P9 digest pinning: ACCEPT.** `2022-latest` is replaced by `name:tag@sha256:<64 hex>` (SEC-SIM-07 applies unchanged); digest is verified by the architecture guard (SEC-SIM-27).
3. **AFL++ licence: SEC-SIM-13 stays satisfied.** Installed apt version is Apache-2.0, used unmodified as an external process, nothing distributed. The same reasoning holds for k6 and Metabase (AGPL, external, unmodified). Added: the job logs `apt-cache policy afl++` and the notices entry names the installed version; a future distro bump to an AGPL package is acceptable only while the tool stays unmodified, external and undistributed (re-check at each Ubuntu image change).
4. **P1 sample 5000 / P7 soak 120 min: ACCEPT.** `5000` matches the SEC-SIM-08 regex `^[0-9]{1,5}$|^all$`; P1 stays within the 45 min job limit. The 120 min soak equals the existing weekly guard limit; each soak job (default and fault variant) is a separate job with its own `timeout-minutes` (<= 120) and no job may be extended without a Phase 3 delta (plan section 0 default stays binding).
5. **New threats (P4-P10):** crash/hang dumps and replayed traffic contain secrets and canaries (SEC-SIM-28); third-party tools phone home or write outside the sandbox (SEC-SIM-29); fake time leaking into audit and monotonic controls (SEC-SIM-26).

| Id | Applies to | Binding condition |
|---|---|---|
| SEC-SIM-24 (amended) | Stage 2-4 | As above, with the compile-out clause replaced by SEC-SIM-25/26. |
| SEC-SIM-25 | P7 seam lane | The seam PRD addendum must cover: (a) the closed list of time-reading production paths (token and JWT validation, API-key and session expiry, consent, caches and TTLs, schema reload, lockout and rate limits, audit timestamps) and a ban on direct `DateTime(Offset).UtcNow` there, enforced by an architecture test (`Time_SecurityRelevantNamespaces_DoNotReadSystemClockDirectly`) with an explicit allowlist; (b) `TimeProvider.System` registered by default via `TryAddSingleton` in the production composition root, with no configuration, environment variable or header able to select another provider; (c) a startup guard in the production entry (`Autheris.Api`) that throws unless the resolved `TimeProvider` is `TimeProvider.System`, with no bypass flag (the sim host is a separate entry point and is the only place registering `FakeTimeProvider`); (d) `Microsoft.Extensions.TimeProvider.Testing` referenced only by `Autheris.Sim.ApiHost` and sim/test projects, never by `src/Autheris.Api` or any production project (architecture test `Time_FakeTimeProviderPackage_NotReferencedByProductionProjects`, scanning csproj and `Directory.Packages.props` use); (e) regression tests proving expiry is honoured with the fake clock (expired token rejected, consent revoked, cache not served after TTL) and fails closed on a backward clock jump; (f) no behaviour change with `TimeProvider.System` (existing suites green). The addendum needs its own Phase 3 before the seam lane starts; Stage 3 is blocked until it is merged. |
| SEC-SIM-26 | P7/P8 | Fake time applies only to wall-clock reads: monotonic controls (rate limits, timeouts, lockout windows measured with `GetTimestamp`/`Stopwatch`) are not advanced by it, or the addendum documents each one and the sim asserts it still limits. Simulated timestamps in audit and reports are labelled as simulated; the sim host never writes to a non-throwaway audit store. Clock jumps (including backwards) must produce fail-closed outcomes, never accepted-expired credentials. |
| SEC-SIM-27 | P8/P9/P10 | Architecture guard asserts every image reference in sim workflows and in `Databases.cs` used by sim jobs matches `@sha256:[0-9a-f]{64}` (SQL Server `2022-latest` included, k6, Toxiproxy, Metabase); Toxiproxy admin API (8474) and every container port bind `127.0.0.1`; DB credentials are generated per run; SQL Server EULA acceptance is recorded in `THIRD-PARTY-NOTICES.md`. Test: `SimWorkflow_AllContainerImagesDigestPinned`. |
| SEC-SIM-28 | P5/P7/P10 | Process dumps (hang watchdog), EventPipe traces and the k6 JSONL replay file stay in `$RUNNER_TEMP`, are never uploaded (extend SEC-SIM-15 exclusions); a `Hang` finding carries only a scrubbed managed stack text. Replay JSONL is generated from synthetic callers per run and deleted at job end. P10 patterns are committed only after a scan for tokens, hostnames and canaries (`BiReplay_PatternsContainNoSecretsOrRealHosts`). Reports escape NUL and control characters from P9 data shapes (`SimReport_EscapesControlCharsAndNul`). |
| SEC-SIM-29 | P7/P10 | Third-party tools run unmodified with telemetry off and no outbound network beyond the image pull (Metabase `MB_ANONYMOUS_TRACKING_ENABLED=false`, dbt `DBT_SEND_ANONYMOUS_USAGE_STATS=false`, k6 no cloud output, no `K6_CLOUD_TOKEN`); containers run `--cap-drop ALL`, `--security-opt no-new-privileges`, no docker socket, volume mounts only under `$RUNNER_TEMP`. |

Mandatory tests (`[Trait("Category","Security")]`): `Time_SecurityRelevantNamespaces_DoNotReadSystemClockDirectly`, `Time_FakeTimeProviderPackage_NotReferencedByProductionProjects`, `ApiStartup_NonSystemTimeProvider_Throws`, `ApiStartup_DefaultTimeProvider_IsSystem`, `Token_ExpiredByFakeClock_IsRejected`, `Token_BackwardClockJump_FailsClosed`, `Consent_RevokedByFakeClock_DeniesAccess`, `SimWorkflow_AllContainerImagesDigestPinned`, `SimWorkflow_NoDumpsOrReplayFilesUploaded`, `BiReplay_PatternsContainNoSecretsOrRealHosts`, `SimReport_EscapesControlCharsAndNul`, `SimWorkflow_ToolTelemetryDisabled`. Tests for the seam are owned by the seam lane, the rest by their stage addenda. No new blocking plan changes; WP0 scaffold is unaffected.

### Plan changes required

Non-blocking (take in WP0, no re-review): (a) add `retention-days: 7` to section 2 artifacts line; (b) replace AFL `-m none` by a finite limit (SEC-SIM-20); (c) add fork guard and `persist-credentials: false` to the workflow template (SEC-SIM-05/22); (d) `quarantine.json` gets an owner-approval date and 90-day expiry (SEC-SIM-17). Blocking: none.

### Open questions (defaults taken)

1. HAR upload: default scrubbed HAR only (SEC-SIM-14); owner can allow unscrubbed HAR since data is synthetic. 2. apt AFL++ version pin: default accept the `ubuntu-24.04` distro version and log it. 3. Fork guard: default on.

## Changelog (G-PLAN-REBASE drift log)

- 2026-10-24 (WP1): `GateRunner` is bound to the gate fixture (`GateModel` snapshots, `GatePolicies` catalog), so the corpus uses the direct compiler path of decision 10: `FastSqlEngine.Compile` plus `SqliteCompiledSqlBinder.Bind` on the corpus SQLite file, with `GateRunner.IsGoverned` reused to separate documented refusals from crashes. The catalog comes from `PRAGMA table_info`; corpus tables have no tenant column.
- 2026-10-24 (WP1): the ADO `CommandTimeout` of `Microsoft.Data.Sqlite` is only a busy timeout and cannot stop a long query. Reference and engine reads therefore interrupt the statement with `sqlite3_interrupt` after a wall-clock timeout (reference 10 s, engine 5 s). An interrupted engine statement is a `Hang` finding (candidate defect, GitHub issue #339); an interrupted reference is a harness mismatch. A circuit breaker (40 hangs of one class in one file) skips the rest of that file so the job stays red but cannot time out.
- 2026-10-24 (WP1): the manifest carries `host` and `repo` next to `commit`; `fetch-sqllogictest.sh` builds the URL from them, checks it against a script-level host allowlist (`raw.githubusercontent.com`) and offers `--verify`, `--check-url` and `--self-test` modes so the fail-closed paths are testable without network access.
- 2026-10-24 (WP1): `SimResults` lines may carry a `disposition` (`KnownGap`, `Finding`, ...) and run statistics (`Executed`, `Equal`, ...) are written as note lines, so a clean run still has entries (SEC-SIM-19: empty results are a failure). `sim-baseline.json` holds `minExecuted`, `maxHarnessMismatchPercent` and the baselined KnownGap classes.
- 2026-10-24 (WP1): scenario B reference is a SQLite backup copy of the corpus file with `DELETE FROM t1 WHERE a IS NULL OR NOT (a > 120)`; a B result equal to the unfiltered A result while the filtered reference differs is a `PolicyLeak`. Deterministic probes on our own table (`SltRowFilterProbes`) cover subqueries, joins, set operations, CTEs and aggregates. No masks are defined in WP1 scenarios; the "masked value" leak check belongs to the later stages.
- 2026-10-24 (WP1): `Autheris.Tests.Simulation.csproj` gains a `None Update` item that copies the manifest, classification files, own fixtures and `sim-baseline.json` to the output directory (no package change).
