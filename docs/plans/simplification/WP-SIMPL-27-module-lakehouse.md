# WP-SIMPL-27 Module wave A: Lakehouse
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-27-module-wave-a-lakehouse) | Issues: #515, #490 (Lakehouse part, lands before or inside this WP) | Owner decisions: OD-15, OD-17 | Security conditions: SEC-SIMPL-02, 03, 05, 30, 31, 43, 48, 49

## 1. Goal and outcome
Move Lakehouse (Iceberg REST catalog, Delta Lake executor) into `modules/Autheris.Modules.Lakehouse`. Outcome: about -4,600 core `src` lines, 5 Iceberg routes served only when the module is enabled, and the module ALC native-library path proven by a test fixture module.

## 2. Preconditions and dependencies
- WP-SIMPL-26 merged (runtime loading confirmed or fallback chosen). The Lakehouse part of #490 merged first or included as PR 1.
- Can run in parallel with WP-SIMPL-28 only if they do not touch the same `ExtensionsServiceCollectionExtensions.cs` hunks (serialize merges).

## 3. Scope
### In scope
- `src/Autheris.Extensions/Lakehouse/` (20 files, 3,680 lines), `src/Autheris.Api/Endpoints/IcebergRestCatalogEndpoints.cs` (5 `Map*`), mapping at `GatewayApplicationBuilderExtensions.cs:269`, Lakehouse DI registrations and options.
- Lakehouse part of #490 (if not yet merged).
- Native-dependency ALC test (fixture module).
### Out of scope
- Parquet.Net removal from core (see section 4). Arrow/Parquet export. DuckDB.

## 4. Current state (verified on 63cbb893)
- `Parquet.Net` 6.1.0 is referenced in `src/Autheris.Application/Autheris.Application.csproj:18` and used by core: `Application/Serialization/ParquetExportService.cs`, `Api/Serialization/ParquetContentNegotiation.cs`, `Api/Middleware/ParquetGraphQLResponseMiddleware.cs`, `Api/Endpoints/WebSqlEndpoints.Execution.cs`. Lakehouse uses it in `Extensions/Lakehouse/Services/DeltaLakeDataSourceExecutor.cs`. So Parquet.Net stays core and the module resolves it from the default ALC; it does not prove private-dependency loading.

## 5. Implementation steps
1. If #490 Lakehouse fix is open: implement it in core first (own PR, its own tests).
2. Red: `LakehouseModuleContractTests` (routes, auth, 404 absent, config fail-fast).
3. Create `modules/Autheris.Modules.Lakehouse` (+ `.Tests`), refs as in WP-SIMPL-26.
4. `git mv` Lakehouse sources and `IcebergRestCatalogEndpoints.cs`; `LakehouseModule : IAutherisModule`, `Id = "lakehouse"`, sections from the existing Lakehouse options binding (verify name by grep before coding); routes via `registrar.MapGroup` with unchanged paths.
5. Remove mapping at `GatewayApplicationBuilderExtensions.cs:269` and DI lines; add `lakehouse` to `KnownModuleSections` and the full-image enabled list.
6. Delta Lake data-source executor registration: register through the core data-source executor extension point; core must not reference the module type.
7. Native ALC proof: add `tests/Autheris.Tests.Unit/Hosting/Fixtures/NativeDepModule` (tiny module with a private managed dependency not present in the host and a `runtimes/linux-x64/native/` stub lib) and test it through `ModuleAssemblyLoadContext`.
8. Move tests.

## 6. Tests
- `LakehouseModuleContractTests`: `Iceberg_routes_unchanged_when_enabled` (baseline `Baselines/lakehouse-routes.txt`), `Iceberg_routes_require_authorization` [Security], `Iceberg_routes_404_when_absent` [Security], `Lakehouse_config_without_module_fails_fast` [Security], `Delta_executor_enforces_query_gate` [Security].
- `ModuleAssemblyLoadContextTests.Private_managed_dependency_resolves_from_module_folder`, `.Private_native_library_resolves_from_runtimes_folder`, `.Shared_host_assembly_Parquet_resolves_from_default_context` [Security].
- Moved: all existing Lakehouse tests.

## 7. Verification commands
```
git grep -n -i "lakehouse\|iceberg" -- src ':!*.md'   # expect only config schema table entries
dotnet build Autheris.sln -c Release -warnaserror
dotnet test modules/Autheris.Modules.Lakehouse.Tests -c Release
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --filter "FullyQualifiedName~Hosting"
dotnet test tests/Autheris.Tests.Architecture -c Release
```

## 8. Acceptance criteria
- [ ] No Lakehouse/Iceberg source under `src/`; `src` LOC reduced by >= 4,300.
- [ ] Route baseline identical when enabled; 404 when absent.
- [ ] Native and private-managed ALC tests pass.
- [ ] Module folder contains no `Parquet.Net.dll` (collision rule) - the module build sets `Private=false`/`ExcludeAssets=runtime` for host-shared packages.
- [ ] SEC-SIMPL-02 items in PR description.

## 9. PR slicing
1. (#490 Lakehouse fix, if open.)
2. Module skeleton, contract tests, native ALC fixture tests (~250).
3. Move (`size-exception`, net negative).

## 10. Risks, rollback, must NOT
- Risk: module ships its own `Parquet.Net.dll` and trips the collision check; fix in the csproj, never by relaxing the check.
- Must NOT: remove Parquet.Net from core, change Iceberg paths, weaken the gate for Delta queries.

## 11. Estimate
-4,600 src, +4,650 modules, +150 test fixture; 2 person-days.

## Decisions taken in this spec
- Parquet.Net stays core (verified core consumers); the plan's "Parquet.Net leaves core" is not achievable. Footprint package table in the plan to be corrected in the addendum. Private/native dependency loading is proven with a test fixture module instead.
