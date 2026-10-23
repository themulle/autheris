# PRD: Issue #282 - Register SQL data source tables via dbt sync

ID: `REQ-CAT-282`. Phase 1 (product-manager). Status: PRD ready for Phase 2.

## Problem
No supported way creates the first catalog entries for tables of a configured SQL data source (`DataSources:Connections:*`). `POST /api/extensions/dbt/sync` updates only tables with an existing entry (`existingMeta != null` in `DbtMetadataIngestionService`) and skips others silently. `dbt/governance` and `consents/apply` then fail with "not found in catalog". The only workaround is direct writes to governance tables (no validation, no audit, schema-coupled).

## Decision (default taken)
- Option 1 (dbt sync creates missing entries): IN SCOPE (WP-1).
- Option 2 (admin endpoint introspecting a data source): OUT OF SCOPE. No reusable schema-introspection service exists in `src/` (grep found none), so it is not cheap. Tracked as follow-up (OQ-1).
- Rationale: smallest change that satisfies the issue acceptance sequence (sync, governance import, consent manifest); reuses the manifest as the single schema source; no live DB read at sync time.

## Requirements
1. REQ-282-1: `dbt/sync` creates a catalog entry for each manifest model/source that has no entry and whose `database` maps to a configured SQL data source (`DataSources:Connections:*` name, case-insensitive exact match). Columns and types come from the manifest (`columns[].data_type`, missing type stored as unknown/string, never guessed).
2. REQ-282-2: Created entries are inactive (`IsActive = false`) by default. New option `Dbt:ActivateNewTables` (bool, default `false`) mirrors the existing `DataCatalog`/`OpenMetadata` `ActivateNewTables`. Activation stays an explicit admin action. (Note: there is no global `Catalog:ActivateNewTables`; the per-integration pattern is followed.)
3. REQ-282-3: New option `Dbt:CreateMissingTables` (bool, default `true` per no-production rule, OQ-2). When `false`, behavior is as today but skipped items are still reported (REQ-282-5).
4. REQ-282-4: Fail-closed: creation requires the admin scope already required by `dbt/sync` (`RequireAuthorization` plus the same admin policy as the config-changing endpoints). No mapping to a configured data source means no entry (skip, never default to a source). Names (schema/table/column) are validated with the existing identifier validation; invalid names are skipped with a reason. No dynamic SQL is executed; no connection to the data source is opened.
5. REQ-282-5: `DbtSyncResult` gains `CreatedTables` (list of qualified names) and `SkippedItems` (list of `{ name, kind: model|source, reason }`). Reason codes: `NoDataSourceMapping`, `DataSourceNotSql`, `InvalidIdentifier`, `CreationDisabled`, `Ineligible` (e.g. ephemeral models, disabled nodes), `Duplicate`. The sync no longer skips silently; each skip also logs one warning.
6. REQ-282-6: Idempotent: re-running sync does not duplicate entries and never overwrites `IsActive`, sensitivity, masks, consents or other governance fields of existing entries (existing update path unchanged).
7. REQ-282-7: `dryRun=true` returns the would-be `CreatedTables` and `SkippedItems` and writes nothing.
8. REQ-282-8: Each creation is audited (existing `AuditConfigChanged` full audit on the endpoint plus per-table created count and names in the audit payload; no column data or secrets).
9. REQ-282-9: Newly created tables become targets for `dbt/governance` and `consents/apply` in the same or a following call; within one sync run, creation precedes the governance/documentation update so a single call suffices.
10. REQ-282-10: Table creation is atomic per table; a failure on one item is recorded as a skip with reason `CreateFailed` and does not abort the run (consistent with existing per-model handling).

## Acceptance criteria
1. AC-1: Empty governance DB, one SQL data source, manifest with 2 models + 1 source on that `database`: sync returns 3 `CreatedTables`, 0 skipped, all `IsActive=false` (default config).
2. AC-2: With `Dbt:ActivateNewTables=true` the created entries are active.
3. AC-3: Model with unmapped `database` appears in `SkippedItems` with `NoDataSourceMapping`; no entry created.
4. AC-4: Second identical sync creates nothing; existing entry fields (`IsActive`, sensitivity) unchanged.
5. AC-5: Sequence sync, `dbt/governance`, consent manifest apply yields tables with declared masks and consents, queryable once activated, with no direct governance-table writes (integration test on PostgreSQL via Testcontainers).
6. AC-6: Non-admin caller gets 403 and nothing is created.
7. AC-7: Invalid identifier (e.g. quote or semicolon in table name) is skipped with `InvalidIdentifier`.
8. AC-8: `dryRun` writes nothing and reports the same lists.
9. AC-9: Audit event contains created table names; no secrets.

## Work packages
- WP-1: Ingestion service creation path, options, result DTO, endpoint response, unit tests (AC-1..4, 6..8).
- WP-2: PostgreSQL integration test for the full sequence (AC-5, AC-9) and update of `docs/extensions/dbt-integration-guide.md` only if the owner requests docs (no docs phase).

## Open questions (recommended default, non-blocking)
1. OQ-1: Admin endpoint for live schema introspection (option 2)? Default: deferred to a separate issue once an introspection service exists.
2. OQ-2: `Dbt:CreateMissingTables` default `true` or `false`? Default: `true` (no production; entries are inactive anyway).
3. OQ-3: Mapping `database` to data source when the dbt `database` differs from the connection name? Default: exact name match plus optional `Dbt:DatabaseMappings` dictionary (dbt database to connection name), skipped if unresolved.
4. OQ-4: Should masks/consents be auto-proposed for new tables? Default: no; existing proposal flow handles it.
