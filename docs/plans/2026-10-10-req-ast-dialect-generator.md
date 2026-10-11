# PRD: AST Target Dialect Generator - Governed Cutover from the Legacy Token Rewriter

**Document ID:** `REQ-SQL-AST-DIALECT-16` (master plan track `PLAN-AST-DIALECT-GEN-16`)  
**Date:** 2026-10-10  
**Status:** PROPOSED  
**Author:** Enterprise Product Manager (`product-manager`, Phase 1 of the 6-phase lifecycle)  
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)  
**Refines:** [ADR-017 §2 "AST Target Dialect Generator"](../adr/ADR-017-distributed-state-ast-generator-and-rbac.md)  
**Supersedes (as requirement baseline):** [2026-10-06-implementation-plan-ast-dialect-generator.md](2026-10-06-implementation-plan-ast-dialect-generator.md) (German draft, see §11)  
**Related feature reference:** [F-DIALECT-01](../features/f-dialect-01-ast-target-dialect-pushdown.md), [F-DATA-02 Governed WebSQL](../features/f-data-02-governed-websql.md), [F-GOV-09 Virtual Filters](../features/f-gov-09-virtual-filters.md), [F-GOV-14 Federated Virtual Filters](../features/f-gov-14-federated-virtual-filters.md), [F-PERF-11 Query Plan Cache](../features/f-perf-11-query-plan-cache.md)

---

## 0. Gatekeeper Verdict (TL;DR)

**GO WITH CHANGES.**

The problem is real and the direction (typed AST + per-dialect emitter instead of token-stream text surgery) matches industry practice. However, **most of the 2026-10-06 plan is already built** and the remaining risk is not "write a compiler" but **"switch production traffic from a hardened rewriter to a younger one without a security regression"**. The plan as written treats that cutover as a two-day "Phase 6" and justifies it with claims ("mathematical proof", "constructively impossible", "syntactic immunity") that the current code does not support. This PRD re-scopes the track into measurable, reversible, per-dialect increments with a working shadow mode and an explicit kill switch.

---

## 1. Problem Statement

### 1.1 Customer problem

Autheris tenants query heterogeneous backends (SQL Server, PostgreSQL, SQLite, Oracle, DuckDB, Snowflake) through one Trino-flavoured SQL surface (WebSQL, declarative SQL endpoints, virtual filters, MCP dataset tools). Security officers need a guarantee that tenant RLS, consent filters and column masks are applied **in every branch of every query** and that the SQL sent to the backend **means the same thing in the backend dialect** as it meant when the gateway checked it.

### 1.2 Architectural problem

The legacy path ([`RlsListener`](../../src/TrinoSqlEngine/RlsListener.cs), ~980 lines, `TokenStreamRewriter`) edits the *source* token stream and forwards the result. Any difference between how the Trino lexer and the target engine tokenize the same bytes (brackets, backslash escapes, `E'...'`, `$$`, nested comments, identifier folding) is a potential security differential (findings `SQ-01`, `SQ-02`, `SQ-05`, `SQL-1` in ADR-017). The legacy path closes these with deny-lists and lexer guards (`SqlTokenSecurityOptions`), i.e. by enumerating known-bad inputs. An emitter that serializes only typed nodes in the target dialect removes this class structurally rather than by enumeration.

### 1.3 Is the problem still valid given the hardened legacy pipeline?

**Yes, but the urgency is about maintainability and new dialects, not an open exploit.** The legacy pipeline has passed security rounds 4, 5 and 15 and is the production default today. Its remaining weaknesses are:

- Every new dialect or grammar feature requires new deny-list entries (open-ended, enumerate-badness approach that OWASP explicitly ranks below structural defenses).
- It cannot translate semantics (pagination, booleans, date functions, casts) per dialect; the AST path already does, and virtual filters already depend on it.
- Two rewriters with diverging behavior are themselves a risk (policy drift between WebSQL and virtual filters).

Therefore the value of this track is: **one governed rewriter, chosen per dialect on evidence, with the legacy path retired only when the evidence says so.**

---

## 2. Current State (Deduplication against `docs/features/` and code)

Verified on branch `feat/ast-target-dialect-generator` at commit `e7ee288`.

### 2.1 Already documented

- [F-DIALECT-01](../features/f-dialect-01-ast-target-dialect-pushdown.md) is registered in [`docs/features/README.md`](../features/README.md) and describes the AST compiler as if it were the general pipeline. It does **not** state that the AST compiler is opt-in for WebSQL and that `LegacyTokenStream` is the default. **This is a documentation accuracy gap for the Phase 6 documentation expert, not a new feature.** No new `docs/features/` entry is required for this track; F-DIALECT-01 must be corrected when the increments ship.

### 2.2 Already built and tested (do NOT re-plan as new work)

