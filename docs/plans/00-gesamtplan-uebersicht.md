# Gesamtübersicht der Architektur- & Implementierungspläne

**Stand:** 09.10.2026 · **Zweig:** `feat/ast-target-dialect-generator`  
**Rolle:** C# & .NET Solution Architect  
**Ziel:** Strukturierte Übersicht und Ausführungsgraph aller abgeschlossenen sowie neu erstellten, feature-bezogenen Implementierungspläne in `docs/plans/`.

---

## 1. Übersicht der Implementierungspläne pro Feature

| Plan / Dokument | Thema / Feature | Behandelte Befunde & Anforderungen | Status |
|---|---|---|---|
| **[Plan 1: Distributed State & Invalidation](plan-architektur-distributed-state-invalidation.md)** | Cache-Konsistenz, ReBAC-Invalidierung & Shared State | **AR-01, AR-02, AR-03, AR-04, AR-12** | Vollständig umgesetzt & verifiziert ✅ |
| **[Plan 2: God-Classes & Modularisierung](plan-architektur-refactoring-godclasses-modules.md)** | Refactoring von `GovernedSqlExecutionService`, DI Composition Root & Repositories | **AR-05, AR-06, AR-07, AR-11, AR-18** | Vollständig umgesetzt & verifiziert ✅ |
| **[Plan 3: Performance & Caching](plan-architektur-performance-caching-dialekte.md)** | Epochen-Pipelining, Bounded LRU Plan-Cache & Dialekt-Konsistenz | **AR-08, AR-09, AR-10, AR-13, AR-14, AR-15, AR-19** | Vollständig umgesetzt & verifiziert ✅ |
| **[Plan 4: Supply Chain, CI & Deploy](plan-security-supply-chain-ci-deploy.md)** | Container-Scanning (Trivy), Cosign-Signierung, Attestations & Root-Drop | **SC-01 bis SC-18** | Vollständig umgesetzt & verifiziert ✅ |
| **[Plan 5: PoC Arrow/OLAP & MCP Staging](plan-poc-arrow-olap-rebac-und-mcp-staging.md)** | ReBAC-Fallback für Arrow/OLAP, anonyme OAuth-Discovery & JSON-RPC-Batches | **Befunde 3.1 & 3.2** | Vollständig umgesetzt & verifiziert ✅ |
| **[Plan 6: Lückenloses Zugriffs-Audit](plan-lueckenloses-zugriffs-audit-by-default.md)** | „Audit by Default“ über alle Endpunkte & Middleware, Denial-Audit | **Lücken L-1 bis L-9 (Phasen 0 bis 7)** | Vollständig umgesetzt & verifiziert ✅ |
| **[Plan 7: WebSQL API Federation Join](plan-websql-heterogene-api-federation-join.md)** | Heterogene Joins zwischen SQL-Tabellen und Web-APIs via WebSQL & DuckDB Routing | **WebSQL Query Federation & API Joins** | Vollständig umgesetzt & verifiziert ✅ |

### 1.1 Entscheidungen aus dem Review (09.10.2026)

Leitlinie: Laufzeit-Performance im Standardbetrieb darf nicht sinken; was nur mit Latenzkosten geht, ist per Option zuschaltbar (Default aus). Jeder Plan enthält dazu ein Benchmark-Abnahmekriterium (≤ +3 % auf dem Hot-Path).

| Plan | Entscheidung |
|---|---|
| Plan 1 | AR-12 per Option A (zustandsloser Matcher), B nur als Fallback. Epoch/Generation lokal 1 s gehalten (`0` = strikt), später im MGET aus Plan 3 – kein Zusatz-Roundtrip. |
| Plan 2 | ADR „Erlaubte Fremdabhängigkeiten“: Casbin.NET und HotChocolate.Language bleiben (Namespace-Whitelist), `DeclarativeHttpDataSourceExecutor` → Infrastructure, MemoryPack bleibt in Domain (arc42 4.1 korrigieren). |
| Plan 3 | Micro-Cache Default aus; Spalten-Hash/Policy-Fingerprint vorberechnet bzw. wiederverwendet (keine Zusatzkosten je Request). |
| Plan 4 | Action-SHAs per `git ls-remote` gesetzt (Stand 09.10.2026). `Gateway__DataMasking__HmacSecret` ist wirkungslos (kein Options-Property) → streichen; Key kommt aus `HMAC_SECRET_KEY`. Zwei Bench-Startpfade dokumentiert; `stage_sources.sh` baut aus fremdem Checkout (`/root/gql`) → Entfernung ist Voraussetzung für Phase 2. Secrets nur beim Start gelesen, keine Laufzeitkosten. |
| Plan 5 | RFC 8414: Variante A – Endpunkt entfernt (`404`). JSON-RPC-Batches werden mit `-32600` abgelehnt. |
| Plan 6 | Katalog-Audit verdichtet (Option `Full`); `SynchronousQueryAudit` Option, Default aus (fail-closed über Kanal-Annahme vor dem ersten Byte); Retention je Klasse mit Mindestwerten; regulatorische Pflichtereignisse fest aktiv; p95 Lesepfad ≤ 3 % / ≤ 0,5 ms. |
| Plan 7 | Option `WebSqlOptions.CrossSource.Enabled` (Default `false`, Lazy-Routing ohne Zusatzkosten). Phase 0 (Ablehnung von HTTP/Plugin/Lakehouse-Tabellen im SQL-Pfad, heute fail-open) läuft **unbedingt** – Sicherheitsfix, reiner Enum-Vergleich. v1 ohne Bind-Joins, ohne Pagination, ohne Spill, ohne `ForwardBearerToken`-Quellen. HMAC-Joins über Quellen (E-5) vorbehaltlich Bestätigung Datenschutz. |

