# PLAN-OI-802: Prompt GraphQL schema rebuild after catalog writes, schema status endpoint (issue #802)

Issue: [#802](https://github.com/themulle/autheris/issues/802). Builds on [#786](2026-11-17-plan-issue-786-catalog-idle-cpu.md) (coalescing, quiet period, version short-circuit). Related, not implemented here: [#672](https://github.com/themulle/autheris/issues/672) (one version bump per multi-table write), [#675](https://github.com/themulle/autheris/issues/675) (debounce change events). Bug fix: one new anonymous status endpoint, no new options.

**Status: IMPLEMENTED (WP-802-1..3)** in one PR.

## 1. Product decision

After a dbt sync, a governance import or a consent apply, a client must be able to query the new datasets within seconds, and a seeding job must be able to wait for that moment without guessing.

- Committed catalog writes that change the GraphQL-visible schema trigger a prompt, coalesced rebuild on every instance that receives the change event. The periodic check stays as the backstop.
- `/health/ready` keeps its meaning (the instance can serve). It does not turn 503 during a routine rebuild: at 3,000 tables one rebuild takes about 4 minutes (#786), and all instances rebuild at the same time, so a 503 would take the whole cluster out of the load balancer for every catalog write.
- A separate anonymous endpoint `GET /health/graphql-schema` reports whether the served GraphQL schema is behind the catalog: `200 {"status":"Current"}` or `503 {"status":"Updating"}` with `Retry-After: 1`. A client polls it until 200. It is not meant as a load balancer probe.
- Consent apply does not change the GraphQL schema. The schema is global (one executor per process, built from catalog metadata, transport visibility and the static partitioning options). Consents are evaluated per request; the shield middleware (`GraphQlEnumerationShieldMiddleware`) makes unknown and denied fields look the same. `DeclarativeConsentApplyService` only calls `ReplaceConsentsAsync`; table-level consents bump a table epoch only, pattern grants bump the content version and publish `catalog:changed`. Neither changes the schema fingerprint (`CatalogSchemaModel.Fingerprint` hashes structure; the epoch is only the fingerprint cache key), so a consent apply costs at most one candidate check and never a rebuild.

## 2. Root cause

- `CatalogGraphQlTypeModule` learns about catalog changes only from its periodic timer (`CatalogGraphQlTypeModule.Refresh.cs:229-245`, `GraphQlOptions.CatalogSchemaRefreshSeconds = 60`). It does not subscribe to `catalog:changed`, which every committed catalog write publishes (`GovernanceRepository.CatalogRows.cs:57-73`, once per write scope or chunk via `CatalogWriteScope`, immediately for restrictive writes). The snapshot provider, the search warm-up, the tenant lookup and the version provider do subscribe. The `reloadSchema` mutation (`Mutation.Catalog.cs:45`) even publishes on that channel for the GraphQL module, which never listened.
- Nothing reports that the served schema is behind the catalog, so `/health/ready` says 200 while new fields fail validation.

Multi-instance: `catalog:changed` goes through `IEventBus`. With Redis (`Caching:Redis:Enabled`) every instance receives it; with the in-process bus only the writing instance does, and the other instances keep relying on the periodic check (unchanged; PostgreSQL has no LISTEN/NOTIFY path). The status endpoint compares against the catalog version read through `ICatalogVersionProvider` (5 s cache), so it reports `Updating` on those instances too until their periodic check has run.

## 3. Design (smallest change on top of #786)

### WP-802-1 Event-driven check with debounce (module, new partial `CatalogGraphQlTypeModule.ChangeSignal.cs`)
- The module takes an optional `IEventBus` and subscribes to `ICatalogVersionProvider.CatalogChangedEventChannel`. The handler only increments a signal sequence and arms a one-shot `TimeProvider` timer; no work runs on the bus thread.
- Scheduling:
  - First signal after a quiet period (leading edge): the check runs after `ChangeSignalDebounce = 1 s`, so the writes of one request land in one check. A later signal does not push it out.
  - Signals within the quiet period of the previous signal (a burst): the check moves to `lastSignal + QuietPeriod`, capped at `burstStart + MaxRebuildDeferral` (5 min, #786). A continuous write stream therefore causes no event-driven checks until it settles; the periodic check keeps its #786 behaviour.
  - `QuietPeriod = clamp(last executor build duration, 2 s, 60 s)`. Small catalogs settle in 2 s; at 3,000 tables (build about 4 min) it is 60 s, the same as today's periodic tick, so no more rebuilds than with #786.
  - A check that saw a further change and deferred the rebuild (#786 quiet-period rule) schedules one follow-up check at `QuietPeriod`, so the trailing rebuild does not wait for the periodic tick.
  - Signals that arrive while a check runs schedule one trailing check.
- The timer runs `CheckForCatalogChangesAsync(fromTimer: false, forceFull: true)`. `forceFull` is required: the version provider's cache is invalidated by the same asynchronous event and may still hold the old version. The candidate check decides by fingerprint, so a write that does not change the schema (consent, pattern grant, description outside the schema) updates the built version and raises no rebuild.
- Before the startup build has started, a signal is acknowledged without a check (the startup build reads the current catalog).
- Rebuild requests still go through the #786 latch (at most one running, one waiting, newest model wins). The new path only makes the leading-edge and trailing checks earlier; it never raises a rebuild by itself.
- `CreateTypesAsync` (fresh-build branch) reads the catalog version before it builds the model, so the recorded version can never claim a write that the model does not contain.

### WP-802-2 Schema freshness (module plus endpoint)
- New `ICatalogSchemaFreshness.IsSchemaCurrentAsync(ct)` (`src/Autheris.GraphQL/Catalog/`), implemented by the module. `false` when, read in pipeline order:
  1. a change signal is not yet checked (`signalSeq != checkedSignalSeq`);
  2. a changed model waits (`_pendingBuild != null`, deferral);
  3. a rebuild is requested (`_rebuildRequested`);
  4. no build has started yet, or a build is in flight (set at `CreateTypesAsync` start, cleared by `ReportExecutorCreated`, which Hot Chocolate raises when the new executor is created);
  5. the last build failed closed (`_built.Fingerprint is null`);
  6. the catalog version read through the version provider (single-flight, 2 s timeout) is newer than the built version. A failed or timed-out read reports `Updating` (fail-closed).
- `GET /health/graphql-schema` (`HealthEndpoints.cs`): anonymous, audit-exempt, `Cache-Control: no-store`; `200 {"status":"Current"}` or `503 {"status":"Updating"}` with `Retry-After: 1`; `404` when the GraphQL catalog module is not registered. The body never carries counts, versions or names, also not in Development.
- Stage 1 closes the race between commit and check on the writing instance as soon as the event is dispatched (in-process bus: milliseconds after the commit, before the HTTP response normally reaches the client). On other instances it is the Redis delivery or, without Redis, stage 6.

### WP-802-3 Composition
- `CreateCatalogGraphQlTypeModule` passes `eventBus: sp.GetService<IEventBus>()`; `ICatalogSchemaFreshness` is registered as the module singleton. The module disposes the subscription and the signal timer.

## 4. Test plan (unit, new class `CatalogGraphQlChangeSignalTests`; fake event bus that dispatches synchronously, `FakeTimeProvider`)
- `CatalogChangedEvent_RebuildsAfterDebounce_WithoutPeriodicTick`: refresh interval 0, a write plus event, advance 1 s: the schema contains the new table.
- `BurstOfEventsWithinDebounce_OneCheckOneRebuild`: 10 events within the debounce give one rebuild request (coalescing per write batch).
- `ChangeWithoutSchemaEffect_NoRebuild_StatusClears` (consent apply case): event with an unchanged fingerprint gives no rebuild; the status is `Updating` until the check ran and `Current` afterwards.
- `EventStream_NoCheckUntilQuiet_ThenOneTrailingRebuild`: events every 500 ms for 10 s after the leading check cause no further check until 2 s after the last event; then one trailing rebuild.
- `SignalBeforeStartupBuild_NoCheck`.
- `SchemaStatus_UpdatingUntilExecutorCreated_ThenCurrent`: status reports `Updating` from the event until the new executor is created.
- `SchemaStatus_VersionNewerThanBuilt_ReportsUpdating` (another instance wrote, no event).
- `[Security] SchemaStatus_VersionReadFails_ReportsUpdating`, `[Security] SchemaStatus_FailedBuild_ReportsUpdating`.
- `[Security] HealthGraphQlSchema_BodyCarriesStatusOnly` (endpoint handler: status codes, `Retry-After`, `no-store`, body key `status` only, also in Development) and `HealthGraphQlSchema_NoModule_Returns404`.
- Existing `CatalogGraphQlIdleRebuildTests` (#786) must stay green unchanged.
- Architecture baselines: `/health/graphql-schema` added to `readonly-get-routes.txt`, `readonly-get-head-endpoints.txt` and `ingress-exemptions.txt`.
- Integration/parity: no fixture change needed (`GraphQlSchemaReadiness` keeps calling `CheckForCatalogChangesAsync(forceFull: true)`); compile check only.

## 5. Security conditions
- SEC-802-1: the status endpoint is anonymous and returns only `status`; no catalog names, counts, versions or fingerprints, in every environment.
- SEC-802-2: an anonymous status request never triggers a catalog check or a rebuild, and reaches the governance store at most through the version provider's cache with a single-flight read (no amplification beyond one read per cache period per instance).
- SEC-802-3: fail-closed status: an unknown version, a failed version read, a failed build or an unfinished build report `Updating`, never `Current`.
- SEC-802-4: the event handler does no work on the bus thread and ignores the payload (the `reloadSchema` mutation publishes `0:0`); a flood of events causes at most one check per debounce or quiet period, and rebuilds stay behind the #786 latch.
- The data-path gates are unchanged; the schema is not an enforcement point.

## 6. Risks
- `ExecutorCreated` fires right before Hot Chocolate swaps the executor in; the status can report `Current` a few microseconds early. Accepted.
- Without Redis, a multi-instance cluster still updates the other instances at their periodic check; the status endpoint reports that honestly.
