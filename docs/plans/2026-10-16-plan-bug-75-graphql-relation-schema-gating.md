# Implementation Plan: BUG #75 - GraphQL Schema Must Not Advertise Cross-Source Relations That Cannot Run

**Document ID:** `PLAN-BUG-75-GATING` (revision 1, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [REQ-FED-01](2026-10-16-req-cross-source-all-consumers.md) (matrix row 5, FR-7, AC-7, rejected anti-pattern 8), [PLAN-FED-01](2026-10-16-plan-cross-source-all-consumers.md) (4.7 WP-FED-07, 4.8 WP-FED-08), issue #75 / F-GOV-19
**Branch:** `fix/bug-75-graphql-relation-schema-gating` (base `main` `83853479`)
**Parent Plans:** [00-master-plan-overview.md](00-master-plan-overview.md), [PLAN-FED-01](2026-10-16-plan-cross-source-all-consumers.md)
**Status:** PROPOSED, Phase 3 security review done: APPROVED WITH CONDITIONS (next: Phase 4 TDD with conditions C-1..C-6 of the Phase 3 section)

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
| 1.1 | 2026-10-16 | Phase 3 security review appended (APPROVED WITH CONDITIONS, SEC-B75-01..07) |


---

## Phase 3 Security Review (2026-10-16)

**Reviewer:** `csharp-security-expert`. **Code base:** worktree at `83853479` plus plan commit. All claims below verified against the code.

### Threat model

| Asset | Threat | Relevance to this fix |
|---|---|---|
| Governance of cross-table reads (consent, row filters, masks, tenant filter per table) | A relation between two tables of different data sources executed as one statement or as an ungoverned lookup (federation gate bypass, REQ-FED-01 FR-7) | Core. The fix must never make such an execution possible and must not diverge from the runtime rule. |
| Catalog topology | Schema or error text as an existence/topology oracle (which tables share a source, which relations exist) | Low. The schema is global (not per caller): `BuildAsync` takes `GetAllTablesAsync` filtered by `IsActive` and `DataSourceType.Sql` only (`CatalogSchemaModel.cs:111,118`), so every authenticated caller already sees all tables and relations. Omission applies equally to all callers and depends on catalog metadata only. It adds no oracle: a missing field says only "not a same-source relation", derivable from nothing the caller could not already infer from the table list. |
| Availability / consistency | Schema advertising unrunnable relations; stale schema | Medium-low; fail closed. |
| Dead code | Registered but unused resolver/reader resolved later by mistake | Low, see SEC-B75-05. |

Direction of the change is fail closed: it only removes schema fields, and the runtime backstop stays (`GovernedTreeQueryService.cs:268-277`).

### Verification of the specific questions

1. **Predicate vs runtime check.** The runtime check compares only `Metadata.Table.SourceName` with `OrdinalIgnoreCase` against the ROOT table's name, for every table of the tree (`GovernedTreeQueryService.cs:267-277`), after a per-access `DataSourceType == Sql` check (`:270-273`). Because every pair in a tree is compared against the same root, "all equal root" is equivalent to pairwise equality, so a pairwise predicate `DataSourceType` + `SourceName` (OrdinalIgnoreCase) matches the check for single hops and for multi-hop chains (A(pg1)->B(pg1)->C(pg2) is omitted at B->C, and could never run). No case where the schema hides something the runtime accepts. **But the runtime applies a third condition the predicate lacks:** `access.Metadata.Dialect != dialect` of the connection provider throws `InvalidOperationException` (`:292-298`). Two tables with the same `SourceName` but different `Table.SourceType` (dialect) therefore pass the predicate and fail at runtime (see SEC-B75-01). Also `SourceName` empty on both sides is "equal" and the runtime then throws "has no database connection" (`:279-283`); that is fail closed but advertised (SEC-B75-03).
2. **Case-insensitivity / bypass.** The connection map is `new(StringComparer.OrdinalIgnoreCase)` (`src/Autheris.Domain/Options/Storage/DatabaseOptions.cs:23`), so `PG1` and `pg1` resolve to the SAME connection at runtime (`GovernedTreeQueryService.cs:279`); two distinct connections that differ only by case cannot exist. Every other source-name comparison in the code base is also OrdinalIgnoreCase (`GovernedSqlRewriter.cs:432,441`, `GovernancePolicyFactory.cs:86,204`, `GovernedCatalogProvider.cs:267`, `CrossSourceRelationResolver.cs:59`). Treating names that differ only by case as one source is therefore correct and cannot cause a cross-connection join. Whitespace or Unicode variants are not normalised anywhere, they compare as different and are omitted (safe direction).
3. **Single source of truth.** Required. Today the runtime check is inline (`:274`) and the plan keeps it inline plus a parity test. A parity test alone does not prevent drift. See SEC-B75-02: the runtime must call the same predicate.
4. **Cross-domain relations on one connection.** Governed: `columnsByTable` is collected for every table of the tree and each table goes through `ResolveOnceAsync` + `AuditOnceAsync`; one denial fails the whole query with "Access denied." before any SQL (`:244-260`); tenant filter, masks and row filters are compiled per table (step 3, `:300ff`). Domain is a catalog label, not a security boundary of the connection, so keeping these relations is not a bypass. Using `IsCrossDataSource` (with `Domain`) would remove working, governed relations, so A-1 is right.
5. **Per caller or global; oracle.** Global (see threat model). Omission leaks nothing new. Showing the relations leaks relation names and join column names of tables the caller may not read; that is a pre-existing property (type names, column names and relations of all active SQL tables are in the schema) and unchanged by this fix. Recommend tracking it separately (SEC-B75-06).
6. **Cache invalidation.** `Fingerprint()` hashes emitted relations and per-table type, columns (`CatalogSchemaModel.cs:61-83`); it does NOT hash `SourceName`. Because the guard decides emission from `SourceName`, a table moved to another source changes the emitted relations and therefore the fingerprint. The version-provider short circuit (`CatalogGraphQlTypeModule.cs:70-78`) skips the fingerprint check when the catalog version is unchanged, which is correct as long as a catalog edit bumps the version (existing behaviour). A stale schema between rebuilds is rejected at runtime (fail closed). OK.
7. **Introspection, persisted queries, aggregates, subscriptions, MCP.** Relation fields are emitted only from `table.Relations` (`CatalogGraphQlTypeModule.cs:234-253`); `_aggregate` has only `groupBy`/`where` string arguments and a fixed result type (`:272-278`) and no relation path in the schema; MCP map reads `entry.Relations` from a model built by `BuildAsync` (`Mcp/CatalogGraphQlMap.cs:196,46,146`). Persisted/stored documents are validated against the current schema, so an omitted field fails validation (A-7). All consistent. Residual gap: aggregate `groupBy`/`where` are free strings; whether they accept dotted relation paths is not covered by this fix (SEC-B75-07).
8. **Unwired resolver and stub reader (Q-2).** `rg` over `src` shows the only non-test references to `ICrossSourceRelationResolver`/`ICrossSourceRowReader`/`InMemoryCrossSourceRowReader` are the two DI lines (`DataApiServiceExtensions.cs:25-26`); nothing in `Autheris.GraphQL`, MCP or REST resolves them, the `GovernedTreeQueryService` constructor does not take them. The in-memory reader returns only seeded rows (empty in production). A future developer injecting the resolver would get an in-memory reader returning an empty set: no ungoverned data, but also a silent false "no related rows" (a correctness trap, not a leak). Risk is low but non-zero and is removed by WP-FED-06 (SEC-B75-05).
9. **Logging (Q-3).** Count-only line is PII-free and consistent with the existing line at `CatalogSchemaModel.cs:125` (which logs names). Fine.

### Findings

| ID | Severity | Evidence | Finding and required change |
|---|---|---|---|
| SEC-B75-01 | Medium | Predicate (plan A-1) vs `GovernedTreeQueryService.cs:292-298`; `TableModels.cs:37,92` (`Dialect` derives from `Table.SourceType`) | The predicate omits the dialect condition the runtime also enforces. Same `SourceName`, different `SourceType` is advertised and then fails with `InvalidOperationException` (not even a GatewayException, so a 500-class error). Not a bypass, but the predicate's doc comment claims "exact condition checked". **Change:** `IsSameExecutionSource` additionally requires equal `Table.SourceType` (OrdinalIgnoreCase) or equal parsed `Dialect`. Add it to parity test 14. |
| SEC-B75-02 | Medium | Runtime check inline at `GovernedTreeQueryService.cs:268-277`; plan A-4 keeps it inline | Two copies of the rule can drift (the plan's own top risk). **Change:** the tree service must call `RelationExecutability` (single source of truth) for the type + source check, keeping the existing exception types and messages byte-identical (`GatewayInvalidQueryException`, same texts). The dialect-vs-provider check at `:292-298` stays (it depends on the connection). This is a small edit and does not conflict with WP-FED-08 (which only extends the predicate). Update plan sections 3 and A-4 ("Not modified: GovernedTreeQueryService.cs" no longer holds). |
| SEC-B75-03 | Low | `TableModels.cs:38` default `string.Empty`; `GovernedTreeQueryService.cs:279-283` | Empty/whitespace `SourceName` on both sides is "same source" and the runtime then fails with "no database connection". Existing fixtures rely on the empty default, so the predicate must keep treating empty==empty as equal. **Change:** none to the predicate; add a test pinning empty==empty as same (documents the decision) and note the runtime fail-closed error. Never treat `null`/empty as "wildcard equal to any source" (empty vs non-empty must be different). |
| SEC-B75-04 | Low | Plan test 8, `CatalogSchemaModel` guards | Case-insensitive treatment is correct (verified above), but it must be pinned by a test that mixed case is the SAME source and that the runtime accepts the same pair (see S-3). Guards must also never cache or normalise the name beyond `OrdinalIgnoreCase` (no `ToLower` culture variants, no trimming). |
| SEC-B75-05 | Low | `DataApiServiceExtensions.cs:25-26` | Registered-but-unused resolver with in-memory stub reader. Not exploitable now. **Change:** keep as planned (Q-2) plus the remarks in A-6; additionally add an architecture/guard test that no type in `Autheris.GraphQL`, the MCP assembly or `GovernedTreeQueryService` depends on `ICrossSourceRelationResolver` (reflection on constructor parameters), so a later accidental wiring fails CI. WP-FED-06 must remove the in-memory reader from production DI. |
| SEC-B75-06 | Info (pre-existing, out of scope) | `CatalogSchemaModel.cs:111-118` | Schema is global: relation and join-column names of tables a caller cannot read are visible to every authenticated caller. Not worsened by this fix. **Recommendation:** open a backlog item (per-caller schema or documented stance); do not mix into this fix. |
| SEC-B75-07 | Low | `CatalogGraphQlTypeModule.cs:272-278` | `_aggregate(groupBy: [String], where: String)` are free-text; the plan only pins "no relation fields in the type". **Change:** add one test that `groupBy`/`where` containing a relation path of an omitted cross-source relation (for example `partner.name`) is rejected by the aggregate path (existing column validation) and never reaches SQL; if it unexpectedly resolves, raise as a blocking defect. |

### Binding conditions for Phase 4

- **C-1** (SEC-B75-01) Predicate = `DataSourceType` equal AND `Table.SourceType` equal AND `Table.SourceName` equal, all `OrdinalIgnoreCase`/ordinal-ignore-case as appropriate, no `Domain`.
- **C-2** (SEC-B75-02) `GovernedTreeQueryService` calls the same predicate; messages and exception types unchanged; existing tree tests pass unedited.
- **C-3** `AllowCrossDataSource` is not referenced anywhere in `src/Autheris.GraphQL` after the change (grep test or review check); no gate or option is read in the guard.
- **C-4** Guards sit before name allocation, forward and reverse use the same predicate on the (parent, child) pair in that orientation (plan A-2). The predicate is symmetric, so orientation errors cannot flip the result, but tests 1/2 must cover both directions.
- **C-5** Log line carries counts only (no table, relation or source names); do not add the names later at Information level.
- **C-6** (SEC-B75-05) DI guard test for the unwired resolver; no change to DI registrations in this fix.

### Mandatory security tests (in addition to plan section 4)

- **S-1** `RelationExecutability_DifferentDialectSameSourceName_NotExecutable` plus runtime counterpart: tree service on such a pair still fails closed (parity, SEC-B75-01).
- **S-2** `GovernedTreeQueryService_UsesSharedPredicate`: for the table-driven set of test 14 (including `PG1` vs `pg1`, empty vs empty, empty vs non-empty, different `DataSourceType`) the schema omits exactly when the tree service throws the "across data sources" error, and keeps exactly when it proceeds past the source check. Driven by one data table so the two cannot diverge.
- **S-3** `Tree_MixedCaseSourceNames_ExecuteAsOneStatement`: `PG1` and `pg1` pair runs against ONE connection (assert one connection lookup, one statement) and consent/row filter/mask of both tables are applied.
- **S-4** `Tree_CrossDomainSameSource_AppliesGovernancePerTable`: deny on the child table of a cross-domain same-source relation yields "Access denied." with no SQL executed; row filter and mask of the child apply.
- **S-5** `Schema_OmittedRelation_QueryFailsValidationAndNeverReachesTreeService`: a query naming an omitted field produces a validation error; `GovernedTreeQueryService` mock is never invoked; the error text contains neither relation name, target table nor source name.
- **S-6** `Schema_IsIdenticalForAllCallers`: two principals with different access get the same SDL (pins "no per-caller oracle").
- **S-7** `Fingerprint_ChangesWhenTableMovesToOtherSource` (relation set changes -> fingerprint changes), and a stale-schema case: field still present in an old schema, runtime rejects with the existing error (fail closed).
- **S-8** `CrossSourceRelationResolver_NotInjectedByGraphQlOrTreePath` (SEC-B75-05) and SEC-B75-07 aggregate relation-path test.
- **S-9** `MCP_SearchCatalog_DoesNotListOmittedRelation` (test 12) also asserts the MCP GraphQL map text contains no name of the omitted target type.

### Recommendations for open questions

- **Q-1** Confirm the narrower predicate. Ground truth for "same execution unit" is the runtime check, which does not consider `Domain`; a later federated path may still want `Domain` for key-clearness, but that is a separate concern and must not be encoded in the schema predicate. Align PLAN-FED-01 4.8 to `IsSameExecutionSource` (not `!IsCrossDataSource`) and add the dialect condition (C-1).
- **Q-2** Keep the registration (default) with C-6. Removal is also acceptable security-wise but offers no extra protection since nothing resolves it.
- **Q-3** Keep Information (counts only, once per real rebuild). Verified the module passes `_logger` only at the real build (`CatalogGraphQlTypeModule.cs:137`) while the change check and MCP map pass `null` (`:85`, `CatalogGraphQlMap.cs:196`), so no per-poll noise.

### Missed by the architect

- The dialect condition in the runtime check (SEC-B75-01) and the need for a shared predicate call in the runtime (SEC-B75-02).
- The empty-`SourceName` equality semantics (SEC-B75-03) and the aggregate free-text path (SEC-B75-07).

### Verdict

**APPROVED WITH CONDITIONS.** The design is fail closed and cannot widen access. Phase 4 may start once C-1..C-6 and tests S-1..S-9 are incorporated; no further Phase 3 round is needed unless the owner rejects C-2 (shared predicate in the runtime), in which case re-review.
