# Implementation Plan: Architecture Waves R4-R6 (API & Options, Hot-Path Performance, Test & Build Hygiene)

**Document ID:** `IMPL-ARCH-R4-R6`
**Parent plans:** [`../00-master-plan-overview.md`](../00-master-plan-overview.md), [`../architecture.md`](../architecture.md)
**Status:** **DELIVERED ✅** (`WP-ARC-C-01..C-18`, `WP-ARC-C-20..C-22` delivered; `WP-ARC-C-19` physical unit-test project split reserved for a dedicated test-freeze window)
**Scope:** Themes T7 (configuration and god-class decomposition), T8 (API surface standardization), T9 (hot-path performance), T10 (engineering hygiene, test structure, tooling).
**Date:** 2026-10-15
**Base:** `feat/impl-wave-5-6`
**WP prefix:** `WP-ARC-C-NN`

Delivered across Waves 1–6: `WP-ARC-C-01` (`GatewayOptions` split into area files, `GatewaySections`, `GatewayOptionsConsumptionRatchetTests`), `C-02` (`IValidateOptions<GatewayOptions>` validators), `C-04` (named middleware classes, `DomainScopeRewriteMiddleware`, `McpPathResolver`), `C-05` (`ApiProblems` & `ApiErrorCodes` RFC 7807 with legacy `error` field compatibility), `C-06` (`GatewayPolicies`), `C-07` (endpoint partial decomposition & `DevPortalAssets`), `C-08` (`VersionedEndpointRouteBuilderExtensions`), `C-09` (`CasbinEnforcementService`, `VirtualFilterAdministrationService`, `TableAccessPolicy` decomposition), `C-10` (`AstSecurityVisitor` split), `C-11` (parser worker pool), `C-12` (lock-free `CompiledSqlTemplateCache` hit path & compiled AST child enumeration), `C-13` (`StreamRowsAsync`/`StreamAsync` streaming core), `C-14` (zero-copy `BoundedResponseBufferStream.WrittenMemory`), `C-15` (`ICatalogVersionProvider` & `TenantCatalogLookup`), `C-16` (`IRedisConnectionProvider`/`RedisConnectionInitializer` & unified `GatewayDiagnostics`), `C-17` (`Autheris.Tests.Common` & `FakeEpochValidationService`), `C-20` (`TrinoSqlEngine` packaging hygiene), and `C-22` (`ScheduledHostedService` & `CatalogDiscoveryService` `IHttpClientFactory`). `Autheris.slnx` was removed per `G-10`.

---

## 1. Verification table (status on main)

Counts re-measured on `main` @ `a4137626`. All rows are open unless marked otherwise.

### T7 - Configuration and god-class decomposition

| ID | Status | Evidence |
|---|---|---|
| ARC-L-01 GatewayOptions god config | Open | `src/Autheris.Domain/Options/GatewayOptions.cs` is 2150 lines, 79 `class` declarations. 295 `IOptions<GatewayOptions>` + 8 `IOptionsMonitor<GatewayOptions>` usages in `src/`. |
| ARC-L-07 option validation structure | Open | `Api/Extensions/GatewayServiceCollectionExtensions.cs:94-180` still ~24 inline `.Validate(...)` lambdas incl. disk I/O (`File.Exists` `:104,110`, `Directory.Exists` `:176`); second validator `GatewayStartupValidator.ValidateStartup` (`GatewayStartupValidator.cs:24`, 914 lines); third `GatewayEmptyListConfigurationValidator` (`:186`); options bound a second time as snapshot at `:190` (`Get<GatewayOptions>()`). 12 `IValidateOptions` implementations exist now (new standalone option classes), none for the `GatewayOptions` rules. |
| ARC-L-11 giant registration/startup classes | Open | `GatewayServiceCollectionExtensions.cs` 683 lines, `GatewayStartupValidator.cs` 914, `GatewayApplicationBuilderExtensions.cs` 512; `Plan9ServiceExtensions.cs` unchanged (`Program.cs:70`). |
| ARC-A-04 route god files | Open | `Endpoints/GovernanceEndpoints.cs` 1007, `WebSqlEndpoints.cs` 973, `VirtualFilterEndpoints.cs` 907, `ODataEndpoints.cs` 796, `DevPortalEndpoints.cs` 712 lines. |
| ARC-C-06 Application god classes | Open | `Governance/CasbinEnforcementService.cs` 1897, `VirtualFilters/VirtualFilterAdministrationService.cs` 971, `Policy/TableAccessPolicy.cs` 746 lines. |
| ARC-F-14 Extensions/Mcp god classes | Open | `Extensions/Dbt/DbtMetadataIngestionService.cs` 1354, `Extensions/OpenMetadata/OpenMetadataSyncService.cs` 1168, `Application/Mcp/Services/AiDataGuardrailService.cs` 1049 lines, `HitLStepUpApprovalService.cs`. |
| ARC-S-11 engine god types | Open | `TrinoSqlEngine/Ast/Builder/SqlAstBuilder.cs` 1568 lines; `AstSecurityVisitor` in 7 partial files (1872 lines); `Ast/Generators/SqlDialectGeneratorBase*.cs` 656+616+480. |
| ARC-G-08 resolver service locators | Not re-verified in depth; treat as Open | Folded into WP-ARC-C-09; confirm the `= default!` `[Service]` parameters in `GraphQL/Types/QueryTypes.cs`/`MutationTypes.cs` before work. |
| ARC-C-11 forwarding API surface | Open | `Application/Services/GatewayExecutionService.cs:562-580` still forwards `EnsureInMemoryFilterIsEnforceable` / `FilterRows` / `IsTypeStrictRowFilter` to `InMemoryRowFilterEvaluator`; twin constructors at `:43` and `:90`. |

### T8 - API surface standardization

| ID | Status | Evidence |
|---|---|---|
| ARC-A-03 no shared error contract | Open | 25 `Results.Problem`/`ProblemDetails` hits vs ~209 ad-hoc `error =` shapes in `Endpoints/`, `Middleware/`, `Extensions/`. Exception text no longer reaches clients (SR-L-03/SR-L-23: interim `Endpoints/Support/SafeErrorResults.cs`, guard `tests/Autheris.Tests.Architecture/NoRawExceptionMessageInEndpointResponsesTests.cs`); WP-ARC-C-05 replaces the interim wrapper. |
| ARC-A-06 MCP caller placement | Partially resolved | Admin checks use `McpAdminTools.CanUseAdminTools`. Still open: `Mcp/GatewayMcpServer.cs:325` calls `McpEndpoints.ResolveCaller` (`McpEndpoints.cs:187`), and `EndpointSecurity` keeps separate admin predicates. |
| ARC-A-07 route conventions | Open | Mixed `/api/v1/...` and `/api/...`; SQL registered under several aliases (`WebSqlEndpoints.cs`, `SqlEndpointRoutes.cs`). |
| ARC-A-08 imperative authorization | Partially resolved | 22 `RequireAuthorization(GatewayPolicies.X)` sites now (e.g. `GovernanceClassificationEndpoints.cs`), but still 42 `HasRole`/`HasAnyRole` calls in Endpoints/Mcp/Middleware and 148 `RequireAuthorization(` in total. |
| ARC-A-09 inline pipeline lambdas | Open | 9 `app.Use(async ...)` in `Extensions/GatewayApplicationBuilderExtensions.cs`. |
| ARC-A-10 domain-scope rewrite after pipeline | Open | `GatewayApplicationBuilderExtensions.cs:378-390` rewrites `/graphql/{domain}` in `MapGatewayEndpoints`, after `UseGatewayPipeline` (`ReadOnlyTokenMiddleware` `:314`, `FinOpsBudgetMiddleware` `:321`). |
| ARC-A-13 service locator in handlers | Open | 43 `RequestServices.GetService*`/`context.Services` hits in `Autheris.Api`. |
| ARC-A-14 inline OpenAPI/HTML | Open | HTML/JS in `Endpoints/DevPortalEndpoints.cs` and `Endpoints/ODataEndpoints.cs`. |
| ARC-A-15 OData option parsing duplication | Open | `Endpoints/ODataEndpoints.cs` (796 lines) unchanged in structure. |
| ARC-A-16 missing CancellationToken | Open | 75 `WriteAsJsonAsync(` calls in `Autheris.Api`; re-count the ones without a token at implementation time. |
| ARC-A-17 static handler seams | Open | `Endpoints/EndpointSecurity.cs:19` `internal static class`; `InternalsVisibleTo` Unit+Integration in `Autheris.Api.csproj:44-45`. |
| ARC-A-18 manual endpoint list | Open | 35 `Map*Endpoints/Routes(` calls in `GatewayApplicationBuilderExtensions.cs`. |
| ARC-A-19 MCP base path twice | Open | `Endpoints/McpEndpoints.cs:51-53` (`TrimEnd('/')`) vs `GatewayApplicationBuilderExtensions.cs:339` `ResolveMcpBasePath` (used at `:129,316` and by `ParquetGraphQLResponseMiddleware.cs:53`). |

