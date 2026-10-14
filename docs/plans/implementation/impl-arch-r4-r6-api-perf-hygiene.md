# Implementation Plan: Architecture Waves R4-R6 (API & Options, Hot-Path Performance, Test & Build Hygiene)

**Document ID:** `IMPL-ARCH-R4-R6`
**Parent plans:** [`../00-master-plan-overview.md`](../00-master-plan-overview.md), [`../architecture.md`](../architecture.md)
**Scope:** Themes T7 (configuration and god-class decomposition), T8 (API surface standardization), T9 (hot-path performance), T10 (engineering hygiene, test structure, tooling) with all `ARC-*` finding IDs listed there.
**Date:** 2026-10-14
**Base:** `main` @ `317e65b8` (post AST X1 cutover, security Waves A/B)
**WP prefix:** `WP-ARC-C-NN`
**Phase:** Phase 2 (Solution Architect) of the 6-phase lifecycle. Phase 3 (security audit) is mandatory for WP-ARC-C-04, -05, -06, -08, -11, -13, -14, -15, -20.

Neighbouring scopes (referenced, not planned here): Architecture R0+R2 (T2 audit contract, T5 cluster/HA, T6 eventing); Architecture R1+R3 (T1 query pipeline, T3 application ports, T4 migrations); Security Wave C (SR-L-03 / SR-L-23 error leakage, SR-L-04 role parsing, SR-D-07 `AutherisIncludeTestAuth`, SR-D-09 SDK pinning); Features.

---

## 1. Verification table (status on main)

Counts re-measured on `main` @ `317e65b8`. "Open" means the finding is still valid as described, possibly with changed numbers.

### T7 - Configuration and god-class decomposition

| ID | Status | Evidence |
|---|---|---|
| ARC-L-01 GatewayOptions god config | Open | `src/Autheris.Domain/Options/GatewayOptions.cs` is 2073 lines, 78 `class` declarations (nested option classes already exist but live in one file). 288 `IOptions<GatewayOptions>` + 8 `IOptionsMonitor<GatewayOptions>` usages in `src/`. `GatewayOptions` referenced in 78 Application, 24 Infrastructure, 54 Api, 51 GraphQL/Extensions files. |
| ARC-L-07 option validation structure | Open | `Api/Extensions/GatewayServiceCollectionExtensions.cs:94-180` still ~23 inline `.Validate(...)` lambdas incl. disk I/O (`File.Exists` `:104,110`, `Directory.Exists` `:173`); a second validator `GatewayStartupValidator.ValidateStartup` (`:669`, 871 lines); third `GatewayEmptyListConfigurationValidator` (`:182`); options bound a second time as snapshot at `:187` (`Get<GatewayOptions>()`), used for `HostOptions`/`ForwardedHeaders` and conditional registration. Only one `IValidateOptions` implementation exists in `src/`. |
| ARC-L-11 giant registration/startup classes | Open | `GatewayServiceCollectionExtensions.cs` 674 lines, `GatewayStartupValidator.cs` 871, `GatewayApplicationBuilderExtensions.cs` 504; Api still wires concrete Infrastructure types; `Plan9ServiceExtensions.cs` unchanged. |
| ARC-A-04 route god files | Open | `Endpoints/GovernanceEndpoints.cs` 1019, `WebSqlEndpoints.cs` 973, `VirtualFilterEndpoints.cs` 919, `ODataEndpoints.cs` 796, `DevPortalEndpoints.cs` 712 lines. |
| ARC-C-06 Application god classes | Open | `Governance/CasbinEnforcementService.cs` 1897, `VirtualFilters/VirtualFilterAdministrationService.cs` 969, `Policy/TableAccessPolicy.cs` 746 lines. |
| ARC-F-14 Extensions/Mcp god classes | Open (not re-measured per file; sizes from review, files unchanged in X1) | `Extensions/Dbt/DbtMetadataIngestionService.cs`, `Extensions/OpenMetadata/OpenMetadataSyncService.cs`, `Application/Mcp/Services/AiDataGuardrailService.cs`, `HitLStepUpApprovalService.cs`. |
| ARC-S-11 engine god types | Open | `TrinoSqlEngine/Ast/Builder/SqlAstBuilder.cs` 1498 lines; `AstSecurityVisitor` in 6 partial files (~1700 lines); `SqlDialectGeneratorBase*.cs` 660+480+618. |
| ARC-G-08 resolver service locators | Not re-verified in depth; treat as Open | Folded into WP-ARC-C-09 (low severity; confirm `= default!` `[Service]` parameters in `GraphQL/Types/QueryTypes.cs`/`MutationTypes.cs` before work). |
| ARC-C-11 forwarding API surface | Open | `Application/Services/GatewayExecutionService.cs:598-601` still forwards `EnsureInMemoryFilterIsEnforceable` / `FilterRows` to `InMemoryRowFilterEvaluator`; file is 693 lines, `partial`, twin constructors. |

### T8 - API surface standardization

| ID | Status | Evidence |
|---|---|---|
| ARC-A-03 no shared error contract | Open | 29 `Results.Problem`/`ProblemDetails` hits vs 218 ad-hoc `error =` shapes in `Endpoints/`, `Middleware/`, `Extensions/`; 120 bare `Results.StatusCode`/403 sites. `GatewayExceptionHandler` + `AddProblemDetails()` exist (`GatewayServiceCollectionExtensions.cs:533-537`) but handlers do not use them. |
| ARC-A-06 MCP admin/role duplication | Partially resolved | Hard-coded admin strings are gone: `Mcp/GatewayMcpServer.cs:67-73` now calls `McpAdminTools.CanUseAdminTools` (SR-H-02 fix). Still open: `GatewayMcpServer.Caller` calls `McpEndpoints.ResolveCaller` (`GatewayMcpServer.cs:432`, `McpEndpoints.cs:187`), and `EndpointSecurity` keeps separate admin predicates. |
| ARC-A-07 route conventions | Open | Mixed `/api/v1/...` and `/api/...`; only 7 `MapGroup` in `Endpoints/`; SQL registered under several aliases (`WebSqlEndpoints.cs`, `SqlEndpointRoutes.cs`). |
| ARC-A-08 imperative authorization | Open | 42 `HasRole`/`HasAnyRole` calls in Endpoints/Mcp/Middleware; 143 `RequireAuthorization(` with 0 named policies; `Security/GatewayPolicies.cs` defines constants (`:19-24`) and `AddPolicy` (`:94`) only. |
| ARC-A-09 inline pipeline lambdas | Open | 9 `app.Use(async ...)` in `Extensions/GatewayApplicationBuilderExtensions.cs`. |
| ARC-A-10 domain-scope rewrite after pipeline | Open | `GatewayApplicationBuilderExtensions.cs:370-379` rewrites `/graphql/{domain}` in `MapGatewayEndpoints`, after `UseGatewayPipeline` (`ReadOnlyTokenMiddleware`, `FinOpsBudgetMiddleware` at `:306-313`). |
| ARC-A-13 service locator in handlers | Open | 37 `RequestServices.GetService*`/`context.Services` hits in `Autheris.Api`. |
| ARC-A-14 inline OpenAPI/HTML | Open | HTML/JS in `Endpoints/DevPortalEndpoints.cs` and `Endpoints/ODataEndpoints.cs`. |
| ARC-A-15 OData option parsing duplication | Open | `Endpoints/ODataEndpoints.cs` (796 lines) unchanged in structure. |
| ARC-A-16 missing CancellationToken | Open | 26 `WriteAsJsonAsync(` calls in `Autheris.Api` without a visible token (75 total). |
| ARC-A-17 static handler seams | Open | `EndpointSecurity` still `internal static class` (`Endpoints/EndpointSecurity.cs:19`); `InternalsVisibleTo` Unit+Integration in `Autheris.Api.csproj:44-45`. |
| ARC-A-18 manual endpoint list | Open | 36 `Map*Endpoints/Routes(` calls in `GatewayApplicationBuilderExtensions.cs`. |
| ARC-A-19 MCP base path twice | Open | `Endpoints/McpEndpoints.cs:51-53` (`TrimEnd('/')`) vs `GatewayApplicationBuilderExtensions.cs:331` `ResolveMcpBasePath` (also reused by `ParquetGraphQLResponseMiddleware.cs:53`). |

### T9 - Hot-path performance

| ID | Status | Evidence |
|---|---|---|
| ARC-S-03 thread per parse | Open | `TrinoSqlEngine/FastSqlEngine.cs:197-240` `RunWithParseBudget` creates `new Thread(..., ParseThreadStackSize)` on every parse; 16 MB default (`:36`); static `SharedParseConcurrencyLimiter` (`:48`). |
| ARC-S-04 `new FastSqlEngine()` per call | Partially resolved | `AstSecurityVisitor` no longer instantiates the engine (no match). Still open: `Api/Endpoints/GovernanceEndpoints.cs:705`, `Application/Policy/Lint/PolicyParserSetup.cs:19`, `Application/VirtualFilters/SqlFilterCompiler.cs:73,172,453`, `StructuredFilterSqlBuilder.cs:36` (`engine ?? new FastSqlEngine()`). |
| ARC-S-09 syntax errors parsed twice | Open | `FastSqlEngine.cs:337-350` any `ParseCanceledException` from SLL triggers a full LL reparse. |
| ARC-S-13 cache key | Partially resolved | Hashing moved outside the lock and reflection memoised (commits `329ea5e0`, `bfb7738f`). Still open: global `lock (_gate)` for Find/CountHit/CountMiss/Add/evict (`Ast/Emit/CompiledSqlTemplate.cs:124,164,177,182,189`), FIFO eviction, key material proportional to SQL size (`GovernedSqlCompiler.cs:143-149`). |
| ARC-S-15 reflection AST walk | Open | `Ast/Security/AstReflection.cs` used by `DialectCapabilityValidator`, `DmlMaskedReadGuard`, `InsertCheckPolicy`, `AstSecurityVisitor.TypedDml/Tables`, `SecurityCoverageVerifier`. |
| ARC-A-11 per-request catalog load | Open | `Endpoints/ODataEndpoints.cs:344` and `Endpoints/DuckDbOlapEndpoints.cs:180` call `GetAllTablesAsync` per request. |
| ARC-A-12 buffered egress | Open | `Endpoints/ArrowExportEndpoints.cs:185` `ExecuteQueryBufferedAsync`; Parquet/extensibility buffering middlewares unchanged. |
| ARC-I-06 fake streaming | Open | `Infrastructure/Connectors/SqlConnector.cs:131-141` `ReadSplitAsync` awaits the full `ReadBatchAsync` and then yields; `Application/Services/SqlDataSourceExecutor.cs` returns `IReadOnlyList<IReadOnlyDictionary<...>>`. |
| ARC-G-02 N+1 catalog change detection | Open | `GraphQL/Catalog/CatalogSchemaModel.cs:198-206` per-table `GetRelationsForTableAsync`; `CatalogGraphQlTypeModule.cs:68` rebuilds the whole model to compute a fingerprint. No `ICatalogVersionProvider`. |
| ARC-G-06 per-request resolver overhead | Open (low) | Not re-measured; unchanged files. |
| ARC-C-14 synchronous audit on hot path | Open, owned by T2 | 5 awaited `RecordAuditEventAsync` in `Application/Services/GatewayExecutionService.cs`. Fix is the Tier-A/Tier-B audit contract (Architecture R0+R2). Not planned here. |
| ARC-I-10 blocking startup I/O | Partially open | Redis/Garnet: sync `ConnectionMultiplexer.Connect` in singleton factories (`Api/Extensions/DependencyInjection/GatewayStorageServiceExtensions.cs:67,81`) plus `StartServer()` at `:56` and `Program.cs:79`. Repository-constructor DDL belongs to T4 (migration runner). |

### T10 - Engineering hygiene, tests, tooling

