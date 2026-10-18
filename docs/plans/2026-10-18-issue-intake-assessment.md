# Issue Intake Assessment: #163, #174, #177, #178

**Document ID:** `INTAKE-2026-10-18`  
**Date:** 2026-10-18  
**Phase:** 1 (product qualification), by the Enterprise Product Manager & Strategic Gatekeeper  
**Code base verified:** `main` / `feat/issue-143-restricted-scope-columns` at `a87f9b06`  
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md) (section 0, "Issue intake 2026-10-18")  
**Child Requirements:** `REQ-GOV-163`, `REQ-OPS-174`, `REQ-GOV-177`, `REQ-DATA-178`  

---

## 1. Summary

All four issues are valid, strategically aligned with Autheris' core architecture, and represent real enterprise customer and operational needs. None is rejected and none is a duplicate. Each issue is **sensible with changes** (architectural refinements, fail-closed enforcement, and strict zero-allocation boundaries).

| Issue | Verdict | Recommended solution (one line) | Size | Proposed wave / rank |
|---|---|---|:---:|---|
| **#163** required filter columns per table | Sensible with changes | Declare `RequiredFilterColumns` in table metadata (baseline DDL x3); enforce fail-closed in `AstSecurityVisitor` (SQL), `TreeSqlCompiler` (GraphQL), and `GovernedTableReadPlanner` (REST/Data APIs) unless satisfied by client or active RLS row policy | M | Wave 2, rank 2.12 |
| **#174** measure ReadyToRun impact for #150 WP-D | Sensible with changes (operational spike) | Standardized cold-start benchmark harness comparing Release with and without `PublishReadyToRun=true` (`-r linux-x64`); measure cold P99, warm median, and Docker image size delta to resolve #150 OQ-9 | S | Wave 2, rank 2.8a (spike for #150) |
| **#177** cost admission coarse for nested single-row lookups | Sensible with changes | Distinguish 1:1 and N:1 relations in `QueryCostAnalyzerRule` (cardinality 1); implement two-phase token bucket (reserve upfront, settle/refund actual work post-execution in `CostAndQuotaMiddleware`); configurable factors | M | Wave 2, rank 2.13 |
| **#178** bulk read of one table for read-only tokens | Sensible with changes | Dedicated single-table endpoint (`/api/v1/data/.../bulk`) for read-only tokens; typed JSON filter AST; tamper-evident keyset cursor; chunked streaming response; per-row quota/FinOps accounting | M-L | Wave 2, rank 2.14 (or Wave 3 rank 3.11) |

---

## 2. Cross-Cutting Architectural Findings & Guardrails

1. **Zero-Trust & Fail-Closed Enforcement:**
   - **#163**: If a table specifies required filter columns, any read path lacking constraints on those columns must reject with `400 REQUIRED_FILTER_MISSING`. However, if the gateway's active Casbin ABAC or Consent row policy already injects a predicate constraining the required column (e.g. `tenant_id = @tenant`), the query is valid and must pass.
   - **#177**: Cost admission must never be bypassed. Upfront reservation prevents concurrency exhaustion. Post-execution settlement only refunds up to the bucket capacity; it never mints excess tokens.
   - **#178**: Strictly scoped to a single table. No joins, no subqueries, no lateral queries. Keyset cursors are opaque and validated against the table's declared primary key (`TableKeyDefinition`) to prevent IDOR / tampering.
2. **High-Performance .NET 10 (Zero-Allocation on Hot Paths):**
   - AST validation for #163 must run in existing visitor passes (`AstSecurityVisitor`, `TreeSqlCompiler`) without additional AST cloning.
   - Streaming in #178 must utilize `PipeWriter` / `Utf8JsonWriter` or Arrow IPC streams directly to the response body, avoiding Large Object Heap (LOH) buffering (unlike legacy `ExecuteQueryBufferedAsync`).
   - #174 evaluates whether the native code footprint expansion (+30-60 MB image size) provides the required $\ge 25\%$ cold-start latency reduction before committing to R2R.
3. **Shared Baseline DDL & Governance Repositories:**
   - #163 adds `required_filter_columns_json` to `TABLES` across PostgreSQL, SQLite, and SQL Server baselines (`0001` baseline extended in place per owner rule 2026-10-15: no upgrade migrations).

---

## 3. Per-Issue Assessment

### 3.1 #163 Required filter columns per table (declared in the catalog), enforced on every read path

**Verdict:** Sensible with changes.  
**Problem:** Massive partitioned tables (e.g. telemetry, audit, transactions) cause catastrophic table scans, buffer pool eviction, or cross-tenant leaks if queries omit required filter columns (such as `tenant_id`, `created_date`, or `organization_id`).

