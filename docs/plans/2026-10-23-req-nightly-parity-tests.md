# REQ-PARITY-NIGHTLY: Nightly functional-completeness (parity) test suite

Phase 1 PRD. Status: draft. Origin: master plan row `REQ-PARITY-NIGHTLY`.

## 1. Problem

Issues from app developer Robin (rgruebel) keep reporting features that "obviously" exist but are missing or crash (#280 aggregation `where` with value -> `INTERNAL_SERVER_ERROR`; group by a date part in WebSQL/GraphQL; GraphQL group by; empty `where`). Existing tests cover what developers thought of. The gaps sit in feature combinations and boundary inputs. The suite must find them proactively, nightly, without blocking PRs.

## 2. Goals / non-goals

Goals
1. Systematically cover capability x channel x data type x dialect, and boundary inputs, with explicit oracles.
2. Every gap is either a failing test or a visible, tracked KnownGap; nightly is green-with-visible-gaps.
3. Every new Robin issue adds a case (persona suite).

Non-goals
1. Fixing discovered gaps (separate issues/PRs).
2. Weakening or replacing existing tests; load/perf tests; UI tests.
3. Security-negative testing (covered by `Category=Security`, Fuzz, Metamorphic).
4. Unseeded random data or random queries without an oracle.
5. Running on PRs or release pipelines.

## 3. Benchmark and rejected anti-patterns

Adopted: pairwise (all-pairs) generation (PICT-style; covers most defects with a fraction of the full product); differential testing / query-oracle testing (SQLancer: NoREC, TLP partitioning: `COUNT(*) = COUNT(p) + COUNT(NOT p) + COUNT(p IS NULL)`); property-based invariants (no 5xx, determinism, idempotent paging); boundary-value analysis; spec-driven coverage from introspection.
Rejected: (a) nondeterministic generation: fixed seed, seed printed on failure and in trx; (b) random data without oracle (a crash-only check is allowed only as invariant C); (c) asserting "currently observed" output as expected (golden-snapshot of buggy behavior); (d) retries to mask flakiness (house rule M-5: no retries); (e) skipping silently: a skip must carry a KnownGap id.

## 4. Nightly-only isolation (HARD)

1. New project `tests/Autheris.Tests.Parity`, every test class carries `[Trait("Category","Parity")]` (enforced by a base class / custom attribute `ParityFact`/`ParityTheory`).
2. The project is NOT added to `Autheris.sln`. It lives in `Autheris.Parity.slnf`/own `.sln` and is referenced only by the nightly workflow. This makes local `dotnet test Autheris.sln`, ci.yml unit step, release.yml and docker-publish.yml exclude it by construction (their existing `Category!=...` filters stay unchanged; defense in depth: add `Category!=Parity` to them).
3. Integration shards (`.github/scripts/integration-shard-filter.py`) target only `Autheris.Tests.Integration`; Parity never lives there. `BASE_FILTER` gains `&Category!=Parity` as defense.
4. Guard (in `Autheris.Tests.Architecture`, runs in normal CI): fails if (a) the Parity csproj is referenced by any `*.sln` used by normal workflows, (b) any non-nightly workflow `dotnet test` line targets the Parity project or lacks `Category!=Parity` where it runs a solution, (c) a Parity test class lacks the trait, (d) another test project contains `Category=Parity`.
5. Runtime guard: Parity fixture fails immediately unless env `AUTHERIS_PARITY_RUN=1` (set only by the nightly workflow), so an accidental run on a developer machine or normal job is a loud failure, not a long run.
6. Workflow: new job in `.github/workflows/sql-compiler-gate-nightly.yml` (cron `0 2 * * *`, already has `workflow_dispatch`) or sibling `parity-nightly.yml` (recommended: sibling, so SQL gate stays focused; same cron, no `pull_request` trigger, no secrets). Inputs for dispatch: `filter`, `seed`. Outputs: trx artifact, `known-gaps.md` artifact and GitHub step summary (table of KnownGap ids, channel, capability, issue-draft id), pass/fail counts.

## 5. Known-gap mechanism

1. `[KnownGap("PARITY-GAP-NNN", "short reason")]` on a test or `KnownGapCell` entry in the matrix data. The id maps to an issue draft section in the Phase 2 plan (`docs/plans/...-parity-gap-drafts.md`), later replaced by the GitHub issue number.
2. Behavior: the test still executes. If it fails, it is reported as skipped-with-reason (xunit `Assert.Skip` after catching) and listed in the summary. If it passes, the test FAILS with "gap is fixed: remove KnownGap" so the list cannot rot (strict xfail).
3. A guard test fails if a KnownGap id is not present in the gap-drafts file, or a KnownGap has no id.
4. Known gaps are never added to hide a regression of a previously passing cell: the nightly compares to the committed `parity-baseline.json`; a cell that was green and now fails is a hard failure.

## 6. Building blocks (priority order for delivery)

Estimates are production-test LOC (C#) including data tables; effort in developer days.

### C. Cross-cutting invariant: no 500 (P1, ~400 LOC, 2d)
1. Every generated input (from B, D, F) runs through the channel; assertion: response is success OR a 4xx with a stable error code from the documented code list; never HTTP 5xx, `INTERNAL_SERVER_ERROR`, unhandled exception, stack trace text, or timeout.
2. Generic `ParityAssert.NoServerError(channelResult)`; error body must contain a non-empty `code` matching the registry.
3. AC: injecting a throwing stub in a channel makes the invariant test fail; #280 pre-fix reproduces as failure.

### D. Boundary-value catalogue (P1, ~500 LOC, 2d)
1. Single shared catalogue `BoundaryValues` per type: empty string, whitespace, null, single element, duplicates, special chars (quote, `%`, `_`, backslash, unicode, emoji, SQL keywords), very long (1k, 64k), number min/max/zero/negative/NaN-like strings, dates (epoch, 0001, 9999, leap day 2024-02-29 and 2100-02-29 invalid, DST transition days, UTC offsets), time zones (UTC, Europe/Berlin, invalid id), page size 0/1/max/max+1/negative, offset beyond end, empty `IN ()`, list of 1 and 1000 items.
2. Applied to every filter operand, sort key, paging parameter and declared-query parameter, on each channel in scope.
3. AC: catalogue has fixed ordering and ids (stable test names); adding a value in one place extends all channels; expected outcome per value is `ok(result)` or `rejected(code)`, never undecided.

### G. Robin persona suite (P1, ~700 LOC, 3d)
1. End-user scenario catalogue (section 8), each a named test `Robin_<issue>_<slug>`, run through the channel the issue used plus the sibling channels.
2. AC: each scenario cites a source issue or "proactive"; PR template/review checklist gains line "new rgruebel issue => add Robin case"; scenarios assert correct result, not only no-500.

### A. Capability x channel parity matrix (P2, ~900 LOC, 4d)
1. Capabilities: aggregates (sum, avg, count, count distinct, min, max), group by (column, date part year/month/day, expression), filters (all comparison operators, LIKE, IS NULL, IN, empty filter, `IN ()`), order, paging, NULL handling, parameterised comparisons (scalar and list). Channels: WebSQL, GraphQL, REST/OData, MCP. First delivery: aggregation/filter/grouping over WebSQL and GraphQL; REST/OData and MCP cells registered as `Pending` (counted, reported, not failing) until implemented.
2. Matrix is data: `ParityMatrix.cs` listing every cell as `Supported(expectedResultSpec)` or `Unsupported(errorCode)`. A cell missing from the matrix fails a completeness test (capability x channel product must equal matrix keys). [Theory] row per cell.
3. `Unsupported` means HTTP 4xx with stable code and an English message naming the capability; verified.
4. AC: matrix completeness test; both outcomes asserted; result correctness checked against seeded data with hand-computed expected values (small fixture, <= 20 rows with NULLs, duplicates, dates across years).

### B. Pairwise generation (P2, ~600 LOC, 3d)
1. Factors: aggregate x grouping x filter shape x data type x dialect (SQLite, PostgreSQL, SQL Server, MySQL where Testcontainers available; SQLite always).
2. Deterministic pairwise generator (in-repo, greedy IPOG, no new dependency) with fixed default seed; seed overridable via dispatch input and logged. Constraints table excludes invalid combos (e.g., sum over string => expected 4xx `code`).
3. Each generated case: name = factors hash; oracle = E (when applicable) or A's expected-spec rules (type-driven: count distinct on any type works, sum on numeric only, ...).
4. AC: same seed => identical case list (test); every factor pair covered (test); case count printed.

### E. Differential testing (P3, ~700 LOC, 4d)
1. In-process SQLite (always) and DuckDB (via `DuckDB.NET`, optional, behind package approval; recommended default: SQLite first) as reference. Same logical query executed through Autheris (WebSQL, GraphQL) and directly; result sets compared (order-insensitive unless ORDER BY, numeric tolerance for avg, NULL-aware).
2. TLP-style partition invariant on filters as extra oracle where no reference engine exists (PostgreSQL/SQL Server).
3. Semantic differences (collation, integer division, date-part naming) go to an explicit allow-list with reason, each tied to a KnownGap or "documented difference".
4. AC: a deliberately wrong compiler stub is detected; allow-list entries require reason.

### F. Spec-driven (P3, ~450 LOC, 2d)
1. Iterate GraphQL introspection: every query field and argument invoked once with valid minimal input (type-driven argument synthesis) and with each D value; result is success or documented 4xx.
2. Iterate the advertised SQL function list (capability endpoint/docs source of truth; exact source determined in Phase 2) and call each once with type-correct arguments on SQLite.
3. AC: adding a function/field automatically adds a test; no hard-coded list.

Total: ~4850 LOC, ~23 dev days: building blocks A-G ~4250 LOC / 20d plus infrastructure (fixtures, KnownGap, workflow, guard; section 7) ~600 LOC / 3d, delivered as P1. Phase 2 re-estimate per work package: ~5150 LOC (see the plan).

## 7. Infrastructure (WP-P0, P1, ~600 LOC, 3d)
1. Test host reuse: `WebApplicationFactory` fixture from `Autheris.Tests.Common`, SQLite fixture data source, GraphQL aggregation enabled, test auth (`-p:AutherisIncludeTestAuth=true`).
2. `IChannel` abstraction: `WebSql`, `GraphQl`, `Rest`, `Mcp`, uniform `ChannelResult { Status, Code, Rows }`.
3. Seed handling, KnownGap, guard tests, workflow, baseline file, summary renderer.

## 8. Robin scenario catalogue (draft; source in brackets)
1. Sum/count/avg/min/max per group via GraphQL with token read-only [#28, #112].
2. Aggregation GraphQL with `where` column = value on PostgreSQL and SQL Server [#280].
3. Same with `where` comparing parameter in declared query/REST [#280].
4. Group by date part year in WebSQL [proactive; user report].
5. Group by year/month/day in GraphQL aggregation [proactive].
6. Group by in GraphQL for plain column and expression [proactive].
7. Empty `where` / empty filter object on every channel [proactive; user report].
8. `IN (@departments)` with list parameter of 0, 1, 3, 1000 elements [#128].
9. Empty list `IN ()` outcome [#128, proactive].
10. Time zone conversion then group by local calendar day [#130].
11. Federated query with bound parameters [#131].
12. Federated query with aggregation and group by [#131, proactive].
13. Paging a list of 1000 rows with truncation signal [#39].
14. Dashboard: revenue per region and month, order by month, limit [#112].
15. Required filter column missing => stable code on REST, aggregation, bulk, federated [#163, #236].
16. Required filter satisfied through each channel [#163, #236].
17. Catalog table without columns does not stop gateway and is absent/empty in schema [#157].
18. HMAC-pseudonymized non-string column in GraphQL [#270].
19. Aggregate over masked column: allowed or clear 4xx [#28, proactive].
20. Count distinct on date and string columns [proactive].
21. Sum over NULL-only group returns null, not 0 or error [proactive].
22. Avg over integer column returns fractional result [proactive].
23. Order by aggregate alias and by group key [proactive].
24. Filter on date range with inclusive/exclusive bounds and leap day [proactive].
25. LIKE with `%`/`_` literals and escaping [proactive].
26. Parameter comparison where parameter is NULL (`= @p`) [#280, proactive].
27. Same logical query across WebSQL/GraphQL/REST returns equal rows [proactive].
28. Large page size above cap => 4xx with code, not truncation without signal [#39].

## 9. Requirements summary (numbered, testable)
1. REQ-PAR-1 Parity project exists, trait `Parity`, excluded from every normal run (section 4).
2. REQ-PAR-2 Guard tests enforce 4.4 and 4.5 in normal CI.
3. REQ-PAR-3 Nightly workflow with cron 02:00 UTC, dispatch, trx and known-gap artifacts, step summary.
4. REQ-PAR-4 Blocks C, D, G (P1), then A, B for aggregation/filter/grouping on WebSQL+GraphQL (P2), then E, F (P3).
5. REQ-PAR-5 KnownGap strict-xfail mechanism and baseline regression check.
6. REQ-PAR-6 Fixed seeds everywhere; seed logged.
7. REQ-PAR-7 Nightly wall time <= 30 min; a case-count budget per block, pairwise reduces product.

## 10. Open questions (recommended default)
1. Sibling workflow vs extending SQL gate nightly? Default: sibling `parity-nightly.yml`.
2. Add DuckDB dependency? Default: SQLite first, DuckDB in P3 after owner approves package.
3. Which dialects beyond SQLite nightly? Default: PostgreSQL and SQL Server via Testcontainers in a second job; SQLite job first.
4. Should a previously-green cell failing page someone? Default: workflow fails (red), no paging.
5. REST/OData and MCP cells in first delivery? Default: registered as `Pending`, implemented in follow-up.
6. Source of "advertised SQL function list" for F? Default: Phase 2 determines; use the existing catalog/capability endpoint.
7. Gap-draft issues auto-created? Default: no; PM files them manually from the nightly summary.
