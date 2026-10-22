# Plan: Security Findings Area EXT (HIGH / MEDIUM)

Source: `docs/plans/security.txt` (findings EXT-01, EXT-05 to EXT-09, EXT-11). LOW findings (EXT-02/03/04/10, EXT-12 to EXT-19) are out of scope.
Branch: `fix/security-review-open-findings`. All root causes below were re-verified against this branch on 2026-10-22; none is fixed yet, none is wrong.
No production deployment exists: breaking signature and behaviour changes are acceptable (no migrations, no compat shims).

## Work packages and order

| WP | Findings | Scope | Size | Depends on |
|----|----------|-------|------|------------|
| WP1 | EXT-11 | `CatalogGovernanceRatchet.Merge` carries governance fields | S (~150 LOC incl. tests) | none |
| WP2 | EXT-05, EXT-07, EXT-06 | dbt manifest sync and governance import (`DbtMetadataIngestionService`) | L (~600 LOC) | WP1 (reuses ratchet helpers) |
| WP3 | EXT-08 | dbt circuit breaker attribution and tenant keying | M (~350 LOC) | none (touches `DbtMetadataIngestionService` manifest parse: run after WP2 or rebase) |
| WP4 | EXT-09 | custom-query CDC poller watermark-only persistence | S (~120 LOC) | none |
| WP5 | EXT-01 | OData CSDL / service-document cache key | S (~120 LOC) | none |

Order: WP1 -> WP2 -> WP3 sequentially (shared files); WP4 and WP5 in a parallel lane. One developer per WP, Sonnet TDD agents, batched build/test.

---

## EXT-11 (HIGH) Catalog syncs wipe RequiredFilterColumns (WP1)

**Root cause (verified).** `CatalogGovernanceRatchet.Merge` (`src/Autheris.Application/DataCatalog/Services/CatalogGovernanceRatchet.cs:28-60`) builds a new `Table` with only Id, SourceType, SourceName, SchemaName, TableName, DisplayName, Description, LongDescription, DocumentationSource, Sensitivity, RequiresFourEyes, IsActive, DataSourceType, HttpEndpoint, PluginName. Not carried: `RequiredFilterColumns`, `RequiredFilterColumnGroups`, `MinGroupSize`, `SmallGroupStrategy`, `RlsExpression`, `Visibility`, `Tags`, `Location`, `PhysicalDatabaseName`, `EnforcementMode`, `Texts`, `ExpectedRows`. The returned `TableMetadata` (lines 104-142) carries only Table, Identifier, Columns, ColumnMaskingRules, PrimaryKeyColumns, Indexes, ReplaceKeyDefinition, dropping `OwnerTenantId`, `RowFilterEvaluation`, `CatalogView`, `CatalogTags`, `ReleaseProfileToCatalogViewers`, `ProfilePublicationPolicy`, `PredefinedSyntheticSamples`, `MinGroupSize`, `SmallGroupStrategy`, `DeletedKeyPolicy`, `ExpectedRows`/`EstimatedRowCount`. Upsert then writes NULL for these.

**Fix.**
1. In `Merge`, set on `Table`: `RequiredFilterColumns = Union(ex, in)` (ordinal-ignore-case, stored order first); `RequiredFilterColumnGroups = ex ∪ in` (a group is kept if stored; incoming groups added); `MinGroupSize = Max(ex, in)`; `SmallGroupStrategy`, `RlsExpression`, `Visibility`, `EnforcementMode`, `Location`, `PhysicalDatabaseName` = existing when set, else incoming; `Tags` = union; `Texts`/`ExpectedRows` = incoming ?? existing.
2. On `TableMetadata`: `OwnerTenantId`, `RowFilterEvaluation`, `CatalogView`, `ReleaseProfileToCatalogViewers`, `ProfilePublicationPolicy`, `DeletedKeyPolicy`, `PredefinedSyntheticSamples`, `MinGroupSize`/`SmallGroupStrategy` from existing (governance-owned, catalogs never set them); `CatalogTags` union; `EstimatedRowCount`/`ExpectedRows` incoming ?? existing.
3. Add public helpers reused by WP2: `static ImmutableArray<string> UnionRequiredFilterColumns(ImmutableArray<string> stored, ImmutableArray<string> incoming)` and `static ImmutableArray<ImmutableArray<string>> UnionRequiredFilterColumnGroups(...)`.
4. Guard against future drift: architecture/unit test that reflects over all init properties of `Table` and `TableMetadata` and fails when a property is neither copied by `Merge` nor listed in an explicit `CatalogOwnedProperties` allow-list in the test.

