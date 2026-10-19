# PLAN-REL-151: Composite-Key Relations - Implementation Plan

**Document ID:** `PLAN-REL-151`
**Date:** 2026-10-17
**Phase:** 2 (solution architect), next: Phase 3 short review
**Requirements:** [`REQ-REL-151`](2026-10-17-req-issue-151-composite-key-relations.md) (GitHub #151)
**Owner decisions (inputs):** intake OQ-1..15 accepted ([assessment](2026-10-17-issue-intake-assessment.md)): OQ-15 child table of key pairs, `dbt_constraints` later; rank 1.8 (before I-18 and `REQ-CAT-138` INC-138-4).
**Defaults taken:** Q-151-2 key columns by name (validated on write); Q-151-4 warning `PARENT_KEY_NOT_UNIQUE`, still imported; Q-151-6 maximum width 16; Q-151-3 `dbt_constraints` not in this fix.
**Code base verified:** `origin/main` `b5c1b5dd`.
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)

## 1. Verified Current Behaviour (additions to REQ-REL-151 section 2)

All REQ claims C1..C11 and N-1..N-6 re-checked on `b5c1b5dd`: confirmed. Additional facts the design depends on:

| # | Fact | Evidence |
|---|---|---|
| V-1 | `TableRelation.JoinKeysParent` defaults to `["id"]` (a second invented key, beside N-1); single-key getters fall back to `"id"` | `src/Autheris.Domain/Model/TableModels.cs:273,280` |
| V-2 | `AllowCrossDataSource` already exists on the model (init-only) but is not persisted by any repository | `TableModels.cs:292`; no DDL column |
| V-3 | Only two production call sites: `CreateRelationAsync` from the dbt importer, `GetRelationsForTableAsync` from `CatalogSchemaModel.BuildAsync` | `DbtMetadataIngestionService.cs:554`, `CatalogSchemaModel.cs:227`; interface `IGovernanceRepository.cs:250-254` |
| V-4 | Single-key properties are used only by the three repositories (read mapping and write parameters) and one test reader | PostgreSQL `...Catalog.cs:620-621,674-675`; SQL Server `:631-632,688-689`; SQLite `:1105-1106,1127-1128`; `tests/Autheris.Tests.Unit/InfrastructureTests.cs:237-238` |
| V-5 | `CreateRelationAsync` runs without a transaction on all engines; PostgreSQL/SQL Server resolve table ids without `is_active` (`LIMIT 1`) | `PostgreSqlGovernanceRepository.Catalog.cs:628-660` |
| V-6 | A second relation writer exists: SQLite demo seed inserts `TABLE_RELATIONS` with `join_key_*` | `SqliteGovernanceRepository.Schema.cs:338-348` |
| V-7 | `CatalogSchemaModel` checks keys non-empty but not equal length; mismatches are caught only at query time by the compiler | `CatalogSchemaModel.cs:250-252,304-306`; `TreeSqlCompiler.cs:381-384` |
| V-8 | `RelationExecutability` and `CatalogGraphQlMap`/`McpDatasetCatalog` never read key columns | `RelationExecutability.cs:11-30`; `McpDatasetCatalog.cs:669`; `McpDatasetModels.cs:78` |
| V-9 | No code deletes `TABLE_RELATIONS` rows (no stale-relation cleanup) | grep over `src` |
| V-10 | Baseline checksum identities are fixed tokens (`"<engine>:0001:baseline_governance_schema:v1"`); old stores are caught by the shape verifier, as for earlier baseline edits (I-1) | `*GovernanceMigrationRunner.cs:18-22` |
| V-11 | Shape snapshots hold columns, primary keys and indexes; SQLite is checked in unit tests, PostgreSQL/SQL Server in integration tests | `GovernanceSchemaShapeSnapshotTests.cs:21`, `tests/Autheris.Tests.Integration/*GovernanceSchemaShapeTests.cs` |
| V-12 | The `governance-v1.1.8-preshim.sqlite.sql` fixture deliberately holds an old shape and must stay unchanged | `GovernanceSchemaVerificationTests.cs:100-150` |

## 2. Design Decisions

