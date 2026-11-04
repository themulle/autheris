# Plan: Catalog-owned minimum group size for non-personal tables (issue #448)

- PRD: [2026-11-03-req-issue-448-catalog-min-group-size.md](2026-11-03-req-issue-448-catalog-min-group-size.md) (REQ-MGS-01..12; PRD defaults taken apply unchanged).
- Phase: 2 (architecture). Verified against `main` @ `50d89ec9`.
- Autheris is not in production: breaking changes to internal signatures, response DTOs and persisted shape are allowed.

## 1. Verified components (current code)

1. `GovernedAggregationService.Buckets.cs`: instance `ResolveEffectiveMinGroupSize(query, metadata)` (l. 477) resolves `Privacy.DefaultMinGroupSize` (or 1 without explicit options), then `TableMinimumGroupSizes[key]`, then `Privacy.Datasets[key].MinGroupSize` (dataset wins), then calls the static overload (l. 461) that applies `metadata.EffectiveMinGroupSize` and `query.MinGroupSizeOverride` with `Math.Max` (SEC-112-02).
2. `GovernedAggregationService.cs`: one resolved value feeds `AggregationNegationGuard.Enforce` (l. 180), `NoisyCacheKey.Build` (l. 213, value already part of the key), bucket suppression (l. 235-265), avg sensitivity (l. 572), `AggregationMetadata` (l. 346) and the audit dictionary `minimum_group_size` (l. 363). `SemanticMetricQueryService.cs` l. 350 copies it; manifest metric/measure values arrive as `MinGroupSizeOverride` (l. 291).
3. `AnalyticsOptionsValidator.cs` l. 45-70: floors < 2 need `Privacy.AllowDisabled` (MISC-10) for default, table and dataset values.
4. Domain: `Table.MinGroupSize` (`TableModels.cs` l. 162) and `TableMetadata.MinGroupSize` with `EffectiveMinGroupSize => MinGroupSize ?? Table.MinGroupSize` (l. 426-431). Column flag: `TableColumn.IsSensitive` only; the single "catalog-sensitive" definition is `TableAccessDecision.IsCatalogSensitive(column, metadata)` (IsSensitive or a masking rule). dbt column meta `pii=true` only creates a masking proposal (`DbtMetadataIngestionService.cs` l. 411-420).
5. **Correction to PRD REQ-MGS-10:** no `min_group_size` column exists. `TABLES` and `TABLE_CATALOG_SETTINGS` (baseline DDL) have no group-size field and no code reads or writes `Table.MinGroupSize`/`TableMetadata.MinGroupSize` from persistence; both are only copied (`CatalogGovernanceRatchet.cs` l. 32/64/154, dbt l. 626/1330). Today the catalog value is effectively dead outside tests. This plan adds persistence for both values.
6. Governance-owned overlay: `DatasetCatalogStore` keeps tier and tags in `TABLE_CATALOG_SETTINGS`, separate from `TABLES` because ingestion upserts `TABLES`; edits go through `DatasetCatalogAdminService.ApplyAsync` (REST `CatalogApiEndpoints.cs`, MCP `McpAdminTools.cs` l. 472) with required audit (`AuditEventTypes.CatalogVisibilityChanged`/`CatalogTagsChanged`, before/after, actor, source).
7. Migrations: forward-only steps `src/Autheris.Infrastructure/Persistence/Migrations/Steps/{sqlite,postgresql,sqlserver}/NNNN_<name>.sql` + `.shape.json` (embedded, `-- guard:` header, shape ops incl. `addColumn`). Latest step today: `0004_physical_nonunique_indexes`. v1.5.0 baseline is frozen.

## 2. Decisions

