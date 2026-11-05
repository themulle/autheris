# WP-SIMPL-29 Module wave C: events delivery (CDC/Streaming + event webhooks) and Lineage
Status: Ready for implementation
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
