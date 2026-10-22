# Plan: Security review findings, area SQLE (SQL engine), HIGH and MEDIUM

- Source: `docs/plans/security.txt` (SQLE-01, -02, -03, -04, -05, -08, -09, -10, -12). LOW findings (SQLE-06, -07, -13, -14) are out of scope.
- Branch basis: `fix/security-review-open-findings` (verified 2026-10-22; all cited locations re-read, all findings still present).
- No production yet: breaking behaviour changes (new rejections, changed emitted SQL) are accepted.
- Phase 2 author: `csharp-architect`. Next: Phase 3 (`csharp-security-expert`), then Phase 4 per work package.

## 1. Findings

### SQLE-10 (HIGH, security) Cache-hit rebind drops LIKE escaping
- Verified: `SqlEmitterContext.BindPolicy(..., escapeLikePattern: true)` (Ast/Buffer/SqlEmitterContext.cs:134-152) binds `EscapeLike(text)` only at emit time. `SqlSlot` (Ast/Emit/CompiledSqlTemplate.cs:17-28) has no escape flag; `CompiledSqlTemplate.Rebind` (:71-99) binds `current.Value` raw. Cache hit path: `GovernedSqlCompiler.cs:138`.
- Fix:
  1. Move `EscapeLike` to `internal static class LikePatternEscaper { public static string Escape(string text); }` (Ast/Buffer/LikePatternEscaper.cs), used by emitter and rebind.
  2. Add `bool EscapeLikePattern = false` to `BoundParameter` (Ast/Emit/CompiledSql.cs:20) and `SqlSlot`; `BindPolicy` sets it on the escaped path; `ToSlots` copies it.
  3. `Rebind`: when `slot.EscapeLikePattern`, require `current.Value is string` (else `SecurityException`, same message as emit) and bind `LikePatternEscaper.Escape(text)`.
  4. Add `SecurityControl.CacheRebindLikeEscape` hook in `Rebind` so the mutation suite proves the test kills the "raw value" mutant.
- Fail-closed: non-string value for an escaped slot throws on hit and miss alike.
- Red tests (tests/TrinoSqlEngine.Tests/Security/SqleCacheLikeEscapeTests.cs): `PolicyLikeParameter_CacheHit_BindsEscapedValue` (miss and hit both bind `sales\_eu\%`), `PolicyLikeParameter_CacheHit_DifferentUserValue_IsEscaped`, `PolicyLikeParameter_CacheHit_NonStringValue_Throws`, `Rebind_SlotWithoutEscapeFlag_BindsRaw` (regression for `=` comparisons).
- Breaking: `BoundParameter`/`SqlSlot` gain a field (positional records; update constructors in tests).

### SQLE-09 (MEDIUM, security) Policy LIKE escaping only for a bare parameter
- Verified: generator branch `lk.Pattern is PolicyParameterExpression` (SqlDialectGeneratorBase.Expressions.cs:64); `PolicyExpressionParser.VisitLikeExpression` (:475-487) visits a non-literal pattern generically, so string literals inside `:org || '%'` become `NewLiteralParameter(..., isLikePattern: false)`; `DialectCapabilityValidator.cs:41` matches only the bare shape.
- Fix:
  1. `PolicyExpressionParser.VisitLikeExpression`: accept a pattern only of shape `Literal | DeclaredParam | Concat(pattern, pattern)` (recursive); anything else (function call, CASE, arithmetic, column) is rejected with `Reject("LIKE pattern in policy predicates may only combine literals and parameters with ||.")`. String literals in the tree become `NewLiteralParameter(..., isLikePattern: true)` (admin wildcards intentional); declared parameters keep `IsLikePattern = false`.
  2. Generator LIKE branch: condition becomes `context.IsBound && lk.Escape == null && ContainsPolicyParameter(lk.Pattern)`; emit the pattern by walking the Concat tree, binding each `PolicyParameterExpression` with `BindPolicy(p, escapeLikePattern: !p.IsLikePattern)` and each Concat via the dialect operator (SQL Server `+`, parenthesized per SQLE-05), then append the constant `ESCAPE '\'` fragment. Add `private static bool ContainsPolicyParameter(Expression)` in the same partial.
  3. `DialectCapabilityValidator`: replace the bare-shape case with `case LikeExpression lk when capabilities.LikeIsCaseInsensitive && ContainsPolicyParameter(lk.Pattern)`.
