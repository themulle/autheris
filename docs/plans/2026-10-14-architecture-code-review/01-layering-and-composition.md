# Review 01 - Layering and Composition (read-only)

## 1. Scope and metrics
Reviewed: solution/project structure, csproj graph, Domain purity, DI composition root, hosted services, architecture tests, docs-vs-code consistency.

| Metric | Value |
|---|---|
| C# LOC (src, incl. partials) | Application 61.3k, Infrastructure 25.4k, Api 22.6k, TrinoSqlEngine 21.8k, Extensions 13.8k, Domain 10.6k, GraphQL 7.5k |
| Application: .cs files / interface files | 442 / 184 |
| Largest files | `src/Autheris.Domain/Options/GatewayOptions.cs` 2034 (77 classes); `src/Autheris.Application/Governance/CasbinEnforcementService.cs` 1897; `SqliteGovernanceRepository.Consent.cs` 1705; 3 provider repos total 5725/4553/4534 LOC |
| `IOptions<GatewayOptions>` injections | 275 (+8 `IOptionsMonitor`) |
| Service-locator hits outside Api (`IServiceProvider`, `GetService<>`) | 92 |
| `ValidateOnStart` calls | 1 (on a ~25-lambda `.Validate` chain) |
| Architecture tests | 9 files, 1017 LOC, 42 `[Fact]/[Theory]` (NetArchTest + Mono.Cecil) |
| Hosted services registered | 21 `AddHostedService` + 8 `IHostedService` classes |

## 2. Strengths
- Project graph is acyclic and matches the declared rule: Domain has no ProjectReference; Infrastructure/GraphQL/Extensions reference only Application+Domain; only Api references everything (`src/*/*.csproj`).
- Architecture tests are meaningful and go beyond namespaces: Cecil IL scan for experimental-dialect setter (`tests/Autheris.Tests.Architecture/CompilerSeamArchitectureTests.cs:11-40`), "no foreign-system clients in core" (`ArchitectureTests.cs:114-148`), Extensions isolation.
- Composition root is split into topical extension classes (`src/Autheris.Api/Extensions/DependencyInjection/*`, 12 files), `Program.cs` is only 136 lines.
- `ValidateOnBuild = true` plus options validation with fail-closed messages and `ValidateOnStart` (`Program.cs:46-50`, `GatewayServiceCollectionExtensions.cs:91-150`).
- Build hygiene: `TreatWarningsAsErrors`, lock files with locked restore in CI, NuGet audit as errors (`Directory.Build.props`).
- Static caches are bounded (`TargetPatternMatcher.cs:30`, `SqlSecurityValidator.cs:135`, `SubgraphSecurityValidator.cs:80`).

## 3. Findings

| ID | Sev | Category | Title |
|---|---|---|---|
| ARC-L-01 | High | Config/Coupling | `GatewayOptions` is a 2034-line, 77-class god-config injected everywhere (275 sites) |
| ARC-L-02 | High | Layering | Application is a "fat" layer: DuckDB, Parquet, Arrow Flight server, Casbin, Prometheus, TrinoSqlEngine all compiled in |
| ARC-L-03 | High | Cohesion | One repository class implements ~13 ports, tripled per DB dialect (~14.8k LOC) |
| ARC-L-04 | Medium | Layering | Domain is not pure: runtime options, metrics singletons, MemoryPack, GlobalUsings |
| ARC-L-05 | Medium | DI/Lifetime | Service-locator usage in business code (92 hits), optional `IServiceProvider? = null` ctor params |
| ARC-L-06 | Medium | DI/Lifetime | `ValidateScopes` only in Development; singleton repos + scoped services + scope-per-call workers |
| ARC-L-07 | Medium | Config | Option validation is imperative in Api, duplicated (lambda chain + `ValidateGatewayOptions`), file-system checks in predicates, snapshot vs IOptions drift |
| ARC-L-08 | Medium | Testability | Architecture tests miss key rules (Domain third-party purity, Application infra packages, GraphQL/HotChocolate leak, lifetimes, namespace-cycles) |
| ARC-L-09 | Medium | Consistency | arc42 quality-gate claims are stale/false (5 arch tests, 735 tests, "Hot Chocolate isolated to GraphQL") |
| ARC-L-10 | Medium | Maintainability | Two parallel solution files (`Autheris.sln` vs `Autheris.slnx`) that have already diverged |
| ARC-L-11 | Medium | Maintainability | Giant registration/startup classes (GatewayStartupValidator 769, ServiceCollectionExtensions 672, ApplicationBuilderExtensions 501) with Api->Infrastructure concrete wiring |
| ARC-L-12 | Medium | Resilience | Fire-and-forget `Task.Run` / `_ =` and sync-over-async in lifecycle paths |
| ARC-L-13 | Low | Consistency | Duplicate plan files / German-named plans / `doc` symlink contradict CLAUDE.md rules |
| ARC-L-14 | Low | Maintainability | Stray artifacts at repo root and in `src/Autheris.Api/scratch` |
| ARC-L-15 | Low | Consistency | Naming debt: `Plan9ServiceExtensions`, `AP-9.x`, WP-X0l tags as architecture identifiers |
| ARC-L-16 | Low | DI/Lifetime | Static mutable state: process-wide replay cache and caches without DI/abstraction |
| ARC-L-17 | Low | Config | `Directory.Build.props` suppresses 25 CA rules globally; `AutherisIncludeTestAuth` defaults true |
| ARC-L-18 | Low | Coupling | Extensions references Application but plugin boundary exposes whole Application surface; `InternalsVisibleTo` to tests only on Unit |

