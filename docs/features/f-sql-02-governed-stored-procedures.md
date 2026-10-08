# F-SQL-02: Governed Stored Procedures & Multi-DB TVFs

Autheris enables enterprise organizations to securely expose version-controlled, mission-critical SQL Server stored procedures and table-valued functions (TVFs) as governed REST endpoints (`/api/v1/procedures/{name}`) and auto-generated OpenAPI 3.0 specifications.

Unlike traditional direct database execution or unmanaged API wrappers, all procedure executions pass through Autheris's central **Zero-Trust Data-Owner-Consent** engine, enforcing tenant isolation, caller context binding, caller role verification, source-entity consent resolution, dynamic column-level masking, and fail-closed result pruning.

Architecture and design rationale are specified in [ADR-018: Governed Stored Procedures](../adr/ADR-018-governed-stored-procedures.md).

---

## 🏛️ Architecture Overview

```
                                      +---------------------------------------------+
                                      | CLIENT REQUEST (GET/POST /api/v1/procedures)|
                                      +---------------------------------------------+
                                                             │
                                                             ▼
                                      +---------------------------------------------+
                                      | 1. HTTP Ingress & Protocol Arbitration      |
                                      |    - Role validation (@roles / Casbin)      |
                                      |    - Request parameter binding & validation |
                                      |    - Non-tamperable context injection       |
                                      +---------------------------------------------+
                                                             │
                                                             ▼
                                      +---------------------------------------------+
                                      | 2. Consent & ABAC Pre-Execution Check       |
                                      |    - Source table & column consent check    |
                                      |    - Deny / Mask / Clear policy resolution  |
                                      +---------------------------------------------+
                                                             │
                                                             ▼
                                      +---------------------------------------------+
                                      | 3. Governed Database Execution              |
                                      |    - Technical connection (autheris_proc)   |
                                      |    - Zero-Trust SESSION_CONTEXT injection   |
                                      |    - Streaming ADO.NET execution (1st set)  |
                                      +---------------------------------------------+
                                                             │
                                                             ▼
                                      +---------------------------------------------+
                                      | 4. Zero-Trust Post-Processing Pipeline      |
                                      |    - Fail-Closed Column Pruning (undeclared)|
                                      |    - Column-Level Masking (HMAC / Redaction)|
                                      |    - Secondary Row Scope Filtering (RLS key)|
                                      |    - Immutable HMAC-SHA256 WORM Audit Log   |
                                      +---------------------------------------------+
                                                             │
                                                             ▼
                                      +---------------------------------------------+
                                      | JSON Payload ({ columns, rows, rowCount })  |
                                      +---------------------------------------------+
```

---

## ⚙️ Configuration (`appsettings.json`)

Enable and configure stored procedure endpoints in the `Gateway` options block:

```json
{
  "Gateway": {
    "SqlEndpoints": {
      "Procedures": {
        "Enabled": true,
        "Directory": "procedures",
        "ConnectionName": "procedures",
        "AllowedSchemas": [ "api", "reports" ],
        "RevalidationIntervalMinutes": 15,
        "LockTimeoutMs": 5000,
        "MaxRows": 5000,
        "MaxStringParameterLength": 4000,
        "MaxTimeoutSeconds": 60,
        "EnableHotReload": true,
        "AllowDeclaredValidation": true
      }
    },
    "DataSources": {
      "Connections": {
        "procedures": {
          "Provider": "SqlServer",
          "ConnectionString": "Server=sql.internal;Database=EnterpriseDB;User Id=autheris_proc;Password=...;TrustServerCertificate=True;"
        }
      }
    }
  }
}
```

### Configuration Options Reference

| Option | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `Enabled` | `bool` | `false` | Master switch enabling the stored procedure endpoint subsystem. |
| `Directory` | `string` | `"procedures"` | Directory containing `.proc.yaml` and `.proc.sql` declarations. Monitored via hot-reload. |
| `ConnectionName` | `string` | `"procedures"` | Data source connection name in `DataSources:Connections` used for execution. |
| `AllowedSchemas` | `List<string>` | `[]` | Allowed database schemas for procedures (e.g. `["api", "reports"]`). Procedures in undeclared schemas fail validation. |
| `RevalidationIntervalMinutes` | `int` | `15` | Periodic background revalidation interval against database catalogs. |
| `LockTimeoutMs` | `int` | `5000` | Database lock timeout (`SET LOCK_TIMEOUT`) in milliseconds to prevent catalog lock contention. |
| `MaxRows` | `int` | `5000` | Hard cap on the maximum rows returned per execution (`0` = unlimited). Results exceeding this are truncated with `truncated: true`. |
| `MaxStringParameterLength` | `int` | `4000` | Maximum character length accepted for string parameters to prevent memory exhaustion attacks. |
| `MaxTimeoutSeconds` | `int` | `60` | Upper bound for `@timeout` values declared in procedure definitions. |
| `EnableHotReload` | `bool` | `true` | Enables `FileSystemWatcher` for zero-downtime hot-reloading of procedure definitions upon disk modification. |
| `AllowDeclaredValidation` | `bool` | `false` | Permits `validation: declared` mode in non-Development environments for zero-privilege production execution. |

