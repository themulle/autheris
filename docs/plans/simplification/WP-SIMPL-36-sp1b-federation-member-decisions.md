# WP-SIMPL-36 sub-package 1, part B: federation member column decisions (addendum)

Parent: [WP-SIMPL-36 security lane](WP-SIMPL-36-security-lane.md), sub-package 1, item 5 (GHSA-7ccv-hfgq-5qwm, federation column access checks). Must satisfy SEC-SIMPL-94 (OD-15): each source subtree is authorized and masked against its own access decision before staging. The fix list is the private advisory's fix list; this addendum names no exploit detail. Part A (audit flag from the real read path) is already on `feat/simpl-36-sp1` and stays green.

Verified on `feat/simpl-36-sp1b` (8b0edb42): `CrossSourcePlanner.Plan` partitions at `:266` without decisions, stages each subtree with the root member's decision (`:329`); `SubtreeCompiler.Compile` (`:79`) builds per-member decisions but compiles with a no-op mask provider (`:170`); single tables strip Deny columns (`:383-401`). `SubtreeGraphPartitioner.Partition(statement, tables, max)` has two callers: the planner and `QueryAnalysisService.Sql.cs:355` (explain only).

## Decisions
1. **Design: fail-closed pushdown eligibility, no plan-model change.** A table may join a same-source subtree only if its own decision makes every column Clear. All other tables become `RemainingSingleTables` and are staged one by one with their own decision (the existing single-table path that strips Deny and masks per member). Invariant: inside a subtree every output column is Clear for its own member, so the root-decision lookup by name in staging and `ProjectRow` cannot weaken any member. A per-member decision map in the plan model is not needed for the fix list and is not built (default taken: minimal design).
2. **Eligibility predicate.** New `internal static bool SubtreeCompiler.IsPushdownEligible(ResolvedSourceTable table, TableAccessDecision decision)`: true only when `decision.IsAllowed`, `HasUnconstrainedColumnAllow`, every `ColumnAccess` entry is `Clear`, `ColumnMasks` is null or has no `MaskedColumns`, and `GetEffectiveColumnAccess(c, table.Metadata) == Clear` for every `table.Metadata.Columns` entry (covers catalog-sensitive columns, masking rules and scope clearance). Empty catalog column list with any explicit `ColumnAccess` entry: not eligible.
3. **Partitioner hook.** `SubtreeGraphPartitioner.Partition(statement, tables, max, Func<ResolvedSourceTable, bool>? isPushdownEligible = null)`; `AnalyzeTableSource` returns a non-SQL cluster (`IsSql = false`) for a named table the predicate rejects, so it never merges and lands in `RemainingSingleTables`. `PartitionSubtrees` forwards the optional argument. Null keeps today's behaviour for the explain caller only.
4. **Planner wiring.** `CrossSourcePlanner.Plan` (`:266`) passes `t => SubtreeCompiler.IsPushdownEligible(t, RequireDecision(decisions, t))` (missing decision throws, fail-closed). The required-filter check at `:270` is unchanged.
5. **Compiler guard (defense in depth).** In `SubtreeCompiler.Compile`, after the member-decision loop (`:101-110`), throw `GatewaySecurityException` (`GovernedSqlErrorCodes.PolicyDenied`, reuse an existing code; `Sql/Errors/**` is not touched) when any member, root included, is not eligible. The transitive masking heritage code (`:216-300`) stays as a second layer; its removal is left to a later simplification WP.
6. **Out of scope / not touched:** `FederatedDuckDbExecutionService.cs`, `GovernedSqlRewriter.cs`, `Sql/Errors/**`, `Sql/Policy/**`, `src/TrinoSqlEngine/**`, `FederatedStagingService.cs`, `GovernedConnectorReader.cs`, `QueryAnalysisService.Sql.cs`.
7. **Size:** about 70 net src lines (predicate 25, partitioner 15, planner 5, compiler guard 15, xml docs). Budget 400.

## TDD steps (one batched build/test run per step)
1. Red: add the regression cases and the `FederatedSubtree_*` tests below; they fail on today's code (denied/masked member column staged in clear).
2. Green: decisions 2-4. Then decision 5 with its unit test.
3. Refactor: update existing subtree tests whose tables carry masks (see risk 1) to assert the fallback, then run `Category=Security` plus `tests/Autheris.Tests.Unit/Federation/*Subtree*` and `Security/WebSqlSameSourceSubtreeSecurityTests`.
Commit: `fix(security): resolve GHSA-7ccv-hfgq-5qwm (federation column access checks)`.

