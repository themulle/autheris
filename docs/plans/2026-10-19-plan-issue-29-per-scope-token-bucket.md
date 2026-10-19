# PLAN-GOV-29: Per-Scope Token Bucket & Rate Limiting Isolation

**Document ID:** `PLAN-GOV-29`  
**Date:** 2026-10-19  
**Status:** APPROVED WITH CONDITIONS (Phase 3 Security Reviewed)  
**Author:** Solution Architect (`csharp-architect`)  
**Input PRD:** [`docs/plans/2026-10-19-req-issue-29-per-scope-token-bucket.md`](2026-10-19-req-issue-29-per-scope-token-bucket.md)  
**Parent Plan:** [`docs/plans/00-master-plan-overview.md`](00-master-plan-overview.md)  
**GitHub Issue:** #29 Addendum  
**Priority:** Wave 2 (Urgently Missing Feature), Rank 2.17  

---

## 1. Executive Summary & Problem Context

Autheris enforces cost-based token-bucket rate limiting via `ClientTierResolver` and `IRateLimiterService`. In `ClientTierResolver.BuildAuthenticatedSubjectId`, the bucket subject identifier is currently constructed as:
```csharp
var subject = $"user:{tenant}:{userSid}";
if (!string.IsNullOrWhiteSpace(clientId) && !string.Equals(clientId, userSid, StringComparison.OrdinalIgnoreCase))
{
    subject += $":{clientId}";
}
```
This partition scheme **completely ignores OAuth token scopes (`scp`)**. In enterprise deployments, an authenticated user identity interacts with Autheris simultaneously across two distinct operational channels:
1. **Interactive Web Dashboards:** Human users operating under user-delegated tokens with interactive scopes (e.g. `Apps.Read`).
2. **Autonomous AI Agents & Scripts:** Automated background tooling acting on behalf of the user with restricted delegated scopes (e.g. `Agent.Read`).

Because both contexts share the exact same subject key, rapid batches from autonomous AI agents deplete the shared bucket. Human users are abruptly locked out with HTTP 429 `TOO_MANY_REQUESTS`. Conversely, an active user session starves critical agent tasks.

### Core Architectural Goals
1. **Scope-Partitioned Subject Keys:** Key token buckets by `user:{tenant}:{userSid}:{clientId}:{scope}` (or `user:{tenant}:{userSid}:{scope}`) using canonical, sorted bare scope strings.
2. **Deterministic Fallback:** Seamlessly preserve the legacy subject key when scopes are absent or wildcards are used.
3. **Granular Scope Configuration:** Extend `ClientTierOptions` with `ScopeTierMappings` and `ScopeLimits` allowing distinct token capacities and refill rates for agent vs human workloads.
4. **Diagnostic Transparency:** Emit the throttled scope in diagnostic headers (`X-RateLimit-Scope`) or `ProblemDetails.Extensions["scope"]`.
5. **Zero-Allocation Hot Path:** Fast-path single-scope tokens and eliminate string formatting overhead on high-throughput requests.

---

## 2. Verified Code Facts & Delta Analysis

1. **`ClientTierResolver.cs` (`src/Autheris.Application/Caching/Services/ClientTierResolver.cs`):**
   - Lines 57-123 implement `ResolveAsync`:
     - Evaluates API keys $\to$ Claims (`tier`, `client_tier`) $\to$ `RoleTierMappings` $\to$ default `Standard`.
     - Needs to evaluate `ScopeTierMappings` before falling back to `RoleTierMappings` or default `Standard`.
   - Lines 129-163 implement `BuildAuthenticatedSubjectId`:
     - Does not inspect scopes.
     - `TokenAccessScope.GetScopes(principal)` is available in `Autheris.Domain.Security` to retrieve scopes.
