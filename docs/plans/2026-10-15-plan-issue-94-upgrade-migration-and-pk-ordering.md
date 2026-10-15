# Implementation Plan: REQ-GOV-94 - Governance Schema Shape Verification and Key-Based Read Ordering

**Document ID:** `PLAN-GOV-94`
**Date:** 2026-10-15
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [PRD REQ-GOV-94](2026-10-15-req-issue-94-upgrade-migration-and-pk-ordering.md) (as amended by the owner decisions of 2026-10-15)
**Branch:** `fix/issue-94-upgrade-migration-pk`
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Next:** Phase 3 (`csharp-security-expert`)

---

## 0. Binding Owner Decisions (2026-10-15)

| # | Decision | Effect on this plan |
|---|---|---|
| OD-1 | **No upgrade or repair migrations.** No `0002`, no `ALTER TABLE`, rename or backfill for old shapes on any engine. The client (PoC "Talos") loads its governance data in the newest schema. | Item 1 is reduced to a read-only shape verification. US-A1 / AC-A1.x are removed from the PRD. |
| OD-2 | Migration `0001` (DDL and checksum) stays unchanged. | No column or table for persisted keys can be added. Keys come from runtime discovery (D-6). |
| OD-3 | Q3 (duplicate `DATA_OWNERS.ad_sid`) is moot. | No merge, no dedicated check. A database where `0001` cannot create `UX_DATA_OWNERS_SID` fails in `0001` and gets the diagnostic shape report (D-4). |
| OD-4 | Resolve all open questions with industry best practice and record the decision. | Q1 -> D-2/D-3, Q2 -> D-9/D-10/section 7. |

---

## 1. Verification of the PRD Against the Code

| PRD claim | Result |
|---|---|
| RC-1: v1.1.8 shims (`EnsureTableColumns`, `EnsureConsentColumnRuleColumns`, `EnsureAuditLogColumns`, `EnsureAccessProfilesTableMigration`, `DeduplicateDataOwnersBeforeIndex`, ...) were removed; the `*BaselineSchema.cs` files contain only `CREATE ... IF NOT EXISTS`; the runner records `0001` without shape verification. | **Confirmed** (`git show v1.1.8:src/Autheris.Infrastructure/Persistence/SqliteGovernanceRepository.Schema.cs`; `GovernanceMigrationRunnerBase.RunUnderLockAsync`). PostgreSQL v1.1.8 only had `ADD COLUMN IF NOT EXISTS applicable_scopes` and the owner dedup; SQL Server v1.1.8 had `COL_LENGTH` guards for `applicable_scopes` and `meta_json` plus a *deleting* owner dedup. The `ACCESS_PROFILES` rename existed only on SQLite. |
| AC-A1.2 (pre-shim DB without version table migrates to `0001`+`0002`) | **Would not have been reachable** (now moot): on SQLite, `0001` itself fails with `no such column: seq` on a pre-shim `AUDIT_LOG_ENTRIES`, because the baseline runs `CREATE UNIQUE INDEX IF NOT EXISTS idx_audit_seq ON AUDIT_LOG_ENTRIES (seq)`. A `0002` never runs in that case. D-4 turns this raw error into the actionable drift report. |
| The reporter's database recorded `0001` | Consistent: its `AUDIT_LOG_ENTRIES` already had `seq`/`tenant_id`, otherwise `0001` would have failed. |
| RC-2 line references (`TableModels.cs:114`, `SqliteGovernanceRepository.Catalog.cs:184`, PG `:129`, MSSQL `:130`, `GovernedTableReadPlanner.cs:188/192`, `GovernedDataQueryService.cs:142`, v1.1.8 `SqlDataSourceExecutor.cs:321`, `ODataHandler.cs:465`) | **Confirmed.** |
| "Keys computed by `OpenApiIngestionService` (`:313-336`) are dropped on upsert" | Confirmed, with an addition: those keys are **heuristic** (`required` properties, else `id`/`*_id`, else the first column). They are guesses, not keys; see D-7. |
| Item 3: "WebSQL ... compiles through the SqlGate/AST compiler, which adds a key ordering only when every key column is `Clear` (`TreeSqlCompiler.cs:287`)" | **Wrong.** `TreeSqlCompiler` is the GraphQL tree compiler (`GovernedTreeQueryService`). WebSQL (`WebSqlEndpoints` -> `IGovernedSqlExecutionService`) never adds a key ordering. The only generated ordering is `ORDER BY (SELECT NULL)` that `SqlServerDialectGenerator` emits for `OFFSET` without `ORDER BY`. A stale `["id"]` key therefore cannot cause the WebSQL 504. See section 7. |
| "Live source introspection (`IColumnMetadataReader`) reads names, types and collations but not key membership" | Confirmed. |
| WebSQL 504 response | Confirmed as 504, but the body is an ad-hoc JSON object (`error`, `message`, `traceId`), not RFC 9457 problem details (D-10). |

