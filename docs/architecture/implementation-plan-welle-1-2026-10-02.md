# Master-Implementierungsplan: Welle 1 (Enterprise Differenzierung & Core Expansion)

**Dokument-ID:** `IMPL-PLAN-2026-10-02-WELLE-1`  
**Datum:** 2026-10-02  
**Status:** Genehmigt (Architektur-Freigabe)  
**Autor:** Principal Enterprise Software & Security Architect  
**Geltungsbereich:** `gql` (Core Gateway), `gql_sqlparser`, `gql_extensions`  
**Referenzen:** [`marktanalyse.md`](file:///root/lis-git/gql/gql/marktanalyse.md), [`plan-architektur-evolution-ha-ast-roles-2026-10-02.md`](file:///root/lis-git/gql/gql/docs/architecture/plan-architektur-evolution-ha-ast-roles-2026-10-02.md), [`ADR-017`](file:///root/lis-git/gql/gql/docs/adr/ADR-017-distributed-state-ast-generator-and-rbac.md)

---

## 1. Executive Summary & Strategischer Kontext

Nach vollständiger Bereinigung der 100 operativen Sicherheitsbefunde (Runde 4 & 5) und der Grundsteinlegung für den Multi-Node-Clusterbetrieb (`K-K14`) sowie das RBAC-Modell (`K-K10`) adressiert **Welle 1 (Wave 1)** die drei höchstpriorisierten Differenzierungsmerkmale der **Enterprise-Roadmap (Q4 2026)**:

```
+---------------------------------------------------------------------------------------------------+
|                                      WELLE 1 LEISTUNGSUMFANG                                      |
+------------------------------------+----------------------------------+---------------------------+
| F-AI-07: Semantic Schema Pruning   | F-CDC-03: Zero-Kafka Postgres    | F-OPS-01: AST-Aware Dark  |
| & Just-in-Time MCP Tools           | Logical Streaming Replication    | Traffic Shadowing         |
| -> Bis zu 80% Prompt-Token Ersparnis| -> Zero-Broker CDC via pgoutput  | -> Risikoarme Staging-    |
| -> Keine LLM-Kontextüberlastung    | -> Native WAL-to-Subscription    |    Replays ohne Mutationen|
+------------------------------------+----------------------------------+---------------------------+
                                  |
                                  v
+---------------------------------------------------------------------------------------------------+
|                     ARCHITEKTONISCHES FUNDAMENT (Verzahnung & Enabler)                           |
|  - K-K14: Multi-Node State Provider (Redis/NATS) für verteiltes Event-Routing & Session-Sync     |
|  - AST Dialect Pipeline (SQ-01/02/05) für AST-Inspektion im Dark-Shadowing-Pfad                  |
|  - K-K10: Einheitliche GatewayRole & ClaimsNormalization für mandantenisoliertes Streaming        |
+---------------------------------------------------------------------------------------------------+
```

---

## 2. Feature-Spezifikationen & Technisches Design

### 2.1 `F-AI-07` Dynamic Semantic Schema Pruning & Just-in-Time MCP Tools

#### 2.1.1 Problem & Schmerzpunkt
Große Unternehmens-Supergraphs mit hunderten Objekttypen sprengen das Kontextfenster von LLMs (30.000 bis 60.000 Tokens allein für Tool-Definitionen). Dies führt zu:
1. Extrem hohen Inferenzkosten je Agenten-Turn.
2. Signifikanten First-Token-Latenzen (TTFT > 2.500 ms).
3. "Attention Degradation": LLMs wählen bei zu vielen Werkzeugen falsche oder halluzinierte Signaturen.

#### 2.1.2 Architektur & Lösungsansatz
Dynamisches Pruning zur Laufzeit über ein zweistufiges Vektor-/Semantik-Ranking:
1. **Offline/Warmup Indexierung:** `SemanticMcpCompiler` generiert für jedes Tool eine kompakte semantische Repräsentation (Name, Beschreibung, dbt-Tags, OpenMetadata-Domänen). Diese werden als Einbettungsvektoren im Speicher gehalten (`ToolEmbeddingIndex`).
2. **Online Just-in-Time Filtering:** Bei Eingang eines Agenten-Prompts berechnet das Gateway die Kosinus-Ähnlichkeit und wählt nur die Top-$K$ relevanten Werkzeuge (Standard: $K = 8$) innerhalb des konfigurierten Token-Budgets (z. B. max. 4.000 Tokens für Tool-Definitionen).
3. **Prompt Injection Guard:** Der Pruner ignoriert versteckte Steuerzeichen und Anweisungen im Prompt und filtert rein nach semantischer Distanz zur fachlichen Domäne.

#### 2.1.3 Kernschnittstellen
```csharp
namespace Autheris.Application.Mcp.Pruning;

public interface ISemanticToolPruner
{
    ValueTask<IReadOnlyList<McpToolDefinition>> PruneToolsAsync(
        string userPrompt,
        IReadOnlyList<McpToolDefinition> availableTools,
        ToolPruningOptions options,
        CancellationToken ct = default);
}

public sealed record ToolPruningOptions(
    int MaxTools = 8,
    int MaxToolDefinitionTokens = 4000,
    float MinSimilarityThreshold = 0.45f,
    bool ForceIncludeGoldenQueries = true
);
```

---

### 2.2 `F-CDC-03` Zero-Kafka PostgreSQL CDC via Logical Streaming Replication

#### 2.2.1 Problem & Schmerzpunkt
Echtzeit-Streaming über Apache Kafka, Zookeeper/KRaft und Debezium scheitert in vielen Abteilungen an den hohen Betriebs- und Infrastrukturkosten ("The Kafka Barrier"). Während MSSQL bereits nativ via `CHANGETABLE` unterstützt wird (`F-CDC-02`), fehlte eine leichtgewichtige Lösung für PostgreSQL-, Supabase- und Aurora-Landschaften.

#### 2.2.2 Architektur & Lösungsansatz
1. **Natives Streaming-Protokoll:** Das Gateway verbindet sich über eine dedizierte Replikations-Verbindung (`Npgsql.Replication`) direkt mit dem PostgreSQL-Server.
2. **`pgoutput` Logical Decoding:** Nutzung des integrierten `pgoutput`-Plugins ohne externe C-Libraries oder Fremdplugins auf DB-Ebene.
3. **Replication Slot Governance:**
   - Automatisches Anlegen und Verwalten eines flüchtigen oder permanenten Replikations-Slots (`autheris_gateway_cdc_slot`).
   - Regelmäßiges LSN-Commit (Acknowledgement) verhindert unkontrolliertes Anwachsen der Write-Ahead-Logs (WAL) auf dem Datenbankserver.
4. **Zero-Trust Event Pipeline:** Decodierte `INSERT`-, `UPDATE`- und `DELETE`-Nachrichten durchlaufen vor der Auslieferung an WebSocket- oder SSE-Clients die zentrale RLS- und Mandantenfilterung (`GatewayExecutionService.FilterRows`).

#### 2.2.3 Datenfluss-Diagramm
```
+-----------------------------------------------------------------------------------+
| PostgreSQL Database (WAL Engine)                                                  |
| WAL Sender -> Logical Decoding via pgoutput -> Replication Slot                   |
+-----------------------------------------------------------------------------------+
                                         |
                                         | Streaming TCP (Port 5432 / TLS)
                                         v
+-----------------------------------------------------------------------------------+
| Autheris.Infrastructure / Streaming                                             |
|                                                                                   |
| [ PostgreSqlLogicalReplicationService ]                                           |
|   - NpgsqlLogicalReplicationConnection                                           |
|   - WalMessageDecoder (InsertMessage, UpdateMessage, DeleteMessage)               |
|   - LsnAcknowledgementManager (StandbyStatusUpdate)                               |
+-----------------------------------------------------------------------------------+
                                         |
                                         v
+-----------------------------------------------------------------------------------+
| Governance & Fan-Out Layer                                                        |
|   - StreamRlsPolicyEnforcer (Spaltenmaskierung & Row-Level-Security je Tenant)     |
|   - IDistributedClusterStateProvider / Redis PubSub (K-K14)                       |
|   - Hot Chocolate GraphQL Subscriptions (WebSocket / SSE)                         |
+-----------------------------------------------------------------------------------+
```

---

### 2.3 `F-OPS-01` AST-Aware Production Traffic Shadowing & Dark Replay

#### 2.3.1 Problem & Schmerzpunkt
Statische Schematests und synthetische Unit-Tests erkennen keine Last-Latenz-Regressionen, DB-Lock-Konflikte oder semantische Berechnungsabweichungen unter Realbedingungen.

#### 2.3.2 Architektur & Lösungsansatz
1. **AST-Mutation Guard:** Eine Middleware fängt produktive Requests ab und analysiert den GraphQL- / SQL-AST. **Schreibende Operationen (Mutationen, DML `INSERT/UPDATE/DELETE`) werden im Shadow-Pfad zwingend und konstruktiv verworfen.**
2. **Zero-Impact Asynchrone Pipelining:** Der Produktiv-Request wird ohne Verzögerung ausgeführt. Ein Klon der Abfrage wird über einen internen `System.Threading.Channels.Channel<ShadowRequest>` mit fester Kapazität entkoppelt.
3. **PII Redaction Engine:** Vor dem Replay gegen die Staging-/Canary-Umgebung werden Passwörter, Tokens und PII-Muster (E-Mails, Kreditkarten, SV-Nummern) durch den `PiiShadowingRedactor` maskiert oder gehasht.
4. **Vergleichende Latenz- & Fehlermetrik:** Das Gateway sendet die Abfrage an das Staging-Ziel, vergleicht HTTP-Status, Fehlerquoten und Latenzen ($p50, p95, p99$) und publiziert OpenTelemetry-Metriken (`traffic_shadow_diff_seconds`).

#### 2.3.3 Kernschnittstellen
```csharp
namespace Autheris.Application.Diagnostics.Shadowing;

public interface ITrafficShadowingService
{
    ValueTask EnqueueShadowRequestAsync(
        HttpContext context,
        string requestBody,
        AstNode parsedAst,
        CancellationToken ct = default);
}

public sealed record TrafficShadowingOptions(
    bool Enabled = false,
    string TargetBaseUrl = "https://staging-gateway.internal:5001",
    double SampleRatePercentage = 5.0, // 0.1% bis 100%
    int ChannelCapacity = 5000,
    int TimeoutMs = 3000,
    bool StripPiiHeaders = true
);
```

---

## 3. Phasierungs- und Meilensteinplan für Welle 1

Die Realisierung von Welle 1 erfolgt in **4 aufeinander aufbauenden Sprints**:

| Meilenstein / Sprint | Dauer | Schwerpunkte & Arbeitspakete | Betroffene Komponenten | Quality Gate / Deliverable |
|---|:---:|---|---|---|
| **Sprint 1: Foundations & F-AI-07** | 3 Tage | **Semantic MCP Tool Pruning (`F-AI-07`)**<br>- `ISemanticToolPruner` & `CosineSimilarityRanker`<br>- MiniLM / Lightweight Embedding Provider<br>- Integration in `SemanticMcpCompiler` & `McpEndpoints`<br>- Token Budget Bounding & Golden Query Pinning | `Autheris.Application.Mcp`<br>`Autheris.Api` | Unit-Tests für Pruning; Reduzierung der Tool-Token im MCP-Handshake von 25k auf < 4k |
| **Sprint 2: F-CDC-03 Streaming** | 4 Tage | **PostgreSQL Logical CDC (`F-CDC-03`)**<br>- `PostgreSqlLogicalReplicationService`<br>- `pgoutput` Decoder & LSN Ack Worker<br>- RLS-Filterung auf decodierten WAL-Events<br>- Anbindung an `IEventBus` / GraphQL Subscriptions | `Autheris.Infrastructure.Streaming`<br>`Autheris.GraphQL` | End-to-End Test: PG `INSERT` -> WAL -> Subscription Receive in < 50 ms ohne Kafka |
| **Sprint 3: F-OPS-01 Shadowing** | 3 Tage | **AST Dark Traffic Shadowing (`F-OPS-01`)**<br>- `TrafficShadowingMiddleware`<br>- AST Guard (Sperre für Mutationen & DML)<br>- Async Channel Worker & PII Redactor<br>- OTel Metriken (`gateway_shadow_*`) | `Autheris.Api.Middleware`<br>`Autheris.Application.Diagnostics` | Dark Replay Test: 100% Mutationen blockiert; Latenz-Diff Staging vs. Prod messbar |
| **Sprint 4: Integration & E2E** | 2 Tage | **End-to-End Verifikation & Härtung**<br>- Multi-Node HA Test (CDC + Shadowing mit Redis `K-K14`)<br>- Lasttests mit `graphql-bench` (Zero-Overhead Proof)<br>- Feature-Dokumentation & Marktanalyse Update | Gesamtsystem | Alle Unit-, Integrations- und Shadowing-Tests grün; 0 Performance-Regression |

---

## 4. Sicherheits-, Governance- und Compliance-Leitplanken

1. **Kein Data-Leakage im Shadowing-Pfad (`F-OPS-01`):**
   - Produktive Authorization-Header (`Bearer ...`, Kerberos Tickets) werden vor dem Dark Replay durch synthetische Service-Tokens für das Staging-System ersetzt.
   - PII-Spalten werden anhand der Metadaten aus dem Data Catalog (`CatalogTagClassifier`) geschwärzt.
2. **WAL-Überlaufschutz für PostgreSQL (`F-CDC-03`):**
   - Health-Check überwacht die LSN-Distanz (`pg_wal_lsn_diff`).
   - Übersteigt der Lag 1 GB (z. B. bei getrenntem Gateway), meldet das Gateway `Degraded` und pausiert den Slot, um ein Volllaufen der DB-Festplatte zu verhindern.
3. **Fail-Closed bei MCP Pruning (`F-AI-07`):**
   - Kann die semantische Ähnlichkeit nicht berechnet werden, greift ein deterministischer Fallback auf vorkonfigurierte Basis-Tools (`DefaultCoreTools`) statt des ungefilterten Supergraphs.

---

## 5. Traceability-Matrix Welle 1

| Feature-ID | Name | Priorität | Test-Suite | Dokumentation |
|---|---|:---:|---|---|
| **`F-AI-07`** | Dynamic Semantic Schema Pruning | Hoch (RICE-1) | `SemanticToolPruningTests.cs` | [`f-ai-07-dynamic-semantic-schema-pruning.md`](file:///root/lis-git/gql/gql/docs/features/f-ai-07-dynamic-semantic-schema-pruning.md) |
| **`F-CDC-03`** | Zero-Kafka PostgreSQL CDC | Hoch (RICE-2) | `PostgreSqlLogicalCdcTests.cs` | [`f-cdc-03-zero-kafka-postgresql-cdc.md`](file:///root/lis-git/gql/gql/docs/features/f-cdc-03-zero-kafka-postgresql-cdc.md) |
| **`F-OPS-01`** | AST-Aware Traffic Shadowing | Hoch (RICE-3) | `TrafficShadowingGuardTests.cs` | [`f-ops-01-traffic-shadowing-dark-replay.md`](file:///root/lis-git/gql/gql/docs/features/f-ops-01-traffic-shadowing-dark-replay.md) |
