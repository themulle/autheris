# WP-SIMPL-29 Module wave C: events delivery (CDC/Streaming + event webhooks) and Lineage
Status: Phase 2 rework done (addendum 2026-11-19); Phase 3 review of the addendum pending
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-29-module-wave-c-events-delivery-cdcstreaming--webhooks-and-lineage) | Issues: #515, #476, #503 (OpenLineage part) | Owner decisions: OD-10 (GDPR disclosure report stays core), OD-15, OD-22 (controls on every transport), OD-24 (Lakehouse pilot), OD-29 (workflow webhook is core), OD-30 (single access decision point), OD-31 (event webhooks move, workflow webhook stays core) | Security conditions: SEC-SIMPL-03, 05, 30, 31, 43, 44, 48, 49; SEC-AZ-01; GHSA-qx32-576x-5cq2 (GDPR disclosure report)

> **Changed by OD-29/OD-31 and WP-SIMPL-47:** the webhook primitives `WebhookSignature`, `WebhookDeliveryClient` (egress guard), `IWebhookSecretProtector` and `WebhookRetryPolicy` stay core because the core consent dispatcher (WP-SIMPL-47) uses them; only CDC/streaming, event subscriptions, the event bridge and the event outbox move. This WP is the last module WP and owns the removal of the `Autheris.Api` -> `Autheris.Extensions` project reference.

## 1. Goal and outcome
Move CDC/Streaming, event-webhook delivery and Lineage impact analysis/OpenLineage export out of the core into `modules/Autheris.Modules.Events` and `modules/Autheris.Modules.Lineage`. Core `src/` shrinks by about 9,500 lines and 11 routes (events 10, OpenLineage sync 1); the core image starts without these modules; the full image serves the same routes with the same tests green; streamed rows keep row filter and masking via `IAccessDecisionPoint`. The GDPR disclosure report (computation, exception, exporter and its REST and GraphQL entry points) stays in core (OD-10). The last PR removes the `Autheris.Api` -> `Autheris.Extensions` project reference.

## 2. Preconditions and dependencies
- Merged first: WP-SIMPL-25 (module host, SEC-SIMPL-40..47 tests green; defines `IGovernanceConnectionFactory` and `IAutherisGraphQLModule`), WP-SIMPL-27 (Lakehouse pilot, runtime-vs-fallback exit decision), WP-SIMPL-46 (`StreamRlsPolicyEnforcer` decides via `IAccessDecisionPoint.DecideAsync(Streaming)`), WP-SIMPL-20 (unified governance repository), WP-SIMPL-24 (Application folders; `Events` folder is moved here).
- Fixes that must land before or inside this WP (plan R6): #476 (CDC custom query poller read-only on all supported engines) and the OpenLineage part of #503. The GHSA-qx32-576x-5cq2 fix (WP-SIMPL-36 sub-package 0) must be merged in core before PR 1.
- The last PR (Api -> Extensions reference removal) additionally needs WP-SIMPL-12 (Backstage/ITSM/DataCatalog deleted), WP-SIMPL-26 (OData moved out of Extensions), WP-SIMPL-27 and WP-SIMPL-28 merged.
- May run in parallel with WP-SIMPL-28 (disjoint folders) except the last PR; not with WP-SIMPL-32/33.

## 3. Scope
### In scope
- Events module: `src/Autheris.Application/Streaming/**` (29 files, 4,440 lines), `src/Autheris.Extensions/Cdc/**` (14 files, 2,296), `src/Autheris.Application/Webhooks/{WebhookDispatcherService, IWebhookDispatcherService, WebhookDeliveryHostedService, WebhookEventBridgeHostedService, WebhookSubscriptionResolver, IWebhookOutbox}.cs`, `src/Autheris.Application/Events/Services/{CdcWebhookEventId,CdcWebhooksOptionsValidator,WebhookOutboxBoundsException,CdcSubscriptionPrincipal}.cs`, Api: `StreamingCdcEndpoints.cs` (4 `Map*`), `CdcStreamAdminEndpoints.cs` (4), `TableStreamEndpoints.cs` (2 routes plus the group), `Extensions/DependencyInjection/CdcWebhooksFeature.cs`, `Hosting/CdcWebhookStartupCheck.cs`, their options classes.
- Stay core (WP-SIMPL-47, OD-29, OD-31): `WebhookSignature`, `WebhookDeliveryClient`, `IWebhookSecretProtector`, `WebhookRetryPolicy`. The events module uses them from `Autheris.Application`.
- Store: CDC stream and event-webhook outbox persistence (`CdcStreamSqlCore`, `WebhookOutboxSqlCore` and dialect partials) move into the module and use `IModuleHostContext.GovernanceConnections` (`IGovernanceConnectionFactory`, governance store only, defined by WP-SIMPL-25; plan default Q5). Migration steps for their tables stay in the core forward-only migration runner (schema is versioned by the host).
- GDPR disclosure report to core `src/Autheris.Application/Compliance/`: `IGdprAuditReportExporter` (today `Application/Interfaces/`), `GdprAuditReportPdfExporter`, `GdprSubjectScopeUnresolvedException`, and the GDPR disclosure computation extracted from `LineageImpactAnalyzerService.GetGdprDataDisclosureReportAsync` (`:449` to end, with every private helper it uses) behind a new core `IGdprDisclosureReportService`.
- Lineage module: the rest of `src/Autheris.Application/Lineage/**` (`LineageImpactAnalyzerService` impact analysis, `ConsumerFolding`), `ILineageImpactAnalyzerService` without the GDPR method, `src/Autheris.Extensions/Lineage/**` (3 files, 447 lines: OpenLineage/OpenJEV clients), the route `POST /api/lineage/openlineage/sync` (`GovernanceEndpoints.Audit.cs:447`), the GraphQL lineage fields `GetTableConsumersAsync` (`GraphQL/Types/Query.Catalog.cs:357-404`) and `CalculateConsentRevocationImpactAsync` (`GraphQL/Types/Query.Consent.cs:226-234`) via `IAutherisGraphQLModule`.
- Removal of the `Autheris.Api.csproj:8` reference to `Autheris.Extensions` (last PR) and the architecture test `PackageBoundaryArchitectureTests.Api_does_not_reference_Extensions`.
- Move of the corresponding tests to `modules/Autheris.Modules.{Events,Lineage}.Tests`.
### Out of scope
- GDPR disclosure report (stays core, OD-10); webhook primitives listed above (stay core); the consent dispatcher (WP-SIMPL-47).
- `WebhookEndpoints.cs`: the OpenMetadata receiver moves with WP-SIMPL-28; the ITSM and catalog receivers are deleted by WP-SIMPL-12. `IDataCatalogWebhookHandler`, `IDbtWebhookReceiver` (WP-SIMPL-28); `IItsmWebhookHandler` (deleted in WP-SIMPL-12).
- Table cache / DuckDB (core, OD-15); audit chain.
- Any behaviour change besides route-group wrapping by the host and the GDPR extraction.

