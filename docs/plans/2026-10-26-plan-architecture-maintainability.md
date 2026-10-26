# Plan: Architecture and maintainability improvements (ARCH-MNT)

Owner request 2026-10-26: "investigate and implement architecture and maintainability improvements in a new branch".
Phase 2 (`csharp-architect`), branch `refactor/architecture-maintainability-plan`, base `origin/main` 87d33b74.
Ground rules: no production yet, so breaking **internal** changes are allowed; public API behaviour and security must not regress; no DDL; no work inside in-flight areas (section 6).

## 1. Context and method

- Read-only static survey (scripts over the tree, no build). Generated ANTLR code (`obj/`) excluded unless stated.
- File sizes: line counts per `.cs`; partial classes aggregated per class.
- Method sizes: brace-matching scanner over declarations (heuristic). The 13 largest hits were spot-checked by hand; one false positive (`InterpolateRlsFilter`) was removed.
- Project graph: parsed every `*.csproj` `ProjectReference`; compared with `tests/Autheris.Tests.Architecture`.
- Static state: grep for non-readonly static fields and static readonly collections in `src/`.
- Dead options: every `public {get; set|init;}` property of the 182 `*Options` classes, counted `.Property` reads in `src/` outside the defining file and outside `*Validat*` and `*StartupValidator*` files.
- Totals: 3,507 `.cs` files and 635k lines. In `src/`: 123 files over 500 lines, 19 over 1,000 lines, 313 methods over 100 lines and 152 over 150 lines (8,248 methods scanned).

## 2. Findings by theme

### 2.1 Oversized units (top 15; excluding baseline-schema DDL files)

| # | Unit | Lines | Note |
|---|------|-------|------|
| 1 | `{Sqlite,SqlServer,PostgreSql}GovernanceRepository` (24 partials each) | 7,558 / 7,340 / 7,273 | Consent partials 2,026/1,952/1,920; more than 60% cross-dialect duplication (REV-05) |
| 2 | `AstSecurityVisitor` (11 partials, TrinoSqlEngine) | 3,148 | security core; size only, no refactor proposed |
| 3 | `QueryAnalysisService` (4 partials) | 2,306 | `AnalyzeQueriesAsync` 472, `AnalyzeSqlTextAsync` 345 |
| 4 | `Extensions/Dbt/DbtMetadataIngestionService.cs` | 2,173 | `IngestGovernanceStreamAsync` 519, `IngestManifestStreamAsync` 482 |
| 5 | `TrinoSqlEngine/Ast/Builder/SqlAstBuilder.cs` | 1,673 | visitor, many small methods; acceptable |
| 6 | `GraphQL/Types/MutationTypes.cs` | 1,385 | `ApproveConsentRequestAsync` 316 |
| 7 | `Extensions/OpenMetadata/OpenMetadataSyncService.cs` | 1,346 | 11 catch sites |
| 8 | `Application/Mcp/Services/McpDatasetCatalog.cs` | 1,319 | |
| 9 | `Api/Extensions/DependencyInjection/GatewayStartupValidator.cs` | 1,226 | `ValidateStartup` 592 (41-632), `ValidateSqlServerWindowsAuth` 143 |
| 10 | `GraphQL/Interceptors/QueryCostAnalyzerRule.cs` | 1,139 | `CalculateSelectionSetCost` 345 |
| 11 | `Application/Olap/FederatedStagingService.StageAsync` | 649 (method) | longest hand-written method |
| 12 | `Api/Endpoints/TableStreamEndpoints.HandleBulkStreamAsync` | 588 (method) | endpoint handler with inline logic |
| 13 | `Application/Sql/Services/GovernedSqlRewriter.RewriteCoreAsync` | 580 (method) | enforcement path |
| 14 | `GraphQL/Types/QueryTypes.cs` | 785 | resolver grab-bag |
| 15 | `Api/Extensions/GatewayServiceCollectionExtensions.cs` | 692 | `AddGatewayAuth` 303, `AddGatewayGraphQL` 136, `AddGatewayOptions` 91 |

More methods over 350 lines: `GovernedTreeQueryService.ExecuteAsync` 439, `CrossSourcePlanner.Plan` 411, `AiDataGuardrailService.ExecuteToolWithGuardrailAsync` 407, `DeclarativeConsentApplyService.ApplyManifestAsync` 407, `McpToolExecutionHandler.ExecuteToolAsync` 380, `CatalogSchemaModel.BuildAsync` 359, `DbtEndpoints.MapDbtEndpoints` 355.
The baseline schemas (`*BaselineSchema.Apply` 981/842/816) are DDL and out of scope.
`Program.cs` (161 lines) and `AggregateResultMapper.cs` (325 lines) are not oversized, so no action is needed.
The largest test files are `CatalogTypeMapGoldenTests` 1,456, `SecurityFindingsRemediationTests` 1,412 and `SimulationIsolationArchitectureTests` 1,158 (deferred, see section 6).

### 2.2 Layering and project-reference graph

- No cycles. The `src` graph matches the intended layers: Domain has no references; Application references Domain and TrinoSqlEngine; Infrastructure, GraphQL and Extensions reference Application and Domain; Api is the composition root.
- Enforced today (`ArchitectureTests.cs`, NetArchTest, assembly-scoped): Domain has no project dependencies; Application has no dependency on Infrastructure, GraphQL or Api; Infrastructure has no dependency on Api; GraphQL has no dependency on Infrastructure or Api; Extensions has no dependency on Infrastructure, GraphQL or Api; TrinoSqlEngine has no dependency on Autheris; core layers have no dependency on Extensions; Application and Domain have no dependency on AspNetCore; Application has no dependency on Prometheus. Many more targeted rules exist (audit, Oracle, consent, OLAP).
- Gaps:
  - (a) No test freezes the `.csproj` graph. A new `ProjectReference` is caught only if a type dependency is actually used.
  - (b) `TrinoSqlEngine.Tests` references Application and Infrastructure, but only 3 of its 183 files use them (`Security/ActivationProbes.cs`, `Security/SqleRequiredFilterScopeTests.cs`, `Ast/AstSecurityVisitorRequiredFilterTests.cs`).
  - (c) No namespace-matches-assembly rule. Three types sit in a namespace that belongs to another assembly: `Api/Mcp/McpStdioRunner.cs` uses `Autheris.Application.Mcp.Services`, `Infrastructure/Docs/FileSystemUniversalDocStore.cs` uses `Autheris.Application.Docs.Services`, and `Application/Sql/WebSqlSecurityException.cs` uses `Autheris.Domain.Common`. There are 35 namespace/folder mismatches in total. 28 of them are the intentional flat `Autheris.Domain.Options` namespace across the `Options/*` subfolders, which is accepted.
  - (d) Four projects are absent from `Autheris.sln` (Simulation, Parity, Sim.ApiHost, Fuzz). This is intentional and already enforced (`ParityIsolation_ProjectIsNotInAnySolution`, simulation isolation tests), so no action is needed.

### 2.3 God classes and mutable static state

- **ANTLR DFA caches (memory-exhaustion risk).** The generated `SqlBaseParser` and `SqlBaseLexer` hold `protected static DFA[] decisionToDFA` and `static PredictionContextCache sharedContextCache` (obj/.../SqlBaseParser.cs:39-40). They are process-wide and never cleared. Each distinct input shape adds DFA states for the life of the process. SLL, and the LL fallback in `FastSqlEngine.cs:~465`, both fill the cache. Pooled parsers (`ParserPooledObjectPolicy`) and the two raw lexers (`Analysis/SqlParameterExtractor.cs:48`, `Ast/Visitors/AstSecurityVisitor.Support.cs:281`) all share it. The only input cap is `MaxQueryLength` 65,536 per statement, and it does not bound the cumulative total. This becomes ARCH-MNT-15.
- **Unbounded static cache:** `Application/Governance/Policies/PatternMatcher.CompiledCache`, a `ConcurrentDictionary<string, Regex>` with `RegexOptions.Compiled` and no cap. Its sibling `Domain/Model/TargetPatternMatcher` caps at 1,024 entries; `SqlSecurityValidator` (2,000), `CompiledSubRuleCache` (20,000), `StreamingRowFilterAstEvaluator` (1,000) and `SubgraphSecurityValidator.SeenNonces` (10,000 with pruning) are capped too.
- **Static mutable state with cross-instance effect:**
  - `Extensions/DataCatalog/PurviewDataCatalogClient.cs:29-30`: a static `_cachedToken` and `_tokenExpiry` shared by every instance. The cache is not keyed by tenant or client id and survives an options reload. It also holds the dev mock token in the same static.
  - `Domain/Model/IndividualGrantLimits` exposes static `Configure`/`Reset`, which `IndividualGrantServiceCollectionExtensions.cs:18` calls through `PostConfigure`. This is global state in Domain, and test order matters.
  - `Application/Telemetry/DataSourceLoggers.s_factory` and `SourceCommandTelemetry.s_defaultPolicyProvider` are static service locators.
  - `GraphQL/Catalog/CatalogRelationRegistry._registry` is a static registry.
  - `Api/Security/BasicAuthenticationHandler.cs:36-38` holds a static `SemaphoreSlim` that is resized from configuration.
  - The remaining static fields are counters with `Interlocked` or `[ThreadStatic]` fields (`CompiledSqlTemplate._keyBuilder`) and are acceptable.

### 2.4 Dead configuration options

