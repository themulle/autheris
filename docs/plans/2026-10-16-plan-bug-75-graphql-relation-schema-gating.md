# Implementation Plan: BUG #75 - GraphQL Schema Must Not Advertise Cross-Source Relations That Cannot Run

**Document ID:** `PLAN-BUG-75-GATING` (revision 1, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [REQ-FED-01](2026-10-16-req-cross-source-all-consumers.md) (matrix row 5, FR-7, AC-7, rejected anti-pattern 8), [PLAN-FED-01](2026-10-16-plan-cross-source-all-consumers.md) (4.7 WP-FED-07, 4.8 WP-FED-08), issue #75 / F-GOV-19
**Branch:** `fix/bug-75-graphql-relation-schema-gating` (base `main` `83853479`)
**Parent Plans:** [00-master-plan-overview.md](00-master-plan-overview.md), [PLAN-FED-01](2026-10-16-plan-cross-source-all-consumers.md)
**Status:** PROPOSED (Phase 2 complete; next: Phase 3 security review, then Phase 4 TDD)

**Binding project rules applied:** fail closed; Autheris is not in production, so a breaking schema change is allowed; no migrations (nothing persistent changes); CI stays fast; Phase-4 TDD developers are Sonnet; documentation in English; `docs/` carries no progress notes (the F-GOV-19 correction is a Phase 6 task and describes current behaviour only).

---

## 1. Verification of the Bug in the Code (`83853479`)

| Question | Result |
|---|---|
| Where are relation fields added to the schema model? | **Confirmed.** `CatalogSchemaModel.BuildAsync` (`src/Autheris.GraphQL/Catalog/CatalogSchemaModel.cs`) builds `CatalogTableType.Relations` in two loops: forward relations (`:238-274`, the table is the relation parent) and reverse relations (`:279-326`, the table is the relation child; field name `<parentTable>_by_<relation>`). There is no `AddForward`/`AddReverse` method, both are inline loops. |
| What do the loops check? | Only that the other table is in the schema (`tableMapById`, `:243-246`; every active SQL table of a tree-capable dialect) and that join keys exist (`:247-251`, `:297-301`). **No comparison of data sources.** `TableRelation.AllowCrossDataSource` (`src/Autheris.Domain/Model/TableModels.cs:293`) is not read anywhere in `src/Autheris.GraphQL`. |
| How is `IsCrossDataSource` determined? | Only in `CrossSourceRelationResolver.IsCrossDataSource` (`src/Autheris.Application/Data/Relations/CrossSourceRelationResolver.cs:49-70`): different `DataSourceType`, **or** different `Table.SourceName`, **or** different `Identifier.Domain`. It is never called by the schema or by the tree path. |
| How are the fields wired into the schema? | `CatalogGraphQlTypeModule.cs:234-253` emits one field per `table.Relations` entry (list or single, with `where`/`orderBy`/`first`/`offset` arguments). Resolution is a pure JSON projection of the tree result (`ResolveRelationValue`, `:479-497`); there is no relation data loader. `GraphQlTreeBuilder.cs:159-181` maps a selected relation field to a `TreeRelationNode` of the same single tree query. |
| What happens at runtime when such a field is selected? | `GovernedTreeQueryService` resolves access for every table of the tree first (denial wins: `GatewayForbiddenException("Access denied.")`, `:253-260`), then at `src/Autheris.Application/Sql/Tree/GovernedTreeQueryService.cs:274-277` throws `GatewayInvalidQueryException("Relations across data sources are not supported in one query.")`. The error code is the constructor default `INVALID_QUERY_OPTION` (`src/Autheris.Domain/Exceptions/GatewayExceptions.cs:58`; REQ-FED-01 calls it `INVALID_QUERY`, the family is the same); outside Development `ErrorSanitizingFilter` replaces the message by a generic one. **The check compares `Metadata.Table.SourceName` only** (not `Domain`, not `AllowCrossDataSource`). So the field always fails when the two tables have different `SourceName`, and **works** for tables with the same `SourceName` and different `Domain` (one connection, one tree statement). |
| Is `CrossSourceRelationResolver` wired? | **Registered but unreachable.** `src/Autheris.Api/Extensions/DependencyInjection/DataApiServiceExtensions.cs:25-26` registers `InMemoryCrossSourceRowReader` (singleton, in-memory test double, empty unless seeded) and `CrossSourceRelationResolver` (scoped). No production code resolves or calls `ICrossSourceRelationResolver` or `ResolveRelationAsync` (only static `TryExtractKey` is used by change detection, `GovernedChangeDetectionService*.cs`, `InMemoryChangeVersionStore.cs:126`). Neither the GraphQL module nor `GovernedTreeQueryService` references it. |
| Aggregate types, filter/order types, subscriptions | **Not affected.** `<table>_aggregate` (`CatalogGraphQlTypeModule.cs:272-278`) takes `groupBy`/`where` strings and has no relation fields; `<T>_filter` and `<T>_order_by` contain no relation fields (REQ-FED-01 matrix row 6); subscriptions are one topic per table (matrix row 9). Relations exist only on the object type of a table (`:234-253`), in `GraphQlTreeBuilder` (`:160`) and in `CatalogGraphQlMap` (`Mcp/CatalogGraphQlMap.cs:46,146-153`), all three read `CatalogTableType.Relations`. |
| Which docs overclaim? | `docs/features/f-gov-19-cross-source-graphql-relations.md`: title and 1.2 ("Autheris supports Declared Cross-Source Relations ... Batched Key Lookup", `:17-23`), the sequence diagram (section 2), config block with `MaxNestedRowBudget` (section 3; real option name is `MaxNestedRows`, not bound), the example with `where: { customer: { country ... } }` (section 4.1; relation filters do not exist in the schema), and the error table (section 5, errors that are never surfaced by GraphQL). REQ-FED-01 section 1.4 lists the same lines. `docs/features/README.md:76` repeats the title. |

**Conclusion: the bug is confirmed.** Every relation between tables of different `SourceName` is advertised in the schema (and in the MCP GraphQL map) and fails at execution. `AllowCrossDataSource` has no effect on the schema.

---

## 2. Architecture Decisions

### A-1 One executability predicate, shared by the schema now and by WP-FED-08 later
New pure static class `RelationExecutability` in `src/Autheris.Application/Sql/Tree/Federation/RelationExecutability.cs` (the file WP-FED-08 already lists as its own, PLAN-FED-01 section 7). This bug fix creates the file with the minimal member; WP-FED-08 extends it (A-4).

```csharp
namespace Autheris.Application.Sql.Tree.Federation;

public static class RelationExecutability
{
    /// <summary>
    /// True when the inner tree service can run a relation between the two tables as one statement:
    /// both tables belong to the same data source connection (the exact condition checked at
    /// GovernedTreeQueryService, "Relations across data sources are not supported in one query").
    /// </summary>
    public static bool IsSameExecutionSource(TableMetadata parent, TableMetadata child);
}
```

`IsSameExecutionSource` = `DataSourceType` equal **and** `Table.SourceName` equal (`OrdinalIgnoreCase`). It deliberately does **not** compare `Identifier.Domain`, unlike `CrossSourceRelationResolver.IsCrossDataSource`: the tree service accepts relations across domains of one connection, so hiding them would remove relations that work today (a regression). See open question Q-1 for the resulting difference from PLAN-FED-01 4.8, which says "`!IsCrossDataSource`".

### A-2 Gate in `CatalogSchemaModel.BuildAsync`: omit before any name allocation
In both loops, directly after the existing "target table exists" and "join keys present" checks, add:

```csharp
if (!RelationExecutability.IsSameExecutionSource(meta.Metadata, targetTable.Metadata)) { omitted++; continue; }   // forward
if (!RelationExecutability.IsSameExecutionSource(other.Metadata, meta.Metadata))      { omitted++; continue; }   // reverse
```

The `continue` happens **before** `SanitizeGraphQlName`, `usedRelationFieldNames` and the `_rel`/numeric suffix logic, so an omitted relation never consumes a field name and the names of the remaining (same-source) relations are identical to today for all schemas without cross-source relations (and stable when an unrelated cross-source relation is added later). The forward and the reverse side of one relation use the same predicate on the same pair, so they are omitted together (no half-advertised relation).

`TableRelation.AllowCrossDataSource` is intentionally **not** read in this fix: until WP-FED-08 the answer for a cross-source relation is "omit" regardless of the flag, because the runtime cannot execute it even when the flag is set (gap, not a policy decision).

### A-3 Observability instead of silence
`BuildAsync` already takes an optional `ILogger`. When `omitted > 0` it logs **one** Information line per build: `GraphQL catalog schema: {Count} relation(s) between different data sources are not exposed; cross-source GraphQL relations are not delivered yet.` Counts only: no table names, relation names or source names (they are catalog metadata, but the line is emitted on every schema rebuild and must stay quiet and PII-free). The rebuild check in `CatalogGraphQlTypeModule.cs:85` passes `logger: null`, so only real rebuilds log.

### A-4 Mapping to WP-FED-08 (no duplicated work)
| Item | This fix (BUG-75) | WP-FED-08 (later) |
|---|---|---|
| `RelationExecutability.cs` | Created with `IsSameExecutionSource` | Extended: `IsExecutable(relation, parentMeta, childMeta, IFederationGate, transport)` = `IsSameExecutionSource` **or** (`relation.AllowCrossDataSource` and `gate.IsEnabledFor("GraphQL")`); the MCP helper reuses it |
| `CatalogSchemaModel` relation loops | The two `continue` guards (A-2) | Guards call the extended predicate; `BuildAsync` gets the gate as parameter; `Fingerprint()` includes the federation state and the flag |
| Schema cache key | unchanged (omission is deterministic from the catalog, the fingerprint already follows the emitted relations) | adds the federation state |
| `GovernedTreeQueryService.cs:274-277` | unchanged (stays as defense in depth; its message stays) | lines 223-235 region only, per PLAN-FED-01 |
| MCP `search_catalog` / `describe_dataset` | `CatalogGraphQlMap` is derived from the model and is consistent automatically (A-5); `describe_dataset` `join_relations` (SQL-join advice, `McpToolExecutionHandler`) unchanged | switches to the shared helper |
| Tests | `CatalogSchemaModelTests` cases below stay valid and become the same-source regression set | adds flagged/gated cases on top; the cross-source cases of this fix are inverted only for the flagged + gate-on combination |

WP-FED-08's scope shrinks by the guard plumbing; PLAN-FED-01 section 7 WP-FED-08 should note "predicate file and base guards delivered by PLAN-BUG-75-GATING" (change for the lead to apply to PLAN-FED-01 and the master plan, not made here).

### A-5 Introspection, snapshots, MCP map, subscriptions, aggregates: why they stay consistent
- **Introspection / SDL:** fields are produced only from `CatalogTableType.Relations`; no relation, no field, no type reference. A target type is still emitted for its own table (tables are unchanged), so no dangling type name.
- **Fingerprint:** `Fingerprint()` hashes the emitted relations, so deploying the fix changes the fingerprint once (schema rebuild at first check, intended); afterwards stable.
- **`GraphQlTreeBuilder`:** looks fields up in `currentTable.Relations`; an old cached query naming an omitted field is rejected by Hot Chocolate validation (unknown field) before reaching the tree builder.
- **`CatalogGraphQlMap` / MCP `search_catalog`:** builds its own model through `BuildAsync` (`Mcp/CatalogGraphQlMap.cs:196`) and maps `entry.Relations`; automatically consistent with the schema (AC-7 for the GraphQL map part).
- **Aggregate types and subscriptions:** carry no relation fields (section 1), nothing to gate; a test pins this so a future change cannot silently add them.
- **Schema snapshot/baseline tests:** `GraphQLTests.cs` and `CatalogGraphQlSchemaTests.cs` use same-source fixtures (default `SourceName` is empty, equal on both sides) and must stay unchanged; Phase 4 verifies by running them first (they are the regression guard, expected green without edits).

### A-6 Resolver and in-memory reader: keep, do not touch
`CrossSourceRelationResolver`, `ICrossSourceRowReader`, `InMemoryCrossSourceRowReader` and their DI registration stay as they are. They are dead in production but WP-FED-06 rewrites all five files in `Data/Relations` and removes the in-memory reader from production DI (PLAN-FED-01 4.6); deleting them now would create a merge conflict and pre-empt WP-FED-06 while saving no behaviour. The static `TryExtractKey` is live (change detection) and must not move. To keep the dead code honest, the Phase 4 developer adds a one-line `<remarks>` to `CrossSourceRelationResolver` and `InMemoryCrossSourceRowReader` ("Not used by the GraphQL path; replaced by WP-FED-06"); this is comment-only.

### A-7 Behaviour change note
Clients that selected a relation field whose target table lives in a different data source (`SourceName`) get a GraphQL **validation error (unknown field)** instead of the previous runtime error `INVALID_QUERY_OPTION` "Relations across data sources are not supported in one query." (generic message outside Development). Because that query always failed, no working client can depend on those fields. Clients that hard-code such field names in persisted queries must remove them; the fields return when PLAN-FED-01 INC-3 delivers flagged, gated relations. Same-source relations, including relations across domains of one source, are unchanged (names, arguments, behaviour). Release note text goes into the Phase 6 documentation (section 7), not into `docs/`.

---

## 3. Files per Layer and Ownership

Single work package, one developer (about 130 production LOC including comments, about 330 test LOC).

| File | Change | Owner |
|---|---|---|
| `src/Autheris.Application/Sql/Tree/Federation/RelationExecutability.cs` (new) | `IsSameExecutionSource` (A-1) | Dev |
| `src/Autheris.GraphQL/Catalog/CatalogSchemaModel.cs` | two guards, omitted counter, one Information log (A-2, A-3) | Dev |
| `src/Autheris.Application/Data/Relations/CrossSourceRelationResolver.cs`, `InMemoryCrossSourceRowReader.cs` | XML remark only (A-6) | Dev |
| `tests/Autheris.Tests.Unit/GraphQL/CatalogSchemaModelTests.cs` | new cases (section 4) | Dev |
| `tests/Autheris.Tests.Unit/Sql/Tree/RelationExecutabilityTests.cs` (new) | predicate unit tests | Dev |
| `tests/Autheris.Tests.Unit/GraphQL/CatalogGraphQlSchemaTests.cs`, `Mcp/CatalogGraphQlMapTests.cs` | one added case each | Dev |
| `docs/features/f-gov-19-cross-source-graphql-relations.md`, `docs/features/README.md` | Phase 6 only (section 7) | Documentation expert |
| `docs/plans/00-master-plan-overview.md`, `docs/plans/2026-10-16-plan-cross-source-all-consumers.md` | link this plan, note WP-FED-08 scope reduction (lead) | Lead |

Not modified: `GovernedTreeQueryService.cs`, `CatalogGraphQlTypeModule.cs`, `GraphQlTreeBuilder.cs`, `DataApiServiceExtensions.cs`, any persistence, options or Domain file.

---

## 4. TDD Test List (red first)

All unit tests use the existing `CatalogSchemaModelTests` fixtures (`CreateTable` with a `sourceName` parameter added if absent).

1. **RED `BuildAsync_CrossSourceForwardRelation_IsOmitted`**: parent in `SourceName=pg1`, child in `pg2`, relation declared; `Assert.Empty(parentModel.Relations)`. Fails today (relation emitted).
2. **RED `BuildAsync_CrossSourceReverseRelation_IsOmitted`**: same fixture; `Assert.Empty(childModel.Relations)`.
3. **RED `BuildAsync_CrossSourceRelationWithAllowFlag_IsStillOmitted`**: `AllowCrossDataSource = true`; still omitted (documents the interim rule; this case is inverted by WP-FED-08 only when the gate allows GraphQL).
4. `BuildAsync_SameSourceRelation_IsUnchanged`: existing test at `CatalogSchemaModelTests.cs:~215-250` stays green unchanged (regression guard).
5. **RED-then-green `BuildAsync_SameSourceDifferentDomain_RelationIsKept`**: both tables `SourceName=pg1`, `Domain` `sales` vs `crm`; relation present (guards the decision not to reuse `IsCrossDataSource`). Green on today's code; stays green with the fix; fails if the predicate wrongly compares `Domain`.
6. **RED `BuildAsync_OmittedCrossSourceRelation_DoesNotConsumeFieldNames`**: a table has one cross-source relation named `profile` and one same-source relation named `profile` (different targets); the surviving field is `profile` (no `_rel`, no `_2`). Pins the "omit before allocation" order (A-2).
7. **RED `BuildAsync_MixedRelations_OnlyCrossSourceOmitted`**: one table with one same-source and one cross-source relation; exactly one field remains; the cross-source field is absent in the reverse direction too.
8. `BuildAsync_SourceNameComparison_IsCaseInsensitive`: `PG1` vs `pg1` is same source (matches the tree service's `OrdinalIgnoreCase`).
9. `Fingerprint_ChangesWhenCrossSourceRelationIsAddedOnlyIfItIsEmitted`: adding an omitted cross-source relation does **not** change the fingerprint (no needless schema rebuild); adding a same-source relation does.
10. **RED `Schema_CrossSourceRelation_AbsentFromSdlAndIntrospection`** (`CatalogGraphQlSchemaTests`): build the Hot Chocolate schema from the module with a cross-source relation; the SDL of the parent type contains no relation field; a query selecting it fails validation ("field does not exist") rather than reaching execution.
11. `Schema_AggregateAndSubscriptionTypes_HaveNoRelationFields` (same file): pins section 1 for aggregates and subscriptions.
12. **RED `CatalogGraphQlMap_CrossSourceRelation_NotListed`** (`CatalogGraphQlMapTests`): map entry `Relations` empty for the cross-source pair.
13. **RED `BuildAsync_OmittedRelations_LogsCountOnce`**: `FakeLogger`; exactly one Information entry containing the count, no table, relation or source names.
14. **Parity `RelationExecutability_MatchesTreeServiceRule`** (`RelationExecutabilityTests`): table-driven over {same source, different source, different `DataSourceType`, different `Domain` same source, case difference}; the expected values are the conditions of `GovernedTreeQueryService.cs:268-277`; one additional case runs `GovernedTreeQueryService` for a different-source pair and asserts the existing `INVALID_QUERY_OPTION` message, so predicate and runtime check cannot drift apart.

Existing suites that must stay green without edits: `GraphQLTests`, `CatalogGraphQlSchemaTests` (existing cases), `CatalogGraphQlMapTests` (existing), `CatalogMaskedFieldsExtensionTests`, `MustFixRemediationTests`, `RowFilterChannelParityTests` GraphQL tree cases, `CrossSourceRelationsAndChangesSinceTests` (resolver tests are untouched).

---

## 5. Sequencing

Single developer, strict order: (1) tests 1-3, 6, 7, 10, 12, 13 red; (2) `RelationExecutability` + guards, green; (3) tests 5, 8, 9, 11, 14 and regression run of the existing GraphQL suites; (4) A-6 comments. Lead compiles and tests once (Release, warnings as errors) after the developer returns, per the standing project rule. No dependency on other work packages; can merge before PLAN-FED-01 INC-1.

---

## 6. Risks

| Risk | Mitigation |
|---|---|
| Predicate drift between schema and tree service (the schema hides what the runtime would accept, or the reverse) | Same-rule parity test (14); WP-FED-08 keeps one shared predicate; tree service check stays as fail-closed backstop. |
| Wrongly using `IsCrossDataSource` (with `Domain`) would remove working cross-domain relations | A-1 uses a separate predicate; test 5. |
| Relation field names of remaining relations shift | Omit before allocation; tests 6, 7. |
| A persisted client query breaks | Intended; it already failed at runtime. Behaviour note A-7. |
| Cached schema keeps old fields until rebuild | Fingerprint changes at deploy; the module rebuilds on the next check; restart also rebuilds. |
| Dead resolver code looks live and gets "fixed" by a later change instead of WP-FED-06 | Remarks (A-6), this plan and PLAN-FED-01 4.6 are linked from the master plan. |
| Federation gate not available at WP-FED-08 build time (async or scoped) | Out of scope; `BuildAsync` gets an optional parameter then; the base guards here do not depend on it. |

---

## 7. Phase 6 Documentation Note (F-GOV-19 correction)

`docs/features/f-gov-19-cross-source-graphql-relations.md` must describe only current behaviour (no progress or WIP notes inside `docs/`):

- Retitle and rewrite section 1.2: relations are exposed in the GraphQL schema only when both tables belong to the same data source; relations between different data sources are not part of the schema and cannot be queried through GraphQL. `TableRelation.AllowCrossDataSource` exists in the domain model but has no effect on the schema.
- Remove or relocate to `doc/plan/` the unbacked material: the sequence diagram (section 2), the `CrossSourceRelations` config block (`MaxNestedRowBudget` does not exist; the real option `MaxNestedRows` is not bound), the `where: { customer: ... }` example (4.1), and the `CROSS_SOURCE_*` error table (5) that GraphQL never surfaces. Keep a short section "Limitations" listing: tree queries run as one statement per data source; a relation across sources is omitted from the schema; same-source relations across domains work.
- Update `Components` (the resolver is not used by the GraphQL path) and the feature title in `docs/features/README.md:76`.
- The full F-GOV-19 content (batched key lookup, key clearness, anti-oracle, provenance) is rewritten in Phase 6 of PLAN-FED-01 after INC-3; this fix only makes the document truthful.
- Add the user-facing behaviour change (A-7) to the changelog/release notes location the project uses.
- The lead updates `doc/plan/00-master-plan-overview.md` (link to this plan, bug status) and PLAN-FED-01 section 7 (WP-FED-08 scope note, A-4).

---

## 8. Security Notes for Phase 3

1. **Fail-closed direction:** the change only removes fields from the schema; it cannot widen access. The runtime rejection at `GovernedTreeQueryService.cs:274-277` and the per-table access resolution stay as the backstop, so a regression of the gate degrades to today's runtime error, never to a cross-source read.
2. **No existence oracle:** relation fields vanish for **all** callers equally, regardless of their access to either table; the gate depends on catalog metadata only (source names), never on the caller. This does not worsen the existing property that the schema lists all tables to all authenticated callers (already out of scope here). Phase 3 should confirm that the Information log line (counts only) and the generic validation error leak nothing beyond what the schema already reveals.
3. **Do not read `AllowCrossDataSource` yet:** honouring the flag before WP-FED-06 would re-open advertising of unrunnable relations; honouring it before the federation gate exists would bypass `CrossSourceOptions.Enabled`/`AllowedTransports`. Phase 3 verifies the flag is not referenced in `src/Autheris.GraphQL` after the change.
4. **Resolver left registered:** it is reachable only through DI resolution that no production code performs; Phase 3 should confirm no endpoint or MCP tool resolves `ICrossSourceRelationResolver`, and that the in-memory reader (empty unless seeded) cannot return data in production. If doubt remains, the safer variant is removing only the DI registration lines, at the price of a conflict with WP-FED-06 (owner decision, Q-2).
5. **Schema-time vs request-time consistency:** metadata changes (a table moved to another `SourceName`) are picked up by the fingerprint check; between rebuilds a stale schema may still list a relation that the runtime check then rejects (fail closed). Accepted.
6. **MCP descriptions:** `describe_dataset` `join_relations` may still list cross-source joins (valid for the SQL federation pipeline); its alignment is WP-FED-08, not part of this fix.

---

## 9. Open Questions for the Owner

- **Q-1** PLAN-FED-01 4.8 phrases the guard as `!IsCrossDataSource`. This plan uses a narrower "same data source connection" predicate (source name and type, no `Domain`) so no working relation disappears. Confirm, and align WP-FED-08 text and the resolver's `IsCrossDataSource` semantics (should `Domain` count at all for the later federated path).
- **Q-2** Keep the unwired resolver registration (default, no conflict with WP-FED-06) or remove the two DI lines now.
- **Q-3** Information log on every schema rebuild with omitted relations: keep (default) or downgrade to Debug.

---

## 10. Changelog

| Revision | Date | Change |
|---|---|---|
| 1 | 2026-10-16 | Initial Phase 2 plan (PROPOSED) |
