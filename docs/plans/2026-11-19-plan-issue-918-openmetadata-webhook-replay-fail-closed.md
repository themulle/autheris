# Plan: OpenMetadata webhook replay check fails closed without the shared store (Issue #918)

Phase 2 (solution architect), 2026-11-19. Code facts verified on `origin/main` `37578c014`.

## 1. Problem and evidence

- Issue: [#918](https://github.com/themulle/autheris/issues/918). Severity Low (needs a captured, validly signed event inside the replay window). The issue names WP-SIMPL-28 PR 4a and `SEC-28-44`; neither exists in `docs/plans/` yet, so this plan is the delivery vehicle.
- `src/Autheris.Extensions/OpenMetadata/OpenMetadataSyncService.cs`:
  - `:43` a static per-process `ProcessedWebhookEvents` dictionary is the first replay cache; `:677` checks it before the shared store.
  - `:683-698`: the shared store (`om:dedup:{id}`, `IncrementAsync`) is consulted only when `_clusterState != null`. A `null` counter (the documented "store unavailable" signal, `IDistributedClusterStateProvider.cs:32-34`, returned by `RedisClusterStateProvider.cs:197-202`) passes as "not seen". An exception is logged at `:696` as "falling back to in-memory deduplication".
  - Net effect on a multi-node deployment with Redis down: each node accepts the same event once.
  - `:666` the skew check uses `DateTimeOffset.UtcNow`, not the injected `_timeProvider` (untestable time).
- `src/Autheris.Api/Endpoints/WebhookEndpoints.cs:49-53`: `bool` result, every failure becomes 400. The sender cannot tell "retry later" from "rejected".
- Reference behaviour:
  - `src/Autheris.Application/Products/Sync/ProductSyncWebhook.cs:75-93`: no store, a `null` counter or `ClusterStateUnavailableException` returns `ProductSyncWebhookStatus.Unavailable`. `ProductEndpoints.Sync.cs:110` maps that to 503. There is no local fallback; single-node hosts get the per-process `InMemoryClusterStateProvider` (`GatewayStorageServiceExtensions.cs:61/70`, `ClusterStateServiceCollectionExtensions.cs:14-26`).
  - `src/Autheris.Extensions/Dbt/DbtWebhookReceiver.cs:161-203`: fails closed **only** when `warn_ignore_webhook_timestamp_tolerance` is set. In the normal mode it falls back to in-memory (`:187-191`, `:196-203`) exactly like OpenMetadata. The statement in the issue that dbt rejects in this case is only true for the insecure flag.

## 2. Options

- **A. Topology-gated:** keep the local dictionary and reject only when `IClusterStateTopology.IsClusterWide` and the store fails. This keeps two caches and adds a dependency.
- **B. ProductSync parity (recommended):** the shared replay store is the only replay cache. A missing provider, a `null` counter or `ClusterStateUnavailableException` returns `Unavailable` and the endpoint answers 503. The InMemory mode is unchanged because its provider is per-process by design. The static dictionary and its bounding code are removed.
- **C. Fix OpenMetadata only, leave dbt as it is:** this is inconsistent; rejected.

Recommendation: B for OpenMetadata, plus the same rule for dbt (WP-918-3, Q-918-1).

## 3. Work packages (one PR, order 1 then 2 then 3; no DDL, no step number used)

**WP-918-1 (S): OpenMetadata replay via the shared store only**
- `src/Autheris.Application/Catalog/OpenMetadata/IOpenMetadataSyncService.cs`: new `public enum OpenMetadataWebhookOutcome { Processed, Rejected, Unavailable }`. `HandleWebhookEventAsync` returns `Task<OpenMetadataWebhookOutcome>`.
- `OpenMetadataSyncService.cs`:
  - Remove `ProcessedWebhookEvents`, `ProcessedWebhookEventsCount`, `ClearProcessedWebhookEvents` and the bounding block (`:701-717`).
  - Replay step: `_clusterState is null`, a `null` counter or `ClusterStateUnavailableException` → log a warning and return `Unavailable` (no fallback). `count > 1` → `Rejected`.
  - TTL stays 15 min (at least 2 × the 5 min skew window + 1 min).
  - Skew uses `_timeProvider.GetUtcNow()`.
  - `ReleaseWebhookEventAsync` (`:737`) only removes the shared key.
- Tests:
  - `tests/Autheris.Extensions.Tests/OpenMetadataWebhookTests.cs` [Security]:
    - `Store_unavailable_rejects_without_local_fallback(bool returnNull)` (copy the `UnavailableCounterState` fake from `ProductSyncWebhookTests.cs:376`);
    - `Missing_store_rejects_as_unavailable`;
    - `Replay_across_two_service_instances_sharing_one_store_is_rejected` (proves no per-process state).
  - Replace `Buffer_is_bounded` with that two-instance test.
  - Adapt every construction site to pass `new InMemoryClusterStateProvider(time)` and the new outcome:
    - `OpenMetadataSyncServiceTests` (8 calls);
    - `OpenMetadataWebhookRetryExt12Tests`;
    - `OpenMetadataWebhookCollisionExt16Tests`;
    - `OpenMetadataReconcileAndAlationHardeningTests`;
    - `LakehouseAndCatalogExtensionsSecurityTests`.
- Local run: `dotnet test tests/Autheris.Extensions.Tests/Autheris.Extensions.Tests.csproj --filter "FullyQualifiedName~OpenMetadata"` (same flags as the unit filter).

**WP-918-2 (XS): endpoint mapping** (depends on WP-918-1)
- `WebhookEndpoints.HandleOpenMetadataWebhookAsync`: `Unavailable` → `ApiProblems.ServiceUnavailable(detail: "The webhook replay store is unavailable.")` as in `ProductEndpoints.Sync.cs:110`; `Rejected` → the existing 400; `Processed` → 200.
- `tests/Autheris.Tests.Unit/Api/WebhookPreAuthBufferingTests.cs:49`: update the fake's signature. Add `OpenMetadata_store_unavailable_returns_503` [Security].

**WP-918-3 (XS, Q-918-1): dbt receives the same rule**
- `DbtWebhookReceiver.cs:161-203`: drop the `ignoreTimestampTolerance` condition. A `null` counter or an exception always rejects with `Webhook deduplication unavailable.`. The local `TryRecordOrDetectDuplicate` (`:221`) runs only when `_clusterState is null`, or remove it in line with B (Q-918-2).
- `src/Autheris.Domain/Model/DbtWebhookModels.cs:24`: optional `bool StoreUnavailable = false`. `DbtEndpoints.cs:549-553` maps it to 503 instead of 401.
- Tests (`tests/Autheris.Tests.Unit/Dbt/DbtWebhookReceiverTests.cs`, [Security]):
  - generalise `Cluster_mode_with_ignored_timestamp_tolerance_rejects_instead_of_in_memory_fallback` (`:330`) to `Store_unavailable_rejects_in_every_mode(bool returnNull, bool ignoreTolerance)`;
  - add `Dbt_store_unavailable_returns_503` next to the endpoint tests.
- Add a `CHANGELOG.md` line for WP-918-1..3 (behaviour change: 503 instead of local acceptance while Redis is down).

## 4. Integration test impact

- `tests/Autheris.Tests.Integration/OpenMetadataIntegrationTests.cs:53-142`: the hosts use the InMemory provider, so 400 and 200 expectations hold. Hydrogen checks that the test host registers `AddClusterState` and adds no new case.
- `IntegrationGapAEndpointMetadataTests.cs:70`: route metadata is unchanged.
- No parity impact. Compile-check `tests/Autheris.Tests.Integration` locally; GitHub Actions runs the suites.
- WP-SIMPL-28 PR 4 (OpenMetadata extraction) moves this file. Whichever lands second rebases; land #918 first.

## 5. Phase 3

**Required (short).** Replay protection on a signature-authenticated public webhook changes from fail-open to fail-closed, and the status codes change. The Phase 3 expert sets the conditions; none are invented here.

## 6. Owner questions (defaults taken unless the owner objects)

| ID | Question | Default |
|---|---|---|
| Q-918-1 | Apply the same fail-closed rule to the dbt receiver in this PR? | **Yes** (consistency; the issue's premise about dbt is only true under the insecure flag) |
| Q-918-2 | Remove the local in-memory replay caches entirely (OpenMetadata and dbt), relying on the InMemory provider for single-node hosts? | **Yes** (ProductSync parity) |
| Q-918-3 | Answer 503 (sender retries) instead of 400/401 when the replay store is down? | **Yes** |
