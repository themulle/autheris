# PLAN-OI-SRCH: Open issues, catalog search cluster (#624, #625, #626, #627, #628, #501, #571, #580)

- Date: 2026-11-13 (Phase 2, `csharp-architect`). Plan status: PLANNED; Phase 3 pending.
- Base: `origin/main` @ `10400ec4`. Code facts below were verified with grep on that commit; re-verify line numbers before editing.
- Origin plan: [`00-master-plan-overview.md`](00-master-plan-overview.md). WP specs: [`simplification/README.md`](simplification/README.md). Owner decisions OD-1..OD-45 in [PRD section 2.0](2026-11-05-req-project-simplification.md) are binding; a later OD wins. OD-4: catalog search including embeddings is core.
- Freeze (PRD 5.1): bug fixes, security fixes and tests are allowed; new options, endpoints, integrations or dependencies need an owner freeze-exception recorded in the PR. PR limit: <= 400 net added `src/` lines per PR (PRD 5.3).
- Security test convention: `[Trait("Category","Security")]` (S). Every search change keeps the existing restricted-table scrub (`CatalogDiscoveryService.ShapeHit`, GHSA-95f9-ffc7-vw2c) green.

## Summary

| Issue | Verdict | WP / spec | Open on main | Size | Phase 3 |
|---|---|---|---|---|---|
| #501 | NEW PLAN (assessed FIX-IN-CORE, security lane; no WP spec existed) | WP-OI-SRCH-501 | all three items | S-M, 1 PR | yes |
| #627 | NEW PLAN (bug) | WP-OI-SRCH-627 | all | S, 1 PR | no |
| #624 | NEW PLAN (tests only) | WP-OI-SRCH-624 | all | M (test only), 1 PR | no |
| #625 | NEW PLAN (bug; option already deleted) | WP-OI-SRCH-625 | all | S-M, 1 PR | yes (light) |
| #626 | NEW PLAN (relevance fix; freeze status Q-SRCH-3) | WP-OI-SRCH-626 | all | M, 1 PR | yes |
| #628 | PARTLY NEW PLAN: honesty fix now, in-process model after an owner decision | WP-OI-SRCH-628a..d | all | S now; L later (3 PRs) | yes |
| #571 | COVERED | [WP-SIMPL-52](simplification/WP-SIMPL-52-governance-data-model.md) PR 4b (D-52p, Q25 owner-confirmed) | all (WP-52 Draft, nothing merged) | covered by spec | yes (WP-52 review) |
| #580 | COVERED | [WP-SIMPL-54](simplification/WP-SIMPL-54-golden-queries-from-dbt.md) (after the feature freeze, Q26/Q27 owner-confirmed) | all (Draft, nothing merged) | covered by spec | yes (WP-54 review) |