| ID | Status | Evidence |
|---|---|---|
| ARC-T-01 monolithic Unit project | Open (worse) | `tests/Autheris.Tests.Unit`: 471 files, 106,448 LOC, references all 7 src projects (`Autheris.Tests.Unit.csproj:26-32`). Other projects: TrinoSqlEngine.Tests 128 files/25k LOC, Integration 85/19k, Gate 33/4.5k, Extensions.Tests 18/6.8k, Architecture 10/1.7k. |
| ARC-T-02 duplicated test doubles | Open | 8x `class StubEpochValidationService`, 7x `class TestOptionsMonitor`. `tests/Autheris.Tests.Unit/Common/` holds only `TableIdentifierConsistencyTests.cs`; no shared `Tests.Common` project. |
| ARC-T-03 wall clock / sleeps | Open | `Task.Delay` 50 lines, `DateTime(Offset).(Utc)Now` 547 lines in tests; `src/` has 322 `UtcNow` call sites vs 16 files using `TimeProvider`; `FakeTimeProvider` appears in only 2 TrinoSqlEngine test files (no `Microsoft.Extensions.TimeProvider.Testing` in the Unit project). `Thread.Sleep` is gone from Unit (0 hits). |
| ARC-T-04 FS/loopback, no categories | Partially resolved | `[Trait("Category", ...)]` now used 119 times; Unit/Integration still not systematically categorised, temp-path and localhost usage unchanged. |
| ARC-T-05 oversized/date-named test files | Open | 22 files named `*SecurityReview*`/`*Remediation*`; largest files unchanged. |
| ARC-T-06 legacy path under test | Mostly resolved | Legacy differential and plan-cache tests removed (`fe352e29`); `RewriteRls`/`RlsListener` no longer exist in `src/`. 7 test files still reference the names (`tests/TrinoSqlEngine.Tests/{AnalysisAndMaskingTests,SecurityReview20261002SqTests,SecurityRemediationTests,TrinoParserComplianceTests}.cs`, `Unit/Security/{CorrelatedRowFilterAliasTests,SecurityReviewRemediationCoverageTests}.cs`, `SqlCompilerGate/Harness/FrozenLegacy.cs`). Residual cleanup only; folded into WP-ARC-C-19. |
| ARC-T-07 serialized suites | Open | `tests/Autheris.Tests.Integration/xunit.runner.json:5` `maxParallelThreads: 1`; Gate project serialized. |
| ARC-T-08 architecture tests thin | Open | 10 files / 1.7k LOC in `tests/Autheris.Tests.Architecture`; `tests/Autheris.Tests.Unit/ArchitectureAndCleanCodeTests.cs` still in Unit. |
| ARC-L-08 arch test gaps | Open | `ArchitectureTests.cs` has no HotChocolate / Prometheus / `Microsoft.Data` / `GatewayOptions` / `IServiceProvider` rule (grep on the file returns none). The CI `dotnet test Autheris.sln -c Release` runs them. |
| ARC-L-09 arc42 stale | Not verified line by line; treat as Open | `docs/architecture/arc42.md:29,68` still mention Hot Chocolate isolation; verify numbers at implementation time. |
| ARC-L-10 two solution files | Open | `Autheris.sln` and `Autheris.slnx` both present; slnx is 21 lines and lacks `SqlCompilerGate` (`grep` shows only the Architecture project). CI uses `Autheris.sln` (`.github/workflows/ci.yml:29,38`). |
| ARC-L-13 plan/doc governance | Partially resolved | Plans consolidated (`d8db81cc`); stale German overview is gone. Still open: `doc -> docs` symlink at repo root, both `docs/plan` and `docs/plans` listing the same files, `docs/architecture/konzept-data-governance-und-mcp-documentation.md` (German filename). |
| ARC-L-14 stray artifacts | Open (local only) | `fix4.tgz`, `fix4b.tgz`, `libpod/`, `dist/` at repo root; git-ignored, nothing tracked. |
| ARC-L-15 process-tag naming | Open | `Api/Extensions/Plan9ServiceExtensions.cs` (`AddPlan9Services`, `Program.cs:51`). |
| ARC-L-17 CA suppressions | Open (suppression half only) | `Directory.Build.props:16` NoWarn lists 24 CA/CS codes incl. correctness-relevant `CA1816`, `CA2012`. `AutherisIncludeTestAuth` default `true` (`:17`) is owned by SR-D-07; release/docker-publish workflows already override to `false`. |
| ARC-S-06 `#if DEBUG` seams | Open, mitigated | 32 `#if DEBUG` blocks across 20 production files: 24 in `TrinoSqlEngine` (`CompilerTestSeams.cs`, `GovernedSqlCompiler.cs`, `Ast/Emit/*`, `Ast/Security/TenantPredicateFactory.cs`, ...), plus `Application/Security/TenantRegistryGate.cs:81`, `Application/Sql/Errors/SqlCompilerErrorMapper.cs:43`, `Infrastructure/Persistence/SessionPinning.cs:53`, `Infrastructure/Persistence/Catalog/GovernedCatalogProvider.cs:118`. Mitigations already present: `tests/TrinoSqlEngine.Tests/Security/ReleaseHookAbsenceTests.cs`, CI runs the Gate in Debug and Release (`ci.yml:71-79`). |
| ARC-S-10 engine not standalone | Open | `TrinoSqlEngine.csproj:36-40` `InternalsVisibleTo Autheris.Tests.Unit` (always) and Application/Infrastructure (Debug only); PackageId `FastSqlEngine`. |
| ARC-S-12 `IReadOnlyList` in AST records | Open | `Ast/Nodes/Clauses.cs:5,35-42` and siblings. |
| ARC-S-14 `TrustedSqlExpression` | Already resolved | No occurrence in `src/` or tests except the guard `tests/Autheris.Tests.Architecture/CutoverArchitectureTests.cs:148-162` (`NoTrustedSqlExpression`). Evidence: AST X1 cutover. |
| ARC-S-16 build tooling / stale identity | Open | `TrinoSqlEngine.csproj:6-10` PackageId/Product `FastSqlEngine`, German comment `:13`, `LangVersion 14` (`:5`) vs C# 13 guidance in CLAUDE.md; German comments in `FastSqlEngine.cs:332,339`. |
| ARC-I-09 dual telemetry | Open | 7 prometheus-net static metric declarations remain in `src/` (down from 27 in the review; exact list to be re-grepped); no `IMeterFactory`, no OTel Prometheus exporter. |
| ARC-I-11 diagnostics facade | Open | `Infrastructure/Diagnostics/GatewayDiagnostics.cs` still exists next to the Domain class. |
| ARC-G-04 GraphQL pipeline in Api | Open | No `AddAutherisGraphQL` in `src/` (grep empty); pipeline still in `Api/Extensions/GatewayServiceCollectionExtensions.cs` (`AddGatewayGraphQL`). |
| ARC-G-05 dead/demo GraphQL code | Open | `GraphQL/DynamicTypes/DynamicTableType.cs` and `GraphQL/Loaders/InvoiceItemDataLoader.cs` present. |
| ARC-F-12 ad-hoc HttpClient | Open | `Application/Catalog/Services/CatalogDiscoveryService.cs:317-323` builds handler + `new HttpClient` per call. |
| ARC-F-13 ITSM split across projects | Open | Unchanged layout (`Extensions/Itsm`, `Application/Workflows/ItsmWorkflowDispatcher.cs`, `Infrastructure/Itsm/*HostedService.cs`). |
| ARC-F-18 hosted service patterns | Open | Three loop styles remain (`PeriodicTimer` in `Extensions/OpenMetadata/OpenMetadataSyncBackgroundService.cs`, `Task.Delay` loops elsewhere). |
| ARC-C-19 German comments / magic constants | Open | `Application/Services/GatewayExecutionService.cs:524` German comment; magic defaults as listed in the review. |

Items with no work in this plan: ARC-S-14 (resolved), ARC-C-14 (owned by T2, referenced), ARC-I-10 DDL half (owned by T4).

---

## 2. Work packages

Effort scale: S (<= 1 day), M (2-3 days), L (4-6 days), XL (> 6 days, split into sub-batches). Every WP follows TDD (red test first), then the Phase 3 security review where marked, and uses `dotnet build Autheris.sln` + `dotnet test` (per project) plus a Release build with warnings as errors before push. Parallel developer agents implement only; the lead compiles once at the end of a batch.

### Wave R4 - Structure and API decoupling

#### WP-ARC-C-01 GatewayOptions physical split and consumption ratchet
- **Covers:** ARC-L-01 (phase 1).
- **Goal:** Make the option classes individually addressable and stop the growth of `IOptions<GatewayOptions>` usage without changing behaviour.
- **Design:**
  1. Move each of the 77 nested option classes out of `src/Autheris.Domain/Options/GatewayOptions.cs` into `src/Autheris.Domain/Options/<Area>/<Name>Options.cs` (Authentication, Caching, Audit, Mcp, GraphQL, DataSources, Catalog, Lakehouse, WebSql, Federation, ...), keeping namespace `Autheris.Domain.Options` so no using changes. `GatewayOptions` keeps only the root properties and `SectionName`. Pure move; `git mv`-style diffs reviewed with `--color-moved`.
  2. Add `Autheris.Domain.Options.GatewaySections` (static, constants for every section path: `"Gateway:Mcp"`, `"Gateway:GraphQL"`, ...) so binding keys are not magic strings.
  3. Add `GatewayOptionsServiceCollectionExtensions.AddGatewaySection<TOptions>(string path)` in `src/Autheris.Api/Extensions/` registering `AddOptions<TOptions>().Bind(configuration.GetSection(path)).ValidateOnStart()`. Because `DevConfiguration.Apply` mutates the `IConfiguration` before binding (`Program.cs:52-54`), section bindings see the same dev-preset values as the root binding; no `PostConfigure<GatewayOptions>` exists in `src/` (verified), so there is no ordering drift.
  4. Architecture test `GatewayOptionsConsumptionRatchetTests` in `tests/Autheris.Tests.Architecture`: asserts the number of types with a constructor parameter or field of type `IOptions<GatewayOptions>`/`IOptionsMonitor<GatewayOptions>`/`GatewayOptions` is `<= N` (N written into the test, lowered by each WP-03 batch). New classes may not take `GatewayOptions` (allowlist: composition roots, validators).
- **Affected files:** `GatewayOptions.cs` (split), new files under `Options/<Area>/`, `GatewayServiceCollectionExtensions.cs`, new ratchet test. Hot file: `GatewayOptions.cs` is touched by every feature branch that adds a setting.
- **Migration/compat:** No config-key changes, no binary changes for consumers (same namespace/assembly). Source-compatible. Merge conflicts for open branches editing `GatewayOptions.cs`: announce a freeze window and merge this WP first thing, when few branches are open (see sequence).
- **Security:** None directly. Keep `[Required]`/DataAnnotations attributes on each class during the move so `ValidateDataAnnotations` still covers them (test below).
- **TDD:** `tests/Autheris.Tests.Unit/Options/GatewayOptionsBindingParityTests`: bind a fixture `appsettings` containing every section; red test asserts that `IOptions<GatewayOptions>` and `IOptions<McpOptions>` (via `AddGatewaySection`) yield equal values for each section (written first against the not-yet-existing helper). Plus `GatewayOptionsConsumptionRatchetTests` (red until baseline N is set).
- **Acceptance:** `GatewayOptions.cs` < 300 lines; no behaviour change (full Unit + Integration green); ratchet test present with baseline 288/8.
- **Effort:** M. **Risk:** Low (mechanical), high merge-conflict exposure.

