# PRD: REQ-GOV-29 - Per-Scope Token Bucket & Rate Limiting

**Document ID:** `REQ-GOV-29`  
**Date:** 2026-10-19  
**Status:** APPROVED WITH CONDITIONS (Phase 1 Qualified)  
**Author:** Enterprise Product Manager & Strategic Gatekeeper  
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)  
**GitHub Issue:** #29 Addendum  
**Priority:** Wave 2 (Urgently Missing Feature), Rank 2.17  

---

## 1. Executive Summary & Problem Statement

### 1.1 The Problem
Autheris provides token-bucket rate limiting via `ClientTierResolver` and `IRateLimiterService`. In `ClientTierResolver.BuildAuthenticatedSubjectId`, the rate-limiter bucket subject key is constructed as:
```csharp
var subject = $"user:{tenant}:{userSid}";
if (!string.IsNullOrWhiteSpace(clientId) && !string.Equals(clientId, userSid, StringComparison.OrdinalIgnoreCase))
{
    subject += $":{clientId}";
}
```
Currently, this key **completely ignores token scopes (`scp`)**. Furthermore, `ClientTierOptions` only supports role-to-tier mappings (`RoleTierMappings`) and API key mappings (`ApiKeys`).

**The Collision Hazard:**
In enterprise zero-trust deployments, a single user identity interacts with Autheris through multiple channels:
1. **Interactive Web Apps / Portals:** Authenticated with user delegated tokens (`Apps.Read` or `user_impersonation`).
2. **Autonomous AI Agents & Scripts:** Acting on behalf of the user with restricted delegated scopes (`Agent.Read`).

Because both tokens share the identical subject key (`user:{tenant}:{userSid}`), the AI agent's rapid, complex query batches deplete the user's token bucket. The human user operating the web dashboard is abruptly blocked with HTTP 429 `TOO_MANY_REQUESTS` / `GatewayThrottledException`. Conversely, an active user session can starve an automated workflow.

### 1.2 Target Audience
- **Enterprise Application Developers:** Requiring reliable uptime for interactive users regardless of background agent tasks.
- **AI Agent Operators:** Needing defined, predictable execution budgets without interfering with interactive dashboards.
- **Security & Platform Administrators:** Configuring granular tier limits per OAuth scope.

### 1.3 Strategic & Architectural Fit
Zero-Trust principles dictate **Least Privilege and Resource Isolation**. Workloads operating with distinct scopes must be isolated into distinct failure and rate-limiting domains.

---

## 2. Market & Industry Benchmark

- **IETF RFC 6749 (OAuth 2.0) & RFC 9068 (JWT Profile for Authorization Grants):** Defines `scope` as the primary mechanism to partition permissions and resource boundaries.
- **Stripe & AWS API Gateway Architecture:** Partitions rate limit buckets by `(account, caller_type, scope)` to prevent background integrations from choking user traffic.
- **OWASP API4:2023 (Unrestricted Resource Consumption):** Recommends separate rate limits for automated API clients versus user-facing applications.

---

## 3. User Stories & Acceptance Criteria

### US-1: Scope-Aware Rate Limiting Buckets
*As a System Administrator, I want token buckets to be keyed by user and scope, so that an AI agent using `Agent.Read` does not consume the rate limit bucket of an interactive user using `Apps.Read`.*

- **AC-1.1:** `ClientTierResolver.BuildAuthenticatedSubjectId` extracts normalized token scopes via `TokenAccessScope.GetScopes(principal)`.
- **AC-1.2:** The subject ID format incorporates the normalized scope identifier: `user:{tenant}:{userSid}:{scope}` (e.g. `user:tenant-1:usr-123:Agent.Read`).
- **AC-1.3:** For tokens containing multiple scopes, scopes are sorted alphabetically and joined canonically (`user:{tenant}:{userSid}:{scopeA+scopeB}`) to guarantee consistent key generation.
- **AC-1.4:** If client ID is present and distinct, it is included: `user:{tenant}:{userSid}:{clientId}:{scope}`.

### US-2: Scope-to-Tier and Scope-to-Limit Mappings
*As a Security Administrator, I want to configure specific rate-limiting tiers and limits per scope in `appsettings.json`, so that AI agents have lower burst capacity but higher steady refill rates than human users.*

- **AC-2.1:** `ClientTierOptions` is extended with:
  - `ScopeTierMappings: Dictionary<string, string>` (e.g. `"Agent.Read" => "Standard"`, `"Apps.Read" => "Premium"`).
  - `ScopeLimits: Dictionary<string, ClientTierLimitOverride>` (custom capacity, refill rate, and max cost per query).
- **AC-2.2:** Tier resolution precedence in `ClientTierResolver.ResolveAsync`:
  1. Registered API Key (`ApiKeys`).
  2. Explicit `tier` or `client_tier` JWT claim.
  3. Scope Tier Mapping (`ScopeTierMappings`).
  4. Role Tier Mapping (`RoleTierMappings`).
  5. Default fallback (`Standard` for authenticated, `Free` for anonymous).
- **AC-2.3:** Scope matching uses `ScopeNames.ToBareName` so that full form (`api://my-app/Agent.Read`) and bare form (`Agent.Read`) match identically.

### US-3: Rate Limit Diagnostic Parity
*As an API Consumer receiving HTTP 429, I want to know which scope's bucket was exhausted.*

- **AC-3.1:** The rate-limiting response header (`X-RateLimit-Scope` or `ProblemDetails.Extensions["scope"]`) indicates the scope key associated with the throttled bucket.

---

## 4. Non-Functional Requirements (NFRs)

- **Performance (Zero-Allocation on Hot Path):** Scope extraction and key formatting must avoid repeated string concatenations. Where single known scopes (`Agent.Read`, `Apps.Read`) are present, use cached strings or stack-allocated buffers.
- **Fail-Closed Security:** Unrecognized or malformed scopes must not bypass rate limiting; they map to the default `Standard` tier with a sanitized subject key.
- **Storage Compatibility:** Redis key naming convention must remain cluster-safe (using hash tags `{tenant}:{userSid}` to ensure cross-slot compatibility if needed).

---

## 5. Security & Governance Review

- **Denial of Service Prevention:** Prevents noisy-neighbor starvation between agentic tooling and interactive UI sessions.
- **Audit Logging:** Audit events for rate-limit rejections (`AuditEventTypes.RateLimitThrottled`) must capture the effective scope and bucket key.

---

## 6. Implementation Scope

1. **`Autheris.Domain.Options/Resilience/RateLimitingOptions.cs`:**
   - Add `ScopeTierMappings` and `ScopeLimits` to `ClientTierOptions`.
2. **`Autheris.Application/Caching/Services/ClientTierResolver.cs`:**
   - Update `BuildAuthenticatedSubjectId` to include scope.
   - Update `ResolveAsync` to evaluate `ScopeTierMappings` and `ScopeLimits`.
3. **`Autheris.Infrastructure/RateLimiting/`:**
   - Ensure Redis and InMemory rate limiters handle scope-aware bucket keys seamlessly.
4. **Automated Tests:**
   - Unit tests verifying subject key generation across single, multiple, and empty scopes.
   - Integration tests verifying independent rate limit exhaustion for `Agent.Read` vs `Apps.Read`.