**Fail-closed.** A sync can only add filter requirements, never remove them; unknown/new properties fail the reflection test.

**Red tests** (`tests/Autheris.Tests.Unit/Catalog/CatalogGovernanceRatchetTests.cs`): `Merge_KeepsStoredRequiredFilterColumns_WhenIncomingHasNone`, `Merge_UnionsRequiredFilterColumnGroups`, `Merge_KeepsMinGroupSizeAndSmallGroupStrategy`, `Merge_KeepsOwnerTenantAndReleasePolicies`, `Merge_CopiesOrAllowListsEveryTableAndMetadataProperty` (reflection). Integration: `DataCatalogSync_KeepsRequiredFilterColumns` (`tests/Autheris.Extensions.Tests`), same for OpenMetadata and OpenAPI re-ingest.

**Breaking.** None externally; catalog syncs no longer clear governance fields.

---

## EXT-05 (HIGH) dbt manifest sync relaxes governance and writes cross-tenant (WP2)

**Root cause (verified).** `IngestManifestStreamAsync(Stream, bool, TenantId?, ct)` (`DbtMetadataIngestionService.cs:268`) upserts directly (`:575`): `RequiredFilterColumns = ParseMetaRequiredFilterColumns(model.Meta) ?? stored` (`:501`; an empty meta value returns `ImmutableArray.Empty` at `:1724`, clearing them), `colMeta = matchingCol?.Meta ?? c.Meta` (`:538`, full replace), `PredefinedSyntheticSamples = model.SyntheticSamples ?? stored` (`:561`). Target table is `model.ToTableIdentifier()` (`:323`); the tenant is used only for lineage (`:481`). Endpoint `/api/extensions/dbt/sync` admits `DbtAdmin` (`DbtEndpoints.cs:31-32`).

**Fix.**
1. Signature (breaking): `Task<DbtSyncResult> IngestManifestStreamAsync(Stream manifestStream, DbtGovernanceImportCaller caller, bool dryRun = false, CancellationToken ct = default)`; same for `IngestManifestFileAsync`. Endpoint builds the caller as the governance endpoint does (`IsClusterAdmin` from role).
2. Tenant binding: new private `bool IsTenantAllowed(TableIdentifier id, DbtGovernanceImportCaller caller)` = `caller.IsClusterAdmin || id.Domain equals caller.Tenant.Value (OrdinalIgnoreCase)`. Domain `default` is shared (visible to all tenants in OData) and allowed only for ClusterAdmin or `TenantId.LegacySingleTenant` (default taken). Models failing it are skipped entirely (no metadata, keys, relations, REBAC structure tuples, lineage) and reported in a new `DbtSyncResult.RejectedModels` list; relations whose parent or child is rejected are dropped.
3. Ratchet semantics (manifest = mirror, like SEC M-32): new `DbtManifestRatchet` (internal static, same folder) with
   - `RequiredFilterColumns = CatalogGovernanceRatchet.UnionRequiredFilterColumns(stored, incoming)`, groups likewise (an empty meta value no longer clears);
   - column meta merge: start from stored meta, overlay incoming keys except protected keys `pii`, `is_pii`, `sensitivity`, `release_value_list`, `release_allowed_values`, `classification_review`; protected keys may only tighten (pii false->true, sensitivity rank up via `Table.SensitivityRank`); `release_*` keys may never be added or widened by the manifest;
   - `PredefinedSyntheticSamples`: never replaced when stored is non-empty;
   - keys: unchanged SEC-94-11 behaviour (replace only with explicit intent).
   Each suppressed relaxation is reported in `DbtSyncResult.SuppressedRelaxations` (table, field, reason) and logged; the stored value stays. Relaxations go through `/dbt/governance` (GovernanceAdmin) or the dataset API.

