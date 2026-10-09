# Gesamtübersicht der Architektur- & Implementierungspläne

**Stand:** 09.10.2026 · **Zweig:** `feat/ast-target-dialect-generator`  
**Rolle:** C# & .NET Solution Architect  
**Ziel:** Strukturierte Übersicht und Ausführungsgraph des aktiven Backlogs in `docs/plans/`. Abgeschlossene Pläne (1–7, SQL-AST Härtung sowie Tracks A, B, C) wurden nach erfolgreicher Implementierung und Verifikation bereinigt.

---

## 1. Aktive Implementierungspläne (Noch OFFEN ⏳)

Basierend auf der Produktmanager-Gap-Analyse wurde der architektonische Implementierungsplan **Plan 9** erstellt und zur Umsetzung freigegeben:

| Plan / Dokument | Thema / Feature | Behandelte Anforderungen & Komponenten | Status |
|---|---|---|---|
| **[Plan 9: Restliche Lücken – Onboarding, Paginierung, UI & Jobs](2026-10-09-implementierungsplan-restliche-luecken-onboarding-pagination-ui-jobs.md)** | • **AP-9.1:** Verbindungstest für Datenquellen (`POST /api/v1/catalog/datasources/{id}/test`)<br>• **AP-9.2:** Multi-Page HTTP Staging Pagination Engine (`offset/limit`, `nextLink`, `cursor`)<br>• **AP-9.3:** 2FA & HitL Web Console im DevPortal (`/portal/2fa/enroll`, `/portal/approvals`)<br>• **AP-9.4:** Async Long-Running Query Job Engine (`/api/v1/jobs/query`, `GET /status`, `GET /result`) | **R-55, R-56, R-60, R-64 UX, F-DATA-05**<br>• `DatasourceTestingService.cs`<br>• `DeclarativeHttpDataSourceExecutor.cs`<br>• `DevPortalEndpoints.cs`<br>• `AsyncQueryJobManager.cs` | **OFFEN ⏳**<br>Bereit zur TDD-Umsetzung |

---

## 2. Historie: Erfolgreich umgesetzte & verifizierte Pläne ✅

Alle nachfolgenden Arbeitspakete wurden vollständig implementiert, durch automatisierte Tests verifiziert und aus dem aktiven Backlog bereinigt:

- **Plan 8 – Vollständige Daten-API & MCP-Bereitstellung (Tracks A bis E):** ✅
  - **Track A (Governed REST Data API):** `/api/v1/data/{domain}/{table}` mit Streaming, Paging, Maskierung und System-Tabellen (`governance.system.*`).
  - **Track B (Catalog & Discovery API & Swagger Ingestion):** `/api/v1/catalog/*`, Swagger 2.0 / OpenAPI Ingestion, Zero-Leakage Secrets, ReBAC Filterung.
  - **Track C (RFC 6238 TOTP 2FA Engine):** Enrollment, Replay-Schutz, Validierung für Microsoft Authenticator, Google Authenticator, 1Password.
  - **Track D (Hybrid MCP Tools & Resources):** High-Level Tools (`query_sql`, `query_dataset`, `search_catalog`, `get_my_permissions`, `list_datasources`, `get_data_lineage`, `describe_api`, `invoke_api`), native MCP Resources (`autheris://*`) und Prompts (`explore_dataset`, `audit_access_compliance`).
  - **Track E (Admin MCP Tools & Two-Phase Freigabe):** `admin_plan_access`, `admin_apply_access`, `admin_register_datasource`, `admin_set_dataset_state`, `admin_resolve_principal` mit TOTP 2FA Step-Up und WORM-Audit.
- **SQL-AST Keyword-Support & Fail-Loud Härtung (AP-1 bis AP-5):** ✅
  - Härtung gegen Silent Dropping bei `TABLESAMPLE`, `PIVOT`, `MATCH_RECOGNIZE` (`SqlAstBuilder.VisitSampledRelation`).
  - Modellierung und Codegenerierung von `FETCH ... ROWS WITH TIES` über alle Dialekte (SQL Server, Postgres, Oracle, Snowflake, DuckDB, SQLite).
  - Lexer-Regex-Erweiterung für alphanumerische Keywords (`UTF8`, `UTF16`, `UTF32`) in `SqlKeywords.cs`.
  - Case-Insensitive Snowflake Identifier Quoting (`"USER"`, `"ORDERS"`) gegen Keyword-Kollisionen.
  - Verifiziert durch **1.421 Tests mit 0 Fehlern** in `TrinoSqlEngine.Tests`.
