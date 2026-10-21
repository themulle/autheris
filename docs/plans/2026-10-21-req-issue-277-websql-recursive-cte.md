# REQ-SQL-277: WebSQL accepts `WITH RECURSIVE` (governed, opt-in, bounded)

Source: GitHub issue #277. Phase 1 PRD. Date: 2026-10-21. Plan/audit: pending (Phase 2/3).

## 1. Problem and verified findings

1. Governed WebSQL (`POST /api/v1/sql`, MCP `query_sql`) answers `WITH RECURSIVE` with 403 `SQL_POLICY_DENIED`, although the AST compiler supports it (CR-ADG-29, `AstSecurityVisitor.Select.cs`: with `IsRecursive` the CTE name is added to scope before the body is visited; integration tests `RecursiveCte_ExecutesAndOnlyReadsTheTenantsRows` on SQL Server, PostgreSQL, Oracle; SQLite timeout test).
2. Confirmed root cause (code read): `SqlQueryAnalyzer.ExitNamedQuery` (src/TrinoSqlEngine/Analysis/SqlQueryAnalyzer.cs, ~line 180) registers the CTE name in `_cteScopeStack` only after the body. A self-reference in the body therefore passes `RecordTableAccess` (~line 464) as a base table and lands in `ReferencedTables`; `CrossSourceQueryRouter` (line ~108) cannot resolve it and throws `WebSqlTableDeniedException` -> mapped to 403 `SQL_POLICY_DENIED` (`SqlCompilerErrorMapper`).
3. `CrossSourcePlanner` (`VisitCommonTableExpression`, ~line 613) already registers the name before the body; `SubtreeCompiler` (~line 159) and `FederatedDuckDbExecutionService` (~lines 461/486) throw the same denial on unresolved targets and must be re-verified once the analyzer is fixed.
4. Docs state "not usable": `docs/features/f-dialect-02-ast-sql-compiler.md` section 5.2 (CTEs bullet, line ~210), section 8.1 (line ~414 mentions legacy `WITH RECURSIVE` defect only; check wording), section 9 (line ~435 and ~479). Section 7 lacks the new option keys.
5. The existing row cap (`DefaultMaxRows`/`MaxAllowedRows`) is enforced on the outer compiled statement. It does NOT bound intermediate recursion rows (for example an outer `GROUP BY`/aggregate over a runaway recursion). Bounding therefore needs depth and time limits in addition to the row cap.

## 2. Scope

- Analyzer fix so recursive CTE self-references are not treated as base tables.
- Downstream audit (planner, subtree compiler, execution services, MCP `query_sql`) for the same assumption.
- Opt-in switch, depth limit, runtime bounds, stable error codes.
- Tests on all four providers and the WebSQL endpoint.
- Alignment of `docs/features/f-dialect-02-ast-sql-compiler.md` (explicitly in scope per issue).

## 3. Non-goals

- OLAP path: `DuckDbOlapEngine` keeps blocking `RECURSIVE` (SQL2-9). Unchanged.
- Cross-source (federated) recursive CTEs spanning several sources: remain rejected with a stable code unless the recursive statement is single-source (see FR-9).
- Cycle detection syntax (`CYCLE`, `SEARCH`), recursive views, recursive DML.
- Non-WebSQL channels (GraphQL, OData, Arrow) gain no new SQL surface.
- Upgrade migrations or backward-compat shims (Autheris not in production).

## 4. Functional requirements

