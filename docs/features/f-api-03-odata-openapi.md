# F-API-03: Dual-Access Exposure (OData v4 & Dynamic OpenAPI 3.1)\n\n**Status:** [Done] (100% GA – Wave 1)  \n**Komponenten:** [`ODataHandler.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/OData/ODataHandler.cs), [`DynamicOpenApiGenerator.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/OData/Services/DynamicOpenApiGenerator.cs), [`OpenApiCacheManager.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/OData/Services/OpenApiCacheManager.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
Reine GraphQL-Gateways scheitern an Data-Science-Tools (Pandas/Python), BI-Tools (Power BI/Excel) und B2B-Partnern, die standardisiertes REST oder OData fordern.

## 2. Architektur & Umsetzung
- Vollwertiger OData v4 HTTP-Endpunkt (`/odata/v4`, `/$metadata`).
- Dynamische Generierung von OpenAPI 3.1 Spezifikationen (`/odata/v4/$openapi`, `/odata/v4/{domain}/openapi.json|yaml`).
- Integriertes Swagger UI (`/docs`, `/odata/v4/$swagger`).
- Identische Zero-Trust Governance (Casbin ABAC, RLS, Masking) wie im GraphQL-Pfad.

## 3. Business Value
- Einheitliche Governance über GraphQL, REST und OData.
- Erschließung von BI- und Data-Science-Konsumenten ohne zusätzliche Gateways.\n