1. **Two layers, governance over source.** `Table.MinGroupSize` + new `Table.NonPersonal` (bool, default false) are source-owned (dbt), stored in `TABLES`. `TableMetadata.MinGroupSize` + new `TableMetadata.NonPersonal` (bool?) are governance-owned, stored in `TABLE_CATALOG_SETTINGS` and applied by `DatasetCatalogStore.Apply`. New `TableMetadata.EffectiveNonPersonal => NonPersonal ?? Table.NonPersonal`. This realises REQ-MGS-04 "governance wins over dbt" with the existing `EffectiveMinGroupSize` pattern.
2. **Resolution algorithm** (replaces both overloads; static overload becomes `internal static EffectiveMinGroupSize Resolve(TableMetadata, AggregationQuery, OperatorFloor, AnalyticsPrivacyOptions, ILogger)`, returning `readonly record struct EffectiveMinGroupSize(int Value, MinGroupSizeSource Source)`; enum `Default|Operator|Catalog|Request` in `AggregationModels.cs`):
   1. `floor = Default` (or 1 without explicit options); table then dataset override sets `floor` and `source = Operator`, `operatorExplicit = true` (order unchanged).
   2. `c = metadata.EffectiveMinGroupSize`: if `c > floor` raise, `source = Catalog`. If `c < floor` and `!operatorExplicit` and `IsCatalogLoweringEligible(metadata, c)`: `floor = c`, `source = Catalog`. Otherwise ignored; log `Debug` with the failed condition (no values from rows).
   3. `query.MinGroupSizeOverride > floor` raises, `source = Request` (covers manifest metric/measure values; REQ-MGS-05 unchanged).
   4. Return `Math.Max(1, floor)`. Equal values never change the source.
   `IsCatalogLoweringEligible` = `Privacy.AllowCatalogLowering` AND `metadata.EffectiveNonPersonal` AND `c >= Privacy.CatalogMinGroupSizeFloor` AND no column with `TableAccessDecision.IsCatalogSensitive` AND NOT `Table.IsHighlySensitive` (default taken: four-eyes / high sensitivity tables also block lowering; fail-closed). Evaluated per query (REQ-MGS-07).
