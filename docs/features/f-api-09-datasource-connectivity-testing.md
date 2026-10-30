# F-API-09: Pre-Flight Datasource Connectivity Testing & Secret Scrubbing

**Status:** [Done] (100% GA – Production-Ready)  
**Components:** [`DatasourceTestingService.cs`](file:///root/autheris/src/Autheris.Application/Catalog/Services/DatasourceTestingService.cs), [`IDatasourceTestingService.cs`](file:///root/autheris/src/Autheris.Application/Catalog/Interfaces/IDatasourceTestingService.cs), [`CatalogApiEndpoints.cs`](file:///root/autheris/src/Autheris.Api/Endpoints/CatalogApiEndpoints.cs), [`EgressUrlPolicy.cs`](file:///root/autheris/src/Autheris.Application/Security/EgressUrlPolicy.cs), [`SecretScrubber.cs`](file:///root/autheris/src/Autheris.Domain/Security/SecretScrubber.cs)

---

## 1. Overview & Problem Statement

When onboarding new upstream databases (PostgreSQL, SQL Server, Oracle, Snowflake, DuckDB) or declarative REST APIs into the data catalog, misconfigured credentials, network firewalls, or invalid connection strings frequently cause runtime query failures for end users. Furthermore, testing connections naively introduces two severe enterprise vulnerabilities:
1. **Server-Side Request Forgery (SSRF)**: Malicious administrators or compromised credentials targeting loopback interfaces (`127.0.0.1`) or Cloud Instance Metadata Services (`169.254.169.254`).
2. **Credential Leakage in Error Logs**: Database driver connection strings containing embedded passwords or bearer tokens leaked in stack traces and UI error dialogs.

**F-API-09** introduces a hardened pre-flight **Datasource Connectivity Testing Service** via `POST /api/v1/catalog/datasources/{id}/test`. It validates connection handshakes without querying real business data, enforces strict egress security policies, and guarantees zero password leakage.

---

## 2. Business Value

- **Zero-Data Probing**: Executes strictly isolated ping/handshake queries (e.g. `SELECT 1` or HTTP `HEAD`) without touching customer tables.
- **SSRF Protection by Default**: Validates hostnames and IP addresses against `EgressUrlPolicy`. Disallows loopback (`127.0.0.0/8`), link-local (`169.254.0.0/16`), RFC 1918 private subnets, and IPv6 loopback (`::1`) unless explicitly permitted in dev environments.
- **Automated Secret Scrubbing**: All connection errors, exception messages, and socket timeouts pass through `SecretScrubber`, redacting database passwords, API keys, and connection strings before returning them to the client.
- **WORM Audit Trail**: Every connection test is recorded to the immutable audit log with tenant, user SID, target host, and result status.

---

## 3. Architecture & Validation Pipeline

```mermaid
flowchart TD
    CLIENT["Admin / DevPortal Client<br/>POST /api/v1/catalog/datasources/{id}/test"] --> AUTH["Tenant & Role Check<br/>(ClusterAdmin or DomainOwner)"]
    AUTH --> SSRF{"EgressUrlPolicy<br/>Check Host/IP"}
    
    SSRF -- Loopback / Cloud Metadata --> BLOCKED["SecurityException<br/>HTTP 400 Bad Request<br/>'Target IP/host is prohibited by egress policy.'"]
    
    SSRF -- Allowed Egress --> RESOLVE["Resolve Secrets<br/>(KeyVaultSecretProvider)"]
    RESOLVE --> HANDSHAKE["Pre-Flight Handshake<br/>• Relational: OpenConnection + SELECT 1<br/>• REST API: HTTP HEAD / GET health probe"]
    
    HANDSHAKE --> SCRUB["SecretScrubber<br/>(Redact passwords, tokens & credentials)"]
    SCRUB --> AUDIT["IAuditLogRepository<br/>(Record DatasourceTested event)"]
    AUDIT --> RESULT["DatasourceTestResult<br/>(Success: true/false, LatencyMs, SafeMessage)"]
```

---

## 4. Usage Examples

### Testing a Relational Database Connection

**Request:**
```bash
curl -X POST "http://localhost:8080/api/v1/catalog/datasources/finance_db/test" \
  -H "Authorization: Bearer <admin-token>" \
  -H "Content-Type: application/json" \
  -d '{
    "timeoutSeconds": 5
  }'
```

**Successful Response:**
```json
{
  "datasourceId": "finance_db",
  "success": true,
  "statusCode": 200,
  "latencyMs": 14.2,
  "message": "Connection handshake and probe query (SELECT 1) succeeded."
}
```

**Failed Response with Secret Scrubbing (Password Redacted):**
```json
{
  "datasourceId": "finance_db",
  "success": false,
  "statusCode": 400,
  "latencyMs": 2005.1,
  "message": "Connection to host 'db.internal.corp:5432' failed: Password authentication failed for user 'finance_svc' [PASSWORD REDACTED]."
}
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "DatasourceTesting": {
      "Enabled": true,
      "DefaultTimeoutSeconds": 5,
      "MaxTimeoutSeconds": 30,
      "AllowPrivateIps": false,
      "ProhibitedHosts": [
        "169.254.169.254",
        "metadata.google.internal",
        "localhost",
        "127.0.0.1"
      ]
    }
  }
}
```

## SQL data sources

For SQL data sources the endpoint opens a real connection through the configured connection factory (see [F-AUTH-04](f-auth-04-sqlserver-windows-auth-ntlm-kerberos.md#35-testing-a-sql-server-data-source)). Host and port come from the connection string, `ProbeDetails` contains `Dialect`, `AuthPath`, the effective `LoginName` (never a password) and, on failure, a `Category`. After a login failure the data source is protected by a circuit breaker: further probes are refused with category `LoginBlocked` and the remaining seconds, without contacting the server, so the endpoint cannot drive Active Directory lockout (see [F-AUTH-04 section 3.7](f-auth-04-sqlserver-windows-auth-ntlm-kerberos.md#37-lockout-protection-login-circuit-breaker)). TLS details are not determined and are not fabricated.

## Listing Data Sources

`GET /api/v1/catalog/datasources` (and the MCP tool `list_datasources`, the resource `autheris://catalog/datasources` and the `invoke_api` route of the same name) discloses endpoint base URLs and authentication state. It is restricted to `GovernanceAdmin` and `ClusterAdmin`; a `GovernanceAdmin` of a tenant sees only the data sources of the tables of its tenant. A `GovernanceAdmin` without a resolvable tenant receives `403` with the code `TENANT_UNRESOLVED` (no tenant claim and no `Gateway:Tenancy:DefaultTenantId`) or `TENANT_CLAIM_INVALID` (malformed claim).
