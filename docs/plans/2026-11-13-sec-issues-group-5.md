# Phase 3 security review: open-issue plans, priority group 5

- **Date:** 2026-11-14 · **Phase:** 3 (`csharp-security-expert`) · **Base verified:** `origin/main` @ `7a4df959`
- **Reviewed:** [`2026-11-13-plan-issues-sql-engine-dialects.md`](2026-11-13-plan-issues-sql-engine-dialects.md) sections #478, #486, #479, #492. Owner defaults `Q-SQL-1..5` are binding (2026-11-13). `Q-SEC1-*`/`Q-SEC2-*` unconfirmed; nothing here depends on them.
- **Ids:** `SEC-OI-<issue>-<n>`. Every test below carries `[Trait("Category","Security")]`. Binding for Phase 4 and Phase 5.

| Issue | WP | Verdict | Developer-ready |
|---|---|---|:---:|
| #478 | WP-OI-SQL-478-A, -B | APPROVED WITH CONDITIONS (stack safety of downstream walkers is the real DoS control) | yes |
| #486 | WP-OI-SQL-486 | APPROVED WITH CONDITIONS | yes (not parallel with WP-46 PR 3-5/WP-48) |
| #479 | -A, -B, -D (light), -C | APPROVED WITH CONDITIONS (SQL expansion bound; LIKE escaping rules) | yes, order A, B, D, C |
| #492 | WP-OI-SQL-492 | APPROVED WITH CONDITIONS, DEFERRED (Q-SQL-4 = no freeze exception) | no (after Stage 3 exit) |

## #478 AST depth for flat chains

**Verified.** `SqlAstBuilder.cs:29` `EnterScope()` per operator in `VisitAnd/Or/ArithmeticBinary/Concatenation`; backstops `MaxParseTreeDepth=3,000`, `MaxNestingDepth=100`, `MaxQueryLength=65,536`, `ExecutionStackGuard` (28 references in `TrinoSqlEngine`). The depth limit 64 is today what keeps every recursive AST walker (security visitor, generators, row-filter injection, hashing, federation) away from the stack limit; a left-deep chain of 1,000 nodes puts 1,000 frames into each of them. A .NET stack overflow is process-fatal for an authenticated caller.
- SEC-OI-478-1: the chain is collected and folded **iteratively** (no recursion over `context.left` in the builder). The cap `MaxOperatorChainTerms = 1,000` counts the whole left spine of one context type regardless of operator (so `a+b-c+d...` counts as one spine), is a constant, and fails with the typed `AstBuildException` family.
- SEC-OI-478-2: every recursive walker over `BinaryExpression` must be proven at 1,000 terms: a test runs the **full pipeline** (parse, `AstSecurityVisitor`, policy injection, generation for PostgreSQL, SQL Server, Oracle, Databricks, DuckDB, federation staging) for 1,000-term `OR`, `AND`, `||` and `+` chains on a thread with a small stack (512 KiB). Result must be success or a typed error; a walker without `ExecutionStackGuard` gets one or is made iterative. If a walker fails, the cap is lowered (constant), not the test.
- SEC-OI-478-3: mixed alternation (`(a OR b) AND c OR d ...`) still enters one scope per operator switch, so nesting stays at 64 and the 65,536 length bound holds node count; parentheses, NOT, subqueries, CASE and function calls keep entering a scope. Policy-added predicates are appended after the cap check and counted in the same pipeline test.
- SEC-OI-478-4 (OData): `ChainNode` operands are bounded by the existing `MaxFilterLength` (4,096) and an explicit operand cap (1,000); `ToSql` and `CollectReferencedColumns` iterate, call `EnterDepth` once, and `CollectReferencedColumns` returns **every** operand's columns (a missed column would let a required-filter or column-permission check pass).
- SEC-OI-478-5: the AST is byte-identical to today's left-deep build; the golden test covers `AstSecurityVisitor` outcomes (required filters, forbidden columns) for a chain, not only emitted SQL.
- Tests: plan names plus `Chain_1000_Terms_FullPipeline_SmallStack_NoStackOverflow`, `Chain_1001_Terms_Rejected_Typed`, `Mixed_Operator_Arithmetic_Spine_Counts_As_One_Chain`, `Chain_Security_Visitor_Outcome_Identical_To_Recursive_Build`, `OData_Chain_CollectReferencedColumns_Includes_Every_Operand`.

