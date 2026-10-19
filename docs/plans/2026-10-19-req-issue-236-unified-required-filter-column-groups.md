# Requirements Specification: Issue #236 Unified Required Filter Column Groups

**Document ID:** `REQ-GOV-236`  
**Date:** 2026-10-19  
**Status:** APPROVED (P0 / Immediate Hotfix)  
**Author:** Delivery Project Manager & Product Owner (`project-manager`)  
**Parent Plan:** [`docs/plans/00-master-plan-overview.md`](00-master-plan-overview.md)  
**Target Milestone:** v1.2.5 (Immediate Patch / Hotfix)  

---

## 1. Executive Summary & Defect Classification

During security audit and regression triage of the v1.2.4 release, a **critical fail-open governance defect** was identified:
While GitHub issue #221 introduced required filter column groups (`RequiredFilterColumnGroups`) to complement #163 (`RequiredFilterColumns`), validation is currently isolated to the AST compilation pipeline (`AstSecurityVisitor.RequiredFilters.cs`) and GraphQL entity field interceptors. 

Multiple non-AST and alternate read paths bypass required filter validation entirely or only inspect flat `RequiredFilterColumns`, silently ignoring `RequiredFilterColumnGroups`:
1. **REST Single-Table Data Reads:** Endpoint handlers execute queries without validating disjunctive column groups against incoming query parameters.
2. **Typed Aggregations (`<table>_aggregate`):** `GovernedAggregationService` validates row-level security and column masking, but omits enforcement of required filter column groups, allowing unbounded aggregations across tenant partitions.
3. **Keyset Bulk Streaming (`POST /api/v1/bulk/...`):** Keyset paging streams stream table contents starting at cursor 0 without enforcing that the query contains constraints for at least one required filter column group.
4. **Federated OLAP Staging (`OlapStagingService`):** Data staged for cross-source DuckDB execution extracts tables from upstream providers without validating that filter constraints satisfy required column groups.

> [!CAUTION]
> **Defect Severity: P0 / Critical (Fail-Open Data Disclosure & Resource Exhaustion)**  
> Allowing callers to query partitioned or tenant-isolated tables without required filters permits unauthorized cross-tenant data discovery, bypasses table owner governance intent, and risks severe denial-of-service on multi-million row tables.

---

## 2. Functional Requirements

### FR-236-1: Universal Required Filter Evaluation Engine
- The gateway MUST provide a centralized domain/application service `IRequiredFilterValidator` to evaluate whether a proposed set of filter constraints satisfies a table's governance rules.
- **Evaluation Semantics:**
  - **Flat Required Columns (`RequiredFilterColumns`):** ALL declared columns MUST be constrained by explicit filter predicates or authoritative row-level policies.
  - **Column Groups (`RequiredFilterColumnGroups`):** Declared as an array of alternative conjunctive groups (e.g., `[[tenant_id], [org_id, department_id]]`). The requirement is satisfied if AT LEAST ONE column group has ALL of its member columns constrained.
  - **Combined Rules:** If a table defines both `RequiredFilterColumns` and `RequiredFilterColumnGroups`, BOTH rules must be satisfied: every flat required column must be present, AND at least one column group must be fully satisfied.
  - **Relation Join Equivalence:** If a table is joined on a column that is an equality condition against an already-constrained key (or foreign key join), that column is considered satisfied.

### FR-236-2: REST Single-Table Read Path Enforcement
- The REST read pipeline (in `GovernedQueryStages` / single-table execution pipeline) MUST invoke `IRequiredFilterValidator.Validate` before constructing or executing the SQL command.
- If required filter column groups are present in `TableCatalog` / `CatalogMetadata` and the query filter parameters fail to satisfy at least one group, the request MUST be immediately rejected with HTTP 400 Bad Request and error code `REQUIRED_FILTER_COLUMN_MISSING`.

