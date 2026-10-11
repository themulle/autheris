# PRD: Federated Virtual Filters on Web-APIs (F-GOV-14)

**Document ID:** `REQ-FEDERATED-VF-WEBAPI-15` (child of `PLAN-FEDERATED-VF-DUCKDB-15`)
**Phase:** 1 - Product Management (6-phase lifecycle, see `AGENTS.md` section 6)
**Date:** 2026-10-10
**Origin concept (German):** [2026-10-10-konzept-federated-virtual-filter-web-api-duckdb.md](./2026-10-10-konzept-federated-virtual-filter-web-api-duckdb.md)
**Origin plan:** [00-master-plan-overview.md](./00-master-plan-overview.md)
**Feature reference (current, unverified):** [../features/f-gov-14-federated-virtual-filters.md](../features/f-gov-14-federated-virtual-filters.md)
**Implementation under review:** commit `e7ee288`

> **Verdict of this phase:** The implementation in `e7ee288` is **not** releasable. The "COMPLETED & VERIFIED" status in the
> master plan is not supported by evidence: none of the 5 end-to-end tests from the concept exist, and the code contains
> several fail-open paths (sections 7 and 8). This PRD restates the feature as testable requirements and is the input for
> Phase 2 (architecture) and Phase 3 (security audit).

---

## 1. Problem and goals

### 1.1 Problem
Virtual Filters restrict rows of a protected object by a key set that lives in a relational source (for example an
MSSQL authorization view). For objects behind REST/Web-APIs (`DataSourceType.HttpDeclarative`), the filter cannot be
pushed into the source as SQL. Before F-GOV-14 the gateway therefore denied such queries (HTTP 403, fail-closed via
`EnsureInMemoryFilterIsEnforceable`). Customers need governed read access to Web-API data that is filtered by
authorization data held in a different (SQL) source, without fetching and exposing unauthorized rows and without
unnecessary egress traffic.

### 1.2 Goals
- G1: Enforce a Virtual Filter whose key source is a SQL source on a Web-API object, with **exactly the same row
  semantics** as on a SQL object (no row the filter would reject is ever returned).
- G2: Avoid needless upstream calls: a point lookup for an unauthorized key, or a query by a principal with an empty key
  set, causes **zero** upstream HTTP requests.
- G3: Reduce egress volume by pushing the authorized key set to the Web-API when the API supports it, as an
  **optimization only**.
- G4: Support single and composite keys, column name mapping, and declarative binding by pattern.
- G5: Stay within bounded memory, request size and time budgets; every decision is audited.

### 1.3 Success metrics
- 0 rows outside the authorized key set in any channel/strategy/format combination (property-based and E2E tests).
- 0 upstream HTTP calls for unauthorized point lookups and empty key sets (verified by a request-counting stub).
- For key sets within the pushdown budget on a pushdown-capable endpoint, transferred upstream rows are at most
  the authorized rows plus what the API itself over-returns (measured in an E2E test).

---

## 2. Scope and non-goals

### 2.1 In scope (v1)
- WebSQL cross-source path (`FederatedDuckDbExecutionService`), which is where every query touching an
  `HttpDeclarative` table is routed (`CrossSourceQueryRouter`), gated by `WebSql.CrossSource.Enabled`.
- Virtual Filters with a **structured** definition (`StructuredFilterDefinition`) whose source is a SQL data source.
- Target objects of type `HttpDeclarative`.
- Execution tiers: Tier 0 (empty key set), Tier 1 (short-circuit point lookup), Tier 2 (key-set pushdown), Tier 3
  (DuckDB semi-join), plus the mandatory post-fetch enforcement (section 4, FR-7).
- Strategies `Adaptive`, `ShortCircuitOnly`, `DuckDbHashJoin`, `PushdownOnly`.
- Pushdown formats `CommaSeparated`, `RepeatedParam`, `ODataIn`, `PostBatch`.

### 2.2 Non-goals (v1)
- REST, GraphQL, OData and MCP channels on HTTP objects: they **remain fail-closed (403)** for cross-source Virtual
  Filters. Channel parity is a follow-up requirement (see D-12).
- Stage 2 (SQL-text, `VirtualFilter.Sql`) filters as key sources: they must be rejected fail-closed with a clear error
  code until a dedicated design exists.
- DML on Web-API objects (already rejected by the router).
- Web-API objects as the **key source** of a filter (key source must be SQL in v1).
- AI-assisted or heuristic tier selection.

---

## 3. User stories

- **US-1 (Data steward):** As a data steward, I bind an existing SQL-backed Virtual Filter to a Web-API object via a
  target pattern and a column map, so that analysts only see Web-API rows for clients they are authorized for.
- **US-2 (Analyst):** As an analyst, I query `SELECT * FROM crm_api.public.customers WHERE id = 47` and get the row if I
  am authorized, or the same empty result as for a non-existent id if I am not.
- **US-3 (Analyst):** As an analyst, I run aggregate and join queries over a Web-API object and get correct counts
  (no duplicated rows, no extra columns).