---

## 📜 Declarative Definition Formats

Autheris supports two formats for defining stored procedure endpoints:
1. **Contract-First Pure YAML (`*.proc.yaml`)** — *Recommended for production and zero-privilege deployment.*
2. **SQL Comment Header (`*.proc.sql`)** — *Recommended for rapid prototyping and catalog-backed development.*

### Format 1: Contract-First Pure YAML (`procedures/get_customer_orders.proc.yaml`)

```yaml
name: get_customer_orders
procedure: api.usp_GetCustomerOrders
mode: read
kind: stored_procedure
summary: Retrieves order history for a customer with source column governance
validation: declared

integrity:
  ddl_hash: "sha256:7f83b1657ff1fc53b92dc18148a1d65dfc2d4b1fa3d677284addd200126d9069"

parameters:
  - name: customer_id
    type: int
    required: true
    description: Unique customer identifier
  - name: order_status
    type: nvarchar(20)
    required: false
    description: Optional status filter (e.g. 'SHIPPED', 'PENDING')

context:
  tenant_id: "@TenantId"
  user_sid: "@ActorSid"

outputs:
  - name: order_id
    type: int
    source_table: sales.orders
    source_column: order_id
  - name: customer_id
    type: int
    source_table: sales.orders
    source_column: customer_id
  - name: order_total
    type: decimal(18,2)
    source_table: sales.orders
    source_column: total_amount
  - name: internal_margin
    type: decimal(18,2)
    source_table: sales.orders
    source_column: margin_amount
  - name: order_date
    type: datetime2
    source_table: sales.orders
    source_column: created_at

referenced_tables:
  - sales.orders

row_scope_key: order_id
roles:
  - order-reader
  - sales-analyst
timeout: 30
```

### Format 2: SQL Header Annotation (`procedures/get_orders.proc.sql`)

```sql
-- @name get_orders
-- @procedure api.usp_GetOrders
-- @mode read
-- @summary Retrieves orders for a given customer
-- @param customer_id int required Customer number
-- @param note nvarchar(40) optional Search note
-- @context tenant_id -> @TenantId
-- @context user_sid  -> @ActorSid
-- @result-table sales.orders
-- @result-column order_id clear
-- @row-scope-key order_id
-- @roles order-reader
-- @timeout 30
```

---

## 🔒 Security Invariants & Zero-Privilege Runtime

### 1. Dual Validation Modes: Catalog vs. Declared

Autheris provides two validation modes to suit different enterprise security profiles:

| Mode | Database Privileges Required | Source Mapping | Verification Mechanism |
| :--- | :--- | :--- | :--- |
| **`catalog`** | `EXECUTE`, `VIEW DEFINITION`, `VIEW DATABASE STATE` | Discovered dynamically via `sys.dm_exec_describe_first_result_set_for_object` in browse mode (`browse = 1`). | Database catalog inspection of `sys.sql_modules`, `sys.parameters`, and object permissions. |
| **`declared` (Pure YAML)** | **`GRANT EXECUTE` ONLY (Zero Metadata Privileges)** | Statically declared in YAML via `source_table` and `source_column`. | Contract-first validation: Result schema, source entities, and parameter types are taken directly from the YAML contract. |

> [!IMPORTANT]
> **Zero-Privilege Runtime Principle:**
> In `validation: declared` mode, the technical database user `autheris_proc` requires **zero metadata permissions** (`NO VIEW DEFINITION`, `NO VIEW DATABASE STATE`, `NO SELECT` on tables). It only requires `GRANT EXECUTE ON SCHEMA::api TO autheris_proc`.

### 2. Source Entity & Column-Level Governance (Review D-2)

Stored procedures often project columns using custom aliases (e.g. `SELECT total_amount AS invoice_val, ssn AS taxpayer_id FROM ...`). Traditional gateways either bypass column masking or break because the alias doesn't match the database catalog.

