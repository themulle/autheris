# Plan: Aggregation `where` with values on SQL sources (Issue #280, REQ-AGG-280)

Phase 2 architecture plan for [PRD](2026-10-22-prd-issue-280-aggregation-where-params.md). One work package (WP-280-1).

## 1. Root cause (verified on `fix/issue-280-aggregation-where-params`)

1. GraphQL `<table>_aggregate` (`AggregateGraphQlTypes.cs:218-230`) builds `where` with `TreeFilterClauseBuilder.Build`, whose markers are
   `@__gql_tf_N` (`TreeFilterClauseBuilder.ParameterPrefix`); `in`/`notIn`/`between` expand to one scalar marker per value
   (`TreeFilterRenderer.cs:94`).
2. `GovernedAggregationService.ExecuteAndBuildBucketsAsync` (`GovernedAggregationService.ValidationAndExecution.cs:414-433`) plans with
   `PlanAggregate` and sends `planned.Parameters` as `GovernedSqlQueryRequest.Parameters` (`PlannedTableRead: true`).
3. `GovernedSqlExecutor.ExecuteCoreAsync` (line 249) calls `GovernedSqlExecutionService.EnsureClientParameterNames(request.Parameters)`,
   which rejects `__gql_` names, so every valued aggregate `where` fails. `isNull` has no marker, so it passes.
4. Open question 1 (list field route): the GraphQL list field does not use the governed executor at all. It runs through
   `GovernedTreeQueryService` + `TreeSqlCompiler`, which names markers `@gqN`/`@rfN_*` and binds them on its own `DbCommand`
   (`GovernedTreeQueryService.cs:740`). That route is a different compiler and is not reusable for the planner's aggregate SQL.
   OData/REST list reads use `SqlTableReadExecutor` with non-reserved names (`@p_arg_N`, OData names) in `Parameters`; they bind as
   client-named values. Decision: reuse that binding (client-named slots, values never in text) and only add a separate, trusted
   input channel for the names.
5. REST analytics `where` arrives as `TableFilterClause.FromTrinoText` with zero parameters (`AnalyticsEndpoints.cs:97,181`); metric
   queries build typed trees with the same builder. So every value in `PlannedTableAggregate.Parameters` has a gateway-generated name.

## 2. Decisions

D1. Trusted channel. Add to `GovernedSqlQueryRequest` (`src/Autheris.Application/Sql/Interfaces/IGovernedSqlExecutionService.cs`) a body
member, not a positional parameter:
```csharp
/// <summary>Issue #280: parameters whose names the gateway generated for a planned read (internal; never from client input).</summary>
internal IReadOnlyDictionary<string, object?>? GatewayParameters { get; init; }
```
Structural trust: the setter is `internal` to `Autheris.Application` (InternalsVisibleTo is only Tests.Unit, Tests.SqlCompilerGate,
Benchmarks), so `Autheris.Api`, `Autheris.GraphQL` and `Autheris.Extensions` cannot set it, and System.Text.Json never binds a
non-public member. `PlannedTableRead` alone is NOT trust: it is a public positional flag and the request may still carry client `Parameters`.

D2. Merge after the guard. New method in `GovernedSqlExecutionService` (`src/Autheris.Application/Sql/Services/GovernedSqlExecutionService.cs`):
```csharp
internal static IReadOnlyDictionary<string, object?>? MergeGatewayParameters(GovernedSqlQueryRequest request)
```
- Returns `request.Parameters` unchanged when `GatewayParameters` is null or empty.
- `!request.PlannedTableRead` → `throw new System.Security.SecurityException("Gateway parameters require a planned table read (fail-closed).")`.
- Builds `Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)` keyed by `name.TrimStart('@')`: client entries with `TryAdd`
  (first case spelling wins, as `BuildClientValues` does today), then gateway entries; a gateway key that fails `TryAdd` (collision
  with a client key or another gateway key) → `throw new System.Security.SecurityException("A statement parameter name is bound twice (fail-closed).")`.
- A gateway value with `ListParameterNormalizer.IsListCandidate(value)` or a name where `CallerAttributeParameterName.IsReserved(trimmed)`
  → `throw new InvalidOperationException("A gateway parameter is not a scalar planned-read parameter.")` (the gateway never builds either).

