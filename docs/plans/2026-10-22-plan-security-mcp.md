# Plan: Security review fixes, area MCP (HIGH and MEDIUM)

Source: `docs/plans/security.txt`. Scope: MCP-01, MCP-02 (HIGH); MCP-03, MCP-04, MCP-06, MCP-07, MCP-08, MCP-09, MCP-11, MCP-13, MCP-14 (MEDIUM).
LOW findings MCP-05, -10, -12, -15, -16, -17 are out of scope. Root causes were checked against `fix/security-review-open-findings` on 2026-10-22.
No production deployment exists, so breaking changes are allowed. Tests go in `tests/Autheris.Tests.Unit/Mcp/` unless a test says otherwise. Red tests come first.

## EP cross-references (the EP audit is still running)

- **EP-06** is withdrawn as a duplicate of MCP-01 and is covered here. Its extra detail (the GET also skips `Deny` filtering) is included in MCP-01. `2026-10-22-plan-security-ep.md` already lists EP-06 as out of scope.
- **EP-04** (the REST plan apply leaves out the tenant) names the MCP path as the correct reference: MCP passes `sessionContext.TenantId`. No MCP change is needed. The EP plan must keep MCP behaviour unchanged.
- **EP-08** (the ReBAC time-bound gate only applies to `user:` subjects) also affects MCP plan apply (`McpAdminTools` -> `IAccessPlanningService`). The EP plan owns the fix. Dependency: MCP gets the fix through the shared service. Add one MCP regression test to EP-08: `McpAdminPlanApply_BareSidGrant_IsTimeBound`.
- No other EP entry concerns the MCP gateway.

## Findings

**MCP-01 (HIGH, confirmed)**: GET synthetic-samples has no authorization or column filter.
Root cause: `CatalogApiEndpoints.cs:279-285` only calls `RequireAuthorization()`. `GetDatasetSyntheticSamplesHandler` (:643-661) resolves the dataset globally through `DatasetIdResolver.ResolveActiveAsync` and returns `PredefinedSyntheticSamples` or registered samples unchanged.
Fix: this GET is the curation counterpart of the PUT. Give both GET routes `RequireAuthorization(GatewayPolicies.GovernanceAdmin)`. Inject `ICatalogVisibilityService` and resolve the dataset with `FindVisibleAsync(caller, table)` so the admin's tenant confinement applies. On null, return the generic `DatasetKeysNotFound()`. Project rows onto `table.Columns`: keys that are not table columns are dropped. Consumers get synthetic rows only through `GET /api/v1/catalog/datasets/{dataset}/sample-rows?synthetic=true`, which is already guarded (`McpDatasetCatalog.SampleRowsAsync`, synthetic path).
Fail-closed: a non-admin gets 403. A dataset that is invisible or belongs to another tenant gets the same 404 as an unknown dataset.
Tests: `SyntheticSamplesGet_NonAdmin_Returns403`, `SyntheticSamplesGet_OtherTenantDataset_Returns404`, `SyntheticSamplesGet_HiddenDataset_SameBodyAsUnknown`, `SyntheticSamplesGet_DropsKeysNotInTableColumns` (Integration, `WebApplicationFactory`).
Breaking: consumers can no longer read this GET.

**MCP-02 (HIGH, confirmed)**: a dbt `meta` sample string is read as a file path.
Root cause: in `DbtArtifactStreamingParser.cs`, `ExtractSyntheticSamples` (:676) -> `FindFilePath` (:796, `Path.IsPathRooted`, relative to CWD and BaseDirectory) -> `File.ReadAllText` (:749) / `ParseCsvSamples` (:812, `File.ReadAllLines`).
Fix: delete `FindFilePath` and `ParseCsvSamples` and the whole string branch, so only inline JSON arrays are accepted. Remove the `samples` alias, keeping `synthetic_samples`, `syntheticSamples`, `sample_rows` and `sampleRows`. Change the signature to `ExtractSyntheticSamples(JsonElement metaProp, IReadOnlyCollection<string> modelColumns)`. It caps rows at 100 (`MaxSyntheticSampleRows`), drops keys that are not model columns, accepts only scalar values (string, number, bool, null), caps each string at 1024 chars (longer strings are skipped) and caps each row at 256 keys. Share the 100-row constant with the PUT endpoint.
Fail-closed: a string value or an oversized structure yields `null` (no samples) plus a parser warning. The file system is never touched.
Tests (Extensions.Tests): `ExtractSyntheticSamples_StringPath_IgnoredAndNoFileRead` (absolute path, `../` path and a real temp file must all give null), `..._SamplesAliasIgnored`, `..._MoreThan100Rows_Truncated`, `..._UnknownKeys_Dropped`, `..._NestedObjectValue_Dropped`.
Breaking: dbt projects that relied on file samples must inline them.

