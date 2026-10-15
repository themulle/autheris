# Solution Architecture Review & Strategic Remediation Roadmap

**Document ID:** `PLAN-ARCH-REVIEW`  
**Location:** `docs/plans/architecture.md`  
**Status:** R0 COMPLETE ✅ · R1 NEARLY COMPLETE · R2–R6 PLANNED 📋  
**Last Updated:** 2026-10-14 · **Baseline:** `main` at `a4137626`  
**Parent Plan:** [`00-master-plan-overview.md`](00-master-plan-overview.md)  
**Implementation Plans:** [`implementation/impl-arch-r0-r2-trust-ha-eventing.md`](implementation/impl-arch-r0-r2-trust-ha-eventing.md), [`implementation/impl-arch-r1-r3-pipeline-persistence.md`](implementation/impl-arch-r1-r3-pipeline-persistence.md), [`implementation/impl-arch-r4-r6-api-perf-hygiene.md`](implementation/impl-arch-r4-r6-api-perf-hygiene.md); global order in [`implementation/00-implementation-roadmap.md`](implementation/00-implementation-roadmap.md)  
**Role:** Solution Architect & Engineering Lead  
**Scope:** Full-solution architectural inspection across all production assemblies (`Autheris.Api`, `Autheris.Application`, `Autheris.Domain`, `Autheris.Infrastructure`, `Autheris.Extensions`, `Autheris.GraphQL`, `TrinoSqlEngine`), test suites, and deployment configurations.

---

## 1. Executive Summary

The review produced 121 findings (26 High, 62 Medium, 33 Low) across six slices, synthesized into ten themes. The solution graph is acyclic and follows the Onion Architecture rule (Domain → Application → Infrastructure / Extensions / GraphQL → Api). The original review text is in git history (`git show d8db81cc^:docs/plans/2026-10-14-architecture-code-review/`).

Implementation Waves 1–3 delivered R0 (correctness and trust) and most of R1 (unified query pipeline). The finding-level status is in the three implementation plans.

---

## 2. Themes: Open Targets

| Theme | Status on `main` | Open target (WPs) |
|---|---|---|
| **T1** One governed query pipeline | Mostly done: shared `IGovernedTableReadPlanner`, `GovernedQueryStages`, `SqlTableReadExecutor` (no `SqlDataSourceExecutor`); `TreeSqlCompiler` is the documented exception (G-6). | Endpoint use-case services (B-07); remove the `LegacyTextRowFilterEvaluator` text fallback (ARC-C-12 remainder). |
| **T2** Unified audit contract | Mostly done: `IAuditWriter`, `AuditPolicyTable`, chain-violation latch, no `Null*` fallbacks. | Migrate the remaining direct `RecordAuditEventAsync` sites (R-2); group-commit audit sink (A-07). |
| **T3** Thin Application layer via ports | Open. | `ISqlDialectProvider` registry (B-09); `Autheris.Infrastructure.Analytics` and package-boundary tests (B-10); folder/interface hygiene (B-11). |
| **T4** Persistence & versioned migrations | Partly done: versioned migration runner (`autheris_schema_migrations`), fail-closed startup, no constructor DDL. | Not-ready guard, constructor I/O test, required dependencies (B-12 remainder); pooled SQLite/WAL (B-13); per-aggregate stores (B-14). |
| **T5** Cluster & HA semantics | Partly done: absent vs unavailable (`ClusterStateUnavailableException`), topology guard, shared TTL state for access plans, tokens and secrets. | Leases and durable CDC watermark (A-11), async jobs (A-12), cluster-wide CDC fan-out (A-13), Casbin cache and epoch (A-14). |
| **T6** Eventing, webhooks & plugins | Partly done: bounded queued event bus, CDC webhooks behind a default-off flag, interceptor failure policy. | Webhook outbox (A-02), CDC webhook delivery (A-03), plugin listener reflection (R-1); versioned plugin abstractions skipped (G-13). |
| **T7** Configuration & god classes | Open. | `GatewayOptions` split and validation (C-01..C-03); god-class decomposition (C-09, C-10). |
| **T8** API surface standardization | Open (interim `SafeErrorResults` in place). | Pipeline hygiene (C-04), RFC 7807 ProblemDetails (C-05), declarative policies (C-06), endpoint decomposition (C-07), route versioning (C-08). |
| **T9** Hot-path performance | Partly done: pooled parser worker threads, shared engine instance, bounded syntax-error path. | Compile-cache and AST walk (C-12), streaming core and egress (C-13, C-14), catalog hot paths (C-15), startup I/O and telemetry (C-16). |
| **T10** Engineering hygiene | Partly done: `Autheris.Tests.Common`, no `#if DEBUG` seams in `src/`, `AutherisIncludeTestAuth` off by default. | Shared test doubles, traits, banned APIs (C-17 remainder); `TimeProvider` (C-18); split `Autheris.Tests.Unit` and delete `Autheris.slnx` (C-19); engine packaging (C-20); analyzer and documentation hygiene (C-21); integration hygiene (C-22). |

---

## 3. Remediation Waves (R0–R6)

| Wave | Focus | Status | Open deliverables |
|---|---|:---:|---|
| **R0** | Correctness & Trust | **DONE ✅** | Small remainders R-1..R-4 (see r0-r2 plan). |
| **R1** | Unified Pipeline | **NEARLY DONE 🚀** | B-07, ARC-C-12 remainder. |
| **R2** | HA & State | **PLANNED 📋** | A-02, A-03, A-11..A-14 (roadmap Wave 4). |
| **R3** | Persistence & Dialects | **IN PROGRESS 🚀** | B-09, B-10, B-12 remainder, B-13, B-14. |
| **R4** | API & Options | **PLANNED 📋** | C-01..C-08. |
| **R5** | Performance | **IN PROGRESS 🚀** | C-12..C-16 (C-11 parser pool delivered). |
| **R6** | Engineering Hygiene | **IN PROGRESS 🚀** | C-17 remainder, C-18..C-22, B-11. |

---

## 4. Architecture Test Guardrails

1. **Domain Isolation:** `Autheris.Domain` references zero external packages (BCL only). *Project references are tested; the package-level test is added by B-10.*
2. **Application Decoupling:** `Autheris.Application` must not reference concrete ADO.NET packages (`Microsoft.Data.*`, `Npgsql`, `DuckDB.NET`), `Apache.Arrow`, or construct raw `HttpClient`. *Not yet enforced; added by B-10.*
3. **DI Cleanliness:** No usage of `IServiceProvider.GetService()` or service-locator anti-patterns outside composition roots. *Enforced for audit repositories in `Autheris.Api`; broader rule in C-07 / C-21.*
4. **GraphQL Encapsulation:** `HotChocolate.*` is restricted to `Autheris.GraphQL` and composition roots.
5. **No Production Test Doubles:** No production class references `Null*` repositories or test mocks (`NoProductionNullTestDoublesArchitectureTests`).
6. **No Constructor I/O:** Types registered as singletons must not open connections or run queries in constructors. *Not yet enforced by a test; added by the B-12 remainder.*

The cluster-state contract is `IDistributedClusterStateProvider`.
