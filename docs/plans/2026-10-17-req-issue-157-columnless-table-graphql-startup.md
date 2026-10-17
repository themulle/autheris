# REQ-BUG-157: A Catalog Table Without Queryable Columns Must Not Stop the Gateway (GraphQL Schema Build)

**Document ID:** `REQ-BUG-157`
**Date:** 2026-10-17
**Status:** PRD READY (Phase 1 complete); next: Phase 2 plan (short), Phase 3 review (short), Phase 4 TDD fix
**Priority:** P1 availability bug (one catalog row takes every data source down, restart loop)
**Author:** Delivery Project Manager (Phase 1)
**Source:** GitHub issue #157 (v1.1.15, unchanged in v1.1.16 and `main`)
**Branch:** `docs/issue-intake-2026-10-17b` (docs only; suggested fix branch `fix/issue-157-columnless-table-schema`)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md); assessment [`INTAKE-2026-10-17B`](2026-10-17-issue-intake-assessment-2.md)

---

## 1. Verdict

**Sensible with changes.** The bug is real and the root cause in the issue is correct. Changes against the issue:

- The trigger is wider than "zero columns": a table whose columns are **all** dropped by the plain-identifier rule
  (R-GQL-13) produces the same empty type. The guard must test the columns that survive filtering, not
  `meta.Columns.Count`.
