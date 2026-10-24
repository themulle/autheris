# REQ-ROBIN-GAPS: Product fixes for the nightly Parity gaps

Status: Phase 1 PRD (2026-10-23). Branch `fix/robin-gaps`. Inputs: `2026-10-23-plan-nightly-parity-tests.md` section 5 (PARITY-GAP-030..057) and `2026-10-23-plan-robin-100-addendum.md` (4 intentional red cells).

## 1. Goal and rules

1. Make the nightly Parity suite green by fixing product defects. Never weaken a test, `KnownGap`, baseline cell or gate (SEC-R100-08/-19).
2. Fail closed: a denied or malformed request never returns data. No 5xx from user-induced input; every 4xx carries a stable `code` and a fixed message (#294/#295 contract).
3. Breaking behaviour changes are allowed (no production yet); each is listed in section 4 for release notes.
4. Each fix is TDD: the Parity cell is the Red test; add a unit test per item. A `KnownGap` for a fixed id is removed in the same change (strict xfail).

## 2. Defects (priority order)

### P1 #320 SECURITY: federated source existence oracle (Robin_30, join + single)
- Now: unknown source `nope.main.parity_sales` gives 403 `SQL_POLICY_VIOLATION`; unknown table of a known source gives 403 `SQL_POLICY_DENIED`.
- AC: unknown source, unknown table, and known-but-unreachable source answer with the same status, code, message and body bytes (and no timing-relevant extra path, e.g. no extra catalog lookup that is skipped).
- Benchmark: OWASP resource-enumeration guidance (uniform denial); same stance as SEC-X1-05.
- Rejected: distinct messages "for debugging"; logging differences visible to the client.
- By-design: no.

### P2 #321 UNION arity mismatch gives 502 `SQL_PROVIDER_ERROR`
- AC: set operations (`UNION`/`INTERSECT`/`EXCEPT`) with differing column counts are rejected before execution with a stable 4xx (default: 400 `SQL_SYNTAX_ERROR`, fixed message); never reaches the provider.
- Benchmark: ISO SQL requires equal degree of both operands.
- Rejected: mapping the provider exception text to the client; catching 502 in the test harness.
- By-design: no.

### P3 ROW_NUMBER() OVER (PARTITION/ORDER BY masked column), WebSQL
- AC: request stays denied (403, no data, no timing of value); only the code changes. Default: `AGGREGATION_ON_MASKED_COLUMN` (designed code). Architect/security confirm, because a window function is not an aggregate; if a new code is chosen it must be catalogued and documented.
- Benchmark: ordering/partitioning by a masked column is an inference channel (rank leaks order).
- Rejected: allowing the window function with masked ordering; leaving the generic `SQL_POLICY_VIOLATION`.
- By-design: plausible (generic code may be intended); decision needed, see Q1.

### #318 / GAP-052 GraphQL `first` / `offset` as `Int` variables
- AC: `first`/`offset` accept `Int` variables (5,10 gives ids 11-15; absent/null for `offset` means 0). Explicit `null` or unresolved `first` is treated exactly like literal `first: null` in the cost analysis (max page cost, fail closed); `query($f:Int=1){t(first:$f)}` with `f:null` must not return all rows at cost 2: either capped to the default page with full cost, or rejected `INVALID_QUERY` (see Q2). `first: 0` gives zero rows.
- Benchmark: GraphQL spec 3.1.7/6.1.2 (variable coercion, explicit null differs from absent; default applies only when absent).
- Rejected: cost computed from the variable default when a null is supplied; accepting unbounded `first`.
- By-design: no (cost bypass is a DoS vector).

### #319 / GAP-053 `or: [{}, X]`
- AC: an empty object member is the neutral element of its parent: true in `or` (all 18 rows), true in `and`; empty `or: []` stays rejected or false per existing contract (unchanged).
- Benchmark: Hasura/Prisma semantics (`{}` = no constraint); SQL identity `TRUE OR x`.
- Rejected: dropping empty members before the `or` (turns it into `X`).
- By-design: plausible (drop-empty may be intended), but it silently narrows results; fix.

### #302 / GAP-054 and `first: 0` / `first: -1` / REST offset over cap
- AC: REST `offset` above the cap (10000) gives 400 `INVALID_QUERY`, never 200 empty. GraphQL `first: -1` and `offset: -1` give `INVALID_QUERY` (see GAP-057 for the code); `first: 0` returns zero rows (consistent with REST `limit=0`, #301).
- Benchmark: OData v4 `$top`/`$skip` must be non-negative integers (400 otherwise); an empty 200 for an out-of-range offset hides the cap.
- Rejected: silently clamping to the cap.
- By-design: no.

### #323 GAP-030 REST `startswith(col,'x')`
- AC: 200 with rows whose column starts with the literal; same rows as WebSQL `LIKE 'x%'`; `%`, `_` and `\` in the literal are escaped (no wildcard injection). Add `endswith`/`contains` only if the translator already lacks them and the change is trivial (Q3).
- Benchmark: OData v4 5.1.1.4 canonical functions.
- Rejected: building `LIKE` by string concatenation.

### #324 GAP-033 REST `eq null` / `ne null`
- AC: `amount eq null` becomes `IS NULL`, `ne null` becomes `IS NOT NULL` (never `= NULL`); rows match WebSQL `IS NULL` / `IS NOT NULL`; `is null` keeps working.
- Benchmark: OData v4 (`eq null` is null test); SQL 3VL (`= NULL` is unknown).
- Note: GraphQL/WebSQL `eq: null` stays zero rows (#300 decision); the REST/OData syntax differs by spec. Document in release notes.

### #325 GAP-034 GraphQL `QUARTER` bucket
- AC: SQLite (and every provider that supports buckets) returns groups keyed by the first day of the quarter (8 groups for the seed); no `AGGREGATION_RESULT_TYPE_MISMATCH`.
- Benchmark: calendar quarters (Jan/Apr/Jul/Oct); same key shape as the MONTH bucket.

### #326 GAP-035 `HOUR` bucket with `timeZone`
- AC: local hour is computed with the DST offset valid at that instant (Europe/Berlin: 11 winter, 17 summer for the seed rows), not the standard offset.
- Benchmark: IANA tz rules; offset resolved per timestamp, never per zone constant.
- Rejected: fixing the test expectation to 16.

### #327 GAP-050 WebSQL federated join chain order
- AC: `default.A JOIN default.dim JOIN second.B` returns 200 with the four overlapping rows, equal to the order `second` first; no 400 `SQL_SYNTAX_ERROR`. Per-source authorization is still evaluated for every table in the chain (fail closed); a chain with an unknown or denied source gives the P1 uniform denial.

### #328 GAP-051 GraphQL `nin` with null member
- AC: `nin: ["North", null]` returns zero rows (`x NOT IN (..., NULL)` is never true); `in` with null still matches the non-null members only.
- Benchmark: SQL 3VL; matches WebSQL.
- Rejected: stripping nulls from the list (gives rows, wrong).
- By-design: plausible (Prisma-style null stripping); keep 3VL for cross-channel parity (Q4).

### #329 GAP-056 REST malformed filter
- AC: `region eq` (dangling operator) and `(region eq 'North'` (unbalanced) give 400 `INVALID_QUERY` with a fixed message, not `SQL_SYNTAX_ERROR`; no parser detail or position is echoed.

### #330 GAP-057 GraphQL page size out of range
- AC: `first` above `MaxAllowedLimit` or negative gives `INVALID_QUERY` (REST parity).
- Constraint: the #294 production enumeration shield stays byte-identical: validation errors for unknown field/argument/type keep the fixed `GRAPHQL_VALIDATION_FAILED` body and message in every environment; only the page-size range rule changes code, and only because it is a value rule, not a schema-existence rule. If the shield would differ for any other error, the change is reverted for that error. A regression test pins the shielded bodies.
- By-design: plausible (the code was uniform by design); decision needed, see Q5.

## 3. Cross-cutting acceptance criteria

1. Nightly Parity run shows 0 red cells and no `KnownGap` left for ids 030-057 covered here; baseline cells updated only to the new correct outcome.
2. No new 5xx path: a unit test per fixed item sends the malformed input and asserts 4xx with a stable code.
3. No error body differs between the existence-oracle pairs (P1) and none contains source/table names supplied by the caller beyond what exists today.
4. `Robin100_NoKnownGapForDeniedOrLeakCells` and the leak scans pass unchanged.

## 4. Release-note list (breaking changes)

1. Unknown federated source: code `SQL_POLICY_VIOLATION` becomes `SQL_POLICY_DENIED`.
2. UNION arity mismatch: 502 becomes a 4xx.
3. Window function over masked column: code change (per Q1).
4. GraphQL `first`/`offset` accept `Int` variables; explicit null `first` is capped and costed at maximum.
5. `or: [{}, X]` returns all rows (was X only).
6. REST offset above cap and negative GraphQL paging: 200 empty / other codes become 400 `INVALID_QUERY`.
7. REST: `startswith(...)` and `eq null`/`ne null` now work.
8. GraphQL `nin` with null returns zero rows.
9. GraphQL `QUARTER` and DST-aware `HOUR` buckets now return data/correct hours.
10. WebSQL federated join chains in any source order now work.
11. REST malformed filter code: `SQL_SYNTAX_ERROR` becomes `INVALID_QUERY`; GraphQL page-size code becomes `INVALID_QUERY`.

## 5. Open questions (recommended default)

1. P3 code for window over masked column: `AGGREGATION_ON_MASKED_COLUMN` (default) vs new `WINDOW_ON_MASKED_COLUMN`. Security decides.
2. #318 explicit null `first`: cap to default page at full cost (default) vs reject `INVALID_QUERY`.
3. GAP-030: add `endswith`/`contains` now? Default: only if already missing and trivial; otherwise a follow-up issue.
4. GAP-051: keep 3VL zero rows (default) vs null-stripping.
5. GAP-057: confirm `INVALID_QUERY` for page-size range inside the #294 shield (default yes, shield bodies untouched).
6. `first: 0` on GraphQL: zero rows (default) vs rejection.
