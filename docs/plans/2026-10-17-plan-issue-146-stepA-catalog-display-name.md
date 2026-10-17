# Implementation Plan: PLAN-CAT-146-A - `displayName` in MCP `list_datasets` / `describe_dataset`

**PRD:** [`REQ-CAT-146`](2026-10-17-req-issue-146-multilingual-catalog-texts.md) section 5.1 (step A only; steps B/C are rank 3.10, out of scope). **Master plan rank:** 2.10. **Size:** S. **Base:** `origin/main` `f0071be6` plus the lane-B plans [`PLAN-GOV-111`](2026-10-16-plan-issue-111-dataset-tags-catalog-visibility.md) (I-7, rev. 3 incl. Phase 3 conditions) and [`PLAN-CAT-138-INC1`](2026-10-17-plan-issue-138-inc1-catalog-visibility-service.md) (I-7a, incl. `SEC-CAT138-*`). No lane-B branch exists on `origin` yet (`git branch -r`: no `feat/issue-111-*`, no `#138` branch), so every I-7/I-7a fact below is a **projection from the plans**, marked **[P]**.

## 1. Verified Facts

| # | Fact | Evidence |
|---|---|---|
| F1 | `Table.DisplayName` exists, non-null, default `""` | `Domain/Model/TableModels.cs:41`; `display_name` `NOT NULL` in `TABLES` on three engines (SQL Server `NVARCHAR(512)`, PostgreSQL/SQLite `TEXT`, unbounded) |
| F2 | `TableColumn` has no display name; adding one needs baseline DDL x3 + shapes | `TableModels.cs:100-110`; `TABLE_COLUMNS` in `*BaselineSchema.cs` |
| F3 | `McpDatasetSummary(Id, Description, Sensitivity, ColumnCount, GraphQlField, Tags, AccessStatus)` and `McpDatasetDescription(Id, Domain, Schema, Table, Description, LongDescription, ...)` carry no display name | `Domain/Model/McpDatasetModels.cs:8-15`, `:99-117` |
| F4 | Single construction sites: summary `McpDatasetCatalog.cs:174`, description `:295`; only consumer outside is `GatewayMcpQueryExecutor.cs:285-287` (serialises the record, camelCase) and the legacy asset projection `:341-350` (unchanged) | `git grep` on `origin/main` |
| F5 | Display names are **externally authored** text: OpenMetadata, Purview, Collibra, Alation, dbt `model.Name`, OpenAPI ingestion, caller schema, governance ratchet (`DataCatalogSyncService.cs:197`, `OpenMetadataCatalogAdapter.cs:122`, `DbtSemanticManifestIngestionService.cs:329`, `CatalogGovernanceRatchet.cs:35`); no write-side length or character validation | same |
| F6 | describe of an unknown/hidden table throws `TableNotFoundException(requested)` with the caller's own input, never catalog text | `McpDatasetCatalog.cs:797` |
| F7 **[P]** | After I-7: `Summary` rows reach MCP with `CatalogView == Summary`; `describe_dataset` builds them via new `BuildSummaryDescription` (allow-list); a reflection test pins the exact `McpDatasetDescription` property set for `Summary` (BC-14, F-C10; new properties default to excluded); `McpDatasetModels.cs` gains `View` and a nullable `GraphQl` | `PLAN-GOV-111` A-4, WP-3, BC-14 |
| F8 **[P]** | After I-7a: list/describe obtain rows only from `ICatalogVisibilityService.GetVisibleAsync` / `FindVisibleAsync` (ReBAC intersected); line anchors in `McpDatasetCatalog.cs` move | `PLAN-CAT-138-INC1` D-5.1 |

## 2. Decisions

