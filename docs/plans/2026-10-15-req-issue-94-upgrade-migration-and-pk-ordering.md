# PRD: REQ-GOV-94 - Governance Schema Shape Verification and Key-Based Read Ordering

**Document ID:** `REQ-GOV-94`
**Date:** 2026-10-15
**Status:** IMPLEMENTED (PR pending); APPROVED, amended by owner decision 2026-10-15 (Phase 2 plan: [2026-10-15-plan-issue-94-upgrade-migration-and-pk-ordering.md](2026-10-15-plan-issue-94-upgrade-migration-and-pk-ordering.md))
**Author:** Enterprise Product Manager
**Source:** GitHub issue #94 ("Update from v1.1.8 to v1.1.10: migration does not add columns, and tables without a primary key fail on ORDER BY id")
**Branch:** `fix/issue-94-upgrade-migration-pk` (base `main` `574c82f1`)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Related feature docs (read-only references):** [F-GOV-12 multi-engine governance storage](../features/f-gov-12-multi-engine-governance-storage.md)

---

## 0. Owner Decision 2026-10-15 (binding, supersedes conflicting text below)

Autheris does **not** ship upgrade or repair migrations. There is no migration `0002`, no `ALTER TABLE`, no rename and no backfill for older governance shapes on any engine. A client that loads governance data from an older installation (the PoC client "Talos") is responsible for providing it in the newest schema. Consequences:

- **Removed:** US-A1 and all of AC-A1.1 to AC-A1.8 (automatic upgrade of legacy shapes). Reason: owner decision; Autheris has no production deployment and keeps a single baseline (`0001`).
- **Kept:** US-A2 (read-only shape verification that fails closed). AC-A2.3 is restated: the migration version is not recorded when the shape is wrong.
- **Moot:** Q1 (PoC schema) and Q3 (duplicate `DATA_OWNERS.ad_sid`): no upgrade handling, no merge.
- **Changed by consequence:** migration `0001` and its checksum stay unchanged, so no key column can be added. AC-B1.2 and AC-B1.3 (persisted keys) are replaced: keys are not persisted; SQL source keys are discovered at runtime from source metadata and cached (AC-B1.4). The architecture decision is recorded in the Phase-2 plan.
- **Unchanged:** US-B1 (except the persistence ACs above), US-B2 and item 3 (triage), with the best-practice resolutions below.
- **Open questions resolved by best practice (owner instruction, 2026-10-15):**
  - Q1: the expected shape is generated from the current baseline (snapshot of a freshly migrated database per engine, strict-equality test), compares tables, columns (name and type family), primary keys and named indexes, reports all deviations at once in a deterministic message and tolerates extra objects. Tested with a synthetic old-shape database built from the v1.1.8 DDL.
  - Q2: deterministic total order with a unique tiebreaker (primary key, then a NOT NULL unique index, then all orderable clear-text columns); an explicit order gets the default ordering columns appended as tiebreaker (AC-B2.7 amended); `@odata.nextLink` whenever more rows exist; WebSQL timeouts return 504 as RFC 9457 problem details; a genuine slow sort on an unindexed column is expected behaviour (index recommended, no workaround).
  - Decisions and rationale: [Phase-2 plan](2026-10-15-plan-issue-94-upgrade-migration-and-pk-ordering.md), sections 0 and 2.

### 0.1 Owner Decisions 2026-10-15, second round (binding, supersede section 0 where they conflict)

- **OD-5 Declared keys and indexes.** Primary keys and indexes are part of the table definition: composite primary keys (ordered) and composite indexes (named, ordered columns, unique flag). The caller (Talos) supplies them when it defines or registers a table. They are persisted in the governance store **as part of the baseline schema** (`0001` DDL is extended; its checksum is a constant identity string and stays unchanged). Existing databases without the new tables are reported by the shape verifier; the caller supplies the newest schema (no migration). Definitions are validated fail-closed: key/index columns exist in the table's columns, no duplicates, contiguous ordinals, index names unique per table.
- **OD-6 Minimal rights (read only).** Autheris needs no metadata or DDL privileges on customer source databases. Runtime key discovery from source system catalogs is dropped (this supersedes the "runtime discovery" text of section 0 and AC-B1.4); the declared definition is the single source of truth. Default ordering: declared primary key, then declared unique index, then a composite of all clear-text orderable columns, then 400. On the governance database, `Mode=Verify` (shape verification and startup) must work without DDL rights; DDL is needed only for `Mode=Apply`. Least-privilege grants are documented per engine, and code that needs more than read on sources is flagged (Phase-2 plan section 8).