### T9 - Hot-path performance

| ID | Status | Evidence |
|---|---|---|
| ARC-S-13 cache key | Partially resolved | Hashing outside the lock, key material in `CompileCacheKey.Material` (`GovernedSqlCompiler.cs:129`). Still open: global `lock (_gate)` for Find/CountHit/CountMiss/Add/evict (`Ast/Emit/CompiledSqlTemplate.cs:122,162,175,180,187,200`), FIFO eviction, key material proportional to SQL size. |
| ARC-S-15 reflection AST walk | Open | `Ast/Security/AstReflection.cs` used by `DialectCapabilityValidator`, `DmlMaskedReadGuard`, `InsertCheckPolicy`, `AstSecurityVisitor.TypedDml/Tables`, `SecurityCoverageVerifier`, `Governance/PolicyPredicate.cs`, `PolicyExpressionParser.cs`, `TypedPolicyContext.cs`. |
| ARC-A-11 per-request catalog load | Open | `Endpoints/ODataEndpoints.cs:344` and `Endpoints/DuckDbOlapEndpoints.cs:174` call `GetAllTablesAsync` per request. |
| ARC-A-12 buffered egress | Open | `Endpoints/ArrowExportEndpoints.cs:185` `ExecuteQueryBufferedAsync`; `BoundedResponseBufferStream.cs:39` and `GatewayExtensibilityMiddleware.cs:173` copy with `ToArray()`. |
| ARC-I-06 fake streaming | Open | `SqlDataSourceExecutor` is gone (WP-ARC-B-02); its successor `Application/Services/SqlTableReadExecutor.cs:64` (`IDataSourceExecutor.ExecuteAsync`) still returns `IReadOnlyList<IReadOnlyDictionary<...>>`, and `Infrastructure/Connectors/SqlConnector.cs:130-141` `ReadSplitAsync` awaits the full `ReadBatchAsync` before yielding. |
| ARC-G-02 N+1 catalog change detection | Open | `GraphQL/Catalog/CatalogSchemaModel.cs:216` per-table `GetRelationsForTableAsync`; `CatalogGraphQlTypeModule.cs:68` rebuilds the whole model to compute a fingerprint. No `ICatalogVersionProvider`. |
| ARC-G-06 per-request resolver overhead | Open (low) | Not re-measured. |
| ARC-I-10 blocking startup I/O | Open (Redis/Garnet half) | Sync `ConnectionMultiplexer.Connect` in singleton factories (`Api/Extensions/DependencyInjection/GatewayStorageServiceExtensions.cs:67,81`), `StartServer()` at `:56` and `Program.cs:100`. |

### T10 - Engineering hygiene, tests, tooling

| ID | Status | Evidence |
|---|---|---|
| ARC-T-01 monolithic Unit project | Open | `tests/Autheris.Tests.Unit`: 518 files, ~112k LOC, references all 7 src projects plus `Tests.Common` (`Autheris.Tests.Unit.csproj:26-33`). |
| ARC-T-02 duplicated test doubles | Open | `tests/Autheris.Tests.Common` exists (`Eventually`, `StaticOptionsMonitor<T>`, `StubHttpMessageHandler`, `TempDirectory`), referenced by Unit and Integration, but the copies are not migrated: 8 files declare `StubEpochValidationService`, 7 declare `TestOptionsMonitor`. |
| ARC-T-03 wall clock / sleeps | Open | Tests: 62 `Task.Delay` lines, 696 `DateTime(Offset).(Utc)Now` lines; `src/` has 319 `UtcNow` sites vs 21 files using `TimeProvider`; no `Microsoft.Extensions.TimeProvider.Testing` in the test projects. `Thread.Sleep` is gone from Unit. |
| ARC-T-04 FS/loopback, no categories | Partially resolved | `[Trait("Category", ...)]` used 164 times; no convention per project, temp-path and localhost usage unchanged. |
| ARC-T-05 oversized/date-named test files | Open | 22 files named `*SecurityReview*`/`*Remediation*`. |
| ARC-T-06 legacy names in tests | Residual | `RlsListener`/`RewriteRls` names in `tests/TrinoSqlEngine.Tests/{AnalysisAndMaskingTests,SecurityReview20261002SqTests,SecurityRemediationTests,TrinoParserComplianceTests}.cs`, `Unit/Security/{CorrelatedRowFilterAliasTests,SecurityReviewRemediationCoverageTests}.cs`, `SqlCompilerGate/Harness/FrozenLegacy.cs`. Folded into WP-ARC-C-19. |
| ARC-T-07 serialized suites | Open | `tests/Autheris.Tests.Integration/xunit.runner.json` and `tests/Autheris.Tests.SqlCompilerGate/xunit.runner.json` set `maxParallelThreads: 1`. Speed work is tracked in [`impl-test-suite-speedup.md`](impl-test-suite-speedup.md) (WP-TS-05). |
| ARC-T-08 architecture tests thin | Partially resolved | `tests/Autheris.Tests.Architecture` grew to 15 files / ~2.2k LOC (audit, seams, `NoNewFastSqlEngineInSrc`, Null test doubles, raw exception text). `tests/Autheris.Tests.Unit/ArchitectureAndCleanCodeTests.cs` still in Unit. |
| ARC-L-08 arch test gaps | Open | `ArchitectureTests.cs` has no HotChocolate / Prometheus / `Microsoft.Data` / `GatewayOptions` / `IServiceProvider` rule. |
| ARC-L-09 arc42 stale | Not verified line by line; treat as Open | `docs/architecture/arc42.md` quality-gate text and "Hot Chocolate isolated" claim. |
| ARC-L-10 two solution files | Open | `Autheris.sln` and `Autheris.slnx` (23 lines, no `SqlCompilerGate`) both present; CI uses `Autheris.sln` (`.github/workflows/ci.yml:29,42`). Decision G-10: delete `.slnx`. |
| ARC-L-13 plan/doc governance | Partially resolved | Still open: `doc` symlink at repo root, `docs/plan` next to `docs/plans`, `docs/architecture/konzept-data-governance-und-mcp-documentation.md` (German filename). |
| ARC-L-15 process-tag naming | Open | `Api/Extensions/Plan9ServiceExtensions.cs` (`AddPlan9Services`, `Program.cs:70`). |
| ARC-L-17 CA suppressions | Open | `Directory.Build.props:16` NoWarn lists 23 CA/CS codes incl. correctness-relevant `CA1816`, `CA2012`. |
| ARC-S-10 engine not standalone | Open | `TrinoSqlEngine.csproj:36-40` `InternalsVisibleTo` for `TrinoSqlEngine.Tests`, `Autheris.Tests.Unit`, and (unconditionally, for `ICompilerHooks`) `Autheris.Application`/`Autheris.Infrastructure`; PackageId `FastSqlEngine`; meter name `Autheris.SqlCompiler` (`CompilerTelemetry.cs:12`). |
| ARC-S-12 `IReadOnlyList` in AST records | Open | `Ast/Nodes/Clauses.cs:5,35-36` and siblings. |
| ARC-S-16 build tooling / stale identity | Open | `TrinoSqlEngine.csproj:8-9` PackageId/Product `FastSqlEngine`, German comment `:13`, `LangVersion 14` (`:5`) vs C# 13 guidance in CLAUDE.md. |
| ARC-I-09 dual telemetry | Open | prometheus-net `Metrics.Create*` in 10 files (`RedisClusterStateProvider`, `RedisIdempotencyStore`, `ConsentCacheService`, `RedisTokenRevocationService`, `RedisRateLimiterService`, `SqlCompilerErrorMapper`, `RateLimitingMiddleware`, `BasicAuthenticationHandler`, `Infrastructure/Diagnostics/GatewayDiagnostics`, ...); no OTel Prometheus exporter. |
| ARC-I-11 diagnostics facade | Open | `Infrastructure/Diagnostics/GatewayDiagnostics.cs` still exists next to the Domain class. |
| ARC-G-04 GraphQL pipeline in Api | Open | No `AddAutherisGraphQL`; pipeline still in `GatewayServiceCollectionExtensions.cs:549` (`AddGatewayGraphQL`, `Program.cs:69`). |
| ARC-G-05 dead/demo GraphQL code | Open | `GraphQL/DynamicTypes/DynamicTableType.cs` and `GraphQL/Loaders/InvoiceItemDataLoader.cs` present. |
| ARC-F-12 ad-hoc HttpClient | Open | `Application/Catalog/Services/CatalogDiscoveryService.cs:321-327` builds handler + `new HttpClient` per call. |
| ARC-F-13 ITSM split across projects | Open | `Extensions/Itsm`, `Application/Workflows/ItsmWorkflowDispatcher.cs`, `Infrastructure/Itsm/*HostedService.cs`. |
| ARC-F-18 hosted service patterns | Open | Several loop styles (`PeriodicTimer` in `Extensions/OpenMetadata/OpenMetadataSyncBackgroundService.cs:45`, `Task.Delay` loops elsewhere). No shared base class. |
| ARC-C-19 German comments / magic constants | Open | German comments remain mainly in `Autheris.Domain` (e.g. `Model/CatalogSearchModels.cs`, `Model/GovernanceV2Models.cs:66`, `Options/CatalogSearchOptions.cs`, `Diagnostics/GatewayDiagnostics.cs:55`, `Audit/AuditCanonicalizer.cs:8`) and `TrinoSqlEngine.csproj:13`; magic defaults as listed in the review. |

