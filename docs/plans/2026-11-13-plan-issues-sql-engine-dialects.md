# PLAN-OI-SQL: Open issues, SQL engine and dialects (#478, #479, #486, #492)

- **Date:** 2026-11-13 · **Base:** `origin/main` @ `10400ec4` · **Phase:** 2 (architecture plan) · **Status:** PLANNED, Phase 3 pending
- **Origin plan:** [`00-master-plan-overview.md`](00-master-plan-overview.md) · **Program:** [`simplification/README.md`](simplification/README.md), assessment table "Issues (61)" in [`2026-11-05-plan-project-simplification.md`](2026-11-05-plan-project-simplification.md) · **Binding:** PRD [`2026-11-05-req-project-simplification.md`](2026-11-05-req-project-simplification.md) section 2.0 (OD-1..OD-45; a later OD wins), [ADR-019](../adr/ADR-019-sql-engine-build-vs-buy.md) (keep and shrink the engine)
- **Freeze:** PRD 5.1. Bug and security fixes are allowed without approval; new request fields or options need an owner freeze-exception. **PR limit:** <= 400 net added `src/` lines per PR (PRD 5.3).
- **Shared-file rule:** WP-SIMPL-31 (SQLite data-source removal) must not run in parallel with any PR here that touches `src/TrinoSqlEngine/**` (WP-31 spec, "Parallel"); the later PR rebases.

## Summary

| Issue | Verdict | WP / spec | Open on main @ `10400ec4` | Size | Phase 3 |
|---|---|---|---|:---:|:---:|
| #478 | NEW PLAN | WP-OI-SQL-478-A (SQL engine), WP-OI-SQL-478-B (OData); assessment: "SQL engine lane with #481" (#481 merged in #644 without touching this) | All of it | M | yes |
| #479 | PARTLY COVERED | WP-OI-SQL-479-A..D; SQLite items closed by WP-SIMPL-31; Oracle proof by the Oracle suite (OD-12, WP-SIMPL-21 note) | All non-SQLite items; two extra gaps found (Oracle `ADD_MONTHS` month-end, Databricks integer `/`) | L | PR C only |
| #486 | NEW PLAN | WP-OI-SQL-486; assessment bundled it with #480 (merged in #643 without touching this) | All of it | S-M | yes |
| #492 | NEW PLAN (deferred) | WP-OI-SQL-492; assessment verdict DEFERRED (default taken): after Stage 3 exit | All of it (feature) | M | yes |

Recommended order: #486 (security guard, S) -> #478 A/B (parallel; different projects) -> #479 A, B, D, C -> #492 after the freeze.

## #478 Flat AND/OR/+/|| chains hit the AST depth limit (SQL engine 64, OData ~37)

- **Verdict:** NEW PLAN (bug). No fix on main; neither file changed since 2026-11-06.
- **Problem:** left-associative chains count one depth level per operator, so `id = 1 OR ... OR id = 70`, 60-term `||` chains and OData `$filter` with ~37 `or` terms are rejected as too deep.
- **Verified state:** `src/TrinoSqlEngine/Ast/Builder/SqlAstBuilder.cs` `EnterScope()` (l.29, limit `AstBuilderOptions.MaxAllowedAstDepth = 64`, `AstBuilderOptions.cs:12`) is called in `VisitAnd` (l.868), `VisitOr` (l.876), `VisitArithmeticBinary` (l.1003), `VisitConcatenation` (l.1131), each recursing into `context.left`. OData moved to `src/Autheris.Application/OData/ODataFilterParser.cs`: `ParseOr`/`ParseAnd` (l.410/430) already loop, but build nested `BinaryOpNode` (l.714) whose `ToSql` calls `ctx.EnterDepth()` (l.602, limit `MaxRecursionDepth + 5` = 37, `MaxRecursionDepth = 32` l.19); `CollectReferencedColumns` has the same depth check. DoS backstops that stay: `FastSqlEngine.MaxParseTreeDepth = 3_000` (l.73), `MaxNestingDepth = 100` (token-level parentheses, l.67), `MaxQueryLength = 65_536` (l.61), `ExecutionStackGuard` (20 call sites), OData `MaxFilterLength = 4096`.
- **Root cause:** the depth counter measures tree height, and a flat chain of N terms is a tree of height N.
- **Design:**
  1. SQL engine: in the four visitors, collect the operands of a same-operator left spine iteratively (walk `context.left` while it is the same context type and operator), visit each operand, and fold the result left-deep bottom-up; `EnterScope()` once per chain. The produced AST is byte-identical to today (left-deep `BinaryExpression`), so generators, `AstSecurityVisitor` and template caches are unchanged. Parentheses, subqueries, CASE, function calls and NOT still enter a scope.
  2. New constant `SqlAstBuilder.MaxOperatorChainTerms = 1_000` (constant, not an option: no freeze ratchet growth); a longer chain fails with the existing `AstBuildException` depth message family. Reason: every downstream walker still recurses once per AST level; the cap keeps that bounded independently of the parse-tree limit.
  3. OData: `ParseOr`/`ParseAnd` build one n-ary `ChainNode(op, operands)` instead of nested `BinaryOpNode`s; `ToSql` and `CollectReferencedColumns` iterate operands and call `EnterDepth` once. `GroupingNode`, `NotOpNode` and `FunctionCallNode` keep counting.