- **US-4 (Platform operator):** As an operator, I declare which pushdown format and limits a Web-API supports, so that
  the gateway never sends requests the API cannot handle (HTTP 414) and never relies on the API to filter.
- **US-5 (Security officer):** As a security officer, I can reconstruct from the audit log which filter, tier and key
  source were used for each query, and I am guaranteed that an outage of the key source denies access.
- **US-6 (Security officer):** As a security officer, I can restrict a sensitive Web-API object to point lookups only
  (`ShortCircuitOnly`) and get an explicit, audited denial for scans.

---

## 4. Functional requirements

Each requirement is normative (MUST) and lists acceptance criteria (AC). "Upstream" means the remote Web-API;
"key source" means the SQL source of the Virtual Filter.

### FR-1 Binding model and validation
`FilterBinding` carries `TargetPattern`, `ColumnMap`, `Strategy`, `PushdownFormat`, `MaxPushdownKeys`.
- AC-1.1: `MaxPushdownKeys` is validated to `1 <= n <= HardMaxPushdownKeys` (gateway option, default 1000); values
  outside are rejected at write time.
- AC-1.2: Every `ColumnMap` key is a key column of the referenced filter; every value is a column of the target object
  in the catalog. Violations are rejected at write time (and at snapshot load, fail-closed).
- AC-1.3: `CompositeKeys` is either removed or must equal the filter's key column set (see D-9); any mismatch is a
  validation error. The effective key set is always derived from `VirtualFilter.KeyColumns`.
- AC-1.4: A binding with `Strategy` in {`Adaptive`, `PushdownOnly`} on an endpoint that declares no compatible pushdown
  capability (FR-5) is rejected for `PushdownOnly` and silently restricted to Tier 3 for `Adaptive` (documented, audited
  as `tier=3, reason=no_pushdown_capability`).
- AC-1.5: All new binding fields round-trip through `VirtualFilterStore` and participate in the definition hash.

### FR-2 Target pattern matching (regex)
- AC-2.1: Regex patterns (prefix `^` or `regex:`) are compiled with `RegexOptions.NonBacktracking |
  RegexOptions.CultureInvariant` and a match timeout (default 50 ms). Patterns unsupported by the non-backtracking
  engine (backreferences, lookarounds) are rejected at write time.
- AC-2.2: A pattern is matched against the canonical qualified name only (`source.schema.object`), as a full match.
- AC-2.3: A match timeout or any matching exception **denies the request** (`GatewaySecurityException`,
  code `VF_PATTERN_EVALUATION_FAILED`) and writes a denial audit event. It MUST NOT be treated as "no match".
- AC-2.4: An invalid pattern found at snapshot load denies every query on objects in the profile's scope.

### FR-3 Single source of truth for applicable filters
- AC-3.1: The set of Virtual Filters applied to an HTTP object is taken **only** from the authorization decision
  produced by `MandatoryRowFilterResolver` (`TableAccessDecision.AppliedVirtualFilters` plus the bindings that produced
  them). The federation path does not re-resolve profiles.
- AC-3.2: Grantee matching (user, group, role, service principal) is identical to the SQL path; a profile of another
  grantee is never used.
- AC-3.3: When several filters/bindings apply, all of them are enforced with the same AND/OR combination semantics as
  on SQL objects (including `Supersedes`). Test: two filters with disjoint key sets yield 0 rows.
- AC-3.4: If a filter applies but any required component (key provider, evaluator, binding metadata) is unavailable,
  the query is denied (`VF_ENFORCEMENT_UNAVAILABLE`). Optional constructor dependencies MUST NOT silently disable
  enforcement.

### FR-4 Authorized key loading
- AC-4.1: Keys are computed by executing the filter's key query **in the key source** using the same predicate builder
  as the SQL path (`StructuredFilterSqlBuilder` / `IVirtualFilterPredicateBuilder`), including `Joins`, all `Where`
  operators, and `ValidFromColumn`/`ValidToColumn` evaluated at query time. No partial in-memory re-implementation.
- AC-4.2: Unsupported definition shapes (Stage 2 SQL filters, unknown operators) are denied with
  `VF_KEY_SOURCE_UNSUPPORTED`; they are never treated as "condition satisfied".
- AC-4.3: Key source unreachable, timeout or error: request denied with `VIRTUAL_FILTER_SOURCE_UNAVAILABLE`
  (HTTP 503 mapping, or 403 per existing error policy), **no** upstream request is sent.
- AC-4.4: More keys than `MaxAuthorizedKeys` (gateway option, default 50,000): request denied with
  `VF_KEY_SET_TOO_LARGE`. Never truncate.
- AC-4.5: Keys are canonicalized by the **catalog type of the target column** (integer, decimal, uuid, string with
  ordinal comparison). A key that cannot be converted is dropped from the key set and counted in the audit record.
  Comparisons are ordinal and case-sensitive unless the target column is declared case-insensitive in the catalog.
- AC-4.6: Key rows with NULL in any key column never authorize anything.
- AC-4.7: Duplicate key tuples are de-duplicated before use.