### ARC-L-01 God options object (High, Config/Coupling, Effort L)
- Evidence: `src/Autheris.Domain/Options/GatewayOptions.cs` (2034 lines, 77 `class` declarations); 275 `IOptions<GatewayOptions>` and 8 `IOptionsMonitor<GatewayOptions>` usages in `src/`; raw `GatewayOptions` fields held by services: `src/Autheris.Application/Data/Services/GovernedDataQueryService.cs:47`, `Policy/TableAccessPolicy.cs:98`, `Mcp/Services/McpProtocolHandler.cs:28`, `Extensibility/Interceptors/AuditLineageEgressInterceptor.cs:11`.
- Problem: every service depends on the entire configuration tree (Casbin, Garnet, Authentication, OpenMetadata...), not on its own slice. Domain owns a deployment/configuration model (Api-only concerns like `Authentication.ForwardAuth` live next to domain rules).
- Impact: hidden coupling, every option change touches a 2k-line file (merge conflicts), unit tests must build a full tree, no per-feature validation, snapshot/reload semantics ambiguous (raw `GatewayOptions` vs `IOptions` vs `IOptionsMonitor`).
- Recommendation: split into per-feature options classes (`CasbinOptions`, `AuthenticationOptions`, ... each its own file) bound via `AddOptions<T>().BindConfiguration(...)`; keep `GatewayOptions` as a thin binding aggregate in Api; move Api-only slices (Authentication, Hosting, ReverseProxy, GraphQL tooling) out of Domain; migrate consumers incrementally (arch test: new code may not inject `IOptions<GatewayOptions>`).

### ARC-L-02 Application layer carries infrastructure engines (High, Layering, Effort L)
- Evidence: `src/Autheris.Application/Autheris.Application.csproj` references `DuckDB.NET.Data.Full`, `Parquet.Net`, `Apache.Arrow`, `Casbin.NET`, `prometheus-net`, `YamlDotNet`, `HotChocolate.Language`, `MemoryPack`, `Microsoft.Extensions.Http`, and ProjectReference `TrinoSqlEngine`. Usages: `Olap/DuckDbOlapEngine.cs:9,102` (`new DuckDBConnection("DataSource=:memory:")`), `Serialization/ArrowFlightSqlServer.cs` (a server inside Application), `Serialization/ParquetExportService.cs`, `Governance/CasbinEnforcementService.cs` (1897 lines), `Procedures/Services/MssqlProcedureInvoker.cs:116`, `Common/DataAccessErrorClassifier.cs:58` (SqlClient/Npgsql error strings). Registration of `DuckDbOlapEngine` by hand in `src/Autheris.Api/Extensions/DependencyInjection/GatewayExecutionServiceExtensions.cs:113-116`.
- Problem: the dependency rule "Application depends on abstractions" is only enforced for project references; concrete engines (DuckDB, Parquet, Arrow, Prometheus) are in Application, so Infrastructure is not the sole owner of I/O. Application is 61k LOC (6x Domain), 442 files.
- Impact: slow builds/tests for any Application change, native DuckDB binary in every consumer (`DuckDB.NET.Data.Full` is large), impossible to swap/omit engines, risk of vendor leakage into policy code (`HotChocolate.Language` in `SchemaRegistry/ISchemaLinter.cs:3`, `Mcp/Services/PreFlightQuerySimulator.cs:11`).
- Recommendation: define ports in Application (`IOlapEngine`, `IColumnarExporter`, `IPolicyEngine`), move DuckDB/Parquet/Arrow/Flight/Casbin adapters to Infrastructure (or new `Autheris.Infrastructure.Analytics` / `Autheris.Policy.Casbin` projects); keep Prometheus behind the existing `System.Diagnostics.Metrics` `GatewayDiagnostics` and expose via exporter in Api only; split Application into `Application.Core` (policy, governance) and feature modules.