| Plan milestone (2026-10-06) | Code reality | Evidence |
|---|---|---|
| M1 AST IR | **Built.** `src/TrinoSqlEngine/Ast/Nodes/*` (records for statements, query bodies, expressions, table sources, clauses, CTEs, DML) plus a `TrustedSqlExpression` node not in the plan. | ~500 lines of node records |
| M2 Builder & guards | **Built.** `SqlAstBuilder` (1,364 lines), `AstBuilderOptions`, depth guard, fail-loud on unsupported constructs. | `SqlAstBuilderTests`, `AstDepthGuardTests`, `AstBuilderFailLoudTests` |
| M3 Security visitor | **Built.** `AstSecurityVisitor` (1,315 lines): subquery-encapsulated RLS with `autheris_target`, CTE scoping, masking with wildcard expansion, DML guards, tautology detection (SR15-09/10). | `AstSecurityVisitor{Rls,Cte,Masking,Dml}Tests`, `AstSecurityDmlPrecedenceTests` |
| M4 Dialect generators | **Built, beyond plan scope.** ANSI, SQL Server, PostgreSQL, SQLite, DuckDB, Snowflake **and Oracle**; per-dialect EXTRACT, CAST/TRY_CAST, date functions, window frames, grouping sets, `WITH TIES`, `IS DISTINCT FROM`. | `DialectGenerators/*Tests` (5 files) + 10 emission test files |
| (unplanned) Simplifier | **Built.** `AstSimplificationVisitor` runs **after** the security visitor (constant folding, contradiction detection, De Morgan). | `AstSimplificationVisitorTests` |
| M5 Allocation budget | **Not verified.** No BenchmarkDotNet benchmark for the AST path exists in `benchmarks/`. The "< 2.2 KB/query" target is unmeasured. | none |
| M6 Differential suite | **Minimal.** `LegacyVsAstDifferentialTests` has 4 tests that only assert both outputs contain the tenant predicate; no semantic comparison. | 4 tests |
| M7 Container E2E | **Smoke only.** One Testcontainers test each for SQL Server, PostgreSQL and Oracle executes one AST-compiled query; SQLite executes in-process (`AstSqliteExecutionTests`). No DuckDB or Snowflake execution test. | `tests/Autheris.Tests.Integration/{MsSql,PostgreSql,Oracle}IntegrationTests.cs` |
| M8 Feature flag | **Partially built.** `GatewayOptions.WebSql.SqlRewriterEngine` (string) selects `LegacyTokenStream` (default) / `AstCompiler` / `ShadowDualRun`; `RlsOptions.RewriterEngine` allows per-call override; the plan cache key includes the engine. | `FastSqlEngine.RewriteRls`, `GovernedSqlRewriter` |

Local verification: `dotnet test tests/TrinoSqlEngine.Tests --filter "FullyQualifiedName~Ast|FullyQualifiedName~Differential"` → **433 passed, 0 failed** (2026-10-10).

### 2.3 Production exposure today

- **WebSQL / SQL endpoints** (`GovernedSqlRewriter`): legacy by default; AST only if an operator sets `WebSql:SqlRewriterEngine=AstCompiler`. No `appsettings*.json` sets it.
- **Virtual filters** (`SqlFilterCompiler.Compile`): **always** `AstCompiler`. The AST path is therefore already a production security dependency, regardless of this track.
- `RlsListener` is already marked `[Obsolete("Superseded by AstSecurityVisitor ...")]` while still being the default production path, with the warning suppressed (`#pragma warning disable CS0618`). The signal is premature.
- ADR-017 §2 states "Default in WebSQL set to `AstCompiler`". **The code contradicts the ADR.** One of them must be corrected in Phase 2.

### 2.4 Gaps found during qualification (input for Phase 2 and Phase 3)

| ID | Gap | Severity (PM estimate) |
|---|---|---|
| G-1 | **Shadow mode is not a shadow mode.** `ShadowDualRun` computes the AST result, discards it, swallows every exception (`catch { }`) and emits no log, metric or diff. It doubles CPU cost on the request path and produces zero evidence. | High (blocks any evidence-based cutover) |
| G-2 | **Engine selector is an untyped string.** Unknown values (e.g. `"Legacy"`, typos) silently fall back to the legacy engine; there is no startup validation. | Medium |
| G-3 | **Parameter budget invariant (plan I11) is not enforced for the dominant path.** Only generator-allocated positional markers call `CheckParameterBudget`; named client parameters and synthetic `__param_*` placeholders bypass the counter. | Medium |
| G-4 | **Wrong/obsolete limits.** SQLite budget hard-coded to 999 (pre-3.32.0 default; 32,766 since 3.32.0, and the bundled `Microsoft.Data.Sqlite` 10.x ships a newer SQLite). Oracle budget hard-coded to 1,000, which is the `IN`-list expression limit (ORA-01795), not a bind-variable limit. | Medium (functional false rejects; missing IN-list guard on Oracle) |
| G-5 | **Raw SQL channels inside the "typed" pipeline.** Gateway-rendered row filters and mask expressions are spliced verbatim as `TrustedSqlExpression` (`PolicyFiltersAreTargetDialectSql`, SQL-4). The claim "no raw fragments reach the target database" is false; the trust boundary is the policy renderer. | High (must be an explicit, tested trust boundary) |
| G-6 | **Post-security optimization.** `AstSimplificationVisitor` rewrites `WHERE` trees after RLS injection. A folding bug could weaken or drop an injected predicate. There is no test oracle that proves injected predicates survive simplification. | High |
| G-7 | **No performance evidence** for the AST path (allocation, P95/P99) against the legacy baseline. | Medium |
| G-8 | **Engine flag is global for WebSQL**; no per-data-source or per-dialect rollout granularity, so a cutover is all-dialects-at-once. | Medium |

