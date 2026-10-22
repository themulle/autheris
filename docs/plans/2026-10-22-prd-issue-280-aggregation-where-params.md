# PRD: Aggregation `where` with values fails on SQL sources (Issue #280)

ID: `REQ-AGG-280`. Phase 1 (product-manager). Type: bug fix. Affects v1.2.9 and v1.2.11.

## 1. Problem

With `Gateway:GraphQL:Aggregation:Enabled=true`, `<table>_aggregate(... where: {region: {eq: "north"}})` on a SQL source
(PostgreSQL, SQL Server) returns `INTERNAL_SERVER_ERROR`. Works: no `where`, `where: {region: {isNull: false}}` (no bound value),
and the same `eq` filter on the plain list field. `WebSql:Enabled=false` is the supported setup and must stay that way.

Suspected root cause (call sites verified in code):
1. Aggregation runs as a planned table read (`GovernedSqlQueryRequest.PlannedTableRead = true`) via `GovernedSqlExecutor.ExecuteCoreAsync`
   (`GovernedSqlExecutor.cs:249`).
2. That call runs `GovernedSqlExecutionService.EnsureClientParameterNames(request.Parameters)` (`GovernedSqlExecutionService.cs:370`).
   It rejects every parameter starting with the reserved prefixes (`__gql_`, `p_rls_`, `rls_`, `p_pd_`, `__rls_ks`, client placeholder prefix) with
   `WebSqlPolicyException("A request parameter uses a reserved gateway parameter name.")`.
3. The gateway's own filter parameters for aggregation (typed filter/tree-filter markers such as `@__gql_tf_N`, `__gql_vf_`) carry the
   reserved prefix, so the gateway rejects its own parameters. `isNull` has no value, so it never trips the guard.
4. The federated DuckDB path has the same guard (`FederatedDuckDbExecutionService.cs:264`) and is likely affected for the same reason.

The guard cannot tell gateway-originated from client-originated parameters. The list field avoids it by a different parameter route; the exact
route is to be confirmed in Phase 2 (open question 1).

## 2. Requirements

1. Aggregation `where` with bound values works on SQL sources, with the same semantics as the list field filter.
2. Values stay bound parameters. A filter value is never inlined into SQL text, in any path, including errors and logs.
3. Aggregation (group by, aggregates, filter) runs at the source; for example "max per group within a time window" is one source query.
4. Fix is structural. The executor receives gateway-originated parameters through a trusted channel that client input cannot populate
   (for example a separate internal-only parameter collection or typed marker on the request), not by name allowlisting or prefix checks.
5. `EnsureClientParameterNames` keeps rejecting every client-supplied reserved name (all prefixes listed above, case-insensitive, with or
   without `@`) on all client-SQL paths, with `PlannedTableRead` true or false.
6. A gateway parameter whose name collides with a client parameter name is a fail-closed error (no overwrite, no merge-by-last-wins).
7. All other behavior of planned table reads (RLS, masking, tenant filter, audit, row limits) is unchanged.

## 3. Acceptance criteria

1. `where: {region: {eq: "north"}}` with `groupBy`, `aggregates`, `limit` returns correct grouped rows on PostgreSQL and SQL Server
   (integration or provider-level tests; unit tests with captured SQL and parameters for the rest).
2. Every comparison operator with a value works in aggregation `where`: `eq`, `ne`, `gt`, `gte`, `lt`, `lte`, `contains`/`startsWith`/
   `endsWith` (where the filter input offers them), `in`, `notIn`, `between` (list/range values), plus `and`/`or`/`not` nesting.
3. Time window: `where: {created_at: {gte: "...", lt: "..."}}` with `MAX`/`MIN`/`SUM`/`AVG`/`COUNT` per group returns the expected values,
   with typed (date/time) parameters bound, not strings concatenated.
4. `isNull` still works; mixing `isNull` and value predicates in one `where` works.
5. `in: []` and `between` with missing bound behave as in the list field (same validation error or empty result, no 500).
6. Federated/DuckDB path: an aggregation over a federated source with a valued `where` works, or, if not reachable from aggregation
   today, a regression test proves the guard change there keeps client rejection (decision in Phase 2).
7. Generated SQL text contains only parameter markers; a test asserts the value literal (for example `north`) never appears in SQL text.
8. Row-policy, masking and tenant filter still apply: with RLS and a masked column on the table, an aggregation with a valued `where` returns
   only permitted rows and cannot filter or group on a masked-denied column beyond what the list field allows.
9. A client SQL request (Web SQL, `PlannedTableRead=false`) with parameter `__gql_tf_0`, `@__gql_rls_0`, `p_rls_x`, `rls_x`, `p_pd_x`,
   `__rls_ks`, any case, still fails with the reserved-name error.
10. A planned read request that sets a client parameter with a reserved name (client-originated) still fails; a trusted gateway parameter
    with the same name as a client parameter fails closed.
11. Regression tests exist for requirement 1 at executor level and for the GraphQL aggregation resolver (no 500 on `eq` with value).

## 4. Security requirements (owner condition, binding for Phase 2/3)

1. Reserved-name guard keeps rejecting client-supplied `__gql_*` and other gateway names. No weakening, no exemption by name or by
   `PlannedTableRead` alone (that flag today means only "gateway planned the SQL", not "parameters are trusted").
2. Trusted parameters are distinguished structurally: only gateway code that builds typed predicates can create them; no deserialization,
   GraphQL variable, header or query-string path can reach the trusted channel.
3. No spoofing or overriding of gateway parameters (RLS, caller attribute `__gql_caller_*`, tenant, pushdown `p_pd_*`) by client values;
   collisions fail closed.
4. RLS, masking, tenant and audit pipeline are not bypassed; trusted parameters are still bound, typed and subject to the same limits.
5. Fail closed on any ambiguity (unknown channel, null/duplicate name, unexpected type): reject, do not run, no value in the error text.
6. Phase 3 mandatory tests: spoof attempts per reserved prefix, collision, case/`@` variants, trusted channel not constructible from client DTOs.

## 5. Non-goals

- No change to Web SQL gating (`WebSql:Enabled`, allowed data sources) and no new config switch.
- No new filter operators, no new aggregate functions, no change to the GraphQL schema.
- No relaxation of the reserved prefix list; no renaming of existing gateway parameter prefixes.
- No in-memory fallback aggregation (aggregation stays pushed to the source).
- No feature doc (docs phase removed).

## 6. Open questions (recommended default taken)

1. How does the list field pass its filter parameters past the guard (different route, or not through `EnsureClientParameterNames`)?
   Default taken: Phase 2 reads that route and reuses it for aggregation if it already is a structural trusted channel.
2. Trusted channel shape: separate `TrustedParameters` collection on `GovernedSqlQueryRequest` vs. typed parameter wrapper.
   Default taken: separate internal-only collection, merged after the client guard, collision fails closed.
3. Is the DuckDB federated guard reachable by aggregation? Default taken: apply the same trusted channel there if reachable, otherwise
   test-only coverage (criterion 3.6).
4. Backport to 1.2.x line? Default taken: no (no production yet); fix on main only.
