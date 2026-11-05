# WP-SIMPL-28 Module wave B: catalog sync (dbt, OpenMetadata)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-28-module-wave-b-catalog-sync-dbt-openmetadata) | Issues: #515, #488 (webhook items) | Advisories (handled before this WP by WP-SIMPL-36 sub-package 5): GHSA-jw88-9j35-3c96 (dbt semantic manifest import; fix), GHSA-jfrx-2mgc-pfh5 (external catalog sync; closed on `main`, re-verify only) | Owner decisions: OD-4, OD-15, OD-17, OD-24 (Lakehouse pilot), OD-25 (#459 in WP-SIMPL-44) | Security conditions: SEC-SIMPL-01, 02, 03, 05, 30, 31, 43, 48, 49

## 1. Goal and outcome
Move dbt ingestion and OpenMetadata sync implementations into `modules/Autheris.Modules.Dbt` and `modules/Autheris.Modules.OpenMetadata`. Outcome: about -7,500 core `src` lines; 13 dbt routes and the OpenMetadata receiver leave core; the semantic metric model (`SemanticMetricCatalogProjection`, placed in `Application/Catalog/Semantic` by WP-SIMPL-17) stays core with no module dependency; every core consumer of a dbt or OpenMetadata type is cut as listed in step 2; the #488 webhook items are fixed before extraction.

