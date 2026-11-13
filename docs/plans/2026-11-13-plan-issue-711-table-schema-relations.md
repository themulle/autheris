# PLAN-OI-711: get_table_schema ignores include_relations

- Date: 2026-11-13 · Base: `origin/main` @ `a67797a6` · Issue: [#711](https://github.com/themulle/autheris/issues/711)
- Origin plan: [`00-master-plan-overview.md`](00-master-plan-overview.md) (plan registry).
- Scope: part A only (honour `include_relations`). Part B of the issue (a GraphQL field per table and column, the caller's column state) adds fields and is not part of this plan; it needs a `freeze-exception` and is best built with or after WP-SIMPL-41 PR 3.
- Freeze (PRD 5.1): bug fix; the parameter `include_relations` and the field `relations` already exist. No new parameter, field, endpoint, option or switch. No `freeze-exception` needed.
- Size: about 60 net lines in `src/`.

## Phase 1: Requirement

- **Problem:** the input schema of `get_table_schema` offers `include_relations` "(default true)" (`McpDatasetTools.cs`), but the handler always returns `relations: []` (`McpToolExecutionHandler.cs`, comment MCP-05: "none are available on this path"). A client cannot use the batch tool to learn join paths.
- **REQ-711-1:** without `include_relations` or with `true`, each table lists its declared relations as `{ targetTable, fromColumn, toColumn }`; `targetTable` is the `list_datasets` id (`domain.schema.table`).
- **REQ-711-2:** `include_relations: false` lists no relations and reads no relation target.
- **REQ-711-3 (MCP-05):** relations are never guessed from column names; only relations declared in the catalog (the relations of the catalog GraphQL schema) are listed.
- **Anti-patterns rejected:** reusing the join paths of the search index (they are partly derived from `*_id` column names); listing a relation to a table the caller cannot see (leaks a hidden table name).

## Phase 2: Design

- Source: `IGraphQlCatalogMap.GetTableAsync(table).Relations`, the same declared relations as `describe_dataset` (`graphQl.relations`). Both directions are present; the own join columns come first (issue #392).
- `McpToolExecutionHandler` takes the map as an optional constructor dependency (registered as a singleton in `AddAutherisMcp`). Without a map, or for a table outside the GraphQL schema, the list stays empty.
- Visibility, as in `describe_dataset` (`McpRelationColumnProjector`): a relation is listed only when the target is visible to the caller (`ICatalogDiscoveryService.GetDatasetDetailAsync` answers non-null), the own join column is among the caller's listed columns and the target join column is among the target's listed columns. A Summary row lists no columns and therefore no relations. Lookups are cached per call, so a target that is also requested is read once.
- `McpTableRelation` carries one column pair. A composite-key relation is not listed, rather than being split into misleading single-column relations. Representing composite keys would need a new field (part B territory).
- Argument parsing: only a JSON `false` turns relations off; anything else keeps the documented default `true`.

## Phase 3: Security review

| Check | Result |
|---|---|
| Hidden table names | A relation target is listed only after the caller's own visibility check for that target (same path as a requested table). An invisible target yields no entry; the response does not differ from a table without that relation. |
| Hidden columns | Join columns are disclosed only when they are listed for the caller on both sides (all-or-nothing per relation, as I392). |
| MCP-05 | Relations come only from the declared catalog relations; column-name guessing stays excluded (test `RelationsAreNeverGuessedFromColumnNames`). |
| Authorization of reads | No data is read; target lookups are catalog reads under the caller's context, like `describe_dataset`. The guardrail's ABAC targets of the call are unchanged (the requested datasets). |
| Fail-closed | A missing map or mapping yields no relations; a lookup exception propagates like any failure of a requested table. |
| Tests | `McpGetTableSchemaRelationsTests`; security cases with `[Trait("Category","Security")]`: invisible target, join column not listed on either side, no guessing. |

## Phase 4 and 5

- TDD: red commit with the tests and a constructor stub (2 of 8 cases failed: no relations listed), then the implementation.
- Delivery: UNDER REVIEW in PR #725.
