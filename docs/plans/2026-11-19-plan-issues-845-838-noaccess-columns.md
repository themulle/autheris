# Security review: NoAccess columns on MCP get_table_schema and GraphQL discovery (#845, #838)

Verdict: APPROVED WITH CONDITIONS. Approach mirrors #813/#817 (search oracle fix).

## Problem
- `get_table_schema` (McpToolExecutionHandler) reads `CatalogDiscoveryProjection.ToDatasetDetail`. For an ungranted table every column is listed as Deny (kept for the REST request-access flow). For a granted table NoAccess columns are dropped. Name, type, description, IsSensitive and GraphQlField of denied columns leak. `describe_dataset` already filters them.
- GraphQL `ToTableMetadataDto` (Query.Catalog.cs, used by discovery list and `catalogObject`) puts `obj.Columns` and `ColumnNames` unfiltered, with state.
- Owner rule: a NoAccess column is never named on MCP `get_table_schema` or GraphQL catalog discovery.

## Surface inventory
| Surface | Names NoAccess column? | In scope |
|---|---|---|
| MCP `get_table_schema` | yes (Deny rows) | YES |
| MCP `get_my_permissions` (handler ~L442, same `GetDatasetDetailAsync`, emits `masking` per column) | yes, same source | YES (same projection) |
| MCP `describe_dataset` | no (filtered, L313) | reference; must stay equal |
| MCP `search_catalog` relevantColumns (Projection L83) | no (filtered) | verify only |
| MCP `find_relevant_tables` | uses ToSearchHit/relevantColumns | verify only |
| MCP `list_datasets` `ColumnCount` (`obj.Columns.Count`, Discovery.cs L167) | count includes NoAccess: side channel | YES |
| MCP legacy `ColumnCount` (Discovery.cs L189, McpDatasetCatalog.cs L352/371) | L189/L371 exclude Deny; L352 uses `t.Columns.Count` | YES (L352 unless proven unreachable) |
| MCP `analyze_query` hints | not proven; verify no column list from unfiltered metadata | verify, fix if it names one |
| GraphQL discovery list, `catalog`, `catalogObject` (`ToTableMetadataDto`, L213/L323/L412) | yes | YES |
| GraphQL legacy `ToCatalogDto` path | verify same filtering | YES if it lists Deny |
| Keys, indexes, required filters | handled by #826 | out; regression-check only |
| Error messages | must not echo requested column names that are NoAccess | verify only |
| REST `GET /api/v1/catalog/datasets/{id}` | lists ungranted columns as Deny | OUT (request-access flow; unchanged) |

## Binding conditions
- SEC-845-01 One shared projection (single method in Application, e.g. `DiscoveryColumnVisibility.VisibleColumns(obj)`: returns columns with `State != NoAccess`) is the only source of column lists for `describe_dataset`, `get_table_schema`, `get_my_permissions` and GraphQL. Duplicated `.Where(State != NoAccess)` filters in channels are replaced by it. REST keeps `ToDatasetDetail` and must not call it.
- SEC-845-02 MCP `get_table_schema` must not use REST-shaped `ToDatasetDetail` output unfiltered. Either a channel-specific detail call or a post-filter via SEC-845-01. An ungranted table lists zero columns (not Deny rows); request-access info comes only from `accessStatus`.
- SEC-845-03 GraphQL: `Columns`, `ColumnNames` and any derived field (counts, PK/index/filter lists, GraphQlField names) are built from the shared visible set only. Hidden-text handling stays applied on top.
- SEC-845-04 Fail-closed: if column levels or `obj.Columns` cannot be resolved (null, exception, level missing), list zero columns and keep table-level behaviour; never fall back to all columns. An unknown state is treated as NoAccess.
- SEC-845-05 No count side channel: every column count (`list_datasets` ColumnCount, summaries, GraphQL counts, truncation/total fields) counts visible columns only. The legacy path at McpDatasetCatalog.cs L352 must use the same rule. A table with all columns denied reports 0.
- SEC-845-06 No-consent caller: sees no column names on any channel; unknown table and ungranted table answer identically for columns (no existence or column oracle).
- SEC-845-07 Admin view: administrators are filtered by the same projection. An admin sees a column iff its state is not NoAccess for that caller; admin bypass must not be added in the projection. Admin catalog-management tools are separate and unchanged.
- SEC-845-08 Restricted token scope (`Agent.Read`): column state is computed for the effective principal including scope. A column hidden by scope is NoAccess and omitted on both channels. The scope must never widen the visible set.
- SEC-845-09 No column name of a NoAccess column appears in errors, logs or audit payloads produced by these paths (including `get_table_schema` with unknown or denied column parameters).
- SEC-845-10 Counter-check: a column with state Clear or Masked remains listed with `access` "clear" or "mask" (MCP) and its state (GraphQL). No regression of #826 keys/indexes/filters.
- SEC-845-11 No `WP-`, `SEC-` or `OD-` identifiers in comments under `src/Autheris.Api` or `src/Autheris.Application` (Phase 4 reminder).

## Mandatory tests (all `[Trait("Category","Security")]`)
Local unit (tests/Autheris.Tests.Unit):
- `GetTableSchema_NoAccessColumn_IsNotListed` (granted table, restricted scope Agent.Read)
- `GetTableSchema_UngrantedTable_ListsNoColumns`
- `GetTableSchema_And_DescribeDataset_ReturnSameColumnSet`
- `GraphQlDiscovery_DeniedColumn_IsNotListed` (Columns and ColumnNames)
- `GraphQlCatalogObject_DeniedColumn_IsNotListed`
- `GetMyPermissions_NoAccessColumn_IsNotListed`
- `ListDatasets_ColumnCount_ExcludesNoAccessColumns` (incl. all-denied gives 0)
- `DiscoveryColumnVisibility_UnresolvedColumns_FailsClosed`
- `DiscoveryColumnVisibility_AdminView_StillFiltersNoAccess`
- `GetTableSchema_GrantedColumn_ListedWithClearOrMaskAccess` (counter-check)
- `GraphQlDiscovery_GrantedColumn_StillListedWithState` (counter-check)
- `RestDetail_UngrantedTable_StillListsDenyColumns` (guards the unchanged REST contract)
Integration (GitHub Actions, containers/live DB), extend `CatalogChannelParityTests`:
- `Parity_DeniedColumn_NotNamedOnRestFilteredMcpGetTableSchemaDescribeDatasetAndGraphQl`
- `Parity_AgentReadScope_ColumnSetEqualAcrossMcpTools`
- `Parity_ConsentlessCaller_NoColumnNamesOnMcpOrGraphQl`
- `Parity_ColumnCount_ListDatasetsEqualsDescribeDatasetColumns`

## Files to touch
- src/Autheris.Application/Catalog/Discovery/ (new shared visibility helper; CatalogDiscoveryProjection.cs stays REST-shaped)
- src/Autheris.Application/Mcp/Services/McpToolExecutionHandler.cs (get_table_schema, get_my_permissions)
- src/Autheris.Application/Mcp/Services/McpDatasetCatalog.Discovery.cs, McpDatasetCatalog.cs (counts, describe filter via helper)
- src/Autheris.GraphQL/Types/Query.Catalog.cs (ToTableMetadataDto, ToCatalogDto)
- tests/Autheris.Tests.Unit (new tests), tests/Autheris.Tests.Integration/CatalogChannelParityTests.cs
