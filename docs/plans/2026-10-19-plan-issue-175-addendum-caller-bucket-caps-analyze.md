# PLAN-GOV-175B: Caller Bucket State & Active System Caps in Static Query Analysis (`/analyze`)

**Document ID:** `PLAN-GOV-175B`  
**Date:** 2026-10-19  
**Status:** APPROVED WITH CONDITIONS (Phase 3 Security Reviewed)  
**Author:** Solution Architect (`csharp-architect`)  
**Input PRD:** [`docs/plans/2026-10-19-req-issue-175-addendum-caller-bucket-caps-analyze.md`](2026-10-19-req-issue-175-addendum-caller-bucket-caps-analyze.md)  
**Parent Plan:** [`docs/plans/00-master-plan-overview.md`](00-master-plan-overview.md)  
**Parent Feature:** `REQ-GOV-175`, `PLAN-GOV-175`, `F-GOV-20`  
**GitHub Issue:** #175 Addendum  
**Priority:** Wave 2 (Enhancement), Rank 2.7b  

---

## 1. Executive Summary & Problem Context

In Issue #175 (`PLAN-GOV-175`), `POST /api/v1/governance/queries/analyze` was unlocked for read-only tokens and equipped with AST cost evaluation via Hot Chocolate's `QueryCostAnalyzerRule`.

However, the analysis response currently focuses exclusively on static validation, leaving callers and autonomous AI agents unaware of their **operational runtime boundaries**:
1. **Blind Quota State:** Clients cannot see how many tokens remain in their token bucket, their maximum burst capacity, or their refill rate.
2. **Opaque Gateway Ceilings:** Clients cannot inspect active system ceilings (`MaxAllowedComplexity`, `MaxResponseRows`, `MaxAllowedOffset`, `MaxNestingDepth`, `MaxAggregateRowBudget`).

Without this information, autonomous AI agents cannot budget execution or paginate safely, frequently colliding with HTTP 429 `TOO_MANY_REQUESTS` or `RESPONSE_TOO_LARGE` errors.

### Core Architectural Goals
1. **Non-Destructive Bucket Inspection:** Provide a read-only `GetBucketStateAsync` method in `IRateLimiterService` that calculates available tokens and refill dynamics without deducting tokens or acquiring exclusive write locks.
2. **System Caps Discovery:** Package active tenant-level gateway limits and caller-specific complexity ceilings into an immutable `ActiveSystemCaps` record.
3. **Integrated Response Model:** Extend `QueryAnalysisResult` with `CallerQuotaSummary? CallerQuota` containing tier identity, bucket state, and active system ceilings.
4. **Zero-Read / Zero-Mutation Guarantee:** Ensure zero source database reads, zero Redis write mutations, and zero quota decrement during `/analyze` execution.

---

## 2. Verified Code Facts & Delta Analysis

1. **`IRateLimiterService` (`src/Autheris.Application/Interfaces/IRateLimiterService.cs`):**
   - Declares `CheckCostQuotaAsync`, `ReserveCostQuotaAsync`, and `SettleCostQuotaAsync`.
   - All existing methods deduct or mutate token state.
   - Missing: A non-destructive inspection method `GetBucketStateAsync(string key, ClientQuotaPolicy policy, CancellationToken ct = default)`.
2. **Rate Limiter Implementations (`src/Autheris.Infrastructure/RateLimiting/`):**
   - `InMemoryRateLimiterService`:
     - Stores `ConcurrentDictionary<string, TokenBucket> _costBuckets`.
     - `TokenBucket` contains `Tokens` (double) and `LastRefillTimestamp` (long Stopwatch timestamp).
     - Bucket state can be derived by reading current fields and applying elapsed refill:
       $$\text{currentTokens} = \min(\text{capacity}, \text{tokens} + \text{elapsedSec} \times \text{refillRate})$$
   - `RedisRateLimiterService`:
     - Stores hash at `{prefix}ratelimit:quota:{sanitizedKey}` with fields `tokens` and `last_refill`.
     - Non-destructive inspection can be implemented via `HMGET` (or `HGETALL`) in a single read trip with 0 Redis writes.
3. **`QueryAnalysisModels.cs` (`src/Autheris.Application/Governance/Analysis/QueryAnalysisModels.cs`):**
   - Lines 224-229 define `QueryAnalysisResult`.
   - Adding `CallerQuotaSummary? CallerQuota = null` as a defaulted trailing record parameter ensures 100% backward binary and JSON compatibility.
