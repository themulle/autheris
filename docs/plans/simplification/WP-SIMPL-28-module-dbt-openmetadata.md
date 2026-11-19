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

## Addendum 2026-11-19 (PR 3 rework)
Verified on `368ebcd06`. Open decisions take the recommended default ("default taken"). Breaking changes are allowed (no production) with a CHANGELOG entry. This addendum replaces §5 step 3, the dbt rows of §6 and the PR 3 item of §9.

### Verified inventory
- Moving: `src/Autheris.Extensions/Dbt/` = 34 `.cs` files, 7,674 lines (not 36). `src/Autheris.Api/Endpoints/DbtEndpoints.cs` = 557 lines, 13 `Map*` (`:29-345`). Wiring: `ExtensionsServiceCollectionExtensions.cs:37` (`AddDbtIntegration`), `GatewayApplicationBuilderExtensions.cs:231` (`MapDbtEndpoints`).
- Also moving into the module (their implementations move): `GatewayGovernanceServiceExtensions.Policy.cs:41` (`DbtTestTargetMap`), `GatewayAnalyticsServiceExtensions.cs:34` (`DbtSemanticManifestIngestionService`), and the `ScimDataOwnerIdentityVerifier` registration (only dbt consumes `IDataOwnerIdentityVerifier`).
- Staying core: `Application/Catalog/Semantic/*` contracts (incl. `IDbtTestTargetMap`, `IDbtSemanticManifestIngestionService`, `ITelemetryMetricsProvider`), `NoDbtHealthData` (`Policy.cs:42`), `IDbtProposalRepository` + `Infrastructure/Persistence/InMemoryDbtProposalRepository` (`Policy.cs:40`), `DbtOptions` (`Domain/Options/Integrations/DataCatalogOptions.cs:82`), `DbtWebhookProcessingResult` (`Domain/Model/DbtWebhookModels.cs`), `DbtHealthExecutionMiddleware`. `KnownModuleSections` already maps `GatewaySections.Dbt -> "dbt"` (`GatewayConfigurationSchema.cs:110`).
- `Autheris.Modules.Abstractions` already references `Autheris.Domain` (`TenantId`, `SecurityPrincipalContext`, `AuditPolicy`, `AuditEventTypes` are usable there).

### Decisions
1. **Module-safe endpoint helpers: host-provided service plus Abstractions conventions (default taken; per-module copies rejected because they let authorization checks drift).**
   - New `src/Autheris.Modules.Abstractions/IModuleRequestContext.cs`. Every member delegates 1:1 to the existing host helper, so its behavior is identical to that helper:
     - `TenantId GetRequestTenant(HttpContext context)`: throws on a malformed tenant claim (fail closed).
     - `SecurityPrincipalContext GetSecurityContext(HttpContext context)`, `string? GetCallerIdentity(HttpContext context)`.
     - `bool IsCanonicalClusterAdmin(ClaimsPrincipal? principal)`, `bool IsGlobalGovernanceAdmin(ClaimsPrincipal? principal)`.
     - `IResult? TryApplyBodyLimit(HttpRequest request, long maxBytes, string tooLargeMessage)`, `Task<(string? Body, IResult? Error)> TryReadBodyAsync(HttpRequest request, long maxBytes, string tooLargeMessage, CancellationToken ct)`, `Task<IResult> WithBodyLimitAsync(Func<Task<IResult>> action, string tooLargeMessage)`.
     - Problem results: `NotFound(string detail, HttpContext? ctx = null)`, `BadRequest(string code, string detail, HttpContext? ctx = null)`, `Forbidden(HttpContext? ctx = null)`, `ServiceUnavailable(string detail, HttpContext? ctx = null)`, `Problem(int status, string code, string detail, HttpContext? ctx = null)`. They produce the same ProblemDetails shape and trace id as `ApiProblems`.
   - New `src/Autheris.Modules.Abstractions/ModuleErrorCodes.cs`: string constants for the codes modules use (`ResourceNotFound` plus the codes used by `StreamingCdcEndpoints`/`CdcStreamAdminEndpoints`). Each value equals the `ApiErrorCodes` constant of the same name, and an architecture test enforces this.
   - New `src/Autheris.Modules.Abstractions/ModuleEndpointConventions.cs`: `WithModuleAudit<TBuilder>(AuditLevel, string eventType)` adds `AuditPolicy` metadata, and `WithModuleRequestBodyLimit<TBuilder>(long maxBytes)` adds `RequestSizeLimitAttribute`. `AuditEndpointConventions.WithAudit` and `RequestBodyLimitExtensions.WithRequestBodyLimit` (Api) forward to these, so the metadata is built in one place. An endpoint-level audit overrides the registrar group default because it is added later.
   - Implementation: `src/Autheris.Api/Hosting/ModuleRequestContext.cs` (sealed, stateless) delegates to `EndpointSecurity` and `ApiProblems`. It is registered as a singleton next to `ModuleHostContext`, always, even when no module is enabled. Modules get it by minimal-API parameter injection.
   - Api endpoint files keep `EndpointSecurity`, `ApiProblems`, `ApiErrorCodes`, `WithAudit` and `WithRequestBodyLimit` unchanged. Only code under `modules/**` uses the new surface. Lakehouse drops its private copy (`IcebergRestCatalogEndpoints.cs:143-175`, `GetRequestTenant`) for `IModuleRequestContext.GetRequestTenant`. `RequireTenant` keeps its whitespace check.
