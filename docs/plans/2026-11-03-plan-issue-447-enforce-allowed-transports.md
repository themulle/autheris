# PLAN: Enforce per-data-source `AllowedTransports` (issue #447)

Phase 2 (solution architect). Input PRD: [2026-11-03-req-issue-447-enforce-allowed-transports.md](2026-11-03-req-issue-447-enforce-allowed-transports.md) (REQ-ATR-01..12, AC-ATR-01..13). Verified against `main` @ `50d89ec9`.
Autheris is not in production: breaking API/behaviour changes are acceptable. No schema change is planned; if one becomes necessary (REQ-ATR-12 scope seed only) it ships as a forward-only step to the newest DB version (v1.5.0 frozen base).

## 1. Verified code facts (G-PLAN-REBASE)

1. `DataSourceAllowlistGuard.EnsureTransportAllowed` (`src/Autheris.Application/Sql/Services/DataSourceAllowlistGuard.cs:90`) has zero callers and allows when `resolver == null`.
2. `HierarchicalDataSourceOptionsResolver.Resolve`/`IsTransportAllowed` (`src/Autheris.Application/DataSources/`) return the **default profile** (`AllowedTransports = All`) for an unknown or blank name: a second fail-open. Profiles exist only for keys of `DataSources:Profiles` and `Gateway:DataSources:Connections`; `WebSql.DefaultDataSourceName` (`"default"`), `AllowedDataSources`, `DataSourceMappings` values are not guaranteed a profile.
3. The transport string is lost below the edge: `SqlTableReadExecutor` hard-codes `SqlTransports.Rest` (and `WebSql` for subtree plans) for every planned read, so GraphQL, OData and MCP dataset reads reach the SQL engine as `Rest`. GraphQL aggregates send `"graphql"`, semantic metrics `"SemanticMetrics"`. `SqlTransports` (string, federation gate) has no GraphQL/OData entry.
4. Two background re-entry points create fresh DI scopes: `WebSqlStatementManager` (`Task.Run` + `CreateScope`, line ~160; Trino/WebSQL statements) and `AsyncQueryJobBackgroundWorker` (`CreateScope`, line ~236).
5. Flight SQL is an HTTP facade (`ArrowFlightSqlEndpoints`: `/api/v1/flight/sql/{info,tables,stream}`), not gRPC.
6. `DataSourceProfileOptions.AllowedTransports` is non-nullable with initializer `All` (`src/Autheris.Domain/Options/DataSources/DataSourceProfileOptions.cs:77`). `HierarchicalDataSourceOptionsResolver` (line ~264) uses `Defaults.AllowedTransports` only when no profile exists at all. A profile that sets any other property (for example only `MaxResponseRows`) silently gets `All`, regardless of `Defaults`: a third fail-open.
6. `GatewaySecurityException.ErrorCode` is mapped by `GatewayExceptionHandler` via `GatewayDenialStatusMapper.SpecificCode`; GraphQL by `ErrorSanitizingFilter` (fixed text per code); WebSQL/Trino by `WebSqlEndpoints.Governance.cs` (`case GatewaySecurityException`).

### Data-reaching choke points (every ingress path converges here)

| # | Choke point (file / method) | Reached by |
|---|---|---|
| C1 | `GovernedSqlExecutor.ExecuteCoreAsync` (early, explicit `DataSourceName`) and `ExecuteRewrittenAsync` (`rewrite.DataSourceName`, before connection) | WebSQL, Trino, MCP `query_sql`, Flight stream, REST dataset/table stream/Arrow export/analytics/SQL endpoints, async jobs, all planned reads via `SqlTableReadExecutor` |
| C2 | `FederatedDuckDbExecutionService` after `_router.RouteAsync` (validate ~225 and execute ~290): every `ResolvedSourceTable.EffectiveDataSource` | Cross-source SQL (any SQL transport) |
| C3 | `GatewayExecutionService.ExecutePageCoreAsync`, before the connector/executor branch (~line 290) | GraphQL table fields, OData entity sets/`$count`/`$batch`, MCP dataset tools, `GatewayMcpQueryExecutor` |
| C4 | `GovernedAggregationService.ValidationAndExecution` before `executor.ExecuteAsync` (~366) | GraphQL/REST/MCP aggregates and semantic metrics |
| C5 | `OlapStagingService` (~629) and `FederatedStagingService` (~92), next to the existing `DataSourceAllowlistGuard.EnsureAllowed` per source | OLAP / federated staging |

## 2. Design decisions

