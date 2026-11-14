# Phase 3 security review: open-issue plans, priority group 4

- **Date:** 2026-11-14 · **Phase:** 3 (`csharp-security-expert`) · **Base verified:** `origin/main` @ `7a4df959`
- **Reviewed:** [`2026-11-13-plan-issues-websql-mcp.md`](2026-11-13-plan-issues-websql-mcp.md) sections #668, #681, #682. Owner defaults `Q-WEB-1..4` are binding (2026-11-13). `Q-SEC1-*`/`Q-SEC2-*` unconfirmed; nothing here depends on them.
- **Scope note:** #682 A is merged (#729); only #682 B is reviewed. **#681 is not fixed on main** (verified: `McpDatasetCatalog.ListDatasetsAsync` still pages by `offset` over the full visible set, no cursor, no `DecideManyAsync` caller outside the PDP); the full plan section is reviewed. If a #681 fix exists in another branch, re-check these conditions against it.
- **Ids:** `SEC-OI-<issue>-<n>`. Every test below carries `[Trait("Category","Security")]`. Binding for Phase 4 and Phase 5.

| Issue | WP | Verdict | Developer-ready |
|---|---|---|:---:|
| #668 | WP-OI-WEB-668 | APPROVED WITH CONDITIONS (gate and profile must see the connection key; no chaining) | yes |
| #681 | WP-SIMPL-41 PR 3 amendment + WP-OI-WEB-681 | APPROVED WITH CONDITIONS (cursor integrity, counts, cache key) | after WP-41 PR 3 |
| #682 B | with WP-SIMPL-41 PR 3 | APPROVED WITH CONDITIONS (no existence oracle, global model cache) | after WP-41 PR 3 |

## #668 data source name resolution

**Verified.** `GovernedCatalogProvider.cs:195` `FindConnection` reads `DataSources:Connections[name]` directly and `:266` requires `Table.SourceName == name`; `SqlDataSourceResolver.cs:91-93` resolves a mapped name to itself; `DataSourceAllowlistGuard.cs:85` already maps names for the allowlist and transport gate (`:76` canonicalises itself). So there are already two mapping implementations; the plan adds a third entry point unless it replaces them.
- SEC-OI-668-1: one resolver. `SqlDataSourceResolver.ResolveConnectionKey` is the only mapping code; `DataSourceAllowlistGuard` and the transport gate call it (no private mapping copy remains). Mapping is **one hop**: the target must be an existing key in `DataSources:Connections` (case-insensitive, as the connection dictionary); a target that is itself a mapping key, an empty target or a cycle fails startup validation (`GatewayStartupValidator`) and at runtime resolves to `null` = deny.
- SEC-OI-668-2: the admission rule is `Table.SourceName == ConnectionKey` and nothing else; a mapping never admits an entry registered on another connection. `Table.SourceName == logical name` is **not** accepted as an alternative once a mapping exists for that name (no either-or).
- SEC-OI-668-3: transport gate, per-source profile (read-only login, row caps, timeouts), credentials and dialect come from the **connection key**; allowlists (global, tenant) and the catalog domain stay on the logical name. Where a rule exists on both names, both must pass (deny if either denies). Tenant ownership of the connection (if connections are tenant-owned) is checked for the connection key too.
- SEC-OI-668-4: caches (catalog admission, fingerprint, plan or template caches keyed by data source) include the connection key and a hash of the mapping value; a mapping change never reuses a cached admission (plan test kept). Federation uses the same resolver; a test enumerates every call site of the old lookup and fails on a bare `Connections[...]` read outside the resolver.
- SEC-OI-668-5: the detailed denial reason naming both names goes to the server log only (`LogAdmissionDenied`); client responses keep the fixed denial text and never include a connection key or mapping target (connection names are internal topology).
- Tests: plan names plus `Mapping_TargetThatIsAlsoMappingKey_FailsStartup`, `Mapping_Cycle_FailsStartup_AndResolvesToNull`, `GetCatalog_EntryWithLogicalSourceName_Denied_WhenMappingExists`, `TransportGate_And_AllowlistGuard_ShareResolver` (architecture), `Profile_ComesFromConnectionKey_NotLogicalName`, `Denial_Response_ContainsNoConnectionKey`, `TwoLogicalNames_OneConnection_EachSeesOnlyOwnDomainTables`.

## #681 list_datasets paging

