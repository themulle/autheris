# Implementation Plan: Architecture Waves R0 and R2 (Trust, HA & State, Eventing)

**Document ID:** `PLAN-IMPL-ARC-A`
**Parent plans:** [`../00-master-plan-overview.md`](../00-master-plan-overview.md), [`../architecture.md`](../architecture.md) (themes T2, T5, T6; waves R0 and R2)
**Status:** **DELIVERED ✅** (`R-1..R-4`, `WP-ARC-A-01..A-15` delivered; `WP-ARC-A-16` skipped per `G-13`)
**Scope:** ARC-* findings of theme T2 (audit), T5 (cluster/HA semantics) and T6 (eventing, webhooks, plugin contracts).
**WP prefix:** `WP-ARC-A-NN`
**Date:** 2026-10-15
**Baseline:** `feat/impl-wave-5-6`

Waves R0 and R2 are complete: `WP-ARC-A-01..A-15` and remainders `R-1..R-4` have been delivered across Waves 1–5 (including `IWebhookOutbox` / `WebhookDeliveryHostedService` in `A-02`, `CdcWebhookDeliveryService` in `A-03`, `AuditGroupCommitSink` / `RecordAuditEventsAsync` in `A-07`, `IClusterLeaderLease` / `GovernanceDbMssqlWatermarkStore` in `A-11`, HA `AsyncJobScheduler` in `A-12`, `RedisCdcEventChannel` & cluster subscription cap in `A-13`, and `CasbinDecisionCache` / `PolicyEpochGuard` in `A-14`). `WP-ARC-A-16` is skipped per Gate 0 decision `G-13`.

---

## 1. Open findings (verified on main a4137626)

| Finding | Status | Evidence |
|---|---|---|
| ARC-F-01 CDC webhooks never delivered | **Open** (guarded) | Endpoints return 501 unless `Gateway:CdcWebhooks:Enabled` (`Api/Endpoints/StreamingCdcEndpoints.cs:96-104,164-175`). When enabled, nothing calls `Transform`/`DispatchAsync`/`GetSubscriptionsAsync` outside `Application/Events/`; the only store is `InMemoryCloudEventSubscriptionStore` (`Api/Extensions/DependencyInjection/GatewayExecutionServiceExtensions.cs:143-145`). Fixed by A-02 + A-03. |
| ARC-F-03 volatile, unclassified retries | **Open** | `Application/Webhooks/WebhookDispatcherService.cs`: timestamp and signature computed once before the retry loop (`:91,113`, loop `:124`), every non-2xx retried, `response` never disposed (`:151`), `Task.Delay` outside try (`:180`), no jitter/Retry-After, no outbox, no dead letter. |
| ARC-F-16 two webhook dispatchers | **Partially resolved** | Both now sign with `WebhookSignature` (SEC2-01), but two transports remain; `CloudEventWebhookDispatcher.cs:72` still passes `isDev: false`, no retry. A-02. |
| ARC-F-17 plugin listener reflection | **Partially resolved** | Event bus match cache done (A-04). `Infrastructure/Plugins/PluginManager.cs:326` still calls `MethodInfo.Invoke` per event. Remainder R-1. |
| ARC-A-02 hand-written audit | **Partially resolved** | The 14 `GetService<IAuditLogRepository>()` sites use `IAuditWriter`; direct `RecordAuditEventAsync` calls remain in `Api/Endpoints/RebacEndpoints.cs:41,66`, `TokenRevocationEndpoints.cs:70`, `TenantRegistryAdminEndpoints.cs:76`, `Api/Middleware/AccessAuditMiddleware.cs:204`, `Sql/Services/GovernedSqlExecutor.cs:218,264,473`. Remainder R-2. |
| ARC-C-14 synchronous audit on hot path | **Open** | Every audited request awaits one repository write; no `IAuditSink`/group commit. A-07. |
| ARC-I-07 pub/sub at-most-once | **Partially resolved** | `IEventBus` doc is English; Redis subscriber handler failures are counted. Open: bare `catch` blocks in `Infrastructure/Cache/ConsentCacheService.cs:161,208,286,361`; no dropped-publish metric in `Infrastructure/Messaging/RedisEventBus.cs`. Remainder R-3. |
| ARC-G-03 subscriptions single-node | **Open** | `AddInMemorySubscriptions` at `Api/Extensions/GatewayServiceCollectionExtensions.cs:639`; data path `ICdcEventChannel.SubscribeAsync` (`GraphQL/Subscriptions/Subscription.cs:135`) bound to `InMemoryCdcEventChannel` (`GatewayExecutionServiceExtensions.cs:122`); per-subject limiter is a per-process `ConcurrentDictionary` (`GraphQL/Subscriptions/CdcSubscriptionGovernor.cs:32`). A-13. |
| ARC-F-06 lock leases not renewed | **Partially resolved** | `OpenMetadataSyncBackgroundService` now guards lock acquisition (`:58-62`) and the sync. Open: no renewal or fencing token (`TryAcquireLockAsync` returns `IAsyncDisposable?` only); `_clusterState == null` still means no lock (`OpenMetadataSyncBackgroundService.cs:56`, `DataCatalogSyncBackgroundService.cs:55`, `Cdc/MssqlChangeTrackingHostedService.cs:67`). A-11. |
| ARC-F-10 CDC watermark in memory | **Open** | `Extensions/Cdc/CdcSourceServiceCollectionExtensions.cs:25` registers only `InMemoryMssqlWatermarkStore`. A-11. |
| ARC-F-07 async jobs not HA-safe, serial | **Open** | `Application/Jobs/Services/AsyncQueryJobBackgroundWorker.cs:50-52` downcasts and exits; `AsyncQueryJobManager.cs:25-27` in-process channel and `ConcurrentDictionary` registry; no concurrency option. A-12. |
| ARC-C-07 Casbin decision cache and epoch | **Open** | `Governance/CasbinEnforcementService.cs:713` string key, `:724-726` `Count >= 10_000` then `Clear()`, TTL 60 s (`:132`), per-node epoch (`:423`). A-14. |
| ARC-F-04 unversioned plugin contract | **Open** (deferred, G-13) | `Infrastructure/Plugins/PluginAssemblyLoadContext.cs:74-75` shares `Autheris.Domain*`/`Autheris.Application*`. A-16. |

