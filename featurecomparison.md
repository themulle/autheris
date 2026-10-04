# ⚖️ Enterprise Feature-Vergleich & Wettbewerbsanalyse (Feature Comparison)

**Dokument:** Autheris vs. Markt-Wettbewerber (Stand: 2025 / 2026)  
**Autor:** Principal Enterprise Product Manager & Platform Strategist  
**Verglichene Lösungen:**
1. **Autheris** (Enterprise Zero-Trust Data-Owner Gateway)
2. **Apollo GraphQL** (Federation v2, GraphOS, Apollo Router)
3. **Hasura Enterprise** (DDN / Data Delivery Network v3)
4. **WunderGraph / Cosmo** (Open-Source Federation Router)
5. **StepZen** (IBM API Gateway / Declarative GraphQL)
6. **Data Security Suites** (Immuta, Privacera)
7. **Klassische API Gateways** (Tyk.io, Kong Enterprise, Envoy Gateway)

---

## 1. Executive Summary & Markteinordnung

Im Jahr 2025/2026 hat sich der Markt für GraphQL- und API-Gateways in drei Hauptlager aufgeteilt:
* **Frontend- & API-Föderations-Router (Apollo, Cosmo):** Exzellent im Zusammensetzen von Subgraphs, aber blind für relationale Data Governance, zeilenbasierte Autorisierung (RLS) und revisionssichere Audit-Compliance.
* **Instant-DB-APIs (Hasura DDN, StepZen):** Schneller Zugriff auf Datenbanken, jedoch stark proprietär gekoppelt, mit astronomischen Enterprise-Lizenzkosten und ohne native Integration in ITSM-Freigabeprozesse.
* **Klassische Ingress-Gateways (Kong, Tyk, Envoy):** Stark im L7-Routing, scheitern jedoch an Deep GraphQL AST-Kontexten, verursachen durch Out-of-Process-Coprozesse (gRPC) massive Latenzstrafen und kennen nur statisches Allow/Deny statt interaktiver Governance.

**Autheris besetzt die Leerstelle:** Ein **Zero-Trust Enterprise Data Gateway**, das Subgraph-Föderation (Hot Chocolate Fusion) mit tiefem relationalen SQL-RLS-Pushdown, nativer Apache Iceberg Lakehouse-Unterstützung, **columnar Apache Parquet Analytics-Export**, OData v4 Dual-Access, unternehmensweiter Metadaten-Synchronisation (Purview, Collibra, Alation), dbt Data-Mesh-Absicherung und nativer KI-Agenten-Governance (Model Context Protocol / MCP) verbindet.

---

## 2. Umfassende Feature-Vergleichsmatrix

