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
- Api endpoints, `AccessAuditMiddleware`, the ReBAC seed service and `GovernedSqlExecutor` write only through `IAuditWriter`; an architecture rule rejects direct `IAuditLogRepository` writes in the Api layer.
- Required records written after a committed change (ReBAC tuple changes, token revocation, tenant collision resolution) use a non-cancelable token, so a client disconnect cannot drop the record. `RecordAsync`/`RecordRequiredAsync` rethrow `OperationCanceledException` only when the caller's own token was cancelled.
- ReBAC administration has explicit event types and policies:

  | Event type | Policy |
  | :--- | :--- |
  | `REBAC_TUPLE_ADDED` | `FailClosed` |
  | `REBAC_TUPLE_REMOVED` | `FailClosed` |
  | `REBAC_INVALIDATION_FAILED` | `FailClosed` |
  | `REBAC_TUPLE_SEED` | `BestEffort` (startup seed) |
  | `WEBSQL_DML_FAILED` | `FailClosed` (a DML statement that failed during execution; `WEBSQL_DML_REJECTED` is used for rejections) |

- When the audit store is unavailable, `POST`/`DELETE /api/v1/rebac/tuples`, `POST /api/admin/tokens/revoke` and `POST /api/admin/tenant-registry/{tenantId}/resolve-collision` answer `503` with `Retry-After: 5` (`AuditUnavailableException`); the change itself may already be committed, so clients retry idempotently.
- A denial never depends on the audit outcome: `AccessAuditMiddleware` writes `DENY` records best-effort and every other decision as a required record (an unavailable store fails the request closed). A denial is never turned into an allow by an audit failure.
- The ReBAC table gate keeps denying when its dependency fails, records `authz_dependency_failure_total`, and lets cancellation propagate.

### 10. Batched Group Commit of Required Audit Writes
- `IAuditWriter.RecordRequiredAsync` hands entries to the singleton `AuditGroupCommitSink`. Concurrent required writes are coalesced into batches: **one transaction, one chain-tail read and one anchor per batch** instead of one round trip and one global lock per row.
- The ambient caller context is resolved on the request thread before the entry enters the sink (the drain loop has no request context).
- Chain hashes are computed in memory from the single tail read; the rows are then written with multi-row `INSERT` statements (`AuditBatchInsertSql`, 13 parameters per row, well below the SQL Server limit of 2,100). The statement shape never influences the chain.
- **Acknowledgement:** every caller awaits the durable commit of its own batch. A batch that contains a Tier-A event (security-critical mutation, administrative action or denial; everything except allowed query reads) is acknowledged only **after its anchor is written**. When the anchor cannot be persisted, the rows stay committed (the next batch anchors the tail) but the callers receive `AuditUnavailableException` (fail closed, `503`). Tier-B-only batches never fail on a late anchor.
- A persistence failure faults every caller of the batch. Chain-violation admission (section 6) is applied per entry inside the batch.
- Sink limits (built-in defaults, `AuditGroupCommitOptions`):

  | Setting | Default | Meaning |
  | :--- | :--- | :--- |
  | `MaxBatchSize` | `64` | Maximum entries per transaction. |
  | `MaxBatchDelayMs` | `5` | Maximum wait for more entries after the first one of a batch arrived. |
  | `ChannelCapacity` | `4096` | Pending entries before callers are back-pressured (`BoundedChannelFullMode.Wait`). |

- Best-effort writes (`RecordBestEffortAsync`) and the Tier-B query audit channel are unchanged.

---

## Paged Audit Listing (`GET /api/v1/governance/audit`)

Keyset-paged, newest-first listing of audit rows by chain sequence.

| Query parameter | Description |
| :--- | :--- |
| `cursor` | Opaque continuation token from `nextCursor` (encodes the last sequence). A malformed cursor is `400 INVALID_CURSOR`. |
| `pageSize` | Default 100, clamped to 1..500. |
| `targetTable` | Restrict to one table. |
| `actor` | Restrict to one actor SID. |
| `since` | Only rows at or after this instant. |
| `tenant` | Another tenant; honoured for `ClusterAdmin` only, otherwise `403`. |

**Authorization:** `GovernanceAdmin`, `TenantAdmin`, `SecurityAuditor` and `PrivacyAdmin` read their tenant; a `DataOwner` reads only the audit of one table they own (`targetTable`). Every read writes a summarized `AUDIT_READ` event.

Items carry `sequence`, `occurredAt`, `eventType`, `decision`, `actorSid`, `targetTable`, `targetColumn`, `traceId` and `tenantId`; the response carries `nextCursor` (null on the last page) and the effective `pageSize`. The grant listing `GET /api/v1/governance/grants` uses the same model ([F-GOV-11, section 7](f-gov-11-declarative-access-profiles.md#7-paged-grant-listing-get-apiv1governancegrants)).

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
