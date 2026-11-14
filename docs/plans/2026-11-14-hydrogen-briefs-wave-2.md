# Phase 4 Hydrogen Task Briefs — Wave 2 (2026-11-14)

**Status:** Phase 3 APPROVED WITH CONDITIONS — Ready for Phase 4 Execution (`csharp-tdd-developer` on Gemini 4 Hydrogen)
**Baseline Verified:** `origin/main` @ `2d6715a0` (2026-11-14)
**Deduplication Check:** Verified against `gh pr list --state open` (`#748`/`#666`, `#747`/`#668`, `#744`/`#669`, `#734`/`#624`) and merged `main` commits (`ddb53afb` PR 1 `#578`, `554ace53` PR 2 `#579`, `9b3e8852` PR 3 `#572`, `ba5dd11d` PR 5 `#573` WebSQL). Zero items in this wave duplicate open or merged PRs.

---

## Global Execution Rules for All Wave-2 Briefs (Mandatory)

1. **Dedicated Git Worktree & Branch:**
   - Never work in `/root/autheris` directly. Create the dedicated worktree under `/work/wt/<worktree-name>` branching from `origin/main` (`2d6715a0` or later).
2. **Strict Local Resource Guardrails (`csharp-tdd-developer`):**
   - Always prefix `dotnet` commands with `MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget`.
   - Always pass `-c Release -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false` on build/test commands, and `--blame-hang --blame-hang-timeout 120s` on `dotnet test`.
   - **NEVER** run the full unfiltered unit test suite or any integration test locally. Run **only** the targeted `--filter` unit and architecture tests specified in your brief.
   - Compile affected integration test projects locally with `dotnet build ... --no-dependencies` only.
3. **PR Size & Hygiene:**
   - Each PR must stay `<= 400` net added lines in `src/`.
   - Never write absolute URI links (the literal scheme `file` followed by colon-slash-slash) inside any `.md` file (`DocsIntegrityTests.No_absolute_file_links`).
   - Do not delete your worktree until the PR is merged or `@coordinator` instructs you.

---

## Brief 1: `WP-SIMPL-53 PR 4` — GraphQL Aggregation Index Pricing & `extensions.autheris.indexHints` (`#573`)

- **Worktree:** `/work/wt/simpl-53-pr4-gql-index-hints`
- **Branch:** `feat/simpl-53-pr4-gql-index-hints`
- **Issue:** `#573`
- **Plan & Phase 3 References:**
  - `docs/plans/simplification/WP-SIMPL-53-mcp-catalog-client-hints.md` (§4 step 4, §5, and Phase 3 binding conditions `SEC-53-17` .. `SEC-53-22`)

### 1.1 Context & Current State on `main` (`2d6715a0`)
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
    /// or <c>T_aggregate</c> field lacks a usable declared index (#573, WP-SIMPL-53 PR 4).
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
       - Look up the canonical column in `tableMeta.Columns` (`c => string.Equals(c.Name, col, StringComparison.OrdinalIgnoreCase)`): if not found, return `Array.Empty<string>()`; otherwise add `canonicalCol.Name` to the result list.
     - Return the canonical column name list.

2. **`QueryCostAnalyzerRule.Aggregation.cs` & `QueryCostAnalyzerRule.cs` — Aggregation Surcharge & Hint Collection (`SEC-53-17` .. `SEC-53-22`):**
   - In `QueryCostAnalyzerRule.cs`:
     - Add an optional parameter `List<GraphQlIndexHint>? indexHintsOut = null` to `QueryCostAnalyzerRule.CalculateCost(...)` (or an overload that populates `out IReadOnlyList<GraphQlIndexHint> indexHints` while keeping the existing signature binary/source-compatible for all callers).
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
   - Store the captured `IReadOnlyList<GraphQlIndexHint>` on `context.ContextData["Autheris.IndexHints"]` when `indexHints.Count > 0`.
   - After `await _next(context)` (and on `CreateErrorResult` if applicable, or on `IoperationResult` in `CostAndQuotaMiddleware` when `context.Result is IOperationResult opResult` and `indexHints.Count > 0`), attach `extensions["autheris"]`:
     - Construct a helper `AttachIndexHintsToResult(IOperationResult result, IReadOnlyList<GraphQlIndexHint> indexHints)` (and expose it as `internal static` or on `QueryCostAnalyzerRule` so unit tests can also verify the extension payload structure directly).
     - When `indexHints.Count > 0`, build a new `OperationResultBuilder.FromResult(result)` (or merge into `Extensions`) setting `extensions["autheris"] = new Dictionary<string, object?> { ["indexHints"] = indexHints }` (preserving any existing keys if `extensions["autheris"]` is already a dictionary). When `indexHints.Count == 0`, do not add `extensions.autheris`.

### 1.4 Required Unit Tests (`tests/Autheris.Tests.Unit/GraphQL/AggregationIndexPricingTests.cs`)
Write comprehensive unit tests (marking security tests with `[Trait("Category", "Security")]`):
1. `Aggregation_without_usable_index_gets_surcharge` (`[Trait("Category", "Security")]`):
   - Table `orders` with index on `(customer_id)` and unindexed column `status`.
   - `orders_aggregate(groupBy: [status]) { group { status } aggregates { count } }` with `UnindexedRootListSurchargeMultiplier = 2.0` -> cost is `Math.Ceiling(baseCost * 2.0)` and `indexHints` contains 1 hint `{ Root = "orders_aggregate", Reason = "NO_USABLE_INDEX", HelpfulIndexColumns = ["status"] }`.
   - Also assert an ungrouped, unfiltered `orders_aggregate { aggregates { count } }` gets the surcharge and emits `{ Root = "orders_aggregate", Reason = "NO_USABLE_INDEX", HelpfulIndexColumns = [] }`.