- **Work packages:** WP-OI-SQL-478-A SQL engine (~90 src lines, 1 PR); WP-OI-SQL-478-B OData (~80 src lines, 1 PR). Bug fix, no freeze-exception.
- **Tests (TDD, write first):**
  - `TrinoSqlEngine.Tests/Ast/AstDepthGuardTests`: `Or_Chain_Of_200_Terms_Is_Accepted`, `And_Chain_Of_200_Terms_Is_Accepted`, `Concat_Chain_Of_200_Terms_Is_Accepted`, `Arithmetic_Chain_Of_200_Terms_Is_Accepted`, `Chain_Ast_Is_Identical_To_Recursive_Build` (golden SQL per dialect for a 5-term chain, unchanged).
  - `[Trait("Category","Security")]` `Deep_Parenthesised_Nesting_Is_Still_Rejected` (65 nested parentheses), `Nested_Subqueries_Still_Count_Per_Level`, `Chain_Above_MaxOperatorChainTerms_Is_Rejected_Typed` (typed error, no `StackOverflowException`, no `InsufficientExecutionStackException` leaking), `Mixed_And_Or_Alternation_Counts_Each_Switch` (`a AND b OR c AND d ...` alternation still bounded).
  - `Autheris.Tests.Unit/OData/ODataFilterParserRecursionTests`: `Or_Chain_Of_200_Terms_Is_Accepted`, `And_Chain_Within_MaxFilterLength_Is_Accepted`, `[Trait("Category","Security")]` `Nested_Parentheses_Beyond_Limit_Are_Rejected`, `Not_Nesting_Beyond_Limit_Is_Rejected`, `ChainNode_CollectReferencedColumns_Returns_All_Columns`.
  - Integration (PostgreSQL, SQL Server): one 200-term OR query executes through WebSQL and OData and returns the expected rows.
- **Dependencies:** none; not in parallel with WP-SIMPL-31 (A) or WP-SIMPL-26 OData hardening if it moves `ODataFilterParser` again (B rebases).
- **Risks:** SQL Server rejects very deep generated expressions ("nested too deeply") for long OR chains; the 1,000 cap plus an integration test at 1,000 terms on SQL Server bounds it (lower the constant if the test fails). Dialect `MaxExpressionDepth` (SQLite 1,000, `DialectCapabilityTable.cs:224`) still applies.
- **Owner questions:** none.
- **Size:** M (~170 src, ~350 test, 2 PRs).

## #479 Dialect semantics differ from Trino

- **Verdict:** PARTLY COVERED. SQLite items are closed by WP-SIMPL-31 (deletes `SqliteDialectGenerator`; rule 3 "no fixes in code a later WP deletes"); all other items are NEW PLAN. SQLite is still admitted today (`DataSourceDialectValidator.AdmittedDialects`, `src/Autheris.Api/Extensions/DependencyInjection/DataSourceDialectValidator.cs:11-18`), so this depends on Q-SQL-2.
- **Verified state per item** (generators in `src/TrinoSqlEngine/Ast/Generators/`):