2. **dbt Cloud webhook: option (a) (default taken).** Per the dbt Cloud webhook docs (not re-fetched here; the fetch tool failed), dbt Cloud signs the body with HMAC-SHA256 and sends the hex digest in the `Authorization` header. It cannot add a bearer token or custom headers, so (b) would need a relay and is rejected.
   - The route becomes core and anonymous: `POST /api/webhooks/dbt-cloud` in `src/Autheris.Api/Endpoints/WebhookEndpoints.cs` (new path; breaking). It keeps `AllowAnonymous`, the 2 MB `WithRequestBodyLimit`, and `WithAudit(Full, AuditConfigChanged)`.
   - The handler runs these steps in order:
     1. Resolve `IDbtCloudWebhookHandler?` from DI. If it is null, return 404 before reading headers or the body.
     2. Take the signature header in this order: `X-Dbt-Signature`, `X-Hub-Signature-256`, then `Authorization` when it is exactly 64 hex characters with no scheme. If none is present, return 401.
     3. Bounded 2 MB read (`EndpointSecurity.TryReadBodyAsync`).
     4. Verify the signature with core `DbtCloudWebhookSignature.Verify(payload, header, secret, bypass)`, using the secret from `GatewayOptions.Dbt.WebhookSecret` or the `DBT_WEBHOOK_SECRET` fallback. On failure, return 401.
     5. Call `handler.HandleVerifiedAsync(payload, ct)`.
   - New core files: `Application/Catalog/Semantic/IDbtCloudWebhookHandler.cs` (`Task<DbtWebhookProcessingResult> HandleVerifiedAsync(string payload, CancellationToken ct)`) and `Application/Catalog/Semantic/DbtCloudWebhookSignature.cs`. The second is `DbtWebhookReceiver.ValidateSignature` moved unchanged: constant-time compare, optional `sha256=` prefix.
   - Module side: `DbtWebhookReceiver` implements `IDbtCloudWebhookHandler` and becomes internal. It keeps JSON parsing, the 5-minute skew window and event-id dedup, and loses the signature check (one verifier, in core). `IDbtWebhookReceiver` is deleted.
   - Security reasoning: the anonymous surface is unchanged from today (one route, the same checks). Signature verification and the body cap run in core before any module code. An absent module answers 404 without reading the body. The registrar invariant (no anonymous module route; `VerifyModuleEndpoints` unchanged) holds because this route is core-owned.
3. **Routes (breaking, D-50k).** The module maps `registrar.MapGroup("/modules/dbt")` with relative routes: `sync`, `governance`, `exposures`, `proposals`, `proposals/{id:guid}/approve`, `proposals/{id:guid}/reject`, `validate-contract`, `run-results`, `health`, `health/reset`, `semantic-manifest`, `metrics` (12 routes). The 13th route is the core webhook from decision 2. The old `/api/extensions/dbt/*` paths return 404. Each route keeps its per-endpoint audit level and body limit, now set through `ModuleEndpointConventions`. The explicit `RequireAuthorization()` calls go away because the registrar adds them.
4. **Module shape.** `modules/Autheris.Modules.Dbt{,.Tests}` are copies of the Lakehouse csproj files. References: Abstractions, Domain, Application; no Extensions or Infrastructure. `DbtModule`: `Id = "dbt"`, `ConfigurationSections = ["Gateway:Dbt"]`. `ConfigureServices` takes the current `AddDbtIntegration` body plus the three moved registrations (inventory). Namespace `Autheris.Modules.Dbt`. `MapDbtEndpoints` and `AddDbtIntegration` are deleted from core.

