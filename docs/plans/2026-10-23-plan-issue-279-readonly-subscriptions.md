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

## Addendum 2026-10-24: default off

Owner decision 2026-10-24: `Gateway:GraphQL:Subscriptions:Enabled` defaults to `false` (fail closed; not in production, breaking change accepted). This supersedes the "defaults to true" parts of design item 1, test 7 and SEC-279-5. The 5xx known-gap policy stays strict; nothing changes there. Verified on `fix/subscriptions-default-off` @ `b9fac204`.

**Where the default is defined or stated (all must say `false`):**
- REQ-279-A1: `src/Autheris.Domain/Options/Protocols/GraphQlOptions.cs:68`. Change `GraphQLSubscriptionOptions.Enabled` to `= false` and update the summary on line 67 to "Default false; when false the schema exposes no Subscription type (read once at startup)".
- REQ-279-A2: `src/Autheris.Api/appsettings.json:82`. Change to `"Subscriptions": { "Enabled": false }`. Keep the key so operators can find it. `appsettings.Development.json` has no entry; leave it that way.
- REQ-279-A3: `docs/configuration-guide.md:488`. Change the default column to `false`, and state that the root type and the in-memory provider are only registered when the value is `true`. This is a current-behaviour description, kept in the file's existing language.
- REQ-279-A4: `docs/features/p05-subscriptions-realtime.md:50-58`. The example uses `Gateway:Subscriptions:Enabled`, which is not bound by any options class (only `RevocationWatchOptions` lives under that area). Add `Gateway:GraphQL:Subscriptions:Enabled: true` as the opt-in that is required, and drop or relabel the unbound `Enabled` key.
- No occurrences in `deploy/helm`, `deploy/podman-compose*.yaml`, `.github` or any env template, so nothing changes there (default taken: do not add a Helm value).

**Behaviour of the disabled path today (source of truth):**
- REQ-279-A5: Schema. `GatewayGraphQlSubscriptionRegistration.AddGatewaySubscriptions` (`src/Autheris.Api/Extensions/DependencyInjection/GatewayGraphQlSubscriptionRegistration.cs:13-15`), called from `GatewayServiceCollectionExtensions.cs:650`, skips `AddSubscriptionType<Subscription>().AddInMemorySubscriptions()`. So `Schema.SubscriptionType` is null, and introspection returns `subscriptionType: null` with no `Subscription` type in the SDL. The Parity spec already expects this (`tests/Autheris.Tests.Parity/Spec/SpecGraphQlTests.cs:22,44`).
- REQ-279-A6: HTTP POST or GET `/graphql` with `subscription { ... }`. Hot Chocolate document validation rejects it with the standard "operation not supported by schema" validation error before `ReadOnlyOperationMiddleware`, any cost analysis or any resolver runs (`QueryCostAnalyzerRule.cs:277` is null-guarded, SEC-279-6). There is no Autheris-specific code; the HC validation code is the stable code (SEC-279-7). The developer pins the observed `Errors[0].Code` as a constant in the test during the Red step.
- REQ-279-A7: WebSocket (graphql-ws and graphql-transport-ws). The transport stays mapped through `MapGraphQL` (`GatewayApplicationBuilderExtensions.cs:164-167`). `WebSocketAuthInterceptor` stays registered (registration line 19, decision 5), so `connection_init` is still authenticated. A `subscribe` message gets the same validation error frame for its id; no topic subscription and no `CdcSubscriptionGovernor` slot is created. No change here.

**Tests that rely on the old default:**
- REQ-279-A8: `tests/Autheris.Tests.Unit/GraphQL/Issue279ReadOnlySubscriptionTests.cs`.
  - Rename `GraphQLSubscriptionOptions_DefaultsToEnabled` (line 260) to `GraphQLSubscriptionOptions_DefaultsToDisabled` and assert `ShouldBeFalse`.
  - Rename `GatewayOptions_Bind_SubscriptionsSectionAbsent_EnabledTrue` (line 266) to `..._EnabledFalse` and assert `ShouldBeFalse`.
  - Replace `GatewayOptions_Bind_EnabledFalse_IsHonoured` (line 273) with `GatewayOptions_Bind_EnabledTrue_IsHonoured`.
  - The other tests set `Enabled` explicitly or build their own executor with `AddSubscriptionType<S>()` (line 74), so they need no change.
