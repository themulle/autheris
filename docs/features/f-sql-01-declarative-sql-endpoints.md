# F-SQL-01: Declarative SQL-to-API Engine & Auto-OpenAPI\n\n**Status:** [Done] (100% GA – Wave 2)  \n**Komponenten:** [`SqlEndpointLoader.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/SqlEndpoints/Services/SqlEndpointLoader.cs), [`InMemorySqlEndpointRegistry.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/SqlEndpoints/Services/InMemorySqlEndpointRegistry.cs), [`SqlEndpointRoutes.cs`](file:///root/lis-git/gql/gql/src/Autheris.Api/Endpoints/SqlEndpointRoutes.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
Unternehmen besitzen viele Zeilen optimierten SQLs in Data Warehouses und Repositories, müssen aber oft proprietäre DSLs nutzen, um REST-Endpunkte zu erstellen.

## 2. Architektur & Umsetzung
- Exponiert versionierte `.sql`-Dateien (`queries/*.sql`) direkt als typisierte REST-APIs (`GET`/`POST /api/v1/queries/{name}`).
- Unterstützt native `@param`-Syntax.
- Automatische Generierung der OpenAPI 3.0 Spezifikation (`/api/v1/queries/openapi.json`).
- Durchsetzung von Casbin-ABAC, RLS und PII-Maskierung.

## 3. Business Value
- Zero-Boilerplate Erstellung sicherer REST-APIs direkt aus versionierten SQL-Dateien.\n