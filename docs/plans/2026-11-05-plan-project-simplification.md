# PLAN-SIMPL: Project Simplification Program (Architecture Plan)

- Phase: 2 (Solution Architect). Date: 2026-11-05. Base: origin/main fe0e131d (branch `docs/plans-project-simplification`).
- Input PRD: [2026-11-05-req-project-simplification.md](2026-11-05-req-project-simplification.md) (REQ-SIMPL-01..13, OD-1 binding, OD-2 default taken, D1-D15).
- Related: [2026-10-26-plan-architecture-maintainability.md](2026-10-26-plan-architecture-maintainability.md) (ARCH-MNT-22 dialect dedup; section 2.4 ignored flags). ADR: [ADR-019](../adr/ADR-019-sql-engine-build-vs-buy.md) (Proposed, pending owner acceptance: keep and shrink the in-house engine).
- Tracking: issues #509-#514 (Stage 0), #515 (Stages 1-3, one child issue per stage and per module). CI-speed issues #516-#522 are coordinated, not duplicated.
- Constraints: no production deployment (breaking changes allowed, no shims); forward-only governance migrations with v1.5.0 frozen base; English only; workflow files under `.github/workflows/` are changed only by the WP that owns the change, and every such change is an **owner-approved item** (marked `[WF]`).

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
| Heavy packages | Application: Casbin.NET, DuckDB.NET.Data.Full, Apache.Arrow, Parquet.Net, YamlDotNet. Infrastructure: Microsoft.Data.Sqlite, Microsoft.Garnet, Oracle.ManagedDataAccess.Core. Api: Microsoft.AspNetCore.Authentication.Negotiate. No central package management |
| Dialect generators | `src/TrinoSqlEngine/Ast/Generators/`: SqlServer 693, Oracle 559, Analytical (DuckDb/Snowflake/Ansi) 453, Sqlite 433, PostgreSql 375, Databricks 364, base 1,828; enum `src/TrinoSqlEngine/TargetSqlDialect.cs` (8 values) |
| Dialect tests | TrinoSqlEngine.Tests name-matching files: Oracle 973, Sqlite 1,053, Databricks 311, DuckDb 279 lines; all test projects name-matching: Oracle 3,759, Databricks 2,804, DuckDb 2,371 |
| Option guard | `Api/Configuration/UnknownGatewayKeyGuard.ThrowIfUnknownKeys` throws on keys outside `GatewayConfigurationSchema` (removed keys fail fast for free once removed from the schema) |
| Ratchet baselines | `tests/Autheris.Tests.Architecture/Baselines/`: unconsumed-options, options-consumed-indirectly, test-sleep-counts, source-size-ceilings, project-edges, namespace-assembly-violations, bare-error-body-counts, datetime-now-counts, ingress-exemptions |
| Workflows | ci.yml (unit job, 4 integration shards, Spark job), parity-nightly, sql-compiler-gate-nightly, simulation-nightly, release, docker-publish, trivy, codeql, policy-lint, governance-immutability |
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

### AD-2 SQL engine: keep, reduce scope (decision; ADR-019, status Proposed, pending owner acceptance: keep and shrink)

Keep the in-house TrinoSqlEngine (ANTLR Trino grammar, own AST, `AstSecurityVisitor`, dialect generators). Core dialects: PostgreSQL and SQL Server; SQLite until OD-2 step 3 (WP-SIMPL-31). The DuckDB-as-target generator is deleted from core when Olap leaves core (WP-SIMPL-26); the Olap module carries any DuckDB SQL it needs. Engine + tests 59,390 -> <= 45,000 lines. Oracle, Databricks, Snowflake and Ansi-as-target generators are deleted in Stage 1. `TargetSqlDialect.Ansi` stays only if it is the internal canonical form (open question Q6). CI gate: a generator without a parity baseline file fails the build. Stryker configs 3 -> 2. TrinoSqlEngine + tests <= 45,000 lines.

### AD-3 Module mechanism: runtime-loaded first-party modules with a signed manifest (decision, D3 confirmed)

1. Contract project `src/Autheris.Modules.Abstractions` (no dependency besides ASP.NET Core abstractions and `Autheris.Domain`):
   - `IAutherisModule { string Id; void ConfigureServices(IServiceCollection, IConfiguration, IModuleHostContext); void MapEndpoints(IEndpointRouteBuilder); IReadOnlyList<string> ConfigurationSections; }`
   - optional `IAutherisGraphQLModule` (in `Autheris.GraphQL`) for type extensions.
   - `IModuleHostContext` exposes only governed services: query pipeline (SQL gate), catalog read, audit writer, consent/RLS evaluation, `TimeProvider`, `IHttpClientFactory` with the egress allow-list. No raw data-source `DbConnection`.
2. Layout: `modules/Autheris.Modules.<Name>/` and `modules/Autheris.Modules.<Name>.Tests/` (outside `src/`, in `Autheris.sln`); each module is published to `modules/<id>/` with `module.json`.
3. Manifest `module.json`: `id`, `version` (must equal the host version; one tag builds host and modules), `abstractionsVersion`, `entryAssembly`, `entryType`, `files[{path, sha256}]`, `signature` (ECDSA P-256 over the canonical manifest without `signature`, BCL `System.Security.Cryptography.ECDsa`, no new package).
4. Trust anchor: release public key embedded in `Autheris.Api` as a resource. Only first-party signed modules are supported (Q3); operators cannot add keys. Development builds accept an unsigned manifest only when `ASPNETCORE_ENVIRONMENT=Development` and `Gateway:Modules:AllowUnsignedInDevelopment=true` (same pattern as `PluginManager`).
5. Loader `Autheris.Api/Hosting/ModuleLoader` runs in `Program.cs` before `builder.Build()`: reads `Gateway:Modules:Enabled` (explicit allow-list); for each id verifies signature, version equality, every file hash, rejects files not listed in the manifest, loads into a non-collectible `ModuleAssemblyLoadContext` that resolves `Autheris.*`, `Microsoft.*`, `System.*` from the default context and module-private dependencies from the module folder; a private dependency whose name collides with a host assembly fails startup. Any failure (enabled but missing, unsigned, hash mismatch, version mismatch, duplicate id, exception in `ConfigureServices`) stops startup with an error naming the module. Present but not enabled modules are not loaded.
6. Endpoints: the host maps each module inside a host-owned route group that applies the default authorization policy and the existing endpoint filters (`EndpointSecurity`, `RebacEndpointFilter`); existing routes keep their paths.
7. Configuration: `UnknownGatewayKeyGuard` accepts a module's `ConfigurationSections` only when the module is loaded; keys of a module that is not installed fail fast with "module <id> is not installed".
8. Trust-code reuse: SHA-256 and manifest parsing move from `PluginManager`/`PluginTrustList` into `Infrastructure/Modules/Trust` and are shared by plugins and modules. The existing plugin feature stays (KEEP as module host base).
9. Image flavors: `autheris` (core, no modules) and `autheris-full` (core + all first-party modules under `/app/modules`). Build argument `FLAVOR` in `Dockerfile*` `[WF]` docker-publish.yml publishes both tags from the same version.
10. Fallback (compile-time, default if Phase 3 rejects runtime loading or the Olap pilot shows HotChocolate/OpenAPI registration cannot work pre-Build): `-p:AutherisFlavor=full` adds `ProjectReference`s to modules; discovery via `[assembly: AutherisModule(typeof(...))]`; same `IAutherisModule` contract, no ALC, no signature. The Api -> Extensions reference is removed in both variants.
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
| src LOC (src/ only) | 302,461 | <= 275,000 (PRD 250k; measured DELETE footprint about 27k) | <= 255,000 (PRD 230k) | <= 215,000 core (PRD 180k, +19%: modules move out about 52k, deletes 27k, unification 8k; ownerless-module deletions lower it further); modules <= 55,000 |
| Map* endpoints in src | 275 | <= 240 | <= 230 | <= 140 |
| Application top-level folders | 49 | <= 40 | <= 32 | <= 25, 0 duplicates |
| Governance repository LOC (AD-1 metric) | ~18,580 (3 families + cores) | ~14,480 | <= 7,000 | <= 6,000 |
| Governance dialects | 3 | 2 | 2 | 2 |
| Data-source generators | 6 | 3 (PG, SS, SQLite) + DuckDb pending module | 3, all parity on real engines, none Pending | 2 (PG, SS) |
| Core image / cold start P50 | 441 MB / 5.58 s (as configured) | measured after R2R removal and package deletions | measured | <= 250 MB / <= 3.0 s; warm P99 <= 3.47 ms |
| Options classes / settable props / `*Enabled` | 173 / ~1,173 / 96 | measured | measured | <= 90 / <= 600 / <= 40 |
| Unconsumed options baseline | 46 lines | 0 (Stage 0) | 0 | 0, file removed |
| Tests: container+integration share / Substitute.For / Received / sleeps | 6.3% / 4,159 / 986 / 92 | ratchets active | >= 12% / <= 3,000 / <= 700 / <= 40 | >= 15% / <= 2,000 / <= 400 / <= 20 |
| Test LOC | 382,125 | follows deletions | <= 300,000 | <= 240,000 |
| CI time PR / full | measured in WP-SIMPL-10 | not worse than baseline | <= 20 / <= 40 min | <= 15 / <= 30 min (D7) |
| Hosted services / DI registrations | 39 / 549 | measured | measured | <= 20 / <= 330 |