1. **Ingress context (REQ-ATR-06).** New scoped `IIngressTransportContext` (`src/Autheris.Application/DataSources/IngressTransportContext.cs`): `DataSourceTransportFlags Current` (default `None`), `void Set(DataSourceTransportFlags t)`. Set-once: `None`, multi-bit values, or a second `Set` with a different value throw `InvalidOperationException`; the same value is a no-op. It is never read from client input. Inner delegation (MCP -> governed SQL, MCP -> in-process GraphQL executor, planned reads tagged `Rest`) inherits the outer value, so the MCP flag is the one checked. `GovernedSqlQueryRequest.Transport` stays unchanged and keeps driving only the `CrossSource.AllowedTransports` federation gate.
2. **Stamping.** An `IngressTransportMetadata(DataSourceTransportFlags)` endpoint metadata plus `IngressTransportMiddleware` (after routing, before authorization handlers run endpoint code) copies it into the context. Each `Map*` file declares its own metadata with `.WithIngressTransport(...)` (`src/Autheris.Api/Endpoints/IngressTransportEndpointExtensions.cs`). Non-HTTP and background paths call `Set` explicitly: `McpStdioRunner` (new async scope per request, `Mcp`), `WebSqlStatementManager` (captures the submitting value into `StatementSession` and sets it inside the `Task.Run` scope), `AsyncQueryJobBackgroundWorker` (`WebSql`: the only submit route is `/api/v1/jobs/query`, which takes client SQL; default taken).
3. **Route-to-flag map.** `/api/v1/sql`, `/api/sql`, `/api/v1/sql/statements/*`, `/api/v1/jobs/*` -> `WebSql`; `/v1/statement*` -> `Trino`; GraphQL endpoint -> `GraphQL`; OData routes -> `OData`; MCP HTTP + stdio -> `Mcp`; `/api/v1/flight/sql/*` -> `ArrowFlight`; dataset data, table stream, Arrow export, analytics, SQL endpoint routes (`SqlEndpointRoutes`), DuckDB OLAP, catalog API -> `Rest`. Admin, governance, SCIM, health, auth and dev-portal endpoints get no metadata and sit on the reviewed exemption list (non-goal 2).
4. **Single gate (REQ-ATR-01/02).** New `IDataSourceTransportGate` / `DataSourceTransportGate` (scoped, `src/Autheris.Application/DataSources/`), dependencies required (no nullable resolver): `IIngressTransportContext`, `IDataSourceOptionsResolver`, `IOptionsMonitor<GatewayOptions>` (name mapping), `ITransportDenialRecorder`. API: `void Ensure(string? dataSourceName)`, `void EnsureAll(IEnumerable<string?> names)` (all-or-nothing, checked before any data access), `bool IsAllowed(string? name)` (discovery; no audit). Decision is a pure static `TransportDecision Decide(IDataSourceOptionsResolver, string? canonicalName, DataSourceTransportFlags transport)` with reasons `Allowed | TransportUnset | InvalidTransport | UnknownSource | FlagNotSet | EvaluationFailed`. The name is canonicalised with `DataSourceAllowlistGuard.MapDataSource`; lookup uses `TryResolve` (never `Resolve`); any exception -> `EvaluationFailed` -> deny. `DataSourceAllowlistGuard.EnsureTransportAllowed` and `IDataSourceOptionsResolver.IsTransportAllowed` are deleted (the fail-open paths); `EffectiveDataSourceProfile.IsTransportAllowed` remains as the bit test.
5. **Known-source closure.** `HierarchicalDataSourceOptionsResolver.Rebuild` adds `WebSql.DefaultDataSourceName`, `WebSql.AllowedDataSources`, `WebSql.DataSourceMappings` values and `TenantDataSourceAllowlist` values to the profile name set (each gets a profile built from `Defaults`), so a legitimately configured source never hits `UnknownSource` (AC-ATR-13).
6. **Exception and error mapping (REQ-ATR-04).** New `TransportNotAllowedException : GatewaySecurityException` (`src/Autheris.Domain/Exceptions/GatewayExceptions.cs`), `ErrorCode = "TRANSPORT_NOT_ALLOWED"`. The fixed message is `Access to data source '<logical name>' is not allowed over transport '<transport>'.`; the logical name is the caller-supplied or catalog name before mapping, never the physical connection key. Unknown source and denied flag produce the same exception, so the responses cannot be told apart.