1. **Table level only; column `displayName` deferred to step B** (default taken, PRD Q-146-3 and the R-146-2 escape clause). Step A has **no DDL**, so it is outside the G-SHARED baseline serialisation; step B adds the `TABLE_COLUMNS` column and `texts_json` in one baseline edit. No column field is emitted in step A (no placeholder equal to the column name).
2. **Contract.** `McpDatasetSummary` and `McpDatasetDescription` get a **required positional** `string DisplayName` directly after `Id` (breaking, no production). Required, not optional, so the compiler forces every construction site, including the post-I-7 `BuildSummaryDescription`, to set it. JSON: `displayName`, non-null, always present.
3. **One output helper.** New `src/Autheris.Application/Mcp/Services/McpCatalogText.cs`, `internal static string DisplayName(string? stored, string tableName)`: remove Unicode `Cc` and the format/bidi controls (`U+200B..U+200F`, `U+202A..U+202E`, `U+2066..U+2069`, `U+FEFF`), collapse whitespace runs to one space, trim, cap at **256 UTF-16 code units** without splitting a surrogate pair; empty result -> `tableName` (R-146-1 fallback). No exceptions, no logging of the text. Both construction sites call it with `t.Identifier.TableName`. Step B replaces the cap with its write-side validation but keeps this read-side guard.
4. **Visibility (fail closed).** The helper is applied only to rows that the visibility layer already returned (I-7 tiers, I-7a ReBAC); step A adds no read path, no lookup by display name and no matching on it. `Hidden`, `Unmarked`, unconsented and ReBAC-denied tables therefore never yield their display name; error messages stay as F6.
5. **`Summary` tier.** `DisplayName` is table-level text of the same trust as `Description`, which the `Summary` allow-list already contains, so it is **added to the allow-list** (PRD SEC-146-1: shown exactly where the default texts are shown). The I-7 reflection test is updated in the same commit, deliberately, with a one-line rationale.
6. **Untrusted data for LLM consumers (SEC-146-3).** The value is a data field only; tool descriptions in `McpDatasetTools.cs` and any guidance strings are **not** changed and never interpolate catalog text. Trust statement goes into the PR text: authored by data owners/external catalogs, same trust as `Description`.
7. **Out of scope:** `list_datasets` `search` matching on display names and `search_catalog` (step C), GraphQL `catalog` (already returns `DisplayName`, unchanged), REST catalog DTOs, MCP resources, write-side validation and audit (step B).

## 3. Work Packages (one developer, one PR)

| WP | Content | Size |
|---|---|:---:|
| WP-A1 | `McpCatalogText` helper + unit tests | XS |
| WP-A2 | Record contract (Decision 2), both construction sites, `Summary` allow-list update, MCP tests | S |

## 4. TDD Test List (red first)

**`Tests.Unit/Mcp/McpCatalogTextTests.cs`:** `NullEmptyOrWhitespace_FallsBackToTableName`, `OnlyControlCharacters_FallsBackToTableName`, `ControlAndBidiOverrideCharacters_Removed`, `WhitespaceRuns_CollapsedAndTrimmed`, `LongerThan256_Truncated_NoLoneSurrogate`, `PlainUnicodeText_Unchanged` ("Aufträge").

**`Tests.Unit/Mcp/McpDatasetDisplayNameTests.cs`:** `ListDatasets_ReturnsStoredDisplayName`, `ListDatasets_EmptyDisplayName_FallsBackToTableName`, `DescribeDataset_FullView_ReturnsDisplayName`, `DescribeDataset_EmptyDisplayName_FallsBackToTableName`, `DescribeDataset_SummaryView_HasDisplayName_NoColumns` [P], `DisplayName_OversizedAndControlChars_SanitisedInBothTools`.

**Security (`[Trait("Category","Security")]`, same class):** `HiddenTable_DistinctiveDisplayName_AbsentFromListJsonAndDescribeError`, `UnconsentedUnmarkedTable_DisplayNameAbsent` [P], `RebacDeniedTable_DisplayNameAbsentFromListAndDescribe` [P, after I-7a], `ToolsList_NeverContainsCatalogDisplayName` (a distinctive stored value never appears in serialised `tools/list`), `Paging_TotalUnaffectedByDisplayName`.

**Wiring (`McpDatasetToolWiringTests`, extend):** `ListDatasets_Json_HasCamelCaseDisplayNameAfterId`, `DescribeDataset_Json_HasDisplayName`.

**Changed:** the I-7 `Summary` allow-list reflection test gains `DisplayName` [P]; existing `McpServerTests`, `ColumnLevelParityTests`, `McpDatasetCatalogScopeLevelTests` stay green (constructor updates only).

## 5. Fail-Closed Summary

