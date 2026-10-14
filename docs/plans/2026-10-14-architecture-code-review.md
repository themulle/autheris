# Architecture Code Review - Full Solution (2026-10-14)

**Plan ID:** `PLAN-ARCH-REVIEW-2026-10-14`
**Role:** Solution Architect (Phase 2 perspective, read-only review)
**Commit base:** `88f7df1c` (`main`)
**Parent:** [`00-master-plan-overview.md`](00-master-plan-overview.md)
**Related:** `2026-10-13-plan-security-audit-and-remediation.md` *(archived, see git history)* (AppSec audit; pure security bugs are out of scope here), `2026-10-10-plan-ast-dialect-generator.md` *(completed & archived, see git history; documented in [F-DIALECT-02](../features/f-dialect-02-ast-sql-compiler.md))* (X1 cutover)

Detailed evidence per slice (each finding with `path:line`, problem, impact, recommendation, effort):

| # | Slice | Detail report | Findings |
|---|---|---|---|
| 01 | Layering, Domain, DI composition, architecture tests, docs consistency | [01-layering-and-composition.md](2026-10-14-architecture-code-review/01-layering-and-composition.md) | ARC-L-01..18 |
| 02 | `Autheris.Api` (endpoints, middleware, MCP) | [02-api-layer.md](2026-10-14-architecture-code-review/02-api-layer.md) | ARC-A-01..19 |
| 03 | Application core (governance, policy, SQL, audit, caching, state) | [03-application-core.md](2026-10-14-architecture-code-review/03-application-core.md) | ARC-C-01..19 |
| 04 | Application feature modules + `Autheris.Extensions` | [04-application-features-and-extensions.md](2026-10-14-architecture-code-review/04-application-features-and-extensions.md) | ARC-F-01..20 |
| 05 | `Autheris.Infrastructure` + `Autheris.GraphQL` | [05-infrastructure-and-graphql.md](2026-10-14-architecture-code-review/05-infrastructure-and-graphql.md) | ARC-I-01..13, ARC-G-01..08 |
| 06 | `TrinoSqlEngine` + test landscape | [06-sql-engine-and-tests.md](2026-10-14-architecture-code-review/06-sql-engine-and-tests.md) | ARC-S-01..16, ARC-T-01..08 |

---

## 1. Executive Summary

**Overall verdict: structurally sound at the project level, but the solution has outgrown its internal boundaries.**

The project graph is acyclic and follows the Onion dependency rule, Cecil-based architecture tests exist, the outbound HTTP chain is hardened centrally, and plugin loading is hash-pinned with collectible `AssemblyLoadContext`. Fail-closed thinking is visible in many places.

The main architectural risks are not in the project graph but *inside* the layers:

1. **No single enforcement pipeline.** Governance is decided centrally (`TableAccessPolicy` + Casbin), but SQL generation, RLS/masking injection and audit run through at least four parallel paths (GraphQL/OData/REST table reads with string-composed `WHERE`, WebSQL/federation via the token-stream rewriter, the new AST compiler, procedures). The new `GovernedSqlCompiler` has **no production caller**; production still defaults to `SqlRewriterEngine = "LegacyTokenStream"`. Every protocol therefore has its own semantics for masking, row filters and audit.
2. **Audit semantics are inconsistent.** Some paths fail closed on audit failure, some swallow, some fall back to `NullAuditLogRepository` by constructor overload, and ~10 Api handlers write audit records by hand.
3. **Cluster/HA correctness is assumed, not designed.** Several components keep authoritative state in per-node memory (access plans + one-time tokens + secrets, CDC watermarks, async job state, GraphQL subscriptions, Casbin decision cache epoch). `RedisClusterStateProvider` maps every Redis error to "absent", pub/sub invalidation is at-most-once, and distributed lock leases are not renewed.
4. **The Application layer is "fat".** It compiles DuckDB, Parquet, Arrow Flight, ADO.NET execution, Oracle specifics, prometheus-net and HTTP adapters. Dialect knowledge (`DatabaseDialect.`) is spread over ~40 files; adding a dialect touches 15-20 places in 4 projects.
5. **Persistence of the gateway's own metadata is triplicated and unversioned.** One governance repository class per backend (SQLite/SQL Server/PostgreSQL, 14.8k LOC, 58% of Infrastructure) implements ~13 ports; schema evolution is idempotent DDL in constructors without a version table.
6. **Eventing/webhooks (latest feature) are not production-grade.** The CloudEvents CDC webhook subsystem is registered and exposed via CRUD endpoints but is never invoked by production code; the event bus awaits all handlers inline on the request path; webhook delivery has no outbox and retries 4xx; there are two divergent webhook dispatchers with different signature schemes.
7. **Size and cohesion debt.** `GatewayOptions` (2,034 lines, 77 classes, injected at 275 sites), `CasbinEnforcementService` (1,897), several 900-1,000-line endpoint files, 1,500-line AST builder, and a 101k-LOC monolithic unit test project.

