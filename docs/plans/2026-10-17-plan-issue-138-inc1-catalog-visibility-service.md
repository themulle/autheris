# Implementation Plan: PLAN-CAT-138-INC1 - One Catalog Visibility Service (ReBAC in MCP `list_datasets` / `describe_dataset`)

**Document ID:** `PLAN-CAT-138-INC1`
**Date:** 2026-10-17
**Status:** PROPOSED (Phase 2 complete); next: Phase 3 review; Phase 4 only after tracker step I-7 is merged
**Author:** Solution Architect (Phase 2)
**Source:** [`REQ-CAT-138`](2026-10-17-req-issue-138-catalog-at-scale.md) INC-138-1 (R-138-1.1..1.4, AC-138-1/2, SEC-138-1/2); intake rank 1.7 ([`issue-intake-assessment`](2026-10-17-issue-intake-assessment.md), OQ-1 and OQ-5 accepted)
**Builds on:** [`plan-issue-111`](2026-10-16-plan-issue-111-dataset-tags-catalog-visibility.md) rev. 3 (WP-0 merged as B-1; WP-1..6 = step I-7, conditions F-C1..F-C10, W0-C1..W0-C11)
**Branch (Phase 4):** `fix/issue-138-inc1-catalog-visibility-service`, created from the `main` that contains I-7
**Size:** S-M (PRD said S; the GraphQL `catalog` query and `/datasources` are added so that every discovery surface uses one rule)

---

## 1. Verified Current Behaviour (`origin/main` `b5c1b5dd`, G-PLAN-REBASE)

