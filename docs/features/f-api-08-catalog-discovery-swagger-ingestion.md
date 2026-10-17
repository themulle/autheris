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

### 2.1 Catalog Read Endpoints and Visibility

| Method | Endpoint | Description | Access |
|---|---|---|---|
| `GET` | `/api/v1/catalog/datasets` | Lists the datasets the caller may discover | Authenticated, consent-based |
| `GET` | `/api/v1/catalog/datasets/{domain.schema.table}` | Dataset detail (only the columns the caller's consents allow) | Authenticated, consent-based; `404` for an unknown or not visible dataset (identical answer) |
| `GET` | `/api/v1/catalog/search` | Hybrid search over the visible datasets | Authenticated, consent-based |
| `GET` | `/api/v1/catalog/datasources` | Data sources with endpoint base URL and authentication state | `GovernanceAdmin` or `ClusterAdmin` only; `GovernanceAdmin` of a tenant sees only the data sources of the tables of its tenant |

The visible set is computed by the shared catalog visibility rule: a dataset is visible with an active Allow consent of the caller (columns per consent, Deny rules and masking `DENY` hide columns); canonical `ClusterAdmin` sees everything, a tenant-scoped `GovernanceAdmin` its tenant (see `Gateway:Tenancy:Mode` in the configuration guide). ReBAC, when enabled, is an additional filter (intersection). MCP resources (`autheris://catalog/*`), the MCP tools `search_catalog`, `get_my_permissions`, `list_datasources` and the `invoke_api` catalog routes use the same service and the same caller class.

**Search result hygiene.** The search index is built over the whole catalog, so results are rebuilt from the caller's filtered metadata: `RelevantColumns` and `MatchedTerms` contain only the identifier, description and columns visible to the caller, `RelatedJoinPaths` only join paths between visible tables over visible columns, and a hit that exists only because of a column the caller cannot see is dropped. Semantic-only recall is therefore reduced for column-restricted callers.

**Fail closed.** The service requires the consent repository and the gateway options at construction; a failing dependency at call time yields an empty list, `404` or an empty search, never the unfiltered catalog.

## 3. Implementation Details

- **Service:** `CatalogDiscoveryService` in `src/Autheris.Application/Catalog/Services/CatalogDiscoveryService.cs`.
- **Secrets Provider:** `IKeyVaultSecretProvider` in `src/Autheris.Core/Security/IKeyVaultSecretProvider.cs`.
- **Endpoints:** Mapped in `src/Autheris.Api/Endpoints/CatalogApiEndpoints.cs`.

## 4. Remote Spec Download Limits

Specs fetched via `SpecUrl` are downloaded with a hard size cap (`Gateway:Catalog:MaxSpecDownloadBytes`, default 10 MiB). A declared `Content-Length` above the cap is rejected before reading, and the body is additionally capped while streaming so chunked responses without `Content-Length` cannot bypass the limit. Redirects are never followed (`SecureOutboundHttp` uses `AllowAutoRedirect = false`). Overflow is rejected with a generic error message.
