# 📋 Autheris & Autheris.Extensions — Vollständige Enterprise Feature-Liste

**Dokument-Version:** 2.0 (General Availability)  
**Autor:** Principal Enterprise Product Manager & Platform Strategist  
**Plattform:** .NET 10 | C# 14 | Hot Chocolate 16 | Garnet / Redis | Hot Chocolate Fusion  
**Architektur:** Clean / Onion Architecture, Zero-Trust Data-Owner-Consent Engine  

---

## Executive Summary

**Autheris** ist ein zentrales, hochperformantes und hochsicheres Enterprise GraphQL Gateway für heterogene Unternehmensdatenlandschaften. Anstelle traditioneller, statischer Rollenmodelle (RBAC) setzt Autheris auf ein striktes **Zero-Trust Data-Owner-Consent-Modell**: Kein Byte verlässt das Gateway ohne aktive, zeitlich befristete, zweckgebundene und durch Data Owner autorisierte Freigabe.

Die Lösung vereint föderierte GraphQL-Abfragen über relationale Datenbanken, Data Lakes, moderne Iceberg-Lakehouses, REST-APIs und Subgraph-Verbünde mit tiefer Data Catalog Federation (Microsoft Purview, Collibra, Alation, OpenMetadata), dbt Data-Mesh-Governance, Echtzeit-Event-Streaming mit In-Stream RLS und nativer Agentic-AI-Absicherung via Model Context Protocol (MCP).

---

## Inhaltsverzeichnis

