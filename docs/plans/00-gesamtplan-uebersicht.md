# Gesamtübersicht der Architektur- & Implementierungspläne

> [!IMPORTANT]
> **English Documentation Standard & Canonical Origin Plan:**
> In accordance with project policy, all plans and documentation are maintained in English.
> Please refer to the canonical master plan: 👉 **[`00-master-plan-overview.md`](file:///root/autheris/doc/plan/00-master-plan-overview.md)**.
> All AI agents must register, refine, and update their plans on `00-master-plan-overview.md`.

**Stand:** 10.10.2026 · **Zweig:** `feat/ast-target-dialect-generator`  
**Rolle:** C# & .NET Solution Architect  
**Ziel:** Strukturierte Übersicht und Ausführungsgraph des aktiven Backlogs in `docs/plans/`. Abgeschlossene Pläne (1–7, SQL-AST Härtung sowie Tracks A, B, C) wurden nach erfolgreicher Implementierung und Verifikation bereinigt.

---

## 1. Aktive Implementierungspläne (3 AKTIV 🚀)

| Plan / Dokument | Thema / Feature | Behandelte Anforderungen & Komponenten | Status |
|---|---|---|---|
| [2026-10-10-implementierungsplan-offline-doc-mcp-gateway.md](file:///root/autheris/docs/plans/2026-10-10-implementierungsplan-offline-doc-mcp-gateway.md) (`PLAN-OFFLINE-DOC-MCP-12`) | Offline Doc- & Runbook-MCP-Gateway (Autheris & Generische APIs) | Ingestion für Markdown, arc42, Runbooks, OpenAPI & Error-Catalogs; lokale BM25/SIMD-Hybrid-Suche; Integration in `GatewayMcpServer` & eigenständige Stdio-Bridge | **IN ARBEIT / DRAFT 🛡️** |
| [2026-10-10-konzept-datenklassifizierung-ki-vorklassifizierung.md](file:///root/autheris/docs/plans/2026-10-10-konzept-datenklassifizierung-ki-vorklassifizierung.md) (`PLAN-GOV-KI-VORKLASSIFIZIERUNG-13`) | Datenobjekt-Klassifizierung & KI-Vorklassifizierung (OpenJEV-Style) | Startzustand `UNCLASSIFIED` (Fail-Closed); optionale KI-Vorklassifizierung mit Timeout-Guard (150ms) & Injection-Schutz; Auto-Vorklassi bei Konfidenz $\ge 95\%$ | **GROBKONZEPT 🛡️** |
| [konzept-data-governance-und-mcp-documentation.md](file:///root/autheris/docs/architecture/konzept-data-governance-und-mcp-documentation.md) (`ARCH-GOV-DOC-MCP-2026`) | **arc42 Master-Architekturkonzept:** Unified Data Governance & Offline Doc-MCP Platform | Konsolidiertes Gesamt-Architekturkonzept (arc42 v8.2): Shared-Volume Ingestion API & MCP-Deposit, Inotify-Indexierung, dynamische Taxonomie mit Rängen, OpenJEV-Style Vorklassifizierung, Dual-Sign-Off, asymmetrisches Change Management und WORM-Drive-Sealing | **ARCHITEKTUR-BLUEPRINT (arc42) 🏛️** |
| [2026-10-10-konzept-universelle-dokumentenablage-mcp.md](file:///root/autheris/docs/plans/2026-10-10-konzept-universelle-dokumentenablage-mcp.md) (`PLAN-UNIVERSAL-DOC-STORE-14`) | Universelle Dokumentenablage für alle Applikationen via MCP | 4 Ablage-Konzepte (GitOps, S3/MinIO, Ingestion-API/MCP-Deposit, Shared Volume); 100% Offline/Air-Gapped; Multi-App-Katalog & Suche für KI-Agenten | **KONZEPT 📚** |

---

---

> [!NOTE]
> **Historische Pläne (1–11):** Alle bisherigen Pläne (1 bis 11, SQL-AST Härtung sowie Tracks A bis E) sind zu 100 % fertiggestellt, durch automatisierte Tests verifiziert und wurden gemäß Richtlinie aus `docs/plans/` bereinigt.