Autheris solves this via **Source Column Mapping**:
- Every output column is linked to its underlying source entity (`source_table: sales.orders`, `source_column: ssn`).
- Before returning data, Autheris queries the central consent and Casbin ABAC engine for `sales.orders.ssn`.
- If the column is marked **`Mask`**, Autheris applies deterministic zero-allocation format-preserving masking or HMAC-SHA256 pseudonymization.
- If marked **`Deny`**, the column is automatically stripped from the response payload.
- Computed columns or aggregates with no source table are stripped unless explicitly marked with `clear: true` or `@result-column <name> clear`.

### 3. Fail-Closed Result Pruning

If a DBA or developer modifies a stored procedure on the database to return additional sensitive columns (e.g. `SELECT *, salary, password_hash FROM ...`), Autheris enforces **fail-closed column pruning**:
- Any column returned by the database at runtime that was **not explicitly declared** in `outputs` is automatically dropped from the JSON response before leaving the gateway.

### 4. Cryptographic DDL Integrity Verification

To detect out-of-band modifications or unauthorized procedure updates on the database:
- Procedure declarations support `integrity.ddl_hash: "sha256:..."`.
- During validation, Autheris computes the SHA-256 hash of the procedure's source definition in `sys.sql_modules`.
- If the computed hash differs from the declared hash, the endpoint is immediately disabled (`ProcedureState.Disabled`) with an integrity mismatch error.

### 5. Context Binding (Non-Tamperable Caller State)

Parameters marked with `@context` or under the `context:` block are resolved directly from the authenticated caller's security claims and cannot be supplied or overridden by the HTTP client:

- `tenant_id -> @TenantId`: The caller's validated tenant identifier.
- `user_sid -> @ActorSid`: The caller's canonical Windows SID or Entra ID `oid`.
- `purpose -> @Purpose`: The declared data processing purpose.

These parameters are omitted from the public OpenAPI specification to prevent confusion and tampering.

### 6. Post-Execution Row Scope Filtering & Virtual Filters (`row_scope_key`)

Because stored procedures encapsulate their own SQL queries, database Row-Level Security (RLS) filters cannot be directly rewritten into the procedure's internal `WHERE` clauses.

To enforce row-level security and cross-channel policy parity on procedure outputs:
1. The declaration defines a unique key: `row_scope_key: order_id` (or composite `[client_id, order_id]`) pointing to the primary key or unique index of `result_table`.
2. The key columns are verified against database unique indexes (`sys.indexes` or declared contract).
3. **Data-Owner Consents & Virtual Filters ([`F-GOV-09`](f-gov-09-virtual-filters.md))**:
   - `GovernedProcedureExecutionService` evaluates active data-owner row filters as well as `AccessProfile` virtual filter bindings for `FilterObjectKinds.ProcedureResult`.
   - If the procedure result table is in a profile's scope with `UncoveredPolicy.Deny` and no virtual filter binding covers it, execution is rejected immediately (fail-closed).
4. After the procedure executes, Autheris extracts the distinct key values from the result rows.
5. Autheris issues an authoritative secondary verification query against the data source combining tenant isolation, consent RLS, and effective virtual filter predicates:
   ```sql
   SELECT order_id FROM sales.orders AS autheris_target
   WHERE tenant_id = @TenantId 
     AND (<Data-Owner Consent RLS Predicate>)
     AND (<Virtual Filter Predicates>)
     AND order_id IN (@k0, @k1, ...)
   ```
6. Result rows whose keys do not appear in the authoritative filtered set are stripped from the response. Any applied virtual filters are logged to the cryptographic WORM audit trail.

---

## 🛠️ Offline CI/CD Generator Tool (`IProcedureYamlGenerator`)

To generate contract-first `.proc.yaml` declarations automatically during CI/CD builds or against staging databases, Autheris includes the `IProcedureYamlGenerator` service:

### C# API Usage

```csharp
using Autheris.Application.Procedures.Tools;

IProcedureYamlGenerator generator = serviceProvider.GetRequiredService<IProcedureYamlGenerator>();

ProcedureYamlSpec spec = new()
{
    EndpointName = "get_customer_orders",
    ProcedureName = "api.usp_GetCustomerOrders",
    Summary = "Retrieves customer order history",
    ValidationMode = "declared",
    ResultTable = "sales.orders",
    RowScopeKey = "order_id",
    AllowedRoles = new[] { "order-reader" },
    ContextBindings = new Dictionary<string, string>
    {
        ["tenant_id"] = "@TenantId",
        ["user_sid"] = "@ActorSid"
    }
};

using var dbConnection = new SqlConnection("Server=staging-sql;Database=EnterpriseDB;...");
await dbConnection.OpenAsync();

// Inspects staging metadata and outputs valid .proc.yaml
string yamlOutput = await generator.GenerateYamlAsync(spec, dbConnection);
await File.WriteAllTextAsync("procedures/get_customer_orders.proc.yaml", yamlOutput);
```

