# Phase 4 Hydrogen Task Briefs — Wave 2 (2026-11-14, Revised)

**Status:** Phase 3 APPROVED WITH CONDITIONS — Ready for Phase 4 Execution (`csharp-tdd-developer` on Gemini 4 Hydrogen)
**Baseline Verified:** `origin/main` @ `75bcf65a` (2026-11-14; `#667` `WP-OI-CAT-2` merged at `1e926a8e`, `#672` PR 1+2 merged at `968c670e`, `#674` merged at `9d07967f`, `WP-SIMPL-46 PR 6a` `#733` merged at `75bcf65a`)
**Deduplication Check:** Verified against `gh pr list --state open` (`#748`/`#666`, `#747`/`#668`, `#744`/`#669`, `#734`/`#624`) and merged `main` commits (`ddb53afb` PR 1 `#578`, `554ace53` PR 2 `#579`, `9b3e8852` PR 3 `#572`, `ba5dd11d` PR 5 `#573` WebSQL, `1e926a8e` `#667`). Zero items in this wave duplicate open or merged PRs.

---

## Global Execution Rules for All Wave-2 Briefs (Mandatory)

1. **Dedicated Git Worktree & Branch:**
   - Never work in `/root/autheris` directly. Create the dedicated worktree under `/work/wt/<worktree-name>` branching from `origin/main` (`75bcf65a` or later).
2. **Strict Local Resource Guardrails (`csharp-tdd-developer`):**
   - Always prefix `dotnet` commands with `MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget`.
   - Always pass `-c Release -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false` on build/test commands, and `--blame-hang --blame-hang-timeout 120s` on `dotnet test`.
   - **NEVER** run the full unfiltered unit test suite or any integration test locally. Run **only** the targeted `--filter` unit and architecture tests specified in your brief.
   - Compile affected integration test projects locally with `dotnet build ... --no-dependencies` only.
3. **PR Size & Hygiene:**
   - Each PR must stay `<= 400` net added lines in `src/`.
   - Never write plan-ID tokens (`WP-`, `SEC-`, `OD-`) in code comments under `src/` (`PlanIdCommentRatchetTests`).
   - Never write absolute URI links (the literal scheme `file` followed by colon-slash-slash) inside any `.md` file (`DocsIntegrityTests.No_absolute_file_links`).
   - Do not delete your worktree until the PR is merged or `@coordinator` instructs you.

---

## Brief 1: `WP-SIMPL-53 PR 4` — GraphQL Aggregation Index Pricing & `extensions.autheris.indexHints` (`#573`)

- **Worktree:** `/work/wt/simpl-53-pr4-gql-index-hints`
- **Branch:** `feat/simpl-53-pr4-gql-index-hints`
- **Issue:** `#573`
- **Plan & Phase 3 References:**
  - `docs/plans/simplification/WP-SIMPL-53-mcp-catalog-client-hints.md` (§4 step 4, §5, and Phase 3 binding conditions `SEC-53-17` .. `SEC-53-22`)

### 1.1 Context & Current State on `main` (`75bcf65a`)
- `src/Autheris.GraphQL/Interceptors/QueryCostAnalyzerRule.cs:625-649` already evaluates `IndexUsabilityEvaluator.HasUsableIndex(tableMeta, visibleColumns, orderBy, filterColumns)` for paged root list fields (`!isAggregateField && !isSingleItemLookup`) when `_costOptions?.UnindexedRootListSurchargeMultiplier > 1.0`, multiplying the field cost by `(int)Math.Min(int.MaxValue, Math.Ceiling(cost * _costOptions.UnindexedRootListSurchargeMultiplier))`.
- However:
  1. `QueryCostAnalyzerRule.cs:418-422` (`isAggregateField` branch) delegates to `AggregateFieldCost(field, variableDefinitions, variableValues)` (`src/Autheris.GraphQL/Interceptors/QueryCostAnalyzerRule.Aggregation.cs:96-131`), which prices `T_aggregate` by shape only and never checks `IndexUsabilityEvaluator.HasUsableIndex` or applies `UnindexedRootListSurchargeMultiplier`.
  2. `src/Autheris.GraphQL/Interceptors/CostAndQuotaMiddleware.cs:106-114` and `:227-235` call `QueryCostAnalyzerRule.CalculateCost(...)` WITHOUT passing `tableMetadataLookup: CatalogGraphQlTypeModule.CurrentLookup`, whereas `SlowListsMiddleware.cs:54` passes `CatalogGraphQlTypeModule.CurrentLookup`.
  3. Neither root list fields nor root `T_aggregate` fields emit `extensions.autheris.indexHints: [{root, reason: "NO_USABLE_INDEX", helpfulIndexColumns: [...]}]` when surcharged.

### 1.2 Files to Create / Modify
- **Create:**
  - `src/Autheris.Domain/Model/GraphQlIndexHint.cs`:
    ```csharp
    using System.Text.Json.Serialization;

    namespace Autheris.Domain.Model;

    /// <summary>
    /// Diagnostic hint emitted under <c>extensions.autheris.indexHints</c> when a GraphQL root list
    /// or <c>T_aggregate</c> field lacks a usable declared index (#573).
    /// </summary>
    public sealed record GraphQlIndexHint(
        [property: JsonPropertyName("root")] string Root,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("helpfulIndexColumns")] IReadOnlyList<string> HelpfulIndexColumns)
    {
        public const string NoUsableIndexReason = "NO_USABLE_INDEX";
    }
    ```
  - `tests/Autheris.Tests.Unit/GraphQL/AggregationIndexPricingTests.cs`
- **Modify:**
  - `src/Autheris.GraphQL/Interceptors/IndexUsabilityEvaluator.cs`
  - `src/Autheris.GraphQL/Interceptors/QueryCostAnalyzerRule.cs`
  - `src/Autheris.GraphQL/Interceptors/QueryCostAnalyzerRule.Aggregation.cs`
  - `src/Autheris.GraphQL/Interceptors/CostAndQuotaMiddleware.cs`

### 1.3 Exact Implementation Requirements & Binding Security Conditions (`SEC-53-17` .. `SEC-53-22`)