2. `Aggregation_with_leading_key_filter_not_surcharged` (`[Trait("Category", "Security")]`):
   - `orders_aggregate(where: { customer_id: { eq: 42 } }) { aggregates { count } }` where `(customer_id)` is a declared index -> NOT surcharged (`cost == baseCost`) and `indexHints` is empty.
   - Also assert `orders_aggregate(groupBy: [customer_id])` on indexed `customer_id` is NOT surcharged and `indexHints` is empty.
3. `Aggregation_with_unresolved_variable_in_where_or_groupBy_fails_closed_with_surcharge` (`[Trait("Category", "Security")]`):
   - `query($g: [OrdersGroupByField!]) { orders_aggregate(groupBy: $g) { aggregates { count } } }` with empty/null `variableValues` -> surcharged (`SEC-53-17`) and `HelpfulIndexColumns` is empty (`[]`).
4. `Index_hint_omits_columns_when_any_is_denied` (`[Trait("Category", "Security")]`):
   - Table `orders` with visible column `status` and denied column `secret_margin` (`visibleColumns = {"id", "customer_id", "status"}`).
   - Query `orders_aggregate(where: { status: { eq: "OPEN" }, secret_margin: { gt: 10 } }, groupBy: [status])` -> surcharged, `indexHints` has 1 entry with `Reason = "NO_USABLE_INDEX"` and `HelpfulIndexColumns` MUST be empty (`[]`), never leaking `secret_margin` or partial column lists (`SEC-53-19`).
   - Same assertion for a root list `orders(first: 10, where: { status: { eq: "OPEN" }, secret_margin: { gt: 10 } })`.
5. `Hint_absent_when_index_usable` (`[Trait("Category", "Security")]`):
   - Indexed root list `orders(first: 10, where: { customer_id: { eq: 1 } })` and indexed aggregation `orders_aggregate(where: { customer_id: { eq: 1 } })` -> `indexHints` is empty and `AttachIndexHintsToResult` does not add `extensions.autheris`.
6. `Hint_never_contains_filter_literal_values` (`[Trait("Category", "Security")]`):
   - Query with sensitive filter literal `where: { status: { eq: "TOP_SECRET_LITERAL_999" } }` -> serialized `indexHints` JSON does NOT contain `"TOP_SECRET_LITERAL_999"` (`SEC-53-20`).

### 1.5 Local Verification Commands
```bash
MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~AggregationIndexPricingTests|FullyQualifiedName~AggregateCostAnalysisTests|FullyQualifiedName~IndexUsabilityEvaluatorTests|FullyQualifiedName~QueryCostAnalyzerTests" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~DocsIntegrityTests|FullyQualifiedName~HotPathAllocationRatchetTests" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release -p:AutherisIncludeTestAuth=true --no-dependencies -m:1 -nodeReuse:false
```

---

## Brief 2: `WP-OI-CAT-7` — Federation Explain Subtree Eligibility Parity (`#612`)

- **Worktree:** `/work/wt/oi-cat-612-explain-subtrees`
- **Branch:** `feat/oi-cat-612-explain-subtrees`
- **Issue:** `#612`
- **Plan & Phase 3 References:**
  - `docs/plans/2026-11-13-plan-issues-catalog-consent-correctness.md` (§8 `WP-OI-CAT-7` — Issue `#612`, lines 281–312)
  - `docs/plans/2026-11-13-sec-issues-group-3.md` (§7 `WP-OI-CAT-7` — Issue `#612`, `SEC-OI-612-1`, `SEC-OI-612-2`, lines 239–268)

### 2.1 Context & Current State on `main` (`2d6715a0`)
- In execution (`src/Autheris.Application/Sql/Services/CrossSourcePlanner.cs:266-268`), federation subtree partitioning calls:
  ```csharp
  SubtreeGraphPartitioner.Partition(
      statement,
      tables,
      options.MaxTablesPerSubtree,
      t => SubtreeCompiler.IsPushdownEligible(t, RequireDecision(decisions, t)));
  ```
- In explain/analysis (`src/Autheris.Application/Governance/Analysis/QueryAnalysisService.Sql.cs:357`), `AnalyzeSqlQueryAsync` currently calls:
  ```csharp
  var partitionResult = SubtreeGraphPartitioner.PartitionSubtrees(parsedStatement, resolvedTables);
  ```
  which passes `maxTablesPerSubtree = DefaultMaxTablesPerSubtree` and `isPushdownEligible = null`, so explain claims tables with row filters, masked columns, or denied access are grouped into multi-table pushdown subtrees even though `CrossSourcePlanner` rejects them at execution time.
- In `src/Autheris.Application/Governance/Analysis/QueryAnalysisService.Evaluation.cs`, `EvaluateTableAndColumnsAsync` (lines 18–196) already evaluates table access (lines 46–83), resolves per-column levels via `ResolveTableColumnLevels` (cached in `scopeContext.LevelCache[table.Identifier]`, line 105), and computes `ResolveEffectiveColumnAccess(col, columnLevels, isOwner, isAdmin)` for every column (line 127).

### 2.2 Files to Create / Modify
- **Modify:**
  - `src/Autheris.Application/Governance/Analysis/QueryAnalysisService.cs` (`AnalysisScopeContext`, lines 77–91)
  - `src/Autheris.Application/Governance/Analysis/QueryAnalysisService.Evaluation.cs` (`EvaluateTableAndColumnsAsync`)
  - `src/Autheris.Application/Governance/Analysis/QueryAnalysisService.Sql.cs` (`AnalyzeSqlQueryAsync`, line 357)
- **Create / Modify Tests:**
  - `tests/Autheris.Tests.Unit/Governance/QueryAnalysisSubtreeEligibilityTests.cs` (new or extend `QueryAnalysisServiceTests.cs`)

### 2.3 Exact Implementation Requirements & Binding Security Conditions (`SEC-OI-612-1`, `SEC-OI-612-2`)