- 99 option properties have no read outside their defining file and validators (heuristic; each one must be confirmed before removal). The whole `WebhooksOptions` delivery block (9 properties) is in this set, plus `CatalogSearchOptions` (7), `AuditOptions` (7) and the `MetricsOptions` cardinality limits (3).
- **Security-flavoured flags that are silently ignored (false sense of safety):**
  - `McpOptions.RequirePiiMasking` and `McpOptions.AllowAnonymousDiscovery`
  - `ScimOptions.AllowedIpRanges` and `ScimOptions.EnableMassChangeProtection`
  - `EnvoyExtAuthzOptions.TrustedPeers`
  - `CasbinOptions.EnforceInQueryPipeline`
  - `AuthenticationOptions.EnableKerberos`
  - `OtlpOptions.AllowInsecureTransport`
  - `AuditOptions.StoreStatementText`
  - `ClassificationOptions.MaskingRules` and `ClassificationOptions.StepUpAuthThresholdRank`
  - `ExtensibilityOptions.BreakGlassAllowedRoles`
  - `RedisOptions.FailClosedOnRevocationLookupError` (one other src file names it; verify)
  - `GatewayOptions.Insecure`
- `GatewayOptionsConsumptionRatchetTests` only ratchets wrapper injection; nothing checks for unconsumed properties.

### 2.5 DI composition

- 386 Singleton, 73 Scoped, 14 Transient, 42 HostedService and 20 HttpClient registrations. There are 222 factory-lambda resolutions (`sp.GetRequiredService`); 69 of them are in `GatewayGovernanceServiceExtensions.cs` (419 lines, 146 registrations, `AddAutherisGovernance` 293 lines).
- `GatewayServiceCollectionExtensions.cs` still mixes options, infrastructure, auth and GraphQL, although the modular `DependencyInjection/Gateway*ServiceExtensions` files exist.
- `Plan9ServiceExtensions` (`AddPlan9Services`, `MapPlan9Endpoints`) is named after a plan id, not a capability.
- `Program.cs:49-50`: `ValidateOnBuild = true` always and `ValidateScopes` only in Development. This is acceptable because the Unit composition tests build with `ValidateScopes = true`.
- No test snapshots the descriptor set, so refactoring registrations has no regression net.

### 2.6 Naming and docs/plans hygiene