1. **Model (`TableModels.cs`).** `JoinKeysParent`/`JoinKeysChild` stay the canonical lists (all consumers already use them). Defaults become empty; `JoinKeyParent`/`JoinKeyChild` are **removed** (stronger than R-151-11: no getter returns a key prefix). `IsComposite` stays. Test: `TableRelation_HasNoSingleKeyAccessors` (reflection) and `TableRelation_DefaultKeys_AreEmpty`.
2. **Key rules in one place.** New `src/Autheris.Domain/Model/RelationKeyRules.cs`: `static RelationKeyViolation? Validate(IReadOnlyList<string> parent, IReadOnlyList<string> child)` with `RelationKeyViolation { MissingColumns, CountMismatch, EmptyName, DuplicateColumn, TooWide }`, `MaxKeyColumns = 16`, duplicates compared `OrdinalIgnoreCase` per side. Used by parser, importer, three repositories (write and read) and `CatalogSchemaModel`. New `InvalidRelationException(RelationKeyViolation? violation, string message)` in `src/Autheris.Domain/Exceptions/GatewayExceptions.cs`; messages carry relation name and column identifiers only.
3. **Storage.** `TABLE_RELATION_KEYS` child table, one row per ordered pair (section 3). `join_key_parent`/`join_key_child` removed. Natural key `(parent_table_id, child_table_id, relation_name)` unique. Deviation from R-151-5: instead of one unique index on `(relation_id, parent_column, child_column)` there are two, `(relation_id, parent_column)` and `(relation_id, child_column)`, because R-151-2 forbids a repeated column on either side; the pair index would allow it.
4. **Write (`CreateRelationAsync`, three engines).** One transaction (PostgreSQL/SQL Server `BeginTransactionAsync`, SQLite inside `WriteScopeAsync`): (a) `RelationKeyRules.Validate` before opening the connection; (b) resolve both table ids from the identifiers with `is_active = 1` on all engines, without `LIMIT 1`: zero or two or more matches -> `InvalidRelationException` (SEC-REL151-07; fixes N-3; SQLite stops reading `relation.ParentTableId/ChildTableId`); (c) check every key column exists in `TABLE_COLUMNS` of its table (case-insensitive) and store the catalog's canonical name; (d) upsert the relation row on the natural key returning the stored id (PostgreSQL `ON CONFLICT (...) DO UPDATE SET cardinality = EXCLUDED.cardinality RETURNING id`; SQLite same syntax; SQL Server `MERGE ... WITH (HOLDLOCK) ON t.parent_table_id = s.p AND t.child_table_id = s.c AND t.relation_name = s.n ... OUTPUT inserted.id`); the caller's `Id` is used only on insert; (e) `DELETE FROM TABLE_RELATION_KEYS WHERE relation_id = @id`, then insert pairs with ordinals 1..n; (f) commit. Any failure rolls back all rows. `ITableRelationRepository.CreateRelationAsync` changes from `Task` to `Task<RelationWriteResult>` (`record RelationWriteResult(Guid StoredId, bool Created, bool KeysChanged)` in `IGovernanceRepository.cs`); `KeysChanged` is true when an existing relation's ordered pairs differ from the stored ones, read inside the same transaction before the delete (SEC-REL151-04). The upsert `SET` list contains only discovery-owned columns (`cardinality`); I-18 must keep it that way for `allow_cross_data_source`.
5. **Read (`GetRelationsForTableAsync`).** One statement: relation join `TABLES` x2 `LEFT JOIN TABLE_RELATION_KEYS k ON k.relation_id = r.id ORDER BY r.id, k.key_ordinal`, assembled by a new shared `src/Autheris.Infrastructure/Persistence/RelationRowAssembler.cs` (engine-neutral: takes rows `(relation fields, ordinal?, parent, child)` and yields `TableRelation`). A relation with no key rows, an ordinal gap (ordinals not exactly 1..n) or a `RelationKeyRules` violation is skipped with `LogError` naming the relation id only; the load continues (R-151-10).
6. **Seed.** `SqliteGovernanceRepository.Schema.cs:338-348` inserts the relation without `join_key_*` plus one `TABLE_RELATION_KEYS` row (`id` -> `parent_id`); `InfrastructureTests.cs:237-238` asserts the lists.
7. **Parser (`DbtArtifactStreamingParser.cs`).** `ParseManifestWithRelationshipsAsync` returns `DbtManifestParseResult(Models, Relationships, Warnings)` (record in `DbtMetadataModels.cs`); `ParseManifestStreamAsync` keeps its signature. `DbtRelationshipDefinition` gets `IReadOnlyList<string> ParentColumns, ChildColumns` instead of the single columns. Model-level `foreign_key` (`:170-190`): read all `columns`/`to_columns` in order, no `"id"` default; `RelationKeyRules` violation or missing `to` -> warning, not added; `expression` without `columns` -> `UNSUPPORTED_FORM`. Column-level `foreign_key` (`:236-250`) and the `relationships` test (`:57-100`): one pair, missing target column -> `MISSING_COLUMNS` warning. Limits (SEC-REL151-02): at most 10,000 relations per manifest, the rest skipped with one `TOO_MANY_RELATIONS` warning; a relation name, model name or column name that is empty, longer than 256 characters or contains a control character -> `INVALID_IDENTIFIER`. Warning text: `Relation '<name>' (<child> -> <parent>) skipped: <CODE>`; every logged, warned or thrown identifier passes one helper `DbtIdentifierSanitizer.ForLog` (truncate to 128 characters, control characters replaced), no manifest free text (SEC-151-5).
8. **Importer (`DbtMetadataIngestionService.cs:525-565`).** Parser warnings are added to `warnings`. Per relation, after tables are synced: unknown model -> `UNKNOWN_MODEL`; key columns checked against the stored `TableMetadata` (`GetColumn`), or, when the table is not yet stored (dry run), against the manifest model's `Columns` -> `UNKNOWN_COLUMN`, canonical catalog names used; parent columns that equal neither `PrimaryKeyColumns` nor a unique `Indexes` entry (as sets) -> `PARENT_KEY_NOT_UNIQUE` (warning only, still imported); `CreateRelationAsync` failure -> `PERSIST_FAILED` warning plus the existing log. Before any write, relations are grouped by natural key (parent table, child table, relation name, case-insensitive); a group with more than one entry is skipped entirely with one `DUPLICATE_RELATION` warning, never last-wins and never ordered by manifest position (SEC-REL151-01; default taken over the suffix alternative). The importer never sets `AllowCrossDataSource` (SEC-REL151-04). The same checks run in dry run, so dry run reports the same warnings (AC-151-3).
9. **Consumers.** `CatalogSchemaModel.cs:250,304`: replace the non-empty check by `RelationKeyRules.Validate(...) is not null` -> omit the field and log (fail closed before advertising). `TreeSqlCompiler`, `GraphQlTreeBuilder`, `CrossSourceRelationResolver`, `RelationExecutability`, `CrossSourcePlanner` (SQL text, first-column push-down only, results correct): no change, locked by tests (section 6). MCP `describe_dataset`/search show no key columns today (V-8); key output is INC-151-4 (WP-151-6).
10. **Model to extend later (binding for I-18 and INC-138-4).** I-18 (`WP-FED-07`) adds `allow_cross_data_source` (default false) to `TABLE_RELATIONS` in the same three baselines, maps it in the `RelationRowAssembler` (absent or unreadable -> `false`) and keeps it out of the natural-key upsert `SET` list (discovery never sets or overwrites it); admin writes address the relation by its stored `id`. Binding for I-18 (SEC-REL151-04): when `RelationWriteResult.KeysChanged` is true, the cross-source approval of that relation is reset in the same transaction (flag evaluates `false`) and needs re-approval by both table owners. The per-table owner model is independent of the key table. INC-138-4 adds `GetAllRelationsAsync` as the same statement without the parent filter (`ORDER BY r.id, k.key_ordinal`) through `RelationRowAssembler`, and the indexes `ix_table_relations_parent`/`_child`; the natural unique index already serves the parent lookup prefix, so only the child index is new there. Neither step may re-introduce single key columns.