---

## 2. Work packages

Conventions: all new tests follow Red-Green-Refactor. Unit tests go to `tests/Autheris.Tests.Unit/` (until R6 splits it), architecture tests to `tests/Autheris.Tests.Architecture/`, extension tests to `tests/Autheris.Extensions.Tests/`, integration tests to `tests/Autheris.Tests.Integration/` (set `DOTNET_USE_POLLING_FILE_WATCHER=true` under WSL). Schema changes are added as versioned governance migrations (WP-ARC-B-12 runner, on `main`) for all three backends.

### Remainders of delivered R0 packages (S each)

- **R-1 (A-04 remainder, ARC-F-17):** `PluginManager` compiles one `Func<object, object, CancellationToken, Task>` per listener instead of `MethodInfo.Invoke` (`PluginManager.cs:326`). Test: `PluginListener_IsInvokedWithoutReflectionPerEvent`.
- **R-2 (A-06 remainder, ARC-A-02):** route the remaining direct `RecordAuditEventAsync` calls listed in section 1 through `IAuditWriter` with an explicit `AuditPolicyTable` entry, and extend `tests/Autheris.Tests.Architecture/AuditUsageRulesTests.cs` to forbid direct repository writes in `src/Autheris.Api`. `AccessAuditMiddleware` may stay best-effort but must say so through the writer.
- **R-3 (A-09 remainder, ARC-I-07):** replace the bare `catch` blocks in `ConsentCacheService.cs:161,208,286,361` with filtered catches plus a stable event id; add `autheris_event_bus_publish_failed_total` to `RedisEventBus`.
- **R-4 (A-05 remainder, owned by R1/WP-ARC-B):** group the `GovernedSqlExecutionService` constructor dependencies and replace the optional `IServiceProvider` (`Sql/Services/GovernedSqlExecutionService.cs:43,71`) with `Lazy<IFederatedQueryExecutionService>`.

### WP-ARC-A-02 - Webhook outbox and unified delivery core (R2)

