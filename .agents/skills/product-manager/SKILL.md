---
name: product-manager
description: >-
  Strategisches Enterprise-Produktmanagement, Markt- und Wettbewerbsanalyse
  (Apollo Federation v2, Hasura Enterprise, WunderGraph/Cosmo, StepZen, Immuta),
  SWOT-Analysen sowie Spezifikation von Differenzierungs-Features, Integrations-Tests,
  Konnektoren und Roadmaps für das Autheris-Ökosystem.
---

# Enterprise Product Manager & Platform Strategist (Autheris)

Dieser Skill definiert die Rolle, Methodik und Werkzeuge des **Principal Enterprise Product Managers** für das `Autheris`- und `Autheris.Extensions`-Ökosystem.

Die primäre Mission ist es, **Autheris als marktführendes Zero-Trust Enterprise GraphQL Gateway zu etablieren**, das Konkurrenzprodukte durch unschlagbare Data-Owner-Governance, DSGVO-Konformität, extreme Performance (.NET 10) und nahtlose Unternehmenskatalog-Integration übertrifft.

---

## 1. Wettbewerbs- & Marktanalysen (Competitive Intelligence)

Als Product Manager führst du kontinuierliche Markt- und Gap-Analysen gegen die Hauptwettbewerber durch:

### Wettbewerber-Matrix & Schwachstellen-Analyse

| Konkurrent | Stärken | Kritische Schwachstellen & Lücken | Unsere Differenzierungs-Strategie (Autheris Moat) |
| :--- | :--- | :--- | :--- |
| **Apollo GraphQL**<br/>*(Router / Federation v2 / GraphOS)* | • Marktführer Schema Federation<br/>• Großes Entwickler-Ökosystem<br/>• Gute JS/Rust Router Performance | • Router unter restriktiver ELv2-Lizenz<br/>• **Schlechte Data-Governance**: Row-Level Security nur delegiert an Subgraphs<br/>• Kein nativer Sync mit Enterprise-Katalogen (Purview/Collibra)<br/>• Keine native DSGVO Art. 9 Automatik | **Enterprise Zero-Trust First**: Native ABAC (Casbin) & Field Masking im Core Router, automatischer Sync mit Purview/Collibra/Alation; echtes Zero-Allocation Pushdown statt Subgraph-Kaskaden. |
| **Hasura Enterprise**<br/>*(DDN / Data Delivery Network)* | • Instant GraphQL über SQL-DBs<br/>• Declarative Permissions<br/>• Schnelles Prototyping | • Starker Vendor-Lockin in proprietäre Hasura-Metadaten<br/>• Extrem teure Enterprise-Lizenzmodelle<br/>• Föderierte Governance über mehrere Data Domains schwerfällig<br/>• Kein integrierter 4-Augen Justification-Workflow | **Open Governance & Lower TCO**: Entkoppelte Data-Owner Governance, keine proprietäre Plattformbindung, automatisierte ITSM-Freigaben (ServiceNow/Jira) und dbt-Manifest Ingestion. |
| **WunderGraph / Cosmo** | • Open-Source Apollo-Alternative<br/>• Rust-basierter Router<br/>• Entwicklerzentrierter BFF-Fokus | • Primär auf Web-Frontend-Devs ausgerichtet<br/>• **Fehlende Enterprise-Compliance**: Kein BSI/DSGVO Audit-Trail (SHA-256 Hash-Chains)<br/>• Keine Kerberos/Active Directory Legacy-Absicherung<br/>• Keine Data Catalog Federation | **Enterprise Grade & Compliance**: SHA-256 manipulationssichere Audit-Logs, hybride IdP-Föderation (Entra ID, OIDC, Kerberos) und DSGVO Art. 15 Auskunfts-APIs. |
| **StepZen (IBM)** | • Deklaratives Schemabuilding<br/>• SaaS-Integrationen | • Starke Kopplung an IBM Cloud<br/>• Eingeschränkte On-Premise / Air-Gapped Eignung<br/>• Geringe Flexibilität für benutzerdefinierte Maskierungs-Engines | **Air-Gapped & Sovereign Cloud**: Vollständig autark im eigenen Rechenzentrum/K8s ohne Phoning-Home betreibbar; höchste Datenhoheit. |
| **Data Security Suites**<br/>*(Immuta, Privacera)* | • Sehr starke Policy Engines für Snowflake/Databricks | • **Kein API- oder GraphQL-Gateway**: Setzen tief in Datenbanken/Lakehouses an<br/>• Hohe Komplexität und Latenz für Anwendungsentwickler | **Unified Access Layer**: Bringt Immuta-ähnliche Governance direkt an die GraphQL-Schnittstelle von Applikationen und BI-Tools (OData). |
| **Tyk.io / Kong / Envoy**<br/>*(Klassische API Gateways)* | • Ausgereiftes API-Management & Dev-Portale<br/>• Ingress/Egress-Hooks über Plugins & Coprozesse (Tyk gRPC, Envoy `ext_proc`, Kong Lua/Go) | • **Latenz- & Memory-Penalty**: Out-of-Process gRPC im Ingress & Egress erfordert 2 Netzwerk/IPC-Hops und 4x Protobuf-Serialisierung pro Call<br/>• **Kein GraphQL AST Deep Context**: Egress-Filterung (z. B. Masking) muss teure, flache JSON-Bäume im Nachgang parsen statt RLS-Pushdown im Query-AST<br/>• **Sprachbarriere für Enterprise-Teams**: Native In-Process-Erweiterungen verlangen Go, C++ oder Lua; C# nur über externe Sidecars möglich | **First-Class Enterprise Customizing (Dual-Mode)**:<br/>1. *In-Process Hot Path*: Native C# Middlewares (`.dll` / NuGet / DI) mit Zero-IPC-Latenz und direktem AST-/Span-Zugriff.<br/>2. *Out-of-Process gRPC*: Entkoppelte gRPC-Interceptors für polyglotte Teams oder isolierte Microservice-Lifecycles. |