## 4. Current state (verified on 63cbb893; re-checked on 88e5ab74)
- Routes moving: StreamingCdcEndpoints 4, CdcStreamAdminEndpoints 4, TableStreamEndpoints 2 (`:61`, `:67`; group at `:57`) = 10 events routes; OpenLineage sync 1. `WebhookEndpoints.cs` (6 inbound routes) is not event delivery (see out of scope).
- Lineage: `Application/Lineage` 4 files (1,561 lines on 88e5ab74: `ConsumerFolding.cs`, `GdprAuditReportPdfExporter.cs`, `GdprSubjectScopeUnresolvedException.cs`, `LineageImpactAnalyzerService.cs` with the GDPR computation from `:449`); `Extensions/Lineage` 3 files 447 lines.
- GDPR report wiring: `GatewayGovernanceServiceExtensions.Audit.cs:57-58` registers `ILineageImpactAnalyzerService` and `IGdprAuditReportExporter`; REST `GovernanceEndpoints.Audit.cs:382` injects `ILineageImpactAnalyzerService`, `:423` calls `GetGdprDataDisclosureReportAsync`, `:425` catches `GdprSubjectScopeUnresolvedException`; GraphQL `Query.Compliance.cs:21,59,61,76` does the same.
- Webhook primitives: `Application/Webhooks/WebhookDeliveryClient.cs` (egress guard), `WebhookSignature.cs`, `IWebhookSecretProtector.cs`, `WebhookRetryPolicy.cs` (stay core).
- Remaining `src/Autheris.Extensions` content besides the moving folders: `BoundedHttpContent.cs`, `ExtensionRegistration.cs`, `ExtensionsServiceCollectionExtensions.cs` (`AddGatewayExtensions`), `Governance/IGovernanceWorkflowHook.cs`; `Backstage`, `DataCatalog`, `Itsm` are deleted by WP-SIMPL-12.