## 4. Work packages

Summary (order = roadmap order; L-PER = serial persistence/DDL lane):

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
| 13 | 1 | #515 | Delete Oracle/Databricks/Snowflake/Ansi dialects | L-ENG | - | -3,000 | L 4 |
| 14 | 1 | #515 | Delete Casbin | L-DEL | 06 | -3,000 | M 2 |
| 15 | 1 | #515 | Delete embedded Garnet, traffic shadowing | L-DEL | - | -900 | M 1.5 |
| 16 | 1 | #515 | Delete Flight-over-HTTP, FinOps, Mesh, dev portal | L-DEL | 04 | -1,800 | M 2 |
| 17 | 1 | #515 | Merge duplicate folders | L-DEL | 12 | -750 | M 2.5 |
| 18 | 1 | #515 | Borderline triage (D2) | L-DEL | owner | 0..-11,000 | S-L 1-5 |
| 19 | 2 | #515 | Governance contract suite (PG + SS) | L-PER | 11, 12 | tests | M 4 |
| 20 | 2 | #515 | Unified governance repository (20a-20e) | L-PER | 19 | -7,500 | L 11 |
| 21 | 2 | #515 | Parity on real engines, dialect gate `[WF]` | L-ENG | 13 | tests | L 5 |
| 22 | 2 | #515 | Real-DB tests out of Unit | L-ENG | 11, 21 | tests | L 7 |
| 23 | 2 | #515 | Test-suite restructuring + ratchets | L-TST | Stage 1 | tests | L 6 |
| 24 | 2 | #515 | Application folders <= 32 | L-DEL | 17, 18 | ~0 | M 3 |
| 25 | 3 | #515 | Module host `[WF]` | L-MOD | Stage 2 | +900 | L 7 |
| 26 | 3 | #515 | Pilot module Olap | L-MOD | 25 | -3,600 | M 3 |
| 27 | 3 | #515 | Modules OData, Lakehouse | L-MOD | 26 | -7,300 | M 4 |
| 28 | 3 | #515 | Modules dbt, OpenMetadata | L-MOD | 26, 17 | -8,000 | M 4 |
| 29 | 3 | #515 | Modules Events (CDC+Webhooks), Lineage | L-MOD | 26, 20 | -11,000 | L 6 |
| 30 | 3 | #515 | Modules SCIM, TOTP, Negotiate, Envoy (+owned borderline) | L-MOD | 26 | -5,000..-15,000 | L 5 |
| 31 | 3 | #515 | Full SQLite removal step 3 (OD-2) `[WF]` | L-ENG | 21, 22 | -1,000 | M 3 |
| 32 | 3 | #514 | Configuration surface reduction | L-CFG | 26-30 | -1,500 | M 4 |
| 33 | 3 | #515 | Plan-id comment cleanup | L-MOD | 26-32 | -1,900 | M 2 |
| 34 | 3 | #515 | README, feature docs, migration guide | L-DOC | 33 | docs | M 3 |
| 35 | 3 | #509 | Footprint budget CI job `[WF]` | L-RAT | 10, 26-30 | scripts | S-M 2 |
| 36 | 0 (parallel) | #469, #505-#508, advisories | Security lane: merge `fix/security-batch-5` first, fail-closed audit bundle (#505, #507), DML bundle (OD-3), FIX-IN-CORE issues and advisories (see the assessment section) | L-SEC | - | small | L 8-12 |

Effort in person-days (pd). LOC delta is src (tests in brackets). Every WP: TDD per lifecycle; deleting WPs add their rows to the migration guide and the CHANGELOG "Removed" section in the same PR (section 6); removed config keys disappear from `GatewayConfigurationSchema` so `UnknownGatewayKeyGuard` fails fast.

### Stage 0 - Quick wins (issues #509-#514)

#### WP-SIMPL-01 Remove ReadyToRun (#509)
- Scope: delete `Autheris.Api.csproj:34-39` R2R comment and `PublishReadyToRun`; keep `RuntimeIdentifiers` only if the locked restore needs it (verify with `dotnet restore --locked-mode`); delete `benchmarks/Autheris.Benchmarks/ReadyToRunBenchmark.cs`; keep `benchmarks/results/r2r-spike-report.md` as evidence. No workflow edit (docker-publish.yml has no R2R reference).
- Deps: none. LOC: -10 (-ReadyToRunBenchmark). Effort: S, 0.5 pd. Risk: locked restore graph change (low).
- Tests: `NoReadyToRunArchitectureTests.No_project_or_props_file_enables_PublishReadyToRun` (scans all `*.csproj`, `Directory.Build.*`).
- AC/target: no `PublishReadyToRun` in the repo; image measured by WP-SIMPL-10 is <= 373 MB.

#### WP-SIMPL-02 Version from git tag, Helm coupling, CHANGELOG (#510) `[WF]`
- Scope: `Directory.Build.props` `<Version>0.0.0-dev</Version>` (overridable); `Chart.yaml` `version`/`appVersion` `0.0.0-dev` in git; `release.yml` derives `VERSION=${TAG#v}` and passes `-p:Version=$VERSION` to publish, `helm package --version $VERSION --app-version $VERSION`, image tag `$VERSION`; new release-validation step asserts tag == assembly `InformationalVersion` (read from the published dll) == chart version == chart appVersion == image tag. No MinVer (Q11). `values.yaml` tag stays empty and falls back to the packaged appVersion.
- CHANGELOG.md (Keep a Changelog): backfilled v1.0.0..v1.7.0 grouped by minor; v1.0-v1.4 marked "unsupported upgrade base (oldest supported: v1.5.0)"; `## [Unreleased]` with `### BREAKING`, `### Removed`, `### Moved to modules`, `### Fixed`, `### Security`. Release notes = CHANGELOG section of the tag, extracted by a script in `release.yml`.
- Deps: none. LOC: +40 workflow/script, +~150 CHANGELOG. Effort: M, 2 pd. Risk: docker-publish.yml builds on main without a tag (keeps `0.0.0-dev+sha`).
- Tests: `ReleaseVersionArchitectureTests.Directory_build_props_holds_no_product_version`, `...Chart_versions_are_placeholders`; release-validation step itself is the runtime check.
- AC/target: inconsistent versions = 0 on the next tag; `helm install` default pulls the chart's own appVersion; CHANGELOG present.

#### WP-SIMPL-03 Remove unverified performance claims (#511)
- Scope: `README.md:56-58` numbers and the HotChocolate/Native AOT version text; `README.md:25` link text "64,500+ req/s BenchmarkDotNet reports" (and the `docs/comparisons/README.md` link it carries); delete `docs/comparisons/performance-benchmarks.md` (no protocol exists) and its links; delete `.agents/skills/readme-copywriter/` and `.agents/agents/readme-copywriter.md` (prescribes the numbers, `SKILL.md:36`).
- Deps: none. LOC: docs only. Effort: S, 1 pd.
- Tests: `DocsClaimsArchitectureTests.Performance_figures_link_a_results_file` (regex `req/s|P99|P50|\d+(\.\d+)? ?ms` in README.md and docs/ outside docs/plans requires a link into `benchmarks/results/` or `benchmarks/protocol/` on the same line or paragraph).
- AC/target: unsupported performance claims = 0.

#### WP-SIMPL-04 Docs describing non-existent components (#512)
- Scope: rewrite `docs/features/f-sec-04-rebac-openfga.md` to the in-memory Zanzibar evaluator (`InMemoryRebacStore`, `ZanzibarRebacEvaluator`) and rename the file; delete f-ai-09, f-ai-10, f-gov-06, f-perf-10 and their index lines in `docs/features/README.md`; remove all "Flight SQL", "JDBC", "ODBC" claims (the endpoints are deleted in WP-SIMPL-16; until then docs say "HTTP endpoints returning Arrow IPC").
- Deps: none. Effort: S, 1.5 pd.
- Tests: covered by WP-SIMPL-05 `Docs_reference_existing_source_paths` and a protocol-claim check `DocsClaimsArchitectureTests.Protocol_claims_require_interop_test` (JDBC/ODBC/gRPC/Flight words outside docs/plans fail unless allow-listed with a test name).
- AC/target: docs referencing non-existent source files = 0.

#### WP-SIMPL-05 Docs hygiene and docs checks (#513)
- Scope: script-rewrite all `file:///root/autheris/` links to relative links (all tracked `*.md`, including CLAUDE.md, AGENTS.md and docs/plans); fix 23 broken relative links; remove `Status:` tags and "100% GA / Production-Ready" wording outside docs/plans; condense `docs/architecture/konzept-data-governance-und-mcp-documentation.md` into the English architecture doc and delete it. Checks run as architecture tests in the existing CI job, so no workflow edit is needed.
- Deps: WP-SIMPL-04 (merge after it to avoid double edits). Effort: M, 2.5 pd.
- Tests: `DocsIntegrityTests.No_absolute_file_links`, `.Relative_links_resolve`, `.Backticked_source_paths_exist`, `.No_status_tags_outside_plans`, `.Docs_are_english` (heuristic: German stop-word density). Gated scope: README.md, root `*.md`, `docs/` outside `docs/plans`, module READMEs (docs/plans is rewritten but not gated, Q15).
- AC/target: broken links 0, `file:///` 0, status tags 0.

