# PLAN-SIMPL: Project Simplification Program (Architecture Plan)

- Phase: 2 (Solution Architect). Date: 2026-11-05. Base: origin/main fe0e131d (branch `docs/plans-project-simplification`).
- Input PRD: [2026-11-05-req-project-simplification.md](2026-11-05-req-project-simplification.md) (REQ-SIMPL-01..13; owner decisions OD-1..OD-45 in PRD section 2.0; D1-D17).
- Related: [2026-10-26-plan-architecture-maintainability.md](2026-10-26-plan-architecture-maintainability.md) (ARCH-MNT-22 dialect dedup; section 2.4 ignored flags). ADR: [ADR-019](../adr/ADR-019-sql-engine-build-vs-buy.md) (Accepted by the owner 2026-11-05: keep and shrink the in-house engine).
- Tracking: issues #509-#514 (Stage 0), #515 (Stages 1-3, one child issue per stage and per module). CI-speed issues #516-#522 are coordinated, not duplicated.
- Constraints: no production deployment (breaking changes allowed, no shims); forward-only governance migrations with v1.5.0 frozen base; English only; workflow files under `.github/workflows/` are changed only by the WP that owns the change, and every such change is an **owner-approved item** (marked `[WF]`).

> **Precedence:** the owner decisions OD-1..OD-45 (PRD section 2.0), [ADR-020](../adr/ADR-020-unified-authorization-model.md) (Accepted, OD-30) and the corrections in [`simplification/README.md`](simplification/README.md) override any conflicting body text of this plan; where two owner decisions conflict, the later one wins. The developer-ready WP specs in `simplification/` carry the detail; this plan is the summary.

## 1. Verified code facts used by this plan (verified on fe0e131d, 2026-11-05)