---
## 2. Work packages

Effort scale: S (<= 1 day), M (2-3 days), L (4-6 days), XL (> 6 days, split into sub-batches). Every WP follows TDD (red test first), then the Phase 3 security review where marked, and uses `dotnet build Autheris.sln` + `dotnet test` (per project) plus a Release build with warnings as errors before push. Parallel developer agents implement only; the lead compiles once at the end of a batch.

### Wave R4 - Structure and API decoupling

#### WP-ARC-C-01 GatewayOptions physical split and consumption ratchet
- **Covers:** ARC-L-01 (phase 1).
- **Goal:** Make the option classes individually addressable and stop the growth of `IOptions<GatewayOptions>` usage without changing behaviour.
- **Design:**
  1. Move each of the 78 nested option classes out of `src/Autheris.Domain/Options/GatewayOptions.cs` into `src/Autheris.Domain/Options/<Area>/<Name>Options.cs` (Authentication, Caching, Audit, Mcp, GraphQL, DataSources, Catalog, Lakehouse, WebSql, Federation, ...), keeping namespace `Autheris.Domain.Options` so no using changes. `GatewayOptions` keeps only the root properties and `SectionName`. Pure move; `git mv`-style diffs reviewed with `--color-moved`.
  2. Add `Autheris.Domain.Options.GatewaySections` (static, constants for every section path: `"Gateway:Mcp"`, `"Gateway:GraphQL"`, ...) so binding keys are not magic strings.
  3. Add `GatewayOptionsServiceCollectionExtensions.AddGatewaySection<TOptions>(string path)` in `src/Autheris.Api/Extensions/` registering `AddOptions<TOptions>().Bind(configuration.GetSection(path)).ValidateOnStart()`. Because `DevConfiguration.Apply` mutates the `IConfiguration` before binding (`Program.cs:59`), section bindings see the same dev-preset values as the root binding; no `PostConfigure<GatewayOptions>` exists in `src/` (verified), so there is no ordering drift.
  4. Architecture test `GatewayOptionsConsumptionRatchetTests` in `tests/Autheris.Tests.Architecture`: asserts the number of types with a constructor parameter or field of type `IOptions<GatewayOptions>`/`IOptionsMonitor<GatewayOptions>`/`GatewayOptions` is `<= N` (N written into the test, lowered by each WP-03 batch). New classes may not take `GatewayOptions` (allowlist: composition roots, validators).
- **Affected files:** `GatewayOptions.cs` (split), new files under `Options/<Area>/`, `GatewayServiceCollectionExtensions.cs`, new ratchet test. Hot file: `GatewayOptions.cs` is touched by every feature branch that adds a setting.
- **Migration/compat:** No config-key changes, no binary changes for consumers (same namespace/assembly). Source-compatible. Merge conflicts for open branches editing `GatewayOptions.cs`: announce a freeze window and merge this WP first thing, when few branches are open (see sequence).
- **Security:** None directly. Keep `[Required]`/DataAnnotations attributes on each class during the move so `ValidateDataAnnotations` still covers them (test below).
- **TDD:** `tests/Autheris.Tests.Unit/Options/GatewayOptionsBindingParityTests`: bind a fixture `appsettings` containing every section; red test asserts that `IOptions<GatewayOptions>` and `IOptions<McpOptions>` (via `AddGatewaySection`) yield equal values for each section (written first against the not-yet-existing helper). Plus `GatewayOptionsConsumptionRatchetTests` (red until baseline N is set).
- **Acceptance:** `GatewayOptions.cs` < 300 lines; no behaviour change (full Unit + Integration green); ratchet test present with baseline 295/8.
- **Effort:** M. **Risk:** Low (mechanical), high merge-conflict exposure.

#### WP-ARC-C-02 Options validation consolidation
- **Covers:** ARC-L-07.
- **Goal:** One validation mechanism, no disk I/O in option predicates, no second binding.
- **Design:**
  1. Replace the ~24 `.Validate(lambda)` in `GatewayServiceCollectionExtensions.cs:94-180` with `IValidateOptions<T>` classes under `src/Autheris.Api/Configuration/Validation/` (`CasbinOptionsValidator`, `AuthenticationOptionsValidator`, `HighAvailabilityOptionsValidator`, ...), each with a stable failure message used by existing tests (messages are asserted in current tests; keep text).
  2. File-system checks (`Casbin.ModelPath`, `Casbin.PolicyPath`, `Plugins.Directory`) move behind `IFileSystemProbe` (new, `Autheris.Application` port or Api-internal) so validators are pure and testable; the real probe is registered in the composition root. Validation still runs under `ValidateOnStart()` so a missing file still fails startup (fail-closed unchanged).
  3. Fold `GatewayStartupValidator.ValidateStartup` rule groups into the same `IValidateOptions` classes where they only need the options object; rules needing `IHostEnvironment`/environment variables become validators taking those via constructor. `GatewayStartupValidator` stays only for cross-cutting pre-bind checks (`ValidateSqlRewriterEngine`, `ValidateRemovedWebSqlSettings`, legacy keys) that operate on raw `IConfiguration`.
  4. Eliminate the second binding (`Get<GatewayOptions>()` at `:190`): `AddGatewayOptions` still returns a snapshot (needed for conditional registration before the container exists), but it is produced by the same `Configure` pipeline (`new GatewayOptions()` + `configuration.GetSection(...).Bind(...)`) through one private method used by both the options builder and the snapshot, and a unit test asserts snapshot equals `IOptions` value.
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
- **Design:** Migrate in vertical batches, one area per PR, each batch lowering the ratchet baseline from WP-01. Order by consumer count and independence: (1) `Mcp` + `Casbin`, (2) `GraphQL` + `RateLimiting`, (3) `Audit` + `DataMasking`, (4) `Caching`/`Redis`/`Garnet`, (5) `DataSources`/`Catalog`/`Lakehouse`, (6) `WebSql`/`SqlEndpoints`/`ProcedureEndpoints`/`CrossSource`, (7) `Authentication` (last: widest blast radius, touches the security pipeline), (8) remaining. For each area: register the section (`AddGatewaySection`), replace constructor injection, update tests to `Options.Create(new McpOptions{...})`. Where a service needs several sections it takes several `IOptions<T>` (a sign it needs splitting, noted in the PR, not fixed here). Raw `GatewayOptions` fields held by services (`TableAccessPolicy.cs:98`, `McpProtocolHandler.cs:28`, `AuditLineageEgressInterceptor.cs:11`) are migrated in the matching batch. Hot-reload semantics: services that currently use `IOptionsMonitor<GatewayOptions>` use `IOptionsMonitor<SectionOptions>`; sections bound through `Bind(IConfigurationSection)` reload with the config provider, same as before.
- **Affected files:** about 295 injection sites across Application/Infrastructure/Api/GraphQL/Extensions plus their tests.
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
  3. ARC-A-19: one `McpPathResolver.Resolve(GatewayOptions)` (new, `Api/Mcp/`), used by `McpEndpoints.cs:51`, `GatewayApplicationBuilderExtensions.cs:129,316,339`, `ParquetGraphQLResponseMiddleware.cs:53`; normalisation: leading slash, no trailing slash, fallback `/mcp`.
  4. ARC-A-18: introduce `IEndpointModule { void Map(IEndpointRouteBuilder app); }` with constructor-free modules discovered from a single explicit list (explicit, not assembly scanning, to keep removal of an endpoint visible in review); uniform signature replaces `(app)`, `(app, gatewayOptions)`, `(app, gatewayOptions, env)`; modules receive `IOptions` from `app.ServiceProvider`. Migrate the 35 calls module by module, behaviour unchanged. Rename `*EndpointRoutes` to `*Endpoints` consistently.
  5. ARC-L-15: rename `AddPlan9Services` to `AddAsyncJobs` + `AddDatasourceTesting` (split by capability), drop `AP-9.x` tags from comments, fix `Program.cs` step numbering.
  6. ARC-L-11 (Api side only): collapse the three near-identical provider blocks in `GatewayGovernanceServiceExtensions.cs:37-95` into a loop over a (dialect -> repository registration) table; replace manual `new` factories in `GatewayExecutionServiceExtensions.cs:79-97,113-116` with plain registrations. The per-project `AddAutherisApplication()/AddAutherisInfrastructure()` extraction is deliberately deferred until WP-ARC-B-10 settles the Application/Infrastructure boundary; this WP only prepares call sites.
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
  1. New `Autheris.Api.Errors.ApiProblems` static factory returning `IResult` (`application/problem+json`): `Forbidden(code)`, `NotFound(code)`, `BadRequest(code, errors)`, `Unauthorized`, `TooManyRequests(retryAfter)`, `Conflict`, `Unprocessable`. Each sets `type = https://docs.autheris.dev/errors/<code>` (URI constant), `title`, `status`, `extensions["code"]` (stable string from `ApiErrorCodes`, a `const string` catalog in `Autheris.Domain` or Api), and `traceId`. `detail` is always a fixed generic text from the catalog; no exception messages (the SR-L-03 / SR-L-23 leakage rules already hold through the interim `SafeErrorResults`; this WP replaces that wrapper as the single place to enforce them and retires the baseline allow-list of `NoRawExceptionMessageInEndpointResponsesTests`).
  2. Replace the ~209 `{ error = ... }` shapes and the bodiless 403/`StatusCode` returns endpoint file by endpoint, with `Results.Problem`-compatible output. Bodiless 403s become `ApiProblems.Forbidden(ApiErrorCodes.AccessDenied)` (status unchanged, body added).
  3. OData endpoints keep the OData envelope (`{"error":{"code","message"}}` with `odata.metadata=minimal`) because clients depend on it; centralise it in one `ODataError(...)` (already exists at `ODataEndpoints.cs:608`) and remove the remaining hand-built copies (ARC-A-15; re-locate them, the old `:402-433` reference predates Wave 3). Document this as the one sanctioned exception.
  4. WebSQL/Trino statement protocol (`/v1/statement`) must keep Trino JSON error format; sanctioned exception, mapped in `WebSqlEndpoints` error mapper (`SqlErrorResponse.cs`).
  5. MCP errors remain JSON-RPC errors; sanctioned exception.
  6. `GatewayExceptionHandler`/`GatewayDenialStatusMapper` become the only translation of `GatewaySecurityException` and are called by handlers that currently catch-and-map manually.
  7. Architecture test `ErrorContractTests`: no new `error =` anonymous object in `Autheris.Api/Endpoints` outside the allowlist files (OData, WebSQL/Trino, MCP); ratchet number for existing ones lowered per batch.