1. **`IndexUsabilityEvaluator.cs` — GroupBy Extraction & Helpful Column Builder:**
   - Add `ExtractGroupByAsOrder(IValueNode? groupByArg, IValueNode? timeBucketsArg, IReadOnlyDictionary<string, object?>? variableValues)`:
     - Returns `IReadOnlyList<(string Column, string Direction)>`.
     - For `groupByArg`:
       - Resolve top-level `VariableNode` via `variableValues`; if `groupByArg` is a `VariableNode` that cannot be resolved (or resolves to a non-collection/non-string), return `[(UnresolvedVariableMarker, "ASC")]` (`SEC-53-17`).
       - Iterate items in `ListValueNode` (or single `EnumValueNode`/`StringValueNode`, or `IEnumerable` from resolved variable): each `EnumValueNode` or `StringValueNode` with non-whitespace value maps to `(val.Trim(), "ASC")`; any unresolved nested `VariableNode` or unsupported item node maps to `(UnresolvedVariableMarker, "ASC")`.
     - For `timeBucketsArg`:
       - Resolve top-level `VariableNode` via `variableValues`; if unresolvable, append `(UnresolvedVariableMarker, "ASC")`.
       - Iterate items in `ListValueNode` (or `IReadOnlyDictionary<string, object?>` from resolved variable): extract the `column` field (`StringValueNode` or `EnumValueNode` -> `(col.Trim(), "ASC")`); if `column` is missing, blank, or an unresolved `VariableNode`, append `(UnresolvedVariableMarker, "ASC")`.
   - Add `BuildHelpfulIndexColumns(TableMetadata tableMeta, IReadOnlySet<string>? visibleColumns, IReadOnlyList<string>? filterColumns, IReadOnlyList<(string Column, string Direction)>? orderOrGroupColumns)`:
     - Collect candidate columns in first-seen order (case-insensitive deduplication via `StringComparer.OrdinalIgnoreCase`): all items in `filterColumns` followed by all `.Column` items in `orderOrGroupColumns`.
     - If candidate list is empty, return `Array.Empty<string>()`.
     - **Fail-closed visibility check (`SEC-53-19`):** For each candidate column:
       - If `string.Equals(col, UnresolvedVariableMarker, StringComparison.Ordinal)` -> return `Array.Empty<string>()`.
       - If `visibleColumns is not null && !visibleColumns.Contains(col)` -> return `Array.Empty<string>()` (if ANY referenced column is denied/invisible to the caller, the entire hint omits all columns).
       - Look up the canonical column in `tableMeta.Columns` (`c => string.Equals(c.ColumnName, col, StringComparison.OrdinalIgnoreCase)`): if not found, return `Array.Empty<string>()`; otherwise add `canonicalCol.ColumnName` to the result list.
     - Return the canonical column name list.

2. **`QueryCostAnalyzerRule.Aggregation.cs` & `QueryCostAnalyzerRule.cs` — Aggregation Surcharge & Hint Collection (`SEC-53-17` .. `SEC-53-22`):**
   - In `QueryCostAnalyzerRule.cs`:
     - Add an optional parameter `List<GraphQlIndexHint>? indexHintsOut = null` to `QueryCostAnalyzerRule.CalculateCost(...)` (keeping existing callers source-compatible).
     - Store `private readonly List<GraphQlIndexHint>? _indexHints;` on `QueryCostAnalyzerRule`.
     - In the root-list unindexed surcharge block (`QueryCostAnalyzerRule.cs:625-649`), when `!IndexUsabilityEvaluator.HasUsableIndex(tableMeta, visibleColumns, orderBy, filterColumns)`:
       - Apply the surcharge as today.
       - If `depth == 1` and `_indexHints is not null`, append:
         ```csharp
         var rootKey = field.Alias?.Value ?? field.Name.Value;
         var helpfulCols = IndexUsabilityEvaluator.BuildHelpfulIndexColumns(tableMeta, visibleColumns, filterColumns, orderBy);
         _indexHints.Add(new GraphQlIndexHint(rootKey, GraphQlIndexHint.NoUsableIndexReason, helpfulCols));
         ```
   - In `QueryCostAnalyzerRule.Aggregation.cs` (`AggregateFieldCost`):
     - Compute the base `totalCost = _aggregateFieldCostWeight + groupByWeight + aggregatesWeight` as today.
     - If `!string.Equals(field.Name.Value, "queryMetrics", StringComparison.OrdinalIgnoreCase)` (`SEC-53-22`) and `_costOptions?.UnindexedRootListSurchargeMultiplier > 1.0`:
       - Resolve `tableMeta`: look up `baseTableName = field.Name.Value.EndsWith("_aggregate", StringComparison.OrdinalIgnoreCase) ? field.Name.Value[..^"_aggregate".Length] : field.Name.Value` in `_tableMetadataLookup` (checking both `baseTableName` and `field.Name.Value`).
       - If `tableMeta is not null`:
         - Resolve `visibleColumns` for `baseTableName` / `tableMeta.Identifier` from `_visibleColumnsLookup` (`ResolveVisibleColumns(baseTableName)`).
         - Extract `groupByArg` (`groupBy`), `timeBucketsArg` (`timeBuckets`), and `whereArg` (`where`) from `field.Arguments`.
         - `var groupByAsOrder = IndexUsabilityEvaluator.ExtractGroupByAsOrder(groupByArg?.Value, timeBucketsArg?.Value, variableValues);`
         - `var filterColumns = whereArg is not null ? IndexUsabilityEvaluator.ExtractFilterColumns(whereArg.Value, variableValues) : (IReadOnlyList<string>)Array.Empty<string>();`
         - **Critical aggregation scan rule (`SEC-53-17`):** Unlike paged root lists (`first: 10`), an ungrouped and unfiltered `T_aggregate` scans every row in the table. Therefore:
           ```csharp
           bool hasUsableIndex = (groupByAsOrder.Count > 0 || filterColumns.Count > 0)
               && IndexUsabilityEvaluator.HasUsableIndex(tableMeta, visibleColumns, groupByAsOrder, filterColumns);
           ```
         - If `!hasUsableIndex`:
           - `totalCost = (int)Math.Min(int.MaxValue, Math.Ceiling(totalCost * _costOptions.UnindexedRootListSurchargeMultiplier));` (`SEC-53-18`).
           - If `_indexHints is not null`, append `new GraphQlIndexHint(field.Alias?.Value ?? field.Name.Value, GraphQlIndexHint.NoUsableIndexReason, IndexUsabilityEvaluator.BuildHelpfulIndexColumns(tableMeta, visibleColumns, filterColumns, groupByAsOrder))`.
     - Note: When `_tableMetadataLookup` is `null` or `tableMeta` is not found, `totalCost` is returned unsurcharged so existing schema-less tests in `AggregateCostAnalysisTests` continue to pass unchanged.

3. **`CostAndQuotaMiddleware.cs` — Wire `tableMetadataLookup` and Attach `extensions.autheris.indexHints` (`SEC-53-18`, `SEC-53-21`):**
   - At both `QueryCostAnalyzerRule.CalculateCost` call sites in `CostAndQuotaMiddleware.cs` (`lines 106-114` and `227-235`), pass `tableMetadataLookup: CatalogGraphQlTypeModule.CurrentLookup` and capture `indexHints` (`List<GraphQlIndexHint>`).
   - After `await _next(context)`, when `context.Result is IOperationResult opResult` and `indexHints.Count > 0`, attach `extensions["autheris"] = new Dictionary<string, object?> { ["indexHints"] = indexHints }` (merging into any existing `extensions["autheris"]` dictionary without clobbering sibling keys). Expose `AttachIndexHintsToResult(IOperationResult result, IReadOnlyList<GraphQlIndexHint> indexHints)` as `internal static` on `CostAndQuotaMiddleware` or `QueryCostAnalyzerRule` for direct unit testing.