### Finding statistics

| Slice | Critical | High | Medium | Low | Total |
|---|---|---|---|---|---|
| 01 Layering | 0 | 3 | 9 | 6 | 18 |
| 02 Api | 0 | 4 | 10 | 5 | 19 |
| 03 App core | 0 | 5 | 10 | 4 | 19 |
| 04 Features/Ext | 0 | 4 | 10 | 6 | 20 |
| 05 Infra/GraphQL | 0 | 6 | 10 | 5 | 21 |
| 06 Engine/Tests | 0 | 4 | 13 | 7 | 24 |
| **Total** | **0** | **26** | **62** | **33** | **121** |

After de-duplication across slices (see §3), the 121 raw findings collapse into **10 architectural themes** and **~100 distinct findings**.

---

## 2. Verification Log

Findings were produced by six parallel slice reviews and then spot-checked against the code by the lead architect. Line numbers in the detail reports were partly reconstructed from partial reads; treat them as pointers and re-confirm before editing.

| Finding | Check | Result |
|---|---|---|
| ARC-S-01 | `src/TrinoSqlEngine/FastSqlEngine.cs:106` default `"LegacyTokenStream"`; grep for `GovernedSqlCompiler` outside `TrinoSqlEngine` | **Confirmed**: no production caller, no appsettings override |
| ARC-S-03 | `FastSqlEngine.cs:36` `DefaultParseThreadStackSize = 16 MB`; `new Thread(...)` per parse at `:228` | **Confirmed**. Note: intentional (deep ANTLR recursion + hard cancellation); the remedy is a bounded pool of long-lived large-stack threads, not removal |
| ARC-I-03 | `src/Autheris.Infrastructure/State/RedisClusterStateProvider.cs:56-239` - every `catch (Exception)` returns `default`/`null`/`false` | **Confirmed** |
| ARC-C-05 | `src/Autheris.Application/Governance/Services/AccessPlanningService.cs:48-50` - three `ConcurrentDictionary` (plans, consumed tokens, in-memory vault), no eviction found | **Confirmed** |
| ARC-C-01 | `src/Autheris.Application/Services/SqlDataSourceExecutor.cs:305-345` - `WHERE` assembled via `string.Join` of predicate fragments | **Confirmed** as parallel path. Values are bound as parameters; this is a consistency/architecture finding, not an injection finding |
| ARC-C-04 | `NullAuditLogRepository.Instance` used in convenience constructors of 7 services (e.g. `GovernedSqlExecutionService.cs:74,133`) | **Confirmed with nuance**: MS DI selects the longest satisfiable constructor, so production wiring receives the real repository. The risk is silent audit loss when a registration is missing or a test/host uses the short overload. Severity kept High because the failure is silent |
| ARC-F-01 | grep for `ICloudEventTransformer` / `ICloudEventWebhookDispatcher` callers | **Confirmed**: tests only |
| ARC-F-02 | `InMemoryGatewayEventBus.cs:62-63` - `await Task.WhenAll(handlers)` inside `PublishAsync` | **Confirmed** |
| ARC-G-01 | `MutationTypes` - ITSM dispatcher, status logic and compensation inside the GraphQL resolver (`:182-276`) | **Confirmed** |