| Fact | Location |
|---|---|
| R2R switch | `src/Autheris.Api/Autheris.Api.csproj:34-39` (comment block + `<PublishReadyToRun Condition="'$(SelfContained)' != 'true'">true`); `docker-publish.yml` has no R2R reference |
| Version sources | `Directory.Build.props:3` `<Version>1.2.1</Version>`; `deploy/helm/autheris/Chart.yaml:5-6` 1.0.0/"1.0.0"; `values.yaml:9` `tag: ""`; `release.yml:25-39` validates and checks out the tag but never passes a version |
| Governance store default | `src/Autheris.Api/appsettings.json:39` `"Provider": "Sqlite"` |
| SQLite governance wiring | `GatewayGovernanceServiceExtensions.cs:100` (`RegisterGovernanceAggregateStores<Sqlite...>`), `StartupChecks/StorageStartupChecks.cs:47-62,118,127` |
| SQLite data-source wiring (not OD-1) | `GatewayStorageServiceExtensions.cs:130` (`SqliteColumnMetadataReader`), `Persistence/Catalog/SqliteTypeAffinity.cs` (used by `GovernedCatalogProvider.cs:337`), `TrinoSqlEngine/Ast/Security/CatalogTypeMap.cs:289-302` |
| Governance families | SqlServer 26 partials / 5,276 lines; PostgreSql 26 / 5,243; Sqlite 26 / 5,241. Largest partials (PG/SS): Audit 1,064/1,120, Catalog 1,031/1,052, Consent 809/807 |
| Dedup already merged (ARCH-MNT-22) | `Persistence/*SqlCore.cs`: Consent 997, Catalog 840, CdcStream 689, Audit 629, DataOwnerAssignment 305, ConsentGrant 160, ItsmOutbox 136, WebhookOutbox 125, `SqlCoreBinding.cs` 35 (4,100 lines total); `Persistence/Dialects/SqlDialectProviders.cs` 147; `DialectValueCodec.cs`. Nine PG partials already delegate to cores; SQL literals stay per dialect (step-1 lesson: -317 instead of -610) |
| Migrations | `Persistence/Migrations/{Postgre,SqlServer,Sqlite}BaselineSchema.cs`, `*GovernanceMigrationRunner.cs`, `Steps/{postgresql,sqlserver,sqlite}`, `Shape/SqliteSchemaCatalogReader.cs` (used only by the SQLite runner) |
| Runtime loading exists | `Infrastructure/Plugins/PluginManager.cs` (SHA-256 trust list `Plugins:TrustedPluginHashes`, manifest never a trust anchor outside Development), `PluginAssemblyLoadContext.cs` (collectible ALC), `Plugins/Auth/AuthPluginHost.cs`; loaded after `Build()` (`GatewayApplicationBuilderExtensions.cs:154-160`), so plugins cannot register DI or endpoints |
| Extensions coupling | `Autheris.Api.csproj:8` references `Autheris.Extensions`; entry `ExtensionsServiceCollectionExtensions.AddGatewayExtensions`; 9 Api files `using Autheris.Extensions` |
| Heavy packages | Application: Casbin.NET, DuckDB.NET.Data.Full, Apache.Arrow, Parquet.Net, YamlDotNet. Infrastructure: Microsoft.Data.Sqlite, Microsoft.Garnet, Oracle.ManagedDataAccess.Core. Api: Microsoft.AspNetCore.Authentication.Negotiate. No central package management Baseline before the program. Target core set: DuckDB.NET.Data.Full (OD-15), Apache.Arrow, Oracle.ManagedDataAccess.Core (OD-12), Microsoft.Data.SqlClient, Npgsql; deleted: Casbin.NET (WP-14), Microsoft.Garnet (WP-15, OD-17); to modules: Parquet.Net (Lakehouse), YamlDotNet (with dbt if only dbt uses it); Negotiate stays core (OD-21); Microsoft.Data.Sqlite leaves at OD-2 step 3. |
| Dialect generators | `src/TrinoSqlEngine/Ast/Generators/`: SqlServer 693, Oracle 559, Analytical (DuckDb/Snowflake/Ansi) 453, Sqlite 433, PostgreSql 375, Databricks 364, base 1,828; enum `src/TrinoSqlEngine/TargetSqlDialect.cs` (8 values) |
| Dialect tests | TrinoSqlEngine.Tests name-matching files: Oracle 973, Sqlite 1,053, Databricks 311, DuckDb 279 lines; all test projects name-matching: Oracle 3,759, Databricks 2,804, DuckDb 2,371 |
| Option guard | `Api/Configuration/UnknownGatewayKeyGuard.ThrowIfUnknownKeys` throws on keys outside `GatewayConfigurationSchema` (removed keys fail fast for free once removed from the schema) |
| Ratchet baselines | `tests/Autheris.Tests.Architecture/Baselines/`: unconsumed-options, options-consumed-indirectly, test-sleep-counts, source-size-ceilings, project-edges, namespace-assembly-violations, bare-error-body-counts, datetime-now-counts, ingress-exemptions |
| Workflows | ci.yml (unit job, 4 integration shards, Spark job), parity-nightly, sql-compiler-gate-nightly, simulation-nightly, release, docker-publish, trivy, codeql, policy-lint, governance-immutability Baseline before the program. Target: Spark/Databricks and full Oracle suites nightly with a security-tagged PR smoke subset (OD-12, #522), real-workspace Databricks nightly once secrets exist (Q21), `parity-sqlite` removed at OD-2 step 2, footprint job added (WP-35); `[WF]` bundles approved (OD-17). |
| Process files | `.github/CODEOWNERS` 22 lines; `.agents/agents` 9 files, `.agents/skills` 8 dirs; no `.claude/agents` in the repo; 3 Stryker configs at repo root |
| Application folders | 49 (sizes: Sql 20,671; Governance 15,981; Mcp 11,400; Catalog 6,540; Services 6,367; Data 6,290; ... Integrations 23; Observability 131; Performance 144) |

## 2. Architecture decisions

### AD-1 Governance persistence: one dialect-neutral repository (decision)

Options evaluated (after OD-1, two engines):

| Option | Effect | Verdict |
|---|---|---|
| A. One `GovernanceRepository` (partials per aggregate) + `IGovernanceSqlDialect` fragment provider, extending the merged `*SqlCore` pattern | SQL text written once in an ANSI subset both engines accept; engine differences isolated in about 15 fragment members | **Chosen** |
| B. Accept two implementations (status quo minus SQLite) | ~10.5k duplicated lines remain; every change twice; drift already produced DEF-02 (tenant-parse fallback) | Rejected |
| C. PostgreSQL only | -5.3k lines at once, but contradicts D4 (keep SQL Server) | Deferred to D4 review after Stage 2; option A makes it a one-file deletion later |
| D. Introduce EF Core or Dapper | EF migrations conflict with frozen forward-only SQL steps; Dapper adds a dependency for mapping that `SqlCoreBinding`/`DialectValueCodec` already cover | Rejected (default taken) |

Design of option A:
1. SQL is written once using constructs both engines accept: double-quoted identifiers (SqlClient sessions run with `QUOTED_IDENTIFIER ON`), `OFFSET n ROWS FETCH NEXT m ROWS ONLY` paging, parameters for booleans and timestamps (`DialectValueCodec`), `COALESCE`, `CASE`, standard joins.
2. `IGovernanceSqlDialect` (in `Persistence/Dialects/`, replacing the governance part of `SqlDialectProviders.cs`) owns only real divergences: `Upsert(table, keyColumns, valueColumns)` (MERGE vs `ON CONFLICT`), `InsertReturning(column)` (`OUTPUT INSERTED` vs `RETURNING`), `LockRowsForUpdate` (`WITH (UPDLOCK, ROWLOCK)` vs `FOR UPDATE`), `UtcNow`, `JsonValue(column, path)`, `StringAggregate`, `LimitWithoutOrder`, `IsUniqueViolation(DbException)`, `IsTransient(DbException)`, `ParameterBudget` (existing `DatabaseParameterBudgetProvider`), `CreateConnection`. Two sealed implementations: `PostgreSqlGovernanceDialect`, `SqlServerGovernanceDialect`.
3. Each existing `*SqlCore` absorbs its two dialect partials; the `*SqlCore` name disappears (it becomes the partial of `GovernanceRepository`).
4. The tenant/audit predicates and the DEF-02 fail-closed tenant parse are implemented once and pinned by the contract suite (WP-SIMPL-19).
5. Migrations (default taken, deviation from REQ-SIMPL-05.3 recorded): baseline and step trees stay hand-written per engine because DDL types genuinely differ and the forward step trees are tiny (pg 56, sqlserver 75 lines); a generator would cost more than it saves. A new step-parity architecture test requires every step id in both trees. `GovernanceMigrationFreezeTests` stays unchanged.
6. LOC metric (made explicit): governance repository LOC = `*GovernanceRepository*.cs` + `*SqlCore.cs` + governance dialect classes. Baseline after OD-1: 10,519 + 3,964 (cores without ItsmOutbox) = about 14,480. Targets: Stage 2 exit <= 7,000; program exit <= 6,000 (PRD: 5,500 on a metric that excluded the 4,100 core lines; revised +9%, within the 20% allowance). Duplicated-code ratio < 15% (ARCH-MNT metric).

### AD-2 SQL engine: keep, reduce scope (decision; ADR-019, Accepted 2026-11-05: keep and shrink)

Keep the in-house TrinoSqlEngine (ANTLR Trino grammar, own AST, `AstSecurityVisitor`, dialect generators). Core data-source dialects (OD-12, binding): PostgreSQL, SQL Server, Oracle, Databricks; SQLite until OD-2 step 3 (WP-SIMPL-31). Governance store stays PostgreSQL + SQL Server only (OD-1). The DuckDB generator stays core as the federation staging target, with the Olap engine (OD-15). Ansi stays a generator-only dialect, not admitted as data source (OD-14, Q6); the Snowflake generator is deleted in WP-SIMPL-13 (OD-14, binding, revised). `TargetSqlDialect` keeps SqlServer, PostgreSql, Oracle, Databricks, Ansi and DuckDb (Sqlite until WP-SIMPL-31). Engine + tests 59,390 -> <= 48,500 lines (ADR-019; 45,000 + kept Oracle/Databricks generators and engine tests 2,207 + kept Ansi and DuckDb generator code, rounded up). CI gate: a generator without a parity baseline file fails the build. Stryker configs 3 -> 2.

### AD-3 Module mechanism: runtime-loaded first-party modules with a signed manifest (decision, D3 confirmed)

1. Contract project `src/Autheris.Modules.Abstractions` (no dependency besides ASP.NET Core abstractions and `Autheris.Domain`):
   - `IAutherisModule { string Id; void ConfigureServices(IServiceCollection, IConfiguration, IModuleHostContext); void MapEndpoints(IEndpointRouteBuilder); IReadOnlyList<string> ConfigurationSections; }`
   - optional `IAutherisGraphQLModule` (in `Autheris.GraphQL`) for type extensions.
   - `IModuleHostContext` exposes only governed services: query pipeline (SQL gate), catalog read, audit writer, consent/RLS evaluation, `TimeProvider`, `IHttpClientFactory` with the egress allow-list. No raw data-source `DbConnection`.
2. Layout: `modules/Autheris.Modules.<Name>/` and `modules/Autheris.Modules.<Name>.Tests/` (outside `src/`, in `Autheris.sln`); each module is published to `modules/<id>/` with `module.json`.
3. Manifest `module.json`: `id`, `version` (must equal the host version; one tag builds host and modules), `abstractionsVersion`, `entryAssembly`, `entryType`, `files[{path, sha256}]`, `signature` (ECDSA P-256 over the canonical manifest without `signature`, BCL `System.Security.Cryptography.ECDsa`, no new package).
4. Trust anchor: release public key embedded in `Autheris.Api` as a resource. Only first-party signed modules are supported (Q3); operators cannot add keys. Development builds accept an unsigned manifest only when `ASPNETCORE_ENVIRONMENT=Development` and `Gateway:Modules:AllowUnsignedInDevelopment=true` (same pattern as `PluginManager`).
5. Loader `Autheris.Api/Hosting/ModuleLoader` runs in `Program.cs` before `builder.Build()`: reads `Gateway:Modules:Enabled` (explicit allow-list); for each id verifies signature, version equality, every file hash, rejects files not listed in the manifest, loads into a non-collectible `ModuleAssemblyLoadContext` that resolves `Autheris.*`, `Microsoft.*`, `System.*` from the default context and module-private dependencies from the module folder; a private dependency whose name collides with a host assembly fails startup. Any failure (enabled but missing, unsigned, hash mismatch, version mismatch, duplicate id, exception in `ConfigureServices`) stops startup with an error naming the module. Present but not enabled modules are not loaded.
6. Endpoints: the host maps each module inside a host-owned route group that applies the default authorization policy, `EndpointSecurity` and, for data endpoints, the access decision point (ADR-020; no `RebacEndpointFilter`, ReBAC is removed by WP-SIMPL-46); existing routes keep their paths.
7. Configuration: `UnknownGatewayKeyGuard` accepts a module's `ConfigurationSections` only when the module is loaded; keys of a module that is not installed fail fast with "module <id> is not installed".
8. Trust-code reuse: SHA-256 and manifest parsing move from `PluginManager`/`PluginTrustList` into `Infrastructure/Modules/Trust` and are shared by plugins and modules. The existing plugin feature stays (KEEP as module host base).
9. Image flavors: `autheris` (core, no modules) and `autheris-full` (core + all first-party modules under `/app/modules`). Build argument `FLAVOR` in `Dockerfile*` `[WF]` docker-publish.yml publishes both tags from the same version.
10. Fallback (compile-time, default if Phase 3 rejects runtime loading or the first real module, Lakehouse in WP-SIMPL-27, shows that registration cannot work pre-Build): `-p:AutherisFlavor=full` adds `ProjectReference`s to modules; discovery via `[assembly: AutherisModule(typeof(...))]`; same `IAutherisModule` contract, no ALC, no signature. The runtime-versus-compile-time decision is taken in WP-SIMPL-27 (OD-37). The Api -> Extensions reference is removed in both variants, by WP-SIMPL-29 (last module WP), after WP-SIMPL-26 has moved OData out of `Autheris.Extensions` into core.
11. Module rule (PRD 3.1): each module has a named owner in CODEOWNERS; ownerless modules are deleted at the next review.

### AD-4 Measurement

LOC, endpoint, folder, option and test counts are measured by `benchmarks/scripts/repo-metrics.sh` (checked in by WP-SIMPL-10) on the merge commit; "src LOC" means C# under `src/` only, modules are counted separately under `modules/`. Footprint and CI time follow the protocol in WP-SIMPL-10.

### AD-5 SQLite removal staging (OD-1 binding, OD-2 default taken)

| OD-2 step | WP | Content | Owner stop point | Effect |
|---|---|---|---|---|
| 1 governance store (OD-1, binding) | WP-SIMPL-11 | Sqlite governance families, runner, baseline, steps, guards deleted; `Provider=Sqlite` fails closed; PostgreSQL default everywhere; governance tests on shared PostgreSQL fixture | none (binding) | about -6,900 src; governance dialects 3 -> 2 |
| 2a parity | WP-SIMPL-21 | PostgreSQL reference engine, SQL Server cells executed, SQLite cells kept until both are green | after 2b | parity becomes container-backed; nightly parity slower by one container start per engine |
| 2b tests | WP-SIMPL-22 | real-DB tests leave Unit (fakes or PostgreSQL fixture) | yes (Stage 2 exit) | Unit faster (no file DB, no host); Integration grows; CI budget per section 3 |
| 3 data-source dialect | WP-SIMPL-31 | generator, affinity, zone table, column reader, package, `parity-sqlite` job | yes (before Stage 3) | about -1,000 src; `Microsoft.Data.Sqlite` gone from src and 4 test projects |

Never delete a SQLite parity cell before the PostgreSQL and SQL Server cells for the same case are green. CI design (sharding, caching, job split) stays in #516-#522; this program only requires one container per test assembly and the section 3 CI budget.

## 3. Stage targets (revised where justified; zero targets never weakened)

| Metric | Baseline | Stage 1 exit | Stage 2 exit | Program exit |
|---|---|---|---|---|
| src LOC (src/ only) | 302,461 | <= 278,000 (PRD 250k; DELETE footprint about 24k after OD-12 keeps 3,017 Oracle/Databricks lines) | <= 258,000 (PRD 230k) | <= 220,000 core (accepted by the owner, OD-17; PRD 180k: modules move out about 46k, deletes 24k, unification 8k; Olap 3.4k stays core per OD-15, so the margin is tighter and is absorbed by deleting ownerless modules and the plan-id/option cleanup); modules <= 50,000 |
| Map* endpoints in src | 275 | <= 240 | <= 230 | <= 140 (unchanged: Oracle/Databricks own no endpoints) |
| Application top-level folders | 49 | <= 40 | <= 32 | <= 25, 0 duplicates |
| Governance repository LOC (AD-1 metric) | ~18,580 (3 families + cores) | ~14,480 | <= 7,000 | <= 6,000 |
| Governance dialects | 3 | 2 | 2 | 2 |
| Data-source dialects (generators) | 6 files / 8 enum values | 5 (PG, SS, Oracle, Databricks, SQLite); generator-only Ansi and DuckDb (federation staging); Snowflake deleted | 5, each parity-tested (PG/SS/Oracle real engines; Databricks contract on PR, real or Spark nightly), none Pending | 4 (PG, SS, Oracle, Databricks), each fully parity-tested |
| Core image / cold start P50 | 441 MB / 5.58 s (as configured) | measured after R2R removal and package deletions | measured | <= 325 MB / <= 3.0 s; warm P99 <= 3.47 ms |
| Options classes / settable props / `*Enabled` | 173 / ~1,173 / 96 | measured | measured | <= 90 / <= 600 / <= 40 |
| Unconsumed options baseline | 46 lines | 0 (Stage 0) | 0 | 0, file removed |
| Tests: container+integration share / Substitute.For / Received / sleeps | 6.3% / 4,159 / 986 / 92 | ratchets active | >= 12% / <= 3,000 / <= 700 / <= 40 | >= 15% / <= 2,000 / <= 400 / <= 20 |
| Test LOC | 382,125 | follows deletions | <= 307,000 | <= 247,000 (+6,563 kept Oracle/Databricks test lines, rounded) |
| CI time (OD-6: PR wall-clock push to last required check, median, queue time included) | measured in WP-SIMPL-10 | not worse than baseline | <= 40 min | < 30 min (OD-6); full/nightly reported, no target |
| Hosted services / DI registrations | 39 / 549 | measured | measured | <= 20 / <= 330 |

OD-15 recompute (2026-11-05): federation (`FederatedDuckDbExecutionService` 744, `CrossSourcePlanner` 853), DuckDb generator (about 290) and table cache stay core, so about 1,900 lines no longer move to modules (core exit +2,000, modules -2,000); the DuckDB native library (68 MB linux-x64 in `DuckDB.NET.Bindings.Full`) stays in the core image (image target +68 MB, rounded up); cold start unchanged because DuckDB is loaded on the first federated query, verified in WP-SIMPL-10 (a regression makes it lazy). OD-14: Snowflake (about 95 lines) deleted, Ansi kept; net effect within rounding. Core exit is +22% over the PRD because of binding OD-12/OD-15, not a data-driven revision.

OD-12 recompute method (2026-11-05): src LOC = previous target + `wc -l` of the kept Oracle/Databricks src files (3,017, of which generators 923) minus the Snowflake/Ansi generator lines still deleted under D16 (about 165), rounded up to the next thousand; test LOC = previous target + name-matching Oracle (3,759) and Databricks (2,804) test lines. Image: previous target + `Oracle.ManagedDataAccess.dll` (about 5.2 MB, net8.0 lib) rounded up; Databricks adds no package. Cold start unchanged: the Oracle assembly loads only on the first Oracle connection and Databricks registers HTTP clients only when configured (verified in WP-SIMPL-10 with the cold-start protocol; a regression above 3.0 s makes Oracle/Databricks registration lazy). Endpoints unchanged: no `Map*` endpoint is owned by Oracle/Databricks code.

## 4. Work packages

Summary (order = roadmap order; L-PER = serial persistence/DDL lane; L-AUTHZ = core authorization lane of OD-19..OD-37; the binding execution order is the table in [`simplification/README.md`](simplification/README.md)):

| WP | Stage | Issue | Title | Lane | Deps | src LOC | Size (pd) |
|---|---|---|---|---|---|---|---|
| 01 | 0 | #509 | Remove ReadyToRun | L-REL | - | -10 | S 0.5 |
| 02 | 0 | #510 | Version from tag, Helm, CHANGELOG `[WF]` | L-REL | - | +40 | M 2 |
| 03 | 0 | #511 | Remove perf claims, readme-copywriter | L-DOC | - | docs | S 1 |
| 04 | 0 | #512 | Docs of non-existent components | L-DOC | - | docs | S 1.5 |
| 05 | 0 | #513 | Docs hygiene + docs tests | L-DOC | 04 | docs | M 2.5 |
| 06 | 0 | #514 | Unconsumed options + config reference | L-CFG | - | -500 | M 3 |
| 07 | 0 | #515 | Freeze ratchets, PR size check `[WF]` | L-RAT | - | tests | M 2 |
| 08 | 0 | #515 | CODEOWNERS, .mailmap | L-DOC | D6 | - | S 0.5 |
| 09 | 0 | #515 | Agent definitions cleanup | L-DOC | 03 | - | S 0.25 |
| 10 | 0 | #515 | Measurement protocol + baselines | L-RAT | 01 | scripts | M 2 |
| 11 | 1 | #515 | SQLite governance store removal (OD-1) | L-PER | 10 | -6,900 | L 6 |
| 12 | 1 | #515 | Delete catalog vendors, Backstage, ITSM | L-PER | 11 | -4,000 | M 3 |
| 13 | 1 | #515 | Connection-secret policy, Snowflake generator deletion, dialect admission | L-ENG | - | +100 | M 2 |
| 14 | 2 | #515 | Delete Casbin (no data-path replacement, OD-30) | L-AUTHZ | 06, 46 PR 1-5, 43 PR 4 (Envoy), 45 PR 1-3 | -3,220 | S 1.5 |
| 15 | 1 | #515 | Delete embedded Garnet, traffic shadowing | L-DEL | - | -900 | M 1.5 |
| 16 | 1 | #515 | Delete Flight-over-HTTP and the dev portal (FinOps via 42, Mesh stays core, OD-20/OD-21/OD-37) | L-DEL | 04 | -1,200 | M 1 |
| 17 | 1 | #515 | Merge duplicate folders | L-DEL | 12 | -750 | M 2.5 |
| 18 | 1 | #515 | Borderline triage (D2): differential privacy, CDN purge, SchemaRegistry only (OD-4, OD-11, OD-18) | L-DEL | - | -2,350 | S 1.5 |
| 19 | 2 | #515 | Governance contract suite (PG + SS) | L-PER | 11, 12 | tests | M 4 |
| 20 | 2 | #515 | Unified governance repository (20a-20e) | L-PER | 19 | -7,500 | L 11 |
| 21 | 2 | #515 | Parity on real engines, dialect gate `[WF]` | L-ENG | 13 | tests | L 7 |
| 22 | 2 | #515 | Real-DB tests out of Unit | L-ENG | 11, 21 | tests | L 7 |
| 23 | 2 | #515 | Test-suite restructuring + ratchets | L-TST | Stage 1 | tests | L 6 |
| 24 | 2 | #515 | Application folders <= 32 | L-DEL | 17, 18 | ~0 | M 3 |
| 25 | 3 | #515 | Module host `[WF]` | L-MOD | Stage 2 | +900 | L 7 |
| 26 | 2-3 | #515 | OData core hardening (OD-24): move OData from `Extensions` into core, Power BI/Excel interop tests, OD-22 controls | L-AUTHZ | 46; before 29 | +150 | M 3 |
| 27 | 3 | #515 | Module Lakehouse (pilot, OD-24; runtime vs compile-time decision, OD-37) | L-MOD | 25 | -4,600 | M 2 |
| 28 | 3 | #515 | Modules dbt, OpenMetadata | L-MOD | 27, 17, 44 | -8,000 | M 4 |
| 29 | 3 | #515 | Modules Events (CDC+Webhooks delivery), Lineage; removes the Api -> Extensions reference | L-MOD | 27, 20, 26 | -11,000 | L 6 |
| 30 | - | - | Cancelled (OD-21): SCIM, TOTP, Negotiate, Envoy stay core (WP-SIMPL-43) | - | - | 0 | 0 |
| 31 | 3 | #515 | Full SQLite removal step 3 (OD-2) `[WF]` | L-ENG | 21, 22 | -1,000 | M 3 |
| 32 | 3 | #514 | Configuration surface reduction | L-CFG | 27-29 | -1,500 | M 4 |
| 33 | 3 | #515 | Plan-id comment cleanup | L-MOD | 27-32 | -1,900 | M 2 |
| 34 | 3 | #515 | README, feature docs, migration guide | L-DOC | 33 | docs | M 3 |
| 35 | 3 | #509 | Footprint and CI-time budget job `[WF]` | L-RAT | 10, 27-29 | scripts | S-M 2 |
| 36 | 0 (parallel) | #469, #505-#508, advisories | Security lane: merge `fix/security-batch-5` first, fail-closed audit bundle (#505, #507), DML bundle (OD-3), FIX-IN-CORE issues and advisories (see the assessment section) | L-SEC | - | small | L 8-12 |
| 37 | 1 (engine lane) | OD-16, OD-36 | Databricks authentication modes: OBO, service principal (OAuth M2M), PAT in production; gateway-enforced mode; Databricks tier flip on WebSQL, Trino and MCP (X1-DBX) | L-ENG | 36 (batch 5) | +600 | M 4 |
| 38 | 1-2 | #470, #484 | Trino client protocol `/v1/statement`, clean core design (OD-18) | L-ENG | -; step 6 (metadata access status): 46 PR 1, 41 discovery | +570 | M 5 |
| 39 | 1-2 | #491 | Governed stored procedures, clean core design (OD-18) | L-ENG | - | -660 | M 7 |
| 40 | 1-2 | GHSA-p234 | Docs ingestion and MCP doc tools, clean design (OD-18) | L-PER (40b-d) | 40a: -; 40b: 11, 20a-20e; 40c: 40b; 40d: 40c | +320 | M 6-7 |
| 41 | 1-2 | OD-19 | Authorized data discovery (replaces OpenSchema) | L-AUTHZ | 36 SP0; PR 3+: 46 PR 1, 43 PR 3b; PR 4: 39 PR 4; PR 5: none (REST route delivered later by 47 PR 5); PR 6: 40c | -260 | L 10.5 |
| 42 | 2 | OD-20 | Usage accounting per user and AD group (replaces FinOps) | L-PER | PR-2+: 11, 19, 20a-20d; PR-3b: 46 PR 1; PR-7: 37 | +1,710 | L 14 |
| 43 | 1-2 | #482, #489 | Authentication and authorization core: direct and ForwardAuth modes, SCIM groups (OD-21, OD-27) | L-AUTHZ | 36 SP0, SP1; PR 4 (Envoy): 46 PR 3; PR 5a: 11; PR 5c: 36 SP2 | +1,420 | L 13 |
| 44 | 1-2 | #459 | No source-metadata reads (OD-25) | L-ENG | 12, 17, 39 PR 3; before 28 | -1,570 | M 6 |
| 45 | 1-2 | #505 | Governed write access, WebSQL only (OD-26, OD-33, OD-35) | L-AUTHZ | 36 SP0, SP3; PR 1-3 before 46 PR 1; PR 4-6 after 46 PR 1 | +1,680 | L 9-11 |
| 46 | 1-2 | OD-30 | Unified authorization model: one grant model, one decision point (ADR-020) | L-AUTHZ | 36 SP0, 43 PR 2-3b, 45 PR 1-3; PR 6: 43 PR 5a/5b | -1,950 | L 10 |
| 47 | 1-2 | OD-28, OD-29 | Catalog and consent API, workflow internal or external, signed time-limited grants | L-AUTHZ | 12, 43, 46 PR 1, 41 PR 3 and PR 5, 45 PR 1-3; PR 7: 48 PR 5-6 | +2,350 | L 16 |
| 48 | 1-2 | OD-38..OD-43 | Row policies, dynamic object coverage, per-cell column state, effective-access report | L-AUTHZ | 46 PR 1 (PR 1, 2, 5), 46 PR 2 (PR 3, 4) | +940 | L 12 |
| 49 | after freeze | OD-44 | Data products: views with load modes and procedures, own product owner | L-AUTHZ | Stage 3 exit; 46, 47, 48, 39, 41, 42, 44, 45, 21, 20 | +1,400 | L 10-11 |

Effort in person-days (pd). LOC delta is src (tests in brackets). Every WP: TDD per lifecycle; deleting WPs add their rows to the migration guide and the CHANGELOG "Removed" section in the same PR (section 6); removed config keys disappear from `GatewayConfigurationSchema` so `UnknownGatewayKeyGuard` fails fast.

### Stage 0 - Quick wins (issues #509-#514)

#### WP-SIMPL-01 Remove ReadyToRun (#509)
- Scope: delete `Autheris.Api.csproj:34-39` R2R comment and `PublishReadyToRun`; keep `RuntimeIdentifiers` only if the locked restore needs it (verify with `dotnet restore --locked-mode`); delete `benchmarks/Autheris.Benchmarks/ReadyToRunBenchmark.cs`; keep `benchmarks/results/r2r-spike-report.md` as evidence. No workflow edit (docker-publish.yml has no R2R reference).
- Deps: none. LOC: -10 (-ReadyToRunBenchmark). Effort: S, 0.5 pd. Risk: locked restore graph change (low).
- Tests: `NoReadyToRunArchitectureTests.No_project_or_props_file_enables_PublishReadyToRun` (scans all `*.csproj`, `Directory.Build.*`).
- AC/target: no `PublishReadyToRun` in the repo; image measured by WP-SIMPL-10 is <= 373 MB.

#### WP-SIMPL-02 Version from git tag, Helm coupling, CHANGELOG (#510) `[WF]`
- Scope: `Directory.Build.props` `<Version>0.0.0-dev</Version>` (overridable); `Chart.yaml` `version`/`appVersion` `0.0.0-dev` in git; `release.yml` derives `VERSION=${TAG#v}` and passes `-p:Version=$VERSION` to publish, `helm package --version $VERSION --app-version $VERSION` (chart `appVersion` = `X.Y.Z`), `docker-publish.yml` pushes the image as both `vX.Y.Z` and `X.Y.Z` (OD-37); new release-validation step asserts tag == assembly `InformationalVersion` (read from the published dll) == chart version == chart appVersion == image tag. No MinVer (Q11). `values.yaml` tag stays empty and falls back to the packaged appVersion.
- CHANGELOG.md (Keep a Changelog): backfilled v1.0.0..v1.7.0 grouped by minor; v1.0-v1.4 marked "unsupported upgrade base (oldest supported: v1.5.0)"; `## [Unreleased]` with `### BREAKING`, `### Removed`, `### Moved to modules`, `### Fixed`, `### Security`. Release notes = CHANGELOG section of the tag, extracted by a script in `release.yml`.
- Deps: none. LOC: +40 workflow/script, +~150 CHANGELOG. Effort: M, 2 pd. Risk: docker-publish.yml builds on main without a tag (keeps `0.0.0-dev+sha`).
- Tests: `ReleaseVersionArchitectureTests.Directory_build_props_holds_no_product_version`, `...Chart_versions_are_placeholders`; release-validation step itself is the runtime check.
- AC/target: inconsistent versions = 0 on the next tag; `helm install` default pulls the chart's own appVersion, and a test asserts that the default rendered image tag is one of the pushed tags (`vX.Y.Z` or `X.Y.Z`); CHANGELOG present.

#### WP-SIMPL-03 Remove unverified performance claims (#511)
- Scope: `README.md:56-58` numbers and the HotChocolate/Native AOT version text; `README.md:25` link text "64,500+ req/s BenchmarkDotNet reports" (and the `docs/comparisons/README.md` link it carries); delete `docs/comparisons/performance-benchmarks.md` (no protocol exists) and its links; delete `.agents/skills/readme-copywriter/` and `.agents/agents/readme-copywriter.md` (prescribes the numbers, `SKILL.md:36`).
- Deps: none. LOC: docs only. Effort: S, 1 pd.
- Tests: `DocsClaimsArchitectureTests.Performance_figures_link_a_results_file` (regex `req/s|P99|P50|\d+(\.\d+)? ?ms` in README.md and docs/ outside docs/plans requires a link into `benchmarks/results/` or `benchmarks/protocol/` on the same line or paragraph).
- AC/target: unsupported performance claims = 0.

#### WP-SIMPL-04 Docs describing non-existent components (#512)
- Scope: delete `docs/features/f-sec-04-rebac-openfga.md` and its index line (ReBAC is removed by WP-SIMPL-46, OD-30); delete f-ai-09, f-ai-10, f-gov-06, f-perf-10 and their index lines in `docs/features/README.md`; remove all "Flight SQL", "JDBC", "ODBC" claims (the endpoints are deleted in WP-SIMPL-16; until then docs say "HTTP endpoints returning Arrow IPC").
- Deps: none. Effort: S, 1.5 pd.
- Tests: covered by WP-SIMPL-05 `Docs_reference_existing_source_paths` and a protocol-claim check `DocsClaimsArchitectureTests.Protocol_claims_require_interop_test` (JDBC/ODBC/gRPC/Flight words outside docs/plans fail unless allow-listed with a test name).
- AC/target: docs referencing non-existent source files = 0.

#### WP-SIMPL-05 Docs hygiene and docs checks (#513)
- Scope: script-rewrite all `file:///root/autheris/` links to relative links (all tracked `*.md`, including CLAUDE.md, AGENTS.md and docs/plans); fix 23 broken relative links; remove `Status:` tags and "100% GA / Production-Ready" wording outside docs/plans; condense `docs/architecture/konzept-data-governance-und-mcp-documentation.md` into the English architecture doc and delete it. Checks run as architecture tests in the existing CI job, so no workflow edit is needed.
- Deps: WP-SIMPL-04 (merge after it to avoid double edits). Effort: M, 2.5 pd.
- Tests: `DocsIntegrityTests.No_absolute_file_links`, `.Relative_links_resolve`, `.Backticked_source_paths_exist`, `.No_status_tags_outside_plans`, `.Docs_are_english` (heuristic: German stop-word density). Gated scope: README.md, root `*.md`, `docs/` outside `docs/plans`, module READMEs (docs/plans is rewritten but not gated, Q15).
- AC/target: broken links 0, `file:///` 0, status tags 0.

#### WP-SIMPL-06 Delete unconsumed options; generated configuration reference (#514)
- Scope: delete the options of `Baselines/unconsumed-options.txt` that are verified unconsumed (WebhooksOptions delivery block, QueryCostOptions BaseQueryCost/CostPerRowReturned/NestedDepthFactor, RebacOptions OpenFgaApiUrl/OpenFgaStoreId, ...) and then the baseline file; the 14 entries that are read in their declaring file (Webhooks Effective*, AuditGroupCommit MaxBatchDelayMs, TenantRegistryGate CacheTtl, IdentityRealms DefaultRealm, GatewayOptions.OpenSchema, WorkflowOptions.ActiveProfile, ...) are false positives: they are removed from the baseline, not deleted, and the consumption test is fixed to count reads in the declaring file. The `GatewayOptionsConsumptionRatchetTests` keeps running with no baseline escape. ARCH-MNT 2.4 flags (verified): `CasbinOptions.EnforceInQueryPipeline` and `McpOptions.RequirePiiMasking` are already removed; `ScimOptions.AllowedIpRanges` is enforced and stays (SCIM is core, OD-21).
- Reference generator: `tests/Autheris.Tests.Architecture/ConfigurationReferenceTests` reflects over all `*Options` types bound under `GatewayConfigurationSchema`, reads XML doc summaries (`GenerateDocumentationFile` on options-bearing projects with `CS1591` suppressed), and compares with the checked-in `docs/reference/configuration.md` (columns: key, type, default, description, source file). `AUTHERIS_UPDATE_CONFIG_REFERENCE=1` regenerates. Publishing it as a CI artifact needs one upload step in ci.yml `[WF]` (optional; the checked-in file is the source of truth).
- Deps: none (WP-SIMPL-14 and modules later delete or move more options). LOC: -400 to -600. Effort: M, 3 pd. Risk: options read indirectly (`options-consumed-indirectly.txt`) misclassified; verify each with a grep before deletion.
- Tests: `ConfigurationReferenceTests.Reference_is_current`, `.Every_option_has_a_description`, `GatewayOptionsConsumptionRatchetTests` (no baseline), `UnknownGatewayKeyGuardTests.Removed_key_fails_startup` (one sample key per removed option class).
- AC/target: unconsumed baseline 0 and file deleted; undocumented option names 0.

#### WP-SIMPL-07 Freeze mechanics and program ratchets
- Scope: architecture ratchets that make the freeze mechanical: `ProgramRatchetTests.Endpoint_count_does_not_grow` (Map* count in src, baseline file `endpoint-count.txt`), `.Options_class_count_does_not_grow`, `.Enabled_flag_count_does_not_grow`, `.Application_top_level_folders_are_allow_listed`, `.Plan_id_comments_do_not_grow` (regex for plan ids such as `REV-\d+`, `SEC-\w+`, `AST-X\d`, `ARCH-MNT-\d+`, `PLAN-[A-Z]+-\d+`, baseline per project). A ratchet may grow only when the same PR updates the committed baseline and carries the label `freeze-exception`, which applies to security PRs and to the owner-mandated WP-SIMPL-38..48 (listed in `simplification/README.md`); the CI check reads the label (PR event), the architecture test reads the committed baseline. PR template gets a freeze checklist and the `freeze-exception` label rule. PR net-LOC check (REQ-SIMPL-02c): a ci.yml step computing `git diff --numstat origin/main...HEAD -- src` minus generated files and migrations, failing above +400 unless the `size-exception` label is present `[WF]`. Tag `pre-simplification` on the base commit (owner action).
- Deps: none. LOC: +300 (tests). Effort: M, 2 pd.
- AC: ratchet files committed with current counts; label rules documented in the PR template.

#### WP-SIMPL-08 CODEOWNERS subsystem map and identity consolidation
- Scope: rewrite `.github/CODEOWNERS` to the 14 subsystems of PRD 5.2 plus one line per module; each path has a primary owner and a backup comment (`# backup: vacant` allowed, never omitted); `.mailmap` consolidates the four identities. Branch protection "require CODEOWNERS review" is an owner setting.
- Deps: owner names (D6). Effort: S, 0.5 pd.
- Tests: `CodeOwnersArchitectureTests.Every_src_and_module_folder_has_an_owner`, `.Every_entry_declares_a_backup`.
- AC/target: uncovered src top-level folders = 0.

#### WP-SIMPL-09 Agent and skill definitions cleanup
- Scope: delete `.agents/agents/{conversion-copywriter,qa-cro-reviewer,frontend-developer,ui-ux-architect}.md` (readme-copywriter goes with WP-SIMPL-03); fix references in AGENTS.md. No `.claude/agents` mirror exists in the repo (verified); a local mirror outside the repo is the owner's concern.
- Deps: WP-SIMPL-03. Effort: S, 0.25 pd. AC: no reference to deleted agents in tracked files.

#### WP-SIMPL-10 Measurement protocol and Stage 0 baselines
- Scope: `benchmarks/protocol/README.md` (hardware, OS, container runtime, .NET SDK, commands, repetitions, percentile method: nearest-rank) and scripts:
  - `benchmarks/scripts/footprint.sh`: builds the image exactly as docker-publish.yml does (same Dockerfile and args); size = `podman image inspect --format '{{.Size}}'` (uncompressed) plus compressed size from `podman save | gzip -c | wc -c`; cold start = wall time from `podman run` to first HTTP 200 on `/health/ready`, 20 runs with fresh containers, P50/P95; warm latency = 2,000 requests after 200 warm-up on a fixed read query against a PostgreSQL container (reuse the r2r spike method), P50/P99.
  - `benchmarks/scripts/ci-duration.sh`: median wall time of the last 10 successful ci.yml runs on main via `gh run list/view --json`, per job.
  - `benchmarks/scripts/repo-metrics.sh`: src/modules/test LOC, Map* count, Application folders, Options classes, `*Enabled`, DI registrations, hosted services, Substitute.For, Received, sleeps, container-backed test share.
  - Results as `benchmarks/results/<metric>-<yyyy-mm-dd>-<sha7>.json`.
- Deps: WP-SIMPL-01 for the post-R2R measurement (run once before and once after). Effort: M, 2 pd.
- AC: Stage 0 baseline JSONs committed; every later stage exit cites a results file.

### Stage 1 - Delete (issue #515 stage 1)

#### WP-SIMPL-11 Drop SQLite as governance store (OD-1, REQ-SIMPL-12) - persistence lane, serial
- Delete (Infrastructure/Persistence): `SqliteGovernanceRepository*.cs` (26), `Migrations/SqliteBaselineSchema.cs`, `Migrations/SqliteGovernanceMigrationRunner.cs`, `Migrations/Steps/sqlite/` (9), `Migrations/Shape/SqliteSchemaCatalogReader.cs` (only used by the SQLite runner, triage result), `SqliteGovernanceConnectionProvider.cs`, `SqliteGovernanceLintSource.cs`, `SqliteExecutionGuard.cs`, `SqliteProviderExecutionGuard.cs`, the SQLite audit repository/anchor store branch in `AuditChainAnchorStores.cs`, the SQLite `_lock` audit write serialization, SQLite branches in `StorageStartupChecks.cs:47-62,118,127`, `GatewayGovernanceServiceExtensions.cs:100`, health checks and `ConnectionTlsPolicy`.
- Keep (data source, OD-2 step 3): `Persistence/Catalog/SqliteColumnMetadataReader.cs`, `Persistence/Catalog/SqliteTypeAffinity.cs`, `SqliteDialectGenerator`, `Ast/Security/SqliteTypeAffinity`, `SqliteZoneOffsetTable`, `Microsoft.Data.Sqlite` package.
- Fail closed: new `GovernanceProviderGuard` (pattern of `LegacySwitchGuard`) throws at startup when the governance `Provider` is `Sqlite` or the connection string parses as SQLite (`Data Source=*.db`, `Mode=Memory`, `Filename=`), message names OD-1 and `docs/operations/migration-simplification.md`. Unknown provider values fail as before.
- Defaults to PostgreSQL in: `appsettings.json:39`, `appsettings.Development.json:42`, `deploy/helm/autheris/values.yaml` + `templates/governance-migration-job.yaml` (PostgreSQL connection secret required; chart fails `helm template` when missing via `required`), `deploy/podman-compose.yaml` (adds a pinned `postgres` service with healthcheck; gateway `depends_on: condition: service_healthy`), `deploy/README.md`, `deploy/containers/gqlgateway-api/{Containerfile,appsettings.Benchmark.json}`, `deploy/containers/governance-seed/{Containerfile,seed_governance.py}` (psycopg), `deploy/containers/load-generator/k6-benchmark.js`, `deploy/run-benchmark.{sh,ps1}`, `deploy/scripts/generate_report.py`, README quickstart, and the 43 docs mentioning SQLite as governance store.
- Tests: governance-store tests that use SQLite (heuristic count: Unit 24, Integration 10, Sim.ApiHost 1, Architecture 1, Parity 1 files; the developer confirms each) move to `Autheris.Tests.Integration/Governance/` on a shared `GovernancePostgresFixture` (one container per assembly via `ICollectionFixture`, per-test database cloned from a migrated template database; existing podman host override applies). Contract tests for SQL Server come in WP-SIMPL-19. New: `GovernanceProviderGuardTests.Sqlite_provider_fails_startup`, `.Sqlite_connection_string_fails_startup`, `.PostgreSql_and_SqlServer_pass`; `NoSqliteGovernanceArchitectureTests.No_Sqlite_governance_types`, `.No_sqlite_step_folder`; `ShippedConfigTests.Default_governance_provider_is_PostgreSql` (appsettings*, values.yaml, compose).
- Deps: WP-SIMPL-10 baseline. LOC: about -6,900 src, about -3,000 tests. Effort: L, 6 pd. Risks: hidden SQLite fallbacks in DI (mitigate: architecture test on type names); compose quickstart regression (smoke test `deploy/scripts/smoke-compose.sh` run by the developer and in PR evidence); Helm job ordering with an external DB.
- AC/target: governance dialects 2; default provider PostgreSQL in 100% of shipped configs; `podman-compose up` one-command quickstart passes `/health/ready`.

#### WP-SIMPL-12 Delete external catalog connectors, Backstage, ITSM
- Delete: `Extensions/DataCatalog/{Purview*,Collibra*,Alation*,CatalogWebhookHandler (if catalog-vendor only)}`, DataHub client, `DataCatalogClientFactory` vendor branches (OpenMetadata adapter stays for WP-SIMPL-28); `Extensions/Backstage/` (604), `Application/Integrations/Backstage` (23), `Api/Endpoints/BackstageEndpoints.cs`; `Extensions/Itsm/` (1,062), `ItsmOutboxSqlCore.cs` and the `.Outbox.cs` ITSM partials, ITSM options; docs `docs/extensions/itsm-webhook-guide.md`, vendor sections of `data-catalog-guide.md`; tests in Extensions.Tests/Unit.
- Keep until WP-SIMPL-47 replaces them: `ConsentRecertificationHostedService` (generic consent expiry) and the consent status `PENDING_EXTERNAL_APPROVAL`; only the ITSM and break-glass specifics are deleted here.
- DDL: none (orphaned ITSM outbox tables are dropped in WP-SIMPL-20e).
- Deps: WP-SIMPL-11 merged (both touch governance repository partials). LOC: about -4,000 src (-4,000 tests). Effort: M, 3 pd. Risk: `GovernanceSchemaShapeVerifier` may expect ITSM tables; keep the expected shape until WP-SIMPL-20e.
- Tests: `DeletedFeaturesArchitectureTests.No_types_in_deleted_namespaces` (list grows per WP), `UnknownGatewayKeyGuardTests` sample for removed sections.
- AC: no reference to Purview/Collibra/Alation/DataHub/Backstage/Itsm in src, config schema or docs outside docs/plans and the migration guide.

#### WP-SIMPL-13 Connection-secret policy and data-source dialect admission (ADR-019, OD-12, OD-14)
- OD-12 (binding, 2026-11-05): Oracle and Databricks stay as governed core data sources. Kept: `OracleDialectGenerator`, `DatabricksDialectGenerator`, Oracle connectivity (`OracleConnectionSupport`, `BindByNameOracleConnection`, `OracleColumnMetadataReader`, `OracleBindByName`), `Infrastructure/Databricks/*` (incl. `DatabricksOboTokenProvider`, `DatabricksStatementExecutionClient`, `DatabricksDbCommand`), `DatabricksColumnMetadataReader`, package `Oracle.ManagedDataAccess.Core`, Testcontainers.Oracle, `OracleDriverArchitectureTests`, `OracleIntegrationTestArchitectureTests`. About 3,000 src and 6,500 test lines stay.
- Step 1 (independent of any deletion, separate commit): generalise the Oracle-only plaintext-password check `OracleConnectionSupport.HasPlaintextPassword` into a provider-neutral `ConnectionSecretPolicy.HasPlaintextPassword(provider, connectionString)` covering PostgreSQL (`NpgsqlConnectionStringBuilder.Password`) and SQL Server (`SqlConnectionStringBuilder.Password`), wired into the same startup/connection checks that call the Oracle variant today, with the same fail-closed outcome. Tests: `ConnectionSecretPolicyTests.PostgreSql_plaintext_password_is_rejected`, `.SqlServer_plaintext_password_is_rejected`, `.Secret_reference_is_accepted`. Phase 3 confirms the policy outcome (reject vs warn) matches today's Oracle behaviour.
- OD-14 (binding, revised 2026-11-05): `AnsiDialectGenerator` stays generator-only (not admitted as a data source, per `SqlDialectMapper`/`DialectAdmissionTests`); the DuckDb generator stays core as federated staging target (OD-15).
- Step 1b (unconditional): delete `SnowflakeDialectGenerator` (about 95 lines in `AnalyticalDialectGenerators.cs`), the `TargetSqlDialect.Snowflake` value and its capability entries. Snowflake has no consumer outside generator tests (`DatabricksDialectGenerator` derives from `SqlDialectGeneratorBase`): Snowflake cases in `AnalyticalDialectTests`, `DateFunctionTranslationTests`, `CastTypeNameTests`, `RowFilterKeySetStageTests` are deleted; the non-admitted examples in `DialectAdmissionTests`, `SqlDialectRegistryTests` and `GovernedSqlExecutorX1Tests` `InlineData` switch to `Ansi`.
- Step 2: startup validation admits exactly PostgreSql, SqlServer, Oracle, Databricks (+ Sqlite until WP-SIMPL-31) as data-source dialects; Ansi, DuckDb, the removed Snowflake and unknown names fail closed with a typed configuration error (ADR-019 item 4); compile-time admission stays as second line.
- CI `[WF]`: the Spark integration job stays (it serves Databricks, Q18); it moves to nightly under WP-SIMPL-21.
- Deps: none (engine lane). LOC: about +100 src net (policy + validation +200, Snowflake -95); tests about -150. Effort: M, 2 pd.
- Tests: `ConnectionSecretPolicyTests.*` (Step 1), `TargetSqlDialectTests.No_snowflake_value`, `DialectConfigurationTests.Unsupported_dialect_fails_validation` (Ansi, Snowflake, DuckDb, unknown), `DialectConfigurationTests.Oracle_and_Databricks_are_accepted`, `DialectAdmissionTests` with `Ansi` as non-admitted example.
- AC/target: data-source dialects PG, SS, Oracle, Databricks (+ SQLite until WP-SIMPL-31); generator-only Ansi and DuckDb (federation staging, OD-15); Snowflake absent; `Oracle.ManagedDataAccess.Core` present in Infrastructure only.

#### WP-SIMPL-14 Delete Casbin
- Delete: Casbin engine, adapters and options (8 files, 2,667 lines, 58 mention files), `IPolicyEnforcementService`, `CasbinDecisionBenchmark.cs`, package `Casbin.NET`. No native policy engine, no ReBAC gate and no role-based (`DmlWriterRoles`) write check replaces it (OD-30, ADR-020 decision 7); Casbin policies have no data-path replacement, operators re-express them as grants or deny consents (migration guide row).
- Deps: WP-SIMPL-06 (option already gone); WP-SIMPL-46 PR 1-5 (every `IPolicyEnforcementService` consumer incl. Envoy ext_authz calls `IAccessDecisionPoint`, policy simulation replaced by explain, ReBAC removed), WP-SIMPL-43 PR 4 (Envoy) and WP-SIMPL-45 PR 1-3 merged. LOC: about +80 / -3,300 src. Effort: S, 1.5 pd. Risk: a remaining consumer of `IPolicyEnforcementService`; mitigated by the WP-46 architecture tests (single decision point).
- Tests: `DeletedFeaturesArchitectureTests` entry; `PackageBoundaryArchitectureTests` forbids `Casbin.*`.

#### WP-SIMPL-15 Delete embedded Garnet server and traffic shadowing (OD-17)
- Garnet: deleted (OD-17). External Redis/Garnet stays supported through `StackExchange.Redis`; without Redis the per-node in-memory cache applies. The embedded server ran with password auth (SEC H-01); the reason for deletion is footprint and ownership. Traffic shadowing is deleted as well.
- Delete: `Infrastructure/Garnet/GarnetServerManager.cs` (260), `Program.cs:106-111`, `Caching:Garnet:EnableEmbeddedServer`, package `Microsoft.Garnet`; traffic shadowing (3 files, 376 lines) with its options and middleware registration. `StackExchange.Redis` stays (external Redis/Garnet optional).
- Deps: none. LOC: about -900. Effort: M, 1.5 pd. Risk: tests starting an embedded server for distributed-state tests move to a Redis Testcontainer or in-memory state.
- Tests: `ProgramStartupTests.No_embedded_cache_server_started`; `PackageBoundaryArchitectureTests` forbids `Microsoft.Garnet`.

#### WP-SIMPL-16 Delete Arrow Flight SQL over HTTP and the dev portal
- Changed by OD-20, OD-21 and OD-37: FinOps is removed by WP-SIMPL-42 PR-5 together with its replacement; `Application/Mesh` and `EnvoyExtAuthzEndpoints` stay core; the dev portal is deleted.
- Delete: `Api/Endpoints/ArrowFlightSqlEndpoints.cs`, `ArrowFlightSqlServer` (4 files, 505); keep `ArrowExportEndpoints.cs` (Arrow IPC as a REST format). Dev portal deleted (OD-37): `DevPortalEndpoints.cs`, `DevPortalAssets.cs` and the dev portal static assets. `DevEndpoints.cs` is mapped only when `IHostEnvironment.IsDevelopment()`.
- Deps: WP-SIMPL-04. LOC: about +50 / -1,250. Effort: M, 1 pd.
- Tests: `ReleaseSurfaceTests.Dev_endpoints_absent_outside_development`, `DeletedFeaturesArchitectureTests` entries.

#### WP-SIMPL-17 Merge duplicate folders
- Scope: `Application/Auditing` -> `Application/Audit`; `Application/DataCatalog` -> `Application/Catalog`; `Application/Dbt` (2,342) and `Application/OpenMetadata` (328) merged into `Extensions/Dbt` and `Extensions/OpenMetadata` (the module source of WP-SIMPL-28), removing duplicated code; namespaces follow folders (`namespace-assembly-violations.txt` must not grow).
- Deps: WP-SIMPL-12 (DataCatalog vendor deletions first). LOC: -500 to -1,000 (duplicates). Effort: M, 2.5 pd. Risk: wide namespace churn; one PR per pair.
- Tests: `ProgramRatchetTests.Application_top_level_folders_are_allow_listed` (allow-list shrinks), `NamespaceAssemblyConsistencyTests`.

#### WP-SIMPL-18 Borderline triage execution (D2)
- Scope (after OD-4, OD-11 and OD-18): delete differential-privacy noise (PR A), CDN cache purge (PR B) and SchemaRegistry (887 + 5 endpoints, PR E). Governed aggregation stays core (OD-11); the Trino client protocol route (WP-SIMPL-38), Procedures (WP-SIMPL-39), Docs ingestion (WP-SIMPL-40) and catalog search including embeddings (OD-4) stay core.
- Additional borderline rows (recommended defaults; owner may move an item before Stage 1):


| Item | Default | Reason | Issues |
|---|---|---|---|
| Analytics (governed aggregation) + differential privacy | **Decided (OD-11):** aggregation with small-group suppression and time buckets KEEP (core); differential-privacy noise DELETE; differencing across overlapping filters is accepted residual risk, mitigated by suppression, `ExposeSuppressedGroupsCount=false` by default and filter-shape audit (WP-SIMPL-50 B16, D-50d) | owner decision 2026-11-05 | #467, #468 obsolete by removal |
| CDN cache purge | DELETE | C1 false; outbound HTTP (C2) | #474 closed by this WP (R1) |
| Table cache (DuckDB-backed) | CORE (revised for OD-15): it serves federated staging and DuckDB stays core, so moving it gains no footprint | DuckDB is core | #475 fixed in core (security lane), with an integration test on the real DuckDB engine |
| Trino client protocol route `/v1/statement` | **Decided (OD-18):** KEEP core, clean redesign in WP-SIMPL-38 | only standard SQL client protocol | #470, #484 fixed in WP-SIMPL-38 |
| `FederatedDuckDbExecutionService` (744) + `CrossSourcePlanner` (853) in Application/Sql | CORE (binding OD-15: cross-source joins, e.g. Databricks x SQL Server x declarative HTTP source, are core); subtree compiler/partitioner and federated staging stay core; no module seam | RLS/masks must hold per subtree and in staging | Core parity: `FederationParityTests` (PG x SS, Databricks-contract x SS, HTTP x PG) in WP-SIMPL-21 |
| Catalog semantic search/embeddings (`Application/Catalog/Search`, `LocalOnnxEmbeddingGenerator`) | **Decided (OD-4):** KEEP core | catalog search is core; `LocalOnnxEmbeddingGenerator` has no ONNX package | #501, #488 embeddings part fixed in core (security lane) |

- Deps: none (owner decisions taken). LOC: about -2,350 src. Effort: S, 1.5 pd.
- AC: every Application/Extensions folder appears in the section 5 table as KEEP, MODULE (with owner) or deleted.

### Stage 2 - Structure

#### WP-SIMPL-19 Governance repository contract suite (persistence lane, first)
- Scope: one abstract `GovernanceRepositoryContract` test class per aggregate (Consent, ConsentGrants, ConsentPage, Catalog, Schema, Audit, CdcStreams, WebhookOutbox, CloudEventSubscriptions, DataOwnerAssignment, AccessProfiles, CallerAttributes, ...) in `Autheris.Tests.Integration/Governance/Contract/`, executed by two subclasses on `GovernancePostgresFixture` and `GovernanceSqlServerFixture` (Testcontainers MsSql). Pins tenant predicates, DEF-02 fail-closed tenant parse, audit chain ordering, paging, upsert idempotency, unique-violation mapping. Replaces per-engine copies.
- Deps: WP-SIMPL-11, WP-SIMPL-12. LOC: tests +3,000 / -4,000. Effort: M, 4 pd.
- AC: suite green on both engines against the current two families before any merge in WP-SIMPL-20.

#### WP-SIMPL-20 Unified governance repository (AD-1) - persistence lane, serial steps
- Steps (one PR each, contract suite green before and after): 20a `IGovernanceSqlDialect` + two implementations + Consent/ConsentGrants/ConsentPage; 20b Catalog + Schema; 20c Audit (+ anchor stores); 20d remaining partials, delete `PostgreSqlGovernanceRepository*`/`SqlServerGovernanceRepository*`; 20e one forward-only step per engine dropping orphaned tables of deleted features (ITSM outbox, Casbin, FinOps if persisted), with `GovernanceSchemaShapeVerifier` updated - the only DDL change of the program besides WP-SIMPL-11's tree removal.
- Files: `Infrastructure/Persistence/GovernanceRepository.*.cs`, `Persistence/Dialects/*GovernanceDialect.cs`, DI in `GatewayGovernanceServiceExtensions.cs`.
- Deps: WP-SIMPL-19. LOC: about -7,500. Effort: L, 11 pd (20a 3, 20b 3, 20c 3, 20d 1.5, 20e 0.5).
- Risks: SQL regressions (contract suite); SQL Server `QUOTED_IDENTIFIER` assumptions (assert in `SqlServerGovernanceDialect.CreateConnection`); MERGE concurrency on SQL Server (`HOLDLOCK`).
- Tests: `NoPerDialectGovernanceRepositoryArchitectureTests.No_dialect_prefixed_governance_repository_types`, `GovernanceMigrationStepParityTests.Every_step_exists_for_both_engines`, `GovernanceRepositoryDuplicationTests` (ratio < 15%).
- AC/target: governance repository LOC <= 7,000 (AD-1 metric), exit <= 6,000 after WP-SIMPL-33 cleanups.

#### WP-SIMPL-21 Parity on real engines and dialect gate (ADR-019; OD-2 step 2a) `[WF]`
- Scope: parity reference engine becomes PostgreSQL (`ParityPostgresHostFixture`); SQL Server cells switched from Pending to executed (`ParitySqlServerHostFixture`); SQLite cells kept until both are green; DuckDB federation staging stays core (OD-15) and gets core coverage on PR: `FederationParityTests` (cross-source joins PG x SS, Databricks-contract x SS, declarative HTTP x PG; RLS/mask/consent per subtree and in staging) on Testcontainers plus in-process DuckDB in `Autheris.Tests.Integration`. Oracle and Databricks (OD-12) get full parity matrices too:
  - PR (required checks): PostgreSQL and SQL Server full parity on Testcontainers; Oracle security-tagged smoke subset (`Category=Parity&Dialect=Oracle&Trait=Security`: RLS, masking, consent, injection-rejection and bind-by-name cells) on `Testcontainers.Oracle` with the image pre-pulled (#521); Databricks contract tests: generated-SQL snapshot per parity case (`DatabricksParitySnapshotTests`) plus executor contract against a fake statement-execution API (`DatabricksStatementExecutionContractTests`: OBO token use, parameter binding, paging, error mapping, fail-closed on 401/403/timeout).
  - Nightly (`parity-nightly.yml`, #522): full Oracle suite; full Databricks suite against a real workspace when `DATABRICKS_HOST`/credentials secrets exist, otherwise the Spark container suite (`Category=Spark`, moved from ci.yml); SQL Server job added; `parity-sqlite` removed in WP-SIMPL-31.
  - Gate: `DialectParityGateTests.Every_generator_has_a_parity_baseline_file`, `DialectParityGateTests.Every_core_dialect_has_a_pr_parity_lane` (PG/SS full, Oracle smoke, Databricks contract). Stryker configs merged to 2 (`stryker-config.security-guards.json` folded into `stryker-config.json`, Q13).
- Deps: WP-SIMPL-13 Step 2. LOC: tests +2,300 / -2,000. Effort: L, 7 pd. Risk: SQL Server or Oracle cells reveal generator bugs (fix in the same lane; never leave Pending); Databricks nightly without credentials covers Spark semantics only (recorded in the results file as `engine=spark`).
- AC/target: PG + SS + Oracle 100% parity on real engines (Oracle full nightly, smoke on PR), Databricks 100% contract cells on PR and 100% nightly cells (real or Spark), none Pending; open master-plan baselines closed (DuckDB covered by core federation parity).

#### WP-SIMPL-22 Real-database tests out of Unit (OD-2 step 2b; REQ-SIMPL-08.2)
- Scope: 83 Unit files using real SQLite and the DuckDB files: (i) behaviour that does not depend on DB semantics -> in-memory fakes/stores; (ii) DB semantics -> `Autheris.Tests.Integration` on the shared PostgreSQL fixture; (iii) Olap/DuckDB -> core DuckDB integration tests (OD-15). 11 TestServer/WebApplication.Create files -> Integration. Same for SqlCompilerGate (1), Simulation (4), Sim.ApiHost (2), TrinoSqlEngine.Tests (16, engine semantics via PostgreSQL fixture or in-memory row source).
- Deps: WP-SIMPL-11, WP-SIMPL-21. LOC: tests about -4,000 net. Effort: L, 7 pd (splittable per domain folder).
- Tests: `TestLayeringArchitectureTests.Unit_tests_open_no_database_or_host` (forbids `SqliteConnection`, `DuckDBConnection`, `NpgsqlConnection`, `SqlConnection`, `TestServer`, `WebApplication.Create` in Autheris.Tests.Unit).
- Coordination: fixture reuse and CI sharding with #516-#522; this WP only requires one container per test assembly and the CI budget not exceeded.

#### WP-SIMPL-23 Test-suite restructuring and ratchets (REQ-SIMPL-08)
- Scope: 155 loose root files of Autheris.Tests.Unit moved into domain folders mirroring the consolidated Application folders; review/finding-named files (e.g. `SecurityFindingsRemediationTests.cs` 1,378 lines, `BasicAuthDxTests`, `*Ext15Tests`, `SecurityReviewG5Tests`) folded into domain test classes named after behaviour; files > 800 lines split. New ratchets: `TestSuiteRatchetTests.No_test_files_in_unit_root`, `.Test_file_line_ceiling` (800), `.Substitute_for_count_does_not_grow` / `.Received_count_does_not_grow` (baseline `mock-counts.txt` per project), existing `test-sleep-counts.txt` lowered as sleeps are replaced by `FakeTimeProvider` or awaitable signals; `.Test_folders_follow_domains` (folder allow-list).
- Deps: Stage 1 deletions merged (do not move tests that will be deleted); runs per domain to limit conflicts. LOC: tests -10,000 to -20,000 (merging duplicates). Effort: L, 6 pd.
- AC/target: Stage 2 column of section 3; root files 0.

#### WP-SIMPL-24 Application folder consolidation (<= 32, ratchet)
- Scope mapping: Observability + Telemetry + Diagnostics + Performance -> `Observability`; Common + Serialization + Interfaces (distributed to owners where possible) -> `Common`; DataSources + Connectors + Data -> `DataSources`; Hosting + Jobs + ResourceGroups -> `Runtime`; SqlEndpoints -> `Sql`; Caching + State -> `State`; Workflows -> `Governance`; Events stays until the events module (WP-SIMPL-29). Allow-list file updated in the same PR.
- Deps: WP-SIMPL-17, WP-SIMPL-18. Effort: M, 3 pd. Risk: namespace churn; one PR per merge group, no logic change.
- AC/target: <= 32 folders at Stage 2 exit; <= 25 after module extraction.

### Stage 3 - Modules and polish

#### WP-SIMPL-25 Module host (AD-3) - Phase 3 review mandatory before merge `[WF]`
- Scope: `src/Autheris.Modules.Abstractions`, `Autheris.Api/Hosting/ModuleLoader.cs`, `ModuleAssemblyLoadContext.cs`, `Infrastructure/Modules/Trust/*` (extracted from `PluginManager`), embedded release public key, manifest signing tool `tools/Autheris.ModuleSigner` used by release.yml (private key from a GitHub secret), `Dockerfile*` `FLAVOR` argument, docker-publish.yml publishes `autheris` and `autheris-full`. The `Autheris.Api.csproj:8` Extensions reference is removed later by WP-SIMPL-29 (after WP-SIMPL-26 moved OData into core); `AddGatewayExtensions` callers become module registrations as each module WP moves its code.
- Deps: Stage 2 exit (stable abstractions); WP-SIMPL-46 PR 5 merged (no `RebacEndpointFilter` left for the module route group). LOC: +900 src. Effort: L, 7 pd.
- Tests: `ModuleLoaderTests.Unsigned_manifest_fails_startup`, `.Hash_mismatch_fails_startup`, `.Unlisted_file_fails_startup`, `.Version_mismatch_fails_startup`, `.Enabled_but_missing_fails_startup`, `.Host_assembly_collision_fails_startup`, `.Not_enabled_module_is_not_loaded`, `.Unsigned_allowed_only_in_development_with_flag`; `ModuleEndpointTests.Module_routes_require_authorization`; `ModuleConfigurationTests.Keys_of_missing_module_fail_fast`; architecture: `ModuleBoundaryArchitectureTests.Modules_reference_only_abstractions_domain_and_application`, `.Modules_open_no_data_source_connections` (Cecil scan of module assemblies), `PackageBoundaryArchitectureTests.Api_does_not_reference_Extensions`.
- AC: core image starts without any module; full image loads all first-party modules; fallback switch documented.

#### WP-SIMPL-26 OData core hardening (OD-24)
- Rewritten by OD-24 as **OData core hardening** (no module): move OData from `src/Autheris.Extensions/OData` (1,927) into a core project next to `Application/OData` (768) and `ODataEndpoints` (21 Map*, unchanged paths); Power BI and Excel OData client interoperability tests; the OD-22 access-model controls (row-level security, masking, column-level security, hidden objects) enforced on OData through the access decision point, with OData rows in the WP-SIMPL-46 parity matrix.
- The runtime-loading-versus-compile-time decision moves to WP-SIMPL-27 (first real module, OD-37). Removing the `Autheris.Api -> Autheris.Extensions` reference is owned by WP-SIMPL-29.
- Deps: WP-SIMPL-46 merged (OData decides through `IAccessDecisionPoint`); must merge before WP-SIMPL-29. LOC: about +200 / -50 src plus the move of 1,927 lines (net 0). Effort: M, 3 pd. Endpoints: unchanged.

#### WP-SIMPL-27 Module wave A: Lakehouse
- Lakehouse is the module pilot (OD-24): `Extensions/Lakehouse` (3,680) + `IcebergRestCatalogEndpoints` (4), package `Parquet.Net` leaves core (first module with a private dependency; verifies the module ALC loading of its packages). Exit decision: runtime loading confirmed or fallback (AD-3.10) taken for all modules (OD-37).
- Deps: WP-SIMPL-25. LOC: -4,600 src. Effort: M, 2 pd. Endpoints: -4.

#### WP-SIMPL-28 Module wave B: catalog sync (dbt, OpenMetadata)
- `modules/Autheris.Modules.Dbt` (Extensions/Dbt 5,692 incl. merged Application copy, `DbtEndpoints` 13; `YamlDotNet` moves if only dbt uses it - verify), `modules/Autheris.Modules.OpenMetadata` (1,838 + adapter). Catalog import API in core stays the integration point. The semantic metric model (`SemanticMetricCatalogProjection`, today in `Application/Dbt`) moves to core Catalog/Analytics before extraction, so the dbt module only imports manifests into it and a later central views/measures layer on DuckDB (PRD forward-looking note, OD-15) does not depend on the dbt module.
- Deps: WP-SIMPL-27 (pilot), WP-SIMPL-17, WP-SIMPL-44 (source-metadata reads removed first). LOC: -8,000 src. Effort: M, 4 pd. Endpoints: -13.

#### WP-SIMPL-29 Module wave C: events delivery (CDC/Streaming + Webhooks) and Lineage
- `modules/Autheris.Modules.Events`: Application/Streaming (4,440), Extensions/Cdc (2,296), `CdcStreamSqlCore` + CDC partials (stays in governance repository if the tables are core-owned; default: module owns its store via `IModuleHostContext` governance connection factory, Q5), Webhooks event/CDC/lineage delivery (1,157), `WebhookEndpoints`, `StreamingCdcEndpoints`, `CdcStreamAdminEndpoints`, `TableStreamEndpoints`. `modules/Autheris.Modules.Lineage`: Application/Lineage + Extensions/Lineage.
- Stays core: the webhook core classes `WebhookSignature`, `WebhookDeliveryClient` (egress/SSRF guard), `IWebhookSecretProtector` and `WebhookRetryPolicy` (used by the consent workflow webhook, OD-29, WP-SIMPL-47) and the GDPR disclosure report (`GetGdprDataDisclosureReportAsync`, `GdprSubjectScopeUnresolvedException`, OD-10).
- Last module WP: removes the `Autheris.Api -> Autheris.Extensions` project reference (after WP-SIMPL-26 moved OData into core); `PackageBoundaryArchitectureTests.Api_does_not_reference_Extensions`.
- Deps: WP-SIMPL-27, WP-SIMPL-20, WP-SIMPL-26. LOC: -11,000 src. Effort: L, 6 pd. Endpoints: -16. Risk: SSRF guard for webhooks must stay intact in core (Phase 3 re-check).

#### WP-SIMPL-30 Module wave D: identity and edge modules
- **Cancelled (OD-21).** Authentication and authorization stay core; SCIM, TOTP, Negotiate and Envoy ext_authz are consolidated in WP-SIMPL-43. Procedures stay core (WP-SIMPL-39); governed aggregation stays core (OD-11). The text below is the original scope, kept for reference only.
- SCIM (`Scim*Endpoints` 15 Map*), TOTP (local-auth), Negotiate (package `Microsoft.AspNetCore.Authentication.Negotiate` leaves Api), Envoy ext-authz (`EnvoyExtAuthzEndpoints` 3), plus owned borderline modules from WP-SIMPL-18 (Procedures, governed-aggregates).
- Deps: WP-SIMPL-26. LOC: -5,000 to -15,000 src. Effort: L, 5 pd. Risk: authentication handler registration order (module `ConfigureServices` runs before `AddAuthentication` finalisation; test `Negotiate_module_registers_scheme`).

#### WP-SIMPL-31 Full SQLite removal step 3 (OD-2; owner may stop before)
- Delete `SqliteDialectGenerator.cs` (433), `Ast/Security/SqliteTypeAffinity.cs` (40), `CatalogTypeMap.cs` SQLite branch, `Application/Data/Services/SqliteZoneOffsetTable.cs` (135), `Persistence/Catalog/SqliteColumnMetadataReader.cs` + `SqliteTypeAffinity.cs`, `GatewayStorageServiceExtensions.cs:130`, the 28 `TargetSqlDialect.Sqlite`/provider lines, Emit/Application/Sql/middleware uses, package `Microsoft.Data.Sqlite` from Infrastructure and 4 test projects; SQLite parity cells and `parity-sqlite` job `[WF]`.
- Deps: WP-SIMPL-21 and WP-SIMPL-22 green on PostgreSQL. LOC: about -1,000 src. Effort: M, 3 pd.
- Tests: `PackageBoundaryArchitectureTests.No_Microsoft_Data_Sqlite_reference`, `TargetSqlDialectTests.Only_supported_values_exist` (PostgreSql, SqlServer, Oracle, Databricks admitted; Ansi, DuckDb generator-only; no Sqlite, no Snowflake).
- AC/target: SQLite references in src 0; real-SQLite test files 0.

#### WP-SIMPL-32 Configuration surface reduction (REQ-SIMPL-06.4)
- Scope: after deletions and module moves, merge small option classes per subsystem, remove `*Enabled` flags only of deleted features or of always-on core features (disabling a security control is not configurable), move module options into modules. Opt-in switches of kept features stay (e.g. `SqlEndpoints:Procedures:Enabled`, `WebSql:Enabled`, write access). Uses the WP-SIMPL-06 reference generator; ratchets from WP-SIMPL-07 lowered to targets.
- Deps: WP-SIMPL-27..29. Effort: M, 4 pd. Risk: removing an `Enabled` flag that guarded a fail-closed default (Phase 3 lists flags that must stay).
- AC/target: Options <= 90, props <= 600, `*Enabled` <= 40, undocumented 0.

#### WP-SIMPL-33 Plan-id comment cleanup (REQ-SIMPL-02d)
- Scope: scripted removal per project (`tools/strip-plan-ids.py`, reviewed by diff statistics: comment lines only, no code tokens changed - verified by comparing Roslyn syntax trees without trivia), then `ProgramRatchetTests.Plan_id_comments_do_not_grow` baseline set to 0; residual cleanups in the governance repository to reach <= 6,000.
- Deps: WP-SIMPL-27..32 (do not clean code twice). LOC: about -1,900. Effort: M, 2 pd.

#### WP-SIMPL-34 README rewrite, feature docs, migration guide completion
- Scope: README per REQ-SIMPL-11.5 and OD-29 (positioning, core capabilities, the five core transports REST incl. Web SQL, GraphQL, OData, Trino `/v1/statement` and MCP, modules with install lines, honest status, no numbers without results link); feature docs <= 45 (DELETE items lose docs, MODULE docs move to module READMEs); `docs/operations/migration-simplification.md` completeness check; architecture docs updated to the module model.
- Deps: WP-SIMPL-33. Effort: M, 3 pd. AC: owner accepts README (freeze exit criterion).

#### WP-SIMPL-35 Footprint budget job and published numbers `[WF]`
- Scope: CI job (nightly + on release) running `benchmarks/scripts/footprint.sh` and `ci-duration.sh`, comparing with `benchmarks/budgets.json` (section 3 values: core image 325 MB, cold P50 3.0 s, warm P99 3.47 ms; CI: PR wall-clock median incl. queue < 30 min per OD-6, full/nightly reported only), failing at +10% for footprint and above 30 min for the PR CI median; results uploaded as artifact and committed per release.
- Deps: WP-SIMPL-10, WP-SIMPL-27..29. Effort: S-M, 2 pd.

#### WP-SIMPL-36 Security lane (freeze exception; parallel to Stage 0)
- Scope: issues #505-#508 and the FIX-IN-CORE advisories listed in the "Open issues and advisories assessment" section (PM-owned). One bundled change for the fail-closed audit fixes of #505 and #507: every security-relevant audit write on the affected paths uses `IAuditWriter.RecordRequiredAsync` (fails the request when the audit write fails) instead of best-effort recording, plus an architecture test `RequiredAuditArchitectureTests.Security_decision_paths_use_RecordRequiredAsync` (Cecil scan of the affected endpoint/mutation types; baseline starts at the fixed state). Remaining issues/advisories: one PR each, only for KEEP code.
- Deps: none; runs under the security exception of the freeze (PRD 5.1); first item: merge `fix/security-batch-5`. Effort: L, 8-12 pd (batch 5 review and merge, audit bundle, urgent DML items per OD-3 (the write-grant design is WP-SIMPL-45, OD-26), about 20 further FIX-IN-CORE items). Risk: overlaps with deletion WPs (rule R1 in section 7).
- AC: #505-#508 closed or explicitly re-classified OBSOLETE-BY-REMOVAL with the deleting WP named.

#### WP-SIMPL-37 Databricks authentication modes (OD-16)
- Verified code state (2026-11-05): `DatabricksAuthMode` already has `OAuthM2M` (default), `Pat` and `OAuthObo` (`Domain/Options/Storage/DatabaseOptions.cs:220`); `Pat` is rejected outside Development in two places (`DatabricksOboTokenProvider.cs:75`, `DatabricksStatementExecutionClient.cs:182`); the M2M token endpoint is derived from the host (`https://{Host}/oidc/v1/token`, `DatabricksOboTokenProvider.cs:320`); secrets already resolve through `SecretReferenceResolver` (`PatRef`, `ClientSecretRef`).
- Scope: three supported production modes per Databricks data source: (a) OBO (delegated, per caller), (b) service principal via OAuth M2M client credentials, (c) PAT. Remove the Development-only restriction for `Pat` and replace it with the mode validation below. Optional explicit `TokenEndpoint` for M2M (https only, must match the workspace host or an allowlisted account host; default stays host-derived). Optional per-tenant service principal (`Auth.Tenants[tenantId]` with its own `ClientId`/`ClientSecretRef` or `PatRef`; no fallback from a tenant entry to the global principal unless configured explicitly).
- Secrets: only secret references (no inline token or client secret; an inline value fails startup); rotation without restart (secret provider re-read on expiry or on a 401 with one retry; token cache invalidated per data source and tenant); tokens never in logs, errors, problem details or audit (redaction test).
- Enforcement: with (b) and (c) the workspace sees one shared identity, so the data source runs in gateway-enforced mode: consent, RLS and masking are fully enforced by the gateway on the secured AST; no "source enforces" flag is honoured for these modes. Audit records the real caller (subject, tenant) plus the technical identity (mode, client id or token fingerprint, never the token).
- Startup validation per mode (fail closed): missing or unresolvable secret reference, non-https host or token endpoint, `OAuthObo` without a configured token-exchange audience, any mode/enforcement mismatch (shared identity with source-side enforcement), inline secrets.
- Tests: contract tests per mode against the Databricks fake on PR (token acquisition, rotation, redaction, gateway enforcement, audit attribution); real workspace nightly per Q21 when credentials exist.
- Tier flip (OD-36, X1-DBX): Databricks is enabled on WebSQL, the Trino protocol and MCP, with parity tests per transport.
- Deps: WP-SIMPL-36 item 0 (batch 5) for the shared files; runs in the engine lane, parallel to Stage 1. LOC: about +600 src, +800 tests. Effort: M, 4 pd. Phase 3: SEC-SIMPL-95..98.

#### WP-SIMPL-38..48 Owner-decision work packages (OD-18..OD-43)

Developer-ready specs in `simplification/`; dependencies and order in the README execution table. All run under the `freeze-exception` label (WP-SIMPL-07).
- [WP-SIMPL-38](simplification/WP-SIMPL-38-trino-protocol-clean.md) Trino client protocol `/v1/statement` (OD-18; #470, #484). Metadata listing reports access status through `IAccessDecisionPoint.DecideManyAsync` (no second decision path).
- [WP-SIMPL-39](simplification/WP-SIMPL-39-procedures-clean.md) Governed stored procedures (OD-18).
- [WP-SIMPL-40](simplification/WP-SIMPL-40-docs-ingestion-clean.md) Docs ingestion and MCP doc tools (OD-18; GHSA-p234-g3ph-gqh2); storage move after WP-SIMPL-20.
- [WP-SIMPL-41](simplification/WP-SIMPL-41-authorized-data-discovery.md) Authorized data discovery (OD-19, OD-22): visibility per OD-19 (everything visible to authenticated users unless explicitly hidden; hidden objects only for admins and the data owner), access status from `DecideManyAsync`; consent requests through the WP-SIMPL-47 API.
- [WP-SIMPL-42](simplification/WP-SIMPL-42-usage-accounting-users-groups.md) Usage accounting per user and AD group (OD-20, OD-29), replaces FinOps; usage attributed to every contributing grant id carried by the decision record (WP-SIMPL-46, OD-42), each counted in full, including owner grant records (OD-34).
- [WP-SIMPL-43](simplification/WP-SIMPL-43-authentication-authorization-core.md) Authentication and authorization core (OD-21, OD-27): direct and ForwardAuth modes, SCIM groups; Envoy ext_authz decides through the decision point.
- [WP-SIMPL-44](simplification/WP-SIMPL-44-no-source-metadata-reads.md) No source-metadata reads (OD-25; #459).
- [WP-SIMPL-45](simplification/WP-SIMPL-45-governed-write-access.md) Governed write access (OD-26, OD-33, OD-35): write grant records with validity, independent of read grants; enforcement in the shared pipeline; WebSQL only, other transports reject writes fail-closed.
- [WP-SIMPL-46](simplification/WP-SIMPL-46-unified-authorization-model.md) Unified authorization model ([ADR-020](../adr/ADR-020-unified-authorization-model.md), OD-30, OD-31, OD-32): one grant model and one `IAccessDecisionPoint`; ReBAC removed; access profiles grant only through materialized grant records; plugin hooks narrowing only; the decision record lists every contributing grant (OD-42).
- [WP-SIMPL-47](simplification/WP-SIMPL-47-external-catalog-and-consent-api.md) Catalog and consent API, workflow internal or external, signed time-limited grant records (OD-28, OD-29, OD-32, OD-40, OD-43); owns the consent request REST route; profile items with patterns and the approval-time effect preview.
- [WP-SIMPL-48](simplification/WP-SIMPL-48-row-policies-dynamic-coverage.md) Row policies, dynamic object coverage and per-cell column state (OD-38..OD-43): named row policies with parameters at two levels (grant scope ORed, hard policies ANDed), pattern grants evaluated at decision time, per-cell masking for overlapping grants, effective-access report and approval preview.
- [WP-SIMPL-49](simplification/WP-SIMPL-49-data-products.md) Data products (OD-44): views (two-stage DuckDB execution with load modes schedule, onDemand and live), stored procedures (declared read or write), defined in Autheris with their own product owner; base owners decoupled; new role `DataProductAdmin`; Git sync; built after the feature freeze (not under `freeze-exception`, not in the program total).
- [WP-SIMPL-50](simplification/WP-SIMPL-50-review-remediation-v1-7-1.md) Remediation of the v1.7.1..HEAD code and security reviews: release blockers (unsigned workspace module fallback, missing-ABAC-service and unknown-IP fail-open, CODEOWNERS remap, module audit serialization, HitL broadcast binding), module host hardening (trust, route prefix, service allowlist, no raw governance connection, signed release path), ABAC row-filter propagation, ITSM and break-glass leftovers removed, aggregation residual risk after the DP removal (OD-11), low cleanups; developer-ready, Phase 3 approved with conditions SEC-50-01..16, owner confirmed all defaults 2026-11-07 (D-50i KMS/HSM signing key with environment-secret interim, D-50k Lakehouse route move); not started (about 9.5 pd).
- Follow-up to WP-SIMPL-50 D-50j (not scheduled): enable culture analyzers CA1307, CA1309 and CA1310 as errors in `src` (culture-sensitive identifier comparisons are a bypass class); CA1304, CA1305 and CA1311 later.
- [WP-SIMPL-51](simplification/WP-SIMPL-51-websql-client-contract.md) WebSQL client contract from issues #527, #528, #529, #533: cost estimate and per-caller quota shared with GraphQL, typed columns with mask flags, syntax error positions and trailing semicolon, paging via bound continuation tokens without rewriting the statement (about 8 pd; developer-ready, Phase 3 approved with conditions SEC-51-01..22, owner confirmed all defaults 2026-11-07 incl. D-51m policy-denied statements charged; not started).
- [WP-SIMPL-52](simplification/WP-SIMPL-52-governance-data-model.md) Governance data model consolidation and classification-based masking (OD-45): data classifications per tenant with hierarchy, multiple classifications per column, typed mask functions per classification with per-column override, Clear/Masked/Deny per classification on grants and profile items, per-classification time-limited clear requests; column-level DDL for the WP-45..49 tables, pattern grants in `consents`, access-profile items, canonical grant id `CONSENTS.id`, drop step for superseded columns and tables; PR 4b catalog text of Masked columns per classification (#571, D-52p) (about 14.5 pd; Draft, Phase 3 security review pending).
- [WP-SIMPL-53](simplification/WP-SIMPL-53-mcp-catalog-client-hints.md) MCP row cap and truthful catalog hints from issues #578, #579, #572, #573: `Mcp:MaxResultRows` enforced on every row-reading MCP tool, catalog hints derived from the effective tool and route switches, dataset size class from ingested metadata only, aggregation and WebSQL index-usability pricing and hints (about 7 pd; Draft, Phase 3 pending; PR 3-5 under `freeze-exception`, Q23).
- [WP-SIMPL-54](simplification/WP-SIMPL-54-golden-queries-from-dbt.md) Golden queries maintained by data owners in dbt `meta.golden_queries`, stored in the governance store, served without restart (#580; about 4.5 pd; after the feature freeze, in the dbt module; not in the total below, like WP-SIMPL-49).

Total effort: about 268-275 pd (WP-SIMPL-50 review remediation about 9.5 pd, WP-SIMPL-51 WebSQL client contract about 8 pd, WP-SIMPL-52 governance data model about 14.5 pd and WP-SIMPL-53 MCP row cap and catalog hints about 7 pd included; WP-SIMPL-49 and WP-SIMPL-54 after the freeze, excluded) as the sum of the table above: WP-SIMPL-01..37 about 120-124 pd (security lane WP-SIMPL-36 8-12 and WP-SIMPL-37 4 included; WP-SIMPL-30 cancelled; WP-14, WP-16, WP-18 and WP-21 aligned with their specs and owner decisions) plus the owner-decision work packages WP-SIMPL-38..48 about 109-112 pd (WP-48 12 pd, WP-47 raised to 16 pd by OD-43) (estimates from section 11 of each spec). Contingency is not included; the earlier figure of 135-157 pd covered WP-01..37 only.

## 5. Architecture view: KEEP / MODULE / DELETE

| Area | Class | Projects / folders | Packages | WP |
|---|---|---|---|---|
| Consent, ConsentRequests, FunctionalRights, SubjectLifecycle | KEEP | Application/Governance (Consent*), ConsentRequests | - | - |
| RLS, VirtualFilters, Policy | KEEP | Application/VirtualFilters, Policy | - | - |
| Masking, Federation, subgraph masking | KEEP | Application/Federation, GraphQL/Federation | - | - |
| Audit | KEEP (merged) | Application/Audit (+Auditing), Infrastructure audit stores | - | 17 |
| Authorization: access decision point, grant store, RBAC (control plane only) | KEEP (OD-30, ADR-020) | Application/Governance, Policy, Security | - | 46 |
| Identity, SCIM, TOTP, Negotiate, ForwardAuth, Envoy ext-authz (`Application/Mesh`), Security, TokenRevocation, Tenancy | KEEP (OD-21, OD-27) | Application/Identity, Security, Tenancy, Mesh | JwtBearer, Authentication.Negotiate | 43 |
| ReBAC (evaluator, stores, tuple API, endpoint filter) | DELETE (OD-30) | Application/Security/Rebac, Infrastructure/Rebac, Api ReBAC endpoints and filter | - | 46 |
| SQL compiler, gate | KEEP (scope reduced; dialects PG, SS, Oracle, Databricks) | src/TrinoSqlEngine, Application/Sql (+SqlEndpoints), tests SqlCompilerGate, Fuzz | Antlr4.Runtime | 13, 21, 31 |
| Catalog + classification (internal) | KEEP (merged) | Application/Catalog (+DataCatalog) | - | 17 |
| Workflows, recertification, HITL | KEEP | Application/Governance, Mcp HITL | - | 24 |
| Transports REST (incl. Web SQL), GraphQL, OData, Trino `/v1/statement`, MCP; Arrow IPC export | KEEP (OD-18, OD-24, OD-29) | Api/Endpoints, Autheris.GraphQL, OData core project, Application/Mcp | HotChocolate, ModelContextProtocol, Apache.Arrow | 26, 38 |
| Governance persistence (PG, SS) | KEEP, unified | Infrastructure/Persistence | Npgsql, Microsoft.Data.SqlClient | 11, 19, 20 |
| Observability, health, jobs, state/caching | KEEP (slim) | consolidated folders | OpenTelemetry, prometheus-net, StackExchange.Redis | 15, 24 |
| Plugins/Extensibility | KEEP (module host base) | Application/Plugins, Extensibility, Infrastructure/Plugins | - | 25 |
| Olap engine and endpoints, DuckDB table cache, DuckDB generator | KEEP core (OD-15 extended; strategic base for future central views and measures) | Application/Olap, DuckDbOlapEndpoints | DuckDB.NET.Data.Full (core) | - |
| Cross-source federation (DuckDB staging, CrossSourcePlanner, subtree compiler, table cache) | KEEP (OD-15) | Application/Sql, Caching | DuckDB.NET.Data.Full | 21 |
| Lakehouse (Delta/Iceberg) | MODULE (pilot, OD-24) | modules/Autheris.Modules.Lakehouse | Parquet.Net | 27 |
| dbt | MODULE | modules/Autheris.Modules.Dbt | YamlDotNet (verify) | 28 |
| OpenMetadata | MODULE | modules/Autheris.Modules.OpenMetadata | - | 28 |
| CDC/Streaming + Webhooks event delivery | MODULE (webhook core classes and consent workflow webhook stay core) | modules/Autheris.Modules.Events | - | 29 |
| Lineage | MODULE (GDPR disclosure report stays core, OD-10) | modules/Autheris.Modules.Lineage | - | 29 |
| Procedures, Docs ingestion and MCP doc tools | KEEP (OD-18) | Application/Procedures, Application/Docs | YamlDotNet | 39, 40 |
| Governed aggregation (Analytics) | KEEP (OD-11) | Application/Analytics | - | - |
| Catalog search incl. embeddings, authorized discovery | KEEP (OD-4, OD-19) | Application/Catalog | - | 41 |
| Usage accounting (replaces FinOps) | KEEP (OD-20) | Application/Usage | - | 42 |
| Governed writes, consent API and workflow, signed grant records | KEEP (OD-26, OD-28, OD-29) | Application/Governance, Sql | - | 45, 47 |
| Differential privacy, SchemaRegistry, CDN purge | DELETE (OD-11, D2) | Application/Analytics DP part, SchemaRegistry, Cdn | - | 18 |
| Purview, Collibra, Alation, DataHub | DELETE | Extensions/DataCatalog vendor clients | - | 12 |
| Backstage, ITSM (ServiceNow/Jira) | DELETE | Extensions/Backstage, Itsm; Application/Integrations | - | 12 |
| Oracle and Databricks data sources (OD-12) | KEEP (core, governed data sources; never governance store) | Oracle/Databricks generators, Infrastructure Oracle support, Infrastructure/Databricks (OBO token provider, executor) | Oracle.ManagedDataAccess.Core; tests Testcontainers.Oracle, Spark image | 13 (secret policy), 21 |
| Ansi generator (OD-14) | KEEP (generator-only, not admitted as data source) | `AnalyticalDialectGenerators.cs` Ansi part | - | 13 (admission) |
| Snowflake generator (OD-14) | DELETE | `AnalyticalDialectGenerators.cs` Snowflake part, enum value | - | 13 |
| Casbin | DELETE (no data-path replacement, OD-30) | Casbin engine/options, `IPolicyEnforcementService` | Casbin.NET | 14 (after 46) |
| Embedded Garnet | DELETE (OD-17) | Infrastructure/Garnet | Microsoft.Garnet | 15 |
| Traffic shadowing | DELETE | shadowing middleware | - | 15 |
| Arrow Flight SQL over HTTP, dev portal | DELETE (OD-37 for the dev portal) | Api/Endpoints/ArrowFlightSql*, DevPortal* | - | 16 |
| FinOps | REPLACED (OD-20) | Application/FinOps | - | 42 (PR-5) |
| SQLite governance store | DELETE (OD-1) | Persistence Sqlite* governance, Steps/sqlite | - | 11 |
| SQLite data-source dialect | DELETE last (OD-2) | SqliteDialectGenerator, affinity, zone table | Microsoft.Data.Sqlite | 31 |

## 6. User and operator communication per stage

| Stage | Artifacts |
|---|---|
| 0 | CHANGELOG created with backfill; release notes from CHANGELOG; README without numbers; `pre-simplification` tag; freeze announced in README "Project status" (system description, no progress tags) and in issue #515 |
| 1 | Release N (next minor, `### BREAKING`): SQLite governance store removed (OD-1), default provider PostgreSQL, deleted integrations, Snowflake generator removed (was generator-only, never an admitted data source; OD-14), Oracle and Databricks stay supported data sources (OD-12) and Ansi stays generator-only (release notes state this explicitly), embedded Garnet deleted (OD-17), Flight-over-HTTP and the dev portal deleted (OD-37), new plaintext-password rejection for PostgreSQL and SQL Server connection strings (WP-SIMPL-13 Step 1); `docs/operations/migration-simplification.md` created with one row per DELETE item (feature, removed keys, replacement, last tag containing it); fail-fast messages point to the guide; one GitHub issue per deleted integration with restoration path |
| 2 | Release N+1: no user-visible API change expected; CHANGELOG notes governance repository unification and parity engines; orphaned tables dropped by forward-only steps (CHANGELOG "Changed" with table names) |
| 3 | Release N+2 (`### BREAKING`, `### Moved to modules`): `autheris` vs `autheris-full` images, `Gateway:Modules:Enabled`, module install lines; OD-2 step 3 removal; rewritten README; deprecation policy for the post-production future stated in README |

## 7. Roadmap, lanes and merge order

Roadmap rules:
- R1: no fixes for code that a later WP deletes; OBSOLETE-BY-REMOVAL issues and advisories are closed by the deleting WP (its PR names them).
- R2: the CI hang fix (#516) is merged first in Stage 0, before any other WP.
- R3: the security lane (WP-SIMPL-36) runs parallel to Stage 0 under the freeze security exception and has merge priority over deletions in the files it touches.
- R4: #504 (Security trait on tests, coverage map) merges before any deletion PR (WP-SIMPL-11..18), so deletions can prove no KEEP security test is lost.
- R5: #503 plugin-loader fix (PluginManager is the module host base) merges before WP-SIMPL-25.
- R6: fixes in code that moves to a module (dbt GHSA item, #476 CDC, #488 webhooks, #490 Lakehouse limit, #503 OpenLineage part) land before or inside the extraction WP (27-29), never after; the OLAP part of #503 is fixed in core (OD-15).
- R7: folded into deletion PRs: the Flight half of #487 (WP-SIMPL-16), most of #491 and the OpenJev part of #503 (the deleting WP named in the assessment section).
- R8 (revised for OD-12): Oracle and Databricks stay, so #479 (Oracle dialect semantics) and the Oracle part of #483 (TLS) are fixed in core in the engine/security lanes, after WP-SIMPL-21 has the Oracle parity lane in place so fixes are proven against a real Oracle; no dependency on a deletion.
- R10 (OD-12): #521 (pre-pull images, incl. Oracle and Spark) and #522 (Oracle/Stress/Spark suites to nightly) are fully in scope and land with WP-SIMPL-21: full Oracle and Spark/Databricks suites nightly, PR keeps the Oracle security smoke subset and the Databricks contract tests.
- R9: #520 (test host reuse) lands before or with WP-SIMPL-11; #516 and #517 go first in Stage 0.

Lanes (one worktree each; one build at a time per disk budget):
- **L-SEC** (security, freeze exception): 36, parallel to Stage 0.
- **L-DOC** (docs/process): 03 -> 04 -> 05; 08, 09 parallel.
- **L-CFG**: 06 -> (Stage 3) 32.
- **L-RAT**: 07, 10 (10 before and after 01).
- **L-REL**: 01, 02.
- **L-PER** (persistence, **serial, owns all governance DDL**): 11 -> 12 -> 19 -> 20a -> 20b -> 20c -> 20d -> 20e -> 29 (store part).
- **L-ENG** (engine): 13 -> 21 (incl. #521/#522 Oracle and Spark parts) -> 22 -> 31; Oracle fixes (#479, #483 Oracle part) after 21.
- **L-DEL** (deletions without DDL): 15, 16 parallel after 06; 17 after 12; 18 (owner decisions taken). WP-14 moved to L-AUTHZ.
- **L-AUTHZ** (core authorization, freeze exception, OD-19..OD-37): 43 PR 1-3b and 45 PR 1-3 -> 46 PR 1 (decision point) -> {41 PR 3+, 45 PR 4-6, 47, 42 PR-3b} and 46 PR 2-6 (43 PR 4 Envoy after 46 PR 3) -> 26 (OData core hardening) and 14 (Casbin deletion after 46 PR 1-5, 43 PR 4 and 45 PR 1-3); 38, 39, 44 in parallel; 40b-d and 42 PR-2+ in L-PER after 20. Binding order and gates: README execution table.
- **L-TST**: 23 per domain after the Stage 1 deletions of that domain.
- **L-MOD**: 25 -> 27 (pilot, runtime vs compile-time decision) -> {28, 29} (28 after 44; 29 after 20 and 26, removes the Api -> Extensions reference) -> 32 -> 33 -> 34; 35 after 27. WP-30 cancelled (OD-21).

Merge order:
1. Stage 0: #516 CI hang fix and #517 first (R2, R9); #504 before Stage 1 (R4); then 01, 02, 03, 07, 10(before) and 36 in parallel; then 04, 06, 08, 09; then 05, 10(after). Exit: Stage 0 ACs, freeze active.
2. Stage 1: 11 first (default provider change affects every later test fixture); 13, 15, 16 in parallel with 11 but merged after it (rebase on DI files); 12 after 11; 17 after 12; 18. 14 follows WP-46 (L-AUTHZ). Exit: section 3 Stage 1 column.
3. Stage 2: 19 -> 20a..20e (serial); 21 -> 22 in parallel with L-PER; 23 and 24 after Stage 1 exit. Exit: Stage 2 column; owner stop point for OD-2 (after 22).
4. Stage 3: 25 (Phase 3 review; after WP-46 PR 5) -> 27 (pilot, go/no-go for runtime loading) -> 28 -> 29 -> 31 -> 32 -> 33 -> 34 -> 35. 26 is core hardening (L-AUTHZ), 30 is cancelled. Exit: program targets; freeze lifted.

DDL rule: only L-PER changes governance DDL (WP-11 removes the SQLite tree, WP-20e adds one forward-only step per engine, WP-29 only if the events module needs a store change). The v1.5.0 frozen base is never edited.

## Open issues and advisories assessment

Assessment date 2026-11-05 (PM consolidation of seven triage slices; #527, #528, #529 and #533 added 2026-11-07 as WP-SIMPL-51; #571, #572, #573, #578, #579 and #580 added 2026-11-08 as WP-SIMPL-52 PR 4b, WP-SIMPL-53 and WP-SIMPL-54). Scope: all 61 assessed GitHub issues and the draft security advisories. **Disclosure rule (SEC-SIMPL-01):** advisories are private drafts in a public repository; this plan names them only by GHSA id and the affected component. Details stay in the advisory.

Verdicts: **BLOCKER** (stops other work), **FIX-IN-CORE** (code stays; fix it), **QUICK-WIN** (small, independent), **OBSOLETE-BY-REMOVAL** (closed by the deleting WP; no fix work), **PARTLY-OBSOLETE** (the part in deleted code is closed by the deleting WP; the rest is fixed), **DUPLICATE**, **NEEDS-DECISION** (depends on an owner decision below). Priorities P0 (now) to P3 (backlog). "Security lane" means WP-SIMPL-36 (section 4), which runs parallel to Stage 0 under the freeze exception; its first item is merging `fix/security-batch-5`.

### Sequencing rules derived from the assessment (binding for the roadmap)

1. **CI first.** #516 (hang regression, BLOCKER) and #517 (job timeouts, hang dumps) land before any WP-SIMPL work, including the CI baseline measurement (WP-SIMPL-10). Measurements taken while CI hangs are invalid.
2. **Security lane (WP-SIMPL-36) parallel to Stage 0** (freeze exception): first `fix/security-batch-5` (committed, reviewed, merged before WP-SIMPL-12/18/20/28/29 touch the same files), then #469 (P0), #505-#508 and the FIX-IN-CORE advisories below. The fail-closed audit fixes of #505 and #507 are one bundle (`RecordRequiredAsync` plus an architecture test that forbids fire-and-forget audit on write and admin paths).
3. **No fixes in code that a later WP deletes.** OBSOLETE-BY-REMOVAL items and the obsolete parts of PARTLY-OBSOLETE items are closed by the deleting WP, which links the issue in its PR. Examples: Flight half of #487, most of #491, OpenJev part of #503 and the Arrow part of #485 go into the deletion PRs (WP-SIMPL-12..16). Oracle and Databricks are kept (OD-12), so their parts of #479, #483, #521 and #522 are fixed, not closed by removal.
4. **Security test inventory before deletions.** #504 (Security trait on the #495-#497 tests, missing tests added) merges before the first deletion PR, so every deletion PR can prove with the trait filter that no security test was removed (SEC-SIMPL-03). Branch `fix/security-batch-5` already adds tagged tests; it merges first.
5. **Module prerequisites.** #503 plugin-loader collision fix precedes module extraction (WP-SIMPL-25+, `PluginManager` becomes the module host base). Fixes in code that moves to a module land before or inside the extraction WP: GHSA-jw88-9j35-3c96 and #459 before the dbt module (WP-SIMPL-28), #476 and #475 with the CDC/Events module (WP-SIMPL-29), #488 webhook items with OpenMetadata/dbt (WP-SIMPL-28), the Lakehouse part of #490 and the OpenLineage part of #503 with their modules (the OLAP part of #503 stays core, OD-15).
6. **Test host reuse before the SQLite governance removal.** 70 of the 72 integration-test files that start a host use in-memory SQLite governance. #520 (reuse hosts instead of ~650 per-test host starts) is a hard dependency of WP-SIMPL-11; WP-SIMPL-11 uses one shared PostgreSQL container per test run with a per-test-class database cloned from a migrated template database, so migrations run once.
7. **Plaintext-password guard for all engines.** Oracle stays (OD-12); the Oracle plaintext-password check is kept and generalised to PostgreSQL and SQL Server connection strings (part of the #507 bundle). No ordering dependency on WP-SIMPL-13 remains.
8. **CI workflow bundles** (workflow edits need owner approval, `[WF]`): #517 + #518 in one PR; #519 + the remaining part of #521 in a second; both in a fixed merge order with WP-SIMPL-21 and -35. With Oracle and Databricks kept (OD-12), the Oracle and Spark/Databricks suites stay: full suites nightly (as #522 proposes), a security-tagged smoke subset on PRs, Oracle and SQL Server images pre-pulled (#521). Deletions alone cut only about 10-15 % of integration-test time, so #519 (with a shard-weight regeneration script, rerun after each deletion WP) and #520 stay necessary.
9. **Options cleanup.** #477 merges into #514 / WP-SIMPL-06, except the pre-filled security allowlists, which are fixed in the security lane (empty by default, fail closed).

### Issues (61)

| Issue | Title | Verdict | Prio | Work package / sequencing |
|---|---|---|---|---|
| #459 | Declared keys/indexes only; read `meta.indexes` from dbt | FIX-IN-CORE | P2 | WP-SIMPL-44 (OD-25 widens it): after WP-SIMPL-17 (Dbt folder merge), before WP-SIMPL-28 (dbt module) |
| #467 | Differential privacy: noise, budget, ordering | OBSOLETE-BY-REMOVAL | - | Closed by the WP-SIMPL-18 deletion of differential-privacy noise (OD-11) |
| #468 | Noisy aggregate cache hit without audit entry | OBSOLETE-BY-REMOVAL | - | Closed with #467 (OD-11) |
| #469 | `Sid.GetUserSid` client-id fallback; static `RealmLookup` | FIX-IN-CORE | **P0** | Security lane |
| #470 | WebSQL `wait_timeout` requests treated as Trino transport | FIX-IN-CORE | P1 | WP-SIMPL-38 (`/v1/statement` stays core, OD-18) |
| #471 | MCP JSON-RPC batch guard inspects only 512 bytes | QUICK-WIN | P1 | Security lane (MCP is core) |
| #472 | `/health/ready` anonymous, uncached, not rate limited | QUICK-WIN | P2 | Security lane; SQLite lock contention part closed by WP-SIMPL-11 |
| #473 | Audit batch lost on shutdown; exact-match redaction | PARTLY-OBSOLETE | P1 | Split: redaction now (security lane); shutdown batch loss in the unified repository (WP-SIMPL-20) |
| #474 | CDN purge reports success without provider | QUICK-WIN | P2 | Depends on the CDN row in WP-SIMPL-18; fix only if kept |
| #475 | Table cache applies CDC mutations as string literals | FIX-IN-CORE | P2 | DuckDB stays core (OD-15), so the table cache no longer leaves core because of its dependency; the table cache stays core (WP-SIMPL-18 default); fixed in the security lane with an integration test on a real DuckDB |
| #476 | CDC custom query poller read-only only on SQLite/PostgreSQL | FIX-IN-CORE | P1 | Before or inside WP-SIMPL-29 (Events module) |
| #477 | Options with no effect; pre-filled security allowlists | PARTLY-OBSOLETE | P2 | Unconsumed options: WP-SIMPL-06; allowlists: security lane |
| #478 | Flat AND/OR chains hit AST depth limit | FIX-IN-CORE | P2 | SQL engine lane, bundled with #481 |
| #479 | Dialect semantics differ from Trino | FIX-IN-CORE | P2 | SQL engine lane, including the Oracle parts (MOD, integer division; OD-12); proven by the Oracle parity suite |
| #480 | Data source resolver defaults and swallowed errors | FIX-IN-CORE | P1 | Security lane; bundle with #486 |
| #481 | SQL function policy denylist-only; unbounded padding | FIX-IN-CORE | P1 | SQL engine lane with #478 |
| #482 | ForwardAuth tenant-prefixed roles; unregistered IP allowlist middleware | FIX-IN-CORE | P1 | WP-SIMPL-43; delete the dead middleware rather than wire it |
| #483 | Transport TLS and supply chain | FIX-IN-CORE | P1 | Security lane: Oracle TCPS required outside Development (OD-12); PostgreSQL/SQL Server TLS with WP-SIMPL-11 (SEC-SIMPL-14) |
| #484 | Read-only tokens and `/v1/statement` / cancel | QUICK-WIN | P2 | WP-SIMPL-38 |
| #485 | Hardening backlog (streaming errors, validation, caches) | PARTLY-OBSOLETE | P3 | Arrow part closed by WP-SIMPL-16; ITSM webhook handler part closed by WP-SIMPL-12 (OD-5), replaced generically by WP-SIMPL-47; dbt part with WP-SIMPL-28; rest P3 |
| #486 | Required filter column satisfied by any reference | FIX-IN-CORE | P2 | Security lane with #480 |
| #487 | Flight ticket key, keyset cursor key, cursor SID | PARTLY-OBSOLETE | P1 | Flight part closed by WP-SIMPL-16; cursor part in security lane |
| #488 | Webhook replay, buffering, embeddings client, egress | PARTLY-OBSOLETE | P1 | Egress and embeddings parts FIX-IN-CORE in security lane (OD-4); webhook parts with WP-SIMPL-28 |
| #489 | Caller labels, revocation, CSRF, ForwardAuth SID | FIX-IN-CORE | P1 | WP-SIMPL-43 |
| #490 | Download re-authorisation; Lakehouse LIMIT ordering | PARTLY-OBSOLETE | P1 | Re-authorisation in security lane; Lakehouse part with its module extraction |
| #491 | Unwired code paths and kill switch | FIX-IN-CORE | P1 | Mostly deletions: folded into WP-SIMPL-12..16; procedures part in WP-SIMPL-39 |
| #492 | Query analysis against another read-only scope | DEFERRED (default taken) | P2 | Feature request under the freeze; revisit after Stage 3 exit |
| #500 | Catalog text untrusted before MCP/LLM clients | FIX-IN-CORE | P1 | Security lane (MCP and catalog are core) |
| #501 | Catalog search index race, staleness, rate limit | FIX-IN-CORE | P2 | Security lane (OD-4: catalog search is core) |
| #502 | Set-based nested lists: cost window; SQLite version | PARTLY-OBSOLETE | P2 | Cost part SQL engine lane; SQLite part closed by WP-SIMPL-31 |
| #503 | Plugin collisions, OpenLineage, OpenJev, OLAP default | PARTLY-OBSOLETE | P1 | Plugin fix before WP-SIMPL-25; OpenJev in deletion PRs; OLAP default fixed in core (OD-15); OpenLineage with the Lineage module |
| #504 | Tag security tests from #495-#497 | QUICK-WIN | P1 | Before the first deletion PR (rule 4) |
| #505 | DML hardening | FIX-IN-CORE | P1 | Security lane (OD-3: DML stays core): audit bundle with #507, then the urgent DML items; write-grant design in WP-SIMPL-45 (OD-26); no Casbin dependency |
| #506 | Cache/header hardening | FIX-IN-CORE | P2 | Global `no-store`/`Vary` default now; OData part in core (OD-24, WP-SIMPL-26) |
| #507 | Admin plane hardening | FIX-IN-CORE | P1 | Security lane audit bundle with #505; Oracle password check kept and generalised to PostgreSQL/SQL Server; break-glass header part OBSOLETE-BY-REMOVAL (OD-5, WP-SIMPL-12) |
| #508 | MCP hardening | FIX-IN-CORE | P1 | Security lane |
| #509 | Remove ReadyToRun | QUICK-WIN | P2 | WP-SIMPL-01 (before the "after" run of WP-SIMPL-10) |
| #510 | Version from git tag; Helm default; CHANGELOG | FIX-IN-CORE | P1 | WP-SIMPL-02 `[WF]` |
| #511 | Remove unverified performance claims | QUICK-WIN | P2 | WP-SIMPL-03 (includes README.md:25), then WP-SIMPL-09 |
| #512 | Docs describe non-existent components | PARTLY-OBSOLETE | P3 | WP-SIMPL-04; Flight claims vanish with WP-SIMPL-16 |
| #513 | Docs hygiene | FIX-IN-CORE | P3 | WP-SIMPL-05 |
| #514 | Delete unconsumed options; config reference | FIX-IN-CORE | P2 | WP-SIMPL-06 (absorbs #477) |
| #515 | Structural simplification umbrella | PLANNED (this plan) | P1 | This plan, Stages 1-3; one child issue per stage/module |
| #516 | CI hang regression since #464 | **BLOCKER** | **P0** | First, before every WP (rule 1) |
| #517 | Job timeouts and hang dumps | QUICK-WIN | **P0** | With or just before #516; `[WF]` bundle with #518 |
| #518 | Coalesce main runs, path filters, concurrency | QUICK-WIN | P1 | `[WF]` bundle with #517 |
| #519 | Rebalance shards from TRX data | QUICK-WIN | P1 | Now; weight regeneration rerun after each deletion WP |
| #520 | Reuse test hosts | FIX-IN-CORE | P1 | Hard dependency of WP-SIMPL-11 and WP-SIMPL-22 (rule 6) |
| #521 | Pre-pull Testcontainers images | QUICK-WIN | P2 | Bundled with #519; Oracle image pre-pull stays (OD-12) |
| #522 | Oracle/Stress/Spark suites to nightly | QUICK-WIN | P1 | Oracle and Spark/Databricks suites stay (OD-12): full suites nightly, security-tagged smoke subset on PRs; after #519; aligned with WP-SIMPL-21 parity split |
| #523 | CI under 30 min (umbrella) | FIX-IN-CORE | P1 | Umbrella for #516-#522; target definition decided (OD-6) |
| #527 | WebSQL cost estimate and per-caller quota | FIX-IN-CORE | P1 | WP-SIMPL-51 I1; quota shared with GraphQL (missing gateway policy on a core transport, OD-3) |
| #528 | WebSQL column types and mask flags | FIX-IN-CORE | P2 | WP-SIMPL-51 I2; breaking response shape allowed |
| #529 | WebSQL syntax error position, trailing semicolon | QUICK-WIN | P2 | WP-SIMPL-51 I3; position only, no statement echo |
| #533 | WebSQL and GraphQL paging without rewriting | FIX-IN-CORE | P2 | WP-SIMPL-51 I4; bound continuation token, ORDER BY required (D-51b) |
| #571 | Catalog text of masked columns: governance decides visibility | FIX-IN-CORE | P2 | Extends WP-SIMPL-52 (5.8, PR 4b, D-52p; OD-45): per-classification `catalog_text` with per-column override, default VISIBLE (Q25) |
| #572 | Catalog: approximate table size per dataset | FIX-IN-CORE | P3 | WP-SIMPL-53 PR 3: size class from ingested metadata only (OD-25), never exact counts (Q24); freeze exception (Q23) |
| #573 | Aggregation/WebSQL without usable index: price and hint | FIX-IN-CORE | P2 | WP-SIMPL-53 PR 4 (GraphQL, after WP-44) and PR 5 (WebSQL, after WP-51 PR 1/4); freeze exception (Q23) |
| #578 | `Mcp:MaxResultRows` documented but not enforced | FIX-IN-CORE | P1 | WP-SIMPL-53 PR 1 (enforce, D-53a); WP-SIMPL-06 keeps the option |
| #579 | MCP catalog hints name disabled tools and routes | FIX-IN-CORE | P2 | WP-SIMPL-53 PR 2 (hints from `IReadPathSwitches`) |
| #580 | Golden queries maintained by data owners (dbt meta) | NEEDS-DECISION (default taken) | P3 | WP-SIMPL-54 after the feature freeze, in the dbt module (WP-28); dbt meta only (Q26, Q27) |

### Security advisories

State: see [`simplification/advisories-overview.md`](simplification/advisories-overview.md) (authoritative for state and neutral titles). This plan names advisories only by GHSA id and the neutral component title; root cause, fix approach and test notes stay in the private advisories (SEC-SIMPL-01). Security-lane item 0 is `fix/security-batch-5`: it is committed, reviewed (Phase 5) and merged first, before WP-SIMPL-12, -18, -20, -28 or -29 touch the same files. None of the advisories is specific to the SQLite governance store.

| Advisory | Component (neutral title) | State | Work package / sequencing |
|---|---|---|---|
| GHSA-jcf8-hchm-55h3 | WebSQL DML authorization | Open | WP-SIMPL-36 (DML bundle); write-grant model WP-SIMPL-45 (OD-26) |
| GHSA-7ccv-hfgq-5qwm | Federation column access checks | Open | WP-SIMPL-36 (P0); core federation (OD-15) |
| GHSA-7pjw-hcrc-hvff | Federation masking catalog | Open | WP-SIMPL-36 (P0); core federation (OD-15) |
| GHSA-v5c2-29pv-cc86 | Data source registration | Open | WP-SIMPL-36 (P0, OD-9 role model), before WP-SIMPL-17 |
| GHSA-9229-wx6j-wvm9 | Admin catalog operations | Open | WP-SIMPL-36 (P0, OD-9 role model); Backstage part closed by WP-SIMPL-12 |
| GHSA-3prq-qxg7-92qf | WebSQL DML scoping | Open | WP-SIMPL-36 (DML bundle); WP-SIMPL-45 |
| GHSA-83g2-c2jr-c8vq | WebSQL DML token scopes | Open | WP-SIMPL-36 (DML bundle); WP-SIMPL-45 |
| GHSA-66xj-4459-x8gx | WebSQL row-filter evaluation | Open | WP-SIMPL-36 (proof on PostgreSQL, SQL Server, Oracle, Databricks) |
| GHSA-jw88-9j35-3c96 | dbt semantic manifest import | Open | WP-SIMPL-36, before WP-SIMPL-28 |
| GHSA-rxr4-mf28-p2hv | Governed aggregation | Open | WP-SIMPL-36 (OD-11) |
| GHSA-92vp-663x-pp52 | Transport gate | Open | WP-SIMPL-36; procedures part in WP-SIMPL-39; module parts before WP-SIMPL-27 and WP-SIMPL-29 |
| GHSA-p234-g3ph-gqh2 | Docs ingestion store | Open | WP-SIMPL-36 sub-package 7; WP-SIMPL-40 keeps the regression test green (OD-18) |
| GHSA-v77c-jh96-7rgr | Access profile persistence | Open | WP-SIMPL-36, before WP-SIMPL-20 |
| GHSA-x8g9-cm3h-p6jq | Authorization caches | Open | WP-SIMPL-36; coordinate with WP-SIMPL-15 and WP-SIMPL-43 |
| GHSA-v75c-28mx-7wm7 | WebSQL DML request handling | Open | WP-SIMPL-36 (DML bundle); WP-SIMPL-45 |
| GHSA-qx32-576x-5cq2 | GDPR disclosure report | Closed | Stays core (OD-10), not moved with Lineage in WP-SIMPL-29 |
| GHSA-xpw9-4m63-69pf | Federation result masking | Closed | - |
| GHSA-95f9-ffc7-vw2c | Catalog search | Closed | Catalog search stays core (OD-4); WP-SIMPL-36 re-checks residual follow-ups against `main` |
| GHSA-53m3-948q-hj4x | Async job export | Closed | - |
| GHSA-jfrx-2mgc-pfh5 | External catalog sync | Closed | Closed on `main`; WP-SIMPL-36 sub-package 5 re-verifies only (before WP-SIMPL-28) |
| GHSA-4qx8-fv2p-q8r2 | Audit archive | Closed | WP-SIMPL-36 re-checks residual follow-ups against `main` |
| GHSA-78j7-3gh6-7cqx | ReBAC enforcement | Closed | ReBAC removed by WP-SIMPL-46 PR 5 (OD-30) |
| GHSA-335j-6vwc-7p4c | Access profile revoke | Closed | - |
| GHSA-cj7m-c75j-6p6h | Resource limits | Closed | - |
| GHSA-qfx6-4vp6-4hjv | Windows authentication | Closed | Negotiate stays core (OD-21) |
| GHSA-jcpx-6m27-2m97 | Error responses | Closed | - |
| GHSA-547h-29x4-m4ch | dbt sync | Closed | - |
| GHSA-6523-m29q-c397 | Governed aggregation | Closed | - |
| GHSA-mgv5-4r69-mwww | Admin role model | Closed | - |
| GHSA-wg8p-wfw5-qvgj | Step-up approval | Closed | - |
| GHSA-hw85-rqjh-cwxx | Token client allowlist | Closed | Before WP-SIMPL-43 PR 2 |
| GHSA-wvwx-q6jg-44wv | CDC webhooks | Closed | Before WP-SIMPL-29 |
| GHSA-3953-r994-fg4m | Transport gate | Closed | - |

### Owner decisions from the assessment

All owner decisions are recorded in PRD section 2.0 (OD-1..OD-45, binding); this section only maps them to the assessment.
- **OD-3** WebSQL DML stays core; **OD-4** catalog search including embeddings is core; **OD-5** ITSM integration and the header-based break-glass/justification flow are deleted (access-profile break-glass assignments are removed as well by OD-32).
- **OD-6** CI target counts queue time (#523 definition: PR wall-clock from push to the last required check, median, < 30 min); **OD-7** ReBAC enforcement on all paths, revised by OD-30 (ReBAC removed); **OD-8** unwired WORM archive export deleted, startup fails on `Audit:Worm:Enabled`, retention purge keeps signed checkpoints and anchors; **OD-9** global operations only for canonical `ClusterAdmin`, `GovernanceAdmin` always tenant-bound (batch 5 is adjusted before merge); **OD-10** the GDPR disclosure report stays core; **OD-11** governed aggregation with small-group suppression and time buckets stays core, differential-privacy noise is deleted (#467, #468 obsolete).
- **OD-12** Oracle and Databricks stay in core as governed data sources; the Oracle/Databricks parts of #479, #483, #521 and #522 are FIX-IN-CORE or QUICK-WIN instead of closed by removal. **OD-13** the PostgreSQL wire protocol proposal is dropped. **OD-14** deletes the Snowflake generator and keeps Ansi as a generator-only dialect. **OD-15 (extended)** keeps DuckDB entirely in core (federation, Olap, table cache with #475 fixed in core, generator, tests). **OD-16** three Databricks authentication modes (WP-SIMPL-37). **OD-17** accepts all remaining recommendations (embedded Garnet deleted, 220k core target, ADR-019 accepted, module signing scheme re-reviewed before WP-25, freeze and PR limit, second owner before Stage 3, `[WF]` bundles, SQL Server governance kept; its OData pilot item is superseded by OD-24).
- **OD-18..OD-43** add the work packages WP-SIMPL-38..48 and the defaults of OD-37; OD-38..OD-43 (row policies, dynamic coverage, per-cell column state, grant attribution, transparency) add WP-SIMPL-48; OD-44 (data products) adds WP-SIMPL-49 after the feature freeze; see PRD section 2.0 and `simplification/README.md`.
- **OD-45 (binding, owner, 2026-11-07)**: classification-based masking. Masking is not only for PII: columns carry one or more tenant-scoped data classifications (for example PII, PII-special, Financial, Confidential, Credentials, Public); masking rules with typed mask functions are defined per classification with a per-column override; grants state Clear, Masked or Deny per classification, and clear access is requested separately per classification. Spec: WP-SIMPL-52 (also consolidates the governance data model).

## 8. Open architecture questions (default taken)

| ID | Question | Default taken |
|---|---|---|
| Q23 | Freeze status of WP-SIMPL-53 PR 3-5 (new response fields for #572, #573) | Carried under `freeze-exception` with the issue link, like WP-51 (client-guidance gaps on core paths); if the owner declines, PR 3-5 move after Stage 3 exit. Owner decision open |
| Q24 | Table size exposure (#572) | Coarse size class with source and age, visible datasets only, never exact counts, also for datasets with row policies. Owner decision open |
| Q25 | Granularity of the masked-column catalog-text setting (#571) | Per classification rule with per-column override, default VISIBLE; no per-grant or per-scope setting. Owner decision open |
| Q26 | When to build golden queries from dbt (#580) | After the feature freeze (WP-SIMPL-54, new table and ingestion path, dbt module first). Owner decision open |
| Q27 | Golden-query catalog REST API (#580 option 2) | Not built; dbt meta is the only owner-maintained source. Owner decision open |
| Q1 | Where do modules live? | Same repo under `modules/`, same tag and version as the host |
| Q2 | Module release cadence | Same as host (deviation from PRD "own cadence"; single version source wins); revisit after first production release |
| Q3 | Third-party/custom modules | Not supported; only first-party signed modules; custom code uses the existing plugin interfaces |
| Q4 | Neutral-model generation of migration steps | No; hand-written per-engine steps plus step-parity test (deviation from REQ-SIMPL-05.3, justified by about 130 lines of steps) |
| Q5 | Orphaned tables and module-owned stores | One forward-only drop step per engine in WP-20e; the events module uses the governance connection factory, its schema stays in the core migration tree |
| Q6 | `TargetSqlDialect.Ansi` | Kept as generator-only dialect (OD-14); never selectable as a data-source target |
| Q7 | Procedures (ADR-018) | **Decided (OD-18):** core, clean redesign in WP-SIMPL-39 |
| Q8 | Analytics (governed aggregation) and differential privacy | **Decided (OD-11):** aggregation core, differential-privacy noise deleted |
| Q9 | DuckDB in tests | Core DuckDB integration and parity tests (federation, Olap, table cache; OD-15); never in Unit (WP-SIMPL-22 rule) |
| Q10 | Manifest signature algorithm | ECDSA P-256 from the BCL; private key in a GitHub secret; rotation by host release |
| Q11 | Versioning tool | No MinVer; release.yml passes `-p:Version` from the tag; local builds `0.0.0-dev` |
| Q12 | CHANGELOG generation | Hand-maintained `[Unreleased]` section; release notes extracted from it |
| Q13 | Stryker configs | Merge security-guards into the main config: 2 configs (main, governance) |
| Q14 | src LOC metric and exit target | `src/` only, modules counted separately; core exit target <= 220,000 (OD-17(2); +22% vs PRD, caused by binding OD-12/OD-15, not by a data-driven revision; section 3) |
| Q15 | Docs checks on docs/plans | Links rewritten, not gated |
| Q16 | `McpOptions.RequirePiiMasking` | Already removed (verified); masking is unconditional |
| Q17 | `ScimOptions.AllowedIpRanges` | Kept and enforced in core (verified; SCIM is core, OD-21) |
| Q18 | Spark CI job | Kept (Databricks stays, OD-12); moves from ci.yml to `parity-nightly.yml` as the nightly Databricks fallback when no real-workspace credentials exist (WP-SIMPL-21, #522) |
| Q20 | Snowflake and Ansi generators | Decided (OD-14, revised): Snowflake deleted in WP-SIMPL-13; Ansi kept generator-only, not admitted as data source |
| Q22 | Embedded Garnet server | **Decided (OD-17):** delete in WP-SIMPL-15; external Redis/Garnet supported, per-node in-memory cache otherwise |
| Q21 | Databricks real-workspace parity | Nightly only when repository secrets for a test workspace exist; otherwise Spark container; results file records `engine=databricks|spark` **Decided (OD-17):** plan the nightly secrets; Spark container is the fallback until they exist. |
| Q19 | SQL Server as governance DB (D4) | Kept; revisit after Stage 2 with the AD-1 design making removal cheap |

## 9. Risks (architecture)

| Risk | Mitigation |
|---|---|
| Runtime module loading adds an untrusted-code path | Signed manifest, embedded key, explicit enable list, fail closed, Phase 3 review before WP-25 merges; compile-time fallback |
| Unified repository changes SQL semantics | Contract suite on both engines before and after each step; DEF-02 pinned |
| Parallel deletion PRs conflict in DI registration files | Merge order in section 7; small PRs; rebase before merge |
| Stage targets missed | Measured per stage by checked-in scripts; revisions only with data (max 20%) |
| Test reshuffle hides lost coverage | Stryker score on KEEP code must not drop; security tests are moved, never deleted, unless the feature is deleted |
| Oracle/Databricks kept (OD-12): PR CI time grows with an Oracle container | PR runs only the security-tagged Oracle smoke subset on a pre-pulled image (#521); full suite nightly (#522); Oracle smoke duration reported against the section 3 CI budget |
| Databricks parity without a real workspace covers Spark semantics only | Contract tests on PR pin generated SQL and executor behaviour; nightly real-workspace run when credentials exist; Spark gaps recorded per cell, never as silent pass |

## Phase 3 security review (csharp-security-expert)

See [2026-11-05-sec-project-simplification.md](2026-11-05-sec-project-simplification.md).

## Work package specifications

Developer-ready specifications for WP-SIMPL-01..50 (steps, tests, acceptance, PR slicing), execution order and corrections found while writing them: [`simplification/README.md`](simplification/README.md).