### FR-5 Endpoint pushdown capability
Pushdown capability describes what the **API** supports, so it belongs to `HttpEndpointDescriptor` (see D-10).
- AC-5.1: The descriptor declares `PushdownFormat`, the parameter (or JSON property) name, the key column it filters,
  `MaxUrlLength` (default 2,048 bytes) and `MaxKeysPerRequest`.
- AC-5.2: A binding's `PushdownFormat`, if set, must equal the descriptor's; otherwise validation error.

### FR-6 Tier selection and strategy semantics
Tier selection happens per protected HTTP table after authorization and before any upstream I/O.

| Situation | `Adaptive` | `ShortCircuitOnly` | `PushdownOnly` | `DuckDbHashJoin` |
|---|---|---|---|---|
| Authorized key set empty (Tier 0) | 0 rows, no upstream call | 0 rows, no upstream call | 0 rows, no upstream call | 0 rows, no upstream call |
| Point lookup, key not authorized (Tier 1 miss) | 0 rows, no upstream call | 0 rows, no upstream call | 0 rows, no upstream call | 0 rows, no upstream call |
| Point lookup, key authorized (Tier 1 hit) | push down only the queried key(s) | push down only the queried key(s); if endpoint has no capability: path lookup or deny | push down only the queried key(s) | Tier 3 |
| Scan, keys within pushdown budget | Tier 2 | **deny** `VF_SCAN_NOT_PERMITTED` | Tier 2 | Tier 3 |
| Scan, keys over budget or no capability | Tier 3 | **deny** `VF_SCAN_NOT_PERMITTED` | **deny** `VF_PUSHDOWN_LIMIT_EXCEEDED` | Tier 3 |

- AC-6.1: Each cell of the table above has an automated test.
- AC-6.2: A "point lookup" requires equality predicates on **all** key columns, qualified to (or unambiguously
  resolving to) the protected table, in a conjunctive top-level `WHERE`. Predicates on other tables' columns with the
  same name are ignored. `IN (...)` lists are treated as multi-point lookups (intersection with the authorized set).
- AC-6.3: Tier 1 hit pushes down `queried ∩ authorized`, not the whole authorized set.
- AC-6.4: The chosen tier and reason are recorded in the audit record (FR-9).

### FR-7 Mandatory post-fetch enforcement (defense in depth)
Pushdown is an optimization, never the security boundary.
- AC-7.1: Rows returned by the upstream are **always** restricted by the gateway to the authorized key set before they
  can be referenced by the user query, for every tier and format. A test stub API that ignores all filter parameters
  and returns every row MUST yield only authorized rows.
- AC-7.2: Enforcement happens **at staging time**: the staged relation the user query sees is already the filtered
  relation (for example `CREATE TEMP TABLE s0 AS SELECT r.* FROM raw r WHERE (k1, k2) IN (SELECT k1, k2 FROM keys)`, or
  a `SEMI JOIN`). The user query AST is not rewritten to add the join. Consequently UNION, CTE, sub-query, self-join
  and set-operation queries are covered without special cases.
- AC-7.3: Join semantics are semi-join: no row multiplication from duplicate key tuples, no key-relation columns in
  `SELECT *`, aggregates (`COUNT(*)`) equal the count of authorized upstream rows.
- AC-7.4: The session flag that suppresses the generic in-memory row filter
  (`GovernedConnectorReader.VirtualFilterFederationHandledKey`) is set **only** for a table whose every applied
  filter was enforced by FR-7, and never suppresses other row-filter rules (RLS, consent, tenant) present in
  `CombinedRowFilterSql`. Tables without applied Virtual Filters keep the pre-F-GOV-14 behaviour.
- AC-7.5: Join keys are compared with identical types on both sides (FR-4 AC-4.5); a type mismatch that cannot be
  resolved at plan time denies the query (`VF_KEY_TYPE_MISMATCH`), it never compares via string fallback silently.

### FR-8 Tier 2 key-set pushdown
- AC-8.1: Request formatting uses a single formatter that is the code path actually executed by
  `DeclarativeHttpDataSourceExecutor` (no unused helper). Output per format:
  - `CommaSeparated`: `?{param}=v1,v2,v3` (each value percent-encoded; values containing `,` are rejected for this
    format).
  - `RepeatedParam`: `?{param}=v1&{param}=v2`.
  - `ODataIn`: `$filter={prop} in (lit1,lit2)` with literals typed by the **catalog column type** (not by parsing the
    value), string literals quoted with `''` escaping, the whole expression percent-encoded; an `or`-chain fallback is
    selectable for OData 4.0 services.
  - `PostBatch`: HTTP `POST` with a JSON body built by `Utf8JsonWriter`/`System.Text.Json` (never string
    concatenation), property name from the descriptor.
- AC-8.2: If the encoded request exceeds `MaxUrlLength` or `MaxKeysPerRequest`, keys are split into chunks; at most
  `MaxPushdownRequests` (default 10) chunks are sent. Beyond that, `Adaptive` falls back to Tier 3 and `PushdownOnly`
  denies.
