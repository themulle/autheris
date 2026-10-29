# Plan: forward-only governance schema migrations from the v1.5.0 base (issue #388)

Phase 2 (csharp-architect), 2026-10-29. Branch `feat/issue-388-governance-migrations` (from `origin/main` 43c6585d). Triage: [2026-10-29-triage-issues-388-393.md](2026-10-29-triage-issues-388-393.md) (#388 section).

Owner decisions (binding, 2026-10-29):
- O-1: "v1.5.0 is the base". The governance schema as shipped in v1.5.0 (tag `v1.5.0` = 6cd9e0a7, migration `0001 baseline_governance_schema` per dialect) is frozen. It is never edited again.
- O-2: Databases older than v1.5.0 are unsupported. Startup fails closed with a drift report; the operator reinstalls or supplies the newest schema (triage option A).
- O-3: Every schema change after v1.5.0 is an ordered, idempotent, forward-only step `0002..n` for PostgreSQL, SQL Server and SQLite. Steps are applied **automatically**: on startup in `Mode=Apply` (default), and in the pre-deployment `--migrate-governance` run of the Talos setup (`MigrationConnectionString` role, app in `Mode=Verify`). No manual DDL.
- O-4: Safety: exactly one migrator under a cross-replica lock (others wait, then verify), transactional per step where the dialect allows it, fail closed with a governed error (database newer than build, failed step, drift, checksum mismatch), idempotent steps, version and checksum history, dialect parity.
- This supersedes the "no upgrade migrations" policy (#94/#105, f-gov-12, configuration guide, plans of #281/#286, default-tenant plan section 4) for every change after v1.5.0.

Verified facts: `git diff v1.5.0 origin/main -- src/Autheris.Infrastructure/Persistence/Migrations/` is empty (12 commits after the tag), so the baseline and shape snapshots on `main` are byte-identical to v1.5.0. The default-tenant branch does not touch schema files yet.

## 1. What exists today

| Concern | Where | State |
|---|---|---|
| Engine | `GovernanceMigrationRunnerBase.RunAsync` (:78-119), `RunUnderLockAsync` (:121-194) | Lock, re-read history under the lock, checksum check, pending steps, shape verify before the last version row (plan D-1), post-conditions (audit trigger, truncate guard). |
| Catalogue | `{PostgreSql,SqlServer,Sqlite}GovernanceMigrationRunner.Catalogue` | One step: `0001 baseline_governance_schema`, body in `{PostgreSql,SqlServer,Sqlite}BaselineSchema.cs` (859/996/838 lines, self-contained, idempotent `IF NOT EXISTS`). `ValidateCatalogue` (:325) enforces gap-free versions from 1. |
| Checksum | `MigrationStep.ComputeChecksum("postgresql:0001:baseline_governance_schema:v1")` | Hash of a label, **not of the DDL**. A baseline edit is invisible to the checksum (root cause of #388: v1.2.12 and v1.5.0 databases both carry the same `0001` row). Test `BaselineMigrationChecksums_AreUnchanged` pins the labels only. |
| History table | `autheris_schema_migrations(version, name, checksum, applied_at, applied_by)` | Created outside the catalogue by `CreateVersionTableSql`; runtime grant `SELECT` only (f-gov-21 section 6.2). |
| Newer DB | :133-138 | Fails unless `Migrations:AllowNewerSchema=true` (allowed in every environment). |
| Verify mode | :151-160 | No DDL, fails when steps are pending, then shape and post-conditions. |
| Shape | `Shape/governance-shape.<engine>.json` (72-74 KB each, embedded), `GovernanceSchemaShapeVerifier`, comparer | One snapshot = shape after **all** steps. `SchemaColumn(Name, Type, Family)`: no nullability, no default. Extra tables tolerated. |
| Drift report | `BuildDrift` (:229-241), `GovernanceSchemaDriftException` | Engine, sanitized DB label, applied and pending versions, `SchemaDeviation` list; no connection details (test `DriftMessage_ContainsNoConnectionDetails`). No machine-readable code. |
| Lock | PG `pg_advisory_lock` (session, **blocking, no timeout**); SQL Server `sp_getapplock` session, 60 s; SQLite repository write gate + `BEGIN IMMEDIATE` | SQLite runs the whole run in one transaction (`UseStepTransactions => false`); PG/SQL Server open a step transaction only when `MigrationStep.Transactional` (default `false`, baseline non-transactional). |
| Modes | `GovernanceMigrationMode { Apply = 0 (default), Verify, Skip }`, `ProductionStartupChecks.cs:43` rejects `Skip` outside Development | Apply uses `MigrationConnectionString` when set (PG/SQL Server runners :61), else the runtime connection. |
| CLI | `Program.cs:76` `--migrate-governance` | Runs the runner **per `Mode`** (Verify only verifies), exit 0/1. Helm `governance-migration-job.yaml:61-67` sets `Mode=Apply` and `MigrationConnectionString`; values.yaml:188 sets the app to `Verify`. |
| Tests | Unit `GovernanceSchemaMigrationTests` (16), `GovernanceSchemaShapeSnapshotTests`, `BaselineSchemaShapeParityTests`; integration `PostgreSqlGovernanceSchemaShapeTests`, `SqlServerGovernanceSchemaShapeTests`; fixture `governance-v1.1.8-preshim.sqlite.sql` | All test a one-step catalogue. |

## 2. Design

**D-1 Freeze the v1.5.0 baseline.** Step `0001` keeps its label checksum forever (changing it would brick every v1.5.0 database). The freeze is enforced by goldens instead: `tests/Autheris.Tests.Architecture/Golden/GovernanceBaseline/v1.5.0.sha256` lists SHA-256 (LF-normalized UTF-8) of the three `*BaselineSchema.cs` files and of the three v1.5.0 shape snapshots (renamed to `governance-shape.<engine>.v0001.json`, see D-5). Test `Baseline_V150_SourcesAndSnapshotsAreByteIdentical` fails on any change; the golden file has no update switch (a change needs an owner decision and a plan). Behavioural twin: `Baseline_StepOneOnly_ProducesFrozenV0001Shape` (SQLite unit; PG/SQL Server integration). Arch test `Baseline_Files_ReferenceNoOtherAutherisTypes` keeps the hash meaningful (today true: only driver and logging usings).

**D-2 Step format.** A step `NNNN_<snake_name>` exists per dialect as embedded SQL `Persistence/Migrations/Steps/<engine>/NNNN_<name>.sql` plus a shape delta `NNNN_<name>.shape.json` (D-5). One catalogue type `SqlMigrationStep` loads them; `MigrationStep` stays the runtime record. Rules: (a) idempotent DDL (`IF NOT EXISTS`/`COL_LENGTH`/catalog guards; SQLite has no `ADD COLUMN IF NOT EXISTS`, so a step may declare a guard query in a header line `-- guard: <SELECT returning 0 or 1>`; 1 = already applied, statements skipped, version still recorded after the shape check); (b) SQL Server batches split on a line `-- batch`; (c) forbidden by lint (D-9): `CONCURRENTLY`, `DISABLE TRIGGER`, `DROP TRIGGER`, `TRUNCATE`, `UPDATE`/`DELETE` on `AUDIT_LOG_ENTRIES`, `ALTER`/`DROP` of the audit table or truncate guard, transaction control statements. Checksum for `NNNN >= 2`: `SHA-256("<engine>:<NNNN>:<name>:" + LF-normalized resource bytes)`, hex lower case. A step whose name or version differs across dialects fails `Catalogues_HaveTheSameStepsForAllProviders`.

**D-3 Apply semantics.** Steps `>= 2` are `Transactional = true` on PG and SQL Server (both run DDL in transactions); the step body, the shape check of that version (D-5) and the history row commit together or roll back together. SQLite keeps the run-level `BEGIN IMMEDIATE` transaction (all pending steps atomic). The baseline stays non-transactional (frozen). A failed step: rollback, `TryDiagnoseAsync` drift report, code `GOVERNANCE_MIGRATION_STEP_FAILED`, process exits 1 / host start throws; earlier committed steps stay (forward-only, rerun continues from there).

**D-4 Concurrency.** Exactly one migrator: the existing lock stays the only serialisation point and the history is re-read under it (existing :129), so a waiting replica finds no pending step and only verifies. New bounded wait `GovernanceDb:Migrations:LockTimeoutSeconds` (default 300, range 10-3600): PG switches to a `pg_try_advisory_lock` poll with back-off until the deadline; SQL Server passes the value to `@LockTimeout` (today hard-coded 60000); SQLite uses it for the write gate and `busy_timeout`. Timeout = `GOVERNANCE_MIGRATION_LOCK_TIMEOUT`, fail closed. PG session locks die with the connection, so a killed migrator never leaves a stale lock.

**D-5 Shape per version.** `governance-shape.<engine>.v0001.json` is the frozen v1.5.0 snapshot (renamed, content unchanged except D-6). Each step ships a shape delta (ops: `addTable`, `dropTable`, `addColumn`, `dropColumn`, `alterColumn`, `setPrimaryKey`, `addIndex`, `dropIndex`). `GovernanceSchemaShapeLoader.LoadAt(engine, version)` folds v0001 with deltas `2..version`. The runner verifies: (a) **before** applying pending steps on a database with history, actual shape == shape at `max(applied)` (catches pre-v1.5.0 databases and manual edits; code `GOVERNANCE_SCHEMA_UNSUPPORTED_BASE` when `max(applied) == 1`, else `GOVERNANCE_SCHEMA_DRIFT`; nothing is applied); (b) inside each step transaction, before its history row, actual == shape at that version; (c) after the run and in Verify mode, actual == shape at latest (existing). A database without history table runs step 1 (idempotent adoption, kept) and then check (b) for version 1, so pre-v1.5.0 tables are rejected before step 2.

**D-6 Shape format: nullability.** `SchemaColumn` gains `bool Nullable`, deviation kind `WrongNullability`; the three catalog readers read it (`is_nullable`, `sys.columns.is_nullable`, `pragma_table_info.notnull`). Needed by the first consumers (NOT NULL columns in #390a and default-tenant). Done in WP-388-1 **before** the v0001 hash is recorded; the content of v0001 changes only by the added field, regenerated from the unchanged baseline (`Snapshot_V0001_RegeneratedFromBaselineOnly`). Column defaults stay outside the shape (default taken; behaviour is pinned by step tests).

**D-7 Fail-closed checks and governed codes.** `GovernanceSchemaDriftException` gains `string Code` (log, `--migrate-governance` stderr, startup failure message; never connection details). Codes: `GOVERNANCE_SCHEMA_NEWER_THAN_BUILD` (existing check), `GOVERNANCE_SCHEMA_CHECKSUM_MISMATCH` (existing check), `GOVERNANCE_SCHEMA_HISTORY_GAP` (new: applied versions are not exactly `1..k`, or a row below `knownMax` is unknown to the catalogue), `GOVERNANCE_SCHEMA_UNSUPPORTED_BASE`, `GOVERNANCE_SCHEMA_DRIFT`, `GOVERNANCE_SCHEMA_PENDING` (Verify mode), `GOVERNANCE_MIGRATION_STEP_FAILED`, `GOVERNANCE_MIGRATION_LOCK_TIMEOUT`. Order of checks under the lock: gap, newer, checksum, pre-apply shape (D-5a), apply, post-conditions.

**D-8 Modes and config mapping (default taken where not owner-fixed).**

| Setup | Config | Behaviour after this plan |
|---|---|---|
| Default (single login, any dialect) | `Migrations:Mode` absent = `Apply` | Automatic upgrade at startup with the runtime login; a login without DDL rights fails with `GOVERNANCE_MIGRATION_STEP_FAILED` and a hint to set `MigrationConnectionString` or use the job + `Verify`. |
| Separate DDL role at startup | `Apply` + `MigrationConnectionString` | Automatic upgrade with the migration role (unchanged wiring). |
| Talos / Helm job | app `Verify`; job `--migrate-governance` | The job **always applies** (it ignores `Mode`, logs it; `Skip` irrelevant), uses `MigrationConnectionString` when set. The app verifies and fails closed with `GOVERNANCE_SCHEMA_PENDING` if the job did not run. Helm env `Migrations__Mode=Apply` in the job stays harmless. |
| Dev | `Skip` | Unchanged (Development only). |

No key is renamed or removed; new key `Migrations:LockTimeoutSeconds` (registered in the unknown-key schema of #369). `AllowNewerSchema` stays (rollback aid); see Q-4. The history table is unchanged (no `0000` meta-migration).

**D-9 CI gates (unit tests in the default CI job, dialect parity in the integration shards).**
- `Baseline_V150_SourcesAndSnapshotsAreByteIdentical` (D-1).
- `ReleasedStepChecksums_AreUnchanged`: golden `Golden/GovernanceSteps/checksums.<engine>.txt` (`version name checksum`); existing lines immutable, appending allowed; every catalogue step must be listed.
- `ShapeAtLatest_EqualsFreshlyMigratedDatabase` (generalises `EmbeddedSnapshot_EqualsShapeOfFreshlyMigratedDatabase`): a PR that changes the schema without a step and delta fails here (and in PG/SQL Server `EmbeddedSnapshot_EqualsShapeOfTheBaseline`, renamed `ShapeAtEveryVersion_EqualsDatabaseMigratedToThatVersion`).
- `EveryStep_HasSqlAndShapeDeltaForAllEngines`, `Catalogues_HaveTheSameStepsForAllProviders`.
- `StepSql_ContainsNoForbiddenStatements` (D-2 c), `StepSql_TouchingAuditObjects_RequiresPhase3Marker` (`-- phase3: <condition id>` header).
- `NoGovernanceDdl_OutsideMigrations`: string literals with `CREATE TABLE|ALTER TABLE|CREATE INDEX|DROP TABLE` in `src/Autheris.Infrastructure` outside `Persistence/Migrations/`; allowlist today: `Caching/TableCache/InMemoryTableCacheStore.cs`, `DuckDbPersistentTableCacheStore.cs`.
- Upgrade path (D-10).

**D-10 Upgrade-path and parity tests (per dialect: SQLite unit, PG and SQL Server integration via Testcontainers).** A test-only seam `internal` runner constructor taking extra steps and deltas lets WP-388-1 test the engine before a real step exists (synthetic step `0002_test_add_probe_table`). Tests: `Upgrade_FromV150Baseline_AppliesAllSteps_KeepsData_AndAuditChainVerifies` (seed consents, catalog, audit entries and anchor on step 1 only, run all, `VerifyAuditHashChainAsync` and anchor verification pass), `Upgrade_FromPreV150Database_FailsUnsupportedBase_AndAppliesNothing` (SQLite: `governance-v1.1.8-preshim` fixture with a `0001` row; PG: baseline minus `consumer_app_daily` and with the v1.2.12 `access_profile_assignments` PK), `Run_WithHistoryGap_FailsClosed`, `Run_StepFails_RollsBackStepAndRecordsNoVersion`, `Run_StepIsIdempotent_WhenObjectsAlreadyExist`, `ConcurrentRunners_ExactlyOneApplies_OthersVerify` (two runners, barrier), `LockTimeout_FailsClosed`, `Verify_WithPendingStep_FailsWithPendingCode`, `MigrateGovernanceCli_AppliesEvenWhenModeIsVerify`, `ShapeAtEveryVersion_EqualsDatabaseMigratedToThatVersion`.

## 3. Documentation (WP-388-4)

- `docs/configuration-guide.md` (:203-215): Mode table and paragraph rewritten: forward-only automatic steps from v1.5.0, `LockTimeoutSeconds`, CLI always applies, error codes; delete "Autheris does not upgrade older governance schemas".
- `docs/features/f-gov-12-multi-engine-governance-storage.md` (:25-39, :113): replace "No upgrade migrations" with "Supported base v1.5.0; forward-only steps; < v1.5.0 unsupported (reinstall or supply the newest schema)"; step format and shape-per-version.
- `docs/features/f-gov-21-...md` section 6.2: the migration role must own the existing objects (ALTER on them), not only `CREATE`.
- `docs/operations-runbook.md` 4.ac: upgrade procedure v1.5.0 to newer (back up, run `--migrate-governance` or start in `Apply`, read the codes, rollback = restore backup; older binary only with `AllowNewerSchema` after additive steps), and "database older than v1.5.0" = drift report with `GOVERNANCE_SCHEMA_UNSUPPORTED_BASE`, then reinstall/load in the newest schema.
- Release-notes rule (developer guide, PR checklist): every release that adds steps lists "Governance schema: steps NNNN-MMMM (applied automatically; requires DDL rights or the migration job)" and flags non-additive steps. `deploy/helm/autheris/values.yaml:188` comment updated.
- Plans that state "no upgrade migrations" for future work (default-tenant section 4, #390 triage) are corrected by their lanes (section 5).

## 4. #390 re-plan (first consumer, outline only)

390a ships its schema change as a step, not a baseline edit: `NNNN_key_and_index_column_direction` adds `is_descending` (PG `BOOLEAN NOT NULL DEFAULT FALSE`, SQL Server `BIT NOT NULL CONSTRAINT DF_<table>_is_descending DEFAULT 0`, SQLite `INTEGER NOT NULL DEFAULT 0` with a `pragma_table_info` guard) to `TABLE_PRIMARY_KEY_COLUMNS` and `TABLE_INDEX_COLUMNS`, plus optional `is_clustered` on the index definition table if 390a keeps it. Purely additive, so existing rows read as ASC and an older binary keeps working (`AllowNewerSchema` rollback safe). Delivers SQL and shape delta for three dialects, checksum golden lines, and `Step_KeyDirection_DefaultsExistingRowsToAscending` per dialect. The rest of 390a (catalog model, `DefaultReadOrdering`, `TreeSqlCompiler`, planner tie-breaker, `describe_dataset`) is unchanged from the triage and needs its own Phase 2/3. Version number = next free at merge time; the lane that merges second renumbers (gap-free gate).

## 5. Conflict check: default-tenant lane (WP-DT-2)

Finding: **yes, WP-DT-2 changes the governance schema.** Plan `2026-10-26-plan-default-tenant.md` section 4 and section 7 item 6 edit the baselines of all three dialects: drop `DEFAULT 'legacy-single-tenant'` on `CONSENTS.tenant_id` and `AUDIT_LOG_ENTRIES.tenant_id`, make `CONSENT_REQUESTS.tenant_id` NOT NULL, under "no migrations". Not implemented yet (branch `refactor/default-tenant` has WP-DT-1/3 and a WIP WP-DT-4, no schema diff). After O-1 that baseline edit is forbidden. Sequencing:
1. WP-388-1 and WP-388-2 merge first (framework, freeze gates).
2. WP-DT-2 rebases and replaces its baseline edit with step `NNNN_tenant_column_constraints` (PG/SQL Server `ALTER ... DROP DEFAULT` / drop the named or looked-up default constraint, `SET NOT NULL`/`ALTER COLUMN ... NOT NULL`; the audit table is only altered by `DROP DEFAULT`, needs the Phase 3 marker). SQLite: default taken, keep the dead `DEFAULT` on `AUDIT_LOG_ENTRIES` (a table rebuild of the append-only audit table is forbidden) and rebuild only `CONSENT_REQUESTS` (copy, recreate indexes) for NOT NULL.
3. Open for the DT lane (DT-Q-5): rows with NULL `CONSENT_REQUESTS.tenant_id` on upgrade. Recommendation: the step fails closed with `GOVERNANCE_MIGRATION_STEP_FAILED` naming the table and count (no invented tenant, consistent with DT-A-9); the operator deletes or fixes them. Alternative: keep the column nullable and enforce in writers only.
4. DT section 4 text "no migrations" is corrected in that lane; its schema-shape goldens become the shape delta.
If WP-DT-2 must merge before WP-388-1, it may not edit the baseline; it then defers the schema part to a follow-up step.

## 6. Security notes and Phase 3 questions

Notes: the history table is not a tamper-proof ledger (a DDL-capable login can rewrite it; such a login already owns the schema); integrity comes from the frozen goldens, the per-version shape check and the audit post-conditions. Steps never read or rewrite audit rows; the hash-chain test proves the chain survives every step. Error texts carry identifiers and codes only.

- Q-1 Migration role: in Production with PG/SQL Server, `Mode=Apply` without `MigrationConnectionString` means the runtime login holds DDL (and can disable the audit trigger). Warn (owner wants automatic by default) or fail? Recommendation: startup warning plus audit event `GOVERNANCE_MIGRATION_RUNTIME_DDL`, no failure.
- Q-2 Least-privilege self-check in Verify mode: should Verify assert that the runtime login has no `INSERT/UPDATE` on `autheris_schema_migrations` and no DDL rights (fail or warn)?
- Q-3 Audit objects in steps: approve the lint list (D-2 c) and the `-- phase3:` marker rule; confirm `DROP DEFAULT` on `AUDIT_LOG_ENTRIES` (PG/SQL Server) is acceptable and the SQLite audit-table rebuild ban.
- Q-4 `AllowNewerSchema` in Production: keep allowed (rollback after additive steps) or restrict to steps marked additive? Shape check against the older build must then tolerate extra columns; confirm.
- Q-5 Lock timeout: default 300 s acceptable; must a lock timeout of a waiting replica fail startup (recommended) or retry?
- Q-6 Partially applied non-transactional baseline on PG/SQL Server (adoption path): created tables remain without a version row; acceptable as fail-closed?
- Q-7 CLI `--migrate-governance` ignoring `Mode`: any objection (it can no longer be used as a pure verifier; Verify stays available by starting the app)?

## 7. Work packages

| WP | Content | Files (src/test) | Size | Order |
|---|---|---|---|---|
| WP-388-1 | Engine: `SqlMigrationStep` + resource loader, step checksums, transactional steps, shape fold `LoadAt`, nullability in shape + readers + comparer, pre-apply and per-step shape checks, gap check, error codes, lock timeout option (PG poll, SQL Server param, SQLite), CLI always applies, test seam, rename v0001 snapshots | ~14 / ~6 | M-L (~700 src, ~900 test LOC) | first |
| WP-388-2 | Freeze and CI gates (D-1, D-9): baseline and v0001 hash golden, checksum goldens, step lint, DDL-outside-migrations arch test, catalogue parity | ~0 / ~6 + goldens | S-M | after 1 |
| WP-388-3 | Upgrade-path and parity tests (D-10) for SQLite, PG, SQL Server, pre-v1.5.0 fixtures, concurrency | ~0 / ~6 | M | after 1, parallel to 2 |
| WP-388-4 | Docs, runbook, release-notes rule, Helm comment (section 3) | 6 docs | S | last |

Total L. Phase 3 required (fail-closed paths, migration role, audit objects). Then WP-DT-2 (step) and #390a (step), whichever merges first takes `0002`.
