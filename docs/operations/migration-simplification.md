# Simplification Migration Guide

This guide documents configuration, transport, and module changes for deployments upgrading to the simplified Autheris architecture.

---

## 1. Removed and Modularized Capabilities

The table below lists every capability removed (`DELETE`) or extracted into an optional module (`MODULE`), the configuration keys that fail startup through `UnknownGatewayKeyGuard` or `LegacySwitchGuard`, and the replacement or module installation path.

| Item | Classification | Removed / Affected Configuration Keys | Startup Guard Message | Replacement / Module Install |
| :--- | :--- | :--- | :--- | :--- |
| **SQLite governance store** | DELETE | `Gateway:GovernanceDb:Provider = "Sqlite"`, `Gateway:GovernanceDb:ConnectionString = "Data Source=*.db"` | `SQLite is no longer supported as the governance store` | Use `PostgreSql` (default) or `SqlServer` for `Gateway:GovernanceDb:Provider`; export and re-import via the governance API. |
| **SQLite data source** | DELETE | `Gateway:DataSources:*` with `Provider = "Sqlite"` | `Unsupported data source dialect: Sqlite` | Migrate local or test data sources to PostgreSQL, SQL Server, Oracle, or DuckDB OLAP. |
| **ReBAC and OpenFGA** | DELETE | `Gateway:Rebac`, `Gateway:Rebac:OpenFgaApiUrl`, `Gateway:Rebac:OpenFgaStoreId` | `Unknown configuration keys under 'Gateway'` | Express access decisions as time-limited consent or access-profile grant records evaluated by `IAccessDecisionPoint`. |
| **Casbin policy engine** | DELETE | `Gateway:Casbin`, `Gateway:Casbin:EnforceInQueryPipeline` | `Unknown configuration keys under 'Gateway'` | Express data access rules as explicit allow/deny consent grants and row filters; roles apply to the control plane only. |
| **HTTP `Flight SQL` and `PG wire`** | DELETE | `Gateway:FlightSql` | `Unknown configuration keys under 'Gateway'` | Use Trino client protocol (`/v1/statement`), REST WebSQL (`/api/v1/sql`), or REST Arrow IPC export (`/api/v1/export/arrow`). |
| **FinOps cost accounting** | DELETE (replaced) | `Gateway:FinOps`, `Gateway:FinOps:ScopeMonthlyBudgets` | `Unknown configuration keys under 'Gateway'` | Replaced by core usage accounting per user and AD group (`Gateway:UsageAccounting`) with per-source cost models. |
| **Developer portal** | DELETE | `Gateway:DevPortal` | `Unknown configuration keys under 'Gateway'` | Use the OpenAPI specification endpoints and Authorized Data Discovery API (`/api/v1/catalog/discovery`). |
| **Snowflake SQL dialect generator** | DELETE | `Gateway:DataSources:*` with `Dialect = "Snowflake"` | `Unsupported SQL dialect: Snowflake` | Use supported governed data-source dialects: PostgreSQL, SQL Server, Oracle, or Databricks. |
| **Embedded Garnet server** | DELETE | `Gateway:Caching:Garnet:EnableEmbeddedServer`, `Gateway:Caching:Garnet:DisableConsole` | `Unknown configuration keys under 'Gateway'` | Connect to an external Redis/Garnet cluster (`Gateway:Caching:Redis`) or use the per-node in-memory cache. |
| **Traffic shadowing** | DELETE | `Gateway:Diagnostics:Shadowing`, `Gateway:Shadowing` | `Unknown configuration keys under 'Gateway'` | Use pre-flight query simulation and parity/load test suites outside production traffic paths. |
| **Purview, Collibra, Alation, DataHub connectors** | DELETE | `Gateway:Catalog:Purview`, `Gateway:Catalog:Collibra`, `Gateway:Catalog:Alation`, `Gateway:Catalog:DataHub`, `Gateway:Catalog:WebhookSecret`, `Gateway:Catalog:SyncMode` | `Unknown configuration keys under 'Gateway'` | Push external metadata through the Catalog API (`/api/v1/catalog/*`) or use the OpenMetadata module. |
| **Backstage integration** | DELETE | `Gateway:Backstage` | `Unknown configuration keys under 'Gateway'` | Integrate portals directly against the Catalog Discovery and Consent REST/GraphQL APIs. |
| **ITSM integration (ServiceNow / Jira)** | DELETE | `Gateway:Itsm`, `Gateway:HitLStepUp:AutoCreateItsmTicket`, `Gateway:HitLStepUp:PreferredItsmSystem`, `Gateway:Itsm:warn_fallback_default_tenant_for_webhooks` | `Unknown configuration keys under 'Gateway'` | Use the generic Consent Workflow API and outbound workflow webhook (`Gateway:Workflows`). |
| **Break-glass and justification headers** | DELETE | `Gateway:Extensibility:EnableBreakGlass`, `Gateway:Extensibility:RequireJustificationForBreakGlass`, `Gateway:Extensibility:RequireRoleForBreakGlass`, `Gateway:Extensibility:BreakGlassAllowedRoles`, `Gateway:Extensibility:BreakGlassHeaderName`, `Gateway:Extensibility:JustificationHeaderName`, `Gateway:Extensibility:TicketValidationRegex`, `Gateway:Governance:IndividualGrants:BreakGlassMaxHours`, `Gateway:Governance:IndividualGrants:MaxBreakGlassDurationHours` | `Unknown configuration keys under 'Gateway'` | Request time-limited individual or group grants via the Consent API (`/api/v1/consents`) with external or four-eyes approval. |
| **Differential-privacy noise** | DELETE | `Analytics:Privacy:Noise`, `Gateway:Analytics:Privacy:Noise` | `Analytics:Privacy:Noise (removed (OD-11); small-group suppression applies)` | Governed aggregation with minimum group size (small-group suppression) and time buckets in core. |
| **CDN cache purge** | DELETE | `Gateway:Cdn`, `Gateway:Cdn:PurgeMode` | `Unknown configuration keys under 'Gateway'` | Manage HTTP cache invalidation in upstream reverse proxies or CDN control planes. |
| **SchemaRegistry** | DELETE | `Gateway:SchemaRegistry` | `Unknown configuration keys under 'Gateway'` | Manage schema contracts through the core Catalog API or the dbt module. |
| **Anonymous OpenSchema flag** | DELETE (replaced) | `Gateway:OpenSchema` | `Unknown configuration keys under 'Gateway'` | Replaced by Authorized Data Discovery for authenticated users and AI agents (`Gateway:Catalog:Discovery`). |
| **WORM archive S3/file export** | DELETE | `Gateway:Audit:Worm:Enabled` | `Audit:Worm:Enabled is no longer supported` | Core retains HMAC-chained audit logs and signed retention purge checkpoints; export long-term archives via SIEM or DB backup. |
| **Lakehouse (Delta / Apache Iceberg)** | MODULE | `Gateway:Lakehouse`, `Gateway:Lakehouse:MaxConcurrentFileScans` | `Unknown configuration keys under 'Gateway'` (when module is not loaded) | Enable `Lakehouse` in `Gateway:Modules:Enabled` or deploy the `autheris-full` image (`Autheris.Module.Lakehouse`). |
| **dbt and OpenMetadata** | MODULE | `Gateway:Dbt`, `Gateway:OpenMetadata` | `Unknown configuration keys under 'Gateway'` (when module is not loaded) | Enable `Dbt` / `OpenMetadata` in `Gateway:Modules:Enabled` or deploy the `autheris-full` image. |
| **CDC / Streaming and event webhooks** | MODULE | `Gateway:CdcStream`, `Gateway:CdcWebhooks`, `Gateway:CustomQueryPoller`, `Gateway:Webhooks` (event delivery) | `Unknown configuration keys under 'Gateway'` (when module is not loaded) | Enable `Cdc` in `Gateway:Modules:Enabled` or deploy the `autheris-full` image (consent workflow webhooks remain in core). |
| **Lineage impact analysis and OpenLineage** | MODULE | `Gateway:Lineage`, `Gateway:OpenLineage` | `Unknown configuration keys under 'Gateway'` (when module is not loaded) | Enable `Lineage` in `Gateway:Modules:Enabled` or deploy the `autheris-full` image (GDPR Art. 15 disclosure report remains in core). |

