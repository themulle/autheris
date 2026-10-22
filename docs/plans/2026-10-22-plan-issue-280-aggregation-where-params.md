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

## 6. Addendum A (2026-10-22): text-level reserved guard rejects gateway placeholders

Finding (lead integration run, `GovernedAggregationWhereParams*` on PostgreSQL and SQL Server): `GovernedSqlExecutor` (l. 251-252) normalizes `@__gql_tf_0` to `__param___gql_tf_0`; `GovernedSqlRewriter.RewriteCoreAsync` (l. 171-175) then rejects every text containing `__gql_`/`__rls_ks` ("reserved gateway identifier"). Unit tests covered merge/normalize only.

Option chosen: executor-side alias (A). Rejected: (B) planner emits a non-reserved prefix. `TreeFilterClauseBuilder.ParameterPrefix` is shared with the tree path, `MergeGatewayParameters` validates on it, and a planner-chosen neutral name would be matched by plain `supplied.Contains` in client-style normalization, so client-shaped text could bind it. (C) relaxing the rewriter guard for planned reads breaks SEC-280-6 and SEC-RF107-07(e). Default taken: A. The text guard and `EnsureClientParameterNames` stay unchanged.

A1. `GovernedSqlExecutionService`: add `internal const string GatewayParameterAliasPrefix = "gw_tf_";` and `internal static string GatewayParameterAlias(string name)`, which returns `GatewayParameterAliasPrefix + name.TrimStart('@')[tfPrefix.Length..]`. The alias has no `__gql_`, `__rls_ks`, `__param_`, `p_rls_`, `rls_` or `p_pd_` prefix, so it can never equal a rewriter-generated RLS/mask/VF/caller/key-set name. Those names all use reserved prefixes.

A2. `MergeGatewayParameters`: after the existing prefix check, require the suffix after `__gql_tf_` to be 1-6 ASCII digits (else `InvalidOperationException`, no name in the message). Key the merged entry by `GatewayParameterAlias(trimmed)`. Fail closed (`SecurityException`, existing message) when the client parameters already contain the original name or the alias (`merged.ContainsKey(trimmed) || !merged.TryAdd(alias, value)`). Two gateway names that map to the same alias (`__gql_tf_0`/`__GQL_TF_0`) collide on `TryAdd` and fail closed.

A3. `NormalizeClientParameters(string sql, IReadOnlyDictionary<string, object?>? parameters, IReadOnlyDictionary<string, object?>? gatewayParameters, out IReadOnlyCollection<string> replacedNames)`. Add the parameter (default `null` keeps all other call sites) and build `gatewayNames` (trimmed, OrdinalIgnoreCase). Build `supplied` from `parameters` keys minus every alias, so text `@gw_tf_0` is never replaced and stays unbound. In the lexer branch, a name in `gatewayNames` appends `ClientParameterPlaceholderPrefix + GatewayParameterAlias(name)` and adds the alias to `names`. Otherwise the existing `supplied` branch runs. Quoting rules are unchanged: a `@__gql_tf_0` inside a literal stays in the text and the rewriter guard rejects it (fail closed). The value is never inlined; only the name changes.

A4. `GovernedSqlExecutor`: l. 252 passes `request.GatewayParameters` to `NormalizeClientParameters`. In `BuildClientValues` (l. 686-692), use `values.Add(GovernedSqlExecutionService.GatewayParameterAlias(name), value)`. `Add` stays the second collision net. Downstream binding is unchanged: `SqlAstBuilder` (l. 1060) turns `__param_gw_tf_N` into `ParameterReference("gw_tf_N")`, which is bound from `clientValues["gw_tf_N"]` like any client scalar, so PostgreSQL and SQL Server need no dialect work. The federated path (SEC-280-7) is unchanged.

