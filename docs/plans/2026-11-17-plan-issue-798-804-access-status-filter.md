# PLAN-OI-798-804: accessStatus on catalog search hits and an accessStatus filter (issues #798, #804)

Issues: [#798](https://github.com/themulle/autheris/issues/798) (`list_datasets` cannot filter by access status), [#804](https://github.com/themulle/autheris/issues/804) (`search_catalog` / `find_relevant_tables` hits carry no access status and cannot be filtered). Related, not solved here: [#681](https://github.com/themulle/autheris/issues/681) (the visible set is recomputed per page). Code facts verified on `main` @ `595770e4`.

**Status: IMPLEMENTED** in branch `fix/798-804-access-status-filter` (one PR).

## 1. Product decision

Intended agent flow (issue #798 comment): "search my readable data first, then everything else to suggest an access request". Two calls must be enough: `search_catalog(query, accessStatus: "GRANTED")`, then `search_catalog(query)` (or `accessStatus: "REQUEST_ACCESS"`).

- Every `search_catalog` hit and every `find_relevant_tables` candidate carries `accessStatus`, the value `list_datasets` reports for the same dataset and caller (`GRANTED`, `PENDING`, `EXPIRED`, `REQUEST_ACCESS`; procedures `GRANTED` as on `list_datasets`).
- Optional filter `accessStatus` (one value) on `list_datasets`, `search_catalog`, `find_relevant_tables` and the paged REST list `GET /api/v1/catalog/objects`. It is applied before ranking, `limit`, `offset` and paging, so `total`, `total_hits`, `nextOffset` and `totalCount`/`nextCursor` describe the filtered set.
- MCP values: the enum `GRANTED | PENDING | EXPIRED | REQUEST_ACCESS` in the input schema (closed-world schema check rejects anything else before the tool runs). REST values: what `callerState.access` reports (`granted | requested | expired | none`) plus the MCP names, case-insensitive. Anything else: `400 INVALID_QUERY` naming the allowed values. Never ignored silently.
- `REST /objects` with an app-only caller (service principal, no `callerState`): the filter is rejected with `400 INVALID_QUERY` (an app has no caller access state to filter on).
- `find_relevant_tables.recommendedNextStep` no longer sends the agent to query tools for candidates it cannot read: when some or all candidates are not `GRANTED` it says to request access for those first.
- Rate-limit rationale (reporter): discovery is limited per person by `Catalog:Discovery:RequestsPerMinute` (default 120, refill 2/s; beyond that `RATE_LIMITED`). Without the filter a client pages `list_datasets` 30 times through 2,976 datasets to find a caller's few readable ones, so four loads of a "my data" page use up the minute; with the filter applied before paging it is one call. The rate-limit error contract is unchanged (separate issue).
- Rejected: a second "readable" boolean (two names for one fact), multi-value filters (not needed for the two-call flow), client-side filtering after `limit` (wrong totals; the reported problem).

## 2. Root cause

- `McpDatasetCatalog.ListDatasetsAsync` / `ListDatasetsWithDiscoveryAsync` (`src/Autheris.Application/Mcp/Services/McpDatasetCatalog.cs:183`, `McpDatasetCatalog.Discovery.cs:19`) and `DiscoveryQuery` (`src/Autheris.Application/Catalog/Discovery/IDiscoveryService.cs:19`) have no access parameter; access is only computed for the paged slice (`DiscoveryService.ResolveObjectsBatchAsync`, `DiscoveryService.cs:468`).
- `CatalogSearchHit` (`src/Autheris.Domain/Model/CatalogSearchModels.cs:59`) has no access member; `CatalogDiscoveryProjection.ToSearchHit` drops `DiscoveryObject.Access`; `McpToolExecutionHandler` (`McpToolExecutionHandler.cs:208-330`) does not emit it and has no filter.
- `ExternalCatalogListQuery` (`ExternalCatalogModels.cs:81`) has no access filter.

## 3. Design

- **One rule, one place.** `DiscoveryAccessMapper.AccessStateOf(isAllowed, isPending, isExpired)` is the single precedence rule (Granted > Requested > Expired > None); `MapAccessAndRowScope` and the filter both use it. `CatalogAccessStatus` (new, `Application/Catalog/Discovery`) maps `DiscoveryAccessState` to the MCP status string (moved from `McpDatasetCatalog.MapDiscoveryAccessStatus`) and parses filter values.
- **Set-based caller view (performance).** `DiscoveryService.LoadAccessViewAsync` computes the caller's access for a table list with:
  1. one `GetAllActiveConsentsForSubjectsAsync` (caller user SIDs + group SIDs, caller tenant), giving the grant candidates: tables targeted by an active Allow read consent (direct, or matched by a pattern grant; pattern grants never count for hidden tables), every table when consent is bypassed;
  2. the PDP (`DecideManyAsync`, `Discover`) only for those candidates; a table outside the candidates cannot be allowed (zero trust: the consent resolver denies without an applicable Allow consent, SR-M-07: access profiles never create an allow);
  3. the existing set reads `LoadPendingRequestTablesAsync` / `LoadRecentlyExpiredTablesAsync` (one query each).
  For 3,000 tables and a caller with 3 grants: 3 queries plus 3 PDP decisions. The unfiltered path is unchanged (#681 not made worse).
- `DiscoveryQuery` and `DiscoverySearchOptions` gain `DiscoveryAccessState? Access`. `ListAsync` filters the matched tables (and procedures by their resolved state) before `TotalCount`/`Skip`/`Take`. `SearchAsync` adds the filter to the engine visibility predicate (applied before top-k and `Limit`) and to the fallback path.
- `CatalogSearchQuery.AccessStatus` (filter) and `CatalogSearchHit.AccessStatus` (value). `CatalogDiscoveryService` passes the filter to discovery; the legacy path without a discovery service derives the status from the visible row (`CatalogView.Summary/Full` = `NONE`, otherwise `GRANTED`, the legacy vocabulary of `CatalogVisibility.ComputeAccessStatus`).
- `McpDatasetCatalog`: new overload with `accessStatus`; the discovery path passes the parsed state into `DiscoveryQuery`; the legacy path computes `CatalogVisibility.ComputeAccessStatus` for the matched candidates with the consents loaded once, before paging. MCP auth bypass reports `GRANTED` for every dataset as before.

## 4. Work packages

- WP-1 `CatalogAccessStatus` + `AccessStateOf`; `DiscoveryQuery`/`DiscoverySearchOptions.Access`; `DiscoveryService` filter.
- WP-2 `CatalogSearchHit.AccessStatus`, `CatalogSearchQuery.AccessStatus`, projection, `CatalogDiscoveryService`.
- WP-3 MCP: `list_datasets` overload and executor dispatch, `search_catalog` / `find_relevant_tables` argument, output member, schema and description, `recommendedNextStep`.
- WP-4 REST `/api/v1/catalog/objects?accessStatus=`.
- WP-5 tests, CHANGELOG.

## 5. Test plan (unit, filtered)

- `DiscoveryAccessStatusFilterTests`: list filter GRANTED / REQUEST_ACCESS / PENDING / EXPIRED with correct `TotalCount` and paging; the PDP is asked only for grant candidates (3 of 300); search filter applied before the limit; hidden tables stay hidden with the filter (Security); another caller's grants do not change the result (Security); status equality list vs. search.
- `McpAccessStatusFilterTests`: `search_catalog` / `find_relevant_tables` emit `accessStatus`, pass the filter, reject invalid values with `INVALID_PARAMS`; `recommendedNextStep` for non-granted candidates; tool schemas declare the enum; `list_datasets` (discovery and legacy path) filter with `total`/`nextOffset` of the filtered set, invalid value rejected.
- `ExternalCatalogAccessStatusFilterTests`: REST `/objects` filter, totals, invalid value 400, app caller 400.

## 6. Security conditions

- **SEC-798-1** The filter only narrows the caller's existing visible set (`LoadScopedCatalogAsync` runs first); it never adds a table, a hidden table never appears with any filter value.
- **SEC-798-2** The status is the caller's own: computed from the caller's SIDs, group SIDs and tenant only; no other caller's grants, no grantee, no consent id or count is emitted.
- **SEC-798-3** Fail closed: any error while computing the view falls into the existing discovery fail-closed handler (empty result); an unknown filter value is rejected, never treated as "no filter".
- **SEC-798-4** Error texts are fixed (allowed values only), the request value is never echoed.
- **SEC-798-5** Exactness: the candidate set is a superset of the tables the PDP can allow; the final state of each candidate comes from the PDP, so the filter and the per-object `accessStatus` agree.