### 1.4 Required Unit Tests (`tests/Autheris.Tests.Unit/GraphQL/AggregationIndexPricingTests.cs`)
Mark security tests with `[Trait("Category", "Security")]`:
1. `Aggregation_without_usable_index_gets_surcharge` (`[Trait("Category", "Security")]`)
2. `Aggregation_with_leading_key_filter_not_surcharged` (`[Trait("Category", "Security")]`)
3. `Aggregation_with_unresolved_variable_in_where_or_groupBy_fails_closed_with_surcharge` (`[Trait("Category", "Security")]`)
4. `Index_hint_omits_columns_when_any_is_denied` (`[Trait("Category", "Security")]`)
5. `Hint_absent_when_index_usable` (`[Trait("Category", "Security")]`)
6. `Hint_never_contains_filter_literal_values` (`[Trait("Category", "Security")]`)

### 1.5 Local Verification Commands
```bash
MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~AggregationIndexPricingTests|FullyQualifiedName~AggregateCostAnalysisTests|FullyQualifiedName~IndexUsabilityEvaluatorTests|FullyQualifiedName~QueryCostAnalyzerTests" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~DocsIntegrityTests|FullyQualifiedName~HotPathAllocationRatchetTests|FullyQualifiedName~Plan_id" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release -p:AutherisIncludeTestAuth=true --no-dependencies -m:1 -nodeReuse:false
```

---

## Brief 2: `WP-OI-CAT-7` — Federation Explain Subtree Eligibility Parity (`#612`)

- **Worktree:** `/work/wt/oi-cat-612-explain-subtrees`
- **Branch:** `feat/oi-cat-612-explain-subtrees`
- **Issue:** `#612`
- **Plan & Phase 3 References:**
  - `docs/plans/2026-11-13-plan-issues-catalog-consent-correctness.md` (§ `#612`, lines 106–115)
  - `docs/plans/2026-11-13-sec-issues-group-3.md` (§ `#612`, `SEC-OI-612-1`, `SEC-OI-612-2`, lines 57–63)

### 2.1 Context & Current State on `main` (`75bcf65a`)
- In execution (`src/Autheris.Application/Sql/Services/CrossSourcePlanner.cs:266-268`), federation subtree partitioning calls:
  ```csharp
  SubtreeGraphPartitioner.Partition(
      statement,
      tables,
      options.MaxTablesPerSubtree,
      t => SubtreeCompiler.IsPushdownEligible(t, RequireDecision(decisions, t)));
  ```
  where `t` is a **`ResolvedSourceTable`** (`SubtreeGraphPartitioner.PartitionSubtrees(SqlStatement statement, IReadOnlyList<ResolvedSourceTable> tables, int maxTablesPerSubtree = DefaultMaxTablesPerSubtree, Func<ResolvedSourceTable, bool>? isPushdownEligible = null)` in `src/Autheris.Application/Sql/Federation/Subtrees/SubtreeGraphPartitioner.cs:90-95`) and `SubtreeCompiler.IsPushdownEligible(ResolvedSourceTable table, TableAccessDecision decision)` (`src/Autheris.Application/Sql/Federation/Subtrees/SubtreeCompiler.cs:83`) takes `(ResolvedSourceTable, TableAccessDecision)`.
- In explain/analysis (`src/Autheris.Application/Governance/Analysis/QueryAnalysisService.Sql.cs:357`), `AnalyzeSqlQueryAsync` currently calls:
  ```csharp
  var partitionResult = SubtreeGraphPartitioner.PartitionSubtrees(parsedStatement, resolvedTables);
  ```
  without `maxTablesPerSubtree` or `isPushdownEligible`.
- Notice that `QueryAnalysisService.Sql.cs:225-235` (`BuildAnalysisTableDecisions` / per-table decision construction) and `QueryAnalysisService.Evaluation.cs:105-137` ALREADY compute per-column access levels and build a `TableAccessDecision` per SQL table!

### 2.2 Files to Create / Modify
- **Modify:**
  - `src/Autheris.Application/Governance/Analysis/QueryAnalysisService.Sql.cs` (and `QueryAnalysisService.cs` / `QueryAnalysisService.Evaluation.cs` if storing the already-computed per-table `TableAccessDecision` on `AnalysisScopeContext`)
- **Create Tests:**
  - `tests/Autheris.Tests.Unit/Federation/ExplainSubtreeEligibilityTests.cs`

### 2.3 Exact Implementation Requirements & Binding Security Conditions (`SEC-OI-612-1`, `SEC-OI-612-2`)

1. **Reuse Decisions Already Computed in Explain (`SEC-OI-612-2`):**
   - Explain must NOT evaluate new decisions for tables or invoke extra repository/PDP calls per table beyond the existing per-table decision explain already builds (`QueryAnalysisService.Sql.cs:225-235` / `QueryAnalysisService.Evaluation.cs:105-137`).
   - Ensure the per-table `TableAccessDecision` map built during explain (keyed by `TableIdentifier`) records:
     - Denied tables (no table grant / not visible / `Deny` column or table) as `TableAccessDecision.Denied(...)` (or absent from the allowed map).
     - Allowed tables with accurate `ColumnAccessLevels` (including `Mask`, `Deny`, `Clear`), `hasUnconstrainedColumnAllow: allColumnsAreClear` (false whenever any column of the table is `Mask` or `Deny`), and any active row filter (`CombinedRowFilterSql` / `RowPolicy`) so `SubtreeCompiler.IsPushdownEligible(t, decision)` sees the exact same eligibility inputs as `CrossSourcePlanner`.

