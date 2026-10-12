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
      "SanitizeLiterals": true
    },
    "GovernanceDb": {
      "Provider": "sqlserver",
      "ConnectionString": "Server=tcp:sql.corp.local;Database=Governance;Integrated Security=SSPI;TrustServerCertificate=True;",
      "AuditHmacKeyVaultRef": "azure-keyvault://my-vault/secrets/audit-hmac-key"
    }
  }
}
```
