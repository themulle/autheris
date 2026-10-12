# F-DATA-06: Governed REST Data API & System Virtual Tables

## 1. Overview & Business Value

The **Governed REST Data API** (`GET /api/v1/data/{domain}/{table}`) provides a lightweight, performant, and fully governed RESTful query interface over heterogeneous enterprise datasets. It bridges the gap between complex GraphQL queries, analytical WebSQL commands, and citizen developer / REST-centric enterprise applications.

Crucially, all REST requests pass through the exact same Zero-Trust Policy Decision Point (PDP) as GraphQL and WebSQL:
- **Zero-Trust Consent Enforcement:** Access is validated against data-owner consents and ReBAC (Zanzibar) policies.
- **Pre-Storage Dynamic Data Masking:** Column masking (HMAC pseudonymization, partial masking, nullification) is applied natively before serializing results.
- **Row-Level Security (RLS) Pushdown:** SQL predicates are pushed directly to the underlying SQL engines or evaluated in-memory for HTTP/REST sources.
- **Protocol Symmetry:** System virtual tables (`governance.system.consents`, `governance.system.audit`, `governance.system.costs`) are accessible identically across REST Data API, WebSQL, GraphQL, and OData.

## 2. API Specifications

### 2.1 Table Query Endpoint

```http
GET /api/v1/data/{domain}/{table}?$top=50&$skip=0&$select=id,name,status&$filter=status eq 'ACTIVE'
Host: autheris.company.internal
Authorization: Bearer <jwt-or-api-key>
Accept: application/json
```

#### Query Parameters:
- `$top` (int): Number of rows to return (bounded by `MaxTransportRowLimit`).
- `$skip` (int): Offset for pagination.
- `$select` (string): Comma-separated list of projected columns.
- `$filter` (string): OData-compliant filter expression translated to AST predicates.
- `Accept` Header: Supports `application/json`, `application/vnd.apache.parquet`, or `application/vnd.apache.arrow.stream`.

### 2.2 System Virtual Tables

Virtual system tables are mapped under `domain = "governance"`, providing metadata introspection:
- `governance.system.consents`: Active, expired, and revoked consent grants and delegations.
- `governance.system.audit`: Cryptographically chained HMAC-SHA256 audit entries.
- `governance.system.costs`: FinOps compute, storage, and token accounting metrics.

## 3. Architecture & Implementation

- **Service:** `GovernedDataQueryService` in `src/Autheris.Application/Data/Services/GovernedDataQueryService.cs`
- **Hosted Registry:** `VirtualSystemTablesHostedService` registers system tables into `ITableMetadataRepository` during application startup.
- **Endpoints:** Registered in `src/Autheris.Api/Endpoints/DataApiEndpoints.cs`.