**Verified** (see scope note). The visible set and all caller consents are loaded per page on main; WP-SIMPL-41 PR 3 introduces `IDiscoveryService`. The plan changes the paging contract and adds a cache, both security-relevant.
- SEC-OI-681-1: the cursor is **signed and bound**: HMAC over `{after-id, filter hash, subject id, tenant id, catalog epoch, expiry (15 min)}` with the data-protection/keyset key (same key policy as SEC-OI-487, failing startup outside Development if absent), at most 512 bytes. Any parse, signature, expiry, subject or tenant mismatch answers `InvalidArgument` with a fixed message; never a restart from the first page, never an unfiltered list. The cursor is compared as an ordinal position only (`id > after`); the `after` id is never looked up, so a forged or hidden id behaves exactly like a nonexistent one.
- SEC-OI-681-2: the page is cut over the candidate list **after** visibility (scope plus hidden flags), then `DecideManyAsync` runs for the page objects only. The next cursor is the last **candidate** id of the page (not the last returned row), so a page thinned by decisions still advances. A decision throw or a missing decision for an object = that object is denied, an overall failure returns an error or empty page, never the undecided list.
- SEC-OI-681-3: `total`/`hasMore`/counts are derived from the visible candidate list only, with the same meaning as today; no count of hidden objects, denied objects or decisions is exposed. Page size has a hard maximum (100) enforced server-side; `offset` (legacy) is capped at 10,000 and `offset+limit` is bounded; the legacy `offset` path is removed after one release (tracked in the plan, not open-ended).
- SEC-OI-681-4: the optional cross-request candidate cache key is `(tenant, catalog epoch, **grant epoch**, subject id, scope key, claims hash, filter hash)`. The grant epoch is required because candidate visibility depends on tenant allow-consent and owner views; without it a revoked grant stays visible until the catalog epoch moves. Bounded by entry count (e.g. 1,000) and bytes, absolute TTL at most 30 s as backstop; an epoch-store failure bypasses the cache (miss), never serves stale. The cached value holds table ids only (no decisions, no column data); decisions are never cached across requests by this layer.
- SEC-OI-681-5: the tenant allow-consent and owner/delegate queries are per tenant and per subject with indexed predicates and a row cap; a query failure denies (empty candidate set for that source), not "all visible".
- Tests: plan names plus `Cursor_Tampered_Or_Expired_InvalidArgument_NoRestart`, `Cursor_OtherSubject_Or_OtherTenant_InvalidArgument`, `Cursor_HiddenOrUnknownAfterId_BehavesIdentically`, `Page_ThinnedByDecisions_StillAdvancesCursor`, `Counts_DoNotIncludeHiddenOrDeniedObjects`, `Limit_Above_Max_IsClamped_And_Offset_Capped`, `CandidateCache_GrantRevoked_DisappearsOnNextPage`, `CandidateCache_EpochStoreDown_BypassesCache`, `DecideMany_MissingDecision_IsDenied`.

## #682 B describe by GraphQL root field

**Verified.** `CatalogSchemaModel.cs:124,307` keeps `TablesByQueryFieldName` (Ordinal) and omits name-collision tables, so a root field maps to at most one table. `CatalogGraphQlMap` holds one global `_cached` model for 30 s (`:31,:371-378`), not per tenant: the map may contain tables of other tenants.
- SEC-OI-682-1: `graphQlField` is validated before lookup: at most 256 chars, `[A-Za-z0-9_]` only, control characters rejected; exactly one of `dataset`/`graphQlField` (both or none = `InvalidArgument`, fixed message). Case-sensitive exact match.
- SEC-OI-682-2: the map result is only a candidate identifier. The tenant filter and visibility are applied by the unchanged `FindVisibleAsync` re-read; the map is never used to answer visibility. Unknown field, a field of a hidden table, of another tenant, or of a table without root field produce **the same code path** (unknown or unresolved field is converted to a sentinel id and passed through `FindVisibleAsync` too), the same result object and a byte-equal payload; the response and audit never echo the field value or the resolved identifier.
- SEC-OI-682-3: `datasetGraphQlField` in `graphQl.relations[]` is emitted only for targets in `VisibleRelationTargetsAsync`; for invisible targets the property is **absent**, and the serialized relation is identical to today's. The field comes from the map (SEC-OI-682-A rule), never from a string formula.
- SEC-OI-682-4: map entries of tables the caller's tenant does not own never surface in an error, log line at Information or above, or timing-distinguishable branch (verify `CatalogGraphQlMap` building scope; if the global model contains all tenants' tables, document that the lookup stays safe only because of SEC-OI-682-2).
- Tests: plan names plus `DescribeByGraphQlField_InvalidCharsOrLength_InvalidArgument`, `DescribeByGraphQlField_UnknownHiddenOtherTenant_SameCodePath` (call-count fake on `FindVisibleAsync` equal), `DescribeByGraphQlField_ErrorDoesNotEchoField`, `Relations_InvisibleTarget_PropertyAbsent_ShapeUnchanged`, `GraphQlFieldMap_CollisionOmittedTable_NotResolvable`.

## Anything the plans missed

1. #668 already has a second mapping implementation in the allowlist guard; one resolver must replace it, and chaining must be impossible (SEC-OI-668-1).
2. #681 unsigned cursor, grant epoch missing from the cache key, thinned pages stalling or leaking (SEC-OI-681-1/2/4).
3. #682 B lookup must run the same path for unknown and invisible fields; model cache is global (SEC-OI-682-2/4).
4. #681 is open on main contrary to the intake note.

## New owner questions (NOT confirmed)

| Id | Question | Recommended default |
|---|---|---|
| Q-SEC4-1 | #681: sign the cursor with the keyset/data-protection key (adds a startup key requirement outside Development)? | Yes; same key policy as SEC-OI-487 |
| Q-SEC4-2 | #681: remove legacy `offset` after one release or keep capped permanently? | Remove after one release |
| Q-SEC4-3 | #668: allow only one-hop mappings and fail startup on chain/cycle? | Yes |
