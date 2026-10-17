# REQ-CAT-146: Display Names and Descriptions in Several Languages (MCP Tools, REST/GraphQL Catalog, Search)

**Document ID:** `REQ-CAT-146`
**Date:** 2026-10-17
**Status:** PRD READY (Phase 1 complete); next: Phase 2 plan (step A can start without waiting for the rest)
**Author:** Enterprise Product Manager (Phase 1)
**Source:** GitHub issue #146 (owner, v1.1.16); related: #138 (`REQ-CAT-138`, search index), #111 (catalog visibility tiers), #153
**Branch:** `docs/issue-intake-2026-10-17` (docs only)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Binding project constraints:** fail closed; no production deployment and **no upgrade migrations** (the `0001` baseline DDL of SQLite, PostgreSQL and SQL Server plus the shape snapshots is edited in place; the caller supplies the newest schema); English only; no row values in logs.

---

## 1. Verdict

**Sensible with changes.** The use case (one table shown as "Aufträge" or "Orders") is real and the MCP gap is real. Changes against the issue: store other languages as a **JSON map in the existing table and column rows** (not child translation tables), keep the default language in the existing columns, accept only a **configured set of languages**, and index translated texts in the one search document of `REQ-CAT-138` instead of building a per-language index. The first step (return `displayName` in MCP) is trivial and independent.

| Item | Result |
|---|---|
| Size | M overall: step A S, step B M, step C S (after `REQ-CAT-138` INC-138-2) |
| Wave | Wave 3 (feature completion): A and B after #111 step I-7 (same files and DDL), C after INC-138-2 |
| Dependencies | I-7 (`McpDatasetCatalog.cs`, `CatalogVisibility.cs`, baseline DDL serialisation, G-SHARED); `REQ-CAT-138` INC-138-2 for step C |
| Security class | New free-text channel read by LLM agents; needs Phase 3 review (injection, size, language input, hidden-dataset leakage) |

## 2. Verified Current State

| # | Claim in #146 | Result | Evidence |
|---|---|---|---|
| C1 | `Table.DisplayName` exists and GraphQL returns it | **Confirmed** | `Autheris.Domain/Model/TableModels.cs:41` (`string DisplayName`, default empty); `TableMetadataDto.DisplayName` at `Autheris.GraphQL/Types/QueryTypes.cs:925-937`; column `display_name TEXT NOT NULL` in `TABLES` (`PostgreSqlBaselineSchema.cs:17-33`) |
| C2 | `list_datasets` and `describe_dataset` do not include it | **Confirmed** | `McpDatasetSummary` is built from id, description, sensitivity, column count, GraphQL field, tags, access status (`McpDatasetCatalog.cs:168-176`); `McpDatasetColumn` from name, type, description, sensitivity, key flag, GraphQL field and type, semantics (`:235-247`). No display name anywhere in the MCP models |
| C3 | `TableColumn` has `Description` and `LongDescription` but no display name | **Confirmed** | `TableModels.cs:100-110`; `TABLE_COLUMNS` has `description`, `long_description`, `doc_source`, `meta_json` and no display name (`PostgreSqlBaselineSchema.cs:38-47`) |
| C4 | No language on tables, columns or tools; no language parameter | **Confirmed** | grep over `src` finds no `Accept-Language`, request localisation or language argument in catalog or MCP code |
| C5 | Search matches display names and descriptions | **Partly wrong today** | The index contains the **technical** names and short descriptions only (`CatalogSearchEngine.cs:69-73`): neither `Table.DisplayName` nor long description is indexed (`REQ-CAT-138` C15). The issue's point 3 therefore depends on #138 |
| C6 | "The governance import and seeds can set these texts in the same place as descriptions today" | **Confirmed as a requirement** | Texts enter through `UpsertTableMetadataAsync` (`ITableMetadataRepository`, `IGovernanceRepository.cs:7`), dbt documentation sync (`DbtMetadataIngestionService`), OpenAPI ingestion and the caller-supplied schema; all go through `TableMetadata` |