1. [Core Runtime & High-Performance Architecture](#1-core-runtime--high-performance-architecture)
2. [Enterprise Identity, Ingress & ForwardAuth](#2-enterprise-identity-ingress--forwardauth)
3. [Zero-Trust Data-Owner-Consent Governance & Workflows](#3-zero-trust-data-owner-consent-governance--workflows)
4. [Dynamic Access Control Engine (Casbin ABAC & SQL RLS Pushdown)](#4-dynamic-access-control-engine-casbin-abac--sql-rls-pushdown)
5. [Column-Level Data Masking, Privacy & Side-Channel Defense](#5-column-level-data-masking-privacy--side-channel-defense)
6. [Heterogeneous Data Connectors & Lakehouse](#6-heterogeneous-data-connectors--lakehouse)
7. [Real-Time Event Streaming & CDC (Change Data Capture)](#7-real-time-event-streaming--cdc-change-data-capture)
8. [Hot Chocolate Fusion & Subgraph Federation](#8-hot-chocolate-fusion--subgraph-federation)
9. [Enterprise Data Catalog Federation & Compliance](#9-enterprise-data-catalog-federation--compliance)
10. [dbt Data Mesh & Contract Governance](#10-dbt-data-mesh--contract-governance)
11. [Enterprise Extensibility Framework (Dual-Mode C# / gRPC)](#11-enterprise-extensibility-framework-dual-mode-c--grpc)
12. [Agentic AI & Model Context Protocol (MCP) Gateway](#12-agentic-ai--model-context-protocol-mcp-gateway)
13. [Cryptographic Compliance & Tamper-Evident Audit Trail](#13-cryptographic-compliance--tamper-evident-audit-trail)
14. [Edge Performance, CDN Caching & Traffic Management](#14-edge-performance-cdn-caching--traffic-management)
15. [Operations, High Availability & Enterprise Resilienz](#15-operations-high-availability--enterprise-resilienz)

---

## 1. Core Runtime & High-Performance Architecture

* **Modernste .NET 10 & C# 14 Basis**:
  * Entwickelt auf der neuesten .NET 10 LTS-Laufzeitumgebung unter Ausnutzung modernster C# 14 Sprachfeatures (Primary Constructors, ref struct, Inline Arrays, Collection Expressions, Pattern Matching).
  * Kompiliert mit striktem `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (0 Warnungen, 0 Fehler).
* **Hot Chocolate 16 GraphQL Engine**:
  * Dynamic Schema Building & Execution Engine der Spitzenklasse (`HotChocolate.AspNetCore` 16.6.7).
  * Dynamische Typ-Projektion basierend auf dem aktiven Governance-Katalog.
  * Native Unterstützung von Queries, Mutations und Realtime Subscriptions (`graphql-transport-ws`, SSE).
* **Zero-Allocation Hot Paths**:
  * Durchgängiger Einsatz von `ReadOnlySpan<char>`, `ReadOnlySpan<byte>` und `stackalloc` für sicherheitskritische String- und Hash-Operationen.
  * Format-preserving String-Maskierung via `string.Create(..., (span, state) => ...)` ohne Zwischenallokationen auf dem Garbage-Collection-Heap.
* **Zweistufige High-Throughput Caching-Architektur**:
  * **L1 In-Memory Cache**: `IMemoryCache` mit lock-freien Tabellen-Indexen für Sub-Millisekunden-Lookups (< 0.5 ms SLA).
  * **L2 Distributed Cache**: Microsoft Garnet / Redis Cluster mit schnellen Pipeline-Batch-Abfragen und asynchronem Multiplexing.
  * **Monotonische Policy-Epochen**: Bei jeder Governance- oder Consent-Änderung inkrementiert das Gateway atomar die Tabellen-Policy-Epoche. Veraltete Cache-Einträge verfallen pod-übergreifend augenblicklich ohne Cache-Stampedes.
* **MemoryPack & High-Speed Serialisierung**:
  * Unterstützung von `MemoryPack` für binäre, zero-copy State-Serialisierung in Redis für maximale Durchsätze bei minimalem CPU-Overhead.
* **Clean / Onion Architecture**:
  * Strikte Schichtenarchitektur (`Domain`, `Application`, `Infrastructure`, `GraphQL`, `Api`, `Extensions`).
  * Überprüft durch automatisierte NetArchTest-Architekturtests im Build-Prozess.

---

## 2. Enterprise Identity, Ingress & ForwardAuth

* **Smart Dynamic Scheme Selector**:
  * Automatische, header-gesteuerte Protokoll-Arbitrierung zur Laufzeit zwischen ForwardAuth, Entra ID Bearer, HTTP Basic und Kerberos Negotiate.
* **Kubernetes Ingress & Traefik ForwardAuth**:
  * Native Integration mit Kubernetes Ingress Controllern (Traefik, NGINX) und vorgeschalteten ForwardAuth-Diensten (Authelia, Keycloak, Authentik, OAuth2-Proxy).
  * Validierung von Proxy-Netzwerken gegen konfigurierte CIDR-Blöcke (`TrustedNetworks`, `TrustedProxies`) zum Schutz gegen `X-Forwarded-For`-Spoofing.
  * Schutz vor Header-Injektionen durch zeitkonstante Verifikation kryptographischer Pre-Shared Secrets (`X-Forwarded-Secret`).
  * Sichere Extraktion von `X-Forwarded-User`, `X-Forwarded-Groups`, `X-Forwarded-Roles` und `X-Forwarded-Email`.
* **Microsoft Entra ID (Azure AD) & AD FS JWT Bearer**:
  * Native Validierung von OIDC/OAuth2-Bearer-Tokens gegen Azure AD / Entra ID Mandanten.
  * `EnterpriseClaimsTransformation`: Normalisiert heterogene Enterprise-Claims (`oid`, `sub`, `onprem_sid`, `primarygroupsid`, Rollen) in kanonische Windows `Sid` Value Objects.
* **HTTP Basic Authentication & `/api/auth/login`**:
  * Direkte Basic-Auth-Unterstützung für GraphQL-Queries sowie dedizierter Login-Endpoint (`GET`/`POST /api/auth/login`).
  * Produktionssichere Passwort-Prüfung mittels gesalzenem PBKDF2 (`$pbkdf2$...`) mit HMAC-SHA256 und 100.000 Iterationen.
  * **Timing-Attack Parität**: Bei unbekannten Benutzern führt das Gateway eine identische Dummy-PBKDF2-Iteration aus, um Side-Channel Zeitmessungen zur Benutzer-Enumeration unmöglich zu machen. Alle Vergleiche erfolgen via `CryptographicOperations.FixedTimeEquals`.
* **Kerberos / SPNEGO Negotiate**:
  * Windows Integrated Authentication (WIA) für On-Premises-Umgebungen mit Kerberos-Only-Durchsetzung und automatischer Auflösung von Domain-Gruppen-SIDs.
* **Machine-to-Machine (M2M) Service Principals**:
  * Dedizierter Authentifizierungsflow für automatisierte Batch-Jobs und Microservices via Client-Credentials und mutual TLS (mTLS).
  * Trennung interaktiver Benutzer-SIDs von System-SIDs (`SP-<client_id>`).
* **Entwicklungs-Authentifizierungs-Simulator**:
  * Ermöglicht im `Development`-Modus die Simulation beliebiger SIDs, Rollen und Gruppen über HTTP-Header (`X-Test-User-Sid`, `X-Test-Roles`, `X-Test-Groups`), in Produktion hardgecodet deaktiviert.

---

## 3. Zero-Trust Data-Owner-Consent Governance & Workflows

* **Default Fail-Closed Governance**:
  * Kein Zugriff ohne aktive, gültige Freigabe. Jede Tabelle, Spalte oder Zeile, für die kein autorisierter Consent existiert, wird mit `FORBIDDEN` abgewiesen.
* **Active Directory Security Identifiers (SID)**:
  * Durchgängige Autorisierung basierend auf kryptographisch unveränderlichen Active Directory Windows Security Identifiers für Benutzer und Sicherheitsgruppen (`S-1-5-21-...`).
  * Automatische transitive Gruppenauflösung: Gewährte Gruppen-Consents vererben sich sofort an alle Gruppenmitglieder.
* **Four-Eyes-Approval Workflow (Vier-Augen-Prinzip)**:
  * Zwingend für Tabellen mit Sensitivitätsstufe `HIGH` oder DSGVO-Art.-9-Klassifizierung.
  * Funktionale Funktionstrennung (Segregation of Duties): Antragsteller dürfen ihre eigenen Anträge niemals genehmigen; doppelte Genehmigungen desselben Approvers werden abgewiesen.
* **Zeitbegrenzte Vertretungsregelungen (Delegationen)**:
  * Nahtlose Urlaubs- und Abwesenheitsübergabe (`DATA_OWNER_DELEGATIONS`): Data Owner können ihre Genehmigungsbefugnis für feste Zeitfenster an Stellvertreter delegieren.
* **Justification-Driven Access (Begründungsbasierte Zugriffe)**:
  * Client übergibt Ticket-ID oder Begründung (`X-Access-Justification: INC-88219`).
  * Ingress-Hook verifiziert die Ticket-Gültigkeit in Echtzeit gegen ServiceNow oder Jira.
* **Interaktive 4-Augen Challenge & Response**:
  * Statt eines statischen 403-Fehlers liefert das Gateway bei fehlender Freigabe einen strukturierten Challenge-Response (`412 Precondition Failed` / `ConsentRequired`) inklusive Freigabe-URL und Workflow-ID.
  * Nach Freigabe durch den Data Owner invalidiert ein Redis-Event die Policy-Epoche; die Wiederholung der Query gelingt transparent.
* **Break-Glass Notfall-Zugriff**:
  * Für Notfall-SREs (`X-Break-Glass: true`): Sofortige Freischaltung ohne Vorab-Genehmigung bei gleichzeitiger automatischer Alarmierung des Security Operations Center (SOC) und lückenlosem Egress-Audit-Hashing.
* **DSGVO-Rezertifizierungs-Workflows**:
  * `ConsentRecertificationHostedService`: Automatisierte zyklische Prüfung (z. B. alle 30 Tage) bestehender Freigaben mit Benachrichtigung der Data Owner und automatischem Widerruf nicht rezertifizierter Zugriffe.
* **Policy Simulation Sandbox**:
  * Ermöglicht Data Stewards und Security Officers das risikolose Testen von Berechtigungen im "What-if"-Modus vor dem eigentlichen Rollout.

---

## 4. Dynamic Access Control Engine (Casbin ABAC & SQL RLS Pushdown)

* **Casbin ABAC & RBAC Engine**:
  * Hocheffiziente Richtlinienauswertung mit dynamischer Regelauswertung (`sub_rule`).
  * Hot-Reloading (`ReloadPoliciesAsync`) zur Laufzeit via `ReaderWriterLockSlim` ohne Pod-Neustarts.
  * Standalone Policy-Linting-Tool (`tools/casbin-policy-lint`) zur statischen Validierung von Richtliniendateien in der CI/CD-Pipeline.
* **Natives SQL Row-Level Security (RLS) Pushdown**:
  * Anstelle von ineffizientem nachträglichem Filtern im Speicher generiert die Engine dynamische, parametrisierte SQL-Fragmente (`CombinedRowFilterSql`), die direkt in die `WHERE`-Klausel der Ziel-Datenbank gepusht werden.
  * Nicht autorisierte Zeilen verlassen die Datenbank-Engine zu keinem Zeitpunkt.
* **Multi-Dialekt RLS-Unterstützung**:
  * **Microsoft SQL Server (T-SQL)**: Optimierte Parameterbindung mit `@p...`
  * **PostgreSQL (PL/pgSQL)**: Native Typprüfung und Parameterisierung mit `$1, $2...`
  * **SQLite**: In-Memory- und Datei-basierte RLS-Klauseln mit `@...`
  * **Databricks SQL**: Cloud-Data-Warehouse-kompatible Filterbedingungen
  * **Oracle**: Dialektgerechte SQL-Expressions
* **Komplexe Filter-Operatoren**:
  * Gleichheit, Ungleichheit, Bereichsabfragen (`<`, `>`, `<=`, `>=`).
  * Mengenoperatoren (`IN`, Tupel-`IN`).
  * Zeitliche Gültigkeitsfilter (`valid_from <= @now AND valid_to >= @now`).
  * Parametrisierte Subqueries (`EXISTS (SELECT 1 FROM ...)`) für dynamische Verknüpfungen.
* **RLS-Propagation über DataLoaders**:
  * Konsistente Weitergabe aller Zeilenfilter über hierarchische Vater-Kind-Relationen im GraphQL-Query-Tree.
  * Adaptive Chunking-Strategie zum Schutz gegen datenbankspezifische Parameter-Limits (SQLite: 999, Oracle: 1000, MSSQL: 2100, PostgreSQL/Databricks: 10000).

---

## 5. Column-Level Data Masking, Privacy & Side-Channel Defense

* **Granulare Spaltenrichtlinien**:
  * `CLEAR`: Unveränderte Durchleitung für autorisierte Rollen.
  * `MASK`: Format-preserving Maskierung (z. B. Kreditkarten `************1234`, E-Mail `j***e@domain.com`).
  * `HMAC_SHA256`: Reversible oder kryptographisch deterministische Pseudonymisierung unter Verwendung von Stack-Speicher.
  * `NULLIFY`: Ersetzung sensibler Felder durch `null`.
  * `REDACT`: Vollständige Schwärzung mit `[REDACTED]`.
* **Rule-5 Side-Channel Inference Defense**:
  * Schutz vor statistischen Binärsuch- und Inferenzangriffen: GraphQL-AST-Inspektion prüft eingehende `where`- und `filter`-Argumente.
  * Befindet sich eine Spalte im Status `MASK` oder `DENY`, wird jede Filterbedingung auf diese Spalte sofort mit einer `SecurityException` abgewiesen. Angreifer können Datenwerte nicht über Filter-Laufzeiten oder Treffermengen erraten.
* **Table Oracle Defense**:
  * Der `ErrorSanitizingFilter` fängt `TableNotFoundException` in Produktionsumgebungen ab und maskiert sie als generischen `FORBIDDEN`-Fehler. Angreifer können das Schema nicht durch systematische Fehlertests ausspionieren.
* **Differential Privacy Engine**:
  * Dynamische Rausch-Injektion (Epsilon-Differential Privacy) auf aggregierte statistische Abfragen (COUNT, AVG, SUM), um Rückschlüsse auf Einzelpersonen zu verhindern.
* **Anti-CSRF Preflight Schutz**:
  * Zwingende Prüfung des `GraphQL-Preflight: 1`-Headers auf allen zustandsverändernden und lesenden Endpoints verhindert Cross-Site Request Forgery via Standard-Browser-Formularen.
* **ReverseProxy Anti-Spoofing**:
  * Strikte Absicherung via `ReverseProxyOptions` mit expliziten `KnownIPNetworks` (`System.Net.IPNetwork`) verhindert das Fälschen von Client-IPs über manipulierte `X-Forwarded-For`-Header.

---

## 6. Heterogeneous Data Connectors & Lakehouse

* **Natives SQL Datenquellen-Subsystem**:
  * Hochperformante ADO.NET-Verbindungen über `ISqlConnectionFactory` für MSSQL, PostgreSQL, SQLite, Databricks und Oracle.
* **Declarative REST Data Source Engine (Pattern 3)**:
  * Deklarative Einbindung externer REST-APIs mit URL-Template-Ersetzung (`/api/v1/customers/{id}`).
  * Header- und Query-Pushdown (`X-Tenant-Id`, `X-User-Sid`), Bearer-Token-Forwarding und API-Key-Injektion.
  * JSONPath-basierte Payload-Extraktion.
  * Adaptive Batching-Strategien: `QueryParameterList`, `JsonBodyArray`, `ParallelSingleRequests` (gedrosselt via `SemaphoreSlim`).
  * **Integrierte SSRF-Verteidigung**: DNS-Vorabauflösung blockiert RFC 1918 (private Netze), Link-Local (169.254.x.x), Loopback (127.0.0.1) und Cloud-Metadaten-Endpunkte (AWS/Azure/GCP).
  * Automatische Weiterleitungs-Blockade (`AllowAutoRedirect = false`).
* **Apache Iceberg v2 Lakehouse Connector (Pattern 4)**:
  * Nativer Direktzugriff auf Data-Lakehouse-Tabellen im Apache Iceberg v2 Format.
  * **Vektorisierte Metadaten- & Partitions-Pruning**: Liest Iceberg-Snapshots und Manifest-Dateien; filtert Parquet-Dateien anhand von Spalten-Min/Max-Statistiken vor dem eigentlichen Datenzugriff aus.
  * **Multi-Cloud Storage Provider**: Schlüsselfertige Provider für lokales Dateisystem, Amazon S3 (SigV4 signierte Anfragen) und Azure Blob Storage.
  * **L1 Manifest-Cache**: Konfigurierbarer Cache (`MetadataCacheTtlMinutes`) für Iceberg-Metadaten zur Minimierung von Cloud-Storage-List-Operationen.
  * Voll integriert in die Zero-Trust RLS- und Maskierungs-Pipeline.
* **OData v4 Dual-Access Schnittstelle**:
  * Parallele Bereitstellung der Datenmodelle als OData v4 Endpoints.
  * Ermöglicht Enterprise-Reporting-Tools (Power BI, Tableau, Microsoft Excel, SAP) den direkten Datenzugriff unter denselben RLS- und Maskierungsregeln wie GraphQL.
* **Nativer Apache Parquet Analytics Egress**:
  * Bereitstellung von columnar Apache Parquet Datenauszügen direkt aus relationalen SQL-Datenbanken (MSSQL, PostgreSQL, SQLite) und Lakehouse-Tabellen.
  * Kompatibel mit modernen Data Science Stacks: Python (Pandas, Polars), DuckDB, R und PySpark.
  * Vollständige Wahrung der Zero-Trust Consent Governance: RLS-Pushdown und In-Stream Spaltenmaskierung (z. B. PII-Redaction, HMAC-Hashing) greifen auch beim Parquet-Export.
* **Isoliertes Plugin-System**:
  * Laden von Third-Party-Konnektoren in isolierten, entladbaren `AssemblyLoadContext`-Instanzen (`IHttpDataSourcePlugin`) verhindert Versionskonflikte von Abhängigkeiten mit dem Gateway-Host.

---

## 7. Real-Time Event Streaming & CDC (Change Data Capture)

* **GraphQL Realtime Subscriptions**:
  * Volle Unterstützung von WebSocket-basierten Subscriptions (`graphql-transport-ws` Protokoll) und Server-Sent Events (SSE).
* **WebSocket Connection Authentication**:
  * `WebSocketAuthInterceptor`: Validiert Zugriffs-Tokens während der `connection_init`-Phase und etabliert einen sicheren Benutzerkontext für die gesamte Dauer der Verbindung.
* **In-Stream Row-Level Security (RLS)**:
  * `StreamRlsPolicyEnforcer`: Jeder einzelne übertragene Datensatz wird in Echtzeit gegen die aktiven Casbin-ABAC-Regeln des abonnierenden Benutzers geprüft.
  * Ändert sich der Berechtigungsstatus, filtert der Stream unberechtigte Events augenblicklich heraus.
* **In-Stream Column Masking**:
  * Dynamische Maskierung sensibler Felder direkt im Payload des Event-Streams vor der Serialisierung an den WebSocket-Client.
* **Debezium & Kafka CDC Ingestion**:
  * `DebeziumCdcParser`: Verarbeitet Change Data Capture (CDC) Payloads von Kafka/Debezium (`op: c, u, d`), extrahiert Vorher/Nachher-Zustände und leitet sie in die Gateway-Subscription-Kanäle weiter.
* **Strikte Mandantentrennung im Stream**:
  * Event-Multiplexing mit strikter Isolation stellt sicher, dass Mandantendaten niemals in fremde Subscription-Streams leaken.

---

## 8. Hot Chocolate Fusion & Subgraph Federation

* **Federated Subgraph Routing**:
  * Nahtlose Aggregation verteilter Subgraphs zu einem föderierten Schema via Hot Chocolate Fusion.
* **Zero-Trust Context Forwarding**:
  * `SubgraphSecurityDelegatingHandler`: Sichere Weiterleitung authentifizierter Benutzerkontexte und SIDs an nachgelagerte Subgraphs ohne Preisgabe von Masterschlüsseln.
* **In-Memory Result Masking auf aggregierten Daten**:
  * `SubgraphResultMaskingMiddleware`: Wendet Maskierungs- und Compliance-Regeln auch auf Daten an, die aus externen Subgraphs zusammengeführt wurden.
* **CDN Cache-Tag Integration für Föderation**:
  * Extrahiert Entitäten-Tags über Subgraph-Grenzen hinweg und kombiniert sie zu ganzheitlichen Cache-Tags für vorgeschaltete Edge-Router.

---

## 9. Enterprise Data Catalog Federation & Compliance

* **Multi-Catalog Provider Integration**:
  * Schlüsselfertige Adapter für führende Enterprise-Metadatenkataloge:
    * **Microsoft Purview** (Apache Atlas REST API)
    * **Collibra** (REST Core API v2)
    * **Alation** (API v2)
    * **OpenMetadata** (REST & Webhooks)
* **Betriebsmodi**:
  * **Mirror Mode**: Synchronisiert Schemata, Beschreibungen, Tags und Klassifizierungen periodisch in den lokalen Governance-Store.
  * **Reference Mode**: Dynamische On-Demand-Föderation von Katalog-Metadaten ohne redundante Persistenz.
* **Automatisierte DSGVO Art. 9 Spezialkategorie-Erkennung**:
  * Tabellen oder Spalten mit Tags wie `GDPR_ARTICLE_9`, `HEALTH_DATA`, `BIOMETRIC`, `GENETIC` oder `RELIGIOUS` werden automatisch auf Sensitivitätsstufe `HIGH` gesetzt, fordern zwingend 4-Augen-Genehmigungen und erzwingen `REDACT`-Maskierung.
* **Automatisches PII Tag Mapping**:
  * Mappt Unternehmenskatalog-Tags (`TagToMaskingRuleMap`) automatisch auf Implementierungsregeln (`MASK_EMAIL`, `HMAC_SHA256`, `REDACT`).
* **Lineage & Downstream Consumer Impact Analysis**:
  * **Statischer Lineage DAG (BFS)**: Ermittelt Downstream-Abhängigkeiten (Dashboards, ETL-Pipelines, dbt, Airflow) und berechnet den Impact-Radius.
  * **Operative Runtime Lineage**: Verknüpft statische Lineage-Graphen mit HMAC-Audit-Logs, um tatsächliche aktive Consumer, Abfragehäufigkeiten und Personen der letzten 365 Tage zu identifizieren.
  * **Pre-Schema-Change Blast Radius**: Berechnet automatisierte Risikoscores (`CRITICAL`, `HIGH`, `MEDIUM`, `LOW`) vor Schema-Migrationen.
* **DSGVO Art. 15 Auskunftsrecht (Subject Access Report)**:
  * Vollautomatisierte Generierung von Auskunftsberichten (Art. 15 Abs. 1 Bst. c DSGVO): Wer hat wann welche Datenkategorien zu welchem Zweck eingesehen?
  * Revisionssicherer PDF-Export via QuestPDF (`GdprAuditReportPdfExporter`) für Datenschutzbeauftragte.
* **OpenLineage Standard Event Egress**:
  * `OpenLineageClient`: Sendet standardisierte `RunEvent`-Metadaten an zentrale Datenkataloge (Marquez, Collibra, Purview).

---

## 10. dbt Data Mesh & Contract Governance

* **dbt Artefakt-Streaming-Parser**:
  * High-Throughput Streaming-Parser für dbt `manifest.json`, `catalog.json` und `run_results.json` ohne hohen Speicherverbrauch bei Multi-Megabyte-Dateien.
* **Data Health Circuit Breaker**:
  * Schlägt ein Upstream `dbt test` in `run_results.json` fehl, versetzt das Gateway die betroffene Tabelle automatisch in den Quarantäne-Zustand (`CircuitBreaker: Open`). Fehlerhafte Daten werden nicht an Konsumenten ausgeliefert.
* **Model Contract Validation & CI/CD Breaking-Change Detection**:
  * Prüft dbt Model Contracts gegen das aktive Gateway-Schema vor dem Deployment. Verhindert Inkompatibilitäten frühzeitig im Build-Prozess.
* **dbt Proposal & Approval Lifecycle**:
  * `IDbtProposalRepository`: Verwaltet Änderungsvorschläge für Tabellenschemata und Klassifizierungen mit Audit-Trail.
* **Live-Telemetrie in dbt Exposures**:
  * `DbtExposurePublisher`: Spiegelt reale GraphQL-Abfrage-Frequenzen und Consumer-Metadaten zurück in dbt `exposure`-Deklarationen.

---

## 11. Enterprise Extensibility Framework (Dual-Mode C# / gRPC)

* **Dual-Mode Architektur**:
  * **In-Process C# Middlewares (Hot Path)**:
    * Direkte Einbindung in die ASP.NET Core DI-Pipeline als `.dll` oder NuGet-Paket.
    * Zugriff auf GraphQL AST, Spans und ExecutionContext mit < 0.1 ms Overhead und 0 IPC-Netzwerkhops.
  * **Out-of-Process gRPC Coprocess**:
    * Standardisierter gRPC Interceptor-Contract für polyglotte Teams (Go, Python, Java, Rust) oder getrennte Service-Lifecycles.
* **Ingress Interceptors**:
  * Benutzerdefinierte Token-Validierung, dynamische Token-Transformation, Ingress-Header-Validierung und ITSM-Challenge-Injektion.
* **Egress Interceptors**:
  * Nachgelagerte Data Loss Prevention (DLP), branchenspezifische Maskierungsalgorithmen und unternehmensspezifische Compliance-Audit-Sinks.

---

## 12. Agentic AI & Model Context Protocol (MCP) Gateway

* **Nativer Model Context Protocol (MCP) Server**:
  * Exponiert GraphQL-Queries und Schemadaten als validierte Tools für KI-Agenten (Anthropic Claude Desktop, Cursor, OpenAI Agents).
* **Multi-Transport Unterstützung**:
  * **Stdio Runner (`McpStdioRunner`)**: Standard-I/O-Verbindung für lokale Desktop- und CLI-Agenten.
  * **Streamable HTTP & SSE (`/mcp`, `/mcp/sse`)**: Web-kompatibler Transport für Cloud-Agenten und verteilte LLM-Pipelines.
* **Semantic Prompt Injection & Jailbreak Guardrail**:
  * `SemanticPromptGuardrail`: Erkennt und blockiert Prompt-Injection-Angriffe (OWASP LLM01), ChatML-Breakout-Versuche, Base64-Obfuskationen und System-Prompt-Override-Muster.
* **AI Data Guardrail Engine**:
  * `AiDataGuardrailService`: Automatisches PII-Scrubbing auf allen Agenten-Payloads, Durchsetzung von Max-Token- und Query-Kosten-Budgets sowie strenge Session-Ownership-Validierung.

---

## 13. Cryptographic Compliance & Tamper-Evident Audit Trail

* **HMAC-SHA256 Hash Chained Audit Log**:
  * Jede Abfrage, Genehmigung, Ablehnung, Rezertifizierung und Policy-Änderung erzeugt einen unveränderlichen Audit-Eintrag.
  * Jeder Eintrag enthält den kryptographischen Hash des direkten Vorgängers (`PrevHash -> EntryHash`).
  * Signiert mit geheimem HMAC-Schlüssel (`HMACSHA256.HashData(key, payload)`): Manipulationen an der Datenbank zerstören die Kette unweigerlich und schlagen bei automatischen Prüfungen Alarm.
* **Zeitkonstante Integritätsprüfung**:
  * Kontinuierliche Integritätsprüfungen via `CryptographicOperations.FixedTimeEquals` zur Vermeidung von Side-Channel-Laufzeitangriffen.
* **WORM-Konformer S3/Azure Audit Export**:
  * Exportiert Audit-Logs in unveränderliche WORM-Storage-Buckets (S3 Object Lock mit Compliance Mode oder Azure Immutable Blob Storage) zur Einhaltung von BSI-, BaFin-, HIPAA- und SOX-Vorgaben.
* **Strukturierte "Insecure Mode" Risikopräfixe**:
  * Klare Kennzeichnung unsicherer Entwicklungs- und Testkonfigurationen durch Präfixe:
    * `warn_`: Mittleres Risiko (`warn_allow_all_cors_origins`, `warn_disable_rate_limiting`)
    * `danger_`: Kritisches Risiko (`danger_allow_anonymous_queries`, `danger_bypass_authorization`)
  * Im Produktionsmodus standardmäßig hart blockiert (`Fail-Closed`).

---

## 14. Edge Performance, CDN Caching & Traffic Management

* **Surrogate-Key & CDN Cache-Tag Headers**:
  * `CdnCacheTagVisitor` & `CdnCacheTagMiddleware`: Analysiert den GraphQL AST und generiert feingranulare `Cache-Tag`- bzw. `Surrogate-Key`-Header für Edge-CDNs (Cloudflare, Fastly).
* **Zero-Trust Cache Isolation**:
  * Erkennt die Middleware, dass eine Query Spalten mit aktiven RLS-Filtern oder dynamischer Maskierung berührt, schaltet das Gateway den Header automatisch auf `Cache-Control: private, no-store`.
  * Verhindert zuverlässig, dass personalisierte Daten im öffentlichen Edge-Cache zwischengespeichert werden.
* **Asynchrone CDN Edge Invalidation via Outbox**:
  * Daten-Mutationen erzeugen Outbox-Events, die über `CloudflareCdnPurgeService` bzw. `FastlyCdnPurgeService` gezielte Cache-Purges auf CDN-Ebene auslösen.
* **Resilientes Distributed Token-Bucket Rate Limiting**:
  * `RedisRateLimiterService`: Atomare Lua-Skripte in Redis für Sliding-Window IP-Rate-Limiting und User-SID Concurrency-Limiting über K8s-Pod-Grenzen hinweg.
  * **Automatischer In-Memory-Fallback**: Fällt der Redis-Cluster aus, schaltet das Gateway transparent auf `InMemoryRateLimiterService` mit lock-freien `Interlocked`-Zählern um.
* **Client Quotas & Query Cost Telemetrie**:
  * `ClientTierResolver` unterteilt Konsumenten in Tiers (`Free`, `Standard`, `Enterprise`, `Internal`).
  * `CostAndQuotaMiddleware`: Weist GraphQL-Feldern Kostenfaktoren zu, berechnet Query-Komplexität und liefert Telemetriedaten über `X-Query-Cost`, `X-RateLimit-*` und `extensions.cost`.
* **Smart Schema Sunsetting**:
  * Automatische Signalisierung veralteter Schemata und Felder via `Sunset`- und `Deprecation`-HTTP-Header inklusive Telemetrie-Tracking betroffener Clients.

---

## 15. Operations, High Availability & Enterprise Resilienz

* **Kubernetes Probes**:
  * Duale Probes: `/health/live` (Liveness) und `/health/ready` (Readiness).
  * `IGatewayHealthCheckService`: Prüft Governance-DB, Redis-Cluster, Ziel-Datenbanken und Active Directory Verbindung.
* **6-Phase Graceful Traffic Drain Controller**:
  * Ermöglicht unterbrechungsfreie Rolling Updates in Kubernetes durch einen 6-stufigen Shutdown-Zyklus (Signalempfang, Traffic-Drain, GraphQL-In-Flight-Completion, DB-Connection-Draining, Background-Worker-Stop, Process-Exit).
* **Redis Pub/Sub Event Bus**:
  * `RedisEventBus`: Verteilte Echtzeit-Signalisierung von Epoch-Inkrementen und Policy-Invalidierungen über hunderte Pods.
* **Idempotenz-Store**:
  * `RedisIdempotencyStore`: Verhindert die mehrfache Ausführung sicherheitskritischer Governance-Mutationen bei Netzwerk-Retries.
* **Zero External Dependencies im Dev-Modus**:
  * Lokale Ausführung ohne Docker, Redis oder SQL Server durch integrierte SQLite-Engine und Test-Auth-Handler.
