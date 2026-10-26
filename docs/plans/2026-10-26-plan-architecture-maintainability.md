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