4. **`QueryAnalysisService.cs` (`src/Autheris.Application/Governance/Analysis/QueryAnalysisService.cs`):**
   - Both `IClientTierResolver` and `IRateLimiterService` reside in `Autheris.Application`, allowing direct constructor injection without introducing any circular project dependencies.

---

## 3. Architectural Design

```
+---------------------------------------------------------------------------------------------------+
|                           HTTP Layer: POST /api/v1/governance/queries/analyze                     |
|                                                                                                   |
|  CallerPrincipal (JWT with UserSid, Tenant, Scopes) -> QueryAnalysisAndProfilingEndpoints         |
+---------------------------------------------------------------------------------------------------+
                                            |
                                            v
+---------------------------------------------------------------------------------------------------+
|                        Application Layer: QueryAnalysisService.AnalyzeQueriesAsync                |
|                                                                                                   |
|  1. Static AST & SQL Parsing (0 DB reads, 0 mutations)                                            |
|  2. Resolve Caller Quota Context:                                                                 |
|     - clientTierResolver.ResolveAsync(callerPrincipal, apiKey: null, clientIp: null)              |
|       => Returns ClientQuotaContext(SubjectId, Tier, Policy)                                      |
|  3. Probe Bucket State (Non-Destructive):                                                         |
|     - rateLimiterService.GetBucketStateAsync(quotaContext.SubjectId, quotaContext.Policy)         |
|       => Returns CallerBucketState(Capacity, CurrentBalance, RefillRatePerSecond)                 |
|  4. Assemble System Caps:                                                                         |
|     - ActiveSystemCaps(MaxAllowedComplexity, MaxResponseRows, MaxAllowedOffset, ...)               |
|  5. Assemble CallerQuotaSummary:                                                                  |
|     - CallerQuotaSummary(Tier, BucketState, SystemCaps)                                           |
|  6. Attach to QueryAnalysisResult:                                                                |
|     - return new QueryAnalysisResult(..., CallerQuota: callerQuota);                              |
+---------------------------------------------------------------------------------------------------+
                                            |
                      +---------------------+---------------------+
                      |                                           |
                      v (Non-Destructive Probe)                   v (Non-Destructive Probe)
+-------------------------------------------+ +-------------------------------------------+
|    InMemoryRateLimiterService             | |    RedisRateLimiterService                |
|    - _costBuckets.TryGetValue(key)        | |    - HMGET key tokens last_refill         |
|    - Read tokens + elapsed refill         | |    - Compute elapsed refill in memory     |
|    - ZERO write / ZERO deduction          | |    - ZERO write / ZERO deduction          |
+-------------------------------------------+ +-------------------------------------------+
```

### Architectural Decisions

- **Decision D-175B.1: Pure Read-Only Inspection Contract**
  - `IRateLimiterService.GetBucketStateAsync` MUST NOT invoke Lua scripts that mutate keys or reset TTLs.
  - If a bucket key has not yet been initialized (first request), it returns `CurrentBalance = Capacity`, representing a fresh, untaxed bucket.
- **Decision D-175B.2: Graceful Degradation on Probe Failure**
  - If Redis is down or unreachable during static analysis, `RedisRateLimiterService` falls back to the in-memory limiter or returns `BucketState: null`.
  - Static query analysis MUST NOT fail simply because the rate-limit telemetry probe experienced a transient error.
- **Decision D-175B.3: Caps Precision & Scope**
  - `ActiveSystemCaps` exposes gateway governance parameters:
    - `MaxAllowedComplexity`: derived from caller tier policy or gateway default (`MaxAllowedComplexity`).
    - `MaxResponseRows`: active pagination row clamp (`GatewayOptions.GraphQL.MaxResponseRows`).
    - `MaxAllowedOffset`: active offset ceiling (`GatewayOptions.GraphQL.MaxAllowedOffset`).
    - `MaxNestingDepth`: fixed query nesting limit (10).
    - `MaxAggregateRowBudget`: budget across nested relations (`GatewayOptions.GraphQL.MaxAggregateRowBudget`).

---

## 4. Work Packages

