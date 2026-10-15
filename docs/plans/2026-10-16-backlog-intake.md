# Backlog Intake 2026-10-16: Owner Requests

**ID:** `BACKLOG-2026-10-16`
**Source:** owner requests of 2026-10-15/16 (originally in German; summarized in English here).
**Process:** every item goes through the 6-phase lifecycle (`README.md`). Items BL-04 to BL-06 are handled **sequentially** by the product manager; each gets its own PRD (`*-req-*.md`) and plan (`*-plan-*.md`) in `docs/plans/` and its own feature branch.

## 1. Items

| ID | Topic | Owner request (summary) | Branch | Status |
|---|---|---|---|---|
| BL-01 | Governed CDC fan-out and change-tracking hardening (GitHub #99) | Webhooks and C# DLL plugins as receivers, one producer for many consumers, hardening. | `feat/issue-99-cdc-fanout` | Phase 4 (I1 contracts) |
| BL-02 | Row filter key-set cache (GitHub #107) | Cache the result of correlated row-filter subqueries and bind it as `IN (@p0, …)`; strategy per rule and per table. | `feat/issue-107-rowfilter-keyset-cache` | Phase 4 (contracts) |
| BL-03 | Scale to 5,000 users with many tables and access rules | Architect and performance expert check whether access can be governed efficiently and performantly at this size, which technical changes are needed, and how system performance and scaling behave. Findings are then implemented. | `feat/scale-5000-users` | Analysis |
| BL-04 | Table cache: cache tables or parts of tables inside Autheris | See section 2.1. | to be created | Intake |
| BL-05 | Granular configuration per data source | See section 2.2. | to be created | Intake |
| BL-06 | Tracing and logging via open standards | See section 2.3. | `feat/bl-06-open-telemetry` | Phase 2/3 |
| BL-07 | PostgreSQL wire protocol endpoint | PG-compatible endpoint for BI tools; PRD `2026-10-16-req-postgres-wire-protocol-endpoint.md` on `feat/scale-5000-users`: conditional go, first verify BI tools via the existing Trino protocol. | `feat/scale-5000-users` (PRD) | PRD ready, decision pending |

## 2. Item Details (owner requirements, not yet refined)

### 2.1 BL-04 Table cache

- Whole tables or parts of tables (column and row subsets) can be cached inside Autheris.
- Refresh modes per cached object:
  - change tracking / CDC driven (builds on BL-01),
  - periodic refresh queries,
  - classic expiry after a configurable time (seconds, minutes, hours), then reload.
- Joins between cached and non-cached data are transparent to the caller.
- Optional persistence on disk, so that the data stays available with high availability even when the source system is unavailable.
- Governance (consents, row filters, masking, tenant isolation, audit) applies to cached data exactly as to source data.

### 2.2 BL-05 Granular configuration per data source

- Settings that are global today become configurable per data source (with inheritance: data source overrides global), for example:
  - cost settings per data source (query cost, budgets, limits),
  - logging settings per data source (level, generated SQL logging, sampling),
  - further candidates to be identified by the product manager (timeouts, row and byte limits, caching, rate limits, isolation level).

### 2.3 BL-06 Tracing and logging via open standards

- Traces, metrics and logs use open standards (for example OpenTelemetry with OTLP export and W3C Trace Context propagation), so that any compatible backend can be used.
- Correlation across gateway, governance store and source queries; no sensitive values in telemetry.

### 2.4 Cross-cutting: open-source assessment

Every new topic (BL-01 to BL-07) gets an open-source assessment in its analysis or PRD: candidate libraries or services, licence
compatibility with the Autheris licence (BSL 1.1; GPL, AGPL and third-party BSL code must not be copied), maturity, fit, and a
build-versus-adopt recommendation.

## 3. Sequence

BL-01 and BL-02 are already in implementation; BL-03 is in analysis. The remaining items are refined one after another:

1. **BL-06 Tracing and logging** first: it is the smallest item and gives the measurements that BL-03 and BL-04 need.
2. **BL-05 Per-data-source configuration** second: BL-04 configures caches per data source and table and builds on it.
3. **BL-04 Table cache** last: it depends on BL-01 (CDC stream), BL-05 (configuration) and the BL-03 findings.