2. **`RateLimitingOptions.cs` (`src/Autheris.Domain/Options/Resilience/RateLimitingOptions.cs`):**
   - `ClientTierOptions` contains `TierLimits`, `RoleTierMappings`, `ApiKeys`.
   - Requires adding:
     ```csharp
     public Dictionary<string, string> ScopeTierMappings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
     public Dictionary<string, ClientTierLimitOverride> ScopeLimits { get; init; } = new(StringComparer.OrdinalIgnoreCase);
     ```
3. **`TokenAccessScope.cs` & `ScopeNames.cs` (`src/Autheris.Domain/Security/`):**
   - `TokenAccessScope.GetScopes(ClaimsPrincipal)` parses `scp` and `http://schemas.microsoft.com/identity/claims/scope`.
   - `ScopeNames.ToBareName(string)` strips API prefixes (e.g. `api://autheris/Agent.Read` $\to$ `Agent.Read`).
4. **`RedisRateLimiterService` & `InMemoryRateLimiterService`:**
   - Keys are sanitized (`sanitizedKey = key.Replace("{", "_").Replace("}", "_")`).
   - Adding scopes preserves Redis cluster compatibility and does not exceed memory constraints.

---

## 3. Architectural Design

```
+---------------------------------------------------------------------------------------------------+
|                                      Token Authentication                                         |
|                                                                                                   |
|  JWT ClaimsPrincipal: tenant=acme, oid=usr-42, clientId=web-portal, scp="Apps.Read"               |
|  JWT ClaimsPrincipal: tenant=acme, oid=usr-42, clientId=ai-agent,   scp="Agent.Read"              |
+---------------------------------------------------------------------------------------------------+
                                            |
                                            v
+---------------------------------------------------------------------------------------------------+
|                                  ClientTierResolver.ResolveAsync                                  |
|                                                                                                   |
|  1. Check API Key Cache / Configured Keys                                                         |
|  2. Check Explicit JWT Claim ('tier', 'client_tier')                                              |
|  3. Check ScopeTierMappings & ScopeLimits:                                                        |
|     - Normalize scopes: ScopeNames.ToBareName (e.g. "Agent.Read", "Apps.Read")                    |
|     - Look up ScopeLimits / ScopeTierMappings                                                     |
|     - Pick highest tier among scopes or apply ScopeLimits override                                |
|  4. Check RoleTierMappings (fallback)                                                             |
|  5. Fallback: Standard Tier                                                                       |
|                                                                                                   |
|  Generate Subject ID:                                                                             |
|  - Canonically sort bare scopes: ["Agent.Read"] -> "Agent.Read"                                   |
|  - Subject: user:acme:usr-42:ai-agent:Agent.Read                                                  |
|  - (Independent from user:acme:usr-42:web-portal:Apps.Read)                                       |
+---------------------------------------------------------------------------------------------------+
                                            |
                      +---------------------+---------------------+
                      |                                           |
                      v                                           v
+-------------------------------------------+ +-------------------------------------------+
|    Token Bucket: ...:Apps.Read            | |    Token Bucket: ...:Agent.Read           |
|    - Capacity: 1,000                      | |    - Capacity: 300                        |
|    - Refill: 20 tokens/sec                | |    - Refill: 50 tokens/sec                |
|    - State: 850 remaining                 | |    - State: 12 remaining (Throttled!)     |
+-------------------------------------------+ +-------------------------------------------+
```

### Architectural Decisions

- **Decision D-29.1: Scope Normalization and Canonical Sorting**
  - Scopes can arrive in arbitrary order (e.g. `scp: "B A"` vs `scp: "A B"`) or with full URI prefixes (`api://gateway/Agent.Read`).
  - Rules:
    1. Extract scopes via `TokenAccessScope.GetScopes(principal)`.
    2. Normalize each via `ScopeNames.ToBareName`.
    3. Filter out empty/whitespace strings.
    4. If 0 scopes remain, omit scope suffix (clean fallback to legacy key).
    5. If 1 scope, use directly without sorting allocations.
    6. If $> 1$ scopes, sort alphabetically with `StringComparer.OrdinalIgnoreCase` and join with `+`.
