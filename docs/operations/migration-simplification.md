# Simplification Migration Guide

This guide documents configuration and operational changes for deployments upgrading to the simplified Autheris architecture.

---

## Governance store

SQLite is no longer supported as a governance or metadata store (owner decision OD-1). The gateway fails startup immediately if `GovernanceDb:Provider` is set to `Sqlite` or if `GovernanceDb:ConnectionString` uses a SQLite file or in-memory connection string (`Data Source=*.db`, `Data Source=*.sqlite`, `:memory:`, `Mode=Memory`, or `Filename=`), with no environment exemption.

### Required Action

Configure PostgreSQL (default) or SQL Server for `Gateway:GovernanceDb`:

| Removed Configuration | Replacement | Notes |
|---|---|---|
| `Gateway:GovernanceDb:Provider = "Sqlite"` | `Gateway:GovernanceDb:Provider = "PostgreSql"` or `"SqlServer"` | Default provider is `PostgreSql` when `GovernanceDb:ConnectionString` is supplied. |
| `Gateway:GovernanceDb:ConnectionString = "Data Source=*.db"` | PostgreSQL or SQL Server connection string | Outside `Development`, server certificate verification is mandatory (`SSL Mode=VerifyFull` or `VerifyCA` for PostgreSQL; `Encrypt=True;TrustServerCertificate=False` for SQL Server). |

### Example Configuration (PostgreSQL)

```json
{
  "Gateway": {
    "GovernanceDb": {
      "Provider": "PostgreSql",
      "ConnectionString": "Host=postgres.internal;Port=5432;Database=autheris_governance;Username=autheris;SSL Mode=VerifyFull"
    }
  }
}
```

### Example Configuration (SQL Server)

```json
{
  "Gateway": {
    "GovernanceDb": {
      "Provider": "SqlServer",
      "ConnectionString": "Server=sqlserver.internal,1433;Database=autheris_governance;User Id=autheris;Encrypt=True;TrustServerCertificate=False"
    }
  }
}
```
