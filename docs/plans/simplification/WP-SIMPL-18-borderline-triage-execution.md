# WP-SIMPL-18 Borderline triage execution (D2)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-18-borderline-triage-execution-d2) | Issues: #467, #468, #474 (closed here); #470, #484 moved to WP-SIMPL-38; #475, #488 (embeddings part), #501 fixed in core by the security lane | Owner decisions: OD-4, OD-11, OD-15, OD-17 (D2 default), OD-18 | Security conditions: SEC-SIMPL-30, SEC-SIMPL-31, SEC-SIMPL-32, SEC-SIMPL-03

> **Changed by OD-4 and OD-18 (2026-11-05):** catalog search including semantic search and embeddings, the Trino `/v1/statement` route, Procedures and Docs ingestion with the MCP doc tools are core. The former PRs C (Trino), D (Procedures), F (Docs) and G (embeddings) are withdrawn; their cleanup lives in [WP-SIMPL-38](WP-SIMPL-38-trino-protocol-clean.md), [WP-SIMPL-39](WP-SIMPL-39-procedures-clean.md), [WP-SIMPL-40](WP-SIMPL-40-docs-ingestion-clean.md) and the security lane. This WP deletes only differential-privacy noise (A), CDN purge (B) and SchemaRegistry (E). The sections below carry the binding text.

## 1. Goal and outcome
Execute the D2 triage for the items that remain ownerless after OD-4/OD-11/OD-18: differential-privacy noise, CDN cache purge and SchemaRegistry are deleted. Their routes return 404 and their configuration keys fail startup. Governed aggregation (small-group suppression, time buckets), catalog search (BM25 and semantic search with embeddings), the Trino `/v1/statement` route, Procedures, Docs ingestion with the MCP doc tools, the DuckDB table cache, federation and GraphQL trusted documents stay in core and are not touched. Expected net change: about -2,350 src lines, -8 Map* routes (3 DP, 5 SchemaRegistry; CDN purge has no routes).

## 2. Preconditions and dependencies
- Must be merged first: WP-SIMPL-12 (Backstage deletion removes the only Extensions consumer of SchemaRegistry: `src/Autheris.Extensions/Backstage/BackstageCatalogExportService.cs`).
- No owner names a module owner for DP noise, CDN purge or SchemaRegistry by the start of this WP (the lead confirms this in the PR description). If an owner is named, that item is marked MODULE and skipped here; it moves to the module WP of the matching wave (WP-SIMPL-27..29).
- SEC-SIMPL-30/31 shared test classes (`RemovedEndpointsTests`, `RemovedConfigKeysTests`, `UnknownGatewayKeyGuard` entries) from WP-SIMPL-12/14/15/16 are reused. If they are not merged yet, PR A creates them (see step 1).
- Runs in parallel with WP-SIMPL-13 (engine lane), WP-SIMPL-11 (persistence lane) and WP-SIMPL-38/39/40 (whichever merges second rebases on shared baselines; procedure, docs and Trino rows stay in every baseline). WP-SIMPL-24 depends on this WP.

