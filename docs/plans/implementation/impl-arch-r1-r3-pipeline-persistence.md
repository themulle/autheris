# Implementation Plan: Architecture Waves R1 (Unified Governed Query Pipeline) and R3 (Persistence and Dialect Ports)

**Document ID:** `IMPL-ARCH-R1-R3`
**Parent plans:** [`../00-master-plan-overview.md`](../00-master-plan-overview.md), [`../architecture.md`](../architecture.md) (`PLAN-ARCH-REVIEW`, themes T1, T3, T4, waves R1 and R3)
**Role:** Solution Architect (Phase 2 of the CLAUDE.md lifecycle). Next phase: Security Expert review (Phase 3), then TDD developers.
**Date:** 2026-10-14
**Base:** `main` at a4137626 (Waves 1-3 of the implementation roadmap merged)
**WP prefix:** `WP-ARC-B-NN`
**Source of finding IDs:** original review reports, `git show d8db81cc^:docs/plans/2026-10-14-architecture-code-review/0{1..6}-*.md`.

**Delivered and removed from this plan** (see git history of this file and commits on `main`): WP-ARC-B-01 table-read planner, B-02a/b/c governed `SqlTableReadExecutor` (`SqlDataSourceExecutor` deleted), B-03 `GovernedQueryStages` and tree-vs-`Compile` parity guard, B-04 mask/row-filter semantics corpus, B-05 `IConsentRequestService`, B-06 `IMcpResourceProvider`, B-08 capability-driven unbound markers and `DialectRegistryCompletenessTests`, and B-12 versioned migrations (except the remainder below). Decisions D1 (tree exception), D2 (hard cutover) and D6 (fail startup on migration failure) are settled by Gate 0 G-6, G-7 and G-8.

## 0. Scope and non-scope

In scope (open part): themes T1 (ARC-A-01, ARC-C-12 remainder), T3 (ARC-L-02, ARC-C-02, ARC-F-19, ARC-I-02, ARC-L-04, ARC-C-10, ARC-C-16, ARC-C-18, ARC-G-07 remainder) and T4 (ARC-L-03, ARC-I-01, ARC-I-05, ARC-I-12, ARC-I-13, ARC-I-04 remainder).

