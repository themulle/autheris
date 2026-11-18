# PLAN-OI-802: Prompt GraphQL schema rebuild after catalog writes, schema status endpoint (issue #802)

Issue: [#802](https://github.com/themulle/autheris/issues/802). Builds on [#786](2026-11-17-plan-issue-786-catalog-idle-cpu.md) (coalescing, quiet period, version short-circuit). Related, not implemented here: [#672](https://github.com/themulle/autheris/issues/672) (one version bump per multi-table write), [#675](https://github.com/themulle/autheris/issues/675) (debounce change events). No new options. One new authenticated route, which raises the endpoint-count ratchet from 319 to 320 (`freeze-exception` label needed, see section 7).

**Status: IMPLEMENTED (WP-802-1..3)** in one PR, revised after the Phase 5 review (uncached status read, authenticated route, follow-up check skipped when the periodic tick comes first, cancelled builds).

## 1. Product decision

After a dbt sync, a governance import or a consent apply, a client must be able to query the new datasets within seconds, and a seeding job must be able to wait for that moment without guessing.

- Committed catalog writes that change the GraphQL-visible schema trigger a prompt, coalesced rebuild on every instance that receives the change event. The periodic check stays as the backstop.
- `/health/ready` keeps its meaning (the instance can serve). It does not turn 503 during a routine rebuild: at 3,000 tables one rebuild takes about 4 minutes (#786), and all instances rebuild at the same time, so a 503 would take the whole cluster out of the load balancer for every catalog write.
- A separate route `GET /api/v1/catalog/graphql-schema-status` reports whether the served GraphQL schema is behind the catalog: `200 {"status":"Current"}` or `503 {"status":"Updating"}` with `Retry-After: 1`, `Cache-Control: no-store`. It requires an authenticated caller (any principal): the answer reveals when the catalog was written (including consent pattern grants), and the clients that need it hold write credentials anyway. It is not a load-balancer probe. It lives in the catalog API group (REST ingress transport, summarized catalog-read audit) because `/health/*` is anonymous by convention and exempt from rate limiting and the host allow-list.
- Consent apply does not change the GraphQL schema. The schema is global (one executor per process, built from catalog metadata, transport visibility and the static partitioning options). Consents are evaluated per request; the shield middleware (`GraphQlEnumerationShieldMiddleware`) makes unknown and denied fields look the same. `DeclarativeConsentApplyService` only calls `ReplaceConsentsAsync`; table-level consents bump a table epoch only, pattern grants bump the content version and publish `catalog:changed`. Neither changes the schema fingerprint (`CatalogSchemaModel.Fingerprint` hashes structure; the epoch is only the fingerprint cache key), so a consent apply costs at most one candidate check and never a rebuild.

## 2. Root cause

- `CatalogGraphQlTypeModule` learns about catalog changes only from its periodic timer (`CatalogGraphQlTypeModule.Refresh.cs:229-245`, `GraphQlOptions.CatalogSchemaRefreshSeconds = 60`). It does not subscribe to `catalog:changed`, which every committed catalog write publishes (`GovernanceRepository.CatalogRows.cs:57-73`, once per write scope or chunk via `CatalogWriteScope`, immediately for restrictive writes). The snapshot provider, the search warm-up and the version provider do subscribe. The `reloadSchema` mutation (`Mutation.Catalog.cs:45`) even publishes on that channel for the GraphQL module, which never listened.
- Nothing reports that the served schema is behind the catalog, so `/health/ready` says 200 while new fields fail validation.

Event delivery is not synchronous with the write: `catalog:changed` uses the queued path of `InProcessChannelEventBus` (only `invalidat`/`epoch` channels are dispatched inline), the queue can drop events when full, and with Redis it takes a network round trip. The HTTP response of a write can therefore reach the client before any instance has seen the event. With Redis (`Caching:Redis:Enabled`) every instance receives the event; with the in-process bus only the writing instance does, and the other instances rebuild at their periodic check (PostgreSQL has no LISTEN/NOTIFY path here). In every case the status route reads the catalog version from the store (section 3, stage 6), so it never reports `Current` before the instance has caught up.

## 3. Design (smallest change on top of #786)

### WP-802-1 Event-driven check with debounce (module, partial `CatalogGraphQlTypeModule.ChangeSignal.cs`)
- The module takes an optional `IEventBus` and subscribes to `ICatalogVersionProvider.CatalogChangedEventChannel`. The handler only increments a signal sequence and arms a one-shot `TimeProvider` timer; no work runs on the bus thread, the payload is ignored.
- Scheduling:
  - First signal after a quiet period (leading edge): the check runs after `ChangeSignalDebounce = 1 s`, so the writes of one request land in one check. A later signal does not push it out.
  - Signals within the quiet period of the previous signal (a burst): the check moves to `lastSignal + QuietPeriod`, capped at `burstStart + MaxRebuildDeferral` (5 min, #786). A continuous write stream causes one event-driven check per 5 minutes at most; the periodic check keeps its #786 behaviour.
  - `QuietPeriod = clamp(last executor build duration, 2 s, 60 s)`. Small catalogs settle in 2 s; at 3,000 tables (build about 4 min) it is 60 s, the same as the periodic tick, so no more rebuilds than with #786.
  - A check that saw a further change and deferred the rebuild (#786 quiet-period rule) schedules one follow-up check at `QuietPeriod`, unless the periodic tick is due earlier (the tick ends the burst too; a second full check would only add load).
- The timer runs `CheckForCatalogChangesAsync(fromTimer: false, forceFull: true)`. A forced check reads the catalog version from the store (`ITableMetadataRepository.GetCatalogVersionAsync`), not through the provider's cache, which is invalidated by the same asynchronous event and may still hold the old version. The candidate fingerprint decides, so a write without schema effect updates the built version (never lowered) and raises no rebuild.
- Before the startup build has started, a signal is acknowledged without a check (the startup build reads the current catalog).
- Rebuild requests still go through the #786 latch (at most one running, one waiting, newest model wins). The new path only makes the leading-edge and trailing checks earlier; it never raises a rebuild by itself.
- `CreateTypesAsync` (fresh-build branch) reads the catalog version before it builds the model, so the recorded version never claims a write the model lacks. A cancelled build clears the in-flight mark.

### WP-802-2 Schema freshness (module plus route)
- `ICatalogSchemaFreshness.IsSchemaCurrentAsync(ct)` (`src/Autheris.GraphQL/Catalog/`), implemented by the module. `false` when, read in pipeline order:
  1. a change signal is not yet checked (`signalSeq != checkedSignalSeq`);
  2. a changed model waits (`_pendingBuild != null`, deferral);
  3. a rebuild is requested (`_rebuildRequested`);
  4. no build has started yet, or a build is in flight (set at `CreateTypesAsync` start, cleared by `ReportExecutorCreated`, which Hot Chocolate raises when the new executor is created, or by a cancelled build);
  5. the last build failed closed (`_built.Fingerprint is null`);
  6. the catalog version in the store is newer than the built version. The read is uncached (one single-row `SELECT` on `CATALOG_STATE`), and a request only waits for a read that starts after it arrived: it joins a read that is queued but not started, never one in flight (that read may predate the caller's write). Reads start at least `StatusReadSpacing = 250 ms` apart per instance, which caps the store load whatever the request rate. A failed or timed-out read reports `Updating` (fail-closed).
- Stage 6 makes the answer correct without the event: a write whose event is queued, dropped or delivered to another instance is visible in the store the moment it commits.
- Route `GET /api/v1/catalog/graphql-schema-status` (`CatalogApiEndpoints.GraphQlSchemaStatus.cs`): `RequireAuthorization()`, catalog-read audit (summarized), `Cache-Control: no-store`, body `status` only, `Retry-After: 1` on 503, 404 when the GraphQL catalog module is not registered.

### WP-802-3 Composition and docs
- `CreateCatalogGraphQlTypeModule` passes `eventBus: sp.GetService<IEventBus>()`; `ICatalogSchemaFreshness` is registered as the module singleton. The module disposes the subscription and the signal timer.
- Reference docs: route in `docs/endpoints-and-testing.md`, operator paragraph in `docs/operations-runbook.md` (4.aa), threat-model note next to R3-4.

## 4. Test plan (unit, class `CatalogGraphQlChangeSignalTests`; fake event bus that dispatches synchronously, `FakeTimeProvider`)
- `CatalogChangedEvent_RebuildsAfterDebounce_WithoutPeriodicTick` (the provider cache still holds the old version).
- `BurstOfEventsWithinDebounce_OneCheckOneRebuild` (one check per write batch).
- `ChangeWithoutSchemaEffect_NoRebuild_StatusClears` (consent apply case).
- `EventStream_NoCheckUntilQuiet_ThenOneTrailingRebuild`, `ContinuousEventStream_ChecksAtTheDeferralCap` (5-minute cap).
- `DeferredRebuild_FollowUpCheckEndsTheBurst`, `DeferredRebuild_PeriodicTickDueFirst_NoFollowUpCheck`.
- `SignalBeforeStartupBuild_NoCheck`, `Dispose_ReleasesSubscriptionAndTimer_NoCheckAfterwards`.
- `SchemaStatus_UpdatingUntilExecutorCreated_ThenCurrent`.
- `[Security] SchemaStatus_EventNotDelivered_StoreVersionDecides` (version bumped in the store, event not delivered, provider cache warm: `Updating`).
- `[Security] SchemaStatus_ReadStartedBeforeWrite_IsNotJoined`, `[Security] SchemaStatus_ReadsAreSpaced`.
- `[Security] SchemaStatus_VersionReadFails_ReportsUpdating`, `[Security] SchemaStatus_FailedBuild_ReportsUpdating`.
- `[Security] StatusEndpoint_BodyCarriesStatusOnly`, `StatusEndpoint_NoModule_Returns404`, `[Security] StatusRoute_RefusesAnonymousCallers`.
- Existing `CatalogGraphQlIdleRebuildTests` (#786) stay green unchanged.
- Architecture baselines: route added to `readonly-get-routes.txt` and `readonly-get-head-endpoints.txt`; `endpoint-count.txt` 319 to 320.
- Integration/parity: no fixture change needed (`GraphQlSchemaReadiness` keeps calling `CheckForCatalogChangesAsync(forceFull: true)`); compile check only.

## 5. Security conditions
- SEC-802-1: the status route requires an authenticated principal and returns only `status`; no catalog names, counts, versions or fingerprints.
- SEC-802-2: the status reads the catalog version from the store, uncached, with a read that starts after the request arrived; reads start at least 250 ms apart per instance (bounded store load, the route is not a `/health` route and is rate limited like the rest of the API). A status request never triggers a catalog check or a rebuild.
- SEC-802-3: fail-closed status: an unknown version, a failed version read, a failed build or an unfinished build report `Updating`, never `Current`.
- SEC-802-4: the event handler does no work on the bus thread and ignores the payload (the `reloadSchema` mutation publishes `0:0`); a flood of events causes at most one check per debounce or quiet period, and rebuilds stay behind the #786 latch.
- The data-path gates are unchanged; the schema is not an enforcement point.

## 6. Risks
- `ExecutorCreated` fires right before Hot Chocolate swaps the executor in; the status can report `Current` a few microseconds early. Accepted.
- Silent Hot Chocolate rebuild failure: if Hot Chocolate fails a build after `CreateTypesAsync` returned, `ExecutorCreated` never fires and the status stays `Updating` until the next successful build (the next schema change). That answer is accurate (the new schema is not served); no automatic retry is added here.
- Without Redis, a multi-instance cluster still updates the other instances at their periodic check; the status route on those instances reports `Updating` until then.

## 7. Endpoint ratchet
The new route raises `endpoint-count.txt` from 319 to 320. Per WP-SIMPL-07 this needs the `freeze-exception` label and an owner decision recorded in the PR. The alternative (a field on `/health/ready`) was rejected: it is anonymous and would leak write timing, and a 503 there takes instances out of the load balancer.
