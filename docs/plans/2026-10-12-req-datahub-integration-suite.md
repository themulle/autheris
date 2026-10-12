# PRD: REQ-DATAHUB-01 Enterprise Data Governance Suite: Dual-Mode Embedded & Federated Integration

**Document ID:** `REQ-DATAHUB-01`  
**Date:** 2026-10-12  
**Status:** APPROVED  
**Author:** Enterprise Product Manager & Strategic Gatekeeper  
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)  
**Target Milestone:** Wave 3 – Data Governance, Contracts & Lineage Observability  

---

## 1. Executive Summary & Problem Statement

### 1.1 Context & Background
DataHub (originally LinkedIn, maintained by Acryl Data) is one of the most widely adopted open-source metadata platforms in modern enterprise data stacks. It defines standards for metadata modeling (aspect-oriented architecture), verifiable **Data Contracts & Assertions**, and **Column-Level Lineage**.

Autheris operates as a **Zero-Trust Enterprise GraphQL and Data Access Gateway**, enforcing row-level security (RLS), dynamic field masking, schema contract projection (`@tag`), and cryptographic KMS-anchored audit logs directly at Layer 7.

### 1.2 The Strategic Architectural Dilemma: Embedded vs. Federated
1. **The Heavyweight Stack Barrier:** Running an external DataHub deployment requires a multi-container stack (Java Spring Boot GMS, Apache Kafka, ElasticSearch/OpenSearch, Neo4j, MySQL, Python runners). For air-gapped defense networks, edge deployments, CI/CD sandbox testing, and lean enterprise teams, this footprint is prohibitive.
2. **Passive vs. Active Governance:** External data catalogs are fundamentally **passive observation systems**. When a data contract or freshness SLA breaks in DataHub, a dashboard turns red, but downstream consumers continue ingesting corrupted data.
3. **The Runtime Query Lineage Blind Spot:** DataHub tracks *declared* or *pipeline* lineage (from dbt runs or Airflow DAGs). However, compliance officers cannot observe real-time API query lineage: *Which API consumer, service identity, or autonomous AI agent queried which column at 14:02:15?*

### 1.3 The Solution: Dual-Mode Governance Architecture
Autheris adopts the core concepts of DataHub into a flexible **Dual-Mode Governance Architecture**:

