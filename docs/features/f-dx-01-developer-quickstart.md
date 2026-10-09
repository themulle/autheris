# F-DX-01: Zero-Config Developer Quickstart & Dev Portal Hub

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`QuickstartExtensions.cs`](file:///root/autheris/src/Autheris.Api/Configuration/QuickstartExtensions.cs), [`TestAuthHandler.cs`](file:///root/autheris/src/Autheris.Infrastructure/Security/TestAuthHandler.cs)

---

## 1. Overview & Problem Statement

Setting up complex enterprise infrastructure (Active Directory, Kerberos, Redis cluster, Purview, multiple SQL instances) slows down local developer onboarding. F-DX-01 provides a zero-external-dependency local developer quickstart: an embedded in-memory SQLite governance catalog seeded with 10 realistic enterprise domains, an in-process Garnet cache, and test authentication simulation allowing developers to run the entire gateway with a single `dotnet run` command.

---

## 2. Business Value

- **Instant Developer Onboarding**: New developers and QA engineers can clone the repository and run all tests or the local gateway in under 2 minutes.
- **Zero Cost & Cloud Independence**: No need for cloud subscriptions or Docker setups for local feature development.
- **Production-Safety Guardrails**: Test authentication and mock seeds are strictly forbidden and fail-closed outside of the `Development` environment.

---

## 3. Architecture & Capabilities

- Pre-seeded SQLite database with 10 enterprise domains (Finance, Sales, HR, CRM, Inventory, etc.).
- Header-based user SID, group, and role simulation via `TestAuthHandler`.
- Integrated interactive Swagger UI and Banana Cake Pop GraphQL IDE out-of-the-box.

---

## 4. Usage Example

```bash
# Run locally with zero dependencies
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Autheris.Api/Autheris.Api.csproj

# Execute test query using simulated developer identity
curl -X POST http://localhost:5000/graphql \
  -H "Content-Type: application/json" \
  -H "GraphQL-Preflight: 1" \
  -H "X-Test-User-Sid: S-1-5-21-CONSUMER-1" \
  -H "X-Test-Group-Sids: S-1-5-21-FINANCE-ANALYSTS" \
  -d '{"query": "{ catalog { domain schemaName tableName displayName } }"}'
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "Authentication": {
      "EnableTestAuthHandler": true
    },
    "GovernanceDb": {
      "Provider": "Sqlite",
      "ConnectionString": "Data Source=:memory:",
      "SeedDemoData": true
    }
  }
}
```