---

## 3. Market & Best-Practice Benchmark

| Reference | What it does | Lesson for Autheris |
|---|---|---|
| **sqlglot** (Python) | Parses ~30 dialects into one expression tree and regenerates per-dialect SQL via a generator class per dialect. Its README states that it only "aims to" produce semantically correct SQL, that it "is a transpiler, not a validator", and that untranslatable constructs get a warning and a "best-effort translation" by default unless `unsupported_level=ErrorLevel.RAISE`/`IMMEDIATE` is set. Each dialect is tested with `validate_identity` (round-trip) and `validate_all` (cross-dialect expected-output matrices). | (a) Never claim universal equivalence; claim it per tested construct. (b) Unsupported constructs must **raise** in a security product (fail loud), which Autheris already does. (c) Adopt the per-dialect "expected output matrix" fixture style as the conformance suite. |
| **Apache Calcite** | Parses to `SqlNode`, validates/plans to `RelNode`, and unparses via `RelToSqlConverter` + `SqlDialect` subclasses for JDBC pushdown. Dialect capabilities are declared (`supportsCharSet`, `supportsAggregateFunction`, ...), and anything not supported is not pushed down. | Dialect **capability declarations** decide what may be pushed down; unknown capability = no pushdown (fail closed), not "emit and hope". |
| **jOOQ** | Typed DSL rendered per `SQLDialect`; bind values by default; automatically inlines bind values once a dialect's bind limit is reached (documented: SQL Server 2,100, PostgreSQL 65,535, SQLite 999, Oracle 32,767), overridable with `inlineThreshold`. Its translator only promises "what jOOQ would generate". Offers a parser/translator for dialect migration. | Parameter budgets are a **rendering policy**, not just an exception: decide per dialect between reject, inline-safe-literals and table-valued/array parameters. |
| **Trino** (our source dialect) | Row filters and column masks are returned by the `SystemAccessControl` SPI (`getRowFilters`, `getColumnMasks`) and applied by the **analyzer/planner on the query plan**, not by rewriting SQL text. JDBC connectors receive typed `ConnectorExpression`s and return `Optional.empty()` when they cannot translate, in which case the work stays in Trino. | Confirms the target architecture: security is attached to the **plan/tree**, and pushdown is capability-driven. It also shows that even Trino does not emit arbitrary SQL for arbitrary engines; it pushes down only what each connector declares it can handle. |
| **OWASP SQL Injection Prevention Cheat Sheet** | Primary defenses: parameterized queries; for identifiers (table/column names) that cannot be bound, use allow-list validation; escaping all user input is "STRONGLY DISCOURAGED" because it is database-specific and cannot guarantee prevention. | Identifiers come from a typed AST but originate from user SQL; they must be quoted **and** resolved against the catalog allow-list. Values must be bound, not escaped. `TrustedSqlExpression` must never carry user input (G-5). |
| **Differential testing (SQLancer, GitHub Scientist)** | SQLancer finds logic bugs by comparing results of semantically equivalent queries (TLP/NoREC oracles). Scientist runs a candidate path next to the control, compares results, publishes mismatches, and always returns the control result. | Shadow mode must **compare, record and sample**, never just execute. Differential oracles must account for *intentional* differences (AST stricter than legacy). |

Bind-parameter and identifier limits (for NFR-4):

| Engine | Bind parameters per statement | Other relevant limits |
|---|---|---|
| SQL Server | 2,100 parameters (documented per stored procedure/UDF; applies to `sp_executesql` RPC) | identifiers 128 chars |
| PostgreSQL | 65,535 (Int16 count in the Bind message) | identifiers 63 bytes (`NAMEDATALEN-1`), silently truncated |
| SQLite | 999 before 3.32.0, 32,766 since 3.32.0 (`SQLITE_MAX_VARIABLE_NUMBER`; caps the highest parameter number and can be lowered at runtime via `sqlite3_limit`) | |
| Oracle | No limit published in the Oracle logical-limits reference; jOOQ documents 32,767 (architect must verify by container test) | 1,000 expressions per `IN` list (ORA-01795); identifiers 30 bytes before 12.2, 128 bytes since |
| DuckDB / Snowflake | No published numeric limit found; to be measured | Snowflake folds unquoted identifiers to upper case; DuckDB supports `?`, `$1`, `$name` |

Sources are listed in §12.

---

## 4. Gatekeeper Findings: Rejected Anti-Patterns and Safe Alternatives

