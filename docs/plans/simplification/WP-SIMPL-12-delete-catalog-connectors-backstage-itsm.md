# WP-SIMPL-12 Delete external catalog connectors, Backstage, ITSM and the header break-glass flow
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-12-delete-external-catalog-connectors-backstage-itsm) | Issues: #485 (ITSM part), #491 (part), #507 (break-glass header part), #515 | Owner decisions: OD-5, OD-17(6) | Security conditions: SEC-SIMPL-30, 31, 37, 39, 83, 84; advisories GHSA-9229-wx6j-wvm9 (Backstage part), GHSA-jfrx-2mgc-pfh5 (vendor part) - see private advisories

## 1. Goal and outcome
Remove Purview, Collibra, Alation, DataHub clients, the Backstage export, the ITSM (ServiceNow/Jira) integration and `JustificationAndBreakGlassInterceptor` from core. Outcome: `git grep -il "purview|collibra|alation|datahub|backstage|itsm|JustificationAndBreakGlass" -- src` returns nothing; about -4,000 src lines; every removed route returns 404; every removed config key fails startup.

## 2. Preconditions and dependencies
- Merged first: `fix/security-batch-5` (SEC-SIMPL-84; it touches `DataCatalogSyncService.cs`, `OpenMetadataSyncService.cs`, `ApiErrorCodes.cs`), #504 (security test baseline; the "no security test lost" proof below is measured against it), WP-SIMPL-11 (shared governance repository partials), WP-SIMPL-06.
- Parallel-safe with WP-SIMPL-14/15/16 except for `GatewayApplicationBuilderExtensions.cs`, `UnknownGatewayKeyGuard.cs` and `DeletedFeaturesArchitectureTests` (rebase, append-only lists).
- WP-SIMPL-17 and WP-SIMPL-28 start after this WP.

## 3. Scope
### In scope
- Vendor catalog clients, `DataCatalogClientFactory` vendor branches, vendor webhook handling, Backstage, ITSM, header break-glass/justification interceptor, their options, DI, endpoints, docs, tests, golden SQL files.
### Out of scope
- OpenMetadata adapter and dbt (WP-SIMPL-28); internal catalog (`Application/Catalog`); access-profile break-glass assignments (stay, OD-5); dropping ITSM outbox tables (WP-SIMPL-20e); `GovernanceSchemaShapeVerifier` expected shape (unchanged until 20e).

## 4. Current state (verified at 63cbb893)
- Extensions/DataCatalog: `AlationCatalogClient.cs` (270), `CollibraDataCatalogClient.cs` (136), `PurviewDataCatalogClient.cs` (215), `PurviewQualifiedName.cs` (148), `PurviewTokenCache.cs` (139), `CatalogWebhookHandler.cs` (434), `DataCatalogClientFactory.cs` (43). Application: `Governance/Services/DataHubCatalogClient.cs` (61), `DataCatalog/Interfaces/IDataCatalogClientFactory.cs`, `IDataCatalogWebhookHandler.cs`.
- Backstage: `Extensions/Backstage/{BackstageCatalogExportService(448),BackstageServiceCollectionExtensions(31),BackstageYamlSerializer(125)}.cs`, `Application/Integrations/Backstage/IBackstageCatalogExportService.cs`, `Domain/Model/BackstageModels.cs` (84), `Api/Endpoints/BackstageEndpoints.cs` (138), mapped at `Api/Extensions/GatewayApplicationBuilderExtensions.cs:234`.
- ITSM: `Extensions/Itsm/{ItsmServiceCollectionExtensions(47),ItsmWebhookHandler(769),JiraCloudRestClient(131),ServiceNowTableApiClient(115)}.cs`; `Infrastructure/Itsm/{ConsentRecertificationHostedService(87),ItsmOutboxDispatcherHostedService(192)}.cs`; `Infrastructure/Persistence/ItsmOutboxSqlCore.cs` (136) plus `.Outbox.cs` partials; `Application/Interfaces/{IItsmOutboxRepository,IItsmWebhookHandler,IItsmWorkflowClient}.cs`; `Application/Workflows/ItsmWorkflowDispatcher.cs` (70); `Application/ConsentRequests/ItsmConsentRequestDispatcher.cs` (22); `Domain/Model/ItsmOutboxMessage.cs`; `Domain/Options/Integrations/ItsmOptions.cs` (65); DI `GatewayGovernanceServiceExtensions.cs:59` -> `GatewayGovernanceServiceExtensions.Consent.cs:57 AddItsmConsentRequests`.
- Interceptor: `Application/Extensibility/Interceptors/JustificationAndBreakGlassInterceptor.cs` (176), registered `GatewayCoreServiceExtensions.cs:140` as `IIngressInterceptor`.
- Docs: `docs/extensions/itsm-webhook-guide.md`, `docs/extensions/data-catalog-guide.md` (vendor sections), `docs/features/f-int-01-backstage-software-catalog.md`, `docs/features/p01-data-catalog-connectors.md`, ADR-010 (add a "superseded in part" note only).
- Tests (delete): Unit `BackstageIntegrationTests.cs`, `BackstageTableEntityNameExt15Tests.cs`, `Catalog/PurviewQualifiedNameTests.cs`, `DataCatalog/PurviewTokenCacheTests.cs`, `ItsmClientsAndRecertificationTests.cs`, `ItsmOutboxTests.cs`, `Persistence/DialectSql/ItsmOutboxSqlCoreGoldenTests.cs`, `Webhooks/ItsmWebhookNoLegacyFallbackTests.cs`, `Webhooks/ItsmWebhookRetryExt13Ext14Tests.cs`; Extensions.Tests `ItsmClientTests.cs`, Alation parts of `OpenMetadataReconcileAndAlationHardeningTests.cs` (958; split, keep OpenMetadata cases); Integration `BackstageIntegrationEndpointTests.cs`, `ItsmIntegrationTests.cs`; Architecture golden `Golden/DialectSql/{PostgreSql,SqlServer,Sqlite}.ItsmOutbox.txt`. Review `OptionsBinding/BreakGlassAllowedRolesBindingTests.cs`: keep if it binds access-profile break-glass roles.

