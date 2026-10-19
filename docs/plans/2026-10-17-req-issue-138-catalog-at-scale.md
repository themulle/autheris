# REQ-CAT-138: Catalog at Scale - Search Quality, One Visibility Rule, Index Freshness and Cheap Refresh

**Document ID:** `REQ-CAT-138`
**Date:** 2026-10-17
**Status:** PRD READY (Phase 1 complete); next: Phase 2 plan per increment (INC-138-1 first), then Phase 3
**Author:** Enterprise Product Manager (Phase 1)
**Source:** GitHub issue #138 (owner, v1.1.14); related: #146 (multilingual texts), #153 (procedures in search), #111 (visibility tiers), scale-5000 plans (H-5, WP-PF-10, WP-PF-11)
**Branch:** `docs/issue-intake-2026-10-17` (docs only)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Pointer (intake 2026-10-17B):** [`REQ-BUG-157`](2026-10-17-req-issue-157-columnless-table-graphql-startup.md) changes `CatalogSchemaModel.BuildAsync` (omits tables without queryable columns, `OmittedTables`); it is merged before INC-4.
**Binding project constraints:** fail closed; no production deployment yet and **no upgrade migrations** (the `0001` baseline DDL of the three engines plus the three shape snapshots is edited in place; the caller supplies the newest schema); English only; no row values in logs; the feature-doc phase is not part of delivery.

---

## 1. Verdict

**Sensible with changes, and it must be split into five increments.** The use case is real (thousands of raw tables, agents find data through MCP). About half of the "Permission checks" section is stale or wrong for current `main` (see 2.1), and two requests are replaced by safer alternatives: the multilingual ONNX model is **not** shipped by default (D-2), and the GraphQL schema is **not** made incremental (D-3).

