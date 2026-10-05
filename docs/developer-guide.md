# Developer Guide - GraphQL Enterprise Gateway

This guide assists engineers in contributing to the GraphQL Enterprise Gateway, writing tests, and running the project locally with zero external dependencies.

---

## 1. Prerequisites & Environment

- **.NET SDK**: 10.0 or higher (`net10.0`)
- **IDE**: Visual Studio 2026, VS Code with C# Dev Kit, or JetBrains Rider
- **No External Services Required**: The gateway includes an in-memory SQLite governance catalog and test authentication simulation, allowing full local execution without Docker, SQL Server, or Redis.

---

## 2. Quick Start (Local Run)

### 2.1 Build the Solution
```bash
dotnet build Autheris.sln
```
The build enforces strict typing and treat-warnings-as-errors (`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`).

### 2.2 Run the WebHost
```bash
dotnet run --project src/Autheris.Api/Autheris.Api.csproj
```
The server will start on `http://localhost:5000`. Hot Chocolate Nitro Banana Cake Pop IDE is accessible at:
- `http://localhost:5000/graphql`

---

## 3. Simulating Authentication in Development

The gateway supports multiple authentication schemes in development:

### 3.1 TestAuthHandler (Header-based Simulation)
When running in `Development` mode, the `TestAuthHandler` is enabled. You can impersonate any Windows User or Group SID by passing custom HTTP headers:
- `X-Test-User-Sid`: The calling user's SID (e.g. `S-1-5-21-1001`)
- `X-Test-Group-Sids`: Comma-separated group SIDs (e.g. `S-1-5-21-FINANCE-ANALYSTS,S-1-5-21-ALL-STAFF`)
- `X-Test-Roles`: Comma-separated application roles (e.g. `DataConsumer,DataOwner,GovernanceAdmin`)

```bash
curl -X POST http://localhost:5000/graphql \
  -H "Content-Type: application/json" \
  -H "GraphQL-Preflight: 1" \
  -H "X-Test-User-Sid: S-1-5-21-1001" \
  -H "X-Test-Group-Sids: S-1-5-21-FINANCE-ANALYSTS" \
  -d '{"query": "{ catalog { domain schemaName tableName displayName } }"}'
```

### 3.2 Traefik ForwardAuth (Kubernetes Ingress Simulation)
Simulate requests originating from a Traefik Ingress controller:
```bash
curl -X POST http://localhost:5000/graphql \
  -H "Content-Type: application/json" \
  -H "GraphQL-Preflight: 1" \
  -H "X-Forwarded-User: S-1-5-21-1001" \
  -H "X-Forwarded-Groups: S-1-5-21-FINANCE-ANALYSTS" \
  -d '{"query": "{ catalog { domain schemaName tableName } }"}'
```

### 3.3 HTTP Basic Authentication & `/api/auth/login`
Test credential validation and direct Basic Auth GraphQL queries:
```bash
# Verify credentials
curl -u "analyst:Secret123!" http://localhost:5000/api/auth/login

# Direct GraphQL query
curl -X POST http://localhost:5000/graphql \
  -u "analyst:Secret123!" \
  -H "Content-Type: application/json" \
  -H "GraphQL-Preflight: 1" \
  -d '{"query": "{ catalog { domain schemaName tableName } }"}'
```

---

## 4. Architecture Guidelines & Quality Gates

The codebase follows **Clean Architecture**:
- `Autheris.Domain`: Pure business rules, entities, and domain calculation services. Zero dependencies on external libraries or frameworks.
- `Autheris.Application`: Core execution engine (`GatewayExecutionService`, `IGatewayExecutionService`), business services (`ConsentResolutionService`, `ColumnMaskingProvider`, `RlsFilterGenerator`, `ChunkedQueryExecutor`), data source executors (`SqlDataSourceExecutor`), and repository contracts.
- `Autheris.Infrastructure`: ADO.NET SQL persistence (`SqlConnectionFactory`, `SqliteGovernanceRepository`), Redis multi-instance messaging (`RedisEventBus`), rate limiters, idempotency stores, and authentication handlers (`ForwardAuthAuthenticationHandler`, `BasicAuthenticationHandler`, `EnterpriseClaimsTransformation`).
- `Autheris.GraphQL`: Hot Chocolate schema configuration, dynamic types, queries, and mutations.
- `Autheris.Api`: ASP.NET Core host, Basic Auth login endpoint, rate limiting middlewares, health check probes, and graceful drain hosted service.

### 4.1 Running Automated Tests
```bash
# Run all tests (Unit, Integration, Architecture)
dotnet test Autheris.sln

# Run only Architecture boundary tests
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj

# Run Unit & Property-Based tests
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj

# Run Walking Skeleton Integration tests
dotnet test tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj
```

---

## 5. Adding New Queries and Dynamic Tables

1. **Register Table in Governance Catalog**: Add entry in `TABLES` table with `CATALOG_NAME`, `SCHEMA_NAME`, and `TABLE_NAME`.
2. **Define Columns and Types**: Add column specifications in `TABLE_COLUMNS`.
3. **Configure Hot Chocolate Dynamic Type**: Handled automatically by `DynamicTableType` which inspects catalog metadata, applies scalar conversions, and hooks field masking.
4. **Grant Consent**: Ensure the calling SID has an active `ALLOW` consent for the table before querying, otherwise Zero Trust will return `FORBIDDEN`.

---

## 6. Testing Data Catalog Synchronization