### ARC-L-03 Repository god classes tripled per dialect (High, Cohesion, Effort L)
- Evidence: `src/Autheris.Api/Extensions/DependencyInjection/GatewayGovernanceServiceExtensions.cs:37-60` registers 13 interfaces from one concrete class per provider (IGovernanceRepository, ITableMetadataRepository, IConsentRepository, IAuditLogRepository, IPolicyEpochRepository, IConsentApprovalRepository, IDataOwnershipRepository, ITableRelationRepository, IItsmOutboxRepository, IVirtualFilterRepository, IAuditChainExportSource ...); `Sqlite/PostgreSql/SqlServerGovernanceRepository*.cs` = 5725 / 4553 / 4534 LOC across 42 files in `src/Autheris.Infrastructure/Persistence/`; `SqliteGovernanceRepository.cs:18` `partial class ... : IGovernanceRepository, IDisposable`, additional interfaces added by partials (`.AccessProfiles.cs:15`, `.Audit.cs:16`, `.TenantRegistry.cs:6`). Sync-over-async dispose: `PostgreSqlGovernanceRepository.cs:275`, `SqlServerGovernanceRepository.cs:302`.
- Problem: ISP violation inverted into a single singleton per DB holding one connection (`SqliteGovernanceRepository.cs:20` `_connection`); ~15k LOC of near-duplicate SQL per dialect.
- Impact: bug fixes must be applied 3x (drift risk in audit/consent paths), no per-aggregate transaction boundaries, impossible to scale one concern (audit writes) separately, large-file review fatigue.
- Recommendation: split by aggregate (ConsentRepository, AuditRepository, CatalogRepository, OutboxRepository) sharing a `IGovernanceDb` connection/dialect abstraction; consolidate SQL via a dialect helper or Dapper-style shared query text with dialect hooks; contract-test suite run against all three providers.

### ARC-L-04 Domain purity gaps (Medium, Layering, Effort M)
- Evidence: `src/Autheris.Domain/Autheris.Domain.csproj` references `MemoryPack` and `System.ComponentModel.Annotations`; `Model/CachedConsentEnvelope.cs:5,13` (`[MemoryPackable]`, a Redis cache wire DTO in Domain); `Diagnostics/GatewayDiagnostics.cs:5-14` static `ActivitySource`/`Meter` singletons with German description (line 16) (telemetry plumbing in Domain); `Options/` 2598 LOC (26% of Domain) incl. `DemoDataSwitch.cs`, `DevOptions.cs`; `Model/` 4777 LOC. Anemic model: `Model/*.cs` has 564 init/record property declarations vs 27 settable props, i.e. immutable data carriers, behaviour lives in Application services (e.g. `ConsentResolutionService`).
- Problem: Domain mixes domain rules with serialization contract, observability and configuration; the aggregate logic is largely not in the domain (only `TargetPatternMatcher`, `Common/Sid`).
- Impact: Domain cannot be reused without MemoryPack/Metrics; wire-format changes (MemoryPack layout comment at `CachedConsentEnvelope.cs:26` "Appended last") become domain changes; weak invariants.
- Recommendation: move `CachedConsentEnvelope` and MemoryPack to Infrastructure (map from domain `ConsentResolution`); move `GatewayDiagnostics` to Application/Observability; move Options per ARC-L-01; migrate invariant logic (consent validity, composite keys, four-eyes) into domain types.