1. **Reuse Decisions Already Computed in `AnalysisScopeContext` (`SEC-OI-612-2`):**
   - Do **NOT** issue a second `ITableAccessPolicy` or `IGovernanceDecisionPoint` call per table during subtree partitioning.
   - On `AnalysisScopeContext` (`src/Autheris.Application/Governance/Analysis/QueryAnalysisService.cs:77-91`), add:
     ```csharp
     public Dictionary<string, TableAccessDecision> TableDecisions { get; } = new(StringComparer.OrdinalIgnoreCase);
     ```
   - Inside `EvaluateTableAndColumnsAsync` (`src/Autheris.Application/Governance/Analysis/QueryAnalysisService.Evaluation.cs`), after column evaluation completes for a table that passed the table-visibility and table-grant check (`hasTableGrant || isOwner || isAdmin` at line 62; Note: if `!hasTableGrant && !isOwner && !isAdmin`, do NOT record an allowed `TableAccessDecision` — leave it absent or record `TableAccessDecision.Denied(...)`):
     - Build the full `effectiveColumnLevels` dictionary across all `table.Columns` using `ResolveEffectiveColumnAccess(col, columnLevels, isOwner, isAdmin)` (note: `columnLevels` is already resolved at line 105; compute the map over all `table.Columns` so even columns not explicitly projected in `SELECT` are checked for `SubtreeCompiler.IsPushdownEligible`, matching `CrossSourcePlanner` which evaluates table-wide `HasUnconstrainedColumnAllow` and `ColumnAccessLevels`).
     - Set `bool hasUnconstrainedColumnAllow = table.Columns.All(c => effectiveTableColumnLevels.TryGetValue(c.Name, out var lvl) && lvl == ColumnAccessLevel.Clear);`
     - Store `scopeContext.TableDecisions[table.Identifier] = TableAccessDecision.Allowed(grants: Array.Empty<DecisionGrantRef>(), columnLevels: effectiveTableColumnLevels, hasUnconstrainedColumnAllow: hasUnconstrainedColumnAllow);` (note: row filter eligibility is already checked by `SubtreeCompiler.IsPushdownEligible(table, decision)` via `table.RowFilterExpressions` and `table.VirtualFilterDefinitions`, and column masking/deny eligibility is checked via `decision.HasUnconstrainedColumnAllow` and `decision.ColumnAccessLevels`!).
     - Also check if the caller has any consent-based or profile-based row filters (`rowFilters` in `EvaluateTableAndColumnsAsync`, line 174): if `rowFilters.Count > 0` (from active consents/profiles), pass `rowScopes: [new EvaluatedRowScope( PolicyName: "__consent_row_filter", SqlPredicate: rowFilters[0].FilterExpression, Parameters: new Dictionary<string, object?>(), UncoveredRowBehavior: UncoveredRowBehavior.Deny, GrantId: "analysis")]` on `TableAccessDecision.Allowed(...)` so `decision.HasRowPolicies` is `true` and `SubtreeCompiler.IsPushdownEligible` returns `false` whenever consent/profile row filters apply!

2. **Fail-Closed `TryIsPushdownEligible` Wrapper in `QueryAnalysisService.Sql.cs` (`SEC-OI-612-1`):**
   - At `src/Autheris.Application/Governance/Analysis/QueryAnalysisService.Sql.cs:357`, replace:
     ```csharp
     var partitionResult = SubtreeGraphPartitioner.PartitionSubtrees(parsedStatement, resolvedTables);
     ```
     with:
     ```csharp
     int maxTablesPerSubtree = _gatewayOptions.WebSql?.CrossSource?.MaxTablesPerSubtree
         ?? SubtreeGraphPartitioner.DefaultMaxTablesPerSubtree;
     var partitionResult = SubtreeGraphPartitioner.PartitionSubtrees(
         parsedStatement,
         resolvedTables,
         maxTablesPerSubtree,
         t => TryIsPushdownEligible(t, scopeContext));
     ```
   - Implement `TryIsPushdownEligible(TableMetadata table, AnalysisScopeContext scopeContext)`:
     ```csharp
     private static bool TryIsPushdownEligible(TableMetadata table, AnalysisScopeContext scopeContext)
     {
         try
         {
             if (table is null || string.IsNullOrWhiteSpace(table.Identifier))
             {
                 return false;
             }

             if (!scopeContext.TableDecisions.TryGetValue(table.Identifier, out var decision) || decision is null)
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
   - Keep `AnalyzedSubtree` response DTO unchanged (`C-6`).

### 2.4 Required Unit Tests (`tests/Autheris.Tests.Unit/Governance/QueryAnalysisSubtreeEligibilityTests.cs`)
Mark security tests with `[Trait("Category", "Security")]`:
1. `Explain_SubtreePartitioning_MatchesExecution_WhenTableHasRowFilter_OrMasking`:
   - Same-source join between `db.hr.employees` and `db.hr.departments` where `employees` has a masked column or row filter -> `AnalyzeSqlQueryAsync` produces single-table fallback subtrees (`IsSingleTableFallback == true`, not merged into a 2-table pushdown subtree), matching `CrossSourcePlanner`.
2. `Explain_SubtreePartitioning_MergesSameSourceTables_WhenBothAreUnconstrainedClear`:
   - Same-source join between two unconstrained `Clear` tables on the same non-federated source -> merged into a single pushdown subtree (`Tables.Count == 2`, `IsSingleTableFallback == false`).
3. `Explain_SubtreePartitioning_DeniedOrMissingDecision_ReturnsNotPushdownEligible` (`[Trait("Category", "Security")]`, `SEC-OI-612-1`):
   - When one table in a same-source join is denied (no grant) or throws/lacks a decision in `scopeContext.TableDecisions`, `TryIsPushdownEligible` returns `false` without throwing and never merges the denied table into a multi-table pushdown subtree.
4. `Explain_SubtreePartitioning_DoesNotInvokeDuplicatePolicyEvaluation` (`[Trait("Category", "Security")]`, `SEC-OI-612-2`):
   - Assert via mock/counting `IGovernanceRepository` / `ITableAccessPolicy` that partitioning does not perform extra per-table decision calls beyond `EvaluateTableAndColumnsAsync`.

### 2.5 Local Verification Commands
```bash
MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~QueryAnalysisSubtreeEligibilityTests|FullyQualifiedName~QueryAnalysisServiceTests|FullyQualifiedName~SubtreeGraphPartitionerTests" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~DocsIntegrityTests" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release -p:AutherisIncludeTestAuth=true --no-dependencies -m:1 -nodeReuse:false
```

---

## Brief 3: `WP-OI-CAT-3` — Consent Revoke Authority Enforcement & Dead `ROLE_MEMBERS` Removal (`#670`)

