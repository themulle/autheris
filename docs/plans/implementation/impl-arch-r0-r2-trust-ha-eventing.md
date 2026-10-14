# Implementation Plan: Architecture Waves R0 and R2 (Trust, HA & State, Eventing)

**Document ID:** `PLAN-IMPL-ARC-A`
**Parent plans:** [`../00-master-plan-overview.md`](../00-master-plan-overview.md), [`../architecture.md`](../architecture.md) (themes T2, T5, T6; waves R0 and R2)
**Related:** [`../security.md`](../security.md) (Wave C items SR-L-06..10, SR-L-13/14, SR-L-18)
**Scope:** ARC-* findings of theme T2 (unified audit contract), T5 (cluster/HA semantics), T6 (eventing, webhooks, plugin contracts), plus the R0 items ARC-F-01, ARC-I-03, ARC-C-05, ARC-C-04, ARC-F-02 and ARC-S-06 (the only items the summary assigns to R0; all are covered below).
**WP prefix:** `WP-ARC-A-NN`
**Date:** 2026-10-14
**Phase:** 2 (Solution Architect). Planning only; nothing in this document has been built or tested.

Notes on terminology: `architecture.md` calls the cluster state port `IClusterStateProvider`. The real type is `IDistributedClusterStateProvider` (`src/Autheris.Application/State/IDistributedClusterStateProvider.cs`). This plan uses the real name. Detailed finding text was read from `git show d8db81cc^:docs/plans/2026-10-14-architecture-code-review/0{1..6}-*.md`.

---

## 1. Verification table (status on main 317e65b8)

| Finding | Status | Evidence |
|---|---|---|
| ARC-F-01 CDC webhooks never delivered | **Open** | `ICloudEventTransformer`, `ICloudEventWebhookDispatcher`, `ICloudEventSubscriptionStore` registered at `Api/Extensions/DependencyInjection/GatewayExecutionServiceExtensions.cs:135-137`; CRUD endpoints `Api/Endpoints/StreamingCdcEndpoints.cs:97-181`; no production caller of `Transform`/`DispatchAsync`/`GetSubscriptionsAsync` outside `Application/Events/` (grep of `src`); store is `InMemoryCloudEventSubscriptionStore` (`Application/Events/Services/`). |
| ARC-F-02 sync event fan-out | **Partially resolved** | SR-M-13 added a per-handler timeout and `WaitAsync` (`Application/Events/InMemoryGatewayEventBus.cs:132-137`) and a queued bridge (`Application/Webhooks/WebhookEventBridgeHostedService.cs`, bounded `Channel`, 4 workers, drop-on-full). Still open: `PublishAsync` awaits `Task.WhenAll` (`InMemoryGatewayEventBus.cs:60-62`), so publishers wait up to the 10 s handler timeout; a cancelled publisher token drops the event for all handlers (`:139-143`); plugin listeners run inline. |
| ARC-F-03 volatile, unclassified retries | **Open** | `Application/Webhooks/WebhookDispatcherService.cs`: timestamp/signature computed once outside the retry loop (`:90-91`), every non-2xx retried including 4xx, `response` never disposed (`:134`), `Task.Delay` outside try (`:164`), no jitter/Retry-After, no outbox, no dead letter. Bridge drops events when its 1024-entry queue is full. |
| ARC-F-04 unversioned plugin contract | **Open** | `Infrastructure/Plugins/PluginAssemblyLoadContext.cs:71-79` shares `Autheris.Domain*`/`Autheris.Application*`; grep for `ContractVersion`/`PluginApiVersion`/`AutherisPluginContract` finds nothing. |
| ARC-F-11 three disjoint extension mechanisms | **Open** | `Application/Extensibility/ExtensibilityPipeline.cs` has no per-interceptor failure policy/timeout (per review `:25-56`); interceptors DI-only. |
| ARC-F-15 dead members/options | **Open** | `IHttpDataSourcePlugin.ConfigureServices` (`Application/Plugins/IHttpDataSourcePlugin.cs:15`) has no caller; `PluginsOptions.EnableHotReload` (`Domain/Options/GatewayOptions.cs:387`) has no consumer; `PluginDirectory` (`:1407`) duplicates `Plugins.Directory`. |
| ARC-F-16 two webhook dispatchers | **Open** | `X-Autheris-Signature-256` over `timestamp.payload` (`WebhookDispatcherService.cs`) vs `X-Autheris-Signature` over payload only, no timestamp, no retry (`Events/Services/CloudEventWebhookDispatcher.cs:97-103`, `isDev: false` at `:60`). |
| ARC-F-17 event bus allocations/reflection | **Open** | Global lock + `IsAssignableFrom` scan + list copy per publish (`InMemoryGatewayEventBus.cs:41-57`); `PluginManager` uses `MethodInfo.Invoke` per event. |
| ARC-F-20 German plugin API docs | **Open** | `Application/Plugins/IHttpDataSourcePlugin.cs:11,14` ("Eindeutiger Bezeichner", "Registriert pluginspezifische..."). `Application/Interfaces/IEventBus.cs:9-10` is also German. |
| ARC-A-02 hand-written audit, mixed fail-open/closed | **Open** | 14 `GetService<IAuditLogRepository>()` sites in Api: `GovernanceEndpoints.cs:112,319,783,935`, `VirtualFilterEndpoints.cs:232,284,320,538`, `SqlEndpointRoutes.cs:139`, `SecurityContextResolutionMiddleware.cs:153`, `DuckDbOlapEndpoints.cs:110` (the only fail-closed one, local `TryAuditAsync` `:115`). |
| ARC-C-03 audit failure semantics differ per path | **Partially resolved** | The bare `catch { }` at `GovernedSqlExecutor` DML failure audit is now logged (`Sql/Services/GovernedSqlExecutor.cs:395-399`), but the policy is still per-call-site; `GatewayExecutionService.cs:175,257,278,525,661` awaits un-guarded; ALLOW audit is still written after rows materialise (`:278`). No `AuditGuard`/`IAuditWriter` exists (`Application/Audit/` has only `NullAuditLogRepository`, `AuthFailureAggregator`). |
| ARC-C-04 Null* defaults in production constructors | **Open** | `NullAuditLogRepository.Instance` in `DifferentialPrivacyEngine.cs:36`, `EuAiActAuditExporter.cs:35`, `AiDataGuardrailService.cs:86`, `GovernedProcedureExecutionService.cs:74`, `GovernedSqlExecutionService.cs:76,135`, `ConsentRecertificationWorkflowService.cs:37`, `Extensions/Lakehouse/Services/IcebergRestCatalogFederationService.cs:53`; `NullPolicyEnforcementService.Instance` fallbacks in `GovernedProcedureExecutionService.cs:393`, `GatewayExecutionService.cs:579`, `FederatedDuckDbExecutionService.cs:120`, `GovernedSqlRewriter.cs:658`, `UnifiedPolicyDecisionPoint.cs:92`, `DefaultCrossDomainAccessResolver.cs:47`. |
| ARC-C-09 swallowing catches | **Partially resolved** | `Policy/RebacTableGate.cs:71-74` still `catch { return false; }` (also swallows cancellation); `ClusterTotpSecretStore.cs` catches remain; `GovernedSqlExecutor.cs` bare catches replaced by logging. |
| ARC-C-14 synchronous audit on hot path | **Open** | `GatewayExecutionService.cs:175,257,278,525,661` and `GovernedSqlExecutor.cs` await `RecordAuditEventAsync` per request; no `IAuditSink`. |
| ARC-C-17 test support in production assembly | **Open** | `NullAuditLogRepository`, `NullPolicyEnforcementService`, `NullCompiledSqlQueryPlanCache`; `#if DEBUG` in `Security/TenantRegistryGate.cs`. |
| ARC-S-06 `#if DEBUG` security seams | **Open** | 32 `#if DEBUG` blocks in 20 files under `src/` (`TrinoSqlEngine/GovernedSqlCompiler.cs` x4, `Ast/Emit/CompiledSqlTemplate.cs` x3, `Ast/Security/TenantPredicateFactory.cs` x3, `CompilerTestSeams.cs`, `FastSqlEngine.cs`, `ExecutionStackGuard.cs`, `Application/Security/TenantRegistryGate.cs`, `Application/Sql/Errors/SqlCompilerErrorMapper.cs`, `Infrastructure/Persistence/SessionPinning.cs`, `Infrastructure/Persistence/Catalog/GovernedCatalogProvider.cs`, etc.). |
| ARC-C-05 access plan state process-local and unbounded | **Open** | `Governance/Services/AccessPlanningService.cs:49-51` three `ConcurrentDictionary` (`_plans`, `_consumedTokens`, `_inMemoryVault`); no removal anywhere; confirmation token HMAC key is a static per-process random (`:35`, used `:511,536`) so tokens cannot validate on another node; `_inMemoryVault` is written (`:347`) and never read (dead plaintext store). |
| ARC-I-03 Redis provider maps failure to absent | **Open** | `Infrastructure/State/RedisClusterStateProvider.cs:56-64` (Get), `:78` (Set swallow), `:92-95` (Remove false), `:115-118` (Increment null), `:236-240` (TryAcquireLock returns null on error, indistinguishable from "busy"). Precedent for a typed outcome exists only for budgets (`BudgetConsumeOutcome.StoreUnavailable`). |
| ARC-I-07 pub/sub at-most-once | **Partially resolved** | `Application/Interfaces/IEventBus.cs:9-10` documents "no delivery guarantee; correctness via epochs/pull validation" (German). Open: no dropped-publish/failed-handler metric, `ConsentCacheService` `catch { }` blocks, fire-and-forget handler in the Redis provider. |
| ARC-I-08 embedded Garnet per-node island | **Partially resolved** | `Api/Extensions/GatewayServiceCollectionExtensions.cs:158-170` and `GatewayStartupValidator.cs:448-451` require `Caching.Redis.Enabled` when `MultiNodeClusterMode` or `Replicas > 1`. Gap: `GatewayStorageServiceExtensions.cs:45` takes the embedded branch first, so `EnableEmbeddedServer=true` plus `Redis.Enabled=true` passes validation while every replica runs its own Garnet; the five registrations are duplicated (`:67-73` vs `:81-87`). |
| ARC-G-03 subscriptions single-node | **Open** (root cause differs from review) | `AddInMemorySubscriptions` still at `Api/Extensions/GatewayServiceCollectionExtensions.cs:630`, but the data path is `ICdcEventChannel.SubscribeAsync` (`GraphQL/Subscriptions/Subscription.cs:113`) bound to `Infrastructure/Streaming/InMemoryCdcEventChannel.cs`; the per-subject limiter is a per-process `ConcurrentDictionary` (`GraphQL/Subscriptions/CdcSubscriptionGovernor.cs:32`). The MSSQL poller holds a cluster lock (`cdc:mssql:poll`), so only one replica ingests; subscribers on the other replicas never see events. |
| ARC-F-06 lock leases not renewed | **Open** | `Extensions/OpenMetadata/OpenMetadataSyncBackgroundService.cs:40-64`: `TryAcquireLockAsync` is outside any try, initial sync at `:43` unguarded; lease `max(5, interval)` minutes; CDC lease `max(5000, 3*interval)` ms (`Cdc/MssqlChangeTrackingHostedService.cs:70`); `_clusterState == null` silently means no lock. `TryAcquireLockAsync` returns only `IAsyncDisposable?`, no renewal or fencing. |
| ARC-F-07 async jobs not HA-safe, serial | **Open** | `Application/Jobs/Services/AsyncQueryJobBackgroundWorker.cs:48-52` downcasts to `AsyncQueryJobManager` and exits silently otherwise; `AsyncQueryJobManager.cs:26-27` `ConcurrentDictionary` job registry, in-process channel (`:25`), node-local scratch. |
| ARC-F-10 CDC watermark in memory | **Open** | `Extensions/Cdc/CdcSourceServiceCollectionExtensions.cs:25` `TryAddSingleton<IMssqlWatermarkStore, InMemoryMssqlWatermarkStore>`; no durable implementation. |
| ARC-C-07 Casbin decision cache and epoch | **Open** | `Governance/CasbinEnforcementService.cs:713` ~12-part string key per call, `:724-727` non-atomic `Count >= 10_000` then `Clear()`, `DecisionCacheTtl = 60 s` (`:132`); `Epoch` is a per-node snapshot counter (`:423`), no cluster publish. |
| ARC-C-13 in-memory provider races | **Open** | `State/InMemoryClusterStateProvider.cs:60-80` `result` captured and written inside `AddOrUpdate` factories (may run more than once); `_budgetLock` does not guard `IncrementAsync`. |
| ARC-L-16 static replay cache | **Partially resolved** | `GraphQL/Federation/SubgraphSecurityValidator.cs:20,85-100` still has a static local nonce dictionary but now also checks `IDistributedClusterStateProvider.IncrementAsync`; if the shared store throws it falls back to the local cache only. The static `PlanCache` in `StreamingRowFilterAstEvaluator.cs:23` is unbounded (not re-verified for growth). |