### WP-175B-1: Domain & Application Model Contracts
- In `Autheris.Application.Interfaces.IRateLimiterService`:
  ```csharp
  public sealed record CallerBucketState(
      int Capacity,
      double CurrentBalance,
      double RefillRatePerSecond);

  public interface IRateLimiterService
  {
      // ... existing methods ...
      Task<CallerBucketState?> GetBucketStateAsync(string key, ClientQuotaPolicy policy, CancellationToken ct = default);
  }
  ```
- In `Autheris.Application.Governance.Analysis.QueryAnalysisModels`:
  ```csharp
  public sealed record ActiveSystemCaps(
      int MaxAllowedComplexity,
      int MaxResponseRows,
      int MaxAllowedOffset,
      int MaxNestingDepth,
      int MaxAggregateRowBudget);

  public sealed record CallerQuotaSummary(
      string ClientTier,
      CallerBucketState? BucketState,
      ActiveSystemCaps SystemCaps);
  ```
- Extend `QueryAnalysisResult` with trailing optional `CallerQuotaSummary? CallerQuota = null`.

### WP-175B-2: Non-Destructive In-Memory Bucket Probe
- In `InMemoryRateLimiterService`:
  - Implement `GetBucketStateAsync`:
    ```csharp
    public Task<CallerBucketState?> GetBucketStateAsync(string key, ClientQuotaPolicy policy, CancellationToken ct = default)
    {
        var capacity = policy.MaxTokensCapacity;
        var refillRate = policy.TokenRefillRatePerSecond > 0 ? policy.TokenRefillRatePerSecond : 1.0;
        var currentTimestamp = Stopwatch.GetTimestamp();

        if (!_costBuckets.TryGetValue(key, out var bucket))
        {
            return Task.FromResult<CallerBucketState?>(new CallerBucketState(capacity, capacity, refillRate));
        }

        lock (bucket.Lock)
        {
            var elapsed = Stopwatch.GetElapsedTime(bucket.LastRefillTimestamp, currentTimestamp).TotalSeconds;
            var tokens = Math.Min(capacity, bucket.Tokens + (elapsed * refillRate));
            return Task.FromResult<CallerBucketState?>(new CallerBucketState(capacity, Math.Round(tokens, 2), refillRate));
        }
    }
    ```

### WP-175B-3: Non-Destructive Redis Bucket Probe
- In `RedisRateLimiterService`:
  - Implement `GetBucketStateAsync`:
    - Issue Redis `HMGET` for fields `tokens` and `last_refill`.
    - If empty, return full capacity.
    - If populated, calculate elapsed time against `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`.
    - Calculate current balance without calling `HSET` or `EXPIRE`.
    - Wrap in try/catch to fall back to in-memory probe on Redis error.

### WP-175B-4: `QueryAnalysisService` Integration
- In `QueryAnalysisService`:
  - Inject `IClientTierResolver? clientTierResolver = null` and `IRateLimiterService? rateLimiterService = null`.
  - In `AnalyzeQueriesAsync`:
    - Resolve caller quota:
      ```csharp
      CallerQuotaSummary? callerQuota = null;
      if (_clientTierResolver != null)
      {
          var quotaCtx = await _clientTierResolver.ResolveAsync(callerPrincipal, apiKey: null, clientIp: null, ct).ConfigureAwait(false);
          CallerBucketState? bucketState = null;
          if (_rateLimiterService != null)
          {
              bucketState = await _rateLimiterService.GetBucketStateAsync(quotaCtx.SubjectId, quotaCtx.Policy, ct).ConfigureAwait(false);
          }

          var systemCaps = new ActiveSystemCaps(
              MaxAllowedComplexity: maxAllowedCost,
              MaxResponseRows: _gatewayOptions.GraphQL?.MaxResponseRows > 0 ? _gatewayOptions.GraphQL.MaxResponseRows : 1000,
              MaxAllowedOffset: _gatewayOptions.GraphQL?.MaxAllowedOffset >= 0 ? _gatewayOptions.GraphQL.MaxAllowedOffset : 10000,
              MaxNestingDepth: 10,
              MaxAggregateRowBudget: _gatewayOptions.GraphQL?.MaxAggregateRowBudget > 0 ? _gatewayOptions.GraphQL.MaxAggregateRowBudget : 50000);

          callerQuota = new CallerQuotaSummary(quotaCtx.Tier.ToString(), bucketState, systemCaps);
      }
      ```
    - Pass `CallerQuota: callerQuota` to `QueryAnalysisResult`.