---

## 3. Architectural Themes (de-duplicated)

### T1 - Converge on one governed query pipeline (Critical path)
**Findings:** ARC-C-01, ARC-S-01, ARC-S-02, ARC-C-12, ARC-A-01, ARC-A-05, ARC-G-01, ARC-G-07, ARC-T-06

- Four execution paths with separately maintained semantics; two masking engines and two row-filter evaluators (ARC-C-12); two AST pipelines (`GenerateGovernedSql` skips verifier/invariant checks, ARC-S-02).
- Business workflows live in protocol adapters (GraphQL mutation for consent requests, Api handlers for governance/VF/DuckDB, MCP resources rendered in Api bypassing guardrails).

**Target architecture:** a single `IGovernedQueryPipeline` in Application (parse -> authorize -> typed policy IR -> compile per dialect -> execute -> audit) consumed by every protocol adapter (GraphQL, OData, REST, WebSQL, MCP, Arrow/Flight). Table reads become AST queries instead of string-composed SQL. Workflows (consent request, ITSM) move into Application use-cases callable from GraphQL, REST and MCP.
**Dependency:** this is the X1 cutover of `PLAN-AST-DIALECT-GEN-16`; this review adds the table-read path and `GenerateGovernedSql` to the X1 scope.

### T2 - One audit contract with explicit failure semantics
**Findings:** ARC-A-02, ARC-C-03, ARC-C-04, ARC-C-14, ARC-C-09, ARC-C-17

Define an `AuditPolicy` per event class (Tier-A security decisions: fail-closed; Tier-B telemetry: async/best-effort with bounded queue) and enforce it in one decorator/pipeline step. Remove `NullAuditLogRepository` overloads from production types; replace hand-written audit in Api handlers by the pipeline. Move ALLOW-path audit off the hot path via a durable, bounded channel.

### T3 - Thin the Application layer; introduce ports
**Findings:** ARC-L-02, ARC-C-02, ARC-F-19, ARC-I-02, ARC-S-07, ARC-L-04, ARC-C-10, ARC-C-16, ARC-C-18

Introduce `IDialect` / `ISqlDialectProvider` (quoting, parameter markers, paging, capability table) owned by the compiler and resolved once; move ADO.NET/DuckDB/Arrow/Parquet/Flight/Oracle code to Infrastructure (or dedicated `Autheris.Infrastructure.Analytics`). Dissolve `Services/` and `Interfaces/` buckets into feature folders. Extend architecture tests to forbid infrastructure packages in Application and third-party types in Domain (ARC-L-08).

### T4 - Governance persistence: one model, versioned migrations
**Findings:** ARC-L-03 = ARC-I-01 (duplicate), ARC-I-04, ARC-I-05, ARC-I-12, ARC-I-13

Split the per-backend god repository into per-aggregate repositories sharing a dialect-neutral SQL core (or adopt a lightweight mapper with dialect adapters). Introduce a versioned migration runner (schema-version table, ordered scripts per backend, run once at startup under a distributed lock - not in constructors). Replace the single shared `SqliteConnection` + semaphore with pooled connections in WAL mode.

### T5 - Make cluster semantics explicit (HA readiness)
**Findings:** ARC-C-05, ARC-I-03, ARC-I-07, ARC-I-08, ARC-G-03, ARC-F-06, ARC-F-07, ARC-F-10, ARC-C-07, ARC-C-13, ARC-L-16

