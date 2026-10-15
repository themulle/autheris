# F-DATA-04: Apache Arrow Flight SQL Egress & Zero-Copy Analytics Pipeline

**Feature-ID:** F-DATA-04  
**Components:** [`ArrowFlightSqlEndpoints.cs`](file:///root/autheris/src/Autheris.Api/Endpoints/ArrowFlightSqlEndpoints.cs), [`IArrowFlightSqlServer.cs`](file:///root/autheris/src/Autheris.Application/Serialization/IArrowFlightSqlServer.cs), [`ArrowFlightSqlServer.cs`](file:///root/autheris/src/Autheris.Infrastructure/Serialization/ArrowFlightSqlServer.cs)

---

## 1. Overview & Problem Statement

Transferring multi-million-row datasets over standard JSON REST endpoints creates CPU and allocation overhead due to row-by-row JSON serialization and repeated memory copies. `F-DATA-04` implements a governed Apache Arrow Flight SQL egress pipeline over HTTP/2 and Apache Arrow IPC streaming (`application/vnd.apache.arrow.stream`). Governed database results are converted into binary columnar Arrow `RecordBatch` payloads and streamed directly to analytical clients (DuckDB, Pandas, Polars, PyArrow, PySpark) with full row-level security (RLS) pushdown and format-preserving column masking.

---

## 2. Business Value

- **High-Throughput Columnar Ingestion**: Streams binary Apache Arrow `RecordBatch` payloads (`application/vnd.apache.arrow.stream`) without row-by-row JSON serialization overhead.
- **Zero-Copy Memory Layout**: Direct columnar memory layout compatibility between the gateway and client analytics engines (`pyarrow.ipc`, Polars, DuckDB).
- **Tamper-Proof Two-Phase Flight Protocol**: Query planning (`POST /api/v1/flight/sql/info`) issues an HMAC-SHA256 signed `FlightSqlTicket` bound to the authenticated subject SID, tenant, and expiration timestamp before streaming (`POST /api/v1/flight/sql/stream`).

---

## 3. Architecture & Component Wiring

1. **Flight Info & Ticket Issuance (`POST /api/v1/flight/sql/info`)**:
   - Validates the caller's SQL query and returns a `FlightSqlInfoResponse` containing the target `FlightEndpointUri` (`/api/v1/flight/sql/stream`) and an HMAC-SHA256 signed `FlightSqlTicket`.
   - Each ticket binds the SQL command, caller `SubjectSid`, `TenantId`, and expiration timestamp (`ExpiresAtUnixMs`) using a 256-bit cryptographic signing key.
2. **Governed Catalog Discovery (`GET /api/v1/flight/sql/tables`)**:
   - Returns the list of governed catalog tables (`FlightSqlTableMetadata`) visible to the authenticated caller, filtered by tenant isolation and active Casbin ABAC / Data-Owner Consents.
3. **Binary Arrow IPC Streaming (`POST /api/v1/flight/sql/stream`)**:
   - Verifies the HMAC-SHA256 signature, expiration, and caller identity (`SubjectSid` and `TenantId`) of the submitted `FlightSqlTicket` in constant time (`CryptographicOperations.FixedTimeEquals`).
   - Executes the query through `IGovernedSqlExecutionService` using transport identifier `SqlTransports.FlightSql`, enforcing AST-level RLS pushdown, column masking, and max row limits.
   - Serializes the governed result set into an Apache Arrow IPC stream (`application/vnd.apache.arrow.stream`) via `IArrowExportService` and sets the `X-Autheris-Truncated` response header (`true` or `false`).

### Federated Query Transport Opt-In (`WebSql:CrossSource:AllowedTransports`)

Federated queries that span several data sources are only available when `Gateway:WebSql:CrossSource:Enabled` is `true`, and only for transports that are explicitly listed in `Gateway:WebSql:CrossSource:AllowedTransports`. Valid names are the constants of `SqlTransports`: `WebSql`, `Trino`, `Rest`, `Mcp`, `ArrowExport`, `FlightSql`, `SqlEndpoints`, and `AsyncJob`. When nothing is configured, only `WebSql` and `Trino` are allowed. Every other channel is rejected on federated queries (generic "not supported" response) unless its name is listed. Flight SQL queries use the transport name `FlightSql`; the Arrow IPC export endpoint uses `ArrowExport`. An explicitly empty list allows no transport at all (fail-closed). A configured list replaces the default; it is not merged with it.

```json
{
  "Gateway": {
    "WebSql": {
      "CrossSource": {
        "Enabled": true,
        "AllowedTransports": [ "WebSql", "Trino", "FlightSql" ]
      }
    }
  }
}
```

---

## 4. Usage Example

```python
import httpx
import pyarrow as pa
import pyarrow.ipc as ipc

headers = {"Authorization": "Bearer <token>"}

# 1. Request Flight SQL info and signed ticket
info_resp = httpx.post(
    "https://gateway.internal/api/v1/flight/sql/info",
    json={"sqlQuery": "SELECT * FROM finance.dbo.general_ledger"},
    headers=headers,
)
info_resp.raise_for_status()
ticket = info_resp.json()["ticket"]

# 2. Stream binary Apache Arrow RecordBatches
stream_resp = httpx.post(
    "https://gateway.internal/api/v1/flight/sql/stream",
    json=ticket,
    headers=headers,
)
stream_resp.raise_for_status()
truncated = stream_resp.headers.get("X-Autheris-Truncated") == "true"

with ipc.open_stream(pa.BufferReader(stream_resp.content)) as reader:
    table = reader.read_all()
    print(table.to_pandas().head())
```

---

## 5. Security, Zero-Trust & Fail-Closed Behavior

- **Cryptographic Ticket Binding**: `ArrowFlightSqlServer` signs tickets with HMAC-SHA256 over the canonical payload (`SqlQuery`, `SubjectSid`, `TenantId`, `ExpiresAtUnixMs`). Any tampering, replay after expiry, or cross-principal/cross-tenant ticket substitution is rejected with `UnauthorizedAccessException` (`403 Forbidden`).
- **Fail-Closed Key Validation**: In non-development environments, `ArrowFlightSqlServer` requires an explicit cryptographic signing key (`FlightSql:TicketSigningKey` or `Governance:CursorSigningKey`) of at least 32 bytes.
- **Full RLS & Masking Enforcement**: Queries execute through `IGovernedSqlExecutionService` with `SqlTransports.FlightSql`, ensuring identical Casbin ABAC, Data-Owner Consent, RLS predicate injection, and column masking as WebSQL and REST endpoints.
