# Requirements: ARCH-MNT-22 Step 3 - Dialect Dedup of Catalog Repositories

**Document ID:** `REQ-DIALECT-DEDUP-CATALOG-22`  
**Parent:** `docs/plans/2026-10-26-plan-architecture-maintainability.md` (Row ARCH-MNT-22)  
**Phase:** Phase 1 (`product-manager`)  
**Base:** `origin/main` `67b229f83`  
**Branch:** `refactor/arch-mnt-22-dialect-dedup-catalog`  

---

## 1. Executive Summary & Problem Statement

Following the successful execution of **ARCH-MNT-22 Step 1 (Outbox & Consent Grants)** and **Step 2 (Audit Repositories)**, the largest remaining source-code duplication in `src/Autheris.Infrastructure/Persistence/` resides in the Catalog repository partials:
- [`PostgreSqlGovernanceRepository.Catalog.cs`](../../src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Catalog.cs): **1,541 lines**
- [`SqlServerGovernanceRepository.Catalog.cs`](../../src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.Catalog.cs): **1,563 lines**
- [`SqliteGovernanceRepository.Catalog.cs`](../../src/Autheris.Infrastructure/Persistence/SqliteGovernanceRepository.Catalog.cs): **1,648 lines**
- **Total Catalog Persistence Surface:** **4,752 lines**.

Over 60% of these files consist of identical row mapping, metadata hydration, and query logic:
1. `Table` entity mapping (17 columns: id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active, data_source_type, http_endpoint_json, plugin_name, description, long_description, doc_source, required_filter_columns_json, texts_json).
2. `TableColumn` and `MaskingRule` mapping (8 columns: id, column_name, data_type, is_sensitive, sensitivity, mask_type, mask_expression, texts_json).
3. `DatasetCatalogSettings` queries, tag replacement, and visibility aggregation.
4. Table epoch reading and bumping (`GetTableEpochAsync`, `IncrementTableEpochAsync`).
5. `TableRelation` queries and write operations (`GetRelationsForTableAsync`, `CreateRelationAsync`, `GetAllRelationsAsync`, `SetRelationCrossDataSourceOptInAsync`).
6. Data ownership and approver resolution (`GetDataOwnersForTableAsync`, `IsAuthorizedApproverForTableAsync`, `GetTransitiveRolesAsync`, `DelegateDataOwnershipAsync`).
7. Catalog versioning and cache invalidation notification (`GetCatalogVersionAsync`, `BumpCatalogVersionInternalAsync`, `PublishCatalogChangedEventAsync`).

This duplication increases maintenance overhead and risks divergent behavior when catalog features or metadata fields evolve.

---

## 2. Invariants & Guardrails

- **INV-CAT-01 (Exact SQL & Lock Preservation):** Dialect-specific concurrency semantics (e.g. SQLite read/write scopes, PostgreSQL connection pooling, SQL Server table hints) must remain in the dialect partials.
- **INV-CAT-02 (Schema Immutability & Gate Compliance):** No schema DDL changes; SQL text, parameter names, and type mappings must stay byte-compatible with existing schema migrations.
- **INV-CAT-03 (Performance & Zero Allocation):** Row reading must avoid redundant allocations; use ordinal index reading and string pooling where applicable.
- **INV-CAT-04 (Fail-Closed Governance & Authorization):** Approver checking (`IsAuthorizedApproverForTableAsync`) and ownership delegations must strictly preserve their evaluation logic and transitive role resolution.
- **INV-CAT-05 (Architecture Ratchets):** The line count ceilings in `source-size-ceilings.txt` for the three `Catalog.cs` partials must ratchet down by over 1,000 lines combined.

---

## 3. Success Metrics

1. **Code Reduction:** Eliminate $\ge 1,000$ lines of duplicate code across the 3 catalog partials.
2. **Architecture Ratchet:** Ratchet down `source-size-ceilings.txt` for `PostgreSqlGovernanceRepository.Catalog.cs`, `SqlServerGovernanceRepository.Catalog.cs`, and `SqliteGovernanceRepository.Catalog.cs`.
3. **Verification:** 100% green test suite across Architecture, Catalog Unit, and Integration test suites.