- **Worktree:** `/work/wt/oi-cat-670-revoke-auth`
- **Branch:** `feat/oi-cat-670-revoke-auth`
- **Issue:** `#670`
- **Sequencing Note:** Sequence right after `#667` (`WP-OI-CAT-2`) merges on `main` (or coordinate with `DeclarativeConsentApplyService.cs` so both `UpsertConsentAsync` and `RevokeConsentAsync` pass the same `ConsentMutationAuthority` built from `IGatewayRoleEvaluator` + `ICurrentTenantProvider`).
- **Plan & Phase 3 References:**
  - `docs/plans/2026-11-13-plan-issues-catalog-consent-correctness.md` (§4 `WP-OI-CAT-3` — Issue `#670`, lines 134–169)
  - `docs/plans/2026-11-13-sec-issues-group-3.md` (§3 `WP-OI-CAT-3` — Issue `#670`, `SEC-OI-670-1` .. `SEC-OI-670-6`, lines 98–153)
  - `docs/plans/2026-11-14-owner-decisions-sec-groups-1-9.md` (`Q-SEC3-1` confirmed: option (a) grantee self-revocation permitted only when caller is the exact direct grantee `grantee_type IN ('User', 'ServicePrincipal')` and normalized `grantee_id == callerSid`).

### 3.1 Context & Current State on `main` (`2d6715a0`)
1. `src/Autheris.Infrastructure/Persistence/GovernanceRepository.ConsentGrants.cs:261-274` (`RevokeConsentInTransactionAsync`):
   - Takes `(IDbConnection conn, IDbTransaction tx, string consentId, string revokedBy, string reason, CancellationToken ct)` with `bool isAdmin = false` missing or unused — it executes `UPDATE ... SET status = 'Revoked'` without loading the consent row, without verifying tenant isolation, and without checking `IsAuthorizedApproverForTableInternalAsync`!
2. `src/Autheris.Infrastructure/Persistence/GovernanceRepository.ConsentGrants.cs:306-359` (`IsAuthorizedApproverForTableInternalAsync`):
   - Line 307 returns `true` whenever `isAdmin` is passed as `true` by the caller (`Mutation.Consent.cs:859` passes `IsAdmin(principal)` which includes global roles regardless of tenant scope unless gated).
   - Lines 339–349 execute a live SQL query against `ROLE_MEMBERS` (`SELECT role_name FROM ROLE_MEMBERS WHERE member_id = @UserId`), which is dead legacy state retired by `OD-30` / `WP-SIMPL-46 PR 3` (`0011_role_grantee_and_profile_assignment_retirement`).
3. Dead `ROLE_MEMBERS` seed & helper methods still in `src/`:
   - `src/Autheris.Infrastructure/Persistence/GovernanceRepository.Schema.cs:413` (`SELECT COUNT(*) FROM ROLES`) and `:616` (`INSERT INTO ROLE_MEMBERS`).
   - `src/Autheris.Infrastructure/Persistence/GovernanceRepository.Catalog.cs:891` (`GetTransitiveRolesAsync`) and `src/Autheris.Infrastructure/Persistence/Internal/CatalogRows.cs:35, 550` (`RoleEdgeRow` / `ROLE_MEMBERS` query).

### 3.2 Files to Modify / Create
- **Create (if `#667` has not yet landed `ConsentMutationAuthority`):**
  - `src/Autheris.Domain/Model/Governance/ConsentMutationAuthority.cs`:
    ```csharp
    namespace Autheris.Domain.Model.Governance;

    /// <summary>
    /// Represents the server-verified caller authority for consent mutations (#667, #670).
    /// </summary>
    public sealed record ConsentMutationAuthority(
        string CallerSid,
        string? CallerTenantId,
        bool IsClusterAdmin = false,
        bool IsTenantGovernanceAdmin = false);
    ```
- **Modify:**
  - `src/Autheris.Domain/Interfaces/IGovernanceRepository.cs` (`RevokeConsentAsync`, `IsAuthorizedApproverForTableAsync`)
  - `src/Autheris.Infrastructure/Persistence/GovernanceRepository.ConsentGrants.cs` (`RevokeConsentAsync`, `RevokeConsentInTransactionAsync`, `IsAuthorizedApproverForTableAsync`, `IsAuthorizedApproverForTableInternalAsync`)
  - `src/Autheris.Infrastructure/Persistence/GovernanceRepository.Schema.cs` (remove `ROLE_MEMBERS` / `ROLES` seed inserts; keep DDL table creation untouched until migration drop step `WP-SIMPL-52 PR 6b`)
  - `src/Autheris.Infrastructure/Persistence/GovernanceRepository.Catalog.cs` & `src/Autheris.Infrastructure/Persistence/Internal/CatalogRows.cs` (delete dead `GetTransitiveRolesAsync` and `RoleEdgeRow`)
  - `src/Autheris.Application/Identity/Attributes/AttributeValidatingConsentRepository.cs` (forward updated `RevokeConsentAsync` / `IsAuthorizedApproverForTableAsync` signatures)
  - `src/Autheris.Application/Governance/Services/DeclarativeConsentApplyService.cs` (pass caller `ConsentMutationAuthority` into `RevokeConsentAsync`)
  - `src/Autheris.GraphQL/Types/Mutation.Consent.cs` (pass `ConsentMutationAuthority` into `RevokeConsentAsync`)
- **Create / Modify Tests:**
  - `tests/Autheris.Tests.Unit/Governance/ConsentRevokeAuthorityTests.cs`