- FR-1 Analyzer: when a `WITH` clause is RECURSIVE (or, per FR-8, the dialect omits the keyword), each CTE name is in scope on entering its `NamedQuery`, so self-references are not recorded as base tables. Non-recursive CTEs keep Exit-timing (SEC-CTE, SEC C-02 shadowing semantics unchanged).
- FR-2 A CTE name equal to a catalog table (any visible schema) in a recursive WITH stays rejected (`EnsureRecursiveCteIsNotACatalogTable`); the analyzer must not let a recursive name mask a real table reference outside the CTE body.
- FR-3 Anchor and recursive member are both secured: tenant filter, row policy, column deny/masks apply to every base table in both members (extends existing compiler behavior; asserted end-to-end on the endpoint).
- FR-4 Option `Gateway:WebSql:AllowRecursiveCte`, default `false`. When false, a recursive statement fails before execution with a stable, dedicated code (proposed `SQL_CONSTRUCT_NOT_SUPPORTED`, existing; HTTP 400/422 per current mapping), never `SQL_POLICY_DENIED`. Message is sanitized and does not reveal catalog data.
- FR-5 Option `Gateway:WebSql:MaxRecursionDepth` (default 100, validated range 1..10000, fail start on invalid). Enforced where the provider supports it (SQL Server: emit `OPTION (MAXRECURSION n)` inside the compiler; the user cannot supply or override query hints). Exceeding it maps to `SQL_LIMIT_EXCEEDED`.
- FR-6 Where the provider has no native depth limit (PostgreSQL, Oracle, SQLite) runaway recursion is stopped by `ExecutionTimeoutSeconds` (`SQL_TIMEOUT`, 504) and, on SQLite, `IProviderExecutionGuard` (progress handler). Row cap continues to bound the returned result.
- FR-7 Runaway or cyclic recursion yields a clear error (`SQL_LIMIT_EXCEEDED` or `SQL_TIMEOUT`), never a hung worker thread or a partial silent result.
- FR-8 Dialect spelling: the compiler emits `WITH RECURSIVE` for PostgreSQL/SQLite and plain `WITH` (no keyword) for SQL Server and Oracle; both input forms are accepted from the caller when the dialect would reject the keyword at the provider. Recursion is detected structurally (self-reference), not only by the keyword.
- FR-9 Federated/DuckDB path: a recursive CTE whose base tables resolve to a single source runs push-down on that source. A recursive CTE requiring cross-source staging fails closed with a stable error (no partial staging).
- FR-10 Audit: the audit record flags recursive statements (existing audit fields plus `recursive_cte=true` marker, no raw SQL unless `StoreCompiledSql`).
- FR-11 Startup: `AllowRecursiveCte=true` is logged once as a startup notice; validator rejects `MaxRecursionDepth` outside range. Config keys documented in section 7 of the compiler feature doc.
- FR-12 Docs: update `f-dialect-02-ast-sql-compiler.md` sections 5.2, 7, 8 and the "not usable" lines in sections 9 and 10 to describe current behavior (opt-in, limits, dialect notes). No status/WIP wording. Master plan row to be added at lane end.

## 5. Acceptance criteria

1. Issue repro (hierarchy walk on SQL Server `lwe_infor_p01.dbo.ttibom010108`-style table) and the CTE-only recursion (`SELECT lvl + 1 FROM r WHERE lvl < 3`) return HTTP 200 with `AllowRecursiveCte=true`, same rows as the unsecured query on SQL Server, PostgreSQL, Oracle, SQLite (integration, per provider).
2. Endpoint test (extends `RecursiveCte_ExecutesAndOnlyReadsTheTenantsRows` to `POST /api/v1/sql` and MCP `query_sql`): rows of other tenants never appear in anchor or recursive member; row policy and masks apply to both members; denied column in the recursive member -> `SQL_POLICY_DENIED`.
3. Analyzer unit tests: self-reference not in `ReferencedTables`; real base tables in both members are; non-recursive CTE referencing own name still treated as base table (SEC C-02 regression); nested/multiple CTEs, shadowing and case-variant names covered.
4. CTE name equal to a catalog table -> rejected (all providers); recursive CTE name used in a different WITH scope does not leak.
5. With `AllowRecursiveCte=false` (default) a recursive statement returns the stable code of FR-4, not 403 `SQL_POLICY_DENIED`; test for both keyword and keyword-less structural recursion.
6. Cyclic data / `WHERE`-less recursion: SQL Server stops at `MaxRecursionDepth` with `SQL_LIMIT_EXCEEDED`; PostgreSQL/Oracle/SQLite stop at timeout with `SQL_TIMEOUT`; worker thread released; outer `GROUP BY` over runaway recursion is also bounded (covers finding 5).
7. A user-supplied `OPTION (MAXRECURSION ...)` hint or any query hint is still rejected.
8. DuckDB OLAP path still rejects `RECURSIVE` (regression test unchanged).
9. `dotnet build Autheris.sln` clean; unit tests pass; docs grep shows no remaining "recursive CTEs are not usable" statements.

## 6. Defaults taken (open questions)

1. D1: `AllowRecursiveCte` default `false` (issue proposal); owner can flip per deployment.
2. D2: OLAP/DuckDB path stays blocked (out of scope).
3. D3: `MaxRecursionDepth` default 100 (equals SQL Server native default); native enforcement on SQL Server only, timeout elsewhere. A portable synthetic depth counter (injected column) is rejected: alters query semantics and result shape.
4. D4: Disabled-state error code is `SQL_CONSTRUCT_NOT_SUPPORTED` (reuses an existing stable code; no new code introduced). Architect may substitute a dedicated code if mapping conflicts.
5. D5: Cross-source recursive CTEs unsupported (FR-9), single-source push-down only.
6. D6: Option keys live under `Gateway:WebSql:` next to `ExecutionTimeoutSeconds`; no new per-tenant override.

## 7. Risks

- Unbounded intermediate rows/memory on PostgreSQL/Oracle (timeout-only bound): Phase 3 must judge whether a statement-level memory/temp-space limit is required.
- Compiler surface growth: every recursion form (UNION vs UNION ALL, multiple recursive members, mutual recursion) needs fail-closed handling; unsupported forms must reject, not pass through.
- Dialect differences (keyword omission, Oracle recursive subquery column list requirement, SQL Server MAXRECURSION default 100 already limiting).
- Template cache keys must include the recursion flag/options so cached compiled text is not reused across option changes.
