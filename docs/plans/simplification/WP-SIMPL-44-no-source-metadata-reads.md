# WP-SIMPL-44 Data access only: no source metadata reads
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#issues-51) (WP added by OD-25; folds in #459) | [PRD OD-25](../2026-11-05-req-project-simplification.md) | Issues: #459 (whole issue, widened by OD-25) | Owner decisions: OD-25 (binding), OD-3, OD-18, OD-22, OD-23, OD-17 | Security conditions: SEC-SIMPL-01, 02, 03, 21, 23, 25, 26, 30, 31, 32; SEC-SIMPL-140..147 (section "Phase 3 conditions", binding per the sec plan section 11.2, row 44); ADR-018

## 1. Goal and outcome
Autheris reads data from data sources and never reads their metadata. After this WP:
- No production code path sends a statement to a data-source connection that reads a schema or system catalog (`INFORMATION_SCHEMA`, `sys.*`, metadata functions such as `OBJECT_ID`, `pg_catalog`/`pg_*` relations, Oracle `ALL_/USER_/DBA_/CDB_` dictionary views and `V$`, Databricks `information_schema`, `system.*`, `SHOW`/`DESCRIBE`). The only exception is the allowlisted CDC change-tracking call (D6).
- Column types, collations, keys and indexes used by the compiler, GraphQL/OData cost rules and procedure governance come only from the declared catalog (catalog API upload in core; dbt/OpenMetadata modules as optional feeders).
- Two guards stop the reads from coming back. An architecture test scans `src`. A runtime guard wraps every data-source connection and rejects metadata references before execution (fail closed).
- Operators get least-privilege role templates for PostgreSQL, SQL Server, Oracle and Databricks. Integration tests prove the governed paths work under those roles on PostgreSQL and SQL Server.
- Measured: `git grep -nE "<catalog pattern of step 1>" -- src` returns only the governance-DB allowlist plus the CDC entry. About -2,600 src lines and +900 added.

## 2. Preconditions and dependencies
- Merged first:
  - WP-SIMPL-12: vendor catalog connectors are deleted, so `DataCatalogSyncService` is only touched once.
  - WP-SIMPL-17: Dbt folder merge. The plan places #459 after WP-17 and before WP-28.
  - WP-SIMPL-39 PR 3: one YAML definition format, which PR 3 of this WP builds on.
- Must merge before WP-SIMPL-28 (dbt/OpenMetadata module). The `meta.indexes` reader moves with the module.
- Coordinates with:
  - WP-SIMPL-41: the statistics and samples upload `PUT /api/v1/catalog/datasets/{id}/discovery-artifacts` is the external path for statistics and samples. This WP adds nothing for them and only asserts that no statistics or samples are computed from sources.
  - WP-SIMPL-38: `TrinoMetadataQueryHandler` already answers from `ITableMetadataRepository` and never forwards. This WP adds the guard and a test (no new handler work).
- Parallel-safe with WP-SIMPL-43 and with WP-SIMPL-42 except its PR-3. With WP-SIMPL-39, rebase PR 3 on top of it; WP-39 does not extend the catalog validation (its OD-25 banner) and this WP deletes it (D3).
  - `GovernedProcedureExecutionService.cs` is also edited by WP-SIMPL-39, WP-SIMPL-46 step 4 and WP-SIMPL-42 PR-3: PR 3 does not run in parallel with those PRs; whichever merges second rebases.
  - WP-SIMPL-37 PR C (Databricks tier flip, OD-36) edits the `GovernedCatalogProvider` admission rules of step 7; whichever merges second rebases.
  - WP-SIMPL-45 owns the write connection (`WriteConnection`) and its privileges; the operator template below links to it.

## 3. Scope
### In scope
- Delete the column and key/index metadata readers and switch `GovernedCatalogProvider` to declared metadata (no source connection).
- #459 part 1: delete the physical key/index chain, the two `refresh-physical-keys` routes and `TableMetadata.PhysicalIndexes`. `IndexCandidates`, `IndexUsabilityEvaluator`, `NestedListIndexMatcher`, `DatasetIndexProjection` and `TableKeyVisibility` then work on declared indexes only.
- #459 part 2: dbt `meta.indexes` on sources and models.
- Core catalog API for declared table definitions (columns with native type and collation, keys, indexes): `PUT /api/v1/catalog/datasets/{id}/definition`.
- Procedures:
  - Delete `StoredProcedureCatalogValidator` and `ProcedureValidationMode.Catalog`; declared validation becomes the only mode.
  - Row-scope uniqueness proof by `sys.indexes`/`pg_index` is replaced by a key-closure data query.
- Runtime guard (`SourceMetadataGuard`, `MetadataGuardedConnection`), architecture test, Npgsql type loading off, error mapping of drift, reserved-name validation at catalog upload.
- Operator role templates in `docs/operations/source-least-privilege-roles.md` (system reference, no status).

### Out of scope
- The governance database owned by Autheris: migrations, shape checks, `GovernanceSchemaValidator`, `CatalogKeyDefinitionStore` `OBJECT_ID` and `DbCatalogVersionProvider` stay.
- Autheris-internal in-process engines (DuckDB OLAP, federation and table cache). They are not data sources; the guard does not wrap them.
- Statistics and samples upload, and synthetic sample generation (WP-SIMPL-41, OD-22).
- Lakehouse Delta-log and Iceberg-manifest reads (WP-SIMPL-27). The table format's own log is how its data is read; the module must still govern by the declared column set (D7).
- Result-set metadata from the data protocol (`DbDataReader.GetColumnSchema()` in `SqlResultColumns.cs:89-93`). It is part of the data response, not a catalog read. `CommandBehavior.KeyInfo`/`SchemaOnly` stay unused (guarded by the architecture test).

## 4. Current state (verified on `docs/simplification-plans` @ db45aa80)
Inventory of source-metadata reads (purpose -> replacement):

