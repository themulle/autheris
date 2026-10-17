# PLAN-BUG-157: Omit Catalog Tables Without Queryable Columns from the GraphQL Schema

**Document ID:** `PLAN-BUG-157`
**Date:** 2026-10-17
**Phase:** 2 (solution architect)
**PRD:** [`REQ-BUG-157`](2026-10-17-req-issue-157-columnless-table-graphql-startup.md); intake [`INTAKE-2026-10-17B`](2026-10-17-issue-intake-assessment-2.md) item 1.9
**Owner decisions:** OQ-16..OQ-19 accepted (wave 1 rank 1.9; no write-time rejection; log plus list in system health; no catalog-free fallback)
**Suggested fix branch:** `fix/issue-157-columnless-table-schema`

## 1. Context

One active SQL catalog table whose surviving column list is empty yields a field-less `<type>_order_by` input (and a
field-less entity type without relations). HotChocolate rejects the schema during executor warm-up and the host stops.
The fix makes `CatalogSchemaModel.BuildAsync` omit such tables before reserving their type names, records every
omission with a reason, and exposes the list to admins in the existing system health endpoint.

## 2. Verified Code Facts (`origin/main` `fa7e29c0`)

| # | Fact | Location |
|---|---|---|
| F1 | Dialect exclusion: executable dialects without tree compile are logged (`LogInformation`) and excluded; unknown or non-executable dialects are dropped silently by the same `Where` | `src/Autheris.GraphQL/Catalog/CatalogSchemaModel.cs:121-138` |
| F2 | Names computed `:157-160`, collision check `LogError` + `continue` `:162-174`, reservation `:176-178`, column loop (R-GQL-13 regex skip `:188-192`) `:184-214`, unconditional add `:216` | `CatalogSchemaModel.cs` |
| F3 | Relation targets resolved via `tableMapById` built from `tableBuilders` only (`:219-221`, `:246`); reverse loop iterates `tableBuilders` (`:286`) | `CatalogSchemaModel.cs` |
| F4 | `Fingerprint()` covers included tables only | `CatalogSchemaModel.cs:62-84` |
| F5 | Private ctor `CatalogSchemaModel(List<CatalogTableType>)`; `Empty = new([])` | `CatalogSchemaModel.cs:56`, `:86-92` |
| F6 | Change check builds the model with `logger: null` and compares fingerprints; rebuild via `TypesChanged` | `src/Autheris.GraphQL/Catalog/CatalogGraphQlTypeModule.cs:57-106` (build `:85`) |
| F7 | Real build in `CreateTypesAsync` with `_logger`; catch only covers model build, sets `CatalogSchemaModel.Empty` | `CatalogGraphQlTypeModule.cs:130-151` |
| F8 | `_order_by` gets column fields only (`:207-215`); entity `ObjectType` columns + relations (`:217-254`) | `CatalogGraphQlTypeModule.cs` |
| F9 | Module is one DI singleton, also used as type module | `src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.cs:538-542`, `:599-600`; `AddGatewayGraphQL` always called (`Program.cs:69`) |
| F10 | `/api/governance/system/health` gated by `SystemMetrics.Enabled`, `ExposeRestEndpoints`, `IsAuthorized` (roles, default GovernanceAdmin); returns anonymous JSON `status/version/uptime/components`, 503 when a component is not healthy | `src/Autheris.Api/Endpoints/SystemEndpoints.cs:46-73`, `:98-106` |
| F11 | MCP map builds its own model with `logger: null` (cached) | `src/Autheris.GraphQL/Mcp/CatalogGraphQlMap.cs:196` |
| F12 | Test templates: collision `CatalogSchemaModelTests.cs:50-120`, R-GQL-13 `:317-335`, `CapturingLogger` `:371`; schema `CatalogGraphQlSchemaTests.cs:303` (colliding tables); refresh `CatalogSchemaRefreshRGql12Tests.cs:47-64`; SQLite host seeding `tests/Autheris.Tests.Integration/CatalogChannelParityTests.cs:42-62` | tests |

## 3. Design Decisions

