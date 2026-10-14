# Architecture Review 04 - Application feature modules and Autheris.Extensions

Reviewer: Solution Architect (read-only). All paths relative to `/root/lis-git/autheris/src` unless stated.

## 1. Scope and key metrics

| Slice | Files | Lines |
|---|---|---|
| Application: Mcp (largest) | 42 | 7,236 |
| Application: Catalog / DataCatalog / OpenMetadata | 12 / 7 / 3 | 1,585 / 723 / 282 |
| Application: Serialization / Streaming / Olap | 6 / 8 / 4 | 1,305 / 1,127 / 1,065 |
| Application: Docs / Dbt / Lineage / Jobs / OData / Connectors | 6 / 12 / 2 / 5 / 4 / 11 | 914 / 741 / 722 / 676 / 686 / 692 |
| Application: Events / Extensibility / Plugins / Integrations | 8 / 11 / 5 / 1 | 579 / 476 / 130 / 23 |
| Autheris.Extensions (9 integrations) | 72 | 13,882 |

Largest files in slice: `Extensions/Dbt/DbtMetadataIngestionService.cs` 1353, `Extensions/OpenMetadata/OpenMetadataSyncService.cs` 1154, `Application/Mcp/Services/AiDataGuardrailService.cs` 1051, `Extensions/OData/ODataFilterParser.cs` 833, `Extensions/Itsm/ItsmWebhookHandler.cs` 747, `Application/Mcp/Services/HitLStepUpApprovalService.cs` 744, `Application/Serialization/ParquetExportService.cs` 664, `Application/Streaming/Services/StreamingRowFilterAstEvaluator.cs` 640, `Application/Olap/DuckDbOlapEngine.cs` 639, `Extensions/OData/ODataHandler.cs` 549.

Plugin runtime lives outside the slice: `Autheris.Infrastructure/Plugins/*` (PluginManager 386 lines, ALC 80 lines). Webhook code lives in `Application/Webhooks` and `Application/Events`; both were reviewed because the plugin/event branch is in focus.

Note: `Autheris.Extensions.csproj` references only Domain and Application (Autheris.Extensions.csproj lines 4-5), and a grep for Infrastructure/Api types in Extensions returned nothing. The layering of the project itself is clean.

## 2. Strengths

- Extensions is a genuine outer layer: it depends only on Domain and Application abstractions, each integration has its own `Add*Integration` method guarded by an idempotency marker (`Extensions/ExtensionRegistration.cs:10-25`), and workers are gated by `Enabled` flags (e.g. `Extensions/Cdc/CdcSourceServiceCollectionExtensions.cs:27-30`, `Extensions/OpenMetadata/OpenMetadataServiceCollectionExtensions.cs:28`).
- Outbound HTTP is uniformly typed/named `IHttpClientFactory` clients routed through one hardened handler chain (`Application/Security/SecureOutboundHttp.cs:43-50`: no redirects, no system proxy, connect-time address validation, SSRF handler); applied consistently in Extensions (`Itsm/ItsmServiceCollectionExtensions.cs:29-30`, `DataCatalog/DataCatalogServiceCollectionExtensions.cs:29-31`, `Lineage/LineageExportServiceCollectionExtensions.cs:33,36`).
- Bounded response reading is centralised (`Extensions/BoundedHttpContent.cs`) and used by all 8 catalog/ITSM/lineage/OpenMetadata clients.
- Plugin loading is hash-pinned and verified-bytes-only with a collectible ALC and shared-assembly delegation (`Infrastructure/Plugins/PluginAssemblyLoadContext.cs:26-39,70-79`, `PluginManager.cs:137-170,184-196`).
- Cluster-aware periodic workers use a distributed lock on a named key (`Extensions/DataCatalog/DataCatalogSyncBackgroundService.cs:57`, `OpenMetadata/OpenMetadataSyncBackgroundService.cs:58`, `Cdc/MssqlChangeTrackingHostedService.cs:70`).
- Event bus contract documents fault isolation and implements it per subscriber (`Application/Events/InMemoryGatewayEventBus.cs:125-142`).

## 3. Findings overview