- 45 test files are named after review rounds, waves or increments (`SecurityReview20261002*`, `Round4*`, `Robin100*`, `*Inc2*`) instead of features (merged with REV-10).
- `docs/plans`: 32 `.md` files, 0 broken relative links. Four plans are not linked from the master plan and their work is merged, so master rule 5 says to delete them: `2026-10-24-plan-issue-341-graphql-error-mapping.md` (#346), `2026-10-24-plan-pers-04-sec-281-6-residuals.md` and `2026-10-24-prd-pers-04-sec-281-6-residuals.md` (#338), and `2026-10-24-plan-security-low-quickwins.md` (#337).

## 3. Prioritized backlog

Value and risk: H/M/L. Size: S/M/L. BP means behaviour-preserving.

| ID | Title | Evidence | Proposed change | Value | Risk | Size | Files | Security notes | BP |
|----|-------|----------|-----------------|-------|------|------|-------|----------------|----|
| ARCH-MNT-01 | Project-reference graph freeze | 2.2(a) | Arch test parses all csproj files; exact edge allowlist; no cycles; src never references tests or tools | H | L | S | 1 | none | yes |
| ARCH-MNT-02 | Source-size ratchet | 2.1: 19 src files over 1,000 lines | Arch test: src file at most 1,000 lines unless allowlisted with its current ceiling; ceilings may only fall; a stale entry fails the test | H | L | S | 2 | none | yes |
| ARCH-MNT-03 | Namespace/assembly consistency | 2.2(c) | Arch test: namespace root equals assembly root (3 known violators allowlisted); then fix the 3 types (follow-up) | M | L | S | 1 (+5 follow-up) | none | yes |
| ARCH-MNT-04 | Clock and HttpClient ratchets (REV-03, REV-09) | 166 src files use `DateTime.UtcNow`; no `new HttpClient(` ban | Count ratchet on `DateTime.UtcNow`/`Now` in src (may only fall); ban `new HttpClient(` outside an allowlist | M | L | S | 2 | HttpClient ban keeps egress handlers enforced | yes |
| ARCH-MNT-05 | Unconsumed-options ratchet | 2.4: 99 candidates | Arch test: reflection over `*Options` properties; text scan for reads; baseline file may only shrink | H | L | S | 2 | none | yes |
| ARCH-MNT-06 | Resolve dead security options | 2.4 security flags | Per property: wire it or delete it. When a key is deleted, a bound config key must not be silently ignored, so startup fails on it | H | M | M | ~15 | **Security review**: deletion versus wiring; unknown-key startup rejection is fail-closed | no |
| ARCH-MNT-07 | Error-response arch test extension (REV-06a, REV-09) | ad-hoc `{error=ex.Message}` in `VirtualFilterEndpoints`, `IcebergRestCatalogEndpoints`, `McpStdioRunner:60` | Extend `NoRawExceptionMessageInEndpointResponsesTests` patterns (`Results.Json`, JSON-RPC `-32603`); current violators allowlisted | H | L | S | 1 | detection only | yes |
| ARCH-MNT-08 | Fix raw exception leaks (REV-06b) | as 07 | Map to `GatewayExceptionStatusMapper`/ProblemDetails; empty the allowlist | H | M | M | ~6 | **Security review** (information disclosure; response bodies change) | no |
| ARCH-MNT-09 | DI descriptor snapshot | 2.5 | Unit test renders ordered descriptors (service, implementation or factory marker, lifetime, key) for each refactored extension under 2 config profiles; golden files | H | L | S | 3 | regression net for fail-closed decorator order | yes |
| ARCH-MNT-10 | Split `AddAutherisGovernance` | 293 lines, 146 registrations | Partial files per aggregate (consent, catalog, audit, policy); same call order; snapshot stays equal | M | L | S | 5 | none if snapshot is equal | yes |
| ARCH-MNT-11 | Slim `GatewayServiceCollectionExtensions` | 692 lines | Move `AddGatewayGraphQL` and `AddGatewayOptions`/`AddGatewaySection` into `DependencyInjection/` files; public signatures unchanged | M | L | S | 3 | `AddGatewayAuth` excluded (13) | yes |
| ARCH-MNT-12 | Rename `Plan9ServiceExtensions` | 2.5 | `AsyncJobServiceExtensions` (`AddAsyncJobsAndDatasourceTesting`, `MapAsyncJobEndpoints`) | L | L | S | 3 | none | yes |
| ARCH-MNT-13 | Split `AddGatewayAuth` | 303 lines | Per-scheme private methods; snapshot plus auth integration tests | M | M | M | 3 | **Security review** (authentication wiring) | yes |
| ARCH-MNT-14 | Decompose `GatewayStartupValidator.ValidateStartup` | 592-line method | One `IStartupCheck` per area, run in the same order; aggregate failures identical | M | M | M | ~10 | **Security review**: fail-closed order; no check may be dropped (mutation test) | yes |
| ARCH-MNT-15 | Bound ANTLR DFA caches | 2.3 | See section 3.1 | H | M | M | ~5 | **Security review mandatory** | yes (parse results) |
| ARCH-MNT-16 | Bound `PatternMatcher.CompiledCache` | unbounded; sibling caps at 1,024 | Same cap-and-clear pattern as `TargetPatternMatcher` | M | L | S | 2 | DoS hardening; Phase 3 light | yes |
| ARCH-MNT-17 | Instance-scope Purview token cache | static token, no key | Instance field keyed by (TenantId, ClientId); invalidate on options change | M | L | S | 2 | **Security review** (credential/token isolation) | yes |
| ARCH-MNT-18 | Remove static config from Domain | `IndividualGrantLimits`, `DataSourceLoggers`, `SourceCommandTelemetry`, `CatalogRelationRegistry` | Inject an options-backed service; keep the static facade only where hot paths require it | M | M | M | ~15 | grant limits are security bounds: tests for default-deny limits | yes |
| ARCH-MNT-19 | Engine test project isolation | 2.2(b) | Move the 3 files to `Autheris.Tests.Unit`; drop the Application/Infrastructure refs | M | L | S | 5 | check Stryker configs that target engine tests | yes |
| ARCH-MNT-20 | Long-method extraction, one PR per hotspot | 2.1 items 11-13 plus the methods listed after the table | Extract-method only; no logic change | M | M | L | 1-3 each | enforcement paths: Phase 3 per PR | yes |
| ARCH-MNT-21 | GraphQL type split | `MutationTypes` 1,385, `QueryTypes` 785 | Resolver classes per aggregate; schema SDL snapshot must be equal | M | L | M | ~8 | none if SDL is equal | yes |
| ARCH-MNT-22 | Dialect repository dedup (REV-05) | 2.1 item 1 | Shared helpers plus a dialect strategy, starting with ConsentGrants and Outbox pairs | H | M | L | 2-4 per step | tenant/audit predicates must not drop | yes |
| ARCH-MNT-23 | Test reliability (REV-01, 02, 04) | 15 wall-clock asserts; 110 `Task.Delay`/`Sleep` in Unit; 5 env-var files | Correctness asserts, signal/`WaitUntil` helper, `FakeTimeProvider`, `EnvVarScope` helper | H | L | M | ~17 | none | yes (tests) |
| ARCH-MNT-24 | docs/plans housekeeping | 2.6 | Delete the 4 merged plans; list them under "Completed plans" in the master | L | L | S | 5 | none | yes |
| ARCH-MNT-25 | Test file naming and shared fakes (REV-10) | 45 round-named test files; about 10 fakes redefined | Rename by feature; move fakes to Tests.Common | M | L | M | many | none | yes |
| ARCH-MNT-26 | Catch-site audit (REV-07) | 875 catch sites | Read-only audit of log-less `catch (Exception)` in security paths | M | L | M | 0 (report) | security relevant | yes |

### 3.1 ARCH-MNT-15: bounded ANTLR DFA caches

- **Approach.**
  - Stage 0 measures DFA state count growth over the fuzz and SQL corpus (needs PR #360 merged).
  - Stage 1 gives every pooled parser and lexer its own `ParserATNSimulator`/`LexerATNSimulator` with a private `DFA[]` and `PredictionContextCache`, built in `ParserPooledObjectPolicy.Create`. `Return` discards an instance whose state count is over a cap or which has served more than N uses. Total memory is then bounded by pool size times the cap, without a global lock. The two raw `new SqlBaseLexer` sites move to a pooled lexer.
  - Alternative: a global `ClearDFA()` under a reader/writer lock when the summed states exceed a cap. It is simpler, but every parse waits during a reset.
#### Phase 3 binding conditions (csharp-security-expert, 2026-10-26)

**Verdict: APPROVED WITH CONDITIONS.**

**Threat.** `decisionToDFA` and `sharedContextCache` are process-wide statics that only grow. An authenticated caller sending many distinct statements (each at most `MaxQueryLength` 65,536) adds DFA and prediction-context states for the life of the process. This leads to OOM and a denial of service for all tenants. Fail-closed does not apply to the OOM itself; the control must be a bound, not a rejection. Code facts verified: `new SqlBaseLexer(` has five sites, not two. They are `FastSqlEngine.cs:415` (per parse), `SqlParameterExtractor.cs:48`, `AstSecurityVisitor.Support.cs:281`, `Application/Analytics/AggregationNegationGuard.cs:108` and `Application/Diagnostics/Shadowing/PiiShadowingRedactor.cs:329`. `ParserPooledObjectPolicy.Create` builds the parser with the static `Interpreter`.

**Chosen approach: generation swap (not per-instance DFA, not global `ClearDFA`).**
- Per-instance private DFA is rejected: it multiplies memory by pool size, every instance re-learns (cold latency on each), and per-parse lexers would need pooling too.
- Global `ClearDFA()` under a lock is rejected: it stalls all parses and `ClearDFA` racing with in-flight prediction is not a documented-safe operation.
- Chosen: one immutable-reference `AntlrCacheGeneration` holds `DFA[]` for parser and lexer plus a `PredictionContextCache`. Each parse or lex binds its simulator to the generation current at rent time. When the state count exceeds the cap, a fresh generation is published with `Interlocked.CompareExchange` (one winner). In-flight work keeps the old generation, which the GC collects after the last reference drops. No lock on the parse path, no stall, DFA objects stay in the shape ANTLR already shares across threads.
- The count (sum of `DFA.states.Count` plus cache size) is checked at `Return` and at lexer release, at most every K uses (K configurable), never per token.

**Binding conditions**
- ARCH-MNT-15-SC-01: All `SqlBaseLexer` and `SqlBaseParser` construction goes through one factory that binds the current generation. All five lexer sites move to it; an architecture test forbids `new SqlBaseLexer(` and `new SqlBaseParser(` elsewhere in `src`.
- ARCH-MNT-15-SC-02: A generation is never mutated by anyone except ANTLR's own prediction. Nothing calls `ClearDFA()` on a generation that any parse or lexer still references. Swap is a single reference publish.
- ARCH-MNT-15-SC-03: The cap is a total-state cap per generation (parser plus lexer DFA states plus context-cache entries), counted deterministically. A single parse that exceeds the cap mid-parse completes on its generation; the swap happens afterwards. Peak memory is thus at most about two generations plus in-flight ones; document this.
- ARCH-MNT-15-SC-04: Options `MaxDfaStatesPerGeneration` and `GenerationCheckInterval` are validated at startup (`ValidateOnStart`): both greater than 0, with an upper bound for the cap. Invalid or missing values fail startup. No silent default override at runtime and no "0 means unlimited".
- ARCH-MNT-15-SC-05: Fail-closed. Any exception while binding, counting or swapping rejects that parse with the existing generic parse-error path (the instance is discarded, not returned to the pool). The swap path must not catch and continue with a half-bound simulator. Never a fallback to the old static arrays.
- ARCH-MNT-15-SC-06: `AstSecurityVisitor`, `SqlSecurityValidator` and all downstream validation run on every parse regardless of generation state, swap, rebind or error recovery. Swap code has no early-return path past them.
- ARCH-MNT-15-SC-07: Parse results are independent of cache state. SLL-first then LL fallback (`FastSqlEngine` ~465), ARC-S-09 token-mismatch finality, per-statement time budgets and `MaxQueryLength` stay unchanged. The swap adds no new timeout or retry that could change the accept/reject decision.
- ARCH-MNT-15-SC-08: A rebound parser (after a swap) has its `ParserATNSimulator` rebuilt with `PredictionMode.SLL` and its error listeners, error strategy and `Reset()` state restored exactly as `ParserPooledObjectPolicy.Create` and `Return` do today. A parser never leaves the pool in LL mode (existing check kept).
- ARCH-MNT-15-SC-09: No SQL text, statement hash, tenant or principal in metrics, logs or exceptions from the swap. Only counters and gauges: generation number, state count, swap count. Metric tags are fixed, low-cardinality, never user-derived.
- ARCH-MNT-15-SC-10: Swap events are rate-limited by the cap itself and by `GenerationCheckInterval`; an adversary cannot force a swap per request to degrade throughput below cold-parse speed. A minimum number of parses between swaps (`MinParsesPerGeneration`, validated greater than 0) is enforced.
- ARCH-MNT-15-SC-11: Cold-cache latency, with the cap forced low, is gated with `SqlGate.BenchCheck`; the default cap is calibrated from the Stage 0 corpus measurement and written into the options default with a comment citing the measurement.

**Mandatory tests** (all `[Trait("Category","Security")]` unless noted; names are binding)
- ARCH-MNT-15-T-01 `Parse_CorpusTreesAndErrors_IdenticalAcrossWarmColdAndSwappedGenerations`: SQL corpus plus negative corpus; compare serialized tree and error codes.
- ARCH-MNT-15-T-02 `Generation_StateCount_StaysBoundedForDistinctStatements`: generate N (for example 5,000) distinct statements deterministically from a fixed seed with a low cap; assert the max observed state count at every swap check is at most cap plus a stated per-parse slack, and that the swap count is at least 1. Count states, no wall-clock, no GC measurement.
- ARCH-MNT-15-T-03 `Generation_ConcurrentParsesDuringSwap_NoExceptionsAndEquivalentResults`: parallel parses (at least 16 tasks) with a tiny cap that forces many swaps; all results equal the single-threaded baseline.
- ARCH-MNT-15-T-04 `Generation_OldGenerationNotCleared_WhileParseInFlight`: hold a parse (gate) on generation 1, force a swap, release; assert it completes correctly and generation 1 is unreachable afterwards (`WeakReference` after forced GC, `Category=Unit`).
- ARCH-MNT-15-T-05 `Swap_FaultInjected_RejectsParse_DoesNotReturnInstanceToPool`: failing count or bind seam; assert a parse rejection, a discarded instance and no use of static arrays.
- ARCH-MNT-15-T-06 `AstSecurityVisitor_RunsOnEveryParse_IncludingFirstParseAfterSwap`: forbidden statement is rejected on the parse that triggers the swap and the one right after.
- ARCH-MNT-15-T-07 `Parser_AfterRebind_UsesSllModeAndSameListeners`.
- ARCH-MNT-15-T-08 `Options_InvalidCapIntervalOrMinParses_FailStartup` (zero, negative, above upper bound, missing).
- ARCH-MNT-15-T-09 `SwapMetricsAndLogs_ContainNoSqlTextOrTenantIdentifiers`: capture log and `MeterListener` output while parsing a statement with a unique marker; assert the marker is absent and tags are the fixed set.
- ARCH-MNT-15-T-10 `Swap_AdversarialDistinctStatements_RespectsMinParsesPerGeneration`.
- ARCH-MNT-15-T-11 (architecture, `Category=Architecture`) `SqlBaseLexerAndParser_AreConstructedOnlyThroughTheFactory`; coordinate with the guardrail test project.
- ARCH-MNT-15-T-12 `SqlGate.BenchCheck` cold-cache case (SC-11).

**Size.** Medium: about 250 to 350 lines of source (generation type, factory, policy changes, options with validator, metrics, five call-site moves) and about 450 lines of tests. One PR, one work package, Phase 4 Sonnet.

**Dependency on PR #360.** The implementation and tests T-01 to T-11 do not need #360; T-02 generates its own statements. Only SC-11 default calibration (Stage 0) benefits from the #360 fuzz corpus. If #360 is not merged, ship a conservative default and record the measurement as a follow-up, which does not block the implementation. It must not start before PR 1 (guardrails) merges only if T-11 is placed there.

**Blocker.** None.

## 4. Proposed PR cut

### PR 1: Architecture guardrails (tests only; ARCH-MNT-01, 02, 03 test, 04, 05, 07)

Every test ships with a self-test that plants a violation in an in-memory sample and proves the scanner detects it (pattern of `EachPatternDetected`). All files are in `tests/Autheris.Tests.Architecture/`.

| WP | Files | Tests |
|----|-------|-------|
| WP1.1 | `ProjectReferenceGraphArchitectureTests.cs` | `ProjectGraph_MatchesAllowlist`, `ProjectGraph_HasNoCycles`, `SrcProjects_DoNotReferenceTestOrToolProjects`, `Scanner_DetectsPlantedEdge` |
| WP1.2 | `SourceSizeRatchetArchitectureTests.cs`, `Baselines/source-size-ceilings.txt` | `SrcFiles_StayWithinCeiling`, `Ceilings_HaveNoStaleEntries`, `Scanner_DetectsOversizedSample` |
| WP1.3 | `NamespaceAssemblyConsistencyTests.cs` | `TypeNamespaces_MatchTheirAssemblyRoot`, `Allowlist_HasNoStaleEntries` |
| WP1.4 | `ClockAndHttpClientRatchetTests.cs`, `Baselines/datetime-now-counts.txt` | `DateTimeNowUsage_DoesNotGrow`, `NewHttpClient_IsOnlyUsedInAllowlist`, `Scanner_DetectsPlantedUsage` |
| WP1.5 | `OptionsConsumptionInventoryTests.cs`, `Baselines/unconsumed-options.txt` | `UnconsumedOptionProperties_DoNotGrow`, `Baseline_HasNoStaleEntries` |
| WP1.6 | `NoRawExceptionMessageInEndpointResponsesTests.cs` (extend) | `ResultsJson_ExceptionMessage_IsDetected`, `JsonRpcError_ExceptionMessage_IsDetected` |

Verification: `dotnet test tests/Autheris.Tests.Architecture`. Then delete the ratchet allowlist entry for one known violator locally and confirm the test goes red; this is a manual check and is not committed. Total of about 10 files.

### PR 2: Test reliability (tests only; ARCH-MNT-23)

Lead decision (default taken): the composition-root decomposition below moves to PR 3, because `chore/remove-orphaned-code` is still in flight and changes the DI composition root. Test reliability is tests only, touches no in-flight files and addresses the flakes seen under load.

| WP | Files | Tests |
|----|-------|-------|
| WP2.1 | `tests/Autheris.Tests.Common` helpers (`WaitUntil` polling helper, `EnvVarScope`) | helper self-tests |
| WP2.2 | the unit tests with wall-clock budget asserts (REV-01) | sub-second budgets become correctness asserts; longer budgets become hang guards |
| WP2.3 | the 3 worst `Task.Delay`/`Thread.Sleep` files (REV-02) and the 5 env-var files (REV-04) | signals, `WaitUntil` or `FakeTimeProvider`; `EnvVarScope` |

Verification: the full Unit suite plus the touched filters repeated 3 times.

### PR 3: Composition-root decomposition (src/Autheris.Api DI; ARCH-MNT-09, 10, 11, 12)

WP2.0 is landed first; WP2.1 to WP2.3 then run in parallel, with no shared files.

| WP | Files | Verification |
|----|-------|--------------|
| WP2.0 | `tests/Autheris.Tests.Unit/DependencyInjection/CompositionDescriptorSnapshotTests.cs`, `Snapshots/governance.txt`, `Snapshots/graphql-options.txt` | `AddAutherisGovernance_DescriptorSnapshot_IsStable`, `AddGatewayGraphQlAndOptions_DescriptorSnapshot_IsStable` (dev SQLite and production-like profiles) |
| WP2.1 | `GatewayGovernanceServiceExtensions.cs` plus `.Consent.cs`, `.Catalog.cs`, `.Audit.cs`, `.Policy.cs` | snapshot is equal; Unit filter `FullyQualifiedName~DependencyInjection` |
| WP2.2 | `GatewayServiceCollectionExtensions.cs`, new `DependencyInjection/GatewayGraphQlServiceExtensions.cs`, `DependencyInjection/GatewayOptionsServiceExtensions.cs` | snapshot is equal; Unit `~GraphQL` and `~Options` |
| WP2.3 | `Plan9ServiceExtensions.cs` renamed to `AsyncJobServiceExtensions.cs`, `Program.cs`, `GatewayApplicationBuilderExtensions.cs` | Unit `~AsyncJob`; Integration host-start smoke |

Final gate (lead): full Unit, Architecture and Integration runs. About 14 files.
Sequencing: land PR 2 after `chore/remove-orphaned-code` merges, or regenerate the golden files on rebase; that branch changes `GatewayExecutionServiceExtensions.cs`, which PR 2 does not touch.
Phase 3 light review: confirm that equal snapshots imply unchanged decorator order.

### Follow-ups, in order

1. The rest of ARCH-MNT-23: the remaining `Task.Delay`/`Sleep` sites plus a `Task.Delay` ratchet.
2. ARCH-MNT-24 housekeeping, together with the lead's tracker update.
3. ARCH-MNT-16, then 17.
4. ARCH-MNT-03 fixes, then ARCH-MNT-19.
5. ARCH-MNT-15 (after PR #360), then ARCH-MNT-06, then ARCH-MNT-08.
6. ARCH-MNT-13, then 14.
7. ARCH-MNT-21 and ARCH-MNT-20, one hotspot per PR.
8. ARCH-MNT-22 and ARCH-MNT-18.
9. ARCH-MNT-25 and ARCH-MNT-26.

## 5. Decisions (default taken)

1. Domain `Options/*` subfolders keep the flat `Autheris.Domain.Options` namespace, so they are not counted as violations.
2. Ratchet baselines live as text files beside the tests; ceilings may only fall, and stale entries fail the test.
3. Method-length ratchets are not added, because they would need Roslyn in the architecture project. Changing that project's package set touches the NuGet signature policy tests.
4. Baseline-schema DDL files are excluded from all refactors.

## 6. Deferred until merged (no changes in the first PRs)

Both branches below merged on 2026-10-26 (#360 and #361), so these areas are open for the follow-ups. The PR 1 ratchets were re-run against the merged `main`.

- **PR #360 (parser-fuzz):** `scripts/sim/run-parser-fuzz.sh`, the simulation workflow, the `Autheris.Tests.Fuzz` harness and `SimulationIsolationArchitectureTests.cs` (1,158 lines, a split candidate later).
- **`chore/remove-orphaned-code`:** `GatewayExecutionService` (704 lines), `IGatewayExecutionService`, `BatchRelationLoader`, `ChunkedQueryExecutor`, `GatewayExecutionServiceExtensions.cs`, `GraphQlOptions.MaxInClauseBatchSize`, `InvoiceModels`, `docs/plans/security.txt` and `docs/architecture/arc42.md`.

## 7. Reviewer findings (merged)

`csharp-code-reviewer` survey of 2026-10-26, deduplicated into section 3:

- REV-01, REV-02, REV-04: ARCH-MNT-23.
- REV-03: ARCH-MNT-04 (ratchet); the `TimeProvider` migration is a follow-up inside ARCH-MNT-23 and ARCH-MNT-18.
- REV-05: ARCH-MNT-22.
- REV-06: ARCH-MNT-07 (detection) and ARCH-MNT-08 (fix, security review).
- REV-07: ARCH-MNT-26.
- REV-08: SQL generators already share a base; no action.
- REV-09: ARCH-MNT-04 (HttpClient ban) and ARCH-MNT-07. The assembly layering rule exists (`ArchitectureTests.cs`), and ARCH-MNT-01 adds the csproj freeze. The rule that every endpoint group requires authorization becomes a new item under ARCH-MNT-07, as an allowlisted ratchet.
- REV-10: ARCH-MNT-25.

## 8. Delivery status (2026-10-26)

| PR | Branch | Items | Phase 3 | Phase 5 | Status |
|----|--------|-------|---------|---------|--------|
| #362 | `refactor/architecture-maintainability-1` | ARCH-MNT-01, 02, 03 (test), 04, 05, 07; this plan | not needed (detection only) | APPROVED after 1 round (ratchets tolerate shrink and deletion) | merged |
| #363 | `refactor/architecture-maintainability-2` | ARCH-MNT-23 part 1 (budget asserts, 3 worst delay files, `EnvVarScope`, `WaitHelper`) | not needed (tests only) | APPROVED after 1 round (R1 to R10) | merged |
| #364 | `refactor/architecture-maintainability-3` | ARCH-MNT-15 (generation swap) | SC-01..SC-11 plus rulings D1..D7 | APPROVED; Low findings fixed | merged |
| #365 | `refactor/architecture-maintainability-5` | ARCH-MNT-23 rest (sleep-based sync replaced, test-sleep ratchet) | not needed (tests only) | APPROVED | merged |
| #366 | `refactor/architecture-maintainability-6` | ARCH-MNT-16, 17, 27 | 17-SC-01..05, 27-SC-01..03 | APPROVED; Low findings fixed | merged |
| #367 | `refactor/architecture-maintainability-4` | ARCH-MNT-09, 10, 11, 12, 14; wave 2 addendum and Phase 3 conditions | 14-SC-01..06 | APPROVED after 1 round (TestAuth-independent DI snapshot); CI startup stack overflow from the WP4.3 rename fixed (self-recursive `MapAsyncJobEndpoints` wrapper removed, `ExtensionSelfRecursionArchitectureTests`, local host start to `/health/ready` 200) | merged |
| #369 | `refactor/architecture-maintainability-7` | ARCH-MNT-06 | 06-SC-01..09 | APPROVED; findings fixed | merged |
| #370 | `refactor/architecture-maintainability-9` | ARCH-MNT-15-FU-02 (cap calibration; default 50,000 kept) | not needed (no default change) | APPROVED after 1 round | merged |
| PR 10 | `refactor/architecture-maintainability-10` | ARCH-MNT-22 step 1 (ITSM Outbox, WebhookOutbox, ConsentGrants cores; child plan [`2026-10-26-plan-arch-mnt-22-dialect-dedup.md`](2026-10-26-plan-arch-mnt-22-dialect-dedup.md)) | 22-SC-01..08 | APPROVED | open; merge only after green PG/MSSQL Integration CI |
| #368 | `refactor/architecture-maintainability-8` | ARCH-MNT-08 | 08-SC-01..09 | APPROVED after 1 round (SC-01 guard widened) | merged |

Merge order: any. After #362 merges, #363 and #364 must pass its ratchets. #364 adds no `DateTime.UtcNow`, no `new HttpClient(`, no src file over 1,000 lines, and its new options are consumed.

New follow-ups found during delivery:

- **ARCH-MNT-15-FU-01:** extend `SqlGate.BenchCheck` with a cold-cache P99 threshold (Phase 3 ruling D1).
- **ARCH-MNT-15-FU-02 (done in PR 9, `refactor/architecture-maintainability-9`):** calibrated on 2,794 distinct statements (308 fuzz seeds plus the engine corpora): 9,080 states at steady state (4,445 for the seeds alone), at most 411 new states per statement, about 1.25 KB per state, 0 swaps. The default of 50,000 stays: that is 5.5x the steady state, and the worst case of 50,000 x `HardCapFactor` 4 is about 239 MB per generation. `AntlrCacheCalibrationTests` pins the invariant: the seed corpus fits in one generation with 2x headroom, measured by state counts and not by time.
- **ARCH-MNT-27:** `SecurityReviewG3EndpointAuthzTests.Envoy_ExportEmitsPathPrefixAndValidatesParameters` failed once with a `RegexMatchTimeoutException` under parallel build load. Check whether the regex timeout belongs to production code, which would mean timeouts under real load too, and fix it with a deterministic assertion or a `NonBacktracking` regex.
- **Resolved:** ARCH-MNT-27 (#366: production Envoy regexes replaced by non-regex checks); DI decomposition is PR 4.
- **DI snapshot maintenance:** every PR that adds or removes a DI registration must update `tests/Autheris.Tests.Unit/DependencyInjection/Snapshots/*.txt` with `AUTHERIS_UPDATE_SNAPSHOTS=1` locally (rejected in CI) and explain the diff in the PR.
- **Lesson from #367:** a pure rename let a wrapper extension bind to itself; only a host start shows it. `ExtensionSelfRecursionArchitectureTests` now guards it, and refactors that touch the composition root must prove a local host start (`dotnet run` with `ASPNETCORE_ENVIRONMENT=Development`, `/health/ready`).
- **ARCH-MNT-06-FU-01 (owner decision pending):** `docs/features/f-perf-13` (line 172, `MaxCostPerMinute`), `f-gov-13` (line 90, `MaskingRules`) and `f-ops-02` (`Gateway__DrainDelaySeconds`) still mention removed keys. Feature docs change only on owner request.
- **ARCH-MNT-06-FU-02 (SC-09):** SCIM mass-change protection (`EnableMassChangeProtection`) was never implemented and its flag is deleted, so it is a known absent control.
- **ARCH-MNT-06-FU-03:** report `Otlp:AllowInsecureTransport` in the startup danger list (`StartupChecks`); today it only logs a warning.
- **ARCH-MNT-06-FU-04:** wire the Helm `drainDelaySeconds`/`shutdownTimeoutSeconds` to `Gateway:HighAvailability:*`, together with the `QueryTimeoutSeconds` alignment that NF-HA-01 requires.
- **ARCH-MNT-08-FU-01:** let `OlapSqlRejectedException` accept only fixed reason codes, so no caller data can reach a response.
- **ARCH-MNT-23-FU-01:** add `TimeProvider` seams to ReBAC tuple expiry and the `ConsentCacheService` expiry to remove the last real-time waits. `StartupWarmupTests.WarmupMetrics_RecordDurationAndOutcome_NoRequestCounters` failed once in a full run (it uses a global metrics listener) and needs isolation.
- **ARCH-MNT-22 step 1 result:** production lines went from 1,904 to 1,587 (-317; the plan estimated -610), because the verbatim SQL literals stay in the dialect files. By area: ITSM Outbox -119, ConsentGrants -92, WebhookOutbox -5 (kept for the shared binding and the golden coverage). The dialect divergences D1-D5 are preserved and pinned. Follow-ups DEF-01..DEF-04 are in the child plan; the most important is DEF-02, the PostgreSQL/SQL Server tenant-parse fallback, which must fail closed. Next steps for ARCH-MNT-22: Audit, Catalog and Consent (L).

## 9. Wave 2 addendum (2026-10-26)

Owner order: continue with the rest of the plan. Verified against `main` 38a4b1ff (#362 to #364 merged). Each PR is its own branch off `origin/main`; the PR 1 ratchets stay green, and shrink-only baselines are lowered in the PR that shrinks them. This section replaces "Follow-ups, in order" of section 4.

### 9.1 PR 4 (`-4`): DI decomposition and startup checks (ARCH-MNT-09..12, 14)

Refresh of WP2.0 to WP2.3: #361 left `GatewayExecutionServiceExtensions.cs` in place and #364 changed only the SQL engine registrations, so the cut stands. `AddAutherisGovernance` spans lines 48-341 (one helper at 342). `GatewayServiceCollectionExtensions` has 693 lines. `GatewayStartupValidator.ValidateStartup` spans lines 41-632 with 64 `throw` sites and no aggregation: it is fail-fast, the first violation wins, and warnings are logged between checks. The split keeps that contract. WP4.0a and WP4.0b land first; WP4.1 to WP4.4 then run in parallel and share no files.

| WP | Files | Tests and verification |
|----|-------|------------------------|
| WP4.0a | `tests/Autheris.Tests.Unit/DependencyInjection/CompositionDescriptorSnapshotTests.cs`, `Snapshots/composition-dev-sqlite.txt`, `Snapshots/composition-production-like.txt` | `Composition_DescriptorSnapshot_IsStable(profile)`. It replays the Program.cs registration sequence (lines 60-69) on an in-memory configuration. One line per descriptor: index, service type, lifetime, key, and `type:<Impl>`, `instance:<Type>` or `factory:<ReturnType>`. Lambda method names are excluded because they change under extract-method. Regenerate with `AUTHERIS_UPDATE_SNAPSHOTS=1`. |
| WP4.0b | `tests/Autheris.Tests.Unit/Configuration/GatewayStartupValidatorGoldenTests.cs`, `Snapshots/startup-validator-failures.txt`, `Snapshots/startup-validator-warnings.txt` | `EachInvalidCase_FailsWithGoldenMessage`: one case per throw site, run under Production and Development, recording the exception type and message. `ValidConfigs_LogGoldenWarnings`. Both goldens are generated on unchanged code. |
| WP4.1 | `GatewayGovernanceServiceExtensions.cs` becomes `partial`, plus `.Consent.cs`, `.Catalog.cs`, `.Audit.cs`, `.Policy.cs`, `.Identity.cs` | Every private method covers one contiguous line range of today's body and is called in the same order. The snapshot is equal. |
| WP4.2 | `GatewayServiceCollectionExtensions.cs` becomes `partial`, plus `GatewayServiceCollectionExtensions.Options.cs` (lines 88-218) and `.GraphQL.cs` (lines 546-682) | Keeping the class partial means the class name, the namespace and the callers stay unchanged. The snapshot is equal. |
| WP4.3 | `Plan9ServiceExtensions.cs` renamed to `AsyncJobServiceExtensions.cs` (`AddAsyncJobsAndDatasourceTesting`, `MapAsyncJobEndpoints`), `Program.cs:69`, `GatewayApplicationBuilderExtensions.cs:289` | Unit `~AsyncJob`, then a host-start smoke test. |
| WP4.4 | `GatewayStartupValidator.cs` (`ValidateStartup` becomes a loop), plus new `StartupChecks/StartupCheck.cs`, `GatewayStartupChecks.cs`, `PolicyAndNetworkStartupChecks.cs`, `GraphQlAndSwitchStartupChecks.cs`, `AuthenticationStartupChecks.cs`, `ProductionStartupChecks.cs`, `StorageStartupChecks.cs` | Goldens are equal. `StartupChecks_OrderIsStable` compares the check names to a golden. `EachCheck_IsGuardedByAGoldenCase` drops one registry entry at a time and requires at least one golden case to change, which is an in-test mutation proof that no check was dropped. Lower the `source-size-ceilings.txt` entry, or delete it once the file is under 1,000 lines. |

Decision (default taken): the checks are `internal sealed record StartupCheck(string Name, Action<StartupCheckContext> Run)` in one static ordered array, not DI services, because validation runs before the container exists. `StartupCheckContext` holds the options, the environment, `GetEnv`, the logger, `IsDevelopment`, `DangerBypasses` and `Warnings`. The bypass and warning lists are computed once; both are pure functions of the options. An internal overload `ValidateStartup(..., IReadOnlyList<StartupCheck> checks)` serves the mutation test. The order follows today's line ranges:

1. ObjectGraph (55).
2. The eight existing per-area methods (58-65).
3. Egress (67).
4. CasbinModel (76).
5. CasbinPolicy (110).
6. ReverseProxy (146).
7. DevelopmentInContainer (171).
8. GraphQlTrustedDocuments (184).
9. FederationSigning (206).
10. SecuritySwitchReport (223-261).
11. OperationalInvariants (263-295).
12. BasicAuthSession (296).
13. BasicAuthUsers (303).
14. ForwardAuth (313-373).
15. ProductionGovernance (374-420).
16. ProductionTransportAndOrigins (421-501).
17. ProductionAuthentication (502-539).
18. ProductionStorageAndAudit (540-612).
19. GovernanceDbProvider (613).
20. ClusterState (618-631).

### 9.2 PR 5 (`-5`, in implementation)

This is the rest of ARCH-MNT-23: the remaining `Task.Delay`/`Thread.Sleep` synchronization in tests, plus a `Task.Delay`-in-tests ratchet. No design is needed.

### 9.3 PR 6 (`-6`): ARCH-MNT-16, 17, 27

1. **ARCH-MNT-16** (`PatternMatcher.cs`). This change copies the `TargetPatternMatcher` pattern:
   - Add `MaxCachedPatterns = 1024`.
   - Replace `GetOrAdd` with `TryGetValue`, then `CompileAndCache`, which runs `Clear()` when `Count >= cap`.
   - Add `internal ClearCache()` and `CachedPatternCount`.
   - Tests in `tests/Autheris.Tests.Unit/Governance/PatternMatcherCacheTests.cs`: `ManyDistinctPatterns_CacheStaysWithinCap` and `AfterEviction_StillMatches`. Each asserts `<= cap` only, so the tests stay safe in parallel.
2. **ARCH-MNT-17.** The client is registered with `AddHttpClient<PurviewDataCatalogClient>()` and is therefore transient. A token cache held in an instance field would cache nothing, so the cache moves to a new singleton:
   - The singleton is `src/Autheris.Extensions/DataCatalog/PurviewTokenCache.cs`, registered in `DataCatalogServiceCollectionExtensions.cs` and injected with `TimeProvider` and `IOptionsMonitor<GatewayOptions>`.
   - It has one slot keyed by `PurviewCredentialKey(TenantId, ClientId, SecretFingerprint = SHA-256 hex of ClientSecret)` and a per-instance `SemaphoreSlim`.
   - An `OnChange` callback clears the slot. The development mock token is returned as a constant and is never cached.
   - The static fields are deleted from `PurviewDataCatalogClient.cs`, and its `datetime-now-counts.txt` entry is lowered from 4 to 0.
   - Tests in `tests/Autheris.Tests.Unit/DataCatalog/PurviewTokenCacheTests.cs`: `TwoCredentialSets_DoNotShareTokens`, `SecretRotation_Refetches`, `OptionsChange_ClearsCache`, `DevMockToken_IsNeverCached`, `Expiry_Refetches` (with a fake `TimeProvider`) and `ConcurrentCallers_FetchOnce`.
3. **ARCH-MNT-27.**
   - **Root cause:** the timeout comes from production code. `EnvoyExtAuthzService.DnsLabelRegex` and `AuthzPathRegex` (lines 439-440, `Compiled`, 100 ms) are linear and anchored. The match timeout is measured in wall-clock time, so a thread that is descheduled under load trips it. On the export endpoints this would surface as a 500 under real load.
   - **Fix:** replace the regex checks with deterministic `SearchValues<char>` checks, `IsDnsLabel(ReadOnlySpan<char>)` and `IsAuthzPath(...)`, in the new file `src/Autheris.Application/Mesh/Services/EnvoyExportValidation.cs`. Do the same for `VersionSegment` (line 31).
   - **Tests:** `EnvoyExportValidationTests.IsDnsLabel_AgreesWithFormerRegex` (boundaries at 63/64 characters, leading and trailing `-`, upper case, empty) and `IsAuthzPath_AgreesWithFormerRegex`. The existing G3 test stays unchanged.

### 9.4 PR 7 (`-7`): ARCH-MNT-06 inventory and unknown-key rejection

Correction to section 2.4: `EnvoyExtAuthzOptions.TrustedPeers` (`IsTrustedPeer`, used at `EnvoyExtAuthzEndpoints.cs:138`), `AuthenticationOptions.EnableKerberos` (`IsKerberosConfigured`), `RedisOptions.FailClosedOnRevocationLookupError` (`Resolve…`), `GatewayOptions.Insecure` (the `Is*` members), `ExtensibilityOptions.BreakGlassAllowedRoles` (`GetEffective…`), `TenantRegistryGateOptions.MaxNewRegistrationsPerIssuerPerWindow` and the `DevOptions`/`DevToolingOptions` keys (read raw in `DevConfiguration.Apply`) are consumed. They are false positives of the "outside the defining file" heuristic.

All of them stay. They leave the baseline through a new `Baselines/options-consumed-indirectly.txt` file with lines of the form `Type.Property|ConsumingMember`. A new test, `IndirectConsumers_AreReadOutsideTheDefiningFile`, checks that each named consuming member is read outside the defining file.

| Property | Intended semantics | Wire | Decision | Files |
|----------|--------------------|------|----------|-------|
| `OtlpOptions.AllowInsecureTransport` | allow plaintext OTLP export | S | wire: outside Development, an `http://` endpoint with a non-loopback host fails the start unless the flag is true | `GatewayTelemetryServiceExtensions.cs` (~168), `TelemetryOptions.cs` |
| `ScimOptions.AllowedIpRanges` | CIDR allowlist of SCIM callers per realm | S | wire: empty means unrestricted (today's behaviour); a non-empty list makes the handler fail authentication for any other peer; an invalid CIDR fails the start | `ScimAuthenticationHandler.cs` (~74), `IdentityRealmsOptions.cs`, `GatewaySecurityOptionsValidator.cs` |
| `ScimOptions.EnableMassChangeProtection`, `MassChangeThresholdPercentage` | block bulk SCIM deprovisioning | L | delete, backlog ARCH-MNT-06-FU-01 | `IdentityRealmsOptions.cs` |
| `McpOptions.RequirePiiMasking`, `AllowAnonymousDiscovery` | masking and auth on MCP | n/a | delete: masking is always on unless `Insecure:warn_allow_unmasked_ai_access` is set, and auth applies unless `danger_bypass_mcp_auth` is set | `McpOptions.cs` |
| `CasbinOptions.EnforceInQueryPipeline` | Casbin enforcement in the query pipeline | n/a | delete (`Casbin:Enabled` governs) | `RlsOptions.cs` |
| `AuditOptions.StoreStatementText` | store SQL text in the audit log | n/a | delete: the audit log never stores statement text, and telemetry has `AllowStatementText` | `AuditLoggingOptions.cs` |
| `ClassificationOptions.MaskingRules`, `StepUpAuthThresholdRank`, `AutoApproveMaxRank` | classification-driven masking, step-up and auto-approval | L | delete: `ApprovalPipelineConfig` and DataMasking govern these | `ClassificationOptions.cs` |
| `EntraIdAuthOptions.SidClaimType`, `GroupsClaimType`; `AdfsAuthOptions.SidClaimType`, `GroupSidClaimType` | configurable identity claims | M | delete: `Sid.cs` keeps fixed claim resolution, and configurable claim types would widen the spoofing surface | `JwtOptions.cs` |
| `IdentityOptions.PseudonymKeyRef` (telemetry) | key used for pseudonymous identity attributes | M | delete: no pseudonymous telemetry path exists | `TelemetryOptions.cs` |
| `TableCacheEncryptionOptions.KeyDerivationContext` | HKDF context | S | delete: it is not secret, and the constant in `TableCacheCryptoHelper` stays | `TableCacheEncryptionOptions.cs` |
| `TableCacheOptions.AllowStaleOnOutage` | serve stale data on an outage | n/a | delete: false is the fail-closed default and was never implemented | `TableCacheOptions.cs` |
| `L1MemoryCacheOptions.SensitiveTableTtlSeconds` | shorter L1 TTL for sensitive tables | M | delete, backlog FU-02: revocation freshness is enforced by epoch validation (`FailClosedOnSensitiveTables`) | `RedisOptions.cs` |
| `RebacOptions.MaxCachedDecisions` | cap on a ReBAC decision cache | n/a | delete: no such cache exists | `RlsOptions.cs` |
| `PostAuthSidRateLimitOptions.MaxCostPerMinute` | cost-based limit per SID | L | delete, backlog FU-03 | `RateLimitingOptions.cs` |

Each deleted row also leaves `unconsumed-options.txt`, `appsettings*.json` and the Helm values. Non-security baseline entries (Webhooks, CatalogSearch, Metrics and others) stay in the shrink-only ratchet.

**Unknown-key rejection (default taken; it lives in its own files):**

- **Files.** `src/Autheris.Api/Configuration/UnknownGatewayKeyGuard.cs` holds the static `ThrowIfUnknownKeys(IConfiguration)`. `GatewayConfigurationSchema.cs` holds the side-bound `(section, Type)` pairs (for example `Gateway:DuckDbOlap`, `Gateway:Catalog`, `Gateway:State`, `Gateway:TableCache`, `Gateway:AsyncJobs`), the free-form subtrees and the keys owned by specific guards.
- **Call site.** `Program.cs` calls the guard once, right after `DevConfiguration.Apply` (line 56). Dev presets inject keys, so the guard has to run after them. `LegacySwitchGuard` and `SerilogSectionGuard` run earlier (lines 54-55), so their specific messages win. `WebSql:SqlRewriterEngine` and `WebSql:PlanCache` are exempt, so `ValidateRemovedWebSqlSettings` keeps its message. This touches neither `AddGatewayOptions` nor `GatewayStartupValidator` (PR 4), and in `Program.cs` it is a different hunk from WP4.3.
- **Detection.** The guard walks the leaves of `GetSection("Gateway").AsEnumerable(true)` and resolves each path segment by segment. The rules are, in order:
  - The longest side-bound prefix switches the type.
  - Public properties match case-insensitively.
  - An `IDictionary<string,T>` key is free-form.
  - A list or array segment must be a numeric index.
  - A scalar must be a leaf.
  - An `IConfigurationSection`, a `Dictionary<string,string>` or an `object` is free-form.
- **Why not `ErrorOnUnknownConfiguration`.** `BinderOptions.ErrorOnUnknownConfiguration` is not used. It stops at the first type, cannot merge the side-bound sections, and would have to change every `Bind` call.
- **Failure.** The guard throws one `InvalidOperationException` that lists up to 20 unknown keys, sorted, in both `A:B` and `Gateway__A__B` form. It never includes a value.
- **Scope.** Only the `Gateway` root is checked. `Logging`, `Kestrel`, `ConnectionStrings`, `OTEL_*` and `AllowedHosts` are out of scope. Environment variables map onto the same keys and are checked the same way. Helm renders `Gateway__*` environment variables, so shipped values are covered by a test.
- **No bypass.** The guard has no escape hatch and does not honour `AUTHERIS_ALLOW_LEGACY_CONFIG`.
- **Tests** (`tests/Autheris.Tests.Unit/Configuration/UnknownGatewayKeyGuardTests.cs`):
  - `UnknownKey_FailsStartup_ListsKeyNotValue`
  - `EnvVarStyleKey_IsChecked`
  - `DictionaryAndListKeys_AreAccepted`
  - `SideBoundSectionKeys_AreAccepted`
  - `DeletedSecurityFlag_FailsStartup` (`Gateway:Mcp:RequirePiiMasking`)
  - `RemovedWebSqlKey_KeepsSpecificMessage`
  - `DevPresetKeys_AreKnown`
  - `ShippedConfiguration_HasNoUnknownGatewayKeys` (`src/Autheris.Api/appsettings*.json`, `deploy/containers/**/appsettings*.json`, `deploy/helm/autheris/values*.yaml`, `templates/deployment.yaml`, `deploy/podman-compose*.yaml`)
  - `Schema_CoversEveryGatewaySectionLiteralInSrc`, a drift test over `"Gateway:..."` literals and `GatewaySections`

### 9.5 PR 8 (`-8`): ARCH-MNT-08 raw exception messages

All sites switch to `ApiProblems.Problem` (`application/problem+json` with `type`, `title`, `status`, a fixed generic `detail`, `code`, `correlationId` and `traceId`). The original exception is logged through `ApiProblems.LogFailure`. Dev-gated `isDev ? ex.Message` branches are removed, and statuses stay as they are unless stated otherwise. New codes go into `ApiErrorCodes`. The arch test baseline becomes empty, and the regex is widened to `\b\w*(?:ex|Ex|exception|Exception)\.Message\b`, which also catches `sex.Message` in `GovernancePlanEndpoints:81,120`.

| Site | Response after the change |
|------|---------------------------|
| `CatalogApiEndpoints:745` | 400 `INVALID_SECRET_REF` |
| `DataContractEndpoints:65` | narrowed to `ArgumentException`, `JsonException` or `ValidationException`: 400 `DATA_CONTRACT_INVALID`; any other exception goes to the global handler |
| `DuckDbOlapEndpoints` (3 sites) | 400 `OLAP_QUERY_FAILED` or `OLAP_RESULT_TOO_LARGE`, written as a problem body while `!HasStarted` |
| `GovernanceEndpoints.Audit:385` | 429 `PRIVACY_BUDGET_EXHAUSTED`; the header and the extensions `clientId`, `consumed` and `totalBudget` are kept |
| `GovernanceEndpoints.Access:187` | 400 `ROW_FILTER_SYNTAX_INVALID` in every environment |
| `GovernanceEndpoints.Access:340,619`, `RebacEndpoints:76` | audit `DetailsJson`, not a response: `reasonType = ex.GetType().Name` instead of the message |
| `GovernancePlanEndpoints:81,85,120,124` | 403 `ACCESS_DENIED`; `catch (Exception)` narrowed to `ArgumentException` or `InvalidOperationException`: 400 `GOVERNANCE_PLAN_INVALID` |
| `IcebergRestCatalogEndpoints:107-110` | 403 `ACCESS_DENIED`, 400 `BAD_REQUEST` ("The request is invalid."), 501 `NOT_IMPLEMENTED`, 500 `INTERNAL_ERROR` |
| `ProcedureEndpointRoutes:395,401` | 400 `PROCEDURE_ARGUMENT_INVALID`, 422 `PROCEDURE_BUSINESS_RULE_VIOLATION`; `WriteErrorAsync` emits a problem body |
| `VirtualFilterEndpoints:189-207` | 409 `VIRTUAL_FILTER_CONFLICT` or `RESOURCE_LOCKED`, 404 `RESOURCE_NOT_FOUND`, 400 `VIRTUAL_FILTER_INVALID`, 400 with `code = ex.Code` (governed `VirtualFilterErrorCodes`), 400 `INVALID_JSON` or `BAD_REQUEST` |
| `McpStdioRunner:60` | `{"jsonrpc":"2.0","error":{"code":-32603,"message":"Internal error","data":{"correlationId":"<guid>"}},"id":null}`; the log entry carries the same id |

Tests: `RawExceptionMessage_NeverReachesTheClient(site)` throws a fake exception with a `SENTINEL-<guid>` message per site and asserts the code, the presence of `correlationId`, and that the body does not contain the sentinel. `McpStdio_InternalError_IsGenericWithCorrelationId` covers the MCP site. The architecture test runs with an empty baseline.

### 9.6 PR 9 (optional) and later

- **FU-02 is cheap (S).** `tests/Autheris.Tests.Fuzz/Replay/ParserCacheCalibrationTests.cs` replays the 309 seed files on a fresh `AntlrCacheGenerations` with the `stateCounter` hook and records the peak number of states per generation. The 50,000 default stays unless twice the peak exceeds it, and the numbers are recorded here.
- **FU-01 is not cheap (M).** `SqlGate.BenchCheck` reads only the G8 `SqlCompilerBenchmarks` output. The cold/warm `SqlParserCacheBenchmarks` needs a reader, a ratio threshold and a workflow job, so FU-01 is deferred.
- **Later.** ARCH-MNT-22 and the long-method splits (ARCH-MNT-20) need no design now.

### 9.7 Phase 3 scope and questions

PR 7 and PR 8 need a mandatory review. ARCH-MNT-14 (PR 4) and ARCH-MNT-17 (PR 6) need a light review. ARCH-MNT-16 and 27 need none beyond the PR 6 light review.

1. (07) Unknown `Gateway` keys fail the start in every environment, including Development, with no escape hatch. Is that accepted?
2. (07) Is limiting the check to the `Gateway` root acceptable?
3. (07) The failure message lists keys only, never values, capped at 20. Is that acceptable?
4. (07) OTLP: is the loopback exemption for `http` acceptable? Should plaintext stay allowed in Development?
5. (07) SCIM `AllowedIpRanges`: should it use the raw peer address or the forwarded IP after the `KnownProxies` processing? Should an empty list mean unrestricted, or deny outside Development?
6. (07) Is it acceptable to delete the configurable SID and group claim types in favour of fixed resolution?
7. (07) For `Mcp:RequirePiiMasking`, `Mcp:AllowAnonymousDiscovery` and `Casbin:EnforceInQueryPipeline`, confirm that enforcement is unconditional today, so deleting them changes nothing.
8. (08) Dev-gated exception messages are removed in all environments; the detail is available only in the logs, by correlation id. Is that accepted?
9. (08) May the privacy-budget 429 keep the `clientId`, `consumed` and `totalBudget` extensions?
10. (08) Audit details record the exception type instead of the message. Is that sufficient for forensics?
11. (08) Iceberg REST: should errors stay ProblemDetails or follow the Iceberg `ErrorModel`?
12. (08) Narrowing `catch (Exception)` turns unexpected errors from 400 into a mapped 500. Is that accepted?
13. (08) MCP stdio: should the error keep `id: null`, and is a GUID correlation id acceptable?
14. (14) Do equal goldens (failure messages for every throw site, warnings, check order) and the per-check mutation test prove fail-closed order and completeness? Is a lambda-insensitive descriptor snapshot enough to prove the decorator order is unchanged?
15. (17) Is the single-slot singleton cache sufficient for token isolation: keyed with the secret fingerprint, cleared on options change, with the mock token never cached and the token never logged?

### 9.x Phase 3 binding conditions (csharp-security-expert, 2026-10-26)

Verdicts: PR 7 (ARCH-MNT-06) APPROVED WITH CONDITIONS. PR 8 (ARCH-MNT-08) APPROVED WITH CONDITIONS. PR 4 (ARCH-MNT-14) APPROVED WITH CONDITIONS. PR 6 (ARCH-MNT-17, 27) APPROVED WITH CONDITIONS. ARCH-MNT-16 needs no condition beyond the plan.
Code facts checked: `Mcp:RequirePiiMasking`, `Mcp:AllowAnonymousDiscovery`, `Casbin:EnforceInQueryPipeline` have no reader in `src/` (defaults true/false/true), so deleting them changes nothing. MCP auth is gated only by `IsMcpAuthBypassed`. `IsMcpUnmaskedAllowed` has no reader outside `GatewayOptions.cs`, so the plan's statement "masking is always on unless warn_allow_unmasked_ai_access" is unverified and gets a test (T-03). `SidClaimType`/`GroupsClaimType`/`GroupSidClaimType` have no reader outside `JwtOptions.cs`. `ScimAuthenticationHandler` fails with generic messages; `HttpContextClientIpResolver` is the canonical post-forwarded-headers IP.

**Rulings on questions 1-15**
1. Accepted: unknown `Gateway` keys fail startup in every environment, no bypass, `AUTHERIS_ALLOW_LEGACY_CONFIG` not honoured.
2. Accepted: `Gateway` root only; `Logging`, `Kestrel`, `ConnectionStrings`, `OTEL_*`, `AllowedHosts` are out of scope.
3. Accepted: key names only, sorted, capped at 20 plus a "N more" count; never a value, and never a value-derived substring (a mistyped key can itself be a secret, see SC-06-04).
4. Accepted: loopback `http` is allowed; plaintext in Development is allowed. Non-loopback `http` outside Development fails unless `AllowInsecureTransport` is true, and then logs a startup warning and appears in the danger list.
5. Use the resolved client IP from `HttpContextClientIpResolver` (trusted proxies only; raw peer when no proxy is trusted). Empty list means unrestricted (not a regression), with a Production startup warning; a non-empty list that cannot be evaluated (no IP) denies.
6. Accepted: delete the configurable claim types; fixed resolution stays.
7. Confirmed for the three flags (no reader). The masking claim needs the test T-03 before the flag is deleted.
8. Accepted: no exception message in any response in any environment; detail only in logs by correlation id.
9. Accepted: `clientId` is the caller's own id and `consumed`/`totalBudget` are budget figures, not secrets. The 429 must still be returned only after authentication and must not echo other clients' data.
10. Sufficient: exception type name only, never the message, never the stack; the correlation id links to the log.
11. Follow the Iceberg REST `ErrorModel` (`{"error":{"message","type","code"}}`) on the Iceberg routes so client SDKs parse it; the message is the fixed generic text. Governed `ApiErrorCodes` map to `type`. A ProblemDetails body is not required there.
12. Accepted: unexpected exceptions become the global-handler 500 with a correlation id (fail-closed, no 400 masking of server faults).
13. Accepted: `id: null` and a GUID correlation id (`Guid.NewGuid()`, not derived from input), logged with the exception.
14. Goldens plus the per-check mutation test prove order and completeness only if the conditions SC-14-01..04 hold. A lambda-insensitive snapshot proves the decorator order only if the descriptor list includes the index and the implementation-type of decorator registrations (SC-14-05).
15. Sufficient with the conditions SC-17-01..05 (fingerprint is never logged or exposed, the cache never stores the secret).

**ARCH-MNT-06 (PR 7), binding**
- SC-01: Every wired flag fails closed: invalid value (bad CIDR, bad URI) fails startup, never silently disables the control.
- SC-02: OTLP check applies to every configured exporter endpoint (traces, metrics, logs, per-signal overrides); loopback means `localhost`, `127.0.0.0/8`, `::1` only, parsed with `Uri`/`IPAddress`, not string prefix (`http://localhost.evil.com` must fail).
- SC-03: SCIM allowlist is evaluated before the bearer secret comparison result is revealed, applies to every SCIM endpoint, uses IPv4-mapped-IPv6 normalisation, and rejection returns the same generic failure text as other failures (no "IP not allowed" oracle).
- SC-04: Guard error text and logs never contain values; the key path is sanitised (control characters stripped, each key truncated to 128 chars) to prevent log injection.
- SC-05: The guard never blocks the specific guard messages (LegacySwitchGuard, SerilogSectionGuard, removed WebSql) and runs after `DevConfiguration.Apply`; `Gateway:Insecure:*` and `Gateway:Casbin`, `Gateway:Rls`, `Gateway:Audit` security sections are bound to their types, not free-form.
- SC-06: No free-form (`object`/`IConfigurationSection`/`Dictionary<string,string>`) exemption may cover a security-relevant subtree (Insecure, Authentication, Casbin, Mcp, Audit, Consent, Tls); the schema lists each free-form subtree explicitly and the test enumerates them.
- SC-07: Deleted flags are removed from code, `unconsumed-options.txt`, appsettings, Helm values, docs and `deploy/` in the same PR (otherwise shipped config fails startup by design).
- SC-08: A baseline entry in `options-consumed-indirectly.txt` is valid only if the consuming member is read outside the defining file and is not itself dead; stale lines fail the test.
- SC-09: Backlog items FU-01..03 stay out of the PR; `ScimOptions.EnableMassChangeProtection` deletion must be noted in the plan as a known absent control (no behaviour change).
Mandatory tests (`[Trait("Category","Security")]`), ids ARCH-MNT-06-T-NN: 01 `UnknownKey_FailsStartup_InDevelopmentAndProduction`; 02 `UnknownKey_ErrorNeverContainsValue` (secret-looking key and value); 03 `McpMasking_AppliedByDefault_WithoutFlag` and `McpAuth_RequiredByDefault`; 04 `CasbinEnforcement_AppliedWithoutEnforceInQueryPipelineKey`; 05 `Otlp_PlaintextNonLoopback_FailsStart_ExceptDevOrFlag` incl. `localhost.evil.com`; 06 `Scim_AllowedIpRanges_RejectsOtherPeer_AcceptsListed`, `Scim_InvalidCidr_FailsStart`, `Scim_EmptyList_Unrestricted`, `Scim_ForwardedHeaderFromUntrustedPeer_DoesNotBypassRanges`; 07 `DeletedClaimTypeKeys_FailStartup`; 08 `FreeFormSubtrees_ExcludeSecurityRoots`; 09 `IndirectConsumers_AreReadOutsideTheDefiningFile` (plan test); plus the plan's guard tests.

**ARCH-MNT-08 (PR 8), binding**
- SC-01: No `ex.Message`, `ex.ToString()`, `ex.InnerException`, stack or exception type appears in any response body or header; the widened regex also bans `.ToString()` on exceptions in the endpoint folders.
- SC-02: Response `detail` is a fixed string per code; never interpolated with caller or exception data (including `ex.Code` from `VirtualFilterErrorCodes`, which must be validated against the governed list, unknown values map to `BAD_REQUEST`).
- SC-03: `ApiProblems.LogFailure` logs the exception with the correlation id; log templates use structured parameters, no user-controlled string concatenated.
- SC-04: Audit `DetailsJson` gets `reasonType` = exception type name only; no message.
- SC-05: Narrowed `catch` filters must not swallow `OperationCanceledException`; unmapped exceptions propagate to the global handler (500, generic).
- SC-06: DuckDB OLAP writes a problem body only while `!HasStarted`; once started, abort the connection and never append an error string to a partial stream.
- SC-07: `ACCESS_DENIED` responses are identical for "not found" and "forbidden" where today's behaviour is already existence-hiding; do not introduce a distinguishing code.
- SC-08: Iceberg and MCP error shapes follow rulings 11 and 13; MCP stdio never writes the exception to stdout.
- SC-09: The arch test baseline is empty and the test fails on a missing endpoint folder (no vacuous pass).
Mandatory tests, ids ARCH-MNT-08-T-NN: 01 `RawExceptionMessage_NeverReachesTheClient(site)` (plan test, sentinel in body, headers and `Location`); 02 `ProblemBody_HasCodeCorrelationIdAndFixedDetail`; 03 `AuditDetails_ContainTypeNotMessage` (Access x2, Rebac); 04 `PrivacyBudget429_KeepsExtensions_NoMessage`; 05 `UnexpectedException_Returns500_NotMapped400`; 06 `Cancellation_IsNotMappedToProblem`; 07 `Iceberg_ErrorsUseErrorModel_GenericMessage`; 08 `McpStdio_InternalError_IsGenericWithCorrelationId` and `McpStdio_NothingExceptJsonRpcOnStdout`; 09 `VirtualFilter_UnknownCode_MapsToBadRequest`; 10 `OlapError_AfterResponseStarted_DoesNotAppendMessage`; 11 `ArchTest_NoExceptionMessageInEndpoints_EmptyBaseline`.

**ARCH-MNT-14 and DI snapshot (PR 4), light**
- SC-01: Goldens (failures, warnings, order) are generated on unchanged code in a separate first commit; the refactor commit may not change them.
- SC-02: One golden case per throw site, each also asserting the exception type; a site without a case fails `EachCheck_IsGuardedByAGoldenCase`.
- SC-03: Fail-fast stays: the first failing check wins; the loop must not catch and aggregate, and `Warnings`/`DangerBypasses` stay computed once from options.
- SC-04: Any check that is added, removed or moved changes the order golden; the order test compares names and count.
- SC-05: The DI snapshot lists index, service type, lifetime, key and implementation (type/instance type/factory return type); authorization, audit and decorator registrations are verified in order, in both profiles; the snapshot updates only with `AUTHERIS_UPDATE_SNAPSHOTS=1` and the env var must not be set in CI (CI test fails if it is).
- SC-06: The snapshot never records option values or secrets.
Tests ARCH-MNT-14-T-NN: 01 `EachInvalidCase_FailsWithGoldenMessage`; 02 `StartupChecks_OrderIsStable`; 03 `EachCheck_IsGuardedByAGoldenCase`; 04 `Composition_DescriptorSnapshot_IsStable` (both profiles); 05 `Snapshot_UpdateFlagIsRejectedInCi`; 06 `Validator_FirstViolationWins_NoAggregation`.

**ARCH-MNT-17 (PR 6), light**
- SC-01: Cache holds the token only; the key is SHA-256 of tenant, client id and secret, and neither the secret, the fingerprint nor the token is logged or in an exception message.
- SC-02: Token is kept in memory only, stored with expiry minus a safety skew (60 s) via `TimeProvider`; an expired or failed fetch is never cached and failures are not served from a stale slot (fail closed).
- SC-03: The slot is replaced atomically; a fetch started under old options must not write into the slot after an `OnChange` clear (compare key before store).
- SC-04: Semaphore wait honours the caller's cancellation token and has no unbounded wait.
- SC-05: Mock token only when Development mock mode is active; production configuration can never reach that branch.
Tests ARCH-MNT-17-T-NN (plan tests plus): 07 `FetchStartedBeforeOptionsChange_DoesNotPopulateCache`; 08 `FailedFetch_IsNotCached`; 09 `TokenAndSecret_NeverInLogsOrExceptions`.

**ARCH-MNT-27 (PR 6), light**
- SC-27-01: `IsDnsLabel` and `IsAuthzPath` accept exactly the former regex language (same anchors, `$` newline semantics included: the old `$` accepted a trailing `\n`; the new check must reject it and the plan must record that as an intended tightening), ASCII only, no culture, no Unicode letters.
- SC-27-02: Fail closed: any input that is null, empty, over 63 (label) or over the former path bound is rejected; no exception escapes on any input.
- SC-27-03: The equivalence tests run over the regex retained in test code, with a boundary table plus a seeded random corpus (at least 10,000 strings over a hostile alphabet including `\n`, `\0`, Unicode, `%2e`, `..`, `//`), and assert the only differences are the documented trailing-newline case.
Tests ARCH-MNT-27-T-NN: 01 `IsDnsLabel_AgreesWithFormerRegex`; 02 `IsAuthzPath_AgreesWithFormerRegex`; 03 `EnvoyExport_RejectsHostileInputs_Without500`; 04 `VersionSegment_AgreesWithFormerRegex`.

**Backlog**: ARCH-MNT-06-FU-01..03 stand. No further item moved: all wired flags (OTLP, SCIM CIDR) are small and stay in PR 7.