## Tests (all `[Trait("Category","Security")]`)
- `AdvisoryRegressionTests.Ghsa_7ccv_5qwm_regression` new `InlineData` cases, one per fix-list bullet: `non_root_deny_column_never_staged_wildcard`, `non_root_deny_column_never_staged_explicit`, `name_clash_root_clear_member_masked_stays_masked`, `member_mask_applied_per_member`; existing `audit_reports_actual_masking_path` stays.
- New `tests/Autheris.Tests.Unit/Security/FederatedSubtreeColumnDecisionTests.cs`: `FederatedSubtree_NonRootMemberDenyColumn_IsNeverStaged_WildcardAndExplicit`, `FederatedSubtree_NameClashRootClearMemberMasked_StaysMasked`, `FederatedSubtree_AuditReportsActualMaskingPath`, `FederatedSubtree_ColumnConstrainedMember_FallsBackToSingleTableStaging`, `FederatedSubtree_AllMembersClear_StillPushesDown`, `FederatedSubtree_Compile_RejectsIneligibleMember`, `FederatedSubtree_Partitioner_IneligibleTable_IsRemainingSingle`.
- Assertions run through the real `CrossSourcePlanner` plus `FederatedStagingService` with a fake connector that returns the raw (unmasked, all-column) rows; assert the staged DuckDB table content (denied column absent, masked value masked), not only the final result, for both the wildcard and the explicit-reference form, and for both table orders in the join.

## Risks
1. Existing tests `T_SEC_DATA123_02_MaskingHeritage_PreservedInDuckDb` and `_03_CrossSourceJoinOnSubtreeMask_Blocked` use a masked member; they now see single-table staging. Rewrite them to assert the masked value and the same refusal on the single-table path (join on a masked column must still be refused); never weaken them.
2. Performance: governed tables with column constraints lose same-source join pushdown (more rows staged, bounded by the existing staging row/byte limits). Accepted as the fail-closed trade-off; per-member pushdown is a possible follow-up.
3. Explain output (`QueryAnalysisService`) may still show a subtree that execution does not push down (it has no decisions). Cosmetic; no data path.
4. Row-filter or virtual-filter interplay is unchanged (row policies already apply per member in `Compile`).

## Owner questions (default taken)
- OQ-1: accept loss of pushdown for column-constrained tables instead of a per-member decision model in this fix? Default: yes (minimal fail-closed, SEC-SIMPL-94 satisfied).
- OQ-2: align explain output with the eligibility rule? Default: no in this fix; follow-up issue.

## Binding conditions (Phase 3)
Verdict: APPROVED WITH CONDITIONS. The design covers every item of the fix list (per-member deny/mask for wildcard and explicit references, name clash, audit flag via part A) and satisfies SEC-SIMPL-94 / OD-15. OQ-1 and OQ-2 defaults are agreed (OQ-2 follow-up issue must be filed).
- C-1: `IsPushdownEligible` returns false (never throws, never true by default) for a null decision, null or unavailable `Metadata`, and any unknown or non-enumerated access level; every branch defaults to "not eligible".
- C-2: Eligibility additionally requires `Metadata.ColumnMasks`-equivalent catalog state to be clear independent of the column list: `ColumnMaskingRules.Count == 0` and `ScopeClearance` not restricting, because `IsCatalogSensitive` also keys on masking rules whose column is absent from `Columns`.
- C-3: The partitioner predicate is evaluated for every named table, including the first and a would-be root; the default `null` predicate must not be reachable from `CrossSourcePlanner` (planner always passes it).
- C-4: The compiler guard (decision 5) must throw before any SQL text for the subtree is generated, and the message must not contain column names or mask values.
- C-5: The single-table fallback must keep refusing filters, joins and window references on masked or denied columns (`IsMaskedOrDenied`); the rewritten `T_SEC_DATA123_02/_03` tests must keep their refusal assertions unchanged.
- C-6: The OQ-2 follow-up (explain shows a subtree that execution does not push down) must expose no column or decision detail beyond what explain shows today.
Extra mandatory tests (`[Trait("Category","Security")]`):
- `FederatedSubtree_IsPushdownEligible_DefaultsToFalse_ForMissingDecisionEmptyCatalogOrUnknownLevel`
- `FederatedSubtree_CatalogMaskingRuleForColumnAbsentFromColumnList_NotEligible`
- `FederatedSubtree_ScopeRestrictedClearance_NotEligible`
- `FederatedSubtree_Compile_RejectsIneligibleRoot` (root, not only non-root member)
- `FederatedSubtree_FallbackSingleTable_FilterOnMaskedMemberColumn_StillRefused`
