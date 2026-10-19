# PLAN-GOV-39: GraphQL Pagination Truncation Signal & Root Keyset Cursor Guidance

**Document ID:** `PLAN-GOV-39`  
**Date:** 2026-10-19  
**Status:** APPROVED WITH CONDITIONS (Phase 3 Security Reviewed)  
**Author:** Solution Architect (`csharp-architect`)  
**Input PRD:** [`docs/plans/2026-10-19-req-issue-39-graphql-pagination-truncation-signal.md`](2026-10-19-req-issue-39-graphql-pagination-truncation-signal.md)  
**Parent Plan:** [`docs/plans/00-master-plan-overview.md`](00-master-plan-overview.md)  
**GitHub Issue:** #39 Addendum  
**Priority:** Wave 1 (Bug / Correctness), Rank 1.11  

---

## 1. Executive Summary & Problem Context

In Autheris GraphQL, `PaginationTruncationMiddleware` is registered in the Hot Chocolate execution pipeline to inspect `context.ContextData["PaginationTruncated"]` and attach `extensions.pagination: { isTruncated: true, maxRowsReached: true }`.

However, code verification reveals that **no component in `src/` ever writes to `"PaginationTruncated"`**. When client queries omit a limit or request large pages, `CatalogGraphQlTypeModule` and `GovernedTreeQueryService` clamp or slice results at `MaxResponseRows` (default 1,000 / 5,000). The query completes with HTTP 200 containing partial data, yet `extensions.pagination` is completely missing. Downstream systems, sync jobs, and autonomous AI agents interpret the partial array as complete, causing silent data loss and sync drift.

Furthermore, root table fields lack informative pagination guidance in the generated schema, leaving callers unaware of key-set cursor paging patterns on primary keys (`TableKeyDefinition`) or offset boundaries (`MaxAllowedOffset`).

### Core Architectural Goals
1. **Pipeline Truncation Signaling:** Reliably set `ctx.ContextData[PaginationTruncationMiddleware.ContextKey] = true` and `ctx.ContextData[PaginationTruncationMiddleware.MaxRowsReachedKey] = true/false` in `CatalogGraphQlTypeModule.ResolveRootQueryAsync` whenever database results meet or exceed gateway caps or client limits.
2. **Lookahead Slicing Support:** When a client provides `first: N`, execute with lookahead $N + 1$ (up to `MaxResponseRows + 1`); if $N + 1$ rows return, slice the list to $N$ items and set `isTruncated = true`, `maxRowsReached = false` (or `true` if $N \ge \text{MaxResponseRows}$).
3. **Response Extension Enrichment:** Update `PaginationTruncationMiddleware` to emit a standardized `extensions.pagination` dictionary containing `isTruncated`, `maxRowsReached`, and `pageSize`.
4. **Schema Guidance & Offset Enforcement:** Enrich GraphQL root query field descriptions with explicit keyset pagination syntax guidance and reject requests exceeding `MaxAllowedOffset` with a structured `INVALID_QUERY` error.
5. **Zero-Allocation Hot Path:** Access and update `IResolverContext.ContextData` and `RequestContext.ContextData` without unnecessary dictionary copies or boxing.

---

## 2. Verified Code Facts & Delta Analysis

1. **`PaginationTruncationMiddleware` (`src/Autheris.GraphQL/Interceptors/PaginationTruncationMiddleware.cs`):**
   - Declares `public const string ContextKey = "PaginationTruncated";`.
   - Inspects `context.ContextData.TryGetValue(ContextKey, out var truncatedObj) && truncatedObj is true`.
   - Hardcodes `["isTruncated"] = true, ["maxRowsReached"] = true`.
   - Needs extension keys: `public const string MaxRowsReachedKey = "PaginationMaxRowsReached";` and `public const string PageSizeKey = "PaginationPageSize";`.
