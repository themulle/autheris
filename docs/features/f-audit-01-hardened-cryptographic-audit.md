# F-AUDIT-01: Hardened Cryptographic Audit & Asymmetric KMS Anchors (AU-01..AU-19)

## Executive Summary

Autheris features an enterprise-grade, cryptographically verifiable audit subsystem designed for high throughput, tamper resistance, and regulatory compliance (SEC Rule 17a-4, GDPR Art. 17/30, HIPAA, and BaFin). 

The audit subsystem decouples in-memory high-throughput hash chaining from asymmetric hardware-backed KMS/HSM sealing. It enforces strict **fail-closed startup guarantees** in production, **transactional audit enrollment** to prevent phantom records upon database rollbacks, and **PII redaction** to reconcile the GDPR "Right to be Forgotten" with immutable audit logging.

---

## Key Capabilities & Architectural Invariants

### 1. Dual-Tier Cryptographic Architecture (AU-02)
- **Tier A (In-Process High Throughput):** Every query execution, authorization check, and consent decision is chained in-memory using HMAC-SHA256 (`PrevHash -> EntryHash`). Keys are derived via HKDF key separation from root secrets.
- **Tier B (Asymmetric KMS/HSM WORM Anchoring):** Block anchors are periodically sealed using asymmetric hardware keys (e.g. AWS KMS, Azure Key Vault, or GCP Cloud KMS). The gateway only holds public verification keys or calls sign operations via dedicated IAM roles. Internal database administrators cannot retroactively alter or forge the audit history.

### 2. Production Fail-Closed Default (AU-01 & AU-03)
- In non-development/production environments (`ASPNETCORE_ENVIRONMENT != Development`), the gateway strictly refuses to start (`InvalidOperationException`) if a persistent audit anchor path (`Audit:ChainAnchorPath`, `Audit:ChainAnchorWormDirectory`) or KMS signer (`Audit:ChainAnchorSignerKeyVaultRef`) is not configured.
- Ephemeral fallback keys and in-memory audit chains are explicitly prohibited outside development and test environments.

### 3. Transaction-Enrolled Audit Coupling (AU-04)
- Audit log entries generated during database mutations (such as consent granting or access profile updates) participate directly in the underlying database transaction (`DbTransaction` / `SqliteTransaction`).
- If the outer transaction commits (`OnTransactionCommitted`), the audit sequence numbers and entry hashes are permanently finalized.
- If the transaction is rolled back (`OnTransactionRolledBack`), the in-memory sequence numbers and chain hashes automatically revert to the committed state. This completely eliminates phantom audit records.

### 4. GDPR Art. 17 Compliant PII Scrubbing (AU-06 & AU-07)
- Raw SQL queries sent through WebSQL or Declarative SQL endpoints are canonicalized via [`AuditCanonicalizer`](../../src/Autheris.Infrastructure/Persistence/AuditCanonicalizer.cs).
- Literal values (strings, numbers, timestamps) are scrubbed and parameterized (`@p_redacted`), preventing personal data from being immutably sealed into WORM storage while maintaining verifiable query structure integrity.

### 5. Resilient Dead-Letter Channel & Backpressure (AU-05)
- The audit ingestion pipeline uses bounded channel buffering with `BoundedChannelFullMode.Wait`.
- In case of storage latency or transient network partitions, audit entries apply backpressure to callers rather than silently dropping security events.
- Failed deliveries are routed to an isolated Dead-Letter Queue (DLQ) with alert probes.

### 6. Latched Chain Violation Policy (SR-L-08)
- A detected audit hash chain violation is latched in its own field for the process lifetime. A successful write after a transient failure never clears it; recovery is a restart after forensic review (there is deliberately no reset endpoint).
- `Gateway:Audit:OnChainViolation` selects the reaction. Default: `DenyDataAccess` outside Development/Test, `AlertOnly` in Development/Test.
  - `DenyDataAccess`: query (Tier-B) audit events are refused with an `AuditUnavailableException`, so the data request fails closed. Security and admin (Tier-A) events are still written, carry `chainViolation=true` in their details, and one `AUDIT_PIPELINE_FAULT` event is written per latch.
  - `DenyAll`: every audit event is refused.
  - `AlertOnly`: the violation is logged and fails readiness only (`FailReadinessOnChainViolation`).

### 7. Anchor Monotonicity (SR-L-07)
- Anchor stores never move backwards: the file store (read-compare-write under an in-process lock and an exclusive sibling `<anchor>.lock` file for replicas sharing a path), the in-memory store and all three repositories ignore an anchor with a lower sequence than the stored one. The WORM directory store is append-only and always loads the highest sequence.

### 8. Unsequenced Rows
- Every audit row is written with a sequence number; there is no legacy HMAC key and no migration path for rows without one.
- On startup, SQLite audit rows without a sequence number latch a chain violation, and chain verification rejects them.

### 9. Unified Audit Contract (`IAuditWriter`)
- Endpoints and services write audit records through `Autheris.Application.Audit.IAuditWriter` instead of resolving `IAuditLogRepository` ad hoc.
  - `RecordRequiredAsync` throws `AuditUnavailableException` when the record cannot be written ("no audit, no data"); the API maps it to `503` with `Retry-After`. Used for ALLOW records and administrative changes.
  - `RecordBestEffortAsync` logs and counts (`autheris_audit_write_failed_total{tier}`) but never throws. Used where a failing sink must not change the outcome (for example tenant-collision denials).
  - `RecordAsync` dispatches through `AuditPolicyTable`, which assigns an explicit `AuditFailurePolicy` (`FailClosed` or `BestEffort`) to every `AuditEventTypes` entry; unknown types are `FailClosed`.
- A denial is never turned into an allow by an audit failure.
- The ReBAC table gate keeps denying when its dependency fails, now records `authz_dependency_failure_total`, and lets cancellation propagate.

---

## Configuration Example

```json
{
  "Gateway": {
    "Audit": {
      "Enabled": true,
      "ChainAnchorPath": "/var/data/autheris/audit_anchors.dat",
      "ChainAnchorWormDirectory": "/mnt/worm/audit_anchors",
      "ChainAnchorSignerKeyVaultRef": "azure-keyvault://my-vault/keys/audit-signer",
      "FlushIntervalSeconds": 10,
      "MaxBatchSize": 500,
      "SanitizeLiterals": true,
      "OnChainViolation": "DenyDataAccess"
    },
    "GovernanceDb": {
      "Provider": "sqlserver",
      "ConnectionString": "Server=tcp:sql.corp.local;Database=Governance;Integrated Security=SSPI;TrustServerCertificate=True;",
      "AuditHmacKeyVaultRef": "azure-keyvault://my-vault/secrets/audit-hmac-key"
    }
  }
}
```

## Unrouted request handling

Requests that match no endpoint and are answered by the router with a non-authentication 4xx status (typically 404, 405 or 400) are scanner noise and are never written to the audit chain, because the target of such a request is chosen by the client. Instead the counter `autheris_unrouted_requests_total` (tags `status_code` and `channel`, no path tag) is incremented and one in 100 occurrences is logged at Information level with a truncated path.

The following behaviour is unchanged:

- 401 and 429 responses are aggregated through `IAuthFailureAggregator` and not persisted individually.
- 403 responses on routed endpoints are persisted as DENY rows.
- Any exception and any 5xx response is still audited as an error.
- Routed endpoints that answer 404 are still audited.

The `TargetTable` of every persisted middleware audit row is capped at 256 characters.