### ARC-L-05 Service locator in business code (Medium, DI/Lifetime, Effort M)
- Evidence: `src/Autheris.Application/Sql/Services/GovernedSqlExecutionService.cs:44,71,101,148-150` (optional `IServiceProvider? serviceProvider = null` then `GetService(typeof(IFederatedQueryExecutionService))`); `Connectors/IAutherisConnectorFactory.cs:9` passes `IServiceProvider` through a public port; `Security/SecureOutboundHttp.cs:57-61`, `SsrfProtectionHandler.cs:50-55` (static factories resolving `IOptions<GatewayOptions>` with `?? new GatewayOptions()` fallback); `Infrastructure/Persistence/SqlConnectionFactory.cs:21,39,203-208`; `Infrastructure/Plugins/PluginManager.cs:24,38`; `Extensions/DataCatalog/DataCatalogClientFactory.cs:15-33` (factory over 4 concrete clients); `Application/Procedures/Services/ProcedureRegistrationService.cs:92-93` (`GetService` silently null if missing).
- Problem: hidden dependencies, nullable optional ctor deps (silent degradation: missing service becomes feature disabled, fail-open style for validator/audit at `ProcedureRegistrationService.cs:92-93`), `?? new GatewayOptions()` default swallows misconfiguration.
- Impact: ValidateOnBuild cannot see these; runtime-only failures; tests construct partial graphs.
- Recommendation: constructor-inject `IFederatedQueryExecutionService?` explicitly or register a null-object; keyed services (`AddKeyedSingleton`) for DataCatalog clients; scope handling through `IServiceScopeFactory` only inside worker classes (already the pattern in `Jobs/Services/AsyncQueryJobBackgroundWorker.cs:88`) ; arch test banning `IServiceProvider` in Application except `*Worker`/`*HostedService`.

### ARC-L-06 Lifetime safety only validated in Development (Medium, DI/Lifetime, Effort S)
- Evidence: `src/Autheris.Api/Program.cs:46-50` (`ValidateScopes = builder.Environment.IsDevelopment()`); governance repositories all `AddSingleton` (`GatewayGovernanceServiceExtensions.cs:39-52`) while `GatewayExecutionService` and policy services are `AddScoped` (`GatewayExecutionServiceExtensions.cs:77-100,129`); 53 hosted-service/worker sites create scopes manually (`Itsm/ItsmOutboxDispatcherHostedService.cs:88-99` resolves 4 services via `GetService`, `Itsm/ConsentRecertificationHostedService.cs:40-55`).
- Problem: captive-dependency bugs would surface only in Development/Integration tests; production silently keeps scoped instances alive. `GetService` (optional) inside outbox dispatcher means a missing registration just skips dispatch.
- Impact: latent stale-state/tenant-leak risk in a security gateway.
- Recommendation: set `ValidateScopes = true` always (cost is negligible) or run a CI test that builds the production provider with `ValidateScopes=true`; replace `GetService` by `GetRequiredService` for mandatory deps.

### ARC-L-07 Options validation structure (Medium, Config, Effort M)
- Evidence: `src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.cs:91-150` (~25 inline lambdas, some doing disk I/O: `File.Exists` at 104,110, `Directory.Exists` at ~173 in a predicate); second validator `GatewayStartupValidator.ValidateStartup` called at `:667` (769 lines, `Autheris.Api/Extensions/DependencyInjection/GatewayStartupValidator.cs`); third `GatewayEmptyListConfigurationValidator` registered via factory at `:~156`; options are bound a 2nd time via `configuration.GetSection(...).Get<GatewayOptions>()` at `:~160` and used for conditional registration while services read `IOptions` (different instance, bypasses `PostConfigure`/dev preset ordering, `Program.cs:61-63`). `HostOptions.ShutdownTimeout` and `ForwardedHeadersOptions` are configured from the snapshot (`:~167-197`).
- Problem: three overlapping validation mechanisms with no single list of rules; only `GatewayOptions` is validated, 1 `ValidateOnStart` in the repo (grep); other options (Webhooks, Classification, Federation, VirtualFilterFederation) have none.
- Impact: inconsistent fail-fast, rule duplication risk, hard to unit test rule sets.
- Recommendation: one `IValidateOptions<T>` class per feature options (testable, ValidateOnStart); keep security-environment rules in one `GatewaySecurityOptionsValidator`; pass the validated `IOptions` value rather than a second `Get<>()`.