#### WP-ARC-C-02 Options validation consolidation
- **Covers:** ARC-L-07.
- **Goal:** One validation mechanism, no disk I/O in option predicates, no second binding.
- **Design:**
  1. Replace the ~23 `.Validate(lambda)` in `GatewayServiceCollectionExtensions.cs:94-180` with `IValidateOptions<T>` classes under `src/Autheris.Api/Configuration/Validation/` (`CasbinOptionsValidator`, `AuthenticationOptionsValidator`, `HighAvailabilityOptionsValidator`, ...), each with a stable failure message used by existing tests (messages are asserted in current tests; keep text).
  2. File-system checks (`Casbin.ModelPath`, `Casbin.PolicyPath`, `Plugins.Directory`) move behind `IFileSystemProbe` (new, `Autheris.Application` port or Api-internal) so validators are pure and testable; the real probe is registered in the composition root. Validation still runs under `ValidateOnStart()` so a missing file still fails startup (fail-closed unchanged).
  3. Fold `GatewayStartupValidator.ValidateStartup` rule groups into the same `IValidateOptions` classes where they only need the options object; rules needing `IHostEnvironment`/environment variables become validators taking those via constructor. `GatewayStartupValidator` stays only for cross-cutting pre-bind checks (`ValidateSqlRewriterEngine`, `ValidateRemovedWebSqlSettings`, legacy keys) that operate on raw `IConfiguration`.
  4. Eliminate the second binding (`Get<GatewayOptions>()` at `:187`): `AddGatewayOptions` still returns a snapshot (needed for conditional registration before the container exists), but it is produced by the same `Configure` pipeline (`new GatewayOptions()` + `configuration.GetSection(...).Bind(...)`) through one private method used by both the options builder and the snapshot, and a unit test asserts snapshot equals `IOptions` value.
  5. Per-section validators target `IOptions<McpOptions>` etc. as WP-03 migrates consumers; until then they validate the corresponding sub-object of `GatewayOptions`.
- **Affected files:** `Api/Extensions/GatewayServiceCollectionExtensions.cs`, `Api/Extensions/DependencyInjection/GatewayStartupValidator.cs`, `GatewayEmptyListConfigurationValidator.cs`, new `Api/Configuration/Validation/*`.
- **Migration/compat:** Startup failures keep message and exception type (`OptionsValidationException`). Config unchanged.
- **Security (fail-closed):** Every validator that exists today must continue to fail startup on the same input; a regression table test (below) is mandatory. Phase 3 review of the rule inventory (no rule silently dropped).
- **TDD:** `tests/Autheris.Tests.Unit/Options/GatewayOptionsValidationRegressionTests`: a theory with one row per existing lambda/`ValidateStartup` rule (invalid input -> expected failure message), written first against current behaviour (green), then kept green through the refactor. Red test added first: `OptionsValidatorsDoNoDiskIoTests` (validators constructed with a throwing `IFileSystemProbe` must not call it for disabled features).
- **Acceptance:** zero `.Validate(` lambdas in `GatewayServiceCollectionExtensions`; all rules covered by the regression theory; snapshot/`IOptions` parity test green.
- **Effort:** M-L. **Risk:** Medium (silent loss of a rule). Mitigation: rule inventory table generated before the refactor.

#### WP-ARC-C-03 Options consumer migration by area
- **Covers:** ARC-L-01 (phase 2), supports ARC-A-13.
- **Goal:** Services depend on the narrow options they use (`IOptions<McpOptions>`, `IOptionsMonitor<CasbinOptions>`) instead of the root.
- **Design:** Migrate in vertical batches, one area per PR, each batch lowering the ratchet baseline from WP-01. Order by consumer count and independence: (1) `Mcp` + `Casbin`, (2) `GraphQL` + `RateLimiting`, (3) `Audit` + `DataMasking`, (4) `Caching`/`Redis`/`Garnet`, (5) `DataSources`/`Catalog`/`Lakehouse`, (6) `WebSql`/`SqlEndpoints`/`ProcedureEndpoints`/`CrossSource`, (7) `Authentication` (last: widest blast radius, touches the security pipeline), (8) remaining. For each area: register the section (`AddGatewaySection`), replace constructor injection, update tests to `Options.Create(new McpOptions{...})`. Where a service needs several sections it takes several `IOptions<T>` (a sign it needs splitting, noted in the PR, not fixed here). Raw `GatewayOptions` fields held by services (`GovernedDataQueryService.cs:47`, `TableAccessPolicy.cs:98`, `McpProtocolHandler.cs:28`, `AuditLineageEgressInterceptor.cs:11`) are migrated in the matching batch. Hot-reload semantics: services that currently use `IOptionsMonitor<GatewayOptions>` use `IOptionsMonitor<SectionOptions>`; sections bound through `Bind(IConfigurationSection)` reload with the config provider, same as before.
- **Affected files:** about 288 injection sites across Application/Infrastructure/Api/GraphQL/Extensions plus their tests.
- **Migration/compat:** Internal-only; no config or API change. Plugin contract: plugins that read `GatewayOptions` through DI keep working while the root registration stays (do not remove `IOptions<GatewayOptions>` registration; the ratchet only bounds consumers).
- **Security:** Authentication/Authorization batch (7) is a Phase 3 review: confirm no auth option is read from a default-constructed object (a missing registration would yield defaults, i.e. potentially weaker settings). Mitigation: `ValidateOnStart()` for every section plus `GatewayOptionsBindingParityTests` rows per section; container `ValidateOnBuild=true` (already on) catches missing registrations.
- **TDD:** Per batch, update the unit tests of migrated services first (they will not compile against the old constructor = red), then change the constructors. Ratchet test baseline lowered per batch.
- **Acceptance:** `IOptions<GatewayOptions>` consumers <= 20 (composition roots, validators, diagnostics) at end; ratchet enforces it.
- **Effort:** XL (8 batches of S-M each; batches are independent after WP-01 and can run in parallel per area when files do not overlap, but each batch must merge before the next touches shared test files).
- **Risk:** Medium (test churn, merge conflicts). Do not run concurrently with WP-ARC-C-19 (test project split); see sequence.

#### WP-ARC-C-04 Pipeline and composition-root hygiene
- **Covers:** ARC-A-09, ARC-A-10, ARC-A-18, ARC-A-19, ARC-L-11, ARC-L-15.
- **Goal:** Named, testable middleware; security-relevant path rewriting before the security pipeline; no process-tag names; shrink registration classes.
- **Design:**
  1. Extract the 9 inline lambdas of `UseGatewayPipeline` into classes in `Api/Middleware/` (`EdgeHeaderStrippingMiddleware`, `GatewayCorsCsrfMiddleware`, `McpContentTypeGuardMiddleware`, ...). Behaviour identical; each gets unit tests with `DefaultHttpContext`. Pipeline order is asserted by a `PipelineOrderTests` test that builds the pipeline and checks the sequence of middleware types (security ordering becomes a regression test).
  2. ARC-A-10: move the `/graphql/{domain}` rewrite into a `DomainScopeRewriteMiddleware` registered before `AccessAuditMiddleware`, `ReadOnlyTokenMiddleware`, `SchemaContractMiddleware`, `FinOpsBudgetMiddleware` so that every path-inspecting middleware sees the canonical `/graphql` path and the audit event carries the domain scope. Domain value validation (`subPath` must match the allowed domain pattern, otherwise 404) is performed in the middleware (fail-closed: unknown/invalid domain does not rewrite).
  3. ARC-A-19: one `McpPathResolver.Resolve(GatewayOptions)` (new, `Api/Mcp/`), used by `McpEndpoints.cs:51`, `GatewayApplicationBuilderExtensions.cs:122,308`, `ParquetGraphQLResponseMiddleware.cs:53`; normalisation: leading slash, no trailing slash, fallback `/mcp`.
  4. ARC-A-18: introduce `IEndpointModule { void Map(IEndpointRouteBuilder app); }` with constructor-free modules discovered from a single explicit list (explicit, not assembly scanning, to keep removal of an endpoint visible in review); uniform signature replaces `(app)`, `(app, gatewayOptions)`, `(app, gatewayOptions, env)`; modules receive `IOptions` from `app.ServiceProvider`. Migrate the 36 calls module by module, behaviour unchanged. Rename `*EndpointRoutes` to `*Endpoints` consistently.
  5. ARC-L-15: rename `AddPlan9Services` to `AddAsyncJobs` + `AddDatasourceTesting` (split by capability), drop `AP-9.x` tags from comments, fix `Program.cs` step numbering.
  6. ARC-L-11 (Api side only): collapse the three near-identical provider blocks in `GatewayGovernanceServiceExtensions.cs:37-95` into a loop over a (dialect -> repository registration) table; replace manual `new` factories in `GatewayExecutionServiceExtensions.cs:79-97,113-116` with plain registrations. The per-project `AddAutherisApplication()/AddAutherisInfrastructure()` extraction is deliberately deferred until Architecture R3 (T3) settles the Application/Infrastructure port boundary; this WP only prepares call sites.
- **Affected files:** `Api/Extensions/GatewayApplicationBuilderExtensions.cs`, `Api/Middleware/*` (new), `Api/Endpoints/McpEndpoints.cs`, `Api/Extensions/Plan9ServiceExtensions.cs`, `Program.cs`, `GatewayGovernanceServiceExtensions.cs`, `GatewayExecutionServiceExtensions.cs`.
- **Migration/compat:** No wire change except audit records for `/graphql/{domain}` now log the canonical path plus `DomainScope` (document in release notes). Extension method names change (`AddPlan9Services`) - internal to the host, but referenced by `Program.cs` and possibly integration tests (grep before rename).
- **Security:** Phase 3 mandatory for item 2 and the CORS/CSRF extraction (item 1): the CSRF/CORS middleware must be byte-for-byte the same decision logic; keep the existing tests and add a differential table test (old lambda vs new class on a request matrix) before deleting the lambda. For item 2 add a test that a `ReadOnlyTokenMiddleware` rule on `/graphql` blocks `/graphql/finance` writes (today this ordering is a latent gap).
- **TDD:** red tests first: `DomainScopeRewriteMiddlewareTests.RewritesBeforeAuditAndReadOnlyGuard`, `PipelineOrderTests`, `McpPathResolverTests` (matrix of `"mcp"`, `"/mcp/"`, `""`, `"/"`).
- **Acceptance:** `UseGatewayPipeline` contains no inline `Use(async ...)`; one MCP path function; `rg "Plan9"` empty in `src/`; pipeline order test green.
- **Effort:** L. **Risk:** Medium (security middleware ordering). **Serialization:** touches `GatewayApplicationBuilderExtensions.cs`, a hot file; do not run in parallel with WP-05, -06, -07, -08, -16.

#### WP-ARC-C-05 RFC 7807 ProblemDetails contract and error-code catalog
- **Covers:** ARC-A-03, ARC-A-15 (error envelope), ARC-A-16 (cancellation tokens on the touched writes).
- **Goal:** One error shape for every HTTP endpoint with stable machine-readable codes.
- **Design:**
  1. New `Autheris.Api.Errors.ApiProblems` static factory returning `IResult` (`application/problem+json`): `Forbidden(code)`, `NotFound(code)`, `BadRequest(code, errors)`, `Unauthorized`, `TooManyRequests(retryAfter)`, `Conflict`, `Unprocessable`. Each sets `type = https://docs.autheris.dev/errors/<code>` (URI constant), `title`, `status`, `extensions["code"]` (stable string from `ApiErrorCodes`, a `const string` catalog in `Autheris.Domain` or Api), and `traceId`. `detail` is always a fixed generic text from the catalog; no exception messages (leakage rules owned by SR-L-03 / SR-L-23, which define what `detail` may contain; this WP supplies the single place to enforce them).
  2. Replace the 218 `{ error = ... }` shapes and 120 bodiless 403/`StatusCode` returns endpoint file by endpoint, with `Results.Problem`-compatible output. Bodiless 403s become `ApiProblems.Forbidden(ApiErrorCodes.AccessDenied)` (status unchanged, body added).
  3. OData endpoints keep the OData envelope (`{"error":{"code","message"}}` with `odata.metadata=minimal`) because clients depend on it; centralise it in one `ODataError(...)` (already exists at `ODataEndpoints.cs:608`) and remove the three copies at `:402-433` (ARC-A-15). Document this as the one sanctioned exception.
  4. WebSQL/Trino statement protocol (`/v1/statement`) must keep Trino JSON error format; sanctioned exception, mapped in `WebSqlEndpoints` error mapper (`SqlErrorResponse.cs`).
  5. MCP errors remain JSON-RPC errors; sanctioned exception.
  6. `GatewayExceptionHandler`/`GatewayDenialStatusMapper` become the only translation of `GatewaySecurityException` and are called by handlers that currently catch-and-map manually.
  7. Architecture test `ErrorContractTests`: no new `error =` anonymous object in `Autheris.Api/Endpoints` outside the allowlist files (OData, WebSQL/Trino, MCP); ratchet number for existing ones lowered per batch.
