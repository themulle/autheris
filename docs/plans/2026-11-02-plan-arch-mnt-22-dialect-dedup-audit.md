# Plan: ARCH-MNT-22 Step 2 - Dialect Dedup of Audit Repositories

**Document ID:** `PLAN-DIALECT-DEDUP-AUDIT-22`  
**Parent:** `docs/plans/2026-10-26-plan-architecture-maintainability.md` (Row ARCH-MNT-22), `docs/plans/2026-11-02-req-arch-mnt-22-dialect-dedup-audit.md`  
**Phase:** Phase 2 (`csharp-architect`) & Phase 3 (`csharp-security-expert`)  
**Base:** `origin/main` `67b229f83`  
**Branch:** `refactor/arch-mnt-22-dialect-dedup-audit`  

---

## 1. Measured Duplication Baseline

The three dialect audit partials currently contain 4,379 raw lines:
- [`src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Audit.cs`](../../src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Audit.cs): **1,449 lines**
- [`src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.Audit.cs`](../../src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.Audit.cs): **1,505 lines**
- [`src/Autheris.Infrastructure/Persistence/SqliteGovernanceRepository.Audit.cs`](../../src/Autheris.Infrastructure/Persistence/SqliteGovernanceRepository.Audit.cs): **1,425 lines**

### Common vs Dialect-Specific Logic Breakdown

| Area / Operation | Status across PG / MSSQL / SQLite | Destination |
|---|---|---|
| `ReadAuditEntry` (12 columns) | 100% Identical mapping logic | `AuditSqlCore.ReadAuditEntry` |
| `ReadPurgeCheckpoint` (7 columns) | 100% Identical mapping logic | `AuditSqlCore.ReadPurgeCheckpoint` |
| `TryLoadVerifiedAnchor` / `TryLoadVerifiedPurgeCheckpoint` / `CreateSignedAnchor` | 100% Identical cryptographic logic | `AuditSqlCore` |
| `GetLatestPurgeCheckpointAsync` | Identical statement execution & mapping | `AuditSqlCore.GetLatestPurgeCheckpointAsync` |
| `SavePurgeCheckpointAsync` | Identical parameter binding & storage | `AuditSqlCore.SavePurgeCheckpointAsync` |
| `PlanPurgePrefixAsync` | Identical 4-step algorithm (oldest, surviving, newest, max) | `AuditSqlCore.PlanPurgePrefixAsync` |
| `GetAuditLogEntriesAsync` | Identical paging & tenant mapping | `AuditSqlCore.GetAuditLogEntriesAsync` |
| `QueryAuditLogsAsync` | Identical dynamic filter predicate construction | `AuditSqlCore.QueryAuditLogsAsync` |
| `QueryAuditLogPageAsync` | Identical cursor paging and slicing | `AuditSqlCore.QueryAuditLogPageAsync` |
| `GetAuditChainRangeAsync` | Identical min/max and count logic | `AuditSqlCore.GetAuditChainRangeAsync` |
| `GetAuditChainPageAsync` | Identical range paging and row wrapping | `AuditSqlCore.GetAuditChainPageAsync` |
| `ReadAuditTail` | Identical fallback count & tail extraction | `AuditSqlCore.ReadAuditTail` |
| Hash-chain scanning verification loop | Identical row-by-row verification and cryptographic checks | `AuditSqlCore.VerifyAuditRowsAsync` |
| Concurrency & Lock semantics (`pg_advisory_xact_lock`, `sp_getapplock`, SQLite write scopes) | Dialect-specific | **Retained in Dialect Partials** |
| Channel buffering & dead-letter queue (`_auditChannel`, `ProcessAuditChannelAsync`, `WriteToDeadLetterAsync`) | Dialect-specific | **Retained in Dialect Partials** |
| Provider-specific batch insert building (`AuditBatchInsertSql`, TVP / parameters) | Dialect-specific | **Retained in Dialect Partials** |

---

## 2. Architecture Design Decisions

1. **Pattern: Per-Dialect SQL Statements + Engine-Neutral Core:**
   Analogous to `ConsentGrantSqlCore` and `ItsmOutboxSqlCore`, an `internal static class AuditSqlCore` in namespace `Autheris.Infrastructure.Persistence` will own parameter binding, row hydration, and execution algorithms over `DbConnection`/`DbTransaction`/`DbCommand`/`DbDataReader`.
2. **Stateless Core & Resource Isolation:**
   `AuditSqlCore` never opens, gates, commits, rolls back, or disposes connections or transactions. The calling repository partial manages the connection lifetime (e.g. `OpenConnectionAsync` on PostgreSQL/SqlServer or `ReadScopeAsync`/`WriteScopeAsync` on SQLite).
3. **Dialect Statement Encapsulation:**
   Each dialect file encapsulates dialect SQL (such as `LIMIT` vs `TOP`, `rowid` vs `seq`) inside a nested `private static class AuditSql`.
