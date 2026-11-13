# PLAN-OI-WEB: Open issues cluster WebSQL and MCP (#527, #668, #669, #681, #682)

- Date: 2026-11-13. Base: `origin/main` @ `10400ec4`. Status: PLANNED (Phase 2 done, Phase 3 pending).
- Origin plan: [`00-master-plan-overview.md`](00-master-plan-overview.md). Program specs: [`simplification/README.md`](simplification/README.md).
- Binding: owner decisions OD-1..OD-45 in [PRD section 2.0](2026-11-05-req-project-simplification.md) (a later OD wins); feature freeze PRD 5.1 (bug fixes, security fixes and tests for existing behaviour allowed; new features need `freeze-exception` or one of the pre-approved WP-SIMPL-38..48); PR limit 400 net added `src/` lines (PRD 5.3).
- Security test tag: S = `[Trait("Category","Security")]`. Line numbers are as of `10400ec4`; symbols are authoritative.

## Summary

| Issue | Verdict | WP / spec | Open on main | Size | Phase 3 |
|---|---|---|---|:---:|:---:|
| #527 WebSQL cost estimate and per-caller quota | OBSOLETE-CLOSE | WP-SIMPL-51 PR 3 + PR 4 (I1), merged in #581 | nothing | - | no |
| #668 WebSQL denies tables created through `Dbt:DatabaseMappings` | NEW PLAN | WP-OI-WEB-668 (bug fix) | governed catalog ignores the WebSQL data-source mapping | S | yes |
| #669 SQL Server synonyms always denied | OBSOLETE-CLOSE (after a test-only PR) | WP-SIMPL-44 PR 2 (#645) deleted the column readers | regression test only | S (tests) | no |
| #681 `list_datasets` recomputes the visible set and all consents per page | PARTLY COVERED | WP-SIMPL-41 PR 3 (decisions per page) + WP-OI-WEB-681 (gap) | consent hydration per page; full visible set per page; offset paging | M | yes |
| #682 describe a dataset by its GraphQL root field | NEW PLAN | WP-OI-WEB-682 (A bug fix, B feature) | no reverse lookup on MCP; wrong `SuggestedGraphQlField` formula | S | yes (B) |

Suggested order: #669 test PR and #668 now (bug fixes, independent); #682 A now; #682 B with or after WP-SIMPL-41 PR 3; #681 after WP-SIMPL-41 PR 3 and the catalog-scale snapshot work (#675, #677).

## #527 WebSQL: no cost estimate or per-caller quota on /api/v1/sql

- Verdict: OBSOLETE-CLOSE. Implemented by WP-SIMPL-51 I1; the merge PR #581 (`edeca24b`, commit "feat(sql): add sql statement cost estimator and admission gate (#527)") only carried "closes #533", so #527 stayed open.
- Evidence on main:
  - Shared quota: `src/Autheris.Application/Common/Quota/IQueryCostQuota.cs`, `QueryCostQuota.cs` (also used by `CostAndQuotaMiddleware`).
  - Estimate and gate: `src/Autheris.Application/Sql/Cost/SqlStatementCostEstimator.cs`; `GovernedSqlExecutor.Cost.cs` `AdmitCostAsync` (estimate > ceiling -> `SqlCostExceededException`, quota exhausted -> `SqlQuotaExhaustedException`, quota store failure -> `SqlQuotaUnavailableException`, fail closed), settle and `ChargeFailureFeeAsync`; async path in `WebSqlStatementManager.cs:201`.
  - Options: `WebSqlCostOptions` in `RestApiOptions.cs` (`MaxStatementCost` 2000, `CrossJoinCost` 250, ...).
  - Codes: `SqlCompilerErrorMapper.cs:131-133` (400 `QUERY_COST_QUOTA_EXCEEDED`, 429 with Retry-After, 503); `SqlErrorResponse.cs:52-58`.
  - Tests: `tests/Autheris.Tests.Unit/Sql/SqlStatementCostEstimatorTests.cs`; `tests/Autheris.Tests.Unit/Security/WebSqlCostQuotaTests.cs` (`OverCost_RejectedBeforeSourceConnection`, `RepeatedTimeouts_ExhaustQuota`, `Quota_IsPerCallerAndTenant`, `Quota_SharedWithGraphQl`, `TrinoRoute_And_Parquet_AreMetered`, `CrossSource_IsMetered`).
