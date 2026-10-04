# Autheris - Enterprise GraphQL Gateway with Data-Owner-Consent

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Hot Chocolate](https://img.shields.io/badge/GraphQL-Hot%20Chocolate%2016-F00E2B?logo=graphql&logoColor=white)](https://chillicream.com/)
[![MCP Ready](https://img.shields.io/badge/AI-Model%20Context%20Protocol-8A2BE2?logo=anthropic&logoColor=white)](#-agentic-ai--model-context-protocol-mcp-gateway)
[![Iceberg](https://img.shields.io/badge/Lakehouse-Apache%20Iceberg%20v2-4B8BBE?logo=apache&logoColor=white)](#)
[![OData](https://img.shields.io/badge/Protocol-OData%20v4-0078D4)](#)
[![AuthZ](https://img.shields.io/badge/AuthZ-Casbin%20ABAC-009688)](#)
[![CI Build & Test](https://img.shields.io/badge/CI-Passing-brightgreen?logo=githubactions&logoColor=white)](.github/workflows/ci.yml)
[![Tests](https://img.shields.io/badge/Tests-2%2C261%20Passing-brightgreen)](tests/Autheris.Tests.Unit)
[![Security Review](https://img.shields.io/badge/Security%20Review-2026--10--02%20Remediated-brightgreen)](security-review-2026-10-02.md)
[![Docker](https://img.shields.io/badge/Docker-ghcr.io-2496ED?logo=docker&logoColor=white)](https://github.com/themulle/gql/pkgs/container/gql)
[![Architecture](https://img.shields.io/badge/Architecture-Clean%20%2F%20Onion-blue)](docs/architecture/arc42.md)
[![Features](https://img.shields.io/badge/Features-Enterprise%20Catalog-blueviolet)](featurelist.md)
[![Comparison](https://img.shields.io/badge/Comparison-Market%20Moats-orange)](featurecomparison.md)
[![License: BSL 1.1](https://img.shields.io/badge/License-BSL%201.1%20%2F%20Commercial-blue)](#-license)

Autheris is a high-performance, secure, centralized enterprise GraphQL gateway built with **.NET 10** and **Hot Chocolate 16.6.7**. It provides unified GraphQL access to heterogeneous enterprise databases (**Microsoft SQL Server / MSSQL, SQLite, PostgreSQL, Databricks, Oracle**), modern **Apache Iceberg Lakehouses**, REST APIs, and federated **Hot Chocolate Fusion Subgraphs** while enforcing a strict **Zero-Trust Data-Owner-Consent** governance model.

Instead of traditional coarse-grained role-based access control (RBAC), access to tables, rows, and columns requires explicitly granted, time-bounded, and auditable consents governed directly by data owners.

> 📚 **Produkt- & Strategie-Dokumente**:
> - [📋 Vollständige Feature-Liste (featurelist.md)](featurelist.md) — Detailliertes Inventar aller Enterprise-Funktionen.
> - [⚖️ Wettbewerbs- & Marktvergleich (featurecomparison.md)](featurecomparison.md) — Autheris vs. Apollo Federation v2, Hasura DDN, WunderGraph Cosmo, StepZen, Immuta und Tyk/Kong/Envoy.
> - [📊 Marktanalyse & RICE-C Roadmap (marktanalyse.md)](marktanalyse.md) — Umfassende Markt- und Gap-Analyse.

---

## 🌟 Key Features

- **Zero-Trust Governance Model**:
  - Default fail-closed: Non-consented tables, columns, or rows are strictly denied (`FORBIDDEN`).
  - Active Directory Windows Security Identifiers (`Sid`) integration for both users and security groups.
  - Multi-step **Four-Eyes Approval Workflow** with segregation of duties (requester cannot approve own requests; duplicate approvals rejected).
  - Time-bounded delegations (`DATA_OWNER_DELEGATIONS`) allowing seamless holiday/vacation handovers.

- **Multi-Protocol Enterprise Identity & ForwardAuth (Kubernetes / Traefik)**:
  - **Traefik Ingress ForwardAuth**: Native support for Kubernetes ingress authentication offloading (Authelia, Keycloak, Authentik, OAuth2-Proxy). Validates proxy network CIDRs (`TrustedNetworks`, `TrustedProxies`) and timing-safe shared secrets (`X-Forwarded-Secret`), extracting `X-Forwarded-User`, `X-Forwarded-Groups`, `X-Forwarded-Roles`, and `X-Forwarded-Email`.
  - **Microsoft Entra ID (Azure AD) & AD FS**: Native JWT Bearer token authentication with normalized enterprise claims transformation (`EnterpriseClaimsTransformation`) mapping `oid`, `onprem_sid`, `primarygroupsid`, and claim roles into canonical `Sid` value objects.
  - **HTTP Basic Authentication**: Support for direct Basic Auth headers on GraphQL queries and a dedicated credential verification endpoint (`GET` / `POST /api/auth/login`). Production enforces salted PBKDF2 (`$pbkdf2$...`) with dynamic dummy-iteration parity for non-existent users; plaintext and unsalted SHA-256 are strictly restricted to `Development`. All comparisons use `CryptographicOperations.FixedTimeEquals`.
  - **Kerberos / SPNEGO Negotiate**: Windows Integrated Authentication with strict Kerberos-only enforcement and group SID resolution.
  - **Smart Dynamic Scheme Selector**: Automatic header-based protocol arbitration dispatching requests to ForwardAuth, Bearer, Basic, or Negotiate schemes.

- **Heterogeneous Multi-Source Data Architecture & Real SQL Execution**:
  - Dynamic type projection and schema generation based on the active governance catalog.
  - **Native SQL Execution with RLS Pushdown**: Direct ADO.NET execution via `ISqlConnectionFactory` supporting **MSSQL (SQL Server)**, **SQLite**, **PostgreSQL**, **Databricks**, and **Oracle**. Row-Level Security (RLS) filters are pushed down directly into generated SQL queries (`CombinedRowFilterSql`), preventing unauthorized rows from ever leaving the database engine.
  - **Modern Apache Iceberg v2 Lakehouse Connector (Pattern 4)**: Direct querying of Iceberg v2 tables on Amazon S3 (SigV4), Azure Blob Storage, and local filesystems with vectorized partition pruning, Min/Max statistics filtering, and L1 metadata caching.
  - **Declarative REST Data Source Engine (Pattern 3)**: Expose external REST APIs with URL-template parameter substitution (`/api/v1/customers/{id}`), header/query pushdown (`X-Tenant-Id`, `X-User-Sid`), bearer token forwarding / API keys, JSONPath extraction, and adaptive batching (`QueryParameterList`, `JsonBodyArray`, `ParallelSingleRequests` throttled via `SemaphoreSlim`). Integrated **SSRF Defense** with DNS pre-resolution (blocking RFC 1918, link-local, loopback, and cloud metadata) and hop-by-hop HTTP redirect protection (`AllowAutoRedirect = false`).
  - **Hot Chocolate Fusion Subgraph Router**: Composes distributed microservice subgraphs into a unified supergraph schema with zero-trust client token forwarding and in-memory result masking.
  - **Isolated C# Plugin System (Pattern 4)**: Host specialized HTTP/data connectors in isolated, collectible `AssemblyLoadContext` instances (`IHttpDataSourcePlugin`) preventing dependency collisions with host packages.
  - **Dual-Mode Enterprise Extensibility**: Native in-process C# DLL/NuGet middlewares for the high-performance hot path (<0.1ms overhead, zero IPC) alongside decoupled out-of-process gRPC coprocess interceptors.
  - **Central Zero-Trust Pipeline**: Regardless of source (SQL, Lakehouse, REST, Plugin, or Subgraph), all data passes through central consent evaluation (`GatewayExecutionService`), in-memory RLS post-filtering, central column masking, response budgeting, and audit logging.
  - Efficient DataLoader-based batching and selective child relation loading with chunking to protect underlying database parameter limits (e.g. SQLite 999, Oracle 1000, MSSQL 2100, PostgreSQL/Databricks 10000).
  - Introspection and Banana Cake Pop (Nitro) UI configurable per environment.

- **Realtime Event Streaming & CDC (Change Data Capture)**:
  - **GraphQL Subscriptions**: Full WebSocket (`graphql-transport-ws`) and Server-Sent Events (SSE) support with `WebSocketAuthInterceptor` token validation during `connection_init`.
  - **In-Stream Row-Level Security**: `StreamRlsPolicyEnforcer` validates dynamic Casbin ABAC permissions per emitted event, ensuring immediate drop of unconsented data.
  - **Debezium / Kafka CDC Ingestion**: `DebeziumCdcParser` decodes change events (`op: c, u, d`) with strict tenant stream isolation and in-stream column masking.

- **Agentic AI & Model Context Protocol (MCP) Gateway**:
  - Native MCP server exposing GraphQL queries and schema as AI Agent Tools via Stdio (`McpStdioRunner`) and Streamable HTTP/SSE (`/mcp`, `/mcp/sse`).
  - **Semantic Prompt Injection Defense**: `SemanticPromptGuardrail` inspects tool arguments against OWASP LLM01 prompt injection patterns, ChatML delimiters, and Base64 evasion techniques.
  - **AI Data Guardrail Engine**: Dynamic PII scrubbing, token consumption budgeting, query cost limits, and session ownership enforcement.

- **dbt Data Mesh & Contract Governance**:
  - High-throughput streaming parser for dbt `manifest.json`, `catalog.json`, and `run_results.json`.
  - **Data Health Circuit Breaker**: Tables with failing upstream `dbt test` executions are quarantined (`CircuitBreaker: Open`) to prevent serving dirty data.
  - **Model Contract Breaking-Change CI Gate**: Validates dbt model contracts against active schemas before deployment.
  - **Live-Telemetrie in dbt Exposures**: Spiegelt reale GraphQL-Abfrage-Frequenzen und Consumer-Metadaten zurück in dbt `exposure`-Deklarationen.
  - **Omnichannel Documentation Passthrough (`F-DOC-01`)**: Lossless ingestion of dbt markdown doc-blocks and OpenMetadata business definitions into GraphQL Web UI (Banana Cake Pop), MCP AI tool signatures, Dynamic OpenAPI 3.1 Swagger, and OData CSDL `$metadata` tooltips.

- **Declarative SQL-to-API Engine & Auto-Generated OpenAPI 3.0 / Swagger (`F-SQL-01`)**:
  - **Zero-Code SQL Endpoints**: Instantly expose governed REST endpoints directly from version-controlled `.sql` files (`queries/*.sql`) via `GET` and `POST /api/v1/queries/{name}`.
  - **Universal Parameter Syntax & AST Token Normalization**: Supports native database parameter syntax (`@param`) as well as templating syntax (`{{param}}`) with automatic token extraction, type inference, and AST normalization.
  - **Auto-Generated OpenAPI 3.0 Specification**: Dynamically generates `/api/v1/queries/openapi.json` from parsed SQL metadata, doc-blocks (`-- @name`, `-- @summary`, `-- @param`), and query projections for immediate interactive testing in Swagger UI.
  - **Zero-Trust AST Injection**: Automatically injects tenant isolation, Casbin ABAC, Row-Level Security (`RlsListener`), and dynamic column masking directly into the generated SQL execution plan.
  - **Dual Ingestion Mode**: Hot-reloading via `FileSystemWatcher` (Option A) and automatic model sync from dbt pipelines (Option B).

- **Governed WebSQL Engine (`F-DATA-02`)**:
  - **Secure HTTP-based SQL Execution**: Execute ad-hoc SQL queries over HTTP (`POST /api/v1/sql`) modeled after Trino/Presto, completely eliminating the need for exposed database ports (1433/5432) or uncontrolled database logins.
  - **AST-Level Security Linter & Rewriter**: Uses the high-performance `TrinoSqlEngine` / ANTLR4 parser to enforce strict read-only semantics (`SELECT` only), prevent multi-statement injection (`;`), block system functions (`@@`, comments), and enforce maximum result pagination limits.
  - **Deep AST Row-Level Security Pushdown**: Injects Casbin ABAC rules and correlated subquery filters (`IN`, `EXISTS`) transparently into the `WHERE` tree before the query hits the database.
  - **DML guardrails (`WebSql.AllowDml`, regular option)**: DML requires a role from `WebSql.DmlWriterRoles`; `UPDATE`/`DELETE` without `WHERE` or with a trivially true condition (`WHERE 1=1`, `WHERE true`, `… OR 1=1`, `id = id`) are rejected on the original statement (`RlsOptions.RejectUnfilteredDml`, default `true`); each DML statement runs in a transaction and is rolled back if it affects more than `WebSql.MaxAffectedRows` rows (default `1000`, `0` = unlimited); executed, rejected and failed DML is written to the audit chain (`WEBSQL_DML_EXECUTED` / `WEBSQL_DML_REJECTED` / `WEBSQL_DML_FAILED`: statement type, tables, affected rows, actor, tenant, SHA-256 of the SQL – no SQL text or literal values). Per-table write permissions via a Casbin `write` action are planned.

- **Enterprise Governance Mutations & 4-Eyes Segregation of Duties**:
  - **Fail-Closed Mutation Suite**: Granular GraphQL mutations (`requestConsent`, `approveConsent`, `rejectConsent`, `revokeConsent`, `recertifyConsent`) requiring explicit tenant authorization.
  - **Anti-Self-Approval (Four-Eyes Principle / SoD)**: Data owners cannot approve their own requests; approvals strictly reject duplicate approval attempts.
  - **Idempotency & Replay Protection**: User-scoped 24-hour distributed idempotency keys (`RedisIdempotencyStore`) prevent double-submission of approval requests.

- **WORM Storage Cryptographic Audit Logging for Consents**:
  - **Full Lifecycle Audit Sealing**: Every consent grant (`CONSENT_GRANTED`), revocation (`CONSENT_REVOKED`), and recertification (`CONSENT_RECERTIFIED_AND_EXTENDED`) is cryptographically sealed in the immutable HMAC-SHA256 hash chain.
  - **WORM-Drive Export**: Seamless automated export to WORM storage (S3 Object Lock Compliance Mode / Read-Only filesystem) guaranteeing compliance with SEC Rule 17a-4 and GDPR audit standards.

- **Distributed Multi-Instance Clustering (Redis)**:
  - **Redis Pub/Sub Event Bus (`RedisEventBus`)**: Real-time cross-pod propagation of catalog and policy epoch increments, invalidating distributed caches across all cluster nodes simultaneously.
  - **Resilient Distributed Token-Bucket Rate Limiting (`RedisRateLimiterService`)**: Sliding-window IP rate limiting and atomic token-bucket consumption per user SID across multi-node Kubernetes deployments with **transparent automatic fallback** to local `InMemoryRateLimiterService` (featuring lock-free atomic `Interlocked` counters) upon Redis cluster degradation.
  - **Distributed Mutation Idempotency (`RedisIdempotencyStore`)**: High-availability deduplication of sensitive governance mutations across gateway instances.
  - **Deep Cluster Health Checks (`IGatewayHealthCheckService`)**: Comprehensive Kubernetes readiness probes checking Governance DB, Redis cluster, and Active Directory connectivity.

- **Column-Level Data Masking & Dynamic RLS**:
  - Transparent column-level policies: `Clear`, `Mask` (redaction / zero-allocation format-preserving masking via `ReadOnlySpan<char>` and `string.Create` / HMAC-SHA256 pseudonymization via `HMACSHA256.HashData` and stack memory), or `Deny`.
  - Side-channel inference defense (Rule 5 compliance): GraphQL AST `where` clauses referencing `Mask` or `Deny` columns are strictly rejected with a `SecurityException`, thwarting binary search inference attacks.
  - Dialect-aware SQL Row-Level Security (RLS) generation supporting SQLite, SQL Server (T-SQL), PostgreSQL (PL/pgSQL), Databricks, and Oracle with full row filter propagation across nested child DataLoaders.
  - Support for comparison operators, set inclusion (`IN`, tuple `IN`), temporal validity filters, and parameterized subqueries (`EXISTS`).

- **High-Performance Two-Tier Caching & Invalidation**:
  - **L1 In-Memory Cache** (MemoryCache) for ultra-low latency sub-millisecond lookups with lock-free table indexing.
  - **L2 Distributed Cache** (Redis) with fast pipelined batch operations.
  - **Epoch-based Invalidation**: Monotonic policy epochs invalidate stale cache entries across all gateway instances without cache stampedes.
  - High-throughput batch hydration for active consents (`WHERE consent_id IN (...)`) eliminating N+1 query overhead.

- **Tamper-Evident HMAC-SHA256 Audit Hash Chain**:
  - Every access evaluation, consent creation, approval, and revocation records an immutable audit entry.
  - Transaction-safe atomic audit log persistence with keyed `HMACSHA256.HashData(secretKey, payload)` preventing hash chain tampering even with direct database write access.
  - Continuous cryptographic HMAC-SHA256 hash chaining (`PrevHash -> EntryHash`) persisted across gateway restarts and verifiable via automated health routines using timing-safe `CryptographicOperations.FixedTimeEquals`.

- **Enterprise Data Catalog Integration (Microsoft Purview, Collibra, Alation, OpenMetadata)**:
  - Unified multi-catalog provider abstraction (`IDataCatalogClient`) supporting **Microsoft Purview** (Apache Atlas REST), **Collibra** (REST Core API v2), **Alation** (API v2), and **OpenMetadata**.
  - **Mirror Mode**: Synchronizes schemas, descriptions, tags, and classification rules directly into the local SQLite governance store.
  - **Reference Mode**: Dynamic, federated on-demand metadata lookup without duplicate persistence.
  - **Automated GDPR Art. 9 Special Category Protection**: Automatic classification of health, genetic, biometric, religious, and political data (`GDPR_ARTICLE_9`) enforcing mandatory `HIGH` sensitivity, four-eyes approval (`RequiresFourEyes = true`), and `REDACT` masking (`[REDACTED-GDPR-ART9]`).
  - **Automated PII Tag Mapping**: Maps catalog PII tags (`TagToMaskingRuleMap`) to masking algorithms (`MASK_EMAIL`, `HMAC_SHA256`, `REDACT`).
  - Administrative GraphQL mutation `syncDataCatalog(dryRun: Boolean)`.

- **Data Lineage: Downstream Consumer Impact & GDPR Art. 15 Disclosure**:
  - **Static Graph Lineage (DAG BFS)**: Iterative cycle-safe traversal over Dashboards (PowerBI, Tableau), ETL Pipelines (dbt, Airflow), and External Services with distance-from-root metrics.
  - **Operational Runtime Lineage**: Correlates static graph nodes with cryptographically signed audit logs to identify active consumers, query frequencies, and distinct actors over configurable timeframes.
  - **Pre-Schema-Change Risk Rating**: Automated blast radius calculation (`CRITICAL`, `HIGH`, `MEDIUM`, `LOW`) with proactive mitigation recommendations (deprecation notice windows, affected dashboard/pipeline owner notifications).
  - **Zero-Trust Contact Protection**: Owner contact emails are masked (`null`) unless the caller is an authorized Data Owner, GovernanceAdmin, or ClusterAdmin.
  - **GDPR Art. 15 Disclosure Reporting (Right of Access)**: Produces legally compliant reports (Art. 15 Abs. 1 Bst. c DSGVO) detailing all disclosed recipients, recipient categories, accessed columns, masking rules, and purposes over up to 365 days.

- **Modern Hybrid Identity & Machine-to-Machine (M2M) Service Principals**:
  - Unified Identity Provider abstraction (`IIdentityProvider`) decoupling gateway logic from specific IdPs.
  - Seamless hybrid migration support for **Microsoft Entra ID / Azure AD (OIDC)** alongside on-premises Windows Active Directory / Kerberos.
  - **M2M / Batch Service Accounts**: Dedicated Client-Credentials and mutual TLS (mTLS) authentication schema with service principal consents (`SP-<client_id>` SIDs) distinct from interactive user accounts.

- **Developer Onboarding & "Insecure Modes" (Explicit Risk Controls)**:
  - Pragmatic onboarding for external integrations and incoming webhooks. Every security switch defaults to `false` and is classified in `GatewayOptions.GetAllActiveBypasses()` (the configuration property names are kept for compatibility; a historic `warn_*` name may be classified as DANGER):
  - **DANGER** (genuinely not recommended): outside `Development` the gateway refuses to start (`ValidateGatewayOptions`); the health component `SecurityConfiguration` is unhealthy (outside Development `/health/ready` → 503); startup banner "INSECURE GETTING-STARTED CONFIGURATION".
  - **WARN** (mildly security-relevant): permitted in Production, but loud: console warning at startup, health component stays healthy with the description `degraded: …`, listed in the Development health details (`activeBypasses`, `activeWarnings`). Production health responses expose no additional details.
  - **Regular options** (e.g. `WebSql.AllowDml`): no message.
  - `securityMode` in the Development health details: `INSECURE_DEV_MODE` (any DANGER), `STRICT_WITH_WARNINGS` (only WARN), `STRICT_ZERO_TRUST` (none). The `X-Gateway-Insecure-Mode` header (Development only) lists all DANGER and WARN entries.

  | Switch (configuration property) | Class |
  |---|---|
  | `danger_allow_anonymous_access`, `danger_bypass_consent_checks`, `danger_disable_column_masking`, `danger_allow_insecure_transport`, `danger_bypass_webhook_signature_validation` / `danger_allow_anonymous_webhooks`, `danger_allow_untrusted_certificates`, `danger_bypass_mcp_auth`, `danger_bypass_lakehouse_auth`, `danger_bypass_websql_governance` / `WebSql.danger_bypass_sql_governance`, `OpenSchema` / `Catalog.OpenSchema` | DANGER |
  | `warn_allow_unmasked_ai_access`, `warn_mock_external_systems_if_unreachable`, `warn_auto_approve_access_requests`, `warn_disable_rate_limiting`, `warn_allow_unsigned_s3_requests`, `warn_ignore_webhook_timestamp_tolerance` | DANGER (historic `warn_` name) |
  | `warn_allow_all_cors_origins`, `warn_relaxed_query_limits`, `warn_enable_introspection`, `warn_fallback_default_tenant_for_webhooks` | WARN |
  | `Catalog.AllowLegacyPayloadOnlySignature`, `Itsm.LegacyGlobalWebhookSecret`, `OpenMetadata.AutoCreateConsents` | WARN |
  | `AllowDevelopmentInContainer` | WARN (additionally only effective together with `Development`, see container check) |
  | `WebSql.warn_allow_dml`, `Insecure.warn_allow_websql_dml` (legacy aliases) | WARN – use `WebSql.AllowDml` |
  | `WebSql.AllowDml` (+ mandatory `WebSql.DmlWriterRoles`) | regular option, no message |

  - Independent hard checks stay in place in every non-Development environment: Quickstart profile, `EnableTestAuthHandler`, anonymous access, `SeedDemoData`, valid HMAC Key-Vault reference, Development-in-container opt-in; `WebSql.AllowDml` without `DmlWriterRoles` aborts startup in every environment.

- **Two-Phase ITSM Integration & AI-Assisted Governance**:
  - **ITSM Webhook Integration**: Bi-directional integration with **ServiceNow** and **Jira** for approval workflows. Webhooks secured with timing-safe HMAC-SHA256 verification and 5-minute replay prevention.
  - **AI-Assisted Justification Triage**: Evaluates business justifications via `OpenJevClient` with prompt-injection defense, strict 500-character limits, and token-bucket rate limiting.
  - **Casbin ABAC/RBAC Engine**: Dynamic policy evaluation (`sub_rule`) with standalone policy validation tool (`tools/casbin-policy-lint`).

- **Enterprise Network & Edge Protection**:
  - Pre-Authentication IP Rate Limiting and Post-Authentication SID Token-Bucket Concurrency Limiting.
  - Anti-CSRF Preflight enforcement on GraphQL endpoints.
  - Table Oracle Defense: `ErrorSanitizingFilter` masks `TableNotFoundException` as generic `FORBIDDEN` in non-development environments to prevent schema probing.
  - Secure `ReverseProxyOptions` with populated `KnownIPNetworks` (`System.Net.IPNetwork`) and `KnownProxies` to prevent `X-Forwarded-For` spoofing.
  - Dual Kubernetes probes (`/health/live`, `/health/ready`) and 6-phase graceful traffic drain controller for zero-downtime rolling deployments.

- **Zero External Dependencies in Development**:
  - Includes an embedded in-memory SQLite governance catalog and a simulated `TestAuthHandler` enabled exclusively in `Development` mode.

---

## 🏛 Architecture Overview

The solution adheres strictly to **Clean / Onion Architecture** principles with clear layer boundaries:

```
                  ┌───────────────────────────────┐
                  │       Autheris.Api          │  ASP.NET Core Host, Middleware,
                  │                               │  Health Checks, DI Configuration, Webhooks
                  └──────────────┬────────────────┘
                                 │
                  ┌──────────────▼────────────────┐
                  │      Autheris.GraphQL       │  Hot Chocolate Schema, Types, Subscriptions,
                  │                               │  Fusion Router, DataLoaders, MCP Server
                  └──────────────┬────────────────┘
                                 │
                  ┌──────────────▼────────────────┐
                  │     Autheris.Application    │  Use Cases, Consent Resolution, Masking,
                  │                               │  Casbin ABAC, RLS Generation, Streaming RLS
                  └──────────────┬────────────────┘
                                 │
         ┌───────────────────────┴───────────────────────┐
         │                                               │
┌────────▼───────────────────────┐             ┌─────────▼─────────────────────┐
│    Autheris.Infrastructure   │             │       Autheris.Domain       │
│                                │             │                               │
│ SQLite/ADO.NET, Redis/Memory,  │             │ Domain Entities, Value Objects│
│ Cryptography, OpenMetadataSync │             │ (Sid, TableIdentifier), Enums │
└────────────────────────────────┘             └───────────────────────────────┘
```

### Architektur: Kern vs. Extensions

Der Kern (`src/*`) enthält Schnittstellen, Orchestrierung und Governance-Logik (Consent, RLS, Masking, Ratchet, Outbox, Stream-Backbone). **Alle Anbindungen an Fremdsysteme** liegen in `gql_extensions/src/Autheris.Extensions` (ein Ordner je Anbindung) und werden genau einmal über `services.AddGatewayExtensions(gatewayOptions)` in `AddGatewayInfrastructure` eingebunden:

| Anbindung | Extensions-Ordner | Im Kern verbleibend | Hintergrunddienst aktiv bei |
|---|---|---|---|
| Datenkataloge (Purview, Collibra, Alation, OpenMetadata) | `DataCatalog/` | Schnittstellen, `CatalogGovernanceRatchet`, OpenAPI-Ingestion | `Gateway:Catalog:Enabled` |
| ITSM (ServiceNow, Jira, Webhooks) | `Itsm/` | `ItsmWorkflowDispatcher`, Rezertifizierung, Outbox-Worker | `Gateway:Itsm:Enabled` (Kern-Worker) |
| OpenMetadata Policy-Sync | `OpenMetadata/` | Schnittstellen | `Gateway:OpenMetadata:Enabled` |
| Lineage-Export (OpenLineage, OpenJEV) | `Lineage/` | `LineageGraphStore`, Impact-Analyse | – |
| Backstage-Export | `Backstage/` | `IBackstageCatalogExportService`, Endpunkte | – (Endpunkte: `Gateway:Backstage:Enabled`) |
| CDC-Quellen (MSSQL Change Tracking, Debezium) | `Cdc/` | `InMemoryCdcEventChannel`, Stream-RLS | `Gateway:MssqlChangeTracking:Enabled` |
| dbt, OData, Iceberg-Lakehouse | `Dbt/`, `OData/`, `Lakehouse/` | Endpunkte/Executor-Pipeline | – |

Abhängigkeitsrichtung: Extensions → Application/Domain; Api → Extensions. Domain/Application/Infrastructure/GraphQL referenzieren die Extensions nicht und enthalten keine Fremdsystem-Clients (Architekturtests `CoreLayers_ShouldNotHaveDependencyOnExtensions`, `CoreLayers_ShouldNotContainForeignSystemClients`). Der gemeinsame `SsrfProtectionHandler` liegt in `Autheris.Application.Security`.

**Interne Ziele (On-Premises-Anbindungen):** Alle HttpClients der Extensions laufen durch den `SsrfProtectionHandler`, der private, Loopback- und Metadaten-Adressen standardmäßig blockiert und außerhalb von Development HTTPS verlangt. Für Jira, ServiceNow, OpenMetadata, OpenLineage o. ä. im Firmennetz werden die Ziele explizit freigegeben:

```json
"Gateway": {
  "Egress": {
    "TrustedInternalHosts": [ "jira.corp.local", "servicenow.corp.local" ],
    "TrustedInternalNetworks": [ "10.20.0.0/16" ]
  }
}
```

Freigegebene Ziele sind nur von der Prüfung auf private Adressen ausgenommen. Metadaten-Endpunkte sowie Loopback- und Link-Local-Adressen bleiben gesperrt, und HTTPS bleibt außerhalb von Development Pflicht.

Ergänzungen (Nachprüfung E-01 bis E-04):

- Die Allowlist gilt nur für die Integrationen in `Egress:TrustedIntegrations` (nicht gesetzt = `Itsm`, `Catalog`, `OpenMetadata`, `Lineage`; zusätzlich erlaubt: `AuditWorm`, `Cdn`). `Lakehouse` kann die Allowlist nie nutzen, weil seine Ziel-URLs aus Iceberg-Manifesten stammen.
- Die Einträge werden beim Start in jeder Umgebung geprüft: ungültige CIDRs, IPv4-Netze größer als /8, IPv6-Netze größer als /32 und Netze, die 0.0.0.0/8, 127.0.0.0/8, 169.254.0.0/16, 100.64.0.0/10, Multicast/Broadcast, `::`, `::1`, fe80::/10, `::ffff:0:0/96` oder fd00:ec2::/32 berühren, brechen den Start ab. ULA-Netze (fc00::/7) einzelner Standorte bleiben freigebbar. Eine aktive Allowlist erscheint als `WARN:` in der Bypass-Liste.
- Auch für freigegebene Ziele gesperrt: 0.0.0.0/8 und `::`, 100.64.0.0/10, Multicast, Broadcast, IPv4-mapped-Adressen (werden auf IPv4 normalisiert) und die Metadaten-IPs 169.254.169.254, 169.254.170.2, 100.100.100.200, fd00:ec2::254 und 168.63.129.16. Löst ein freigegebener Hostname nicht auf, wird der Aufruf abgelehnt.
- Alle Integrations-Clients folgen keinen Redirects mehr: eine 3xx-Antwort gilt als Fehler. Die Verbindung wird nur zu einer geprüften IP aufgebaut (Prüfung beim Verbindungsaufbau, Schutz gegen DNS-Rebinding). Verbindungen zum System-Proxy (`HTTPS_PROXY`) sind davon ausgenommen.

### Projects

| Project | Target | Description |
|---|---|---|
| [`Autheris.Domain`](src/Autheris.Domain) | `net10.0` | Value Objects (`Sid`, `TableIdentifier`, `CompositeKey`), Models, Options, Enums |
| [`Autheris.Application`](src/Autheris.Application) | `net10.0` | Central execution engine (`GatewayExecutionService`), business services (`ConsentResolutionService`, `ColumnMaskingProvider`, `RlsFilterGenerator`), streaming RLS (`StreamRlsPolicyEnforcer`), MCP services |
| [`Autheris.Infrastructure`](src/Autheris.Infrastructure) | `net10.0` | Persistence (`SqliteGovernanceRepository`, `SqlConnectionFactory`), Caching (`ConsentCacheService`), Multi-Instance Messaging (`RedisEventBus`), Rate Limiting (`RedisRateLimiterService`), Security Handlers (`ForwardAuthAuthenticationHandler`, `BasicAuthenticationHandler`) |
| [`Autheris.GraphQL`](src/Autheris.GraphQL) | `net10.0` | Hot Chocolate 16.6.7 GraphQL engine, dynamic schemas, Subscriptions, Fusion Router (`FusionGatewayExtensions`), MCP Server, queries & mutations |
| [`Autheris.Api`](src/Autheris.Api) | `net10.0` | ASP.NET Core Host, Basic Auth Login (`/api/auth/login`), ForwardAuth header security, rate limiting, anti-CSRF, health probes, ITSM webhooks, MCP endpoints |
| [`Autheris.Extensions`](/root/gql_extensions/src/Autheris.Extensions) | `net10.0` | All connectors to foreign systems: Data Catalogs (Purview, Collibra, Alation, OpenMetadata), ITSM (ServiceNow, Jira, webhooks), OpenMetadata sync, dbt, OData, Iceberg Lakehouse, OpenLineage/OpenJEV, Backstage export, CDC sources (MSSQL Change Tracking, Debezium) |
| [`TrinoSqlEngine`](/root/gql_sqlparser) | `net10.0` | High-performance ANTLR4 SQL Parser, AST Rewriter, WebSQL engine, and parameter extractor (857 parser tests) |
| [`Autheris.Benchmarks`](benchmarks/Autheris.Benchmarks) | `net10.0` | BenchmarkDotNet suites for throughput, cache hit/miss, and masking allocations |
| [`Autheris.Tests.Unit`](tests/Autheris.Tests.Unit) | `net10.0` | 1,180 Unit & Property-Based tests (xUnit, Shouldly, FsCheck, NSubstitute) |
| [`Autheris.Tests.Architecture`](tests/Autheris.Tests.Architecture) | `net10.0` | 8 NetArchTest/reflection rules enforcing Clean Architecture dependency directions (incl. Kern vs. Extensions) |
| [`Autheris.Tests.Integration`](tests/Autheris.Tests.Integration) | `net10.0` | 144 End-to-end integration tests using `WebApplicationFactory<Program>` |
| [`Autheris.Extensions.Tests`](/root/gql_extensions/tests/Autheris.Extensions.Tests) | `net10.0` | 75 Unit & Integration tests for Iceberg Lakehouse, Data Catalogs, dbt, ITSM, and OData |

---

## 🚀 Quick Start

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or higher
- C# 14 compatible toolchain (Visual Studio 2026, JetBrains Rider 2025.3+, or VS Code with C# Dev Kit)

### 1. Build Solution

```bash
dotnet build Autheris.sln -c Release
dotnet build /root/gql_extensions/AutherisExtensions.slnx -c Release
```
*Note: Both solutions enforce `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (0 warnings, 0 errors).*

### 2. Run Tests

```bash
dotnet test Autheris.sln -c Release
dotnet test /root/gql_extensions/AutherisExtensions.slnx -c Release
dotnet test /root/gql_sqlparser/TrinoSqlEngine.csproj -c Release
```
Currently passes **2,261 / 2,261 tests (100% green)** across all test suites:
- **857 TrinoSqlEngine & WebSQL Parser Tests** (ANTLR4 parsing, AST statement validation, parameter extraction, RLS AST-injection, type inference)
- **1,180 Unit Tests** (Authentication & ForwardAuth Security, Multi-Dialect RLS, Declarative SQL-to-API Execution, Casbin ABAC Hot-Reload, Four-Eyes & Delegation Stress, Concurrency & Audit Replication, DataLoader Odd Batching, AST Filter Inference Defense, Zero-Allocation Column Masking, Downstream Lineage BFS, GDPR Art. 15 Disclosure, MCP Guardrails, Differential Privacy)
- **144 Integration Tests** (End-to-end GraphQL pipeline, Traefik ForwardAuth Ingress, Basic Auth Login & Query Verification, Declarative REST & Plugin Zero-Trust enforcement, Declarative SQL Endpoints & OpenAPI 3.0 Generation, Anti-CSRF, Four-Eyes Multi-Step Approval, Vacation Delegation, Red-Team Prompt Injection Defense, Insecure Mode Guardrails, Subscriptions & In-Stream RLS, Fusion Federation)
- **75 Extensions Tests** (Apache Iceberg v2 Lakehouse connector & partition pruning, Microsoft Purview, Collibra, Alation, OpenMetadata catalog sync, GDPR Art. 9 tag enforcement, dbt manifest ingestion & contract validation, ServiceNow/Jira webhooks, OData)
- **8 Architecture Tests** (Clean Architecture layering enforcement via NetArchTest including zero-dependency checks on AspNetCore in Domain and Application and the Kern-vs-Extensions boundary)

### 3. Run Gateway via Docker Container (Fastest / Getting Started)

Ein schlüsselfertiges Container-Image mit integriertem **Microsoft Garnet .NET Cache**, In-Memory Governance-DB (10 Domänen vorbefüllt) und aktivierter Web-UI steht in der GitHub Container Registry bereit:

> **Sicherheitshinweis:** Das Image startet standardmäßig in `Production`. Der unten gezeigte Getting-Started-Modus
> setzt explizit `ASPNETCORE_ENVIRONMENT=Development` plus das Opt-in `AUTHERIS_ALLOW_DEV_IN_CONTAINER=true` und ist
> ausschließlich für lokale Tests gedacht.

```bash
# Direkt via Docker Run (Ports 8080 HTTP / 8081 HTTPS) – lokaler Getting-Started-Modus
docker run -d -p 8080:8080 -p 8081:8081 \
  -e ASPNETCORE_ENVIRONMENT=Development -e AUTHERIS_ALLOW_DEV_IN_CONTAINER=true \
  --name gql-gateway ghcr.io/themulle/gql:getting-started

# Oder via Docker Compose (Basis = Production, Override = lokaler Dev-Modus)
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d
```

#### Sofort verfügbare Endpunkte auf Port 8080:
- **Banana Cake Pop GraphQL IDE**: [`http://localhost:8080/graphql`](http://localhost:8080/graphql)
- **Swagger UI (REST / OpenAPI Explorer)**: [`http://localhost:8080/docs`](http://localhost:8080/docs)
- **Declarative SQL OpenAPI 3.0 Spezifikation**: [`http://localhost:8080/api/v1/queries/openapi.json`](http://localhost:8080/api/v1/queries/openapi.json)
- **Declarative SQL-to-API Endpoints**: `GET` / `POST http://localhost:8080/api/v1/queries/{name}`
- **Governed WebSQL Ausführung**: `POST http://localhost:8080/api/v1/sql`
- **OData v4 Datenabruf (REST / Excel / Power BI)**: `GET http://localhost:8080/odata/v4/{domain}/{schema}/{table}`
- **OpenAPI 3.1 Spezifikation (OData)**: [`http://localhost:8080/odata/v4/$openapi`](http://localhost:8080/odata/v4/$openapi)
- **MCP (Model Context Protocol für KI-Agenten)**: `POST http://localhost:8080/mcp`
- **Health Checks**: [`http://localhost:8080/health/live`](http://localhost:8080/health/live) & [`/health/ready`](http://localhost:8080/health/ready)

### 4. Run Gateway Locally from Source

```bash
dotnet run --project src/Autheris.Api/Autheris.Api.csproj
```

The service starts locally on `http://localhost:5000` (or `https://localhost:5001`). In `Development` mode, the Hot Chocolate Banana Cake Pop GraphQL IDE is accessible at:
- `http://localhost:5000/graphql`

---

## 🔒 Enterprise Authentication & Ingress Integration

Autheris features a Smart Dynamic Authentication Scheme Selector supporting multiple production and development authentication mechanisms:

### 1. Kubernetes Ingress / Traefik ForwardAuth
When running behind an ingress controller (such as Traefik Ingress) that performs authentication (via Authelia, Keycloak, Authentik, or OAuth2-Proxy):
- Ingress passes caller identity and groups via headers:
  - `X-Forwarded-User`: Username or user SID
  - `X-Forwarded-Groups`: Comma-separated group SIDs or names
  - `X-Forwarded-Roles`: Comma-separated roles (e.g. `GovernanceAdmin,DataOwner`)
  - `X-Forwarded-Email`: User email address
  - `X-Forwarded-Secret`: Pre-shared secret header ensuring header authenticity
- Gateway verifies incoming proxy IP against `TrustedNetworks` (CIDR blocks) and `TrustedProxies`.
- Timing-safe comparison of `X-Forwarded-Secret` against Key Vault / AppSettings.

```bash
curl -X POST http://localhost:5000/graphql \
  -H "Content-Type: application/json" \
  -H "GraphQL-Preflight: 1" \
  -H "X-Forwarded-User: S-1-5-21-CONSUMER-1" \
  -H "X-Forwarded-Groups: S-1-5-21-FINANCE-ANALYSTS" \
  -H "X-Forwarded-Secret: YOUR-SHARED-SECRET" \
  -d '{"query": "{ catalog { domain schemaName tableName displayName } }"}'
```

### 2. HTTP Basic Authentication & `/api/auth/login`
Supports direct Basic Auth headers on GraphQL requests and a dedicated login endpoint:
- `GET /api/auth/login` or `POST /api/auth/login` verifies credentials and returns user identity metadata.
- GraphQL queries authenticate directly via `Authorization: Basic base64(user:password)`.

```bash
# Verify credentials via login endpoint
curl -u "analyst:Secret123!" http://localhost:5000/api/auth/login

# Direct GraphQL query with Basic Auth
curl -X POST http://localhost:5000/graphql \
  -u "analyst:Secret123!" \
  -H "Content-Type: application/json" \
  -H "GraphQL-Preflight: 1" \
  -d '{"query": "{ catalog { domain schemaName tableName } }"}'
```

### 3. Microsoft Entra ID (Azure AD) & AD FS JWT Bearer
- Native JWT Bearer validation against Entra ID / ADFS authority.
- `EnterpriseClaimsTransformation` normalizes Active Directory object IDs (`oid`), on-premises SIDs (`onprem_sid`), and primary group SIDs (`primarygroupsid`) into canonical `ClaimTypes.PrimarySid` and `ClaimTypes.GroupSid`.

```bash
curl -X POST http://localhost:5000/graphql \
  -H "Authorization: Bearer eyJhbGciOi..." \
  -H "Content-Type: application/json" \
  -H "GraphQL-Preflight: 1" \
  -d '{"query": "{ catalog { domain schemaName tableName } }"}'
```

### 4. Authentication Simulation in Development
In `Development` mode (with `Gateway:Authentication:EnableTestAuthHandler = true`), you can test any SID, role, and group membership using custom HTTP request headers:

| Header | Description | Example |
|---|---|---|
| `X-Test-User-Sid` | Caller user SID | `S-1-5-21-DATAOWNER-1` or `S-1-5-21-CONSUMER-1` |
| `X-Test-Group-Sids` | Comma-separated group SIDs | `S-1-5-21-FINANCE-ANALYSTS,S-1-5-21-STAFF` |
| `X-Test-Roles` | Comma-separated roles | `GovernanceAdmin` or `ClusterAdmin` |
| `GraphQL-Preflight` | Anti-CSRF header (required on POST) | `1` |

> ⚠️ **Security Warning**: `TestAuthHandler` is strictly blocked in production environments (`IsProduction()`). Any attempt to enable it outside of `Development` triggers a fatal startup validation exception.

### Example: Query Active Catalog via cURL

```bash
curl -X POST http://localhost:5000/graphql \
  -H "Content-Type: application/json" \
  -H "GraphQL-Preflight: 1" \
  -H "X-Test-User-Sid: S-1-5-21-CONSUMER-1" \
  -d '{"query": "{ catalog { domain schemaName tableName displayName isConsented columns } }"}'
```

### Example: Request Access to a Table

```bash
curl -X POST http://localhost:5000/graphql \
  -H "Content-Type: application/json" \
  -H "GraphQL-Preflight: 1" \
  -H "X-Test-User-Sid: S-1-5-21-CONSUMER-1" \
  -d '{"query": "mutation { requestTableAccess(domain: \"finance\", schema: \"dbo\", table: \"finance_table_1\", justification: \"Q4 Financial Audit Analysis\", daysValid: 30) { requestId status message } }"}'
```

---

## ⚙️ Configuration (`appsettings.json`)

Key configuration settings under the `Gateway` section:

```json
{
  "Gateway": {
    "HighAvailability": {
      "DrainDelaySeconds": 5,
      "ShutdownTimeoutSeconds": 40
    },
    "Authentication": {
      "Domain": "CORP.LOCAL",
      "EnableTestAuthHandler": false,
      "ForwardAuth": {
        "Enabled": true,
        "UserHeader": "X-Forwarded-User",
        "GroupsHeader": "X-Forwarded-Groups",
        "RolesHeader": "X-Forwarded-Roles",
        "SharedSecretHeader": "X-Forwarded-Secret",
        "SharedSecret": "YOUR-SHARED-SECRET",
        "RequireTrustedProxy": true,
        "TrustedNetworks": ["127.0.0.1/32", "::1/128", "10.244.0.0/16"]
      },
      "BasicAuth": {
        "Enabled": true,
        "Users": [
          {
            "Username": "analyst",
            "Password": "SecretPassword123!",
            "Roles": ["DataConsumer"],
            "UserSid": "S-1-5-21-CONSUMER-1",
            "GroupSids": ["S-1-5-21-FINANCE-ANALYSTS"]
          }
        ]
      },
      "EntraId": {
        "Enabled": false,
        "TenantId": "00000000-0000-0000-0000-000000000000",
        "ClientId": "00000000-0000-0000-0000-000000000000",
        "Audience": "api://gql-gateway"
      },
      "Adfs": {
        "Enabled": false,
        "MetadataAddress": "https://adfs.corp.local/federationmetadata/2007-06/federationmetadata.xml",
        "Audience": "microsoft:identityserver:gql-gateway"
      }
    },
    "GovernanceDb": {
      "Provider": "Sqlite",
      "ConnectionString": "Data Source=governance.db",
      "SeedDemoData": true
    },
    "DataSources": {
      "finance": {
        "Provider": "SqlServer",
        "ConnectionString": "Server=sql-finance.corp.local;Database=FinanceDb;Integrated Security=SSPI;"
      },
      "hr": {
        "Provider": "PostgreSql",
        "ConnectionString": "Host=pg-hr.corp.local;Database=HrDb;Username=gql_app;Password=secret"
      }
    },
    "Caching": {
      "L1MemoryCache": {
        "SizeLimitMb": 512,
        "DefaultTtlMinutes": 10
      },
      "Redis": {
        "Configuration": "redis-cluster.corp.local:6379,abortConnect=false"
      }
    },
    "ReverseProxy": {
      "Enabled": true,
      "KnownNetworks": ["127.0.0.1/32", "::1/128", "10.0.0.0/8"],
      "KnownProxies": []
    },
    "GraphQL": {
      "EndpointPath": "/graphql",
      "MaxAllowedExecutionDepth": 10,
      "MaxAllowedComplexity": 1500
    },
    "DataMasking": {
      "HmacKeyId": "key-2026-q1"
    },
    "Catalog": {
      "Enabled": true,
      "Provider": "MicrosoftPurview",
      "SyncMode": "Mirror",
      "SyncIntervalMinutes": 60,
      "Purview": {
        "Endpoint": "https://corp-purview.purview.azure.com",
        "TenantId": "72f988bf-86f1-41af-91ab-2d7cd011db47",
        "ClientId": "a820c78a-f326-4d1d-91b4-2195f1342618"
      }
    },
    "Insecure": {
      "warn_allow_all_cors_origins": false,
      "warn_disable_rate_limiting": false,
      "danger_bypass_authorization": false,
      "danger_allow_anonymous_queries": false,
      "danger_bypass_webhook_signature_validation": false
    }
  }
}
```

---

## 🔍 Data Catalog, Lineage & GDPR Operations

Autheris exposes comprehensive administrative and governance operations via GraphQL:

### 1. Synchronize Data Catalog (Microsoft Purview / Collibra / Alation / OpenMetadata)

Trigger an on-demand catalog sync (or dry-run) to mirror metadata and update GDPR Art. 9 / PII tag classifications:

```graphql
mutation SyncEnterpriseCatalog {
  syncDataCatalog(dryRun: false) {
    success
    syncedTablesCount
    syncedColumnsCount
    maskedColumnsCount
    art9ProtectedTablesCount
    affectedTables {
      domain
      schema
      tableName
    }
    warnings
  }
}
```

### 2. Pre-Schema-Change Impact Analysis (Downstream Consumers)

Before renaming or dropping columns, assess affected BI dashboards, ETL pipelines, and active readers:

```graphql
query AssessSchemaChangeImpact {
  tableConsumers(domain: "sales", schema: "dbo", tableName: "orders", timeWindowDays: 30) {
    table
    breakingChangeRisk # CRITICAL, HIGH, MEDIUM, LOW
    totalDownstreamCount
    activeReadersCount
    lastAccessedAt
    downstreamConsumers {
      id
      name
      type # Dashboard, Pipeline, ExternalService
      ownerTeam
      ownerEmail # Masked for non-admins (Zero-Trust)
      distanceFromRoot
    }
    runtimeConsumers {
      actorSid
      clientType # ServicePrincipal, InteractiveUser, DownstreamSystem
      queryCount
      lastSeenAt
    }
    recommendedMitigations
  }
}
```

### 3. GDPR Art. 15 Disclosure Reporting (Right of Access)

Generate legally binding disclosure reports under Art. 15 Abs. 1 Bst. c DSGVO for auditors or data subjects:

```graphql
query GenerateGdprDisclosureReport {
  gdprDataDisclosureReport(
    domain: "healthcare"
    schema: "dbo"
    tableName: "patient_diagnoses"
    timeWindowDays: 365
  ) {
    targetTable
    totalAccessEvents
    sensitivityCategories
    legalBasisNotice
    disclosedRecipients {
      recipientSid
      recipientCategory
      purpose
      firstAccess
      lastAccess
      totalQueries
      accessedColumns
      maskingRuleApplied
    }
  }
}
```

## 📦 Parquet-Ausgabe

Alle Daten-Ausgabekanäle liefern ihr Ergebnis auf Wunsch als echte Apache-Parquet-Datei (Parquet.Net, eine Row-Group, Snappy-komprimiert) statt JSON:

```bash
curl -H "Accept: application/vnd.apache.parquet" -H "GraphQL-Preflight: 1" \
     -H "Content-Type: application/json" \
     -d '{"query":"{ table(domain:\"sales\", name:\"orders\") { jsonRows } }"}' \
     -o orders.parquet http://localhost:8080/graphql
```

- **Header:** `Accept: application/vnd.apache.parquet` (Alias `application/x-parquet`). Parquet wird nur gewählt, wenn der Typ explizit mit q>0 angegeben ist und kein anderer Typ eine höhere q-Präferenz hat (`*/*` zählt nicht). Antwort: `Content-Type: application/vnd.apache.parquet`, `Content-Disposition: attachment`, `X-Row-Count`, `X-Export-Truncated`, `Vary: Accept`, `Cache-Control: no-store`.
- **Kanäle:** GraphQL (`/graphql`), WebSQL (`POST /api/sql`, `/api/v1/sql`), SQL-Endpoints (`/api/v1/queries/{name}`), OData-Entity-Sets (`/odata/v4/{domain}/{schema}/{table}`). Andere Routen antworten auf einen reinen Parquet-Accept-Header mit `406 Not Acceptable`; enthält der Header zusätzlich `application/json` oder `*/*`, wird normal JSON geliefert.
- **Governance:** Die Konvertierung ist eine reine Ausgabe-Transformation nach RLS, Masking, Consent und Egress-Interceptors – Parquet enthält exakt die Daten der JSON-Antwort (maskierte Werte bleiben maskiert).
- **Grenzen:** `GatewayOptions:ParquetEgress:MaxRowsPerFile` (Default 100000, darüber `X-Export-Truncated: true`), `MaxBufferedSourceBytes` (Default 64 MB für die gepufferte GraphQL-JSON-Antwort, darüber `413`), `Compression` (`None`/`Snappy`/`Gzip`), `FlattenNestedStructures` (verschachtelte Objekte → Spalten `parent.child`, Listen → JSON-String).
- **GraphQL:** genau ein Root-Feld pro Operation; Zeilenquelle ist `jsonRows`, eine Liste `rows`/`items`/`nodes`, `edges[].node` oder eine Liste von Objekten. Skalare Ergebnisse → `406`.
- **Fehler bleiben JSON:** GraphQL-`errors` (Header `X-Parquet-Conversion: skipped-errors`), Policy-/Validierungsfehler und alle Status ≠ 200 werden unverändert als JSON geliefert.
- **Ausgenommen:** MCP (`/mcp`, JSON-RPC-Protokoll), Subscriptions/SSE/WebSockets, Webhooks, Health und Metrics werden nie konvertiert.
- `GET /api/export/parquet/{domain}/{table}` liefert weiterhin nur ein Schema-Gerüst ohne Zeilen.

## 📝 Code Review & Export Artifacts

For offline security audits, external architecture reviews, or LLM-assisted code reviews, pre-bundled review and diff files can be generated in the repository root:

| Artifact | Size | Description | Target Audience |
| :--- | :--- | :--- | :--- |
| [`review.txt`](file:///root/gql/review.txt) | ~436 KB | Consolidated bundle of all production C# source code (`src/**/*.cs`, 73 files) with a Table of Contents and standard file separators (`FILE: <path>`). | AI/LLM Reviewers, Single-File Ingestion |
| [`src_codebase_review.txt`](file:///root/gql/src_codebase_review.txt) | ~436 KB | Exact mirror of `review.txt` for tooling expecting the `src_codebase_review` naming convention. | Automated CI/CD Review Pipelines |
| [`full_codebase_review.txt`](file:///root/gql/full_codebase_review.txt) | ~812 KB | Extended bundle including all production (`src/`), test (`tests/`), and benchmark (`benchmarks/`) C# code (108 files total). | Comprehensive Test & Benchmark Audits |
| [`review_diff.patch`](file:///root/gql/review_diff.patch) / [`codebase.diff`](file:///root/gql/codebase.diff) | ~928 KB | Complete unified Git diff across all commits relative to upstream `origin/main`. | Git / Patch Tools, PR Reviewers |
| [`src_codebase.diff`](file:///root/gql/src_codebase.diff) | ~454 KB | Unified Git diff restricted strictly to production code under `src/`. | Production Code Reviewers |

### Re-generating Review Artifacts

To regenerate these review bundles and diffs after modifying code:

```bash
# 1. Regenerate production source bundle (review.txt)
(
  echo "================================================================================"
  echo "AUTHERIS PRODUCTION SOURCE CODE EXPORT (src/**/*.cs)"
  echo "Generated: $(date -u '+%Y-%m-%d %H:%M:%SZ')"
  echo "================================================================================"
  echo ""
  echo "TABLE OF CONTENTS:"
  find src -name "*.cs" | sort | while read -r f; do echo "  - $f"; done
  echo ""
  find src -name "*.cs" | sort | while read -r f; do
    echo "================================================================================"
    echo "FILE: $f"
    echo "================================================================================"
    cat "$f"
    echo ""
  done
) > review.txt

# 2. Regenerate git diffs against origin/main
git diff origin/main...HEAD > review_diff.patch
git diff origin/main...HEAD -- src > src_codebase.diff
```

---

## 📊 Benchmarks

Run the benchmark suite using BenchmarkDotNet:

```bash
dotnet run --project benchmarks/Autheris.Benchmarks/Autheris.Benchmarks.csproj -c Release
```

Included benchmark suites:
- **`ConsentResolutionBenchmark`**: Resolution latency across 100+ subject SIDs and permissive consent rules.
- **`ConsentCacheBenchmark`**: L1/L2 cache hit vs. miss latency with epoch invalidation checks.
- **`ColumnMaskingBenchmark`**: Throughput and memory allocation across Redaction, Masking, and HMAC Pseudonymization.
- **`GatewayLoadBenchmark`**: High-concurrency end-to-end GraphQL execution.

---

## 📚 Detailed Documentation

For comprehensive engineering and operational guides, consult the `docs/` directory:

- [**arc42 Architecture Documentation**](docs/architecture/arc42.md) – System context, building blocks, runtime view, deployment, and quality goals.
- [**Configuration Guide**](docs/configuration-guide.md) – Comprehensive reference of all `appsettings.json` sections, environment variables, startup validations, and production hardening.
- [**Developer Guide**](docs/developer-guide.md) – Guidelines for extending services, adding resolvers, writing TDD tests, and coding standards.
- [**Threat Model & Security Whitepaper**](docs/threat-model/threat-model.md) – STRIDE analysis, attack surface, mitigation matrices, and cryptographic guarantees.
- [**Operations & HA Runbook**](docs/operations-runbook.md) – Rolling updates, graceful traffic drain protocol, alerts, backup & restore procedures.
- [**Architecture Decision Records (ADRs)**](docs/adr/) – Key architectural decisions (ADR-001 through ADR-009).

---

## 📄 License

This repository follows a dual-licensing / Open-Core model:

- **Autheris Core (`gql/` & `gql_sqlparser/`)**: Licensed under the **[Business Source License 1.1 (BSL 1.1)](LICENSE)**.
  - **Free for Internal Use**: Free to use in development, testing, and internal enterprise production environments.
  - **Cloud Hosting & Managed Services**: Offering Autheris as a hosted service, managed API gateway, or cloud service to third parties is strictly subject to a commercial license.
  - **Change License**: Transitions automatically to the **Apache License, Version 2.0** on **2029-10-01**.
- **Enterprise Extensions (`gql_extensions/`)**: Proprietary enterprise modules (Apache Iceberg Lakehouse, Data Catalog Sync for Microsoft Purview/Collibra, ServiceNow/Jira ITSM, WORM S3 Compliance Export) are subject to a **[Commercial Enterprise License](../gql_extensions/LICENSE)**. Commercial distribution and reselling are reserved exclusively for the copyright holders.