3. **Options/validator.** `AnalyticsPrivacyOptions.AllowCatalogLowering` (bool, false) and `CatalogMinGroupSizeFloor` (int, 1). Validator: `CatalogMinGroupSizeFloor` in `1..SemanticManifestValidator.MaxMinGroupSize`, else fail (REQ-MGS-12). Catalog lowering to 1 does not need `AllowDisabled` (REQ-MGS-08); MISC-10 checks for default/table/dataset stay unchanged.
4. **Shared value rules.** New `src/Autheris.Application/DataCatalog/Services/CatalogMinGroupSizeRules.cs`: `TryParseMinGroupSize(string?, out int?, out string? error)` (integer `1..MaxMinGroupSize`; negative/zero/non-integer = error) and `TryParseNonPersonal(string?, out bool?)` (`true|false|1|0`, case-insensitive). Used by governance edit and dbt ingestion.
5. **Persistence (forward-only step).** One step `NNNN_catalog_min_group_size` per dialect; the step number is assigned at implementation time (next free after the latest merged step, currently `0004`), because DDL changes merge serially. Columns: `TABLES.min_group_size` int NULL, `TABLES.non_personal` bool NOT NULL DEFAULT false; `TABLE_CATALOG_SETTINGS.min_group_size` int NULL, `TABLE_CATALOG_SETTINGS.non_personal` bool NULL. Types: SQLite `INTEGER`; PostgreSQL `integer`/`boolean`; SQL Server `INT`/`BIT` (named default constraint per dialect convention). Guard header checks column absence; `.shape.json` with four `addColumn` ops. Baseline schema classes and `governance-shape.*.v0001.json` stay untouched.
6. **Governance edit + audit.** `DatasetCatalogEdit` gains `string? MinGroupSize` (`"none"` clears) and `bool? NonPersonal`; `DatasetCatalogSettings` gains `int? MinGroupSize, bool? NonPersonal`. New `IGovernanceRepository.SetDatasetPrivacyAsync(table, int? minGroupSize, bool? nonPersonal, Sid actor, ct)` implemented via `DatasetCatalogStore.SetPrivacyAsync` (same `MutateAsync`/insert-or-update pattern as `SetVisibilityAsync`; insert keeps `before.Visibility`). Same authorization as visibility edits. Audit: new `AuditEventTypes.CatalogMinGroupSizeChanged = "CATALOG_MIN_GROUP_SIZE_CHANGED"` (PRD default 5: catalog audit, distinct name), `RecordRequiredAsync` with before/after, actor, source; no write when unchanged. If the table is `non_personal` and has a catalog-sensitive column, the edit succeeds but the result carries a warning (REQ-MGS-07).
7. **Metadata cache.** The privacy overlay change uses the same invalidation path as the visibility change (verify call site in the three `*GovernanceRepository.Catalog.cs` at implementation); a test asserts the next aggregation sees the new value.
8. **dbt meta ingestion.** Model meta keys `autheris.min_group_size` / `autheris.non_personal`, accepted flat (`"autheris.min_group_size"`) or nested (`autheris: {min_group_size, non_personal}`, parsed from the JSON text `JsonValueText` stores). dbt owns the `Table` layer: key present = value, key absent = cleared (null / false; fail-closed). Invalid value: warning, field cleared. A model with any column meta `pii=true` (or the existing email heuristic) gets `non_personal=false` plus a warning. Applied in the two `new Table { ... }` sites (l. 626, l. 1330) and `TryRegisterMissingTableAsync` (l. 790). A changed value records `CatalogMinGroupSizeChanged` with source `dbt`; when `_auditWriter` is null the two fields keep their stored value and a warning is added (fail-closed: no unaudited change).
9. **Other catalog syncs.** `CatalogGovernanceRatchet.Merge`: `MinGroupSize` keeps `Math.Max` (connectors may only raise); `NonPersonal = exTable.NonPersonal` (not owned by OpenMetadata/DataCatalog/OpenAPI connectors, SEC-RATCHET-01); `TableMetadata.NonPersonal` kept from existing (governance overlay). l. 154 keeps existing `MinGroupSize`/`NonPersonal`.
10. **Response/audit metadata.** `AggregationMetadata` gains `MinGroupSizeSource MinimumGroupSizeSource`; audit dict adds `min_group_size_source` (lower-case enum name); `SemanticMetricQueryService` l. 350 copies it. GraphQL: `minimumGroupSizeSource: String!` on aggregate and metric result types (`AggregateGraphQlTypes.cs`, `MetricGraphQlTypes.cs`, both mappers). REST serialises the record unchanged.
11. **Out of scope (defaults taken):** governance-file import (`IngestGovernanceStreamAsync`) does not carry the fields; REQ-MGS-11 deferred; noise and small-group strategy unchanged.

## 3. Work packages (TDD; unit tests in `tests/Autheris.Tests.Unit`, integration in `tests/Autheris.Tests.Integration`)

