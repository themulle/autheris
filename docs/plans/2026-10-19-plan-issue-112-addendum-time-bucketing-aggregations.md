# PLAN-DATA-112B: Time-Bucketing in Governed GraphQL Aggregations

**Document ID:** `PLAN-DATA-112B`  
**Date:** 2026-10-19  
**Status:** APPROVED WITH CONDITIONS (Phase 3 Security Reviewed)  
**Author:** Solution Architect (`csharp-architect`)  
**Input PRD:** [`docs/plans/2026-10-19-req-issue-112-addendum-time-bucketing-aggregations.md`](2026-10-19-req-issue-112-addendum-time-bucketing-aggregations.md)  
**Parent Plan:** [`docs/plans/00-master-plan-overview.md`](00-master-plan-overview.md)  
**Parent Feature:** `REQ-DATA-112`, `F-DATA-08`, `REQ-GOV-163`, `REQ-SQL-130`  
**GitHub Issue:** #112 Addendum  
**Priority:** Wave 2 (Enhancement), Rank 2.6b  

---

## 1. Executive Summary & Problem Context

Issue #112 delivered typed pushdown aggregations (`<table>_aggregate`) with k-anonymity and differential privacy. However, grouping was constrained strictly to raw, un-transformed column identifiers. When querying timestamp fields (e.g. `created_at`, `event_time`, `logged_at`), grouping by raw values produces millions of distinct microsecond buckets. This immediately triggers `AggregationRowCapExceededException` or memory exhaustion, forcing clients to circumvent governed aggregations by exporting raw data.

To enable operational time-series analytics while strictly preserving zero-allocation gateway performance and Zero-Trust privacy boundaries, Autheris must support **governed time-bucketing** pushed directly down into the source database engine with explicit time-zone awareness.