- **Migration/compat (breaking):** Clients parsing `{ "error": "..." }` break. Mitigation: for one release emit ProblemDetails with an additional extension member `error` carrying the same legacy string, controlled by `Gateway:Api:LegacyErrorField` (default `true` in the first release, flipped to `false` the next, then removed). Status codes do not change. Add to release notes and `docs/` API reference.
- **Security:** Fail-closed: error bodies never include exception text or internal identifiers; `ProblemDetails.Extensions` whitelisted keys only. Phase 3 review.
- **TDD:** `ApiProblemsTests` (shape, content type, code, no detail leakage given an exception message with a secret), then per-endpoint-group tests asserting `application/problem+json` and `code` (red against the current `{error}`), starting with `GovernanceEndpoints`, `VirtualFilterEndpoints`, `DuckDbOlapEndpoints`, `ArrowExportEndpoints`, then the rest. Integration test: unauthenticated and forbidden calls to a sample of 10 routes return problem+json.
- **Acceptance:** `rg 'new\s*\{\s*error\s*='` in `Autheris.Api/Endpoints` matches only allowlisted files; every non-2xx from `Autheris.Api` (except sanctioned protocols) is `application/problem+json` with `code`.
- **Effort:** L-XL (many files, mechanical). **Risk:** Medium (client compatibility). **Serialization:** touches nearly every endpoint file; conflicts with WP-06, -07, -08. Run batches per endpoint file group, serialized against those WPs (see sequence); batches in disjoint files may be parallelised.

#### WP-ARC-C-06 Declarative authorization policies and MCP caller placement
- **Covers:** ARC-A-08, ARC-A-06 (remaining part).
- **Goal:** Authorization stated at route registration through named policies; handlers no longer call `HasRole`.
- **Design:**
  1. Extend `Api/Security/GatewayPolicies.cs` with a policy per role requirement actually used by the 42 `HasRole/HasAnyRole` calls (GovernanceAdmin, ClusterAdmin, TenantAdmin, Approver, PrivacyAdmin, SchemaAdmin, SchemaPublisher already exist as constants; add the missing combinations as named policies, e.g. `GovernanceOrClusterAdmin`). Policies use a requirement + `AuthorizationHandler` that call the strict role parser already on `main` (`GatewayRoleEvaluator`, SR-L-04). This WP must not re-implement role parsing.
  2. Replace `RequireAuthorization()` on admin groups with `RequireAuthorization(GatewayPolicies.X)` at the `MapGroup` level; delete in-handler role checks only after a test proves the policy rejects the same callers.
  3. `EndpointSecurity` (static, `Endpoints/EndpointSecurity.cs`) keeps tenant/caller helpers but its admin predicates are replaced by the policy handlers; MCP: introduce `IMcpCallerResolver` (`Api/Mcp/McpCallerResolver.cs`) used by both `McpEndpoints` and `GatewayMcpServer`, removing the dependency of `GatewayMcpServer.cs:325` on `McpEndpoints.ResolveCaller`.
  4. Authorization-matrix test `EndpointAuthorizationMatrixTests` (Integration): enumerates `EndpointDataSource`, and asserts every endpoint has either a named policy, `RequireAuthorization()` explicitly on an allowlist of "any authenticated" routes, or is in the `AllowAnonymous` allowlist (health, well-known, dev endpoints only when dev preset). New endpoints without a decision fail the test. `DevEndpoints` group-level `.AllowAnonymous()` (`DevEndpoints.cs:30`) is checked against the dev-only registration.
- **Migration/compat:** HTTP behaviour stays 401/403; the 403 body now comes from the authorization middleware result handler (customise via `IAuthorizationMiddlewareResultHandler` to emit ProblemDetails, aligned with WP-05).
- **Security (fail-closed):** Policy defaults deny; a missing policy name throws at startup (ASP.NET behaviour for unknown policy at request time is an exception, so a startup test enumerating policy names used by endpoints against registered policies is required). Phase 3 mandatory. Audit events for denied access must still be written (today the handler writes the denial audit; the result handler must keep emitting `AccessDenied` audit events, tested).
- **TDD:** red first: `EndpointAuthorizationMatrixTests` (fails for the 42 imperative-check endpoints because they carry no policy metadata), then per-policy `GovernancePoliciesTests` with claims fixtures.
- **Acceptance:** `rg "HasRole|HasAnyRole" src/Autheris.Api/Endpoints` returns only the policy handlers; matrix test green.
- **Effort:** L. **Risk:** Medium-High (authorization regression).