**WP-1 Domain, options, resolution, response metadata.** Files: `Domain/Model/TableModels.cs`, `Domain/Options/AnalyticsOptions.cs`, `Domain/Audit/AuditEventTypes.cs` (constant only), `Application/Data/Services/AnalyticsOptionsValidator.cs`, `Application/Analytics/AggregationModels.cs`, `GovernedAggregationService.Buckets.cs`, `GovernedAggregationService.cs`, `SemanticMetricQueryService.cs`, `ISemanticMetricQueryService.cs`, `Application/DataCatalog/Services/CatalogMinGroupSizeRules.cs` (new), `GraphQL/Catalog/{Aggregate,Metric}GraphQlTypes.cs`, `{Aggregate,Metric}ResultMapper.cs`. Tests (`Analytics/CatalogMinGroupSizeResolutionTests.cs`, `Data/AnalyticsOptionsValidatorCatalogFloorTests.cs`, `DataCatalog/CatalogMinGroupSizeRulesTests.cs`):
- `Resolve_NonPersonalEligibleCatalogOne_ReturnsOneWithCatalogSource` (AC1), `Resolve_CapabilityOff_KeepsDefault` (AC2), `Resolve_NotNonPersonal_KeepsDefault` (AC3), `Resolve_SensitiveColumn_KeepsDefault`, `Resolve_MaskingRuleColumn_KeepsDefault`, `Resolve_HighlySensitiveTable_KeepsDefault` (AC4), `Resolve_OperatorTableOverride_Wins` / `Resolve_OperatorDatasetOverride_Wins` (AC5), `Resolve_RequestOverrideOne_StaysDefault`, `Resolve_RequestOverrideEight_RaisesWithRequestSource` (AC6), `Resolve_CatalogBelowCeiling_KeepsDefault` (AC7), `Resolve_CatalogRaise_AlwaysApplies`, `Resolve_GovernanceOverlayBeatsSourceValue`, `Resolve_NoExplicitOptions_UnchangedBehaviour`.
- `Aggregate_LoweredFloor_ReturnsSmallGroupsAndSourceCatalog`, `Aggregate_LoweredFloor_NegationGuardSkipped`, `NoisyCacheKey_DiffersBetweenLoweredAndDefault` (AC8), `Aggregate_AuditContainsMinGroupSizeSource`, `Metric_ResultCarriesMinGroupSizeSource`, GraphQL `minimumGroupSizeSource` field test.
- Validator: `CatalogFloorZero_Fails`, `CatalogFloorAboveMax_Fails`, `CatalogLoweringWithoutAllowDisabled_Passes`. Rules: parse valid/negative/zero/non-integer/over-max; bool variants.
- Existing `Security/Issue112SecurityConditionsTests.cs` and `Analytics/AggregationPrivacyHardeningTests.cs` stay green (AC11); adapt only call signatures.

**WP-2 Persistence + migration step.** Files: `Persistence/Migrations/Steps/{sqlite,postgresql,sqlserver}/NNNN_catalog_min_group_size.sql` + `.shape.json` (new), `{Sqlite,PostgreSql,SqlServer}GovernanceRepository.Catalog.cs` and `.Schema.cs` (TABLES read/write), `DatasetCatalogStore.cs` (overlay read/apply, `SetPrivacyAsync`), `Domain/Model/DatasetCatalogModels.cs`, `Application/Interfaces/IGovernanceRepository.cs`, `Application/DataCatalog/Services/CatalogGovernanceRatchet.cs`. Tests: unit `Persistence/` Sqlite round-trip `UpsertTable_PersistsMinGroupSizeAndNonPersonal`, `SetDatasetPrivacy_OverlayAppliedOnRead`, `SetDatasetPrivacy_Unchanged_ReturnsNoChange`, `Ratchet_KeepsNonPersonalAndRaisesMinGroupSize`; step lint/shape tests pick up the new files; integration `GovernanceUpgradeTestsBase` subclasses (SQL Server, PostgreSQL) plus SQLite: fresh install and upgrade from the previous version end with the four columns and defaults (AC10).

**WP-3 Governance edit + audit (after WP-2).** Files: `Governance/Interfaces/IDatasetCatalogAdminService.cs`, `Governance/Services/DatasetCatalogAdminService.cs`, `Api/Endpoints/CatalogApiEndpoints.cs` (bind new fields), `Mcp/Tools/McpAdminTools.cs`. Tests (`Governance/DatasetCatalogPrivacyEditTests.cs`): `Apply_SetNonPersonalAndMinGroupSize_AuditsBeforeAfter` (AC9), `Apply_InvalidMinGroupSize_InvalidRequest`, `Apply_Unchanged_NoAudit`, `Apply_Unauthorized_Denied`, `Apply_NonPersonalWithSensitiveColumn_Warning`, `Apply_AuditWriteFails_NothingCommitted`, `Apply_Then_Aggregate_SeesNewFloor`.

**WP-4 dbt meta ingestion (after WP-2; parallel to WP-3).** Files: `Extensions/Dbt/DbtMetadataIngestionService.cs` (and a small `DbtCatalogPrivacyMeta.cs` helper if extraction keeps the service under its size). Tests (`Dbt/DbtCatalogMinGroupSizeIngestionTests.cs`): `Ingest_FlatKeys_SetsTableValues`, `Ingest_NestedAutherisObject_SetsTableValues`, `Ingest_KeyRemoved_ClearsValues`, `Ingest_InvalidValue_WarnsAndClears`, `Ingest_PiiColumn_ForcesNonPersonalFalse`, `Ingest_Change_AuditedWithSourceDbt`, `Ingest_NoAuditWriter_KeepsStoredValues`, `Ingest_GovernanceOverlay_StillWins`, `Ingest_DryRun_NoWrite`.