Recommended order: WP-OI-SRCH-501 -> 627 -> 628a -> 624 -> 625 -> 626 -> (owner decision Q-SRCH-4) 628b..d. WP-52 PR 4b (#571) and WP-54 (#580) follow their spec gates. 625 and 626 touch `Bm25SearchIndex.cs` and merge serially; 501 and the scale cluster ([2026-11-13-plan-issues-catalog-scale.md](2026-11-13-plan-issues-catalog-scale.md): #675 debounce, #676 incremental index) touch `CatalogSearchWarmupService.cs` and `CatalogSearchEngine.RebuildIndexAsync`: 501 merges first, the scale WPs rebase.

## #501 Catalog search index: rebuild race, invisible staleness and no rebuild rate limit

- Verdict: NEW PLAN. Assessed FIX-IN-CORE (security lane, P2) in the simplification plan; no WP-SIMPL-36 sub-package carries it; nothing merged.
- Verified state on main:
  - Race confirmed: `src/Autheris.Application/Catalog/Search/CatalogSearchWarmupService.cs` `RebuildSafeAsync` calls `GetAllTablesAsync` (l. 126) and `RebuildIndexAsync` (l. 127) and only then reads `GetCatalogVersionAsync` into `_lastIndexedVersion` (l. 130). A change between l. 126 and l. 130 is marked as indexed.
  - Invisible failures confirmed: the catch at l. 138 only logs; `src/Autheris.Infrastructure/Health/GatewayHealthCheckService.cs` l. 179-188 adds component `CatalogSearchIndex` with a hard-coded `true`.
  - Rate limit: `ICatalogVersionProvider` is always registered (`GatewayCatalogServiceExtensions.cs` l. 41, `TryAddSingleton`), so the periodic path already skips unchanged versions (l. 104-107); the 15 s full rebuild without a provider no longer occurs. Still open: `catalog:changed` bursts are coalesced only by a 50 ms delay (l. 91), and `CatalogSearchOptionsValidator` does not validate `RefreshSeconds` (no reference in the validator).
- Root cause: version stamp read after the work; no rebuild outcome state; no minimum interval.
- Design:
  1. Read `GetCatalogVersionAsync` before `GetAllTablesAsync`; assign `_lastIndexedVersion` only after `RebuildIndexAsync` succeeded. A version read failure leaves `_lastIndexedVersion` unchanged (next tick rebuilds: fail toward freshness).
  2. Rebuild outcome record `CatalogSearchIndexStatus(LastSuccessUtc, LastAttemptUtc, LastErrorCode, IndexAge)` held by the warmup service and exposed via `ICatalogSearchEngine`/a small status interface. Error codes are a closed set (`REBUILD_FAILED`, `VERSION_READ_FAILED`, `EMBEDDING_FAILED`); never exception text (the readiness endpoint is anonymous, see #472 in [2026-11-13-plan-issues-security-identity-transport.md](2026-11-13-plan-issues-security-identity-transport.md)).
  3. `GatewayHealthCheckService`: component `CatalogSearchIndex` is Degraded (not Unhealthy; search is not a data path) when the last rebuild failed or index age > 3 x `RefreshSeconds` while a newer catalog version exists. Factor 3 is a constant (no new option).
  4. Minimum rebuild interval 5 s (constant) and single pending flag: events during a rebuild or inside the interval collapse into one follow-up rebuild. `CatalogSearchOptionsValidator` accepts `RefreshSeconds` = 0 or >= 5.
- Work packages: WP-OI-SRCH-501, one PR (~120 src, ~250 test). Freeze: bug/security fix, no approval needed; no new option.
- Tests (red first): `CatalogSearchWarmupServiceTests.VersionChangeDuringRebuild_TriggersSecondRebuild`, `.FailedRebuild_KeepsPreviousVersionStamp`, `.EventBurstOf100_CausesOneRebuild`, `.RebuildsAreAtLeastMinimumIntervalApart` (fake `TimeProvider`); `CatalogSearchOptionsValidatorTests.RefreshSeconds_1To4_Rejected`, `.RefreshSeconds_0And5_Accepted`; `GatewayHealthCheckServiceTests.CatalogSearchIndex_FailedRebuild_ReportsDegraded`, `.StaleIndex_ReportsDegraded`; S: `GatewayHealthCheckServiceTests.CatalogSearchIndex_DegradedDescription_ContainsNoExceptionText`.
- Dependencies: none blocking. Coordinates with #675/#676 (scale cluster; they replace the full rebuild with deltas and must keep the status record) and #472 (health endpoint exposure).
- Risks: a stuck rebuild lock delays freshness; the status record makes it visible. Health flapping under heavy catalog churn: Degraded only on age with a newer version pending.
- Owner questions: none (defaults from the issue comment by the owner, 2026-11-05).
- Size: S-M, ~120 src / ~250 test, 1 PR.

## #627 search_catalog: hits name the table without its schema

- Verdict: NEW PLAN (bug, wrong identifier).
- Verified state on main: `src/Autheris.Application/Mcp/Services/McpToolExecutionHandler.cs` l. 233 `table = $"{Domain}.{TableName}"`, l. 243 `target_table` same form; `find_relevant_tables` l. 271 uses `h.DatasetId ?? $"{Domain}.{TableName}"` (fallback without schema) and l. 277 `TargetTable` without schema. `CatalogDiscoveryService` sets `DatasetId = metadata.Identifier.ToString()` (l. 157, 207), which is the `list_datasets` id. The REST route `GET /api/v1/catalog/search` (`CatalogApiEndpoints.cs` l. 151) serializes the full `TableIdentifier` and is not affected.
- Root cause: hand-built identifier strings in the MCP handler instead of the canonical dataset id.
- Design: one helper `McpDatasetCatalog.DatasetIdOf(CatalogSearchHit)` = `h.DatasetId ?? h.TableIdentifier.ToString()` (and `TableIdentifier.ToString()` for relation targets); used by `search_catalog` (`table`, `target_table`) and `find_relevant_tables` (`Dataset`, `TargetTable`). `join_condition` keeps table names (it is a SQL hint). Breaking value change accepted (no production; changelog line). Field naming: Q-SRCH-1.
- Work packages: WP-OI-SRCH-627, one PR (~20 src, ~80 test). Freeze: bug fix.
- Tests: `McpTokenEfficientRetrievalTests.SearchCatalog_HitIdentifier_EqualsListDatasetsId`, `.SearchCatalog_SameTableNameInTwoSchemas_TwoDistinctHits`, `.SearchCatalog_JoinTarget_UsesFullIdentifier`, `.FindRelevantTables_FallbackIdentifier_IncludesSchema`; `CatalogChannelParityTests` (integration) asserts MCP hit id equals REST `datasetId`.
- Dependencies: none. #624 case "same name in two schemas" switches on with this PR.
- Risks: clients parsing `domain.table`; acceptable before production.
- Size: S, 1 PR.
- Delivery: UNDER REVIEW in PR #722 (together with WP-OI-WEB-682-A). Implementation note: the helper is `McpDatasetCatalog.DatasetIdOf(hit)` = the procedure id for a procedure, otherwise `McpDatasetCatalog.DatasetId(TableIdentifier)` (lower case), because the done criterion is equality with the `list_datasets` id; `TableIdentifier.ToString()` keeps the original case. The #624 case "same name in two schemas" switches on when the #624 relevance set exists.

## #624 Catalog search: a relevance test set with expected top hits

- Verdict: NEW PLAN (test only; allowed under the freeze).
- Verified state on main: tests exist only for invariants: `tests/Autheris.Tests.Unit/Security/CatalogSearchMetamorphicTests.cs`, `tests/Autheris.Tests.Unit/Catalog/CatalogSearchMultilingualTests.cs`, `Bm25SearchIndexTests.cs`, `SmartSchemaTokenizerTests.cs`. No ranked expectation set.
- Design: neutral fixture `tests/Autheris.Tests.Unit/Catalog/Relevance/catalog-relevance-fixture.json` (40 tables, German and English texts, German compounds, two schemas with a shared table name, a key column described as "... Kran-ID"-like in most tables) and `relevance-cases.json` (query, expected dataset ids, `maxRank`, `needs` = `baseline|field-weights|partial-words|word-forms|embeddings|full-identifier`, linked issue). Runner `CatalogSearchRelevanceTests` builds `CatalogSearchEngine` over the fixture (keyword mode always; hybrid only when a deterministic test embedding generator is available) and reports per case the rank of each expected table in the test output (`ITestOutputHelper`). Cases whose `needs` feature is not built are `Skip`ped with the issue link and switch on with the feature PR (each feature PR flips its flags in the same PR). Word forms ("Auftraege" -> "Auftrag") stay skipped until an owner decision on stemming (Q-SRCH-6).
- Work packages: WP-OI-SRCH-624, one PR (0 src, ~250 test lines plus fixture data).
- Tests: `CatalogSearchRelevanceTests.Case_ExpectedTablesWithinMaxRank` (theory over cases, >= 2 queries per row of the issue table), `.Fixture_ContainsSameNameInTwoSchemas`, `.EveryCaseNeedingAFeature_LinksAnIssue`; S: `.RestrictedCaller_FixtureHiddenColumnText_NeverProducesHit` (a caller without the column sees no hit for a term that occurs only in a Deny column).
- Dependencies: none; gate for #625, #626, #627, #628.
- Risks: a fixture tuned to the implementation; mitigated by writing cases from the issue observations before #625/#626 code.
- Size: M (test only), 1 PR.
- Delivery: UNDER REVIEW in PR #734, see [`2026-11-14-plan-issue-624-catalog-relevance-set.md`](2026-11-14-plan-issue-624-catalog-relevance-set.md) (deviations: the `full-identifier` cases check the `search_catalog` projection, two cases corrected after the first run).

## #625 Catalog search: field weights not applied

- Verdict: NEW PLAN. The issue premise changed: `CatalogSearchOptions.FieldWeights` was deleted as unconsumed by WP-SIMPL-06 (`fd41812a`, #535); it is listed in `tests/Autheris.Tests.Architecture/Baselines/removed-options.txt` l. 17 and the unknown-key guard rejects `Gateway:CatalogSearch:FieldWeights` (`UnknownGatewayKeyGuardTests.cs` l. 249). The ranking defect is unchanged.
- Verified state on main: `CatalogSearchEngine.RebuildIndexAsync` (l. 57-112) concatenates names, display names, descriptions, translations, column names, types and column descriptions into one document; `Bm25SearchIndex` (`K1` 1.5, `B` 0.75, l. 14-15) scores one token stream per document.
- Root cause: single-field BM25.
- Design: BM25F in `Bm25SearchIndex`: `IndexedDocument` holds per-field term frequencies and lengths for seven fields (technical name, display name, tags and owner, short description, long description, column names, column descriptions; translations go into the field of their source); weighted pseudo term frequency per document, one saturation, per-field length normalization against per-field averages. Weights are internal constants in `CatalogSearchFieldWeights` (10, 8, 5, 3, 2, 2, 1, the values of the deleted option). `MatchedTerms` stays a list of terms; an additional `MatchedFields` is not added (keeps the hit DTO stable). Re-introducing a configurable option is Q-SRCH-2. The restricted-table scrub (`ShapeHit` drops matched terms not in visible text and nulls the BM25 score) is unchanged.
- Work packages: WP-OI-SRCH-625, one PR (~150 src, ~200 test). Freeze: bug fix without a new option.
- Tests: `Bm25SearchIndexTests.TermInTechnicalName_OutranksTermOnlyInColumnDescriptions`, `.FieldWeightConstants_ChangeRanking` (internal constructor taking weights), `.PerFieldLengthNormalization_LongDescriptionDoesNotDominate`; relevance cases `needs=field-weights` switched on; S: `CatalogSearchMetamorphicTests.RestrictedTable_ScoreStaysNull_AfterBm25F`, `.HiddenColumnDescriptionTerm_NoHitForRestrictedCaller`.
- Dependencies: #624 merged first. Serial with #626 (same file).
- Risks: ranking shifts for existing clients; intended. Index memory grows by per-field dictionaries (~7x small maps per table); measured by the scale cluster's 10k/50k fixture.
- Owner questions: Q-SRCH-2.
- Size: S-M, 1 PR.

## #626 Catalog search: no match on parts of words (German compounds)

- Verdict: NEW PLAN. Freeze status: Q-SRCH-3.
- Verified state on main: `Bm25SearchIndex.Search` looks up query terms exactly (`_idfMap.TryGetValue(term)`, l. 95-96); `SmartSchemaTokenizer` splits on separators and camel case (`CasingSplitRegex`, l. 15) and folds umlauts/accents (l. 57-63), but never inside a word.
- Design: vocabulary scan at query time: for a query token of length >= 4 (constant), collect indexed tokens that contain it (ordinal, after the same folding), capped at 32 expansions per token (constant) to bound cost; expansions score with factor 0.4 of a whole-word match and contribute to the same field weights. Whole-word matches rank above partial matches of the same term (score cap). Matched terms report the indexed token; therefore the restricted-table scrub checks the indexed token against the visible text, so a partial match on hidden column text is dropped like a whole-word match. The vocabulary is held as a sorted array built with the index (no n-gram field, no dictionary; both rejected as larger and language-specific).
- Work packages: WP-OI-SRCH-626, one PR (~180 src, ~220 test).
- Tests: `Bm25SearchIndexTests.Betriebsstunden_FindsMotorbetriebsstunden`, `.Kran_FindsKranspezifikationen`, `.WholeWord_OutranksPartialMatch`, `.TokenShorterThan4_MatchesWholeWordsOnly`, `.ExpansionCap_BoundsCandidateTerms`; relevance cases `needs=partial-words` switched on; S: `CatalogSearchMetamorphicTests.PartialMatchOnHiddenColumnText_NoHitForRestrictedCaller`, `.PartialMatch_RestrictedTable_ScoreStaysNull`.
- Dependencies: #624, #625.
- Risks: noise on short common stems; mitigated by the minimum length, cap and lower weight, measured by #624. Query cost at 50k tables: vocabulary scan is O(vocabulary); measured in the scale fixture with a budget of 5 ms p95 per query token.
- Size: M, 1 PR.

## #628 Catalog search: local embedding provider returns no vectors; multilingual in-process model

- Verdict: PARTLY NEW PLAN. The honesty defect is a bug fixed now (628a); the in-process model (628b..d) adds a dependency and model files and needs an owner decision (Q-SRCH-4).
- Verified state on main: `src/Autheris.Application/Catalog/Search/LocalOnnxEmbeddingGenerator.cs` returns `ReadOnlyMemory<float>.Empty` unconditionally (l. 106-115) while `IsAvailable => _isValid` (l. 20) is true once model path and SHA-256 validate; `ModelIdentifier` is `local-onnx-minilm-l6-v2` (English). No project references an ONNX runtime. `CatalogSearchEngine` derives `effectiveMode` from `IsAvailable` (l. 239, 272-277), so a LocalOnnx deployment reports `hybrid` while running keyword search; the mode is set on `CatalogSearchHit.EffectiveMode` (l. 389) and returned by REST, but `search_catalog` (MCP) does not output it. Ollama (`OllamaEmbeddingGenerator.cs`) is the only working provider. Measured image size 373 MB (WP-SIMPL-10 baseline, before R2R removal) against the 325 MB budget (OD-37).
- Design:
  - 628a (now, bug): `LocalOnnxEmbeddingGenerator.IsAvailable` is false until a runtime exists, and `CatalogSearchOptionsValidator` fails startup for `EmbeddingProvider = LocalOnnx` with "not available in this build" (fail loud instead of silent keyword fallback). `search_catalog` outputs `mode` (effective mode) per response; one debug log per query with requested and effective mode.
  - 628b (after Q-SRCH-4): ONNX Runtime for .NET plus `Microsoft.ML.Tokenizers` in `LocalOnnxEmbeddingGenerator`; default model `multilingual-e5-small` (384 dims, matches `EmbeddingDimensions`), int8 quantized; existing SHA-256 and path checks stay mandatory; model and tokenizer files are mounted (Helm volume or init container), not baked into the default image (recommended default of Q-SRCH-4).
  - 628c: focused embedding text per table (names, display names, table descriptions, column names) instead of the BM25 concatenation; batched embedding during rebuilds; per-column vectors fused by the existing reciprocal rank fusion. Leak rule (Q-SRCH-5): for a caller with restricted columns, only the table-level vector (no column names or column text) may produce or rank a hit; a per-column vector contributes only when that column is visible to the caller and its catalog text is not `HIDDEN` (WP-52 D-52p).
  - 628d: latency budget test on a 5,000-table fixture (p95 query embedding + search <= 50 ms on the CI runner, documented).
- Work packages: WP-OI-SRCH-628a (~40 src, ~80 test, now); 628b (~250 src), 628c (~200 src), 628d (test only).
- Tests: 628a `LocalOnnxEmbeddingGeneratorTests.IsAvailable_FalseWithoutRuntime`, `CatalogSearchOptionsValidatorTests.LocalOnnx_FailsStartup_NotAvailableInBuild`, `McpTokenEfficientRetrievalTests.SearchCatalog_ReportsEffectiveMode`; 628b `LocalOnnxEmbeddingGeneratorTests.ModelHashMismatch_FailsStartup` (S), `.GeneratesNormalized384DimVector`; 628c S: `CatalogSearchMetamorphicTests.SemanticHit_RestrictedCaller_NeverFromHiddenColumnVector`, `.TableVector_ContainsNoColumnText`; relevance cases `needs=embeddings` switched on in hybrid mode; 628d `CatalogSearchLatencyTests.Hybrid5000Tables_WithinBudget`.
- Dependencies: #624; #676 (scale cluster: incremental re-embedding of changed documents) for large catalogs; WP-SIMPL-52 PR 4b for the `HIDDEN` rule; WP-SIMPL-35 footprint job for the image budget.
- Risks: image and memory growth (runtime ~20-40 MB native, model ~120 MB if baked in); supply chain of model files (hash pinning mandatory); CPU cost of full re-embedding at 10k+ tables without #676.
- Owner questions: Q-SRCH-4, Q-SRCH-5.
- Size: 628a S (1 PR); 628b..d L (3 PRs).

## #571 Catalog text of masked columns: governance decides whether a restricted scope sees it

- Verdict: COVERED by [WP-SIMPL-52](simplification/WP-SIMPL-52-governance-data-model.md) PR 4b (section 5.8, D-52p; Q25 owner-confirmed 2026-11-08: per classification rule with per-column override, default VISIBLE).
- Open on main: everything. WP-SIMPL-52 is Draft with Phase 3 pending; no `catalog_text`/`CatalogTextVisible` code exists. Today: Deny/invisible columns are already excluded from hits and matched terms (`CatalogDiscoveryService.ShapeHit`, l. 584-625; GHSA-95f9-ffc7-vw2c); `describe_dataset` reduces only semantics and value profile for masked columns (`McpDatasetCatalog.RestrictSemantics`, l. 495, 841).
- Gap added by this cluster: the D-52p indexing rule ("search never indexes text of columns with any HIDDEN rule") must cover every new search input: the BM25F column fields (#625), the partial-match vocabulary (#626) and the embedding texts and per-column vectors (#628c). PR 4b adds `CatalogSearchMetamorphicTests.HiddenCatalogText_NotIndexed_InAnyField`, `.HiddenCatalogText_NoPartialMatch`, `.HiddenCatalogText_NotInEmbeddingText` (S); whichever of WP-52 PR 4b and WP-OI-SRCH-625/626/628c merges later adds the test for its field.
- Size: per the WP-52 spec (PR 4b ~170 src, 1.5 pd).

## #580 Golden queries maintained by data owners (dbt meta)

- Verdict: COVERED by [WP-SIMPL-54](simplification/WP-SIMPL-54-golden-queries-from-dbt.md) (Q26: after the feature freeze; Q27: dbt meta only, no REST API; owner-confirmed 2026-11-08).
- Open on main: everything; WP-54 is Draft with Phase 3 pending. Golden queries still load only from `Gateway:GoldenQueries` (`GatewayOptions.cs` l. 34, `GatewaySections.cs` l. 78). Preconditions on main: WP-SIMPL-20 (20a-20e) merged; WP-SIMPL-44 PR 2 (declared catalog, #645) merged; WP-SIMPL-28 (dbt module), WP-SIMPL-41 and WP-SIMPL-52 PR 4b open.
- Gap: none. Note for the WP-54 developer: golden-query dataset references use the canonical dataset id (`TableIdentifier.ToString()`, with schema), consistent with WP-OI-SRCH-627.

## Open owner questions

| Id | Question | Recommended default |
|---|---|---|
| Q-SRCH-1 | #627: new field `dataset` or change the value of `table`/`target_table`? | Change the values to the canonical dataset id (no production, breaking allowed); no new field; changelog line. |
| Q-SRCH-2 | #625: re-introduce `Gateway:CatalogSearch:FieldWeights` as an option? | No. Internal constants (the deleted option's values); an option would grow the options ratchet and needs a freeze-exception; revisit after Stage 3 exit if an operator needs tuning. |
| Q-SRCH-3 | #626: is partial-word matching a bug fix (allowed under the freeze) or a feature? | Bug fix: search misses tables the listing finds; no new option, endpoint or dependency, so no ratchet grows. |
| Q-SRCH-4 | #628b-d: in-process ONNX runtime and a multilingual model during the freeze, and how to ship the model given the image budget (373 MB measured vs 325 MB)? | Build 628a now; 628b-d after the feature freeze (new dependency and capability, like WP-54), unless the owner grants a freeze-exception. Model files are mounted (Helm volume or init container) with mandatory SHA-256, never baked into the default image; the runtime package must fit the WP-35 footprint budget. |
| Q-SRCH-5 | #628c: may embeddings of column text rank tables for callers with restricted columns? | No. Restricted callers are ranked only by the table-level vector (no column text); per-column vectors count only for visible columns whose catalog text is not HIDDEN. |
| Q-SRCH-6 | #624 "word forms" row (Aufträge -> Auftrag): add a stemmer? | Not now; cases stay skipped and linked; partial matching (#626) covers most German plural forms; revisit with the #624 results after #626. |

## Phase 3 review scope

- WP-OI-SRCH-501: readiness/health exposure (no exception text on the anonymous readiness endpoint, closed error-code set), rebuild throttling as an availability control.
- WP-OI-SRCH-625 and 626: restricted-table text oracle (GHSA-95f9-ffc7-vw2c invariants) under per-field scoring and partial matches.
- WP-OI-SRCH-628a..d: fail-loud provider selection, model file integrity and supply chain, vector-based leakage of hidden or HIDDEN-text columns (Q-SRCH-5).
- #571 and #580 are reviewed with their specs (WP-SIMPL-52, WP-SIMPL-54); the cross-check tests listed under #571 are binding for whichever PR merges later.
- WP-OI-SRCH-624 and 627: no Phase 3 review needed (tests; identifier format of already visible datasets).

## Owner decisions (2026-11-13)

The owner confirmed the recommended default of every open question (`Q-*`) in this file. The defaults in the question tables are binding for the implementation; a later owner decision wins. Phase 3 (security review) still applies before implementation starts.