### PR slicing (replaces §9 item 3; each PR is green on its own)
- **PR 3a: helper surface** (about 250 lines, reusable by WP-SIMPL-29 PR 3). Decision 1 plus the Lakehouse migration. No route or behavior change.
  - Tests: `tests/Autheris.Tests.Unit/Hosting/ModuleRequestContextTests.cs`:
    - `Request_tenant_matches_endpoint_security` [Security]
    - `Malformed_tenant_claim_fails_closed` [Security]
    - `Admin_checks_match_endpoint_security` [Security]
    - `Body_over_limit_returns_413_problem`
    - `Problem_results_match_api_problems`
  - Tests: `ModuleBoundaryArchitectureTests`:
    - `.Modules_do_not_resolve_tenant_from_http_items` [Security]: no `SecurityPrincipalContext.ItemKey`/`TenantIdItemKey` under `modules/**`.
    - `.Module_error_codes_equal_api_error_codes`
    - `.Module_request_context_registered_without_modules`
  - Filter: `FullyQualifiedName~ModuleRequestContextTests|FullyQualifiedName~Lakehouse`.
  - Integration: `LakehouseModuleAbsentSecurityTests` and the Iceberg tenant tests stay unchanged; compile check only.
- **PR 3b: dbt move** (`size-exception: move-only`). Decisions 2-4, tests, baselines, docs paths and CHANGELOG. Depends on 3a.
- **PR 3c: protected files** (owner review, after 3b): `docker-publish.yml` publishes and signs `modules/dbt`, `module-e2e.sh` adds `Enabled__N=dbt` and asserts anonymous `GET /modules/dbt/health` returns 401 and `GET /api/extensions/dbt/health` returns 404, and `CODEOWNERS` covers both dbt module folders. Same pattern as WP-SIMPL-29 WP-29-D.

### PR 3b test and ratchet impact
- `git mv` with names unchanged:
  - From `tests/Autheris.Extensions.Tests/` to `modules/Autheris.Modules.Dbt.Tests`: `DbtTests`, `DbtCompositeRelationTests{,.SourceRelations}`, `DbtPhysicalNameTests`, `DbtSourceRelationTests`, `DbtSyncTestExtensions`, `DbtSyntheticSamplesSecurityTests`.
  - From `tests/Autheris.Tests.Unit/Dbt/*`: all files except `PersistentSemanticCatalogTests`, `SemanticCatalog*`, `SemanticManifestValidatorTests` and `ScimDataOwnerIdentityVerifierTests` (core types). Also `Unit/Api/{DbtSemanticManifestEndpointTests,DbtSyncEndpointCreationTests}`.
  - Mixed classes stay in Unit with a module ProjectReference (Lakehouse precedent): `ExtensionsSecurityAuditRemediationTests`, `Batch5SecurityTestsB`, `Issue504RegressionSecurityTests`, `SecurityReviewRemediationCoverageTests`, `WebhookPreAuthBufferingTests`, `GovernedAggregationAndMetricsTests`, `SemanticMcpDispatchAndTrinoMetadataTests`, `IngestionCallersBatchUpsertTests`, `CatalogWriterEventCountTests`, `CatalogCallerMigrationBatch3Tests`, `ClassificationIngestionSyncTests`, `DiscoveryArtifactServingTests`, `ReadOnlyTokenAggregateTests`.
