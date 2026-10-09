# Autheris: Endpoint Inventory and Testing Guide

Last Updated: October 2026. All routes are registered via `GatewayApplicationBuilderExtensions.MapGatewayEndpoints`.  
Default Security: FallbackPolicy `RequireAuthenticatedUser`; role and tenant authorization checks are enforced inside endpoint handlers or via `[Authorize]`.  
CSRF Protection: Mandatory for GraphQL; required for REST when browser indicators (`Cookie`, `Origin`, `Referer`) are detected -> requires header `GraphQL-Preflight: 1`, `X-Requested-With`, or `X-CSRF-Token`.

---

## 1. Mapped Endpoint Inventory

| Group | Paths | Auth Requirements | Activation Switch | Primary Test Coverage |
|---|---|---|---|---|
| **GraphQL** | `/graphql`, `/graphql/{domain}` (GET/POST/WebSocket) | Authenticated + CSRF | Always active; Nitro IDE in Dev only | `WalkingSkeleton`, `EndToEndSqlite` |
| **Metrics & Health** | `/metrics`, `/health/live`, `/health/ready` | Metrics: Auth; Health: Anonymous | Always active | `WalkingSkeleton`, `EndToEndSqlite` |
| **Authentication & Session** | `/api/auth/login` (GET/POST), `/session`, `/logout` | Basic / ForwardAuth / Session | Always active | `WalkingSkeleton`, `BasicAuthOptimizationTests` |
| **Developer Hub** | `/`, `/getting-started`, `/api/dev/info`, `/personas`, `/login/{persona}` | Anonymous | `Development` environment only | `InsecureGettingStarted` |
| **System & Monitoring** | `/api/governance/system/{metrics,health,resource-groups}` | Role-gated | `SystemMetrics.Enabled` | Unit tests |
| **Governed WebSQL** | `POST /api/v1/sql`, `/api/sql` | Authenticated (DML requires `WebSql.DmlWriterRoles`) | `WebSql.Enabled` | `GovernedWebSql`, `EndToEndSqlite` |
| **Declarative SQL Endpoints** | `/api/v1/queries/`, `/openapi.json`, `/{name}` (GET/POST) | Authenticated | `SqlEndpoints.Enabled` + `WebSql.Enabled` | `EndToEndSqlite`, Unit tests |
| **Stored Procedures** | `/api/v1/procedures/*` | Authenticated | `SqlEndpoints.Procedures.Enabled` (SQL Server) | Unit tests (`ProcedureYamlGeneratorTests`) |
| **OData v4 & OpenAPI 3.1** | `/odata/v4`, `$metadata`, `$openapi`, `/{domain}/{schema}/{table}`, `/ui/swagger`, `/docs` | Authenticated (or Anonymous if `OpenSchema`) | Always active | `ODataIntegration`, `OpenApiDocsEndToEnd` |
| **Virtual Filters & Access Profiles** | `/api/v1/governance/virtual-filters[/{name}]`, `/access-profiles[/{name}]`, `/sync/{plan,apply}`, `/effective-filters` | `FilterAdmin`, `GovernanceAdmin`, `SecurityAuditor` | Always active | `CrossChannelVirtualFilterParity`, `VirtualFilterAdministration` |
| **Consents & Bulk Grants** | `POST /api/v1/consents/bulk`, GraphQL mutations | `DataOwner`, `GovernanceAdmin` | Always active | `ConsentIntegrationTests`, `AccessProfileTests` |
| **Model Context Protocol (MCP)** | `/mcp` (Streamable HTTP), `/.well-known/oauth-protected-resource/mcp` | Authenticated (OAuth/Entra ID) | `Mcp.Enabled` | `McpIntegration`, `McpDatasetTools` |
| **dbt Ingestion & Workflows** | `/api/extensions/dbt/*` (`sync?mode=replace`, `exposures`, `proposals`, `validate-contract`, `run-results`, `webhook`) | `DbtAdmin`, `GovernanceAdmin`, `ClusterAdmin` | Always active | `DbtIntegration`, `DbtTests` |
| **Governance & Compliance** | `/api/governance/{catalog/ingest-openapi, sunsetting/*, differential-privacy/*, gdpr/export-pdf}`, `/api/lineage/openlineage/sync` | Role-gated (`PrivacyAdmin`, `Auditor`) | Always active | `GovernanceAdvancedMoats` |
| **Schema Registry** | `/api/schema-registry/{publish,check,services,{svc}/latest,{svc}/history}` | Role-gated | Always active | `SchemaRegistryEndpointSecurity` |
| **Webhooks & Ingestion** | `/api/webhooks/{openmetadata,itsm/status-change,servicenow,jira,catalog}`, `/api/v1/governance/catalog/webhook/{provider}` | HMAC Signature | Always active | `Itsm`, `OpenMetadata` |
| **CDC Streaming** | `/api/v1/cdc/{events,subscriptions}` | Role-gated | Always active | `RealtimeStreamingSubscription` |
| **Human-in-the-Loop (HITL)** | `/api/governance/hitl/{pending,approve,reject}` | Approver roles | `HitLStepUp.Enabled` | Unit tests |
| **Token Revocation** | `POST /api/admin/tokens/revoke` | `GovernanceAdmin` | Always active | Unit tests |
| **FOCUS FinOps Accounting** | `/api/v1/finops/{focus,budget/{tenant}}` | `FinOpsAdmin` | `FinOps.Enabled` | `FinOpsIntegration`, `FinOpsEndToEndSqlite` |
| **ReBAC (Zanzibar / OpenFGA)** | `/api/v1/rebac/{tuples,check,batch-check}` | Authenticated | Always active | `ZanzibarRebacTests` |
| **Arrow Flight SQL & Export** | `/api/v1/export/arrow`, `/api/v1/flight/sql/{info,tables,stream}` | Authenticated | Always active | Unit tests |
| **DuckDB In-Memory OLAP** | `POST /api/v1/olap/query` | Authenticated | `DuckDbOlap.Enabled` | Unit tests |
| **Apache Iceberg Lakehouse** | `/v1/{prefix}/namespaces/...` | Authenticated | `Lakehouse:Enabled` | `LakehouseIntegration` |
| **Envoy ext_authz** | `/api/v1/envoy/{authz,check,export/*.yaml}` | Authenticated | Always active | Unit tests |
| **Backstage Integration** | `/api/integrations/backstage/catalog-entities[/{name}]`, `catalog-info.yaml` | Authenticated | `Backstage.Enabled` | `BackstageIntegration` |

