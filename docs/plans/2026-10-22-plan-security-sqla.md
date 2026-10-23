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

#### SQLA-06 follow-up (addendum 2026-10-23)

Owner decision 2026-10-23: build it now, keep the function, keep it fail-closed. This replaces item 2 for typed predicates with subqueries. Untyped text stays denied.

1. **Approach: reuse the WebSQL pipeline, no new engine API.** For each tree table whose decision has `RowPolicyRequiresEnginePipeline == true` and `RowPolicy is PolicyPredicateRowPolicy`, the tree gets a *secured source*. That is the SQL text the governed compiler emits for `SELECT <all catalog columns> FROM "<schema>"."<table>"`. It is built through `GovernancePolicyFactory.CreateAssemblyAsync` followed by `FastSqlEngine.Compile` (`CompileRequestFactory.Create`, `enforcedMaxRows: 0`). The engine then applies everything WebSQL applies: the `SecureTyped` wrapper (`... FROM t AS autheris_target WHERE tenant AND policy`), `PolicySubqueryTenantRewriter` (a tenant wrapper on every subquery table, nested subqueries rejected, uncataloged tables rejected), `SecurityCoverageVerifier`, the capability validator and the emitted-text invariant checker. The factory's `FindUnusableDependentsAsync` (inactive tables, `RequireTenantColumn`) and its catalog give the dependents the same admission as WebSQL. Dependents get only the tenant predicate, as in WebSQL (no consent recursion, SEC-ADG-11). Rejected alternative: a new public engine facade that copies `SecureTyped`. It would be a second implementation of the wrapper that could drift.
2. **New type** `Autheris.Application/Sql/Tree/TreeSecuredSourceBuilder.cs` (internal sealed): `Task<TreeSecuredSource> BuildAsync(ResolvedTableAccess access, string dataSourceName, DatabaseDialect dialect, CancellationToken ct)`. The input is `GovernedPolicyInput(source, tenant, {identity: decision}, TablesWithoutRls: {}, Correlated) { Metadata = {identity: metadata}, KeySetStatement = null }`. Default taken: no key-set stage on the tree (AC-8, byte-identical EXISTS path). The policy is passed `with { Masks = TreeNoMasks.Instance, TablesWithMaskedColumns = {} }`, because the tree keeps masking in its outer projection (no double HMAC, Issue143 parity). `record TreeSecuredSource(string Sql, ImmutableArray<BoundParameter> Parameters, TargetSqlDialect Dialect)`. The dialect map is `Sqlite`/`SqlServer`/`PostgreSql`. Any other dialect is denied.
3. **Wiring.** `TreeTableAccess` gets an optional `TreeSecuredSource? SecuredSource`. `GovernedTreeQueryService` (step 3, the `treeAccess` loop, ~l.444) calls the builder for each table that qualifies. Its constructor takes `GovernancePolicyFactory?` and `FastSqlEngine?` (both already in DI). When either is missing and a table needs them, the request is denied.
4. **Splice in `TreeSqlCompiler.BuildInner`.** With `SecuredSource` set, `FROM <table> AS autheris_target` becomes `FROM (<remapped secured SQL>) AS autheris_target`. The `CombinedRowFilterSql` splice is skipped, because the policy is inside the source. The outer tenant conjunct stays (defence in depth). Columns, the relation join, `node.Where` and the order keep referencing `autheris_target.<col>`. This works because the secured source projects every catalog column under its own name. Without `SecuredSource`, the existing `TREE_ROW_POLICY_UNSUPPORTED` throw is unchanged. The test-facing `TreeSqlCompiler.Compile` stays static and synchronous.
5. **Parameter binding.** New `CompileContext.BindSecuredSource(TreeSecuredSource s)`. For each `BoundParameter`, it allocates a fresh `@gqN` (shared counter and `MaxParameters` budget) and replaces the exact `Marker` token outside literals and quoted identifiers. It generalises `RenameParameters` to the `@pN` and `$N` marker styles, longest marker first, and matches the whole token (`@p1` is not a prefix of `@p10`). Engine names (`@p0`, `$1`) therefore never reach the tree command. Two secured tables in one tree never collide. The tree's `@gq`/`@rf` names stay unique. Sqlite and SqlServer use `@pN` and PostgreSQL uses `$N`. On a GraphQL SQLite source, the tree command is bound by name exactly as today (`ToProviderValue` applied).