#### WP-ARC-C-07 Endpoint decomposition, embedded assets, service-locator removal
- **Covers:** ARC-A-04, ARC-A-13, ARC-A-14, ARC-A-15, ARC-A-16, ARC-A-17.
- **Goal:** No endpoint file over ~400 lines; handlers receive dependencies by parameter binding; static HTML/JS out of C# strings.
- **Design:**
  1. Split by feature under feature folders with one `MapGroup` per resource: `Endpoints/WebSql/` (`WebSqlEndpoints`, `TrinoStatementEndpoints`, `WebSqlOpenApiSpec`, `WebSqlErrorMapper`, `JsonRowWriter`), `Endpoints/Governance/` (ingestion, simulation, sunsetting, differential privacy as separate modules), `Endpoints/VirtualFilters/`, `Endpoints/OData/` (`ODataEntitySetHandler`, `ODataCountHandler`, shared `ODataQueryOptions` parser used by both, replacing the duplicated parsing and `headers.ToDictionary` copies at `:480,:532`), `Endpoints/DevPortal/`.
  2. ARC-A-14: move the Swagger UI HTML (`ODataEndpoints.cs:719-794`), DevPortal templates (`DevPortalEndpoints.cs:223-703`) and hand-built OpenAPI JSON (`WebSqlEndpoints.cs:610-755`) to embedded resources under `src/Autheris.Api/Resources/` with an `EmbeddedResourceReader`; keep generated text byte-identical (golden tests compare rendered output before/after).
  3. ARC-A-13: bind services in handler signatures (`IOptions<ArrowExportOptions> o, ILoggerFactory lf`), delete `RequestServices.GetService` calls (43 sites); for middleware, constructor/`InvokeAsync` injection. `SecurityContextResolutionMiddleware.cs` gets its optional services through its constructor (null-object when feature disabled), not per-request `GetService`. Add `ServiceLocatorArchitectureTests`: no `GetService`/`GetRequiredService` on `HttpContext.RequestServices` in `Autheris.Api` except an allowlist.
  4. ARC-A-16: bind `CancellationToken ct` in handlers and pass it to the `WriteAsJsonAsync` calls without a token (re-count; 75 calls in total); no behaviour change.
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
- **Migration/compat (breaking at removal):** Aliases remain for at least two releases; per Gate 0 decision G-9. Authorization policies (WP-06) are attached at group level so alias and canonical routes share them (security test: alias requests are denied for the same callers as canonical ones).
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
  - ARC-C-11: delete `GatewayExecutionService.FilterRows/EnsureInMemoryFilterIsEnforceable/IsTypeStrictRowFilter` forwarders (`:562-580`) and call `InMemoryRowFilterEvaluator` directly; collapse twin constructors in `GatewayExecutionService`, `GovernedSqlExecutionService`, `GovernedProcedureExecutionService` to one DI constructor each (test-only constructors replaced by test builders in WP-17; the `Null*` fallbacks are already gone, WP-ARC-A-05).
  - ARC-F-14: split `Extensions/Dbt/DbtMetadataIngestionService`, `Extensions/OpenMetadata/OpenMetadataSyncService` into parse/map/persist/publish; `AiDataGuardrailService` into detector/policy/auditor; `HitLStepUpApprovalService` into locking/persistence/notification. One type per PR.
  - ARC-G-08: in `GraphQL/Types/QueryTypes.cs`/`MutationTypes.cs` remove `= default!` `[Service]` parameters and alias-only overloads (verify current state first; ARC-G-01 workflow extraction belongs to the R1 pipeline work).
- **Migration/compat:** Internal refactoring; public interfaces unchanged; `InternalsVisibleTo` tests that call private members need adjusting.
- **Security:** Casbin is the authorization core: refactor must be behaviour-preserving, protected by the existing Casbin suites (`CasbinAbacPropertyTests`, `CasbinSnapshotImmutabilityTests`, `CasbinHotReloadTests`, `CasbinRlsPushdownTests`) plus a characterization test written first (snapshot of decisions over a policy x request matrix, recorded from the current implementation). Phase 3 review for Casbin. Fail-closed: a snapshot-build failure must keep the last good snapshot or deny (existing behaviour, assert it).
- **TDD:** characterization tests (green on old code) first, then extraction in small steps keeping them green.
- **Acceptance:** `CasbinEnforcementService.cs` < 600 lines; forwarders deleted; Casbin matrix test unchanged and green.
- **Effort:** XL (L for Casbin alone). **Risk:** Medium-High for Casbin. **Serialization:** the Casbin decision cache is also changed by WP-ARC-A-12 (ARC-C-07, cluster invalidation). Do not run WP-09's Casbin part concurrently with A-12; sequence A-12 first or implement both in one branch under one owner. Non-Casbin parts are parallelisable.

#### WP-ARC-C-10 TrinoSqlEngine structural decomposition
- **Covers:** ARC-S-11, ARC-S-12.
- **Goal:** Smaller engine types with explicit passes; value-equal AST records.
- **Design:** Split `AstSecurityVisitor` partials into composable passes (`RowFilterInjector`, `MaskProjector`, `DmlGuardPass`) sharing a `SecurityScope`; split `SqlAstBuilder` (1498 lines) into statement/expression/table-source builders; replace `IReadOnlyList<T>` in AST records (`Ast/Nodes/Clauses.cs`, `Expressions.cs`, `SqlStatement.cs`, `TableSources.cs`) with `ImmutableArray<T>` (or an `EquatableArray<T>` wrapper to keep structural equality). Add/keep the exhaustiveness test (reflection over `SqlNode` subclasses vs generator/rewriter/verifier handlers, partially present in `SecurityCoverageTests`).
- **Migration/compat:** Public AST node signatures change (`ImmutableArray`); consumers in Application/Infrastructure (`PolicyPredicateRenderer`, compile callers) recompile. Plan the change as one atomic PR across the solution.
- **Security:** The AST security visitor is the RLS/masking boundary. Gate suites (`Autheris.Tests.SqlCompilerGate`, `TrinoSqlEngine.Tests` `Category=SqlGate`, mutants via `CompilerTestSeams`) must be green in Debug and Release. Phase 3 mandatory. No behaviour change: golden SQL outputs (`CatalogTypeMapGoldenTests` etc.) byte-identical.
- **TDD:** Add value-equality test for records first (red with `IReadOnlyList`), exhaustiveness test, then refactor.
- **Acceptance:** no engine type > 800 lines; equality tests green; Gate green in both configurations.
- **Effort:** L-XL. **Risk:** High (security boundary). **Serialization:** shares `TrinoSqlEngine` with WP-12, -20; run strictly after WP-12 or before, never concurrently (same hot files: `AstSecurityVisitor*.cs`, `CompiledSqlTemplate.cs`, `FastSqlEngine.cs`).

### Wave R5 - Hot-path performance

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
- **Design:** `SqlDataSourceExecutor` was replaced by the governed `SqlTableReadExecutor` (WP-ARC-B-02), which runs planned table reads through `IGovernedSqlExecutionService`. Add a streaming variant along that path: `IAsyncEnumerable<...> StreamRowsAsync(...)` on the governed execution service (yield inside the `await using var reader` scope; `CommandBehavior.SequentialAccess` retained) and on `SqlTableReadExecutor`; `ExecuteAsync` (`SqlTableReadExecutor.cs:64`) becomes a thin collector over the stream so existing callers keep working; `SqlConnector.ReadSplitAsync` (`Infrastructure/Connectors/SqlConnector.cs:130`) delegates to the stream (no up-front `ReadBatchAsync`). Per-row byte accounting and row-limit ceilings apply while streaming. Masking, row-level filters and column denial run in the compiled SQL before the row is yielded. A rows-as-`object?[]` + schema representation for the streaming consumers (Arrow/Parquet) is introduced in WP-14; dictionary rows stay for existing small-result paths.
- **Migration/compat:** New interface members with default implementations or a new interface `IStreamingConnectorRecordSource` to avoid breaking plugins (`IConnectorRecordSource` is a plugin-facing contract).
- **Security (fail-closed):** (a) Governance must complete (access decision, compile, mask plan) before the first row is read; (b) mid-stream failure aborts the reader and the consumer receives an error, never a silently truncated successful result; (c) limits are enforced during streaming, not after; (d) cancellation disposes the DB reader. The query audit (`IAuditWriter`, WP-ARC-A-06) is written before streaming starts; a completion/abort event is appended when the stream ends.
- **TDD:** red: `SqlConnectorStreamingTests` (first row yielded before the reader completes: fake `DbDataReader` that throws if read beyond row 1 before the first yield is consumed; limit exceeded mid-stream aborts; cancellation disposes reader); memory benchmark with 1M synthetic rows.
- **Acceptance:** time-to-first-row independent of result size; peak memory for 1M-row export bounded by batch size; behaviour of existing small queries unchanged.
- **Effort:** L. **Risk:** Medium. **Dependency:** WP-ARC-B-10 moves ADO.NET execution into `Autheris.Infrastructure.Analytics`; see decision 3 in Section 5.

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
  1. `ICatalogVersionProvider.GetVersionAsync(ct)` (Application port) with implementation reading `max(updated_at)` + `count(*)` from the catalog tables (no schema change; if a migration adds a version counter column, switch implementation later). `CatalogGraphQlTypeModule.CheckForCatalogChangesAsync` compares versions and rebuilds the model only on change (`CatalogGraphQlTypeModule.cs:56-100`); publish `catalog:changed` on `IEventBus` from catalog writes to drive immediate rebuild (the timer stays as fallback). Add `ITableRelationRepository.GetRelationsAsync(IEnumerable<TableIdentifier>)` bulk method; replace the per-table loop in `CatalogSchemaModel.cs:216` (all three governance repositories need the bulk query: touch the three dialect partials minimally; coordinate with WP-ARC-B-14, which splits these repositories).
  2. ARC-A-11: `ICatalogLookup` (cached `ConcurrentDictionary` per tenant keyed by entity name, invalidated on `catalog:changed`) replaces `GetAllTablesAsync` per request in `ODataEndpoints.cs:344` and `DuckDbOlapEndpoints.cs:180`; tenant resolved only via `EndpointSecurity.GetRequestTenant`. Cache entries are tenant-scoped; lookup miss means 404 (fail-closed), never a fallback to another tenant.
  3. ARC-G-04: move the GraphQL request-pipeline builder from `Api/Extensions/GatewayServiceCollectionExtensions.cs` (`AddGatewayGraphQL`) into `Autheris.GraphQL` as `AddAutherisGraphQL(GraphQLOptions, ...)`, with a unit test asserting middleware order (`CostAndQuotaMiddleware` after variable coercion and before execution). Depth/cost limits expressed as one `QueryLimits` record consumed by GraphQL (OData/REST adoption of the record is out of scope, noted).
  4. ARC-G-05: delete `GraphQL/DynamicTypes/DynamicTableType.cs`; move `InvoiceItemDataLoader`/`InvoiceTypes` to a `Demo` folder behind a conditional DI extension (`demoDataEnabled`).
  5. ARC-G-06: pass headers lazily (`IHeaderDictionary` wrapper) in `CatalogGraphQlTypeModule.cs:484-489`, remove the second list copy (`:512-546`), and order `CostAndQuotaMiddleware` after the operation resolver so the document is parsed once.