| # | Anti-pattern in the 2026-10-06 plan | Risk | Safe alternative (binding for Phase 2) |
|---|---|---|---|
| AP-1 | **"Mathematical proof" / "constructively impossible" / "100% comment-free, syntactic immunity" claims** (plan §1, §8, ADR-017 "Garantie"). | Overstated assurance for auditors; demonstrably false today (G-5 raw fragments, G-6 post-security rewriting). | Replace with **named, testable invariants** (§6.1), each mapped to a test suite, a property-based test and a fuzz target. Wording in all docs: "enforced and continuously tested", never "proven". |
| AP-2 | **Big-bang default switch + decommission in "1-2 days"** (plan Phase 6) across six dialects and DML at once. | One defect becomes a cross-tenant data leak on every backend simultaneously. | **Per-dialect, per-statement-class increments** (§5) behind a typed flag with per-data-source override and a documented kill switch; legacy stays available as instant rollback until the exit criteria of increment 5 are met. |
| AP-3 | **Removing lexer deny-lists after cutover** ("`RejectBracketLexerDifferentials` becomes obsolete"). | Removes defense in depth; the AST builder still consumes the same Trino lexer, and `TrustedSqlExpression` fragments never pass through the emitter. | Keep input token guards permanently (cheap, independent layer). Remove a guard only with a per-guard security sign-off and a regression test proving the attack is blocked by another layer. |
| AP-4 | **Legacy output as the differential oracle** with "0 semantic deviations on 10,000 synthetic queries". | Legacy is not ground truth; the AST path is intentionally stricter in places. A zero-diff target either blocks forever or invites weakening the AST path to match legacy. | Diff **classification**: `identical`, `equivalent-result`, `ast-stricter (expected)`, `ast-looser (security defect, blocks)`, `ast-error`. Gate on **zero `ast-looser`** and a bounded, triaged `ast-error` rate. Result comparison must use deterministic `ORDER BY` or multiset hashing. |
| AP-5 | **Shadow mode that executes and discards** (current `ShadowDualRun`, G-1). | Double cost, zero evidence, hidden exceptions. | Scientist-style shadow: sampled (configurable rate), bounded latency budget, never alters the response, publishes a classified diff metric and a redacted structured log (query fingerprint + diff class, **no literals, no tenant values**). |
| AP-6 | **Six dialects as equal-tier deliverables**, including Snowflake with no executable test target. | Untested emitters shipped as "supported" (F-DIALECT-01 already lists them). | Dialect tiers (§6.3). A dialect becomes "supported" only with container (or in-process) execution tests; otherwise "experimental" and blocked from AST default. |
| AP-7 | **"Zero-allocation" and "< 2.2 KB/query"** without a benchmark; immutable record trees inherently allocate. | Unverifiable NFR; marketing claim in docs. | Measured budget relative to the legacy baseline (NFR-3), enforced by a BenchmarkDotNet regression gate. "Zero-allocation" applies to the emitter buffer only. |
| AP-8 | **Free-text engine selector** with silent fallback (G-2). | A typo silently runs the wrong security engine. | Typed enum with startup validation (fail fast on unknown value) and the effective engine exposed in telemetry and the audit record. |
| AP-9 | **Parameter budget as a single exception** with wrong limits (G-3, G-4). | False rejects on SQLite; missing Oracle IN-list guard; named params uncounted. | Per-dialect capability table (bind limit, IN-list limit, identifier length) as data; count **all** emitted markers; behavior on overflow is a stakeholder decision (§9, Q-4). |
| AP-10 | **Plan doc in German with in-doc status "Genehmigt"** and a non-standard file name. | Violates AGENTS.md §1 and §3. | Phase 2 delivers an English `YYYY-MM-DD-plan-ast-dialect-generator.md` that supersedes it (§11). |

---

## 5. Scope by Release Increment

Each increment is independently shippable and reversible. Increment N+1 starts only when increment N meets its Definition of Done.

### Increment 0 - "Make it measurable" (no default change)

- Typed engine selector (`Legacy`, `Ast`, `Shadow`) with fail-fast validation; per-data-source override (`DataSources:<name>:SqlRewriterEngine`) on top of the global `WebSql` default.
- Real shadow mode (AP-5) with classified diffs, sampling and OpenTelemetry metric `autheris.sql.rewriter.shadow_diff{dialect,statement_class,diff_class}`.
- Effective engine recorded in the audit event and as a span attribute.
- Fix G-3/G-4: dialect capability table, count all markers, Oracle IN-list guard.
- Security-predicate preservation oracle for the simplifier (G-6), see §6.1 INV-3.
- `TrustedSqlExpression` trust boundary made explicit and tested (G-5), see §6.1 INV-5.
- BenchmarkDotNet baseline for legacy vs AST (NFR-3).
- Resolve the ADR-017 vs code contradiction and the premature `[Obsolete]` signal (decision in Phase 2).

### Increment 1 - AST default for read-only `SELECT` on Tier-1 dialects (PostgreSQL, SQLite)

- `Ast` becomes the default for read-only statements on PostgreSQL and SQLite data sources; DML stays on legacy.
- Conformance matrix (sqlglot-style expected outputs) and container-executed result-equivalence corpus for both dialects.