| # | Surface | Path (file:line) | Consent/tier core | ReBAC today |
|---|---|---|---|---|
| S1 | MCP `list_datasets` | `McpDatasetCatalog.cs:140-160` -> private `VisibleTablesAsync` `:800-807` -> `McpCatalogVisibility.VisibleTablesAsync` `:36-64` -> `CatalogVisibility.VisibleTablesAsync` | yes | **no** (C6b) |
| S2 | MCP `describe_dataset` | `:199-201` -> `FindVisibleAsync` `:785-798` (unknown = hidden = `TableNotFoundException`) | yes | **no** |
| S3 | MCP describe relations | `GraphQlAccessAsync` `:658-672` (second full `VisibleTablesAsync` per describe, `:665`) | yes | **no** |
| S4 | MCP `sample_rows` | `:584-600` -> `FindVisibleAsync` | yes | **no** |
| S5 | MCP `search_catalog` | `McpToolExecutionHandler.cs:146-160` -> `CatalogDiscoveryService.SearchCatalogDetailedAsync` `:193` | yes | yes |
| S6 | MCP resources `autheris://catalog/summary`, `datasets/{id}/schema`, datasources | `McpResourceProvider.cs:51,68,91` -> `CatalogDiscoveryService` | yes | yes (list/detail) |
| S7 | `invoke_api` catalog calls | `ApiDispatcherService.cs:111-151` -> `CatalogDiscoveryService` | yes | yes |
| S8 | REST `GET /api/v1/catalog/datasets`, `/datasets/{id}`, `/search` | `CatalogApiEndpoints.cs:27,39,94` -> `CatalogDiscoveryService.cs:72-77`, `:81-106` (single-table read + one ReBAC check), `:151-156`, `:193-202` | yes (`VisibleAsync` `:331-352`) | yes: `ResolvePermittedAsync` `:296-325`, unbounded `Task.WhenAll` per table `:317-324`; `IsRebacAllowedAsync` `:354-367` uses `context.SubjectId`; an evaluator exception escapes the `WhenAll` |
| S9 | REST/MCP/`invoke_api` `/datasources` | `CatalogDiscoveryService.cs:241-262` (admin classes only, `VisibleAsync`) | yes | no |
| S10 | GraphQL `catalog` query | `QueryTypes.cs:305-470`: own admin branches `:434-445` (`FilterToTenant`), non-admin `FilterForSubject` `:448`; contract filter `:409-412`; `OpenSchema` branch ends `:362` | yes (duplicated logic) | **no** |
| S11 | GraphQL schema types / introspection | `CatalogGraphQlTypeModule` (global schema); introspection off outside Development (`GatewayServiceCollectionExtensions.cs:628-630`, `GatewayStartupValidator.cs:396-397`); data fields gated by `RebacDirectiveType` / `RebacTableGate` | n/a | out of scope (non-goal of REQ-CAT-138 and #111) |
| S12 | DataAccess consumers (#111 inventory rows 7-12): Trino `TrinoMetadataQueryHandler.cs:100`, Arrow `ArrowFlightSqlServer.cs:131`, Iceberg `IcebergRestCatalogFederationService.cs:113`, guardrail `AiDataGuardrailService.cs:296,359`, `SemanticMcpCompiler.cs:116`, `EffectiveAccessExplanationService.cs:64,98` | consent-only by design | ReBAC via `RebacTableGate.IsEnforcedOnQueryPaths` on the query paths | **unchanged** |

ReBAC facts: `IRebacEvaluator.BatchCheckAsync` exists (`IRebacEvaluator.cs:18`); `ZanzibarRebacEvaluator.BatchCheckAsync` (`:285-307`) runs `CheckAsync` per item, stops on cancellation (missing keys), denies cross-tenant items; `CheckAsync` maps internal errors to `Denied` (`:228-231`) and uses the decision cache plus the tenant generation cache (`:135-165`, default 1 s). Registration is unconditional (`GatewaySecurityServiceExtensions.cs:99`). `GetAllTablesAsync` returns active tables only (`SqliteGovernanceRepository.Catalog.cs:244`), `GetTableMetadataAsync` does not filter `is_active`. Bug #127 (scope-aware column levels in `McpDatasetCatalog`, `ResolveColumnLevels`) is on `main` and untouched by this plan.

## 2. Dependency on I-7 (#111 WP-1..6)

INC-1 consumes, and must not duplicate: (a) `CatalogSurface { DataAccess, Discovery }` and the `surface` parameter of `CatalogVisibility.VisibleTablesAsync` (C-2); (b) `TableMetadata.CatalogView` set on every returned row, default `Unmarked` = most restrictive (F-C2); (c) the fresh tier/tag overlay in all three repositories (A-6, F-C6); (d) WP-3 MCP output (`BuildSummaryDescription`, `sample_rows` term `CatalogView == Full`); (e) WP-5 discovery path `VisibleTablesAsync(..., Discovery)` plus ReBAC intersection; (f) GraphQL non-admin `FilterForSubject(Discovery)` with the `SharedTables` gate; (g) `CallerScopeResolver` and `Gateway:Tenancy:Mode` (WP-0); (h) tests T-3 (DataAccess callers not edited), F-C10 allow-list architecture test, `CatalogChannelParityTests` with tier fixtures (F-C7).

**Re-verify after I-7 is merged (first task of Phase 4, recorded in the PR):** final signature and parameter order of `CatalogVisibility.VisibleTablesAsync`/`FilterForSubject`; whether WP-5 already moved the ReBAC loop or added a helper (reuse it, then delete); line anchors of S1-S10; the `CatalogView` values on `Summary` rows; the T-3/F-C10 test classes (extend them instead of adding a parallel test); that `McpCatalogVisibility` still carries only anonymous handling plus surface pass-through.

## 3. Design Decisions

**D-1 Service.** New `Autheris.Application/Catalog/Interfaces/ICatalogVisibilityService.cs` and `Catalog/Services/CatalogVisibilityService.cs` (scoped). It is the only *discovery* visibility entry point and always uses `CatalogSurface.Discovery`; it orchestrates and does not re-implement: `CallerScopeResolver` -> `CatalogVisibility.VisibleTablesAsync(..., Discovery, tenancyMode, SharedTables)` -> ReBAC intersection.
```csharp
public enum CatalogChannel { Rest = 0, GraphQl = 1, Mcp = 2 }      // diagnostics only; never changes the result
public sealed record CatalogCaller(ClaimsPrincipal? Principal, TenantId Tenant, CatalogChannel Channel);
public interface ICatalogVisibilityService
{
    ValueTask<CatalogVisibleSet> GetVisibleAsync(CatalogCaller caller, SchemaContractDefinition? contract = null, CancellationToken ct = default);
    ValueTask<TableMetadata?> FindVisibleAsync(CatalogCaller caller, TableIdentifier table, CancellationToken ct = default);   // null = unknown, inactive or hidden
    ValueTask<IReadOnlySet<TableIdentifier>> FilterVisibleAsync(CatalogCaller caller, IReadOnlyCollection<TableIdentifier> candidates, CancellationToken ct = default);
}
public sealed class CatalogVisibleSet   // Tables, Scope, Contains(id), Find(id); internal AllTables (search hygiene in the same assembly only)
```
**D-2 Inputs.** Principal (caller class, SIDs, groups, roles, token scopes via claims), `CatalogCaller.Tenant` supplied by the channel exactly as today (REST `context.TenantId`, MCP `SafeTenantId(principal)`, GraphQL `principal.GetTenantId()`; the R3-04/W0-C11 difference stays documented, never wider), consents (`IConsentRepository`), #111 tier/tags (rows from the repository overlay), `SharedTables`, tenancy mode, ReBAC. ReBAC subject = `principal.GetUserSid()` (not `RequestContext.SubjectId`), so MCP resources, tools and REST evaluate the same subject; ReBAC tenant = `caller.Tenant`.

**D-3 Fail closed.** Null principal, no SID, the `ANONYMOUS_MCP_CLIENT` SID, `CallerScope.None`, a missing required dependency, or a consent/visibility exception -> empty set / null (logged once, no caller values). ReBAC only shrinks: rows are intersected for every caller class and every `CatalogView` (default taken OQ-1, OQ-2, consistent with #111 WP-5). A ReBAC chunk that throws hides its tables (Warning with the count only, no SID, no table names); a decision absent from the batch result is a deny; `OperationCanceledException` propagates. `FindVisibleAsync` rejects `!Table.IsActive`.

**D-4 Cost, no N+1.** `GetVisibleAsync`: one `GetAllTablesAsync`, one consent query (inside `CatalogVisibility`), ReBAC through `BatchCheckAsync` in chunks of 32 with at most 8 chunks in flight (`Parallel.ForEachAsync`), replacing the unbounded `Task.WhenAll`; the evaluator's decision and generation caches are reused, no new cross-request cache (INC-138-3 adds the epoch-validated bitset cache and turns 32/8 into options). Results are memoised per DI scope keyed by principal reference, tenant and contract, so a request never evaluates twice. `FindVisibleAsync` uses `GetTableMetadataAsync` plus the pipeline on one row (one ReBAC check, as REST detail today); `FilterVisibleAsync` runs the pipeline on the candidate rows only. MCP `describe_dataset` therefore drops from two full catalog evaluations to one single-row plus k relation-target checks.

**D-5 Call-site migration** (each removes its local visibility/ReBAC code):
1. `McpDatasetCatalog`: `ListDatasetsAsync` -> `GetVisibleAsync`; `FindVisibleAsync` (describe, `sample_rows`) -> service `FindVisibleAsync`, unknown and hidden both throw `TableNotFoundException` (R-138-1.3); `GraphQlAccessAsync` relations -> `FilterVisibleAsync(graphQl.Relations targets)` (R-138-1.4). The `IsMcpAuthBypassed` short-circuit stays in `McpDatasetCatalog` (H-02). New required ctor dependency `ICatalogVisibilityService` (null in the legacy test ctor -> empty, W0-C5).
2. `CatalogDiscoveryService`: `ResolvePermittedAsync`, `VisibleAsync`, `IsRebacAllowedAsync` deleted; list/search use `GetVisibleAsync` (`AllTables` for `ShapeHit`, W0-C1 unchanged), detail uses `FindVisibleAsync`, `/datasources` keeps its admin-class gate then uses `GetVisibleAsync` (ReBAC applies, default taken OQ-4). S5-S7 follow automatically.
3. `QueryTypes.GetCatalogAsync`: the three branches `:434-450` become one `GetVisibleAsync(caller, contractDef)`; `ComputeAccessStatus` and `OpenSchema` unchanged (OQ-5).
4. `McpCatalogVisibility`: keeps anonymous handling and serves only the DataAccess consumers (guardrail, `SemanticMcpCompiler`); no discovery caller remains.
5. DI: `GatewayCatalogServiceExtensions` registers `TryAddScoped<ICatalogVisibilityService, CatalogVisibilityService>()`.

**D-6 Architecture rule (AC-138-2).** Cecil test (style of `ConsentQueryTenantArchitectureTests`): calls to `CatalogVisibility.VisibleTablesAsync`/`FilterForSubject`/`FilterToTenant` and `McpCatalogVisibility.VisibleTablesAsync` are allowed only from `CatalogVisibilityService`, `McpCatalogVisibility` and the S12 DataAccess list; `RebacTableGate.ObjectId` with `IRebacEvaluator.CheckAsync` is not referenced by `CatalogDiscoveryService`, `McpDatasetCatalog` or `QueryTypes`. Extends the #111 T-3 test if it already enumerates callers.

## 4. Work Packages (one developer, sequential, one PR)

| WP | Content | Size |
|---|---|:---:|
| WP-1 | Contracts (D-1), `CatalogVisibilityService` (D-2..D-4), DI, unit tests | S |
| WP-2 | Migration D-5.1..D-5.4, adapt existing tests | S-M |
| WP-3 | Parity integration matrix, architecture test D-6, release note | S |

## 5. Test Plan (red first)

- **Unit `Tests.Unit/Catalog/CatalogVisibilityServiceTests.cs`:** `RebacDisabled_EqualsConsentAndTierSet`, `RebacEnabled_IsIntersection_NeverUnion`, `RebacAppliesToAdminClassesAndSummaryRows`, `RebacChunkThrows_ChunkHidden_OthersKept_LogHasNoSidOrTableName`, `MissingBatchDecision_IsDeny`, `Cancellation_Propagates`, `NullPrincipal_NoSid_AnonymousMcpSid_ScopeNone_Empty`, `ConsentRepositoryThrows_Empty`, `UsesBatchCheck_ChunksOf32_AtMostEightInFlight_NoPerTableCheckAsync`, `SameScope_EvaluatesCatalogOnce`, `Memo_NotSharedAcrossPrincipalsTenantsOrContracts`, `FindVisible_UnknownHiddenInactiveRebacDenied_AllNull`, `FindVisible_OneRebacCheck`, `FilterVisible_ChecksCandidatesOnly`, `RebacSubject_IsPrincipalSid`, `SummaryRow_KeepsCatalogViewSummary`.
- **Unit `Tests.Unit/Mcp/McpDatasetCatalogRebacTests.cs`:** `ListDatasets_RebacDeniedTable_AbsentFromPageAndPaging`, `DescribeDataset_RebacDenied_SameErrorAsUnknown`, `DescribeDataset_RelationToRebacDeniedTarget_NotAdvertised`, `SampleRows_RebacDenied_TableNotFound`, `AuthBypass_Unchanged`, `MissingVisibilityService_Empty`. Existing `Issue127ScopeAwareLevelTests`, `SecurityReviewMcpHighTests`, `VisibleTablesTenancyTests` stay green.
- **Unit GraphQL/REST:** `CatalogQuery_RebacDeniedTable_Absent`, `CatalogQuery_ContractFilterApplied`, `CatalogQuery_OpenSchemaUnchanged`; `CatalogDiscoveryVisibilityTests.Search_RebacDenied_NoHitNoCount`, `Datasources_RebacApplies`.
- **Integration `CatalogChannelParityTests` (extend):** `[Theory] VisibleSet_IdenticalAcrossChannels(rebac: false|true)` with two persons (A: consent T1, tuple T1; B: consents T1+T2, tuple T1) over REST list/search/detail, MCP `list_datasets`/`describe_dataset`/`search_catalog`, resource `autheris://catalog/summary`, `invoke_api` list, GraphQL `catalog`; `RebacDeniedTable_AbsentEverywhere_NameCountScoreRelationsTotals`; extend `ReBacEnabledWithoutTuples_ConsentedCallerSeesNothing` to MCP and GraphQL; re-run with the #111 `Hidden/Summary/Full` fixtures (F-C7).
- **Architecture `Tests.Architecture/CatalogVisibilityServiceArchitectureTests.cs`:** `OnlyServiceAndDataAccessAllowList_CallCatalogVisibility`, `DiscoveryCode_DoesNotCallRebacTableGateDirectly`; DI composition test `CatalogVisibilityService_ResolvesForAllDiscoveryConsumers`.

## 6. Files Touched

New: `Application/Catalog/Interfaces/ICatalogVisibilityService.cs`, `Application/Catalog/Services/CatalogVisibilityService.cs`, the test files of section 5. Changed: `Application/Mcp/Services/McpDatasetCatalog.cs`, `Application/Mcp/Services/McpCatalogVisibility.cs`, `Application/Catalog/Services/CatalogDiscoveryService.cs`, `GraphQL/Types/QueryTypes.cs` (`GetCatalogAsync` only), `Api/Extensions/DependencyInjection/GatewayCatalogServiceExtensions.cs`, `tests/Autheris.Tests.Integration/CatalogChannelParityTests.cs`. Not changed: `CatalogVisibility.cs` (core stays a pure function), repositories, DDL, S12 consumers.

## 7. Overlap and Position

| Step | Shared | Handling |
|---|---|---|
| I-7 (#111) | `CatalogVisibility.cs` semantics, `McpDatasetCatalog.cs`, `CatalogDiscoveryService.cs`, `QueryTypes.cs`, parity tests | hard predecessor; section 2 re-verification |
| I-8 (#112) | `QueryTypes.cs` (aggregate fields `:690-702`, other method) | textual rebase only |
| I-22 (#113 A) | `ConsentRequestService.SubmitAsync` visibility check planned on `CatalogVisibility(Discovery)` (`plan-issue-113` line 113) | after INC-1 it must call `ICatalogVisibilityService.FindVisibleAsync` (the D-6 test enforces it); one-line delta for the #113 developer; a ReBAC-denied table then answers the same `FORBIDDEN` as unknown |
| I-23 (#114) | `QueryTypes.cs:540-596` (other resolvers) | none |
| #143, #146 A, #152-154 INC-4, #138 INC-2/3 | scope logic / MCP output / catalog entries / cache | consume this service; INC-3 extends it |

**Recommended position:** new tracker step **I-7a**, immediately after I-7 and before I-8 (wave-1 security slice, OQ-1). Phase 3 review of this plan precedes Phase 4.

## 8. Risks

1. **Behaviour change** in ReBAC-on deployments: MCP and GraphQL callers (admins included) lose tables without `can_query` tuples, as REST already does; release note required (no production yet).
2. **Latency:** describe gains ReBAC checks but loses one full evaluation; cold 5,000-table list is bounded (8x32) instead of unbounded; measured in INC-138-3, not here.
3. **#111 drift:** if I-7 changes signatures, section 2 re-verification adapts D-1 without a second abstraction.
4. **Tenant input per channel** (R3-04) remains; documented, confined, never wider.
5. **Summary discoverability:** ReBAC can hide a `Summary` table a user would otherwise request (OQ-2).

## 9. Open Questions (defaults taken)

| # | Question | Default taken |
|---|---|---|
| OQ-1 | ReBAC for admin caller classes? | Yes (current REST rule, one rule) |
| OQ-2 | ReBAC on `Summary`/`Full` tier rows without consent? | Yes (as #111 WP-5) |
| OQ-3 | Apply the service to DataAccess consumers (S12)? | No; query-path ReBAC stays with `EnforceOnQueryPaths` |
| OQ-4 | `/datasources` under ReBAC? | Yes (shrinks only, admin endpoint) |
| OQ-5 | GraphQL `OpenSchema` mode? | Unchanged (operator opt-in bypass) |
| OQ-6 | Chunk size / parallelism configurable now? | No; constants 32/8, options in INC-138-3 |

---

## Phase 3 Security Review (csharp-security-expert, 2026-10-17)

**Verdict: APPROVED WITH CONDITIONS.** The design is fail-closed in intent, only shrinks the visible set (ReBAC intersects, never unions), keeps the #111 tier core untouched and removes three divergent rules. The conditions below are binding on Phase 4 and need no plan revision. F-C1..F-C10 and W0-C1..W0-C11 are not weakened, provided SEC-CAT138-01..12 hold.

**Code facts verified** (worktree, origin/main lineage): `ZanzibarRebacEvaluator.BatchCheckAsync` (`Security/Rebac/Services/ZanzibarRebacEvaluator.cs:285-307`) breaks on cancellation and leaves keys missing, denies cross-tenant items, and is sequential per call; `CatalogDiscoveryService.IsRebacAllowedAsync` (`:354`) keys on `context.SubjectId`, while the data path (`TableAccessPolicy.Consent.cs:27-28`) keys on `query.UserSid`; the data path honours `IsRebacBypassed` and `EnforceOnQueryPaths`, the discovery path checks only `evaluator.IsEnabled`; `McpDatasetCatalog.GraphQlAccessAsync` (`:658-672`) is the only relation filter.

| # | Finding | Sev |
|---|---|---|
| F1 | Discovery ReBAC uses `IsEnabled`; data path uses `EnforceOnQueryPaths`. Catalog may hide what data allows (acceptable direction), never the reverse; must stay so. | Info |
| F2 | ReBAC subject changes from `RequestContext.SubjectId` to `principal.GetUserSid()` (D-2). If the two differ (impersonation, service tokens), REST list could widen or narrow versus today and versus the data path (`query.UserSid`). | Medium |
| F3 | Per-scope memo keyed on "principal reference" is unsafe if the principal is mutated or replaced mid-scope (token exchange, `IsMcpAuthBypassed` clone) and ignores the consent/ReBAC epoch inside a long-lived scope (MCP session reusing one scope, background hosts). | Medium |
| F4 | `Parallel.ForEachAsync` over a shared `BatchCheckAsync` evaluator: a chunk exception handling that hides only that chunk must also not swallow `OperationCanceledException`; a partial result after cancellation must not be cached or returned as a complete set. | Medium |
| F5 | `FindVisibleAsync` uses `GetTableMetadataAsync` (no `is_active` filter). Plan rejects `!IsActive`; also tenant must be re-checked on the row, not only on the lookup key. | Medium |
| F6 | Relation targets: `FilterVisibleAsync` is the right fix, but `describe_dataset` also exposes names elsewhere (`DatasetAccess`, lineage, semantic/derived references, OData/Arrow paths, error text). Not enumerated. | Medium |
| F7 | Missed discovery surfaces: `DynamicOpenApiGenerator` (OpenAPI per domain, lists tables), OData service document/`$metadata`, `QueryAnalysisService`/`AccessPlanningService`/`BundledConsentRequestService` (table-exists vs forbidden oracle), `CatalogVersionProvider`, Trino/Arrow metadata (S12, consent-only). The plan classifies S12 only. | Medium |
| F8 | Error/timing oracle: describe of a ReBAC-denied table must be byte-identical to unknown (message, code, no extra ReBAC round trip skew visible in `invoke_api` envelopes). | Low |
| F9 | Admin classes under ReBAC (OQ-1): correct and consistent, but a break-glass/operator admin with no tuples loses discovery; operator lockout of the catalog must not block consent administration endpoints. | Low |
| F10 | Architecture test by Cecil call-site allow-list is bypassable via reflection, delegates, lambdas compiled into display classes, or a new private helper in the allow-listed `McpCatalogVisibility`. | Low |
| F11 | Logs: "count only" is right; the memo/diagnostic `CatalogChannel` must not carry SID/tenant values into metrics tags (cardinality and confidentiality). | Low |

### Binding conditions
- **SEC-CAT138-01** Null principal, no SID, `ANONYMOUS_MCP_CLIENT`, `CallerScope.None`, null dependency or any consent/tier/visibility exception -> empty set / `null` / empty `FilterVisible`; never an unfiltered fallback. `FilterVisibleAsync` of an empty candidate list returns empty without calling the repository.
- **SEC-CAT138-02** ReBAC enabled (`evaluator.IsEnabled`) with evaluator null, throwing outside a chunk, or returning a result without a key -> deny. ReBAC disabled -> no ReBAC term. Chunk exception hides that chunk only; `OperationCanceledException` propagates and the partial result is never memoised or returned.
- **SEC-CAT138-03** One ReBAC rule: relation `can_query`, object `RebacTableGate.ObjectId`, tenant = `caller.Tenant` (validated non-empty), subject = `principal.GetUserSid()`. Phase 4 must prove REST subject equality with the former `context.SubjectId` for human and service tokens; if unequal, the stricter of both (both must allow) applies until a decision is logged. The data path subject (`UserSid`) must equal the catalog subject in a test.
- **SEC-CAT138-04** Discovery may hide more than the data path, never less: where `IsRebacBypassed` or `EnforceOnQueryPaths=false` lets data pass, discovery still applies ReBAC when `IsEnabled`; the reverse (catalog shows, data path denies by ReBAC) is a defect, covered by a parity test.
- **SEC-CAT138-05** Memo key = (principal subject SID string + group/role/scope hash used by consent cache, tenant, contract id, surface). Reference equality alone is forbidden. Memo lives only in the scoped service, is not shared with singletons, and is dropped when the scope ends; no cross-request cache in INC-1 (INC-3 must key additionally on consent epoch and ReBAC tenant generation).
- **SEC-CAT138-06** Fixed bounds are hard: chunk 32, parallelism 8, candidate cap = the catalog size already loaded; no per-request override from callers/headers/query args. `FilterVisibleAsync` candidates are de-duplicated and capped (relations cap, for example 256) -> excess is dropped (deny), not evaluated.
- **SEC-CAT138-07** `FindVisibleAsync`/`FilterVisibleAsync` re-check `Table.IsActive` and `row.TenantId == caller tenant` (or shared-table rule) on the loaded row, in addition to consent, tier and ReBAC; `CatalogView` default `Unmarked` still denies.
- **SEC-CAT138-08** `Summary` rows remain exposed only via the #111 Summary DTOs: the service returns `TableMetadata` untouched; no new code path may add columns, samples or `sample_rows` for `CatalogView != Full`. Relation targets in describe are filtered through `FilterVisibleAsync` and every other table name in `describe_dataset` (access paths, lineage, semantic refs) must derive from a visible set; Phase 4 greps `McpDatasetCatalog` for other `TableIdentifier` emissions and lists them in the PR.
- **SEC-CAT138-09** Denied, unknown, inactive and wrong-tenant describe/sample/detail return the identical exception type, code and message (R-138-1.3); no ReBAC wording in errors. Warning logs carry counts only; no SID, tenant, table name or tuple; metrics tags never carry principal or table values.
- **SEC-CAT138-10** Surface completeness: Phase 4 records, in the PR, a disposition (migrated / consent-only by design / out of scope with reason) for `DynamicOpenApiGenerator`, OData service document and `$metadata`, `QueryAnalysisService`, `AccessPlanningService`, `BundledConsentRequestService`, `CatalogVersionProvider`, `EffectiveAccessExplanationService`; any that enumerates table names to a non-admin caller on a Discovery-like surface is migrated or opened as a tracked follow-up before merge.
- **SEC-CAT138-11** Architecture test (D-6) additionally forbids: new callers inside `McpCatalogVisibility` beyond the DataAccess list, any discovery class referencing `IRebacEvaluator`, and the removed members `ResolvePermittedAsync`/`IsRebacAllowedAsync` (assert absent). It must fail when a type outside the allow-list references the methods, including via lambdas/closure classes (check the compiler-generated nested types by declaring type root).
- **SEC-CAT138-12** I-7 gate: Phase 4 starts only after I-7 merged and section 2 re-verification is recorded; F-C1..F-C10 and W0-C1..W0-C11 tests stay green unmodified. `/datasources` keeps its admin-class gate before the visibility call (ReBAC only shrinks); GraphQL `OpenSchema` bypass stays operator opt-in and must be logged at startup once.

### Open-question defaults
OQ-1 yes, OQ-2 yes, OQ-3 no, OQ-4 yes, OQ-5 unchanged, OQ-6 constants: all accepted. OQ-1 consequence (F9) is a release-note item, not a blocker.

### Mandatory tests (`[Trait("Category","Security")]`)
- **T-CAT138-01** `NullPrincipal_NoSid_AnonymousMcpSid_ScopeNone_ReturnEmpty_AllThreeMethods`
- **T-CAT138-02** `RebacEvaluatorNullOrDisabledResultMissingKey_Denies` (null evaluator while enabled, key absent -> deny)
- **T-CAT138-03** `RebacChunkThrows_OnlyThatChunkHidden_CancellationPropagates_NoPartialMemo`
- **T-CAT138-04** `Memo_KeyedBySidTenantContractSurface_NotReferenceOnly_ReplacedPrincipalReEvaluates`
- **T-CAT138-05** `Batching_Chunk32_Parallel8_Enforced_RelationCandidatesCappedAndDeduped`
- **T-CAT138-06** `FindVisible_InactiveWrongTenantUnknownRebacDenied_IdenticalErrorAndMessage` (MCP describe, sample_rows, REST detail)
- **T-CAT138-07** `Describe_RelationTargetDenied_NoNameInRelationsAccessPathsLineageOrError`
- **T-CAT138-08** `RestRebacSubject_EqualsDataPathSubject_ForUserAndServiceToken`
- **T-CAT138-09** `CatalogNeverShowsWhatDataPathDeniesByRebac_Theory` (REST, MCP, GraphQL x admin/non-admin x Hidden/Summary/Full)
- **T-CAT138-10** `SummaryRow_NoColumnsNoSamples_ThroughServiceAllChannels_RebacOnAndOff`
- **T-CAT138-11** `AdminClass_RebacOn_NoTuples_SeesNothing_ConsentAdminEndpointsStillReachable`
- **T-CAT138-12** `Datasources_AdminGateBeforeVisibility_RebacShrinksOnly`
- **T-CAT138-13** `Logs_Metrics_NoSidTenantTableName_OnChunkFailureAndDeny`
- **T-CAT138-14** `Architecture_RemovedMembersAbsent_ClosureCallersDetected_DiscoveryHasNoRebacEvaluatorReference`
- **T-CAT138-15** `SurfaceDispositionList_OpenApiODataQueryAnalysis_NoHiddenTableNameToNonAdmin` (covers SEC-CAT138-10 outcome)
