# F-API-04: Upstream Web API & Microservice Ingestion via OpenAPI

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`IOpenApiIngestionService.cs`](file:///root/autheris/src/Autheris.Application/DataCatalog/Interfaces/IOpenApiIngestionService.cs), [`OpenApiIngestionService.cs`](file:///root/autheris/src/Autheris.Application/DataCatalog/Services/OpenApiIngestionService.cs)

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

---

## 4. Usage Example

```bash
# Register an upstream microservice OpenAPI spec via admin endpoint
curl -X POST http://localhost:8080/api/v1/schemas/ingest-openapi \
  -H "Authorization: Bearer <admin-token>" \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "logistics",
    "serviceName": "shipping_service",
    "openApiSpecUrl": "https://shipping.internal.corp/swagger/v1/swagger.json",
    "baseUrl": "https://shipping.internal.corp/api/v1"
  }'

# Query the newly exposed GraphQL type generated from the OpenAPI spec
# query { shipping_getShipmentById(id: "SH-1024") { trackingNumber carrier status } }
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "OpenApiIngestion": {
      "Enabled": true,
      "AutoSyncIntervalMinutes": 60,
      "DefaultTimeoutSeconds": 30,
      "FollowRedirects": false,
      "ValidateCertificates": true
    }
  }
}
```