- Action: the owner closes #527 with a link to #581. No work package.

## #668 WebSQL: tables created through Dbt:DatabaseMappings are denied

- Verdict: NEW PLAN (bug fix; no freeze-exception needed).
- Problem: a dbt table whose database `D` is mapped to connection `C` gets the catalog entry `D.schema.table` with `SourceName = C` (`DbtCatalogTableFactory.TryResolveConnection`, `src/Autheris.Extensions/Dbt/DbtCatalogTableFactory.cs:110-135`). WebSQL resolves the requested name `D` and the governed catalog looks up a connection named `D`, which does not exist, so the table is denied. `WebSql:DataSourceMappings:D = C` does not help either.
- Verified state on main:
  - `SqlDataSourceResolver.TryResolveGloballyAllowedDataSource` (`src/Autheris.Application/Sql/Services/SqlDataSourceResolver.cs:91-93`) resolves a mapped name to itself; only `ResolveConnectionOptions` (`:118-135`) applies `WebSql:DataSourceMappings`, and the executor uses it (`GovernedSqlExecutor.cs:443`).
  - `GovernedSqlRewriter.cs:368` passes the logical name to `GovernancePolicyFactory` (`GovernancePolicyFactory.cs:119`) -> `IGovernedCatalogProvider.GetCatalogAsync(dataSourceName, ...)`.
  - `GovernedCatalogProvider.FindConnection` (`src/Autheris.Infrastructure/Persistence/Catalog/GovernedCatalogProvider.cs:195`) reads `DataSources:Connections[dataSourceName]` directly; `LoadAsync` (`:242-277`) then also requires `metadata.Table.SourceName == dataSourceName`, which fails for `D` vs `C` even if the connection lookup were fixed. `FingerprintMatchesAsync` (`:200`) and `GovernedCatalogProvider.PhysicalKeys.cs:35` use the same lookup.
  - Transport gate on this path: `GovernedSqlExecutor.cs:434` `EnsureCatalogSourceAsync(dsName)` with the logical name.
- Root cause: two names for one data source (logical/catalog domain vs connection key) and three components resolving them differently: the executor applies the WebSQL mapping, the governed catalog does not, the dbt factory applies its own mapping.
- Design:
  1. One resolution: `SqlDataSourceResolver.ResolveConnectionKey(string logicalName)` returns the mapped connection key (WebSQL mapping, else the name itself) or `null` when no connection exists. `ResolveConnectionOptions` uses it.
  2. The rewriter passes a `GovernedDataSource(string LogicalName, string ConnectionKey)` value to `GovernancePolicyFactory` and `IGovernedCatalogProvider.GetCatalogAsync`. `GovernedCatalogProvider` uses `ConnectionKey` for `FindConnection`, the dialect check and the fingerprint, and requires `metadata.Table.SourceName == ConnectionKey` (exact, case-insensitive as today). The table identifier keeps the logical domain. Cache keys include both names.
  3. Fail closed: no connection for the key, `SourceName` of the entry differs from the key, or the mapping target is empty -> table denied with a specific reason in `LogAdmissionDenied` (for example "catalog entry belongs to connection 'X', the request resolves to 'Y'"). A mapping never admits a table registered on another connection (no widening).
  4. Allowlists (`AllowedDataSources`, tenant data sources) stay keyed by the logical name, as documented in `RestApiOptions.cs:72-81`. The transport gate and the per-source profile are evaluated for the effective connection key (verify in step 1 which key the profiles use; if they are keyed by logical name, evaluate both and deny if either denies).
  5. Federation (`FederatedDuckDbExecutionService`, `routing.Tables[].EffectiveDataSource`) uses the same resolver; no second mapping code.
  6. Whether `Dbt:DatabaseMappings` should also act as the WebSQL mapping is an owner question (Q-WEB-1); default: no implicit coupling now, a startup warning names a dbt-mapped database that is in `WebSql:AllowedDataSources` without a WebSQL mapping.