### 3.3 Exact Implementation Requirements & Binding Security Conditions (`SEC-OI-670-1` .. `SEC-OI-670-6`, `Q-SEC3-1`)

1. **In-Transaction Load & Tenant Isolation Before Revoke (`SEC-OI-670-1`, `SEC-OI-670-2`):**
   - In `RevokeConsentInTransactionAsync`:
     - Reject null/whitespace `authority.CallerSid` (or `revokedBy`) with `UnauthorizedAccessException` (`SEC-OI-670-5`: `system` or empty identity is NEVER treated as an implicit admin bypass).
     - Query the target consent row inside `(conn, tx)` (`SELECT id, table_id, tenant_id, grantee_type, grantee_id, status FROM CONSENTS WHERE id = @Id`). If not found, throw `KeyNotFoundException($"Consent '{consentId}' was not found.")`.
     - **Tenant Isolation (`SEC-OI-670-2`):** Unless `authority.IsClusterAdmin` is `true`, verify that the consent's `tenant_id` matches `authority.CallerTenantId` (case-insensitive ordinal). If the consent row has a `tenant_id` that does not match `authority.CallerTenantId` (or if `authority.CallerTenantId` is null/empty when the consent has a `tenant_id`), throw `UnauthorizedAccessException` — even if `authority.IsTenantGovernanceAdmin` is `true`!

2. **Three-Way Revoke Authority Check (`SEC-OI-670-1`, `SEC-OI-670-2`, `SEC-OI-670-3`, `Q-SEC3-1`):**
   - A caller in the same tenant (or `IsClusterAdmin`) is authorized to revoke consent `c` if and only if at least one of the following holds:
     1. **Admin Authority (`SEC-OI-670-2`):** `authority.IsClusterAdmin` is `true`, OR (`authority.IsTenantGovernanceAdmin` is `true` AND `string.Equals(consent.TenantId, authority.CallerTenantId, StringComparison.OrdinalIgnoreCase)`).
     2. **Table Owner / Active Delegate Authority (`SEC-OI-670-1`):** `await IsAuthorizedApproverForTableInternalAsync(conn, tx, consent.TableId, authority.CallerSid, isAdmin: false, ct)` returns `true` (active `DATA_OWNERS` for `consent.TableId` using normalized SID comparison, or active unexpired `DATA_OWNER_DELEGATIONS` where ` valid_from <= @Now AND valid_until > @Now`).
     3. **Direct Grantee Self-Revocation (`Q-SEC3-1`, `SEC-OI-670-3`):**
        - `consent.GranteeType` is `"User"` or `"ServicePrincipal"` (case-insensitive) — **NEVER** `"Group"` or `"Role"` (`SEC-OI-670-3`), AND
        - `NormalizeIdentity(consent.GranteeId) == NormalizeIdentity(authority.CallerSid)`.
   - If none of (1), (2), or (3) holds, throw `UnauthorizedAccessException` before executing `UPDATE CONSENTS`.
   - Scope the `UPDATE CONSENTS SET status = 'Revoked', ... WHERE id = @Id AND tenant_id = @TenantId` (when `consent.TenantId` is non-null).

3. **Remove `ROLE_MEMBERS` from `IsAuthorizedApproverForTableInternalAsync` & Seed/Catalog Code (`SEC-OI-670-4`):**
   - In `GovernanceRepository.ConsentGrants.cs:339-349`, delete the `SELECT role_name FROM ROLE_MEMBERS WHERE member_id = @UserId` query and the `approverRoles.Overlaps(userRoles)` check. `IsAuthorizedApproverForTableInternalAsync` checks ONLY:
     - `isAdmin` (when caller verified admin authority for the target tenant),
     - Direct active `DATA_OWNERS` (`user_id` normalized match on `table_id`),
     - Active unexpired `DATA_OWNER_DELEGATIONS` (`delegate_user_id` normalized match for an active owner of `table_id` with `is_active = 1 AND valid_from <= @Now AND valid_until > @Now`).
   - Delete `GetTransitiveRolesAsync` from `GovernanceRepository.Catalog.cs:891` and `RoleEdgeRow` / `SelectAllRoleEdgesSql` from `CatalogRows.cs:35, 550`.
   - Remove the `ROLES` / `ROLE_MEMBERS` seed inserts in `GovernanceRepository.Schema.cs:413-625` (while keeping the `CREATE TABLE` DDL untouched so existing schema shape tests pass until `WP-SIMPL-52 PR 6b`).

4. **Defense-in-Depth in `Mutation.Consent.cs` (`SEC-OI-670-6`):**
   - Keep the pre-check in `Mutation.Consent.cs` (`RevokeConsentAsync`), updated to allow direct `User`/`ServicePrincipal` self-revocation (`Q-SEC3-1`) or table approver/admin, AND pass the constructed `ConsentMutationAuthority` down to `_repository.RevokeConsentAsync(...)` so both GraphQL and `DeclarativeConsentApplyService` enforce the exact same repository-level transaction check.

### 3.4 Required Unit Tests (`tests/Autheris.Tests.Unit/Governance/ConsentRevokeAuthorityTests.cs`)
Mark all security tests with `[Trait("Category", "Security")]`:
1. `RevokeConsent_UnauthorizedCaller_ThrowsUnauthorizedAccessException` (`[Trait("Category", "Security")]`, `SEC-OI-670-1`):
   - Caller is neither admin, table owner/delegate, nor grantee -> throws `UnauthorizedAccessException` and does not execute `UPDATE CONSENTS`.
2. `RevokeConsent_TableOwnerAndActiveDelegate_Succeeds` (`[Trait("Category", "Security")]`, `SEC-OI-670-1`):
   - Direct `DATA_OWNERS` match and active unexpired `DATA_OWNER_DELEGATIONS` match both succeed.
   - Expired delegation (`valid_until <= now`) throws `UnauthorizedAccessException`.