A5. Tests. Phase 4 builds nothing; the lead runs them.
- Update in `tests/Autheris.Tests.Unit/Sql/GovernedSqlExecutionParameterSecurityTests.cs`. `Issue280_PlannedRead_GatewayParameters_Eq_BindsValue_NeverInText` expects `merged["gw_tf_0"]`, text containing `__param_gw_tf_0`, text containing no `__gql_` at all, and `names` containing `gw_tf_0`; the call passes `request.GatewayParameters`. `Issue280_PlannedRead_GatewayParameters_InAndBetween_Bind` uses keys `gw_tf_0..3`. `Issue280_GatewayAndClientNameCollision_FailsClosed` gets one case for the original name and one for the alias (`gw_tf_0`).
- New `Issue280_PlannedRead_GatewayParameters_PassRewriterTextGuard`. Run the real `GovernedSqlRewriter` (existing rewriter fixture in this file or `GovernedSqlRewriterTests`) on the normalized text of a planned read with `region = @__gql_tf_0`. Expect no `WebSqlPolicyException`, and expect a `ParameterReference` named `gw_tf_0` bound to the sentinel. This test reproduces the integration failure in Red.
- New `Issue280_ClientTextAlias_NotBoundToGatewayValue`. Planned read text `... = @gw_tf_0` plus gateway `@__gql_tf_0`: the text keeps `@gw_tf_0` (not replaced), and execution fails, never binding the sentinel.
- New `Issue280_GatewayParameterSuffixNotDigits_FailsClosed` (`__gql_tf_x`, `__gql_tf_`, `__gql_tf_0a`).
- New `Issue280_GatewayCaseVariantAliasCollision_FailsClosed`.
- Unchanged and must stay green: `SEC_280_6_ClientSqlText_ReferencingGatewayParameterNames_IsRejected_WhenPlannedRead` and `Issue280_PolicyParameterValue_NotOverriddenByGatewayParameter`. Integration `GovernedAggregationWhereParams*` (PostgreSQL, SQL Server) is the acceptance gate.

### 6.1 Phase 3 review of Addendum A (csharp-security-expert, 2026-10-22)

Verdict: APPROVED WITH CONDITIONS. The text guard and client name guard stay unchanged, values are never inlined, and SEC-280-6 holds because client text containing `__gql_`/`__rls_ks` is still rejected by the rewriter. Alias `gw_tf_N` cannot collide with rewriter names (all use reserved prefixes). Default taken for a legitimate WebSQL client parameter named `gw_tf_N`: allowed when the request has no gateway map (normal client binding), fail closed (generic `SecurityException`) when a gateway map is present and the client supplies the same name or alias. The client can never bind or override a gateway value.

- SEC-280-9: Alias and original-name comparison is `OrdinalIgnoreCase` everywhere (supplied-minus-alias in A3, `merged.ContainsKey` in A2); a client `GW_TF_0` with a gateway map must fail closed, not bind.
- SEC-280-10: With a gateway map present, any client parameter whose normalized key equals a `gw_tf_` alias fails closed before binding (no silent drop, no name or value in the message). Without a gateway map, behavior is unchanged: client `gw_tf_0` binds as ordinary client scalar.
- SEC-280-11: Client text containing `@__param_gw_tf_N` (pre-normalized form) must be rejected by `EnsureClientParameterNames`/reserved-prefix guard in both planned and ordinary reads; add a test that it never resolves to a gateway value.
- SEC-280-12: The alias is applied only to names validated by A2 (digits-only suffix, prefix match); `GatewayParameterAlias` throws on anything else; no gateway name reaches the rewriter unaliased. Aliasing happens only after the federated rejection (SEC-280-7) and does not change the SEC-280-8 count bound.
- SEC-280-13: Quoted-literal rule unchanged: `'@__gql_tf_0'` or `'@gw_tf_0'` inside a string/identifier literal is never replaced or bound; the former is rejected by the rewriter guard.

Mandatory tests (`[Trait("Category","Security")]`):
- `SEC_280_9_GatewayAliasCaseVariantClientParam_FailsClosed`
- `SEC_280_10_ClientParamNamedAlias_WithGatewayMap_FailsClosed` and `SEC_280_10_ClientParamNamedGwTf0_WithoutGatewayMap_BindsAsClientValue`
- `SEC_280_11_ClientTextPreNormalizedGatewayPlaceholder_IsRejected`
- `SEC_280_12_GatewayParameterAlias_RejectsInvalidNames`
- `SEC_280_13_GatewayPlaceholderInQuotedLiteral_NotReplaced_NotBound`
- `SEC_280_6_ClientSqlText_ReferencingGatewayParameterNames_IsRejected_WhenPlannedRead` stays green (re-run as regression); integration `GovernedAggregationWhereParams*` on PostgreSQL and SQL Server must pass.
