# PRD: REQ-GOV-107 - Key-Set Cache Strategy for Correlated Row Filters

**Document ID:** `REQ-GOV-107`
**Date:** 2026-10-15
**Status:** PRD READY (Phase 1 complete); next: Phase 2 architecture plan (`csharp-architect`)
**Author:** Enterprise Product Manager (Phase 1)
**Source:** GitHub issue #107 ("Row filters: cache the subquery result and pass it as IN (@p0, @p1, ...), strategy per object/rule"), written in German; this PRD is the English, binding restatement.
**Branch:** `feat/issue-107-rowfilter-keyset-cache` (base `main` `21dfd399`, v1.1.14 + #94)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Related feature docs (read-only references):** [F-GOV-14 federated virtual filters](../features/f-gov-14-federated-virtual-filters.md), [F-DIALECT-02 AST SQL compiler](../features/f-dialect-02-ast-sql-compiler.md), [F-GOV-12 multi-engine governance storage](../features/f-gov-12-multi-engine-governance-storage.md#schema-shape-verification), [F-GOV-21 declared table keys](../features/f-gov-21-declared-table-keys-and-read-ordering.md), [F-SEC-06 decision cache & policy epoch](../features/f-sec-06-casbin-decision-cache-policy-epoch.md), [F-AI-13 admin MCP tools](../features/f-ai-13-admin-mcp-tools-two-phase-hitl.md), [F-GOV-15 declarative consents](../features/f-gov-15-declarative-consents-scopes.md)

**Binding project constraints:** no upgrade or repair migrations (governance schema changes go into the `0001` baseline DDL of SQLite, PostgreSQL and SQL Server in place, plus the schema-shape snapshots under `src/Autheris.Infrastructure/Persistence/Migrations/Shape/`; the caller supplies the newest schema); Autheris is not in production, breaking changes are allowed; least privilege on sources (SELECT only).

---

## 1. Problem Statement

### 1.1 Observed behaviour

A correlated consent row filter (`RowFilterType.SubqueryCorrelated`) is compiled into every governed statement as `EXISTS (SELECT 1 FROM <dependent> [JOIN <hops>] WHERE dep.pk = autheris_target.fk AND <predicate>)`. The SQL is correct, but the semi-join runs with every statement and the plan depends on how the source optimizer resolves it. Reference case (issue #107, `bug-order-by-zeilenfilter-timeout-v1-1-11.md`): `SELECT ts FROM lwetem_prod.fms.air1 ORDER BY client_id LIMIT 5` runs into the WebSQL timeout (504 after 10.5 s) for user `david` and takes 0.4 s for `admin`. `fms.air1` has 1.5 M rows and PK `(ts, client_id)`; the authorized key set (`client_id` of cranes that are not delivered) is small and changes rarely.

### 1.2 Verified against the code (`21dfd399`)

| Claim in the issue | Verified state |
|---|---|
| Correlated filters are always `EXISTS` | Confirmed. `AdvancedRlsPredicateBuilder.CorrelatedExpression` (`src/Autheris.Application/Sql/Policy/AdvancedRlsPredicateBuilder.cs`) always emits `ExistsExpression`; the doc comment states the former SQL Server IN strategy was a text-level optimisation and was dropped. Called from `TypedRowFilterBuilder.Condition` (consents) and `CasbinEnforcementService.BuildCorrelatedRowFilterPredicate` (Casbin rules with a `CorrelatedRowFilter`). |
| Dependent tables are governed | Confirmed. `GovernancePolicyFactory.CreateAsync` resolves the tables read by policy subqueries (`PolicySubqueryTables`), makes them resolvable but never "admitted", and denies the owning table when a dependent table is inactive, has no governance record, or violates `DataSources:RequireTenantColumn` (M-A1). The compiler injects the tenant predicate and the governed projection seen in the issue's SQL. |
| `CONSENT_ROW_FILTERS` holds the rule | Confirmed (`0001` baseline, all three engines). Rows are written only in `CreateConsentAsync` (immutable per consent); there is no update path. |
| Policy epochs per table | Confirmed. `POLICY_EPOCHS` per table, `IPolicyEpochRepository.IncrementTableEpochAsync`, `IEpochValidationService` (micro-cache + Redis/event-bus invalidation). Consent create/revoke bumps the target table's epoch. |
| Reusable key-set pattern | Confirmed. `VirtualFilterKeySetCache` (dedicated size-limited `MemoryCache`, single-flight per key, failures never cached, shared load not bound to the first caller's cancellation, own timeout) and `SqlVirtualFilterKeySetProvider` (governed read connection, tenant-only session context, read-only transaction on PostgreSQL, `Max+1` row read, database messages never surfaced). |
| `LogGeneratedSql` | Confirmed: `GovernedSqlRewriter` logs `compiled.Sql` at Debug when `Gateway:Logging:LogGeneratedSql` is on. |
| Existing strategy option | Found: `Gateway:RowFilters:SubqueryStrategy` (`RowFilterSubqueryStrategy`: `Exists`, `InCorrelated`, `In`) exists but applies only to the legacy string path (`RowFilterSqlBuilder`, `AdvancedRlsFilterGenerator.BuildCorrelatedSubquery`); the typed compiler path ignores it. It must not be confused with the new setting (decision D-3). |
| Maintenance via admin API, MCP admin tools, access profiles (F-GOV-11), four-eyes | Partly inaccurate. F-GOV-11 access profiles are masking exemptions and carry no row filters. The declarative consent manifest (F-GOV-15, `DeclarativeRowFilter`) cannot express correlated filters at all. Consent requests (the four-eyes approval flow, `MutationTypes`) create consents **without** row filters. Correlated filters therefore enter the store only by direct load (Talos) today; no four-eyes process covers row filters (decision D-10). |
| Parameter limits | `DialectCapabilityTable`: SQL Server `MaxBindParameters` 2,100; PostgreSQL 65,535; Oracle `MaxInListItems` 1,000 (existing `InLists` chunks with `OR`). |
| Compiled-SQL caching | The engine template cache (F-DIALECT-02, 512 entries, FIFO) is keyed by policy shape; an IN list with a different item count is a different shape (decision D-7). |
| Decision cache | Cached `TableAccessDecision`s carry the typed `RowPolicy` keyed by the Casbin epoch (`DecisionKey`). A key set baked into that predicate would live as long as the decision, not as long as the configured duration (decision D-5). |

### 1.3 Qualification

Accepted as an **opt-in performance feature with an explicit, bounded security trade-off**: within the cache duration a change in the dependent data (a crane gets delivered) is not visible, so for `ALLOW` filters a row may stay visible up to the configured duration after it should have disappeared. The default remains `Exists` (today's behaviour, exact). The trade-off is chosen per rule or per object by a governance administrator, capped globally, audited, and never applied where it would widen access in a way that cannot be bounded (deny filters, temporal filters).

---

## 2. Best-Practice Benchmark

| Topic | Practice | Consequence for Autheris |
|---|---|---|
| Semi-join materialization | Optimizers (SQL Server, PostgreSQL) may choose nested-loop semi-joins with per-row probes when cardinality estimates are off; materializing a small, stable inner set into a constant list lets the optimizer treat the filter as a sargable scan/seek predicate. Oracle/Snowflake/Databricks row-access policies document the same "mapping table lookup" cost. | Materialize the key set once and inject `fk IN (@p...)`; keep `EXISTS` for large or unstable sets. |
| Parameter typing | On SQL Server an `nvarchar` parameter against a `varchar` column causes `CONVERT_IMPLICIT` and turns a seek into a scan. | Parameters are typed from the target FK column's catalog type; a non-lossless conversion falls back to `EXISTS` (D-8). |
| Plan/template cache pollution | Variable-length IN lists create one plan per length (SQL Server plan cache, PostgreSQL prepared statements, Autheris engine template cache). | Pad the list to a bucket size by repeating the last key (D-7). |
| Bind limits | SQL Server 2,100 parameters per RPC; Oracle 1,000 items per IN list. | Global parameter budget per statement; fall back to `EXISTS` rather than fail (D-6). |
| Cache stampede | Single-flight (request coalescing) per key; do not cache failures; bound the shared load by its own timeout. | Reuse the `VirtualFilterKeySetCache` mechanism (D-4). |
| Staleness of cached authorization | Absolute expiry only; invalidate on policy version change; never serve stale data after expiry ("stale-while-revalidate" extends the authorization window). | Absolute TTL, epochs of all involved tables in the key, age re-checked on hit against the current effective duration (D-5). |
| Caching authorization data securely | Key must include every input that changes the result (tenant, data source, full filter definition incl. parameter values); keep it in process memory, size-bounded, never in a shared/distributed cache; never log the cached values. | D-4, SR-1..SR-8. |
| Negative-result caching | Cache "too many keys" as a valid outcome so that oversized sets do not run the key query on every request; never cache errors. | Overflow sentinel (D-6). |

### 2.1 Rejected anti-patterns

1. **Sliding expiration** - staleness becomes unbounded under steady traffic. Rejected.
2. **Stale-while-revalidate / serve-stale-on-error** - serves authorization data beyond the declared bound. Rejected.
3. **Distributed (Redis) key-set cache** - puts authorization material into a shared store, adds a trust and serialization surface; per-node memory plus cluster-wide epochs is sufficient. Rejected.
4. **Baking the key set into the cached access decision** - staleness would follow the decision cache, not the configured duration. Rejected (D-5).
5. **Truncating an oversized key set** - would silently deny legitimate rows and make results nondeterministic. Rejected; fall back to `EXISTS`.
6. **Key set for DENY filters** - a stale deny list leaks newly denied rows, and `NOT (fk IN (...))` differs from `NOT EXISTS` for `NULL` foreign keys. Rejected (kept `EXISTS`).
7. **Table-valued parameters / temporary tables on the source** - need `CREATE TYPE` or write rights on the source; violates least privilege. Rejected.
8. **Query hints (`OPTION (HASH JOIN)`, `pg_hint_plan`)** - dialect-specific, brittle, and not governed by the compiler. Rejected for this feature (an index on the FK column remains the recommended fix where possible).
9. **Global-only switch** - one staleness bound for all filters; the issue explicitly asks for rule and object level. Rejected as the sole mechanism (global stays default and upper bound).
10. **Logging key values** - keys can be business identifiers; only counts and hashes are logged. Rejected.

---

## 3. Decisions (open questions resolved by best practice)

| ID | Decision |
|---|---|
| **D-1 Settings model** | Three settings: `strategy` (`Exists` \| `KeySetCache`, `null` = inherit), `durationSeconds` (`null` = inherit), `maxKeys` (`null` = inherit). Each setting is resolved independently with precedence **rule > object > global default**, then clamped to the global caps (`MaxDuration`, `MaxKeys`). The diagnostic reports the source of the strategy (`Rule`, `Object`, `Global`). |
| **D-2 Storage** | Rule level: three nullable columns on `CONSENT_ROW_FILTERS` (`key_set_strategy`, `key_set_duration_seconds`, `key_set_max_keys`). Object level: a new table `TABLE_ROW_FILTER_STRATEGIES` (`table_id` PK = `TABLES.id`, the same three columns, `updated_by`, `updated_at`), separate from `TABLES` so that catalog ingestion upserts can never overwrite it (same reasoning as F-GOV-21). Both go into the `0001` baseline of all three engines in place; shape snapshots updated; checksum constants unchanged. Exact names/types fixed in Phase 2. |
| **D-3 Naming** | The new setting is called the **evaluation strategy** (`RowFilterEvaluationStrategy`), not `SubqueryStrategy`, to avoid confusion with the legacy `Gateway:RowFilters:SubqueryStrategy` (string path only), which stays unchanged. API JSON names follow the issue: `subqueryStrategy`, `keySetCacheDurationSeconds`, `keySetMaxKeys` (rule) and `rowFilterStrategy { strategy, durationSeconds, maxKeys }` (object). |
| **D-4 Cache** | A dedicated instance of the generalized key-set cache (shared mechanism with `VirtualFilterKeySetCache`, separate `MemoryCache` and size limit `Gateway:RowFilters:KeySetCache:SizeLimitBytes`) so row-filter key sets cannot evict virtual-filter key sets or access profiles. Single-flight per cache key; failures never cached; absolute expiration; per node. **Cache key:** data source, tenant, SHA-256 of the canonical filter definition (dependent table, alias, hops, FK/PK columns, predicate JSON canonicalized, all bound parameter values with types), target FK column type, compiler/catalog fingerprint of the dependent tables, and the current policy epochs of the target table and every dependent table. |
| **D-5 Where substitution happens** | Never in the cached access decision. The decision keeps the exact `EXISTS` predicate plus a marker of the eligible filter; the key set is resolved and substituted per statement in the asynchronous policy assembly (`GovernancePolicyFactory` stage or a dedicated stage directly after it; Phase 2 decides). On a cache hit the entry's age is checked against the **current** effective duration, so lowering a duration takes effect immediately. |
| **D-6 Fallback to `EXISTS` (never deny)** for: strategy `Exists`; DENY consents; filters with `TargetTemporalColumn`; key count > effective `MaxKeys` (an overflow sentinel is cached for the duration so the key query is not repeated); parameter budget exceeded; epochs unavailable (cache bypassed); dialect or data source not supported by the key loader; FK/PK type pair not losslessly bindable; filter inputs not covered by the cache key (e.g. a future user-attribute value). Each fallback has a reason code in the diagnostics. |
| **D-7 IN-list shape** | `autheris_target.fk IN (@p0, ...)`; padded to the next bucket (1, 2, 4, 8, ... up to `MaxKeys`) by repeating the last key (semantically neutral), to bound plan- and template-cache variants. Oracle lists chunked at 1,000 via the existing `InLists`. Empty key set: the condition becomes `1 = 0` (only that leaf; the existing AND/OR combination of groups and consents is preserved). |
| **D-8 Typing** | Parameters are bound with the target FK column's catalog type (length/Unicode included). If the dependent key type cannot be converted to it without loss, fall back to `EXISTS`. |
| **D-9 Key query** | Built as an AST `SELECT DISTINCT dep.pk FROM <dependent> [JOIN hops] WHERE dep.pk IS NOT NULL AND <predicate>` and compiled by the **same compiler with the same governance policy** as the statement (tenant predicate, governed projection, active/`RequireTenantColumn` checks of M-A1), so governance parity with the `EXISTS` form holds by construction. Executed on the governed read connection with `TOP/LIMIT MaxKeys+1`, key-query timeout, read-only transaction on PostgreSQL, tenant-only session context (principal-independent, as F-GOV-14). Failure (error or timeout) of an eligible key query: nothing cached, statement **denied** (fail closed, stable error code, no database message). |
| **D-10 Four-eyes** | No row-filter four-eyes process exists today, so none is added (as the issue requires "where it applies today"). Changes require the `GovernanceAdmin` policy (REST) or the two-phase HitL plan/apply with TOTP (MCP, F-AI-13) and are written to the HMAC-chained audit with before/after values. |
| **D-11 Maintenance paths** | (a) REST: `PUT /api/v1/governance/row-filters/{id}/evaluation` (rule) and `PUT /api/v1/catalog/datasets/{id}/row-filter-strategy` (object), plus `GET` counterparts showing stored and effective values with source. (b) MCP admin tools through the existing plan/apply flow. (c) Direct load (Talos) into the documented columns/table. The F-GOV-15 manifest is **not** extended (it cannot express correlated filters; separate follow-up). Values above the global caps, unknown strategies, non-positive values, `KeySetCache` on a non-correlated, DENY or temporal rule are rejected with `400`. A stored value that is invalid on read (direct load) is treated as `Exists` and logged (safe and exact). |
| **D-12 Epoch bump** | Every change of a rule setting bumps the epoch of the consent's target table; every change of an object setting bumps that table's epoch; both invalidate cached decisions and key sets (the target epoch is part of the key). |
| **D-13 Channels** | Applies to every channel compiled through the typed governed pipeline (`GovernedSqlRewriter` → `GovernancePolicyFactory`: WebSQL and the channels that delegate to `GovernedSqlExecutionService`). A channel that builds correlated predicates outside this pipeline keeps `EXISTS` until migrated; Phase 2 lists the channels. Row parity across all channels is mandatory either way. |
| **D-14 Casbin-sourced correlated filters** | Have no `CONSENT_ROW_FILTERS` row: they inherit object and global settings only. |
| **D-15 Global config** | `Gateway:RowFilters:KeySetCache { DefaultStrategy = Exists, DefaultDuration = 00:05:00, MaxDuration = 01:00:00, MaxKeys = 2000, SizeLimitBytes = 64 MiB, KeyQueryTimeout = 00:00:05 }`, validated at startup (`IValidateOptions`): `1 s <= DefaultDuration <= MaxDuration <= 24 h`, `1 <= MaxKeys <= 10,000`, `KeyQueryTimeout` <= the WebSQL statement timeout. `DefaultStrategy = KeySetCache` is allowed but logs a startup warning. A rule/object value above a later-lowered cap is clamped at runtime (more conservative). |
| **D-16 Diagnostics** | Per eligible filter: strategy, source, outcome (`Hit`, `Miss`, `Fallback:<reason>`), key count, entry age; in the statement audit details and in the `LogGeneratedSql` debug line; never key values. Metrics on `GatewayDiagnostics`: hits, misses, fallbacks by reason, key-query duration, cache size. |

---

## 4. Scope

### 4.1 In scope

- Rule- and object-level settings, global defaults/caps, resolution and clamping (D-1, D-15).
- Persistence in the `0001` baseline for SQLite, PostgreSQL, SQL Server, shape snapshots, repository round-trip (D-2).
- Key query, key-set cache, substitution, fallback, typing, padding (D-4..D-9).
- REST endpoints, MCP admin tools, audit, epoch bump (D-10..D-12).
- Diagnostics and metrics (D-16); feature documentation in `docs/features/` (Phase 6).

### 4.2 Non-goals

- Changing the default (`Exists`) or removing the legacy `Gateway:RowFilters:SubqueryStrategy`.
- Key-set caching for `CrossSourceSetFilter`, simple predicates, virtual filters (already covered by F-GOV-14).
- Data-change-driven invalidation (CDC on dependent tables), refresh-ahead, distributed cache - possible follow-ups.
- Extending the F-GOV-15 manifest or consent requests to author correlated filters.
- Array parameters (`= ANY(@arr)`, `OPENJSON`) to exceed bind limits.
- Index advice or automatic index creation on sources.

---

## 5. Functional Requirements

- **FR-1** Strategy, duration and max keys are settable per rule and per object; resolution rule > object > global; each value clamped to the global caps; the effective values and the strategy source are readable via `GET`.
- **FR-2** With effective `KeySetCache`, an eligible ALLOW correlated filter is compiled as `autheris_target.<fk> IN (@p...)` with the cached keys; otherwise the SQL is byte-identical to today.
- **FR-3** The key query is executed at most once per cache key and duration per node, including under concurrent requests (single flight).
- **FR-4** A new policy epoch of the target or any dependent table forces a new key query.
- **FR-5** Fallback to `EXISTS` for every case of D-6, with a reason code; DENY and temporal filters always `EXISTS`.
- **FR-6** Empty key set → `1 = 0` for that condition.
- **FR-7** Several filters in one statement are resolved independently (e.g. `fms.air1` via cache, `md.engine` via `EXISTS`); the parameter budget is enforced over the whole statement, falling back the largest lists first.
- **FR-8** Admin changes: validated, audited (before/after, actor, tenant), epoch bumped, cached keys invalidated.
- **FR-9** Diagnostics and metrics per D-16.

## 6. Security Requirements

- **SR-1 Fail closed on key-query failure** (error, timeout, governance violation of a dependent table): no caching, statement denied with a stable code; database messages never surfaced or logged (type only).
- **SR-2 Governance parity:** the key query applies exactly the governance of the `EXISTS` form (tenant predicate, active table, `RequireTenantColumn`, governed projection); proven by tests that toggle each condition.
- **SR-3 Cache-key completeness:** every input that changes the key set is in the key (D-4); a filter with an input that cannot be keyed is not eligible. Tenants never share entries.
- **SR-4 Bounded staleness:** absolute expiry ≤ effective duration ≤ `MaxDuration`; age re-checked on hit; no stale serving; epoch unavailable → cache bypassed.
- **SR-5 No widening beyond the accepted trade-off:** never for DENY or temporal filters; `NULL` semantics identical to `EXISTS` (keys `IS NOT NULL`; `NULL` FK never matches).
- **SR-6 Least privilege:** key query is a plain `SELECT` on the governed read connection; no DDL, temp tables, TVPs or catalog queries on sources.
- **SR-7 Resource bounds:** at most `MaxKeys+1` rows read; size-limited dedicated cache; key-query timeout; shared load not cancellable by one caller.
- **SR-8 Confidentiality of keys:** key values never in logs, audit, metrics or error messages; only counts and hashes.
- **SR-9 Admin surface:** `GovernanceAdmin` (REST, 401/403 as F-GOV-21), MCP plan/apply with TOTP, tenant match for rule-level changes (a consent's tenant), fixed `404` body without echoing ids.
- **SR-10 Session context:** the key query runs with tenant-only session context. Documented restriction: `KeySetCache` must not be enabled on filters whose dependent tables rely on source-native per-user row-level security (risk R-3).

---

## 7. Acceptance Criteria

- **AC-1** Strategy and duration (and max keys) are settable per rule and per object; precedence rule > object > global; values above the global caps are rejected on save (`400`).
- **AC-2** With `KeySetCache` on rule or object, WebSQL produces for issue examples 1 (`fms.air1` via `conf.client` + `md.crane`) and 2 (`md.engine` via `md.crane`) the key query of step 1 and the statement of step 2 (generated-SQL unit tests for SQL Server, PostgreSQL and Oracle; padding per D-7 reflected in the expected parameters).
- **AC-3** A statement over two tables with different strategies produces the matching form per table.
- **AC-4** Changing strategy or duration at rule or object level bumps the policy epoch and discards cached keys (next statement runs the key query).
- **AC-5** Within the duration the key query runs once per cache key, also for N parallel cold requests (single-flight test).
- **AC-6** A new policy epoch of any involved table (target or dependent) forces a new key query.
- **AC-7** Above `MaxKeys`, above the parameter budget, for DENY filters and for temporal filters the SQL stays `EXISTS`; the overflow is cached (no repeated key query within the duration).
- **AC-8** Without any setting at rule, object or global level the generated SQL is unchanged (`EXISTS`, golden tests unchanged).
- **AC-9** Same rows with and without cache in every channel (extend `RowFilterChannelParityTests` with a `KeySetCache` variant), including empty key set, `NULL` FKs and multi-hop filters.
- **AC-10** `SELECT ts FROM lwetem_prod.fms.air1 ORDER BY client_id LIMIT 5` as `david` runs under 1 s with the cache warm (verified on the reference environment; see test strategy).
- **AC-11** A failing or timing-out key query denies the statement with a stable error code, caches nothing, and the next request retries.
- **AC-12** Lowering the duration takes effect on the next request even for an already cached entry.
- **AC-13** Audit details and the `LogGeneratedSql` line state per filter strategy, source, hit/miss/fallback reason and key count, and contain no key values.
- **AC-14** Baseline DDL of all three engines contains the new columns/table; shape snapshots updated; `0001` checksum constants unchanged; repository round-trip per engine; invalid stored values read as `Exists` with a warning.
- **AC-15** Tenant isolation: identical filters for two tenants never share a cache entry and return their own keys.

---

## 8. Test Strategy

- **Unit (TDD, Phase 4):** settings resolution and clamping matrix; validator; canonical filter hash (stable, sensitive to every input); substitution on the AST (ALLOW only, inside AND/OR groups, empty set, padding, Oracle chunking, parameter budget); fallback reason per D-6; generated SQL golden tests per dialect (AC-2, AC-3, AC-8); cache single-flight, failure not cached, overflow sentinel, age re-check, epoch in key (using `TimeProvider`).
- **Integration (Testcontainers SQL Server and PostgreSQL, SQLite in-process):** key-query governance parity (tenant column, inactive dependent, `RequireTenantColumn`); channel parity (AC-9); epoch bump through REST and MCP; fail-closed on source failure (AC-11); repository round-trip and shape verification per engine (AC-14); least-privilege grant test (SELECT-only login runs the key query).
- **Security regression:** register the new security ids in the regression traceability catalog; tests for tenant separation (AC-15), no key values in logs/audit (log capture), DENY/temporal never cached.
- **Performance:** a non-gating benchmark (category `Performance`) with a scaled `air1`-like table on SQL Server comparing `EXISTS` and `KeySetCache`; AC-10 verified manually on the reference environment and recorded in the Phase-4 hand-off.
- **Mutation testing:** substitution and fallback logic included in the existing Stryker scope.

---

## 9. Risks

| ID | Risk | Mitigation |
|---|---|---|
| R-1 | Accepted staleness widens ALLOW access for up to the duration (e.g. delivered crane still visible). | Opt-in per rule/object, default `Exists`, global `MaxDuration`, audit of every change, documented in the feature doc. |
| R-2 | Cache key misses an input → cross-filter or cross-tenant key reuse. | Canonical hash over the full definition, tenant and data source in the key, eligibility rule for un-keyable inputs, dedicated tests (SR-3, AC-15). |
| R-3 | Key query with tenant-only session context differs from the per-user `EXISTS` evaluation where the source enforces native per-user RLS. | Documented restriction (SR-10); Phase 3 to evaluate a per-data-source eligibility flag. |
| R-4 | Substitution after admission conflicts with the "string and typed forms agree" check in `GovernancePolicyFactory` or with template-cache validation. | Phase 2 places substitution after the agreement check and validates the result through the compiler's coverage proof. |
| R-5 | Per-node caches: N nodes run N key queries per duration; a node that missed an epoch hint relies on the periodic epoch check. | Acceptable cost; epoch unavailable → bypass (SR-4). |
| R-6 | Plan regressions with IN lists on some sources (e.g. very selective EXISTS with good indexes was faster). | Opt-in per object/rule, metrics and benchmark; `Exists` stays default. |
| R-7 | No authoring path for correlated filters except direct load; rule-level fields are only useful once filters exist. | Admin endpoint edits existing rules; manifest support is a separate follow-up. |
| R-8 | The performance acceptance (AC-10) depends on the reference environment, not reproducible in CI. | Benchmark + manual verification with recorded plan and timing. |

---

## 10. Lifecycle Hand-off

- **Phase 2 (`csharp-architect`):** decide the exact substitution point (D-5), the generalized cache type, DDL names/types per engine, the channel list (D-13), the error code, and the parameter-budget algorithm; write `2026-10-15-plan-issue-107-row-filter-key-set-cache.md`.
- **Phase 3 (`csharp-security-expert`):** audit SR-1..SR-10, especially R-2 and R-3.
- **Phase 4-6:** TDD implementation, review, feature doc `docs/features/f-gov-22-row-filter-key-set-cache.md` (number confirmed in Phase 6), master plan update.

## 11. Changelog

- 2026-10-15: Initial PRD (Phase 1) from issue #107, verified against `21dfd399`.