- **Security:** Catalog cache must honour tenant isolation and visibility (a table hidden by policy must not become resolvable through the cache); invalidation failures (event lost) must fall back to a short TTL, not permanent staleness. Phase 3 review for the lookup cache and the GraphQL pipeline move (security middleware order).
- **TDD:** red: `CatalogVersionProviderTests` (no rebuild when unchanged; N tables cause 1 relations query), `ODataCatalogLookupTests` (no `GetAllTablesAsync` per request; tenant A cannot resolve tenant B entity), `GraphQlPipelineOrderTests`.
- **Acceptance:** governance DB queries per refresh tick constant instead of N; OData flat route does not scan the catalog; `DynamicTableType` removed.
- **Effort:** L. **Risk:** Medium. **Dependencies:** WP-ARC-B-14 (repository split) touches the same repository partials; coordinate rebases.

#### WP-ARC-C-16 Startup I/O and telemetry unification
- **Covers:** ARC-I-10 (Redis/Garnet connect), ARC-I-09, ARC-I-11.
- **Design:**
  1. ARC-I-10: `IRedisConnectionProvider` backed by `ConnectionMultiplexer.ConnectAsync` initialised in an `IHostedService` (`RedisConnectionInitializer`, started before readiness; readiness check reports "connecting"), replacing `AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(...))` (`GatewayStorageServiceExtensions.cs:67,81`) and moving `StartServer()` (`:56`, `Program.cs:100`) into the same initializer for the embedded Garnet case. Consumers that need the multiplexer get a `Lazy`/async accessor; consumers whose failure semantic matters (absent vs unavailable, WP-ARC-A-09) get `ClusterStateUnavailableException` semantics, not a null multiplexer. Governance schema DDL already runs through the versioned migrations (WP-ARC-B-12).
  2. ARC-I-09: standardise on `System.Diagnostics.Metrics` (`IMeterFactory`-created instruments); migrate the prometheus-net statics (10 files with `Metrics.Create*`, listed in the T10 table) keeping metric names and labels; expose via `OpenTelemetry.Exporter.Prometheus.AspNetCore` on `/metrics` (replaces `MapMetrics()` at `GatewayApplicationBuilderExtensions.cs:364`; HTTP metrics via OTel ASP.NET instrumentation or keep `UseHttpMetrics` until metric-name parity is proven). Remove `prometheus-net` package from Application/Infrastructure (also satisfies the B-10 package-boundary rule).
  3. ARC-I-11: delete `Infrastructure/Diagnostics/GatewayDiagnostics.cs`; callers use the Domain class (or injected metrics from item 2).
- **Migration/compat:** `/metrics` output names must stay identical (dashboards); verified by a scrape-diff test. New package `OpenTelemetry.Exporter.Prometheus.AspNetCore` (pre-release flag check; packages are pinned via lock files). Garnet start timing change: readiness endpoint reflects initialisation.
- **Security:** `/metrics` exposure/authorization unchanged (verify auth middleware still guards it; no new anonymous endpoint). Redis connect failure keeps fail-closed semantics for rate limiting and revocation (fail-closed defaults from SR-L-10/G-3); this WP must not turn an unavailable Redis into "no limits".
- **TDD:** red: `RedisConnectionInitializerTests` (no sync connect during DI resolution: resolving the provider does not block or throw when Redis is down; readiness false until connected), `MetricsParityTests` (names/labels of migrated instruments match a golden list), `NoPrometheusNetInApplicationTests` (architecture).
- **Acceptance:** no `ConnectionMultiplexer.Connect(` sync call in factories; one metrics pipeline; arch test green.
- **Effort:** M-L. **Risk:** Medium (readiness/startup ordering, dashboard compatibility). Shares `GatewayStorageServiceExtensions.cs` with WP-ARC-A-11..A-14; coordinate.

### Wave R6 - Test and build hygiene

#### WP-ARC-C-17 Shared test foundation (remaining part), traits, banned APIs
- **Covers:** ARC-T-02, ARC-T-04. Delivered: `tests/Autheris.Tests.Common` with `Eventually`, `StaticOptionsMonitor<T>`, `StubHttpMessageHandler`, `TempDirectory`, referenced by Unit and Integration.
- **Design (remaining):** add `FakeEpochValidationService` and a `FakeTimeProvider` re-export (`Microsoft.Extensions.TimeProvider.Testing`) to Common. Delete the 8 `StubEpochValidationService` and 7 `TestOptionsMonitor<T>` copies. Add a trait convention `[Trait("Category","Unit|Integration|Docker|Slow")]` (xunit `ITraitAttribute` discoverer or per-folder base); CI lanes use `--filter`. Introduce `BannedApiAnalyzers` `BannedSymbols.txt` for test projects (`Thread.Sleep`, unjustified `Task.Delay`, `DateTime.UtcNow`/`DateTimeOffset.UtcNow` in new tests) as ratcheting warnings with a baseline file. Integration parallelism (`[Collection]` groups instead of `maxParallelThreads: 1`) is owned by WP-TS-05 in [`impl-test-suite-speedup.md`](impl-test-suite-speedup.md).
- **Migration/compat:** test-only.
- **Security:** none; keep test doubles fail-closed by default (fakes deny unless configured).
- **TDD:** the architecture test `NoDuplicateTestDoubles` (names `Stub*Epoch*`, `TestOptionsMonitor*` declared outside Common) is the red test; migrate until green.
- **Acceptance:** zero duplicated doubles; every test class carries a category; CI lanes selectable by filter.
- **Effort:** M. **Risk:** Low. **Order:** must precede WP-19 (split) because moved tests should consume Common, not recreate doubles.

#### WP-ARC-C-18 TimeProvider adoption and deterministic timing
- **Covers:** ARC-T-03, part of ARC-C-19 (magic timing constants).
- **Design:** Inject `TimeProvider` into time-dependent services (cache TTLs, token/session expiry, plan cache, rate limiters, hosted-service schedulers, approvals); register `TimeProvider.System` once in the composition root. Priority list from tests that sleep or compare wall clock: `Application/Sql/CompiledSqlQueryPlanCache` , Casbin hot reload (`CasbinHotReloadTests.cs`), chunked executors, FinOps budgets, epoch validation, consent expiry. Replace `Task.Delay` polling in tests by `FakeTimeProvider.Advance` or `Eventually`. Ratchet architecture test `NoNewUtcNowInServices`: types registered as services may not call `DateTime.UtcNow`/`DateTimeOffset.UtcNow` outside an allowlist (starting from the current 319 sites, lowered per batch; migrate service classes first, DTO/entity factories last). Time-dependent security checks (token expiry, replay windows) migrate first: they become testable without sleeps and are tamper-resistant against skew bugs.
- **Migration/compat:** internal; constructors gain a `TimeProvider` parameter (default `TimeProvider.System` only on factory/test helpers, not in production constructors, to avoid hidden fallbacks).
- **Security:** Behaviour must not change for expiry boundaries: add boundary tests (`expiresAt == now` is expired = fail-closed) for each migrated component before migrating.
- **TDD:** per component, first a failing `FakeTimeProvider` test that cannot be written today (no injection point), then the change.
- **Acceptance:** `Task.Delay`+`Thread.Sleep` in Unit tests reduced to those testing real I/O timeouts (target < 10, each justified); `UtcNow` ratchet baseline stated and decreasing.
- **Effort:** L-XL (long tail, batchable by component). **Risk:** Low-Medium. Can run in parallel with WP-17 after the Common project exists; coordinate with the WP-ARC-A-07 and A-11..A-14 owners who edit the same services (audit, cluster state, leases).

