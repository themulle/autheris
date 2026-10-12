# Implementation Plan: Wave 3 Catalog Transparency, Data Ownership & Response Enhancements

**Document ID:** `PLAN-WAVE3-CATALOG-OWNERSHIP-2026-10-12`  
**Date:** 2026-10-12  
**Status:** COMPLETED & VERIFIED ✅  
**Author:** Solution Architect, Security Expert & Delivery Project Manager  
**Target Branch:** `feat/catalog-ownership-wave3`  
**Target Path:** `docs/plans/2026-10-12-plan-catalog-ownership-wave3.md`  
**Parent Plan:** [PLAN-GH-ISSUES-ROADMAP-2026-10-12](file:///root/autheris/docs/plans/2026-10-12-plan-github-issues-roadmap.md)  
**PRD:** [REQ-WAVE3-CATALOG-OWNERSHIP-2026-10-12](file:///root/autheris/docs/plans/2026-10-12-req-catalog-ownership-wave3.md)  
**Origin Master Plan:** [00-master-plan-overview.md](file:///root/autheris/docs/plans/00-master-plan-overview.md)  

---

## 1. Executive Summary & Delivery Scope

This implementation plan delivers **Wave 3** of the Autheris GitHub Issues Roadmap, focusing on catalog discovery, metadata tags, data owner attribution, Backstage synchronization, and response transparency:

| Issue | Title | Complexity | Component Touchpoints |
| :---: | :--- | :---: | :--- |
| **#43** | Return Catalog Tags in `list_datasets` and `describe_dataset` | Low-Medium | `McpDatasetModels.cs`, `McpDatasetCatalog.cs`, `McpDatasetTools.cs`, `CatalogSchemaModel.cs` |
| **#33** | Data Owners in Catalog, `describe_dataset`, and Backstage | Medium | `IDataOwnershipRepository.cs`, `McpDatasetCatalog.cs`, `BackstageCatalogExportService.cs` |
| **#32** | Catalog Discovery with Access Status & Dataset Visibility | Medium | `TableModels.cs`, `CatalogVisibility.cs`, `McpDatasetCatalog.cs`, `QueryTypes.cs` |
| **#27** | Mark Masked Values in GraphQL Responses (`extensions.maskedFields`) | Medium | `DynamicTableType.cs`, `MaskedFieldsMiddleware.cs`, `HotChocolateFieldMiddleware` |

---

## 2. Technical Architecture & Work Package Specifications

### WP-1: Issue #43 – Catalog Tags in MCP & GraphQL
* **Design Solution:**
  1. In `Autheris.Domain.Model.McpDatasetModels.cs`:
     - Add `IReadOnlyList<string>? Tags = null` to `McpDatasetSummary`.
     - Add `IReadOnlyList<string>? Tags = null` to `McpDatasetDescription`.
  2. In `IMcpDatasetCatalog.cs` & `McpDatasetCatalog.cs`:
     - Extend `ListDatasetsAsync` with `string? tag = null`.
     - Filter datasets: `string.IsNullOrWhiteSpace(tag) || (t.Table.Tags != null && t.Table.Tags.Any(tg => string.Equals(tg, tag.Trim(), StringComparison.OrdinalIgnoreCase)))`.
     - Populate `Tags = t.Table.Tags` on `McpDatasetSummary` and `McpDatasetDescription`.
  3. In `McpDatasetTools.cs`:
     - Add `"tag"` optional string property to `list_datasets` input schema.
  4. In `Autheris.GraphQL`:
     - Ensure `tags: [String!]!` is exposed on `TableMetadataDto` / catalog query.

---

### WP-2: Issue #33 – Data Owners in Catalog, `describe_dataset`, and Backstage
* **Design Solution:**
  1. In `Autheris.Domain.Model.McpDatasetModels.cs`:
     - Add `string? DataOwner = null` and `IReadOnlyList<string>? DataOwnerDelegates = null` to `McpDatasetDescription`.
  2. In `McpDatasetCatalog.cs`:
     - Inject `IDataOwnershipRepository? dataOwnershipRepository = null`.
     - In `DescribeDatasetAsync`:
       - If `dataOwnershipRepository != null`, call `await dataOwnershipRepository.GetDataOwnersForTableAsync(table.Identifier, ct)`.
       - Format primary owner as `"DisplayName <email>"` or `"AdAccount"`.
       - Format active delegates if present.
  3. In `BackstageCatalogExportService.cs`:
     - Inject `IDataOwnershipRepository? dataOwnershipRepository = null`.
     - In `ExportCatalogEntitiesAsync`, for each table:
       - Query active data owner via `GetDataOwnersForTableAsync`.
       - If found and active: set `Owner = owner.Email ?? owner.AdAccount ?? backstageOpts.DefaultOwner`.
       - If unowned or inactive: set `Owner = backstageOpts.DefaultOwner`.

---

### WP-3: Issue #32 – Catalog Discovery with Access Status (`GRANTED | PENDING | NONE`) & Visibility
* **Design Solution:**
  1. In `Autheris.Domain.Model.TableModels.cs`:
     - Add enum `DatasetVisibility { Hidden = 0, Summary = 1, Full = 2 }`.
     - Add `public DatasetVisibility Visibility { get; init; } = DatasetVisibility.Hidden;` to `Table`.
  2. In `McpDatasetModels.cs`:
     - Add `string? AccessStatus = null` to `McpDatasetSummary` and `McpDatasetDescription`.
  3. In `CatalogVisibility.cs`:
     - Add `ComputeAccessStatus(table, activeConsents, pendingRequests, userSid)`:
       - If caller has active Allow consent -> `"GRANTED"`.
       - Else if caller has a pending consent request -> `"PENDING"`.
       - Else -> `"NONE"`.
     - Visibility rules:
       - `Hidden`: Visible ONLY if `AccessStatus == "GRANTED"` (or admin).
       - `Summary`: Visible with basic summary (name, description, tags, access status) even if `NONE` or `PENDING`.
       - `Full`: Always visible in catalog.

---

### WP-4: Issue #27 – Mark Masked Values in GraphQL Responses (`extensions.maskedFields`)
* **Design Solution:**
  1. Implement `MaskedFieldsMiddleware` in `Autheris.GraphQL.Interceptors`:
     - Registers a thread-safe `ConcurrentBag<string>` in `context.ContextData["MaskedFields"]`.
     - Awaits next pipeline step.
     - On completion, if `context.Result is IQueryResult queryResult` and any fields were masked:
       - Adds `extensions.maskedFields = maskedFields.Distinct().OrderBy(x => x).ToList()`.
  2. In `DynamicTableType.cs`:
     - When `level == ColumnAccessLevel.Mask`:
       - If `ctx.ContextData.TryGetValue("MaskedFields", out var mfObj) && mfObj is ConcurrentBag<string> bag`:
         - Record field path: `bag.Add(ctx.Path.ToString())`.
  3. Register `MaskedFieldsMiddleware` in Hot Chocolate request execution pipeline.

---

## 3. Phase 3: Security & Compliance Invariants

- [x] **Anti-Oracle Gate:** `Hidden` datasets with `AccessStatus == "NONE"` must NEVER appear in `list_datasets`, GraphQL `catalog`, or search results.
- [x] **No Secret Leakage in Extensions:** `extensions.maskedFields` lists only field paths (e.g. `"orders[0].salary"`). It never includes raw values or HMAC keys.
- [x] **Tenant Scoping:** Access status calculation and request lookups are strictly isolated by `TenantId`.
- [x] **Zero Overhead:** When no masking occurs, `extensions.maskedFields` is omitted completely from the GraphQL response.

---

## 4. Phase 4: Delivery Sequence (Red-Green-Refactor)

- [x] **Step 1:** Implement WP-1 (Tags in `McpDatasetModels`, `McpDatasetCatalog`, `McpDatasetTools`, GraphQL) + Unit Tests.
- [x] **Step 2:** Implement WP-2 (Data Owners in `McpDatasetCatalog`, `DescribeDatasetAsync`, `BackstageCatalogExportService`) + Unit Tests.
- [x] **Step 3:** Implement WP-3 (Access Status `GRANTED/PENDING/NONE` & Dataset Visibility `FULL/SUMMARY/HIDDEN`) + Unit Tests.
- [x] **Step 4:** Implement WP-4 (Mark Masked Values in GraphQL Responses `extensions.maskedFields`) + Unit Tests.
- [x] **Step 5:** Run full test suite (`dotnet test Autheris.sln`) -> verify 100% green.
- [x] **Step 6:** Author documentation `docs/features/f-gov-16-catalog-ownership-transparency.md`, update master plan, push branch, close GitHub issues #43, #33, #32, #27.