- AC-8.3: Upstream HTTP 414 or 413: `Adaptive` retries once with Tier 3; `PushdownOnly` denies
  (`VF_PUSHDOWN_REJECTED_BY_UPSTREAM`). Other non-2xx responses deny the query (no partial results).
- AC-8.4: If a pushdown argument cannot be applied by the connector (unknown parameter, missing batch config), the
  executor throws; it MUST NOT drop the argument and fetch unfiltered.
- AC-8.5: Pushdown parameters are gateway-controlled: a caller-supplied argument with the same name is rejected.

### FR-9 Audit
- AC-9.1: One `WEBSQL_CROSS_SOURCE_SOURCE_READ` event for the key source read (filter name, source, key count, cache
  hit yes/no; never the key values) and one per upstream Web-API read (endpoint id, request count, rows received, rows
  kept after enforcement).
- AC-9.2: One `WEBSQL_CROSS_SOURCE_QUERY` record per query including: filter names, binding hash, strategy, tier,
  tier reason, short-circuit yes/no. Short-circuited queries produce a **complete** record (start and outcome).
- AC-9.3: Every denial listed in this PRD writes `WEBSQL_QUERY_DENIED` with its error code.
- AC-9.4: All events share the W3C trace id; audit write failure fails the query (existing INV-17).

### FR-10 Key caching
- AC-10.1: Cache key = tenant + filter id + filter definition hash + ordered key columns (+ principal SID only if the
  filter is principal-scoped). A missing principal SID denies the request; there is no `"anonymous"` fallback.
- AC-10.2: TTL is configurable (`VirtualFilters.KeyCacheTtl`, default 30 s, max 5 min); a filter or binding change
  invalidates entries immediately (hash in key). Revocation latency equals the TTL and is documented.
- AC-10.3: The cache stores canonicalized, de-duplicated, immutable sets (`FrozenSet`) so that Tier 1 lookups are O(1)
  for single and composite keys.
- AC-10.4: No public mutation API on the production key provider; tests use a fake `IVirtualFilterKeyProvider`.

### FR-11 Resource limits
- AC-11.1: The staged key relation counts against `MaxStagedRowsPerTable`, `MaxTotalStagedRows` and the byte budgets
  exactly like connector-staged tables.
- AC-11.2: Upstream reads stay bounded by `MaxStagedRowsPerTable` / `MaxStagedBytesPerTable`; exceeding them denies
  the query (`ROW_LIMIT_EXCEEDED`), never truncates.
- AC-11.3: DuckDB federation runs with `CrossSource.MaxMemory` (default 256 MB) and `MaxTempDirectorySize = 0B`.
- AC-11.4: Whole query bounded by `CrossSource.TimeoutSeconds`; cancellation aborts upstream requests.

---

## 5. Non-functional requirements

- **NFR-1 Fail-closed:** Any error, timeout, unsupported shape or missing component on the enforcement path denies the
  query. Proven by a fault-injection test per component (key source, regex, connector, DuckDB, audit).
- **NFR-2 No oracle beyond the principal's own grants:** A Tier 1 miss returns the same shape and status as an existing
  but empty match (0 rows, same columns). Upstream existence of unauthorized keys is never observable because they are
  never requested. Response timing may reveal whether a key is in the caller's *own* authorized set; this is accepted
  and documented. `ShortCircuitOnly` scan denial depends only on query shape and is therefore not an oracle.
- **NFR-3 Unsafe consumption of APIs (OWASP API10:2023):** Upstream responses are untrusted: schema-checked against the
  catalog, size-bounded, and always post-filtered (FR-7). Redirects stay same-origin (existing SEC M-25).
- **NFR-4 Resource consumption (OWASP API4:2023):** All limits in FR-8 and FR-11 have defaults, hard maxima and
  validation. No unbounded loops over keys or pages.
- **NFR-5 Misconfiguration (OWASP API8:2023):** Incompatible binding/endpoint combinations are rejected at write time,
  not discovered at query time.
- **NFR-6 ReDoS:** Non-backtracking regex engine plus timeout; timeout is a denial (FR-2).
- **NFR-7 Performance:** Tier 0/1 decision p95 < 1 ms with a warm cache (excluding audit I/O). Tier 3 with 50,000
  upstream rows and 10,000 keys completes in < 2 s p95 on the reference CI runner. No per-row LINQ allocations in the
  enforcement loop; large upstream payloads are streamed into staging in batches (no single LOH-sized buffer).
- **NFR-8 Quality gates:** Build with zero warnings (`/warnaserror`), every source file <= 800 lines, strict TDD.
- **NFR-9 Observability:** Metrics per tier (count, latency), pushdown chunk count, 414 fallbacks, denials by code.

