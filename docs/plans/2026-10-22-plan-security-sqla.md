# Plan: Security Findings Area SQLA (HIGH/MEDIUM)

- Source: `docs/plans/security.txt`, findings `SQLA-01, -02, -06, -07, -09, -10, -11, -12, -14, -16`.
- Out of scope: LOW findings SQLA-03/04/05/08/13/15/17/18/19/20. SQLA-20 (the ungoverned co-located `Compile` overload) is partly covered by WP-SQLA-4 because SQLA-14 depends on it.
- Branch: `fix/security-review-open-findings`. Breaking changes are allowed because nothing runs in production yet.
- Owner rule: every open decision takes the recommended option, recorded below as "Decision".

## Verification summary (against the current branch)

| ID | Sev | Status | Verified location |
|---|---|---|---|
| SQLA-01 | MED | Confirmed | `GovernedTreeQueryService.cs:837-855`: unchecked `parentMultiplier * Math.Max(1, node.Limit)`; `TreeSqlCompiler.MaxLimit = 10_001` |
| SQLA-02 | MED | Confirmed | `TreeSqlCompiler.cs:546-575`: `TreeFilterOperator.IsNull` counts regardless of value; `Eq` counts even with a null value |
| SQLA-06 | HIGH | Confirmed | `TreeSqlCompiler.cs:642-646` splices `Decision.CombinedRowFilterSql`; `PolicySubqueryTenantRewriter` (internal, `src/TrinoSqlEngine/Ast/Security/`) is only used by `AstSecurityVisitor.Tables/TypedDml` |
| SQLA-07 | MED | Confirmed | `CrossSourceQueryRouter.cs:~90` `EnsureAllowed(domain)`, falls back to "default" with no SourceName check; `FederatedStagingService.cs:90-92` checks `Identifier.Domain`, but reads `Table.SourceName` at `:434-446` |
| SQLA-09 | MED | Confirmed | `Federation/Pushdown/PushedClauseBuilder.cs:104-114` `AllocateParameter(..., null)`; `CrossSourcePlanner.cs:434-463` (`else if` branch) |
| SQLA-10 | MED | Confirmed | `PushabilityEvaluator.cs`, Unary `Not` delegates to the operand; range operators are not collation-checked; `PushabilityMatrix.IsNegativeFilterSafeForDialect` |
| SQLA-11 | MED | Confirmed | `DynamicKeySetPlanner.cs:57-157`: candidate joins ignore `ExtractedConjunct.Origin` |
| SQLA-12 | MED | Confirmed | `Federation/Optimization/PushedClauseBuilder.cs:333-334` unconditional `return true`; also matches `target.Schema` as a qualifier (`:327`) |
| SQLA-14 | MED | Confirmed | `CrossSourcePlanner.cs:303` passes no user/tenant; `SubtreeCompiler.cs:223` `tenantId?.Value ?? "default"` |
| SQLA-16 | MED | Confirmed | `SubtreeGraphPartitioner.cs:262-276` pushes every cluster-local WHERE conjunct; `IsSupportedSubtreeJoin` accepts Left/Right/Full outer joins |

## Findings and fixes

### SQLA-06 (HIGH): tree row policy loses the tenant wrapper on policy subqueries
1. **Fix.** Add `internal static bool RowPolicyRequiresEnginePipeline(TableAccessDecision d)` in `TreeSqlCompiler`. It returns true when `d.RowPolicy is PolicyPredicateRowPolicy p` and `p.Predicate.Expression` contains an `ExistsExpression`, an `InSubqueryExpression` or a scalar subquery (`TrinoSqlEngine.Ast.Nodes`; detect them with a small `SqlAstVisitor` collector, `PolicyPredicateSubqueryDetector`, placed in `Autheris.Application/Sql/Policy/`). It also returns true when `RowPolicy` is untyped and `CombinedRowFilterSql` is non-empty, because subqueries cannot be proven absent from untyped text. `BuildInner` throws `GatewaySecurityException("TREE_ROW_POLICY_UNSUPPORTED")` before it emits any SQL when this returns true.
2. **Decision (recommended option).** Fail-closed denial now. Routing tree nodes through `PolicySubqueryTenantRewriter` and `SecurityCoverageVerifier` would mean exposing internal engine types. That is tracked as a follow-up in the master plan backlog and is not part of this plan.
3. **Fail-closed.** A detector exception or an unknown `IRowPolicy` type is treated as "requires engine", so the request is denied. Only typed predicates without subqueries keep using the spliced text.
4. **Red tests** (`tests/Autheris.Tests.Unit/GraphQL/TreeSqlCompilerTests.cs`): `Tree_RowPolicyWithInSubquery_IsDenied`, `Tree_RowPolicyWithExists_IsDenied`, `Tree_UntypedRowFilterText_IsDenied`, `Tree_SimpleTypedRowPolicy_StillCompiles`, and `TreeVsCompilePolicyParityTests.SubqueryPolicy_TreeDeniesWhereWebSqlWraps`.
5. **Breaking.** GraphQL relation queries on tables with correlated consent or key-source virtual filters now fail until the follow-up lands. **Dependencies:** none.

