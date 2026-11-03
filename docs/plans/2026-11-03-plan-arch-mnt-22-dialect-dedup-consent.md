# Plan: ARCH-MNT-22 Step 4 - Dialect Dedup of Consent Repositories & ARCH-MNT-03 Namespace Consistency

**Document ID:** `PLAN-DIALECT-DEDUP-CONSENT-22`  
**Parent:** `docs/plans/2026-10-26-plan-architecture-maintainability.md` (Rows ARCH-MNT-22, ARCH-MNT-03, ARCH-MNT-24), `docs/plans/2026-11-03-req-arch-mnt-22-dialect-dedup-consent.md`  
**Phase:** Phase 2 (`csharp-architect`) & Phase 3 (`csharp-security-expert`)  
**Base:** `origin/main` `25c88f2d6`  
**Branch:** `refactor/arch-mnt-22-dialect-dedup-consent`  

---

## 1. Measured Duplication Baseline

The three dialect consent partials currently total **5,593 lines**:
- [`src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Consent.cs`](../../src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Consent.cs): **1,821 lines**
- [`src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.Consent.cs`](../../src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.Consent.cs): **1,854 lines**
- [`src/Autheris.Infrastructure/Persistence/SqliteGovernanceRepository.Consent.cs`](../../src/Autheris.Infrastructure/Persistence/SqliteGovernanceRepository.Consent.cs): **1,918 lines**

### Common vs. Dialect-Specific Breakdown

| Area / Operation | Status across PG / MSSQL / SQLite | Destination |
|---|---|---|
| `Consent` row mapping (subject, all-active, request-id, by-id, expiring) | Identical parsing & active-window clamping | `ConsentSqlCore` row readers & query helpers |
| `ConsentColumnRule` & `ConsentRowFilter` batch hydration & description loading | Identical mapping & JSON hop deserialization | `ConsentSqlCore.HydrateConsentsBatchAsync`, `LoadConsentDescriptionsAsync` |
| `CONSENTS`, `CONSENT_COLUMN_RULES`, `CONSENT_ROW_FILTERS` insertion | Identical parameter binding & audit payload | `ConsentSqlCore.InsertConsentRowsAsync`, `BuildConsentGrantedAudit` |
| `ConsentRequest` CRUD (`Create`, `Get`, `GetByRequester`, `GetPendingForApprover`, `GetByTicketId`, `Delete`, `UpdateTicketId`) | Identical parameter binding & step loading | `ConsentSqlCore` request helpers |
| Four-eyes approval state (`ReadLockedApprovalStateAsync`), step insertion, guarded status update (`ApplyGuardedStatusAsync`) | Identical logic & parameter binding | `ConsentSqlCore` approval helpers |
| ITSM activation (`ActivateConsentCoreAsync` status transition, duplicate check, column snapshot rules) | Identical SQL & snapshot rules | `ConsentSqlCore.TryActivateConsentRowsAsync` |
| Concurrency scopes, row lock hints, epoch bump, audit chain write | Dialect-specific | **Retained in Dialect Partials** |

---

## 2. Architecture Design Decisions

1. **Engine-Neutral `ConsentSqlCore` ($\le 1,000$ lines):**
   `internal static class ConsentSqlCore` in `Autheris.Infrastructure.Persistence` operates over `DbConnection`, `DbTransaction?`, `DbCommand`, and `DbDataReader` using `SqlCoreBinding.AddBound`. It never opens or disposes connections or transactions and never calls `DateTime.UtcNow` / `DateTimeOffset.UtcNow` (timestamps are passed in by callers).
2. **Compliance with `ConsentMaterializationGuardArchitectureTests`:**
   `ConsentMaterializationGuardArchitectureTests` scans each `{Provider}GovernanceRepository.Consent.cs` for methods containing the literal `"INSERT INTO CONSENTS"`, requires `"InsertConsentCoreAsync"` to be present in that list, and requires any other method in `.Consent.cs` that contains `"INSERT INTO CONSENTS"` or calls `"InsertConsentCoreAsync("` (except `CreateConsentAsync`) to call `"EnsureMayMaterializeAsync("`.
   Therefore:
   - Each `.Consent.cs` retains `private Task<Consent> InsertConsentCoreAsync(...)` which passes `"INSERT INTO CONSENTS ..."` (or contains the literal) to `ConsentSqlCore`.
   - `ActivateConsentCoreAsync` in `.Consent.cs` calls `EnsureMayMaterializeAsync(...)` before delegating to `ConsentSqlCore`.
