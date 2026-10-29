# F-GOV-12: Multi-Engine Governance Database Storage (MSSQL, PostgreSQL, SQLite)

## Executive Summary

Autheris provides native **Multi-Engine Governance Persistence**, allowing organizations to host the gateway's metadata, policies, consents, and cryptographic audit records on their enterprise database of choice without external dependencies.

Complete feature, schema, and concurrency parities are maintained across **Microsoft SQL Server (MSSQL)**, **PostgreSQL**, and **SQLite**.

---

## Architectural Comparison & Engine Parity

| Capability | Microsoft SQL Server (`SqlServerGovernanceRepository`) | PostgreSQL (`PostgreSqlGovernanceRepository`) | SQLite (`SqliteGovernanceRepository`) |
|---|---|---|---|
| **Production Target** | Enterprise on-premises & Azure SQL Database | Cloud Native (AWS RDS, Aurora, Cloud SQL) | Embedded developer mode & standalone edge deployments |
| **Cluster Concurrency Locking** | Native session/tx application locks via `sp_getapplock` (`Review PG-1`) | Transaction-level advisory locks via `pg_advisory_xact_lock` | In-process reader-writer coordination via `SemaphoreSlim` |
| **Audit Hash Chaining** | Transaction-enrolled SHA-256 HMAC with rollback state synchronization | Transaction-enrolled SHA-256 HMAC with rollback state synchronization | Transaction-enrolled SHA-256 HMAC with rollback state synchronization |
| **KMS WORM Sealing** | Mandatory external anchor outside Dev/Test (AU-01/AU-03) | Mandatory external anchor outside Dev/Test (AU-01/AU-03) | Mandatory external anchor outside Dev/Test (AU-01/AU-03) |
| **Schema Managed** | Consents, Delegations, Audit Log, Catalogs (incl. declared keys and indexes), Virtual Filters, ITSM Outbox, Access Profiles, Webhook Outbox, CDC Watermarks | Consents, Delegations, Audit Log, Catalogs (incl. declared keys and indexes), Virtual Filters, ITSM Outbox, Access Profiles, Webhook Outbox, CDC Watermarks | Consents, Delegations, Audit Log, Catalogs (incl. declared keys and indexes), Virtual Filters, ITSM Outbox, Access Profiles, Webhook Outbox, CDC Watermarks |

---

## Versioned Schema Migrations

The governance schema is created and evolved by ordered, checksummed migrations recorded in `autheris_schema_migrations` (`version`, `name`, `checksum`, `applied_at`, `applied_by`). They run once at startup under the cross-replica schema lock, or as a separate step with `--migrate-governance` (see the [configuration guide](../configuration-guide.md) and the [operations runbook](../operations-runbook.md)). Every engine has the same catalogue:

| Version | Name | Content |
| :--- | :--- | :--- |
| `0001` | `baseline_governance_schema` | Idempotent baseline of the complete governance schema (including `WEBHOOK_OUTBOX`, `autheris_cdc_watermarks`, and `CLOUDEVENT_SUBSCRIPTIONS`). |

Migrations are forward-only. An older build refuses to start against a newer schema (`GOVERNANCE_SCHEMA_NEWER_THAN_BUILD`) unless `GovernanceDb:Migrations:AllowNewerSchema` is `true`; even then no DDL runs and the history, checksums and shape of the known steps are verified.

**Supported base and upgrades.** v1.5.0 is the oldest supported base. Step `0001` is frozen: its checksum is the constant identity `<engine>:0001:baseline_governance_schema:v1` (changing it would reject every v1.5.0 database), and the three baseline sources and the v1.5.0 shape snapshots are pinned byte-for-byte by golden hashes in the architecture tests. Every later schema change is a new step `NNNN_<snake_name>`:

- It exists per engine as embedded SQL (`Persistence/Migrations/Steps/<engine>/NNNN_<name>.sql`) plus a shape delta (`NNNN_<name>.shape.json`; operations `addTable`, `dropTable`, `addColumn`, `dropColumn`, `alterColumn`, `setPrimaryKey`, `addIndex`, `dropIndex`). The catalogue has the same steps for all engines.
- The checksum is `SHA-256("<engine>:<NNNN>:<name>:" + LF-normalized SQL)`, computed over exactly the executed text; released step checksums are pinned in goldens (append-only).
- SQL is idempotent (guards), is linted at build time (no `DROP`/`TRUNCATE` of audit objects, `GRANT`/`REVOKE`, role or login DDL, dynamic SQL, transaction control, access to the history table) and may touch the audit objects only with a security-reviewed `-- phase3:` marker.
- On PostgreSQL and SQL Server each step runs in one transaction together with its shape check and its history row; on SQLite all pending steps share the run transaction. After every step the append-only audit trigger and truncate guard are re-verified; a step that disables them rolls back.
- Steps never read or rewrite audit rows, so the audit hash chain stays verifiable across upgrades.