| ID | Sev | Category | Title |
|---|---|---|---|
| ARC-F-01 | High | Consistency | CloudEvents CDC webhook subsystem (store, transformer, dispatcher) is wired and exposed via API but never delivers |
| ARC-F-02 | High | Resilience | Event publish is awaited inline; webhook retries/backoff and plugin listeners run on the caller's request path |
| ARC-F-03 | High | Resilience | Webhook delivery has no outbox, no durability, retries 4xx, and the process-level retry is lost on restart |
| ARC-F-04 | High | Extensibility | Plugin contract is unversioned and effectively the entire `Autheris.Application` assembly |
| ARC-F-05 | Medium | Resilience | No timeouts/retry/circuit breaker on any outbound integration client; resilience is hand-rolled in one place |
| ARC-F-06 | Medium | Concurrency | Lock leases are not renewed; sync can outlive lease and overlap across replicas; lock-acquire exceptions can kill the host |
| ARC-F-07 | Medium | Concurrency | Async query jobs: single serial consumer, in-memory state, local scratch files - not HA-safe |
| ARC-F-08 | Medium | Cohesion | Three overlapping "catalog" concepts and two OpenMetadata integrations with separate clients, configs and workers |
| ARC-F-09 | Medium | Coupling | Extensions cannot be removed cleanly: Api endpoints bind to concrete Extension types; some "extensions" are core features |
| ARC-F-10 | Medium | Resilience | CDC watermark store is in-memory only; restart loses progress, multi-replica lock hands over without state |
| ARC-F-11 | Medium | Extensibility | Three disjoint extension mechanisms (plugins, interceptors, bus) with no shared registry, lifecycle or isolation |
| ARC-F-12 | Medium | Consistency | Ad-hoc `new HttpClient` with an unconfigured allowlist bypasses the standard outbound chain |
| ARC-F-13 | Medium | Cohesion | One integration is split across three projects (ITSM) |
| ARC-F-14 | Medium | Maintainability | Oversized god-classes in Extensions and Mcp |
| ARC-F-15 | Low | Maintainability | Dead/never-invoked members and options (`ConfigureServices`, `EnableHotReload`, `PluginDirectory`) |
| ARC-F-16 | Low | Consistency | Two near-identical webhook dispatchers with divergent signing and header semantics |
| ARC-F-17 | Low | Performance | Reflection `MethodInfo.Invoke` per event; per-event allocation of handler list under a global lock |
| ARC-F-18 | Low | Consistency | Hosted-service patterns are inconsistent (three loop styles, shutdown and error semantics differ) |
| ARC-F-19 | Low | Layering | Application hosts runtime/IO concerns (stdio runner, Flight SQL server, file stores, DuckDB, bridge hosted services) |
| ARC-F-20 | Low | Consistency | Plugin contract documentation in German, violating the English-only directive |

## 4. Findings in detail

### ARC-F-01 - CloudEvents webhook subsystem never delivers
- Severity: High. Category: Consistency (dead-but-exposed feature).
- Evidence: DI registers store/transformer/dispatcher (`Api/Extensions/DependencyInjection/GatewayExecutionServiceExtensions.cs:137-141`). CRUD endpoints for subscriptions exist (`Api/Endpoints/StreamingCdcEndpoints.cs:97-170`). A repo-wide grep (src and tests) for `ICloudEventTransformer`, `.Transform(`, `.DispatchAsync(` and `GetSubscriptionsAsync` shows production callers only inside `Application/Events/`; every other caller is a test (`tests/Autheris.Tests.Unit/Security/CloudEventWebhookSecurityTests.cs:41,81`). `CdcEventIngestionService.cs:31` publishes to a channel only.
- Problem: Tenants can register CDC webhook subscriptions, receive 200/201, and nothing ever sends them. The subscriptions are also only in `InMemoryCloudEventSubscriptionStore` (lost on restart, not shared across replicas).
- Impact: Silent functional gap; security tests give false assurance (SSRF/HMAC tests cover code that is not on the live path).
- Recommendation: Either (a) wire a `CdcWebhookDeliveryService` (hosted, reads CDC channel, store lookup, transformer, dispatcher with outbox, see ARC-F-03) or (b) remove the endpoints and registrations until the delivery path exists. Add a test that asserts a registered subscription receives a delivery end-to-end.
- Effort: M.

