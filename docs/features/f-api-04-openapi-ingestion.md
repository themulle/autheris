# F-API-04: Upstream Web API & Microservice Ingestion via OpenAPI\n\n**Status:** [Done] (100% GA – Wave 1)  \n**Komponenten:** [`OpenApiIngestionService.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/DataCatalog/Services/OpenApiIngestionService.cs), `POST /api/governance/catalog/ingest-openapi`\n\n---\n\n## 1. Übersicht & Problemstellung
Microservices exponieren OpenAPI/Swagger-Definitionen, die manuell in GraphQL-Schemas übersetzt werden müssten.

## 2. Architektur & Umsetzung
- Liest OpenAPI 3.0/3.1 Dokumente ein.
- Extrahiert Entitäten, Typen, Pfade und Spaltenkommentare.
- Registriert Microservices automatisch als verwaltete Tabellen/Endpoints unter der Gateway-Governance.

## 3. Business Value
- Schnelle Föderierung bestehender REST-Microservices in das zentrale Datenmodell.\n