- Fail-closed: unsupported pattern shapes are rejected at policy parse time; every declared value in a LIKE pattern is escaped, also on cache hits (via SQLE-10 slot flag).
- Red tests (tests/TrinoSqlEngine.Tests/Compiler/PolicyExpressionParserTests.cs + Security/SqlePolicyLikeConcatTests.cs): `PolicyLike_ConcatPrefix_EscapesDeclaredValue_KeepsLiteralWildcard` (PostgreSql: `LIKE ($2 || $3) ESCAPE '\'`, $2=`sales\_eu`, $3=`%`), `PolicyLike_ConcatPrefix_SqlServer_EmitsPlusAndEscape`, `PolicyLike_ConcatPrefix_Sqlite_Rejected`, `PolicyLike_FunctionInPattern_Rejected`, `PolicyLike_ConcatPrefix_CacheHit_StillEscaped`.
- Breaking: policy LIKE patterns with functions/CASE are rejected; literal `\` in a concat pattern now acts as escape character (same as the bare-literal case today).
- Depends on SQLE-10 (escaper + slot flag) and SQLE-05 (Concat precedence on SQL Server).

### SQLE-01 (HIGH, bug) Unary plus compiled to NOT
- Verified: SqlAstBuilder.cs:943 maps every non-MINUS operator to `UnaryOperator.Not`.
- Fix: `VisitArithmeticUnary` switches on the token: `MINUS => new UnaryExpression(Negate, operand)`, `PLUS => operand` (identity), default `throw Unsupported("arithmetic unary operator")`. `UnaryOperator.Not` is produced only by `VisitLogicalNot`.
- Red tests (tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs or new Ast/SqleBuilderCorrectnessTests.cs): `UnaryPlus_Literal_IsIdentity` (`WHERE id = +5` emits `"id" = $2`, no NOT), `UnaryPlus_Column_InSelect_Sqlite`, `UnaryPlus_InDeleteWhere_Sqlite_NoNot`, `UnaryMinus_Unchanged`.

### SQLE-02 (MEDIUM, bug) CORRESPONDING dropped
- Verified: `VisitSetOperation` (SqlAstBuilder.cs:422-434) ignores `context.corresponding()`; grammar SqlBase.g4:311-312.
- Fix: first statement after scope entry: `if (context.corresponding() != null) throw Unsupported("CORRESPONDING");` (default taken: reject, no AST modelling).
- Red tests: `SetOperation_Corresponding_Rejected` for UNION, EXCEPT, INTERSECT (`AstBuildException`).

### SQLE-03 (MEDIUM, bug) INTERSECT precedence lost on Oracle/SQLite
- Verified: `GenerateSetOperation` (SqlDialectGeneratorBase.cs:472-489) emits operands flat; no override. Parenthesized query primaries are rejected (`VisitSubquery`, :472), so a right-nested `SetOperationQuery` arises only from INTERSECT precedence.
- Fix (default taken: reject, not rewrite): add `bool IntersectBindsTighter = false` to `DialectCapabilities`; set `true` in `DialectCapabilityTable` only for Ansi, PostgreSql, SqlServer, DuckDb, Snowflake, Databricks after Phase 4 confirms each vendor rule (document per entry). `DialectCapabilityValidator`: `case SetOperationQuery { Right: SetOperationQuery } when !capabilities.IntersectBindsTighter => throw new SqlCompileNotSupportedException(Construct, "INTERSECT mixed with UNION/EXCEPT")`. Left-nested chains stay allowed (left-to-right evaluation is correct on all dialects).
- Fail-closed: unknown/new dialect defaults to rejection.
- Red tests (Compiler/DialectCapabilitiesTests.cs): `UnionThenIntersect_Oracle_Rejected`, `ExceptThenIntersect_Sqlite_Rejected`, `UnionThenIntersect_PostgreSql_EmitsFlat`, `IntersectThenUnion_Sqlite_Allowed`, `CapabilityTable_IntersectBindsTighter_ExplicitPerDialect`.

### SQLE-04 (MEDIUM, bug) Column aliases dropped
- Verified: `VisitAliasedRelation` NamedTableSource arm (SqlAstBuilder.cs:619) discards `colAliases`; LATERAL branch in `GenerateTableSource` (SqlDialectGeneratorBase.cs:451-457) never prints `ColumnAliases` (the Subquery branch at :410-416 does).
- Fix: (a) builder: `NamedTableSource n when colAliases is not null => throw Unsupported("column aliases on a table reference")`. (b) generator LATERAL branch: emit `(<a>, <b>)` after the alias exactly like the Subquery branch (extract `AppendColumnAliasList(...)` helper used by both).
- Red tests: `NamedTable_WithColumnAliases_Rejected`, `Lateral_ColumnAliases_Emitted` (`AS "l"("a","b")`), `Lateral_PermutedColumnAliases_MaskGuardMatchesEmittedNames` (mask on `status`, `LATERAL (SELECT status, region ...) AS l(region, status)` - guard and emitted SQL agree).
- Breaking: `FROM t AS x(a,b)` now fails instead of silently ignoring the list.

### SQLE-05 (MEDIUM, bug) Missing parentheses for right-nested equal precedence
- Verified: `NeedsParentheses`/`GetPrecedence` (SqlDialectGeneratorBase.Expressions.cs:398-442) parenthesize equal-precedence right children only for `-`, `/`, `%`; SQL Server Concat is `+` (SqlServerDialectGenerator.cs:472-476) but ranked 4. `VisitParenthesizedExpression` (SqlAstBuilder.cs:1479) drops parens.
- Fix (default taken: emitter rule, keep builder unchanged because guards unwrap `ParenthesizedExpression` in many places):
  1. Make `NeedsParentheses` an instance method using `protected virtual int GetPrecedence(BinaryOperator op)`.
  2. Rule: child binary with `childPrec < parentPrec` -> parens; right child with `childPrec == parentPrec` -> parens unless `parentOp == childOp` and op is `And` or `Or`; any comparison child (prec 3) of a comparison parent, left or right -> parens.
  3. `SqlServerDialectGenerator.GetPrecedence`: `Concat => 5` (same as Add), so mixed `+` chains get explicit parens on both sides of non-left-associative positions.
- Red tests (new Compiler/SqleExpressionPrecedenceTests.cs, theory over all dialects): `Multiply_RightModulo_Parenthesized`, `Multiply_RightDivide_Parenthesized`, `Comparison_OfComparison_Parenthesized` (Sqlite `1 = ("region" = ?2)`), `SqlServer_ConcatOfAdd_Parenthesized`, `AndChain_NotParenthesized` (no noise for associative AND/OR), `LeftAssociativeSubtract_Unchanged` (golden regression).
- Breaking: emitted SQL text changes (golden tests updated; extra parens only).

### SQLE-12 (MEDIUM, bug) IS [NOT] TRUE/FALSE rewritten as comparison
- Verified: SqlAstBuilder.cs:909-917 builds `BinaryExpression(left, Equal|NotEqual, TRUE|FALSE)`.
- Fix (default taken: builder expansion, no new node): build `BinaryExpression(CaseExpression(WHEN cond THEN thenVal ELSE elseVal), Equal, Literal 1)` with integer literals:
  `IS TRUE`: WHEN x THEN 1 ELSE 0; `IS NOT TRUE`: WHEN x THEN 0 ELSE 1; `IS FALSE`: WHEN NOT (x) THEN 1 ELSE 0; `IS NOT FALSE`: WHEN NOT (x) THEN 0 ELSE 1. CASE WHEN treats NULL as not matched, giving correct three-valued semantics and valid SQL on all dialects (no boolean literal compared with a predicate).
- Fail-closed: `IsTriviallyTrue` (AstSecurityVisitor.Expressions.cs:123) must return false for this shape (no folding); simplifier must not fold the CASE.
- Red tests (Ast/SqleBuilderCorrectnessTests.cs + Compiler golden): `IsNotTrue_NullRow_Selected_Sqlite` (in-memory SQLite execution: row with NULL predicate is returned), `IsTrue_EmitsCase_SqlServer_ValidShape`, `IsNotFalse_Oracle_NoBooleanLiteral`, `DeleteWhere_IsNotTrue_NotTriviallyTrue`.
- Breaking: emitted SQL shape changes.

### SQLE-08 (MEDIUM, security) Required-filter guard satisfied via alias collision
- Verified: `MatchesColumn` (AstSecurityVisitor.RequiredFilters.cs:239-270) accepts the simple/full table name even when the table has an alias, and the 3-part arm accepts `parts[1]` == simple name; `CollectNamedTableSources` (:74-93) skips Subquery/Lateral sources, so their aliases are invisible; 1-part names always match.
- Fix:
  1. Replace the tuple list with `readonly record struct FromScopeEntry(TableSource Source, string? TableName, string ExposedName, IReadOnlySet<string>? OutputColumns)`; `CollectFromScope` adds named, subquery and lateral sources (exposed name = alias, else simple table name; `OutputColumns` = column aliases or projection names, `null` when unknowable, e.g. `*`).
  2. `MatchesColumn(colRef, target, scope)`: 2-part qualifier matches only `target.ExposedName` (an aliased table is no longer matched by its table name) and only if no other scope entry has the same exposed name (collision -> no match). 3-part matches only an unaliased target by full `schema.table`. 1-part matches only when no other entry may expose the column (named: catalog columns via `_typed`; derived: `OutputColumns` contains it or is `null`).
- Fail-closed: any ambiguity counts as "not constrained" -> `RequiredFilterColumnMissingException`.
- Red tests (tests/TrinoSqlEngine.Tests/Security/SqleRequiredFilterScopeTests.cs): `AliasedTable_DerivedTableNamedLikeTable_Rejected` (finding repro), `AliasedTable_QualifiedByTableName_Rejected`, `AliasedTable_QualifiedByAlias_Accepted`, `UnaliasedTable_QualifiedByName_Accepted`, `Unqualified_WithDerivedExposingColumn_Rejected`, `Unqualified_SingleSource_Accepted`, `ThreePart_AliasedTable_Rejected`; keep `RequiredFilterColumnsParityIntegrationTests` green.
- Breaking: queries that qualified an aliased table by its table name now fail the guard.

## 2. Work packages (order)

| WP | Findings | Files (main) | Size |
|---|---|---|---|
| WP-SQLE-A | SQLE-10, then SQLE-09 | CompiledSqlTemplate.cs, CompiledSql.cs, SqlEmitterContext.cs, new LikePatternEscaper.cs, PolicyExpressionParser.cs, SqlDialectGeneratorBase.Expressions.cs (LIKE branch), DialectCapabilityValidator.cs, CompilerHooks.cs | M (~250 LOC incl. tests) |
| WP-SQLE-B | SQLE-08 | AstSecurityVisitor.RequiredFilters.cs | M (~200) |
| WP-SQLE-C | SQLE-01, -02, -04, -12 | SqlAstBuilder.cs, SqlDialectGeneratorBase.cs (LATERAL) | S-M (~180) |
| WP-SQLE-D | SQLE-05, -03 | SqlDialectGeneratorBase.Expressions.cs (precedence), SqlServerDialectGenerator.cs, DialectCapabilities*.cs, DialectCapabilityValidator.cs | M (~220) |

Order: A (both HIGH-security) -> C (HIGH bug SQLE-01) -> B -> D. A and B can run as parallel lanes (disjoint files). C must precede D (both touch SqlDialectGeneratorBase*.cs; SQLE-12 CASE shape is covered by D's precedence goldens). A's SQL Server concat test is finalised after D (or D's precedence change is pulled forward into A if lanes are merged).
After C and D: bump the compiler version string used in the template cache key so templates emitted by the old generator are not reused.

## 3. Dependencies on other areas
- Template cache / governance (`GovernedSqlCompiler`): only via the `SqlSlot`/`BoundParameter` field (WP-A); no key change except the version bump.
- Data API planners (`GovernedTableReadPlanner*RequiredFilter*` tests in Autheris.Tests.Unit) consume the required-filter guard; must stay green after WP-B.
- No dependency on non-SQLE findings in security.txt.

## 4. Security conditions for Phase 3
1. Every policy value that reaches a LIKE pattern is escaped on cache miss and hit; proven by a hit test with a different value and by the `CacheRebindLikeEscape` mutant.
2. Policy LIKE pattern shapes outside `literal | param | ||` are rejected at policy parse time, never emitted.
3. SQLite (case-insensitive LIKE) rejects any policy LIKE whose pattern contains a policy parameter.
4. Required-filter guard: ambiguity (alias collision, unknown derived output, aliased table referenced by name) is "not satisfied".
5. Builder never maps an unknown operator/construct to a different semantics: unknown unary operator and CORRESPONDING throw `AstBuildException`.
6. Set operations with right-nested operands are rejected unless the dialect explicitly declares `IntersectBindsTighter`.
7. Emitter output for right-nested equal-precedence and comparison-of-comparison is parenthesized on all dialects (theory test over `TargetSqlDialect`).
8. `IS [NOT] TRUE|FALSE` expansion is never treated as trivially true by DML guards.
