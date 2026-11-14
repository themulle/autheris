# PLAN-OI-668: WebSQL denies tables created through Dbt:DatabaseMappings

- Date: 2026-11-14 · Base: `origin/main` @ `c4b0078a` · Issue: [#668](https://github.com/themulle/autheris/issues/668)
- Phase 1 and 2: [`2026-11-13-plan-issues-websql-mcp.md`](2026-11-13-plan-issues-websql-mcp.md), section #668 (WP-OI-WEB-668; Q-WEB-1: no implicit coupling to the dbt mapping, only a startup warning).
- Phase 3: [`2026-11-13-sec-issues-group-4.md`](2026-11-13-sec-issues-group-4.md), section #668 (SEC-OI-668-1..5, APPROVED WITH CONDITIONS); owner decisions [`2026-11-14-owner-decisions-sec-groups-1-9.md`](2026-11-14-owner-decisions-sec-groups-1-9.md) (Q-SEC4-3: one-hop mappings only). This file records the verification on the base and how each condition is met.
- Origin plan: [`00-master-plan-overview.md`](00-master-plan-overview.md) (plan registry).
- Freeze (PRD 5.1): bug fix, no new option, field, endpoint or switch. Target <= 400 net lines in `src/`.

## Verified on the base

- The governed catalog never applies `WebSql:DataSourceMappings`: `GovernedCatalogProvider.FindConnection` reads `DataSources:Connections[name]` with the logical name and `LoadAsync` requires `Table.SourceName == name`. A mapped name therefore never reaches a connection; no deployment can rely on a mapping through the governed path today.
- `WebSql:DataSourceMappings` is read in five places, each with its own copy of the lookup: `SqlDataSourceResolver.TryResolveGloballyAllowedDataSource` and `.ResolveConnectionOptions`, `DataSourceAllowlistGuard.MapDataSource`, `HierarchicalDataSourceOptionsResolver` (per-source profiles), `FederatedStagingService`, plus the name set in `GatewayStartupValidator`. Phase 3 found two; SEC-OI-668-1 applies to all of them.
- Executor: `GovernedSqlExecutor` calls the transport gate with the logical name (`EnsureCatalogSourceAsync(dsName)`) and resolves the connection through `ResolveConnectionOptions(dsName)`. Rewriter: `GovernancePolicyFactory` passes the logical name to `IGovernedCatalogProvider.GetCatalogAsync`.
- No open PR touches these files (checked 2026-11-14; #738/#739 merged; `feat/simpl-51-pr-4` is an older branch without PR).

## Deviation from Phase 3: admission on the logical name

The plans assume a dbt table catalogued as `D.schema.table` keeps `Table.SourceName = C` (the connection key). That holds only in memory: `DbtCatalogTableFactory` sets `SourceName = connectionName`, but the repository writes `Identifier.Domain` into `TABLES.source_name` (`GovernanceRepository.Catalog.cs`, `@domain`) and reads `SourceName` back from that column. A persisted entry therefore always has `SourceName == D` and records no connection at all; which connection a table uses follows from the configuration only (`DataSources:Connections` plus the WebSQL mapping). SEC-OI-668-2 ("admit only when `Table.SourceName == ConnectionKey`") would deny every mapped table, and "an entry registered on another connection" cannot be represented.

This PR therefore admits on the logical name (`Table.SourceName == requested logical name`, as before) and takes connection, dialect, fingerprint, physical keys, transport gate and per-source profile from the connection key the resolver returns. Recording the connection per table would need a governance DDL step and every registration path; it is out of scope. **The Phase 3 reviewer is asked to re-approve this deviation in the PR.**

## Conditions and how they are met

| Condition | Implementation |
|---|---|
| SEC-OI-668-1 one resolver, one hop | `SqlDataSourceResolver.ResolveConnectionKey(logicalName)` is the only mapping lookup; the allowlist guard, federation, rewriter and governed catalog call it. A target must be an existing connection key and not itself a mapping key; chain, cycle, empty or unknown target fail startup validation and resolve to `null` (deny) at runtime. An architecture test fails on a direct mapping lookup outside the resolver. |
| SEC-OI-668-2 admission | Deviation, see above: `Table.SourceName == logical name`; a mapping never admits a table of another logical name; a mapping to a missing connection or a connection of another dialect denies. |
| SEC-OI-668-3 keys | transport gate, profile, credentials and dialect from the connection key; allowlists and catalog domain from the logical name. |
| SEC-OI-668-4 caches | admission and fingerprint cache keys contain the logical name, the connection key and the mapping value. |
| SEC-OI-668-5 disclosure | the reason naming both names goes to `LogAdmissionDenied` only; the client keeps the fixed denial text. |
| Q-WEB-1 | startup warning for a `Dbt:DatabaseMappings` source database in `WebSql:AllowedDataSources` without a WebSQL mapping; no coupling. |

## Phase 4 and 5

- Phase 4 by a Sonnet developer agent (handover rule), strict TDD: red commit with the tests (builds on its own; 24 of 41 targeted unit tests failed for the expected reasons), green commit, and a follow-up commit for the secured tree harness stub (it compiled through the implicit `string` conversion and failed at run time).
- Size: `src/` +152 / -51 (101 net); tests +835.
- Phase 5 review (this session) over the diff; no blocking finding. Points disclosed in the PR:
  - Admission does not check that the mapped connection is the right one for an entry; a mapping to another connection of the same dialect is accepted. The catalog does not record a connection, so the operator's mapping and the allowlists are the guard (see the deviation above).
  - Startup now fails for a mapping with an empty or unknown target, a chain, a cycle or a self-mapping (`C -> C`); before, an empty target fell back to the name and a chain ended after one hop. The check runs whether or not WebSQL is enabled, because the allowlist guard, federation and per-source profiles read the same mapping.
  - `GovernedDataSource` converts implicitly from `string` (an unmapped name is its own connection key). A mapped name passed as a plain string fails closed (the provider re-resolves the key and denies on a mismatch), but a test stub on the old signature compiles and fails only at run time.
- Verification: `dotnet build Autheris.sln` 0 warnings, 0 errors; unit 16,821 passed, 3 failed (the known time-zone cases of `TimeBucketingPlannedSqlCompileTests`, also red on `main`); architecture 551 passed, 2 skipped; integration `WebSqlDbtMappedTableTests` 3 passed; regression `GovernedEndToEndTests|GovernedWebSqlIntegrationTests|WebSqlSqlServerSynonymTests` 59 passed, 8 skipped (DML cases until WP-SIMPL-45).
- Delivery: UNDER REVIEW (draft PR #747; the deviation from SEC-OI-668-2 needs re-approval by the Phase 3 reviewer).