- Integration, adapt paths to `/modules/dbt/*` and enable module `dbt` (new `DbtWorkspaceTestSettings`, copied from `LakehouseWorkspaceTestSettings`): `DbtIntegrationTests`, `DbtSyncCatalogRegistrationPostgreSqlTests`, `DbtSyncEndpointCallerTests`, `EndToEndSqliteTests`, `IntegrationGapAEndpointMetadataTests`, `TypedAggregationEndToEndTests`. Check the namespaces in `Governance/{CatalogIngestionUnavailableTableTests,Contract/TableRelationContract,Issue390DeclarationPathTests,SecurityReviewRemediationGovernanceTests,VirtualFilterBindingServiceValidationTests,DbtGovernanceOwnerImportTests}`, `WebSqlDbtMappedTableTests` and `IngressExemptionTests`.
- Baselines (`tests/Autheris.Tests.Architecture/Baselines/`):
  - `ingress-exemptions.txt:20-31`: delete 12 lines (module routes carry registrar ingress metadata). Line 32 becomes `/api/webhooks/dbt-cloud`.
  - `readonly-get-routes.txt:46-49`: delete.
  - `readonly-get-head-endpoints.txt:12-15`: rewrite to `/modules/dbt/{exposures,health,metrics,proposals}`.
  - `endpoint-count.txt`: lower to the measured value (expected 328).
  - New `dbt-routes.txt` (12 lines, format of `lakehouse-routes.txt`).
  - `api-extensions-usages.txt`: delete the dbt entries.
  - `source-size-ceilings.txt`: rename the `DbtMetadataIngestionService.cs` path. Rename paths in `test-file-line-ceilings.txt`; update the `unit-test-folders.txt` `Dbt` entry and `datetime-now-counts.txt`.
  - `security-tests-base.txt`/`removed-security-tests.txt`: rename only, no removals.
  - `project-edges.txt`: add `Autheris.Modules.Dbt->{Abstractions,Application,Domain}` and `{Extensions.Tests,Tests.Architecture,Tests.Integration,Tests.Unit}->Autheris.Modules.Dbt`.
- Snapshots: regenerate `Unit/DependencyInjection/Snapshots/composition-{dev,production-like}.txt` (dbt registrations leave core). Check `Unit/Configuration/Snapshots/startup-validator-warnings.txt`.
- Coverage map: rerun `scripts/test-coverage-map.py` and commit `docs/plans/2026-11-05-test-coverage-map.csv`.
- Docs, current behavior only: `docs/extensions/dbt-integration-guide.md`, `docs/endpoints-and-testing.md`, `docs/configuration-guide.md`, `docs/reference/configuration.md`, `deploy/README.md`, `deploy/scripts/test_extensions.py`, `deploy/containers/mock-extensions/mock_server.py`. Also the XML doc path at `DataCatalogOptions.cs:106`.
- CHANGELOG: routes moved, webhook path, module `dbt` must be enabled, `Authorization` header now accepted for the HMAC.

### Mandatory tests (PR 3b)
- `modules/Autheris.Modules.Dbt.Tests/DbtModuleContractTests.cs`:
  - `Dbt_routes_unchanged_when_enabled` (baseline `dbt-routes.txt`)
  - `Dbt_routes_require_authorization` [Security]
  - `Dbt_module_maps_no_anonymous_route` [Security]
  - `Dbt_endpoint_audit_levels_preserved` [Security]
  - `Dbt_body_limits_preserved` [Security]
- `tests/Autheris.Tests.Integration/Modules/DbtModuleAbsentTests.cs` (fixture copied from `LakehouseModuleAbsentSecurityTests`):
  - `Dbt_routes_404_when_absent` [Security] (old and new paths)
  - `Dbt_config_without_module_fails_fast` [Security]
  - `Dbt_cloud_webhook_returns_404_without_module_before_body_read` [Security]
  - `Analytics_metrics_work_without_dbt_module`
  - `GraphQL_queries_work_without_dbt_module`
- `tests/Autheris.Tests.Unit/Api/DbtCloudWebhookEndpointTests.cs` [Security]:
  - `Unsigned_request_returns_401_and_never_reaches_handler`
  - `Invalid_signature_never_reaches_handler`
  - `Authorization_header_hmac_is_accepted`
  - `Authorization_bearer_value_is_not_treated_as_signature`
  - `Body_over_2_mb_returns_413`
  - `Signature_bypass_flag_honored_only_when_set`
- `ModuleParityTests.Dbt_security_tests_migrated` [Security]
- Existing `DbtWebhookReceiverTests` replay and dedup cases stay green in the module. The signature cases move to `DbtCloudWebhookSignatureTests` (core).

