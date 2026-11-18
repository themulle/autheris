# PLAN-OI-805/807: describe_dataset and the REST catalog detail drop declared keys, indexes and required filters

- Date: 2026-11-17 · Base: `main` @ `595770e4` · Issues: [#805](https://github.com/themulle/autheris/issues/805),
  [#807](https://github.com/themulle/autheris/issues/807)
- Origin plan: [`00-master-plan-overview.md`](00-master-plan-overview.md) (entry maintained by the orchestrator).
- Branch: `fix/805-807-describe-keys-filters`.

## Phase 1: Requirement

- **Problem (#805):** declared primary keys (with order and direction) and declared indexes are stored
  (`TABLE_PRIMARY_KEY_COLUMNS`, `TABLE_INDEXES`, `TABLE_INDEX_COLUMNS`) and used by the read path, but MCP
  `describe_dataset` reports `primaryKey: false` for every column, no `primaryKeyOrder` and no `indexes`; REST
  `GET /api/v1/catalog/datasets/{id}` reports `primaryKeys: []`, `indexes: []`, `primaryKeyDirections: null`.
- **Problem (#807):** `describe_dataset` no longer reports `requiredFilterColumns` / `requiredFilterColumnGroups`,
  although the required filter is enforced, so agents learn it only from a failed query.
- **REQ-805-1:** `describe_dataset` (full view) reports `primaryKey` per column, `primaryKeyOrder` (column and
  `ASC`/`DESC`, in key order) and `indexes` (columns in index order, `directions` per column when any column is
  descending, the 1.7.1 wire contract) for a key/index whose columns are all Clear for the caller. `include: ["keys",
  "indexes"]` keeps them.
- **REQ-805-2:** the REST detail reports `primaryKeys` (key order), `primaryKeyDirections` (when any column is
  descending), `indexes` and `isPrimaryKey` per column under the same rule.
- **REQ-807-1:** `describe_dataset` reports `requiredFilterColumns` and `requiredFilterColumnGroups` as in 1.7.1.
  The REST detail gains the same two optional members (additive; absent when the dataset has none).
- **Anti-patterns rejected:** reporting keys/indexes regardless of column access (leaks names/order of masked or hidden
  columns); reporting a subset of a key (misleading paging contract); exposing index names (free text, I404-SC-L3).

## Phase 2: Design

- **Root cause:** commit `9344167e` (feat(discovery), first released in v1.8.0) routed MCP `describe_dataset`
  (`McpDatasetCatalog.DescribeDatasetWithDiscoveryAsync`) and the REST detail (`CatalogDiscoveryService.GetDatasetDetailAsync`
  -> `CatalogDiscoveryProjection.ToDatasetDetail`) through the discovery service whenever an access decision point is
  registered (always in production). `DiscoveryObject` carries no key, index or required filter data, so the new path
  hard-codes `PrimaryKey: false` / `Indexes: null` (`McpDatasetCatalog.Discovery.cs:322,353`), never sets
  `PrimaryKeyOrder` / `RequiredFilterColumns*`, and `CatalogDiscoveryProjection.cs:40,49,51` hard-codes
  `IsPrimaryKey: false`, `PrimaryKeys: []`, `Indexes: []`. The repository load (`GovernanceRepository.Catalog.cs`,
  `CatalogKeyDefinitionStore.ReadAsync`) is correct; the legacy (non-discovery) projection is correct.
- **Fix:** one projection in the discovery core. `DiscoveryAccessMapper.Map` computes a new `DiscoveryQueryShape`
  (`DiscoveryObject.QueryShape`) from the table metadata and the caller's mapped column states:
  - keys/indexes: `DatasetIndexProjection.Project(table, clear)` where `clear` = the visible (not discovery-hidden)
    columns whose mapped state is `Clear` (the same state the response reports per column);
  - required filters: `RequiredFilterProjection` names a required column only when the object lists it as accessible
    (not hidden, state not `NoAccess`); a group only when every member is listed (never a partial group).
- MCP discovery describe and `CatalogDiscoveryProjection.ToDatasetDetail` read `QueryShape`. `CatalogDatasetDetail`
  gains optional `RequiredFilterColumns` / `RequiredFilterColumnGroups`; the legacy REST path fills them too.
- Work packages: WP1 domain record + mapper; WP2 MCP discovery describe; WP3 REST projection + DTO; WP4 tests.

## Phase 3: Security

- **SEC-805-1:** a key or index is reported only as a whole and only when every column is a visible column with mapped
  state `Clear` for this caller (I404-SC-L1..L6 unchanged; masked, row-dependent, denied, discovery-hidden or unknown
  columns omit it). Index names are never reported.
- **SEC-805-2:** a caller without access (decision denied, all columns `NoAccess`) gets no key, index or required
  filter (fail closed: empty clear set yields `DiscoveryQueryShape.Empty`).
- **SEC-807-1:** a required filter column name is reported only when the same response lists that column; a group
  only when it lists every member, so no hidden or denied column name is disclosed.
- **SEC-805-3:** the shape is computed inside the per-caller memo key of the discovery service (no cross-caller reuse).
- Security tests carry `[Trait("Category", "Security")]`.

## Phase 4: Test plan

`Issue805807DiscoveryKeysAndFiltersTests` (unit, discovery path, both surfaces):
composite key `[device ASC, ts DESC]` + secondary index `[ts DESC]` in order and direction on MCP and REST;
`include: ["keys","indexes"]` narrowing keeps them; required filter columns and groups on MCP and REST; masked key
column hides key and index; discovery-hidden key column hides key; denied caller gets nothing; required filter group
with a hidden/denied member is withheld.

## Phase 5: Review follow-up

- The legacy (non-discovery) MCP describe path also projects the required filters onto the columns it lists (SEC-807-1).
- `MapQueryShape` builds the listed set only for tables with required filters and the clear set only for tables with keys or indexes.
- Added tests: row-dependent key column (maps to Masked, key omitted), masked required filter column (named: the rule is "listed
  with access", pinned), REST group with a `Deny` member (withheld), legacy-path hidden required filter column.

## Residual / notes

- A required filter group withheld by SEC-807-1 may leave a caller with fewer alternatives than enforced; the query
  then fails with `REQUIRED_FILTER_COLUMN_MISSING` as before (no disclosure, no bypass: enforcement is unchanged).