#### WP-ARC-C-19 Split of `Autheris.Tests.Unit`
- **Covers:** ARC-T-01, ARC-T-05, ARC-T-06 (residual), ARC-L-10.
- **Goal:** Test projects mirror production assemblies; solution has one solution file.
- **Design:** Create `Autheris.Domain.Tests`, `Autheris.Application.Tests`, `Autheris.Infrastructure.Tests`, `Autheris.GraphQL.Tests`, `Autheris.Api.Tests`; engine-specific tests currently in Unit (referencing `RlsOptions`/engine internals, `GovernedSqlPlanCacheTests`, `Security/CorrelatedRowFilterAliasTests`) move to `TrinoSqlEngine.Tests`; `ArchitectureAndCleanCodeTests.cs` moves to `Autheris.Tests.Architecture` (ARC-T-08 part). Each project references only its production assembly (+ `Tests.Common`), which also gives the dependency checks real teeth. Move in batches by folder/namespace with `git mv` to keep history; one project per PR (order: Domain -> Infrastructure -> GraphQL -> Api -> Application, because Application tests have the widest cross-references). Update `InternalsVisibleTo` in each `src/*.csproj` (replace blanket `Autheris.Tests.Unit` entries; keep until the last batch lands, then delete) and `stryker-config.json`, `.github/workflows/ci.yml`/`release.yml` test steps. Rename date/ticket-named files (`SecurityReview20261002*`, `SecurityFindingsRemediationTests`) by behaviour with `[Trait("Finding","SEC-xx")]` kept for traceability (the catalog `tests/TrinoSqlEngine.Tests/Security/SecurityRegressionCatalog.cs` already exists); split files over ~500 lines while moving, not before. Remove the 7 residual legacy-name references (ARC-T-06) while touching those files: rename helper names and delete tests proven duplicated by `Compile`-based tests (each deletion needs the traceability catalog entry to stay covered).
  ARC-L-10: delete `Autheris.slnx` and keep `Autheris.sln` (decision G-10).
- **Migration/compat:** CLAUDE.md test command `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj` changes; update CLAUDE.md, AGENTS.md, `docs/developer-guide.md`, CI. Until the last batch, Unit remains and is shrinking.
- **Security:** No test may be dropped silently: before moving, record the test count per class (`dotnet test --list-tests`) and compare total counts before/after each PR (CI check script in `scripts/`); security-review tests keep their traits.
- **TDD:** the red test is a count-parity check plus an architecture test `TestProjectsMirrorSourceProjects` (each test project references at most one production project besides Common).
- **Acceptance:** `Autheris.Tests.Unit` removed; total test count unchanged or higher; each new project builds and runs independently; CI green in both lanes; only one solution file.
- **Effort:** XL. **Risk:** Medium (merge conflicts, lost tests). **Serialization (explicit):** this WP rewrites the paths of ~520 files and conflicts with every branch touching tests. Execute when the fewest feature branches are open (recommended: immediately after a release cut, with a declared two-day merge freeze on `tests/Autheris.Tests.Unit/**`), one project per PR merged the same day; do not run concurrently with WP-03, WP-17, WP-18 (all edit test files). Hence order: WP-17 first (Common), then WP-19, then WP-18/WP-03 batches rebased onto the new layout.

#### WP-ARC-C-20 Engine packaging
- **Covers:** ARC-S-10, ARC-S-16. The `#if DEBUG` seams (ARC-S-06) are gone: WP-ARC-A-08 replaced them with the internal `ICompilerHooks` (`src/TrinoSqlEngine/CompilerHooks.cs`), guarded by `tests/Autheris.Tests.Architecture/NoDebugSecuritySeamsArchitectureTests.cs`.
- **Design:**
  ARC-S-10: remove `InternalsVisibleTo Autheris.Application/Infrastructure` (`TrinoSqlEngine.csproj:39-40`) by exposing the hook query those assemblies need through a minimal public-safe seam (or moving the four non-engine controls behind an Application-owned interface); rename meter `Autheris.SqlCompiler` to a host-configurable name (`CompilerTelemetry.cs:12`); the Core/Governance assembly split is deferred (Section 5).
  ARC-S-16: PackageId/Product/Authors to a name decision (`Autheris.SqlEngine` or keep `FastSqlEngine` deliberately), translate the German comment (`TrinoSqlEngine.csproj:13`), mark ANTLR generated output `generated_code = true` in `.editorconfig` and exclude it from Stryker `mutate`, pin the `Antlr4BuildTasks` tool version (`AntlrToolVersion`), confirm/justify `LangVersion 14` vs CLAUDE.md "C# 13 idioms" (update CLAUDE.md or the csproj).
- **Migration/compat:** No public API change of `TrinoSqlEngine` (hooks stay internal). PackageId rename affects only standalone consumers.
- **Security:** Phase 3 mandatory for the `InternalsVisibleTo` change: no configuration, environment variable or DI registration in production may disable a control (absence of hooks == all controls enabled); mutant suites must keep their kill rate.
- **TDD:** red tests first: `ProductionHostHasNoControlSwitchTests` (resolves from the real host, asserts no hook installed); architecture test that `TrinoSqlEngine` grants `InternalsVisibleTo` to test assemblies only.
- **Acceptance:** no `InternalsVisibleTo Autheris.Application`/`Autheris.Infrastructure` on TrinoSqlEngine; Gate suites (Debug and Release) green with mutant parity.
- **Effort:** M. **Risk:** Medium. **Serialization:** touches `GovernedSqlCompiler.cs`, `CompiledSqlTemplate.cs`, `TenantPredicateFactory.cs` shared with WP-10, -12; run after them.

#### WP-ARC-C-21 Architecture test expansion, analyzer and documentation hygiene
- **Covers:** ARC-L-08, ARC-T-08, ARC-L-09, ARC-L-13, ARC-L-17 (suppressions), ARC-C-19.
- **Design:**
  1. Architecture tests (in `tests/Autheris.Tests.Architecture`): Domain has no third-party package reference beyond the documented list (MemoryPack allowlisted until the Domain-purity decision); Application must not use `Microsoft.Data.*`, `Npgsql`, `DuckDB`, `Parquet`, `Prometheus`, `HotChocolate` outside allowlist (HotChocolate.Language in `SchemaRegistry/ISchemaLinter.cs` allowlisted or moved; boundary rules owned by WP-ARC-B-10 are added there, this WP adds the framework and the rules that are green today, with an explicit "pending B-10" skip list so rules turn on when the work lands); no `GatewayOptions` consumption beyond the ratchet (WP-01); no `IServiceProvider` outside composition roots; max file length 500 lines with a baseline allowlist (ratchet); lifetime rules via a `ServiceCollection` scan (singleton must not depend on scoped). Consolidate `tests/Autheris.Tests.Unit/ArchitectureAndCleanCodeTests.cs` (moved in WP-19).
  2. ARC-L-17: remove suppressions from `Directory.Build.props:16` in groups: first `CA1816` and `CA2012` (correctness: Dispose pattern, ValueTask misuse), then `CA1305/CA1304/CA1307/CA1310/CA1311` (culture/string comparison: security-relevant for case-insensitive identifier compares), then the style ones (`CA1822`, `CA1848`, `CA1859`, `CA1861`, ...). Fix or move to a per-project `NoWarn` with a justification comment; each group is its own PR because `TreatWarningsAsErrors` makes every hit a build break. Document the process in `docs/developer-guide.md`.
  3. ARC-L-13 / L-09: keep one plan root (`docs/plans`) and remove `docs/plan` duplicates and the `doc -> docs` symlink after fixing references (CLAUDE.md/AGENTS.md still cite `doc/plan/...`; they are governance files - propose the edit to the owner, do not change them silently); rename `docs/architecture/konzept-data-governance-und-mcp-documentation.md` to English (and translate); refresh `docs/architecture/arc42.md` quality-gate text (test counts, "Hot Chocolate isolated to GraphQL" claim) or remove volatile numbers. This is documentation work for Phase 6.
  4. ARC-C-19: translate remaining German comments (mainly `Autheris.Domain`, see the T10 table; `TrinoSqlEngine.csproj:13` in WP-20) and move magic defaults to option classes (`Casbin` cache cap, `FederatedDuckDbExecutionService` limits) as part of WP-03/WP-09 where those files are touched; a CI grep for common German markers is optional.