- **Findings:** ARC-F-03, ARC-F-16. SR-L-13/14 are done (WP-SEC2-01): `WebhookSignature` (timestamped `X-Autheris-Signature-256`, optional legacy header) and `UrlRedactor` exist and are reused unchanged here.
- **Goal:** One durable at-least-once webhook delivery path: outbox table, retry classification, fresh timestamp/signature per attempt, dead letter, per-subscription circuit breaker, single signing format.
- **Design:**
  - Domain (`Autheris.Domain/Model`): `WebhookOutboxMessage` (Id, SubscriptionId, TenantId, EventId, EventType, PayloadJson (already masked), TargetUrl snapshot reference, Attempt, NextAttemptAt, State {Pending, InFlight, Delivered, Dead}, LeaseUntil, LastStatusCode, LastErrorCode (not free text), CreatedAt). Unique constraint on `(SubscriptionId, EventId)` for idempotent enqueue.
  - Port `IWebhookOutbox` (`Autheris.Application/Webhooks/`): `EnqueueAsync`, `ClaimBatchAsync(limit, leaseDuration, ct)` (claim with lease: `FOR UPDATE SKIP LOCKED` on PostgreSQL, `READPAST` on SQL Server, single-writer on SQLite), `CompleteAsync`, `RescheduleAsync`, `DeadLetterAsync`, `ListDeadLettersAsync`, `CountPendingAsync` (metric). A claim lease removes the need for a cluster lock (the ITSM outbox needs one because it has no claim: `Infrastructure/Itsm/ItsmOutboxDispatcherHostedService.cs:98-103`).
  - Implementations as new partials `PostgreSqlGovernanceRepository.WebhookOutbox.cs`, `SqlServerGovernanceRepository.WebhookOutbox.cs`, `SqliteGovernanceRepository.WebhookOutbox.cs` next to the existing `*.Outbox.cs` files, plus one versioned migration per backend.
  - `WebhookRetryPolicy.Classify(statusCode | exception)` returns `Retry` (5xx, 408, 429 honouring `Retry-After` capped at a configured maximum, network error, timeout), `Permanent` (other 4xx, malformed URL, egress/SSRF rejection) or `Success`. Backoff: exponential with full jitter, base 5 s, cap 1 h, default 8 attempts then `Dead`. `TimeProvider` injected.
  - `WebhookDeliveryClient` replaces both dispatchers' HTTP code: `using var response`, per-attempt `X-Autheris-Timestamp` and `X-Autheris-Signature-256: sha256=HMAC(secret, "{timestamp}.{payload}")`, header deny-list from `CloudEventWebhookDispatcher.IsForbiddenCustomHeader`, `EgressUrlPolicy.ValidateResolvedAsync` per attempt with `isDev` from environment (the hard-coded `false` goes away), error messages reduced to stable codes (no resolved IPs, no exception text).
  - `WebhookDeliveryHostedService`: N workers (`Webhooks.DeliveryWorkers`, default 4), `PeriodicTimer` poll, claim, deliver, update. Per-subscription circuit: after `Webhooks.AutoDisableAfterConsecutiveFailures` (default 20) the subscription is skipped until a cool-down passes; state kept in the outbox row set (no new store).
  - `WebhookEventBridgeHostedService` changes from "queue + dispatch" to "match subscriptions + `EnqueueAsync`" for configured `Webhooks.Subscriptions`. `IWebhookDispatcherService.DispatchEventAsync` is kept as a thin enqueue facade for existing callers/tests.
  - Honest scope on "transactional": phase 1 is durable-enqueue-at-bus-handler (at-least-once once enqueued). A same-transaction enqueue is added only where the publisher already owns a `DbTransaction` (the audit repository has `RecordAuditEventAsync(entry, DbTransaction)`); that is an optional phase 2 and must not block this WP.
  - Retire `CloudEventWebhookDispatcher` as a transport; `ICloudEventWebhookDispatcher` becomes a builder that produces an outbox message (consumed by A-03). `CloudEventWebhookSubscription.HmacSecret` signing uses the unified format.
- **Affected files:** `Application/Webhooks/*`, `Application/Events/Services/CloudEventWebhookDispatcher.cs`, `Domain/Model/*`, `Domain/Options/GatewayOptions.cs` (`WebhooksOptions`), three `*GovernanceRepository.*` families under `Infrastructure/Persistence/`, `Api/Extensions/DependencyInjection/GatewayExecutionServiceExtensions.cs`.
- **Compatibility:** Configured `Webhooks.Subscriptions` keep their current headers (`X-Autheris-Signature-256`, `X-Autheris-Timestamp`) so existing receivers continue to work. The CloudEvent path never delivered, so changing it to the same scheme breaks nobody. Behaviour change: 4xx responses are no longer retried; `MaxRetries` semantics become "max attempts" explicitly (rename with config alias for one release). New table in the governance DB (all three backends; SQLite allowed only single-node, already enforced by `GatewayStartupValidator.cs:504-521`).
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
  - Keep the A-01 flag (`Gateway:CdcWebhooks:Enabled`, `Domain/Options/CdcWebhooksOptions.cs`) default `false` until the end-to-end test is green, then flip the default in a separate commit.