- REQ-279-A9: No WebApplicationFactory host executes a GraphQL subscription. `RealtimeStreamingSubscriptionTests` and `CdcWebhookEndToEndTests` use REST CDC and SSE, and there is no WebSocket client in `tests/`. `WebSocketReadOnlyMutationSecurityTests`, `CatalogGraphQlSchemaTests`, `StartedResponseGql2Tests` and the MCP tests (`CatalogGraphQlMapTests:137`, `McpDatasetToolWiringTests:233`) build their own executors or assert rejection. So no shared fixture needs `Enabled=true`.
  - Rule for future host tests: set `Gateway:GraphQL:Subscriptions:Enabled=true` with `UseSetting` in the host's `NewHost` or constructor, the same way as the existing `Gateway:Authentication:EnableTestAuthHandler` setting.
- REQ-279-A10: Phase 4 runs the full unit suite plus `Autheris.Tests.Parity` and `Autheris.Tests.Architecture` (CI) to catch any SDL snapshot that relies on the old default. There are no `.graphql` or `.snap` snapshots in `tests/` today.

**WP-279-A (single lane, about 5 production LOC and 60 test LOC):** REQ-279-A1 to A4 and A8. New tests in `Issue279ReadOnlySubscriptionTests`, all `[Trait("Category","Security")]`:
1. `GraphQLSubscriptionOptions_DefaultsToDisabled` and `GatewayOptions_Bind_SubscriptionsSectionAbsent_EnabledFalse` (A1).
2. `AppSettings_GraphQLSubscriptionsEnabled_IsFalse`: parse `src/Autheris.Api/appsettings.json` and assert the value is false (A2, guards against drift).
3. `AddGatewaySubscriptions_DefaultOptions_SchemaHasNoSubscriptionType_AndIntrospectionSubscriptionTypeNull`: use `new GraphQLOptions()` without an explicit flag, and assert `__schema { subscriptionType { name } }` returns null (A5).
4. `AddGatewaySubscriptions_DefaultOptions_SubscriptionDocument_RejectedWithStableValidationCode_ResolverNotInvoked`: resolver counter stays 0, and the error contains the pinned HC code with no config value (A6, SEC-279-7 and SEC-279-8).
5. `AddGatewaySubscriptions_DefaultOptions_WebSocketAuthInterceptor_StillRegistered` (A7).
6. `GatewayOptions_Bind_EnabledTrue_IsHonoured` together with the existing `AddGatewaySubscriptions_Enabled_SchemaHasSubscriptionType` and `RegularPrincipal_Subscription_IsNotRejectedByMiddleware` (the enabled path is unchanged).

### Phase 3 review of the addendum (csharp-security-expert, 2026-10-24)

Verdict: APPROVED WITH CONDITIONS. Disabled means no Subscription root type (`AddSubscriptionType` not called, verified in `GatewayGraphQlSubscriptionRegistration.cs`), so no subscription operation can execute over HTTP POST/GET, SSE/multipart or WebSocket; no resolver, governor slot or topic is reachable.

Decision on the WS transport (REQ-279-A7): acceptable to leave mapped. `/graphql` WebSocket also carries queries and mutations, so unmapping `MapGraphQL` WS or rejecting the upgrade would break legitimate traffic and gain no isolation. The upgrade request is already inside `RequireAuthorization` (or `AllowAnonymous` only in the Development anonymous mode), the interceptor authenticates `connection_init` and fail-closes, and `subscribe` dies at HC validation.

