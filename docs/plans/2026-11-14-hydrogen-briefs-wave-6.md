# Phase 4 Hydrogen Developer Briefs — Wave 6 (Plus Post-Freeze & Follow-On Queue)

- **Date:** 2026-11-14
- **Baseline:** `origin/main` @ `173703d6` / `2b2876bd` (sequenced immediately after Wave 5 `docs/plans/2026-11-14-hydrogen-briefs-wave-5.md`).
- **Global Constraints for All Hydrogen Workers:**
  1. **English Only:** All identifiers, comments, error messages, logs, and commit messages MUST be in English.
  2. **Constrained Local Build & Filtered Test Execution Only:**
     - Always set `export MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget`.
     - Build only the target test/source `.csproj` with `-m:1 -nodeReuse:false` (NEVER run `dotnet build Autheris.sln`).
     - Run `dotnet test` ONLY with `--no-build -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s` and a tight `--filter "<Filter>"` on both `Autheris.Tests.Unit` (`-p:AutherisIncludeTestAuth=true`) and `Autheris.Tests.Architecture`.
     - Compile-check `Autheris.Tests.Integration` with `dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release --no-dependencies -m:1 -nodeReuse:false` (NEVER execute integration tests or unfiltered test suites locally).
  3. **PR Size & Hygiene:**
     - Keep every PR slice `<= 400` net added lines in `src/` (generated `.shape.json` files are exempt).
     - Never write plan-ID tokens (`WP-`, `SEC-`, `OD-`) in code comments under `src/` (`PlanIdCommentRatchetTests`).
     - Never write literal `file`+`://` URI strings in any `.md` file (`DocsIntegrityTests.No_absolute_file_links`).
  4. **Clean `bin/obj` After Push:** Run `find src tests -maxdepth 2 \( -name bin -o -name obj \) -exec rm -rf {} +` after pushing your branch to keep `/work` disk usage low.

---

## 0. Backlog Completeness Audit & Deduplication Notes

1. **`WP-SIMPL-18` (`WP-SIMPL-18-borderline-triage-execution.md`):** **100% MERGED on `main` (`3dc2558a` `#542`)**. Differential Privacy noise, CDN cache purge, and `SchemaRegistry` are already deleted on `main` (`git grep -E 'DifferentialPrivacy|CdnCachePurge|Autheris\.Application\.SchemaRegistry' -- src/` is empty and `src/Autheris.Application/SchemaRegistry` does not exist). Do NOT re-dispatch; only note that `#504` (`WP-OI-CQ-1`, Brief 5A below) locks the security-test inventory before `WP-SIMPL-31` deletes SQLite security tests.
2. **`WP-SIMPL-30` (`WP-SIMPL-30-auth-extensions-module.md`):** **CANCELLED** per owner decisions `OD-21, OD-24, OD-27`.
3. **Wave 2 / Wave 3 Landings & In-Flight Items:**
   - `#612` (`WP-OI-CAT-7`): **MERGED on `main` (`173703d6`)**.
   - `#670` (`WP-OI-CAT-3`): Completed on `feat/oi-cat-670-revoke-auth` (`5bfa220a`).
   - `#478-A` (`WP-OI-SQL-478-A`): Completed on `feat/oi-sql-478a-trino-chains` (`6ccbd417`); `#478-B` (`WP-OI-SQL-478-B` OData `ChainNode`) in flight with `@dev-auth` in `/work/wt/oi-sql-478b-odata-chains`.
   - `#675 PR 1` (`WP-OI-SCALE-4 PR 1`): Completed on `feat/oi-scale-675-pr1-coalescing` (`9b0e8de8`).
   - `#573` (`WP-SIMPL-53 PR 4`): In flight with `@dev-health` in `/work/wt/simpl-53-pr4-gql-index-hints` (`f45f7104`). No further `WP-SIMPL-53` PRs remain after PR 4 merges.
4. **Open GitHub PRs (`#734`, `#744`, `#747`, `#748`) Interactions:**
   - **PR `#734` (`#624` relevance benchmark)** & **PR `#744` (`#669` SQL Server synonyms test)**: Ready to merge cleanly onto `main` (`0` conflicts). Merge `#734` before Brief 2 (`#625` / `#626`) so Brief 2 flips `needs="field-weights"` and `needs="partial-words"` in `relevance-cases.json`.
   - **PR `#747` (`#668` WebSQL mapped connection)**: Draft, clean `git merge-tree`; touches `GovernedSqlRewriter.cs` (+27 lines) and `DataSourceAllowlistGuard.cs`. Sequence before or rebase around Wave 3 `WP-SIMPL-45 PR 4B`.
   - **PR `#748` (`#666` dbt physical table name)**: Draft; **CONFLICTS with `main`** on `tests/Autheris.Tests.Architecture/Golden/GovernanceSteps/checksums.{postgresql,sqlserver}.txt` and collides on migration step `0012_dbt_unique_id` (vs `main`'s `0012_scim_external_identity_and_group_links`, Wave 3's `0013_catalog_discovery`, Wave 4's `0014_consent_api`, and Wave 5's `0015_policy_epochs_change_version`). Must renumber its migration step to **`0016_dbt_unique_id`** (single DDL slot in Wave 6, before `WP-SIMPL-52` starts at `0017+`) and regenerate checksums before undrafting.

---

## Wave 6 Conflict & Single-DDL-Slot Serialization Matrix