### 0.2 Revised acceptance criteria (second round; replace AC-B1.2 to AC-B1.5 and amend AC-B2.2)

- **AC-B1.2'** The baseline DDL of all three engines contains `TABLE_PRIMARY_KEY_COLUMNS`, `TABLE_INDEXES` and `TABLE_INDEX_COLUMNS` (names and columns in the Phase-2 plan, section 3). `0001` checksum constants are unchanged (test). A database without these tables fails the shape verification with them listed.
- **AC-B1.3'** `TableMetadata` carries `PrimaryKeyColumns` (ordered) and `Indexes` (`TableIndexDefinition`: name, ordered columns, unique flag). A composite primary key and composite unique and non-unique indexes round-trip through `UpsertTableMetadataAsync`/`GetTableMetadataAsync` (and `GetAllTablesAsync`) on SQLite, PostgreSQL and SQL Server (contract test per engine). An upsert without an explicit definition keeps the stored definition.
- **AC-B1.4'** Validation (fail closed, 400 at the APIs, exception at the repository): unknown column, duplicate column in a key or index, empty index, duplicate index name (case-insensitive), more than 32 columns per key/index or 64 indexes per table, invalid index name. A persisted definition with gaps in ordinals or a dangling column reference makes the table unavailable (logged, never silently ignored).
- **AC-B1.5'** Every table-definition entry point can supply keys and indexes: the repository upsert, a new admin endpoint `PUT /api/v1/catalog/datasets/{id}/keys`, the data catalog sync (`CatalogTableAsset`, including the existing `CatalogColumnAsset.IsPrimaryKey`), OpenMetadata `tableConstraints` (`PRIMARY_KEY`, `UNIQUE`), dbt `primary_key`/`unique` constraints, and OpenAPI vendor extensions `x-autheris-primary-key`/`x-autheris-indexes` (the `required`/`id`/first-column heuristic is removed). Direct loading into the governance store uses the documented tables.
- **AC-B1.6'** No query against a source system catalog for keys or indexes (architecture test over `Persistence/Catalog/**`). CSDL `<Key>` uses the declared primary key, else the declared unique index with the fewest columns, else the documented all-column composite; OpenAPI, catalog discovery (`PrimaryKeys`, `Indexes`), catalog search and the MCP dataset catalog report the declared definition.
- **AC-B2.2'** Default ordering: (1) declared primary key if every column is `Clear`; (2) else the declared unique index with the fewest columns whose columns are all `Clear` (ties: definition order); (3) else the composite of all `Clear` orderable columns; (4) else 400 as in AC-B2.2.3. Tests cover composite keys and composite unique indexes.
- **AC-LP1** Least-privilege integration tests (PostgreSQL and SQL Server Testcontainers): (a) a source login with `SELECT` on one table only reads it through the governed path with a declared composite key; (b) a governance runtime login with DML only starts in `Mode=Verify` (shape verification, post-conditions, store initialization) and serves catalog reads/writes; (c) the same login in `Mode=Apply` on an unmigrated database fails with a clear message.

---

## 1. Executive Summary & Problem Statement

### 1.1 What happened

A PoC (image `ghcr.io/themulle/autheris:v1.1.10`, SQL Server source with 148 tables, SQLite governance database first used under v1.1.8) passes all checks on v1.1.8 and breaks after the upgrade to v1.1.10 in two independent ways:

1. **Upgrade path (governance store).** Startup logs `Applying governance migration 1 'baseline_governance_schema'` and succeeds, yet every governance query then fails (`no such column: applicable_scopes`, `no such column: p.profile_id`). Version 1 is recorded as applied although the schema does not have the shape this build reads.
2. **Reads on tables without a primary key.** `GET /odata/v4/lwetem_prod/fms/air1?$top=5` returns `500 Invalid column name 'id'` (200 on v1.1.8).
3. **Not yet analysed:** a WebSQL statement with `ORDER BY` returns 504 after 10.6 s, and `@odata.nextLink` is missing.

### 1.2 Verified root causes

**RC-1: legacy schema shims were removed without a replacement (regression in v1.1.9, commit `424fe0d0`, consolidated in `83dc9041`).**
Up to v1.1.8 every startup ran idempotent shape repair (`SqliteGovernanceRepository.Schema.cs` at tag `v1.1.8`):