Der frühere Umsetzungsplan zum Zugriffs-Audit ist in Plan 6 aufgegangen und entfernt.

---

## 2. Historie: Bereits umgesetzte & verifizierte Features ✅

Alle ursprünglichen Kernfunktionen des Branches wurden vollständig umgesetzt, getestet und durch 3.600+ Unit-Tests verifiziert:
- **dbt-Governance & Metadata Streaming:** B-01 bis B-06, R-50, R-51 (`replace`-Modus, typgerechtes `REDACT`).
- **Erweiterte Maskierungsregeln:** R-53, R-25 (`GEO_JITTER`, `PARTIAL_MASK`, `TOKENIZATION`).
- **Klartext je Person & Profile:** R-52, R-50, R-20 (`AccessProfile`, `david` unmasked vs. `philipp` default).
- **Audit-Kern-Härtung:** AU-01 bis AU-19 (KMS-Anker, Transaktionskopplung, Dead-Letter Queue).
- **Security Review Phasen 1–3:** SG-01 bis SG-39, Release Cross-Compile Fix (NU1004).

---

## 3. Ausführungsgraph des verbleibenden Backlogs

```mermaid
flowchart TD
    subgraph Foundation["1. Basis-Architektur & State (Plan 1 & 2)"]
        STATE["Plan 1: Distributed State & Cache-Invalidation<br/>(AR-01..04, AR-12)"]
        MODULAR["Plan 2: God-Classes Refactoring & DI-Module<br/>(AR-05..07, AR-11, AR-18)"]
    end

    subgraph Optimization["2. Performance & Security CI (Plan 3 & 4)"]
        PERF["Plan 3: Performance, Epochen-Batching & Dialekte<br/>(AR-08..10, AR-13..15, AR-19)"]
        SUPPLY["Plan 4: Supply-Chain, Cosign & Deploy-Härtung<br/>(SC-01..18)"]
    end

    subgraph Egress["3. Egress- & Audit-Vollständigkeit (Plan 5, 6 & 7)"]
        POC["Plan 5: PoC Arrow/OLAP & MCP Staging<br/>(3.1 & 3.2)"]
        AUDIT["Plan 6: Lückenloses Zugriffs-Audit<br/>(L-1..L-9, Phasen 0..7)"]
        WEBSQL["Plan 7: WebSQL Cross-Source Join<br/>(Phase 0 = Sicherheitsfix, vorziehbar)"]
    end

    STATE --> MODULAR
    MODULAR --> PERF
    STATE --> SUPPLY
    MODULAR --> AUDIT
    PERF --> POC
    POC --> AUDIT
    PERF --> WEBSQL
    AUDIT --> WEBSQL
```

---

## 4. Quelltexte und Referenzdokumente

- [Plan 1: Distributed State & Invalidation](plan-architektur-distributed-state-invalidation.md)
- [Plan 2: God-Classes & Modularisierung](plan-architektur-refactoring-godclasses-modules.md)
- [Plan 3: Performance & Caching](plan-architektur-performance-caching-dialekte.md)
- [Plan 4: Supply Chain, CI & Deploy](plan-security-supply-chain-ci-deploy.md)
- [Plan 5: PoC Arrow/OLAP & MCP Staging](plan-poc-arrow-olap-rebac-und-mcp-staging.md)
- [Plan 6: Lückenloses Zugriffs-Audit](plan-lueckenloses-zugriffs-audit-by-default.md)
- [Plan 7: WebSQL API Federation Join](plan-websql-heterogene-api-federation-join.md)
- [Architektur-Review 2026-10-09](2026-10-09-architecture-review.md)
- [Security Review Build & Supply Chain 2026-10-09](2026-10-09-security-review-supply-chain-deploy.md)
- [Feature Lückenloses Zugriffs-Audit](2026-10-09-feature-lueckenloses-zugriffs-audit.md)
- [Requirements PoC Citizen Dev](2026-10-09-requirements-poc-v1-1-2.md)