### Questions for Phase 3
1. Is the 404-versus-401 difference on the anonymous webhook (it reveals whether the module is loaded) acceptable, or must the absent case also answer 401?
2. Should the `DBT_WEBHOOK_SECRET` environment fallback stay, or should only `Gateway:Dbt:WebhookSecret` count?
3. Accepting an `Authorization` value that is 64 bare hex characters: confirm no authentication handler (JWT, API key) parses it first or logs it.
4. Is it acceptable that the module trusts `HandleVerifiedAsync` callers? It is reachable only through the core route; an architecture test could also forbid other callers.
5. Does `IModuleRequestContext` need `IsTenantBoundGovernanceAdmin` or `RequireTenantRouteMatch` for WP-SIMPL-29 PR 3? Both are left out until a module needs them.

## Security conditions (Phase 3, 2026-11-19, PR 3 rework)
Verdict: **APPROVED WITH CONDITIONS** (verified on `ae42895c1`). Consistent with SEC-SIMPL-02/03/43/48/49, SEC-29-01..09 and the #637 webhook protections.

**Answers.** (1) 404 is required, not 401: SEC-SIMPL-49 mandates 404, and every authenticated module route already reveals module presence (401 vs 404), so the webhook adds no new disclosure. (2) Drop `DBT_WEBHOOK_SECRET`; only `Gateway:Dbt:WebhookSecret` counts (it is covered by options validation and by the `secret` key redaction; no deploy file uses the variable; CHANGELOG entry). (3) Confirmed: `GatewayAuthSchemeSelector` sends a value without a `Bearer `/`Basic `/`Negotiate `/`NTLM ` prefix to the JwtBearer default, which ignores it. `HttpDelegationContextProvider` and `RateLimitingMiddleware` read only `Bearer`/`Basic`, so the request stays under the pre-auth IP limit. `AccessAuditMiddleware` records no headers. (4) Acceptable under SEC-28-06. (5) Not needed: the events endpoints use only `IsCanonicalClusterAdmin` and the tenant. A later addition must be a 1:1 delegation with its own parity test. A tenant-route filter is added as a registrar option, never implemented by a module.