**MCP-03 (MEDIUM, confirmed)**: predefined samples ignore Mask and the sensitive fallback.
Root cause: the predefined branch of `SyntheticSampleRowGenerator.GenerateRows` (:58-91) defaults to `ColumnAccessLevel.Clear` and handles only `Deny`.
Fix: move level resolution into one private method `ResolveLevel(TableMetadata, TableColumn, IReadOnlyDictionary<string,ColumnAccessLevel>?)` and use it in both branches. Rules: a null map means all columns are `Deny`. With a map, a missing entry is `Deny`. An explicit `Clear` on a column that is `IsSensitive` or in `ColumnMaskingRules` becomes `Mask`. Predefined values are emitted only for `Clear`. `Mask` columns use `GenerateSafeSensitivePlaceholder(column, rowIndex)`. Fix the class doc contract.
Check: today the generated branch treats a null map as Clear-or-Mask. All callers (`McpDatasetCatalog` :1036, profiling endpoints) pass a map from `ResolveColumnLevels`; grep for other `GenerateRows(` callers and pass a full map.
Tests: `GenerateRows_Predefined_MaskColumn_EmitsPlaceholder`, `..._Predefined_SensitiveColumnNoEntry_NotEmitted`, `..._NullLevelMap_EmitsOnlySyntheticMarker`, `SampleRowsSynthetic_ReleasedFullViewerWithoutConsent_NoRealMaskValues`.

**MCP-04 (MEDIUM, confirmed)**: the `get_table_schema` `datasets[]` array skips ABAC.
Root cause: `McpDatasetTools.TargetTable` (:84-90) returns only `dataset`, or `CatalogTable` when only `datasets` is given. `AiDataGuardrailService` :272 runs ABAC over that single table. `McpToolExecutionHandler` :236-307 describes every entry of the array, and the array has no size limit.
Fix: add `McpDatasetTools.TargetTables(string toolName, string? argumentsJson) : IReadOnlyList<TableIdentifier>?`, which returns the distinct union of `dataset` and `datasets`. `ParseTablesFromTool` uses it for `get_table_schema`, so ABAC and four-eyes cover every entry. Add `MaxSchemaBatch = 20`. More entries, an entry that is not a string or an entry that does not parse is rejected with `InvalidParams` in the guardrail before any repository call, and again in the handler as defense in depth.
Fail-closed: if any entry is denied, the whole call is denied.
Tests: `GetTableSchema_DatasetsArray_AbacDenyOnEntry_DeniesCall`, `GetTableSchema_21Entries_InvalidParams_NoRepositoryCall`, `TargetTables_UnionOfDatasetAndDatasets`.

**MCP-06 (MEDIUM, confirmed)**: MCP `sample_rows` ignores `synthetic`.
Root cause: the default arm in `GatewayMcpQueryExecutor.cs:299` calls the overload with three arguments.
Fix: add an explicit arm, `McpDatasetTools.SampleRows => _datasetCatalog.SampleRowsAsync(principal, dataset, ToCount(variables), Bool("synthetic") ?? false, ct)`. A non-boolean `synthetic` yields `InvalidParams`. The guardrail keeps classifying `sample_rows` as reading rows (`ReadsRows(toolName)`). Synthetic requests are not relaxed.
Dependency: land MCP-03 first or in the same WP, because this makes the MCP-03 path reachable over MCP.
Tests: `SampleRows_SyntheticTrue_CallsSyntheticPath_NoTableQuery`, `SampleRows_SyntheticTrue_WritesMetadataSyntheticSampleAudit`, `SampleRows_SyntheticString_InvalidParams`.

**MCP-07 (MEDIUM, confirmed)**: the query-embedding cache has no bound and generation is synchronous.
Root cause: `CatalogSearchEngine.cs:29` is a `ConcurrentDictionary`, filled with `GetOrAdd` at :347. `OllamaEmbeddingGenerator.cs:78-79` uses `GetAwaiter().GetResult()` with `CancellationToken.None`.
Fix: replace the dictionary with a bounded LRU cache (new `internal sealed class BoundedEmbeddingCache(int capacity, TimeSpan ttl)`, default 2048 entries and 30 min, configured via `Catalog:Search:QueryEmbeddingCache*`). Empty results are not cached. Add `ICatalogSearchEngine.SearchAsync(CatalogSearchQuery, CancellationToken)`, which calls `GenerateNormalizedEmbeddingsAsync(..., ct)`. Migrate the MCP tools (`search_catalog`, `find_relevant_tables`) and REST `/catalog/search` to it, and remove the synchronous `Search` path when it falls back to Ollama. Truncate query text to 512 chars before embedding and hashing.
Fail-closed: when embedding fails or times out, that request uses keyword-only search and nothing is cached.
Tests: `QueryEmbeddingCache_ExceedsCapacity_Evicts`, `QueryEmbeddingCache_EmptyResult_NotCached`, `SearchAsync_PropagatesCancellation`, `SearchAsync_LongQuery_TruncatedBeforeEmbedding`.
Breaking: `ICatalogSearchEngine` gains a method, and callers move to it.

