# F-GOV-16: Catalog Discovery Transparency, Data Ownership & Response Masking Metadata

## 1. Executive Summary & Problem Statement

Enterprise data architectures require transparent, federated data discovery without compromising confidentiality or creating existence oracles. Autonomous agents and data analysts need to discover datasets, know who owns them, understand their current access posture, and inspect response masking metadata:

1. **Tag-Driven Discovery Gaps:** Modern data estates host thousands of datasets across disparate domains. Without categorization tags (e.g. `finance`, `pii`, `core-banking`, `esg`), clients and AI agents struggle to discover relevant data products and cannot filter tools efficiently.
2. **Missing Ownership Attribution:** Decentralized data governance (Data Mesh) requires clear data ownership. Consumers discovering an ungranted dataset must know the designated data owner or delegate to request approval. Moreover, developer portals such as Spotify Backstage require entity ownership (`spec.owner`) synchronized directly with the enterprise governance authority.
3. **Binary Discovery vs. Access Status:** In traditional systems, catalog discovery is either binary-closed (users cannot see what they do not have access to, preventing access requests) or binary-open (users see sensitive metadata for all tables). A graduated access status model (`GRANTED`, `PENDING`, `NONE`) is intended to be combined with dataset visibility tiers (`Hidden`, `Summary`, `Full`) to allow controlled discovery while eliminating existence oracles for confidential assets. The tiers are part of the catalog model but are not persisted in the governance database, see the limitation in section 2.
4. **Opaque Masked Responses in GraphQL:** When column-level masking (e.g., hash, redaction, partial email masking) is applied, consumers cannot distinguish between genuine database `null`/blank values and intentionally masked sensitive fields. Standardized response extensions (`extensions.maskedFields`) provide machine-readable transparency without leaking raw values or salt keys.

`F-GOV-16` resolves these challenges by introducing tag-based catalog discovery, automated data owner resolution across MCP, GraphQL, and Spotify Backstage, graduated access status calculation, and transparent GraphQL response masking metadata in .NET 10.

---

## 2. Architecture & Component Wiring

```mermaid
flowchart TD
    subgraph DiscoveryLayer ["Client & Discovery Surface"]
        McpAgent["AI Agent (MCP Client)"]
        GqlClient["Frontend / GraphQL Client"]
        BackstagePortal["Spotify Backstage Catalog"]
    end

    subgraph GovernanceGateway ["Autheris Governance Gateway"]
        McpCatalog["McpDatasetCatalog<br/>(list_datasets / describe_dataset)"]
        GqlCatalog["Query.GetCatalogAsync<br/>(GraphQL catalog query)"]
        BackstageService["BackstageCatalogExportService<br/>(/api/integrations/backstage)"]
        VisibilityEngine["CatalogVisibility<br/>(FilterForSubject / ComputeAccessStatus)"]
        MaskingMiddleware["MaskedFieldsMiddleware<br/>(Hot Chocolate Request Pipeline)"]
        DynamicResolver["CatalogGraphQlTypeModule<br/>(ColumnAccessLevel.Mask)"]
    end

    subgraph DataStorage ["Data & Security Repositories"]
        MetaRepo["ITableMetadataRepository"]
        ConsentRepo["IConsentRepository"]
        ApprovalRepo["IConsentApprovalRepository"]
        OwnerRepo["IDataOwnershipRepository"]
    end

    McpAgent -->|list_datasets(tag) / describe_dataset| McpCatalog
    GqlClient -->|catalog(tag)| GqlCatalog
    GqlClient -->|GraphQL Queries| MaskingMiddleware
    BackstagePortal -->|Sync entities| BackstageService

    McpCatalog --> VisibilityEngine
    GqlCatalog --> VisibilityEngine
    BackstageService --> OwnerRepo
    BackstageService --> MetaRepo

    VisibilityEngine --> MetaRepo
    VisibilityEngine --> ConsentRepo
    VisibilityEngine --> ApprovalRepo
    McpCatalog --> OwnerRepo

    MaskingMiddleware --> DynamicResolver
```

### Component Roles & Responsibilities

> **Limitation - dataset tags and visibility tiers:** Dataset tags and the visibility tiers `Hidden`, `Summary` and `Full` are part of the catalog model but are not persisted in the governance database. Tables therefore always carry no tags and the default visibility, and the tag filters and tier behavior described below have no effect on stored data. Access status (`GRANTED`, `PENDING`, `NONE`) and data ownership are persisted and work as described.

