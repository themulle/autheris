# Autheris: Endpunkt-Inventar und Testbarkeit

Stand: Code-Lesung (Oktober 2026). Alle Routen werden in `GatewayApplicationBuilderExtensions.MapGatewayEndpoints` registriert.
Standard: FallbackPolicy `RequireAuthenticatedUser`; Rollen werden meist im Handler geprueft.
CSRF: GraphQL immer, REST bei Browser-Indikatoren (Cookie/Origin/Referer) -> Header `GraphQL-Preflight: 1`, `X-Requested-With` oder `X-CSRF-Token`.

| Gruppe | Pfade | Auth | Aktivierung | Test (Integration) |
|---|---|---|---|---|
| GraphQL | `/graphql`, `/graphql/{domain}` (GET/POST/WS) | Auth + CSRF | immer; Nitro-UI nur Dev | WalkingSkeleton, EndToEndSqlite |
| Metrics | `/metrics` | Auth | immer | -- |
| Health | `/health/live`, `/health/ready` | anonym | immer | WalkingSkeleton, EndToEndSqlite |
| Auth | `/api/auth/login` (GET/POST), `/session`, `/logout` | Basic/ForwardAuth/Session | immer | WalkingSkeleton |
| Dev | `/`, `/getting-started`, `/api/dev/info`, `/personas`, `/login/{persona}` | anonym | nur Development | InsecureGettingStarted |
| System | `/api/governance/system/{metrics,health,resource-groups}` | Rollen | `SystemMetrics.Enabled` | -- |
| WebSQL | `POST /api/v1/sql`, `/api/sql` | Auth | `WebSql.Enabled` | GovernedWebSql, **EndToEndSqlite** |
| Deklarative SQL-Endpunkte | `/api/v1/queries/` (non-admins see name, summary, parameters only), `/openapi.json`, `/{name}` GET/POST | Auth | `SqlEndpoints.Enabled` + `WebSql.Enabled` | **EndToEndSqlite** |
| Stored Procedures | `/api/v1/procedures/*` | Auth | `SqlEndpoints.Procedures.Enabled` (nur SQL Server) | nur Unit |
| OData / OpenAPI | `/odata/v4`, `$metadata`, `$openapi[/index]`, `{domain}/openapi.json|yaml`, `/api/v1/openapi/index`, `/ui/swagger` (Swagger UI, lokal ausgeliefert, offline-faehig; `/docs` und `$swagger` sind Aliase) | Auth (GovernanceAdmin/ClusterAdmin for the spec routes) oder anonym bei `OpenSchema` | immer | ODataIntegration, OpenApiIntegration, **EndToEndSqlite**, **OpenApiDocsEndToEnd** (Index-Crawl, Spec-Validitaet, Docs-UI-Links) |
| MCP | `/mcp`, `/mcp/sse`, `/mcp/message`, `DELETE /mcp/session/{id}` | Auth | `Mcp.Enabled` | McpIntegration, **EndToEndSqlite** |
| Backstage | `/api/integrations/backstage/catalog-entities[/{name}]`, `catalog-info.yaml` | Auth | `Backstage.Enabled` | BackstageIntegration, **EndToEndSqlite** |
| dbt | `/api/extensions/dbt/*` (sync, exposures, proposals, validate-contract, run-results, health, webhook) | Rollen (global dbt state: GovernanceAdmin/ClusterAdmin, DbtAdmin for sync/run-results; a plain DataOwner has no access, E-7) | immer | DbtIntegration |
| Governance | `/api/governance/{catalog/ingest-openapi, policy-simulation/replay (GovernanceAdmin/ClusterAdmin/PrivacyAdmin/Auditor; DataOwner only for an owned `TargetTable`), sunsetting/*, differential-privacy/*, eu-ai-act/*, gdpr/export-pdf}`, `/api/lineage/openlineage/sync` | Rollen | immer | GovernanceAdvancedMoats |
| Schema Registry | `/api/schema-registry/{publish,check,services,{svc}/latest,{svc}/history}` | Rollen | immer | SchemaRegistryEndpointSecurity |
| Webhooks | `/api/webhooks/{openmetadata,itsm/status-change,servicenow,jira,catalog}`, `/api/v1/governance/catalog/webhook/{provider}` | HMAC-Signatur | immer | Itsm, OpenMetadata |
| CDC-Streaming | `/api/v1/cdc/{events,subscriptions}` | Rollen | immer | RealtimeStreamingSubscription |
| HitL | `/api/governance/hitl/{pending,approve,reject}` | Approver-Rollen | `HitLStepUp.Enabled` | -- (Luecke) |
| Token-Revocation | `POST /api/admin/tokens/revoke` | `GovernanceAdmin` | immer | -- (Luecke) |
| FinOps | `/api/v1/finops/{focus,budget/{tenant}}` | Rollen | `FinOps.Enabled` | FinOpsIntegration, FinOpsEndToEndSqlite (+Unit) |
| ReBAC | `/api/v1/rebac/{tuples,check,batch-check}` | Auth | immer | nur Unit |
| Arrow / Flight SQL | `/api/v1/export/arrow`, `/api/v1/flight/sql/{info,tables,stream}` | Auth (+ReBAC) | immer | nur Unit |
| DuckDB-OLAP | `POST /api/v1/olap/query` | Auth | `DuckDbOlap.Enabled` | nur Unit |
| Iceberg REST | `/v1/{prefix}/namespaces/...` | Auth | Lakehouse-Config | LakehouseIntegration |
| Envoy ExtAuthz | `/api/v1/envoy/{authz,check,export/*.yaml}` | Auth | immer | nur Unit |

Insgesamt ca. 104 Routen (5 nur Dev, 4 MCP, 3 Backstage, 4 Procedures).

## Testen

* Vorhandene Tests: `dotnet test Autheris.sln -c Release` (xunit, Shouldly; kein Docker noetig).
* Hosting: `WebApplicationFactory<Program>`, Konfiguration per `UseSetting("Gateway:...")`, Auth per `TestAuthHandler`-Header (`X-Test-User-Sid`, `X-Test-Roles`, `X-Test-Tenant`; nur Development).
* Governance-DB: In-Memory-SQLite (`Data Source=:memory:;Mode=Memory;Cache=Shared`), im Development automatisch geseedet.
* Datenquelle: `Gateway:DataSources:Connections:<name>:Provider=Sqlite` + `ConnectionString` -> echte Zeilen ueber WebSQL/SQL-Endpunkte. Ohne Verbindung liefern GraphQL/OData synthetische Zeilen.
* Neu: `tests/Autheris.Tests.Integration/EndToEndSqliteTests.cs` (HTTP -> Pipeline -> Governance -> SQLite-Datei).
  `dotnet test tests/Autheris.Tests.Integration --filter "FullyQualifiedName~EndToEndSqlite"`
* Manuell (laufender Stack): `curl -u admin:admin -H "X-Requested-With: x" -H "Content-Type: application/json" -d '{"query":"{__typename}"}' http://localhost:8080/graphql`
