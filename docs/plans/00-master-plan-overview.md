# Master Architecture & Implementation Backlog

**Last Updated:** October 20, 2026 · **Branch:** `main` (v1.2.0)  
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
| **1** | **BL-07:** PostgreSQL wire protocol endpoint (native pgwire protocol support for PostgreSQL BI tools) ([`REQ-PG-WIRE-01`](2026-10-16-req-postgres-wire-protocol-endpoint.md)) | L | **PROPOSED** | Conditional go pending owner evaluation. |
| **2** | **WP-ARC-C-19:** Unit test project physical separation | S | **PLANNED** | Scheduled for next release freeze window. |

---

### Recently Merged & Released Tracks (v1.2.0–v1.2.7)

| Item | Document | PR | Summary |
|---|---|:---:|---|
| **BL-03 Wave 2** | [`PLAN-SCALE-5000-WAVE2`](2026-10-20-plan-scale-5000-users-wave2.md)<br/>[`REQ-SCALE-5000-WAVE2`](2026-10-20-req-scale-5000-users-wave2.md) | **#265** | Scale to 5,000 users: Multi-issuer Identity Realm Registry, Role-Resolver with SCIM-store/Graph/AppRoles, SCIM 2.0 receiver, sub-second deprovisioning lock, functional-rights mapping, scoped access policies, and recertification campaigns. |
| **#257** | [`PLAN-OPS-257-256`](2026-10-20-plan-issues-256-257-poc-blockers-and-dbt-sources.md) | **#259** | PoC deployment blockers remediation (Serilog guard resiliency, release pipeline tagging/signing, diagnostic clarity in TableAccessPolicy, and schema 80->83 alignment). |
| **#256** | [`PLAN-OPS-257-256`](2026-10-20-plan-issues-256-257-poc-blockers-and-dbt-sources.md) | **#259** | dbt sources manifest ingestion documentation & end-to-end test suite. |
| **#254** | [`PLAN-PERF-254`](2026-10-20-plan-issue-254-memory-scale-catalog-performance.md) | **#258** | Memory bounds, container limits, row-by-row query result streaming, metadata BitSet cache, HMAC parity. |
| **#146 Steps B & C** | [`PLAN-CAT-146-BC`](2026-10-20-plan-issue-146-multilingual-catalog-texts.md) | **#255** | Multilingual catalog texts, RFC 4647 language negotiation, and diacritic folding search. |
| **#138 INC-2..5** | [`PLAN-CAT-138-INC2-5`](2026-10-20-plan-issue-138-catalog-at-scale-inc2-5.md) | **#252** | Catalog at scale: `CATALOG_STATE` versioning, immutable `CatalogSnapshot`, pre-filter bitset ranking, bulk relations, and honest embeddings (`None`, `LocalOnnx`, `Ollama`). |
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
| [`2026-10-20-req-issue-257-poc-deployment-blockers.md`](2026-10-20-req-issue-257-poc-deployment-blockers.md)<br/>`REQ-OPS-257`<br/>Plan: [`2026-10-20-plan-issues-256-257-poc-blockers-and-dbt-sources.md`](2026-10-20-plan-issues-256-257-poc-blockers-and-dbt-sources.md)<br/>`PLAN-OPS-257-256` | **Issue #257: PoC Deployment Blockers Remediation** | Serilog guard resiliency (`AUTHERIS_ALLOW_LEGACY_CONFIG`), release pipeline GHCR semantic version tagging (`ghcr.io/themulle/autheris:vX.Y.Z`) & Cosign keyless signatures alongside `main`, explicit diagnostic clarity in `TableAccessPolicy` (`ReBAC: Allowed, Consent: Denied`), and governance database schema evolution (80->83 elements) via `--migrate-governance`. | **MERGED & VERIFIED ✅** | Merged into `main` via PR #259 (`c6ed9070`). |
| [`2026-10-20-req-issue-256-dbt-sources-manifest-ingestion.md`](2026-10-20-req-issue-256-dbt-sources-manifest-ingestion.md)<br/>`REQ-DOC-256`<br/>Plan: [`2026-10-20-plan-issues-256-257-poc-blockers-and-dbt-sources.md`](2026-10-20-plan-issues-256-257-poc-blockers-and-dbt-sources.md)<br/>`PLAN-OPS-257-256` | **Issue #256: dbt Sources Manifest Ingestion Documentation & End-to-End Test Suite** | Documentation of top-level `sources` ingestion (materialization `source`, attribute mappings for name, database, schema, description, columns, tags, meta, constraints) in `docs/extensions/dbt-integration-guide.md`; end-to-end unit test suite in `tests/Autheris.Tests.Unit/` verifying sources-only manifest ingestion and proposal generation. | **MERGED & VERIFIED ✅** | Merged into `main` via PR #259 (`c6ed9070`). |
| [`2026-10-20-req-issue-254-memory-scale-catalog-performance.md`](2026-10-20-req-issue-254-memory-scale-catalog-performance.md)<br/>`REQ-PERF-254`<br/>Plan: [`2026-10-20-plan-issue-254-memory-scale-catalog-performance.md`](2026-10-20-plan-issue-254-memory-scale-catalog-performance.md)<br/>`PLAN-PERF-254` | **Issue #254: Memory Growth, Scale Catalog Limits, Discovery Caches, Streaming & Channel Consistency** | Embedded Garnet memory limits (`MemorySizeMb`, cgroup derivation) & .NET GC heap hard limits; row-by-row query result streaming; LRU eviction & sliding expiration for user metadata; OData `$metadata` & OpenAPI index permission-set caching; Backstage `catalog-info.yaml` HTTP 304 ETag validation; startup pre-warming for data sources & discovery; ERP GraphQL schema partitioning; cross-channel HMAC hash string parity; standard `Retry-After` header on 429 concurrency throttles; epoch rollback warning dedup on empty cache bootstrap. | **MERGED & VERIFIED ✅** | Merged into `main` via PR #258 (`541f4053`). |
| [`2026-10-17-req-issue-146-multilingual-catalog-texts.md`](2026-10-17-req-issue-146-multilingual-catalog-texts.md)<br/>`REQ-CAT-146`<br/>Plan: [`2026-10-20-plan-issue-146-multilingual-catalog-texts.md`](2026-10-20-plan-issue-146-multilingual-catalog-texts.md)<br/>`PLAN-CAT-146-BC` | **Issue #146 Steps B & C: Multilingual Catalog Texts, Language Negotiation & Multilingual Search** | Domain model `LocalizedTexts` & `LocalizedTextEntry`; baseline DDL `texts_json` on `TABLES` and `TABLE_COLUMNS` across SQLite, PostgreSQL, SQL Server + shape snapshots; RFC 4647 language negotiation (`ILanguageNegotiator`) across MCP, REST (`Content-Language`, `Vary`), and GraphQL; multilingual search indexing with `DiacriticNormalizer` (German umlauts & Unicode Form D accent folding); fail-closed zero-trust visibility invariance (`CatalogVisibleBitSet`). | **MERGED & VERIFIED ✅** | Merged into `main` via PR #255 (`038b7ece`). |
| [`2026-10-20-plan-issue-138-catalog-at-scale-inc2-5.md`](2026-10-20-plan-issue-138-catalog-at-scale-inc2-5.md)<br/>`PLAN-CAT-138-INC2-5`<br/>PRD: [`2026-10-17-req-issue-138-catalog-at-scale.md`](2026-10-17-req-issue-138-catalog-at-scale.md) | **Issue #138 Increments 2..5: Catalog at Scale** | `CATALOG_STATE` versioning counter (`structure_version`, `content_version`), change events, immutable `CatalogSnapshot`, BitSet pre-filter ranking, bulk relations (`GetAllRelationsAsync`), and honest embedding providers (`None`, `LocalOnnx`, `Ollama`). | **COMPLETED & VERIFIED ✅** | Merged into `main` via PR #252 (`d9af80b5`). |
| [`2026-10-20-req-scale-5000-users-wave2.md`](2026-10-20-req-scale-5000-users-wave2.md)<br/>`REQ-SCALE-5000-WAVE2`<br/>Plan: [`2026-10-20-plan-scale-5000-users-wave2.md`](2026-10-20-plan-scale-5000-users-wave2.md)<br/>`PLAN-SCALE-5000-WAVE2`<br/>Base: [`2026-10-16-plan-scale-5000-users.md`](2026-10-16-plan-scale-5000-users.md)<br/>`PLAN-SCALE-5000`<br/>[`2026-10-16-analysis-scale-5000-users-governance.md`](2026-10-16-analysis-scale-5000-users-governance.md)<br/>[`2026-10-16-analysis-scale-5000-users-performance.md`](2026-10-16-analysis-scale-5000-users-performance.md) | **BL-03 Wave 2: Scale to 5,000 Users (Identity Foundation & Structural Access Governance)** | 6 Wave 2 core packages: WP-SC-13 (Identity Realm Registry & dynamic multi-issuer JWT routing), WP-SC-05 (Modular Role-Resolver & SCIM-store/Graph/AppRole providers), WP-SC-07 (SCIM 2.0 receiver, dedicated auth, and sub-second deprovisioning lock), WP-SC-14 (Functional-Rights Mapping with four-eyes approvals), WP-SC-06 (Scoped Access Policies with tag/pattern selectors for group/role grantees), WP-SC-08 (Risk-based recertification campaigns 90d/365d with auto-revoke). | **MERGED & VERIFIED ✅** | Merged into `main` via PR #265 (`83738b8c`). |
| [`2026-10-16-req-postgres-wire-protocol-endpoint.md`](2026-10-16-req-postgres-wire-protocol-endpoint.md)<br/>`REQ-PG-WIRE-01` | **BL-07: PostgreSQL Wire Protocol Endpoint** | Native PostgreSQL wire protocol listener allowing direct connectivity from PostgreSQL-compatible BI tools (Tableau, PowerBI, DBeaver) through Autheris's Zero-Trust governance engine. | **PROPOSED** | Owner decision. |
| [`architecture.md`](architecture.md)<br/>`PLAN-ARCH-REVIEW` | **Solution Architecture Remediation (R0–R6)** | Core architecture reference, clean layering, DI lifetime scoping, AST compilation, and streaming pipeline standards. | **CANONICAL REFERENCE** | Ongoing enforcement. |
| [`features.md`](features.md)<br/>`PLAN-FEATURES-ROADMAP` | **Analytics & Governance Feature Roadmap** | Feature index for governed aggregations, differential privacy, dbt semantic layer, CDC streams, query analysis, and data masking. | **CANONICAL REFERENCE** | Feature reference. |
| [`security.md`](security.md)<br/>`PLAN-SECURITY-REVIEW` | **Security Review, Hardening & Compliance** | Zero-Trust principles, fail-closed Casbin ABAC evaluation, AST pushdown guardrails, and cryptographic WORM audit chains. | **CANONICAL REFERENCE** | Ongoing security gate. |
| [`README.md`](README.md)<br/>`PLAN-AGENT-GOV-DOCS-01` | **Agent Governance & English Documentation Standards** | Project-wide English documentation policy; `docs/plans/` as the sole plan repository; 5-phase implementation lifecycle rules; strict ban on progress notes in `docs/`. | **CANONICAL REFERENCE** | Ongoing enforcement. |