---

## 2. Removed Configuration Keys Enforced at Startup

`UnknownGatewayKeyGuard` rejects any unknown or removed key under `Gateway` at startup across all environments. In particular, the following removed key prefixes always fail startup:

- `Gateway:Itsm`
- `Gateway:Backstage`
- `Gateway:Catalog:Purview`
- `Gateway:Catalog:Collibra`
- `Gateway:Catalog:Alation`
- `Gateway:Catalog:DataHub`
- `Gateway:Catalog:WebhookSecret`
- `Gateway:Extensibility:EnableBreakGlass`
- `Gateway:Extensibility:RequireJustificationForBreakGlass`
- `Gateway:Extensibility:RequireRoleForBreakGlass`
- `Gateway:Extensibility:BreakGlassAllowedRoles`
- `Gateway:Extensibility:BreakGlassHeaderName`
- `Gateway:Extensibility:JustificationHeaderName`
- `Gateway:Extensibility:TicketValidationRegex`
- `Gateway:Governance:IndividualGrants:BreakGlassMaxHours`
- `Gateway:Governance:IndividualGrants:MaxBreakGlassDurationHours`
- `Gateway:HitLStepUp:AutoCreateItsmTicket`
- `Gateway:HitLStepUp:PreferredItsmSystem`
- `Analytics:Privacy:Noise`
- `Gateway:Analytics:Privacy:Noise`

