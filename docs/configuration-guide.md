# Configuration Guide – GraphQL Enterprise Gateway

This guide is a complete, field-tested reference of all configuration options of the **GraphQL Enterprise Gateway**, including validation rules, security invariants, environment variables, Kubernetes configuration and profiles (development vs. production).

An automatically generated, complete reference of all bound configuration keys is available in the [Configuration Reference](reference/configuration.md).

---

## 1. Configuration Architecture

The configuration is based on the .NET 10 options pattern (`IOptions<GatewayOptions>`) with strict validation at application startup (**fail-fast** via `ValidateOnStart()`).

### 1.1 Hierarchy & Load Order
Configuration values are evaluated in the following priority (later sources override earlier ones):
1. **`appsettings.json`**: Base configuration for default settings.
2. **`appsettings.{Environment}.json`**: Environment-specific overrides (e.g. `appsettings.Development.json` or `appsettings.Production.json`).
3. **Environment variables**: Container and host configuration (syntax: double underscores `Gateway__Section__Property`).
4. **Secrets provider**: Key Vault / environment secret fallback for sensitive cryptographic keys.

### 1.2 Startup Validation & Security Invariants
When the host starts (`Program.cs`), `GatewayServiceCollectionExtensions.AddGatewayOptions` performs recursive DataAnnotation and semantic security checks. If a condition fails, the startup process aborts immediately with a `ValidationException`:

| Rule ID | Check | Failure condition |
| :--- | :--- | :--- |
| **NF-HA-01a** | `ShutdownTimeoutSeconds >= QueryTimeoutSeconds + 10` | Shutdown must have at least 10 seconds more buffer than the longest query timeout. |
| **NF-HA-01b** | `TerminationGracePeriodSeconds >= DrainDelaySeconds + ShutdownTimeoutSeconds + 10` | The kubelet grace period must cover the entire drain and shutdown cycle. |
| **NF-SEC-01** | `environment.IsDevelopment() \|\| !Authentication.EnableTestAuthHandler` | The `TestAuthHandler` (header-based SID impersonation) is **strictly forbidden** outside `Development`. |
| **NF-SEC-03** | Outside Development: `!string.IsNullOrWhiteSpace(HmacSecretKeyVaultRef)` and not equal to the test defaults | HMAC salts must come from a secure secret store in staging/production. |
| **NF-SEC-04** | Outside Development: BasicAuth users must use the salted PBKDF2 format (`$pbkdf2$...`) | Plaintext and unsalted SHA-256 passwords are forbidden in staging/production and are rejected at runtime with `401 Unauthorized`. |
| **R-DEP-2** | Outside Development: Secret keys (`DataMasking.HmacSecretKeyVaultRef`, `GovernanceDb.AuditHmacKeyVaultRef`, `Authentication.ForwardAuth.SharedSecret`) must be at least 32 bytes in UTF-8 representation | Cryptographic keys shorter than 32 UTF-8 bytes are rejected fail-closed during startup validation. |
| **R-API-1** | Outside Development: all `DANGER:` bypass flags strictly prohibited; ITSM webhooks are verified with per-instance secrets only | Any `DANGER:` flag causes an immediate `ValidationException` process startup crash outside `Development`. |

> [!IMPORTANT]
> **Secret Key Format and Requirements (R-DEP-2 & Arch 6)**:
> Startup and runtime validation checks the **UTF-8 byte count** of the resolved secret text string (`byteCount >= 32`).
> - **Entropy Warning**: A string of 32 hexadecimal characters (`[0-9a-fA-F]{32}`) occupies 32 ASCII/UTF-8 bytes, but represents only **16 bytes (128 bits) of cryptographic entropy**, which is insufficient for production HMAC master and audit keys.
> - **Recommended Practice**: Provide secrets as Base64-encoded strings representing at least **32 cryptographically random bytes** (256 bits of raw entropy, yielding >= 44 Base64 ASCII characters) or generate at least 32 raw random characters.

---

## 2. Complete Reference of the Configuration Sections (`Gateway:*`)

All gateway-specific options live under the root node `"Gateway"`. An automatically generated table of all options and their source files is available in the [Configuration Reference](reference/configuration.md).

### 2.1 `HighAvailability` (High Availability & Graceful Shutdown)

Controls traffic draining during rolling deployments and pod terminations.

| Property | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `DrainDelaySeconds` | `int` | `1 .. 30` | `5` | Wait time after the SIGTERM signal arrives before active connections are closed (lets the K8s endpoints controller propagate the unready state). |
| `QueryTimeoutSeconds` | `int` | `5 .. 120` | `30` | Maximum permitted execution time of a GraphQL request. |
| `ShutdownTimeoutSeconds` | `int` | `10 .. 180` | `40` | Maximum time granted to active requests to finish normally. |
| `TerminationGracePeriodSeconds` | `int` | `20 .. 300` | `60` | Kubelet pod grace period corridor that avoids an abrupt SIGKILL. |

The Helm chart renders these four keys as `Gateway__HighAvailability__*` from `gateway.drainDelaySeconds`, `gateway.shutdownTimeoutSeconds`, `gateway.queryTimeoutSeconds` and `terminationGracePeriodSeconds`. The chart default `gateway.queryTimeoutSeconds` is 20 (the application default is 30). The chart renders only values inside the ranges above and enforces the stricter NF-HA-01b `TerminationGracePeriodSeconds >= 2 * DrainDelaySeconds + ShutdownTimeoutSeconds + 10`, because the `preStop` sleep also consumes the drain time; the render fails otherwise. See [f-ops-02](features/f-ops-02-high-availability-kubernetes-helm.md) section 5.2.

```json
"HighAvailability": {
  "DrainDelaySeconds": 5,
  "QueryTimeoutSeconds": 30,
  "ShutdownTimeoutSeconds": 40,
  "TerminationGracePeriodSeconds": 60
}
```

---

### 2.2 `Authentication` (Multi-Protocol Identity, ForwardAuth & Kerberos)

The gateway supports a flexible, multi-track authentication concept with automatic protocol selection (**Smart Dynamic Scheme Selector**). It combines Kubernetes Ingress ForwardAuth, Microsoft Entra ID (Azure AD), AD FS, HTTP Basic Authentication and Windows Kerberos.

#### 2.2.1 Base Settings & Kerberos / Negotiate
| Property | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `Domain` | `string` | Valid FQDN | `"CORP.LOCAL"` | Active Directory domain. |
| `ServicePrincipalName` | `string` | SPN format | `"HTTP/gql-gateway.corp.local"` | Kerberos service principal name for SPNEGO/Negotiate. |
| `RequireKerberosOnly` | `bool` | `true \| false` | `true` | Enforces Kerberos: `NTLM` Authorization headers are not routed to Negotiate, and identities authenticated by Negotiate with an authentication type other than Kerberos (e.g. NTLM) are rejected. `PersistNtlmCredentials`/`PersistKerberosCredentials` are always disabled so credentials never carry over on shared upstream connections. |
| `EnableTestAuthHandler` | `bool` | `true \| false` | `false` | Enables the `X-Test-User-Sid` header for simulating identities (**allowed in Development only!**). |

#### 2.2.2 `Authentication.ForwardAuth` (Kubernetes / Traefik Ingress)
When the gateway runs in Kubernetes, authentication can be delegated to the upstream ingress controller (e.g. **Traefik Ingress**) via ForwardAuth (e.g. Authelia, Keycloak Gatekeeper, Authentik, OAuth2-Proxy). Traefik terminates SSL, checks the user session cookie or JWT and forwards the verified identity attributes via HTTP headers:

| Property | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `Enabled` | `bool` | `true \| false` | `false` | Enables the ForwardAuth authentication scheme. |
| `UserHeader` | `string` | Header name | `"X-Forwarded-User"` | Header carrying the user identifier (user name or user SID). |
| `EmailHeader` | `string` | Header name | `"X-Forwarded-Email"` | Header carrying the user's email address. |
| `GroupsHeader` | `string` | Header name | `"X-Forwarded-Groups"` | Comma-separated list of group SIDs or group names. |
| `RolesHeader` | `string` | Header name | `"X-Forwarded-Roles"` | Comma-separated list of roles (e.g. `GovernanceAdmin,DataOwner`). |
| `SharedSecretHeader` | `string` | Header name | `"X-Forwarded-Secret"` | Header for the pre-shared secret between ingress and gateway. |
| `SharedSecret` | `string` | Secret token | `""` | Pre-Shared Secret for ForwardAuth validation. Outside Development, mandatory if `SharedSecretKeyVaultRef` is unset, and must be at least 32 UTF-8 bytes long (`Encoding.UTF8.GetByteCount >= 32`). Recommended: Base64 string of >= 32 cryptographically random bytes (256 bits). |
| `SharedSecretKeyVaultRef` | `string` | Secret name | `""` | Name of the secret in Azure Key Vault / HashiCorp Vault. |
| `RequireTrustedProxy` | `bool` | `true \| false` | `true` | **Zero-Trust**: Requires that requests originate from an IP in `TrustedNetworks` or `TrustedProxies`. |
| `TrustedProxies` | `List<string>` | IP addresses | `[]` | Fixed IP addresses of the trusted Traefik pods / proxies. |
| `TrustedNetworks` | `List<string>` | CIDR blocks | `["127.0.0.1/32", "::1/128"]` | Allowed subnets (e.g. Kubernetes pod CIDR `"10.244.0.0/16"`). |

#### 2.2.3 `Authentication.BasicAuth` (HTTP Basic Authentication & Login API)
Enables direct authentication via `Authorization: Basic <base64>` for GraphQL queries as well as a dedicated endpoint `/api/auth/login`:

| Property | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `Enabled` | `bool` | `true \| false` | `false` | Enables HTTP Basic Auth and the login endpoint `/api/auth/login`. |
| `Users` | `List<BasicAuthUserConfig>` | Array | `[]` | List of configured user accounts. |
| `Users[].Username` | `string` | Text | `""` | Unique user name for Basic Auth. |
| `Users[].Password` | `string` | PBKDF2 / plaintext | `""` | In production: salted PBKDF2 string in the format `$pbkdf2$<iterations>$<saltBase64>$<hashBase64>`. Plaintext is allowed **in `Development` only**! |
| `Users[].PasswordHashSha256` | `string` | 64-hex SHA256 | `""` | Deprecated unsalted SHA-256 hash (allowed **in `Development` only**; forbidden in production). |
| `Users[].Roles` | `List<string>` | Role array | `[]` | Assigned roles (`DataConsumer`, `DataOwner`, `GovernanceAdmin`). |
| `Users[].UserSid` | `string` | SID format | `""` | Mapped Windows user SID (e.g. `S-1-5-21-CONSUMER-1`). |
| `Users[].GroupSids` | `List<string>` | SID array | `[]` | Mapped Windows group SIDs. |
| `SuccessCacheSeconds` | `int` | `>= 0` | `30` | Lifetime of a verified-credential marker; spares the password hash on repeated requests. `0` disables it. |
| `MarkerKeyRef` | `string?` | Secret reference | `null` | Secret (via the key vault secret provider) that keys the **cluster-shared** verified marker in Redis (HMAC bound to the credential version). Unset: the marker is per replica. See [F-AUTH-DX](features/f-auth-dx-basic-auth-session.md#behaviour-and-security). |

> [!IMPORTANT]
> **Security invariant for production**:
> - Outside the `Development` environment, unsalted SHA-256 hashes (`PasswordHashSha256`) and plaintext passwords (`Password`) are rejected without exception (`401 Unauthorized`).
> - Passwords must use the PBKDF2 format: `$pbkdf2$<iterations>$<salt>$<hash>` (e.g. `$pbkdf2$600000$c2FsdHNhbHQ=$...`).
> - **Timing attack protection**: If a requested user name does not exist, the gateway performs a dummy PBKDF2 computation with the same iteration count in the background, so attackers cannot enumerate valid user names via timing measurements. All hash comparisons use `CryptographicOperations.FixedTimeEquals`.

#### 2.2.4 `Authentication.EntraId` & `Authentication.Adfs` (JWT Bearer)
Supports modern OIDC/OAuth2 bearer tokens from Microsoft Entra ID (Azure AD) and Active Directory Federation Services (AD FS):

- **EntraId**:
  - `Enabled` (`bool`): Enables bearer validation against Microsoft Entra ID.
  - `Instance` (`string`, default: `"https://login.microsoftonline.com/"`): Entra ID login instance.
  - `TenantId` (`string`): Entra ID tenant ID (GUID).
  - `ClientId` (`string`): Application client ID.
  - `Audience` (`string`): Expected token audience (e.g. `"api://gql-gateway"`).
- **Adfs**:
  - `Enabled` (`bool`): Enables bearer validation against AD FS.
  - `MetadataAddress` (`string`): Federation metadata URL of AD FS.
  - `Audience` (`string`): Relying party identifier.
- **EnterpriseClaimsTransformation**:
  - Automatically normalizes Entra ID (`oid`, `preferred_username`, `groups` GUIDs/SIDs) and AD FS claims (`onprem_sid`, `primarysid`, `primarygroupsid`, `roles`) into the canonical `ClaimTypes.PrimarySid`, `ClaimTypes.GroupSid` and `ClaimTypes.Role`.

```json
"Authentication": {
  "Domain": "CORP.LOCAL",
  "ServicePrincipalName": "HTTP/gql-gateway.corp.local",
  "RequireKerberosOnly": true,
  "GroupCacheTtlMinutes": 5,
  "EnableTestAuthHandler": false,
  "ForwardAuth": {
    "Enabled": true,
    "UserHeader": "X-Forwarded-User",
    "GroupsHeader": "X-Forwarded-Groups",
    "RolesHeader": "X-Forwarded-Roles",
    "SharedSecretHeader": "X-Forwarded-Secret",
    "SharedSecretKeyVaultRef": "GQL-FORWARD-AUTH-SECRET",
    "RequireTrustedProxy": true,
    "TrustedNetworks": [
      "127.0.0.1/32",
      "::1/128",
      "10.244.0.0/16"
    ]
  },
  "BasicAuth": {
    "Enabled": true,
    "Users": [
      {
        "Username": "service-analyst",
        "Password": "$pbkdf2$600000$ZXhhbXBsZXNhbHQxMjM0NQ==$dGVzdGhhc2hiYXNlNjQ=",
        "Roles": ["DataConsumer"],
        "UserSid": "S-1-5-21-CONSUMER-1",
        "GroupSids": ["S-1-5-21-FINANCE-ANALYSTS"]
      }
    ]
  },
  "EntraId": {
    "Enabled": true,
    "TenantId": "72f988bf-86f1-41af-91ab-2d7cd011db47",
    "ClientId": "a820c78a-f326-4d1d-91b4-2195f1342618",
    "Audience": "api://gql-gateway"
  }
}
```

---

#### 2.2.5 `Authentication:Realms:Scim:MassChangeProtection` (SCIM mass-deprovisioning guard)
Throttles SCIM-originated user deprovisioning (PUT/PATCH `active=false` and DELETE on `/scim/v2/{realm}/Users`) so a compromised or buggy IdP token cannot deactivate every user at once. The guard counts distinct users locked by SCIM per tenant and realm within the window, using the shared lock table (cluster-wide). Admin and manual locks are never counted or blocked. A repeat on an already-locked user is neither counted again nor blocked. Group deletes are not counted.

| Key | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `Enabled` | `bool` | `true \| false` | `true` | `false` is an explicit opt-out and logs a startup warning. |
| `MaxDestructiveOperations` | `int` | 1 - 100000 | `100` | Distinct users SCIM may lock per realm within the window. |
| `WindowMinutes` | `int` | 1 - 1440 | `60` | Sliding window length. |

Out-of-range values fail startup. Above the limit the endpoint answers `429` with a fixed SCIM error body and a `Retry-After` header (seconds until the oldest counted lock leaves the window); nothing is changed for the blocked user. The first block per realm and window writes the required audit event `SCIM_MASS_CHANGE_BLOCKED` (tenant, realm, count, limit, window only) and one warning log line. If the count query fails, the SCIM operation fails (fail closed); admin deprovisioning is unaffected. Concurrent nodes may overshoot the limit by at most the number of in-flight requests.

### 2.3 `GovernanceDb` (Catalog & Consent Database)

Storage for metadata, grants, delegations, four-eyes approvals and audit logs.

| Property | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `Provider` | `string` | `"PostgreSql"`, `"SqlServer"` | `"PostgreSql"` | Database driver for governance storage. Any other value (including `"Sqlite"`) makes startup fail (see `docs/operations/migration-simplification.md`). |
| `ConnectionString` | `string` | ADO.NET ConnStr | `"Data Source=governance.db;Cache=Shared"` | Connection string to the governance DB. |
| `CommandTimeoutSeconds` | `int` | `1 .. 60` | `15` | Timeout for governance SQL statements. |
| `EnableOutboxProcessor` | `bool` | `true \| false` | `true` | Starts the asynchronous outbox worker for invalidation events. |
| `SeedDemoData` | `bool` | `true \| false` | `true` | Initializes demo tables and default governance rules when the DB is empty. |
| `MigrationConnectionString` | `string` | ADO.NET ConnStr | unset | PostgreSQL / SQL Server only: separate role that runs the schema migrations (DDL). The runtime role then needs no DDL rights. |
| `Migrations:Mode` | `string` | `Apply`, `Verify`, `Skip` | `Apply` | How the versioned schema migrations are handled at startup (see below). `Skip` is rejected outside Development. |
| `Migrations:AllowNewerSchema` | `bool` | `true \| false` | `false` | When `false`, startup fails with `GOVERNANCE_SCHEMA_NEWER_THAN_BUILD` if the database holds migrations newer than the running build. When `true` (rollback aid), that one check is skipped with a warning per start (`GOVERNANCE_SCHEMA_NEWER_ALLOWED`); the checksums, history and shape of every step the build knows are still verified and no DDL runs. |
| `Migrations:LockTimeoutSeconds` | `int` | `10 .. 3600` | `300` | Upper bound for opening the migration connection and acquiring the cross-process schema lock. Exceeding it fails startup with `GOVERNANCE_MIGRATION_LOCK_TIMEOUT`. |
| `Migrations:StepLockTimeoutSeconds` | `int` | `1 .. 3600` | `30` | PostgreSQL `lock_timeout` / SQL Server `LOCK_TIMEOUT` inside one migration step (a timeout rolls the step back). |
| `Migrations:StepStatementTimeoutSeconds` | `int` | `10 .. 86400` | `900` | PostgreSQL `statement_timeout` inside one migration step. |
| `Migrations:RequireLeastPrivilege` | `bool` | `true \| false` | `false` | PostgreSQL / SQL Server: in `Mode=Verify` the runtime login is checked for write access to the migration history and DDL rights. `false` logs `GOVERNANCE_RUNTIME_LOGIN_PRIVILEGED`; `true` fails startup. |

**Schema migrations.** The governance schema is created and evolved by ordered migrations recorded in the table `autheris_schema_migrations` (`version`, `name`, `checksum`, `applied_at`, `applied_by`). Repository constructors run no DDL. A hosted service that is registered before every other hosted service runs the migrations once at startup, holding the existing cross-replica lock (PostgreSQL `pg_advisory_lock`, SQL Server `sp_getapplock`, SQLite `BEGIN IMMEDIATE`), and then initializes the store (audit chain tail and anchor verification, optional demo seed, audit processor start). Migration `0001` (`baseline_governance_schema`) is the frozen v1.5.0 baseline of the complete governance schema (including `WEBHOOK_OUTBOX`, `autheris_cdc_watermarks`, and `CLOUDEVENT_SUBSCRIPTIONS`); every later schema change is a new ordered, forward-only step (`0002`, `0003`, ...) with a checksum over its SQL. v1.5.0 is the oldest supported base: databases created by a release older than v1.5.0 are not upgraded (they fail with `GOVERNANCE_SCHEMA_UNSUPPORTED_BASE` before any statement runs). Startup fails closed, with a governed error code, when a migration cannot be applied, when the history or a stored checksum differs from the build, when the database is newer than the build (unless `AllowNewerSchema`), when the schema shape drifts, or when the append-only audit trigger or truncate guard is missing or disabled after a step or the run (PostgreSQL, SQL Server).

- `Apply` (default): applies pending migrations automatically at startup. Without `MigrationConnectionString` the runtime login runs the DDL; in `Production` on PostgreSQL / SQL Server this logs `GOVERNANCE_MIGRATION_RUNTIME_DDL` once per start (and records a best-effort audit event after a successful apply). A login without DDL rights fails with `GOVERNANCE_MIGRATION_STEP_FAILED`: set `MigrationConnectionString` or use `--migrate-governance` plus `Mode=Verify`.
- `Verify`: changes nothing (no DDL, no write, no lock) and fails startup with `GOVERNANCE_SCHEMA_PENDING` while migrations are pending, or when the history, the schema shape or the audit trigger post-conditions do not match the build. Use it for replicas whose schema is migrated by a separate pipeline or init job, and for least-privilege runtime logins.
- `Skip`: neither inspects nor changes the schema (Development only).

The command-line jobs ignore `Migrations:Mode`: `Autheris.Api --migrate-governance` always applies (and logs that the mode is ignored; it uses `MigrationConnectionString` when set), `Autheris.Api --verify-governance` only verifies like `Mode=Verify`. Both exit `0` on success and `1` on failure and print the error code to stderr.

Per-step checks: before the first pending step the actual schema must equal the expected shape of the highest applied version; inside each step the shape of that version (including column nullability) is verified before its history row is written, so a step whose result differs from its declared shape rolls back. Exactly one process migrates at a time (PostgreSQL advisory lock, SQL Server application lock, SQLite write transaction); the others wait up to `LockTimeoutSeconds`, then re-read the history and only verify. Error codes:

PostgreSQL 16 or later is required for the governance store: step `0031_access_profile_items` validates stored JSON with `IS JSON`, which older versions reject. The step also needs every node to run the new build first (see the migration guide).

| Code | Meaning |
| :--- | :--- |
| `GOVERNANCE_SCHEMA_PENDING` | `Verify`: steps are pending. Run the migration job or start in `Apply`. |
| `GOVERNANCE_SCHEMA_UNSUPPORTED_BASE` | The database is older than the v1.5.0 baseline (or foreign). Nothing was executed. |
| `GOVERNANCE_SCHEMA_DRIFT` | The schema differs from the expected shape of the applied (or just executed) version. |
| `GOVERNANCE_SCHEMA_CHECKSUM_MISMATCH` | A recorded checksum differs from the build (edited step or edited history row). |
| `GOVERNANCE_SCHEMA_HISTORY_GAP` / `_HISTORY_DUPLICATE` / `_HISTORY_RENAMED` | The history is not exactly `1..k`, has duplicate versions, or a name differs from the build. |
| `GOVERNANCE_SCHEMA_NEWER_THAN_BUILD` | The database holds steps the build does not know (older binary, rollback). |
| `GOVERNANCE_MIGRATION_STEP_FAILED` | A step failed (database error number or SQLSTATE only, no SQL text or row values) and was rolled back. |
| `GOVERNANCE_MIGRATION_LOCK_TIMEOUT` / `_LOCK_FAILED` | The schema lock was not acquired within `LockTimeoutSeconds` / could not be taken. |
| `GOVERNANCE_SCHEMA_NEWER_ALLOWED` (warning) | `AllowNewerSchema` is `true` and the database is newer. |
| `GOVERNANCE_MIGRATION_RUNTIME_DDL` (warning) | Production, `Apply`, no `MigrationConnectionString`. |
| `GOVERNANCE_RUNTIME_LOGIN_PRIVILEGED` (warning, failure with `RequireLeastPrivilege`) | `Verify`: the runtime login can write the migration history or run DDL. |

In `Apply` and `Verify` mode the actual schema is compared with the shape that the build expects; a deviation fails startup with a drift report (see [Operations Runbook, 4.ac](operations-runbook.md)). See [F-GOV-12](features/f-gov-12-multi-engine-governance-storage.md#versioned-schema-migrations) and the grants in [F-GOV-21](features/f-gov-21-declared-table-keys-and-read-ordering.md#62-governance-database).

Run the migrations as a one-off job with `Autheris.Api --migrate-governance`: it applies (or verifies, per `Migrations:Mode`) the migrations and exits with code `0` on success and `1` on failure, without serving requests.

```json
"GovernanceDb": {
  "Provider": "PostgreSql",
  "ConnectionString": "Host=postgres.internal;Port=5432;Database=autheris_governance;Username=autheris;SSL Mode=VerifyFull",
  "CommandTimeoutSeconds": 15,
  "EnableOutboxProcessor": true,
  "SeedDemoData": false
}
```

**SQL Server as governance DB:** `Provider = "SqlServer"` (aliases `MsSql`, `SqlServer`) creates the schema itself at startup (idempotent, serialized via `sp_getapplock`) and, like PostgreSQL, is permitted for cluster / multi-replica operation. Example:

```json
"GovernanceDb": {
  "Provider": "SqlServer",
  "ConnectionString": "Server=sql.corp.local;Database=autheris_governance;User Id=autheris_app;Password=<secret>;Encrypt=Mandatory;TrustServerCertificate=false",
  "MigrationConnectionString": "Server=sql.corp.local;Database=autheris_governance;User Id=autheris_migrator;Password=<secret>;Encrypt=Mandatory"
}
```

- Outside Development, the TLS policy requires `Encrypt=Mandatory` (or `Strict`) and `TrustServerCertificate=false`.
- `MigrationConnectionString` (optional) runs the DDL with a separate login; the runtime login then only needs `SELECT`/`INSERT`/`UPDATE`/`DELETE` (on `AUDIT_LOG_ENTRIES` only `SELECT`/`INSERT`).
- The audit log is append-only: an `INSTEAD OF` trigger blocks `UPDATE`/`DELETE`, and an empty guard table with a foreign key blocks `TRUNCATE`. A table owner can remove both, so the runtime login must not have DDL rights.
- Integration tests: `SqlServer*ContractTests` run against `AUTHERIS_TEST_MSSQL` (connection string of a server on which the login may create databases) or via Testcontainers (Docker).

---

### 2.4 `DataSources` (Backend Business Databases & RLS Pushdown)

Configures real relational database backends for the queried business data. Via `ISqlConnectionFactory` the gateway supports the providers `"SqlServer"`, `"PostgreSql"` and `"Sqlite"`. See [source-least-privilege-roles.md](operations/source-least-privilege-roles.md) for least-privilege source database role templates.

SQL table reads (GraphQL, OData, MCP, connectors) run through the governed SQL pipeline (`SqlTableReadExecutor`, the AST compiler and the dialect binder): the compiler **applies the tenant filter, the row-level security policy and the column masks inside the statement sent to the database**. There is no synthetic-data fallback:

| Property | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `DataSources:Connections:{sourceName}:Provider` | `string` | `"SqlServer"`, `"PostgreSql"`, `"Sqlite"` | `"Sqlite"` | Database driver for the target data source. |
| `DataSources:Connections:{sourceName}:ConnectionString` | `string` | ADO.NET ConnStr | `""` | Connection string to the target database. DEP-7: outside Development, startup requires `SSL Mode=VerifyFull` (or `VerifyCA`) for PostgreSQL and `Encrypt=Mandatory`/`Strict` without `TrustServerCertificate=true` for SQL Server. |
| `DataSources:RequireTenantColumn` | `bool` | `true`, `false` | `false` | Review E-5: when `true`, a table without a tenant column (`tenant_id`, `TenantId`, ...) is refused fail-closed instead of being read unscoped. |
| `DataSources:TenantColumnExemptTables` | `string[]` | `schema.table` or `table` | `[]` | Tables that are deliberately shared across tenants and therefore exempt from `RequireTenantColumn`. For WebSQL DML an entry exempts a table only by its exact `schema.table` name (case-insensitive); a bare table name or a suffix never matches, and DML on any other table without a tenant column is refused. |

```json
"DataSources": {
  "Connections": {
    "finance": {
      "Provider": "SqlServer",
      "ConnectionString": "Server=sql-finance.corp.local;Database=FinanceDb;Integrated Security=SSPI;Encrypt=Mandatory;TrustServerCertificate=false;"
    },
    "hr": {
      "Provider": "PostgreSql",
      "ConnectionString": "Host=pg-hr.corp.local;Port=5432;Database=HrDb;Username=gql_app;Password=<secret>;SSL Mode=VerifyFull;"
    }
  }
}
```

#### 2.4.1 SQL Server Windows Authentication (NTLM / Kerberos) in Linux Containers

When connecting from a Linux container (`mcr.microsoft.com/dotnet/aspnet:11.0`) to an on-premises SQL Server that requires Active Directory Windows Authentication (`Integrated Security=true` / `Integrated Security=SSPI`), `Microsoft.Data.SqlClient` delegates authentication to GSSAPI (`libgssapi-krb5-2`) and the NTLM GSSAPI plugin (`gss-ntlmssp`).

The official Autheris container image (`Dockerfile`) pre-installs `libgssapi-krb5-2`, `gss-ntlmssp`, `krb5-user`, and `libkrb5-3`, registers `/etc/gss/mech.d/mech.ntlmssp.conf` (and `/etc/gss/mech`) with read permissions for the non-root container user (`10001:10001`), and provides `/app/data/gss` (`0700`) for runtime GSS state.

> [!CAUTION]
> **Active Directory Account Lockout Warning:**
> - **Never** pass a Windows domain account (`DOMAIN\user`) via `User ID=...;Password=...` without `Integrated Security=true`! `Microsoft.Data.SqlClient` sends `User ID` as a SQL Server login, which fails for AD accounts and increments the Active Directory bad-password counter until the service account is locked out.
> - Autheris detects and rejects domain-qualified `User ID` values without `Integrated Security=true` both at startup (`GatewayStartupValidator.ValidateSqlServerWindowsAuth`) and before opening any connection (`SqlServerWindowsAuthConfigurator.PrepareConnection`), **without attempting a network login**.

##### Option A: Per-Datasource `WindowsAuth` Configuration with Secret Reference (Recommended)

Configure `DataSources:Connections:<name>:WindowsAuth` with `Domain`, `Username`, and `PasswordRef` (pointing to a mounted container secret `file:/run/secrets/...` or Key Vault secret). Autheris automatically enforces `Integrated Security=true`, resolves the secret, materializes a `0600`-restricted `NTLM_USER_FILE` in `/app/data/gss/ntlm_users`, and sets `NTLM_USER_FILE`, `NTLMUSER`, and `NETBIOS_DOMAIN_NAME`:

Optional per data source: `WindowsAuth.UseManagedNtlm` (`true` managed NTLM, `false` gss-ntlmssp; unset follows `DOTNET_System_Net_Security_UseManagedNtlm`, default managed) and `LoginFailureCooldownSeconds` (default 300, `0` disables the login circuit breaker). See F-AUTH-04 sections 3.3 and 3.7.

```json
"DataSources": {
  "Connections": {
    "LweBi": {
      "Provider": "SqlServer",
      "ConnectionString": "Server=10.160.2.94,1433;Database=Reporting;Integrated Security=true;Encrypt=True;TrustServerCertificate=True;",
      "WindowsAuth": {
        "Mode": "Ntlm",
        "Domain": "LWEW2K",
        "Username": "lwesvcbidbreportread",
        "PasswordRef": "file:/run/secrets/lwe_bi_password"
      }
    }
  }
}
```

##### Option B: Pre-Mounted `NTLM_USER_FILE` (`DOMAIN:USER:PASSWORD`)

Mount a credentials file with permissions `0400` or `0600` owned by `10001:10001` where each line follows the `gss-ntlmssp` format `DOMAIN:USER:PASSWORD`:

```text
LWEW2K:lwesvcbidbreportread:<PASSWORD>
```

Docker Compose example:

```yaml
services:
  autheris:
    image: ghcr.io/themulle/autheris:latest
    environment:
      NTLM_USER_FILE: /run/secrets/ntlm_users
      Gateway__DataSources__Connections__LweBi__Provider: SqlServer
      Gateway__DataSources__Connections__LweBi__ConnectionString: "Server=10.160.2.94,1433;Database=Reporting;Integrated Security=true;Encrypt=True;TrustServerCertificate=True;"
      Gateway__DataSources__Connections__LweBi__WindowsAuth__Mode: Ntlm
      Gateway__DataSources__Connections__LweBi__WindowsAuth__NtlmUserFilePath: /run/secrets/ntlm_users
    secrets:
      - source: ntlm_users
        target: /run/secrets/ntlm_users
        uid: "10001"
        gid: "10001"
        mode: 0400

secrets:
  ntlm_users:
    file: ./secrets/ntlm_users
```

##### Option C: Kerberos (`krb5.conf` + Keytab)

When a Kerberos KDC is reachable and the SQL Server SPN (`MSSQLSvc/<fqdn>:1433@REALM`) is registered in Active Directory:

```json
"DataSources": {
  "Connections": {
    "LweBi": {
      "Provider": "SqlServer",
      "ConnectionString": "Server=sql-prod.lwe.local,1433;Database=Reporting;Integrated Security=true;Encrypt=Mandatory;TrustServerCertificate=false;",
      "WindowsAuth": {
        "Mode": "Kerberos",
        "KerberosConfigPath": "/etc/krb5.conf",
        "KerberosKeytabPath": "/run/secrets/sql_client.keytab",
        "ServicePrincipalName": "MSSQLSvc/sql-prod.lwe.local:1433@LWE.LOCAL"
      }
    }
  }
}
```

---

### 2.5 `Caching` (Two-Tier Caching & Multi-Instance Redis Clustering)

Controls the L1 in-memory cache, L2 Redis and the consistency check.

#### `Caching.L1MemoryCache`
| Property | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `SizeLimitMb` | `int` | `16 .. 4096` | `512` | Maximum in-memory cache size in megabytes. |
| `DefaultTtlMinutes` | `int` | `1 .. 120` | `10` | Default validity of cached consent decisions. |

#### `Caching.Redis`
| Property | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `Configuration` | `string` | StackExchange.Redis ConnStr | `"localhost:6379,abortConnect=false"` | Redis host and connection parameters. |
| `InstanceName` | `string` | Prefix | `"Autheris:"` | Key prefix for multi-gateway clusters. |
| `InvalidationChannel` | `string` | Channel name | `"consent:invalidations"` | Redis pub/sub channel for epoch changes. |
| `ConnectTimeoutMs` | `int` | `100 .. 10000` | `2000` | Connection timeout in milliseconds. |
| `SyncTimeoutMs` | `int` | `100 .. 10000` | `1000` | Synchronous read/write timeout in ms. |

#### `Caching.EpochValidation`
| Property | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `FailClosedOnSensitiveTables` | `bool` | `true \| false` | `true` | Zero-Trust: If the epoch store is unreachable, access to sensitive tables is blocked. |
| `DegradedMaxStalenessSeconds` | `int` | `1 .. 300` | `30` | Maximum staleness for non-critical tables during a Redis outage. |
| `PipelinedMGetEnabled` | `bool` | `true \| false` | `true` | Reads the epochs of several tables as concurrent single-key `GET`s pipelined over one connection (no multi-key `MGET`, so it works on Redis Cluster without `CROSSSLOT`). `false` reads them one after another. |
| `LocalStalenessBudgetMilliseconds` | `int` | `0 .. 250` | `150` | How long a replica may reuse an epoch read from Redis for **non-sensitive** tables of Autheris-enforced sources. Always 0 for highly sensitive tables and delegated sources; `0` disables the budget. See [F-OPS-02, section 4.5](features/f-ops-02-high-availability-kubernetes-helm.md#45-replica-aware-request-features). |
| `MaxLocalEpochEntries` | `int` | — | `50000` | Upper bound of locally cached epochs for the staleness budget. |

```json
"Caching": {
  "L1MemoryCache": {
    "SizeLimitMb": 512,
    "DefaultTtlMinutes": 10
  },
  "Redis": {
    "Configuration": "redis-cluster.corp.local:6379,abortConnect=false,ssl=true",
    "InstanceName": "AutherisProd:",
    "InvalidationChannel": "consent:invalidations",
    "ConnectTimeoutMs": 2000,
    "SyncTimeoutMs": 1000
  },
  "EpochValidation": {
    "FailClosedOnSensitiveTables": true,
    "DegradedMaxStalenessSeconds": 30,
    "PipelinedMGetEnabled": true,
    "LocalStalenessBudgetMilliseconds": 150
  }
}
```

---

### 2.5 `RateLimiting` (DDoS & Abuse Protection)

Combines pre-authentication IP limiting with token-bucket consumption per Windows user SID.

#### `RateLimiting.PreAuthIpRateLimit`
| Property | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `PermitLimit` | `int` | `1 .. 100000` | `100` | Maximum allowed requests per IP address within the window. |
| `WindowSeconds` | `int` | `1 .. 3600` | `60` | Time window in seconds for the IP rate limit. |
| `QueueLimit` | `int` | `>= 0` | `0` | Queue size before rejection with `HTTP 429`. |

#### `RateLimiting.PostAuthSidRateLimit`
| Property | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `TokenBucketCapacity` | `int` | `10 .. 100000` | `500` | Maximum token capacity per authenticated user SID. |
| `TokensPerSecond` | `int` | `1 .. 10000` | `50` | Refill rate of the token bucket per second. |

#### `RateLimiting.ClientTiers` (GraphQL Pre-Execution Cost & Quota Defense)

Controls the [`CostAndQuotaMiddleware`](../src/Autheris.GraphQL/Interceptors/CostAndQuotaMiddleware.cs) ([`F-PERF-13`](features/f-perf-13-cost-and-quota-rate-limiting.md)) for the dynamic assignment of callers to quota tiers (`Free`, `Standard`, `Enterprise`, `Internal`) before GraphQL execution.

| Property | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `RoleTierMappings` | `Dictionary<string, string>` | `{}` | Maps user roles (claims) to tiers (e.g. `"FinanceAdmins": "Enterprise"`). |
| `ApiKeys` | `Dictionary<string, string>` | `{}` | Maps registered API keys (`X-API-Key`) to tiers. |
| `TierLimits` | `Dictionary<string, ClientTierLimitOverride>` | Default values per tier | Allows overriding the default limits per tier. |

##### `ClientTierLimitOverride` Parameter
| Property | Type | Default (Standard / Enterprise) | Description |
| :--- | :--- | :--- | :--- |
| `MaxCostPerQuery` | `int?` | `250` / `1000` | Maximum permitted static AST cost points per single query (fail-fast on overrun with `QUERY_COST_QUOTA_EXCEEDED`). |
| `MaxComplexityDepth` | `int?` | `10` / `20` | Maximum nesting depth of the GraphQL selection. |
| `MaxTokensCapacity` | `int?` | `1000` / `10000` | Maximum capacity of the distributed token bucket. |
| `TokenRefillRatePerSecond` | `double?` | `20.0` / `200.0` | Refill rate of cost points per second. |
| `ExposeCostExtensions` | `bool?` | `true` | Enables returning `extensions.cost` in the GraphQL response. |

```json
"RateLimiting": {
  "PreAuthIpRateLimit": {
    "PermitLimit": 100,
    "WindowSeconds": 60,
    "QueueLimit": 0
  },
  "PostAuthSidRateLimit": {
    "TokenBucketCapacity": 500,
    "TokensPerSecond": 50
  },
  "ClientTiers": {
    "RoleTierMappings": {
      "FinanceSuperUser": "Enterprise",
      "AnalyticsService": "Standard"
    },
    "ApiKeys": {
      "ak_live_partner_abc123": "Enterprise"
    },
    "TierLimits": {
      "Standard": {
        "MaxCostPerQuery": 300,
        "MaxTokensCapacity": 2000,
        "TokenRefillRatePerSecond": 30.0
      }
    }
  }
}
```

> [!TIP]
> **Resilience & High Availability (Failover)**:
> In multi-pod environments `RedisRateLimiterService` uses Redis for cross-instance rate limiting. Should the Redis cluster fail or report connection problems, the gateway switches **automatically and transparently** to the local `InMemoryRateLimiterService`. Requests are not blocked, and the gateway stays protected against DoS attacks.
> 
> **Performance on hot paths**:
> The local `InMemoryRateLimiterService` maintains IP and bucket counters via atomic `Interlocked.Increment` / `Interlocked.Decrement` operations to completely avoid deadlocks and lock contention on `ConcurrentDictionary.Count`.


---

### 2.6 `GraphQL` (Engine & Query Protection)

Controls Hot Chocolate 16 parameters, complexity limits and anti-CSRF checks.

| Property | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `EndpointPath` | `string` | URL path | `"/graphql"` | Relative endpoint path for GraphQL queries. |
| `MaxAllowedExecutionDepth` | `int` | `1 .. 25` | `10` | Maximum query depth to prevent cyclic DoS queries. |
| `MaxAllowedComplexity` | `int` | `100 .. 10000` | `1500` | Maximum static query cost budget. |
| `Subscriptions:Enabled` | `bool` | `true \| false` | `false` | Enables the GraphQL subscription root type together with the in-memory provider (start-time setting, restart required). Only when `true` are the root type and the in-memory provider registered; when `false` the schema contains no `Subscription` type and subscription documents fail validation. The WebSocket transport for queries and mutations stays authenticated. Read-only tokens may never execute subscriptions. |
| `EnableIntrospection` | `bool` | `true \| false` | `false` | Schema introspection (`__schema`). Disable in production! |
| `PersistedQueriesOnly` | `bool` | `true \| false` | `false` | Allows only pre-registered hash-based queries. |
| `EnableBananaCakePop` | `bool` | `true \| false` | `false` | Nitro Banana Cake Pop GraphQL IDE in the browser. Disable in production! |
| `MaxResponseRows` | `int` | `100 .. 100000` | `5000` | Maximum permitted row count in the response budget. |
| `MaxResponseBytes` | `long` | `1 MB .. 100 MB` | `10485760` (10 MB) | Maximum byte budget for GraphQL responses. |
| `TrustedOrigins` | `List<string>` | URLs / `"*"` | `[]` | CORS and anti-CSRF allowlist for `Origin`/`Referer` headers. |
| `TrustedDocumentsDirectory` | `string` | Directory path | `""` | Global allowlist of `*.graphql` / `*.gql` documents (hashed over the normalized document). Required and must exist when `PersistedQueriesOnly=true`, unless `AppScopedTrustedDocuments.Enabled=true` (then it may be empty). |
| `AppScopedTrustedDocuments.Enabled` | `bool` | `true \| false` | `false` | Runtime-managed, app-scoped trusted documents (see below). Requires `PersistedQueriesOnly=true`; startup fails otherwise. |
| `AppScopedTrustedDocuments.HeaderName` | `string` | HTTP token | `"X-Autheris-App"` | Request header that carries the app identity `<appId>/<version>`. |
| `AppScopedTrustedDocuments.MaxHashesPerVersion` | `int` | `1 .. 5000` | `500` | Maximum document hashes of one app version. |
| `AppScopedTrustedDocuments.MaxActiveVersionsPerApp` | `int` | `1 .. 200` | `20` | Maximum active versions of one app. |
| `AppScopedTrustedDocuments.MaxAllowedOriginsPerApp` | `int` | `1 .. 50` | `10` | Maximum allowed origins of one app. |
| `AppScopedTrustedDocuments.CacheRefreshSeconds` | `int` | `1 .. 60` | `5` | Interval in which each instance checks the store for changes made by other instances. |

```json
"GraphQL": {
  "EndpointPath": "/graphql",
  "MaxAllowedExecutionDepth": 10,
  "MaxAllowedComplexity": 1500,
  "EnableIntrospection": false,
  "PersistedQueriesOnly": false,
  "EnableBananaCakePop": false,
  "MaxResponseRows": 5000,
  "MaxResponseBytes": 10485760,
  "TrustedOrigins": [
    "https://portal.corp.local",
    "https://analytics.corp.local"
  ]
}
```

#### App-scoped trusted documents

With `GraphQL:AppScopedTrustedDocuments:Enabled=true` an operator registers, per app version, the exact documents that app may run, without restarting the gateway. A request that carries the header `X-Autheris-App: <appId>/<version>` (`appId` `[a-z0-9][a-z0-9._-]{0,63}`, version `[A-Za-z0-9][A-Za-z0-9._+-]{0,63}`) is checked only against that app version:

- The document is identified by the SHA-256 of its exact source text (UTF-8 bytes, no normalization): any whitespace change is a different document. Requests with only a document id, an APQ `persistedQuery` extension, a client-supplied document hash or no source text are rejected.
- The request must carry exactly one `Origin` header that equals one of the origins registered for the app (scheme and host lower-cased, default port elided). A missing, `null` or duplicated `Origin` is rejected, also for server-to-server clients and for the WebSocket upgrade request.
- A request with the header never falls back to the global directory. Requests without the header use the global `TrustedDocumentsDirectory` as before. With the feature disabled the header is ignored.
- Every rejection returns the same `TRUSTED_DOCUMENT_REQUIRED` error; the reason is only logged at Debug level (reason code, app id and version, never document text).
- Multi-instance coherence: a write is effective on the instance that handled it before the call returns; other instances pick it up within `CacheRefreshSeconds` (default 5 s), so a retired version can stay usable on other instances for up to that interval. If the store cannot be read, requests with the app header are rejected.
- Apps are gateway-wide (not tenant-scoped). Allowed origins are app-level: a PUT for any version replaces the origins of all versions of that app.

The admin API is mapped only while the feature is enabled and is meant to be reached from inside the cluster. The Helm chart's ingress denies the `/api/admin` prefix by default (`ingress.exposeAdmin: false`): the prefix is routed to an endpoint-less Service, so the controller answers 503 instead of forwarding it through the catch-all `/` path. Only the boolean `true` exposes it (any other value keeps the deny); set it only on an internal-only ingress and keep the NetworkPolicy.

- nginx (`ingress.className: nginx`): the chart adds `nginx.ingress.kubernetes.io/use-regex: "true"` so the deny also matches `/API/Admin`, `/api//admin`, `/api%2Fadmin` and a trailing slash (nginx matches the normalized URI). `use-regex` applies to the whole host: every Ingress object that shares a host with this one becomes case-insensitive regex, and all paths of this Ingress are treated as regex (a configured path with a regex metacharacter fails the render). The deny prefix `^/api/admin` also blocks `/api/administrator`. A user annotation cannot switch `use-regex` off while the deny is rendered. `ingress.nginxCaseInsensitiveAdminDeny: false` opts out: the deny then stays a case-sensitive prefix and mixed-case variants are blocked only by the application. The chart never renders snippet annotations.
- Other controllers (for example Traefik): path prefix matching is case-sensitive, so `/API/ADMIN` is forwarded to the gateway; encoded-slash handling is controller- and version-specific. Also block `/api/admin` case-insensitively at the public edge.
- The ingress does not cover `merge_slashes off` or operator snippets, hosts not listed in `ingress.hosts`, or any path that bypasses this Ingress (LoadBalancer or NodePort Service, port-forward, Gateway API, other Ingress objects). The application authorization is the actual control: the routes require the `ClusterAdmin` role and reject unauthenticated callers regardless of path case, trailing slash, encoded slash or double slash.

The admin API requires the `ClusterAdmin` role plus a delegated scope; every change is audited (`TRUSTED_DOCUMENTS_REPLACED`, `TRUSTED_DOCUMENTS_RETIRED`):

| Method and path | Scope | Result |
| :--- | :--- | :--- |
| `PUT /api/admin/trusted-documents/{appId}/{version}` body `{"hashes":[...],"allowedOrigins":[...]}` | `trusted-documents:write` | `200 {added, removed, unchanged}`; replaces the hash set and origins of the version; `400` invalid input, `409` retired version or version limit, `413` body over 1 MiB |
| `DELETE /api/admin/trusted-documents/{appId}/{version}` | `trusted-documents:write` | `204` (also when already retired), `404` unknown version; retired versions are never reactivated |
| `GET /api/admin/trusted-documents/{appId}` | `trusted-documents:read` or `write` | `200` origins and versions with status and hash count, `404` unknown app |

> [!IMPORTANT]
> **Anti-CSRF preflight protection**: Every incoming POST or GET request to `/graphql` requires the header `GraphQL-Preflight: 1` or `X-Requested-With`. Browser requests without this header are rejected with `HTTP 400 Bad Request`.

---

### 2.7 `DataMasking` (Cryptographic Pseudonymization & Masking)

Configures deterministic pseudonymization (`HMAC_SHA256`) and masking caches.

| Property | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `HmacKeyId` | `string` | Identifier | `"key-2026-q1"` | Key ID to support key rotations. |
| `HmacSecretKeyVaultRef` | `string` | Secret-Name | `"DEV_INSECURE_...` | Name of the secret in Azure Key Vault / HashiCorp Vault or environment fallback. Outside Development, must resolve to at least 32 UTF-8 bytes. |
| `MaskingCacheTtlHours` | `int` | `1 .. 168` | `24` | Validity period of the cache for precomputed masking rules. |

> [!NOTE]
> **Key Entropy & Length (R-DEP-2)**: The HMAC masking master key must contain >= 32 UTF-8 bytes outside Development. 32 hexadecimal characters encode only 16 bytes of entropy and are only 32 ASCII bytes, which could be weak. Recommended practice is a Base64-encoded string derived from at least 32 cryptographically secure random bytes (256 bits of entropy).

```json
"DataMasking": {
  "HmacKeyId": "key-2026-q1",
  "HmacSecretKeyVaultRef": "GQL-GATEWAY-HMAC-SECRET-KEY",
  "MaskingCacheTtlHours": 24
}
```

---

### 2.8 `Audit` (Tamper-Proof HMAC-SHA256 Audit Hash Chain)

Logs data accesses tamper-proof in a cryptographically chained HMAC-SHA256 audit chain (`AUDIT_LOG_ENTRIES`). By using a secret HMAC key (from Key Vault or environment), the chain cannot be modified unnoticed even with direct write access to the relational governance DB.

| Property | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `TierBAggregationWindowSeconds` | `int` | `1 .. 3600` | `60` | Aggregation interval for non-critical tier-B bulk accesses. |
| `AuditLogRetentionDays` | `int` | `1 .. 7300` | `3650` (10 years) | Statutory retention period for audit logs. |
| `VerifyHashChainIntervalHours` | `int` | `1 .. 168` | `24` | Cyclic background integrity check of the entire audit chain using timing-safe `FixedTimeEquals`. |
| `HmacSecretKeyVaultRef` | `string` | Secret name | `"GQL-GATEWAY-AUDIT-HMAC-SECRET"` | Key Vault reference for the secret HMAC key of the audit chain. Outside Development, must resolve to at least 32 UTF-8 bytes (recommended: Base64 of >= 32 random bytes). |
| `ElasticsearchSinkUrl` | `string` | URL | `""` | Optional secondary sink for SIEM systems (Splunk / Elasticsearch). |

```json
"Audit": {
  "TierBAggregationWindowSeconds": 60,
  "AuditLogRetentionDays": 3650,
  "VerifyHashChainIntervalHours": 24,
  "HmacSecretKeyVaultRef": "GQL-GATEWAY-AUDIT-HMAC-SECRET",
  "ElasticsearchSinkUrl": "https://siem.corp.local:9200"
}
```

---

### 2.9 `ReverseProxy` (Forwarded Headers & Proxy Networks)

Protects against IP spoofing behind load balancers (K8s Ingress, F5, Envoy, NGINX).

| Property | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `Enabled` | `bool` | `true \| false` | `true` | Enables evaluation of `X-Forwarded-For` and `X-Forwarded-Proto`. |
| `KnownNetworks` | `List<string>` | CIDR notation | `["127.0.0.1/32", "::1/128"]` | Trusted IP networks (e.g. internal pod subnet). A configured list replaces the default entirely (no merging). Loopback is always trusted for `X-Forwarded-*` headers, so an empty array (`[]`) is treated as not configured and logs a startup warning. |
| `KnownProxies` | `List<string>` | IP addresses | `[]` | Fixed IP addresses of upstream reverse proxies. |

```json
"ReverseProxy": {
  "Enabled": true,
  "KnownNetworks": [
    "127.0.0.1/32",
    "::1/128",
    "10.244.0.0/16"
  ],
  "KnownProxies": [
    "10.0.1.50"
  ]
}
```

---

### 2.10 `OpenMetadata` (Governance Catalog Synchronization & Webhooks)

Automatic synchronization of schema metadata, classification tags (`PII.*`) and ownership from OpenMetadata via the `openmetadata` runtime module (`Gateway:Modules:Enabled`). Without the `openmetadata` module, any configuration under `Gateway:OpenMetadata` (or `Gateway:Catalog:Enabled: true`) fails startup, `/api/webhooks/openmetadata` returns `404` before reading headers or the body, and `syncOpenMetadata` / `syncDataCatalog` return `UNAVAILABLE`.

| Property | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `Enabled` | `bool` | `true \| false` | `false` | Enables the OpenMetadata sync background worker. |
| `ServerUrl` | `string` | URL | `"http://localhost:8585/api/v1"` | Base URL of the OpenMetadata REST API. |
| `AuthToken` | `string` | JWT / token | `""` | Bearer token for the OpenMetadata API. |
| `WebhookSecret` | `string` | Secret | `""` | HMAC-SHA256 secret for validating incoming webhooks at `/api/webhooks/openmetadata`. |
| `ServiceFilter` | `string` | Service name | `""` | Filters tables to a specific data service. |
| `SyncIntervalMinutes` | `int` | `1 .. 1440` | `30` | Background polling interval. |
| `TagToMaskingRuleMap` | `Map` | Tag -> MaskingType | *See below* | Mapping of OpenMetadata classification tags to masking rules. |
| `TeamToGroupSidMap` | `Map` | Team -> SID | `{}` | Translation of OpenMetadata teams to Active Directory group SIDs. Keys need the prefix `id:` (valid GUID), `fqn:` or `name:`; values must be valid SIDs (`S-1-...`). A key without a prefix, an invalid GUID, an empty or invalid value, and two keys for the same team make startup fail (the message names the option path and entry position, never a SID value). |
| `UserToUserSidMap` | `Map` | User -> SID | `{}` | Translation of OpenMetadata user names to Windows user SIDs. |

```json
"OpenMetadata": {
  "Enabled": true,
  "ServerUrl": "https://openmetadata.corp.local/api/v1",
  "AuthToken": "eyJhbGciOi...",
  "WebhookSecret": "<GENERATE_STRONG_SECRET>", // openssl rand -base64 32
  "ServiceFilter": "enterprise_dw",
  "SyncIntervalMinutes": 30,
  "TagToMaskingRuleMap": {
    "PII.Sensitive": "REDACT",
    "PII.Email": "MASK_EMAIL",
    "PII.Pseudonym": "HMAC_SHA256",
    "PersonalData.Personal": "REDACT"
  },
  "TeamToGroupSidMap": {
    "name:FinanceAnalytics": "S-1-5-21-5001",
    "fqn:Org.DataScienceTeam": "S-1-5-21-5002"
  },
  "UserToUserSidMap": {
    "john.doe": "S-1-5-21-1001"
  }
}
```

> [!IMPORTANT]
> **Webhook replay protection (`/api/webhooks/openmetadata`)**:
> - Configure OpenMetadata webhook subscriptions with `authType: bearer` and `secretKey` matching `Gateway:OpenMetadata:WebhookSecret`.
> - Incoming webhooks require a valid HMAC-SHA256 signature in the `X-OpenMetadata-Signature` header (fallback `X-OM-Signature`), verified via `CryptographicOperations.FixedTimeEquals`. Missing/invalid signatures or a blank `WebhookSecret` return `HTTP 401` before body parsing; without the `openmetadata` module, the route returns `HTTP 404` before reading headers or body.
> - Verified webhook payloads must contain `id` (unique GUID/ID) and `timestamp` (Unix milliseconds). Payloads missing required fields, already processed IDs (deduplication), or timestamps outside the 5-minute sliding window are rejected fail-closed (`HTTP 400`).

---

### 2.11 `Plugins` (Isolated C# Connectors)

Manages dynamic C# extensions (`IHttpDataSourcePlugin`) in isolated `AssemblyLoadContext` instances.

| Property | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `Directory` | `string` | Directory path | `"plugins"` | Relative or absolute path to the plugin folder containing DLLs. |

```json
"Plugins": {
  "Directory": "/var/gql-gateway/plugins"
}
```

#### `Gateway:ExtensibilityPipeline` (Interceptor failure policy)

| Property | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `IngressFailurePolicy` | `FailClosed \| FailOpen` | `FailClosed` | An ingress interceptor that throws or times out denies the request (HTTP 503) when fail-closed; it is skipped when fail-open. |
| `EgressFailurePolicy` | `FailClosed \| FailOpen` | `FailOpen` | An egress interceptor failure is logged and skipped when fail-open; with fail-closed the failure propagates and the response is not delivered. |
| `InterceptorTimeoutMs` | `int` | `5000` | Per-interceptor time budget in milliseconds (1-600000). |

Plugin action handlers are limited to 30 seconds per execution; in-flight actions are drained (up to 5 seconds) on shutdown. `IHttpDataSourcePlugin` has no `ConfigureServices` member; plugins receive services through constructor injection.

---

### 2.12 `DataSources.Http` (SSRF Protection & Egress Security for REST)

Declarative HTTP data sources (`DeclarativeHttpDataSourceExecutor`) are subject to strict zero-trust egress rules to protect against server-side request forgery (SSRF):

- **DNS & IP validation**: Before every HTTP call the target host name is resolved via DNS. Loopback addresses (`127.0.0.0/8`, `::1`), link-local (`169.254.0.0/16`, `fe80::/10`) and private networks per RFC 1918 (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`) are rejected.
- **Cloud metadata protection**: Host names such as `metadata.google.internal` and `kubernetes.default.svc` are explicitly blocked.
- **Hop-by-hop redirect validation**: Automatic following of HTTP redirects is disabled in the HTTP client (`AllowAutoRedirect = false`). For status codes 301, 302, 307 and 308 the executor re-validates the `Location` header step by step (maximum 5 hops) to rule out SSRF via open redirects.

---

### 2.13 `Catalog` (Enterprise Data Catalog Integration & GDPR Classification)

Enables the central connection to **OpenMetadata** for automated mirroring of metadata and sensitivity classifications.

| Property | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `Enabled` | `bool` | `true \| false` | `false` | Enables the data catalog integration. |
| `Provider` | `string` | `OpenMetadata` | `"OpenMetadata"` | Active data catalog provider. |
| `SyncIntervalMinutes` | `int` | `1 .. 1440` | `60` | Synchronization interval of the background service in minutes. |
| `TagToMaskingRuleMap` | `Dictionary<string, string>` | Key-value pairs | *(default map)* | Maps catalog tags to masking rules (`REDACT`, `MASK_EMAIL`, `HMAC_SHA256`). |
| `GdprArticle9Tags` | `List<string>` | Tag names | *(Art. 9 tags)* | Tags for special categories (health, biometrics, genetics, religion). Enforces `HIGH`, four-eyes and `REDACT`. |
| `PiiTags` | `List<string>` | Tag names | *(PII tags)* | Tags for personal data. |
| `SharedTables` | `List<string>` | `domain.schema.table` | `[]` | Tables visible in the GraphQL `catalog` to administrators and discovery users of every tenant, in addition to the tables the tenant holds a consent record on. |

> [!NOTE]
> **Tenant-scoped GraphQL catalog and `syncOpenMetadata`**: Outside the default tenant, the tenant administrator view of the GraphQL `catalog` is limited to tables on which the caller's tenant holds at least one consent record (Allow or Deny) or that are listed in `Catalog.SharedTables`. Only the canonical `ClusterAdmin` keeps the global view; `GovernanceAdmin` is tenant-scoped. `accessStatus` is computed from the caller's consents. `syncOpenMetadata` is available to the canonical `ClusterAdmin` (all tenants) and to `GovernanceAdmin` (own tenant only: tables mapped to that tenant via `OpenMetadata.ServiceDatabaseToTenantMap`); it returns warnings as stable codes with counts (for example `OM_TABLE_MAP_FAILED: 3`) and failures as the error code `SYNC_FAILED` without detail text. Full details are written to the server log.

> [!NOTE]
> **`AllowAuthenticatedCatalogDiscovery` is deprecated and has no effect.** Every non-administrator (GraphQL, REST, MCP) sees only the tables and columns its consents allow; the flag no longer widens the catalog. A start-up warning is logged when it is set.

#### Removed: OpenSchema

The `Gateway:OpenSchema` and `Gateway:Catalog:OpenSchema` keys were removed (OD-19). Setting either key triggers application startup failure via `LegacySwitchGuard`. Anonymous catalog access is not supported; authenticated users discover the catalog by default ("visible unless hidden"). Objects and columns can be explicitly hidden using `PUT /api/v1/catalog/datasets/{id}/discovery`, and the legacy `visibility` tier is ignored.

> [!NOTE]
> **Sensitivity Classification Ranking (D-4 & ADR-010)**:
> The gateway evaluates table sensitivity across a defined ratchet ranking:
> `PUBLIC` / `LOW` (rank 0) < `NORMAL` / `INTERNAL` (rank 1) < `MEDIUM` (rank 2) < `CONFIDENTIAL` (rank 3) < `HIGH` (rank 4) < `RESTRICTED` / `SECRET` (rank 5).
> - **High-Sensitivity Threshold**: A table is treated as highly sensitive (`Table.IsSensitivityHigh = true` / `IsHighlySensitive`) if its sensitivity is **`CONFIDENTIAL` or higher** (rank >= 3), or if it has an unknown classification (fail-closed, e.g. `PII`).
> - Tables classified as `MEDIUM` are **not** treated as high-sensitivity.
> - High-sensitivity triggers mandatory Four-Eyes approval (`RequiresFourEyes = true`), shorter consent cache TTL, and fail-closed behavior in degraded mode.

```json
"Catalog": {
  "Enabled": true,
  "Provider": "OpenMetadata",
  "SyncIntervalMinutes": 60,
  "TagToMaskingRuleMap": {
    "PII.Sensitive": "REDACT",
    "PII.Email": "MASK_EMAIL",
    "PII.Pseudonym": "HMAC_SHA256"
  },
  "GdprArticle9Tags": [
    "GDPR.Article9", "Art9", "HealthData", "Biometric", "Genetic",
    "ReligiousBelief", "TradeUnionMembership", "SexLife", "PoliticalOpinion"
  ]
}
```

---

### 2.14 `Insecure` (Pragmatic Onboarding & Third-Party System Integration)

For quick PoCs, integration tests, external webhook systems or third-party connectors, security checks can be relaxed via configuration. To make risks transparent, all parameters are prefixed by security impact:

- **`warn_` (medium impact)**: Relaxes limits and network protection.
- **`danger_` (critical impact)**: Completely disables authentication, authorization or certificate checks.

> [!CAUTION]
> **Production & Staging Warning (DANGER Switches)**:
> All `danger_`-prefixed switches, as well as flags reclassified as `DANGER:` (such as `warn_allow_unmasked_ai_access`, `warn_mock_external_systems_if_unreachable`, `warn_auto_approve_access_requests`, `warn_disable_rate_limiting`, `warn_allow_unsigned_s3_requests`, `warn_ignore_webhook_timestamp_tolerance`, `OpenMetadata.AutoCreateConsents`, and `WebSql.MaxAffectedRows <= 0` with DML enabled), **strictly abort application startup outside of the `Development` environment** (`ValidationException` fail-fast).

| Property | Type | Default | Security level | Description |
| :--- | :--- | :--- | :--- | :--- |
| `danger_allow_anonymous_access` | `bool` | `false` | **CRITICAL** | Allows GraphQL queries without any authentication (anonymous user). |
| `danger_bypass_consent_checks` | `bool` | `false` | **CRITICAL** | Bypasses the zero-trust consent check (`ALLOW` for all tables). |
| `danger_bypass_webhook_signature_validation` | `bool` | `false` | **CRITICAL** | Skips HMAC digest verification for incoming webhooks while still requiring a non-empty signature header (`X-OpenMetadata-Signature` / `X-OM-Signature`). |
| `danger_allow_untrusted_certificates` | `bool` | `false` | **CRITICAL** | Accepts self-signed or expired SSL/TLS certificates on outgoing HTTP calls. |
| `danger_allow_anonymous_webhooks` | `bool` | `false` | **CRITICAL** | Accepts incoming webhook payloads without an auth token or secret. |
| `warn_allow_all_cors_origins` | `bool` | `false` | **WARN** | Sets `Access-Control-Allow-Origin: *` and disables the CSRF preflight. |
| `warn_disable_rate_limiting` | `bool` | `false` | **WARN** | Disables IP- and SID-based rate limiting (no `429 Too Many Requests`). |
| `warn_relaxed_query_limits` | `bool` | `false` | **WARN** | Disables AST depth and complexity limits for deeply nested queries. |

```json
"Insecure": {
  "warn_allow_all_cors_origins": true,
  "warn_disable_rate_limiting": true,
  "danger_bypass_webhook_signature_validation": false,
  "danger_allow_untrusted_certificates": false
}
```

---

### 2.14a `Dev` (Development-only switches)

`Gateway:Dev` groups the development conveniences (class A) and development security switches (class B). Security bypasses stay in `Insecure` (class C). Everything in `Dev` is **Development only**: outside Development a non-default value is a startup error. Detailed developer guide: [developer-guide.md#3-developer-options-gatewaydev](developer-guide.md#3-developer-options-gatewaydev).

| Property | Type | Default | Class | Description |
| :--- | :--- | :--- | :--- | :--- |
| `Preset` | `string` | `Standard` | A/B | `Standard`, `Quickstart` (adds `warn_allow_all_cors_origins`, `warn_enable_introspection`, `warn_auto_approve_access_requests`) or `Strict` (no test auth, no introspection). The former `Gateway:Profile` key was removed and fails startup. |
| `Banner` | `bool` | `true` | A | Startup banner with links and personas. |
| `VerboseErrors` | `bool` | `true` | A | Diagnostic `problem+json` for 403 and unhandled exceptions. |
| `PersonaLogin` | `bool` | `true` | A | `/api/dev/personas` and `/api/dev/login/{persona}`. |
| `Info` | `bool` | `true` | A | `/api/dev/info` (effective configuration without secrets). |
| `DemoData` | `bool?` | preset | A | Seed demo catalog data. Alias of `GovernanceDb.SeedDemoData`. |
| `TestAuthHandler` | `bool?` | preset | B | Header-based test identities. Alias of `Authentication.EnableTestAuthHandler`. |
| `Tooling:BananaCakePop` | `bool?` | preset | A | Alias of `GraphQL.EnableBananaCakePop` in Development. |
| `Tooling:Introspection` | `bool?` | preset | B | Alias of `GraphQL.EnableIntrospection` in Development. |

Precedence per switch: explicit `Dev` value, then an explicitly set legacy key (deprecated, logged at startup), then the preset default. A `Dev` value that contradicts its legacy key fails the start. Do not set the legacy keys in the base `appsettings.json`: an explicit value there defeats the preset default. `GraphQL.EnableIntrospection` and `GraphQL.EnableBananaCakePop` remain regular options in every environment; only the `Dev:Tooling:*` aliases are Development-only.

---

### 2.15 `Identity & Multi-Tenant / M2M Service-Accounts`

Supports hybrid identity migration and machine-to-machine access for background jobs:

- **Abstraction layer (`IIdentityProvider`)**: Enables parallel operation of on-prem Active Directory (Kerberos) and Microsoft Entra ID (Azure AD / OIDC) without code changes to business components.
- **Service accounts & M2M access**: Authentication via OAuth2 client credentials or mutual TLS (mTLS). In the gateway the caller is represented as a service principal with an `SP-<client_id>` SID and receives dedicated, time-limited consents with a technical justification.
- **Multi-tenant data isolation**: Two-tier tenant separation via SQL pushdown (`WHERE tenant_id = @tenant`) and native PostgreSQL row-level security using transactional `SET LOCAL app.tenant_id = @tenant`.

---

### 2.16 `Mcp` (Enterprise Model Context Protocol Server & AI Data Guardrails)

Exposes authorized GraphQL persisted queries as typed tools for autonomous AI agents (Anthropic Claude, AutoGen, LangChain) over standards-compliant Server-Sent Events (SSE) and JSON-RPC 2.0.

| Property | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `Mcp:Enabled` | `bool` | `true \| false` | `false` | Enables the MCP server (streamable HTTP at `Mcp:EndpointPath`, official MCP C# SDK, stateless). |
| `Mcp:EndpointPath` | `string` | URL path | `"/mcp"` | Path of the MCP endpoint (streamable HTTP). |
| `Mcp:MaxTokensPerCall` | `int` | `256 .. 128000` | `4096` | Maximum token budget per tool call; prevents context window overflows. |
| `Mcp:MaxResultRows` | `int` | `1 .. 10000` | `100` | Upper limit of result rows per MCP data tool (`query_dataset`, `query_sql`, `call_procedure`, `sample_rows`, `query_graphql`; for GraphQL per list). A smaller `limit` in the request is kept, a larger one never raises the limit; an invalid `limit` (0, negative, not an integer) is rejected. If rows were truncated, the result carries `truncated: true` and `rowCap`. `sample_rows` additionally stays limited to 20 rows. A value `<= 0` falls back to `100`. |
| `Mcp:AllowedOperations` | `string[]` | GraphQL operations | `[]` | Allowlist of approved queries. |

> `warn_allow_unmasked_ai_access` and `danger_bypass_mcp_auth` live exclusively under `Insecure` (ADR-012, phase 4).

The server is stateless: every request runs with the identity of its own HTTP request, there are no MCP sessions and no legacy SSE transport (`/mcp/sse`, `/mcp/message`) any more. If Entra ID or AD FS is enabled, the gateway publishes the OAuth metadata (RFC 9728) at `/.well-known/oauth-protected-resource<EndpointPath>` with these issuers as authorization servers; 401 responses of the MCP endpoint point to it via `WWW-Authenticate: Bearer resource_metadata=…`. This way MCP clients find out without further configuration where to obtain a token.

The dataset tools `list_datasets`, `describe_dataset`, `query_graphql` and `sample_rows` ([F-AI-11](features/f-ai-11-mcp-dataset-tools.md)) are always registered and need no entry in `AllowedOperations`. Agents preferably query data with `query_graphql`. Every MCP tool is decided by the Access Decision Point; consent, row filters and masking apply in every case.

```json
"Mcp": {
  "Enabled": true,
  "EndpointPath": "/mcp",
  "MaxTokensPerCall": 4096,
  "MaxResultRows": 100,
  "AllowedOperations": [
    "query_customers",
    "query_invoices",
    "query_data_catalog"
  ]
}
```

### 2.17 `DuckDbOlap` & `ArrowExport` (In-Memory OLAP & Arrow IPC Streaming)

| Key | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `DuckDbOlap:Enabled` | `bool` | `true \| false` | `true` | Enables the embedded in-memory DuckDB vector engine (`F-DATA-03`). |
| `DuckDbOlap:MaxMemory` | `string` | e.g. `"512MB"`, `"2GB"` | `"1GB"` | Maximum RAM quota per DuckDB session (`PRAGMA max_memory`). |
| `DuckDbOlap:MaxThreads` | `int` | `1 .. 8` | `2` | Maximum thread count for parallel SIMD execution. |
| `DuckDbOlap:MaxStagedRowsPerTable` | `int` | `1000 .. 5000000` | `250000` | Upper limit of staged rows per temporary source table. |
| `DuckDbOlap:MaxResultRows` | `int` | `1 ..` | `50000` | Upper limit of the rows of an OLAP result; a smaller `limit` in the request is kept. |
| `DuckDbOlap:QueryTimeoutSeconds` | `int` | `1 .. 300` | `60` | Timeout for analytical in-memory DuckDB queries; it also covers authorization and the source reads of the request. |
| `DuckDbOlap:MaxTempDirectorySize` | `string` | e.g. `"256MB"`, `"0B"` | `"256MB"` | Maximum size of the session spill directory (`PRAGMA max_temp_directory_size`). Authoritative for all DuckDB sessions; `WebSql:CrossSource:MaxTempDirectorySize` was removed. |
| `DuckDbOlap:MaxTableCount` | `int` | `1 ..` | `10` | Maximum distinct tables of one request. |
| `DuckDbOlap:MaxTotalStagedRows` | `int` | `1 ..` | `500000` | Maximum staged rows across all tables of one request. |
| `DuckDbOlap:MaxStagedBytesPerTable` | `long` | `1 ..` | `67108864` | Maximum estimated bytes staged from one table (every request). |
| `DuckDbOlap:MaxTotalStagedBytes` | `long` | `1 ..` | `268435456` | Maximum estimated bytes staged by one request (every request). |
| `DuckDbOlap:MaxConcurrentQueries` | `int` | `1 .. 64` | `4` | Global cap on concurrent OLAP requests. |
| `DuckDbOlap:MaxConcurrentQueriesPerTenant` | `int` | `1 ..` | `2` | Per-tenant cap on concurrent OLAP requests (`429` when exceeded). |
| `DuckDbOlap:MaxSqlLength` | `int` | `1 ..` | `32768` | Maximum length of the client SQL text. |
| `ArrowExport:Enabled` | `bool` | `true \| false` | `false` | Enables the binary zero-copy Apache Arrow export (`F-DATA-04`). |
| `ArrowExport:BatchSize` | `int` | `100 .. 100000` | `10000` | Row count per streamed Arrow RecordBatch. |
| `ArrowExport:MaxRowsPerExport` | `int` | `1000 .. 10000000` | `1000000` | Maximum row count per export operation. |

```json
"DuckDbOlap": {
  "Enabled": true,
  "MaxMemory": "1GB",
  "MaxThreads": 2,
  "MaxStagedRowsPerTable": 250000,
  "QueryTimeoutSeconds": 30
},
"ArrowExport": {
  "Enabled": true,
  "BatchSize": 10000,
  "MaxRowsPerExport": 1000000
}
```


#### `WebSql:CrossSource` (federation switch and limits)

| Key | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `WebSql:CrossSource:Enabled` | `bool` | `true \| false` | `false` | Federation switch. When `false`, requests that join several data sources are denied on every transport, including OLAP. |
| `WebSql:CrossSource:AllowedTransports` | `string[]` | `WebSql`, `Trino`, `Rest`, `Mcp`, `ArrowExport`, `SqlEndpoints`, `AsyncJob`, `Olap` (case-insensitive) | `["WebSql", "Trino"]` | Transports that may run cross-source queries. A configured list replaces the default entirely (list every transport you need); an empty list allows none. `Olap` is never implied: add it explicitly to allow cross-source requests on `POST /api/v1/olap/query`. An unknown or blank name aborts the start. A start-up warning is logged when `Olap` is listed while `Enabled` is `false`. |
| `WebSql:CrossSource:MaxTableCount` | `int` | `1 ..` | `5` | Maximum distinct tables of a cross-source request. |
| `WebSql:CrossSource:MaxStagedRowsPerTable` / `MaxTotalStagedRows` | `int` | `1 ..` | `50000` / `200000` | Row budgets of the SQL federation. |
| `WebSql:CrossSource:MaxStagedBytesPerTable` / `MaxTotalStagedBytes` | `long` | `1 ..` | `33554432` / `134217728` | Byte budgets; the OLAP endpoint uses the smaller of these and the `DuckDbOlap` values for cross-source requests. |
| `WebSql:CrossSource:TimeoutSeconds` | `int` | `1 ..` | `30` | Timeout of the SQL federation. |
| `WebSql:CrossSource:MaxParallelSourceReads` | `int` | `1 .. 16` | `4` | Upper bound on parallel source reads (reads are currently sequential). |
| `WebSql:CrossSource:AllowNonEquiJoins` | `bool` | `true \| false` | `false` | Permits cross joins and non-equi joins in cross-source statements (SQL federation and OLAP). |

`WebSql:CrossSource:MaxMemory` and `WebSql:CrossSource:MaxTempDirectorySize` were removed; they never had an effect. Use `DuckDbOlap:MaxMemory` and `DuckDbOlap:MaxTempDirectorySize`. A start-up warning is logged when the old keys are still present. Non-positive limits, an unparsable `DuckDbOlap:MaxMemory` or `MaxTempDirectorySize`, `MaxConcurrentQueries` or `MaxConcurrentQueriesPerTenant` below 1, and `MaxThreads` outside `1 .. 8` abort the start.

### 2.19 `WebSql` (Governed SQL & Trino REST Protocol)

Autheris provides an integrated, secured WebSQL interface that is 100% compatible with the Trino/Presto SQL syntax and the native Trino REST client protocol. Queries can be submitted over standard HTTP/HTTPS without opening database ports to the outside.

| Property | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `WebSql:Enabled` | `bool` | `true` | Enables the WebSQL and Trino statement endpoints. |
| `WebSql:DefaultDataSourceName` | `string` | `"default"` | Default data source when no catalog/data source is specified explicitly. |
| `WebSql:AllowedDataSources` | `string[]` | `[]` | List of globally approved data sources for WebSQL queries. |
| `WebSql:TenantDataSourceAllowlist` | `Dictionary<string, string[]>` | `{}` | Tenant-specific restriction of allowed data sources. |
| `WebSql:DefaultMaxRows` | `long` | `1000` | Rows of a query without `LIMIT` (`POST /api/v1/sql`). |
| `WebSql:MaxAllowedRows` | `long` | `10000` | Upper limit for an explicit `LIMIT` (`0` = no upper limit). |
| `WebSql:ExecutionTimeoutSeconds` | `int` | `30` | Maximum timeout for query execution in the backend. |
| `WebSql:AllowDml` | `bool` | `false` | Allows write operations (`INSERT`, `UPDATE`, `DELETE`) based on explicitly approved write rules (`WriteRules`) in consents/access profiles. Startup fails unless `DataSources:RequireTenantColumn=true`, `WebSql:MaxAffectedRows` is greater than 0, and a cluster-wide idempotency store (`Caching:Redis:Enabled = true`) is configured. DML on `/api/v1/sql` requires the `Idempotency-Key` header. |
| `WebSql:MaxAffectedRows` | `long` | `1000` | Maximum permitted row count for DML; exceeding it triggers an automatic rollback. |
| `WebSql:RejectUnfilteredDml` | `bool` | `true` | Prevents unfiltered `UPDATE`/`DELETE` statements (`WHERE 1=1`, `WHERE true`). |

```json
"WebSql": {
  "Enabled": true,
  "DefaultDataSourceName": "default",
  "AllowedDataSources": ["sales", "finance", "analytics"],
  "DefaultMaxRows": 1000,
  "MaxAllowedRows": 10000,
  "ExecutionTimeoutSeconds": 30,
  "AllowDml": false,
  "MaxAffectedRows": 1000,
  "RejectUnfilteredDml": true
}
```

#### Row Limits per Transport (`RowLimits`)

All SQL transports run through the same WebSQL pipeline. Without their own values, `WebSql:DefaultMaxRows` and `WebSql:MaxAllowedRows` apply; per transport, each of the two values can be overridden individually:

| Key | Transport | Additional upper limit |
|---|---|---|
| `RowLimits:Trino:DefaultMaxRows` / `:MaxAllowedRows` | Trino protocol (`POST /v1/statement`) | – |
| `RowLimits:Parquet:...` | WebSQL with `Accept: application/vnd.apache.parquet` | `ParquetEgress:MaxRowsPerFile` |
| `RowLimits:SqlEndpoints:...` | Declared SQL endpoints (`/api/v1/queries/...`) | – |
| `RowLimits:ArrowExport:...` | Arrow-Export (`POST /api/v1/export/arrow`) | `Arrow:MaxExportRows` |

Results that reach the limit are reported as truncated (`truncated`, `X-Autheris-Truncated`, for Parquet additionally `X-Export-Truncated`).

```json
"RowLimits": {
  "Parquet": { "MaxAllowedRows": 1000000 },
  "Trino": { "DefaultMaxRows": 10000, "MaxAllowedRows": 100000 }
}
```

#### Trino REST Client Protocol (`/v1/statement`) & `wait_timeout`

Autheris supports the native Trino REST client protocol, so standard Trino tools (Trino CLI, Python `trino-python-client`, DBeaver, Apache Superset) can be connected directly:

- **Endpoints:**
  - `POST /v1/statement` (or `POST /api/v1/sql`): Accepts the query in the request body (text or JSON).
  - `GET /v1/statement/queued/{id}` (or `GET /api/sql/statements/{id}`): Polls the status of long-running queries.
  - `DELETE /v1/statement/{id}` (or `DELETE /api/sql/statements/{id}`): Cancels a running query (`204 No Content`).
- **Synchronous Fast-Path via `X-Trino-Wait-Timeout`:**
  - If a timeout is passed (e.g. `X-Trino-Wait-Timeout: 5s` or URL parameter `?wait_timeout=5s`) and the query finishes within this window, the gateway responds immediately with HTTP 200 and status `FINISHED`, including all rows.
- **Asynchronous Continuation Path:**
  - If the query takes longer than the timeout, the gateway responds immediately with status `RUNNING` and a `nextUri` (`/v1/statement/queued/{id}`) to prevent timeouts at load balancers and proxies.
- **Three-part identifiers (`<catalog>.<schema>.<table>`):**
  - Tables can be addressed as `catalog.schema.table` by default.
  - The `catalog` part is validated against the data source or automatically inferred as the target data source.
  - The dialect generator strips the catalog prefix before execution on PostgreSQL (`"schema"."table"`), SQL Server (`[schema].[table]`) or SQLite (`[table]`), which prevents cross-database and DB-escape collisions.
- **Header support:**
  - `X-Trino-Catalog`: Selects the target data source.
  - `X-Trino-Schema`: Default schema.
  - `X-Trino-Wait-Timeout`: How long a request waits for a synchronous result (e.g. `5s`, `500ms`, `1m`).
  - `X-Trino-User` & `X-Trino-Source`: Identity and auditing context.

### 2.20 `VirtualFilters` (Virtual Filters & Access Profiles)

Controls the management and cluster synchronization of relation-based row filters ([`F-GOV-09`](features/f-gov-09-virtual-filters.md)).

| Property | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `VirtualFilters:MaxRemovals` | `int` | `10` | Maximum number of bindings a GitOps sync may remove without `?force=true`. Removing widens the grantee's visibility rights; `0` disables the guard. |
| `VirtualFilters:GenerationCheckSeconds` | `int` | `5` | Interval in seconds at which a gateway instance reconciles the generation with the database. `0` checks on every access. |
| `RowFilters:SubqueryStrategy` | `enum` | `Exists` | SQL strategy for row-filter subqueries built as SQL text: `Exists` (`EXISTS (SELECT 1 ...)`), `InCorrelated` or `In`. Applies to the REST data API, OLAP and federated staging, governed aggregations, procedures and change detection. **No effect on the governed SQL compiler** (WebSQL, Trino protocol, Arrow IPC over HTTP, async jobs, SQL endpoints, MCP SQL tools), which always emits correlated policy subqueries; a non-default value is reported as a startup warning (`Configuration: Gateway:RowFilters:SubqueryStrategy ... has no effect ...`). |
| `Logging:LogGeneratedSql` | `bool` | `false` | Diagnostic switch for logging generated target SQL queries including RLS and virtual filter predicates. |

```json
"VirtualFilters": {
  "MaxRemovals": 10,
  "GenerationCheckSeconds": 5
},
"RowFilters": {
  "SubqueryStrategy": "Exists"
},
"Logging": {
  "LogGeneratedSql": false
}
```

---

### 2.21 `Governance:IndividualGrants` (Time-Bound Individual Grants)

Optional operator limits of individual grants (`User` / `ServicePrincipal` grantees and individual access profile assignments). There are **no compiled-in ceilings**: grants may be valid for multiple years. The overall maximum validity comes from the required `Governance:Grants:MaxDuration` setting (ISO-8601, for example `P5Y`; startup fails without it, `DefaultDuration` must not exceed it). A per-class value below can only **shorten** that maximum for its class; unset (`null`) means no class-specific limit, and a value below 1 aborts startup. Group and role grants take no class limit, but an explicit expiry is bounded by `MaxDuration` too. Details and error codes: [F-GOV-22](features/f-gov-22-time-bound-individual-grants.md).

| Key | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `RegularMaxDays` | `int?` | `>= 1` | unset | Regular data. Also the read-time clamp of user consents when set. |
| `SensitiveMaxDays` | `int?` | `>= 1` | unset | Sensitive / PII tables (unclassifiable tables count as sensitive). |
| `UnmaskedMaxDays` | `int?` | `>= 1` | unset | Individual `Unmasked` access. |
| `PrivilegedMaxDays` | `int?` | `>= 1` | unset | Individual privileged functional rights. |
| `ServicePrincipalMaxDays` | `int?` | `>= 1` | unset | Service principals as direct grantees. Also their read-time clamp when set. |
| `CustomerRegularMaxDays` | `int?` | `>= 1` | unset | Regular data for customer-realm users. |
| `AllowCustomerUnmasked` | `bool` | | `false` | Allows individual `Unmasked` grants in customer realms. |
| `AllowCustomerPrivileged` | `bool` | | `false` | Allows individual privileged grants in customer realms. |
| `RoleConversionHintAfterRenewals` | `int` | `1 .. 10` | `2` | Renewals after which a conversion to a group/role mapping is suggested. |
| `CampaignRequiredAfterRenewals` | `int` | `1 .. 10` | `3` | Renewals after which a direct extension is refused and a campaign decision is required. |

```json
"Governance": {
  "Grants": {
    "DefaultDuration": "P1Y",
    "MaxDuration": "P5Y",
    "DeclarativeMaxDuration": "P5Y"
  },
  "IndividualGrants": {
    "SensitiveMaxDays": 365,
    "UnmaskedMaxDays": 180
  }
}
```

### 2.22 `Tenancy` (Default tenant)

Whether the deployment is single- or multi-tenant is a **deployment setting**. It is never inferred from a missing tenant claim in a token.

| Key | Type | Range | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `DefaultTenantId` | `string` | tenant id (alphanumeric, `_` and `-`, at most 64 characters, no leading `_`) | unset | The tenant of every principal without a tenant claim and the binding of identity realms without their own tenant binding. The default tenant is also the catalog owner: its `GovernanceAdmin` sees the whole catalog. Unset: no default tenant; a principal without tenant claim is rejected with `403 TENANT_UNRESOLVED`, and a realm without tenant binding fails start-up. An empty, malformed or reserved value fails start-up. |

Single-tenant deployments set `Gateway:Tenancy:DefaultTenantId`; multi-tenant deployments omit it and carry a tenant claim in every token. The removed keys `Gateway:Tenancy:Mode` and `Gateway:Authentication:ForwardAuth:DefaultTenantId` fail start-up on presence, whatever their value and spelling (for example `Gateway__Tenancy__Mode`).

Identity realms are read from `Authentication:Realms` once at start and registered in the realm registry; a principal issued by a realm resolves its tenant from the realm (`FixedTenantId`, a tenant claim mapping, or the default tenant for an unbound realm). Changes to `Authentication:Realms` need an application restart: the realm registry is initialised once at start-up and is not reloaded.

**Catalog caller classes.** Every catalog channel (REST `/api/v1/catalog/*`, GraphQL `catalog`, MCP tools and resources, `invoke_api`, Trino/Arrow/Iceberg metadata) decides the caller class from the authenticated principal only, through one resolver:

| Caller | Without default tenant | With default tenant |
| :--- | :--- | :--- |
| Canonical `ClusterAdmin` (no forward-auth identity, no tenant-prefixed role) | whole catalog, all tenants | whole catalog |
| `GovernanceAdmin` of the default tenant (with or without tenant claim) | not applicable | whole catalog |
| `GovernanceAdmin` of another tenant | tables of its tenant (consent-derived ownership plus `Gateway:Catalog:SharedTables`) | same |
| `GovernanceAdmin` without tenant claim | `403 TENANT_UNRESOLVED` | acts in the default tenant |
| Everybody else | consent path: tables and columns allowed by the caller's active consents | same |
| No principal, unauthenticated, invalid tenant claim | nothing | nothing |

**Semantic metric catalog.** The in-memory semantic catalog (dbt semantic manifest, `GET /modules/dbt/metrics`, metric queries) is partitioned per tenant. A tenant sees only its own partition; the default tenant is the partition of principals without tenant claim. `GET /modules/dbt/metrics` and `GET /api/v1/analytics/metrics` return the full metric definition (filter template, type parameters, meta) only to `GovernanceAdmin` and `DbtAdmin`; everybody else gets name, type, label, description and unit.

The tenant signal of the tenant administrator view is derived from consent records and is not a hard tenant boundary; a dedicated tenant-owner attribute for tables is a separate topic.

**Start-up diagnostics.** A Warning is logged at start when `DefaultTenantId` is not configured (single-tenant deployments must set it). The effective state (`Configured` or `None`) is audited with the catalog default visibility.

#### Behaviour notes

- A principal without tenant claim and without default tenant is no administrator and gets `403 TENANT_UNRESOLVED` on protected endpoints.
- Deployments with ReBAC enabled need a consent **and** a ReBAC permission to discover a dataset (intersection).
- `GET /api/v1/catalog/datasources` and its MCP equivalents are administrator-only.
- `AllowAuthenticatedCatalogDiscovery` has no effect.
- `sample_rows(synthetic: true)` needs the administrator view or an active table consent.
- Search recall is reduced for column-restricted callers.
- Known limitation: the tenant-scoped administrator sees only the tables on which its tenant holds a consent plus `Gateway:Catalog:SharedTables`.
- The semantic manifest import (`POST /modules/dbt/semantic-manifest`) writes into the tenant of the caller's token, or the default tenant for a principal without tenant claim.

---

### 2.23 `Dbt:ImportDataOwners` (data owners from the dbt governance import)

| Key | Type | Default | Meaning |
|---|---|---|---|
| `Gateway:Dbt:ImportDataOwners` | bool | `false` | Allows `POST /modules/dbt/governance` to write table data owners (`owner` / `owners` per table item). Data owners act as consent approvers, so the switch is explicit configuration and never a request parameter. |

With the default `false`, a payload that carries `owner`, `owners`, `group` or `groups` is rejected as a whole (`dbt_owner_import_disabled`); nothing is written. With `true`, the caller must additionally hold the `GovernanceAdmin` role (re-checked in the import service; `DbtAdmin` alone is rejected), and the write repository and the audit repository must be registered. The manifest sync (`/sync`) never writes owners. See the dbt module documentation for the payload and its limits.

## 3. Deployment & Environment Variables

```bash
# ASP.NET Core Hosting & Environment
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:5000

# High Availability
Gateway__HighAvailability__DrainDelaySeconds=5
Gateway__HighAvailability__QueryTimeoutSeconds=30
Gateway__HighAvailability__ShutdownTimeoutSeconds=40
Gateway__HighAvailability__TerminationGracePeriodSeconds=60

# Authentication & security
Gateway__Authentication__Domain=CORP.LOCAL
Gateway__Authentication__ServicePrincipalName=HTTP/gateway.corp.local
Gateway__Authentication__RequireKerberosOnly=true
Gateway__Authentication__EnableTestAuthHandler=false

# Governance database
Gateway__GovernanceDb__Provider=PostgreSql
Gateway__GovernanceDb__ConnectionString="Host=pg-ha.corp.local;Database=governance;Username=autheris_app;Password=<secret>;SSL Mode=VerifyFull"

# Caching & Redis Cluster
Gateway__Caching__Redis__Configuration="redis-ha.corp.local:6379,abortConnect=false,ssl=true,password=SecretRedisPass!"
Gateway__Caching__EpochValidation__FailClosedOnSensitiveTables=true

# Secrets & keys
Gateway__DataMasking__HmacKeyId=key-2026-q1
Gateway__DataMasking__HmacSecretKeyVaultRef=GQL-HMAC-SECRET-KEY

# GraphQL & origin security
Gateway__GraphQL__EnableIntrospection=false
Gateway__GraphQL__EnableBananaCakePop=false
Gateway__GraphQL__TrustedOrigins__0=https://bi.corp.local
Gateway__GraphQL__TrustedOrigins__1=https://portal.corp.local

# OpenMetadata (runtime module)
Gateway__Modules__Enabled__0=openmetadata
Gateway__OpenMetadata__Enabled=true
Gateway__OpenMetadata__ServerUrl=https://openmetadata.corp.local/api/v1
Gateway__OpenMetadata__AuthToken=eyJhbGciOi...
Gateway__OpenMetadata__WebhookSecret=<GENERATE_STRONG_SECRET> # openssl rand -base64 32
```

### 3.2 Example Kubernetes Deployment & ConfigMap

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: gql-gateway
  namespace: data-governance
spec:
  replicas: 3
  selector:
    matchLabels:
      app: gql-gateway
  template:
    metadata:
      labels:
        app: gql-gateway
    spec:
      terminationGracePeriodSeconds: 60
      containers:
        - name: gateway
          image: registry.corp.local/gql-gateway:latest
          ports:
            - containerPort: 5000
          resources:
            requests:
              cpu: "1000m"
              memory: "1Gi"
            limits:
              cpu: "4000m"
              memory: "2Gi"
          lifecycle:
            preStop:
              exec:
                command: ["/bin/sh", "-c", "sleep 5"]
          livenessProbe:
            httpGet:
              path: /health/live
              port: 5000
            initialDelaySeconds: 5
            periodSeconds: 10
          readinessProbe:
            httpGet:
              path: /health/ready
              port: 5000
            initialDelaySeconds: 5
            periodSeconds: 5
          envFrom:
            - configMapRef:
                name: gql-gateway-config
            - secretRef:
                name: gql-gateway-secrets
```

### 3.3 Traefik Ingress & ForwardAuth Integration (Kubernetes)

When Autheris runs in Kubernetes behind **Traefik**, Traefik handles authentication (e.g. via Authelia, Keycloak or Authentik) and forwards the verified identity attributes to Autheris.

#### 3.3.1 Traefik ForwardAuth Middleware

```yaml
apiVersion: traefik.io/v1alpha1
kind: Middleware
metadata:
  name: forward-auth
  namespace: data-governance
spec:
  forwardAuth:
    address: https://auth.corp.local/api/verify
    trustForwardHeader: true
    authResponseHeaders:
      - X-Forwarded-User
      - X-Forwarded-Email
      - X-Forwarded-Groups
      - X-Forwarded-Roles
      - X-Forwarded-Tenant
```

> [!SECURITY]
> **SEC H-1 tenant isolation**: `X-Forwarded-Tenant` is ignored by Autheris by default (`TrustUpstreamTenant = false`) to prevent header spoofing by clients. If the reverse proxy sets the tenant, `Authentication:ForwardAuth:TrustUpstreamTenant = true` and an explicit allowlist `Authentication:ForwardAuth:AllowedTenantIds` must be configured. If `AllowedTenantIds` is empty, every authentication fails closed.

#### 3.3.2 Traefik Shared-Secret Middleware (Anti-Spoofing)

```yaml
apiVersion: traefik.io/v1alpha1
kind: Middleware
metadata:
  name: gateway-shared-secret
  namespace: data-governance
spec:
  headers:
    customRequestHeaders:
      X-Forwarded-Secret: "<GENERATE_STRONG_SECRET>" # openssl rand -base64 32
```

#### 3.3.3 Traefik IngressRoute

```yaml
apiVersion: traefik.io/v1alpha1
kind: IngressRoute
metadata:
  name: gql-gateway-ingress
  namespace: data-governance
spec:
  entryPoints:
    - websecure
  routes:
    - match: Host(`graphql.corp.local`) && PathPrefix(`/graphql`)
      kind: Rule
      middlewares:
        - name: forward-auth
        - name: gateway-shared-secret
      services:
        - name: gql-gateway
          port: 5000
  tls:
    secretName: corp-wildcard-tls
```

---

## 4. Configuration Profiles Compared

| Parameter | Development (`appsettings.Development.json`) | Production (`appsettings.Production.json`) |
| :--- | :--- | :--- |
| `Logging:LogLevel:Default` | `Debug` | `Information` / `Warning` |
| `Authentication:EnableTestAuthHandler` | `true` (allows test header) | `false` (**mandatory**) |
| `Authentication:RequireKerberosOnly` | `false` | `true` |
| `Authentication:ForwardAuth:Enabled` | `false` / `true` for tests | `true` (behind K8s Traefik Ingress) |
| `Authentication:ForwardAuth:RequireTrustedProxy` | `false` | `true` (validates Traefik pod CIDRs) |
| `GovernanceDb:Provider` | `PostgreSql` or `SqlServer` (connection string required) | `PostgreSql` or `SqlServer` (TLS with certificate validation) |
| `Caching:EpochValidation:FailClosed` | `false` | `true` (Zero-Trust Fail-Closed) |
| `GraphQL:EnableIntrospection` | `true` | `false` |
| `GraphQL:EnableBananaCakePop` | `true` (Nitro IDE in the browser) | `false` |
| `GraphQL:PersistedQueriesOnly` | `false` | `true` (recommended) |
| `DataMasking:HmacSecretKeyVaultRef` | `"DEV_INSECURE_TEST_KEY_ONLY"` | Key Vault secret reference (**validated**) |
| `Audit:TierBAggregationWindowSeconds` | `5` seconds | `60` seconds |

---

## 5. Production Rollout Checklist

Before releasing a new production environment, verify the following items:

- [ ] **Auth security**: `Gateway:Authentication:EnableTestAuthHandler` is set to `false`.
- [ ] **ForwardAuth / Traefik trust**: In Kubernetes deployments `RequireTrustedProxy = true` is set, `TrustedNetworks` contains only the Traefik ingress pod CIDR and `SharedSecretKeyVaultRef` is configured.
- [ ] **Cryptography**: `Gateway:DataMasking:HmacSecretKeyVaultRef` points to a valid Key Vault secret and not to dev defaults.
- [ ] **Introspection**: `Gateway:GraphQL:EnableIntrospection` and `EnableBananaCakePop` are set to `false`.
- [ ] **Zero-Trust fail-closed**: `Gateway:Caching:EpochValidation:FailClosedOnSensitiveTables` is `true`.
- [ ] **Anti-CSRF & CORS**: `Gateway:GraphQL:TrustedOrigins` contains only verified domains (no wildcard `*` in production!).
- [ ] **High availability**: K8s `terminationGracePeriodSeconds` is greater than `DrainDelaySeconds + ShutdownTimeoutSeconds + 10s`.
- [ ] **Proxy security**: `Gateway:ReverseProxy:KnownNetworks` restricts trusted IPs to the actual ingress controllers.
- [ ] **Real data sources**: `Gateway:DataSources` contains valid connection strings for production business databases.