### SQLA-01: tree row budget overflow
1. **Fix.** Replace `EstimateRows` with `static long EstimateRows(TreeQueryNode node, long parentMultiplier, bool isList, long cap)`. It uses saturating arithmetic (`Math.BigMul`, or a `checked` multiply in a try block that returns `long.MaxValue`) and stops recursing as soon as the running total exceeds `cap` (`maxBudget`). `ValidateTreeBudget` compares the saturated total.
2. **Fail-closed.** Overflow saturates to `long.MaxValue`, which is always over budget. A non-positive `Limit` is treated as `MaxLimit`, not 1.
3. **Red tests** (`GovernedTreeQueryServiceTests`): `AggregateRowBudget_FourNestedMaxLimits_Rejected` (root 1000 with 4 levels of 10001), `EstimateRows_Overflow_SaturatesToMax`, and `AggregateRowBudget_WithinBudget_Passes`.
4. **Breaking:** none.

### SQLA-02: `isNull:false` satisfies a required filter column
1. **Fix.** In `TreeSqlCompiler.IsColumnConstrained`, `IsNull` counts only when `comp.Value is true`. `Eq`, `Contains`, `StartsWith` and `EndsWith` count only with a non-null value (and a non-empty string for the three string operators). `In` counts only when the list is non-empty and contains at least one non-null item. The rest stays aligned with `AstSecurityVisitor.RequiredFilters.IsColumnConstrained`.
2. **Fail-closed.** Any value shape the check does not recognise means "not constrained".
3. **Red tests** (`TreeSqlCompilerRequiredFilterTests`): `GraphQLTree_IsNullFalse_DoesNotSatisfyRequiredFilter`, `GraphQLTree_IsNullTrue_Satisfies`, `GraphQLTree_EqNull_DoesNotSatisfy`, `GraphQLTree_InOnlyNulls_DoesNotSatisfy`.
4. **Breaking:** clients that relied on `isNull:false` are now rejected.

### SQLA-07: allowlist checked on the domain, but the read uses `Table.SourceName`
1. **Fix.** Add `public static string EffectiveDataSource(WebSqlOptions o, TableMetadata m)` to `DataSourceAllowlistGuard`. It returns `m.Table.SourceName ?? m.Identifier.Domain`, mapped through `DataSourceMappings`, and it is the single source of truth, reused at `FederatedStagingService.cs:434-446`.
   - `CrossSourceQueryRouter`, after the metadata lookup: call `EnsureAllowed(_webSqlOptions, tenantId, EffectiveDataSource(...))`. Apply the rewriter's "default"-fallback rule (`GovernedSqlRewriter.cs:438-448`): a fallback is allowed only when `SourceName` is non-empty and equals the requested domain, mapped. Otherwise throw `WebSqlTableDeniedException`.
   - `FederatedStagingService`: replace the domain check with a check on the effective source, keeping the domain check as well, so both must pass.
2. **Fail-closed.** An empty effective source is denied, and both checks must pass.
3. **Red tests** (`tests/Autheris.Tests.Unit/Security/DataSourceAllowlistSrH06Tests.cs`): `Router_DefaultFallbackToUnlistedSourceName_Denied`, `Router_SourceNameNotInTenantAllowlist_Denied`, `Staging_SourceNameNotAllowed_DeniedBeforeConnectionOpen`, `Router_MappedSourceAllowed_Passes`.
4. **Breaking:** legacy `default.*` entries that point at another source are now rejected. **Dependencies:** none.

### SQLA-09: pushed client parameters are bound as NULL
1. **Fix.** In `Federation/Pushdown/PushedClauseBuilder.Build`, add the parameter `IReadOnlyDictionary<string, object?>? clientParameters`. For `ParameterReference` and `NamedBindMarkerExpression`, resolve the value by name (with the same name normalisation as `FederatedParameterContract`). If the name is missing, throw `PushdownRejectedException`; the planner catches it and drops that conjunct from the pushdown, so DuckDB evaluates it after staging.
   - `CrossSourcePlanner.cs:458` passes `parameters`.
   - `PushabilityEvaluator` rejects positional `?` markers that cannot be resolved by name.
