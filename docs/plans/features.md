# Feature Roadmap & Architectural Execution Plan

**Document ID:** `PLAN-FEATURES-ROADMAP`  
**Location:** `docs/plans/features.md`  
**Status:** PLANNED 📋 · blocked by Gate 0 decision G-14 (owner review)  
**Last Updated:** 2026-10-14 · **Baseline:** `main` at `a4137626`  
**Parent Plan:** [`00-master-plan-overview.md`](00-master-plan-overview.md)  
**Implementation Plan:** [`implementation/impl-features-analytics.md`](implementation/impl-features-analytics.md); global order in [`implementation/00-implementation-roadmap.md`](implementation/00-implementation-roadmap.md)  
**Scope:** Open analytics and headless BI feature streams building on the delivered AST SQL compiler (`F-DIALECT-02`), the shared governed table-read planner and the masked-aggregate guard.

---

## 1. Executive Summary

Delivered and documented in [`docs/features/`](../features/README.md): roadmap Waves 1–3 (#26, #27, #29–#35, #40–#44), the non-AST parts of Wave 4 (#36, #39, #38 Stage 1), REST dataset OFFSET pushdown (`f-data-06`) and the compiler rejection of masked columns in aggregates and `GROUP BY` (`AGGREGATION_ON_MASKED_COLUMN`).

Open: #28, #37, #38 Stage 2/3. **Before any of them starts, the owner reconciles the plans with the bodies of GitHub issues #28, #37 and #38 (G-14).**

```mermaid
flowchart TD
    G14["G-14: owner review vs issues"]
    I28["#28: Governed GraphQL Aggregations"]
    I37["#37: Minimum Group Size & K-Anonymity Guard"]
    I38["#38: dbt Semantic Layer (Stage 2/3)"]

    G14 --> I28
    I28 --> I37
    I28 --> I38
    I37 --> I38
```

---

## 2. Open Feature Tracks

### 2.1 Issue #28: Governed Aggregation in GraphQL (`count`, `sum`, `avg`, `min`, `max`)

* **Problem Statement:** Clients requiring aggregated metrics retrieve all raw rows and compute aggregations client-side, causing network and CPU exhaustion.
* **Architecture Design:**
  - Add aggregate fields to the catalog-generated GraphQL schema (`aggregate(groupBy: [...]) { count sum { column } avg { column } min { column } max { column } }`).
  - Plan through the shared table-read planner (`IGovernedTableReadPlanner`) and compile through `ISqlEngine.Compile` / `IGovernedSqlExecutionService`; do not extend `TreeSqlCompiler` (G-6).
  - Row filters (RLS / consent) apply before aggregation. Aggregation over masked columns is already rejected by the compiler (`AGGREGATION_ON_MASKED_COLUMN`).
* **Acceptance Criteria:** grouped aggregations enforce row-level security before aggregating; masked-column aggregates fail closed. **Do not release without #37.**
* **Implementation:** `WP-FEAT-03`.

### 2.2 Issue #37: Minimum Group Size & K-Anonymity Guard

* **Problem Statement:** Small aggregate groups allow re-identification.
* **Architecture Design:**
  - Configure a minimum group size per dataset or measure (e.g. `threshold = 5`); suppress or merge small groups (`HAVING COUNT(*) >= :threshold` or an `Other` bucket).
  - Optional differencing defence via `DifferentialPrivacyEngine` noise, only on compliance demand (G-12).
* **Acceptance Criteria:** groups below the threshold are suppressed or merged; response metadata reports `suppressedGroupsCount`. No k-guard on free-form SQL in v1 (G-12).
* **Implementation:** `WP-FEAT-04`, optional `WP-FEAT-05`.

### 2.3 Issue #38: dbt Semantic Layer Stage 2/3

* **Stage 2:** register the existing `list_metrics` / `describe_metric` MCP tools, make the semantic catalog tenant-aware and persistent, then add `query_metrics` for GraphQL and MCP with row filters, masks and the #37 group-size guard (`WP-FEAT-06a/b`).
* **Stage 3:** headless BI connectivity via the Trino protocol, semantic models as catalog views and a gRPC Arrow Flight SQL server (`WP-FEAT-07a/b/c`). Name the target BI clients first (G-12).
* **Component Touchpoints:** `Autheris.Extensions.Dbt`, `McpSemanticTools`, `ISqlEngine.Compile`, `IGovernedSqlExecutionService`.
* **Acceptance Criteria:** semantic metrics queryable via GraphQL and MCP with full tenant isolation and audit logging.
