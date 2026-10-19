# Issue Intake Assessment: Closed Issues Addenda (#39, #29, #75, #112, #175)

**Document ID:** `INTAKE-2026-10-19-ADDENDA`  
**Date:** 2026-10-19  
**Phase:** 1 (Product Intake & Strategic Qualification)  
**Author:** Enterprise Product Manager & Strategic Gatekeeper (`product-manager`)  
**Codebase Verified:** `main` (v1.2.1 at `8a0b831e`)  
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)  
**Child Requirements:** `REQ-GOV-39`, `REQ-GOV-29`, `REQ-DATA-75`, `REQ-DATA-112B`, `REQ-GOV-175B`  

---

## 1. Executive Summary & Qualification Matrix

This intake evaluates 5 follow-up items and addenda identified in comments on closed GitHub issues by `@rgruebel`. All 5 items have been qualified against Autheris' core mission: **Zero-Trust Enterprise Data Access Gateway** with high-performance .NET 10 runtime execution, zero data leaks, and uncompromising governance boundaries.

All five proposals are **APPROVED WITH CONDITIONS** (sensible with architectural refinements). None is a duplicate and none is rejected, though strict boundary guardrails are mandated to prevent denial-of-service, join-based oracle leaks, and allocation regressions.

| Item / Issue | Verdict | Recommended Solution Summary | Size | Wave / Rank | RICE-C Score |
|---|---|---|:---:|---|:---:|
| **#39 Addendum** (`REQ-GOV-39` / Bug)<br/>Pagination Truncation Signal | **Approved with Conditions** | Set `context.ContextData["PaginationTruncated"] = true` during root list slicing when `list.Count >= maxResponseRows` or lookahead limit triggers; emit `extensions.pagination { isTruncated, maxRowsReached }`; document cursor guidance. | S | Wave 1, Rank 1.11 | 240.0 |
| **#29 Addendum** (`REQ-GOV-29` / Feature)<br/>Per-Scope Token Bucket & Rate Limiting | **Approved with Conditions** | Expand `ClientTierResolver.BuildAuthenticatedSubjectId` to include normalized token scopes (`user:{tenant}:{oid}:{scope}`); introduce `ScopeTierMappings` and `ScopeLimits` in `ClientTierOptions` to isolate agents (`Agent.Read`) from interactive apps (`Apps.Read`). | S-M | Wave 2, Rank 2.17 | 187.5 |
| **#75 Addendum** (`REQ-DATA-75` / Gap)<br/>Cross-Source Relation Resolver Wiring in GraphQL | **Approved with Conditions** | Wire `CrossSourceRelationResolver` into `GovernedTreeQueryService`; split tree into same-source primary SQL + cross-source batched foreign key lookups; enforce 4-eyes `AllowCrossDataSource` and clear-key (no mask) join rules. | M | Wave 3, Rank 3.2b | 162.0 |
| **#112 Addendum** (`REQ-DATA-112B` / Enhancement)<br/>Time-Bucketing in GraphQL Aggregations | **Approved with Conditions** | Support structured `groupBy: [{column: "ts", bucket: DAY, timeZone: "Europe/Berlin"}, ...]`; push down date truncation expressions to source SQL dialects; enforce required filter columns (#163); cost by group cardinality. | M | Wave 2, Rank 2.6b | 150.0 |
| **#175 Addendum** (`REQ-GOV-175B` / Enhancement)<br/>Caller Bucket State & System Caps in `/analyze` | **Approved with Conditions** | Add non-destructive `GetBucketStateAsync` to `IRateLimiterService`; surface caller's current capacity, balance, refill rate, and active system caps (`MaxAllowedComplexity`, `MaxResponseRows`, `MaxAllowedOffset`) in `/analyze` response. | S | Wave 2, Rank 2.7b | 216.0 |

---

## 2. Cross-Cutting Strategic & Architectural Guardrails