- **Affected files:** new service, `Domain/Model/CloudEventModels.cs`, `StreamingCdcEndpoints.cs` (capture creator identity), `Application/Events/Services/CloudEventTransformer.cs`, `Infrastructure/Persistence/*` (subscriptions table), DI.
- **Compatibility:** New columns/table; `CloudEventWebhookSubscription` record gains optional members (positional record: add trailing members with defaults to avoid breaking callers). Subscriptions created before this change (in-memory only) do not exist across restarts, so no migration.
- **Security:** tenant isolation via enforcer step 1 (`TenantId` check); `cdc_all` consumption is internal only; unauthorised events dropped ("zero leakage" as in `Subscription.cs:160`); SSRF validated at create time and at every delivery (A-02). Audit each delivery batch summary as Tier-B (A-06).
- **TDD:** `tests/Autheris.Tests.Integration/CdcWebhookEndToEndTests`. Red first: `RegisteredSubscription_ReceivesMaskedInsertEvent_ViaOutbox`. Plus unit: `Service_DropsEvent_WhenSubscriberLacksColumnPermission`, `Service_DropsEvent_WhenCreatorSnapshotMissing`, `Service_NeverPersistsUnmaskedPayload`, `Post_Subscription_WhenSecretVaultUnsupported_ReturnsFailClosed`.
- **Acceptance:** end-to-end delivery with correct signature; masked columns absent in outbox payload; restart keeps subscriptions; duplicate ingestion yields one delivery.
- **Effort/Risk:** L (about 4 days) / medium-high (masking identity is a security-sensitive design; see Open Decision 1).

### WP-ARC-A-07 - Audit hot path: durable enqueue and single chain writer (R1/R2)

- **Findings:** ARC-C-14 (pairs with ARC-C-03).
- **Goal:** Remove the audit store as a per-request throughput ceiling without weakening "no audit, no data".
- **Design:**
  - `IAuditSink` behind `IAuditWriter`: `AuditSinkOptions { Mode: Synchronous (default) | GroupCommit }`. In `GroupCommit` mode `RecordRequiredAsync` appends to a bounded in-process channel and awaits a group-commit acknowledgement (completion source resolved after the single writer has committed the batch in one transaction with chain hashing); `RecordBestEffortAsync` only enqueues. One chain-writer task per tenant chain keeps hash-chain order.
  - Do not return before the commit for fail-closed events; the gain comes from batching, not from deferring durability. A "WAL spool" for asynchronous durability is out of scope (G-5: group commit only).
  - ALLOW audit for reads: record "intent" before execution and complete after materialisation only if the stakeholder accepts the two-phase record; default keeps one record after materialisation (unchanged).
- **Affected files:** `Application/Audit/*`, `Application/Audit/AuditWriter.cs`, the three `*GovernanceRepository.Audit.cs` files (batch append API `RecordAuditEventsAsync(IReadOnlyList<AuditLogEntry>)`), `Infrastructure/Persistence/AuditChainAnchorStores.cs`.
- **Compatibility:** default mode unchanged. New repository method on `IAuditLogRepository` (default interface implementation loops the single-entry call to avoid breaking test doubles).
- **Security:** hash-chain order must be preserved; the chain integrity latch (SR-L-08) and monotonic anchors (SR-L-07) are already on `main` (WP-SEC1-07) and must stay intact; a writer failure surfaces as `AuditUnavailableException` through `IAuditWriter.RecordRequiredAsync`.
- **TDD:** `tests/Autheris.Tests.Unit/Audit/AuditGroupCommitTests` (red: `ConcurrentWriters_ProduceContiguousChain`), `tests/Autheris.Tests.Integration/AuditGroupCommitThroughputTests`.
- **Acceptance:** chain verifies after concurrent load; p95 latency of audited reads improves versus synchronous mode in the benchmark project; a writer failure fails every awaiting caller.
- **Effort/Risk:** M-L (3-4 days) / high (security-critical, touches all three audit repositories).

### WP-ARC-A-11 - Distributed lease, scheduled worker base, durable CDC watermark (R2)