- Classify every piece of state as *node-local cache*, *cluster-shared* or *durable*; document it in arc42 §8.
- `IClusterStateProvider` must distinguish *absent* from *unavailable* (`Result`/exception); consumers decide fail-closed explicitly.
- Pub/sub invalidation: add epoch reconciliation (periodic epoch compare) to cover lost messages.
- Move access plans/one-time tokens/secrets, CDC watermarks and async-job state to the cluster store with TTLs; use Redis-backed GraphQL subscriptions when clustered.
- Distributed locks: lease renewal + fencing token; move acquire inside the worker's error handling.
- Document embedded Garnet as single-node only and fail startup when `Cluster.Enabled` with embedded mode.

### T6 - Eventing, webhooks and plugin contract
**Findings:** ARC-F-01, ARC-F-02, ARC-F-03, ARC-F-04, ARC-F-11, ARC-F-15, ARC-F-16, ARC-F-17, ARC-F-20

- Decide on ARC-F-01 immediately: wire a hosted `CdcWebhookDeliveryService` or remove the endpoints/registrations.
- Event bus: publish to a bounded `Channel<T>`, dispatch on a background worker; per-listener timeout and isolation.
- Webhooks: transactional outbox, retry only on 5xx/408/429/network errors, fresh timestamp+signature per attempt, dead-letter state; merge the two dispatchers into one signature scheme.
- Plugins: publish a versioned `Autheris.Plugin.Abstractions` assembly (semver, compatibility check on load) instead of exposing all of `Autheris.Application`; implement or remove `ConfigureServices` and `EnableHotReload`.

### T7 - Configuration and god-class decomposition
**Findings:** ARC-L-01, ARC-L-07, ARC-L-11, ARC-A-04, ARC-C-06, ARC-F-14, ARC-S-11, ARC-G-08, ARC-C-11

Split `GatewayOptions` into per-feature options classes bound to their own sections (`IOptions<OdataOptions>` etc.), validated with `IValidateOptions<T>` + `ValidateOnStart` (single mechanism). Decompose `CasbinEnforcementService`, VF administration, WebSQL/Governance/VF endpoint files and `SqlAstBuilder` along responsibilities.

### T8 - API surface consistency
**Findings:** ARC-A-03, ARC-A-07, ARC-A-08, ARC-A-09, ARC-A-10, ARC-A-13, ARC-A-14, ARC-A-15, ARC-A-16, ARC-A-17, ARC-A-18, ARC-A-19, ARC-A-06

ProblemDetails everywhere (one error-code catalog), route groups with a versioning policy, declarative authorization policies instead of 43 imperative `HasRole` checks, extracted named middleware classes (CSRF/CORS), domain-path rewrite before the security pipeline, one OpenAPI generation approach, HTML/JS moved to static assets.

### T9 - Hot-path performance
**Findings:** ARC-S-03, ARC-S-04, ARC-S-09, ARC-S-13, ARC-S-15, ARC-A-11, ARC-A-12, ARC-I-06, ARC-G-02, ARC-G-06, ARC-C-14, ARC-I-10

Large-stack parser thread pool; singleton `FastSqlEngine`; streaming `IAsyncEnumerable` for connector reads and Arrow/Parquet egress; cached catalog snapshot per epoch for OData; event-driven catalog change detection instead of 60 s N+1 polling on every node; async startup initialisation via `IHostedService` instead of blocking factories.

### T10 - Engineering hygiene, tests and documentation
**Findings:** ARC-T-01..08, ARC-L-08, ARC-L-09, ARC-L-10, ARC-L-13, ARC-L-14, ARC-L-15, ARC-L-17, ARC-S-06, ARC-S-10, ARC-S-12, ARC-S-14, ARC-S-16, ARC-I-09, ARC-I-11, ARC-G-04, ARC-G-05, ARC-F-12, ARC-F-13, ARC-F-18, ARC-C-19

- Split `Autheris.Tests.Unit` per production assembly; shared `Autheris.Tests.Common` for doubles; `TimeProvider`/`FakeTimeProvider` instead of `DateTime.Now` and `Task.Delay`; traits for file-system/network tests.
- Remove `#if DEBUG` security seams (Debug and Release compile different security code).
- One solution file; remove `doc` symlink, duplicate/German plan files, stray artifacts; fix stale arc42 quality claims.
- One telemetry stack (OTel meters with Prometheus exporter).
- Revisit the 25 globally suppressed CA rules and `AutherisIncludeTestAuth=true` default.

