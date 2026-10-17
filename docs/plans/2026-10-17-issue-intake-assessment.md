# Issue Intake Assessment: #138, #143, #146, #150, #151, #152, #153, #154

**Document ID:** `INTAKE-2026-10-17`
**Date:** 2026-10-17
**Phase:** 1 (product qualification), consolidated by the Delivery Project Manager
**Code base verified:** `main` at `b5c1b5dd` (v1.1.16 plus waves 2/3 part 1, PR #149)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md) (section 0, "Issue intake 2026-10-17")
**Child PRDs:**
[`REQ-CAT-138`](2026-10-17-req-issue-138-catalog-at-scale.md),
[`REQ-GOV-143`](2026-10-17-req-issue-143-restricted-scope-sensitive-columns.md),
[`REQ-CAT-146`](2026-10-17-req-issue-146-multilingual-catalog-texts.md),
[`REQ-OPS-150`](2026-10-17-req-issue-150-startup-warmup-readiness.md),
[`REQ-REL-151`](2026-10-17-req-issue-151-composite-key-relations.md),
[`REQ-PROC-152..154`](2026-10-17-req-issues-152-154-governed-procedures.md)

---

## 1. Summary

No issue is rejected and none is a duplicate. Every issue describes a real gap, but seven of eight contain at least one
claim that is wrong or out of date on current `main`, or propose a mechanism that should change. All eight are
**sensible with changes**. Each has a Phase 1 PRD that describes the recommended solution and names where it deviates
from the issue text. The stored-procedure issues share one PRD because they all change one execution path
(`GovernedProcedureExecutionService.ExecuteAsync`).

| Issue | Verdict | Recommended solution (one line) | Size | Proposed wave |
|---|---|---|:---:|---|
| #138 catalog at scale | Sensible with changes, split into 5 increments | One visibility service (ReBAC also in MCP list/describe), catalog version counter plus shared snapshot, ranking inside the caller's visible set, bulk relations, embeddings opt-in only | XL (S+M+M+M+L) | INC-1 wave 1 (security), INC-2..4 wave 3, INC-5 wave 4 |
| #143 restricted scopes | Sensible with changes | `Governance:RestrictedScopes` list; the decision records scope-specific `Clear` rules, and `GetEffectiveColumnAccess` masks sensitive columns without one; any restricted scope in the token wins | M | 2 |
| #146 multilingual texts | Sensible with changes | Step A: `displayName` in MCP tools; step B: `texts_json` map plus allow-listed language negotiation; step C: search over all languages | M (S+M+S) | A wave 2, B and C wave 3 |
| #150 cold first request | Sensible with changes (cause in the issue is probably wrong) | Measure first; bounded background warm-up without data reads, readiness `503 Warming` until done or 30 s; ReadyToRun only if measurement justifies it | M | 2 |
| #151 composite keys | Sensible with changes (correctness bug) | Importer keeps all key pairs or skips with a warning; `TABLE_RELATION_KEYS` child table in the baseline DDL; idempotent relation upsert | S-M | 1 (bug step, before I-18 and #138 INC-4) |
| #152 procedure column rules | Sensible with changes | `cleared_columns` lifts only the sensitivity mask, never an explicit `Mask`/`Deny`; `cleared_for_scopes` per computed column instead of virtual consent columns; `maskedColumns` plus `removedColumnCount` | M | precedence fix wave 1, rest wave 3 |
| #153 procedures in catalog | Sensible with changes | One `ProcedureVisibility` predicate (roles plus consent on every checked table); identical 404 for unknown and denied; catalog kind `procedure`; MCP call tool deferred | M-L | security slice wave 1, catalog entries wave 3, call tool wave 4 |
| #154 limits, verbs, errors | Sensible with changes | Generic 422 unless a declared error map; consent before validation; concurrency cap; `limit` after row scoping with a bounded `MaxScanRows`; read-only `POST` | M | security slice wave 1, rest wave 2 |

## 2. Cross-cutting findings

1. **Security bugs found during verification** (pulled into wave 1, owner rule "security and bugs first"):
   - Procedures: a denied caller can tell an existing procedure from an unknown one (403 vs 404,
     `GovernedProcedureExecutionService.cs:110-113` vs `:161-169`; 503 for a non-active procedure before consent at
     `:135-138`); parameter validation runs before the consent check (`:141` before `:158`); procedure `THROW` text is
     returned as 422 before any row or column governance (`MssqlProcedureInvoker.cs:322-352`,
     `ProcedureEndpointRoutes.cs:357-359`) and can carry row data; `cleared_columns` lifts an explicit scope-specific
     `Mask` to `Clear` (`GovernedProcedureExecutionService.cs:603-609`); no concurrency cap on procedure calls.
   - Catalog: the issue's claim that `search_catalog` leaks hidden tables is **wrong** on current `main`
     (`CatalogDiscoveryService.cs:202-211` filters hits by the permitted set; B-1 / #111 WP-0 unified the core rule).
     The real gap is the reverse: MCP `list_datasets` and `describe_dataset` apply no ReBAC at all, so with
     `Rebac.Enabled = true` a ReBAC-denied table's name, columns and relations are visible there.
2. **One precedence rule for column access.** #143 and #152 both touch the question "when may a scope receive a
   sensitive or scope-masked column in clear". The answer must come from the single resolver
   (`TableAccessDecision.GetEffectiveColumnAccess`, `TableAccessDecision.cs:413-434`), and procedure
   `cleared_columns` must be defined in its terms (a base `Clear` against catalog sensitivity only). #143's outcome is
   binding for procedures.
3. **One catalog visibility predicate and one index.** #138 INC-1 creates the shared visibility service; #153 adds
   procedures through the same predicate and the `CatalogEntryKey(kind, id)` index key from #138; #146 adds translated
   texts to the same index (step C after #138 INC-2). No second visibility rule and no second index.
4. **Shared baseline DDL.** #138 (`CATALOG_STATE`, relation indexes), #146 (`texts_json`), #151
   (`TABLE_RELATION_KEYS`) all extend the `0001` baseline on three engines plus shape snapshots (no upgrade migrations,
   owner rule 2026-10-15). They are serialised under the tracker's G-SHARED rule: #151 first, then #138 INC-2/INC-4,
   then #146 B.
5. **Files shared with wave 2 step I-7 (#111).** #138 INC-1 and #146 step A edit the MCP catalog and visibility files
   that I-7 changes; they start after I-7 is merged.

## 3. Per-issue assessment

### 3.1 #138 Catalog at scale (search, permissions, refresh)

**Verdict:** sensible with changes; the issue is an epic and is split into five increments.

**Claim check (current `main`):**

| Claim | Result |
|---|---|
| Embedding provider/model configured but only a hash projection exists | Confirmed: `CatalogSearchOptions.cs:15,18` never read; only `LocalDeterministicEmbeddingGenerator` registered (`GatewayCatalogServiceExtensions.cs:21`) |
| Index built once at startup, never rebuilt | Confirmed: the warm-up service is the only `RebuildIndexAsync` caller |
| Candidate pool 20-100, visibility filtered after ranking | Confirmed: `CatalogSearchEngine.cs:152,199` |
| Search leaks hidden tables; ReBAC-off returns every table | **Wrong**: hits are filtered by the permitted (consent-visible) set; the gap is missing ReBAC in MCP list/describe |
| One ReBAC check per table per search, unbounded `Task.WhenAll` | Confirmed: `CatalogDiscoveryService.cs:318-324`, plus `GetAllTablesAsync` per call |
| `catalog:changed` has no publisher | Confirmed (subscriber `TenantCatalogLookup.cs:49-56`) |
| GraphQL module rebuilds the full model every 60 s | Confirmed; `CatalogVersionProvider` is registered nowhere and hashes only names, types and counts |
| Relations loaded per table, reverse relations by scan | Confirmed: `CatalogSchemaModel.cs:225-229`, `:291-338` |
| `LOWER(...)` prevents index use | **Partly wrong**: expression indexes / `_lc` columns exist; the real gap is no index on `TABLE_RELATIONS.parent_table_id` / `child_table_id` |

**Recommended solution (deviations from the issue):**
- INC-1 (S, wave 1): one catalog visibility service used by list, describe, relations and search, including ReBAC.
- INC-2 (M, wave 3): `CATALOG_STATE` version counter bumped in the writing transaction, `catalog:changed` published
  after commit, counter polling as the reliable fallback (every instance), one shared immutable catalog snapshot that
  feeds both the search index and GraphQL change detection. Absorbs the `CatalogSnapshot` part of `WP-PF-10`.
- INC-3 (M, wave 3): rank only within the caller's visible set (bitset validated against catalog, consent,
  ReBAC-generation and tier epochs), no candidate-pool cap, bulk ReBAC evaluation, fielded BM25.
- INC-4 (M, wave 3): relations in one query, relation indexes, rebuild duration logged, measured against the
  5,000-table gate. **Deviation:** no incremental or lazy GraphQL schema now; if the gate is missed, a separate PRD.
- INC-5 (L, wave 4): embeddings as an opt-in provider (`None` default, `LocalOnnx` with operator-mounted model and
  pinned SHA-256, `Ollama` with egress governance), configured vs effective provider in startup log and health.
  **Deviation:** no 100-470 MB model in the default image; the hash vector leaves default fusion (it is a second
  keyword match, i.e. dishonest "hybrid" search).

**Security notes:** fail closed on any visibility read failure; no oracle through counts, scores or paging;
hidden-column text must not influence ranking; caches keyed to the full epoch tuple and invalidated cross-replica like
the ReBAC decision cache; no data in `catalog:changed` messages.
**Dependencies:** I-7 (#111) before INC-1; INC-2 before INC-3/4/5; #151 before INC-4.

### 3.2 #143 Sensitive columns for restricted scopes

**Verdict:** sensible with changes. The gap is real: a base `Clear` rule also clears a sensitive column for the agent
scope (`TableAccessDecision.cs:413-434`; `GetConsentColumnLevel` at `:241-300` lets scope rules only tighten).

**Recommended solution:** keep the issue's global list (`Governance:RestrictedScopes`, normalised like
`JwtOptions.ReadOnlyScopes`, including the `api://.../Scope` form). `ConsentResolutionService.ResolveAccess` records,
per restricted scope held, which columns carry a scope-specific `Clear`; `GetEffectiveColumnAccess` returns `Mask` for
a sensitive column without one. The change can only lower a level. A token holding several scopes is restricted if any
of them is restricted and needs an explicit rule for every restricted scope it holds (restrictive wins). The `Unmasked`
access profile does not override the restriction. Procedure `cleared_columns` follow the same rule (see #152).
**Changes vs the issue:** multi-scope rule and access-profile interaction specified; seam for a later tag-based
sensitivity predicate (`WP-SC-06`). Rejected alternatives: scope metadata in the consent model (three repositories, no
scope registry exists), default-deny for all read-only scopes (would also block `App.Read`).
**Known residual gaps (documented in the PRD):** app-only tokens carry no scope claim; CDC stream and Iceberg
federation resolve access without scopes; `GetScopes` reads only `scp`.
**Size / wave:** M, wave 2. **Dependencies:** none blocking (#116, #127 merged); coordinate with #152 INC-1.
**Security notes:** mandatory Phase 3 review (access-control semantics); fail closed on configuration errors.

### 3.3 #146 Multilingual display names and descriptions

**Verdict:** sensible with changes. Verified: `Table.DisplayName` exists (`TableModels.cs:41`) and GraphQL returns it,
MCP list/describe do not (`McpDatasetCatalog.cs:168-247`); `TableColumn` has no display name; no language handling
exists; search does not index display names, so point 3 depends on #138.
**Recommended solution:** step A (S, wave 2): `displayName` for tables and columns in MCP tools with fallback to the
technical name. Step B (M, wave 3): `texts_json` map on `TABLES` and `TABLE_COLUMNS` (house pattern like `meta_json`;
no extra query), configured allow-list of languages (default `en`, `de`, max 16), negotiation order explicit
`language` argument, then `Accept-Language` (RFC 4647 lookup), then default; response names the language used
(`language`, `Content-Language`, `Vary`). Step C (S, wave 3, after #138 INC-2): one BM25 document per entry with all
languages and diacritic folding.
**Deviations:** JSON map instead of translation tables; GraphQL type and field names are not translated; unsupported
tags fall back silently to the default.
**Security notes:** hidden datasets and columns never leak in any language; length and control-character caps on
texts; texts are untrusted for prompt injection; audit without text content.

### 3.4 #150 Cold first authenticated request

**Verdict:** sensible with changes; the stated cause is probably wrong. The GraphQL executor is already built eagerly
at startup (`McpSchemaDiscoveryService.StartAsync` awaits `GetExecutorAsync`, `McpSchemaDiscoveryService.cs:38-60`).
`/health/ready` checks only drain state and a 5 s cached health report (`HealthEndpoints.cs:52-108`). Tiering switches
did not help, which points at first-use initialisation (likely the lazy OIDC metadata and signing-key fetch on the first
bearer token, MCP serialisation and tool registry) rather than JIT.
**Recommended solution:** WP-A measurement harness with per-stage breakdown; WP-B background `StartupWarmupService`
with non-data, exception-isolated steps (HotChocolate executor warm-up via parse/validate of `__typename` only, OIDC
metadata and key prefetch for configured authorities, MCP serialisation warm-up); readiness `503 Warming` until done or
a bounded timeout (default 30 s); WP-C ReadyToRun only if measurement shows JIT is a material share.
**Anti-patterns rejected:** self-HTTP calls with fake or privileged tokens, governed reads with a synthetic principal,
a public `/warmup` route, an unbounded readiness gate.
**Size / wave:** M, wave 2. **Security notes:** warm-up must not read governed data, resolve consent or write audit
records (enforced by a dependency test); anonymous readiness discloses only Ready/Warming.

### 3.5 #151 Composite-key relations

**Verdict:** sensible with changes; correctness bug (wrong nested results without a signal; no disclosure because row
filters and masks still apply). Confirmed: `DbtArtifactStreamingParser.cs:172-185` takes only the first column and
defaults a missing list to `id`; `DbtRelationshipDefinition` holds one column per side; `TABLE_RELATIONS` and all three
repositories store one key and the `JoinKeyParent` setter truncates (`TableModels.cs:278-288`). The query side is
composite-ready (`TreeSqlCompiler.cs:381-395`).
**Wrong claim:** composite cross-source relations do not need a fail-closed stop; `CrossSourceRelationResolver`
already builds composite predicates. A test locks this behaviour instead.
**Additional findings (in scope):** each dbt sync creates new relation rows (no natural key, `Guid.NewGuid()`);
SQLite `CreateRelationAsync` writes table ids that the importer never sets (to confirm by test); unknown models and
persistence failures produce no warning.
**Recommended solution:** importer keeps all pairs in order or skips with a reason-coded sync warning (count mismatch,
unknown column or model, `expression` form); never shorten or default. Storage: `TABLE_RELATION_KEYS` child table
(ordinal, parent column, child column) in the `0001` baseline on three engines, natural-key unique index and upsert.
**Deviation:** baseline DDL change instead of a migration (owner rule: no upgrade migrations, no production yet).
Discovery output (key pairs in `describe_dataset` and search) is a later step and shows pairs only when every key
column is visible to the caller.
**Size / wave:** S-M, wave 1 as a bug step, before I-18 (`REQ-FED-01`) and before #138 INC-4 (same DDL and
repositories).

### 3.6 #152 Procedures: scope-specific column rules, masked and removed columns

**Verdict:** sensible with changes. Confirmed: sourceless columns are `Clear` for every scope when listed in
`cleared_columns` (`GovernedProcedureExecutionService.cs:561-571`); `cleared_columns` lifts any non-`Deny` level,
including an explicit scope-specific `Mask` (`:603-609`, worse than the issue states); the response has no masking
metadata (`ProcedureEndpointRoutes.cs:337-339`).
**Recommended solution:** precedence fix first (wave 1): `cleared_columns` lifts only the catalog-sensitivity mask.
Then (wave 3): declaration-level `cleared_for_scopes` per computed column (allow-list; `otherwise: remove|mask`);
response meta `maskedColumns` (names) and `removedColumnCount` (integer); caller-aware describe endpoint built from one
pure column-planning function shared with the data path; `ddl_hash` warning for declared procedures, error outside
Development as an option.
**Deviations:** "virtual columns with their own consent rules" rejected (computed columns have no catalog identity;
would need storage in three repositories plus consent UI); `removedColumns` names rejected (existence information);
`openapi.json` stays caller-independent (a per-caller spec is a leak and a caching hazard).

### 3.7 #153 Procedures in the catalog and MCP

**Verdict:** sensible with changes. Confirmed: only an MCP hint line (`McpDatasetCatalog.cs:740-742`); list and
OpenAPI filter by roles only (`ProcedureEndpointRoutes.cs:63-76`); roles come only from token claims
(`Sid.cs:153-163`); 403 vs 404 existence oracle. Also found: open-schema (anonymous documentation) mode lists every
active procedure.
**Recommended solution:** one `ProcedureVisibility` predicate: role gate passes and consent on every table the execute
path checks (referenced tables plus result table, stricter than "result table only" in the issue). Unknown, role
denied, consent denied, no result table and unavailable-without-consent all answer an identical 404; server-side audit
keeps the precise reason. Fixed order: role, visibility/consent, concurrency lease, parameter validation, call, row
scope, column governance, audit. Catalog entries of kind `procedure` (id `procedure:<name>`) in list, describe and
search through #138's visibility service and index key; limits applied after the visibility filter.
**Deviations:** MCP call tool deferred to wave 4 (own increment, belongs to the #115 `Query` tool group; adding an
agent egress path before limits and error hygiene are fixed is an anti-pattern); timing is not equalised (no
artificial sleeps; residual difference covered by rate limiting and the cap); procedures do not get #111 tiers or tags.

### 3.8 #154 Procedures: limit, read-only verbs, concurrency, error text

**Verdict:** sensible with changes. Confirmed: `MaxRows` cuts raw rows before row scoping
(`MssqlProcedureInvoker.cs:156-161`, then `GovernedProcedureExecutionService.cs:196-205`) and `truncated` comes from
the raw read; `ReadOnlyTokenMiddleware.cs:22-30` does not list procedures; no concurrency cap; validation precedes
consent.
**Missing in the issue:** the `POST` body is the flat parameter dictionary (`ProcedureEndpointRoutes.cs:263-300`), so a
`limit` key collides with a declared parameter; `limit` becomes a reserved name and a declaration using it fails to load.
**Recommended solution:** wave 1: generic 422 `PROCEDURE_REJECTED` (database text never forwarded or logged), opt-in
`error_codes` map to fixed declared codes and messages; consent before validation; concurrency cap per tenant, user and
every consent-checked table, taken after visibility so a 429 never reveals existence. Wave 2: `limit` after row
scoping, clamped to `MaxRows`; new bounded `MaxScanRows` (default 10 x `MaxRows`, byte-capped) so a narrow row filter no
longer loses allowed rows without an unbounded read; `truncated` means "more scoped rows exist or scan cap hit";
read-only `POST` only for `POST /api/v1/procedures/{name}` after Phase 2 proves write procedures cannot be reached on
that route.
**Deviation:** stricter error handling than the issue (no raw message even without a map).

**Procedure increments (from the PRD):** INC-1 security slice (M, wave 1), INC-2 limits and verbs (M, wave 2), INC-3
column governance and describe (M, wave 3), INC-4 catalog and MCP entries (M, wave 3, after #138 INC-1/INC-2 and #111),
INC-5 MCP call tool (wave 4, own PRD). INC-1 and INC-2 change the same method and run sequentially.

## 4. Placement in the prioritized backlog (confirmed 2026-10-17)

| Wave | Rank (proposed) | Item | Size |
|---|---|---|:---:|
| 1 | 1.6 | `REQ-PROC-152..154` INC-1: procedure existence oracle, validation order, error text, concurrency cap, `cleared_columns` precedence | M |
| 1 | 1.7 | `REQ-CAT-138` INC-1: one catalog visibility service, ReBAC in MCP list/describe (starts after I-7, same files) | S |
| 1 | 1.8 | `REQ-REL-151`: composite-key import and storage (before I-18 and #138 INC-4) | S-M |
| 2 | 2.7 | `REQ-GOV-143` restricted scopes | M |
| 2 | 2.8 | `REQ-OPS-150` warm-up and readiness (measurement first) | M |
| 2 | 2.9 | `REQ-PROC-152..154` INC-2: limit, scan cap, read-only `POST`, error map, `ddl_hash` warning | M |
| 2 | 2.10 | `REQ-CAT-146` step A: `displayName` in MCP tools | S |
| 3 | 3.8 | `REQ-CAT-138` INC-2..4: version counter and snapshot, pre-filter ranking, bulk relations | L |
| 3 | 3.9 | `REQ-PROC-152..154` INC-3 and INC-4: scope-aware computed columns, masking meta, describe, catalog entries | M-L |
| 3 | 3.10 | `REQ-CAT-146` steps B and C: language texts and negotiation, multilingual search | M |
| 4 | 4.9 | `REQ-CAT-138` INC-5: opt-in embedding providers | L |
| 4 | 4.10 | `REQ-PROC-153` INC-5: MCP tool to call procedures (own PRD) | M |

The wave 2/3 items are not part of the running `PLAN-DELIVERY-W23` sequence; they enter the tracker when that delivery
reaches them, so the gate G-PLAN of the current run is not reopened. Wave 1 items need Phase 2 and Phase 3 before
Phase 4 like every other item.

## 5. Owner questions and decisions

**Decision 2026-10-17:** the owner accepted every recommended default below (OQ-1..OQ-15). The placements in section 4 are confirmed.

| ID | Question | Default |
|---|---|---|
| OQ-1 | Pull the three security slices (procedure INC-1, #138 INC-1, #151) into wave 1 ahead of the running wave 2 items? | Yes; #138 INC-1 starts right after I-7 |
| OQ-2 (#143 Q1) | Default of `RestrictedScopes`: `["Agent.Read"]` (secure by default) or empty (opt-in)? | `["Agent.Read"]` |
| OQ-3 (#143 Q3) | May the `Unmasked` access profile override the restriction? | No |
| OQ-4 (#138) | Default embedding provider; ship an ONNX model in the image? | `None` (keyword-only), no model in the image |
| OQ-5 (#138) | Should ReBAC-denied tables disappear from MCP list and describe? | Yes |
| OQ-6 (#138) | If the 5,000-table GraphQL rebuild gate is missed: separate PRD (opt-in exposure or split schema)? | Yes, separate PRD |
| OQ-7 (#146) | Supported languages and default | `["en","de"]`, default `en` |
| OQ-8 (#150) | Bounded fail-open of readiness after 30 s, background warm-up with `503 Warming`? | Yes |
| OQ-9 (#150) | ReadyToRun images | Decide after measurement (accept if JIT is at least 25 % of the cold path) |
| OQ-10 (#152) | Was the `cleared_columns` lift of an explicit scope `Mask` intended by any existing declaration? | No, treat as a bug |
| OQ-11 (#152) | Who maintains `cleared_for_scopes` in declaration files (moves scope control for computed columns out of the consent UI)? | Platform team with data-owner review |
| OQ-12 (#153) | Hide procedures from the anonymous open-schema OpenAPI? | Yes |
| OQ-13 (#153) | Is an MCP tool to call procedures wanted now? | Not now (wave 4) |
| OQ-14 (#154) | `MaxScanRows` default 10 x `MaxRows`; `RequireDdlHashForDeclared` true outside Development? | Yes, yes |
| OQ-15 (#151) | Child table of key pairs (vs JSON array); `dbt_constraints` package support later? | Child table; later |

Further PRD-level questions with defaults are listed in each PRD; they default to the PRD recommendation unless the
owner decides otherwise.