## 4. Shared files and ordering
- WP order: WP-1, WP-2, then WP-3 and WP-4 in parallel (disjoint files; `AuditEventTypes` and the rules class land in WP-1).
- Sibling plans (#443-#450): migration step numbers and `IGovernanceRepository.cs` / `*GovernanceRepository.Catalog.cs` may collide; rebase and renumber the step at implementation. `TableModels.cs`, `AnalyticsOptions.cs` and `GovernedAggregationService*.cs` are hot files: keep edits additive.
- DI composition snapshots (`tests/Autheris.Tests.Unit/DependencyInjection/Snapshots`) only change if a new service is registered (none planned).

## 5. Risks
1. Floor 1 removes differencing protection for that table; mitigated by four independent gates, operator opt-in, required audit and per-query re-evaluation. Noise (if enabled) still applies; avg sensitivity grows at floor 1 (expected).
2. Stale metadata cache after an overlay change would apply an old floor; covered by D7 test.
3. dbt last-sync-wins on the `Table` layer lets a manifest change re-lower after a raise; acceptable because lowering still needs `non_personal`, operator opt-in and is audited; governance overlay can pin a value.
4. SQL Server `BIT NOT NULL DEFAULT` needs a named constraint to keep shape comparison stable.

## 6. Traceability
| REQ | Decision | WP / tests |
|---|---|---|
| MGS-01, 02 | D2 | WP-1 resolution tests (AC1-4, 7) |
| MGS-03, 05 | D2 | WP-1 `Resolve_Operator*`, `Resolve_RequestOverride*` (AC5, 6, 11) |
| MGS-04 | D1, D4, D8 | WP-3, WP-4 `Ingest_GovernanceOverlay_StillWins` |
| MGS-06 | D6, D10 | WP-1 audit/response tests, WP-3 audit tests (AC9) |
| MGS-07 | D2, D6, D8 | `Resolve_SensitiveColumn_*`, `Apply_NonPersonalWithSensitiveColumn_Warning`, `Ingest_PiiColumn_*` |
| MGS-08 | D3 | `CatalogLoweringWithoutAllowDisabled_Passes` |
| MGS-09 | §1.2 | `NoisyCacheKey_DiffersBetweenLoweredAndDefault`, negation-guard test (AC8) |
| MGS-10 | D5 | WP-2 migration/upgrade tests (AC10) |
| MGS-11 | D11 | deferred |
| MGS-12 | D3, D4 | validator and rules tests |

## Phase 3 security review

**Verdict: APPROVED WITH CONDITIONS** (Phase 3, `csharp-security-expert`; code facts checked against `main` @ `50d89ec9`; no code edited).

### Threat model (summary)
| # | Threat | Assessment |
|---|---|---|
| T1 | Lowering for personal data (mis-flag or malicious `non_personal`) | Four gates + operator opt-in are sound but depend on column flags being complete; see SEC-MGS-03/04. |
| T2 | dbt meta as untrusted lowering source (a model author can switch off small-group suppression with one meta key) | Plan lets dbt alone satisfy gates (a) and value. Source authors are a lower trust tier than governance. See SEC-MGS-02. |
| T3 | Precedence confusion (dbt vs governance vs operator) | Operator > governance > dbt is correct; `NonPersonal=false` overlay must pin off (SEC-MGS-02). |
| T4 | Pii flag or sensitive column added after non_personal | Per-query evaluation covers it; unknown/empty column set must fail closed (SEC-MGS-03). |
| T5 | Differencing/negation with floor 1 | Accepted by design for explicitly flagged non-personal tables; guard skipped only when resolved floor <= 1 (SEC-MGS-06, residual R1). |
| T6 | Cache key / stale metadata | Value already in `NoisyCacheKey`; stale metadata cache is the real risk (SEC-MGS-07). |
| T7 | Audit integrity (silent lowering, unaudited dbt change) | D6/D8 already audit; add source to aggregation audit; no-audit = no change (SEC-MGS-05). |
| T8 | Connector sync (OpenMetadata/DataCatalog/OpenAPI) lowering or clearing | Ratchet keeps `Math.Max` and existing flags; must never lower (SEC-MGS-08). |
| T9 | Input parsing (overflow, culture, huge value, "none") | Shared parser; strict invariant integer (SEC-MGS-09). |
| T10 | Migration safety | `non_personal NOT NULL DEFAULT false` and nullable `min_group_size` make existing rows fail closed; guard header + shape check OK (SEC-MGS-10). |
| T11 | Multi-table queries (joins, semantic metrics) taking the floor of the wrong table | Floor must not be lowered by an eligible table when another table in the query is not (SEC-MGS-11). |

### Binding conditions (fail-closed)
- **SEC-MGS-01** Capability default off: `AllowCatalogLowering=false` and `CatalogMinGroupSizeFloor=1`; with the capability off no catalog value below the operator/default floor is ever applied, and any parse/option/metadata error resolves to the unlowered floor (never to 1).
- **SEC-MGS-02** dbt must not be able to lower alone. Lowering is effective only when `non_personal=true` comes from the governance overlay (`TableMetadata.NonPersonal == true`); a dbt-sourced `Table.NonPersonal` is a proposal that is surfaced (warning/response of the catalog) but not honoured, unless the operator sets a separate option `AllowSourceDeclaredLowering` (default false, validated, documented). Overlay `NonPersonal=false` always pins lowering off regardless of dbt. The catalog `MinGroupSize` value itself may come from either layer (governance wins).
- **SEC-MGS-03** Eligibility is evaluated per query from freshly resolved metadata and fails closed: lowering is refused when the column list is null/empty/unloaded, when any column satisfies `TableAccessDecision.IsCatalogSensitive`, when the table is `IsHighlySensitive`, or when table-level classification/tags carry a pii/sensitive marker already recognised by the catalog. Any exception during evaluation = no lowering.
- **SEC-MGS-04** `non_personal` never loosens anything else: it must not bypass masking, row filters, tier/visibility checks, `AllowDisabled`-gated operator floors, or request-time raise-only rules (REQ-MGS-05); only the numeric floor is affected.
- **SEC-MGS-05** Audit integrity: every change of `non_personal` or `min_group_size` (governance, MCP, REST, dbt) writes `CATALOG_MIN_GROUP_SIZE_CHANGED` via `RecordRequiredAsync` before/with the state change (before/after values, actor Sid, source); write failure aborts the change; dbt without an audit writer keeps stored values (D8). Aggregation audit and response include `min_group_size_source`; audit never contains row values. Edit authorization is at least the one for visibility edits on both REST and MCP paths.
- **SEC-MGS-06** `Resolve` is the single place computing the floor; the same `EffectiveMinGroupSize.Value` feeds negation guard, suppression, avg sensitivity, `NoisyCacheKey` and metadata. The negation guard is skipped only when that value is <= 1; at values >= 2 it still runs.
- **SEC-MGS-07** Metadata cache invalidation on overlay change (D7) is mandatory; if invalidation cannot be verified for a dialect repository, that dialect must read the overlay uncached. Cache key keeps the effective value and also the source.
- **SEC-MGS-08** Connector merges (`CatalogGovernanceRatchet`) may only raise `MinGroupSize`; they never set or clear `NonPersonal` on either layer and never overwrite the governance overlay; a connector-supplied lower value is dropped.
- **SEC-MGS-09** `CatalogMinGroupSizeRules`: invariant culture, `int.TryParse` with `NumberStyles.None`, range `1..MaxMinGroupSize`, reject whitespace/sign/overflow/decimal; `"none"` accepted only on the governance edit input, never from dbt; bool accepts exactly `true|false|1|0`. Invalid = field ignored/cleared with warning, never defaulted to a low value.
- **SEC-MGS-10** Migration: `non_personal` NOT NULL DEFAULT false (named constraint on SQL Server), `min_group_size` NULL; guard checks column absence so re-run is a no-op; no backfill; baseline v1.5.0 untouched; startup/shape check must fail on a missing column rather than treating it as default-lowered.
- **SEC-MGS-11** For a query touching several tables (joins, semantic metrics spanning datasets) the catalog may lower only when every touched table is eligible; the resolved floor is the maximum across touched tables. If multi-table resolution is not implemented, lowering is refused for such queries.
- **SEC-MGS-12** Logs: Debug "lowering refused" logs name the failed condition and table id only; no values, no column data.

### Mandatory security tests (all `[Trait("Category","Security")]`)
| Test | WP |
|---|---|
| `Resolve_CapabilityOff_CatalogOneIgnored`, `Resolve_ResolverError_FailsClosedToDefault` (SEC-01) | WP-1 |
| `Resolve_DbtOnlyNonPersonal_DoesNotLower`, `Resolve_DbtOnlyNonPersonal_LowersOnlyWithSourceDeclaredOption`, `Resolve_OverlayNonPersonalFalse_PinsOff` (SEC-02) | WP-1 |
| `Resolve_EmptyOrUnloadedColumns_DoesNotLower`, `Resolve_TableLevelPiiTag_DoesNotLower`, `Resolve_PiiFlagAddedAfterLowering_RevertsNextQuery` (SEC-03) | WP-1 |
| `Aggregate_LoweredFloor_MaskingAndRowFiltersStillApply` (SEC-04) | WP-1 |
| `Apply_ChangeAuditWriteFails_ChangeAborted`, `Ingest_NoAuditWriter_KeepsStoredValues`, `Apply_MinGroupSizeViaMcp_RequiresSameAuthorizationAsVisibility`, `Aggregate_AuditCarriesSourceWithoutRowValues` (SEC-05, 12) | WP-3, WP-4, WP-1 |
| `Aggregate_FloorTwo_NegationGuardStillEnforced`, `NoisyCacheKey_SameValueDifferentSource_DoesNotShare` (SEC-06, 07) | WP-1 |
| `Resolve_OverlayChange_NextAggregationSeesNewValue` per dialect (SEC-07) | WP-2 |
| `Ratchet_ConnectorLowerValue_Dropped`, `Ratchet_ConnectorCannotSetOrClearNonPersonal` (SEC-08) | WP-2 |
| `Rules_Overflow_Whitespace_Sign_Decimal_Rejected`, `Ingest_NoneKeyword_Rejected` (SEC-09) | WP-1, WP-4 |
| `Migration_ExistingRows_NonPersonalFalseAndMinGroupNull`, `Migration_Rerun_IsNoOp` per dialect (SEC-10) | WP-2 |
| `Resolve_JoinWithIneligibleTable_UsesMaxFloor` (SEC-11) | WP-1 |

### Accepted residual risks
- R1: At floor 1 on an explicitly flagged non-personal table, differencing/singleton exposure exists by design; bounded by governance-confirmed flag, operator opt-in, audit and per-query re-check.
- R2: A governance admin can mislabel personal data as non-personal; mitigated only by audit and the column-flag gate, not preventable technically.
- R3: dbt last-sync-wins can re-raise or clear values between syncs (fail-safe direction: absent key clears to the stricter state).

### Owner decisions (2026-11-03)
All defaults confirmed by the owner:
1. dbt alone never satisfies `non_personal` (SEC-MGS-02); governance confirmation is required, `AllowSourceDeclaredLowering` (default false) is the operator opt-in.
2. Table-level pii/sensitive tags block lowering as well as column flags; four-eyes and highly sensitive tables block lowering too.
3. Multi-table queries use the maximum floor across the tables involved.
4. Per-column eligibility (only grouped/aggregated columns count) stays a follow-up candidate.