- **Findings:** ARC-F-06, ARC-F-18, ARC-F-10.
- **Goal:** Singleton background jobs keep their lock for as long as they run, fail uniformly, and CDC resumes from a durable position.
- **Design:**
  - `IDistributedLease` (Application/State): `Token` (fencing value from a monotonic cluster counter), `LeaseLost` cancellation token, auto-renew heartbeat every lease/3, `DisposeAsync` releases. Redis implementation extends `RedisLockLease` with a compare-and-extend script; `InMemoryClusterStateProvider` gets an equivalent. `TryAcquireLeaseAsync` is added next to `TryAcquireLockAsync`; the old method stays for short critical sections.
  - `ClusterScheduledService<TJob>` base (Application/Hosting): one loop (`PeriodicTimer`, jitter, optional initial delay), try/catch around lease acquisition and job, `IOptionsMonitor` interval, metrics (`last_success`, `duration`, `lease_lost_total`). Migrate `DataCatalogSyncBackgroundService`, `OpenMetadataSyncBackgroundService`, `MssqlChangeTrackingHostedService`. Link the job token to `LeaseLost`.
  - Lock is mandatory when `IClusterStateTopology.IsClusterWide` or `Replicas > 1`: constructors take a non-null state provider; the optional `_clusterState == null` silent no-lock path is removed (fail at startup).
  - `IMssqlWatermarkStore` durable implementation `GovernanceDbMssqlWatermarkStore` (table `autheris_cdc_watermarks`, three backends, partial files `*.CdcWatermarks.cs`) updated with `max(existing, new)` semantics; the in-memory store is registered only in Development. Startup validator fails when `MssqlChangeTracking.Enabled` and the store is in-memory outside Development. The poller validates `CHANGE_TRACKING_MIN_VALID_VERSION` and surfaces "watermark below minimum valid version" as an error instead of replaying history. Today the poller resets the watermark when it is below the minimum valid version (`MssqlChangeTrackingPoller.cs:133-137`). Stop defaulting the primary key to `[Id]` silently (`MssqlChangeTrackingPoller.cs:123,299`): require configuration or fail.
- **Affected files:** `Application/State/*`, `Infrastructure/State/RedisClusterStateProvider.cs`, `Extensions/DataCatalog/*`, `Extensions/OpenMetadata/*`, `Extensions/Cdc/*`, `Application/Streaming/Interfaces/IMssqlWatermarkStore.cs`, persistence partials.
- **Compatibility:** New table. Hosted-service constructors change (internal). Behaviour: single-node dev keeps working with in-memory lease.
- **Security:** fencing token prevents a paused node from writing after losing the lease; prevents duplicated CDC emission, which would otherwise duplicate webhook deliveries.
- **TDD:** `tests/Autheris.Tests.Unit/State/DistributedLeaseTests` (`FakeTimeProvider`, red first: `Lease_IsRenewed_WhileJobRuns_LongerThanInitialTtl`), `tests/Autheris.Extensions.Tests/Cdc/WatermarkStoreContractTests` (`Watermark_SurvivesStoreRecreation`), `OpenMetadataSyncServiceLeaseTests` (`AcquireThrows_DoesNotStopHost`).
- **Acceptance:** a job running 3x the lease keeps exclusivity; Redis outage during acquire is logged and retried; CDC restarts from stored version.
- **Effort/Risk:** L (4 days) / medium. A-09 (exception semantics) is on `main`.

### WP-ARC-A-12 - Async query jobs: HA-safe contract and concurrency (R2)

- **Findings:** ARC-F-07.
- **Goal:** Remove silent worker exit and head-of-line blocking; make the HA story explicit.
- **Design:**
  - Phase 1 (no cluster dependency): expose `IAsyncJobQueue` (reader, writer) on the abstraction and drop the downcast in `AsyncQueryJobBackgroundWorker.cs:50-52`; add `AsyncJobOptions.MaxConcurrentJobs` (default 4) and a per-tenant semaphore (`MaxConcurrentJobsPerTenant`); startup sweeper for orphaned scratch files; worker fails startup (not a warning) if the queue port is missing.
  - Phase 2 (multi-replica job access; G-1 says multi-replica is supported): persist `AsyncJobDescriptor` in cluster state with TTL (`asyncjob:{tenant}:{id}`), keep results in the shared object store (existing storage connector) or require sticky routing; `GET /jobs/{id}` on a node without the job returns 404 only when the descriptor is absent and 503 when state is unavailable (A-09).