No item in scope is fully resolved. F-02, C-03, C-09, I-07, I-08, L-16 are partially resolved by Wave A/B (SR-M-13 and related) and the remaining slice is what this plan covers.

---

## 2. Work packages

Conventions: all new tests follow Red-Green-Refactor. Unit tests go to `tests/Autheris.Tests.Unit/` (until R6 splits it), architecture tests to `tests/Autheris.Tests.Architecture/`, extension tests to `tests/Autheris.Extensions.Tests/`, integration tests to `tests/Autheris.Tests.Integration/` (set `DOTNET_USE_POLLING_FILE_WATCHER=true` under WSL). Schema changes follow the existing per-backend `*GovernanceRepository.Schema.cs` pattern until the R3 migration runner (T4) exists; each DDL is written as a standalone, idempotent, versionable script constant so R3 can adopt it unchanged.

### WP-ARC-A-01 - Interim guard for CDC webhook subscriptions (R0)

- **Findings:** ARC-F-01 (immediate part).
- **Goal:** Stop accepting subscriptions that are never delivered. Until WP-ARC-A-03 ships, the three `/api/v1/cdc/subscriptions` endpoints must not return 200/201.
- **Design:**
  - Add `StreamingOptions.CdcWebhooks.Enabled` (default `false`) in `Domain/Options/GatewayOptions.cs` (new small nested class; the options split itself belongs to R4/T7).
  - In `Api/Endpoints/StreamingCdcEndpoints.cs:97-181` return `Results.Problem(statusCode: 501, ...)` when disabled; skip registering `ICloudEventSubscriptionStore`/dispatcher HTTP client in `GatewayExecutionServiceExtensions.cs:135-137` when disabled (keeps egress client list honest).
  - Add startup log warning when enabled while WP-ARC-A-03 is not present (removed by A-03).
- **Affected files:** `StreamingCdcEndpoints.cs`, `GatewayExecutionServiceExtensions.cs`, `GatewayOptions.cs`, `appsettings*.json`, `docs/features/` (CDC webhook feature page: state "disabled by default").
- **Compatibility:** Behaviour change: POST/GET/DELETE return 501 by default. No live receiver can exist today, so no consumer breaks.
- **Security (fail-closed):** disabled means no store writes and no egress clients. Tests asserting SSRF/HMAC on the dormant classes stay valid as unit tests but are re-labelled as component tests in A-03.
- **TDD:** `tests/Autheris.Tests.Unit/Streaming/CdcWebhookSubscriptionGuardTests`. Red first: `Post_Subscription_WhenCdcWebhooksDisabled_Returns501_AndStoresNothing`. Also `Get_WhenDisabled_Returns501`, `Post_WhenEnabled_StillRequiresGovernanceOrTenantAdmin`.
- **Acceptance:** default config returns 501; enabling flag restores current behaviour; no store registration when disabled.
- **Effort/Risk:** S / low.

### WP-ARC-A-02 - Webhook outbox and unified delivery core (R2)