### 5.1 Required tests (minimum)
Integration tests in `tests/Autheris.Tests.Integration/` against a real SQL key source (Testcontainers) and a stub
HTTP server that counts requests and can be configured to ignore filter parameters:
1. E2E-1 Tier 1 miss: `WHERE id = 999` -> 0 rows, 0 upstream requests, complete audit record.
2. E2E-2 Tier 1 hit: `WHERE id = 47` -> 1 row, exactly one upstream request carrying only key 47.
3. E2E-3 Tier 2 per format (4 cases): 3 authorized keys -> request in the expected format; stub that **ignores** the
   parameter still yields only the 3 authorized rows.
4. E2E-4 Tier 3 composite key `(tenant_id, client_id)` with duplicate key tuples and type-mismatched representations
   -> exactly the authorized rows, no duplicates, no extra columns, correct `COUNT(*)`.
5. E2E-5 Key source down -> denial (503/403), 0 upstream requests.
6. E2E-6 Query shapes: UNION, CTE, sub-query, self-join over the protected object -> only authorized rows.
7. E2E-7 Empty key set -> 0 rows, 0 upstream requests.
8. E2E-8 Strategy matrix (FR-6 table), regex timeout denial, `PushdownOnly` overflow denial, 414 fallback.
9. E2E-9 Non-VF row filter (RLS/consent) on an HTTP table in a cross-source query is still enforced (regression guard
   for AC-7.4).

---

## 6. Concept vs. implementation compliance matrix (commit `e7ee288`)

Legend: **Implemented** = matches concept and is safe; **Partial** = present but incomplete; **Missing** = absent;
**Divergent** = behaves differently from the concept (often unsafely).