## 5. Implementation steps
1. Red: add the absent-module tests (section 6, `EventsModuleAbsentTests`, `LineageModuleAbsentTests`) in `tests/Autheris.Tests.Integration` against the core host; they fail today because routes exist.
2. GDPR report to core (PR 1): create `src/Autheris.Application/Compliance/` (namespace `Autheris.Application.Compliance`); move `IGdprAuditReportExporter`, `GdprAuditReportPdfExporter`, `GdprSubjectScopeUnresolvedException`; extract `GetGdprDataDisclosureReportAsync` and the private helpers it uses unchanged into `GdprDisclosureReportService : IGdprDisclosureReportService`; remove the method from `ILineageImpactAnalyzerService`. Switch `GovernanceEndpoints.Audit.cs:382-429` and `Query.Compliance.cs` to `IGdprDisclosureReportService`. Existing GDPR tests stay in Unit with unchanged names.
3. Create `modules/Autheris.Modules.Events` (csproj referencing only `Autheris.Modules.Abstractions`, `Autheris.Domain`, `Autheris.Application`), `EventsModule : IAutherisModule` with `Id = "events"`, `ConfigurationSections = ["Gateway:Cdc", "Gateway:CdcWebhooks", "Gateway:Streaming", "Gateway:Webhooks"]` (verify exact section names from the moved options' `SectionName` constants; a section also read by the core consent dispatcher stays core), `ConfigureServices` = body of `CdcWebhooksFeature` plus streaming registrations, `MapEndpoints` = the three endpoint files through the host registrar (SEC-SIMPL-43).
4. `git mv` the in-scope files; fix namespaces to `Autheris.Modules.Events.*`; remove registrations from `CdcWebhooksFeature` callers in the Api; `CdcWebhookStartupCheck` becomes a module startup check. The core webhook primitives keep their namespace.
5. Persistence: move `CdcStreamSqlCore`/`WebhookOutboxSqlCore` and their dialect partials into the module; obtain connections via `IModuleHostContext.GovernanceConnections`; keep migration steps in core.
6. Data path: streamed CDC events and table-stream rows keep using the core `StreamRlsPolicyEnforcer`/`IAccessDecisionPoint` path (WP-SIMPL-46); the module constructs no `TableAccessPolicy` and has no private decision path.
7. Lineage (PR 4): `LineageModule` with `Id = "lineage"`; move the remaining Lineage files, the OpenLineage sync route through the registrar, and the two GraphQL lineage fields via `IAutherisGraphQLModule`.
8. Add removed sections to the module-aware branch of `UnknownGatewayKeyGuard` (SEC-SIMPL-49): present but module not loaded -> fail with "module <id> is not installed".
9. Move tests (`git mv`, names unchanged); add coverage-map rows (SEC-SIMPL-03/60) for any rename.
10. Update `source-size-ceilings.txt`, `project-edges.txt`, `namespace-assembly-violations.txt` baselines in the same PR; add modules to `Autheris.sln`, `CODEOWNERS` (named owner, AD-3.11).
11. Api -> Extensions reference removal (PR 5, last; after WP-SIMPL-12/26/27/28): delete the `AddGatewayExtensions` call (`GatewayExecutionServiceExtensions.cs:125`); move each remaining `src/Autheris.Extensions` file that still has a consumer (`BoundedHttpContent`, `ExtensionRegistration`, `IGovernanceWorkflowHook`) into the core project of its consumer, delete the unused ones, delete the empty `Autheris.Extensions` project from `Autheris.sln`; remove `Autheris.Api.csproj:8`; add `PackageBoundaryArchitectureTests.Api_does_not_reference_Extensions`; the `api-extensions-usages.txt` baseline (WP-SIMPL-25) reaches 0 and is deleted.

## 6. Tests
- `tests/Autheris.Tests.Integration/Modules/EventsModuleAbsentTests`: `Cdc_stream_routes_return_404_without_module`, `Table_stream_routes_return_404_without_module`, `Events_config_section_fails_fast_without_module` - [Trait("Category","Security")].
- `EventsModuleAbsentTests.Consent_webhook_dispatch_works_without_events_module` [Security] (core consent dispatcher signs and delivers with the core primitives, WP-SIMPL-47).
- `LineageModuleAbsentTests.Lineage_routes_return_404_without_module`, `.Lineage_graphql_fields_absent_without_module`, `.Gdpr_report_route_still_served_without_lineage_module` [Security] (OD-10).
- `AdvisoryRegressionTests.Ghsa_qx32_5cq2_regression` (Category=Security; details: private advisory) stays green with the Lineage module absent and after the PR 1 extraction.
- `modules/Autheris.Modules.Events.Tests/EventsModuleTests`: `Module_routes_require_authorization`, `Event_delivery_uses_core_webhook_delivery_client` (egress guard applies when loaded as module), `Webhook_signature_secret_not_logged`, `Cdc_custom_query_poller_is_read_only` (#476) - [Security].
- `EventsModuleGovernanceTests` [Security] (OD-22, module loaded): `Cdc_stream_applies_row_filter_and_masking_when_loaded_as_module`, `Table_stream_applies_row_filter_and_masking_when_loaded_as_module`.
- Architecture: `ModuleBoundaryArchitectureTests.Modules_open_no_data_source_connections` covers the new assemblies; `PackageBoundaryArchitectureTests.Events_module_types_do_not_bypass_access_decision_point` (SEC-AZ-01 scan over the Events and Lineage module assemblies), `.Core_has_no_Streaming_or_Lineage_namespace` (new), `.Webhook_primitives_stay_core` (the four primitives exist in `Autheris.Application`), `.Api_does_not_reference_Extensions` (PR 5).
- `ModuleParityTests.Events_security_tests_migrated` and `.Lineage_security_tests_migrated` [Security] (SEC-SIMPL-48/49: every Security test of the pre-move list exists and is green with the module loaded).
- Moved (unchanged names): all tests under `tests/Autheris.Tests.Unit/**/Streaming`, `**/Webhooks` (except tests of the four core primitives), `**/Cdc*`, `**/Lineage*` except GDPR report tests.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build`
- `dotnet test modules/Autheris.Modules.Events.Tests -c Release --no-build`; same for Lineage.
- `dotnet test tests/Autheris.Tests.Architecture -c Release --no-build`
- `dotnet test tests/Autheris.Tests.Integration --filter "FullyQualifiedName~ModuleAbsent|Category=Security" -c Release --no-build`
- `git grep -l "Autheris.Application.Streaming\|Autheris.Application.Lineage" -- src` returns nothing; `git grep -n "Autheris.Extensions" -- src/Autheris.Api` returns nothing after PR 5.

## 8. Acceptance criteria
- [ ] No file under `src/` in the Streaming, Cdc or Lineage folders; `src/Autheris.Application/Webhooks/` contains only `WebhookSignature`, `WebhookDeliveryClient`, `IWebhookSecretProtector`, `WebhookRetryPolicy`.
- [ ] GDPR disclosure computation, exception and exporter live in `Application/Compliance`; REST and GraphQL GDPR entry points work in the core image without the Lineage module.
- [ ] Core host: the 11 moved routes return 404 (auth and anon); events and lineage config keys fail fast.
- [ ] Streamed rows apply row filter and masking with the module loaded (`EventsModuleGovernanceTests` green).
- [ ] Core consent webhook dispatch works without the events module.
- [ ] Full flavor: all moved tests green with unchanged names; Security test count not lower (coverage map).
- [ ] `Autheris.Api` no longer references `Autheris.Extensions` (`Api_does_not_reference_Extensions` green).
- [ ] `src/` LOC reduced by >= 8,000 (measured with the WP-SIMPL-10 script).

## 9. PR slicing
1. GDPR report extraction to `Application/Compliance` (+~250 / -~230).
2. Events module skeleton + host wiring + absent-module and governance tests (<= 400 added src lines).
3. Events code move (git mv, `size-exception` per plan: move-only).
4. Lineage module (move + absent tests; `size-exception`, move-only).
5. Api -> Extensions reference removal + `Api_does_not_reference_Extensions` (after WP-SIMPL-12/26/27/28).

## 10. Risks, rollback, must NOT
- The egress guard and signature handling stay core unchanged; the module only calls them.
- Risk: the GDPR extraction changes report behaviour; mitigated by moving the code unchanged (class/namespace diff only) and keeping the existing GDPR tests and the advisory regression test green.
- Rollback: revert the move PR; module host stays.
- Must NOT: move the GDPR report or the webhook primitives; change route paths; register endpoints on the root builder; let module code obtain a raw data-source `DbConnection` or build its own access decision; drop or rename Security tests without coverage-map rows.

## 11. Estimate
+~850 (module glue, GDPR extraction, tests) / -~9,500 src (moved to modules). 7 person-days.

## Decisions taken in this spec
- CDC/webhook tables: module owns the event store code, schema migrations stay in the core runner (plan default Q5, forward-only migration policy).
- GDPR disclosure computation is extracted into core `IGdprDisclosureReportService` (OD-10), so Lineage moves without a core dependency on the module.
- Webhook primitives stay core for the consent dispatcher (WP-SIMPL-47, OD-29, OD-31).
- This WP owns the Api -> Extensions reference removal as the last module WP.

## Addendum 2026-11-19 (Phase 2 rework)
Verified on `ceee5a75e`. The owner takes the recommended default for every open decision. This addendum replaces the §3 "Store" bullet, §5 steps 3-8, the lineage GraphQL tests in §6, the LOC criterion in §8, §9 and §11. PR 1 (GDPR extraction to `Application/Compliance`) is merged. Binding inputs from merged work: D-50g (no connection-typed member on `IModuleHostContext`; test `ModuleBoundaryArchitectureTests.Module_host_context_exposes_no_governance_connection`), D-50h (no module GraphQL extension point; `.No_module_graphql_extension_point`), D-50k (module routes only under `/modules/{id}/`), `ModuleSecurityGuards.ValidateConfigClaim` (a module claims only `Gateway:<PascalId>`), `.Modules_open_no_data_source_connections` (scans `modules/**`).

### Decisions
1. **(a) Streaming split (default taken).** Rule: code stays in core when it opens a governance or data-source connection, feeds the core table cache (OD-15) or decides access (SEC-AZ-01). Only delivery surfaces move. Nothing moves to Domain: ports belong in Application, the module already references Application, and Domain would gain no consumer. The namespace `Autheris.Application.Streaming` stays as the core change-stream substrate, because renaming it adds churn and does not change the boundary.
   - Core, unchanged: all 13 `Application/Streaming/Interfaces/*` (`ICdcStreamStore`, `ICdcStreamKeyStore`, `ICdcEventChannel`, `IMssqlWatermarkStore`, `ICdcKeyEncryptionKeyProvider`, `ICdcStreamCrypto`, `ICdcPayloadCodec`, `ICdcStreamTailHub`, `ICdcStreamSignal`, `ICdcStreamRestarter`, `IMssqlChangeTrackingPoller`, `IPostgreSqlCdcService`, `IStreamRlsPolicyEnforcer`). Also `CdcExceptions.cs`, `Crypto/*`, `Validation/CustomQueryPollerValidator`, and the services `CdcEventIngestionService`, `CdcStreamCompatFeed`, `CdcStreamEventSource`, `CdcStreamRetentionService`, `CdcStreamSignal`, `CdcStreamTailHub`, `StreamingRowFilterAstEvaluator` and `StreamRlsPolicyEnforcer`. The CDC models are already in `Autheris.Domain.Model`.
   - Core Infrastructure, unchanged: `CdcStreamSqlCore`, `GovernanceRepository.{CdcStreams,CdcStreamKeys,WebhookOutbox}`, `WebhookOutboxSqlCore`, `CloudEventSubscriptionMapping`, `KeyVaultCdcKekProvider`, and all of `Infrastructure/Streaming/*` (`GovernanceDbMssqlWatermarkStore`, `InMemoryCdcEventChannel`, `RedisCdcEventChannel`, `InMemoryCdcStreamStore`, `PostgreSqlLogicalReplicationService`).
   - Core relocation: `src/Autheris.Extensions/Cdc/**` (14 files) moves to `src/Autheris.Infrastructure/Streaming/Producers/**` with namespace `Autheris.Infrastructure.Streaming.Producers[.CustomQuery]`. The producers open data-source connections, so they cannot live in a module. PR 5 needs this move anyway.
   - Move to `modules/Autheris.Modules.Events`: `Application/Streaming/Services/{CdcStreamAdminService,CdcWebhookDeliveryService}`, `Application/Webhooks/{WebhookDispatcherService,IWebhookDispatcherService,WebhookDeliveryHostedService,WebhookEventBridgeHostedService,WebhookSubscriptionResolver}`, `Application/Events/Services/{CdcSubscriptionPrincipal,CdcWebhooksOptionsValidator,CdcCustomHeaders}`, `Domain/Options/CdcWebhooksOptions`, Api `Endpoints/{StreamingCdcEndpoints,CdcStreamAdminEndpoints}`, `Extensions/DependencyInjection/CdcWebhooksFeature`, and `Hosting/CdcWebhookStartupCheck`. Move guard: a listed type moves only when `git grep -lw <Type> -- src` finds no core consumer after the move. Otherwise it stays in core, and the PR description names it. These stay in core: `IWebhookOutbox`, `CloudEvent*`, `CdcWebhookEventId`, `WebhookOutboxBoundsException`, and `TableStreamEndpoints` (bulk ingress on the data API, with no Streaming dependency). That leaves 8 events routes, not 10.
2. **(b) `GovernanceConnections` on `IModuleHostContext`: rejected (default taken; D-50g stays).** The core keeps the stores. The module resolves the core ports (`ICdcStreamStore`, `ICdcStreamKeyStore`, `IWebhookOutbox`) from DI. No raw `DbConnection` crosses the boundary, and tenant scoping stays in the core SQL. Withdrawn: the §3 "Store" bullet and §5 step 5. Migrations stay in the core runner. The unused `Api/Hosting/GovernanceConnectionFactory` is out of scope. If the owner overrides this decision, all of the following apply: the owner reverses D-50g and its test explicitly; the factory binds only `GatewayOptions.GovernanceDb`; it has no data-source types (`Governance_connection_factory_returns_governance_store_only` keeps passing); the connection uses a least-privilege login limited to the module's tables; and every module SQL statement carries the tenant predicate.
3. **(c) GraphQL stays in core (default taken).** This follows D-50h and the WP-SIMPL-28 deferral of 2026-11-18. The CDC `Subscription` is a transport over the core substrate, so it has no absent-module behaviour. Lineage impact analysis stays in core: `ILineageImpactAnalyzerService`, `LineageImpactAnalyzerService`, `ConsumerFolding` and `ILineageGraphStore`. Their consumers are `Query.Catalog.cs:575` and the consent revocation impact in `Query.Consent.cs:235`; `ILineageGraphStore` is also used by MCP and dbt. Follow-up as a new WP, not part of this one: `IAutherisGraphQLModule` with exact type matching, once a module ships GraphQL.
4. **Lineage module = OpenLineage export only:** `Application/Lineage/IOpenLineageClient`, `Extensions/Lineage/{OpenLineageClient,LineageExportServiceCollectionExtensions}`, and the route at `GovernanceEndpoints.Audit.cs:302-307` together with `SyncOpenLineageAsync`.
5. **Routes and config (breaking; allowed because nothing is in production).**
   - Routes: `/api/v1/cdc/{events,subscriptions,subscriptions/{id},streams,streams/{id}/...}` moves to `/modules/events/v1/cdc/...`. `/api/lineage/openlineage/sync` moves to `/modules/lineage/openlineage/sync`.
   - Config: `Gateway:CdcWebhooks` moves to `Gateway:Events:CdcWebhooks`. `Gateway:Catalog:OpenLineageEndpoint|OpenLineageApiKey` moves to `Gateway:Lineage:OpenLineageEndpoint|OpenLineageApiKey`. Each old key gets a removed-key message in `UnknownGatewayKeyGuard` that names the new path.
   - `Gateway:Webhooks`, `Gateway:CdcStream`, `Gateway:PostgreSqlCdc` and `Gateway:MssqlChangeTracking` stay core sections.
   - The module sees only its own config section, so its hosted services read `IOptions<>` at runtime and stay idle when their feature is disabled.
   - Startup fails when `Gateway:Webhooks:Subscriptions` is not empty and module `events` is not loaded.

### PR slicing (replaces §9; each PR is green on its own)
- PR 2a = WP-29-A1, PR 2b = WP-29-A2 (move only), PR 3 = WP-29-B, PR 4 = WP-29-C, PR 4b = WP-29-D (protected files, owner review, after PR 3 and PR 4), PR 5 unchanged.
- PR 3 combines the skeleton, the move, the absent-module tests and the governance tests (`size-exception: move-only`). This avoids the red skeleton (gap 4).
- Moved src lines: about 2,500 (events about 2,100, lineage about 400). This replaces the §8 target of ">= 8,000" and the §11 estimate.

### Work packages
- **WP-29-A1 config guard (core only).**
  - Files: in `Domain/Options/GatewaySections.cs`, add `Events = "Gateway:Events"` and `Lineage = "Gateway:Lineage"`. In `Api/Configuration/GatewayConfigurationSchema.KnownModuleSections`, delete the 3 stale `"cdc"` entries and add `Events->"events"` and `Lineage->"lineage"`. In the override map of `GatewayServiceCollectionExtensions.AddGatewayModules`, add the same 2 entries.
  - Tests in `tests/Autheris.Tests.Unit/Hosting/ModuleHostAndConfigTests.cs`: `Events_section_without_events_module_fails_fast`, `Lineage_section_without_lineage_module_fails_fast`, `Core_cdc_sections_accepted_without_events_module`, `Cdc_sections_are_host_sections_not_module_claimable` (all four [Security]).
- **WP-29-A2 Extensions/Cdc to Infrastructure.**
  - `git mv` per decision 1. Fix the namespaces, the Api callers (`CdcSourceServiceCollectionExtensions`, `CustomQueryPollerServiceExtensions`) and the usings in `tests/Autheris.Tests.Unit/Cdc/*` and `tests/Autheris.Tests.Integration/SqlServerChangeTrackingProducerTests.cs`.
  - Baselines: remove entries from `api-extensions-usages.txt`; rename paths in `source-size-ceilings.txt` and `namespace-assembly-violations.txt`.
  - Filter: `FullyQualifiedName~Autheris.Tests.Unit.Cdc`.
- **WP-29-B events module.**
  - Create `modules/Autheris.Modules.Events{,.Tests}` by copying the Lakehouse csproj files. Add both projects to `Autheris.sln` and a ProjectReference in `Autheris.Tests.Integration.csproj`.
  - `EventsModule`:
    - `Id = "events"`, `ConfigurationSections = ["Gateway:Events"]`.
    - `ConfigureServices` takes over the registrations removed from `GatewayEventServiceExtensions.cs:39-50`, the admin service from `CdcStreamServiceExtensions`, and the CdcWebhooks binding and `CdcWebhookDeliveryService` from `GatewayExecutionServiceExtensions.cs:~142`.
    - `MapEndpoints` calls `registrar.MapGroup("/modules/events")`, and the endpoint classes map `/v1/cdc/...` on that group.
  - Remove `("Gateway:CdcWebhooks", ...)` from `GatewayConfigurationSchema.cs:35` and add the subscriptions startup check from decision 5.
  - Integration tests, new: `tests/Autheris.Tests.Integration/Modules/EventsModuleAbsentTests.cs` (fixture copied from `LakehouseModuleAbsentSecurityTests`) with `Cdc_stream_routes_return_404_without_module` (old and new paths), `Events_config_section_fails_fast_without_module`, `Consent_webhook_dispatch_works_without_events_module` and `Webhook_subscriptions_without_events_module_fail_fast` (all four [Security]). `Table_stream_routes_return_404_without_module` is dropped.
  - Module tests, new: `EventsModuleTests` with the §6 names. `Cdc_custom_query_poller_is_read_only` stays as the core `CustomQueryPollerReadOnlyTests`. `EventsModuleGovernanceTests` gets `Cdc_stream_applies_row_filter_and_masking_when_loaded_as_module` and `Cdc_webhook_delivery_applies_row_filter_and_masking_when_loaded_as_module`. Add `WebhookDeliveryHostedServiceTests.Idles_when_webhooks_and_cdc_webhooks_disabled` and `WebhookEventBridgeHostedServiceTests.Idles_when_webhooks_disabled`.
  - Unit tests to move with `git mv` (names unchanged): `Unit/CdcStream/CdcStreamAdminEndpointsTests`, `Unit/Streaming/{CdcWebhookDeliveryServiceTests,CdcSubscriptionEndpointTests,CdcWebhookSubscriptionGuardTests,CdcReviewFindingsTests}`, `Unit/Webhooks/StreamingCdcEndpointErrorTests`, `Unit/DependencyInjection/CdcWebhookRegistrationTests`. Mixed classes, and any class that contains a forbidden connection identifier, stay in Unit with a module ProjectReference (Lakehouse precedent). Regenerate `Unit/DependencyInjection/Snapshots/composition-*.txt`.
  - Integration tests to adapt: `CdcWebhookEndToEndTests`, `EndToEndSqliteTests` and `IntegrationGapAEndpointMetadataTests` get the new paths, `Gateway:Events:CdcWebhooks:*` and a new `EventsWorkspaceTestSettings` (a copy of `LakehouseWorkspaceTestSettings`). `RealtimeStreamingSubscriptionTests` stays in core.
- **WP-29-C lineage module.**
  - Same pattern as WP-29-B: `Id = "lineage"`, `ConfigurationSections = ["Gateway:Lineage"]`, new `LineageModuleOptions` bound from the module config, and `AddHttpClient<OpenLineageClient>().AddSecureOutboundHandlers(EgressIntegrations.Lineage)` inside the module. Remove `OpenLineage*` from `DataCatalogOptions`.
  - Integration tests in `LineageModuleAbsentTests`: `Lineage_routes_return_404_without_module`, `Lineage_config_section_fails_fast_without_module` and `Gdpr_report_route_still_served_without_lineage_module` (all [Security]). `Lineage_graphql_fields_absent_without_module` is dropped by decision 3.
  - Module test `LineageModuleTests.OpenLineage_client_uses_secure_outbound_handlers` [Security].
- **WP-29-D protected files (separate PR for owner review; diff description).**
  - `.github/workflows/docker-publish.yml`, build job, after the Lakehouse sign block (line about 120): loop `for id in events lineage`. For each id, run `dotnet publish modules/Autheris.Modules.<Pascal>/Autheris.Modules.<Pascal>.csproj -c Release --no-restore -p:Version="$VERSION" -o ./dist/modules/$id`, then `ModuleSigner --dir ./dist/modules/$id --id $id --version "$HOST_VERSION" --host-version "$HOST_VERSION"` (no native trimming).
  - `.github/workflows/docker-publish.yml`, module-e2e job (lines 366-390): rename the step to "Publish Autheris and sign the modules" and add the same loop with `$VERSION`.
  - `.github/scripts/module-e2e.sh`:
    - Add `-e Gateway__Modules__Enabled__1=events -e Gateway__Modules__Enabled__2=lineage` after line 106.
    - After line 137, assert these anonymous calls: `GET /modules/events/v1/cdc/streams` returns 401, `POST /modules/lineage/openlineage/sync` returns 401, `GET /api/v1/cdc/streams` returns 404.
    - Update the header comment on line 2.
  - `.github/CODEOWNERS`, after line 149: add `modules/Autheris.Modules.Events/`, `modules/Autheris.Modules.Events.Tests/`, `modules/Autheris.Modules.Lineage/` and `modules/Autheris.Modules.Lineage.Tests/` with the same owner as the Lakehouse lines.
  - `release.yml:273` already loops over `modules/*` and needs no change.

### Test and ratchet impact (PR 3 / PR 4)
- `ModuleBoundaryArchitectureTests`: no code change; the new projects fall under the existing scans. `.Module_host_context_exposes_no_governance_connection` and `.No_module_graphql_extension_point` stay green.
- New `PackageBoundaryArchitectureTests.Events_module_types_do_not_bypass_access_decision_point` [Security], modeled on `Lakehouse_module_does_not_bypass_access_decision_point` (`:125`). It checks that the Events assembly never constructs or invokes `TableAccessPolicy`. Positive control: `CdcWebhookDeliveryService` calls `IStreamRlsPolicyEnforcer.EvaluateAndMaskAsync`.
- `.Core_has_no_Streaming_or_Lineage_namespace` is replaced by `.Core_has_no_event_delivery_or_openlineage_types` (the moved type names are absent from core assemblies). `.Webhook_primitives_stay_core` stays as planned.
- `CdcRoutesReachNoSourceReaderTests`: the route-class lookup moves to the module assembly.
- Baselines: `ingress-exemptions.txt:60,67-73`, `readonly-get-head-endpoints.txt:50-51` and `readonly-get-routes.txt:27,120` get the new paths. `source-size-ceilings.txt`, `namespace-assembly-violations.txt` and `project-edges.txt` (new edges `Tests.{Unit,Integration,Architecture}->Autheris.Modules.{Events,Lineage}`) are updated in the same PR.
- `UnknownGatewayKeyGuard` module-aware branch (SEC-SIMPL-49): covered by WP-29-A1 plus the absent-module config tests.
- `ModuleParityTests.{Events,Lineage}_security_tests_migrated` list the post-move FQNs. `security-tests-base.txt` follows the WP-SIMPL-27 handling.

### Questions for Phase 3
1. Registrar-forced metadata: `POST /modules/events/v1/cdc/events` now gets REST ingress-transport metadata and the registrar's audit. It was exempt before (`ingress-exemptions.txt:67`). Is that acceptable, or does it need a module exemption path? Do the per-route `GovernanceAdmin` policies survive the registrar?
2. Protected services: should `IStreamRlsPolicyEnforcer`, `ICdcStreamCrypto`, `ICdcStreamKeyStore`, `WebhookDeliveryClient` and `IWebhookSecretProtector` join the SEC-SIMPL-44 snapshot of protected services, so that the module cannot replace the enforcer, the key store or the egress guard?
3. The module resolves core store ports (wrapped DEKs, outbox) directly from DI. Is that acceptable, or must a host-curated accessor narrow it?
4. Egress registered inside a module (`EgressIntegrations.Lineage`, webhook client): is a module-side `AddSecureOutboundHandlers` enough, or must the host verify that every module `HttpClient` carries the SSRF handler?
5. Are the relocated secrets (`Gateway:Lineage:OpenLineageApiKey`, `Gateway:Events:CdcWebhooks:*`) covered by config redaction and dump rules?
6. OD-22 parity: the CDC REST transport is now a module while the GraphQL CDC subscription stays core. Which `CrossTransportParityHarness` case is mandatory with the module loaded?

## Security conditions (Phase 3, 2026-11-19)
Verdict: **APPROVED WITH CONDITIONS** for the addendum (owner defaults taken). Verified on `9486d9eee`. Downstream Phase 4 developers: never put `WP-`, `SEC-` or `OD-` identifiers into comments under `src/Autheris.Api` or `src/Autheris.Application`.

### Answers
1. Registrar-forced ingress metadata and audit: acceptable and stronger; no exemption path (SEC-29-02). `GovernanceAdmin` survives only if the endpoint code keeps its own `RequireAuthorization(GatewayPolicies.GovernanceAdmin)`; the registrar adds the default policy on the group and policies are additive (AND). The decisive risk is different: `ModuleSecurityGuards.ProtectedServiceTypes` contains `IHostedService`, so the events module's hosted services (`WebhookDeliveryHostedService`, `WebhookEventBridgeHostedService`, `CdcWebhookDeliveryService`, `CdcWebhookStartupCheck`) would fail the snapshot count check at startup (SEC-29-03).
2. Yes for the decision-bearing and secret-bearing ports (SEC-29-04). Not `WebhookDeliveryClient` by type (concrete, typed-client registered by the host); protect its egress via SEC-29-06.
3. Direct DI use of core ports is acceptable (D-50g stays): they are tenant-scoped core ports, no raw connection. A curated accessor is not needed if SEC-29-05 holds.
4. Module-side `AddSecureOutboundHandlers` is not enough alone; the host must verify (SEC-29-06).
5. Config keys containing `secret`, `token`, `password` are redacted by `LogRedactionProcessor`; `apikey` is not in that list. Not sufficiently covered (SEC-29-07).
6. Mandatory parity case: SEC-29-09.

### Binding conditions
- **SEC-29-01 (absent module, fail closed).** With module `events` or `lineage` not loaded: every old and new route returns 404 and never 401/200; `Gateway:Events`/`Gateway:Lineage` fail startup; the old keys `Gateway:CdcWebhooks`, `Gateway:Catalog:OpenLineage*` fail startup with the removed-key message. `Gateway:Webhooks:Subscriptions` non-empty without `events` fails startup. The check must not depend on the module's own code and must run before any hosted service starts. Routes are never kept alive in core as a fallback.
- **SEC-29-02 (no private decision path).** Module handlers use the registrar group only (authorization, rate limiter, `WithAudit`, `WithIngressTransport`); `POST /modules/events/v1/cdc/events` is not exempt, and its `ingress-exemptions.txt` entry is deleted, not re-pathed. Every endpoint keeps its per-route policy (`GovernanceAdmin` for the admin routes, the existing policy for ingestion/subscriptions). Row filter and masking go only through `IStreamRlsPolicyEnforcer` (positive control); the module never constructs `TableAccessPolicy`, never reads tenant from the request body, and derives tenant/principal only from `ICallerContextProvider`.
- **SEC-29-03 (hosted-service guard).** Do not remove `IHostedService` from the protected set. Instead allow only additions whose `ImplementationType` is declared in the module's own assembly, never removals or replacements of host descriptors. Anything else keeps failing startup naming module and type.
- **SEC-29-04 (protected snapshot).** Add `IStreamRlsPolicyEnforcer`, `ICdcStreamCrypto`, `ICdcStreamKeyStore`, `ICdcStreamStore`, `ICdcKeyEncryptionKeyProvider`, `IWebhookSecretProtector` and `IWebhookOutbox` to `ProtectedServiceTypes`. They are registered by the host (core), never by the module. `IWebhookSecretProtector` registration moves out of the `CdcWebhooksFeature` block into core and stays registered whenever consent webhooks or `Gateway:Webhooks` are in use; if the secret protector is unavailable, delivery and subscription creation fail closed (`WebhookSecretUnavailableException`), never plaintext.
- **SEC-29-05 (core ports).** The module may reference only these core ports; it must not reference `DbConnection`, `ISqlConnectionFactory`, `GovernanceRepository` or SQL strings (existing `Modules_open_no_data_source_connections` scan stays and is extended with the governance store types). Wrapped DEKs never leave `ICdcStreamCrypto`; the module never sees an unwrapped DEK or KEK.
- **SEC-29-06 (egress).** The host, after module `ConfigureServices`, verifies that every `HttpClient` registration added by a module (`IHttpClientFactory` named/typed builders) has `SsrfProtectionHandler` in its handler chain; otherwise startup fails naming the module. `OpenLineageClient` (`EgressIntegrations.Lineage`) and `WebhookDeliveryClient` (`EgressIntegrations.Webhooks`) keep the same integration names, allowlist, private-range block and DNS-rebinding connect check; redirects stay disabled. Bounds (timeout, response size, retry count, subscription count, custom header count) stay unchanged and are not read from module-supplied config beyond the existing option validators.
- **SEC-29-07 (secrets).** Webhook signing secrets, KEK settings and `OpenLineageApiKey` are never logged, audited, returned by any response or ProblemDetails, or included in exceptions and `ToString`. Add `apikey`, `api-key`, `signing`, `kek` to the redaction key list (config dump, `LogRedactionProcessor`, `ErrorSanitizingFilter`). Module audit details (`BuildModuleAuditDetails`) carry no body or header values. Subscription secrets are stored only wrapped via `IWebhookSecretProtector`; a GET returns no secret.
- **SEC-29-08 (relocation hygiene).** The A2 move is namespace/path only: no behaviour change, read-only custom-query poller guard and `CustomQueryPollerValidator` untouched. Module signing in WP-29-D covers `events` and `lineage`; CODEOWNERS entries equal the Lakehouse owners; the module-e2e job fails when a module is unsigned. No `AllowUnsigned` anywhere in the new workflow steps.
- **SEC-29-09 (parity).** `CrossTransportParityHarness` case `Cdc_rest_and_graphql_subscription_apply_identical_rls_and_masking_with_events_module_loaded` is mandatory: same principal, same stream, same filtered and masked payload on the REST module route and the core GraphQL subscription; and `Cdc_graphql_subscription_unchanged_without_events_module` (core path, no module dependency).

### Mandatory tests (all `[Trait("Category","Security")]`)
Local unit tests unless marked IT (GitHub Actions only).
- **WP-29-A1:** the four named tests, plus `Removed_cdc_webhooks_and_openlineage_keys_fail_with_new_path_message` (SEC-29-01).
- **WP-29-A2:** `Streaming_producers_move_keeps_read_only_poller_guard` (existing `CustomQueryPollerReadOnlyTests` stays green); `Producers_namespace_has_no_module_reference` (architecture).
- **WP-29-B:** IT `Cdc_stream_routes_return_404_without_module`, `Events_config_section_fails_fast_without_module`, `Webhook_subscriptions_without_events_module_fail_fast`, `Consent_webhook_dispatch_works_without_events_module`, `Cdc_ingest_requires_auth_and_audits_under_module` (401 anonymous, audit row written, ingress metadata present); unit `Events_module_hosted_services_allowed_only_from_module_assembly`, `Module_replacing_hosted_service_or_stream_enforcer_fails_startup`, `Module_replacing_secret_protector_or_key_store_fails_startup`, `Cdc_admin_routes_keep_governance_admin_policy`, `Events_module_has_no_data_source_or_governance_connection_reference`, `Webhook_client_chain_has_ssrf_handler_when_events_module_loaded`, `Webhook_signing_secret_not_in_logs_audit_or_responses`, `Events_module_types_do_not_bypass_access_decision_point`, parity case from SEC-29-09 (IT), the two `EventsModuleGovernanceTests` named in the addendum.
- **WP-29-C:** IT `Lineage_routes_return_404_without_module`, `Lineage_config_section_fails_fast_without_module`, `Gdpr_report_route_still_served_without_lineage_module`; unit `OpenLineage_client_uses_secure_outbound_handlers`, `Module_httpclient_without_ssrf_handler_fails_startup`, `OpenLineage_endpoint_to_private_address_is_blocked`, `OpenLineage_api_key_not_in_logs_or_config_dump`, `Lineage_sync_route_requires_governance_admin_and_audits`.
- **WP-29-D:** `ReleaseWorkflowTests.Image_build_rejects_unsigned_module` extended to events and lineage; `Workflow_signs_events_and_lineage_without_allow_unsigned`; `CodeownersTests.Events_and_lineage_module_paths_have_owner`; module-e2e asserts (401 events, 401 lineage sync, 404 old CDC path). Owner review required for the protected files.

## Changelog

- 2026-11-19 PR 2a: event bus moved from Gateway:Events to Gateway:EventBus because Gateway:Events is the events module section; default taken.
