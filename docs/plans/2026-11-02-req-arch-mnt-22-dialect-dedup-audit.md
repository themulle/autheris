# PRD: Governance Repository Dialect Deduplication for Audit (ARCH-MNT-22 Step 2)

**Document ID:** `REQ-DIALECT-DEDUP-AUDIT-22`  
**Date:** November 2, 2026  
**Status:** APPROVED  
**Author:** Product Manager  
**Architecture Lead:** Solution Architect  
**Security Lead:** Application Security Expert  
**Master Plan Reference:** [`docs/plans/00-master-plan-overview.md`](00-master-plan-overview.md) (Item 12, `ARCH-MNT-22`)

---

## 1. Context & Problem Statement

In Autheris, the governance persistence subsystem is partitioned across three relational database engines: PostgreSQL, Microsoft SQL Server, and SQLite.

Following the successful completion of **ARCH-MNT-22 Step 1** (which deduplicated `ConsentGrants`, `ItsmOutbox`, and `WebhookOutbox` via `ConsentGrantSqlCore`, `ItsmOutboxSqlCore`, and `WebhookOutboxSqlCore`), the **Audit** persistence files remain the largest source of code duplication and architectural debt in the repository:
- [`src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Audit.cs`](../../src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Audit.cs) (1,449 lines)
- [`src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.Audit.cs`](../../src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.Audit.cs) (1,505 lines)
- [`src/Autheris.Infrastructure/Persistence/SqliteGovernanceRepository.Audit.cs`](../../src/Autheris.Infrastructure/Persistence/SqliteGovernanceRepository.Audit.cs) (1,425 lines)

Total volume: **4,379 lines**. All three files exceed the 1,000 LOC ceiling defined by architectural governance rules (`tests/Autheris.Tests.Architecture/Baselines/source-size-ceilings.txt`).

Inspection reveals that roughly ~60% of the logic across these three files is completely engine-neutral and repeated verbatim:
1. **Purge Checkpoint Persistence & Planning:** Checkpoint deserialization, signature verification, monotonic advancement checks, and prefix purge range calculations (`GetLatestPurgeCheckpointAsync`, `SavePurgeCheckpointAsync`, `PlanPurgePrefixAsync`).
2. **Audit Log Querying & Paging:** Dynamic SQL predicate construction (`target_table`, `actor_sid`, `occurred_at`, `tenant_id`, `seq < @before`), execution, parameter binding, and hydration into `AuditLogEntry` (`GetAuditLogEntriesAsync`, `QueryAuditLogsAsync`, `QueryAuditLogPageAsync`).
3. **WORM Chain Range & Verification:** Scanning and validating the append-only SHA-256 HMAC hash chain, checking sequence continuity, verifying anchor signatures, detecting tail divergence, and slicing chain pages (`VerifyAuditHashChainAsync`, `GetAuditChainRangeAsync`, `GetAuditChainPageAsync`, `ReadAuditTail`).
4. **Anchor Management & Cryptographic Utilities:** `TryLoadVerifiedAnchor`, `TryLoadVerifiedPurgeCheckpoint`, and `CreateSignedAnchor`.

---

## 2. Goals & Non-Goals

### 2.1 Goals
- **Extract Engine-Neutral Logic into `AuditSqlCore.cs`:**
  - Common row mapping (`ReadAuditEntry`, `ReadPurgeCheckpoint`).
  - Purge checkpoint operations (`GetLatestPurgeCheckpointAsync`, `SavePurgeCheckpointAsync`, `PlanPurgePrefixAsync`).
  - Querying and cursor paging (`GetAuditLogEntriesAsync`, `QueryAuditLogsAsync`, `QueryAuditLogPageAsync`).
  - Chain range queries (`GetAuditChainRangeAsync`, `GetAuditChainPageAsync`, `ReadAuditTail`).
  - Cryptographic anchor and checkpoint verification helpers (`TryLoadVerifiedAnchor`, `TryLoadVerifiedPurgeCheckpoint`, `CreateSignedAnchor`).
  - Core WORM hash-chain verification loop.