## 3. Scope
### In scope
- PR A, differential privacy (OD-11): delete the noise engine, its budget, endpoints and options. Keep aggregation and suppression.
- PR B, CDN cache purge: DELETE (closes #474 by removal).
- PR E, SchemaRegistry (`Application/SchemaRegistry`, 887 lines, 5 routes): DELETE.
### Out of scope
- Governed aggregation, small-group suppression and time buckets (`Application/Analytics`): KEEP (OD-11).
- Catalog search, BM25 and semantic search with embeddings (`Bm25SearchIndex`, `CatalogSearchEngine`, `ICatalogEmbeddingGenerator`, `NoneEmbeddingGenerator`, `CatalogEmbeddingGeneratorFactory`, `VectorSearchIndex`, `LocalOnnxEmbeddingGenerator`, `OllamaEmbeddingGenerator`, `CatalogSearch:EmbeddingProvider`/`Ollama*` keys): KEEP core (OD-4, OD-18). #501 and the embeddings part of #488 are fixed in core by the security lane; `CatalogSearchSecurityTests` are owned by WP-SIMPL-36.
- Trino client protocol route `/v1/statement` and its queued, executing and cancel routes, `DataSourceTransportFlags.Trino`: KEEP core (OD-18); cleanup and #470/#484 in WP-SIMPL-38.
- Procedures (`Application/Procedures`, routes, MCP procedure tools, `Procedures` options incl. `SqlEndpoints:Procedures:Enabled`): KEEP core (OD-18); cleanup in WP-SIMPL-39.
- Docs ingestion (`Application/Docs`, `DocIngestionEndpoints`, `FileSystemUniversalDocStore`, `McpDocTools`, docs options): KEEP core (OD-18); cleanup and GHSA-p234-g3ph-gqh2 in WP-SIMPL-40.
- DuckDB table cache, `FederatedDuckDbExecutionService`, `CrossSourcePlanner`: KEEP core (OD-15). #475 is fixed in the security lane.
- `TrustedDocumentAdminEndpoints` (3 routes): KEEP. It serves GraphQL trusted documents (`TrustedDocumentsOnlyMiddleware`, `GraphQlAndSwitchStartupChecks`, `GatewayServiceCollectionExtensions.GraphQL.cs`).
- Envoy and TOTP (other D2 rows): core (OD-21, WP-SIMPL-43). Lineage: WP-SIMPL-29.

## 4. Current state (verified at 63cbb893)
- DP: `Application/Governance/Services/DifferentialPrivacyEngine.cs`, `Application/Governance/Interfaces/IDifferentialPrivacyEngine.cs`, `Domain/Model/DifferentialPrivacyModels.cs`. DI: `GatewayGovernanceServiceExtensions.Catalog.cs:91`. Routes: `GovernanceEndpoints.Audit.cs:265` GET `/api/governance/differential-privacy/budget/{clientId}`, `:293` POST `.../budget/{clientId}/reset`, `:345` POST `/api/governance/differential-privacy/perturb` (handler `:535`). Consumers: `GovernedAggregationService.cs:61,82,102,119,138,360,541-556` (`ApplyDifferentialPrivacyNoiseAsync`) and `EuAiActAuditExporter.cs:28,37,50` (`GetBudgetAsync`). Options: `AnalyticsOptions.cs:93` `Noise`, `:103-108` `AnalyticsNoiseOptions` (`Analytics:Privacy:Noise`). Also referenced in `Domain/Model/EuAiActModels.cs`. Tests: `Unit/DifferentialPrivacyTests.cs`, `Unit/Security/DifferentialPrivacyBudgetResetTests.cs`, `Unit/Security/DifferentialPrivacyDistributedBudgetTests.cs`, `Unit/Analytics/DifferentialPrivacyNoiseAndGraphQlMetricsTests.cs`, plus references in `Integration/GovernanceAdvancedMoatsIntegrationTests.cs`, `Unit/DataApi/Issue405GroupKeyAuditServiceTests.cs`, `Unit/Api/RawExceptionMessageNeverReachesClientTests.cs`, `Architecture/NoNewBareErrorBodiesTests.cs`, and the DI snapshots `composition-dev-sqlite.txt` and `composition-production-like.txt`.
- CDN: `Application/Caching/Interfaces/ICdnCachePurgeService.cs`, `Domain/Model/CdnCacheModels.cs`, `Domain/Options/CdnOptions.cs` (`Enabled`, `PurgeMode`, `Cloudflare`, `Fastly`; no `Provider` key), `GraphQL/Interceptors/CdnCacheTagMiddleware.cs`, `GraphQL/Interceptors/CdnCacheTagVisitor.cs`, `Infrastructure/Cdn/CloudflareCdnPurgeService.cs`, `Infrastructure/Cdn/FastlyCdnPurgeService.cs`. Section `Gateway:Caching:Cdn` (`GatewaySections.cs:66`), bound through `CachingOptions.Cdn` (`Domain/Options/Storage/RedisOptions.cs:11`). DI: `GatewayServiceCollectionExtensions.GraphQL.cs:128-129`; `using Autheris.Infrastructure.Cdn` in `.cs`, `.Options.cs` and `.GraphQL.cs`. Tests: `Unit/GraphQL/StartedResponseGql2Tests.cs` (CDN parts) and `Architecture/Baselines/unconsumed-options.txt`.
- SchemaRegistry: `Application/SchemaRegistry` (13 files, 887 lines), `Api/Endpoints/SchemaRegistryEndpoints.cs` (5 routes), `Infrastructure/Persistence/InMemorySchemaRegistryRepository.cs`. Wiring in `GatewayCoreServiceExtensions.cs`, `GatewayServiceCollectionExtensions{,.GraphQL,.Options}.cs`. Tests: `Integration/SchemaRegistryEndpointSecurityTests.cs`, `Unit/SchemaRegistryAndLinterTests.cs`.

## 5. Implementation steps (one PR per item; TDD order inside each)
1. Red, per PR: add the removal tests from section 6 first. They fail while the code is still present. Reuse the shared `RemovedEndpointsTests` / `RemovedConfigKeysTests` classes. If absent, create `tests/Autheris.Tests.Integration/Removal/RemovedRouteAssertions.cs` with `Task AssertRemovedAsync(HttpClient admin, HttpClient anon, HttpMethod m, string path)`. It asserts 404 for both clients, absence from `/openapi` JSON and absence from the ingress metadata (SEC-SIMPL-30).
2. Green, PR A (DP): delete the 3 DP files and the 3 routes with their handler. Remove the `_dpEngine` field, both ctor params and `ApplyDifferentialPrivacyNoiseAsync` from `GovernedAggregationService` (the `dpEngine` parameters are optional, so callers compile). Keep suppression and buckets unchanged. In `EuAiActAuditExporter`, remove the dependency and the budget section, and drop the matching fields from `EuAiActModels.cs`. Delete `AnalyticsNoiseOptions` and `AnalyticsOptions.Noise`. Fix the XML doc on `AnalyticsOptions.cs:87-88` so it no longer says "enable the noise option". Add `Analytics:Privacy:Noise` to `UnknownGatewayKeyGuard` with the message "removed (OD-11); small-group suppression applies". SEC-SIMPL-32: the PR states that `Noise:Enabled` was a protection toggle; suppression stays unconditional.
3. PR B (CDN): delete the 7 files, the DI lines, the GraphQL middleware registration and `CachingOptions.Cdn`. Guard `Gateway:Caching:Cdn` with all children (`Enabled`, `PurgeMode`, `Cloudflare:*`, `Fastly:*`).
4. PR E (SchemaRegistry): delete the folder, the endpoints, `InMemorySchemaRegistryRepository` and the wiring. Guard the options section.
5. Per PR: regenerate the DI composition snapshots and the `source-size-ceilings.txt` / `unconsumed-options.txt` / `datetime-now-counts.txt` baselines (only removals; procedure, docs and Trino rows stay). Update the generated configuration reference and the endpoint inventory.

## 6. Tests
New tests, all `[Trait("Category","Security")]`, in `tests/Autheris.Tests.Integration/Removal/BorderlineRemovalTests.cs`:
- `DifferentialPrivacy_routes_return_404` (3 routes, admin and anonymous), `Cdn_has_no_routes_and_no_outbound_client` (DI: no `CloudflareCdnPurgeService`/`FastlyCdnPurgeService` HttpClient), `SchemaRegistry_routes_return_404` (5), `TrustedDocument_admin_routes_still_require_admin` (guards the kept item). The 404 rows are also added to the shared `RemovedEndpointsTests.Removed_routes_return_404_for_admin_and_anonymous`.
- In `tests/Autheris.Tests.Unit/Configuration/BorderlineRemovedKeysTests.cs`: `[Theory] Removed_key_fails_startup(string key, string value)` with `InlineData` for `Analytics:Privacy:Noise:Enabled=false`, `Gateway:Caching:Cdn:Enabled=false`, `Gateway:Caching:Cdn:Cloudflare:ApiToken=x` and the SchemaRegistry section. Asserts the startup exception names the key (SEC-SIMPL-31). The same rows are appended to `RemovedConfigKeysTests.Every_removed_key_fails_startup`.
- Unit: `GovernedAggregationSuppressionTests.Small_groups_are_suppressed_without_noise` (OD-11 keeps suppression). `AggregationPrivacyTests.Small_groups_suppressed_without_recovery_by_subtraction_and_threshold_floor_enforced` (SEC-SIMPL-89) stays green.
- Delete: the 4 DP test files, `SchemaRegistryEndpointSecurityTests.cs`, `SchemaRegistryAndLinterTests.cs`, and the CDN parts of `StartedResponseGql2Tests.cs`. Edit the shared files listed in section 4. SEC-SIMPL-03: every deleted security test is listed in the PR as "obsolete by removal" with the feature it covered.
- Unchanged and green: Trino protocol, procedure, docs-ingestion, MCP doc-tool, catalog-search and embedding tests.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror` (one build at a time).
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build`
- `dotnet test tests/Autheris.Tests.Architecture -c Release --no-build`
- `dotnet test tests/Autheris.Tests.Integration -c Release --no-build --filter "FullyQualifiedName~BorderlineRemovalTests|Category=Security"`
- `git grep -n -E 'DifferentialPrivacy|CdnCachePurge|Autheris.Application.SchemaRegistry' -- src` must return nothing.
- `test ! -d src/Autheris.Application/SchemaRegistry`

## 8. Acceptance criteria
- [ ] All grep checks in section 7 are empty, and the SchemaRegistry folder is gone.
- [ ] Every removed route returns 404 for admin and anonymous clients and is absent from OpenAPI (SEC-SIMPL-30).
- [ ] Every removed key fails startup, even with `false` or empty values (SEC-SIMPL-31).
- [ ] Governed aggregation suppression tests, catalog search tests (BM25 and embeddings), Trino protocol, procedure, docs-ingestion and MCP doc-tool tests, trusted-document tests and DuckDB table cache tests stay green and unchanged.
- [ ] The section 5 table in the plan lists DP noise, CDN purge and SchemaRegistry as deleted; #467, #468 and #474 are closed as obsolete by removal (closing is done by the lead).
- [ ] Architecture baselines only shrink; the Release build has 0 warnings.

## 9. PR slicing
Order: A (DP), B (CDN), E (SchemaRegistry). Each PR is a pure deletion plus removal tests and adds under 150 src lines. Each PR is independently revertible. (PR letters keep the original numbering; C, D, F and G are withdrawn.)

## 10. Risks, rollback, must NOT
- Risk: a shared baseline edit drops a procedure, docs or Trino row. Mitigation: baselines only shrink by the deleted items; WP-SIMPL-38/39/40 rebase on them.
- Rollback: revert the single PR. No schema migration is involved.
- Must NOT: delete aggregation suppression or time buckets, catalog search (BM25 or embeddings), the Trino route, Procedures, Docs ingestion or MCP doc tools, `TrustedDocumentAdminEndpoints`, the table cache or federation. Do not guard keys of kept features (`SqlEndpoints:Procedures:Enabled`, `CatalogSearch:*`, docs or Trino keys). Do not keep "disabled" stubs or flags for deleted features. Do not loosen any baseline. Do not put advisory details in PRs.

## 11. Estimate
Src: DP about -900, CDN about -400, SchemaRegistry about -1,050; total about -2,350 src, +about 150 test, -about 1,500 test. 1.5 person-days.

## Decisions taken in this spec
1. No module owner was named for DP noise, CDN purge and SchemaRegistry, so they are deleted (PRD D2 default, OD-17).
2. OD-4/OD-18: former PRs C (Trino `/v1/statement`), D (Procedures), F (Docs ingestion) and G (embeddings) are withdrawn; those features are core and cleaned by WP-SIMPL-38, 39, 40 and the security lane.
3. `TrustedDocumentAdminEndpoints` stays (it serves GraphQL trusted documents).