## 3. DDL (baseline `0001`, edited in place; no upgrade or repair migration)

`TABLE_RELATIONS`: drop the two `join_key_*` lines; add after the table:

- PostgreSQL / SQLite: `CREATE UNIQUE INDEX IF NOT EXISTS ux_table_relations_natural ON TABLE_RELATIONS (parent_table_id, child_table_id, relation_name);` (SQLite name upper case, `UX_TABLE_RELATIONS_NATURAL`, as the #94 precedent)
- SQL Server: `IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_TABLE_RELATIONS_NATURAL' AND object_id = OBJECT_ID(N'dbo.TABLE_RELATIONS')) CREATE UNIQUE INDEX UX_TABLE_RELATIONS_NATURAL ON dbo.TABLE_RELATIONS (parent_table_id, child_table_id, relation_name);`

New table (PostgreSQL and SQLite, text types as the neighbouring tables):

```sql
-- Issue #151: ordered key pairs of a relation (one row per pair; column names validated on write)
CREATE TABLE IF NOT EXISTS TABLE_RELATION_KEYS (
    relation_id TEXT NOT NULL,
    key_ordinal INTEGER NOT NULL CHECK (key_ordinal >= 1),
    parent_column TEXT NOT NULL,
    child_column TEXT NOT NULL,
    PRIMARY KEY (relation_id, key_ordinal)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_table_relation_keys_parent ON TABLE_RELATION_KEYS (relation_id, parent_column);
CREATE UNIQUE INDEX IF NOT EXISTS ux_table_relation_keys_child ON TABLE_RELATION_KEYS (relation_id, child_column);
```

SQL Server: `relation_id NVARCHAR(128)`, `key_ordinal INT ... CONSTRAINT CK_TABLE_RELATION_KEYS_ordinal CHECK (key_ordinal >= 1)`, `parent_column`/`child_column NVARCHAR(256)`, `CONSTRAINT PK_TABLE_RELATION_KEYS PRIMARY KEY (relation_id, key_ordinal)`, both unique indexes guarded by `sys.indexes` checks (`UX_TABLE_RELATION_KEYS_PARENT`, `UX_TABLE_RELATION_KEYS_CHILD`). No foreign key constraint (baseline convention, #94 precedent); the repository deletes key rows in the same transaction. Checksum identities unchanged (V-10). Shape snapshots x3: `TABLE_RELATIONS` without `join_key_*` plus the natural index; new `TABLE_RELATION_KEYS` with primary key and two indexes. The preshim fixture stays (V-12).

## 4. Work Packages (one PR, TDD order)

| WP | Content | Files | Size |
|---|---|---|:---:|
| WP-151-1 | Model: remove single-key accessors and defaults; `RelationKeyRules`, `RelationKeyViolation`, `InvalidRelationException` | `TableModels.cs`, new `RelationKeyRules.cs`, `GatewayExceptions.cs` | S |
| WP-151-2 | Baseline DDL x3, shape snapshots x3 | `*BaselineSchema.cs` x3, `governance-shape.*.json` x3 | S |
| WP-151-3 | Repositories x3: transactional natural-key upsert, key replace, write validation, id resolution with `is_active` and ambiguity rejection (SEC-REL151-07), `RelationWriteResult` with `KeysChanged`, read assembly; SQLite seed | `*GovernanceRepository.Catalog.cs` x3, `IGovernanceRepository.cs`, new `RelationRowAssembler.cs`, `SqliteGovernanceRepository.Schema.cs` | M |
| WP-151-4 | Parser result record, all pairs, reason-coded warnings, limits and identifier rules (SEC-REL151-02), `DUPLICATE_RELATION` grouping (SEC-REL151-01), importer column and uniqueness checks, dry-run parity | `DbtArtifactStreamingParser.cs`, `DbtMetadataModels.cs`, `DbtMetadataIngestionService.cs` | S-M |
| WP-151-5 | `CatalogSchemaModel` key-rule check; composite query, masking and cross-source tests | `CatalogSchemaModel.cs`, tests | S |
| WP-151-6 | (deferred, wave 3 with `REQ-CAT-138` INC-138-2, after I-7) `describe_dataset` keys with visibility rule R-151-16/17; own short addendum | `McpDatasetModels.cs`, `McpDatasetCatalog.cs` | S |

Total WP-151-1..5: **M** (about 700-900 changed lines including tests). Test files that construct `TableRelation`/`DbtRelationshipDefinition` (11 files, V-4) need mechanical updates only.

## 5. Repository and Transaction Semantics (testable outcomes)

1. Re-import of an unchanged manifest leaves `TABLE_RELATIONS` and `TABLE_RELATION_KEYS` row sets identical, including the stored relation id.
2. Re-import with changed keys (2 -> 3 pairs, or reordered) replaces the key rows atomically; a reader never sees a mix (single transaction; PostgreSQL row lock from `ON CONFLICT DO UPDATE`, SQL Server `HOLDLOCK`, SQLite single writer).
3. A failure after the upsert (unknown column, unique violation, cancellation) leaves the previous relation and keys unchanged.
4. Concurrent syncs of the same relation end with exactly one relation row and one consistent key set.
5. Stale relations (constraint removed from the manifest) are not deleted (V-9; out of scope, OQ-2).

## 6. Test Plan

- **Unit, model/rules** (`tests/Autheris.Tests.Unit/Governance/RelationKeyRulesTests.cs`): `Validate_ReturnsNull_ForEqualNonEmptyKeys`, `Validate_ReturnsCountMismatch`, `Validate_ReturnsMissingColumns_ForEmptyLists`, `Validate_ReturnsDuplicateColumn_CaseInsensitive`, `Validate_ReturnsTooWide_Above16`, `TableRelation_HasNoSingleKeyAccessors`, `TableRelation_DefaultKeys_AreEmpty`.
- **Unit, parser** (`tests/Autheris.Extensions.Tests/DbtTests.cs`): `Parse_ModelLevelForeignKey_KeepsAllPairsInOrder` (2 and 3 columns), `Parse_CountMismatch_SkipsWithWarning`, `Parse_MissingToColumns_SkipsWithoutIdDefault`, `Parse_DuplicateColumn_SkipsWithWarning`, `Parse_ExpressionForm_SkipsUnsupportedForm`, `Parse_MoreThan16Columns_SkipsTooWide`, `Parse_ColumnLevelForeignKey_WithoutTargetColumn_SkipsWithWarning`, `Parse_Warnings_ContainNoManifestFreeText`.
- **Unit, importer**: `Ingest_UnknownModel_ReportsUnknownModel`, `Ingest_UnknownKeyColumn_ReportsUnknownColumn`, `Ingest_ParentKeyNotUnique_WarnsAndImports`, `Ingest_PersistFailure_ReportsPersistFailed`, `Ingest_DryRun_ReportsSameWarnings`, `Ingest_UsesCanonicalCatalogColumnNames`.
- **Repository, all engines** (SQLite unit; PostgreSQL/SQL Server Testcontainers in `tests/Autheris.Tests.Integration`): `CreateRelation_CompositeKeys_RoundTripsInOrder`, `CreateRelation_Twice_KeepsOneRowAndStoredId`, `CreateRelation_ChangedKeys_ReplacesKeyRows`, `CreateRelation_InvalidKeys_WritesNothing`, `CreateRelation_UnknownColumn_WritesNothingAndKeepsPrevious`, `CreateRelation_InactiveTable_Throws`, `GetRelations_RelationWithoutKeyRows_IsSkippedAndLogged`, `GetRelations_OrdinalGap_IsSkipped`, `Sqlite_CreateRelation_ResolvesTableIdsFromIdentifiers` (AC-151-4).
- **Shapes**: `EmbeddedSnapshot_EqualsShapeOfFreshlyMigratedDatabase` (existing, SQLite) and the two integration shape tests stay green; new `EmbeddedSnapshot_CoversTheRelationKeyTable` (theory over three engines: table present, `join_key_*` absent, natural index present).
- **Query** (`GovernedTreeQueryServiceTests`, `CatalogSchemaModelTests`, `CrossSourceRelationsAndChangesSinceTests`): `CompositeRelation_JoinsOnAllColumns_BothDirections` (seed rows differing only in the second key column), `CompositeRelation_SecondKeyColumnMasked_FailsClosed`, `CompositeRelation_RowFilterAppliedPerLevel`, `CatalogSchema_KeyCountMismatch_OmitsRelationField`, `CatalogSchema_Fingerprint_ChangesWithSecondKeyPair`, `CrossSource_CompositeRelation_JoinsOnAllColumns`.
- **Integration end to end** (`tests/Autheris.Tests.Integration`): `DbtManifest_CompositeForeignKey_ImportsBothPairs_AndSurvivesRestart` per engine (AC-151-1), `DbtManifest_ImportedTwice_YieldsOneRelation` (AC-151-2).

## 7. Overlap with Waves 2/3 and Position

DDL-touching steps still open: I-7 (#111), I-8 (#112 semantic manifest), I-15, I-18, I-23, `REQ-CAT-138` INC-138-2/INC-138-4, `REQ-CAT-146` B. Shared files with #151: baseline DDL x3 and shapes (all of them), `TableModels.cs` (I-15, I-18), relation repositories (I-18, INC-138-4), `CatalogSchemaModel.cs` (I-8, I-19, INC-138-4).

**Recommended position:** the next free DDL slot after I-7 merges and before I-8 starts Phase 4 (rank 1.8; branch `fix/issue-151-composite-relations` from the `main` that contains I-7). It has no code dependency on I-7 or `REQ-CAT-138` INC-138-1 and may run before I-7 if I-7 has not started Phase 4. Not in flight at the same time as any other DDL step (G-SHARED). I-18 and INC-138-4 branch from a `main` containing #151 and follow decision 10.

## 8. Risks

| Risk | Mitigation |
|---|---|
| Existing local stores have the old `TABLE_RELATIONS` shape | Shape verifier refuses start (fail closed); caller supplies the newest schema, relations re-created by re-import (project rule) |
| Stricter import drops relations that "worked" by accident (`id` default) | Every drop is a reason-coded sync warning |
| Removing single-key accessors breaks unknown callers | Only V-4 sites exist; compile errors, not silent behaviour |
| SQL Server `MERGE` race | `HOLDLOCK` plus natural unique index; concurrency test (section 5.4) |

## 9. Open Questions (defaults taken; confirmed by the owner 2026-10-17)

| # | Question | Default taken |
|---|---|---|
| OQ-1 | Two side-unique indexes instead of the pair index of R-151-5 | Yes (decision 3) |
| OQ-2 | Delete relations whose constraint disappeared from the manifest | Not in this fix; follow-up with the I-18 relation admin path |
| OQ-3 | Remove the single-key getters too (beyond R-151-11) | Yes (decision 1) |

## Phase 3 Security Review (csharp-security-expert, 2026-10-17)

**Verdict: APPROVED WITH CONDITIONS.** No plan revision blocks Phase 4; SEC-REL151-01 and -02 must be folded into decisions 7/8 by the developer (short note in the PR), the rest are binding at implementation time. Code facts checked on `origin/main`: `TreeSqlCompiler.cs:381-397` already throws on empty or unequal key lists and requires `Clear` on every parent and child key column; `CrossSourceRelationResolver.Helpers.cs:115-145` (`ValidateJoinKeysClear`) checks every pair for count, existence and `Clear`; `TryExtractKey` drops a row on any null key part (no partial-key join); the importer only links models present in the same manifest (`DbtMetadataIngestionService.cs:527-533`); the parser has `MaxDepth = 64` but no size or count bound (`DbtArtifactStreamingParser.cs:38`); V-1..V-12 confirmed.

### Findings

| # | Sev | Finding |
|---|---|---|
| F-1 | Medium | Relation name is derived from the `to` model only (`:182,248`). Two foreign keys from one child to one parent (e.g. `created_by`, `approved_by`) share the natural key `(parent, child, relation_name)`; the upsert (decision 4) then silently replaces the first key set with the last one, order-dependent. Wrong join semantics, not an access bypass, but a silent join-key swap. |
| F-2 | Medium | Nothing bounds relations per manifest, identifier length (SQL Server `NVARCHAR(256)`), or control characters; exception and warning text carries identifiers (log forging, truncation errors). Column names reach SQL only after matching a catalog column (decision 4c), which removes injection; keep it that way. |
| F-3 | Medium | I-18 compatibility gap: an approval (`allow_cross_data_source`, SEC-FED-DELTA-12) is granted for a specific key set. Re-import that changes keys keeps the flag (the upsert correctly never touches it), so approved relations could silently join on different columns. |
| F-4 | Low | `LIMIT 1` id resolution is nondeterministic when two tables differ only by case (`LOWER()` match); plan adds `is_active` but not ambiguity rejection. |
| F-5 | Low | Key rows have no FK: orphan key rows (relation deleted by a future admin path) must never surface; key columns dropped or renamed by a later catalog sync leave stale pairs. Runtime stays fail closed (compiler and `ValidateJoinKeysClear` throw on a missing column), but the schema would still advertise the field. |
| F-6 | Low | Per-relation `LogError` on every schema build can flood logs from one bad import. |
| F-7 | Info | Join-key oracle: closed at runtime on both executors as long as every pair position is checked on both sides; the deferred `describe_dataset` key output (WP-151-6) would re-open it if it listed non-`Clear` key columns. |

### Binding conditions

- **SEC-REL151-01** (F-1): Importer detects two relations with the same natural key in one import; none of them is written (fail closed, no last-wins), warning `DUPLICATE_RELATION`. Alternative allowed: deterministic disambiguating name suffix from the child column list, never from manifest order.
- **SEC-REL151-02** (F-2): Parser caps relations per manifest (max 10 000; excess skipped with one `TOO_MANY_RELATIONS` warning), and rejects identifiers that are empty, longer than 256 characters, or contain control characters. Violations are `INVALID_IDENTIFIER` warnings. Every logged or thrown identifier is truncated to 128 characters with control characters replaced.
- **SEC-REL151-03**: `RelationKeyRules.Validate` runs in the repository on every write regardless of caller, and the repository stores the catalog's canonical column names only. No manifest string is ever concatenated into SQL; all values are parameters.
- **SEC-REL151-04** (F-3): Dbt import never sets `allow_cross_data_source`; `RelationRowAssembler` defaults it to `false`. `CreateRelationAsync` reports whether the key set of an existing relation changed (return value or event). I-18 must reset the approval when the keys change and re-approve per table owner; add this to decision 10 as binding for `WP-FED-07`.
- **SEC-REL151-05**: A composite relation across sources or dialects stays non-executable by `RelationExecutability` regardless of key count; key rules never relax gating.
- **SEC-REL151-06**: Each statement runs on one connection and one explicit transaction (decision 4); the read (decision 5) stays a single statement so a reader never sees a mixed key set. Any exception, including cancellation, rolls back relation row and key rows.
- **SEC-REL151-07** (F-4): Table id resolution returns zero or one active match; two matches throw `InvalidRelationException` (no `LIMIT 1`).
- **SEC-REL151-08** (F-5): Key rows are only read through the join from `TABLE_RELATIONS` (orphan key rows are ignored). Any future relation delete must remove its key rows in the same transaction. `CatalogSchemaModel` omits a relation whose key column no longer exists in the table metadata.
- **SEC-REL151-09** (F-6): Skipped-relation logging is capped per load (one aggregated summary plus at most 10 detail lines), relation id only.
- **SEC-REL151-10** (F-7): WP-151-6 may expose a key column name only when that column is `Clear` for the caller on its table; otherwise omit the whole relation key list. Binding for the addendum.
- **SEC-REL151-11**: Masked or denied key columns fail the whole relation on every pair position and on both sides, on the tree compiler and on the cross-source resolver; no result row may be returned for the relation field.
- **SEC-REL151-12**: No new DDL beyond the baseline edit (project rule); the shape verifier remains the fail-closed guard for old stores. Not a condition for an upgrade migration.

### Mandatory security tests (`[Trait("Category","Security")]`)

- **T-REL151-01** `CompositeRelation_FirstKeyMatchOnly_ReturnsNoChildRows` (tree, rows equal in key 1 only).
- **T-REL151-02** `CompositeRelation_AnyKeyPositionMasked_FailsClosed` (theory: position 1..3, parent and child side, Deny and Mask).
- **T-REL151-03** `CrossSource_CompositeRelation_NullKeyPart_NeverJoins` and `CrossSource_CompositeRelation_KeyPositionNotClear_Throws`.
- **T-REL151-04** `Import_CrossSourceCompositeRelation_NotExecutable_UntilApproved` (SEC-REL151-04/05).
- **T-REL151-05** `Import_TwoForeignKeysSameParent_NoLastWinsSwap` (SEC-REL151-01).
- **T-REL151-06** `Parse_RelationCountAndIdentifierLimits_SkipWithWarning` (10 001 relations, 257-char and control-char names, 17 keys).
- **T-REL151-07** `Import_MaliciousColumnNames_NeverReachSql_AndWarningsAreSanitized` (quotes, `;--`, newlines, 5 000 chars).
- **T-REL151-08** `CreateRelation_KeyChange_FlagsChangeAndKeepsApprovalFlagUntouched` (SEC-REL151-04).
- **T-REL151-09** `CreateRelation_Cancelled_OrFailed_LeavesPreviousRelationAndKeys` per engine (SEC-REL151-06).
- **T-REL151-10** `CreateRelation_Concurrent_SingleRelationConsistentKeys` per engine.
- **T-REL151-11** `CreateRelation_AmbiguousCaseOnlyTables_Throws` (SEC-REL151-07).
- **T-REL151-12** `GetRelations_OrphanKeyRows_AreNotSurfaced` and `CatalogSchema_DroppedKeyColumn_OmitsRelation` (SEC-REL151-08).
- **T-REL151-13** `GetRelations_ManyBrokenRelations_LogsBoundedLines` (SEC-REL151-09).
- **T-REL151-14** `ImportedRelation_DoesNotAdvertiseKeys_OfMaskedColumn_ToCaller` (applies when WP-151-6 lands).

Total: 14 mandatory tests (T-REL151-01..14). Compatible with I-18 (upsert `SET` limited to `cardinality`, per-table owner model independent of the key table, subject to SEC-REL151-04) and with INC-138-4 (`GetAllRelationsAsync` through `RelationRowAssembler`, no filter bypass: visibility filtering stays in the caller, not the repository).

## Changelog

- **2026-10-17, Phase 4/5 implementation drift (WP-151-1..5; reviewer finding CR151-01).**
  a. Sequencing: WP-151-2 added `TABLE_RELATION_KEYS` and the natural index only; the `join_key_*` columns were dropped in WP-151-3 together with the repository rewrite, so every WP stayed green.
  b. Decision 8 / SEC-REL151-01: entries of one natural-key group with identical ordered key sets (case-insensitive), e.g. a column-level `foreign_key` plus a `relationships` test for the same pair, collapse into one relation. Only groups whose ordered key sets differ (including the same pairs in another order) are skipped whole with `DUPLICATE_RELATION`. Order-independent, no last-wins; accepted by the reviewer.
  c. Decision 7: additional reason codes `MISSING_TARGET` (foreign key without `to`), `COUNT_MISMATCH`, `DUPLICATE_COLUMN`, `TOO_WIDE` (one per `RelationKeyViolation`). The `TOO_MANY_RELATIONS` cap counts attempts (accepted plus skipped), which also bounds the warnings. A missing `to_columns` now yields `MISSING_COLUMNS` instead of the former `"id"` default (behaviour change, intended by R-151). `PERSIST_FAILED` carries the code only; `PARENT_KEY_NOT_UNIQUE` reads `... imported with warning: PARENT_KEY_NOT_UNIQUE`.
  d. Repository exceptions for an unknown, inactive or ambiguous table, an unknown column and invalid keys are `InvalidRelationException`; identifiers in messages pass `RelationWriteSupport.Safe` (Infrastructure); the dbt side uses `DbtIdentifierSanitizer.ForLog` (Extensions).
  e. T-REL151-11 is a no-op on SQL Server: the case-insensitive collation plus `ux_tables_natural` make case-only twin tables impossible; the repository check applies on all engines and the test runs on SQLite and PostgreSQL.
  f. T-REL151-04 and T-REL151-08 assert `AllowCrossDataSource == false` (the column arrives with I-18). The end-to-end "survives restart" test reads back through a fresh connection (the repositories hold no relation cache).
  g. SEC-REL151-09: the `CatalogSchemaModel` cap applies per schema build; the `RelationRowAssembler` cap applies per repository read call (one call per table).