3. `RevokeConsent_CrossTenantGovernanceAdmin_ThrowsUnauthorizedAccessException` (`[Trait("Category", "Security")]`, `SEC-OI-670-2`):
   - Caller with `IsTenantGovernanceAdmin = true` for `tenant-a` attempting to revoke a consent in `tenant-b` throws `UnauthorizedAccessException`.
   - `IsClusterAdmin = true` succeeds across tenants.
4. `RevokeConsent_DirectUserOrSpGranteeSelfRevoke_Succeeds_ButGroupGranteeMember_Throws` (`[Trait("Category", "Security")]`, `SEC-OI-670-3`, `Q-SEC3-1`):
   - Consent with `grantee_type = 'User'` and `grantee_id = 'user-1'` revoked by `user-1` in the same tenant succeeds.
   - Consent with `grantee_type = 'Group'` and `grantee_id = 'group-1'` revoked by a non-owner member of `group-1` throws `UnauthorizedAccessException`.
5. `IsAuthorizedApproverForTable_RoleMembersRow_DoesNotGrantAuthority` (`[Trait("Category", "Security")]`, `SEC-OI-670-4`):
   - Even if `DATA_OWNERS.approver_roles` is populated, `IsAuthorizedApproverForTableInternalAsync` does not query `ROLE_MEMBERS` and returns `false` for non-owner/non-delegate callers.
6. `DeclarativeConsentApply_RevokeWithoutOwnerOrAdminAuthority_FailsClosed` (`[Trait("Category", "Security")]`, `SEC-OI-670-5`):
   - `DeclarativeConsentApplyService` prune/revoke path with a non-owner, non-admin caller identity throws `UnauthorizedAccessException` (never bypasses via `"system"`).

### 3.5 Local Verification Commands
```bash
MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~ConsentRevokeAuthorityTests|FullyQualifiedName~DeclarativeConsentApply" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~DocsIntegrityTests" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release -p:AutherisIncludeTestAuth=true --no-dependencies -m:1 -nodeReuse:false
```

---

## Brief 4: `WP-OI-SQL-478` — Flat Same-Precedence Operator Chains & Bounded-Stack Evaluation (`#478`)

- **Issue:** `#478`
- **Plan & Phase 3 References:**
  - `docs/plans/2026-11-13-plan-issues-sql-engine-dialects.md` (§1 `WP-OI-SQL-478-A` and `WP-OI-SQL-478-B`, lines 19–62)
  - `docs/plans/2026-11-13-sec-issues-group-5.md` (§1 `WP-OI-SQL-478-A` & `WP-OI-SQL-478-B`, `SEC-OI-478-1` .. `SEC-OI-478-5`, lines 23–67)
  - `docs/plans/2026-11-14-owner-decisions-sec-groups-1-9.md` (`Q-SEC5-1` confirmed: enforce `MaxOperatorChainLength = 1000` in SQL AST builder and `MaxBooleanChainTerms = 256` in OData filter parser).
- **Slicing:** Per the approved Phase 2 plan, `#478` is executed as two small, independent PRs (`478-A` for `TrinoSqlEngine` and `478-B` for `ODataFilterParser`), or sequentially on separate branches. Both sub-briefs are specified below.

---

### Brief 4A: `WP-OI-SQL-478-A` — `TrinoSqlEngine` Iterative Operator-Chain Folding & Downstream Walker Stack Safety (`#478`)

- **Worktree:** `/work/wt/oi-sql-478a-trino-chains`
- **Branch:** `feat/oi-sql-478a-trino-chains`

#### 4A.1 Context & Current State on `main` (`2d6715a0`)
- `src/TrinoSqlEngine/Ast/Builder/SqlAstBuilder.cs` increments `_currentDepth` via `using var _ = EnterScope();` (line 29, max `_options.MaxAllowedAstDepth = 64`) on every binary operator node in a left-associative ANTLR chain:
  - `VisitAnd(TrinoSqlParser.AndContext context)` (lines 868–874)
  - `VisitOr(TrinoSqlParser.OrContext context)` (lines 876–882)
  - `VisitArithmeticBinary(TrinoSqlParser.ArithmeticBinaryContext context)` (lines 1003–1016)
  - `VisitConcatenation(TrinoSqlParser.ConcatenationContext context)` (lines 1131–1137)
- A flat `WHERE c = 1 OR c = 2 OR ... OR c = 100` (100 terms) recurses 100 levels deep in `VisitOr` and throws `SqlAstDepthExceededException` at term 65.
- Downstream walkers: `SqlDialectGeneratorBase.Expressions.cs:20`, `SqlAstRewriter.cs:29`, `AstReflection.cs:22`, and `SecurityCoverageVerifier.cs:54` already call `ExecutionStackGuard.Ensure()` (`RuntimeHelpers.EnsureSufficientExecutionStack()`), or we must ensure left-deep binary chains in `AstSecurityVisitor`, `SqlDialectGeneratorBase.VisitBinaryExpression`, `SqlAstRewriter.RewriteBinary`, and federation conjunct splitters (`SingleTableConjunctClassifier`, `SqlConjunctSplitter`) either iterate flat chains or guard stack depth so a 1,000-term chain runs cleanly on a 512 KiB thread stack (`SEC-OI-478-2`).

#### 4A.2 Files to Modify / Create
- **Modify:**
  - `src/TrinoSqlEngine/Options/SqlEngineOptions.cs` (add `public int MaxOperatorChainLength { get; set; } = 1000;` validated in range `1..5000`)
  - `src/TrinoSqlEngine/Ast/Builder/SqlAstBuilder.cs` (`VisitAnd`, `VisitOr`, `VisitArithmeticBinary`, `VisitConcatenation`)
  - `src/TrinoSqlEngine/Security/AstSecurityVisitor.cs` / `src/TrinoSqlEngine/Rewriter/SqlAstRewriter.cs` / `src/TrinoSqlEngine/Dialects/SqlDialectGeneratorBase.Expressions.cs` (iteratively unroll left-spine `SqlBinaryExpression` chains in `VisitBinaryExpression` / `RewriteBinary` so a 1,000-term left-deep tree uses $O(1)$ call-stack frames for the spine!)