**MCP-08 (MEDIUM, confirmed)**: doc tools skip the token budget, and `limit` has no bounds.
Root cause: the doc branch at `AiDataGuardrailService.cs:212-222` returns before the budget block at :472; `McpDocTools.cs:218-220` calls `GetInt32()`.
Fix: extract `private string ApplyTokenBudget(string toolName, string json, out int estimatedTokens)` from :472-500 and call it in the doc branch after scrubbing. In `McpDocTools`, read `limit` with `TryGetInt32` and clamp it to `1..20`. Non-integer values yield `InvalidParams`.
Tests: `SearchDocs_LimitMaxInt_ClampedTo20`, `SearchDocs_LimitFraction_InvalidParams`, `DocTool_OversizedResult_TruncatedToMaxTokensPerCall`.

**MCP-09 (MEDIUM, confirmed)**: the injection guardrail can be bypassed and never inspects output.
Root cause: `SemanticPromptGuardrail` regexes (:24-56) match exact phrases; normalisation does no confusable folding; `Base64SegmentRegex` decodes only at the aligned offset. `AiDataGuardrailService` :166 evaluates only `ArgumentsJson`.
Fix: (a) Hardening the input: add a confusable skeleton fold (a compact Cyrillic/Greek-to-Latin map in a new `ConfusableFold` static, applied after NFKC). Make the override regex accept plural and singular forms and the synonyms `previous|prior|above|earlier` and `instructions?|rules?|prompts?`. Decode base64 at offsets 0-3 and also accept URL-safe base64. (b) Inspecting output (main fix): add `PromptGuardrailEvaluation EvaluateToolOutput(string toolName, string resultJson)`. It walks the string values of the JSON with `Utf8JsonReader`, with no allocation for each value beyond normalisation, capped at 256 KB inspected. Call it in `ExecuteToolWithGuardrailAsync` after scrubbing and before the budget check, for every tool including doc tools, and for native and semantic resource reads (see MCP-13 and MCP-14). On a match, replace the string value that matched with `"[content withheld: suspected prompt injection]"`, set `McpToolCallResult.IsMasked`, and write audit `MCP_OUTPUT_INJECTION_SUSPECTED`. The call is not denied, so a poisoned text cannot be used to block catalog access.
Tests: `Input_HomoglyphIgnorePrevious_Detected`, `Input_Base64MisalignedOffset_Detected`, `Input_UrlSafeBase64_Detected`, `Output_DescribeDataset_InjectedDescription_Withheld`, `Output_SearchDocs_InjectedSection_WithheldAndAudited`, `Output_BenignCatalogText_Unchanged`.

**MCP-11 (MEDIUM, confirmed)**: fragment spreads are expanded without memoisation.
Root cause: `CatalogGraphQlMap.cs:49-186` walks each `FragmentSpreadNode` again for every spread, collects into `List<TableIdentifier>` (:69), and is bounded only by `MaxSelectionDepth = 32`.
Fix: add a `Walk` context class holding: `Dictionary<string, (bool Ok, HashSet<TableIdentifier> Tables)>` to memoise fragment results, `HashSet<string>` for the fragments in progress (a cycle returns `false`), `HashSet<TableIdentifier>` for the output, and a counter of visited selections with `MaxVisitedSelections = 10_000`. If the counter is exceeded, a cycle is found or the depth limit is hit, `ResolveDocumentTablesAsync` returns `null`, so the guardrail cannot resolve tables and denies the call. Also cap the document length at 64 KB before parsing.
Tests (`CatalogGraphQlMapTests`): `FragmentBomb30Levels_CompletesUnder1s_ReturnsDistinctTables`, `CyclicFragments_ReturnsNull`, `VisitedSelectionsCapExceeded_ReturnsNull_GuardrailDenies`.

