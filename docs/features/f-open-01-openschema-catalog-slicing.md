# F-OPEN-01: OpenSchema Mode, Multi-File OpenAPI & Catalog Slicing

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`CatalogSlicingService.cs`](file:///root/autheris/src/Autheris.Application/Catalog/CatalogSlicingService.cs), [`OpenSchemaMiddleware.cs`](file:///root/autheris/src/Autheris.Api/Middleware/OpenSchemaMiddleware.cs)

---

## 1. Overview & Problem Statement

In massive enterprise environments, exposing a monolithic 500-table schema creates huge API payloads and confuses external integration partners. F-OPEN-01 delivers two complementary features: (1) OpenSchema mode for flexible development onboarding, and (2) Catalog Slicing, which allows generating targeted, role-based multi-file OpenAPI and GraphQL schema slices filtered by domain, project, or consumer team.

---

## 2. Business Value

- **Tailored Developer Experience**: Partner teams receive lean, clean schema specifications containing only the endpoints and tables relevant to their domain.
- **Enhanced Security**: Hides irrelevant internal schemas from external contractors and partner organizations.
- **Faster Client Code Generation**: Small, sliced OpenAPI specs compile significantly faster into client SDKs without exceeding tool limits.

---

## 3. Architecture & Capabilities

- URL-based and header-based schema slicing: `/api/v1/openapi/{domain}.json` or `X-Schema-Slice: finance`.
- Role-based projection: automatically trims unpermitted fields from exported OpenAPI and CSDL schemas.
- Hot-reloading of slices when underlying governance catalog permissions change.

---

## 4. Usage Example

```bash
# Download a sliced OpenAPI 3.0 specification for the 'billing' domain only
curl -X GET http://localhost:8080/api/v1/queries/openapi.json?domain=billing \
  -H "Authorization: Bearer <user-token>" \
  -o billing-api.json

# Fetch GraphQL schema sliced by domain tag
curl -X GET "http://localhost:8080/graphql?slice=ecommerce" \
  -H "Authorization: Bearer <user-token>"
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "CatalogSlicing": {
      "Enabled": true,
      "AllowDomainSlicing": true,
      "AllowTagSlicing": true,
      "DefaultSlice": "core"
    }
  }
}
```
