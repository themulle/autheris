# Plan: procedure row scope fails on SQL Server with a subquery row filter (Issue #921)

- Issue: [#921](https://github.com/themulle/autheris/issues/921). Phase 2 plan, 2026-11-19. Code facts verified on `origin/main` after the v1.9.0 tag.
- Type: bug in row-filter application for procedure results (SQL Server only). No DDL.
- Phase 3: **required**. The change rewrites the SQL that applies the caller's row filter and tenant predicate to procedure results.

## 1. Problem and evidence

1. `src/Autheris.Application/Procedures/Services/SqlProcedureRowScopeResolver.cs:239-286` (`BuildKeyQuery`) emits the key-closure query:
   `SELECT keys FROM t AS autheris_target WHERE (key tuples) GROUP BY keys HAVING COUNT(*) = SUM(CASE WHEN <tenant> AND (<row filter>) THEN 1 ELSE 0 END)` (line 285).
2. A Stage-2 SQL virtual filter (`sql: from … where … target.<col>`, `src/Autheris.Domain/Model/VirtualFilterModels.cs:100-104`) compiles to `EXISTS (…)`. Inside `SUM(...)` that is a subquery in an aggregate argument. SQL Server rejects it with error 130 ("Cannot perform an aggregate function on an expression containing an aggregate or a subquery"). PostgreSQL accepts it, so the bug is SQL Server only.
3. Observed result: HTTP 503 `PROCEDURE_FAILED`, detail `row-scope-SqlException`. The call fails closed, so no data leaks, but every caller with such a filter is locked out.
4. Coverage gap: `tests/Autheris.Tests.Integration/Security/SqlProcedureRowScopeResolverIntegrationTests.cs` runs on PostgreSQL only (`CREATE SCHEMA IF NOT EXISTS main`, line 56). There is no SQL Server execution of the key-closure query, and no test uses an `EXISTS` filter.
5. `BuildKeyQuery` is the only emitter with this shape (`git grep "HAVING COUNT(\*) ="` on `src` finds only this file).

## 2. Options

| Option | SQL shape | Verdict |
|---|---|---|
| A | Derived table: `SELECT keys FROM (SELECT keys, CASE WHEN <cond> THEN 1 ELSE 0 END AS autheris_rs_ok FROM t AS autheris_target WHERE (key tuples)) AS autheris_rs GROUP BY keys HAVING COUNT(*) = SUM(autheris_rs_ok)` | **Recommended.** The aggregate argument is a plain column, so it is legal on SQL Server. The filter stays correlated to `autheris_target` with no rebinding. The `CASE … ELSE 0` keeps UNKNOWN (NULL) results as "not allowed", which is fail-closed. Works on both dialects, so there is one emitter. |
| B | `NOT EXISTS (SELECT 1 FROM t AS v WHERE v.keys = autheris_target.keys AND NOT (<cond>))` (issue proposal) | Rejected. `NOT (UNKNOWN)` is UNKNOWN, so a row whose filter evaluates to NULL is not counted as a violation. That fails open. The filter would also need rebinding from `autheris_target` to the inner alias, and NULL keys break the equality join. |
| C | Dialect-specific: keep the PostgreSQL shape, use A only on SQL Server | Rejected. Two shapes need two sets of tests for the same semantics. A is plan-equivalent on PostgreSQL. |

## 3. Decisions
1. D-921-1: use option A for both dialects.
2. D-921-2: reserved names: the flag column is `autheris_rs_ok` and the derived-table alias is `autheris_rs`. Add them as constants next to `TrinoSqlEngine.RowFilterAliases.Target`, or as `internal const` fields in the resolver. If a key column is named `autheris_rs_ok`, compare case-insensitively and throw `InvalidOperationException`. Fail closed; do not rename silently.
3. D-921-3: keep `COUNT(*) = SUM(flag)` (all rows of a key must pass). Do not switch to `MIN(flag) = 1`; the current form is pinned by tests and reviews.
4. D-921-4: the tenant predicate stays inside the same `CASE` condition, so tenant and row filter are evaluated per row exactly as today.
5. D-921-5: parameters (`@rs_tenant`, `@rs_i_k`, row-filter parameters) keep their names and binding order. `AddParameter` and its duplicate-name guard (lines 288-299) stay unchanged.

## 4. Work packages (one PR)

### WP-921-1 (S): rewrite `BuildKeyQuery`
- File: `src/Autheris.Application/Procedures/Services/SqlProcedureRowScopeResolver.cs`. Change `BuildKeyQuery(DatabaseDialect, TableMetadata, IReadOnlyList<string> keyColumns, string? tenantColumn, string? filterSql, int tupleCount)` (signature unchanged) to emit option A. Quote the reserved names per dialect with `dialect.QuoteIdentifier`. Update the XML docs at lines 22-25 and 234-238.
- Unit tests (local TDD filter `ProcedureRowScopeKeyClosureTests|SqlProcedureRowScopeResolverTests`):
  - `tests/Autheris.Tests.Unit/Procedures/ProcedureRowScopeKeyClosureTests.cs`: update `BuildKeyQuery_GroupsByKey_HavingAllRowsPassFilterAndTenant` (line 52) to the new shape. Add `BuildKeyQuery_SubqueryFilter_IsNotInsideAggregate(DatabaseDialect)`: the text between `SUM(` and its closing parenthesis is exactly the quoted flag column.
  - `tests/Autheris.Tests.Unit/Security/SqlProcedureRowScopeResolverTests.cs` (line 82 area): add `BuildKeyQuery_KeyColumnNamedLikeReservedFlag_Throws`, and `BuildKeyQuery_NullFilterResult_CountsAsNotAllowed`, which asserts that the `CASE` has `ELSE 0`. Mark both `[Trait("Category", "Security")]`.
- Acceptance: no emitted query has a subquery inside an aggregate argument, on either dialect.

### WP-921-2 (M): SQL Server execution coverage
- New file `tests/Autheris.Tests.Integration/Security/SqlServerProcedureRowScopeResolverIntegrationTests.cs`. Mirror the PostgreSQL class with a SQL Server Testcontainer; follow the container setup of `AstCompilerSqlServerFixture` in `tests/Autheris.Tests.Integration/AstCompilerSqlServerExecutionTests.cs:26`. Cases, all `[Trait("Category", "Security")]`:
  - `PrimaryKey_RowFilterAndTenant_AreAppliedInTheDatabase`
  - `ExistsRowFilter_IsEvaluated_NotRejected`: the regression for #921, with an `EXISTS (SELECT 1 FROM ref r WHERE r.id = autheris_target.x)` filter.
  - `ExistsRowFilter_NonUniqueKey_MixedRowsRejected`
  - `RowFilterEvaluatingToNull_KeyRejected`
- Extend `tests/Autheris.Tests.Integration/Security/SqlProcedureRowScopeResolverIntegrationTests.cs` (PostgreSQL) with `ExistsRowFilter_IsEvaluated` and `RowFilterEvaluatingToNull_KeyRejected` for parity.
- Hydrogen compile-checks the integration project locally; GitHub Actions runs it.

### Order
WP-921-1, then WP-921-2, in one PR. No dependency on other rows.

## 5. Integration test impact
- `tests/Autheris.Tests.Integration/Security/SqlProcedureRowScopeResolverIntegrationTests.cs` (extended) and the new SQL Server class.
- `tests/Autheris.Tests.Integration/PostgreSqlRowScopeContractTests.cs` and `tests/Autheris.Tests.Integration/Security/SourceLeastPrivilegeContainerTests.cs` use the resolver. Check them for assertions on the emitted SQL text and adapt them.
- Procedure endpoint tests that assert `row-scope-SqlException` for SQL Server, if any, now expect 200 (`git grep -n "row-scope-" tests`).

## 6. Phase 3 focus (no conditions invented here)
- Equivalence: same admitted key set as the old query for every filter that PostgreSQL accepted.
- NULL handling of the filter and of key columns.
- Reserved-name collision handling.
- Parameter binding order.

## 7. Owner questions
- Q-921-1: rewrite the query for both dialects (option A), or only for SQL Server? **Default taken: both dialects (one emitter).**
