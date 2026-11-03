# Architectural Implementation Plan: Set-Based "Latest per Parent" Nested Lists and Slow-List Reporting

**Document ID:** `PLAN-LPP-443`
**Date:** 2026-11-03
**GitHub Issue:** #443 (related: #417, #419, #421, #177, #224)
**Input PRD:** [`2026-11-03-req-issue-443-latest-per-parent-set-based.md`](2026-11-03-req-issue-443-latest-per-parent-set-based.md) (REQ-LPP-01..11; REQ-LPP-12 pricing is out of this delivery, OQ-6)
**Phase:** 2 (Solution Architect)
**Context:** Autheris is not in production; breaking internal API changes are allowed. No schema change and no migration (v1.5.0 frozen base untouched).

---

## 1. Verified Components (current `main`, G-PLAN-REBASE)

| Component | File / symbol | Finding |
|---|---|---|
| Nested list compiler | `src/Autheris.Application/Sql/Tree/TreeSqlCompiler.cs` (`Prepare`, `BuildInner` L686, `BuildChild` L860, `BuildRoot` L841, `ObjectPairs` L827) | One statement per root field. Each nested list is a scalar subquery in the parent's JSON projection over a derived table `(SELECT [TOP (n)] … FROM child AS autheris_target WHERE tenant AND (row filter) AND child.k = parent.k AND where ORDER BY …)`, correlated on the parent level alias (`NodePlan.Alias`, `t0`, `t1`, …). |
| Dialects with tree compile | `TreeSqlCompiler.SupportsTreeCompile` L40, `CatalogSchemaModel.cs` L172-200 | Only SQL Server, PostgreSQL, SQLite. Oracle and Databricks tables expose no nested relation fields (G-6), so PRD AC-3 (Oracle) and AC-5 (Databricks) have nothing to compile. **PRD correction (default taken):** they are out of scope; the shared rule (WP-A) is dialect-neutral and is reused if tree compile is added for them later. |
| AST engine | `src/TrinoSqlEngine` | Not on the tree path (tree SQL is emitted directly by `TreeSqlCompiler`); no change. |
| Ordering | `Prepare` L375-410 | Explicit `orderBy` is used as-is, **no PK tie-break is appended** (PRD REQ-LPP-06 wording "PK appended as today" is inaccurate). Default ordering (`DefaultReadOrdering`) applies only when no `orderBy` is given. Order and join columns are forced Clear (`ResolveClearColumn`, SEC-01). |
| Masks / HMAC | `ObjectPairs`, `OutputColumn.LiteralExpression`, `context.HmacColumns` | Masks are literals in the outer projection; HMAC columns are read raw and pseudonymized afterwards by response path. Neither depends on the inner FROM shape. |
| Row policy | `BuildInner` L715-733, `TreeSecuredSource` | Tenant predicate, spliced row filter (or secured derived table) are in the inner WHERE/FROM of every level. |
| Cross-source relations | `GovernedTreeQueryService` L528-650 (`ICrossSourceRelationResolver`) | Already resolved per parent batch outside the statement; unaffected by this plan except measurement (WP-D). |
| Executor | `GovernedTreeQueryService.ExecuteAsync` L225, command L773-800 | Single `ExecuteReaderAsync`, single row. No timing today. |
| Index metadata | `TableMetadata.PrimaryKeyColumns/PrimaryKeyDirections/Indexes/PhysicalIndexes`, `KeyDirections` (`Autheris.Domain/Model/TableModels.cs`) | Sufficient for the rule; no catalog change. |
| Root index evaluator | `src/Autheris.GraphQL/Interceptors/IndexUsabilityEvaluator.cs` (`HasUsableIndex`, `GetCandidateIndexes` L134) | Root lists only; exact direction match only; lives in GraphQL, not reachable from Application. |
| Extensions reporting | `MaskedFieldsMiddleware` (ContextData bag + `opResult.Extensions.SetItem`), `CostAndQuotaMiddleware` L346 (`ClientQuotaPolicy.ExposeCostExtensions`), pipeline `GatewayServiceCollectionExtensions.GraphQL.cs` L190-201 | Pattern to copy. No "diagnostics permission" exists; the closest existing gate is `ExposeCostExtensions` (OQ-5 default mapped to it). |
| Options | `src/Autheris.Domain/Options/Protocols/GraphQlOptions.cs` (`GraphQLOptions`) | New sub-options object added here. |