### ARC-F-02 - Event publish is synchronous fan-out on the caller's path
- Severity: High. Category: Resilience / Concurrency.
- Evidence: `Application/Events/InMemoryGatewayEventBus.cs:60-61` (`await Task.WhenAll(...)` of all handlers). Publishers await it in request flows: `Application/Mcp/Services/AiDataGuardrailService.cs:194` (prompt-injection DENY path) and `:413`, `Application/Governance/Services/AccessPlanningService.cs:153,306,430`, `Api/Endpoints/DocIngestionEndpoints.cs:118`. The webhook bridge subscribes to `IGatewayEvent` (all events) and awaits full retry loops (`Application/Webhooks/WebhookEventBridgeHostedService.cs:36-41` -> `WebhookDispatcherService.cs:77-80,111-166`: timeout x `MaxRetries` plus 200/400/800 ms... backoff per subscription).
- Problem: Delivery semantics are "in-process, at-most-once, synchronous, unordered across handlers, no backpressure, no per-handler timeout". The user-visible request (MCP tool call, access plan apply, document deposit) is delayed by the slowest listener, including a plugin listener that hangs (plugin code gets the request's `CancellationToken` only, `PluginManager.cs:292-297`). `catch (OperationCanceledException) when (ct.IsCancellationRequested)` (`InMemoryGatewayEventBus.cs:131`) means a cancelled publisher silently drops the event for all handlers.
- Impact: Latency coupling of core flows to third-party endpoints and plugin DLLs; a single dead webhook with default retries stalls every publisher. Denial path of the AI guardrail (security-relevant) waits on external HTTP.
- Recommendation: Make `PublishAsync` enqueue to a bounded `Channel<IGatewayEvent>` (e.g. capacity 10k, `DropOldest` or `Wait` with metric) drained by one or more dispatcher workers; give each subscriber its own bounded queue and per-handler timeout (`CancelAfter`) so a slow listener cannot block others; expose `Subscribe` options (ordering key, concurrency). Keep the synchronous variant only for internal invalidation channels.
- Effort: M.

### ARC-F-03 - Webhook retries are volatile, non-durable and not classified
- Severity: High. Category: Resilience.
- Evidence: `Application/Webhooks/WebhookDispatcherService.cs:111-166`: retry loop in memory; any non-2xx is retried including 4xx (`:135-146`); `response` is never disposed (`:134`); the HMAC timestamp is computed once outside the loop (`:90-91`) so late retries can fall outside a receiver's replay window; `Task.Delay(delayMs, ct)` at `:164` throws outside the try on cancel (unhandled -> faulted task in `Task.WhenAll`, caught only by the bridge's catch at `WebhookEventBridgeHostedService.cs:38`); no jitter, no `Retry-After`; `maxRetries = Math.Max(1, ...)` so `MaxRetries` is "attempts", not retries. No circuit breaker per subscription: a dead endpoint is retried for every event. A grep for "outbox" in Application/Extensions finds only the ITSM outbox in Infrastructure; none for webhooks.
- Impact: Events are lost on restart or after exhaustion (error log only); retry storms on a permanently failing target; no dead-letter queue, no delivery history for operators. For an enterprise gateway whose events include `SecurityAlertEvent`/`PolicyDeniedEvent` this is at-most-once for the most important signals.
- Recommendation: Introduce `IWebhookOutbox` (table in the existing governance store, reuse the ITSM outbox pattern in `Infrastructure/Itsm/ItsmOutboxDispatcherHostedService.cs`): persist (subscription, event, attempt, next_at), a dispatcher worker under a distributed lock, retry only on 5xx/408/429/network errors with jitter and `Retry-After`, per-subscription breaker/disable-after-N, dead-letter state, recompute timestamp/signature per attempt, `using var response`. Document at-least-once plus `ce-id` for idempotent receivers.
- Effort: L.

### ARC-F-04 - Plugin contract unversioned, shared surface = whole Application assembly
- Severity: High. Category: Extensibility.
- Evidence: `Infrastructure/Plugins/PluginAssemblyLoadContext.cs:68-79` treats `Autheris.Domain*`, `Autheris.Application*`, `Microsoft.AspNetCore*`, `Microsoft.Extensions*`, `System.*` as shared. Contracts in `Application/Plugins/*.cs` take `IServiceCollection`, `IConfiguration`, `ClaimsPrincipal`, `TableMetadata` (`IHttpDataSourcePlugin.cs:11-31`). No version attribute/handshake; grep for `ContractVersion`/`PluginApiVersion` returns nothing. `PluginManager.cs:198-202` discovers by `IsAssignableFrom` on every type of every DLL.
- Problem: Any refactor of Domain/Application types (all `public`, incl. ~every record) is a breaking change for plugin authors, detected only by `MissingMethodException`/`TypeLoadException` at runtime; the failure is swallowed to a warning (`PluginManager.cs:265-268`) so a plugin silently disappears after an upgrade. There is also no isolation of the `Microsoft.Extensions.*` version a plugin was compiled against.
- Impact: Upgrade fragility; no deprecation path.
- Recommendation: Extract `Autheris.Plugins.Abstractions` (Domain-free DTOs: event envelope, execution context with primitive types) with an assembly version policy and a mandatory `[assembly: AutherisPluginContract("1.x")]`; the loader compares major version and refuses to load incompatible plugins with an ERROR (and a health check entry). Restrict `IsSharedAssembly` to that abstractions assembly plus `Microsoft.Extensions.*.Abstractions`.
- Effort: L.

