# Master Architecture & Implementation Backlog

**Last Updated:** October 15, 2026 · **Branch:** `feat/impl-wave-5-6`  
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

## 1. 🚀 Active Execution Tracks & Plan Matrix

| Plan / Category Document | Topic & Scope | Open Requirements & Components | Status | Next Milestone |
|---|---|---|:---:|---|
| [`implementation/00-implementation-roadmap.md`](implementation/00-implementation-roadmap.md)<br/>`IMPL-ROADMAP-2026-10-14` | **Cross-Scope Implementation Roadmap** | Architecture Waves 1–6 (`WP-ARC-A`, `WP-ARC-B`, `WP-ARC-C`), Analytics/Governance feature tracks (`WP-FEAT-01..07b`: #28, #37, #38, #75, #77, #79, #80), documentation alignment (`WP-DOC-01`: `f-data-04-arrow-flight-sql.md`, `f-dbt-07-dbt-semantic-layer-metrics.md`), and test suite speed-up (`WP-TS-00..06`) delivered and verified. Optional freeze-window test project split (`WP-ARC-C-19`). | **COMPLETED & VERIFIED ✅** | Optional freeze-window physical unit-test project split (`WP-ARC-C-19`). |
| [`security.md`](security.md)<br/>`PLAN-SECURITY-REVIEW` | **Security Review, Hardening & Compliance** | Waves A, B, C (`SR-H`, `SR-M`, `SR-L`), SEC2-08/09 remainders, and all 18 `accepted-differences.json` entries reviewed & approved. | **COMPLETED & VERIFIED ✅** | Maintain security regression & mutation gates. |
| [`architecture.md`](architecture.md)<br/>`PLAN-ARCH-REVIEW` | **Solution Architecture Remediation (R0–R6)** | R0–R6 architecture waves implemented: cluster-wide CDC fan-out (`A-13`), WORM audit group commit & `AuditChainEngine` (`A-07`, `B-14`), `ISqlDialectProvider` & `ISqlDialectRegistry` (`B-09`), Application package diet (`B-10`), `GatewayOptions` split & `IValidateOptions<T>` (`C-01`, `C-02`), API middleware/ProblemDetails/policies/endpoints (`C-04..C-08`), god-class decomposition (`C-09`), lock-free template cache & non-reflection AST walk (`C-10`, `C-12`), true streaming core & egress (`C-13`, `C-14`), `ICatalogVersionProvider` & `ICatalogLookup` (`C-15`), `IRedisConnectionProvider` & unified `GatewayDiagnostics` (`C-16`), and test/build hygiene (`C-17`, `C-18`, `C-20..C-22`). | **COMPLETED & VERIFIED ✅** | Optional freeze-window test project split (`WP-ARC-C-19`). |
| [`features.md`](features.md)<br/>`PLAN-FEATURES-ROADMAP` | **Analytics & Governance Feature Roadmap** | #28 governed aggregations (`GovernedAggregationService`, `F-DATA-08`), #37 minimum group size & DP noise/caching (`IDifferentialPrivacyEngine`, `F-DATA-08`), #38 dbt Semantic Layer Stage 2/3 (`SemanticMetricQueryService`, MCP `list_metrics`/`describe_metric`/`query_metrics`, GraphQL `queryMetrics`, `semantic.<model>` views, `TrinoMetadataQueryHandler` for `/v1/statement`, `F-DBT-07`), #75 cross-source GraphQL relations (`CrossSourceRelationResolver`, `F-GOV-19`), #77 change detection `ETag` & `changesSince` cursor (`GovernedChangeDetectionService`, `F-DATA-09`), #79 static query analysis & bundled consents (`QueryAnalysisService`, `F-GOV-20`), #80 column value profiles & synthetic sample rows (`ColumnValueProfileService`, `SyntheticSampleRowGenerator`, `F-AI-17`). | **COMPLETED & VERIFIED ✅** | Documented in `docs/features/` (`F-DATA-04`, `F-DATA-08`, `F-DATA-09`, `F-DBT-07`, `F-GOV-19`, `F-GOV-20`, `F-AI-17`). |
| [`implementation/impl-test-suite-speedup.md`](implementation/impl-test-suite-speedup.md)<br/>`IMPL-TEST-SPEEDUP-2026-10-14` | **Test Suite Speed-Up** | `WP-TS-00` (TRX artifacts), `WP-TS-01` (shared DB servers), `WP-TS-02` (CI shards), `WP-TS-03` (parallel Spark job), `WP-TS-04` (Cecil module & call-graph cache), `WP-TS-05` (`SerialIntegrationCollection` isolating `ItsmIntegrationTests`), and `WP-TS-06` (parallel SQL compiler gate) delivered and verified. | **COMPLETED & VERIFIED ✅** | Refresh CI shard weights periodically. |
| [`2026-10-15-req-issue-94-upgrade-migration-and-pk-ordering.md`](2026-10-15-req-issue-94-upgrade-migration-and-pk-ordering.md)<br/>`REQ-GOV-94`<br/>Plan: [`2026-10-15-plan-issue-94-upgrade-migration-and-pk-ordering.md`](2026-10-15-plan-issue-94-upgrade-migration-and-pk-ordering.md) | **Bug #94 & #102: Schema Shape Verification, Declared Keys/Indexes & Key-Based Read Ordering** | Regression v1.1.8 → v1.1.9/v1.1.10. Owner decisions 2026-10-15: no upgrade/repair migrations; read-only, fail-closed shape verification before the version is recorded; declared composite primary keys and indexes persisted in the extended baseline (`0001` checksum unchanged), validated fail-closed; no source catalog queries for keys (least privilege); ordering PK → unique index → clear-text composite → 400; WebSQL 504 by design (problem details). | **COMPLETED ✅ – merged (PR #105)** | Phases 1–6 done on `fix/issue-94-upgrade-migration-pk`: streams A (DDL + shape verifier + least-privilege tests), B (definition + persistence + keys endpoint + ingestion entry points), C (ordering + consumers incl. GraphQL tree compiler, WebSQL 504 problem details). Feature docs: [F-GOV-21](../features/f-gov-21-declared-table-keys-and-read-ordering.md), [F-GOV-12 schema shape verification](../features/f-gov-12-multi-engine-governance-storage.md#schema-shape-verification). PR #105. SQL Server Windows Auth (#102) delivered separately (`F-AUTH-04`). |
| [`2026-10-15-req-issue-107-row-filter-key-set-cache.md`](2026-10-15-req-issue-107-row-filter-key-set-cache.md)<br/>`REQ-GOV-107`<br/>Plan: [`2026-10-15-plan-issue-107-row-filter-key-set-cache.md`](2026-10-15-plan-issue-107-row-filter-key-set-cache.md) | **Issue #107: Key-Set Cache Strategy for Correlated Row Filters** | Opt-in `KeySetCache` evaluation strategy for `SubqueryCorrelated` row filters: governed key query run once, keys cached per data source/tenant/filter hash/policy epochs, injected as `fk IN (@p...)`; settings per rule (`CONSENT_ROW_FILTERS`) and per object (`TABLE_ROW_FILTER_STRATEGIES`), global default and caps; precedence rule > object > global; default `Exists`; fallback to `EXISTS` (max keys, parameter budget, DENY, temporal); fail closed on key-query failure; baseline `0001` DDL extended in place. | **SECURITY REVIEWED 🔍** | Phase 2 plan `PLAN-GOV-107` done (substitution in `GovernancePolicyFactory` after admission on the compiler input only; key query derived from the registered EXISTS via new engine entry point `IPolicyKeyQueryCompiler` reusing `PolicySubqueryTenantRewriter`, `AstSecurityVisitor` untouched; cache key = SHA-256 over compiled key query + binds + tenant + authoritative epochs; work packages WP-A settings/persistence/admin, WP-B engine/substitution, WP-C runtime/cache/integration). Phase 3 done: approved with binding conditions `SEC-RF107-01..16` (principal independence + per-source `KeySetCacheAllowed` default false, authoritative epochs, polarity, lossless typing, READ COMMITTED key reads, concurrency limit). Next: Phase 4 TDD (`csharp-tdd-developer`, Sonnet). |
| [`2026-10-15-plan-sqlgate-cutover-and-accepted-differences.md`](2026-10-15-plan-sqlgate-cutover-and-accepted-differences.md)<br/>`PLAN-SQLGATE-CUTOVER-03` | **SqlGate Differences Sign-Off & Cutover** | All 18 triage entries (57 unique statements) in `accepted-differences.json` audited, documented with concrete security justifications, and signed off (`approved`). Cutover precondition verified. | **COMPLETED & VERIFIED ✅** | Maintain G4 differential gate regression protection. |
| [`2026-10-16-analysis-scale-5000-users-governance.md`](2026-10-16-analysis-scale-5000-users-governance.md)<br/>`ANALYSIS-SCALE-5000-GOV` | **Scale to 5,000 Users: Governability of the Access Model** | Architect analysis: per-table consents are the only grant (no scope/tag inheritance), `USER_ATTRIBUTE` row filters fail closed, access profiles in-memory on PostgreSQL/SQL Server, missing consent indexes, no JML/directory integration, per-consent recertification, no paged admin/entitlement queries. Proposed target model (scoped Access Policies, RBAC+ABAC, tag-based column policy) and work packages `WP-SC-01..12`; open-source assessment §6 (adopt Microsoft.Graph/S.DS.Protocols when unblocked; borrow Ranger/Unity Catalog model and MIT SCIM schemas; reject OPA/Cedar as runtime PDP, AGPL Permify). | **ANALYSED → see `PLAN-SCALE-5000`** | Consolidated in [`2026-10-16-plan-scale-5000-users.md`](2026-10-16-plan-scale-5000-users.md). |
| [`2026-10-16-analysis-scale-5000-users-performance.md`](2026-10-16-analysis-scale-5000-users-performance.md)<br/>`ANALYSIS-SCALE-5000-PERF` | **Scale to 5,000 Users: Runtime Performance & Scaling** | Performance analysis: cluster-wide serialized audit writes (B-1), epoch MGET CROSSSLOT on Redis Cluster (B-2), CDC cluster cap keyed per tenant (B-3), uncached DynamicExpresso `sub_rule` evaluation (~547 µs/op measured) and per-user Casbin decision keys (H-1), full-table consent misses (H-2), unbounded memos (H-3), ReBAC `HGETALL` (H-4), `GetAllTablesAsync` on request paths (H-5), Basic-auth PBKDF2/IP limiter (H-6); capacity estimate (~600 rps peak), k6 load-test plan, open-source alternatives (BitFaster.Caching, HybridCache/FusionCache, OpenFGA conditional). | **ANALYSED → see `PLAN-SCALE-5000`** | Consolidated in [`2026-10-16-plan-scale-5000-users.md`](2026-10-16-plan-scale-5000-users.md). |
| [`2026-10-16-plan-scale-5000-users.md`](2026-10-16-plan-scale-5000-users.md)<br/>`PLAN-SCALE-5000` | **Scale to 5,000 Users: Consolidated Implementation Plan (Phase 2 + Phase 3, rev. 3)** | Owner decisions 2026-10-16 (Entra ID identity-only tokens, SCIM per mode, 3-layer attributes with LDAPS/Entra App Roles, recertification 90/365 days + SCIM lock, OBO delegation) incl. corrections: **Q4 individual grants allowed but only time-bound** (`WP-SC-16` time-bound grant policy: mandatory expiry on all grant paths, class maxima e.g. 180 d regular / 90 d PII / 30 d unmasked / 7 d privileged / 72 h break-glass, renewal rules, read-time clamp of legacy permanent grants) and **delegation connector-agnostic** (Databricks first `WP-SC-15`, SQL Server second `WP-SC-17`: Entra OBO token / read-only `SESSION_CONTEXT` asserted identity / optional KCD spike, per-user pool partitions, connection lifetime ≤ token). Multi-issuer realms (workforce + customer OAuth). Conflicts C1–C8 resolved. Security `SEC-SC-01..25`. LOC: Wave 1 ≈ 3.9k prod / 5.0k test; Wave 2 ≈ 20.3k prod / 24.2k test. | **WAVE 1 IMPLEMENTED – PR pending (branch `feat/scale-5000-users`); Wave 2 awaiting owner re-evaluation (re-review needed for SC-13, SC-15/SC-17, SC-06, PF-13)** | Wave 1 delivered: consent indexes + indexed consent query (`WP-SC-01`), persisted access profiles with user/group/role assignments and `Unmasked` rules (`WP-SC-03`), time-bound individual grant policy on all grant paths with read-time clamp (`WP-SC-16`), atomic manifest apply without duplicate role consents (`WP-SC-02`), audit group commit with Tier-A ack after anchor (`WP-PF-04`), Cluster-safe epoch reads + 150 ms staleness budget (`WP-PF-01/-07`), compiled `sub_rule` delegates (`WP-PF-03`), bounded caches on BitFaster.Caching (`WP-PF-06`), shape-shared sharded decision cache with kill switch `ShareAcrossUsers` (`WP-PF-09`), Basic-auth cluster marker + post-auth IP limiting (`WP-PF-08`), paged `GET /api/v1/governance/grants` and `/audit` (`WP-SC-10`). Docs: [F-GOV-22](../features/f-gov-22-time-bound-individual-grants.md), F-GOV-11/12/15, F-AUDIT-01, F-SEC-06, F-OPS-02, F-AUTH-DX, configuration guide 2.21, runbook 4.ai–4.ak/9.4. Next: owner merges PR, then re-evaluates Wave 2. |
| [`2026-10-16-req-postgres-wire-protocol-endpoint.md`](2026-10-16-req-postgres-wire-protocol-endpoint.md)<br/>`REQ-PROTO-PGWIRE` | **PostgreSQL Wire Protocol Endpoint (PRD, Phase 1)** | Product-manager evaluation of pure C# simple-query server vs. Rust `pgwire` sidecar vs. native C# Extended Query implementation vs. existing Trino/Flight protocols; open-source alternatives and license (BSL 1.1) constraints; governed execution only, TLS + strong auth mandatory. | **PROPOSED** | Phase 0: validate BI clients on the existing Trino protocol; owner decision on PG-only clients. |
| [`README.md`](README.md)<br/>`PLAN-AGENT-GOV-DOCS-01` | **Agent Governance & English Documentation Standards** | Project-wide English documentation policy; `docs/plans/` as the plan repository; agent refinement lifecycle rules; root `AGENTS.md` & `CLAUDE.md`. | **COMPLETED & VERIFIED ✅** | Ongoing enforcement. |

---

## 2. 🗺️ Dependency & Refinement Graph

```mermaid
flowchart TD
    MP["00-master-plan-overview.md<br/>(Master Origin Plan)"]
    AG["README.md<br/>Agent Governance & English Docs"]
    IR["implementation/00-implementation-roadmap.md<br/>Waves 4-6"]

    subgraph Categories ["Category Hubs"]
        SR["security.md<br/>SR-D remainders, gate review"]
        AR["architecture.md<br/>R1 rest, R2-R6"]
        FT["features.md<br/>#28, #37, #38 Stage 2/3"]
    end

    TS["implementation/impl-test-suite-speedup.md"]
    B94["2026-10-15-req-issue-94-...md<br/>Bug #94 upgrade & PK ordering"]
    R107["2026-10-15-req-issue-107-...md<br/>#107 row filter key-set cache"]

    MP --> AG
    MP --> SR
    MP --> AR
    MP --> FT
    MP --> TS
    MP --> B94
    MP --> R107
    SR --> IR
    AR --> IR
    FT --> IR
```

---

## 3. 📋 Historical Completed Tracks (Archive)

> [!NOTE]
> Completed and delivered tracks are documented in [`docs/features/`](../features/README.md); completed plan and requirement documents are preserved in git history.

| Track ID / Feature ID | Topic & Delivered Scope | Documented Reference | Delivered Date |
|---|---|---|---|
| `IMPL-ROADMAP` Waves 1–3 | **Implementation Waves 1–3:** Security Wave C, R0 trust foundation, R1 table-read pipeline, versioned migrations, parser pool. | [`docs/features/README.md`](../features/README.md) | 2026-10-14 (PRs #78, #81, #82) |
| `PLAN-AST-DIALECT-GEN-16`<br/>`F-DIALECT-02` | **AST Target Dialect Compiler: Single-Path Cutover (X1)** | [`docs/features/f-dialect-02-ast-sql-compiler.md`](../features/f-dialect-02-ast-sql-compiler.md) | 2026-10-14 |
| `PLAN-ADMIN-DATASOURCES-MCP`<br/>`F-AI-16` / `F-GOV-16` | **OpenAPI Datasource Ingestion & MCP Admin Tools** | [`docs/features/f-api-08-catalog-discovery-swagger-ingestion.md`](../features/f-api-08-catalog-discovery-swagger-ingestion.md)<br/>[`docs/features/f-ai-13-admin-mcp-tools-two-phase-hitl.md`](../features/f-ai-13-admin-mcp-tools-two-phase-hitl.md) | 2026-10-12 |
| `PLAN-EXT-EVENTS-WEBHOOKS-01`<br/>`F-EXT-01` | **Generic Extensibility, CloudEvents & Webhook Subsystem** | [`docs/features/f-ext-01-generic-plugin-events-and-webhooks.md`](../features/f-ext-01-generic-plugin-events-and-webhooks.md) | 2026-10-13 |
| `PLAN-FEDERATED-VF-DUCKDB-15`<br/>`F-GOV-14` | **Federated Virtual Filters & Web API Pushdown (DuckDB)** | [`docs/features/f-gov-14-federated-virtual-filters.md`](../features/f-gov-14-federated-virtual-filters.md) | 2026-10-11 |
| `PLAN-OFFLINE-DOC-MCP-14`<br/>`F-AI-15` | **Offline Document Vault & MCP Ingestion Gateway** | [`docs/features/f-ai-15-offline-doc-runbook-mcp-gateway.md`](../features/f-ai-15-offline-doc-runbook-mcp-gateway.md) | 2026-10-10 |
| `F-GOV-11`..`18`, `F-SEC-05` | **Governance, Access Profiles, Zero-Trust Hardening & WORM Compliance** | [`docs/features/README.md`](../features/README.md) | 2026-10-12 |