- **Create Tests:**
  - `tests/TrinoSqlEngine.Tests/SqlOperatorChainDepthTests.cs`

#### 4A.3 Exact Implementation Requirements & Binding Security Conditions (`SEC-OI-478-1` .. `SEC-OI-478-5`, `Q-SEC5-1`)

1. **Iterative Spine Unrolling in `SqlAstBuilder.cs` (`SEC-OI-478-1`, `SEC-OI-478-3`, `SEC-OI-478-4`):**
   - Enter **one** `using var _ = EnterScope();` for an entire flat same-family chain in:
     - `VisitAnd`: while `current is TrinoSqlParser.AndContext andCtx`, push `(andCtx.right, "AND")` onto a `ValueListBuilder` / `List`, advance `current = andCtx.left`. Enforce `chainCount <= _options.MaxOperatorChainLength` (default `1000`; throw `SqlAstDepthExceededException` when exceeded, `SEC-OI-478-3`). Visit the leftmost operand and each right operand (each operand visit enters its own scope for nested parens/subqueries/functions, so nested `( ( ( ... ) ) )` still increments `_currentDepth` per level, `SEC-OI-478-4`), then fold left-to-right into `new SqlBinaryExpression(acc, op, rightOperand)` preserving exact left-associative AST shape.
     - `VisitOr`: same iterative spine unrolling for `TrinoSqlParser.OrContext`.
     - `VisitArithmeticBinary`: iterative spine unrolling across contiguous same-precedence `TrinoSqlParser.ArithmeticBinaryContext` nodes (`+`/`-` together, or `*`/`/`/`%` together), preserving left-to-right associativity and `MaxOperatorChainLength`.
     - `VisitConcatenation`: iterative spine unrolling across contiguous `TrinoSqlParser.ConcatenationContext` (`||`) nodes.
   - **Critical (`SEC-OI-478-4`):** `ParenthesizedExpression` (`VisitParenthesizedExpression`), subqueries, CASE expressions, and function calls MUST continue calling `EnterScope()` on every nesting level so 65 nested parentheses `((((...))))` or 65 nested `CASE WHEN (CASE WHEN ...)` still throw `SqlAstDepthExceededException`.

2. **Iterative Left-Spine Traversal in Downstream AST Walkers (`SEC-OI-478-2`, `SEC-OI-478-5`):**
   - A 1,000-term left-associative `SqlBinaryExpression` has height 1,000 along its `.Left` spine. If `AstSecurityVisitor.VisitBinaryExpression`, `SqlAstRewriter.RewriteBinary`, or `SqlDialectGeneratorBase.VisitBinaryExpression` recurses on `node.Left` 1,000 times on a 512 KiB thread stack, it will either overflow the stack or hit `InsufficientExecutionStackException`!
   - Therefore, in:
     - `AstSecurityVisitor` (`VisitBinaryExpression` in `src/TrinoSqlEngine/Security/AstSecurityVisitor.cs`),
     - `SqlAstRewriter` (`RewriteBinary` in `src/TrinoSqlEngine/Rewriter/SqlAstRewriter.cs`), and
     - `SqlDialectGeneratorBase` (`VisitBinaryExpression` in `src/TrinoSqlEngine/Dialects/SqlDialectGeneratorBase.Expressions.cs`),
     unroll the `.Left` spine of `SqlBinaryExpression` iteratively using an explicit `Stack<SqlBinaryExpression>` (visiting the leftmost leaf first, then iterating down the stack to visit each `.Right` child and recombine/emit), AND keep `ExecutionStackGuard.Ensure()` at the entry!
   - Also check `SqlConjunctSplitter` / `SingleTableConjunctClassifier` if used on `AND`/`OR` trees: if recursive on `.Left`, unroll `.Left` iteratively with an explicit stack as well.
   - This guarantees every single node in the 1,000-term chain is visited by `AstSecurityVisitor` (zero short-circuiting of column masking/deny guards at term 1,000, `SEC-OI-478-5`) while using only 1 C# stack frame for the entire 1,000-node spine (`SEC-OI-478-2`)!

#### 4A.4 Required Unit Tests (`tests/TrinoSqlEngine.Tests/SqlOperatorChainDepthTests.cs`)
Mark security tests with `[Trait("Category", "Security")]`:
1. `FlatOrAndArithmeticConcatChain_200Terms_SucceedsAndPreservesLeftAssociativity`:
   - 200-term `OR` chain, 200-term `AND` chain, 200-term `+` chain, and 200-term `||` chain parse, validate, rewrite, and generate SQL across PostgreSQL and SQL Server dialects without throwing.
2. `FullPipeline_1000TermBooleanChain_SucceedsOn512KiBStack` (`[Trait("Category", "Security")]`, `SEC-OI-478-2`):
   - Spawn a thread with `new Thread(..., maxStackSize: 512 * 1024)` running full parse -> `AstSecurityVisitor` -> `SqlAstRewriter` -> dialect SQL generation on a 1,000-term `AND`/`OR` chain -> completes without `StackOverflowException` or `InsufficientExecutionStackException`.
3. `FlatChain_1001Terms_ExceedingMaxOperatorChainLength_ThrowsSqlAstDepthExceededException` (`[Trait("Category", "Security")]`, `SEC-OI-478-3`, `Q-SEC5-1`):
   - 1,001-term `OR` chain (with default `MaxOperatorChainLength = 1000`) throws `SqlAstDepthExceededException`.
4. `DeeplyNestedParentheses_65Levels_StillThrowsSqlAstDepthExceededException` (`[Trait("Category", "Security")]`, `SEC-OI-478-4`):
   - `SELECT ((((... 65 levels ...)))) FROM t` throws `SqlAstDepthExceededException`.
5. `MaskedOrDeniedColumn_At1000thTermOfFlatChain_IsRejectedBySecurityVisitor` (`[Trait("Category", "Security")]`, `SEC-OI-478-5`):
   - 1,000-term `OR` chain where terms 1..999 reference clear column `id` and term 1,000 references masked column `ssn` (or denied column `secret`) -> `AstSecurityVisitor` throws `MaskedColumnPredicateException` (or unauthorized column error).