- **Security:** Enabling CA1307/CA1310/CA1311 can surface culture-sensitive comparisons in authorization code; fixes must use `StringComparison.Ordinal(IgnoreCase)` deliberately (review each change in security-relevant namespaces).
- **TDD:** each new architecture rule is its own red test first (written against a seeded violation fixture or the current baseline).
- **Acceptance:** rules listed above present and green (or explicitly skipped with an owner and WP link); NoWarn reduced to a documented minimal list; one plan directory.
- **Effort:** L (spread over several small PRs). **Risk:** Low-Medium (warnings-as-errors fallout).

#### WP-ARC-C-22 Integration hygiene in Application/Extensions
- **Covers:** ARC-F-12, ARC-F-13, ARC-F-18.
- **Design:**
  - ARC-F-12: register a named secure client `EgressIntegrations.OpenApiIngestion` through `AddSecureOutboundHandlers` and inject `IHttpClientFactory` into `CatalogDiscoveryService` (`Application/Catalog/Services/CatalogDiscoveryService.cs:321-327`), deleting manual `SecureOutboundHttp.CreatePrimaryHandler(... EgressAllowlist.Create(null, null) ...)`. Allowlist comes from `OutboundEgressOptions`; with an empty allowlist the client denies (fail-closed). SSRF protection handler included.
  - ARC-F-13: move ITSM adapters, outbox, and hosted services into `Autheris.Extensions/Itsm` behind an Application `IOutboxStore<T>` port; reusable `OutboxDispatcher<T>`. Depends on ARC-F-09 (extension boundary) and the webhook outbox (WP-ARC-A-02): schedule after those; this WP only owns the ITSM relocation.
  - ARC-F-18: `ScheduledHostedService<TJob>` base class with uniform loop (`PeriodicTimer`, optional initial delay, jitter, metrics `last_success`/`duration`, graceful shutdown, `IOptionsMonitor` interval). Lease/leader semantics come from ARC-F-06 (WP-ARC-A-11); the base class exposes a `ILeaderLease` seam but does not implement it. Migrate `MssqlChangeTrackingHostedService`, `OpenMetadataSyncBackgroundService`, `DataCatalogSyncBackgroundService`, `AuditChainIntegrityMonitor`, ITSM hosted services one by one.
- **Security:** F-12 fail-closed egress; F-13 outbox relocation must keep delivery semantics (WP-ARC-A-02 defines retry rules); F-18 must not change which node runs jobs.
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
| 05 (ProblemDetails) | 07 (preferably) | split first, then change bodies; replaces `SafeErrorResults` |
| 06 (policies) | 07 (preferably) | strict role parsing (SR-L-04) is already on `main` |
| 08 (routes) | 06, 07 | policy parity on aliases, smaller files |
| 09 (god classes) | 17 (builders) preferred; Casbin part after WP-ARC-A-12 | ctor collapse needs test builders; same Casbin cache files |
| 10 (engine structure) | 12 | same files |
| 13 (streaming core) | WP-ARC-B-10 (coordinate) | avoid doing the work twice |
| 14 (streaming egress) | 13 | consumes the stream |
| 15 (catalog hot paths) | WP-ARC-B-14 coordination | GraphQL/Catalog and repository partials |
| 16 (startup/telemetry) | WP-ARC-A-11..A-14 coordination | `GatewayStorageServiceExtensions.cs` |
| 17 (test foundation) | none | prerequisite for 19, 18, 22 |
| 19 (test split) | 17 | consume Common |
| 18 (TimeProvider) | 17 | FakeTimeProvider in Common |
| 20 (engine packaging) | 10, 12 | same files |
| 21 (arch tests/hygiene) | 01 (ratchet), 19 (test move), 16 (Prometheus rule) | rules need the work they guard |
| 22 | 18 (fake clock for base class), WP-ARC-A-02 / A-11 | outbox and lease seams |

### On neighbouring scopes (by finding ID, not planned here)

- **Architecture R0+R2:** ARC-C-07 (Casbin cache invalidation, WP-ARC-A-12; sequence with WP-09 Casbin split), ARC-I-03/-07/-08 (shared `GatewayStorageServiceExtensions.cs` with WP-16), ARC-F-06 (lease, WP-ARC-A-11; WP-22 seam), ARC-F-01..04 (webhook outbox, WP-ARC-A-02; WP-22 ITSM `OutboxDispatcher<T>` reuse), audit group commit (WP-ARC-A-07; WP-13 completion event).
- **Architecture R1+R3:** WP-ARC-B-10 (location of the streaming API for WP-13, prometheus removal from Application for WP-16, package-boundary rules for WP-21), WP-ARC-B-14 (repository split; WP-15 bulk relations method).
- **Features:** new feature work adds options and endpoints; they must use the new conventions after WP-01/-05/-06/-08 land (see freeze windows).

---

## 4. Recommended sequence

Phase 0 (can start immediately, low conflict, parallel):
- WP-ARC-C-17 remainder, WP-ARC-C-21 items 2/4 (CA suppression groups, comments), WP-ARC-C-22 F-12 (S).

Phase 1 (structural freeze windows, serialized; do when few branches are open):
1. WP-ARC-C-01 (options file split) - first, short freeze on `GatewayOptions.cs`.
2. WP-ARC-C-19 (test project split) - second, with a declared merge freeze on `tests/Autheris.Tests.Unit/**`. Run before the long-tail WPs that edit many tests (03, 18). WP-01 and WP-19 must not overlap in time.
3. WP-ARC-C-02 (validation), then WP-ARC-C-03 batches (each batch a small PR; parallel across areas only if test files are disjoint).

Phase 2 (API surface, serialized on `Autheris.Api`, one at a time because of shared hot files `GatewayApplicationBuilderExtensions.cs`, endpoint files, `EndpointSecurity.cs`):
4. WP-ARC-C-04 (pipeline/composition) -> 5. WP-ARC-C-07 (split endpoints, embedded assets, locator removal; parallelisable per endpoint family) -> 6. WP-ARC-C-06 (policies) -> 7. WP-ARC-C-05 (ProblemDetails) -> 8. WP-ARC-C-08 (routes/versioning).

Phase 3 (performance, partly parallel with Phase 2 because the file sets are disjoint):
- Engine track (serialized, same files): WP-12 -> WP-10 -> WP-20.
- Data track (serialized): WP-13 -> WP-14 (touches `ArrowExportEndpoints.cs`, `ODataEndpoints.cs`: schedule after WP-07 and WP-05 for those files).
- Independent: WP-15 (coordinate with B-14), WP-16 (coordinate with A-11..A-14).

Phase 4 (long tail, any time after Phase 1, mostly parallel):
- WP-ARC-C-09 (non-Casbin parts first; Casbin part after A-12), WP-ARC-C-18 (TimeProvider, by component), WP-ARC-C-22 (F-18, F-13 after A-02/A-11), WP-ARC-C-21 items 1/3 (rules switched on as dependencies land).

Must be serialized: 01 -> 19 -> 03; 04 -> 07 -> 06 -> 05 -> 08; 12 -> 10 -> 20; 13 -> 14.

---

## 5. Open decisions

Decisions already taken in Gate 0 ([`00-implementation-roadmap.md`](00-implementation-roadmap.md) section 5): G-9 (legacy `error` field for one release, deprecated route aliases for two releases; WP-05, WP-08) and G-10 (keep `Autheris.sln`, delete `Autheris.slnx`; WP-19). Still open, with recommendation:

1. **Test project granularity (WP-19):** one project per production assembly (recommended) vs a coarser fast/slow split. Recommendation: per assembly plus category traits for lanes.
2. **TrinoSqlEngine Core/Governance split (ARC-S-10, deferred):** only worth doing if the engine is published standalone. Recommendation: defer; record the decision in `architecture.md`.
3. **Streaming API location (WP-13):** implement now on the governed execution path and let WP-ARC-B-10 move it, or wait for B-10. Recommendation: define the port now, implement in the existing executor, let the B-10 move carry it.
4. **Metrics stack (WP-16):** OpenTelemetry Prometheus exporter (pre-release package) vs keeping prometheus-net for `/metrics` and bridging meters. Recommendation: OTel exporter; otherwise keep prometheus-net only in `Autheris.Api` and bridge `Meter` instruments.
5. **Governance documents (WP-21):** CLAUDE.md/AGENTS.md now refer directly to `docs/plans/`; the `doc` and `docs/plan` symlinks were removed per owner directive (2026-10-21).
6. **Option binding model (WP-03):** keep `GatewayOptions` root as the user-facing config model (recommended; no config break) vs separate top-level config sections (breaking).
7. **Parser inline fast path (carry-over from the delivered WP-C-11):** `FastSqlEngine.InlineParseMaxLength` exists and defaults to 0 (off). Enable it only after a `ParseThroughputBenchmarks` run (not yet in `benchmarks/`) shows a material gain.