| Feature-Bereich | Feature / Anforderung | Autheris | Apollo GraphOS / Router | Hasura DDN Enterprise | WunderGraph Cosmo | StepZen (IBM) | Immuta / Privacera | Tyk / Kong / Envoy |
| :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Architektur & Engine** | Laufzeitumgebung / Sprache | **.NET 10 / C# 14** | Rust (Router) / Node.js | Rust / Haskell | Rust / Go | Go / Java | Java / Python Daemon | Go / C++ / Lua |
| | AST-Parsing & Zero-Allocation | **Ja** (Spans, SIMD) | Ja (Rust) | Ja | Ja | Teilweise | Nein (DB-Proxy) | Nein (JSON-Stream) |
| | Dual-Mode Extensibility | **Ja** (C# DLL & gRPC) | Nein (Rhai / Rust) | Nein (Webhooks) | Nein (Go Plugins) | Nein | Nein | Teilweise (gRPC/Lua) |
| **Access Control & RLS** | Zero-Trust Default Fail-Closed | **Ja** (Zwingend) | Nein (Opt-In) | Ja | Nein | Nein | Ja | Nein (Allow-all Default) |
| | Natives SQL RLS Pushdown | **Ja** (MSSQL, PG, SQLite, Databricks, Oracle) | ❌ Nein (Subgraph-Delegation) | **Ja** (Proprietär) | ❌ Nein | ❌ Nein | **Ja** (DB-Ebene) | ❌ Nein (Nur HTTP Path) |
| | Casbin ABAC / Dynamic Policy | **Ja** (`sub_rule`, Hot-Reload) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | Ja (Proprietär) | Teilweise (OPA Sidecar) |
| | Active Directory / Kerberos SID | **Ja** (Transitiv SIDs) | ❌ Nein (Nur Claims) | ❌ Nein | ❌ Nein | ❌ Nein | Teilweise | ❌ Nein |
| **Governance & Workflows** | Data-Owner-Consent Modell | **Ja** (Mandatorisch) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | Teilweise | ❌ Nein |
| | 4-Augen-Prinzip (Four-Eyes) | **Ja** (Integrierte Logik) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein |
| | Interaktive Challenge-Response | **Ja** (412 Precondition) | ❌ Nein (Nur 403) | ❌ Nein (Nur 403) | ❌ Nein (Nur 403) | ❌ Nein | ❌ Nein | ❌ Nein |
| | Justification-Driven Access | **Ja** (ServiceNow/Jira) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | Teilweise | ❌ Nein |
| | Break-Glass Emergency SRE | **Ja** (SOC Alert + Hash) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein |
| | Policy Simulator Sandbox | **Ja** (What-if Analyzer) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | Ja | ❌ Nein |
| **Datenschutz & Compliance** | DSGVO Art. 9 Automatisierung | **Ja** (Auto-Redaction) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | Teilweise | ❌ Nein |
| | DSGVO Art. 15 Auskunft-Report | **Ja** (QuestPDF Export) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein |
| | Revisionssichere Hash-Chains | **Ja** (HMAC-SHA256) | ❌ Nein (Standard Logs) | ❌ Nein | ❌ Nein | ❌ Nein | Ja | ❌ Nein |
| | WORM Storage S3/Azure Export | **Ja** (Object Lock) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | Teilweise | ❌ Nein |
| | Side-Channel Inference Defense | **Ja** (AST Filter Sperre) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | Teilweise | ❌ Nein |
| | Table Oracle Schema Masking | **Ja** (Generic FORBIDDEN) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein |
| **Konnektoren & Quellen** | Relationale SQL DBs | **Ja** (Direct ADO.NET) | ❌ Nein (Subgraphs nötig) | **Ja** (Nativ) | ❌ Nein | **Ja** | **Ja** | ❌ Nein |
| | Deklaratives REST mit SSRF-Schutz | **Ja** (DNS-Pre-Resolve) | Teilweise (Connectors) | Teilweise | Teilweise | **Ja** | ❌ Nein | **Ja** |
| | Apache Iceberg Lakehouse | **Ja** (v2 Pruning + Cache) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | Teilweise | ❌ Nein |
| | Subgraph Föderation | **Ja** (Fusion Router) | **Ja** (Federation v2) | Teilweise | **Ja** (Cosmo) | ❌ Nein | ❌ Nein | ❌ Nein |
| | Subgraph Dynamic Feature Flags | **Ja** (Header/Cookie/ABAC) | ❌ Nein (Nur Studio Varianten) | ❌ Nein | **Ja** (Graph Feature Flags) | ❌ Nein | ❌ Nein | Teilweise (Envoy Route) |
| **Konnektoren & Quellen** | Relationale SQL DBs | **Ja** (Direct ADO.NET) | ❌ Nein (Subgraphs nötig) | **Ja** (Nativ via NDC) | ❌ Nein | **Ja** | **Ja** | ❌ Nein |
| | Deklaratives REST mit SSRF-Schutz | **Ja** (DNS-Pre-Resolve) | Teilweise (Connectors) | Teilweise | Teilweise | **Ja** | ❌ Nein | **Ja** |
| | Apache Iceberg Lakehouse & IRC | **Ja** (v2 Pruning + REST Catalog) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | Teilweise | ❌ Nein |
| | Native Vector DBs (pgvector/Qdrant) | **Ja** (Standardisiertes SPI + RLS) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | Teilweise | ❌ Nein |
| | OData v4 Dual Access | **Ja** (Power BI/SAP) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein |
| | Nativer Apache Parquet Export | **Ja** (Columnar Snappy mit RLS & Masking) | ❌ Nein (Nur JSON) | ❌ Nein (Nur JSON) | ❌ Nein (Nur JSON) | ❌ Nein | ❌ Nein (Nur SQL Proxy) | ❌ Nein (Nur Raw HTTP) |
| | Native Apache Arrow Flight SQL Egress | **Ja** (Zero-Copy Multi-GB/s Stream) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein |
| | Governed WebSQL (HTTP SQL) | **Ja** (`POST /api/v1/sql` mit AST Linter & RLS) | ❌ Nein | ❌ Nein (Nur GraphQL) | ❌ Nein | ❌ Nein | ❌ Nein (Nur DB-Proxy) | ❌ Nein |
| | Single-Query SQL Pushdown | **Ja** (`FOR JSON PATH` / `json_agg` gegen N+1) | ❌ Nein (DataLoader/Subgraphs) | **Ja** (Nativ) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein |
| **Echtzeit & Streaming** | GraphQL Subscriptions | **Ja** (WS / SSE) | **Ja** | **Ja** | **Ja** (Cosmo Streams) | Teilweise | ❌ Nein | Teilweise |
| | In-Stream Casbin RLS Filtering | **Ja** (Pro Event) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein |
| | Zero-Kafka CDC (MSSQL & PG Output) | **Ja** (Change Tracking & Logical Repl)| ❌ Nein | Teilweise (Trigger-basiert) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein |
| **Data Mesh & Kataloge** | Microsoft Purview Sync | **Ja** (Mirror & Ref) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | Teilweise | ❌ Nein |
| | Collibra / Alation Integration | **Ja** (REST APIs) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | Teilweise | ❌ Nein |
| | dbt Contract & Health Gate | **Ja** (Circuit Breaker) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein |
| | OpenLineage Event Egress | **Ja** (Standard RunEvents) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein |
| **AI / Agentic Governance** | Model Context Protocol (MCP) | **Ja** (Stdio & SSE) | **Ja** (Apollo MCP Server) | ❌ Nein | **Ja** (Cosmo MCP Gateway) | ❌ Nein | ❌ Nein | ❌ Nein |
| | Curated Persisted Operations as Tools | **Ja** (Prompt-Injection Shield) | ❌ Nein (Rohe Schemas) | ❌ Nein | Teilweise (Safelists) | ❌ Nein | ❌ Nein | ❌ Nein |
| | Semantic Context & Few-Shot Grounding | **Ja** (Business Semantik & Golden Queries)| ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein |
| | Semantic Query Cache & FinOps Caps | **Ja** (Cosine Match + FOCUS v1.2) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein |
| | Prompt Injection Guardrail | **Ja** (OWASP LLM01) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein |
| | Dynamic PII Scrubbing für LLMs | **Ja** (Vor dem Tokenstream)| ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein |
| | Dynamic Differential Privacy ($(\epsilon, \delta)$) | **Ja** (Laplace/Gauß Rausch-Engine) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | **Ja** (SQL-Ebene) | ❌ Nein |
| | EU AI Act Art. 10 Compliance Trails | **Ja** (WORM Hash-Chains) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | Teilweise | ❌ Nein |
| **Edge & Caching** | CDN Cache-Tags (Surrogate-Keys) | **Ja** (Cloudflare/Fastly) | **Ja** | ❌ Nein | Teilweise | ❌ Nein | ❌ Nein | Teilweise |
| | Zero-Trust Cache Isolation | **Ja** (`private, no-store` + Hash Partition)| ❌ Nein (Manuell) | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein | ❌ Nein |
| | Distributed Token-Bucket / Fallback | **Ja** (Redis + Lock-free Mem)| Teilweise | Teilweise | Teilweise | ❌ Nein | ❌ Nein | **Ja** |
| **Lizenzmodell & TCO** | Lizenzmodell | **Open Enterprise / Autark (0 €)** | Restriktiv (ELv2 / GraphOS)| Active Model-Based Pricing | Open Source / Cloud | IBM Cloud Lockin | Sehr hohe Enterprise Fee | Open Source / Core |
| | Air-Gapped / On-Premise Eignung| **100% Autark (Kein Call-Home)**| Eingeschränkt (GraphOS Zwang)| Eingeschränkt | **Ja** | ❌ Cloud Only | **Ja** | **Ja** |

---

## 3. Detaillierter Wettbewerber-Vergleich

### 3.1 Apollo GraphQL (Federation v2.15+, GraphOS, Apollo Router v2.17+)

* **Marktstellung:** De-facto-Standard für GraphQL Subgraph Federation in Cloud-Native-Startups und Frontend-BFF-Teams. 2025/2026 erweiterte Apollo die Plattform um den **Apollo MCP Server**, Rust-basierte Schema-Composition (v2.15+) und Apollo REST Connectors. Router v1.x erreichte im Februar 2026 das offizielle End-of-Life (EOL).
* **Wo Apollo glänzt:**
  * Großes globales Entwickler-Ökosystem und umfangreiche Dokumentation.
  * Reife Implementierung von Subgraph-Stitching und Entity-Resolution via Rust-Router v2.
  * Direkte Anbindung von KI-Agenten über den Apollo MCP Server.
* **Kritische Lücken im Enterprise-Einsatz:**
  1. **Kein natives Data Governance & RLS:** Apollo besitzt kein Konzept für Data-Owner-Consents oder relationales Row-Level Security. Autorisierungslogik muss mühsam in jeden einzelnen Subgraph dupliziert werden.
  2. **Lizenz-Falle (ELv2):** Der Apollo Router steht unter der Elastic License v2, was Cloud-Hosting, Managed-Service-Angebote und interne Bereitstellung in Konzernen juristisch verkompliziert.
  3. **Keine Metadaten-Katalog-Integration:** Keine Unterstützung für automatische Synchronisation mit Microsoft Purview, Collibra oder Alation.
  4. **Kein DSGVO-Compliance-Stack & EU AI Act Governance:** Fehlen von revisionssicheren HMAC-SHA256 WORM Audit-Trails, automatisierten DSGVO-Art.-9-Schwärzungen und EU-AI-Act-Art.-10-Audit-Zertifikaten.
  5. **Semantik-Blindheit bei KI-Agenten:** Apollo MCP exponiert rohe Schemas ohne Geschäftssemantik, Formeln oder FinOps-Token-Caps (`F-AI-08`), was bei autonomen Agenten zu Halluzinationen und unkontrollierten Token-Kosten führt.
  6. **Reine JSON-Schnittstelle ohne Analytics Egress:** Apollo ist strikt auf Frontend-JSON beschränkt. Data Science Teams (DuckDB, Pandas, Polars) müssen riesige JSON-Payloads parsen, anstatt typsichere, komprimierte Apache Parquet- oder Arrow-Flight-Streams unter Einhaltung von Governance zu beziehen.

> **Autheris-Vorteil:** Hot Chocolate Fusion Föderation kombiniert mit nativer Data-Owner-Governance, echtem SQL RLS-Pushdown, nativer Apache Parquet/Arrow Bereitstellung für Data Science, semantischem MCP Tool Grounding und 100% autarker On-Premises-Betreibbarkeit ohne Lizenzgebühren.

---

### 3.2 Hasura Enterprise (DDN / Data Delivery Network v3)

* **Marktstellung:** Pionier für "Instant GraphQL" direkt auf relationalen Datenbanken. Mit Hasura DDN (v3) migrierte die Plattform auf eine entkoppelte Architektur mit einer schnellen Rust-basierten Data-Plane-Engine und der **Native Data Connectors (NDC)** Spezifikation sowie dem OpenDD-Framework.
* **Wo Hasura glänzt:**
  * Sehr schnelle Entwicklungszyklen für deklarative APIs über heterogene Datenbanken.
  * Standardisiertes NDC-Ökosystem mit Rust-Konnektoren für Sub-Millisekunden-Cold-Starts.
* **Kritische Lücken im Enterprise-Einsatz:**
  1. **Unkalkulierbares Active Model-Based Pricing:** Hasura DDN rechnet primär nach aktiven Datenmodellen ab ($\ge 1.000$ Hits/Monat). Bei dynamischen Schemas, Microservices und komplexen Enterprise-Domänen führt dies zu explodierenden und schwer budgetierbaren Lizenzkosten.
  2. **Massiver Vendor Lock-in:** Die gesamte Datenzugriffslogik wird in proprietäre Hasura OpenDD-Metadatenmodelle gesperrt. Eine Migration gleicht einem Total-Rewrite.
  3. **Fehlen von Enterprise-Workflows:** Keine Unterstützung für temporäre Berechtigungsdelegationen, interaktive 4-Augen-Freigabe-Challenges (Challenge-Response) oder Notfall-Break-Glass-Szenarien.
  4. **Kein dbt Contract Gate:** Hasura ignoriert Upstream-Testfehler (`run_results.json`) und liefert unbemerkt korrupte Daten an API-Consumer aus.
  5. **Kein nativer Columnar Analytics Egress:** Hasura unterstützt ausschließlich flache JSON-Streams via GraphQL/REST. Analytische Bulk-Exporte in Apache Parquet oder Arrow Flight für Data Warehousing oder Feature Stores existieren nicht.

> **Autheris-Vorteil:** Offene, standardisierte Clean Architecture auf .NET 10 Basis, Integration in ServiceNow/Jira, dbt Health Circuit Breaker, Parquet/Arrow-Analytics-Egress und 0 € Lizenzkosten ohne Modell- oder Core-Besteuerung.

---

### 3.3 WunderGraph / Cosmo

* **Marktstellung:** Moderne Open-Source-Alternative zu Apollo Federation auf Rust/Go-Basis. In 2025/2026 erweiterte Cosmo die Plattform um das **Cosmo MCP Gateway** mit Curated Persisted Operations, **Graph Feature Flags** (dynamisches Subgraph-Traffic-Routing) und **Cosmo Streams** (zentralisierte Router-Subscriptions).
* **Wo Cosmo glänzt:**
  * Schneller Router mit geringem Memory-Footprint und nativer OpenTelemetry-Integration.
  * Schutz vor Prompt Injection durch Einschränkung von KI-Agenten auf Curated Persisted Operations.
  * Graph Feature Flags für sichere Canary-Rollouts und progressive Subgraph-Deployments.
* **Kritische Lücken im Enterprise-Einsatz:**
  1. **Reiner Subgraph-Proxy ohne relationale Datenanbindung:** Cosmo kann nicht direkt mit relationalen Datenbanken, Vektordatenbanken oder Apache Iceberg Lakehouses sprechen; verlangt zwingend vorgeschaltete Microservices für jede Datenquelle.
  2. **Keine Enterprise-Compliance & Audit-Integrität:** Cosmo erzeugt normale Logzeilen, bietet aber keine kryptographisch verketteten SHA-256 Hash-Chains, die vor Gerichten oder Aufsichtsbehörden (BaFin, BSI) manipulationssicher sind.
  3. **Keine Legacy-Identitäts-Integration:** Keine Unterstützung für Windows Kerberos / SPNEGO Negotiate oder Active Directory SID-Hierarchien.
  4. **Fehlende EU AI Act & Differential Privacy Suite:** Keine mathematischen Re-Identifikationsgarantien für RAG oder analytische Abfragen.

> **Autheris-Vorteil:** Schlüsselfertige Hybrid-Identity (Kerberos + Entra ID), direkte Anbindung von SQL-Datenbanken, Vektor-DBs (pgvector/Qdrant/Milvus) und Iceberg Lakehouses, gerichtsverwertbare WORM-Audit-Trails und integrierte Dynamic Differential Privacy.

---

### 3.4 StepZen (IBM)

* **Marktstellung:** Deklaratives GraphQL-Gateway für SaaS- und REST-APIs, 2023 von IBM übernommen.
* **Wo StepZen glänzt:**
  * Schnelle deklarative Konfiguration via benutzerdefinierte GraphQL-Direktiven (`@dbquery`, `@rest`).
* **Kritische Lücken im Enterprise-Einsatz:**
  1. **Starke IBM-Cloud-Bindung:** Zunehmende Kopplung an das IBM Hybrid-Cloud-Ökosystem und Red Hat OpenShift.
  2. **Geringe Flexibilität für benutzerdefinierte Algorithmen:** Keine Möglichkeit, tiefgreifende In-Memory-Maskierungsengines, Differential Privacy oder komplexe Casbin-ABAC-Regeln einzubinden.
  3. **Keine Lakehouse-Fähigkeiten:** Völlig ungeeignet für analytische Workloads oder Apache Iceberg Parquet-Pruning.

> **Autheris-Vorteil:** Unabhängigkeit von Hyperscalern, volle Erweiterbarkeit via C# In-Process Middlewares und nativer Iceberg Lakehouse Connector.

---

### 3.5 Data Security Suites (Immuta, Privacera)

* **Marktstellung:** Marktführer für Data Governance, Fine-Grained Access Control und Data Privacy direkt in Data Warehouses (Snowflake, Databricks, Redshift).
* **Wo Immuta/Privacera glänzen:**
  * Exzellente, zentrale Policy Engines für SQL-Analysten in BI- und Data-Science-Tools.
* **Kritische Lücken im Enterprise-Einsatz:**
  1. **Kein API- oder GraphQL-Gateway:** Immuta schützt die Datenbank, bietet aber keine Schnittstelle für moderne Web-, Mobile- oder Microservice-Applikationen.
  2. **Hohe Latenz & Komplexität:** Setzt tief als DB-Treiber oder Proxy an; erfordert für jeden Entwickler komplexe SQL-ODBC/JDBC-Verbindungen.
  3. **Kein HTTP-basierter Parquet-Download für Data Science:** Immuta kontrolliert SQL-Queries in Warehouses, stellt aber keinen API-Endpunkt für On-Demand-Streaming von Parquet-Dateien an moderne Data-Science-Pipelines (Polars, DuckDB) bereit.

> **Autheris-Vorteil:** Autheris fungiert als **Unified Access Layer**, der Immuta-ähnliche Richtlinien direkt an die GraphQL-, OData- und Parquet-Schnittstelle bringt und dadurch Entwicklern, BI-Tools, Data Scientists und KI-Agenten denselben Schutz bietet.

---

### 3.6 Klassische API Gateways (Tyk.io, Kong Enterprise, Envoy Gateway)

* **Marktstellung:** Etablierte Enterprise API Gateways für HTTP/REST-Traffic-Management, Ratelimiting und OAuth-Absicherung.
* **Wo klassische Gateways glänzen:**
  * Ausgereifte Entwicklerportale, globale Ingress-Verwaltung und hohe HTTP/1- und HTTP/2-Reverse-Proxy-Durchsätze.
* **Kritische Lücken im Enterprise-Einsatz:**
  1. **Der "Double-Hop-Flaschenhals" bei Coprozessen:**
     * Um Speziallogik auszuführen, schalten Tyk und Envoy externe Coprozesse via gRPC ein (`ext_proc`).
     * Jeder Request erfordert **zwei zusätzliche IPC/Netzwerk-Hops** und **vierfache Protobuf-Serialisierung**, was die P99-Latenz um 1 bis 5 ms verschlechtert und bei großen GraphQL-Payloads zu erheblichem Speicher-Overhead führt.
  2. **Kein GraphQL AST-Kontext:**
     * Klassische Gateways sehen nur flachen JSON-Text. Sie können keine RLS-Filter in den relationalen AST injizieren und müssen Egress-Maskierungen über teures Re-Parsing der fertigen JSON-Antwort durchführen.
  3. **Reines Binär-Modell (Allow/Deny):**
     * Keine Möglichkeit, interaktive Workflows (4-Augen-Freigabe, ServiceNow-Challenge, zeitbegrenzte Delegationen) abzuwickeln.

> **Autheris-Vorteil:** Nativer In-Process C# Hot Path mit direktem Zugriff auf Hot Chocolate AST-Knoten ohne IPC-Hops, ergänzt durch integrierte Workflow-Orchestrierung.

---

## 4. Architektur-Vergleich: Ingress/Egress-Pipeline & RLS Pushdown

### 4.1 Ingress/Egress Customizing: In-Process C# vs. Out-of-Process gRPC

```mermaid
flowchart TD
    subgraph Client ["Client HTTP / GraphQL Request"]
        C_REQ["GraphQL Query / Mutation"]
    end

    subgraph Klassisch ["Klassisches Gateway (Tyk / Envoy Modell)"]
        direction TB
        K_ING["Ingress Hook"]
        K_CORE["HTTP Proxy Core (Kein GraphQL AST)"]
        K_EGR["Egress Hook"]
        
        GRPC_ING["Externer gRPC Coprozess (Auth/Validation)"]
        GRPC_EGR["Externer gRPC Coprozess (JSON Re-Parsing Masking)"]
        
        K_ING -.->|Hop 1: IPC + Protobuf (+1-2ms)| GRPC_ING
        GRPC_ING -.->|Hop 2: Response| K_ING
        K_ING --> K_CORE --> K_EGR
        K_EGR -.->|Hop 3: IPC + Large JSON (+2-3ms)| GRPC_EGR
        GRPC_EGR -.->|Hop 4: Masked Data| K_EGR
    end

    subgraph AutherisEngine ["Autheris (In-Process Hot Path Modell)"]
        direction TB
        G_ING["In-Process C# Middleware (Zero-Copy Span)"]
        G_AST["AST Engine: Casbin ABAC + SQL RLS Pushdown"]
        G_EGR["In-Process C# Egress (Direct Memory Masking)"]
        
        G_ING ===|0 IPC Hops (< 0.1ms)| G_AST
        G_AST ===|Zero-Allocation| G_EGR
    end

    C_REQ --> K_ING
    C_REQ --> G_ING
```

---

### 4.2 Autorisierungs-Architektur: RLS Pushdown vs. Subgraph-Kaskade

```mermaid
sequenceDiagram
    autonumber
    actor User as Client / Consumer
    participant GW as Gateway
    participant Sub1 as Subgraph / DB 1
    participant Sub2 as Subgraph / DB 2

    Note over GW,Sub2: Traditioneller Subgraph Router (Apollo / Cosmo)
    User->>GW: Query: { user { orders { total } } }
    GW->>Sub1: Fetch User (Ohne RLS Kontext)
    Sub1-->>GW: 1000 User Records (Volle Payload)
    GW->>Sub2: Fetch Orders for 1000 Users
    Sub2-->>GW: 50.000 Orders
    GW->>GW: Veraltete In-Memory Filterung / Hohe Latenz & Memory Penalty

    Note over GW,Sub2: Autheris Zero-Trust RLS Pushdown
    User->>GW: Query: { user { orders { total } } }
    GW->>GW: Casbin ABAC evaluiert Mandant & Berechtigung (< 0.5ms)
    GW->>Sub1: Pushdown SQL: SELECT ... WHERE tenant_id = @p0 AND region IN (...)
    Sub1-->>GW: Exakt 1 autorisierter Record
    GW->>Sub2: Pushdown SQL: SELECT ... WHERE user_id = @p0 AND order_date >= @p1
    Sub2-->>GW: Exakt 5 autorisierte Orders (Minimale I/O & Network)
    GW-->>User: Ergebnis mit gerichtsverwertbarem Audit-Hash
```

---

## 5. SWOT-Analyse: Autheris im Enterprise-Wettbewerb

```mermaid
quadrantChart
    title Strategische SWOT-Matrix für Autheris
    x-axis "Interner Fokus" --> "Externer Markt"
    y-axis "Herausforderung / Risiko" --> "Erfolgsfaktor / Chance"
    quadrant-1 "OPPORTUNITIES (Marktchancen)"
    quadrant-2 "STRENGTHS (Interne Stärken)"
    quadrant-3 "WEAKNESSES (Herausforderungen)"
    quadrant-4 "THREATS (Marktrisiken)"
    "C# 14 / .NET 10 High Performance": [0.22, 0.88]
    "Integrierte Zero-Trust Governance": [0.15, 0.95]
    "DSGVO Art. 9 & 4-Augen Workflow": [0.10, 0.82]
    "Audit-Trail HMAC Hash-Chains": [0.28, 0.75]
    "Kleineres Open-Source Ökosystem als Apollo": [0.25, 0.22]
    "Noch kein Standalone Web UI Dashboard": [0.12, 0.32]
    "Enterprise Data Mesh & AI Governance Boom": [0.78, 0.92]
    "Ablöse teurer Hasura/Apollo Lizenzen": [0.88, 0.82]
    "Data Catalog Federation Standard (Purview/Collibra)": [0.70, 0.85]
    "EU AI Act Art. 10 & Differential Privacy Mandat": [0.84, 0.94]
    "Iceberg REST Catalog Convergence (Polaris/Unity)": [0.76, 0.89]
    "Apollo erweitert Router um native ABAC": [0.82, 0.30]
    "Hyperscaler bieten native Managed Data Gateways": [0.74, 0.18]
```

### Stärken (Strengths)
1. **Unübertroffene Performance:** .NET 10, Zero-Allocation Spans, L1/L2 Cache-Architektur mit P99 < 15ms.
2. **Umfassender Compliance-Stack:** Manipulationssichere HMAC-SHA256 Hash-Chains, automatisierte DSGVO Art. 9 Maskierung, EU-AI-Act-Art.-10-Auditierung und Dynamic Differential Privacy.
3. **Enterprise Data Integration:** Schlüsselfertige Konnektoren für SQL (MSSQL, Postgres, Oracle), Vektor-DBs (pgvector, Qdrant, Milvus), Apache Iceberg Lakehouses, dbt und führende Datenkataloge (Purview, Collibra).
4. **Agentic-AI Ready:** Eingebauter MCP Server mit Prompt-Injection-Guardrails (Curated Persisted Tools), FOCUS FinOps Budget-Caps und In-Stream PII-Scrubbing schützt Daten vor LLM-Missbrauch.

### Schwächen (Weaknesses)
1. **Markenbekanntheit:** Geringere Bekanntheit im reinen Web-Frontend-Segment verglichen mit Apollo GraphQL.
2. **Headless:** Aktuell noch kein Standalone Web UI Management Dashboard (für P6 geplant).

### Chancen (Opportunities)
1. **Flucht vor Lizenzkosten:** Unternehmen suchen aktiv nach Alternativen zu Apollos restriktiver ELv2-Lizenz und Hasuras neuem, schwer kalkulierbarem *Active Model-Based Pricing*.
2. **Strenge Regulierung in EU & USA:** Vollzug des EU AI Act (Ende 2026), NIS-2, DORA und DSGVO zwingen Finanz-, Industrie- und Gesundheitsunternehmen zu nachweisbarer Governance und Hash-Audit-Trails.
3. **Modern Lakehouse Convergence:** Breite Industrie-Standardisierung auf den Apache Iceberg REST Catalog (IRC) und Apache Arrow Flight SQL für Hochdurchsatz-Analysen ohne Vendor-Lockin.
4. **Data Mesh & dbt Boom:** Wachsender Bedarf an Gateways, die Data Engineering (dbt, Iceberg) und API-Konsumenten ohne Informationsverlust verbinden.

### Risiken (Threats)
1. **Feature-Nachzug etablierter Player:** Apollo oder Cosmo könnten versuchen, rudimentäre ABAC-Plugins nachzurüsten.
2. **Cloud-Hyperscaler:** AWS oder Microsoft könnten eigene proprietäre GraphQL-Lakehouse-Gateways auf den Markt bringen.

---

## 6. Total Cost of Ownership (TCO) & Wirtschaftlichkeitsanalyse

| Kostenfaktor | Apollo GraphOS Enterprise | Hasura Enterprise / DDN | Tyk / Kong Enterprise | Autheris |
| :--- | :--- | :--- | :--- | :--- |
| **Lizenzmodell** | $1.500+ / Monat Basis + Operation-Tax (Cloud, ELv2)| Active Model-Based Pricing + Core Fee ($30k - $150k+ / Jahr) | $18.000 - $60.000+ / Jahr (Node-basiert) | **0 € Lizenzgebühren (Open Enterprise / Autark)** |
| **Air-Gapped On-Premises** | Sehr teuer / erfordert Sonderverträge | Extrem restriktiv lizenziert | Teure Add-ons erforderlich | **100% autark ohne Internet-Zwang / Phoning-Home** |
| **Infrastruktur-Footprint** | Mittel (Rust Router v2) | Hoch (Rust NDC + Metadata Control Plane) | Hoch (Zusätzliche gRPC Sidecars & Proxies) | **Extrem gering (Kompakter .NET 10 Container, < 150 MB RAM)** |
| **Entwicklungs- & Integrationsaufwand** | Hoch (RLS & Governance muss in Subgraphs gebaut werden) | Mittel (Proprietäre OpenDD-Metadaten) | Hoch (Eigene gRPC-Coprozesse müssen gewartet werden) | **Minimal (Schlüsselfertige Konnektoren, Purview-Sync, C# DI)** |
| **Compliance- & Audit-Risiko** | Hoch (Kein Manipulationsschutz, Gefahr von DSGVO-Strafen) | Mittel (Proprietäre Logs, kein EU AI Act Art. 10) | Hoch (Keine Hash-Chains) | **Minimal (Revisionssichere HMAC-Chains, DSGVO Art. 15 & EU AI Act)** |

---

## 7. Fazit & Handlungsempfehlung

Für Konzerne und regulierte Organisationen, die vor der Entscheidung zwischen Apollo, Hasura und Autheris stehen, ist das Ergebnis eindeutig:

* **Apollo GraphOS** eignet sich primär für reine Frontend-Entwicklerteams, die ausschließlich Microservice-APIs zusammenstecken wollen und keine relationalen Datenbanken, Lakehouses oder DSGVO-Art.-9-/EU-AI-Act-Vorgaben verwalten müssen.
* **Hasura Enterprise / DDN** bietet schnelles Prototyping, bindet das Unternehmen jedoch an proprietäre OpenDD-Metadatenmodelle und ein kontroverses, dynamisch eskalierendes Active-Model-Preismodell.
* **Autheris ist die optimale Wahl für Enterprise-Plattformen**, die höchste Performance (.NET 10), echte Zero-Trust-Datenhoheit (Data-Owner-Consent), föderierte Datenkataloge (Purview/Collibra), dbt Data-Mesh-Absicherung, offene Lakehouse-Standards (Iceberg REST Catalog / Arrow Flight) und zukunftssichere KI-Agenten-Governance (MCP & Differential Privacy) fordern — bei vollständiger Daten- und Betriebs-Souveränität und 0 € Lizenzkosten.
