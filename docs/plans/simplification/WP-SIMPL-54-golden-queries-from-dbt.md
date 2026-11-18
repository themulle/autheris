# WP-SIMPL-54 Golden queries maintained by data owners (dbt meta, governance store)

Parent: [plan](../2026-11-05-plan-project-simplification.md#4-work-packages) | Issue: [#580](https://github.com/themulle/autheris/issues/580) | Related: [WP-SIMPL-28](WP-SIMPL-28-module-dbt-openmetadata.md), [WP-SIMPL-41](WP-SIMPL-41-authorized-data-discovery.md), [WP-SIMPL-52](WP-SIMPL-52-governance-data-model.md) (catalog text of masked columns)

Status: Ready for implementation (after Stage 3 feature-freeze exit, Q26 owner-confirmed 2026-11-08); Phase 3 security review: APPROVED WITH CONDITIONS (`SEC-54-01..07`, 2026-11-14).

## 1. Goal and outcome
Data owners maintain example queries per dataset in dbt `meta.golden_queries`; a dbt sync stores them in the governance store, and `describe_dataset.examples` and `get_golden_queries` serve them without a gateway restart. Removing them in dbt removes them on the next sync. `Gateway:GoldenQueries:InitialQueries` stays for demos and tests.

## 2. Preconditions and dependencies
Stage 3 exit (new table and ingestion path are a feature, PRD 5.1); WP-SIMPL-20 (unified repository), WP-SIMPL-28 (dbt module: the import lives in the module, not in core), WP-SIMPL-41 (visibility), WP-SIMPL-52 PR 4b (examples touching columns with hidden catalog text). Not under `freeze-exception`.

## 3. Current state (verified on `origin/main` 13411f71, 2026-11-08)
`GoldenQueryService` (`Application/Mcp/Services/GoldenQueryService.cs`) keeps an in-memory `ConcurrentDictionary` (`:17`, capacity 5000, text <= 65536 chars) filled only from `GoldenQueryOptions.InitialQueries` (`Domain/Options/Protocols/GraphQlOptions.cs:124-128`); `IGoldenQueryService.GetGoldenQueriesAsync(domain, table, visible, ct)` already filters by a visibility predicate before the cap. Model: `GoldenQuery(Id, Domain, TableName, Title, Description, QueryText, VariablesJson, Tags, CreatedAt)`. dbt `meta.indexes` import precedent: #459 / WP-SIMPL-44.

## 4. Steps
1. **PR 1 Store**: forward migration step `golden_queries(id UUID, tenant_id, table_id TEXT, source VARCHAR(16) 'DBT'|'CONFIG', source_key VARCHAR(256), title VARCHAR(256), description VARCHAR(2048), query_text TEXT, variables_json TEXT NULL, content_hash CHAR(64), updated_at TIMESTAMPTZ)`, unique `(tenant_id, table_id, source, source_key)`; PG and SQL Server, shape JSON; repository `IGoldenQueryRepository.ReplaceForTableAsync(tenant, tableId, source, entries)` (one transaction: replace the whole set per table and source).
2. **PR 2 dbt import (module)**: parse `meta.golden_queries: [{title, description, query, variables}]` on sources and models in the dbt module ingestion; validation per entry: non-empty query <= 16 KiB, title <= 256, description <= 2048, parses as GraphQL, every root field belongs to the owning dataset, variables JSON object <= 4 KiB, at most 20 entries per dataset. Invalid entries are skipped with a warning carrying dataset id, entry index and a reason code only (no values, no query text). Text runs through the shared catalog-text sanitiser (#500). An empty or missing list deletes the dataset's DBT entries.
3. **PR 3 Read path**: `GoldenQueryService` reads store entries plus `InitialQueries` (config entries win on equal id; store wins on equal source key). Visibility per caller: an entry is returned only if every dataset it references is visible and no selected column is Deny; WP-52 hidden catalog text drops the entry for that caller. Cache keyed by catalog epoch.

## 5. Tests (red first; S = `[Trait("Category","Security")]`)
- `Dbt/GoldenQueryMetaImportTests`: `Valid_entries_imported`, `Removed_in_dbt_removed_after_sync`, `Empty_query_rejected_with_reason_code`, `Query_over_other_dataset_rejected` (S), `Oversized_text_rejected`, `Warning_contains_no_values` (S), `More_than_20_entries_truncated_with_warning`.
- `Mcp/GoldenQueryStoreReadTests`: `Describe_dataset_examples_after_sync_without_restart`, `Entry_with_invisible_relation_hidden` (S), `Entry_selecting_denied_column_hidden` (S), `Config_entries_still_served`.
- Integration (PG, SQL Server): `GoldenQueryRepositoryTests.Replace_is_atomic_per_table_and_source`, `Tenant_isolation` (S); migration shape test.

## 6. PR slicing
PR 1 ~280 src (+SQL), PR 2 ~260, PR 3 ~200.

## 7. Threat notes for Phase 3
Catalog text injection towards LLM clients (#500 sanitiser); examples as a disclosure channel for hidden relations or columns (per-caller filter); cross-tenant leakage (tenant column, tenant-scoped reads); ingestion flooding (per-dataset cap, size limits).

## 8. Decisions (owner-confirmed 2026-11-08: Q26, Q27)
- D-54a dbt meta only (issue option 1); a per-dataset catalog REST API (option 2) is not built (Q27).
- D-54b Store table in the governance DB; sync replaces the set per table and source.

## 9. Estimate
PR 1 1.5 pd, PR 2 1.5 pd, PR 3 1.5 pd: about 4.5 pd.

## 10. Phase 3 Security Review (2026-11-14, `csharp-security-expert`)
**Verdict:** `APPROVED WITH CONDITIONS` (`SEC-54-01..07`)

### Binding Security Conditions
- **SEC-54-01 (Tenant-Scoped Persistence & Read Isolation):** Every `golden_queries` row is strictly bound to `(tenant_id, table_id, source, source_key)`. `IGoldenQueryRepository.ReplaceForTableAsync` and read queries MUST bind `tenant_id` in SQL (`WHERE tenant_id = @tenantId`) and reject empty/default `TenantId`. Cross-tenant leakage of golden queries is pinned by a `[Trait("Category","Security")]` integration test.
- **SEC-54-02 (Caller-Level Visibility & Column-Deny Filtering Before Cap):** `GoldenQueryService.GetGoldenQueriesAsync` MUST evaluate visibility per caller via `IDiscoveryService` / `IAccessDecisionPoint` before applying any result limit: an entry is returned ONLY when (a) the owning dataset and every referenced relation dataset are discoverable for the caller (`!DiscoveryHidden` and in tenant scope), (b) no column selected or filtered in the parsed GraphQL query has `ColumnAccessLevel.Deny` or `DiscoveryHidden = true` for the caller, and (c) under `WP-SIMPL-52 PR 4b`, any column whose catalog text is hidden for the caller (`Mask` with hidden text) drops the example query for that caller.
- **SEC-54-03 (Strict GraphQL AST Validation & Root-Field Ownership at Ingestion):** During dbt `meta.golden_queries` import, each query text MUST be parsed by the GraphQL AST parser with bounded depth/complexity before storage; only `query` operations (never `mutation` or `subscription`) are accepted, and every root selection field MUST belong to the owning dataset (`Query_over_other_dataset_rejected` `[S]`).
- **SEC-54-04 (Untrusted Catalog Text Sanitization & MCP Wrapping):** `title`, `description`, `query_text`, and `variables_json` string values originate from untrusted dbt metadata (#500). They MUST pass through the shared catalog-text rune sanitizer (`UntrustedText` / `McpCatalogText`) at ingestion and be emitted to MCP clients inside untrusted-content wrappers with `UntrustedContentNotice`, never interpolated into system/tool guidance prompts.
- **SEC-54-05 (Ingestion Resource Caps & Atomic Replacement):** Enforce hard caps per entry (`title <= 256`, `description <= 2048`, `query_text <= 16 KiB`, `variables_json <= 4 KiB` flat JSON object with no nested depth > 3, max `20` entries per dataset). `ReplaceForTableAsync` executes inside one transaction using `GovernanceTable.GoldenQueries` and `AddBound` parameters.
- **SEC-54-06 (Zero-Value Diagnostic Logging):** Skipped or invalid `meta.golden_queries` entries log only `(datasetId, entryIndex, reasonCode)` and NEVER log raw `query`, `variables`, `title`, or `description` payloads (`Warning_contains_no_values` `[S]`).
- **SEC-54-07 (Cache Key Epoch & Tenant Binding):** Any in-memory cache in `GoldenQueryService` MUST include `(TenantId, CatalogVersion, CallerDecisionFingerprint)` or evaluate caller column/visibility filters after reading the tenant+version-keyed parsed entry set so one caller's cached view never serves another caller.

## 11. Implementation notes (2026-11-18, PR 1..3 combined on `feat/simpl-54-golden-queries`)
- Plan drift: the brief reserved `0025_golden_queries`; the catalogue on `main` ended at `0024_drop_superseded_governance_columns` after the merge of the column-drop work, so the step is `0025_golden_queries` (gap-free runner; renumbered from 0023 on 2026-11-18). The WP-SIMPL-49 branch also adds a step; whichever merges second renumbers.
- Store: `id` is `VARCHAR(36)`/`NVARCHAR(36)` like the other governance tables; `table_id` is `TEXT` (PostgreSQL) and `NVARCHAR(128)` (SQL Server). `ReplaceForTableAsync` bumps the catalog content version in the same transaction so cached readers refresh without a restart.
- dbt import: unchanged declarations are not rewritten (content hash compare); a declaration that is not a list leaves the stored entries untouched; only a missing or empty list removes them. Root-field ownership accepts the table name and the catalog GraphQL name (`domain_schema_table`, `schema_table`), optionally with the `Connection` suffix.
- Read path: the store entry set is cached per `(TenantId, CatalogVersion)` and is caller independent; caller visibility, relation targets and hidden column identifiers are evaluated per call before the result cap (SEC-54-07, second option). `SemanticMcpCompiler` still serves configured entries only.
- Configured (administrator-authored) entries keep the pre-existing identifier match and owner visibility; their relation targets are not visibility-checked (follow-up candidate). Stored entries are always verified: a stored entry whose owner has no GraphQL mapping is served only as a flat selection of the owner's own fields, and entries of datasets beyond the 500-dataset closure cap are dropped. `...Connection` root fields are rejected at import (`QUERY_CONNECTION_ROOT_NOT_ALLOWED`).
