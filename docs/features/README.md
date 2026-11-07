# Autheris Feature Catalog

Reference documentation for the core capabilities of **Autheris**.

Every feature document includes an architectural overview, configuration options, and fail-closed security behavior.

> [!NOTE]
> **Documentation Standard for Contributors:**
> - Individual feature documentation resides in `docs/features/` using the naming scheme `f-<category>-<number>-<slug>.md`.
> - Register feature documents in the index table below.
> - Feature documents describe the working state, architecture, and usage. Do not include transient development progress, WIP notes, or task checklists (those belong in [`docs/plans/`](../plans/)). See [`AGENTS.md`](../../AGENTS.md).

| Feature ID | Title | Documentation |
| :--- | :--- | :--- |
| **F-AI-05** | Human-in-the-Loop Step-Up Approval & Two-Phase Admin MCP Planning | [f-ai-05-hitl-step-up-approval.md](f-ai-05-hitl-step-up-approval.md) |
| **F-AI-11** | MCP Dataset Tools (Catalog Discovery and GraphQL Queries for AI Agents) | [f-ai-11-mcp-dataset-tools.md](f-ai-11-mcp-dataset-tools.md) |
| **F-AI-12** | Official Hybrid MCP Gateway (Query Tools, Schema Grounding, Safety Simulator & Provenance) | [f-ai-12-hybrid-mcp-gateway.md](f-ai-12-hybrid-mcp-gateway.md) |
| **F-AI-14** | MCP Schema-RAG & Hybrid-Vector Search Engine | [f-ai-14-mcp-schema-rag-vector-search.md](f-ai-14-mcp-schema-rag-vector-search.md) |
| **F-AI-16** | Universal Multi-App Document Store, Omnichannel Passthrough & MCP Doc Tools | [f-ai-16-universal-doc-store-mcp.md](f-ai-16-universal-doc-store-mcp.md) |
| **F-AI-17** | Privacy-Coarsened Column Value Profiles & Externally Uploaded Sample Rows | [f-ai-17-column-value-profiles-and-synthetic-samples.md](f-ai-17-column-value-profiles-and-synthetic-samples.md) |
| **F-API-03** | Dual-Access Exposure (OData v4 for Power BI / Excel & Dynamic OpenAPI 3.1) | [f-api-03-odata-openapi.md](f-api-03-odata-openapi.md) |
| **F-API-04** | Declarative HTTP/OpenAPI & Swagger Data Source Ingestion | [f-api-04-openapi-ingestion.md](f-api-04-openapi-ingestion.md) |
| **F-API-09** | Pre-Flight Datasource Connectivity Testing, Secret Scrubbing & System Metadata | [f-api-09-datasource-connectivity-testing.md](f-api-09-datasource-connectivity-testing.md) |
| **F-ARCH-10** | Standardized Connector SPI & Customer DLL Plugin Host | [f-arch-10-connector-spi.md](f-arch-10-connector-spi.md) |
| **F-ARCH-11** | Envoy External Authorization (`ext_authz`) Decision Endpoint | [f-arch-11-envoy-external-authorization.md](f-arch-11-envoy-external-authorization.md) |
| **F-AUDIT-01** | Hardened Cryptographic Audit & Asymmetric KMS Anchors | [f-audit-01-hardened-cryptographic-audit.md](f-audit-01-hardened-cryptographic-audit.md) |
| **F-AUDIT-02** | Cryptographic Audit Retention Purge & Checkpoint Anchors | [f-audit-02-retention-purge-and-checkpoints.md](f-audit-02-retention-purge-and-checkpoints.md) |
| **F-AUTH-03** | RFC 6238 TOTP Two-Factor Authentication & HitL Step-Up Engine | [f-auth-03-rfc6238-totp-2fa-stepup.md](f-auth-03-rfc6238-totp-2fa-stepup.md) |
| **F-AUTH-04** | SQL Server Windows Authentication (NTLM & Kerberos) in Containers | [f-auth-04-sqlserver-windows-auth-ntlm-kerberos.md](f-auth-04-sqlserver-windows-auth-ntlm-kerberos.md) |
| **F-AUTH-DX** | Basic Auth with Cookie Session and Developer Helpers | [f-auth-dx-basic-auth-session.md](f-auth-dx-basic-auth-session.md) |
| **F-CDC-02** | Native MSSQL Change Tracking Ingestion Provider | [f-cdc-02-mssql-change-tracking.md](f-cdc-02-mssql-change-tracking.md) |
| **F-DATA-02** | Governed WebSQL Engine, Cross-Source Pushdown, Bulk Streaming & Parquet Egress | [f-data-02-governed-websql.md](f-data-02-governed-websql.md) |
| **F-DATA-03** | Embedded In-Memory OLAP & Cross-Source Federation via DuckDB.NET | [f-data-03-duckdb-olap.md](f-data-03-duckdb-olap.md) |
| **F-DATA-06** | Governed REST Data API, Change Detection & System Virtual Tables | [f-data-06-governed-rest-data-api.md](f-data-06-governed-rest-data-api.md) |
| **F-DATA-07** | Asynchronous Query Job Engine & Sandbox Storage | [f-data-07-async-query-jobs.md](f-data-07-async-query-jobs.md) |
| **F-DATA-08** | Governed Aggregations & Minimum Group Size (Small-Group Suppression) | [f-data-08-governed-aggregations-and-k-anonymity.md](f-data-08-governed-aggregations-and-k-anonymity.md) |
| **F-DIALECT-02** | Governed AST SQL Compiler (DQL and DML; PostgreSQL, SQL Server, Oracle, Databricks) | [f-dialect-02-ast-sql-compiler.md](f-dialect-02-ast-sql-compiler.md) |
| **F-EXT-01** | Customer C# DLL Plugin & Workflow Webhook Subsystem | [f-ext-01-generic-plugin-events-and-webhooks.md](f-ext-01-generic-plugin-events-and-webhooks.md) |
| **F-GOV-09** | Virtual Filters (Relation-Scoped Cross-Channel Mandatory Row Filtering) | [f-gov-09-virtual-filters.md](f-gov-09-virtual-filters.md) |
| **F-GOV-11** | Declarative Access Profiles & Materialized Time-Limited Grants | [f-gov-11-declarative-access-profiles.md](f-gov-11-declarative-access-profiles.md) |
| **F-GOV-12** | Governance Database Storage (PostgreSQL, SQL Server) & Schema Shape Verification | [f-gov-12-multi-engine-governance-storage.md](f-gov-12-multi-engine-governance-storage.md) |
| **F-GOV-13** | Enterprise Data Classification, PII Tagging, Tag Projection & Sensitivity Governance | [f-gov-13-data-classification-and-pii-governance.md](f-gov-13-data-classification-and-pii-governance.md) |
| **F-GOV-14** | Federated Virtual Filters on Web-APIs | [f-gov-14-federated-virtual-filters.md](f-gov-14-federated-virtual-filters.md) |
| **F-GOV-15** | Consent Workflow API, Four-Eyes Approval, Static Query Analysis & Scope Governance | [f-gov-15-declarative-consents-scopes.md](f-gov-15-declarative-consents-scopes.md) |
| **F-GOV-16** | Authorized Data Discovery, Data Ownership & Response Masking Metadata | [f-gov-16-catalog-ownership-transparency.md](f-gov-16-catalog-ownership-transparency.md) |
| **F-GOV-19** | GraphQL Relation Exposure, Subgraph Federation & Data Source Boundaries | [f-gov-19-cross-source-graphql-relations.md](f-gov-19-cross-source-graphql-relations.md) |
| **F-GOV-21** | Declared Table Keys & Indexes, Deterministic Read Ordering & Least-Privilege Grants | [f-gov-21-declared-table-keys-and-read-ordering.md](f-gov-21-declared-table-keys-and-read-ordering.md) |
| **F-GOV-22** | Time-Bound Individual Grants (Grant Policy, Class Maxima, Renewal & Read-Time Clamp) | [f-gov-22-time-bound-individual-grants.md](f-gov-22-time-bound-individual-grants.md) |
| **F-GOV-23** | Row Filter Key-Set Cache | [f-gov-23-row-filter-key-set-cache.md](f-gov-23-row-filter-key-set-cache.md) |
| **F-MASK-02** | Extended Column Masking Engine (GEO_JITTER, PARTIAL_MASK, TOKENIZATION) | [f-mask-02-extended-masking-engine.md](f-mask-02-extended-masking-engine.md) |
| **F-OPS-02** | Enterprise High Availability, Kubernetes Helm & Distributed Concurrency | [f-ops-02-high-availability-kubernetes-helm.md](f-ops-02-high-availability-kubernetes-helm.md) |
| **F-PERF-09** | GraphQL-to-SQL AST Single-Query Compiler & Incremental Delivery (`@defer`/`@stream`) | [f-perf-09-single-query-pushdown.md](f-perf-09-single-query-pushdown.md) |
| **F-PERF-13** | Usage Accounting, Cost Models, Resource Groups & Quota Rate Limiting | [f-perf-13-cost-and-quota-rate-limiting.md](f-perf-13-cost-and-quota-rate-limiting.md) |
| **F-SEC-05** | Gateway Hardening & Adaptive Defense Suite | [f-sec-05-gateway-hardening-defense.md](f-sec-05-gateway-hardening-defense.md) |
| **F-SQL-01** | Declarative SQL-to-API Engine & Auto-OpenAPI | [f-sql-01-declarative-sql-endpoints.md](f-sql-01-declarative-sql-endpoints.md) |
| **F-SQL-02** | Governed Stored Procedures & Multi-DB TVFs | [f-sql-02-governed-stored-procedures.md](f-sql-02-governed-stored-procedures.md) |