2. **Fail-Closed `TryIsPushdownEligible(ResolvedSourceTable, ...)` & `MaxTablesPerSubtree` (`SEC-OI-612-1`):**
   - At `src/Autheris.Application/Governance/Analysis/QueryAnalysisService.Sql.cs:357`, pass the configured `MaxTablesPerSubtree` (`_gatewayOptions.WebSql?.CrossSource?.MaxTablesPerSubtree ?? SubtreeGraphPartitioner.DefaultMaxTablesPerSubtree`; note `SubtreeGraphPartitioner.Partition` already clamps non-positive values `<= 0` to `DefaultMaxTablesPerSubtree`, so no unbounded subtree is possible) and the predicate:
     ```csharp
     int maxTablesPerSubtree = _gatewayOptions.WebSql?.CrossSource?.MaxTablesPerSubtree
         ?? SubtreeGraphPartitioner.DefaultMaxTablesPerSubtree;
     var partitionResult = SubtreeGraphPartitioner.PartitionSubtrees(
         parsedStatement,
         resolvedTables,
         maxTablesPerSubtree,
         t => TryIsPushdownEligible(t, tableDecisions));
     ```
   - Implement `TryIsPushdownEligible(ResolvedSourceTable table, Func<ResolvedSourceTable, TableAccessDecision?> decisionLookup)` (or dictionary lookup):
     ```csharp
     internal static bool TryIsPushdownEligible(
         ResolvedSourceTable? table,
         Func<ResolvedSourceTable, TableAccessDecision?> decisionFor)
     {
         if (table is null || decisionFor is null)
         {
             return false;
         }

         try
         {
             var decision = decisionFor(table);
             if (decision is null || !decision.IsAllowed)
             {
                 return false;
             }

             return SubtreeCompiler.IsPushdownEligible(table, decision);
         }
         catch
         {
             return false;
         }
     }
     ```
   - **Condition C-6 (`SEC-OI-612-1`):** Tables that fail the eligibility predicate move to the existing single-table fallback staging list (`partitionResult.FallbackTables`); `AnalyzedSubtree` and the serialized explain response MUST keep their exact current property set — no new field, no column list, and no ineligibility reason text!

### 2.4 Required Unit Tests (`tests/Autheris.Tests.Unit/Federation/ExplainSubtreeEligibilityTests.cs`)
Write all 7 tests specified in the plan and Phase 3 review (marking security tests with `[Trait("Category", "Security")]`):
1. `Explain_MaskedMember_NotShownInSubtree`:
   - Same-source join where one member has a masked column -> not grouped into a multi-table pushdown subtree; moved to single-table fallback.
2. `Explain_AllClearMembers_ShownAsSubtree`:
   - Same-source join where both members are allowed with all columns `Clear` and no row filters -> shown as a 2-table pushdown subtree.
3. `Explain_UsesConfiguredMaxTablesPerSubtree`:
   - Configured `MaxTablesPerSubtree` bounds the explain subtree size identically to execution (and non-positive `<= 0` falls back to `DefaultMaxTablesPerSubtree`).
4. `Explain_SubtreeMembers_EqualCrossSourcePlannerPartition_ForSameDecisions`:
   - For identical `ResolvedSourceTable` list and `TableAccessDecision` map, explain's partition output matches `CrossSourcePlanner` partitioning.
5. `Explain_IneligibleMember_ExposesNoColumnOrReasonDetail` (`[Trait("Category", "Security")]`, C-6 / `SEC-OI-612-1`):
   - Reflection test pinning the exact public property set of `AnalyzedSubtree` (and serialized JSON keys of the explain subtree output) and asserting that an ineligible member exposes no column name or ineligibility reason detail.
6. `Explain_MissingDecision_TreatedAsIneligible` (`[Trait("Category", "Security")]`, `SEC-OI-612-1`):
   - When a table has no decision (`null`) or a `Denied` decision, `TryIsPushdownEligible` returns `false` and the table is not placed in a multi-table subtree.
7. `Explain_DecisionThrows_TableIneligible_NotSubtree` (`[Trait("Category", "Security")]`, `SEC-OI-612-1`):
   - When the decision lookup throws an exception for a table, `TryIsPushdownEligible` catches it, returns `false`, and keeps the table out of multi-table subtrees without failing the explain call.

### 2.5 Local Verification Commands
```bash
MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~ExplainSubtreeEligibilityTests|FullyQualifiedName~QueryAnalysisServiceTests|FullyQualifiedName~SubtreeGraphPartitionerTests" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~DocsIntegrityTests|FullyQualifiedName~Plan_id" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release -p:AutherisIncludeTestAuth=true --no-dependencies -m:1 -nodeReuse:false
```

---

## Brief 3: `WP-OI-CAT-3` — Consent Revoke Authority Enforcement & `ROLE_MEMBERS` Removal (`#670`)

- **Worktree:** `/work/wt/oi-cat-670-revoke-auth`
- **Branch:** `feat/oi-cat-670-revoke-auth`
- **Issue:** `#670`
- **Precondition Verified:** `#667` (`WP-OI-CAT-2`) is already merged on `main` (`1e926a8e`).
- **Plan & Phase 3 References:**
  - `docs/plans/2026-11-13-plan-issues-catalog-consent-correctness.md` (§ `#670`, lines 56–70)
  - `docs/plans/2026-11-13-sec-issues-group-3.md` (§ `#670`, `SEC-OI-670-1` .. `SEC-OI-670-6`, lines 25–35)
  - `docs/plans/2026-11-14-owner-decisions-sec-groups-1-9.md` (`Q-SEC3-1` confirmed: removing the `ROLE_MEMBERS` branch in `IsAuthorizedApproverForTableInternalAsync` also stops DB-role admins from approving consent requests; approvals go through owner, delegate, or token admin).

### 3.1 Context & Current State on `main` (`75bcf65a`)
1. `src/Autheris.Infrastructure/Persistence/GovernanceRepository.ConsentGrants.cs:261-274` (`RevokeConsentInTransactionAsync`):
   - Checks `!found.IsGranteeSelf && !await IsAuthorizedApproverForTableInternalAsync(conn, tx, tableId.Value, actor, null, ct)` and throws `UnauthorizedAccessException($"User '{actor}' is neither a data owner nor a delegated approver for '{tableId.Value}', nor the beneficiary.")` (which becomes HTTP 500 on declarative apply and leaks `actor` SID and `tableId` in the message!).
   - Furthermore, `IsAuthorizedApproverForTableInternalAsync` (`:306-359`) has a third SQL branch (`:347-358`) that queries `FROM ROLES r JOIN ROLE_MEMBERS rm ON r.id = rm.role_id WHERE (r.role_name = 'GovernanceAdmin' OR r.role_name = 'ClusterAdmin')` — so token-authenticated `GovernanceAdmin` and `ClusterAdmin` callers who are not in the dead `ROLE_MEMBERS` table are rejected inside the transaction!
2. Callers of `RevokeConsentAsync` and `ReplaceConsentsAsync`:
   - `src/Autheris.Application/Governance/Services/DeclarativeConsentApplyService.cs:403, 409` (`ReplaceConsentsAsync`)
   - `src/Autheris.GraphQL/Types/Mutation.Consent.cs:933` (`RevokeConsentAsync`)
   - `src/Autheris.Application/Identity/Attributes/AttributeValidatingConsentRepository.cs:60, 76, 106` (decorator over `IConsentRepository` / `IConsentDescriptionRepository`)