1. **Omission types.** Add to `CatalogSchemaModel.cs`: `public enum CatalogTableOmissionReason { NoQueryableColumns, NameCollision, UnsupportedDialect }`
   and `public sealed record CatalogOmittedTable(TableIdentifier Table, CatalogTableOmissionReason Reason)`. The model gets
   `public IReadOnlyList<CatalogOmittedTable> OmittedTables { get; }`, ordered as encountered (deterministic, tables are sorted). Private ctor takes both lists; `Empty` has none.
2. **Filter position and name ordering.** In the `foreach (var meta in activeSqlTables)` loop: compute names, then build the column list (moved up, unchanged logic), then
   `if (columns.Count == 0)` record `NoQueryableColumns`, `LogWarning`, `continue` (no reservation); then the collision check (records `NameCollision`, keeps `LogError`); then reserve the three names. A table that is both empty and colliding is reported as `NoQueryableColumns`. Outcome: an omitted table never blocks names of a later table; relations to or from it vanish via F3 without code change.
3. **Dialect omissions.** Every active SQL table removed by the dialect filter (F1, both the logged and the silent case) is recorded as `UnsupportedDialect`; existing logging unchanged (no new log line for the silent case).
4. **Fingerprint includes omissions.** `Fingerprint()` appends `!<qualified name>:<reason>;` per omitted entry. Without this a newly added column-less table does not change the fingerprint (F4), so neither the warning nor the health list would appear until an unrelated change. Cost: one schema rebuild per catalog change that adds or removes an omission (rare).
5. **Logging.** One `LogWarning` per omitted table per real build: `"GraphQL catalog schema: table '{Table}' has no queryable columns and is omitted from the GraphQL schema."` with `ToQualifiedName()` only; no column names, no data, no count of skipped columns. Builds with `logger: null` (change check F6, MCP map F11) stay silent, so the warning appears once at startup and once per rebuild.
6. **Diagnostics source.** New `public interface ICatalogSchemaDiagnostics { IReadOnlyList<CatalogOmittedTable> OmittedTables { get; } }` in `src/Autheris.GraphQL/Catalog/ICatalogSchemaDiagnostics.cs`, implemented by `CatalogGraphQlTypeModule` through a `volatile` field set in `CreateTypesAsync` after a successful build; reset to `[]` in the catch branch (F7). Registered as `services.AddSingleton<ICatalogSchemaDiagnostics>(sp => sp.GetRequiredService<CatalogGraphQlTypeModule>())` next to F9. The list reflects the schema actually served, not the last change check.
7. **Health contract (additive).** `SystemEndpoints` health handler takes `[FromServices] ICatalogSchemaDiagnostics? catalogDiagnostics` and adds
   `graphQlCatalog = { omittedTableCount: int, omittedTables: [{ table: "domain.schema.table", reason: "NoQueryableColumns" }], omittedTablesTruncated: bool }`.
   Reason serialised with `ToString()` (no global enum converter dependency). List capped at 100 entries (`count` is the full number). Emitted only after the existing gates (F10), so 404/403 callers never see names. **Omissions do not change `status` or the status code** (default taken): a bad catalog row must not turn the health probe red, which would re-couple availability to catalog content.
8. **Runtime refresh.** No separate code: the change check builds via the fixed `BuildAsync`, so a column-less table added at runtime yields a valid rebuild (decision 4 triggers it), the warning and an updated health list. Locked by tests in WP-2.
9. **Non-changes.** No write-time validation (OQ-17), no catalog-free fallback (OQ-19), REST/OData/MCP/SQL untouched (R-7).

## 4. Work Packages