- **Migration/compat (breaking):** Clients parsing `{ "error": "..." }` break. Mitigation: for one release emit ProblemDetails with an additional extension member `error` carrying the same legacy string, controlled by `Gateway:Api:LegacyErrorField` (default `true` in the first release, flipped to `false` the next, then removed). Status codes do not change. Add to release notes and `docs/` API reference.
- **Security:** Fail-closed: error bodies never include exception text or internal identifiers; `ProblemDetails.Extensions` whitelisted keys only. Phase 3 review with the SR-L-03/SR-L-23 owner.
- **TDD:** `ApiProblemsTests` (shape, content type, code, no detail leakage given an exception message with a secret), then per-endpoint-group tests asserting `application/problem+json` and `code` (red against the current `{error}`), starting with `GovernanceEndpoints`, `VirtualFilterEndpoints`, `DuckDbOlapEndpoints`, `ArrowExportEndpoints`, then the rest. Integration test: unauthenticated and forbidden calls to a sample of 10 routes return problem+json.
- **Acceptance:** `rg 'new\s*\{\s*error\s*='` in `Autheris.Api/Endpoints` matches only allowlisted files; every non-2xx from `Autheris.Api` (except sanctioned protocols) is `application/problem+json` with `code`.
- **Effort:** L-XL (many files, mechanical). **Risk:** Medium (client compatibility). **Serialization:** touches nearly every endpoint file; conflicts with WP-06, -07, -08. Run batches per endpoint file group, serialized against those WPs (see sequence); batches in disjoint files may be parallelised.

#### WP-ARC-C-06 Declarative authorization policies and MCP caller placement
- **Covers:** ARC-A-08, ARC-A-06 (remaining part).
- **Goal:** Authorization stated at route registration through named policies; handlers no longer call `HasRole`.
- **Design:**
  1. Extend `Api/Security/GatewayPolicies.cs` with a policy per role requirement actually used by the 42 `HasRole/HasAnyRole` calls (GovernanceAdmin, ClusterAdmin, TenantAdmin, Approver, PrivacyAdmin, SchemaAdmin, SchemaPublisher already exist as constants; add the missing combinations as named policies, e.g. `GovernanceOrClusterAdmin`). Policies use a requirement + `AuthorizationHandler` that call the single role accessor owned by Security Wave C (SR-L-04: canonical role parsing). This WP must not re-implement role parsing.
  2. Replace `RequireAuthorization()` on admin groups with `RequireAuthorization(GatewayPolicies.X)` at the `MapGroup` level; delete in-handler role checks only after a test proves the policy rejects the same callers.
  3. `EndpointSecurity` (static, `Endpoints/EndpointSecurity.cs`) keeps tenant/caller helpers but its admin predicates are replaced by the policy handlers; MCP: introduce `IMcpCallerResolver` (`Api/Mcp/McpCallerResolver.cs`) used by both `McpEndpoints` and `GatewayMcpServer`, removing the dependency of `GatewayMcpServer.cs:432` on `McpEndpoints.ResolveCaller`.
  4. Authorization-matrix test `EndpointAuthorizationMatrixTests` (Integration): enumerates `EndpointDataSource`, and asserts every endpoint has either a named policy, `RequireAuthorization()` explicitly on an allowlist of "any authenticated" routes, or is in the `AllowAnonymous` allowlist (health, well-known, dev endpoints only when dev preset). New endpoints without a decision fail the test. `DevEndpoints` group-level `.AllowAnonymous()` (`DevEndpoints.cs:30`) is checked against the dev-only registration.
- **Migration/compat:** HTTP behaviour stays 401/403; the 403 body now comes from the authorization middleware result handler (customise via `IAuthorizationMiddlewareResultHandler` to emit ProblemDetails, aligned with WP-05).
- **Security (fail-closed):** Policy defaults deny; a missing policy name throws at startup (ASP.NET behaviour for unknown policy at request time is an exception, so a startup test enumerating policy names used by endpoints against registered policies is required). Phase 3 mandatory. Audit events for denied access must still be written (today the handler writes the denial audit; the result handler must keep emitting `AccessDenied` audit events, tested).
- **TDD:** red first: `EndpointAuthorizationMatrixTests` (fails for the 42 imperative-check endpoints because they carry no policy metadata), then per-policy `GovernancePoliciesTests` with claims fixtures.
- **Acceptance:** `rg "HasRole|HasAnyRole" src/Autheris.Api/Endpoints` returns only the policy handlers; matrix test green.
- **Effort:** L. **Risk:** Medium-High (authorization regression). **Dependency:** SR-L-04 must land first or in the same release; otherwise two role parsers coexist.

#### WP-ARC-C-07 Endpoint decomposition, embedded assets, service-locator removal
- **Covers:** ARC-A-04, ARC-A-13, ARC-A-14, ARC-A-15, ARC-A-16, ARC-A-17.
- **Goal:** No endpoint file over ~400 lines; handlers receive dependencies by parameter binding; static HTML/JS out of C# strings.
- **Design:**
  1. Split by feature under feature folders with one `MapGroup` per resource: `Endpoints/WebSql/` (`WebSqlEndpoints`, `TrinoStatementEndpoints`, `WebSqlOpenApiSpec`, `WebSqlErrorMapper`, `JsonRowWriter`), `Endpoints/Governance/` (ingestion, simulation, sunsetting, differential privacy as separate modules), `Endpoints/VirtualFilters/`, `Endpoints/OData/` (`ODataEntitySetHandler`, `ODataCountHandler`, shared `ODataQueryOptions` parser used by both, replacing the duplicated parsing and `headers.ToDictionary` copies at `:480,:532`), `Endpoints/DevPortal/`.
  2. ARC-A-14: move the Swagger UI HTML (`ODataEndpoints.cs:711-796`), DevPortal templates (`DevPortalEndpoints.cs:223-703`) and hand-built OpenAPI JSON (`WebSqlEndpoints.cs:610-755`) to embedded resources under `src/Autheris.Api/Resources/` with an `EmbeddedResourceReader`; keep generated text byte-identical (golden tests compare rendered output before/after).
  3. ARC-A-13: bind services in handler signatures (`IOptions<ArrowExportOptions> o, ILoggerFactory lf`), delete `RequestServices.GetService` calls (37 sites); for middleware, constructor/`InvokeAsync` injection. `SecurityContextResolutionMiddleware.cs` gets its optional services through its constructor (null-object when feature disabled), not per-request `GetService`. Add `ServiceLocatorArchitectureTests`: no `GetService`/`GetRequiredService` on `HttpContext.RequestServices` in `Autheris.Api` except an allowlist.
  4. ARC-A-16: bind `CancellationToken ct` in handlers and pass it to the 26 `WriteAsJsonAsync` calls without a token; no behaviour change.
  5. ARC-A-17: convert `EndpointSecurity` statics to an injected `IEndpointCallerContext` only where handlers are being moved anyway; keep `InternalsVisibleTo` (tests are split in WP-19, which reduces its reach).
- **Migration/compat:** None on the wire. Golden-text tests guard embedded assets.
- **Security:** Mechanical but sits on security code (denials, tenant resolution); Phase 3 sampling review of moved handlers; every moved handler keeps its existing tests unchanged (no test edits other than namespace).
- **TDD:** golden tests for assets first (green, then relocated); `ServiceLocatorArchitectureTests` red first; file-length architecture test (see WP-21) lowered progressively.
- **Acceptance:** no file in `Autheris.Api/Endpoints` above 500 lines; zero request-scoped locator calls outside allowlist.
- **Effort:** XL (split per endpoint family, parallelisable across families). **Risk:** Low-Medium. **Serialization:** conflicts with WP-05/-06 in the same files; sequence A: WP-07 split first (pure moves), then WP-05 and WP-06 operate on the smaller files, because moving code after changing it doubles review cost.

#### WP-ARC-C-08 Route conventions and versioning
- **Covers:** ARC-A-07.
- **Goal:** One canonical versioned route per resource, aliases explicitly deprecated.
- **Design:** Canonical prefix `/api/v1/<resource>` for every REST endpoint (`/api/v1/governance`, `/api/v1/extensions`, `/api/v1/webhooks`, `/api/v1/schema-registry`, `/api/v1/auth`, `/api/v1/sql`). Keep legacy aliases (`/api/governance/...`, `/api/sql`, `/api/v1/queries`) registered through a `MapLegacyAlias` helper that adds `Deprecation: true`, `Sunset` and `Link: <canonical>; rel="successor-version"` headers and emits a counter metric `gateway_deprecated_route_hits_total{route}` so removal can be data-driven. Protocol-fixed paths stay as is (`/v1/statement` Trino protocol, `/odata/v4`, `/mcp`, `/graphql`, `.well-known`). One OpenAPI document location per surface, listed in `docs/`.
- **Migration/compat (breaking at removal):** Aliases remain for at least two releases; removal requires a stakeholder decision (Section 6). Authorization policies (WP-06) are attached at group level so alias and canonical routes share them (security test: alias requests are denied for the same callers as canonical ones).
- **Security:** Duplicate route registrations are a classic auth-bypass risk (a policy attached to one registration only). Test enumerates endpoints and asserts alias and canonical endpoints have identical `IAuthorizeData` metadata. Phase 3 review.
- **TDD:** `RouteConventionTests`: every non-allowlisted route matches `^/api/v1/`; `LegacyAliasTests` assert headers and policy parity.
- **Acceptance:** all REST routes have a `/api/v1` canonical form; deprecated aliases carry headers; documentation updated.
- **Effort:** M. **Risk:** Medium (client breakage on removal only). Depends on WP-06 and WP-07.

#### WP-ARC-C-09 Application god-class decomposition
- **Covers:** ARC-C-06, ARC-C-11, ARC-G-08, ARC-F-14.
- **Goal:** Types under ~600 lines with one responsibility; remove forwarding API surface.
- **Design:**
  - `CasbinEnforcementService` (1897): extract `CasbinPolicyParser` (policy/model parsing, `:1037,1326`), `CasbinSnapshotBuilder` (`:411`), `CasbinDecisionCache` (cache + invalidation, `:712-726,865`; replaces magic `10_000` with an option, ARC-C-19), `CasbinReloadCoordinator` (reload and diagnostics, `:1792,1877`). `CasbinEnforcementService` remains the facade implementing the existing port; public API unchanged.
  - `VirtualFilterAdministrationService` (969): `VirtualFilterHistoryStore` (locks and history), `VirtualFilterDriftDetector`, contributor-id helper.
  - `TableAccessPolicy` (746): split evaluation from option interpretation.
  - ARC-C-11: delete `GatewayExecutionService.FilterRows/EnsureInMemoryFilterIsEnforceable/IsTypeStrictRowFilter` forwarders (`:598-606`) and call `InMemoryRowFilterEvaluator` directly; collapse twin constructors in `GatewayExecutionService`, `GovernedSqlExecutionService`, `GovernedProcedureExecutionService` to one DI constructor each (test-only constructors replaced by test builders in WP-17; coordinate with T2 which removes `Null*` constructor fallbacks).
  - ARC-F-14: split `Extensions/Dbt/DbtMetadataIngestionService`, `Extensions/OpenMetadata/OpenMetadataSyncService` into parse/map/persist/publish; `AiDataGuardrailService` into detector/policy/auditor; `HitLStepUpApprovalService` into locking/persistence/notification. One type per PR.
  - ARC-G-08: in `GraphQL/Types/QueryTypes.cs`/`MutationTypes.cs` remove `= default!` `[Service]` parameters and alias-only overloads (verify current state first; ARC-G-01 workflow extraction belongs to T1).