- **`McpDatasetCatalog` & `McpDatasetTools`:**
  - `list_datasets(search, domain, tag, offset, limit)`: Filters datasets by tag (case-insensitive substring/equality), populates `tags` on `McpDatasetSummary`, and attaches dynamic `accessStatus` (`GRANTED`, `PENDING`, `NONE`).
  - `describe_dataset(dataset)`: Resolves table data owners and delegates from `IDataOwnershipRepository`, populating `dataOwner` (formatted as `DisplayName <email>` or account) and `dataOwnerDelegates`. Returns `accessStatus` and `tags`.
- **`CatalogVisibility` Service:**
  - `ComputeAccessStatus(table, activeConsents, pendingRequests, isAdmin)`: Determines the caller's relationship to the table:
    - `"GRANTED"`: Caller has active Allow consent (and no unconditional Deny) or is GovernanceAdmin/ClusterAdmin.
    - `"PENDING"`: Caller has an active pending consent request in `IConsentApprovalRepository`.
    - `"NONE"`: Caller has no active consent or pending request.
  - `FilterForSubject(allTables, activeConsents, tenantId)`: Implements the `DatasetVisibility` tiers on the catalog model (not effective on persisted data, see limitation above):
    - `Hidden`: Omitted completely when caller has no consent (Zero Existence Oracle).
    - `Summary`: Included with `accessStatus: "NONE"` or `"PENDING"`, with column schema hidden (`Columns: []`).
    - `Full`: Included with schema metadata regardless of access status.
- **`BackstageCatalogExportService`:**
  - Synchronizes governed table entities (`data-{schema}-{table}`) into Backstage `API` entities (`backstage.io/v1alpha1`).
  - Injects `IDataOwnershipRepository` to map active data owners directly into `spec.owner` (e.g. `steward@corp.internal`), falling back to `GatewayOptions.Backstage.DefaultOwner` only when unowned.
  - Emits annotations (`autheris.io/data-owner`) and tags (`tags`) corresponding to dataset tags.
- **`MaskedFieldsMiddleware` & `DynamicTableType`:**
  - Catalog-based schema (`CatalogGraphQlTypeModule`): the governed tree query compiles masked columns as SQL literals. After executing a root field, `IGovernedTreeQueryService.GetMaskedColumns` reports which selected columns (including columns of nested relations, identified by their relation path) are masked for the caller. When a column resolver then returns a value for such a column, it records its response path as a pointer (`/root/0/relation/1/column`, list indexes included) into the request-scoped `ConcurrentBag<string>` stored in `context.ContextData["MaskedFields"]`, one entry per row. Clear columns are never recorded.
  - `MaskedFieldsMiddleware`: Registered in the Hot Chocolate execution pipeline. When masked fields are recorded, attaches `extensions.maskedFields` containing a distinct, sorted list of field paths. Omitted completely when no masking occurs.

---

## 3. Configuration & Usage Examples

### 3.1 MCP Tool Invocations

#### Listing Datasets with Tag Filtering
```json
{
  "name": "list_datasets",
  "arguments": {
    "tag": "financial",
    "limit": 20
  }
}
```

**Response Payload:**
```json
{
  "datasets": [
    {
      "id": "sales.public.orders",
      "description": "Enterprise customer orders and checkout events",
      "sensitivity": "HIGH",
      "columnCount": 12,
      "graphQlField": "sales_public_orders",
      "tags": ["financial", "sales", "pii"],
      "accessStatus": "GRANTED"
    },
    {
      "id": "treasury.public.ledgers",
      "description": "General corporate financial ledgers",
      "sensitivity": "RESTRICTED",
      "columnCount": 0,
      "graphQlField": "treasury_public_ledgers",
      "tags": ["financial", "treasury"],
      "accessStatus": "NONE"
    }
  ],
  "total": 2,
  "hasMore": false
}
```

#### Describing Dataset with Data Owner
```json
{
  "name": "describe_dataset",
  "arguments": {
    "dataset": "sales.public.orders"
  }
}
```

