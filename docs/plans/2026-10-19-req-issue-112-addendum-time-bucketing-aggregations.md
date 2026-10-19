# PRD: REQ-DATA-112B - Time-Bucketing in GraphQL Aggregations

**Document ID:** `REQ-DATA-112B`  
**Date:** 2026-10-19  
**Status:** APPROVED WITH CONDITIONS (Phase 1 Qualified)  
**Author:** Enterprise Product Manager & Strategic Gatekeeper  
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)  
**Parent Feature:** `REQ-DATA-112`, `F-DATA-08`, `REQ-GOV-163`  
**GitHub Issue:** #112 Addendum  
**Priority:** Wave 2 (Enhancement), Rank 2.6b  

---

## 1. Executive Summary & Problem Statement

### 1.1 The Problem
Issue #112 introduced typed GraphQL aggregations (`<table>_aggregate`) with differential privacy and k-anonymity. However, the current GraphQL schema only allows grouping by raw, un-transformed column names:
```graphql
query {
  telemetry_aggregate(groupBy: [device_id, status], aggregates: [{ fn: COUNT, alias: "cnt" }]) {
    groups { ... }
  }
}
```
In enterprise operational and telemetry analytics, grouping by timestamp columns requires **time-bucketing** (e.g. `HOUR`, `DAY`, `WEEK`, `MONTH`, `QUARTER`, `YEAR`) with explicit time-zone awareness (e.g. `"Europe/Berlin"`, `"America/New_York"`).

Without time-bucketing support in GraphQL aggregations:
1. Grouping by a raw timestamp column produces millions of single-row buckets, triggering `AggregationRowCapExceededException` or memory exhaustion.
2. Clients are forced to fall back to raw data exports, defeating the privacy protections of k-anonymity and differential privacy.
3. Performing time-bucketing in gateway memory is an anti-pattern that violates Autheris' zero-allocation standards.

### 1.2 Target Audience
- **Frontend & Dashboard Developers:** Visualizing time-series metrics over daily, weekly, or monthly periods.
- **Autonomous AI Agents & BI Tools:** Performing analytical trends over governed datasets without requesting raw event rows.
- **Data Governance Teams:** Preserving k-anonymity and differential privacy over temporal aggregations.

### 1.3 Strategic & Architectural Fit
Pushdown aggregation is a core differentiator for Autheris over traditional API gateways. Pushing date truncation into the underlying database engine (`date_trunc`, `DATETRUNC`, `strftime`) leverages source index pruning and minimizes network transfer.

---

## 2. Market & Industry Benchmark

- **GraphQL Foundation & Hasura DDN:** Supports date-trunc aggregation functions on timestamp columns.
- **Trino / Presto / DuckDB:** Standardizes `date_trunc(unit, timestamp)` syntax with time-zone conversion.
- **OWASP API4:2023 (Resource Consumption):** Aggregations over unbounded time intervals must be constrained by mandatory partition filters and cost caps.

---

## 3. User Stories & Acceptance Criteria

### US-1: Time-Bucketed Grouping in GraphQL Schema
*As an API Consumer, I want to group aggregate results by time buckets and time zones, so that I can render daily or monthly trend charts.*

- **AC-1.1:** The GraphQL `groupBy` argument accepts either column enum strings or structured input objects:
  ```graphql
  telemetry_aggregate(
    groupBy: [
      { column: ts, bucket: DAY, timeZone: "Europe/Berlin" },
      device_id
    ],
    aggregates: [{ fn: SUM, column: bytes_sent, alias: "total_bytes" }]
  )
  ```
- **AC-1.2:** Supported bucket intervals include: `HOUR`, `DAY`, `WEEK`, `MONTH`, `QUARTER`, `YEAR`.
- **AC-1.3:** `timeZone` argument is optional (defaults to `"UTC"`). When provided, it is validated against the closed IANA time-zone set delivered in #130 (`REQ-SQL-130`). Unrecognized time zones reject fail-closed with `INVALID_QUERY`.

### US-2: Database-Engine Pushdown of Time Truncation
*As a System Architect, I want the date truncation to execute within the source database, so that the gateway does not buffer unaggregated rows into memory.*

- **AC-2.1:** `ISqlDialectProvider` and `TreeSqlCompiler` compile time-bucketing into native dialect expressions:
  - **PostgreSQL:** `date_trunc('day', ts AT TIME ZONE 'Europe/Berlin')`
  - **SQLite:** `strftime('%Y-%m-%d', datetime(ts, '+02:00'))` (or UTC fallback)
  - **SQL Server:** `DATETRUNC(day, ts AT TIME ZONE 'Europe/Berlin')`
  - **DuckDB:** `date_trunc('day', ts AT TIME ZONE 'Europe/Berlin')`
- **AC-2.2:** The returned group `key` dictionary reflects the truncated ISO-8601 string or date value.

### US-3: Bounding by Required Filter Columns (#163)
*As a Data Owner of a multi-billion row table, I want aggregations to fail if required partition filter columns are missing, so that aggregate queries do not cause cluster-wide table scans.*

- **AC-3.1:** If the target table defines `RequiredFilterColumns` (delivered in `REQ-GOV-163`), the aggregation query's `where` argument must constrain the required column(s).
- **AC-3.2:** If the required column is omitted by the client and not satisfied by an active row policy, execution aborts with `400 REQUIRED_FILTER_MISSING`.

### US-4: Cardinality & Cost Admission
*As a Gateway Operator, I want time-bucket queries with excessive intervals to be rejected at query analysis time.*

- **AC-4.1:** Static query cost calculation factors in requested time buckets:
  - If a filter specifies a 1-year range and the bucket is `HOUR`, estimated cardinality is 8,760 groups. If this exceeds `MaxGroups` (default 100, max 1,000), the query is rejected during admission with `QUERY_TOO_COMPLEX`.

---

## 4. Non-Functional Requirements (NFRs)

- **Performance & Allocations:** AST rewriting for date truncation must generate zero heap allocations per row; all processing occurs in dialect SQL generation and stream mapping.
- **Privacy Protections:** K-anonymity minimum group size and differential privacy Laplace/Gaussian noise (F-DATA-08) remain enforced on time-bucketed groups. Small buckets below `minimumGroupSize` are suppressed or merged into `OTHER`.

---

## 5. Security & Governance Review

- **SQL Injection Defense:** Time zone strings and bucket enum names must never be interpolated directly into raw SQL. Dialects must use parameterized or strictly whitelisted identifier mapping.
- **Masking Interoperability:** If a timestamp column has column access `Mask` or `Deny`, time-bucketing on that column is prohibited and fails closed with `403 ACCESS_DENIED`.

---

## 6. Implementation Scope

1. **`Autheris.GraphQL/Catalog/AggregateGraphQlTypes.cs`:**
   - Define `TimeBucket` enum and `TimeBucketGroupByInput` input object type.
   - Update `groupBy` argument descriptor to accept union or structured input.
2. **`Autheris.Application/Analytics/`:**
   - Update `AggregationQuery` and `TableAggregateIntent` to carry `TimeBucketSpecification`.
   - Update `AggregationQueryPlanner` to emit dialect-specific date truncation.
3. **Automated Tests:**
   - Dialect golden tests verifying SQL output across PostgreSQL, SQLite, SQL Server, and DuckDB.
   - Integration tests verifying k-anonymity suppression on low-volume time buckets.