| v1.1.8 shim | Effect |
|---|---|
| `EnsureTableColumns` | `TABLES`: `data_source_type`, `http_endpoint_json`, `plugin_name`, `description`, `long_description`, `doc_source` |
| `EnsureTableColumnFields` | `TABLE_COLUMNS`: `description`, `long_description`, `doc_source`, `meta_json` |
| `EnsureConsentColumns` | `CONSENTS.tenant_id` |
| `EnsureConsentRequestColumns` | `CONSENT_REQUESTS`: `itsm_ticket_id`, `tenant_id`, `requester_identifiers_json` |
| `EnsureConsentRowFilterColumns` | `CONSENT_ROW_FILTERS`: 11 columns (`filter_type` ... `additional_hops_json`) |
| `EnsureConsentColumnRuleColumns` | `CONSENT_COLUMN_RULES.applicable_scopes` |
| `EnsureAuditLogColumns` | `AUDIT_LOG_ENTRIES`: `tenant_id`, `seq`, indexes `idx_audit_seq`, `idx_audit_tenant_id` |
| `EnsureAccessProfilesTableMigration` | legacy `ACCESS_PROFILES(id, ...)` renamed to `VIRTUAL_FILTER_ACCESS_PROFILES`, freeing the name for the new `ACCESS_PROFILES(profile_id, ...)` |
| `DeduplicateDataOwnersBeforeIndex` | merges duplicate `DATA_OWNERS.ad_sid` before `UX_DATA_OWNERS_SID` |

The same gap exists on the other engines: v1.1.8 SQL Server used `COL_LENGTH` guards for `CONSENT_COLUMN_RULES.applicable_scopes` and `TABLE_COLUMNS.meta_json`; PostgreSQL used `ADD COLUMN IF NOT EXISTS applicable_scopes`. The current `*BaselineSchema.cs` files contain only `CREATE ... IF NOT EXISTS`, so an existing table keeps its old shape. `GovernanceMigrationRunnerBase` then records `0001` because the step did not throw; there is no post-migration shape verification beyond dialect post-conditions (audit trigger).

The two reported errors map exactly: `applicable_scopes` (shim `EnsureConsentColumnRuleColumns`) and `p.profile_id` (a legacy `ACCESS_PROFILES` table that was never renamed, so `CREATE TABLE IF NOT EXISTS ACCESS_PROFILES` was a no-op).

Scope note: an Autheris-created database that was *started* under v1.1.8 already carries the v1.1.8 shape (the shims ran), and the current baseline adds only new tables and the two audit indexes on top of it; such a database is expected to upgrade cleanly. The failing database was produced by a custom script and had never been reconciled. This does not make the bug invalid: any database that predates the shims (older Autheris versions, restored backups, scripted seeds) hits it, and **all databases that ran v1.1.9/v1.1.10 on a wrong shape now carry `0001` as applied**, so a fix inside `0001` would never run for them.

**RC-2: `TableMetadata.PrimaryKeyColumns` defaults to `["id"]` and is never persisted.**
- `src/Autheris.Domain/Model/TableModels.cs:114`: `PrimaryKeyColumns { get; init; } = new[] { "id" }`.
- SQLite `GetTableMetadataAsync` (`SqliteGovernanceRepository.Catalog.cs:184`) does not set the property (default `["id"]`); PostgreSQL (`:129`) and SQL Server (`:130`) hard-code `["id"]`. The governance schema has no column for key membership, so keys computed by `OpenApiIngestionService` (`:313-336`) are dropped on upsert.
- `GovernedTableReadPlanner.ResolveOrderBy` (`:188`) orders by `PrimaryKeyColumns` whenever `Count > 0`, without checking that the column exists or is readable in clear text. The first-clear-column fallback (`:192`) is therefore unreachable. Even `$skip=0` requests fail because the ORDER BY is always emitted.
- v1.1.8 (`SqlDataSourceExecutor.cs:321`) filtered the key through `metadata.HasColumn(pk)`, so it never referenced a non-existent column. The regression came with the WP-ARC-B-01/B-02 table-read planner.
- Secondary findings, same root cause:
  - `GovernedDataQueryService.cs:142` rejects offset paging when `PrimaryKeyColumns.Count == 0`, which contradicts the planner fallback once the default is removed.
  - The planner orders by a key column without checking it is `Clear`; `TreeSqlCompiler.cs:287` checks this correctly. Ordering by a masked column is an ordering oracle (it leaks the relative order of masked values).
  - `ODataCsdlGenerator.ResolveEntityKeys` has its own fallback chain (declared key, then `id`, then all columns); `CatalogSearchEngine`, `CatalogDiscoveryService`, `McpDatasetCatalog` and `DynamicOpenApiGenerator` report `id` as the key for tables that have no such column.
  - Live source introspection (`IColumnMetadataReader` implementations used by `GovernedCatalogProvider`) reads names, types and collations but not key membership.

