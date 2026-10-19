# PRD: REQ-GOV-175B - Caller Bucket State & System Caps in Static Query Analysis (`/analyze`)

**Document ID:** `REQ-GOV-175B`  
**Date:** 2026-10-19  
**Status:** APPROVED WITH CONDITIONS (Phase 1 Qualified)  
**Author:** Enterprise Product Manager & Strategic Gatekeeper  
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)  
**Parent Feature:** `REQ-GOV-175`, `PLAN-GOV-175`, `F-GOV-20`  
**GitHub Issue:** #175 Addendum  
**Priority:** Wave 2 (Enhancement), Rank 2.7b  

---

## 1. Executive Summary & Problem Statement

### 1.1 The Problem
In Issue #175 (`REQ-GOV-175`), `POST /api/v1/governance/queries/analyze` was unlocked for read-only tokens, returning `ValidationReport` and exact execution cost per root field via `QueryCostAnalyzerRule`.

However, the endpoint currently **does not inform the caller about their own operational limits**:
1. **Unknown Bucket State:** Callers and autonomous AI agents do not know how many tokens remain in their token bucket, what their burst capacity is, or at what rate tokens refill.
2. **Hidden System Caps:** Callers cannot discover gateway-wide enforcement ceilings (`MaxAllowedComplexity`, `MaxResponseRows`, `MaxAllowedOffset`, `MaxNestingDepth`).

Without this information, autonomous AI agents cannot make informed decisions about query batching, pagination page sizes, or request throttling. They are forced to operate blind until hitting an unexpected HTTP 429 `TOO_MANY_REQUESTS` or `RESPONSE_TOO_LARGE` rejection.

### 1.2 Target Audience
- **Autonomous AI Agents & Orchestrators:** Planning query execution sequences within available rate-limit and cost budgets.
- **Client Application Developers & SDKs:** Inspecting dynamic gateway caps during startup or pre-flight initialization.
- **Platform Operators:** Troubleshooting why specific client tiers or tokens are experiencing throttling.

### 1.3 Strategic & Architectural Fit
Zero-Trust and AI-native gateways must provide **Operational Transparency**. Exposing caller quotas and system caps non-destructively through `/analyze` transforms Autheris into an intelligent, self-describing gateway that guides agentic consumption rather than abruptly terminating it.

---

## 2. Market & Industry Benchmark

- **GitHub REST API & GraphQL API:** Provides `X-RateLimit-Limit`, `X-RateLimit-Remaining`, and the `rateLimit` schema query object so automated tooling can budget operations.
- **OpenAI & Anthropic APIs:** Surface token bucket and concurrency rate limits in pre-flight headers and usage endpoints.
- **OWASP API4:2023 (Unrestricted Resource Consumption):** Advocates that systems provide explicit rate-limit visibility so well-behaved clients can self-throttle.

---

## 3. User Stories & Acceptance Criteria

### US-1: Non-Destructive Bucket State Probe
*As an Autonomous AI Agent, I want to query my current token bucket balance and refill rate without deducting any tokens, so that I can decide whether to run a heavy query now or defer it.*

- **AC-1.1:** `IRateLimiterService` provides a read-only inspection method:
  ```csharp
  Task<CallerBucketState> GetBucketStateAsync(string key, ClientQuotaPolicy policy, CancellationToken ct = default);
  ```
- **AC-1.2:** `GetBucketStateAsync` reads the current token count and last refill timestamp from Redis / memory without decrementing tokens or acquiring write locks.
- **AC-1.3:** It calculates and returns:
  - `Capacity` (integer maximum tokens).
  - `CurrentBalance` (current available tokens, accounting for fractional elapsed refill time).
  - `RefillRatePerSecond` (tokens added per second).

### US-2: Active System Caps Surfacing
*As a Query Developer, I want `/analyze` to tell me the active system ceilings, so that I can set `first` and `offset` arguments safely within gateway limits.*

- **AC-2.1:** `QueryAnalysisResult` includes a `SystemCaps` object:
  ```json
  "systemCaps": {
    "maxAllowedCost": 500,
    "maxResponseRows": 5000,
    "maxAllowedOffset": 10000,
    "maxNestingDepth": 10,
    "maxAggregateRowBudget": 50000
  }
  ```
- **AC-2.2:** The caps reflect the active configuration (`GatewayOptions.GraphQL` and caller's tier policy overrides) for the authenticated tenant.

### US-3: Integrated Response Payload in `/analyze`
*As a Caller invoking `POST /api/v1/governance/queries/analyze`, I want caller quota and system caps included in the response.*

- **AC-3.1:** `QueryAnalysisResult` is extended with an optional property:
  ```csharp
  public sealed record CallerQuotaSummary(
      string ClientTier,
      CallerBucketState BucketState,
      ActiveSystemCaps SystemCaps);
  ```
- **AC-3.2:** When the request is authenticated, `CallerQuota` is populated; for anonymous pre-flight requests (if permitted), it reflects the `Free` tier.

---

## 4. Non-Functional Requirements (NFRs)

- **Zero Deduction & Side-Effects:** Calling `/analyze` must never consume tokens from the caller's bucket.
- **Performance:** `GetBucketStateAsync` must execute in $< 0.5$ ms (single Redis `HMGET` or memory dictionary lookup).
- **Tenant Isolation:** A caller can only see their own bucket state and tier; no other caller's quota or system status can be enumerated.

---

## 5. Security & Governance Review

- **Information Disclosure:** Surfacing system caps (`MaxResponseRows`, `MaxAllowedCost`) does not leak sensitive data; it describes API contract limits. Bucket state reflects only the authenticated caller's identity (`user:{tenant}:{oid}`).
- **Fail-Closed Default:** If Redis is temporarily unreachable for the quota probe, `/analyze` gracefully returns `BucketState: null` without failing the core static analysis.

---

## 6. Implementation Scope

1. **`Autheris.Application/Interfaces/IRateLimiterService.cs`:**
   - Define `CallerBucketState(int Capacity, double CurrentBalance, double RefillRatePerSecond)`.
   - Add `GetBucketStateAsync` to `IRateLimiterService`.
2. **`Autheris.Infrastructure/RateLimiting/`:**
   - Implement `GetBucketStateAsync` in `InMemoryRateLimiterService` and `RedisRateLimiterService`.
3. **`Autheris.Application/Governance/Analysis/`:**
   - Add `CallerQuotaSummary` and `ActiveSystemCaps` to `QueryAnalysisModels.cs`.
   - Update `QueryAnalysisService.AnalyzeQueriesAsync` to populate caller quota.
4. **Automated Tests:**
   - Unit tests verifying bucket state calculation with time-based refill.
   - Integration tests verifying `/analyze` response JSON schema.
