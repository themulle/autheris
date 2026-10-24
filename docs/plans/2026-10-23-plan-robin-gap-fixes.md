# PLAN-ROBIN-GAPS: Product fixes for the nightly Parity gaps (Phase 2)

Status: Phase 2 plan (2026-10-23). Branch `fix/robin-gaps`. PRD: `2026-10-23-req-robin-gap-fixes.md`. Gap drafts: `2026-10-23-plan-nightly-parity-tests.md` section 5. Robin context: `2026-10-23-plan-robin-100-addendum.md` sections 7-9.

## 1. Inputs and defaults taken

All PRD open questions take the recommended default (recorded as "default taken"):
- Q1 P3 code: `AGGREGATION_ON_MASKED_COLUMN` (default taken; Phase 3 confirms, no new code).
- Q2 explicit null `first`: capped to the default page (100, capped to `MaxAllowedLimit`), costed at the maximum (default taken).
- Q3 GAP-030: `endswith`/`contains` are missing and fall out of the same rewrite, so they are added (default taken: "already missing and trivial").
- Q4 GAP-051: 3VL, zero rows (default taken).
- Q5 GAP-057: `INVALID_QUERY` for the page-size/offset range; every other #294 shield body unchanged (default taken).
- Q6 GraphQL `first: 0`: zero rows (default taken; already the tree builder contract, #302).

## 2. Decisions per defect

Every item: Red = the Parity cell (strict xfail key removed in the same change) plus the named unit tests. Unit tests live in `tests/Autheris.Tests.Unit`; `[Trait("Category", "Security")]` where marked (S).

### WP-S (WebSQL + REST)

S1. P1 #320 source existence oracle (S). Root cause: `CrossSourceQueryRouter.cs:110` calls `DataSourceAllowlistGuard.EnsureAllowed`, which throws a plain `WebSqlPolicyException` (`DataSourceAllowlistGuard.cs:22/33/38`) that lands on the generic `SecurityException` row (`SqlCompilerErrorMapper.cs:146`, `SQL_POLICY_VIOLATION`). An unknown table of a known source throws `WebSqlTableDeniedException` (`:122`, `SQL_POLICY_DENIED`, mapper `:138`). Both the single and the join variant go through this loop. Fix: add a non-throwing `DataSourceAllowlistGuard.IsAllowed(...)` with the same logic; the router (`:110` and `:137`) throws `WebSqlTableDeniedException` with the same message text as `:122` when it is false. `EnsureAllowed` and its OLAP callers (`OlapStagingService.cs:632`, `FederatedStagingService.cs:92/95`) stay unchanged (scope). The guard stays before the metadata lookup (SR-H-06: no lookup for a source the tenant may not use). Residual for Phase 3: the denied-source path skips one metadata lookup (in-memory decision vs. repository call); Phase 3 decides whether to also run the primary lookup (without the `default` fallback) and discard its result. By-design decision: a known, allowed source that is down keeps 503 `SQL_SOURCE_UNAVAILABLE`. Reason: the caller is authorized for that source, so the outage reveals nothing about existence, and clients need the 503 to retry. "Known but not enabled for WebSQL or the tenant" gets the uniform 403. Tests: `CrossSourceQueryRouterTests.UnknownSource_ThrowsTableDenied`, `...SourceNotEnabledForTenant_ThrowsTableDenied`; (S) `SqlCompilerErrorMapperTests.UnknownSourceAndUnknownTable_SameStatusCodeMessage` (compares the full mapped body bytes). Parity: `Robin_30_FederatedUnknownSourceSameCode` (join, single) turns green (hard-failure cell, no gap key).

S2. P2 #321 set-operation degree. Root cause: `SqlAstBuilder.VisitSetOperation` (`SqlAstBuilder.cs:435`) builds `SetOperationQuery` without a degree check, so the provider rejects it (502). Fix: in `VisitSetOperation`, when both operands resolve to a `QuerySpecification` (leftmost spec for nested set operations) without a `WildcardSelectItem`, compare `Projections.Count`. On a mismatch, throw a new `SetOperationDegreeMismatchException` (TrinoSqlEngine, fixed message, no column text). `SqlCompilerErrorMapper` gets a type row before the generic rows: 400 `SQL_SYNTAX_ERROR`. Decision: an operand with `*` cannot be checked in the AST (`*` is never expanded, `SqlDialectGeneratorBase.cs:379`), so it is skipped here and tracked as follow-up PARITY-FU-003 (draft added by WP-S). Phase 3 must re-check that case for a remaining 5xx. Tests: `SqlAstBuilderSetOperationTests.UnionDegreeMismatch_Throws` (UNION, INTERSECT, EXCEPT, nested), `...EqualDegree_Builds`, `...Wildcard_IsNotChecked`, plus the mapper row test. Parity: `Robin_Proactive_SetOperations|WebSql|mismatch` green.

S3. P3 #322 window over a masked column (S). Root cause: `SqlQueryAnalyzer.EnterOrderBy` (`TrinoSqlEngine/Analysis/SqlQueryAnalyzer.cs:429`) adds the ORDER BY of a window `OVER` clause to `FilterColumnReferences`. `GovernedSqlExecutionService.EnforceMaskedColumnGuardrails` (`:299`, throw `:347`) and `GovernedSqlRewriter.cs:~607-620` then throw the generic `WebSqlPolicyException` before the engine window guard (`MaskGuards.cs:436/448-463`, `MaskedAggregationException`) runs. The developer verifies whether `PARTITION BY` is collected the same way. Fix: the analyzer records window `PARTITION BY`/`ORDER BY` columns in a separate `WindowColumnReferences` set instead of `FilterColumnReferences`. Both Application guards throw `MaskedAggregationException` (403 `AGGREGATION_ON_MASKED_COLUMN`) when that set contains a masked column, so the request is still denied before execution even if the engine guard were bypassed. Plain `ORDER BY masked` stays 403 `SQL_POLICY_VIOLATION`. Code: default taken (Q1). Tests (S): `SqlQueryAnalyzerTests.WindowOrderAndPartition_NotInFilterReferences`, `GovernedSqlExecutionServiceMaskTests.WindowOverMasked_ThrowsMaskedAggregation`, `...PlainOrderByMasked_StaysPolicyViolation`, and the same pair for the rewriter path. Parity: `Robin_Proactive_RowNumberOverPartition|WebSql|masked` green.

S4. GAP-050 #327 federated join chain order. Root cause: `CrossSourcePlanner.MatchesSubtree`/`CollectTableNames` (`CrossSourcePlanner.cs:624-643`) compare bare table names. In `((a JOIN r) JOIN b)` both `a` and `b` are `parity_sales`, so the outer join matches the subtree `{a, r}`, and `FederationAstRewriter.VisitJoinedTableSource` (`:606`) replaces the whole 3-way join, dropping `b` and its ON clause. The first Red test pins the exact throw site of the 400 (`SubtreeCompiler.cs:184` re-parse or `:138`). Fix: match each `NamedTableSource` on its qualified identity (catalog.schema.table plus alias) against `MemberTables[*].Target.FullName`/alias, never on the last name part. Every table in the chain still passes S1's per-source authorization. Tests: `CrossSourcePlannerSubtreeTests.SameNameTableOtherSource_NotMatchedBySubtree`, `...ChainOrderIndependent_SamePlanShape`; (S) `...ChainWithUnknownSource_UniformDenial`. Parity: `Robin_123_FederatedSameSourceSubqueryPart|WebSql|` green (key removed).

S5. GAP-054 #302 REST offset above cap. Root cause: `GovernedDataQueryService.cs:145` checks `DatasetApiOptions.MaxOffset`, whose default is `DefaultMaxOffset = 100_000` (`AnalyticsOptions.cs:49`), so offset 10001 passes and returns 200 empty. Fix: `DefaultMaxOffset = 10_000` (equal to `GraphQL.MaxAllowedOffset`). The check stays `offset > max` and throws `ArgumentException` (400 `INVALID_QUERY`, `DatasetDataEndpoints.cs:215`). This is a breaking default (section 3). Tests: `GovernedDataQueryServiceTests.OffsetAboveDefaultCap_Throws`, `...OffsetAtCap_Allowed`, `DatasetDataEndpointsTests.OffsetAboveCap_Returns400InvalidQuery`. Parity: `Robin_302_OffsetBoundaryAndFirstZero|Rest|offset_over` green.

S6. GAP-030 #323 REST `startswith`. Root cause: `DataApiFilterTranslator.TranslateFilterToSql` (`:87-113`) only rewrites operator words, so `startswith(...)` reaches the compiler as an unknown function (400). Fix: a pre-pass over the raw expression, before literal segmentation, matches `(startswith|endswith|contains)\s*\(\s*<identifier>\s*,\s*'<literal>'\s*\)` and emits `<identifier> LIKE '<pattern>' ESCAPE '\'`. In the literal content, `\`, `%` and `_` are escaped with `\` and `''` is kept. Any other argument shape stays untouched and fails as today. `escape` joins `ReservedKeywords` (`:18`). Q3 default taken. Tests (S): `DataApiFilterTranslatorTests.StartsWith_EmitsEscapedLike`, `...WildcardsInLiteral_AreEscaped` (`50%`, `a_b`, `back\slash`), `...QuoteInLiteral_StaysOneLiteral`, `...EndsWithContains`. Parity: `Robin_Proactive_LikePrefix|Rest|` green.

S7. GAP-033 #324 REST `eq null`/`ne null`. Root cause: `ReplaceOperators` (`DataApiFilterTranslator.cs:217-230`) turns `eq null` into `= null`. Fix: before the `eq`/`ne` rules, `\beq\s+null\b` becomes `IS NULL` and `\bne\s+null\b` becomes `IS NOT NULL` (non-literal segments only, as today). Tests: `DataApiFilterTranslatorTests.EqNull_IsNull`, `...NeNull_IsNotNull`, `...LiteralNullText_Untouched` (`'null'` stays a string). Parity: `Robin_Proactive_IsNullAmount|Rest|`, `Robin_Proactive_IsNotNullRegion|Rest|` green.

S8. GAP-056 #329 REST malformed filter. Root cause: `DatasetDataEndpoints.cs:194` maps every governed SQL error through `SqlCompilerErrorMapper.TryMap` before the `ArgumentException` branch, so a filter that fails the parser answers `SQL_SYNTAX_ERROR`. Fix: in that catch, a mapped `SQL_SYNTAX_ERROR` is answered as 400 `INVALID_QUERY` with `InvalidQueryMessage`, and the original code is logged at Warning. Decision: REST builds the SQL from quoted select and orderBy parts, so the filter is the only free text that reaches the parser. REST therefore never answers `SQL_SYNTAX_ERROR`. Tests: `DatasetDataEndpointsTests.DanglingOperator_Returns400InvalidQuery`, `...UnbalancedParenthesis_Returns400InvalidQuery`; (S) `...MalformedFilter_BodyHasNoParserDetail`. Parity: `Robin_Proactive_TypeMismatchStableCode|Rest|dangling_filter`, `Robin_Proactive_MalformedFilterStableCode|Rest|unbalanced_filter` green.

### WP-G (GraphQL)

G0. Pin the site first (Red, no fix). Write a test in the style of `ErrorSanitizingFilterEngineErrorTests`, against the real engine, that records the raw (pre-sanitizer) error code and phase for these inputs: `query($f:Int,$o:Int)` with (5,10), (null,null) and (0,null); `first: 100000`; `first: -1`. The parity host runs in Development, so the observed 400 `GRAPHQL_VALIDATION_FAILED` is a validation-phase error: the shield (`GraphQlEnumerationShieldMiddleware.cs:58-76`) turns every non-neutral validation error, `QUERY_TOO_COMPLEX` included, into that code. Candidates: `QueryCostAnalyzerRule.Validate` (`:138-167`, priced without variable values) or a HotChocolate built-in validation or cost error (HCnnnn, `ErrorSanitizingFilter.cs:311-323`). G1 and G4 fix the pinned site. If it is neither candidate, the developer stops and reports to the lead.

G1. #318 / GAP-052 `Int` variables (S). Root causes: (a) the G0 site; (b) `GraphQlTreeBuilder.ResolveVariableLiteral` (`:443-451`) reads only `context.Variables.TryGetValue<IValueNode>` and otherwise returns `NullValueNode`; (c) a null `offset` hits `"The offset is invalid."` (`:125-128`); (d) `QueryCostAnalyzerRule` `:431-456` uses the declared variable default when a runtime null was supplied, which is a cost bypass for `$f:Int=1` with `f:null`. Fix:
- Variable resolution returns the coerced value for any stored representation (value node, int, long).
- A null or absent `first` means the default page (`Math.Min(100, maxAllowedLimit)`, Q2 default taken). A null `offset` means 0.
- In the cost rule, the declared default applies only when the variable is absent from `variableValues` (GraphQL spec 6.1.2). An explicit null is priced like literal `first: null` (worst case).
- The validation-time pass, which has no variable values, never rejects a document for a variable `first`; the runtime pass (`CostAndQuotaMiddleware.cs:110-126`) prices it with the real values.

Tests: `GraphQlTreeBuilderVariableTests.FirstOffsetVariables_FiveTen`, `...NullOffset_IsZero`, `...NullFirst_DefaultPage`, `...ZeroFirst_ZeroRows`; (S) `QueryCostAnalyzerRuleTests.ExplicitNullFirst_IgnoresDeclaredDefault_MaxCost`, `...AbsentVariable_UsesDeclaredDefault`, `...NullFirstNested_StaysOverBudget`. Parity: the three `Robin_318_IntVariablesFirstOffset` keys removed.

G2. #319 / GAP-053 `or: [{}, X]`. Root cause: `ParseObjectFilter` returns null for `{}` (`GraphQlTreeBuilder.cs:374`) and the `or` branch drops it (`:285-288`), which narrows `TRUE OR X` to `X`. Fix:
- In `or`, a member whose parse yields no constraint makes the whole `or` neutral (no filter item added).
- In `and` it stays dropped (neutral, unchanged). `or: []` is unchanged.
- A null list member in `or`, which is silently skipped today, is rejected with `INVALID_QUERY` like `or: null` (fail closed, no silent narrowing).
- Row policy and masking are applied outside the tree filter and are unaffected.

Tests: `GraphQlTreeBuilderFilterTests.OrWithEmptyMember_IsNeutral`, `...AndWithEmptyMember_IsNeutral`, `...EmptyOrList_Unchanged`, `...OrNullMember_Rejected`; (S) `...OrEmptyMember_RowPolicyStillApplied` (a scoped table returns only permitted rows). Parity: `Robin_319_EmptyLogicalMembers|GraphQl|or_empty_member` key removed.

G3. GAP-051 #328 `nin` with null. Root cause: `TreeFilterRenderer.ToList` (`Autheris.Application/Sql/Tree/TreeFilterRenderer.cs:173`) strips nulls for both `In` and `NotIn`. Fix: `NotIn` with any null member renders `1 = 0` (3VL: never true; Q4 default taken). `In` keeps dropping nulls, because a null never matches; an `In` list that is all null renders `1 = 0`. Tests: `TreeFilterRendererTests.NotInWithNull_RendersFalse`, `...InWithNull_MatchesNonNullOnly`, `...NotInWithoutNull_Unchanged`. Parity: `Robin_Proactive_NotInWithNullIsEmpty|GraphQl|` key removed.

G4. GAP-057 #330 and the GraphQL part of GAP-054: page-range code (S). Root cause: the G0 site rejects `first: 100000` and `first: -1` during validation, before `GraphQlTreeBuilder.cs:104-107` can throw `INVALID_QUERY`. Fix:
- The validation-phase rule no longer rejects a page-size or offset value. Cost uses `Math.Min(limit, maxResponseRows)`, and a negative literal never reaches execution.
- The range check belongs to the tree builder. It throws a typed `GatewayPageRangeException` (subclass of `GatewayInvalidQueryException`) with the fixed message `GraphQlTreeBuilder.PageRangeMessage` ("The page size or offset is out of range."). It covers `first` < 0 or > `MaxAllowedLimit` and `offset` < 0 or > `MaxAllowedOffset`.
- `CatalogGraphQlTypeModule.cs:671` maps the exception to code `INVALID_QUERY` with that constant.
- Outside Development, `ErrorSanitizingFilter` keeps that exact constant instead of replacing it with the shield message. `GraphQlEnumerationShieldMiddleware` does not shield a result whose only errors are `INVALID_QUERY` with exactly that constant message. The message is a server constant with no caller text, so a caller cannot forge it.
- Every other shielded body (unknown field, argument or type; denied table or column) stays byte-identical in every environment. Q5 default taken.

Tests: `GraphQlTreeBuilderPagingTests.FirstAboveMax_PageRange`, `...FirstNegative_PageRange`, `...OffsetNegative_PageRange`, `...FirstZero_ZeroRows`; (S) `GraphQlEnumerationShieldTests.ProductionShieldBodies_ByteIdentical` (pins the bodies for unknown field, unknown argument, unknown type, denied table and denied column against literals, in Production and in Development), `...PageRange_NotShielded_FixedBody`, `...PageRangeMixedWithUnknownField_Shielded`. Parity: `Robin_39_PageAboveCapIsRejectedOrSignalled|GraphQl|` and `Robin_Proactive_NegativeLimitRejected|GraphQl|` keys removed.

G5. GAP-034 #325 `QUARTER` bucket (SQLite). Root cause: in `GovernedTableReadPlanner.BuildTimeBucketExpression`, the SQLite format `"quarter" => "%Y-m"` (`GovernedTableReadPlanner.cs:627`) yields `2025-m`, which fails the declared type. Fix: compute the quarter start as `date(<local>, 'start of month', printf('-%d months', (CAST(strftime('%m', <local>) AS INTEGER) - 1) % 3))`, with the same key shape as MONTH. Tests: `GovernedTableReadPlannerTests.SqliteQuarterBucket_FirstDayOfQuarter` (runs against in-memory SQLite and expects Jan/Apr/Jul/Oct keys). Parity: `Robin_Proactive_GroupByQuarter|GraphQl|` key removed.

G6. GAP-035 #326 `HOUR` + `timeZone` (SQLite). Root cause: `GovernedTableReadPlanner.cs:639-650` uses `tzi.BaseUtcOffset`, the standard offset without DST. Fix: SQLite has no time-zone data, so the planner emits `datetime(<col>, CASE WHEN datetime(<col>) >= '<t1>' AND datetime(<col>) < '<t2>' THEN '<+hh:mm>' ... ELSE '<base>' END)`. The arms come from the zone's UTC transitions (`TimeZoneInfo`, years 1970-2099), cached per zone in a static `SqliteZoneOffsetTable`. The literals are generated by the server from an allowlisted zone (`TimeZoneRegistry.Contains`, `:592`), never from caller text. Outside that year range the base offset applies (a documented limit). This applies to every interval on SQLite with a non-UTC zone. Tests: `SqliteZoneOffsetTableTests.Berlin_WinterAndSummer`, `...ZoneWithoutDst_SingleArm`, `...HalfHourZone` (`Asia/Kolkata`); `GovernedTableReadPlannerTests.SqliteHourBucketBerlin_DstAware` (in-memory SQLite, expects 11 and 17). Parity: `Robin_130_HourBucketBerlinWinterSummer|GraphQl|` key removed.

## 3. Contract changes (breaking, release notes)

| Channel | Input | Old | New |
|---|---|---|---|
| WebSQL | unknown federated source | 403 `SQL_POLICY_VIOLATION` | 403 `SQL_POLICY_DENIED` (same body as unknown table) |
| WebSQL | set operation with unequal degree | 502 `SQL_PROVIDER_ERROR` | 400 `SQL_SYNTAX_ERROR` |
| WebSQL | window `OVER` a masked column | 403 `SQL_POLICY_VIOLATION` | 403 `AGGREGATION_ON_MASKED_COLUMN` |
| WebSQL | federated join chain, any order | 400 `SQL_SYNTAX_ERROR` | 200 |
| REST | `offset` above 10000 | 200 empty | 400 `INVALID_QUERY` (default `DatasetApi.MaxOffset` 100000 becomes 10000) |
| REST | malformed filter | 400 `SQL_SYNTAX_ERROR` | 400 `INVALID_QUERY` (REST never answers `SQL_SYNTAX_ERROR`) |
| REST | `startswith`/`endswith`/`contains` | 400 `INVALID_QUERY` | 200 |
| REST | `eq null` / `ne null` | 200 zero rows (`= NULL`) | `IS NULL` / `IS NOT NULL` (differs from GraphQL/WebSQL `eq: null`, #300, by spec) |
| GraphQL | `first`/`offset` as `Int` variables | 400 `GRAPHQL_VALIDATION_FAILED` | works; null `first` = default page at max cost; null `offset` = 0 |
| GraphQL | `first` < 0 or > `MaxAllowedLimit`, `offset` < 0 or > cap | 400 `GRAPHQL_VALIDATION_FAILED` | 400 `INVALID_QUERY`, fixed message, every environment |
| GraphQL | `or: [{}, X]` | X rows | all rows (row policy still applied) |
| GraphQL | `nin` with a null member | rows | zero rows |
| GraphQL | `QUARTER` bucket (SQLite) | `AGGREGATION_RESULT_TYPE_MISMATCH` | groups keyed by quarter start |
| GraphQL | `HOUR` bucket + `timeZone` (SQLite) | standard offset | DST offset per instant |

## 4. Work packages

Two parallel lanes, no shared production file (checked by grep; `SqlCompilerErrorMapper` is WP-S only, `GovernedTableReadPlanner`/`TreeFilterRenderer` are WP-G only).

| WP | Items | Production files | Rough LOC (prod + test) |
|---|---|---|---|
| WP-S | S1-S8 | `Sql/Services/CrossSourceQueryRouter.cs`, `Sql/Services/DataSourceAllowlistGuard.cs`, `TrinoSqlEngine/Ast/Builder/SqlAstBuilder.cs`, new `SetOperationDegreeMismatchException.cs` (TrinoSqlEngine), `Sql/Errors/SqlCompilerErrorMapper.cs`, `TrinoSqlEngine/Analysis/SqlQueryAnalyzer.cs`, `Sql/Services/GovernedSqlExecutionService.cs`, `Sql/Services/GovernedSqlRewriter.cs`, `Sql/Services/CrossSourcePlanner.cs`, `Domain/Options/AnalyticsOptions.cs`, `Data/Services/DataApiFilterTranslator.cs`, `Api/Endpoints/DatasetDataEndpoints.cs` | ~250 + ~450 |
| WP-G | G0-G6 | `GraphQL/Catalog/GraphQlTreeBuilder.cs`, `GraphQL/Interceptors/QueryCostAnalyzerRule.cs`, `GraphQL/Catalog/CatalogGraphQlTypeModule.cs`, `Domain/Exceptions/GatewayExceptions.cs` (new subclass), `Api/Middleware/ErrorSanitizingFilter.cs`, `Api/Middleware/GraphQlEnumerationShieldMiddleware.cs`, `Application/Sql/Tree/TreeFilterRenderer.cs`, `Application/Data/Services/GovernedTableReadPlanner.cs`, new `Application/Data/Services/SqliteZoneOffsetTable.cs` | ~300 + ~500 |

Shared test and plan files (line-disjoint blocks):
- `tests/Autheris.Tests.Parity/Robin/RobinKnownGaps.cs`: WP-S deletes the keys at lines 12-14, 19, 27, 28, 33; WP-G deletes lines 15-16, 22-26, 29-30. Block comments stay. Adjacent deletions (14/15, 26/27, 28/29) give a trivial merge conflict that the lead resolves as the union of deletions.
- `tests/Autheris.Tests.Architecture/Robin100V2aSecurityTests.cs`, `Robin100ArchitectureTests.cs`: no change (the guard checks only existing keys; its self-tests use literal strings).
- Robin scenario files (`RobinScenarios*.cs`): no change expected. A dev who finds a wrong expectation stops and reports to the lead; a cell is never edited to match observed output.
- Nightly plan section 5: each WP appends one line "Fixed on fix/robin-gaps (#nnn)." under its own drafts only (WP-S: 030, 033, 050, 054, 056, plus the new PARITY-FU-003 draft under "Follow-ups"; WP-G: 034, 035, 051, 052, 053, 057). The Robin-100 addendum stays history; the lead records the 4 red cells as fixed in the tracker.
- `parity-baseline.json`: devs must not edit it; the lead regenerates it after both lanes merge.

Exit per WP: unit tests green, the WP's Parity cells green with their keys removed, `Robin100_NoKnownGapForDeniedOrLeakCells` and the leak scans unchanged and green. Lead after merge: full Parity run with 0 red cells, baseline regenerated.

## 5. Phase 3 security conditions

Reviewer: csharp-security-expert, 2026-10-23. Verdict: **APPROVED WITH CONDITIONS**. Every test below carries `[Trait("Category", "Security")]`. No existing security test, gate, `KnownGap` guard or shield body may be weakened; keys are removed only for the cells the plan names.

### Decisions

1. P1 #320: a byte-identical 403 `SQL_POLICY_DENIED` (status, code, message, headers) for unknown source, source not enabled for WebSQL/tenant, and unknown table is sufficient. The skipped metadata lookup is an accepted timing residual (default taken: no dummy lookup; a dummy call would add load and a new failure mode for little gain). 503 `SQL_SOURCE_UNAVAILABLE` only after the allowlist decision passed.
2. P2 #321: deferring `*` to PARITY-FU-003 is not accepted as is. A user-SQL-reachable 5xx is an owner-level defect, so a fail-closed fallback is required now (SEC-RG-03).
3. P3 #322: `AGGREGATION_ON_MASKED_COLUMN` (403) is right; it is a denial of equal strength, and the request is denied before execution.
4. #318: null/unresolved `first` is costed as the default page at the maximum (never lower than an explicit default page); the cost uses the coerced runtime value, and the declared default only when the variable is absent.
5. G4/#330: the exemption is acceptable only as an exact-match, single-error, server-constant rule (SEC-RG-08/09).
6. MaxOffset 100000 -> 10000: accepted (tightening, not a weakening).
7. REST `eq null` -> `IS NULL`: the translated SQL goes through the same governed guards, so a masked/denied column in `IS NULL` is still a filter reference and denied; required-filter and row-policy predicates are added outside the user filter (SEC-RG-10).
8. G6: accepted with SEC-RG-12; G2 null member -> `INVALID_QUERY` accepted (SEC-RG-11).

### Binding conditions

- SEC-RG-01 (S1): `IsAllowed` is evaluated before any metadata/repository call and uses the exact `EnsureAllowed` logic (shared private core, no second implementation). The denied path throws `WebSqlTableDeniedException` with the same message as the unknown-table path; no source name beyond what the caller sent is echoed differently between the cases.
- SEC-RG-02 (S1): `EnsureAllowed` behaviour for OLAP/staging callers is unchanged. 503 is never produced for a source the tenant may not use.
- SEC-RG-03 (S2): In addition to the AST degree check, the mapper (or the provider-error wrapper) maps a provider set-operation column-count error to 400 `SQL_SYNTAX_ERROR` with a fixed message (no provider text, no column names). Classification by provider error code/SQLSTATE, not by user text. Wildcard operands (`SELECT *` UNION ...) must therefore return 4xx, and PARITY-FU-003 is closed or reduced to "provider message variants". An unclassified provider error stays 502 with a sanitized body (unchanged).
- SEC-RG-04 (S3): Window `PARTITION BY`/`ORDER BY` on a masked or denied column remains denied pre-execution in both guards (executor and rewriter); `WindowColumnReferences` is checked against the same masked/denied/HMAC rules as `FilterColumnReferences` (denied columns too, not only masked). Plain `ORDER BY/WHERE masked` stays `SQL_POLICY_VIOLATION`; JOIN-predicate rule unchanged. The 403 body carries no column value.
- SEC-RG-05 (G1): The cost rule prices a variable `first` from the coerced runtime value; absent variable -> declared default; explicit null, unresolved or unparsable -> maximum default-page cost. The runtime pass must run before execution and fail closed (reject) if a variable value cannot be coerced. The validation-time pass relaxing never skips the runtime pass.
- SEC-RG-06 (G1): Null `first` executes the default page (`min(100, MaxAllowedLimit)`) and is never unbounded; `first` and the response-row cap still apply after coercion.
- SEC-RG-07 (G4/G0): `GatewayPageRangeException` is the only source of the exempt message; the range check covers literals and variable values alike, with `long` overflow handled (values above `int.MaxValue` -> `INVALID_QUERY`, not an exception 5xx).
- SEC-RG-08 (G4): The shield exemption applies only when every error of the response is `INVALID_QUERY` with exactly `PageRangeMessage` (ordinal compare) and no other error, extension or path text beyond the fixed body. Any mixed response is shielded. The message must be identical for unknown and denied tables (the range check runs on the argument alone and reveals nothing about existence); the range check must not run before field/table authorization in a way that lets `first: 100000` on a denied table answer differently from `first: 10` on it.
- SEC-RG-09 (G4): `ProductionShieldBodies_ByteIdentical` pins the five #294 bodies as literals captured from main before the change (not regenerated), in Production and Development.
- SEC-RG-10 (S7): `eq null`/`ne null` rewrite only in non-literal segments; `IS NULL` on a masked or denied column is rejected exactly like `= 'x'` on it; the rewrite happens before the guards (guards see the final SQL). No rewrite of `null` inside a larger identifier (`\b`, case-insensitive handled).
- SEC-RG-11 (G2): `or` with an empty member is neutral only for the tree filter; row policy, required filters and masking still apply. Null list member in `or` and in `and`/`not` -> `INVALID_QUERY`; nested `or` depth/size bounds unchanged.
- SEC-RG-12 (G6): DST arms are generated only from allowlisted zones, formatted with invariant culture as fixed-shape literals; the per-zone table is bounded (zone count x years) and cached thread-safely. A zone absent from the allowlist still fails closed as today.
- SEC-RG-13 (S5): `MaxOffset` default 10000 may be overridden by configuration but values `<= 0` or above `int.MaxValue` fail startup validation; check stays `offset > max`.
- SEC-RG-14 (S6/S8): `startswith/endswith/contains` literal escapes `\`, `%`, `_`, `'` so a caller cannot widen the pattern; the `LIKE` target column goes through the same masked/denied guard. S8: the original `SQL_SYNTAX_ERROR` detail is logged without the filter text value beyond the existing redaction rules, and the REST body is the fixed `InvalidQueryMessage`. Policy codes (403) are never remapped to `INVALID_QUERY`.
- SEC-RG-15 (S4): Qualified-identity matching must treat same-name tables of different source/schema as distinct and must not let a denied table pass via alias reuse; the allowlist/policy check runs per resolved table, including the re-added `b`.
- SEC-RG-16 (G3): `NotIn` with a null renders `1 = 0` (fail closed, narrower, never wider).

### Mandatory tests (all `[Trait("Category","Security")]`)

- `SqlCompilerErrorMapperTests.UnknownSourceUnknownTableDisabledSource_ByteIdenticalBody` (SEC-RG-01/02)
- `CrossSourceQueryRouterTests.DeniedSource_NoMetadataLookup`, `...AllowedDownSource_Returns503`
- `SqlCompilerErrorMapperTests.SetOperationColumnCountProviderError_Maps400_NoProviderText`; Parity `Robin_Proactive_SetOperations|WebSql|wildcard_mismatch` returns 4xx (SEC-RG-03)
- `GovernedSqlExecutionServiceMaskTests.WindowPartitionOnDeniedColumn_Denied`, `...WindowOverMasked_ThrowsMaskedAggregation`, `GovernedSqlRewriterMaskTests.WindowOverMasked_Denied` (SEC-RG-04)
- `QueryCostAnalyzerRuleTests.ExplicitNullFirst_IgnoresDeclaredDefault_MaxCost`, `...UnparsableVariable_Rejected`, `...AbsentVariable_UsesDeclaredDefault`; `GraphQlTreeBuilderVariableTests.NullFirst_DefaultPageBounded` (SEC-RG-05/06)
- `GraphQlTreeBuilderPagingTests.LongOverflowFirst_PageRange`, `...VariableFirstAboveMax_PageRange`
- `GraphQlEnumerationShieldTests.ProductionShieldBodies_ByteIdentical`, `...PageRange_NotShielded_FixedBody`, `...PageRangeMixedWithUnknownField_Shielded`, `...PageRangeOnDeniedTable_SameBodyAsAllowedTable` (SEC-RG-07/08/09)
- `DataApiFilterTranslatorTests.IsNullOnMaskedColumn_Denied` (via governed service), `...EqNullInsideLiteral_Untouched`, `...ContainsPatternMetacharacters_Escaped` (SEC-RG-10/14)
- `GraphQlTreeBuilderFilterTests.OrEmptyMember_RowPolicyStillApplied`, `...OrNullMember_Rejected`, `...AndNullMember_Rejected` (SEC-RG-11)
- `SqliteZoneOffsetTableTests.NonAllowlistedZone_FailsClosed`, `...LiteralsInvariantFormat` (SEC-RG-12)
- `DatasetApiOptionsTests.MaxOffsetNonPositive_FailsValidation` (SEC-RG-13)
- `DatasetDataEndpointsTests.PolicyViolation_NotRemappedToInvalidQuery` (SEC-RG-14)
- `CrossSourcePlannerTests.JoinChainSameNameDifferentSource_DistinctAndPolicyChecked` (SEC-RG-15)
- `TreeFilterRendererTests.NotInWithNull_RendersFalse` (SEC-RG-16)

## 6. Changelog (Phase 4, WP-G)

- 2026-10-23 G0 pinned: the raw error of every rejected input (`first` variable without default, `first: 100000`, `first: -1`) is a validation-phase `QUERY_TOO_COMPLEX`, emitted by `QueryCostAnalyzerRule.Validate` (the only emitter of that code; no HotChocolate built-in is involved). Budget 5000 in the parity host: a variable without default priced at `maxResponseRows` x 10, an out-of-range literal at `min(value, maxResponseRows)` x 10.
- 2026-10-23 Drift: `IVariableValueCollection.TryGetValue<T>` is constrained to `IValueNode`, so the tree builder reads page variables through that one path (value node); root cause (b) of G1 is only the cost rule. `ReadPageNumber` now covers value nodes, null, and saturating overflow.
- 2026-10-23 Drift (SEC-RG-08): the plan's "range check in the tree builder" would answer `first: 100000` on a denied table before authorization. `GraphQlTreeBuilder.BuildTree(..., out bool pageRangeViolated)` clamps a violating node to a zero-row page; `ResolveRootQueryAsync` runs the normal tree authorization with it and only then throws `GatewayPageRangeException`. A denied table therefore answers ACCESS_DENIED (shielded) for every page size. The 4-argument `BuildTree` overload still throws `GatewayPageRangeException` directly.
- 2026-10-23 Cost rule: for catalog table fields (arguments `where`, `orderBy`, `first`, `offset`) a value outside `[0, maxResponseRows]` is priced as the minimal page (it is rejected before data access); an explicit or literal null `first` is priced as the default page `min(100, maxResponseRows)` (what it executes); a variable absent without a default is the field default page. Other fields keep the worst-case pricing. Unparsable runtime values stay worst case.
- 2026-10-23 SEC-RG-05 fallback: `CostAndQuotaMiddleware` skips tier/quota when rate limiting is disabled or there is no HTTP context; because the validation pass no longer prices an unbound variable page at the worst case, those paths now apply the gateway complexity limit to the runtime variable values (`RejectIfOverGatewayComplexity`).
- 2026-10-23 G3: `GraphQlTreeBuilderNullTests.NotInList_OnlyNull_IsAlwaysTrue_AndDropsNullItems` pinned the old #300 behavior (NOT IN with null drops the null); replaced by `NotInList_WithNull_IsNeverTrue` (SEC-RG-16). `InList_DropsNullItems` is unchanged.
- 2026-10-23 G6: a zone that is allowlisted but unknown to the host's tz data now fails closed (`ArgumentException`) instead of silently using UTC.
- 2026-10-24 Phase 5 fixes: the cost rule now distinguishes root fields from relation lists. A value is discounted (priced minimal) only when the builder rejects it for that field: root outside `[0, maxResponseRows]`, relation outside `[0, TreeSqlCompiler.MaxLimit]` (10_001). Relation values are priced at their value (cap `MaxLimit`); the default page is priced at what executes (root `min(100, maxResponseRows)`, relation 100). Pinned by `QueryCostAnalyzerPagingTests.SEC_RG_05_RelationFirst_*` and `SEC_RG_05_RelationDefaultPage_IsPricedAtWhatExecutes` (literal, variable and runtime-node values, the same inputs `CostAndQuotaMiddleware` passes).
- 2026-10-24 SEC-RG-16 extended: `TreeFilterRenderer.Render` tracks negation parity. Under an odd number of NOTs a NOT IN with a null renders `NOT (col IN (...))` (or TRUE when only nulls) and an IN with a null renders TRUE, so `not: { x: { in: [a, null] } }` is never true and double negation equals none (`TreeFilterRendererTests.SEC_RG_16_*`).
- 2026-10-24 Test renames (Security trait added): `PageRange_NotShielded_FixedBody` -> `SEC_RG_08_PageRange_NotShielded_FixedBody`; `FirstOutOfRange_Variable_FixedBody` -> `SEC_RG_07_FirstOutOfRange_Variable_FixedBody`; `NullMember_Rejected` -> `SEC_RG_11_NullMember_Rejected` (now covers `or` and `and`, null first and last).
- 2026-10-24 Known cost (not changed): the SQLite DST offset `CASE` is repeated at every use of the local time in a bucket expression (about 13 KB per use for Berlin; QUARTER with a zone uses it three times, in SELECT and GROUP BY), so a zone-aware QUARTER is roughly 80 KB of SQL, far below SQLite's 1 MB limit. A subquery would not survive the governed rewriter, which re-parses the planned SQL, so it is left as is.
- 2026-10-24 Tree fields in the cost rule are still recognised by the argument set `where`, `orderBy`, `first`, `offset`; marking them through field features/context data was not cheap in HotChocolate 16 and is left as a follow-up. The discount never applies to other fields.