### FR-236-3: Typed Aggregation Path Enforcement
- In `GovernedAggregationService`, before constructing the aggregate SQL query, the service MUST extract all applied filter predicates (including caller filters and active RLS filters) and evaluate them via `IRequiredFilterValidator`.
- If required filter column groups are defined on the target dataset and not satisfied, aggregation MUST be aborted with `RequiredFilterColumnMissingException` mapped to HotChocolate error code `REQUIRED_FILTER_COLUMN_MISSING`.

### FR-236-4: Bulk Keyset Stream Path Enforcement
- In `BulkStreamService` and keyset streaming endpoints, table read parameters MUST be checked against both `RequiredFilterColumns` and `RequiredFilterColumnGroups`.
- Starting or continuing a bulk stream on a table with required filters without supplying valid group constraints MUST fail closed with HTTP 400 Bad Request and error code `REQUIRED_FILTER_COLUMN_MISSING`.

### FR-236-5: Federated OLAP Staging Enforcement
- In `OlapStagingService` and federated data staging for DuckDB, any table-scan staging step MUST verify that extracted partitions satisfy required filter column groups.
- If staging queries attempt to extract an entire table without required filters, staging MUST fail closed and log an audit warning event `FEDERATED_STAGING_FILTER_VIOLATION`.

---

## 3. Non-Functional & Security Requirements

- **SEC-236-1 (Fail-Closed Default):** Under no circumstance may an unhandled exception, null collection, or unexpected AST node type bypass the filter validator. Missing or invalid filters ALWAYS result in query denial.
- **SEC-236-2 (No Information Leakage):** Error messages MUST NOT leak table schema secrets or unmasked sample data. The error message MUST clearly identify the missing required columns or column groups in a canonical, sanitized format.
- **SEC-236-3 (Audit Trail):** Every rejection due to missing required filter column groups MUST be recorded in the audit pipeline as a `DenyAccess` / `RequiredFilterMissing` security event, tagged with the caller's identity and client application.
- **NFR-236-1 (Zero Allocation in Hot Paths):** The filter validation engine MUST operate with zero or near-zero heap allocations during steady-state query processing by using `ReadOnlySpan<string>` or pooled predicate symbol collections.

---

## 4. Acceptance Criteria

1. **AC-236-1 (REST API):** A `GET /api/v1/data/{source}/{table}` request on a table requiring `[[tenant_id], [org_id, department_id]]` fails with HTTP 400 (`REQUIRED_FILTER_COLUMN_MISSING`) when called without filters, succeeds when `tenant_id` is supplied, and succeeds when both `org_id` and `department_id` are supplied.
2. **AC-236-2 (Typed Aggregation):** GraphQL query `<table>_aggregate(where: { ... })` fails with code `REQUIRED_FILTER_COLUMN_MISSING` if neither `tenant_id` nor (`org_id` AND `department_id`) is present in `where`.
3. **AC-236-3 (Bulk Stream):** `POST /api/v1/bulk/...` requests without satisfying filter groups are rejected with HTTP 400 before any database connection is acquired or cursor generated.
4. **AC-236-4 (OLAP Staging):** Staging queries attempting to copy tables into DuckDB without required filter group predicates are halted with `RequiredFilterColumnMissingException`.
5. **AC-236-5 (Full Test Suite):** 100% test pass rate across unit, integration, and security test suites with 0 warnings and 0 errors.

---

## 5. Architectural & Phase Assignment

- **Phase 1 (PM):** Intake & PRD approved (this document, `REQ-GOV-236`).
- **Phase 2 (Architecture):** Solution Architect designs `IRequiredFilterValidator`, unified evaluation engine, and injection points across REST, Aggregation, Stream, and OLAP.
- **Phase 3 (Security):** Security Expert reviews fail-closed guarantees and audit emission.
- **Phase 4 (C# TDD Developer):** Red-Green-Refactor implementation across `TrinoSqlEngine`, `Autheris.Application`, `Autheris.DataApi`, and `Autheris.GraphQL`.
- **Phase 5 (Code Review):** Code review, test verification, and merge to `main`.