### ARC-F-05 - No resilience policies on outbound integrations
- Severity: Medium. Category: Resilience / Consistency.
- Evidence: `SecureOutboundHttp.AddSecureOutboundHandlers` only adds primary handler + SSRF handler (`Application/Security/SecureOutboundHttp.cs:43-50`). A grep for `Resilience|Polly|AddStandard|AddResilienceHandler` across `src` returns nothing (only Databricks calls `ConfigureHttpClient`-style setup: `Infrastructure/Databricks/DatabricksServiceCollectionExtensions.cs:21`). In Extensions, `Timeout` appears only in `Lineage/OpenJevClient.cs:64` and OData; no `Retry` handling in clients. `HttpClient.Timeout` therefore defaults to 100 s for Purview/Collibra/Alation/OpenMetadata/Jira/ServiceNow/OpenLineage/S3/Azure.
- Problem: Each client invents (or omits) timeout/retry; sync workers hold a distributed lock for up to 100 s per request page without breaker.
- Impact: Cascading slowness on a degraded catalog; inconsistent behaviour per integration.
- Recommendation: Add `AddSecureOutboundHandlers(name, ResiliencePreset)` that also calls `AddResilienceHandler` (Microsoft.Extensions.Http.Resilience): total timeout, attempt timeout, retry w/ jitter for idempotent verbs only, circuit breaker; presets per integration kind (catalog sync, ITSM write, lineage fire-and-forget). Make timeouts configurable under `Egress:`.
- Effort: M.

### ARC-F-06 - Lock leases not renewed; overlapping runs and host-crash risk
- Severity: Medium. Category: Concurrency.
- Evidence: Lease = `Math.Max(5, SyncIntervalMinutes)` min (`Extensions/DataCatalog/DataCatalogSyncBackgroundService.cs:48,57`; `OpenMetadata/OpenMetadataSyncBackgroundService.cs:40,58`); CDC lease `Math.Max(5000, interval*3)` ms (`Cdc/MssqlChangeTrackingHostedService.cs:70`) while a poll cycle iterates all tables sequentially and each does DB IO. `IDistributedClusterStateProvider.TryAcquireLockAsync(key, expiry, ct)` returns only an `IAsyncDisposable` (`Application/State/IDistributedClusterStateProvider.cs:54`) - no renewal or fencing token. In `OpenMetadataSyncBackgroundService.cs:55-64` the `TryAcquireLockAsync` call is outside any try/catch (the try is at `:68`), so a Redis outage during the initial sync (`:43`) throws out of `ExecuteAsync`, which with default `BackgroundServiceExceptionBehavior.StopHost` stops the host. In `DataCatalogSyncBackgroundService.cs:50-96` the call is inside a try, so behaviour differs. With `_clusterState == null` (optional ctor parameter) no lock at all is taken, silently, on multi-instance deployments without the provider.
- Impact: Two replicas can run the same sync concurrently once the run exceeds the lease (large catalog); CDC poll duplicates events since the watermark is local (ARC-F-10).
- Recommendation: Add `IDistributedLease` with auto-renew (background heartbeat at lease/3 and a `CancellationToken LeaseLost`) and pass `LeaseLost` into the sync; make the lock mandatory when `Cluster:Enabled`; factor the loop into one `PeriodicClusterWorker<TJob>` base (see ARC-F-18) with uniform try/catch.
- Effort: M.

### ARC-F-07 - Async query jobs not HA-safe, strictly serial
- Severity: Medium. Category: Concurrency / Scalability.
- Evidence: single `await foreach ... await ProcessJobItemAsync` (`Application/Jobs/Services/AsyncQueryJobBackgroundWorker.cs:55-58`) => one job at a time per node; job registry is `ConcurrentDictionary` in memory (`Jobs/Services/AsyncQueryJobManager.cs:24-25`), channel is in-process (`:39-45`), results go to a node-local `ScratchDirectory` (`:53`, `Worker:104-108`). The worker downcasts the abstraction (`Worker.cs:45` `_jobManager is not AsyncQueryJobManager`) and silently exits if it is anything else.
- Impact: Behind a load balancer, `GET /jobs/{id}` on another replica returns 404; restart loses queued jobs and orphans scratch files; a single long query blocks every other tenant's job (head-of-line blocking); `AsyncJobOwner`/tenant fairness absent.
- Recommendation: Expose `IAsyncJobQueue` (reader) on the abstraction instead of downcasting; run N workers (`MaxConcurrentJobs`, per-tenant semaphore); persist job descriptors in the cluster state store and results in the shared object store (or document sticky routing); startup sweeper for orphaned scratch files.
- Effort: M-L.

