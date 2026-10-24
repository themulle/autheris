# REQ-SIMTEST: Phase 2 architecture plan (simulation testing)

Input PRD: [`2026-10-24-req-simulation-testing.md`](2026-10-24-req-simulation-testing.md) (REQ-SIM-01..61). Branch `feat/simtest`. Code facts verified on `feat/simtest` (28c5fa8e).

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
   - `AUTHERIS_SIM_SAMPLE` (`^[0-9]{1,5}$|^all$`) defaults to 3000 nightly; the weekly run uses `all`.
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
   - `findings/crashes/`, `findings/hangs/` (`.gitkeep`) and `findings/quarantine.json` (`[{sha256, issue, reason}]`, owner-only).
   - A finding input is committed only together with its product fix (another lane). Until then the nightly stays red, unless the owner quarantines it explicitly.
23. **Smoke run.** `scripts/sim/run-parser-fuzz.sh <seconds>`:
   - `dotnet publish tests/Autheris.Tests.Fuzz -c Release -o $RUNNER_TEMP/fuzz-bin`
   - `sharpfuzz $RUNNER_TEMP/fuzz-bin/TrinoSqlEngine.dll`: only this copy is instrumented, never a production build. The tool is installed with `dotnet tool install SharpFuzz.CommandLine --version <pin> --tool-path $RUNNER_TEMP/sf`.
   - AFL++ comes from `sudo apt-get install -y --no-install-recommends afl++` on `ubuntu-24.04`, with `AFL_SKIP_CPUFREQ=1 AFL_NO_UI=1 AFL_I_DONT_CARE_ABOUT_MISSING_CRASHES=1`.
   - `afl-fuzz -i corpus -x corpus/sql.dict -o $RUNNER_TEMP/afl -t 5000 -m none -s $SEED -V <seconds> -- dotnet $RUNNER_TEMP/fuzz-bin/Autheris.Tests.Fuzz.dll --afl`
   - Then `--replay` over `afl/default/{crashes,hangs}`. The job fails if either is non-empty, apart from quarantined sha256 values.
   - Local run: `dotnet run --project tests/Autheris.Tests.Fuzz -- --replay tests/Autheris.Tests.Fuzz/corpus tests/Autheris.Tests.Fuzz/findings` (no AFL needed).

## 2. Workflow `.github/workflows/simulation-nightly.yml` (owned by WP0)

The workflow follows `parity-nightly.yml`:
- Triggers: `schedule` and `workflow_dispatch` only. Top-level `permissions: {}`; each job `permissions: contents: read`. No secrets, no retries.
- Actions pinned by SHA, copied: checkout `3d3c42e5aac5ba805825da76410c181273ba90b1`, setup-dotnet `a98b56852c35b8e3190ac28c8c2271da59106c68`, upload-artifact `cf430e030ddbb5b0abf93d22962f4752f3646cd9`.
- Dispatch inputs `job` (choice: all|corpus|api|parser), `seed` and `sample` reach the shell only through `env:` and are regex-validated before use.
- Each job sets `AUTHERIS_SIM_RUN: '1'` and `AUTHERIS_SIM_REPORT_DIR`. Each selects itself with `if: github.event_name == 'workflow_dispatch' || github.event.schedule == '<its cron>'`.
- Jobs call only the scripts named in this plan, so the YAML stays stable while WP1-3 work.

| Job | Cron (UTC) | Timeout | Budget |
|---|---|---|---|
| `sim-corpus` | `0 3 * * *` | 45 | build 5, fetch 1, 3000 sampled queries over A+B about 10 |
| `sim-api-fuzz` | `30 3 * * *` | 60 | build 5, read phase 25, write phase 15 |
| `sim-parser-fuzz` | `30 4 * * *` | 30 | install and build 8, AFL `-V 300`, replay 2 |
| `sim-corpus-weekly` | `0 5 * * 0` | 90 | `sample=all` |
| `sim-parser-fuzz-weekly` | `0 6 * * 0` | 90 | AFL `-V 3600` |

Artifacts: reports, HAR, issue drafts and the AFL crash/hang inputs; never the host data dir.

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
- WP0 owns the workflow, the architecture guard file, `THIRD-PARTY-NOTICES.md` and both csproj/lock pairs. Its notices entries: sqllogictest (fetched, MIT elected), Schemathesis (MIT, container), SharpFuzz (MIT), AFL++ (Apache-2.0, apt).
- WP0 sets every package and project reference up front, including the pinned SharpFuzz version and the Schemathesis image version plus digest. WP1-3 add no packages to the shared projects. If a WP needs a change, the lead makes it.
- `Autheris.sln`, `ci.yml` and `Directory.Build.props` are **not touched**.
- Stage 1 total: 12 days (14 with WP1b).

## 4. Stages 2-4 (plan level; each gets an addendum before Phase 3)

| P | Scope / approach | Placement | Days | Key risk |
|---|---|---|---|---|
| P4 generator | One logical query model rendered to WebSQL, GraphQL and REST over the P2 fixture; CsCheck after a one-day spike; shrinking to under 10 tokens | nightly job `sim-generator`, 30 min | 6 | Renderer drift between channels |
| P5 oracles | Channel equality, TLP, NoREC and security invariants; each oracle has a mutant self-test | with P4 | 5 | False positives on NULL/collation |
| P6 policy variants | Stricter caller's result must be a subset or masked form of the broader caller's (roles, tenants, consent, masking) | with P4 | 3 | Fixture policy explosion |
| P7 soak | `TimeProvider` injection in the test host, k6 external, leak trend and deadlock watchdog | weekly, 120 min | 6 | Needs a product `TimeProvider` seam: own PRD decision |
| P8 faults | Toxiproxy container in front of source and governance DBs, clock jumps; expected outcome 503/fail-closed, then recovery | weekly | 4 | Flaky timing; assert outcomes, not latencies |
| P9 dialects | Oracle, SQL Server, DuckDB and PostgreSQL containers; skewed data, special characters, extreme timestamps | weekly | 6 | Runner time; reuse gate container DBs |
| P10 BI replay (optional) | Hand-curated anonymised pattern files | weekly | 2 | Provenance of the patterns |

## 5. Risks (Stage 1)

1. A `ProjectReference` to a test project can fail because of the generated entry point. Mitigation: `GenerateProgramFile=false` in the Simulation project, or the linked-file fallback (decision 1).
2. The gate's `SqliteGateDatabase` may hard-load the gate schema. Mitigation: the direct compiler path (decision 10).
3. SLT semantics are SQLite-specific (integer division, affinity) and may flood the accepted-difference list. Mitigation: rules are keyed by error class, so one rule covers a whole class.
4. AFL timing makes runs non-deterministic despite `-s`. Mitigation: the acceptance relies on the deterministic replay of the committed corpus.
5. Schemathesis v4 CLI flags may differ from those named here. WP2 verifies them against the pinned version, and the guard checks only the image pin.
6. `WebApplicationFactory.UseKestrel` is required. If it is unavailable, the fallback is `dotnet run src/Autheris.Api` with the same settings through environment variables.