### Core Architectural Goals
1. **Expressive GraphQL Schema:** Extend the `groupBy` argument to accept either plain field identifiers or structured time-bucketing input objects (`{ column, bucket, timeZone }`).
2. **Database Engine Pushdown:** Translate time bucketing into native dialect date truncation (`date_trunc`, `DATETRUNC`, `strftime`) to leverage database indexes and eliminate gateway-side row buffering.
3. **Strict Time Zone Security:** Validate time zones against the closed IANA registry (`TimeZoneRegistry` from REQ-SQL-130) to eliminate SQL injection vectors.
4. **Partition Filter Bounding (#163):** Enforce mandatory partition column filters (`RequiredFilterColumns`) even when time bucketing is specified, preventing unbounded table scans.
5. **Static Cardinality & Cost Admission:** Calculate estimated group cardinality from filter date bounds and bucket intervals; fail closed with `QUERY_TOO_COMPLEX` if estimated buckets exceed configured caps.
6. **Privacy Invariant Preservation:** Maintain k-anonymity (k=3 or table threshold) and differential privacy noise across time-bucketed groups.

---

## 2. Verified Code Facts & Delta Analysis

1. **`AggregateGraphQlTypes.cs` (`src/Autheris.GraphQL/Catalog/AggregateGraphQlTypes.cs`):**
   - Lines 90–98 register enum `<table>_group_by_fields`.
   - Line 161 binds the argument: `.Argument("groupBy", a => a.Type(new ListTypeNode(NonNull(names.Column))))`.
   - Must register `TimeBucketInterval` enum (`HOUR`, `DAY`, `WEEK`, `MONTH`, `QUARTER`, `YEAR`) and input object `<table>_time_bucket_input`.
   - Must extend `groupBy` argument to accept `[<table>_group_by_item_input!]`.
2. **`AggregateArgumentParser.cs` (`src/Autheris.GraphQL/Catalog/AggregateArgumentParser.cs`):**
   - Lines 71–100 implement `ParseGroupBy`: expects `EnumValueNode` or `StringValueNode`.
   - Must be updated to inspect each list element: if `ObjectValueNode`, parse structured time bucket (`column`, `bucket`, `timeZone`); if `EnumValueNode`/`StringValueNode`, parse as plain column identifier.
3. **`AggregationModels.cs` (`src/Autheris.Application/Analytics/AggregationModels.cs`):**
   - `AggregationQuery` currently carries `IReadOnlyList<string>? GroupBy`.
   - Must define:
     ```csharp
     public enum TimeBucketInterval { Hour = 0, Day = 1, Week = 2, Month = 3, Quarter = 4, Year = 5 }
     public sealed record TimeBucketSpecification(string Column, TimeBucketInterval Interval, string TimeZone = "UTC");
     public sealed record GroupByClause(string? ColumnName, TimeBucketSpecification? TimeBucket = null)
     {
         public string KeyName => TimeBucket != null ? TimeBucket.Column : ColumnName!;
     }
     ```
   - Update `AggregationQuery` to carry `IReadOnlyList<GroupByClause> GroupByItems`.
4. **`GovernedTableReadPlanner.cs` (`src/Autheris.Application/Data/Services/GovernedTableReadPlanner.cs`):**
   - `PlanAggregate` builds Trino SQL for `IGovernedSqlExecutionService`.
   - For time-bucketed items, emit `date_trunc('unit', convert_timezone('UTC', 'targetZone', col))` or dialect-equivalent expressions in `SELECT` and `GROUP BY`.
5. **`TrinoSqlEngine` & `TimeZoneRegistry.cs`:**
   - Already contains complete IANA-to-Windows mapping, `Contains(ianaId)`, and `TryResolve(dialect, ianaId, out dialectValue)`.
   - Date truncation is already supported across dialects in `SqlDialectGeneratorBase.Functions.cs`.

---

## 3. Architectural Design

```
+----------------------------------------------------------------------------------------------------+
|                                    GraphQL Aggregation Query                                       |
|                                                                                                    |
|  telemetry_aggregate(                                                                              |
|    groupBy: [                                                                                      |
|      { column: ts, bucket: DAY, timeZone: "Europe/Berlin" },                                       |
|      status                                                                                        |
|    ],                                                                                              |
|    where: { ts: { gte: "2026-10-01T00:00:00Z", lt: "2026-10-15T00:00:00Z" } },                    |
|    aggregates: [{ fn: COUNT, alias: "cnt" }]                                                       |
|  )                                                                                                 |
+----------------------------------------------------------------------------------------------------+
                                                |
                                                v
+----------------------------------------------------------------------------------------------------+
|                               AggregateArgumentParser.Parse                                        |
|                                                                                                    |
|  1. Validate Column Existence & Timestamp Data Type (ts is datetime/timestamp)                     |
|  2. Validate TimeZone via TimeZoneRegistry.Contains("Europe/Berlin") (Fail-Closed)                 |
|  3. Validate Column Access != Mask / Deny (Fail-Closed: MaskedAggregationException)                |
+----------------------------------------------------------------------------------------------------+
                                                |
                                                v
+----------------------------------------------------------------------------------------------------+
|                              Cardinality & Cost Admission Check                                    |
|                                                                                                    |
|  - Date Range: 14 days; Interval: DAY -> Estimated Buckets: 14                                    |
|  - Check 14 <= MaxGroups (100) -> PASS                                                             |
|  - Verify #163 Required Filter Columns are satisfied in where clause -> PASS                       |
+----------------------------------------------------------------------------------------------------+
                                                |
                                                v
+----------------------------------------------------------------------------------------------------+
|                          GovernedTableReadPlanner -> Native SQL Pushdown                           |
|                                                                                                    |
|  Postgres:                                                                                         |
|    SELECT date_trunc('day', ts AT TIME ZONE 'Europe/Berlin') AS ts, status, COUNT(*) AS __n,      |
|           COUNT(*) AS cnt                                                                          |
|    FROM telemetry                                                                                  |
|    WHERE ts >= '2026-10-01' AND ts < '2026-10-15'                                                 |
|    GROUP BY date_trunc('day', ts AT TIME ZONE 'Europe/Berlin'), status                            |
|                                                                                                    |
|  SQL Server:                                                                                       |
|    SELECT DATETRUNC(day, ts AT TIME ZONE 'W. Europe Standard Time') AS ts, status, ...             |
+----------------------------------------------------------------------------------------------------+
                                                |
                                                v
+----------------------------------------------------------------------------------------------------+
|                           Privacy Post-Processing & Anonymization                                  |
|                                                                                                    |
|  - Apply K-Anonymity (k >= 3): groups with count < 3 merged into 'OTHER' or suppressed             |
|  - Apply Differential Privacy noise if configured (Laplace / Gaussian sampler)                     |
|  - Return ISO-8601 formatted date strings in GraphQL response key                                  |
+----------------------------------------------------------------------------------------------------+
```

### Architectural Decisions

- **Decision D-112B.1: Structured Input Object for GroupBy Items**
  - Define `TimeBucketInterval` enum: `HOUR`, `DAY`, `WEEK`, `MONTH`, `QUARTER`, `YEAR`.
  - For each table, define `<table>_time_bucket_input` containing:
    - `column: <table>_timestamp_fields!`
    - `bucket: TimeBucketInterval!`
    - `timeZone: String` (defaults to `"UTC"`)
  - Define `<table>_group_by_item_input` containing:
    - `field: <table>_group_by_fields`
    - `timeBucket: <table>_time_bucket_input`
  - For backwards compatibility, `AggregateArgumentParser` also accepts bare enum values in the list.
- **Decision D-112B.2: Closed-Set TimeZone Validation (Anti-SQLi)**
  - All timeZone string arguments MUST be validated through `TrinoSqlEngine.TimeZoneRegistry.Contains(timeZone)` before SQL generation.
  - Any timeZone string failing validation aborts immediately with `SqlTimeZoneException` / HTTP 400 `INVALID_TIME_ZONE`. No user string is ever interpolated directly into SQL.
- **Decision D-112B.3: Engine-Native Pushdown via Trino SQL & AST**
  - In `GovernedTableReadPlanner`: emit `date_trunc('unit', col)` or `date_trunc('unit', convert_timezone('UTC', timeZone, col))` as standard Trino AST.
  - The downstream `GovernedSqlCompiler` translates this into the dialect's native syntax (`date_trunc` + `AT TIME ZONE` in PostgreSQL/DuckDB, `DATETRUNC` + Windows Zone in SQL Server, `strftime` in SQLite).
- **Decision D-112B.4: Strict Adherence to Required Filter Columns (#163)**
  - Even if a timestamp column is the target of time bucketing, if the table metadata declares `RequiredFilterColumns`, the query's `where` clause MUST contain a valid predicate bounding the required column(s).
  - Time-bucketing does not satisfy a partition constraint; only explicit `where` boundaries do.

---

## 4. Work Packages & Component Specifications

### WP-112B-1: GraphQL Schema Types Extension
- In `src/Autheris.GraphQL/Catalog/AggregateGraphQlTypes.cs`:
  - Register shared `TimeBucketInterval` enum in `RegisterShared`: `HOUR`, `DAY`, `WEEK`, `MONTH`, `QUARTER`, `YEAR`.
  - In `RegisterTable`:
    - Filter columns of `DateTime` / `Date` types into `<table>_timestamp_fields` enum.
    - Register `<table>_time_bucket_input` input object type (`column`, `bucket`, `timeZone`).
    - Register `<table>_group_by_item_input` input object type.
    - Update `groupBy` argument descriptor on the aggregate query field to accept `[<table>_group_by_item_input!]`.

### WP-112B-2: Argument Parsing & Time Zone Validation
- In `src/Autheris.GraphQL/Catalog/AggregateArgumentParser.cs`:
  - Enhance `ParseGroupBy` to handle both `ObjectValueNode` and scalar enum/string nodes.
  - If `timeBucket` object is provided:
    - Validate that `column` is a valid timestamp column of the table.
    - Parse `bucket` interval.
    - Extract `timeZone` string (defaulting to `"UTC"` if absent).
    - Validate `TimeZoneRegistry.Contains(timeZone)`; reject unknown or malformed time zones with `AggregateErrors.ArgumentInvalid("Invalid timeZone specified.")`.
  - Validate that the timestamp column access level is `ColumnAccessLevel.Clear` (fail closed if `Mask` or `Deny`).

### WP-112B-3: Application Model & Planner Pushdown
- In `src/Autheris.Application/Analytics/AggregationModels.cs`:
  - Define `TimeBucketInterval`, `TimeBucketSpecification`, and `GroupByClause`.
  - Update `AggregationQuery` and `TableAggregateIntent`.
- In `src/Autheris.Application/Data/Services/GovernedTableReadPlanner.cs`:
  - Update `PlanAggregate`:
    - For each `GroupByClause` with `TimeBucket`:
      - Emit `date_trunc` expression in `selectItems` with an alias matching the column name.
      - Emit corresponding `date_trunc` expression in `GROUP BY` clause.
    - Ensure `EnsureRequiredFilterColumnsSatisfied` continues to validate `intent.Filter`.

### WP-112B-4: Static Cardinality & Cost Admission Check
- In `src/Autheris.Application/Analytics/AggregationCostCalculator.cs`:
  - Add `EstimateTimeBucketCardinality(TableFilterClause? where, TimeBucketSpecification timeBucket)`:
    - Inspect `where` clause for lower and upper bounds on the time bucket column.
    - Compute interval duration / bucket step (e.g. 365 days / 1 hour = 8,760 buckets).
    - If bounds are missing or estimated buckets exceed `options.Aggregation.MaxGroups` (default 100, max 1,000), reject with `QUERY_TOO_COMPLEX` before touching the database.

### WP-112B-5: Dialect Golden Tests & Privacy Verification
- Add golden tests for SQL generation across PostgreSQL, SQL Server, DuckDB, and SQLite.
- Verify k-anonymity suppression: when low-volume time buckets have row counts $< 3$, verify they are suppressed or merged into `OTHER`.
- Verify DP noise addition across time buckets.

---

## 5. Non-Functional Requirements & Performance

- **Zero Gateway Allocation:** Date truncation execution is 100% pushed down into the source database. Gateway memory allocation per row is zero.
- **Index Friendly:** Pushing native `date_trunc` / `DATETRUNC` allows the source query optimizer to leverage date partition pruning.
- **Response Format:** Truncated dates in the output `key` dictionary are serialized as standardized ISO-8601 strings (`yyyy-MM-ddTHH:mm:ssZ` or `yyyy-MM-dd`).

---

## 6. Verification Checklist

- [ ] GraphQL schema introspection exposes `TimeBucketInterval` and `<table>_group_by_item_input`.
- [ ] Queries grouping by raw columns continue to function without regression.
- [ ] Time-bucketed queries execute native `date_trunc` in PostgreSQL, `DATETRUNC` in SQL Server, and `strftime` in SQLite.
- [ ] Invalid time zones (e.g. `"Invalid/Zone"`, `"; DROP TABLE; --"`) fail closed with HTTP 400 `INVALID_QUERY`.
- [ ] Timestamp columns with `ColumnAccessLevel.Mask` fail closed with `AGGREGATION_ON_MASKED_COLUMN`.
- [ ] Omission of required partition filter columns (#163) fails closed with `REQUIRED_FILTER_MISSING`.
- [ ] Unbounded time queries with excessive estimated buckets fail admission with `QUERY_TOO_COMPLEX`.
- [ ] K-anonymity (k=3) correctly merges or suppresses low-cardinality time buckets.

---

## 7. Phase 3 Security Review & Binding Conditions (`csharp-security-expert`)

**Review Date:** 2026-10-19  
**Verdict:** APPROVED WITH CONDITIONS  
**Reviewer:** Application Security Expert (`csharp-security-expert`)

### Binding Security Conditions

1. **`SEC-DATA112B-01`: Closed-Set TimeZone & Dialect Injection Defense**
   - The `timeZone` parameter MUST be strictly validated against closed-set IANA / `TrinoSqlEngine.TimeZoneRegistry.Contains`.
   - Unknown, malformed, or arbitrary strings MUST fail closed immediately with HTTP 400 / `INVALID_ARGUMENT` (`AggregateErrors.ArgumentInvalid`).
   - User-supplied time zone strings MUST NEVER be concatenated directly into generated SQL. Dialect generators MUST only emit verified registry-canonical identifier values or mapped Windows time zones.
2. **`SEC-DATA112B-02`: Required Filter Columns Bounding (#163 Partition Enforcement)**
   - Time-bucketing aggregations MUST NOT bypass `RequiredFilterColumns`. If table metadata declares required partition columns, the query's `where` clause MUST contain explicit bounding predicates satisfying those partition columns.
   - If required partition filters are missing or empty, execution MUST fail closed with `REQUIRED_FILTER_MISSING` prior to AST compilation or database execution.
3. **`SEC-DATA112B-03`: Cardinality & Denial-of-Service Defense**
   - High-resolution bucketing (e.g. `HOUR` or `DAY` over large or unbounded intervals) MUST be bounded by static cardinality estimation in `AggregationCostCalculator`.
   - Any time-bucket query lacking timestamp range filters or producing an estimated bucket count exceeding configured caps (`options.Aggregation.MaxGroups`, default 100, hard ceiling 1,000) MUST fail closed with `QUERY_TOO_COMPLEX` before touching the database engine.
4. **`SEC-DATA112B-04`: Differential Privacy & K-Anonymity Invariance**
   - K-anonymity (k=3 or configured table threshold), low-count suppression / `OTHER` binning, and Differential Privacy Laplace/Gaussian noise MUST be applied across every bucketed group identically.
   - Low-count time buckets MUST NOT leak precise timestamp existence; buckets with count $< k$ must be suppressed or bucket-merged to prevent timing re-identification attacks.
5. **`SEC-DATA112B-05`: Masked Column Prohibition in Time-Bucketing (Zero-Trust Anti-Oracle)**
   - If a timestamp column has access level `Mask`, `Hmac`, or `Deny` under the caller's active consent or profile, time-bucketing on that column MUST fail closed with `AGGREGATION_ON_MASKED_COLUMN` (`MaskedAggregationException`).
   - Grouping or truncating masked timestamps creates an inference side-channel that leaks date ranges.

### Mandatory Security Test Suite (`[Trait("Category", "Security")]`)
- `[Trait("Category", "Security")] T_SEC_DATA112B_01_TimeZoneInjectionRejected`: Verifies that timeZone strings containing SQL injection tokens, unmapped time zones, or shell escapes fail closed with `INVALID_ARGUMENT`.
- `[Trait("Category", "Security")] T_SEC_DATA112B_02_RequiredFilterPartitionEnforced`: Verifies that querying a table with `RequiredFilterColumns` without explicit bounding predicates in `where` fails closed with `REQUIRED_FILTER_MISSING`.
- `[Trait("Category", "Security")] T_SEC_DATA112B_03_ExcessiveBucketCardinalityRejected`: Verifies that queries requesting high-resolution bucketing over large unbounded ranges fail admission with `QUERY_TOO_COMPLEX`.
- `[Trait("Category", "Security")] T_SEC_DATA112B_04_PrivacyInvarianceUnderBucketing`: Verifies that k-anonymity suppression (k=3) and DP noise are strictly applied to all output time buckets.
- `[Trait("Category", "Security")] T_SEC_DATA112B_05_MaskedTimestampBucketFailsClosed`: Verifies that time-bucketing on a masked timestamp column raises `MaskedAggregationException` and aborts.