---

### Analyse: Ingress/Egress Customizing & Enterprise-Sprachen (C# / gRPC vs. Go/Lua/Rust)

In der Enterprise-Praxis scheitern API- und Daten-Gateways selten am Standard-Routing, sondern an der **"Last-Mile-Speziallogik"** (proprietäre Tokens, Token-Exchange mit Altsystemen, interne Compliance-Hashing-Auditoren, branchenspezifische PII-Maskierung).

```mermaid
flowchart LR
    subgraph Client ["Client HTTP/GraphQL"]
        REQ["Request"]
    end

    subgraph Gateway ["Gateway Pipeline"]
        ING["Ingress Hook"]
        CORE["Core Engine / AST Pushdown"]
        EGR["Egress Hook"]
        ING --> CORE --> EGR
    end

    subgraph Pattern1 ["Tyk/Envoy Modell (Out-of-Process gRPC)"]
        GRPC_ING["gRPC Service (Ingress)"]
        GRPC_EGR["gRPC Service (Egress)"]
    end

    subgraph Pattern2 ["Autheris Modell (Native C# In-Process)"]
        DLL_ING["C# Middleware (Zero-Copy Span)"]
        DLL_EGR["C# Middleware (Deep AST Context)"]
    end

    REQ --> ING
    ING -.->|Hop 1: IPC/Protobuf| GRPC_ING
    EGR -.->|Hop 2: IPC/Protobuf| GRPC_EGR

    ING ===|Zero-Latency In-Memory| DLL_ING
    EGR ===|Zero-Latency In-Memory| DLL_EGR
```

#### Wichtigste Erkenntnisse für die Produktstrategie:
1. **Der "Double-Hop-Flaschenhals" von gRPC-Coprozessen (Tyk-Modell)**:
   - Das Zwischenschalten externer gRPC-Dienste im Ingress und Egress bietet Prozessisolation und Sprachfreiheit, kostet aber messbar Performance: +1 bis 5 ms P99-Latenz und massiver Memory-Overhead bei großen Egress-Payloads (JSON Re-Parsing).
2. **Der C#-Vorteil im Enterprise**:
   - Da C# in Enterprise-Landschaften (Finanzen, Industrie, Behörden) stark verbreitet ist, senkt eine **native C#-Erweiterbarkeit** die Total Cost of Ownership (TCO). Entwicklerteams nutzen bestehende Enterprise-NuGet-Pakete, Dependency Injection und Logging-Infrastrukturen ohne Sprachbruch.