D3. Executor wiring (`src/Autheris.Application/Sql/Services/GovernedSqlExecutor.cs`, `ExecuteCoreAsync`, all entry points funnel here):
keep line 249 `EnsureClientParameterNames(request.Parameters)` unchanged (client names only), then
`var bindNames = GovernedSqlExecutionService.MergeGatewayParameters(request);` and pass `bindNames` instead of `request.Parameters` to
`NormalizeClientParameters` (line 251). In `BuildClientValues(GovernedSqlQueryRequest, IReadOnlySet<string>)` (line 674): wrap the client
loop in `if (parameters != null) { ... }` (no early return), then append
`foreach (var (name, value) in request.GatewayParameters ?? EmptyParameters) values.Add(name.TrimStart('@'), value);` (`Add`, not indexer:
second safety net). Gateway values skip `AdmitList`/`WebSqlParameterValues.Normalize` (typed CLR scalars, already coerced by
`TreeFilterClauseBuilder`). They bind as `ParameterOrigin.ClientNamed` slots exactly like OData planned reads; RLS, masking, tenant filter,
audit and row limits are untouched. Add `private static readonly IReadOnlyDictionary<string, object?> EmptyParameters = new Dictionary<string, object?>();`
if no such field exists.

D4. Producer (`GovernedAggregationService.ValidationAndExecution.cs:427`):
```csharp
var sqlRequest = new GovernedSqlQueryRequest(planned.Sql, Parameters: null, Transport: query.Transport ?? SqlTransports.Rest, PlannedTableRead: true)
{
    GatewayParameters = planned.Parameters.Count > 0 ? planned.Parameters : null,
};
```
No other producer changes (`SqlTableReadExecutor` names are not reserved; out of scope).

D5. Federated/DuckDB. A single-table planned aggregate never raises `CrossSourceRoutingException`, and the default cross-source transport
allowlist (WebSql, Trino) excludes Rest/GraphQL, so aggregation does not reach `FederatedDuckDbExecutionService` today (default taken). In
`ExecuteFederatedInternalAsync` add, directly before line 264 `EnsureClientParameterNames`:
`if (request.GatewayParameters is { Count: > 0 }) throw new WebSqlPolicyException("Gateway-planned parameters are not supported on the federated path.");`
Client-name guard and `FederatedParameterContract.ValidateParameterName` stay unchanged.

D6. Error surface: unchanged mapping (SecurityException → `SECURITY_VIOLATION`, WebSqlPolicyException → policy error); no value or name
of a gateway parameter appears in an exception message.

## 3. WP-280-1 (single package)

Files: `IGovernedSqlExecutionService.cs` (D1), `GovernedSqlExecutionService.cs` (D2), `GovernedSqlExecutor.cs` (D3),
`GovernedAggregationService.ValidationAndExecution.cs` (D4), `FederatedDuckDbExecutionService.cs` (D5). Red first, in this order.

Extend `tests/Autheris.Tests.Unit/Sql/GovernedSqlExecutionParameterSecurityTests.cs` (fixture `CreateService`; capture SQL and parameters with
`FakeDbConnection`/`FakeDbCommand` from `tests/Autheris.Tests.Unit/Sql/GovernedTestSupport.cs`):
1. `Issue280_PlannedRead_GatewayParameters_Eq_BindsValue_NeverInText` (regression: `WHERE region = @__gql_tf_0`, value `north`; command text
   has no `north`, a bound parameter carries it).
2. `Issue280_PlannedRead_GatewayParameters_InAndBetween_Bind` (`IN (@__gql_tf_0, @__gql_tf_1)`, `>= @__gql_tf_2 AND < @__gql_tf_3` with
   `DateTime` values; bound values keep CLR type `DateTime`).
3. `Issue280_ClientReservedName_StillRejected_WhenPlannedRead` (Theory: `__gql_tf_0`, `@__gql_rls_0`, `p_rls_x`, `rls_x`, `p_pd_x`,
   `__rls_ks`, upper case; `PlannedTableRead: true` and false; `WebSqlPolicyException` "reserved gateway parameter name").
4. `Issue280_GatewayAndClientNameCollision_FailsClosed` (client `region`, gateway `@REGION` → `SecurityException`, no command executed).
5. `Issue280_GatewayParameters_WithoutPlannedRead_FailsClosed` (→ `SecurityException`, no command executed).
6. `Issue280_GatewayListValue_FailsClosed` (gateway value `new[] {1,2}` → `InvalidOperationException`).
7. `Issue280_GatewayParameters_NotSettableOutsideApplication` (reflection: `GatewayParameters` setter and getter are not public).

Extend `tests/Autheris.Tests.Unit/Analytics/AggregationPlannerRoutingTests.cs` (uses `Create(...)` with captured `Requests`):
8. `Aggregation_TypedWhere_SendsGatewayParameters_NotClientParameters` (`Parameters` null, `GatewayParameters` holds the `__gql_tf_` keys,
   `PlannedTableRead` true).

Federated (harness `tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedServiceHarness.cs`):
9. `Issue280_Federated_GatewayParameters_FailClosed` and `Issue280_Federated_ClientReservedName_StillRejected` (PlannedTableRead true).