### WP-1 Model filter and reason codes (S, about 55 prod / 170 test LOC)
Files: `src/Autheris.GraphQL/Catalog/CatalogSchemaModel.cs`; `tests/Autheris.Tests.Unit/GraphQL/CatalogSchemaModelTests.cs`.
Tests (Red first):
- `BuildAsync_TableWithoutColumns_IsOmittedWithNoQueryableColumns`
- `BuildAsync_TableWhoseColumnsAllFailPlainIdentifierRule_IsOmittedWithNoQueryableColumns`
- `BuildAsync_OmittedColumnlessTable_DoesNotReserveTypeNames` (column-less `sales.dbo.orders` sorted before a valid table that sanitises to the same base name; valid one is kept)
- `BuildAsync_RelationToOrFromOmittedTable_IsNotExposed`
- `BuildAsync_CollidingTable_IsRecordedAsNameCollision`
- `BuildAsync_TreeUnsupportedDialectTable_IsRecordedAsUnsupportedDialect`
- `BuildAsync_ColumnlessTable_LogsOneWarningWithQualifiedNameOnly` (CapturingLogger; level Warning; message contains no column name)
- `BuildAsync_NullLogger_ColumnlessTable_DoesNotThrow`
- `Model_NeverContainsTableWithZeroColumns` (mixed catalog: valid, empty, all-filtered, colliding)
- `Fingerprint_ChangesWhenColumnlessTableAppears` and `Fingerprint_ChangesWhenOmittedTableGainsColumns`

### WP-2 Schema invariant, diagnostics source, refresh path (S, about 25 prod / 140 test LOC)
Files: `src/Autheris.GraphQL/Catalog/CatalogGraphQlTypeModule.cs`, new `src/Autheris.GraphQL/Catalog/ICatalogSchemaDiagnostics.cs`;
`tests/Autheris.Tests.Unit/GraphQL/CatalogGraphQlSchemaTests.cs`, `tests/Autheris.Tests.Unit/GraphQL/CatalogSchemaRefreshRGql12Tests.cs`.
Tests:
- `Schema_WithColumnlessAndAllFilteredTables_BuildsAndExecutesQueryOnValidTable` (Red today: `SchemaException`); omitted tables' query fields absent
- `Schema_NoGeneratedCatalogTypeHasZeroFields` (every catalog `InputObjectType` and `ObjectType` from the model has at least one non-introspection field)
- `Refresh_ColumnlessTableAddedAtRuntime_RaisesTypesChangedAndRebuiltExecutorServesValidTable`
- `Diagnostics_AfterBuild_ReportsOmittedTables` and `Diagnostics_CatalogUnreachable_ReportsEmptyList`

### WP-3 Health endpoint and host start (S, about 35 prod / 120 test LOC)
Files: `src/Autheris.Api/Endpoints/SystemEndpoints.cs`, `src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.cs` (one registration line);
new `tests/Autheris.Tests.Integration/ColumnlessCatalogTableStartupTests.cs` (SQLite seeding as F12).
Tests:
- `HostStart_WithColumnlessCatalogTable_ReachesReadyAndAnswersGraphQlOnOtherTable`
- `Health_Admin_ListsOmittedTableWithReason_StatusUnaffected`
- `Health_NonAdmin_Returns403WithoutTableNames`; `Health_Disabled_Returns404WithoutTableNames`
- `Health_MoreThan100Omissions_TruncatesListAndReportsFullCount`

**Total:** S, about 115 production and 430 test LOC. One developer step, WP-1 then WP-2 then WP-3.

## 5. Files Touched (overlap check)