#### WP-SIMPL-06 Delete unconsumed options; generated configuration reference (#514)
- Scope: delete every entry of `Baselines/unconsumed-options.txt` (WebhooksOptions delivery block, QueryCostOptions BaseQueryCost/CostPerRowReturned/NestedDepthFactor, RebacOptions OpenFgaApiUrl/OpenFgaStoreId, ...) and the baseline file; the `GatewayOptionsConsumptionRatchetTests` keeps running with no baseline escape. ARCH-MNT 2.4 flags: `CasbinOptions.EnforceInQueryPipeline` deleted now (Casbin itself in WP-SIMPL-14); `ScimOptions.AllowedIpRanges` deleted (the SCIM module may re-add it only with enforcement); `McpOptions.RequirePiiMasking` deleted because masking is unconditional in the shared pipeline (Phase 3 to confirm, Q16).
- Reference generator: `tests/Autheris.Tests.Architecture/ConfigurationReferenceTests` reflects over all `*Options` types bound under `GatewayConfigurationSchema`, reads XML doc summaries (`GenerateDocumentationFile` on options-bearing projects with `CS1591` suppressed), and compares with the checked-in `docs/reference/configuration.md` (columns: key, type, default, description, source file). `AUTHERIS_UPDATE_CONFIG_REFERENCE=1` regenerates. Publishing it as a CI artifact needs one upload step in ci.yml `[WF]` (optional; the checked-in file is the source of truth).
- Deps: none (WP-SIMPL-14 and modules later delete or move more options). LOC: -400 to -600. Effort: M, 3 pd. Risk: options read indirectly (`options-consumed-indirectly.txt`) misclassified; verify each with a grep before deletion.
- Tests: `ConfigurationReferenceTests.Reference_is_current`, `.Every_option_has_a_description`, `GatewayOptionsConsumptionRatchetTests` (no baseline), `UnknownGatewayKeyGuardTests.Removed_key_fails_startup` (one sample key per removed option class).
- AC/target: unconsumed baseline 0 and file deleted; undocumented option names 0.

#### WP-SIMPL-07 Freeze mechanics and program ratchets
- Scope: architecture ratchets that make the freeze mechanical: `ProgramRatchetTests.Endpoint_count_does_not_grow` (Map* count in src, baseline file `endpoint-count.txt`), `.Options_class_count_does_not_grow`, `.Enabled_flag_count_does_not_grow`, `.Application_top_level_folders_are_allow_listed`, `.Plan_id_comments_do_not_grow` (regex for plan ids such as `REV-\d+`, `SEC-\w+`, `AST-X\d`, `ARCH-MNT-\d+`, `PLAN-[A-Z]+-\d+`, baseline per project). Each ratchet only decreases. PR template gets a freeze checklist and the `freeze-exception` label rule. PR net-LOC check (REQ-SIMPL-02c): a ci.yml step computing `git diff --numstat origin/main...HEAD -- src` minus generated files and migrations, failing above +400 unless the `size-exception` label is present `[WF]`. Tag `pre-simplification` on the base commit (owner action).
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
- DDL: none (orphaned ITSM outbox tables are dropped in WP-SIMPL-20e).
- Deps: WP-SIMPL-11 merged (both touch governance repository partials). LOC: about -4,000 src (-4,000 tests). Effort: M, 3 pd. Risk: `GovernanceSchemaShapeVerifier` may expect ITSM tables; keep the expected shape until WP-SIMPL-20e.
- Tests: `DeletedFeaturesArchitectureTests.No_types_in_deleted_namespaces` (list grows per WP), `UnknownGatewayKeyGuardTests` sample for removed sections.
- AC: no reference to Purview/Collibra/Alation/DataHub/Backstage/Itsm in src, config schema or docs outside docs/plans and the migration guide.

#### WP-SIMPL-13 Delete Oracle, Databricks, Snowflake and Ansi-as-target dialects (ADR-019)
- Prerequisite step (separate commit, before any Oracle deletion): generalise the Oracle-only plaintext-password check `OracleConnectionSupport.HasPlaintextPassword` into a provider-neutral `ConnectionSecretPolicy.HasPlaintextPassword(provider, connectionString)` covering PostgreSQL (`NpgsqlConnectionStringBuilder.Password`) and SQL Server (`SqlConnectionStringBuilder.Password`), wired into the same startup/connection checks that call the Oracle variant today, with the same fail-closed outcome. Tests: `ConnectionSecretPolicyTests.PostgreSql_plaintext_password_is_rejected`, `.SqlServer_plaintext_password_is_rejected`, `.Secret_reference_is_accepted`. Phase 3 confirms the policy outcome (reject vs warn) matches today's Oracle behaviour.
- Delete: `OracleDialectGenerator.cs`, `DatabricksDialectGenerator.cs`, Snowflake and Ansi target parts of `AnalyticalDialectGenerators.cs` (DuckDb part stays for WP-SIMPL-26), Oracle connectivity (`OracleConnectionSupport.cs`, `BindByNameOracleConnection.cs`, `Persistence/Catalog/OracleColumnMetadataReader.cs`, Oracle branches in `SqlConnectionFactory`, `SessionPinning`, `DatabaseParameterBudgetProvider`, `Dialects/SqlDialectRegistry`), Databricks connector, enum values in `TargetSqlDialect.cs`; package `Oracle.ManagedDataAccess.Core`; Testcontainers.Oracle in 2 test csproj; `OracleDriverArchitectureTests`, `OracleIntegrationTestArchitectureTests`; Oracle/Databricks parity cells and engine tests (about 7,800 test lines). Config validation rejects the removed dialect names with a clear error.
- CI `[WF]`: remove the Spark integration job in ci.yml if it only serves Databricks/Spark (verify first, Q18).
- Deps: none (engine lane). LOC: about -3,000 src (-7,800 tests). Effort: L, 4 pd. Risk: shared helpers in `SqlDialectGeneratorBase` used only by deleted generators (remove dead branches; Stryker governance run confirms no survivors added).
- Tests: `TargetSqlDialectTests.Only_supported_values_exist`, `DialectConfigurationTests.Removed_dialect_fails_validation`.
- AC/target: generators 3 + DuckDb (pending module); Oracle package absent.

#### WP-SIMPL-14 Delete Casbin
- Delete: Casbin engine, adapters and options (8 files, 2,667 lines, 58 mention files), `CasbinDecisionBenchmark.cs`, package `Casbin.NET`; policies that used Casbin map to native RBAC/ReBAC (migration guide row).
- Deps: WP-SIMPL-06 (option already gone). LOC: about -3,000 src. Effort: M, 2 pd. Risk: Casbin hooked into the policy pipeline; mitigate with `NoScopedAccessPolicyEngineTests` and policy simulation tests staying green.
- Tests: `DeletedFeaturesArchitectureTests` entry; `PackageBoundaryArchitectureTests` forbids `Casbin.*`.

#### WP-SIMPL-15 Delete embedded Garnet server and traffic shadowing
- Delete: `Infrastructure/Garnet/GarnetServerManager.cs` (260), `Program.cs:106-111`, `Caching:Garnet:EnableEmbeddedServer`, package `Microsoft.Garnet`; traffic shadowing (3 files, 376 lines) with its options and middleware registration. `StackExchange.Redis` stays (external Redis/Garnet optional).
- Deps: none. LOC: about -900. Effort: M, 1.5 pd. Risk: tests starting an embedded server for distributed-state tests move to a Redis Testcontainer or in-memory state.
- Tests: `ProgramStartupTests.No_embedded_cache_server_started`; `PackageBoundaryArchitectureTests` forbids `Microsoft.Garnet`.

#### WP-SIMPL-16 Delete Arrow Flight SQL over HTTP, FinOps, Mesh, dev portal in release builds
- Delete: `Api/Endpoints/ArrowFlightSqlEndpoints.cs`, `ArrowFlightSqlServer` (4 files, 505); keep `ArrowExportEndpoints.cs` (Arrow IPC as a REST format). `Application/FinOps` (278) + 4 related files; `Application/Mesh` (542). `DevPortalEndpoints.cs` and `DevEndpoints.cs` are mapped only when `IHostEnvironment.IsDevelopment()`; DevPortal static assets excluded from publish (`Content Remove` with `Condition="'$(Configuration)'=='Release'"`), default DELETE of dev portal assets if no owner (D2).
- Deps: WP-SIMPL-04. LOC: about -1,800. Effort: M, 2 pd.
- Tests: `ReleaseSurfaceTests.Dev_endpoints_absent_outside_development`, `DeletedFeaturesArchitectureTests` entries.