**RC-3 (hypothesis, item 3).** See section 6.

### 1.3 Target audience
Operators upgrading Autheris, data consumers using OData/REST/GraphQL/WebSQL over legacy sources (data warehouses and staging tables often have no primary key), and security officers who rely on the fail-closed startup.

### 1.4 Qualification
- **Type:** bug, regression (v1.1.8 -> v1.1.9/v1.1.10) in two subsystems.
- **Severity:** **High.** RC-1 makes the gateway fully unusable after an upgrade while reporting a healthy migration (silent corruption of the "schema is current" invariant). RC-2 makes every unkeyed table unreadable over the governed table-read paths. No data leak; both fail with errors. Autheris has no production deployment yet, so breaking changes are allowed, but this is the first real upgrade path and must be fixed before the next release.
- **Deduplication:** No existing feature or plan covers this. F-GOV-12 documents the migration catalogue (`0001` only). `impl-arch-r1-r3-pipeline-persistence.md` lists the ARC-I-04 remainder (not-ready guard, I/O-free constructors, Helm init job), which is complementary and stays there. `impl-arch-r0-r2-trust-ha-eventing.md` already asks the MSSQL CDC poller to stop defaulting the key to `[Id]`; this PRD applies the same principle to the catalog and does not change the CDC poller.
- **Strategic fit:** fail-closed startup and deterministic, policy-aware paging are core Autheris guarantees. No anti-pattern in the request; the requester's "or abort with a clear message" option is adopted as the behaviour for unknown shapes.

---

## 2. Market & Best-Practice Benchmark