```text
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                                 AUTHERIS L7 GATEWAY                                    │
│                                                                                        │
│  ┌────────────────────────────────────────┐  ┌──────────────────────────────────────┐  │
│  │   MODE A: STANDALONE EMBEDDED MODE     │  │   MODE B: FEDERATED ENTERPRISE MODE  │  │
│  │   (Zero-Dependency / Air-Gapped)       │  │   (Central Enterprise Data Platform) │  │
│  │                                        │  │                                      │  │
│  │ • Embedded Lineage Graph in SQLite/    │  │ • DataHub GMS REST/GraphQL Client    │  │
│  │   PostgreSQL / DuckDB                  │  │ • Real-time Webhook (MCP/MCL)        │  │
│  │ • Declarative YAML Data Contracts      │  │ • Outbound Runtime Query Lineage     │  │
│  │ • Native In-Gateway Assertion Runner   │  │ • External Contract Circuit Breaker  │  │
│  │ • Native Business Glossary & Taxonomy  │  │ • Ingestion of Purview/Collibra/     │  │
│  │ • Native Data Product Packaging        │  │   DataHub Tags & Classifications     │  │
│  └────────────────────────────────────────┘  └──────────────────────────────────────┘  │
│                                        │                                               │
│                                        ▼                                               │
│                     UNIFIED ACTIVE ENFORCEMENT ENGINE                                  │
│             AST Pushdown • Fail-Closed Circuit Breaker • Masking                       │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Mode A (Standalone Embedded):** Operates 100% self-sufficiently without external dependencies. Lineage, data contracts, and glossaries are persisted in Autheris's existing Governance DB ([F-GOV-12](file:///root/autheris/docs/features/f-gov-12-multi-engine-governance-storage.md): PostgreSQL or SQLite) and DuckDB ([F-DATA-03](file:///root/autheris/docs/features/f-data-03-duckdb-olap.md)).
- **Mode B (Federated Enterprise):** Connects bidirectionally to an external DataHub cluster, synchronizing tags and contracts while emitting real-time query lineage back to the central platform.

---

## 2. Market & Best-Practice Benchmark

| Capability | Apollo Federation v2 / Router | Hasura DDN | DataHub Native | **Autheris Dual-Mode Suite** |
| :--- | :--- | :--- | :--- | :--- |
| **Operational Model** | Router only (BFF) | Cloud-managed DDN | Central Catalog Server | **Dual-Mode: Embedded Standalone OR Federated** |
| **Catalog Metadata Ingestion** | ❌ None | ❌ Proprietary Metadata | N/A (Is the catalog) | **Native REST/GraphQL Ingestion** (Auto-maps to Casbin & Masking) |
| **Runtime Column Lineage** | ❌ OTel spans only (no dataset URNs) | ❌ In-flight metrics only | Relies on external emitters | **In-Gateway Graph (Mode A) + DataHub Egress (Mode B)** |
| **Data Contract Enforcement** | ❌ None | ❌ None | Passive Contract Status (`FAILING`) | **Active Fail-Closed Interception** (503 Quarantine on SLA breach) |
| **In-Gateway Assertion Runner** | ❌ None | ❌ None | Requires external runners | **Embedded SQL/DuckDB Runner** (Evaluates rules in-process) |
| **Real-time Event Invalidation** | ❌ Schema reload via Uplink | ❌ CLI metadata deploy | Kafka / Webhook emitters | **HMAC-Protected Ingestion Endpoint** (Instant in-memory sync) |
| **Zero-Allocation Telemetry** | ⚠️ High alloc in Rust/Node | ⚠️ Go runtime GC overhead | N/A | **.NET 10 System.Threading.Channels** (<0.05ms overhead) |

---

## 3. High-Level Architecture & Workflow

```mermaid
flowchart TD
    subgraph DataHubPlatform ["External DataHub Platform (Mode B)"]
        GMS["DataHub GMS (GraphQL & REST API)"]
        ACTIONS["DataHub Actions / Kafka Webhook Emitter"]
    end

    subgraph AutherisGateway ["Autheris L7 Zero-Trust Gateway (.NET 10)"]
        subgraph ModeB ["Federated Connectors (Mode B)"]
            CLIENT["DataHubCatalogClient<br/>(IDataCatalogClient)"]
            WEBHOOK["DataHubWebhookHandler<br/>(/api/v1/catalog/datahub/events)"]
            LINEAGE_EGRESS["AstRuntimeLineageEmitter<br/>(Push to DataHub / OpenLineage)"]
        end

        subgraph ModeA ["Embedded Engine (Mode A: Standalone)"]
            EMBED_LINEAGE["EmbeddedLineageEngine<br/>(Recursive Graph in SQLite/DuckDB)"]
            NATIVE_CONTRACTS["NativeDataContractEngine<br/>(Declarative YAML/JSON Contracts)"]
            ASSERT_RUNNER["EmbeddedAssertionRunner<br/>(SQL AST & DuckDB Evaluator)"]
            GLOSSARY["BusinessGlossaryService<br/>(Hierarchical Terms & Taxonomies)"]
            PRODUCT_MGR["DataProductManager<br/>(Subgraph & SLA Packaging)"]
        end

        subgraph CoreEnforcement ["Core Gateway Enforcement Layer"]
            CIRCUIT["DataHealthCircuitBreaker<br/>(Quarantine & Fail-Closed 503)"]
            AST_ENGINE["AST Parser & Rewriter<br/>(Single-Query Pushdown)"]
            POLICY_CACHE["Dynamic Policy & Masking Cache<br/>(Casbin / RLS / Masking)"]
        end
    end

    subgraph Consumers ["Data Consumers & AI Agents"]
        AI["AI Agents via MCP Gateway"]
        APPS["Enterprise Applications / Web Clients"]
    end

    %% Mode A Flows
    NATIVE_CONTRACTS --> ASSERT_RUNNER
    ASSERT_RUNNER -->|Assertion Breach| CIRCUIT
    AST_ENGINE -->|Record Query Graph| EMBED_LINEAGE
    GLOSSARY --> POLICY_CACHE
    PRODUCT_MGR --> AST_ENGINE

    %% Mode B Flows
    GMS -->|Schema & Tag Sync| CLIENT
    CLIENT --> POLICY_CACHE
    ACTIONS -->|MCL Events| WEBHOOK
    WEBHOOK --> POLICY_CACHE
    WEBHOOK -->|External Assertion Failure| CIRCUIT
    AST_ENGINE -->|Extract Accessed Columns| LINEAGE_EGRESS
    LINEAGE_EGRESS --> GMS

    %% Enforcement on Consumers
    CIRCUIT -->|Block Corrupted Data (503)| APPS
    CIRCUIT -->|Prevent Hallucinations| AI
    APPS & AI -->|Execute Query| AST_ENGINE
