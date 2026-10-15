# Implementation Plan: REQ-GOV-94 - Schema Shape Verification, Declared Keys/Indexes and Key-Based Read Ordering

**Document ID:** `PLAN-GOV-94` (revision 2, 2026-10-15)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [PRD REQ-GOV-94](2026-10-15-req-issue-94-upgrade-migration-and-pk-ordering.md), sections 0, 0.1 and 0.2 (owner decisions of 2026-10-15)
**Branch:** `fix/issue-94-upgrade-migration-pk`
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Status:** IMPLEMENTED (Phases 1–6 complete; PR pending). Feature docs: [F-GOV-19](../features/f-gov-19-declared-table-keys-and-read-ordering.md), [F-GOV-12](../features/f-gov-12-multi-engine-governance-storage.md#schema-shape-verification)

---

## 0. Binding Owner Decisions

| # | Decision | Effect |
|---|---|---|
| OD-1 | No upgrade/repair migrations (no `0002`, no `ALTER`, rename or backfill). The client (PoC "Talos") supplies governance data in the newest schema. | Item 1 = read-only shape verification. |
| OD-2 | `0001` checksum unchanged. | The checksum is the constant identity `ComputeChecksum("<engine>:0001:baseline_governance_schema:v1")`, so the baseline DDL can be extended without changing it (OD-5). |
| OD-3 | Duplicate `DATA_OWNERS.ad_sid` is moot. | No merge. |
| OD-4 | Open questions resolved by best practice. | Q1 -> D-2/D-3; Q2 -> D-10/D-11, section 9. |
| OD-5 | Primary keys and indexes (composite, ordered, unique flag) are part of the table definition, supplied by the caller, persisted in the extended baseline, validated fail-closed. | D-6 to D-8, Stream A (DDL) and Stream B. |
| OD-6 | Minimal rights: read only on sources, no source catalog queries for keys; declared definition is the only key source. `Mode=Verify` works without DDL rights. | D-9, D-12, section 8. Runtime source key discovery (revision 1) is dropped. |

Note for the record: extending the DDL of a released step while keeping its checksum contradicts the guidance in the `MigrationStep` XML doc ("add a new, higher version instead"). It is done here by explicit owner decision; Stream A updates that comment to say that the baseline is extended in place while no installation is in production, and that existing databases are checked by the shape verifier.

---

## 1. Verification of the PRD Against the Code

| PRD claim | Result |
|---|---|
| RC-1 (v1.1.8 shims removed; baseline is `CREATE ... IF NOT EXISTS` only; `0001` recorded without shape check) | **Confirmed.** PostgreSQL v1.1.8 only added `applicable_scopes` and deduplicated owners; SQL Server v1.1.8 guarded `applicable_scopes`/`meta_json` and *deleted* duplicate owners; the `ACCESS_PROFILES` rename existed only on SQLite. |
| AC-A1.2 reachable with a `0002` | **Was not** (now moot): on a pre-shim SQLite database `0001` fails itself (`idx_audit_seq` on a missing `seq`). D-4 turns that into the drift report. |
| RC-2 line references | **Confirmed.** |
| OpenAPI ingestion "computes keys" | It **guesses** them (`required` properties, then `id`/`*_id`, then the first column). Removed (D-8). |
| `CatalogColumnAsset.IsPrimaryKey` | Exists but is **ignored** by `DataCatalogSyncService` (not in the PRD). Mapped in D-8. |
| Item 3: WebSQL adds a key ordering (`TreeSqlCompiler.cs:287`) | **Wrong.** `TreeSqlCompiler` is the GraphQL tree compiler. WebSQL (`IGovernedSqlExecutionService`) adds no ordering; only `SqlServerDialectGenerator` emits `ORDER BY (SELECT NULL)` for `OFFSET` without `ORDER BY`. |
| WebSQL 504 body | Ad-hoc JSON (`error`, `message`, `traceId`), not RFC 9457 problem details (D-11). |

---

## 2. Architecture Decisions

### D-1 Read-only shape verification inside the migration runner
- `GovernanceMigrationRunnerBase` gets the engine's `GovernanceSchemaShapeVerifier` from the concrete runner (no DI change).
- **Apply:** after the last pending step and **before** its version row is inserted. With no pending step, after the checksum checks. A wrong shape throws `GovernanceSchemaDriftException`; the version is not recorded; a database that already records `0001` fails every start.
- **Verify:** after the pending-migrations check, the shape is verified, **and the existing `VerifyPostConditionsAsync` (audit triggers) now also runs in Verify mode** (read-only catalog queries). No DDL, no write.
- **Skip:** unchanged.
- SQLite: one `BEGIN IMMEDIATE` run, a failure rolls back everything. PostgreSQL/SQL Server: `0001` stays non-transactional; its idempotent `CREATE ... IF NOT EXISTS` may remain, the version row is not written.
- Rejected: separate hosted service (runs after recording), Verify-only checking (the reporter's database would start), checks in repository constructors (I/O-free constructor rule).

### D-2 Expected shape = generated snapshot of the baseline per engine (Q1)
A test applies the catalogue to a fresh database, reads the system catalog with the verifier's reader and compares it with the embedded `governance-shape.<engine>.json`. The test generates the snapshot (on mismatch it writes the actual JSON and fails with the path); nobody edits it by hand; the comparison in this test is strict equality. SQLite in the unit project, PostgreSQL/SQL Server on the Testcontainers fixtures. Rejected: runtime scratch database (needs `CREATE DATABASE` on PG/MSSQL), DDL parsing, hand-written lists.

### D-3 Compared objects (Q1)
| Object | Compared | Tolerated / not compared |
|---|---|---|
| Table | existence (case-insensitive) | extra tables (one Information log) |
| Column | existence; type family (`string`, `integer`, `decimal`, `float`, `boolean`, `datetime`, `binary`, `json`, `other`; SQLite affinity via `SqliteTypeAffinity`) | length, nullability, defaults, extra columns |
| Primary key | ordered column set | constraint name |
| Index | every explicitly named index: existence, uniqueness, ordered columns (SQLite `origin='c'`; PG not constraint-owned; MSSQL `is_primary_key=0 AND is_unique_constraint=0`) | auto-named constraint indexes, extra indexes |
| Not checked | triggers (post-conditions), foreign keys, check constraints, data | |

### D-4 Diagnostic report when a step fails
If a step throws a `DbException`, the runner runs the read-only verifier on the same connection and throws `GovernanceSchemaDriftException` (deviation list, original exception as `InnerException`). If the verifier fails too, the original exception propagates.

### D-5 Message format (AC-A2.2)
Deterministic (kind, table, name), at most 100 lines plus `... and N more`, one Error log:
```
Governance schema drift on SQLite database 'gov.db': the schema does not match this build (applied migrations: 0001; pending: none).
  missing table      TABLE_PRIMARY_KEY_COLUMNS
  missing column     CONSENT_COLUMN_RULES.applicable_scopes (expected TEXT, family string)
  wrong primary key  ACCESS_PROFILES (expected tenant_id, profile_id; found id)
  wrong type         TABLES.data_source_type (expected family integer; found string)
  missing index      AUDIT_LOG_ENTRIES.idx_audit_seq (expected UNIQUE (seq))
Autheris does not upgrade older governance schemas. Load the governance data in the current schema or restore the matching backup; see docs/features/f-gov-12-multi-engine-governance-storage.md#schema-shape-verification.
```
Database label: SQLite file name, PG/MSSQL `DbConnection.Database`; never connection string, host or user. `GovernanceSchemaDriftException.Deviations` (`IReadOnlyList<SchemaDeviation>`). Telemetry tag `autheris.governance.schema.deviations`.

### D-6 Persistence model for declared keys and indexes (OD-5)
Three normalized tables appended to the baseline DDL of all three engines. Columns are referenced by `TABLE_COLUMNS.id` (rename-safe, existence checked by join). Ordinals are 1-based.

| Option | Verdict |
|---|---|
| `TABLE_PRIMARY_KEY_COLUMNS` + `TABLE_INDEXES` + `TABLE_INDEX_COLUMNS` | **Chosen.** One uniform, normalized model for keys and indexes; composite by construction; DB constraints enforce ordinal and column uniqueness. |
| `TABLE_COLUMNS.key_ordinal` | Rejected: covers the primary key only; uniqueness needs filtered/partial indexes that differ per engine; indexes would still need their own tables. |
| JSON column on `TABLES` | Rejected: no relational integrity; Talos loads the store directly, so the schema must be self-describing. |

No database-level foreign keys (consistent with the other catalog tables); integrity is enforced by the validator on write and on read (D-7).

### D-7 Domain model and validation (fail closed)
- `TableMetadata.PrimaryKeyColumns`: ordered, **default empty** (the `["id"]` default and the PG/MSSQL hard-coded `["id"]` are removed).
- `TableMetadata.Indexes`: `IReadOnlyList<TableIndexDefinition>`, default empty.
- `TableMetadata.ReplaceKeyDefinition` (write intent, default `false`): the upsert replaces the stored definition **only** when `true`; otherwise it keeps it. Rationale: many paths build a new `TableMetadata` (data catalog sync, governance ratchet, OpenMetadata merge) and would otherwise wipe a definition they never knew. Clearing = `true` with empty lists. Rejected: "non-empty means replace" (a path that copies the key but not the indexes would wipe indexes); a separate repository method (two transactions, two epoch bumps).
- `TableKeyDefinitionValidator.Validate(TableMetadata)` (Domain, pure) returns a normalized definition (catalog spelling) or throws `TableDefinitionException : ArgumentException` (mapped to 400). Rules: every column exists in `Columns` (case-insensitive, unique match); no duplicate column within a key or an index; index has 1..32 columns; key has 1..32 columns when present; at most 64 indexes; index name `^[A-Za-z_][A-Za-z0-9_]{0,127}$`, unique per table case-insensitive. A unique index identical to the primary key is allowed but ignored for ordering.
- Read side: missing ordinal (gap), duplicate ordinal or a column id that does not belong to the table -> the table's metadata is invalid: Warning log (table id and rule, no values) and the table is skipped like other invalid catalog rows (fail closed, never served with a partial key).
- Repositories validate again before persisting (defense in depth; the API validation gives the 400 earlier).

### D-8 Table-definition entry points
| Entry point | Change |
|---|---|
| `IGovernanceRepository.UpsertTableMetadataAsync` (SQLite/PG/MSSQL) | replace definition in the same transaction when `ReplaceKeyDefinition`; read it in `GetTableMetadataAsync` and the SQLite bulk `GetAllTablesAsync`. |
| New admin endpoint `PUT /api/v1/catalog/datasets/{id}/keys` (`GovernanceAdmin`, `AuditLevel.Full`) | body `{ "primaryKey": ["a","b"], "indexes": [{ "name": "ix_x", "columns": ["c","d"], "unique": true }] }`; 400 on validation failure; 404/403 as the existing dataset endpoints. |
| Data catalog sync (`CatalogTableAsset`, `DataCatalogSyncService`) | `CatalogTableAsset.PrimaryKeyColumns` and `Indexes` (new); if empty, the primary key is taken from `CatalogColumnAsset.IsPrimaryKey` in column order; replace only when the asset supplies a definition. |
| OpenMetadata (`OpenMetadataSyncService`) | `tableConstraints`: `PRIMARY_KEY` -> key, `UNIQUE` -> unique index named `uq_<n>` (1-based by position); replace only when constraints are present. |
| dbt (`DbtArtifactStreamingParser`, `DbtMetadataIngestionService`) | model and column `constraints` of type `primary_key` / `unique` (the parser already reads `foreign_key`). |
| OpenAPI ingestion (`OpenApiIngestionService`) | vendor extensions `x-autheris-primary-key` (string array) and `x-autheris-indexes` on the schema object; the `required`/`id`/first-column heuristic is removed. |
| Direct load by the client into the governance store | the three tables are documented in F-GOV-12 (Phase 6); read-side validation (D-7) protects against bad rows. |
| `VirtualSystemTables` | in-code keys unchanged. |
| MCP | no table-definition tools exist; no change. |
| `AccessPlanningService`, `CatalogGovernanceRatchet`, dbt `with` copies | unchanged (flag stays `false`, definition kept). |

### D-9 No source catalog queries for keys (OD-6)
Revision 1's `ISourceKeyDiscovery`/`ISourceKeyReader` are dropped. The declared definition is the only key source. An architecture test forbids `sys.indexes`, `sys.index_columns`, `pg_index`, `pg_constraint`, `ALL_CONSTRAINTS`, `ALL_INDEXES`, `duckdb_constraints`, `pragma_index_list` and `information_schema.(table_constraints|key_column_usage)` in `src/Autheris.Infrastructure/Persistence/Catalog/**` and in the Application data/OData/catalog code.

### D-10 Default ordering and tiebreaker (AC-B2.2', Q2)
Pure static `DefaultReadOrdering.Resolve(metadata, decision)`:
1. **PrimaryKey:** declared primary key, all columns `Clear`.
2. **UniqueIndex:** among declared unique indexes whose columns are all `Clear`, the one with the fewest columns (ties: definition order).
3. **ClearColumnComposite:** all `Clear` columns with an orderable type on `metadata.Dialect`, catalog order.
4. **None.**

Orderable types: deny-list per dialect; unknown types count as orderable. Not orderable: `json/jsonb`, `xml`, binary (`binary/varbinary/bytea/blob/image/raw/long raw`), LOBs (`text/ntext/image/clob/nclob` on SQL Server and Oracle, any `(max)` type on SQL Server), spatial (`geometry/geography/hierarchyid/sdo_geometry`), `sql_variant`, complex types (`array/map/struct/variant/list/union`). `text` stays orderable on PostgreSQL, SQLite and DuckDB.

Explicit order: validation unchanged (masked/denied rejected); the default ordering columns that are not already named are appended ascending as tiebreaker. `None`: `offset > 0` -> 400 `no orderable column readable in clear text`; `offset = 0` is read without `ORDER BY`, and if the look-ahead row shows more rows the transport returns the same 400 (refuse rather than return pages that duplicate or skip rows). No cap on the composite (sort cost is driven by rows; bounded by the execution timeout; remedy: declare a key). A declared unique index is trusted as stated; columns that hold NULL may tie (documented; recommend NOT NULL columns). Rows that tie on all orderable clear columns can still move across pages (documented limitation; fully duplicate rows keep the multiset exact).

Row identity for catalog consumers: `TableRowIdentity.Select(metadata)` = declared primary key, else the unique index with the fewest columns, else none (no access filtering; used for reporting only). CSDL `<Key>`: row identity, else all columns (existing documented composite; the `id` guess is removed).

### D-11 Paging and timeouts (Q2)
- `@odata.nextLink` whenever the look-ahead row proves more rows (existing mechanism); tested for keyed, unique-indexed and unkeyed tables.
- WebSQL 504 becomes RFC 9457 problem details (`application/problem+json`, `type`, `title`, `status`, `detail` = existing generic message, `traceId`). OData keeps the OData error format (protocol requirement). REST data API keeps its contract (verified 504 with a stable code).

### D-12 Least privilege (OD-6)
- Source reads need `SELECT` on governed objects only. The existing column-metadata readers (`sys.columns`/`sys.types`, `pg_catalog.pg_attribute`, `ALL_TAB_COLUMNS`, `pragma_table_info`, `information_schema.columns` on Databricks) remain: they are catalog **reads** that the engines expose for objects the login can select (metadata visibility), need no extra grant, and are required for the compiler's type/collation safety (SEC-X1-09). They are proven by the least-privilege test (AC-LP1a). Flagged for the owner in section 8.
- Governance store: `Mode=Verify` is the runtime mode for least privilege; `Mode=Apply` is for a migration job with a DDL login (`GovernanceDb:MigrationConnectionString`). Verify mode reads only: `autheris_schema_migrations`, the system catalog, triggers. The schema lock (`pg_advisory_lock`, `sp_getapplock`) needs no grant beyond `public`.

---

## 3. Contracts (fixed up front)

### 3.1 Governance schema (Stream A creates, Stream B reads/writes)
SQLite (PostgreSQL identical with lower-case index names; SQL Server `NVARCHAR(128)` ids/names, `INT` ordinals, `BIT` flag, `IF OBJECT_ID(...) IS NULL` guards, `dbo.`):
```sql
CREATE TABLE IF NOT EXISTS TABLE_PRIMARY_KEY_COLUMNS (
    table_id TEXT NOT NULL,
    key_ordinal INTEGER NOT NULL CHECK (key_ordinal >= 1),
    table_column_id TEXT NOT NULL,
    PRIMARY KEY (table_id, key_ordinal)
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_TABLE_PK_COLUMNS_COLUMN ON TABLE_PRIMARY_KEY_COLUMNS (table_id, table_column_id);

CREATE TABLE IF NOT EXISTS TABLE_INDEXES (
    id TEXT PRIMARY KEY,
    table_id TEXT NOT NULL,
    index_name TEXT NOT NULL,
    is_unique INTEGER NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_TABLE_INDEXES_NAME ON TABLE_INDEXES (table_id, index_name);

CREATE TABLE IF NOT EXISTS TABLE_INDEX_COLUMNS (
    index_id TEXT NOT NULL,
    column_ordinal INTEGER NOT NULL CHECK (column_ordinal >= 1),
    table_column_id TEXT NOT NULL,
    PRIMARY KEY (index_id, column_ordinal)
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_TABLE_INDEX_COLUMNS_COLUMN ON TABLE_INDEX_COLUMNS (index_id, table_column_id);
```
Replace semantics (Stream B): in the upsert transaction, delete `TABLE_INDEX_COLUMNS` of the table's indexes, then `TABLE_INDEXES`, then `TABLE_PRIMARY_KEY_COLUMNS` of the table, then insert in list order (ordinal = position + 1). The existing epoch increment and cache invalidation cover the change.

### 3.2 Domain (Stream B creates in its first commit; Stream C consumes)
```csharp
// src/Autheris.Domain/Model/TableModels.cs
public sealed record TableIndexDefinition(string Name, IReadOnlyList<string> Columns, bool IsUnique);
// TableMetadata:
public IReadOnlyList<string> PrimaryKeyColumns { get; init; } = Array.Empty<string>();
public IReadOnlyList<TableIndexDefinition> Indexes { get; init; } = Array.Empty<TableIndexDefinition>();
public bool ReplaceKeyDefinition { get; init; }

// src/Autheris.Domain/Model/TableKeyDefinitionValidator.cs
public static class TableKeyDefinitionValidator
{
    public const int MaxColumnsPerKey = 32;
    public const int MaxIndexesPerTable = 64;
    /// Returns metadata with the definition in catalog spelling; throws TableDefinitionException.
    public static TableMetadata Validate(TableMetadata metadata);
}
public sealed class TableDefinitionException(string message) : ArgumentException(message);
```

### 3.3 Application ordering (Stream C creates)
```csharp
// src/Autheris.Application/Data/Services/DefaultReadOrdering.cs
public enum DefaultOrderingKind { Explicit, PrimaryKey, UniqueIndex, ClearColumnComposite, None }
public sealed record DefaultOrdering(IReadOnlyList<string> Columns, DefaultOrderingKind Kind);
public static class DefaultReadOrdering
{
    public static DefaultOrdering Resolve(TableMetadata metadata, TableAccessDecision decision);
    public static bool IsOrderableType(string dataType, DatabaseDialect dialect);
}
// src/Autheris.Application/Data/Services/TableRowIdentity.cs
public static class TableRowIdentity { public static IReadOnlyList<string> Select(TableMetadata metadata); }
// PlannedTableRead: trailing parameter DefaultOrderingKind Ordering = DefaultOrderingKind.Explicit
// TableQueryItems.Ordering = "TableQuery.Ordering" (response item set by SqlTableReadExecutor)
```

### 3.4 Shape verifier (Stream A internal)
```csharp
public sealed record SchemaDeviation(SchemaDeviationKind Kind, string Table, string? Name, string Expected, string? Found);
public enum SchemaDeviationKind { MissingTable, MissingColumn, WrongType, WrongPrimaryKey, MissingIndex, WrongIndex }
```

---

## 4. Files per Layer and Stream

### Stream A - Baseline DDL extension, shape verifier, governance least privilege
| File | Change |
|---|---|
| `src/Autheris.Infrastructure/Persistence/Migrations/{Sqlite,PostgreSql,SqlServer}BaselineSchema.cs` | append the three tables (3.1). Checksums untouched. |
| `.../Migrations/MigrationModel.cs` | `GovernanceSchemaDriftException.Deviations`; `MigrationStep` doc comment (section 0 note). |
| `.../Migrations/GovernanceMigrationRunnerBase.cs` | D-1, D-4; post-conditions also in Verify. |
| `.../Migrations/{Sqlite,PostgreSql,SqlServer}GovernanceMigrationRunner.cs` | wire verifier + database label. |
| `.../Migrations/Shape/` (new): `GovernanceSchemaShape.cs`, `GovernanceSchemaShapeComparer.cs`, `GovernanceSchemaShapeVerifier.cs`, `IGovernanceSchemaCatalogReader.cs`, `{Sqlite,PostgreSql,SqlServer}SchemaCatalogReader.cs`, `governance-shape.{sqlite,postgresql,sqlserver}.json` | D-2, D-3, D-5. |
| `src/Autheris.Infrastructure/Autheris.Infrastructure.csproj` | embedded resources. |

### Stream B - Declared definition: domain, validation, persistence, entry points
| File | Change |
|---|---|
| `src/Autheris.Domain/Model/TableModels.cs`, new `TableKeyDefinitionValidator.cs` | 3.2. |
| `src/Autheris.Infrastructure/Persistence/{Sqlite,PostgreSql,SqlServer}GovernanceRepository.Catalog.cs` | read/write definition, read-side validation, remove `["id"]`. |
| `src/Autheris.Api/Endpoints/CatalogApiEndpoints.cs` (+ request DTO) | `PUT /datasets/{id}/keys`. |
| `src/Autheris.Application/DataCatalog/Models/DataCatalogModels.cs`, `src/Autheris.Extensions/DataCatalog/DataCatalogSyncService.cs` | D-8. |
| `src/Autheris.Extensions/OpenMetadata/OpenMetadataSyncService.cs` (+ its model) | D-8. |
| `src/Autheris.Extensions/Dbt/DbtArtifactStreamingParser.cs`, `DbtMetadataIngestionService.cs` | D-8. |
| `src/Autheris.Application/DataCatalog/Services/OpenApiIngestionService.cs` | D-8. |

### Stream C - Ordering, consumers, transports, item 3, source least privilege
| File | Change |
|---|---|
| `src/Autheris.Application/Data/Services/DefaultReadOrdering.cs`, `TableRowIdentity.cs` (new) | 3.3. |
| `src/Autheris.Application/Data/Interfaces/IGovernedTableReadPlanner.cs`, `Data/Services/GovernedTableReadPlanner.cs` | D-10 incl. tiebreaker and `None`. |
| `src/Autheris.Application/Data/Services/GovernedDataQueryService.cs` | remove line-142 check; 400 for `None` with offset > 0 (also cross-source) or more rows. |
| `src/Autheris.Application/Services/SqlTableReadExecutor.cs`, `Interfaces/IGatewayExecutionService.cs` | `TableQueryItems.Ordering`. |
| `src/Autheris.Extensions/OData/ODataHandler.cs`, `ODataCsdlGenerator.cs` | copy `Indexes` in the filtered metadata (~line 176); 400 for `None` with more rows; CSDL key via `TableRowIdentity`. |
| `src/Autheris.Application/Catalog/Services/CatalogDiscoveryService.cs` (+ detail DTO `Indexes`), `Catalog/Search/CatalogSearchEngine.cs`, `Mcp/Services/McpDatasetCatalog.cs`, `OData/Services/DynamicOpenApiGenerator.cs` | report the declared definition. |
| `src/Autheris.Api/Endpoints/WebSqlEndpoints.cs` | 504 problem details. |

File ownership is disjoint. Shared test helpers are not edited; each stream adds its own test files and fixes the existing tests that its production change breaks (B: tests asserting the `["id"]` default; C: planner/OData/CSDL/catalog tests).

---

## 5. Sequencing (parallel start)

- **A1** (first commit of Stream A, push immediately): DDL of 3.1 on all three engines + `MigrationCatalogueChecksumTests`. Stream B's persistence steps (B4+) need it.
- **B1** (first commit of Stream B, push immediately): domain contract 3.2 without behaviour change other than the default. Stream C's planner steps (C2+) need `Indexes`.
- After A1 and B1 all three streams run fully in parallel. Developers: Sonnet `csharp-tdd-developer` agents (memory rule), strict red-green-refactor, push after every commit.

### Stream A steps
1. A1 (above). Red: checksum test + `GovernanceSchemaMigrationTests` case "fresh DB contains the three tables". Green: DDL.
2. Red: `GovernanceSchemaShapeComparerTests`. Green: model, comparer, formatter.
3. Red: SQLite catalog reader test. Green: reader.
4. Red: `GovernanceSchemaShapeSnapshotTests` (SQLite). Green: generated snapshot, embedded resource.
5. Red: `GovernanceSchemaVerificationTests` (cases in section 6). Green: runner hook, D-4, post-conditions in Verify.
6. Red: `PostgreSqlGovernanceSchemaShapeTests`, `SqlServerGovernanceSchemaShapeTests`. Green: readers, snapshots.
7. Red: `PostgreSqlGovernanceLeastPrivilegeTests`, `SqlServerGovernanceLeastPrivilegeTests` (AC-LP1 b/c). Green: whatever Verify mode still needs (expected: nothing beyond step 5).

### Stream B steps
1. B1 (above). Red: `TableMetadataDefaultsTests` (empty default; grep test forbids `["id"]`/`{ "id" }` in `TableModels.cs` and the three `Catalog.cs`). Green: domain contract.
2. Red: `TableKeyDefinitionValidatorTests` (every rule of D-7, composite cases, spelling normalization). Green: validator.
3. Red: `SqliteCatalogKeyDefinitionContractTests` (round trip composite PK + composite unique + non-unique index; keep vs replace; clear; bulk `GetAllTablesAsync`; corrupted rows -> table skipped). Green: SQLite repository. Remove PG/MSSQL `["id"]`.
4. Red: `PostgreSqlCatalogKeyDefinitionContractTests`, `SqlServerCatalogKeyDefinitionContractTests` (Integration). Green: PG/MSSQL repositories.
5. Red: `CatalogKeysEndpointTests` (200, 400 per rule, 403 non-admin, audit). Green: endpoint.
6. Red: entry-point tests (`DataCatalogSyncKeyDefinitionTests`, `OpenMetadataKeyConstraintTests`, `DbtKeyConstraintTests`, `OpenApiIngestionKeyExtensionTests`). Green: D-8 mappings; update `OpenApiIngestionServiceTests` heuristics.

### Stream C steps
1. Red: `DefaultReadOrderingTests` (PK, composite PK, unique index choice, masked key column `ssn` -> next rule, composite fallback, orderable types per dialect, `None`). Green: `DefaultReadOrdering`, `TableRowIdentity`.
2. Red: `GovernedTableReadPlannerTests` (AC-B2.1, tiebreaker, `None`). Green: planner, `PlannedTableRead.Ordering`.
3. Red: `GovernedDataQueryServiceTests` (AC-B2.2.3, B2.4). Green: service.
4. Red: `ODataNextLinkTests`, executor tests. Green: executor, `ODataHandler`.
5. Red: consumer tests (CSDL composite keys and unique-index key, OpenAPI, discovery `Indexes`, search, MCP). Green: consumers.
6. Red: `WebSqlTimeoutProblemDetailsTests`. Green: WebSQL 504.
7. Red: `ArchitectureNoSourceKeyCatalogQueriesTests` (D-9). Green: nothing expected.
8. Integration: `UnkeyedTableODataReadTests`, `UnkeyedTablePagingStabilityTests`, `WebSqlUnkeyedOrderByTests` (SQLite), `SqlServerUnkeyedTableReadTests`, `SqlServerSelectOnlySourceReadTests`, `PostgreSqlSelectOnlySourceReadTests` (AC-LP1a).

---

## 6. Test Strategy (AC mapping)

New integration classes live in namespace `Autheris.Tests.Integration` (CI shards them automatically) and use `PostgreSqlContainerFixture`/`MsSqlContainerFixture` (or `PostgreSqlTestDatabase`).

| AC | Test class | Project | Stream |
|---|---|---|---|
| AC-A2.1 snapshot | `GovernanceSchemaShapeSnapshotTests`; `PostgreSqlGovernanceSchemaShapeTests`; `SqlServerGovernanceSchemaShapeTests` | Unit; Integration | A |
| AC-A2.1 single drop | `GovernanceSchemaShapeComparerTests` (`[Theory]` over every table/column/index of each snapshot) | Unit | A |
| AC-A2.2 message | `GovernanceSchemaShapeComparerTests`, `GovernanceSchemaVerificationTests` (no `Data Source=`, `Password`, host) | Unit | A |
| AC-A2.3 | `GovernanceSchemaVerificationTests`: synthetic old shape from the v1.1.8 DDL (`tests/Autheris.Tests.Unit/Governance/Fixtures/governance-v1.1.8-preshim.sqlite.sql`, extracted with `git show v1.1.8:...`, shim columns removed, legacy `ACCESS_PROFILES(id, ...)`): (a) no version table -> drift, version table absent afterwards; (b) `0001` recorded -> drift every start, lists `applicable_scopes`, `ACCESS_PROFILES` key, the three new tables; (c) pre-shim `AUDIT_LOG_ENTRIES` -> `0001` fails, drift report with inner `SqliteException`. PG/MSSQL: pre-created `CONSENT_COLUMN_RULES` without `applicable_scopes` -> drift, no version row. | Unit; Integration | A |
| AC-A2.4 / A2.5 | `GovernanceSchemaVerificationTests` (extras tolerated + one log; Verify: no DDL, post-conditions checked; Skip unchanged) | Unit | A |
| AC-B1.1 | `TableMetadataDefaultsTests` | Unit | B |
| AC-B1.2' | `MigrationCatalogueChecksumTests` + shape tests | Unit; Integration | A |
| AC-B1.3' | `SqliteCatalogKeyDefinitionContractTests`; `PostgreSqlCatalogKeyDefinitionContractTests`; `SqlServerCatalogKeyDefinitionContractTests` | Unit; Integration | B |
| AC-B1.4' | `TableKeyDefinitionValidatorTests`; read-side cases in the contract tests | Unit; Integration | B |
| AC-B1.5' | `CatalogKeysEndpointTests`, `DataCatalogSyncKeyDefinitionTests`, `OpenMetadataKeyConstraintTests`, `DbtKeyConstraintTests`, `OpenApiIngestionKeyExtensionTests` | Unit | B |
| AC-B1.6' | `ArchitectureNoSourceKeyCatalogQueriesTests`; consumer tests (CSDL, OpenAPI, discovery, search, MCP) | Unit | C |
| AC-B2.1 | `GovernedTableReadPlannerTests`; `UnkeyedTableODataReadTests`; `SqlServerUnkeyedTableReadTests` (`fms.air1`, no key, no `id`) | Unit; Integration | C |
| AC-B2.2' / B2.3 | `DefaultReadOrderingTests` (composite PK, composite unique index, masked `ssn`) | Unit | C |
| AC-B2.2.3 / B2.4 | `GovernedDataQueryServiceTests`, OData handler tests | Unit | C |
| AC-B2.5 | `UnkeyedTablePagingStabilityTests` | Integration | C |
| AC-B2.6 | `ODataNextLinkTests` (keyed, unique-indexed, unkeyed) | Unit | C |
| AC-B2.7 | `GovernedTableReadPlannerTests` (tiebreaker, masked explicit column rejected) | Unit | C |
| AC-C1 | `WebSqlUnkeyedOrderByTests`; `WebSqlTimeoutProblemDetailsTests` | Integration; Unit | C |
| AC-LP1a | `SqlServerSelectOnlySourceReadTests`, `PostgreSqlSelectOnlySourceReadTests`: login with `SELECT` on one table only; governed read with a declared composite key returns ordered rows | Integration | C |
| AC-LP1b/c | `SqlServerGovernanceLeastPrivilegeTests`, `PostgreSqlGovernanceLeastPrivilegeTests`: schema applied by the admin login; runtime login with the grants of section 7 starts in Verify, runs initializer, catalog read/write with keys; Apply on an unmigrated database fails with a clear message | Integration | A |

---

## 7. Least-Privilege Grants (documented in Phase 6)

### Source databases (governed reads)
| Engine | Grants |
|---|---|
| SQL Server | `CONNECT`; `SELECT` on each governed table/view (or schema). |
| PostgreSQL | `CONNECT`; `USAGE` on the schema; `SELECT` on each governed table/view. |
| Oracle | `CREATE SESSION`; `SELECT` on each governed table/view. |
| SQLite / DuckDB | read-only file access (`Mode=ReadOnly`). |
| Databricks | `USE CATALOG`, `USE SCHEMA`, `SELECT`. |

### Governance database
| Login | SQLite | PostgreSQL | SQL Server |
|---|---|---|---|
| Runtime (`Mode=Verify`) | read/write on the file (WAL) | `CONNECT`, `USAGE` on the schema; `SELECT, INSERT, UPDATE, DELETE` on all governance tables except `AUDIT_LOG_ENTRIES` (`SELECT, INSERT`); `USAGE, SELECT` on the `AUDIT_LOG_ENTRIES` id sequence; `SELECT` on `autheris_schema_migrations` | `CONNECT`; `SELECT, INSERT, UPDATE, DELETE` on all governance tables except `AUDIT_LOG_ENTRIES` (`SELECT, INSERT`); `SELECT` on `autheris_schema_migrations` |
| Migration (`Mode=Apply`, `MigrationConnectionString`) | same file | owner of the schema objects (`CREATE` on the schema; creates functions and triggers) | `db_ddladmin` plus `ALTER` on `dbo` (trigger) |

---

## 8. Flags: Code That Needs More Than Read on Sources

| Code | Needs | Disposition |
|---|---|---|
| Column-metadata readers (`Persistence/Catalog/*ColumnMetadataReader.cs`) | catalog reads, visible with `SELECT` (no extra grant) | keep; required for compiler type/collation safety; proven by AC-LP1a. Owner may decide later to make them optional. |
| `StoredProcedureCatalogValidator` (`sys.objects`, `sys.parameters`, `sys.sql_modules`, dependencies) | `EXECUTE` on the procedure, `VIEW DEFINITION` to read the module text | feature-scoped (stored procedures only); document as an optional grant. |
| `SqlProcedureRowScopeResolver` (`sys.indexes`, `pg_index`) | index catalog reads on sources | **Kept** (Phase 3, SEC-94-10): it proves physical uniqueness for row-scope authorization; declared (unverified) indexes must not replace it. Needs only the metadata visibility granted by `SELECT`; allowlisted in the D-9 architecture test. |
| `MssqlChangeTrackingPoller` (`CHANGETABLE`, `sys.indexes`) | `VIEW CHANGE TRACKING`, index catalog read | CDC feature only; CDC key handling is owned by `impl-arch-r0-r2-trust-ha-eventing.md`; flagged (could use the declared primary key). |
| Governed DML (`Ast*Dml*`) | `INSERT/UPDATE/DELETE` | only when DML is enabled for a data source; document as optional. |
| Stored procedure execution | `EXECUTE` | optional feature. |

---

## 9. Item 3 Triage Result (AC-C2)

- Missing `@odata.nextLink`: consequence of RC-2 (500 before the page). Fixed by Stream C (AC-B2.6).
- WebSQL 504 after 10.6 s: WebSQL adds no ordering (section 1). A user `ORDER BY` on a non-indexed column of a large table sorts the full table and hits `WebSql:ExecutionTimeoutSeconds`. **By design**; no workaround. Runbook (Phase 6): add an index on the ordering column or raise the timeout. AC-C1 regression test; 504 as problem details (D-11). If the test shows an Autheris-generated clause, Stream C fixes it here.

---

## 10. Risks

| Risk | Mitigation |
|---|---|
| Snapshot and baseline diverge | strict-equality snapshot tests on all engines (D-2). |
| False drift on working databases | type family only; nullability/length ignored (D-3). |
| Existing databases lack the three new tables | expected and reported by the verifier; the client supplies the newest schema (OD-1). |
| Definition wiped by an ingestion path | replace only with `ReplaceKeyDefinition = true` (D-7). |
| Bad rows loaded directly by the client | read-side validation; table skipped and logged (D-7). |
| Composite ORDER BY slow on wide/large unkeyed tables | execution timeout; remedy: declare a key or unique index. |
| Behaviour changes (no `id` key; tiebreaker on explicit order; OpenAPI heuristic removed) | allowed (no production); release note. |

## 11. Phase-3 Review Focus
- Ordering oracle: default ordering and tiebreaker only on `Clear` columns.
- Definition validation on write and read; admin endpoint authorization and audit.
- Drift message content (no connection details, no data).
- Least-privilege grants and the flags in section 8.

## 12. Gate 2 Checklist
- [x] Layers: Domain (definition, validator) -> Application (ordering, consumers) -> Infrastructure (DDL, verifier, persistence) -> Api/Extensions (endpoints, ingestion, transports).
- [x] Hot path: ordering resolved once per request from in-memory metadata; definition loaded with the cached table metadata; no source round trip for keys.
- [x] Concrete steps (section 5), parallel streams with disjoint files and fixed contracts (section 3).

---

## 13. Security review (Phase 3)

**Reviewer:** `csharp-security-expert`, 2026-10-15, plan revision 2. The review checked the plan against the code on `fix/issue-94-upgrade-migration-pk` (base `574c82f1`); it did not rely on the plan's own claims.
**Verdict:** **Approved with conditions.** Every finding marked High or Medium is a **condition for Phase 4**: the owning stream implements the mitigation and the mandatory tests in 13.3. Phase 5 checks each condition. Low and Info items are to be fixed in scope where it says so; otherwise they are documented in Phase 6.

### 13.1 Binding security decisions (added to D-7, D-8 and D-10)

| # | Decision |
|---|---|
| SD-1 | **Ordering candidates are all-or-nothing.** A declared primary key or unique index is a candidate only if **every** column is `Clear` for the caller (`GetEffectiveColumnAccess`; this covers `Deny`, `Mask`, HMAC rules and catalog-sensitive columns that have no explicit `Clear`). A candidate with any non-`Clear` column is **skipped as a whole**, with no 400. A prefix or subset of a key or index is **never** used. A 400 here would tell the caller that the key contains a hidden column. `DefaultOrderingKind` stays internal (logs and telemetry only). It is never written to a response body, header or annotation. |
| SD-2 | **One ordering guard.** All three paths call `DefaultReadOrdering.Resolve` with the same `TableAccessDecision` that drives projection: the governed table-read planner, the GraphQL tree compiler (`TreeSqlCompiler.cs:280-292`) and the cross-source staging path. No path keeps its own rule. |
| SD-3 | **An invalid persisted definition is unavailable, never "absent".** On read, an invalid definition makes `GetTableMetadataAsync` throw a new `TableMetadataUnavailableException` (Domain, sealed, message contains only the table id) instead of returning `null`. `GetAllTablesAsync` leaves the table out and logs a Warning. Read paths map the exception to deny or 404; `GovernancePolicyFactory.LoadRecordAsync` already treats exceptions as "unusable". Write and ingestion paths stop for that table and never upsert it as a new table. |
| SD-4 | **Definitions are filtered for each caller.** Any metadata copy that is filtered for a caller (OData `filteredMeta`, CSDL, dynamic OpenAPI, MCP dataset catalog) drops every key or index that references a column the caller cannot see (`Deny`), as a whole. Row identity and the CSDL `<Key>` are computed from the filtered metadata. |
| SD-5 | **The keys endpoint writes narrowly.** `PUT /datasets/{id}/keys` does not send a full `TableMetadata` through `UpsertTableMetadataAsync`. It calls a new `IGovernanceRepository.ReplaceTableKeyDefinitionAsync(TableIdentifier, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, CancellationToken)`. In **one** transaction the method reads the stored columns, validates against them, replaces only the three definition tables and bumps the epoch once. The D-7 rejection of a separate method ("two transactions, two epoch bumps") does not apply to this path. |

### 13.2 Findings

| ID | Severity | Finding (verified in code) | Required mitigation (stream) |
|---|---|---|---|
| SEC-94-01 | **High** | Fail-open chain: D-7 "table skipped" means `GetTableMetadataAsync` returns `null`. Ingestion paths load `existing` (`DataCatalogSyncService.cs:82`, `OpenMetadataSyncService.cs:134/667`, `OpenApiIngestionService.cs:349`, dbt). `CatalogGovernanceRatchet.Merge(incoming, null)` then returns `incoming` without the stored masking rules. The SQLite upsert (`SqliteGovernanceRepository.Catalog.cs:518-541`) deletes and re-inserts `COLUMN_MASKING_RULES` for every incoming column. Result: one corrupt key row loaded directly into the store can **erase column masking** on the next sync. | SD-3. Additionally, `UpsertTableMetadataAsync` with `ReplaceKeyDefinition = false` must not read or validate the stored definition, so an upsert never fails or wipes data because of it. Tests in 13.3 (B). |
| SEC-94-02 | Medium | The GraphQL tree compiler orders by `metadata.PrimaryKeyColumns` with its own rule (`TreeSqlCompiler.cs:280-292`) and is not in the plan. After the `["id"]` default is removed, GraphQL offset paging loses its ordering (SQL Server: `ORDER BY (SELECT NULL)`). A second copy of the oracle guard would also drift. | SD-2: Stream C adds `TreeSqlCompiler.cs` to its file list and uses `DefaultReadOrdering.Resolve` for the default order (composite fallback only on the root node; nested relation lists may stop after the unique-index rule to bound cost). |
| SEC-94-03 | Medium | Hidden-column disclosure through definitions: `ODataHandler.cs:176-182` copies `PrimaryKeyColumns` unfiltered into the caller's filtered metadata, and the plan adds `Indexes` the same way. `ODataCsdlGenerator.ResolveEntityKeys` silently drops missing columns and emits a **partial** key. With `TableRowIdentity` over the unfiltered metadata, a `Deny` column name could reach the CSDL `<Key>` or the index list. | SD-4 (Stream C). `TableRowIdentity.Select` takes the filtered metadata. Catalog discovery, search, MCP and dynamic OpenAPI report index entries only for columns that the response already lists. |
| SEC-94-04 | Medium | Existence oracle in 400 texts: the planner (`GovernedTableReadPlanner.cs:146-160, 170-180`) returns "Column 'x' does not exist" for unknown columns but "Ordering/Filtering on masked or denied column 'x'" for `Deny` columns. The REST data API echoes `ArgumentException.Message` (`DatasetDataEndpoints.cs:210-214`). A caller can therefore probe for the names of columns that are hidden from its schema. | Stream C (in scope, because the planner is changed anyway): a `Deny` column gets exactly the unknown-column text. The "masked" text is used only for `Mask` columns, which are visible. The new `None` 400 uses a fixed text without column names: `no orderable column readable in clear text`. |
| SEC-94-05 | Medium | Lost update on masking: if the keys endpoint reads metadata (possibly from cache) and writes the full object back, it rewrites `COLUMN_MASKING_RULES` and can undo a concurrent masking change. | SD-5 (Stream B). |
| SEC-94-06 | Medium | Audit: the plan says `AuditLevel.Full`, but the sibling `PUT /datasets/{id}/state` uses the unrelated event type `ConsentApplied`. A change to a definition must be traceable in the HMAC-chained audit log. | Stream B: new `AuditEventTypes.CatalogKeyDefinitionChanged = "CATALOG_KEY_DEFINITION_CHANGED"` on the endpoint, plus a domain `AuditLogEntry` with actor SID, request tenant, dataset id and the before and after definitions (identifiers only, no data), written through the existing audit writer so it joins the HMAC chain. A replacement made by an ingestion path logs one Information line (source, table id, column counts). |
| SEC-94-07 | Medium | Read-side validation is too narrow: D-7 checks only ordinals, duplicates and column ownership. Index names loaded directly bypass the write regex and flow into CSDL XML, OpenAPI JSON and **MCP markdown** (`McpResourceProvider.cs:81`, LLM context: table breaking and prompt-injection text). | Stream B: the read side runs the **full** `TableKeyDefinitionValidator` (name regex, limits, duplicates, contiguous ordinals from 1, every `table_column_id` joined with `TABLE_COLUMNS.table_id = TABLES.id` of the same table). Any violation leads to SD-3. |
| SEC-94-08 | Medium | Fail-closed details of the shape verifier that the plan does not state. | Stream A: (a) a missing, empty or unparseable `governance-shape.<engine>.json` **throws** at startup; an empty expected shape must never pass. Parse with `System.Text.Json` into sealed records, without polymorphism. (b) The verifier runs under the schema lock in Apply **and** Verify. (c) Catalog reads bind every table or index name from the snapshot as a parameter, with no interpolation. (d) PostgreSQL resolves tables the same way the runtime does (unqualified names through `search_path`, i.e. `to_regclass(@name)` / `current_schema()`), so a shadow table in another schema neither satisfies nor hides the check. SQL Server checks `dbo`. (e) An object that the login cannot see is reported as missing (fail closed). (f) D-4 wraps only a `DbException` thrown by a step, and the original exception stays the `InnerException` for logs only. (g) The message contains `Path.GetFileName` of the SQLite file and `DbConnection.Database` only, never a connection string, host, user, password or directory path. The drift exception reaches only the startup log (`GovernanceMigrationHostedService`); no health or HTTP surface exposes it. |
| SEC-94-09 | Medium | Weak post-conditions, now also run in Verify: PostgreSQL counts `pg_trigger` by name only, without `tgrelid` or the enabled state (`PostgreSqlGovernanceMigrationRunner.cs:83`). SQL Server ignores `is_disabled` (`SqlServerGovernanceMigrationRunner.cs:100`). A disabled append-only trigger passes, which leaves the audit open to tampering. | Stream A: PostgreSQL requires `tgrelid = to_regclass('audit_log_entries')` and `tgenabled <> 'D'`. SQL Server requires `is_disabled = 0` and the existence of `fk_audit_truncate_guard`. |
| SEC-94-10 | Medium | Plan section 8 proposes that declared indexes "could replace" the `SqlProcedureRowScopeResolver` check. That check proves **physical** uniqueness for row-scope filtering, which is an authorization path, and it fails closed (`NotUnique`). Declared uniqueness is "trusted as stated" (D-10) and unverified, so the replacement would allow row-scope widening. | Section 8 is amended: the resolver **keeps** its source catalog query. That query needs only the metadata visibility that `SELECT` already grants and is documented as the one sanctioned key-catalog read. The CDC poller may move to the declared primary key in a follow-up (not an authorization path). `StoredProcedureCatalogValidator` stays an optional, documented grant (`VIEW DEFINITION`). The D-9 architecture test (Stream C) uses (1) a **positive** scope: `src/Autheris.Infrastructure/Persistence/Catalog/**`, `src/Autheris.Application/{Data,OData,Catalog,DataCatalog,Sql}/**`, `src/Autheris.Extensions/{OData,DataCatalog,OpenMetadata,Dbt}/**`; (2) an exact-path allowlist of exactly `Application/Procedures/Services/SqlProcedureRowScopeResolver.cs` and `Extensions/Cdc/MssqlChangeTrackingPoller.cs`, each with a justification string; (3) `Persistence/Migrations/**` (governance DB, including the new `Shape/` readers) outside the scope; (4) case-insensitive patterns extended by `pragma_index_info`, `pragma_index_xinfo`, `sys.key_constraints`, `ALL_IND_COLUMNS`, `ALL_CONS_COLUMNS`, the `USER_`/`DBA_` variants, `information_schema.constraint_column_usage` and `duckdb_indexes`; (5) a self-test proving that each pattern is detected. |
| SEC-94-11 | Low | Ingestion inputs (OpenMetadata, dbt, OpenAPI extensions, catalog assets) are untrusted. As written, `CatalogGovernanceRatchet.Merge` (`:106`) would also drop an incoming definition, because it copies only `existing.PrimaryKeyColumns`. | Stream B: every import runs the validator. An invalid definition is dropped **as a whole**: the table is still ingested with `ReplaceKeyDefinition = false` (stored definition kept), and a Warning logs the table id and the rule, without values. Size and type caps apply before allocation (string arrays only; at most 64 indexes and 32 columns). `Merge` carries the incoming definition and flag when the incoming flag is set and otherwise keeps the existing one. Repositories always return `ReplaceKeyDefinition = false`, so `existing with { ... }` round trips cannot wipe a definition. |
| SEC-94-12 | Low | Grant table (section 7): the runtime "all governance tables" grant includes `AUDIT_LOG_TRUNCATE_GUARD`, and the table does not exclude ownership or DDL roles, which allow `DISABLE TRIGGER`. | Stream A (tests) and Phase 6 (docs): SQL Server runtime gets no grant on `AUDIT_LOG_TRUNCATE_GUARD` and no `ALTER`/`CONTROL`/`db_owner`/`db_ddladmin`. PostgreSQL runtime is neither the owner nor a member of the owner role. `sp_getapplock` and `pg_advisory_lock` need only `public`. SQLite has no login separation (documented limitation). |
| SEC-94-13 | Low | WebSQL 504 problem details could pick up exception data through `ProblemDetailsService` or the developer exception page in Development. | Stream C: the endpoint writes the body explicitly: `type` (stable URI), `title`, `status`, `detail` = `GenericTimeoutMessage`, `traceId`. No `instance` with the query, no SQL text, no exception type, no stack trace. Content type `application/problem+json`. |
| SEC-94-14 | Low | `ODataHandler.BuildNextLink` (`:480-506`) appends `Domain/Schema/TableName` without escaping. | Stream C: escape each path segment. The link carries only the client's own query options. The tiebreaker is recomputed on the server for each request and never serialized into the link. |
| SEC-94-15 | Info | `TABLES` has no tenant column: the catalog is global, and `GovernanceAdmin` (or `ClusterAdmin`) is the write role, as for `PUT /datasets/{id}/state`. There is no cross-tenant definition to protect. A definition does not change authorization, so four-eyes approval is not required. | Endpoint: `RequireAuthorization(GatewayPolicies.GovernanceAdmin)`; strict dataset-id parsing; the same 404 body for unknown, inactive and unavailable datasets; 400 bodies contain the validation rule only. |
| SEC-94-16 | Info | Direct loads into the store by the client bypass the epoch bump, so cached metadata stays stale until the TTL expires. The `ClearColumnComposite` sort has no column cap, which is a DoS concern, but the execution timeout bounds it. | Phase 6 documents both: load before start or bump the epoch; declare a key. Accepted. |

Row filters and RLS: ordering is applied inside the same governed query, after the row predicate, and uses only `Clear` columns. It does not change which rows are returned, so it is not a side channel. The cross-source staging path must take its ordering from the same decision object (SD-2).

### 13.3 Mandatory security test cases

| Stream | Test (class.method) | Kind |
|---|---|---|
| A | `GovernanceSchemaShapeVerifierTests.MissingOrEmptySnapshotResource_ThrowsAtStartup` | Unit |
| A | `GovernanceSchemaVerificationTests.ApplyMode_WrongShape_NoVersionRowRecorded`, `.RecordedVersion_WrongShape_FailsOnEveryStart` (two consecutive runs) | Unit |
| A | `GovernanceSchemaVerificationTests.VerifyMode_ExecutesNoDdlOrWrite` (command interceptor: no `CREATE`/`ALTER`/`INSERT`/`UPDATE`/`DELETE`) | Unit |
| A | `GovernanceSchemaVerificationTests.DriftMessage_ContainsNoConnectionDetails` (no `Data Source=`, `Password`, `Pwd`, `User Id`, `Host=`, `Server=`, directory separators) | Unit |
| A | `GovernanceSchemaVerificationTests.ConcurrentRunners_VerifyUnderSchemaLock` | Unit |
| A | `GovernanceSchemaCatalogReaderTests.ShapeTableName_IsParameterBound` (name with `'` and `;`) | Unit |
| A | `PostgreSqlGovernanceSchemaShapeTests.ShadowTableInOtherSchema_DoesNotSatisfyShape` | Integration |
| A | `PostgreSqlGovernancePostConditionTests.DisabledOrMisplacedAuditTrigger_FailsVerify`; `SqlServerGovernancePostConditionTests.DisabledAuditTrigger_FailsVerify` | Integration |
| A | `PostgreSqlGovernanceLeastPrivilegeTests.RuntimeLogin_CannotUpdateAudit_DisableTrigger_OrWriteMigrationTable`; same in `SqlServerGovernanceLeastPrivilegeTests` | Integration |
| B | `TableKeyDefinitionValidatorTests` (each D-7 rule, plus `IndexName_WithMarkupOrControlCharacters_Rejected`, `OversizedArrays_RejectedBeforeAllocation`) | Unit |
| B | `SqliteCatalogKeyDefinitionContractTests.CorruptDefinition_GetThrowsUnavailable_GetAllOmits` (ordinal gap, duplicate ordinal, column id of another table, invalid index name, too many columns) | Unit |
| B | `SqliteCatalogKeyDefinitionContractTests.ReadMetadata_ReplaceFlagAlwaysFalse`, `.UpsertWithoutReplace_KeepsDefinition` | Unit |
| B | `CatalogIngestionUnavailableTableTests.Sync_OnUnavailableTable_DoesNotUpsert_MaskingRulesPreserved` (DataCatalogSync, OpenMetadata, dbt, OpenAPI) | Unit |
| B | `CatalogKeysEndpointTests.Anonymous_401`, `.NonAdmin_403`, `.UnknownOrUnavailableDataset_404_SameBody`, `.Invalid_400_RuleOnlyNoStackTrace`, `.Success_WritesHmacChainedAuditEntry_WithBeforeAfter`, `.ConcurrentMaskingChange_IsPreserved` | Unit |
| B | `CatalogGovernanceRatchetTests.Merge_CarriesIncomingDefinitionOnlyWhenFlagged` | Unit |
| B | `OpenApiIngestionKeyExtensionTests.MalformedExtension_DefinitionDropped_TableIngested`; `OpenMetadataKeyConstraintTests.UnknownColumn_DefinitionDropped`; `DbtKeyConstraintTests.UnknownColumn_DefinitionDropped` | Unit |
| B | `PostgreSqlCatalogKeyDefinitionContractTests.CorruptDefinition_Unavailable`; same in `SqlServerCatalogKeyDefinitionContractTests` | Integration |
| C | `DefaultReadOrderingTests.KeyWithMaskedColumn_SkippedWhole_NeverPrefix`, `.KeyWithDeniedColumn_Skipped`, `.UniqueIndexWithMaskedColumn_Skipped`, `.HmacOrCatalogSensitiveColumn_NotUsed`, `.NoClearOrderableColumn_None` | Unit |
| C | `GovernedTableReadPlannerTests.OrderByDeniedColumn_MessageEqualsUnknownColumn`, `.FilterOnDeniedColumn_MessageEqualsUnknownColumn`, `.Tiebreaker_UsesOnlyClearColumns`, `.NoneWithOffset_400_FixedTextWithoutColumnNames` | Unit |
| C | `TreeSqlCompilerDefaultOrderingTests.MaskedKey_SkippedToNextCandidate` | Unit |
| C | `ODataCsdlKeyVisibilityTests.DeniedKeyOrIndexColumn_NotEmitted_CandidateDroppedWhole`; `McpDatasetCatalogKeyVisibilityTests.IndexWithInvisibleColumn_NotReported` | Unit |
| C | `ODataNextLinkTests.PathSegmentsEscaped`, `.DerivedTiebreakerNotInLink`; `OrderingKindNotExposedTests.ResponseHasNoOrderingKind` | Unit |
| C | `WebSqlTimeoutProblemDetailsTests.Body_IsProblemJson_WithoutSqlOrExceptionData` | Unit |
| C | `ArchitectureNoSourceKeyCatalogQueriesTests.ExactAllowlist`, `.MigrationsOutOfScope`, `.EachPatternDetected` | Unit |
| C | `UnkeyedTablePagingStabilityTests.RowFilteredUnkeyedTable_PagesContainOnlyPermittedRows` | Integration |

### 13.4 Gate 3 checklist
- [x] Fail-closed defaults verified: drift stops startup and the version is not recorded (D-1). An invalid definition makes the table unavailable (SD-3). An ordering candidate with a non-clear column is skipped (SD-1).
- [x] Input validation and injection guards: validator on write and read (SEC-94-07), parameterized catalog reads (SEC-94-08c), identifiers quoted in catalog spelling via `QuoteIdentifier`.
- [x] Tamper-evident audit for the state change: SEC-94-06. Audit trigger post-conditions hardened: SEC-94-09.
- [x] Mandatory security test cases appended (13.3).