- **Decision D-29.2: Tier Resolution Precedence**
  - Order:
    1. Registered API Key (`ApiKeys`).
    2. Explicit `tier` / `client_tier` JWT claim.
    3. Scope Tier Mapping (`ScopeTierMappings`) & `ScopeLimits`.
    4. Role Tier Mapping (`RoleTierMappings`).
    5. Default authenticated fallback (`Standard`).
  - If a token possesses multiple scopes with conflicting tiers, select the **highest tier** (ordered by `ClientTier` enum: `Free` < `Standard` < `Premium` < `Enterprise`).
- **Decision D-29.3: Scope-Specific Limit Overrides (`ScopeLimits`)**
  - If `ScopeLimits` defines an override for the resolved scope, construct a custom `ClientQuotaPolicy` using that override, superseding the base tier options.

---

## 4. Work Packages

### WP-29-1: Domain Model Options Extension
- Modify `Autheris.Domain.Options.Resilience.RateLimitingOptions`:
  - Extend `ClientTierOptions` with:
    - `public Dictionary<string, string> ScopeTierMappings { get; init; } = new(StringComparer.OrdinalIgnoreCase);`
    - `public Dictionary<string, ClientTierLimitOverride> ScopeLimits { get; init; } = new(StringComparer.OrdinalIgnoreCase);`

### WP-29-2: Subject ID Scope Partitioning
- Update `ClientTierResolver.BuildAuthenticatedSubjectId`:
  - Extract and normalize scopes.
  - Construct subject ID:
    ```csharp
    var canonicalScope = GetCanonicalScopeIdentifier(principal);
    var subject = $"user:{tenant}:{userSid}";
    if (!string.IsNullOrWhiteSpace(clientId) && !string.Equals(clientId, userSid, StringComparison.OrdinalIgnoreCase))
    {
        subject += $":{clientId}";
    }
    if (!string.IsNullOrWhiteSpace(canonicalScope))
    {
        subject += $":{canonicalScope}";
    }
    return subject;
    ```
  - Optimize `GetCanonicalScopeIdentifier`:
    - Fast path: zero allocations for 0 or 1 scope.
    - Uses stackalloc / string pooling for high-frequency scopes (`Agent.Read`, `Apps.Read`).

### WP-29-3: Scope-to-Tier Resolution in `ClientTierResolver`
- In `ClientTierResolver.ResolveAsync`:
  - After claim check, inspect `tierOptions?.ScopeTierMappings` and `tierOptions?.ScopeLimits`.
  - Match bare names of scopes against mappings.
  - If matched, apply resolved tier and custom `ClientQuotaPolicy`.
  - Fall back to role mappings and standard defaults.

### WP-29-4: Diagnostic Header Propagation
- In `ClientQuotaContext`:
  - Add `string? ResolvedScope { get; init; }`.
- When rate limiting rejects a request in HTTP middleware:
  - Attach header `X-RateLimit-Scope: {canonicalScope}` to HTTP 429 response.
  - In `ProblemDetails.Extensions`, set `["scope"] = canonicalScope`.

### WP-29-5: Unit & Concurrency Test Verification
- Unit tests:
  - Canonical scope key generation: single scope, multiple unordered scopes, full URI scopes, no scopes.
  - Precedence verification: API key vs claim vs scope vs role.
  - `ScopeLimits` custom policy application.
- Integration tests:
  - Spin up 2 clients with same user identity but different scopes (`Agent.Read` vs `Apps.Read`).
  - Exhaust `Agent.Read` bucket to HTTP 429.
  - Assert `Apps.Read` client continues to succeed with HTTP 200 without throttling.

---

## 5. Non-Functional Requirements & Performance