- SEC-279-A1: The disabled path must add no Autheris code that special-cases subscriptions; rejection stays HC validation (no custom message embedding config keys, type names or stack data). The pinned `Errors[0].Code` and message must contain no internal type/path/option names beyond the generic HC text.
- SEC-279-A2: `WebSocketAuthInterceptor` stays registered in both modes (REQ-279-A7). Disabled mode must not weaken `connection_init` auth, session expiry, revocation watch or the READ_ONLY propagation (`WebSocketAuthInterceptor.cs:174-178`).
- SEC-279-A3: The default is `false` in code (A1) and in `appsettings.json` (A2); no environment/Helm/compose path may set it to `true`. A missing section, an empty value or an unparseable bool must resolve to `false` or fail startup, never `true` (the existing invalid-bool startup test stays green).
- SEC-279-A4: The flag is read once at startup (`GatewayGraphQlSubscriptionRegistration`); no runtime toggle or per-request override (header, query string, tenant config) may be added. Enabling requires a restart.
- SEC-279-A5: When enabled, `ReadOnlyOperationMiddleware` still rejects mutation/subscription-write attempts from read-only tokens with `READ_ONLY_TOKEN` (PR #288 behaviour) and `READ_ONLY_TOKEN` stays in `ErrorSanitizingFilter` allow-list (line 46); default-flip must not change that code or its message.
- SEC-279-A6: Docs (A3/A4) must state that disabling removes the subscription root type only; WebSocket transport for queries/mutations remains and stays authenticated.

Mandatory tests (all `[Trait("Category","Security")]`):
- `Subscriptions_DefaultOptions_EnabledIsFalse` (bind with section absent, empty section, and appsettings.json value).
- `Subscriptions_InvalidBoolValue_FailsStartup_NotEnabled`.
- `Subscriptions_Disabled_HttpPostSubscription_RejectedByValidation_NoResolverRun` (asserts pinned code, no governor slot, message without internal names).
- `Subscriptions_Disabled_HttpGetAndSseSubscription_Rejected`.
- `Subscriptions_Disabled_WebSocketSubscribe_ErrorFrame_NoSubscriptionCreated` (graphql-transport-ws via host or interceptor-level with executor lacking the type; unauthenticated `connection_init` still rejected).
- `Subscriptions_Disabled_Introspection_SubscriptionTypeNull`.
- `Subscriptions_Enabled_ReadOnlyToken_Mutation_ReturnsReadOnlyTokenCode` (regression for #288 with `Enabled=true`).

### Phase 4 deviations (WP-279-A, 2026-10-24)

- `Subscriptions_Disabled_GetSseAndWebSocketSubscribe_RejectedByValidation_NoSubscriptionCreated` is an executor-level test. GET, SSE and graphql-transport-ws `subscribe` all run the same executor, and no host or WebSocket client exists in `tests/`. The HTTP POST mandatory test and the HTTP GET/SSE test share this executor-level coverage.
- Mandatory test names were merged or renamed: `Subscriptions_Disabled_Introspection_SubscriptionTypeNull` is `AddGatewaySubscriptions_DefaultOptions_SchemaHasNoSubscriptionType_AndIntrospectionSubscriptionTypeNull`; `Subscriptions_DefaultOptions_EnabledIsFalse` is split into the defaults, section-absent, empty-value (startup fails) and `AppSettings_GraphQLSubscriptionsEnabled_IsFalse` tests; `Subscriptions_InvalidBool_FailsStartup_NotEnabled` is the existing `GraphQLSubscriptionOptions_InvalidBool_FailsStartup`; the pre-existing `Subscriptions_Disabled_WebSocketAuthInterceptor_StillRegistered` is kept.
- Final state of the Hot Chocolate pin: the validation error carries no `Code` (null), so the tests pin the generic message "This GraphQL schema does not support `Subscription` operations." instead of a code.
- SEC-279-A2 connection_init: `Subscriptions_Disabled_ConnectionInit_NoToken_Unauthenticated_Rejected` and `Subscriptions_Disabled_ConnectionInit_OriginPresent_AmbientCredentialsNoToken_Rejected` use the real `WebSocketAuthInterceptor`.