## 2. Decisions

1. **D-1 Shared index-match rule (REQ-LPP-01/02).** New `src/Autheris.Application/Sql/Indexing/IndexCandidates.cs` takes over `CandidateIndex` and `GetCandidateIndexes` from `IndexUsabilityEvaluator` (moved verbatim; the evaluator delegates, root results unchanged). New `NestedListIndexMatcher.Matches(TableMetadata metadata, IReadOnlyList<string> joinColumns, IReadOnlyCollection<string> equalityColumns, IReadOnlyList<(string Column, bool Descending)> order)`: a candidate matches when (a) its maximal leading run of columns that are in `E = joinColumns ∪ equalityColumns` contains every join column, and (b) the order entries, after dropping columns in `E`, equal the next candidate columns in sequence with directions either all equal or all inverted (backward scan). Empty remaining order matches after (a). No candidates (unknown metadata) means no match (OQ-2). `equalityColumns` are the top-level AND-conjunct `TreeComparison` with `Eq` and a non-null value; OR/NOT branches are ignored. Uses full metadata (the decision is internal; every prefix column is a caller-visible join/filter column).
2. **D-2 Eligibility.** A relation is compiled set-based only when all hold: `IsList`, explicit `orderBy` on the child (PRD scope; default-ordered lists stay correlated, follow-up OQ), parent is the root level (depth 1; deeper levels stay correlated, default taken), not cross-source, D-1 reports no match, the root page order is total (D-4), and no child inner column collides with the reserved names `autheris_k{i}`, `autheris_rn`, `autheris_row`, `autheris_json`, `autheris_page`. Otherwise the correlated form is emitted unchanged. Each relation decides independently (REQ-LPP-08).
3. **D-3 Strategy is ROW_NUMBER only (OQ-1).** `MAX`/seek is not implemented in this delivery; the benchmark in WP-C decides whether a follow-up is worth it.
4. **D-4 Page CTE and totality.** When at least one child is set-based, the root inner query becomes the CTE `autheris_page`, referenced by the root projection and by each child restriction. Because SQL Server inlines a CTE per reference, the page must be deterministic: the root order gets the Clear PK (or Clear unique key) columns not already present appended as tie-break (root inner `ORDER BY` and outer JSON `ORDER BY`). No Clear unique key on the root means fallback to correlated for every child (reason `ParentOrderNotTotal`).
5. **D-5 Child tie-break.** Inside `ROW_NUMBER() OVER (PARTITION BY … ORDER BY …)` the child's Clear PK columns not already in the order are appended, so the winner among ties is deterministic. The correlated form stays unchanged (byte-identical), so on ties the two forms may legally pick different rows; parity tests use tie-free data plus a separate determinism test.
6. **D-6 Uniform three-level shape (all three dialects).** `ranked` (governed child rows restricted to page keys, filters before ranking) → `rows` (`rn <= n`, one JSON object per row) → `k{i}` (aggregate per join key, ordered by `rn`), then `LEFT JOIN k{i}` on the join key in the root projection. Rationale: a GROUP BY plus join is optimizer-robust; a per-parent scalar subquery over a window CTE can be decorrelated with the partition predicate pushed down, which would recreate the per-parent scan. Row JSON is computed one level below the aggregate because SQL Server rejects subqueries inside `STRING_AGG`.
7. **D-7 Fallback (REQ-LPP-07, AC-11).** The set-based builder runs inside a guard that snapshots `CompileContext` (parameter count and alias counter); on any exception it restores the snapshot and emits the correlated form (reason `CompileFailure`). Both forms take their predicates from one extracted method `BuildInnerPredicates(plan, context, parent, relation, correlate)`, so a set-based form without tenant/row filter cannot be emitted. Test-only fault injection: internal `TreeCompileOptions.FailSetBasedFor` (response key), reachable via the existing `InternalsVisibleTo` (Unit, SqlCompilerGate).
8. **D-8 Strategy metadata.** `CompiledTreeQuery` gains `IReadOnlyList<TreeListPlan> Lists` (`Path` = relation response keys, `Strategy` ∈ {`CorrelatedIndexed`, `CorrelatedUnindexed`, `SetBased`}, `Reason`, `Limit`). It is used by WP-D and by a counter `autheris.tree.nested_list.strategy` (tags: dialect, strategy, reason; no table or column names) in `GatewayDiagnostics`.
9. **D-9 Slow-list attribution (REQ-LPP-09/10, OQ-3/4/5).** A statement-level tree query has no per-list segments, so a per-list stopwatch is impossible without a second execution (forbidden by REQ-LPP-11). Default taken: (a) one `Stopwatch` around each tree statement and each cross-source `ResolveRelationAsync` call; (b) ranking: a cross-source list whose measured time is at least half of the request's measured time ranks first; the remaining lists rank by static class `CorrelatedUnindexed` > `SetBased` > `CorrelatedIndexed`, then by parent page size × `Limit` (descending), then by path (ordinal). SQL Server logical-reads enrichment is not implemented (optional per REQ-LPP-10). Output `extensions.slowLists = [{ "path": "devices.readings", "rank": 1 }, …]` (top `MaxEntries`, nested lists only, dot-joined response keys without indices, root field response name first).
10. **D-10 Gating (REQ-LPP-11).** `GraphQLOptions.SlowListReport { Enabled = false, ThresholdMs = 1000 [Range 50..600000], MaxEntries = 3 [Range 1..5] }`. The extension appears only when `Enabled`, request elapsed ≥ `ThresholdMs`, the resolved client policy has `ExposeCostExtensions = true` (stored by `CostAndQuotaMiddleware` in ContextData key `Autheris.ExposeCostExtensions`), and the result is an `OperationResult` without errors. No new error codes; error responses are unchanged.

