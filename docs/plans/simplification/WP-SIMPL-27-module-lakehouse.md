# WP-SIMPL-27 Module wave A: Lakehouse (module pilot)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-27-module-wave-a-lakehouse) | Issues: #515, #490 (Lakehouse part, lands before or inside this WP) | Owner decisions: OD-15, OD-17, OD-23 (module sources run through the governed pipeline), OD-24 (Lakehouse is the module pilot), OD-25 (declared column set; WP-SIMPL-44 D7), OD-30 (single access decision point), OD-37 (runtime-vs-compile-time module loading decided here) | Security conditions: SEC-SIMPL-02, 03, 05, 30, 31, 40..49

> **Module pilot (OD-24, OD-37):** OData stays core, so Lakehouse is the first real module. This WP takes the runtime-loading-vs-compile-time (fallback AD-3.10) decision for all modules (step 9); WP-SIMPL-28 and WP-SIMPL-29 start only after it is recorded.

## 1. Goal and outcome
Move Lakehouse (Iceberg REST catalog, Delta Lake executor) into `modules/Autheris.Modules.Lakehouse`. Outcome: about -4,600 core `src` lines; the 4 Iceberg routes (5 `Map*` calls including the group) are served only when the module is enabled; Delta and Iceberg data access runs through the same governed pipeline as core sources (consent grants, OD-22 controls, audit, transport gate, cross-source federation; OD-23); the module ALC native-library path is proven by a test fixture module; the runtime-vs-fallback exit decision is recorded.

## 2. Preconditions and dependencies
- WP-SIMPL-25 merged (module host, SEC-SIMPL-40..49 tests green).
- WP-SIMPL-46 merged (Iceberg load and credential vending decide through `IAccessDecisionPoint` with `AccessTransport.Lakehouse`; `IcebergRestCatalogFederationService.cs:118-124,328-330` re-routed by WP-SIMPL-46 step 5).
- The Lakehouse part of #490 merged first or included as PR 1.
- Blocks WP-SIMPL-28 and WP-SIMPL-29 (they need the exit decision of step 9). May overlap with WP-SIMPL-28 PR work that does not touch `ExtensionsServiceCollectionExtensions.cs` (serialize merges).

## 3. Scope
### In scope
- `src/Autheris.Extensions/Lakehouse/` (20 files, 3,680 lines), `src/Autheris.Api/Endpoints/IcebergRestCatalogEndpoints.cs` (1 `MapGroup` at `:21`, 4 routes at `:24`, `:37`, `:52`, `:78`), mapping at `GatewayApplicationBuilderExtensions.cs:269`, Lakehouse DI registrations and options.
- Lakehouse part of #490 (if not yet merged).
- Governed-pipeline tests for Delta and Iceberg (OD-23) and undeclared-column rejection (WP-SIMPL-44 D7).
- Native-dependency ALC test (fixture module).
- Exit decision runtime loading vs fallback AD-3.10 (OD-37).
### Out of scope
- Parquet.Net removal from core (see section 4). Arrow/Parquet export. DuckDB.
- Writes to Lakehouse tables (no write path exists; OD-35).

## 4. Current state (verified on 63cbb893; route count re-checked on 88e5ab74)
- `Parquet.Net` 6.1.0 is referenced in `src/Autheris.Application/Autheris.Application.csproj:18` and used by core: `Application/Serialization/ParquetExportService.cs`, `Api/Serialization/ParquetContentNegotiation.cs`, `Api/Middleware/ParquetGraphQLResponseMiddleware.cs`, `Api/Endpoints/WebSqlEndpoints.Execution.cs`. Lakehouse uses it in `Extensions/Lakehouse/Services/DeltaLakeDataSourceExecutor.cs`. So Parquet.Net stays core and the module resolves it from the default ALC; it does not prove private-dependency loading.
- `IcebergRestCatalogEndpoints.cs`: `MapGroup("")` at `:21`, `MapGet` at `:24`, `:37`, `:52`, `MapPost` (credentials) at `:78`.

