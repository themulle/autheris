# Product Requirement Document (PRD): Index-Aware Pricing for GraphQL Root-List Plans

**Document ID:** `REQ-COST-421`  
**Author:** Principal Product Manager & Technical Strategist (`product-manager`)  
**Contributors:** Lead Solution Architect (`csharp-architect`), Security Principal (`csharp-security-expert`)  
**Status:** `PROPOSED`  
**Date:** 2026-11-01  
**GitHub Issues:** #421 (Follow-up of #417 WP-D, builds on #390, #403, #404, #412)  
**Parent Initiative:** #417 (GraphQL Root Lists: Admission Price, Ceilings, Cursors & Pricing)

---

## 1. Executive Summary & Problem Statement

### 1.1 Context & Problem
In Autheris v1.5.0+, GraphQL root lists (e.g. `query { crm_sales_orders(first: 100, where: {...}, orderBy: {...}) { id amount } }`) are priced by HotChocolate AST validation (`QueryCostAnalyzerRule`) and the runtime quota middleware (`CostAndQuotaMiddleware`) using a formula dependent strictly on row count (`first`) and selected child fields:
$$\text{Cost} = (\text{effectiveRows} \times \text{listMultiplier}) + (\text{childCost} \times \text{effectiveRows})$$

Neither the filter (`where`) nor the sorting specification (`orderBy`) nor the physical schema design (presence or absence of matching database indexes) currently influences the query's admission price:
- **Scenario A (Indexed Access):** A query with `where: { id: { gt: $last } }` or `orderBy: [{ created_at: DESC }]` where `created_at` or `id` has a matching primary key, unique index, or physical non-unique index (#404) executes via an efficient B-tree index seek/range scan on the data source, using minimal CPU and I/O.
- **Scenario B (Unindexed Full Scan / Sort):** A query with `orderBy: [{ unindexed_column: DESC }]` or filtering on an unindexed column forces the database engine into a full table scan and an in-memory/on-disk sort of millions of rows, imposing up to $100\times$ to $1000\times$ more load on the database engine.

Under the current pricing model, both queries carry the identical admission price. This disincentivizes consumers from using indexed access paths, leaves the source vulnerable to denial-of-service through resource-exhausting unindexed sorts, and prevents operators from reflecting the true operational cost of unindexed queries.

### 1.2 Strategic Goal
Introduce **Index-Aware Admission Pricing** for GraphQL root lists:
1. Root-list query plans that cannot be served by a usable index (declared primary key with direction matching #390, declared secondary/unique index, or physical non-unique index #404) are subject to an index surcharge factor (configured via `QueryCostOptions.UnindexedRootListSurchargeMultiplier`).
2. Pricing relies **strictly on deterministic catalog metadata** (`TableMetadata`), never on live table statistics or volatile row counts.
3. Enforce **Zero-Trust Anti-Enumeration**: Internal pricing reflects index absence, but external error responses and codes in Production remain byte-identical to standard cost rejections. No index layout or column existence is leaked to untrusted callers.

---

## 2. Gatekeeper Qualification & Competitive Benchmark

### 2.1 Deduplication Check (`docs/features/`)
- Autheris features [`f-gov-14-federated-virtual-filters.md`](file:///root/autheris/docs/features/f-gov-14-federated-virtual-filters.md) and [`f-sec-08-hmac-column-level-encryption.md`](file:///root/autheris/docs/features/f-sec-08-hmac-column-level-encryption.md) define AST compilation and token masking, but the GraphQL cost analysis engine does not currently consume index metadata.
- Work Package D (#421) was identified in the origin master plan #417 as the logical follow-up to physical index synchronization (#404) and cursor pagination (#420).

### 2.2 Gatekeeper Rulings & Anti-Pattern Rejections
1. **🛑 Anti-Pattern 1: Hard rejection of unindexed queries by default**
   - *Risk:* Rejecting queries immediately because an index does not exist breaks backwards compatibility and turns the GraphQL gateway into an index oracle, enabling attackers to probe for the presence of indexes across tables.
   - *Safe Solution:* Price unindexed root lists higher via an additive or multiplicative surcharge. Hard rejection is deferred behind an explicit operator option (`RejectUnindexedRootLists = false` by default).
2. **🛑 Anti-Pattern 2: Live statistics / runtime `ANALYZE` queries**
   - *Risk:* Querying database system views (like `pg_stats` or `sys.dm_db_partition_stats`) during query validation introduces high latency, database connection contention, and stale-statistics vulnerabilities.
   - *Safe Solution:* Evaluate index usability strictly against cached, synchronized catalog metadata (`TableMetadata.PrimaryKeyColumns`, `PrimaryKeyDirections`, `Indexes`, `PhysicalIndexes`).
3. **🛑 Anti-Pattern 3: Index layout leakage via error messages**
   - *Risk:* Returning messages such as `"Field 'order_date' has no index"` in Production reveals internal database schema and indexing structure to unauthorized callers.
   - *Safe Solution:* In Production (`GraphQlEnumerationShieldMiddleware`), unindexed cost rejections yield the generic `AUTH_QUERY_COMPLEXITY_EXCEEDED` error with shielded details.

### 2.3 Competitive Moat Matrix
| Platform | Index-Aware Costing | Column Visibility Pruning | Anti-Enumeration Shielding |
| :--- | :--- | :--- | :--- |
| **Apollo Router / Fed v2** | ❌ None (RLS and indexing delegated to downstream subgraphs) | ❌ None | ❌ Subgraph errors leak downstream details |
| **Hasura DDN** | ❌ Static permissions; no index-aware cost model | ⚠️ Role-based checks only | ⚠️ Verbose error reporting |
| **Autheris (WP-D #421)** | ✅ **Native AST index evaluation** across declared keys, unique indexes, and physical indexes | ✅ **Restricts indexes by caller column visibility** (#412 / `TableKeyVisibility`) | ✅ **Zero-Trust fail-closed shield**; identical rejection envelope |

---

## 3. Functional Requirements

### 3.1 Index Usability Evaluation Rules
For a GraphQL root list query selection `R` targeting table `T`:
- **`REQ-COST-421-01` (Candidate Index Discovery):** Discover all candidate indexes from `TableMetadata`:
  1. The declared primary key (`T.PrimaryKeyColumns`, with `T.PrimaryKeyDirections`).
  2. Declared secondary and unique indexes (`T.Indexes`).
  3. Source-observed physical non-unique indexes (`T.PhysicalIndexes`).
- **`REQ-COST-421-02` (Column Clearance Pruning):** Restrict candidate indexes using `TableKeyVisibility.Restrict(table, visibleColumns)`. Any index referencing one or more columns not authorized or visible to the caller is disqualified from consideration.
- **`REQ-COST-421-03` (Ordering Usability Match):**
  - If the query specifies `orderBy`, an index is *usable for ordering* if its leading indexed columns match the requested sort columns and sort directions in order.
  - If the query does not specify `orderBy`, default catalog ordering is assumed (which uses primary key or primary unique index), so ordering is treated as indexed.
- **`REQ-COST-421-04` (Filter Usability Match):**
  - If the query specifies a `where` filter, an index is *usable for filtering* if its leading column appears in an equality (`eq`, `in`) or bounded range (`gt`, `gte`, `lt`, `lte`) condition.
- **`REQ-COST-421-05` (Usable Plan Determination):**
  - A root list plan is classified as **Indexed** if:
    a) A usable ordering index exists matching the `orderBy` columns/directions, OR
    b) No explicit `orderBy` is provided AND a usable filter index exists, OR
    c) Both `where` and `orderBy` utilize the same index prefix or compatible index scans.
  - Otherwise, the plan is classified as **Unindexed**.

### 3.2 Pricing Surcharge Model
- **`REQ-COST-421-06` (QueryCostOptions Extension):**
  - Add configuration property to `Autheris.Domain.Options.QueryCostOptions`:
    ```csharp
    [Range(1.0, 50.0)]
    public double UnindexedRootListSurchargeMultiplier { get; set; } = 2.0;
    ```
  - When a root list is determined to be unindexed, the base list contribution is multiplied by `UnindexedRootListSurchargeMultiplier`:
    $$\text{effectiveListMultiplier} = \lceil \text{DefaultListMultiplier} \times \text{UnindexedRootListSurchargeMultiplier} \rceil$$
- **`REQ-COST-421-07` (Saturating Arithmetic):** All multiplication and addition operations must use saturating arithmetic (`Math.Min(int.MaxValue, ...)`) to prevent integer overflow attacks.

### 3.3 Security, Telemetry & Audit
- **`REQ-COST-421-08` (Audit Integrity):** Audit event logging retains standard single-record-per-request volume (`AuditEventTypes.QueryExecution`). The index evaluation result (`IsIndexedPlan: bool`) is recorded in internal diagnostics/telemetry spans without exposing sensitive column names.
- **`REQ-COST-421-09` (Production Anti-Enumeration):** In non-development environments, rejections caused by index surcharge are reported as `AUTH_QUERY_COMPLEXITY_EXCEEDED` without disclosing whether an index was present or absent.

---

## 4. Acceptance Criteria & Test Strategy
1. **Unit Tests:**
   - Verify `UnindexedRootListSurchargeMultiplier` bounds `[1.0, 50.0]` and startup validation in `GatewayStartupValidator`.
   - Verify index usability resolver correctly identifies:
     - PK matching `orderBy` column and direction -> Indexed (no surcharge).
     - PK matching `orderBy` column but opposite direction -> Unindexed (surcharge applied).
     - Physical non-unique index matching filter column -> Indexed.
     - Unindexed column in `orderBy` -> Unindexed (surcharge applied).
     - Index containing masked/hidden column for caller -> Disqualified -> Unindexed.
2. **Integration Tests:**
   - Verify execution through `CostAndQuotaMiddleware` accurately rejects unindexed queries exceeding tier limits while admitting indexed counterparts.
   - Verify production error responses do not leak index names or structural layout.
3. **Architecture Tests:**
   - 100% compliance with `Autheris.Tests.Architecture` (no size ceiling regressions, zero unconsumed options properties).