### ARC-F-08 - Overlapping catalog concepts
- Severity: Medium. Category: Cohesion.
- Evidence: (1) `Application/Catalog` = internal datasource discovery/search/testing (`Catalog/Services/CatalogDiscoveryService.cs`, `Catalog/Search/*`, 1585 lines); (2) `Application/DataCatalog` + `Extensions/DataCatalog` = external catalog sync (Purview/Collibra/Alation) toggled by `Gateway:Catalog:Enabled` (`Extensions/DataCatalog/DataCatalogServiceCollectionExtensions.cs:41-44`); (3) `Application/OpenMetadata` + `Extensions/OpenMetadata` = a second OpenMetadata integration (permission sync) toggled by `Gateway:OpenMetadata:Enabled`; and `Extensions/DataCatalog/OpenMetadataCatalogAdapter.cs:24` is a third path implementing `IDataCatalogClient` over the same `IOpenMetadataClient`. Both have their own periodic worker and lock key (`catalog:datacatalog:sync`, `catalog:openmetadata:sync`), own `SyncIntervalMinutes`, own webhook handler (`Extensions/DataCatalog/CatalogWebhookHandler.cs` 433 lines, `Extensions/OpenMetadata/OpenMetadataSyncService.cs:HandleWebhookEventAsync`). Plus `Application/Integrations/Backstage` (1 interface, 23 lines) beside `Extensions/Backstage` and `Application/Dbt` (semantic/health) next to `Extensions/Dbt` (ingestion).
- Problem: "Catalog" means four things; the same OpenMetadata endpoint can be polled by two workers with different mapping logic; `Catalog` option naming collides with the `Application/Catalog` module.
- Impact: Divergent governance mapping (Art.9 detection exists in DataCatalogSync; OpenMetadata sync has its own 1154-line version); operator confusion; double load on the catalog.
- Recommendation: Rename for intent (`SchemaDiscovery` / `ExternalCatalogSync`), make OpenMetadata a provider of the DataCatalog abstraction for metadata and keep only the permission-sync part as a distinct capability (`IPermissionSource`); share one scheduler/worker and one webhook receiver with provider routing; collapse single-interface folders (`Application/Integrations`) into their feature.
- Effort: L.

### ARC-F-09 - "Optional" Extensions are not cleanly removable
- Severity: Medium. Category: Coupling.
- Evidence: `Api/Autheris.Api.csproj:8` project reference; Api endpoints import concrete Extension namespaces: `Api/Endpoints/BackstageEndpoints.cs:9`, `ODataEndpoints.cs:21`, `StreamingCdcEndpoints.cs:11`, `IcebergRestCatalogEndpoints.cs:8` (also `Extensions.Lakehouse.Interfaces` has 12 usages inside Extensions that are consumed by Api). Core registration calls `services.AddGatewayExtensions` unconditionally (`Api/Extensions/DependencyInjection/GatewayExecutionServiceExtensions.cs:123`), and `AddGatewayExtensions` has no per-integration toggle (`Extensions/ExtensionsServiceCollectionExtensions.cs:40-48` - only workers are gated). OData (`ODataHandler`, `ODataFilterParser`, 1,400+ lines) and Lakehouse (Iceberg/Delta executors registered as core `IDataSourceExecutor`, `Lakehouse/LakehouseServiceCollectionExtensions.cs:49-52`) are core protocol/data-plane features, not "connectors to foreign systems". `Extensions/Governance/IGovernanceWorkflowHook.cs:7` is an empty alias interface of an Application interface.
- Impact: Disabling an integration still registers HTTP clients, handlers and options validation; removing the project breaks Api compilation; the folder name sets a wrong expectation.
- Recommendation: Move interface contracts the Api consumes (`IBackstageCatalogExportService` already moved, `IODataHandler`, Cdc and Lakehouse interfaces) into Application; move OData and Lakehouse into first-class projects (`Autheris.OData`, `Autheris.Lakehouse`); add per-integration `Enabled` guards inside each `Add*Integration` and a `Gateway:Extensions:<name>:Enabled` convention; delete the alias interface.
- Effort: M.