### WP-175B-5: Automated Unit & Integration Testing
- Unit tests:
  - `InMemoryRateLimiterService.GetBucketStateAsync` returns initial full balance.
  - After `CheckCostQuotaAsync` deducts tokens, `GetBucketStateAsync` verifies non-destructive read (does not alter balance).
  - Time advance verifies proper refill calculation.
- Integration tests:
  - `POST /api/v1/governance/queries/analyze` asserts JSON contains `callerQuota` with `clientTier`, `bucketState`, and `systemCaps`.
  - Verify invoking `/analyze` 10 times in a row leaves bucket balance unchanged.

---

## 5. Non-Functional Requirements & Performance

- **Zero Deduction Guarantee:** Calling `/analyze` must never consume rate limit tokens.
- **Probe Latency:** $< 0.1$ ms in-memory, $< 0.5$ ms Redis `HMGET`.
- **Fault Tolerance:** Transient caching failures fail open for the bucket probe (returning `null`) while completing static query analysis.

---

## 6. Verification Checklist

- [ ] `/analyze` response payload includes `callerQuota` object.
- [ ] `callerQuota.bucketState` displays accurate capacity and current balance.
- [ ] Calling `/analyze` repeatedly does not deplete tokens or cause rate limiting.
- [ ] `systemCaps` accurately surfaces configured `MaxResponseRows`, `MaxAllowedOffset`, and `MaxAllowedComplexity`.

---

## 7. Phase 3 Security Review & Binding Conditions (`csharp-security-expert`)

**Review Date:** 2026-10-19  
**Verdict:** APPROVED WITH CONDITIONS  
**Reviewer:** Application Security Expert (`csharp-security-expert`)

### Binding Security Conditions

1. **`SEC-GOV175B-01`: Non-Destructive Isolation & Read-Only Probe Guarantee**
   - `IRateLimiterService.GetBucketStateAsync` MUST be strictly non-destructive: zero token decrement, zero quota mutation, zero key TTL updates, and zero write locks.
   - For `RedisRateLimiterService`, inspection MUST issue read-only commands (`HMGET` on `tokens` and `last_refill`); under no circumstances may `HSET`, `EXPIRE`, or Lua mutation scripts be invoked during static query analysis.
   - For `InMemoryRateLimiterService`, reading bucket state MUST NOT mutate `TokenBucket.Tokens` or `LastRefillTimestamp`.

2. **`SEC-GOV175B-02`: Read-Only Token Route Authorization & Fail-Closed Boundaries**
   - The `/api/v1/governance/queries/analyze` endpoint MUST strictly adhere to `SEC-GOV175-01`: exact path matching only, permitting read-only tokens while strictly blocking access to any mutation routes (`/consents/*`, `/plans/*`, `/hitl/*`).
   - If a caller's token is invalid, expired, or untrusted, the endpoint MUST fail closed immediately with HTTP 401/403 before resolving caller tier or bucket telemetry.

3. **`SEC-GOV175B-03`: Uniform Information Density & Timing Oracle Defense**
   - If a bucket key has not yet been initialized in Redis or memory (e.g. first request by a caller), `GetBucketStateAsync` MUST uniformly return `CurrentBalance = Capacity`, preventing attackers from probing whether target subjects have initiated activity.
   - Transient failures connecting to Redis or rate-limiting storage MUST degrade gracefully to `BucketState: null` without failing static analysis and MUST NOT expose internal connection strings, hostnames, cluster topology, or stack traces in responses.

### Mandatory Security Test Suite

All conditions must be verified with dedicated tests marked `[Trait("Category", "Security")]`:
- `T_SEC_GOV175B_01_NonDestructiveBucketStateInspection`: Verifies that invoking `GetBucketStateAsync` repeatedly produces 0 token mutations in Redis and memory.
- `T_SEC_GOV175B_02_ReadOnlyTokenAnalyzeAccess`: Verifies that read-only tokens can access `/analyze` and receive `callerQuota` while mutation endpoints remain forbidden (`403 Forbidden`).
- `T_SEC_GOV175B_03_UniformInformationDensityAndErrorMasking`: Verifies that fresh/uninitialized buckets return uniform full capacity and Redis connection faults fail open gracefully without leaking internal infrastructure details.

