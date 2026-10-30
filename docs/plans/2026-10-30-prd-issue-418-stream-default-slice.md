# PRD / fix note: table stream default slice never admitted (issue #418)

Parent: [00-master-plan-overview.md](00-master-plan-overview.md). Related: #417 (GraphQL cost config, same bucket model).

## Findings (verified on origin/main)

1. `TableStreamEndpoints.cs:262-265`: `sliceLimit = Limit > 0 ? min(Limit, 10000) : 5000`, `reservedCost = 10 + sliceLimit`. Reserved at `:284` via `ReserveCostQuotaAsync`.
2. Bucket capacities (`ClientTierModels.cs:23-26`): Free 100, Standard 1000, Enterprise 10000, Internal 50000. Overridable via `ClientTierOptions.TierLimits` and scope limits. Largest admissible limit is `capacity - 10`.
3. All-or-nothing admission: in-memory `InMemoryRateLimiterService.cs:340` (`Tokens >= requestedCost`) and Redis/Garnet Lua `RedisRateLimiterService.cs:87` (`tokens >= requested_cost`), which also falls back to the in-memory limiter (`:311,319`). No PostgreSQL limiter exists. Behaviour is identical.
4. Impact: default 5010 is never admitted for Free (cap 100) and Standard (cap 1000). An explicit `limit` above `capacity - 10` is never admitted for any tier (Enterprise 9991..10000, Internal none, since max slice is 10000). No-tier-resolver path uses Internal, so it is unaffected.
5. Client today: 429 `TOO_MANY_REQUESTS` ProblemDetails with `Retry-After` and `retryAfterSeconds` (`:292`, `ApiProblems.cs:148`). Retry-After is computed as `ceil((cost - tokens) / refill)` (`InMemory...:344-348`), e.g. about 201 s Standard and about 2455 s Free, but the bucket is capped at capacity, so the wait never makes the request admissible. A well-behaved client retries forever.
6. Other channels: GraphQL `CostAndQuotaMiddleware.cs:233` reserves the computed cost, but a `MaxCostPerQuery` check (400) precedes it and the per-query max is below capacity in all default tiers, so only misconfigured overrides can hit it. Mid-stream calls (`:411,:540`) use cost 0. Bulk and OData do not reserve. Only the table stream is affected by defaults.

Classification: bug (defaults inconsistent with tier config), small, behaviour change acceptable (no production yet).

## Requirements

1. Default slice = `min(5000, capacity - 10)`, where capacity is `policy.MaxTokensCapacity` of the resolved policy (after tier and scope overrides). Resolve the policy before computing the slice (reorder `:262-282`).
2. Explicit `limit` > `capacity - 10` (after the existing 10000 clamp) is rejected before reservation with a governed 400 (`ApiErrorCodes.BadRequest`, or a new governed code, see Q1) and no `Retry-After`. Detail names the largest admissible limit, e.g. "limit must not exceed 990 for this caller".
3. Do not record `DenyRateLimit` usage for this validation reject; no bucket tokens consumed.
4. If `capacity <= 10` (degenerate override): fail closed with the same 400 and no limit offered (detail generic).
5. No tier name, refill rate or capacity in the body; only the derived max limit. Same body in Production and Development. Written through `ApiProblems.WriteAsync` (no legacy error body, guard on main stays green).
6. Optional hardening: genuine 429 for a never-admissible reservation must not occur anymore on the stream path.

## Benchmark

- 429 means retry later can succeed; a request that can never succeed is a client error: 400/422 (RFC 9110 15.5.1/15.5.21). 413 is for body size, not applicable. 422 vs 400: repo uses 400 for argument validation of this endpoint (`:255`), so keep 400.
- `Retry-After` only on 429/503 where waiting helps; never on the 400.
- Capping the default is the least surprising (GitHub, Stripe style pagination caps) and silently safe; an explicit over-limit gets a clear error rather than silent clamping, so clients do not miss rows unnoticed.

## Tests (TDD, `TableStreamEndpointsTests.cs`)

1. Standard policy, no limit: admitted, reservation of 1010; Free: 100.
2. Enterprise/Internal default unchanged (5010).
3. Explicit limit 990 Standard admitted; 991 returns 400 with governed code, body mentions 990, no `Retry-After`, no reservation call, no usage `DenyRateLimit`.
4. Enterprise limit 10000 returns 400 naming 9990.
5. Override capacity (scope limit 500) uses 500-based max.
6. Capacity <= 10 fails closed.
7. Body has no tier/capacity members in Production and Development; error-body guard test passes.
8. Genuine exhausted bucket still returns 429 with Retry-After.

## Open questions (recommended default)

1. New governed code (e.g. `STREAM_LIMIT_EXCEEDS_QUOTA`) vs `BAD_REQUEST`? Default: `BAD_REQUEST` plus detail, no new code and no extension allowlist change.
2. Machine-readable `maxLimit` extension? Default: no (detail only), avoids allowlist change; revisit if clients need it.
3. Reject vs clamp an explicit over-limit? Default: reject.
4. Align #417 cost config in the same PR? Default: no, separate; note shared capacity source.

## Phase 3 rulings (csharp-security-expert, 2026-10-30)