| Transport | Surface | Mapping (file) |
|---|---|---|
| WebSql / Trino | 403 problem; Trino error object `errorName=TRANSPORT_NOT_ALLOWED` | `GatewayDenialStatusMapper.SpecificCode`, `WebSqlEndpoints.Governance.cs` |
| Rest / OData / ArrowFlight | 403 problem `code=TRANSPORT_NOT_ALLOWED` (the Flight facade is HTTP; this is the `PERMISSION_DENIED` equivalent) | `GatewayDenialStatusMapper.SpecificCode`, `ApiErrorCodes.TransportNotAllowed` |
| GraphQL | `errors[].extensions.code = TRANSPORT_NOT_ALLOWED`, fixed text, `data` null for the whole operation | `ErrorSanitizingFilter` typed-code table |
| Mcp | tool result `isError=true`, code `TRANSPORT_NOT_ALLOWED`, fixed text | `McpToolExecutionHandler` (central tool error mapping) |

7. **Audit and metric (REQ-ATR-05).** `ITransportDenialRecorder` (`src/Autheris.Application/DataSources/TransportDenialRecorder.cs`) writes one event `AuditEventTypes.TransportNotAllowed = "TRANSPORT_NOT_ALLOWED"` through `IAuditWriter` with the fields caller sid, tenant, transport, data source (logical), correlation/trace id, `reason=TransportNotAllowed` and the sub-reason, `outcome=DENY`. Failures are caught and counted via the existing `autheris_audit_write_failed_total`, and the denial still throws. The counter is `GatewayDiagnostics.TransportDenied` = `autheris_transport_denied_total{transport,datasource}`. The `datasource` tag is a configured profile name, otherwise the literal `unknown`, which keeps cardinality bounded.
8. **Multi-source and all-or-nothing (REQ-ATR-03/07).** C2 calls `EnsureAll` on the routed sources before staging, in addition to the existing `FederationGate`/`CrossSource.AllowedTransports` check; both must pass. A GraphQL operation that touches S and T is denied as a whole: C3 throws and the error filter turns the operation into a request-level error with no partial `data`. OData `$batch` items are gated individually and fail the whole changeset or batch. REST bulk is gated before the first item.
9. **Hot reload (REQ-ATR-10).** The gate reads the live resolver for each call. `WebSqlStatementManager.GetStatusOrWaitAsync` re-runs `EnsureAll(session.DataSources)` under the session's stored ingress before it returns a page. Flight `DoGetStreamAsync` re-checks when a ticket is redeemed. MCP re-checks on every tool call. Running streams are not killed.
10. **Discovery (REQ-ATR-08).** `CatalogVisibilityService` drops rows whose source fails `gate.IsAllowed` for the current ingress. This covers GraphQL `Query.Catalog`, `CatalogApiEndpoints`, `McpDatasetCatalog`, `McpCatalogVisibility` and `SemanticMcpCompiler`. Surfaces that have their own filter: `ArrowFlightSqlServer.GetTablesAsync`, `ODataEndpoints.Metadata.cs` / `DynamicOpenApiGenerator`, and `CatalogGraphQlTypeModule` (the schema excludes tables whose source denies GraphQL; the existing schema rebuild is also triggered by a resolver change). With no ingress set (for example in admin contexts), discovery is not filtered: the endpoint is exempt and no data is reachable through it.
11. **Startup and reload warnings (REQ-ATR-09).** A new `DataSourceTransportConfigurationReporter` (`IHostedService`, `src/Autheris.Api/Configuration/Validation/`) logs one warning per source on start and on `OnChange` in two cases: `AllowedTransports == None`, or every allowed transport is globally disabled (for example `WebSql:Enabled=false`, GraphQL/OData/MCP/Flight disabled). Startup does not fail, so `DataSourceOptionsValidator` stays fail-only.
12. **Diagnostics (REQ-ATR-11).** The effective `AllowedTransports` (flag names) per source is added to the existing data source admin listing. If none exists, the default taken is a new `GET /api/v1/admin/datasources/effective-transports` under the existing admin policy. The developer checks for an existing endpoint first.
13. **Transport inheritance (owner decision 2026-11-03, fact 6).** `DataSourceProfileOptions.AllowedTransports` becomes `DataSourceTransportFlags?` without initializer; `DataSourceConfigurationOptions.Defaults.AllowedTransports` keeps the effective default `All` (resolved in the resolver, not by the property initializer). The resolver computes `profile?.AllowedTransports ?? Defaults.AllowedTransports ?? All`, so a profile without the key inherits `Defaults` like every other nullable profile property. An explicit `None` in a profile stays `None` (deny) and is never replaced by `Defaults`. Binding behaviour for an explicit empty string or an unparsable value fails validation at startup instead of silently becoming `All`.

## 3. Work packages

