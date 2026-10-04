# Autheris Enterprise Features Catalog

Overview of all production-ready, Generally Available (GA) enterprise features in **Autheris**.

Every feature document includes an architectural overview, explanation of business value, concrete usage examples, and configuration specifications.

| Feature ID | Title | Documentation |
| :--- | :--- | :--- |
| **F-AI-02** | Semantic MCP Compiler & Schema Grounding | [f-ai-02-semantic-mcp-compiler.md](f-ai-02-semantic-mcp-compiler.md) |
| **F-AI-03** | Dynamic Few-Shot Golden Query Injection | [f-ai-03-golden-queries.md](f-ai-03-golden-queries.md) |
| **F-AI-04** | Pre-Flight Query Simulator & Safety Limits | [f-ai-04-preflight-simulator.md](f-ai-04-preflight-simulator.md) |
| **F-AI-05** | Human-in-the-Loop Step-Up Approval | [f-ai-05-hitl-step-up-approval.md](f-ai-05-hitl-step-up-approval.md) |
| **F-AI-06** | Explainable AI & Provenance Footnotes | [f-ai-06-provenance-footnoting.md](f-ai-06-provenance-footnoting.md) |
| **F-AI-07** | Dynamic Semantic Schema Pruning & Just-in-Time MCP Tools | [f-ai-07-dynamic-semantic-schema-pruning.md](f-ai-07-dynamic-semantic-schema-pruning.md) |
| **F-AI-08** | FOCUS FinOps Accounting for Token & Compute | [f-ai-08-focus-finops-accounting.md](f-ai-08-focus-finops-accounting.md) |
| **F-AI-09** | Native Vector Database & RAG Egress (pgvector, Qdrant, Milvus) | [f-ai-09-native-vector-database-rag-egress.md](f-ai-09-native-vector-database-rag-egress.md) |
| **F-AI-10** | Semantic Query Cache & Autonomous Policy Recommendation | [f-ai-10-semantic-cache-policy-recommendation.md](f-ai-10-semantic-cache-policy-recommendation.md) |
| **F-API-03** | Dual-Access Exposure (OData v4 & Dynamic OpenAPI 3.1) | [f-api-03-odata-openapi.md](f-api-03-odata-openapi.md) |
| **F-API-04** | Upstream Web API & Microservice Ingestion via OpenAPI | [f-api-04-openapi-ingestion.md](f-api-04-openapi-ingestion.md) |
| **F-API-07** | Canonical System Metadata & Monitoring Schema ($system) | [f-api-07-system-metadata-monitoring.md](f-api-07-system-metadata-monitoring.md) |
| **F-ARCH-10** | Standardized Connector SPI (Trino Pattern) | [f-arch-10-connector-spi.md](f-arch-10-connector-spi.md) |
| **F-CDC-02** | Native MSSQL Change Tracking Ingestion Provider | [f-cdc-02-mssql-change-tracking.md](f-cdc-02-mssql-change-tracking.md) |
| **F-CDC-03** | Zero-Kafka PostgreSQL CDC via Logical Streaming Replication | [f-cdc-03-zero-kafka-postgresql-cdc.md](f-cdc-03-zero-kafka-postgresql-cdc.md) |
| **F-DATA-01** | Hierarchical Parquet Egress & Nested Query Serialization | [f-data-01-parquet-egress.md](f-data-01-parquet-egress.md) |
| **F-DATA-02** | Governed WebSQL Engine | [f-data-02-governed-websql.md](f-data-02-governed-websql.md) |
| **F-DATA-03** | Embedded In-Memory OLAP via DuckDB.NET | [f-data-03-duckdb-olap.md](f-data-03-duckdb-olap.md) |
| **F-DATA-04** | Native Apache Arrow Flight SQL Egress & Zero-Copy Analytics Pipeline | [f-data-04-arrow-flight-sql.md](f-data-04-arrow-flight-sql.md) |
| **F-DBT-1** | dbt Data Health Circuit Breaker | [f-dbt-01-health-circuit-breaker.md](f-dbt-01-health-circuit-breaker.md) |
| **F-DBT-2** | dbt Model Contract Enforcement & Breaking-Change CI Gate | [f-dbt-02-contract-enforcement.md](f-dbt-02-contract-enforcement.md) |
| **F-DBT-3** | Live-Telemetry Exposure Publisher | [f-dbt-03-telemetry-exposures.md](f-dbt-03-telemetry-exposures.md) |
| **F-DBT-4** | Zero-Touch dbt Cloud & Orchestrator Webhook Integration | [f-dbt-04-orchestrator-webhooks.md](f-dbt-04-orchestrator-webhooks.md) |
| **F-DBT-6** | Policy & RLS Auto-Sync from dbt Metadata | [f-dbt-06-policy-rls-sync.md](f-dbt-06-policy-rls-sync.md) |
| **F-DOC-01** | Omnichannel Semantic Documentation Passthrough | [f-doc-01-omnichannel-documentation.md](f-doc-01-omnichannel-documentation.md) |
| **F-DX-01** | Zero-Config Developer Quickstart & Dev Portal Hub | [f-dx-01-developer-quickstart.md](f-dx-01-developer-quickstart.md) |
| **F-GOV-06** | Multi-Stage Pushdown Cascades & Cross-Domain Joins | [f-gov-06-cross-domain-joins.md](f-gov-06-cross-domain-joins.md) |
| **F-GOV-08** | Dynamic Schema Contracts & Tag-Based Projection (@tag) | [f-gov-08-schema-contracts-tag-projection.md](f-gov-08-schema-contracts-tag-projection.md) |
| **F-OPEN-01** | OpenSchema Mode, Multi-File OpenAPI & Catalog Slicing | [f-open-01-openschema-catalog-slicing.md](f-open-01-openschema-catalog-slicing.md) |
| **F-OPS-01** | AST-Aware Production Traffic Shadowing & Dark Replay | [f-ops-01-traffic-shadowing-dark-replay.md](f-ops-01-traffic-shadowing-dark-replay.md) |
| **F-PERF-08** | Hierarchical Resource Groups & Workload Queuing | [f-perf-08-hierarchical-resource-groups.md](f-perf-08-hierarchical-resource-groups.md) |
| **F-PERF-09** | GraphQL-to-SQL AST Single-Query Compiler | [f-perf-09-single-query-pushdown.md](f-perf-09-single-query-pushdown.md) |
| **F-PERF-10** | Split-Engine & Zero-LOH Streaming Result Pipelining | [f-perf-10-streaming-pipelining.md](f-perf-10-streaming-pipelining.md) |
| **F-PERF-11** | Multi-Tenant Isolated Query Plan Cache & Kestrel Tuning | [f-perf-11-query-plan-cache.md](f-perf-11-query-plan-cache.md) |
| **F-PERF-12** | Incremental Delivery via @defer & @stream | [f-perf-12-incremental-delivery.md](f-perf-12-incremental-delivery.md) |
| **F-SEC-04** | Relationship-Based Access Control (ReBAC via OpenFGA / Zanzibar) | [f-sec-04-rebac-openfga.md](f-sec-04-rebac-openfga.md) |
| **F-SQL-01** | Declarative SQL-to-API Engine & Auto-OpenAPI | [f-sql-01-declarative-sql-endpoints.md](f-sql-01-declarative-sql-endpoints.md) |
| **P1** | Enterprise Data Catalog Connectors (Purview, Collibra, OpenMetadata) | [p01-data-catalog-connectors.md](p01-data-catalog-connectors.md) |
| **P4** | Modern Lakehouse Connector (Apache Iceberg v2) | [p04-lakehouse-connector.md](p04-lakehouse-connector.md) |
| **P5** | Subscriptions, Realtime Events & In-Stream RLS | [p05-subscriptions-realtime.md](p05-subscriptions-realtime.md) |
| **P7** | Subgraph Federation Router (Hot Chocolate Fusion) | [p07-subgraph-federation.md](p07-subgraph-federation.md) |
| **P8** | WORM Audit Logging & Consent Sealing | [p08-worm-audit-sealing.md](p08-worm-audit-sealing.md) |
| **P9** | Native C# Ingress/Egress Pipeline & Dual-Mode Extensibility | [p09-native-csharp-pipeline.md](p09-native-csharp-pipeline.md) |
| **P10** | Enterprise Governance Mutations & 4-Eyes SoD | [p10-governance-mutations-sod.md](p10-governance-mutations-sod.md) |
