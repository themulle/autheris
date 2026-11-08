# WP-SIMPL-53 MCP row cap and truthful catalog hints for query clients

Parent: [plan](../2026-11-05-plan-project-simplification.md#4-work-packages) | Issues: [#578](https://github.com/themulle/autheris/issues/578), [#579](https://github.com/themulle/autheris/issues/579), [#572](https://github.com/themulle/autheris/issues/572), [#573](https://github.com/themulle/autheris/issues/573) | Related: [WP-SIMPL-41](WP-SIMPL-41-authorized-data-discovery.md), [WP-SIMPL-44](WP-SIMPL-44-no-source-metadata-reads.md), [WP-SIMPL-51](WP-SIMPL-51-websql-client-contract.md)

Status: Draft (Phase 2, 2026-11-08); Phase 3 review done for PR 1 and PR 2 (2026-11-08), PR 3-5 pending.

## 1. Goal and outcome
Query clients (mostly LLM agents) get only what the operator allows and only hints that are true: `Mcp:MaxResultRows` caps every row-reading MCP tool (#578), catalog hints name only enabled tools and routes (#579), datasets carry a coarse size class from ingested metadata (#572), and aggregations and WebSQL statements without a usable index are priced like unindexed root lists and say so (#573).

## 2. Preconditions and dependencies
- PR 1, PR 2: none (bug fixes on core paths, allowed under the freeze, PRD 5.1). PR 2 rebases on WP-SIMPL-16 if the Arrow branch of `DatasetAccess` is already deleted.
- PR 3: after the WP-41 discovery DTO PR (same output types). Size data only from ingestion (OD-25, WP-44 "Do NOT compute statistics, row counts or samples from sources").
- PR 4: after WP-44 (index usability on declared indexes only). PR 5: after WP-51 PR 1 (`SqlStatementCostEstimator`) and PR 4.
- PR 3-5 add response fields: carried under `freeze-exception` with the issue link (Q23, default taken).

## 3. Current state (verified on `origin/main` 13411f71, 2026-11-08)
- `McpOptions.MaxResultRows` (`Domain/Options/Protocols/McpOptions.cs:10`, `[Range(1, 10000)]`, default 100) has no consumer. Own limits: `query_dataset` `limit` max 1000 (`McpDatasetTools.cs:283`), `call_procedure` `max_rows` default 100 clamp 1000 (`McpToolExecutionHandler.cs:533`), `sample_rows` `MaxSampleRows = 20` (`McpDatasetCatalog.cs:32`), `query_graphql` only `first` and `GraphQL:MaxResponseRows`. Dispatch: `GatewayMcpQueryExecutor.cs:313`.
- Hints: `McpDatasetCatalog.Guidance` (`:36`) always names `query_graphql`; `Endpoints()` (`:1087`) always adds GraphQL and adds WebSQL plus Trino under `WebSql.Enabled` only; `DatasetAccess()` always adds "MCP sample_rows" (`:1075`); the `list_datasets` tool description (`McpDatasetTools.cs:193`) and the server instructions (`:38`) name `query_*` and `sample_rows` unconditionally. `IReadPathSwitches.IsToolEnabled` / `IsGroupEnabled` (`Application/Mcp/Interfaces/IReadPathSwitches.cs:20-23`) already answer the effective switches.
- Size: `TableMetadata.EstimatedRowCount` / `ExpectedRows` (`TableModels.cs:419,422`) merged only from ingestion (`CatalogGovernanceRatchet.cs:153`); no catalog output carries them.
- Cost: `QueryCostAnalyzerRule.cs:631-646` applies `UnindexedRootListSurchargeMultiplier` (`QueryCostOptions.cs:43`) to paged root lists only via `IndexUsabilityEvaluator.HasUsableIndex` (`IndexUsabilityEvaluator.cs:36`); aggregations are priced by shape only.

## 4. Steps
1. **PR 1 Row cap (#578)**: new `McpRowCap` (`Application/Mcp/Services/`, static) with `Effective(int? requested, int optionCap) => Math.Min(requested ?? optionCap, optionCap)`. Apply to `query_dataset` `limit`, `call_procedure` `max_rows`, `sample_rows` `count` (also capped by `MaxSampleRows`), `query_sql` row limit, and per root list of `query_graphql` results (post-execution trim in the MCP executor; GraphQL HTTP is untouched). Result carries `truncated: true, rowCap: n` when rows were cut. Tool input schemas show `maximum` = effective cap. Correct `docs/configuration-guide.md` (option text) in the same PR. Option stays (not deleted by WP-SIMPL-06).
2. **PR 2 Truthful hints (#579)**: `McpDatasetCatalog` takes `IReadPathSwitches`; new private `AccessHints` builder used by `Guidance`, `Endpoints()`, `DatasetAccess()`, the `list_datasets` description and the server instructions. A tool is named only when `IsToolEnabled(name)`; GraphQL route only when the GraphQL endpoint is mapped; WebSQL and Trino each by their own route switch; a hint with nothing enabled is dropped, never replaced by a disabled one. Description strings become per-instance (built once at registration from the switches, which are configuration-only).
3. **PR 3 Size class (#572)**: `McpDatasetSize(string SizeClass, string Source, DateTimeOffset? AsOf)` on `list_datasets` items, `describe_dataset` and the catalog REST detail. Classes: `tiny` < 1e3, `small` < 1e5, `medium` < 1e7, `large` < 1e9, `huge` >= 1e9, `unknown`. Source: `DBT_META`, `DECLARED` (`ExpectedRows`), `UNKNOWN`; `AsOf` = last ingestion time of the table metadata or null. Exact counts are never emitted (Q24). Only for datasets visible to the caller (WP-41 visibility).
4. **PR 4 Aggregation pricing and hint, GraphQL (#573)**: `QueryCostAnalyzerRule` aggregation branch calls `IndexUsabilityEvaluator.HasUsableIndex(tableMeta, visibleColumns, groupBy-as-order, filterColumns)`; no usable index -> multiply by `UnindexedRootListSurchargeMultiplier` (no new option). Response `extensions.autheris.indexHints: [{root, reason: "NO_USABLE_INDEX", helpfulIndexColumns[]}]` for root lists and aggregations; columns listed only when every one is visible (not Deny) to the caller, otherwise the entry has no columns.
5. **PR 5 WebSQL hint (#573)**: `SqlStatementCostEstimator` applies the same surcharge per unindexed table scan with a filter or GROUP BY; WebSQL response field `indexHints` (same shape, table id instead of root). Statement text is never echoed.

## 5. Tests (red first; S = `[Trait("Category","Security")]`)
- `Mcp/McpRowCapTests`: `Each_row_tool_returns_at_most_option_rows` (Theory over the five tools, `MaxResultRows = 20`), `Larger_client_limit_does_not_raise_cap` (S), `Smaller_client_limit_is_kept`, `Truncated_flag_set_only_when_rows_cut`, `GraphQl_cap_applies_per_root_list`, `Option_is_consumed` (architecture: `McpOptions.MaxResultRows` has a reader).
- `Mcp/McpAccessHintTests`: `Query_group_off_hides_query_tools_in_all_hints`, `DisabledTools_entry_never_recommended`, `WebSql_off_drops_WebSql_and_Trino_endpoints`, `Nothing_enabled_drops_hint`, `List_datasets_description_follows_switches`.
- `Catalog/DatasetSizeClassTests`: `Class_boundaries` (Theory), `Unknown_without_ingested_count`, `Exact_count_never_serialized` (S), `Invisible_dataset_has_no_size` (S), `No_source_query_issued` (S, fake connection factory asserts zero calls).
- `GraphQL/AggregationIndexPricingTests`: `Aggregation_without_usable_index_gets_surcharge`, `Aggregation_with_leading_key_filter_not_surcharged`, `Index_hint_omits_columns_when_any_is_denied` (S), `Hint_absent_when_index_usable`.
- `Sql/WebSqlIndexHintTests`: `Unindexed_group_by_surcharged_and_hinted`, `Hint_never_contains_statement_text` (S).

## 6. PR slicing (<= 400 net added src lines each)
PR 1 ~160, PR 2 ~170, PR 3 ~180, PR 4 ~200, PR 5 ~150. PR 1 and PR 2 any time, in either order.

## 7. Threat notes for Phase 3
Row cap is data minimisation towards model providers: a client value can only lower it. Size class leaks table cardinality including rows hidden by row policies; coarse classes only, visible datasets only (Q24). Index hints must not reveal Deny columns or index names of hidden objects. Hint builder reads configuration only, never request data.

## 8. Decisions (defaults taken; owner may change)
- D-53a `Mcp:MaxResultRows` is enforced, not deleted (issue option 1).
- D-53b Size class instead of exact count; buckets as in step 3.
- D-53c Aggregations without a usable index reuse `UnindexedRootListSurchargeMultiplier`; behaviour change accepted (no production yet).

## 9. Estimate
PR 1 1.5 pd, PR 2 1.5 pd, PR 3 1.5 pd, PR 4 1.5 pd, PR 5 1 pd: about 7 pd.

## Phase 3 binding conditions (PR 1, PR 2)

Verdict: APPROVED WITH CONDITIONS (PR 1 #578, PR 2 #579). PR 3-5 not yet reviewed. Current-state references in section 3 were spot-checked and hold (`McpOptions.MaxResultRows` unread, `IReadPathSwitches`, `DisabledTools` validator).

### PR 1: row cap
- SEC-53-01 The cap is the minimum of the option value and the client value; a client value can never raise it. Applies to all five tools, including `query_sql` (cap applied after any client `LIMIT`, by trimming or by wrapping/limiting in the executor, never by trusting statement text) and `query_graphql`.
- SEC-53-02 Fail closed on bad client limits: null uses the cap; zero, negative, non-numeric, NaN or overflowing values are rejected with a validation error (or clamped to the cap), never treated as "unlimited" and never as a larger value. Use `long`/checked parsing, no `int` overflow wrap.
- SEC-53-03 The effective cap is `Math.Clamp(option, 1, 10000)` re-read per call from `IOptionsMonitor`/options snapshot; a misconfigured value (<= 0) falls back to the default 100, never to unbounded. `sample_rows` also respects `MaxSampleRows`; the lower value wins.
- SEC-53-04 GraphQL: the trim applies to every list in the response tree (root lists and nested lists, aliases, fragments, and aggregation/edge/node lists), not only root lists. Trim happens before serialization into the tool result and before audit/log payloads; it must also bound the serialized size (a trimmed list of huge rows is still bounded by existing byte limits, which stay).
- SEC-53-05 `truncated`/`rowCap` leak nothing about hidden rows: `truncated` is true only if the already policy-filtered result exceeded the cap, with no total count, no "N more rows", and no probing of an extra row past `cap+1` beyond what the engine already returned. `rowCap` equals the public effective cap only.
- SEC-53-06 The cap is applied after row policies and column masking; it must not change which rows are returned first (same ordering as without cap), and must not bypass guardrail/governance checks that run on the full request.
- SEC-53-07 Tool input schema `maximum` is informational only; enforcement is server-side regardless of schema (schema is client-controlled input).
- SEC-53-08 Logs/audit record the cap and the returned count only, never row values.

### PR 2: truthful hints
- SEC-53-09 `AccessHints` reads `IReadPathSwitches` and options only; no request, principal, tenant or dataset data enters hint text. Descriptions are built per instance from configuration; no static/shared mutable cache keyed across instances or tenants.
- SEC-53-10 A tool, group, GraphQL route, WebSQL or Trino endpoint is named only if its switch is on at build time. A `DisabledTools` entry, `Mcp:Enabled=false`, an `AllowedOperations` exclusion, or a disabled group removes it from `Guidance`, `Endpoints()`, `DatasetAccess()`, the `list_datasets` description and the server instructions. Unknown or null switch state means "disabled" (fail closed).
- SEC-53-11 Hints are never replaced by an alternative route that is not itself enabled; an empty hint list is dropped, not filled with a default string.
- SEC-53-12 Hints must not reveal the existence of a disabled feature (no "X is disabled" text, no route URLs of disabled endpoints). Enabled route URLs come from configuration, not from the `Host`/`X-Forwarded-*` request headers.
- SEC-53-13 If switches can change at runtime, descriptions are rebuilt on change or the builder is documented as startup-only; a stale description naming a now-disabled tool is a defect. The execution path still enforces `IsToolEnabled` independently of hints (hints never grant access).

### Mandatory tests (`[Trait("Category","Security")]`)
- `McpRowCapTests.Larger_client_limit_does_not_raise_cap` (all five tools, Theory)
- `McpRowCapTests.Invalid_client_limit_is_rejected_or_clamped` (0, -1, int.MaxValue, long overflow, non-numeric)
- `McpRowCapTests.QuerySql_client_LIMIT_above_cap_is_trimmed`
- `McpRowCapTests.GraphQl_nested_list_and_alias_trimmed_to_cap`
- `McpRowCapTests.Truncated_flag_carries_no_hidden_row_count`
- `McpRowCapTests.Cap_applies_after_row_policy` (hidden rows do not consume or reveal cap)
- `McpRowCapTests.Misconfigured_option_falls_back_to_default_not_unbounded`
- `McpAccessHintTests.Disabled_tool_never_named_in_any_hint_surface` (Theory over every surface and every switch)
- `McpAccessHintTests.Hints_do_not_depend_on_request_or_principal`
- `McpAccessHintTests.Disabled_feature_not_mentioned_by_name_or_route`
- `McpAccessHintTests.Hint_naming_a_disabled_tool_cannot_enable_it` (execution still returns not-enabled)

### Owner questions (defaults)
- Q1 `query_sql` cap by trimming rows in the MCP executor (default) vs rewriting SQL; trimming is safer and engine-independent.
- Q2 Invalid client limit rejects with a validation error (default) vs silent clamp.
