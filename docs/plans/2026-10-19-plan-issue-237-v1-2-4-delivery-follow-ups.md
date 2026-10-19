# Implementation Plan: Issue #237 v1.2.4 Delivery Follow-Ups

**Document ID:** `PLAN-GOV-237`  
**Date:** 2026-10-19  
**Status:** APPROVED WITH CONDITIONS (Phase 2 & Phase 3 Complete)  
**Author:** Solution Architect & Lead Delivery PM (`csharp-architect`, `project-manager`)  
**Parent PRD:** [`docs/plans/2026-10-19-req-issue-237-v1-2-4-delivery-follow-ups.md`](2026-10-19-req-issue-237-v1-2-4-delivery-follow-ups.md) (`REQ-GOV-237`)  
**Branch:** `feat/issues-236-237-governance-hotfix`  
**Target Milestone:** v1.2.5  

---

## 1. Architectural Architecture & Work Packages

This plan addresses the 5 polish and gap items identified following the v1.2.4 release of #220, #222, #224, #225, and #226.

### WP-237-1: Telemetry & Usage Recording Polish (#220)
1. **Bulk Stream Telemetry:** In `TableStreamEndpoints.cs`:
   - Resolve `IConsumerUsageRecorder` from `httpContext.RequestServices`.
   - Record `ConsumerOutcome.Allow` upon successful streaming with `rowsEmitted` and `rowsMeasured: true`.
   - Record `ConsumerOutcome.DenyRateLimit` on throttling, `ConsumerOutcome.DenyCost` on budget rejection, and `ConsumerOutcome.DenyAccess` on policy refusal.
2. **GraphQL Tree Query Service Fix:** In `GovernedTreeQueryService.cs`:
   - Remove `usageRecorder: _usageRecorder` argument from the audit log call at line 680 to eliminate duplicate call counting.
   - In `RunTreeAsync`, calculate actual root rows returned from the JSON payload (array length or 1) and invoke `RecordUsage` with `rowsReturned: measuredRows, rowsMeasured: true`.
3. **Cost Rejection Recording:** In `CostAndQuotaMiddleware.cs`:
   - When query cost exceeds `maxAllowedCost` (`QUERY_COST_QUOTA_EXCEEDED`), invoke `IConsumerUsageRecorder.RecordUsage` with `ConsumerOutcome.DenyCost`.
   - When rate limit is exceeded (`RATE_LIMIT_EXCEEDED`), invoke `IConsumerUsageRecorder.RecordUsage` with `ConsumerOutcome.DenyRateLimit`.
4. **Production HMAC Key Injection:** In `GatewayGovernanceServiceExtensions.cs`:
   - Register `ConsumerUsageRecorder` with factory checking configured cluster secret / key outside Development environment. Fail closed if missing outside Development.

### WP-237-2: Keyset Cursor HMAC Cryptographic Context Binding (#222)
1. **Context Abstraction:** Define `KeysetCursorContext(string? TenantId, string? DataSource, string? TableName, string? Direction, IReadOnlyList<string>? Columns, string? Caller)`.
2. **Signature Binding:** In `KeysetCursorEncoder`:
   - Overload `EncodeCursor` and `DecodeCursor` with `KeysetCursorContext? context = null`.
   - Cryptographically bind `context` into the HMAC-SHA256 signature calculation: `HMAC(key, $"{payload}:{tenant}:{source}:{table}:{direction}:{columns}:{caller}")`.
   - In `DecodeCursor`, compute expected signature using the table's actual metadata and request context; reject with `InvalidCursorException` on tampering or cross-table/cross-tenant reuse.
3. **Endpoint & Planner Integration:** Pass active request context in `TableStreamEndpoints.cs` and `GovernedTableReadPlanner.Keyset.cs`.

### WP-237-3: Nested Cost Settlement Precision (#224)
1. In `CostAndQuotaMiddleware.cs`:
   - In `ComputeActualCost`, evaluate `CountResultRows(opResult.Data.Value)` when `opResult.Data is not null`.
   - Set `rowsReturned = Math.Max(rowsReturned, countedRows)` so nested child rows are counted even when `rowsReturned > 0` (e.g., 10 parents with 100 children settled on 1,010 rows).

### WP-237-4: Query Analysis rowsPerRequest & Cost Bounds (#225)
1. In `QueryAnalysisModels.cs`:
   - Add `RowsPerRequest`, `Requests`, and `SecondsAtCurrentBucket` properties to `QueryCostEstimate`.
2. In `QueryAnalysisService.cs`:
   - Calculate `rowsPerRequest` as the largest `first` / batch size fitting within the caller's tier limit `maxAllowedCost`.
   - Calculate `requests = Math.Ceiling(totalEstimatedRows / rowsPerRequest)`.
   - Calculate `secondsAtCurrentBucket` estimating wait time until sufficient tokens refill based on `CallerBucketState`.

### WP-237-5: Self-Service Disable & Error Message Formatting (#226)
1. In `EntraIdAuthOptions`:
   - Add property `public bool SelfServiceEnabled { get; init; } = true;`.
2. In `ReadOnlyOperationMiddleware.cs`:
   - If `SelfServiceEnabled == false` or `ReadOnlySelfServiceScopes` is empty, reject self-service mutations with `READ_ONLY_TOKEN`.
3. In `MutationTypes.cs`:
   - If `SelfServiceEnabled == false` or `AccessReviewScopes` is empty, reject with `FORBIDDEN` ("Self-service access review is disabled.").
   - When required review scope is missing, format error message dynamically: `$"The '{string.Join("', '", reviewScopes)}' scope is required to review consent requests."`.

---

## 2. Phase 3 Security & Compliance Review (Binding Conditions)

- **SEC-BC237-01 (Context-Bound HMAC)**: Keyset pagination cursors MUST be cryptographically bound to tenant, data source, table, sort direction, and key columns. Presentation of a cursor on a different dataset or tenant MUST fail closed with `INVALID_CURSOR`.
- **SEC-BC237-02 (Zero Telemetry Key Fallback in Production)**: In staging and production environments, `ConsumerUsageRecorder` MUST NOT use the hardcoded default secret. Startup MUST fail closed if no cluster secret or telemetry key is provided.
- **SEC-BC237-03 (Nested Cost Settlement Integrity)**: Actual cost settlement MUST NOT under-count nested GraphQL list responses. Recursive counting must be bounded to depth 32 to prevent stack overflow.
- **SEC-BC237-04 (Self-Service Fail-Closed Disable)**: Explicitly configuring `SelfServiceEnabled: false` or empty scopes MUST disable self-service mutations with zero bypass possible.

---

## 3. Work Package Execution Order
1. WP-237-2: Keyset Cursor HMAC Context Binding (`KeysetCursorEncoder`, `TableStreamEndpoints`, `GovernedTableReadPlanner.Keyset`)
2. WP-237-3: Nested Cost Settlement in `CostAndQuotaMiddleware`
3. WP-237-5: Self-Service Disable & Error Formatting in `JwtOptions`, `ReadOnlyOperationMiddleware`, `MutationTypes`
4. WP-237-4: Query Analysis `rowsPerRequest`, `requests`, `secondsAtCurrentBucket` in `QueryAnalysisModels` & `QueryAnalysisService`
5. WP-237-1: Telemetry in `TableStreamEndpoints`, `GovernedTreeQueryService`, `CostAndQuotaMiddleware`, and DI registration
6. Comprehensive Unit & Security Tests