### ARC-F-10 - CDC watermark in memory
- Severity: Medium. Category: Resilience.
- Evidence: `Extensions/Cdc/CdcSourceServiceCollectionExtensions.cs:25` registers `InMemoryMssqlWatermarkStore` with `TryAddSingleton`; `InMemoryMssqlWatermarkStore.cs:15-30` is a `ConcurrentDictionary`; poller reads default 0 on miss (`MssqlChangeTrackingPoller.cs:113`) and writes after dispatch (`:247`). No durable implementation exists in the repo (grep of `IMssqlWatermarkStore` finds only these).
- Impact: After restart or lock handover to another replica the poller restarts from version 0 and re-emits the entire change history, or (depending on the CHANGETABLE query) skips changes; combined with ARC-F-06, duplicate emission across replicas. Defaulting the PK to `[Id]` (`Poller:121-124`) is a silent data-shape guess.
- Recommendation: Provide `PostgreSqlMssqlWatermarkStore` (or cluster-state-backed) and make registration fail startup when `MssqlChangeTracking.Enabled` and only the in-memory store is present and cluster mode is on; add `CHANGE_TRACKING_MIN_VALID_VERSION` check.
- Effort: S-M.

### ARC-F-11 - Three disjoint extension mechanisms
- Severity: Medium. Category: Extensibility.
- Evidence: (a) `IPluginManager` exposes only HTTP data-source, event listener, action handler (`Application/Plugins/IPluginManager.cs:11-26`); (b) `IIngressInterceptor`/`IEgressInterceptor` are DI-only, registered by hand (`Api/Extensions/DependencyInjection/GatewayCoreServiceExtensions.cs:139-140`) - plugins cannot contribute them; (c) the event bus (`IGatewayEventBus`). `ExtensibilityPipeline` has no try/catch around interceptors (`Application/Extensibility/ExtensibilityPipeline.cs:25-35,48-56`), no timeout, and no per-interceptor failure policy (fail-open vs fail-closed), unlike the bus. Plugin actions have `ExecuteActionAsync` but grep shows no Api caller outside the manager/tests, i.e. the registry is populated yet unused. Plugin hot reload is absent while `PluginsOptions.EnableHotReload` exists (ARC-F-15); `Dispose` unloads all contexts without draining in-flight handlers (`PluginManager.cs:314-345`) and `ExecuteAction` has no timeout (`:106-114`).
- Impact: Extension story is confusing for plugin authors; a throwing interceptor propagates 500 on every request.
- Recommendation: Define one `IGatewayExtensionPoint` model with declared failure mode, timeout and ordering; allow plugin DLLs to contribute interceptors through the manager; wrap each call in a guard (try/catch, `CancelAfter`, metrics); add a plugin registry endpoint/health showing loaded plugins, contract version and last error.
- Effort: M.

### ARC-F-12 - Ad-hoc HttpClient bypasses standard chain
- Severity: Medium. Category: Consistency.
- Evidence: `Application/Catalog/Services/CatalogDiscoveryService.cs:318-325` creates `SecureOutboundHttp.CreatePrimaryHandler(... EgressAllowlist.Create(null, null) ...)` and `new HttpClient(handler)` per call, hard-coded `isDevelopment: false`, no `SsrfProtectionHandler`, no configured allowlist/proxy options, no `IHttpClientFactory` (socket churn, no DNS refresh, no logging/metrics handlers). Similar one-off: `Api/Security/HealthCheckCli.cs:39` (acceptable for CLI).
- Impact: This is the only outbound path in Application that does not honour `GatewayOptions.Egress` (`SecureOutboundHttp.cs:62-67` reads it for every other client).
- Recommendation: Register a named client `EgressIntegrations.OpenApiIngestion` via `AddSecureOutboundHandlers` and inject `IHttpClientFactory`; delete the manual handler construction.
- Effort: S.

### ARC-F-13 - ITSM split across three projects
- Severity: Medium. Category: Cohesion.
- Evidence: clients and webhook handler in `Extensions/Itsm` (`ItsmWebhookHandler.cs` 747 lines, `JiraCloudRestClient.cs`, `ServiceNowTableApiClient.cs`); workflow dispatcher in `Application/Workflows/ItsmWorkflowDispatcher.cs` (registered in `Api/Extensions/DependencyInjection/GatewayGovernanceServiceExtensions.cs:147`); outbox and recertification hosted services in `Infrastructure/Itsm/ItsmOutboxDispatcherHostedService.cs:103` and `ConsentRecertificationHostedService.cs:44`. `Extensions/Itsm/ItsmServiceCollectionExtensions.cs:15` explicitly says "workers stay in the core".
- Impact: Contradicts the claim that each integration lives in its own folder; toggling ITSM requires editing three projects; the only real outbox implementation in the repo (needed by ARC-F-03) is hidden in Infrastructure.
- Recommendation: Keep ports in Application, move all ITSM adapters, outbox, and hosted services into Extensions/Itsm (Infrastructure types needed - the DB store - go behind an Application `IOutboxStore<T>`); extract a generic `OutboxDispatcher<T>` reusable for webhooks.
- Effort: M.