- **SEC-28-01 (absent module).** Without module `dbt`, all 12 `/modules/dbt/*` routes, all 13 old `/api/extensions/dbt/*` routes and `POST /api/webhooks/dbt-cloud` return 404. The webhook decides 404 before it reads any header, the body or `GatewayOptions.Dbt`. `Gateway:Dbt` without the module fails startup. Core keeps no fallback route.
- **SEC-28-02 (no drift).** `ModuleRequestContext` is sealed and stateless, and every member is a single-expression delegation to `EndpointSecurity`/`ApiProblems`: no own logic, caching, try/catch or default values. `SecurityException` from a malformed tenant claim propagates unchanged. The dbt handlers keep every in-handler `IsGlobalGovernanceAdmin`/`IsCanonicalClusterAdmin` check 1:1. Dropping the bare `RequireAuthorization()` is allowed only because all 12 routes use the default policy (verified); no route loses a named policy. Accepted tightening: Lakehouse no longer falls through to `TenantIdItemKey` when the principal context carries a blank tenant, so `RequireTenant` fails closed.
- **SEC-28-03 (protected host context).** Add `IModuleRequestContext` to `ModuleSecurityGuards.ProtectedServiceTypes`. The host registers it before any module `ConfigureServices`, and any module add, replace or remove fails startup naming the module. No type under `modules/**` implements `IModuleRequestContext`. `IDbtCloudWebhookHandler` has at most one descriptor, whose implementation type lives in the `dbt` module assembly; otherwise startup fails.
- **SEC-28-04 (conventions cannot weaken).** `VerifyModuleEndpoints` rejects a module endpoint whose `AuditPolicy.Level` is `Delegated` (a module cannot self-audit) or whose `RequestSizeLimit` is above 100 MiB (today's largest) or not positive. Abstractions exposes no audit-exemption or anonymous convention. The Api forwarders keep their current Api behavior.
- **SEC-28-05 (webhook order).** Exactly the five addendum steps, in order. Missing and invalid signatures return the same fixed 401 body. No JSON parse, module code or handler call happens before `Verify` returns true. `DbtCloudWebhookSignature` is a static core class (not in DI) using `CryptographicOperations.FixedTimeEquals`. A blank secret, non-hex value or wrong length returns false. `sha256=` is accepted only in the two signature headers, never in `Authorization`. The bypass keeps `IsWebhookSignatureBypassed` semantics and its warning log. The route keeps `AllowAnonymous`, the 2 MB limit, `WithAudit(Full, AuditConfigChanged)` and the pre-auth IP limiter, with no exemption.
- **SEC-28-06 (replay, trusted caller).** The module handler keeps the mandatory `timestamp`/`eventId`, the 5-minute skew window, the bounded 24 h dedup (10,000 entries), and the fail-closed result when cluster dedup is unavailable. In `src/**`, only `WebhookEndpoints.cs` references `IDbtCloudWebhookHandler`. The module implementation is `internal` and no module route calls it.
- **SEC-28-07 (confidentiality).** The secret, every signature header value, the `Authorization` value and the payload are never logged, audited, echoed in responses or ProblemDetails, or written to exception messages.
- **SEC-28-08 (tenant source).** Modules derive tenant and principal only from `IModuleRequestContext` (`GetRequestTenant`, `GetSecurityContext`) or `ICallerContextProvider`. They never read them from `HttpContext.Items` keys, raw claims (`GetTenantId`, `FindFirst`), route, query, header or body. This amends SEC-29-02 to allow both sources. The webhook handler takes no tenant or principal from the payload and keeps today's scope.
- **SEC-28-09 (no connection).** The dbt module references only Abstractions, Domain and Application. It does not reference `DbConnection`, `ISqlConnectionFactory`, Infrastructure, `HttpClient`/`IHttpClientFactory` or hosted services (none today); `Modules_open_no_data_source_connections` covers the dbt assembly. The module registers the core `ScimDataOwnerIdentityVerifier`, because a missing verifier silently becomes `NotConfigured` (accepted owners).
- **SEC-28-10 (test migration).** SEC-SIMPL-03 applies: every moved or renamed security test is mapped in the coverage map, `security-tests-base.txt` gets renames only, and the signature cases keep their assertions in `DbtCloudWebhookSignatureTests`.

**Mandatory tests** (`[Trait("Category","Security")]`; Unit/Architecture = local, IT = GitHub Actions only).
- PR 3a, Unit: the three addendum `ModuleRequestContextTests` security cases, plus `Security_context_matches_endpoint_security`, `Caller_identity_matches_endpoint_security` and `Malformed_tenant_claim_propagates_security_exception`; `ModuleSecurityGuardsTests.Module_cannot_replace_request_context`; `ModuleEndpointRegistrarTests.Module_endpoint_with_delegated_audit_fails_load` and `.Module_endpoint_body_limit_above_ceiling_fails_load`; `IcebergRestCatalogTenantTests.Blank_principal_tenant_fails_closed`. Architecture: `ModuleBoundaryArchitectureTests.Modules_do_not_resolve_tenant_from_http_items` (extended to `GetTenantId`/`FindFirst`) and `.No_module_type_implements_module_request_context`. IT: none new (compile check).
- PR 3b, Unit: the addendum `DbtCloudWebhookEndpointTests` plus `Missing_and_invalid_signature_return_identical_401_body`, `Handler_not_resolved_before_signature_verified`, `Non_hex_or_wrong_length_signature_rejected`, `Sha256_prefix_in_authorization_header_rejected`, `Blank_secret_rejects_all_webhooks`, `Environment_secret_fallback_is_ignored` and `Secret_and_signature_never_logged` (capturing logger). Module: existing `DbtWebhookReceiverTests` replay/dedup names unchanged; `DbtModuleContractTests.Dbt_module_registers_owner_identity_verifier`. Unit: `ModuleSecurityGuardsTests.Second_dbt_webhook_handler_fails_startup`. Architecture: `ModuleBoundaryArchitectureTests.Dbt_webhook_handler_referenced_only_by_core_webhook_endpoint` and `.Signature_verifier_uses_fixed_time_compare`. IT: the addendum `DbtModuleAbsentTests` cases, plus `DbtModuleSecurityTests.Anonymous_module_dbt_route_returns_401` and `.Signed_webhook_reaches_handler_when_enabled`.
- PR 3c (CI script, GitHub Actions): `module-e2e.sh` also asserts that unsigned `POST /api/webhooks/dbt-cloud` returns 401 with `dbt` enabled and 404 without it. `docker-publish.yml` signature verification of `modules/dbt` must pass. `CODEOWNERS` adds `DbtCloudWebhookSignature.cs` and `WebhookEndpoints.cs`.

Phase 4 reminder: never put `WP-`, `SEC-` or `OD-` identifiers into comments in `src/Autheris.Api` or `src/Autheris.Application`.

## Changelog
- 2026-11-18: PR 1 (#488 webhook items) was already on `main` (#637: signature header checked before body read, unauthenticated bodies capped at 2 MB, OpenMetadata event-id replay cache, dbt skew and event-id dedup). Re-verified; `OpenMetadataWebhookTests.Replayed_event_is_rejected` and `.Buffer_is_bounded` exist and are green; no code change.
- 2026-11-18: PR 2 done: `ISemanticMetricImport` (core, implemented by the semantic catalogs, consumed by `DbtSemanticManifestIngestionService`); `IDbtHealthCircuitBreaker` core default `NoDbtHealthData`, replaced by `AddDbtIntegration`; `CoreMetricModelArchitectureTests` added. `AnalyticsEndpoints` and `McpSemanticTools` already referenced only `Autheris.Application.Catalog.Semantic`.
- 2026-11-18: Deferred: moving the GraphQL mutation `SyncOpenMetadataAsync` behind `IAutherisGraphQLModule` (the interface does not exist yet; WP-SIMPL-25). It stays in core until the OpenMetadata extraction (PR 4).
- 2026-11-19: PR 3 rework addendum (Phase 2): shared module request helpers (PR 3a), core anonymous HMAC dbt Cloud receiver with module handler, routes under `/modules/dbt`, PR 3b/3c slicing.
- 2026-11-19: Phase 3 security review of the PR 3 rework: APPROVED WITH CONDITIONS (SEC-28-01..10, mandatory tests per PR 3a/3b/3c).
- 2026-11-19: PR 3a done (local): `IModuleRequestContext`/`ModuleErrorCodes`/`ModuleEndpointConventions` in Abstractions, host `ModuleRequestContext` (public sealed forwarder, registered in `AddGatewayModules`, protected), `VerifyModuleEndpoints` rejects delegated audit and body limits above 100 MiB, Lakehouse migrated. Drift: `ModuleSecurityGuardsTests`/`ModuleEndpointRegistrarTests` do not exist; the new tests live in `ModuleLoaderSecurityTests` and `ModuleEndpointSecurityTests`.
- 2026-11-19: PR 3b (dbt move) implemented. Deviations from the addendum, all behavior-neutral: (1) `IModuleRequestContext` has no `HasRole`; the module uses the public `GatewayRoleEvaluator` (the same evaluator `GatewayPolicies.HasRole` wraps), so the surface is unchanged. (2) The core webhook decides module presence with `IServiceProviderIsService` and resolves `IDbtCloudWebhookHandler` only after the signature verified (`Handler_not_resolved_before_signature_verified`). (3) `ModuleSecurityGuards.VerifyDbtCloudWebhookHandler` runs after every module `ConfigureServices` (SEC-28-03). (4) `SemanticManifestValidator.CreateDefault()` replaces the module-side `new SemanticManifestValidator(PolicyParserSetup.CreateParser(), ...)` so the module needs no `TrinoSqlEngine` reference (SEC-28-09 edge set unchanged). (5) `DbtSemanticManifestEndpointTests` (uses internal core `AnalyticsEndpoints`) and `SemanticImportGovernanceHelper`/`SemanticCatalogTestKit` (shared with core tests) stay in Unit; the module tests link the helpers. `Unit/Catalog/Dbt*Tests` stay in Unit with a module reference. (6) `Autheris.Application` gains `InternalsVisibleTo Autheris.Modules.Dbt.Tests` for the linked `CatalogVisibilityTestFactory`. (7) CODEOWNERS now lives in 3b (owner-approved, module folders only) and `Every_src_and_module_folder_has_an_owner` is green.
- 2026-11-19: PR 3c (CI, owner review): `module-e2e.sh` has no "webhook returns 404 without dbt" assertion because it runs a single enabled pass; `DbtModuleAbsentTests` covers it instead. CODEOWNERS relies on its directory rules for `DbtCloudWebhookSignature.cs` and `WebhookEndpoints.cs` (no separate file entries).