| # | Location | Read | Purpose | Replacement |
|---|---|---|---|---|
| 1 | `Infrastructure/Persistence/Catalog/{PostgreSql(44),SqlServer(58),Oracle(70),Databricks(41),Sqlite(33),DuckDb(36)}ColumnMetadataReader.cs` via `IColumnMetadataReader` (`Application/Sql/Catalog/IColumnMetadataReader.cs:20`) | `information_schema.columns`, `sys.columns`, `ALL_TAB_COLUMNS`, Unity `information_schema.columns` (`Databricks...:18-19`), `pragma_table_info`, `duckdb_columns()` | `GovernedCatalogProvider.LoadAsync` (`GovernedCatalogProvider.cs:246-360`) admits a table only if every governed column exists in the source; it takes native type, collation and column order for `CatalogColumn` (`:347`) used by the compiler (tenant predicate typing, `PolicyKeyQueryCompiler.cs:245` bindable key pairs) | Declared `TableColumn.DataType` + new `Collation`, `LengthSemantics`; admission from declared metadata only; no connection |
| 2 | `{PostgreSql(56),SqlServer(52)}KeyIndexMetadataReader.cs` via `IKeyIndexMetadataReader`, `GovernedCatalogProvider.PhysicalKeys.cs` (227) implementing `IPhysicalKeyCatalog` (`IPhysicalKeyCatalog.cs:51`) | `sys.indexes`/`index_columns`, `pg_index` | Key directions (#403), non-unique indexes (#404), refresh (#427) | Declared keys/indexes (`PUT .../keys`, `.../definition`, dbt `meta.indexes`) |
| 3 | `PhysicalKeyRefreshService.cs` (311, `:112`), `PhysicalKeyDirectionReconciler.cs` (228), `PhysicalNonUniqueIndexResolver.cs` (137), `Extensions/DataCatalog/PhysicalIndexSync.cs` (107), `DataCatalogSyncService.cs:238`, `DbtMetadataIngestionService.PhysicalKeys.cs` (153), `CatalogPhysicalIndexStore.cs`, `IPhysicalIndexRepository.cs`, `{PostgreSql,SqlServer,Sqlite}GovernanceRepository.PhysicalIndexes.cs`, `PhysicalKeyRefreshModels.cs`, `IPhysicalKeyRefreshService.cs` | via #2 | Consumers of physical keys; routes `POST /api/v1/catalog/datasources/{id}/refresh-physical-keys` and `/api/governance/...` (`CatalogApiEndpoints.cs:251-258`) | Deleted; routes 404 |
| 4 | `IndexCandidates.cs:34`, `DatasetIndexProjection.cs:90`, `TableKeyVisibility.cs:33` | `TableMetadata.PhysicalIndexes` (`TableModels.cs:395`) | Index-aware pricing (`IndexUsabilityEvaluator`, `QueryCostAnalyzerRule`, `NestedListIndexMatcher`, `TreeSqlCompiler.SetBased`), MCP index projection | Declared `Indexes` only |
| 5 | `Application/Procedures/Services/StoredProcedureCatalogValidator.cs` (538): `sys.objects` `:135,:196`, `sys.parameters` `:278`, `sys.columns` `:363`, `sys.dm_exec_describe_first_result_set_for_object` `:375`, `sys.dm_sql_referenced_entities` `:406`, `sys.security_policies`/`sys.security_predicates` `:488-489` | catalog | Signature, result columns, browse-mode column sources (SEC D-2), referenced tables, dynamic SQL, `is_updated`, SECURITY POLICY presence | Declared definition (D3) |
| 6 | `SqlProcedureRowScopeResolver.EnsureUniqueKeyAsync` (`:342-384`, SQL `:302-316`) | `sys.indexes`, `pg_index`, `pragma_index_list` | Proves the row-scope key is unique before `BuildKeyQuery` (`:247-292`) | Key-closure query (step 9), no uniqueness proof needed |
| 7 | `Extensions/Cdc/CdcSourceQueries.cs:60` | `CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@fullName))` | Change-tracking validity (module, WP-29) | Allowlisted exception (D6) |
| 8 | `TrinoMetadataQueryHandler.cs` (411) | none on sources: it reads `metadataRepository.GetAllTablesAsync` (`:113`) | SHOW/`information_schema` emulation | Unchanged; test that it opens no source connection |
| 9 | `Npgsql.NpgsqlConnection` created at `SqlConnectionFactory.cs:114` | Npgsql loads types from `pg_type`/`pg_catalog` on first open | Driver type loading | `ServerCompatibilityMode = NoTypeLoading` (D5) |

These are not source metadata reads and stay:
- `SessionPinning.cs:13-38` (`set_config('search_path', 'pg_catalog, …')`, the Oracle schema pin, `@@OPTIONS`) only sets or reads session settings; `pg_catalog` appears as a search-path entry, not as a read.
- The denylists in `RowFilterAstValidator.cs:26`, `ProcedureDefinitionLoader.cs:258-263`, `SqlFunctionPolicy.cs:26-81` and `CasbinEnforcementService.Columns.cs:41` (deleted by WP-14).
- The governance DB items listed in section 3 (out of scope).

There is no other auto-registration and no row-count, size or profiling probe against sources:
- Tables enter the catalog only through `UpsertTableMetadataAsync` callers: dbt, OpenMetadata, `DataCatalogSyncService`, OpenAPI ingestion, `AccessPlanningService.cs:842`, `VirtualSystemTables.cs:186` and `CatalogApiEndpoints.cs:735,790`.
- `ColumnValueProfileService.ComputeAndStoreProfileAsync`/`ComputeAndStoreDatasetProfilesAsync` (`:43,:172`) take values from the caller and have no production caller outside `Catalog/Profiling`.
- `EstimatedRowCount` is only merged from ingestion (`CatalogGovernanceRatchet.cs:153`).
- Every `COUNT(*)` hit is either a governed data read (`GovernedTableReadPlanner.cs:113,210,225`) or a governance-DB query.

Gaps:
- There is no core catalog API that registers a table with its columns and types. Today columns arrive only through dbt, OpenMetadata or vendor sync.
- `ArchitectureNoSourceKeyCatalogQueriesTests` (Tests.Architecture, Category=Security) covers key and index patterns only and allowlists #2 and #6.

## 5. Implementation steps (TDD order; red test first in each step)
1. **Architecture test (PR 1).** Rename `tests/Autheris.Tests.Architecture/ArchitectureNoSourceKeyCatalogQueriesTests.cs` to `ArchitectureNoSourceMetadataReadsTests.cs`.
   - Patterns: extend the list to `\binformation_schema\b`, `\bsys\.[a-z_]+`, `\b(OBJECT_ID|OBJECT_NAME|OBJECT_SCHEMA_NAME|OBJECTPROPERTY(EX)?|COLUMNPROPERTY|INDEXPROPERTY|COL_LENGTH|OBJECT_DEFINITION|HAS_PERMS_BY_NAME)\s*\(`, `\bsp_(help|describe_|columns|tables|pkeys|fkeys|statistics|spaceused|stored_procedures|sproc_columns|special_columns)`, `\bpg_catalog\b`, `\bpg_(class|attribute|index|constraint|namespace|proc|type|description|stat[a-z_]*|statistic|settings|tables|views|indexes|matviews|roles|authid|database)\b`, `\bpg_get_[a-z_]+\s*\(`, `::reg(class|proc|type)\b`, Oracle `\b(ALL|USER|DBA|CDB)_(TABLES|TAB_COLUMNS|TAB_COLS|VIEWS|OBJECTS|CONSTRAINTS|CONS_COLUMNS|INDEXES|IND_COLUMNS|ARGUMENTS|PROCEDURES|SOURCE|SYNONYMS|TAB_STATISTICS|TAB_COL_STATISTICS|TAB_COMMENTS|COL_COMMENTS|DEPENDENCIES|POLICIES|SEGMENTS|TYPES|MVIEWS)\b`, `\bG?V\$[A-Z_]+`, `\bDBMS_METADATA\b`, `"\s*(SHOW|DESCRIBE|DESC|ANALYZE)\s+`, `\bduckdb_(columns|constraints|indexes)\b`, `\bpragma_(table_x?info|index_)`, `CommandBehavior\.(KeyInfo|SchemaOnly)`, `\.GetSchema\(`.
   - Each pattern gets a positive sample in `Samples`.
   - Directory exclusions (governance DB): `Autheris.Infrastructure/Persistence/Migrations/`, the governance repositories and stores (`*GovernanceRepository*.cs`, `*SqlCore.cs`, `*Store.cs` under `Autheris.Infrastructure/Persistence/` except `SqlConnectionFactory.cs`), `GovernanceSchemaValidator.cs`, `DbCatalogVersionProvider.cs`.
   - Exact-path allowlist with a reason:
     - `Autheris.Extensions/Cdc/CdcSourceQueries.cs` (D6).
     - The guard itself, `Autheris.Application/Sql/SourceMetadataGuard.cs` (pattern constants).
     - The denylist files `RowFilterAstValidator.cs`, `ProcedureDefinitionLoader.cs`, `SqlFunctionPolicy.cs`, `SqlFunctionAllowlists.cs`, `SessionPinning.cs`, `DatabaseOptions.cs` and `TrinoMetadataQueryHandler.cs`, each with the reason "denylist/emulation literal, never sent to a source".
   - Match comment-stripped code: drop `//` and `///` lines.
   - The test starts red, listing items #1, #2, #5 and #6 of section 4, and turns green in step 10.
2. **Runtime guard (PR 1).** Create `src/Autheris.Application/Sql/SourceMetadataGuard.cs`:
   ```csharp
   public static class SourceMetadataGuard
   {
       public static void EnsureDataOnly(string sql, DatabaseDialect dialect);       // throws SourceMetadataAccessDeniedException
       internal static bool TryFindViolation(string sql, DatabaseDialect dialect, out string rule);
   }
   public sealed class SourceMetadataAccessDeniedException(string rule) : InvalidOperationException(...)
   ```
   - Lexer: skip string literals (`'..'` with `''`, `N'..'`, `E'..'`, PostgreSQL `$tag$..$tag$`) and comments (`--`, nested `/* */`). Normalize quoted identifiers (`"x"`, `[x]`, `` `x` ``) to lower-case bare tokens. Unterminated literals or comments are a violation (fail closed).
   - Rules on the token stream, case-insensitive. Each rule has an id in the exception (never the SQL):
     - R1: `information_schema` followed by `.`.
     - R2 (SqlServer): `sys` followed by `.`; the metadata functions of step 1 followed by `(`; `sp_` catalog procedures from step 1; three-part names starting `master`/`msdb`.
     - R3 (PostgreSql): `pg_catalog`; the `pg_*` relations and functions of step 1; `::regclass/regproc/regtype`.
     - R4 (Oracle): the dictionary views of step 1, `v$`/`gv$`, `dbms_metadata`, qualifier `sys`.
     - R5 (Databricks): a three-part name starting `system`.
     - R6 (all dialects): leading statement keyword `SHOW`, `DESCRIBE`, `DESC`, `ANALYZE`, `EXPLAIN`, `DBCC`, `SET SHOWPLAN_*`, `SET STATISTICS`.
   - Allocation: span-based scan, no regex per call, `SearchValues<string>` for keyword sets.
3. **Guarded connection (PR 1).** Create `src/Autheris.Infrastructure/Persistence/MetadataGuardedConnection.cs`, a `DbConnection` decorator modelled on `BindByNameOracleConnection.cs`:
   - `CreateDbCommand` returns `MetadataGuardedCommand`, which calls `SourceMetadataGuard.EnsureDataOnly(CommandText, dialect)` in `ExecuteDbDataReader(Async)`, `ExecuteNonQuery(Async)`, `ExecuteScalar(Async)` and `Prepare`.
   - In `SqlConnectionFactory.CreateOpenConnectionAsync`, run the session pins on the raw connection (`:140-208` unchanged), then return `new MetadataGuardedConnection(raw, options.Dialect)`.
   - No `Unwrap` method. Provider type checks after the factory exist only inside the factory today (verified: `SqlConnectionFactory.cs:140,159,176,188`). `VirtualFilterStore.cs:83` uses its own governance connection (verify in PR 1; if it uses `ISqlConnectionFactory`, switch it to a dialect property).
   - On a violation, also log a warning `SourceMetadataReadBlocked` with data source and rule id only, and emit the audit event `SOURCE_METADATA_READ_BLOCKED` (new const in `AuditEventTypes.cs`, `AuditPolicyTable` entry, AuditLevel.Full).
4. **Npgsql type loading off (PR 1).** At `SqlConnectionFactory.cs:114`, build the connection through `NpgsqlConnectionStringBuilder { ServerCompatibilityMode = ServerCompatibilityMode.NoTypeLoading }` (D5).
5. **Drift error mapping (PR 1).** In `DataAccessErrorClassifier.cs`, extend `DataAccessErrorKind` with `SourceSchemaMismatch = 3` and `SourcePermissionDenied = 4`:
   - SQL Server numbers: mismatch {207, 208, 2812, 201, 8144}; permission {229, 230, 262}.
   - PostgreSQL SqlState: mismatch {42P01, 42703, 42883, 42804}; permission {42501}.
   - Oracle `Number`: mismatch {942, 904, 6550, 6553}; permission {1031}.
   - Databricks: the error-class string the facade surfaces (`Infrastructure/Databricks/DatabricksDbCommand.cs`). Mismatch: `TABLE_OR_VIEW_NOT_FOUND`, `UNRESOLVED_COLUMN`, `UNRESOLVED_ROUTINE`. Permission: `PERMISSION_DENIED`, `INSUFFICIENT_PERMISSIONS`.
   - `GatewayExceptionStatusMapper.cs:55` and `GatewayExceptionHandler.cs:151` map both kinds to 502, with codes `SOURCE_SCHEMA_MISMATCH` and `SOURCE_PERMISSION_DENIED` in `ApiErrorCodes.cs`. The message is generic, with no object or column names and no SQL (SEC-SIMPL-25).
   - `SourceMetadataAccessDeniedException` maps to 500 `SOURCE_METADATA_READ_BLOCKED` (an internal defect, not a client error).
6. **Declared column metadata (PR 2).** Add `public string? Collation { get; init; }` and `public string? LengthSemantics { get; init; }` (Oracle `char`/`byte`) to `TableColumn` (`TableModels.cs:213`).
   - Governance migration: the next free forward-only migration step at merge time (per the frozen v1.5.0 base policy), named `column_collation`, for `postgresql` and `sqlserver` (`Persistence/Migrations/Steps/`). It adds two nullable columns to the column table with a shape file (SEC-SIMPL-16).
   - Repository read and write follow.
   - `DataType` becomes the declared native type, including length/precision/scale (`nvarchar(64)`, `numeric(18,2)`). The uploader validates it per dialect with a strict grammar (`^[a-z][a-z0-9 _]*(\(\d+(,\s*\d+)?\|max\))?$`, case-insensitive).
7. **Catalog provider from declared metadata (PR 2).** In `GovernedCatalogProvider`:
   - Delete the `_readers`/`_keyReaders` fields and ctor parameters, `OpenAsync` and the connection in `GetCatalogAsync`.
   - `LoadAsync` builds `TableCatalogEntry` from `metadata.Columns` in declared order, using `CatalogColumn(c.ColumnName, c.DataType, c.Collation)`.
   - Admission rules stay: registered, `DataSourceType.Sql`, source name and dialect match, at least one column, tenant column among the columns, SQLite affinity check on the declared type until WP-31, dialect admitted only when `SqlDialectMapper.IsExecutable` is true (tier `Production`; Databricks after WP-SIMPL-37 PR C, OD-36), with no explicit per-dialect branch.
   - `Fingerprint` (`:225-237`) adds each column's `DataType` and `Collation`, so a declared type change invalidates the cache.
   - `CatalogColumnVersion` is unchanged.
   - `KeySetKeyTypes.IsBindablePair` with a null collation on a string type returns false (fail closed: not bindable). Add this if it is not already the behaviour.
   - Remove the `ISqlConnectionFactory` dependency and the reader registrations at `GatewayStorageServiceExtensions.cs:127-138`.
8. **Core catalog definition upload (PR 2).** Add `PUT /api/v1/catalog/datasets/{id}/definition` to `CatalogApiEndpoints.cs`:
   - Id parsing as at `:62`. `RequireAuthorization(GatewayPolicies.GovernanceAdmin)`, `.WithAudit(AuditLevel.Full, AuditEventTypes.CatalogKeyDefinitionChanged)`.
   - Body `CatalogDatasetDefinitionRequest { schemaVersion: 1, dataSource, kind: table|view, columns: [{ name, nativeType, collation?, lengthSemantics?, description? }], tenantColumn?, primaryKey?: ["col", "col DESC"], indexes?: [{ name, columns, unique }] }`. Keys and indexes use the same `KeyColumnSpec` grammar as `PUT .../keys`.
   - Create or replace. Replace keeps masking, consent, tags, discovery flags and owners, and replaces columns, keys and indexes atomically.
   - Validation:
     - `TableKeyDefinitionValidator` for keys.
     - Column names match `^[A-Za-z_][A-Za-z0-9_$#]{0,127}$`.
     - Limits: 1,024 columns and 32 indexes; body 1 MiB (SEC-SIMPL-23).
     - Reserved source names are rejected with 400 `CATALOG_RESERVED_SOURCE_NAME` (SEC-SIMPL-143): schema `sys`, `information_schema`, `pg_catalog`, `pg_toast`; any `pg_*` table name; Oracle owners `SYS`, `SYSTEM`, `XDB`, `CTXSYS`, `MDSYS`; Databricks catalog `system`; any name matching an R4 dictionary view.
   - The same reserved-name check runs in `UpsertTableMetadataAsync` callers through one helper, `CatalogSourceNameValidator.Validate(TableMetadata)`, called by the repository upsert path. This covers dbt, OpenMetadata and OpenAPI as well.
   - No discovery, no source connection and no "import from source" option.
9. **Row-scope key closure (PR 3).** In `SqlProcedureRowScopeResolver`, delete `EnsureUniqueKeyAsync`, `BuildUniqueIndexQuery`, `UniqueIndexTableArgument`, `_uniquenessCache`, `UniquenessCacheTtl` and `NotUnique`. Rewrite `BuildKeyQuery` (`:247-292`) to
   `SELECT {keys} FROM {table} AS autheris_target WHERE ({key tuples}) GROUP BY {keys} HAVING COUNT(*) = SUM(CASE WHEN {tenant} AND ({filter}) THEN 1 ELSE 0 END)`.
   - A key is allowed only if every row carrying it passes the tenant predicate and the row filter, so uniqueness is no longer needed.
   - Parameters and batching are unchanged; the same parameter names are used.
   - Update the class summary (`:25`).
   - Remove the resolver entry from the architecture allowlist.
10. **Procedures declared only (PR 3).**
    - Delete `StoredProcedureCatalogValidator.cs` and its DI registration.
    - Delete `ProcedureValidationMode` (always declared), so the YAML key `validation` is rejected by the strict deserializer.
    - Delete `RestApiOptions.AllowDeclaredValidation` (`RestApiOptions.cs:265`) and add the key to `UnknownGatewayKeyGuard` (SEC-SIMPL-31).
    - Delete the `ProcedureConnectionProvider.cs:46` catalog branch.
    - Loader `:226-240`: the declared rules apply to all definitions. Require `result_table`, non-empty `outputs`, `parameters` (name, type, direction, optional default) and the new `tables:` (referenced governed tables; every entry must be registered in the catalog, otherwise the definition stays `Pending` with reason `TABLE_NOT_REGISTERED`).
    - `RegisteredProcedure.GovernedTables()` (WP-39 step 8) = declared `tables` + `result_table`.
    - Result governance (`GovernedProcedureExecutionService.cs:907-915`):
      - Undeclared columns are dropped.
      - A declared output may carry `source: <table>.<column>`.
      - A column without `source` is governed by the result table.
      - Columns of other tables survive only with an explicit column-level Clear (SEC D-2 semantics kept, source now declared).
      - `ProcedureModels.cs:185` browse-mode doc is updated.
    - Parameter binding uses the declared types; SqlClient `DeriveParameters` is never used (architecture test pattern `DeriveParameters`).
    - SECURITY POLICY presence is no longer checked (D3). A procedure with `rls: session_context` on tables that have row filters for the caller must also declare `row_scope_key` (Autheris-side key closure, step 9); otherwise the load fails with `ROW_SCOPE_REQUIRED`.
    - `ddl_hash` stays an audited operator attestation. If any code compares it with a source definition, that comparison is deleted.
11. **Physical key/index chain removal (PR 4, #459 part 1).**
    - Delete the files of section 4 #2 and #3 and the `IKeyIndexMetadataReader`/`IColumnMetadataReader` interfaces.
    - Delete `TableMetadata.PhysicalIndexes` and its uses: `IndexCandidates.cs:34-40`, `DatasetIndexProjection.cs:90`, `TableKeyVisibility.cs:33-53`, `DbtMetadataIngestionService.cs:706,1571`.
    - Delete `AuditEventTypes.CatalogPhysicalIndexesChanged` and its policy entry, `TableKeyDefinitionValidator.MaxPhysicalIndexesPerTable` and the DI lines.
    - Map both `refresh-physical-keys` routes to 404 by removal and add them to `DeletedFeaturesArchitectureTests` (SEC-SIMPL-30).
    - Governance step 0004 stays shipped and unused (frozen-base rule). The repository stops reading and writing its table.
12. **dbt `meta.indexes` (PR 4, #459 part 2).** In `DbtArtifactStreamingParser.cs` (index collector at `:609-612`):
    - Read `config.meta.indexes` (fallback `meta.indexes`) on sources and models, in the exact shape of issue #459.
    - `primary_key: true` (at most one) gives the declared PK with directions; every other entry is a declared index; `clustered` is informational and ignored.
    - Validate with `TableKeyDefinitionValidator`. Any invalid entry drops the whole declaration with a warning that carries node id and rule only.
    - `meta.indexes` wins over key tests of the same node.

## 6. Tests ([S] = `[Trait("Category","Security")]`; project `tests/Autheris.Tests.Unit` unless stated)
- `Tests.Architecture/ArchitectureNoSourceMetadataReadsTests` [S]:
  - `No_source_metadata_read` (sec plan 11.2 row 44)
  - `EveryPattern_HasPositiveSample`
  - `Allowlist_EntriesExistAndStillMatch` (a stale entry fails)
  - `NoCommandBehaviorKeyInfoOrDeriveParameters`
- `Sql/SourceMetadataGuardTests` [S]:
  - `Rejects_InformationSchema_AllDialects` (Theory over 5 dialects)
  - `Rejects_SqlServerSysAndMetadataFunctions` (`sys.tables`, `[sys].[objects]`, `OBJECT_ID(`, `sp_help`)
  - `Rejects_PostgreSqlCatalog` (`pg_catalog.pg_class`, `"pg_catalog"."pg_attribute"`, `'t'::regclass`)
  - `Rejects_OracleDictionary` (`ALL_TAB_COLUMNS`, `v$session`, `DBMS_METADATA.GET_DDL`)
  - `Rejects_DatabricksSystemAndStatements` (`system.information_schema.tables`, `SHOW TABLES`, `DESCRIBE t`)
  - `Rejects_ExplainShowplanDbcc`
  - `Allows_LiteralsAndCommentsContainingCatalogNames` (`WHERE a = 'sys.tables'`, `-- information_schema`)
  - `Allows_UserTablesNamedLikeUsersOrderStats` (`USER_PROFILES`, `orders_stats`)
  - `Rejects_UnterminatedLiteralOrComment`
  - `Exception_CarriesRuleIdNotSql`
- `Persistence/MetadataGuardedConnectionTests` [S]:
  - `RuntimeGuard_denies_system_catalog_reads_and_never_logs_sql` (sec plan 11.2 row 44; fake inner command counts executions = 0, no SQL in logs)
  - `ExecuteNonQueryScalarPrepare_AllGuarded`
  - `SessionPins_RunOnRawConnection_NotRejected`
  - `Violation_EmitsAuditAndWarning_WithoutSql`
  - `Factory_ReturnsGuardedConnection_ForEveryDialect`
- `Persistence/SqlConnectionFactoryNpgsqlTests`: `PostgreSql_ConnectionString_UsesNoTypeLoading`.
- `Common/DataAccessErrorClassifierDriftTests`:
  - `Classifies_SchemaMismatch_PerDialect`
  - `Classifies_PermissionDenied_PerDialect`
  - [S] `Mapper_Returns502_GenericMessage_NoObjectNames`
- `Sql/GovernedCatalogProviderTests` (rewrite):
  - [S] `Admission_UsesDeclaredColumnsOnly_NoConnectionOpened` (`ISqlConnectionFactory` absent from the ctor; a fake repository only)
  - `DeclaredTypeAndCollation_FlowIntoCatalogColumn`
  - [S] `FingerprintChanges_WhenDeclaredTypeOrCollationChanges`
  - [S] `UndeclaredTenantColumn_Denied`
  - [S] `NullCollationStringKey_NotBindable`
- `Catalog/CatalogDatasetDefinitionEndpointTests`:
  - [S] `Put_RequiresGovernanceAdmin`
  - `Put_CreatesThenReplaces_KeepsMaskingAndConsent`
  - [S] `Reserved_source_names_rejected` (sec plan 11.2 row 44; Theory: `sys`, `information_schema`, `pg_catalog`, `SYS`, `system`, `ALL_TABLES`)
  - [S] `Put_LimitsEnforced`
  - `Put_InvalidNativeType_Rejected`
  - [S] `Put_NeverOpensSourceConnection`
- `Catalog/CatalogSourceNameValidatorTests` [S]: `DbtOpenMetadataOpenApiUpserts_ReservedNames_Rejected`.
- `Procedures/ProcedureRowScopeKeyClosureTests`:
  - [S] `BuildKeyQuery_GroupsByKey_HavingAllRowsPassFilterAndTenant`
  - [S] `Non_unique_key_never_widens_access` (sec plan 11.2 row 44; PostgreSQL container in `Tests.Integration`)
  - `UniqueKey_SameResultAsBefore`
- `Procedures/ProcedureDeclaredOnlyTests`:
  - [S] `ValidationKey_Rejected`
  - [S] `MissingTablesOrOutputs_Pending`
  - [S] `UnregisteredTable_Pending`
  - [S] `RlsSessionContextWithRowFilters_RequiresRowScopeKey`
  - [S] `UndeclaredResultColumn_Dropped`
  - [S] `DeclaredSourceColumn_GovernedBySourceTable`
  - `AllowDeclaredValidationKey_FailsStartup`
- `Tests.Integration/SourceLeastPrivilegeContainerTests` [S] (Testcontainers; PostgreSQL and SQL Server):
  - Set up the role exactly as in `docs/operations/source-least-privilege-roles.md`.
  - `GovernedRead_Works_UnderTemplateRole`
  - `ProcedureCall_Works_UnderTemplateRole` (SQL Server, with `sp_set_session_context`)
  - `RowScopeClosure_Works_UnderTemplateRole`
  - `SqlServer_SysCatalogSelect_Denied` (raw connection, error 229)
  - `AutherisRequestTrace_ContainsNoCatalogStatement` (SQL Server: Extended Events session or `sys.dm_exec_requests` sampling read by the *admin* connection; PostgreSQL: `log_statement=all` captured from the container log, then grep for the step-1 patterns -> 0)
- `Sql/TrinoMetadataQueryHandlerTests` [S]: `MetadataQueries_NeverResolveSourceConnection` (a strict mock `ISqlConnectionFactory` with 0 calls).
- `Dbt/DbtMetaIndexesTests`:
  - `MetaIndexes_OnSource_DeclaresPkAndIndexes`
  - `MetaIndexes_WinsOverKeyTests`
  - [S] `InvalidEntry_DropsDeclaration_LogsNoValues`
  - `TwoPrimaryKeys_Rejected`
- `Catalog/DeletedPhysicalKeyRoutesTests` [S] (or entries in `DeletedFeaturesArchitectureTests`): `RefreshPhysicalKeys_BothRoutes_404`.
- Tests to delete, each with its security equivalent named, per SEC-SIMPL-03:
  - Unit: `Sql/KeyIndexMetadataReaderTests`, `Sql/ColumnMetadataReaderTests`, `Sql/GovernedCatalogProviderPhysicalKeysTests`, `DataCatalog/PhysicalKeyRefreshServiceTests`, `DataCatalog/PhysicalNonUniqueIndexTests`, `DataCatalog/Issue403PhysicalKeyReconcilerTests`, `DependencyInjection/Issue404PhysicalIndexWiringTests`, `Governance/CatalogPhysicalIndexStoreTests`, `Catalog/PhysicalKeyRefreshEndpointTests` (-> `DeletedPhysicalKeyRoutesTests`), `Catalog/Issue404SyncThenDescribeTests`.
  - Integration: `CatalogMetadataReaderTests`, `KeyIndexMetadataReaderContractTests`, `KeyDirectionSyncPathContainerTests`, `PhysicalKeyRefreshEndpointAuthIntegrationTests` (-> 404 test).
  - Extensions.Tests: `Issue403DataCatalogSyncDirectionTests`, `Issue404DataCatalogSyncPhysicalIndexTests`.
  - Procedure catalog-validator tests -> `ProcedureDeclaredOnlyTests`.
- Tests to adapt:
  - `Dbt/DbtPhysicalKeyIngestionTests` -> `DbtMetaIndexesTests`.
  - `GraphQL/Issue421IndexAwarePricingTests` and `Mcp/DatasetIndexVisibilityLeakTests`: declared indexes only.
  - DI snapshots `composition-*.txt`.
  - `Security/ActivationProbes.cs`.
  - The `GovernedCatalogProviderDiagnosticsTests` and `GovernancePolicyFactoryTests` constructors.

## 7. Verification commands (one build at a time)
```bash
dotnet build Autheris.sln -c Release -warnaserror
dotnet test tests/Autheris.Tests.Architecture -c Release --no-build --filter "FullyQualifiedName~ArchitectureNoSourceMetadataReadsTests|FullyQualifiedName~DeletedFeatures"
dotnet test tests/Autheris.Tests.Unit -c Release --no-build --filter "Category=Security"
dotnet test tests/Autheris.Tests.Unit -c Release --no-build --filter "FullyQualifiedName~SourceMetadataGuard|FullyQualifiedName~MetadataGuardedConnection|FullyQualifiedName~GovernedCatalogProvider|FullyQualifiedName~CatalogDatasetDefinition|FullyQualifiedName~ProcedureRowScopeKeyClosure|FullyQualifiedName~ProcedureDeclaredOnly|FullyQualifiedName~DbtMetaIndexes"
dotnet test tests/Autheris.Tests.Integration -c Release --no-build --filter "FullyQualifiedName~SourceLeastPrivilegeContainerTests"
git grep -nE "IColumnMetadataReader|IKeyIndexMetadataReader|IPhysicalKeyCatalog|PhysicalIndexes|refresh-physical-keys|StoredProcedureCatalogValidator|ProcedureValidationMode|AllowDeclaredValidation" -- src   # expect: no output
git grep -nE "sys\.(indexes|columns|objects|parameters)|pg_index|information_schema\.columns|ALL_TAB_COLUMNS" -- src ':!src/Autheris.Infrastructure/Persistence/Migrations' ':!*GovernanceSchemaValidator.cs'   # expect: denylist/emulation files only
```

## 8. Acceptance criteria
- [ ] `ArchitectureNoSourceMetadataReadsTests` is green. Its allowlist holds only the governance-DB exclusions, the CDC entry, the guard and the denylist/emulation files, each with a reason.
- [ ] Every data-source connection from `ISqlConnectionFactory` is a `MetadataGuardedConnection`, and a catalog statement on it throws before reaching the driver (test).
- [ ] `GovernedCatalogProvider` has no `ISqlConnectionFactory` dependency and admits tables from declared metadata only.
- [ ] Both `refresh-physical-keys` routes return 404, and `TableMetadata.PhysicalIndexes` no longer exists.
- [ ] dbt `meta.indexes` declares PK and indexes as in #459, and #459 can be closed by the PR.
- [ ] Procedures: `StoredProcedureCatalogValidator` is deleted, the `validation` YAML key is rejected, `AllowDeclaredValidation` fails startup, and row scope uses key closure.
- [ ] `PUT /api/v1/catalog/datasets/{id}/definition` creates and replaces declared tables, and reserved source names are rejected on every ingestion path.
- [ ] Drift returns 502 `SOURCE_SCHEMA_MISMATCH` or `SOURCE_PERMISSION_DENIED` with no object names, and there is no automatic discovery.
- [ ] `SourceLeastPrivilegeContainerTests` are green on PostgreSQL and SQL Server, and the trace shows 0 catalog statements from Autheris.
- [ ] `docs/operations/source-least-privilege-roles.md` exists (template below) and is linked from the data-source configuration docs, without status notes.
- [ ] No security test is lost without a named equivalent (SEC-SIMPL-03 list in the PR body).

## 9. PR slicing (<= 400 net added src lines each; deletions unlimited)
1. PR 1 Guard (+~380): steps 1 (test red, marked `Skip` until PR 4 only for the not-yet-removed items, with the allowlist temporarily listing them and the reason "removed in PR 2-4"), 2, 3, 4, 5.
2. PR 2 Declared catalog (+~350, -~450): steps 6, 7, 8 and the deletion of the column readers.
3. PR 3 Procedures (+~150, -~650): steps 9, 10. Rebased on WP-SIMPL-39.
4. PR 4 #459 (+~200, -~1,500): steps 11, 12, the final allowlist and the operator doc. Closes #459.

## 10. Risks, rollback, and things the developer must NOT do
- Risk: `NoTypeLoading` stops Npgsql from reading PostgreSQL enum, domain, composite and custom array types. The operator doc says to expose them as text through a view. Rollback is per data source only by reverting PR 1; there is no config switch (D5).
- Risk: declared types that are wrong produce source errors instead of silent admission. This is intended, and they are mapped to 502 drift codes.
- Risk: the key-closure query scans the key groups touched by the batch. Its cost is similar to today's key query plus a GROUP BY, and the batch size is unchanged.
- Risk: operators lose `refresh-physical-keys`. The migration guide (WP-SIMPL-34) points to `PUT .../definition`, `PUT .../keys` and dbt `meta.indexes`.
- Do NOT:
  - add an "import schema from source", "test schema" or "validate against source" feature;
  - keep any reader behind a flag;
  - put SQL, object or column names into errors, audit details or logs (rule ids and dataset ids only);
  - add an `Unwrap` to the guarded connection;
  - wrap governance-DB connections;
  - compute statistics, row counts or samples from sources;
  - weaken the row-scope check to "declared key is unique";
  - run more than one `dotnet build` at a time.

## 11. Estimate
Src: about +1,080 / -2,650 (net about -1,570). Tests: about +1,100 / -2,300. Effort: 6 person-days (PR 1 1.5, PR 2 1.5, PR 3 1.5, PR 4 1.5).

## Operator template (content for `docs/operations/source-least-privilege-roles.md`)
Grant per governed object, never per database or catalog. The data login below is read-only. Writes use a separate login configured as `WriteConnection` ([WP-SIMPL-45](WP-SIMPL-45-governed-write-access.md), OD-25/OD-26) that holds only the privileges listed by `GET /api/v1/admin/datasources/{name}/required-write-privileges` (column-level `INSERT`/`UPDATE` on tables with active write grants, `DELETE` only where a delete grant exists); it never holds catalog privileges either.
- **PostgreSQL**:
  ```sql
  CREATE ROLE autheris_data NOLOGIN;
  GRANT CONNECT ON DATABASE sales TO autheris_data;
  GRANT USAGE ON SCHEMA core TO autheris_data;
  GRANT SELECT ON core.orders, core.v_orders TO autheris_data;
  GRANT EXECUTE ON FUNCTION core.fn_orders_for(integer) TO autheris_data;
  CREATE ROLE autheris_svc LOGIN PASSWORD '<secret>' IN ROLE autheris_data;
  ALTER ROLE autheris_svc SET default_transaction_read_only = on;
  ```
  - Never grant `pg_read_all_data`, `pg_read_all_stats`, `pg_read_all_settings`, `pg_monitor` or superuser.
  - PostgreSQL always exposes `pg_catalog` to every role. That cannot be revoked per role, so for PostgreSQL OD-25 is enforced by Autheris: the guard and no type loading.
- **SQL Server**:
  ```sql
  CREATE USER autheris_svc FOR LOGIN autheris_svc;
  CREATE ROLE autheris_data;
  GRANT SELECT ON OBJECT::core.Orders TO autheris_data;
  GRANT EXECUTE ON OBJECT::core.usp_Orders TO autheris_data;
  ALTER ROLE autheris_data ADD MEMBER autheris_svc;
  DENY VIEW DEFINITION TO autheris_data;
  DENY VIEW DATABASE STATE TO autheris_data;
  DENY SELECT ON SCHEMA::sys TO autheris_data;
  DENY SELECT ON SCHEMA::INFORMATION_SCHEMA TO autheris_data;
  ```
  - Keep `EXECUTE` on `sys.sp_set_session_context` (the public default; ADR-018).
  - Never use `db_datareader`, `db_owner`, `VIEW ANY DEFINITION` or `VIEW SERVER STATE`.
  - CDC module only: `VIEW CHANGE TRACKING` on the tracked tables.
- **Oracle**:
  ```sql
  CREATE USER autheris_svc IDENTIFIED BY "<secret>";
  GRANT CREATE SESSION TO autheris_svc;
  CREATE ROLE autheris_data;
  GRANT READ ON core.orders TO autheris_data;
  GRANT autheris_data TO autheris_svc;
  ALTER USER autheris_svc DEFAULT ROLE autheris_data;
  ```
  - Use `READ`, not `SELECT`, so no `FOR UPDATE` locks are possible.
  - Never grant `SELECT ANY DICTIONARY`, `SELECT_CATALOG_ROLE`, `SELECT/READ ANY TABLE`, `DBA` or `RESOURCE`.
  - `ALL_*` views list only granted objects and cannot be revoked from PUBLIC safely, so they are guarded by Autheris.
  - Oracle procedures stay rejected (WP-39 D3).
- **Databricks (Unity Catalog)**:
  ```sql
  GRANT USE CATALOG ON CATALOG sales TO `autheris-svc`;
  GRANT USE SCHEMA ON SCHEMA sales.core TO `autheris-svc`;
  GRANT SELECT ON TABLE sales.core.orders TO `autheris-svc`;
  ```
  - Grant `CAN USE` on the SQL warehouse only.
  - Never grant `BROWSE`, `ALL PRIVILEGES`, `MANAGE`, ownership, or any privilege on catalog `system`.

## Phase 3 conditions (binding, sec plan section 11.2, row 44)
- SEC-SIMPL-140 (no source metadata read): the architecture test is a required check, and allowlist changes need a CODEOWNERS security review.
- SEC-SIMPL-141 (runtime guard fail closed): every `ISqlConnectionFactory` connection is guarded. Lexer errors are violations. The violation path never executes the inner command and never logs SQL.
- SEC-SIMPL-142 (declared metadata only): the compiler catalog opens no source connection, the fingerprint covers declared type and collation, and a string key with unknown collation is not bindable.
- SEC-SIMPL-143 (reserved source names): no catalog ingestion path can register a system schema, a system catalog or a dictionary view as a governed table.
- SEC-SIMPL-144 (row scope without uniqueness proof): key closure over tenant and row filter. A non-unique key never widens access (container test).
- SEC-SIMPL-145 (procedures declared): the declared referenced tables must be registered. `rls: session_context` on row-filtered tables requires `row_scope_key`. Undeclared result columns are dropped.
- SEC-SIMPL-146 (drift errors): 502 with a generic message, no object names, no automatic re-discovery and no fallback to a source read.
- SEC-SIMPL-147 (least privilege proven): container tests run the governed paths under the documented roles, and the SQL Server catalog select is denied.

## Decisions taken in this spec
- D1 Core ingestion: the catalog API is core (`PUT .../definition`, `PUT .../keys`, WP-41 `discovery-artifacts`). dbt and OpenMetadata stay modules (WP-28) as optional feeders. There is no conflict with OD-25, because a deployment without modules can still register everything through the catalog API. This resolves the plan's "catalog import API in core stays the integration point" (WP-28), which had no schema upload until now.
- D2 Guard placement: a connection decorator at the single factory, instead of a call per execution site. It covers all 16 `CreateOpenConnectionAsync` call sites, including raw SQL paths (row-filter key sets, virtual-filter subqueries, attribute tables, procedures). The compiler's unknown-table rejection remains the first line.
- D3 Procedures, a conflict with WP-39 D3 resolved by OD-25: SQL Server catalog validation (signature, browse-mode sources, `sys.security_policies` presence) is removed. Signatures, outputs, column sources and referenced tables are declared. The unverifiable database SECURITY POLICY becomes defense in depth, and Autheris-side key closure is mandatory where row filters apply.
- D4 Row-scope uniqueness (#459 and the SEC-94-10 allowlist reason) is superseded by OD-25. Key closure gives the same guarantee without any metadata.
- D5 Npgsql `NoTypeLoading` on data-source connections, because driver type loading is a `pg_catalog` read issued on Autheris's behalf.
- D6 CDC `CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@t))` (module, WP-29) is the only allowlisted metadata function. It resolves only a declared, granted object, needs no catalog privilege (`VIEW CHANGE TRACKING`) and has no data-only alternative. Decision taken: allowlisted (default; the sec plan section 11.2 row 44 adopts SEC-SIMPL-140..147 with this allowlist, and allowlist changes need a CODEOWNERS security review).
- D7 Lakehouse: reading the Delta log or Iceberg metadata is reading the table format and is out of scope. The module must still reject columns that are not declared.
- D8 Drift: no auto-discovery and no cache-repair read. Mismatch and permission errors map to 502 with stable codes. The fix is a new upload.
- D9 This WP uses the condition ids SEC-SIMPL-140..147, a range no other WP uses.
