# F-GOV-21: Declared Table Keys, Indexes & Deterministic Read Ordering

## 1. Overview

Primary keys and indexes are part of the **table definition** that Autheris keeps in its governance catalog. They are declared by the caller (an administrator, a catalog sync, dbt, OpenMetadata or an ingested OpenAPI document), validated fail-closed, persisted in the governance store and used for two things:

- **Row identity** reported to catalog consumers (OData CSDL `<Key>`, dynamic OpenAPI, catalog discovery, catalog search, MCP dataset catalog).
- **Deterministic default read ordering** for paged reads (REST data API, OData, GraphQL, cross-source reads).

Autheris never discovers keys at runtime from a source database. The declared definition is the only key source, so a governed source login needs nothing beyond `SELECT` (see [Least-privilege grants](#6-least-privilege-grants)).

Related documents: [F-GOV-12 governance storage and schema shape verification](f-gov-12-multi-engine-governance-storage.md), [F-DATA-06 REST data API](f-data-06-governed-rest-data-api.md), [F-API-03 OData and OpenAPI](f-api-03-odata-openapi.md), [F-DATA-02 WebSQL](f-data-02-governed-websql.md).

---

## 2. The Definition Model

| Element | Description |
| :--- | :--- |
| `PrimaryKeyColumns` | Ordered list of column names. Composite keys are supported. Empty means "no declared primary key"; there is no implicit `id` default. |
| `PrimaryKeyDirections` | Optional sort direction per primary key column (`ASC` / `DESC`), parallel to `PrimaryKeyColumns`. Empty means every column is ascending (the default and the only form of a table without a declared direction). When present it has exactly one entry per key column. |
| `Indexes` | List of `TableIndexDefinition(Name, Columns, IsUnique, Directions)`. Columns are ordered; composite and non-unique indexes are supported. `Directions` is `null` (all ascending) or has one `ASC` / `DESC` entry per column; it is serialized as `directions` only when present. |

### 2.1 Validation rules (`TableKeyDefinitionValidator`)

The same validator runs on every write path (admin endpoint, every ingestion path, every repository upsert) and on every read of a persisted definition.

| Rule | Limit |
| :--- | :--- |
| Every referenced column exists in the table (case-insensitive, unique match); names are normalized to the catalog spelling. | - |
| No column appears twice within the primary key or within one index. | - |
| Primary key: 1 to 32 columns when present. | `MaxColumnsPerKey = 32` |
| Index: 1 to 32 columns. | `MaxColumnsPerKey = 32` |
| At most 64 indexes per table. | `MaxIndexesPerTable = 64` |
| Index name matches `^[A-Za-z_][A-Za-z0-9_]{0,127}$` and is unique per table (case-insensitive). | - |
| A direction list has exactly one entry per column of its key or index and only the values `ASC` / `DESC`; an all-ascending list is normalized to the canonical empty form. | - |

A unique index identical to the primary key is allowed. Size limits are checked before element data is copied. Validation messages echo caller-supplied names only in a bounded, markup-free form.

### 2.2 Fail closed on an invalid persisted definition

A persisted definition is validated again when it is read, with the full rule set plus storage integrity: ordinals must be contiguous from 1 without duplicates, and every referenced column id must belong to the same table. If any rule fails:

- `GetTableMetadataAsync` throws `TableMetadataUnavailableException` (the message contains only the table id). The table is **unavailable**, never treated as absent and never served with a partial key.
- `GetAllTablesAsync` leaves the table out and logs a Warning with the table id and the rule (no values).
- Read paths answer as for an unknown table on every transport: `TableMetadataUnavailableException` is a `TableNotFoundException`, so REST, WebSQL, OData, GraphQL, MCP and CDC give the same status, body and message as for a table that does not exist (no distinguishing detail).
- Write and ingestion paths (data catalog sync, OpenMetadata sync and webhook, dbt ingestion, OpenAPI ingestion) skip that table with a warning and never upsert it as a new table, so stored masking rules are preserved.

To repair such a table, replace the definition with the admin endpoint (section 4.1): it resolves the dataset from its catalog row without loading the stored definition, so an invalid stored definition does not block the repair. Alternatively correct or delete the rows in the three definition tables (section 3).

The key tables are probed once per repository instance (a positive result is cached; a missing table is re-probed and always means unavailable). `GetAllTablesAsync` reads the primary key and index rows of all tables with one query each (chunked), keeping the per-table validation.

### 2.3 Keep unless replaced

`TableMetadata.ReplaceKeyDefinition` is a write intent (default `false`). An upsert replaces the stored definition **only** when the flag is `true`; otherwise the stored definition is kept, even when the incoming metadata carries none. Clearing a definition means replacing it with empty lists. Repositories always return the flag as `false`, so copying a loaded object and writing it back can never wipe a definition. The governance ratchet carries an incoming definition only when the incoming flag is set.

---

## 3. Persistence

The definition is stored in three normalized tables that are part of the governance baseline schema on SQLite, PostgreSQL and SQL Server. Columns are referenced by `TABLE_COLUMNS.id` (rename-safe); ordinals are 1-based. There are no database-level foreign keys; integrity is enforced by the validator on write and on read.

```sql
CREATE TABLE TABLE_PRIMARY_KEY_COLUMNS (
    table_id         TEXT    NOT NULL,              -- TABLES.id
    key_ordinal      INTEGER NOT NULL CHECK (key_ordinal >= 1),
    table_column_id  TEXT    NOT NULL,              -- TABLE_COLUMNS.id of the same table
    is_descending    INTEGER NOT NULL DEFAULT 0 CHECK (is_descending IN (0, 1)),  -- governance step 0002
    PRIMARY KEY (table_id, key_ordinal)
);
CREATE UNIQUE INDEX UX_TABLE_PK_COLUMNS_COLUMN ON TABLE_PRIMARY_KEY_COLUMNS (table_id, table_column_id);

CREATE TABLE TABLE_INDEXES (
    id          TEXT PRIMARY KEY,
    table_id    TEXT    NOT NULL,                   -- TABLES.id
    index_name  TEXT    NOT NULL,
    is_unique   INTEGER NOT NULL                    -- 0 / 1
);
CREATE UNIQUE INDEX UX_TABLE_INDEXES_NAME ON TABLE_INDEXES (table_id, index_name);

CREATE TABLE TABLE_INDEX_COLUMNS (
    index_id         TEXT    NOT NULL,              -- TABLE_INDEXES.id
    column_ordinal   INTEGER NOT NULL CHECK (column_ordinal >= 1),
    table_column_id  TEXT    NOT NULL,              -- TABLE_COLUMNS.id of the same table
    is_descending    INTEGER NOT NULL DEFAULT 0 CHECK (is_descending IN (0, 1)),  -- governance step 0002
    PRIMARY KEY (index_id, column_ordinal)
);
CREATE UNIQUE INDEX UX_TABLE_INDEX_COLUMNS_COLUMN ON TABLE_INDEX_COLUMNS (index_id, table_column_id);
```

The DDL above is the SQLite form. PostgreSQL uses the same structure with lower-case index names and a `BOOLEAN` direction flag; SQL Server uses `NVARCHAR(128)` ids and names, `INT` ordinals and `BIT` flags in schema `dbo`.

**Directions (`is_descending`).** The two `is_descending` columns are added by the governance migration step `0002_key_and_index_column_direction` (guarded and idempotent on every engine; the column is `NOT NULL` with the constant default `0` / `FALSE`, so existing rows read as ascending and an older binary that inserts without the column also reads ascending). The reader accepts only `0` / `1` (or a boolean); any other value makes the table's definition unavailable (section 2.2) instead of ordering by a guess.

A replacement runs in one transaction: the index columns of the table's indexes, its indexes and its primary key columns are deleted, then the new rows are inserted in list order (ordinal = position + 1). The governance epoch is incremented and cached metadata is invalidated.

**Direct loads.** A client that writes these tables directly into the governance store bypasses the epoch increment, so running replicas keep cached metadata until the cache TTL expires. Load the data before Autheris starts, or bump the epoch afterwards. Bad rows are caught by the read-side validation (section 2.2).

---

## 4. Declaring Keys and Indexes

### 4.1 Admin endpoint

`PUT /api/v1/catalog/datasets/{id}/keys` (alias `PUT /api/catalog/datasets/{id}/keys`) replaces the declared definition of one dataset.

- **Authorization:** `GovernanceAdmin` policy. Anonymous callers receive `401`, other callers `403`.
- **`{id}`:** the table GUID (format `D`) or `domain.schema.table` (`/` is accepted as separator), at most 512 characters.

```http
PUT /api/v1/catalog/datasets/sales.dbo.order_lines/keys
Content-Type: application/json

{
  "primaryKey": ["order_id", "line_no"],
  "indexes": [
    { "name": "ux_order_lines_sku", "columns": ["order_id", "sku"], "unique": true },
    { "name": "ix_order_lines_ts",  "columns": ["created_at"] }
  ]
}
```

Directions are optional. `primaryKeyDirections` (parallel to `primaryKey` or its alias) and, per index, `directions` take the strings `ASC` / `DESC` (case-insensitive), one entry per column; omitted means all ascending:

```json
{ "primaryKey": ["device_id", "ts"], "primaryKeyDirections": ["ASC", "DESC"] }
```

An unknown value or a count that differs from the column list is rejected with `400` and the generic message (the value is never echoed, nothing is persisted or audited).

`primaryKeyColumns` is accepted as an alias of `primaryKey`. On an index, `unique` is the canonical flag (as in the OpenAPI extension) and `isUnique` is accepted as an alias; if both are given and disagree the request is rejected with `400`. Omitted lists are treated as empty, so `{}` clears the definition.

| Status | When | Body |
| :--- | :--- | :--- |
| `200` | Definition replaced. | `{ "datasetId", "primaryKey", "indexes" }` in catalog spelling; `primaryKeyDirections` (`ASC` / `DESC` strings) appears only when a key column is descending, an index carries `directions` only when one of its columns is. |
| `400` | Missing body, or a validation rule of section 2.1 failed. | `{ "error": "..." }` without stack trace or values. |
| `404` | The dataset is unknown or inactive. | The same fixed body `{ "error": "Dataset was not found." }` for both cases; the id is not echoed. A dataset whose stored definition is invalid (section 2.2) is **not** a 404: it can be repaired by this endpoint. |

The endpoint writes narrowly: `IGovernanceRepository.ReplaceTableKeyDefinitionAsync` reads the stored columns, validates against them, replaces only the three definition tables and bumps the epoch once, all in one transaction. Columns and masking rules are not rewritten, so a concurrent masking change is preserved.

**Audit.** Every successful replacement writes an audit entry `CATALOG_KEY_DEFINITION_CHANGED` through the HMAC-chained audit writer, with actor SID, request tenant, dataset id and the definition before and after (identifiers only, no data; `primaryKeyDirections` appears only when a column is descending, as the closed strings `ASC` / `DESC`). A change of directions alone is a change and is audited. When the stored definition was invalid, "before" is recorded as `{ "unavailable": true }` (no partial data). The entry is written **after** the commit: if the audit store is unavailable the caller receives `503`, but the definition change persists (the request can be repeated; replacing is idempotent).

The catalog is global (the `TABLES` table has no tenant column); a definition does not change authorization, so no four-eyes approval is required.

### 4.2 Ingestion entry points

Every ingestion path treats the source as untrusted. It replaces the stored definition **only when the source declares keys** (primary key and/or unique constraints) and the declaration is valid. A source that declares nothing leaves the stored definition untouched. An invalid declaration (unknown column, size limit, duplicate primary key, malformed shape) is dropped as a whole with a Warning (table id and rule, no values); the table itself is still ingested and the stored definition kept. A replacement logs one Information line (source, table id, column counts).

An ingested declaration replaces the **whole** definition (primary key and indexes). A source that declares only unique constraints therefore clears a primary key that an administrator declared before; declare the primary key at the source as well, or re-apply it with the admin endpoint afterwards.

| Source | Declaration read | Mapping |
| :--- | :--- | :--- |
| Data catalog sync (`DataCatalogSyncService`, Purview / Collibra / generic connectors) | `CatalogTableAsset.PrimaryKeyColumns` (with optional `PrimaryKeyDirections`) and `Indexes` (each with optional `Directions`); when no primary key is given, the columns flagged `CatalogColumnAsset.IsPrimaryKey` (in column order). | Primary key and indexes as declared. |
| OpenMetadata (`OpenMetadataSyncService`, sync and webhook) | `tableConstraints` of the table (requested with `fields=...,tableConstraints`). | `PRIMARY_KEY` -> primary key; each `UNIQUE` -> unique index `uq_<n>` (1-based by position). |
| dbt (`DbtArtifactStreamingParser`, `DbtMetadataIngestionService`) | Model-level and column-level `constraints` of type `primary_key` and `unique` (a model-level `columns` entry may end in `ASC` / `DESC`, e.g. `"ts DESC"`; the grammar of `KeyColumnSpec` allows exactly one trailing `ASC` / `DESC` after a plain identifier, any other shape such as `NULLS FIRST`, `COLLATE`, quotes or a second direction drops the whole declaration); data tests `unique` and `unique_combination_of_columns` attached to the model. | `primary_key` -> primary key; each unique declaration -> unique index `uq_<n>`. |
| OpenAPI ingestion (`OpenApiIngestionService`) | Vendor extensions on the schema object: `x-autheris-primary-key` (string array) and `x-autheris-indexes` (array of `{ "name", "columns", "unique", "directions" }`, `unique` optional boolean, `directions` optional), plus `x-autheris-primary-key-directions` next to the key. Directions are arrays of `ASC` / `DESC` strings (case-insensitive) with exactly one entry per column; any other shape drops the whole definition. | As declared. No key is guessed from `required`, `id`, `*_id` or the first property. |

OpenAPI example:

```yaml
components:
  schemas:
    OrderLine:
      type: object
      x-autheris-primary-key: [order_id, line_no]
      x-autheris-indexes:
        - name: ux_order_lines_sku
          columns: [order_id, sku]
          unique: true
      properties:
        order_id: { type: string }
        line_no:  { type: integer }
        sku:      { type: string }
```

### 4.3 Column Meta markers and the `(client_id, ts)` convention

Two further key sources are recognized after the declared definition:

- **Column `Meta` markers** (a declaration channel for metadata that only carries column attributes): `primary_key_ordinal` (1-based integer), or `is_primary_key = "true"`, or `role = "primary_key"`. Ordinals win over flags. The marker key is validated fail-closed by one shared parser (`TableKeyMarkers`, used by the repositories and every read path): a non-numeric or non-positive ordinal, a duplicate, a gap or two marker kinds that name different columns invalidate the whole marker key. Markers are a declaration channel only when **no declared primary key rows exist**; declared rows always win, so a stale marker can never produce a subset key. A replacing write rewrites the markers of all stored columns of the table.
- **The `(client_id, ts)` convention:** a table that has both columns uses `(client_id, ts)` as its row identity. `client_id` alone is never used as a key.

### 4.4 System tables

The virtual system tables (`governance.system.*`) carry their key definition in code; it replaces the stored definition when they are registered at startup.

---

## 5. Using the Definition

### 5.1 Row identity for catalog consumers

`TableRowIdentity.Select` picks the first complete candidate:

1. Declared primary key.
2. Declared unique index with the fewest columns (ties: definition order).
3. Column `Meta` marker key.
4. `(client_id, ts)` convention.

Consumers compute it on metadata that is **already filtered for the caller** (`TableKeyVisibility.Restrict`): every declared key, every index and the marker key survive only if all of their columns are visible to the caller; a candidate that touches a hidden column is dropped as a whole, never reported as a subset.

- **OData CSDL `<Key>`:** the row identity of the caller's filtered metadata; without one, all visible properties form a composite key (CSDL requires a key). A table without columns keeps the `id` placeholder.
- **Dynamic OpenAPI:** the declared primary key columns listed as `required` properties; declared indexes in `x-autheris-indexes` (`name`, `unique`, `columns`). Both only when wholly visible to the caller.
- **Catalog discovery, catalog search and MCP dataset catalog:** the declared primary key and `Indexes`, restricted to the columns that the response lists.

### 5.2 Default read ordering

`DefaultReadOrdering.Resolve(metadata, decision)` is the single ordering rule of every read path: the governed table-read planner (REST data API, OData), the GraphQL tree compiler and the cross-source path. It uses the same access decision that drives the projection.

Precedence:

| # | Rule | Condition |
| :--- | :--- | :--- |
| 1 | Declared primary key | Every key column is `Clear` for the caller. |
| 2 | Declared unique index | Among unique indexes whose columns are all `Clear`, the one with the fewest columns (ties: definition order). |
| 3 | Column `Meta` marker key | Every marker column is `Clear`. |
| 4 | `(client_id, ts)` convention | Both columns exist and are `Clear`. |
| 5 | Clear-column composite | All `Clear` columns with an orderable type for the table's dialect, in catalog order. |
| 6 | None | No orderable `Clear` column. |

**Clear only, all or nothing.** A key or index candidate with any column that is not readable in clear text (`Deny`, `Mask`, HMAC rules, catalog-sensitive columns without an explicit `Clear`) is skipped as a whole and the next rule applies. A prefix or subset of a key is never used, and skipping causes no error, so a response never reveals that a key contains a hidden column. The chosen rule is internal (logs, telemetry) and never appears in a response.

**Orderable types.** Unknown types count as orderable. Not orderable: `json`/`jsonb`, `xml`, binary types (`binary`, `varbinary`, `bytea`, `blob`, `image`, `raw`, `long raw`, `bfile`), spatial types (`geometry`, `geography`, `hierarchyid`, `sdo_geometry`), `sql_variant`/`variant`, structured types (`array`, `map`, `struct`, `list`, `union`, `row`, `object`, `...[]`) and any `(max)` type; on SQL Server and Oracle also LOBs (`text`, `ntext`, `clob`, `nclob`, Oracle `long`). `text` stays orderable on PostgreSQL, SQLite and Databricks.

**Directions.** The default order follows the declared direction of the chosen key or unique index: `ORDER BY a, b DESC` for a primary key `(a ASC, b DESC)`. Which candidate is chosen never depends on the direction, so the all-or-nothing rule above works the same for a descending key. Marker keys, the `(client_id, ts)` convention and the clear-column composite are ascending. A table without a declared direction compiles to byte-identical SQL. No `NULLS FIRST` / `NULLS LAST` is emitted: the placement of `NULL` values is defined by the engine (PostgreSQL sorts them first under `DESC`, SQL Server and SQLite treat `NULL` as the smallest value); declare keys on `NOT NULL` columns. Filters, row filters, masking and an explicit order are unaffected.

**Explicit ordering.** An explicit order (`orderBy`, `$orderby`, GraphQL `order`) is validated as before: unknown and denied columns get the same "does not exist" message (no existence oracle); masked columns are rejected. The default ordering columns that the caller did not name are then appended with their declared direction as a **tiebreaker** (a column the caller names keeps the caller's direction), so pages stay stable when the explicit columns contain duplicates. The tiebreaker is recomputed on the server for every request and is never written into a next link.

**No stable order (rule 6).**

- A request with a positive offset (`offset`, `$skip`) is rejected with `400` and the fixed text `Paging is not possible: no orderable column readable in clear text is available to order by.` (no column names).
- A first page (offset 0) is read without `ORDER BY`. If the look-ahead row shows that more rows exist, the request is rejected with the same `400`, instead of returning a page that the next page could duplicate or skip. A result that fits into one page is returned normally.
- The cross-source path applies the same checks.

**GraphQL.** The root list uses the full precedence; nested relation lists stop after rule 4 (no clear-column composite) to bound sort cost. When the default ordering has a column whose name the compiler cannot emit safely, the query is refused (it never runs without an ORDER BY), and a root page beyond the first (`offset > 0`) is refused when no clear orderable column exists, with the same text as the REST planner.

**Limitations.**

- A declared unique index is trusted as declared. Columns that contain `NULL` may tie; declare keys on `NOT NULL` columns.
- With the clear-column composite, rows that are equal in all orderable clear columns can still swap places between pages. Fully duplicate rows keep the returned multiset exact.
- The clear-column composite has no column cap. Its cost grows with the row count and is bounded by the execution timeout. Remedy: declare a primary key or a unique index.

### 5.2.1 Bulk keyset stream

The bulk table stream seeks on the primary key (else the first unique index) and follows the declared directions: `ORDER BY` uses each column's direction and the continuation predicate uses `<` for a descending column and `>` for an ascending one (the single-column form and every branch of the composite `OR` chain). The operator and the `ORDER BY` direction come from the same source. The cursor binds the direction signature in its signed context: `asc` when every column is ascending (cursors issued before directions existed stay valid), otherwise one word per column (for example `asc,desc`). A cursor issued before a direction change is rejected as `INVALID_CURSOR`; the stream never restarts silently. A row whose key value is `NULL` cannot be a cursor position (existing rule, independent of the direction).

### 5.2.2 Reporting

`describe_dataset` returns `primaryKeyOrder` (`[{ "column", "direction" }]`, key order, `ASC` / `DESC`) and the index `directions` only for a key or index whose columns are all visible to the caller; a key or index touching a hidden column is omitted as a whole, directions included. The catalog dataset detail reports `primaryKeyDirections` parallel to `primaryKeys` (omitted when every column is ascending). OData and the dynamic OpenAPI document have no direction concept and are unchanged.

### 5.3 OData next link

`@odata.nextLink` is emitted whenever the look-ahead row proves more rows, for keyed, unique-indexed and unkeyed tables alike. Each path segment (domain, schema, table) is URI-escaped; the link carries only the client's own query options.

---

## 6. Least-Privilege Grants

### 6.1 Source databases (governed reads)

Governed reads, OData and ingestion run no key or index catalog queries against sources (`sys.indexes`, `pg_index`, `pg_constraint`, `ALL_CONSTRAINTS`, `information_schema.table_constraints`, `pragma_index_list`, `duckdb_constraints` and similar); an architecture test enforces this for the catalog, data, OData and ingestion code. The exceptions are listed in the table below, including the catalog sync's key direction readers.

| Engine | Grants for governed reads |
| :--- | :--- |
| SQL Server | `CONNECT`; `SELECT` on each governed table or view (or on the schema). |
| PostgreSQL | `CONNECT`; `USAGE` on the schema; `SELECT` on each governed table or view. |
| Oracle | `CREATE SESSION`; `SELECT` on each governed table or view. |
| SQLite / DuckDB | Read-only file access (`Mode=ReadOnly`). |
| Databricks | `USE CATALOG`, `USE SCHEMA`, `SELECT`. |

The column-metadata readers (`sys.columns`/`sys.types`, `pg_catalog.pg_attribute`, `ALL_TAB_COLUMNS`, `pragma_table_info`, `information_schema.columns`) read column types for the compiler's type and collation safety. They read only metadata that the engine exposes for objects the login can select, so they need no extra grant.

Optional features need more than `SELECT`:

| Feature | Additional need |
| :--- | :--- |
| Governed stored procedures ([F-SQL-02](f-sql-02-governed-stored-procedures.md)) | `EXECUTE` on the procedure; `VIEW DEFINITION` for `StoredProcedureCatalogValidator` (module text and dependencies). |
| Procedure row scope (`SqlProcedureRowScopeResolver`) | Reads `sys.indexes` / `pg_index` on the source to prove **physical** uniqueness of the row-scope key (an authorization path, fails closed). It needs only the metadata visibility that `SELECT` grants. Declared indexes do not replace it. |
| Catalog sync key directions (`SqlServerKeyIndexMetadataReader`, `PostgreSqlKeyIndexMetadataReader`, used only by `GovernedCatalogProvider`) | Reads `sys.index_columns.is_descending_key` / `pg_index.indoption` with bound parameters to fill the direction of an already declared key whose column list matches the physical primary key or unique index exactly. It never adds, removes or reorders key columns, and declared or stored directions take precedence. Any read failure, ambiguity or mismatch leaves the key unchanged. SQL Server needs `VIEW DEFINITION` on the table; without it the directions stay unknown (ascending). SQLite, Oracle, DuckDB and Databricks are not read. |
| MSSQL change tracking ([F-CDC-02](f-cdc-02-mssql-change-tracking.md), `MssqlChangeTrackingPoller`) | `VIEW CHANGE TRACKING` on the tracked tables; reads the source index catalog for the primary key. |
| Governed DML | `INSERT`, `UPDATE`, `DELETE` on the target, only when DML is enabled for the data source. |

### 6.2 Governance database

| Login | SQLite | PostgreSQL | SQL Server |
| :--- | :--- | :--- | :--- |
| Runtime (`GovernanceDb:Migrations:Mode=Verify`) | Read/write on the database file (WAL). SQLite has no login separation. | `CONNECT`; `USAGE` on the schema; `SELECT, INSERT, UPDATE, DELETE` on all governance tables except `audit_log_entries` (`SELECT, INSERT`) and `autheris_schema_migrations` (`SELECT`); `USAGE, SELECT` on the `audit_log_entries` id sequence. Neither the owner of the objects nor a member of the owner role. | `CONNECT`; `SELECT, INSERT, UPDATE, DELETE` on all governance tables except `AUDIT_LOG_ENTRIES` (`SELECT, INSERT`), `autheris_schema_migrations` (`SELECT`) and `AUDIT_LOG_TRUNCATE_GUARD` (`VIEW DEFINITION` only, so that Verify can see the truncate guard; no data grant). No `ALTER`, `CONTROL`, `db_owner` or `db_ddladmin`. |
| Migration (`Mode=Apply`, `GovernanceDb:MigrationConnectionString` or a separate `--migrate-governance` job) | Same file. | Owner of the schema objects (`CREATE` on the schema; creates functions and triggers). | `db_ddladmin` plus `ALTER` on `dbo` (trigger). |

DDL rights are needed only for `Mode=Apply`. The schema lock (`pg_advisory_lock`, `sp_getapplock`) needs no grant beyond `public`. A runtime login without DDL rights fails `Mode=Apply` on an unmigrated database and reports the drift in `Mode=Verify` (see [F-GOV-12](f-gov-12-multi-engine-governance-storage.md#schema-shape-verification)).

---

## 7. Behaviour Changes

Compared with earlier builds:

- **No guessed `id` key.** A table has no primary key unless one is declared; the former `["id"]` default is gone. The OData CSDL and the dynamic OpenAPI no longer emit an `id` key that the table does not have; the CSDL uses the row identity or, without one, all visible properties.
- **Explicit ordering gets a tiebreaker.** `orderBy` / `$orderby` / GraphQL `order` are followed by the default ordering columns that the caller did not name.
- **OpenAPI ingestion no longer guesses keys** from `required`, `id`, `*_id` or the first property. Declare keys with `x-autheris-primary-key` / `x-autheris-indexes`.
- **Paging without a stable order returns `400`** (positive offset, or a first page with more rows) instead of returning pages that may duplicate or skip rows.
- **The governance database must be at the v1.5.0 base or newer.** Later versions upgrade by forward-only steps (see [F-GOV-12](f-gov-12-multi-engine-governance-storage.md#versioned-schema-migrations)); a pre-v1.5.0 database, or any database whose shape differs from the expected version, fails startup with a drift report (`GOVERNANCE_SCHEMA_UNSUPPORTED_BASE` / `GOVERNANCE_SCHEMA_DRIFT`, see [F-GOV-12](f-gov-12-multi-engine-governance-storage.md#schema-shape-verification)). The migration login must own the existing governance objects, because steps `ALTER` them.
- **WebSQL timeouts** are answered as RFC 9457 problem details (`application/problem+json`, see [F-DATA-02](f-data-02-governed-websql.md#44-execution-timeouts)).

---

## 8. Architecture

| Layer | Component |
| :--- | :--- |
| Domain | `TableMetadata.PrimaryKeyColumns`, `PrimaryKeyDirections`, `Indexes`, `ReplaceKeyDefinition`; `TableIndexDefinition`; `KeyColumnDirection`, `KeyDirections`; `TableKeyDefinitionValidator`, `TableKeyMarkers`, `TableDefinitionException`; `TableMetadataUnavailableException`; `AuditEventTypes.CatalogKeyDefinitionChanged`. |
| Application | `DefaultReadOrdering`, `TableRowIdentity`, `TableKeyVisibility` (both use the Domain `TableKeyMarkers` parser); `GovernedTableReadPlanner`, `GovernedDataQueryService`; `CatalogKeyDefinitionImport` (shared ingestion import); `OpenApiIngestionService`; `CatalogGovernanceRatchet`. |
| Infrastructure | `CatalogKeyDefinitionStore` and the `*GovernanceRepository.Catalog.cs` repositories (read, validate, replace); baseline DDL of the three tables. |
| Api / Extensions | `CatalogApiEndpoints` (`PUT .../keys`); `DataCatalogSyncService`, `OpenMetadataSyncService`, `DbtArtifactStreamingParser` / `DbtMetadataIngestionService`; `ODataHandler`, `ODataCsdlGenerator`; `TreeSqlCompiler` (GraphQL). |