**Fail-closed rules (each one throws `GatewaySecurityException("TREE_ROW_POLICY_UNSUPPORTED")` with a fixed message; no inner message is copied):**
- F1: an untyped or unknown `IRowPolicy` with text is denied (unchanged, `SQLA_06_TreeSqlCompiler_DeniesATextOnlyRowFilter` stays green).
- F2: any `SecurityException` (which covers `SecurityCoverageException`), `SqlLimitExceededException` or `SqlCompile*Exception` from the factory or the compile is denied.
- F3: the compile result is checked. It must have `StatementClass == Select` and `Dialect ==` the mapped dialect. `AppliedPredicates` must contain the policy id `(identity,1)`, and the tenant id `(identity,0)` when the table has a tenant column. Every parameter's `Origin` must be Tenant or Policy (or Literal), never a client origin. The SQL must contain no `LIMIT`/`TOP`/`FETCH` at the root (`enforcedMaxRows: 0`).
- F4: every `Marker` occurs at least once as a whole token, and no engine marker remains after the remap. Otherwise the request is denied.
- F5: a dependent that is not admitted becomes deny-all through the factory (`PolicyPredicate.DenyAll`), so the tree returns zero rows, exactly as WebSQL. Default taken: parity with WebSQL rather than an error.
- F6: the factory tenant is the tenant of the tree's root access. A mismatch is a defect and is denied.

**WP-SQLA-1b** (one WP, ~350 LOC incl. tests; rebase on WP-SQLA-1; coordinate with the lane editing the SqlCompilerGate accepted-differences file). Red tests:
- Unit, `tests/Autheris.Tests.Unit/GraphQL/TreeSecuredSourceTests.cs`: `SecuredSource_ExistsPolicy_SplicesDerivedTableAndNoRowFilterText`, `SecuredSource_RemapsEngineMarkersToGqNames`, `SecuredSource_TwoTablesSamePolicyParameter_NoCollision`, `SecuredSource_MarkerP1NotPrefixOfP10`, `SecuredSource_ClientOriginParameter_Denied`, `SecuredSource_MissingPolicyPredicateId_Denied`, `SecuredSource_RootLimitPresent_Denied`, `Builder_NestedPolicySubquery_Denied`, `Builder_UncatalogedSubqueryTable_Denied`, `Builder_FactoryMissing_Denied`, `Builder_UnsupportedDialect_Denied`, `Builder_MasksNotAppliedInSource`.
- Sqlite parity, `TreeVsCompilePolicyParityTests` (add a `consents` table, and tenant-B consent rows that point at tenant-A orders): `SubqueryPolicy_Exists_TreeAndCompileExposeTheSameRows(tenant)`, `SubqueryPolicy_InSubquery_TreeAndCompileExposeTheSameRows(tenant)`, `SubqueryPolicy_ForeignTenantConsent_NeverAdmitsARow` (proves the tenant wrapper inside the subquery), `SubqueryPolicy_OnChildRelation_TreeAndCompileExposeTheSameRows`, `SubqueryPolicy_WithMask_TreeAndCompileExposeTheSameRows`, `SubqueryPolicy_InactiveDependent_YieldsNoRowsOnBothPaths`.
- SqlCompilerGate: `TreeSqlCompilerConsumer` builds a `SecuredSource` when `filter.Typed` has a subquery. New `Parity/TreeTypedSubqueryParityTests.SQLA_06_TreeSqlCompiler_TypedSubqueryFilter_MatchesWebSqlRows` and `SQLA_06_TreeSqlCompiler_TypedSubqueryFilter_NeverReturnsForeignTenantRows`.

**Existing tests.** `Tree_RowPolicyWithInSubquery_IsDenied`, `Tree_RowPolicyWithExists_IsDenied`, `Tree_UntypedRowFilterText_IsDenied` and `SQLA_06_TreeSqlCompiler_DeniesATextOnlyRowFilter` are kept unchanged. They now pin the case of a subquery or text with no secured source, which is still denied. `Tree_SimpleTypedRowPolicy_StillCompiles` is unchanged: typed predicates without a subquery keep the splice (default taken: only subquery predicates are routed). `TreeVsCompilePolicyParityTests.SubqueryPolicy_TreeDeniesWhereWebSqlWraps` (item 4) was never added and is superseded by the wrap tests above. Integration targets (graphql and graphql-tree channels, SQLite, run on CI): `SqlVirtualFilterChannelParityTests`, `VirtualFilterChannelParityTests`, `RowFilterChannelParityTests` (incl. `GraphQl_E2E_SQLite_Air1_...`), `RowFilterKeySetChannelParityTests.KeySetCache_Channel_...` (the tree path stays on EXISTS, and the rows are equal by design) and `Issue143RestrictedScopeParityTests`.

**Open questions (default taken).** Key-set substitution on the tree is out of scope. Cross-source tree children keep their current path. Per-request compile cost is covered by the engine template cache.

**Phase 3 review (security expert, 2026-10-23): APPROVED WITH CONDITIONS.**
Bar decision: WebSQL parity is the correct and sufficient bar. `PolicySubqueryTenantRewriter` (SEC-ADG-11) documents that policy-subquery tables get the tenant predicate only, never consent/row filters, because recursion is unsound and the subquery result only decides visibility of the root row; it is never projected. The owner phrase "full policy applied to every table inside the subqueries" is read as "the full engine policy pipeline (tenant wrapper, catalog/active/RequireTenantColumn admission, coverage verifier, capability and invariant checks) runs over every subquery table". Per-table consent recursion is not required and is not allowed. Accept commit 89c1737c: the 3 entries are `ast-stricter`, reason `SqlCompileNotSupportedException`, `approved`, issue `#277`, reviewer `@themulle` (21 of 23 existing entries use it; matches `ReviewerPattern`, `IssuePattern`); the gate stays fail-closed (gate compiles with `AllowRecursiveCte` off; legacy returned anchor rows only or a silent recursion). SQL Server has no RECURSIVE keyword, so the implicit self-reference entry is consistent.