Order: WP-1, then WP-2a..f (parallel), then WP-3, then WP-4/WP-5/WP-6 (parallel), then WP-7 (follow-up). WP-2 only stamps the ingress and changes no behaviour. WP-3 turns enforcement on and must land after all WP-2 lanes, otherwise every request is denied (`TransportUnset`).

| WP | Scope | Files touched (disjoint per parallel lane) |
|---|---|---|
| WP-1 Core gate | Decisions 1, 2 (middleware + extension only), 4, 5, 6, 7, 13 | `Application/DataSources/{IngressTransportContext,DataSourceTransportGate,TransportDenialRecorder}.cs`, `HierarchicalDataSourceOptionsResolver.cs`, `Domain/Options/DataSources/DataSourceProfileOptions.cs`, options validator, `IDataSourceOptionsResolver.cs`, `Sql/Services/DataSourceAllowlistGuard.cs`, `Domain/Exceptions/GatewayExceptions.cs`, `Domain/Audit/AuditEventTypes.cs`, `Domain/Diagnostics/GatewayDiagnostics.cs`, `Api/Middleware/{IngressTransportMiddleware,GatewayDenialStatusMapper,ErrorSanitizingFilter}.cs`, `Api/Errors/ApiErrorCodes.cs`, `Api/Endpoints/IngressTransportEndpointExtensions.cs`, `Api/Extensions/GatewayApplicationBuilderExtensions.cs` (middleware registration), `Api/Extensions/DependencyInjection/GatewayDataSourceServiceExtensions.cs` |
| WP-2a WebSql/Trino stamping | Metadata on SQL/Trino routes and jobs; statement manager and job worker carry the ingress | `Api/Endpoints/WebSqlEndpoints.cs`, `Api/Endpoints/AsyncJobEndpoints.cs`, `Application/Sql/Services/WebSqlStatementManager.cs`, `Application/Jobs/Services/AsyncQueryJobBackgroundWorker.cs` |
| WP-2b GraphQL | Metadata on `MapGraphQL` | `Api/Extensions/GatewayApplicationBuilderExtensions.cs` (lines ~167 only; WP-1 must be merged first) |
| WP-2c MCP | HTTP metadata; stdio per-request scope + `Set(Mcp)` | `Api/Endpoints/McpEndpoints.cs`, `Api/Mcp/McpStdioRunner.cs` |
| WP-2d OData | Metadata | `Api/Endpoints/ODataEndpoints*.cs` |
| WP-2e REST | Metadata | `Api/Endpoints/{DatasetDataEndpoints,TableStreamEndpoints,ArrowExportEndpoints,AnalyticsEndpoints,SqlEndpointRoutes,DuckDbOlapEndpoints,CatalogApiEndpoints}.cs` |
| WP-2f Flight | Metadata | `Api/Endpoints/ArrowFlightSqlEndpoints.cs` |
| WP-3 Enforcement | Gate calls at C1..C5, statement page re-check, Flight ticket re-check, MCP error mapping, Trino error object | `GovernedSqlExecutor.cs`, `FederatedDuckDbExecutionService.cs`, `GatewayExecutionService.cs`, `GovernedAggregationService.ValidationAndExecution.cs`, `OlapStagingService.cs`, `FederatedStagingService.cs`, `WebSqlStatementManager.cs`, `ArrowFlightSqlServer.cs` (DoGet), `McpToolExecutionHandler.cs`, `WebSqlEndpoints.Governance.cs` |
| WP-4 Discovery | Decision 10 | `Catalog/Services/CatalogVisibilityService.cs`, `ArrowFlightSqlServer.cs` (GetTables), `ODataEndpoints.Metadata.cs`, `OData/Services/DynamicOpenApiGenerator.cs`, `GraphQL/Catalog/CatalogGraphQlTypeModule.cs` |
| WP-5 Ops | Decisions 11, 12 | `Api/Configuration/Validation/DataSourceTransportConfigurationReporter.cs`, DI registration, admin data source endpoint |
| WP-6 Coverage guard | AC-ATR-11 | `tests/Autheris.Tests.Architecture/TransportGateCoverageTests.cs`, `Baselines/ingress-exemptions.txt`, one integration test |
| WP-7 Follow-up (REQ-ATR-12) | Scope `WebSql.Query` required on WebSql/Trino/Flight/async-job ingress when `WebSql:RequireScope=true` | Separate plan addendum before Phase 4; implemented in a separate PR after WP-1..6 |

WP-3 and WP-4 both touch `ArrowFlightSqlServer.cs` (different methods) and run sequentially. All tests below are new unless noted.

