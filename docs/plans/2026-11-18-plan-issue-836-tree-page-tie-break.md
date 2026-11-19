# PLAN-OI-836: Set-based tree page CTE does not project its tie-break key (issue #836)

Issue: [#836](https://github.com/themulle/autheris/issues/836) (reported 2026-11-18 against v1.8.4). Related: plan
[`2026-11-03-plan-issue-443-latest-per-parent-set-based.md`](2026-11-03-plan-issue-443-latest-per-parent-set-based.md)
(D-4 page CTE and totality, D-5 child tie-break). Bug fix, one PR, no options, no schema change.

**Status: IMPLEMENTED (WP-836-1, WP-836-2)** on branch `fix/836-tree-page-tie-break`, including the review follow-ups.
The new tests fail on `main` except the counter-checks and `TieBreakColumn_NotSelected_IsNotReturnedInJson` (see section 4).

## 1. Product (qualification)

- Valid bug, confirmed on `main` @ `feeab350`: a tree query whose root has a set-based list child, an explicit `orderBy`
  without the root's unique key, and no selection of that key fails with an invalid column (SQL Server) or an unknown
  column (PostgreSQL). The client sees `INTERNAL_SERVER_ERROR`.
- Expected behaviour: the query returns rows in the requested order, made total by the clear unique key (as #443 D-4
  intends). The tie-break key is an internal sort column; it is not returned unless the caller selected it.
- Rejected alternative: falling back to the correlated strategy in this shape. It hides the bug and loses the #443
  performance gain. Rejected alternative: adding the key through `Need()` in `Prepare` for every list root. It changes the
  correlated SQL and every non-set-based plan, for no benefit.

## 2. Root cause

- `src/Autheris.Application/Sql/Tree/TreeSqlCompiler.SetBased.cs` `BuildSetBasedRoot` (about L223-L229 on `main`) appends
  the clear unique-key columns from `GetClearUniqueKeyColumns` (primary key, else the first clear unique index) that are
  not in `plan.Order`. The page CTE `ORDER BY` and the outer `ORDER BY {rootAlias}.<col>` (SQL Server `FOR JSON`,
  PostgreSQL `jsonb_agg(... ORDER BY ...)`) both use this list.
- `BuildPageCte` (about L305 on `main`) projects only `plan.InnerColumns`. `Prepare` (`TreeSqlCompiler.cs` L300-L430) adds
  a column there only when it is selected (clear or HMAC), ordered, a relation parent column, or the single fallback
  column. The tie-break key is therefore missing from `autheris_page` unless one of these applies, and the outer reference
  fails.
- Secondary: `GetClearUniqueKeyColumns` returns the spelling of the key metadata, not the catalog column spelling, and
  checks access on that spelling. A key declared as `ID` for column `id` is quoted as `"ID"` on PostgreSQL (a different
  identifier) and is projected twice next to a selected `id`. With a case-sensitive column access map, a Mask or Deny rule
  on `id` is not found under `ID`, so the masked key was used as tie-break. The child ranking tie-break (#443 D-5) has the
  same access check on the raw spelling.

## 3. Design

1. New `ResolveClearKey(metadata, decision, columns)`: resolves every key column to its catalog spelling
   (`metadata.GetColumn(c).ColumnName`), checks the effective access on that name, and requires `SafeNameRegex`. An empty
   key, or a column that is not in the catalog, not a safe name, or not effectively Clear, disqualifies the key (fail
   closed). `GetClearUniqueKeyColumns` uses it for the primary key and each unique index.
2. The child ranking tie-break (`BuildSetBasedChildCte`) uses `ResolveClearKey` for the child primary key too.
3. `BuildPageCte` projects `plan.InnerColumns` followed by the order columns (`rootOrderWithTieBreaks`), de-duplicated
   case-insensitively with an explicit loop and a `HashSet` (first occurrence wins). Order columns from `plan.Order` are
   already inner columns, so only missing tie-break columns are added, after the existing columns. When the key metadata
   spells a column differently from the catalog (`ID` for `id`), the generated text changes to the catalog spelling: the
   same column on SQL Server (case-insensitive collation of identifiers), and the fix for PostgreSQL. For a key in catalog
   spelling that was already projected, the SQL text is unchanged.
4. The outer projection is built from `plan.Outputs` only, so the extra page column never reaches the JSON.

## 4. Work packages

- WP-836-1: the page CTE projection and the root key resolution in `TreeSqlCompiler.SetBased.cs`; CHANGELOG `Fixed` entry.
- WP-836-2 (review follow-up): the child ranking tie-break uses the same key resolution; explicit de-duplication loop.
- Tests (red first): new `tests/Autheris.Tests.Unit/GraphQL/Issue836SetBasedTreePageTieBreakTests.cs`, SQL Server and
  PostgreSQL each. No in-memory engine runs the tree SQL for these dialects, so the tests parse the compiled text: every
  outer `ORDER BY` column must be a page CTE output, with no duplicates.
  - Reported shape and composite keys: `SingleColumnKey_NotSelectedNotOrdered_IsProjectedInPageCte`,
    `CompositeKey_NotSelectedNotOrdered_AllColumnsProjectedInPageCte`, `CompositeKey_OnePartOrdered_OtherPartProjectedOnce`,
    `KeySpelledDifferentlyInMetadata_UsesCanonicalColumnNameOnce`, `SecuredSourceRoot_TieBreakIsProjectedFromTheSubquery`.
  - Counter-checks (pass on `main` too): `Key_Selected_IsProjectedExactlyOnce`, `Key_InRootOrder_IsProjectedExactlyOnce`.
  - Security (`[Trait("Category", "Security")]`): `TieBreakColumn_NotSelected_IsNotReturnedInJson` (passes on `main`,
    guards the output), `MaskedPrimaryKey_IsNeverProjected_ClearUniqueIndexIsTheTieBreak`,
    `DeniedPrimaryKey_IsNeverProjected_ClearUniqueIndexIsTheTieBreak`, `KeySpelledDifferently_MaskOnCatalogSpelling_RejectsTheKey`,
    `UnknownOrUnsafeKeyColumn_DisqualifiesTheKey_FallsBackToClearUniqueIndex`,
    `UnknownOrUnsafeKeyColumn_NoOtherClearKey_FallsBackCorrelated`,
    `ChildKeySpelledDifferently_MaskOnCatalogSpelling_IsNotARankingTieBreak`. The fallback index is on `asset_tag`, a
    column that is neither selected nor a join column, so only the tie-break logic can project it.
- Existing `Issue443SetBasedTreeCompileTests` must stay green. No golden SQL snapshots exist for the page CTE.
  Integration/Parity: no test covers this shape; compile check only.

## 5. Security conditions

- SEC-836-1: a key column becomes a tie-break (and so a page CTE column or a ranking order term) only when it exists in the
  catalog, has a safe name, and is effectively Clear under its catalog spelling. A masked or denied primary key, also one
  declared in a different spelling, is never projected or ordered by; the next clear unique index is used, else the list
  falls back to the correlated strategy (`ParentOrderNotTotal`). Tests: the six key-resolution Security tests above.
- SEC-836-2: tie-break columns are never part of the JSON response unless selected; the outer projection stays
  `plan.Outputs` only. Test: `TieBreakColumn_NotSelected_IsNotReturnedInJson`.
- SEC-836-3: no new identifier source; every identifier still passes the dialect quoter's validation and `SafeNameRegex`.
  On a secured source the page CTE reads the tie-break column from the secured subquery, which selects every catalog column.