3. Dead `ROLE_MEMBERS` seeds and `GetTransitiveRolesAsync`:
   - `src/Autheris.Infrastructure/Persistence/GovernanceRepository.Schema.cs:413, 616` (seed rows for `ROLES` and `ROLE_MEMBERS`).
   - `src/Autheris.Infrastructure/Persistence/GovernanceRepository.CatalogRelations.cs:124-128` (`GetTransitiveRolesAsync`), `src/Autheris.Infrastructure/Persistence/Internal/CatalogRows.cs:33, 557` (`RoleEdgeRow` and `GetTransitiveRolesAsync`), and `src/Autheris.Application/Common/Interfaces/IGovernanceRepository.cs:348` (`IDataOwnershipRepository.GetTransitiveRolesAsync`), plus test callers in `tests/Autheris.Tests.Unit/Security/TableStreamVisibilityTests.cs` and `tests/Autheris.Tests.Unit/Security/GovernanceSecurityTests.cs`.

### 3.2 Files to Create / Modify
- **Create:**
  - `src/Autheris.Application/Governance/Grants/ConsentRevocationAuthority.cs` (and `ConsentRevocationForbiddenException`)
  - `tests/Autheris.Tests.Unit/Governance/ConsentRevocationAuthorityTests.cs`
  - `tests/Autheris.Tests.Integration/Governance/ConsentRevocationAuthorityTests.cs` (compile-only locally; runs in CI against PostgreSQL and SQL Server)
- **Modify:**
  - `src/Autheris.Application/Common/Interfaces/IGovernanceRepository.cs` (`IConsentRepository.RevokeConsentAsync`, `IConsentRepository.ReplaceConsentsAsync`, `IConsentDescriptionRepository.ReplaceConsentsAsync`, and remove `IDataOwnershipRepository.GetTransitiveRolesAsync`)
  - `src/Autheris.Application/Identity/Attributes/AttributeValidatingConsentRepository.cs`
  - `src/Autheris.Infrastructure/Persistence/GovernanceRepository.ConsentGrants.cs`
  - `src/Autheris.Infrastructure/Persistence/GovernanceRepository.CatalogRelations.cs` (remove `GetTransitiveRolesAsync`)
  - `src/Autheris.Infrastructure/Persistence/Internal/CatalogRows.cs` (remove `RoleEdgeRow` and `GetTransitiveRolesAsync`)
  - `src/Autheris.Infrastructure/Persistence/GovernanceRepository.Schema.cs` (remove `ROLES` / `ROLE_MEMBERS` seed inserts at `:413, 616`; keep `CREATE TABLE` DDL intact until `WP-SIMPL-52 PR 6b`)
  - `src/Autheris.Application/Governance/Services/DeclarativeConsentApplyService.cs`
  - `src/Autheris.Api/Endpoints/GovernanceEndpoints.Catalog.cs` (map `ConsentRevocationForbiddenException` to 403 `ACCESS_DENIED` via `ApiProblems.Forbidden`)
  - `src/Autheris.GraphQL/Types/Mutation.Consent.cs` (pass `ConsentRevocationAuthority.FromPrincipal(principal)` to `RevokeConsentAsync` and map `ConsentRevocationForbiddenException` to GraphQL error code `FORBIDDEN`)
  - `tests/Autheris.Tests.Architecture/CleanArchitectureTests.cs` (or a dedicated test in `tests/Autheris.Tests.Architecture` for `Authority_NotConstructibleOutsideFactory`)
  - Existing unit/integration callers of `RevokeConsentAsync` / `ReplaceConsentsAsync` / `GetTransitiveRolesAsync` (`ConsentReplaceRepositoryTests.cs`, `TableStreamVisibilityTests.cs`, `GovernanceSecurityTests.cs`, etc.)

### 3.3 Exact Implementation Requirements & Binding Security Conditions (`SEC-OI-670-1` .. `SEC-OI-670-6`, `Q-SEC3-1`)

1. **Unforgeable `ConsentRevocationAuthority` (`SEC-OI-670-1`):**
   - Define `ConsentRevocationAuthority` in `src/Autheris.Application/Governance/Grants/ConsentRevocationAuthority.cs`:
     - Must have a **`private` constructor** and **exactly one public factory method `FromPrincipal(ClaimsPrincipal? principal)`** (using `GatewayRoleEvaluator` and `ClusterAdminPolicy.IsCanonicalClusterAdmin` + `principal.GetTenantId()`).
     - It is **NOT a DTO**: no `[JsonConstructor]`, no public setters or `init` properties, never bound from a request body or deserialized.
     - Represents three closed states:
       1. `GranteeOrApproverOnly` (default when `principal` is null, unauthenticated, has no admin role, or is a tenant-bound `GovernanceAdmin` whose tenant claim is missing/empty/invalid!).
       2. `TenantGovernanceAdmin(TenantId tenant)` (when `GatewayRoleEvaluator` grants `GatewayRole.GovernanceAdmin` and `principal.GetTenantId()` succeeds with a valid `TenantId`).
       3. `ClusterAdmin` (only when `ClusterAdminPolicy.IsCanonicalClusterAdmin(principal)` is true).
     - Null/default handling (`SEC-OI-670-2`): `internal static ConsentRevocationAuthority Effective(ConsentRevocationAuthority? authority) => authority ?? GranteeOrApproverOnly;` so a `null` reference passed at runtime behaves identically to `GranteeOrApproverOnly` (never `ClusterAdmin` or "allowed").
     - Add architecture test `Authority_NotConstructibleOutsideFactory` in `tests/Autheris.Tests.Architecture`: inspect `ConsentRevocationAuthority` via reflection (all constructors `IsPrivate`, zero `[JsonConstructor]` attributes, zero public setters/init properties) and scan `src/**/*.cs` to assert `new ConsentRevocationAuthority(` appears only inside `ConsentRevocationAuthority.cs`.

2. **No Default Parameter Anywhere on Repository Interfaces or Decorator (`SEC-OI-670-2`):**
   - On `IConsentRepository`, `IConsentDescriptionRepository`, `AttributeValidatingConsentRepository`, and `GovernanceRepository`:
     - `Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default);`
     - `Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default);`
     - `Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries, IReadOnlyList<ConsentDescriptionUpdate>? descriptionUpdates, CancellationToken ct = default);`
   - Do **NOT** provide `ConsentRevocationAuthority? authority = null` default parameter values on any production interface, decorator, or repository method — omitting `authority` in production code MUST be a compile error!

