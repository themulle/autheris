# WP-SIMPL-54 Golden queries maintained by data owners (dbt meta, governance store)

Parent: [plan](../2026-11-05-plan-project-simplification.md#4-work-packages) | Issue: [#580](https://github.com/themulle/autheris/issues/580) | Related: [WP-SIMPL-28](WP-SIMPL-28-module-dbt-openmetadata.md), [WP-SIMPL-41](WP-SIMPL-41-authorized-data-discovery.md), [WP-SIMPL-52](WP-SIMPL-52-governance-data-model.md) (catalog text of masked columns)

Status: Draft (Phase 2, 2026-11-08); scheduled after the feature freeze (Stage 3 exit, Q26 default); Phase 3 review pending.

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

## 8. Decisions (defaults taken; owner may change)
- D-54a dbt meta only (issue option 1); a per-dataset catalog REST API (option 2) is not built (Q27).
- D-54b Store table in the governance DB; sync replaces the set per table and source.

## 9. Estimate
PR 1 1.5 pd, PR 2 1.5 pd, PR 3 1.5 pd: about 4.5 pd.
