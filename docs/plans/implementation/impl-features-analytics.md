# Implementation Plan: Analytics Feature Roadmap (#28, #37, #38 Stage 2/3)

**Document ID:** `IMPL-FEATURES-ANALYTICS`
**Date:** 2026-10-14
**Parent plans:** [`../00-master-plan-overview.md`](../00-master-plan-overview.md), [`../features.md`](../features.md)
**Scope:** Issue #28 governed GraphQL aggregations, Issue #37 minimum group size / k-anonymity guard, Issue #38 dbt Semantic Layer Stage 2 and Stage 3.
**Work package prefix:** `WP-FEAT-NN`
**Baseline:** `main` at a4137626 (2026-10-14, after Waves 1-3). WP-FEAT-01 (REST OFFSET pushdown, `f-data-06` correction) and WP-FEAT-02 (masked-aggregate compiler guard, `AGGREGATION_ON_MASKED_COLUMN`) are delivered and removed from this plan.
**Blocking:** Gate 0 decision G-14 (open decision D-1): the owner must reconcile this plan with the bodies of issues #28, #37 and #38 before Wave 4 starts. Requirements are taken from `docs/plans/features.md`.

---

## 1. Current State of Open Items

| # | Item | Status on main | Evidence |
|---|---|---|---|
| V-3 | #28 GraphQL aggregations | **Open** | No aggregate field in the catalog-generated schema: per table only `where`/`orderBy`/`first`/`offset` (`CatalogGraphQlTypeModule.cs:229-234`). No `IGovernedAggregationService` in `src`. Building blocks now exist: `IGovernedTableReadPlanner` (WP-ARC-B-01), `GovernedQueryStages` (WP-ARC-B-03), `AnalyticsOptions` (WP-FEAT-01) and the compiler rejection of masked columns in aggregates and `GROUP BY` (WP-FEAT-02, `AstSecurityVisitor.MaskGuards.cs:150-155`). |
| V-4 | GraphQL table reads and `TreeSqlCompiler` | **Accepted exception (G-6)** | Root table queries run through `TreeSqlCompiler.Compile` (`GovernedTreeQueryService.cs:268`), guarded by `TreeVsCompilePolicyParityTests`. #28 must not extend `TreeSqlCompiler`; aggregates use `ISqlEngine.Compile` (see WP-FEAT-03). |
| V-6 | #37 minimum group size | **Open** | `DifferentialPrivacyEngine.PerturbAsync` has a cohort check and an atomic epsilon budget, but it is wired only to the governance endpoints (`GovernanceEndpoints.cs:255,283,337`) and is not on any query path. Daily budget is hard-coded (`DefaultDailyEpsilonBudget = 10.0`, `DifferentialPrivacyEngine.cs:23`). |
| V-7 | #38 Stage 1 MCP metric tools | **Open (registration gap)** | `list_metrics`/`describe_metric` are defined and unit-tested (`McpSemanticTools.cs`, `McpSemanticToolsTests.cs`) but not registered in the MCP server (no reference outside the test), although `f-gov-17` documents them as available. The semantic catalog is process-local memory and has no tenant dimension. |
| V-8 | #38 Stage 2 `query_metrics` | **Open** | No `query_metrics` symbol in `src`/`tests`. |
| V-9 | #38 Stage 3 headless BI | **Partially present** | Trino-style HTTP protocol endpoints exist (`WebSqlEndpoints.cs:75-91`). "Flight SQL" is HTTP/JSON+Arrow (`ArrowFlightSqlEndpoints.cs:26-64`), not gRPC; no `Grpc.*` or `Apache.Arrow.Flight` package in any `src/*/*.csproj`, although `f-data-04` claims a gRPC service. |

---

## 2. Design Principles for This Scope

1. **One new governed read service, not three.** #28 (GraphQL), #37 (guard) and #38 Stage 2 (metrics) all reduce to "build a grouped aggregate statement over one logical table, run it through the governed SQL pipeline, post-process groups". This plan introduces `IGovernedAggregationService` once (WP-FEAT-03) and lets GraphQL, MCP and the metric compiler be thin adapters.
2. **Compile through `ISqlEngine.Compile`, not through `TreeSqlCompiler`.** The aggregation service plans Trino-dialect SQL text through the shared `IGovernedTableReadPlanner` and submits it via `IGovernedSqlExecutionService.ExecuteQueryBufferedAsync`, exactly as `GovernedDataQueryService` does for REST. Row filters, tenant column, masking and function allowlists are applied by the AST compiler. No policy text is spliced by the caller.
3. **Fail closed.** Any column that is not `ColumnAccessLevel.Clear` in the caller's `TableAccessDecision` is rejected for aggregation, grouping, filtering and ordering (same anti-oracle rule as `GovernedTableReadPlanner.cs:134-180`). Unknown table, column or measure produces the same error as a denied one.
4. **New options are members of the existing `AnalyticsOptions`** (section `Analytics`, `AnalyticsOptionsValidator`, `ValidateOnStart()`), following the target pattern of Architecture T7. No new members on `GatewayOptions`.

---

## 3. Work Packages

### WP-FEAT-03: Governed aggregation service and GraphQL aggregate fields (#28)