| Item | State on main | Plan |
|---|---|---|
| Oracle modulo `%` | Open: `SqlDialectGeneratorBase.Expressions.cs:408` emits `%`; no Oracle override of `FormatBinaryExpression` (l.255, virtual) | PR A: `MOD(a, b)` |
| Oracle integer `/` | Open: no integral handling anywhere; no expression typing exists in the engine | PR A: `TRUNC(a / b)` when both operands are integral |
| Databricks integer `/` (new gap) | Open: Spark `/` always divides as floating point; base operator emitted | PR A: `a DIV b` when both operands are integral |
| `date_add('quarter', ...)` | Open: PostgreSQL `INTERVAL '1 quarter'` (`PostgreSqlDialectGenerator.cs:91/100`), Oracle `NUMTODSINTERVAL(n,'QUARTER')` (`OracleDialectGenerator.cs:256`), base/Ansi/DuckDB `INTERVAL 'n' QUARTER` (`SqlDialectGeneratorBase.Functions.cs:424`); SQL Server (`SqlServerDialectGenerator.cs:277`) and Databricks `DATEADD(QUARTER, ...)` are correct | PR B: quarter = months x 3 in PostgreSQL, Oracle (`ADD_MONTHS`) and base (verify DuckDB; keep native if it accepts `QUARTER`) |
| Oracle month arithmetic at month end (new gap) | Open: `ADD_MONTHS` (`OracleDialectGenerator.cs:245`) maps the last day of a month to the last day of the target month (`2024-02-29 + 1 month` = `2024-03-31`, Trino `2024-03-29`) | PR B: `CASE WHEN EXTRACT(DAY FROM x) <= EXTRACT(DAY FROM LAST_DAY(ADD_MONTHS(x, n))) THEN ADD_MONTHS(...)` form that keeps the day of month, clamped |
| `||` precedence | Open: base precedence `Concat = 4` below `+`/`-` (`Expressions.cs:457-466`), SQL Server override 5 (`SqlServerDialectGenerator.cs:520`); Oracle binds `||` equal to `+`/`-` | PR B: always parenthesise arithmetic children of `Concat` in every dialect |
| User `LIKE` without ESCAPE | Open: `Expressions.cs:61-91` adds `ESCAPE` only for policy patterns (SQLE-09); user patterns pass through (PostgreSQL and Databricks backslash default escape, SQL Server `[...]` classes) | PR C (see design) |
| SQL Server `substr` start <= 0, `strpos` collation | Open: `SqlServerDialectGenerator.cs:331-352` maps to `SUBSTRING`/`CHARINDEX` unchanged | PR D; also verify PostgreSQL `substr` with start <= 0 (same divergence) and fix in the same helper |
| SQLite `date_add` month overflow, `CAST AS timestamp`, LIKE case | Open in `SqliteDialectGenerator.cs:187-212` | Closed by WP-SIMPL-31 (Q-SQL-2) |