### ARC-F-14 - God-classes
- Severity: Medium. Category: Maintainability.
- Evidence: `Extensions/Dbt/DbtMetadataIngestionService.cs` (1353), `Extensions/OpenMetadata/OpenMetadataSyncService.cs` (1154), `Application/Mcp/Services/AiDataGuardrailService.cs` (1051, mixes prompt-injection, DLP, token estimation, audit, and event publishing: `:185-200,413`), `Extensions/Itsm/ItsmWebhookHandler.cs` (747), `Application/Mcp/Services/HitLStepUpApprovalService.cs` (744, owns locking `:546`, persistence, notifications), `Application/Serialization/ParquetExportService.cs` (664), `Application/Olap/DuckDbOlapEngine.cs` (639).
- Impact: Hard to unit-test in isolation; review hotspots.
- Recommendation: Split by responsibility (parse / map / persist / publish for Dbt and OpenMetadata; detector / policy / auditor for the guardrail); target <400 lines per type.
- Effort: M each.

### ARC-F-15 - Dead members and options
- Severity: Low. Category: Maintainability.
- Evidence: `IHttpDataSourcePlugin.ConfigureServices(IServiceCollection, IConfiguration)` (`Application/Plugins/IHttpDataSourcePlugin.cs:13`) is never called: grep for `.ConfigureServices(` against plugins finds no invocation in `PluginManager.cs` or Api (the only plugin-related hit set is `LoadFromDirectory` for SQL endpoints). `PluginsOptions.EnableHotReload` (`Domain/Options/GatewayOptions.cs:384`) has no consumer (the only hit is SqlEndpoints'). `GatewayOptions.cs:1396` `PluginDirectory` duplicates `Plugins.Directory` (`:383`) with no reader in Api/Infrastructure. `ICloudEventTransformer`/`ICloudEventWebhookDispatcher` unreferenced in production (see ARC-F-01).
- Impact: Plugin authors rely on a promised DI hook that silently does nothing (typed `HttpClient` registration described in the docs comment cannot work).
- Recommendation: Implement (create child provider per plugin, as `ConfigureServices` promises) or remove; remove unused options.
- Effort: S.

### ARC-F-16 - Duplicate webhook dispatchers
- Severity: Low. Category: Consistency.
- Evidence: `Application/Webhooks/WebhookDispatcherService.cs` (header `X-Autheris-Signature-256`, `sha256=` over `timestamp.payload`, replay timestamp, retries) vs `Application/Events/Services/CloudEventWebhookDispatcher.cs:97-103` (header `X-Autheris-Signature`, over payload only, no timestamp, no retry, custom headers allowed with deny-list `:158-175`, `isDev: false` hard-coded `:60`); different config models (`GatewayOptions.Webhooks.Subscriptions` vs `CloudEventWebhookSubscription`); header sets differ (`ce-tenantid` only in the latter); registered with different egress integration names (`Webhooks` vs `"CloudEvents"`).
- Impact: Receivers must support two signature schemes; fixes (e.g. response disposal, timestamp) applied to one only; the second lacks replay protection.
- Recommendation: One `IWebhookDelivery` with a single signing format; keep two adapters only at the subscription-selection level.
- Effort: M.

### ARC-F-17 - Event bus allocations and reflection
- Severity: Low. Category: Performance.
- Evidence: `InMemoryGatewayEventBus.cs:41-57` takes a global `lock` and copies handler lists on every publish, iterating all keys with `IsAssignableFrom`, even for the common "no subscriber" case; `Select(...)` + `Task.WhenAll` allocate per publish (`:60`). `PluginManager.cs:296` uses `handleMethod.Invoke(listener, [@event, ct])` per event (boxing, array alloc, `TargetInvocationException` wrapping); plugin handlers are registered through `Subscribe(Type,...)` that wraps in another delegate.
- Recommendation: Cache handler arrays per concrete event type in a `ConcurrentDictionary<Type, Subscription[]>` invalidated on subscribe/unsubscribe (copy-on-write); compile a `Func<object,object,CancellationToken,Task>` delegate once per listener via `Delegate.CreateDelegate`/expression tree.
- Effort: S.

### ARC-F-18 - Inconsistent hosted-service patterns
- Severity: Low. Category: Consistency.
- Evidence: Loop styles: `Task.Delay` + manual try/break (`Cdc/MssqlChangeTrackingHostedService.cs:53-120`), `PeriodicTimer` (`OpenMetadata/OpenMetadataSyncBackgroundService.cs:45-50`), `Task.Delay` unguarded at start (`DataCatalog/DataCatalogSyncBackgroundService.cs:46`). Initial delay: 15 s for one, none for others. Options re-read from `IOptions` (not `IOptionsMonitor`) so interval changes need restart; scope APIs differ (`CreateScope` vs `CreateAsyncScope`). Registration of hosted services inside `DataCatalogServiceCollectionExtensions.cs` and via `AddHostedService` in Api for others (`GatewayExecutionServiceExtensions.cs:134`).
- Recommendation: One `ClusterScheduledService<TJob>` base class implementing the loop, lease (ARC-F-06), jitter, metrics (`last_success`, `duration`), and uniform graceful shutdown.
- Effort: M.

### ARC-F-19 - Runtime/IO concerns in Application
- Severity: Low. Category: Layering.
- Evidence: Application contains `Mcp/Services/McpStdioRunner.cs` (stdin/stdout host), `Serialization/ArrowFlightSqlServer.cs`, `Docs/Services/FileSystemUniversalDocStore.cs` (446 lines, file system), `Olap/DuckDbOlapEngine.cs` (DuckDB driver, `Autheris.Application.csproj:12`), `Jobs` scratch-file IO, and 2 hosted-service bridges (`Webhooks/WebhookEventBridgeHostedService.cs`, `Catalog/Search/CatalogSearchWarmupService.cs`). The csproj carries `DuckDB.NET.Data.Full`, `Parquet.Net`, `Apache.Arrow`, `Casbin.NET`, `prometheus-net` (`Autheris.Application.csproj:11-28`), so "Application" cannot be referenced without heavy native dependencies. Mcp is also split: protocol/handlers in Application (7,236 lines) and transport in `Api/Mcp/GatewayMcpServer.cs` (475) - that split is fine, but `McpStdioRunner` is transport.
- Recommendation: Move adapters with native/IO dependencies to Infrastructure (or the new Extensions projects from ARC-F-09), leaving ports in Application; move `McpStdioRunner` next to `GatewayMcpServer`.
- Effort: M.

### ARC-F-20 - German contract docs in public plugin API
- Severity: Low. Category: Consistency.
- Evidence: `Application/Plugins/IHttpDataSourcePlugin.cs:11-18` ("Eindeutiger Bezeichner...", "Registriert pluginspezifische Abhaengigkeiten", "Fuehrt den Aufruf aus"), contradicting `CLAUDE.md` mandate 1 (English only).
- Recommendation: Translate; add an analyzer/CI grep to prevent regressions.
- Effort: S.

## 5. Dead-code / speculative generality spot-check

- Production-unreferenced (tests only): `ICloudEventTransformer`, `ICloudEventWebhookDispatcher`, `CloudEventSubscriptionStore.GetSubscriptionsAsync` (ARC-F-01).
- Single-type modules: `Application/Integrations` (1 file, 23 lines), `Application/Plugins` (5 files, 130 lines), `Application/Observability` (2 files, 128 lines) - cohesion candidates for merging into their sibling features.
- `Extensions/Governance/IGovernanceWorkflowHook.cs` is an empty alias of the Application interface (consumers `Application/Governance/Services/ClassificationEngine.cs:24,33` use the Application one).
- Features in Application used by exactly one endpoint file (FinOps -> `Api/Endpoints/FinOpsEndpoints.cs`, `Api/Middleware/FinOpsBudgetMiddleware.cs`; Mesh -> `Api/Endpoints/EnvoyExtAuthzEndpoints.cs`) are legitimate but good candidates for the same optional-module treatment as ARC-F-09.

## 6. Suggested sequencing

1. Decide ARC-F-01 (wire or remove) - correctness/trust issue.
2. ARC-F-02 and ARC-F-03 together (queue-based bus plus outbox) - removes request-path coupling and gives defined delivery semantics.
3. ARC-F-05, ARC-F-06, ARC-F-18 (shared resilience + scheduled-worker base) - mechanical, benefits all integrations.
4. ARC-F-04 and ARC-F-11 before the plugin ecosystem grows (contract versioning is cheap now, expensive later).
5. Structural cleanup (ARC-F-08, 09, 13, 19) as planned refactors.