Integration (existing Testcontainers fixtures, `SharedDatabaseServers`): add to `PostgreSqlIntegrationTests.cs` and `MsSqlIntegrationTests.cs`
`Aggregate_WhereEqAndTimeWindow_GroupedMax_ReturnsExpectedRows` driving `GovernedAggregationService` (or the GraphQL endpoint where the
fixture hosts it) with `eq`, `in`, `gte`/`lt` and `MAX`/`COUNT` per group, plus RLS on the table (AC 1, 3, 8). The lead runs them.

## 4. Open questions (default taken)

1. List field route: confirmed different (tree compiler); not reused (section 1.4). Default taken.
2. DuckDB: not reachable from aggregation; fail closed with tests (D5). Default taken.
3. A client REST `where` text that names `@__gql_tf_N` of a metric's typed filter in the same request would bind the caller's own value;
   no cross-principal value is reachable. No change (default taken); Phase 3 may decide otherwise.

## 5. Phase 3 security conditions

Verdict: APPROVED WITH CONDITIONS. Verified: `Autheris.Application` InternalsVisibleTo is limited to Tests.Unit, Tests.SqlCompilerGate and Benchmarks (csproj lines 38-40; Api/GraphQL/Extensions are not listed, and no assembly-level attribute adds more), so `internal init` is structurally unreachable from client-facing assemblies; STJ ignores non-public members. The client guard (`EnsureClientParameterNames`, line 249, prefix `__gql_` plus rls/pd names) stays unchanged and sees only `request.Parameters`; the channel is structural, not name-based. Residual: any future `InternalsVisibleTo` on Application widens the trust boundary.

- SEC-280-1: `GatewayParameters` stays `internal init`, no public accessor/ctor parameter; no InternalsVisibleTo may be added to Application for Api/GraphQL/Extensions/Infrastructure (test 7 pins this; extend it to assert the IVT list contains only the three known assemblies).
- SEC-280-2: `EnsureClientParameterNames` and the `__gql_`/caller-attribute checks in `BuildClientValues` run on client `Parameters` only, before and independent of the merge, for `PlannedTableRead` true and false; never exempt a name because a gateway map exists.
- SEC-280-3: Merge is fail-closed and complete: null/empty gateway map is a no-op; non-planned, collision (case-insensitive, `@` trimmed, client or gateway duplicate), list value, caller-attribute-reserved name, null/empty name, or a name not starting with `__gql_tf_` (TreeFilterClauseBuilder.ParameterPrefix) all throw. Gateway names must not match `__gql_rls_`, `__gql_mask_`, `__gql_vf_`, `__gql_caller_`.
- SEC-280-4: Policy (RLS), masking and caller-attribute slots are produced by the compiler with their own prefixes and must never be sourced from, or overridable by, the gateway map: bind lookup of a policy slot must come from the policy parameter set, never `values`; a gateway key equal to a policy-generated name fails closed (test below). Tenant filter, RLS, masking, audit and row limits are untouched.
- SEC-280-5: Values stay bound: gateway values never reach SQL text, logs, audit fields or exception messages (D6); no name/value in `SecurityException`/`InvalidOperationException` text; `Add` (not indexer) in `BuildClientValues` stays as second net. Typed CLR values only (scalars, `DateTime`, etc.); reject `byte[]`/non-scalar objects in the map.
- SEC-280-6: Open question 3, default taken (no change) with tightening: the aggregation `where` is a typed `TableFilterClause`, not raw text; the compiled predicate must come only from `TreeFilterClauseBuilder`. If any client-controlled text can reach `planned.Sql`, it must be rejected when it contains `__gql_` (case-insensitive); add the test below.
- SEC-280-7: Federated path (D5) rejects `GatewayParameters` before any work, with a generic message; no silent drop.
- SEC-280-8: Bound `GatewayParameters` count by the existing statement-parameter limit (DoS); exceeding it fails closed.

Mandatory tests (all `[Trait("Category","Security")]`), in addition to plan tests 1-9:
- `Issue280_GatewayParameterNamedLikePolicyOrMask_FailsClosed` (`__gql_rls_0`, `__gql_mask_x`, `__gql_vf_0`, `__gql_caller_a__0`, non-`__gql_tf_` name).
- `Issue280_PolicyParameterValue_NotOverriddenByGatewayParameter` (RLS table plus gateway map; RLS bound value is the policy value; masking unchanged).
- `Issue280_GatewayValues_NotInLogsAuditOrExceptionMessages` (sentinel value absent from command text, captured logs, audit fields, exception text).
- `Issue280_Aggregation_ClientWhereText_CannotReferenceGatewayMarker` (client-controlled text naming `@__gql_tf_0` is rejected or never reaches SQL).
- `Issue280_GatewayParameters_OverLimit_FailsClosed`.
- `Issue280_ApplicationInternalsVisibleTo_OnlyKnownTestAssemblies` (reflection over `InternalsVisibleToAttribute`).
