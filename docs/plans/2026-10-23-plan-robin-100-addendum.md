# PLAN-ROBIN-100: Robin suite to 100 scenarios (Phase 2 addendum)

Input: [`2026-10-23-req-robin-100-scenarios.md`](2026-10-23-req-robin-100-scenarios.md). Parent: [`2026-10-23-plan-nightly-parity-tests.md`](2026-10-23-plan-nightly-parity-tests.md) (D-17, sections 5 and 7). Verified against `feat/robin-100` (base `fix/parity-gaps`). Test code only; no host variant, no `src/` change (SEC-PAR-03).

## 1. Decisions

1. **Host facts.** Min group size is lifted to 1 (`ParityHostSettings`), so R39 cannot be observed. No catalog relations, no denied table/column and no scope rule exist, so R81, R87 and R100 cannot be observed. Present and enabled by default: bulk route `POST /api/v1/data/{d}/{s}/{t}/bulk` (`BulkTableStreamRequest`: `select`, `cursor`, `limit`; `_meta.nextCursor`/`hasMore` line and `X-Next-Cursor` header; tampered cursor gives 400 `INVALID_CURSOR`) and analysis `POST /api/v1/governance/queries/analyze` (`{queries:[{sqlQuery,dataSource:"default"}]}` gives `validationReport.isValid`, `costEstimate.estimatedRows` (equal to an explicit LIMIT), `sourceDatabaseReadsExecuted`). R70, R71, R84-R86 stay.
2. **Swaps (reserve `Robin_Proactive_Sum*`).** R39 becomes `SumQtyByProduct`, R81 `SumAmountForProductList`, R87 `SumAmountByYear`, R100 `SumAmountScopedTwoRegions`. SQLite-only rewrites: R44 and R45 use GraphQL `timeBuckets.timeZone` (runs on SQLite) and WebSQL `strftime` in UTC. R46 is GraphQL only (on SQLite, WebSQL `convert_timezone` is always `SQL_POLICY_VIOLATION`, already covered by `Robin_130_TimeZoneConversionGroupByLocalDay`). Reshaped to avoid duplicating existing methods: R68 (page size 250, adds WebSql), R82 (unknown table vs unknown schema), R96 (grouped), R97 (other column is not enough). *Superseded in part by section 5 (Rework v2).*
3. **PRD corrections.** COUNT(note) is 7 (#1-#6, #8), not 5. The required-filter code is `REQUIRED_FILTER_COLUMN_MISSING`. R65 asserts ids, not a truncation flag (an explicit page is not truncated).
4. **Notation.** `W(sql)` = `Sql(...)`; `G(args, keys)` = `GqlAggregate`; `GL(args[, sel, table])` = `GqlList`; `R(f)` = `RestAsync(SalesRest + "?select=id&filter=" + Escape(f) + "&orderBy=id&limit=100")`; `O(q)` = GET `/odata/default/main/parity_sales?` + q (v2). Ids are an order-insensitive `IdsShouldBe` unless marked "ordered". "Leak" = throw `DeniedRequestSucceededException` (hard failure, never a gap). New expected literals go into `ParityExpected` with a LINQ line in `ParitySeed_ExpectedValuesMatchRows`.
5. **Files (D-17 kept).** Make `RobinScenarios` `public sealed partial class` (the same one-word change in both WPs, so it merges cleanly). Add new parts in `Robin/RobinScenarios.Wp1.cs` and `Robin/RobinScenarios.Wp2.cs` without repeating `[Trait]`/`[Collection]`/base list: `ParityIsolation_EveryParityTestClassDerivesFromBase` only scans the attributed part. Primary-constructor `host` is not in scope in other parts, so use `_robin`. WP-R2 adds `RobinClient.PostJsonAsync(path, body)` (raw status, body, headers; `Checked` applied).
6. **Shared files.** `RobinKnownGaps.cs`, `ParityExpected`, `ParitySeed_ExpectedValuesMatchRows`, `parity-baseline.json` and plan section 5: each WP appends only inside a `// --- WP-R1 ---` / `// --- WP-R2 ---` block (JSON: whole cells, no reordering). The lead merges and then adds `RobinSuite_HasExactly100DistinctScenarios` (Meta; it counts distinct `Robin_` methods on `RobinScenarios` by reflection, so it is red until both WPs are merged; its name must not start with `Robin_`).
7. **Gap protocol.** A failing functional cell gets `RobinKnownGaps["Method|Channel|Variant"] = "PARITY-GAP-NNN"` (WP-R1 030-049, WP-R2 050-069; highest existing id is 022) and a `### PARITY-GAP-NNN` draft in section 5 of the nightly plan (only that file is read by `ParityIsolation_KnownGapIdsHaveDrafts`; duplicate headings fail it). Never a gap: a 5xx, a leak, a denied request that succeeds (R92-R95, R98), or a security assertion (SEC-PAR-05).

## 2. Scenarios (method `Robin_<name>`; expected values hand-computed from the seed literals)

Final set after Rework v2. Rows marked `v2-xx` replace the original scenario; their query shape and expected values are in section 5.2. Base file `RobinScenarios.cs` (30 methods): `Proactive_GroupByColumnGraphQl`, `Proactive_SameLogicalQueryEqualRowsOnEveryChannel`, `157_ColumnlessCatalogTableDoesNotStopGateway` and `270_HmacPseudonymizedNonStringColumn` are replaced by v2-01, v2-29 (in Wp2), v2-02 and v2-03; the other 26 stay.

### WP-R1 (R31-R65, ~35 methods, ~700 LOC incl. expected literals)

| Id | Name | Ch | Query shape | Expected |
|---|---|---|---|---|
| R31 | v2-04 Proactive_AggregateErrorsStableCode | W,G | see 5.2 | see 5.2 |
| R32 | 28_CountStarVsCountColumn | W,G | `COUNT(*) n, COUNT(amount) na, COUNT(note) nn`; G COUNT, COUNT column amount, note | 18, 15, 7 |
| R33 | v2-05 112_CaseWhenBucketGroupBy | W | see 5.2 | see 5.2 |
| R34 | v2-06 Proactive_NotLikeAndNoMatchLike | W,G | see 5.2 | see 5.2 |
| R35 | v2-07 Proactive_ArithmeticAndCast | W | see 5.2 | see 5.2 |
| R36 | 112_SumByRegionAndProduct | W,G | GROUP BY region, product; G groupBy [region, product] | N-Wid 4/220.50, N-Gad 2/400.00, N-Giz 1/15.25, S-Wid 2/150.00, S-Gad 2/800.00, S-Giz 2/225.75, E-* 1/null each (3 groups), null-Wid 1/10.00, null-Gad 1/60.00; 11 groups, sums total 1881.50 |
| R37 | 280_AggregationWhereIsReturn | W,G | `WHERE is_return = 1` (W), `is_return: { eq: true }` | [3, 490.00] |
| R38 | 280_AggregationWhereBetweenDates | W,G | `sale_date >= '2024-01-01' AND sale_date < '2025-01-01'`; G gte/lt | [8, 1405.25] |
| R39 | v2-08 Proactive_SetOperations | W | see 5.2 | see 5.2 |
| R40 | Proactive_HavingCountGreaterThan | W | `GROUP BY region HAVING COUNT(*) > 5` | North 7, South 6 |
| R41 | Proactive_GroupByQuarter | W,G | W `strftime('%Y',sale_date)`, `(CAST(strftime('%m',sale_date) AS INTEGER)+2)/3`; G bucket QUARTER on sale_date | ordered 2023Q1 1, 2023Q2 1, 2024Q1 4, 2024Q2 1, 2024Q3 2, 2024Q4 1, 2025Q1 5, 2025Q2 3 (G key: first day of the quarter) |
| R42 | v2-09 130_MonthBucketBerlinYearEndAndDstHour | G | see 5.2 | see 5.2 |
| R43 | Proactive_GroupByHour | W,G | W `strftime('%H', sold_at)`; G bucket HOUR on sold_at (UTC) | 11 groups: 00 4, 05 1, 06 2, 07 1, 08 1, 09 1, 10 1, 11 1, 12 2, 15 2, 23 2 |
| R44 | 130_HourBucketBerlinWinterSummer | G (v2: W dropped) | `id IN (1,8)`; G HOUR bucket with `timeZone: "Europe/Berlin"` | G hours 11 (#1, CET) and 17 (#8, CEST) |
| R45 | 130_LocalDayBoundaryNorth | G (v2: W dropped) | `id IN (6,10)`; G DAY bucket with `timeZone: "UTC"` | 2024-03-31, 2024-12-31 (Berlin gives 2025-01-01 for #10, see the existing #130 scenario) |
| R46 | 130_UnknownTimeZoneRejected | G | DAY bucket with `timeZone: "Mars/Olympus"` | `Rejected()` (stable 4xx) |
| R47 | 36_TimestampFilterIso | W,G,R | `sold_at >= '2025-01-01T00:00:00Z'`; G gte; R `sold_at ge 2025-01-01T00:00:00Z` | #11-#18 |
| R48 | v2-10 Proactive_TypeMismatchStableCode | G,R | see 5.2 | see 5.2 |
| R49 | Proactive_IsNullAmount | W,G,R | `amount IS NULL`; G isNull; R `amount eq null` | #13, #14, #15 |
| R50 | Proactive_IsNotNullRegion | W,G,R | `region IS NOT NULL`; G `isNull: false`; R `region ne null` | #1-#15, #18 |
| R51 | 280_NeComparisonDropsNullRows | W,G | `region <> 'North'`; G neq | #7-#15 |
| R52 | Proactive_CoalesceAmountZero | W | `SELECT id, COALESCE(amount, 0) a ... WHERE id IN (12,13,14,15)` and `SUM(COALESCE(amount,0))` | 135.75, 0, 0, 0; sum 1881.50 |
| R53 | v2-11 Proactive_NotInWithNullIsEmpty | W,G | see 5.2 | see 5.2 |
| R54 | v2-12 Proactive_RowNumberOverPartition | W | see 5.2 | see 5.2 |
| R55 | v2-13 Proactive_NullSafeEquality | W | see 5.2 | see 5.2 |
| R56 | 128_InListStrings | W,G,R | `product IN ('Widget','Gizmo')`; G in; R `product in ('Widget','Gizmo')` | #1, 2, 5, 6, 7, 10, 11, 12, 13, 15, 16, 18 |
| R57 | v2-14 318_IntVariablesFirstOffset | G | see 5.2 | see 5.2 |
| R58 | v2-15 319_EmptyLogicalMembers | G | see 5.2 | see 5.2 |
| R59 | 128_NotInListParameter | W,G | W `product NOT IN (@p)` p=[Widget,Gizmo]; G `nin: $p` | #3, 4, 8, 9, 14, 17 |
| R60 | 128_InListWithApostropheValue | W,G | W `note IN (@n)` n=["it's"]; G `in: $n` | #4 |
| R61 | Proactive_LikePrefix | W,G,R | `note LIKE 'emoji%'`; G startsWith; R `startswith(note,'emoji')` | #8 |
| R62 | v2-16 Proactive_StringFunctions | W | see 5.2 | see 5.2 |
| R63 | Proactive_ContainsBackslash | W,G | W `instr(note, '\') > 0`; G `contains: "\\"` | #5 |
| R64 | Proactive_CombinedAndOrNesting | W,G,R | `(region='North' AND qty>=5) OR product='Gizmo'`; G or/and; R same with `and`/`or` | #1, 2, 6, 10, 12, 15 |
| R65 | v2-17 302_OffsetBoundaryAndFirstZero | W,G,R | see 5.2 | see 5.2 |

### WP-R2 (R66-R100, ~35 methods, ~800 LOC incl. `PostJsonAsync` and NDJSON parsing)

| Id | Name | Ch | Query shape | Expected |
|---|---|---|---|---|
| R66 | v2-18 Proactive_MalformedFilterStableCode | G,R | see 5.2 | see 5.2 |
| R67 | v2-19 128_InListOverCapStableCode | W | see 5.2 | see 5.2 |
| R68 | v2-20 Proactive_GraphQlVariablesAndDirectives | G | see 5.2 | see 5.2 |
| R69 | v2-21 116_UnmaskedColumnsOnMaskedTable | W,G,R | see 5.2 | see 5.2 |
| R70 | 178_BulkCursorContinuation | R | POST `.../parity_big/bulk` `{select:["id"],limit:400}`, resume with `cursor` until no `nextCursor` | slices 400/400/200; ids 1..1000 once; last `hasMore` false |
| R71 | 222_TamperedCursorRejected | R | first slice cursor with one base64 character changed; valid cursor on the parity_sales route | both 400 `INVALID_CURSOR` (v2) |
| R72 | Proactive_OrderByTwoKeys | W,G | `region IN ('North','South') ORDER BY region, amount DESC, id` | ordered 3, 4, 1, 2, 18, 5, 6, 8, 9, 12, 10, 11, 7 |
| R73 | Proactive_OrderByNullPlacement | W,G | `ORDER BY amount, id` (all 18) | W and G ordered 13, 14, 15, 16, 6, 5, 18, 2, 17, 7, 11, 10, 1, 12, 3, 4, 8, 9 (v2: channel parity, see 5.3) |
| R74 | Proactive_JoinSalesToRegions | W | `s JOIN parity_regions r ON s.region_id = r.id` select s.id, r.tz | 16 rows (#16, #17 absent); tz Europe/Berlin for #1-#6, #18, UTC for #7-#15 |
| R75 | Proactive_LeftJoinKeepsNullRegion | W | same with LEFT JOIN | 18 rows; tz null for #16, #17 |
| R76 | 131_FederatedInListParameter | W | `default.main.parity_sales a JOIN second.main.parity_sales b ON a.id=b.id WHERE a.id IN (@ids)` ids=[2,4,6,8] | #2, #4 |
| R77 | v2-22 Proactive_JoinWithZeroMatches | W | see 5.2 | see 5.2 |
| R78 | 123_FederatedSameSourceSubqueryPart | W | a JOIN default parity_regions r ON a.region_id=r.id JOIN second b ON a.id=b.id; select a.id, r.name | #1-#4, all North |
| R79 | v2-23 30_FederatedUnknownSourceSameCode | W | see 5.2 | see 5.2 |
| R80 | 131_FederatedParameterNullSafe | W | same join `WHERE b.region = @r`, r = null | zero rows |
| R81 | v2-24 Proactive_ScalarAndCorrelatedSubquery | W | see 5.2 | see 5.2 |
| R82 | 30_UnknownTableSameCodeAsUnknownSchema | W,G,R | W `FROM parity_nope` vs `FROM default.nope.parity_sales`; R `/default/main/parity_nope` vs `/default/nope/parity_sales`; G root field `default_main_parity_nope` | per channel equal status and code: W 403 `SQL_POLICY_DENIED`, R 404 `RESOURCE_NOT_FOUND`, G `GRAPHQL_VALIDATION_FAILED` |
| R83 | 30_UnknownColumnStableCode | W,G,R | `SELECT nope FROM parity_sales`; GL selection `nope`; R `select=nope` | W 403 `SQL_POLICY_VIOLATION`, G `GRAPHQL_VALIDATION_FAILED`, R 400 `INVALID_QUERY`; no "no such column" (SEC-PAR-07) |
| R84 | 79_AnalyzeValidQuery | W | analyze `SELECT id FROM parity_sales WHERE region = 'North'` | 200, `isValid` true, `sourceDatabaseReadsExecuted` 0, no row values in the body |
| R85 | 79_AnalyzeInvalidSqlStableCode | W | analyze `SELEC id FRM parity_sales` | 200, `isValid` false, a diagnostic with code `SYNTAX_ERROR`; no stack frames (v2: the 4xx branch is a failure) |
| R86 | 175_AnalyzeReportsRowEstimate | W | analyze `SELECT id FROM parity_sales LIMIT 10` | `costEstimate.estimatedRows` 10, reads 0 |
| R87 | v2-25 277_CteAndRecursiveGate | W | see 5.2 | see 5.2 |
| R88 | Proactive_WriteStatementRejected | W | variants `UPDATE parity_sales SET qty=0`, `DELETE FROM parity_sales`, `DROP TABLE parity_sales` | security cell: 403, follow-up COUNT(*) 18; code cell: `SQL_STATEMENT_NOT_PERMITTED` (v2, see 5.3) |
| R89 | Proactive_MultiStatementRejected | W | `SELECT 1; SELECT 2`; `SELECT 1; DELETE FROM parity_sales` | 400 `SQL_SYNTAX_ERROR` for both; zero rows and a follow-up `COUNT(*)` of 18 |
| R90 | Proactive_NegativeLimitRejected | W,G,R | `LIMIT -1`; GL `first: -1`; R `limit=-1` | W 400 `SQL_SYNTAX_ERROR`, G `INVALID_QUERY`, R 400 `INVALID_QUERY` |
| R91 | 27_MaskedValueMarkedInGraphQl | G | GL `id amount` on parity_masked, all rows | 18 rows, every `amount` null (NULLIFY); a clear value is a leak |
| R92 | 28_FilterOnMaskedColumnDenied | W,G,R | `amount > 100` on parity_masked | W 403 `SQL_POLICY_VIOLATION`, G `INVALID_QUERY`, R 400 `INVALID_QUERY`; ids {3, 4, 8, 9, 12} is a leak |
| R93 | 28_OrderByMaskedColumnDenied | W,G | `ORDER BY amount, id` on parity_masked | W 403 `SQL_POLICY_VIOLATION`, G `INVALID_QUERY`; the clear order of R73 is a leak |
| R94 | 28_GroupByMaskedColumnDenied | W,G | GROUP BY amount on parity_masked | W 403 and G both `AGGREGATION_ON_MASKED_COLUMN`; any clear amount key is a leak |
| R95 | 116_SelectStarOmitsOrMasksColumn | W,R | `SELECT *` / REST without select on parity_masked | 18 rows; `amount` absent or null in every row |
| R96 | v2-26 Proactive_UnauthenticatedRequestRejected | W,G,R,OData | see 5.2 | see 5.2 |
| R97 | 236_RequiredFilterRestOtherColumnNotEnough | R | parity_scoped `product eq 'Widget'`, then `region eq 'North' and product eq 'Widget'` | `REQUIRED_FILTER_COLUMN_MISSING`; then #1, 2, 5, 18 |
| R98 | 221_RequiredFilterInOrChainRejected | W,G | parity_scoped `region = 'North' OR product = 'Widget'`; G `or: [...]` | `REQUIRED_FILTER_COLUMN_MISSING`; success is a leak |
| R99 | 236_RequiredFilterSatisfiedByInList | W,G,R | parity_scoped `region IN ('North')` | #1-#6, #18 on every channel (default taken: accepted) |
| R100 | v2-27 163_GraphQlAliasesOnScopedTable | G | see 5.2 | see 5.2 |
| - | v2-29 Proactive_ODataFilterMatchesRest (base-file slot of SameLogicalQuery) | OData,R | see 5.2 | see 5.2 |

Total: 26 base + 4 base replacements (v2-01..03, v2-29) + 35 + 35 = 100 distinct `Robin_` methods; no Pending cell remains (#157, #270 stay open issues, see 5.1).

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

## 4. Delivery result (2026-10-23, feat/robin-100)

- 100 distinct `Robin_*` methods (guarded by `RobinSuite_HasExactly100DistinctScenarios`, Architecture project). Local SQLite run (`Category=Parity&Dialect!=Container`): 228 Robin cells, 218 pass, 8 known-gap cells, 2 Pending (#157, #270), 0 fail. `parity-baseline.json` updated (138 new pass cells, none removed).
- New known gaps (drafts in section 5 of the nightly parity plan): PARITY-GAP-030 (REST translator rejects OData `startswith`), 033 (REST `eq null` / `ne null` return zero rows), 034 (GraphQL QUARTER bucket `AGGREGATION_RESULT_TYPE_MISMATCH`), 035 (GraphQL HOUR bucket ignores the summer offset of `timeZone`), 050 (WebSQL federated join chain order gives `SQL_SYNTAX_ERROR`). Ids 031, 032, 051, 052 were test bugs and are unused.
- Test-fixture fix: `ParityHostFixture.RegisterCatalogAsync` declares key `id` and Full visibility for `parity_sales` and `parity_big` (bulk route and analyze route need them); masked, scoped and second-source tables are unchanged.
- Decisions taken: R88 asserts the observed 403 `SQL_POLICY_VIOLATION`; R46 asserts GraphQL `INVALID_QUERY` (HTTP 200 per GraphQL convention). Open Low: R89 write variant (`SELECT 1; DELETE ...`) accepts any stable 4xx code; the no-mutation COUNT check holds.

## 5. Rework v2 (Opus review CHANGES REQUIRED, 2026-10-23)

Codes below are derived from the source mapping, not from runs: WebSQL via `SqlCompilerErrorMapper` (`SecurityException` 403 `SQL_POLICY_VIOLATION`, `WebSqlTableDeniedException` 403 `SQL_POLICY_DENIED`, `MaskedAggregationException` 403 `AGGREGATION_ON_MASKED_COLUMN`, `AggregateTypeInvalidException` 400, `Construct` 422 `SQL_CONSTRUCT_NOT_SUPPORTED`, `StatementClass` 403 `SQL_STATEMENT_NOT_PERMITTED`, list too large 400 `SQL_LIST_PARAM_TOO_LARGE`); REST via `DatasetDataEndpoints` (`ArgumentException`/invalid query 400 `INVALID_QUERY`, `TableNotFoundException` 404 `RESOURCE_NOT_FOUND`); GraphQL list via `CatalogGraphQlTypeModule` (`GatewayInvalidQueryException` gives `INVALID_QUERY`, also for page size and offset), enum/type errors `GRAPHQL_VALIDATION_FAILED`, document syntax `GRAPHQL_PARSE_FAILED`. The developer confirms each mapping row by grep before Red; a plan/source mismatch goes to the lead (plan fix), never to the observed output.

### 5.1 Host support (verified on `feat/robin-100`, head 994c321e)

1. **OData:** enabled by default (`ODataOptions.Enabled = true`), `GET /odata/{domain}/{schema}/{table}` with `RequireAuthorization()`; runnable (v2-26, v2-29).
2. **MCP:** `McpOptions.Enabled = false`, not set by `ParityHostSettings`; enabling it is a host variant (SEC-PAR-03). Not runnable; OData is the nearest variant for the SameLogicalQuery swap.
3. **Relations:** the fixture registers no relation (`CatalogRelationRegistry` is empty for the parity tables). Nested relation scenarios are not runnable and are not added (registering a relation would change the catalog of every scenario).
4. **WITH RECURSIVE (#277):** `WebSql.AllowRecursiveCte = false` and not set; only the gated rejection and non-recursive CTEs are runnable (v2-25). A recursive series needs a host variant: not taken.
5. **Window functions and set operations:** parsed and guarded by `AstSecurityVisitor` (window calls go through `CheckAggregationScope`); runnable on SQLite.
6. **Unauthenticated:** `FallbackPolicy` requires an authenticated user; `TestAuthHandler` returns `NoResult` without `X-Test-User-Sid`, so 401 is runnable. **Wrong scope:** no read path in the parity host is gated by a token scope (`X-Test-Scope` only feeds REQ-GOV-143 restricted-scope masking, and parity_masked is masked for everyone); a 403 wrong-scope cell is not runnable. Nearest variant: unauthenticated on four channels.
7. **Denied table / #157 / #270:** no denied catalog table, no columnless table and no HMAC rule exist; nearest runnable variants are an unregistered physical table (`sqlite_master`) and a masked column reached through a join (v2-02, v2-03). #157 and #270 stay open issues outside this suite.

### 5.2 Swap table (old -> new; seed values from `ParitySeed.Sales`)

| v2 | Old -> new `Robin_*` | WP / file | Ch | Query shape | Expected |
|---|---|---|---|---|---|
| 01 | Proactive_GroupByColumnGraphQl -> 27_FragmentsKeepMasking | a / RobinScenarios.cs | G | parity_masked, all rows: named `fragment F on T { id amount }` and inline `... on T { amount }` (T from `__typename` of a first call) | 18 rows each, `amount` null; a clear value is a leak |
| 02 | 157_ColumnlessCatalogTable... -> 30_UnregisteredSourceTableDenied | a / RobinScenarios.cs | W,R | W `SELECT name FROM sqlite_master` vs `SELECT id FROM parity_nope`; W `SELECT * FROM pragma_table_info('parity_sales')`; R `/api/v1/data/default/main/sqlite_master?select=name` | W 403 `SQL_POLICY_DENIED`, equal status and code to the unknown table; pragma 403 `SQL_POLICY_VIOLATION`; R 404 `RESOURCE_NOT_FOUND`; any row is a leak |
| 03 | 270_HmacPseudonymized... -> 28_MaskedColumnThroughJoin | a / RobinScenarios.cs | W | `SELECT s.id, m.amount FROM parity_sales s JOIN parity_masked m ON s.id = m.id WHERE s.id IN (3, 8) ORDER BY s.id`; variant `ON s.amount = m.amount` | ordered (3, null), (8, null); 200.00/400.00 is a leak; ON variant 403 `SQL_POLICY_VIOLATION` |
| 04 | 28_MinMaxAmount -> Proactive_AggregateErrorsStableCode | a / Wp1 | W,G | variants `sum_string` (W `SUM(product)`, G `{ fn: SUM, column: product }`), `unknown_function` (W `NOPE_FN(amount)`, G `fn: NOPE`), `unknown_group_column` (W `GROUP BY nope`, G `groupBy: [nope]`) | W 400 `AGGREGATION_INVALID_FUNCTION_FOR_TYPE`, 403 `SQL_POLICY_VIOLATION`, R83 W code; G `AGGREGATION_INVALID_FUNCTION_FOR_TYPE`, `GRAPHQL_VALIDATION_FAILED`, `GRAPHQL_VALIDATION_FAILED` |
| 05 | 112_SumByProduct -> 112_CaseWhenBucketGroupBy | a / Wp1 | W | `SELECT CASE WHEN amount IS NULL THEN 'none' WHEN amount < 100 THEN 'small' ELSE 'large' END AS b, COUNT(*) n, SUM(amount) s ... GROUP BY <same CASE> ORDER BY b` | ordered large 6/1435.75, none 3/null, small 9/445.75 |
| 06 | 112_CountByProductOrdered -> Proactive_NotLikeAndNoMatchLike (also add) | a / Wp1 | W,G | `note NOT LIKE 'emoji%'`; `note LIKE '%zzz%'`; G `not: { note: { startsWith: "emoji" } }`, `note: { contains: "zzz" }` | #1-#6 (null notes excluded, 3VL); zero rows |
| 07 | 112_SumByRegionNullGroup -> Proactive_ArithmeticAndCast | a / Wp1 | W | `SELECT id, qty * amount qa, CAST(qty AS VARCHAR) qs, amount + 0.25 a2 ... WHERE id IN (1, 12, 13) ORDER BY id`; `SUM(qty * amount)` | (1, 1000.00, "10", 100.25), (12, 1221.75, "9", 136.00), (13, null, null, null); sum 8599.50 |
| 08 | Proactive_SumQtyByProduct -> Proactive_SetOperations | a / Wp1 | W | A = `SELECT id ... WHERE region = 'North'`, B = `... WHERE product = 'Widget'`; `A UNION ALL B`, `UNION`, `EXCEPT`, `INTERSECT`; `SELECT id ... UNION SELECT id, qty ...` | multiset 1,1,2,2,3,4,5,5,6,7,11,13,16,18,18; 1-7, 11, 13, 16, 18; 3, 4, 6; 1, 2, 5, 18; mismatch 400 `SQL_SYNTAX_ERROR` |
| 09 | Proactive_GroupByDay -> 130_MonthBucketBerlinYearEndAndDstHour | a / Wp1 | G | `id in [10, 11]` MONTH bucket on sold_at with `timeZone` Europe/Berlin and UTC; `id in [6]` HOUR bucket Europe/Berlin | Berlin one group 2025-01 n 2; UTC 2024-12 n 1, 2025-01 n 1; #6 (00:30Z, CET before the 01:00Z switch) hour 01 on 2024-03-31, n 1 |
| 10 | Proactive_DateEqualsSingleDay -> Proactive_TypeMismatchStableCode | a / Wp1 | G,R | GL `first: "10"`; GL `where: { id: { eq: "x" } }`; R `filter=region eq` (dangling) | G `GRAPHQL_VALIDATION_FAILED` both; R 400 `INVALID_QUERY` |
| 11 | Proactive_NotInWithNullRows -> Proactive_NotInWithNullIsEmpty | a / Wp1 | W,G | `region NOT IN ('North', NULL)`; G `region: { nin: ["North", null] }` | zero rows (3VL: every row is NULL or false) |
| 12 | Proactive_AvgIgnoresNull -> Proactive_RowNumberOverPartition | a / Wp1 | W | `SELECT id, ROW_NUMBER() OVER (PARTITION BY region ORDER BY id) rn FROM parity_sales ORDER BY id`; masked variant `ROW_NUMBER() OVER (ORDER BY amount)` on parity_masked | rn by id: 1-6 -> 1-6, 7-12 -> 1-6, 13-15 -> 1-3, 16 -> 1, 17 -> 2, 18 -> 7; masked 403 `AGGREGATION_ON_MASKED_COLUMN` (security cell) |
| 13 | Proactive_NullBoolIsReturn -> Proactive_NullSafeEquality | a / Wp1 | W | `region IS NOT DISTINCT FROM @r` r = null and r = "South"; `region IS DISTINCT FROM @r` r = null | #16, #17; #7-#12; #1-#15, #18 |
| 14 | 128_InListIntegers -> 318_IntVariablesFirstOffset | a / Wp1 | G | `query($f: Int, $o: Int) { default_main_parity_sales(first: $f, offset: $o, orderBy: [{ id: ASC }]) { id } }` with (5, 10), (null, null), (0, null) | ordered 11-15; equal to the same query without `first`/`offset` (1-18); zero rows. All 18 rows for (5, 10) is #318 |
| 15 | 128_InListDuplicatesCollapse -> 319_EmptyLogicalMembers | a / Wp1 | G | `where: { or: [{}, { region: { eq: "North" } }] }`; `where: { and: [] }`; `where: { not: {} }` | 18 rows; 18 rows; zero rows (`{}` is true). North-only for `or` is #319 |
| 16 | Proactive_LikeCaseSensitivity -> Proactive_StringFunctions | a / Wp1 | W | `SELECT id, UPPER(note) u, LENGTH(note) l, SUBSTR(product, 1, 3) p3, product \|\| '-' \|\| region pr, TRIM('  ' \|\| product \|\| '  ') t ... WHERE id IN (1, 8) ORDER BY id` | (1, "PLAIN", 5, "Wid", "Widget-North", "Widget"), (8, "EMOJI 😀", 7, "Gad", "Gadget-South", "Gadget") |
| 17 | 39_FirstPageOfBig -> 302_OffsetBoundaryAndFirstZero | a / Wp1 | W,G,R | parity_big ordered by id: `offset_max` limit 10 offset 10000; `offset_over` offset 10001; `first_zero` W `LIMIT 0`, GL `first: 0`, R `limit=0` | offset_max 200 zero rows; offset_over W zero rows, G `INVALID_QUERY`, R 400 `INVALID_QUERY`; first_zero 200 zero rows (#302) |
| 18 | 39_LastPageOfBig -> Proactive_MalformedFilterStableCode (also add) | b / Wp2 | G,R | G `{ default_main_parity_sales(first: 1) { id }` (unbalanced); GL `where: { region: { like: "x" } }`; R `filter=(region eq 'North'`; R `filter=nope(region)` | `GRAPHQL_PARSE_FAILED`; `GRAPHQL_VALIDATION_FAILED`; 400 `INVALID_QUERY` both |
| 19 | 39_OffsetBeyondEndEmpty -> 128_InListOverCapStableCode (also add) | b / Wp2 | W | parity_big `id IN (@ids)` ids = 1..1000 and 1..1001 | 1000 rows; 400 `SQL_LIST_PARAM_TOO_LARGE` (`MaxListParameterValues` 1000) |
| 20 | 39_PagesAreDisjointAndComplete -> Proactive_GraphQlVariablesAndDirectives | b / Wp2 | G | `query($w: <where type>) { ...(where: $w, first: 100) { id } }` w = `{ region: { eq: "North" } }` (type name from `__type` of the `where` argument); parity_masked `amount @include(if: true)` and `amount @skip(if: true)` | #1-#6, #18; 18 rows with `amount` null; 18 rows without an `amount` key; a clear value is a leak |
| 21 | 39_OrderByDescPaging -> 116_UnmaskedColumnsOnMaskedTable (also add) | b / Wp2 | W,G,R | parity_masked `id, region, product, qty WHERE id IN (1, 2)`; filter `region = 'South'` | (1, North, Widget, 10), (2, North, Widget, 5); #7-#12 |
| 22 | 122_FederatedFilterPushdownSameResult -> Proactive_JoinWithZeroMatches (also add) | b / Wp2 | W | `s JOIN parity_regions r ON s.region_id = r.id WHERE r.name = 'Nope'`; federated `a JOIN second b ON a.id = b.id WHERE b.id > 4` | 200, zero rows, columns present, both |
| 23 | 124_FederatedKeySetJoin -> 30_FederatedUnknownSourceSameCode | b / Wp2 | W | `default.main.parity_sales a JOIN nope.main.parity_sales b ON a.id = b.id` vs `... JOIN second.main.parity_nope b ...`; `SELECT id FROM nope.main.parity_sales` vs `FROM parity_nope` | all 403 `SQL_POLICY_DENIED`, equal status and code per pair (no existence oracle) |
| 24 | Proactive_SumAmountForProductList -> Proactive_ScalarAndCorrelatedSubquery | b / Wp2 | W | `amount > (SELECT AVG(amount) FROM parity_sales)`; `EXISTS (SELECT 1 FROM parity_regions r WHERE r.id = s.region_id AND r.tz = 'UTC')` | #3, 4, 8, 9, 12 (AVG 125.43); #7-#15 |
| 25 | Proactive_SumAmountByYear -> 277_CteAndRecursiveGate | b / Wp2 | W | `WITH n AS (SELECT id FROM parity_sales WHERE region = 'North') SELECT id FROM n ORDER BY id`; `WITH RECURSIVE s(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM s WHERE x < 5) SELECT x FROM s`; self-reference without RECURSIVE | ordered 1-6, 18; 422 `SQL_CONSTRUCT_NOT_SUPPORTED` (gate off, security cell); 422 `SQL_CONSTRUCT_NOT_SUPPORTED` |
| 26 | 163_RequiredFilterOnAggregate -> Proactive_UnauthenticatedRequestRejected | b / Wp2 | W,G,R,OData | client without `X-Test-*` headers: W `SELECT id FROM parity_sales`, GL `first: 1`, R `select=id`, O `$select=id` | 401, no row, on every channel (security cell) |
| 27 | Proactive_SumAmountScopedTwoRegions -> 163_GraphQlAliasesOnScopedTable | b / Wp2 | G | `north: default_main_parity_scoped(where: { region: { eq: "North" } }) { id } south: ...(eq "South")`; variant `north` + `all: default_main_parity_scoped(first: 100) { id }` | north #1-#6, #18, south #7-#12; variant: error `REQUIRED_FILTER_COLUMN_MISSING`, `data` null for both aliases; a row under `all` is a leak, `north` data present is a KnownGap |
| 29 | Proactive_SameLogicalQueryEqualRowsOnEveryChannel (deleted by a) -> Proactive_ODataFilterMatchesRest | b / Wp2 | OData,R | O `$filter=region eq 'North'&$select=id&$orderby=id`; O `$filter=amount gt 100&$top=3&$orderby=id`; R same filters | ordered #1-#6, #18; ordered #3, #4, #8; equal on both channels |

### 5.3 Assertion fixes (B)

1. Wp2.cs:442, 443, 460, 473: `FindProperty(...) ?? throw new XunitException("<name> missing")` before `ShouldBe` (R84 `isValid` true, reads 0; R86 `estimatedRows` 10, reads 0).
2. Pinned codes (no `Rejected()` without a code, no "error or zero rows" branch): R83 (:417) and R92-R94 (:558, :582, :605) as in section 2; R85 (:452) only the 200 branch with `isValid` false and a `SYNTAX_ERROR` diagnostic; R89 write (:495) 400 `SQL_SYNTAX_ERROR`; R90 (:516) as in section 2; R71 (:243) other-table cursor 400 `INVALID_CURSOR`; AggregateOverMasked (RobinScenarios.cs:459) W 403 and G `AGGREGATION_ON_MASKED_COLUMN`.
3. PageAboveCap (RobinScenarios.cs:326-336): W 200, ordered 1..1000, WebSQL `truncated` false (`MaxRows` 5000 > 1000); G `INVALID_QUERY` (page size above `MaxAllowedLimit`); R ordered 1..1000 and `RobinClient.IsTruncatedSignal` false (`DefaultMaxRows` 1000). No body substring. FirstPageOfBig is replaced (v2-17).
4. R73 (Wp2.cs:272): G must equal the W order (channel parity: both push ORDER BY to the same SQLite source, NULLs first for ASC); the "Observed" comment goes.
5. R88 (Wp2.cs:487): keep the security cell (403 + COUNT 18, never a gap) and add a code cell variant `<variant>-code` expecting `SQL_STATEMENT_NOT_PERMITTED` (dedicated `StatementClass` row); `SQL_POLICY_VIOLATION` there is a KnownGap (070). (v2 has no id 28; R96's required-filter coverage stays on R97-R99 and the `163_RequiredFilter*` base methods.)
6. R44/R45 (Wp1.cs:243-247, 263-267): remove the WebSql `InlineData` (they only projected a UTC `strftime`; WebSQL time zones on SQLite are covered by `Robin_130_TimeZoneConversionGroupByLocalDay`). Their two baseline cells are removed with a note (mislabelled cells, not a weakening).
7. GroupByYearAndMonth (RobinScenarios.cs:140-150): full ordered rows (y, m as int): 2023/3 1, 2023/6 1, 2024/1 2, 2024/2 1, 2024/3 1, 2024/5 1, 2024/7 2, 2024/12 1, 2025/1 1, 2025/2 1, 2025/3 3, 2025/4 2, 2025/5 1. GroupByDatePart (:151-159): MONTH the same 13 rows keyed by the first day of the month; DAY 16 rows keyed by the distinct `sale_date`, n 2 for 2024-01-10 and 2024-07-20, else 1.
8. AggregationWhereEqualsValue (RobinScenarios.cs:94): the Rest cell runs with capability `list_where_eq` (ids #1-#6, #18); the old baseline cell is renamed.
9. Gap pattern: a non-leaking rejection with another stable code in a non-security code cell (R46, R71, R82, R83, R85, R89, R90, v2-04, 08, 10, 17, 18, 19, R88 code cell) may be a KnownGap; security cells (R88 security, R91-R98, v2-01, 02, 03, 12 masked, 25 gate, 26, 27 `all` alias) never are. Needs a Phase 3 amendment of SEC-R100-01/-02 and of SEC-R100-07 (v2-26 uses `RobinClient.CreateUnauthenticatedClient`, only there; `Robin100_PostJsonAsyncUsesAuthenticatedHostClient` gets that single allowance).

### 5.4 Work packages

- **WP-V2a** owns `RobinScenarios.cs` and `RobinScenarios.Wp1.cs`: v2-01..17, B3, B6-B8, AggregateOverMasked; deletes 18 methods, adds 17 (SameLogicalQuery is deleted without a local replacement). Removes the gap key `NullBoolIsReturn|Rest` (method gone). Gap ids 051-069, assigned in order only to red cells; candidates: 051 v2-11 G, 052 #318, 053 #319, 054 #302 / offset cap, 055 window, 056 null-safe equality, 057 set-op code, 058 v2-06 G 3VL, 059 PageAboveCap R, 060 v2-09.
- **WP-V2b** owns `RobinScenarios.Wp2.cs` and `RobinClient.cs` (adds `CreateUnauthenticatedClient()` and `ODataAsync(path)`, which parses `value` and maps `error.code`; `Run` accepts channel `OData`): v2-18..27, v2-29, B1, B2 (Wp2 part), B4, B5; deletes 10, adds 11. Removes the gap key `124_FederatedKeySetJoin|WebSql` (050 stays for R78). Gap ids 070-089; candidates: 070 R88 code, 071 v2-27 partial data, 072 v2-29, 073 R90 W, 074 R73 G, 075 v2-20, 076 v2-24, 077 v2-25 CTE, 078 v2-18, 079 v2-21.
- **Shared bookkeeping** (`RobinKnownGaps.cs`, this section, `ParityExpected`, `ParitySeed_ExpectedValuesMatchRows`, `parity-baseline.json`, gap drafts in the nightly plan): edits only inside `// --- WP-V2a ---` / `// --- WP-V2b ---` blocks. `RobinSuite_HasExactly100DistinctScenarios` is green only after both merge (-18 +17 -10 +11 = 100). `Robin100_NoKnownGapForDeniedOrLeakCells` gets the new security cells of 5.3 item 9.

## 6. Phase 3 amendment for rework v2 (2026-10-23)

Verdict: APPROVED WITH CONDITIONS. Test-only, no `src/` change; no v2 scenario expects a fail-open outcome. Lead decision: R88 is NOT split. It stays pinned to 403 `SQL_POLICY_VIOLATION` with the COUNT 18 check; section 5.3 item 5 (code cell, gap 070) is void and 070 is not reserved for it.

- SEC-R100-11 (amends -01/-02): A KnownGap for a different stable code is allowed only in the non-security cells listed in 5.3 item 9 (R88 code cell excluded; closed list). The cell must show a 4xx, a non-empty stable code (upper snake case), no row or clear value anywhere in the body, no stack frame and no engine text; a 5xx, a 2xx for a request that must be rejected, or any leak is never a gap. The gap draft records the expected and the observed code only. A guard test fails if a gap key exists outside the closed list; the closed list is the 5.3 item 9 cells plus the four explicit outcome gaps of SEC-R100-19.
- SEC-R100-12 (amends -07): `RobinClient.CreateUnauthenticatedClient()` is the only anonymous client. It sends no `X-Test-*` header and no token, is used only by v2-26, only asserts exactly 401 (no redirect, no 200), and is never used for a positive read or a baseline. v2-26 asserts 401 with no row and no seed value for W, G, R and OData; each channel is its own cell and none is a gap. `Robin100_PostJsonAsyncUsesAuthenticatedHostClient` allows exactly this one call site.
- SEC-R100-13: v2-23 and v2-02 compare status, code and the body shape per pair (unknown source vs unknown table, `sqlite_master` vs `parity_nope`); the message must not state whether a source or table exists. All cells are security cells, never gaps. The pragma and `sqlite_master` variants must also return no row.
- SEC-R100-14: v2-27 `all` alias: any row under `all`, or a 200 without an `errors` entry, throws `DeniedRequestSucceededException`. The only allowed gap is partial `north` data together with `REQUIRED_FILTER_COLUMN_MISSING` and a null `all`. Also assert the single-alias unfiltered request is rejected with the same code.
- SEC-R100-15: v2-01 and v2-20 (fragments, `@include`, `@skip`, variables): besides `amount` null, scan the raw body for every clear `amount` of the parity_masked seed (as a number and as a string); any hit is a leak. Add the alias form `a: amount` to v2-01. The `__typename` and `__type` lookups use the authenticated client.
- SEC-R100-16: v2-03 and v2-12: the join returns null for the masked column (200.00/400.00 is a leak); the `ON s.amount = m.amount` and the masked window variant are 403 security cells, never gaps. No scenario may expect a 200 for a filter, join key, ORDER BY or window on a masked column.
- SEC-R100-17: v2-18 and v2-10 (malformed filter, parse error): 4xx with a stable code, no stack frame, no parser or engine text; a 5xx fails and is never a gap. v2-19: 1000 values accepted, 1001 rejected with 400 `SQL_LIST_PARAM_TOO_LARGE`; a 200 for 1001 values is a failure (DoS bound), not a KnownGap.
- SEC-R100-19: Functional outcome gaps (wrong rows, or a different rejection or acceptance, without a stable-code match) are allowed only for exactly four cells: PARITY-GAP-051 (v2-11 G, `nin` with null), 052 (#318, Int variables for `first`/`offset`), 053 (#319, empty `or` member) and 054 (R `offset=10001` returns 200 with zero rows). Conditions: the table is public (`parity_sales` or `parity_big`, never `parity_masked`, `parity_scoped` or a denied or required-filter table); the observed rows are a subset of the rows the caller may see for an unmodified request (no extra, no clear masked value, no row from another scope); no 5xx, stack frame or engine text; the gap records scenario, channel, expected and observed outcome as ids, counts and codes only. The outcome must stay equal to the recorded observation: a changed outcome fails the cell, so a gap cannot hide a new leak. Any other functional gap needs a new amendment. 054 stays a gap only while the body has zero rows (offset beyond the cap must never return rows).
- SEC-R100-18: v2-25 gate-off rejection stays a security cell. The gap/baseline bookkeeping edits only inside the WP-V2a/V2b blocks and weakens no existing cell.

Mandatory tests (all `[Trait("Category","Security")]`):
- `Robin100_UnauthenticatedClientUsedOnlyForUnauthorizedAssertions`
- `Robin100_KnownGapsOnlyInNonSecurityCellsWithStableCode` (extends `Robin100_NoKnownGapForDeniedOrLeakCells` with the closed list and the R88 exclusion)
- `Robin100_UnknownSourceAndUnknownTableAreIndistinguishable`
- `Robin100_ScopedAliasWithoutFilterRejectsWholeRequest`
- `Robin100_MaskedFragmentsDirectivesAndAliasesNeverLeakClearValue`
- `Robin100_InListOverCapIsRejected`

<!-- WP-V2a begin -->
## 7. WP-V2a drift and corrections (2026-10-23)

- Code mapping re-checked in `src/`: WebSQL `SqlCompilerErrorMapper` rows confirmed. A SUM over a masked column in WebSQL is `TrinoSqlEngine.MaskedAggregationException` and therefore 403 `AGGREGATION_ON_MASKED_COLUMN` (section 5.3 item 2 said only "W 403"); asserted as such.
- v2-02 pragma: a bare `FROM pragma_table_info('x')` is not Trino grammar (400 `SQL_SYNTAX_ERROR`); the 403 `SQL_POLICY_VIOLATION` of section 5.2 belongs to the `FROM TABLE(pragma_table_info('x'))` form (`SqlAstBuilder.VisitTableFunctionInvocation` throws `SecurityException`). Both forms are cells (`pragma_table_function`, `pragma_bare_call`); both are security cells without a gap.
- v2-12 masked window: the generic masked-reference guard can answer 403 `SQL_POLICY_VIOLATION` before the aggregation guard answers `AGGREGATION_ON_MASKED_COLUMN`; the cell asserts 403 with either of the two stable codes (security cell, never a gap).
- v2-10 R and `REST` filter errors: `SqlCompilerErrorMapper.TryMap` precedes the `ArgumentException` branch in `DatasetDataEndpoints`, so a filter that reaches the SQL parser answers `SQL_SYNTAX_ERROR`; kept as expected `INVALID_QUERY` with gap 056.
- v2-14: the GraphQL cost analyzer prices a variable `first` as the worst case (`QueryCostAnalyzerRule`); all three variants answer `GRAPHQL_VALIDATION_FAILED` (gap 052). The same cost rule is the likely source of 057 (page size above the cap is a validation error before the resolver check).
- v2-08 `mismatch` (`UNION` of 1 and 2 columns): HTTP 502 `SQL_PROVIDER_ERROR` instead of 400 `SQL_SYNTAX_ERROR`. A 5xx is a hard failure, never a gap (SEC-R100-11); the cell stays red until the product validates set-operation arity.
- v2-17 `offset_over` REST answers 200 with zero rows (gap 054); WebSQL answers 200 with zero rows as expected; GraphQL answers 400 `INVALID_QUERY`. Note: a 200 for a request the plan expects rejected sits in section 5.3 item 9 only as "another stable code"; it is registered as a functional gap for the lead's decision.
- New gap ids 051-057 (drafts in the nightly parity plan, section 5). Gap key `Robin_Proactive_NullBoolIsReturn|Rest|` removed (method deleted). Guard: `Robin100V2aSecurityTests` (closed list, security variants, masked-leak scan).
<!-- WP-V2a end -->
<!-- WP-V2b -->
## 8. WP-V2b result notes (2026-10-23)

- 2026-10-23 plan correction (R83 W, also the v2-04 `unknown_group_column` W code): an unknown column on WebSQL answers 403 `SQL_POLICY_DENIED`, not `SQL_POLICY_VIOLATION` (`DmlConstraintViolationException`, #312 / SEC-PG-10 / SEC-X1-05: an unknown identifier is answered like a denied column, no column-name oracle). `Robin_30_UnknownColumnStableCode` pins `SQL_POLICY_DENIED`.
- 2026-10-23 correction: `GraphQlAliasesOnScopedTable` cannot take a gap for partial `north` data through key alone: `Robin100_NoKnownGapForDeniedOrLeakCells` matches the name part `scope` (`...OnScopedTable`); that cell is red-by-design if the product ever returns partial data (hard failure to report, not a gap).
- 2026-10-23 gaps: 070 (v2-18 REST unbalanced filter, observed `SQL_SYNTAX_ERROR`), 071 (R90 GraphQL `first: -1`, observed `GRAPHQL_VALIDATION_FAILED`, HTTP 400). The plan's 070 (R88 code cell) is void; GraphQL R90 replaces the candidate "073 R90 W".
- 2026-10-23 hard failure (SEC-R100-13, security cell, never a gap): `Robin_30_FederatedUnknownSourceSameCode` (variants `join`, `single`) is red. An unknown source (`nope.main.parity_sales`) answers 403 `SQL_POLICY_VIOLATION` (generic `SecurityException` row in `SqlCompilerErrorMapper`), an unknown table of a known source answers 403 `SQL_POLICY_DENIED`: the two are distinguishable (source existence oracle). The product must map an unknown source to `SQL_POLICY_DENIED` like SEC-X1-05.
- 2026-10-23 host notes: `RobinClient.AssertUnauthorizedAsync` is the only user of `CreateUnauthenticatedClient()` (SEC-R100-12); `Robin100_PostJsonAsyncUsesAuthenticatedHostClient` strips exactly the marked `// --- WP-V2b unauthenticated ---` region before its checks. GraphQL validation errors answer HTTP 400 (not 200) in the parity host.
<!-- end WP-V2b -->

## 9. Merge fix-up decisions (2026-10-23)

- Merged tree compiled with -warnaserror without changes (no duplicate helper names across the partials).
- PARITY-GAP-055 removed: WebSQL `GROUP BY <unknown column>` pins 403 `SQL_POLICY_DENIED` (no-column-oracle contract, #312, as the R83 correction).
- PARITY-GAP-056 now covers both REST malformed-filter cells (`dangling_filter`, `unbalanced_filter`; REST contract `INVALID_QUERY`, #313). PARITY-GAP-070 removed.
- PARITY-GAP-057 now covers both GraphQL page-size cells (`first: 100000`, `first: -1`). `GRAPHQL_VALIDATION_FAILED` is the designed code for type/schema errors (`first: "10"`), while `GraphQlTreeBuilder` designs `INVALID_QUERY` for the page-size range ("The page size must be between 0 and MaxAllowedLimit"). PARITY-GAP-071 removed. `PageAboveCap` asserts the pinned `INVALID_QUERY` through `Rejected(code)`.
- v2-12 masked window pins `AGGREGATION_ON_MASKED_COLUMN` (403): `AstSecurityVisitor.MaskGuards.EnsureNoMaskedColumnsInAggregatesAndGrouping` treats a window function as an aggregation scope (:436/:462). Observed `SQL_POLICY_VIOLATION` (403): the ORDER BY guard at :75 can fire first, so the guard order is ambiguous. The request is denied; the cell stays a red security cell (never a gap) and is listed in the nightly plan under "Observed server errors and leaks".
- Hard failures kept red, never gaps (4 red cells): `Robin_30_FederatedUnknownSourceSameCode` (join, single), `Robin_Proactive_SetOperations|WebSql|mismatch` and `Robin_Proactive_RowNumberOverPartition|WebSql|masked` (listed in the nightly plan under "Observed server errors and leaks").
- PARITY-GAP-054 (REST offset above cap answers 200 with zero rows) is accepted as a functional gap.
- Final gap keys: 030, 033, 034, 035, 050, 051, 052, 053, 054, 056, 057 (no security cell keyed).
- R82 `Robin_30_UnknownTableSameCodeAsUnknownSchema` pins per channel: WebSQL 403 `SQL_POLICY_DENIED`, REST 404 `RESOURCE_NOT_FOUND`, GraphQL 400 `GRAPHQL_VALIDATION_FAILED`.
- SEC-R100-19 enforcement: the guard maps 051/052/053 to their methods and allows 054 only for `Robin_302...|Rest|offset_over`; the 054 cell throws the hard failure `DeniedRequestSucceededException` if the body carries any row. "Outcome equals the recorded observation" for 051-053 is not enforced by the gap mechanism (strict xfail only requires the cell to fail); a changed wrong outcome is therefore not detected beyond the leak scan (`RobinMaskedLeakScan` now also matches whole amounts as number or quoted string) and remains an open item.
