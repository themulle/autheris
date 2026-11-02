# Plan: ARCH-MNT-22 Step 3 - Dialect Dedup of Catalog Repositories

**Document ID:** `PLAN-DIALECT-DEDUP-CATALOG-22`  
**Parent:** `docs/plans/2026-10-26-plan-architecture-maintainability.md` (Row ARCH-MNT-22), `docs/plans/2026-11-02-req-arch-mnt-22-dialect-dedup-catalog.md`  
**Phase:** Phase 2 (`csharp-architect`) & Phase 3 (`csharp-security-expert`)  
**Base:** `origin/main` `67b229f83`  
**Branch:** `refactor/arch-mnt-22-dialect-dedup-catalog`  

---

## 1. Measured Duplication Baseline

The three dialect catalog partials contain 4,752 raw lines:
- [`src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Catalog.cs`](../../src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Catalog.cs): **1,541 lines**
- [`src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.Catalog.cs`](../../src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.Catalog.cs): **1,563 lines**
- [`src/Autheris.Infrastructure/Persistence/SqliteGovernanceRepository.Catalog.cs`](../../src/Autheris.Infrastructure/Persistence/SqliteGovernanceRepository.Catalog.cs): **1,648 lines**

### Common vs Dialect-Specific Logic Breakdown

| Area / Operation | Status across PG / MSSQL / SQLite | Destination |
|---|---|---|
| `Table` entity mapping (17 columns) | 100% Identical mapping logic | `CatalogSqlCore.ReadTableEntity` |
| `TableColumn` & `MaskingRule` mapping (8 columns) | 100% Identical mapping logic | `CatalogSqlCore.ReadTableColumn` |
| `TableRelation` mapping & queries | 100% Identical mapping logic | `CatalogSqlCore.ReadTableRelation`, `GetRelationsForTableAsync` |
| `DatasetCatalogSettings` & visibility queries | 100% Identical mapping logic | `CatalogSqlCore.GetDatasetCatalogSettingsAsync`, etc. |
| Epoch reading & increment logic | Identical algorithm & parameter binding | `CatalogSqlCore.GetTableEpochAsync`, `IncrementTableEpochAsync` |
| Active table identifier lookup | Identical queries & case-folding | `CatalogSqlCore.GetActiveTableIdentifierAsync` |
| Data ownership & approver checks | Identical recursive role expansion & matching | `CatalogSqlCore.IsAuthorizedApproverInternalAsync`, etc. |
| Catalog version bump & event dispatch | Identical statement execution & event publication | `CatalogSqlCore.BumpCatalogVersionInternalAsync`, etc. |
| Concurrency scopes & connection open | Dialect-specific (`OpenConnectionAsync` vs `ReadScopeAsync`/`WriteScopeAsync`) | **Retained in Dialect Partials** |

---

## 2. Architecture Design Decisions

1. **Pattern: Engine-Neutral `CatalogSqlCore`:**
   Following `ConsentGrantSqlCore`, `ItsmOutboxSqlCore`, and `AuditSqlCore`, an `internal static class CatalogSqlCore` in namespace `Autheris.Infrastructure.Persistence` will own parameter binding, row hydration, and execution algorithms over `DbConnection`/`DbTransaction`/`DbCommand`/`DbDataReader`.
2. **Stateless Core & Resource Isolation:**
   `CatalogSqlCore` never opens, gates, commits, rolls back, or disposes connections or transactions. The calling repository partial manages connection lifetimes and concurrency scopes.
3. **Dialect SQL Invariance:**
   Each per-dialect file retains dialect-specific SQL (e.g. `LOWER(...)`, parameter prefixes, schema qualifiers) inside a nested `internal static class CatalogSql`.
4. **Parameter Binding:**
   All parameters use `SqlCoreBinding.AddBound(cmd, name, value)` with standard null/whitespace validation.

---

## 3. Work Package Breakdown

- **WP1: Core Implementation (`CatalogSqlCore.cs`)**
  - Implement `ReadTableEntity`, `ReadTableColumn`, `ReadTableRelation`, `ReadDataOwner`.
  - Implement metadata loading and mapping helpers (`LoadTableMetadataAsync`, `GetAllTablesAsync`).
  - Implement relation queries and updates (`GetRelationsForTableAsync`, `CreateRelationAsync`, `GetAllRelationsAsync`, `SetRelationCrossDataSourceOptInAsync`).
  - Implement dataset settings operations (`GetDatasetVisibilityCountsAsync`, `GetDatasetCatalogSettingsAsync`, `WriteCatalogSettingsAsync`).
  - Implement data owner queries and approver authorization logic.
  - Implement catalog versioning and epoch helpers.
- **WP2: Dialect Partial Refactoring**
  - Refactor `PostgreSqlGovernanceRepository.Catalog.cs` to delegate common logic to `CatalogSqlCore`.
  - Refactor `SqlServerGovernanceRepository.Catalog.cs` to delegate common logic to `CatalogSqlCore`.
  - Refactor `SqliteGovernanceRepository.Catalog.cs` to delegate common logic to `CatalogSqlCore`.
- **WP3: Architecture Baseline Ratchet Update**
  - Re-measure line counts of the 3 Catalog partials.
  - Lower the ceilings in `tests/Autheris.Tests.Architecture/Baselines/source-size-ceilings.txt`.
  - Allowlist `CatalogSqlCore` in `CutoverArchitectureTests.CommandTextAllowlist`.
- **WP4: Testing & Verification**
  - Add L2 runtime golden tests in `tests/Autheris.Tests.Unit/Persistence/DialectSql/CatalogSqlCoreGoldenTests.cs`.
  - Run all architecture tests, catalog unit tests, and integration test suites.

---

## 4. Phase 3: Security & Compliance Review (`csharp-security-expert`)

**Verdict:** APPROVED WITH BINDING CONDITIONS.

### Binding Security Conditions:
1. **SEC-CAT-01 (Approver Authorization Fail-Closed):** `IsAuthorizedApproverForTableAsync` must maintain fail-closed evaluation: if no table entity, no data owner, or no matching transitive role is found, it must return `false` (never grant access on ambiguous state).
2. **SEC-CAT-02 (Transitive Role Resolution Isolation):** Recursive role resolution (`GetTransitiveRolesAsync`) must bound cyclic role hierarchies and ignore unmapped SIDs without leaking unapproved permissions.
3. **SEC-CAT-03 (Cross-Data-Source Opt-In Protection):** `SetRelationCrossDataSourceOptInAsync` must strictly enforce row-level verification under the relation ID and never allow wildcards across unapproved data source boundaries.
4. **SEC-CAT-04 (Epoch Monotonicity):** Table epoch increments must be strictly monotonic (`epoch + 1`), ensuring cache invalidation across distributed instances.