4. **Parameter Binding:**
   All parameters use `SqlCoreBinding.AddBound(cmd, name, value)`. `GuardTenantParameter` guarantees that `@tenantId` or `@tenant` will throw `ArgumentException` if null/empty/whitespace, preventing fail-open multi-tenant leaks.
5. **Naming Compliance:**
   The core class must be named `AuditSqlCore` (and explicitly NOT contain `GovernanceRepository` in its name) to prevent bypassing architecture rules.

---

## 3. Work Package Breakdown

- **WP1: Core Implementation (`AuditSqlCore.cs`)**
  - Implement `ReadAuditEntry` and `ReadPurgeCheckpoint`.
  - Implement checkpoint methods (`GetLatestPurgeCheckpointAsync`, `SavePurgeCheckpointAsync`, `PlanPurgePrefixAsync`).
  - Implement query and paging methods (`GetAuditLogEntriesAsync`, `QueryAuditLogsAsync`, `QueryAuditLogPageAsync`).
  - Implement chain range and tail methods (`GetAuditChainRangeAsync`, `GetAuditChainPageAsync`, `ReadAuditTail`).
  - Implement anchor cryptographic methods (`TryLoadVerifiedAnchor`, `TryLoadVerifiedPurgeCheckpoint`, `CreateSignedAnchor`).
  - Implement chain row verification loop (`VerifyChainRowsAsync`).
- **WP2: Dialect Partial Refactoring**
  - Refactor `PostgreSqlGovernanceRepository.Audit.cs` to delegate common methods to `AuditSqlCore`.
  - Refactor `SqlServerGovernanceRepository.Audit.cs` to delegate common methods to `AuditSqlCore`.
  - Refactor `SqliteGovernanceRepository.Audit.cs` to delegate common methods to `AuditSqlCore`.
- **WP3: Architecture Baseline Ratchet Update**
  - Re-measure line counts of the 3 Audit partials.
  - Lower the ceilings in `tests/Autheris.Tests.Architecture/Baselines/source-size-ceilings.txt`.
- **WP4: Testing & Verification**
  - Execute architecture tests: `DialectSqlGoldenArchitectureTests`, `SourceSizeRatchetArchitectureTests`, `AuditArchitectureTests`.
  - Execute unit tests: `AuditPurgeChainIntegrityTests`, `AuditPurgeCheckpointAnchorStoreTests`, etc.

---

## 4. Phase 3: Security & Compliance Review (`csharp-security-expert`)

**Verdict:** APPROVED WITH BINDING CONDITIONS.

### Binding Security Conditions:
1. **SEC-AUD-01 (Tenant Parameter Guard):** All query statements filtering by tenant must bind `@tenantId` using `SqlCoreBinding.AddBound`, preserving the null/whitespace guard.
2. **SEC-AUD-02 (Fail-Closed Hash Chain Integrity):** The hash-chain scanning loop in `AuditSqlCore.VerifyAuditRowsAsync` must maintain exact fixed-time comparison (`CryptographicOperations.FixedTimeEquals`) for prev-hash, entry-hash, and anchor verification. Any mismatch must immediately flag the chain violation and return `false`.
3. **SEC-AUD-03 (WORM Checkpoint Monotonicity):** Checkpoint saving must strictly enforce monotonic advancement (`checkpoint.FirstSurvivingSequence > latest.FirstSurvivingSequence`).
4. **SEC-AUD-04 (Zero Secret Exposure):** HMAC keys (`_auditHmacKey`) must only be used in memory for hash computation and signature validation; no keys or raw signatures shall appear in log statements or exception messages.

---

## 5. Phase 5: Code Review & Quality Gate (`csharp-code-reviewer`)

**Verdict:** APPROVED.

- **Verification Results:**
  - Solution build: 0 warnings, 0 errors (`dotnet build Autheris.sln`).
  - Architecture test suite: 330 passed, 0 failed, 2 skipped (`tests/Autheris.Tests.Architecture`).
  - Persistence Dialect SQL suite: 121 passed, 0 failed, 0 skipped (`Persistence.DialectSql`).
  - Audit Unit test suite: 822 passed, 0 failed, 0 skipped (`FullyQualifiedName~Audit`).
- **Line Count Reductions:**
  - `PostgreSqlGovernanceRepository.Audit.cs`: 1,449 -> 1,064 (-385 lines)
  - `SqlServerGovernanceRepository.Audit.cs`: 1,505 -> 1,120 (-385 lines)
  - `SqliteGovernanceRepository.Audit.cs`: 1,425 -> 1,004 (-421 lines)
  - Total eliminated across 3 repositories: **1,191 duplicate lines**.
- **Architecture Baseline Ratchets:**
  - `source-size-ceilings.txt`: Lowered ceilings for all 3 repository audit partials.
  - `datetime-now-counts.txt`: Lowered ratchet ceilings across all 3 audit partials (4->3, 4->3, 5->4); zero raw clock reads introduced.
  - `CutoverArchitectureTests.CommandTextAllowlist`: `AuditSqlCore` added with documented architectural rationale.