- **Drastically Reduce Source LOC:** Reduce the line counts of `PostgreSqlGovernanceRepository.Audit.cs`, `SqlServerGovernanceRepository.Audit.cs`, and `SqliteGovernanceRepository.Audit.cs` by at least 400-600 lines each, allowing the ratchet baselines in `source-size-ceilings.txt` to be significantly reduced.
- **Preserve Concurrency & Transaction Semantics:** Keep dialect-specific locking (`pg_advisory_xact_lock` on PostgreSQL, `sp_getapplock` on SQL Server, write-scope gating on SQLite) and bounded channel processing within each respective partial.
- **Zero Security or Semantic Drift:** Ensure strict tenant isolation (`tenant_id = @tenantId`, `StoredTenant.RequireWritable`), SHA-256 HMAC integrity, and fail-closed error handling remain completely intact.
- **Pass All Architecture and Unit Test Suites:** Verify with zero regression on all existing architecture, unit, and governance tests.

### 2.2 Non-Goals
- Changing database schema or DDL (`AUDIT_LOG_ENTRIES`, `AUDIT_PURGE_CHECKPOINTS`, `AUDIT_DEAD_LETTER`).
- Modifying the public contracts of `IGovernanceRepository` or `IAuditPurgeKeyProvider`.
- Unifying or changing channel batching retry algorithms or connection lifecycle management across dialects.

---

## 3. Scope & Requirements

### 3.1 Functional Requirements (REQ-FN)
- **REQ-FN-01 (Statement Execution Neutrality):** `AuditSqlCore` shall operate purely against `DbConnection`, `DbTransaction`, `DbCommand`, and `DbDataReader`. It shall never open, dispose, commit, or roll back database connections or transactions.
- **REQ-FN-02 (Parameter Binding Hygiene):** All parameters must be bound through `SqlCoreBinding.AddBound` ensuring that empty, null, or whitespace tenant parameters fail closed with `ArgumentException`.
- **REQ-FN-03 (Row Mapping Neutrality):** Centralize `ReadAuditEntry` and `ReadPurgeCheckpoint` in `AuditSqlCore` to ensure uniform parsing across all database providers.
- **REQ-FN-04 (Dialect SQL Segregation):** Dialect-specific SQL statements (such as `LIMIT` vs `TOP`, `rowid` vs `seq`) shall be defined in `private static class AuditSql` within each dialect repository partial or passed as statements into `AuditSqlCore`.

### 3.2 Security Requirements (REQ-SEC)
- **REQ-SEC-01 (Strict Tenant Isolation):** All tenant filters in queries (`WHERE tenant_id = @tenantId`) must be strictly preserved.
- **REQ-SEC-02 (Fail-Closed Hash Chain Verification):** Chain validation must fail closed (`false` or flag violation) if any gap, divergence, tampering, or invalid signature is detected.
- **REQ-SEC-03 (No Secret Leakage):** HMAC keys and cryptographic signatures must not be logged or exposed in exception messages.

### 3.3 Quality & Architecture Requirements (REQ-QA)
- **REQ-QA-01 (Naming Guard):** The core class must be named `AuditSqlCore` so that architecture reflection guards (e.g. `ConsentQueryTenantArchitectureTests` which excludes `*GovernanceRepository*`) properly scan it.
- **REQ-QA-02 (Source-Size Ratchet):** Baselines for the three Audit partials in `tests/Autheris.Tests.Architecture/Baselines/source-size-ceilings.txt` must be ratcheted downward to match the new reduced line counts.
- **REQ-QA-03 (TimeProvider Compliance):** Timestamps must use `TimeProvider` or explicit UTC formatting, ensuring zero regressions on `ClockAndHttpClientRatchetTests`.
