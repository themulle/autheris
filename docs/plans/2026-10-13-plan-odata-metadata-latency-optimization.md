# Implementation Plan: OData Metadata & Service Document Latency Optimization

**Document ID:** `PLAN-ODATA-METADATA-LATENCY-2026-10-13`  
**Date:** October 13, 2026  
**Status:** IN PROGRESS 🚀  
**Component:** `Autheris.Extensions.OData`, `Autheris.Application.Services.GatewayExecutionService`  
**Parent Plan:** [`doc/plan/00-master-plan-overview.md`](00-master-plan-overview.md)  

---

## 1. Executive Summary & Problem Analysis

In production testing of Autheris v1.1.6 / v1.1.7, requests to `/odata` (OData Service Document) took **~7 seconds** and `/odata/$metadata` (CSDL XML) took **~11 seconds** on catalogs with 150+ tables.

### Root Causes Identified:
1. **Redundant N+1 Synchronous Tier-A Audit Logging:**
   `ODataHandler.GetAuthorizedTablesAsync` invoked `IGatewayExecutionService.CheckTableAccessAsync` inside a sequential `foreach` loop over all candidate tables. `CheckTableAccessAsync` issued an individual audit event with `EventType = "CHILD_RELATION_CHECK"`. Because this event type is neither `TABLE_QUERY` nor `WEBSQL_QUERY`, and because denied decisions are security-critical, `SqliteGovernanceRepository.Audit` treats every check as a **Tier-A event** requiring an exclusive mutex lock, SHA-256 HMAC hash chaining, and a synchronous disk flush. 150 tables resulted in 150 sequential disk transactions taking 6–8 seconds alone.
2. **Redundant Catalog Metadata Queries:**
   Although `ODataHandler` already fetched all active table metadata using `_metadataRepo.GetAllTablesAsync()`, `CheckTableAccessAsync` called `_metadataRepository.GetTableMetadataAsync(table)` again for each table, resulting in 150 redundant SQL select queries.
3. **Missing Epoch/In-Memory Caching:**
   Neither the generated CSDL XML nor the service document or authorized table lists were cached. Every request from Excel, Power BI, or browsers re-executed all 150 database lookups and 150 synchronous audit writes.

---

## 2. Architecture & Design Specification

### 2.1 Fast-Path `CheckTableAccessAsync` Overload (Audit & Lookup Bypass)
Extend `IGatewayExecutionService` and `GatewayExecutionService`:
```csharp
Task<TableAccessDecision> CheckTableAccessAsync(
    ClaimsPrincipal? principal,
    TableMetadata metadata,
    bool recordAudit = true,
    CancellationToken ct = default);
```
- Accepts already-loaded `TableMetadata` to eliminate redundant catalog database queries.
- Adds `bool recordAudit = true`. When set to `false` (as used by OData metadata discovery, which is already audited at the endpoint level as `CatalogRead`), individual `CHILD_RELATION_CHECK` audit writes are bypassed.
- Preserves full backward compatibility for `CheckTableAccessAsync(principal, tableIdentifier, ct)`.

### 2.2 Memory Caching in `ODataHandler`
- Inject optional `IMemoryCache? memoryCache = null`.
- Cache authorized table metadata lists: `odata:tables:{tenant}:{userSid}` (TTL: 60s).
- Cache CSDL XML string: `odata:csdl:{tenant}:{userSid}` (TTL: 60s).
- Cache Service Document object: `odata:servicedoc:{tenant}:{userSid}:{serviceRootUrl}` (TTL: 60s).
- On initial cache miss, in-memory policy resolution without audit writes executes in ~10–25ms. Subsequent cache hits return in < 1ms.

---

## 3. Implementation Steps

1. **Step 1:** Add `CheckTableAccessAsync(ClaimsPrincipal?, TableMetadata, bool, CancellationToken)` to `IGatewayExecutionService` and implement in `GatewayExecutionService`.
2. **Step 2:** Update `ODataHandler` constructor to accept optional `IMemoryCache? memoryCache = null`.
3. **Step 3:** Update `ODataHandler.GetAuthorizedTablesAsync` to use the pre-loaded metadata, pass `recordAudit: false`, and cache the authorized table list.
4. **Step 4:** Update `ODataHandler.GetMetadataCsdlAsync` and `GetServiceDocumentAsync` to leverage `IMemoryCache`.
5. **Step 5:** Add unit tests verifying that metadata evaluation avoids redundant queries, skips per-table audit events, and uses cache correctly.
6. **Step 6:** Run full test suite, verify compilation, and update origin master plan.