**Claim Check:**
- Confirmed: Neither `Table` nor `TableMetadata` (`TableModels.cs`) contains required filter definitions.
- Confirmed: `AstSecurityVisitor`, `TreeSqlCompiler`, and `GovernedTableReadPlanner` allow unconstrained `SELECT * FROM table` as long as table consent is active.
- Confirmed: GraphQL schema generates `_filter` as entirely optional for all tables.

**Recommended Solution:**
- Declare `IReadOnlyList<string> RequiredFilterColumns` on `TableMetadata`, persisted in `TABLES.required_filter_columns_json`.
- Enforce across all read paths:
  1. **SQL / WebSQL / Trino / MCP SQL:** `AstSecurityVisitor` verifies that the effective `Where` clause (including injected RLS row filters) contains a comparison or `IN` predicate referencing the required column.
  2. **GraphQL:** Hot Chocolate validation rule / `TreeSqlCompiler` validates that the required column is present in the `_filter` argument or supplied by active row policy.
  3. **REST Data API & OData:** `GovernedTableReadPlanner` validates `intent.Filter` or injected policy; rejects with HTTP 400 if missing.
  4. **Arrow Export & OLAP:** Rejects table export requests without filter parameters.
- **Deviation from Issue:** Do not reject if an active gateway row filter already constrains the column. Requiring client duplication of an already-injected tenant filter would break standard multi-tenant consumption.

**Size / Wave:** M, Wave 2 (rank 2.12).  
**Dependencies:** Baseline DDL slot under `G-SHARED`.

---

### 3.2 #174 Measure ReadyToRun impact (cold start, warm median, image size) for #150 WP-D

**Verdict:** Sensible with changes (operational qualification spike).  
**Problem:** In `PLAN-OPS-150`, WP-D (`PublishReadyToRun=true -r linux-x64`) was left conditional under owner decision OQ-9: "ReadyToRun only if JIT is at least 25% of the cold path". Issue #174 requests empirical measurement of cold start, warm median, and image size.

**Claim Check:**
- Confirmed: `PublishReadyToRun` is not set in `Autheris.Api.csproj` or Docker workflows.
- Confirmed: `benchmarks/load/scripts` exists; `cold-start.sh` harness (WP-A) isolates JIT via `System.Runtime` event counters and `dotnet-trace`.

**Recommended Solution:**
- Execute a dedicated measurement spike comparing `Release` non-R2R vs `Release` R2R on `linux-x64`:
  1. **Cold Start P99:** Container start to first authenticated GraphQL & MCP request completion.
  2. **Warm Median Latency:** Steady-state request latency over 1,000 queries.
  3. **Image Footprint:** Docker image size delta (uncompressed and compressed registry footprint).
  4. **Security Verification:** Run architecture test `ReleaseBuildExcludesTestAuthTests` (SC-04) on R2R binaries to verify that IL inspection and test-auth exclusion hold.
- Decision gate: If JIT accounts for $\ge 25\%$ of cold-start latency and warm median is not regressed, approve WP-D into the publish workflow; otherwise reject R2R to avoid image bloat.