| Slot | Work Package | Branch / Worktree | DDL Step | Shared-File & Sequencing Rules (`<= 400` net `src/` lines per PR) |
| :--- | :--- | :--- | :--- | :--- |
| **W6-0 (DDL)** | **`PR #748 Rebase (#666)`** (`WP-OI-CAT-1` dbt physical table name & `0016_dbt_unique_id`) | `origin/pr-748` (`/work/wt/oi-cat-666-dbt-id`) | **`0016_dbt_unique_id`** *(Single DDL slot in Wave 6; runs after Wave 5 `0015_policy_epochs_change_version` and Wave 4 `#673` merge)* | Touches `Migrations/Steps/{postgresql,sqlserver}/0016_dbt_unique_id*`, `DbtMetadataIngestionService.cs`. Must merge before `WP-SIMPL-52 PR 1a` (`0017+`). |
| **W6-1A & 1B** | **`WP-OI-SQL-479-D`** (`substr`/`strpos`) then **`WP-OI-SQL-479-C`** (`LIKE` explicit `ESCAPE`) | `feat/oi-sql-479d-substr-strpos` -> `feat/oi-sql-479c-like-escape` | None *(Starts after Wave 5 `WP-OI-SQL-479-B` merges)* | Touches `src/TrinoSqlEngine/**` (`SqlServerDialectGenerator.cs`, `PostgreSqlDialectGenerator.cs`, `AstSecurityVisitor.Expressions.cs`, `SqlDialectGeneratorBase.Expressions.cs`). Run `479-D` then `479-C` serially, and **before** `W6-6` (`WP-SIMPL-31`). |
| **W6-2A & 2B** | **`WP-OI-SRCH-625`** (BM25F 7-field weights) then **`WP-OI-SRCH-626`** (Compound partial-word scan) | `feat/oi-srch-625-bm25f` -> `feat/oi-srch-626-compound-words` | None *(Starts after PR `#734` `#624` and Wave 5 `#676` merge)* | Both touch `src/Autheris.Application/Catalog/Search/{Bm25SearchIndex.cs,CatalogSearchEngine.cs,SmartSchemaTokenizer.cs}`: run `625` first, then `626`. |
| **W6-3A & 3B** | **`WP-OI-WEB-681`** (Signed keyset paging) & **`WP-OI-WEB-682-B`** (`describe_dataset(graphQlField)`) | `feat/oi-web-681-discovery-paging` -> `feat/oi-web-682b-graphql-field` | None *(Starts after Wave 4 `WP-SIMPL-41 PR 3` and Wave 5 `#677 PR 1` merge)* | Touch `src/Autheris.Application/Catalog/Discovery/**`, `src/Autheris.Application/Mcp/Services/{McpDatasetCatalog.cs,McpDatasetTools.cs}`, `src/Autheris.GraphQL/Mcp/CatalogGraphQlMap.cs`. |
| **W6-4A & 4B** | **`WP-OI-SCALE-7 (#678 PR 1–2)`** (Bounded caches + batched epochs) & **`WP-OI-SCALE-8 (#679 PR 1)`** (Single `CatalogSchemaModel` build) | `feat/oi-scale-678-bounded-caches` & `feat/oi-scale-679-gql-schema-build` | None *(Starts after Wave 5 `#675 PR 2`, `#676`, and `#677 PR 1` merge)* | `#678` touches `DataCatalogOptions.cs`, `GovernedCatalogProvider.cs`, `TenantCatalogLookup.cs`, `GovernanceRepository.Catalog.cs`. `#679` touches `CatalogGraphQlTypeModule.cs` & `CatalogSchemaModel.cs` (can run in parallel with `#678`!). |
| **W6-5A & 5B** | **`WP-OI-CQ-1 (#504)`** (Security trait sweep) & **`WP-OI-CQ-2a/2b (#613)`** (`CA1307/CA1309/CA1310` errors in `src/`) | `feat/oi-cq-504-security-traits` & `feat/oi-cq-613-2a-ca1310` -> `feat/oi-cq-613-2b-ca1307` | None *(Run `#504` BEFORE `W6-6` `WP-SIMPL-31` deletions; run `#613` between feature waves to avoid merge churn)* | `#504` is tests-only (`0` `src/` lines). `#613-2a` & `2b` touch `Directory.Build.props`, `.editorconfig`, and string comparison call sites across `src/`. |
| **W6-6A & 6B** | **`WP-SIMPL-31 PR 1 & PR 2`** (Full SQLite Removal Step 3) | `feat/simpl-31-pr1-engine-sqlite-removal` -> `feat/simpl-31-pr2-host-sqlite-removal` | None *(Starts AFTER `#504` and all `#479-A..D` `TrinoSqlEngine` PRs merge)* | Deletes `SqliteDialectGenerator.cs`, `SqliteTypeAffinity.cs`, `TargetSqlDialect.Sqlite`, `DatabaseDialect.Sqlite`, `SqliteZoneOffsetTable.cs`, `SqliteDialectProvider`, and `Microsoft.Data.Sqlite` refs. |
| **W6-7A & 7B** | **`WP-SIMPL-47 PR 2`** (`IGrantWriter` & Signed `GRANT_RECORDS`) & **`WP-SIMPL-45 PR 6`** (Discovery `DiscoveryWriteState`) | `feat/simpl-47-pr2-grant-writer` & `feat/simpl-45-pr6-discovery-write-state` | None *(Starts after Wave 4 `WP-SIMPL-47 PR 1`, Wave 4 `WP-SIMPL-41 PR 3`, and Wave 5 `WP-SIMPL-45 PR 5`)* | `47 PR 2` touches `src/Autheris.Application/Governance/Grants/**` & `GovernanceRepository.ConsentGrants.cs`. `45 PR 6` touches `DiscoveryModels.cs`, `DiscoveryService.cs`, `Query.Catalog.cs`, and docs. |

---

## Brief 1 — `WP-OI-SQL-479-D` & `WP-OI-SQL-479-C`: Dialect Semantics PR D (`substr`/`strpos`) and PR C (User `LIKE` Explicit `ESCAPE`)

### Slice 6-1A — `WP-OI-SQL-479-D`: SQL Server & PostgreSQL `substr` (`start <= 0`) and SQL Server `strpos` (`COLLATE Latin1_General_BIN2`)
- **Worktree:** `/work/wt/oi-sql-479d-substr-strpos`
- **Branch:** `feat/oi-sql-479d-substr-strpos` (branch from `origin/main` after Wave 5 `WP-OI-SQL-479-B` merges; `~120` net `src/` lines)
- **Plan & Security References:**
  - `docs/plans/2026-11-13-plan-issues-sql-engine-dialects.md` (`#479` PR D, lines 54, 61–63)
  - `docs/plans/2026-11-13-sec-issues-group-5.md` (`SEC-OI-479-1` non-duplicating operand rule & `4 * MaxQueryLength` ceiling)
- **Exact Changes (`src/TrinoSqlEngine/Ast/Generators/SqlServerDialectGenerator.cs:331-352` [`693` lines] & `src/TrinoSqlEngine/Ast/Generators/PostgreSqlDialectGenerator.cs` [`375` lines]):**
  1. **`substr(str, start[, length])` (`FormatTrinoSubstring`):**
     - In Trino, `substr(s, 0, ...)` returns `''` (empty string), and `substr(s, -k, ...)` counts `k` characters from the end of `s` (`LEN(s) + start + 1` when `LEN(s) + start + 1 >= 1`, else `''`). SQL Server `SUBSTRING(s, 0, len)` starts before index 1 (reducing effective length) and negative start returns empty or shifts length; PostgreSQL `SUBSTR(s, 0, len)` starts at position 0.
     - Implement `FormatTrinoSubstring` in `SqlServerDialectGenerator.cs` and `PostgreSqlDialectGenerator.cs`:
       - When `start` is a positive integer literal (`>= 1`), emit direct `SUBSTRING(str, start, length)` / `SUBSTR(str, start, length)` (fast path, zero expansion).
       - When `start` is literal `0`, emit `''`.
       - When `start` is a negative integer literal (`-k`) and `str` is a simple operand (`ColumnReference`, `LiteralExpression`, `ParameterExpression`), emit the clamped negative-offset `CASE WHEN CHAR_LENGTH(str) + (-k) + 1 >= 1 THEN SUBSTR(str, CHAR_LENGTH(str) + (-k) + 1, ...) ELSE '' END` (PostgreSQL) / `LEN(str)` equivalent (SQL Server), respecting `SEC-OI-479-1` (never duplicate compound AST subtrees).
  2. **SQL Server `strpos(haystack, needle)` (`SqlServerDialectGenerator.cs:345-352`):**
     - Emit `CHARINDEX(({needle}) COLLATE Latin1_General_BIN2, ({haystack}) COLLATE Latin1_General_BIN2)` so `strpos` is case- and accent-sensitive like Trino regardless of database collation.
- **Required Unit Tests (`tests/TrinoSqlEngine.Tests/Ast/DialectGenerators/`):**
  - `SqlServerSubstrStartTests.cs` & `SqlServerStrposBinaryCollationTests.cs`: `Substr_StartZero_EmitsEmptyString`, `Substr_PositiveLiteralStart_EmitsDirectSubstring`, `Substr_NegativeLiteralStart_CountsFromEnd`, `Strpos_SqlServer_EmitsLatin1GeneralBin2Collation`.

### Slice 6-1B — `WP-OI-SQL-479-C`: User `LIKE` Explicit `ESCAPE` Rewriting & Non-Literal Pattern Rejection on SQL Server
- **Worktree:** `/work/wt/oi-sql-479c-like-escape`
- **Branch:** `feat/oi-sql-479c-like-escape` (branch from `origin/main` after Slice 6-1A merges; `~140` net `src/` lines)
- **Plan & Security References:**
  - `docs/plans/2026-11-13-plan-issues-sql-engine-dialects.md` (`#479` PR C, lines 53, 60, 63, 100 `Q-SQL-3`)
  - `docs/plans/2026-11-13-sec-issues-group-5.md` (`SEC-OI-479-3`, lines 32–36)
