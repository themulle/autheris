# PRD: Set-Based "Latest per Parent" Nested Lists and Slow-List Reporting

**Document ID:** `REQ-LPP-443`
**Status:** `PROPOSED`
**Date:** 2026-11-03
**GitHub Issue:** #443 (related: #417, #419, #421, #177, #224)
**Phase:** 1 (Product Manager)

---

## 1. Problem

A nested list of the form `first: n, orderBy: [{ts: DESC}]` joined on a parent key ("latest value per device") is compiled into a correlated subquery per parent row (`SELECT TOP (1) ... WHERE child.device_id = parent.device_id ORDER BY child.ts DESC`). When no index serves "equality on join key, then order by `ts`" (for example a clustered PK `(ts, device_id)`), each parent row scans the child table until it finds a match. Devices with no or old rows scan the whole table.

Measured (SQL Server 2019, 1.35 M child rows, 6 nested lists): 5.6 s per page for 140 parents, versus 142 ms with a set-based rewrite on the same indexes (1,060 ms for 20,000 parents). `IndexUsabilityEvaluator` (#421) covers root lists only and does not apply an equality-then-order rule to nested lists.

The missing index is the data source owner's fix. This PRD covers Autheris generating good SQL while the index is missing, and telling the caller which list is slow.

## 2. Requirements

Plan-time decisions (matching index or not) use deterministic catalog metadata only (declared PK with direction, declared indexes, physical indexes), never live statistics.

### 2.1 Index-match rule for nested lists
- **REQ-LPP-01** Define a nested-list index-match rule: the join (equality) columns and `where` equality columns first (any order), then the `orderBy` columns with their direction (a PK or index read backwards counts as a direction match only when the equality prefix is satisfied). A PK `(ts, device_id)` does NOT match `device_id = ? ORDER BY ts DESC`.
- **REQ-LPP-02** The rule lives in one shared component, reused by the compiler (REQ-LPP-03) and, optionally, by pricing (REQ-LPP-12). It extends or sits next to `IndexUsabilityEvaluator` and must not change root-list evaluation results.

### 2.2 Set-based compilation
- **REQ-LPP-03** When a nested to-many list has a bounded `first: n` and an `orderBy`, and REQ-LPP-01 reports no matching index, compile it set-based: resolve the list once for all parent keys of the page, not once per parent row.
- **REQ-LPP-04** When a matching index exists, keep the current correlated `TOP`/`LIMIT` form (unchanged SQL, byte-identical).
- **REQ-LPP-05** The set-based form uses a ranking strategy valid in every dialect: `ROW_NUMBER() OVER (PARTITION BY <join key> ORDER BY <orderBy incl. direction and tie-break>)` with `rn <= n`, restricted to the page's parent keys (join to the already page-limited parent set or a key list/CTE, never the whole child table without that restriction). For `first: 1` with a single order column, `MAX/MIN` per key plus a key seek is allowed as an optimisation (measured gain), provided results are identical.
- **REQ-LPP-06** Result equivalence: same rows, same order; an explicit `orderBy` gets no PK tie-break today, so a deterministic PK tie-break is added only in the set-based form and the correlated form stays byte-identical; same `null`/empty-list semantics for parents without child rows, same nested sub-selections and `where` filters as the correlated form.
- **REQ-LPP-07** Security parity: row filters (row-level security), column masks, caller-attribute scoping and audit are applied exactly as today, inside the set-based query, before ranking. Masked or forbidden columns must not be usable in `PARTITION BY`/`ORDER BY` beyond what `orderBy` already permits. All values stay parameterised; identifiers stay quoted via the dialect quoter. Any failure to compile the set-based form falls back to the correlated form (fail-safe for results) and never to an unfiltered query.
- **REQ-LPP-13** Scope: depth-1 nested lists with an explicit `orderBy` only. Nested lists compile only for SQL Server, PostgreSQL and SQLite (`TreeSqlCompiler.SupportsTreeCompile`). A root without a unique key falls back to the correlated form.
- **REQ-LPP-08** Multiple nested lists on one parent are compiled independently; each decides its own strategy.

### 2.3 Slow-list reporting
- **REQ-LPP-09** Configurable slow-query threshold (elapsed ms per request; see OQ-3). Only when the elapsed time is above the threshold, report in response `extensions` (next to `maskedFields`) the nested list(s) that caused most of the work. Each entry carries only `path` (client response keys, e.g. `devices.readings`) and `rank`: no durations, counts, strategy or catalog names.
- **REQ-LPP-14** Set-based compilation applies only with a finite, enforced `first:` cap and a bounded row budget; otherwise the correlated form is kept.
- **REQ-LPP-10** Attribution: a per-list stopwatch would need a second execution, so use a stopwatch per statement and per cross-source call, plus a fixed per-list class (unindexed correlated, then set-based, then indexed) as the ranking of likely cost. Logical reads only as optional SQL Server enrichment, never required.
- **REQ-LPP-11** Disclosure control (zero-trust): the extension contains only field paths the caller is already allowed to select, no table names, index names, SQL, row counts or hints about missing indexes; the report gate is conjunctive: `Enabled` (default false), the threshold exceeded, a client policy present, its `ExposeCostExtensions` set, and the response has no errors (there is no diagnostics permission); no new error codes. Measurement must not add a second query execution.

### 2.4 Optional
- **REQ-LPP-12** (Optional, separable) Apply the REQ-LPP-01 rule to nested lists in the admission price, so unindexed nested "first n per parent" lookups are priced higher (consistent with #421 tiers, #177/#224 nested single-row cost model). Ship only if it does not break existing price expectations; may be a follow-up issue.

## 3. Acceptance Criteria

Per dialect: SQL Server, PostgreSQL, SQLite. Oracle (AC-3) and Databricks (AC-5) are not applicable: nested lists do not compile there (default taken).

1. **SQL Server:** nested `first: 1, orderBy: ts DESC` over PK `(ts, device_id)` compiles to the set-based form; with an index `(device_id, ts)` it compiles to correlated `TOP (1)`. Result equal to the correlated form on a fixture incl. parents with no rows and ties. Benchmark fixture (>=1 M child rows, 140 parents): at least 10x faster than correlated; target <= 250 ms.
2. **PostgreSQL:** same split; set-based uses `ROW_NUMBER()`; correlated form keeps `LIMIT n` (or `LATERAL` where already used).
3. **Oracle:** not applicable (no nested-list compile).
4. **SQLite:** same split via `ROW_NUMBER()` (window functions supported); equivalent results verified by integration test.
5. **Databricks:** not applicable (no nested-list compile).
6. `first: n > 1`, multi-column `orderBy`, mixed directions, and `where` on the child all produce identical results to the correlated form (parity tests over a shared fixture, all dialects).
7. Row filters, masks and audit entries for the child table are identical to today (regression tests; a row hidden by a filter never ranks ahead of a visible row).
8. Plan-time rule: PK `(ts, device_id)` with join `device_id` + `ts DESC` reports "no match"; index `(device_id, ts)` reports "match"; `(device_id, ts DESC)` vs `ts ASC` direction cases covered. Root-list evaluator results unchanged.
9. Parameter and identifier handling: no string-interpolated values; set-based SQL is deterministic (stable text for a given shape) so statement caches work.
10. Slow-list reporting: with all gate conditions met (Enabled, threshold exceeded, policy, `ExposeCostExtensions`, no errors), `extensions` contains only `path` and `rank` per list; if any condition fails it is absent; no durations, counts, strategy, values, table/index names or SQL leak; byte-identical error responses in Production.
12. Set-based is not used without a finite enforced `first:` cap and bounded row budget (correlated form kept; test).
11. Fallback test: forced set-based compile failure yields the correlated result, never an unfiltered one.

## 4. Non-Goals

- Creating, recommending or auto-creating indexes (data source owner's task).
- Changing the root-list compilation, cursors (#420) or root-list pricing (#421).
- Cost-based planner behaviour or use of live statistics/row counts.
- Reporting actual execution plans, SQL text or index names to callers.
- Caching or materialising "latest" values.

## 5. Rejected Anti-Patterns

1. Always using the set-based form: slower than correlated `TOP` where an index matches (REQ-LPP-04).
2. Window function over the entire child table without restricting to page parent keys (full scan and sort for 20 k-parent worst case).
3. Per-parent N+1 queries from the application layer (round trips; breaks single-statement audit/filter parity).
4. Dialect-specific hints (`FORCESEEK`, index hints) as the fix: brittle, tied to index names the owner may change.
5. Emitting plan details, index names or "missing index" advice in `extensions` (schema enumeration leak).
6. Applying row filters after ranking (wrong rows win; leak of hidden rows' presence).
7. Deciding by live statistics: non-deterministic, untestable pricing and plans.

## 6. Migration and Compatibility

No schema change planned (generated SQL and a config option only). Breaking behaviour changes are acceptable (not in production). If a persisted setting for the threshold is added, it follows the forward-only migration policy (v1.5.0 frozen base; new step to the newest DB version only).

## 7. Open Questions (default taken)

1. OQ-1 Strategy for `first: 1`: `ROW_NUMBER` only or also `MAX` plus seek? Default taken: `ROW_NUMBER` for all `n` as baseline; `MAX`/seek only if the benchmark shows a clear gain and parity tests pass (decided in Phase 2).
2. OQ-2 Treat unknown index metadata (catalog lacks physical indexes) as match or no match? Default taken: no match (set-based is never much worse than correlated; fail-safe for performance).
3. OQ-3 Default slow-query threshold. Default taken: disabled by default; when enabled, 1000 ms; config key under the existing GraphQL options.
4. OQ-4 Attribution method. Default taken: stopwatch per statement and cross-source call plus fixed per-list class; optional SQL Server logical-reads enrichment.
5. OQ-5 Expose the report in Production? Default taken: only when explicitly enabled and the client policy sets `ExposeCostExtensions`; otherwise omitted.
6. OQ-6 Include REQ-LPP-12 (admission pricing) in this delivery? Default taken: no, separate follow-up issue after #421 lands.
7. OQ-7 Set-based form for to-one (single-row lookup) nested fields? Default taken: out of scope (#177/#224 cost model; unique-key lookups are already cheap).