### ARC-L-08 Architecture test coverage gaps (Medium, Testability, Effort M)
- Evidence: `tests/Autheris.Tests.Architecture/ArchitectureTests.cs:17-200` covers project-namespace dependencies, AspNetCore in Application/Domain, interface naming only. Missing: (a) Domain third-party restriction (Domain test only checks sibling namespaces, `:18-27`; `MemoryPack` passes); (b) Application must not use `Microsoft.Data.*`, `DuckDB`, `Parquet`, `Prometheus`, `HotChocolate` (only AspNetCore is banned, `:54-63`; Application uses `HotChocolate.Language` at `SchemaRegistry/ISchemaLinter.cs:3`); (c) no `IServiceProvider` outside composition/hosting; (d) no `GatewayOptions` direct consumption; (e) lifetime rules (singleton must not take scoped); (f) namespace cycles inside Application (47 top-level feature folders, e.g. `Policy` <-> `Governance` <-> `Sql`); (g) Api endpoints must not reference `Infrastructure` concrete types (Api references Infrastructure at `Autheris.Api.csproj`); (h) `Extensions` -> `Application` surface via an allowlist. Also `Application_ShouldNotHaveDependencyOnInfrastructureOrApi` does not forbid `TrinoSqlEngine` coupling in Domain beyond namespace name.
- Impact: rules documented in ADRs are enforced only by convention.
- Recommendation: add the above tests (NetArchTest `ResideInNamespace` + custom `ICustomRule` for lifetimes via `ServiceCollection` scan); add an Application-internal slice/cycle test (e.g. ArchUnitNET `Slices`).