- **Migration/compat:** Internal refactoring; public interfaces unchanged; `InternalsVisibleTo` tests that call private members need adjusting.
- **Security:** Casbin is the authorization core: refactor must be behaviour-preserving, protected by the existing Casbin suites (`CasbinAbacPropertyTests`, `CasbinSnapshotImmutabilityTests`, `CasbinHotReloadTests`, `CasbinRlsPushdownTests`) plus a characterization test written first (snapshot of decisions over a policy x request matrix, recorded from the current implementation). Phase 3 review for Casbin. Fail-closed: a snapshot-build failure must keep the last good snapshot or deny (existing behaviour, assert it).
- **TDD:** characterization tests (green on old code) first, then extraction in small steps keeping them green.
- **Acceptance:** `CasbinEnforcementService.cs` < 600 lines; forwarders deleted; Casbin matrix test unchanged and green.
- **Effort:** XL (L for Casbin alone). **Risk:** Medium-High for Casbin. **Serialization:** the Casbin decision cache is also changed by T5 (ARC-C-07, cluster invalidation) owned by Architecture R0+R2. Do not run WP-09's Casbin part concurrently with ARC-C-07; sequence ARC-C-07 first or implement both in one branch under one owner. Non-Casbin parts are parallelisable.

#### WP-ARC-C-10 TrinoSqlEngine structural decomposition
- **Covers:** ARC-S-11, ARC-S-12.
- **Goal:** Smaller engine types with explicit passes; value-equal AST records.
- **Design:** Split `AstSecurityVisitor` partials into composable passes (`RowFilterInjector`, `MaskProjector`, `DmlGuardPass`) sharing a `SecurityScope`; split `SqlAstBuilder` (1498 lines) into statement/expression/table-source builders; replace `IReadOnlyList<T>` in AST records (`Ast/Nodes/Clauses.cs`, `Expressions.cs`, `SqlStatement.cs`, `TableSources.cs`) with `ImmutableArray<T>` (or an `EquatableArray<T>` wrapper to keep structural equality). Add/keep the exhaustiveness test (reflection over `SqlNode` subclasses vs generator/rewriter/verifier handlers, partially present in `SecurityCoverageTests`).
- **Migration/compat:** Public AST node signatures change (`ImmutableArray`); consumers in Application/Infrastructure (`PolicyPredicateRenderer`, compile callers) recompile. Plan the change as one atomic PR across the solution.
- **Security:** The AST security visitor is the RLS/masking boundary. Gate suites (`Autheris.Tests.SqlCompilerGate`, `TrinoSqlEngine.Tests` `Category=SqlGate`, mutants via `CompilerTestSeams`) must be green in Debug and Release. Phase 3 mandatory. No behaviour change: golden SQL outputs (`CatalogTypeMapGoldenTests` etc.) byte-identical.
- **TDD:** Add value-equality test for records first (red with `IReadOnlyList`), exhaustiveness test, then refactor.
- **Acceptance:** no engine type > 800 lines; equality tests green; Gate green in both configurations.
- **Effort:** L-XL. **Risk:** High (security boundary). **Serialization:** shares `TrinoSqlEngine` with WP-11, -12, -20; run strictly after WP-11/-12 or before, never concurrently (same hot files: `AstSecurityVisitor*.cs`, `CompiledSqlTemplate.cs`, `FastSqlEngine.cs`).

### Wave R5 - Hot-path performance

#### WP-ARC-C-11 Parser worker pool, engine singleton, cheaper syntax-error path
- **Covers:** ARC-S-03, ARC-S-04, ARC-S-09.
- **Goal:** Remove per-parse thread creation, restore parser/cache reuse, bound the cost of invalid input.
- **Design:**
  1. `ParseWorkerPool` (new, `src/TrinoSqlEngine/Parsing/`): N long-lived dedicated threads (`N = DefaultMaxConcurrentParses`, configurable, 16 MB stack as today, `IsBackground = true`), fed by a bounded queue (`Channel<ParseWork>` with `BoundedChannelFullMode.Wait` + queue-wait counted against the existing `ParseTimeout` budget). `RunWithParseBudget` submits work and waits with the remaining budget exactly as now; cooperative cancellation through `ParseCancellation` is unchanged. Queue full/budget exhausted -> same `ParseCanceledException("...rejected...")` (fail-closed, unchanged contract). Pool is owned by the engine instance but the default instance shares one process-wide pool (replaces static `SharedParseConcurrencyLimiter`; keep the `ParseConcurrencyLimiter` override property for tests or map it onto pool size).
  2. Inline fast path (optional, behind a measured benchmark): run on the calling thread when `sql.Length` is below a threshold (e.g. 4 KB) and `ExecutionStackGuard` confirms enough stack; otherwise use the pool. Decide by benchmark in `benchmarks/Autheris.Benchmarks` (see Section 6); default off until the benchmark and a deep-nesting stack-overflow test prove it safe.
  3. ARC-S-04: inject `ISqlEngine`/`FastSqlEngine.Default` everywhere: `GovernanceEndpoints.cs:705`, `PolicyParserSetup.cs:19`, `SqlFilterCompiler.cs:73,172,453`, `StructuredFilterSqlBuilder.cs:36` (drop the `?? new` fallback; callers pass the engine, test code uses `FastSqlEngine.Default` or a test helper). Add architecture test `NoNewFastSqlEngineInSrc` (only `FastSqlEngine.cs` and the DI registration may construct it).
  4. ARC-S-09: in `FastSqlEngine.cs:337-350` inspect the SLL `ParseCanceledException`: re-run LL only if the inner exception is an ambiguity/prediction failure (not for a lexer error or plain `InputMismatchException` at a position already in LL-equivalent context), cap LL fallback to a smaller budget (default 1 s, option), and add a small negative cache (bounded, keyed by SQL hash + options fingerprint, short TTL) so repeated invalid input is rejected without re-parsing. Benchmark with an invalid-SQL corpus.
- **Migration/compat:** `FastSqlEngine` constructor stays public for the standalone-library use; the Autheris production code stops using it. New options on `FastSqlEngine` (pool size, LL budget) have defaults equal to today's behaviour.
- **Security (fail-closed, DoS):** limits must not weaken: max concurrent parses, timeout, max nesting depth, max query length remain enforced; negative cache must not cache successful or policy-dependent results incorrectly (key includes token-security options; entries only for syntax failures; bounded size). Timeouts abort the worker cooperatively; a worker that exceeds budget by a grace period is replaced (never leaked). Phase 3 mandatory.
- **TDD:** red tests first in `tests/TrinoSqlEngine.Tests/Compiler/`: `ParseWorkerPoolTests` (no new thread created per parse: assert thread ids reused over 1000 parses; saturation returns the rejection exception within budget; cancellation releases the slot), `SyntaxErrorFastPathTests` (invalid SQL does exactly one LL pass for non-ambiguity errors), `DeepNestingStackTests`. Architecture test `NoNewFastSqlEngineInSrc` red until callers are fixed. Benchmark `ParseThroughputBenchmarks` before/after.
- **Acceptance:** thread creation per parse eliminated (test), p50 small-query parse latency improved in benchmark without regression of Gate suites; architecture rule green.
- **Effort:** L. **Risk:** Medium. **Serialization:** `FastSqlEngine.cs` shared with WP-10, -12, -20.

#### WP-ARC-C-12 Compile-cache and AST walk performance
- **Covers:** ARC-S-13 (remaining), ARC-S-15.
- **Design:**
  1. `CompiledSqlTemplateCache`: replace global `lock (_gate)` with `ConcurrentDictionary` plus `Interlocked` counters; eviction by approximate LRU (CLOCK or generation-based) with a size bound; key = `(request fingerprint, xxHash128(sql))` with a full-text equality check on hit, where the request fingerprint is computed once per datasource/policy epoch and memoised on the request object. Keep SHA-256 only where the hash is a security artefact (audit HMAC input), not as a cache index.
  2. `AstReflection`: give `SqlNode` a source-generated or hand-written `EnumerateChildren(Action<SqlNode>)`/`Children` implementation (source generator preferred: a small Roslyn generator in `tools/` or `TrinoSqlEngine.Generators`; fallback: hand-written per node with an exhaustiveness test comparing against reflection). Rewire `DialectCapabilityValidator`, `DmlMaskedReadGuard`, `InsertCheckPolicy`, `AstSecurityVisitor.TypedDml/Tables`, `SecurityCoverageVerifier` to the explicit-stack walk. Keep `AstReflection` in the test project as the oracle.
- **Security:** The cache key must include every security-relevant request field (existing mutant `OmittedCacheKeyFields` in `CompilerTestSeams` proves this and must stay green). Cache poisoning across tenants/policy epochs is the risk: Phase 3 mandatory; add a property test that two requests differing in any single `CompileRequest` property never share an entry.
- **TDD:** red: `CompiledSqlTemplateCacheConcurrencyTests` (contention benchmark/threshold test, LRU keeps hot key under cold traffic), `ChildEnumerationParityTests` (generated children == reflection children for every node type, including new nodes); existing cache-key mutant tests must stay green.
- **Acceptance:** no global lock on the hit path; hot entries survive cold traffic; reflection absent from compile path; Gate green Debug+Release.
- **Effort:** L. **Risk:** Medium-High. **Serialization:** `CompiledSqlTemplate.cs` is shared with WP-10 and WP-20.

#### WP-ARC-C-13 True streaming core (connector and executor)
- **Covers:** ARC-I-06.
- **Design:** Add `IAsyncEnumerable<ConnectorRow> StreamRowsAsync(...)` to `SqlDataSourceExecutor` (yield inside the `await using var reader` scope; `CommandBehavior.SequentialAccess` retained); `ReadBatchAsync` becomes a thin collector over the stream so existing callers keep working; `SqlConnector.ReadSplitAsync` (`Infrastructure/Connectors/SqlConnector.cs:131`) delegates to the stream (no up-front `ReadBatchAsync`). Per-row byte accounting and row-limit ceilings (`max(5000, 250_000, 1_000_000)`) apply while streaming. Masking, row-level filters and column denial run per row/batch in the pipeline before the row is yielded. A rows-as-`object?[]` + schema representation (instead of dictionary per row) is introduced for the streaming consumers (Arrow/Parquet) in WP-14; dictionary rows stay for existing small-result paths.
- **Migration/compat:** New interface members with default implementations or new interface `IStreamingConnectorRecordSource` to avoid breaking plugins (`IConnectorRecordSource` is a plugin-facing contract); existing implementers keep `ReadSplitAsync` semantics.
- **Security (fail-closed):** (a) Governance must complete (access decision, RLS compile, column mask plan) before the first row is read; (b) mid-stream failure aborts the reader and the consumer receives an error, never a silently truncated successful result; (c) limits are enforced during streaming, not after; (d) cancellation disposes the DB reader. Audit record for the query is written before streaming starts (ALLOW) and a completion/abort event is appended when the stream ends (coordinate with T2 audit tiers: ARC-C-14).
- **TDD:** red: `SqlConnectorStreamingTests` (first row yielded before the reader completes: use a fake `DbDataReader` that throws if read beyond row 1 before the first yield is consumed; limit exceeded mid-stream aborts; cancellation disposes reader); memory test with 1M synthetic rows asserting bounded allocation (benchmark, not unit gate).
- **Acceptance:** time-to-first-row independent of result size; peak memory for 1M-row export bounded by batch size; behaviour of existing small queries unchanged.
- **Effort:** L. **Risk:** Medium. **Dependency:** Architecture R3 (T3) moves ADO.NET execution out of Application into `Autheris.Infrastructure.Analytics`; implement the streaming API against the post-move location to avoid doing the work twice, or coordinate so WP-13 lands first and the move carries it (decision in Section 6).