#### 4A.5 Local Verification Commands for 4A
```bash
MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/TrinoSqlEngine.Tests/TrinoSqlEngine.Tests.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~SqlOperatorChainDepthTests|FullyQualifiedName~AstSecurityVisitor" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~DocsIntegrityTests" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s
```

---

### Brief 4B: `WP-OI-SQL-478-B` — `ODataFilterParser` Flat `and`/`or` Chain Handling (`#478`)

- **Worktree:** `/work/wt/oi-sql-478b-odata-chains`
- **Branch:** `feat/oi-sql-478b-odata-chains`

#### 4B.1 Context & Current State on `main` (`2d6715a0`)
- In `src/Autheris.Application/OData/ODataFilterParser.cs`:
  - `MaxRecursionDepth = 32` (line 19).
  - `ParseOr` (lines 410–428) and `ParseAnd` (lines 430–448) are already `while` loops during parsing, creating a left-deep `BinaryOpNode(left, op, right)` tree!
  - **Why flat chains currently fail in `ODataFilterParser.cs`:** `BinaryOpNode.ToSql(SqlGenerationContext context)` (lines 714–724) calls `using var _ = context.EnterDepth();` on every `BinaryOpNode` visit, and recurses `Left.ToSql(context)`. Thus a 33-term flat `a eq 1 or b eq 2 or ...` chain throws `FormatException($"$filter expression exceeds maximum nesting depth of {MaxRecursionDepth}.")` inside `BinaryOpNode.ToSql` at `SqlGenerationContext.EnterDepth()` (line 602)!

#### 4B.2 Files to Modify / Create
- **Modify:**
  - `src/Autheris.Application/OData/ODataFilterParser.cs`
- **Modify / Create Tests:**
  - `tests/Autheris.Tests.Unit/OData/ODataFilterParserTests.cs`

#### 4B.3 Exact Implementation Requirements & Binding Security Conditions (`SEC-OI-478-1` .. `SEC-OI-478-5`, `Q-SEC5-1`)

1. **Enforce `MaxBooleanChainTerms = 256` & Iterative `BinaryOpNode.ToSql` (`Q-SEC5-1`, `SEC-OI-478-1` .. `SEC-OI-478-5`):**
   - In `ODataFilterParser.cs`, add `public const int MaxBooleanChainTerms = 256;` (`Q-SEC5-1`).
   - In `ParseOr` (line 410) and `ParseAnd` (line 430), track the number of terms in the flat chain (`int termCount = 1;`). When `termCount > MaxBooleanChainTerms`, throw `FormatException($"$filter boolean chain exceeds maximum length of {MaxBooleanChainTerms} terms.")` (`SEC-OI-478-3`).
   - In `BinaryOpNode.ToSql(SqlGenerationContext context)` (lines 714–724):
     - Enter **one** `using var _ = context.EnterDepth();` for an entire contiguous same-operator (`AND` or `OR`) left-spine chain instead of calling `EnterDepth()` per binary node!
     - Unroll the `.Left` spine of `BinaryOpNode` with the same `Operator` iteratively using a `Stack<FilterNode>` (pushing `current.Right` while `current is BinaryOpNode bin && bin.Operator == Operator`), count the spine terms (enforcing `<= MaxBooleanChainTerms`), evaluate the leftmost leaf `.ToSql(context)`, and then pop each right operand `.ToSql(context)` combining with `({acc} {Operator} {rightSql})` so the generated SQL string is **byte-for-byte identical** to the prior recursive output (`(a = @p0 OR b = @p1)`), while consuming only 1 depth increment on `SqlGenerationContext` and 1 C# stack frame!
   - **Critical (`SEC-OI-478-4`, `SEC-OI-478-5`):**
     - `ParsePrimary` (`TokenType.OpenParen` at lines 473–481) still calls `EnterDepth()` on every `(` so 33 nested parentheses `((((...))))` or 33 nested `not(not(...))` still throw `FormatException` (`SEC-OI-478-4`).
     - Every comparison leaf in the 256-term chain still validates its column against `allowedColumns` and binds its parameter (`@p0`, `@p1`, ...) so an unauthorized/blocked column at term 256 is still rejected (`SEC-OI-478-5`).

#### 4B.4 Required Unit Tests (`tests/Autheris.Tests.Unit/OData/ODataFilterParserTests.cs`)
Mark security tests with `[Trait("Category", "Security")]`:
1. `Parse_FlatOrAndChain_100Terms_SucceedsAndMatchesLeftAssociativeSql`:
   - 100-term flat `or` chain and 100-term flat `and` chain succeed and produce valid parameterized SQL.
2. `Parse_FlatBooleanChain_256Terms_SucceedsOn512KiBStack` (`[Trait("Category", "Security")]`, `SEC-OI-478-2`):
   - 256-term flat `or` chain runs inside a 512 KiB stack thread without stack overflow.
3. `Parse_FlatBooleanChain_257Terms_ThrowsFormatException` (`[Trait("Category", "Security")]`, `SEC-OI-478-3`, `Q-SEC5-1`):
   - 257-term flat `or` or `and` chain throws `FormatException` (`exceeds maximum length of 256 terms`).
4. `Parse_DeeplyNestedParentheses_33Levels_StillThrowsFormatException` (`[Trait("Category", "Security")]`, `SEC-OI-478-4`):
   - 33 nested `((((...))))` throws `FormatException` (`exceeds maximum nesting depth of 32`).
5. `Parse_DisallowedColumnAt256thTerm_ThrowsFormatException` (`[Trait("Category", "Security")]`, `SEC-OI-478-5`):
   - 256-term flat `or` chain where term 256 references a column not in `allowedColumns` throws `FormatException`.

#### 4B.5 Local Verification Commands for 4B
```bash
MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~ODataFilterParserTests" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~DocsIntegrityTests" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s
```
