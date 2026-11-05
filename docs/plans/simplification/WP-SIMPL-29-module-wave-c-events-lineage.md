# WP-SIMPL-29 Module wave C: events delivery (CDC/Streaming + Webhooks) and Lineage
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-29-module-wave-c-events-delivery-cdcstreaming--webhooks-and-lineage) | Issues: #515, #476, #503 (OpenLineage part) | Owner decisions: OD-10, OD-15 | Security conditions: SEC-SIMPL-03, 05, 30, 31, 43, 44, 48, 49; GHSA-qx32-576x-5cq2 (GDPR report authorization; see private advisory)

## 1. Goal and outcome
Move CDC/Streaming, webhook delivery and Lineage (minus the GDPR disclosure report) out of the core into `modules/Autheris.Modules.Events` and `modules/Autheris.Modules.Lineage`. Core `src/` shrinks by about 11,000 lines and 16 endpoints; the core image starts without these modules; the full image serves the same routes with the same tests green. The GDPR disclosure report (`GdprAuditReportPdfExporter`, `IGdprAuditReportExporter`, its route in `GovernanceEndpoints.Audit.cs`) stays in core (OD-10).

## 2. Preconditions and dependencies
- Merged first: WP-SIMPL-25 (module host, SEC-SIMPL-40..47 tests green), WP-SIMPL-26 (pilot, runtime-vs-fallback exit decision), WP-SIMPL-20 (unified governance repository), WP-SIMPL-24 (Application folders; `Events` folder is moved here).
- Fixes that must land before or inside this WP (plan R6): #476 (CDC custom query poller read-only on all supported engines) and the OpenLineage part of #503. GHSA-qx32-576x-5cq2 fix must be merged in core before the Lineage move.
- May run in parallel with WP-SIMPL-27, 28, 30 (disjoint folders); not with WP-SIMPL-32/33.

## 3. Scope
### In scope
- Events module: `src/Autheris.Application/Streaming/**` (29 files, 4,440 lines), `src/Autheris.Extensions/Cdc/**` (14 files, 2,296), `src/Autheris.Application/Webhooks/**`, `src/Autheris.Application/Events/Services/{CdcWebhookEventId,CdcWebhooksOptionsValidator,WebhookOutboxBoundsException,CdcSubscriptionPrincipal}.cs`, Api: `WebhookEndpoints.cs` (6 Map*), `StreamingCdcEndpoints.cs` (4), `CdcStreamAdminEndpoints.cs` (4), `TableStreamEndpoints.cs` (2), `Extensions/DependencyInjection/CdcWebhooksFeature.cs`, `Hosting/CdcWebhookStartupCheck.cs`, their options classes.
- Store: CDC stream and webhook outbox persistence (`CdcStreamSqlCore`, `WebhookOutboxSqlCore` and dialect partials) move into the module and use the `IModuleHostContext` governance connection factory (plan default, Q5). Migration steps for their tables stay in the core forward-only migration runner (schema is versioned by the host).
- Lineage module: `src/Autheris.Application/Lineage/**` except `GdprAuditReportPdfExporter.cs`, `src/Autheris.Extensions/Lineage/**` (447), `ILineageImpactAnalyzerService`, GraphQL lineage fields via `IAutherisGraphQLModule`.
- Move of the corresponding tests to `modules/Autheris.Modules.{Events,Lineage}.Tests`.
### Out of scope
- GDPR disclosure report (stays core, OD-10); `IDataCatalogWebhookHandler`, `IDbtWebhookReceiver` (WP-SIMPL-28); `IItsmWebhookHandler` (deleted in WP-SIMPL-12).
- Table cache / DuckDB (core, OD-15); audit chain.
- Any behaviour change besides route-group wrapping by the host.

## 4. Current state (verified on 63cbb893)
- Map* counts: WebhookEndpoints 6, StreamingCdcEndpoints 4, CdcStreamAdminEndpoints 4, TableStreamEndpoints 2 = 16.
- Lineage: Application/Lineage 4 files 938 lines (contains `GdprAuditReportPdfExporter.cs`, `LineageImpactAnalyzerService.cs`); Extensions/Lineage 3 files 447 lines.
- GDPR report wiring: `GatewayGovernanceServiceExtensions.Audit.cs:58` registers `IGdprAuditReportExporter`; consumed at `GovernanceEndpoints.Audit.cs:383`; also referenced by `GraphQL/Types/Query.Compliance.cs`.
- Webhook delivery: `Application/Webhooks/WebhookDeliveryClient.cs` (egress/SSRF guard), `WebhookSignature.cs`, `IWebhookSecretProtector.cs`.

