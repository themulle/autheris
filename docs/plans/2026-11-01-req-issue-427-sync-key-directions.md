# PRD: Source Physical Key and Index Direction Ingestion (#427)

**Document ID:** `REQ-CAT-427`  
**Phase:** 1 (Product Management Qualification & Intake)  
**Author:** Enterprise Product Manager & Strategic Gatekeeper  
**Date:** 2026-11-01  
**Status:** PROPOSED  
**Issue Reference:** [Issue #427](https://github.com/themulle/autheris/issues/427)  
**Related Documents:** [Issue #390](https://github.com/themulle/autheris/issues/390), [Issue #403](https://github.com/themulle/autheris/issues/403), [Issue #404](https://github.com/themulle/autheris/issues/404), [`2026-10-30-plan-issue-403-sync-key-direction.md`](file:///root/autheris/docs/plans/2026-10-30-plan-issue-403-sync-key-direction.md), [F-GOV-21](file:///root/autheris/docs/features/f-gov-21-declared-table-keys-and-read-ordering.md)

---

## 1. Problem Statement & Motivation

In Autheris v1.6.0:
- Table key and index column directions (`ASC` / `DESC`) are stored in the governance catalog (migration step 0002, #390).
- `DefaultReadOrdering`, `TreeSqlCompiler`, and `GovernedTableReadPlanner` use these directions for default ordering and tie-breakers.
- `IPhysicalKeyCatalog` and `PhysicalKeyDirectionReconciler` (#403) read physical index directions from PostgreSQL and SQL Server sources, but **only during an external data catalog sync** (`DataCatalogSyncService`).
- Non-unique physical indexes (#404) are likewise imported only during that sync.

### Gap in Ingestion Channels
Many Autheris deployments do not use an external enterprise data catalog (such as Purview, Collibra, or OpenMetadata). Instead, tables and models enter the Autheris catalog through:
1. **dbt Artifact Ingestion (`DbtMetadataIngestionService`):** dbt models and sources define primary keys or unique constraints (e.g., `device_id, ts`), but manifest constraints rarely specify direction tokens like `ts DESC`.
2. **Catalog REST API (`PUT /api/catalog/datasets/{id}/keys`):** Keys are updated or registered via CI/CD pipelines without direction metadata.
3. **Stand-alone / Direct Ingestion:** Deployments configuring SQL data sources directly without an external catalog sync never execute `DataCatalogSyncService`.

### Observed Impact
When a SQL Server table has a clustered primary key with a descending column (e.g. `(client_id ASC, ts DESC)`):
- Because the dbt or API declaration did not specify `DESC`, the catalog defaults each column to `ASC`.
- Any read query without explicit `orderBy` compiles to `ORDER BY client_id, ts` (ascending).
- Directly on SQL Server:
  - `SELECT TOP 20 * FROM t ORDER BY client_id ASC, ts DESC`: Instant index seek (< 1 ms).
  - `SELECT TOP 20 * FROM t ORDER BY client_id ASC, ts ASC`: Full table scan and sort across millions of rows (causing query timeouts and CPU spikes).

Although `PhysicalKeyDirectionReconciler` already implements safe matching logic, it is unreachable for dbt-managed or directly configured SQL tables.

---

## 2. Gatekeeper Qualification & Deduplication

### Catalog Deduplication Check (`docs/features/`)
- Existing capabilities:
  - `IPhysicalKeyCatalog` exists and can inspect physical indexes on SQL Server and PostgreSQL via `GovernedCatalogProvider.PhysicalKeys.cs`.
  - `PhysicalKeyDirectionReconciler.Reconcile` cleanly reconciles incoming metadata with physical snapshots, adhering to strict precedence: **Declared > Stored > Physical**.
  - `DbtMetadataIngestionService` ingests dbt models and sources but currently passes only parsed AST tokens without querying the physical database.
- **Verdict:** Valid performance and governance gap. We should leverage the existing `IPhysicalKeyCatalog` and `PhysicalKeyDirectionReconciler` without creating duplicate reconciler abstractions.

### Zero-Trust & Architectural Boundary Rules
- **"No Key is Ever Inferred" Rule:** Autheris forbids inferring semantic keys that the data owner or dbt contract did not declare.
  - *Safety check:* Reading physical direction for a **declared** column list `(c1, c2)` does NOT violate this rule because the column set is already declared by the owner. It only attaches physical storage attributes (`ASC`/`DESC`).
  - If the physical index column list differs from the declared key (e.g., physical key is `(c1, c2, c3)` while declared key is `(c1, c2)`), the reconciler fails closed with `Mismatch` and assigns NO direction.
- **Fail-Closed & Dialect Scoping:**
  - Physical inspection only applies to supported SQL dialects (PostgreSQL, SQL Server). Unsupported dialects (SQLite, Oracle, DuckDB) or offline sources remain unchanged (default `ASC`).
  - Network timeouts or permission errors (`Unavailable`) must fail closed: leave the declared direction as-is and log a structured warning without failing the entire ingestion job.

---

## 3. Scope & Proposed Architecture

We propose two complementary mechanisms:

### Feature Track A: Physical Key Reconciler Integration into dbt Ingestion
- In `DbtMetadataIngestionService`:
  - When importing models or sources associated with a SQL data source:
  - If `IPhysicalKeyCatalog` is available and the dialect is supported:
  - For each table where a key was declared via dbt without explicit directions, query `IPhysicalKeyCatalog.ReadAsync(metadata)`.
  - Pass the snapshot to `PhysicalKeyDirectionReconciler.Reconcile(metadata, existing, snapshot)`.
  - Persist reconciled directions and non-unique indexes.
  - Record audit event `CATALOG_KEY_DEFINITION_CHANGED` with `directionSource=physical` and actor `system:dbt-sync`.

### Feature Track B: Administrative Physical Key Refresh Endpoint
- Add a governed administrative endpoint for ad-hoc or scheduled reconciliation:
  - `POST /api/catalog/datasources/{id}/refresh-physical-keys` (and `/refresh-physical-indexes`)
  - **Authorization:** Requires `ClusterAdmin` or `GovernanceAdmin` role.
  - **Parameters:**
    - `dryRun` (`bool`, default `false`): If true, returns detected directions and drift without writing changes or audit records.
    - `tableFilter` (`string?`, optional): Specific table name to refresh.
  - **Behavior:**
    - Iterates over declared tables for the target SQL datasource.
    - Inspects physical indexes via `IPhysicalKeyCatalog`.
    - Applies `PhysicalKeyDirectionReconciler`.
    - If differences exist, writes updated metadata to `CatalogKeyDefinitionStore` and emits `CATALOG_KEY_DEFINITION_CHANGED` audit records.
    - Invalidates catalog caches for updated tables.

---

## 4. Acceptance Criteria

1. **AC-1 (dbt Physical Direction Reconciliation):** A dbt source or model defining a primary key `(device_id, ts)` on a SQL Server table whose clustered index is `(device_id ASC, ts DESC)` is stored with `PrimaryKeyDirections = [Ascending, Descending]` when ingested.
2. **AC-2 (Declared Precedence Preserved):** If a dbt constraint explicitly specifies direction tokens (e.g. `device_id ASC, ts ASC`), the declared direction takes precedence over the physical database index (`PhysicalKeyDirectionReconciler.DriftCode` warning emitted).
3. **AC-3 (Column Mismatch Fails Closed):** If the physical primary key columns do not match the declared key columns, no direction is inferred, existing behavior is preserved, and a structured warning is reported.
4. **AC-4 (Admin Refresh Operation):** An admin endpoint `POST /api/catalog/datasources/{id}/refresh-physical-keys` refreshes physical key directions and non-unique indexes for existing catalog tables, supporting `dryRun=true` and tenant isolation.
5. **AC-5 (Audit Trail Integrity):** Every direction change resulting from physical reconciliation writes an immutable audit record with `directionSource=physical`.
6. **AC-6 (Graceful Degradation):** Transient database connection failures or lack of `VIEW DEFINITION` permissions during reconciliation log a warning and leave the dataset in its declared state without failing the ingestion process.

---

## 5. RICE-C Prioritization

- **Reach:** 7/10 (All users using dbt or direct catalog registration on SQL Server / PostgreSQL).
- **Impact:** 2.5 (Eliminates full table sorts and timeouts on indexed time-series / chronological tables).
- **Confidence:** 95% (`PhysicalKeyDirectionReconciler` and `IPhysicalKeyCatalog` already exist and are proven by #403/#404).
- **Compliance Weight:** 1.0 (Zero-trust compliant; maintains data-owner declaration precedence).
- **Effort:** 1.5 Person-Weeks (M size, ~350-500 LOC).

**Lifecycle Stage:** Approved by Product Management $\to$ Hand off to **Phase 2 (Solution Architect)**.
