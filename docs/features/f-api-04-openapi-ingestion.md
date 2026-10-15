# F-API-04: Upstream Web API & Microservice Ingestion via OpenAPI

**Components:** [`GovernanceEndpoints.cs`](file:///root/autheris/src/Autheris.Api/Endpoints/GovernanceEndpoints.cs), [`IOpenApiIngestionUseCase.cs`](file:///root/autheris/src/Autheris.Application/Governance/IOpenApiIngestionUseCase.cs), [`OpenApiIngestionUseCase.cs`](file:///root/autheris/src/Autheris.Application/Governance/OpenApiIngestionUseCase.cs), [`IOpenApiIngestionService.cs`](file:///root/autheris/src/Autheris.Application/DataCatalog/Interfaces/IOpenApiIngestionService.cs), [`OpenApiIngestionService.cs`](file:///root/autheris/src/Autheris.Application/DataCatalog/Services/OpenApiIngestionService.cs)

---

## 1. Overview & Problem Statement

Enterprises possess hundreds of internal REST microservices described by OpenAPI/Swagger specs that need to be unified into a single data mesh. F-API-04 dynamically ingests upstream OpenAPI 2.0, 3.0, and 3.1 specifications, translating REST paths, request bodies, and JSON schemas directly into typed GraphQL queries and mutations without writing boilerplate bridge code.

---

## 2. Business Value

- **Radical Development Speed**: Ingest third-party and internal REST microservices into the unified enterprise graph in seconds.
- **Zero-Trust REST Governance**: Wraps legacy REST APIs with Active Directory SID-based consent checks, dynamic RLS, and column-level PII masking.
- **Elimination of Hand-Crafted BFFs**: Replaces brittle backend-for-frontend translation layers with automated schema generation.

---

## 3. Architecture & Capabilities

- Real-time parsing of OpenAPI JSON/YAML definitions from URLs or local files.
- Automatic translation of path and query parameters into GraphQL field arguments.
- SSRF-protected HTTP execution with connection pooling and circuit breaking.

### 3.1 Ingestion Use Case

`POST /api/governance/catalog/ingest-openapi` is a thin endpoint over `IOpenApiIngestionUseCase` (Application layer), which owns authorization, ingestion and audit:

1. **Role check first:** the caller needs `GovernanceAdmin` or `SchemaPublisher`; otherwise `403` before any tenant resolution or body read.
2. **Domain check:** the optional query parameter `domain` must equal the caller's tenant unless the caller is a canonical `ClusterAdmin`. Without `domain` the caller's tenant is used (`external` for a ClusterAdmin). A violation answers `403` before the body is read.
3. **Bounded body read** (endpoint): the request body is the OpenAPI document itself, at most 20 MB.
4. **Ingestion:** `IOpenApiIngestionService` parses and registers the specification; `baseUrl` (query parameter) overrides the upstream base URL. A rejected specification answers `400` with the ingestion result.
5. **Audit:** a successful ingestion is recorded as `OPENAPI_CATALOG_INGESTED` through `IAuditWriter.RecordRequiredAsync`; an unavailable audit store fails the request with `503`.

---

## 4. Usage Example

```bash
# Register an upstream microservice OpenAPI spec (the body is the OpenAPI JSON document)
curl -X POST "http://localhost:8080/api/governance/catalog/ingest-openapi?domain=logistics&baseUrl=https://shipping.internal.corp/api/v1" \
  -H "Authorization: Bearer <admin-token>" \
  -H "Content-Type: application/json" \
  --data-binary @shipping-openapi.json

# Response (200):
# { "success": true, "serviceTitle": "Shipping Service", "ingestedTablesCount": 3, "ingestedColumnsCount": 27,
#   "ingestedTableNames": [ ... ], "warnings": [] }
```

---

## 5. Configuration

The ingestion endpoint has no dedicated options section. Calls to ingested upstream services are subject to the gateway egress policy (`Gateway:Egress`).