- **Zero-Allocation Fast Path:** Tokens with $\le 1$ scope allocate zero auxiliary arrays or sort buffers.
- **Fail-Closed Security:** Corrupted or excessively long scope strings are sanitized and truncated to prevent key injection attacks.
- **Cluster Safety:** Redis key naming preserves cluster slot affinity via `{tenant}:{userSid}` pattern.

---

## 6. Verification Checklist

- [ ] Interactive token (`Apps.Read`) and agent token (`Agent.Read`) have distinct subject keys for identical user SID.
- [ ] Exhausting agent token quota does not throttle interactive user requests.
- [ ] Multi-scope tokens produce identical canonical keys regardless of scope declaration order in the JWT.
- [ ] Throttled responses contain `X-RateLimit-Scope` indicating the exhausted bucket.

---

## 7. Phase 3 Security Review & Binding Conditions (`csharp-security-expert`)

**Review Date:** 2026-10-19  
**Verdict:** APPROVED WITH CONDITIONS  
**Reviewer:** Application Security Expert (`csharp-security-expert`)

### Binding Security Conditions

1. **`SEC-GOV29-01`: Anti-Escalation Precedence & Tier Ceilings**
   - The effective tier resolved from `ScopeTierMappings` MUST NOT elevate a caller above the administrator-assigned maximum tier for that user/role without explicit configuration enablement.
   - If an unprivileged caller obtains a token carrying elevated or third-party scopes, the assigned rate-limiting tier shall be clamped to the caller's maximum permitted role/user tier.
   - Any unrecognized or unparseable tier string specified in configuration or claims MUST fail closed to the lowest safe default (`Standard` or `Free`), never granting elevated privilege.

2. **`SEC-GOV29-02`: Canonical Scope Normalization & Key Delimiter Sanitization**
   - In `GetCanonicalScopeIdentifier`, bare scope names MUST be lowercased, trimmed of whitespace, deduplicated, and sorted with ordinal comparison (`StringComparer.OrdinalIgnoreCase`).
   - Scope names containing delimiter characters (`:`, `+`, `{`, `}`, `\r`, `\n`) MUST be sanitized or rejected to prevent subject key injection, delimiter collision attacks, or Redis cluster slot misalignment.
   - To guard against memory exhaustion and key-length DoS via crafted JWT tokens with massive scope arrays, enforce a maximum canonical scope length (e.g. 256 characters) and maximum distinct scope count (e.g. 32 scopes).

3. **`SEC-GOV29-03`: Zero-Downtime Fallback & Fail-Closed Quota Enforcement**
   - When a token lacks OAuth scopes or contains solely unmapped scopes, `ClientTierResolver` MUST cleanly fall back to standard subject partitioning (`user:{tenant}:{userSid}` or `user:{tenant}:{userSid}:{clientId}`) without throwing unhandled exceptions.
   - The fallback must preserve standard rate-limiting controls rather than bypassing them.
   - Throttled responses emitting `X-RateLimit-Scope` and `ProblemDetails.Extensions["scope"]` MUST strictly reflect sanitized bare scope names, avoiding reflection of unsanitized caller inputs.

### Mandatory Security Test Suite

All conditions must be verified with dedicated tests marked `[Trait("Category", "Security")]`:
- `T_SEC_GOV29_01_AntiEscalationTierCeiling`: Verifies that a user identity with a restricted tier ceiling cannot elevate to `Enterprise` or bypass limits simply by presenting elevated scopes.
- `T_SEC_GOV29_02_ScopeCanonicalizationAndInjectionSanitization`: Verifies that unordered, uppercase, duplicate, and delimiter-containing scope values produce safe, deterministic canonical keys without subject key or Redis command injection.
- `T_SEC_GOV29_03_UnmappedScopeFallbackFailClosed`: Verifies that tokens with missing, unmapped, or malformed scopes fall back gracefully to the standard tier and legacy subject key with standard rate-limiting intact.