- **Covers:** #28; `features.md` 2.1; provides the shared service for #37 and #38 Stage 2.
- **Goal:** Let GraphQL clients request `count`, `sum`, `avg`, `min`, `max` with optional `groupBy`, pushed down to the database, with RLS/consent/tenant filters applied before aggregation and masked/denied columns rejected.
- **Concrete design:**
  - **Domain/Application model** (new, `src/Autheris.Application/Analytics/`):
    - `AggregationQuery(TableIdentifier Table, IReadOnlyList<string> GroupBy, IReadOnlyList<AggregateSelection> Aggregates, TableFilterClause? Where, IReadOnlyList<TableOrderBy>? OrderBy, int Limit, int Offset, string Transport)`.
    - `AggregateSelection(AggregateFunction Function, string? Column, string Alias)`; `enum AggregateFunction { Count, Sum, Avg, Min, Max }`. `COUNT(*)` has `Column == null`. `COUNT(DISTINCT x)` is deliberately not offered in v1 (it leaks per-value uniqueness and complicates #37).
    - `AggregationResult(IReadOnlyList<AggregationColumn> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Groups, AggregationMetadata Metadata)`, with `AggregationMetadata(bool Truncated, int? SuppressedGroupsCount, GroupSizeGuardMode GuardMode)` (the two guard members are filled by WP-FEAT-04).
    - `IGovernedAggregationService.ExecuteAsync(AggregationQuery, RequestContext, CancellationToken)`; implementation `GovernedAggregationService` built on the shared stages delivered by WP-ARC-B-01/B-03: authorization and audit through `GovernedQueryStages` (`src/Autheris.Application/Policy/GovernedQueryStages.cs`), column authorization and anti-oracle checks through `IGovernedTableReadPlanner` (`src/Autheris.Application/Data/Interfaces/IGovernedTableReadPlanner.cs`; extend it with a `PlanAggregate` method next to `Plan`/`PlanCount` instead of a second SQL builder), execution through `IGovernedSqlExecutionService` (or the federated service for cross-source tables) as in `GovernedDataQueryService.cs:168-210`.
  - **SQL shape:** `SELECT g1, g2, COUNT(*) AS "count", SUM(c) AS "sum_c", ... FROM t [WHERE <translated filter>] GROUP BY g1, g2 ORDER BY ... OFFSET n ROWS LIMIT m`. Row policy and tenant predicate are injected into the WHERE by the AST compiler, i.e. before grouping.
  - **Validation (fail closed):** every `GroupBy`, `Aggregate.Column`, filter and order column must exist and be `ColumnAccessLevel.Clear` (else `AGGREGATION_ON_MASKED_COLUMN` for `Mask`, generic not-found style rejection for `Deny`, mirroring `GovernedTableReadPlanner.cs:134-180`); `Sum`/`Avg` only on numeric catalog types; `Min`/`Max` on numeric, date/time and string; at most `Analytics:Aggregation:MaxGroupByColumns` (default 4) and `MaxAggregates` (default 16); `Limit <= Analytics:Aggregation:MaxGroups` (default 1000) and a truncation fail-closed rule (if the SQL layer truncates, the call errors rather than returning a partial group list, because partial groups corrupt #37 counts).
  - **GraphQL schema** (`src/Autheris.GraphQL/Catalog/CatalogGraphQlTypeModule.cs:229-234` table field + `CatalogSchemaModel.cs`): per table add
    - enum `<T>_group_by` (column field names),
    - object `<T>_aggregate` with fields `group: <T>_group` (object whose fields are the group-by columns' types, nullable), `count: Long!`, `sum: <T>_sum`, `avg: <T>_avg`, `min: <T>_min`, `max: <T>_max` (the latter four are objects with one field per eligible column; numeric columns for sum/avg, comparable columns for min/max),
    - root field `<queryField>_aggregate(where: <T>_filter, groupBy: [<T>_group_by!], orderBy: ..., first: Int = 100, offset: Int = 0): [<T>_aggregate!]!` registered in the `Query` extension next to the existing table field.
    - Resolver `ResolveAggregateAsync` builds an `AggregationQuery` from the `IResolverContext` (reuse the existing argument-to-filter conversion of `GraphQlTreeBuilder`) and calls `IGovernedAggregationService`. The resolver reads only selection-set fields to build the aggregate list (no `SELECT *`).
  - **Schema generation vs per-user access:** the schema is shared (built once from the catalog fingerprint), access is per user, so ineligible columns are rejected at runtime with a stable error, not hidden in the schema. Introspection exposure is governed by the existing `EnableIntrospection` and `MaskedFieldsMiddleware`.
  - **Cost control:** register cost in `QueryCostAnalyzerRule`/`CostAndQuotaMiddleware` (`src/Autheris.GraphQL/Interceptors`) with a fixed higher weight per aggregate field; `MaxRootFieldsPerOperation` already limits alias amplification (SEC M-13).
  - **Not in scope:** aggregates on nested relation fields (only root `_aggregate`), `having`, window functions, `COUNT(DISTINCT)`, cross-source aggregation in v1 (cross-source tables answer `GatewayNotImplementedException`; consistent with the cross-source restrictions of `GatewayExecutionService`).
- **Affected files:** new `src/Autheris.Application/Analytics/*` (service, query model), `src/Autheris.Application/Data/Interfaces/IGovernedTableReadPlanner.cs` + `Services/GovernedTableReadPlanner.cs` (aggregate planning), `src/Autheris.Domain/Options/AnalyticsOptions.cs` + `src/Autheris.Application/Data/Services/AnalyticsOptionsValidator.cs` (existing, add members), `src/Autheris.Api/Extensions/DependencyInjection/DataApiServiceExtensions.cs` (registration), `src/Autheris.GraphQL/Catalog/CatalogGraphQlTypeModule.cs`, `CatalogSchemaModel.cs`, `GraphQlTreeBuilder.cs` (filter reuse only), `Interceptors/QueryCostAnalyzerRule.cs`.
- **Migration / compatibility:** Additive schema change. Catalog fingerprint changes on upgrade, which triggers the existing schema rebuild (R-GQL-12). Persisted-queries-only deployments must add trusted documents to use the new fields. No config required; feature enabled by `Analytics:Aggregation:Enabled` (default true) so operators can switch it off.
- **Security considerations:** Aggregation never bypasses the access decision: `decision.IsAllowed` false throws `GatewayForbiddenException` with an audited DENY (through `GovernedQueryStages` and `IAuditWriter`); audit entry `AGGREGATE_QUERY` records table, group-by columns, aggregate list and filter shape (no values); tenant isolation by the compiler-injected tenant predicate (`TableMetadata.RequireTenantColumnOrThrow` semantics preserved). Aggregates over `Deny` columns are rejected with the same message as unknown columns (no existence oracle). Security Wave C item SR-L-12 (subgraph masking) is orthogonal: aggregate responses are produced locally; federated subgraph aggregates are out of scope.
- **TDD plan:**
  - `tests/Autheris.Tests.Unit/Analytics/GovernedAggregationServiceTests.cs`: Red first `ExecuteAsync_GroupedSum_AppliesRowPolicyBeforeAggregation` (captured SQL goes through `IGovernedSqlExecutionService`, contains `GROUP BY`, contains no policy text), `..._AggregateOnMaskedColumn_ThrowsAggregationOnMaskedColumn`, `..._GroupByDeniedColumn_RejectedAsUnknown`, `..._SumOnStringColumn_Rejected`, `..._TruncatedResult_FailsClosed`, `..._TooManyGroupByColumns_Rejected`, `..._CrossSourceTable_NotSupported`.
  - `tests/Autheris.Tests.Unit/GraphQl/CatalogAggregateSchemaTests.cs`: schema contains `<T>_aggregate` only with eligible columns; query with `sum { amount }` resolves via the service; alias-amplification cost.
  - `tests/Autheris.Tests.Integration` (SQLite): row-level security test with two tenants proving the aggregate only counts the caller's tenant rows; consent-filtered rows excluded.
  - Architecture test: `Autheris.GraphQL` calls `IGovernedAggregationService`, never `IGovernedSqlExecutionService` directly (`tests/Autheris.Tests.Architecture`).
- **Acceptance criteria:** (from `features.md`) grouped aggregations enforce RLS before computing aggregates; aggregation over masked columns rejected with `AGGREGATION_ON_MASKED_COLUMN`; plus: no `TreeSqlCompiler` use, no raw-row transfer to the gateway, response bounded by `MaxGroups`.
- **Effort:** L (5-6 days). **Risk:** Medium (HotChocolate dynamic schema typing, numeric overflow semantics of SUM per dialect: cast to decimal/bigint in the builder via a per-dialect-neutral `CAST(... AS DECIMAL(38,6))` for integral columns; document the result type).

---

### WP-FEAT-04: Minimum group size / k-anonymity guard (#37)

- **Covers:** #37; `features.md` 2.2 (suppression, `suppressedGroupsCount`).
- **Goal:** Guarantee that no aggregate result exposes a group derived from fewer than `k` rows, expose `suppressedGroupsCount`, and cut the cheapest differencing channels. This WP deliberately does not include noise addition (see WP-FEAT-05).
- **Threat model (differencing attacks):**

| Attack | Description | Treatment |
|---|---|---|
| T-1 Small group | `groupBy department` yields a group of 1 or 2 people; the average salary is that person's salary. | **In scope.** Groups with `COUNT(*) < k` are suppressed (or merged, see D-3). |
| T-2 Narrow filter | `where: {name: {eq: "Alice"}}` on an ungrouped aggregate. | **In scope.** The guard evaluates the group size of the filtered result, including the single global group of an ungrouped aggregate. A global aggregate with `COUNT(*) < k` is suppressed. |
| T-3 Complement / tracker | `SUM` over all minus `SUM` over all-except-Alice (both groups >= k). | **Partially in scope.** Mitigations: (a) reject `where` on identifier-like columns (primary key, unique columns, columns classified as PII identifiers via F-GOV-13) in aggregate queries; (b) reject negated filters (`neq`, `nin`, `not`) on aggregate queries when `Analytics:Privacy:RestrictNegatedFilters` is true (default true); (c) audit and rate-limit aggregate queries per client (existing cost/quota pipeline). Full defence requires noise (WP-FEAT-05). |
| T-4 Complementary suppression | Total minus visible groups reveals the suppressed group. | **In scope for the response only**: when any group is suppressed, grand totals (`ungrouped aggregate on the same filter`) are not returned within the same response; `suppressedGroupsCount` may be disabled by `Analytics:Privacy:ExposeSuppressedGroupsCount` (default true as the issue requires it; see D-4). Cross-query difference of two requests remains T-3. |
| T-5 Min/max disclosure | `MIN`/`MAX` of a group with k rows reveals one individual's value. | **Out of scope for v1** (documented residual risk); mitigated by a configurable `MinMaxRequiresGroupSize` multiplier is not planned. Open decision D-5. |
| T-6 Collusion across clients/accounts | Several clients pooled budgets. | **Out of scope.** Needs identity-level correlation outside this feature. |
| T-7 Auxiliary data / join with raw reads | Attacker combines aggregates with row-level data they are allowed to read. | **Out of scope.** The guard protects aggregate channels only; row-level access is governed by RLS/consent/masking. |
| T-8 Timing / error side channels | Distinguishing suppressed groups from empty groups by latency or error text. | **Partially in scope**: suppression produces no error and the same envelope; timing is not hardened. |
| T-9 Formal differential privacy guarantee | (epsilon, delta) composition proof. | **Out of scope.** WP-FEAT-05 provides a best-effort noise layer with simple additive budgeting. |

- **Concrete design:**
  - `AnalyticsOptions` (exists since WP-FEAT-01: `src/Autheris.Domain/Options/AnalyticsOptions.cs`, section `Analytics`, validator `src/Autheris.Application/Data/Services/AnalyticsOptionsValidator.cs`, registered in `DataApiServiceExtensions.cs:23-26`) gains: `Privacy.MinimumGroupSize` (default 5, range 2-10 000; setting it to 0/1 requires `Privacy.AllowDisabled = true` and logs a startup warning, fail-closed otherwise), `Privacy.Datasets` (list of `{ Table, MinimumGroupSize, MeasureOverrides }` keyed by `domain.schema.table`, may only raise the global value, never lower it below the global floor), `Privacy.SuppressionMode` (`Suppress` default | `MergeToOther`), `Privacy.RestrictNegatedFilters`, `Privacy.ExposeSuppressedGroupsCount`.
  - `IGroupSizeGuard` (`src/Autheris.Application/Analytics/GroupSizeGuard.cs`): `Apply(AggregationResult raw, int k) -> GuardedResult`. Always adds an internal `COUNT(*) AS __n` selection to the statement built by `GovernedAggregationService`. The guard runs in the gateway after the governed query returns: groups with `__n < k` are removed and counted, `__n` is stripped from the response. Post-filtering in the gateway (instead of `HAVING COUNT(*) >= :k`) is chosen because it yields an exact `suppressedGroupsCount` in one round trip and enables the merge mode; the small groups only transit the trusted gateway process. A SQL-side `HAVING` is not used because it would hide the suppressed count and could not feed merging.
  - Fail-closed rules: if the result is truncated by the row cap the call fails (a missing group would make the suppressed count and merged bucket wrong); if `__n` is missing or null for any row the whole result is rejected; if the guard cannot be resolved the aggregation service refuses to execute (no "guard optional" constructor overload; follow the audit-contract lesson of Architecture T2).
  - `MergeToOther` (optional mode, off by default): suppressed groups are summed into one `Other` bucket only if the merged bucket itself has `n >= k`; otherwise it is dropped. Only `count` and `sum` merge exactly; `avg` is recomputed from merged `sum`/`count` (the builder adds hidden `SUM`/`COUNT` columns for `avg`), `min`/`max` merge by min/max. Group keys of `Other` are the literal string `Other` for string keys and `null` otherwise (documented).
  - GraphQL: `<T>_aggregate` list response gets a sibling metadata field. Because the root field returns a list, introduce a wrapper root field `<queryField>_aggregate` returning `<T>_aggregate_result { groups: [<T>_aggregate!]!, suppressedGroupsCount: Int, minimumGroupSize: Int! , truncated: Boolean! }`. This changes the WP-FEAT-03 field type, so WP-FEAT-03 must ship the wrapper form from the start (agreed here to avoid a later breaking change). REST/MCP/metrics adapters surface the same metadata.
  - Per-dataset thresholds: options-based for v1 (no DB migration; persistence can later use the versioned governance migrations of WP-ARC-B-12). dbt `meta.minimum_group_size` on a model/metric is read by WP-FEAT-06 and may only raise the floor.
- **Affected files:** `src/Autheris.Domain/Options/AnalyticsOptions.cs` + `AnalyticsOptionsValidator.cs`; `src/Autheris.Application/Analytics/GovernedAggregationService.cs` (guard hook), `GroupSizeGuard.cs`; `src/Autheris.GraphQL/Catalog/CatalogGraphQlTypeModule.cs` (result wrapper); `src/Autheris.Api/Extensions/DependencyInjection/DataApiServiceExtensions.cs`; docs `docs/features/` (Phase 6).
- **Migration / compatibility:** Default k = 5 means existing aggregate-style WebSQL queries are unaffected (guard applies to the new aggregation service only, not to free-form SQL; G-12). Operators with legitimately small cohorts configure per-dataset values >= 2. Disabling needs two explicit settings.
- **Security considerations:** Guard cannot be bypassed by choosing aggregate shape: every statement built by the service carries `__n`; clients cannot request a column named `__n` (identifier reserved like the `autheris_` prefix). Group keys are protected as well (a suppressed group's key is never returned). Suppression happens before serialization, masking, audit payload creation (audit stores counts and k, not suppressed keys), and caching. Aggregation results must not enter the semantic cache keyed without the k value (cache key includes k and mode).
- **TDD plan** (`tests/Autheris.Tests.Unit/Analytics/GroupSizeGuardTests.cs`, `AnalyticsOptionsValidatorTests.cs`): Red first `Apply_GroupBelowK_IsRemovedAndCounted`; then `Apply_UngroupedAggregateBelowK_IsSuppressed`, `Apply_AllGroupsSuppressed_ReturnsEmptyWithCount`, `Apply_MergeToOther_MergedBucketBelowK_Dropped`, `Apply_MergeToOther_AvgRecomputedFromSumAndCount`, `Apply_MissingCountColumn_FailsClosed`, `Apply_TruncatedInput_FailsClosed`, `Validator_MinimumGroupSizeBelowTwo_WithoutAllowDisabled_Fails`, `Service_NegatedFilterOnAggregate_RejectedWhenRestricted`, `Service_FilterOnPrimaryKey_Rejected`, `Service_PerDatasetThresholdBelowGlobal_Ignored`. Integration (SQLite): department with 1, 4 and 20 employees, `k=5` returns only the 20 group and `suppressedGroupsCount = 2`.
- **Acceptance criteria:** (from `features.md`) groups below the threshold are suppressed or merged; response metadata contains `suppressedGroupsCount`; plus all rows in a response have `n >= k`, validated options fail at startup, T-1/T-2/T-3(a,b)/T-4 covered by tests, residual risks T-5..T-9 stated in the feature doc.
- **Effort:** L (5 days). **Risk:** Medium (merge-mode arithmetic, k-threshold UX).

---

### WP-FEAT-05: Differencing defence with differential-privacy noise (#37, optional phase)

- **Covers:** `features.md` 2.2 (optional differencing defence), threat T-3 residual.
- **Goal:** Wire the existing `IDifferentialPrivacyEngine` into the aggregation service as an opt-in noise layer with a per-client epsilon budget, so that the complement/tracker attack becomes statistically unreliable.
- **Concrete design:**
  - `Analytics:Privacy:Noise:Enabled` (default false), `Epsilon` per query (default 0.5), `DailyBudgetEpsilon` (replaces the hard-coded 10.0 in `DifferentialPrivacyEngine.cs:23`; engine takes `IOptions<AnalyticsOptions>`), `MeasureBounds` (per measure `{Min, Max}` clamp used as sensitivity for `sum`, `avg`; `count` has sensitivity 1).
  - `GovernedAggregationService` calls `PerturbAsync` once per released numeric cell after the group-size guard; budget consumption per query is `epsilon x number of released cells` (simple sequential composition: documented conservative accounting) or, with a configured `Analytics:Privacy:Noise:Composition = PerQuery`, a single epsilon per query with Laplace scale divided accordingly (open decision D-6).
  - Budget exhaustion raises `PrivacyBudgetExhaustedException` mapped to HTTP 429 / GraphQL error code `PRIVACY_BUDGET_EXHAUSTED`; cluster store failure fails closed (the engine already does, `DifferentialPrivacyEngine.cs` header comment, AR-04).
  - Add a clamping step (values outside `MeasureBounds` are rejected at the SQL layer by wrapping the measure in `LEAST/GREATEST` or by catalog-declared bounds only; reject measures without bounds when noise is enabled).
  - Budget key is tenant-bound client id (reuse `ResolveTenantBoundClientId` from `GovernanceEndpoints.cs`).
- **Affected files:** `src/Autheris.Application/Governance/Services/DifferentialPrivacyEngine.cs` (options-driven budget), `IDifferentialPrivacyEngine.cs` (batch overload `PerturbManyAsync` to avoid N atomic Redis round trips), `GovernedAggregationService.cs`, `AnalyticsOptions.cs`.
- **Migration / compatibility:** Off by default; no change for existing governance endpoints except that the daily budget becomes configurable (default 10.0 unchanged).
- **Security considerations:** Noise keeps using the cryptographically secure RNG (`RandomNumberGenerator`, `DifferentialPrivacyEngine.cs:201`); noisy results must never be cached or served twice for the same request key (re-noise per request would permit averaging attacks, so identical queries within a window return the cached noisy value: decision D-6); suppressed groups consume no budget.
- **TDD plan** (`tests/Autheris.Tests.Unit/DifferentialPrivacyTests.cs` extended, `Analytics/NoisyAggregationTests.cs`): Red first `Execute_NoiseEnabled_ConsumesBudgetPerReleasedCell`; `Execute_BudgetExhausted_Throws_429Mapping`; `Execute_NoBoundsForMeasure_RejectedWhenNoiseEnabled`; `Execute_ClusterStateUnavailable_FailsClosed`; statistical test with fixed seed seam (`TimeProvider`/`IRandomSource`) verifying mean error ~ 0.
- **Acceptance criteria:** noisy aggregates, exhausted budget stops further answers for that client until the daily reset, audit event per consumption.
- **Effort:** L (4-5 days). **Risk:** High (statistical correctness, composition accounting); only after WP-FEAT-04 and only on compliance demand (G-12).

---

### WP-FEAT-06: `query_metrics` for GraphQL and MCP (#38 Stage 2)

- **Covers:** #38 Stage 2; V-7 (unregistered MCP metric tools), V-8.
- **Goal:** Compile a semantic query (`metrics`, `groupBy`, `where`, `orderBy`, `limit`) from the dbt semantic catalog into an `AggregationQuery` and run it through `IGovernedAggregationService`, so metrics inherit tenant filtering, consent, masking rejection, group-size guard and audit.
- **Concrete design:**
  - `IMetricQueryCompiler` (`src/Autheris.Application/Dbt/Services/MetricQueryCompiler.cs`): resolves each metric via `ISemanticCatalogService.GetMetricAsync`, maps `DbtSemanticModel.NodeRelation` to a `TableIdentifier`, and translates:
    - `simple` metrics: `DbtMeasure.Aggregation` (`sum|count|avg|min|max`; `count_distinct` rejected in v1) over `DbtMeasure.Expr` or the measure name, `Expr` restricted to a plain catalog column (an expression grammar is a v2 item and would need AST-level compilation; free SQL text from a manifest is never spliced).
    - `ratio` and `derived` metrics: arithmetic over already-compiled simple measures computed in the gateway from the returned group rows (numerator and denominator selected as hidden aggregates; division by zero yields null). `cumulative`, `conversion` and cross-model joins are out of scope for Stage 2 (rejected with a typed `METRIC_TYPE_NOT_SUPPORTED`).
    - `groupBy` dimensions must belong to the metric's semantic model; `time` dimensions accept a grain (`day|week|month|quarter|year`) implemented as the compiler's `DATE_TRUNC`/`DateFunctionExpression` equivalent through the Trino-form text (function allowlist verified per dialect).
    - `Metric.Filter` (Jinja `{{ Dimension('x') }}` form) parsed by a strict allowlist parser into `TableFilterClause`; unparseable filter fails closed (metric not queryable), it is never ignored.
    - Dimension, measure and metric names are validated against the catalog; each resolves to a physical column that must pass the access decision (WP-FEAT-03 validation); a measure over a masked column is rejected, not silently nulled.
  - **GraphQL:** static root field `queryMetrics(metrics: [String!]!, groupBy: [String!], where: [MetricFilter!], orderBy: [MetricOrder!], first: Int = 100)` in `src/Autheris.GraphQL/Types/QueryTypes.cs`, returning `MetricQueryResult { columns, rows, suppressedGroupsCount, minimumGroupSize, truncated }`. Rows are JSON-typed values (dynamic columns).
  - **MCP:** add `query_metrics` to `McpSemanticTools.Definitions()` (`src/Autheris.Application/Mcp/Services/McpSemanticTools.cs`), and **register the whole semantic tool set** (`list_metrics`, `describe_metric`, `query_metrics`) in the MCP tool dispatcher (`McpToolExecutionHandler.cs` / `GatewayMcpServer.cs` / `GatewayMcpServiceExtensions.cs`), which is currently missing (V-7). The tool must go through `AiDataGuardrailService` (tables resolved from the compiled `AggregationQuery`, not from a GraphQL string; `McpGraphQlTableResolver` is not used) so MCP data-guardrail, HITL and rate limits apply.
  - **Catalog robustness (required for tenant isolation):** `InMemorySemanticCatalog` is process-local and tenant-agnostic. Stage 2 adds a tenant argument to `ISemanticCatalogService` queries (`tenant` filter on manifests registered per tenant) and an `ISemanticCatalogStore` persistence port (governance DB table `semantic_manifests` as a new versioned migration, runner delivered by WP-ARC-B-12; G-12 decided to persist the catalog).
  - Metric-level k override from `meta.minimum_group_size` (may only raise the floor).
- **Affected files:** new `src/Autheris.Application/Dbt/Services/MetricQueryCompiler.cs` and `IMetricQueryCompiler.cs`; `src/Autheris.Application/Dbt/Interfaces/ISemanticCatalogService.cs`, `src/Autheris.Application/Dbt/Services/InMemorySemanticCatalog.cs`; `src/Autheris.Application/Mcp/Services/McpSemanticTools.cs`, `McpToolExecutionHandler.cs`, `AiDataGuardrailService.cs`; `src/Autheris.Api/Mcp/GatewayMcpServer.cs`, `Extensions/DependencyInjection/GatewayMcpServiceExtensions.cs`; `src/Autheris.GraphQL/Types/QueryTypes.cs`; `src/Autheris.Extensions/Dbt/DbtServiceCollectionExtensions.cs`; `src/Autheris.Api/Endpoints/DbtEndpoints.cs` (optional REST `POST /api/v1/metrics/query`).
- **Migration / compatibility:** Additive. Registering the previously unregistered MCP metric tools makes them visible to MCP clients (the documented behaviour of `f-gov-17`); tenants with no manifest see an empty catalog. `ISemanticCatalogService` signature change is internal (one implementation).
- **Security considerations:** Metric names must not be an existence oracle across tenants (unknown and not-visible metric produce the same response); a metric whose underlying table is denied to the caller behaves as not found (`f-gov-17` anti-oracle rule). Manifest content is untrusted input: identifiers are matched against the live catalog, no manifest string reaches the SQL text unvalidated. Every query is audited with metric names and dimension list (event type `METRIC_QUERY`).
- **TDD plan:** `tests/Autheris.Tests.Unit/Dbt/MetricQueryCompilerTests.cs` (Red first `Compile_SimpleSumMetric_ProducesGroupedAggregationQuery`; `Compile_DimensionOfOtherModel_Rejected`; `Compile_ManifestExprWithSql_Rejected`; `Compile_RatioMetric_ComputesFromHiddenAggregates`; `Compile_UnsupportedCumulative_Rejected`; `Compile_MetricFilterUnparseable_FailsClosed`), `tests/Autheris.Tests.Unit/Mcp/McpSemanticToolsTests.cs` extended (`query_metrics` tool executes through the aggregation service; all three tools appear in `tools/list`: this is the red test for V-7), `tests/Autheris.Tests.Unit/GraphQl/QueryMetricsTests.cs`, tenant isolation test `SemanticCatalogTenantTests`. Integration (SQLite) with a two-tenant manifest and a masked measure column.
- **Acceptance criteria:** semantic metrics queryable via GraphQL and MCP with full tenant isolation and audit logging (from `features.md`); group-size guard applied; masked-column measure rejected; `list_metrics`/`describe_metric` reachable over MCP.
- **Effort:** XL (7-9 days, can be split: 06a MCP registration fix + catalog tenancy (S-M), 06b compiler + GraphQL/MCP `query_metrics` (L)). **Risk:** High (semantic subset boundaries, persistence decision).

---

### WP-FEAT-07: Headless BI connectivity (#38 Stage 3)

- **Covers:** #38 Stage 3; V-9.
- **Goal:** Make the existing Trino-protocol and Arrow endpoints usable by real BI/driver clients and add a real gRPC Arrow Flight SQL server, with governed access to semantic models. This WP is decision-gated: the target BI clients must be named before Stage 3 (G-12, D-8).
- **Concrete design (phased):**
  - **07a Trino protocol conformance (M):** integration tests using the official Trino Python client and Trino JDBC (Testcontainers, `tests/Autheris.Tests.Integration`) against `/v1/statement` (`nextUri` paging, `columns` metadata types, cancel, `SHOW CATALOGS/SCHEMAS/TABLES`, `information_schema.columns` served from the governed catalog, `X-Trino-*` headers incl. `X-Trino-User` ignored in favour of the authenticated principal). Fix gaps found. All statements keep using `SqlTransports.Trino` and the existing compile path.
  - **07b Semantic models as governed virtual tables (M):** expose each dbt semantic model as a catalog table/view (`semantic.<model>`) whose columns are dimensions and measures (measures as pre-aggregated columns are not exposed; BI tools receive dimensions + raw measure columns or the `query_metrics` result through a `TABLE(query_metrics(...))`-style function is a later option). Recommended v1: register `semantic_models` as read-only catalog views so Tableau/Superset/PowerBI SQL goes through the normal governed SQL path; metric calculation stays in `query_metrics` (WP-FEAT-06). Group-size guard does not apply to free SQL (G-12).
  - **07c gRPC Arrow Flight SQL (L-XL):** add `Apache.Arrow.Flight.AspNetCore` / `Grpc.AspNetCore` packages, a `FlightSqlGrpcService` mapped on Kestrel HTTP/2 (port per `ArrowFlightSql` options), implementing `GetFlightInfo`, `DoGet`, `GetCatalogs/Schemas/Tables`, `DoAction` prepared-statement subset; authentication via gRPC metadata Bearer through the existing authentication pipeline; execution through `IArrowFlightSqlService` -> `IGovernedSqlExecutionService` with `SqlTransports.FlightSql`. Correct `f-data-04` (it currently describes gRPC that does not exist) in Phase 6.
  - New options go to `Analytics:BiConnectivity` as members of `AnalyticsOptions` (T7 pattern).
- **Affected files:** `src/Autheris.Api/Endpoints/WebSqlEndpoints.cs`, `ArrowFlightSqlEndpoints.cs`, new `src/Autheris.Api/Grpc/FlightSqlGrpcService.cs`, `src/Autheris.Infrastructure/Arrow/*`, `src/Autheris.Api/Autheris.Api.csproj` (package refs), `docs/features/f-data-04-arrow-flight-sql.md`.
- **Migration / compatibility:** New port and packages (supply-chain review: packages must be on the Directory lock files, `packages.lock.json`); no change to existing HTTP endpoints. Kestrel HTTP/2 prior-knowledge listener must be opt-in.
- **Security considerations:** gRPC path must apply the same authentication, rate limits, audit and tenant resolution as HTTP (`AccessAuditMiddleware`, `RateLimitingMiddleware` do not run on a separate listener unless wired); TLS required on the Flight port outside development; size limits on messages (`MaxReceiveMessageSize`); cross-source transport name `FlightSql` stays opt-in per `WebSql:CrossSource:AllowedTransports`.
- **TDD plan:** 07a conformance tests are the red tests (`TrinoClientConformanceTests`: `Python/JDBC client runs SELECT with paging and receives masked values`); 07c: `FlightSqlGrpcServiceTests` using an in-process `GrpcChannel` (unauthenticated call rejected, governed query returns masked Arrow batches, cancelled stream disposes reader).
- **Acceptance criteria:** a stock Trino JDBC/Python client and a stock `pyarrow.flight` Flight SQL client execute a governed query with RLS and masking identical to WebSQL; audit entries carry the transport names.
- **Effort:** XL (07a M + 07b M + 07c L-XL, 10+ days total). **Risk:** High (protocol surface, packages, HA/port exposure). Recommendation: implement 07a first; defer 07c until a concrete BI client requires gRPC Flight SQL.

---

## 4. Dependencies

### Between work packages
- WP-FEAT-03 ships the result wrapper `<T>_aggregate_result` agreed in WP-FEAT-04 (design contract, so the two WPs are co-designed; implementation of the guard follows).
- WP-FEAT-04 depends on WP-FEAT-03 (service hook). WP-FEAT-05 depends on WP-FEAT-04.
- WP-FEAT-06b depends on WP-FEAT-03 and WP-FEAT-04 (guard is mandatory for metrics). WP-FEAT-06a is independent.
- WP-FEAT-07 depends on WP-FEAT-06 only for 07b; 07a and 07c are independent.
- `AnalyticsOptions` and `AnalyticsOptionsValidator` are shared by WP-FEAT-03/04/05/07 (merge-conflict hot files, serialize).

### On neighbouring scopes
- **Architecture R1 (T1):** GraphQL root table reads keep `TreeSqlCompiler` (G-6), aggregates use `ISqlEngine.Compile`; the parity test keeps both paths aligned. R3 (dialect ports, WP-ARC-B-09/B-10) moves executors, not the service interfaces named above.
- **Architecture R4 (T7 options split, WP-ARC-C-01):** if C-01 chooses another section naming scheme, only the binding line of `AnalyticsOptions` changes.
- **SQL compiler gate:** the masked-aggregation tightening of WP-FEAT-02 is recorded as `MaskedAggregationException` entries in `tests/Autheris.Tests.SqlCompilerGate/Corpus/accepted-differences.json`; reviewed and approved on 2026-10-15 ([`PLAN-SQLGATE-CUTOVER-03`](../2026-10-15-plan-sqlgate-cutover-and-accepted-differences.md)).
- **Security SR-L-12 (subgraph masking):** aggregate fields are not federated in this scope; if Fusion later forwards `<T>_aggregate`, `SubgraphResultMaskingMiddleware` must understand the aggregate result shape.
- **Persistence:** the persistent semantic catalog (WP-FEAT-06) and future persisted per-dataset thresholds use the versioned governance migrations (WP-ARC-B-12, delivered). DP budgets use `IDistributedClusterStateProvider` with the absent-vs-unavailable semantics of WP-ARC-A-09.

---

## 5. Recommended Sequence

| Phase | Work packages | Parallelism and rationale |
|---|---|---|
| 0 | G-14 owner review | Blocks all phases below. |
| 1 | WP-FEAT-03 (aggregation service + GraphQL), then WP-FEAT-04 (guard) | Serialize: share `GovernedAggregationService`, `AnalyticsOptions`, `CatalogGraphQlTypeModule`. A second developer can build the pure `GroupSizeGuard`/options/validator work against the agreed interface. Do not release #28 to production without WP-FEAT-04; gate with `Analytics:Aggregation:Enabled`. |
| 2 | WP-FEAT-06a (MCP registration fix + catalog tenancy), in parallel with WP-FEAT-04 | Touches `Autheris.Application/Dbt` and MCP registration only. Closes V-7. |
| 3 | WP-FEAT-06b (`query_metrics`) | After WP-FEAT-03/04; hot files `QueryTypes.cs`, `McpSemanticTools.cs`. |
| 4 | WP-FEAT-07a (Trino conformance), WP-FEAT-05 (DP noise, only on compliance demand) | Independent of each other. |
| 5 | WP-FEAT-07b/07c | Decision-gated (D-8). |

Overall hot files (serialize edits): `src/Autheris.Domain/Options/AnalyticsOptions.cs`, `src/Autheris.GraphQL/Catalog/CatalogGraphQlTypeModule.cs`, `src/Autheris.Application/Analytics/GovernedAggregationService.cs`, `src/Autheris.Application/Data/Services/GovernedTableReadPlanner.cs`, `src/Autheris.Api/Extensions/DependencyInjection/DataApiServiceExtensions.cs`.

Phase 6 (documentation) per CLAUDE.md lifecycle: add `docs/features/f-analytics-01-governed-aggregations.md`, `f-analytics-02-group-size-guard.md`, `f-analytics-03-semantic-metric-queries.md`, correct `f-data-04` and `f-gov-17`, register in `docs/features/README.md`, and update `docs/plans/features.md` and the master plan.

---

## 6. Open Decisions

Gate 0 (G-12) settled: no k-guard on free-form SQL in v1; differential privacy only on compliance demand; persist the semantic catalog (now possible with WP-ARC-B-12); name the target BI clients before Stage 3. The remaining scope decisions:

| ID | Question | Recommendation |
|---|---|---|
| D-1 (G-14) | Do the bodies of issues #28/#37/#38 contain requirements beyond `features.md` (e.g. nested aggregates, `having`, `count distinct`, specific response shape)? | **Blocking:** owner review before Wave 4. Reconciliation proposal in section 7 (awaiting owner decision). |
| D-3 | Suppress or merge into `Other`? | Default `Suppress`; offer `MergeToOther` as opt-in, since merging leaks the sum of suppressed groups and is easy to get wrong. |
| D-4 | Expose `suppressedGroupsCount` by default? | Yes (the issue requires it), configurable off per dataset because the count itself leaks "there are small groups". |
| D-5 | Treat `min`/`max` as disclosive and require a larger k (e.g. 2k) or disallow them below a threshold? | Accept the residual risk in v1 and document it; add `Privacy.MinMaxMultiplier` later if a regulated dataset needs it. |
| D-6 | If WP-FEAT-05 is built: per-cell or per-query budget accounting, and cache identical noisy answers? | Per-query accounting with cached identical answers per (client, query shape, day). |
| D-8 | Stage 3: which BI clients and protocols are actually required (Trino JDBC for Superset/Tableau, Flight SQL for Python only)? | Do 07a (conformance of what already exists) first; defer gRPC Flight SQL (07c) until a named client requires it; fix `f-data-04` wording either way. |

---

## 7. G-14 Reconciliation Proposal

**Status: proposal, awaiting owner decision.** G-14 remains open until the owner accepts, amends or rejects the proposed decision text in 7.5. Nothing in this section changes the WPs above yet.

**Sources:** issue bodies of #28, #37 and #38 as published on GitHub on 2026-10-14 (no comments on any of the three), this plan, `features.md` and roadmap decisions G-6/G-12. Code spot-checks on the worktree baseline.

**Legend:** *Covered* = a WP meets the requirement; *Partial* = a WP addresses it with a gap; *Missing* = no WP; *Plan exceeds* = plan adds scope or restrictions the issue does not ask for; *Conflict* = plan or a gate decision contradicts the issue.

### 7.1 Issue #28: Aggregation in GraphQL

| # | Requirement in issue | Plan coverage | Assessment | Recommendation |
|---|---|---|---|---|
| A-1 | `count`, `sum`, `avg`, `min`, `max` and grouping in GraphQL | WP-FEAT-03 root `<queryField>_aggregate` with `groupBy` | Covered | None. |
| A-2 | Same row filters and masks as row queries | WP-FEAT-03 (AST compiler injects row policy, tenant and consent before `GROUP BY`) | Covered | None. |
| A-3 | Aggregate and group only over clear columns | WP-FEAT-03 validation; compiler guard `AGGREGATION_ON_MASKED_COLUMN` (WP-FEAT-02, delivered) | Covered | None. |
| A-4 | Aggregations have their own cost in the cost model | WP-FEAT-03 fixed higher weight per aggregate field in `QueryCostAnalyzerRule` | Covered | Name the weight in the WP (configurable in `Analytics:Aggregation`). |
| A-5 | Done when: two **persons** with different row filters get different, correct grouped sums | WP-FEAT-03 integration test uses two **tenants** | Partial | Adjust plan: add an integration test with two principals in the same tenant whose RLS predicates differ. |
| A-6 | Done when: aggregating over a masked column is rejected | WP-FEAT-03 TDD `..._AggregateOnMaskedColumn_...` | Covered | None. |
| A-7 | (not in issue) | `COUNT(DISTINCT)`, `having`, nested-relation aggregates and cross-source aggregates excluded; limits `MaxGroupByColumns`/`MaxAggregates`/`MaxGroups`; fail-closed on truncation; result wrapper `<T>_aggregate_result` | Plan exceeds (restrictions) | Keep in plan; record the exclusions in the issue as "not in v1". |
| A-8 | (not in issue) | "Do not release #28 without #37" | Plan exceeds | Keep; note the coupling in #28. |

### 7.2 Issue #37: Minimum group size for aggregated results

| # | Requirement in issue | Plan coverage | Assessment | Recommendation |
|---|---|---|---|---|
| K-1 | Minimum group size per table (or per measure), **set by the data owner** | WP-FEAT-04 `Analytics:Privacy:Datasets` in operator configuration (options file); `meta.minimum_group_size` from dbt via WP-FEAT-06 | Partial | Owner question Q-2. Proposal: v1 keeps options plus dbt `meta` (the data owner's own artefact); a governance-API or catalog setting is deferred to a follow-up WP on the versioned governance store (WP-ARC-B-12). |
| K-2 | Every aggregation path: GraphQL, **SQL** and metric queries | GraphQL (FEAT-03/04) and metrics (FEAT-06b) covered; free-form SQL excluded by **G-12**; Stage 2 metric queries over SQL/Trino not planned (see M-6) | **Conflict** | Owner question Q-1. Proposal: confirm G-12 (no k-guard on free-form SQL in v1, a parser cannot prove group sizes for arbitrary SQL) and amend #37 to say so explicitly; document that row-level SQL access to a k-protected table is a separate grant (consistent with K-6). Optional follow-up: deny free-form SQL on tables with a configured threshold unless the caller holds row-level access. |
| K-3 | Suppress or merge into "other", as configured | WP-FEAT-04 `SuppressionMode` (`Suppress` default, `MergeToOther` opt-in) | Covered | None. |
| K-4 | Count taken over rows the caller may see, after row filters | WP-FEAT-04 hidden `COUNT(*) AS __n` in the governed statement, row policy applied before grouping | Covered | None. |
| K-5a | Protection against filters narrowing a group below the threshold | T-2: guard evaluates each filtered group, including the global group | Covered | None. |
| K-5b | Protection against differencing via totals for suppressed groups | T-4: no grand total in the same response when a group is suppressed; cross-query complement (T-3) only mitigated (identifier filters, negated filters, rate limits); full defence only with optional WP-FEAT-05 (DP noise, gated by G-12) | Partial | Adjust issue: state that the guarantee covers a single response plus listed mitigations, and that cross-query differencing is a documented residual risk unless DP noise is enabled. Adjust plan: add the issue's exact "done when" scenario (total must not allow computing the suppressed group) as an integration test. |
| K-6 | Response marks that groups were suppressed, without revealing their values | `suppressedGroupsCount` in the result wrapper; D-4 lets operators switch the count off | Partial | Adjust plan: always return a boolean `suppressed` flag (cannot be switched off); only the exact count is configurable. |
| K-7 | Row-level access stays a separate consent; threshold applies to aggregates | T-7: guard protects aggregate channels only; row access governed by RLS/consent | Covered | None. |
| K-8 | Done when: threshold 5, groups < 5 suppressed and marked; filtered group of 3 returns no value; total does not reveal the suppressed group | Integration test (1/4/20 departments) covers the first part | Partial | Adjust plan: add the filtered-group and total scenarios to the WP-FEAT-04 integration test. |
| K-9 | (not in issue) | `MIN`/`MAX` residual risk (T-5, D-5); rejection of filters on identifier-like columns and negated filters; reserved `__n`; cache key includes k | Plan exceeds | Keep; the filter restrictions are visible API behaviour and should be listed in #37. |

### 7.3 Issue #38: dbt Semantic Layer

| # | Requirement in issue | Plan coverage | Assessment | Recommendation |
|---|---|---|---|---|
| M-1 | **Spike first:** does MetricFlow-compiled SQL (simple, ratio, cumulative, entity joins) pass the governed SQL path and rewriter unchanged; short report | No spike WP. WP-FEAT-06 builds an own `MetricQueryCompiler` on `IGovernedAggregationService` instead of MetricFlow | **Missing / conflict of approach** | Owner question Q-3. Proposal: add WP-FEAT-06s (spike, S, 1-2 days) before 06b: run a fixed set of MetricFlow-generated statements through `ISqlEngine.Compile` and the governed pipeline, report pass/fail per metric type. Decide own compiler vs MetricFlow from the result. |
| M-2 | Stage 1: import `semantic_manifest.json` | Delivered (`POST /api/extensions/dbt/semantic-manifest`, `DbtSemanticManifestIngestionService`) | Covered | None. |
| M-3 | Stage 1: metrics, dimensions, entities, **time granularities** in the catalog with **description, unit, format** and groupable dimensions | `DbtMetric` has description and label but no unit or format; time grain only as an opaque `TypeParams` string; `saved_queries` not imported | Partial | Adjust plan: extend WP-FEAT-06a with unit/format (from metric `meta` or `config`), parsed time granularity per time dimension and groupable dimensions per metric. `saved_queries` deferred (Q-5). |
| M-4 | Stage 1: exposed via GraphQL and MCP | MCP `list_metrics`/`describe_metric` exist but are unregistered (confirmed: no reference to `McpSemanticTools` outside its own file in `src`); no metric catalog in GraphQL; REST `GET /api/extensions/dbt/metrics` exists | Partial | Adjust plan: WP-FEAT-06a registers the MCP tools **and** adds GraphQL catalog fields (`metrics`, `metric(name)`). Also apply the anti-oracle visibility filter to the REST endpoint, which today lists every metric to any authenticated caller without tenant or table-access filtering. |
| M-5 | Stage 2: one query shape (metrics, group by dimensions, filters on dimensions, time grain, order, limit) via GraphQL and MCP `query_metrics` | WP-FEAT-06b | Covered | None. |
| M-6 | Stage 2: metric queries also via **SQL/Trino protocol for BI tools** | WP-FEAT-07b exposes semantic models as catalog views; metric calculation stays in `query_metrics`; k-guard does not apply to free SQL (G-12) | Partial / conflict with K-2 | Owner question Q-4. Proposal: defer SQL/Trino metric queries to Stage 3 work with the named BI clients (G-12, D-8); amend #38 Stage 2 to GraphQL and MCP. |
| M-7 | Stage 2: cumulative metrics and joins via entities ("typical metrics") | Simple, ratio and derived only; cumulative, conversion and cross-model joins rejected with `METRIC_TYPE_NOT_SUPPORTED` | Partial | Decide after the spike (M-1); until then amend the issue to the plan's subset. |
| M-8 | Stage 2: row filters on base rows; masked-column metrics rejected; minimum group sizes; cost limits and audit | WP-FEAT-06b via `IGovernedAggregationService` (RLS, masking, guard, `METRIC_QUERY` audit, MCP guardrails) | Covered | Make the cost weight for `queryMetrics`/`query_metrics` explicit in WP-FEAT-06b. |
| M-9 | Stage 2 done when: two persons with different row filters, masked-column rejection, small group suppressed | WP-FEAT-06b tests: two-tenant manifest, masked measure | Partial | Same adjustment as A-5 (two principals, same tenant, different RLS) plus a suppression assertion. |
| M-10 | **Stage 3 = governance per metric:** consent per metric in addition to per table | Missing. The plan's "Stage 3" is headless BI connectivity (WP-FEAT-07) | **Missing / stage naming conflict** | Adjust plan: rename WP-FEAT-07 to "headless BI connectivity" (not Stage 3) and add WP-FEAT-08 "per-metric governance" for M-10..M-12. |
| M-11 | Stage 3: a metric may be released while the underlying rows are not | WP-FEAT-06 security rule: a metric whose underlying table is denied behaves as not found | **Conflict** | Owner question Q-6. Proposal: keep the deny-as-not-found default and let WP-FEAT-08 add an explicit per-metric grant that authorizes the aggregate even when row reads are denied; the grant still requires the k-guard and never releases masked measures. |
| M-12 | Stage 3: audit names the metric; metric definition version (dbt run) in responses | Audit `METRIC_QUERY` with metric names covered by WP-FEAT-06b; manifest version not tracked | Partial | WP-FEAT-08: store manifest version (dbt `invocation_id`/generation time) in the persisted catalog and return it in metric responses. |
| M-13 | (not in issue) | Trino client conformance (07a), gRPC Arrow Flight SQL server (07c) | Plan exceeds | Keep 07a as a carry-over quality item; defer 07c until a named client requires it (D-8). Neither should be tracked under #38. |
| M-14 | (not in issue) | Tenant-aware, persisted semantic catalog (G-12) | Plan exceeds (required by isolation) | Keep; it is a prerequisite for M-4 and M-8. |

### 7.4 Owner Questions

| ID | Question | Proposed answer |
|---|---|---|
| Q-1 | #37 requires the guard on SQL; G-12 excludes free-form SQL. Which prevails? | G-12 prevails for v1; amend #37. |
| Q-2 | Must data owners set thresholds at runtime (API/UI), or are configuration plus dbt `meta` sufficient for v1? | Configuration plus dbt `meta` in v1; runtime setting as a follow-up. |
| Q-3 | MetricFlow as the compiler (issue) or an own restricted compiler (plan)? | Run the spike first; default to the own compiler if MetricFlow SQL needs rewriting to pass the governed path. |
| Q-4 | Are metric queries over the SQL/Trino protocol required in Stage 2? | No; move them to the BI connectivity work after the target clients are named. |
| Q-5 | Is `saved_queries` import required? | Defer; not in any "done when" criterion. |
| Q-6 | May a metric be released while its rows are denied (per-metric consent)? | Yes, but only through an explicit per-metric grant in WP-FEAT-08; default stays deny-as-not-found. |
| Q-7 | Should the plan's restrictions (no `COUNT(DISTINCT)`, no `having`, no identifier or negated filters on aggregates) be written into the issues? | Yes, as "not in v1" notes. |

### 7.5 Proposed Decision Text (for the owner)

> **G-14 (proposed):** The WP-FEAT plans are accepted as the implementation basis for #28, #37 and #38 with these amendments: (1) WP-FEAT-03, WP-FEAT-04 and WP-FEAT-06b add integration tests for two principals in the same tenant with different row filters and for the #37 "done when" scenarios (filtered group of 3, total not revealing a suppressed group). (2) WP-FEAT-04 always returns a boolean `suppressed` flag; only the exact count is configurable. (3) A MetricFlow spike (WP-FEAT-06s) runs before WP-FEAT-06b and decides the compiler approach. (4) WP-FEAT-06a adds unit, format and time granularity to the semantic catalog, exposes it via GraphQL and MCP, and filters the REST metric listing by visibility. (5) WP-FEAT-07 is renamed "headless BI connectivity" and is no longer tracked as #38 Stage 3; a new WP-FEAT-08 covers per-metric consent and the manifest version in responses. (6) G-12 is confirmed: no k-guard on free-form SQL in v1; SQL/Trino metric queries are deferred to the BI connectivity work. The issues are updated accordingly: #37 states the SQL exclusion and the cross-query differencing residual risk, #38 states the Stage 2 metric subset and the deferral of SQL/Trino metric queries, and #28/#37 list the v1 restrictions.