- **Findings:** ARC-F-03, ARC-F-16 (and the retry/signing/error-sanitising parts of SR-L-13 and SR-L-14).
- **Goal:** One durable at-least-once webhook delivery path: outbox table, retry classification, fresh timestamp/signature per attempt, dead letter, per-subscription circuit breaker, single signing format.
- **Design:**
  - Domain (`Autheris.Domain/Model`): `WebhookOutboxMessage` (Id, SubscriptionId, TenantId, EventId, EventType, PayloadJson (already masked), TargetUrl snapshot reference, Attempt, NextAttemptAt, State {Pending, InFlight, Delivered, Dead}, LeaseUntil, LastStatusCode, LastErrorCode (not free text), CreatedAt). Unique constraint on `(SubscriptionId, EventId)` for idempotent enqueue.
  - Port `IWebhookOutbox` (`Autheris.Application/Webhooks/`): `EnqueueAsync`, `ClaimBatchAsync(limit, leaseDuration, ct)` (claim with lease: `FOR UPDATE SKIP LOCKED` on PostgreSQL, `READPAST` on SQL Server, single-writer on SQLite), `CompleteAsync`, `RescheduleAsync`, `DeadLetterAsync`, `ListDeadLettersAsync`, `CountPendingAsync` (metric). A claim lease removes the need for a cluster lock (the ITSM outbox needs one because it has no claim: `Infrastructure/Itsm/ItsmOutboxDispatcherHostedService.cs:94-110`).
  - Implementations as new partials `PostgreSqlGovernanceRepository.WebhookOutbox.cs`, `SqlServerGovernanceRepository.WebhookOutbox.cs`, `SqliteGovernanceRepository.WebhookOutbox.cs` next to the existing `*.Outbox.cs` files, plus schema DDL in each `*.Schema.cs`.
  - `WebhookRetryPolicy.Classify(statusCode | exception)` returns `Retry` (5xx, 408, 429 honouring `Retry-After` capped at a configured maximum, network error, timeout), `Permanent` (other 4xx, malformed URL, egress/SSRF rejection) or `Success`. Backoff: exponential with full jitter, base 5 s, cap 1 h, default 8 attempts then `Dead`. `TimeProvider` injected.
  - `WebhookDeliveryClient` replaces both dispatchers' HTTP code: `using var response`, per-attempt `X-Autheris-Timestamp` and `X-Autheris-Signature-256: sha256=HMAC(secret, "{timestamp}.{payload}")`, header deny-list from `CloudEventWebhookDispatcher.IsForbiddenCustomHeader`, `EgressUrlPolicy.ValidateResolvedAsync` per attempt with `isDev` from environment (the hard-coded `false` goes away), error messages reduced to stable codes (no resolved IPs, no exception text).
  - `WebhookDeliveryHostedService`: N workers (`Webhooks.DeliveryWorkers`, default 4), `PeriodicTimer` poll, claim, deliver, update. Per-subscription circuit: after `Webhooks.AutoDisableAfterConsecutiveFailures` (default 20) the subscription is skipped until a cool-down passes; state kept in the outbox row set (no new store).
  - `WebhookEventBridgeHostedService` changes from "queue + dispatch" to "match subscriptions + `EnqueueAsync`" for configured `Webhooks.Subscriptions`. `IWebhookDispatcherService.DispatchEventAsync` is kept as a thin enqueue facade for existing callers/tests.
  - Honest scope on "transactional": phase 1 is durable-enqueue-at-bus-handler (at-least-once once enqueued). A same-transaction enqueue is added only where the publisher already owns a `DbTransaction` (the audit repository has `RecordAuditEventAsync(entry, DbTransaction)`); that is an optional phase 2 and must not block this WP.
  - Retire `CloudEventWebhookDispatcher` as a transport; `ICloudEventWebhookDispatcher` becomes a builder that produces an outbox message (consumed by A-03). `CloudEventWebhookSubscription.HmacSecret` signing uses the unified format.
- **Affected files:** `Application/Webhooks/*`, `Application/Events/Services/CloudEventWebhookDispatcher.cs`, `Domain/Model/*`, `Domain/Options/GatewayOptions.cs` (`WebhooksOptions`), three `*GovernanceRepository.*` families under `Infrastructure/Persistence/`, `Api/Extensions/DependencyInjection/GatewayExecutionServiceExtensions.cs`.
- **Compatibility:** Configured `Webhooks.Subscriptions` keep their current headers (`X-Autheris-Signature-256`, `X-Autheris-Timestamp`) so existing receivers continue to work. The CloudEvent path never delivered, so changing it to the same scheme breaks nobody. Behaviour change: 4xx responses are no longer retried; `MaxRetries` semantics become "max attempts" explicitly (rename with config alias for one release). New table in the governance DB (all three backends; SQLite allowed only single-node, already enforced by `GatewayStartupValidator.cs:586-590`).
- **Security:** payload stored in the outbox is already masked/tenant-scoped by the producer (A-03) or is a domain event from the bus; never store raw CDC rows. Secrets are not copied into outbox rows (resolve at delivery). Delivery re-validates egress at send time (DNS rebinding). Dead-letter listing is tenant-scoped.
- **TDD:** `tests/Autheris.Tests.Unit/Webhooks/WebhookRetryPolicyTests`, `WebhookDeliveryClientTests`, `WebhookOutboxContractTests` (one abstract suite run against Sqlite plus PG/SQL Server in Integration), `WebhookDeliveryHostedServiceTests` with `FakeTimeProvider`. Red first: `Classify_Http404_IsPermanent_AndNotRetried`; then `Delivery_Retry_RecomputesTimestampAndSignature`, `Enqueue_SameSubscriptionAndEventTwice_StoresOnce`, `TwoWorkers_ClaimDisjointBatches`, `Delivery_ErrorMessage_ContainsNoResolvedIp`.
- **Acceptance:** event survives process restart; 5xx retried with growing delay and fresh signature; 4xx dead-lettered immediately; two replicas never deliver the same row concurrently; both old dispatchers' unit tests ported to the new client.
- **Effort/Risk:** XL (about 5-6 days) / medium (three SQL dialects, new table).

### WP-ARC-A-03 - CDC webhook delivery service and durable subscriptions (R2)

- **Findings:** ARC-F-01 (full fix), completes ARC-F-16.
- **Goal:** Registered CDC subscriptions actually receive governed, masked events, durably.
- **Design:**
  - `CdcWebhookDeliveryService : BackgroundService` (Application/Streaming or Extensions/Cdc, depends only on ports): reads `ICdcEventChannel.SubscribeAsync("cdc_all")`, resolves matching subscriptions with `ICloudEventSubscriptionStore.GetSubscriptionsAsync(tenant, table, op)`, applies `IStreamRlsPolicyEnforcer.EvaluateAndMaskAsync(cdcEvent, subscriptionPrincipal)` (`Application/Streaming/Services/StreamRlsPolicyEnforcer.cs:72`), builds the envelope with `ICloudEventTransformer.Transform(cdcEvent, maskedPayload)` and enqueues into `IWebhookOutbox` (A-02). Masking happens before enqueue, so raw data is never persisted.
  - Subscription principal: extend `CloudEventWebhookSubscription` (`Domain/Model/CloudEventModels.cs:25`) with `CreatedBySid`, `CreatedByGroupSids`, `CreatedByRoles` captured by the POST endpoint from the authenticated admin. The delivery service builds a `ClaimsPrincipal` from that snapshot; missing snapshot means deny (fail-closed). Policy is evaluated at event time with the current epoch, so revoking the creator's access stops delivery.
  - Durable store: `ICloudEventSubscriptionStore` implementation in governance DB (three backends, partial `*.CloudEventSubscriptions.cs`). `HmacSecret` is stored only as a vault reference via `IKeyVaultSecretProvider.SetSecret` (`Application/Interfaces/IKeyVaultSecretProvider.cs:9`); when the provider throws `NotSupportedException` the POST returns 503/501 (fail closed) rather than persisting a plaintext secret. `InMemoryCloudEventSubscriptionStore` remains for Development and tests.
  - Cluster: every replica may run the service; dedupe is by the outbox unique key `(SubscriptionId, EventId)`, so no lock is needed. With A-13 the channel carries events from the polling replica to all replicas.
  - Remove the A-01 warning; keep the feature flag default `false` until the end-to-end test is green, then flip the default in a separate commit.
- **Affected files:** new service, `Domain/Model/CloudEventModels.cs`, `StreamingCdcEndpoints.cs` (capture creator identity), `Application/Events/Services/CloudEventTransformer.cs`, `Infrastructure/Persistence/*` (subscriptions table), DI.
- **Compatibility:** New columns/table; `CloudEventWebhookSubscription` record gains optional members (positional record: add trailing members with defaults to avoid breaking callers). Subscriptions created before this change (in-memory only) do not exist across restarts, so no migration.
- **Security:** tenant isolation via enforcer step 1 (`TenantId` check); `cdc_all` consumption is internal only; unauthorised events dropped ("zero leakage" as in `Subscription.cs:143`); SSRF validated at create time and at every delivery (A-02). Audit each delivery batch summary as Tier-B (A-06).
- **TDD:** `tests/Autheris.Tests.Integration/CdcWebhookEndToEndTests`. Red first: `RegisteredSubscription_ReceivesMaskedInsertEvent_ViaOutbox`. Plus unit: `Service_DropsEvent_WhenSubscriberLacksColumnPermission`, `Service_DropsEvent_WhenCreatorSnapshotMissing`, `Service_NeverPersistsUnmaskedPayload`, `Post_Subscription_WhenSecretVaultUnsupported_ReturnsFailClosed`.
- **Acceptance:** end-to-end delivery with correct signature; masked columns absent in outbox payload; restart keeps subscriptions; duplicate ingestion yields one delivery.
- **Effort/Risk:** L (about 4 days) / medium-high (masking identity is a security-sensitive design; see Open Decision 2).

### WP-ARC-A-04 - Bounded asynchronous event bus (R0)