**Response Payload:**
```json
{
  "id": "sales.public.orders",
  "domain": "sales",
  "schema": "public",
  "tableName": "orders",
  "description": "Enterprise customer orders and checkout events",
  "sensitivity": "HIGH",
  "requiresFourEyes": true,
  "tags": ["financial", "sales", "pii"],
  "dataOwner": "Alice Johnson <alice.johnson@corp.internal>",
  "dataOwnerDelegates": ["Bob Smith <bob.smith@corp.internal>"],
  "accessStatus": "GRANTED",
  "columns": [
    {
      "columnName": "id",
      "dataType": "uuid",
      "isSensitive": false,
      "isPrimaryKey": true
    },
    {
      "columnName": "amount",
      "dataType": "numeric",
      "isSensitive": true,
      "isPrimaryKey": false
    }
  ]
}
```

---

### 3.2 GraphQL Catalog Query & Response Extensions

#### Catalog Query with Tag Filter
```graphql
query GetFinancialCatalog {
  catalog(tag: "financial") {
    domain
    tableName
    displayName
    sensitivity
    tags
    accessStatus
    columns
  }
}
```

**Response Payload:**
```json
{
  "data": {
    "catalog": [
      {
        "domain": "sales",
        "tableName": "orders",
        "displayName": "Customer Orders",
        "sensitivity": "HIGH",
        "tags": ["financial", "sales", "pii"],
        "accessStatus": "GRANTED",
        "columns": ["id", "order_date", "customer_id", "amount"]
      },
      {
        "domain": "treasury",
        "tableName": "ledgers",
        "displayName": "Corporate Ledgers",
        "sensitivity": "RESTRICTED",
        "tags": ["financial", "treasury"],
        "accessStatus": "NONE",
        "columns": []
      }
    ]
  }
}
```

#### Masked Response with `extensions.maskedFields`
```graphql
query GetOrderDetails {
  sales_public_orders(first: 2) {
    id
    customerEmail
    totalAmount
  }
}
```

**Response Payload:**
```json
{
  "data": {
    "sales_public_orders": [
      {
        "id": "ord-1001",
        "customerEmail": "a***@example.com",
        "totalAmount": 149.99
      },
      {
        "id": "ord-1002",
        "customerEmail": "j***@company.org",
        "totalAmount": 420.00
      }
    ]
  },
  "extensions": {
    "maskedFields": [
      "/sales_public_orders/0/customerEmail",
      "/sales_public_orders/1/customerEmail"
    ]
  }
}
```

---

### 3.3 Spotify Backstage Catalog Synchronization

When exported to Backstage via `GET /api/integrations/backstage/catalog-entities`, table entities automatically resolve their active data owners from the governance repository:

```yaml
apiVersion: backstage.io/v1alpha1
kind: API
metadata:
  name: data-public-orders
  namespace: default
  title: public.orders
  description: Enterprise customer orders and checkout events.
  tags:
    - data-product
    - table
    - postgresql
    - pii
    - four-eyes-required
    - sensitivity-high
    - financial
    - sales
  annotations:
    backstage.io/managed-by-location: url:/api/integrations/backstage/catalog-entities/data-public-orders
    autheris.io/source-type: PostgreSQL
    autheris.io/schema-name: public
    autheris.io/table-name: orders
    autheris.io/sensitivity: HIGH
    autheris.io/requires-four-eyes: "true"
    autheris.io/data-owner: alice.johnson@corp.internal
spec:
  type: openapi
  lifecycle: production
  owner: alice.johnson@corp.internal
  system: data-mesh
```

---

## 4. Security, Zero-Trust & Fail-Closed Behavior

1. **Zero Existence Oracle (OWASP API1:2023), applies once visibility tiers can be stored (see limitation above):**
   - Tables configured with `DatasetVisibility.Hidden` where the caller has no active Allow consent fail closed. They do not appear in `list_datasets`, GraphQL `catalog`, or search queries. Direct lookups (`describe_dataset`, `sample_rows`) throw `TableNotFoundException`, returning an identical response to non-existent tables.
2. **Graduated Discoverability (OWASP API3:2023), applies once visibility tiers can be stored (see limitation above):**
   - Tables with `DatasetVisibility.Summary` allow consumers to discover dataset existence and contact the data owner (`DataOwner`), while column schemas remain completely withheld (`Columns: []`) until consent is requested and approved.
3. **Tamper-Proof Response Masking Metadata:**
   - `extensions.maskedFields` records strictly abstract field paths. It never discloses raw cleartext values, HMAC salts, masking algorithms, or cryptographic keys.
4. **Performance & Zero Overhead:**
   - If a GraphQL query executes without triggering any `ColumnAccessLevel.Mask` rules, `extensions.maskedFields` is omitted completely, adding zero bytes and 0B memory overhead to the response payload.
