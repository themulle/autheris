# REQ-ROBIN-100: Robin parity suite expanded to 100 scenarios

Parent: [`2026-10-23-plan-nightly-parity-tests.md`](2026-10-23-plan-nightly-parity-tests.md) (D-17, section 7). Gap handling: [`2026-10-23-plan-parity-gap-fixes.md`](2026-10-23-plan-parity-gap-fixes.md).

## 1. Requirements

1. Owner decision: `tests/Autheris.Tests.Parity/Robin/RobinScenarios.cs` grows from 30 to 100 distinct scenario methods (70 new, ids R31..R100 below). Existing 30 are not repeated.
2. Source is Robin's (rgruebel) user-visible capability reports; where issues run out, `Robin_Proactive_<Slug>` variants in the same spirit.
3. Data comes from `ParitySeed` only (18 sales rows, 3 regions, `parity_scoped`, `parity_masked`, `parity_big` 1000 rows, source `second` = first 4 sales rows). New expected literals go to `ParityExpected` plus a LINQ check in `ParitySeed_ExpectedValuesMatchRows`. The architect hand-computes exact values.
4. Every scenario runs through `Run(channel, capability, body, variant)`; failures follow section 7 of the parent plan (`RobinKnownGaps`, new PARITY-GAP draft). A 5xx, leak or succeeded denied request is never a gap.
5. Error-contract scenarios assert stable fail-closed codes (`Rejected(code)`); no scenario weakens a security test or relaxes an allow-list.
6. Channels: W = WebSql, G = GraphQl, R = Rest. Seed row ids are written `#n`.

## 2. Catalogue (70 new scenarios)

Method name is `Robin_<issue>_<Slug>` or `Robin_Proactive_<Slug>`. Expected outcome is in terms of seed rows.

### A. Aggregation (#28, #112, #37, #280) - 10

