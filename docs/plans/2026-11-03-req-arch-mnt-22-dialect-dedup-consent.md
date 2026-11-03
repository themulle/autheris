# Requirements: ARCH-MNT-22 Step 4 - Dialect Dedup of Consent Repositories & ARCH-MNT-03 Namespace Consistency

**Document ID:** `REQ-DIALECT-DEDUP-CONSENT-22`  
**Parent:** `docs/plans/2026-10-26-plan-architecture-maintainability.md` (Rows ARCH-MNT-22, ARCH-MNT-03, ARCH-MNT-24)  
**Phase:** Phase 1 (`product-manager`)  
**Base:** `origin/main` `25c88f2d6`  
**Branch:** `refactor/arch-mnt-22-dialect-dedup-consent`  

---

## 1. Executive Summary & Problem Statement

Following **ARCH-MNT-22 Step 1 (Outbox & Consent Grants)**, **Step 2 (Audit)**, and **Step 3 (Catalog)**, the largest remaining source-code duplication across the three governance repositories resides in the Consent repository partials:
- [`PostgreSqlGovernanceRepository.Consent.cs`](../../src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Consent.cs): **1,821 lines** (ceiling 1,920)
- [`SqlServerGovernanceRepository.Consent.cs`](../../src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.Consent.cs): **1,854 lines** (ceiling 1,952)
- [`SqliteGovernanceRepository.Consent.cs`](../../src/Autheris.Infrastructure/Persistence/SqliteGovernanceRepository.Consent.cs): **1,918 lines** (ceiling 2,026)
- **Total Consent Persistence Surface:** **5,593 lines**.

Over 65% of these files consist of duplicated row mapping, batch hydration, parameter binding, consent insertion, consent request CRUD, four-eyes approval state reads, guarded status transitions, and ITSM activation column snapshots.

Additionally, **ARCH-MNT-03** (`Baselines/namespace-assembly-violations.txt`) still records 3 production types whose namespaces do not match their assembly root (`McpStdioRunner` in `Autheris.Api`, `WebSqlSecurityException` in `Autheris.Application`, and `FileSystemUniversalDocStore` in `Autheris.Infrastructure`), and `docs/plans/` contains duplicate `2026-11-02-*-arch-mnt-15-bound-antlr-dfa-caches.md` files for an item already implemented and merged in PR #364 / #370.

---

## 2. Invariants & Guardrails

- **INV-CON-01 (Exact SQL, Lock & Concurrency Preservation):** Dialect-specific concurrency semantics (`OpenConnectionAsync` and `FOR UPDATE` / `WITH (UPDLOCK, ROWLOCK, HOLDLOCK)` in PostgreSQL/SQL Server vs. `ReadScopeAsync`/`WriteScopeAsync` in SQLite) must remain in the dialect partials.
- **INV-CON-02 (SQLite WriteScope Non-Reentrancy):** Never call connection-opening or gate-acquiring repository methods while holding `WriteScopeAsync` on SQLite (prevents self-deadlock on in-memory SQLite).
- **INV-CON-03 (Architecture Guard Compliance):**
  - `ConsentMaterializationGuardArchitectureTests`: Keep `InsertConsentCoreAsync` with `INSERT INTO CONSENTS` in each `.Consent.cs` partial or ensure every consent insertion path passes `EnsureMayMaterializeAsync` as required by the architecture test.
  - `DialectSqlGoldenArchitectureTests`: Keep `RevokeSystemConsentAsync` calling `ConsentGrantSqlCore.ResolveConsentTenant` and emitting `ConsentGrantSqlCore.RevokedDecision` (`"REVOKED"`).
  - `CutoverArchitectureTests`: Register `ConsentSqlCore` in `CommandTextAllowlist`.
  - `ClockAndHttpClientRatchetTests`: Do not introduce `DateTime.UtcNow` or `DateTimeOffset.UtcNow` inside `ConsentSqlCore.cs`; lower the counts in `Baselines/datetime-now-counts.txt` where applicable.
  - `SourceSizeRatchetArchitectureTests`: Reduce all three `.Consent.cs` partials below 1,000 lines and remove their entries from `Baselines/source-size-ceilings.txt` (while keeping `ConsentSqlCore.cs` $\le 1,000$ lines).
- **INV-CON-04 (ARCH-MNT-03 Namespace Root Consistency):** Move the 3 allowlisted types into namespaces rooted at their respective assembly names and empty `Baselines/namespace-assembly-violations.txt`.

---

## 3. Success Metrics

1. **Code Reduction:** Eliminate $> 2,000$ lines of duplicate code across the 3 `.Consent.cs` partials, bringing each file below the 1,000-line ceiling and removing all 3 `.Consent.cs` entries from `Baselines/source-size-ceilings.txt`.
2. **Namespace Violations Retired:** `Baselines/namespace-assembly-violations.txt` has 0 entries.
3. **Verification:** 100% green build and test suite across Architecture and Unit test suites.