**Fail-closed.** Unknown tenant/domain -> skip; parse error of a protected meta value -> keep stored value; dry run reports the same rejections.

**Red tests** (`tests/Autheris.Tests.Unit/Dbt/DbtManifestGovernanceTests.cs`, new): `ManifestSync_EmptyRequiredFilterMeta_KeepsStoredFilters`, `ManifestSync_DropsPiiMeta_KeepsStoredPii`, `ManifestSync_AddsReleaseValueList_IsSuppressed`, `ManifestSync_ForeignTenantDatabase_RejectsModelAndRelations`, `ManifestSync_DefaultDomain_RequiresClusterAdmin`, `ManifestSync_ClusterAdmin_MayTargetForeignTenant`, `ManifestSync_TighteningPii_IsApplied`. Endpoint test: `DbtSyncEndpoint_PassesCallerTenantAndClusterAdminFlag`.

**Breaking.** Interface signature; `DbtSyncResult` gains two lists; existing tests calling `IngestManifestStreamAsync(stream, dryRun, tenant)` are updated.

---

## EXT-07 (MEDIUM) Governance import table updates not tenant-bound, arbitrary fallback (WP2)

**Root cause (verified).** `IngestGovernanceStreamAsync` checks tenants only for virtual filters/profiles (`:929-940`). Tables resolve via `ParseTableIdentifier(table, schema, database)` (`:972`) and, if missing, `GetAllTablesAsync().FirstOrDefault(schema+name match)` across all domains (`:987-994`).

**Fix.** Apply `IsTenantAllowed` (EXT-05) to the parsed `tableId` before lookup: foreign domain -> add rejection "Table '{id}': tenant '{domain}' is not the caller's tenant; only a ClusterAdmin may import into another tenant." (whole import rejected via the existing `:1278` gate). Replace the fallback by a lookup restricted to tables passing `IsTenantAllowed`; if more than one matches -> rejection "ambiguous table reference"; exactly one -> use it; none -> existing "missing table" path.

**Fail-closed.** Foreign or ambiguous reference rejects the whole import before any write.

**Red tests** (`tests/Autheris.Tests.Unit/Dbt/DbtGovernanceTenantBindingTests.cs`, new): `GovernanceImport_ForeignDatabase_RejectsWholeImport`, `GovernanceImport_SchemaNameFallback_IgnoresForeignTenantTables`, `GovernanceImport_AmbiguousFallback_Rejects`, `GovernanceImport_ClusterAdmin_MayTargetForeignTenant`.

**Breaking.** Imports with unqualified names that previously hit another tenant's table now fail.

---

## EXT-06 (MEDIUM) Masking ratchet compares only rule tiers (WP2)

**Root cause (verified).** `ApplyMaskingRule` (`:895-907`) rejects only when `MaskingRuleStrength(stored) > MaskingRuleStrength(new)`; all non-NULLIFY/REDACT/HMAC/TOKENIZE rules are tier 1 (`CatalogGovernanceRatchet.cs:143-149`); parameters (`:1184-1231`) are taken verbatim (KeepPrefix/KeepSuffix unbounded).

**Fix.**
1. New `public static bool IsMaskingRelaxation(MaskingRule stored, MaskingRule incoming)` in `CatalogGovernanceRatchet`: false if incoming tier is higher; true if lower; at equal tier false only when RuleType is equal and every disclosure parameter is equal or stricter: `KeepPrefix`/`KeepSuffix`/`FixedLength`-revealed chars not larger, `Decimals` not larger, `RadiusMeters` not smaller, and `PatternOrFormat`, `Replacement`, `MaskChar`, `HmacKeyId`, `Mode`, `TokenDomain` equal. Everything else (incl. type change inside a tier) is a relaxation.
2. `ApplyMaskingRule` and the check at `:1650` use `IsMaskingRelaxation`; relaxation -> existing rejection text.
3. Parameter validation at parse (`:1184-1231`): reject the import when `KeepPrefix`/`KeepSuffix` < 0 or `KeepPrefix + KeepSuffix > MaskingRule.MaxRevealedChars` (new const = 16, default taken; covers the built-in IP_ANONYMIZE_SUBNET = 9).
4. `CatalogGovernanceRatchet.Merge` (line 94) keeps strict `>` (equal tier keeps stored), no change needed.