- Work packages: WP-OI-WEB-668, one PR, ~150 net src (`SqlDataSourceResolver.cs`, `GovernedSqlRewriter.cs`, `GovernancePolicyFactory.cs`, `IGovernedCatalogProvider.cs`, `GovernedCatalogProvider.cs`, `GovernedCatalogProvider.PhysicalKeys.cs`, `GovernedSqlExecutor.cs`, startup validator). Bug fix under the freeze; no new option.
- Tests (red first):
  - Unit `GovernedCatalogProviderMappingTests`: `GetCatalog_MappedLogicalName_UsesConnectionKeyAndAdmitsTable`; S `GetCatalog_MappedName_EntryOnOtherConnection_Denied`; S `GetCatalog_NoConnectionForResolvedKey_DeniedFailClosed`; S `GetCatalog_EmptyMappingTarget_Denied`; `GetCatalog_CacheKey_IncludesConnectionKey` (mapping change never reuses a cached admission); `LoadAsync_DenialReason_NamesBothNames`.
  - Unit `SqlDataSourceResolverTests`: `ResolveConnectionKey_Mapped_ReturnsTarget`; S `ResolveConnectionKey_MappedName_NotInAllowedDataSources_StillDenied`; S `TenantAllowlist_AppliesToLogicalName`.
  - Unit S `GovernedSqlExecutorTransportGateTests.MappedLogicalName_GateEvaluatesEffectiveConnection`.
  - Integration (PostgreSQL container; SQL Server in the nightly suite) `WebSqlDbtMappedTableTests`: `Query_TableCreatedThroughDbtDatabaseMapping_WithWebSqlMapping_ReturnsRows`; S `Query_MappingToConnectionOfOtherTable_Denied`; `Query_WithoutWebSqlMapping_DeniedWithReason` (until Q-WEB-1 decides otherwise).
- Dependencies: none blocking. Touches `GovernedSqlRewriter.cs` and `GovernedSqlExecutor.cs`, also edited by WP-SIMPL-45 PR 4-6 and WP-SIMPL-46 PR 3-5: not in parallel with those PRs; whichever merges second rebases. Related: #666 (dbt `identifier`/`alias` as physical name, catalog cluster) changes the same dbt factory; no ordering need.
- Risks: per-source options keyed by logical vs connection name may disagree (step 4 covers it); existing deployments using the workaround (connection named like the dbt database) keep working because the mapping is optional.
- Size: S, ~150 src / ~350 test, 1 PR, 1.5 person-days.

## #669 SQL Server: synonyms are registered but always denied