Display name appears only on rows returned by the discovery visibility path; no new lookup; fallback is the technical name the caller already sees in `id`; output is bounded (256) and stripped of control/bidi characters; never in logs, errors, tool descriptions or guidance. No configuration, so no misconfiguration path.

## 6. Sequencing and Parallelisation

**(a) Shared files.**

| File | Shared with | Nature |
|---|---|---|
| `Application/Mcp/Services/McpDatasetCatalog.cs` | I-7 WP-3 (`BuildSummaryDescription`, `ColumnCount`), I-7a D-5.1 (visibility migration), I-22 #113 A (MCP tools, textual), #143 rank 2.7 (column levels, textual), #152-#154 INC-3/4 rank 3.9 (caller-aware describe) | semantic with I-7, textual with the rest |
| `Domain/Model/McpDatasetModels.cs` | I-7 WP-3 (`View`, nullable `GraphQl`), #152-#154 INC-3/4 | semantic with I-7 |
| I-7 `Summary` reflection test (`McpCatalogSummaryViewTests.cs` [P]) | I-7 (owner), I-7a T-CAT138-10 | must be extended, not duplicated |
| `McpDatasetToolWiringTests.cs`, `McpServerTests.cs` | I-4 #115 (MCP tool registry), I-7 | textual |

Open implementation lanes (checked with `git diff --name-only origin/main...origin/<branch>`): lane A (`feat/issue-107-wp-c`, `-128`, `-129`, `-130`, `release/chunk1-consolidated`) and lane C (`fix/issue-157-*`, `fix/issue-158-inc0-*`, `fix/procedure-inc1-security`) touch **none** of these files. Lane B (I-4 -> I-7 -> I-7a) owns all of them. No baseline DDL, so no G-SHARED conflict with #151, #138 INC-2/4, #146 B, #158 INC-1.

**(b) Hard dependencies.** I-7 merged (Decision 5 needs `BuildSummaryDescription` and its reflection test; the summary/description records change in WP-3). I-7a is **not** a semantic dependency (step A only decorates returned rows) but edits the same methods; running before it would force I-7a to rebase. Step B (rank 3.10) depends on this step.

**(c) Recommended position.** New tracker step **I-7b** in **lane B / chunk 2**, directly after I-7a and before I-8, stacked on the I-7a branch (suggested branch `feat/issue-146-a-display-name`). It runs in parallel with lanes A and C and with lane D planning without coordination. It must not run in parallel with I-7 or I-7a. Phase 3 is a light review (data-only field, no new read path); re-verify F7/F8 line anchors and the reflection test name as the first Phase 4 task and record them in the PR.

## 7. Owner Questions

None. Defaults taken: column level deferred to step B (PRD Q-146-3); `displayName` included in the `Summary` view (PRD SEC-146-1); read-side cap 256 code units (below the SQL Server column width 512).

## 8. Phase 3 security review

**Reviewer:** `csharp-security-expert`, light review (size S, data-only field). **Verdict: APPROVED WITH CONDITIONS.**

Verified: F1-F6 hold on `origin/main` (no catalog-text logging in `McpDatasetCatalog.cs`; F6 error uses caller input). F7/F8 [P] are consistent with I-7 A-4/BC-14 (allow-list, new properties default excluded) and I-7a D-5.1 (rows only from the visibility service). Not-in-scope list (no search matching, no step C) is the correct side-channel posture. **Finding:** `SemanticMcpCompiler.CompileToolAsync` (`SemanticMcpCompiler.cs:41-49`) already interpolates `Table.DisplayName` unsanitised into a per-table tool description when `Description` is empty. Decision 6 and test `ToolsList_NeverContainsCatalogDisplayName` therefore claim more than the code guarantees.

### Binding conditions

