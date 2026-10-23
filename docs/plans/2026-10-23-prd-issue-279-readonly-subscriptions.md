# PRD: Read-only tokens must not open GraphQL subscriptions; subscription switch (Issue #279)

ID prefix: `REQ-279`. Phase 1 (product-manager). Type: security hardening / config. Affects v1.2.9 to v1.2.11.

## 1. Problem

1. `ReadOnlyOperationMiddleware.InvokeAsync` (`src/Autheris.GraphQL/Interceptors/ReadOnlyOperationMiddleware.cs`) checks `IsMutation` only.
   Subscription operations (`onTableChanged`) pass through for read-only principals (`ClaimsPrincipal.IsReadOnly()`, scopes from
   `Gateway:Authentication:EntraId:ReadOnlyScopes`).
2. `GatewayServiceCollectionExtensions.cs` (~L636-638) registers `AddSubscriptionType<Subscription>()`, `AddInMemorySubscriptions()` and the
   socket session interceptor unconditionally. No switch exists.
3. Data exposure is already governed (`Subscription.OnTableChanged` checks table access, row filters, masks). The issue is the extra surface
   (long-lived connections, event stream) for tokens meant to be strictly request/response.

## 2. Requirements

1. REQ-279-1: A read-only principal executing a document whose selected operation is a subscription is rejected before execution with
   code `READ_ONLY_TOKEN`, same error shape as mutations (message states that subscriptions are rejected for read-only tokens).
2. REQ-279-2: Operation selection is fail-closed: with no `operationName`, any subscription operation in the document counts (as for mutations);
   with `operationName`, the named operation is checked. No self-service exception applies to subscriptions.
3. REQ-279-3: New option `Gateway:GraphQL:Subscriptions:Enabled` (bool). When `false`, the subscription root type, in-memory subscription
   provider are not registered (the `WebSocketAuthInterceptor` stays registered for query traffic; Phase 3 decision 2026-10-23); subscription documents fail validation (unknown
   root type) for all principals; the schema SDL/introspection exposes no `Subscription` type. When `true`, current behavior plus REQ-279-1.
4. REQ-279-4: Default taken: `Enabled = true` (feature exists and is documented; not a data-exposure change). The read-only rejection
   (REQ-279-1) is not configurable and always on. Deployments wanting strict request/response set `Enabled=false`.
5. REQ-279-5: Option is bound and validated at startup with the existing options pattern (`GraphQlOptions` family); invalid value fails startup
   (fail-closed). Documented in the configuration reference/`appsettings` samples where the other GraphQL options are listed.
6. REQ-279-6: Any dependent component that assumes subscriptions exist (CDC subscription governor, revocation watch, dev portal/MCP schema
   catalog, `QueryCostAnalyzerRule`, health/warmup) must tolerate `Enabled=false` without startup errors or dangling registrations.
7. REQ-279-7: A read-only token opening a WebSocket connection and subscribing receives the `READ_ONLY_TOKEN` error and no CDC resources
   (subscription slots under `MaxSubscriptionsPerSubject/PerTenant`) are acquired.
8. REQ-279-8: Existing read-only behavior for queries and mutations is unchanged; non-read-only principals can still subscribe when enabled.

## 3. Acceptance criteria

1. AC-1: Unit: read-only principal + `subscription { onTableChanged ... }` yields `READ_ONLY_TOKEN`, `next` not invoked (REQ-1).
2. AC-2: Unit: document with query + subscription, no operationName, read-only principal: rejected; with operationName naming the query: allowed;
   naming the subscription: rejected (REQ-2).
3. AC-3: Unit: principal holding a self-service scope is still rejected for subscriptions (REQ-2).
4. AC-4: Integration: `Enabled=false` -> schema has no `Subscription` type, subscription request fails validation for admin and read-only
   principals, WebSocket subscribe yields an error, app starts cleanly (REQ-3, 6).
5. AC-5: Integration: `Enabled=true` (default) -> non-read-only principal subscribes as before; read-only principal rejected over HTTP-less
   WebSocket path and holds no subscription slot (REQ-7, 8).
6. AC-6: Default of the option is `true` when the section is absent; bad value fails startup (REQ-4, 5).
7. AC-7: Existing mutation/query read-only tests stay green (REQ-8).

## 4. Non-goals

1. No change to per-event table access, row filter, mask enforcement.
2. No per-scope subscription allowlist for read-only tokens (rejected: complexity, no demand; the switch covers strict deployments).
3. `UseWebSockets()` stays in the pipeline (shared with other consumers); only the GraphQL subscription transport is gated.

## 5. Open questions (recommended default in brackets)

1. Default of `Subscriptions:Enabled`: true vs false. [true; default taken, revisit if owner prefers fail-closed-by-default].
2. Error message wording for subscriptions. [Same code `READ_ONLY_TOKEN`, message "This token only permits read access; subscriptions are rejected."].
3. Whether `ReadOnlyTokenMiddleware` (HTTP layer) also needs a subscription check. [Phase 2 to confirm; GraphQL middleware is the single enforcement point if it sees all socket operations].
4. Does `Enabled=false` also hide dependent `Streaming:Cdc`/webhook features? [No, independent; only the GraphQL subscription surface].