#### WP-ARC-C-14 Streaming egress (Arrow, Parquet, extensibility)
- **Covers:** ARC-A-12.
- **Design:** `Endpoints/ArrowExportEndpoints.cs`: replace `ExecuteQueryBufferedAsync` (`:185`) with the stream from WP-13 feeding Arrow `RecordBatch` builders (batch size option, e.g. 8192 rows) written to the response body incrementally; OData Parquet and `ParquetGraphQLResponseMiddleware` write row groups per N rows from the stream/`Utf8JsonReader` rather than parsing a fully buffered 64 MB JSON (GraphQL responses remain buffered where the GraphQL executor itself is not streaming; keep the 64 MB cap but remove the second parse by consuming `JsonDocument` once). `GatewayExtensibilityMiddleware`/`BoundedResponseBufferStream`: stop copying (`ToArray()` at `BoundedResponseBufferStream.cs:39`, `GatewayExtensibilityMiddleware.cs:173`) by exposing `ReadOnlySequence<byte>`/pooled `IBufferWriter<byte>`; keep `MaxExtensibilityBufferBytes` enforced; responses above the limit are streamed through untouched only when no egress interceptor is configured for that route, otherwise the request fails closed (413/500 problem) as today.
- **Security (fail-closed):** Egress interceptors (DLP, lineage, masking at egress) must see every byte they see today; no route may bypass an interceptor because it exceeds the buffer. Response status/headers are committed after the first batch: errors afterward abort the connection (HTTP/1.1 reset / HTTP/2 RST_STREAM) rather than emit a normal end of body; test asserts the client sees a failed read. Row limits (`MaxResponseRows`, tier limits) enforced while streaming. Phase 3 mandatory.
- **Migration/compat:** Arrow/Parquet output bytes must stay readable by the same clients; content type and file schema unchanged. Chunked transfer replaces `Content-Length` for these endpoints (document).
- **TDD:** red: `ArrowExportStreamingTests` (first batch written before query completes; mid-stream failure aborts), `GatewayExtensibilityMiddlewareBufferTests` (no `ToArray` copy: allocation assertion with `GC.GetAllocatedBytesForCurrentThread`), `ParquetEgressRowGroupTests`.
- **Acceptance:** export of 1M rows with bounded memory; interceptor tests unchanged and green.
- **Effort:** L. **Risk:** Medium-High. Depends on WP-13; conflicts with WP-05/-07 in `ArrowExportEndpoints.cs`, `ODataEndpoints.cs`.

#### WP-ARC-C-15 Catalog hot paths and GraphQL module cohesion
- **Covers:** ARC-A-11, ARC-G-02, ARC-G-04, ARC-G-05, ARC-G-06.
- **Design:**
  1. `ICatalogVersionProvider.GetVersionAsync(ct)` (Application port) with implementation reading `max(updated_at)` + `count(*)` from the catalog tables (no schema change; if T4 adds a version counter column, switch implementation later). `CatalogGraphQlTypeModule.CheckForCatalogChangesAsync` compares versions and rebuilds the model only on change (`CatalogGraphQlTypeModule.cs:56-100`); publish `catalog:changed` on `IEventBus` from catalog writes to drive immediate rebuild (the timer stays as fallback). Add `ITableRelationRepository.GetRelationsAsync(IEnumerable<TableIdentifier>)` bulk method; replace the per-table loop in `CatalogSchemaModel.cs:198-206` (all three governance repositories need the bulk query: touch the three dialect partials minimally; coordinate with T4/R3 which is restructuring these repositories).
  2. ARC-A-11: `ICatalogLookup` (cached `ConcurrentDictionary` per tenant keyed by entity name, invalidated on `catalog:changed`) replaces `GetAllTablesAsync` per request in `ODataEndpoints.cs:344` and `DuckDbOlapEndpoints.cs:180`; tenant resolved only via `EndpointSecurity.GetRequestTenant`. Cache entries are tenant-scoped; lookup miss means 404 (fail-closed), never a fallback to another tenant.
  3. ARC-G-04: move the GraphQL request-pipeline builder from `Api/Extensions/GatewayServiceCollectionExtensions.cs` (`AddGatewayGraphQL`) into `Autheris.GraphQL` as `AddAutherisGraphQL(GraphQLOptions, ...)`, with a unit test asserting middleware order (`CostAndQuotaMiddleware` after variable coercion and before execution). Depth/cost limits expressed as one `QueryLimits` record consumed by GraphQL (OData/REST adoption of the record is out of scope, noted).
  4. ARC-G-05: delete `GraphQL/DynamicTypes/DynamicTableType.cs`; move `InvoiceItemDataLoader`/`InvoiceTypes` to a `Demo` folder behind a conditional DI extension (`demoDataEnabled`).
  5. ARC-G-06: pass headers lazily (`IHeaderDictionary` wrapper) in `CatalogGraphQlTypeModule.cs:484-489`, remove the second list copy (`:512-546`), and order `CostAndQuotaMiddleware` after the operation resolver so the document is parsed once.
- **Security:** Catalog cache must honour tenant isolation and visibility (a table hidden by policy must not become resolvable through the cache); invalidation failures (event lost) must fall back to a short TTL, not permanent staleness. Phase 3 review for the lookup cache and the GraphQL pipeline move (security middleware order).
- **TDD:** red: `CatalogVersionProviderTests` (no rebuild when unchanged; N tables cause 1 relations query), `ODataCatalogLookupTests` (no `GetAllTablesAsync` per request; tenant A cannot resolve tenant B entity), `GraphQlPipelineOrderTests`.
- **Acceptance:** governance DB queries per refresh tick constant instead of N; OData flat route does not scan the catalog; `DynamicTableType` removed.
- **Effort:** L. **Risk:** Medium. **Dependencies:** T4 (repository restructuring) and T1 (GraphQL table reads via AST) touch the same GraphQL/Catalog files; coordinate rebases.

#### WP-ARC-C-16 Startup I/O and telemetry unification
- **Covers:** ARC-I-10 (Redis/Garnet connect), ARC-I-09, ARC-I-11.
- **Design:**
  1. ARC-I-10: `IRedisConnectionProvider` backed by `ConnectionMultiplexer.ConnectAsync` initialised in an `IHostedService` (`RedisConnectionInitializer`, started before readiness; readiness check reports "connecting"), replacing `AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(...))` (`GatewayStorageServiceExtensions.cs:67,81`) and moving `StartServer()` (`:56`, `Program.cs:79`) into the same initializer for the embedded Garnet case. Consumers that need the multiplexer get a `Lazy`/async accessor; consumers whose failure semantic matters (T5: absent vs unavailable) get `Unavailable` surfaced, not a null multiplexer. Repository-constructor DDL stays with T4.
  2. ARC-I-09: standardise on `System.Diagnostics.Metrics` (`IMeterFactory`-created instruments); migrate the 7 prometheus-net statics (re-grep `Prometheus.Metrics.Create*`, e.g. `RedisIdempotencyStore`, `RedisRateLimiterService`, `RateLimitingMiddleware`, `BasicAuthenticationHandler`, `CompiledSqlQueryPlanCache`, `ConsentCacheService`) keeping metric names and labels; expose via `OpenTelemetry.Exporter.Prometheus.AspNetCore` on `/metrics` (replaces `MapMetrics()` at `GatewayApplicationBuilderExtensions.cs:354`; HTTP metrics via OTel ASP.NET instrumentation or keep `UseHttpMetrics` until metric-name parity is proven). Remove `prometheus-net` package from Application/Infrastructure (also satisfies the T3 boundary rule).
  3. ARC-I-11: delete `Infrastructure/Diagnostics/GatewayDiagnostics.cs`; callers use the Domain class (or injected metrics from item 2).
- **Migration/compat:** `/metrics` output names must stay identical (dashboards); verified by a scrape-diff test. New package `OpenTelemetry.Exporter.Prometheus.AspNetCore` (pre-release flag check; SDK/package pinning per SR-D-09 neighbour). Garnet start timing change: readiness endpoint reflects initialisation.
- **Security:** `/metrics` exposure/authorization unchanged (verify auth middleware still guards it; no new anonymous endpoint). Redis connect failure keeps fail-closed semantics for rate limiting and revocation (owned by T5); this WP must not turn an unavailable Redis into "no limits".
- **TDD:** red: `RedisConnectionInitializerTests` (no sync connect during DI resolution: resolving the provider does not block or throw when Redis is down; readiness false until connected), `MetricsParityTests` (names/labels of migrated instruments match a golden list), `NoPrometheusNetInApplicationTests` (architecture).
- **Acceptance:** no `ConnectionMultiplexer.Connect(` sync call in factories; one metrics pipeline; arch test green.
- **Effort:** M-L. **Risk:** Medium (readiness/startup ordering, dashboard compatibility). Shares `GatewayStorageServiceExtensions.cs` with T5 work (ARC-I-03/-07/-08); coordinate.

### Wave R6 - Test and build hygiene

#### WP-ARC-C-17 Shared test foundation, traits, banned APIs
- **Covers:** ARC-T-02, ARC-T-04, ARC-T-07.
- **Design:** New `tests/Autheris.Tests.Common` (no xunit test classes): builders and fakes (`FakeEpochValidationService`, `StaticOptionsMonitor<T>` using `Options.Create`/`OptionsMonitor` helpers, `StubHttpMessageHandler`, `TempDirectory : IDisposable`, `Eventually(...)` polling helper with `TaskCompletionSource` variants, `FakeTimeProvider` re-export via `Microsoft.Extensions.TimeProvider.Testing`). Delete the 8 `StubEpochValidationService` and 7 `TestOptionsMonitor<T>` copies. Add trait convention: `[Trait("Category","Unit|Integration|Docker|Slow")]` through a `CategoryAttribute` base applied by folder using a small analyzer test or xunit `ITraitAttribute` discoverer; CI lanes use `--filter`. Replace project-wide serialization in Integration by explicit `[Collection]` groups for tests sharing DB/ports (`xunit.runner.json:5`, `AssemblyAttributes.cs:3`); share `WebApplicationFactory` via collection fixtures. Introduce `BannedApiAnalyzers` `BannedSymbols.txt` for test projects (`Thread.Sleep`, `Task.Delay` without justification, `DateTime.UtcNow`/`DateTimeOffset.UtcNow` in new tests) as warnings that ratchet (existing violations suppressed via a baseline file, not new ones).
- **Migration/compat:** test-only. 
- **Security:** none; keep test doubles fail-closed by default (fakes deny unless configured).
- **TDD:** the architecture test `NoDuplicateTestDoubles` (names `Stub*Epoch*`, `TestOptionsMonitor*` declared outside Common) is the red test; migrate until green.
- **Acceptance:** zero duplicated doubles; every test class carries a category; Integration parallelism configured per collection; CI lanes (`unit`, `integration`, `docker`) selectable by filter.
- **Effort:** L. **Risk:** Low. **Order:** must precede WP-19 (split) because moved tests should consume Common, not recreate doubles.

