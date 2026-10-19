# Requirements Specification: Issue #237 v1.2.4 Delivery Follow-Ups

**Document ID:** `REQ-GOV-237`  
**Date:** 2026-10-19  
**Status:** APPROVED (P1 / Quality & Consistency)  
**Author:** Delivery Project Manager & Product Owner (`project-manager`)  
**Parent Plan:** [`docs/plans/00-master-plan-overview.md`](00-master-plan-overview.md)  
**Target Milestone:** v1.2.5  

---

## 1. Executive Summary

Following the release of v1.2.4 (which delivered issues #220 through #227), post-implementation review identified five technical polish and gap items across telemetry, streaming cryptography, cost accounting, query analysis, and self-service authorization:
1. **Item 1 (#220 - Telemetry & Lineage):** Tag telemetry meters with normalized caller client IDs and ensure robust claim extraction.
2. **Item 2 (#222 - Keyset Cursor Cryptographic Context Binding):** Cryptographically bind keyset cursor HMAC signatures to tenant, data source, table, and sort direction to prevent cross-context replay attacks.
3. **Item 3 (#224 - Nested Cost Settlement Precision):** Recursively count nested rows across heterogeneous/null structures and refund unspent quota tokens with full fidelity.
4. **Item 4 (#225 - Query Analysis rowsPerRequest & Tier Bounds):** Expose estimated rows per request in analysis outputs and provide structured diagnostics for tier/scope limit violations.
5. **Item 5 (#226 - Self-Service Boundary Gate):** Fail closed by disabling self-service access request mutations entirely when `ReadOnlySelfServiceScopes` is empty or null.

---

## 2. Functional Requirements

### FR-237-1: Consumer Usage Telemetry & Lineage Polish (#220)
- **FR-237-1.1 (Metric Attributes):** OpenTelemetry meters for consumer usage (`autheris.consumer.calls`, `autheris.consumer.rows`, `autheris.consumer.rejections`) MUST include the standard dimensions:
  - `client_id`: Normalized application/client identity.
  - `application_name`: Cleaned application name (or `unknown`).
  - `data_source`: Logical target data source name.
  - `table_name`: Target dataset name.
- **FR-237-1.2 (Claim Extraction Fallback):** When populating `CallerContext.ClientId`, the extractor MUST evaluate claims in strict precedence: `client_id` -> `azp` -> `appid`. Whitespace strings MUST normalize to `null`.

### FR-237-2: Keyset Cursor HMAC Context Binding & Key Rotation (#222)
- **FR-237-2.1 (Context Binding):** Keyset cursor HMAC-SHA256 signatures in `KeysetCursorEncoder` MUST incorporate the tuple `(TenantId, DataSource, TableName, SortDirection, CursorPayload)`.
- **FR-237-2.2 (Replay Prevention):** Attempting to present a cursor generated for `TableA` in a query against `TableB`, or across different tenants, MUST immediately throw `InvalidCursorException` with code `INVALID_CURSOR`.
- **FR-237-2.3 (Key Rotation Grace Period):** The cursor encoder MUST support primary and secondary HMAC keys via a `kid` header prefix (`v1.<kid>.<hmac>.<payload>`) to enable zero-downtime key rotation.

### FR-237-3: Nested Cost Settlement Precision & Quota Refund (#224)
- **FR-237-3.1 (Recursive Row Counting):** `CostAndQuotaMiddleware.CountResultRows` MUST safely traverse nested dictionary objects and lists. It must handle `null` elements, empty collections, and limit recursion depth to 32 levels to prevent stack overflow.
- **FR-237-3.2 (Settlement Token Refund):** When settled query cost is less than pre-allocated estimate, the unspent token delta MUST be refunded to the caller's token bucket via `IRateLimiterService.RefundTokensAsync`.

### FR-237-4: Query Analysis rowsPerRequest & Tier Bounds (#225)
- **FR-237-4.1 (Report rowsPerRequest):** `QueryAnalysisService` and the `POST /api/v1/governance/queries/analyze` endpoint MUST include `rowsPerRequest` (estimated batch/page row count) in both `QueryCostEstimate` and `QueryValidationReport`.
- **FR-237-4.2 (Tier Limit Violation Diagnostics):** When a query is rejected due to caller tier or scope cost caps, the analysis report MUST include `CallerTier`, `ConfiguredTierCostLimit`, and `TotalEstimatedCost`.

### FR-237-5: Self-Service Boundary Gate & Complete Disable (#226)
- **FR-237-5.1 (Fail-Closed Disable):** If `JwtOptions.ReadOnlySelfServiceScopes` is configured as an empty array `[]` or null, self-service access request endpoints and mutations MUST be completely disabled.
- **FR-237-5.2 (Error Response):** Invocations under disabled configuration MUST return HTTP 403 Forbidden / GraphQL error with code `SELF_SERVICE_DISABLED` and message `"Self-service access requests are disabled on this gateway."`.

---

## 3. Non-Functional & Security Requirements

- **SEC-237-1 (Cursor Anti-Tamper & Confidentiality):** Any bit manipulation of cursor payload or metadata MUST trigger signature verification failure without revealing internal server stack traces.
- **SEC-237-2 (Denial-of-Service Defense):** Nested row counter MUST guard against deeply nested or cyclic payloads by bounding maximum depth and node count.
- **NFR-237-1 (High-Performance Settlement):** Row counting and HMAC verification MUST remain allocation-efficient, leveraging pooled buffers and non-allocating string spans.

---

## 4. Acceptance Criteria

1. **AC-237-1:** Telemetry spans and metrics record distinct tags for `client_id` and `application_name`.
2. **AC-237-2:** Keyset cursor from Table A rejected when used on Table B with `INVALID_CURSOR`.
3. **AC-237-3:** Deeply nested GraphQL responses (e.g. 5 levels) return accurate total row count in settled telemetry and audit records.
4. **AC-237-4:** `POST /api/v1/governance/queries/analyze` outputs `rowsPerRequest` matching query limits.
5. **AC-237-5:** Empty `ReadOnlySelfServiceScopes` causes `requestTableAccess` to return `SELF_SERVICE_DISABLED`.
6. **AC-237-6:** 100% test pass rate across all unit, integration, and security test suites.

---

## 5. Architectural & Phase Assignment

- **Phase 1 (PM):** Intake & PRD approved (this document, `REQ-GOV-237`).
- **Phase 2 (Architecture):** Solution Architect plans implementation work packages across Telemetry, Streaming, GraphQL, and Security middleware.
- **Phase 3 (Security):** Security Expert reviews HMAC context binding and disabled self-service fail-closed logic.
- **Phase 4 (C# TDD Developer):** Red-Green-Refactor development across affected assemblies.
- **Phase 5 (Code Review):** Code review, test verification, and merge to `main`.
