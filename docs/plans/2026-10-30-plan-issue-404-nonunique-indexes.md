# Plan: describe_dataset exposes non-unique indexes (issue #404, follow-up of #390 and #403)

Phase 2 (csharp-architect), 2026-10-30. Branch `feat/issue-404-nonunique-indexes`, based on `feat/issue-390-key-index-direction` (PR #410, `1dd9c2c9`). Implemented after #403 (`2026-10-30-plan-issue-403-sync-key-direction.md`, rulings I403-SC-01..13 apply unchanged to the shared read path).

## 1. Verified state (G-PLAN-REBASE, `1dd9c2c9`)

- The declared model already covers non-unique indexes: `TABLE_INDEXES.is_unique` (baseline 0001), `TableIndexDefinition(Name, Columns, IsUnique, Directions)`, written by PUT key-definition, dbt, OpenAPI `x-autheris-indexes` and the DataCatalog asset, replaced wholesale via `ReplaceKeyDefinition`, audited as `CATALOG_KEY_DEFINITION_CHANGED`, and read by `DefaultReadOrdering`, `KeysetCursorEncoder`, `BulkStreamKey` and `AggregationNegationGuard` (unique only).
- No physical index is ever synced; #403 reads PK and unique indexes only.
- Gap (security): `describe_dataset` (`McpDatasetCatalog.cs:431,541`) and REST detail (`CatalogDiscoveryService.cs:168,206`) emit `Indexes` (with names) after `TableKeyVisibility.Restrict(table, table.Columns)`. That ignores the per-caller levels (`ResolveColumnLevels`, `:421`) and masking (`:174`), so an index over a Masked or Denied column is disclosed today.
- GraphQL: `describe_dataset` over GraphQL MCP (`GatewayMcpQueryExecutor.cs:303`) calls the same `DescribeDatasetAsync`. Parity holds by construction and is pinned by a test.
- Ratchets: `McpDatasetCatalog.cs` is 1317 lines against a ceiling of 1319, so its net growth must be ≤ +2. The migration catalogue must be gap-free (`GovernanceMigrationRunnerBase.cs:626`, `HistoryGap`).

## 2. Design decisions

**D-1 Separate physical model; `TABLE_INDEXES` is not reused.** Declared indexes are operator-owned and audited, and they drive read semantics. Physical non-unique indexes are observed, advisory metadata that each sync refreshes. Sharing one table would let a sync overwrite operator declarations, or let a PUT erase synced rows. Physical indexes never feed read ordering, keyset, bulk-stream or aggregation guards.

**D-2 Step `0004_physical_nonunique_indexes` (forward-only, additive; 0003 is reserved for the default-tenant lane).** Per dialect, a `.sql` file (idempotent: the SQLite `-- guard:` line, SQL Server `IF OBJECT_ID(...) IS NULL`, PostgreSQL `IF NOT EXISTS`) and a `.shape.json` file (`addTable` and `addIndex` ops):
- `TABLE_PHYSICAL_INDEXES(id PK, table_id NOT NULL, index_ordinal INT NOT NULL CHECK (index_ordinal >= 1))` with `UX_TABLE_PHYSICAL_INDEXES_ORDINAL (table_id, index_ordinal)`.
- `TABLE_PHYSICAL_INDEX_COLUMNS(index_id NOT NULL, column_ordinal INT NOT NULL CHECK (column_ordinal >= 1), table_column_id NOT NULL, is_descending 0/1 NOT NULL DEFAULT 0 CHECK (is_descending IN (0, 1)), PK(index_id, column_ordinal))` with `UX_TABLE_PHYSICAL_INDEX_COLUMNS_COLUMN (index_id, table_column_id)`.
- No index name column (data minimisation, O-1) and no timestamp column (clock ratchet).
- Append one checksum line per dialect to `tests/Autheris.Tests.Architecture/Golden/GovernanceSteps/checksums.*.txt`. Lines 0001 and 0002 stay byte-identical.
- **Ordering dependency:** with a gap-free catalogue, 0004 cannot merge before 0003 is on main. The lead rebases WP-404-1 onto the default-tenant merge before opening the PR (O-6).

**D-3 Domain and store.**
- Domain: `sealed record PhysicalIndexDefinition(IReadOnlyList<string> Columns, IReadOnlyList<KeyColumnDirection> Directions)` in `Domain/Model/TableModels.cs`, plus `TableMetadata.PhysicalIndexes`, which defaults to empty.
- Store: `Infrastructure/Persistence/CatalogPhysicalIndexStore.cs` (static, same dialect record as `CatalogKeyDefinitionStore`). It has `ReadAsync`/`ReadBatchAsync`, called next to the key-definition reads, and `ReplaceAsync`, which runs DELETE and INSERT in one transaction with bound parameters only.
- Read rule: an index with any `table_column_id` that no longer resolves is dropped as a whole.
- Table deletion and the `TablesExistAsync` probe include the two new tables.
- Write port: a new narrow `IPhysicalIndexRepository.ReplacePhysicalIndexesAsync(TableIdentifier, IReadOnlyList<PhysicalIndexDefinition>, CancellationToken)` in `Application/Interfaces`. Each dialect implements it in a new partial `*GovernanceRepository.PhysicalIndexes.cs` so the `Catalog.cs` partials do not grow, and DI forwards the port to the repository singleton. `IGovernanceRepository` fakes are untouched.

**D-4 Physical read: extends the #403 port, no second path (I403-SC-02/03).**
- `DbKeyIndexColumn` gains `bool IsUnique`.
- The reader SQL widens from "PK or unique" to every plain index, ordered PK, then unique, then non-unique, then index id and key ordinal.
- Index shape rules:
  - SQL Server: `type IN (1, 2)`, `has_filter = 0`, not disabled, not hypothetical, key columns only.
  - PostgreSQL: `btree`, valid, ready, `indpred IS NULL`, `indexprs IS NULL`, `ord <= indnkeyatts`.
- `PhysicalKeySnapshot` gains `NonUnique`: `Known(list)`, `Unsupported` or `Unavailable`. The #403 reconciler filters `IsPrimaryKey || IsUnique`, so its behaviour is unchanged.
- Caps (new constants in `TableKeyDefinitionValidator`):
  - `MaxPhysicalIndexesPerTable = 32`; columns per index ≤ `MaxColumnsPerKey` (32).
  - The row cap rises to `(MaxIndexesPerTable + MaxPhysicalIndexesPerTable) * MaxColumnsPerKey`, and hitting it still makes everything `Unavailable` (I403-SC-10).
  - When the 33rd non-unique index is reached, the reader stops and sets `NonUnique = Unavailable`. The key part is already complete because keys are ordered first, so it stays `Known`.
- Mapping: physical column names map to catalog columns by the dialect case rule. An index with an unmapped column is dropped whole.
- De-duplication: a physical index whose exact (columns, directions) equals the declared PK, a declared index or an earlier physical index is dropped. Prefixes are kept.

**D-5 Sync write (`DataCatalogSyncService`, after the #403 reconcile, same snapshot, one read per table).**
- `Known`: replace only when the result differs.
- `Unsupported`: replace with an empty list, so the catalog keeps no stale hints for an unreadable source.
- `Unavailable`: keep the stored list (no partial write) and warn `"Table '<id>': PHYSICAL_INDEX_UNAVAILABLE"`, with no names (I403-SC-09).
- A dry run writes nothing. Admission and `restrictToTenant` follow I403-SC-11.
- The write happens before the existing per-table epoch invalidation.
- No audit (O-3). Logs carry counts only.

**D-6 Exposure (MCP, REST, GraphQL MCP in parity).**
- Breaking change (no production): `Indexes` on `McpDatasetDescription` and `CatalogDatasetDetail` becomes `IReadOnlyList<DatasetIndexInfo>?`, with `sealed record DatasetIndexInfo(IReadOnlyList<McpKeyColumn> Columns, bool Unique)`. No index names, physical or declared (O-1).
- Content: declared unique, then declared non-unique, then physical non-unique, de-duplicated by (columns, directions), capped at `MaxIndexesPerTable` entries. The PK is not repeated (`PrimaryKeyOrder`).
- Visibility: a new pure `Application/Data/Services/DatasetIndexProjection.Project(TableMetadata restricted, IReadOnlySet<string> clearColumns)` emits an index only if every column is readable in clear text by the caller. Masked, Denied or missing columns omit the whole index (fail closed, same rule as `DefaultReadOrdering`).
  - MCP: clear set = columns with `columnAccessLevels` = `Clear`; replace `:541`, net ≤ +2 lines.
  - REST: clear set = listed columns with `MaskingState == "Clear"`.
- `TableKeyVisibility.Restrict` also filters `PhysicalIndexes` all-or-nothing (defence in depth).
- The Summary shape keeps `Indexes` excluded (allow-list unchanged). There are no new error shapes; the projection cannot throw.
- Admin GET key-definition (`CatalogApiEndpoints.cs:568`) stays declared-only, with names (steward surface).

**D-7 Ratchets.** Every new file is under 1000 lines, namespace equals folder, there is no clock use, and no legacy error body is added.

## 3. Work packages

| WP | Content | Depends | Size |
|---|---|---|---|
| WP-404-1 | D-1..D-3: step 0004 (3 dialects), goldens, model, store, port, upgrade tests | default-tenant 0003 on main for merge | M (~400) |
| WP-404-2 | D-4, D-5: reader extension, snapshot, sync write | WP-404-1, #403 merged | M (~300) |
| WP-404-3 | D-6: projection, DTO change, MCP/REST/GraphQL parity | WP-404-1 | S-M (~250) |

**Dialect matrix.**
- Governance store (step 0004 and the store): SQLite (unit), PostgreSQL and SQL Server (Testcontainers).
- Source reader:
  - SQL Server and PostgreSQL: `Known` from the system catalogs.
  - SQLite, Databricks, Oracle and DuckDB: `Unsupported`, so there is no physical list and declared indexes are still shown (O-2).

**Tests ((S) = `[Trait("Category","Security")]`).**
- WP-404-1:
  - `Governance/GovernanceSqliteUpgradeTests.cs`: T-01 `I404_Step0004_UpgradeFromV150_WithData_PreservesRowsAndAuditChain`, T-02 `Step0004_Rerun_IsIdempotent_GuardSkips`, T-03 `Step0004_CheckConstraints_RejectInvalidOrdinalAndDirection`.
  - `GovernanceUpgradeTestsBase.cs` (PostgreSQL, SQL Server): T-04 `I404_T_04_Step0004_UpgradeFromV150_AppliesAndShapeVerifies`.
  - Architecture: T-05 `GovernanceStepChecksums_AppendOnly_0004Present` (existing golden test, new lines).
  - `Persistence/CatalogPhysicalIndexStoreTests.cs`: T-06 `Replace_ThenRead_RoundTripsColumnsAndDirections`, T-07 `Read_UnresolvedColumn_DropsWholeIndex` (S), T-08 `DeleteTable_RemovesPhysicalIndexes`, T-09 `Store_Sql_BoundParametersOnly` (S).
- WP-404-2:
  - `Sql/KeyIndexMetadataReaderTests.cs`: T-10 `Readers_Sql_MatchesGolden_IncludesNonUnique`.
  - Integration contract (SQL Server, PostgreSQL): T-11 `NonUnique_AscDesc_ReportedInKeyOrder`, T-12 `FilteredPartialExpressionIncluded_Excluded` (S).
  - `DataCatalog/PhysicalNonUniqueIndexTests.cs`: T-13 `Reconciler_IgnoresNonUniqueRows_403BehaviourUnchanged`, T-14 `NonUniqueOverCap_NonUniqueUnavailable_KeysKnown` (S), T-15 `RowCap_AllUnavailable` (S), T-16 `UnmappedColumn_DropsIndex` (S), T-17 `Dedup_EqualToDeclaredOrPk_Dropped`.
  - `DataCatalog/DataCatalogSyncPhysicalIndexTests.cs`: T-18 `Sync_Known_ReplacesOnlyWhenChanged`, T-19 `Sync_Unavailable_KeepsStored_WarnsCodeOnly` (S), T-20 `Sync_Unsupported_ClearsStored`, T-21 `Sync_DryRun_WritesNothing` (S), T-22 `Sync_AdmissionDenied_NoReadNoFinding` (S), T-23 `PhysicalIndexes_NeverAffectDefaultReadOrderingOrKeyset`.
  - Integration (SQL Server): T-24 `SyncThenDescribe_NonUniqueIndexListed`.
- WP-404-3:
  - `Catalog/DatasetIndexProjectionTests.cs`: T-25 `MaskedColumn_WholeIndexOmitted` (S), T-26 `DeniedOrMissingLevel_WholeIndexOmitted` (S), T-27 `AllClear_EmitsColumnsDirectionsUnique_NoName` (S), T-28 `DeclaredAndPhysical_DedupedOrderedAndCapped`.
  - `Mcp/DescribeDatasetIndexTests.cs`: T-29 `Describe_Masked_IndexOmitted_PerCaller` (S), T-30 `Describe_Summary_NoIndexes` (S).
  - REST: T-31 `RestDetail_MatchesMcpIndexes_ForSameCaller` (S).
  - GraphQL MCP: T-32 `GraphQlMcpDescribe_IndexesJsonEqualsMcp`.
  - T-33 `Restrict_FiltersPhysicalIndexesAllOrNothing` (S).
  - T-34 `Describe_JsonHasNoIndexName` (S).

## 4. Open questions (recommended default)

- O-1 Expose index names? Default: no, in describe and REST, for declared and physical indexes alike; physical names are not even stored.
- O-2 SQLite source reader (`pragma_index_list`/`pragma_index_xinfo` with a bound table argument)? Default: not in #404; `Unsupported`, consistent with #403 O-3.
- O-3 Audit physical index replacement? Default: no. It is advisory metadata with no governance or read-semantic effect; logs carry counts only. Phase 3 may require `CATALOG_PHYSICAL_INDEXES_CHANGED`.
- O-4 Apply the Clear-only rule to `PrimaryKeyOrder` as well? Default: no change in #404 (I390-SC-11 stays); open a follow-up issue.
- O-5 Expose physical unique indexes that are not declared? Default: no (#403 O-2, physical data never creates keys).
- O-6 Step numbering: 0004 waits for 0003 (gap-free catalogue). Default: wait. If the owner wants #404 first, renumber to 0003 before either lane merges; no golden line has been released for 0003 or 0004.
- O-7 Filtered/partial indexes, expression indexes and INCLUDE columns? Default: filtered/partial and expression indexes are excluded; INCLUDE columns are ignored.
- O-8 The OpenAPI generator emits declared index names (`DynamicOpenApiGenerator.cs:353`). Default: unchanged (operator-chosen names, steward document).

## Phase 3 rulings (csharp-security-expert, 2026-10-30)

**Verdict: APPROVED WITH CONDITIONS.** The conditions are binding. Rulings are consistent with I403-SC-01..13 (fail closed, bound parameters, governed codes only, caps, admission, dry run).

**Verified fact.** `McpDatasetCatalog.cs:431` and `CatalogDiscoveryService.cs:168` call `TableKeyVisibility.Restrict(table, table.Columns)`. The filter is the listed-column set, not the clear-text set. `Indexes` (`:541`, `:206`) and `PrimaryKeyOrder` (`:545`) are therefore emitted for keys and indexes that span Masked columns, together with operator-chosen index names (which can embed names of columns the caller must not see).

**Leak ruling.** Severity MEDIUM (metadata disclosure, no row values). Disclosed: that a masked or hidden column is indexed or unique, its sort direction, its composite shape, and index names (free text). It weakens an existing control (masking and column visibility) on main. Decision: split out and ship first, independent of 0003/0004. The split PR contains only the visibility fix (`DatasetIndexProjection` over declared indexes, no names, `PrimaryKeyOrder` rule below) and needs no governance DDL. #404 then adds physical indexes on top and must not reintroduce either.

### Split leak fix (ships alone, ids I404-SC-L*)
- I404-SC-L1 (whole-index omit): `DatasetIndexProjection.Project(table, clearColumns)` emits a declared index only if every column is readable in clear text for the caller. A Masked, Denied, Hidden or unresolved column omits the whole index. No partial index, no column subset, no direction for an omitted index.
- I404-SC-L2 (clear set source): The clear set is derived from the same per-caller access levels that govern value reads (`Clear` only). It is never derived from `table.Columns`. A missing or failed access-level lookup gives an empty clear set (all indexes omitted).
- I404-SC-L3 (no names): Declared index names are removed from MCP `describe_dataset` and REST catalog detail (`DatasetIndexInfo(Columns, Unique)`). The brief and other MCP paths keep `Indexes: null`.
- I404-SC-L4 (PrimaryKeyOrder, O-4 ruling): `PrimaryKeyOrder`, `PrimaryKeys` and key columns are emitted only if every PK column is clear-text readable; otherwise the PK is omitted whole (same as indexes). This overrules the plan default. I390-SC-11 ("visible", not "clear") is tightened to "clear" in the same PR. Applies to MCP, REST and GraphQL MCP.
- I404-SC-L5 (parity): MCP, REST and GraphQL MCP use the one shared projection; no surface re-implements the filter. Counts and orderings are identical for the same caller.
- I404-SC-L6 (cap): Output is capped at `MaxIndexesPerTable` (32) indexes and `MaxColumnsPerKey` (32) columns per index, enforced in the projection.
- I404-T-SL1 `MaskedColumn_WholeIndexOmitted_Mcp_Rest_GraphQl` (S)
- I404-T-SL2 `DeniedOrMissingAccessLevel_WholeIndexOmitted_FailClosed` (S)
- I404-T-SL3 `DescribeDataset_And_RestDetail_EmitNoIndexNames` (S)
- I404-T-SL4 `PrimaryKeyOrder_OmittedWhenAnyPkColumnMasked_AllSurfaces` (S)
- I404-T-SL5 `AllClear_EmitsColumnsDirectionsUnique_Capped32x32` (S)
All tests carry `[Trait("Category","Security")]`; T-25..T-27 in WP-404-3 are satisfied by L-tests and need not be duplicated.

### #404 conditions
- I404-SC-01 (O-1, names): No index name is read into the model, stored, logged, audited, warned or returned. The physical readers do not select names beyond what the query needs to group rows, and discard them immediately.
- I404-SC-02 (O-8 ruling): Overruled. `DynamicOpenApiGenerator.cs:353` must stop emitting declared index names and must apply the same clear-only projection (or omit indexes). A published OpenAPI document is a public-ish surface and must not be more permissive than describe. Delivered in the L-PR; if the generator has no caller context, emit no indexes there.
- I404-SC-03 (O-3 ruling): Audit required, minimal. Replacing physical indexes is not a governance decision, but it changes exposed metadata and is performed by `system:catalog-sync`. Emit one `CATALOG_PHYSICAL_INDEXES_CHANGED` entry per table per actual change (only when the list differs), payload: table id, before and after counts, no columns, no names, written before `ReplacePhysicalIndexesAsync`. An audit failure aborts the replace, keeps the stored list and warns `PHYSICAL_INDEX_AUDIT_FAILED` (code only). No-op syncs write no audit, so there is no audit flood.
- I404-SC-04 (fail closed, `Unavailable`): Keeping the stored list is acceptable because it cannot widen exposure: the projection (L1) re-checks visibility at read time, and the stored list only ever shrinks relative to what a successful sync would show or stays equal. Stored column ids that no longer resolve drop the whole index (D-3). `Unsupported` clears the list. The warning is `PHYSICAL_INDEX_UNAVAILABLE` plus table id only, no exception text or provider text, exceptions logged by type only.
- I404-SC-05 (no key-path coupling): A non-unique read failure (`NonUnique = Unavailable`) never downgrades a `Known` key snapshot. Conversely, a key `Unavailable` implies `NonUnique` Unavailable.
- I404-SC-06 (caps): `MaxPhysicalIndexesPerTable = 32`, columns per index at most 32, enforced while streaming at cap + 1 without buffering. The store `ReplaceAsync` re-validates both caps and the ordinal contract (gap-free, no duplicate column) before writing, regardless of the caller. Violation throws and nothing is written.
- I404-SC-07 (write isolation): `ReplaceAsync` runs DELETE and INSERT in one transaction, bound parameters only, keyed by table id from the verified `TableIdentifier`. It never touches `TABLE_INDEXES`. Tenant scope is inherited from the table id (no cross-tenant write); `restrictToTenant` and admission follow I403-SC-11.
- I404-SC-08 (dry run): Dry run performs the read and the finding only. No store write, no audit, no epoch invalidation.
- I404-SC-09 (no semantic effect): Physical indexes never feed read ordering, keyset, bulk-stream, aggregation, pushdown, policy or masking decisions. Guard test below.
- I404-SC-10 (step 0004): Purely additive (two new tables, check constraints, unique indexes), idempotent guards on all three dialects, no change to 0001..0003 files or their checksum lines; 0003 must be on main first (O-6 default accepted). No name column, no timestamp. Foreign keys to the existing table and column tables must cascade-safe with the delete path (table deletion removes rows).
- I404-SC-11 (no new credential path): Reader extension reuses the #403 port only (I403-SC-02/03). SQL Server `type IN (1,2)`, `has_filter = 0`, not disabled, not hypothetical, key columns only; PostgreSQL btree, valid, ready, no predicate, no expression, key columns only. System catalog queries use bound `@schema`/`@table`.
- I404-SC-12 (O-2, O-5, O-7 accepted): SQLite and other dialects are `Unsupported`; physical unique indexes never become keys; filtered, partial, expression and INCLUDE columns are excluded.
- I404-SC-13 (de-duplication and output order): Dedup is by exact columns and directions after projection, so an omitted index cannot influence the visible list (no side-channel through counts or dedup). The cap is applied after visibility filtering.
- I404-SC-14 (epoch): The replace happens before per-table epoch invalidation so no cached describe result serves the old list after a change. The cache key must include the caller's access context (as today); no cross-caller sharing of projected output.

### Mandatory tests (all `[Trait("Category","Security")]`, in addition to L-tests and plan tests T-07, T-09, T-12, T-19, T-21, T-22)
- I404-T-S01 `PhysicalIndex_OverMaskedColumn_NotDescribed_Mcp_Rest_GraphQl`
- I404-T-S02 `Sync_Unavailable_KeepsStored_WarningHasCodeAndTableIdOnly_NoIndexNameNoErrorText`
- I404-T-S03 `Sync_Replace_WritesAudit_BeforeStore_NoNamesNoColumns` and `Sync_AuditFailure_KeepsStoredList`
- I404-T-S04 `Sync_NoChange_WritesNoAudit`
- I404-T-S05 `Reader_Caps_33rdIndex_NonUniqueUnavailable_KeysStayKnown`
- I404-T-S06 `Store_Replace_RejectsOverCapOrdinalGapOrDuplicateColumn_WritesNothing`
- I404-T-S07 `Store_Replace_CrossTenantTable_Isolated`
- I404-T-S08 `Step0004_Additive_PriorChecksumLinesByteIdentical_NoDataLoss`
- I404-T-S09 `PhysicalIndexes_NeverAffectReadOrderingKeysetPolicyMasking` (extends T-23)
- I404-T-S10 `OpenApi_EmitsNoIndexNames_AndNoIndexOverMaskedColumn`
- I404-T-S11 `Describe_DedupAndCap_AppliedAfterVisibilityFilter_NoCountSideChannel`
- I404-T-S12 `Describe_CacheNotSharedAcrossCallersWithDifferentClearSets`

## Changelog
- 2026-10-30 (leak fix, Phase 4): code facts verified against the branch. `PrimaryKeyOrder` does not exist as a property; the ordered `PrimaryKeys` list is the key output. The REST catalog detail (`CatalogDiscoveryService`) has no per-caller consent resolution, so its clear set is the catalog baseline (masking state `Clear`: not sensitive, no masking rule). OpenAPI has no caller context and emits no indexes. A key or index with more than 32 columns is omitted whole, never truncated. Indexes are de-duplicated by columns and uniqueness after the visibility filter.
