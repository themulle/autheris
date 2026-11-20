# Plan: Trino metadata follow-ups from the PR #887 review (WP-SIMPL-38 PR 4)

Phase 2 (solution architect), 2026-11-19. Source: the Phase 5 review of [PR #887](https://github.com/themulle/autheris/pull/887), which was merged with these findings open. No GitHub issue exists yet. Code facts verified on `origin/main` `e3e32b401`. Implementation starts after the v1.9.0 tag. Lane: WP-SIMPL-38 (Trino protocol); keep in sync with `simplification/WP-SIMPL-38-trino-protocol-clean.md`.

## 1. Findings and evidence

| ID | Review severity | Evidence on main | Status |
|---|---|---|---|
| F-887-1 | Low | `TrinoMetadataQueryHandler.Jdbc.cs:54-55` `VisibleKey` upper-cases domain, schema and name. `TryAdd` at lines 72 (discovery) and 111 (metadata) keeps the first entry. Two catalog rows that differ only in case collide, so a decision for one table can be paired with the other's discovery entry. `TableIdentifier` equality is `OrdinalIgnoreCase` (`src/Autheris.Domain/Common/TableIdentifier.cs:57-66`), but the store does not stop case-variant rows. | open |
| F-887-2 | Low | `TrinoMetadataQueryHandler.Jdbc.cs:47-50`: the handler first loads every discovery page (up to `MaxDiscoveryPages = 1000`), every active table (`CatalogTablePageReader.ReadAllAsync`) and one `DecideManyAsync` over the whole visible set. Then `TrinoJdbcMetadataShapes.Execute` (`TrinoJdbcMetadataShapes.cs:344`) builds all rows. The row cap is applied afterwards in `WebSqlStatementManager.cs:249-255`. | open |
| F-887-3 | Low | `tests/Autheris.Tests.Unit/Sql/TrinoMetadataJdbcTests.cs:90,185`: only `NoAccess` is tested. The fail-closed branch for an undefined `DiscoveryColumnState` (f8edc9f3f, `TrinoJdbcMetadataShapes.cs` `IsVisible`) is not pinned. | open |
| F-887-4 | Info, **treated here as security relevant** | `TrinoMetadataQueryHandler.cs:87` is a public overload that passes `visibility: null`. With null visibility, SHOW and `information_schema` (lines 154-160) list `CatalogVisibility.VisibleTablesAsync` (consent based) without the discovery and PDP intersection. Only `system.jdbc` fails closed (`.Jdbc.cs:41-44`). `WebSqlStatementManager.cs:229-231` resolves `IDiscoveryService` and `IAccessDecisionPoint` with `GetService` and silently falls back to null. Production registers both (`GatewayCatalogServiceExtensions.cs:88`, `GatewayExecutionServiceExtensions.cs:100`), so the fallback only happens in a host composition without them. | open |

## 2. Options and decisions

1. F-887-4. Options: (a) keep the fallback and log it; (b) make visibility mandatory and refuse the statement when it is missing. **Decision: (b)**, fail closed.
   - `TryExecuteAsync` takes a non-nullable `TrinoMetadataVisibility`.
   - The null overloads at lines 54-87 are removed.
   - `WebSqlStatementManager` uses `GetRequiredService`.
   - A missing registration fails at startup through the DI composition snapshot, not per request.
2. F-887-1. Options: (a) key by the exact (ordinal) identifier; (b) keep case-insensitive keys but fail closed on ambiguity. **Decision: (b)**, which matches `TableIdentifier` semantics everywhere else. When two discovery entries or two active metadata rows share a key, the key is ambiguous and no row is emitted for it. A paired entry is also accepted only when the discovery identifier equals the metadata identifier ordinally.
3. F-887-2. Options: (a) pass the limit into `Execute` for an early stop; (b) push equality predicates on `TABLE_SCHEM`/`TABLE_NAME`/`TABLE_CAT` down into the discovered set before `DecideManyAsync`; (c) both. **Decision: (c)**.
   - Without `ORDER BY`, `Execute` stops after `limit + 1` rows so `Truncated` stays correct.
   - Equality and `IN` predicates (the shape JDBC `getColumns(cat, schema, table)` sends) filter the discovered set before metadata pairing and the decision call. The decision call then shrinks from the whole catalog to a handful of tables.
   - LIKE predicates are not pushed down.
   - The full catalog read (`ReadAllAsync`) is replaced by the paged reader once the #677 plan lands. That is out of scope here.

## 3. Work packages

**WP-887-1 (S, about 150 LOC): mandatory visibility (F-887-4), security**
- `src/Autheris.Application/Sql/Services/TrinoMetadataQueryHandler.cs`: delete the overloads at lines 54-87, make `visibility` non-nullable and remove the `if (visibility != null)` branch at line 156.
- `TrinoMetadataQueryHandler.Jdbc.cs:41-44`: remove the null check.
- `WebSqlStatementManager.cs:229-231`: use `GetRequiredService`.
- Migrate the 7 unit test callers to a shared `TrinoMetadataVisibilityFakes` helper (allow-all fake discovery and PDP built from the test catalog) in `tests/Autheris.Tests.Unit/Sql/`. The callers are `CatalogVirtualTableVisibilityTests`, `TrinoMetadataColumnDenyTests`, `VisibleTablesTenancyTests`, `CatalogCallerMigrationBatch2Tests`, `SemanticMcpDispatchAndTrinoMetadataTests`, `Issue271SecurityAuditVerificationTests` and `TrinoMetadataJdbcTests`.
- New `[Trait("Category","Security")]` tests in `TrinoMetadataJdbcTests`:
  - `ShowTables_HiddenByDiscovery_IsNotListed`
  - `InformationSchemaColumns_DeniedByPdp_IsNotListed`
  - `WebSqlStatementManager_WithoutDiscovery_FailsAtComposition` (in the DI composition tests)

**WP-887-2 (S, about 80 LOC): ambiguity fail closed (F-887-1) and undefined state (F-887-3), security**
- `TrinoMetadataQueryHandler.Jdbc.cs` `LoadVisibleObjectsAsync` and `BuildEntriesAsync`: track duplicate keys in a `HashSet<string> ambiguous` and drop those keys. Pair an entry only if `string.Equals(discovery.Domain/Schema/Name, metadata.Identifier.*, StringComparison.Ordinal)`.
- The same key helper is used by SHOW/`information_schema` (`TrinoMetadataQueryHandler.cs:158-159`), so apply the same ambiguity rule there.
- Tests (`TrinoMetadataJdbcTests`, Security):
  - `CaseVariantTables_AreBothOmitted`
  - `DiscoveryAndMetadataCaseMismatch_IsNotPaired`
  - `UndefinedColumnState_IsNotListed` (`(DiscoveryColumnState)99`)

**WP-887-3 (S, about 120 LOC): bounded work (F-887-2)**
- `TrinoJdbcMetadataShapes.Execute(query, entries, int? rowLimit)`: when there is no `ORDER BY`, apply `Take(rowLimit + 1)`.
- New `TrinoJdbcMetadataShapes.PushdownKeys(TrinoJdbcQuery)`: returns the equality and `IN` sets per catalog, schema and table column, and is applied to `discovered` before `BuildEntriesAsync`.
- Thread `rowLimit` from `WebSqlStatementManager` into `TryExecuteAsync`. Keep the existing truncation at lines 249-255 as a second guard.
- Tests: `TrinoMetadataJdbcTests`
  - `GetColumnsShape_DecidesOnlyMatchingTables` (count the PDP calls on the fake)
  - `Unordered_StopsAtLimitPlusOne_TruncatedSet`
  - `Ordered_StillSortsFullFilteredSet`
  - `LikePredicate_IsNotPushedDown`

Order: WP-887-1, then WP-887-2 (both touch the same file), then WP-887-3. WP-887-1 and WP-887-2 can share one PR. No DDL.

## 4. Integration test impact

- `tests/Autheris.Tests.Integration/TrinoJdbcMetadataParityTests.cs`: must stay green. Add a parity case for `getColumns(schema, table)` against a two-table catalog (WP-887-3).
- `git grep -n "TrinoMetadataQueryHandler" tests/Autheris.Tests.Integration tests/Autheris.Tests.Parity`: at merge time there are no direct callers. If one appears, migrate it to the fake helper.
- The PR #908 Trino client interop nightly (owner review) runs the JDBC client suite once after merge.

## 5. Phase 3

**Required** for WP-887-1 and WP-887-2 (metadata visibility and fail-closed pairing). WP-887-3 is a performance change but changes which tables are decided, so it goes to the same Phase 3 review. No SEC conditions are set here.

## 6. Owner questions (recommended defaults)

1. Q1: Should a GitHub issue be filed for these review findings? Default: **yes**, one hardening issue. F-887-4 is not exploitable with the production composition, so it is not an advisory.
2. Q2: Should case-variant tables (F-887-1) be refused at catalog write time instead? Default: **not in this plan**. A separate catalog write check could be a follow-up issue.