#### WP-ARC-C-18 TimeProvider adoption and deterministic timing
- **Covers:** ARC-T-03, part of ARC-C-19 (magic timing constants).
- **Design:** Inject `TimeProvider` into time-dependent services (cache TTLs, token/session expiry, plan cache, rate limiters, hosted-service schedulers, approvals); register `TimeProvider.System` once in the composition root. Priority list from tests that sleep or compare wall clock: `Application/Sql/CompiledSqlQueryPlanCache` (the `Thread.Sleep(70)` against a 50 ms TTL pattern in `GovernedSqlPlanCacheTests.cs`), Casbin hot reload (`CasbinHotReloadTests.cs`), chunked executors, FinOps budgets, epoch validation, consent expiry. Replace `Task.Delay` polling in tests by `FakeTimeProvider.Advance` or `Eventually`. Ratchet architecture test `NoNewUtcNowInServices`: types registered as services may not call `DateTime.UtcNow`/`DateTimeOffset.UtcNow` outside an allowlist (starting from the current 322 sites, lowered per batch; migrate service classes first, DTO/entity factories last). Time-dependent security checks (token expiry, replay windows) migrate first: they become testable without sleeps and are tamper-resistant against skew bugs.
- **Migration/compat:** internal; constructors gain a `TimeProvider` parameter (default `TimeProvider.System` only on factory/test helpers, not in production constructors, to avoid hidden fallbacks as T2 requires for audit).
- **Security:** Behaviour must not change for expiry boundaries: add boundary tests (`expiresAt == now` is expired = fail-closed) for each migrated component before migrating.
- **TDD:** per component, first a failing `FakeTimeProvider` test that cannot be written today (no injection point), then the change.
- **Acceptance:** `Task.Delay`+`Thread.Sleep` in Unit tests reduced to those testing real I/O timeouts (target < 10, each justified); `UtcNow` ratchet baseline stated and decreasing.
- **Effort:** L-XL (long tail, batchable by component). **Risk:** Low-Medium. Can run in parallel with WP-17 after the Common project exists; coordinate with T2/T5 owners who edit the same services (audit, cluster state, tokens).

#### WP-ARC-C-19 Split of `Autheris.Tests.Unit`
- **Covers:** ARC-T-01, ARC-T-05, ARC-T-06 (residual), ARC-L-10.
- **Goal:** Test projects mirror production assemblies; solution has one solution file.
- **Design:** Create `Autheris.Domain.Tests`, `Autheris.Application.Tests`, `Autheris.Infrastructure.Tests`, `Autheris.GraphQL.Tests`, `Autheris.Api.Tests`; engine-specific tests currently in Unit (referencing `RlsOptions`/engine internals, `GovernedSqlPlanCacheTests`, `Security/CorrelatedRowFilterAliasTests`) move to `TrinoSqlEngine.Tests`; `ArchitectureAndCleanCodeTests.cs` moves to `Autheris.Tests.Architecture` (ARC-T-08 part). Each project references only its production assembly (+ `Tests.Common`), which also gives the dependency checks real teeth. Move in batches by folder/namespace with `git mv` to keep history; one project per PR (order: Domain -> Infrastructure -> GraphQL -> Api -> Application, because Application tests have the widest cross-references). Update `InternalsVisibleTo` in each `src/*.csproj` (replace blanket `Autheris.Tests.Unit` entries; keep until the last batch lands, then delete) and `stryker-config.json`, `.github/workflows/ci.yml`/`release.yml` test steps. Rename date/ticket-named files (`SecurityReview20261002*`, `SecurityFindingsRemediationTests`) by behaviour with `[Trait("Finding","SEC-xx")]` kept for traceability (the catalog `tests/TrinoSqlEngine.Tests/Security/SecurityRegressionCatalog.cs` already exists); split files over ~500 lines while moving, not before. Remove the 7 residual legacy-name references (ARC-T-06) while touching those files: rename helper names and delete tests proven duplicated by `Compile`-based tests (each deletion needs the traceability catalog entry to stay covered).
  ARC-L-10: keep `Autheris.sln` (CLAUDE.md and CI use it) and delete `Autheris.slnx`, or migrate to `.slnx` and update CI/CLAUDE.md in the same PR. Recommendation: delete `.slnx` now, revisit later. If kept, add missing `SqlCompilerGate`/Extensions.Tests/Benchmarks projects and an architecture test comparing both files.
- **Migration/compat:** CLAUDE.md test command `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj` changes; update CLAUDE.md, AGENTS.md, `docs/developer-guide.md`, CI. Until the last batch, Unit remains and is shrinking.
- **Security:** No test may be dropped silently: before moving, record the test count per class (`dotnet test --list-tests`) and compare total counts before/after each PR (CI check script in `scripts/`); security-review tests keep their traits.
- **TDD:** the red test is a count-parity check plus an architecture test `TestProjectsMirrorSourceProjects` (each test project references at most one production project besides Common).
- **Acceptance:** `Autheris.Tests.Unit` removed; total test count unchanged or higher; each new project builds and runs independently; CI green in both lanes; only one solution file.
- **Effort:** XL. **Risk:** Medium (merge conflicts, lost tests). **Serialization (explicit):** this WP rewrites the paths of ~470 files and conflicts with every branch touching tests. Execute when the fewest feature branches are open (recommended: immediately after a release cut, with a declared two-day merge freeze on `tests/Autheris.Tests.Unit/**`), one project per PR merged the same day; do not run concurrently with WP-03, WP-17, WP-18 (all edit test files). Hence order: WP-17 first (Common), then WP-19, then WP-18/WP-03 batches rebased onto the new layout.

#### WP-ARC-C-20 `#if DEBUG` security seams and engine packaging
- **Covers:** ARC-S-10, ARC-S-16. ARC-S-06 (`#if DEBUG` seams) is owned by `WP-ARC-A-08` per the cross-scope resolution in [`00-implementation-roadmap.md`](00-implementation-roadmap.md) §2; this WP reuses the A-08 hooks and keeps only the packaging work.
- **Goal:** Release and Debug compile the same security code paths; the mutation harness does not live in the product assemblies as conditional code.
- **Design (recommended option B, see Section 6):** Replace the 32 `#if DEBUG` blocks with an injected `ICompilerHooks` (null-object `NoOpCompilerHooks` in production; one virtual call per stage is negligible next to parse/compile cost) and `ISecurityControlSwitch` (`bool IsDisabled(SecurityControl)`), defined in `TrinoSqlEngine` and implemented by a test-only `MutantHooks` in `TrinoSqlEngine.Tests`/`Autheris.Tests.SqlCompilerGate` that is injected via `CompilerOptions`/DI in tests (not `AsyncLocal` static). The four non-engine sites (`TenantRegistryGate.cs:81`, `SqlCompilerErrorMapper.cs:43`, `SessionPinning.cs:53`, `GovernedCatalogProvider.cs:118`) take `ISecurityControlSwitch` through DI. Registration of a non-no-op switch in a non-test host is impossible: the interface is `internal` to `TrinoSqlEngine` with `InternalsVisibleTo` for the test assemblies only, and a Release-time architecture test (`ReleaseHookAbsenceTests`, already present) asserts no concrete switch type exists in shipped assemblies. Option A (lower effort): keep `#if DEBUG` but make the full unit/gate suite run in Release in CI as a required job and keep `ReleaseHookAbsenceTests`; the Release path then is tested but still differs.
  ARC-S-10: remove `InternalsVisibleTo Autheris.Application/Infrastructure` (`TrinoSqlEngine.csproj:39-40`) as the hooks become public-internal-free; rename meter `Autheris.SqlCompiler` to a host-configurable name (`CompilerTelemetry.cs:12`); the Core/Governance assembly split is deferred (large, no current consumer: Section 6).
  ARC-S-16: PackageId/Product/Authors to a name decision (`Autheris.SqlEngine` or keep `FastSqlEngine` deliberately), translate German comments (`TrinoSqlEngine.csproj:13`, `FastSqlEngine.cs:332,339`), mark ANTLR generated output `generated_code = true` in `.editorconfig` and exclude from Stryker `mutate`, pin `Antlr4BuildTasks` tool version (`AntlrToolVersion`), confirm/justify `LangVersion 14` vs CLAUDE.md "C# 13 idioms" (update CLAUDE.md or the csproj).
- **Migration/compat:** Test code that uses `CompilerTestSeams.Current` (about 9 test files under `tests/TrinoSqlEngine.Tests/Compiler` and `Security`) changes from AsyncLocal to explicit injection. Public API of `TrinoSqlEngine` gains no public members (hooks internal).
- **Security:** This is a security-boundary change: Phase 3 mandatory. Fail-closed requirement: absence of hooks == all controls enabled; there is no configuration, environment variable, or DI registration in production that can disable a control (test `ProductionHostHasNoControlSwitchTests` resolves the switch from the real host and asserts the `NoOp` type). Mutant suite must keep killing mutants at the same rate (compare the number of caught mutants before/after).
- **TDD:** red tests first: `ReleaseAndDebugParityTests` (same compiled output for the Gate corpus in both configurations, run in CI matrix), `ProductionHostHasNoControlSwitchTests`; existing mutant tests re-pointed to injected hooks.
- **Acceptance:** `rg '#if DEBUG' src` is empty; Gate suites (Debug and Release) green with mutant parity; no `InternalsVisibleTo Autheris.Application` on TrinoSqlEngine.
- **Effort:** L-XL. **Risk:** High (mutation gate rewrite). **Serialization:** touches `GovernedSqlCompiler.cs`, `CompiledSqlTemplate.cs`, `TenantPredicateFactory.cs` shared with WP-10, -11, -12; run after them.

#### WP-ARC-C-21 Architecture test expansion, analyzer and documentation hygiene
- **Covers:** ARC-L-08, ARC-T-08, ARC-L-09, ARC-L-13, ARC-L-14, ARC-L-17 (suppressions), ARC-C-19.
- **Design:**
  1. Architecture tests (in `tests/Autheris.Tests.Architecture`): Domain has no third-party package reference beyond the documented list (MemoryPack allowlisted until T3/Domain-purity decides); Application must not use `Microsoft.Data.*`, `Npgsql`, `DuckDB`, `Parquet`, `Prometheus`, `HotChocolate` outside allowlist (HotChocolate.Language in `SchemaRegistry/ISchemaLinter.cs` allowlisted or moved; boundary rules owned by T3 are added there, this WP adds the framework and the rules that are green today, with an explicit "pending T3" skip list so rules turn on when the work lands); no `GatewayOptions` consumption beyond the ratchet (WP-01); no `IServiceProvider` outside composition roots; `NoNewFastSqlEngineInSrc` (WP-11); max file length 500 lines with a baseline allowlist (ratchet); lifetime rules via a `ServiceCollection` scan (singleton must not depend on scoped). Consolidate `tests/Autheris.Tests.Unit/ArchitectureAndCleanCodeTests.cs` (moved in WP-19).
  2. ARC-L-17: remove suppressions from `Directory.Build.props:16` in groups: first `CA1816` and `CA2012` (correctness: Dispose pattern, ValueTask misuse), then `CA1305/CA1304/CA1307/CA1310/CA1311` (culture/string comparison: security-relevant for case-insensitive identifier compares), then the style ones (`CA1822`, `CA1848`, `CA1859`, `CA1861`, ...). Fix or move to a per-project `NoWarn` with a justification comment; each group is its own PR because `TreatWarningsAsErrors` makes every hit a build break. Document the process in `docs/developer-guide.md`.
  3. ARC-L-13 / L-09: keep one plan root (`docs/plans`) and remove `docs/plan` duplicates and the `doc -> docs` symlink after fixing references (CLAUDE.md/AGENTS.md still cite `doc/plan/...`; they are governance files - propose the edit to the owner, do not change them silently); rename `docs/architecture/konzept-data-governance-und-mcp-documentation.md` to English (and translate); refresh `docs/architecture/arc42.md` quality-gate text (test counts, "Hot Chocolate isolated to GraphQL" claim) or remove volatile numbers. This is documentation work for Phase 6.
  4. ARC-L-14: delete local leftovers (`fix4.tgz`, `fix4b.tgz`, `libpod/`, `dist/publish`, `src/Autheris.Api/scratch`); ensure `.gitignore`/`.dockerignore` cover them (already ignored).
  5. ARC-C-19: translate remaining German comments (`Application/Services/GatewayExecutionService.cs:524`, TrinoSqlEngine items in WP-20) and move magic defaults to option classes (`Casbin` cache cap, `FederatedDuckDbExecutionService` limits) as part of WP-03/WP-09 where those files are touched; a CI grep for common German markers is optional.