| # | Concept item | Status | Evidence |
|---|---|---|---|
| 1 | Tier 1 short-circuit miss (0 rows, 0 HTTP) | Partial | `VirtualFilterShortCircuitEvaluator.EvaluateAsync`; `FederatedDuckDbExecutionService.EvaluateVirtualFiltersAsync` returns empty result. Returns on the first matching binding only; lookups extracted by unqualified column name (`SqlPointLookupExtractor` uses `Parts[^1]`); case-insensitive comparison. |
| 2 | Tier 1 hit -> targeted `GET /customers/47` | Divergent | Hit only sets filter/binding; execution continues to Tier 2/3 with the **whole** authorized set (or a full fetch for `ShortCircuitOnly`/`DuckDbHashJoin`). |
| 3 | Tier 2 `CommaSeparated` | Partial | `EvaluateVirtualFiltersAsync` sets `customArgs[batchParam]` (string) and `customArgs["ids"]` (list). Effective only if `BuildUrl` forwards the string arg or the descriptor has `BatchType` set and `BatchParamName == "ids"`. |
| 4 | Tier 2 `RepeatedParam` | Divergent | Passes a list; `BuildUrl` drops `IEnumerable` args and `ExecuteAsync` uses the list only if `descriptor.BatchType != None`. Otherwise the arg is silently dropped -> unfiltered fetch. `PushdownUrlFormatter.FormatQueryString` is not called in production. |
| 5 | Tier 2 `ODataIn` | Divergent | Same as #4; `$filter` is never produced at runtime. Formatter (test-only) types literals by `long.TryParse` of the value. |
| 6 | Tier 2 `PostBatch` | Divergent | `FormatJsonBatchBody` string passed as a query argument (GET `?ids={...}`), not as a POST body; JSON built by string concatenation (escapes `"` only). |
| 7 | Tier 2 is filtered by the API, gateway does not re-check | Divergent (unsafe) | No VF join is added when pushdown is chosen, and `CrossSourcePlanner` sets `VirtualFilterFederationHandledKey` so `GovernedConnectorReader.Apply` skips the in-memory filter. Empty key set (`0 <= MaxPushdownKeys`) also takes this path -> full fetch. |
| 8 | Tier 3 DuckDB join, single key | Partial | `CrossSourcePlanner.Plan` adds `INNER JOIN sN` on top-level `QuerySpecification.From` only; silently skipped for UNION/set operations; CTE/sub-query/self-join not handled; fallback target `"s0"`. |
| 9 | Tier 3 composite keys | Partial | Join over all `filter.KeyColumns` with `ColumnMap`; INNER JOIN multiplies rows on duplicate tuples, adds key columns to `SELECT *`, key staging columns typed `varchar` regardless of target type. No DuckDB execution test. |
| 10 | Strategy `Adaptive` | Partial | Pushdown if single key and count <= max, else join; inherits #7/#8 defects. |
| 11 | Strategy `ShortCircuitOnly` | Divergent | Scans return a silent 0-row result (concept: refuse scans); point hits fall through to a Tier 3 full upstream fetch. |
| 12 | Strategy `DuckDbHashJoin` | Partial | Always Tier 3; inherits #8/#9. |
| 13 | Strategy `PushdownOnly` | Divergent | Over budget or composite -> silently falls back to Tier 3 instead of denying. |
| 14 | Regex `TargetPattern` with 50 ms timeout | Divergent (unsafe) | `FilterBinding.MatchesTarget` returns `false` on `RegexMatchTimeoutException`/`ArgumentException`, i.e. the binding is skipped (fail-open in `MandatoryRowFilterResolver`). Backtracking engine. |
| 15 | `ColumnMap` | Implemented | Used in evaluator and planner join; validated as identifiers. Not validated against filter/catalog columns. |
| 16 | `CompositeKeys` | Partial | Persisted and validated as identifiers, but only read in an `isSingleKey` check; joins use `filter.KeyColumns`. No consistency validation. |
| 17 | `MaxPushdownKeys` | Partial | Lower bound validated (> 0); no upper bound; no URL length budget, chunking or 414 handling. |
| 18 | Memory envelope (256 MB, temp 0 B) | Implemented (pre-existing) | `CrossSourceOptions.MaxMemory`, `MaxTempDirectorySize`; `DuckDbOlapEngine` setup. |
| 19 | Streaming via `ArrayPool`/Appender, no LOH | Missing | Upstream rows materialized as `List<IReadOnlyDictionary<...>>` (`GovernedConnectorReader.ReadRawAsync`). |
| 20 | Key set in `FrozenSet`, O(1) lookup | Partial | `HashSet` rebuilt per call; composite lookup is a linear scan. |
| 21 | Staging budgets for key relation | Missing | `FederatedStagingService.StageAsync` `PreloadedRows` branch skips per-table and total limit checks. |
| 22 | Fail-closed on key source outage | Implemented | `DefaultVirtualFilterKeyProvider.GetAllowedKeyTuplesAsync` wraps errors in `GatewaySecurityException("VIRTUAL_FILTER_SOURCE_UNAVAILABLE")`; unit test with mock only. |
| 23 | Key extraction faithful to filter definition | Divergent (unsafe) | `LoadTuplesFromSourceAsync` reads only `Structured.From`; ignores `Joins`, `ValidFromColumn`/`ValidToColumn`, `Supersedes`; unknown operators fall through as "match". |
| 24 | Grantee-correct binding resolution | Divergent (unsafe) | Fallback in `EvaluateVirtualFiltersAsync` picks the first active profile with a matching binding **without grantee matching**, and only one binding. |
| 25 | Cache isolation per tenant and SID | Partial | Key `vf_tuples:{tenant}:{filter}:{sid}` with `"anonymous"` fallback; key columns and definition hash not in key; fixed 30 s TTL. |
| 26 | Test hook isolation | Divergent | Public `SetInMemoryTuples` on the production singleton, keyed by filter name only (not tenant), bypasses the source. |
| 27 | Audit: 2x `SOURCE_READ` + 1x `CROSS_SOURCE_QUERY` | Partial | Key source read is not audited; short-circuit path records only the start audit; tier/strategy not recorded. |
| 28 | Inference oracle (0 rows on miss) | Implemented | Miss returns empty result with projected columns. |
| 29 | E2E-1 short-circuit miss | Partial | Unit test `FederatedExecution_ShortCircuitMiss_ReturnsZeroRows_WithoutStaging` with a mocked evaluator; no integration test. |
| 30 | E2E-2 short-circuit hit | Missing | No test asserting a targeted upstream call. |
| 31 | E2E-3 parameter pushdown | Missing | Only `PushdownUrlFormatterTests` for a formatter not used at runtime. |
| 32 | E2E-4 composite DuckDB join | Missing | Planner test asserts only that SQL contains `JOIN`; no execution. |
| 33 | E2E-5 fail-closed on DB outage | Partial | Unit test `EvaluateAsync_KeyProviderThrows_FailsClosed`; no integration test. |
| 34 | DoD: zero warnings | Not verified | Not evidenced in the commit; to be verified in Phase 4/5. |
| 35 | DoD: files <= 800 lines | Implemented | Largest touched file `FederatedDuckDbExecutionService.cs` has 646 lines. |
| 36 | DoD: 5 E2E tests in `tests/Autheris.Tests.Integration/` | Missing | No F-GOV-14 integration tests exist. |
| 37 | DoD: complete audit of both subsystems | Partial | See #27. |

Additional regression found outside the concept: `CrossSourcePlanner` sets `VirtualFilterFederationHandledKey` for
**every** `HttpDeclarative` table (and every table with applied Virtual Filters) in a cross-source plan, even when no
Virtual Filter was enforced. `GovernedConnectorReader.ReadAsync` then treats the row filter as pushed down and skips
`CombinedRowFilterSql` entirely, which also disables non-VF row filters (RLS, consent, tenant) on HTTP tables in WebSQL.

---

## 7. Best-practice benchmark