**Fail-closed.** Any non-provably-stricter change is a relaxation; invalid params reject the import.

**Red tests** (`tests/Autheris.Tests.Unit/Catalog/MaskingRelaxationTests.cs`, new): `PartialMaskReplacingMaskEmail_IsRelaxation`, `LargerKeepPrefix_IsRelaxation`, `GeoJitterMoreDecimals_IsRelaxation`, `TokenizeDifferentDomain_IsRelaxation`, `SameRuleSameParams_IsNotRelaxation`, `HigherTier_IsNotRelaxation`, `GovernanceImport_KeepPrefixAboveMax_Rejected`, `GovernanceImport_PartialMaskOverMaskEmail_RejectsImport`.

**Breaking.** Imports that changed tier-1 rules directly now need the proposal workflow.

---

## EXT-08 (MEDIUM) dbt circuit breaker misattributes and is not tenant-keyed (WP3)

**Root cause (verified).** `GetTableHealthAsync` (`DbtHealthCircuitBreaker.cs:26-42`) falls back to the first unordered state with equal name or `StartsWith(name + "_")`; `ResetTableHealthAsync` (`:226`) uses the same prefix rule; `RecordRunResultsAsync(Stream, ct)` stores every state as `default.default.<model>` (`:212`); `ParseTestUniqueId` (`:262-298`) splits at the last underscore.

**Fix.**
1. New `IDbtTestTargetMap` (Application, singleton, in-memory, per tenant): `void Replace(TenantId tenant, IReadOnlyDictionary<string, TableIdentifier> modelToTable, IReadOnlyDictionary<string, IReadOnlyList<string>> testToModels)`; `bool TryResolveTest(TenantId, string testUniqueId, out IReadOnlyList<TableIdentifier>)`; `bool TryResolveModel(TenantId, string modelUniqueId, out TableIdentifier)`. Filled by the manifest sync (WP2 code path) from test nodes' `attached_node` / `depends_on.nodes`, only for models passing `IsTenantAllowed`.
2. Signature (breaking): `RecordRunResultsAsync(Stream runResultsStream, TenantId tenant, CancellationToken ct = default)`; endpoint passes `EndpointSecurity.GetRequestTenant(context)`. Model and test results resolve through the map to full `TableIdentifier`s; a failing test with several parents quarantines all of them.
3. Unresolvable test failure (no manifest yet / unknown id): fallback candidate match against the tenant's known model names (every model `m` with remainder starting `"<test_type>_" + m + "_"` or equal), quarantine all candidates (over-quarantine is fail-closed); zero candidates -> `DbtRunResultsReport.UnattributedFailures` + warning log, no state change.
4. `GetTableHealthAsync`/`ResetTableHealthAsync`: exact key only; remove name/prefix fallback. Delete `ParseTestUniqueId` last-underscore logic.

**Fail-closed.** Ambiguity quarantines more, never fewer, tables; states never cross tenants.

**Red tests** (`tests/Autheris.Tests.Unit/DbtHealthCircuitBreakerTests.cs`): `TestFailure_WithUnderscoreColumn_QuarantinesAttachedModel`, `GetTableHealth_DoesNotMatchPrefixedTable`, `GetTableHealth_IsDeterministic_WithSimilarNames`, `RunResults_TenantA_DoesNotQuarantineTenantB`, `UnresolvableTest_QuarantinesAllCandidates`, `UnresolvableTest_NoCandidate_ReportedUnattributed`, `ResetTableHealth_ExactOnly`. Plus `DbtWebhookReceiverTests` and `DbtHealthExecutionMiddlewareTests` updated for full identifiers.

**Breaking.** Interface signature; states keyed by real identifiers.

---

## EXT-09 (MEDIUM) Custom-query poller never persists vector on all-duplicate batch (WP4)

**Root cause (verified).** `PollStreamBatchAsync` (`CustomQueryPollerService.cs:158-162`) returns 0 when `sourceRows.Count == 0` without persisting `nextVector`, despite the comment.