Production: `src/Autheris.GraphQL/Catalog/CatalogSchemaModel.cs` (G-SHARED with #138 INC-4, merge #157 first),
`src/Autheris.GraphQL/Catalog/CatalogGraphQlTypeModule.cs`, `src/Autheris.GraphQL/Catalog/ICatalogSchemaDiagnostics.cs` (new),
`src/Autheris.Api/Endpoints/SystemEndpoints.cs`, `src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.cs` (one line near `:542`).
Tests: `tests/Autheris.Tests.Unit/GraphQL/CatalogSchemaModelTests.cs`, `tests/Autheris.Tests.Unit/GraphQL/CatalogGraphQlSchemaTests.cs`,
`tests/Autheris.Tests.Unit/GraphQL/CatalogSchemaRefreshRGql12Tests.cs`, `tests/Autheris.Tests.Integration/ColumnlessCatalogTableStartupTests.cs` (new).

## 6. Risks

- **R1 Behaviour of HotChocolate on a failed runtime rebuild is unverified.** Irrelevant after the fix for this cause; WP-2 refresh test locks the fixed path. Other schema errors stay out of scope (OQ-19).
- **R2 Fingerprint change (decision 4)** alters the hash for catalogs that already have collision or dialect omissions: one extra rebuild after deployment. No production yet; acceptable.
- **R3 Table names in logs** are catalog metadata already logged by the collision and dialect paths; no new class of data.
- **R4 SQLite tree-compile support** for the integration test is assumed from `CatalogChannelParityTests`; if absent, the host test uses PostgreSql metadata with the GraphQL query on a stubbed executor (decided in Red).

## 7. Open Questions

None. Defaults taken: health `status` unaffected by omissions (decision 7); list cap 100; silently dropped unknown dialects recorded as `UnsupportedDialect` without a new log line (decision 3).

## Phase 3 Security Review

**Reviewer:** `csharp-security-expert`, 2026-10-17. Code facts F1-F11 re-verified against `origin/main` (`CatalogSchemaModel.cs:56-92,121-216`, `CatalogGraphQlTypeModule.cs:57-151`, `SystemEndpoints.cs:46-73`). They are accurate.

**Verdict: APPROVED WITH CONDITIONS.**

### Assessment
- Fail-closed holds. The change is purely subtractive: a table is dropped from the model, never added or widened. The model is global (not tenant- or principal-scoped), so no per-caller schema variance is introduced. Per-table governance at runtime is untouched.
- R-GQL-13 does not leak. The omission reason is the fixed enum `NoQueryableColumns` and carries no column names. A table whose columns were all filtered by R-GQL-13 is indistinguishable from a table with no columns.
- Name ordering (decision 2) is sound. The omitted table reserves nothing, so no governed table is shadowed. Relations to or from it vanish through `tableMapById` (F3). The MCP map (F11) uses the same `BuildAsync`, so its omissions match.
- Fingerprint: the omission set is derived deterministically from the same inputs on the change-check build (`logger: null`) and the real build. There is no rebuild loop. Startup no longer fails on this cause, which removes an availability DoS reachable by one catalog row.

### Findings
| Id | Sev | Finding |
|---|---|---|
| S-1 | Medium | `GetEffectiveAllowedRoles()` is configurable. Any role added for health monitoring would receive full table names (`domain.schema.table`) in `omittedTables`, which is catalog metadata. The existing health payload carries no catalog data. |
| S-2 | Low | Existing R-GQL-13 `LogInformation` (`:188-192`) logs the column name on real builds. Decision 5 and the planned test ("no column name") would be wrong or flaky if they assert on all log output rather than on the new warning only. |
| S-3 | Low | Name allocation is sorted-order dependent. If a previously empty table that sorts earlier gains columns, it takes the type/field name and a later valid table becomes `NameCollision`. The same GraphQL field name then serves a different table (pre-existing scheme, but the omission now makes it reachable). Runtime governance is per table identifier, so no bypass, but clients are surprised. |
| S-4 | Low | `UnsupportedDialect` recording extends to the silently dropped non-executable/unknown dialects. The list must stay limited to active SQL tables, or it becomes a catalog-wide inventory. |
| S-5 | Info | Catch branch resets diagnostics to `[]`: an unreachable catalog looks identical to "nothing omitted". Acceptable, documented. |

### Binding conditions
- **SEC-BUG157-01** The model change is subtractive only: an omitted table must have no entity type, `_filter`, `_order_by`, query field, relation field (either direction) or MCP map entry, and must not reserve any name.
- **SEC-BUG157-02** `omittedTables` (names) is emitted only after the existing gates AND only when the caller holds `GovernanceAdmin` (or another principal authorised to read catalog metadata). Any other authorised health caller gets `omittedTableCount` only, no names. `graphQlCatalog` is omitted entirely when `ICatalogSchemaDiagnostics` is null.
- **SEC-BUG157-03** Health entries contain only `table` (qualified name) and `reason` (enum `ToString()`); never column names, column counts, dialect/connection details, source type or data-source ids. The list is capped at 100 in deterministic order; `omittedTableCount` is the full number; `omittedTablesTruncated` is set accordingly.
- **SEC-BUG157-04** Omissions never change `status` or the HTTP status code (decision 7 confirmed).
- **SEC-BUG157-05** The new `LogWarning` carries only the qualified table name. The test asserts on the new warning record only (the R-GQL-13 column `LogInformation` is pre-existing and out of scope). No new log line may contain column names or values.
- **SEC-BUG157-06** `UnsupportedDialect` is recorded only for active SQL tables. Inactive and non-SQL tables are never recorded or listed.
- **SEC-BUG157-07** The diagnostics list is an immutable snapshot (array or `ImmutableArray`) swapped atomically via the `volatile` field after the model build succeeds; it is never mutated in place and never shared with the builder's working list.
- **SEC-BUG157-08** Decision-2 ordering is fixed: `NoQueryableColumns` is checked before the collision check and before reservation. Row ordering of the sorted tables stays the allocation order. Do not change collision semantics for non-empty tables.
- **SEC-BUG157-09** The fingerprint omission suffix uses an unambiguous encoding (qualified name plus reason with delimiters that cannot merge two entries); the change check and the real build must use the same `BuildAsync` path so no rebuild loop arises.
- **SEC-BUG157-10** If R4 forces a stubbed executor for the host test, the stub must not bypass the governance pipeline in the security tests below; those run against the real model and endpoint.

### Mandatory security tests (`[Trait("Category","Security")]`)
- **T-BUG157-S01** `Model_OmittedTable_HasNoTypesFieldsRelationsOrReservedNames` (both relation directions, name reuse by the later table).
- **T-BUG157-S02** `Model_AllColumnsFilteredByRGql13_ReasonIsNoQueryableColumnsAndLeaksNoColumnName` (reason, log text and diagnostics contain no column name).
- **T-BUG157-S03** `Health_NonAdminAllowedRole_GetsCountWithoutTableNames`.
- **T-BUG157-S04** `Health_Admin_EntriesContainOnlyTableAndReason`.
- **T-BUG157-S05** `Health_NonAdmin403_AndDisabled404_ExposeNoTableNames` (covers body, not just status).
- **T-BUG157-S06** `Health_MoreThan100Omissions_CapsListDeterministicallyAndReportsFullCount`.
- **T-BUG157-S07** `Health_Omissions_DoNotChangeStatusOrStatusCode`.
- **T-BUG157-S08** `Model_InactiveAndNonSqlTables_NeverAppearInOmittedTables`.
- **T-BUG157-S09** `Warning_ColumnlessTable_ContainsOnlyQualifiedName` (new warning record only).
- **T-BUG157-S10** `Fingerprint_StableAcrossRepeatedBuilds_NoRebuildLoop` (check build vs real build, equal fingerprint with omissions present).
- **T-BUG157-S11** `Schema_OmittedTableName_NotQueryable_ValidTableStillGoverned` (query on the omitted name fails; valid table with masking/column rule still enforced after the fix).
- **T-BUG157-S12** `Diagnostics_Snapshot_NotMutatedByLaterBuild`.

No architect changes required beyond folding the conditions into the work packages.

## Changelog (Phase 4)

- 2026-10-17 G-PLAN-REBASE: F1-F11 re-checked against the branch; no drift. Deviations and notes:
  - `TableIdentifier.ToQualifiedName()` is `Schema.TableName` (no domain). The health payload (`table`) and the fingerprint therefore use the three-part name `Domain.Schema.TableName` (fingerprint length-prefixes every part); the new LogWarning keeps `ToQualifiedName()` as specified in decision 5.
  - Omitted-table list is sorted ordinally by domain, schema, table (not "as encountered") so the dialect omissions, which come from unsorted repository order, are deterministic (SEC-BUG157-03/09).
  - Role hierarchy: `ClusterAdmin` implies `GovernanceAdmin` in `GatewayRoleEvaluator`, so it receives names; the S03 test uses `SecurityAdmin` (default allowed, no implication).
  - R4 resolved without a stub: the host test starts twice over one SQLite file (first start seeds, second builds the schema from a catalog with column-less tables). Real model, schema and endpoint (SEC-BUG157-10).
  - Extra tests: `Fingerprint_OmittedTablesDifferingOnlyInDomain_DoNotCollide`, `Fingerprint_OmissionEntries_AreUnambiguouslyEncoded`.
  - WP-2 schema tests were green on arrival because WP-1 had already fixed the model; Red for the fix itself was the WP-1 tests (compile-red on `OmittedTables`, then behavioural).