- **Findings:** ARC-F-02 (remaining part), ARC-F-17.
- **Goal:** `PublishAsync` never waits for subscribers; slow or hung listeners cannot stall request paths or each other.
- **Design:**
  - In `Application/Events/InMemoryGatewayEventBus.cs` give each subscription its own bounded `Channel<IGatewayEvent>` (capacity `Events.SubscriberQueueCapacity`, default 1024, `FullMode = DropOldest`) with a single reader task that keeps the existing per-handler timeout and exception isolation (`InvokeHandlerSafelyAsync`). `PublishAsync` does only `TryWrite` per matching subscription and returns a completed task. The reader runs on the bus's own lifetime token, so a cancelled publisher no longer drops events.
  - Dropped events increment `autheris_event_bus_dropped_total{event_type}`; handler timeouts increment `autheris_event_bus_handler_timeout_total`.
  - Replace the global lock and `IsAssignableFrom` scan with a copy-on-write `ConcurrentDictionary<Type, Subscription[]>` keyed by concrete event type, invalidated on subscribe/unsubscribe (F-17). `PluginManager` compiles one `Func<object, object, CancellationToken, Task>` per listener instead of `MethodInfo.Invoke`.
  - Add `EventBusOptions.DeliveryMode { Queued (default), Inline }`; `Inline` keeps today's awaited semantics for tests only and is rejected outside Development by `GatewayStartupValidator`.
  - Security-relevant audit must not depend on the bus: confirm `AiDataGuardrailService.cs:194,413` and `AccessPlanningService.cs:153,306,430` write their audit record through the audit path (A-06) and treat the bus event as notification only; add a test per call site.
  - Webhook bridge keeps working (it already queues) and is simplified in A-02.
- **Affected files:** `Application/Events/InMemoryGatewayEventBus.cs`, `IGatewayEventBus.cs`, `Infrastructure/Plugins/PluginManager.cs`, `Domain/Options/GatewayOptions.cs`, `GatewayStartupValidator.cs`, `Domain/Diagnostics/GatewayDiagnostics.cs`.
- **Compatibility:** Behaviour change: after `PublishAsync` returns, handlers may not have run. Existing tests that assert handler side effects directly after `PublishAsync` must use `Inline` mode or a new `IGatewayEventBusDrain.DrainAsync()` test helper. Delivery semantics documented: in-process, at-most-once, per-subscriber ordered, lossy under overload.
- **Security:** a full queue drops the oldest event rather than blocking the request path; this is acceptable only because no Tier-A decision may rely on the bus (checked by the per-call-site tests above).
- **TDD:** `tests/Autheris.Tests.Unit/Events/InMemoryGatewayEventBusQueueTests`. Red first: `PublishAsync_WithHangingHandler_ReturnsImmediately`. Then `SlowHandler_DoesNotDelayOtherHandlers`, `CancelledPublisherToken_StillDeliversEvent`, `FullQueue_DropsOldest_AndIncrementsMetric`, `Subscribe_Unsubscribe_UpdatesCachedHandlerArray`.
- **Acceptance:** publish latency independent of handler duration; no event lost on publisher cancellation; metrics present.
- **Effort/Risk:** M (2-3 days) / medium (test-suite churn from changed completion semantics).

### WP-ARC-A-05 - Remove Null* fallbacks from production constructors (R0)

- **Findings:** ARC-C-04, ARC-C-17 (Null objects part).
- **Goal:** A mis-registered container fails at startup instead of silently dropping audit or policy.
- **Design:**
  - Delete the convenience overloads that hard-wire `NullAuditLogRepository.Instance` in the eight classes listed in the verification table; make `IAuditLogRepository` and `IPolicyEnforcementService` non-nullable constructor parameters everywhere in `src/`.
  - Remove `?? NullPolicyEnforcementService.Instance` at the six listed sites.
  - Move `NullAuditLogRepository`, `NullPolicyEnforcementService`, `NullCompiledSqlQueryPlanCache` to a test-only location (`tests/Autheris.Tests.Common` if present, otherwise a new `tests/Autheris.Testing` project referenced by the test projects only). Production code that legitimately has no cache (`NullCompiledSqlQueryPlanCache`) gets an explicit `NoOpCompiledSqlQueryPlanCache` registered by composition root with a documented name; it is not a security control.
  - Group the 24-parameter `GovernedSqlExecutionService` constructor into `GovernedSqlDependencies` and register `GovernedSqlRewriter`/`GovernedSqlExecutor` in DI; break the federated cycle with `Lazy<IFederatedQueryExecutionService>` instead of `IServiceProvider`. This part overlaps R1 (pipeline convergence) hot files; take only the audit/policy non-null change here and leave the dependency grouping to the R1 owner (declared in section 4).
  - Add `ValidateOnBuild`/`ValidateScopes` startup test that resolves all governed services.
- **Affected files:** the eight constructor sites above, `Application/Audit/NullAuditLogRepository.cs`, `Application/Policy/Services/NullPolicyEnforcementService.cs`, DI extensions, many test files constructing the short overloads (mechanical change).
- **Compatibility:** compile-time break for code calling the short overloads (tests, Extensions). No config change.
- **Security:** converts silent fail-open composition into startup failure.
- **TDD:** `tests/Autheris.Tests.Architecture` rule `NoProductionTypeReferencesNullTestDoubles` (red first: currently 8+6 violations), `tests/Autheris.Tests.Unit/Composition/GovernedServicesResolveOnBuildTests`.
- **Acceptance:** architecture test green; `grep NullAuditLogRepository src` empty; host fails to start when `IAuditLogRepository` is unregistered.
- **Effort/Risk:** M (2-3 days, mostly mechanical test updates) / medium (merge conflicts with R1 on `GovernedSqlExecutionService` and `GatewayExecutionService`).

### WP-ARC-A-06 - Unified audit contract: `IAuditWriter` and `AuditPolicy` (R0/R1 boundary)

- **Findings:** ARC-A-02, ARC-C-03, ARC-C-09 (audit and authorization-dependency catches).
- **Goal:** One place decides what happens when the audit write fails.
- **Design:**
  - `Application/Audit/IAuditWriter`: `RecordRequiredAsync(AuditLogEntry, ct)` (throws `AuditUnavailableException`; callers abort the operation: "no audit, no data" for ALLOW and for admin config changes) and `RecordBestEffortAsync(AuditLogEntry, ct)` (logs, counts `autheris_audit_write_failed_total{tier}`, never throws). `AuditPolicy` enum `{ FailClosed, BestEffort }` selected per `AuditEventTypes` entry via a static table with a unit test that every event type has an explicit policy (DENY records are `FailClosed` for the audit step but the denial itself still happens; the helper must never convert a denial into an allow).
  - Implementation `AuditWriter` fills `OccurredAt`, `TraceId`, `TenantId`, actor from `ISecurityContextAccessor`/`HttpContext` consistently (today set inconsistently: `GovernanceEndpoints.cs:121` vs `VirtualFilterEndpoints.cs:243`) and wraps `IAuditLogRepository`. Constructor-injected, non-optional.
  - Replace the 14 `GetService<IAuditLogRepository>()` sites in Api with `IAuditWriter`; delete `DuckDbOlapEndpoints.TryAuditAsync` in favour of `RecordRequiredAsync`. Replace `_auditLogRepository.RecordAuditEventAsync` calls in `GatewayExecutionService.cs:175,257,278,525,661` with the writer so the policy is explicit (behaviour of the awaited ALLOW write is unchanged: fail-closed; the performance change is A-07).
  - Replace `RebacTableGate.cs:71-74` bare `catch` with `catch (Exception ex) when (ex is not OperationCanceledException)`, log with stable event id and metric `authz_dependency_failure_total`, keep the `false` (deny) result; same treatment for `ClusterTotpSecretStore`/`TotpVerificationService` catches (their failure semantic is decided in A-09).
  - Architecture test: no `GetService<IAuditLogRepository>` and no `if (auditRepo != null)` in `src/Autheris.Api`.
- **Affected files:** new `Application/Audit/*`, `Api/Endpoints/GovernanceEndpoints.cs`, `VirtualFilterEndpoints.cs`, `DuckDbOlapEndpoints.cs`, `SqlEndpointRoutes.cs`, `Api/Middleware/SecurityContextResolutionMiddleware.cs`, `Services/GatewayExecutionService.cs`, `Sql/Services/GovernedSqlExecutor.cs`, `Policy/RebacTableGate.cs`.
- **Compatibility:** governance-config endpoints that used to proceed silently without audit now return 503 when the audit store is down (intended, matches DuckDb endpoint behaviour). Release note required.
- **Security:** fail-closed on admin and ALLOW paths; DENY path always denies even if audit fails; cancellation no longer misreported as a policy decision.
- **TDD:** `tests/Autheris.Tests.Unit/Audit/AuditWriterTests`, `AuditPolicyTableTests`, `tests/Autheris.Tests.Architecture/AuditUsageRulesTests`. Red first: `RecordRequired_WhenRepositoryThrows_ThrowsAuditUnavailable`, then `GovernanceEndpoint_WhenAuditDown_Returns503_AndDoesNotMutate`, `RebacGate_OnCancellation_Rethrows`.
- **Acceptance:** zero hand-built `AuditLogEntry` in Api handlers (or all go through the writer helpers); consistent `OccurredAt`/`TraceId`.
- **Effort/Risk:** M (3 days) / medium (many endpoint edits; `GovernanceEndpoints.cs` is a god file that T8/R4 will also split).

### WP-ARC-A-07 - Audit hot path: durable enqueue and single chain writer (R1/R2)

