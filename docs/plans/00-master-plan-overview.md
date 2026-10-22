# Master Architecture & Implementation Backlog

**Last Updated:** October 22, 2026 · **Branch:** `main` (v1.2.11)  
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

---

## 0. 🎯 Active & Prioritized Backlog

**Rule (owner):** Security-critical items and bugs first, then urgently missing features, only then larger add-ons. Work is pulled strictly top-down; every item passes the 5-phase standard implementation lifecycle (Product Manager qualification $\to$ Solution Architect plan with Gemini 4 Pro $\to$ Security Expert audit $\to$ C# TDD Developer execution $\to$ Code Reviewer inspection). There is no documentation phase in `docs/` (owner rule 2026-10-17); all progress is tracked exclusively here in `docs/plans/00-master-plan-overview.md`.

### Active Tracks

| Rank | Item | Size | Status | Next Milestone / Notes |
|---:|---|:---:|:---:|---|
| **1** | **Security review follow-up to #271** (branch `fix/security-review-open-findings`, PR #278; parallel streams `fix/security-review-ext`, `fix/security-review-gql`, `fix/security-review-inf`: four-eyes on admin write paths, tenant-scoped docs, TOTP/ext_authz hardening, epoch durability, BulkStream signing key, REST doc tenant scoping, Casbin multi-value claims; see [`REQ-SEC-271`](2026-10-21-req-issue-271-security-audit.md)) | M | **COMPLETED & VERIFIED** ✅ | All 4 streams compiled (0 warnings), unit & architecture suites 100% green. GitHub Actions CI Build & Test (including 4 integration shards, Spark, Compiler Gate, DevSwitch), CodeQL SAST, and Trivy scans fully green on PR #278. |
| **2** | **BL-07:** PostgreSQL wire protocol endpoint (native pgwire protocol support for PostgreSQL BI tools) ([`REQ-PG-WIRE-01`](2026-10-16-req-postgres-wire-protocol-endpoint.md)) | L | **PROPOSED** | Conditional go pending owner evaluation. |
| **3** | **WP-ARC-C-19:** Unit test project physical separation | S | **PLANNED** | Scheduled for next release freeze window. |

---

### Recently Merged & Released Tracks (v1.2.0–v1.2.11)

| Item | Document | PR | Summary |
|---|---|:---:|---|
| **#276** | n/a | **#276** | Token-efficient catalog retrieval for AI report & app generators (10k tables). |
| **#274** | n/a | **#274** | Predefined and dbt synthetic samples with unmasked preview in the catalog. |
| **#271** | [`PLAN-SEC-271`](2026-10-21-plan-issue-271-security-audit.md)<br/>[`REQ-SEC-271`](2026-10-21-req-issue-271-security-audit.md) | **#273** | Security audit remediation (7 findings): fork-PR guard in `docker-publish.yml`, category-aware audit purge threshold, four-eyes fail-closed `GrantPolicy`, tenant route guard, MCP doc/procedure/lineage isolation, TOTP enrollment guard and SSRF-hardened OpenAPI client, metric parenthesis guard and `information_schema.columns` deny filtering. Follow-up gaps in the active track above. |
| **#267 / #268** | n/a | **#267**, **#268** | Metamorphic policy tests and governance mutation hardening; metamorphic tests for catalog search and GraphQL complexity analysis. |
| **#269** | n/a | **#269** | Removed legacy symlinks and canonicalized `docs/plans` paths. |
| **#270** | `PLAN-GOV-270`<br/>`REQ-GOV-270` | **#272** | GraphQL HMAC Pseudonymization Typing & Cross-Channel Schema Parity for Non-String Columns. Batch rule hydration across engines, automatic String projection, HotChocolate schema invalidation. |
| **BL-03 Wave 2** | `PLAN-SCALE-5000-WAVE2`<br/>`REQ-SCALE-5000-WAVE2` | **#265** | Scale to 5,000 users: Multi-issuer Identity Realm Registry, Role-Resolver with SCIM-store/Graph/AppRoles, SCIM 2.0 receiver, sub-second deprovisioning lock, functional-rights mapping, scoped access policies, and recertification campaigns. |
| **#257** | `PLAN-OPS-257-256` | **#259** | PoC deployment blockers remediation (Serilog guard resiliency, release pipeline tagging/signing, diagnostic clarity in TableAccessPolicy, and schema 80->83 alignment). |
| **#256** | `PLAN-OPS-257-256` | **#259** | dbt sources manifest ingestion documentation & end-to-end test suite. |
| **#254** | `PLAN-PERF-254` | **#258** | Memory bounds, container limits, row-by-row query result streaming, metadata BitSet cache, HMAC parity. |
| **#146 Steps B & C** | `PLAN-CAT-146-BC` | **#255** | Multilingual catalog texts, RFC 4647 language negotiation, and diacritic folding search. |
| **#138 INC-2..5** | `PLAN-CAT-138-INC2-5` | **#252** | Catalog at scale: `CATALOG_STATE` versioning, immutable `CatalogSnapshot`, pre-filter bitset ranking, bulk relations, and honest embeddings (`None`, `LocalOnnx`, `Ollama`). |
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

---

## 1. 🚀 Active Execution Tracks & Plan Matrix

| Plan / Category Document | Topic & Scope | Open Requirements & Components | Status | Next Milestone |
|---|---|---|:---:|---|
| [`2026-10-21-req-issue-271-security-audit.md`](2026-10-21-req-issue-271-security-audit.md)<br/>`REQ-SEC-271`<br/>Plan: [`2026-10-21-plan-issue-271-security-audit.md`](2026-10-21-plan-issue-271-security-audit.md)<br/>`PLAN-SEC-271` | **Issue #271: Security Audit Remediation (7 Findings)** | Hardening across 7 areas: 01 (CI/CD supply-chain fork-PR trigger guard in `docker-publish.yml`), 02 (Audit retention honouring category-specific `SecurityDays` in `AuditRetentionPurgeWorker`), 03 (Four-eyes bypass remediation in `GrantPolicy` & dbt imports), 04 (Tenant route `{tenantId}` validation filter), 05 (MCP gateway tenant doc isolation, 4-eyes gate for procedures/metrics, lineage filtering), 06 (TOTP step-up & hardened SSRF client for OpenAPI ingestion), 07 (SQL AST optimization fence, metric parenthesis guard, actor HMAC secret). | **COMPLETED & VERIFIED** ✅ | PR #278 + parallel streams verified green in CI and locally across all suites. |
| [`2026-10-16-req-postgres-wire-protocol-endpoint.md`](2026-10-16-req-postgres-wire-protocol-endpoint.md)<br/>`REQ-PG-WIRE-01` | **BL-07: PostgreSQL Wire Protocol Endpoint** | Native PostgreSQL wire protocol listener allowing direct connectivity from PostgreSQL-compatible BI tools (Tableau, PowerBI, DBeaver) through Autheris's Zero-Trust governance engine. | **PROPOSED** | Owner decision. |
| [`architecture.md`](architecture.md)<br/>`PLAN-ARCH-REVIEW` | **Solution Architecture Remediation (R0–R6)** | Core architecture reference, clean layering, DI lifetime scoping, AST compilation, and streaming pipeline standards. | **CANONICAL REFERENCE** | Ongoing enforcement. |
| [`features.md`](features.md)<br/>`PLAN-FEATURES-ROADMAP` | **Analytics & Governance Feature Roadmap** | Feature index for governed aggregations, differential privacy, dbt semantic layer, CDC streams, query analysis, and data masking. | **CANONICAL REFERENCE** | Feature reference. |
| [`security.md`](security.md)<br/>`PLAN-SECURITY-REVIEW` | **Security Review, Hardening & Compliance** | Zero-Trust principles, fail-closed Casbin ABAC evaluation, AST pushdown guardrails, and cryptographic WORM audit chains. | **CANONICAL REFERENCE** | Ongoing security gate. |
| [`README.md`](README.md)<br/>`PLAN-AGENT-GOV-DOCS-01` | **Agent Governance & English Documentation Standards** | Project-wide English documentation policy; `docs/plans/` as the sole plan repository; 5-phase implementation lifecycle rules; strict ban on progress notes in `docs/`. | **CANONICAL REFERENCE** | Ongoing enforcement. |