**MCP-13 (MEDIUM, confirmed)**: native resource reads bypass the guardrail pipeline.
Root cause: `GatewayMcpServer.ReadResourceAsync` sends `IsNativeResource` URIs straight to `ReadNativeResourceAsync` (:149-157). Only `autheris://datasets/` goes through `CallGuardedToolAsync`.
Fix: route `autheris://catalog/datasets/{id}/schema` through `CallGuardedToolAsync(DescribeDataset, {dataset})` and render the Markdown from the guarded JSON result. Route `catalog/summary` through `ListDatasets` and `governance/my-access` through `DescribeMyAccess`. For `catalog/datasources`, use the guarded `ListDatasources`. The static API docs and OpenAPI stay direct; they hold no tenant data. Add `McpMarkdown.EscapeCell(string)`, which escapes `|`, backslash and backtick and turns CR/LF into spaces, and use it for every table cell in `McpResourceProvider`.
Fail-closed: a guarded error yields `ResourceNotFound`, with the same message for denied and missing.
Tests: `ReadSchemaResource_AbacDeniedOnTable_ResourceNotFound`, `ReadSchemaResource_WritesMcpToolExecutionAudit`, `CatalogSummary_PipeAndNewlineInDescription_Escaped`, `ReadSchemaResource_OverBudget_Truncated`.

**MCP-14 (MEDIUM, confirmed)**: semantic resources ignore ReBAC and are rebuilt on every call.
Root cause: `SemanticMcpCompiler.cs:129` and `AiDataGuardrailService.cs:373,436` use the static `McpCatalogVisibility.VisibleTablesAsync`, which has no ReBAC step. `GetSemanticResourcesAsync` builds everything, and `ReadResourceAsync` (:177-183) rebuilds the list to read one URI.
Fix: (a) Replace all three call sites with `ICatalogVisibilityService.FilterVisibleAsync(caller, candidates, ct)` (ReBAC included). Delete `McpCatalogVisibility` once it has no callers. The guardrail four-eyes pre-check and `get_golden_queries` then use the same visibility. (b) Split the interface into `ListSemanticResourceUrisAsync(principal, cursor, pageSize = 100, ct) -> (IReadOnlyList<McpResourceItem> Items /* no Text */, string? NextCursor)` and `ReadSemanticResourceAsync(principal, uri, ct) -> McpResourceItem?`. The read parses the URI, checks visibility for that one table, then renders it. Golden queries are loaded only on read. Wire up MCP `resources/list` pagination (`ListResourcesRequestParams.Cursor`).
Fail-closed: a URI for an invisible or unknown table yields `ResourceNotFound`. Missing `ICatalogVisibilityService` yields an empty list.
Tests: `SemanticResources_RebacDeniedTable_NotListedNotReadable`, `GetGoldenQueries_RebacDenied_Empty`, `FourEyesPreCheck_UsesRebacVisibility`, `ResourcesList_Paginates100_WithCursor`, `ResourceRead_RendersSingleTableOnly` (the mock asserts `GetGoldenQueriesAsync` is called once).
Breaking: `ISemanticMcpCompiler.GetSemanticResourcesAsync` is replaced.

## Work packages (in order)

| WP | Findings | Size | Notes |
|----|----------|------|-------|
| WP-MCP-1 | MCP-02, MCP-03, MCP-06, MCP-01 | M (~350 LOC + tests) | All synthetic-sample leaks. MCP-03 must land before MCP-06. |
| WP-MCP-2 | MCP-04, MCP-08, MCP-11 | M (~300) | Guardrail coverage and limits; mostly `AiDataGuardrailService`, `CatalogGraphQlMap`. |
| WP-MCP-3 | MCP-13, MCP-14 | L (~500) | Resource paths; depends on WP-MCP-2's `ApplyTokenBudget`. |
| WP-MCP-4 | MCP-09 | M (~350) | Output inspection hooks into the resource paths from WP-MCP-3. |
| WP-MCP-5 | MCP-07 | M (~250) | Independent; can run in parallel with WP-MCP-2 to WP-MCP-4. |

Dependencies on other areas: EP-08 (EP plan) for MCP plan apply. The GQL plan does not touch `CatalogGraphQlMap` (checked). The INF epoch work has no overlap. Hot files: WP-MCP-2, -3 and -4 all change `AiDataGuardrailService.cs`, so run them one after another.

## Security conditions for Phase 3

1. Every new limit (schema batch 20, doc limit 20, 10k visited selections, 64 KB document, 512-char query, 100 sample rows) is enforced before any repository, consent, ReBAC or Ollama call.
2. Denied, invisible and unknown resources look the same: same status, same body, same error code (MCP-01, -13, -14).
3. A null or partial column-level map never results in `Clear` (MCP-03). Real predefined values appear only for `Clear` columns.
4. The dbt parser has no `System.IO.File` dependency. Add an architecture test: `DbtArtifactStreamingParser_DoesNotReferenceSystemIoFile`.
5. Output inspection masks and audits but does not deny. Its inspected size has a cap, and it adds no allocation for each value beyond normalisation.
6. A synthetic sample request never weakens the guardrail's classification as row-reading or the four-eyes check.
