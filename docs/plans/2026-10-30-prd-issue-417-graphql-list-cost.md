# PRD: GraphQL root-list admission cost and paging (#417)

Phase 1 triage. Verified against origin/main 8807e534. Related: #39, #177, #29, #178, #222, #390, #404.

## 1. Verification of the issue claims

| # | Claim | Status | Evidence (current main) |
|---|-------|--------|-------------------------|
| 1 | Admission price depends only on `first` and selected fields | CONFIRMED | `QueryCostAnalyzerRule.cs:577-595` (`multiplier * effectiveRows`, child cost times rows); no offset, where, orderBy, index or table size input |
| 2 | Cap at `MaxAllowedComplexity` 10,000 | CONFIRMED (default is 500) | `GraphQlOptions.cs:9` `[Range(100, 10000)]`, default 500 |
| 3 | `QueryCostOptions.SectionName` differs from bound section | CONFIRMED | `QueryCostOptions.cs:7` = `Gateway:Governance:QueryCost`; bound only via property `GatewayOptions.cs:71` = `Gateway:QueryCost`; `f-data-11-bulk-keyset-streaming.md:124` documents the unbound path; `configuration-guide.md` has no QueryCost section |
| 3b | ARCH-MNT-06 unknown-key guard | NOT a safety net | `GatewayConfigurationSchema.cs:45` lists `Gateway:Governance:QueryCost` as a known path, and `Gateway:QueryCost` is a `GatewayOptions` property, so the guard accepts both. Values under the documented path are silently ignored |
| 4 | Validation rule built without cost options | CONFIRMED | `GatewayServiceCollectionExtensions.GraphQL.cs:192-205` omits `costOptions`; ctor falls back to 10 (`QueryCostAnalyzerRule.cs:62`); `CostAndQuotaMiddleware.cs:71-77, 193-200` pass `gatewayOptions.QueryCost`. Checks can disagree |
| 5 | Production shows a generic validation error | CONFIRMED, intended | `GraphQlEnumerationShieldMiddleware.cs:66-84` (R-GQL-6, SEC-PG-12 #294): outside Development cost and depth errors stay inside the single shielded error so over-limit documents do not reveal field existence. Not a defect; do not change |
| 6 | Description says "offset up to 10000" regardless of config | CONFIRMED | `CatalogGraphQlTypeModule.cs:332` hardcoded text; real limit is `GraphQL.MaxAllowedOffset` (`GraphQlOptions.cs:42`, used in `GraphQlTreeBuilder.cs:53`) |
| 7a | Settlement differs from admission | CONFIRMED | `CostAndQuotaMiddleware.cs` settlement path (`BaseCost + ceil(0.5*rows) + depth`) |
| 7b | No cursor on GraphQL root lists | CONFIRMED | No `endCursor/hasNextPage`; only offset `after` in `QueryTypes.cs:36-50` (a different field). REST has the HMAC keyset cursor (`KeysetCursorEncoder`, `TableStreamEndpoints.cs`, f-data-11) |
| 7c | Per-page cost on the source (OFFSET linear, unindexed sorts) | PARTLY | Plausible and consistent with the code; the 49x sort figure is illustrative, not measured here. Index metadata exists (#390, #404) but is not used by the cost model |

## 2. Classification

- Bug: 3, 4, 6.
- Design gap: 1/2/7a (admission model and ceiling), 7c (index-aware pricing).
- Feature: 7b (signed GraphQL cursor).
- By design, no change: 5.

## 3. WP-A: quick fix (TDD, one PR)

Scope: make the configured list multiplier effective and consistent.

1. Bind from one section. Recommended default: keep `Gateway:QueryCost` (the property that binds today) as canonical; change `QueryCostOptions.SectionName`, the `GatewayConfigurationSchema` entry and `f-data-11` text to `Gateway:QueryCost`. No production exists, so removing the dead `Governance:QueryCost` path is acceptable; the unknown-key guard then rejects it (fail-closed, loud).
2. Pass `costOptions: gatewayOptions.QueryCost` to the validation rule in `GatewayServiceCollectionExtensions.GraphQL.cs:193`, so admission and middleware use the same multiplier.
3. Build the offset sentence in `CatalogGraphQlTypeModule.cs:332` from the configured `MaxAllowedOffset` (and omit it when offset paging is disabled).
4. Add a `Gateway:QueryCost` entry to `docs/configuration-guide.md` only if the owner wants docs (no docs phase by default).

Acceptance criteria:
- A test sets `Gateway:QueryCost:DefaultListMultiplier` and asserts the validation rule and the middleware price the same query identically.
- A test with the old path `Gateway:Governance:QueryCost` fails startup with the unknown-key message.
- A test asserts the field description shows the configured `MaxAllowedOffset`.
- The Production error body for over-cost queries is unchanged (shield tests stay green).

Risk: lowering or raising the multiplier changes admission; default stays 10, so no behavior change without config.

## 4. Design work (needs Phase 2 and 3)

### WP-B: admission estimate and ceiling (issue proposal 1)
Benchmarks: GitHub node limit (product of `first` values, cap 500,000 nodes, fixed 5,000 rate points per hour); Shopify calculated cost (requested cost up front, refund to actual, leaky bucket with a max single-query cost).
Findings: our settle-by-actual and refund model already matches Shopify. The ceiling is the admission reserve, not the real work.
Open questions (recommended default):
1. Raise the 10,000 `Range` cap? Default: no global raise; add a per-tier or per-scope `MaxAllowedComplexity` override (#29 scope model) with a hard cap.
2. Price by rows or by a lower "flat column" weight? Default: lower weight for plain scalar columns of root lists, keep full weight for nested lists and masked fields.
3. Admit by reserve then refund, or by estimate? Default: keep reserve and refund; adjust only the weights.
Security: a higher ceiling lets one request hold a large reserve and large result; keep `MaxResponseRows` as the independent hard bound, keep audit one record per request, require the quota bucket to be charged the settled cost. Do not expose cost numbers in Production responses (anti-enumeration).

### WP-C: signed cursor on GraphQL root lists (proposal 2)
Benchmark: Relay Cursor Connections (`first/after`, `edges.cursor`, `pageInfo.endCursor/hasNextPage`); opaque cursors.
Open questions:
1. Connection shape or additive fields on the existing list? Default: additive, opt-in field (`...Page`/connection variant) so existing lists stay stable; no production, but avoid breaking clients needlessly.
2. Reuse `KeysetCursorEncoder` (HMAC, tenant bound, direction flag)? Default: yes, same key and format family, so #390 direction handling applies.
3. Admission price for a cursor page? Default: same per-row price; cursor bounds the DB work, not the quota.
Security: cursor must be tenant and query-shape bound (table, order, filter hash) to stop replay across tables and tenants; fail closed on bad signature with the generic error; no key material in logs; each page is still audited, so audit volume is unchanged versus offset paging.
Reject: exposing raw keys as cursors; client-built `where pk > x` as the only path (already works but leaks DB-cost control).

### WP-D: index-aware pricing or rejection (proposal 3)
Uses #390/#404 index metadata. Anti-patterns to reject: rejecting on missing index silently (creates oracle for schema/index existence); static row-count pricing from stale stats.
Open questions:
1. Price or reject? Default: price only (surcharge factor for orderBy/where columns without a usable index), reject nothing at first; measure, then decide.
2. Where does index metadata come from for non-SQL Server sources? Default: apply only when metadata is known; unknown means no surcharge.
Security: rejection reasons must not differ observably in Production (same shielded error), otherwise index layout leaks.

## 5. Sequence
WP-A now (small, independent). WP-C before WP-B (a cursor removes the main pressure for large ceilings). WP-D after #390/#404 land. Each of B to D needs a Phase 2 plan and a Phase 3 review.

## 6. Out of scope
Changing the Production enumeration shield; moving the 10,000 `Range` without a per-scope design; multi-table fan-out batching (separate issue if needed).
