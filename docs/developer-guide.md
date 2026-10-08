# Developer Guide - GraphQL Enterprise Gateway

This guide helps engineers run the gateway locally with zero external dependencies, use the developer options, write tests, and extend the codebase.

**Contents**

1. [Prerequisites](#1-prerequisites)
2. [Getting Started](#2-getting-started)
3. [Developer Options (`Gateway:Dev`)](#3-developer-options-gatewaydev)
4. [Authentication in Development](#4-authentication-in-development)
5. [Insecure Modes (Security Relaxations)](#5-insecure-modes-security-relaxations)
6. [Architecture Guidelines & Quality Gates](#6-architecture-guidelines--quality-gates)
7. [Adding New Queries and Dynamic Tables](#7-adding-new-queries-and-dynamic-tables)
8. [Testing Data Catalog Synchronization](#8-testing-data-catalog-synchronization)
9. [Testing Lineage & GDPR Disclosure Queries](#9-testing-lineage--gdpr-disclosure-queries)
10. [Validating Casbin Governance Policies](#10-validating-casbin-governance-policies)
11. [Testing In-Memory OLAP & Arrow Export](#11-testing-in-memory-olap--arrow-export-f-data-03--f-data-04)

---

## 1. Prerequisites

- **.NET SDK**: 10.0 or higher (`net10.0`)
- **IDE**: Visual Studio 2026, VS Code with C# Dev Kit, or JetBrains Rider
- **No external services required**: the gateway ships an in-memory SQLite governance catalog, demo data and configured test users, so it runs completely without Docker, SQL Server or Redis.
- **WSL**: if many integration tests fail with an inotify limit error, run them with `DOTNET_USE_POLLING_FILE_WATCHER=true`.

---

## 2. Getting Started

### 2.1 Build

```bash
dotnet build Autheris.sln
```

The build enforces strict typing and treats warnings as errors (`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`).

### 2.2 Run

```bash
# HTTPS profile (recommended: session cookies are Secure)
dotnet run --project src/Autheris.Api --launch-profile https

# or with hot reload
dotnet watch --project src/Autheris.Api run --launch-profile https
```

| Profile | URL |
|---|---|
| `https` | `https://localhost:7214` (and `http://localhost:5031`) |
| `http` | `http://localhost:5031` |

Both profiles set `ASPNETCORE_ENVIRONMENT=Development`, which switches on the developer options described below. Without a launch profile the first profile (`http`) is used.

### 2.3 Read the startup banner

In Development the gateway prints a banner with the useful links (hub, GraphQL, Swagger, identity, effective configuration, health), the active preset, the database mode and a **table of configured personas** with login links. Plaintext passwords are shown, hashed ones never. You can switch the banner off with `Gateway:Dev:Banner=false`.

### 2.4 Log in and send a first query

`appsettings.Development.json` ships personas, all with the password `dev`:

| User | Role | Purpose |
|---|---|---|
| `dev-admin` | ClusterAdmin | administration |
| `owner` | DataOwner (`S-1-5-21-DATAOWNER-1`) | primary owner of the demo tables `finance_table_1` / `_5` |
| `approver-a`, `approver-b` | DataOwner | delegates of `finance_table_5` (four-eyes approval) |
| `analyst` | Analyst | regular user |
| `analyst-b` | Analyst, tenant `tenant-b` | tests across tenant boundaries |

**Browser:** open the persona login link from the banner. It sets the session cookie and redirects to Nitro (Banana Cake Pop):

```
https://localhost:7214/api/dev/login/owner?redirect=/graphql
```

**curl:** log in once, then use the cookie only:

```bash
curl -u owner:dev -c ~/.autheris.jar https://localhost:7214/api/auth/session
curl -b ~/.autheris.jar -H 'GraphQL-Preflight: 1' -H 'Content-Type: application/json' \
     -d '{"query":"{ catalog { domain schemaName tableName displayName } }"}' \
     https://localhost:7214/graphql
```

**Which instance am I talking to?**

```bash
curl -s https://localhost:7214/api/dev/info | jq .dev   # preset, features, active bypasses; no secrets
```

### 2.5 Reset local state

By default the development database is in memory and starts fresh on every run. If you enabled persistence (see 3.3), stop the gateway and run `scripts/dev-reset.sh`.

---

## 3. Developer Options (`Gateway:Dev`)

All development-only behaviour lives in one section, `Gateway:Dev`. Nothing in it exists in production: **outside Development a non-default value is a startup error**, not silently ignored. Code never checks `IsDevelopment()` for a feature; it reads the resolved `DevFeatures`.

```json
"Gateway": {
  "Dev": {
    "Preset": "Standard",            // Standard | Quickstart | Strict
    "Banner": true,
    "VerboseErrors": true,
    "PersonaLogin": true,
    "Info": true,
    "DemoData": true,
    "Persist": { "Enabled": false, "Directory": ".data" },
    "TestAuthHandler": true,
    "Tooling": { "BananaCakePop": true, "Introspection": true }
  }
}
```

Environment variables work as usual, for example `Gateway__Dev__Persist__Enabled=true`.

### 3.1 Switch classes

| Class | Effect | Where | Production |
|---|---|---|---|
| **A: convenience** | No security effect (banner, verbose errors, persona login, info endpoint, persistence, demo data) | `Gateway:Dev` | startup error |
| **B: dev security** | Changes who can authenticate or what is exposed (`TestAuthHandler`, GraphQL introspection) | `Gateway:Dev`, reported separately | startup error |
| **C: bypass** | Weakens a protection (`warn_*`, `danger_*`) | `Gateway:Insecure` | `warn`: allowed and logged, `danger`: forbidden |

### 3.2 Presets

| Preset | Meaning |
|---|---|
| `Standard` (default) | All convenience on; test auth, introspection, Banana Cake Pop and demo data on; security otherwise strict. |
| `Quickstart` | `Standard` plus `Insecure.warn_allow_all_cors_origins`, `warn_enable_introspection`, `warn_auto_approve_access_requests` and `OpenSchema`. For a first look without setting up consents. Replaces the deprecated `Gateway:Profile: Quickstart`. |
| `Strict` | Like `Standard`, but no test auth and no introspection. Use it to reproduce production-like access. |

A preset is a set of concrete values, not hidden logic. What it applied is visible in `/api/dev/info`, in the banner and in the active bypass list.

### 3.3 Options

| Option | Default | Class | Purpose |
|---|---|---|---|
| `Banner` | `true` | A | Startup banner with links and personas. |
| `VerboseErrors` | `true` | A | A 403 from a role policy names the required and the actual roles. An empty 403 from endpoint code gets a `problem+json` with the caller identity. Unhandled exceptions return `problem+json` with a `traceId`. |
| `PersonaLogin` | `true` | A | `GET /api/dev/personas` (users, SIDs, roles, login links; never passwords) and `GET /api/dev/login/{persona}?redirect=/graphql` (one-click login; only same-site paths are followed). |
| `Info` | `true` | A | `GET /api/dev/info`: preset, resolved features, class B switches, auth schemes, session state, database provider, rate limits, GraphQL flags, active bypasses. No connection strings or passwords. |
| `DemoData` | preset | A | Seeds the demo catalog (idempotent). Alias of `GovernanceDb.SeedDemoData`. |
| `Persist:Enabled` / `Persist:Directory` | `false` / `.data` | A | Keeps the governance database in `<Directory>/dev.db`, so approvals and audit data survive restarts. |
| `TestAuthHandler` | preset | B | Header-based test identities (section 4.1). Alias of `Authentication.EnableTestAuthHandler`. |
| `Tooling:BananaCakePop` | preset | A | Nitro IDE at `/graphql`. Alias of `GraphQL.EnableBananaCakePop` in Development. |
| `Tooling:Introspection` | preset | B | GraphQL introspection. Alias of `GraphQL.EnableIntrospection` in Development. |

**Persistence.** `.data/` is git-ignored. Reset with `scripts/dev-reset.sh [dir]` with the gateway stopped. The script removes the database, its WAL files **and** `dev.db.audit-anchor.json`. Deleting only the database leaves an anchor that points to a sequence the new database does not have, and the next start reports an audit chain integrity violation. In a container, map the directory to a named volume and avoid bind mounts on 9p/virtiofs file systems because of SQLite WAL.

### 3.4 Precedence

Per switch: explicit `Gateway:Dev:*` value, then an explicitly set legacy key (deprecated alias, logged as a note at startup), then the preset default. A `Dev` value that contradicts its legacy key fails the start.

Do not set `EnableTestAuthHandler`, `EnableBananaCakePop`, `EnableIntrospection` or `SeedDemoData` in the base `appsettings.json`: an explicit value there counts as "explicitly configured" and defeats the preset default. `GraphQL.EnableIntrospection` and `GraphQL.EnableBananaCakePop` stay regular options in every environment; only the `Dev:Tooling:*` aliases are limited to Development.

### 3.5 Rules for new switches

1. Does it weaken a protection? Add it to `Gateway:Insecure` with the `warn_` or `danger_` prefix, list it in `GetAllActiveBypasses` and add a test. Never add a domain-local duplicate; an architecture test (`DevSwitchTests`) fails on any `warn_`/`danger_` property outside `InsecureGettingStartedOptions`.
2. Development-only but changes authentication or exposure? Class B: `Gateway:Dev`, listed in `DevFeatures.DevSecurity`.
3. Otherwise class A: `Gateway:Dev`, default `true` in Development.
4. No switch reads `IsDevelopment()` directly. Every dev switch needs a policy in `DevOptionsValidator` and must show up in `/api/dev/info`.

Full option reference: [configuration guide, section 2.14a](configuration-guide.md#214a-dev-development-only-switches).

---

## 4. Authentication in Development

Several schemes are available. An `Authorization` header (Bearer, Basic, Negotiate) always wins over the session cookie.

### 4.1 TestAuthHandler (header-based simulation)

Enabled by `Dev:TestAuthHandler` (on in `Standard` and `Quickstart`, off in `Strict`). Impersonate any user by sending headers:

- `X-Test-User-Sid`: the calling user's SID (e.g. `S-1-5-21-1001`)
- `X-Test-Group-Sids`: comma-separated group SIDs
- `X-Test-Roles`: comma-separated application roles (e.g. `DataConsumer,DataOwner,GovernanceAdmin`)

```bash
curl -X POST https://localhost:7214/graphql \
  -H "Content-Type: application/json" -H "GraphQL-Preflight: 1" \
  -H "X-Test-User-Sid: S-1-5-21-1001" \
  -H "X-Test-Group-Sids: S-1-5-21-FINANCE-ANALYSTS" \
  -d '{"query": "{ catalog { domain schemaName tableName displayName } }"}'
```

### 4.2 Traefik ForwardAuth (Kubernetes ingress simulation)

```bash
curl -X POST https://localhost:7214/graphql \
  -H "Content-Type: application/json" -H "GraphQL-Preflight: 1" \
  -H "X-Forwarded-User: S-1-5-21-1001" \
  -H "X-Forwarded-Groups: S-1-5-21-FINANCE-ANALYSTS" \
  -d '{"query": "{ catalog { domain schemaName tableName } }"}'
```

### 4.3 HTTP Basic with cookie session (F-AUTH-DX)

Basic users are configured under `Gateway:Authentication:BasicAuth:Users`. With `BasicAuth.Session.Enabled=true` (on in `appsettings.Development.json`), one successful Basic login issues an encrypted `__Host-` cookie, so browsers, cookie jars, WebSockets and EventSource stay logged in without re-sending the password.

```bash
curl -u owner:dev https://localhost:7214/api/auth/login          # verify credentials
curl -u owner:dev -c jar https://localhost:7214/api/auth/session # who am I, session id, expiry
curl -b jar -c jar -X POST -H 'X-Requested-With: curl' https://localhost:7214/api/auth/logout
```

Send `X-No-Session: 1` to stay stateless. The session is refused in `Production`. Full behaviour, security properties and limitations: [F-AUTH-DX](features/f-auth-dx-basic-auth-session.md).

**Setting a password.**

- In `Development`, `Password` may be plaintext (or an unsalted SHA-256 hex digest).
- In all other environments, passwords must be hashed (salted **PBKDF2-HMAC-SHA256**; Argon2id is refused in Production).
- Generate production hashes directly using the built-in CLI utility. It outputs **PBKDF2-HMAC-SHA256 with 600 000 iterations** by default (Production refuses Argon2id; `--type argon2id` is only for Development). The password is read from **stdin**; passing it as an argument still works but prints a warning because arguments leak into the process list and shell history.

```bash
read -rs PW && printf '%s\n' "$PW" | dotnet run --project src/Autheris.Api -- hash-password
```

Output:
```text
Autheris Password Hash Generator
--------------------------------
Algorithm: PBKDF2-HMAC-SHA256 (NIST compliant)
Hash:      $pbkdf2$600000$W5/aW44Aq8D4BkSIvYCymA==$CVQyS8iYh0rWXBseQxp84+If0jattqHT6LHedNCDNsg=
```

> [!TIP]
> The `$` characters are the usual pitfall. In `docker-compose.yml` write every `$` as `$$`, in a shell use single quotes, and in JSON no escaping is needed. Otherwise the value is interpolated and the user cannot log in.

---

## 5. Insecure Modes (Security Relaxations)

During early development or when onboarding third-party webhooks (e.g. ServiceNow/Jira tunnels), relax individual protections with `Gateway:Insecure` (ADR-012). Every flag defaults to `false` (fail-closed), and engaged flags are reported loudly (`X-Gateway-Insecure-Mode`, `/api/dev/info`, startup log).

```json
"Insecure": {
  "warn_allow_all_cors_origins": true,
  "warn_disable_rate_limiting": true,
  "danger_allow_untrusted_certificates": true,
  "danger_bypass_webhook_signature_validation": true
}
```

- `Insecure` is the **only** place for `warn_*` / `danger_*` flags. The former domain-local copies (e.g. `Mcp:danger_bypass_mcp_auth`, `GraphQL:warn_allow_all_cors_origins`, `WebSql:warn_allow_dml`) were removed in ADR-012 phase 4. If an old key is still set to `true`, the gateway refuses to start and names the replacement (`LegacySwitchGuard`); `WebSql:warn_allow_dml` became `WebSql:AllowDml`. Environment variables follow the same rule: use `Gateway__Insecure__<name>`.
- `danger_*` flags are forbidden outside Development. Any active `DANGER:` switch causes an immediate, fail-fast process startup crash via `ValidationException`. Some `warn_*` flags (e.g. `warn_enable_introspection`) additionally need the explicit opt-in `AllowInsecureWarnFlagsInProduction`.

> [!WARNING]
> **Migration Notice (R-API-1 / API-1)**:
> Outside of the `Development` environment, setting `Itsm.LegacyGlobalWebhookSecret = true` or enabling any `DANGER:` bypass flags strictly blocks application startup with a `ValidationException`.
> Deployments that previously relied on the global ITSM webhook secret must migrate to per-instance webhook secrets (`itsm:webhook-secret:<instanceId>`).
> Never commit `danger_* = true` or `Itsm.LegacyGlobalWebhookSecret = true` in non-development configuration files.

---

## 6. Architecture Guidelines & Quality Gates

The codebase follows **Clean Architecture**:
- `Autheris.Domain`: pure business rules, entities, options and domain calculation services. No dependencies on external libraries or frameworks.
- `Autheris.Application`: core execution engine (`GatewayExecutionService`), business services (`ConsentResolutionService`, `ColumnMaskingProvider`, `RlsFilterGenerator`, `ChunkedQueryExecutor`), data source executors and repository contracts.
- `Autheris.Infrastructure`: ADO.NET SQL persistence (`SqliteGovernanceRepository`), Redis messaging (`RedisEventBus`), rate limiters, idempotency stores and claims transformation.
- `Autheris.GraphQL`: Hot Chocolate schema configuration, dynamic types, queries, mutations and subscriptions.
- `Autheris.Api`: ASP.NET Core host, authentication handlers (`BasicAuthenticationHandler`, `ForwardAuthAuthenticationHandler`), `Dev` configuration and endpoints, rate limiting middleware, health probes and graceful drain.

### 6.1 Running automated tests

```bash
dotnet test Autheris.sln                                                          # everything
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj  # boundary tests
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj                  # unit & property tests
dotnet test tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj    # walking skeleton
```

### 6.2 GraphQL Spec Compliance & Tooling Compatibility

Hot Chocolate 16 introduces an experimental semantic introspection feature that automatically injects non-standard system types (`__SchemaDefinition`, `__SearchResult`, `__SearchDirective`) into the schema. Because the official GraphQL specification strictly reserves the `__` prefix for standard system introspection types (`__Schema`, `__Type`, `__Field`), strict `graphql-js`-based clients (Apollo Client, GraphiQL, GraphQL Code Generator) fail schema validation.

Autheris explicitly disables this feature via:
```csharp
.ModifyOptions(opt => opt.EnableSemanticIntrospection = false)
```
Autheris provides its own decoupled semantic schema grounding for AI agents via the Semantic MCP Compiler (`F-AI-02`). Standard GraphQL schema introspection (`__schema`, `__type`) remains fully functional and is controlled separately via `Gateway:GraphQL:EnableIntrospection`.

---

## 7. Adding New Queries and Dynamic Tables

1. **Register Table in Governance Catalog**: Add entry in `TABLES` table with `CATALOG_NAME`, `SCHEMA_NAME`, and `TABLE_NAME`.
2. **Define Columns and Types**: Add column specifications in `TABLE_COLUMNS`.
3. **Configure Hot Chocolate Dynamic Type**: Handled automatically by `DynamicTableType` which inspects catalog metadata, applies scalar conversions, and hooks field masking.
4. **Grant Consent**: Ensure the calling SID has an active `ALLOW` consent for the table before querying, otherwise Zero Trust will return `FORBIDDEN`.

---

## 8. Testing Data Catalog Synchronization

You can test Data Catalog synchronization against Microsoft Purview, Collibra, Alation, or OpenMetadata locally:

```graphql
# Administrative Mutation (requires GovernanceAdmin or ClusterAdmin)
mutation RunCatalogSync {
  syncDataCatalog(dryRun: true) {
    success
    syncedTablesCount
    syncedColumnsCount
    maskedColumnsCount
    art9ProtectedTablesCount
    warnings
  }
}
```

In unit tests, mock `IDataCatalogClient` and assert that `DataCatalogSyncService` properly maps tags:
- `GdprArticle9Tags` -> Enforces `Table.Sensitivity = "HIGH"`, `RequiresFourEyes = true`, and `MaskingRule = REDACT`.
- `TagToMaskingRuleMap` -> Maps tags (e.g. `PII.Email`) to `MASK_EMAIL` or `HMAC_SHA256`.

---

## 9. Testing Lineage & GDPR Disclosure Queries

To analyze downstream dependencies before schema changes or produce GDPR Art. 15 reports:

```graphql
# 1. Downstream Lineage Impact
query CheckConsumers {
  tableConsumers(domain: "sales", schema: "dbo", tableName: "orders", timeWindowDays: 30) {
    breakingChangeRisk
    activeReadersCount
    downstreamConsumers {
      name
      type
      ownerTeam
      ownerEmail
    }
    recommendedMitigations
  }
}

# 2. GDPR Art. 15 Disclosure Report
query GetGdprDisclosure {
  gdprDataDisclosureReport(
    domain: "healthcare"
    schema: "dbo"
    tableName: "patient_diagnoses"
    timeWindowDays: 365
  ) {
    totalAccessEvents
    sensitivityCategories
    disclosedRecipients {
      recipientSid
      recipientCategory
      totalQueries
      accessedColumns
      maskingRuleApplied
    }
  }
}
```

---

## 10. Validating Casbin Governance Policies

Autheris includes a dedicated CLI linter tool for validating Casbin RBAC/ABAC models and policies:

```bash
# Run the Casbin policy linter
dotnet run --project tools/casbin-policy-lint/casbin-policy-lint.csproj
```

---

## 11. Testing In-Memory OLAP & Arrow Export (`F-DATA-03` / `F-DATA-04`)

Test fast vector analytical queries and Arrow Flight/IPC endpoints:

```bash
# 1. In-Memory DuckDB OLAP Query
curl -X POST https://localhost:7214/api/v1/olap/query \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <token>" \
  -d '{
    "sql": "SELECT region, count(*), sum(amount) FROM sales_records GROUP BY region",
    "tableNames": ["default.public.sales_records"]
  }'

# 2. Apache Arrow IPC Stream Export
curl -X GET "https://localhost:7214/api/v1/arrow/export/default.public.sales_records?batchSize=10000" \
  -H "Authorization: Bearer <token>" \
  --output sales_records.arrow
```


