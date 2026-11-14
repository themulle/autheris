# Phase 3 security review: open-issue plans, priority group 6 (catalog search)

- **Date:** 2026-11-14 · **Phase:** 3 (`csharp-security-expert`) · **Base verified:** `origin/main` @ `7a4df959`
- **Reviewed:** [`2026-11-13-plan-issues-catalog-search.md`](2026-11-13-plan-issues-catalog-search.md) sections #501, #625, #626, #628 (WP-OI-SRCH-501, -625, -626, -628a..d). Owner defaults `Q-SRCH-1..6` are binding (2026-11-13); this review only adds conditions on how they are implemented.
- **Ids:** `SEC-OI-<issue>-<n>`. Every test below carries `[Trait("Category","Security")]`. A condition is binding for Phase 4 and Phase 5. Standing invariant for the whole cluster: GHSA-95f9-ffc7-vw2c (a restricted caller never learns text, terms or ranking evidence of a column or table it cannot see).

| Issue | WP | Verdict | Developer-ready |
|---|---|---|:---:|
| #501 | WP-OI-SRCH-501 | APPROVED WITH CONDITIONS | yes |
| #625 | WP-OI-SRCH-625 | APPROVED WITH CONDITIONS (ranking oracle through column fields) | yes, after #624 |
| #626 | WP-OI-SRCH-626 | APPROVED WITH CONDITIONS (query DoS bounds, expansion oracle) | yes, after #625 |
| #628 | 628a fail-loud | APPROVED WITH CONDITIONS (light) | yes |
| #628 | 628b model loading | APPROVED WITH CONDITIONS (supply chain, native runtime) | after Q-SRCH-4 (post-freeze) |
| #628 | 628c focused text and per-column vectors | CHANGES REQUIRED (plan text contradicts Q-SRCH-5) | after amendment below |
| #628 | 628d latency test | APPROVED | yes |