#### WP-SIMPL-17 Merge duplicate folders
- Scope: `Application/Auditing` -> `Application/Audit`; `Application/DataCatalog` -> `Application/Catalog`; `Application/Dbt` (2,342) and `Application/OpenMetadata` (328) merged into `Extensions/Dbt` and `Extensions/OpenMetadata` (the module source of WP-SIMPL-28), removing duplicated code; namespaces follow folders (`namespace-assembly-violations.txt` must not grow).
- Deps: WP-SIMPL-12 (DataCatalog vendor deletions first). LOC: -500 to -1,000 (duplicates). Effort: M, 2.5 pd. Risk: wide namespace churn; one PR per pair.
- Tests: `ProgramRatchetTests.Application_top_level_folders_are_allow_listed` (allow-list shrinks), `NamespaceAssemblyConsistencyTests`.

#### WP-SIMPL-18 Borderline triage execution (D2)
- Scope: owner names module owners before Stage 1 starts; ownerless items are deleted here: Analytics (governed aggregation; module candidate `governed-aggregates` together with differential privacy, Q8), Procedures (module candidate, SQL Server only, Q7), SchemaRegistry (887 + 5 endpoints), Docs ingestion (744 + `DocIngestionEndpoints` 9; `TrustedDocumentAdminEndpoints` stays if it serves GraphQL trusted documents, verify), differential privacy. Owned items are marked MODULE and extracted in Stage 3.
- Additional borderline rows (recommended defaults; owner may move an item before Stage 1):


| Item | Default | Reason | Issues |
|---|---|---|---|
| Analytics (governed aggregation) + differential privacy | MODULE `governed-aggregates` if owner, else DELETE | C1 adjacent, no evidence of use | drives #467, #468 |
| CDN cache purge | DELETE | C1 false; outbound HTTP (C2) | #474 closed by this WP (R1) |
| Table cache (DuckDB-backed) | MODULE with Olap (WP-SIMPL-26); core Caching stays | DuckDB leaves core | #475 fixed inside WP-SIMPL-26 or closed by removal |
| Trino client protocol route `/v1/statement` | DELETE (fourth SQL transport, D1); MODULE only with an owner and a client interop test | one SQL entry per transport | #470, #484 |
| `FederatedDuckDbExecutionService` (744) + `CrossSourcePlanner` (853) in Application/Sql | MODULE `federation-duckdb` with Olap; core `VirtualFilterFederationPlanner` and `SubtreeCompiler` reject cross-source plans fail-closed when the module is absent (seam + test `CrossSourceQuery_without_module_is_denied`) | DuckDB leaves core; RLS must not silently degrade | Phase 3 to confirm the seam |
| Catalog semantic search/embeddings (`Application/Catalog/Search`, `LocalOnnxEmbeddingGenerator`) | DELETE (MODULE only with owner) | C1 false; native ONNX weight (C3) | #501, #488 closed by removal if deleted |

- Deps: owner decision. LOC: 0 to -14,000. Effort: S-L, 1-6 pd.
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
- Scope: parity reference engine becomes PostgreSQL (`ParityPostgresHostFixture`); SQL Server cells switched from Pending to executed (`ParitySqlServerHostFixture`); SQLite cells kept until both are green; DuckDB parity moves to the Olap module tests. `parity-nightly.yml`: SQL Server job added, `parity-sqlite` removed in WP-SIMPL-31. Gate: `DialectParityGateTests.Every_generator_has_a_parity_baseline_file`. Stryker configs merged to 2 (`stryker-config.security-guards.json` folded into `stryker-config.json`, Q13).
- Deps: WP-SIMPL-13. LOC: tests +1,500 / -2,000. Effort: L, 5 pd. Risk: SQL Server cells reveal generator bugs (fix in the same lane; never leave Pending).
- AC/target: PG + SS 100% parity, none Pending; open master-plan baselines closed (DuckDB N/A for core).

#### WP-SIMPL-22 Real-database tests out of Unit (OD-2 step 2b; REQ-SIMPL-08.2)
- Scope: 83 Unit files using real SQLite and the DuckDB files: (i) behaviour that does not depend on DB semantics -> in-memory fakes/stores; (ii) DB semantics -> `Autheris.Tests.Integration` on the shared PostgreSQL fixture; (iii) Olap/DuckDB -> Olap module tests. 11 TestServer/WebApplication.Create files -> Integration. Same for SqlCompilerGate (1), Simulation (4), Sim.ApiHost (2), TrinoSqlEngine.Tests (16, engine semantics via PostgreSQL fixture or in-memory row source).
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
- Scope: `src/Autheris.Modules.Abstractions`, `Autheris.Api/Hosting/ModuleLoader.cs`, `ModuleAssemblyLoadContext.cs`, `Infrastructure/Modules/Trust/*` (extracted from `PluginManager`), embedded release public key, manifest signing tool `tools/Autheris.ModuleSigner` used by release.yml (private key from a GitHub secret), `Dockerfile*` `FLAVOR` argument, docker-publish.yml publishes `autheris` and `autheris-full`. Remove `Autheris.Api.csproj:8` Extensions reference; `AddGatewayExtensions` callers become module registrations.
- Deps: Stage 2 exit (stable abstractions). LOC: +900 src. Effort: L, 7 pd.
- Tests: `ModuleLoaderTests.Unsigned_manifest_fails_startup`, `.Hash_mismatch_fails_startup`, `.Unlisted_file_fails_startup`, `.Version_mismatch_fails_startup`, `.Enabled_but_missing_fails_startup`, `.Host_assembly_collision_fails_startup`, `.Not_enabled_module_is_not_loaded`, `.Unsigned_allowed_only_in_development_with_flag`; `ModuleEndpointTests.Module_routes_require_authorization`; `ModuleConfigurationTests.Keys_of_missing_module_fail_fast`; architecture: `ModuleBoundaryArchitectureTests.Modules_reference_only_abstractions_domain_and_application`, `.Modules_open_no_data_source_connections` (Cecil scan of module assemblies), `PackageBoundaryArchitectureTests.Api_does_not_reference_Extensions`.
- AC: core image starts without any module; full image loads all first-party modules; fallback switch documented.

#### WP-SIMPL-26 Pilot module: Olap
- Scope: `modules/Autheris.Modules.Olap` from `Application/Olap` (3,342), `DuckDbOlapEndpoints`, DuckDb part of `AnalyticalDialectGenerators.cs`, package `DuckDB.NET.Data.Full` (leaves core), `OlapEntryPointArchitectureTests` moved, DuckDB parity into the module tests. Pilot validates DI, endpoints, options, GraphQL (if any) and native library loading through the module ALC.
- Deps: WP-SIMPL-25. LOC: -3,600 src (+3,600 modules). Effort: M, 3 pd. Exit decision: runtime loading confirmed or fallback (AD-3.10) taken for all modules.

#### WP-SIMPL-27 Module wave A: OData, Lakehouse
- OData: `Extensions/OData` (1,927) + `Application/OData` (768) + `ODataEndpoints` (21 Map*). Lakehouse: `Extensions/Lakehouse` (3,680) + `IcebergRestCatalogEndpoints` (4), package `Parquet.Net` leaves core.
- Deps: WP-SIMPL-26. LOC: -7,300 src. Effort: M, 4 pd. Endpoints: -25.

#### WP-SIMPL-28 Module wave B: catalog sync (dbt, OpenMetadata)
- `modules/Autheris.Modules.Dbt` (Extensions/Dbt 5,692 incl. merged Application copy, `DbtEndpoints` 13; `YamlDotNet` moves if only dbt uses it - verify), `modules/Autheris.Modules.OpenMetadata` (1,838 + adapter). Catalog import API in core stays the integration point.
- Deps: WP-SIMPL-26, WP-SIMPL-17. LOC: -8,000 src. Effort: M, 4 pd. Endpoints: -13.

#### WP-SIMPL-29 Module wave C: events delivery (CDC/Streaming + Webhooks) and Lineage
- `modules/Autheris.Modules.Events`: Application/Streaming (4,440), Extensions/Cdc (2,296), `CdcStreamSqlCore` + CDC partials (stays in governance repository if the tables are core-owned; default: module owns its store via `IModuleHostContext` governance connection factory, Q5), Webhooks (1,157), `WebhookEndpoints`, `StreamingCdcEndpoints`, `CdcStreamAdminEndpoints`, `TableStreamEndpoints`. `modules/Autheris.Modules.Lineage`: Application/Lineage + Extensions/Lineage.
- Deps: WP-SIMPL-26, WP-SIMPL-20. LOC: -11,000 src. Effort: L, 6 pd. Endpoints: -16. Risk: SSRF guard for webhooks must move intact (Phase 3 re-check).

#### WP-SIMPL-30 Module wave D: identity and edge modules
- SCIM (`Scim*Endpoints` 15 Map*), TOTP (local-auth), Negotiate (package `Microsoft.AspNetCore.Authentication.Negotiate` leaves Api), Envoy ext-authz (`EnvoyExtAuthzEndpoints` 3), plus owned borderline modules from WP-SIMPL-18 (Procedures, governed-aggregates).
- Deps: WP-SIMPL-26. LOC: -5,000 to -15,000 src. Effort: L, 5 pd. Risk: authentication handler registration order (module `ConfigureServices` runs before `AddAuthentication` finalisation; test `Negotiate_module_registers_scheme`).