- **Affected files:** `Application/Jobs/Services/AsyncQueryJobManager.cs`, `AsyncQueryJobBackgroundWorker.cs`, `Api/Endpoints/AsyncJobEndpoints.cs`, `Domain/Options/GatewayOptions.cs:1662` (`AsyncJobOptions`).
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
  - Replace `.AddInMemorySubscriptions()` (`GatewayServiceCollectionExtensions.cs:639`) with the Redis-backed HotChocolate provider only if an `[EventMessage]` topic actually relies on it; otherwise leave it and document that the data path is the CDC channel.
  - `CdcSubscriptionGovernor`: back the open-subscription counter with `IDistributedClusterStateProvider.IncrementAsync` (+1 on admit, -1 on dispose) with a TTL refreshed by the lease heartbeat, so leaked counts from crashed nodes expire. When the state is unavailable in cluster mode, admission is denied (fail-closed).
- **Affected files:** `Infrastructure/Streaming/*`, `GraphQL/Subscriptions/CdcSubscriptionGovernor.cs`, `Api/Extensions/GatewayServiceCollectionExtensions.cs`, `Api/Extensions/DependencyInjection/GatewayStorageServiceExtensions.cs`.
- **Compatibility:** none in single-node mode; in cluster mode events now cross nodes (expected behaviour).
- **Security:** each node still runs `IStreamRlsPolicyEnforcer` per subscriber, so cross-node delivery does not bypass masking; events carry `TenantId` and are tenant-checked on receipt.
- **TDD:** `tests/Autheris.Tests.Integration/CdcClusterFanOutTests` (red first: `EventPublishedOnNodeA_IsDeliveredToSubscriberOnNodeB`), `tests/Autheris.Tests.Unit/GraphQL/CdcSubscriptionGovernorClusterTests` (`Limit_IsEnforcedAcrossNodes`, `StateUnavailable_DeniesAdmission`).
- **Acceptance:** two-node integration test green; per-subject limit holds across nodes.
- **Effort/Risk:** M (3 days) / medium. Depends on A-11 (leased poller).

### WP-ARC-A-14 - Casbin decision cache and policy epoch (R2)

- **Findings:** ARC-C-07.
- **Goal:** Bounded, allocation-light decision cache and a provable staleness bound across nodes.
- **Design:**
  - Replace the string key at `CasbinEnforcementService.cs:713` with `readonly record struct DecisionKey` (precomputed hash over tenant, user, table, action, purpose, attributes, columns; full fields kept for equality to avoid hash-collision decisions). Replace `ConcurrentDictionary` + `Clear()` at `:724-726` with a size-limited `MemoryCache`/LRU, eviction by entry rather than global flush.
  - Cluster epoch: on every snapshot publish (`:423`) write `policy:epoch:{tenant}` through `IDistributedClusterStateProvider` and publish a hint on a channel; on cache read compare the node epoch with a short-lived cached cluster epoch (refresh at most every `Casbin.EpochCheckSeconds`, default 5). When the cluster epoch is newer, reload before deciding; when the state is unavailable and the cached entry is older than the TTL the call falls back to a fresh evaluation (no stale allow beyond TTL). Document the maximum staleness (`min(DecisionCacheTtl, EpochCheckSeconds + reload time)`).
  - Reuse the existing `IEpochValidationService` mechanism where possible instead of a parallel mechanism.
- **Affected files:** `Governance/CasbinEnforcementService.cs` (this is also an R4/T7 split target; keep the change inside the cache/epoch region), `Infrastructure/Cache/EpochValidationService.cs` (only if reused).
- **Compatibility:** none externally; new option `Casbin.EpochCheckSeconds`.
- **Security:** shortens the stale-allow window after a policy change; stale state never extends beyond TTL.
- **TDD:** `tests/Autheris.Tests.Unit/CasbinDecisionCacheTests` (existing `CasbinHotReloadTests`, `CasbinSnapshotImmutabilityTests` stay green). Red first: `Cache_AtCapacity_EvictsOneEntry_NotAll`, `PolicyChangedOnNodeA_IsVisibleOnNodeB_WithinEpochCheckInterval`, `Allocation_PerDecision_Below N bytes` (benchmark guard).
- **Acceptance:** no global flush; cross-node propagation test within the documented bound.
- **Effort/Risk:** M (3 days) / medium (security decision hot path; serialize with R4 Casbin decomposition).