Total: **118+ mapped endpoints** across REST, GraphQL, OData, MCP, Arrow Flight SQL, and WebSockets.

---

## 2. Automated Testing Strategy

Autheris maintains a comprehensive test suite of **5,000+ automated tests** verifying every layer of the architecture:

### 2.1 Test Suites Overview

| Project | Type | Count | Focus |
|---|---|---|---|
| [`Autheris.Tests.Unit`](../../tests/Autheris.Tests.Unit) | Unit & Property-Based | **3,609** | Policy evaluation, column masking, ReBAC, AST transforms, audit hashing, access profiles, and cryptographic fail-closed validation. |
| [`TrinoSqlEngine.Tests`](../../tests/TrinoSqlEngine.Tests) | Parser & Compiler | **1,072** | SQL AST generation, dialect pushdown (T-SQL, PostgreSQL, SQLite, DuckDB, Oracle, Snowflake), constant folding, and injection prevention. |
| [`Autheris.Tests.Architecture`](../../tests/Autheris.Tests.Architecture) | Architecture & Layering | **12** | Clean / Onion Architecture constraints, zero-dependency domain, no foreign SDK leaks into core, and isolation rules. |
| [`Autheris.Tests.Integration`](../../tests/Autheris.Tests.Integration) | End-to-End Integration | **239** | HTTP endpoints via `WebApplicationFactory<Program>`, SQLite/PostgreSQL flows, OData, WebSQL, and ForwardAuth. |
| [`Autheris.Extensions.Tests`](../../tests/Autheris.Extensions.Tests) | Foreign Connectors | **117** | Iceberg Lakehouse, Data Catalogs (Purview, Collibra, OpenMetadata), dbt Cloud, and ITSM webhooks. |

### 2.2 Executing Tests Locally

Run the entire solution test suite (no external Docker or database services required):

```bash
# Full test suite execution
dotnet test Autheris.sln -c Release

# Unit tests only
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj

# Architecture validation only
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj

# Target SQL engine tests
dotnet test tests/TrinoSqlEngine.Tests/TrinoSqlEngine.Tests.csproj
```

### 2.3 Integration Test Configuration & Personas
- **Test Framework:** xUnit, Shouldly, NSubstitute, and FsCheck.
- **In-Memory Governance Catalog:** Tests run against isolated, in-memory SQLite instances (`Data Source=gov-{Guid};Mode=Memory;Cache=Shared`) with automatic schema seeding.
- **Identity Impersonation:** In `Development` mode, tests use `TestAuthHandler` via headers:
  - `X-Test-User-Sid: S-1-5-21-..."`
  - `X-Test-Roles: GovernanceAdmin,DataOwner`
  - `X-Test-Tenant: tenant-alpha`
- **Manual Verification:**
  ```bash
  curl -u admin:admin -H "X-Requested-With: x" -H "Content-Type: application/json" \
    -d '{"query":"{__typename}"}' http://localhost:8080/graphql
  ```