## 5. Implementation steps (TDD order)
1. Red: add the security tests of section 6 (they fail while the features exist). Record the security-test inventory baseline: `dotnet test ... --list-tests --filter Category=Security > before.txt` on the #504 merge commit.
2. Delete the interceptor and its DI line (`GatewayCoreServiceExtensions.cs:140`), its options and header constants. Add the header names to a debug-only "ignored legacy header" log (name only, never value) in the ingress pipeline.
3. Delete Backstage files, endpoint mapping (line 234), DI extension call, options section.
4. Delete ITSM: Extensions/Itsm, Infrastructure/Itsm hosted services, `ItsmOutboxSqlCore.cs` and the ITSM methods of the governance repository `.Outbox.cs` partials (keep table DDL until WP-SIMPL-20e), interfaces, dispatchers, `ItsmOptions`, DI `AddItsmConsentRequests`. Consent requests keep their non-ITSM path.
5. Delete vendor catalog clients and `CatalogWebhookHandler` vendor routes; reduce `DataCatalogClientFactory` to OpenMetadata only (or delete the factory if OpenMetadata is resolved directly).
6. Fail-closed config (SEC-SIMPL-31): add to `Api/Configuration/UnknownGatewayKeyGuard.cs` removed sections `Gateway:Itsm`, `Gateway:Backstage`, catalog vendor sections (`Purview`, `Collibra`, `Alation`, `DataHub` under the data-catalog section) and the break-glass/justification interceptor keys, each with message "removed in vX (WP-SIMPL-12, see migration guide)". Fails with any value incl. `false`/empty.
7. Update rate-limit/ingress exemption lists and Helm/deploy ingress for removed routes (SEC-SIMPL-37); remove OpenAPI entries.
8. Delete/trim docs; add migration-guide rows; delete the tests listed in section 4; Green.

## 6. Tests
- Unit `Security/BreakGlassRemovalTests` [Trait("Category","Security")]: `Legacy_justification_and_breakglass_headers_grant_nothing` (same authz and masking result with and without headers); `Access_profile_breakglass_keeps_role_gate_and_audit`.
- Unit `Configuration/RemovedConfigKeysTests` [Security]: `Itsm_keys_fail_startup`, `Backstage_keys_fail_startup`, `Catalog_vendor_keys_fail_startup`, `Breakglass_interceptor_keys_fail_startup` (each with `false` and empty value).
- Integration `RemovedRoutesTests` [Security] (SEC-SIMPL-30): `Removed_routes_return_404_for_admin_and_anonymous` (Theory over Backstage, ITSM webhook, vendor catalog webhook routes), `Removed_routes_absent_from_openapi_and_exemptions`.
- Architecture `DeletedFeaturesArchitectureTests.No_types_in_deleted_namespaces` (create if absent; entries `Autheris.Extensions.Itsm`, `Autheris.Extensions.Backstage`, `Autheris.Infrastructure.Itsm`, `Autheris.Application.Integrations.Backstage`) and `No_type_names_matching` (Purview, Collibra, Alation, DataHub, Itsm, JustificationAndBreakGlass).
- Unit `UnknownGatewayKeyGuardTests`: add one sample per removed section.
- Delete tests of section 4. Security-test proof: `after.txt` minus `before.txt` lists only tests of deleted features; each listed test is mapped in the PR description to either "feature deleted" or its replacement test above.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build`; same for `Autheris.Tests.Architecture`, `Autheris.Extensions.Tests`; Integration filter `FullyQualifiedName~RemovedRoutes|FullyQualifiedName~Consent`.
- `git grep -inE "purview|collibra|alation|datahub|backstage|itsm|JustificationAndBreakGlass" -- src deploy docs ':!docs/plans' ':!docs/migration*'` -> empty.

## 8. Acceptance criteria
- [ ] grep check of section 7 empty; no `Itsm*` options class remains.
- [ ] All section 6 tests green; Security test diff fully mapped in PR description.
- [ ] Removed keys fail startup; removed routes 404; OpenAPI and exemption lists clean.
- [ ] Governance schema shape verifier unchanged; ITSM tables still present (dropped in 20e).

## 9. PR slicing (deletions; added src lines small)
1. PR-A: interceptor removal + BreakGlassRemovalTests + guard keys.
2. PR-B: Backstage + vendor catalog clients + routes tests.
3. PR-C: ITSM (repository partials, hosted services, golden files) + docs + migration guide.

## 10. Risks, rollback, must NOT do
- Do NOT delete access-profile break-glass assignments or the OpenMetadata adapter. Do NOT drop ITSM tables or change the schema shape verifier. Do NOT describe advisory details in PRs (GHSA id + neutral title).
- Risk: consent request flow loses its dispatcher; keep the non-ITSM path covered by existing consent tests. Rollback: revert PR; tag `pre-simplification`.

## 11. Estimate
+250 / -4,000 src, -4,500 tests; 3 person-days.

## Decisions taken in this spec
- D-12a: `DataCatalogClientFactory` kept only if OpenMetadata still resolves through it; otherwise deleted (developer checks consumers).
- D-12b: Legacy break-glass headers are ignored (debug log, name only), not rejected, per SEC-SIMPL-83.