Binding conditions:
- SQLA06F-C1: every subquery table must be proven covered, or the whole request is denied (not deny-all rows). Uncataloged, nested, RequireTenantColumn-without-tenant-column and cross-source subquery tables throw `TREE_ROW_POLICY_UNSUPPORTED`. Only an inactive/unusable dependent may degrade to `DenyAll` (F5, zero rows), and a test must prove it never yields more rows than WebSQL.
- SQLA06F-C2: the secured source must be built per request from the request's tenant and principal; no cache of `TreeSecuredSource` text, parameters or compile results keyed by anything but (tenant, principal-scope hash, table, policy version). Preferably no cache beyond the engine template cache. Bound policy/tenant parameter values are never taken from a shared instance.
- SQLA06F-C3: F6 is strict: every `BoundParameter` tenant value equals the root access tenant, and the outer tree tenant conjunct stays. A mismatch denies.
- SQLA06F-C4: marker remap must tokenise, not text-replace. Skip string literals, quoted identifiers and comments; longest-first whole-token match; after remap no `@pN`/`$N` engine token may remain (F4) and every client value stays a bound `@gqN`. A client string containing `@p0` or `$1` must be inert (values are parameters, never inlined). The remap must be done on the post-compile text only, never on user input. PostgreSQL `$N` inside dollar-quoted strings must not be touched or the statement is denied.
- SQLA06F-C5: masks-off is allowed only inside the derived table. The tree outer projection must apply the mask/hash/deny to every column of every node that has masks, including restricted-scope (Issue143) columns, and must never `SELECT autheris_target.*`. A test must fail if any masked column leaves the inner source unmasked into the client result, `node.Where`, order or relation join (a masked column may not be filtered or ordered on in clear unless already allowed today). The inner projection stays confined to the derived table.
- SQLA06F-C6: the secured source is built with `KeySetStatement = null`, no HMAC/mask rewriting, and `enforcedMaxRows: 0` (F3 root-limit check), while the tree keeps its own row budget (SQLA-01) and parameter budget. The tree outer query adds the tree limits itself.
- SQLA06F-C7: error mapping: every exception from the builder (including `OperationCanceledException` that is not the caller's token, `InvalidOperationException`, parse errors) maps to the fixed `TREE_ROW_POLICY_UNSUPPORTED` message. No inner message, table name, predicate text or parameter value is logged or returned; log only the error code and table identity id at a debug level. Caller cancellation propagates unchanged.
- SQLA06F-C8: a missing `GovernancePolicyFactory`/`FastSqlEngine` or an unsupported dialect denies; there is no fallback to the old splice for a subquery predicate. Untyped text stays denied (F1). The four SQLA_06 deny tests and `Tree_SimpleTypedRowPolicy_StillCompiles` stay unchanged and green; adding any `[Fact(Skip)]` or loosened assertion is a defect.
- SQLA06F-C9: the secured source for a child relation node must be scoped to that node's own table decision and tenant; the join key and the child's tenant conjunct stay in the outer query.

Mandatory security tests (`[Trait("Category","Security")]`):
- `SQLA06F_ForeignTenantConsentRow_NeverAdmitsRow` (also on a child relation).
- `SQLA06F_UncatalogedSubqueryTable_DeniesRequest`, `SQLA06F_NestedSubquery_DeniesRequest`, `SQLA06F_DependentWithoutTenantColumn_DeniesRequest`.
- `SQLA06F_InactiveDependent_ZeroRows_NotMoreThanWebSql`.
- `SQLA06F_MarkerLookalikeInClientValue_StaysBoundAndInert` (`@p0`, `$1`, `@gq1`, `@p10` as filter values) and `SQLA06F_MarkerRemap_DoesNotTouchLiteralsOrQuotedIdentifiers`.
- `SQLA06F_MaskedColumn_NotLeakedViaSubquerySource` (hash and mask on every masked column, plus the Issue143 restricted scope variant `Issue143RestrictedScope_WithSubqueryPolicy_MatchesWebSql`).
- `SQLA06F_TwoTenants_SameTable_NoSharedSecuredSource` (parallel requests, distinct tenant values).
- `SQLA06F_BuilderException_MapsToFixedCode_NoInnerMessage` and `SQLA06F_BuilderCancellation_PropagatesCallerToken`.
- `SQLA06F_RootLimitInSource_Denied`, `SQLA06F_KeySetStageOff_TreeRowsEqualExistsPath`.
- Gate: `SQLA_06_TreeSqlCompiler_TypedSubqueryFilter_NeverReturnsForeignTenantRows` (as planned).

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