- **Mandatory Security Conditions (`SEC-OI-479-3` & `Q-SQL-3`):**
  - **Bound Parameter Only (`SQLE-09`):** Rewritten user `LIKE` patterns MUST remain bound parameters (`ParameterExpression`); raw user pattern text is NEVER emitted into SQL text, and `ESCAPE '\'` is registered as a constant fragment (`ConstantFragmentRegistry`).
  - **Single-Pass Escaping & Length Ceiling:** Escape the escape character `\` first, then dialect metacharacters (`[` on SQL Server), in a single pass so re-compilation cannot double-escape. Enforce `rewrittenPattern.Length <= MaxQueryLength` (reject with `SqlLimitExceededException` if escaping pushes the pattern over the limit).
  - **User-Supplied `ESCAPE` Validation:** If the user query supplies `ESCAPE c`, `c` MUST be a 1-character printable ASCII literal excluding `%` and `_` (any other escape expression or invalid length is rejected with `SqlCompileNotSupportedException`).
  - **SQL Server Non-Literal Pattern Rejection (`Q-SQL-3`):** On `TargetSqlDialect.SqlServer`, if `LikeExpression.Pattern` is not a `LiteralExpression` or bound string literal (e.g. a column or arbitrary expression), throw `SqlCompileNotSupportedException` (because `[...]` character-class divergence cannot be rewritten on non-literal patterns). On PostgreSQL and Databricks, append explicit `ESCAPE ''` (or `ESCAPE '\'` when rewritten) so backslash is not treated as an implicit escape.
- **Exact Changes:**
  - Modify `src/TrinoSqlEngine/Ast/Visitors/AstSecurityVisitor.Expressions.cs:61-91` (`292` lines) and `src/TrinoSqlEngine/Ast/Generators/SqlDialectGeneratorBase.Expressions.cs` (`504` lines).
- **Required Unit Tests (`tests/TrinoSqlEngine.Tests/Ast/DialectGenerators/UserLikeEscapeEmissionTests.cs`, `[Trait("Category","Security")]`):**
  - `UserLikePattern_Metacharacters_Are_Bound_Not_Emitted` (`SEC-OI-479-3`)
  - `SqlServer_NonLiteral_Like_Pattern_Is_Rejected` (`Q-SQL-3`, `SEC-OI-479-3`)
  - `UserLike_InvalidEscapeChar_LengthOrPercentOrUnderscore_Is_Rejected` (`SEC-OI-479-3`)
  - `UserLike_SinglePassEscaping_NeverDoubleEscapes` (`SEC-OI-479-3`)
  - `Policy_Like_Pattern_Escape_Unchanged` (`SQLE-09` regression).

### Constrained Local Verification Commands (Slices 6-1A & 6-1B)
```bash
export MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget
dotnet build tests/TrinoSqlEngine.Tests/TrinoSqlEngine.Tests.csproj -c Release -m:1 -nodeReuse:false
dotnet test tests/TrinoSqlEngine.Tests/TrinoSqlEngine.Tests.csproj -c Release --no-build -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~SqlServerSubstr|FullyQualifiedName~SqlServerStrpos|FullyQualifiedName~UserLikeEscape"
dotnet build tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-dependencies -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~ProgramRatchetTests|FullyQualifiedName~SourceSizeRatchetArchitectureTests|FullyQualifiedName~CleanArchitectureTests|FullyQualifiedName~DocsIntegrityTests|FullyQualifiedName~Plan_id"
```

---

## Brief 2 — `WP-OI-SRCH-625` & `WP-OI-SRCH-626`: Catalog Search BM25F 7-Field Weights & Compound Partial-Word Matching

### Slice 6-2A — `WP-OI-SRCH-625 (#625)`: BM25F 7-Field Weights with Table-Only Scoring for Restricted-Column Callers
- **Worktree:** `/work/wt/oi-srch-625-bm25f`
- **Branch:** `feat/oi-srch-625-bm25f` (branch from `origin/main` after PR `#734` `#624` and Wave 5 `#676` merge; `~150` net `src/` lines)
- **Plan & Security References:**
  - `docs/plans/2026-11-13-plan-issues-catalog-search.md` (`#625`, lines 68–79, `Q-SRCH-2`)
  - `docs/plans/2026-11-13-sec-issues-group-6.md` (`SEC-OI-625-1..4`, lines 37–46, `Q-SEC6-1`)
- **Mandatory Security Conditions (`SEC-OI-625-1..4`):**
  - **`SEC-OI-625-1` (Zero Column-Field Rank Oracle for Restricted Callers):** For any caller that has **any** non-visible (`Deny` or `DiscoveryHidden`) column on a table, that table's BM25F score MUST be computed from the **5 table-level fields only** (`TechnicalName = 10`, `DisplayName = 8`, `TagsAndOwner = 5`, `ShortDescription = 3`, `LongDescription = 2`); `ColumnNames = 2` and `ColumnDescriptions = 1` are excluded, and a table matching only through column fields is dropped for that caller. Only callers with full column visibility on the table include `ColumnNames` and `ColumnDescriptions`.
  - **`SEC-OI-625-2` (Never Index `HIDDEN` Catalog Text):** Columns or tables marked `DiscoveryHidden` (or `CatalogTextVisible == false` once `WP-SIMPL-52 PR 4b` lands) are never indexed in any BM25F field.
  - **`SEC-OI-625-3` (Finite Weights & Zero-Length Guard):** Weights are internal positive finite constants in `CatalogSearchFieldWeights` (no new config option, `Q-SRCH-2`). Guard per-field average length against `0` so an empty field across the corpus scores `0` and never produces `NaN` or `Infinity`.
- **Exact Changes:**
  - Modify `src/Autheris.Application/Catalog/Search/Bm25SearchIndex.cs` (`115` lines) and `src/Autheris.Application/Catalog/Search/CatalogSearchEngine.cs:57-112` (`510` lines).
  - Enable `needs="field-weights"` cases in `tests/Autheris.Tests.Unit/Catalog/Relevance/relevance-cases.json` (from PR `#734`).
- **Required Unit Tests:**
  - `Bm25SearchIndexTests`: `TermInTechnicalName_OutranksTermOnlyInColumnDescriptions`, `FieldWeightConstants_ChangeRanking`, `PerFieldLengthNormalization_LongDescriptionDoesNotDominate`, `ZeroLengthField_DoesNotProduceNaN` (`[Trait("Category","Security")]`, `SEC-OI-625-3`).
  - `CatalogSearchMetamorphicTests` (`[Trait("Category","Security")]`): `RestrictedTable_ScoreStaysNull_AfterBm25F`, `HiddenColumnDescriptionTerm_NoHitForRestrictedCaller`, `HiddenColumnTerm_DoesNotChangeRankForRestrictedCaller` (`SEC-OI-625-1`), `TableMatchingOnlyThroughHiddenColumnField_NotReturned` (`SEC-OI-625-1`).

### Slice 6-2B — `WP-OI-SRCH-626 (#626)`: Partial-Word / German Compound Matching with Bounded Vocabulary Scan
- **Worktree:** `/work/wt/oi-srch-626-compound-words`
- **Branch:** `feat/oi-srch-626-compound-words` (branch from `origin/main` after Slice 6-2A `#625` merges; `~180` net `src/` lines)
- **Plan & Security References:**
  - `docs/plans/2026-11-13-plan-issues-catalog-search.md` (`#626`, lines 81–90, `Q-SRCH-3`)
  - `docs/plans/2026-11-13-sec-issues-group-6.md` (`SEC-OI-626-1..5`, lines 48–63, `Q-SEC6-2`)
- **Mandatory Security Conditions (`SEC-OI-626-1..5`):**
  - **`SEC-OI-626-1` (Deterministic Input & Expansion Caps):** Before scanning the vocabulary, enforce hard constants: query length `<= 256` chars, at most `8` tokens expanded per query, minimum token length `>= 4` chars measured **after** folding, at most `32` expansions per token, and at most `128` expansions per query. Over-cap input is truncated deterministically (never rejected with a distinguishing error, and no `truncated`/`expanded` flag in any response).
  - **`SEC-OI-626-2` & `SEC-OI-626-3` (Sorted Visible Vocabulary & Restricted-Caller Field Rule):** Build the sorted vocabulary array only from indexable (non-hidden) text, order expansions by `StringComparer.Ordinal` before applying the `32`-cap, weight partial matches by factor `0.4` (capped below whole-word matches of the same term), enforce `SEC-OI-625-1` (no column-field contribution for restricted-column callers), and scrub `MatchedTerms` against the caller's visible text.
  - **`SEC-OI-626-4` & `SEC-OI-626-5` (Culture-Invariant Ordinal Folding & 5 ms p95 Budget):** All folding and substring checks use `StringComparison.Ordinal` after `SmartSchemaTokenizer` normalization (verify under `tr-TR`), and p95 per query token at 50k tables (`CatalogScaleFixture`) stays `<= 5 ms`.
- **Exact Changes:**
  - Modify `src/Autheris.Application/Catalog/Search/Bm25SearchIndex.cs` and `src/Autheris.Application/Catalog/Search/SmartSchemaTokenizer.cs` (`85` lines).
  - Enable `needs="partial-words"` cases in `tests/Autheris.Tests.Unit/Catalog/Relevance/relevance-cases.json`.
- **Required Unit Tests:**
  - `Bm25SearchIndexTests`: `Betriebsstunden_FindsMotorbetriebsstunden`, `Kran_FindsKranspezifikationen`, `WholeWord_OutranksPartialMatch`, `TokenShorterThan4_MatchesWholeWordsOnly`, `ExpansionCap_BoundsCandidateTerms`, `QueryWith1000Tokens_IsBoundedAndFast` (`[Trait("Category","Security")]`, `SEC-OI-626-1`), `Expansion_UsesTurkishCultureInvariantFolding` (`[Trait("Category","Security")]`, `SEC-OI-626-4`).
  - `CatalogSearchMetamorphicTests` (`[Trait("Category","Security")]`): `PartialMatchOnHiddenColumnText_NoHitForRestrictedCaller`, `PartialMatch_RestrictedTable_ScoreStaysNull`, `HiddenTokenCannotCrowdOutVisibleExpansion` (`SEC-OI-626-2`), `PartialMatch_ThroughRestrictedColumnField_NotReturned` (`SEC-OI-626-3`).

### Constrained Local Verification Commands (Slices 6-2A & 6-2B)
```bash
export MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget
dotnet build tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~Bm25SearchIndexTests|FullyQualifiedName~CatalogSearchMetamorphicTests|FullyQualifiedName~CatalogSearchRelevanceTests"
dotnet build tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-dependencies -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~ProgramRatchetTests|FullyQualifiedName~SourceSizeRatchetArchitectureTests|FullyQualifiedName~CleanArchitectureTests|FullyQualifiedName~DocsIntegrityTests|FullyQualifiedName~Plan_id"
```

---

## Brief 3 — `WP-OI-WEB-681` & `WP-OI-WEB-682-B`: Signed Keyset Discovery Paging & `describe_dataset(graphQlField)`

### Slice 6-3A — `WP-OI-WEB-681 (#681)`: Signed Keyset Cursor & Per-Page `DecideManyAsync` in Discovery / MCP `list_datasets`
- **Worktree:** `/work/wt/oi-web-681-discovery-paging`
- **Branch:** `feat/oi-web-681-discovery-paging` (branch from `origin/main` after Wave 4 `WP-SIMPL-41 PR 3` and Wave 5 `#677 PR 1` merge; `~250` net `src/` lines)
- **Plan & Security References:**
  - `docs/plans/2026-11-13-plan-issues-websql-mcp.md` (`#681`, lines 66–89, `Q-WEB-3`)
  - `docs/plans/2026-11-13-sec-issues-group-4.md` (`SEC-OI-681-1..5`, lines 24–32, `Q-SEC4-1..2`)
- **Mandatory Security Conditions (`SEC-OI-681-1..5`):**
  - **`SEC-OI-681-1` (HMAC-Signed Bound Cursor):** Sign `{afterId, filterHash, subjectId, tenantId, catalogEpoch, expiresAtUtc (15 min)}` with the keyset/data-protection HMAC key (`<= 512` bytes; fails startup outside Development if absent, matching `SEC-OI-487`). Any tampered signature, expired timestamp, filter mismatch, subject mismatch, or tenant mismatch returns `InvalidArgument` with a fixed message (never restarts from page 1). Compare `afterId` strictly by ordinal position (`id > afterId`); never look up `afterId` in the catalog (so a hidden or unknown `afterId` behaves identically).
  - **`SEC-OI-681-2` & `SEC-OI-681-3` (Slice Candidate Page After Visibility, Then `DecideManyAsync` Per Page Only):** Cut the page (`limit <= 100`, legacy `offset <= 10_000`) over the visible candidate list (`DiscoveryTenantScope` + `DiscoveryHidden`), then invoke `IAccessDecisionPoint.DecideManyAsync` for **only** the page objects (`<= 100`). Advance the next cursor to the last **candidate** ID of the page (so thinned pages still advance). Derive `total`/`hasMore` from the visible candidate list only.
  - **`SEC-OI-681-4` & `SEC-OI-681-5` (Candidate Cache Key Includes `grantEpoch`):** If caching the scoped candidate ID list across requests, key by `(tenantId, catalogEpoch, grantEpoch, subjectId, scopeKey, claimsHash, filterHash)` with max `1,000` entries and `30 s` backstop TTL, storing `TableIdentifier`s only; bypass cache on epoch-store failure.
- **Exact Changes:**
  - Modify `src/Autheris.Application/Catalog/Discovery/DiscoveryService.cs` (created in Wave 4 Brief 2), `src/Autheris.Application/Mcp/Services/McpDatasetCatalog.cs` (`1306` lines), and `src/Autheris.Application/Mcp/Services/McpDatasetTools.cs`.
- **Required Unit Tests (`tests/Autheris.Tests.Unit/Catalog/Discovery/DiscoveryPagingTests.cs`, `[Trait("Category","Security")]`):**
  - `ListPage_DecidesOnlyPageObjects`, `ListPage_ConsentRowsRead_BoundedByPageSize`, `Cursor_FullListing_VisitsEachDatasetOnce`, `Cursor_FilterChanged_Rejected`, `Cursor_Tampered_Or_Expired_InvalidArgument_NoRestart` (`SEC-OI-681-1`), `Cursor_OtherSubject_Or_OtherTenant_InvalidArgument` (`SEC-OI-681-1`), `Cursor_HiddenOrUnknownAfterId_BehavesIdentically` (`SEC-OI-681-1`), `Page_ThinnedByDecisions_StillAdvancesCursor` (`SEC-OI-681-2`), `Counts_DoNotIncludeHiddenOrDeniedObjects` (`SEC-OI-681-3`), `Limit_Above_Max_IsClamped_And_Offset_Capped` (`SEC-OI-681-3`), `CandidateCache_GrantRevoked_DisappearsOnNextPage` (`SEC-OI-681-4`), `CandidateCache_EpochStoreDown_BypassesCache` (`SEC-OI-681-4`), `DecideMany_MissingDecision_IsDenied` (`SEC-OI-681-2`).

### Slice 6-3B — `WP-OI-WEB-682-B (#682-B)`: `describe_dataset` & `describe_my_access` Lookup by `graphQlField`
- **Worktree:** `/work/wt/oi-web-682b-graphql-field`
- **Branch:** `feat/oi-web-682b-graphql-field` (branch from `origin/main` after Slice 6-3A merges; `~120` net `src/` lines)
- **Plan & Security References:**
  - `docs/plans/2026-11-13-plan-issues-websql-mcp.md` (`#682` B, lines 90–109, `Q-WEB-4`)
  - `docs/plans/2026-11-13-sec-issues-group-4.md` (`SEC-OI-682-1..4`, lines 34–42)
- **Mandatory Security Conditions (`SEC-OI-682-1..4`):**
  - **`SEC-OI-682-1` (Strict Input Validation):** Validate `graphQlField` before lookup: `1..256` chars, `[A-Za-z0-9_]` only, zero control chars; require **exactly one** of `dataset` or `graphQlField` (both or neither returns `InvalidArgument` with a fixed message). Case-sensitive (`StringComparison.Ordinal`) exact match.
  - **`SEC-OI-682-2` & `SEC-OI-682-4` (Same Code Path & Byte-Identical Not-Found for Unknown, Hidden, and Other-Tenant Tables):** Convert an unresolved `graphQlField` in `CatalogGraphQlMap.ResolveRootFieldAsync` (`src/Autheris.GraphQL/Mcp/CatalogGraphQlMap.cs:31-386`) into a sentinel `TableIdentifier("__unresolved__", "__unresolved__", "__unresolved__")` and pass it through the exact same `FindVisibleAsync` / `IDiscoveryService.GetAsync` code path so unknown fields, hidden tables, and other-tenant tables execute the same path and return byte-identical not-found responses without echoing the field name or resolved ID in errors or `Information+` logs.
  - **`SEC-OI-682-3` (`datasetGraphQlField` on Visible Relation Targets Only):** In `graphQl.relations[]`, emit `datasetGraphQlField` (resolved via `IGraphQlCatalogMap.GetRootFieldAsync`) ONLY for targets in `VisibleRelationTargetsAsync`; for invisible targets the property is omitted so the serialized relation shape is unchanged.
- **Exact Changes:**
  - Modify `src/Autheris.GraphQL/Mcp/CatalogGraphQlMap.cs` (`386` lines), `src/Autheris.Application/Mcp/Services/McpDatasetCatalog.cs:933-1210` (`1306` lines), and `src/Autheris.Application/Mcp/Services/McpDatasetTools.cs`.
- **Required Unit Tests (`tests/Autheris.Tests.Unit/Mcp/DescribeByGraphQlFieldSecurityTests.cs`, `[Trait("Category","Security")]`):**
  - `DescribeDataset_ByGraphQlField_ReturnsSameAsById`, `DescribeMyAccess_ByGraphQlField_ReturnsSameAsById`, `Describe_BothOrNoneOfDatasetAndGraphQlField_InvalidArgument`, `Describe_GraphQlField_CaseMismatch_NotFound`, `DescribeByGraphQlField_InvalidCharsOrLength_InvalidArgument` (`SEC-OI-682-1`), `DescribeByGraphQlField_UnknownHiddenOtherTenant_SameCodePath` (`SEC-OI-682-2`), `DescribeByGraphQlField_ErrorDoesNotEchoField` (`SEC-OI-682-2`), `Relations_InvisibleTarget_PropertyAbsent_ShapeUnchanged` (`SEC-OI-682-3`), `GraphQlFieldMap_CollisionOmittedTable_NotResolvable` (`SEC-OI-682-4`).

### Constrained Local Verification Commands (Slices 6-3A & 6-3B)
```bash
export MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget
dotnet build tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~DiscoveryPagingTests|FullyQualifiedName~DescribeByGraphQlField|FullyQualifiedName~CatalogGraphQlMapTests"
dotnet build tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-dependencies -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~ProgramRatchetTests|FullyQualifiedName~SourceSizeRatchetArchitectureTests|FullyQualifiedName~CleanArchitectureTests|FullyQualifiedName~DocsIntegrityTests|FullyQualifiedName~Plan_id"
```

---

## Brief 4 — `WP-OI-SCALE-7 (#678)` & `WP-OI-SCALE-8 (#679)`: Bounded Catalog Caches, Batched Epoch Reads & Single `CatalogSchemaModel` Build

### Slice 6-4A — `WP-OI-SCALE-7 (#678 PR 1–2)`: Bounded Provider/Lookup Caches, Per-Tenant 50% Share Cap & Batched Per-Request Epoch Reads
- **Worktree:** `/work/wt/oi-scale-678-bounded-caches`
- **Branch:** `feat/oi-scale-678-bounded-caches` (branch from `origin/main` after Wave 5 `#675 PR 2`, `#676`, and `#677 PR 1` merge; `~300` net `src/` lines across PR 1+2)
- **Plan & Security References:**
  - `docs/plans/2026-11-13-plan-issues-catalog-scale.md` (`WP-OI-SCALE-7` / `#678`, lines 125–137, `Q-SCALE-1,3,5`)
  - `docs/plans/2026-11-13-sec-issues-group-7.md` (`SEC-OI-678-1..5`, lines 122–139)
- **Mandatory Security Conditions (`SEC-OI-678-1..5`):**
  - **`SEC-OI-678-1` (Never Serve Without Authoritative Epoch Check):** Every cache hit in `GovernanceRepository.Catalog.cs` (`GetTableMetadataAsync` / `GetTablesAsync`) MUST verify the cached entry's epoch against the authoritative `POLICY_EPOCHS` table before returning; batch the epoch read into **one** DB round-trip per request for all tables referenced by a query (`GetTableEpochsAsync(IReadOnlyCollection<TableIdentifier>)`). If the epoch read fails, treat as cache miss / fail closed (never serve potentially stale metadata/masking rules).
  - **`SEC-OI-678-2` (Hard Bounds on Options):** Add `MetadataMaxEntries` (default `20_000`, range `100..100_000`), `MetadataTtlSeconds` (default `30`, range `1..3600`), and `ProviderMaxEntries` (default `10_000`, range `100..50_000`) to `DataCatalogOptions` (`src/Autheris.Domain/Options/Integrations/DataCatalogOptions.cs`). Reject values outside the range at startup (`CatalogCacheOptionsValidator`).
  - **`SEC-OI-678-3` (Per-Tenant 50% Eviction Share Cap):** Replace unbounded `ConcurrentDictionary` in `src/Autheris.Infrastructure/Persistence/Catalog/GovernedCatalogProvider.cs:34` (`315` lines) and `src/Autheris.Application/Catalog/TenantCatalogLookup.cs:31` with `BoundedCache` enforcing a per-tenant share cap of **50%** of `ProviderMaxEntries` so a noisy tenant cannot evict other tenants' entries.
  - **`SEC-OI-678-4` & `SEC-OI-678-5` (Compact Search Index Payloads & Background Warm-Up Readiness):** Ensure `CatalogSearchEngine` / `Bm25SearchIndex` / `VectorSearchIndex` hold only `TableIdentifier`s and compact posting/vector structures (never retaining full `TableMetadata` graphs), and ensure readiness reports unhealthy/not-ready until initial security-critical catalog warm-up succeeds.
- **Required Unit Tests:**
  - `CatalogCacheOptionsValidatorTests.RejectsZeroOrNegativeOrAboveCapLimits` (`[Trait("Category","Security")]`, `SEC-OI-678-2`)
  - `GovernedCatalogProviderCacheTests.ExceedingLimit_EvictsAndRecomputes`
  - `TenantCatalogLookupCacheTests.NoisyTenant_CappedAt50Percent_NeverEvictsOtherTenant` (`[Trait("Category","Security")]`, `SEC-OI-678-3`)
  - `TableEpochBatchReadTests.QueryOverFiveTables_OneEpochRoundTrip` (`SEC-OI-678-1`)
  - `CatalogCacheEvictionSecurityTests.EvictedEntry_NeverServedWithStaleEpoch` & `StaleEpochAfterRemoteWrite_IsReloaded` (`[Trait("Category","Security")]`, `SEC-OI-678-1`).

### Slice 6-4B — `WP-OI-SCALE-8 (#679 PR 1)`: GraphQL `CatalogSchemaModel` Single Build Per Structure Version & Epoch Fingerprint Cache
- **Worktree:** `/work/wt/oi-scale-679-gql-schema-build`
- **Branch:** `feat/oi-scale-679-gql-schema-build` (can run in parallel with Slice 6-4A; `~150` net `src/` lines)
- **Plan & Security References:**
  - `docs/plans/2026-11-13-plan-issues-catalog-scale.md` (`WP-OI-SCALE-8` / `#679`, lines 138–150, `Q-SCALE-4`)
- **Exact Changes (`src/Autheris.GraphQL/Catalog/CatalogGraphQlTypeModule.cs:192-277` [`859` lines] & `src/Autheris.GraphQL/Catalog/CatalogSchemaModel.cs:172` [`590` lines]):**
  1. Reuse the `CatalogSchemaModel` built during structure-version fingerprint check (`:217`) directly in `CreateTypesAsync` (`:277`) so a structure version change builds `CatalogSchemaModel` **once** instead of twice.
  2. Cache per-table schema fingerprints keyed by `(TableIdentifier, tableEpoch)` so unchanged tables are not re-hashed on incremental catalog changes; invalidate immediately when `DiscoveryHidden`, transport visibility, or columns change.
  3. Log structured startup build progress (`tablesProcessed`, `elapsedMs`) every `5 s` via injected `TimeProvider`.
- **Required Unit Tests (`tests/Autheris.Tests.Unit/GraphQL/CatalogGraphQlTypeModuleScaleTests.cs`):**
  - `StructureChange_BuildsModelOnce`, `UnchangedTables_NotReHashed`, `StartupBuild_LogsProgress`, and `[Trait("Category","Security")]` `HiddenTableAfterChange_RootFieldRemoved` & `TransportVisibilityChange_ForcesFullRebuild`.

### Constrained Local Verification Commands (Slices 6-4A & 6-4B)
```bash
export MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget
dotnet build tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~CatalogCache|FullyQualifiedName~GovernedCatalogProviderCache|FullyQualifiedName~TenantCatalogLookupCache|FullyQualifiedName~TableEpochBatchRead|FullyQualifiedName~CatalogGraphQlTypeModule"
dotnet build tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-dependencies -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~ProgramRatchetTests|FullyQualifiedName~SourceSizeRatchetArchitectureTests|FullyQualifiedName~CleanArchitectureTests|FullyQualifiedName~DocsIntegrityTests|FullyQualifiedName~Plan_id"
```

---

## Brief 5 — `WP-OI-CQ-1 (#504)` & `WP-OI-CQ-2a/2b (#613)`: Security Trait Coverage Ratchet & Culture Analyzers (`CA1307/CA1309/CA1310`)

### Slice 6-5A — `WP-OI-CQ-1 (#504)` (+ `WP-SIMPL-36j`): Security Trait Sweep, Name-Set Ratchet & Missing Regression Tests
- **Worktree:** `/work/wt/oi-cq-504-security-traits`
- **Branch:** `feat/oi-cq-504-security-traits` (run **BEFORE** Brief 6 `WP-SIMPL-31` deletes SQLite tests; `0` `src/` lines)
- **Plan & Security References:**
  - `docs/plans/2026-11-13-plan-issues-code-quality.md` (`#504`, lines 13–24, `Q-CQ-1`)
  - `docs/plans/2026-11-13-sec-issues-group-9.md` (`SEC-OI-504-1..5`, lines 15–31, `Q-SEC9-1`)
- **Mandatory Security Conditions (`SEC-OI-504-1..5`):**
  - **`SEC-OI-504-1` & `SEC-OI-504-2` (Class-Level Tagging & Shrink-Only Exemption Baseline + Name-Set Ratchet):**
    - Add class-level `[Trait("Category", "Security")]` to the 4 untagged classes (`tests/Autheris.Tests.Unit/Security/AccessPlanningTenantBindingTests.cs`, `tests/Autheris.Tests.Unit/Security/ApproveConsentRequestFourEyesMutationTests.cs`, `tests/Autheris.Tests.Unit/Identity/IdentityDeprovisioningServiceTests.cs`, `tests/Autheris.Tests.Unit/Jobs/AsyncJobWorkerConcurrencyTests.cs`) and all untagged `*Security*Tests.cs` / `**/Security/*Tests.cs` classes.
    - Add `tests/Autheris.Tests.Architecture/SecurityTraitCoverageTests.cs` verifying every class under a `.Security` namespace segment or named `*Security*Tests` carries `[Trait("Category", "Security")]` at class or method level, with a shrink-only exemption file `tests/Autheris.Tests.Architecture/Baselines/security-trait-exemptions.txt` and a committed security-test name-set baseline (`docs/plans/simplification/security-test-inventory.txt`, `SEC-OI-504-3`) where removals require an issue-linked entry in `tests/Autheris.Tests.Architecture/Baselines/removed-security-tests.txt`.
  - **`SEC-OI-504-4` & `SEC-OI-504-5` (Missing Regression Tests with Specific Denial, Zero Side Effect & Positive Control):**
    - Verify and add any still-missing regression tests from `#504`: `GraphQlBatchingSecurityTests.Batched_request_is_rejected`, `AsyncQueryJobScratchQuotaTests.Quota_is_counted_per_tenant_not_globally` (+ positive control), `AnalyticsEndpointsSecurityTests.Missing_table_returns_403_not_404`, `CdcWebhookDeliveryServiceTests.Drops_events_for_locked_inactive_and_foreign_realm_subjects` (+ `Allowed_subject_is_delivered_positive_control`), `GatewayStartupValidatorTests.Rejects_empty_entra_client_allowlist` / `Rejects_appid_only_v1_token` / `Kerberos_branch_fails_without_keytab`, `DbtSemanticManifestIngestionServiceTests.Standing_requires_table_ownership_not_any_data_owner_role`, `HitLStepUpApprovalServiceTests.Enrolled_secret_without_code_is_denied_when_totp_not_required` (+ `Valid_code_is_approved_positive_control`), `CsvExportTests.Header_cells_are_escaped`.

### Slice 6-5B — `WP-OI-CQ-2a & 2b (#613)`: Enable `CA1310` (`2a`) and `CA1307`/`CA1309` (`2b`) as Errors in `src/**`
- **Worktree:** `/work/wt/oi-cq-613-culture-analyzers`
- **Branch:** `feat/oi-cq-613-2a-ca1310` -> `feat/oi-cq-613-2b-ca1307` (schedule between feature waves; `~0` net `src/` lines — in-place replacements)
- **Plan & Security References:**
  - `docs/plans/2026-11-13-plan-issues-code-quality.md` (`#613`, lines 25–36, `Q-CQ-2..3`)
  - `docs/plans/2026-11-13-sec-issues-group-9.md` (`SEC-OI-613-1..5`, lines 33–46, `Q-SEC9-2`)
- **Mandatory Security Conditions (`SEC-OI-613-1..5`):**
  - **PR 2a (`CA1310`):** Remove `CA1310` from `<NoWarn>` in `Directory.Build.props:17` (`24` lines) and set `[src/**/*.cs] dotnet_diagnostic.CA1310.severity = error` in `.editorconfig` (`20` lines). Fix all `StartsWith`/`EndsWith`/`CompareTo`/`string.Compare`/`IndexOf` sites in `src/`:
    - Default to `StringComparison.Ordinal` (`string.CompareOrdinal` for `CompareTo`).
    - **Allow-Decision Rule (`SEC-OI-613-1`, `Q-SEC9-2`):** An allow decision (role, scope, tenant ID, SID, header allowlist, SQL keyword allowlist) MUST NEVER become more permissive (never move from case-sensitive to case-insensitive); a deny decision MUST NEVER move from case-insensitive to case-sensitive. Include the per-site decision table in the PR description.
    - Add `tests/Autheris.Tests.Unit/Security/CultureInvariantComparisonSecurityTests.cs` (`[Trait("Category","Security")]`) running under `CultureInfo.GetCultureInfo("tr-TR")` and `CultureInfo.InvariantCulture`: `Role_check_is_not_affected_by_turkish_i`, `Sid_prefix_check_is_ordinal`, `Header_name_match_is_culture_invariant`, `Sql_keyword_guard_is_culture_invariant` (including dotless `ı` `"ınsert"` and soft-hyphen `"\u00ADadmin"`), `Scope_and_tenant_checks_same_in_tr_TR_and_invariant`, `Soft_hyphen_does_not_match_role`, `Changed_sites_keep_allow_decision_case_sensitivity`.
  - **PR 2b (`CA1307` & `CA1309`):** Remove `CA1307` from `Directory.Build.props:17` and set `[src/**/*.cs] dotnet_diagnostic.CA1307.severity = error` and `dotnet_diagnostic.CA1309.severity = error` in `.editorconfig`. Pass `StringComparison.Ordinal` explicitly on `Contains`/`Replace`/`IndexOf(string)` (`SEC-OI-613-5`). Add `AnalyzerConfigurationTests.Culture_analyzers_are_errors_in_src` and `Culture_pragmas_only_in_allowlisted_files` (`SEC-OI-613-4`).

### Constrained Local Verification Commands (Slices 6-5A & 6-5B)
```bash
export MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget
dotnet build tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~CultureInvariantComparisonSecurityTests|FullyQualifiedName~GraphQlBatchingSecurityTests|FullyQualifiedName~AsyncQueryJobScratchQuotaTests|FullyQualifiedName~HitLStepUpApprovalServiceTests"
dotnet build tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-dependencies -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~SecurityTraitCoverageTests|FullyQualifiedName~AnalyzerConfigurationTests|FullyQualifiedName~ProgramRatchetTests|FullyQualifiedName~SourceSizeRatchetArchitectureTests|FullyQualifiedName~CleanArchitectureTests|FullyQualifiedName~DocsIntegrityTests|FullyQualifiedName~Plan_id"
```

---

## Brief 6 — `WP-SIMPL-31 PR 1 & PR 2`: Full SQLite Removal (Step 3)

- **Sequencing Rule:** Must run **AFTER** Brief 1 (`#479-C` & `#479-D`) and Brief 5A (`#504` security test inventory) merge on `main`. Never run in parallel with any PR touching `src/TrinoSqlEngine/**`.
- **Plan & Security References:**
  - `docs/plans/simplification/WP-SIMPL-31-full-sqlite-removal-step-3.md` (`SEC-SIMPL-53`, `SEC-SIMPL-55..57`, `SEC-SIMPL-38`, `SEC-SIMPL-17`, `SEC-SIMPL-02`, `SEC-SIMPL-03`, `SEC-SIMPL-50`)

### Slice 6-6A — `WP-SIMPL-31 PR 1`: `TrinoSqlEngine` SQLite Removal (`TargetSqlDialect.Sqlite`, `SqliteDialectGenerator.cs`, `SqliteTypeAffinity.cs`)
- **Worktree:** `/work/wt/simpl-31-pr1-engine-sqlite-removal`
- **Branch:** `feat/simpl-31-pr1-engine-sqlite-removal`
- **Exact Changes (`~+60 / -600` `src/` lines):**
  1. Remove `Sqlite` from `src/TrinoSqlEngine/TargetSqlDialect.cs` (`13` lines; exact remaining set: `{ Ansi, PostgreSql, SqlServer, DuckDb, Oracle, Databricks }`).
  2. Delete `src/TrinoSqlEngine/Ast/Generators/SqliteDialectGenerator.cs` (`441` lines) and `src/TrinoSqlEngine/Ast/Security/SqliteTypeAffinity.cs` (`40` lines).
  3. Remove the SQLite dispatch (`:234`) and `TryResolveSqlite` (`:277-302`) from `src/TrinoSqlEngine/Ast/Security/CatalogTypeMap.cs` (`306` lines) and remove SQLite switch arms across `src/TrinoSqlEngine/**` (keeping fail-closed `default => throw new NotSupportedException(...)` arms).
  4. Delete `tests/TrinoSqlEngine.Tests/Ast/DialectGenerators/SqliteDialectTests.cs`, `tests/TrinoSqlEngine.Tests/Compiler/Sqlite*.cs`, and `tests/TrinoSqlEngine.Tests/Conformance/Fixtures/Sqlite.json`, recording every deleted `[Trait("Category","Security")]` test in `tests/Autheris.Tests.Architecture/Baselines/removed-security-tests.txt` with reason `"feature deleted, WP-SIMPL-31"` (`SEC-SIMPL-03`).
  5. Add `TargetSqlDialectTests.Only_supported_values_exist` and `CatalogTypeMapTests.Unsupported_dialect_resolution_fails_closed` (`[Trait("Category","Security")]`) in `tests/TrinoSqlEngine.Tests/`.

### Slice 6-6B — `WP-SIMPL-31 PR 2`: Host/Domain `DatabaseDialect.Sqlite`, Zone/Affinity/Provider & `Microsoft.Data.Sqlite` Removal
- **Worktree:** `/work/wt/simpl-31-pr2-host-sqlite-removal`
- **Branch:** `feat/simpl-31-pr2-host-sqlite-removal` (starts after Slice 6-6A merges)
- **Exact Changes (`~+60 / -700` `src/` lines):**
  1. Record the `git grep` proof that no masking/governance path uses `SqliteZoneOffsetTable` or `SqliteTypeAffinity` (`SEC-SIMPL-17`), then delete `src/Autheris.Application/DataSources/Services/SqliteZoneOffsetTable.cs` and `src/Autheris.Infrastructure/Persistence/Catalog/SqliteTypeAffinity.cs` (`30` lines).
  2. Remove `DatabaseDialect.Sqlite`, `"sqlite"`/`"sqlite3"` aliases, and the empty-provider-defaults-to-SQLite fallback in `src/Autheris.Domain/Common/DatabaseDialect.cs` (`216` lines; empty provider now fails resolution fail-closed, `SEC-SIMPL-02`).
  3. Delete `SqliteDialectProvider` in `src/Autheris.Infrastructure/Persistence/Dialects/SqlDialectProviders.cs:95` (`147` lines) and restrict `DataSourceDialectValidator.AdmittedDialects` in `src/Autheris.Api/Extensions/DependencyInjection/DataSourceDialectValidator.cs:11-18` (`56` lines) to `{ "trino", "postgresql", "sqlserver", "oracle", "databricks" }`, failing startup with a typed unsupported-dialect error referencing `OD-2` (without connection string values) when `Provider=Sqlite`/`sqlite3` is configured (`SEC-SIMPL-38`).
  4. Remove `Microsoft.Data.Sqlite` `PackageReference`s from all `.csproj` files and add `PackageBoundaryArchitectureTests.No_Microsoft_Data_Sqlite_reference`, `NoSqliteArchitectureTests.No_sqlite_types_in_src`, and `NoSqliteArchitectureTests.Sqlite_literals_only_in_allowlisted_guards` in `tests/Autheris.Tests.Architecture/`.

### Constrained Local Verification Commands (Slices 6-6A & 6-6B)
```bash
export MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget
dotnet build tests/TrinoSqlEngine.Tests/TrinoSqlEngine.Tests.csproj -c Release -m:1 -nodeReuse:false
dotnet test tests/TrinoSqlEngine.Tests/TrinoSqlEngine.Tests.csproj -c Release --no-build -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~TargetSqlDialectTests|FullyQualifiedName~CatalogTypeMapTests"
dotnet build tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~DatabaseDialectTests|FullyQualifiedName~DataSourceProviderTests|FullyQualifiedName~DataSourceDialectValidator"
dotnet build tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-dependencies -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~NoSqliteArchitectureTests|FullyQualifiedName~PackageBoundaryArchitectureTests|FullyQualifiedName~ProgramRatchetTests|FullyQualifiedName~SourceSizeRatchetArchitectureTests|FullyQualifiedName~CleanArchitectureTests|FullyQualifiedName~DocsIntegrityTests|FullyQualifiedName~Plan_id"
```

---

## Brief 7 — `WP-SIMPL-47 PR 2` (`IGrantWriter` & Signed `GRANT_RECORDS`) and `WP-SIMPL-45 PR 6` (Discovery Write State)

### Slice 6-7A — `WP-SIMPL-47 PR 2`: `IGrantWriter` & ECDSA P-256 Signed, Per-Tenant Hash-Chained `GRANT_RECORDS`
- **Worktree:** `/work/wt/simpl-47-pr2-grant-writer`
- **Branch:** `feat/simpl-47-pr2-grant-writer` (starts after Wave 4 Brief 1 `WP-SIMPL-47 PR 1` `0014_consent_api` merges; `~350` net `src/` lines)
- **Plan & Security References:**
  - `docs/plans/simplification/WP-SIMPL-47-external-catalog-and-consent-api.md` (§5 Step 3, §6a `SEC-SIMPL-47-03`, `SEC-SIMPL-47-09`, §9 PR 2)
- **Mandatory Security Conditions (`SEC-SIMPL-47-03` & `SEC-SIMPL-47-09`):**
  - **Single Grant-Write Funnel (`SEC-SIMPL-47-03`):** Every creation, replacement, revocation, or expiry of a `CONSENTS` row MUST pass through `IGrantWriter` inside the same database transaction as the `GRANT_RECORDS` append and `GRANT_RECORD_HEADS` update (`SELECT ... FOR UPDATE` / `UPDLOCK, HOLDLOCK` per tenant). Enforce with an architecture test `ConsentWrites_OnlyThroughGrantWriter`.
  - **ECDSA P-256 Signature & Hash Chain (`SEC-SIMPL-47-09`):** Compute `record_hash = SHA-256(prev_hash + "|" + CanonicalJson)` using `GrantRecordCanonicalizer`, sign `record_hash` with the configured ECDSA P-256 signing key (`key_id` recorded on the row; startup fails outside Development if signing key is missing), and provide `IGrantChainVerifier.VerifyTenantChainAsync(tenantId, ct)` detecting any modification, deletion, reordering, or signature mismatch.
- **Exact Changes:**
  - Create `src/Autheris.Application/Governance/Grants/IGrantWriter.cs`, `GrantWriter.cs`, `IGrantChainVerifier.cs`, and wire `src/Autheris.Infrastructure/Persistence/GovernanceRepository.ConsentGrants.cs` (`504` lines).
- **Follow-On `WP-SIMPL-47 PR 3..7` Queue (After `PR 2`):**
  - **PR 3:** `IConsentDecisionPipeline` + `GrantValidityPolicy` (`SEC-SIMPL-47-04..06`).
  - **PR 4:** `ConsentWorkflowRoutes` + `ConsentEvent` outbox delivery + `GrantExpiryHostedService` (`SEC-SIMPL-47-07..08`).
  - **PR 5:** External REST `/api/v1/consent/*` endpoints (`SEC-SIMPL-47-01..02`, `SEC-SIMPL-47-10`).
  - **PR 6:** External catalog delta read (`/api/v1/catalog/changes`) + OpenAPI contract (`SEC-SIMPL-47-11`).
  - **PR 7:** Profile assignment per-owner approval (`PROFILE_ASSIGNMENT_ITEMS`), owner direct grants, and `IGrantEffectPreview` (`SEC-SIMPL-47-12`).

### Slice 6-7B — `WP-SIMPL-45 PR 6`: Discovery `DiscoveryWriteState` Integration & `DmlWriterRoles` Documentation Cleanup
- **Worktree:** `/work/wt/simpl-45-pr6-discovery-write-state`
- **Branch:** `feat/simpl-45-pr6-discovery-write-state` (starts after Wave 4 Brief 2 `WP-SIMPL-41 PR 3` and Wave 5 Brief 4 `WP-SIMPL-45 PR 5` merge; `~200` net `src/` lines)
- **Plan & Security References:**
  - `docs/plans/simplification/WP-SIMPL-45-governed-write-access.md` (§5 Step 12 & Step 13 doc updates, §6 `DiscoveryWriteStateTests`, §9 PR 6)
- **Exact Changes:**
  1. Extend `DiscoveryObject` with `DiscoveryWriteState Write` (`CanInsert`, `CanUpdate`, `CanDelete`, `UpdateRowScope`, `InsertColumns`, `UpdateColumns`, `Requestable`) and `DiscoveryColumn` with `bool WriteProtected`, populated in `DiscoveryService` via `IAccessDecisionPoint.DecideManyAsync` for `AccessOperation.Insert | Update | Delete` (zero data source reads, zero duplicate decision logic).
  2. Mirror `Write` and `WriteProtected` on GraphQL `TableMetadataDto` (`src/Autheris.GraphQL/Types/Query.Catalog.cs`, `387` lines).
  3. Remove all remaining references to `DmlWriterRoles` from `docs/configuration-guide.md`, `docs/endpoints-and-testing.md`, `docs/features/f-data-02-governed-websql.md`, and `docs/features/f-dialect-02-ast-sql-compiler.md`.

### Constrained Local Verification Commands (Slices 6-7A & 6-7B)
```bash
export MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget
dotnet build tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~GrantWriter|FullyQualifiedName~GrantChainVerifier|FullyQualifiedName~DiscoveryWriteStateTests"
dotnet build tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-dependencies -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~ConsentWrites|FullyQualifiedName~DocsIntegrityTests|FullyQualifiedName~ProgramRatchetTests|FullyQualifiedName~SourceSizeRatchetArchitectureTests|FullyQualifiedName~CleanArchitectureTests|FullyQualifiedName~Plan_id"
```

---

## Brief 8 — Post-Freeze / Stage 3 Exit & Follow-On Queue (`#492`, `#628b..d`, `WP-SIMPL-52`, `WP-SIMPL-54`)

| Package | Worktree / Branch | Prerequisite / Gate | Exact Target Files & Binding `SEC-*` Conditions |
| :--- | :--- | :--- | :--- |
| **`WP-OI-SQL-492 (#492)`** (Query analysis `targetScope` tier simulation) | `/work/wt/oi-sql-492-target-scope` (`feat/oi-sql-492-target-scope`) | Post-Stage 3 freeze (`Q-SQL-4`, `Q-SQL-5`) | `src/Autheris.Api/Endpoints/QueryAnalysisAndProfilingEndpoints.cs:21-24` (`215` lines), `src/Autheris.Application/Governance/Analysis/QueryAnalysisService.cs` & `.Evaluation.cs`, `src/Autheris.Application/State/Caching/Services/ClientTierResolver.cs` (`351` lines), `src/Autheris.Domain/Options/GovernanceOptions.cs` (`28` lines). Enforces `GovernanceQueryAnalysisOptions.AllowedTargetScopes` (empty = disabled), rejects combining `TargetScope` + `TargetSubjectId`, replaces scope claims ONLY for `ClientTierResolver.ResolveAsync` (caller visibility/masking/row filters unchanged), omits `secondsAtCurrentBucket`, and mutates zero rate-limit buckets (`SEC-OI-492-1..4`). |
| **`WP-OI-SRCH-628b..d (#628b..d)`** (In-process `multilingual-e5-small` ONNX + table-only & per-column vectors) | `/work/wt/oi-srch-628b-onnx` -> `/work/wt/oi-srch-628c-column-vectors` | Post-Stage 3 freeze (`Q-SRCH-4`, `Q-SRCH-5`, `Q-SEC6-3`) + Wave 5 `#676` | `src/Autheris.Application/Catalog/Search/LocalOnnxEmbeddingGenerator.cs` (`130` lines), `CatalogSearchEngine.cs` (`510` lines), `VectorSearchIndex.cs` (`57` lines). **628b:** single-read SHA-256 verification of mounted model + tokenizer files inside canonicalized model dir, custom ops disabled, bounded token length/batch/concurrency (`SEC-OI-628-3..4`). **628c:** table-level vector text contains ONLY table name, display name, and table description (zero column names/descriptions, `SEC-OI-628-5`); per-column vectors built separately and filtered by caller column visibility (`Clear`) strictly **before** RRF fusion and top-k truncation (`SEC-OI-628-6..8`). **628d:** 5k-table p95 `<= 50 ms` latency test. |
| **`WP-SIMPL-52 PR 1a..7`** (Unified Governance Data Model: `ACCESS_POLICIES`, `ACCESS_PROFILES`, `#571` `CatalogTextVisible` in PR 4b) | `/work/wt/simpl-52-pr1a-schema` .. `pr7` | After Wave 5 `0015`, PR `#748` `0016`, and `WP-SIMPL-46/47/48` stabilize | 10 slices (`<= 400` `src/` lines each) per `docs/plans/simplification/WP-SIMPL-52-governance-data-model.md` (§9 PR 1a..7, §14 `SEC-52-01..12`), starting at single DDL slot `0017_unified_access_policies`. PR 4b implements `#571` (`CatalogTextVisible` / `HIDDEN` catalog text rule across discovery and search `SEC-OI-625-2`, `SEC-OI-628-5`). |
| **`WP-SIMPL-54 (#580)`** (Golden Queries from dbt `meta.autheris.golden_queries`) | `/work/wt/simpl-54-dbt-golden-queries` (`feat/simpl-54-dbt-golden-queries`) | Post-Stage 3 freeze (`Q26`, `Q27`) + `WP-SIMPL-41 PR 3` | `src/Autheris.Extensions/Dbt/DbtMetadataIngestionService.cs` (`GoldenQueryParser`), `src/Autheris.Infrastructure/Persistence/GovernanceRepository.Catalog*.cs`, `src/Autheris.Application/Catalog/Discovery/DiscoveryService.cs`, `src/Autheris.Application/Mcp/Services/McpDatasetCatalog.cs`. Enforces `SEC-54-01..07` (`AstSecurityVisitor` SELECT-only validation at ingestion, referenced-table subset of model's dbt `ref`/`source` Closure, caller `DecideManyAsync` check on all `ReferencedTables` before exposing a golden query in discovery/MCP, and canonical `domain.schema.table` dataset IDs). |