## 2. Preconditions and dependencies
- Merged first:
  - WP-SIMPL-27 (Lakehouse pilot, runtime-vs-fallback exit decision; this WP follows that decision).
  - WP-SIMPL-17 (dbt contracts in `Application/Catalog/Semantic`, OpenMetadata contracts in `Application/Catalog/OpenMetadata`, Api-only dbt types and `IOpenMetadataClient` in `Extensions/{Dbt,OpenMetadata}`).
  - WP-SIMPL-36 sub-package 5 (GHSA-jw88-9j35-3c96 fixed; GHSA-jfrx-2mgc-pfh5 is closed on `main` and only re-verified there; this WP does not implement advisory fixes).
  - WP-SIMPL-44 PR 4 (#459: dbt `meta.indexes` and physical key/index chain removal; this WP does not implement #459).
- Ordered PRs inside this WP: #488 webhook items -> metric model guard -> dbt extraction -> OpenMetadata extraction.
- Runs after WP-SIMPL-27; may run in parallel with WP-SIMPL-29 (disjoint folders, serialize `ExtensionsServiceCollectionExtensions.cs` merges).

## 3. Scope
### In scope
- `src/Autheris.Extensions/Dbt/` (21 files, 5,692 lines on 63cbb893, plus the Api-only types WP-SIMPL-17 moved there), `src/Autheris.Api/Endpoints/DbtEndpoints.cs` (13 `Map*`), mapping at `GatewayApplicationBuilderExtensions.cs:221`.
- `src/Autheris.Extensions/OpenMetadata/` (5 files, 1,838 lines) and adapter; the OpenMetadata receiver `POST /api/webhooks/openmetadata` (`src/Autheris.Api/Endpoints/WebhookEndpoints.cs:75`); GraphQL mutation `SyncOpenMetadataAsync` (`src/Autheris.GraphQL/Types/Mutation.Catalog.cs:86-88`); `IOpenMetadataSyncService` and `OpenMetadataModels` (in `Application/Catalog/OpenMetadata` after WP-SIMPL-17).
- OpenMetadata legacy switches in `src/Autheris.Api/Configuration/LegacySwitchGuard.cs:25-27` stay in core (fail fast also without module).
- Core-consumer cuts of step 2.
- #488 webhook items (replay, buffering) in the dbt/OpenMetadata webhook handlers.
### Out of scope
- GHSA-jw88 fix and GHSA-jfrx re-verification (WP-SIMPL-36 sub-package 5). #459 (WP-SIMPL-44 PR 4).
- YamlDotNet removal (it stays core: used by `Application/Procedures/Services`). Embeddings/egress parts of #488 (security lane, OD-4). New metric features.
- The other inbound routes of `WebhookEndpoints.cs` (ITSM and catalog receivers are deleted by WP-SIMPL-12).

## 4. Current state (verified on 63cbb893; consumers re-checked on 88e5ab74)
- `SemanticMetricCatalogProjection` (today `src/Autheris.Application/Dbt/Services/`) consumers outside dbt: `src/Autheris.Api/Endpoints/AnalyticsEndpoints.cs`, `src/Autheris.Application/Mcp/Services/McpSemanticTools.cs`. WP-SIMPL-17 PR-D moves it with 16 other types to `src/Autheris.Application/Catalog/Semantic/` (namespace `Autheris.Application.Catalog.Semantic`).
- Core consumers of dbt/OpenMetadata contracts: `GraphQL/Interceptors/DbtHealthExecutionMiddleware.cs:20-22,96,100` (`IDbtHealthCircuitBreaker`; registered in the GraphQL pipeline at `Api/Extensions/GatewayServiceCollectionExtensions.GraphQL.cs:192`), `Application/Observability/GatewaySystemMetricsService.cs:21,28` (optional `IDbtHealthCircuitBreaker`), `Api/Extensions/DependencyInjection/GatewayGovernanceServiceExtensions.Policy.cs:43` (registers `DbtHealthCircuitBreaker`, implementation in `Application/Dbt/Services/`, moved to `Extensions/Dbt` by WP-SIMPL-17 as Api-only), `GraphQL/Types/Mutation.Catalog.cs:88` and `Api/Endpoints/WebhookEndpoints.cs:77` (`IOpenMetadataSyncService`).
- The OpenMetadata receiver is mapped with `AllowAnonymous()` and authenticated by the payload signature only (`WebhookEndpoints.cs:106`).
- `YamlDotNet` (`Application.csproj:28`) used only by `Application/Procedures/Services` (no `using YamlDotNet` in Dbt) - stays core.

## 5. Implementation steps
1. #488 webhook items (PR 1): fix the replay and buffering items in the OpenMetadata/dbt webhook handlers per issue text; tests `OpenMetadataWebhookTests.Replayed_event_is_rejected` [Security], `.Buffer_is_bounded` [Security].
2. Core-consumer cuts (PR 2), one decision per consumer:
   - `SemanticMetricCatalogProjection` and the other 16 types WP-SIMPL-17 placed in `Application/Catalog/Semantic` (incl. the four `IDbt*` contracts and `IDbtProposalRepository` with its Infrastructure implementation) stay core. Define `ISemanticMetricImport` (core, `Application/Catalog/Semantic`) that the dbt module calls to import manifests. `AnalyticsEndpoints` and `McpSemanticTools` reference only `Autheris.Application.Catalog.Semantic`. If a moved type drags a dbt-only type into core, cut it with a core DTO, not a module reference.
   - `IDbtHealthCircuitBreaker`: interface stays core; core registers `NoDbtHealthData` (reports "no dbt health data" for every table, which is the current behaviour for tables without dbt tests); the dbt module replaces it with `DbtHealthCircuitBreaker`. `DbtHealthExecutionMiddleware` and `GatewaySystemMetricsService` keep depending on the interface only.
   - `IOpenMetadataSyncService`: no core consumer remains. The GraphQL mutation `SyncOpenMetadataAsync` moves into the OpenMetadata module through `IAutherisGraphQLModule` (WP-SIMPL-25); the receiver moves into the module through the registrar (step 4). `IOpenMetadataSyncService` and `OpenMetadataModels` then move into the module.
3. Extraction dbt (PR 3): module `Id = "dbt"`, references `Autheris.Modules.Abstractions`, `Autheris.Domain`, `Autheris.Application`; `ConfigurationSections` from existing dbt options binding (verify by grep); routes via `registrar.MapGroup` unchanged; remove `GatewayApplicationBuilderExtensions.cs:221` and DI lines (incl. `GatewayGovernanceServiceExtensions.Policy.cs:43`); `KnownModuleSections` + full-image enabled list.
4. Extraction OpenMetadata (PR 4): module `Id = "openmetadata"`; uses the core catalog import API only. The receiver `POST /api/webhooks/openmetadata` is mapped through the registrar, so it requires an authenticated caller (OpenMetadata pushes with a service-principal token) and keeps the payload signature validation unconditionally; anonymous access ends (breaking change, migration-guide row). The GraphQL mutation is registered through `IAutherisGraphQLModule` with its existing authorization.
5. Move tests for both modules; keep Security tags (SEC-SIMPL-03/48).

## 6. Tests
- From step 1 (above), in `Autheris.Extensions.Tests` first, moving with the module.
- `ModuleParityTests.Dbt_security_tests_migrated` and `.OpenMetadata_security_tests_migrated` [Security] (SEC-SIMPL-48/49: every Security test of the pre-move list exists and is green with the module loaded).
- Existing `SemanticMetricCatalogProjectionTests` stay in core (location after WP-SIMPL-17, names unchanged).
- Architecture: `CoreMetricModelArchitectureTests.Metric_model_has_no_dbt_dependency` (no type in `Autheris.Application.Catalog.Semantic` references a module assembly or `Autheris.Extensions`).
- `DbtModuleContractTests`: `Dbt_routes_unchanged_when_enabled` (baseline `Baselines/dbt-routes.txt`), `Dbt_routes_require_authorization` [Security], `Dbt_routes_404_when_absent` [Security], `Dbt_config_without_module_fails_fast` [Security], `Analytics_metrics_work_without_dbt_module`, `GraphQL_queries_work_without_dbt_module` (no-op health implementation).
- `OpenMetadataModuleContractTests`: `Webhook_signature_validation_always_on` [Security], `Webhook_receiver_requires_authenticated_caller` [Security], `Legacy_insecure_switches_fail_fast_without_module` [Security], `OpenMetadata_config_without_module_fails_fast` [Security], `Sync_mutation_absent_without_module`.

## 7. Verification commands
```
git grep -n "SemanticMetricCatalogProjection" -- src   # only Application/Catalog/Semantic + core consumers
git grep -n -i "namespace Autheris.*\(Dbt\|OpenMetadata\)" -- src   # expect none after PR 4
dotnet build Autheris.sln -c Release -warnaserror
dotnet test modules/Autheris.Modules.Dbt.Tests -c Release
dotnet test modules/Autheris.Modules.OpenMetadata.Tests -c Release
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --filter "Category=Security"
dotnet test tests/Autheris.Tests.Architecture -c Release
```

## 8. Acceptance criteria
- [ ] WP-SIMPL-36 sub-package 5 and WP-SIMPL-44 PR 4 merged before PR 3; #488 webhook PR merged before PR 3.
- [ ] Metric model in core (`Application/Catalog/Semantic`); analytics/MCP metric paths and GraphQL queries work with the dbt module absent.
- [ ] No dbt/OpenMetadata ingestion or sync implementation (parser, client, sync service, endpoints, GraphQL mutation) under `src/`; the contracts kept in step 2 stay core; `src` LOC reduced by >= 7,000.
- [ ] Route baselines identical when enabled; 404 when absent; config fail-fast when absent.
- [ ] OpenMetadata receiver requires authentication and a valid signature.
- [ ] No advisory details in titles, commits, tests (SEC-SIMPL-01 scan passes).
- [ ] SEC-SIMPL-02 items in PR 3/4 descriptions.

## 9. PR slicing
1. #488 webhook items. 2. Core-consumer cuts and `ISemanticMetricImport` (~150 net). 3. dbt extraction (`size-exception`, net negative). 4. OpenMetadata extraction (`size-exception`, net negative).

## 10. Risks, rollback, must NOT
- Risk: metric model drags dbt types into core; cut with a core DTO, not a module reference.
- Risk: existing OpenMetadata deployments push anonymously; mitigated by the migration-guide row (no production yet).
- Rollback: revert the extraction PR; contracts stay core.
- Must NOT: extract before PRs 1-2 and the preconditions merge; re-implement GHSA-jw88, re-open GHSA-jfrx or implement #459 here; move YamlDotNet; make webhook signature validation optional; map an anonymous module route; describe the advisories.

## 11. Estimate
-7,500 src, +7,400 modules, +250 cut/test lines; 4 person-days.

## Decisions taken in this spec
- YamlDotNet stays core (verified consumer in Procedures).
- Core namespace for the metric model: `Autheris.Application.Catalog.Semantic` (as placed by WP-SIMPL-17; no second namespace).
- `IDbtHealthCircuitBreaker` stays core with a no-data default implementation; the dbt module supplies the real one.
- The OpenMetadata receiver becomes an authenticated module route (signature still required), because module routes cannot be anonymous (WP-SIMPL-25, SEC-SIMPL-43).
- OpenMetadata legacy switch entries stay in core `LegacySwitchGuard` so they fail fast even when the module is absent.