- **Findings:** ARC-C-14 (pairs with ARC-C-03).
- **Goal:** Remove the audit store as a per-request throughput ceiling without weakening "no audit, no data".
- **Design:**
  - `IAuditSink` behind `IAuditWriter`: `AuditSinkOptions { Mode: Synchronous (default) | GroupCommit }`. In `GroupCommit` mode `RecordRequiredAsync` appends to a bounded in-process channel and awaits a group-commit acknowledgement (completion source resolved after the single writer has committed the batch in one transaction with chain hashing); `RecordBestEffortAsync` only enqueues. One chain-writer task per tenant chain keeps hash-chain order.
  - Do not return before the commit for fail-closed events; the gain comes from batching, not from deferring durability. A "WAL spool" for asynchronous durability is explicitly out of scope until Open Decision 4.
  - ALLOW audit for reads: record "intent" before execution and complete after materialisation only if the stakeholder accepts the two-phase record; default keeps one record after materialisation (unchanged).
- **Affected files:** `Application/Audit/*`, the three `*GovernanceRepository.Audit.cs` files (batch append API `RecordAuditEventsAsync(IReadOnlyList<AuditLogEntry>)`), `Infrastructure/Persistence/AuditChainAnchorStores.cs`.
- **Compatibility:** default mode unchanged. New repository method on `IAuditLogRepository` (default interface implementation loops the single-entry call to avoid breaking test doubles).
- **Security:** hash-chain order must be preserved; the chain integrity latch (SR-L-08) and anchor sequence (SR-L-07) are owned by Security Wave C and must land first or be merged into this WP (section 4).
- **TDD:** `tests/Autheris.Tests.Unit/Audit/AuditGroupCommitTests` (red: `ConcurrentWriters_ProduceContiguousChain`), `tests/Autheris.Tests.Integration/AuditGroupCommitThroughputTests`.
- **Acceptance:** chain verifies after concurrent load; p95 latency of audited reads improves versus synchronous mode in the benchmark project; a writer failure fails every awaiting caller.
- **Effort/Risk:** M-L (3-4 days) / high (security-critical, touches all three audit repositories).

### WP-ARC-A-08 - Remove `#if DEBUG` security seams (R0)

- **Findings:** ARC-S-06, ARC-C-17 (seams part).
- **Goal:** Debug and Release compile the same security code; kill switches do not exist in shipped assemblies.
- **Design:**
  - Introduce `ICompilerHooks` (`TrinoSqlEngine`, production type with a no-op default `CompilerHooks.None`) with one method per stage currently guarded by `SecurityControl` (`CoverageVerifier`, `CacheRebindPolicyValues`, `DmlGuardMutator`, `FaultyInjector`). `GovernedSqlCompiler` calls hooks through a non-null field; the mutation/test-seam implementation lives in the test assembly and is injected via the constructor or `CompileRequest`. The cost is one interface call per stage; no `#if`.
  - For the other 31 blocks, classify each: (a) test seam: move to hook or `InternalsVisibleTo` (make the unconditional, limited to test assemblies); (b) diagnostics only: use `[Conditional("DEBUG")]` attribute on a method or `Debug.Assert`, which keeps identical security semantics; (c) genuinely dev-only behaviour: replace by an `IHostEnvironment` check at composition. `Application/Security/TenantRegistryGate.cs:64-69` (`CompilerTestSeams.IsDisabled(TenantRegistryFailClosed)`) becomes an injected `ISecurityControlSwitch` registered only by test hosts; startup fails if one is registered outside Development.
  - Architecture test: no `#if DEBUG` and no type named `CompilerTestSeams` in `src/` (scan source text and assert the assembly under Release has no such type).
  - CI: run `TrinoSqlEngine.Tests` and `Autheris.Tests.SqlCompilerGate` in Release as well (CLAUDE.md memory: Release build with warnings as errors before every push).
- **Affected files:** the 20 files listed in the verification table, `TrinoSqlEngine.csproj:36-38` (InternalsVisibleTo), all test files using `CompilerTestSeams`.
- **Compatibility:** none at runtime; test code migrates to hooks.
- **Security:** removes Debug-only disable switches; failure to register hooks equals no-op (secure default).
- **TDD:** red first: `Architecture: Src_ContainsNoPreprocessorDebugBlocks` (currently 32 hits), `Release_Assembly_HasNoCompilerTestSeamsType`. Then convert hook by hook keeping the existing mutation tests green.
- **Acceptance:** architecture rules green; full SqlCompilerGate suite green in Debug and Release.
- **Effort/Risk:** M (3 days) / medium-high: `GovernedSqlCompiler.cs` and `CompiledSqlTemplate.cs` are hot files of the R1 X1 cutover; this WP must be serialized with R1 (section 5).

### WP-ARC-A-09 - Cluster state contract: absent vs unavailable, topology guard (R0)

- **Findings:** ARC-I-03, ARC-I-07 (metrics and doc), ARC-I-08, ARC-C-13, ARC-L-16, ARC-C-09 (TOTP store catches).
- **Goal:** Consumers can tell "key absent" from "store unavailable" and decide fail-closed explicitly; embedded Garnet cannot masquerade as a cluster.
- **Design:**
  - Add `ClusterStateUnavailableException` (Application/State). `IDistributedClusterStateProvider`: `GetAsync`, `SetAsync`, `RemoveAsync`, `TryAcquireLockAsync`, `PublishEventAsync` throw it on infrastructure failure; `TryAcquireLockAsync` returns `null` only for "busy". `IncrementAsync` keeps `long?` where `null` means unavailable (documented, used by existing local-fallback callers such as `SubgraphSecurityValidator`); `TryConsumeBudgetAsync` already has `StoreUnavailable`. Add `GetOrUnavailableAsync<T>` convenience returning a `StateRead<T> { Found, Value, Unavailable }` struct for callers that prefer a value over exceptions.
  - `RedisClusterStateProvider`: remove the blanket `catch (Exception) => default` at `:56-64,78,92,115,236`; wrap `RedisException`/`TimeoutException` into the new exception, rethrow cancellation, keep logging with a stable event id and add `autheris_cluster_state_failure_total{operation}`.
  - Consumer audit (explicit table in the WP PR, each consumer gets a documented decision): `ClusterTotpSecretStore` (unavailable means deny, never "no secret enrolled"), `TotpVerificationService`, `HitLStepUpApprovalService` (honours `FailClosedOnClusterPartition`), `McpSessionStore`, `AccessProfileCache`, `DifferentialPrivacyEngine`, `FocusCostAccountingService`, `RedisIdempotencyStore`, `RedisRateLimiterService`, `GovernanceEndpoints.cs:822,974`.
  - `InMemoryClusterStateProvider`: implement `IncrementAsync` with a `TryUpdate` CAS loop (no captured variable written in `AddOrUpdate` factories), take `_budgetLock` for all numeric operations, isolate subscribers in `PublishEventAsync`. Add an abstract `ClusterStateProviderContractTests` run against both providers (Redis variant in Integration with a real/embedded Garnet).
  - `IClusterStateTopology { bool IsClusterWide }` registered by an extracted `AddClusterState(mode)` (`Embedded` | `External` | `InMemory`) replacing the duplicated registrations in `Api/Extensions/DependencyInjection/GatewayStorageServiceExtensions.cs:45-95`. `GatewayStartupValidator` fails when `MultiNodeClusterMode || Replicas > 1` and the mode is `Embedded` or `InMemory`, including the combination `EnableEmbeddedServer=true` + `Redis.Enabled=true` that passes today.
  - Replay cache: introduce `IReplayCache` (in-memory + cluster-state implementation); `SubgraphSecurityValidator` takes it instead of the static dictionary; on cluster-state failure in cluster mode the request is rejected (fail-closed), local-only fallback only when `IClusterStateTopology.IsClusterWide == false`.
  - Make the `IEventBus` documentation English and add dropped-publish/handler-failure counters (Redis event bus and `RedisClusterStateProvider.SubscribeAsync` fire-and-forget handler).
- **Affected files:** `Application/State/*`, `Infrastructure/State/RedisClusterStateProvider.cs`, `Infrastructure/Messaging/RedisEventBus.cs`, `Infrastructure/Idempotency/RedisIdempotencyStore.cs`, `Infrastructure/RateLimiting/RedisRateLimiterService.cs`, the consumers above, `Api/Extensions/DependencyInjection/GatewayStorageServiceExtensions.cs`, `GatewayStartupValidator.cs`, `GraphQL/Federation/SubgraphSecurityValidator.cs`.
- **Compatibility:** Breaking for implementers of `IDistributedClusterStateProvider` (internal). Behaviour change: during Redis outages operations that previously degraded silently now fail (TOTP verification, session reads, idempotency) unless the consumer chose a documented fallback. Release note and a runbook entry are mandatory. New config: none required; optional `ClusterState:Mode`.
- **Security:** fail-closed by default; no consumer may treat unavailable as absent for authentication or replay protection.
- **TDD:** `tests/Autheris.Tests.Unit/State/ClusterStateProviderContractTests`, `RedisClusterStateProviderFailureTests` (fake `IConnectionMultiplexer` throwing). Red first: `Get_WhenRedisThrows_ThrowsClusterStateUnavailable_NotDefault`, `TryAcquireLock_WhenRedisThrows_Throws_NotNull`, `TotpSecretStore_WhenStateUnavailable_Denies`, `Validator_Rejects_Replicas2_WithEmbeddedGarnetAndRedisEnabled`, `InMemory_Increment_ConcurrentDeltas_SumExactly`.
- **Acceptance:** no `catch (Exception)` returning default in the Redis provider; every consumer in the audit table has a test for the unavailable path; validator covers the embedded plus external gap.
- **Effort/Risk:** L (4-5 days) / medium-high (touches authentication-adjacent consumers; wide blast radius).