## 5. Implementation steps
1. If #490 Lakehouse fix is open: implement it in core first (own PR, its own tests).
2. Red: `LakehouseModuleContractTests` and `LakehouseGovernanceTests` (section 6).
3. Create `modules/Autheris.Modules.Lakehouse` (+ `.Tests`); references: `Autheris.Modules.Abstractions`, `Autheris.Domain`, `Autheris.Application`; no `Autheris.Infrastructure`, no `Autheris.Extensions`. Add both to `Autheris.sln`.
4. `git mv` Lakehouse sources and `IcebergRestCatalogEndpoints.cs`; `LakehouseModule : IAutherisModule`, `Id = "lakehouse"`, sections from the existing Lakehouse options binding (verify name by grep before coding); routes via `registrar.MapGroup` with unchanged paths.
5. Remove mapping at `GatewayApplicationBuilderExtensions.cs:269` and DI lines; add `lakehouse` to `KnownModuleSections` and the full-image enabled list.
6. Delta Lake data-source executor registration: register through the core data-source executor extension point; core must not reference the module type. Delta queries enter the core governed pipeline (gate, `IAccessDecisionPoint` with the query's transport, row filter, masking, audit) before the executor runs; the executor receives only the already-governed plan.
7. Declared columns (WP-SIMPL-44 D7, OD-25): reading the Delta log or Iceberg metadata is reading the table format, but the module serves only columns declared in the catalog; a column present in the table format and not declared is rejected (query) or omitted (Iceberg load response), never passed through.
8. Native ALC proof: add `tests/Autheris.Tests.Unit/Hosting/Fixtures/NativeDepModule` (tiny module with a private managed dependency not present in the host and a `runtimes/linux-x64/native/` stub lib) and test it through `ModuleAssemblyLoadContext`.
9. Exit decision (OD-37): run the full-flavor image with the Lakehouse module. If route, OpenAPI or HotChocolate registration fails before `Build()`, or the ALC cannot load the module's dependencies, adopt fallback AD-3.10 (`-p:AutherisFlavor=full`, compile-time inclusion) for all modules; otherwise runtime loading is confirmed. Record the decision with the evidence (test run, image tag) in the plan addendum; WP-SIMPL-28/29 follow it.
10. Move tests (`git mv`, names unchanged, Security tags kept).

## 6. Tests
- `LakehouseModuleContractTests`: `Iceberg_routes_unchanged_when_enabled` (baseline `Baselines/lakehouse-routes.txt`), `Iceberg_routes_require_authorization` [Security], `Iceberg_routes_404_when_absent` [Security], `Lakehouse_config_without_module_fails_fast` [Security], `Delta_executor_enforces_query_gate` [Security].
- `LakehouseGovernanceTests` [Security] (OD-23, run with the module loaded): `Delta_query_applies_row_filter_and_masking`, `Delta_rejects_undeclared_columns` (WP-SIMPL-44 D7), `Iceberg_load_omits_undeclared_columns`, `Iceberg_load_and_credential_vending_decide_via_access_decision_point` (decision recorded with `AccessTransport.Lakehouse`; no grant -> 403 and no credentials issued), `Delta_joins_with_sql_server_source_via_federation` (cross-source join through the core federation path, OD-15), `Delta_query_is_audited`.
- `ModuleAssemblyLoadContextTests.Private_managed_dependency_resolves_from_module_folder`, `.Private_native_library_resolves_from_runtimes_folder`, `.Shared_host_assembly_Parquet_resolves_from_default_context` [Security].
- Architecture: `ModuleBoundaryArchitectureTests` runs on the Lakehouse assembly (no new test); `PackageBoundaryArchitectureTests.Lakehouse_module_does_not_bypass_access_decision_point` (SEC-AZ-01 scan over the module assembly: no `TableAccessPolicy` construction, no private decision path).
- `ModuleParityTests.Lakehouse_security_tests_migrated` [Security] (SEC-SIMPL-48/49: every Lakehouse Security test of the pre-move list exists and is green with the module loaded).
- Moved: all existing Lakehouse tests.

## 7. Verification commands
```
git grep -n -i "lakehouse\|iceberg" -- src ':!*.md'   # expect only config schema table entries and the AccessTransport member
dotnet build Autheris.sln -c Release -warnaserror
dotnet test modules/Autheris.Modules.Lakehouse.Tests -c Release
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --filter "FullyQualifiedName~Hosting"
dotnet test tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release --filter "FullyQualifiedName~Lakehouse"
dotnet test tests/Autheris.Tests.Architecture -c Release
```

## 8. Acceptance criteria
- [ ] No Lakehouse/Iceberg source under `src/`; `src` LOC reduced by >= 4,300.
- [ ] Route baseline identical when enabled (4 routes); 404 when absent.
- [ ] `LakehouseGovernanceTests` green: row filter, masking, undeclared-column rejection, decision-point use, federation join and audit work with the module loaded.
- [ ] Native and private-managed ALC tests pass.
- [ ] Module folder contains no `Parquet.Net.dll` (collision rule) - the module build sets `Private=false`/`ExcludeAssets=runtime` for host-shared packages.
- [ ] Exit decision (runtime loading confirmed or fallback AD-3.10 adopted for all modules) recorded in the plan addendum with evidence.
- [ ] SEC-SIMPL-02 items in PR description.

## 9. PR slicing
1. (#490 Lakehouse fix, if open.)
2. Module skeleton, contract and governance tests, native ALC fixture tests (~300).
3. Move (`size-exception`, net negative) + exit decision record.

## 10. Risks, rollback, must NOT
- Risk: module ships its own `Parquet.Net.dll` and trips the collision check; fix in the csproj, never by relaxing the check.
- Risk: runtime loading fails for HotChocolate/OpenAPI; then fallback AD-3.10 applies to all modules (step 9), not a per-module exception.
- Rollback: revert PR 3; the module host stays.
- Must NOT: remove Parquet.Net from core, change Iceberg paths, weaken the gate for Delta queries, pass undeclared columns through, issue Iceberg credentials without an allow decision from `IAccessDecisionPoint`.

## 11. Estimate
-4,600 src, +4,650 modules, +150 test fixture, +350 governance tests; 3 person-days.

## Decisions taken in this spec
- Parquet.Net stays core (verified core consumers); the plan's "Parquet.Net leaves core" is not achievable. Footprint package table in the plan to be corrected in the addendum. Private/native dependency loading is proven with a test fixture module instead.
- The runtime-vs-compile-time module loading decision is taken here, for all modules (OD-24, OD-37).