| Id | Method | Issue | Ch | Expected |
|---|---|---|---|---|
| R31 | Robin_28_MinMaxAmount | 28 | W,G | MIN 10.00, MAX 400.00 over all rows (NULLs ignored). |
| R32 | Robin_28_CountStarVsCountColumn | 28 | W,G | COUNT(*)=18, COUNT(amount)=15, COUNT(note)=5 non-null notes. |
| R33 | Robin_112_SumByProduct | 112 | W,G | Widget 380.50, Gadget 1260.00, Gizmo 241.00. |
| R34 | Robin_112_CountByProductOrdered | 112 | W,G | Widget 8, Gadget 6, Gizmo 4, ordered by count desc. |
| R35 | Robin_112_SumByRegionNullGroup | 112 | W,G | NULL region group present with 2 rows, sum 70.00. |
| R36 | Robin_112_SumByRegionAndProduct | 112 | W,G | Two-key group; every (region,product) pair sums to the row total 1881.50. |
| R37 | Robin_280_AggregationWhereIsReturn | 280 | W,G | Filter `is_return = true` (#5,#9,#17): count 3, sum 490.00. |
| R38 | Robin_280_AggregationWhereBetweenDates | 280 | W,G | Year 2024 only: 8 rows, sum 1405.25. |
| R39 | Robin_37_SmallGroupHidden | 37 | W,G | With a min-group-size policy the 3-row Empty group is suppressed or flagged, groups >= threshold stay. Needs seed policy; flag as P2. |
| R40 | Robin_Proactive_HavingCountGreaterThan | - | W | HAVING COUNT(*) > 5: North 7, South 6; Empty excluded. |

### B. Date parts and time zones (#130, #36, #28) - 8

| Id | Method | Issue | Ch | Expected |
|---|---|---|---|---|
| R41 | Robin_Proactive_GroupByQuarter | - | W,G | 13 months fold to Q1..Q4 buckets per year; counts sum to 18. |
| R42 | Robin_Proactive_GroupByDay | - | W,G | 16 distinct `sale_date` days; 2024-01-10 and 2024-07-20 have 2 rows. |
| R43 | Robin_Proactive_GroupByHour | - | W,G | Hour buckets of `sold_at`; hour 00 holds #6,#13,#14,#15 = 4 rows. |
| R44 | Robin_130_ConvertTimezoneBerlinWinterSummer | 130 | W | #1 10:00Z -> 11:00 Berlin (CET), #8 15:45Z -> 17:45 (CEST). |
| R45 | Robin_130_LocalDayBoundaryNorth | 130 | W | #10 at 23:30Z on 12-31 is already 01-01 in Berlin; day moves for Berlin, not UTC. |
| R46 | Robin_130_UnknownTimeZoneRejected | 130 | W | Unknown zone name -> stable 4xx code, no 5xx. |
| R47 | Robin_36_TimestampFilterIso | 36 | W,G,R | `sold_at >= 2025-01-01T00:00:00Z`: #11..#18 = 8 rows. |
| R48 | Robin_Proactive_DateEqualsSingleDay | - | W,G,R | `sale_date = 2024-02-29` returns exactly #5 (leap day). |

### C. Null handling (#280, #28) - 7

| Id | Method | Issue | Ch | Expected |
|---|---|---|---|---|
| R49 | Robin_Proactive_IsNullAmount | - | W,G,R | `amount IS NULL` returns #13,#14,#15. |
| R50 | Robin_Proactive_IsNotNullRegion | - | W,G,R | 16 rows; #16,#17 excluded. |
| R51 | Robin_280_NeComparisonDropsNullRows | 280 | W,G | `region <> 'North'` returns South and Empty only (9 rows), NULL region excluded (3-valued). |
| R52 | Robin_Proactive_CoalesceAmountZero | - | W | `COALESCE(amount,0)` sum still 1881.50; #13..#15 show 0. |
| R53 | Robin_Proactive_NotInWithNullRows | - | W,G | `region NOT IN ('North')` excludes NULL-region rows. |
| R54 | Robin_Proactive_AvgIgnoresNull | - | W,G | AVG(amount) = 125.43333333333333 (15 rows, not 18). |
| R55 | Robin_Proactive_NullBoolIsReturn | - | W,G,R | `is_return IS NULL` returns #12..#15 (4 rows). |

### D. Filters, IN lists, LIKE (#128, #280) - 9

| Id | Method | Issue | Ch | Expected |
|---|---|---|---|---|
| R56 | Robin_128_InListStrings | 128 | W,G,R | `product IN ('Widget','Gizmo')`: 12 rows. |
| R57 | Robin_128_InListIntegers | 128 | W,G,R | `id IN (1,5,18,99)`: 3 rows, unknown id yields nothing. |
| R58 | Robin_128_InListDuplicatesCollapse | 128 | W,G | `id IN (1,1,1)` returns #1 once. |
| R59 | Robin_128_NotInListParameter | 128 | W,G | `product NOT IN ('Widget','Gizmo')` is the complement of R56: 6 Gadget rows. |
| R60 | Robin_128_InListWithApostropheValue | 128 | W,G | Parameter `it's` matches note of #4 only; no injection effect. |
| R61 | Robin_Proactive_LikePrefix | - | W,G,R | `note LIKE 'emoji%'` returns #8. |
| R62 | Robin_Proactive_LikeCaseSensitivity | - | W,G | `LIKE 'PLAIN'` documented behavior is identical on all channels (#1 or none, consistent). |
| R63 | Robin_Proactive_ContainsBackslash | - | W,G | note containing `\` matches #5 only. |
| R64 | Robin_Proactive_CombinedAndOrNesting | - | W,G,R | `(region='North' AND qty>=5) OR product='Gizmo'`: #1,#2,#6,#10,#12,#15 as hand-computed. |

### E. Sorting and paging (#39, #178, #222) - 9

| Id | Method | Issue | Ch | Expected |
|---|---|---|---|---|
| R65 | Robin_39_FirstPageOfBig | 39 | W,G,R | limit 100 offset 0 on `parity_big`: ids 1..100, flagged truncated. |
| R66 | Robin_39_LastPageOfBig | 39 | W,G,R | offset 900 limit 100: ids 901..1000. |
| R67 | Robin_39_OffsetBeyondEndEmpty | 39 | W,G,R | offset 1000: zero rows, not an error. |
| R68 | Robin_39_PagesAreDisjointAndComplete | 39 | W,G,R | Walk all pages: 1000 distinct ids, no duplicates. |
| R69 | Robin_39_OrderByDescPaging | 39 | W,G | `ORDER BY id DESC` limit 5: 1000..996. |
| R70 | Robin_178_BulkCursorContinuation | 178 | R | Bulk read returns continuation cursor; resumed stream completes all 1000 rows once. Only if the host exposes the bulk route. |
| R71 | Robin_222_TamperedCursorRejected | 222 | R | Modified cursor -> stable fail-closed code (signed cursor). |
| R72 | Robin_Proactive_OrderByTwoKeys | - | W,G | `ORDER BY region ASC, amount DESC`: deterministic row order matching the seed. |
| R73 | Robin_Proactive_OrderByNullPlacement | - | W,G | NULL amounts (#13..#15) sort at one documented end on all channels. |

### F. Joins and federation (#75, #122-124, #131, #132) - 8

| Id | Method | Issue | Ch | Expected |
|---|---|---|---|---|
| R74 | Robin_Proactive_JoinSalesToRegions | - | W | JOIN on `region_id`: 16 rows with `tz`; NULL-region rows #16,#17 drop (inner join). |
| R75 | Robin_Proactive_LeftJoinKeepsNullRegion | - | W | LEFT JOIN returns 18 rows, `tz` NULL for #16,#17. |
| R76 | Robin_131_FederatedInListParameter | 131 | W | Cross-source join with `IN (@a,@b)` over `main` and `second`; only the first 4 ids match. |
| R77 | Robin_122_FederatedFilterPushdownSameResult | 122 | W | Filter on `product = 'Widget'` gives the same rows as local evaluation (#1,#2). |
| R78 | Robin_123_FederatedSameSourceSubqueryPart | 123 | W | Join of two tables of one source inside a federated query returns the single-source answer. |
| R79 | Robin_124_FederatedKeySetJoin | 124 | W | Join on id between `main` and `second` returns 4 rows. |
| R80 | Robin_131_FederatedParameterNullSafe | 131 | W | Null-valued parameter in a federated filter gives zero rows. |
| R81 | Robin_75_RelationNestedRegionInGraphQl | 75 | G | Nested `region { name tz }` on sales rows resolves for #1; NULL-region rows give null relation. |

### G. Error contracts (#30, #79, #117, #175) - 9

| Id | Method | Issue | Ch | Expected |
|---|---|---|---|---|
| R82 | Robin_30_UnknownTableCodeEqualsDeniedTable | 30 | W,G,R | Unknown and denied table give the same stable code (no existence oracle). |
| R83 | Robin_30_UnknownColumnStableCode | 30 | W,G,R | Unknown column -> stable 4xx code on every channel, no internals. |
| R84 | Robin_79_AnalyzeValidQuery | 79 | W | Analysis of a valid query returns a report and zero data rows. |
| R85 | Robin_79_AnalyzeInvalidSqlStableCode | 79 | W | Syntax error -> stable code, message free of stack frames. |
| R86 | Robin_175_AnalyzeReportsRowEstimate | 175 | W | Estimate for `parity_sales` is a positive number; no rows returned. Only if host exposes analysis. |
| R87 | Robin_117_ExplainDoesNotNameDeniedColumns | 117 | W,G | Access explanation for a denied column does not reveal the column or the filter SQL. |
| R88 | Robin_Proactive_WriteStatementRejected | - | W | `UPDATE`/`DELETE`/`DROP` -> fail-closed policy code. |
| R89 | Robin_Proactive_MultiStatementRejected | - | W | `SELECT 1; SELECT 2` -> stable rejection. |
| R90 | Robin_Proactive_NegativeLimitRejected | - | W,G,R | `limit=-1` -> stable 400-class code, never 5xx. |

### H. Masking, visibility, required filters (#27, #28, #116, #143, #163, #221, #236) - 10

| Id | Method | Issue | Ch | Expected |
|---|---|---|---|---|
| R91 | Robin_27_MaskedValueMarkedInGraphQl | 27 | G | Masked `amount` of `parity_masked` carries the masked marker, not the real value. |
| R92 | Robin_28_FilterOnMaskedColumnDenied | 28 | W,G,R | `WHERE amount > 100` on `parity_masked` -> fail-closed code (no oracle through the filter). |
| R93 | Robin_28_OrderByMaskedColumnDenied | 28 | W,G | `ORDER BY amount` on masked table -> fail-closed code. |
| R94 | Robin_28_GroupByMaskedColumnDenied | 28 | W,G | Group by masked column -> fail-closed code. |
| R95 | Robin_116_SelectStarOmitsOrMasksColumn | 116 | W,R | `SELECT *` on `parity_masked` never contains the raw amount. |
| R96 | Robin_163_RequiredFilterOnAggregate | 163 | W,G | Aggregate on `parity_scoped` without `region` -> `REQUIRED_FILTER_MISSING`; with `region='North'` count 7. |
| R97 | Robin_236_RequiredFilterRestRead | 236 | R | REST read of `parity_scoped` without the filter -> stable code; with `region=North` 7 rows. |
| R98 | Robin_221_RequiredFilterInOrChainRejected | 221 | W,G | Required filter hidden inside an `OR` branch does not satisfy the requirement. |
| R99 | Robin_236_RequiredFilterSatisfiedByInList | 236 | W,G,R | `region IN ('North')` is accepted if policy allows; outcome identical on all channels. |
| R100 | Robin_143_ScopedColumnDenyAcrossChannels | 143 | W,G,R | A column denied for the test scope is absent or rejected identically on every channel. Needs scope-specific rule already in the host; else P2. |

## 3. Counts

| Category | New |
|---|---|
| A Aggregation | 10 |
| B Date parts and time zones | 8 |
| C Null handling | 7 |
| D Filters, IN, LIKE | 9 |
| E Sorting and paging | 9 |
| F Joins and federation | 8 |
| G Error contracts | 9 |
| H Masking, visibility, required filters | 10 |
| Total | 70 |

Issue-derived: 51. Proactive: 19. Issues covered: 27, 28, 30, 36, 37, 39, 75, 79, 112, 116, 117, 122, 123, 124, 128, 130, 131, 143, 163, 175, 178, 221, 222, 236, 280.

## 4. Out of scope (need new host variants or infrastructure)

1. #29, #34, #113, #226 access requests and approver inbox: need a second identity and request workflow in the host.
2. #158, #227 consent descriptions: consent store and approval flow absent.
3. #31 audit-log caller context: audit sink not observable from the parity host.
4. #129 row filters from caller attributes: needs per-person identity claims.
5. #153, #154 stored procedures: no procedure catalog in the host.
6. #38, #240 dbt metrics and `queryMetrics`: no semantic layer in the host.
7. #42, #115 off switches for OData, Arrow, Iceberg: need host variants with features disabled.
8. #41 Negotiate/Kerberos advertising: needs Kerberos configuration.
9. #44 pre-auth IP rate limit: needs rate-limit config and timing, flaky in nightly.
10. #146 multilingual display names: no localized catalog metadata.
11. #138, #80 catalog at 10k tables, value profiles: needs a large catalog variant (MCP not driven).
12. #279 GraphQL subscriptions for read-only tokens: no subscription channel.
13. #286 least-privilege runtime DB account: infrastructure test, not a query scenario.

Out-of-scope count: 13 groups (about 25 issues).

## 5. Open questions (recommended default)

1. R39, R70, R86, R100 depend on host features (min group size policy, bulk route, analysis route, scope rule). Default: architect verifies; if absent, replace with the reserve list `Robin_Proactive_Sum*` variants and note it in the changelog.
2. R62 LIKE case sensitivity differs by dialect. Default: assert channel equality on SQLite only, dialect job covers the rest.
3. R44/R45 require `convert_timezone`, which GAP-019 removes from SQLite. Default: mark WebSql cells `Unsupported(code)` on SQLite and run them in the PostgreSQL/SQL Server nightly job.
4. Naming collisions with the existing 30 are checked by a unique-method-name test; default: add `Robin_ScenarioCount_IsAtLeast100` meta test.

## 6. Acceptance criteria

1. `RobinScenarios.cs` contains 100 distinct `Robin_` methods (2 Pending, the rest runnable).
2. Every new expected literal exists in `ParityExpected` and is verified by LINQ in `ParitySeed_ExpectedValuesMatchRows`.
3. Filtered run `FullyQualifiedName~Robin` is green or reports only registered `KnownGap` cells; baseline updated.
4. No allow-list or security assertion is relaxed.