### WP-ARC-A-10 - Access plans, confirmation tokens and secrets in shared state with TTL (R0/R2)

- **Findings:** ARC-C-05 (with SR-L-06 folded in).
- **Goal:** Admin plan/confirm/apply works across replicas, tokens are single-use cluster-wide, nothing grows unbounded, no plaintext secret in heap.
- **Design:**
  - New port `IAccessPlanStore` (`Application/Governance/`): `SavePlanAsync(plan, ttl)`, `GetPlanAsync`, `SetConfirmedTokenAsync`, `TryConsumeTokenAsync(tokenHash, ttl)`. Implementations: `InMemoryAccessPlanStore` (bounded, TTL eviction via `TimeProvider`, used when `IClusterStateTopology.IsClusterWide` is false) and `ClusterAccessPlanStore` over `IDistributedClusterStateProvider` (keys `accessplan:{tenant}:{planId}`, TTL = plan `ExpiresAt`; token consumption uses `IncrementAsync(key, 1, ttl) == 1`, the same atomic first-wins pattern already used for federation nonces). There is no SetNX in the interface; the increment pattern avoids widening it.
  - Confirmation token HMAC key: derive from `IKeyVaultSecretProvider` (`AccessPlan:SigningKeyRef`), required when clustered; the static per-process `RandomNumberGenerator.GetBytes(32)` stays only as a single-node/dev fallback. Store only a SHA-256 of the consumed token.
  - Bind execution to the confirming admin (SR-L-06): plan stores `AdminSid` (already checked at confirm), the token payload also carries the confirming SID and `ApplyAccessAsync` rejects a different applier unless a configured four-eyes mode allows it.
  - Remove `_inMemoryVault`. `RegisterDatasourceAsync` writes inline secrets with `IKeyVaultSecretProvider.SetSecret`; if the provider does not support it (default interface method throws `NotSupportedException`), reject the request with a clear error instead of silently losing the secret. The dead plaintext copy in process memory disappears.
  - `AccessPlanningService` constructor receives `IAccessPlanStore` (mandatory) and uses A-09 semantics: unavailable store means the operation fails.
- **Affected files:** `Governance/Services/AccessPlanningService.cs`, new store files, DI, `Governance/Services/IAccessPlanningService` consumers (MCP admin tools in `Mcp/`), `GovernancePlanEndpoints.cs`.
- **Compatibility:** plans in flight at deployment are lost (10 minute window); document. Config: `AccessPlan:SigningKeyRef` required in cluster mode (startup validator).
- **Security:** replay protection becomes cluster-wide; TTL bounds secret retention; unavailable shared store means no apply (fail-closed).
- **TDD:** `tests/Autheris.Tests.Unit/Governance/AccessPlanStoreTests` plus updated `Mcp/AdminMcpTwoPhaseTests`. Red first: `TwoNodes_PlanCreatedOnA_ConfirmedAndAppliedOnB` (two service instances over one `InMemoryClusterStateProvider`), `ConsumedTokenOnA_IsRejectedOnB`, `ExpiredPlans_AreEvicted`, `Apply_WhenStateUnavailable_Throws`, `RegisterDatasource_WhenVaultReadOnly_Rejects`.
- **Acceptance:** cross-node confirm/apply and replay rejection proven; no `ConcurrentDictionary` of plans/tokens/secrets left in the service; memory stays flat after 100k plan creations.
- **Effort/Risk:** M (2-3 days) / medium.

### WP-ARC-A-11 - Distributed lease, scheduled worker base, durable CDC watermark (R2)

- **Findings:** ARC-F-06, ARC-F-18, ARC-F-10.
- **Goal:** Singleton background jobs keep their lock for as long as they run, fail uniformly, and CDC resumes from a durable position.
- **Design:**
  - `IDistributedLease` (Application/State): `Token` (fencing value from a monotonic cluster counter), `LeaseLost` cancellation token, auto-renew heartbeat every lease/3, `DisposeAsync` releases. Redis implementation extends `RedisLockLease` with a compare-and-extend script; `InMemoryClusterStateProvider` gets an equivalent. `TryAcquireLeaseAsync` is added next to `TryAcquireLockAsync`; the old method stays for short critical sections.
  - `ClusterScheduledService<TJob>` base (Application/Hosting): one loop (`PeriodicTimer`, jitter, optional initial delay), try/catch around lease acquisition and job, `IOptionsMonitor` interval, metrics (`last_success`, `duration`, `lease_lost_total`). Migrate `DataCatalogSyncBackgroundService`, `OpenMetadataSyncBackgroundService` (fixes the unguarded acquire/initial sync that can stop the host), `MssqlChangeTrackingHostedService`. Link the job token to `LeaseLost`.
  - Lock is mandatory when `IClusterStateTopology.IsClusterWide` or `Replicas > 1`: constructors take a non-null state provider; the optional `_clusterState == null` silent no-lock path is removed (fail at startup).
  - `IMssqlWatermarkStore` durable implementation `GovernanceDbMssqlWatermarkStore` (table `autheris_cdc_watermarks`, three backends, partial files `*.CdcWatermarks.cs`) updated with `max(existing, new)` semantics; the in-memory store is registered only in Development. Startup validator fails when `MssqlChangeTracking.Enabled` and the store is in-memory outside Development. The poller validates `CHANGE_TRACKING_MIN_VALID_VERSION` and surfaces "watermark below minimum valid version" as an error instead of replaying history. Stop defaulting the primary key to `[Id]` silently (`MssqlChangeTrackingPoller.cs:121-124`): require configuration or fail.
- **Affected files:** `Application/State/*`, `Infrastructure/State/RedisClusterStateProvider.cs`, `Extensions/DataCatalog/*`, `Extensions/OpenMetadata/*`, `Extensions/Cdc/*`, `Application/Streaming/Interfaces/IMssqlWatermarkStore.cs`, persistence partials.
- **Compatibility:** New table. Hosted-service constructors change (internal). Behaviour: single-node dev keeps working with in-memory lease.
- **Security:** fencing token prevents a paused node from writing after losing the lease; prevents duplicated CDC emission, which would otherwise duplicate webhook deliveries.
- **TDD:** `tests/Autheris.Tests.Unit/State/DistributedLeaseTests` (`FakeTimeProvider`, red first: `Lease_IsRenewed_WhileJobRuns_LongerThanInitialTtl`), `tests/Autheris.Extensions.Tests/Cdc/WatermarkStoreContractTests` (`Watermark_SurvivesStoreRecreation`), `OpenMetadataSyncServiceLeaseTests` (`AcquireThrows_DoesNotStopHost`).
- **Acceptance:** a job running 3x the lease keeps exclusivity; Redis outage during acquire is logged and retried; CDC restarts from stored version.
- **Effort/Risk:** L (4 days) / medium. Depends on A-09 (exception semantics).

### WP-ARC-A-12 - Async query jobs: HA-safe contract and concurrency (R2)

- **Findings:** ARC-F-07.
- **Goal:** Remove silent worker exit and head-of-line blocking; make the HA story explicit.
- **Design:**
  - Phase 1 (no cluster dependency): expose `IAsyncJobQueue` (reader, writer) on the abstraction and drop the downcast in `AsyncQueryJobBackgroundWorker.cs:48-52`; add `AsyncJobOptions.MaxConcurrentJobs` (default 4) and a per-tenant semaphore (`MaxConcurrentJobsPerTenant`); startup sweeper for orphaned scratch files; worker fails startup (not a warning) if the queue port is missing.
  - Phase 2 (only if Open Decision 3 selects multi-replica job access): persist `AsyncJobDescriptor` in cluster state with TTL (`asyncjob:{tenant}:{id}`), keep results in the shared object store (existing storage connector) or require sticky routing; `GET /jobs/{id}` on a node without the job returns 404 only when the descriptor is absent and 503 when state is unavailable (A-09).