### ARC-L-09 arc42 out of sync (Medium, Consistency, Effort S)
- Evidence: `docs/architecture/arc42.md:317` "NetArchTest asserts ... isolating Hot Chocolate to GraphQL" vs `src/Autheris.Application/Autheris.Application.csproj` (`HotChocolate.Language`, 5 files using it) and `ArchitectureTests.cs` (no HotChocolate rule); `:318` "735 / 735 tests ... 5 Architecture" vs 42 architecture test methods and 451 test files in `tests/Autheris.Tests.Unit`; `:6` lists SQL Server/PostgreSQL/SQLite while Oracle/Databricks drivers exist (`Infrastructure.csproj`, `OracleDriverArchitectureTests.cs`); no Extensions/TrinoSqlEngine projects in the building-block view (`grep -n Extensions arc42.md` returns none). ADR list stops at ADR-018 though plugin/events (PR #71) and EXT-MOVE (Extensions project) are architecture decisions.
- Recommendation: regenerate numbers from CI or drop them; add ADRs for Extensions split, TrinoSqlEngine ownership, MemoryPack/Garnet cache; add a "building blocks" diagram that matches the csproj graph.

### ARC-L-10 Two solution files diverged (Medium, Maintainability, Effort S)
- Evidence: `Autheris.sln` (315 lines, contains `Autheris.Tests.SqlCompilerGate`) vs `Autheris.slnx` (22 lines, no `SqlCompilerGate`; `TrinoSqlEngine` project placed outside any folder at line 21; `grep -c SqlCompilerGate` = 1 vs 0). CI uses `Autheris.sln` only (`.github/workflows/ci.yml:29,38`, `release.yml:36,45`), CLAUDE.md says `Autheris.sln`.
- Impact: IDE/`dotnet` on a directory with both fails ("multiple solution files") and slnx users miss the SqlCompiler gate test.
- Recommendation: keep one (migrate to `.slnx` with `dotnet sln migrate`, update CI) and delete the other.

### ARC-L-11 Giant composition/startup files with concrete wiring (Medium, Maintainability, Effort M)
- Evidence: `src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.cs` 672 lines; `DependencyInjection/GatewayStartupValidator.cs` 769; `GatewayApplicationBuilderExtensions.cs` 501; `GatewayGovernanceServiceExtensions.cs` 175 with 81 registrations and three near-identical 13-line provider blocks (`:37-95`); factory lambdas that `new` services manually (`GatewayExecutionServiceExtensions.cs:79-97`, `:113-116`); endpoint classes 1018/987/919 lines (`Endpoints/WebSqlEndpoints.cs`, `GovernanceEndpoints.cs`, `VirtualFilterEndpoints.cs`). Application/Infrastructure expose almost no own `AddXxx` (only `Governance/ClassificationServiceCollectionExtensions.cs`, `Infrastructure/Databricks/DatabricksServiceCollectionExtensions.cs`), so Api must know every concrete type (Api csproj references Infrastructure).
- Impact: Api is the god-assembly (22.6k LOC), adding a feature touches the composition root; manual `new` with 10+ args breaks silently when a ctor adds a dependency.
- Recommendation: each project exposes `AddAutherisApplication()` / `AddAutherisInfrastructure(options)`; replace manual `new` factories with plain registrations; generate provider repository registrations through a loop over a type list; split endpoint classes per resource.

### ARC-L-12 Background work and lifecycle (Medium, Resilience, Effort M)
- Evidence: fire-and-forget: `Application/Mcp/Services/McpSessionStore.cs:178,264`, `VirtualFilters/Federation/VirtualFilterKeySetCache.cs:57` (`Task.Run(..., CancellationToken.None)`), `GraphQL/Catalog/CatalogGraphQlTypeModule.cs:100` (Timer calling async), `GraphQL/Subscriptions/Subscription.cs:99`, `Infrastructure/Messaging/InProcessChannelEventBus.cs:145`, `RedisEventBus.cs:113`, `RateLimiting/InMemoryRateLimiterService.cs:63,147,281`; sync-over-async: `Application/Sql/Services/WebSqlStatementManager.cs:452`, `Infrastructure/Databricks/DatabricksDbCommand.cs:35,37,65`, repositories' `Dispose` (ARC-L-03). Startup ordering done imperatively instead of via host: `Program.cs:70-75` `GetService<IGarnetServerManager>()?.StartServer()` plus a second hosted-service registration (`GatewayStorageServiceExtensions.cs:48-49,53-60`).
- Problem: unobserved exceptions and unbounded work are not tied to the host `StoppingToken`; two start paths for Garnet; no central background-task abstraction.
- Impact: lost work on shutdown despite the elaborate drain controller (`Hosting/TrafficDrainService.cs`), harder diagnostics.
- Recommendation: `IBackgroundTaskQueue` (Channel) + `BackgroundService` consumer with logging and stopping-token; for Garnet, a single `IHostedService` ordered first, or `IHostedLifecycleService.StartingAsync` (net8+) instead of pre-`Run` locator call.

### ARC-L-13 Plan/doc governance violations (Low, Consistency, Effort S)
- Evidence: `doc -> docs` symlink at repo root (`ls -la`); `doc/plan/00-gesamtplan-uebersicht.md` (30 lines) and `00-master-plan-overview.md` (101 lines) coexist although CLAUDE.md names the latter as single origin; plan dirs `docs/plan` and `docs/plans` both exist; German filenames (`2026-10-10-konzept-*.md`, `...-befunde-v1-1-6.md`, `...-implementierungsplan-*.md`, `docs/architecture/konzept-data-governance-und-mcp-documentation.md`) conflict with the English-only directive; CLAUDE.md links `file:///root/autheris/...` (path not this checkout `/root/lis-git/autheris`).
- Recommendation: delete the stale German overview or turn it into a redirect, standardise on `docs/plan/`, drop the symlink (breaks on Windows checkouts), rename German files, fix absolute links.

### ARC-L-14 Stray artifacts (Low, Maintainability, Effort S)
- Evidence: root `fix4.tgz`, `fix4b.tgz` (0 bytes, ignored by `*.tgz` in `.gitignore` but present), `libpod/tmp`, `dist/publish`, `src/Autheris.Api/scratch/jobs` (all git-ignored: `scratch/`, `dist/`, `libpod/`; `git ls-files` shows none tracked). `.claude/`, `.agents/`, `.config/` at root.
- Impact: low (not tracked) but `src/Autheris.Api/scratch` sits inside the project dir so MSBuild globbing and Docker contexts may pick it up; `.dockerignore` should be checked.
- Recommendation: delete local leftovers; ensure `.dockerignore`/`Compile Remove` cover `scratch`.

### ARC-L-15 Process-tag naming in architecture (Low, Consistency, Effort S)
- Evidence: `src/Autheris.Api/Extensions/Plan9ServiceExtensions.cs:13-30` (`AddPlan9Services`, comments "AP-9.1", "AP-9.4"), registers jobs (`AsyncQueryJobManager`, hosted worker) and `DatasourceTestingService` unrelated by feature; tags such as `WP-X0l`, `SEC M-01`, `EXT-MOVE`, `CR-ADG-20` throughout code and tests; `Program.cs:30-33` numbered comments out of order ("1.", "2.", "3a.", "3.", "4.").
- Recommendation: rename by capability (`AddAsyncJobs`, `AddDatasourceTesting`), keep plan IDs only in commit messages/ADRs.

### ARC-L-16 Static mutable state (Low, DI/Lifetime, Effort S)
- Evidence: `src/Autheris.GraphQL/Federation/SubgraphSecurityValidator.cs:14,19,75-87` static `ConcurrentDictionary` nonce store with `ResetReplayCache()` for tests (not shared across replicas, full scan on >10k entries); `Application/Sql/SqlSecurityValidator.cs:29,79,135` static cache; `Application/Streaming/Services/StreamingRowFilterAstEvaluator.cs:23` static PlanCache (no bound seen); `Domain/Model/TargetPatternMatcher.cs:32` static regex cache; `Domain/Diagnostics/GatewayDiagnostics.cs:11-12` static Meter. Replay protection is per-process although the gateway supports multi-node mode (`GatewayServiceCollectionExtensions.cs:~130`).
- Recommendation: inject an `IReplayCache` (Memory/Redis implementation, the Redis infrastructure already exists), bound PlanCache with MemoryCache; keep static only for pure memoisation.

### ARC-L-17 Build property defaults (Low, Config, Effort S)
- Evidence: `Directory.Build.props` `NoWarn` suppresses 25 codes (CA1822, CA1848, CA1307, CA1310, CA1816, CA2012 ...) while `AnalysisLevel=latest-recommended` and TreatWarningsAsErrors on; `AutherisIncludeTestAuth` defaults to `true` (line `<AutherisIncludeTestAuth ... >true`) so test-auth handler code (`#if AUTHERIS_TEST_AUTH`, `GatewayServiceCollectionExtensions.cs:129-135`) compiles into default Release builds unless Dockerfiles override; `global.json` pins 10.0.401 with `latestFeature` roll-forward while CI uses `10.0.x`.
- Recommendation: default `AutherisIncludeTestAuth=false` outside Debug; document that CA suppressions are tracked (CA1816/CA2012 are correctness-related: ValueTask misuse and Dispose pattern); align CI to `global-json-file`.

### ARC-L-18 Extension boundary and test access (Low, Coupling, Effort M)
- Evidence: `src/Autheris.Extensions/Autheris.Extensions.csproj` references the entire Application assembly (and its DuckDB/Parquet/Casbin transitive packages); only the Api references Extensions (`ArchitectureTests.cs:77-100`), yet `InternalsVisibleTo` is granted to multiple test assemblies on every project (`Application.csproj:38-40` including `Autheris.Benchmarks`), meaning tests/benchmarks bind to internals.
- Recommendation: extract `Autheris.Application.Abstractions` (ports + DTOs) as the only reference for Extensions/plugins; limit `InternalsVisibleTo` to the unit test assembly.

## 4. Docs vs code spot-check summary
- True: Domain has no project references; Extensions only referenced by Api; Application bans AspNetCore; DI validation at build.
- False/stale: arc42:317-318 (see ARC-L-09). The statement "Quality Gate 2: Domain and Application have zero inward or improper dependencies" holds for namespaces only, not for packages (ARC-L-02, ARC-L-04, ARC-L-08).