3. **In-Transaction Tenant Re-Read & Uniform Not-Found Before Authz (`SEC-OI-670-3`):**
   - Inside `RevokeConsentInTransactionAsync(DbConnection conn, DbTransaction tx, Guid consentId, Sid actor, string reason, ConsentRevocationAuthority authority, CancellationToken ct)`:
     - Call `ConsentGrantSqlCore.ReadRevokeTargetAsync(conn, tx, RevokeTargetSql, consentId, actor, ct)`.
     - If `target is not { } found`, throw `new KeyNotFoundException($"Consent with ID '{consentId}' does not exist.")`.
     - Resolve the consent's stored tenant: `var tenant = ConsentGrantSqlCore.ResolveConsentTenant(found.TenantRaw, consentId, found.Table.ToString());`
     - **Cross-tenant uniform not-found (`SEC-OI-670-3`):** If `authority` is `TenantGovernanceAdmin(var callerTenant)` and `!string.Equals(callerTenant.Value, tenant.Value, StringComparison.OrdinalIgnoreCase)`, throw the exact same `new KeyNotFoundException($"Consent with ID '{consentId}' does not exist.")` **BEFORE** any authorization branch (`IsGranteeSelf` or `IsAuthorizedApproverForTableInternalAsync`) is evaluated! Similarly, if a caller passes a tenant-scoped check where the consent belongs to another tenant, foreign consent IDs must produce a byte-equal not-found response to unknown consent IDs (zero 403-vs-404 oracle).
     - Authorization evaluation for same-tenant (or `ClusterAdmin`):
       - Allowed if:
         - `authority.CoversTenant(tenant)` is `true` (`ClusterAdmin` or `TenantGovernanceAdmin` with `callerTenant == tenant`), OR
         - `found.IsGranteeSelf` is `true`, OR
         - `await IsAuthorizedApproverForTableInternalAsync(conn, tx, found.Table, actor, null, ct)` is `true`.

4. **Typed `ConsentRevocationForbiddenException` (403, Fixed Message, Audited Denial — `SEC-OI-670-4`):**
   - Define `ConsentRevocationForbiddenException`:
     ```csharp
     public sealed class ConsentRevocationForbiddenException : Exception
     {
         public const string FixedMessage = "You are not authorized to revoke this consent grant.";

         public ConsentRevocationForbiddenException(Guid consentId, Sid actorSid, TenantId tenantId, string targetTable)
             : base(FixedMessage)
         {
             ConsentId = consentId;
             ActorSid = actorSid;
             TenantId = tenantId;
             TargetTable = targetTable;
         }

         public Guid ConsentId { get; }
         public Sid ActorSid { get; }
         public TenantId TenantId { get; }
         public string TargetTable { get; }
     }
     ```
   - Notice `Message` is the constant `FixedMessage` — it contains **no actor SID and no table id** (`SEC-OI-670-4`).
   - When `RevokeConsentInTransactionAsync` denies revocation, throw `new ConsentRevocationForbiddenException(consentId, actor, tenant, found.Table.ToString())`; in `RevokeConsentAsync` and `ReplaceConsentsAsync`, catch `ConsentRevocationForbiddenException`, roll back the transaction, record a denial audit event (`EventType = "CONSENT_REVOKE_DENIED"`, `TenantId = ex.TenantId`, `ActorSid = ex.ActorSid`, `TargetTable = ex.TargetTable`, `Decision = "Deny"`, `DetailsJson` with `ConsentId = ex.ConsentId`), and rethrow `ex`.
   - Map `ConsentRevocationForbiddenException` to:
     - **REST (`GovernanceEndpoints.Catalog.cs`):** 403 `ACCESS_DENIED` (`ApiProblems.Forbidden(..., ConsentRevocationForbiddenException.FixedMessage)`).
     - **GraphQL (`Mutation.Consent.cs`):** GraphQL error with code `FORBIDDEN` and message `ConsentRevocationForbiddenException.FixedMessage` (and remove the pre-repository `UnauthorizedAccessException` throw in `Mutation.Consent.cs:905-930` or align it so the repository is the authoritative enforcement point and foreign-tenant IDs return `NOT_FOUND`).

5. **Remove `ROLES` / `ROLE_MEMBERS` Branch from `IsAuthorizedApproverForTableInternalAsync` & Delete Dead Code (`SEC-OI-670-5`, `Q-SEC3-1`):**
   - In `src/Autheris.Infrastructure/Persistence/GovernanceRepository.ConsentGrants.cs:347-358`, delete the `roleCmd` query (`SELECT COUNT(*) FROM ROLES r JOIN ROLE_MEMBERS rm ...`) and return `false` after the `DATA_OWNER_DELEGATIONS` check. Also remove the unused `itsmApproverAccount` parameter if no caller passes a non-null value (or keep only the 2-arg overload if still referenced by `WP-SIMPL-47`).
   - Because `IsAuthorizedApproverForTableInternalAsync` is shared with consent request approval flows (`Q-SEC3-1`), removing the `ROLE_MEMBERS` branch also stops DB-role rows from granting approval authority; add test `Approve_RoleMembersRowWithoutOwnership_IsDenied`.
   - Delete `GetTransitiveRolesAsync` from `IGovernanceRepository.cs:348`, `GovernanceRepository.CatalogRelations.cs:124-128`, `CatalogRows.cs:33, 557`, `AttributeValidatingConsentRepository.cs` (if present), and remove dead `ROLES`/`ROLE_MEMBERS` seed rows from `GovernanceRepository.Schema.cs:413, 616` (keeping the `CREATE TABLE` DDL statements untouched so schema shape tests stay green until `WP-SIMPL-52 PR 6b`).

6. **`ReplaceConsentsAsync` Atomicity (`SEC-OI-670-6`):**
   - In `GovernanceRepository.ConsentGrants.cs:125-138`, `ReplaceConsentsAsync` already runs all `RevokeConsentInTransactionAsync` calls (`lines 125-132`) **before** the first `InsertConsentCoreAsync` (`line 136`) and `UpdateConsentDescriptionInTransactionAsync` (`line 144`). Ensure that if any revocation in `revokeConsentIds` fails the authority check (or is not found), the transaction rolls back completely before any insert/update commits, and the denial is audited (`Replace_PartialDenial_RollsBackWholeManifest`).

### 3.4 Required Tests (`[Trait("Category", "Security")]`)
1. **Architecture Test (`tests/Autheris.Tests.Architecture`):**
   - `Authority_NotConstructibleOutsideFactory` (`SEC-OI-670-1`)