#### WP-SIMPL-31 Full SQLite removal step 3 (OD-2; owner may stop before)
- Delete `SqliteDialectGenerator.cs` (433), `Ast/Security/SqliteTypeAffinity.cs` (40), `CatalogTypeMap.cs` SQLite branch, `Application/Data/Services/SqliteZoneOffsetTable.cs` (135), `Persistence/Catalog/SqliteColumnMetadataReader.cs` + `SqliteTypeAffinity.cs`, `GatewayStorageServiceExtensions.cs:130`, the 28 `TargetSqlDialect.Sqlite`/provider lines, Emit/Application/Sql/middleware uses, package `Microsoft.Data.Sqlite` from Infrastructure and 4 test projects; SQLite parity cells and `parity-sqlite` job `[WF]`.
- Deps: WP-SIMPL-21 and WP-SIMPL-22 green on PostgreSQL. LOC: about -1,000 src. Effort: M, 3 pd.
- Tests: `PackageBoundaryArchitectureTests.No_Microsoft_Data_Sqlite_reference`, `TargetSqlDialectTests.Only_supported_values_exist` (PG, SS).
- AC/target: SQLite references in src 0; real-SQLite test files 0.

#### WP-SIMPL-32 Configuration surface reduction (REQ-SIMPL-06.4)
- Scope: after deletions and module moves, merge small option classes per subsystem, remove `*Enabled` flags of always-on KEEP features (the feature is on; disabling a security control is not configurable), move module options into modules. Uses the WP-SIMPL-06 reference generator; ratchets from WP-SIMPL-07 lowered to targets.
- Deps: WP-SIMPL-26..30. Effort: M, 4 pd. Risk: removing an `Enabled` flag that guarded a fail-closed default (Phase 3 lists flags that must stay).
- AC/target: Options <= 90, props <= 600, `*Enabled` <= 40, undocumented 0.

#### WP-SIMPL-33 Plan-id comment cleanup (REQ-SIMPL-02d)
- Scope: scripted removal per project (`tools/strip-plan-ids.py`, reviewed by diff statistics: comment lines only, no code tokens changed - verified by comparing Roslyn syntax trees without trivia), then `ProgramRatchetTests.Plan_id_comments_do_not_grow` baseline set to 0; residual cleanups in the governance repository to reach <= 6,000.
- Deps: WP-SIMPL-26..32 (do not clean code twice). LOC: about -1,900. Effort: M, 2 pd.

#### WP-SIMPL-34 README rewrite, feature docs, migration guide completion
- Scope: README per REQ-SIMPL-11.5 (positioning, core capabilities, 3 transports, modules with install lines, honest status, no numbers without results link); feature docs <= 45 (DELETE items lose docs, MODULE docs move to module READMEs); `docs/operations/migration-simplification.md` completeness check; architecture docs updated to the module model.
- Deps: WP-SIMPL-33. Effort: M, 3 pd. AC: owner accepts README (freeze exit criterion).

#### WP-SIMPL-35 Footprint budget job and published numbers `[WF]`
- Scope: CI job (nightly + on release) running `benchmarks/scripts/footprint.sh` and `ci-duration.sh`, comparing with `benchmarks/budgets.json` (core image 250 MB, cold P50 3.0 s, warm P99 3.47 ms, PR 15 min, full 30 min), failing at +10%; results uploaded as artifact and committed per release.
- Deps: WP-SIMPL-10, WP-SIMPL-26..30. Effort: S-M, 2 pd.

#### WP-SIMPL-36 Security lane (freeze exception; parallel to Stage 0)
- Scope: issues #505-#508 and the FIX-IN-CORE advisories listed in the "Open issues and advisories assessment" section (PM-owned). One bundled change for the fail-closed audit fixes of #505 and #507: every security-relevant audit write on the affected paths uses `IAuditWriter.RecordRequiredAsync` (fails the request when the audit write fails) instead of best-effort recording, plus an architecture test `RequiredAuditArchitectureTests.Security_decision_paths_use_RecordRequiredAsync` (Cecil scan of the affected endpoint/mutation types; baseline starts at the fixed state). Remaining issues/advisories: one PR each, only for KEEP code.
- Deps: none; runs under the security exception of the freeze (PRD 5.1); first item: merge `fix/security-batch-5`. Effort: L, 8-12 pd (batch 5 review and merge, audit bundle, DML bundle per OD-3, about 20 further FIX-IN-CORE items). Risk: overlaps with deletion WPs (rule R1 in section 7).
- AC: #505-#508 closed or explicitly re-classified OBSOLETE-BY-REMOVAL with the deleting WP named.

Total effort: about 131-153 pd (security lane WP-SIMPL-36 about 8-12 included; Stage 0 about 15, Stage 1 about 25-30, Stage 2 about 36, Stage 3 about 45-60).

## 5. Architecture view: KEEP / MODULE / DELETE

| Area | Class | Projects / folders | Packages | WP |
|---|---|---|---|---|
| Consent, ConsentRequests, FunctionalRights, SubjectLifecycle | KEEP | Application/Governance (Consent*), ConsentRequests | - | - |
| RLS, VirtualFilters, Policy | KEEP | Application/VirtualFilters, Policy | - | - |
| Masking, Federation, subgraph masking | KEEP | Application/Federation, GraphQL/Federation | - | - |
| Audit | KEEP (merged) | Application/Audit (+Auditing), Infrastructure audit stores | - | 17 |
| RBAC, ReBAC (in-memory), Identity, Security, TokenRevocation, Tenancy | KEEP | Application/Identity, Security, Tenancy | JwtBearer | - |
| SQL compiler, gate | KEEP (scope reduced) | src/TrinoSqlEngine, Application/Sql (+SqlEndpoints), tests SqlCompilerGate, Fuzz | Antlr4.Runtime | 13, 21, 31 |
| Catalog + classification (internal) | KEEP (merged) | Application/Catalog (+DataCatalog) | - | 17 |
| Workflows, recertification, HITL | KEEP | Application/Governance, Mcp HITL | - | 24 |
| Transports REST, GraphQL, MCP, Arrow IPC export | KEEP | Api/Endpoints, Autheris.GraphQL, Application/Mcp | HotChocolate, ModelContextProtocol, Apache.Arrow | - |
| Governance persistence (PG, SS) | KEEP, unified | Infrastructure/Persistence | Npgsql, Microsoft.Data.SqlClient | 11, 19, 20 |
| Observability, health, jobs, state/caching | KEEP (slim) | consolidated folders | OpenTelemetry, prometheus-net, StackExchange.Redis | 15, 24 |
| Plugins/Extensibility | KEEP (module host base) | Application/Plugins, Extensibility, Infrastructure/Plugins | - | 25 |
| Olap | MODULE (pilot) | modules/Autheris.Modules.Olap | DuckDB.NET.Data.Full | 26 |
| OData | MODULE | modules/Autheris.Modules.OData | - | 27 |
| Lakehouse (Delta/Iceberg) | MODULE | modules/Autheris.Modules.Lakehouse | Parquet.Net | 27 |
| dbt | MODULE | modules/Autheris.Modules.Dbt | YamlDotNet (verify) | 28 |
| OpenMetadata | MODULE | modules/Autheris.Modules.OpenMetadata | - | 28 |
| CDC/Streaming + Webhooks | MODULE | modules/Autheris.Modules.Events | - | 29 |
| Lineage | MODULE | modules/Autheris.Modules.Lineage | - | 29 |
| SCIM, TOTP, Negotiate, Envoy ext-authz | MODULE | modules/Autheris.Modules.{Scim,LocalAuth,Negotiate,EnvoyAuthz} | Authentication.Negotiate | 30 |
| Procedures, Analytics + differential privacy, SchemaRegistry, Docs ingestion | MODULE if owner named, else DELETE | modules/... or deleted | - | 18, 30 |
| Purview, Collibra, Alation, DataHub | DELETE | Extensions/DataCatalog vendor clients | - | 12 |
| Backstage, ITSM (ServiceNow/Jira) | DELETE | Extensions/Backstage, Itsm; Application/Integrations | - | 12 |
| Oracle, Databricks, Snowflake, Ansi-target dialects | DELETE | TrinoSqlEngine generators, Infrastructure Oracle support | Oracle.ManagedDataAccess.Core, Testcontainers.Oracle | 13 |
| Casbin | DELETE | Casbin engine/options | Casbin.NET | 14 |
| Embedded Garnet, traffic shadowing | DELETE | Infrastructure/Garnet, shadowing middleware | Microsoft.Garnet | 15 |
| Arrow Flight SQL over HTTP, FinOps, Mesh, dev portal in release | DELETE | Api/Endpoints/ArrowFlightSql*, Application/FinOps, Mesh | - | 16 |
| SQLite governance store | DELETE (OD-1) | Persistence Sqlite* governance, Steps/sqlite | - | 11 |
| SQLite data-source dialect | DELETE last (OD-2) | SqliteDialectGenerator, affinity, zone table | Microsoft.Data.Sqlite | 31 |

## 6. User and operator communication per stage