You can test Data Catalog synchronization against Microsoft Purview, Collibra, Alation, or OpenMetadata locally:

```graphql
# Administrative Mutation (requires GovernanceAdmin or ClusterAdmin)
mutation RunCatalogSync {
  syncDataCatalog(dryRun: true) {
    success
    syncedTablesCount
    syncedColumnsCount
    maskedColumnsCount
    art9ProtectedTablesCount
    warnings
  }
}
```

In unit tests, mock `IDataCatalogClient` and assert that `DataCatalogSyncService` properly maps tags:
- `GdprArticle9Tags` -> Enforces `Table.Sensitivity = "HIGH"`, `RequiresFourEyes = true`, and `MaskingRule = REDACT`.
- `TagToMaskingRuleMap` -> Maps tags (e.g. `PII.Email`) to `MASK_EMAIL` or `HMAC_SHA256`.

---

## 7. Testing Lineage & GDPR Disclosure Queries

To analyze downstream dependencies before schema changes or produce GDPR Art. 15 reports:

```graphql
# 1. Downstream Lineage Impact
query CheckConsumers {
  tableConsumers(domain: "sales", schema: "dbo", tableName: "orders", timeWindowDays: 30) {
    breakingChangeRisk
    activeReadersCount
    downstreamConsumers {
      name
      type
      ownerTeam
      ownerEmail
    }
    recommendedMitigations
  }
}

# 2. GDPR Art. 15 Disclosure Report
query GetGdprDisclosure {
  gdprDataDisclosureReport(
    domain: "healthcare"
    schema: "dbo"
    tableName: "patient_diagnoses"
    timeWindowDays: 365
  ) {
    totalAccessEvents
    sensitivityCategories
    disclosedRecipients {
      recipientSid
      recipientCategory
      totalQueries
      accessedColumns
      maskingRuleApplied
    }
  }
}
```

---

## 7a. Developer Mode: Personas, Banner and Persistence

In `Development` the gateway prints a banner on startup with the useful links and the configured personas (see [F-AUTH-DX](features/f-auth-dx-basic-auth-session.md)).

```bash
# Run with hot reload
dotnet watch --project src/Autheris.Api run --launch-profile https

# One-click login as a persona (sets the session cookie, then redirects to GraphQL)
open "https://localhost:7214/api/dev/login/owner?redirect=/graphql"

# What is relaxed in this instance? (preset, dev features, bypasses; no secrets)
curl -s https://localhost:7214/api/dev/info | jq .dev
```

- **Switches:** all development-only switches live in `Gateway:Dev` (preset `Standard`, `Quickstart` or `Strict`); see [configuration guide, section 2.14a](configuration-guide.md).
- **Persistent database:** `Gateway__Dev__Persist__Enabled=true` keeps approvals and audit data in `.data/dev.db` across restarts. Reset with `scripts/dev-reset.sh` (stop the gateway first; it removes the audit anchor together with the database).
- **Verbose errors:** a 403 from a role policy names the required and the actual roles; unhandled exceptions return `problem+json` with a `traceId`.
- **Integration tests under WSL:** if many tests fail with an inotify limit error, run them with `DOTNET_USE_POLLING_FILE_WATCHER=true`.

---

## 8. Rapid Prototyping with Insecure Modes

During early development or when onboarding complex third-party webhooks (e.g., ServiceNow/Jira local tunnels), you can temporarily loosen security checks via `appsettings.Development.json`:

```json
"Insecure": {
  "warn_allow_all_cors_origins": true,
  "warn_disable_rate_limiting": true,
  "danger_allow_untrusted_certificates": true,
  "danger_bypass_webhook_signature_validation": true
}
```

> [!WARNING]
> Never commit `danger_* = true` in production configuration files (`appsettings.Production.json`). The gateway emits bold warnings when any insecure flag is engaged.

---

## 9. Validating Casbin Governance Policies

Autheris includes a dedicated CLI linter tool for validating Casbin RBAC/ABAC models and policies:

```bash
# Run the Casbin policy linter
dotnet run --project tools/casbin-policy-lint/casbin-policy-lint.csproj
```

---

## 10. Testing Vector Databases, RAG Egress & Semantic Cache (`F-AI-09` / `F-AI-10`)

Developers can verify RAG search, chunk PII redaction, and semantic caching locally:

```bash
# Run dedicated security & vector integration tests
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj --filter "FullyQualifiedName~Vector|FullyQualifiedName~Semantic"
```

To invoke RAG search through the Model Context Protocol (MCP) tool:
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "search_rag_context",
    "arguments": {
      "collection": "public.documents",
      "query_vector": [0.12, 0.45, -0.22, 0.89],
      "top_k": 5
    }
  }
}
```

---

## 11. Testing In-Memory OLAP & Arrow Export (`F-DATA-03` / `F-DATA-04`)

Test fast vector analytical queries and Arrow Flight/IPC endpoints:

```bash
# 1. In-Memory DuckDB OLAP Query
curl -X POST http://localhost:5000/api/v1/olap/query \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <token>" \
  -d '{
    "sql": "SELECT region, count(*), sum(amount) FROM sales_records GROUP BY region",
    "tableNames": ["default.public.sales_records"]
  }'

# 2. Apache Arrow IPC Stream Export
curl -X GET "http://localhost:5000/api/v1/arrow/export/default.public.sales_records?batchSize=10000" \
  -H "Authorization: Bearer <token>" \
  --output sales_records.arrow
```