- **Security:** Enabling CA1307/CA1310/CA1311 can surface culture-sensitive comparisons in authorization code; fixes must use `StringComparison.Ordinal(IgnoreCase)` deliberately (review each change in security-relevant namespaces). `AutherisIncludeTestAuth` default is SR-D-07 and is not changed here.
- **TDD:** each new architecture rule is its own red test first (written against a seeded violation fixture or the current baseline).
- **Acceptance:** rules listed above present and green (or explicitly skipped with an owner and WP link); NoWarn reduced to a documented minimal list; one plan directory.
- **Effort:** L (spread over several small PRs). **Risk:** Low-Medium (warnings-as-errors fallout).

#### WP-ARC-C-22 Integration hygiene in Application/Extensions
- **Covers:** ARC-F-12, ARC-F-13, ARC-F-18.
- **Design:**
  - ARC-F-12: register a named secure client `EgressIntegrations.OpenApiIngestion` through `AddSecureOutboundHandlers` and inject `IHttpClientFactory` into `CatalogDiscoveryService` (`Application/Catalog/Services/CatalogDiscoveryService.cs:317-323`), deleting manual `SecureOutboundHttp.CreatePrimaryHandler(... EgressAllowlist.Create(null, null) ...)`. Allowlist comes from `OutboundEgressOptions`; with an empty allowlist the client denies (fail-closed). SSRF protection handler included.
  - ARC-F-13: move ITSM adapters, outbox, and hosted services into `Autheris.Extensions/Itsm` behind an Application `IOutboxStore<T>` port; reusable `OutboxDispatcher<T>`. Depends on ARC-F-09 and T3/T6 decisions (extension boundary, outbox for webhooks, Architecture R0+R2/R1+R3): schedule after those; this WP only owns the ITSM relocation.
  - ARC-F-18: `ScheduledHostedService<TJob>` base class with uniform loop (`PeriodicTimer`, optional initial delay, jitter, metrics `last_success`/`duration`, graceful shutdown, `IOptionsMonitor` interval). Lease/leader semantics come from ARC-F-06 (T5); the base class exposes a `ILeaderLease` seam but does not implement it. Migrate `MssqlChangeTrackingHostedService`, `OpenMetadataSyncBackgroundService`, `DataCatalogSyncBackgroundService`, `AuditChainIntegrityMonitor`, ITSM hosted services one by one.
- **Security:** F-12 fail-closed egress; F-13 outbox relocation must keep delivery semantics (T6 defines retry rules); F-18 must not change which node runs jobs.
- **TDD:** red: `CatalogDiscoveryServiceEgressTests` (blocked host -> denied, no `new HttpClient` in Application: architecture rule); `ScheduledHostedServiceTests` with `FakeTimeProvider` (needs WP-18 `TimeProvider` base).
- **Acceptance:** no `new HttpClient` in Application (arch rule: guardrail 2 in `architecture.md` already requires it); one loop pattern.
- **Effort:** M (F-12 S, F-18 M, F-13 M-L). **Risk:** Low-Medium.

---

## 3. Dependencies

### Between work packages

| WP | Depends on | Reason |
|---|---|---|
| 02 (validation) | 01 | needs section classes and `GatewaySections` constants |
| 03 (consumer migration) | 01, 02 | ratchet and per-section validators |
| 04 (pipeline) | none (but see serialization) | hot file `GatewayApplicationBuilderExtensions.cs` |
| 05 (ProblemDetails) | 07 (preferably), SR-L-03/SR-L-23 | split first, then change bodies |
| 06 (policies) | 07 (preferably), SR-L-04 | one role parser |
| 08 (routes) | 06, 07 | policy parity on aliases, smaller files |
| 09 (god classes) | 17 (builders) preferred | ctor collapse needs test builders |
| 10 (engine structure) | 11, 12 complete | same files |
| 12 | 11 | same engine files; sequential |
| 13 (streaming core) | R3/T3 Application/Infrastructure move (coordinate) | avoid doing the work twice |
| 14 (streaming egress) | 13 | consumes the stream |
| 15 (catalog hot paths) | T1/T4 coordination | GraphQL/Catalog and repository partials |
| 16 (startup/telemetry) | T5 (cluster state provider) coordination | `GatewayStorageServiceExtensions.cs` |
| 17 (test foundation) | none | prerequisite for 19, 18, 22 |
| 19 (test split) | 17 | consume Common |
| 18 (TimeProvider) | 17 | FakeTimeProvider in Common; T2/T5 service edits |
| 20 (DEBUG seams) | 10, 11, 12 | same files; mutant suite rewrite last |
| 21 (arch tests/hygiene) | 01 (ratchet), 11 (rule), 19 (test move), 16 (Prometheus rule) | rules need the work they guard |
| 22 | 18 (Fake clock for base class), T6/T5/T3 | outbox and lease seams |

### On neighbouring scopes (by finding ID, not planned here)

- **Architecture R0+R2:** ARC-C-14 (audit tiers; sync audit on hot path, referenced by WP-13 audit-completion event), ARC-C-03/-04/-09/-17 (`Null*` constructor fallbacks; interacts with WP-09 constructor collapse), ARC-C-07 (Casbin cache invalidation; must be sequenced with WP-09 Casbin split), ARC-C-05/ARC-I-03/-07/-08 (cluster state, shared `GatewayStorageServiceExtensions.cs` with WP-16), ARC-F-06 (lease; WP-22 seam), ARC-F-01..04 (webhook outbox; WP-22 ITSM `OutboxDispatcher<T>` reuse).
- **Architecture R1+R3:** ARC-C-12/ARC-G-01/ARC-T-06 context (single pipeline; WP-19 residual legacy names), ARC-L-02/ARC-C-02/ARC-F-19/ARC-I-02 (Application thinning; WP-13 location of streaming API, WP-16 prometheus removal from Application, WP-21 package-boundary rules), ARC-I-01/-04/-05 (repository restructuring and migration runner; WP-15 bulk relations method, ARC-I-10 DDL half).
- **Security Wave C:** SR-L-03 and SR-L-23 (error leakage content rules; WP-05 supplies the single enforcement point), SR-L-04 (canonical role parsing; WP-06 consumes it), SR-D-07 (`AutherisIncludeTestAuth`; WP-21 leaves it alone), SR-D-09 (SDK pinning; WP-16 package addition, WP-20 `global.json`/ANTLR tool pin).
- **Features:** new feature work adds options and endpoints; they must use the new conventions after WP-01/-05/-06/-08 land (see freeze windows).

---

## 4. Recommended sequence

Phase 0 (can start immediately, low conflict, parallel):
- WP-ARC-C-17 (test foundation), WP-ARC-C-11 (parser pool; engine-only plus a few call sites), WP-ARC-C-21 items 2/4/5 (CA suppression groups, stray files, comments), WP-ARC-C-22 F-12 (S).

Phase 1 (structural freeze windows, serialized; do when few branches are open):
1. WP-ARC-C-01 (options file split) - first, short freeze on `GatewayOptions.cs`.
2. WP-ARC-C-19 (test project split) - second, with a declared merge freeze on `tests/Autheris.Tests.Unit/**`. Run before the long-tail WPs that edit many tests (03, 18). WP-01 and WP-19 must not overlap in time (WP-01 adds tests in Unit/Architecture; schedule 01 first, merge, then 19).
3. WP-ARC-C-02 (validation), then WP-ARC-C-03 batches (each batch a small PR; parallel across areas only if test files are disjoint).

Phase 2 (API surface, serialized on `Autheris.Api`, one at a time because of shared hot files `GatewayApplicationBuilderExtensions.cs`, endpoint files, `EndpointSecurity.cs`):
4. WP-ARC-C-04 (pipeline/composition) -> 5. WP-ARC-C-07 (split endpoints, embedded assets, locator removal; parallelisable per endpoint family) -> 6. WP-ARC-C-06 (policies; after SR-L-04) -> 7. WP-ARC-C-05 (ProblemDetails; after SR-L-03/-23; batches per file group can run in parallel once files are small) -> 8. WP-ARC-C-08 (routes/versioning).

Phase 3 (performance, partly parallel with Phase 2 because the file sets are disjoint):
- Engine track (serialized, same files): WP-11 -> WP-12 -> WP-10 -> WP-20.
- Data track (serialized): WP-13 -> WP-14 (touches `ArrowExportEndpoints.cs`, `ODataEndpoints.cs`: schedule after WP-07 and WP-05 for those files).
- Independent: WP-15 (coordinate with T1/T4), WP-16 (coordinate with T5).

Phase 4 (long tail, any time after Phase 1, mostly parallel):
- WP-ARC-C-09 (non-Casbin parts first; Casbin part after ARC-C-07), WP-ARC-C-18 (TimeProvider, by component), WP-ARC-C-22 (F-18, F-13 after T3/T5/T6), WP-ARC-C-21 items 1/3 (rules switched on as dependencies land).

Parallelisable sets (low file overlap): {11, 17, 21.2, 22.F-12} ; {engine track} || {data track} || {API track}; WP-15 || WP-16 || WP-09 non-Casbin.
Must be serialized: 01 -> 19 -> 03; 04 -> 07 -> 06 -> 05 -> 08; 11 -> 12 -> 10 -> 20; 13 -> 14.

Rationale: structural moves that rewrite many paths (01, 19) go first and alone, because every later WP edits files they move. API behaviour changes (05, 06, 08) follow file decomposition (07) so each review sees small files. The engine track is ordered by risk: cheap/measurable perf (11, 12) before structural (10) and the mutation-harness rewrite (20), which is the largest security-boundary change and benefits from the others being stable.

---

## 5. Open decisions requiring stakeholder input

1. **`ProblemDetails` migration policy (WP-05):** hard switch vs. one release carrying a legacy `error` extension field. Recommendation: legacy field for one release (`Gateway:Api:LegacyErrorField=true`), flip default to `false` next release, remove after.
2. **Route alias retention (WP-08):** how many releases deprecated aliases (`/api/...`, `/api/sql`, `/api/v1/queries`) stay. Recommendation: two minor releases, usage counter decides.
3. **`#if DEBUG` removal strategy (WP-20):** option B (injected `ICompilerHooks`/`ISecurityControlSwitch`, no conditional compilation) vs option A (keep seams, require Release-mode full suite in CI). Recommendation: B, because the extra virtual call is negligible and it eliminates divergent code paths; A as an interim step if the mutant-gate rewrite cannot be scheduled.
4. **Parser inline fast path (WP-11):** enable parse-on-caller-thread for short SQL? Recommendation: implement the pool first (always safe), enable inline path only if the benchmark shows a material gain and stack-depth tests pass.
5. **Streaming API location (WP-13):** implement in Application now and let T3 move it, or wait for T3 and implement in `Autheris.Infrastructure.Analytics`. Recommendation: define the port (`IStreamingConnectorRecordSource`) in Application now, implement streaming in the existing executor, and let the T3 move carry it; avoids blocking performance work on R3.
6. **Solution file (WP-19):** keep `Autheris.sln` (delete `.slnx`) or migrate to `.slnx`. Recommendation: keep `.sln`, delete `.slnx`; revisit when tooling/CI is fully `.slnx`-ready.
7. **Test project granularity (WP-19):** one project per production assembly (recommended) vs coarser two-project split (fast/slow). Recommendation: per assembly plus category traits for lanes.
8. **TrinoSqlEngine Core/Governance split (ARC-S-10, deferred):** worth doing only if the engine is to be published standalone. Recommendation: defer; record decision in `architecture.md`.
9. **Metrics stack (WP-16):** OpenTelemetry Prometheus exporter (pre-release status of the package must be accepted) vs keeping prometheus-net for `/metrics` and bridging meters. Recommendation: OTel exporter; if the package is unacceptable, keep prometheus-net only in `Autheris.Api` (not Application/Infrastructure) and bridge `Meter` instruments.
10. **Governance documents (WP-21):** CLAUDE.md/AGENTS.md still refer to `doc/plan/` and `/root/autheris` absolute paths; removing the `doc` symlink requires the owner to approve edits to those files.
11. **Option binding model (WP-03):** keep `GatewayOptions` root as the user-facing config model (recommended; no config break) vs introducing separate top-level config sections (breaking). Recommendation: root stays, sections are views via `Bind(GetSection)`.
