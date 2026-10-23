# Plan: Read-only tokens must not open GraphQL subscriptions; subscription switch (Issue #279)

Phase 2 (csharp-architect). PRD: [2026-10-23-prd-issue-279-readonly-subscriptions.md](2026-10-23-prd-issue-279-readonly-subscriptions.md).
Verified against branch `fix/issue-279-readonly-subscriptions`. One work package (WP-279-1); the parts share the registration and test fixtures.

## Resolved open questions

1. Default taken: `Gateway:GraphQL:Subscriptions:Enabled = true` (PRD Q1).
2. Default taken: code `READ_ONLY_TOKEN`, message "This token only permits read access; subscriptions are rejected." (PRD Q2).
3. Confirmed: a single enforcement point is enough (PRD Q3). Hot Chocolate runs WebSocket `subscribe` messages through the same request
   pipeline, so `UseRequest<ReadOnlyOperationMiddleware>` sees them. The existing `WebSocketReadOnlyMutationSecurityTests` already depend on
   this. `WebSocketAuthInterceptor.OnConnectAsync` (lines 193-201, 250-256) writes the validated principal to session and connection
   `ContextData[nameof(ClaimsPrincipal)]`, and `ResolvePrincipal` reads that first. No extra interceptor check and no change to
   `ReadOnlyTokenMiddleware` (HTTP) are needed. The CDC slot is acquired in the resolver (`Subscription.cs:126`, `CdcSubscriptionGovernor`),
   after the middleware, so a rejected request never acquires a slot (REQ-7).
4. Default taken: webhooks, `Streaming:Cdc` and `CdcSubscriptionGovernor` are not affected by the switch (PRD Q4).
5. Architect decision (deviates from the REQ-279-3 wording, fail-closed): with `Enabled=false`, `WebSocketAuthInterceptor` stays registered.
   `UseWebSockets()` and the socket transport remain for queries (PRD non-goal 3). Removing the interceptor would drop socket
   authentication and the revocation watch. Only the subscription root type and the in-memory provider are removed. Phase 3 to confirm.

## Design

1. Options: `src/Autheris.Domain/Options/Protocols/GraphQlOptions.cs` adds `public GraphQLSubscriptionOptions Subscriptions { get; init; } = new();`
   and `public sealed class GraphQLSubscriptionOptions { public bool Enabled { get; init; } = true; }`. The existing `GatewayOptions` binding
   applies. A non-bool value fails the binder at startup, which is fail-closed.
2. Middleware: `src/Autheris.GraphQL/Interceptors/ReadOnlyOperationMiddleware.cs`. Replace `IsMutation` with
   `internal static bool SelectsOperation(DocumentNode, string? operationName, OperationType)` (same fail-closed selection) and keep
   `IsMutation`/`IsSubscription` as thin wrappers. In `InvokeAsync`, a read-only principal with a selected subscription is rejected first, with
   no self-service branch. The mutation path is unchanged. Extract the error building into a private helper shared by both paths.
3. Registration: new `src/Autheris.Api/Extensions/DependencyInjection/GatewayGraphQlSubscriptionRegistration.cs` with
   `internal static IRequestExecutorBuilder AddGatewaySubscriptions(this IRequestExecutorBuilder b, GraphQLOptions o)`. It calls
   `AddSubscriptionType<Subscription>().AddInMemorySubscriptions()` only when `o.Subscriptions.Enabled`, and always calls
   `AddSocketSessionInterceptor(...)` (decision 5). `GatewayServiceCollectionExtensions.cs:636-638` calls this helper. Add `InternalsVisibleTo`
   for the unit tests if it is missing.
4. Tolerance (REQ-6): `QueryCostAnalyzerRule.cs:277/515` must not throw when `schema.SubscriptionType` is null (null-guard, so the
   standard unknown-root validation error is reported). `CatalogSchemaModel.cs:219` (reserved names) and the MCP executor
   (`GatewayMcpQueryExecutor.cs:420`) need no change.
5. Config/docs: add `"Subscriptions": { "Enabled": true }` under `Gateway:GraphQL` in `src/Autheris.Api/appsettings.json`, and add a row
   to `docs/configuration-guide.md` next to the other GraphQL options (reference text only).

## Tests

Unit tests in `tests/Autheris.Tests.Unit/GraphQL/Issue279ReadOnlySubscriptionTests.cs`, which run locally. They use an in-memory Hot Chocolate
executor with `ReadOnlyOperationMiddleware`, a dummy subscription type and in-memory subscriptions, and follow the pattern in
`WebSocketReadOnlyMutationSecurityTests.cs:202`.
1. `ReadOnlyPrincipal_Subscription_IsRejectedWithReadOnlyToken_AndResolverNotInvoked` (AC-1; resolver counter stays 0).
2. `ReadOnlyPrincipal_MixedDocument_NoOperationName_IsRejected`, `..._OperationNameQuery_IsExecuted`, `..._OperationNameSubscription_IsRejected` (AC-2).
3. `ReadOnlyPrincipal_WithSelfServiceScope_Subscription_IsRejected` (AC-3).
4. `RegularPrincipal_Subscription_IsExecuted` (REQ-8).
5. `ReadOnlyPrincipal_FromSocketContextData_Subscription_IsRejected` (principal only in `ContextData`, no HttpContext; REQ-7).
6. `SelectsOperation_*` theory over mutation, subscription and query with and without `operationName`.
7. `GraphQLSubscriptionOptions_DefaultsToEnabled` and `GatewayOptions_Bind_SubscriptionsSectionAbsent_EnabledTrue`, `..._InvalidBool_Throws` (AC-6).
8. `AddGatewaySubscriptions_Disabled_SchemaHasNoSubscriptionType_AndSubscriptionDocumentFailsValidation`,
   `AddGatewaySubscriptions_Enabled_SchemaHasSubscriptionType` (REQ-3).