| Item | Result |
|---|---|
| Size | XL as one item; INC-138-1 S, INC-138-2 M, INC-138-3 M, INC-138-4 M, INC-138-5 L |
| Wave | INC-1 in wave 2 (right after step I-7 of #111); INC-2..4 in wave 3; INC-5 in wave 4 |
| Security class | INC-1 is a consistency/hardening fix (a name-disclosure gap in the opposite direction from the issue); INC-2..5 add caches and an optional external model path that need Phase 3 review |

## 2. Verified Current State

### 2.1 Claim verification (code at `main` `b5c1b5dd`)

| # | Claim in #138 | Result | Evidence |
|---|---|---|---|
| C1 | `EmbeddingProvider = "LocalOnnx"` and `ModelName = "all-MiniLM-L6-v2"` are named, only a hash projection exists | **Confirmed, and worse** | `Autheris.Domain/Options/CatalogSearchOptions.cs:15,18`. Nothing reads `EmbeddingProvider` or `ModelName` (grep over `src`: only the declarations). The only generator is `LocalDeterministicEmbeddingGenerator` (`Autheris.Infrastructure/AI/LocalDeterministicEmbeddingGenerator.cs:15-45`, token hash into 384 dimensions, id `Deterministic-Local-Projection-384`), registered unconditionally by `GatewayCatalogServiceExtensions.cs:21`. The option value is therefore decorative and misleading |
| C2 | Index is built once at startup, never rebuilt | **Confirmed** | `CatalogSearchWarmupService.cs:34-45` runs once; the only caller of `RebuildIndexAsync` (`CatalogSearchEngine.cs:43`) is that service. `IsIndexReady` is `_currentSnapshot != null` (`:29`), so the substring fallback (`CatalogDiscoveryService.cs:216-218`) is never used after the first build. A table registered later is missing; a removed or deactivated table stays |
| C3 | `list_datasets` is substring match, sorted by name, 200 per page | **Confirmed** | `McpDatasetCatalog.cs:28` (`MaxListedDatasets = 200`, default page 50 at `:158`), `:155` ordinal sort, `:809-814` substring over id, description, column names and column descriptions |
| C4 | Candidate pool `Math.Clamp(limit*5, 20, 100)` and visibility filter afterwards | **Confirmed** | `CatalogSearchEngine.cs:152` (pool), `:199` (`isVisiblePredicate` applied after RRF fusion). A caller who sees 50 of 5,000 tables gets no hit when the top 100 are all hidden |
| C5 | Search and listing apply different visibility rules; `search_catalog` is ReBAC only; with ReBAC off `ListDatasetsAsync` returns every table | **Wrong as written (stale after B-1)** | Both paths now start from the same core `CatalogVisibility.VisibleTablesAsync` (consent, tenancy, admin classes): MCP list via `McpCatalogVisibility.cs:36-71`, REST/search via `CatalogDiscoveryService.cs:296-326` (`ResolvePermittedAsync`, `VisibleAsync` `:331-352`). ReBAC is an **additional intersection** in discovery only (`:312-324`). With ReBAC off, discovery returns the consent-visible set, not every table |
| C6 | Names and descriptions of hidden tables can show up in search | **Wrong (not reproducible)** | `SearchCatalogDetailedAsync` builds `permittedById` and passes it as predicate (`CatalogDiscoveryService.cs:202-211`); `ShapeHit` (`:401-433`) drops every hit not in the permitted set and re-checks lexical relevance against the caller's visible columns for restricted tables. Hit objects are rebuilt from the caller's filtered metadata. No hidden table name, count or score is returned (`total_hits` is the filtered count, `McpToolExecutionHandler.cs:160-165`) |
| C6b | (new finding, opposite direction) | **New** | `list_datasets`/`describe_dataset` do **not** apply ReBAC at all (no ReBAC reference under `Autheris.Application/Mcp`, only a doc comment at `McpDatasetCatalog.cs:24`). A table that ReBAC denies for the caller is hidden in `search_catalog` and REST discovery but listed and describable through MCP. Name, description, columns and GraphQL field names of a ReBAC-denied table are visible to the caller. Data access still fails; this is a metadata disclosure |
| C7 | `ListDatasetsAsync` runs one ReBAC check per table in parallel and every search pays it | **Confirmed** | `CatalogDiscoveryService.cs:318-324` (`visible.Select(...IsRebacAllowedAsync...)`, `Task.WhenAll`, unbounded fan-out); `SearchCatalogDetailedAsync` calls `ResolvePermittedAsync` first (`:202`). Also `GetAllTablesAsync` per call (`:305`, `McpDatasetCatalog.cs:802`), which is scale finding H-5 |
| C8 | `CatalogGraphQlTypeModule` is registered without `ICatalogVersionProvider` | **Confirmed, and the provider is dead code** | `GatewayServiceCollectionExtensions.cs:538-542` passes no provider; the parameter is optional (`CatalogGraphQlTypeModule.cs:39-45`). `CatalogVersionProvider` is registered nowhere (grep). Its version loads all tables (`CatalogVersionProvider.cs:21-28`) and hashes names, types and counts only: **descriptions, tags and relations do not change it**, so wiring it in would miss most metadata edits, and its `updated_at` source is a column meta key that is normally absent (`:60-72`) |
| C9 | `catalog:changed` has a subscriber but no publisher | **Confirmed** | Subscriber `TenantCatalogLookup.cs:49-56`; constant `ICatalogVersionProvider.cs:12`; no `PublishAsync` with that channel anywhere in `src` |
| C10 | Full model build every refresh interval, metadata TTL 30 s so always expired | **Confirmed** | `CatalogGraphQlTypeModule.cs:57-103` (fallback path builds `CatalogSchemaModel.BuildAsync` to compare a fingerprint), timer `:108-118`, default 60 s (`GraphQlOptions.cs:35`). Metadata cache TTL 30 s (`PostgreSqlGovernanceRepository.cs:49`; same in SQLite and SQL Server). `GetAllTablesAsync` reads the table list with one query and the key rows of uncached tables chunked (`PostgreSqlGovernanceRepository.Catalog.cs:164-190`, the M4 comment), so it is not "queries per table" for keys, but columns and owners still load per uncached table |
| C11 | Relations: one query per table | **Confirmed** | `CatalogSchemaModel.cs:225-229` loops `GetRelationsForTableAsync` per table (`ITableRelationRepository` has no bulk method, `IGovernanceRepository.cs:250-254`). The comment "O(N)" refers to repository round trips avoided versus the older N^2 form, but it is N round trips |
| C12 | `LOWER(...)` on name columns "likely prevents index use" | **Partly wrong** | The baseline already creates a matching expression index on PostgreSQL and SQLite (`PostgreSqlBaselineSchema.cs:247-248`, `SqliteBaselineSchema.cs:250`) and persisted lower-case columns with an index on SQL Server (`SqlServerBaselineSchema.cs:288-289`). The real gap is that `TABLE_RELATIONS` has **no index on `parent_table_id` or `child_table_id`** (`PostgreSqlBaselineSchema.cs:250-258`; no `ON TABLE_RELATIONS` index in any engine), so each per-table lookup scans the relation table |
| C13 | Reverse relations found by scanning all tables per table | **Confirmed** | `CatalogSchemaModel.cs:291-338` (`foreach (var other in tableBuilders)` inside the per-table loop) |
| C14 | Any change rebuilds the full GraphQL schema, tens of thousands of types | **Confirmed in principle, not measured** | `TypesChanged` (`CatalogGraphQlTypeModule.cs:99`) causes a full `CreateTypesAsync`. Types per table are table, filter, order-by plus relation fields (`CatalogSchemaModel.cs`). No measurement exists |
| C15 | Index text lacks tags, owner domain, long description, display name | **Confirmed** | Index text is domain, schema, table, short description, and per column name, type and description (`CatalogSearchEngine.cs:69-73`); `Table.LongDescription`, `Table.DisplayName`, `Table.Tags` and owners are not indexed |
| C16 | `search_catalog` should return short description and matching columns | **Partly done** | Hits already carry `description` and `matched_columns` (`McpToolExecutionHandler.cs:167-176`); the columns are matched on column **names** only (`CatalogSearchEngine.cs:219-223`, `CatalogDiscoveryService.cs:430-433`), never on column descriptions, and the `displayName` of a hit is `domain.table` (`CatalogSearchEngine.cs:226`) |
| C17 | Join paths in search results | **New finding** | `RelatedJoinPaths` come from a naming heuristic (`*_id` becomes `<name>s`, `CatalogSearchEngine.cs:84-100`) or column meta, not from `TABLE_RELATIONS`; the MCP output prints a single-column join condition (`McpToolExecutionHandler.cs:171-175`). A guessed join advertised to an agent can be wrong. See also `REQ-REL-151` |

### 2.2 Overlap with existing plans

| Plan | Overlap | Handling |
|---|---|---|
| #111 (`plan-issue-111`) WP-0 (merged as B-1) | The visibility unification of the issue was **already delivered** for REST/search (`CallerScopeResolver`, `CatalogVisibility` as the single core, search hygiene `W0-C1`) | INC-138-1 only closes the remaining ReBAC gap in MCP list/describe |
| #111 WP-1..6 (step I-7) | Persisted `Table.Visibility` tiers `Hidden/Summary/Full` and tags change `CatalogVisibility.cs` and `McpDatasetCatalog.cs`; search must respect `Summary` (no column oracle, G-7) | INC-138-1 and INC-138-3 start after I-7; the visible-set cache key must include the tier/tag epoch |
| Scale plan H-5 / `WP-PF-10` `CatalogSnapshot` (versioned by `ICatalogVersionProvider`) | Same problem as INC-138-2/4: full catalog reads on request paths and timers | **One snapshot, not two.** INC-138-2 delivers the version counter and the immutable snapshot; `WP-PF-10` is then reduced to its audit-monitor half (open question Q-138-1) |
| `WP-PF-11` indexed ReBAC store | Bulk ReBAC evaluation depends on a store that can answer many objects cheaply | INC-138-3 uses bounded batched checks now and consumes the bulk API when `WP-PF-11` lands |
| `REQ-FED-01` / `PLAN-FED-01`, #124 | No overlap with search; cross-source relation key sets are separate | none |
| BL-06 (OpenTelemetry) | Metrics for search latency, rebuild duration, visible-set cache hits | INC-138-2..4 emit metrics through the BL-06 contracts if merged; otherwise structured logs only |
| #153 (procedures as catalog entries) | Needs search over a second entry kind | INC-138-2 keys the index by `CatalogEntryKey(kind, id)` instead of `TableIdentifier`, so #153 does not require a second index |
| #143 (restricted scopes) | None directly; scope-aware column visibility already feeds `ShapeHit` through `CatalogVisibility` | none |
| #146 (multilingual texts) | Indexing of display names and translations | `REQ-CAT-146` section 8; INC-138-2 provides the document builder |

## 3. Problem Statement

1. Search quality: a keyword index over partial text, a pretend vector half, and a fixed candidate pool that is filtered after ranking.
2. Freshness: the index is a startup snapshot per instance; no signal exists that the catalog changed.
3. Cost: every search and list loads the whole catalog and runs a permission check per table; the periodic GraphQL check rebuilds a full model.
4. Consistency: MCP list/describe and search differ on ReBAC.

## 4. Benchmark and Rejected Anti-Patterns

| Practice (public catalogs) | Use here |
|---|---|
| DataHub, OpenMetadata and Unity Catalog search are keyword-first (OpenSearch/Elasticsearch or equivalent) with field boosts (name and display name above description above column names), prefix/identifier tokenisation and **permission trimming before ranking**; embeddings are an optional add-on, not the default | BM25 with field weights as the baseline (REQ 5.2); embeddings optional (INC-138-5) |
| Search backends keep a monotonically increasing change counter or an outbox/change-event stream; consumers poll the counter and optionally subscribe to events | DB-backed counter plus best-effort `catalog:changed` event (REQ 5.3) |
| Pre-filter: compute the caller's allowed set (or push the filter into the index query), then rank only within it | Bitset visible set, no candidate cap (REQ 5.4) |

Rejected anti-patterns:

1. **Shipping a 100-470 MB ONNX model in the default gateway image.** Image size, cold start, memory (model plus runtime per instance), CPU cost of embedding thousands of documents at startup, native runtime per RID, model licence and supply-chain surface, all for installations that never asked for it. Alternative: opt-in, pluggable provider, model mounted by the operator with a pinned checksum, honest keyword-only default reported in health (REQ 5.5).
2. **A decorative provider setting** (`LocalOnnx` default, never read) and a hash projection presented as "vector" search. Alternative: the effective provider is the only truth; the hash generator is no longer fused into hybrid ranking by default.
3. **Hashing all metadata to detect a change** (`CatalogVersionProvider`). It costs a full load and misses description edits. Alternative: a counter bumped in the writing transaction.
4. **Filtering after a bounded candidate pool.** Alternative: filter before ranking.
5. **Unbounded per-table fan-out** of authorization checks (`Task.WhenAll` over thousands of tables). Alternative: bounded batches plus a generation-validated cached set.
6. **Making the GraphQL schema incremental.** The schema is generated from catalog types; Hot Chocolate swaps whole schemas; an incremental scheme would add a second schema model and a drift risk to a security-relevant surface. Alternative: rebuild only on structural change, measure, and stop when the gate is met (REQ 5.6).
7. **Caching per-caller visible sets without generations.** A stale entry would serve a revoked table. Alternative: the cache key carries every epoch that can change visibility and is validated on each use.

## 5. Requirements

### 5.1 INC-138-1: One visibility function for list, describe and search (S)

- **R-138-1.1** A single Application service `ICatalogVisibilityService` (name for the architect to confirm) returns the caller's visible tables: consent visibility (`CatalogVisibility`) intersected with ReBAC when ReBAC is enabled. `McpDatasetCatalog` (list, describe, relations, `sample_rows` guard), `CatalogDiscoveryService`, MCP resources and `ApiDispatcherService` use it. `McpCatalogVisibility` keeps only the MCP specifics (anonymous caller, auth bypass flag) as a thin wrapper.
- **R-138-1.2** ReBAC can only shrink the set. A ReBAC error for a table fails closed for that table (hidden), logged without caller values.
- **R-138-1.3** `describe_dataset` for a table the caller may not see answers exactly like an unknown name (existing behaviour of `FindVisibleAsync`, kept).
- **R-138-1.4** Relations offered by `describe_dataset` (`McpDatasetCatalog.cs:665-672`) use the same set, so a ReBAC-denied target is not advertised.
- **AC-138-1** Test with two persons, ReBAC enabled and disabled: `list_datasets`, `describe_dataset`, `search_catalog`, REST `GET /api/v1/catalog/datasets` and `/search` return the same visible set; a ReBAC-denied table is absent everywhere (name, count, score, relations, pagination totals).
- **AC-138-2** Architecture test: no catalog reader outside the service calls `CatalogVisibility.VisibleTablesAsync` directly (allow-list for the `DataAccess` consumers listed in `plan-issue-111` section 1.2).

### 5.2 Search ranking and result content (part of INC-138-2 and INC-138-3)

- **R-138-2.1** Index document per catalog entry with weighted fields: technical name and display name (highest), tags and owner domain, short description, long description, column names, column descriptions (lowest). Weights are options with documented defaults (BM25F style: weighted term frequencies per field).
- **R-138-2.2** Tokenisation splits snake_case, camelCase and digits, folds case and diacritics; the existing `SmartSchemaTokenizer` abbreviation expansion stays. An exact technical-name match ranks first.
- **R-138-2.3** `SecretScrubber.Redact` stays on every indexed free text (as today, `CatalogSearchEngine.cs:66-71`), including long description and translated texts.
- **R-138-2.4** Hits return the display name (language-aware after `REQ-CAT-146`), short description, the matching columns **by name and by description** (only columns the caller may see), and join paths read from `TABLE_RELATIONS`, not from the naming heuristic (R-138-2.5).
- **R-138-2.5** The `*_id` heuristic for join paths is removed from search output; join paths come from stored relations (`REQ-REL-151`), including all key columns, restricted to relations whose both ends and all key columns the caller may see.
- **R-138-2.6** Result hygiene of `ShapeHit` stays: a hit that exists only because of text from a column the caller cannot see is dropped (`W0-C1`). This applies to lexical and semantic hits and to translated column texts.
- **R-138-2.7** `total_hits` is the number of returned hits only; no total over the index or the unfiltered match count is exposed.

### 5.3 INC-138-2: Catalog version, change events, fresh index on every instance (M)

- **R-138-3.1** New baseline table `CATALOG_STATE (id = 1, structure_version BIGINT, content_version BIGINT, updated_at)` in the `0001` baseline of all three engines and the three shape snapshots. `structure_version` is incremented in the **same transaction** as any change of tables, columns, key definitions, relations, activation; `content_version` for descriptions, display names, translations, tags, owners, visibility tier. A write that changes only content never bumps `structure_version`.
- **R-138-3.2** `ICatalogVersionProvider` is reimplemented on the counter (one `SELECT`, two integers) and registered in DI. The hash-over-all-metadata implementation is deleted.
- **R-138-3.3** After commit, the writer publishes `catalog:changed` on the event bus with the new versions (best effort). Subscribers must not rely on it: each instance also polls the counter every `CatalogSearch:RefreshSeconds` (default 15, shared with the GraphQL check interval where sensible). An idle catalog costs one query per interval and instance.
- **R-138-3.4** Search index refresh: on a version change the instance rebuilds the snapshot in the background (debounced, at most one build at a time, old snapshot served until the new one is swapped by `Volatile` write as today), then updates `LastIndexedAt` and the indexed version. Incremental per-table update is a later optimisation, justified only by a measured rebuild time above the interval at 5,000 tables.
- **R-138-3.5** The index is built from the shared immutable `CatalogSnapshot` (one `GetAllTablesAsync` per version, not per consumer), keyed by version (consolidates scale finding H-5 for search, GraphQL and `TenantCatalogLookup`).
- **R-138-3.6** Index documents are keyed by `CatalogEntryKey(kind, id)`; only `kind = table` exists in this PRD (#153 adds `procedure`).
- **R-138-3.7** A removed or deactivated table disappears at the next refresh; a table that fails to load is skipped and counted, never fails the whole build.
- **AC-138-3** A table registered while the gateway runs is found within the refresh interval on every instance (two-instance test); a deactivated table disappears; a description-only edit does not trigger a GraphQL schema rebuild but does trigger a search index refresh.
- **AC-138-4** Idle catalog: at most 3 governance queries per refresh interval and instance (version read, nothing else).

### 5.4 INC-138-3: Bulk visibility and pre-filter ranking (M)

- **R-138-4.1** Search takes a visible set before ranking. BM25 and vector scoring run over the documents of that set only; the candidate pool cap (`CatalogSearchEngine.cs:152`) is removed. Cost is proportional to matching documents (5,000 documents is cheap in memory; the gate is measured).
- **R-138-4.2** The visible set is a bitset over the snapshot's table ordinals (5,000 tables is under 1 KB per caller; 5,000 cached callers about 3 MB).
- **R-138-4.3** The set is cached per caller key (tenant, subject SIDs, roles, token scope set, caller class) and **validated on every use** against all epochs that can change visibility: catalog `structure_version` and `content_version`, the consent/policy epoch (`IPolicyEpochRepository`), the #111 tier/tag epoch, and the ReBAC tenant generation read through the same cached generation read as the decision cache (`ZanzibarRebacEvaluator.cs:135-165`, default `GenerationCacheMilliseconds = 1000`). A mismatch or a failed generation read is a cache miss (fail closed). The cache is bounded (entries and bytes) with LRU eviction and a hard TTL.
- **R-138-4.4** On a miss, ReBAC checks run in bounded parallel batches (configurable, default 32) instead of `Task.WhenAll` over all tables, and use the decision cache. When `WP-PF-11` provides a bulk list-objects style call, the service uses it.
- **R-138-4.5** `list_datasets` and `search_catalog` cost no per-table permission check on a cache hit. A cache entry is never shared between callers with different keys.
- **R-138-4.6** Revocation: after a consent is revoked or a ReBAC tuple removed, the table disappears from search and list on every instance within the ReBAC generation cache window (default 1 s) plus the policy epoch polling interval; the PRD gate states the measured bound.
- **AC-138-5** A caller who may see 50 of 5,000 tables finds a matching visible table that ranks 3,000th among all tables.
- **AC-138-6** Two-instance revocation test (consent revoke, ReBAC tuple removal) within the stated bound.
- **AC-138-7** Cache key test: two callers that differ only in scope set, tenant or role never share a set.

### 5.5 INC-138-5: Optional embeddings, honest by default (L, wave 4)

- **R-138-5.1** `CatalogSearch:EmbeddingProvider` is an enum with `None` (default), `LocalOnnx`, `Ollama`; a validator rejects unknown values at start. `None` means keyword-only BM25.
- **R-138-5.2** `LocalOnnx` requires `ModelPath`, `TokenizerPath` and `ModelSha256`; the files are mounted by the operator and never downloaded at runtime (no egress); the hash is verified before load; size and dimension are bounded. Suggested multilingual candidates: `paraphrase-multilingual-MiniLM-L12-v2` or `multilingual-e5-small` (roughly 120 MB quantised to 470 MB full precision, architect to pick and check licence and the required query/passage prefixes). The ONNX runtime package and the model are **not** part of the default image; an optional image variant or a documented mount is delivered instead.
- **R-138-5.3** `Ollama` sends catalog texts (descriptions, display names, translations) to a configured endpoint. It uses the secure outbound handlers with an egress allow-list integration (as for other outbound integrations, `AddSecureOutboundHandlers`), HTTPS or an explicit insecure flag, and a startup log line naming the destination. This is metadata leaving the process and needs the owner's explicit configuration.
- **R-138-5.4** If the configured provider cannot load, the gateway starts with keyword-only search, reports **configured** and **effective** provider, `Degraded` search health and a startup error log. Search stays available (keyword results are safe); it never silently pretends to be semantic.
- **R-138-5.5** Vectors are computed in the background after the BM25 index is live; health reports `vectorIndexReady`, model id, dimensions, indexed count and `LastIndexedAt`. Embeddings are cached by the SHA-256 of the document text for the lifetime of the process to keep rebuilds cheap.
- **R-138-5.6** `mode = semantic` or `hybrid` without an effective provider answers with keyword results and a field `effective_mode: "keyword"`; it never errors and never fakes a score.
- **R-138-5.7** The deterministic hash generator is removed from default fusion; it may remain as a test double.
- **R-138-5.8** Health: `GET /health` (search component) and the startup log carry provider, model id, effective mode, document count, last indexed time, index version.
- **AC-138-8** With a multilingual model configured, a German question finds a table described only in English and the reverse (integration test with a small test model or recorded vectors; the model-dependent test is skipped when no model is mounted).
- **AC-138-9** With `None`, health says keyword-only; start-up with `LocalOnnx` and a wrong hash fails the load, falls back to keyword, and health is `Degraded`.

### 5.6 INC-138-4: Cheap refresh and measured GraphQL rebuild (M)

- **R-138-6.1** `ITableRelationRepository` gains `GetAllRelationsAsync` (one query for all active tables, all three engines). `CatalogSchemaModel.BuildAsync` uses it; the per-table loop (`CatalogSchemaModel.cs:225-229`) is removed.
- **R-138-6.2** Baseline DDL: `CREATE INDEX` on `TABLE_RELATIONS (parent_table_id)` and `(child_table_id)` (all engines, shape snapshots updated).
- **R-138-6.3** Reverse relations are found through a dictionary keyed by child table built once per build, replacing the scan at `CatalogSchemaModel.cs:291-338`.
- **R-138-6.4** The `LOWER(...)` request of the issue is dropped: the baseline already carries the matching indexes (C12). A plan step verifies with `EXPLAIN` on each engine that the lookups use them.
- **R-138-6.5** `CatalogGraphQlTypeModule` receives `ICatalogVersionProvider` and compares **`structure_version` only**. Content-only changes do not rebuild the schema. If the schema embeds descriptions, either the descriptions are excluded from the schema (preferred, the MCP tools carry them) or `content_version` also triggers; the architect decides with a measurement.
- **R-138-6.6** A rebuild is logged with duration, type count and table count, runs off the request path and never blocks running requests (the Hot Chocolate schema swap behaviour is verified by a test that keeps requests running during a rebuild).
- **R-138-6.7** No incremental or lazy per-table GraphQL typing in this PRD. If the 5,000-table gate (AC-138-10) is met with R-138-6.1..6.6, the work stops there. If not, the follow-up options are written as a separate PRD (cap on tables exposed to GraphQL by an opt-in tier, or a split schema), not improvised.
- **AC-138-10** Load test with 5,000 tables (about 20 columns each, a few thousand relations) on one instance: a rebuild after one structural change finishes within 10 s with requests still served; `search_catalog` p95 under 300 ms for a caller who sees all tables and for one who sees 50; `list_datasets` and `search_catalog` make zero per-table permission checks on a visible-set cache hit. The existing `ScaleCatalogBenchmark` synthetic catalog is the data source.

## 6. Security Requirements

1. **SEC-138-1 Fail closed.** A failed visibility, ReBAC, generation or epoch read yields an empty or smaller result, never a larger one.
2. **SEC-138-2 No oracle.** Search never reveals the existence, name, count, score position or pagination of a table the caller cannot see; an unknown name and a hidden name give the same answer in `describe_dataset`.
3. **SEC-138-3 Column hygiene.** Matching text from columns the caller cannot see (or from tiers `Summary`) must not influence which tables are returned or their order; for semantic hits the existing lexical re-check on visible text is kept; a restricted table's semantic-only hit is dropped.
4. **SEC-138-4 Cache correctness.** Every cache entry is bound to the full epoch tuple of R-138-4.3; no entry is served across callers, tenants or scope sets; bounded size; no persistence outside the process.
5. **SEC-138-5 Outbound model path.** Ollama is off by default; destination is explicit; egress allow-list applies; texts sent are scrubbed with `SecretScrubber`; no catalog text is logged. The ONNX path performs no network access and verifies the model hash.
6. **SEC-138-6 Counters and events are not secrets but must not carry data.** `catalog:changed` carries versions only, no table names.
7. **SEC-138-7 Scrubbing.** All indexed free text, including long descriptions and translations, passes `SecretScrubber.Redact` before indexing (extends today's behaviour).
8. **SEC-138-8 Observability.** Metrics and logs for rebuild, cache hits and search latency contain no query text and no caller identifiers beyond the existing audit conventions.

## 7. Non-Goals

1. An external search engine (OpenSearch, Elasticsearch) as a dependency.
2. Incremental GraphQL schema types, lazy per-table types or splitting the schema (see R-138-6.7).
3. Language-specific stemming, German compound splitting or synonyms management (covered by embeddings or a later PRD; see `REQ-CAT-146` Q-146-4).
4. Result personalisation, popularity ranking or usage analytics.
5. A new client-side catalog UI.
6. Persisting the search index across restarts.

## 8. Deviations from the Issue

| # | Issue says | This PRD |
|---|---|---|
| D-1 | Search and listing use different visibility rules; hidden tables leak through search | Not reproducible on `main` after B-1 (C5, C6). Replaced by INC-138-1: close the **reverse** gap (ReBAC missing in MCP list/describe) |
| D-2 | Real multilingual embedding model "running offline in the gateway image" | Opt-in provider, model mounted by the operator, not in the default image; honest keyword-only default (anti-pattern 1) |
| D-3 | Faster GraphQL rebuild, with the remark that every change rebuilds the full schema | Structural counter, bulk relation load, measurement against the gate; no incremental schema (anti-pattern 6) |
| D-4 | Normalised name columns instead of `LOWER(...)` | Dropped: the matching indexes exist (C12); add the missing relation indexes instead |
| D-5 | "A counter or the latest update timestamp" as version | Counter only: a timestamp cannot see deletions |
| D-6 | Wire `ICatalogVersionProvider` into the GraphQL module | The provider is replaced by a counter implementation; the existing one would miss description edits (C8) |
| D-7 | Cache of visible tables "invalidated on consent and policy changes" | Cache validated on every use against all epochs, bitset form, bounded |
| D-8 | `search_catalog` returns short description and matching columns | Already partly present (C16); extended to column descriptions and stored relations with all key columns |
| D-9 | Embedding provider in health | Kept and extended: configured versus effective, vector readiness |

## 9. Open Owner Questions

| # | Question | Recommended default |
|---|---|---|
| Q-138-1 | Does INC-138-2 absorb the `CatalogSnapshot` part of scale item `WP-PF-10` (H-5)? | Yes: one version counter and one snapshot for search, GraphQL and tenant lookup; `WP-PF-10` keeps the audit-monitor half |
| Q-138-2 | Default embedding provider | `None` (keyword-only, BM25 with field weights); no model in the image; ONNX opt-in in wave 4. If cross-language search is wanted without a model, curated translations (`REQ-CAT-146`) already give German/English matches for described tables |
| Q-138-3 | Keep the hash vector in default hybrid fusion? | No: remove from fusion; keep the class as a test double; `mode: hybrid` degrades to keyword with `effective_mode` |
| Q-138-4 | Should ReBAC-denied tables disappear from MCP list/describe? | Yes (intersection, fail closed); matches the REST documentation ("ReBAC filtered", `ApiDispatcherService.cs:252`) |
| Q-138-5 | Refresh interval default | 15 s for search; GraphQL keeps its own `CatalogSchemaRefreshSeconds` (60 s) but compares the cheap counter |
| Q-138-6 | Index scope: one global index filtered per caller, or one per tenant | One global index with per-caller filtering (current design); per-tenant indexes only if memory requires it |
| Q-138-7 | If the 5,000-table GraphQL gate is not met after INC-138-4, what then? | Separate PRD; owner decides between opt-in GraphQL exposure per table and a split schema |
| Q-138-8 | Is a documented max catalog size for this design needed (for example 50,000 entries)? | Yes: state 5,000 as the tested size and 50,000 as the design ceiling; above it per-tenant indexes become mandatory |

## 10. Increments, Dependencies and Proposed Waves

| Increment | Content | Size | Wave | Depends on |
|---|---|:---:|---|---|
| INC-138-1 | One visibility function, ReBAC in MCP list/describe/relations (5.1) | S | 2 (immediately after I-7) | I-7 (#111 WP-1..6), shares `McpDatasetCatalog.cs`, `CatalogVisibility.cs` |
| INC-138-2 | `CATALOG_STATE` counter, `catalog:changed` publisher, snapshot, fresh index, field weights, entry key (5.2, 5.3) | M | 3 | INC-138-1; baseline DDL serialisation (G-SHARED) |
| INC-138-3 | Bitset visible set, epoch-validated cache, bounded ReBAC, pre-filter ranking (5.4) | M | 3 | INC-138-2; `WP-PF-11` optional |
| INC-138-4 | Bulk relations, relation indexes, structural GraphQL version, measured rebuild (5.6) | M | 3 | INC-138-2; coordinate with I-18/I-19 (`CatalogSchemaModel.cs`, `TABLE_RELATIONS` DDL) and `REQ-REL-151` |
| INC-138-5 | Optional embeddings, health honesty (5.5) | L | 4 | INC-138-2, INC-138-3 |

Order inside wave 3: INC-138-2, INC-138-3, INC-138-4. All branches stack as the tracker prescribes; none starts before the G-PLAN rule of the tracker is satisfied for it (Phase 2 plan and Phase 3 review first).

## 11. Test Strategy Hints (for Phase 2)

1. TDD per increment; the two-person visibility matrix (ReBAC on/off) is the first test of INC-138-1.
2. Two-instance integration tests for freshness and revocation use the existing multi-replica test harness with an in-memory event bus plus the polling fallback (disable the event to prove polling alone works).
3. A scale test project reuses `ScaleCatalogBenchmark`; gates in AC-138-4, -5, -10 are measured, not assumed.
4. Architecture tests: no direct `CatalogVisibility` use outside the service (AC-138-2); no `GetAllTablesAsync` in search request paths.
5. Shape snapshot tests for `CATALOG_STATE` and the new indexes on three engines.
