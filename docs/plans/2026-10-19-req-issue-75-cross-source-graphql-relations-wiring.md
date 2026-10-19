# PRD: REQ-DATA-75 - Cross-Source Relation Resolver Wiring in GraphQL

**Document ID:** `REQ-DATA-75`  
**Date:** 2026-10-19  
**Status:** APPROVED WITH CONDITIONS (Phase 1 Qualified)  
**Author:** Enterprise Product Manager & Strategic Gatekeeper  
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)  
**Child Documents:** `REQ-FED-01`, `PLAN-BUG-75-GATING`, `F-GOV-19`  
**GitHub Issue:** #75 Addendum  
**Priority:** Wave 3 (Feature Completion), Rank 3.2b  

---

## 1. Executive Summary & Problem Statement

### 1.1 The Problem
In earlier iterations of Autheris, the schema advertised cross-source GraphQL relations, but executing them failed at runtime. Under `PLAN-BUG-75-GATING` (PR #139), `RelationExecutability.IsSameExecutionSource` was introduced to suppress cross-source relations from schema generation until execution support was completed.

To support cross-source lookups, the core components `ICrossSourceRelationResolver` (`CrossSourceRelationResolver.cs`) and `ICrossSourceRowReader` (`InMemoryCrossSourceRowReader.cs`) were implemented and registered in DI (`DataApiServiceExtensions.cs:26`).

However, **`GovernedTreeQueryService` was never wired to use `ICrossSourceRelationResolver`**. Instead, lines 277-280 in `GovernedTreeQueryService.cs` still contain the hardcoded failure check:
```csharp
if (!RelationExecutability.HasSameSourceName(rootAccess.Metadata, access.Metadata))
{
    throw new GatewayInvalidQueryException("Relations across data sources are not supported in one query.");
}
```
As a result, GraphQL clients cannot traverse relations across distinct data sources (e.g. from an `Orders` table in PostgreSQL to a `Customers` table in SQL Server), even when four-eyes approvals (`AllowCrossDataSource`) and master federation switches are enabled.

### 1.2 Target Audience
- **Enterprise Application Developers:** Requiring seamless GraphQL navigation across disparate domain data sources.
- **Data Product Owners:** Linking distributed datasets without centralizing physical databases into a data warehouse.
- **Security & Compliance Officers:** Demanding strict authorization, parameter-bounded batching, and audit logging on every cross-source boundary.

### 1.3 Strategic & Architectural Fit
Autheris is an enterprise gateway designed to unify distributed enterprise databases without vendor lock-in. Delivering governed, batched cross-source relation resolution directly inside `GovernedTreeQueryService` fulfills the core promise of federated GraphQL access while maintaining strict Zero-Trust boundaries.

---

## 2. Market & Industry Benchmark

- **Apollo Federation v2:** Decomposes queries across subgraphs via `@key` and entities. However, subgraphs are independent HTTP hops incurring huge serialization overhead and lacking central row-level/masking governance.
- **Hasura DDN:** Supports remote database relationships, but requires proprietary metadata connectors and lacks WORM cryptographic audit trails.
- **OWASP Top 10 API Security (Broken Object Level Authorization):** Joining across data sources must never allow a child table to bypass tenant isolation or column masking policies applied to the parent or child.

---

## 3. User Stories & Acceptance Criteria

### US-1: Governed Cross-Source Relation Resolution in GraphQL
*As a GraphQL Client, I want to query a parent table from Data Source A and nested related records from Data Source B in a single GraphQL query, so that my frontend receives unified data without manual client-side stitching.*

- **AC-1.1:** `GovernedTreeQueryService` injects `ICrossSourceRelationResolver`.
- **AC-1.2:** When a query selects a relation where `child.SourceName != parent.SourceName`:
  1. The query tree is partitioned into the primary SQL subtree (executed against Source A) and the cross-source dependent subtree (Source B).
  2. The primary SQL query executes and returns parent rows.
  3. Parent join foreign keys are extracted and deduplicated.
  4. The child records are fetched via `ICrossSourceRelationResolver.ResolveRelationAsync` using batched key lookups.
  5. The child results are merged into the parent JSON result document before response serialization.
- **AC-1.3:** Both parent and child rows are subject to their respective table consents, tenant filters, and column masks.

### US-2: Security Opt-In & Clear-Key Guardrails (Fail-Closed)
*As a Data Owner, I want cross-source relations to fail closed unless both data owners explicitly approve the link and the join keys are not masked.*

- **AC-2.1:** Master Switch: Cross-source relations are rejected unless `Gateway:CrossSource:Enabled == true` (default `false`).
- **AC-2.2:** Four-Eyes Consent: Both tables' relation definitions must have `AllowCrossDataSource == true`. If either is `false`, the relation is omitted from schema generation and rejected at runtime.
- **AC-2.3:** Clear-Key Security (SEC-FED-01 / SEC-FED-04): The join key columns on both parent and child tables must have effective access level `Clear`. If either key is `Mask` or `Hmac` for the calling principal, execution aborts with `CROSS_SOURCE_KEY_MASKED`.

### US-3: Parameter-Bounded Batched Execution
*As a Gateway Operator, I want batched cross-source key lookups to be bounded, so that a large parent result set does not exhaust database parameter limits.*

- **AC-3.1:** Key batches are chunked according to target dialect parameter limits (e.g. 1,000 for SQL Server, 2,000 for SQLite, 5,000 for PostgreSQL).
- **AC-3.2:** Total parent rows for cross-source relations cannot exceed `MaxResponseRows`.

### US-4: Schema Exposure Alignment
*As an API Developer, I want cross-source relations to appear in the GraphQL schema only when the required opt-in conditions are satisfied.*

- **AC-4.1:** `CatalogSchemaModel.BuildAsync` updates its filter: a cross-source relation is emitted in the schema if and only if `Gateway:CrossSource:Enabled` is active and `relation.AllowCrossDataSource == true`.

---

## 4. Non-Functional Requirements (NFRs)

- **Performance & Latency:** Cross-source batching overhead must remain $< 5$ ms for batches up to 1,000 keys.
- **Memory & Allocations:** Child row merging must use streaming JSON manipulation or efficient dictionary lookups without buffering whole tables into memory.
- **Audit Logging:** Must emit dedicated audit records (`AuditEventTypes.CrossSourceFederationQuery`) recording both parent and child table identifiers, source names, and total rows stitched.

---

## 5. Security & Governance Review

- **Join-Based Oracle Prevention:** Prohibits cross-source joins where join keys are masked or filtered through differential privacy noise.
- **Tenancy Boundary:** Tenant filters are strictly injected into both primary and secondary queries.
- **No Lateral Privilege Escalation:** The caller's security context (`ClaimsPrincipal`, `TenantId`, `Sid`) is passed identically to the secondary data source lookup.

---

## 6. Implementation Scope

1. **`Autheris.Application/Sql/Tree/GovernedTreeQueryService.cs`:**
   - Inject `ICrossSourceRelationResolver`.
   - Remove hardcoded failure check; replace with tree partitioning logic.
   - Implement batched key extraction and child JSON merge.
2. **`Autheris.GraphQL/Catalog/CatalogSchemaModel.cs`:**
   - Update `RelationExecutability.IsSameExecutionSource` to inspect `Gateway:CrossSource:Enabled` and `TableRelation.AllowCrossDataSource`.
3. **Automated Tests:**
   - Integration tests executing cross-source queries between SQLite and PostgreSQL/SQL Server test fixtures.
   - Security tests verifying fail-closed rejection on masked join keys and disabled master flags.