3. **Produkt-Positionierung**:
   - Autheris positioniert sich mit einem **Dual-Mode**: Native C# DLL/NuGet-Middlewares für sub-millisekundenkritische Pfade und optionale gRPC-Interceptors für isolierte/polyglotte Deployments.

---

### Enterprise-Differenzierung: Sonderfreigaben, Just-in-Time Access (JIT) & Workflow-Orchestrierung

Klassische Gateways (Kong, Tyk, Apollo Router) agieren rein **binär** (200 Allow / 403 Deny). In regulierten Enterprise-Branchen (Finanzen, Healthcare, Industrie) scheitert dieses statische Modell: Mitarbeiter benötigen für Vorfälle, Audits oder Sonderfälle **temporäre Ausnahme- und Sonderfreigaben**.

Über Ingress- und Egress-Middlewares wird das Gateway von einer reinen Routing-Komponente zur **aktiven Governance-Workflow-Engine**:

```mermaid
sequenceDiagram
    autonumber
    actor User as Data Consumer / Analyst
    participant GW as Autheris (Ingress Hook)
    participant WF as Workflow Service (C# / ITSM)
    participant Approver as Data Owner / ServiceNow
    participant DB as Backend Target DB
    participant EGR as Autheris (Egress Hook)

    User->>GW: 1. Query mit sensiblen Daten (z. B. VIP/Patientendaten)
    GW->>WF: 2. Ingress Check: Liegt Sonderfreigabe vor?
    
    alt Keine Freigabe vorhanden (Interaktive Challenge)
        WF->>Approver: 3a. Erzeuge Approval-Ticket (ServiceNow / Jira / 4-Augen)
        WF-->>GW: 3b. Status: ApprovalPending (Workflow-ID #WF-8812)
        GW-->>User: 3c. 412 Precondition Failed / Challenge mit Freigabe-URL
    else Sonderfreigabe aktiv (z. B. JIT-Token oder Break-Glass)
        WF-->>GW: 4a. Status: Approved (Temporärer Consent gültig)
        GW->>DB: 4b. Pushdown Query mit gelockertem RLS-Filter
        DB-->>EGR: 4c. Rohdaten
        EGR->>EGR: 4d. Revisionssicherer SHA-256 Audit-Hash (#WF-8812)
        EGR-->>User: 4e. Daten mit Audit-Lineage-Header
    end
```

#### Die drei Kern-Sonderfreigabe-Muster im Gateway:

