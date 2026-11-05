# Plan: key and index column direction for the default read order (issue #390, part a)

Phase 2 (csharp-architect), 2026-10-30. Branch `feat/issue-390-key-index-direction` from `origin/main` 2ca88d8d (contains the #388 migration framework, PR #402). Triage: [2026-10-29-triage-issues-388-393.md](2026-10-29-triage-issues-388-393.md) section #390. Framework rules: [2026-10-29-plan-issue-388-governance-migrations.md](2026-10-29-plan-issue-388-governance-migrations.md) (D-2 step format, D-5 shape delta, D-9 gates, I388-SC-01..14).

## 1. Problem and scope

A table with a declared key always gets a default `ORDER BY` with every key column ascending (`DefaultReadOrdering.Resolve` returns names only, `DefaultReadOrdering.cs:42-61`; `TreeSqlCompiler.cs:395-398` adds `(column, false)`; `GovernedTableReadPlanner.ResolveOrderBy` `:463-497` appends tie-breakers without direction; `GovernedTableReadPlanner.Keyset.cs:239-240` hard-codes `ASC`). A source index `(device_id ASC, ts DESC)` cannot serve that order, so every unordered read and every offset page is a full sort. The catalog cannot even express the direction: `TableIndexDefinition(Name, Columns, IsUnique)`, `TABLE_PRIMARY_KEY_COLUMNS` and `TABLE_INDEX_COLUMNS` store column order only.

In scope (390a): direction per key/index column in the catalog (governance step 0002), on every declaration path, in the default order, the tie-breaker and the keyset stream, and in `describe_dataset` / catalog detail under the existing visibility rule.
Out of scope, follow-ups: **390b** reading directions (and `is_clustered`) from SQL Server `sys.index_columns.is_descending_key` / PostgreSQL `pg_index.indoption` during sync (new source privilege, own PRD); **390c** non-unique indexes in `describe_dataset` as filter hints. Oracle and other engines get direction only by declaration.

## 2. Design decisions

**D-1 Model (Autheris.Domain, `Model/TableModels.cs`).** New `enum KeyColumnDirection { Ascending = 0, Descending = 1 }`, serialized as `"ASC"`/`"DESC"` (`JsonStringEnumMemberName`). `TableMetadata.PrimaryKeyDirections : IReadOnlyList<KeyColumnDirection>` (init, default empty). `TableIndexDefinition(Name, Columns, IsUnique, IReadOnlyList<KeyColumnDirection>? Directions = null)` with `[JsonIgnore(WhenWritingNull)]`; `TableKeyDefinition` gains `PrimaryKeyDirections` (optional). Parallel lists instead of changing `PrimaryKeyColumns` to a record list: `PrimaryKeyColumns` has dozens of consumers that need names only. **Canonical form:** all-ascending is stored in memory as empty/`null` (so equality, audit JSON and responses of undeclared tables are unchanged); otherwise the list has exactly one entry per column. Helper `KeyDirections.IsDescending(list, i)` (out of range or empty = ascending).

**D-2 Governance step 0002 `0002_key_and_index_column_direction`** (first real step; the default-tenant lane takes 0003, whichever merges second renumbers per #388 section 4). Files `Persistence/Migrations/Steps/<engine>/0002_key_and_index_column_direction.sql` and `.shape.json` for `postgresql`, `sqlserver`, `sqlite`:
- PostgreSQL: `ALTER TABLE TABLE_PRIMARY_KEY_COLUMNS ADD COLUMN IF NOT EXISTS is_descending BOOLEAN NOT NULL DEFAULT FALSE;` and the same for `TABLE_INDEX_COLUMNS` (constant default: metadata-only, no rewrite).
- SQL Server: two batches split by `-- batch`, each `IF COL_LENGTH(N'dbo.<T>', N'is_descending') IS NULL ALTER TABLE dbo.<T> ADD is_descending BIT NOT NULL CONSTRAINT DF_<T>_is_descending DEFAULT 0;` (`DF_TABLE_PRIMARY_KEY_COLUMNS_is_descending`, `DF_TABLE_INDEX_COLUMNS_is_descending`).
- SQLite: header `-- guard: SELECT CASE WHEN (SELECT COUNT(*) FROM pragma_table_info('TABLE_PRIMARY_KEY_COLUMNS') WHERE name = 'is_descending') = 1 AND (SELECT COUNT(*) FROM pragma_table_info('TABLE_INDEX_COLUMNS') WHERE name = 'is_descending') = 1 THEN 1 ELSE 0 END`, then `ALTER TABLE <T> ADD COLUMN is_descending INTEGER NOT NULL DEFAULT 0 CHECK (is_descending IN (0, 1));` for both tables (a half-applied state cannot exist inside the run transaction; if it did, the guard returns 0 and the first ALTER fails closed).
- Shape delta per engine: two `addColumn` ops, table names spelled as in v0001 (PG lower case `table_primary_key_columns`/`table_index_columns`, SQL Server and SQLite upper case), column `{ "name": "is_descending", "type": "BOOLEAN"|"BIT"|"INTEGER", "family": "boolean"|"boolean"|"integer", "nullable": false }`.
- `Golden/GovernanceSteps/checksums.<engine>.txt`: append `0002 key_and_index_column_direction <sha256>` (value produced by the step loader; line 0001 untouched, I388-SC-02).
- Purely additive, no data step, no audit object touched: no `-- phase3:` marker, lint-clean (no `EXEC`, `UPDATE`, trigger or transaction statements). Older binaries keep working (`AllowNewerSchema` rollback): their inserts omit the column and get the default = ascending. `is_clustered` is not added (no consumer in 390a; default taken, goes with 390b).

**D-3 Persistence (`CatalogKeyDefinitionStore`).** `PrimaryKeyRow` and `IndexRow` gain `bool Descending`; `ReadAsync` (:109, :127) and `ReadBatchAsync` (:184, :211) select `is_descending`; `Assemble` builds the canonical direction lists; `ReplaceAsync` (:367, :383) and `ReplaceNarrowAsync` insert `is_descending` (SQLite 0/1). No column probe: startup verify/apply guarantees shape v0002 before reads. A direction-only change is a change: `TableKeyDefinitionChange` carries directions, and the `CatalogKeyDefinitionChanged` audit details (`CatalogApiEndpoints` ~:496) add `primaryKeyDirections` before/after (index directions serialize with the record). `CatalogGovernanceRatchet.Merge` (:142) takes `PrimaryKeyDirections` from the same side as `PrimaryKeyColumns`. `TableKeyDefinitionValidator.Validate` rejects a direction list whose count differs from its column list (existing invalid-definition path) and normalizes all-ascending to empty, keeping directions aligned through column-name normalization (:129).

**D-4 Declaration paths (default ASC = today).**
- `PUT /api/v1/catalog/datasets/{id}/keys`: `SetDatasetKeysDto.PrimaryKeyDirections` and `DatasetIndexDto.Directions` (`IReadOnlyList<string>?`, values `ASC`/`DESC` case-insensitive, applies to `primaryKey` or its alias `primaryKeyColumns`). Unknown value or count mismatch: 400 with the existing invalid-key code and generic message, never echoing the value.
- OpenAPI: sibling extension `x-autheris-primary-key-directions` and `directions` on `x-autheris-indexes` items (`OpenApiIngestionService` ~:425-485).
- Catalog sync (`DataCatalogSyncService` :226-229): asset model `DataCatalogModels` gains `PrimaryKeyDirections`; indexes already carry `TableIndexDefinition`. Plumbing only; connectors fill it when their source has a direction (none today). OpenMetadata constraints have no direction: unchanged.
- dbt (`DbtArtifactStreamingParser.CollectKeyConstraint` :578): a `columns` entry of a `primary_key`/`unique` constraint may end in ` ASC`/` DESC` (dbt renders constraint columns verbatim, and SQL Server accepts `PRIMARY KEY (device_id, ts DESC)`). Shared parser `KeyColumnSpec.TryParse(string token, out string column, out KeyColumnDirection direction)` in `Application/DataCatalog/Services`: trim, split off a final whitespace-separated `ASC`/`DESC` (case-insensitive), the remainder must be non-empty and pass the existing column-name validation; any other shape marks the whole collector invalid (existing drop-with-warning path). dbt `unique` tests stay ascending.
- `CatalogKeyDefinitionImport.Collector.AddPrimaryKey/AddUnique` and `Apply(...)` gain optional direction lists. Column `Meta` markers and the `(client_id, ts)` convention stay ascending.

**D-5 `DefaultReadOrdering`.** `DefaultOrdering(IReadOnlyList<OrderingColumn> Terms, DefaultOrderingKind Kind)` with `readonly record struct OrderingColumn(string Name, bool Descending)` and a computed `Columns` (names) for existing callers (`TableRowIdentity`, tests). `TryKey` returns terms: PK from `PrimaryKeyDirections`, unique index from `index.Directions`, marker/convention ascending. **Candidate choice is independent of direction** (same precedence, same all-or-nothing Clear rule, same "fewest columns" tie-break), so hidden-column behaviour is identical to today. Composite fallback stays ascending.

**D-6 `TreeSqlCompiler` (:395-398).** `order.Add((term.Name, term.Descending))`; the `SafeNameRegex` check stays on names; `DESC` is a fixed literal chosen by a bool, no catalog text enters the SQL except quoted identifiers. Applies to root and nested relation lists. Explicit `orderBy` unchanged.

**D-7 `GovernedTableReadPlanner.ResolveOrderBy` (:463-497).** Default order and tie-breaker emit `quoted + " DESC"` for descending terms. Tie-breaker: key columns already named by the caller are skipped as today (the caller's direction wins); explicit terms are untouched.

**D-8 Keyset stream (`GovernedTableReadPlanner.Keyset.cs`, default taken: same full-sort defect per page).** `ORDER BY` uses the declared PK direction; the seek uses `<` for descending columns (single column :191 and every branch of the OR chain :211). `KeysetCursorContextFactory.ForBulkStream` gets the directions: `Direction` stays `"asc"` when all ascending (existing cursors stay valid) and becomes the signature (e.g. `"asc,desc"`) otherwise, so a cursor issued before a direction change fails the existing context check (fail closed). `TableStreamEndpoints` :221 passes `PrimaryKeyDirections`.

**D-9 Output.** `describe_dataset` (`McpDatasetCatalog` :431-541): new optional `McpDatasetDescription.PrimaryKeyOrder : IReadOnlyList<McpKeyColumn(Column, Direction)>?`, filled from the `TableKeyVisibility.Restrict` view when its PK is non-empty (gives order and direction; existing per-column `primaryKey` flags stay). `Indexes` already serializes the restricted `TableIndexDefinition`, so `directions` appears only for an index with a descending column. Catalog detail (`CatalogDiscoveryService` :168, `CatalogDatasetDetail`): `PrimaryKeyDirections` parallel to `PrimaryKeys`, `null` when all ascending. `TableKeyVisibility.Restrict` drops `PrimaryKeyDirections` together with the key (a key or index touching a hidden column is omitted as a whole; no direction for a partial key). OData/EDM and the dynamic OpenAPI document are unchanged (no direction concept).

**D-10 Semantics.** Only reads without an explicit order, tie-breakers and keyset streams of tables with a declared descending column change row order. Undeclared/ascending tables compile to byte-identical SQL. Filters, row filters, masking and the Clear requirement for ordering columns are unaffected. Release note (lead): "default row order of tables with a declared descending key column follows the declared direction".

## 3. Tests (TDD; `Category=Security` where marked S)

- WP-390-1: `Step0002_KeyDirection_DefaultsExistingRowsToAscending` per dialect (SQLite in `GovernanceSqliteUpgradeTests`, PG/SQL Server via `GovernanceUpgradeTestsBase`: seed v1.5.0 baseline with a 2-column PK and a unique index, run all steps, rows intact, `is_descending` false, history row 0002 with the golden checksum, audit chain and anchor verify); `Step0002_Rerun_IsIdempotent` (guard / `IF NOT EXISTS` / `COL_LENGTH`); `Step0002_InsertWithV1ColumnList_ReadsAscending` (older-binary insert). Existing gates must pass at v2: `ShapeAtEveryVersion_EqualsDatabaseMigratedToThatVersion`, `ReleasedStepChecksums_AreUnchanged`, `EveryStep_HasSqlAndShapeDeltaForAllEngines`, `Catalogues_HaveTheSameStepsForAllProviders`, `StepSql_ContainsNoForbiddenStatements`.
- WP-390-2: `Validator_DirectionCountMismatch_IsInvalid`, `Validator_AllAscending_NormalizesToEmpty`, `KeyDefinition_RoundTripsDirections` (in `CatalogKeyDefinitionContractTestsBase`, three dialects), `ReplaceKeyDefinition_DirectionOnlyChange_ReturnsChangeAndIsAudited`, `Restrict_HiddenKeyColumn_DropsKeyAndDirections` (S), `Ratchet_IncomingWithoutDeclaration_KeepsStoredDirections`.
- WP-390-3: `SetDatasetKeys_WithDirections_PersistsAndAudits`, `SetDatasetKeys_InvalidDirectionOrCountMismatch_Returns400WithoutEchoingValue` (S), `SetDatasetKeys_WithoutDirections_BehavesAsToday`, `KeyColumnSpec_TryParse_Theory` (suffix case, extra tokens, empty name, injection-like tokens rejected) (S), `DbtConstraint_DescSuffix_ImportsDescending`, `DbtConstraint_InvalidToken_DropsDefinition`, `OpenApi_DirectionExtensions_Imported`, `DataCatalogSync_AssetDirections_Imported`.
- WP-390-4: `Resolve_PrimaryKeyWithDesc_ReturnsDirections`, `Resolve_UniqueIndexWithDesc_ReturnsIndexDirections`, `Resolve_HiddenDescColumn_SkipsCandidateAsToday` (S), `Resolve_MarkerAndConventionKeys_StayAscending`; compile goldens in `TreeSqlCompilerDefaultOrderingTests`: `DefaultOrder_DescKey_EmitsADescB` (SQL Server `OFFSET/FETCH`, PostgreSQL `LIMIT`), `NestedRelation_DescKey_EmitsDesc`, `UndeclaredDirection_SqlByteIdentical`, `ExplicitOrderBy_Unchanged`; planner (`GovernedTableReadPlannerTests`): `DefaultOrder_DescKey_EmitsDesc`, `TieBreaker_AppendsKeyWithDeclaredDirection`, `TieBreaker_SkipsColumnNamedByCaller`; keyset (`GovernedTableReadPlannerBulkKeysetStreamTests`): `PlanKeysetStream_DescKeyColumn_SeeksLessThanAndOrdersDesc`, `PlanKeysetStream_CursorFromOtherDirectionSignature_FailsClosed` (S), `PlanKeysetStream_AllAscending_ExistingCursorStillValid`; output: `DescribeDataset_DescKey_ShowsOrderAndDirections_WhenKeyVisible`, `DescribeDataset_HiddenKeyColumn_OmitsKeyOrderAndDirections` (S), `DescribeDataset_IndexTouchingHiddenColumn_Omitted` (S), `CatalogDetail_AscendingKey_PrimaryKeyDirectionsNull`; integration (SQL Server Testcontainer): `GraphQlRead_DescKey_ReturnsNewestFirstPerDevice` (acceptance: key `(device_id ASC, ts DESC)`, read without `orderBy`).

## 4. Work packages and size

| WP | Content | Depends | Size (LOC incl. tests) |
|---|---|---|---|
| WP-390-1 | Step 0002: SQL and shape delta for 3 dialects, checksum golden lines, upgrade tests | - | S (~180) |
| WP-390-2 | D-1, D-3: model, validator, store read/write, audit details, ratchet, `Restrict` | 1 | M (~350) |
| WP-390-3 | D-4: PUT DTO, OpenAPI, catalog sync, dbt `KeyColumnSpec`, collector | 2 | M (~300) |
| WP-390-4 | D-5..D-9: `DefaultReadOrdering`, compiler, planner, keyset, `describe_dataset`/catalog detail | 2 | M (~380) |

WP-390-3 and WP-390-4 can run in parallel after WP-390-2. Total L (~1200 LOC). One build at a time (disk budget).

## 5. Defaults taken

Parallel direction lists instead of a key-column record (D-1); all-ascending canonical as empty (D-1); no `is_clustered` (D-2); dbt direction by column suffix (D-4); keyset stream follows direction (D-8); `PrimaryKeyOrder` emitted in `describe_dataset` whenever the key is visible, index `directions` only when a column is descending (D-9).

## 6. Phase 3 questions

- Q-1 Step 0002 touches no audit object: confirm no `-- phase3:` marker is required and the base-branch append-only gate plus CODEOWNERS on `Migrations/Steps/**` suffice.
- Q-2 Is a visible key's direction safe to expose in `describe_dataset`/catalog detail (physical metadata, only when every key column is visible), and is "direction never influences candidate choice" (D-5) sufficient to keep the hidden-column rule intact?
- Q-3 dbt suffix parsing of untrusted manifests (D-4): grammar, rejection of extra tokens, and drop-the-whole-definition on any invalid token.
- Q-4 Keyset cursor binding by direction signature (D-8): accept that all-ascending cursors stay valid and a changed direction fails closed via the existing context mismatch.
- Q-5 PUT rejection without echo and audit details with directions (D-3, D-4): any further field to redact?
- Q-6 Does the existing key-change invalidation (metadata cache, compiled-plan/result caches) cover a direction-only change, or must a cache key include the directions?

## Phase 3 binding conditions (csharp-security-expert, 2026-10-30)

Verdict: APPROVED WITH CONDITIONS. No existing control is weakened: direction is a bool chosen from a closed enum, `DESC` is a fixed literal, filters/masking/RLS/explicit `orderBy` are not touched.

**Rulings on Q-1..Q-6**
- Q-1 Confirmed: no `-- phase3:` marker (no audit object touched, additive columns on key tables only). Step must still pass the I388-SC-02 base-branch gate and CODEOWNERS.
- Q-2 Confirmed: direction of a fully visible key is physical metadata of the same class as the column list; D-5 (candidate choice independent of direction) keeps the hidden-column rule intact.
- Q-3 Grammar confirmed with I390-SC-05.
- Q-4 Confirmed with I390-SC-07 (all-ascending cursors stay valid, any direction change fails closed).
- Q-5 Confirmed with I390-SC-06.
- Q-6 Not assumed: must be proven, I390-SC-08.

**Binding conditions**
- I390-SC-01 Step 0002 satisfies every I388-SC condition: guard per dialect (PG `IF NOT EXISTS`, SQL Server `COL_LENGTH` per batch, SQLite `-- guard`), shape delta with `addColumn` for both tables, checksum golden line appended only (line 0001 untouched), lint clean (no UPDATE/EXEC/trigger/transaction/DROP), I388-SC-08 timeouts applied by the runner, no data step, no touch of `AUDIT_LOG_ENTRIES` or its trigger (post-conditions of I388-SC-06 still run).
- I390-SC-02 Column is `NOT NULL` with constant default 0/FALSE and (SQLite) `CHECK IN (0,1)`; a PG/SQL Server equivalent CHECK is not required, but the reader must treat any value other than 0/1 as invalid definition (fail closed, key dropped, no ordering by guess).
- I390-SC-03 Upgrade test from the v1.5.0 baseline with seeded data (2-column PK, unique index, audit entries): all rows ascending after step, audit chain and anchor verify, history row 0002 equals the golden checksum, rerun idempotent, v1-column-list insert reads ascending.
- I390-SC-04 Ordering change is confined to default order, tie-breaker and bulk keyset. Filters, row filters, masking, the Clear requirement for ordering columns and explicit `orderBy` must produce byte-identical SQL to today; undeclared/all-ascending tables compile byte-identical SQL.
- I390-SC-05 `KeyColumnSpec.TryParse` grammar: trim; at most one trailing whitespace-separated token, exactly `ASC` or `DESC` (ordinal-ignore-case); remainder must pass the existing column-name validation (so no quotes, brackets, dots, commas, parentheses, `NULLS FIRST/LAST`, `COLLATE`); max token length bounded (<=256) before parsing; any other shape drops the whole definition (never partial, never ascending fallback for an unparsable token) with a warning that logs no manifest text.
- I390-SC-06 PUT and import rejections use the generic message/code and never echo direction values or column text; invalid-direction logs carry counts only. Audit details carry directions as the closed strings `ASC`/`DESC` only (never raw input), added to before/after; an audit entry is written for a direction-only change.
- I390-SC-07 Cursor context for bulk stream binds the full direction signature when any column is descending; the signature is part of the HMAC-protected/validated context (not client-supplied); a mismatch is the existing fail-closed rejection (no fallback to restart). Cursor seek operator and `ORDER BY` for each column come from the same single direction source in one method, never recomputed separately.
- I390-SC-08 Prove cache coherence: every cache keyed by table metadata or key (metadata cache, compiled-plan/result caches, `TableRowIdentity` consumers, MCP/catalog description caches) is invalidated, or keyed by the directions, on a direction-only change; add a test that reads, changes only the direction, reads again and observes the new order without restart. If any cache is not covered, include the direction signature in its key.
- I390-SC-09 Keyset correctness: stream seeks use `<` exactly for descending columns, mixed asc/desc OR-chain verified for each prefix branch; NULL handling: keyset stream requires non-nullable PK columns (a nullable declared PK column with a stream request fails closed with the existing generic error, or direction is ignored with ascending as today, whichever the code already does for NULL cursor values; document and test). Default order over unique-index/PK terms with nullable columns: NULL placement is engine-defined (PG DESC puts NULLs first, SQL Server/SQLite smallest); do not add NULLS clauses, document the per-dialect behaviour in the golden tests.
- I390-SC-10 Tie-breaker stays total and deterministic: after the caller's explicit terms, all key columns not named by the caller are appended with declared direction; the full key remains in the ORDER BY so pages never skip or duplicate rows (offset paging stable).
- I390-SC-11 `Restrict` drops directions together with the key; `PrimaryKeyOrder`, catalog `PrimaryKeyDirections` and index `directions` are emitted only when every column of that key/index is visible to the principal; no direction for partial keys; no cross-tenant/principal data (values come from the per-table definition already tenant-scoped).
- I390-SC-12 Ratchet: `CatalogGovernanceRatchet.Merge` must not let an import/sync downgrade or flip stored directions when the incoming side lacks a declaration, and directions always travel with the same side as their column list (a column list from one side never combined with directions from the other).
- I390-SC-13 DoS: direction lists are bounded by the existing key column limit; a count mismatch is rejected before persistence; no per-request allocation growth beyond the existing term lists.

**Mandatory tests (`[Trait("Category","Security")]`)** in addition to plan section 3 items marked S:
- I390-T-01 `Step0002_UpgradeFromV150_WithData_PreservesRowsAndAuditChain` (all three dialects).
- I390-T-02 `Step0002_Lint_And_Golden_AppendOnly` (existing gates green at v2).
- I390-T-03 `KeyColumnSpec_TryParse_RejectsInjectionAndExtraTokens` (theory: `ts DESC; DROP`, `ts DESC DESC`, `ts NULLS FIRST`, `"ts" DESC`, `ts COLLATE x`, empty name, over-long token).
- I390-T-04 `DbtConstraint_InvalidToken_DropsWholeDefinition_NoPartialKey`.
- I390-T-05 `SetDatasetKeys_InvalidDirection_Returns400_NoEchoNoAuditLeak`.
- I390-T-06 `Reader_InvalidIsDescendingValue_FailsClosed`.
- I390-T-07 `OrderingChange_DoesNotAlterFilterMaskingOrExplicitOrderBySql`.
- I390-T-08 `PlanKeysetStream_MixedDirections_NoSkipNoDuplicateAcrossPages` (data with ties on the first column, asc,desc and desc,asc, per dialect).
- I390-T-09 `PlanKeysetStream_CursorAfterDirectionChange_FailsClosed` and `_TamperedDirectionSignature_FailsClosed`.
- I390-T-10 `PlanKeysetStream_NullableKeyColumn_BehaviourIsDefinedAndTested`.
- I390-T-11 `DirectionOnlyChange_InvalidatesCachesAndAudits`.
- I390-T-12 `DescribeDataset_And_CatalogDetail_HiddenColumn_NoDirectionLeak` (PK and index, other principal sees nothing).
- I390-T-13 `Ratchet_Merge_DirectionsNeverCrossSides`.
