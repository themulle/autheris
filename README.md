# Autheris

Autheris is a policy-enforcement data gateway that authorizes, enforces, and provably documents access to enterprise data across heterogeneous databases and APIs.

Built on .NET 10, Autheris sits between data consumers (applications, BI analysts, and AI agents) and data sources:

1. **Identity**: Callers authenticate directly at Autheris (Microsoft Entra ID / OIDC, Kerberos / Negotiate, Basic, RFC 6238 TOTP) or through an upstream reverse proxy via verified `ForwardAuth` headers. Group memberships combine token claims with on-premises Active Directory groups provisioned via SCIM 2.0.
2. **Authorization on data**: Users and AD groups access tables, views, and stored procedures in Oracle, Databricks, Microsoft SQL Server, PostgreSQL, declarative HTTP/OpenAPI sources, and DuckDB OLAP through a single access decision point (`IAccessDecisionPoint`). Row-level security, PII column masking, column-level visibility, and explicit write grants are enforced identically across GraphQL, REST, OData, Trino `/v1/statement`, and MCP.
3. **Discovery**: Every authenticated user and AI agent can browse catalog metadata, data owners, column descriptions, sensitivity classifications, and externally uploaded pseudonymized samples, statistics, and documentation without exposing raw row data.
4. **Approval workflow**: Configurable per tenant (and optionally per data owner or data source) to run either the built-in workflow engine (data-owner approval, four-eyes segregation of duties, self-approval guards) or an external workflow triggered via signed webhook and completed through the Consent API.
5. **Cryptographic proof and time-limited grants**: Every consent or access-profile approval materializes time-limited grant records (`valid_from` to `valid_until`) verified on every request, revocable early, and recorded in an HMAC-SHA256 hash-chained audit trail with asymmetric KMS/HSM checkpoints.
6. **Usage and per-source cost models**: Queries, row counts, outcomes, and cost units are attributed to the caller's user SID and AD groups using configurable, versioned cost models per data source and optional quotas.
7. **External metadata and least-privilege source access**: Schemas, primary keys, indexes, samples, and statistics are supplied externally through the Catalog API; Autheris holds only data-access privileges on target sources and never queries database system catalogs.

## Core capabilities

- **Unified grant evaluation**: Time-limited allow and deny grant records (from individual consents or approved access-profile templates) evaluated by one fail-closed decision point (`IAccessDecisionPoint`) with an explain API.
- **AST-compiled SQL enforcement**: `TrinoSqlEngine` parses SQL into a typed AST, injects tenant, row-filter, and column-masking expressions, verifies security coverage (`SecurityCoverageVerifier`), and emits parameterized SQL for PostgreSQL, SQL Server, Oracle, and Databricks.
- **Cross-source federation and DuckDB OLAP**: Joins across heterogeneous databases and declarative HTTP sources staged and executed in-memory via DuckDB.
- **Row-level security and virtual filters**: Relation-scoped mandatory row filters pushed down into target SQL `WHERE` clauses or semi-joins on HTTP APIs.
- **Extended PII column masking**: Format-preserving redaction, HMAC-SHA256 pseudonymization, partial masking, tokenization, geographic jitter, and inference-blocking filter guards.
- **Governed write access**: Explicit `INSERT`, `UPDATE`, and `DELETE` grants scoped by column set and row predicate, independent of read grants.
- **Governed aggregations**: Small-group suppression (minimum group size) and time bucketing to prevent re-identification in aggregate queries.
- **Governed stored procedures**: Declarative contract execution inheriting source column masking and row-scope filtering.
- **Tamper-evident audit and GDPR Art. 15 reporting**: Hash-chained audit log entries, signed retention purge checkpoints, and 365-day GDPR Art. 15 disclosure reports.
- **Authorized discovery and document search**: Hybrid semantic and vector search over catalog schemas and linked markdown documentation for users and MCP agents.
- **Customer DLL plugin narrowing hooks**: SHA-256 allowlisted plugins for custom HTTP data sources and narrowing-only authorization hooks.
- **Governance persistence**: Forward-only schema migrations and cluster locking on PostgreSQL (default) and Microsoft SQL Server.

See the [Feature Catalog](docs/features/README.md) and [arc42 Architecture Overview](docs/architecture/arc42.md) for detailed component documentation.

## Transports

All five core data transports share the same authentication, consent, row-filter, masking, usage-accounting, and audit pipeline:

| Transport | Endpoints | Primary Clients |
| :--- | :--- | :--- |
| **GraphQL** | `POST /graphql` | Web applications, BFFs, federated subgraphs (`@defer` / `@stream`, single-query SQL pushdown) |
| **REST** | `POST /api/v1/sql`, `GET/POST /api/v1/queries/{name}`, `GET /api/v1/data/{domain}/{table}`, `POST /api/v1/export/arrow` | Services, Web SQL clients, async query jobs, Apache Parquet and Arrow IPC export |
| **OData** | `GET /odata/v4/{domain}/{schema}/{table}` | Microsoft Power BI, Microsoft Excel, Tableau (OData v4 with CSDL `$metadata`) |
| **Trino client protocol** | `POST /v1/statement` | Trino CLI and standard SQL/BI clients speaking the Trino HTTP statement protocol |
| **MCP** | `POST /mcp` (Streamable HTTP) and stdio runner | AI agents (dataset discovery, governed SQL/GraphQL queries, documentation tools, HITL step-up) |

Additionally, Autheris exposes an **Envoy `ext_authz` decision endpoint** (`/api/v1/ext-authz/*`) that evaluates upstream service-mesh requests through the core access decision point. `Arrow Flight` / `Flight SQL` over HTTP and PostgreSQL wire protocol (`PG wire`) are not offered; use `/v1/statement` or REST Arrow IPC export (`/api/v1/export/arrow`).