- **Pläne 1 bis 7 (Autheris Core Platform):** ✅
  - Distributed State & Invalidation (Plan 1), Modularisierung (Plan 2), Plan-Cache & Dialekte (Plan 3), CI-Härtung (Plan 4), PoC Arrow/OLAP & ReBAC (Plan 5), Lückenloses Zugriffs-Audit (Plan 6), WebSQL heterogene Cross-Source Joins (Plan 7).

---

## 3. Architektur-Zustand des Gesamtsystems

```mermaid
flowchart TD
    subgraph Clients["Clients & Konsumenten"]
        AI_AGENT["KI-Agenten & MCP-Clients<br/>(Claude Desktop / Cursor / IDE)"]
        REST_CLIENT["REST / OpenAPI Clients<br/>(Citizen Dev / Talos / Web-UI)"]
        SQL_CLIENT["WebSQL / BI-Tools<br/>(Trino JDBC / DuckDB OLAP)"]
    end

    subgraph MCP_Layer["Model Context Protocol (MCP)"]
        MCP_SERVER["GatewayMcpServer (Official SDK)<br/>Streamable HTTP / Stateless"]
        MCP_TOOLS["Hybrid Tools: query_sql, query_dataset, describe_api, invoke_api<br/>Admin Tools: admin_plan_access, admin_apply_access"]
        MCP_RES["Native Resources: autheris://*<br/>Prompts: explore_dataset, audit_access_compliance"]
    end

    subgraph Governance_Layer["Autheris Security & Governance Core"]
        PDP["Policy Decision Point (PDP)<br/>ReBAC (Zanzibar) + ABAC + Consent Rules"]
        MASKING["Dynamic Masking & Row-Level Security"]
        TOTP_2FA["RFC 6238 TOTP 2FA Engine<br/>(MS Authenticator / 1Password / Google Auth)"]
        HITL["Two-Phase Confirmation (HitL)<br/>Plan-Diff -> TOTP Step-Up -> confirmationToken -> Apply"]
        WORM["WORM Tamper-Proof Audit Log<br/>(Zero Secret Leakage)"]
    end

    subgraph Execution_Layer["Federated Query & Execution Engine"]
        SQL_EXEC["GovernedSqlExecutionService<br/>(Fast AST Rewriter & Multi-Dialect Generator)"]
        DATA_API["GovernedDataQueryService<br/>(Streaming Data API / System Tables)"]
        CATALOG["CatalogDiscoveryService & Ingestion<br/>(Swagger 2.0 / OpenAPI 3.x / Zero-Leakage Vault)"]
    end

    AI_AGENT --> MCP_SERVER
    REST_CLIENT --> DATA_API
    REST_CLIENT --> CATALOG
    SQL_CLIENT --> SQL_EXEC

    MCP_SERVER --> MCP_TOOLS
    MCP_SERVER --> MCP_RES
    MCP_TOOLS --> PDP
    DATA_API --> PDP
    SQL_EXEC --> PDP

    PDP --> MASKING
    MCP_TOOLS --> HITL
    HITL --> TOTP_2FA
    HITL --> WORM
    MASKING --> SQL_EXEC
    MASKING --> DATA_API
```

---

## 4. Quelltexte und Referenzdokumente

- **Aktiver Implementierungsplan:** [Plan 9: Restliche Lücken – Onboarding, Paginierung, UI & Jobs](2026-10-09-implementierungsplan-restliche-luecken-onboarding-pagination-ui-jobs.md)
- **Historische Pläne (1–8, SQL-AST Härtung, Feature Requests):** Vollständig implementiert, verifiziert und in der Git-Historie archiviert.