**Size / Wave:** S, Wave 2 (rank 2.8a, paired with #150).  
**Dependencies:** None; manual benchmark harness.

---

### 3.3 #177 Cost admission by query shape is too coarse for nested single-row lookups

**Verdict:** Sensible with changes.  
**Problem:** `QueryCostAnalyzerRule` statically assumes `_defaultListMultiplier` (10) for nested selections. For 1:1 or N:1 foreign key relations (e.g. `order { customer { address } }`), static admission vastly overestimates complexity. Even worse, `CostAndQuotaMiddleware` unconditionally deducts this inflated cost upfront from the client's rate-limiter bucket without refunding, penalizing clients 10x-100x their actual compute.

**Claim Check:**
- Confirmed: `CostAndQuotaMiddleware.cs:129` calls `_rateLimiter.CheckCostQuotaAsync`, deducting the static estimate immediately.
- Confirmed: `IRateLimiterService` lacks any refund or settlement API (`CheckCostQuotaAsync` only).
- Confirmed: `QueryCostAnalyzerRule.CalculateCost` uses `_defaultListMultiplier` whenever pagination is not explicitly capped, without checking if the relation is 1:1 or N:1.

**Recommended Solution:**
1. **Multiplicity-Aware Cost Estimation:** In `QueryCostAnalyzerRule`, check catalog relation metadata (`TableRelation.Multiplicity` / single-entity type vs list). If the relation is 1:1 or N:1, multiplier is 1 (not 10).
2. **Two-Phase Token Bucket (Reserve & Settle):**
   - *Phase 1 (Admission):* Deduct estimated cost up front via `ReserveCostQuotaAsync` to prevent concurrency denial-of-service.
   - *Phase 2 (Post-Execution Settlement):* In `CostAndQuotaMiddleware`, after `await _next(context)`, determine actual cost from executed operations and rows returned. Call `SettleCostQuotaAsync(key, reservedCost, actualCost)` to credit the delta (`reservedCost - actualCost`) back to the bucket (capped at maximum capacity).
3. **Configurable Cost Factors:** Expose `CostPerSingleRowLookup`, `CostPerRowReturned`, and `DefaultListMultiplier` in `GatewayOptions.GraphQL`.

**Size / Wave:** M, Wave 2 (rank 2.13, pairs with #175).  
**Dependencies:** Coordinate with `IRateLimiterService` (Redis & In-Memory).

---

### 3.4 #178 Bulk read of one table for read-only tokens

**Verdict:** Sensible with changes.  
**Problem:** Read-only tokens (`ReadOnlyScopes`, AI agents, ETL tools) cannot perform bulk data extraction. GraphQL is blocked by complexity caps and small page sizes; WebSQL/Trino arbitrary SQL is blocked or risks unconstrained query costs; `ArrowExportEndpoints` buffers full results in memory (`ExecuteQueryBufferedAsync`), risking OOM; and REST Data API is limited to offset pagination ($O(N^2)$ backend cost).

**Claim Check:**
- Confirmed: `ReadOnlyTokenMiddleware` only permits safe methods and fixed POST endpoints.
- Confirmed: `ArrowExportEndpoints.cs:191` uses `ExecuteQueryBufferedAsync` without chunked keyset cursor.
- Confirmed: No governed single-table bulk streaming endpoint with keyset cursors exists.

**Recommended Solution:**
- Expose a governed bulk read endpoint: `POST /api/v1/data/{domain}/{schema}/{table}/bulk` (or `/stream`).
- **Single Table Only:** Strict validation against `TableMetadata`. Rejects joins, subqueries, or cross-source access.
- **Typed Filter:** Structured JSON filter AST (e.g., column, operator, value) mapped to parameterized expressions; no arbitrary SQL text.
- **Keyset Cursor Pagination:** Base64 deterministic opaque cursor based on declared primary key / unique index (`TableMetadata.PrimaryKeyColumns`), enforcing $O(1)$ index seek pagination.
- **Bounded Streaming Response:** True HTTP chunked streaming (NDJSON or Arrow IPC stream via `PipeWriter`), bounded per slice (e.g., default 5,000 rows max per slice or 10s streaming window).
- **Row-Based Accounting:** Integrates with #177 to deduct rate-limit and FinOps quota based on actual streamed rows (`X-Rows-Streamed`).
- **Read-Only Token Permitted:** Allow-listed in `ReadOnlyTokenMiddleware`.

**Size / Wave:** M-L, Wave 2 (rank 2.14) or Wave 3 (rank 3.11).  
**Recommendation:** Wave 2 rank 2.14 for core single-table keyset streaming (unblocks AI agent bulk extraction).

---

## 4. Prioritized Placement & Recommended Waves

| Wave | Rank | Item | Size | Gate / Milestone |
|---|---|---|:---:|---|
| **2** | **2.8a** | **#174** ReadyToRun benchmark measurement spike for #150 WP-D | S | Unblocks #150 WP-D decision |
| **2** | **2.12** | **#163** Required filter columns per table enforced across every read path | M | Phase 2 Plan (DDL slot under G-SHARED) |
| **2** | **2.13** | **#177** Cost admission multiplicity & post-execution token bucket settlement | M | Phase 2 Plan (pairs with #175) |
| **2** | **2.14** | **#178** Governed single-table keyset-streaming bulk read for read-only tokens | M-L | Phase 2 Plan (reusable streaming core) |

---

## 5. Owner Questions for Intake Confirmation

| ID | Issue | Question | Recommended Default |
|---|---|---|---|
| **OQ-26** | #163 | Does an active RLS / Consent row filter satisfying the required column waive the client filter requirement? | **Yes** (effective filter check; avoids breaking multi-tenant callers where tenant is injected). |
| **OQ-27** | #163 | How are required filter columns declared in the catalog? | Declarative list in `TABLES.required_filter_columns_json` in baseline DDL x3 (no auto-inference). |
| **OQ-28** | #174 | Treat #174 as an operational measurement spike embedded into #150 rather than a standalone feature? | **Yes** (directly provides data for OQ-9 gate). |
| **OQ-29** | #177 | Should token bucket settlement immediately credit the difference back to Redis / in-memory bucket? | **Yes** (atomic Lua / CAS, clamped to bucket capacity). |
| **OQ-30** | #178 | Default response format for the bulk streaming endpoint: NDJSON or Arrow IPC? | **NDJSON default**, Arrow IPC optional via `Accept: application/vnd.apache.arrow.stream`. |
