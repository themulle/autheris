# Plan: Issue #286 - CONSUMER_APP_* tables in the baseline migrations

PRD: [2026-10-23-prd-issue-286-consumer-app-tables-baseline.md](2026-10-23-prd-issue-286-consumer-app-tables-baseline.md). One work package (WP-1). Branch `fix/issue-286-consumer-app-baseline`.

## Defaults and decisions on open questions
- Q1: no secondary index (default taken).
- Q2: edit the baseline (step 0001) in place, no repair or upgrade migration (default taken). **Checksum: keep the `...:0001:baseline_governance_schema:v1` token unchanged** (architect decision, differs from the PRD "bump checksum"). Reasons: the checksum is a declared identity token (`MigrationModel.cs`), the baseline was already extended in place without a bump (#252, #255, #265), `BaselineMigrationChecksums_AreUnchanged` pins it, and #281 edits the same step. An existing DB that lacks the tables still fails closed because `VerifyShapeAsync` runs in Apply and Verify mode (`GovernanceMigrationRunnerBase.cs:159`).
- Q3: SQL Server key lengths confirmed against the 900-byte clustered key limit (D-2) (default taken, with `actor_hash` as `CHAR(64)`).
- Q4: a missing table does not block readiness beyond the existing shape check (default taken).

## Migration files touched (for serial merge with #281)
`src/Autheris.Infrastructure/Persistence/Migrations/{SqliteBaselineSchema,PostgreSqlBaselineSchema,SqlServerBaselineSchema}.cs` and `Migrations/Shape/governance-shape.{sqlite,postgresql,sqlserver}.json`. The new DDL goes in **one separate block appended after the last table block** (SQLite after `RECERT_ITEMS`; PostgreSQL before the audit trigger; SQL Server as its own `ExecuteSchemaCommand` before `trg_audit_append_only`), marked with the comment `// Issue #286: consumer usage aggregates`. Shape entries are placed in the existing alphabetical order. Runners (`*GovernanceMigrationRunner.cs`) are not touched.

## Decisions
1. **D-1 DDL, SQLite** (`SqliteBaselineSchema`): move the three `CREATE TABLE IF NOT EXISTS` statements from `ConsumerAggregationStore.EnsureTablesCreatedAsync` without changes (`TEXT` keys, `INTEGER` counters, same defaults and PKs).
2. **D-2 DDL, SQL Server** (`SqlServerBaselineSchema`, `IF OBJECT_ID(N'dbo.X', N'U') IS NULL CREATE TABLE dbo.X`, named `PK_CONSUMER_APP_*`, clustered). Types: `tenant_id NVARCHAR(128)`, `table_id NVARCHAR(128)` (Guid text), `day NVARCHAR(10)`, `application NVARCHAR(64)`, `client_id NVARCHAR(64)`, `version NVARCHAR(32)`, `query_name NVARCHAR(64)` (the bounds are `CallerLabelNormalizer.Max*Length`), `actor_hash CHAR(64)` (lowercase hex HMAC-SHA256), all counters `BIGINT NOT NULL CONSTRAINT DF_<table>_<col> DEFAULT 0`, and `client_id`/`version` `DEFAULT N''`. Key bytes: DAILY 256+256+20+128+128+64=852, ACTOR 256+256+20+128+64=724, QUERYNAME 256+256+20+128+128=788. All three stay under 900.
3. **D-3 DDL, PostgreSQL** (`PostgreSqlBaselineSchema`): unquoted names (stored as lowercase `consumer_app_*`), `TEXT NOT NULL` keys, `BIGINT NOT NULL DEFAULT 0` counters, `TEXT NOT NULL DEFAULT ''` for `client_id`/`version`, the same composite PKs with `tenant_id` first, `CREATE TABLE IF NOT EXISTS` (the migrator holds `CREATE`).
4. **D-4 Shape files**: add three tables per engine with the column types the readers report (`TEXT`/`INTEGER` for sqlite; `TEXT`/`BIGINT` for postgresql; `NVARCHAR`/`CHAR`/`BIGINT` for sqlserver), following the existing entry format. Regenerate the SQLite file so it matches `EmbeddedSnapshot_EqualsShapeOfFreshlyMigratedDatabase`. Edit PG/SS by hand; the CI shape integration tests check them.
5. **D-5 Remove runtime DDL** (`ConsumerAggregationStore.cs`): delete `EnsureTablesCreatedAsync` and its 6 call sites (lines 79, 131, 162, 193, 281, 324). In `ConsumerAggregationSecurityTests` (lines 28, 123), replace the calls with `new SqliteBaselineSchema(conn, null).Apply()`.
6. **D-6 Provider DML**: add `internal enum ConsumerAggregationDialect { Sqlite, PostgreSql, SqlServer }` as the second parameter of the three `Record*Async` methods. Each `*GovernanceRepository.ConsumerAggregation.cs` passes its own dialect. Upsert SQL by dialect:
   - SQLite: unchanged.
   - PostgreSQL: qualify the existing row in `DO UPDATE SET` (`calls = CONSUMER_APP_DAILY.calls + excluded.calls`, and the same for all counters and `CONSUMER_APP_QUERYNAME_DAILY.calls`). Unqualified columns give "column reference is ambiguous".
   - SQL Server: `MERGE dbo.CONSUMER_APP_* WITH (HOLDLOCK) AS t USING (SELECT @... ) AS s ON <all PK cols> WHEN MATCHED THEN UPDATE SET t.calls = t.calls + s.calls ... WHEN NOT MATCHED THEN INSERT ...;`. For the actor table, use only `WHEN NOT MATCHED` (DO NOTHING).
   - Reads use dialect-neutral SQL: `CAST(SUM(x) AS BIGINT)` in `QueryConsumerAppDailyAsync`, and `CAST(COUNT(DISTINCT actor_hash) AS BIGINT)` in `QueryDistinctActorCountsAsync`. Without the casts, PG `SUM(bigint)` returns numeric and SQL Server `COUNT` returns int, and `GetInt64` breaks on SQL Server.
   - Retention `DELETE` and the lookup queries stay unchanged (portable).
7. **D-7 Fail closed, no flood** (`ConsumerUsageFlushWorker.cs:70`): keep the catch. On the first failure after a success, log one `LogError` with EventId `ConsumerUsageFlushFailed` (exception type plus provider message, no row data). Log repeated failures at `LogDebug` until the next success resets the latch. Never retry DDL. Readiness, data access and lineage are unchanged (lineage keeps returning an empty usage set on failure, as today).
8. **D-8 Architecture test** (`tests/Autheris.Tests.Architecture/NoRuntimeGovernanceDdlArchitectureTests.cs`): scan `src/Autheris.Infrastructure/Persistence/**/*.cs` (pattern from `NoNewFastSqlEngineInSrcArchitectureTests.RepositoryRoot`) for the regex `\b(CREATE\s+(UNIQUE\s+)?(TABLE|INDEX)|ALTER\s+TABLE)\b`, case-insensitive. Exclude `Persistence/Migrations/` and an explicit allowlist (DuckDB and in-memory cache stores, the `autheris_schema_migrations` bootstrap, as found in the PRD audit, matched by exact relative path). Also assert that no member named `EnsureTablesCreatedAsync` exists in the Infrastructure assembly (reflection).

## Tests (TDD order; unit tests run locally, integration tests are compiled locally and run on CI)
Unit (`tests/Autheris.Tests.Unit`):
- `BaselineSchemaShapeParityTests`: `BaselineSchema_{Sqlite,PostgreSql,SqlServer}_ContainsConsumerAppTables` (DDL text or sqlite_master for the three names).
- `GovernanceSchemaShapeSnapshotTests.EmbeddedSnapshot_CoversConsumerAppTables(engine, daily, actor, queryName)` (all 3 engines, PG lowercase); `EmbeddedSnapshot_EqualsShapeOfFreshlyMigratedDatabase` stays green.
- `GovernanceSchemaShapeVerifierTests.Verify_WhenConsumerAppTableMissing_ReportsMismatchNamingTable` (SQLite DB migrated, then `DROP TABLE CONSUMER_APP_ACTOR_DAILY` gives not ready, and the message contains the name) (AC-3).
- `GovernanceSchemaMigrationTests.BaselineMigrationChecksums_AreUnchanged` stays green (D-Q2).
- `ConsumerAggregationStoreDialectTests`: `Upsert_PostgreSql_QualifiesExistingRowColumns`, `Upsert_SqlServer_UsesMergeWithHoldlock`, `ActorUpsert_SqlServer_HasNoMatchedBranch`, `Upsert_Sqlite_IsUnchanged` (SQL text via an `internal` builder `ConsumerAggregationSql.For(dialect)`), and `RecordAndQuery_Sqlite_RoundTripsAfterBaselineOnly` (no runtime DDL).
- `ConsumerUsageFlushWorkerTests.RepeatedFailures_LogErrorOnce_ThenDebugUntilSuccess`.
- `ConsumerAggregationSecurityTests` and `TableConsumersDualTierSecurityTests` stay green (AC-5).

Architecture: `NoRuntimeGovernanceDdlArchitectureTests.PersistenceStores_ContainNoDdlOutsideMigrations` and `.EnsureTablesCreatedAsync_DoesNotExist` (AC-6).

Integration (`tests/Autheris.Tests.Integration`, CI):
- `PostgreSqlGovernanceLeastPrivilegeTests.ConsumerAggregation_RuntimeRoleWithoutCreate_RecordsAndReads` (grant `SELECT, INSERT, UPDATE, DELETE` on the 3 tables, `Mode=Verify`, record usage, actor and query name, then read stats and purge with no exception) (AC-2).
- `PostgreSqlGovernanceSchemaShapeTests` and `SqlServerGovernanceSchemaShapeTests`: the existing shape equality covers the new tables (AC-1).
- New `SqlServerConsumerAggregationTests.RecordTwice_AccumulatesCounters_AndDistinctActorsCount` on the existing SQL Server fixture (AC-4).

## Out of scope
No upgrade or repair migration, no docs changes (no docs phase), no DuckDB or cache DDL changes.

## Phase 3 security conditions
Verdict: **APPROVED WITH CONDITIONS**. Verified: the checksum is `SHA256(identity token)` only (`MigrationModel.ComputeChecksum`), so an in-place DDL edit with an unchanged token is consistent with #252/#255/#265. `GovernanceMigrationRunnerBase` runs `ShapeVerifier.VerifyAsync` in Apply (line 198) and Verify (line 218), so a missing table or column fails closed. Consequence: an existing database already at step 0001 without the tables is reported not ready (no repair, per the no-upgrade-migrations rule); this is accepted and must stay fail closed, never a silent skip.
- SEC-286-1: Runtime DDL must be removed completely. D-8 must scan all `Persistence/**/*.cs` and `Observability/**/*.cs` of Infrastructure, strip comments, and use a case-insensitive regex that also covers `DROP TABLE`, `CREATE INDEX`, `SELECT INTO`, `IF OBJECT_ID ... CREATE`. The allowlist matches exact relative paths plus a count of expected hits; a new hit in an allowlisted file fails.
- SEC-286-2: The shape files must list every column, type and primary key column of the 3 tables per engine. The verifier must report missing table, missing column and wrong type as not ready, naming the table, without echoing connection data.
- SEC-286-3: All upserts (SQLite `ON CONFLICT`, PostgreSQL `ON CONFLICT ... DO UPDATE` with qualified columns, SQL Server `MERGE ... WITH (HOLDLOCK)` on the full PK including `tenant_id`) take every value as a bound parameter. No string interpolation of tenant, table id, application, client id, version, query name or actor hash. The SQL builder takes only the dialect enum, never caller data. The MERGE `ON` clause must contain all PK columns, otherwise tenants collide.
- SEC-286-4: Tenant scoping is unchanged: every read keeps `tenant_id = @tenant_id` plus `table_id`; every write carries `tenant_id` as the first PK column. Only the retention purge stays global by cutoff (existing behaviour, no tenant input).
- SEC-286-5: Only `actor_hash` is stored, logged or returned; the flush worker logs exception type and provider message only, never SQL parameters, tenant, application, client id or actor values. Provider exceptions must not be logged with parameter dumps (no `EnableSensitiveDataLogging`-style output).
- SEC-286-6: The error-streak latch must not hide a persistent failure: after the first `LogError`, repeats stay at `LogDebug`, but emit another `LogError` at a bounded interval (for example every 15 minutes or every 100 failures), include the failure count and first-failure time in it, and keep a failure counter or health signal. A success resets the latch. The in-memory buffer must stay bounded while the store fails (no unbounded growth, drop oldest or newest with a counter).
- SEC-286-7: The runtime role needs no DDL right: the PostgreSQL least-privilege integration test (`SELECT, INSERT, UPDATE, DELETE` only, no `CREATE`) must pass in `Mode=Verify`, and the SQL Server equivalent must run with a role without `ALTER`/`CREATE TABLE`.
- SEC-286-8: The SQL Server actor table is insert-if-absent only (no `WHEN MATCHED`), and its concurrent insert of the same key must not throw a PK violation (HOLDLOCK serialisation).

Mandatory tests (`[Trait("Category","Security")]`):
- `NoRuntimeGovernanceDdlArchitectureTests.AllPersistenceAndObservability_ContainNoDdl_ExceptAllowlistedExactPaths` and `.EnsureTablesCreatedAsync_DoesNotExist` (SEC-286-1).
- `GovernanceSchemaShapeVerifierTests.Verify_WhenConsumerAppTableMissing_FailsClosed`, `Verify_WhenConsumerAppColumnMissing_FailsClosed`, `Verify_OnExistingBaselineWithoutConsumerTables_IsNotReady` (SEC-286-2).
- `ConsumerAggregationSqlTests.AllDialects_UseOnlyBoundParameters_AndTenantFirstInKey` (asserts no `'` literals or interpolation, MERGE ON contains every PK column) (SEC-286-3).
- `ConsumerAggregationSecurityTests.TenantA_CannotSeeOrAccumulateInto_TenantB_AfterBaselineOnly` (SEC-286-4).
- `ConsumerUsageFlushWorkerTests.FailureLog_DoesNotContainActorOrTenantValues` and `.PersistentFailure_ReEmitsError_AtBoundedInterval` and `.BufferStaysBounded_WhileStoreFails` (SEC-286-5/6).
- CI: `PostgreSqlGovernanceLeastPrivilegeTests.ConsumerAggregation_RuntimeRoleWithoutCreate_RecordsAndReads`, `SqlServerConsumerAggregationTests.ConcurrentActorInsert_SameKey_NoPkViolation` (SEC-286-7/8).

## Changelog
- 2026-10-23 (Phase 4, G-PLAN-REBASE): drift found and handled. (1) `tools/migrations/upgrade_governance_v1_1_to_v1_2.sql` also lists every baseline table and is shape-checked by `GovernanceMigrationSecurityTests.T_SEC_SEC210_04`; the three CONSUMER_APP_* tables were added there (`IF NOT EXISTS`, token unchanged). (2) The DDL allowlist of D-8/SEC-286-1 is empty: the scan roots (`Persistence/**` minus `Migrations/`, `Observability/**`) contain no DDL; DuckDB and in-memory cache stores live under `Caching/` and are out of scope; the `OBJECT_ID` existence check in `CatalogKeyDefinitionStore` is read-only and not DDL. The `SELECT ... INTO` pattern is limited to `#`, `[`, `dbo.`, `public.` targets (Oracle PL/SQL `SELECT INTO v_var` is not DDL). (3) `ConsumerAggregationSecurityTests` used a mock `AUDIT_LOG_ENTRIES`; renamed to `MOCK_FORENSIC_LOG` because the baseline now provides the real table. (4) The flush worker got `FlushOnceAsync` (internal) and an optional `TimeProvider` for testability; failures are drained, not re-queued, so the buffer stays bounded by `MaxSeriesCapacity`.