| Practice | Industry reference | Requirement |
|---|---|---|
| Pushdown is an optimization, not the security boundary; enforce after fetch | Defense in depth; OWASP API10:2023 (unsafe consumption of APIs); Trino/Presto connectors re-apply filters the connector reports as not fully enforced | FR-7 |
| Semi-join, not inner join, for key-set filtering | DuckDB `SEMI JOIN` / `WHERE ... IN (subquery)` / `EXISTS`; standard dynamic-filtering in Trino, Spark | AC-7.3 |
| Filter at the source of the relation, not by rewriting the user query | Row-level security in PostgreSQL and Trino applies policies to the relation, so every query shape is covered | AC-7.2 |
| Typed join keys, no string fallback | DuckDB is strict on VARCHAR vs numeric comparison; implicit coercion causes misses ('047' vs 47) or binder errors | AC-4.5, AC-7.5 |
| URL length budget, chunking, POST fallback, 414 handling | RFC 9110 section 4.1 recommends supporting at least 8,000 octets, but proxies/CDNs often cap at 2-8 KB; HTTP 414 | FR-8 |
| Bounded resources | OWASP API4:2023 | FR-11, NFR-4 |
| Validate configuration up front | OWASP API8:2023 | FR-1, FR-5, NFR-5 |
| ReDoS: non-backtracking engine, timeout, fail closed | OWASP ReDoS guidance; .NET `RegexOptions.NonBacktracking` (.NET 7+) guarantees linear time | FR-2 |
| Same answer for "not found" and "not permitted" | OWASP API1:2023 (BOLA) guidance on enumeration | NFR-2 |
| Single policy decision point | Zero Trust (NIST SP 800-207): one PDP, enforcement points do not re-decide | FR-3 |
| Build structured payloads with serializers | OWASP injection prevention | AC-8.1 |

### 7.1 Rejected anti-patterns and safe alternatives

| Anti-pattern (found in `e7ee288`) | Why it is rejected | Safe alternative |
|---|---|---|
| Trusting the Web-API to have applied the pushed-down filter (no post-filter, flag skips in-memory filter) | Upstream may ignore unknown params, have bugs, or be compromised (API10); one dropped parameter leaks the whole dataset | FR-7: always enforce at staging; pushdown only reduces volume |
| Setting a "handled" flag before enforcement actually happened, for every HTTP table | Disables unrelated row filters; the flag claims a guarantee no component verified | AC-7.4: set the flag only after the enforcing component ran, per filter |
| Rewriting only the top-level `FROM` of the user query | Any other query shape (UNION, CTE, sub-query) bypasses the filter | AC-7.2: filter the staged relation |
| INNER JOIN against a key relation | Row multiplication on duplicates, extra columns, wrong aggregates | Semi-join with de-duplicated keys |
| Regex timeout or invalid regex treated as "no match" | Binding silently not applied -> fail-open | Deny and audit (AC-2.3), non-backtracking engine |
| Silent 0 rows for `ShortCircuitOnly` scans | Misleads users (looks like "no data"), hides policy, impossible to troubleshoot | Explicit `VF_SCAN_NOT_PERMITTED` denial, audited (not an oracle: depends on query shape only) |
| Silent strategy fallback (`PushdownOnly` -> full fetch) | Violates the operator's explicit egress constraint | Deny `VF_PUSHDOWN_LIMIT_EXCEEDED` |
| Public test-only mutable hook (`SetInMemoryTuples`) on a production singleton, keyed without tenant | Can override authorization data at runtime, cross-tenant bleed | Remove; inject a test fake of `IVirtualFilterKeyProvider` |
| Caching under an `"anonymous"` fallback identity | Unrelated callers share one cache identity; hides a missing-identity bug | Deny when SID is missing; derive cache key from what the key set actually depends on (AC-10.1) |
| Re-implementing filter predicates in memory (subset of operators, no joins, unknown operator = match) | Over-permits: ignored joins and validity windows widen the key set | Execute the key query in the key source via the existing predicate builder; unsupported = deny |
| Re-resolving profiles in the execution path without grantee matching | Another grantee's binding can be applied (wrong filter) and other bindings dropped | Use the authorization decision as single source (FR-3) |
| Optional enforcement dependencies (`= null` ctor params) that silently skip enforcement | Misconfigured DI = no filter | Required dependencies, or deny when a filter applies (AC-3.4) |
| Hand-built JSON / OData strings; literal typing by value parsing | Injection and type confusion | `System.Text.Json`, catalog-typed literals (AC-8.1) |
| Truncating or unbounded key sets / preloaded rows outside the staging budget | Memory exhaustion (API4) or silent incorrectness | Hard limits that deny (AC-4.4, AC-11.1) |

---

## 8. Gaps ranked by importance

**Critical (data exposure, must fix before any release)**
1. Unconditional `VirtualFilterFederationHandledKey` in `CrossSourcePlanner` disables in-memory row filtering
   (including non-VF RLS/consent filters) for all HTTP tables in WebSQL cross-source queries.
2. Tier 2 trusts the upstream: pushdown arguments are silently dropped for `RepeatedParam`/`ODataIn`/`PostBatch` or when
   the descriptor has no batch config, and the empty key set also takes the pushdown path, resulting in an
   unfiltered full fetch with no join and no post-filter.
3. Tier 3 join injected only into a top-level `QuerySpecification`; UNION/set operations skip it silently; CTE,
   sub-query and self-join shapes are not covered.
4. Binding resolution in the federation path ignores grantee matching and applies at most one binding; optional
   dependencies silently disable enforcement.

