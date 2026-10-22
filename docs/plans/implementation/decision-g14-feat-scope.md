# Decision Record G-14: Scope of the Analytics Work Packages (#28, #37, #38)

**Decided:** 2026-10-14 by the product owner. All recommendations of the reconciliation between
the features analytics plan (removed after completion) and the bodies of GitHub issues #28, #37 and #38 are adopted.
**Effect:** G-14 is resolved; WP-FEAT-03, 04, 06a/b and the new WPs below join Wave 4 (lane L2: B-07 → FEAT-03 → FEAT-04 →
FEAT-06a → FEAT-06b → FEAT-06c → FEAT-08). Where this record and the scope plan differ, this record wins.

---

## #28 Governed GraphQL aggregation (WP-FEAT-03)

- Scope as planned: `count`, `sum`, `avg`, `min`, `max` with `groupBy`; row filters and masks apply before aggregation;
  aggregates over masked columns are rejected; aggregate fields carry their own cost weight.
- **Acceptance test:** two users **in the same tenant** with different row filters (RLS or consent) run the same grouped sum and
  each get the correct, different result. A two-tenant test alone is not sufficient.
- Release rule: #28 is not released without #37 (FEAT-03 stays behind a disabled flag until FEAT-04 is merged).

## #37 Minimum group size (WP-FEAT-04)

| # | Decision |
|---|---|
| a | No k-guard parsing of free-form SQL. Free-form SQL (WebSQL, the Trino protocol, any raw SQL path) on a table with a minimum group size is **rejected fail-closed**; aggregates over such tables are only served by the governed aggregation service. |
| b | The threshold is a **governance property per table**, set by the data owner through the admin API and persisted with a versioned governance migration. dbt `meta.minimum_group_size` may only raise it. A global default stays in `AnalyticsOptions`. |
| c | Default mode **MergeToOther with secondary suppression**: suppressed groups go into one "Other" group that must itself reach k; if it does not, the smallest visible groups are suppressed as well until it does (or all groups are dropped). Totals then equal the sum of the returned groups, so a separate total query does not reveal a suppressed group. The response marks that groups were suppressed, without their values. |
| d | `min`/`max` over a group of k rows can disclose one value: accepted and documented as a residual risk for v1. |

## #38 dbt Semantic Layer (WP-FEAT-06, WP-FEAT-08)

| # | Decision |
|---|---|
| a | Keep the gateway's own metric compiler on the governed aggregation service. The issue's spike is delivered as a short report in `docs/plans/` stating which MetricFlow query types the governed path supports and why MetricFlow-generated SQL is not passed through. |
| b | WP-FEAT-06a as planned: register `list_metrics` and `describe_metric` in the MCP server; tenant-aware semantic catalog, persisted with a versioned migration. |
| c | New **WP-FEAT-06c**: metric queries over the SQL/Trino protocol for BI tools, right after 06b, in Wave 4. |
| d | `cumulative` metrics are in v1 scope (running total through a window function on the governed path), next to `simple`, `ratio` and `derived`. |
| e | Stage 3 follows the issue as new **WP-FEAT-08 "governance per metric"**: consent per metric (a metric may be released while its base rows are not), audit entries that name the metric, and the metric definition version (dbt run) in responses. |

## Still gated

- **Former WP-FEAT-07** (headless BI connectivity, gRPC Arrow Flight SQL, Trino conformance) is renamed to a separate BI
  connectivity WP and stays gated by D-8 (target BI clients must be named).
- **WP-FEAT-05** (differential-privacy noise) stays gated by G-12 (only on compliance demand).