| Stage | Artifacts |
|---|---|
| 0 | CHANGELOG created with backfill; release notes from CHANGELOG; README without numbers; `pre-simplification` tag; freeze announced in README "Project status" (system description, no progress tags) and in issue #515 |
| 1 | Release N (next minor, `### BREAKING`): SQLite governance store removed (OD-1), default provider PostgreSQL, deleted integrations and dialects; `docs/operations/migration-simplification.md` created with one row per DELETE item (feature, removed keys, replacement, last tag containing it); fail-fast messages point to the guide; one GitHub issue per deleted integration with restoration path |
| 2 | Release N+1: no user-visible API change expected; CHANGELOG notes governance repository unification and parity engines; orphaned tables dropped by forward-only steps (CHANGELOG "Changed" with table names) |
| 3 | Release N+2 (`### BREAKING`, `### Moved to modules`): `autheris` vs `autheris-full` images, `Gateway:Modules:Enabled`, module install lines; OD-2 step 3 removal; rewritten README; deprecation policy for the post-production future stated in README |

## 7. Roadmap, lanes and merge order

Roadmap rules:
- R1: no fixes for code that a later WP deletes; OBSOLETE-BY-REMOVAL issues and advisories are closed by the deleting WP (its PR names them).
- R2: the CI hang fix (#516) is merged first in Stage 0, before any other WP.
- R3: the security lane (WP-SIMPL-36) runs parallel to Stage 0 under the freeze security exception and has merge priority over deletions in the files it touches.
- R4: #504 (Security trait on tests, coverage map) merges before any deletion PR (WP-SIMPL-11..18), so deletions can prove no KEEP security test is lost.
- R5: #503 plugin-loader fix (PluginManager is the module host base) merges before WP-SIMPL-25.
- R6: fixes in code that moves to a module (dbt GHSA item, #476 CDC, #488 webhooks, #490 Lakehouse limit, #503 OpenLineage/OLAP parts) land before or inside the extraction WP (26-30), never after.
- R7: folded into deletion PRs: the Flight half of #487 (WP-SIMPL-16), most of #491 and the OpenJev part of #503 (the deleting WP named in the assessment section).
- R8: Oracle deletion (WP-SIMPL-13) precedes any work on #479/#483.
- R9: #520 (test host reuse) lands before or with WP-SIMPL-11; #516 and #517 go first in Stage 0.

Lanes (one worktree each; one build at a time per disk budget):
- **L-SEC** (security, freeze exception): 36, parallel to Stage 0.
- **L-DOC** (docs/process): 03 -> 04 -> 05; 08, 09 parallel.
- **L-CFG**: 06 -> (Stage 3) 32.
- **L-RAT**: 07, 10 (10 before and after 01).
- **L-REL**: 01, 02.
- **L-PER** (persistence, **serial, owns all governance DDL**): 11 -> 12 -> 19 -> 20a -> 20b -> 20c -> 20d -> 20e -> 29 (store part).
- **L-ENG** (engine): 13 -> 21 -> 22 -> 31.
- **L-DEL** (deletions without DDL): 14, 15, 16 parallel after 06; 17 after 12; 18 after owner decision.
- **L-TST**: 23 per domain after the Stage 1 deletions of that domain.
- **L-MOD**: 25 -> 26 -> {27, 28, 29, 30} (27/28/30 parallel; 29 after 20) -> 32 -> 33 -> 34; 35 after 26.

Merge order:
1. Stage 0: #516 CI hang fix and #517 first (R2, R9); #504 before Stage 1 (R4); then 01, 02, 03, 07, 10(before) and 36 in parallel; then 04, 06, 08, 09; then 05, 10(after). Exit: Stage 0 ACs, freeze active.
2. Stage 1: 11 first (default provider change affects every later test fixture); 13, 14, 15, 16 in parallel with 11 but merged after it (rebase on DI files); 12 after 11; 17 after 12; 18. Exit: section 3 Stage 1 column.
3. Stage 2: 19 -> 20a..20e (serial); 21 -> 22 in parallel with L-PER; 23 and 24 after Stage 1 exit. Exit: Stage 2 column; owner stop point for OD-2 (after 22).
4. Stage 3: 25 (Phase 3 review) -> 26 (go/no-go for runtime loading) -> 27, 28, 30 -> 29 -> 31 -> 32 -> 33 -> 34 -> 35. Exit: program targets; freeze lifted.

DDL rule: only L-PER changes governance DDL (WP-11 removes the SQLite tree, WP-20e adds one forward-only step per engine, WP-29 only if the events module needs a store change). The v1.5.0 frozen base is never edited.

## Open issues and advisories assessment

Assessment date 2026-11-05 (PM consolidation of seven triage slices). Scope: all 51 open GitHub issues and the draft security advisories. **Disclosure rule (SEC-SIMPL-01):** advisories are private drafts in a public repository; this plan names them only by GHSA id and the affected component. Details stay in the advisory.

Verdicts: **BLOCKER** (stops other work), **FIX-IN-CORE** (code stays; fix it), **QUICK-WIN** (small, independent), **OBSOLETE-BY-REMOVAL** (closed by the deleting WP; no fix work), **PARTLY-OBSOLETE** (the part in deleted code is closed by the deleting WP; the rest is fixed), **DUPLICATE**, **NEEDS-DECISION** (depends on an owner decision below). Priorities P0 (now) to P3 (backlog). "Security lane" means WP-SIMPL-36 (section 4), which runs parallel to Stage 0 under the freeze exception; its first item is merging `fix/security-batch-5`.

### Sequencing rules derived from the assessment (binding for the roadmap)

1. **CI first.** #516 (hang regression, BLOCKER) and #517 (job timeouts, hang dumps) land before any WP-SIMPL work, including the CI baseline measurement (WP-SIMPL-10). Measurements taken while CI hangs are invalid.
2. **Security lane (WP-SIMPL-36) parallel to Stage 0** (freeze exception): first `fix/security-batch-5` (committed, reviewed, merged before WP-SIMPL-12/18/20/28/29/30 touch the same files), then #469 (P0), #505-#508 and the FIX-IN-CORE advisories below. The fail-closed audit fixes of #505 and #507 are one bundle (`RecordRequiredAsync` plus an architecture test that forbids fire-and-forget audit on write and admin paths).
3. **No fixes in code that a later WP deletes.** OBSOLETE-BY-REMOVAL items and the obsolete parts of PARTLY-OBSOLETE items are closed by the deleting WP, which links the issue in its PR. Examples: Flight half of #487, most of #491, OpenJev part of #503 and the Arrow part of #485 go into the deletion PRs (WP-SIMPL-12..16); the Oracle parts of #479, #483, #521 and #522 go with WP-SIMPL-13, which runs before any work on the remaining parts of #479/#483.
4. **Security test inventory before deletions.** #504 (Security trait on the #495-#497 tests, missing tests added) merges before the first deletion PR, so every deletion PR can prove with the trait filter that no security test was removed (SEC-SIMPL-03). Branch `fix/security-batch-5` already adds tagged tests; it merges first.
5. **Module prerequisites.** #503 plugin-loader collision fix precedes module extraction (WP-SIMPL-25+, `PluginManager` becomes the module host base). Fixes in code that moves to a module land before or inside the extraction WP: GHSA-jw88-9j35-3c96 and #459 before the dbt module (WP-SIMPL-28), #476 and #475 with the CDC/Events module (WP-SIMPL-29), #488 webhook items with OpenMetadata/dbt (WP-SIMPL-28), the Lakehouse part of #490 and OpenLineage/OLAP parts of #503 with their modules.
6. **Test host reuse before the SQLite governance removal.** 70 of the 72 integration-test files that start a host use in-memory SQLite governance. #520 (reuse hosts instead of ~650 per-test host starts) is a hard dependency of WP-SIMPL-11; WP-SIMPL-11 uses one shared PostgreSQL container per test run with a per-test-class database cloned from a migrated template database, so migrations run once.
7. **Oracle before Oracle-adjacent fixes.** Before the Oracle code is deleted (WP-SIMPL-13), the Oracle-only plaintext-password check is generalised to PostgreSQL and SQL Server connection strings (part of the #507 bundle), so the control is not lost with the dialect.
8. **CI workflow bundles** (workflow edits need owner approval, `[WF]`): #517 + #518 in one PR; #519 + the remaining part of #521 in a second; both in a fixed merge order with WP-SIMPL-13, -21 and -35. Deletions alone cut only about 10-15 % of integration-test time, so #519 (with a shard-weight regeneration script, rerun after each deletion WP) and #520 stay necessary.
9. **Options cleanup.** #477 merges into #514 / WP-SIMPL-06, except the pre-filled security allowlists, which are fixed in the security lane (empty by default, fail closed).

### Issues (51)

| Issue | Title | Verdict | Prio | Work package / sequencing |
|---|---|---|---|---|
| #459 | Declared keys/indexes only; read `meta.indexes` from dbt | FIX-IN-CORE | P2 | After WP-SIMPL-17 (Dbt folder merge), before WP-SIMPL-28 (dbt module) |
| #467 | Differential privacy: noise, budget, ordering | NEEDS-DECISION | P1 if kept | D-ANALYTICS (WP-SIMPL-18): obsolete if Analytics/DP is deleted, otherwise in the module extraction (WP-SIMPL-30) |
| #468 | Noisy aggregate cache hit without audit entry | NEEDS-DECISION | P1 if kept | Bundle with #467 |
| #469 | `Sid.GetUserSid` client-id fallback; static `RealmLookup` | FIX-IN-CORE | **P0** | Security lane |
| #470 | WebSQL `wait_timeout` requests treated as Trino transport | FIX-IN-CORE | P1 | Security lane; `/v1/statement` stays core (see WP-SIMPL-18 row) |
| #471 | MCP JSON-RPC batch guard inspects only 512 bytes | QUICK-WIN | P1 | Security lane (MCP is core) |
| #472 | `/health/ready` anonymous, uncached, not rate limited | QUICK-WIN | P2 | Security lane; SQLite lock contention part closed by WP-SIMPL-11 |
| #473 | Audit batch lost on shutdown; exact-match redaction | PARTLY-OBSOLETE | P1 | Split: redaction now (security lane); shutdown batch loss in the unified repository (WP-SIMPL-20) |
| #474 | CDN purge reports success without provider | QUICK-WIN | P2 | Depends on the CDN row in WP-SIMPL-18; fix only if kept |
| #475 | Table cache applies CDC mutations as string literals | NEEDS-DECISION | P2 | Table cache row in WP-SIMPL-18 (DuckDB leaves core with WP-SIMPL-26); with WP-SIMPL-29 if kept |
| #476 | CDC custom query poller read-only only on SQLite/PostgreSQL | FIX-IN-CORE | P1 | Before or inside WP-SIMPL-29 (Events module) |
| #477 | Options with no effect; pre-filled security allowlists | PARTLY-OBSOLETE | P2 | Unconsumed options: WP-SIMPL-06; allowlists: security lane |
| #478 | Flat AND/OR chains hit AST depth limit | FIX-IN-CORE | P2 | SQL engine lane, bundled with #481 |
| #479 | Dialect semantics differ from Trino | PARTLY-OBSOLETE | P2 | Oracle parts closed by WP-SIMPL-13; rest after it (SQL engine lane) |
| #480 | Data source resolver defaults and swallowed errors | FIX-IN-CORE | P1 | Security lane; bundle with #486 |
| #481 | SQL function policy denylist-only; unbounded padding | FIX-IN-CORE | P1 | SQL engine lane with #478 |
| #482 | ForwardAuth tenant-prefixed roles; unregistered IP allowlist middleware | FIX-IN-CORE | P1 | Security lane; delete the dead middleware rather than wire it |
| #483 | Transport TLS and supply chain | PARTLY-OBSOLETE | P1 | Oracle part closed by WP-SIMPL-13; PostgreSQL/SQL Server TLS with WP-SIMPL-11 (SEC-SIMPL-14) |
| #484 | Read-only tokens and `/v1/statement` / cancel | QUICK-WIN | P2 | Security lane |
| #485 | Hardening backlog (streaming errors, validation, caches) | PARTLY-OBSOLETE | P3 | Arrow part closed by WP-SIMPL-16; ITSM webhook handler part closed by WP-SIMPL-12 (OD-5); dbt part with WP-SIMPL-28; rest P3 |
| #486 | Required filter column satisfied by any reference | FIX-IN-CORE | P2 | Security lane with #480 |
| #487 | Flight ticket key, keyset cursor key, cursor SID | PARTLY-OBSOLETE | P1 | Flight part closed by WP-SIMPL-16; cursor part in security lane |
| #488 | Webhook replay, buffering, embeddings client, egress | PARTLY-OBSOLETE | P1 | Egress and embeddings parts FIX-IN-CORE in security lane (OD-4); webhook parts with WP-SIMPL-28 |
| #489 | Caller labels, revocation, CSRF, ForwardAuth SID | FIX-IN-CORE | P1 | Security lane |
| #490 | Download re-authorisation; Lakehouse LIMIT ordering | PARTLY-OBSOLETE | P1 | Re-authorisation in security lane; Lakehouse part with its module extraction |
| #491 | Unwired code paths and kill switch | FIX-IN-CORE | P1 | Mostly deletions: folded into WP-SIMPL-12..16 |
| #492 | Query analysis against another read-only scope | NEEDS-DECISION | P2 | Freeze rule: no new features; default taken: defer until Stage 3 exit |
| #500 | Catalog text untrusted before MCP/LLM clients | FIX-IN-CORE | P1 | Security lane (MCP and catalog are core) |
| #501 | Catalog search index race, staleness, rate limit | FIX-IN-CORE | P2 | Security lane (OD-4: catalog search is core) |
| #502 | Set-based nested lists: cost window; SQLite version | PARTLY-OBSOLETE | P2 | Cost part SQL engine lane; SQLite part closed by WP-SIMPL-31 |
| #503 | Plugin collisions, OpenLineage, OpenJev, OLAP default | PARTLY-OBSOLETE | P1 | Plugin fix before WP-SIMPL-25; OpenJev in deletion PRs; OpenLineage/OLAP with modules |
| #504 | Tag security tests from #495-#497 | QUICK-WIN | P1 | Before the first deletion PR (rule 4) |
| #505 | DML hardening | FIX-IN-CORE | P1 | Security lane (OD-3: DML stays core): audit bundle with #507, then the DML bundle with the DML advisories; no Casbin dependency |
| #506 | Cache/header hardening | FIX-IN-CORE | P2 | Global `no-store`/`Vary` default now; OData part with its module |
| #507 | Admin plane hardening | FIX-IN-CORE | P1 | Security lane audit bundle with #505; password check generalised before WP-SIMPL-13; break-glass header part OBSOLETE-BY-REMOVAL (OD-5, WP-SIMPL-12) |
| #508 | MCP hardening | FIX-IN-CORE | P1 | Security lane |
| #509 | Remove ReadyToRun | QUICK-WIN | P2 | WP-SIMPL-01 (before the "after" run of WP-SIMPL-10) |
| #510 | Version from git tag; Helm default; CHANGELOG | FIX-IN-CORE | P1 | WP-SIMPL-02 `[WF]` |
| #511 | Remove unverified performance claims | QUICK-WIN | P2 | WP-SIMPL-03 (includes README.md:25), then WP-SIMPL-09 |
| #512 | Docs describe non-existent components | PARTLY-OBSOLETE | P3 | WP-SIMPL-04; Flight claims vanish with WP-SIMPL-16 |
| #513 | Docs hygiene | FIX-IN-CORE | P3 | WP-SIMPL-05 |
| #514 | Delete unconsumed options; config reference | FIX-IN-CORE | P2 | WP-SIMPL-06 (absorbs #477) |
| #515 | Structural simplification umbrella | NEEDS-DECISION | P1 | This plan, Stages 1-3; one child issue per stage/module |
| #516 | CI hang regression since #464 | **BLOCKER** | **P0** | First, before every WP (rule 1) |
| #517 | Job timeouts and hang dumps | QUICK-WIN | **P0** | With or just before #516; `[WF]` bundle with #518 |
| #518 | Coalesce main runs, path filters, concurrency | QUICK-WIN | P1 | `[WF]` bundle with #517 |
| #519 | Rebalance shards from TRX data | QUICK-WIN | P1 | Now; weight regeneration rerun after each deletion WP |
| #520 | Reuse test hosts | FIX-IN-CORE | P1 | Hard dependency of WP-SIMPL-11 and WP-SIMPL-22 (rule 6) |
| #521 | Pre-pull Testcontainers images | PARTLY-OBSOLETE | P2 | Oracle part closed by WP-SIMPL-13; rest bundled with #519 |
| #522 | Oracle/Stress/Spark suites to nightly | PARTLY-OBSOLETE | P2 | Oracle part closed by WP-SIMPL-13; rest after #519 |
| #523 | CI under 30 min (umbrella) | NEEDS-DECISION | P1 | Umbrella for #516-#522; target definition per D-CIQUEUE |

### Draft security advisories (29)

**Security-lane item 0:** all 14 advisories of the second triage slice are addressed, fully or in part, by the in-progress branch `fix/security-batch-5` (uncommitted work in the main checkout). It is committed, reviewed (Phase 5) and merged **first**, before WP-SIMPL-12, -18, -20, -28, -29 or -30 touch the same files. Residuals after batch 5 are listed below. None of the 29 advisories is specific to the SQLite governance store.

| Advisory | Component (neutral title) | Verdict | Prio | Work package / sequencing |
|---|---|---|---|---|
| GHSA-7ccv-hfgq-5qwm | Federation column access checks | FIX-IN-CORE | **P0** | Security lane |
| GHSA-7pjw-hcrc-hvff | Federation masking catalog | FIX-IN-CORE | **P0** | Security lane |
| GHSA-v5c2-29pv-cc86 | Data source registration | FIX-IN-CORE | **P0** | Security lane, before WP-SIMPL-17 (Catalog folder merge) |
| GHSA-9229-wx6j-wvm9 | Admin catalog operations | PARTLY-OBSOLETE | **P0** | Security lane; Backstage part closed by WP-SIMPL-12 |
| GHSA-qx32-576x-5cq2 | GDPR disclosure report | FIX-IN-CORE | **P0** | Batch 5; placement per D-GDPR, before WP-SIMPL-29 |
| GHSA-jcf8-hchm-55h3 | WebSQL DML authorization | FIX-IN-CORE | P1 | Security lane (OD-3: DML stays core); fix on the core grant path, not Casbin |
| GHSA-3prq-qxg7-92qf | WebSQL DML scoping | FIX-IN-CORE | P1 | Security lane, DML bundle |
| GHSA-83g2-c2jr-c8vq | WebSQL DML write scope | FIX-IN-CORE | P1 | Security lane, DML bundle |
| GHSA-v75c-28mx-7wm7 | WebSQL DML statement tickets | FIX-IN-CORE | P2 | Security lane, DML bundle (OD-3); any Flight-only part closed by WP-SIMPL-16 |
| GHSA-66xj-4459-x8gx | Row-filter evaluation | FIX-IN-CORE | P1 | Security lane (SQL engine); per-dialect proof on PostgreSQL and SQL Server only |
| GHSA-jw88-9j35-3c96 | dbt semantic manifest import | FIX-IN-CORE | P1 | Before WP-SIMPL-28 (dbt module) |
| GHSA-547h-29x4-m4ch | dbt privacy metadata | FIX-IN-CORE | P1 | Batch 5; before WP-SIMPL-28 |
| GHSA-92vp-663x-pp52 | Transport gate | FIX-IN-CORE | P1 | Security lane; module parts before WP-SIMPL-29 and the Lakehouse extraction |
| GHSA-wg8p-wfw5-qvgj | HITL step-up | QUICK-WIN | P1 | Batch 5 |
| GHSA-hw85-rqjh-cwxx | Client allowlist for non-Entra realms | FIX-IN-CORE | P1 | Batch 5; before WP-SIMPL-30 |
| GHSA-jcpx-6m27-2m97 | Uniform error responses | PARTLY-OBSOLETE | P1 | Batch 5; Analytics part per D-ANALYTICS |
| GHSA-mgv5-4r69-mwww | Admin scoping model | FIX-IN-CORE | P1 | Batch 5; model confirmation per D-ADMIN; before WP-SIMPL-28/29 |
| GHSA-cj7m-c75j-6p6h | Async job quotas and GraphQL batching | FIX-IN-CORE | P1 | Batch 5 |
| GHSA-wvwx-q6jg-44wv | CDC webhook lifecycle | FIX-IN-CORE | P1 | Batch 5 plus an M follow-up (residual) in the security lane, before WP-SIMPL-29 |
| GHSA-78j7-3gh6-7cqx | ReBAC object ids and defaults | NEEDS-DECISION | P1 | Part 1 in batch 5; part 2 per D-REBAC |
| GHSA-4qx8-fv2p-q8r2 | Audit archive export and anchors | NEEDS-DECISION | P1 | Anchor part in batch 5; export per D-ARCHIVE, before or with WP-SIMPL-20 |
| GHSA-335j-6vwc-7p4c | Access profile revocation | QUICK-WIN | P1 | Batch 5 |
| GHSA-x8g9-cm3h-p6jq | Authorization cache invalidation | FIX-IN-CORE | P2 | Security lane; coordinate with WP-SIMPL-15 (Garnet removal) |
| GHSA-v77c-jh96-7rgr | Persisted enum handling | QUICK-WIN | P2 | Security lane, before WP-SIMPL-20 (access-profile break-glass assignments stay; OD-5) |
| GHSA-rxr4-mf28-p2hv | Small-group suppression | NEEDS-DECISION | P2 | Per D-ANALYTICS |
| GHSA-jfrx-2mgc-pfh5 | External catalog sync | PARTLY-OBSOLETE | P2 | Batch 5; vendor part closed by WP-SIMPL-12; OpenMetadata part with WP-SIMPL-28 |
| GHSA-xpw9-4m63-69pf | Federation masking | FIX-IN-CORE | P2 | Batch 5 |
| GHSA-95f9-ffc7-vw2c | Catalog search | FIX-IN-CORE | P2 | Batch 5 (partial); residual in security lane (OD-4: catalog search is core) |
| GHSA-p234-g3ph-gqh2 | MCP document store | NEEDS-DECISION | P3 | Docs ingestion row in WP-SIMPL-18; default taken: keep in core with the MCP transport, fix in the security lane |

### Owner decisions from the assessment

Decided by the owner on 2026-11-05 (binding, recorded in the PRD section 2.0): **OD-3** WebSQL DML stays core; **OD-4** catalog search including embeddings is core; **OD-5** ITSM integration and the header-based break-glass/justification flow are deleted (access-profile break-glass assignments stay).

Still open (recommended default; work proceeds on the default until the owner decides):

| Id | Question | Recommendation | Effect |
|---|---|---|---|
| D-CIQUEUE | Does runner queue time count in the CI time target? | **Yes**, the #523 definition: wall-clock from push to final status. | Section 3 CI targets use that definition. |
| D-REBAC | GHSA-78j7-3gh6-7cqx part 2: ReBAC default enforcement | **Enforce by default (fail closed)** for tenants with ReBAC relations; explicit opt-out per tenant, audited. | Security lane, S/M. |
| D-ARCHIVE | GHSA-4qx8-fv2p-q8r2: audit archive export | **Delete the unwired archive export and gate purge** behind the anchor verification; rebuild export later only on demand. | Closes the export part by removal; anchor hardening stays in batch 5 and WP-SIMPL-20. |
| D-ADMIN | GHSA-mgv5-4r69-mwww: admin scoping model | **Confirm the batch-5 model** (a tenant-less governance admin is global only with the cluster-admin role; otherwise tenant-scoped). | No extra work if confirmed. |
| D-GDPR | GHSA-qx32-576x-5cq2: GDPR disclosure report placement | **Core** (consent and audit are core; the report must not depend on the optional Lineage module); lineage enrichment optional. | WP-SIMPL-29 moves only lineage graph code; the report stays in core. |
| D-ANALYTICS | Analytics and differential privacy (#467, #468, GHSA-rxr4-mf28-p2hv, part of GHSA-jcpx-6m27-2m97) | As in WP-SIMPL-18: **module only if a human owner is named by Stage 1 exit, otherwise delete.** | Obsolete if deleted; P1 in the module if kept. |

## 8. Open architecture questions (default taken)

| ID | Question | Default taken |
|---|---|---|
| Q1 | Where do modules live? | Same repo under `modules/`, same tag and version as the host |
| Q2 | Module release cadence | Same as host (deviation from PRD "own cadence"; single version source wins); revisit after first production release |
| Q3 | Third-party/custom modules | Not supported; only first-party signed modules; custom code uses the existing plugin interfaces |
| Q4 | Neutral-model generation of migration steps | No; hand-written per-engine steps plus step-parity test (deviation from REQ-SIMPL-05.3, justified by about 130 lines of steps) |
| Q5 | Orphaned tables and module-owned stores | One forward-only drop step per engine in WP-20e; the events module uses the governance connection factory, its schema stays in the core migration tree |
| Q6 | `TargetSqlDialect.Ansi` | Kept only as internal canonical form if used so; never selectable as a data-source target |
| Q7 | Procedures (ADR-018) | MODULE (SQL Server only, enforcement via SESSION_CONTEXT, outside the AST gate); delete if no owner by Stage 1 |
| Q8 | Analytics (governed aggregation) and differential privacy | One `governed-aggregates` module if an owner exists; otherwise delete |
| Q9 | DuckDB in tests | Only inside the Olap module tests |
| Q10 | Manifest signature algorithm | ECDSA P-256 from the BCL; private key in a GitHub secret; rotation by host release |
| Q11 | Versioning tool | No MinVer; release.yml passes `-p:Version` from the tag; local builds `0.0.0-dev` |
| Q12 | CHANGELOG generation | Hand-maintained `[Unreleased]` section; release notes extracted from it |
| Q13 | Stryker configs | Merge security-guards into the main config: 2 configs (main, governance) |
| Q14 | src LOC metric and exit target | `src/` only, modules counted separately; core exit target revised to <= 215,000 |
| Q15 | Docs checks on docs/plans | Links rewritten, not gated |
| Q16 | `McpOptions.RequirePiiMasking` | Deleted (masking unconditional); Phase 3 confirms |
| Q17 | `ScimOptions.AllowedIpRanges` | Deleted in Stage 0; SCIM module re-adds only with enforcement and tests |
| Q18 | Spark CI job | Removed with Databricks if it serves only Databricks/Spark (verify in WP-13) |
| Q19 | SQL Server as governance DB (D4) | Kept; revisit after Stage 2 with the AD-1 design making removal cheap |

## 9. Risks (architecture)

| Risk | Mitigation |
|---|---|
| Runtime module loading adds an untrusted-code path | Signed manifest, embedded key, explicit enable list, fail closed, Phase 3 review before WP-25 merges; compile-time fallback |
| Unified repository changes SQL semantics | Contract suite on both engines before and after each step; DEF-02 pinned |
| Parallel deletion PRs conflict in DI registration files | Merge order in section 7; small PRs; rebase before merge |
| Stage targets missed | Measured per stage by checked-in scripts; revisions only with data (max 20%) |
| Test reshuffle hides lost coverage | Stryker score on KEEP code must not drop; security tests are moved, never deleted, unless the feature is deleted |

## Phase 3 security review (csharp-security-expert)

See [2026-11-05-sec-project-simplification.md](2026-11-05-sec-project-simplification.md).