2. **`CatalogGraphQlTypeModule` (`src/Autheris.GraphQL/Catalog/CatalogGraphQlTypeModule.cs`):**
   - Lines 261-268 define root fields:
     ```csharp
     d.Field(table.QueryFieldName)
         .Description($"Query {table.TypeName}. Keyset paging can be performed via where: {{ id: {{ gt: $lastId }} }}, orderBy: [{{ id: ASC }}].")
     ```
     Primary key fields in `table.Metadata.Key` or `table.Key` are available to render exact column names rather than generic `id`.
   - Lines 542-566 in `ResolveRootQueryAsync`:
     - Reads `doc.RootElement` and parses items into `List<JsonElement>`.
     - Updates `ctx.ContextData["AutherisRowsReturned"]`.
     - Completely omits setting `PaginationTruncated` or `PaginationMaxRowsReached`.
3. **`GraphQlTreeBuilder` & `TreeSqlCompiler`:**
   - `GraphQlTreeBuilder.BuildTree` validates `depth <= 10` and parses `first` (default 100) and `offset` (default 0).
   - If `offset > maxAllowedOffset`, throws `GatewayInvalidQueryException`.
   - `TreeSqlCompiler.Compile` translates `node.Limit` into SQL `LIMIT` / `TOP`. Slicing lookahead requires compiling root limit as $\min(\text{requestedLimit} + 1, \text{MaxResponseRows} + 1)$ when pagination lookahead is active.

---

## 3. Architectural Design

```
+---------------------------------------------------------------------------------------------------+
|                                 Hot Chocolate Execution Pipeline                                  |
|                                                                                                   |
|  [PaginationTruncationMiddleware.InvokeAsync]                                                     |
|    |                                                                                              |
|    +--> await _next(context)                                                                      |
|           |                                                                                       |
|           v                                                                                       |
|  [CatalogGraphQlTypeModule.ResolveRootQueryAsync]                                                 |
|    |                                                                                              |
|    +--> GraphQlTreeBuilder: sets root limit = requestedLimit + 1 (capped at maxResponseRows + 1) |
|    +--> GovernedTreeQueryService.ExecuteAsync: runs query                                         |
|    +--> Slicing & Detection:                                                                      |
|    |      if (fetchedRows > requestedLimit || fetchedRows >= maxResponseRows)                     |
|    |      {                                                                                       |
|    |          list = list.Take(requestedLimit);                                                   |
|    |          lock (ctx.ContextData) {                                                            |
|    |              ctx.ContextData["PaginationTruncated"] = true;                                  |
|    |              ctx.ContextData["PaginationMaxRowsReached"] = (fetchedRows >= maxResponseRows); |
|    |              ctx.ContextData["PaginationPageSize"] = list.Count;                             |
|    |          }                                                                                   |
|    |      }                                                                                       |
|    +--> return list                                                                               |
|           |                                                                                       |
|    <------+ (Execution completes)                                                                 |
|    |                                                                                              |
|    v                                                                                              |
|  [PaginationTruncationMiddleware Post-Execution Check]                                            |
|    |                                                                                              |
|    +--> Checks context.ContextData["PaginationTruncated"] == true                                 |
|    +--> opResult.Extensions.SetItem("pagination", { isTruncated, maxRowsReached, pageSize })      |
|    +--> HTTP 200 payload returned to client with unambiguous extensions.pagination                |
+---------------------------------------------------------------------------------------------------+
```

### Architectural Decisions

- **Decision D-39.1: Lookahead Querying vs. Exact Count**
  - Querying `LIMIT N + 1` is an industry-standard Relay/GraphQL optimization requiring zero secondary `COUNT(*)` DB queries.
  - Slicing occurs in memory before returning `IReadOnlyList<JsonElement>`:
    ```csharp
    var isTruncated = false;
    var maxRowsReached = false;
    var requestedLimit = queryNode.Limit;
    if (list.Count > requestedLimit)
    {
        isTruncated = true;
        maxRowsReached = requestedLimit >= maxResponseRows;
        list.RemoveAt(list.Count - 1);
    }
    else if (list.Count >= maxResponseRows)
    {
        isTruncated = true;
        maxRowsReached = true;
    }
    ```