---

## 4. Prioritized Remediation Roadmap

Each wave follows the 6-phase lifecycle (PRD -> architecture plan -> security review -> TDD -> code review -> documentation) with its own child plan in `docs/plans/`.

| Wave | Goal | Themes / Findings | Effort | Precondition |
|---|---|---|---|---|
| **R0 - Immediate (correctness/trust)** | Stop shipping misleading or silently unsafe behaviour | ARC-F-01 (wire or remove CDC webhooks), ARC-I-03 (absent vs unavailable), ARC-C-05 (TTL + cluster store for plans/tokens), ARC-C-04 (remove `Null*` audit overloads), ARC-F-02 (async bus dispatch), ARC-S-06 (`#if DEBUG` seams) | S-M each | none |
| **R1 - Single pipeline** | One governed query pipeline for all protocols | T1, T2; extend X1 cutover scope with table-read path, `GenerateGovernedSql`, procedures | L | `PLAN-AST-DIALECT-GEN-16` X0 packages green |
| **R2 - HA readiness** | Defined cluster semantics | T5, ARC-F-03 (outbox), ARC-F-06, ARC-F-07, ARC-F-10, ARC-G-03 | M-L | R0 |
| **R3 - Persistence & dialects** | Versioned migrations, dialect port | T3 (dialect port), T4 | L | R1 (dialect port shared with compiler) |
| **R4 - Structure & API** | Decompose god classes, config split, API consistency | T7, T8, T6 (plugin abstractions) | M-L | can run in parallel to R2 |
| **R5 - Performance** | Hot-path allocation and streaming | T9 | M | R1 for parser/engine items |
| **R6 - Hygiene** | Tests, docs, build | T10 | S-M, continuous | none |

### Suggested architecture-test additions (guard rails for all waves)
1. Domain references no third-party package except BCL (MemoryPack DTOs move to Application/Infrastructure).
2. Application references no `Microsoft.Data.*`, `Npgsql`, `DuckDB.*`, `Parquet`, `Apache.Arrow.Flight*`, `prometheus-net`, `System.Net.Http.HttpClient` construction.
3. No `IServiceProvider` / `GetService` outside composition root.
4. No `HotChocolate.*` outside `Autheris.GraphQL` and composition root.
5. No production type references `Null*` test doubles; no `#if DEBUG` in `src/`.
6. No constructor I/O in types registered as singletons (heuristic: no `Execute*`/`Open*` in constructors).

---

## 5. Strengths to Preserve

- Acyclic project graph matching Onion rules; Cecil-based architecture tests already in CI.
- Central policy decision point (`TableAccessPolicy` + Casbin) with fail-closed defaults in most paths.
- Hardened outbound HTTP chain (SSRF egress allowlist, bounded response reads) reused by almost all clients.
- Plugin loading with hash pinning and collectible `AssemblyLoadContext`.
- Parser concurrency limiting with hard time budgets; parameter binding for filter values on the table-read path.
- Epoch/consent cache fails closed on Redis unavailability.
- Broad test investment (~5,380 tests across 6 projects) including a dedicated SQL compiler gate.

---

## 6. Open Questions for Stakeholders

1. **HA target:** is multi-node deployment a supported production topology today? If yes, T5 moves to R0.
2. **CDC webhooks (ARC-F-01):** wire now, or remove the endpoints until a delivery service exists?
3. **Plugin compatibility promise:** are external plugin authors expected? This decides whether a versioned abstractions package is required (ARC-F-04).
4. **Governance store:** must all three backends (SQLite, SQL Server, PostgreSQL) stay first-class, or can SQLite be limited to single-node/dev mode?
5. **Telemetry:** OTel-only (with Prometheus exporter) acceptable for operations?