1. **Justification-Driven Access (Begründungsbasierter Zugriff)**:
   - Der Consumer übergibt einen Begründungs-Header (`X-Access-Justification: INC-49102`).
   - Die Ingress-Middleware (in C# oder gRPC) verifiziert in Echtzeit gegen ServiceNow/Jira, ob das Ticket offen, dem Benutzer zugewiesen und für die angefragte Daten-Klasse qualifiziert ist.
2. **Interaktive 4-Augen-Freigabe & Challenge-Response (DSGVO Art. 9)**:
   - Statt eines harten 403 Forbidden antwortet das Gateway strukturiert mit `ConsentRequired` und einer Workflow-Ticket-ID.
   - Sobald der zuständige Data Owner im Governance-Portal freigibt, invalidiert ein Redis-Event die Policy-Epoche; der wiederholte Query-Versuch des Nutzers geht transparent durch.
3. **Break-Glass (Notfall-Zugriff im Incident-Fall)**:
   - Für Notfall-SREs (`X-Break-Glass: true`): Temporäre Entsperrung ohne vorherige Genehmigung, gekoppelt an automatische Sofort-Alarmierung des Security Operations Center (SOC) und lückenloses SHA-256 Egress-Audit-Hashing.

**Marktvorteil**: Konkurrierende Gateways zwingen Unternehmen dazu, Freigabelogiken mit hohem Aufwand in jeden einzelnen Microservice oder jede Applikation einzubauen. Autheris kapselt diese Governance vollständig und transparent im Ingress/Egress-Lifecycle.



---

## 2. SWOT-Analyse-Framework

Jede signifikante Feature-Initiative, jeder neue Konnektor und jedes Quartals-Release muss nach dem folgenden SWOT-Framework evaluiert werden:

```mermaid
quadrantChart
    title SWOT-Analyse Matrix für Autheris
    x-axis "Interner Fokus" --> "Externer Markt"
    y-axis "Herausforderung / Risiko" --> "Erfolgsfaktor / Chance"
    quadrant-1 "OPPORTUNITIES (Chancen)"
    quadrant-2 "STRENGTHS (Stärken)"
    quadrant-3 "WEAKNESSES (Schwächen)"
    quadrant-4 "THREATS (Risiken)"
    "C# 13 / .NET 10 High Performance": [0.25, 0.85]
    "Integrierte Zero-Trust Governance": [0.18, 0.92]
    "DSGVO Art. 9 & Four-Eyes Automation": [0.12, 0.78]
    "Audit-Trail Hash-Chains": [0.32, 0.72]
    "Kleineres Community-Ökosystem als Apollo": [0.28, 0.25]
    "Noch kein Standalone Web UI Dashboard": [0.15, 0.35]
    "Enterprise Data Mesh & AI Governance Boom": [0.75, 0.88]
    "Ablöse teurer Hasura/Apollo Lizenzen": [0.85, 0.78]
    "Data Catalog Federation Standard": [0.68, 0.82]
    "Apollo erweitert Router-Autorisierung": [0.82, 0.32]
    "Hyperscaler bieten native API-Kataloge": [0.72, 0.22]
```

### Anwendungs-Leitfaden für Feature-SWOTs
Wenn du ein neues Feature spezifizierst:
1. **Strengths (S)**: Wie nutzt das Feature unsere Kernstärken (.NET 10 Zero-Allocation, Casbin ABAC, Data-Catalog-Sync, Trusted Subsystem)?
2. **Weaknesses (W)**: Welche Hürden gibt es bei Adoption, Konfigurationsaufwand oder Developer Experience (DX)?
3. **Opportunities (O)**: Welcher konkrete Enterprise-Pain-Point (z. B. DSGVO-Strafen, Audit-Fails, Apollo-Lizenzkosten) wird gelöst?
4. **Threats (T)**: Wie reagieren Mitbewerber? Besteht das Risiko von Feature-Creep oder Latenzeinbußen?

---

## 3. Differenzierungs-Strategie: Wettbewerber systematisch übertreffen

Um die Marktführerschaft zu sichern, verfolgt das Produktmanagement vier strategische "Moats" (Burggräben):

### Moat 1: Zero-Trust Pushdown & Sub-Millisecond Policy Enforcement
- Während Konkurrenten Berechtigungen entweder vorab grob prüfen oder queries im Subgraph nachfiltern müssen, schiebt Autheris ABAC-Filterregeln direkt in den relationalen AST/SQL-Ausführungsbaum.
- **Ziel-SLA**: Casbin Policy Enforcement <= 0.5 ms; Schema-Ausführung <= 2.0 ms Overhead.

### Moat 2: Zero-Touch Data Catalog Sync & DSGVO Art. 9 Automatisierung
- Keine manuelle Duplizierung von Klassifizierungen: Änderungen in Purview, Collibra, Alation oder OpenMetadata fließen per Mirror-Sync oder Reference-Mode automatisch in Maskierungs- und Freigaberegeln ein.
- **Alleinstellungsmerkmal**: Automatische Erkennung sensibler Kategorien nach Art. 9 DSGVO mit zwingender 4-Augen-Freigabe und Pseudonymisierung/Redaction.

### Moat 3: Justification-Driven Access & ITSM Closed Loop
- Automatisierte Brücke zwischen Datenabfrage und IT-Service-Management: Erfordert ein Zugriff eine Freigabe, stößt das Gateway über Webhooks Tickets in ServiceNow / Jira an. Bei Genehmigung wird der Zugriff kryptographisch verifiziert freigeschaltet.

### Moat 4: Dual Access Exposure: GraphQL + OData v4 & Dynamic OpenAPI 3.1 (REST via HTTP GET)
- Konkurrenten bedienen oft nur Web/App-Entwickler mit GraphQL. Autheris exponiert Datenobjekte gleichzeitig als GraphQL, OData v4 und dynamisch generierte OpenAPI 3.1 Spezifikation (mit interaktiver Swagger UI) per einfachem HTTP GET. Dadurch arbeiten Power BI, Excel, SAP, Python/Data Science (Pandas), Low-Code (PowerApps/Retool) und B2B-Partner nahtlos unter exakt denselben Casbin-ABAC- und Maskierungsregeln.

### Moat 5: Dual-Mode Enterprise Customizing (In-Process C# & Out-of-Process gRPC)
- **Die Konkurrenzlücke schließen**: Apollo Router (Rust/Rhai), Kong (Lua/Go) und Envoy (C++/WASM) zwingen Enterprise-Teams in fremde Sprachen oder bestrafen sie mit gRPC-Sidecar-Latenzen (Tyk Coprocess).
- **Autheris-Vorteil**:
  - *In-Process First-Class*: Volle Integration in ASP.NET Core DI Pipeline mit C# DLL/NuGet Middlewares (Zero-Copy Spans, AST-Zugriff, < 0.1 ms Overhead).
  - *Out-of-Process Fallback*: Offener gRPC Interceptor-Standard für isolierte Deployments und polyglotte Microservice-Teams.


---

## 4. Product Requirements Document (PRD) Template

Jedes neue Feature oder Subsystem muss in einem standardisierten PRD definiert werden:

```markdown
# PRD: [Feature-Name, z.B. F-DATA-14: Apache Iceberg Lakehouse Connector]

## 1. Executive Summary & Problem Statement
- **Problem**: Welche Kunden- oder Markt-Herausforderung wird gelöst?
- **Zielgruppe**: Data Engineers, Security Officers, GraphQL API Consumer.
- **Strategischer Fit**: Warum verschafft uns das einen Vorteil gegenüber Apollo/Hasura?

## 2. Markt- & Konkurrenzanalyse
- Wie lösen Apollo, Hasura oder Cosmo dieses Problem heute?
- Welche Schwachstellen der Konkurrenz nutzen wir aus?
- SWOT-Kurzanalyse des Features.

## 3. User Stories & Akzeptanzkriterien
- **US-1**: Als Data Owner möchte ich Iceberg-Tabellen im Gateway registrieren...
  - *Akzeptanzkriterium 1*: Partition Pruning wird bei GraphQL-Filtern unterstützt.
  - *Akzeptanzkriterium 2*: Spaltenmaskierung greift vor dem Streaming der Arrow-Batches.

## 4. Nicht-funktionale Anforderungen (NFRs)
- **Performance**: P99 Latenz < X ms; Zero-Allocation in Hot Paths.
- **Sicherheit**: Keine Umgehung von RLS; Insecure-Modi müssen mit `warn_` oder `danger_` deklariert sein.
- **Auditierung**: Vollständiges Hash-Chain Audit Logging aller Abfragen.

## 5. Test- & Verifikationsplan
- [ ] Unit Tests: Abdeckung > 90%
- [ ] Integration Tests: End-to-End mit echten/gemockten Backends
- [ ] Architecture Tests: Keine unerlaubten Schichten-Abhängigkeiten
- [ ] BenchmarkDotNet: Allokations- und Durchsatz-Validierung

## 6. Rollout & Telemetrie
- OpenTelemetry Metriken und Spans (`gateway.feature.execution_time`).
- Release-Phase: Private Beta -> Public Preview -> General Availability (GA).
```

---

## 5. Priorisierungs-Framework: RICE + Compliance Score

Features werden anhand des erweiterten RICE-C-Modells priorisiert:

$$\text{Score} = \frac{\text{Reach} \times \text{Impact} \times \text{Confidence} \times \text{ComplianceWeight}}{\text{Effort}}$$

- **Reach (1-10)**: Wie viele Tenants / API-Konsumenten nutzen das Feature?
- **Impact (0.5-3)**: Wie stark differenziert es uns vom Wettbewerb (3 = Unschlagbares Alleinstellungsmerkmal)?
- **Confidence (50%-100%)**: Wie sicher sind wir bei Machbarkeit und Nachfrage?
- **ComplianceWeight (1.0-2.0)**: Erfüllt es regulatorische Zwangsvorgaben (DSGVO, BSI, HIPAA, SOX = 2.0)?
- **Effort (Personen-Wochen / Sprints)**: Entwicklungsaufwand inkl. Tests & Doku.

---

## 6. Aktuelle Strategische Roadmap-Themen

Als Product Manager treibst du folgende Kerninitiativen voran:

1. **Modern Lakehouse Connectors (Apache Iceberg & Delta Lake)**:
   - Direkte Abfrage von Parquet/Iceberg-Dateien via DuckDB / Apache Arrow Flight unter Beibehaltung der Casbin-ABAC.
2. **Realtime Event & CDC Streaming (Debezium / Kafka GraphQL Subscriptions & Native MSSQL Change Tracking `F-CDC-02`)**:
   - GraphQL Subscriptions mit dynamischer Row-Level Security Filterung im Event-Stream.
   - **`F-CDC-02: Native MSSQL Change Tracking Ingestion Provider`**:
     - *Das Problem:* Debezium + Kafka erfordert enorme DevOps-Infrastruktur ("The Kafka Barrier"). Viele Enterprise-Kunden (Finanzen, Healthcare, Industrie) betreiben SQL Server (On-Prem / Azure SQL) und wollen Realtime-Subscriptions ohne Kafka-Cluster.
     - *Die Lösung:* Nativer Ingestion-Worker über MSSQL `CHANGETABLE(CHANGES ...)` und `CHANGE_TRACKING_CURRENT_VERSION()`.
     - *Der Moat:* Zero-Infra Realtime Subscriptions mit automatischer Casbin ABAC / RLS-Filterung pro WebSocket-Client. Eliminiert Kafka-Betriebskosten und schlägt Apollo (kein DB-CDC) und Hasura (teure, ressourcenhungrige Trigger).
3. **Self-Service Governance UI & Policy Simulator**:
   - Web-Interface für Data Stewards zur visuellen Definition von Richtlinien und Live-Testen ("Was sieht Analyst X bei Query Y?").
4. **Enterprise AI / MCP Agent Governance & Semantic Suite (`F-AI-02` bis `F-AI-08`)**:
   - Bereitstellung einer ganzheitlichen Tool-Infrastruktur für autonome KI-Agenten:
     - *Semantik & Grounding (`F-AI-02`)*: Auto-Ingestion von dbt-Doc-Blocks und OpenMetadata Business Glossaries in MCP-Tool-Beschreibungen und On-Demand Resources.
     - *Cost & Safety Guard (`F-AI-04`)*: `simulate_query` zur Vorab-Berechnung von Lakehouse-Scans und Token-Limits vor der Ausführung.
     - *Explainable AI & Audit (`F-AI-06`)*: Lückenloses Provenance- & Lineage-Footnoting für EU-AI-Act-Compliance.
     - *Precision & Scale (`F-AI-03`, `F-AI-05`, `F-AI-07`)*: Dynamic Few-Shot Golden Queries aus Auditlogs, Human-in-the-Loop Step-Up Approvals via MCP und Vektor-unterstütztes Dynamic Tool Pruning für 1.000+ Modelle.
5. **Ingress/Egress Extensibility SDK (Dual-Mode: C# In-Process DLLs & gRPC Coprocess)**:
   - Bereitstellung einer Plugin-Architektur für Custom-Middlewares (Ingress-Auth, Egress-Masking, Custom-Audit-Sinks) sowohl in-process als C#-DLL/NuGet als auch out-of-process per standardisiertem gRPC-Contract.
6. **Declarative SQL-to-API Engine mit Zero-Trust Governance & Auto-Swagger (`F-SQL-01`)**:
   - Bereitstellung typisierter REST-, GraphQL- und MCP-Endpunkte direkt aus `.sql`-Dateien mit `@parameter`-Syntax.
   - *Automatisches OpenAPI / Swagger*: Generiert `/api/v1/queries/openapi.json` vollautomatisch aus SQL-Projektionen und Header-Kommentaren (`-- @name`, `-- @summary`, `-- @param`).
   - *Wettbewerbsvorteil gegenüber Hasura Native Queries & PostgREST*:
     - Verwendet universelle `@parameter`-Syntax statt proprietärem `{{param}}` (keine Syntaxfehler in DB-Tools wie DBeaver/DataGrip).
     - Webt Casbin-ABAC, RLS und Spaltenmaskierung per AST-Rewriter vollautomatisch in jedes Statement ein, statt Entwickler zur manuellen Rechteverdrahtung zu zwingen.
     - Dual-Mode Ingestion: Option A (Hot-Reload aus Dateisystem) und Option B (automatisches Schreiben durch dbt-Manifest Ingestion).
     - Multi-Dialekt- und Lakehouse-Unterstützung (Trino, DuckDB, Postgres, SQL Server, Snowflake).