## #486 required filter columns

**Verified.** `GovernedTableReadPlanner.cs:540` passes `filter?.ReferencedColumns` into `RequiredFilterColumnRule.IsColumnSatisfied` (`:104-105` any mention); `ConstrainedColumns` exists only on federation clauses. This is a cost guard, not an authorization boundary (row policies and tenant filters are separate); the plan states that and the code comment must too.
- SEC-OI-486-1: `ConstrainedColumns` null = nothing constrained for **every** producer; a new `TableFilterClause` producer that forgets it fails closed. The fallback to `ReferencedColumns` and the VF-06 special case are deleted, not kept as an alternative. No other read-path code may read `ReferencedColumns` for the required-filter decision (architecture test greps `IsColumnSatisfied` callers).
- SEC-OI-486-2: counted per column: `=` with a literal or parameter operand, `<,<=,>,>=` with a literal or parameter, `BETWEEN`, non-empty `IN`. Not counted: `col = col` or column-to-column comparison, comparison to NULL, a function-wrapped column (`lower(col)`), `<>`, NOT, `NOT IN`, `LIKE`, `IS NULL`, `IS NOT NULL` (Q-SQL-1: one table on all paths, WebSQL and federation included). AND: any item; OR: every branch; NOT flips to none. Keyset cursor bounds never count.
- SEC-OI-486-3: row-policy and tenant-column satisfaction stay as today; document in code that a policy predicate mention counts because policies are operator-authored (accepted residual).
- SEC-OI-486-4: the DML path (`AllowDml` WHERE) uses the same rule through `AstSecurityVisitor`; a test pins that an `UPDATE` with `WHERE col LIKE '%'` against a required filter column is rejected.
- SEC-OI-486-5: the error stays the fixed `REQUIRED_FILTER_COLUMN_MISSING` text (`AggregateErrors.cs:71` shape); the column name is not added to client output (it may be a column the caller cannot see).
- Tests: plan names plus `ColumnToColumn_And_NullComparison_Do_Not_Satisfy`, `FunctionWrappedColumn_Does_Not_Satisfy`, `Between_And_NonEmptyIn_Satisfy`, `New_Filter_Producer_Without_ConstrainedColumns_Fails_Closed`, `Dml_Where_Like_On_Required_Column_Is_Rejected`, `Error_Does_Not_Name_Column`, parity across REST, GraphQL, OData, MCP, WebSQL and federation.

## #479 dialect semantics