## 5. Implementation steps
1. Red: add the absent-module tests (section 6, `EventsModuleAbsentTests`, `LineageModuleAbsentTests`) in `tests/Autheris.Tests.Integration` against the core host; they fail today because routes exist.
2. Move `GdprAuditReportPdfExporter.cs` and `IGdprAuditReportExporter.cs` to `src/Autheris.Application/Compliance/` (namespace `Autheris.Application.Compliance`); update the two consumers. Its tests stay in Unit. Separate PR (PR 1).
3. Create `modules/Autheris.Modules.Events` (csproj referencing only `Autheris.Modules.Abstractions`, `Autheris.Domain`, `Autheris.Application`), `EventsModule : IAutherisModule` with `Id = "events"`, `ConfigurationSections = ["Gateway:Cdc", "Gateway:CdcWebhooks", "Gateway:Streaming", "Gateway:Webhooks"]` (verify exact section names from the moved options' `SectionName` constants), `ConfigureServices` = body of `CdcWebhooksFeature` plus streaming registrations, `MapEndpoints` = the four endpoint files through the host registrar (SEC-SIMPL-43).
4. `git mv` the in-scope files; fix namespaces to `Autheris.Modules.Events.*`; remove registrations from `CdcWebhooksFeature` callers in the Api; `CdcWebhookStartupCheck` becomes a module startup check.
5. Persistence: move `CdcStreamSqlCore`/`WebhookOutboxSqlCore` and their dialect partials into the module; obtain connections via `IModuleHostContext.GovernanceConnectionFactory`; keep migration steps in core.
6. Lineage: same pattern, `LineageModule` with `Id = "lineage"`; GraphQL lineage types via `IAutherisGraphQLModule`.
7. Add removed sections to the module-aware branch of `UnknownGatewayKeyGuard` (SEC-SIMPL-49): present but module not loaded -> fail with "module <id> is not installed".
8. Move tests (`git mv`, names unchanged); add coverage-map rows (SEC-SIMPL-03/60) for any rename.
9. Update `source-size-ceilings.txt`, `project-edges.txt`, `namespace-assembly-violations.txt` baselines in the same PR; add modules to `Autheris.sln`, `CODEOWNERS` (named owner, AD-3.11).

## 6. Tests
- `tests/Autheris.Tests.Integration/Modules/EventsModuleAbsentTests`: `Webhook_routes_return_404_without_module`, `Cdc_stream_routes_return_404_without_module`, `Events_config_section_fails_fast_without_module` - [Trait("Category","Security")].
- `LineageModuleAbsentTests.Lineage_routes_return_404_without_module`, `.Gdpr_report_route_still_served_and_authorized_without_lineage_module` - [Security] (OD-10, GHSA-qx32-576x-5cq2 regression stays green).
- `modules/Autheris.Modules.Events.Tests/EventsModuleTests`: `Module_routes_require_authorization`, `Webhook_ssrf_guard_rejects_private_targets_when_loaded_as_module`, `Webhook_signature_secret_not_logged`, `Cdc_custom_query_poller_is_read_only` (#476) - [Security].
- Architecture: `ModuleBoundaryArchitectureTests.Modules_open_no_data_source_connections` covers the new assemblies; `PackageBoundaryArchitectureTests.Core_has_no_Streaming_or_Lineage_namespace` (new).
- Moved (unchanged names): all tests under `tests/Autheris.Tests.Unit/**/Streaming`, `**/Webhooks`, `**/Cdc*`, `**/Lineage*` except GDPR report tests.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build`
- `dotnet test modules/Autheris.Modules.Events.Tests -c Release --no-build`; same for Lineage.
- `dotnet test tests/Autheris.Tests.Architecture -c Release --no-build`
- `dotnet test tests/Autheris.Tests.Integration --filter "FullyQualifiedName~ModuleAbsent|Category=Security" -c Release --no-build`
- `git grep -l "Autheris.Application.Streaming\|Autheris.Application.Webhooks\|Autheris.Application.Lineage" -- src` returns nothing.

## 8. Acceptance criteria
- [ ] No file under `src/` in the Streaming, Webhooks, Cdc or Lineage folders (except `Compliance/GdprAuditReport*`).
- [ ] Core host: the 16 routes return 404 (auth and anon); events config keys fail fast.
- [ ] Full flavor: all moved tests green with unchanged names; Security test count not lower (coverage map).
- [ ] GDPR report route works and is authorized in the core image.
- [ ] `src/` LOC reduced by >= 9,000 (measured with the WP-SIMPL-10 script).

## 9. PR slicing
1. GDPR report relocation to `Application/Compliance` (+~20 / -~20).
2. Events module skeleton + host wiring + absent-module tests (<= 400 added src lines).
3. Events code move (git mv, `size-exception` per plan: move-only).
4. Lineage module (move + absent tests).

## 10. Risks, rollback, must NOT
- SSRF guard and signature handling must move unchanged (diff of `WebhookDeliveryClient.cs` is namespace-only).
- Rollback: revert the move PR; module host stays.
- Must NOT: move the GDPR report; change route paths; register endpoints on the root builder; let module code obtain a raw data-source `DbConnection`; drop or rename Security tests without coverage-map rows.

## 11. Estimate
+~600 (module glue, tests) / -~11,000 src (moved to modules). 6 person-days.

## Decisions taken in this spec
- CDC/webhook tables: module owns the store code, schema migrations stay in the core runner (plan default Q5, forward-only migration policy).
- GDPR report relocated to `Application/Compliance` so Lineage moves as a whole folder.
