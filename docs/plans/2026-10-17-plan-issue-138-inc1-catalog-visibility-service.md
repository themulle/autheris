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