- **Affected files:** `Application/Jobs/Services/AsyncQueryJobManager.cs`, `AsyncQueryJobBackgroundWorker.cs`, `Api/Endpoints/AsyncJobEndpoints.cs`, `Domain/Options/GatewayOptions.cs:1585` (`AsyncJobOptions`).
- **Compatibility:** Phase 1 additive; default concurrency changes from 1 to 4 (document; configurable).
- **Security:** tenant fairness limits DoS; job owner check is unchanged and must be preserved on the cluster path.
- **TDD:** `tests/Autheris.Tests.Unit/Jobs/AsyncJobWorkerConcurrencyTests`. Red first: `SlowJobOfTenantA_DoesNotBlockJobOfTenantB`, `Worker_WithNonConcreteQueue_StillProcesses`, `Sweeper_RemovesOrphanedScratchFiles`.
- **Acceptance:** two tenants' jobs run in parallel; tenant cap honoured; phase 2 proven by a two-node test.
- **Effort/Risk:** M (phase 1: 2 days; phase 2: +2-3 days) / low-medium.

### WP-ARC-A-13 - Cluster-wide CDC fan-out and subscription limits (R2)

- **Findings:** ARC-G-03.
- **Goal:** A CDC event ingested on one replica reaches GraphQL subscribers and the webhook service on every replica; per-subject limits are cluster-wide or explicitly per node.
- **Design:**
  - `RedisCdcEventChannel : ICdcEventChannel` in `Autheris.Infrastructure/Streaming/`: `PublishAsync` publishes the serialized `CdcEvent` on a Redis channel (via `IEventBus`/`IDistributedClusterStateProvider.PublishEventAsync`) and each node feeds its local `InMemoryCdcEventChannel` from a subscriber, so existing per-subscriber channels, topic caps and history stay unchanged. Registered when `IClusterStateTopology.IsClusterWide`. Documented as at-most-once (pub/sub hint); consumers needing durability use the CDC watermark and the outbox (A-11, A-02).
  - Replace `.AddInMemorySubscriptions()` (`GatewayServiceCollectionExtensions.cs:630`) with the Redis-backed HotChocolate provider only if an `[EventMessage]` topic actually relies on it; otherwise leave it and document that the data path is the CDC channel.
  - `CdcSubscriptionGovernor`: back the open-subscription counter with `IDistributedClusterStateProvider.IncrementAsync` (+1 on admit, -1 on dispose) with a TTL refreshed by the lease heartbeat, so leaked counts from crashed nodes expire. When the state is unavailable in cluster mode, admission is denied (fail-closed). Alternative documented per-node semantics (limit multiplies by node count) if Open Decision 3 says single-node only.
  - SR-L-18 (revocation watcher fails open) is Security Wave C; this WP exposes the same fail-closed state primitive but does not change `CheckRevocationAsync`.
- **Affected files:** `Infrastructure/Streaming/*`, `GraphQL/Subscriptions/CdcSubscriptionGovernor.cs`, `Api/Extensions/GatewayServiceCollectionExtensions.cs`, `Api/Extensions/DependencyInjection/GatewayStorageServiceExtensions.cs`.
- **Compatibility:** none in single-node mode; in cluster mode events now cross nodes (expected behaviour).
- **Security:** each node still runs `IStreamRlsPolicyEnforcer` per subscriber, so cross-node delivery does not bypass masking; events carry `TenantId` and are tenant-checked on receipt.
- **TDD:** `tests/Autheris.Tests.Integration/CdcClusterFanOutTests` (red first: `EventPublishedOnNodeA_IsDeliveredToSubscriberOnNodeB`), `tests/Autheris.Tests.Unit/GraphQL/CdcSubscriptionGovernorClusterTests` (`Limit_IsEnforcedAcrossNodes`, `StateUnavailable_DeniesAdmission`).
- **Acceptance:** two-node integration test green; per-subject limit holds across nodes.
- **Effort/Risk:** M (3 days) / medium. Depends on A-09 (and A-11 for the leased poller).

### WP-ARC-A-14 - Casbin decision cache and policy epoch (R2)

- **Findings:** ARC-C-07.
- **Goal:** Bounded, allocation-light decision cache and a provable staleness bound across nodes.
- **Design:**
  - Replace the string key at `CasbinEnforcementService.cs:713` with `readonly record struct DecisionKey` (precomputed hash over tenant, user, table, action, purpose, attributes, columns; full fields kept for equality to avoid hash-collision decisions). Replace `ConcurrentDictionary` + `Clear()` at `:724-727` with a size-limited `MemoryCache`/LRU, eviction by entry rather than global flush.
  - Cluster epoch: on every snapshot publish (`:423`) write `policy:epoch:{tenant}` through `IDistributedClusterStateProvider` and publish a hint on a channel; on cache read compare the node epoch with a short-lived cached cluster epoch (refresh at most every `Casbin.EpochCheckSeconds`, default 5). When the cluster epoch is newer, reload before deciding; when the state is unavailable and the cached entry is older than the TTL the call falls back to a fresh evaluation (no stale allow beyond TTL). Document the maximum staleness (`min(DecisionCacheTtl, EpochCheckSeconds + reload time)`).
  - Reuse the existing `IEpochValidationService` mechanism where possible instead of a parallel mechanism.
- **Affected files:** `Governance/CasbinEnforcementService.cs` (this is also an R4/T7 split target; keep the change inside the cache/epoch region), `Infrastructure/Cache/EpochValidationService.cs` (only if reused).
- **Compatibility:** none externally; new option `Casbin.EpochCheckSeconds`.
- **Security:** shortens the stale-allow window after a policy change; stale state never extends beyond TTL.
- **TDD:** `tests/Autheris.Tests.Unit/CasbinDecisionCacheTests` (existing `CasbinHotReloadTests`, `CasbinSnapshotImmutabilityTests` stay green). Red first: `Cache_AtCapacity_EvictsOneEntry_NotAll`, `PolicyChangedOnNodeA_IsVisibleOnNodeB_WithinEpochCheckInterval`, `Allocation_PerDecision_Below N bytes` (benchmark guard).
- **Acceptance:** no global flush; cross-node propagation test within the documented bound.
- **Effort/Risk:** M (3 days) / medium (security decision hot path; serialize with R4 Casbin decomposition).

### WP-ARC-A-15 - Plugin and extension-point hygiene (R2/R4 bridge)

- **Findings:** ARC-F-15, ARC-F-11 (partial), ARC-F-20.
- **Goal:** Remove promises that do nothing and give extension points a failure policy.
- **Design:**
  - `IHttpDataSourcePlugin.ConfigureServices`: decide per Open Decision 5. Recommended: remove the member (no caller exists, so no plugin can depend on a working behaviour), bump contract note. Remove `PluginsOptions.EnableHotReload` (`GatewayOptions.cs:387`) and the duplicate `PluginDirectory` (`:1407`) after confirming no reader.
  - `ExtensibilityPipeline`: wrap each interceptor with try/catch, per-interceptor timeout (`CancelAfter`), and an explicit `InterceptorFailurePolicy { FailClosed, FailOpen }` (default `FailClosed` for ingress interceptors, `FailOpen` for egress logging-type interceptors, configurable). Add `PluginManager.ExecuteActionAsync` timeout and drain in-flight handlers on dispose.
  - Translate `IHttpDataSourcePlugin` and `IEventBus` documentation to English; add a CI grep/analyzer guard for non-ASCII German words in `Application/Plugins`.
- **Affected files:** `Application/Plugins/*`, `Application/Extensibility/ExtensibilityPipeline.cs`, `Infrastructure/Plugins/PluginManager.cs`, `Domain/Options/GatewayOptions.cs`, docs.
- **Compatibility:** removing members is source-breaking for external plugin authors (see Open Decision 5); bundled plugins in repo tests updated.
- **Security:** interceptor exception no longer escapes as an unhandled 500 or silently skips a control.
- **TDD:** `tests/Autheris.Tests.Unit/Extensibility/ExtensibilityPipelineFailurePolicyTests`. Red first: `Interceptor_Throws_FailClosed_DeniesRequest`, `Interceptor_Hangs_TimesOut`.
- **Acceptance:** no unused options; pipeline tests green; English-only docs.
- **Effort/Risk:** S-M (1-2 days) / low.

### WP-ARC-A-16 - Versioned plugin abstractions (R4, conditional)

- **Findings:** ARC-F-04, remainder of ARC-F-11.
- **Goal:** Plugins compile against a small, versioned contract instead of all of `Autheris.Application`.
- **Design:**
  - New project `Autheris.Plugins.Abstractions` (no dependency on Application/Domain; primitive DTOs: event envelope, execution context, data source request/response). Assembly attribute `[assembly: AutherisPluginContract("1.0")]`.
  - `PluginManager` reads the attribute, refuses incompatible major versions with an ERROR and a health-check entry (replacing the swallowed warning at `PluginManager.cs:265-268`).
  - `PluginAssemblyLoadContext.IsSharedAssembly` narrows to `Autheris.Plugins.Abstractions`, `Microsoft.Extensions.*.Abstractions` and the BCL.
  - Adapters in Infrastructure map internal types to the abstraction; keep the old interfaces for one release marked `[Obsolete]`, loadable behind `Plugins.AllowLegacyContract`.
  - Allow plugins to contribute `IIngressInterceptor`/`IEgressInterceptor` through the abstraction (ARC-F-11).
  - The review also recommends extracting `Autheris.Application.Abstractions` for `Autheris.Extensions` (ARC-L-18): that is R1/R3 scope and not planned here.