- **No rejection at write or import time** (the issue's optional part). Many writers create table rows
  (dbt, OpenMetadata, OpenAPI, data-catalog sync, virtual system tables, the caller's own schema), and a table without
  columns is a legitimate intermediate state of an import. The consumer side must be robust; every other read path
  already treats such a table as unusable (see 2.2).
- Diagnostics: a reason-coded list of omitted tables on the schema model, logged as a warning, plus an admin-only
  entry in the existing system health endpoint. No new endpoint.

| Item | Result |
|---|---|
| Size | S |
| Wave | 1 (bug), proposed rank 1.9, standalone step |
| Dependencies | none; touches `CatalogSchemaModel.cs` (G-SHARED file, also edited by #138 INC-4): merge before #138 INC-4 |
| Security class | Availability (denial of service by catalog content). No disclosure. Phase 3: short review |

## 2. Verified Current State (code at `origin/main` `fa7e29c0`)

### 2.1 Claim verification

| # | Claim in #157 | Result | Evidence |
|---|---|---|---|
| C1 | `<type>_filter` and `<type>_order_by` are created for every active SQL table | **Confirmed** | Names at `src/Autheris.GraphQL/Catalog/CatalogSchemaModel.cs:155-160`; types emitted in `CatalogGraphQlTypeModule.cs:191-215` |
| C2 | With zero columns the input object has no fields | **Confirmed for `_order_by` only.** `_filter` always has `and`/`or`/`not` (`CatalogGraphQlTypeModule.cs:197-199`). `_order_by` gets only column fields (`:207-215`). The entity `ObjectType` (`:217-255`) is also empty when the table has no relations | |
| C3 | `SchemaException` from `RequestExecutorWarmupService.StartAsync` stops the host | **Confirmed by code path** (HotChocolate 16.6.8 eager executor initialisation; type validation rejects input and object types without fields). The `try/catch` in `CreateTypesAsync` (`CatalogGraphQlTypeModule.cs:134-151`) only guards `CatalogSchemaModel.BuildAsync`; type validation runs later inside HotChocolate and is not covered | |
| C4 | Name collisions are already omitted with a log entry | **Confirmed** | `CatalogSchemaModel.cs:162-172` (`LogError`, `continue`) |
| C5 | Tables without columns are not | **Confirmed** | Loop `:184-214` builds `columns`; `:216` adds the table unconditionally |

### 2.2 New findings (same code path, in scope)

1. **All-columns-filtered case.** Columns that fail `QueryableColumnNameRegex` are skipped (`CatalogSchemaModel.cs:188-192`).
   A view whose columns are all named like `"order-id"` or `"Kunden Nr"` yields an empty column list and the same crash.
2. **Name reservation order.** Type names are reserved (`:176-178`) before columns are computed. If the guard is added
   after `:216` without reordering, an omitted table still blocks its three names for a later valid table. Columns must
   be computed first, then the collision check, then reservation.
3. **Runtime refresh path.** The 60 s change check (`CatalogGraphQlTypeModule.cs:63-104`) fires `TypesChanged` when
   the fingerprint changes. A column-less table added while the gateway runs breaks the next executor rebuild
   (expected: GraphQL requests fail until the row is fixed; to be confirmed by a test in Phase 2). The same guard fixes
   both paths because both use `CatalogSchemaModel.BuildAsync`.
4. **Relations to an omitted table** disappear automatically: relation mapping looks up targets in `tableMapById`
   (`:219-221`, `:246-249`), which is built from the included tables only. A test locks this.
5. **Other surfaces are already robust.** The governed SQL rewriter, procedure validator and procedure execution treat a
   table with `Columns.Count == 0` as unknown or unavailable (`GovernedSqlRewriter.cs:394`,
   `StoredProcedureCatalogValidator.cs:476`, `GovernedProcedureExecutionService.cs:352`). The MCP GraphQL map
   (`Mcp/CatalogGraphQlMap.cs`) uses the same model and stays consistent with the fix.
6. **Fingerprint.** `Fingerprint()` (`CatalogSchemaModel.cs:62-`) is built from included tables only, so a table that later
   gains columns changes the fingerprint and triggers a rebuild. No extra work.

### 2.3 Overlap with existing plans

- `REQ-CAT-138` INC-4 (bulk relation loading) edits `CatalogSchemaModel.BuildAsync`; #157 is merged first.
- `REQ-OPS-150` (warm-up): the warm-up must not hide schema build failures; #157 removes the most likely cause of one.
- PR #139 (`PLAN-BUG-75-GATING`) already omits non-executable relations in the same method; same pattern.

## 3. Problem Statement

Catalogs are filled by importers that cannot always map columns. One table row without usable columns makes the
GraphQL schema invalid, the host fails startup and the container restarts in a loop, so REST, OData, MCP and SQL
become unavailable too. A single bad catalog row must degrade only itself.

## 4. Benchmark and Rejected Anti-Patterns

- Benchmark: Hasura and PostGraphile skip tables they cannot represent and report them (Hasura "inconsistent metadata"
  list) instead of refusing to start. The house pattern is the same (`CatalogSchemaModel.cs:162-172`, dialect exclusion
  at `:122-129`).
- Rejected: **placeholder fields** (for example a dummy `_` field in `_order_by` or the entity type). It advertises a
  type that can never return data and needs special cases in the tree compiler.
- Rejected: **catch the HotChocolate exception and start without the catalog.** One bad row would still remove every
  table from GraphQL. It may be added later as a last-resort safety net, but not as the fix.
- Rejected: **reject at write time** (see section 1).

## 5. Requirements

- **R-1** `CatalogSchemaModel.BuildAsync` omits a table whose filtered column list (after R-GQL-13 and masking type
  mapping) is empty. The check runs before type names are reserved (finding 2).
- **R-2** Omission is logged once per build as a warning with the qualified table name and reason
  `NoQueryableColumns` (`LogWarning`; the existing collision log stays `LogError`). The refresh change check passes
  `logger: null` today (`CatalogGraphQlTypeModule.cs:85`) and must stay quiet; only the real build logs.
- **R-3** The model exposes `OmittedTables`: a list of `(TableIdentifier, Reason)` with reasons `NoQueryableColumns`,
  `NameCollision`, `UnsupportedDialect`. Existing collision and dialect omissions are recorded there too.
- **R-4** The governance system health endpoint (`SystemEndpoints.cs:46`, `/api/governance/system/health`, already
  gated by `SystemMetrics:Enabled`, `ExposeRestEndpoints` and the system-metrics allowed roles) reports the count and the
  reason-coded list. No other endpoint exposes the list.
- **R-5** Relations from or to an omitted table are not exposed (falls out of R-1; locked by a test).
- **R-6** Invariant: the model never contains a table type with zero column fields; a unit test asserts that every
  generated `_order_by` input and entity type has at least one field for a catalog mixing valid, column-less and
  all-filtered tables.
- **R-7** No change to REST, OData, MCP or SQL behaviour for such tables.

## 6. Security Requirements

- Fail closed: an omitted table is absent from the schema (unknown field), never exposed with partial types.
- No table names to non-admin callers in diagnostics (catalog metadata may be confidential).
- Log lines carry the qualified name and reason only, no column data.

## 7. Non-Goals

- Validation or rejection of column-less tables in importers or admin writes.
- A general "catalog health" product feature.
- A last-resort fallback to a catalog-free schema when HotChocolate rejects the schema for another reason.

## 8. Deviations from the Issue

| Issue text | PRD | Why |
|---|---|---|
| "tables without columns" | tables without **queryable** columns | All-filtered tables crash the same way |
| optionally reject at write/import | not done | Many writers; legitimate intermediate state; consumer side already robust |
| "visible in diagnostics or catalog health" | warning log plus admin-only list in the existing system health endpoint | No new endpoint, no metadata leak |

## 9. Open Owner Questions

| ID | Question | Recommended default |
|---|---|---|
| Q-157-1 | Reject column-less tables at write/import time as well? | No |
| Q-157-2 | Diagnostics surface: log only, or log plus admin-only list in `/api/governance/system/health`? | Log plus admin-only list |
| Q-157-3 | Add a last-resort safety net (start with a catalog-free GraphQL schema if HotChocolate still rejects it)? | Not now; separate item if another schema-validation crash appears |

## 10. Increments and Waves

One increment, size S, wave 1 (proposed rank 1.9). Can start right away: no shared files with the wave-1 items 1.6
(procedures) and 1.7 (MCP catalog visibility); it shares `CatalogSchemaModel.cs` only with #138 INC-4 (wave 3).

## 11. Test Strategy Hints (for Phase 2)

- Unit (`tests/Autheris.Tests.Unit/GraphQL/CatalogSchemaModelTests.cs`, collision tests at `:67-114` as template):
  column-less table omitted and listed with `NoQueryableColumns`; all-columns-filtered table omitted; an omitted table
  does not reserve its names (a later table with the same sanitized name is kept); relation to an omitted table absent.
- Schema test: build the HotChocolate schema with `CatalogGraphQlTypeModule` over a catalog with one valid and one
  column-less table; `BuildSchemaAsync` succeeds, the valid table's query field exists, the other does not (Red today
  with `SchemaException`).
- Integration: host start with such a catalog row (SQLite) reaches ready and answers a GraphQL query on another table.
- Refresh: change check plus rebuild after a column-less table appears at runtime keeps the executor usable.