2. **Decision.** Resolve and bind (recommended), rather than rejecting all parameterised conjuncts.
3. **Fail-closed.** An unresolvable parameter means the conjunct is not pushed, so the result is correct and never silently empty. NULL is never bound.
4. **Red tests** (`tests/Autheris.Tests.Unit/Federation/WebSqlFilterPushdownPlannerTests.cs`, `BoundParametersFederationSecurityTests`): `Pushdown_EqParameter_BindsClientValue`, `Pushdown_InParameters_BindsValues`, `Pushdown_BetweenParameters_BindsValues`, `Pushdown_UnknownParameter_NotPushed`.
5. **Breaking:** none.

### SQLA-10: NOT(...) and string range comparisons bypass the collation guard
1. **Fix** (`PushabilityEvaluator.ValidateExpression`).
   - `UnaryOperator.Not`: evaluate the operand in a negated context (a `bool negated` flag threaded through recursion). In that context, every string comparison, `IN`, `LIKE` and `BETWEEN` on SQL Server must pass `PushabilityMatrix.IsNegativeFilterSafeForDialect`; otherwise the result is `Rejected(CollationRisk)`.
   - `<`, `<=`, `>` and `>=` (and `BETWEEN`, outside a negation) on a string column under SQL Server require the `exact_compare` hint. Add `PushabilityMatrix.IsRangeFilterSafeForDialect(dialect, column)`, which delegates to the same logic.
2. **Fail-closed.** If the column cannot be resolved (`column == null`), the conjunct is rejected.
3. **Red tests** (`WebSqlFilterPushdownPlannerTests`): `Pushdown_NotEquals_SqlServerCiString_Rejected`, `Pushdown_NotIn_Wrapped_SqlServer_Rejected`, `Pushdown_StringLessThan_SqlServer_Rejected`, `Pushdown_StringRange_ExactCompare_Pushed`, `Pushdown_NumericRange_SqlServer_Pushed`.
4. **Breaking:** less pushdown, which is a performance cost only.

### SQLA-11: key-set pushdown drives the preserved side of an outer join
1. **Fix** (`DynamicKeySetPlanner.Plan`).
   - Skip equality conjuncts whose `Origin` is `LeftOuterJoinOn` or `RightOuterJoinOn`.
   - Compute the preserved set from the join tree with a new shared helper, `OuterJoinSides.Compute(QuerySpecification) -> (IReadOnlySet<TableSource> preserved, IReadOnlySet<TableSource> nullSupplying)`, in `Federation/Conjuncts/`, extracted from `SqlConjunctSplitter`.
   - A table that is preserved by any outer join, or on either side of a full join, is never chosen as the driven table. If the only legal direction contradicts the heuristic, use the legal direction or skip the plan.
2. **Fail-closed.** If the sides are ambiguous or cannot be computed, no key-set plan is made.
3. **Red tests** (`WebSqlDynamicKeySetPlannerTests`): `KeySet_LeftJoinPreservedSide_NeverDriven`, `KeySet_OuterJoinOnConjunct_NotCandidate`, `KeySet_FullOuterJoin_NoPlan`, `KeySet_InnerJoin_Unchanged`.
4. **Breaking:** none.

### SQLA-12: parameter pushdown attributes another table's conjunct
1. **Fix** (`Optimization/PushedClauseBuilder.BelongsToTable`).
   - Return false when the qualifier does not match `Alias`, `TableName` or `FullName`. Remove the `Schema` match too, since schema alone is not a table identity.
   - Unqualified columns are accepted only when no other table in the statement has that column. Pass `IReadOnlyList<ResolvedSourceTable> allTables` into `BuildPushdown`/`ExtractCandidates`.
   - `ExtractCandidates` consumes `SqlConjunctSplitter.Split` and only uses `Where` conjuncts whose `AllowedTableSources` (when set) contain the target.
2. **Fail-closed.** If the qualifier is ambiguous or unknown, nothing is pushed.
3. **Red tests** (`WebSqlFilterPushdownPlannerTests`): `ParamPushdown_OtherTableQualifier_NotPushed`, `ParamPushdown_UnqualifiedAmbiguousColumn_NotPushed`, `ParamPushdown_SchemaQualifier_NotPushed`, `ParamPushdown_NullSupplyingSide_NotPushed`.
4. **Breaking:** none.