### TDD test list

- **WP-1** (`tests/Autheris.Tests.Unit/DataSources/`): `DataSourceTransportGateTests`: `Decide_AllowsWhenFlagSet` (theory over all 7 flags), `Decide_DeniesWhenFlagMissing`, `Decide_DeniesUnknownSource`, `Decide_DeniesBlankName`, `Decide_DeniesTransportNone`, `Decide_DeniesMultiBitTransport`, `Decide_DeniesWhenResolverThrows`, `Ensure_UnknownAndDeniedThrowSameShape`, `EnsureAll_OneDeniedDeniesAll_NoPartialAudit`, `Ensure_CanonicalisesMappedName`, `Ensure_MessageNamesLogicalNotPhysicalSource`. `IngressTransportContextTests`: `Set_Once_ThenSameValue_NoOp`, `Set_DifferentValue_Throws`, `Set_None_Throws`, `Set_MultiBit_Throws`. `DataSourceTransportInheritanceTests` (decision 13): `ProfileWithoutAllowedTransports_InheritsDefaults`, `ProfileWithExplicitNone_StaysNone_NotDefaults`, `NoProfile_UsesDefaults`, `DefaultsUnset_EffectiveAll`, `ProfileExplicitValue_OverridesDefaults`, `InvalidOrEmptyValue_FailsValidationAtStartup`. `TransportDenialRecorderTests`: `Denial_WritesExactlyOneAuditEventWithRequiredFields`, `AuditFailure_StillDenies_AndCountsFailure`, `Metric_UnknownSourceTaggedUnknown`. `HierarchicalDataSourceOptionsResolverTests` (extend): `Rebuild_AddsWebSqlDefaultAllowedAndMappedSources`. `GatewayDenialStatusMapperTests`: `TransportNotAllowed_Maps403WithCode`. `ErrorSanitizingFilterTests`: `TransportNotAllowed_FixedTextAndCode`. Architecture: `DataSourceAllowlistGuard_HasNoEnsureTransportAllowed`.
- **WP-2a..f** (integration, `tests/Autheris.Tests.Integration/Transports/IngressStampingTests`): one test per lane, `<Transport>Route_StampsIngress_<Flag>`, which asserts the context value seen by a probe gate. Also `McpStdio_StampsMcpPerRequest`, `StatementManager_BackgroundScopeCarriesSubmittingIngress`, `AsyncJobWorker_StampsWebSql`, `ClientHeader_CannotSetIngress`.
- **WP-3** (integration, `TransportEnforcementTests`; S=`GraphQL` only and T=`All` unless noted; each asserts `TRANSPORT_NOT_ALLOWED`, a counting fake executor/connection at 0, one audit event, a metric delta of 1, and a positive control): `GraphQL_SourceRestOnly_Denied`, `GraphQL_MixedSAndT_DeniedWhole` (AC-01); `WebSql_Denied`, `WebSql_JoinSAndT_Denied`, `WebSql_StatementFetch_Denied` (AC-02); `Trino_Submit_Denied`, `Trino_PageAfterReloadDenies` (AC-03); `Mcp_QuerySql_Denied`, `Mcp_DatasetTool_Denied`, `Mcp_QuerySqlDelegation_ChecksMcpNotWebSql` (S=`WebSql` only), `McpStdio_QuerySql_Denied` (AC-04); `OData_EntitySet_Denied`, `OData_Count_Denied`, `OData_BatchItem_DeniesBatch` (AC-05); `Rest_DatasetData_Denied`, `Rest_TableStream_Denied`, `Rest_ArrowExport_Denied`, `Rest_Bulk_OneDeniedNoPartialEffect` (AC-06); `Flight_Stream_Denied`, `Flight_TicketRedeemAfterReload_Denied` (AC-07); `CrossSource_TransportListedButSourceDenied_Denied` (AC-08); `FailClosed_Theory_AllSevenTransports` over unset resolver profile, unknown source, `None` and a throwing resolver (AC-09); `Aggregate_GraphQl_Denied`, `OlapStaging_Denied`. Trino error object: `Trino_DeniedErrorObjectCarriesCode`.
- **WP-4**: `Discovery_GraphQlSchemaOmitsDeniedSource`, `Discovery_ODataMetadataOmits`, `Discovery_McpDatasetListOmits`, `Discovery_FlightGetTablesOmits`, `Discovery_CatalogApiOmits`, `Discovery_AdminCatalogUnfiltered` (AC-04/05/07, REQ-ATR-08).
- **WP-5**: `Reporter_WarnsWhenNone`, `Reporter_WarnsWhenOnlyGloballyDisabledTransport`, `Reporter_DefaultAll_NoWarning`, `Reporter_WarnsOnReload` (AC-12); `AdminDiagnostics_ShowsEffectiveTransports`.
- **WP-6**: `ChokePoints_InvokeTransportGate` (Cecil: C1..C5 methods call `IDataSourceTransportGate.Ensure*`). `DataExecution_OnlyFromApprovedChokePoints`: callers of `IDataSourceExecutor.ExecuteAsync/StreamAsync`, `GovernedConnectorReader.ReadRawAsync` and `GovernedSqlExecutor.ExecuteCoreAsync` must be in the approved list. `NoCallers_OfResolverIsTransportAllowed`. Integration `AllEndpoints_HaveIngressMetadataOrReviewedExemption` enumerates `EndpointDataSource` against `Baselines/ingress-exemptions.txt` (AC-11).
- **AC-ATR-13**: the existing unit, integration and parity suites pass unchanged; this is the WP-3 exit criterion.

