# Gesamtübersicht der Architektur- & Implementierungspläne

**Stand:** 09.10.2026 · **Zweig:** `feat/ast-target-dialect-generator`  
**Rolle:** C# & .NET Solution Architect  
**Ziel:** Strukturierte Übersicht und Ausführungsgraph des aktiven Backlogs in `docs/plans/`. Abgeschlossene Pläne (1–7) wurden nach erfolgreicher Implementierung und Verifikation bereinigt.

---

## 1. Aktive Implementierungspläne

| Plan / Dokument | Thema / Feature | Behandelte Befunde & Anforderungen | Status |
|---|---|---|---|
| **[Plan 8: Vollständige Daten-API & MCP-Bereitstellung](plan-vollstaendige-daten-api-und-mcp-bereitstellung.md)** | Governed REST Data API (`/api/v1/data/*`), Discovery & MCP-Ökosystem (Tools, Resources, Prompts) | **R-54 bis R-66, PoC Citizen Dev & MCP** | Neu erstellt – bereit zum Review ⏳ |

---

## 2. Historie: Erfolgreich umgesetzte & verifizierte Pläne ✅

Alle vorherigen Pläne wurden vollständig umgesetzt, bereinigt und durch **3.761 Unit-Tests**, **13 Architektur-Tests** und **1.628 Engine-/Extensions-Tests** mit **0 Fehlern und 0 Warnungen** verifiziert:

- **Plan 1 (Distributed State & Invalidation):** AR-01 bis AR-04, AR-12. Epoch-Keyed Caching, generation pull-validation, atomare Redis Lua-Scripts für DP-Budget & FinOps, lock-freier Casbin-Matcher.
- **Plan 2 (God-Classes Refactoring & Modularisierung):** AR-05, AR-06, AR-07, AR-11, AR-18. DI-Modularisierung (`AddAutheris*`), `GovernedSqlRewriter`/`Executor` ($\le 800$ Zeilen), typsichere `TableIdentifier`.
- **Plan 3 (Performance, Caching & Dialekte):** AR-08 bis AR-10, AR-13 bis AR-15, AR-19. Bounded LRU Plan-Cache, Policy-Fingerprinting, `SqlDialectMapper`, SQLite-Produktionswarnung.
- **Plan 4 (Supply Chain & CI-Härtung):** SC-01 bis SC-18. Trivy-Scanning, Cosign Keyless-Signing, Syft-SBOM, Non-Root-User `10001:10001`, Seccomp, Read-Only RootFS.
- **Plan 5 (PoC Arrow/OLAP & MCP Staging):** Befunde 3.1 & 3.2. ReBAC-Hierarchie (`schema:`, `domain:`), Fail-Closed Evaluator, RFC-9728 Discovery, Dev-CORS, Batch-Rejection.
- **Plan 6 (Lückenloses Zugriffs-Audit):** Lücken L-1 bis L-9. `AccessAuditMiddleware`, 100% Endpoint-Audit, `AuditDetailsBuilder` PII-Maskierung, `AuthFailureAggregator`.
- **Plan 7 (WebSQL Heterogene API Federation & Joins):** `CrossSourceQueryRouter`, `CrossSourcePlanner`, `FederatedDuckDbExecutionService`, Pre-Staging Masking vor DuckDB-Ingest.
- **Frühere Kernfunktionen:** dbt-Governance (B-01..06, R-50..51), erweiterte Maskierung (R-53, R-25), Klartext je Person (R-52), KMS-Audit (AU-01..19), Security Review Phasen 1–3 (SG-01..39).

---

## 3. Ausführungsgraph des verbleibenden Backlogs

```mermaid
flowchart TD
    subgraph Baseline["Verifizierte Basis (Pläne 1 bis 7 ✅)"]
        CORE["Autheris Core Platform<br/>(State · DI-Module · Plan-Cache · CI · Audit · WebSQL Joins)"]
    end

    subgraph Active["Aktiver Plan (Plan 8)"]
        REST_DATA["Säule 1: Governed REST Data API<br/>(/api/v1/data/{domain}/{table} · Streaming JSON)"]
        CATALOG_API["Säule 1b: Catalog & Discovery API<br/>(/api/v1/catalog/* · /api/v1/governance/me/access)"]
        MCP_TOOLS["Säule 2a: Erweiterte MCP Tools<br/>(query_sql · query_dataset · search_catalog · get_my_permissions)"]
        MCP_RESOURCES["Säule 2b: MCP Resources & Prompts<br/>(autheris://catalog/* · autheris://governance/*)"]
        MCP_ADMIN["Säule 2c: Admin MCP Tools<br/>(R-54..64: Swagger-Ingestion · Access Plans · Human-in-the-Loop)"]
    end

    CORE --> REST_DATA
    CORE --> CATALOG_API
    REST_DATA --> MCP_TOOLS
    CATALOG_API --> MCP_RESOURCES
    CORE --> MCP_ADMIN
```

---

## 4. Quelltexte und Referenzdokumente

- [Plan 8: Vollständige Daten-API & MCP-Bereitstellung](plan-vollstaendige-daten-api-und-mcp-bereitstellung.md)
- [Feature Request: Admin-Datenquellen & MCP (R-54 bis R-66)](2026-10-09-feature-request-admin-datenquellen-und-mcp.md)