### Increment 2 - Read-only `SELECT` on SQL Server and Oracle

- Same gates as increment 1, plus dialect-specific cases (bracket quoting, `OFFSET/FETCH` with synthetic `ORDER BY`, boolean projection, Oracle identifier folding and length, `IN`-list splitting/rejection).

### Increment 3 - DML (`INSERT`, `UPDATE`, `DELETE`) via AST on Tier-1/Tier-2 dialects

- WITH CHECK OPTION semantics, unfiltered-DML and tautology rejection, masked-column DML guards, consent-filtered insert rejection - all with legacy parity or stricter.

### Increment 4 - Analytical dialects (DuckDB, Snowflake)

- DuckDB: in-process execution tests (no container needed); eligible for Tier-1 treatment.
- Snowflake: remains **experimental** unless the stakeholder provides an execution target (Q-3). Experimental dialects can never be selected as AST default.

### Increment 5 - Legacy retirement

- Preconditions: increments 1-4 at DoD; shadow diff class `ast-looser` = 0 and `ast-error` below the agreed threshold over an agreed observation window (Q-2) in staging and production.
- Legacy `RlsListener` removed from the request path; kept in the test project as a differential reference for one further release, then deleted. Input lexer guards stay (AP-3).

### Non-Goals

- Any new SQL dialect beyond the seven `TargetSqlDialect` values.
- Cost-based optimization or plan-level pushdown decisions (Calcite-style planner). The simplifier must not grow into an optimizer within this track.
- Translating Trino functions not already covered by the function allow-list.
- Formal verification of the rewriter.
- Changes to GraphQL-to-SQL compilation (ADR-004) - separate pipeline.
- Distributed state and RBAC parts of ADR-017 (§1 and §3) - separate tracks.

---

## 6. Requirements

### 6.1 Security invariants (functional, must hold in every increment where `Ast` is selectable)

Each invariant must be backed by (a) example-based tests, (b) a property-based or generated test, and (c) a fuzz or differential target. Phase 3 owns the mapping.

| ID | Invariant |
|---|---|
| INV-1 | **Fail closed on the unknown.** Any parse-tree rule, AST node, function, dialect or capability without an explicit mapping raises; nothing is passed through silently. (Exists; keep.) |
| INV-2 | **Complete policy coverage.** Every physical table reference in every scope (root, subquery, CTE body, set-operation branch, lateral, scalar/`EXISTS`/`IN` subquery, DML source) carries its RLS filter and masking; CTE names never shadow qualified or out-of-scope physical tables. |
| INV-3 | **Predicate preservation across later passes.** No pass after `AstSecurityVisitor` (simplifier, validator, emitter) may remove, weaken or re-scope an injected security predicate. Injected predicates are marked and verified post-emission (e.g. by re-parsing the emitted SQL and checking the marked predicate is still a conjunct at the right scope). Alternatively Phase 2 moves simplification before injection. |
| INV-4 | **Values are bound, identifiers are quoted and resolved.** User-supplied values reach the backend only as bind parameters or as canonical literals emitted by the dialect generator; identifiers are always delimited per dialect with escaping of the closing delimiter, and resolved against the catalog allow-list. |
| INV-5 | **Explicit trusted-fragment boundary.** `TrustedSqlExpression` may only originate from gateway policy rendering, never from request input; its producer escapes all embedded values; every producer has tests with hostile policy values (quotes, delimiters, comment markers, Unicode). Long-term target: parse policy fragments into AST nodes and retire raw splicing. |
| INV-6 | **No source comments or source whitespace tokens in output.** Emitted SQL is produced only from AST nodes and trusted fragments. |
| INV-7 | **Bounded resources.** Depth limit, query length limit and dialect capability limits (bind count, IN-list size, identifier length) are enforced before execution with a typed, auditable error. |
| INV-8 | **Engine transparency.** The engine actually used is deterministic from configuration, recorded in the audit trail and telemetry, and part of the plan-cache key (exists). |
| INV-9 | **Shadow isolation.** The shadow path can never change the response, the executed SQL, the parameters or the error surface returned to the caller. |

### 6.2 Functional requirements

| ID | Requirement |
|---|---|
| FR-1 | Typed engine selector with values `Legacy`, `Ast`, `Shadow`; global default plus per-data-source override; unknown values fail startup. |
| FR-2 | Shadow mode: configurable sample rate (default 0 in production, 100% in staging), per-request time budget, diff classification per AP-4, redacted structured log and OTel metric, no effect on the response (INV-9). |
| FR-3 | Kill switch: switching a data source back to `Legacy` takes effect without redeploy (options reload) and invalidates affected plan-cache entries. |
| FR-4 | Dialect capability table (data, not code constants): bind limit, IN-list limit, identifier max length, boolean representation, pagination form, supported functions. Emitters consult it; unknown capability = reject. |
| FR-5 | Parameter accounting counts every emitted placeholder (positional, named, synthetic). Overflow behavior per Q-4. |
| FR-6 | Conformance suite per dialect: expected-output matrix (Trino input → per-dialect SQL) plus round-trip parse of emitted SQL where a parser is available. |
| FR-7 | Result-equivalence corpus executed against real engines (Testcontainers for SQL Server, PostgreSQL, Oracle; in-process for SQLite and DuckDB) comparing legacy vs AST result multisets for read-only statements. |
| FR-8 | Generated-query differential tests (seeded, reproducible) covering joins, set operations, CTEs, subqueries in every position, window functions and pagination; run nightly, failing seeds stored as regression cases. |
| FR-9 | F-DIALECT-01 and F-DATA-02 reflect the real engine selection behavior once an increment ships (Phase 6). |