## 3. Set-Based SQL Shape (child `readings` of root `devices`, `first: 1, orderBy: [{ts: DESC}]`)

SQL Server (`q()` = dialect quoter, `@pN` = existing parameters; elided parts are today's text):

```sql
WITH [autheris_page] AS (<root BuildInner, ORDER BY … , <pk tie-break> OFFSET … FETCH NEXT …>),
[autheris_k1] AS (
  SELECT [x].[autheris_k0] AS [autheris_k0],
         N'[' + STRING_AGG([x].[autheris_row], N',') WITHIN GROUP (ORDER BY [x].[autheris_rn]) + N']' AS [autheris_json]
  FROM (SELECT [t1].[autheris_k0] AS [autheris_k0], [t1].[autheris_rn] AS [autheris_rn],
               CAST((SELECT <SqlServerSelectList over t1> FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER) AS nvarchar(max)) AS [autheris_row]
        FROM (SELECT <inner columns>, [autheris_target].[device_id] AS [autheris_k0],
                     ROW_NUMBER() OVER (PARTITION BY [autheris_target].[device_id]
                                        ORDER BY [autheris_target].[ts] DESC, <child pk tie-break>) AS [autheris_rn]
              FROM <child table or secured source> AS [autheris_target]
              WHERE <tenant> AND (<row filter>) AND <where>
                AND EXISTS (SELECT 1 FROM [autheris_page] AS [autheris_p] WHERE [autheris_p].[device_id] = [autheris_target].[device_id])) AS [t1]
        WHERE [t1].[autheris_rn] <= 1) AS [x]
  GROUP BY [x].[autheris_k0])
SELECT ISNULL((SELECT <root list>, JSON_QUERY(ISNULL([autheris_k1].[autheris_json], N'[]')) AS [readings]
  FROM [autheris_page] AS [t0] LEFT JOIN [autheris_k1] ON [autheris_k1].[autheris_k0] = [t0].[device_id]
  ORDER BY <root order incl. tie-break> FOR JSON PATH, INCLUDE_NULL_VALUES), N'[]')
```

| Dialect | Differences to the SQL Server shape |
|---|---|
| PostgreSQL | No `TOP`; `autheris_row` = `PostgresObject(t1)`; aggregate `jsonb_agg(x.autheris_row ORDER BY x.autheris_rn)`; projection `coalesce(k1.autheris_json, '[]'::jsonb)`; root keeps `…::text`. A CTE referenced twice is materialized by PostgreSQL (consistent page). |
| SQLite | `autheris_row` = `json(SqliteObject(t1))`; aggregate `json_group_array(json(x.autheris_row) ORDER BY x.autheris_rn)` (aggregate ORDER BY already used today); projection `json(coalesce(k1.autheris_json, '[]'))`. |
| Oracle, Databricks | Not applicable (no tree compile, §1). |

Composite join keys: one `autheris_k{j}` per key column in `PARTITION BY`, `EXISTS`, `GROUP BY` and `ON`. NULL join keys: `EXISTS`/`ON` equality fails as the correlated `=` does, result `[]`. Grandchildren of a set-based child stay correlated against alias `t1` inside `autheris_row`. Row filters, tenant and `where` sit in the WHERE of the ranking level, so they are applied before `ROW_NUMBER` (anti-pattern 6). Names are deterministic (`autheris_k{alias index}`), values remain parameters (AC-9).

## 4. Work Packages

### WP-A Shared index-match rule (REQ-LPP-01/02)
Files: new `src/Autheris.Application/Sql/Indexing/IndexCandidates.cs`, new `src/Autheris.Application/Sql/Indexing/NestedListIndexMatcher.cs`; edit `src/Autheris.GraphQL/Interceptors/IndexUsabilityEvaluator.cs` (delegate `GetCandidateIndexes`); new `tests/Autheris.Tests.Unit/Sql/Issue443NestedListIndexMatcherTests.cs`.
Tests: `Pk_TsDeviceId_JoinDeviceId_OrderTsDesc_NoMatch`, `Index_DeviceIdTs_OrderTsDesc_MatchesBackward`, `Index_DeviceIdTsDesc_OrderTsAsc_MatchesBackward`, `Index_DeviceIdTs_MixedDirections_NoMatch`, `WhereEquality_InPrefix_AnyOrder_Matches`, `JoinColumn_NotInPrefix_NoMatch`, `OrderColumnInEqualitySet_IsSkipped`, `CompositeJoin_BothColumnsInPrefix_Matches`, `NoCandidates_NoMatch`, `OrWhereEquality_NotCountedAsEquality`; regression: existing `Issue421IndexAwarePricingTests` pass unchanged.

### WP-B Compiler set-based strategy (REQ-LPP-03..08)
Files: `src/Autheris.Application/Sql/Tree/TreeSqlCompiler.cs` (extract `BuildInnerPredicates`, strategy decision after `Prepare`, `BuildSetBasedChild`, page CTE in `BuildRoot`, guard), new `src/Autheris.Application/Sql/Tree/TreeSqlCompiler.SetBased.cs` (partial), `src/Autheris.Application/Sql/Tree/TreeQueryModel.cs` (`TreeListPlan`, `TreeListStrategy`, `CompiledTreeQuery.Lists`, internal `TreeCompileOptions`), `src/Autheris.Domain/Diagnostics/GatewayDiagnostics.cs` (counter); new `tests/Autheris.Tests.Unit/GraphQL/Issue443SetBasedTreeCompileTests.cs`.
Order: Red first with characterization snapshots of today's SQL (all three dialects; nested list with and without `orderBy`, to-one, grandchild, secured source, row filter, HMAC) before any refactor.
Tests: `IndexMatched_SqlIsByteIdenticalToBaseline` (per dialect, `[Theory]`), `NoOrderBy_SqlIsByteIdentical`, `ToOneRelation_SqlIsByteIdentical`, `DepthTwo_SqlIsByteIdentical`, `NoMatch_EmitsPageCteAndRowNumber` (per dialect), `NoMatch_RowFilterAndTenantInRankingLevel_BeforeRowNumber`, `NoMatch_SecuredSourceUsedInRankingLevel`, `NoMatch_RestrictsToPageKeysViaExists`, `NoMatch_AppendsChildPkTieBreak`, `NoMatch_AppendsRootPkTieBreak`, `RootWithoutClearUniqueKey_FallsBackCorrelated`, `ReservedNameCollision_FallsBackCorrelated`, `ForcedSetBasedFailure_EmitsCorrelatedAndRestoresParameters`, `ForcedSetBasedFailure_NeverDropsRowFilter`, `TwoListsOnOneParent_DecideIndependently`, `CompositeJoinKey_PartitionsAndJoinsOnAllColumns`, `SetBasedSql_IsDeterministicAcrossCompiles`, `AllValuesParameterized_NoLiteralInterpolation`, `ListsMetadata_ReportsStrategyAndReason`, `MaskedColumn_StillMaskedInRowJson`. Regression: all existing `TreeSqlCompiler*Tests` and `tests/Autheris.Tests.SqlCompilerGate/Parity/*` pass without changing expected text.

### WP-C Parity and benchmark (AC-1..7)
Files: new `tests/Autheris.Tests.Integration/Issue443LatestPerParentParityTests.cs` (SQL Server, PostgreSQL via Testcontainers as in `Issue390DescKeyReadTests`; SQLite in-process), new `tests/Autheris.Tests.Integration/Issue443LatestPerParentBenchmarkTests.cs` (`[Trait("Category","Benchmark")]`, excluded from the default run).
Tests (each over one shared fixture: parents without children, NULL join keys, n = 1 and n = 3, multi-column mixed-direction order, child `where`, tie-free data): `SetBased_EqualsCorrelated_FirstOne`, `SetBased_EqualsCorrelated_FirstN_MixedOrder`, `SetBased_EqualsCorrelated_WithWhere`, `ParentWithoutRows_ReturnsEmptyArray`, `RowFilterHiddenRow_NeverRanksFirst`, `MaskedColumn_SameMaskedValue`, `AuditEntries_SameAsCorrelated`, `Ties_SetBasedPicksLowestPkDeterministically`, `IndexDeviceIdTs_UsesCorrelatedTop`. Benchmark (SQL Server, ≥ 1 M child rows, 140 parents): set-based ≥ 10x faster than correlated, target ≤ 250 ms; result recorded in this plan's progress note by the lead.

### WP-D Slow-list reporting (REQ-LPP-09..11)
Files: `src/Autheris.Domain/Options/Protocols/GraphQlOptions.cs` (`GraphQLSlowListReportOptions`), `src/Autheris.Application/Sql/Tree/GovernedTreeQueryService.cs` (stopwatches; optional `ITreeExecutionObserver` parameter on `ExecuteAsync` of `IGovernedTreeQueryService`), new `src/Autheris.Application/Sql/Tree/TreeExecutionReport.cs`, new `src/Autheris.GraphQL/Interceptors/SlowListsMiddleware.cs` (ContextData accumulator, ranking D-9, gate D-10), `src/Autheris.GraphQL/Interceptors/CostAndQuotaMiddleware.cs` (store `ExposeCostExtensions`), `src/Autheris.GraphQL/Catalog/CatalogGraphQlTypeModule.cs` and `ConnectionGraphQlTypes.cs` (pass observer, prefix root response name), `src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.GraphQL.cs` (register after `MaskedFieldsMiddleware`); new `tests/Autheris.Tests.Unit/GraphQL/Issue443SlowListsMiddlewareTests.cs`.
Tests: `Disabled_NoExtension`, `BelowThreshold_NoExtension`, `AboveThreshold_ReportsTopPathsWithRank`, `PolicyWithoutExposeCost_NoExtension`, `ErrorResult_NoExtensionAndBodyUnchanged`, `Extension_ContainsOnlyPathAndRank_NoTableIndexSqlOrCounts`, `Ranking_UnindexedBeforeSetBasedBeforeIndexed`, `Ranking_CrossSourceOverHalfElapsed_RanksFirst`, `MaxEntries_Respected`, `Measurement_ExecutesStatementOnce`, `Options_RangeValidation`.

## 5. Shared-File Notes
- `TreeSqlCompiler.cs` is the hot file (WP-B only); land WP-B before any other tree-compiler change of the issue batch 443-450, rebase others onto it.
- `IndexUsabilityEvaluator.cs`: WP-A moves code out; the REQ-LPP-12 follow-up and any #421 follow-up must build on `IndexCandidates`.
- `GraphQlOptions.cs`, `CostAndQuotaMiddleware.cs`, `GatewayServiceCollectionExtensions.GraphQL.cs` (pipeline order) are shared with other GraphQL work: additive edits only.
- `IGovernedTreeQueryService.ExecuteAsync` signature change touches every fake implementing it in `tests/` (optional parameter keeps call sites compiling).

## 6. Risks
1. SQL Server inlines `autheris_page` twice; under READ COMMITTED locking a concurrent change can make a page row miss its child set (`[]`). The correlated form already reads child rows at different times within the statement; mitigated by the total order (D-4), accepted, documented for Phase 3.
2. `STRING_AGG` requires SQL Server 2017+ (compatibility level 140); lower levels fail at execution, not compile. Mitigation: fallback is compile-time only, so WP-C must confirm the supported minimum; if 2016 must be supported, gate set-based by a dialect capability flag.
3. Optimizer may still choose a poor plan for the ranking level on very large page sets (20 k parents); the benchmark covers 140 and 20 000 parents.
4. Static ranking (D-9) can name an indexed list that was actually slow (statistics, locking); the report is advisory only.
5. Timing side channel: the extension reveals that a list is "unindexed" by rank order; gated by policy and threshold (Phase 3 to rule).

## 7. Open Decisions (defaults taken)
1. OQ-1: ROW_NUMBER only; `MAX`/seek not built (D-3).
2. OQ-2: unknown index metadata = no match (D-1).
3. OQ-3: `SlowListReport.Enabled = false`, `ThresholdMs = 1000`, `MaxEntries = 3`.
4. OQ-4: statement and cross-source stopwatches plus static per-list class; no logical-reads enrichment (D-9).
5. OQ-5: no diagnostics permission exists; gate is `ExposeCostExtensions` of the client tier policy plus explicit enable (D-10).
6. OQ-6: REQ-LPP-12 pricing deferred to a follow-up issue.
7. OQ-7: to-one relations out of scope.
8. New: set-based only for depth-1 lists with explicit `orderBy` (D-2); default-ordered and deeper lists are follow-up candidates.
9. New: Oracle/Databricks not applicable (no tree compile).

## 8. Traceability

| Requirement | Decision | WP | Key tests |
|---|---|---|---|
| REQ-LPP-01 | D-1 | A | `Pk_TsDeviceId_JoinDeviceId_OrderTsDesc_NoMatch`, `Index_DeviceIdTs_OrderTsDesc_MatchesBackward` |
| REQ-LPP-02 | D-1 | A | `Issue421IndexAwarePricingTests` unchanged |
| REQ-LPP-03 | D-2, D-6 | B, C | `NoMatch_EmitsPageCteAndRowNumber`, `SetBased_EqualsCorrelated_FirstOne` |
| REQ-LPP-04 | D-2 | B | `IndexMatched_SqlIsByteIdenticalToBaseline`, `IndexDeviceIdTs_UsesCorrelatedTop` |
| REQ-LPP-05 | D-3, D-4, D-6 | B | `NoMatch_RestrictsToPageKeysViaExists` |
| REQ-LPP-06 | D-5, D-6 | B, C | `SetBased_EqualsCorrelated_FirstN_MixedOrder`, `ParentWithoutRows_ReturnsEmptyArray` |
| REQ-LPP-07 | D-6, D-7 | B, C | `NoMatch_RowFilterAndTenantInRankingLevel_BeforeRowNumber`, `ForcedSetBasedFailure_NeverDropsRowFilter`, `RowFilterHiddenRow_NeverRanksFirst` |
| REQ-LPP-08 | D-2 | B | `TwoListsOnOneParent_DecideIndependently` |
| REQ-LPP-09 | D-9, D-10 | D | `AboveThreshold_ReportsTopPathsWithRank` |
| REQ-LPP-10 | D-9 | D | `Measurement_ExecutesStatementOnce` |
| REQ-LPP-11 | D-10 | D | `Extension_ContainsOnlyPathAndRank_NoTableIndexSqlOrCounts`, `ErrorResult_NoExtensionAndBodyUnchanged` |
| REQ-LPP-12 | out (OQ-6) | – | – |

## Phase 3 security review

**Reviewer:** `csharp-security-expert`, 2026-11-03. **Verdict: APPROVED WITH CONDITIONS.** No decision weakens an existing control, provided the conditions below are binding. Facts verified: `ExposeCostExtensions` is false for Free tier, true for Standard/Enterprise/Internal (`ClientTierModels.cs`); report paths are the client's own response keys, so no hidden catalog name is echoed.

### Threat assessment (short)
- Row filter, tenant, mask and ranking order: D-6 places tenant, row filter (or secured source) and `where` in the WHERE of the ranking level, below `ROW_NUMBER`; ranking over hidden rows is therefore impossible when one shared predicate builder is used. Masks and HMAC are outer-projection literals (unchanged).
- Fallback: D-7 snapshot/restore is sound only if the correlated path is the sole fallback and re-reads predicates from the same builder; parameter-name collision after a half-built set-based form is the main risk.
- Injection: join keys, partition and order columns come from the catalog (validated identifiers, quoted by the dialect quoter); `autheris_k{i}` names are generated; values stay parameters. `n` is the only literal-like value (TOP/`rn <= n`).
- DoS: set-based scans the child table once per statement and can aggregate far more rows than the correlated TOP form; `STRING_AGG` over `nvarchar(max)` and `jsonb_agg` are unbounded per parent unless `n` is capped.
- Timing/disclosure: rank order reveals "unindexed" status (D-9 static class) and `Strategy` metadata must not reach clients/logs with names.

### Binding conditions (fail-closed)
- **SEC-LPP-01** One predicate builder (`BuildInnerPredicates`) is the only source of tenant, row filter, secured source and `where` for correlated and set-based forms; the set-based builder must not accept an empty predicate set. A missing tenant predicate when the plan requires one throws, and the throw must fall back to correlated or fail the request, never emit an unfiltered statement.
- **SEC-LPP-02** The fallback guard restores `CompileContext` (parameter list, parameter name counter, alias counter, pending HMAC/mask registrations, CTE list) exactly to the snapshot, and asserts equality afterwards. A failure of the fallback itself propagates as a request error (fail closed), never a partially built SQL.
- **SEC-LPP-03** The fallback is catch-scoped to compile exceptions only; `OperationCanceledException` is not swallowed; the reason `CompileFailure` is logged at Warning without SQL text, values or table names (static reason code plus dialect only).
- **SEC-LPP-04** No new SQL text is built from client strings. Every identifier in the new CTE/aggregation (join, partition, order, tie-break, reserved names) passes the existing quoter; reserved-name collision (D-2) falls back to correlated. `n` is emitted only as a validated positive integer within the cap of SEC-LPP-05.
- **SEC-LPP-05** The existing nested `first:` cap (and the root page cap) is enforced before strategy selection and applies to the set-based form; set-based is not eligible without a finite cap (`rn <= n`, `n` clamped). The page CTE root carries the same `first/after` bounds as today; a max parent-page size times `n` bound (rows emitted) is rejected at compile time if it exceeds the existing result-size limit (reuse it; if none exists, add `TreeCompileOptions.MaxSetBasedRows`, default 100 000, over limit means correlated).
- **SEC-LPP-06** The JSON aggregate size is bounded by SEC-LPP-05 and the existing response size limit; no new unbounded `nvarchar(max)` path may bypass the executor's result-size guard. The set-based form never runs without the executor command timeout.
- **SEC-LPP-07** The SQL Server page CTE must be deterministic (D-4 total order); if no Clear unique key exists the plan falls back (as designed). Page-key `EXISTS` restriction uses the same tenant/row-filter-protected page, so child rows are only ranked for parents the caller may see; a page mismatch from concurrent change yields `[]` (accepted, R-1) and never a row of another parent or tenant.
- **SEC-LPP-08** Slow-list report gate (D-10) is conjunctive and fail-closed: `Enabled` AND elapsed >= `ThresholdMs` AND resolved policy present AND `ExposeCostExtensions` AND result without errors. Missing/unknown ContextData key means no extension. The default `Enabled = false` is mandatory; the report is not enabled by any tier default.
- **SEC-LPP-09** The extension carries only `path` and `rank` (no durations, counts, strategy, reason, index or table names, SQL, parameter or tenant data). Paths are built from the client's response keys only, never catalog names; ranking must not be derivable as the unique signal of index existence: either randomise nothing and document the residual (R-2), or rank by measured request share only. Default: keep static ranking but only emit entries when the elapsed gate is met (as designed).
- **SEC-LPP-10** `CompiledTreeQuery.Lists`, `TreeExecutionReport` and the `autheris.tree.nested_list.strategy` counter are internal: not serialised to responses, audit or logs beyond tags dialect/strategy/reason (low cardinality, no names, no tenant). The stopwatches add no second execution and no extra cost-quota consumption; the report never alters admission, pricing or quota decisions (cost/admission computed before, unchanged).
- **SEC-LPP-11** Audit parity: audit entries (columns read, masked fields) are identical for set-based and correlated forms; the report is produced after audit emission and cannot suppress it.

### Mandatory security tests (all `[Trait("Category","Security")]`)
- WP-B: `SetBased_AlwaysContainsTenantAndRowFilter_BeforeRowNumber` (all dialects), `SetBased_EmptyPredicateSet_Throws`, `ForcedFailure_RestoresParametersAliasesAndCtes_ExactSnapshot`, `ForcedFailure_FallbackNeverUnfiltered`, `CompileFailure_LogContainsNoSqlOrNames`, `Cancellation_NotSwallowedByFallback`, `NestedFirst_OverCap_Rejected_BeforeStrategy`, `SetBased_RowBudgetExceeded_FallsBackCorrelated`, `HostileIdentifiers_QuotedOrRejected_InCteNames`, `ValuesRemainParameters_InSetBasedSql` (no literal from `where` values in text), `RootWithoutUniqueKey_NoPageCte`.
- WP-C: `RowFilterHiddenRow_NeverRanksFirst` (SQL Server, PostgreSQL, SQLite; the hidden row would be the winner), `TenantB_RowsNeverRankedForTenantA`, `MaskedColumn_NoClearValueInSetBasedJson`, `AuditEntries_IdenticalToCorrelated`, `ConcurrentInsert_PageMismatch_ReturnsEmptyNotForeign`.
- WP-D: `Gate_AllConjuncts_EachMissingSuppresses` (theory over the five conjuncts), `FreeTier_NoExtension`, `Extension_PathsAreResponseKeysNotCatalogNames` (alias test), `Extension_NoDurationsOrCounts`, `ErrorResult_NoExtensionAndBodyUnchanged`, `Report_DoesNotChangeQuotaOrCost`, `Counter_TagsContainNoNamesOrTenant`.

### Accepted residual risks
- R-1 (plan risk 1): SQL Server double evaluation of the page CTE under READ COMMITTED may yield `[]` for a concurrently changed parent; no cross-parent or cross-tenant leakage.
- R-2: a permitted caller (policy plus Enabled plus slow request) can infer coarse "unindexed list" ranking; the same tier already sees cost extensions.
- R-3: set-based costs one scan of the child partition set per statement; bounded by SEC-LPP-05/06 and the command timeout.
- R-4: tie winners may differ between forms (D-5), not a confidentiality issue.

### Owner questions (defaults)
1. Is there an existing result-row/size limit in the executor to reuse for SEC-LPP-05? Default: yes, reuse; otherwise add `MaxSetBasedRows = 100 000`.
2. Minimum SQL Server version for `STRING_AGG` (plan risk 2)? Default: 2017+ (compat 140); lower means a capability flag forcing correlated.
3. Report for Standard tier too, or Enterprise/Internal only? Default: conjunction with `ExposeCostExtensions` only, per D-10.
