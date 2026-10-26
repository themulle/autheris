# Plan: flaky `T_CAT138_21` and the invalidation race in `DbCatalogVersionProvider`

Id: `PLAN-FLAKY-CAT138-21`. Owner decision 2026-10-25: fix directly, no issue. Lean lifecycle (no PRD; architect and security conditions written by the delivery PM, "default taken").

## Symptom

`CatalogAtScaleSecurityTests.T_CAT138_21_DbCatalogVersionProvider_CachesAndInvalidatesOnEvent` failed once in CI (PR #353, 363 ms) and passed on rerun.

## Root cause

1. **Test (primary).** `ICatalogVersionProvider.CatalogChangedEventChannel` (`catalog:changed`) is not an invalidation channel in `InProcessChannelEventBus.IsInvalidationChannel`, so `PublishAsync` only enqueues the event. Delivery runs later on the consumer loop and a per-subscriber `Task.Run`. The test waits a fixed `Task.Delay(50)` and then expects the cache to be invalidated. On a loaded CI runner (thread-pool starvation from the parallel test classes) the handler has not run after 50 ms, the cached version `1` is returned and `v3.StructureVersion.ShouldBe(2)` fails.
2. **Product (latent).** `DbCatalogVersionProvider.GetCatalogVersionAsync` reads the repository outside the lock and then stores the result unconditionally. An `Invalidate()` that arrives while that read is in flight is lost: the pre-change value is cached for a full TTL after the change event.

`catalog:changed` deliberately stays on the asynchronous queue: its subscribers (`CatalogSnapshotProvider` refresh, GraphQL schema rebuild) do database work that must not run inline in the publisher (repository upserts, `reloadSchema`).

## Fix

- **F1 (product).** `DbCatalogVersionProvider` keeps an invalidation generation, incremented by `Invalidate()` under the lock. A load records the generation before the repository read and caches its result only when the generation is unchanged; it still returns the value it read. The TTL clock moves to an injectable `TimeProvider` (default `TimeProvider.System`) so TTL behaviour is testable without wall-clock waits.
- **F2 (test).** `T_CAT138_21` no longer waits on the asynchronous bus. It captures the handler the provider registers on `catalog:changed` (substitute `IEventBus`) and awaits it directly, then asserts the reload. No `Task.Delay`, no retries; assertions unchanged in strength.
- **F3 (regression tests, written first).** Deterministic tests with a repository read gated by a `TaskCompletionSource`: an invalidation during an in-flight load must not leave the stale value cached (the next call reads the repository again); TTL expiry and TTL hit driven by `FakeTimeProvider`.

## Binding conditions (default taken)

- `SEC-CAT138-F1`: an invalidation observed during a load always wins; no code path caches a value read before the latest `Invalidate()`.
- `SEC-CAT138-F2`: no sleeps, retries or weakened assertions in the changed tests; every wait is on a signal the test controls.
- `SEC-CAT138-F3`: the event-bus dispatch semantics of `catalog:changed` are unchanged.

## Verification

Old test looped under CPU load to reproduce; new tests looped 200x under the same load plus the full unit suite.