### 6.3 Supported dialects (tiering)

| Tier | Dialects | Condition to be selectable as AST default |
|---|---|---|
| Tier 1 | PostgreSQL, SQLite | Increment 1 DoD met |
| Tier 2 | SQL Server, Oracle | Increment 2 DoD met |
| Tier 3 | DuckDB | Increment 4 DoD met (in-process execution) |
| Experimental | Snowflake, ANSI | Never selectable as default without an execution target and a stakeholder decision |

### 6.4 Non-functional requirements

| ID | Category | Requirement |
|---|---|---|
| NFR-1 | Security | All invariants in §6.1; Phase 3 sign-off per increment; no reduction of existing token guards (AP-3). |
| NFR-2 | Latency | AST path (parse + secure + simplify + emit) P95 and P99 at most **1.5x the legacy path** for the benchmark corpus, and under **2 ms P99** absolute for queries up to 4 KB, measured with BenchmarkDotNet on the CI reference runner. Plan-cache hits are unaffected. (Plan's 28 µs P95 is kept as a stretch goal only.) |
| NFR-3 | Allocation | Allocated bytes per query at most **1.25x legacy** (baseline captured in increment 0); **0 LOH allocations** for queries up to 64 KB; emitter buffer pooled. Regression gate: CI fails on >10% allocation regression against the stored baseline. |
| NFR-4 | Limits | Capability table values sourced from vendor documentation (§3) and verified by a container test per dialect where feasible. |
| NFR-5 | Shadow overhead | With shadow sampling at 1%, end-to-end P99 increase under 2%. Shadow work runs within a hard time budget and is abandoned (and counted) on overrun. |
| NFR-6 | Observability | OTel span attributes `sql.rewriter.engine`, `sql.target_dialect`, `sql.statement_class`; metrics for shadow diffs, AST errors by rule, limit rejections. No SQL literals or tenant values in telemetry. |
| NFR-7 | Compatibility | No change to public API contracts (WebSQL, SQL endpoints, MCP tools) other than stricter, documented rejections. |
| NFR-8 | Language & governance | All plans, code comments and docs in English (AGENTS.md §1); status only in `doc/plan/`. |

---

## 7. Acceptance Criteria and Definition of Done per Increment

### Increment 0

- AC-0.1 Unknown engine values fail application startup with a clear message (test).
- AC-0.2 In `Shadow` mode a seeded corpus produces classified diff metrics; a deliberately injected AST defect that loosens RLS is reported as `ast-looser` (mutation test).
- AC-0.3 Shadow exceptions and timeouts are counted and logged (redacted), never surfaced to the caller (INV-9 test).
- AC-0.4 A query with more bind placeholders than the dialect limit (named, positional and synthetic mixed) is rejected or handled per Q-4 for every dialect (test per dialect).
- AC-0.5 Oracle `IN` lists above 1,000 items are handled per Q-4; SQLite limit corrected to the bundled engine's value (container/in-process test).
- AC-0.6 Simplifier preservation: property test over generated predicates shows every injected security predicate is present in the emitted SQL at the correct scope (INV-3).
- AC-0.7 `TrustedSqlExpression` producers covered with hostile policy values for every dialect (INV-5).
- AC-0.8 BenchmarkDotNet baseline for legacy and AST committed; numbers recorded in the Phase 2 plan.
- **DoD-0:** all above green; `dotnet build Autheris.sln` and unit + integration suites green; Phase 3 sign-off; ADR-017 vs code contradiction resolved; master plan updated.

### Increment 1 (PostgreSQL, SQLite read-only)

- AC-1.1 Conformance matrix: at least 300 cases per dialect covering every node type the builder accepts.
- AC-1.2 Result-equivalence corpus: at least 1,000 read-only queries per dialect, **0 `ast-looser`**, every `ast-error` triaged with an issue or an accepted "stricter by design" note.
- AC-1.3 Nightly generated-query run of at least 10,000 seeded queries per dialect with 0 `ast-looser`.
- AC-1.4 NFR-2 and NFR-3 met.
- AC-1.5 Staging shadow at 100% for the agreed window (Q-2) with 0 `ast-looser`.
- AC-1.6 Kill switch drill: flipping a data source back to `Legacy` in staging takes effect without restart (FR-3).
- **DoD-1:** above green; default changed only for PostgreSQL and SQLite read-only; F-DIALECT-01/F-DATA-02 updated (Phase 6); master plan updated.

### Increment 2 (SQL Server, Oracle read-only)

- AC-2.1..2.6 as increment 1 for SQL Server and Oracle, plus dialect-specific cases: closing-bracket escaping, `ORDER BY (SELECT NULL)` pagination, boolean projection, `N'...'` literals, Oracle case folding, identifier length and IN-list limits.
- **DoD-2:** as DoD-1 for these dialects.

### Increment 3 (DML)

- AC-3.1 Every DML guard in `SecurityRemediationTests` and `SecurityReview20261002SqTests` passes identically or stricter on the AST path.
- AC-3.2 Container tests prove tenant-column WITH CHECK OPTION for `INSERT ... VALUES`, `INSERT ... SELECT` and `UPDATE` on each Tier-1/Tier-2 dialect.
- AC-3.3 Shadow comparison of emitted DML statements (not executed twice): 0 `ast-looser`.
- **DoD-3:** as above; DML default switched per dialect.

### Increment 4 (DuckDB, Snowflake)

- AC-4.1 DuckDB: in-process conformance and equivalence corpus as increment 1.
- AC-4.2 Snowflake: conformance matrix only; stays experimental unless Q-3 is answered with an execution target.
- **DoD-4:** DuckDB eligible as default; Snowflake status documented in F-DIALECT-01 without overstating support.

### Increment 5 (retirement)

- AC-5.1 Production shadow evidence over the agreed window: 0 `ast-looser`, `ast-error` below the agreed threshold.
- AC-5.2 Security sign-off per removed component; input token guards retained.
- **DoD-5:** `RlsListener` removed from the request path; ADR-017 status updated; F-DIALECT-01 final; master plan status `COMPLETED & VERIFIED`.

---

## 8. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| R-1 AST path looser than legacy in an untested construct → cross-tenant leak | Medium | Critical | INV-2, generated differential tests, shadow `ast-looser` gate, per-dialect increments, kill switch. |
| R-2 Simplifier changes security semantics (3-valued logic, NULL handling) | Medium | Critical | INV-3 preservation oracle; consider moving simplification before injection. |
| R-3 Trusted fragment renderer escapes incorrectly for a dialect | Low-Medium | Critical | INV-5 hostile-value tests; long-term parse fragments into AST. |
| R-4 Virtual filters already depend on the AST path, so regressions ship regardless of the WebSQL flag | Present today | High | Include `SqlFilterCompiler` outputs in the conformance and equivalence corpus from increment 0. |
| R-5 Result-equivalence false positives (non-deterministic ordering, float formatting, collation) | High | Low | Multiset hashing, deterministic `ORDER BY`, typed value normalization. |
| R-6 Snowflake/Oracle licensing or CI cost blocks execution tests | Medium | Medium | Tiering; Oracle Free container already used; Snowflake stays experimental. |
| R-7 Shadow overhead in production | Low | Medium | Sampling, time budget, off by default in production. |
| R-8 Documentation overstates guarantees (current F-DIALECT-01, ADR-017) | Present today | Medium | Phase 6 corrections; AP-1 wording rule. |

---

## 9. Open Questions for the Stakeholder (must be answered before Phase 2 is finalized)

- **Q-1 Rollout target:** Is the goal (a) AST as default for all channels and dialects with legacy removed, or (b) AST default only where evidence is complete, keeping legacy indefinitely for the rest? This PRD assumes (a) via increments, with (b) as the fallback position.
- **Q-2 Evidence window:** What observation window and `ast-error` threshold are acceptable before switching a dialect default and before legacy retirement (proposal: 14 days staging at 100% shadow, 30 days production at 1% shadow, `ast-error` < 0.1% of sampled queries, `ast-looser` = 0)?
- **Q-3 Snowflake:** Is Snowflake a customer commitment? If yes, who provides a test account / execution target for CI? If not, may F-DIALECT-01 mark it experimental?
- **Q-4 Limit overflow behavior:** When a query exceeds the bind or IN-list limit of a dialect: reject with a typed error (fail closed, simplest), split IN lists into OR-chained chunks, or use dialect-native array/table-valued parameters? Rejecting is the PM default.
- **Q-5 DML priority:** Is DML through the AST path required for the first customer-visible milestone, or can increments 1-2 (read-only) ship first?
- **Q-6 ADR-017 correction:** Should ADR-017 §2 be amended now to reflect that the AST compiler is opt-in for WebSQL (accurate), or should increment 1 make the ADR statement true?
- **Q-7 Trusted fragments:** Is converting gateway-rendered row filters and mask expressions from raw SQL fragments into AST nodes (retiring `TrustedSqlExpression`) in scope for this track or a follow-up?
- **Q-8 Performance budget:** Are the relative budgets (P99 at most 1.5x legacy, allocations at most 1.25x legacy) acceptable, or is a stricter absolute target required for specific customers?

---

## 10. Rollout & Telemetry

- Configuration: `WebSql:SqlRewriterEngine` (global default) and `DataSources:<name>:SqlRewriterEngine` (override); values `Legacy` / `Ast` / `Shadow`; `WebSql:ShadowSampleRate`, `WebSql:ShadowTimeBudgetMs`.
- Staging runs `Shadow` at 100% before each increment default switch; production runs `Shadow` at a low sample rate on data sources still on `Legacy`.
- Dashboards: shadow diff classes per dialect and statement class, AST error rules, limit rejections, P99 by engine.
- Audit: each governed query record contains the effective engine and target dialect.

---

## 11. Handoff Instructions for Phase 2 (`csharp-architect`)

1. The existing plan [2026-10-06-implementation-plan-ast-dialect-generator.md](2026-10-06-implementation-plan-ast-dialect-generator.md) is written in German and violates the English-only directive (AGENTS.md §1) and the naming convention (§3). **Do not translate it.** Deliver a new English implementation plan `doc/plan/YYYY-MM-DD-plan-ast-dialect-generator.md` that **supersedes** it, scoped to the increments in §5, and mark the old file as superseded with a link to the new plan.
2. Start from the as-built inventory in §2.2; do not re-plan M1-M4 as new work. Focus on G-1..G-8, the increment gates and the test infrastructure.
3. Decide and document: simplifier position relative to security injection (INV-3); typed engine enum and per-data-source configuration shape (FR-1); capability table location (FR-4); shadow execution model (synchronous with budget vs background) (FR-2).
4. Propose the ADR-017 amendment wording for Q-6 and the treatment of the premature `[Obsolete]` attribute on `RlsListener`.
5. Update this PRD's track row in [00-master-plan-overview.md](00-master-plan-overview.md) when the plan is ready.

---

## 12. References

- sqlglot repository and documentation: https://github.com/tobymao/sqlglot, https://sqlglot.com/sqlglot.html
- Apache Calcite `SqlDialect` and `RelToSqlConverter`: https://calcite.apache.org/javadocAggregate/org/apache/calcite/sql/SqlDialect.html, https://calcite.apache.org/javadocAggregate/org/apache/calcite/rel/rel2sql/RelToSqlConverter.html
- jOOQ inline threshold setting: https://www.jooq.org/doc/latest/manual/sql-building/dsl-context/custom-settings/settings-inline-threshold/
- Trino `SystemAccessControl` SPI (row filters, column masks): https://github.com/trinodb/trino/blob/master/core/trino-spi/src/main/java/io/trino/spi/security/SystemAccessControl.java
- Trino `RelationPlanner` (`addRowFilters`, `addColumnMasks`): https://github.com/trinodb/trino/blob/master/core/trino-main/src/main/java/io/trino/sql/planner/RelationPlanner.java
- Trino JDBC `JdbcClient` (`convertPredicate`): https://github.com/trinodb/trino/blob/master/plugin/trino-base-jdbc/src/main/java/io/trino/plugin/jdbc/JdbcClient.java
- Trino pushdown: https://trino.io/docs/current/optimizer/pushdown.html
- sqlglot test helpers (`validate_identity`, `validate_all`): https://github.com/tobymao/sqlglot/blob/main/tests/dialects/test_dialect.py
- Calcite adapters and JDBC pushdown: https://calcite.apache.org/docs/adapter.html
- OWASP SQL Injection Prevention Cheat Sheet: https://cheatsheetseries.owasp.org/cheatsheets/SQL_Injection_Prevention_Cheat_Sheet.html
- OWASP Query Parameterization Cheat Sheet: https://cheatsheetseries.owasp.org/cheatsheets/Query_Parameterization_Cheat_Sheet.html
- SQL Server maximum capacity specifications: https://learn.microsoft.com/en-us/sql/sql-server/maximum-capacity-specifications-for-sql-server
- PostgreSQL frontend/backend protocol message formats (Bind): https://www.postgresql.org/docs/current/protocol-message-formats.html
- PostgreSQL identifier length (`NAMEDATALEN`): https://www.postgresql.org/docs/current/sql-syntax-lexical.html
- SQLite limits (`SQLITE_MAX_VARIABLE_NUMBER`): https://www.sqlite.org/limits.html
- Oracle logical database limits: https://docs.oracle.com/en/database/oracle/oracle-database/19/refrn/logical-database-limits.html
- Oracle ORA-01795 (IN-list limit 1000): https://docs.oracle.com/error-help/db/ora-01795/
- Oracle object names (30 vs 128 bytes): https://docs.oracle.com/en/database/oracle/oracle-database/19/sqlrf/Database-Object-Names-and-Qualifiers.html
- DuckDB prepared statements: https://duckdb.org/docs/current/sql/query_syntax/prepared_statements.html
- Snowflake bind variables: https://docs.snowflake.com/en/sql-reference/bind-variables
- SQLancer: https://github.com/sqlancer/sqlancer
- GitHub Scientist: https://github.com/github/scientist

---

## 13. Changelog

- 2026-10-10: Initial PRD (Phase 1). Qualified the track, recorded the as-built state, rejected AP-1..AP-10, defined increments 0-5 and open questions Q-1..Q-8. Registered as `PLAN-AST-DIALECT-GEN-16` in the master plan.