9. `QueryCostAnalyzerRule_SchemaWithoutSubscriptionType_SubscriptionDocument_DoesNotThrow` (REQ-6).
10. Existing `ReadOnlyTokenTests` and `WebSocketReadOnlyMutationSecurityTests` stay green (AC-7).

Integration tests (CI only, optional) in the existing API integration test project:
`SubscriptionsDisabled_AppStarts_SdlHasNoSubscription_WsSubscribeErrors` (AC-4) and
`SubscriptionsEnabled_ReadOnlyWsSubscribe_ReadOnlyTokenError_NoGovernorSlot` (AC-5).

## Work package

WP-279-1 (single lane): design items 1-5 and tests 1-10. Estimated size: about 60 production LOC and about 250 test LOC.

## Phase 3 security conditions

Phase 3 (csharp-security-expert). Verified in code: `ReadOnlyOperationMiddleware` runs after `UseDocumentParser`/`UseDocumentValidation` and before cache, resolver
and execution in the single pipeline (`GatewayServiceCollectionExtensions.cs:~610`), so HTTP, SSE and WebSocket `subscribe` share it. `ResolvePrincipal` reads
`ContextData[nameof(ClaimsPrincipal)]` first, then HttpContext. `IsReadOnly` is null-safe and issuer-bound.

**Verdict: APPROVED WITH CONDITIONS.**

Decision on plan item 5: keep `WebSocketAuthInterceptor` registered when `Subscriptions:Enabled=false`. Agreed; removing it would drop socket authentication and the
revocation watch for query traffic, which weakens an existing control. REQ-279-3 wording must be amended to match.

- SEC-279-1: Operation selection is fail-closed. Without `operationName`, every operation in the document counts (any subscription in a multi-operation document rejects a read-only principal). With a name, only an exact ordinal match counts. Fragments cannot carry operation types, so no extra handling is needed; the theory must still cover them.
- SEC-279-2: The subscription rejection runs before any self-service evaluation and never consults scopes or `SelfServiceEnabled`. The check must not depend on `IOptions<GatewayOptions>` being resolvable.
- SEC-279-3: The rejection sets `context.Result` and returns without calling `_next`. The error contains only the fixed message and code `READ_ONLY_TOKEN`: no operation name, document text, token claims, extensions or exception detail.
- SEC-279-4: A null or anonymous principal is not this middleware's concern, but the plan must not add a path where a missing principal skips the subscription check differently from mutations. Behaviour for non-read-only principals stays unchanged (REQ-8).
- SEC-279-5: `Subscriptions:Enabled` is read once at startup, defaults to true, and a missing or invalid value never silently disables or enables beyond the documented default (invalid bool fails startup). With `Enabled=false` the subscription root, `AddInMemorySubscriptions`, and nothing else is removed; the read-only middleware stays registered in both modes (defence in depth).
- SEC-279-6: The `QueryCostAnalyzerRule` null-guard must degrade to the standard validation error. A null `SubscriptionType` must never be treated as "no cost" for an operation that is otherwise accepted.
- SEC-279-7: The error message must not reveal whether subscriptions are disabled to a read-only caller differently from the enabled case beyond the standard unknown-root validation error. No config values in logs.
- SEC-279-8: Rejected requests must not reach the resolver (no `CdcSubscriptionGovernor` slot, no topic subscription); tests assert the resolver counter stays 0.

Mandatory tests (all `[Trait("Category","Security")]`, in `Issue279ReadOnlySubscriptionTests`): plan tests 1-5 and 8, plus
- `ReadOnlyPrincipal_Subscription_ErrorContainsOnlyFixedMessageAndCode` (SEC-279-3)
- `ReadOnlyPrincipal_SubscriptionNamedInMultiOperationDocument_NoOperationName_IsRejected` (SEC-279-1)
- `ReadOnlyPrincipal_SubscriptionWithFragments_IsRejected` (SEC-279-1)
- `ReadOnlyPrincipal_SubscriptionOperationName_NotMatchingAnyOperation_DoesNotExecute` (SEC-279-1)
- `ReadOnlyPrincipal_SelfServiceDisabled_OptionsUnresolvable_Subscription_IsRejected` (SEC-279-2)
- `Subscriptions_Disabled_WebSocketAuthInterceptor_StillRegistered` (item 5)
- `Subscriptions_Disabled_ReadOnlyMutation_StillRejected` (SEC-279-5)
- `GraphQLSubscriptionOptions_InvalidBool_FailsStartup` (SEC-279-5)

## Changelog

- 2026-10-23 (Phase 4, G-PLAN-REBASE drift): the registration file sits in `src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.cs` (not `.../DependencyInjection/`); the helper `GatewayGraphQlSubscriptionRegistration.cs` is in `Extensions/DependencyInjection/`. Multi-operation documents without `operationName` are already rejected by Hot Chocolate validation before the middleware runs; the middleware check remains as defence in depth. Optional integration tests (AC-4/AC-5) were not added. Test fixture subscription type is named `S`.
