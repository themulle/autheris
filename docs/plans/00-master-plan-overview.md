# Master Architecture & Implementation Backlog

**Last Updated:** October 10, 2026 · **Branch:** `feat/ast-target-dialect-generator`  
**Role:** Solution Architect & Agent Coordinator  
**Location:** `doc/plan/00-master-plan-overview.md` (or `docs/plans/00-master-plan-overview.md`)  
**Purpose:** Canonical origin plan, dependency graph, and active track status for all AI agents and engineers working on the Autheris ecosystem.

---

## 🧭 Origin Plan Protocol for AI Agents

> [!IMPORTANT]
> **Directive for all AI agents and contributors:**
> 1. **Language:** All plans, requirements, and documentation must be written in **English** (aligned with [README.md](file:///root/autheris/README.md)).
> 2. **Repository of Requirements:** All new requirements, design documents, and feature requests must be stored in [`doc/plan/`](file:///root/autheris/doc/plan/).
> 3. **Refinement on the Origin Plan:** When refining an existing feature or adding a new requirement, agents must:
>    - Create or update the detailed plan in `doc/plan/` (e.g., `YYYY-MM-DD-plan-<topic>.md`).
>    - Update the execution matrix and status below in this Origin Plan.
>    - Maintain traceable bidirectional links between parent and child documents.

---

## 1. 🚀 Active Execution Tracks & Plan Matrix

| Plan / Document ID | Topic & Scope | Addressed Requirements & Components | Status | Next Milestone |
|---|---|---|---|---|
| [`2026-10-10-konzept-federated-virtual-filter-web-api-duckdb.md`](file:///root/autheris/doc/plan/2026-10-10-konzept-federated-virtual-filter-web-api-duckdb.md)<br/>`PLAN-FEDERATED-VF-DUCKDB-15` | **Federated Virtual Filters on Web-APIs via DuckDB** | 3-stage adaptive federation (Short-circuit, Key-set pushdown, DuckDB staging); dynamic regex matching; composite key joins in `VirtualFilterStore` | **COMPLETED & VERIFIED ✅** | Documented in `docs/features/f-gov-14-federated-virtual-filters.md` & verified with unit/integration test suite |
| [`2026-10-10-konzept-datenklassifizierung-ki-vorklassifizierung.md`](file:///root/autheris/doc/plan/2026-10-10-konzept-datenklassifizierung-ki-vorklassifizierung.md)<br/>`PLAN-GOV-KI-13` | **Data Object Classification & AI Pre-Classification** | Fail-Closed `UNCLASSIFIED` initial state; optional AI pre-classification with 150ms timeout guard; auto-preclassify at confidence $\ge 95\%$; dual-sign-off workflows | **IMPLEMENTED / IN REVIEW 🛡️** | Finalize UI workflow endpoints and telemetry hooks |
| [`2026-10-10-implementierungsplan-offline-doc-mcp-gateway.md`](file:///root/autheris/doc/plan/2026-10-10-implementierungsplan-offline-doc-mcp-gateway.md)<br/>`PLAN-OFFLINE-DOC-MCP-12` | **Offline Doc- & Runbook-MCP Gateway** | Ingestion pipeline for Markdown, arc42, Runbooks, OpenAPI, Error Catalogs; local BM25/SIMD hybrid search; `GatewayMcpServer` stdio/SSE bridge | **IN PROGRESS / DRAFT 🛡️** | Implement BM25 SIMD vector tokenizer and document deposit endpoint |
| [`2026-10-10-konzept-universelle-dokumentenablage-mcp.md`](file:///root/autheris/doc/plan/2026-10-10-konzept-universelle-dokumentenablage-mcp.md)<br/>`PLAN-UNIVERSAL-DOC-STORE-14` | **Universal Document Store for Multi-App MCP** | 4 deposit options (GitOps, S3/MinIO, Ingestion API/MCP-Deposit, Shared Volume); 100% Air-Gapped/Offline support; Multi-App catalog & search | **CONCEPT COMPLETE 📚** | Draft concrete storage abstraction interfaces in `Autheris.Application` |
| [`README.md`](file:///root/autheris/doc/plan/README.md)<br/>`PLAN-AGENT-GOV-DOCS-01` | **Agent Governance & English Documentation Standards** | Project-wide English documentation policy; central `doc/plan/` repository; agent refinement lifecycle rules; root `AGENTS.md` & `CLAUDE.md` | **ACTIVE & ADOPTED ✅** | Ongoing enforcement across all sub-agent and tool invocations |

---

## 2. 🗺️ Dependency & Refinement Graph

```mermaid
flowchart TD
    MP["00-master-plan-overview.md<br/>(Master Origin Plan)"]
    
    subgraph Governance ["Data & Agent Governance"]
        AG["PLAN-AGENT-GOV-DOCS-01<br/>Agent Governance & English Docs"]
        DK["PLAN-GOV-KI-13<br/>Data Classification & AI Pre-Classification"]
    end
    
    subgraph ExecutionEngine ["Federation & Virtual Filters"]
        VF["PLAN-FEDERATED-VF-DUCKDB-15<br/>Federated Virtual Filters (Web-API / DuckDB)"]
    end
    
    subgraph DocMCP ["MCP Documentation Ecosystem"]
        DM["PLAN-OFFLINE-DOC-MCP-12<br/>Offline Doc- & Runbook-MCP Gateway"]
        UDS["PLAN-UNIVERSAL-DOC-STORE-14<br/>Universal Multi-App Document Store"]
    end

    MP --> AG
    MP --> DK
    MP --> VF
    MP --> DM
    MP --> UDS

    DM <--> UDS
    DK -.-> VF
```

---

## 3. 📋 Historical Completed Tracks (Archive)

> [!NOTE]
> All prior foundation tracks (Plans 1 through 11, SQL-AST Hardening, and Tracks A through E) were 100% completed, verified through 5,600+ automated unit & integration tests, and archived in accordance with repository clean-up guidelines.