Out of scope, only referenced: Architecture R0+R2 (T5 HA, T6 eventing/outbox, audit group commit), Architecture R4-R6 (T7-T10, incl. GatewayOptions split ARC-L-01, ProblemDetails, test project split), Features (#28, #37, #38).

## 1. Verification table (open items on `main` a4137626)

### 1.1 Theme T1 - one governed query pipeline

| ID | Status | Evidence |
|---|---|---|
| ARC-C-01 (parallel pipelines) | Resolved except the documented exceptions | `AllowedExceptionsOutsideCompile` (`tests/Autheris.Tests.Architecture/CutoverArchitectureTests.cs:521-526`) lists only `GovernedTreeQueryService` (G-6), `FederatedStagingService` and `DuckDbOlapEndpoints`. No further work in this plan. |
| ARC-C-12 (two masking engines) | Remainder | Typed path is in place (`InMemoryRowFilterEvaluator.cs:61-73`, corpus `tests/Autheris.Tests.Unit/Masking/MaskSemanticsCorpus*.cs`). A decision without a typed `RowPolicy` still falls back to text parsing in `LegacyTextRowFilterEvaluator` (357 LOC, `InMemoryRowFilterEvaluator.cs:56-58`, string overload `GatewayExecutionService.cs:565-570`). Remove the fallback once every decision carries a typed policy (fold into WP-ARC-B-09 or B-11). |
| ARC-A-01 (orchestration in Api route classes) | Open | Audit now goes through `IAuditWriter` (no null-tolerant locator), but use-case logic stays in handlers: `GovernanceEndpoints.cs:64-137` (OpenAPI ingestion, audit `:113`), `:727` (`GetAllTablesAsync`); `VirtualFilterEndpoints.cs:224-357` (`EffectiveFiltersAsync`, audit writes `:232,281,314`, catalog read `:298`); `DuckDbOlapEndpoints.cs:90-243` (catalog read `:174`, audit `:110-119`); `ArrowExportEndpoints.cs:165` (`SELECT * FROM {table}` built in the handler). |
| ARC-G-07 (dialect gate in GraphQL schema) | Remainder | `CatalogSchemaModel.cs:104-127` gates on `TreeSqlCompiler.SupportsTreeCompile` (constant set {SqlServer, PostgreSql, Sqlite}, `TreeSqlCompiler.cs:40-41`); Oracle tables are excluded from GraphQL and logged once (documented in `docs/features/f-dialect-02-ast-sql-compiler.md`). Remaining: read the capability from the dialect registry (WP-ARC-B-09). |

### 1.2 Theme T3 - thin Application layer, dialect port

| ID | Status | Evidence |
|---|---|---|
| ARC-L-02 (fat Application) | Open | `src/Autheris.Application/Autheris.Application.csproj` still references `DuckDB.NET.Data.Full`, `Apache.Arrow`, `Parquet.Net`, `Casbin.NET`, `prometheus-net`, `YamlDotNet`, `HotChocolate.Language`, `MemoryPack`, `Microsoft.Extensions.Http`. DuckDB only in `Olap/DuckDbOlapEngine.cs`; Arrow/Parquet only in `Serialization/*`; Prometheus only in `Sql/Errors/SqlCompilerErrorMapper.cs`. No package-boundary architecture test (guardrail 2 of `architecture.md` section 4). |
| ARC-C-02 (infra inside Application) | Partially resolved | `SqlDataSourceExecutor` is gone (B-02). Still in Application: `Services/DbSessionContextInitializer.cs` (217 LOC), `Sql/OracleBindByName.cs`, `Procedures/Services/MssqlProcedureInvoker.cs`, `ProcedureConnectionProvider.cs`, `Common/DataAccessErrorClassifier.cs`, `Services/DeclarativeHttpDataSourceExecutor.cs`, `Security/SecureOutboundHttp.cs`. 27 Application files (6,949 LOC) touch `System.Data`. |
| ARC-F-19 (runtime/IO hosts in Application) | Open | `Mcp/Services/McpStdioRunner.cs`, `Docs/Services/FileSystemUniversalDocStore.cs`, `Serialization/ArrowFlightSqlServer.cs`, `Olap/DuckDbOlapEngine.cs`. |
| ARC-I-02 (no dialect port) | Open | `DatabaseDialect.` in 40 files, `TargetSqlDialect.` in 36 files under `src`; hot spots: `SqlDialectMapper.cs` (16), `SqlDataMaskingProvider.cs` (15), `TreeSqlCompiler.cs` (14), `AdvancedRlsFilterGenerator.cs` (10), `MssqlProcedureInvoker.cs` (10), `SqlProcedureRowScopeResolver.cs` (9), `SqlConnectionFactory.cs` (8), `GatewayStartupValidator.cs` (8). Remaining engine-side enum-keyed sites are listed in `tests/TrinoSqlEngine.Tests/Compiler/DialectRegistryCompletenessTests.cs:9-13`. |
| ARC-L-04 (Domain not pure) | Open | `src/Autheris.Domain/Autheris.Domain.csproj` references `MemoryPack` and `System.ComponentModel.Annotations`; `Model/CachedConsentEnvelope.cs` is `[MemoryPackable]`; `Diagnostics/GatewayDiagnostics.cs` static meters. |
| ARC-C-10 / ARC-C-16 / ARC-C-18 (folder/interface hygiene) | Open | `Application/Services` (21 files), `Application/Interfaces` (38 files), `Caching` thin, `Performance` only `IncrementalDelivery`, `Common` 3 entries. |

### 1.3 Theme T4 - persistence and migrations

| ID | Status | Evidence |
|---|---|---|
| ARC-I-04 (versioned migrations) | Remainder | Delivered: `Infrastructure/Persistence/Migrations/*` (runner per dialect, `autheris_schema_migrations`, `GovernanceMigrationHostedService`, `--migrate-governance`, `GovernanceDb:Migrations:Mode`/`AllowNewerSchema`). Open: the SQLite constructor still opens its connection (`SqliteGovernanceRepository.cs:78-84`); no architecture test asserts I/O-free repository constructors; a repository used before `InitializeAsync` does not fail closed (no not-ready guard); no Helm option for a migration init job (D7). |
| ARC-I-05 (SQLite single shared connection + semaphore) | Open | `SqliteGovernanceRepository.cs:19-24` (`_connection`, `internal Connection`, `_lock`); about 140 `_connection` uses across the partials (Consent 44, Schema 29, Catalog 25, AccessProfiles 12, Audit 12, Outbox 5, others); in-memory default `Mode=Memory;Cache=Shared` (`:78`). |
| ARC-I-12 (no unified state-directory policy) | Open | Governance DB, anchors (`Persistence/AuditChainAnchorStores.cs`), `SqliteGovernanceLintSource.cs:18-19`, dev DB (`Api/Configuration/DevConfiguration.cs:155`) each resolve paths themselves; no `StateDirectory` option or `IGatewayStatePaths`. |
| ARC-I-13 (optional-everything constructors) | Partially resolved | `SqlConnector` requires `IColumnMaskingProvider` (B-02). Still open: `SqliteGovernanceRepository` constructor has 7 of 8 parameters defaulted to `null` (`:64-72`), PostgreSQL/SQL Server 6 of 8; `Infrastructure/Cache/EpochValidationService.cs:60,68` `eventBus ?? new InProcessChannelEventBus()` (owned by R2). |
| ARC-I-01 / ARC-L-03 (one God-class per backend, interfaces aliased) | Open | 13,623 LOC in 28 `*GovernanceRepository*.cs` files; each backend aliases one singleton to 13+ interfaces in `Api/Extensions/DependencyInjection/GatewayGovernanceServiceExtensions.cs:44-100`. |

## 2. Work packages

Conventions: tests live in `tests/Autheris.Tests.Unit` unless noted; `tests/Autheris.Tests.Architecture` for NetArchTest/Mono.Cecil guards; `tests/Autheris.Tests.Integration` for container-backed provider tests (set `DOTNET_USE_POLLING_FILE_WATCHER=true` in WSL). Every WP ends with a Release build with warnings as errors. Effort scale: S (<= 1 day), M (1-3 days), L (3-5 days), XL (> 5 days, split into reviewable PRs).

### Wave R1 - remainder

#### WP-ARC-B-07 - Endpoint use-case services (ARC-A-01)

- **Covers:** ARC-A-01.
- **Goal:** Api route classes only map HTTP to use-case results; no repositories or audit writes in handlers.
- **Design (incremental, three use cases, each its own PR):** `IEffectiveFilterInspector` (from `VirtualFilterEndpoints.EffectiveFiltersAsync`, `:224-357`: role check, SID/CSV parsing, catalog paging, per-table `ExplainAsync`, audit writes), `IOlapStagingService` (from `DuckDbOlapEndpoints.cs:90-243`: table-name resolution, budgets, access resolution, audit), `IOpenApiIngestionUseCase` (from `GovernanceEndpoints.cs:64-137`: tenant/domain resolution, 20 MB body read stays in the endpoint as stream limit only, audit composition). `ArrowExportEndpoints.cs:165` `SELECT * FROM {table}` moves into `IGovernedTableReadPlanner` (no SQL text in the handler). All use `RequestContext`/`CallerSecurityContext`. Audit goes through `IAuditWriter` / `GovernedQueryStages`.
- **Compatibility:** HTTP contracts unchanged (response bodies pinned by existing endpoint tests; error format changes are R4/T8).
- **Security:** authorization logic is moved verbatim first, then covered by tests.
- **TDD:** `EffectiveFilterInspectorTests`, `OlapStagingServiceTests`, `OpenApiIngestionUseCaseTests`; red test per use case: `NonAdminCaller_IsDenied_AndDenialIsAudited`.
- **Acceptance:** `VirtualFilterEndpoints`/`DuckDbOlapEndpoints`/`GovernanceEndpoints` inject no `I*Repository` and no `IAuditWriter` for these routes; a NetArchTest rule `Api.Endpoints` must not depend on `Autheris.Application.Interfaces.I*Repository` for the migrated classes (grow the allow-list downward).
- **Effort:** L. **Risk:** low-medium.

### Wave R3 - persistence and dialect ports

#### WP-ARC-B-09 - `ISqlDialectProvider` port and dialect consolidation (ARC-I-02, ARC-G-07, ARC-C-02 remainder)

- **Covers:** ARC-I-02, ARC-G-07 (tree capability), ARC-C-02 (session init, Oracle BindByName, error classifier).
- **Goal:** one registry keyed by `DatabaseDialect` replaces the enum `switch` sites; Application no longer owns provider-specific connection/command quirks.
- **Design:**
  - `Application/Sql/Dialects/ISqlDialectProvider` + `ISqlDialectRegistry.Get(DatabaseDialect)` returning `DialectDescriptor`: `TargetSqlDialect? Target` (the single mapping, absorbing `SqlDialectMapper`), `DialectCapabilities? Capabilities` (from `DialectCapabilityTable`), `IsExecutable`, `SupportsTreeCompile` (replaces the constant `TreeSqlCompiler.SupportsTreeCompile`), `SupportsCdc`, identifier quoting and literal helpers (delegating to `SqlIdentifierHelper`), parameter marker/name formatting (replacing `DatabaseDialectExtensions.FormatParameterMarker/Name`), parameter budget (replacing the `Infrastructure/Persistence/DatabaseParameterBudgetProvider.cs` switch).
  - Infrastructure implementations per dialect (`Infrastructure/Persistence/Dialects/{SqlServer,PostgreSql,Sqlite,Oracle,Databricks}DialectProvider`) own: `IDialectSessionInitializer` (replaces `Application/Services/DbSessionContextInitializer.cs`), Oracle `BindByName` enabling (moves `Application/Sql/OracleBindByName.cs` into Infrastructure; the type test `is BindByNameOracleConnection` at `SqlConnectionFactory.cs:102,129` becomes a provider hook), provider error classification (replaces `Common/DataAccessErrorClassifier.cs` string matching, port `IDataAccessErrorClassifier`).
  - The two enums stay (TrinoSqlEngine must not depend on `Autheris.Domain`); the mapping is confined to the registry (decision D4).
  - Migrate switch sites in priority order of the verification table (`SqlDataMaskingProvider`, `TreeSqlCompiler`, `AdvancedRlsFilterGenerator`, `MssqlProcedureInvoker`, `SqlProcedureRowScopeResolver`, `GatewayStartupValidator`, `GovernedSqlRewriter`). Remaining enum switches carry an architecture-test allow-list entry, and the allow-list shrinks monotonically.
  - `GatewayStartupValidator` provider switch and `CatalogSchemaModel` read the registry (`IsExecutable`, `SupportsTreeCompile`).
- **Compatibility:** no config change. `DatabaseDialectExtensions` helpers marked `[Obsolete]` then removed within the WP. `Admission_ExactSet` and `SqlDialectConsistencyTests` must stay green.
- **Security:** `Get(dialect)` throws for an unmapped dialect (never a default; SEC-X1-14 invariant); the capability tier remains the sole source of admission.
- **TDD:** `SqlDialectRegistryTests`: red test `EveryDatabaseDialect_HasDescriptor_OrIsExplicitlyUnsupported`; `DialectSwitchAllowListArchitectureTests`: count of `switch` on `DatabaseDialect` outside the registry and the engine must not exceed the committed allow-list; Integration `AstCompiler*ExecutionTests` unchanged.
- **Acceptance:** adding a dialect requires only a provider + capability entry + generator/binder (verified by a "fake dialect" test); allow-list at zero for Application except documented entries.
- **Effort:** L-XL (split by consumer group). **Risk:** medium (wide mechanical surface; the allow-list test is the safety net).

#### WP-ARC-B-10 - Application package diet and package-boundary guardrails (ARC-L-02, ARC-F-19, ARC-L-04)

- **Covers:** ARC-L-02, ARC-F-19, ARC-L-04 (MemoryPack, diagnostics), part of ARC-C-02.
- **Goal:** `Autheris.Application` compiles without DuckDB, Arrow, Parquet, MemoryPack; guardrails 2 and 6 of `architecture.md` section 4 become real tests.
- **Design:**
  - New project `src/Autheris.Infrastructure.Analytics` (references Application, Domain; packages `DuckDB.NET.Data.Full`, `Apache.Arrow`, `Parquet.Net`). Move `Olap/DuckDbOlapEngine.cs` (port `IDuckDbOlapEngine` stays in Application), `Serialization/{ArrowExportService,ArrowFlightSqlServer,ParquetExportService}.cs` **and their interfaces** `IArrowExportService`, `IArrowFlightSqlServer` (they expose `Apache.Arrow.RecordBatch`). Registrations in `Api/Extensions/DependencyInjection/GatewayExecutionServiceExtensions.cs:105-115` move to `AddAutherisAnalytics()`.
  - Architecture test update: `CutoverArchitectureTests.GovernedEntryPoints` entries for `ArrowFlightSqlServer` change namespace; `ProductionAssemblies` gains the new assembly; the call-graph scanner must still find exactly `GovernedSqlRewriter` and `GovernedSqlExecutor`.
  - MemoryPack: move `Domain/Model/CachedConsentEnvelope.cs` to `Infrastructure/Cache`, mapped from `ConsentResolution`; remove `MemoryPack` from Domain and Application csproj. Keep the wire layout byte-compatible (golden-bytes test guards Redis rolling upgrades).
  - Move `Mcp/Services/McpStdioRunner.cs` to `Autheris.Api/Mcp/`; `Docs/Services/FileSystemUniversalDocStore.cs` to Infrastructure behind its store interface; `GatewayDiagnostics` stays (tied to the T7 options split).
  - Prometheus: replace `using Prometheus` in `Sql/Errors/SqlCompilerErrorMapper.cs` with `System.Diagnostics.Metrics` through `GatewayDiagnostics`, then drop the package from Application.
  - YamlDotNet and Casbin.NET stay in Application for now (decision D3).
  - New `tests/Autheris.Tests.Architecture/PackageBoundaryArchitectureTests.cs`: Application references none of `DuckDB.NET*`, `Apache.Arrow*`, `Parquet*`, `Microsoft.Data.*`, `Npgsql`, `Oracle.*`, `StackExchange.Redis`, `MemoryPack*`, `prometheus-net*`; Domain references no external package except BCL (`System.ComponentModel.Annotations` explicitly allowed or replaced in R4). Guardrail 6: governance repository constructors open no connections (Cecil scan for `Open`/`ExecuteNonQuery`/`Task.Run`; initially allow-lists SQLite, emptied by WP-ARC-B-13).
- **Compatibility:** Docker/Helm images unchanged. Breaking for external consumers of `IArrowExportService`/`ArrowFlightSqlServer` (namespace `Autheris.Infrastructure.Analytics`); release note. `packages.lock.json` changes in four projects.
- **Security:** none new; Flight SQL and Arrow export keep reaching `Compile` + binder (call-graph test is the proof). Redis cache wire format unchanged.
- **TDD:** red test `PackageBoundaryArchitectureTests.Application_DoesNotReference_HeavyOrDriverPackages` (shrinking allow-list); `CachedConsentEnvelopeWireCompatibilityTests` (golden bytes).
- **Acceptance:** Application and Domain `.csproj` free of the packages above; Release build with warnings as errors and full unit/architecture suite green.
- **Effort:** L. **Risk:** medium (project move, `InternalsVisibleTo` and test references). **Serialize** with WP-11 and WP-09 on shared csproj/DI files.

#### WP-ARC-B-11 - Application folder structure and interface hygiene (ARC-C-10, ARC-C-16, ARC-C-18)

- **Covers:** ARC-C-10, ARC-C-16, ARC-C-18.
- **Design:** mechanical namespace moves guarded by architecture tests: dissolve `Application/Services` (21 files) into `Query/Table` (`SqlTableReadExecutor`, `GatewayExecutionService`), `Policy/Rls`, `Policy/Masking`, `Identity`, `Http`; feature interfaces move beside implementations; `Interfaces/` (38 files) keeps only cross-cutting ports (clock, caller context, audit, connection factory); single-implementation vendor ports (`IOpenJevClient`, `IOpenLineageClient`, `IItsmWebhookHandler`, `ILineageImpactAnalyzerService`, `IGdprAuditReportExporter`, `ITrafficDrainController`, `ISocketTokenValidator`, `IClientIpResolver`) are re-evaluated: keep when a second implementation or an Extensions boundary exists, otherwise register the concrete class. `Caching/` is either filled (`Policy/Services/AccessProfileCache.cs`, `VirtualFilters/Federation/VirtualFilterKeySetCache.cs`) or renamed `Policy/Caching`.
- **Compatibility:** namespace changes only. Large diff, freeze window 3 of the roadmap.
- **TDD:** existing suites are the regression net; add `ApplicationFolderConventionTests` (no types left in `Application.Services`).
- **Effort:** M to L (merge friction). **Risk:** low technically, high for conflicts. **Serialize last.**

#### WP-ARC-B-12 (remainder) - Constructor I/O guard, not-ready guard, required dependencies, Helm migration job

- **Covers:** ARC-I-04 remainder, ARC-I-13 (governance repositories), decision D7.
- **Design:**
  - A repository used before `IGovernanceStoreInitializer.InitializeAsync` throws `GovernanceStoreNotReadyException` (fail closed; `PingAsync` does not initialize lazily).
  - ARC-I-13: required non-null constructor parameters for the security-relevant dependencies of the three repositories (`IAuditChainAnchorStore`, `IKeyVaultSecretProvider` when `AuditHmacKeyVaultRef` is set, `ICallerContextProvider`, `IOptions<GatewayOptions>`); null-object defaults only in `GovernanceStoreFactory` (tests).
  - Architecture test `GovernanceRepositories_Constructors_PerformNoIo` (Cecil); SQLite stays allow-listed until WP-ARC-B-13 removes `_connection.Open()` from the constructor.
  - Helm: optional pre-install/pre-upgrade job running `Autheris.Api --migrate-governance`, app set to `GovernanceDb:Migrations:Mode=Verify` (the runbook already describes the manual variant, `docs/operations-runbook.md` section 4.ac).
- **TDD:** red test `Repository_UsedBeforeInitialize_ThrowsNotReady`; architecture test above; Helm template test for the job.
- **Effort:** M. **Risk:** low-medium (test constructors; `GovernanceStoreFactory` already used by 51 test files, 3 direct `new SqliteGovernanceRepository(` calls remain).

#### WP-ARC-B-13 - SQLite governance: pooled per-operation connections, WAL, single audit writer, state directory (ARC-I-05, ARC-I-12)

- **Covers:** ARC-I-05, ARC-I-12.
- **Goal:** remove the single shared `SqliteConnection` and the global `SemaphoreSlim`; reads run concurrently under WAL; the audit chain keeps exactly one serialized writer; all gateway SQLite files resolve under one documented state directory policy.
- **Design:**
  - `SqliteConnectionProvider` in Infrastructure: `Mode=ReadWriteCreate`, `Cache=Shared` only for the in-memory dev default (kept alive by one sentinel connection), `Pooling=True`, `DefaultTimeout`; per-connection pragmas (`busy_timeout=5000`, `foreign_keys`, `synchronous=NORMAL` in WAL) on open; `journal_mode` is database-level and applied once by the migration runner, honoring `GovernanceDb:JournalMode` with the existing DELETE fallback (R-42, today in `SqliteGovernanceRepository.Schema.cs:17-60`). Each operation does `await using var conn = await provider.OpenAsync(ct)`; the `internal Connection` accessor and `LockAsync/ReleaseLock` (`SqliteGovernanceRepository.cs:19-24`) are deleted; the `_connection` uses are converted per partial file.
  - Audit: `ProcessAuditChannelAsync` (`SqliteGovernanceRepository.Audit.cs:135`) and the lock uses in that partial (`:36,108,123,182`) move to a dedicated serialized writer (`SemaphoreSlim(1,1)` scoped to the audit chain) with `BEGIN IMMEDIATE`, so chain order stays sequential while reads no longer queue behind it.
  - State directory (ARC-I-12): self-contained `StateOptions.StateDirectory` (default `./state`, Development default unchanged) plus `IGatewayStatePaths` (`GovernanceDb`, `AuditAnchors`, `PluginRegistry`) used by `SqliteGovernanceRepository`, `AuditChainAnchorStores.cs`, `SqliteGovernanceLintSource.cs`, `DevConfiguration.cs:155`, `PluginManager`; documented "single writer per file" rule and backup set in `docs/operations-runbook.md`. Explicit `GovernanceDb:ConnectionString` and `Audit:ChainAnchorPath` still win. The in-memory fallback used when no connection string is configured (`SqliteGovernanceRepository.cs:78`) must also be rejected outside Development (`GatewayStartupValidator.cs:504-510` only checks an explicit connection string).
- **Compatibility:** default file DB behavior unchanged; network filesystems and WSL bind mounts keep `JournalMode=DELETE` as the documented workaround.
- **Security:** audit chain order and tamper evidence unchanged (single writer, same HMAC chain, anchor verification at init); `Cache=Shared` not used for file databases; lint source keeps `Mode=ReadOnly`.
- **TDD:** `SqliteGovernanceConcurrencyTests` (file DB): red test `ConcurrentCatalogReads_DoNotBlockOnAuditWrite`; `AuditChain_ConcurrentWriters_RemainSequentialAndVerifiable`; `StatePaths_ResolveRelativeToStateDirectory`.
- **Acceptance:** no `SqliteConnection _connection` field in `SqliteGovernanceRepository`; constructor I/O allow-list (WP-12 remainder) empty; runbook lists the state directory and backup set.
- **Effort:** M-L. **Risk:** medium-high (touches every SQLite partial; check connection-scoped state such as `last_insert_rowid` in Consent/Catalog transactions).

#### WP-ARC-B-14 - Governance stores per aggregate with shared dialect layer (ARC-I-01, ARC-L-03)

- **Covers:** ARC-I-01, ARC-L-03.
- **Goal:** stop the "one class, 13 interfaces, three dialect copies" shape; make consent and audit semantics a single implementation with thin per-dialect SQL.
- **Design (phased, each phase one PR, behavior-preserving):**
  1. Registration: the per-backend alias blocks in `AddAutherisGovernance` (`GatewayGovernanceServiceExtensions.cs:44-100`) become a per-aggregate registration table; the façade class remains temporarily.
  2. Audit store first: chain hashing, anchor handling, channel writer, tail verification, `IAuditLogRepository`, `IAuditChainExportSource` (3,359 lines across the three `Audit.cs` partials) extracted as `AuditChainEngine` plus `IAuditStoreDialect` (SQL text, parameter style, locking: PG `pg_advisory_xact_lock`, SQL Server `sp_getapplock`, SQLite writer lock). Rebases on WP-SEC1-07 (merged) and is coordinated with WP-ARC-A-07 (audit group commit).
  3. Consent store (`IConsentRepository`, `IConsentApprovalRepository`, 4,871 lines across the three `Consent.cs`): dialect-neutral state machine plus `IConsentStoreDialect`.
  4. Catalog, outbox, tenant registry, virtual filters: split only when touched.
  A shared `IGovernanceSqlDialect` supplies upsert, paging and boolean/timestamp fragments. `GovernanceStoreContractTests<TFixture>` runs identical assertions against SQLite, PostgreSQL and SQL Server containers. Dapper/EF Core is decision D5, not baseline.
- **Compatibility:** no schema change, no config change; DI interfaces stay.
- **Security:** audit chain bytes and HMAC input format identical (golden chain fixture verified by the new engine); latched chain-violation behavior preserved.
- **TDD:** `AuditChainEngineGoldenTests`, `GovernanceStoreContractTests` x3 providers, `ConsentStateMachineTests`.
- **Effort:** XL (4 phases). **Risk:** high; do not start before WP-ARC-B-13 is merged.

## 3. Dependencies

### 3.1 Between WPs

```
WP-09 (registry) -> SupportsTreeCompile / IsExecutable consumers (CatalogSchemaModel, GatewayStartupValidator)
WP-10 independent of WP-09 except shared csproj/DI files; WP-10 before WP-11
WP-12 remainder -> WP-13 -> WP-14 ; constructor I/O allow-list emptied by WP-13
WP-07 independent (Api endpoint files; ArrowExport uses the existing planner)
WP-11 last
```

### 3.2 On neighbouring scopes (reference only)

- **Architecture R0+R2:** `EpochValidationService` `eventBus ??` default (ARC-I-13 residual) is owned by R2; WP-ARC-A-07 (audit group commit) and WP-14 phase 2 edit the same audit partials and must be sequenced; `IPolicyCacheInvalidator` belongs to R0/R2 epoch work, WP-11 only relocates cache classes.
- **Architecture R4-R6:** WP-13 adds `StateOptions` as a self-contained class to ease the `GatewayOptions` split (ARC-L-01); the unit-test project split (C-19) comes after WP-10.
- **Features:** #28 aggregations build on the merged planner (`IGovernedTableReadPlanner`, `TableReadIntent` must stay extensible for `SelectExpressions`) and the flat governed path; no WP in this plan blocks them.

## 4. Recommended sequence

- Wave 5 of the roadmap: WP-ARC-B-07 (any time), WP-ARC-B-09 -> WP-ARC-B-10, WP-ARC-B-12 remainder -> WP-ARC-B-13 -> WP-ARC-B-14 (phases sequential).
- Wave 6: WP-ARC-B-11 (folder moves) last, in its freeze window.

Must be serialized: {09, 10, 11} (shared csproj/DI/namespace surface), {12r, 13, 14} (same persistence files). Parallel implementers do not build; the lead compiles and tests once per phase (Release, warnings as errors).

Wave exit criteria: R3 done when the package-boundary and constructor-I/O architecture tests pass with empty allow-lists and the dialect-switch allow-list is at its documented minimum.

## 5. Open decisions (stakeholder input)

| # | Decision | Options | Recommendation |
|---|---|---|---|
| D3 | Casbin and YamlDotNet in Application | leave; or extract `Autheris.Policy.Casbin` | Leave for this wave (managed code, no native or I/O weight); revisit with T7 `CasbinEnforcementService` decomposition (R4). |
| D4 | Keep two dialect enums (`DatabaseDialect` Domain, `TargetSqlDialect` engine) | merge vs. registry-owned mapping | Keep both (engine must not depend on Domain); confine the mapping to the registry. |
| D5 | Governance persistence technology | hand-written SQL per dialect with contract tests (plan baseline) vs Dapper vs EF Core | Baseline; reconsider Dapper only if the contract-test suite shows SQL drift is still the main cost after WP-14 phase 3. |
| D7 | Where migrations run in production | in-process hosted service (default, shipped) vs. Helm init job with `Mode=Verify` in the app | In-process by default; ship the init job as a Helm option for regulated deployments (WP-12 remainder). |
| D8 | New assembly `Autheris.Infrastructure.Analytics` vs. placing DuckDB/Arrow/Parquet into `Autheris.Infrastructure` | new assembly vs. existing | New assembly: keeps the native DuckDB binary and Arrow out of Infrastructure for slim deployments. |