**Verified** (generators in `src/TrinoSqlEngine/Ast/Generators/`). Plan says PR A/B/D need no Phase 3; two issues are security-relevant anyway.
- SEC-OI-479-1 (A, B, D): the rewrites duplicate operand expressions (`CASE` around `substr` start and length, Oracle month-end `CASE` repeating `x` and `n`). Nested calls (`substr(substr(...))`, chained `date_add`) then grow the emitted SQL exponentially from a small input; `MaxQueryLength` bounds the input only. Binding: operands are duplicated only when they are a column, literal or bound parameter; any other operand uses a non-duplicating form or is rejected with `SqlCompileNotSupportedException`; in addition a cap on the emitted SQL length (for example 4 x `MaxQueryLength`) fails with a typed error. Duplicating non-deterministic functions (`random()`) is thereby excluded.
- SEC-OI-479-2 (A): `IsIntegralDivision` is derived only from literals, explicit integer casts and declared catalog types; unknown = not integral = today's behaviour. The flag is part of the AST cache or template key (or the key contains the catalog fingerprint) so a type change cannot reuse a stale classification. Division by zero behaviour unchanged (documented).
- SEC-OI-479-3 (C, LIKE): only dialect-specific extra metacharacters are escaped (SQL Server `[`, PostgreSQL/Databricks `\`); `%` and `_` stay wildcards. The user pattern remains a bound value (no pattern text in SQL), `ESCAPE` is a registered constant fragment, a user `ESCAPE` must be a single-character literal (anything else = typed rejection), and the user escape character itself is escaped. Non-literal patterns on SQL Server are rejected (Q-SQL-3). The SQLE-09 policy branch is untouched. Pattern length is bounded (existing literal limit applies; add 1,024 if none).
- SEC-OI-479-4 (B, concat): parenthesising arithmetic children of `Concat` must not change the AST seen by `AstSecurityVisitor`; it only affects emission.
- Tests: plan names plus `Nested_Substr_20_Levels_Emission_Bounded_Or_Rejected`, `Nested_DateAdd_Month_20_Levels_Emission_Bounded_Or_Rejected`, `NonSimple_Operand_Is_Not_Duplicated`, `IntegralFlag_Part_Of_Cache_Key`, `UserLike_Wildcards_Preserved_Metacharacters_Escaped` (SQL Server `[`, PostgreSQL `\`), `UserLike_Multichar_Or_NonLiteral_Escape_Rejected`, `UserLike_Escape_Char_Itself_Escaped`.

## #492 query analysis target scope (deferred)

**Verified** `QueryAnalysisService.cs:424` resolves the quota context from the effective principal; `ClientTierResolver` has a subject-keyed cache (`_apiKeyCache`, 10,000 entries, 10 min).
- SEC-OI-492-1: the cloned principal is created per call, handed **only** to `ClientTierResolver`, never stored in `HttpContext`, DI or ambient state; access decisions, visibility, masking, mandatory filters and audit actor remain the calling token's.
- SEC-OI-492-2: the tier resolution for a target scope must not write the subject-keyed cache entry used for the real caller (cache poisoning would give the caller another scope's limits on real requests): bypass the cache or key by scope set; test `TargetScope_Does_Not_Poison_Caller_Tier_Cache`.
- SEC-OI-492-3: `AllowedTargetScopes` accepts only read-only scopes (startup validation rejects a scope that carries write, admin or simulate capability); match is ordinal, length at most 128; unknown and not-allowed answer the same validation error and never list the allowed set. Combined with `TargetSubjectId` = validation error (plan kept). The response states the estimate is advisory; execution still uses the real token.
- SEC-OI-492-4: the analysis audit records the requested target scope (a configured name, no values); no bucket is read for write or changed (plan test kept).
- Tests: plan names plus `TargetScope_Does_Not_Poison_Caller_Tier_Cache`, `AllowedTargetScopes_WithWriteScope_FailsStartup`, `Unknown_And_NotAllowed_Scope_Produce_Identical_Error`, `Cloned_Principal_Not_Visible_To_Other_Services`.

## Anything the plans missed

1. #478 raising the depth limit removes the only stack backstop for downstream walkers; a pipeline test at 1,000 terms is mandatory (SEC-OI-478-2).
2. #479 operand duplication creates exponential SQL growth (SEC-OI-479-1).
3. #486 column-to-column, NULL and function-wrapped comparisons must not count; DML path parity (SEC-OI-486-2/4).
4. #492 tier cache poisoning (SEC-OI-492-2).

## New owner questions (NOT confirmed)

| Id | Question | Recommended default |
|---|---|---|
| Q-SEC5-1 | #478: lower `MaxOperatorChainTerms` if a walker fails the 1,000-term small-stack test? | Yes, lower the constant (never relax the test); SQL Server integration at the final value |
| Q-SEC5-2 | #479: cap emitted SQL at 4 x `MaxQueryLength`? | Yes, constant |
| Q-SEC5-3 | #486: do comparisons against another column of the same table (`a.col = a.other`) ever count? | No |
