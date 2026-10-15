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
| **Schema Managed** | Consents, Delegations, Audit Log, Catalogs, Virtual Filters, ITSM Outbox, Access Profiles, Webhook Outbox, CDC Watermarks | Consents, Delegations, Audit Log, Catalogs, Virtual Filters, ITSM Outbox, Access Profiles, Webhook Outbox, CDC Watermarks | Consents, Delegations, Audit Log, Catalogs, Virtual Filters, ITSM Outbox, Access Profiles, Webhook Outbox, CDC Watermarks |

---

## Versioned Schema Migrations

The governance schema is created and evolved by ordered, checksummed migrations recorded in `autheris_schema_migrations` (`version`, `name`, `checksum`, `applied_at`, `applied_by`). They run once at startup under the cross-replica schema lock, or as a separate step with `--migrate-governance` (see the [configuration guide](../configuration-guide.md) and the [operations runbook](../operations-runbook.md)). Every engine has the same catalogue:

| Version | Name | Content |
| :--- | :--- | :--- |
| `0001` | `baseline_governance_schema` | Idempotent baseline of the complete governance schema (existing databases upgrade in place). |
| `0002` | `webhook_outbox` | Table `WEBHOOK_OUTBOX` with the deduplication index `ux_webhook_outbox_dedupe (source, subscription_id, event_id)` and the indexes `ix_webhook_outbox_due`, `ix_webhook_outbox_lease`, `ix_webhook_outbox_owner` ([F-EXT-01](f-ext-01-generic-plugin-events-and-webhooks.md#31-durable-outbox)). Times are Unix milliseconds on every engine. |
| `0003` | `cdc_watermarks` | Table `autheris_cdc_watermarks` with primary key `(source, domain_name, schema_name, table_name)` and the columns `watermark`, `fencing_token`, `updated_at`, `updated_by` ([F-CDC-02](f-cdc-02-mssql-change-tracking.md#31-durable-fenced-watermark)). |

Migrations are forward-only. An older build refuses to start against a newer schema unless `GovernanceDb:Migrations:AllowNewerSchema` is `true`.

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