- **Root cause:** the generators translate syntax, not Trino semantics; there is no per-dialect parity proof for these constructs.
- **Design:**
  - PR A: a small `IntegralOperandClassifier` (TrinoSqlEngine, internal) decides "provably integral" from integer literals, `CAST(... AS integer|bigint|smallint|tinyint)` and column references whose declared catalog type (WP-SIMPL-44 declared catalog) maps to an integer type; it is computed by `AstSecurityVisitor` while it already resolves column references and stored on the `BinaryExpression` (new init-only `bool IsIntegralDivision`), so generators stay type-free. Unknown type = not integral = today's behaviour (no rejection). Oracle overrides `FormatBinaryExpression` for `Modulo` (always `MOD`) and integral `Divide` (`TRUNC`); Databricks for integral `Divide` (`DIV`). Division by zero behaviour is unchanged per dialect (documented accepted difference).
  - PR C: when the pattern is a bound string literal, rewrite it to an explicit-escape form: escape the escape character itself and the dialect metacharacters (SQL Server `[`; PostgreSQL/Databricks `\`) and append `ESCAPE '\'` as a registered constant fragment, exactly like the SQLE-09 policy branch; a user-supplied `ESCAPE` keeps its character and gets the same metacharacter escaping. A non-literal pattern (column or expression) on SQL Server is rejected with `SqlCompileNotSupportedException` (Q-SQL-3); on PostgreSQL/Databricks it gets `ESCAPE ''`/explicit escape only.
  - PR D: one `FormatTrinoSubstring` helper per affected dialect implementing Trino rules (start 0 yields empty, negative start counts from the end) with bound arguments in a `CASE`; `strpos` emits `CHARINDEX(... COLLATE Latin1_General_BIN2)` on the needle and haystack.
- **Work packages:** WP-OI-SQL-479-A (Oracle `MOD`/`TRUNC`, Databricks `DIV`, classifier; ~200 src), -B (quarter, Oracle month end, concat parentheses; ~120 src), -C (LIKE escape; ~140 src), -D (substr/strpos; ~120 src). Bug fixes, no freeze-exception. Order A, B, D, C.
- **Tests (TDD):** golden SQL per dialect in `TrinoSqlEngine.Tests/Ast/DialectGenerators/` (`OracleModuloEmitsModTests`, `OracleIntegralDivisionEmitsTruncTests`, `DatabricksIntegralDivisionEmitsDivTests`, `DateAddQuarterEmitsMonthsTests`, `OracleAddMonthsMonthEndTests`, `ConcatArithmeticChildIsParenthesisedTests`, `UserLikeEscapeEmissionTests`, `SqlServerSubstrStartTests`, `SqlServerStrposBinaryCollationTests`); `IntegralOperandClassifierTests` (literal, cast, declared integer column, decimal column, unknown column = not integral). Execution parity (Trino-expected result vs dialect result) in `Autheris.Tests.Integration/AstCompilerOracleExecutionTests`, `AstCompilerDatabricksSparkExecutionTests` and the PostgreSQL/SQL Server dialect parity in `Autheris.Tests.Parity/Dialects/DialectTests` (one case per row of the table). `[Trait("Category","Security")]` for PR C: `UserLikePattern_Metacharacters_Are_Bound_Not_Emitted` (no pattern text in the SQL text, escape is a constant fragment), `SqlServer_NonLiteral_Like_Pattern_Is_Rejected`, `Policy_Like_Pattern_Escape_Unchanged` (SQLE-09 regression).
- **Dependencies:** WP-SIMPL-44 (declared column types, merged PR 1/2) for PR A; WP-SIMPL-31 sequencing (no parallel PR on `src/TrinoSqlEngine/**`); Oracle suite on PRs only as the security-tagged smoke subset (#522, OD-12), full Oracle parity nightly.
- **Risks:** `TRUNC`/`DIV` only for provably integral operands leaves mixed-type expressions with unknown column types at today's semantics (accepted, documented in the parity results); LIKE rewrite changes plan-cache keys (template cache re-warms once).
- **Owner questions:** Q-SQL-2, Q-SQL-3.
- **Size:** L (~580 src, ~700 test, 4 PRs).

## #486 Required filter column is satisfied by any reference to the column

- **Verdict:** NEW PLAN (security hardening; cost/DoS guard, not a row-filter bypass). Not fixed by #480 (PR #643 did not touch these files).
- **Verified state:** `src/Autheris.Application/DataSources/Services/GovernedTableReadPlanner.cs:529-560` `EnsureRequiredFilterColumnsSatisfied` passes `filter?.ReferencedColumns` to `RequiredFilterColumnRule.IsColumnSatisfied` (`RequiredFilterColumnRule.cs:71-106`), which accepts any mention (l.104-105). Keyset path `GovernedTableReadPlanner.Keyset.cs:437-438` only excludes the empty-`notin` tautology (VF-06). `TableFilterClause.ConstrainedColumns` (`IGatewayExecutionService.cs:74`, "null = none, fail closed") exists but is only set by the federation builders (`Sql/Federation/Optimization/PushedClauseBuilder.cs:182`, `Sql/Services/CrossSourcePlanner.cs:442`). The WebSQL rule `AstSecurityVisitor.RequiredFilters.IsColumnConstrained` (`src/TrinoSqlEngine/Ast/Visitors/AstSecurityVisitor.RequiredFilters.cs:206`) and its federation mirror `RequiredFilterConstraint` (`Sql/Federation/Pushdown/RequiredFilterConstraint.cs:12-19`) count `=`, ranges, BETWEEN, IN, **LIKE and IS NULL**; never `<>`, NOT, IS NOT NULL; OR only when every branch constrains.
- **Root cause:** the structured read paths (REST/GraphQL/MCP tree filters, OData) never compute which columns a filter constrains, so the planner falls back to "mentioned".
- **Design:** every producer of a `TableFilterClause` on a governed read path sets `ConstrainedColumns`: `TreeFilterClauseBuilder.Build` (`Sql/Tree/TreeFilterClauseBuilder.cs:31`) from the n-ary `TreeAndFilter`/`TreeOrFilter`/`TreeNotFilter`/`TreeComparison` model (AND: any item; OR: every item; NOT: none; comparison: `Eq`, `Gt`, `Gte`, `Lt`, `Lte`, non-empty `In`); `ODataFilterParser` (l.62) from its AST with the same rule; `SqlTableReadExecutor` (l.356/393) and `SemanticMetricQueryService.ConsentAndEvaluation.cs:309` from their structured inputs. `KeySetClauseBuilder` clauses (cursor bounds) never count. `EnsureRequiredFilterColumnsSatisfied` and the keyset path use `filter?.ConstrainedColumns ?? []` (null = nothing constrained, fail closed); the VF-06 special case is deleted. Tenant-column and row-policy satisfaction stay unchanged. One shared rule table (`RequiredFilterOperatorRule`) used by the tree, OData and the WebSQL/federation helpers decides LIKE/IS NULL per Q-SQL-1.
- **Work packages:** WP-OI-SQL-486 (one PR, ~150 src). Security fix, no freeze-exception.
- **Tests (TDD), all `[Trait("Category","Security")]`:** `Autheris.Tests.Unit/Data/RequiredFilterColumnRuleTests`: `Neq_Does_Not_Satisfy`, `IsNull_Does_Not_Satisfy` (per Q-SQL-1), `Like_Percent_Does_Not_Satisfy`, `NotIn_Does_Not_Satisfy`, `Or_With_Unconstrained_Branch_Does_Not_Satisfy`, `Not_Eq_Does_Not_Satisfy`, `Eq_In_Range_Satisfy`, `Or_Constrained_On_Both_Branches_Satisfies`, `Null_ConstrainedColumns_Fails_Closed`; same cases on the keyset path (`GovernedTableReadPlannerKeysetRequiredFilterTests`) and for OData (`ODataRequiredFilterConstraintTests`); `TreeFilterClauseBuilderConstrainedColumnsTests`; integration `RequiredFilterColumnsParityIntegrationTests` extended so REST, GraphQL, OData, MCP and WebSQL reject and accept the same cases.
- **Dependencies:** none; touches `GovernedTableReadPlanner*` (shared with WP-46 PR 3-5 and WP-48): not in parallel with them, later PR rebases.
- **Risks:** clients that relied on `ne`/`like` to pass the guard now get `REQUIRED_FILTER_COLUMN_MISSING` (intended; no production deployment).
- **Owner questions:** Q-SQL-1.
- **Size:** S-M (~150 src, ~300 test, 1 PR).

## #492 Query analysis: estimate against the limit of another read-only scope of the same person

- **Verdict:** NEW PLAN, deferred: a feature (new request field `targetScope`, new option list) under the freeze; the assessment default "revisit after Stage 3 exit" stands unless the owner grants a freeze-exception (Q-SQL-4).
- **Verified state:** `AnalyzeQueriesHttpRequest(Queries, TargetSubjectId, TargetRoles)` (`src/Autheris.Api/Endpoints/QueryAnalysisAndProfilingEndpoints.cs:21-24`) has no scope field. `QueryAnalysisService.cs:424` resolves the quota context from `effectivePrincipal`; l.563 from `callerPrincipal` for the caller bucket summary; without `governance:simulate`/`GovernanceAdmin` the target is dropped (l.141-152). Scope limits are resolved by `ClientTierResolver` (`src/Autheris.Application/State/Caching/Services/ClientTierResolver.cs`) from `ScopeLimits`/`ScopeTierMappings` (`RateLimitingOptions`); options section `Gateway:Governance:QueryAnalysis` (`GovernanceOptions.cs:19-21`).
- **Design:** optional `TargetScope` on the request; `GovernanceQueryAnalysisOptions.AllowedTargetScopes` (empty = feature off). When set and allowed: clone the caller's own principal with its scope claims replaced by exactly that scope, and pass it only to `_clientTierResolver.ResolveAsync` (l.424); access decisions, visibility, masking and mandatory filters stay those of the calling token (the estimate never widens what the caller can see). Not combinable with `TargetSubjectId` (validation error). `maxAllowedCost`, `isWithinCostLimit`, `rowsPerRequest` follow the target policy; `secondsAtCurrentBucket` omitted; no bucket is read for writing or changed. Unknown or not-allowed scope = validation error, no estimate.
- **Work packages:** WP-OI-SQL-492 (one PR, ~180 src); freeze-exception required (grows the options ratchet by one entry and the request contract).
- **Tests (TDD):** `QueryAnalysisTargetScopeTests`: `TargetScope_Uses_Target_Tier_Limit` (2,500/10,000/6,000 example), `Without_TargetScope_Uses_Caller_Limit`, `RowsPerRequest_Matches_Execution_Admission` (integration parity at the value and +1 with a real `Apps.Read` token); `[Trait("Category","Security")]` `TargetScope_Not_On_Allowlist_Is_Rejected_Without_Estimate`, `Empty_Allowlist_Disables_Feature`, `TargetScope_With_TargetSubjectId_Is_Rejected`, `TargetScope_Does_Not_Change_Access_Decisions` (hidden dataset stays hidden, masked column stays masked), `Analysis_Leaves_All_Buckets_Unchanged`, `Analysis_Reads_No_Source_Rows`.
- **Dependencies:** WP-SIMPL-43 (scope/role canonicalisation, merged up to PR 3b) and WP-SIMPL-46 decision point (analysis uses the caller's decisions); WP-SIMPL-07 ratchet baseline update in the same PR.
- **Risks:** the response discloses another scope's configured limits; bounded by the allowlist (limits are operator configuration, not data).
- **Owner questions:** Q-SQL-4, Q-SQL-5.
- **Size:** M (~180 src, ~250 test, 1 PR).

## Open owner questions

| Id | Question | Recommended default |
|---|---|---|
| Q-SQL-1 | Do `LIKE` and `IS NULL` satisfy a required filter column? Today WebSQL and federation count them, the issue says they must not. | One rule on every path: `=`, ranges, BETWEEN and non-empty IN only; LIKE and IS NULL no longer count, also on WebSQL and federation (tightening, no production yet). |
| Q-SQL-2 | Fix the SQLite items of #479 or leave them to WP-SIMPL-31? | Leave them: WP-SIMPL-31 deletes the SQLite generator; if the owner stops before WP-31 (OD-2), plan a follow-up PR then. |
| Q-SQL-3 | SQL Server `LIKE` with a non-literal pattern (column or expression): reject or accept the `[...]` class divergence? | Reject with a typed `SqlCompileNotSupportedException`; literal patterns (the normal case) are escaped and accepted. |
| Q-SQL-4 | Freeze-exception for #492 (`targetScope`, new option list)? | No: keep DEFERRED until Stage 3 exit (assessment default); the plan is ready to start then. |
| Q-SQL-5 | #492: report the target scope's bucket state? | No: omit `secondsAtCurrentBucket` for a target scope. |

## Phase 3 review scope

- WP-OI-SQL-486: required-filter rule change on every read path (fail-closed `ConstrainedColumns`, parity across REST/GraphQL/OData/MCP/WebSQL, Q-SQL-1).
- WP-OI-SQL-478-A/B: DoS backstop after relaxing depth counting (chain cap, stack-guard coverage of downstream walkers, OData `ChainNode`).
- WP-OI-SQL-479-C: user LIKE pattern rewriting and constant-fragment registration (SQLE-09 invariants, no literal text in emitted SQL).
- WP-OI-SQL-492 (when unfrozen): `targetScope` must never change access decisions or buckets.
- WP-OI-SQL-479-A/B/D: no Phase 3 needed (semantic correctness only; parity tests are the gate).

## Owner decisions (2026-11-13)

The owner confirmed the recommended default of every open question (`Q-*`) in this file. The defaults in the question tables are binding for the implementation; a later owner decision wins. Phase 3 (security review) still applies before implementation starts.