```

---

## 4. Scope Breakdown & Detailed Requirements

### Group 1: Mode A – Embedded Standalone Governance (Zero-Dependency)

#### REQ-DH-05: Embedded Column-Level Lineage Graph Engine (`EmbeddedLineageEngine`)
* **Description:** Maintain an internal graph of data dependencies and query access without requiring Neo4j or external graph databases.
* **Storage Schema:** Utilize relational adjacency and closure tables stored in `Autheris.Domain.Governance` (PostgreSQL or SQLite):
  - `governance.lineage_nodes`: Entity URNs (Tables, Views, Virtual Fields, API Endpoints, Consumers).
  - `governance.lineage_edges`: Relationships (`DERIVED_FROM`, `TRANSFORMED_BY`, `QUERIED_BY`, `PROJECTS_COLUMN`).
* **Querying:** Support recursive traversal (`WITH RECURSIVE` queries in SQLite/PostgreSQL and DuckDB) exposed via:
  - GraphQL `$system { lineage(urn: "...", depth: 3) { upstream downstream columnEdges } }`
  - Hybrid MCP Tool `get_lineage(urn: string, direction: "UPSTREAM" | "DOWNSTREAM")` for AI agents.

#### REQ-DH-06: Native Declarative Data Contracts & Assertion Runner (`NativeDataContractEngine`)
* **Description:** Enable data producers to define verifiable Data Contracts natively inside Autheris without external tools.
* **Contract Specification (YAML / JSON):**
  - **Schema Contract:** Expected column names, required types, non-null guarantees.
  - **Freshness SLA:** Max allowed age of source data (e.g. `max_age: 1h`, evaluated via `MAX(updated_at)` or CDC timestamp).
  - **Volume Constraints:** Min/max expected row counts or hourly rate of change.
  - **Custom Predicate Assertions:** Declarative SQL filters that must always evaluate to `TRUE` (e.g. `amount >= 0`, `status IN ('PENDING', 'COMPLETED')`).
* **In-Gateway Assertion Runner:**
  - Evaluates assertions periodically (background cron) or on demand.
  - Executes checks either via lightweight SQL pushdown or embedded DuckDB (`F-DATA-03`).
  - Upon failure, automatically trips `DataHealthCircuitBreaker` to quarantine the table.

#### REQ-DH-07: Native Data Product Virtualization & Packaging (`DataProductService`)
* **Description:** Provide native packaging of disparate tables, views, and virtual filters into cohesive business **Data Products**.
* **Capabilities:**
  - Group assets by Domain (e.g. `billing.invoices`, `billing.payment_methods` belong to Data Product `BillingAndRevenue`).
  - Attach explicit Business Owners, SLAs, and Data Contracts to the Data Product.
  - Expose Data Products as virtualized GraphQL Subgraphs or OpenAPI slices with dedicated rate limits (`F-PERF-13`) and API keys.

#### REQ-DH-08: Native Business Glossary & Taxonomy Extension
* **Description:** Extend `PLAN-GOV-KI-13` with a native Business Glossary.
* **Capabilities:**
  - Define hierarchical Glossary Terms (e.g. `Finance.GrossRevenue`, `PII.CustomerEmail`).
  - Annotate terms directly on GraphQL types and database columns.
  - Direct binding between Glossary Terms and Autheris Policy Actions (e.g. term `PII.CustomerEmail` automatically enforces `MASK_EMAIL` and Step-Up TOTP).

---

### Group 2: Mode B – Federated Enterprise Governance (DataHub GMS Integration)

#### REQ-DH-01: Native DataHub Enterprise Catalog Client (`DataHubCatalogClient`)
* **Description:** Implement `IDataCatalogClient` for DataHub GMS via GraphQL and OpenAPI.
* **Provider Type:** Register `DataCatalogProviderType.DataHub`.
* **URN Mapping:** Bi-directional translation between DataHub URNs (`urn:li:dataset:...`) and Autheris `TableIdentifier`.
* **Aspect Ingestion:** Ingest `schemaMetadata`, `globalTags`, `glossaryTerms`, and `ownership` into local Casbin ABAC rules and masking policies.
* **Resilience:** Wrap client in Polly 8 resilience pipelines (exponential backoff, jitter, circuit breaker).

#### REQ-DH-02: Real-Time Metadata Change Proposal (MCP/MCL) Webhooks
* **Description:** Ingest real-time DataHub change events at `POST /api/v1/catalog/datahub/events`.
* **Security:** Enforce HMAC SHA-256 signature verification (`X-DataHub-Signature`) with replay attack prevention (timestamp skew check).
* **Processing:** Invalidate or update local policy caches in < 5ms without gateway reboots.

#### REQ-DH-03: Runtime Column-Level Lineage & Access Telemetry Emitter (AST-to-DataHub)
* **Description:** Extract runtime column access from the AST compiler and push operational lineage to DataHub.
* **Execution:** Non-blocking `System.Threading.Channels.Channel<T>` with batch emission to DataHub `/api/v2/usage` or OpenLineage format.
* **Performance:** Strict budget: < 0.1ms latency overhead on hot query paths, 0 Bytes LOH allocation.

#### REQ-DH-04: DataHub Data Contracts & Assertions Circuit Breaker
* **Description:** Extend `F-DBT-1` to subscribe to DataHub Assertion Run events.
* **Behavior:** Trip circuit breaker (`Closed` $\rightarrow$ `Open`), returning HTTP 503 / GraphQL error when DataHub marks an assertion as failed.

---

## 5. User Stories & Acceptance Criteria

### US-1 (Embedded Mode Lineage Discovery)
> *As a Security Officer in an air-gapped banking network, I want to query column-level lineage directly from Autheris without deploying Kafka or Neo4j, so that we can audit data origins under strict regulatory isolation.*

* **Given** Autheris running in Standalone Mode (`Gateway:Governance:Mode = "Embedded"`).
* **When** a user queries `$system { lineage(urn: "crm.customers.email") { upstream } }`.
* **Then** Autheris evaluates the recursive lineage closure table in SQLite/Postgres in < 2ms and returns the full dependency tree.

### US-2 (Embedded Data Contract Enforcement)
> *As a Data Producer, I want to define a Data Contract with a freshness SLA of 2 hours, so that stale data is automatically blocked from being served.*

* **Given** a declarative YAML contract on `analytics.hourly_clicks` specifying `max_age: 2h`.
* **When** the embedded assertion runner detects that the latest record is 3 hours old.
* **Then** the `DataHealthCircuitBreaker` switches to `Open` and subsequent API requests receive HTTP 503 with reason `CONTRACT_FRESHNESS_BREACHED`.

### US-3 (Federated DataHub Tag Sync & Webhook)
> *As a Data Steward, I want tags added in DataHub to immediately enforce masking in Autheris via webhooks.*

* **Given** Autheris running in Federated Mode connected to DataHub GMS.
* **When** a steward adds `PII.Confidential` to `employees.salary` in DataHub.
* **Then** DataHub fires a signed webhook, Autheris updates its in-memory masking cache in < 5ms, and the next query returns masked data.

### US-4 (AI Agent Hallucination Prevention via Contract Guard)
> *As an AI Platform Architect, I want autonomous agents calling the Autheris MCP Gateway to be blocked from reading datasets with failing contracts.*

* **Given** an AI agent attempting to query `finance.quarterly_summary` via MCP tool `execute_graphql`.
* **When** the table has an active assertion failure (either embedded or in DataHub).
* **Then** the MCP tool call returns an explicit error message `DATA_CONTRACT_ASSERTION_FAILED`, preventing the LLM from hallucinating on corrupt numbers.

---

## 6. Configuration & Schema Specifications

```json
{
  "Gateway": {
    "Governance": {
      "Mode": "Dual", 
      "Embedded": {
        "Enabled": true,
        "StorageEngine": "Sqlite", 
        "LineageTracking": {
          "Enabled": true,
          "TrackColumnLevel": true,
          "RetentionDays": 90
        },
        "DataContracts": {
          "Enabled": true,
          "ContractsDirectory": "/etc/autheris/contracts",
          "AssertionCheckIntervalSeconds": 300,
          "QuarantineOnBreach": true
        },
        "BusinessGlossary": {
          "Enabled": true
        }
      },
      "Federated": {
        "Enabled": true,
        "Provider": "DataHub",
        "DataHub": {
          "Endpoint": "http://datahub-gms:8080",
          "ApiToken": "AQE...<secret>",
          "Webhook": {
            "Enabled": true,
            "Secret": "whsec_08f9...<secret>",
            "ClockSkewSeconds": 300
          },
          "LineageEmitter": {
            "Enabled": true,
            "BatchIntervalSeconds": 10,
            "MaxBatchSize": 250
          }
        }
      }
    }
  }
}
```

---

## 7. Non-Functional Requirements (NFRs)

* **Performance:**
  - Embedded lineage graph queries (`WITH RECURSIVE`) must execute in **< 3ms** for graphs up to 10,000 nodes.
  - Runtime lineage recording must use bounded `System.Threading.Channels` with drop-safeguards, adding **< 0.05ms** to query latency.
* **Security & Fail-Closed:**
  - Default state on broken assertion or missing classification is **strict fail-closed rejection**.
  - All contract state changes and circuit breaker trips are cryptographically anchored in the KMS WORM audit trail.
* **Zero External Dependencies in Standalone Mode:**
  - When `Mode = "Embedded"`, Autheris runs without Kafka, Neo4j, Redis, or external catalog servers.

---

## 8. Gatekeeper Rejection & Guardrail Log

| Requested Concept | Verdict | Rationale & Approved Alternative |
| :--- | :---: | :--- |
| **Embed Neo4j / JVM inside Autheris** | 🛑 **REJECTED** | Massive memory footprint and JVM dependency violate .NET 10 AOT gateway design. Use relational closure tables / CTEs in SQLite/Postgres. |
| **Full Data Catalog Web UI** | 🛑 **REJECTED** | Autheris is an L7 Gateway, not a general metadata web app. Expose GraphQL `$system`, REST Admin APIs, and MCP tools. |
| **External Kafka Dependency for Local Events** | 🛑 **REJECTED** | Unnecessary operational burden for standalone deployments. Use in-memory channels with SQLite WAL / Postgres LISTEN/NOTIFY. |

---

## 9. Rollout, Metrics & Telemetry

* **OpenTelemetry Metrics:**
  - `autheris.governance.mode` (Gauge: 0=Disabled, 1=Embedded, 2=Federated, 3=Dual)
  - `autheris.lineage.nodes_total` / `autheris.lineage.edges_total` (Gauges)
  - `autheris.contracts.active_total` / `autheris.contracts.breached_total` (Gauges)
  - `autheris.circuit_breaker.state` (Gauge: 0=Closed, 1=HalfOpen, 2=Open)