- **Decision D-39.2: Thread-Safe Context Propagation**
  - Hot Chocolate's `IResolverContext.ContextData` shares state with `RequestContext.ContextData`. Access is protected via `lock (ctx.ContextData)` consistent with existing Autheris patterns (`AutherisOperationId`, `AutherisRowsReturned`).
- **Decision D-39.3: Dynamic Keyset Pagination Guidance**
  - When declaring root fields, if `table.Key` (or primary key column) is defined (e.g. `order_id`), use the actual primary key in description:
    `"Query {table.TypeName}. Keyset paging can be performed via where: {{ {pk}: {{ gt: $last{pk} }} }}, orderBy: [{{ {pk}: ASC }}]. Offset pagination is supported up to offset {maxAllowedOffset}."`

---

## 4. Work Packages

### WP-39-1: Lookahead Query AST & Slicing in `CatalogGraphQlTypeModule`
- Update `GraphQlTreeBuilder.BuildNode` for root queries:
  - Keep `queryNode.Limit` as the requested limit $N$.
  - Provide an internal lookahead execution limit $N + 1$ (bounded by `maxResponseRows + 1`) to `GovernedTreeQueryService`.
- In `ResolveRootQueryAsync`:
  - Enumerate `root.EnumerateArray()`.
  - Check if `list.Count > requestedLimit`: truncate the trailing item, set `isTruncated = true`.
  - Check if `list.Count >= maxResponseRows`: set `isTruncated = true`, `maxRowsReached = true`.
  - Store metadata in `ctx.ContextData`:
    - `PaginationTruncationMiddleware.ContextKey` (`"PaginationTruncated"`) $\to$ `true`.
    - `PaginationTruncationMiddleware.MaxRowsReachedKey` (`"PaginationMaxRowsReached"`) $\to$ `bool`.
    - `PaginationTruncationMiddleware.PageSizeKey` (`"PaginationPageSize"`) $\to$ `int`.

### WP-39-2: Response Extension Formatting in `PaginationTruncationMiddleware`
- Add constants:
  - `public const string MaxRowsReachedKey = "PaginationMaxRowsReached";`
  - `public const string PageSizeKey = "PaginationPageSize";`
- In `InvokeAsync`:
  - Retrieve flags from `context.ContextData`.
  - Construct payload:
    ```csharp
    var paginationInfo = new Dictionary<string, object?>
    {
        ["isTruncated"] = true,
        ["maxRowsReached"] = context.ContextData.TryGetValue(MaxRowsReachedKey, out var maxObj) && maxObj is true,
        ["pageSize"] = context.ContextData.TryGetValue(PageSizeKey, out var sizeObj) && sizeObj is int sz ? sz : 0
    };
    opResult.Extensions = extensions.SetItem("pagination", paginationInfo);
    ```

### WP-39-3: Keyset Documentation & Offset Bounds
- In `CatalogGraphQlTypeModule.cs`:
  - Resolve primary key name from `table.Key` or default to `id`.
  - Generate schema field descriptions explaining keyset pagination with where filters and ordering.
  - In `GraphQlTreeBuilder.cs`, ensure `offset > maxAllowedOffset` throws `GatewayInvalidQueryException` with informative remediation message.

### WP-39-4: Audit Event Integration
- In `AuditLogging` record for GraphQL queries (`AuditEventTypes.QueryExecution`):
  - Include `truncated = isTruncated` in the audit details JSON to support FinOps reporting.

### WP-39-5: Unit & Integration Verification
- Unit test `PaginationTruncationMiddleware` with and without context keys.
- Integration test executing root queries with:
  1. Exactly matching page size without further rows (isTruncated = false).
  2. Page size where extra rows exist (isTruncated = true, maxRowsReached = false).
  3. Query hitting `MaxResponseRows` ceiling (isTruncated = true, maxRowsReached = true).
  4. Offset exceeding `MaxAllowedOffset` returning `INVALID_QUERY`.

---

## 5. Non-Functional Requirements & Performance

