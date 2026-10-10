# F-API-08: Catalog Discovery & Zero-Leakage Swagger 2.0 / OpenAPI Ingestion

## 1. Overview & Business Value

The **Catalog Discovery & Swagger Ingestion API** provides automated, single-step onboarding of external REST APIs, OpenAPI 3.x microservices, and legacy Swagger 2.0 endpoints into the Autheris data catalog without manual database seeding or server restarts.

### Key Capabilities:
- **Swagger 2.0 & OpenAPI 3.x Ingestion:** Ingests specifications via request body or remote URL. Parses paths, operations, parameters, schemas, and `definitions` / `components.schemas`.
- **Zero-Leakage Secret Storage:** API keys, HTTP Basic credentials, and Bearer tokens are stored exclusively inside `IKeyVaultSecretProvider`. Secrets are never stored in plaintext, never written to audit logs, and never returned in API responses.
- **Fail-Safe Inactive State (SEC M-30):** Newly ingested datasources and tables are created with `IsActive = false`. They are completely hidden from non-admin catalog listings and query endpoints until explicitly reviewed and activated.
- **Granular Declarative Grants:** `POST /api/v1/catalog/grants` enables administrators to define column masking levels (`Plaintext`, `Masked`, `Deny`) and row-level SQL filters per user/principal in a single call.
- **Human-to-SID Principal Resolution:** `POST /api/v1/catalog/principals/resolve` resolves plain usernames ("david", "philipp") into canonical enterprise SIDs and Entra ID object identifiers.

## 2. API Endpoints

| Method | Endpoint | Description | Role Required |
|---|---|---|---|
| `GET` | `/api/v1/catalog/tables` | Lists visible tables with domain, active state, and column metadata | Authenticated |
| `GET` | `/api/v1/catalog/tables/{domain}/{table}` | Retrieves schema details and column sensitivity classifications | Authenticated |
| `PATCH` | `/api/v1/catalog/tables/{domain}/{table}/state` | Activates (`IsActive: true`) or suspends a table | `GovernanceAdmin`, `SchemaPublisher` |
| `POST` | `/api/v1/catalog/ingest-swagger` | Ingests OpenAPI 3.x or Swagger 2.0 with credentials | `GovernanceAdmin`, `SchemaPublisher` |
| `POST` | `/api/v1/catalog/grants` | Grants granular column & row permissions per person | `ClusterAdmin`, `GovernanceAdmin` |
| `POST` | `/api/v1/catalog/principals/resolve` | Resolves names to canonical SIDs | Authenticated |

## 3. Implementation Details

- **Service:** `CatalogDiscoveryService` in `src/Autheris.Application/Catalog/Services/CatalogDiscoveryService.cs`.
- **Secrets Provider:** `IKeyVaultSecretProvider` in `src/Autheris.Core/Security/IKeyVaultSecretProvider.cs`.
- **Endpoints:** Mapped in `src/Autheris.Api/Endpoints/CatalogApiEndpoints.cs`.
