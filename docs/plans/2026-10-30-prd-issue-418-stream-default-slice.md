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
