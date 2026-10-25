# Master Architecture & Implementation Backlog

**Last Updated:** October 24, 2026 · **Branch:** `main` (v1.2.11)  
**Role:** Solution Architect & Agent Coordinator  
**Location:** `docs/plans/00-master-plan-overview.md`  
**Purpose:** Canonical origin plan, dependency graph, and active track status for all AI agents and engineers working on the Autheris ecosystem.

---

## 🧭 Origin Plan Protocol for AI Agents

> [!IMPORTANT]
> **Directive for all AI agents and contributors:**
> 1. **Language:** All plans, requirements, and documentation must be written in **English** (aligned with [README.md](file:///root/autheris/README.md)).
> 2. **Repository of Requirements:** All new requirements, design documents, and feature requests must be stored in [`docs/plans/`](./).
> 3. **Consolidated Category Structure:** Active work is consolidated under three core categories:
>    - [`security.md`](security.md): Threat modeling, AppSec findings, hardening, and WORM compliance.
>    - [`architecture.md`](architecture.md): Full-solution architecture reviews, layering, component decoupling, and strategic waves (R0–R6).
>    - [`features.md`](features.md): Functional roadmap (aggregations #28, privacy #37, dbt semantic layer #38).
> 4. **Refinement on the Origin Plan:** When refining an existing feature or adding a new requirement, agents must:
>    - Update the corresponding category document (`security.md`, `architecture.md`, or `features.md`).
>    - Update the execution matrix and status below in this Origin Plan.
>    - Maintain traceable bidirectional links between parent and child documents.
> 5. **Housekeeping:** Plans whose work is fully merged and has nothing open are deleted; they are listed under "Completed plans (history in git)" below with their id and PR, so the history stays traceable through `git log`.

---

## 0. 🎯 Active & Prioritized Backlog

**Rule (owner):** Security-critical items and bugs first, then urgently missing features, only then larger add-ons. Work is pulled strictly top-down; every item passes the 5-phase standard implementation lifecycle (Product Manager qualification $\to$ Solution Architect plan with Gemini 4 Pro $\to$ Security Expert audit $\to$ C# TDD Developer execution $\to$ Code Reviewer inspection). There is no documentation phase in `docs/` (owner rule 2026-10-17); all progress is tracked exclusively here in `docs/plans/00-master-plan-overview.md`.

### Active Tracks

| Rank | Item | Size | Status | Next Milestone / Notes |
|---:|---|:---:|:---:|---|
| **0** | **Security findings log remainders** (findings log [`security.txt`](security.txt); remaining area plans [`INF`](2026-10-22-plan-security-inf.md), [`MISC`](2026-10-22-plan-security-misc.md), [`PERS`](2026-10-22-plan-security-pers.md)) | S | **MERGED (PR #287); REMAINDERS OPEN** | All HIGH/MEDIUM code findings resolved (PR #287, SQLA-06 follow-up included). Open owner actions: OPS-01 (rotate the PAT in the local remote URL), create the protected `release` GitHub environment with reviewers (MISC-01/02 workflows already require it), PERS-04 residual decision (`ExtendConsentExpiryAsync` still accepts expired consents, conflicts with SEC-SC-20). Open LOW findings: INF-07, INF-13, GQL-14, GQL-16, GQL-17, EXT-17; partial LOW: GQL-13, MCP-15, INF-12, VF-10. MISC follow-up PRD (data-subject keys in audit records for row-level Art. 15 evidence) not started. |
| **1** | **Security review follow-up to #271** ([`REQ-SEC-271`](2026-10-21-req-issue-271-security-audit.md), PR #273 and #278 merged) | S | **REMAINDERS OPEN** | `REQ-SEC-271-15` partial: actor pseudonymization key still falls back to `Gateway:Cluster:Secret` (`GatewayGovernanceServiceExtensions.cs`); `REQ-SEC-271-03` startup rule not implemented (purge threshold uses the max of all retention periods). |
| **2** | **Issue #281 follow-up:** default Helm ingress `/` prefix still forwards `/api/admin` (SEC-281-6 design gap; routes require ClusterAdmin + scope) | S | **OWNER DECISION** | Plan [`2026-10-23-plan-issue-281-app-scoped-trusted-documents.md`](2026-10-23-plan-issue-281-app-scoped-trusted-documents.md). Feature merged in PR #291. |
| **3** | **Issue #282 follow-ups:** repository insert-if-absent (SEC-282-5 residual); live-introspection admin endpoint (OQ-1, deferred to a separate issue) | S | **PLANNED** | Plan [`2026-10-23-plan-issue-282-catalog-register-sql-tables.md`](2026-10-23-plan-issue-282-catalog-register-sql-tables.md). Feature merged in PR #290. |
| **4** | **Nightly parity suite follow-ups** ([`REQ-PARITY-NIGHTLY`](2026-10-23-req-nightly-parity-tests.md), plan [`2026-10-23-plan-nightly-parity-tests.md`](2026-10-23-plan-nightly-parity-tests.md)) | M | **FOLLOW-UPS OPEN** | Suite merged (PR #292), gaps fixed (PR #317, PR #331: Robin 100 scenarios, no known gap left). Open: PARITY-FU-001 (MCP driver, 36 MCP cells `Pending`), PARITY-FU-002 (mutation sweep), PARITY-FU-003 (container confirmation of set-operation provider codes), SQL Server dialect, DuckDB (WP5b), PostgreSQL dialect baseline and container gates after the first CI/nightly run. |
| **5** | **BL-07:** PostgreSQL wire protocol endpoint ([`REQ-PG-WIRE-01`](2026-10-16-req-postgres-wire-protocol-endpoint.md)) | L | **PROPOSED** | Conditional go pending owner decision on the open questions (PRD section 13). |
| **6** | **WP-ARC-C-19 / WP-ARC-B-11:** unit test project physical separation; folder and interface hygiene ([`architecture.md`](architecture.md), [`implementation/00-implementation-roadmap.md`](implementation/00-implementation-roadmap.md)) | S | **DEFERRED** | Needs a dedicated merge-freeze window on `tests/Autheris.Tests.Unit/**`. |
| **7** | **Security Wave C deployment remainders** `SR-D-06`..`SR-D-09` ([`security.md`](security.md) section 2, [`implementation/impl-security-wave-c-2.md`](implementation/impl-security-wave-c-2.md)) | S | **PARTIALLY RESOLVED** | Open remainders listed in `security.md` section 2. |
| **8** | **Simulation test programme** ([`REQ-SIMTEST`](2026-10-24-req-simulation-testing.md), plan [`2026-10-24-plan-simulation-testing.md`](2026-10-24-plan-simulation-testing.md)) | L | **STAGE 1 MERGED (PR #344)** | Stage 1: sqllogictest corpus (P1), Schemathesis nightly (P2), SharpFuzz on TrinoSqlEngine (P3). Open: first `workflow_dispatch` run of `simulation-nightly.yml`, repeat fuzz runs, Stages 2-4 (policy-aware generator, accelerated-time soak with its own PRD addendum, fault injection). |
| **9** | **Remove demo GraphQL root fields `finance`/`hr`** (owner decision 2026-10-25, plan [`2026-10-25-plan-remove-demo-finance-hr-fields.md`](2026-10-25-plan-remove-demo-finance-hr-fields.md)) | S | **DONE** (2026-10-25) | Branch `fix/remove-demo-finance-hr-fields`; WP1 (single work package), Phase 5 approved (Low findings fixed); follow-ups: orphaned `LoadInvoiceItemsBatchAsync`/`InvoiceItemRecord`, `security.txt` GQL-02 entry. Phase 1 skipped (lean removal). |

---

## 1. 🚀 Plan Matrix (files in `docs/plans/`)

| Plan / Category Document | Topic & Scope | Status |
|---|---|:---:|
| [`2026-10-21-req-issue-271-security-audit.md`](2026-10-21-req-issue-271-security-audit.md) `REQ-SEC-271`<br/>Plan: [`2026-10-21-plan-issue-271-security-audit.md`](2026-10-21-plan-issue-271-security-audit.md) `PLAN-SEC-271` | Issue #271 security audit remediation (7 findings); see backlog rank 1 for the remainders. | **MERGED (#273, #278); REMAINDERS OPEN** |
| [`2026-10-22-plan-security-inf.md`](2026-10-22-plan-security-inf.md), [`2026-10-22-plan-security-misc.md`](2026-10-22-plan-security-misc.md), [`2026-10-22-plan-security-pers.md`](2026-10-22-plan-security-pers.md) | Security findings log area plans with open owner actions (OPS-01, `release` environment, PERS-04 residual, MISC follow-up PRD); findings log [`security.txt`](security.txt). | **MERGED (#287); OWNER ACTIONS OPEN** |
| [`2026-10-23-prd-issue-281-app-scoped-trusted-documents.md`](2026-10-23-prd-issue-281-app-scoped-trusted-documents.md) `REQ-TD-281`<br/>Plan: [`2026-10-23-plan-issue-281-app-scoped-trusted-documents.md`](2026-10-23-plan-issue-281-app-scoped-trusted-documents.md) | Issue #281 app-scoped, runtime-managed trusted GraphQL documents; SEC-281-6 ingress gap open. | **MERGED (#291); OWNER ITEM OPEN** |
| [`2026-10-23-prd-issue-282-catalog-register-sql-tables.md`](2026-10-23-prd-issue-282-catalog-register-sql-tables.md) `REQ-CAT-282`<br/>Plan: [`2026-10-23-plan-issue-282-catalog-register-sql-tables.md`](2026-10-23-plan-issue-282-catalog-register-sql-tables.md) | Issue #282 register SQL data source tables via dbt sync; SEC-282-5 residual and OQ-1 open. | **MERGED (#290); FOLLOW-UPS OPEN** |
| [`2026-10-23-req-nightly-parity-tests.md`](2026-10-23-req-nightly-parity-tests.md) `REQ-PARITY-NIGHTLY`<br/>Plan: [`2026-10-23-plan-nightly-parity-tests.md`](2026-10-23-plan-nightly-parity-tests.md) | Nightly functional-completeness (parity) suite; gap register (section 5) and PARITY-FU-* follow-ups. The plan file is read by `ParityIsolationArchitectureTests`, `PendingReasons` and `.github/scripts/parity-report.py`. | **MERGED (#292, #317, #331); FOLLOW-UPS OPEN** |
| [`2026-10-24-plan-issues-333-335-postgres-parity.md`](2026-10-24-plan-issues-333-335-postgres-parity.md) `REQ-PGP-01..08`, `SEC-PGP-01..07` | **Issues #333, #334, #335: PostgreSQL parity defects found by the nightly container job.** GraphQL time buckets on PostgreSQL and SQL Server now emit canonical `date_trunc` / `convert_timezone` SQL compiled by the AST engine (registry-canonical zone, closed unit enum incl. `quarter` for `date_trunc` only, SQL Server hour/ISO-week/quarter truncation without session-dependent constructs); zoned or non-temporal bucket columns and compiler/zone errors answer 4xx with fixed messages; planned-path bucket keys are invariant `yyyy-MM-ddTHH:mm:ss`. WebSQL writes values through `WebSqlValueWriter`: UTC ISO 8601 with `Z` (Unspecified treated as UTC), `DateOnly` as `yyyy-MM-dd`, everything else culture-invariant. | **IMPLEMENTED (Phase 5 approved; branch `fix/issues-333-335-postgres-parity`)**. Unit suite and TrinoSqlEngine tests local (no Docker); next: nightly PostgreSQL container cells and a SQL Server container cell (SEC-PGP-05) must pass before the issues are closed. |
| [`2026-10-16-req-postgres-wire-protocol-endpoint.md`](2026-10-16-req-postgres-wire-protocol-endpoint.md) `REQ-PG-WIRE-01` | BL-07 PostgreSQL wire protocol endpoint. | **PROPOSED** |
| [`implementation/00-implementation-roadmap.md`](implementation/00-implementation-roadmap.md), [`implementation/impl-security-wave-c-2.md`](implementation/impl-security-wave-c-2.md) | Cross-scope sequencing; deferred freeze windows; Security Wave C deployment remainders. | **DEFERRED / PARTIAL** |
| [`implementation/decision-g14-feat-scope.md`](implementation/decision-g14-feat-scope.md) | Decision record G-14: scope of the analytics work packages (#28, #37, #38). | **DECISION RECORD** |
| [`architecture.md`](architecture.md) `PLAN-ARCH-REVIEW` | Solution architecture remediation (R0–R6). | **CANONICAL REFERENCE** |
| [`features.md`](features.md) `PLAN-FEATURES-ROADMAP` | Analytics & governance feature roadmap. | **CANONICAL REFERENCE** |
| [`security.md`](security.md) `PLAN-SECURITY-REVIEW`, [`security.txt`](security.txt) | Security review, hardening & compliance; bug & security findings log. | **CANONICAL REFERENCE** |
| [`README.md`](README.md) `PLAN-AGENT-GOV-DOCS-01` | Agent governance & English documentation standards. | **CANONICAL REFERENCE** |

---

## 2. ✅ Completed plans (history in git)

Plan files of these items were deleted because their work is merged and nothing is open; `git log --diff-filter=D -- docs/plans/` restores them.

| Id | Title | Issue / PR |
|---|---|:---:|
| `REQ-SQL-277` / `PLAN-SQL-277` | Governed WebSQL `WITH RECURSIVE` (opt-in, bounded) | #277 / **#285** |
| `REQ-AGG-280` / `PLAN-AGG-280` | Aggregation `where` with values on SQL sources | #280 / **#284** |
| `REQ-279` / `PLAN-279` | Read-only tokens and GraphQL subscriptions; subscription switch | #279 / **#288** |
| `REQ-CONS-286` / `PLAN-CONS-286` | `CONSUMER_APP_*` tables in the baseline, no runtime DDL | #286 / **#289** |
| `PLAN-SEC-API`, `-EP`, `-EXT`, `-GOV`, `-GQL`, `-MCP`, `-SQLA`, `-SQLE`, `-VF` | Security findings log area plans (all in-scope HIGH/MEDIUM findings resolved, incl. SQLA-06 follow-up) | **#287** |
| `PLAN-PARITY-GAPS` | Nightly parity gap fixes PARITY-GAP-001..022 | #293–#314 / **#317** |
| `REQ-ROBIN-100` / Robin-100 addendum | Robin persona suite to 100 scenarios | **#331** |
| `REQ-ROBIN-GAPS` / `PLAN-ROBIN-GAPS` | Product fixes for the nightly parity gaps | #302, #318–#330 / **#331** |
| `REQ-GOV-270` / `PLAN-GOV-270` | GraphQL HMAC pseudonymization typing for non-string columns | #270 / **#272** |
| `REQ-OPS-257`, `REQ-DOC-256` / `PLAN-OPS-257-256` | PoC deployment blockers; dbt sources manifest ingestion | #256, #257 / **#259** |
| `REQ-PERF-254` / `PLAN-PERF-254` | Memory bounds, streaming, discovery caches | #254 / **#258** |
| `REQ-CAT-146` / `PLAN-CAT-146-BC` | Multilingual catalog texts and search | #146 / **#255** |
| `REQ-CAT-138` / `PLAN-CAT-138-INC2-5` | Catalog at scale, increments 2..5 | #138 / **#252** |
| `REQ-SCALE-5000-WAVE2` / `PLAN-SCALE-5000-WAVE2`, `PLAN-SCALE-5000` | BL-03 Wave 2: scale to 5,000 users | **#265** |

### Earlier merged tracks (v1.2.0–v1.2.11)

| Item | Plan id | PR | Summary |
|---|---|:---:|---|
| **#276** | n/a | **#276** | Token-efficient catalog retrieval for AI report & app generators (10k tables). |
| **#274** | n/a | **#274** | Predefined and dbt synthetic samples with unmasked preview in the catalog. |
| **#267 / #268** | n/a | **#267**, **#268** | Metamorphic policy tests and governance mutation hardening; metamorphic tests for catalog search and GraphQL complexity analysis. |
| **#269** | n/a | **#269** | Removed legacy symlinks and canonicalized `docs/plans` paths. |
| **BL-04** | `PLAN-DATA-BL04` | **#251** | Governed Table Cache: CDC/TTL/periodic caching with transparent federated join routing in DuckDB. |
| **BL-08** | `PLAN-SEC-BL08` | **#248** | Isolated C# DLL extension points in .NET 10 via cryptographically pinned `PluginAssemblyLoadContext`. |
| **#243** | `PLAN-INT-243` | **#246** | Cross-channel integration fixes (ReBAC cycle detection, OData `$select`, clamped consent diagnostic). |
| **BL-05** | `PLAN-OPS-BL05` | **#244** | Granular per-data-source configuration hierarchy with anti-escalation safety ceilings. |
| **#240** | `PLAN-GOV-240` | **#242** | Read-path switches for metrics (`Gateway:GraphQL:Metrics:Enabled`) & query analysis. |
| **#236 / #237 / #238** | `PLAN-GOV-236`, `PLAN-GOV-237`, `PLAN-DATA-238` | **#239** | Unified required filter column groups across all read paths; Databricks OBO Unity Catalog. |
| **#230** | `PLAN-SEC-230` | **#233** | SQL Server Windows Auth (NTLM) TLS Channel Binding (EPA / CBT) extraction. |
| **#220–#227** | `PLAN-GOV-220-227` | **#228** | Governance & federation refinements across 8 areas (usage, bulk streams, cost settlement, etc.). |
| **#75 / #112B** | `PLAN-DATA-75`, `PLAN-DATA-112B` | **#212** | Cross-source relation resolver wiring in GraphQL; time-bucketing aggregations in GraphQL. |
| **#210** | `PLAN-SEC-210` | **#214** | SQL Server Windows auth EPA diagnostics, datasource pre-flight fix, and v1.2 forward migration script. |
| **#39 / #29 / #175B** | `PLAN-GOV-39`, `PLAN-GOV-29`, `PLAN-GOV-175B` | **#209** | Pagination truncation signal, per-scope token bucket rate limiting, caller bucket caps in `/analyze`. |
| **#101** | `PLAN-DATA-101` | **#205** | Governed multi-watermark poller for CDC change tracking. |