---

## 2. Architecture Decisions

### D-1 Shape verification is part of the migration runner, read-only, and runs on every start (except `Mode=Skip`)
- `GovernanceMigrationRunnerBase` gains one abstract dependency: the engine's `GovernanceSchemaShapeVerifier` (constructed by the concrete runner, no DI change).
- **Apply mode:** after the last pending step has run and **before** its version row is inserted, the runner verifies the shape. A failure throws `GovernanceSchemaDriftException`; the version is not recorded. With no pending steps the verification runs after the checksum checks (a database that already records `0001` with a wrong shape fails on every start, which is the reporter's case).
- **Verify mode:** after the existing "pending migrations" check, the shape is verified the same way. No DDL, no write.
- **Skip mode:** unchanged (warning, nothing verified).
- SQLite: the run is one `BEGIN IMMEDIATE` transaction, so a failure rolls back the whole run, including the version table creation. PostgreSQL / SQL Server: `0001` is non-transactional (unchanged); its `CREATE ... IF NOT EXISTS` statements may stay committed, but the version row is not written, so the next start re-runs `0001` (idempotent) and verifies again. This satisfies the restated AC-A2.3.
- Order of checks: shape verification, then the existing `VerifyPostConditionsAsync` (audit triggers).
- **Not a migration:** the verifier only reads the system catalog. It does not conflict with OD-1 (flagged for the owner in the report as requested; no conflict found).
- *Rejected:* a separate hosted service (would run after the version is recorded); verifying only in Verify mode (the reporter's database would still start); verifying in each repository constructor (the I/O-free constructor rule of WP-ARC-B-12).

### D-2 Expected shape = generated snapshot of the baseline, one per engine (Q1)
- Single source of truth is the baseline DDL that `0001` executes. A test applies the migration catalogue to a fresh database, reads the system catalog with the same reader the verifier uses, and compares it with a checked-in snapshot `governance-shape.<engine>.json` (embedded resource). The snapshot is **generated** by that test (on mismatch the test writes the actual snapshot next to the test output and fails with the path), never edited by hand. Equality is strict in this test, so a later migration that adds an object without regenerating the snapshot fails CI.
- SQLite runs in the unit test project (in-process); PostgreSQL and SQL Server run in the integration project on the shared Testcontainers fixtures.
- *Rejected:* runtime scratch database (possible only for SQLite; PostgreSQL/SQL Server would need `CREATE DATABASE` rights); parsing the DDL text (fragile); a hand-written list (diverges, forbidden by AC-A2.1).

### D-3 What is compared (Q1)
| Object | Compared | Not compared |
|---|---|---|
| Table | existence (case-insensitive name) | extra tables (tolerated, logged once at Information) |
| Column | existence; **type family** (`string`, `integer`, `decimal`, `float`, `boolean`, `datetime`, `binary`, `json`, `other`; SQLite: column affinity via `SqliteTypeAffinity`) | length/precision; nullability; defaults; extra columns (tolerated) |
| Primary key | ordered column set per table (upserts rely on `ON CONFLICT`/`MERGE` targets) | constraint name (SQL Server auto-names it) |
| Index | existence of every explicitly named index (SQLite `origin = 'c'`, PostgreSQL indexes not owned by a constraint, SQL Server `is_primary_key = 0 AND is_unique_constraint = 0`), its uniqueness and ordered column list | auto-named constraint indexes (`sqlite_autoindex_*`, `*_pkey`, `PK__...`); extra indexes (tolerated) |
| Not checked | triggers (already checked by `VerifyPostConditionsAsync`), foreign keys, check constraints, data | |

Rationale: nullability and lengths are not needed for correctness of the reads and writes this build issues; checking them would fail databases that work. Type family and key/index shape are what makes queries or upserts fail.

### D-4 Diagnostic report also when `0001` itself fails
If a step throws a `DbException`, the runner runs the (read-only) verifier on the same connection and throws `GovernanceSchemaDriftException` with the deviation list and the original exception as `InnerException` (its message is logged, not repeated in the drift message). If the verifier itself fails, the original exception propagates unchanged. SQLite keeps the transaction open after a failed statement, PostgreSQL/SQL Server run `0001` outside a transaction, so the catalog read works in all three cases.

### D-5 Error message format (AC-A2.2)
One `Error` log line and one exception message, deterministic (sorted by object type, then table, then name), at most 100 entries plus `... and N more`:
```
Governance schema drift on SQLite database 'gov.db': the schema does not match this build (applied migrations: 0001; pending: none).
  missing table      VIRTUAL_FILTERS
  missing column     CONSENT_COLUMN_RULES.applicable_scopes (expected TEXT, family string)
  missing column     ACCESS_PROFILES.profile_id (expected TEXT, family string)
  wrong primary key  ACCESS_PROFILES (expected tenant_id, profile_id; found id)
  wrong type         TABLES.data_source_type (expected family integer; found string)
  missing index      AUDIT_LOG_ENTRIES.idx_audit_seq (expected UNIQUE (seq))
Autheris does not upgrade older governance schemas. Load the governance data in the current schema or restore the matching backup; see docs/features/f-gov-12-multi-engine-governance-storage.md#schema-shape-verification.
```
- Database label: SQLite file name only (`Path.GetFileName`), PostgreSQL/SQL Server `DbConnection.Database`. Never the connection string, host or user.
- `GovernanceSchemaDriftException` gains `IReadOnlyList<SchemaDeviation> Deviations` (empty for the existing checksum/version cases) for tests and telemetry.
- Telemetry: `Activity.Current?.SetTag("autheris.governance.schema.deviations", count)` (replaces the PRD's `reconciled_objects`, which no longer exists).

### D-6 Keys are not persisted; SQL source keys are discovered at runtime and cached (OD-2)
Because `0001` must not change, there is no place for key membership. Options:

| Option | Verdict |
|---|---|
| Runtime discovery from source metadata, cached with the `GovernedCatalogProvider` TTL | **Chosen.** Truth comes from the source; no schema change; one small catalog query per table and TTL on a cache miss. |
| Reserved entry in `TABLE_COLUMNS.meta_json` | Rejected: hidden schema in free-form JSON; every ingestion path (dbt meta, OpenMetadata, admin) rewrites `meta_json` and would wipe it; a policy-relevant value in user-editable metadata. |
| Client supplies keys in each request | Rejected: not a key, a hint; no API surface for it; transports differ. |
| New column/table | Forbidden (OD-1/OD-2). |

Consequences: non-SQL sources (HTTP, plugin, lakehouse) have no key and use the deterministic fallback; virtual system tables keep their in-code keys. A source without a primary key gets the fast path by adding a primary key or a unique index in the source. Persisting declared keys is a possible later feature (it needs a migration the owner has to approve).

### D-7 Key resolution order (AC-B1.4, Q2 "PK -> unique index -> all orderable clear columns")
`TableKeyResolver.ResolveAsync(metadata)`:
1. **Declared** (in-memory): `metadata.PrimaryKeyColumns` non-empty and every column is a governed column -> `Declared`. Only virtual system tables and tests set it after D-8.
2. **Discovered** (`DataSourceType.Sql` only): `ISourceKeyDiscovery` returns the source primary key; if none, the unique index whose columns are all `NOT NULL`, choosing the fewest columns, then the lowest index name (ordinal). Every column must be a governed column, otherwise the candidate is dropped -> `Discovered`.
3. Otherwise `None`.

Discovery failures (permission, timeout) are logged at Warning with the exception type only and yield `None` (fail-safe: ordering affects stability, not authorization; the compile path still denies an unreadable table).

Ingestion keys (OpenAPI `required` heuristic, dbt/OpenMetadata) are not persisted (D-6). `OpenApiIngestionService` keeps its in-memory computation unchanged; no ingestion file changes in this plan.

### D-8 `TableMetadata.PrimaryKeyColumns` defaults to empty and means "declared key"
- `TableModels.cs`: default `Array.Empty<string>()`. PostgreSQL and SQL Server `GetTableMetadataAsync` stop setting `["id"]`.
- The resolved key is applied at the boundary with `metadata.WithResolvedKey(key)` (a `with` expression), so the planner, CSDL and OpenAPI generators stay synchronous and keep reading `PrimaryKeyColumns`.
- CSDL `ResolveEntityKeys`: resolved key, else **all columns** as composite (OData requires a key; documented existing fallback). The `id` guess (step 2) is removed.
- Catalog discovery, catalog search, MCP dataset catalog, OpenAPI: report the resolved key or none.

### D-9 Default ordering and tiebreaker (AC-B2.2, AC-B2.3, Q2)
New pure static `DefaultReadOrdering.Resolve(metadata, decision)` (Application), used by the planner and nothing else:
1. **Key:** the resolved key, if every key column exists and is `Clear` for the caller.
2. **ClearColumnComposite:** all `Clear` columns whose type is orderable on `metadata.Dialect`, in catalog ordinal order.
3. **None.**

Orderable types: deny-list by family per dialect, unknown types count as orderable (they are what the source reports and the source can sort them). Not orderable: `json/jsonb`, `xml`, binary (`binary/varbinary/bytea/blob/image/raw/long raw`), large objects (`clob/nclob/blob/text/ntext/image` on SQL Server and Oracle, any `(max)` type on SQL Server), spatial (`geometry/geography/hierarchyid/sdo_geometry`), `sql_variant`, complex types (`array/map/struct/variant/list/union`). On PostgreSQL, SQLite and DuckDB `text` stays orderable.

**Explicit order + tiebreaker (Q2 best practice, changes AC-B2.7):** an explicit `$orderby`/`orderBy` keeps its validation (masked or denied columns rejected, unchanged) and gets the default ordering columns that are not already named appended in ascending order. Result: every page boundary is deterministic. With `None` nothing is appended.

**`None` handling:** `offset > 0` -> 400 `no orderable column readable in clear text`. `offset = 0`: the page is read without `ORDER BY`; if the look-ahead row shows more rows, the transport returns 400 with the same message instead of a page with a continuation that cannot be honoured. Deterministic paging is impossible in this case, so refusing is the only answer that does not return duplicated or skipped rows.

**No cap on the composite:** correctness over speed; the cost is bounded by the existing execution timeout (504). Documented, with "add a primary key or unique index in the source" as the remedy. *Rejected:* "first N columns" (breaks determinism for nothing measurable: sort cost is driven by row count).

Known limitation (documented): rows that tie on every orderable clear column but differ in masked/non-orderable columns can still move across page boundaries; for fully duplicate rows the multiset is exact (AC-B2.5).

### D-10 Server-driven paging and timeouts (Q2)
- `@odata.nextLink` is emitted whenever the look-ahead row proves that more rows exist (existing mechanism, `ODataHandler.cs:464-468`). This is stricter than "page is full" and never produces an empty trailing page; AC-B2.6 tests keyed and unkeyed tables.
- WebSQL timeout: the 504 body becomes RFC 9457 problem details (`application/problem+json`, `type`, `title`, `status`, `detail` = existing generic message, `traceId`). OData keeps the OData JSON error format (required by the OData protocol) and the existing 504. REST data API: keep its existing error contract; Stream B checks it is a 504 with a stable code.

---

## 3. Contracts (shared by the streams)

```csharp
// src/Autheris.Application/Data/Keys/ResolvedTableKey.cs
public enum TableKeySource { None, Declared, Discovered }
public sealed record ResolvedTableKey(IReadOnlyList<string> Columns, TableKeySource Source)
{
    public static readonly ResolvedTableKey None = new(Array.Empty<string>(), TableKeySource.None);
}

// src/Autheris.Application/Data/Keys/ISourceKeyDiscovery.cs (implemented by GovernedCatalogProvider)
public interface ISourceKeyDiscovery
{
    /// Primary key, else best NOT NULL unique index, in key order, catalog spelling. Empty when none or unknown. Never throws except OperationCanceledException.
    ValueTask<IReadOnlyList<string>> DiscoverKeyAsync(TableMetadata metadata, CancellationToken ct);
}

// src/Autheris.Application/Data/Keys/ITableKeyResolver.cs (singleton)
public interface ITableKeyResolver
{
    ValueTask<ResolvedTableKey> ResolveAsync(TableMetadata metadata, CancellationToken ct);
}
public static TableMetadata WithResolvedKey(this TableMetadata metadata, ResolvedTableKey key); // TableKeyMetadataExtensions

// src/Autheris.Application/Data/Services/DefaultReadOrdering.cs
public enum DefaultOrderingKind { Explicit, Key, ClearColumnComposite, None }
public sealed record DefaultOrdering(IReadOnlyList<string> Columns, DefaultOrderingKind Kind);
public static class DefaultReadOrdering
{
    public static DefaultOrdering Resolve(TableMetadata metadata, TableAccessDecision decision);
    public static bool IsOrderableType(string dataType, DatabaseDialect dialect);
}

// IGovernedTableReadPlanner.cs: PlannedTableRead gains a trailing parameter
public sealed record PlannedTableRead(string Sql, IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyList<string> ColumnsToSelect, DefaultOrderingKind Ordering = DefaultOrderingKind.Explicit);

// TableQueryItems: new response item
public const string Ordering = "TableQuery.Ordering"; // DefaultOrderingKind, set by SqlTableReadExecutor

// src/Autheris.Infrastructure/Persistence/Catalog/ISourceKeyReader.cs (one per dialect, Infrastructure-internal)
public interface ISourceKeyReader
{
    TargetSqlDialect Dialect { get; }
    Task<IReadOnlyList<string>> ReadKeyAsync(DbConnection connection, TableIdentity table, CancellationToken ct);
}

// src/Autheris.Infrastructure/Persistence/Migrations/Shape/*
public sealed record SchemaDeviation(SchemaDeviationKind Kind, string Table, string? Name, string Expected, string? Found);
public enum SchemaDeviationKind { MissingTable, MissingColumn, WrongType, WrongPrimaryKey, MissingIndex, WrongIndex }
```

The streams below touch disjoint files. Stream A has no contract with Stream B (it works only inside `Persistence/Migrations`). Inside Stream B, B-1 creates the Application contract files in its first commit; B-2 (optional second developer) depends on `ISourceKeyDiscovery` only from its step B2-4 on.

---

## 4. Files per Layer

### Domain
| File | Change | Stream |
|---|---|---|
| `src/Autheris.Domain/Model/TableModels.cs` | `PrimaryKeyColumns` default empty; XML doc "declared key; the effective key comes from `ITableKeyResolver`". | B-1 |

### Application
| File | Change | Stream |
|---|---|---|
| `Data/Keys/ResolvedTableKey.cs`, `ISourceKeyDiscovery.cs`, `ITableKeyResolver.cs`, `TableKeyResolver.cs`, `TableKeyMetadataExtensions.cs` (new) | contracts + resolver (D-7). Debug log once per table and cache period when the fallback is used. | B-1 |
| `Data/Services/DefaultReadOrdering.cs` (new) | D-9 rule and orderable types. | B-1 |
| `Data/Interfaces/IGovernedTableReadPlanner.cs` | `PlannedTableRead.Ordering`; doc of `TableReadIntent.OrderBy`. | B-1 |
| `Data/Services/GovernedTableReadPlanner.cs` | `ResolveOrderBy` uses `DefaultReadOrdering`; tiebreaker; 400 for `None` with `Offset > 0`. | B-1 |
| `Data/Services/GovernedDataQueryService.cs` | resolve key, `WithResolvedKey`; remove the line-142 check (AC-B2.4); `request.Offset > 0 && Ordering == None` -> 400 (covers the cross-source branch, which plans with offset 0); `HasMore && Ordering == None` -> 400. | B-1 |
| `Services/SqlTableReadExecutor.cs` | resolve key before planning; set `TableQueryItems.Ordering`. | B-1 |
| `Interfaces/IGatewayExecutionService.cs` | `TableQueryItems.Ordering`. | B-1 |
| `Catalog/Services/CatalogDiscoveryService.cs`, `Catalog/Search/CatalogSearchEngine.cs`, `Mcp/Services/McpDatasetCatalog.cs`, `OData/Services/DynamicOpenApiGenerator.cs` (or its caller) | use the resolver (`WithResolvedKey`) before reading keys. | B-1 |

### Infrastructure
| File | Change | Stream |
|---|---|---|
| `Persistence/Migrations/GovernanceMigrationRunnerBase.cs` | D-1, D-4: verifier hook before the last version insert, in Verify mode, and on step failure. | A |
| `Persistence/Migrations/MigrationModel.cs` | `GovernanceSchemaDriftException.Deviations`, new ctor. | A |
| `Persistence/Migrations/Shape/GovernanceSchemaShape.cs` (new) | model (`TableShape`, `ColumnShape`, `IndexShape`), JSON load from embedded resource. | A |
| `Persistence/Migrations/Shape/GovernanceSchemaShapeComparer.cs` (new) | pure compare -> `IReadOnlyList<SchemaDeviation>`; message formatter (D-5). | A |
| `Persistence/Migrations/Shape/IGovernanceSchemaCatalogReader.cs` + `SqliteSchemaCatalogReader.cs`, `PostgreSqlSchemaCatalogReader.cs`, `SqlServerSchemaCatalogReader.cs` (new) | read the actual shape (D-3). PostgreSQL: `current_schema()`; SQL Server: `dbo`. | A |
| `Persistence/Migrations/Shape/GovernanceSchemaShapeVerifier.cs` (new) | reader + comparer + logging of extras. | A |
| `Persistence/Migrations/Shape/governance-shape.{sqlite,postgresql,sqlserver}.json` (new, generated) | snapshots. | A |
| `Persistence/Migrations/{Sqlite,PostgreSql,SqlServer}GovernanceMigrationRunner.cs` | pass the engine verifier and database label to the base; catalogue and checksum untouched. | A |
| `Autheris.Infrastructure.csproj` | `<EmbeddedResource Include="Persistence\Migrations\Shape\*.json" />`. | A |
| `Persistence/PostgreSqlGovernanceRepository.Catalog.cs`, `SqlServerGovernanceRepository.Catalog.cs` | drop `PrimaryKeyColumns = ["id"]`. | B-1 |
| `Persistence/Catalog/ISourceKeyReader.cs` + `SqlServer/PostgreSql/Sqlite/Oracle/DuckDb/DatabricksSourceKeyReader.cs` (new) | D-7 discovery SQL, bound names, system catalogs fully qualified (SEC-X1-09). Databricks returns empty. | B-2 |
| `Persistence/Catalog/GovernedCatalogProvider.cs` | implement `ISourceKeyDiscovery`; read the key on the same connection during `LoadAsync`, store it in `CacheItem`; a key-read failure never denies the table. | B-2 |

Discovery SQL (one query per table, returns key columns in order):
- SQL Server: `sys.indexes` (`is_primary_key = 1`, else `is_unique = 1 AND has_filter = 0` with all `sys.columns.is_nullable = 0`) join `sys.index_columns` (`key_ordinal > 0`, `is_included_column = 0`).
- PostgreSQL: `pg_catalog.pg_index` (`indisprimary`, else `indisunique AND indpred IS NULL AND indexprs IS NULL` with all `attnotnull`), `unnest(indkey) WITH ORDINALITY`.
- SQLite: `pragma_table_info(@t, @s).pk > 0` ordered by `pk`; else `pragma_index_list` (`unique = 1`, `partial = 0`) + `pragma_index_info` with `notnull` check.
- Oracle: `ALL_CONSTRAINTS` type `P`, else type `U` / unique `ALL_INDEXES` with `ALL_TAB_COLUMNS.NULLABLE = 'N'`, `ALL_CONS_COLUMNS.POSITION`.
- DuckDB: `duckdb_constraints()` (`PRIMARY KEY`, else `UNIQUE` with all `NOT NULL`).

### Api / Extensions
| File | Change | Stream |
|---|---|---|
| `Api/Extensions/DependencyInjection/GatewayStorageServiceExtensions.cs` | `ISourceKeyDiscovery` -> same `GovernedCatalogProvider` singleton; register `ISourceKeyReader` implementations. | B-2 |
| `Api/Extensions/DependencyInjection/DataApiServiceExtensions.cs` | `ITableKeyResolver` singleton (no-op discovery fallback when no provider is registered, e.g. no SQL sources). | B-1 |
| `Extensions/OData/ODataHandler.cs` | resolve keys for the table list (line ~180); `Ordering == None && rows.Count > top` -> 400 instead of a nextLink. | B-1 |
| `Extensions/OData/ODataCsdlGenerator.cs` | drop the `id` guess in `ResolveEntityKeys`. | B-1 |
| `Api/Endpoints/WebSqlEndpoints.cs` | 504 as problem details (D-10). | B-1 |

DI lifetimes: resolver and readers singleton (stateless); `GovernedCatalogProvider` is already singleton; no scoped dependency is captured.

---

## 5. Test Strategy (AC mapping)

Integration test classes are new classes in namespace `Autheris.Tests.Integration` (CI shards them automatically); PostgreSQL/SQL Server use `PostgreSqlContainerFixture` / `MsSqlContainerFixture` (or `PostgreSqlTestDatabase`).

| AC | Test class (project) | What it proves |
|---|---|---|
| AC-A2.1 snapshot = baseline | `GovernanceSchemaShapeSnapshotTests` (Unit, SQLite); `PostgreSqlGovernanceSchemaShapeTests`, `SqlServerGovernanceSchemaShapeTests` (Integration) | fresh DB after `RunAsync` equals the snapshot exactly; a second `RunAsync` passes. |
| AC-A2.1 single drop fails | `GovernanceSchemaShapeComparerTests` (Unit) | `[Theory]` over every column, index and table of each snapshot: removing it yields exactly one deviation of the right kind. Type family change -> `WrongType`. |
| AC-A2.2 message | `GovernanceSchemaShapeComparerTests`, `GovernanceSchemaVerificationTests` (Unit) | deterministic order, cap, file name only, no `Data Source=`/`Password`/host in the message. |
| AC-A2.3 not recorded | `GovernanceSchemaVerificationTests` (Unit, SQLite) | synthetic old shape built from the v1.1.8 DDL fixture (`tests/Autheris.Tests.Unit/Governance/Fixtures/governance-v1.1.8-preshim.sqlite.sql`, extracted with `git show v1.1.8:...`, shim columns removed, legacy `ACCESS_PROFILES(id, ...)`): (a) without version table -> drift, `autheris_schema_migrations` absent afterwards; (b) with `0001` recorded (reporter state) -> drift on every start, lists `CONSENT_COLUMN_RULES.applicable_scopes`, `ACCESS_PROFILES.profile_id`, `ACCESS_PROFILES` primary key; (c) pre-shim `AUDIT_LOG_ENTRIES` without `seq` -> `0001` fails, drift report with inner `SqliteException` (D-4). |
| AC-A2.3 PG/MSSQL | `PostgreSqlGovernanceSchemaShapeTests`, `SqlServerGovernanceSchemaShapeTests` | pre-created `CONSENT_COLUMN_RULES` without `applicable_scopes` (and MSSQL `TABLE_COLUMNS` without `meta_json`): drift, no version row. |
| AC-A2.4 extras | `GovernanceSchemaVerificationTests` | extra table/column/index passes; one Information log. |
| AC-A2.5 / Verify | existing `GovernanceSchemaMigrationTests` + new cases in `GovernanceSchemaVerificationTests` | Verify mode on a wrong shape -> drift without DDL (schema snapshot unchanged); Skip unchanged. |
| Integrity | existing `GovernanceSchemaMigrationTests.Catalogues_*` + new `MigrationCatalogueChecksumTests` (Unit) | `0001` checksum constants equal the released values on all engines. |
| AC-B1.1 | `PrimaryKeyDefaultTests` (Unit) | default empty; grep test forbids `{ "id" }` / `["id"]` key literals in `TableModels.cs` and the three `*GovernanceRepository.Catalog.cs`; virtual system tables keep keys. |
| AC-B1.4 resolver | `TableKeyResolverTests` (Unit) | declared > discovered > none; ungoverned key column drops the candidate; discovery exception -> `None`; non-SQL sources never call discovery. |
| AC-B1.4 discovery | `SourceKeyReaderIntegrationTests` (Integration: SQL Server, PostgreSQL, Oracle fixtures) + `SqliteSourceKeyReaderTests`, `DuckDbSourceKeyReaderTests` (Unit, in-process) | PK, composite PK order, unique NOT NULL index fallback, nullable unique index ignored, no key -> empty. |
| AC-B1.4 cache | `GovernedCatalogProviderKeyDiscoveryTests` (Unit) | one source round trip per TTL; key-read failure keeps the table admitted. |
| AC-B1.5 consumers | extend `ODataTests`/`CatalogDiscoveryServiceTests`/`CatalogSearchEngineTests`/`McpDatasetCatalogTests`/`DynamicOpenApiGeneratorTests` (Unit) | unkeyed table reports no key (CSDL: all-column composite, no `id` guess). |
| AC-B2.1 regression | `GovernedTableReadPlannerTests` (Unit) + `UnkeyedTableODataReadTests` (Integration, SQLite end-to-end) + `SqlServerUnkeyedTableReadTests` (Integration, `MsSqlContainerFixture`, table `fms.air1` without key or `id`) | `$top=5` -> 200, 5 rows; compiled SQL references only existing columns. |
| AC-B2.2 / B2.3 | `DefaultReadOrderingTests` (Unit) | key / composite / none; masked key column (`ssn`) -> composite without `ssn`; LOB/JSON/`nvarchar(max)` excluded per dialect. |
| AC-B2.2.3 | `GovernedDataQueryServiceTests`, `ODataHandler` tests (Unit) | `None` + offset > 0 -> 400; `None` + offset 0 + more rows -> 400; fits in one page -> 200. |
| AC-B2.4 | `GovernedDataQueryServiceTests` | offset paging on an unkeyed table with clear columns succeeds (no "requires a primary key" error). |
| AC-B2.5 | `UnkeyedTablePagingStabilityTests` (Integration, SQLite) | walk all `nextLink`s: distinct rows exactly once; duplicate rows: multiset equal. |
| AC-B2.6 | `ODataHardeningTests` / new `ODataNextLinkTests` (Unit) | nextLink present for keyed and unkeyed tables when the look-ahead row exists, absent otherwise. |
| AC-B2.7 (changed) | `GovernedTableReadPlannerTests` | explicit order kept, key appended as tiebreaker, no duplicate column, masked explicit column still rejected. |
| AC-C1 | `WebSqlUnkeyedOrderByTests` (Integration, SQLite) | `SELECT ... FROM <unkeyed> ORDER BY <non-key> LIMIT n` compiles to exactly that ORDER BY, no reference to a missing column; 504 is problem details (`WebSqlTimeoutProblemDetailsTests`, Unit/Api). |

Existing tests that assert the `["id"]` default or the `id` CSDL guess are updated by the stream that owns the changed production file.

---

## 6. Phase-4 Streams

Two streams run in parallel; Stream B can be split into B-1 and B-2 if a third developer is available. All developers are Sonnet `csharp-tdd-developer` agents (memory rule), strict red-green-refactor, one commit per green step, push after every commit (`/work` is not persistent).

### Stream A - Governance schema shape verification
**Owns:** `src/Autheris.Infrastructure/Persistence/Migrations/**`, `Autheris.Infrastructure.csproj`, new test files `tests/Autheris.Tests.Unit/Governance/GovernanceSchema*Tests.cs`, `MigrationCatalogueChecksumTests.cs`, `Fixtures/governance-v1.1.8-preshim.sqlite.sql`, new integration classes `*GovernanceSchemaShapeTests.cs`.
1. Red: `GovernanceSchemaShapeComparerTests` (missing column/table/index, wrong type, wrong PK, extras tolerated, deterministic message). Green: `GovernanceSchemaShape`, `SchemaDeviation`, comparer, formatter.
2. Red: `SqliteSchemaCatalogReader` reads a known in-memory schema. Green: reader.
3. Red: `GovernanceSchemaShapeSnapshotTests` (SQLite) fails with generated JSON path. Green: commit generated `governance-shape.sqlite.json`, embedded resource, loader.
4. Red: `GovernanceSchemaVerificationTests` (a), (b), (c) and Verify mode. Green: runner base hook (D-1, D-4), `GovernanceSchemaDriftException.Deviations`, SQLite runner wiring.
5. Red: `MigrationCatalogueChecksumTests`. Green: nothing to change (guard).
6. Red: PostgreSQL / SQL Server shape tests. Green: the two catalog readers, generated snapshots, runner wiring.
7. Refactor; full unit suite green; then Integration shards for the new classes.

### Stream B-1 - Key resolution, ordering and consumers
**Owns:** all Domain/Application/Api/Extensions files listed for B-1 in section 4, the two repository `Catalog.cs` lines, and the affected existing tests.
1. Red: `DefaultReadOrderingTests`. Green: contracts (section 3) + `DefaultReadOrdering`. **Commit and push first** (B-2 builds on it).
2. Red: `TableKeyResolverTests` with a fake `ISourceKeyDiscovery`. Green: resolver, extension, DI registration.
3. Red: `GovernedTableReadPlannerTests` (AC-B2.1, B2.3, B2.7 tiebreaker, `None`). Green: planner.
4. Red: `PrimaryKeyDefaultTests`. Green: default empty, PG/MSSQL `["id"]` removed; fix fallout in existing tests.
5. Red: `GovernedDataQueryServiceTests` (AC-B2.2.3, B2.4). Green: service.
6. Red: executor + `ODataNextLinkTests` + OData 400 for `None`. Green: `SqlTableReadExecutor`, `ODataHandler`, `TableQueryItems.Ordering`.
7. Red: consumer tests (AC-B1.5). Green: CSDL, OpenAPI, discovery, search, MCP.
8. Red: `WebSqlTimeoutProblemDetailsTests`. Green: WebSQL 504 problem details.
9. Integration: `UnkeyedTableODataReadTests`, `UnkeyedTablePagingStabilityTests`, `WebSqlUnkeyedOrderByTests`, `SqlServerUnkeyedTableReadTests`.

### Stream B-2 - Source key discovery (optional split; otherwise B-1 does it after step 2)
**Owns:** `src/Autheris.Infrastructure/Persistence/Catalog/**` (new `*SourceKeyReader.cs`, `GovernedCatalogProvider.cs`), `GatewayStorageServiceExtensions.cs`, new reader tests.
1. Red: `SqliteSourceKeyReaderTests`, `DuckDbSourceKeyReaderTests`. Green: `ISourceKeyReader`, SQLite/DuckDB readers.
2. Red: `SourceKeyReaderIntegrationTests` (SQL Server, PostgreSQL, Oracle). Green: the three readers; Databricks returns empty.
3. Rebase on B-1 step 1.
4. Red: `GovernedCatalogProviderKeyDiscoveryTests`. Green: `ISourceKeyDiscovery` on the provider, cache, DI forwarding.

File ownership is disjoint: A never touches Application/Api/Extensions or `Persistence/Catalog`; B-1 never touches `Persistence/Migrations` or `Persistence/Catalog`; B-2 never touches Application except reading the contracts.

---

## 7. Item 3 Triage Result (AC-C2)

- **Missing `@odata.nextLink`:** a consequence of RC-2 (the request failed with 500 before the page was built). Fixed by Stream B; covered by AC-B2.6.
- **WebSQL 504 after 10.6 s:** WebSQL compiles the user's statement through `IGovernedSqlExecutionService`; Autheris adds no ordering (section 1). A user `ORDER BY` on a non-indexed column of a large SQL Server table sorts the whole table and hits `WebSql:ExecutionTimeoutSeconds`; the 504 is the timeout working as designed. Disposition: **by design**, no workaround. Runbook note (Phase 6): add an index on the ordering column or raise the timeout. AC-C1 adds a compiled-SQL regression test; the 504 body becomes problem details (D-10). If the AC-C1 test shows an Autheris-generated clause, the stream fixes it in this change.

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| Snapshot and baseline diverge | strict-equality snapshot tests on all engines in CI (D-2). |
| False drift on working databases (type spelling) | compare type family only; nullability/length ignored (D-3). |
| Verification latency | one catalog read per start (< 200 ms SQLite, < 1 s remote target); measured in the snapshot tests. |
| Composite ORDER BY slow on wide/large unkeyed tables | bounded by execution timeouts; documented remedy: key or unique index in the source. |
| Key discovery adds round trips | same connection as the column read, cached with the catalog TTL; failure -> no key, never a denial. |
| Behavior change: no `id` key in CSDL/OpenAPI/catalog; explicit order gets a tiebreaker | allowed (no production); release note. |

---

## 9. Phase-3 Review Focus
- Ordering oracle: default ordering and tiebreaker only on `Clear` columns; masked/denied never appended.
- Drift message content: no connection string, host, user or data values.
- Discovery queries: bound names, fully qualified system catalogs; discovery failure cannot widen access.
- `None` handling returns 400, never inconsistent pages.

## 10. Gate 2 Checklist
- [x] Layers respected: Domain (default only) -> Application (contracts, resolver, ordering) -> Infrastructure (verifier, discovery) -> Api/Extensions (DI, transports).
- [x] Hot path: ordering resolution is allocation-light and computed once per request; discovery cached; no per-request source metadata round trip on a cache hit.
- [x] Concrete steps drafted (section 6).
- [x] Decomposed into parallel streams with disjoint file ownership and contracts.
