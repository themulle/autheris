# Plan: catalog sync reads key and index column direction from the source catalog (issue #403, 390b)

Phase 2 (csharp-architect), 2026-10-30. Base: `feat/issue-390-key-index-direction` (PR #410, 390a: `KeyColumnDirection`, step 0002 `is_descending`, `PrimaryKeyDirections`, `TableIndexDefinition.Directions`). Parent plan: [`2026-10-30-plan-issue-390-key-index-direction.md`](2026-10-30-plan-issue-390-key-index-direction.md) (I390-SC-01..13 stay binding).

## 1. Problem and scope

`DataCatalogSyncService.SyncCatalogAsync` (`:224-234`) takes key columns from the external catalog asset (`PrimaryKeyColumns` or `IsPrimaryKey` flags) and directions only from `CatalogTableAsset.PrimaryKeyDirections`, which no connector fills. So a source index `(device_id ASC, ts DESC)` still becomes an all-ascending key. Physical metadata is read today only for columns (`IColumnMetadataReader`, used by `GovernedCatalogProvider`). In scope: read the direction of the physical primary key / unique indexes for SQL Server and PostgreSQL during the sync, check it against the key the sync is about to persist, apply it under a fixed precedence, and report every conflict. Out of scope: the physical catalog never introduces or changes a column list (only directions); non-unique indexes (390c); Oracle function-based DESC indexes; changes to the PUT endpoint. No governance DDL (no step file; step 0003 stays with the default-tenant lane).

## 2. Design decisions

**D-1 Port (Application, `Sql/Catalog/IKeyIndexMetadataReader.cs`, namespace `Autheris.Application.Sql.Catalog`).** `IKeyIndexMetadataReader { TargetSqlDialect Dialect; IAsyncEnumerable<DbKeyIndexColumn> ReadAsync(DbConnection, TableIdentity, CancellationToken); }` with `readonly record struct DbKeyIndexColumn(string IndexName, bool IsPrimaryKey, bool IsClustered, int KeyOrdinal, string ColumnName, bool IsDescending)`. One row per key column of each primary key or unique, unfiltered, enabled, non-expression index; included columns are excluded. Same rules as `IColumnMetadataReader`: fully qualified system catalogs, bound `@schema`/`@table` only (SEC-X1-09).

**D-2 Dialect readers (Infrastructure, `Persistence/Catalog/`).**
- `SqlServerKeyIndexMetadataReader`: `sys.indexes` + `sys.index_columns` (`key_ordinal > 0`, `is_included_column = 0`, `is_descending_key`) + `sys.columns` + `sys.objects`/`sys.schemas` by name; `(is_primary_key = 1 OR is_unique = 1) AND has_filter = 0 AND is_disabled = 0 AND is_hypothetical = 0`; `IsClustered = (type = 1)`.
- `PostgreSqlKeyIndexMetadataReader`: `pg_catalog.pg_index` + `pg_class`/`pg_namespace` by `nspname`/`relname` + `pg_am` (`amname = 'btree'` only, other access methods have no order) + `unnest(indkey) WITH ORDINALITY`; `indisunique AND indisvalid AND indisready AND indpred IS NULL AND indexprs IS NULL AND ord <= indnkeyatts`; `IsDescending = (indoption[ord - 1] & 1) = 1` (int2vector is zero-based; the NULLS FIRST bit 2 is ignored, NULL placement stays as in I390-SC-09); `IsPrimaryKey = indisprimary`; `IsClustered = indisclustered`.
- SQLite, Databricks, Oracle, DuckDB: no reader registered, so the direction is unknown (= ascending, nothing applied). This is the issue's rule for SQLite and Databricks (Databricks has informational constraints, no index).
- Caps: more than `MaxIndexesPerTable * MaxColumnsPerKey` rows, or a `KeyOrdinal` gap or duplicate, makes the snapshot `Unavailable` (fail closed, no partial use).

**D-3 Physical snapshot (Application, `Sql/Catalog/IPhysicalKeyCatalog.cs`).** `IPhysicalKeyCatalog.ReadAsync(TableMetadata, CancellationToken) -> PhysicalKeySnapshot` with state `Known(IReadOnlyList<PhysicalKey>)`, `Unsupported` (no reader for the dialect) or `Unavailable` (no connection, dialect mismatch, login breaker open, exception, caps, or an empty result for a table whose columns are readable, which is how SQL Server shows missing `VIEW DEFINITION`). Implemented as `GovernedCatalogProvider` partial `GovernedCatalogProvider.PhysicalKeys.cs`, reusing `FindConnection`, the dialect admission check (`:253-285`), the login breaker and `_connectionFactory`, so there is no new `IOptions<GatewayOptions>` consumer and no new connection path. The partial needs `partial` on the existing class. Exceptions are logged by type and table id only (no SQL text, no names).

**D-4 Matching (Application, `DataCatalog/Services/PhysicalKeyDirectionReconciler.cs`, pure static).** For a key column list K (the primary key or a unique index the sync is about to persist), the backing physical index is chosen in this order: (1) the physical primary key; (2) when there is no physical primary key, the clustered unique index; (3) otherwise a unique index. The candidate must have exactly K's columns in K's order (case rule per dialect as in `SqlProcedureRowScopeResolver.HasUniqueIndexOn`: PostgreSQL exact, SQL Server case-insensitive). Several step-(3) candidates with different direction lists make the direction `Ambiguous`. If the physical primary key exists but differs from K in columns or order, the result is `Mismatch` (no fallback to (2)/(3)). No candidate gives `NoBackingIndex`. `Mismatch`, `Ambiguous`, `NoBackingIndex` and `Unavailable` contribute no direction (fail closed).

**D-5 Precedence (per key column list, one side only, I390-SC-12).**
1. Directions declared on the incoming asset (`CatalogTableAsset.PrimaryKeyDirections`, index `Directions`) win as in 390a. A differing physical direction is reported (`KEY_DIRECTION_DRIFT`), not applied.
2. A stored, non-empty (canonical) direction list for the identical column list counts as operator-owned (PUT, dbt/OpenAPI import, or an earlier sync). When the physical direction differs, the stored key is kept whole (columns and directions from the stored side; `ReplaceKeyDefinition` stays true for the rest). The finding is `KEY_DIRECTION_CONFLICT`.
3. Otherwise a `Known` match applies the physical directions to the incoming column list (they belong to the same list by exact equality, not by combining sides).
4. Otherwise the direction is unknown, which means ascending (empty).
When the asset declares no key (`ReplaceKeyDefinition = false`), the stored definition is untouched; a physical direction that differs from it gives `KEY_DIRECTION_DRIFT` only. All-ascending equals undeclared (390 D-3 canonical form), so an operator's explicit all-ASC is not protected; the change is audited (D-6).

**D-6 Sync wiring and audit (`DataCatalogSyncService` `:224-234`).** After `CatalogKeyDefinitionImport.Apply` and before `CatalogGovernanceRatchet.Merge`: `snapshot = await _physicalKeys.ReadAsync(metadata, ct)` (only for `DataSourceType.Sql` with `dialectSupported`), then `(metadata, findings) = PhysicalKeyDirectionReconciler.Reconcile(metadata, existing, snapshot)`. The new required constructor dependencies are `IPhysicalKeyCatalog` and `IAuditWriter` (no legacy overload; update the test fakes). If the persisted PK or index directions change because of step 3, one `CATALOG_KEY_DEFINITION_CHANGED` audit is written before the upsert, with actor `system:catalog-sync`, before/after, and details `directionSource=physical`. The policy stays FailClosed: if the audit write fails, that table is upserted with the pre-reconcile metadata and the warning `KEY_DIRECTION_AUDIT_FAILED` is added. Findings go to `CatalogSyncResult.Warnings` as `"Table '<id>': <CODE>"`, with no column, index or SQL text (hidden columns stay hidden). Under `restrictToTenant`, only owned tables are reported (existing rule). A dry run reads and reports but writes no audit and nothing else. The per-table epoch invalidation after the upsert stays as it is (SEC-SYNC-EPOCH).

**D-7 Ratchets.** Every new file is under 1000 lines. `DataCatalogSyncService.cs` (294) grows by about 30 lines, and `GovernedCatalogProvider.cs` (383) does not grow because of the partial. No clock use; `TimeProvider` is only via the provider. Namespace equals folder for every new file. The DI registration goes in `GatewayStorageServiceExtensions` next to the column readers (`:127-132`), and `IPhysicalKeyCatalog` forwards to the `GovernedCatalogProvider` singleton.

## 3. Work packages, tests and dialect matrix

| WP | Content | Depends | Size |
|---|---|---|---|
| WP-403-1 | D-1, D-2: port, SQL Server and PostgreSQL readers, DI, SQL goldens, container contract tests | - | M (~300) |
| WP-403-2 | D-3, D-4, D-5: snapshot, `GovernedCatalogProvider.PhysicalKeys.cs`, pure reconciler | 1 | M (~350) |
| WP-403-3 | D-6: sync wiring, audit, warnings, dry run, end-to-end | 2 | S-M (~250) |

Tests (`S` = `Category=Security`):
- WP-403-1, `tests/Autheris.Tests.Unit/Sql/KeyIndexMetadataReaderTests.cs`: T-01 `SqlServerReader_Sql_MatchesGolden`, T-02 `PostgreSqlReader_Sql_MatchesGolden` (bound parameters only, no concatenation) (S), T-03 `Readers_RegisteredOnlyForSqlServerAndPostgreSql`. In `tests/Autheris.Tests.Integration/KeyIndexMetadataReaderContractTests.cs` (abstract contract, SQL Server and PostgreSQL Testcontainers): T-04 `PrimaryKey_AscDesc_ReportsDirectionsInKeyOrder`, T-05 `IncludedColumns_FilteredAndDisabledIndexes_Excluded`, T-06 `ClusteredUniqueWithoutPrimaryKey_ReportedClustered`, T-07 `PostgreSql_NonBtreeAndExpressionIndexes_Excluded`, T-08 `QuotedMixedCaseNames_BoundNotInterpolated` (S).
- WP-403-2, `tests/Autheris.Tests.Unit/DataCatalog/Issue403PhysicalKeyReconcilerTests.cs`: T-10 `Match_PrimaryKey_AppliesPhysicalDirections`, T-11 `NoPhysicalPk_ClusteredUnique_Applies`, T-12 `NoPhysicalPk_SingleUnique_Applies`, T-13 `PhysicalPkDiffersInColumns_Mismatch_NoDirection` (S), T-14 `PhysicalPkDiffersInOrder_Mismatch_NoDirection` (S), T-15 `TwoUniqueCandidatesDifferentDirections_Ambiguous` (S), T-16 `AssetDeclaredDirections_Win_DriftReported`, T-17 `StoredNonEmptyDirections_Kept_ConflictReported_NoCrossSide` (S), T-18 `NoIncomingKey_StoredUntouched_DriftOnly` (S), T-19 `Unavailable_Or_Unsupported_NoChange` (S), T-20 `UniqueIndexDirections_ReconciledLikePk`. In `GovernedCatalogProviderPhysicalKeysTests.cs`: T-21 `LoginBreakerOpen_Unavailable`, T-22 `DialectMismatch_Unavailable` (S), T-23 `EmptyResultWithReadableColumns_Unavailable` (S), T-24 `CapExceeded_Unavailable_NoPartialSnapshot` (S), T-25 `Exception_LoggedWithoutSqlOrNames` (S).
- WP-403-3, `tests/Autheris.Extensions.Tests/Issue403DataCatalogSyncDirectionTests.cs`: T-30 `Sync_PhysicalDesc_PersistsDirectionsAndAudits`, T-31 `Sync_AuditFailure_KeepsPreReconcileMetadata_Warns` (S), T-32 `Sync_DryRun_ReportsWithoutAuditOrWrite`, T-33 `Sync_Warnings_ContainCodeAndTableOnly` (S), T-34 `Sync_RestrictToTenant_NoForeignFindings` (S), T-35 `Sync_SqliteAndDatabricks_DirectionUnknown_BehavesAsToday`. Integration (SQL Server container): T-36 `SyncThenGraphQlRead_DescKey_NewestFirstWithoutOrderBy`.

Dialect matrix:

| Dialect | Source | Direction | Fallback without PK | Tests |
|---|---|---|---|---|
| SQL Server | `sys.index_columns.is_descending_key` | known | clustered unique, then unique | T-01, T-04..T-08, T-36 |
| PostgreSQL | `pg_index.indoption` bit 1, btree only | known | `indisclustered` unique, then unique | T-02, T-04..T-08 |
| SQLite | none (issue rule) | unknown = ASC | n/a | T-03, T-35 |
| Databricks | none (informational constraints) | unknown = ASC | n/a | T-03, T-35 |
| Oracle, DuckDB | none | unknown = ASC | n/a | T-03 |

## 4. Open questions (recommended default)

- O-1 Provenance of stored directions (operator vs synced) needs a new column (`direction_source`) and a step file, which would be 0004 because step 0003 is taken by the default-tenant lane. Default: no DDL. A non-empty stored list counts as operator-owned (D-5.2); stale synced DESC is corrected through PUT. Revisit if conflicts are frequent.
- O-2 Should the physical primary key seed a key definition when the catalog declares none? Default: no (the catalog stays the owner of column lists, #94 D-8); only `KEY_DIRECTION_DRIFT` is reported.
- O-3 SQLite does expose `pragma_index_xinfo.desc`. Default: follow the issue (unknown); add it later behind the same port if requested.
- O-4 Should the sync audit every key-definition replacement (columns too), not only direction changes? Default: direction changes only in #403; a separate issue for the general case.
- O-5 Source privilege: SQL Server needs `VIEW DEFINITION` on the table (missing gives `Unavailable` through D-3), and PostgreSQL catalogs are world-readable. Default: document it in the operator manual only on request; no new option.
- O-6 Phase 3: does applying physical directions to the incoming column list by exact equality (D-5.3) satisfy I390-SC-12 "never combined across sides"? Default: yes (the physical list is a verification of the same list, not a second side).

## 5. Phase 3 rulings (csharp-security-expert, 2026-10-30)

**Verdict: APPROVED WITH CONDITIONS.** Defaults for O-1..O-5 are accepted. Code facts verified: the sync call site is `src/Autheris.Extensions/DataCatalog/DataCatalogSyncService.cs:224-231` (not under Application; D-4/D-6 paths for the sync wiring are corrected accordingly); `GovernedCatalogProvider*.cs` has no command timeout of its own; `Apply` receives `null` directions whenever the asset declares columns without directions (including the `IsPrimaryKey` flag path).

**Rulings.**
- **O-6: YES, with I403-SC-04.** Applying physical directions to a column list that is exactly equal (same names, same order, dialect case rule) is a verification of one list, not a combination of two sides. Any partial overlap, reorder, subset or superset yields no direction.
- **390a gap (asset re-declaring the same key columns without directions overwrites stored DESC): #403 closes it** (I403-SC-05). D-5.2 ("stored non-empty directions are operator-owned") is void if `Apply` has already reset them to empty before the reconciler runs, which is exactly what happens on every sync of an asset without directions when the physical read is Unsupported/Unavailable. It is a one-line precedence rule inside the same reconciler, so it is not split off.

**Binding conditions.**
- I403-SC-01 (fail closed): Any physical read failure (exception, cancellation, timeout, breaker open, no connection, dialect mismatch, empty result, row cap, ordinal gap/duplicate, duplicate column in one index, unknown index shape) yields `Unavailable`; the reconciler then changes nothing and never produces a partial key update. Directions and columns of one key are always replaced together or not at all.
- I403-SC-02 (no injection): Readers use only fully qualified system catalogs with bound `@schema`/`@table` (or provider parameters); no identifier, name or object id is ever interpolated or concatenated into SQL. Names read from the catalog are data only and are never fed back into SQL, logs or audit.
- I403-SC-03 (no new credential surface): The physical read uses only the existing `GovernedCatalogProvider` path (`FindConnection`, dialect admission, login breaker, `_connectionFactory`). No new `IOptions`, connection string, factory or second connection path; the connection is read-only use (catalog SELECTs only) and disposed per read. A login failure feeds the same breaker; key reads must not bypass an open breaker.
- I403-SC-04 (O-6): Matching is exact-equality only (D-4); a physical primary key that differs in columns or order is `Mismatch` with no fallback; ambiguous candidates give no direction. Physical data never adds, removes or reorders a column and never creates a key definition (O-2 default).
- I403-SC-05 (390a gap): For an identical column list, when the incoming side declares no directions (empty/null) and the stored list is non-empty, the stored directions are retained, independent of the physical result. Directions are reset only when the incoming column list differs from the stored one. This applies to the PK and to each unique index, and to the `IsPrimaryKey` flag path.
- I403-SC-06 (precedence, no silent override): Order is fixed: asset-declared > stored non-empty > physical > unknown. Physical never overwrites a stored non-empty list or asset-declared directions; each difference yields a finding. Physical directions are applied only to a key whose columns the sync itself persists.
- I403-SC-07 (audit before write): Every direction change applied from the physical source is audited (`CATALOG_KEY_DEFINITION_CHANGED`, `system:catalog-sync`, before/after directions, `directionSource=physical`) strictly before `UpsertTableMetadataAsync`. An audit exception (including cancellation) aborts the physical change: the table is upserted with the pre-reconcile (post-`Apply`, SC-05-corrected) metadata and `KEY_DIRECTION_AUDIT_FAILED` is warned. The upsert never happens with physical directions unless the audit succeeded. Audit payloads contain direction lists and table id only, no index names or constraint names.
- I403-SC-08 (dry run): With `dryRun` the physical read and findings run, but no audit, no upsert, no epoch invalidation and no other write occur.
- I403-SC-09 (confidentiality): Warnings, audit details, logs and API responses carry only a governed code and the table id. Never column names, index names, schema text, SQL text, exception messages or provider error text; exceptions are logged by type only. Findings obey `restrictToTenant` and the existing table visibility rule (no finding for tables the caller does not own or may not see).
- I403-SC-10 (resource caps): The read is bounded by a hard row cap (`MaxIndexesPerTable * MaxColumnsPerKey`, enforced while streaming, aborting at cap + 1 without buffering beyond it), a command timeout no longer than the existing catalog column read timeout (and the linked cancellation token), and one read per table per sync. The cap abort yields `Unavailable`, never a truncated snapshot. The sync-wide per-run bound on tables stays as today; no unbounded fan-out per table.
- I403-SC-11 (admission): The physical read only runs for tables that passed the existing governed-admission check and only for `DataSourceType.Sql`; a table denied by admission gets no physical read and no finding.
- I403-SC-12 (no weakening): `CatalogGovernanceRatchet.Merge` still runs after reconcile; physical data never touches sensitivity, four-eyes, masking, active flags or any other security field; the SEC-SYNC-EPOCH invalidation stays.
- I403-SC-13 (SQL Server privilege): An empty result for a table with readable columns is `Unavailable` (no direction, no finding that reveals metadata visibility beyond the code); no new option and no privilege escalation hint in responses.

**Mandatory tests (all `[Trait("Category","Security")]`; in addition to the plan's S-tagged T-02, T-08, T-13..T-15, T-17..T-19, T-22..T-25, T-31, T-33, T-34, which stay mandatory).**
- I403-T-S01 `Reader_Sql_NoInterpolation_BoundParametersOnly_BothDialects` (hostile quoted/mixed-case/`';--` table names stay data).
- I403-T-S02 `PhysicalRead_UsesGovernedProviderPathAndBreaker_NoSecondConnection` (breaker open: factory never called).
- I403-T-S03 `PartialOverlapOrReorder_NoDirectionApplied` (subset, superset, reorder, case variant per dialect).
- I403-T-S04 `AssetWithoutDirections_SameColumns_StoredDescRetained_PkAndUnique_FlagPath` (390a gap; physical Unsupported and Unavailable variants).
- I403-T-S05 `AssetWithoutDirections_ChangedColumns_DirectionsReset`.
- I403-T-S06 `Precedence_AssetOverStoredOverPhysical_NeverSilentOverride` (each difference yields exactly one finding).
- I403-T-S07 `AuditThrows_NoPhysicalDirectionPersisted_AuditBeforeUpsertOrdering` (call-order recorder, also cancellation).
- I403-T-S08 `DryRun_NoAuditNoUpsertNoEpochInvalidation`.
- I403-T-S09 `Warnings_AuditAndLogs_NeverContainIndexColumnOrExceptionText` (sentinel strings in index names, columns and exception messages).
- I403-T-S10 `Streaming_RowCapPlusOne_AbortsUnavailable_NoPartialUpdate` and `CommandTimeout_Unavailable_NoPartialUpdate`.
- I403-T-S11 `AdmissionDeniedTable_NoPhysicalRead_NoFinding`.
- I403-T-S12 `PhysicalDirections_NeverChangeSecurityFlags_RatchetStillApplied`.
- I403-T-S13 `OrdinalGapDuplicateOrDuplicateColumn_Unavailable`.