1. **Zero-Trust & Fail-Closed Defaults:**
   - **Cross-Source GraphQL Wiring (#75):** Cross-data-source joins must **never** execute across tables unless both table relations explicitly declare `AllowCrossDataSource: true` (four-eyes approval) AND the gateway global master switch `Gateway:CrossSource:Enabled` is active. Crucially, join keys between different sources **must not be masked or HMAC-tokenized**; joining on pseudonymized keys across systems is an information leakage oracle and must fail closed with HTTP 403 / GraphQL error `CROSS_SOURCE_KEY_MASKED`.
   - **Time-Bucketing Aggregations (#112):** Bucketing must never be an escape hatch to perform unconstrained scans of massive partitioned time-series tables. If a table defines `RequiredFilterColumns` (delivered in #163), an aggregation query omitting those required columns must fail immediately with `400 REQUIRED_FILTER_MISSING` unless active row policies constrain the columns. Time zone strings must be strictly validated against IANA identifiers to prevent SQL injection in dialect formatting.
   - **Per-Scope Token Bucket (#29):** Default unconfigured scopes must not receive unmetered access; unmapped scopes fallback to `Standard` or `Free` tiers with fail-closed subject keys.

2. **High-Performance .NET 10 (Zero-Allocation Hot Paths):**
   - **Pagination Signal (#39):** Must not allocate tracking objects per row. Setting `ContextData["PaginationTruncated"] = true` is a single dictionary entry write upon hitting the limit slice.
   - **Static Analysis Caller State (#175):** Must be a zero-mutation read-only probe. No locks, no Redis token decrement, and no side-effects on the rate-limiter bucket.
   - **Time-Bucketing Pushdown (#112):** Bucketing must be compiled directly into the source SQL `GROUP BY` via `ISqlDialectProvider`. Pulling unaggregated rows into memory for gateway-side time truncation is strictly prohibited to prevent Large Object Heap (LOH) exhaustion.

---

## 3. Detailed Intake & Qualification per Item

### 3.1 Issue #39 Addendum: Pagination Truncation Signal (`REQ-GOV-39`)

- **Problem Description:** `PaginationTruncationMiddleware.cs` is already active in the Hot Chocolate pipeline (`GatewayServiceCollectionExtensions.cs:596`), reading `context.ContextData.TryGetValue("PaginationTruncated", out var truncatedObj) && truncatedObj is true` to append `extensions.pagination: { isTruncated: true, maxRowsReached: true }`. However, audit of `src/` reveals that **no component ever sets this key**. When queries hit `MaxResponseRows` or `first` limits, clients receive truncated results with no signal that data was cut off.
- **Code Base Verification:**
  - `PaginationTruncationMiddleware.cs` exists and defines `public const string ContextKey = "PaginationTruncated";`.
  - In `CatalogGraphQlTypeModule.cs:542-566`, `GraphQlTreeBuilder.BuildTree` caps limits at `maxResponseRows`, and rows are collected into `list`. If `list.Count >= maxResponseRows`, `ContextKey` is never populated.
- **Strategic Verdict:** **Approved with Conditions (Bugfix / Wave 1)**.
  - Slicing at `maxResponseRows` or when `list.Count == queryNode.Limit` (with lookahead probe `limit + 1`) must set `ctx.ContextData[PaginationTruncationMiddleware.ContextKey] = true`.
  - Schema documentation and response extensions must provide clear cursor guidance: advising clients to paginate using `offset` or declared primary keys (`TableKeyDefinition`) rather than fetching unbounded top-level lists.
- **RICE-C:** Reach: 10, Impact: 2.0, Confidence: 100%, Compliance: 1.2, Effort: 1 PW $\rightarrow$ **Score: 240.0**.

### 3.2 Issue #29 Addendum: Per-Scope Token Bucket & Rate Limiting (`REQ-GOV-29`)

- **Problem Description:** `ClientTierResolver.BuildAuthenticatedSubjectId` generates subject keys strictly as `user:{tenant}:{oid}` (plus client ID). A user running both an interactive web application (`Apps.Read`) and an automated AI agent (`Agent.Read`) shares the exact same rate limit bucket. When an AI agent executes large analysis queries or batch operations, the interactive user suffers sudden 429 `TOO_MANY_REQUESTS` rejections.
- **Code Base Verification:**
  - `ClientTierResolver.cs:129-163` only inspects tenant, `userSid`, and `clientId`. It never inspects scopes.
  - `RateLimitingOptions.cs:12-17` (`ClientTierOptions`) contains `RoleTierMappings` and `ApiKeys`, but lacks any scope mapping.
  - `TokenAccessScope.GetScopes(principal)` already exists in `Autheris.Domain.Security`.
- **Strategic Verdict:** **Approved with Conditions (Feature / Wave 2)**.
  - Extend bucket subject key generation to incorporate normalized token scopes: `user:{tenant}:{oid}:{scope}` (or sorted canonical scope list for multi-scope tokens).
  - Add `ScopeTierMappings` and `ScopeLimits` to `ClientTierOptions` to allow governance teams to grant different token buckets to agents vs user-interactive sessions.
- **RICE-C:** Reach: 8, Impact: 2.5, Confidence: 95%, Compliance: 1.0, Effort: 1.0 PW $\rightarrow$ **Score: 190.0**.

### 3.3 Issue #75 Addendum: Cross-Source Relation Resolver Wiring in GraphQL (`REQ-DATA-75`)

- **Problem Description:** While `CrossSourceRelationResolver` and `InMemoryCrossSourceRowReader` exist in `Autheris.Application.Data.Relations` and are registered in DI (`DataApiServiceExtensions.cs:26`), `GovernedTreeQueryService.cs:277-280` unconditionally throws `GatewayInvalidQueryException("Relations across data sources are not supported in one query.")`.
- **Code Base Verification:**
  - `ICrossSourceRelationResolver` defines `ResolveRelationAsync(CrossSourceRelationRequest, ...)`.
  - `GovernedTreeQueryService` lacks `ICrossSourceRelationResolver` injection and fails fail-closed on differing `SourceName`.
  - `RelationExecutability.IsSameExecutionSource` suppresses cross-source relations from GraphQL schema generation unless gated.
- **Strategic Verdict:** **Approved with Conditions (Gap / Wave 3)**.
  - Wire `ICrossSourceRelationResolver` into `GovernedTreeQueryService`.
  - When relations traverse data sources, execute the primary source query, extract foreign keys, and batch-fetch related rows via `CrossSourceRelationResolver.ResolveRelationAsync`.
  - Must enforce: (1) `Gateway:CrossSource:Enabled == true`, (2) four-eyes approval (`AllowCrossDataSource == true` on relation), and (3) Clear-Key verification (join keys cannot be masked).
- **RICE-C:** Reach: 6, Impact: 3.0, Confidence: 90%, Compliance: 1.0, Effort: 2.0 PW $\rightarrow$ **Score: 162.0**.

### 3.4 Issue #112 Addendum: Time-Bucketing in GraphQL Aggregations (`REQ-DATA-112B`)

- **Problem Description:** Typed aggregations (`<table>_aggregate`) delivered in #112 support `groupBy` only on raw column names. For time-series and event analysis, clients need date/time bucketing (`DAY`, `WEEK`, `MONTH`, `HOUR`, `YEAR`) with time-zone adjustments. Without source database pushdown, clients must pull all rows into gateway memory.
- **Code Base Verification:**
  - `AggregateGraphQlTypes.cs:161` defines `.Argument("groupBy", a => a.Type(new ListTypeNode(NonNull(names.Column))))`.
  - `AggregationQuery` only holds `IReadOnlyList<string> GroupBy`.
  - Dialect generation and `TreeFilterClauseBuilder` support time functions via #130 (`convert_timezone`).
- **Strategic Verdict:** **Approved with Conditions (Enhancement / Wave 2)**.
  - Support `groupBy: [{column: "ts", bucket: DAY, timeZone: "Europe/Berlin"}, "device_id"]` in GraphQL schema.
  - Push down date truncation expressions (`date_trunc`, `DATETRUNC`, `strftime`) to the underlying SQL engine.
  - Enforce required filter columns (#163) so time-bucketing on large tables cannot execute without partition pruning filters.
- **RICE-C:** Reach: 8, Impact: 2.5, Confidence: 90%, Compliance: 1.0, Effort: 1.2 PW $\rightarrow$ **Score: 150.0**.

### 3.5 Issue #175 Addendum: Caller Bucket State & System Caps in `/analyze` (`REQ-GOV-175B`)

- **Problem Description:** Issue #175 provided static query analysis for read-only tokens, returning query validation and exact execution cost. However, clients and AI agents cannot inspect their current token bucket balance, refill rate, or active gateway caps (`MaxAllowedComplexity`, `MaxResponseRows`, `MaxAllowedOffset`).
- **Code Base Verification:**
  - `IRateLimiterService` contains `CheckCostQuotaAsync`, `ReserveCostQuotaAsync`, and `SettleCostQuotaAsync`, but lacks a read-only probe method.
  - `POST /api/v1/governance/queries/analyze` returns `QueryAnalysisResult` containing `ValidationReport` and `AccessRequirements`, with no caller quota context.
- **Strategic Verdict:** **Approved with Conditions (Enhancement / Wave 2)**.
  - Add `GetBucketStateAsync` to `IRateLimiterService` for zero-deduction bucket inspection.
  - Surface caller's bucket state (`capacity`, `currentBalance`, `refillRatePerSecond`) and system caps (`maxAllowedCost`, `maxResponseRows`, `maxAllowedOffset`) in `QueryAnalysisResult`.
- **RICE-C:** Reach: 9, Impact: 2.0, Confidence: 100%, Compliance: 1.2, Effort: 0.8 PW $\rightarrow$ **Score: 216.0**.

---

## 4. Backlog Registration & Sequencing Recommendations

1. **Wave 1 (Bugfixes):**
   - **Rank 1.11**: `REQ-GOV-39` (Pagination truncation signal & cursor guidance). High urgency for client data correctness.
2. **Wave 2 (Urgently Missing Features & Enhancements):**
   - **Rank 2.6b**: `REQ-DATA-112B` (Time-bucketing in GraphQL aggregations). Extends #112.
   - **Rank 2.7b**: `REQ-GOV-175B` (Caller bucket state & system caps in `/analyze`). Extends #175.
   - **Rank 2.17**: `REQ-GOV-29` (Per-scope token bucket & rate limiting). Prevents agent/app rate limit collisions.
3. **Wave 3 (Feature Completion):**
   - **Rank 3.2b**: `REQ-DATA-75` (Cross-source GraphQL relation resolver wiring). Completes cross-source federation milestone.