**Older than v1.5.0 is unsupported.** Autheris does not upgrade, repair or rename pre-v1.5.0 schemas. A database that is not at the supported base fails with `GOVERNANCE_SCHEMA_UNSUPPORTED_BASE` and a drift report **before any statement runs**: with a history table, the actual shape is compared with the shape of the highest applied version first; without one, every existing governance table must match its v1.5.0 definition (missing tables are allowed, a partial baseline is completed) before step `0001` runs. The caller reinstalls from v1.5.0 or newer, or supplies the newest schema itself.

| Mode (`GovernanceDb:Migrations:Mode`) | Behaviour |
| :--- | :--- |
| `Apply` (default) | Under the cross-replica lock, re-reads the history and checks, in this order: history gap/duplicate/rename, newer than build, checksums, pre-apply shape, then applies pending steps (each verified against the shape of its version before its history row is recorded), then verifies the final shape and post-conditions. Needs a DDL-capable login (`GovernanceDb:MigrationConnectionString` on PostgreSQL / SQL Server, otherwise the runtime login, which logs `GOVERNANCE_MIGRATION_RUNTIME_DDL` in Production). |
| `Verify` | Reads only: same checks and codes, fails with `GOVERNANCE_SCHEMA_PENDING` while steps are pending. Executes no DDL, no write and takes no lock, so it runs with the least-privilege runtime login; it warns with `GOVERNANCE_RUNTIME_LOGIN_PRIVILEGED` (fails with `Migrations:RequireLeastPrivilege=true`) when that login can write the history or run DDL. |
| `Skip` | Neither inspects nor changes the schema (Development only). |

`Autheris.Api --migrate-governance` always applies and `--verify-governance` always verifies, whatever `Mode` says. A concurrent migrator waits for the lock up to `Migrations:LockTimeoutSeconds` (`GOVERNANCE_MIGRATION_LOCK_TIMEOUT`), then re-reads the history and applies nothing.

---

## Schema Shape Verification

At every start in `Apply` and `Verify` mode the migration runner compares the actual governance schema with the shape that the build expects (`Apply` under the cross-replica schema lock, `Verify` without a lock). Any deviation throws `GovernanceSchemaDriftException` (with the list in `Deviations`), fails startup and, in `Apply` mode, leaves the version unrecorded, so a drifted database fails on every start until it is fixed.

**Expected shape.** Each engine embeds the frozen v1.5.0 snapshot `governance-shape.<engine>.v0001.json` (`sqlite`, `postgresql`, `sqlserver`), generated by tests from a fresh database that ran the baseline; it is never edited by hand. The expected shape of version *N* is the snapshot folded with the shape deltas of steps `0002..N`; the runner checks the version it is about to leave (before applying), each step's own version (before its history row) and the latest version (after the run and in `Verify`). A missing, empty or unparseable snapshot or delta fails startup (an empty expected shape never passes).

| Object | Compared | Tolerated / not compared |
| :--- | :--- | :--- |
| Table | Existence (case-insensitive). | Extra tables (one Information log). |
| Column | Existence; type family (`string`, `integer`, `decimal`, `float`, `boolean`, `datetime`, `binary`, `json`, `other`; SQLite by type affinity); nullability (primary key columns are normalised to NOT NULL). | Length, defaults, extra columns. |
| Primary key | Ordered column set. | Constraint name. |
| Index | Every explicitly named index: existence, uniqueness, ordered columns. | Auto-named constraint indexes, extra indexes. |
| Not compared | Foreign keys, check constraints, data. Triggers are checked by the post-conditions. | |

**Catalog reads.** The verifier reads the system catalog only, with every table and index name bound as a parameter. PostgreSQL resolves tables like the runtime (unqualified names through `search_path`), so a table of the same name in another schema neither satisfies nor hides the check. SQL Server checks schema `dbo`. An object that the login cannot see is reported as missing (fail closed).

**Post-conditions** (both modes): PostgreSQL requires the append-only audit trigger on `audit_log_entries` to exist and be enabled; SQL Server requires the audit trigger to be enabled and the truncate guard `fk_audit_truncate_guard` from `AUDIT_LOG_TRUNCATE_GUARD` to `AUDIT_LOG_ENTRIES` to exist and be enabled.

**Failed steps.** When a migration step fails with a database error, the runner verifies the shape on the same connection and reports the drift instead (the original error is the inner exception, for logs only). If that diagnosis finds nothing or fails itself, the original error propagates.

**Drift message.** Deterministic (ordered by kind, table and name), at most 50 deviation lines plus `... and N more`, logged once as an Error and tagged on the current activity as `autheris.governance.schema.deviations`:

```text
Governance schema drift on SQLite database 'gov.db': the schema does not match this build (applied migrations: 0001; pending: none).
  missing table      TABLE_PRIMARY_KEY_COLUMNS
  missing column     CONSENT_COLUMN_RULES.applicable_scopes (expected TEXT, family string)
  wrong primary key  ACCESS_PROFILES (expected tenant_id, profile_id; found id)
  wrong index        AUDIT_LOG_ENTRIES.idx_audit_seq (expected UNIQUE (seq); found (seq))
Autheris upgrades governance schemas only forward from the v1.5.0 baseline. Restore the matching backup or load the data into a current schema; see docs/features/f-gov-12-multi-engine-governance-storage.md#schema-shape-verification.
```

The exception carries the governed code (`GOVERNANCE_SCHEMA_DRIFT` or `GOVERNANCE_SCHEMA_UNSUPPORTED_BASE`), which is prefixed to the message. "Applied" lists the versions in the history, "pending" the steps of the build that are not applied. The deviation kinds are `missing table`, `missing column`, `wrong type`, `wrong nullability`, `wrong primary key`, `missing index`, `wrong index`. The report is capped at 50 lines.

The database label is the SQLite file name or the PostgreSQL / SQL Server database name; the message never contains a connection string, host, user, password or directory path. The drift exception reaches only the startup log; no health or HTTP endpoint exposes it.

**Remedy.** Load the governance data in the current schema of the build (all tables, columns, keys and named indexes of the snapshot, including `TABLE_PRIMARY_KEY_COLUMNS`, `TABLE_INDEXES` and `TABLE_INDEX_COLUMNS`), or restore the backup that matches the build.

---

## Declared Table Keys and Indexes

The catalog stores declared primary keys and indexes of governed tables in `TABLE_PRIMARY_KEY_COLUMNS`, `TABLE_INDEXES` and `TABLE_INDEX_COLUMNS`. Their structure, validation and the fail-closed handling of invalid rows are described in [F-GOV-21](f-gov-21-declared-table-keys-and-read-ordering.md#3-persistence).

---

## Consent Query Indexes

The consent lookup of the request path (`GetActiveConsentsForSubjectsAsync`) filters in SQL by tenant, table, revocation flag and grantee instead of loading every consent of a table. All three baselines create:

| Index | Columns |
| :--- | :--- |
| `ix_consents_tenant_table` | `CONSENTS (tenant_id, table_id, is_revoked)` |
| `ix_consents_tenant_grantee` | `CONSENTS (tenant_id, grantee_sid_norm, is_revoked)` |
| `ix_consents_tenant_role` | `CONSENTS (tenant_id, role_name)` |
| `ix_consent_column_rules_consent`, `ix_consent_row_filters_consent` | `consent_id` of the rule tables |
| `ix_column_masking_rules_column` | `COLUMN_MASKING_RULES (table_column_id)` |
| `ix_role_members_member` | `ROLE_MEMBERS (member_sid)` |
| `ix_table_owners_owner` | `TABLE_OWNERS (data_owner_id)` |
| `ix_tables_natural_ci` | case-insensitive natural key of `TABLES` (source, schema, table) |

- `CONSENTS.grantee_sid_norm` holds the upper-invariant grantee SID written by the repository, so the subject match is case-insensitive and sargable. Rows without it (`NULL`) and role consents always pass the SQL prefilter.
- The SQL filter is only a prefilter: the validity check and the exact subject comparison stay in C#, so results are identical to a full in-memory filter.
- Subject lists are chunked at 1,000 parameters per statement (SQL Server allows 2,100); PostgreSQL uses `= ANY(@array)`.
- `CONSENTS.renewal_count` and `access_profile_assignments.renewal_count` count approved renewals of individual grants ([F-GOV-22](f-gov-22-time-bound-individual-grants.md)).
- Keyset paging of consents (`GET /api/v1/governance/grants`) and audit rows (`GET /api/v1/governance/audit`) uses these indexes and the audit sequence.

---

## Least-Privilege Logins

Run the application with a runtime login in `Mode=Verify` and apply the schema with a separate DDL-capable login (`MigrationConnectionString` or a `--migrate-governance` job in `Mode=Apply`). The grants per engine are listed in [F-GOV-21, section 6.2](f-gov-21-declared-table-keys-and-read-ordering.md#62-governance-database). The runtime login must not own the governance objects or hold DDL rights, because an owner can disable the append-only audit trigger and the truncate guard; the migration login must own the existing objects (later steps `ALTER` them), not only hold `CREATE`. `Verify` warns about a privileged runtime login (`GOVERNANCE_RUNTIME_LOGIN_PRIVILEGED`, a startup failure with `Migrations:RequireLeastPrivilege=true`).

---

## Configuration Example

```json
{
  "Gateway": {
    "GovernanceDb": {
      "Provider": "sqlserver", // "sqlserver" | "postgresql" | "sqlite"
      "ConnectionString": "Server=tcp:db.corp.local,1433;Database=AutherisGov;User Id=autheris_app;Password=${DB_PASS};TrustServerCertificate=False;Encrypt=True;",
      "AuditHmacKeyVaultRef": "azure-keyvault://my-vault/secrets/governance-audit-key",
      "SeedDemoData": false
    }
  }
}
```