3. **Compliance with `DialectSqlGoldenArchitectureTests`:**
   `RevokeSystemConsentAsync` in each `.Consent.cs` retains the literal `"REVOKED"` (via `ConsentGrantSqlCore.RevokedDecision`) and the direct call to `ConsentGrantSqlCore.ResolveConsentTenant(...)` in its state machine body so the IL scanner in `DialectSqlGoldenArchitectureTests.Revoke_Is_Unified_Across_Dialects` continues to pass.
4. **ARCH-MNT-03 Namespace Fixes:**
   - `McpStdioRunner` (`src/Autheris.Api/Mcp/McpStdioRunner.cs`): namespace `Autheris.Application.Mcp.Services` $\to$ `Autheris.Api.Mcp`.
   - `WebSqlSecurityException` (`src/Autheris.Application/Sql/WebSqlSecurityException.cs`): namespace `Autheris.Domain.Common` $\to$ `Autheris.Application.Sql`.
   - `FileSystemUniversalDocStore` (`src/Autheris.Infrastructure/Docs/FileSystemUniversalDocStore.cs`): namespace `Autheris.Application.Docs.Services` $\to$ `Autheris.Infrastructure.Docs`.
   - Empty `tests/Autheris.Tests.Architecture/Baselines/namespace-assembly-violations.txt`.

---

## 3. Work Package Breakdown

- **WP1: Create `ConsentSqlCore.cs` & Refactor `.Consent.cs` Partials**
  - Implement shared row readers, batch hydration, description loader, consent insertion helpers, consent request CRUD, approval state reader, guarded status transition, and activation column snapshot in `src/Autheris.Infrastructure/Persistence/ConsentSqlCore.cs`.
  - Refactor `PostgreSqlGovernanceRepository.Consent.cs`, `SqlServerGovernanceRepository.Consent.cs`, and `SqliteGovernanceRepository.Consent.cs` to < 1,000 lines each.
- **WP2: Update Architecture Allowlists & Ratchet Baselines**
  - Add `"Autheris.Infrastructure.Persistence.ConsentSqlCore"` to `CutoverArchitectureTests.CommandTextAllowlist`.
  - Remove the 3 `.Consent.cs` entries from `tests/Autheris.Tests.Architecture/Baselines/source-size-ceilings.txt`.
  - Lower `DateTimeOffset.UtcNow` counts for the `.Consent.cs` files in `tests/Autheris.Tests.Architecture/Baselines/datetime-now-counts.txt`.
- **WP3: ARCH-MNT-03 Namespace Consistency & Plan Housekeeping**
  - Update namespaces for `McpStdioRunner`, `WebSqlSecurityException`, and `FileSystemUniversalDocStore` and all referencing files.
  - Empty `tests/Autheris.Tests.Architecture/Baselines/namespace-assembly-violations.txt`.
  - Delete duplicate `docs/plans/2026-11-02-*-arch-mnt-15-bound-antlr-dfa-caches.md` files and update `docs/plans/00-master-plan-overview.md`.
- **WP4: Unit & Golden Tests**
  - Add `tests/Autheris.Tests.Unit/Persistence/DialectSql/ConsentSqlCoreGoldenTests.cs`.
  - Run `dotnet build Autheris.sln`, `Autheris.Tests.Architecture`, and unit test suites.

---

## 4. Phase 3: Security & Compliance Review (`csharp-security-expert`)

**Verdict:** APPROVED WITH BINDING CONDITIONS.

### Binding Security Conditions:
1. **SEC-CON-01 (Read-Time Expiry Clamp & Deny Semantics):** Active consent queries must preserve `Consent.IsActive(atTime)` read-time clamping (WP-SC-16) and future-dated `Deny` preservation in `GetActiveConsentsForSubjectsAsync`.
2. **SEC-CON-02 (Fail-Closed Row Filter Hop Deserialization):** Malformed `additional_hops_json` in `CONSENT_ROW_FILTERS` must throw `InvalidOperationException` on every dialect (never silently drop join hops).
3. **SEC-CON-03 (Four-Eyes & Separation of Duties):** `EnsureApproverIsNotRequesterAsync`, `ReadLockedApprovalStateAsync`, and `ApplyGuardedStatusAsync` must preserve self-approval rejection, shared `DATA_OWNERS` ID detection, and guarded status transitions (`WHERE id = @id AND status = @expected`).
4. **SEC-CON-04 (Tenant Isolation & Materialization Guard):** Every consent creation/activation path must enforce `StoredTenant.RequireWritable` and `EnsureMayMaterializeAsync` before writing rows.
