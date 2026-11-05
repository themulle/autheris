# WP-SIMPL-26 Pilot module: OData
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-26-pilot-module-odata-revised-for-od-15-extended) | Issues: #515 | Owner decisions: OD-15 (Olap stays core; OData is the pilot), OD-17 | Security conditions: SEC-SIMPL-02, 03, 05, 30, 31, 43, 48, 49

## 1. Goal and outcome
Move OData into `modules/Autheris.Modules.OData`, loaded through the WP-SIMPL-25 host. Outcome: core `src` shrinks by about 2,700 lines; with the module enabled all OData routes behave identically (same paths, same auth, same masking); without it they return 404 and `Gateway:OData` config fails fast. Exit decision recorded: runtime loading confirmed, or fallback AD-3.10 adopted for all modules.

## 2. Preconditions and dependencies
- WP-SIMPL-25 merged (SEC-SIMPL-05). Blocks WP-SIMPL-27..30.

## 3. Scope
### In scope
- `src/Autheris.Extensions/OData/` (6 files, 1,927 lines), `src/Autheris.Application/OData/` (4 files, 768 lines), `src/Autheris.Api/Endpoints/ODataEndpoints.cs`, `ODataEndpoints.Metadata.cs`, `ODataEndpoints.Query.cs`, the OData mapping at `GatewayApplicationBuilderExtensions.cs:230`, OData DI registrations in `ExtensionsServiceCollectionExtensions.cs`.
- `ODataOptions` (`src/Autheris.Domain/Options/Protocols/ODataOptions.cs`): moves into the module.
- OData tests from `Autheris.Tests.Unit`/`Autheris.Extensions.Tests` to `modules/Autheris.Modules.OData.Tests`.
### Out of scope
- Olap, DuckDB, table cache (core, OD-15). Behaviour changes to OData. Other modules.

## 4. Current state (verified on 63cbb893)
- `ODataEndpoints.cs`: 22 `Map*` calls; mapped conditionally at `GatewayApplicationBuilderExtensions.cs:230` (`app.MapODataEndpoints(gatewayOptions)`).
- `ODataOptions.cs` references `Parquet.` (content negotiation); Parquet.Net stays core (see WP-SIMPL-27 decision), so the module resolves it from the default ALC.

## 5. Implementation steps
1. Red: add `ODataModuleContractTests` in the module test project (routes, auth, 404 when absent) against the current host (fail because module does not exist).
2. Create `modules/Autheris.Modules.OData/Autheris.Modules.OData.csproj` (refs: `Autheris.Modules.Abstractions`, `Autheris.Domain`, `Autheris.Application`; no Infrastructure, no Extensions) and `modules/Autheris.Modules.OData.Tests`. Add both to `Autheris.sln`.
3. `git mv` sources: `Extensions/OData/*`, `Application/OData/*`, `Api/Endpoints/ODataEndpoints*.cs`, `ODataOptions.cs` into the module; namespaces `Autheris.Modules.OData.*`.
4. `ODataModule : IAutherisModule` with `Id = "odata"`, `ConfigurationSections = ["Gateway:OData"]` (verify the exact section name from `ODataOptions` binding before coding), `ConfigureServices` = moved registrations, `MapEndpoints(registrar)` = moved route groups using `registrar.MapGroup(<existing prefix>)`; paths unchanged.
5. Remove the OData lines from `GatewayApplicationBuilderExtensions.cs:230` and from `ExtensionsServiceCollectionExtensions.cs`; add `"odata"` to `KnownModuleSections` (WP-SIMPL-25 step 8).
6. Any core consumer of OData types found by `git grep -n "Autheris.*OData" -- src` must be cut via an abstraction in Application or removed; none may reference the module.
7. Move tests (`git mv`), keep names; Security-tagged tests stay Security-tagged (SEC-SIMPL-03/48).
8. Release/full image: module is signed and copied by the WP-SIMPL-25 pipeline; add `odata` to the full-image `Gateway__Modules__Enabled` default.
9. Exit decision: if HotChocolate/OpenAPI or EDM registration fails before `Build()`, switch to fallback AD-3.10 (`-p:AutherisFlavor=full`) and record it in the plan addendum.

## 6. Tests
- `modules/Autheris.Modules.OData.Tests/ODataModuleContractTests`: `Routes_unchanged_when_module_enabled` (route table snapshot equals pre-move snapshot `Baselines/odata-routes.txt`), `Routes_require_authorization` [Security], `Routes_return_404_when_module_absent` [Security], `OData_config_without_module_fails_fast` [Security], `Masking_applies_to_odata_results` [Security].
- Moved: all existing OData unit tests (names unchanged).
- Architecture: `ModuleBoundaryArchitectureTests` now runs on the OData assembly (no new test).

## 7. Verification commands
```
git grep -n "namespace Autheris.*OData" -- src     # expect none
dotnet build Autheris.sln -c Release -warnaserror
dotnet test modules/Autheris.Modules.OData.Tests -c Release
dotnet test tests/Autheris.Tests.Architecture -c Release
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --filter "Category=Security"
```

## 8. Acceptance criteria
- [ ] No OData source under `src/`; `src` LOC reduced by >= 2,600.
- [ ] Route snapshot identical with module enabled; 404 without it.
- [ ] PR description has SEC-SIMPL-02 items (config keys, routes, controls).
- [ ] Exit decision (runtime vs fallback) recorded in the plan.
- [ ] Moved test count equals removed test count (test name diff in PR).

## 9. PR slicing
1. Module project skeleton + contract tests (red) + route baseline.
2. Move sources and tests (`size-exception`: moves only, net src lines negative).

## 10. Risks, rollback, must NOT
- Risk: EDM model built via static state not ALC-safe; fallback AD-3.10.
- Rollback: revert PR 2.
- Must NOT: change routes, add `AllowAnonymous`, reference Infrastructure/Extensions from the module, delete Security tests.

## 11. Estimate
-2,700 src, +2,750 modules (incl. module class), tests moved; 3 person-days.

## Decisions taken in this spec
- `ODataOptions` moves with the module (it is only consumed by OData); verify by grep in step 6.