The generator inspects `sys.sql_modules`, `sys.parameters`, and `sys.dm_exec_describe_first_result_set_for_object`, calculates the SHA-256 DDL hash, infers source column mappings, and produces compliant YAML files.

---

## 🌐 HTTP Endpoints & API Contract

### 1. Execute Procedure

`GET` / `POST /api/v1/procedures/{name}`

**Request Body (for POST, optional for GET via query params):**
```json
{
  "customer_id": 1042,
  "order_status": "SHIPPED"
}
```

**Response (`200 OK`, `Cache-Control: no-store`):**
```json
{
  "columns": [
    { "name": "order_id", "type": "int" },
    { "name": "customer_id", "type": "int" },
    { "name": "order_total", "type": "decimal" },
    { "name": "internal_margin", "type": "decimal" },
    { "name": "order_date", "type": "datetime2" }
  ],
  "rows": [
    [1001, 1042, 149.99, "***MASKED***", "2026-10-01T14:20:00Z"],
    [1002, 1042, 89.50, "***MASKED***", "2026-10-03T09:15:00Z"]
  ],
  "rowCount": 2,
  "truncated": false
}
```

### 2. Discover Active Endpoints

`GET /api/v1/procedures`

Returns a list of all active procedure endpoints visible to the caller's assigned roles.

### 3. OpenAPI 3.0 Documentation

`GET /api/v1/procedures/openapi.json`

Dynamically generates OpenAPI 3.0 specifications for all active procedure endpoints for interactive exploration in Swagger UI.

### HTTP Error Codes

| Status Code | Reason | Description |
| :--- | :--- | :--- |
| `400 Bad Request` | Invalid Parameter | Missing required parameter, type coercion failure, or length exceeded. |
| `403 Forbidden` | Access Denied | Caller lacks required `@roles` or data-owner consent is missing/denied. |
| `404 Not Found` | Unknown Endpoint | Endpoint name does not exist or caller lacks visibility. |
| `422 Unprocessable` | Business Exception | Stored procedure invoked `THROW 50000..59999` with an intentional domain error message. |
| `503 Service Unavailable` | Endpoint Disabled | Procedure failed catalog/integrity validation or database connection is offline. |

---

## 🔑 Database Setup & Least-Privilege Permissions

### Staging / CI Environment (`catalog` mode & generator)

```sql
-- Login and user for catalog discovery and generator tool
CREATE LOGIN autheris_ci WITH PASSWORD = 'StrongPassword123!';
CREATE USER autheris_ci FOR LOGIN autheris_ci;

GRANT EXECUTE ON SCHEMA::api TO autheris_ci;
GRANT VIEW DEFINITION ON SCHEMA::api TO autheris_ci;
GRANT VIEW DATABASE STATE TO autheris_ci;
```

### Production Environment (`declared` mode - Zero-Privilege)

```sql
-- Production technical login with ZERO metadata permissions
CREATE LOGIN autheris_proc WITH PASSWORD = 'UltraSecureProductionPassword!';
CREATE USER autheris_proc FOR LOGIN autheris_proc;

-- Grant EXECUTE only
GRANT EXECUTE ON SCHEMA::api TO autheris_proc;

-- DENY metadata views (defense-in-depth verification)
-- Autheris requires NO VIEW DEFINITION, NO VIEW DATABASE STATE, and NO SELECT on tables.
```

---

## 🧪 Verification & Testing

The stored procedure governance engine is verified by comprehensive test suites in `tests/Autheris.Tests.Unit`:

- [`ProcedureEndpointTests.cs`](file:///root/autheris/tests/Autheris.Tests.Unit/ProcedureEndpointTests.cs): Parsing, context binding, execution timeouts, role checks, and error code mappings.
- [`PureYamlProcedureGovernanceTests.cs`](file:///root/autheris/tests/Autheris.Tests.Unit/Procedures/PureYamlProcedureGovernanceTests.cs): Zero-privilege pure-YAML declarations, source column masking, fail-closed column pruning, and generator round-trip.
- [`ArchitectureAndCleanCodeTests.cs`](file:///root/autheris/tests/Autheris.Tests.Unit/ArchitectureAndCleanCodeTests.cs): DDL hash verification, parameter sanitization, and DI registration.