2. **Unit Tests (`tests/Autheris.Tests.Unit/Governance/ConsentRevocationAuthorityTests.cs`):**
   - `FromPrincipal_TenantPrefixedGovernanceAdmin_IsTenantBound`
   - `FromPrincipal_CanonicalClusterAdmin_IsGlobalClusterAdmin`
   - `Authority_TenantBoundWithoutTenantClaim_MatchesNothing` (`SEC-OI-670-1`, `SEC-OI-670-3`)
   - `FromPrincipal_ConsumerOrUnauthenticated_IsGranteeOrApproverOnly`
   - `Revoke_ForeignTenantConsent_ForbiddenAndUnknownLookLikeEachOther` (byte-equal exception/problem response, `SEC-OI-670-3`)
   - `Revoke_DenialMessage_ContainsNoSidOrTable` (`SEC-OI-670-4`)
   - `Approve_RoleMembersRowWithoutOwnership_IsDenied` (`SEC-OI-670-5`, `Q-SEC3-1`)
   - `Revoke_TokenGovernanceAdmin_SameTenant_Succeeds`
   - `Revoke_TokenClusterAdmin_AnyTenant_Succeeds`
   - `Revoke_RoleMembersRowWithoutTokenRole_IsDenied`
   - `Revoke_ConsumerWithoutOwnership_IsDenied_403`
   - `Revoke_OwnerOrDelegate_Succeeds`
   - `Revoke_GranteeSelf_Succeeds`
   - `Replace_ViaApply_TokenGovernanceAdmin_Succeeds`
   - `Replace_PartialDenial_RollsBackWholeManifest` (`SEC-OI-670-6`)
3. **Integration Contract Tests (`tests/Autheris.Tests.Integration/Governance/ConsentRevocationAuthorityTests.cs`, compile-only locally):**
   - Mirror the contract suite cases for PostgreSQL and SQL Server.

### 3.5 Local Verification Commands
```bash
MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~ConsentRevocationAuthorityTests|FullyQualifiedName~ConsentReplaceRepositoryTests|FullyQualifiedName~DeclarativeConsentApply|FullyQualifiedName~TableStreamVisibilityTests|FullyQualifiedName~GovernanceSecurityTests" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~Authority_NotConstructibleOutsideFactory|FullyQualifiedName~CleanArchitectureTests|FullyQualifiedName~DocsIntegrityTests|FullyQualifiedName~Plan_id" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release -p:AutherisIncludeTestAuth=true --no-dependencies -m:1 -nodeReuse:false
```

---

## Brief 4: `WP-OI-SQL-478` — Flat Same-Precedence Operator Chains & Bounded-Stack Evaluation (`#478`)

- **Issue:** `#478`
- **Plan & Phase 3 References:**
  - `docs/plans/2026-11-13-plan-issues-sql-engine-dialects.md` (§ `#478`, lines 19–39)
  - `docs/plans/2026-11-13-sec-issues-group-5.md` (§ `#478`, `SEC-OI-478-1` .. `SEC-OI-478-5`, lines 14–22)
  - `docs/plans/2026-11-14-owner-decisions-sec-groups-1-9.md` (`Q-SEC5-1` confirmed: lower `MaxOperatorChainTerms` constant if a walker fails the 1,000-term 512 KiB small-stack test; never relax the test).
- **Slicing:** Executed as two independent PRs (`478-A` for `TrinoSqlEngine` and `478-B` for `ODataFilterParser`).

---

### Brief 4A: `WP-OI-SQL-478-A` — `TrinoSqlEngine` Iterative Operator-Chain Folding & Downstream Walker Stack Safety (`#478`)

- **Worktree:** `/work/wt/oi-sql-478a-trino-chains`
- **Branch:** `feat/oi-sql-478a-trino-chains`

#### 4A.1 Context & Current State on `main` (`75bcf65a`)
- `src/TrinoSqlEngine/Ast/Builder/SqlAstBuilder.cs` calls `using var _ = EnterScope();` (line 29, limit `AstBuilderOptions.MaxAllowedAstDepth = 64`) on every binary operator in a left-associative ANTLR chain in `VisitAnd` (l.868), `VisitOr` (l.876), `VisitArithmeticBinary` (l.1003), and `VisitConcatenation` (l.1131), recursing into `context.left`.
- Downstream recursive AST walkers over `BinaryExpression` (security visitor, dialect generators, row-filter injection, hashing, federation conjunct splitters) otherwise put 1,000 stack frames onto the thread stack for a 1,000-term left-deep chain.

#### 4A.2 Files to Modify / Create
- **Modify:**
  - `src/TrinoSqlEngine/Ast/Builder/SqlAstBuilder.cs` (add `public const int MaxOperatorChainTerms = 1_000;` and iteratively collect/fold spines in `VisitAnd`, `VisitOr`, `VisitArithmeticBinary`, `VisitConcatenation` — do **NOT** touch `SqlEngineOptions.cs` or add any configuration option!)
  - Downstream `BinaryExpression` walkers in `src/TrinoSqlEngine/**` and `src/Autheris.Application/Sql/Federation/**` (ensure iterative left-spine traversal or `ExecutionStackGuard` so the full pipeline succeeds on a 1,000-term chain inside a 512 KiB stack thread)
- **Create Tests:**
  - `tests/TrinoSqlEngine.Tests/Ast/AstDepthGuardTests.cs`

#### 4A.3 Exact Implementation Requirements & Binding Security Conditions (`SEC-OI-478-1` .. `SEC-OI-478-5`, `Q-SEC5-1`)

1. **Constant `SqlAstBuilder.MaxOperatorChainTerms = 1_000` & Iterative Spine Folding (`SEC-OI-478-1`, `SEC-OI-478-3`):**
   - Define `public const int MaxOperatorChainTerms = 1_000;` on `SqlAstBuilder` (constant, **NOT** an option: zero freeze ratchet growth).
   - In `VisitAnd`, `VisitOr`, `VisitArithmeticBinary`, and `VisitConcatenation`:
     - Enter `using var _ = EnterScope();` **once** for the entire contiguous left spine of the same context type (`AndContext`, `OrContext`, `ArithmeticBinaryContext` regardless of `+`/`-`/`*`/`/`/`%`, `ConcatenationContext`).
     - Walk `context.left` iteratively while it is the same context type, collecting `(rightContext, operator)` items into a list.
     - Count the terms in the spine (`terms = rightItems.Count + 1`). If `terms > MaxOperatorChainTerms`, throw the existing `AstBuildException` depth message family (`SEC-OI-478-1`).
     - Visit the leftmost operand, then iterate right operands in left-to-right order and fold bottom-up into `new BinaryExpression(acc, op, right)` so the produced AST is **byte-identical** to the prior recursive build (`SEC-OI-478-5`).
     - Mixed alternation (`(a OR b) AND c OR d ...`), `ParenthesizedExpression`, `NOT`, subqueries, `CASE`, and function calls MUST continue calling `EnterScope()` on each level/switch (`SEC-OI-478-3`).