**High**
5. Key extraction ignores `Joins`, validity window and unknown operators (over-permissive key sets).
6. Regex timeout or error treated as "no match" (fail-open); backtracking engine.
7. Production test hook `SetInMemoryTuples` keyed without tenant.
8. INNER JOIN semantics (duplicates, extra columns, wrong aggregates) and `varchar`-typed key staging.
9. Audit gaps: key source read not audited, short-circuit path has no outcome record, tier not recorded.

**Medium**
10. Strategy semantics diverge (`ShortCircuitOnly` silent 0 rows and full fetch on hit; `PushdownOnly` silent
    fallback).
11. Cache key design (`"anonymous"`, missing key columns and definition hash, fixed TTL).
12. No URL length budget, chunking or 414 handling; no upper bound on `MaxPushdownKeys`.
13. Hand-built JSON and value-typed OData literals; production path does not use the tested formatter.
14. Preloaded key rows bypass staging budgets; no streaming into DuckDB.
15. Case-insensitive key comparison; point lookup extraction ignores table qualifiers.

**Low**
16. Tier 1 hit pushes down the whole authorized set instead of the queried key.
17. `CompositeKeys` redundant and unvalidated.
18. Feature reference `docs/features/f-gov-14-federated-virtual-filters.md` describes behaviour that is not
    implemented (pushdown formats, streaming, verified status); to be corrected in Phase 6.
19. None of the concept's E2E tests exist.

---

## 9. Open decisions and recommendations

| ID | Decision | Recommendation |
|---|---|---|
| D-1 | Is gateway post-fetch enforcement mandatory even when pushdown succeeded? | **Yes, always** (FR-7). Cost is a hash probe per row; benefit is that no upstream behaviour can widen access. |
| D-2 | Where is the filter enforced in Tier 3: query rewrite or staging? | **At staging** (filtered staged relation with semi-join). Removes all query-shape bypasses. |
| D-3 | `ShortCircuitOnly` scan: silent 0 rows or explicit denial? | **Explicit denial** `VF_SCAN_NOT_PERMITTED` (403), audited. Not an oracle because it depends only on query shape. |
| D-4 | `PushdownOnly` over budget / composite key: fall back or deny? | **Deny** `VF_PUSHDOWN_LIMIT_EXCEEDED`. `Adaptive` is the strategy for users who want fallback. |
| D-5 | Regex engine and failure handling? | `RegexOptions.NonBacktracking` + 50 ms timeout; timeout/invalid = **deny + audit**; validate at write time. |
| D-6 | Cache key and TTL? | Tenant + filter id + definition hash + key columns (+ SID only for principal-scoped filters); TTL configurable, default 30 s, max 5 min; missing SID = deny. |
| D-7 | Empty authorized key set? | Tier 0: **0 rows, 0 upstream calls**, for every strategy. |
| D-8 | Tier 1 hit: push down queried key or whole set? | Push down `queried ∩ authorized` only. |
| D-9 | Keep `CompositeKeys`? | **Remove** from the binding (derive from `VirtualFilter.KeyColumns`); if kept for compatibility, validate equality with the filter key set and ignore otherwise-different values with a validation error. |
| D-10 | Where does pushdown capability live: binding or endpoint? | **Endpoint descriptor** (what the API supports: format, param, max URL length, max keys per request). Binding keeps only `Strategy` and `MaxPushdownKeys`. |
| D-11 | URL length budget and overflow handling? | Default `MaxUrlLength` 2,048 bytes (safe across proxies), chunk up to 10 requests, then Tier 3 (`Adaptive`) or deny (`PushdownOnly`); 414/413 -> one Tier 3 retry for `Adaptive`, deny otherwise. |
| D-12 | Channels in v1? | **WebSQL only**; REST/GraphQL/OData/MCP stay fail-closed 403 for cross-source VFs on HTTP objects. Channel parity as a follow-up PRD. |
| D-13 | Key comparison case sensitivity and typing? | Typed by catalog column; strings ordinal case-sensitive unless the catalog marks the column case-insensitive. |
| D-14 | Status of `PLAN-FEDERATED-VF-DUCKDB-15` in the master plan? | Downgrade from "COMPLETED & VERIFIED" to **UNDER REVIEW** until Phases 2-6 close the Critical and High gaps and the E2E suite in section 5.1 is green. |
| D-15 | Stage 2 (SQL-text) filters as key source? | Out of scope for v1; deny with `VF_KEY_SOURCE_UNSUPPORTED`. |

---

## 10. Traceability

- Parent: [00-master-plan-overview.md](./00-master-plan-overview.md) - `PLAN-FEDERATED-VF-DUCKDB-15`
- Origin concept: [2026-10-10-konzept-federated-virtual-filter-web-api-duckdb.md](./2026-10-10-konzept-federated-virtual-filter-web-api-duckdb.md)
- Next: Phase 2 architecture plan `2026-10-10-plan-federated-virtual-filters-web-api.md` (to be created by the solution
  architect), Phase 3 security audit against sections 4, 5 and 7.1 of this PRD.