### SQLA-14: subtree tenant predicate compiled for "default"
1. **Fix.**
   - `SubtreeCompiler.Compile(SourceSubtree, decisions, options, ISqlEngine, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)`: `user` and `tenantId` become required and non-nullable. Remove the `"default"` fallback and throw `ArgumentException` on an empty tenant.
   - `CrossSourcePlanner.Plan`: collapse the three overloads into a single overload that takes `ClaimsPrincipal user, TenantId tenantId`.
   - `FederatedDuckDbExecutionService` (`:240` and the execute path) passes them.
   - Delete the ungoverned co-located `Compile(string coLocationGroup, ...)` overload, and delete `CoLocationVerificationService`/`ICoLocationVerificationService` if they are still unreferenced (this partly covers SQLA-20).
2. **Fail-closed.** A missing tenant is a compile-time error or an `ArgumentException`. The second governed pass (`SqlTableReadExecutor`) stays.
3. **Red tests** (`WebSqlSameSourceSubtreeTests`): `Subtree_TenantPredicate_BindsCallerTenant`, `Subtree_EmptyTenant_Throws`, `Subtree_NonDefaultTenant_ReturnsRows` (planner-level, checking the bound parameter value). Add an architecture test, `CoLocatedCompileOverload_DoesNotExist`.
4. **Breaking:** public signature changes; update the test call sites.

### SQLA-16: subtree pushes WHERE into the null-supplying side
1. **Fix** (`SubtreeGraphPartitioner.CreateSubtree`). Use `OuterJoinSides.Compute` (shared with SQLA-11). If any cluster member is null-supplying for an outer join with a table outside the cluster, push no WHERE conjuncts into that subtree.
2. **Decision.** Push no WHERE conjuncts in that case, which is the recommended and simplest option. Null-rejection analysis is not done.
3. **Fail-closed.** If the sides are unknown, nothing is pushed.
4. **Red tests** (`WebSqlSameSourceSubtreeTests`): `Subtree_RightJoinNullSupplyingCluster_WhereNotPushed`, `Subtree_LeftJoinPreservedCluster_WherePushed`, `Subtree_FullJoin_WhereNotPushed`, `Subtree_InnerOnly_Unchanged`.
5. **Breaking:** none.

## Work packages (in order)

| WP | Findings | Scope | Size |
|---|---|---|---|
| WP-SQLA-1 | 06, 01, 02 | GraphQL tree path (`TreeSqlCompiler`, `GovernedTreeQueryService`, new detector) | M (~250 LOC incl. tests) |
| WP-SQLA-2 | 07 | Allowlist on the effective source (router, staging, guard) | S (~150) |
| WP-SQLA-3 | 09, 10, 12 | Predicate pushdown correctness (both `PushedClauseBuilder`s, evaluator, matrix) | M (~350) |
| WP-SQLA-4 | 11, 16, 14 | Join-side helper `OuterJoinSides`, key-set planner, partitioner, subtree tenant and signature cleanup | M-L (~450) |

WP-1 and WP-2 are independent and can run in parallel lanes. WP-3 and WP-4 both touch `CrossSourcePlanner.cs`, so run them sequentially (WP-3 first). In WP-4, extract `OuterJoinSides` first.

**Cross-area dependencies.** The SQLA-07 guard reuse is shared with any other area that touches `DataSourceAllowlistGuard` (SR-H-06), so rebase onto the latest version. No other dependencies are known.

## Security conditions for Phase 3
1. No path in the GraphQL tree path emits a row policy containing a subquery without the tenant wrapper. Denial is the default for untyped or unknown policies (SQLA-06).
2. Budget arithmetic cannot wrap: there is a test with the maximum depth and maximum limits (SQLA-01).
3. Required-filter semantics in the tree path are at least as strict as in the engine: a parity test covers `IsNull(false)`, `Eq(null)` and `In([])` (SQLA-02).
4. Every connection that is opened has its effective source name, after mapping, allowlist-checked before the open (SQLA-07). An empty source name is denied.
5. Pushdown never binds a value the client did not supply. Every pushed clause is a superset of the rows DuckDB keeps: negation and range operators are covered under SQL Server CI collations (SQLA-09/10).
6. Neither key-set nor WHERE pushdown restricts the preserved side of an outer join, and a WHERE conjunct is never pushed into a null-supplying cluster (SQLA-11/12/16).
7. Subtree compilation requires the caller's tenant (no `"default"` fallback), and the ungoverned co-located overload is removed (SQLA-14).
