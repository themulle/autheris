# Plan: flaky `T_SEC_GOV175B_01` and the bucket-state projection refill rate

Id: `PLAN-FLAKY-GOV175B-01`. Owner decision 2026-10-25: fix directly, no issue. Lean lifecycle (no PRD; architect and security conditions written by the delivery PM, "default taken").

## Symptom

`QueryAnalysisBucketSecurityTests.T_SEC_GOV175B_01_NonDestructiveBucketStateInspection` failed once in a full local unit run (Lane A) and passed on rerun.

## Root cause (product)

`GetBucketStateAsync(key, policy)` in `InMemoryRateLimiterService` and `RedisRateLimiterService` projects the current balance with `refillRate = policy.TokenRefillRatePerSecond > 0 ? policy.TokenRefillRatePerSecond : 1.0`. The consuming paths (in-memory `CheckCostQuotaAsync`, Redis Lua scripts) refill with the real policy rate and use the `1.0` fallback only to compute a retry wait. With a policy refill rate of `0` the read-only probe therefore reports one token per elapsed second that the bucket will never grant.

The test consumes 150 of 1000 tokens with rate `0` and expects every probe to report exactly `850`. When at least 5 ms pass between the consumption and a probe (loaded runner), the probe reports `850.01` (`Math.Round(850 + 0.005, 2)`) and the exact assertion fails. The Redis half has the same defect, hidden by a 1-token tolerance.

## Fix

- **F1 (product).** Both projections use the real policy rate (negative values treated as `0`) for refill and report that rate in `CallerBucketState.RefillRatePerSecond`. `QueryAnalysisService` already treats a non-positive rate as "no refill estimate" (`RefillRatePerSecond > 0` guard), so a reported `0` is safe. The retry-wait fallback in the consuming paths is unchanged.
- **F2 (clock).** `InMemoryRateLimiterService` takes an optional `TimeProvider` (default `TimeProvider.System`) and uses it for every timestamp and elapsed-time computation instead of `Stopwatch`, so refill and projection are testable without wall-clock waits. `RedisRateLimiterService` takes an optional `TimeProvider` for the probe's `now` as well, if the change stays local.
- **F3 (regression tests, written first).** Deterministic: rate `0`, consume, advance a `FakeTimeProvider` by minutes, the in-memory probe reports exactly the post-consumption balance and rate `0`; Redis probe with a stored `last_refill` far in the past reports the stored balance exactly. A positive-rate projection test pins that refill still works (advance the fake clock, expect `balance + elapsed * rate` capped at capacity).
- `T_SEC_GOV175B_01` keeps its assertions; the Redis tolerance may be tightened, never loosened.

## Binding conditions (default taken)

- `SEC-GOV175B-F1`: the read-only probe never reports more tokens than the consuming path would grant at the same instant.
- `SEC-GOV175B-F2`: no sleeps, retries or weakened assertions in changed tests.
- `SEC-GOV175B-F3`: probes stay non-destructive (SEC-GOV175B-01 unchanged); consuming-path semantics unchanged apart from the clock source.

## Verification

Old test looped under CPU load to reproduce; new and changed tests looped 200x under the same load plus the full unit suite.

## Addendum: flaky `T_PROC152I2_14_UndeclaredSqlState_DbException_StaysOn500Path_NoMapCode`

Seen on PR #355 CI (passes on `main`). `AssertNoDbText` asserts that no log message or state contains the database-text marker `4711`. `GovernedProcedureExecutionService` logs a trace id (`Activity.Current?.TraceId`, else `Guid.NewGuid().ToString("N")`): 32 random hex characters, which contain `4711` with a probability of about 29/65536 per id. The log in CI read `... (number (null), trace 4...`: the trace id itself contained the marker. Root cause is in the test: the leak marker is a string that legitimate random hex output can contain.

- **F4 (test).** Leak markers in the procedure tests become collision-proof: a value marker that contains characters outside `[0-9a-f-]` (so no hex trace id, GUID or hash can contain it), asserted in full, plus the unchanged `dbo.Secret` table marker. Assertions keep their scope (every log message, every log state, audit details, exception and mapped messages).
- **F5 (regression).** A deterministic test runs the failure path under an `Activity` whose trace id contains the old marker `4711` and shows that the assertions still hold (red with the old marker, green with the new one).
- Other unit tests asserting `ShouldNotContain("4711")` are audited: where the checked text can contain a random hex id, the same marker change applies; otherwise they stay unchanged.
- `SEC-PROC-F4`: no assertion is removed or narrowed; the marker change only removes false positives from random identifiers.
