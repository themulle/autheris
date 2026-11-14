# PLAN-OI-666: dbt sync uses identifier (sources) and alias (models) as physical table name

- Date: 2026-11-14 · Base: `origin/main` @ `c4b0078a` · Issue: [#666](https://github.com/themulle/autheris/issues/666)
- Phase 1 and 2: [`2026-11-13-plan-issues-catalog-consent-correctness.md`](2026-11-13-plan-issues-catalog-consent-correctness.md), section #666 (WP-OI-CAT-1; Q-CAT-1: display name = logical name; Q-CAT-2: no re-key, warning `PHYSICAL_NAME_CHANGED`).
- Phase 3: [`2026-11-13-sec-issues-group-3.md`](2026-11-13-sec-issues-group-3.md), section #666 (SEC-OI-666-1..5); owner decisions [`2026-11-14-owner-decisions-sec-groups-1-9.md`](2026-11-14-owner-decisions-sec-groups-1-9.md) (Q-SEC3-2: reject all colliding nodes). This file records the verification on the base and how each condition is met.
- Origin plan: [`00-master-plan-overview.md`](00-master-plan-overview.md) (plan registry).
- Freeze (PRD 5.1): bug fix. SEC-OI-666-1 needs the dbt unique id on the catalog entry, which no entry records today; this PR adds one internal, nullable column (governance step 0012, PostgreSQL and SQL Server). It is not part of any API, DTO or option. The PR asks the owner whether this needs `freeze-exception`. Target <= 400 net lines in `src/`.

## Verified on the base

- The parser reads only `name`, `database`, `schema` (`DbtArtifactStreamingParser.cs:120-122` models, `:316-318` sources). `DbtModelDefinition.ToTableIdentifier()` builds `(Database, Schema, Name)` and is the only identity the sync uses (`DbtMetadataIngestionService.cs:312, 350, 364, 370, 413`; `DbtCatalogTableFactory.cs` `Identifier`).
- No catalog entry records a dbt unique id. `Table` has no such field; `DocumentationSource` is a free text source label and is not suitable. The governance identity lookup is case-insensitive (`LOWER(source_name/schema_name/table_name)`), so collisions are compared case-insensitively. The newest governance step is 0011.
- An existing entry is updated by every sync that hits its identity, whatever registered it (`DbtMetadataIngestionService.cs` ~600-740). Duplicates within one artifact are first-wins today (`CatalogCreationRun.Seen`, skip reason `DUPLICATE`).
- New entries already get `DisplayName = model.Name` (`DbtCatalogTableFactory`), so Q-CAT-1 holds for created entries once `TableName` becomes the physical name.
- Relations: unique-id path exact; legacy name path resolves only when one model matches (`DbtRelationModelLookup`, AMBIGUOUS_MODEL otherwise).
- SQL endpoint auto-sync: `SqlEndpointLoader.SyncDbtModelDefinitionToFile(name, schema, ...)` uses `name` both as endpoint file name and as quoted `FROM` target (`QuoteDbtIdentifier`).
- Open work on the same files: no open PR. The owner branch `feat/simpl-14-casbin-pr4` (no PR) changes `DbtMetadataIngestionService.cs` in the model policy proposal block and in proposal apply; no overlap with this change expected beyond a rebase.

## Deviation from Phase 3: entries without a recorded unique id

SEC-OI-666-1 allows an upsert onto an existing identity only when the entry carries the same dbt unique id. After step 0012 every existing entry has none (NULL), including the entries dbt registered itself. Read literally, the next sync would reject every existing table. Owner decision 2026-11-14 (Robin): an entry without a recorded unique id is updated, and the node's unique id recorded, **only when the node's physical name equals its logical name** (no differing alias or identifier, i.e. the identity the sync used before this PR). When the physical name differs from the logical name, an entry without a unique id or with another unique id rejects the node with `PHYSICAL_NAME_COLLISION`. So an alias or identifier can never take over a hand-registered entry or another model's entry; a node without alias behaves as before. **The Phase 3 reviewer is asked to re-approve this in the PR.**

## Design

1. `DbtModelDefinition.PhysicalName` (init, default `Name`). The parser sets it from `identifier` (sources) or `alias` (models, seeds, snapshots). Absent, JSON null or empty/whitespace falls back to `name`; any other value is used as is and validated (step 3). `relation_name` is not parsed.
2. `ToTableIdentifier()` uses `PhysicalName`. The relation `ByName` fallback indexes `Name` and `PhysicalName`; more than one model for a name stays AMBIGUOUS_MODEL (skipped, warning). The unique-id path is unchanged.
3. `PhysicalName` passes the same identifier checks as `name` today (`ToTableIdentifier` and `DbtCatalogTableFactory.IsValidCatalogIdentifier`), skip reason `INVALID_IDENTIFIER`, never a fallback to `name`.
4. Governance step 0012 (`dbt_unique_id`, nullable `VARCHAR`/`NVARCHAR` bounded like other identifiers, forward-only, PostgreSQL and SQL Server, shape files, checksums, schema shape tests). `Table.DbtUniqueId` (internal to the catalog model; not mapped to any DTO, GraphQL type, OData model or API response). The repository writes it on insert and keeps the stored value when an update carries none (`COALESCE`), so an admin edit never clears it.
5. Collisions (before any write, order independent): nodes of one artifact that map to one identity (case-insensitive) are all rejected with `PHYSICAL_NAME_COLLISION` and their sanitized unique ids (replaces first-wins `DUPLICATE` for this case). An existing entry at the identity: same unique id -> update; NULL and `PhysicalName == Name` (case-insensitive) -> update and record the unique id; otherwise reject the node with `PHYSICAL_NAME_COLLISION`. The new entry created by the sync records the node's unique id.
6. Q-CAT-1: when `PhysicalName != Name` and an updated entry has an empty display name, set it to `Name`.
7. Q-CAT-2: when `PhysicalName != Name` and an entry exists at the logical identity `(Database, Schema, Name)` whose unique id is NULL or the node's, the sync result gets warning `PHYSICAL_NAME_CHANGED` naming sanitized identifiers only. The old entry is neither changed nor reactivated.
8. SQL endpoint auto-sync: file name stays `model.Name`; the `FROM` target uses `PhysicalName`, quoted through `QuoteDbtIdentifier` (a new `table` argument, no string concatenation of raw values).

## Conditions and how they are met

| Condition | Implementation | Tests (Security trait marked S) |
|---|---|---|
| SEC-OI-666-1 no takeover of another origin | design 4 and 5, with the deviation above | S `Alias_EqualToExistingEntryOfOtherOrigin_IsRejected`, S `Alias_OntoEntryWithoutUniqueId_IsRejected`, `NoAlias_EntryWithoutUniqueId_IsUpdatedAndRecordsUniqueId`, `SameUniqueId_IsUpdated`, S `AdminUpdate_KeepsRecordedUniqueId` |
| SEC-OI-666-2 collisions in one artifact | design 5 | S `Alias_CollisionBetweenTwoNodes_RejectsBoth_OrderIndependent`, S `PhysicalName_DoesNotCollapseTwoSourcesOntoOneCatalogEntry_ReportsCollision` |
| SEC-OI-666-3 validation and quoting | design 1, 3, 8 | S `Alias_NullOrEmpty_FallsBackButInvalid_Rejects`, S `Alias_WithInvalidIdentifier_RejectsNode_NoFallbackToName`, S `Identifier_WithQuoteOrSemicolon_NeverReachesGeneratedSql`, `SqlEndpointAutoSync_SelectsFromPhysicalName` |
| SEC-OI-666-4 name fallback | design 2 | `Relationship_ByUniqueId_ResolvesToPhysicalNames`, `Relationship_ByNameFallback_MatchesLogicalOrPhysicalName`, S `Relation_ByNameFallback_Ambiguous_IsSkipped`, `ForeignKeyConstraint_ResolvesToPhysicalName` |
| SEC-OI-666-5 warnings | design 7 | S `Warning_ContainsOnlySanitizedIdentifiers`, `Sync_ReportsPhysicalNameChanged_ForPreviouslyRegisteredLogicalName`, `OldLogicalEntry_IsNeitherChangedNorReactivated` |
| Parser | design 1 | `Source_WithIdentifier_RegistersIdentifierAsTable`, `Source_WithoutIdentifier_FallsBackToName`, `Model_WithAlias_RegistersAliasAsTable`, `Seed_And_Snapshot_WithAlias_UseAlias`, `DisplayName_SetToLogicalName_WhenDifferent` |
| Step 0012 | design 4 | governance upgrade and schema shape tests for PostgreSQL and SQL Server (Testcontainers), API snapshot unchanged |

Test data is neutral (no names from a real catalog).

## Phase 4 and 5

- Phase 4 by a Sonnet developer agent (handover rule), strict TDD: red commit (builds; 31 new tests in `Autheris.Extensions.Tests` and 10 in `Autheris.Tests.Unit` failed on their assertions), green commit; a follow-up test commit sets the new field in the catalog ratchet property guard (the merge starts from the persisted table, so the recorded unique id survives a catalog sync).
- Size: `src/` +178 / -44 (134 net, including the step files); tests +810.
- Deviations from the design above, found while implementing:
  - Snapshots are not parsed by the sync at all (only `model.`, `seed.` and sources), before and after this change; the test covers seeds only.
  - A node whose unique id cannot be stored (empty, longer than 256 characters, control characters) is skipped as `INVALID_IDENTIFIER`.
  - Two nodes with one identity were first-wins `DUPLICATE`; both are now rejected as `PHYSICAL_NAME_COLLISION` (Q-SEC3-2). The existing test was changed accordingly.
  - The existing-entry read runs at the start of the per-model step, so a rejected node writes nothing (no proposal, lineage, endpoint file, update, relation).
  - Skipped items and warnings of a collision name the sanitized unique id, not the table.
- Phase 5 review (this session) over the diff; no blocking finding. Points disclosed in the PR:
  - An entry that records a unique id rejects every other node, also one without alias. After a dbt project or package rename (new unique ids) the existing entries are rejected until the recorded id is cleared, and no API clears it (by design: an admin update keeps it).
  - `PHYSICAL_NAME_CHANGED` costs one extra catalog read per aliased node and adds one warning per affected node (a manifest with thousands of aliased sources yields thousands of warnings in the first sync).
  - The `DUPLICATE` branch of the creation step is no longer reachable from the sync.
- Verification: build 0 warnings; Extensions.Tests 267 passed; unit 16,797 with only the 3 known time-zone failures after the ratchet fixture commit; architecture 547 passed, 2 skipped; integration (governance steps, schema shape, upgrade, step 0012 contract on PostgreSQL and SQL Server, dbt) 145 passed.
- Delivery: UNDER REVIEW (draft PR #748; freeze-exception question and re-approval of the deviation pending).