Existing storage precedent: structured optional data in `TABLES`/`TABLE_COLUMNS` is stored as JSON text columns (`http_endpoint_json`, `meta_json`), and ordered composite data as child tables with ordinals (`TABLE_PRIMARY_KEY_COLUMNS`, #94). Texts are neither queried by language nor joined, so the JSON-column pattern fits.

### 2.1 Overlap with existing plans

| Plan | Overlap | Handling |
|---|---|---|
| #111 (step I-7) | `Summary`/`Full` tiers decide what a non-consented caller sees. Display name and description are tier-dependent output | Translations follow the same tier rules as the default texts: shown exactly where the default texts are shown; never more |
| `REQ-CAT-138` | Index document builder, freshness (`content_version` bump), `ShapeHit` hygiene | Section 8 below; step C waits for INC-138-2 |
| #153 procedures in catalog | Procedures also have summary and description | The `LocalizedTexts` value object is reusable; procedures are out of scope here |
| `REQ-CAT-138` D-2 | Cross-language search without a model | Curated translations give German/English matches for described tables without any embedding model |
| #143 | None | none |

## 3. Problem Statement

A business user in German and an agent answering in English see the same technical table name. The MCP catalog tools hide the display name that already exists, and texts exist in a single language. Search cannot match a word that only occurs in another language.

## 4. Benchmark and Rejected Anti-Patterns

| Practice | Use here |
|---|---|
| BCP 47 language tags (RFC 5646) as keys; canonical case (`de`, `de-CH`, `zh-Hant`); lookup fallback by truncation (RFC 4647 section 3.4: `de-CH` then `de` then default) | R-146-3 |
| HTTP: `Accept-Language` request header, `Content-Language` response header, `Vary: Accept-Language` for cacheable responses | R-146-6 |
| Catalogs (DataHub, OpenMetadata, Unity Catalog comments) keep one primary text per entity; multi-language is usually handled by display-name properties or external glossaries, and the default-language text is always complete | Default language in existing columns, translations optional and partial |
| dbt 1.9 model-level docs have one `description`; per-language text has no native place | dbt import stays default-language (Q-146-5) |

Rejected anti-patterns:

1. **Child translation tables** (`TABLE_TRANSLATIONS`, `COLUMN_TRANSLATIONS`). At 5,000 tables x 20 columns x 3 languages that is 300,000 rows that must be loaded with every catalog refresh as an extra query per chunk (`REQ-CAT-138` already fights refresh cost), plus two tables, two shape entries, three repositories. Texts are never filtered by language in SQL. Alternative: one JSON column per existing row.
2. **Free-form language tags.** Unbounded tags allow storage bloat and a tag-guessing probe. Alternative: a configured allow-list of languages (default `en`, `de`, at most 16).
3. **Per-language search indexes or per-language query routing.** Alternative: one BM25 document per entry containing all stored texts, with diacritic folding, so a word in any language matches.
4. **Silent language fallback without saying so.** Alternative: every answer names the language it used.
5. **Translating the GraphQL schema descriptions per request.** The schema is built once and shared; per-request variation would need per-language schema instances. Out of scope (non-goal 2).
6. **Machine translation inside the gateway.** Out of scope and a data-egress risk.

## 5. Requirements

### 5.1 Step A: `displayName` in the MCP tools (S)

- **R-146-1** `list_datasets` and `describe_dataset` return `displayName` for each dataset. An empty display name falls back to the technical table name.
- **R-146-2** `describe_dataset` returns `displayName` per column. `TableColumn` gets `DisplayName` (nullable), stored in a new `display_name` column of `TABLE_COLUMNS` in the baseline (same DDL change as step B so the baseline is edited once; step A may ship table-level only if the column change is deferred to step B). An empty column display name falls back to the column name.
- **AC-146-1** Both tools return the fields; fallback tested for table and column.

### 5.2 Step B: Texts per language (M)

- **R-146-3 Model.** A value object `LocalizedTexts` holds, per language tag, optional `displayName`, `description`, `longDescription`. It exists on `Table` and `TableColumn` as `Texts` (empty by default). The existing `Table.DisplayName`, `Description`, `LongDescription` and the column equivalents are the texts in the **catalog default language** (so existing data and all existing readers stay valid). Translations never replace the default texts; a missing field in a translation falls back field by field to the next language in the chain, then to the default texts.
- **R-146-4 Storage.** `texts_json TEXT NULL` on `TABLES` and `TABLE_COLUMNS` in the baseline DDL of all three engines and shape snapshots (no migration; the caller supplies the newest schema). Format: `{"de":{"displayName":"...","description":"...","longDescription":"..."},"en":{...}}`. The default language is not stored in the map (a tag equal to the default language is rejected on write to keep one source of truth).
- **R-146-5 Configuration.** `Gateway:Catalog:DefaultLanguage` (default `en`) and `Gateway:Catalog:SupportedLanguages` (default `["en","de"]`, 1 to 16 entries, each a syntactically valid BCP 47 tag, canonicalised on start, `DefaultLanguage` must be contained). Validated at start (`ValidateOnStart`).
- **R-146-6 Limits on write.** Language tag must be in `SupportedLanguages`; per text field length caps (display name 256, description 2,000 and long description 20,000 characters, aligned with the existing description limits if any are stricter); control characters (other than line breaks in long descriptions) rejected; at most 16 languages per entry. A violation rejects the whole write (fail closed), the error names the field and limit, never echoes the text.
- **R-146-7 Negotiation.** Resolution order for the requested language: (1) the explicit `language` argument of the MCP tool or the `language` query/GraphQL argument; (2) `Accept-Language` of the HTTP request (quality values honoured, RFC 4647 lookup, only tags in `SupportedLanguages`); (3) the default language. A tag that is syntactically invalid or not supported is **not an error**: it resolves to the default language and the answer says so. Resolution runs once per request and produces an ordered fallback chain (for example `de-CH`, `de`, default).
- **R-146-8 Response contract.** MCP tool results carry `language` (the language of the texts actually returned, the default when nothing matched) and, when the request named a language that was not served, `requestedLanguage`. REST catalog responses set `Content-Language` and `Vary: Accept-Language`. GraphQL `catalog(language:)` returns the DTO texts in the resolved language and adds `language` to the DTO.
- **R-146-9 Tool contracts.** `list_datasets`, `describe_dataset` and `search_catalog` accept an optional `language` string argument (input schema updated, max length 35); the search tool also returns the display name in the resolved language per hit.
- **R-146-10 Import and admin.** The governance upsert path and the caller-supplied schema accept `Texts`; the admin API validates with R-146-5/6. A change of texts bumps `content_version` (`REQ-CAT-138` R-138-3.1) and is audited like other metadata writes. dbt keeps one `description` as default-language text; an optional dbt `meta.autheris.texts` mapping is deferred (Q-146-5).
- **AC-146-2** With German and English texts stored, `language: "en"` returns the English texts and `language: "de"` the German ones; `language: "fr"` (not supported) and `language: "xx-invalid"` return the default language and the answer names it; `Accept-Language: de-CH,de;q=0.9,en;q=0.5` returns German.
- **AC-146-3** A translation missing only the description returns its display name in that language and the description from the default texts; the response still reports the requested language.
- **AC-146-4** Write validation: unknown tag, default-language tag in the map, oversized text, 17th language, control character are each rejected with a stable error code and no echo.

### 5.3 Step C: Search over all languages (S, after `REQ-CAT-138` INC-138-2)

- **R-146-11** The search document of an entry contains the display names and descriptions of **all** stored languages plus the default texts, each field weighted as its default-language counterpart (`REQ-CAT-138` R-138-2.1). Matching is language-agnostic.
- **R-146-12** The tokeniser folds diacritics and case for matching (`ä` to `a`, `ß` to `ss`, NFKD) so a query without umlauts finds "Aufträge". No stemming or compound splitting in this PRD (Q-146-4).
- **R-146-13** Translated texts of columns are indexed only as part of the table document but, for hygiene, a hit whose only match is a translated text of a column the caller cannot see is dropped (`ShapeHit` rule, `REQ-CAT-138` R-138-2.6). Texts of a hidden table are never indexed into a visible result.
- **R-146-14** `SecretScrubber.Redact` is applied to translated texts before indexing.
- **AC-146-5** `search_catalog` finds a table by a word that appears only in its German display name, with the default language English, for a caller who may see the table.
- **AC-146-6** The same query by a caller who may not see the table returns nothing for it in any language; hit count and ranking of other tables do not change.

## 6. Security Requirements

1. **SEC-146-1 No leak through texts.** A hidden dataset does not appear through its display name or any translation in any tool, endpoint, GraphQL field, search hit, count or ranking. Translations are returned exactly where the default texts are returned and under the same tier (`Hidden/Summary/Full`) and column visibility rules.
2. **SEC-146-2 Column texts.** Column display names and translations are returned only for columns the caller may list (same rule as `McpDatasetColumn`, `McpDatasetCatalog.cs:228-247`); a column whose level is `Deny` is not listed and not matched.
3. **SEC-146-3 Prompt injection.** Texts are read by LLM agents. Write-side validation (length caps, control characters), the existing `SecretScrubber` on indexed text, and a documented trust statement: texts are authored by data owners and carry the same trust as descriptions today. Texts are returned as data fields, never merged into tool instructions or guidance strings.
4. **SEC-146-4 Input hardening.** The `language` argument and the `Accept-Language` header are length-limited (35 characters per tag, 8 tags and 512 characters per header), parsed without exceptions, never reflected unescaped into headers (`Content-Language` carries only a canonical tag from `SupportedLanguages`), never used as a file or database key.
5. **SEC-146-5 DoS.** Per-entry language count and text size caps bound memory; the index grows by at most the capped text; `SupportedLanguages` is bounded.
6. **SEC-146-6 Fail closed.** If the configured default language is missing from the supported list, start fails. A malformed `texts_json` row is treated as "no translations" for that row, logged once per table without text content, never failing the catalog load.
7. **SEC-146-7 Audit.** Writes of texts appear in the metadata audit trail with table id and language list, not the text content.
8. **SEC-146-8 Caches.** REST responses vary by `Accept-Language` (`Vary` header); no shared cache may serve one language for another. MCP responses are not cached.

## 7. Non-Goals

1. A portal UI or a translation workflow (review, status per language).
2. Translated GraphQL schema descriptions, type names or field names.
3. Machine translation or language detection.
4. Per-user stored language preference (token claim) beyond `Accept-Language`; revisit when #113 or the identity model needs it.
5. Translating tags, owner names, sensitivity labels, error messages or MCP tool descriptions.
6. Per-language access control.

## 8. Interaction with `REQ-CAT-138`

1. One index document per entry; no per-language index (anti-pattern 3).
2. `content_version` bump on a text edit refreshes the search index of every instance but never the GraphQL schema (descriptions are not structure); the `structure_version` rule of `REQ-CAT-138` R-138-3.1 is the contract.
3. Embedding providers (`REQ-CAT-138` INC-138-5) receive the concatenated multilingual document; a multilingual model is only needed for uncurated tables. For curated tables, the translations deliver German/English matching without any model.
4. Step C is not merged before INC-138-2 delivers the document builder and the version counter; step A and B do not depend on it (the old index simply does not see translated words until then).

## 9. Deviations from the Issue

| # | Issue says | This PRD |
|---|---|---|
| D-1 | "Texts per language ... for example a map keyed by BCP 47 tag" | Kept, as a JSON map in the existing rows, default language stays in the existing columns |
| D-2 | One default language per catalog | Kept, as `Gateway:Catalog:DefaultLanguage` plus an allow-list `SupportedLanguages` (new) |
| D-3 | Unknown language returns the default and names it | Kept, plus invalid tags and `Accept-Language` handling specified (RFC 4647 lookup) |
| D-4 | `search_catalog` matches all stored languages | Kept; depends on `REQ-CAT-138` INC-138-2 and adds diacritic folding |
| D-5 | "Import and seeds set these texts in the same place" | Kept for the upsert path and caller-supplied schema; dbt translation mapping deferred |
| D-6 | Returns the language used | Kept: `language` and `requestedLanguage` fields, `Content-Language` on REST |

## 10. Open Owner Questions

| # | Question | Recommended default |
|---|---|---|
| Q-146-1 | Storage form: JSON map in the row or child translation tables | JSON map in `texts_json` (house pattern, no extra queries per refresh); revisit only if per-language queries or per-language audit are required |
| Q-146-2 | Default language and supported set | `en` default, `["en","de"]` supported, configurable up to 16 |
| Q-146-3 | Add `DisplayName` to `TableColumn` now | Yes, with step B in the same baseline edit |
| Q-146-4 | German compound words ("Auftragsposition" for "Position") | Not solved by the keyword index; accepted limit. Options later: character n-grams or a multilingual embedding (`REQ-CAT-138` INC-138-5). Default: document the limit |
| Q-146-5 | dbt import of translations | Deferred; default-language `description` only. If wanted: `meta.autheris.texts` per model and column, same validation as the admin API |
| Q-146-6 | Should a token claim or user profile carry a preferred language | No in this PRD; `language` argument and `Accept-Language` only |
| Q-146-7 | Does a text edit need four-eyes or an approval flow | No; same rule as editing descriptions today |

## 11. Increments and Waves

| Step | Content | Size | Wave | Depends on |
|---|---|:---:|---|---|
| A | `displayName` in `list_datasets` and `describe_dataset` (table; column when the DDL lands) | S | 3 | I-7 (same MCP files) |
| B | `LocalizedTexts`, `texts_json` x3 engines, configuration, negotiation, tool/REST/GraphQL language support, admin and import validation | M | 3 | A, I-7 (baseline DDL serialisation) |
| C | Search integration, diacritic folding | S | 3 | B, `REQ-CAT-138` INC-138-2 |

Step B edits the baseline DDL; it is serialised with the other DDL-touching steps (G-SHARED) and shares one `0001` edit with `REQ-CAT-138` INC-138-2 and `REQ-REL-151` where the branch order allows, to avoid repeated shape snapshot churn.

## 12. Test Strategy Hints (for Phase 2)

1. Unit: negotiation matrix (explicit argument, header with q-values, invalid tags, unsupported tags, chain `de-CH` to `de` to default), write validation matrix, JSON round trip on three engines.
2. Integration: MCP tool calls for two callers (visible and hidden table), REST `Content-Language`/`Vary`, GraphQL `catalog(language:)`.
3. Security tests: SEC-146-1 and -2 with tiers `Hidden` and `Summary`; oversized and control-character texts; malformed `texts_json` row; header injection attempt in `Accept-Language`.
4. Shape snapshot tests for the new columns on three engines.