- **SEC-CAT146A-01** Phase 4 first task: determine whether `CompileToolAsync` output reaches `tools/list` (or any LLM-visible tool description) and for which tables (visibility-filtered or not). If yes, the fallback chain at `:41-43` MUST use `McpCatalogText.DisplayName(...)` (or the raw name-only fallback) so no raw display name reaches a tool description; correct Decision 6 / section 5 wording accordingly and record the finding in the PR. If it provably cannot reach `tools/list`, record the evidence and scope T-CAT146A-06 to the static tools.
- **SEC-CAT146A-02** The helper MUST map control and whitespace characters to a single space BEFORE collapsing, not delete `Cc` first (deleting `\n`/`\t` would fuse words, and U+0085 would be silently dropped). Order: replace `char.IsWhiteSpace`/`Cc`/`U+2028`/`U+2029` with space, remove invisibles, collapse, trim, cap.
- **SEC-CAT146A-03** The invisible-character removal set MUST be widened beyond Decision 3 to all of Unicode category `Cf` plus `U+2060..U+2064`, `U+180E`, `U+00AD`, `U+FE00..U+FE0F`, and the Tags block `U+E0000..U+E007F` (surrogate pairs; invisible "ASCII smuggling" prompt-injection vector), and private-use/unassigned code points (`Co`, `Cn`). Implement via `Rune`/`CharUnicodeInfo.GetUnicodeCategory` over runes, not per `char`.
- **SEC-CAT146A-04** Invalid UTF-16 in the stored value (lone high/low surrogate) MUST be replaced by nothing (or U+FFFD, then dropped as nothing-visible) so the JSON serialiser never emits an invalid string or throws; the helper MUST NOT throw for any input. The 256 cap is counted on the sanitised text and cut on a rune boundary (never inside a surrogate pair); no ellipsis marker that could be mistaken for content is required.
- **SEC-CAT146A-05** The cap and sanitiser apply in BOTH sites and in the post-I-7 `BuildSummaryDescription`; the `DisplayName` JSON value is also the only place the stored value is emitted (not duplicated into `Description`, `GuidanceNote`, hints or error strings). Fallback `tableName` is the technical name already present in `id`; nothing else may substitute.
- **SEC-CAT146A-06** `DisplayName` MUST NOT be added to any log message, exception message, audit/telemetry payload or metric label (including the executor `GatewayMcpQueryExecutor.cs:285-287` path and tool-result tracing). Confirm by grep in the PR; the helper has no logger dependency.
- **SEC-CAT146A-07** The I-7 `Summary` reflection allow-list test MUST be extended in place (no duplicate) so that `DisplayName` is the only added property, and a hidden/Unmarked/unconsented/ReBAC-denied table must yield no row at all (not a row with a blanked name). `totalCount`/paging MUST NOT depend on display name content.
- **SEC-CAT146A-08** Residual risk accepted and documented in the PR: sanitised visible text (up to 256 chars) can still carry natural-language instructions to an LLM; mitigation is data-field-only placement plus the existing "catalog text is untrusted data" posture. GraphQL `catalog` `DisplayName` and legacy asset projection stay unsanitised in this step; add one tracked follow-up note to step B/#146 (not a blocker).

### Mandatory security tests (`[Trait("Category","Security")]`)

- **T-CAT146A-01** `Sanitiser_NewlineAndTab_BecomeSingleSpace_WordsNotFused` (SEC-02).
- **T-CAT146A-02** `Sanitiser_TagCharactersAndZeroWidthAndBidi_Removed` (U+E0041 tags, U+200B, U+202E, U+2066, U+2060, U+FE0F, U+00AD) (SEC-03).
- **T-CAT146A-03** `Sanitiser_LoneSurrogates_NeverThrow_ValidJson` (SEC-04).
- **T-CAT146A-04** `Sanitiser_CapAtSurrogatePair_NoSplit_AndCountedAfterSanitising` (SEC-04).
- **T-CAT146A-05** `Sanitiser_AllInvisibleInput_FallsBackToTableName` (SEC-02/03).
- **T-CAT146A-06** `ToolsList_AndCompiledToolDescriptions_NeverContainRawDisplayName` including `SemanticMcpCompiler` with empty `Description` (SEC-01).
- **T-CAT146A-07** `DisplayName_NotEmittedInLogsOrExceptionMessages` (capturing logger over list, describe, unknown-table error) (SEC-06).
- **T-CAT146A-08** `SummaryAllowList_OnlyDisplayNameAdded_HiddenAndDeniedRowsAbsent` (extends I-7 reflection test, SEC-07).
- Existing plan security tests (`HiddenTable_...`, `UnconsentedUnmarkedTable_...`, `RebacDeniedTable_...`, `Paging_TotalUnaffectedByDisplayName`) are retained and binding.

**Owner questions:** none. Default: keep table level only, 256-unit cap, `displayName` in `Summary` view.
