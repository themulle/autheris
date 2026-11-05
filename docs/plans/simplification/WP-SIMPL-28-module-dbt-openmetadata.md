# WP-SIMPL-28 Module wave B: catalog sync (dbt, OpenMetadata)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-28-module-wave-b-catalog-sync-dbt-openmetadata) | Issues: #515, #459, #488 (webhook items) | Advisories: GHSA-jw88-9j35-3c96 (dbt semantic manifest import; see private advisory) | Owner decisions: OD-4, OD-15, OD-17 | Security conditions: SEC-SIMPL-01, 02, 03, 05, 30, 31, 43, 48, 49

## 1. Goal and outcome
Move dbt ingestion and OpenMetadata sync into `modules/Autheris.Modules.Dbt` and `modules/Autheris.Modules.OpenMetadata`, after moving the semantic metric model to core. Outcome: about -8,000 core `src` lines, 13 dbt routes leave core, `SemanticMetricCatalogProjection` lives in core Catalog/Analytics with no module dependency, and GHSA-jw88, #459 and the #488 webhook items are fixed before extraction.

## 2. Preconditions and dependencies
- WP-SIMPL-26 and WP-SIMPL-17 (Application/Dbt and Application/OpenMetadata merged into Extensions) merged.
- Ordered PRs inside this WP: GHSA-jw88 fix -> #459 -> #488 webhook items -> metric model move -> extraction. Advisory fix PR follows the private advisory process (neutral title, SEC-SIMPL-01).

## 3. Scope
### In scope
- `src/Autheris.Extensions/Dbt/` (21 files, 5,692 lines, plus merged Application copy after WP-17), `src/Autheris.Api/Endpoints/DbtEndpoints.cs` (13 `Map*`), mapping at `GatewayApplicationBuilderExtensions.cs:221`.
- `src/Autheris.Extensions/OpenMetadata/` (5 files, 1,838 lines) and adapter; OpenMetadata legacy switches in `src/Autheris.Api/Configuration/LegacySwitchGuard.cs:25-27` stay in core (fail fast also without module).
- `src/Autheris.Application/Dbt/Services/SemanticMetricCatalogProjection.cs` -> core Catalog/Analytics.
### Out of scope
- YamlDotNet removal (it stays core: used by `Application/Procedures/Services`). Embeddings/egress parts of #488 (security lane, OD-4). New metric features.

## 4. Current state (verified on 63cbb893)
- `SemanticMetricCatalogProjection` consumers outside dbt: `src/Autheris.Api/Endpoints/AnalyticsEndpoints.cs`, `src/Autheris.Application/Mcp/Services/McpSemanticTools.cs` - core depends on it, so it must move before extraction.
- `YamlDotNet` (`Application.csproj:28`) used only by `Application/Procedures/Services` (no `using YamlDotNet` in Dbt) - stays core.
- Plan rows: #459 FIX-IN-CORE before WP-28; #488 webhook parts with WP-28; GHSA-jw88 FIX-IN-CORE before WP-28.

## 5. Implementation steps
1. GHSA-jw88 (PR 1): implement the fix agreed in the private advisory inside `Extensions/Dbt` with tests named neutrally (e.g. `DbtSemanticManifestImportTests.Rejects_invalid_semantic_manifest_input`), [Security].
2. #459 (PR 2): read `meta.indexes` and declared keys only from dbt metadata; tests `DbtMetadataIngestionTests.Reads_declared_indexes_from_meta`, `.Ignores_undeclared_keys`.
3. #488 webhook items (PR 3): fix the replay and buffering items in the OpenMetadata/dbt webhook handlers per issue text; tests `OpenMetadataWebhookTests.Replayed_event_is_rejected` [Security], `.Buffer_is_bounded` [Security].
4. Metric model to core (PR 4): `git mv` `SemanticMetricCatalogProjection.cs` (and its model types it needs, found by compiler) to `src/Autheris.Application/Catalog/Analytics/`, namespace `Autheris.Application.Catalog.Analytics`; define `ISemanticMetricImport` (core) that dbt calls to import manifests. `AnalyticsEndpoints` and `McpSemanticTools` reference only the core namespace. Arch test ensures core metric namespace has no dbt reference.
5. Extraction dbt (PR 5): module `Id = "dbt"`, `ConfigurationSections` from existing dbt options binding (verify by grep); routes via `registrar.MapGroup` unchanged; remove `GatewayApplicationBuilderExtensions.cs:221` and DI lines; `KnownModuleSections` + full-image enabled list.
6. Extraction OpenMetadata (PR 6): module `Id = "openmetadata"`; uses core catalog import API only; webhook endpoints (if any) through registrar with signature validation kept unconditional.
7. Move tests for both modules; keep Security tags.