### WP-ARC-A-16 - Versioned plugin abstractions (R4, conditional)

- **Findings:** ARC-F-04, remainder of ARC-F-11.
- **Goal:** Plugins compile against a small, versioned contract instead of all of `Autheris.Application`.
- **Design:**
  - New project `Autheris.Plugins.Abstractions` (no dependency on Application/Domain; primitive DTOs: event envelope, execution context, data source request/response). Assembly attribute `[assembly: AutherisPluginContract("1.0")]`.
  - `PluginManager` reads the attribute, refuses incompatible major versions with an ERROR and a health-check entry (replacing the swallowed warning at `PluginManager.cs:295-298`).
  - `PluginAssemblyLoadContext.IsSharedAssembly` narrows to `Autheris.Plugins.Abstractions`, `Microsoft.Extensions.*.Abstractions` and the BCL.
  - Adapters in Infrastructure map internal types to the abstraction; keep the old interfaces for one release marked `[Obsolete]`, loadable behind `Plugins.AllowLegacyContract`.
  - Allow plugins to contribute `IIngressInterceptor`/`IEgressInterceptor` through the abstraction (ARC-F-11).
  - The review also recommends extracting `Autheris.Application.Abstractions` for `Autheris.Extensions` (ARC-L-18): that is R1/R3 scope and not planned here.
- **Affected files:** new project, `Infrastructure/Plugins/*`, `Autheris.sln`, `Application/Plugins/*`, `tests/Autheris.Tests.Architecture`.
- **Compatibility:** breaking for existing plugins; mitigated by the legacy flag for one release. Skipped under G-13 (no external plugin authors).
- **Security:** hash pinning and integrity manifest requirements are unchanged; version check happens before any plugin code runs.
- **TDD:** `tests/Autheris.Tests.Unit/Plugins/PluginContractVersionTests`. Red first: `Plugin_WithIncompatibleMajor_IsRejected_AndHealthReportsError`.
- **Acceptance:** incompatible plugin rejected visibly; compatible sample plugin loads using only the abstractions package.
- **Effort/Risk:** L (4-5 days) / medium.

---

## 3. Dependencies

- A-02 before A-03. A-03 audits delivery summaries through `IAuditWriter` and optionally uses A-13 (cross-node events).
- A-11 before A-13 (leased poller) and before A-03 cluster deployment (durable watermark prevents duplicates).
- A-12 phase 2 and A-14 build on the A-09 contract (on `main`).
- A-07 after R-2, so every audit write goes through the writer.
- A-14 coordinates with WP-ARC-C-09 (Casbin split, R4/T7); keep the change inside the cache/epoch region of `CasbinEnforcementService.cs`.
- A-02, A-03 and A-11 add governance tables as versioned migrations (the B-12 runner is on `main`).
- New options go into standalone option classes (for example `CdcWebhooksOptions`, `EventBusOptions`) until WP-ARC-C-01 splits `GatewayOptions`.
- Any CDC webhook feature plan (F-EVT-01) treats A-02/A-03 as its implementation.

---

## 4. Recommended sequence

| Batch | WPs | Parallelism |
|---|---|---|
| 1 (any time) | R-1, R-2, R-3 | Independent, small. R-4 goes with the R1 owner. |
| 2 (R2, Wave 4) | A-02, A-11 | In parallel: webhooks + persistence vs state + extensions. |
| 3 (R2, Wave 4) | A-03, A-13, A-12, A-14 | A-03 needs A-02; A-13 needs A-11; A-12 and A-14 are independent. |
| 4 (Wave 5) | A-07 | Serialize with WP-ARC-B-14 (audit phase) on the `*GovernanceRepository.Audit.cs` files. |
| skipped | A-16 | Only if external plugin authors appear (G-13). |

Release gating: A-03 flips `Gateway:CdcWebhooks:Enabled` to `true` only after A-02 and A-11 are merged and the end-to-end test is green.

---

## 5. Open decisions

Gate 0 settled the cross-scope questions (G-1 multi-replica supported, G-2 wire CDC webhooks, G-5 group commit only, G-13 no plugin compatibility promise). Remaining scope decision:

1. **Identity used to mask CDC webhook payloads (A-03).** Recommendation: snapshot of the creating admin's SID/groups/roles stored on the subscription, evaluated against current policy at event time, deny when missing. Alternative: a dedicated service principal per subscription.