## 4. Risks

1. **R1, unknown-source false denials.** Connector-type sources (plugins, declarative HTTP, lakehouse) may use a `SourceName` that has no profile. Mitigation: decision 5, a WP-3 integration sweep in which every catalog `SourceName` in the test host resolves, and a WP-5 warning that lists catalog source names without a profile.
2. **R2, unset ingress in an unlisted background scope.** Such a scope is denied (fail-closed, safe), so the risk is functional breakage. Mitigation: grep every `CreateScope`/`CreateAsyncScope` that reaches C1..C5 during WP-2. The integration suites surface `TransportUnset` denials through the audit reason.
3. **R3, GraphQL schema filtering vs hot reload.** The schema may be stale until it is rebuilt. Execution is still gated at C3, so a stale schema only exposes table names, never data.
4. **R4, double gating** (C3, then C1 for the same planned read). The calls are idempotent. A denial throws at the first gate, so there is exactly one audit event.
5. **R5, behaviour change.** Sources that were restricted but still reachable are now denied. This is acceptable because Autheris is not in production; it goes in the release notes.

## 5. Open decisions (defaults taken)

1. Async jobs use the `WebSql` flag (client SQL). Per-route capture is an alternative if a REST job route is added.
2. Flight denial is HTTP 403 + `TRANSPORT_NOT_ALLOWED` (HTTP facade, not gRPC status).
3. The error message names the logical source, never the mapped physical key.
4. Discovery is not filtered when no ingress is set (admin/exempt endpoints).
5. Warnings come from a hosted reporter; startup is not failed.
6. The diagnostics endpoint: extend the existing admin listing, else add a new admin GET.
7. PRD defaults 1-7 are adopted unchanged (unset = `All`; unknown = deny; Trino/Flight are their own transport; hide in discovery; REQ-ATR-12 split; 403; re-check per page).

## 6. Traceability

| REQ | Decision / WP | AC / tests |
|---|---|---|
| ATR-01 | D4 / WP-1, WP-3 | AC-11 `ChokePoints_InvokeTransportGate` |
| ATR-02 | D4, D5 / WP-1 | AC-09 `FailClosed_Theory_AllSevenTransports`, `Decide_*` |
| ATR-03 | D8 / WP-3 | AC-01, AC-02, AC-05, AC-06 mixed/bulk tests |
| ATR-04 | D6 / WP-1, WP-3 | `Ensure_UnknownAndDeniedThrowSameShape`, mapper tests |
| ATR-05 | D7 / WP-1 | AC-10 recorder tests |
| ATR-06 | D1, D2 / WP-1, WP-2 | `Mcp_QuerySqlDelegation_ChecksMcpNotWebSql`, `ClientHeader_CannotSetIngress` |
| ATR-07 | D8 / WP-3 | AC-08 |
| ATR-08 | D10 / WP-4 | `Discovery_*` |
| ATR-09 | D11 / WP-5 | AC-12 |
| ATR-10 | D9 / WP-3 | `Trino_PageAfterReloadDenies`, `Flight_TicketRedeemAfterReload_Denied` |
| ATR-11 | D12 / WP-5 | `AdminDiagnostics_ShowsEffectiveTransports` |
| ATR-12 | WP-7 | separate addendum |
| AC-13 | all | existing suites unchanged |

## Phase 3 security review

Reviewer: `csharp-security-expert`, 2026-11-03. Scope: threat model of decisions 1-12 and WP-1..7 against the verified code facts.