## 6. Tests
- From steps 1-3 (above), in Extensions.Tests first, moving with the module.
- `SemanticMetricCatalogProjectionTests` (moved to `tests/Autheris.Tests.Unit/Catalog/Analytics/`, names unchanged).
- Architecture: `CoreMetricModelArchitectureTests.Metric_model_has_no_dbt_dependency`.
- `DbtModuleContractTests`: `Dbt_routes_unchanged_when_enabled` (baseline `Baselines/dbt-routes.txt`), `Dbt_routes_require_authorization` [Security], `Dbt_routes_404_when_absent` [Security], `Dbt_config_without_module_fails_fast` [Security], `Analytics_metrics_work_without_dbt_module`.
- `OpenMetadataModuleContractTests`: `Webhook_signature_validation_always_on` [Security], `Legacy_insecure_switches_fail_fast_without_module` [Security], `OpenMetadata_config_without_module_fails_fast` [Security].

## 7. Verification commands
```
git grep -n "SemanticMetricCatalogProjection" -- src   # only Application/Catalog/Analytics + consumers
git grep -n -i "namespace Autheris.*\(Dbt\|OpenMetadata\)" -- src   # expect none after PR 6
dotnet build Autheris.sln -c Release -warnaserror
dotnet test modules/Autheris.Modules.Dbt.Tests -c Release
dotnet test modules/Autheris.Modules.OpenMetadata.Tests -c Release
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --filter "Category=Security"
dotnet test tests/Autheris.Tests.Architecture -c Release
```

## 8. Acceptance criteria
- [ ] GHSA-jw88, #459, #488 webhook PRs merged before PR 5.
- [ ] Metric model in core; analytics/MCP metric paths work with dbt module absent.
- [ ] No dbt/OpenMetadata source under `src/`; `src` LOC reduced by >= 7,500.
- [ ] Route baselines identical when enabled; 404 when absent; config fail-fast when absent.
- [ ] No advisory details in titles, commits, tests (SEC-SIMPL-01 scan passes).
- [ ] SEC-SIMPL-02 items in PR 5/6 descriptions.

## 9. PR slicing
1. GHSA-jw88 fix. 2. #459. 3. #488 webhook items. 4. Metric model move (~100 net). 5. dbt extraction (`size-exception`, net negative). 6. OpenMetadata extraction (`size-exception`, net negative).

## 10. Risks, rollback, must NOT
- Risk: metric model drags dbt types into core; cut with a core DTO, not a module reference.
- Must NOT: extract before PRs 1-4 merge; move YamlDotNet; make webhook signature validation optional; describe the advisory.

## 11. Estimate
-8,000 src, +7,900 modules, +300 fix/test lines; 4 person-days (+1 for the three fixes).

## Decisions taken in this spec
- YamlDotNet stays core (verified consumer in Procedures).
- Core target namespace for the metric model: `Autheris.Application.Catalog.Analytics`.
- OpenMetadata legacy switch entries stay in core `LegacySwitchGuard` so they fail fast even when the module is absent.