Order and scope changes: SEC-OI-628-5 (table-level vector text excludes column names; the plan's focused text includes them). #625/#626 add per-caller score computation for restricted-column tables (about +40 src each).

## #501 Rebuild race, staleness, rate limit

**Verified.** `CatalogSearchWarmupService.cs:126-130` reads the version after `RebuildIndexAsync` (race confirmed); `GatewayHealthCheckService.cs:188` reports `CatalogSearchIndex` with a hard-coded `true`.

**Threats.**
- A stale index keeps text of a column that became hidden or masked until the next rebuild. Safe only if the hit shaping (`CatalogDiscoveryService.ShapeHit`) evaluates visibility and the matched-term scrub against **current** metadata at query time, not at index time.
- A failed or partial rebuild swapped in as the new index turns an availability bug into empty or wrong results; a failed one that discards the old index fails closed but silently.
- The 5 s minimum interval with a single pending flag can lose the last event of a burst if the flag is cleared before the follow-up rebuild reads the version.
- `RefreshSeconds = 0` must not become a zero-delay loop; the anonymous readiness body must not carry versions, counts, timestamps or exception text.

**Conditions.**
- SEC-OI-501-1: the new index is built completely and published by one atomic reference swap; a failure keeps the previous index and is reported (never an empty or half-built index). `_lastIndexedVersion` is assigned after a successful swap with the version read **before** `GetAllTablesAsync`; a version-read failure leaves it unchanged.
- SEC-OI-501-2: the pending flag is cleared only after the follow-up rebuild has read its version; a burst always ends with one rebuild that sees the final version (test with a fake `TimeProvider`).
- SEC-OI-501-3: `RefreshSeconds` 0 means "no periodic rebuild" (event driven only), 1-4 rejected at startup, upper bound 3600; the loop never awaits `Delay(0)`.
- SEC-OI-501-4: the `CatalogSearchIndex` description is a closed set (`ok`, `rebuild_failed`, `stale`, `version_read_failed`, `embedding_failed`); no counts, versions, timestamps or exception text; evaluated from the status record, no per-request rebuild or database read.
- SEC-OI-501-5: query-time visibility and scrub use current metadata (verify in `ShapeHit`); a test hides a column after indexing and asserts no term, no hit and no score evidence before the next rebuild.
- Tests: the plan names plus `CatalogSearchWarmupServiceTests.FailedRebuild_KeepsPreviousIndexServing`, `.PendingFlagClearedAfterFollowUpReadsVersion`, `CatalogSearchOptionsValidatorTests.RefreshSeconds_Above3600_Rejected`, `CatalogSearchMetamorphicTests.ColumnHiddenAfterIndexing_NoTermNoHitBeforeRebuild`, `GatewayHealthCheckServiceTests.CatalogSearchIndex_Body_HasClosedCodeOnly`.

## #625 BM25F field weights

**Threats.** The index is global, the caller is not. With seven fields, a restricted caller who sees a table but not some columns can still move the table up the ranking through a term that occurs only in a hidden column's name or description (rank oracle; the existing null score on restricted tables hides the number, not the order). `MatchedTerms` scrub alone does not close it.

**Conditions.**
- SEC-OI-625-1: for a caller that has any non-visible column on a table, the table's score is computed from the table-level fields only (technical name, display name, tags/owner, descriptions); column-name and column-description fields are excluded, and a table that matches only through those fields is dropped for that caller. Visible columns may contribute only for callers with full column visibility.
- SEC-OI-625-2: HIDDEN catalog text (D-52p / WP-SIMPL-52 PR 4b) is never indexed in any field; the test of #571 for the BM25F fields lands with this PR if it merges after 4b, otherwise with 4b.
- SEC-OI-625-3: weights are positive finite constants, per-field average length is guarded against zero (no NaN or infinity score; such a document scores 0, never throws), and the field set is closed (no user-defined fields).
- SEC-OI-625-4: index memory is bounded by the field-count constant; the scale fixture (SCALE-0) records bytes per table before and after.
- Tests: the plan names plus `CatalogSearchMetamorphicTests.HiddenColumnTerm_DoesNotChangeRankForRestrictedCaller` (same ranking with and without the hidden term in the corpus), `.TableMatchingOnlyThroughHiddenColumnField_NotReturned`, `Bm25SearchIndexTests.ZeroLengthField_DoesNotProduceNaN`.

## #626 Partial-word matching

**Verified.** `Bm25SearchIndex.Search` looks up exact terms (`_idfMap.TryGetValue`, l. 95-96); plan adds a vocabulary scan.

**Threats.**
- Query-time cost is O(vocabulary x query tokens): an anonymous-equivalent client can send many tokens (no cap in the plan on the number of query tokens or query length).
- Expansion oracle: a partial query ("Kran") expands to indexed tokens from hidden text; the matched token reveals it, and the 32-expansion cap lets hidden-table tokens crowd out visible ones (observable recall difference).
- Folding mismatch (umlaut/Turkish-I) between index and query changes which tokens match.

**Conditions.**
- SEC-OI-626-1: bounds before the scan: query length at most 256 chars, at most 8 tokens expanded per query, at most 32 expansions per token and 128 per query, minimum token length 4 measured **after** folding; over-cap input is truncated deterministically, not rejected with a distinguishing error.
- SEC-OI-626-2: the vocabulary is built only from text that may be indexed (no HIDDEN text); expansions are ordered by ordinal token value before the cap (deterministic, independent of caller); no "truncated" or "expanded" flag appears in any response.
- SEC-OI-626-3: a partial match contributes only through fields allowed for the caller under SEC-OI-625-1; the reported `MatchedTerms` entry passes the same visible-text scrub as a whole-word term; expansions of a token never reach columns the caller cannot see.
- SEC-OI-626-4: folding and comparison are ordinal, culture-invariant (`StringComparison.Ordinal` after the shared normalizer); test under `tr-TR`.
- SEC-OI-626-5: p95 budget 5 ms per query token at 50k tables is a test with the SCALE-0 fixture; exceeding it fails the PR (availability control).
- Tests: the plan names plus `Bm25SearchIndexTests.QueryWith1000Tokens_IsBoundedAndFast`, `.Expansion_UsesTurkishCultureInvariantFolding`, `CatalogSearchMetamorphicTests.HiddenTokenCannotCrowdOutVisibleExpansion`, `.PartialMatch_ThroughRestrictedColumnField_NotReturned`.

## #628 Embeddings

**628a (verified `LocalOnnxEmbeddingGenerator.cs:20,106-115`: `IsAvailable` true, empty vectors).**
- SEC-OI-628-1: unavailable provider selection fails startup outside Development and in Development (no silent keyword fallback); message names the setting, not the model path. The `mode` field in `search_catalog` is only `keyword|hybrid|semantic` (closed set), the debug log carries the two mode names only.
- SEC-OI-628-2: a generator returning an empty or wrong-dimension vector never counts as success: the engine treats it as unavailable for that document and the effective mode drops to keyword; a zero vector is never indexed (cosine of zero vectors ranks every table equally).

**628b (post-freeze).**
- SEC-OI-628-3: SHA-256 is verified over the bytes that are then loaded (read once into memory or a locked stream; no hash-then-reopen by path); model **and** tokenizer/vocabulary files are pinned; configured paths are canonicalised, symlinks resolved and required to stay inside the configured model directory. ONNX session options disable custom-op library registration and external-data paths outside that directory.
- SEC-OI-628-4: bounds: maximum token count per input (model limit, truncate), batch size cap, a concurrency semaphore for inference, a per-call timeout; the query-embedding cache stays bounded. Model files are never downloaded at runtime (mounted only); a hash mismatch fails startup naming the setting only.

**628c (CHANGES REQUIRED).** Plan text: the focused embedding text contains "names, display names, table descriptions, column names", while Q-SRCH-5 and the test `TableVector_ContainsNoColumnText` require a table vector without column text. A table vector containing column names is a similarity oracle for hidden columns.
- SEC-OI-628-5: the table-level vector text is table name, display name and table description only; column names and descriptions appear only in per-column vectors, built only for columns without HIDDEN catalog text.
- SEC-OI-628-6: visibility filtering happens before fusion and before top-k truncation (filter the candidate set per caller, then rank), so hidden-column vectors never occupy or displace result slots; a per-column vector contributes for a caller only when that column is visible and unmasked for the caller.
- SEC-OI-628-7: the embedding cache key (#676) contains provider, model id and dimension plus the SHA-256 of the scrubbed text; text is scrubbed before hashing; the cache stores vectors only and is bounded.
- SEC-OI-628-8: a remote provider (Ollama) receives only the scrubbed table-level and permitted per-column texts, over the secure outbound handler; the Phase 4 developer verifies the existing egress policy applies and logs host only.
- Tests: `LocalOnnxEmbeddingGeneratorTests.EmptyVector_IsNotIndexed`, `.TokenizerFileHashMismatch_FailsStartup`, `.ModelPathOutsideDirectory_FailsStartup`, `.OversizedInput_IsTruncated`, `CatalogSearchMetamorphicTests.HiddenColumnVector_DoesNotChangeRankForRestrictedCaller`, `.TopK_AppliedAfterVisibilityFilter`, `CatalogSearchEmbeddingCacheTests.KeyIncludesModelAndDimension`.

## Anything the plans missed (summary)

1. Column-field scoring is a ranking oracle for restricted callers (SEC-OI-625-1).
2. Partial matching: unbounded query tokens, crowd-out and expansion oracle (SEC-OI-626-1/2).
3. 628c embedding text contradicts Q-SRCH-5 (SEC-OI-628-5); post-filter before top-k (SEC-OI-628-6).
4. Hash-then-reopen race on model files; tokenizer file unpinned (SEC-OI-628-3).
5. `RefreshSeconds = 0` loop and non-atomic index swap (SEC-OI-501-1/3).

## New owner questions

| Id | Question | Recommended default |
|---|---|---|
| Q-SEC6-1 | #625: callers with any hidden column get table-level fields only (no column fields) in the score? | Yes; the alternative leaks rank evidence |
| Q-SEC6-2 | #626: query caps (256 chars, 8 tokens, 128 expansions) as constants? | Yes, constants, no option |
| Q-SEC6-3 | #628b: model and tokenizer pinned by SHA-256 in configuration, mounted read-only, never downloaded? | Yes |