Additional unconsumed or legacy keys removed from options binding (`LegacySwitchGuard` and `Baselines/removed-options.txt`):

- `Gateway:Authentication:danger_allow_anonymous_access` (use `Gateway:Insecure:danger_allow_anonymous_access`)
- `Gateway:GovernanceDb:danger_bypass_consent_checks` (use `Gateway:Insecure:danger_bypass_consent_checks`)
- `Gateway:GovernanceDb:warn_auto_approve_access_requests` (use `Gateway:Insecure:warn_auto_approve_access_requests`)
- `Gateway:RateLimiting:warn_disable_rate_limiting` (use `Gateway:Insecure:warn_disable_rate_limiting`)
- `Gateway:GraphQL:warn_allow_all_cors_origins` (use `Gateway:Insecure:warn_allow_all_cors_origins`)
- `Gateway:GraphQL:warn_relaxed_query_limits` (use `Gateway:Insecure:warn_relaxed_query_limits`)
- `Gateway:GraphQL:warn_enable_introspection` (use `Gateway:Insecure:warn_enable_introspection`)
- `Gateway:DataMasking:danger_disable_column_masking` (use `Gateway:Insecure:danger_disable_column_masking`)
- `Gateway:OpenMetadata:danger_bypass_webhook_signature_validation` (use `Gateway:Insecure:danger_bypass_webhook_signature_validation`)
- `Gateway:OpenMetadata:warn_ignore_webhook_timestamp_tolerance` (use `Gateway:Insecure:warn_ignore_webhook_timestamp_tolerance`)
- `Gateway:OpenMetadata:danger_allow_untrusted_certificates` (use `Gateway:Insecure:danger_allow_untrusted_certificates`)
- `Gateway:Itsm:danger_bypass_webhook_signature_validation` (use `Gateway:Insecure:danger_bypass_webhook_signature_validation`)
- `Gateway:Itsm:warn_ignore_webhook_timestamp_tolerance` (use `Gateway:Insecure:warn_ignore_webhook_timestamp_tolerance`)
- `Gateway:Itsm:warn_mock_external_systems_if_unreachable` (use `Gateway:Insecure:warn_mock_external_systems_if_unreachable`)
- `Gateway:Itsm:danger_allow_untrusted_certificates` (use `Gateway:Insecure:danger_allow_untrusted_certificates`)
- `Gateway:Mcp:warn_allow_unmasked_ai_access` (use `Gateway:Insecure:warn_allow_unmasked_ai_access`)
- `Gateway:Mcp:danger_bypass_mcp_auth` (use `Gateway:Insecure:danger_bypass_mcp_auth`)
- `Gateway:Lakehouse:warn_allow_unsigned_s3_requests` (use `Gateway:Insecure:warn_allow_unsigned_s3_requests`)
- `Gateway:Lakehouse:danger_bypass_lakehouse_auth` (use `Gateway:Insecure:danger_bypass_lakehouse_auth`)
- `Gateway:Dbt:danger_bypass_webhook_signature_validation` (use `Gateway:Insecure:danger_bypass_webhook_signature_validation`)
- `Gateway:WebSql:warn_allow_dml` and `Gateway:Insecure:warn_allow_websql_dml` (use `Gateway:WebSql:AllowDml`)
- `Gateway:WebSql:danger_bypass_sql_governance` (use `Gateway:Insecure:danger_bypass_websql_governance`)
- `Gateway:Profile` (use `Gateway:Dev:Preset`)
- `Gateway:Tenancy:Mode` and `Gateway:Authentication:ForwardAuth:DefaultTenantId` (use `Gateway:Tenancy:DefaultTenantId`)
- `Gateway:Itsm:warn_fallback_default_tenant_for_webhooks`, `Gateway:Insecure:warn_fallback_default_tenant_for_webhooks`, `Gateway:Caching:Redis:FailClosedOnRevocationLookupError`

---

## 3. Governance Store Migration (PostgreSQL and SQL Server)

SQLite is no longer supported as a governance or metadata store. The gateway fails startup immediately if `GovernanceDb:Provider` is set to `Sqlite` or if `GovernanceDb:ConnectionString` uses a SQLite file or in-memory connection string (`Data Source=*.db`, `Data Source=*.sqlite`, `:memory:`, `Mode=Memory`, or `Filename=`), with no environment exemption.

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
