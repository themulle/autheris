# PRD: REQ-GOV-175 - Query Analysis for Read-Only Tokens with the Execution Cost Model

**Document ID:** `REQ-GOV-175`  
**Date:** 2026-10-18  
**Status:** PROPOSED (Phase 1 qualified)  
**Author:** Enterprise Product Manager  
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)  
**GitHub Issue:** #175  

---

## 1. Executive Summary & Problem Statement

Clients holding read-only tokens (such as AI agents, query-linter tools, or IDE plugins evaluating query safety before execution) need to determine whether Autheris will accept a given GraphQL query and what its execution cost is, without executing queries against source databases or reading rows.

Today, `POST /api/v1/governance/queries/analyze` (#79) has four critical limitations:
1. **Forbidden to read-only tokens:** `ReadOnlyTokenMiddleware` blocks `/analyze` with `403 READ_ONLY_TOKEN`.
2. **Model drift (divergent cost models):** The analysis currently uses a rough heuristic (`fields * CostPerField + depth * CostPerJoin`), whereas runtime execution enforces the strict AST visitor `QueryCostAnalyzerRule` (pagination limits via `first`, list multipliers, children per row, variable inputs, and masking surcharges). A query can pass analysis and fail execution, or vice-versa.
3. **No per-root-field breakdown:** Analysis outputs a single global complexity number rather than per-root-field cost.
4. **GraphQL field names vs. table names:** Analysis historically resolves against backend table names rather than exposed GraphQL schema names.

---

## 2. Architectural Decision: Reuse over Approximation

> [!IMPORTANT]
> **Approximation Anti-Pattern Rejected:** Building a separate heuristic or estimator introduces constant drift, false-positives, and maintenance overhead.
> **Approved Solution:** Directly reuse the Hot Chocolate `QueryCostAnalyzerRule.CalculateCost(...)` engine in-process over the query `DocumentNode`. This delivers:
> - **100% Exact Cost Parity:** `costEstimate == calculatedCost`.
> - **Zero Database Reads & Zero Token Deductions:** Only evaluates the parsed AST in memory without invoking resolvers.
> - **Microsecond Performance:** High-performance, zero-allocation AST analysis.

---

## 3. Scope & Phased Implementation

### Phase A (Core & Endpoint Enablement - S/M):
- **Read-Only Authorization:** Update `ReadOnlyTokenMiddleware.cs` to allow `POST /api/v1/governance/queries/analyze` for callers holding `ReadOnlyScopes`.
- **Exact Execution Cost Engine:** In `QueryAnalysisService.GraphQl.cs`, delegate GraphQL cost calculation directly to `QueryCostAnalyzerRule.CalculateCost(document, schema, variables)`.
- **Top-Level Root Field Costs:** Loop over top-level `FieldNode` selections in the operation and compute cost per root field via `CalculateCost`.
- **Parity Guarantees:** Ensure queries exceeding `maxAllowedCost` or containing illegal filters/orderings on masked columns return matching diagnostics/reason codes.

### Phase B (Consumer Tooling - S, Future Wave):
- Expose the analysis capability as an MCP tool (e.g. `analyze_query`) and/or a schema field on `Query`.

---

## 4. Acceptance Criteria (Done When)

- **AC-1:** A token whose scopes are all read-only receives HTTP 200 from `POST /api/v1/governance/queries/analyze` without 403 rejection.
- **AC-2:** For test queries covering list factors, nested lists, and variables, `costEstimate` per root field matches the `calculatedCost` produced by `QueryCostAnalyzerRule`.
- **AC-3:** If a query would be rejected at runtime by `QueryCostAnalyzerRule` or column mask policies, the analysis returns the identical rejection reason code.
- **AC-4:** Analysis executes with 0 source database reads and 0 quota token deductions.
- **AC-5:** No regression on existing SQL / federated analysis routes.