**Verdict: APPROVED WITH CONDITIONS.** The design is sound (single gate, set-once server-side context, `TryResolve`, unknown = deny, deletion of both fail-open paths). The conditions below are binding for Phase 4 and Phase 5.

### Threat summary

| Threat | Assessment |
|---|---|
| Transport spoofing (header/query/body) | Safe only if ingress comes solely from endpoint metadata. Open: `GovernedSqlQueryRequest.Transport` may be client-influenced and drives the federation gate (SEC-ATR-03). |
| Mutable ingress | Set-once is adequate; a missing middleware registration must deny (SEC-ATR-02). |
| Background scopes | Fail-closed by `TransportUnset`; replay across transports needs binding (SEC-ATR-05). |
| GraphQL partial data, cache bypass | Resolver-level gate can leak partial data or be skipped by caches (SEC-ATR-06, -07). |
| Existence oracle, log injection | Same shape for unknown/denied; message echoes caller input; gate order matters (SEC-ATR-08, -09). |
| WP ordering | WP-2 is stamp-only, so the window equals today's state; WP-6 must not trail WP-3 (SEC-ATR-12). |

### Binding conditions (fail-closed)

- **SEC-ATR-01 No bypass or off switch.** The gate has no config flag, no null resolver, no `try/catch` that continues. Any exception, `None`, multi-bit flag or missing profile denies and throws `TransportNotAllowedException`. `Decide` is the only decision point; `IsAllowed` shares it.
- **SEC-ATR-02 Ingress is endpoint-metadata only.** Never read from headers, query, body, host, path string or `X-Forwarded-*`/`X-Trino-*`. `IngressTransportMiddleware` is registered in every host (API, tests, MCP stdio); an endpoint without metadata leaves `None` and denies. A conflicting second `Set` fails the request (no data), never falls back.
- **SEC-ATR-03 Server-side `GovernedSqlQueryRequest.Transport`.** Every ingress overwrites that field from `IIngressTransportContext`; it is never deserialised from client input. `SqlTableReadExecutor` stops hard-coding `Rest`/`WebSql` and passes the ingress-derived value to the federation gate (otherwise a GraphQL read is judged as `Rest`).
- **SEC-ATR-04 Complete choke points.** C1..C5 land in one PR (WP-3), including every write/DML and ingest path that reaches `IDataSourceExecutor`. No choke point is left to a follow-up.
- **SEC-ATR-05 Background and ticket binding.** `StatementSession`, async job rows and Flight tickets persist the submitting ingress. A fetch/redeem whose current ingress differs from the stored one is denied (WebSql statement id on a Trino route, Flight ticket on any other route, and vice versa) and audited. The worker reads the persisted value; a row without it denies. Every `CreateScope`/`CreateAsyncScope`/`Task.Run` that can reach C1..C5 either copies the ingress or is listed in the exemption baseline with security sign-off.
- **SEC-ATR-06 GraphQL all-or-nothing before execution.** Sources touched by the operation (all root fields, aliases, fragments, batched operations) are checked by `EnsureAll` before any resolver runs, so no resolver result or side effect precedes a denial. Subscriptions are gated at subscribe and at each source access.
- **SEC-ATR-07 Gate before caches.** The gate runs before any result, plan, DataLoader or semantic cache lookup (or the cache key includes the ingress). A cache hit never serves a denied transport.
- **SEC-ATR-08 No oracle, no echo abuse.** Unknown, blank, mapped-physical and denied sources return identical code, status and text; the sub-reason (`UnknownSource`, `FlagNotSet`, `EvaluationFailed`) appears only in the audit event. The echoed logical name is length-capped (128) and stripped of control characters; GraphQL and MCP use the fixed text without the name. At each choke point the gate runs after the principal and tenant allowlist checks, so an unauthorised caller cannot learn which sources exist or what they allow.
- **SEC-ATR-09 Audit and log hygiene.** The denial throws even when `IAuditWriter` fails (kept; failure counter increments). Caller-supplied name, trace id and tenant are sanitised and length-capped before audit/log; no connection strings, physical keys, SQL text or exception messages are recorded. The metric `datasource` tag uses only configured profile names or `unknown`.
- **SEC-ATR-10 Hot reload.** `EnsureAll` evaluates all names against one resolver snapshot. `Rebuild` builds the new profile map fully and swaps it atomically; a failed rebuild keeps the last good snapshot, a never-built resolver denies. The known-source closure (decision 5) never overwrites an explicit profile and only adds `Defaults` profiles.
- **SEC-ATR-11 Federation and MCP delegation.** C2 denies the whole query if any routed source fails (no partial staging, no staged temp data left behind). Inner MCP delegation and in-process GraphQL reuse the outer scope; a fresh scope without a copied ingress denies.
- **SEC-ATR-12 Ordering.** WP-2 and WP-3 ship in the same release; WP-6 (coverage guard incl. the `CreateScope` guard of SEC-ATR-05) merges with or before WP-3 and is a merge condition, not a follow-up. WP-3 is not released with any WP-2 lane missing. WP-4 may follow WP-3.
- **SEC-ATR-13 Coverage guard completeness.** `ingress-exemptions.txt` changes require explicit security approval (CODEOWNERS or reviewer note). The guard also fails on a new direct caller of executors/connectors outside the approved list, on a new endpoint without ingress metadata, and on any remaining reference to `IsTransportAllowed` or `EnsureTransportAllowed`.
- **SEC-ATR-14 Internal jobs.** Scheduled/system paths (cache warmers, refreshes, introspection) get no implicit pass: unset ingress denies. A path that must read sources is handled by a separate audited internal marker approved in an addendum, never by a spoofable transport flag.