- Verdict: OBSOLETE-CLOSE once the regression test below is merged.
- Evidence: the cited reader `SqlServerColumnMetadataReader.cs` was deleted by WP-SIMPL-44 PR 2 (`a8c248fd`, #645, "switch to declared catalog and delete column readers"). `GovernedCatalogProvider.LoadAsync` now takes the columns only from the governance catalog (`GovernedCatalogProvider.cs:271-277`: "The table has no columns defined in the governance catalog."), so a synonym registered with declared columns (dbt `columns:` or catalog API) is admitted and queried through its own name; SQL Server resolves the synonym. A synonym without declared columns is denied with that explicit reason, which is the clear log message the issue asked for. No code reads `sys.objects` types for columns any more (`ArchitectureNoSourceMetadataReadsTests` guards it, OD-25).
- Remaining source read: `SqlServerKeyIndexMetadataReader.cs` (keys/indexes for the refresh endpoint) returns nothing for a synonym; it is deleted by WP-SIMPL-44 PR 4 (#459), so nothing is planned here.
- Governance note: Autheris does not read source metadata (OD-25) and therefore cannot resolve a synonym to its base object. A synonym is governed as an independent catalog object with its own owner and grants (like a view). Q-WEB-2 asks the owner to confirm this.
- Work package: WP-OI-WEB-669, test-only PR (allowed under the freeze), ~0 src.
- Tests (SQL Server Testcontainers, nightly SQL Server suite plus PR smoke if a SQL Server smoke slot exists): `WebSqlSqlServerSynonymTests.Query_SynonymWithDeclaredColumns_ReturnsRows`; `Query_CrossDatabaseSynonym_ReturnsRows` (base object in a second database on the same server); `Query_SynonymWithoutDeclaredColumns_DeniedWithNoColumnsReason`; S `Query_SynonymColumnNotDeclared_DeniedByCatalog` (an undeclared base column is not reachable through the synonym); S `Query_BaseTableNotRegistered_DirectAccessDenied`.
- Size: S, ~200 test lines, 0.5 person-days.

## #681 list_datasets recomputes the full visible set and all consents per page

- Verdict: PARTLY COVERED. WP-SIMPL-41 PR 3 (not merged; only PR 1 #633 is on main) re-routes `list_datasets` to `IDiscoveryService` with one `IAccessDecisionPoint.DecideManyAsync` per page and no own consent loading (spec steps 5-6, D11) and cursor paging. The gap: the spec still loads all tenant tables per call (`DiscoveryService` step 1, `GetAllTablesAsync`), memoizes only per DI scope, and states no per-page bound or acceptance numbers.
- Verified state on main:
  - `McpDatasetCatalog.ListDatasetsAsync` (`src/Autheris.Application/Mcp/Services/McpDatasetCatalog.cs:187-300`): `VisibleSetAsync` (`:1163`) -> `ICatalogVisibilityService.GetVisibleAsync` -> `GetAllTablesAsync` plus `GetAllActiveConsentsForSubjectsAsync` (`CatalogVisibility.cs:344-400`); then offset paging in memory (`:292-294`); then `GetCallerConsentsAndRequestsAsync` (`:890-931`) loads all active consents of the caller a second time.
  - `CatalogVisibilityService` memo (`CatalogVisibilityService.cs:48`, `MemoKey` `:72`) lives in a scoped service (`GatewayCatalogServiceExtensions.cs:64`), so it never survives across pages.
  - `AccessDecisionPoint.DecideManyAsync` (`src/Autheris.Application/Policy/Pdp/AccessDecisionPoint.cs:199-215`) loops `DecideAsync` per table with the consent cache (`IConsentCacheService`).
- Root cause: visibility and access are evaluated over the whole catalog before paging, and consents are hydrated per call; there is no cross-request cache keyed by catalog and grant epochs.
- Design (amendment to WP-SIMPL-41 PR 3 plus one follow-up PR):
  1. Visibility is scope plus hidden flags (OD-19); it does not depend on the caller's grants, except the tenant allow-consent scope and the owner/admin view of hidden objects. Compute the candidate list from the shared catalog snapshot (#675) with paged/filtered reads (#677), sorted by dataset id, then cut the page, then call `DecideManyAsync` for the page objects only. No consent load outside the page.
  2. Keyset cursor `after` (last dataset id of the page, opaque, encodes the filter hash); `offset` stays accepted for one release and is capped. An invalid or mismatched cursor answers `InvalidArgument` (fail closed; never a restart from the first page and never an unfiltered list).
  3. Tenant allow-consent table set and the owner/delegate table set per caller come from one indexed query each (bounded by the caller's own rows), cached per request.
  4. Optional cross-request cache of the scoped candidate list keyed by (tenant, catalog epoch, caller scope key incl. claims hash); invalidated by the catalog epoch. Only if the measurement in the tests below misses the target after steps 1-3.
- Work packages: (a) spec amendment of WP-SIMPL-41 PR 3 (this section, no extra PR): decisions per page only, acceptance numbers below; (b) WP-OI-WEB-681, one PR after WP-41 PR 3, ~250 net src (`DiscoveryService`, cursor codec, `McpDatasetTools` argument, consent repository query for tenant allow-consent tables if missing). Freeze: performance fix inside the pre-approved WP-SIMPL-41 exception (cursor paging is in its spec). No interim fix in `McpDatasetCatalog` (rule 3 of the assessment: no fixes in code a later WP rewrites), unless the owner decides otherwise (Q-WEB-3).
- Tests:
  - Unit `DiscoveryPagingTests`: `ListPage_DecidesOnlyPageObjects` (decision point fake counts objects = page size); `ListPage_ConsentRowsRead_BoundedByPageSize` (repository fake counts rows and queries); `Cursor_FullListing_VisitsEachDatasetOnce`; `Cursor_FilterChanged_Rejected`.
  - Security (S): `Cursor_FromOtherCaller_DoesNotRevealHiddenDatasets` (a cursor is a position only; hidden objects are never returned); `CandidateCache_KeyIncludesCallerScopeAndTenant_NoCrossCallerReuse`; `CandidateCache_CatalogEpochChange_Invalidates` (a newly hidden table disappears on the next page); `ListPage_DecisionPointThrows_ReturnsEmptyNotUnfiltered`.
  - Benchmark (nightly, `tests/Autheris.Benchmarks` or the perf integration suite): 1k/10k/50k synthetic tables, a person with grants on all; acceptance: one page (limit 100) < 200 ms on 1 vCPU at 50k; total listing time linear in the table count (ratio 50k/10k <= 6).
- Dependencies: WP-SIMPL-41 PR 3 (after WP-46 PR 1, WP-43 PR 3b, both merged); catalog-scale cluster #675 (snapshot deltas) and #677 (paged reads), see `2026-11-13-plan-issues-catalog-scale.md`; #682 B shares the MCP argument surface.
- Risks: the per-table `DecideAsync` loop may dominate at 100 objects per page; if so, WP-46 batches the grant lookup per subject and page (note for WP-46, not planned here).
- Size: M, ~250 src / ~450 test, 1 PR plus the spec amendment, 3 person-days.

## #682 describe_dataset and describe_my_access: look up a dataset by its GraphQL root field

- Verdict: NEW PLAN. Part A is a bug fix; part B is a new tool argument (feature).
- Verified state on main:
  - The GraphQL model already has the reverse map: `CatalogSchemaModel.TablesByQueryFieldName` (`src/Autheris.GraphQL/Catalog/CatalogSchemaModel.cs:70,124`); root names are built at `:241` as `SanitizeGraphQlName($"{Domain}_{Schema}_{TableName}")`. `CatalogGraphQlMap` (`src/Autheris.GraphQL/Mcp/CatalogGraphQlMap.cs`) caches the model for 30 s and exposes only `GetTableAsync(TableIdentifier)` and `ResolveDocumentTablesAsync`.
  - `describe_dataset` / `describe_my_access` accept only `domain.schema.table` (`McpDatasetCatalog.TryParseDatasetId` `:1141`, `FindVisibleAsync` `:1197`, `DescribeMyAccessAsync` `:933-936`).
  - Bug: discovery and search suggest a root field that does not exist: `CatalogDiscoveryService.cs:431` and `CatalogSearchEngine.cs:388` build `SuggestedGraphQlField` as `{Domain}_{TableName}` (no schema, not sanitized), while the real field is `{Domain}_{Schema}_{TableName}` sanitized.
- Root cause: the root field is a lossy, sanitized derivation of the id; only the GraphQL model knows the mapping, and no tool exposes it.
- Design:
  - A (bug fix): `IGraphQlCatalogMap.ResolveRootFieldAsync(string field)` -> `TableIdentifier?` and `GetRootFieldAsync(TableIdentifier)`; `CatalogDiscoveryService` and `CatalogSearchEngine` take `SuggestedGraphQlField` from the map (null when the table has no root field), never from a string formula.
  - B (feature): `describe_dataset` and `describe_my_access` accept `graphQlField` as an alternative to `dataset` (exactly one of them; both or none -> `InvalidArgument`). Exact, case-sensitive lookup in the map, then the unchanged `FindVisibleAsync` path. Unknown field, field of a hidden or not visible table, and table without a root field all answer the same not-found result as an unknown id (same message, same shape, no timing branch on visibility). `graphQl.relations[]` gains `datasetGraphQlField` for targets that are visible to the caller (`VisibleRelationTargetsAsync` already filters); invisible targets keep today's output.
  - No `list_datasets` exact filter (proposal 2): one path is enough (Q-WEB-4).
- Work packages: WP-OI-WEB-682-A, ~60 net src, bug fix, any time. WP-OI-WEB-682-B, ~120 net src; folded into WP-SIMPL-41 PR 3 (MCP channel rework, pre-approved freeze exception) or, if PR 3 is merged first, a separate PR after it under `freeze-exception` (Q-WEB-4).
- Tests:
  - A: `CatalogGraphQlMapTests.ResolveRootField_ReturnsTable_ForSanitizedNameWithUnderscores`; `SuggestedGraphQlField_EqualsSchemaRootField` (discovery and search, table with schema and underscores in names); `SuggestedGraphQlField_NullWhenTableHasNoRootField`.
  - B: `DescribeDataset_ByGraphQlField_ReturnsSameAsById`; `DescribeMyAccess_ByGraphQlField_ReturnsSameAsById`; `Describe_BothOrNoneOfDatasetAndGraphQlField_InvalidArgument`; `Describe_GraphQlField_CaseMismatch_NotFound`. Security (S): `DescribeByGraphQlField_HiddenTable_AnswersLikeUnknown`; `DescribeByGraphQlField_TableOfOtherTenant_AnswersLikeUnknown`; `Relations_DatasetGraphQlField_OmittedForInvisibleTarget`; `DescribeByGraphQlField_ResponseShapeIdenticalForUnknownAndInvisible` (byte-equal error payload).
- Dependencies: A none; B with WP-SIMPL-41 PR 3 (same tool handlers). Related: catalog-search cluster #627 (search hits name the table without schema) touches `CatalogSearchEngine.cs`; A merges first or rebases.
- Risks: the 30 s model lifetime can describe a table removed seconds ago; `FindVisibleAsync` re-reads the catalog row, so a stale map entry only yields not-found.
- Size: S, ~180 src / ~300 test, 2 PRs, 2 person-days.
- Delivery: WP-OI-WEB-682-A UNDER REVIEW in PR #722 (together with WP-OI-SRCH-627). `CatalogDiscoveryService` sets `SuggestedGraphQlField` from the map on the index and fallback paths; `CatalogSearchEngine` emits null. WP-OI-WEB-682-B open (with WP-SIMPL-41 PR 3, Q-WEB-4).

## Open owner questions

| Id | Question | Recommended default |
|---|---|---|
| Q-WEB-1 | Should `Dbt:DatabaseMappings` also act as the WebSQL data-source mapping, so a dbt-mapped database needs no `WebSql:DataSourceMappings` entry? | No implicit coupling now; WP-OI-WEB-668 honours `WebSql:DataSourceMappings` consistently and warns at startup for dbt-mapped allowed databases without a WebSQL mapping; the two mappings are merged into one data-source alias option in WP-SIMPL-32 (configuration surface reduction) |
| Q-WEB-2 | A SQL Server synonym cannot be resolved to its base object without source-metadata reads (OD-25). Is a synonym governed as an independent catalog object with its own owner and grants? | Yes, like a view; operator note that registering a synonym of a governed table creates a second governed object |
| Q-WEB-3 | Interim fix of the per-page consent hydration in today's `McpDatasetCatalog` before WP-SIMPL-41 PR 3? | No; the fix lands in WP-41 PR 3 and WP-OI-WEB-681 (no fixes in code a later WP rewrites) |
| Q-WEB-4 | `graphQlField` lookup (#682 B): tool argument on `describe_dataset`/`describe_my_access` only, under the WP-SIMPL-41 exception? | Yes: argument only (no `list_datasets` filter), delivered inside WP-41 PR 3; if PR 3 has merged, a separate PR under `freeze-exception` with the issue link |

## Phase 3 review scope

- WP-OI-WEB-668: name resolution between logical data source and connection (no widening through mappings, cache keys, transport gate and per-source profile key).
- WP-OI-WEB-681 and the WP-SIMPL-41 PR 3 amendment: cursor design (position only, filter binding, no hidden-object leak), cross-request candidate cache key and epoch invalidation, fail-closed on decision errors.
- WP-OI-WEB-682 B: not-found equivalence for unknown, hidden and other-tenant root fields (no enumeration oracle), relation target field exposure.
- Not needed: #527 (closed, reviewed under WP-SIMPL-51 SEC-51-07..12), #669 (test-only).

## Owner decisions (2026-11-13)

The owner confirmed the recommended default of every open question (`Q-*`) in this file. The defaults in the question tables are binding for the implementation; a later owner decision wins. Phase 3 (security review) still applies before implementation starts.
