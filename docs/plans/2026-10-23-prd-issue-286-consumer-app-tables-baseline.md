# PRD: Issue #286 - CONSUMER_APP_* tables belong in the baseline, not runtime DDL

Plan ID: `REQ-CONS-286` | Phase 1 (Product Manager) | Date: 2026-10-23

## Problem
`ConsumerAggregationStore` (`src/Autheris.Infrastructure/Persistence/ConsumerAggregationStore.cs`) runs `EnsureTablesCreatedAsync`
(`CREATE TABLE IF NOT EXISTS` for `CONSUMER_APP_DAILY`, `CONSUMER_APP_ACTOR_DAILY`, `CONSUMER_APP_QUERYNAME_DAILY`) before every
read and write (6 call sites). With a least-privilege runtime login on PostgreSQL (`Migrations:Mode=Verify`, F-GOV-12/F-GOV-21) this fails
every 15 s (`permission denied for schema public`; PostgreSQL checks `CREATE` before `IF NOT EXISTS`). Usage statistics and lineage impact stay empty.
Further defects found:
- The tables are in none of the three baselines (`SqliteBaselineSchema`, `PostgreSqlBaselineSchema`, `SqlServerBaselineSchema`) and none of the
  three shape files (`Migrations/Shape/governance-shape.{sqlite,postgresql,sqlserver}.json`), so Verify cannot detect their absence.
- The DDL is SQLite-flavoured (`TEXT` primary keys, `CREATE TABLE IF NOT EXISTS`); it is invalid on SQL Server (no `IF NOT EXISTS`, `TEXT` not usable as key), so the feature is likely broken there as well.

## Scope audit of other runtime DDL (governance stores)
Grep of `src` for `EnsureTablesCreated`, `CREATE TABLE`, `CREATE INDEX`, `ALTER TABLE` outside `Migrations/`:
- `ConsumerAggregationStore`: the only governance store with runtime DDL. IN SCOPE.
- `IdentityStore.cs:316` `IF NOT EXISTS (SELECT ...)` is a data-level idempotent insert, not DDL. Out of scope.
- `DuckDbOlapEngine`, `DuckDbPersistentTableCacheStore`, `InMemoryTableCacheStore`: DDL against local DuckDB/in-memory cache files, not the governance database. Out of scope (note only).
- `SqliteGovernanceMigrationRunner` `autheris_schema_migrations`: migrator-owned bootstrap. Out of scope.

## Requirements
1. REQ-286-1: Add the three tables to the baseline (versioned migration step 1, no new repair step, no upgrade path; owner rule: caller supplies newest schema) for SQLite, PostgreSql and SqlServer, each with provider-correct types and the existing composite primary keys.
2. REQ-286-2: Column semantics unchanged (names, nullability, defaults, PK columns); PostgreSql uses `TEXT`/`BIGINT`, SqlServer uses bounded `NVARCHAR` for key columns and `BIGINT` counters, tenant isolation via `tenant_id` first in every PK.
3. REQ-286-3: Remove `EnsureTablesCreatedAsync` and all its call sites. The runtime never executes DDL for these tables on any provider; reads/writes touch only existing tables.
4. REQ-286-4: Extend the three shape files so Verify (`GovernanceSchemaShapeVerifier`) fails closed (not ready) when any `CONSUMER_APP_*` table or column is missing; Apply mode creates them from the baseline.
5. REQ-286-5: Missing table at runtime (e.g. stale schema): fail closed with one clear structured error; no DDL retry, no 15 s warning flood (log once / throttle). Data access, policies and readiness unaffected by consumer-statistics failure (current behaviour kept).
6. REQ-286-6: Runtime login needs only `SELECT, INSERT, UPDATE, DELETE` (retention) on the three tables; document the grants in the existing F-GOV-12/F-GOV-21 manual only if the owner requests feature docs (no docs phase).
7. REQ-286-7: Architecture test: no `CREATE TABLE|ALTER TABLE|CREATE INDEX` string literals in governance stores outside `Persistence/Migrations/` (guards against regression).

## Acceptance criteria
1. AC-1: Fresh SQLite, PostgreSql (Testcontainers) and SqlServer governance DBs migrated via the baseline contain the three tables; shape verifier passes on all three.
2. AC-2: PostgreSql integration test: migrate with DDL owner, run runtime as login without `CREATE` on schema `public` and `Mode=Verify`; record usage, actor sample, query name, read stats and lineage impact all succeed with no warning logs.
3. AC-3: Verify on a DB lacking any of the three tables reports shape mismatch (not ready) naming the table.
4. AC-4: SqlServer record/read round-trip works (previously invalid DDL).
5. AC-5: Existing `ConsumerAggregationSecurityTests` and `TableConsumersDualTierSecurityTests` stay green (tenant isolation, actor hashing).
6. AC-6: Static check REQ-286-7 passes; `EnsureTablesCreatedAsync` no longer exists.

## Open questions (recommended default)
1. Q1: Add a secondary index on `(tenant_id, table_id, day)`? Default: no; the PK prefix already serves the lineage/usage queries.
2. Q2: Edit baseline in place vs. new migration version 2? Default: edit the baseline in place (no production, no upgrade migrations); bump checksum.
3. Q3: Key column length on SqlServer? Default: `NVARCHAR(256)` for ids/application/query_name, `CHAR(64)` for `actor_hash`, `CHAR(10)` for `day`; architect to confirm against the 900-byte key limit.
4. Q4: Should a missing table also block readiness? Default: no, Verify already blocks startup on shape mismatch; runtime stays degraded-but-available.
