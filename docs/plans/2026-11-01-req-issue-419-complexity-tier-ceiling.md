# PRD: GraphQL Root Lists - Admission Estimate and Per-Tier Complexity Ceiling (Issue #419 / WP-B)

**Document ID:** `REQ-COST-419`  
**Tracking Issue:** [#419](https://github.com/themulle/autheris/issues/419) (follow-up of [#417](https://github.com/themulle/autheris/issues/417), WP-B)  
**Lifecycle Phase:** Phase 1 (Product Manager Qualification & PRD)  
**Author:** Product Manager  
**Date:** 2026-11-01  

---

## 1. Executive Summary & Problem Statement

In Autheris v1.5.0, the GraphQL query admission price is calculated by `QueryCostAnalyzerRule`:
$$\text{Cost} = (\text{Base List Cost} \times \text{effectiveRows}) + (\text{effectiveRows} \times \sum \text{Field Costs})$$

Every scalar field selected on a root list has an individual field cost of $1$. When a client requests a reasonable page size such as `first: 100` on a table with 10 scalar fields:
$$\text{Child Cost} = 10 \times 100 = 1,000$$
Combined with the list multiplier base, the query price exceeds $1,000$.

Currently:
1. `GatewayOptions.GraphQL.MaxAllowedComplexity` acts as a static global ceiling with a default of `500` (range `[100, 10000]`).
2. Even when an operator configures an `Enterprise` or `Internal` tier with `MaxCostPerQuery = 1000` or `5000` in `RateLimitingOptions.ClientTiers.TierLimits`, `CostAndQuotaMiddleware` clamps the effective maximum cost down to `MaxAllowedComplexity` (`500`).
3. Furthermore, HotChocolate's document validation rule `QueryCostAnalyzerRule` rejects any query exceeding `MaxAllowedComplexity` at validation time before caller authentication or tier resolution occurs.
4. Plain scalar database columns (which are fetched in a single SQL SELECT statement with zero additional database roundtrips) are priced at the same heavy weight ($1.0 \times \text{effectiveRows}$) as complex computed fields or nested relations.

This forces clients into artificially small pages (e.g. `first: 10` or `20`), increasing the number of network roundtrips and placing higher aggregate load on backend data sources than a single bounded page read.

---

## 2. Industry Benchmark & Precedent Analysis

- **GitHub GraphQL API:** Uses a node-limit calculation based on the product of `first`/`last` arguments (capped at 500,000 nodes total, with 5,000 rate limit points per hour). Scalar fields on an admitted node do not compound node consumption.
- **Shopify GraphQL API:** Implements calculated query cost with reserve-and-refund. Queries reserve their calculated cost from a leaky bucket prior to execution, and actual execution refunds unconsumed cost. High-trust or Plus clients receive higher cost limits and bucket leak rates.

Autheris already implements the Shopify reserve-and-refund model via `CostAndQuotaMiddleware` and `ConsumerUsageRecorder`. However, Autheris's admission calculation lacks tier awareness and treats cheap scalar columns identically to expensive nested sub-queries.

---

## 3. Product Requirements & Acceptance Criteria

### REQ-419-01: Per-Tier and Per-Scope Complexity Ceiling Overrides
- Operators can configure `MaxAllowedComplexity` per client tier (`Free`, `Standard`, `Enterprise`, `Internal`) and per scope in `RateLimitingOptions.ClientTiers.TierLimits` and `ScopeLimits`.
- The global `GatewayOptions.GraphQL.MaxAllowedComplexity` remains the default ceiling (default `500`, range `100..10000`).
- A hard safety ceiling (`MaxComplexitySafetyCeiling = 10000`) is enforced at startup across all tiers and scope overrides. Any override exceeding `10000` or less than `100` causes startup validation to fail closed (`ValidationException`).

### REQ-419-02: Document Validation vs. Execution Admission Decoupling
- HotChocolate's `IDocumentValidatorRule` (`QueryCostAnalyzerRule`) is cached across requests (`IsCacheable = true`). It enforces admission against the maximum admissible ceiling configured in the system (`MaxAllowedAdmissionCeiling`), ensuring valid high-tier queries pass schema validation.
- `CostAndQuotaMiddleware` enforces the exact per-tier or per-scope ceiling for the authenticated caller:
  $$\text{EffectiveCeiling} = \min(\text{ClientPolicy.MaxCostPerQuery}, \text{ResolvedTierMaxComplexity})$$
- If a query exceeds the client's tier ceiling, it is rejected with code `QUERY_COST_QUOTA_EXCEEDED` and HTTP 400.

### REQ-419-03: Differentiated Weight for Plain Scalar Columns
- Plain scalar columns selected on root list queries (direct table columns without nested selection sets or masking) receive a reduced weight (configurable via `QueryCostOptions.ScalarColumnWeight`, defaulting to `0.2` or an integer divisor where 5 scalar columns = 1 cost unit).
- Nested object selections, relation joins, and columns requiring cryptographic HMAC masking (`MaskedFields`) retain their full weight ($1.0 \times \text{effectiveRows}$ plus relation multipliers).
- Keyset connection wrapper fields (`edges`, `node`, `pageInfo`, `cursor`, `totalCount`) are structured such that the pagination envelope does not penalize Relay connections versus standard flat lists.

### REQ-419-04: Reserve-Then-Refund Integrity
- Admission reserves the estimate calculated by `QueryCostAnalyzerRule.CalculateCost`.
- Post-execution settlement in `CostAndQuotaMiddleware` settles the actual cost based on executed rows and refunds unused quota.
- The hard safety bound `MaxResponseRows` (default `1000`) remains strictly enforced regardless of tier ceiling.

### REQ-419-05: Security, Anti-Enumeration & Zero-Trust
- In Production environments, error responses for over-cost queries must not leak internal cost point counts, formula weights, or backend table statistics (preserving the `SEC-PG-12` enumeration shield).
- Only configured operators via `appsettings.json` can configure tier ceilings; callers cannot supply complexity ceilings via request headers or variables.
- One audit record is emitted per query attempt (`ConsumerOutcome.DenyCost` on rejection, `ConsumerOutcome.Success` on settlement).

---

## 4. Work Breakdown & Scope

- **WP-B1:** `QueryCostOptions` additions (`ScalarColumnWeight` in `[0.01, 1.0]`, validated startup range).
- **WP-B2:** `QueryCostAnalyzerRule` updates to apply `ScalarColumnWeight` to scalar column selections on root lists while preserving full weight for relations and masked fields.
- **WP-B3:** `GatewayStartupValidator` and `ClientTierResolver` integration for validating and resolving tier/scope complexity ceilings.
- **WP-B4:** `CostAndQuotaMiddleware` update to check caller query cost against tier-resolved complexity ceiling instead of hardcoded global fallback.
- **WP-B5:** Comprehensive unit, integration, and architecture tests validating startup validation, tier ceilings, scalar weighting, and fail-closed enumeration defense.