2. **Full-Pipeline 1,000-Term Stack Safety on a 512 KiB Stack (`SEC-OI-478-2`, `Q-SEC5-1`):**
   - Inspect every recursive walker over `BinaryExpression` in the full pipeline (parse, `AstSecurityVisitor`, policy injection, SQL generation for PostgreSQL, SQL Server, Oracle, Databricks, DuckDB, AST hashing/rewriting, and federation staging/conjunct splitting).
   - Unroll left-deep `BinaryExpression` spines iteratively (using an explicit heap `Stack<BinaryExpression>`) in `AstSecurityVisitor`, `SqlDialectGeneratorBase`, `SqlAstRewriter`, `AstHasher` / `SecurityCoverageVerifier`, and federation conjunct walkers so a 1,000-term `OR`, `AND`, `||`, or `+` chain executes the full pipeline on a thread with a 512 KiB stack (`new Thread(..., maxStackSize: 512 * 1024)`) without `StackOverflowException` or `InsufficientExecutionStackException`.
   - **Owner Decision `Q-SEC5-1`:** If any walker still fails the 512 KiB small-stack test at 1,000 terms after unrolling, lower the `MaxOperatorChainTerms` constant — **never** relax the 512 KiB small-stack test!

#### 4A.4 Required Unit Tests (`tests/TrinoSqlEngine.Tests/Ast/AstDepthGuardTests.cs`)
Mark security tests with `[Trait("Category", "Security")]`:
1. `Or_Chain_Of_200_Terms_Is_Accepted`
2. `And_Chain_Of_200_Terms_Is_Accepted`
3. `Concat_Chain_Of_200_Terms_Is_Accepted`
4. `Arithmetic_Chain_Of_200_Terms_Is_Accepted`
5. `Chain_Ast_Is_Identical_To_Recursive_Build` (golden SQL per dialect for a 5-term chain)
6. `Deep_Parenthesised_Nesting_Is_Still_Rejected` (`[Trait("Category", "Security")]`, 65 nested parentheses)
7. `Nested_Subqueries_Still_Count_Per_Level` (`[Trait("Category", "Security")]`)
8. `Chain_Above_MaxOperatorChainTerms_Is_Rejected_Typed` (`[Trait("Category", "Security")]`)
9. `Mixed_And_Or_Alternation_Counts_Each_Switch` (`[Trait("Category", "Security")]`)
10. `Chain_1000_Terms_FullPipeline_SmallStack_NoStackOverflow` (`[Trait("Category", "Security")]`, `SEC-OI-478-2`: runs parse, `AstSecurityVisitor`, policy injection, generation for PostgreSQL, SQL Server, Oracle, Databricks, DuckDB, and federation staging for 1,000-term `OR`, `AND`, `||`, and `+` chains on a 512 KiB stack thread)
11. `Chain_1001_Terms_Rejected_Typed` (`[Trait("Category", "Security")]`, `SEC-OI-478-1`)
12. `Mixed_Operator_Arithmetic_Spine_Counts_As_One_Chain` (`[Trait("Category", "Security")]`, `SEC-OI-478-1`)
13. `Chain_Security_Visitor_Outcome_Identical_To_Recursive_Build` (`[Trait("Category", "Security")]`, `SEC-OI-478-5`: verifies required filters and forbidden/masked columns at the end of a long chain)

#### 4A.5 Local Verification Commands for 4A
```bash
MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/TrinoSqlEngine.Tests/TrinoSqlEngine.Tests.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~AstDepthGuardTests" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~DocsIntegrityTests|FullyQualifiedName~Plan_id" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s
```

---

### Brief 4B: `WP-OI-SQL-478-B` — `ODataFilterParser` `ChainNode` Flat Boolean Chains (`#478`)

- **Worktree:** `/work/wt/oi-sql-478b-odata-chains`
- **Branch:** `feat/oi-sql-478b-odata-chains`

#### 4B.1 Context & Current State on `main` (`75bcf65a`)
- In `src/Autheris.Application/OData/ODataFilterParser.cs`:
  - `ParseOr` (`l.410`) and `ParseAnd` (`l.430`) loop iteratively, but build nested left-deep `BinaryOpNode` (`l.714`) whose `ToSql` calls `ctx.EnterDepth()` (`l.602`, limit `MaxRecursionDepth + 5 = 37`, `MaxRecursionDepth = 32` at `l.19`); `CollectReferencedColumns` has the same recursive depth walk.

#### 4B.2 Files to Modify / Create
- **Modify:**
  - `src/Autheris.Application/OData/ODataFilterParser.cs`
- **Create Tests:**
  - `tests/Autheris.Tests.Unit/OData/ODataFilterParserRecursionTests.cs`

#### 4B.3 Exact Implementation Requirements & Binding Security Conditions (`SEC-OI-478-4`)
1. **N-ary `ChainNode(op, operands)` in `ODataFilterParser.cs` (`SEC-OI-478-4`):**
   - Add `public const int MaxChainOperands = 1_000;` on `ODataFilterParser`.
   - In `ParseOr` (`l.410`) and `ParseAnd` (`l.430`), when there are 2 or more operands in a same-operator chain, build one n-ary `ChainNode(string Operator, IReadOnlyList<FilterNode> Operands)` (bounded by existing `MaxFilterLength = 4_096` and `Operands.Count <= MaxChainOperands`, throwing `FormatException` if exceeded).
   - In `ChainNode.ToSql(SqlGenerationContext context)` and `CollectReferencedColumns`:
     - Call `using var _ = context.EnterDepth();` **once** for the `ChainNode`.
     - Iterate `Operands` sequentially (emitting identical left-associative parenthesised SQL `((a OR b) OR c)` or `(a OR b)` as `BinaryOpNode` did, so golden SQL is preserved).
     - `CollectReferencedColumns` MUST iterate and return **every** operand's columns (`SEC-OI-478-4`: a missed column would let a required-filter or column-permission check pass).
   - `GroupingNode` (`(` ... `)`), `NotOpNode`, and `FunctionCallNode` keep calling `EnterDepth()` per nesting level.

#### 4B.4 Required Unit Tests (`tests/Autheris.Tests.Unit/OData/ODataFilterParserRecursionTests.cs`)
Mark security tests with `[Trait("Category", "Security")]`:
1. `Or_Chain_Of_200_Terms_Is_Accepted`
2. `And_Chain_Within_MaxFilterLength_Is_Accepted`
3. `Nested_Parentheses_Beyond_Limit_Are_Rejected` (`[Trait("Category", "Security")]`)
4. `Not_Nesting_Beyond_Limit_Is_Rejected` (`[Trait("Category", "Security")]`)
5. `ChainNode_CollectReferencedColumns_Returns_All_Columns` (`[Trait("Category", "Security")]`)
6. `OData_Chain_CollectReferencedColumns_Includes_Every_Operand` (`[Trait("Category", "Security")]`, `SEC-OI-478-4`: verifies the 200th operand's column is included in `ReferencedColumns` and validated against allowed columns)

#### 4B.5 Local Verification Commands for 4B
```bash
MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~ODataFilterParserRecursionTests|FullyQualifiedName~ODataFilterParserTests" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~DocsIntegrityTests|FullyQualifiedName~Plan_id" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s
```