**Fix.** When `sourceRows.Count == 0 && !nextVector.Equals(vector)`: append `new CdcAppendBatch(stream.Id, "query_poller", tableId, fencingToken, stream.HeadOffset, NewWatermark: now, [], [], WatermarkState: nextVector.ToJson())`. If the vector did not advance, call `TouchPolledAsync` and log a warning "poller batch fully duplicate without vector progress" (detects a non-advancing query). Verify that all four `ICdcStreamStore.AppendAsync` implementations accept an empty event list (InMemory does: `HeadOffset + 0`); fix PostgreSQL/SQLite/SQL Server cores if they assume `Events.Count > 0`. Requires `MultiWatermarkVector` value equality (add if missing).

**Fail-closed.** Fencing token and expected head offset still enforced; a stale owner gets `FencingTokenRejectedException`.

**Red tests** (`tests/Autheris.Tests.Unit/Cdc/CustomQueryPollerServiceTests.cs`): `AllDuplicateBatch_PersistsAdvancedVector`, `AllDuplicateBatch_NextPollReadsFromAdvancedVector`, `AllDuplicateBatch_StaleFencingToken_Rejected`. Store contract test `AppendAsync_EmptyEvents_UpdatesWatermarkStateOnly` (InMemory + SQLite unit; PostgreSQL/SQL Server via Testcontainers).

**Breaking.** None.

---

## EXT-01 (MEDIUM) OData CSDL cache key ignores column visibility (WP5)

**Root cause (verified).** `GetMetadataCsdlAsync` (`ODataHandler.cs:56-57`) keys by tenant, structure version and a hash over table identifiers only; the cached XML is built from per-caller column-filtered metadata (`:177-187`, Deny columns removed, keys restricted). Service document key (`:85-86`) has the same shape (table names only, so not a leak there).

**Fix.** New private static `string ComputeShapeHash(IReadOnlyList<TableMetadata> tables)`: SHA-256 over, per table in ordinal identifier order, `identifier \0 column names (ordinal, in emitted order) \0 PrimaryKeyColumns \0 index key columns \n`. CSDL key becomes `odata:csdl:{tenant}:s{structureVersion}:{shapeHash}`. Service document key stays (table set only). Generator input and hash input are the same `tables` instance (no second authorization pass).

**Fail-closed.** Any difference in visible columns or keys yields a different key; no hash collision handling needed beyond SHA-256.

**Red tests** (`tests/Autheris.Tests.Unit/OData/ODataCsdlCacheIsolationTests.cs`, new): `Csdl_UserWithDeniedColumn_DoesNotReceiveCachedFullSchema`, `Csdl_FullAccessUser_DoesNotReceiveCachedReducedSchema`, `Csdl_SameVisibility_SharesCacheEntry`.

**Breaking.** None.

---

## Dependencies on other areas

- EXT-02 (LOW, OData tables cache keyed by tenant+SID) is adjacent to WP5 but out of scope; WP5 must not depend on the tables cache being role-aware.
- WP1 changes are consumed by DataCatalog, OpenMetadata and OpenAPI ingestion (area EXT only); no GOV/PERS repository change needed (persistence already writes the fields).
- WP3 depends on the WP2 manifest-parsing changes (test nodes, tenant check).

## Security conditions for Phase 3

1. No catalog, manifest or governance import path can lower RequiredFilterColumns/Groups, sensitivity, pii, release_* meta or masking disclosure without the proposal/dataset-API path (tests above are mandatory).
2. Every dbt write path (manifest, governance, run results) is bound to the caller tenant; only ClusterAdmin crosses tenants; domain `default` requires ClusterAdmin or legacy single tenant.
3. Masking comparison is disclosure-based: anything not provably stricter is a relaxation.
4. Circuit-breaker ambiguity resolves to more quarantine, never to Healthy; no cross-tenant state.
5. CSDL cache keys cover the full per-caller shape (columns and keys).
6. CDC watermark-only appends keep fencing and head-offset checks.
7. Reflection test guards `Merge` against future governance-field drift.