- **Affected files:** new project, `Infrastructure/Plugins/*`, `Autheris.sln`, `Application/Plugins/*`, `tests/Autheris.Tests.Architecture`.
- **Compatibility:** breaking for existing plugins; mitigated by the legacy flag for one release. Skip entirely if there are no external plugin authors (Open Decision 5).
- **Security:** hash pinning and integrity manifest requirements are unchanged; version check happens before any plugin code runs.
- **TDD:** `tests/Autheris.Tests.Unit/Plugins/PluginContractVersionTests`. Red first: `Plugin_WithIncompatibleMajor_IsRejected_AndHealthReportsError`.
- **Acceptance:** incompatible plugin rejected visibly; compatible sample plugin loads using only the abstractions package.
- **Effort/Risk:** L (4-5 days) / medium.

---

## 3. Security Wave C coordination

Security Wave C items are not planned here. Declared relationships:

| Security item | Relationship to this plan |
|---|---|
| SR-L-06 (access plan per-node, no confirmer binding) | **Merge into WP-ARC-A-10.** The confirmer-binding part is included in A-10's design; Security should not implement a separate Redis plan cache. |
| SR-L-07/08/09 (audit anchor, chain latch, sequence numbers) | **Must land before or be merged with WP-ARC-A-07**; A-07 changes how the chain is written. A-06 (`IAuditWriter`) is independent but is the integration point: the SR-L-08 fail-closed latch should surface as `AuditUnavailableException` through `RecordRequiredAsync`. |
| SR-L-10 (token revocation fails open) and SR-L-18 (WebSocket revocation watcher fails open) | **Build on WP-ARC-A-09** (`ClusterStateUnavailableException`, `IClusterStateTopology`); they decide the revocation-specific policy (`FailClosedOnTokenRevocationUnavailable`) on top of that contract. Do not implement them against the current swallow-everything provider. |
| SR-L-13 (CloudEvent signature without timestamp) and SR-L-14 (resolved IPs echoed) | **Merge into WP-ARC-A-02** (single signing format with timestamp, sanitised errors). If Security wants them earlier, implement them as the first commit of A-02 on the unified client, not as separate patches on the dispatchers that A-02 deletes. |

---

## 4. Dependencies

Between WPs:

- A-01 has none; it should be merged first.
- A-02 before A-03. A-03 also uses A-06 (audit of delivery summaries) and optionally A-13 (cross-node events).
- A-04 is independent; A-02 simplifies the bridge afterwards.
- A-05 before A-06 (writer is injected non-optionally).
- A-06 before A-07.
- A-09 before A-10, A-11, A-13 and the SR-L-10/18 work; A-10, A-11, A-13 consume `IClusterStateTopology` and the exception semantics.
- A-11 before A-13 (leased poller) and before A-03 cluster deployment (durable watermark prevents duplicates).
- A-08 independent but serializes with R1 on `GovernedSqlCompiler`.
- A-15 before A-16.
- A-12 phase 2 depends on A-09.
- A-14 depends on A-09 (state semantics) and is independent otherwise.

On other scopes (reference only):

- **Architecture R1+R3:** T1 pipeline work (`ARC-C-01`, `ARC-S-01/02`, `GovernedSqlExecutionService`, `GatewayExecutionService`, `GovernedSqlRewriter`) shares hot files with A-05, A-06, A-07, A-08; coordinate order (section 5). R1 ARC-C-12 (two masking engines) is unrelated. R3/T4 migration runner (`ARC-L-03/I-01/I-04/I-05`): A-02, A-03, A-11 add tables as idempotent script constants so the runner can adopt them; if the runner lands first, the DDL moves into versioned migrations without code changes. ARC-I-04/I-05 (SQLite shared connection) affect the SQLite outbox implementation of A-02 only in performance.
- **Architecture R4-R6:** T7 splits `CasbinEnforcementService` and `GatewayOptions` (A-14 and the new option classes should land before or be rebased onto that split). T8 ProblemDetails (ARC-A-03) will rewrite the endpoint bodies that A-01 and A-06 touch; A-01/A-06 use `Results.Problem` for new error responses to stay compatible. T9 (ARC-S-03, I-06) none. T10 hygiene: ARC-T-* test split will move tests that A-04/A-05/A-08 modify; ARC-C-19 (magic numbers) none.
- **Features:** any new CDC webhook feature plan (F-EVT-01) must treat A-01..A-03 as its implementation; do not plan separately.

---

## 5. Recommended sequence

| Batch | WPs | Rationale | Parallelism |
|---|---|---|---|
| 1 (R0, immediately) | A-01, A-04, A-05, A-15 | Stop misleading behaviour and unbounded publisher latency; remove silent fail-open composition. Small and mostly independent. | A-01, A-04, A-15 in parallel (no shared files). A-05 touches many constructors and tests: run alone or with A-01 only to limit merge conflicts. |
| 2 (R0) | A-09, A-08 | Cluster state semantics is the foundation for HA and the Security Wave C revocation items; DEBUG seams are small but hit R1 hot files. | A-09 and A-08 are disjoint (Infrastructure/State vs TrinoSqlEngine) and can run in parallel. A-08 must be sequenced with R1 on `GovernedSqlCompiler.cs`. |
| 3 (R0/R2) | A-06, A-10 | Audit contract and access plans; A-10 needs A-09. | In parallel (A-06 mostly Api/Audit; A-10 Governance). Both touch `AccessPlanningService` only lightly (A-06 audit calls): rebase order A-10 first. |
| 4 (R2) | A-02, A-11 | Durable delivery and singleton-worker correctness. | In parallel: A-02 (Webhooks + persistence partials) and A-11 (State + Extensions). Both add partials in `Infrastructure/Persistence`; separate files, schema registration lines conflict trivially. |
| 5 (R2) | A-03, A-13, A-12, A-14 | Feature completion on top of batches 3-4. | A-03 (needs A-02), A-13 (needs A-09, A-11), A-12 and A-14 are independent of each other. Four developers possible. |
| 6 (R1/R2 boundary) | A-07 | Needs Security Wave C audit chain fixes and R1 stabilisation. | Serialize after SR-L-07/08/09 and R1 changes to `GatewayExecutionService`/`GovernedSqlExecutor`. |
| 7 (R4, conditional) | A-16 | Only if external plugin authors exist. | Independent. |

Serialization points (shared hot files): `GovernedSqlExecutionService.cs`, `GatewayExecutionService.cs`, `GovernedSqlCompiler.cs` (R1, A-05, A-06, A-07, A-08); `GatewayOptions.cs` (almost every WP adds an option class: pre-agree on one new file per feature, or add classes in separate partial files to avoid conflicts); `GovernanceEndpoints.cs` (A-06, T8); `CasbinEnforcementService.cs` (A-14, T7); `GatewayStartupValidator.cs` (A-04, A-09, A-10, A-11).

Release gating: R0 is complete when A-01, A-04, A-05, A-08, A-09 (provider contract) and A-10 are merged. A-03 flips `CdcWebhooks.Enabled` to true only after A-02 and A-11 are in.

---

## 6. Open decisions

1. **Wire or remove CDC webhooks (ARC-F-01).** Recommendation: wire (A-01 now, A-02/A-03 next), because the CDC channel, transformer, masking enforcer and dispatcher already exist; removal would discard working components. If stakeholders do not need the feature within two releases, remove the endpoints and keep A-01 as the permanent state.
2. **Identity used to mask CDC webhook payloads.** Recommendation: snapshot of the creating admin's SID/groups/roles stored on the subscription, evaluated against current policy at event time, deny when missing. Alternative: a dedicated service principal per subscription (more explicit, needs an admin workflow).
3. **Is multi-replica a supported production topology today?** The validators already imply yes (NF-HA-02). Recommendation: yes; therefore T5 items A-09..A-13 stay in R0/R2 as planned. If no, mark A-12 phase 2 and A-13 as deferred and document single-node semantics instead.
4. **Audit durability model for A-07.** Recommendation: group commit only (durable before acknowledging). A local WAL spool for "never lose DENY" while the audit DB is down is a larger design (disk quota, replay ordering, chain continuity) and should be a separate decision with Security.
5. **Plugin compatibility promise (ARC-F-04, ARC-F-15).** Recommendation: if external plugin authors do not exist, remove `ConfigureServices` and `EnableHotReload` now and defer A-16; if they exist, do A-16 with a one-release legacy flag.
6. **`IncrementAsync` unavailable signal.** Recommendation: keep `long?` (null means unavailable) for compatibility in A-09 and migrate to an explicit result type together with the next breaking interface change; an alternative is to throw like the other methods and update the three local-fallback callers now.
7. **Default `EventBusOptions.DeliveryMode`.** Recommendation: `Queued` by default with `Inline` rejected outside Development, accepting that event handlers are lossy under overload because no Tier-A decision may use the bus.