### Mandatory security tests (all `[Trait("Category","Security")]`)

Per transport (positive control, deny, counting fake at 0, one audit event, metric delta 1), WP-3:
- WebSql: `WebSql_SourceDenied_FailsClosed`, `WebSql_StatementIdOnTrinoRoute_Denied`.
- GraphQL: `GraphQL_DeniedSourceInMixedOperation_NoResolverRuns` (06), `GraphQL_CacheHit_DoesNotBypassGate` (07).
- Rest: `Rest_DatasetStream_DeniedForGraphQlOnlySource`, `Rest_ClientTransportBodyField_Ignored` (03).
- OData: `OData_Batch_OneDeniedItem_FailsWholeBatch`, `OData_CountEntitySet_Denied`.
- Mcp: `Mcp_QuerySqlDelegation_ChecksMcpNotWebSql`, `McpStdio_NewScopeWithoutIngress_Denied`.
- Trino: `Trino_PageAfterReloadDenies`, `Trino_ClientSourceHeader_CannotSetIngress`.
- ArrowFlight: `Flight_TicketRedeemAfterReload_Denied`, `Flight_TicketOnRestRoute_Denied`.

Cross-cutting: `Gate_ResolverThrows_Denies`, `Gate_UnknownBlankMappedPhysical_SameShapeAsDenied` (08), `Gate_AuditWriterThrows_StillDenies`, `Gate_ControlCharsAndLongName_Sanitised` (09), `Gate_RebuildFailure_KeepsLastGoodOrDenies` (10), `Federation_OneSourceDenied_NoStagingCreated` (11), `AsyncJob_RowWithoutIngress_Denied`, `ClientHeader_CannotSetIngress` (`X-Autheris-Transport`, `X-Forwarded-*`, query `transport=`), `Middleware_Unregistered_DeniesAll`, `Guard_CreateScopeSitesCopyOrExempt`, `Guard_NewEndpointWithoutIngressMetadata_Fails`, `SqlTableReadExecutor_PassesIngressToFederationGate` (03).

### Accepted residual risks
1. TOCTOU: a stream running when a reload removes a transport continues (D9); new pages/tickets are re-gated.
2. Sources without an explicit profile get the `Defaults` profile (`All`); permissive default is operator responsibility, mitigated by WP-5 warnings.
3. Discovery filtering is best-effort; a stale GraphQL schema may show table names, never data (R3).
4. A caller authorised for a source can tell "allowed here" from "denied"; an unauthorised caller cannot (SEC-ATR-08 ordering).
5. Denial storms cost one audit write each; bounded by existing rate limiting, no new queue.
6. REQ-ATR-12 (scope enforcement) stays out of scope until WP-7.

### Owner decisions (2026-11-03)
All defaults confirmed by the owner:
1. Unprofiled sources keep `Defaults` (`All`), with WP-5 warnings. Operators restrict globally via `Defaults.AllowedTransports`; this works for profiled sources too because of decision 13.
2. Internal system jobs without a stamped ingress are denied; exceptions need a plan addendum (SEC-ATR-14).
3. The gate runs after the principal/tenant checks (SEC-ATR-08).
4. Async jobs are checked against the `WebSql` flag; Flight denials return HTTP 403 + `TRANSPORT_NOT_ALLOWED` (section 5, items 1-2).
5. Added: decision 13 (`AllowedTransports` inherits from `Defaults`), in scope of WP-1. It only narrows access (removes a fail-open) and falls under SEC-ATR-01; its tests are mandatory security tests.