Verdict: APPROVED WITH CONDITIONS. Code facts verified: `TableStreamEndpoints.cs` ~L260-295 (slice and reservation), `ClientQuotaPolicy.MaxTokensCapacity` is the capacity used by both `InMemoryRateLimiterService` and `RedisRateLimiterService` (`ReserveCostQuotaAsync` delegates to `CheckCostQuotaAsync`, capacity passed as argument).

**Rulings**
1. Disclosure: ALLOWED. The detail may name the caller's own largest admissible limit (`capacity - 10`) in all environments. It exposes only the caller's own bucket size, which the caller already learns from 429 behaviour and is needed to correct the request; it never names a tier, scope or other policy. No Development-only split. The detail must not contain the tier name, scope name, capacity itself, refill rate or key.
2. Probing: the policy comes only from `IClientTierResolver.ResolveAsync(HttpContext.User, X-API-Key, clientIp)`; no request field (body, query, headers other than the existing API key) may select policy, scope or tenant. Only the caller's own number is revealed, so overrides are not enumerable across principals. The API-key header stays the only existing input and is already resolved/validated by the resolver.
3. Quota bypass: no minimal charge on the 400 path. Rejection is O(1) with no I/O beyond the resolver already used on the 429 path, and no row or data access happens before the check. The request still passes authn/authz, table access and argument validation (steps 1-6) first. A validation limiter is out of scope; the general HTTP rate limiter and the existing pre-endpoint controls cover flooding.
4. Audit/usage: no `DenyRateLimit` usage record (it is not a rate-limit denial and would skew billing/metrics). The request is audited through the existing `WithAudit(AuditLevel.Full, TableQuery)` filter as a failed 4xx; if a `ConsumerOutcome` for invalid request exists use it, otherwise record none. The audit entry must not carry the capacity or the computed maximum beyond what the response already carries.
5. Redis parity: the admission check is computed from the resolved policy before any limiter call, so it is backend-independent. When the limiter is null, Redis is unreachable or errors, existing fail-behaviour is unchanged; the 400 never depends on limiter state. Default slice and max are derived from `policy.MaxTokensCapacity` only.
6. Legacy error body: the 400 must go through `ApiProblems.WriteAsync` with `ApiErrorCodes.BadRequest`; no `Results.BadRequest(new { error = ... })`, no raw exception text, no extension member, so the existing no-legacy-body guard and extension allowlist stay green.

**Binding conditions**
- I418-SC-01: Resolve the policy (resolver or `ForTier(Internal)` fallback, unchanged) before computing the slice, from the authenticated principal only; never from request input.
- I418-SC-02: Default slice = `min(5000, capacity - 10)`; overflow-safe arithmetic (`long` or checked); `capacity <= 10` (including 0, negative, int.MaxValue edge) yields the 400 fail-closed, never a zero or negative slice.
- I418-SC-03: Explicit `limit > capacity - 10` is rejected (no clamping) with 400 before any `ReserveCostQuotaAsync`, no `Retry-After`, no `X-RateLimit-Scope` header, no `DenyRateLimit` record.
- I418-SC-04: Detail names only the numeric largest admissible limit; no tier, scope, capacity, refill rate or subject id; fixed text, no echo of the request value.
- I418-SC-05: The existing 10000 hard cap and non-positive-limit handling stay; the final `reservedCost` never exceeds capacity.
- I418-SC-06: Genuinely exhausted bucket remains 429 with `Retry-After`, unchanged; settle path unchanged.
- I418-SC-07: 400 only via `ApiProblems.WriteAsync` and governed code `BAD_REQUEST`; no new code, no allowlist change.
- I418-SC-08: No logging of the policy capacity at Information or above on the rejection path.

**Mandatory tests** (`[Trait("Category","Security")]`)
- I418-T-S01 `Stream_ExplicitLimitAboveCapacityMinusBase_Returns400_WithoutReservation` (limiter mock never called, no Retry-After, no usage record).
- I418-T-S02 `Stream_CapacityAtOrBelowBaseCost_FailsClosedWith400` (capacity 10, 0, negative).
- I418-T-S03 `Stream_FreeAndStandardTier_DefaultSlice_IsAdmitted` (reservation equals capacity at most).
- I418-T-S04 `Stream_RejectionDetail_ContainsOnlyMaxLimit_NoTierScopeOrCapacity`.
- I418-T-S05 `Stream_PolicyResolvedFromPrincipal_IgnoresRequestSuppliedTierOrScope` (headers/body/query tier or scope ignored).
- I418-T-S06 `Stream_ExhaustedBucket_Still429WithRetryAfter`.
- I418-T-S07 `Stream_Rejection_UsesProblemDetailsBadRequest_NoLegacyBody`.
- I418-T-S08 `Stream_DefaultSlice_SameForInMemoryAndRedisLimiter` (policy-derived, limiter-independent; stub limiters).
- I418-T-S09 `Stream_OverflowCapacity_NoArithmeticOverflow` (int.MaxValue capacity, limit 10000 cap).

Open questions 1-3 resolved as the PRD defaults (BAD_REQUEST, detail only, reject); question 4 stays out of scope.
