# PLAN-ROBIN-100: Robin suite to 100 scenarios (Phase 2 addendum)

Input: [`2026-10-23-req-robin-100-scenarios.md`](2026-10-23-req-robin-100-scenarios.md). Parent: [`2026-10-23-plan-nightly-parity-tests.md`](2026-10-23-plan-nightly-parity-tests.md) (D-17, sections 5 and 7). Verified against `feat/robin-100` (base `fix/parity-gaps`). Test code only; no host variant, no `src/` change (SEC-PAR-03).

## 1. Decisions

1. **Host facts.** Min group size is lifted to 1 (`ParityHostSettings`), so R39 cannot be observed. No catalog relations, no denied table/column and no scope rule exist, so R81, R87 and R100 cannot be observed. Present and enabled by default: bulk route `POST /api/v1/data/{d}/{s}/{t}/bulk` (`BulkTableStreamRequest`: `select`, `cursor`, `limit`; `_meta.nextCursor`/`hasMore` line and `X-Next-Cursor` header; tampered cursor gives 400 `INVALID_CURSOR`) and analysis `POST /api/v1/governance/queries/analyze` (`{queries:[{sqlQuery,dataSource:"default"}]}` gives `validationReport.isValid`, `costEstimate.estimatedRows` (equal to an explicit LIMIT), `sourceDatabaseReadsExecuted`). R70, R71, R84-R86 stay.
2. **Swaps (reserve `Robin_Proactive_Sum*`).** R39 becomes `SumQtyByProduct`, R81 `SumAmountForProductList`, R87 `SumAmountByYear`, R100 `SumAmountScopedTwoRegions`. SQLite-only rewrites: R44 and R45 use GraphQL `timeBuckets.timeZone` (runs on SQLite) and WebSQL `strftime` in UTC. R46 is GraphQL only (on SQLite, WebSQL `convert_timezone` is always `SQL_POLICY_VIOLATION`, already covered by `Robin_130_TimeZoneConversionGroupByLocalDay`). Reshaped to avoid duplicating existing methods: R68 (page size 250, adds WebSql), R82 (unknown table vs unknown schema), R96 (grouped), R97 (other column is not enough).
3. **PRD corrections.** COUNT(note) is 7 (#1-#6, #8), not 5. The required-filter code is `REQUIRED_FILTER_COLUMN_MISSING`. R65 asserts ids, not a truncation flag (an explicit page is not truncated).
4. **Notation.** `W(sql)` = `Sql(...)`; `G(args, keys)` = `GqlAggregate`; `GL(args[, sel, table])` = `GqlList`; `R(f)` = `RestAsync(SalesRest + "?select=id&filter=" + Escape(f) + "&orderBy=id&limit=100")`. Ids are an order-insensitive `IdsShouldBe` unless marked "ordered". "Leak" = throw `DeniedRequestSucceededException` (hard failure, never a gap). New expected literals go into `ParityExpected` with a LINQ line in `ParitySeed_ExpectedValuesMatchRows`.
5. **Files (D-17 kept).** Make `RobinScenarios` `public sealed partial class` (the same one-word change in both WPs, so it merges cleanly). Add new parts in `Robin/RobinScenarios.Wp1.cs` and `Robin/RobinScenarios.Wp2.cs` without repeating `[Trait]`/`[Collection]`/base list: `ParityIsolation_EveryParityTestClassDerivesFromBase` only scans the attributed part. Primary-constructor `host` is not in scope in other parts, so use `_robin`. WP-R2 adds `RobinClient.PostJsonAsync(path, body)` (raw status, body, headers; `Checked` applied).
6. **Shared files.** `RobinKnownGaps.cs`, `ParityExpected`, `ParitySeed_ExpectedValuesMatchRows`, `parity-baseline.json` and plan section 5: each WP appends only inside a `// --- WP-R1 ---` / `// --- WP-R2 ---` block (JSON: whole cells, no reordering). The lead merges and then adds `RobinSuite_HasExactly100DistinctScenarios` (Meta; it counts distinct `Robin_` methods on `RobinScenarios` by reflection, so it is red until both WPs are merged; its name must not start with `Robin_`).
7. **Gap protocol.** A failing functional cell gets `RobinKnownGaps["Method|Channel|Variant"] = "PARITY-GAP-NNN"` (WP-R1 030-049, WP-R2 050-069; highest existing id is 022) and a `### PARITY-GAP-NNN` draft in section 5 of the nightly plan (only that file is read by `ParityIsolation_KnownGapIdsHaveDrafts`; duplicate headings fail it). Never a gap: a 5xx, a leak, a denied request that succeeds (R92-R95, R98), or a security assertion (SEC-PAR-05).

## 2. Scenarios (method `Robin_<name>`; expected values hand-computed from the seed literals)

### WP-R1 (R31-R65, ~35 methods, ~700 LOC incl. expected literals)

| Id | Name | Ch | Query shape | Expected |
|---|---|---|---|---|
| R31 | 28_MinMaxAmount | W,G | `SELECT MIN(amount) lo, MAX(amount) hi FROM parity_sales`; G MIN/MAX amount | 10.00, 400.00 |
| R32 | 28_CountStarVsCountColumn | W,G | `COUNT(*) n, COUNT(amount) na, COUNT(note) nn`; G COUNT, COUNT column amount, note | 18, 15, 7 |
| R33 | 112_SumByProduct | W,G | `GROUP BY product` SUM(amount); G groupBy product | Widget 380.50, Gadget 1260.00, Gizmo 241.00 |
| R34 | 112_CountByProductOrdered | W,G | COUNT GROUP BY product ORDER BY n DESC | ordered Widget 8, Gadget 6, Gizmo 4 |
| R35 | 112_SumByRegionNullGroup | W,G | `WHERE region IS NULL` count, sum; G `isNull: true` | [2, 70.00] |
| R36 | 112_SumByRegionAndProduct | W,G | GROUP BY region, product; G groupBy [region, product] | N-Wid 4/220.50, N-Gad 2/400.00, N-Giz 1/15.25, S-Wid 2/150.00, S-Gad 2/800.00, S-Giz 2/225.75, E-* 1/null each (3 groups), null-Wid 1/10.00, null-Gad 1/60.00; 11 groups, sums total 1881.50 |
| R37 | 280_AggregationWhereIsReturn | W,G | `WHERE is_return = 1` (W), `is_return: { eq: true }` | [3, 490.00] |
| R38 | 280_AggregationWhereBetweenDates | W,G | `sale_date >= '2024-01-01' AND sale_date < '2025-01-01'`; G gte/lt | [8, 1405.25] |
| R39 | Proactive_SumQtyByProduct | W,G | SUM(qty) GROUP BY product | Widget 38, Gadget 15, Gizmo 16 |
| R40 | Proactive_HavingCountGreaterThan | W | `GROUP BY region HAVING COUNT(*) > 5` | North 7, South 6 |
| R41 | Proactive_GroupByQuarter | W,G | W `strftime('%Y',sale_date)`, `(CAST(strftime('%m',sale_date) AS INTEGER)+2)/3`; G bucket QUARTER on sale_date | ordered 2023Q1 1, 2023Q2 1, 2024Q1 4, 2024Q2 1, 2024Q3 2, 2024Q4 1, 2025Q1 5, 2025Q2 3 (G key: first day of the quarter) |
| R42 | Proactive_GroupByDay | W,G | GROUP BY sale_date; G bucket DAY | 16 groups, sum 18; 2024-01-10 = 2, 2024-07-20 = 2, all others 1 |
| R43 | Proactive_GroupByHour | W,G | W `strftime('%H', sold_at)`; G bucket HOUR on sold_at (UTC) | 11 groups: 00 4, 05 1, 06 2, 07 1, 08 1, 09 1, 10 1, 11 1, 12 2, 15 2, 23 2 |
| R44 | 130_HourBucketBerlinWinterSummer | W,G | `id IN (1,8)`; G HOUR bucket with `timeZone: "Europe/Berlin"`; W `strftime('%H', sold_at)` | G hours 11 (#1, CET) and 17 (#8, CEST); W (UTC) 10 and 15 |
| R45 | 130_LocalDayBoundaryNorth | W,G | `id IN (6,10)`; G DAY bucket with `timeZone: "UTC"`; W `strftime('%Y-%m-%d', sold_at)` | 2024-03-31, 2024-12-31 (Berlin gives 2025-01-01 for #10, see the existing #130 scenario) |
| R46 | 130_UnknownTimeZoneRejected | G | DAY bucket with `timeZone: "Mars/Olympus"` | `Rejected()` (stable 4xx) |
| R47 | 36_TimestampFilterIso | W,G,R | `sold_at >= '2025-01-01T00:00:00Z'`; G gte; R `sold_at ge 2025-01-01T00:00:00Z` | #11-#18 |
| R48 | Proactive_DateEqualsSingleDay | W,G,R | `sale_date = '2024-02-29'`; G eq; R `sale_date eq 2024-02-29` | #5 |
| R49 | Proactive_IsNullAmount | W,G,R | `amount IS NULL`; G isNull; R `amount eq null` | #13, #14, #15 |
| R50 | Proactive_IsNotNullRegion | W,G,R | `region IS NOT NULL`; G `isNull: false`; R `region ne null` | #1-#15, #18 |
| R51 | 280_NeComparisonDropsNullRows | W,G | `region <> 'North'`; G neq | #7-#15 |
| R52 | Proactive_CoalesceAmountZero | W | `SELECT id, COALESCE(amount, 0) a ... WHERE id IN (12,13,14,15)` and `SUM(COALESCE(amount,0))` | 135.75, 0, 0, 0; sum 1881.50 |
| R53 | Proactive_NotInWithNullRows | W,G | `region NOT IN ('North')`; G nin | #7-#15 |
| R54 | Proactive_AvgIgnoresNull | W,G | AVG(amount) | 125.43333333333333 (`AvgAmount`) |
| R55 | Proactive_NullBoolIsReturn | W,G,R | `is_return IS NULL`; G isNull; R `is_return eq null` | #12-#15 |
| R56 | 128_InListStrings | W,G,R | `product IN ('Widget','Gizmo')`; G in; R `product in ('Widget','Gizmo')` | #1, 2, 5, 6, 7, 10, 11, 12, 13, 15, 16, 18 |
| R57 | 128_InListIntegers | W,G,R | `id IN (1,5,18,99)` | #1, #5, #18 |
| R58 | 128_InListDuplicatesCollapse | W,G | `id IN (1,1,1)` | #1 (once) |
| R59 | 128_NotInListParameter | W,G | W `product NOT IN (@p)` p=[Widget,Gizmo]; G `nin: $p` | #3, 4, 8, 9, 14, 17 |
| R60 | 128_InListWithApostropheValue | W,G | W `note IN (@n)` n=["it's"]; G `in: $n` | #4 |
| R61 | Proactive_LikePrefix | W,G,R | `note LIKE 'emoji%'`; G startsWith; R `startswith(note,'emoji')` | #8 |
| R62 | Proactive_LikeCaseSensitivity | W,G | W `note LIKE 'PLAIN'`; G `startsWith: "PLAIN"` | SQLite: #1 (ASCII LIKE is case-insensitive); G equals W |
| R63 | Proactive_ContainsBackslash | W,G | W `instr(note, '\') > 0`; G `contains: "\\"` | #5 |
| R64 | Proactive_CombinedAndOrNesting | W,G,R | `(region='North' AND qty>=5) OR product='Gizmo'`; G or/and; R same with `and`/`or` | #1, 2, 6, 10, 12, 15 |
| R65 | 39_FirstPageOfBig | W,G,R | parity_big `ORDER BY id LIMIT 100 OFFSET 0`; GL first 100 offset 0; R limit 100 offset 0 | ordered 1..100 |

### WP-R2 (R66-R100, ~35 methods, ~800 LOC incl. `PostJsonAsync` and NDJSON parsing)

| Id | Name | Ch | Query shape | Expected |
|---|---|---|---|---|
| R66 | 39_LastPageOfBig | W,G,R | offset 900 limit 100 | ordered 901..1000 |
| R67 | 39_OffsetBeyondEndEmpty | W,G,R | offset 1000 limit 100 | 200, zero rows |
| R68 | 39_PagesAreDisjointAndComplete | W,G,R | 4 pages of 250 (offset 0, 250, 500, 750) | concatenation = 1..1000, no duplicates |
| R69 | 39_OrderByDescPaging | W,G | `ORDER BY id DESC LIMIT 5`; GL `first: 5, orderBy: [{ id: DESC }]` | ordered 1000, 999, 998, 997, 996 |
| R70 | 178_BulkCursorContinuation | R | POST `.../parity_big/bulk` `{select:["id"],limit:400}`, resume with `cursor` until no `nextCursor` | slices 400/400/200; ids 1..1000 once; last `hasMore` false |
| R71 | 222_TamperedCursorRejected | R | first slice cursor with one base64 character changed | 400 `Rejected("INVALID_CURSOR")` |
| R72 | Proactive_OrderByTwoKeys | W,G | `region IN ('North','South') ORDER BY region, amount DESC, id` | ordered 3, 4, 1, 2, 18, 5, 6, 8, 9, 12, 10, 11, 7 |
| R73 | Proactive_OrderByNullPlacement | W,G | `ORDER BY amount, id` (all 18) | W (SQLite NULLS FIRST): 13, 14, 15, 16, 6, 5, 18, 2, 17, 7, 11, 10, 1, 12, 3, 4, 8, 9. G equals W, or has the same non-null order with 13-15 last; the observed end is pinned in the test |
| R74 | Proactive_JoinSalesToRegions | W | `s JOIN parity_regions r ON s.region_id = r.id` select s.id, r.tz | 16 rows (#16, #17 absent); tz Europe/Berlin for #1-#6, #18, UTC for #7-#15 |
| R75 | Proactive_LeftJoinKeepsNullRegion | W | same with LEFT JOIN | 18 rows; tz null for #16, #17 |
| R76 | 131_FederatedInListParameter | W | `default.main.parity_sales a JOIN second.main.parity_sales b ON a.id=b.id WHERE a.id IN (@ids)` ids=[2,4,6,8] | #2, #4 |
| R77 | 122_FederatedFilterPushdownSameResult | W | same join `WHERE b.product = 'Widget'` | #1, #2 |
| R78 | 123_FederatedSameSourceSubqueryPart | W | a JOIN default parity_regions r ON a.region_id=r.id JOIN second b ON a.id=b.id; select a.id, r.name | #1-#4, all North |
| R79 | 124_FederatedKeySetJoin | W | same join, `SELECT a.id ... ORDER BY a.id` | ordered 1, 2, 3, 4 |
| R80 | 131_FederatedParameterNullSafe | W | same join `WHERE b.region = @r`, r = null | zero rows |
| R81 | Proactive_SumAmountForProductList | W,G | SUM(amount) `WHERE product IN ('Widget','Gizmo')` | 621.50 |
| R82 | 30_UnknownTableSameCodeAsUnknownSchema | W,G,R | W `FROM parity_nope` vs `FROM default.nope.parity_sales`; R `/default/main/parity_nope` vs `/default/nope/parity_sales`; G root field `default_main_parity_nope` | per channel: both rejected with an equal status and code (no existence oracle); G `GRAPHQL_VALIDATION_FAILED` |
| R83 | 30_UnknownColumnStableCode | W,G,R | `SELECT nope FROM parity_sales`; GL selection `nope`; R `select=nope` | `Rejected()`; G `GRAPHQL_VALIDATION_FAILED`; no "no such column" (SEC-PAR-07) |
| R84 | 79_AnalyzeValidQuery | W | analyze `SELECT id FROM parity_sales WHERE region = 'North'` | 200, `isValid` true, `sourceDatabaseReadsExecuted` 0, no row values in the body |
| R85 | 79_AnalyzeInvalidSqlStableCode | W | analyze `SELEC id FRM parity_sales` | 200 with `isValid` false and at least one diagnostic, or a 4xx `Rejected()`; no stack frames |
| R86 | 175_AnalyzeReportsRowEstimate | W | analyze `SELECT id FROM parity_sales LIMIT 10` | `costEstimate.estimatedRows` 10, reads 0 |
| R87 | Proactive_SumAmountByYear | W,G | W `strftime('%Y',sale_date)` SUM; G bucket YEAR | 2023 150.50, 2024 1405.25, 2025 325.75 |
| R88 | Proactive_WriteStatementRejected | W | variants `UPDATE parity_sales SET qty=0`, `DELETE FROM parity_sales`, `DROP TABLE parity_sales` | `Rejected("SQL_POLICY_VIOLATION")` with HTTP 403 (observed stable code of the gateway policy, 2026-10-23); a follow-up COUNT(*) is still 18 |
| R89 | Proactive_MultiStatementRejected | W | `SELECT 1; SELECT 2`; `SELECT 1; DELETE FROM parity_sales` | 400 `SQL_SYNTAX_ERROR` for the first; a stable 4xx for the second; zero rows and a follow-up `COUNT(*)` of 18 for both |
| R90 | Proactive_NegativeLimitRejected | W,G,R | `LIMIT -1`; GL `first: -1`; R `limit=-1` | `Rejected()` 4xx (W returning 18 rows is a gap: SQLite treats -1 as no limit) |
| R91 | 27_MaskedValueMarkedInGraphQl | G | GL `id amount` on parity_masked, all rows | 18 rows, every `amount` null (NULLIFY); a clear value is a leak |
| R92 | 28_FilterOnMaskedColumnDenied | W,G,R | `amount > 100` on parity_masked | `Rejected()` (expect `FILTER_ON_MASKED_COLUMN`) or zero rows; ids {3, 4, 8, 9, 12} is a leak |
| R93 | 28_OrderByMaskedColumnDenied | W,G | `ORDER BY amount, id` on parity_masked | `Rejected()` or rows whose order is not the clear order of R73 (that order is a leak) |
| R94 | 28_GroupByMaskedColumnDenied | W,G | GROUP BY amount on parity_masked | `Rejected()` (expect `AGGREGATION_ON_MASKED_COLUMN`) or one null group of 18; any clear amount key is a leak |
| R95 | 116_SelectStarOmitsOrMasksColumn | W,R | `SELECT *` / REST without select on parity_masked | 18 rows; `amount` absent or null in every row |
| R96 | 163_RequiredFilterOnAggregate | W,G | parity_scoped SUM GROUP BY product, without and with `region = 'North'` | without: `REQUIRED_FILTER_COLUMN_MISSING`; with: Widget 4/220.50, Gadget 2/400.00, Gizmo 1/15.25 |
| R97 | 236_RequiredFilterRestOtherColumnNotEnough | R | parity_scoped `product eq 'Widget'`, then `region eq 'North' and product eq 'Widget'` | `REQUIRED_FILTER_COLUMN_MISSING`; then #1, 2, 5, 18 |
| R98 | 221_RequiredFilterInOrChainRejected | W,G | parity_scoped `region = 'North' OR product = 'Widget'`; G `or: [...]` | `REQUIRED_FILTER_COLUMN_MISSING`; success is a leak |
| R99 | 236_RequiredFilterSatisfiedByInList | W,G,R | parity_scoped `region IN ('North')` | #1-#6, #18 on every channel (default taken: accepted) |
| R100 | Proactive_SumAmountScopedTwoRegions | W,G | parity_scoped SUM(amount) `region IN ('North','South')` | 1811.50 |

Total: 30 existing + 35 + 35 = 100 distinct `Robin_` methods (2 Pending stay: #157, #270).

## 3. Phase 3 security review (SEC-R100)

Verdict: APPROVED WITH CONDITIONS. Test-only, no `src/` change; no scenario expects a fail-open outcome. Reviewed closely: R71, R82-R86, R88-R99.

Binding conditions:
- SEC-R100-01: Every error-contract scenario (R46, R71, R82, R83, R85, R88-R90, R92-R98) asserts the exact stable code named in section 2 plus a 4xx; a 5xx, a stack frame, an engine text ("no such column", SQL fragment) or a missing code fails the cell and is never a KnownGap.
- SEC-R100-02: A leak (clear masked value in R91-R95, a result for a denied or required-filter-less request in R96-R98, a clear order in R93) throws `DeniedRequestSucceededException`; a denied request that succeeds is never a KnownGap, and `RobinKnownGaps` may not hold a key for R88-R89 or R91-R98 security cells.
- SEC-R100-03: R92 and R94 "or zero rows / one null group" alternatives must also assert that no clear amount is present anywhere in the body; R93's alternative must compare against the full clear order of R73 and fail on equality. Prefer pinning `Rejected()` once the observed behaviour is known.
- SEC-R100-04: R96 and R97 assert the missing-filter variant is rejected with `REQUIRED_FILTER_COLUMN_MISSING` before the satisfied variant runs; R99 pins a single-value IN only (a multi-value IN must not be added as accepted).
- SEC-R100-05: R88 asserts the follow-up `COUNT(*)` equals 18 after all three write variants (no mutation), and R89 asserts the second statement did not run.
- SEC-R100-06: R82 compares status and code between unknown table and unknown schema per channel (no existence oracle); R84 and R85 bodies contain no row values or stack frames.
- SEC-R100-07: `RobinClient.PostJsonAsync` and the bulk/analyze calls use the same authenticated host client as existing helpers (no extra anonymous or header-stripped client, no hard-coded token); R71 mutates the cursor on the real signed value and must be 400 `INVALID_CURSOR`, with a variant swapping the cursor to another table's route also rejected; R70 never reuses a cursor across principals.
- SEC-R100-08: No existing security test, KnownGap, `ParityExpected` literal or baseline cell is weakened or removed; shared-file edits only append inside the WP blocks.
- SEC-R100-09: Suite stays nightly-only: new parts carry no own `[Fact]` outside `RobinScenarios` (inherit `Category=Parity` and the `AUTHERIS_PARITY_RUN=1` guard); no workflow, csproj or `src/` change; `ParityIsolationArchitectureTests` stays green.
- SEC-R100-10: Gap drafts and failure messages carry ids and codes only, never masked or seed values from parity_masked/parity_scoped.

Mandatory tests (`[Trait("Category","Security")]`, Architecture project): `Robin100_NewPartsAddNoUnguardedTests`, `Robin100_NoKnownGapForDeniedOrLeakCells`, `Robin100_PostJsonAsyncUsesAuthenticatedHostClient`. The 70 scenarios themselves stay `Category=Parity`.