- **Zero DB Round-Trips:** Lookahead +1 adds 0 additional database queries.
- **Execution Overhead:** Slicing 1 item from `List<JsonElement>` takes $< 10$ ns.
- **Memory Allocation:** Zero extra list allocations when no truncation occurs.
- **Clean Architecture:** No UI or transport logic leaked into domain entities.

---

## 6. Verification Checklist

- [ ] Querying root table with `first: 5` on a 10-row table returns 5 items and `extensions.pagination.isTruncated == true`.
- [ ] Querying root table with `first: 10` on a 5-row table returns 5 items and no `extensions.pagination` (or `isTruncated == false`).
- [ ] Querying at gateway `MaxResponseRows` emits `maxRowsReached: true`.
- [ ] Offsets $> \text{MaxAllowedOffset}$ reject with clear keyset paging guidance.

---

## 7. Phase 3 Security Review & Binding Conditions (`csharp-security-expert`)

**Review Date:** 2026-10-19  
**Verdict:** APPROVED WITH CONDITIONS  
**Reviewer:** Application Security Expert (`csharp-security-expert`)

### Binding Security Conditions

1. **`SEC-GOV39-01`: Lookahead Isolation & Slicing Oracle Prevention**
   - When executing root queries with lookahead $N + 1$, the lookahead fetch MUST remain strictly constrained by the caller's tenant isolation, Row-Level Security (RLS) predicates, and column-level masking rules.
   - Slicing the $(N+1)$-th row from the in-memory array (`list.RemoveAt(list.Count - 1)`) MUST occur in memory prior to GraphQL result projection or serialization.
   - The discarded lookahead row must never leak into the response payload, resolver context, or error extensions.
   - `isTruncated: true` signals solely that additional items matched the caller's authorized query criteria; it MUST NOT reveal internal database IDs, primary keys, timestamps, or existence of records across tenant or permission boundaries.

2. **`SEC-GOV39-02`: Structural Pagination Information Leakage Prevention**
   - The `extensions.pagination` object MUST only emit sanitized structural metadata: `isTruncated: bool`, `maxRowsReached: bool`, and `pageSize: int`.
   - `pageSize` MUST report the count of records actually returned to the caller (`list.Count`), strictly post-authorization and post-filtering.
   - Under no circumstances shall masked column markers, table row counters, or internal database physical record counts be exposed via pagination extensions.

3. **`SEC-GOV39-03`: Fail-Closed Offset Validation & Denial-of-Service Defense**
   - In `GraphQlTreeBuilder.BuildTree` / `BuildNode`, offset inputs MUST be validated before AST SQL compilation. If `offset < 0` or `offset > MaxAllowedOffset` (default 10,000) or contains non-numeric overflow, the request MUST fail closed immediately throwing `GatewayInvalidQueryException` with structured code `INVALID_QUERY`.
   - Zero database round-trips, zero SQL query generation, and zero connection pool acquisitions shall occur when offset boundaries are violated.
   - In `CatalogGraphQlTypeModule`, keyset cursor guidance injected into schema field descriptions MUST strictly use sanitized, validated column names from catalog metadata (`table.Key` / `table.Metadata`) to prevent injection into schema SDL.

### Mandatory Security Test Suite

All conditions must be verified with dedicated tests marked `[Trait("Category", "Security")]`:
- `T_SEC_GOV39_01_LookaheadIsolationAndSlicing`: Verifies that the $N+1$ lookahead item is executed under caller tenant/RLS filters, and exactly $N$ rows are returned with zero bytes or field values of the $(N+1)$-th item exposed in `data` or `extensions`.
- `T_SEC_GOV39_02_PaginationSignalDataMasking`: Verifies that `extensions.pagination` contains only structural keys (`isTruncated`, `maxRowsReached`, `pageSize`), without leaking unmasked values or hidden row metadata.
- `T_SEC_GOV39_03_OffsetBoundaryFailClosed`: Verifies that queries with negative offset, offset exceeding `MaxAllowedOffset`, or integer overflow fail closed with `INVALID_QUERY` without issuing database queries.