| Practice | Reference behaviour | Consequence for Autheris |
|---|---|---|
| Adopting a pre-existing schema | Flyway `baselineOnMigrate` only marks a baseline when the operator opts in and asserts the schema matches; Liquibase uses `changelogSync` plus preconditions (`columnExists`, `tableExists`) to make changesets conditional; EF Core expects the snapshot to match and offers no automatic adoption. | Recording `0001` on a database whose shape was never checked is the anti-pattern these tools avoid. Known legacy shapes may be upgraded by conditional (precondition-guarded) DDL; anything else must stop. |
| Schema drift detection | Flyway `validate`, Liquibase `diff`, Atlas `schema inspect`/lint: compare the live schema with the expected model and fail before the app serves traffic. | Add an expected-shape verification that runs after migrations and in `Mode=Verify`, and fails startup with a list of missing objects. |
| Already-applied history | Migration tools never edit an applied migration (checksum change = drift error). Fixes ship as a new version. | The reconciliation must be migration `0002`, not a change to `0001` (Autheris' own checksum check would otherwise refuse every v1.1.9/v1.1.10 database). |
| Deterministic paging | OData v4 server-driven paging and SQL `OFFSET`/`FETCH` require a total, stable order; SQL Server rejects `OFFSET` without `ORDER BY`; ordering on a non-unique column yields duplicated/skipped rows across pages. | Order by the true key when it exists; otherwise by a deterministic composite of orderable clear columns; never by a guessed column name. |
| Fail closed vs fail open | NIST SP 800-207 and the Autheris governance rules: deny or stop when the trust inputs are uncertain. | Unknown governance schema shape: stop at startup (fail closed). Unknown table key: do not fail the read; use a deterministic, policy-safe fallback (fail-safe, because ordering affects stability, not authorization). Masked columns are never used for ordering (OWASP API3:2023, inference via ordering). |

Competitors: Hasura refuses to track tables without a primary key for some features but still serves plain selects with a default ordering; Data API Builder requires a key or an explicit `key-fields` declaration per entity. Autheris will serve unkeyed tables (common in PoC and warehouse sources) with a documented deterministic fallback, and allows the key to be declared or discovered.

---

## 3. Scope

### 3.1 In scope
- **A. Governance schema shape verification** (read-only, fail closed) for SQLite, PostgreSQL and SQL Server. ~~Upgrade reconciliation~~ removed by owner decision 2026-10-15 (section 0).
- **B. Primary key truth and ordering** for governed table reads (OData, REST data API, GraphQL table reads, MCP dataset reads that use `GovernedTableReadPlanner`/`GovernedDataQueryService`) and catalog/key reporting (CSDL, OpenAPI, catalog discovery, MCP catalog).
- **C. Item 3 triage** (time-boxed, see section 6).

### 3.2 Non-goals
- Re-introducing ad-hoc constructor or startup "Ensure*" shims outside the migration runner.
- Repairing arbitrary hand-made schemas (unknown tables/columns with conflicting types, missing primary keys on governance tables, data repair beyond the v1.1.8 shim semantics).
- A down-migration path or automatic backup (operators back up before upgrade; the runbook says so).
- Changing the MSSQL CDC poller key handling (owned by `impl-arch-r0-r2-trust-ha-eventing.md`).
- Keyset (seek) pagination or `$skiptoken`; offset paging stays.
- Changing OData `$top` semantics (today `$top` acts as the page size and a `nextLink` is emitted when more rows exist; spec alignment is a separate topic).
- WebSQL query performance tuning, unless triage (section 6) proves an Autheris defect.

---

## 4. User Stories & Acceptance Criteria

### US-A1: Automatic upgrade of known legacy governance shapes - REMOVED (owner decision 2026-10-15)

> **Removed.** Autheris ships no upgrade/repair migrations; the client supplies governance data in the newest schema (section 0). AC-A1.1 to AC-A1.8 below are kept for traceability only and are not implemented.
*As an operator, I want an upgrade from any Autheris version up to v1.1.8 (and from a v1.1.9/v1.1.10 database that recorded `0001` on an old shape) to complete automatically, so that I do not need manual SQL patches.*

- **AC-A1.1** A new migration `0002` (name proposed: `reconcile_legacy_governance_shape`) exists for all three engines. It applies, idempotently and guarded by existence checks (`PRAGMA table_info` / `information_schema` / `COL_LENGTH`), every column, index and rename the v1.1.8 shims performed (table in section 1.2), with the same types and defaults as the current baseline. Migration `0001` and its checksum stay unchanged.
- **AC-A1.2** Fixture test (SQLite): a database with the v1.1.8 *pre-shim* shape (e.g. `CONSENT_COLUMN_RULES` without `applicable_scopes`, `AUDIT_LOG_ENTRIES` without `tenant_id`/`seq`, legacy `ACCESS_PROFILES(id, ...)`, `CONSENT_ROW_FILTERS` with only the base columns) and **no** `autheris_schema_migrations` table migrates to `0001`+`0002`, and afterwards consent, access-profile, catalog and audit read/write smoke operations succeed.
- **AC-A1.3** Fixture test (SQLite): the same old-shape database **with `0001` already recorded** (the v1.1.10 state of the reporter) is reconciled by `0002` on the next start.
- **AC-A1.4** Legacy `ACCESS_PROFILES(id, ...)`: when `VIRTUAL_FILTER_ACCESS_PROFILES` does not exist or is empty, the legacy table becomes `VIRTUAL_FILTER_ACCESS_PROFILES` and a current `ACCESS_PROFILES` is created; rows are preserved. When both contain rows, startup fails closed with a message naming both tables (no data is dropped or merged silently).
- **AC-A1.5** Duplicate `DATA_OWNERS.ad_sid` values are merged with the v1.1.8 semantics before `UX_DATA_OWNERS_SID` is created, or startup fails closed with the duplicate SIDs listed; the architect decides which, with a recorded rationale.
- **AC-A1.6** Equivalent PostgreSQL and SQL Server fixture tests (integration project, Testcontainers) cover at least `applicable_scopes` and `meta_json` on a pre-existing table with `0001` recorded.
- **AC-A1.7** A database created fresh by the current build and a database already at `0002` are unchanged by a second start (idempotence; no DDL executed, verified by schema snapshot equality).
- **AC-A1.8** An Autheris-created v1.1.8 SQLite database (created by starting the v1.1.8 schema code, captured as a test fixture or generated from the `v1.1.8` DDL) upgrades without errors.

### US-A2: Fail closed on an unknown or incomplete governance shape
*As a security officer, I want the gateway to refuse to start when the governance schema does not match what this build reads, instead of starting and failing every request.*

- **AC-A2.1** After all pending migrations (and in `Mode=Verify` instead of applying), the runner verifies the expected shape: every table, column and index that the build's repositories reference exists. The expected model is derived from one source of truth per engine (the baseline plus later migrations), not a hand-maintained second list that can diverge; a unit test proves the verification model and the baseline DDL agree (a fresh database passes, a database with any single expected column dropped fails).
- **AC-A2.2** On a mismatch, startup fails with `GovernanceSchemaDriftException` and a single actionable message: engine, database, the list of missing objects (`TABLE.column`, index names), the applied migration versions, and the remediation (restore from backup or contact support; a link to the runbook section). No secret or connection string is logged.
- **AC-A2.3** (restated 2026-10-15) When the shape is wrong, the migration version is not recorded: the verification runs inside the run's lock after the last pending step and before its version insert (SQLite: the whole run rolls back). A database that already records `0001` but has a wrong shape fails startup on every start. The verification is read-only: no DDL and no data change.
- **AC-A2.4** Extra, unknown tables or columns do not fail verification (they are tolerated and logged once at Information level); only missing or type-incompatible expected objects fail.
- **AC-A2.5** `Mode=Skip` keeps skipping (operator responsibility, already logged as a warning).

### US-B1: Truthful primary keys in the catalog
*As a data consumer, I want the catalog to report the real key of a table, or none, never a guessed `id`.*

- **AC-B1.1** `TableMetadata.PrimaryKeyColumns` defaults to an empty list. No repository or service substitutes `["id"]` (architecture test or grep-based test forbids the literal default in `TableModels.cs` and the three `*GovernanceRepository.Catalog.cs` files). Virtual system tables keep their explicit keys.
- **AC-B1.2** ~~REPLACED 2026-10-15, then superseded by AC-B1.2'/B1.3' (section 0.2)~~. Original text: Key membership is persisted per column (proposed: nullable `TABLE_COLUMNS.key_ordinal`, added by a migration on all three engines) and round-trips through `UpsertTableMetadataAsync`/`GetTableMetadataAsync` on all three engines; contract test per engine.
- **AC-B1.3** ~~REPLACED 2026-10-15, then superseded by AC-B1.5' (section 0.2)~~. Original text: Keys supplied by catalog ingestion (OpenAPI ingestion, OpenMetadata `tableConstraints` of type `PRIMARY_KEY` when present, dbt where a primary key constraint is declared, admin catalog APIs) are persisted. Ingestion paths that today copy `existingMeta.PrimaryKeyColumns` keep doing so.
- **AC-B1.4** ~~SUPERSEDED by OD-6 / AC-B1.6'~~ SQL sources (amended 2026-10-15: primary key, else a NOT NULL unique index): primary key columns are discovered from the live source metadata where the dialect exposes them (SQL Server `sys.indexes.is_primary_key`, PostgreSQL `pg_index.indisprimary`, Oracle `ALL_CONSTRAINTS` type `P`, SQLite `pragma table_info.pk`, DuckDB constraints; Databricks informational constraints if available, otherwise none). Discovery results are cached with the existing `GovernedCatalogProvider` TTL. Precedence (amended 2026-10-15): an in-memory declared key (virtual system tables) if all its columns are governed columns, else the discovered key if all its columns are governed columns, else none.
- **AC-B1.5** ~~SUPERSEDED by AC-B1.6'~~ CSDL `<Key>`, OpenAPI, catalog discovery (`PrimaryKeys`), catalog search and MCP dataset catalog all use one shared key resolver; for a table without a resolvable key they report no key (or, for CSDL only, the documented existing composite fallback, because OData entity types require a key).

### US-B2: Deterministic, policy-safe ordering without a key
*As a data consumer, I want `$top`/`$skip`/`limit`/`offset` reads on tables without a primary key to succeed with stable pages.*

- **AC-B2.1** Regression test reproducing the issue: a SQL Server (or SQLite unit-level) table `fms.air1` without a primary key and without an `id` column; `GET /odata/v4/<ds>/fms/air1?$top=5` returns 200 with 5 rows. The generated SQL contains no reference to a column that is not in the table.
- **AC-B2.2** Default ordering when the request names none:
  1. the resolved key, if every key column exists and is `Clear` for the caller;
  2. otherwise a deterministic composite of all `Clear` columns whose type is orderable on the dialect (excluding LOB/JSON/XML/binary/`text`/`ntext`/`image`/`nvarchar(max)`-class types), in catalog ordinal order;
  3. otherwise: a request with `offset > 0` (or one that would produce a `nextLink`) is rejected with 400 and a clear message (`no orderable column readable in clear text`); a request with `offset = 0` is served without ORDER BY.
- **AC-B2.3** A key or fallback column that is masked or denied for the caller is never used in ORDER BY (oracle protection). Test: table with key `ssn` masked for the caller orders by the fallback, not by `ssn`.
- **AC-B2.4** `GovernedDataQueryService` offset-paging validation uses the same resolver as the planner (no contradiction between "needs a primary key" and the fallback).
- **AC-B2.5** Paging stability test: for an unkeyed table with distinct rows, reading all pages via `nextLink` returns every row exactly once; for duplicate rows, the multiset of returned rows equals the table contents.
- **AC-B2.6** `@odata.nextLink` is present when more rows exist than the page size, for keyed and unkeyed tables (OData handler test).
- **AC-B2.7** (amended 2026-10-15) Explicit `$orderby`/`orderBy` keeps its validation, including the masked-column rejection; the default ordering columns that are not already named (clear-text only) are appended as tiebreaker so page boundaries are deterministic.
- **AC-B2.8** The fallback is documented in the data API and OData reference docs (Phase 6), including the performance note in section 7.

---

## 5. Non-Functional Requirements

- **Security:** fail closed at startup for unknown governance shapes; no ordering on masked/denied columns; error messages to clients do not reveal the governance schema; startup messages do not contain credentials. Phase 3 must review: the `ACCESS_PROFILES` rename path (no privilege change through mixing access-profile and virtual-filter profile rows), duplicate-owner merge (ownership must not be widened), and the ordering-oracle guard.
- **Integrity:** migrations are transactional where the engine allows; the SQLite run stays one `BEGIN IMMEDIATE` transaction; SQL Server/PostgreSQL steps use step transactions and the existing schema lock. The audit hash chain is unaffected (adding `tenant_id`/`seq` with defaults must keep legacy rows verifiable with the v1 payload, as in v1.1.8).
- **Performance:** shape verification runs once per startup (catalog queries only, target < 200 ms on SQLite, < 1 s on a remote server). Key discovery is cached; no per-request source metadata round trip on a cache hit. Hot read path allocations are not increased (resolver results cached with the metadata).
- **Observability:** each reconciliation action logs one Information line (`Added column CONSENT_COLUMN_RULES.applicable_scopes`), the drift failure logs one Error line with the full list; an OpenTelemetry startup span attribute `autheris.governance.schema.reconciled_objects` (count). Use of the unkeyed ordering fallback is logged at Debug level once per table and cache period.
- **Compatibility:** `0001` checksum unchanged on all engines; no configuration change needed for the upgrade.

---

## 6. Item 3: WebSQL 504 and missing `@odata.nextLink` (triage, time-boxed)

**Hypothesis.**
- *Missing `@odata.nextLink`:* a consequence of RC-2. The OData handler only emits a `nextLink` after a successful page (`ODataHandler.cs:465`); the request fails with 500 before that. Expected to disappear with US-B2 (covered by AC-B2.6).
- *WebSQL 504 after 10.6 s:* WebSQL does not use `GovernedTableReadPlanner`; it compiles the user's SQL through the SqlGate/AST compiler, which adds a key ordering only when every key column is `Clear` and the statement has no ORDER BY (`TreeSqlCompiler.cs:287`). A user `ORDER BY` on a non-indexed column of a large SQL Server table sorts the full table and hits the configured `WebSql:ExecutionTimeoutSeconds` (the 504 is the timeout working as designed). A secondary possibility: with a stale `["id"]` key, an unordered statement gains `ORDER BY id`; for a table without `id` the compiler resolves the column to null and drops the key ordering, so this is unlikely to cause a timeout.

**In scope:** only the triage. **Acceptance:**
- **AC-C1** After A and B are implemented, a test executes the reporter's statement shape (`SELECT ... FROM <unkeyed table> ORDER BY <non-key column> LIMIT n` via WebSQL) against a test table and asserts the compiled SQL contains exactly the requested ORDER BY and no reference to a non-existent column.
- **AC-C2** The Phase-2 plan records the result. If the 504 is reproduced only as a genuine long-running sort, it is closed as "by design" with a runbook note (raise the timeout or add an index); if an Autheris-generated clause is responsible, it is fixed in this change. The owner is asked for the statement text, the configured timeout and the source execution plan (open question Q2).

---

## 7. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Expected-shape model diverges from the DDL | False startup failures or missed drift | Derive from one source per engine; AC-A2.1 round-trip test on every engine in CI. |
| SQLite `ALTER TABLE ADD COLUMN ... NOT NULL` requires a non-null default | Migration failure on old shapes | Use the v1.1.8 column definitions (all `NOT NULL` adds carry defaults). |
| `ACCESS_PROFILES` rename with data in both tables | Data loss or privilege mix-up | Fail closed (AC-A1.4); Phase 3 review. |
| Composite fallback ORDER BY on wide or large tables | Slow first page, timeouts on sources without indexes | Restrict to orderable types; offset 0 with a single page still needs the order for a `nextLink`; document; allow declaring a key via catalog (US-B1) as the fast path; the architect may propose a configurable cap (e.g. first N orderable columns) with a measured trade-off. |
| Key discovery adds source round trips | Latency | Reuse the `GovernedCatalogProvider` cache and its TTL; discovery is part of the existing column metadata query where possible. |
| Behaviour change: catalogs/CSDL/OpenAPI no longer report `id` for unkeyed tables | Client code generators see a different key | Acceptable (no production deployment yet); release note required. |
| PostgreSQL/SQL Server DBs with partial shapes in the field | Upgrade failure | Covered by `0002` on all engines (AC-A1.6) plus drift verification. |

---

## 8. Open Questions for the Owner

- **Q1** Please share the schema (`sqlite3 gov.db .schema`) of the PoC database before the workaround script, or the creation script. It becomes the AC-A1.3 fixture. Also, which tables and indexes did you count as missing (the issue mentions 7 tables and 6 indexes)? On the current baseline, `CREATE TABLE IF NOT EXISTS` should create missing tables, so missing tables would point to a different failure mode.
- **Q2** WebSQL item 3: the exact statement, the configured `WebSql:ExecutionTimeoutSeconds`, row count of the table, and whether the ORDER BY column is indexed.
- **Q3** Duplicate `DATA_OWNERS.ad_sid`: automatic merge (v1.1.8 behaviour) or fail closed? PM default: fail closed with the list, because merging changes ownership and approval routing; the architect may argue for the merge.

---

## 9. Rollout & Telemetry

- Ships as a patch release; release notes: "Upgrades from v1.1.8 and older now reconcile the governance schema automatically; startup fails with a list of missing objects for unknown shapes"; "Tables without a primary key are ordered deterministically; catalogs no longer report a guessed `id` key".
- Runbook (Phase 6): back up before upgrade; how to read the drift error; `--migrate-governance` dry-run via `Mode=Verify`.
- F-GOV-12 migration catalogue table gains `0002` (and the key-ordinal migration if separate).

## 10. Lifecycle Hand-off

| Phase | Owner | Deliverable |
|---|---|---|
| 2 | `csharp-architect` | Implementation plan in `docs/plans/` (migration `0002` design per engine, shape verification model, key column persistence and discovery, shared key resolver, planner/service changes, test matrix). Resolve AC-A1.5 and the persisted-vs-discovered key precedence. |
| 3 | `csharp-security-expert` | Review of rename/merge paths, ordering oracle, error message content. |
| 4 | `csharp-tdd-developer` | Red tests first: AC-A1.2, AC-A1.3, AC-B2.1. |
| 5 | `csharp-code-reviewer` | Review. |
| 6 | `documentation-expert` | F-GOV-12, data API/OData reference, runbook, master plan. |

## 11. Changelog

| Date | Change |
|---|---|
| 2026-10-15 | Initial PRD (Phase 1). |
| 2026-10-15 | Owner decisions, second round (OD-5, OD-6): declared composite primary keys and indexes persisted in the extended baseline; runtime source key discovery dropped; least-privilege goal and grants; ACs B1.2' to B1.6', B2.2', LP1 added (section 0.2). |
| 2026-10-15 | Owner instruction: open questions resolved by best practice (Q1 shape derivation, Q2 paging/tiebreaker/problem details); AC-B1.4 adds the unique-index step; AC-B2.7 amended. |
| 2026-10-15 | Owner decision: no upgrade/repair migrations (US-A1 / AC-A1.x removed), shape verification kept (AC-A2.3 restated), Q1/Q3 moot, key persistence (AC-B1.2/B1.3) replaced by runtime discovery because `0001` must stay unchanged. Title changed accordingly. |
