# F-GOV-09: Virtual Filters (Relation-Scoped Cross-Channel Mandatory Row Filtering)

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`VirtualFilterEndpoints.cs`](file:///root/autheris/src/Autheris.Api/Endpoints/VirtualFilterEndpoints.cs), [`MandatoryRowFilterResolver.cs`](file:///root/autheris/src/Autheris.Application/VirtualFilters/MandatoryRowFilterResolver.cs), [`StructuredFilterSqlBuilder.cs`](file:///root/autheris/src/Autheris.Application/VirtualFilters/StructuredFilterSqlBuilder.cs), [`SqlFilterCompiler.cs`](file:///root/autheris/src/Autheris.Application/VirtualFilters/SqlFilterCompiler.cs), [`VirtualFilterAdministrationService.cs`](file:///root/autheris/src/Autheris.Application/VirtualFilters/VirtualFilterAdministrationService.cs), [`VirtualFilterSnapshotProvider.cs`](file:///root/autheris/src/Autheris.Application/VirtualFilters/VirtualFilterSnapshotProvider.cs), [`TableAccessPolicy.cs`](file:///root/autheris/src/Autheris.Application/Policy/TableAccessPolicy.cs)

---

## 1. Overview & Problem Statement

In enterprise data access, traditional Row-Level Security (RLS) policies are frequently defined and bound directly to individual tables or rely on disparate per-table consent grants. When business entities span multiple related tables, views, and stored procedure outputs, maintaining independent row filters per object leads to policy drift, high administrative overhead, and potential data leakage when new tables or endpoints are introduced.

**F-GOV-09: Virtual Filters** introduces a declarative, relation-scoped mandatory row filtering subsystem that applies universally across all data access channels:
- Ad-hoc WebSQL (`/api/v1/sql`, `/v1/statement`)
- GraphQL table and selection tree queries (`/graphql`)
- OData v4 endpoints (`/odata/v4/*`)
- Apache Arrow Flight SQL & binary export (`/api/v1/flight/sql/*`, `/api/v1/export/arrow`)
- Governed Stored Procedures (`/api/v1/procedures/*`)
- Embedded DuckDB OLAP queries (`/api/v1/olap/query`)

Virtual filters define reusable filter predicates independently of target tables, yielding key values or predicates that protected objects must satisfy. Virtual filters **never grant access**; they act strictly as mandatory constraints combined via logical conjunction (`AND`) with data-owner consents.

---

## 2. Business Value

- **Universal Cross-Channel Parity**: Exactly identical row filtering semantics are enforced regardless of whether an analyst accesses data through Power BI (OData), Python (Arrow Flight), WebSQL, or web applications (GraphQL).
- **Zero-Drift Access Profiles**: Grantees (Users, Groups, Roles, Service Principals) are assigned `AccessProfile`s governing broad object patterns (`sales.*.*`). Any new table matching the pattern inherits the mandatory filter immediately.
- **Fail-Closed Uncovered Object Governance**: If a table within a profile's scope is not covered by any binding, the profile explicitly dictates whether access is denied (`UncoveredPolicy.Deny` - fail closed) or left to explicit consents alone (`UncoveredPolicy.Skip`).
- **Auditability & Explainability**: The `/api/v1/governance/effective-filters` endpoint allows compliance officers and auditors to simulate and explain exactly which virtual filters apply to a given user and table, showing generated target dialect SQL.
- **Microsecond In-Memory Resolution**: Snapshots are cached in-memory and synchronized atomically across cluster nodes via monotonic generation counters, adding under **0.05 ms** (p99) to query latency.

---

## 3. Architecture & Capabilities

```
┌────────────────────────────────────────────────────────────────────────┐
│ Incoming Query (WebSQL / GraphQL / OData / Arrow / Stored Procedures)  │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│ TableAccessPolicy.DecideAsync                                          │
│ 1. Resolve Data-Owner Consents (Allow/Deny, Column Masking)            │
│ 2. Evaluate Casbin ABAC & ReBAC Relations                              │
│ 3. Resolve Virtual Filters via MandatoryRowFilterResolver:             │
│    - Identify active AccessProfiles for caller (User, Groups, Roles)   │
│    - Match target against profile Scope and FilterBinding patterns     │
│    - Apply Supersedes replacements                                     │
│    - Render Structured or SQL Predicates for target Dialect (target)   │
│    - Evaluate Uncovered Policy if no binding applies                   │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│ Combined Predicate: (Consent_RLS) AND (VirtualFilter_1 AND ...)        │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │
       ┌────────────────────────────┴─────────────────────────────┐
       ▼                                                          ▼
┌──────────────────────────────────┐               ┌──────────────────────────────────┐
│ Relational SQL Backends          │               │ Non-Relational / External Sources│
│ (PostgreSQL, MSSQL, SQLite, ...) │               │ (In-Memory, Lakehouse raw, Mesh) │
│ Predicate pushed down into WHERE │               │ Fail-closed: 403 Forbidden       │
└──────────────────────────────────┘               └──────────────────────────────────┘
```

### 3.1 Virtual Filter Definitions

A `VirtualFilter` is scoped to a catalog data source and specifies either:
1. **Structured Definition**: Starting table (`from`), join chain (`joins`), and equality/null conditions (`where`).
2. **SQL Predicate**: Trino SQL expression referencing the protected entity as `target` (e.g. `EXISTS (SELECT 1 FROM finance.dbo.accounts a WHERE a.id = target.account_id AND a.is_active = true)`).
   - SQL definitions automatically translate Trino date/time functions (`date_add`, `date_diff`, `now()`, etc.) into target database dialect functions (MSSQL `DATEADD`, PostgreSQL `+ INTERVAL`, SQLite `datetime(...)`).
   - Length capped at 4,000 characters to prevent denial-of-service.

### 3.2 Access Profiles & Binding Matching

An `AccessProfile` binds virtual filters to a specific grantee:
- **Grantee**: User SID, Group SID, Service Principal SID, or Role Name.
- **Scope**: 3-segment pattern `source.schema.object` (e.g. `sales.dbo.*`).
- **Uncovered Policy**: `Deny` (caller denied on any table in scope lacking a matching filter binding) or `Skip` (table remains governed by consent alone).
- **Bindings**: Specific filter bindings with optional target pattern overrides, object kinds (`Relation`, `ProcedureResult`), time window columns (`TimeColumn`), and column remappings (`ColumnMap`).

### 3.3 Fail-Closed Transport Enforcement

- **Relational Databases**: Filters compile into target dialect SQL subqueries or joins and push directly into the database engine.
- **In-Memory & External Transports**: Data sources that evaluate filters in memory (or lack relational pushdown capabilities) fail closed with `403 Forbidden` if a virtual filter applies, preventing accidental unfiltered data exposure.
- **Envoy Mesh Integration (`ext_authz`)**: Service-to-service calls authenticated via Envoy `ext_authz` are denied access if the caller identity is restricted by virtual filters.
- **CDC Streaming & Lakehouse REST Catalog**: Realtime subscriptions and raw Iceberg REST access reject requests for objects restricted by virtual filters.

---

## 4. REST Administration & Explanation APIs

All endpoints require authentication under `/api/v1/governance` and enforce tenant boundaries:

### 4.1 List Filters & Profiles

```http
GET /api/v1/governance/virtual-filters
Authorization: Bearer <token>
```

**Response:**
```json
{
  "generation": 42,
  "filters": [
    {
      "name": "active_client_only",
      "source": "sales",
      "keyColumns": ["client.id"],
      "supersedes": []
    }
  ],
  "profiles": [
    {
      "name": "analyst_emea_sales",
      "granteeType": "Role",
      "roleName": "SalesAnalyst",
      "scope": "sales.dbo.*",
      "uncovered": "Deny",
      "bindings": [
        {
          "filterName": "active_client_only",
          "targetPattern": "sales.dbo.orders",
          "objectKinds": ["Relation", "ProcedureResult"]
        }
      ]
    }
  ]
}
```

### 4.2 Create or Update a Virtual Filter

```http
PUT /api/v1/governance/virtual-filters/active_client_only
Content-Type: application/json
Authorization: Bearer <token>

{
  "source": "sales",
  "sql": "EXISTS (SELECT 1 FROM sales.dbo.clients c WHERE c.id = target.client_id AND c.status = 'ACTIVE')"
}
```

### 4.3 Create or Update an Access Profile

```http
PUT /api/v1/governance/access-profiles/analyst_emea_sales
Content-Type: application/json
Authorization: Bearer <token>

{
  "granteeType": "Role",
  "roleName": "SalesAnalyst",
  "scope": "sales.dbo.*",
  "uncovered": "Deny",
  "bindings": [
    {
      "filterName": "active_client_only",
      "targetPattern": "sales.dbo.*",
      "objectKinds": ["Relation", "ProcedureResult"]
    }
  ]
}
```

### 4.4 Declarative Sync & Safety Thresholds

For GitOps pipelines (Talos), Autheris provides atomic sync endpoints:
- `POST /api/v1/governance/virtual-filters/sync/plan`: Pre-calculates changes without applying them.
- `POST /api/v1/governance/virtual-filters/sync/apply`: Applies changes in a single database transaction.

> [!WARNING]
> If a sync operation attempts to delete more bindings than `VirtualFilters:MaxRemovals` (default: 10), or attempts to delete all bindings, the sync is rejected unless submitted with `?force=true` by a caller holding the `FilterAdmin` role.

### 4.5 Explain Effective Filters

Auditors and security engineers can inspect effective filters for any user:

```http
GET /api/v1/governance/effective-filters?user=S-1-5-21-12345678-500&table=sales.dbo.orders
Authorization: Bearer <token>
```

**Response:**
```json
{
  "user": "S-1-5-21-12345678-500",
  "table": "sales.dbo.orders",
  "decision": "Permit",
  "appliedFilters": [ "active_client_only" ],
  "effectiveSql": "EXISTS (SELECT 1 FROM sales.dbo.clients c WHERE c.id = autheris_target.client_id AND c.status = 'ACTIVE')"
}
```

---

## 5. Configuration Example (`appsettings.json`)

```json
{
  "Gateway": {
    "VirtualFilters": {
      "MaxRemovals": 10,
      "GenerationCheckSeconds": 5
    }
  }
}
```

| Option | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `MaxRemovals` | `int` | `10` | Maximum number of bindings that a sync may remove without `force=true`. Removing bindings widens access; setting to `0` disables the limit. |
| `GenerationCheckSeconds` | `int` | `5` | Polling interval in seconds to detect generation increments from other cluster nodes. `0` verifies generation on every request. |

---

## 6. Verification & Automated Test Suites

The Virtual Filter subsystem is validated by comprehensive unit, integration, and cross-channel parity tests:
- `CrossChannelVirtualFilterParityTests.cs`: Proves that WebSQL, GraphQL, OData, and stored procedures return identical rows under virtual filters.
- `VirtualFilterAdministrationTests.cs`: Validates CRUD, GitOps atomic sync, `MaxRemovals` safeguards, and input validation.
- `VirtualFilterResolutionPerformanceTests.cs`: Benchmarks resolution overhead, ensuring p99 latency remains under 0.05 ms.
- `VirtualFilterProcedureParityTests.cs`: Verifies row-scope and virtual filter enforcement on stored procedure outputs.