## Modules

Optional capabilities outside the core enforcement pipeline are isolated into modules enabled via `Gateway:Modules:Enabled` or deployed with the `autheris-full` container image:

| Module | Purpose | Enablement |
| :--- | :--- | :--- |
| **Lakehouse** | Delta Lake and Apache Iceberg v2 table reads with partition and manifest pruning | `Gateway:Modules:Enabled = ["Lakehouse"]` / `autheris-full` |
| **dbt** | Streaming `manifest.json` / `catalog.json` ingestion, contract gates, and health circuit breakers | `Gateway:Modules:Enabled = ["Dbt"]` / `autheris-full` |
| **OpenMetadata** | Catalog metadata and sensitivity classification synchronization from OpenMetadata | `Gateway:Modules:Enabled = ["OpenMetadata"]` / `autheris-full` |
| **CDC & Event Webhooks** | SQL Server Change Tracking, PostgreSQL logical replication, and outbound event webhooks (consent workflow webhooks stay in core) | `Gateway:Modules:Enabled = ["Cdc"]` / `autheris-full` |
| **Lineage** | Downstream lineage impact analysis and OpenLineage export (GDPR Art. 15 report stays in core) | `Gateway:Modules:Enabled = ["Lineage"]` / `autheris-full` |

See the [Simplification Migration Guide](docs/operations/migration-simplification.md) for removed integrations and configuration keys.

## Quick start

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Docker or Podman (for PostgreSQL governance store and compose stack)

### Build and test from source

```bash
dotnet build Autheris.sln -c Release -warnaserror
dotnet test Autheris.sln -c Release -p:AutherisIncludeTestAuth=true
```

### Run with Docker Compose

The compose stack starts PostgreSQL for the governance store alongside the gateway:

```bash
# Production-mode base stack with local development override (loopback only)
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d
```

Key local endpoints on `http://127.0.0.1:8080`:

- GraphQL: `http://127.0.0.1:8080/graphql`
- OpenAPI / Swagger UI: `http://127.0.0.1:8080/docs`
- Governed Web SQL: `POST http://127.0.0.1:8080/api/v1/sql`
- OData v4 service document: `http://127.0.0.1:8080/odata/v4`
- Trino statement protocol: `POST http://127.0.0.1:8080/v1/statement`
- MCP endpoint: `POST http://127.0.0.1:8080/mcp`
- Health probes: `http://127.0.0.1:8080/health/live` and `http://127.0.0.1:8080/health/ready`

## Authentication

Autheris authenticates every request before reaching the access decision point:

- **OIDC / JWT Bearer**: Microsoft Entra ID and AD FS tokens normalized into canonical user and group SIDs.
- **Kerberos / Negotiate**: Windows Integrated Authentication for domain-joined clients and containers.
- **HTTP Basic & TOTP**: Salted PBKDF2 Basic credentials (`/api/auth/login`) and RFC 6238 TOTP step-up verification (`/api/v1/auth/2fa/*`).
- **Ingress `ForwardAuth`**: Trusted reverse-proxy identity headers (`X-Forwarded-User`, `X-Forwarded-Groups`) verified by CIDR allowlist and shared secret (`X-Forwarded-Secret`).
- **SCIM 2.0 Provisioning**: On-premises Active Directory users, groups, and memberships synchronized via `/scim/v2/*` and combined with token groups.

Full configuration details are in the [Configuration Guide](docs/configuration-guide.md) and the generated [Configuration Reference](docs/reference/configuration.md).

## Project status

Autheris is under active development and is **not yet production-hardened**.

- **Upgrade base**: Forward-only governance schema migrations start from the frozen `v1.5.0` baseline; earlier versions are unsupported.
- **Deprecation policy**: While the project has no declared production release, breaking simplifications and removals take effect immediately without compatibility shims and are documented in [CHANGELOG.md](CHANGELOG.md) and the [Simplification Migration Guide](docs/operations/migration-simplification.md). After the first declared production release, any capability or configuration key will be deprecated for at least two minor releases with a runtime startup warning and a `CHANGELOG.md` notice before removal.

## Benchmarks

Performance and footprint figures are tracked exclusively through reproducible scripts and checked-in artifacts:

- **Measurement protocol and budgets**: See [Benchmark Protocol](benchmarks/protocol/README.md) and `benchmarks/budgets.json`.
- **Warm latency and ReadyToRun baseline**: Non-R2R warm P50 of 1.17 ms and P99 of 3.47 ms vs. R2R warm P50 of 2.97 ms and P99 of 10.24 ms are recorded in the [R2R Spike Report](benchmarks/results/r2r-spike-report.md).
- **Footprint and repository metrics**: Checked-in measurements reside in [benchmarks/results/footprint-2026-11-05-7900268.json](benchmarks/results/footprint-2026-11-05-7900268.json) and [benchmarks/results/repo-metrics-2026-11-05-7900268.json](benchmarks/results/repo-metrics-2026-11-05-7900268.json).

Run the microbenchmark suite locally with:

```bash
dotnet run --project benchmarks/Autheris.Benchmarks/Autheris.Benchmarks.csproj -c Release
```

## Contributing

See the [Developer Guide](docs/developer-guide.md), [Operations Runbook](docs/operations-runbook.md), and [Threat Model](docs/threat-model/threat-model.md). All code, tests, and documentation must be written in English, compile with zero warnings (`-warnaserror`), and pass the architecture and security test suites.

## License

Autheris is licensed under the [Business Source License 1.1 (BSL 1.1)](LICENSE), converting automatically to the Apache License, Version 2.0 on 2029-10-01. See [LICENSE](LICENSE) for full terms.
