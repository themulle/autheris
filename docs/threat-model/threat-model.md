# STRIDE Threat Model & Security Architecture: Autheris Enterprise Gateway

**Status:** Normative / Production Standard (corrected 2026-10-06 after security review recheck 4: statements now describe the implemented behaviour; open gaps are listed per threat as *Known limitation*)  
**Classification:** Public Architecture Documentation  
**Applicability:** Autheris Gateway Engine  

---

## 1. System Overview & Trust Boundaries

Autheris acts as an Enterprise Zero-Trust Data Gateway. It enforces Data-Owner-Consent, row-level security (RLS) pushdown, column-level masking (HMAC pseudonymization / format-preserving / redaction), and strict tenant isolation across relational databases, vector engines, and GraphQL supergraphs.

```
[ Unauthenticated Zone / Internet / Client LAN ]
                    |
      (TB-1: Network / Ingress Perimeter)
                    v
[ Ingress Controller / Reverse Proxy / WAF ]
                    |
      (TB-2: Authentication & Token Perimeter)
                    v
[ Autheris Gateway: Identity & Policy Enforcement Middleware ]
                    |
      (TB-3: Distributed State & Synchronization)
                    v
[ Distributed Cache (Redis) / In-Memory Epoch Validation ]
                    |
      (TB-4: Data Source Access / Trusted Subsystem)
                    v
[ Upstream Data Sources (SQL Server, PostgreSQL, SQLite, Oracle, Databricks, DuckDB) ]
```

---

## 2. STRIDE Threat Analysis & Defense Mechanisms

### 2.1 Spoofing (Identity Spoofing & Impersonation)

* **Threat 1.1: Spoofing of Test Identity Headers (`X-Test-User-Sid`)**
  * *Attack Vector:* An attacker sends simulated development headers to impersonate privileged data owners or arbitrary security identifiers (SIDs).
  * *Mitigation:* Strict environment-gated fail-fast startup validation (`ValidateOnStart`). When running in non-development environments (`!IWebHostEnvironment.IsDevelopment()`), any configuration with `EnableTestAuthHandler=true` causes an immediate process startup crash. Test headers are ignored and stripped in Production.

* **Threat 1.2: Ingress & Reverse Proxy Header Spoofing (`X-Forwarded-*`)**
  * *Attack Vector:* Malicious actors directly send `X-Forwarded-User`, `X-Forwarded-Groups`, or spoofed IP headers to the gateway to forge identities.
  * *Mitigation:*
    1. *Proxy Network Validation:* ForwardAuth rejects requests unless the incoming remote IP matches configured `TrustedNetworks` (CIDR allowlist) or `TrustedProxies`.
    2. *Timing-Neutral Shared Secret:* If configured, `X-Forwarded-Secret` is compared against a Key Vault reference using `CryptographicOperations.FixedTimeEquals` to prevent unauthorized forwarding.

* **Threat 1.3: Tenant Header Tampering & Cross-Tenant Bypass (`X-Tenant-ID`)**
  * *Attack Vector:* An authenticated tenant user sends a forged `X-Tenant-ID` header to access records belonging to another tenant.
  * *Mitigation:* Authoritative tenant claims (`tenant_id`, `tid`, `tenant`) derived from verified credentials are the source of the tenant; services read the tenant from the principal and not from the header. Unauthenticated or untrusted tenant headers are rejected outside development unless `TrustUpstreamTenant` is explicitly allowed with strict `AllowedTenantIds` whitelisting.
  * *Known limitation:* `TenantResolutionMiddleware` is currently **not registered** in the request pipeline (Low-18), so a mismatching `X-Tenant-ID` header is not actively rejected by it. Do not rely on header rejection; tenant isolation rests on the claim-based tenant resolution of the services.

* **Threat 1.4: Password Brute-Force & User Enumeration (HTTP Basic Auth)**
  * *Attack Vector:* Attackers mount offline cracking attacks against stored hashes or exploit response-time differentials to enumerate valid usernames.
  * *Mitigation:*
    1. *Cryptographic Standards:* Outside Development only PBKDF2-HMAC-SHA256 (`$pbkdf2$<iters>$salt$hash`) with at least 210,000 iterations is accepted at startup validation. Argon2id (`$argon2id$v=19$m=...,t=...,p=...$salt$hash`) is supported for Development only and cannot be used in production. The `hash-password` CLI emits PBKDF2 with 600,000 iterations by default and reads the password from stdin (a password argument still works but warns).
    2. *Cost Boundaries:* The verifier accepts PBKDF2 iterations between 10,000 and 10,000,000 (the startup validation enforces the 210,000 minimum for configured users); the cost of the dummy verification for unknown users is not capped (A-6).
    3. *Timing Parity (partial):* Probing nonexistent usernames runs a dummy verification (`ConfigureDummyCost`) to reduce response-time differences. *Known limitation:* a timing oracle remains when users with different hash types are configured (A-7); the oracle is reduced, not eliminated.
    4. *Tenant and Kerberos hardening:* a malformed tenant claim fails the request with 403 (it no longer degrades to the legacy tenant), and malformed `BasicAuth.Users[].TenantId`/`ForwardAuth.DefaultTenantId` values abort startup. With `RequireKerberosOnly` NTLM is rejected (no NTLM header routing, non-Kerberos Negotiate identities refused, NTLM/Kerberos credential persistence disabled). Token revocation by `sub`/`oid` matches both the raw and the JwtBearer-mapped claim names (E-1). The `X-No-Session` opt-out header lives outside the stripped `x-autheris-*` prefix.
    4b. *Ingress-Aware Sliding Lockout:* Failed attempts are tracked against `(user, clientIp)` resolved via `IClientIpResolver` inspecting trusted ingress headers, preventing lockout of the proxy IP itself. The lockout state is held in process memory: no `IDistributedCache` implementation is registered by default (the Redis integration uses `IConnectionMultiplexer` directly), so with N replicas an attacker gets up to N times the attempt budget unless the ingress sticks clients to a replica or an `IDistributedCache` is registered by the operator (the guard uses it when present). *Known limitation:* the lockout is per (user, IP) and has no global limit per user (A-4/E-13).

* **Threat 1.5: Server-Side Request Forgery (SSRF) via Declarative Connectors**
  * *Attack Vector:* Attackers configure connector URLs pointing to cloud metadata endpoints (`169.254.169.254`, `metadata.google.internal`), Kubernetes API services, or internal microservices.
  * *Mitigation:* `DeclarativeHttpDataSourceExecutor` performs pre-request DNS resolution and blocks RFC 1918 private subnets (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`), link-local addresses, loopback, and metadata endpoints. Automatic redirects are disabled (`AllowAutoRedirect=false`); `SendWithRedirectProtectionAsync` follows at most 3 redirects, re-validates every hop, and refuses cross-origin redirects so that credentials never reach another origin. `ValidateResolvedAsync` fails closed: DNS errors and empty answers reject the request. All integration clients (including `DeclarativeHttp`, which plugins use via `SsrfProtectedHttpClientFactory`) run behind `SsrfProtectionHandler` and a pinned `ConnectCallback` that checks every resolved address.
  * *Proxy handling (I-1):* the hardened primary handler uses `UseProxy=false` by default, because a connection to a system proxy (`HTTP_PROXY`/`HTTPS_PROXY`) would skip the address rules and let the proxy resolve the target itself. Operators that must egress through a proxy opt in with `Egress:AllowSystemProxy=true`; connections to that proxy are then exempt from the address rules (the target URL is still validated before sending), so the proxy itself must enforce the egress policy.

---

### 2.2 Tampering (Data Manipulation & Integrity)

* **Threat 2.1: SQL Injection via Dynamic GraphQL Filters**
  * *Attack Vector:* Injection of SQL clauses into `where: { ... }` or stored procedure arguments.
  * *Mitigation:* All column names and identifiers are validated against an alphanumeric regex whitelist (`^[a-zA-Z_][a-zA-Z0-9_]*$`) and verified against the registered `TableMetadata`. All filter values and parameter values are parameterized using native ADO.NET parameter binding (`@p0`, `$1`). Dynamic SQL generation in stored procedures requires explicit `@allow-dynamic-sql` annotation and DBA audit review; this check applies to endpoints with catalog validation (`validation: catalog`). *Known limitation:* endpoints with `validation: declared` (contract-first, outside Development only with `ProcedureEndpoints.AllowDeclaredValidation`) are not inspected by the gateway, the DBA review is the only control.

* **Threat 2.2: Audit Trail Tampering**
  * *Attack Vector:* An attacker with database access alters or deletes records in `AUDIT_LOG_ENTRIES` to cover tracks.
  * *Mitigation:* Cryptographic **HMAC-SHA256 hash chaining** (`entry_hash = HMACSHA256(secretKey, prev_hash || payload)`). Without access to the isolated Key Vault HMAC secret, subsequent hashes cannot be recalculated. The chain is verified on demand by `VerifyAuditHashChainAsync` (constant-time comparison); a signed anchor of the chain tail (`Audit:ChainAnchorPath`, preferably on a separate/shared volume or WORM storage) detects truncation of the end of the chain. Anchors can additionally be archived append-only in a WORM directory and mirrors (`Audit:ChainAnchorWormDirectory`, `Audit:ChainAnchorMirrorPaths`; the newest copy wins) and signed asymmetrically with a KMS/Key Vault key (`Audit:ChainAnchorSignerKeyVaultRef` or a custom `IAuditAnchorSigner`), so an attacker holding the database and the HMAC key still cannot forge an anchor (E-11). With PostgreSQL the table is additionally append-only through triggers (UPDATE/DELETE/TRUNCATE rejected) and writers are serialised with an advisory lock; the runtime role should only hold INSERT/SELECT and the schema should be applied with a separate role (`GovernanceDb:MigrationConnectionString`).
  * *Known limitation:* `AuditChainIntegrityMonitor` verifies the chain every 24 hours by default. Without a configured anchor store, truncation of the chain tail is not detected across restarts (the gateway logs a warning). A rollback of a single co-located anchor (for example `Audit:ChainAnchorPath` next to the database) is only detected when a WORM directory and/or mirror store is configured, because the newest copy across all stores wins. If the newest WORM anchor file is invalid or not validly signed, the store falls back to the newest valid anchor and logs a critical alert instead of failing permanently.

* **Threat 2.3: Consent Cache Poisoning & Stale Permissions**
  * *Attack Vector:* Permissions modified in the catalog are not reflected on active nodes due to cached grants.
  * *Mitigation:* Two-tier cache invalidation with **Policy Epochs**. Every table policy modification increments an integer epoch in the governance store. Gateway nodes validate the epoch on every query resolution; mismatched epochs trigger local cache eviction. Several replicas require Redis (`Caching.Redis.Enabled`) for cluster-wide invalidation; this is enforced at startup outside Development.

* **Threat 2.4: Code Injection via Dynamic ABAC Rules**
  * *Attack Vector:* Dynamic Casbin ABAC rule injection via untrusted inputs leading to expression evaluation execution.
  * *Mitigation:* Dynamic rule tokens are sanitized before evaluation; Casbin evaluators enforce strict token allowlists, preventing arbitrary reflection or process execution.

* **Threat 2.5: Per-replica budgets, file-level-only lakehouse isolation, anonymous health probe (E-14, E-15, R3-4)**
  * *Mitigation E-14:* The FinOps monthly spend is a cluster-wide counter (atomic `INCRBY` in the shared state store, key per tenant and month, 40-day TTL), so N replicas no longer grant N times the budget. If the shared store is unreachable, spend is accounted locally and added on top of the last shared value (fail-safe towards enforcement, not towards a reset).
  * *Mitigation E-15:* Lakehouse scans enforce the tenant on every row, not only on files: the Delta executor adds a mandatory tenant predicate (a conflicting caller-supplied predicate is rejected) and drops rows/files whose stored tenant differs; the Iceberg executor no longer overwrites the stored tenant value with the session tenant and drops foreign rows. Comparison is ordinal and case-sensitive.
  * *Mitigation E-3 (corrects and completes E-15):* The first E-15 text overstated the fix. Both executors now require mandatory ownership evidence per data file for the table's tenant column (the column is resolved from the table, so `tenant_id`/`TenantId`/`tenantId` variants are honoured). Only partition equality, or min == max == tenant statistics, count as proof; overlapping min/max bounds and files without partition value or statistics are dropped. The session tenant is never stamped onto a row: the row's tenant is the stored partition value or single-valued statistic, rows without a stored tenant are dropped, and the row-level `RowBelongsToTenant` check now runs in the Delta executor as well.
  * *Mitigation R3-4:* `/health/ready` (anonymous) is served from a 5 s single-flight cache, so the governance DB probe (SQLite: taken behind the repository lock) runs at most once per interval regardless of request volume.
  * *Known limitation:* lakehouse row data is still produced by the sample reader; a real Parquet reader must keep the per-row tenant check (`LakehouseLocationGuard.RowBelongsToTenant`).

---

### 2.3 Repudiation (Audit Integrity)

* **Threat 3.1: Untracked Policy Modifications or Access Grants**
  * *Mitigation:* Mandatory **Four-Eyes Principle (Separation of Duties)** on sensitive mutations. Consents affecting classified or restricted tables require independent approval by an authorized data owner. Governance mutations are recorded in the append-only audit trail; the audit record is written after the mutation in a separate transaction (Tier-A events synchronously, query events asynchronously), so a failure between both leaves a mutation without an audit record (R2-3, *partly open*: no write-ahead intent record for mutations). Query events can be committed synchronously with `Audit:SynchronousQueryAudit=true`; otherwise a hard crash loses at most `Audit:QueryAuditChannelCapacity` (default 5000) in-flight query events. Four-eyes for ITSM-governed requests is enforced by the repository on the approver identity (account), including the second step.
  * *Known limitation:* login events are not audited (E-10/A-5).

---

### 2.4 Information Disclosure (Data Leaks & Side Channels)

* **Threat 4.1: Cross-Tenant Data Leaks via Batch Operations**
  * *Attack Vector:* Batch operations (e.g., ReBAC batch checks) grouping items from multiple tenants leak permission states across tenants.
  * *Mitigation:* Multi-item endpoints (such as ReBAC batch evaluations) reject requests containing items from multiple tenants (`HTTP 400 Bad Request`). Zanzibar evaluators enforce tenant boundaries, and decision caches are partitioned per tenant.

* **Threat 4.2: Data Leakage via Regex Masking Failure**
  * *Attack Vector:* A regex masking pattern fails to match an unexpected input format, causing the sensitive value to pass unmasked.
  * *Mitigation:* **Fail-closed masking:** If a regex pattern does not match the input value or does not alter the sensitive value, the masking engine falls back to full redaction (`REDACTED`). *Known limitation:* a pattern that only matches part of the value leaves the unmatched remainder in clear text (R4-5); use anchored patterns (`^...$`).

* **Threat 4.3: Diagnostic & Stack Trace Leaks in Production**
  * *Attack Vector:* Unhandled exceptions leak database connection strings, internal table structures, or stack traces.
  * *Mitigation:* `ErrorSanitizingFilter` intercepts all GraphQL errors. In production, error messages are scrubbed to generic messages (`"An internal error occurred."`) with correlated telemetry request IDs. Stack traces and internal schema hints are omitted for GraphQL. *Known limitation:* the REST stored-procedure endpoints may return driver exception messages (e.g. `ArgumentException`, PostgreSQL `RAISE` texts) to the caller (P-10).

* **Threat 4.4: Unauthorized Introspection & Schema Scraping**
  * *Mitigation:* Standard introspection can be disabled via `Gateway:GraphQL:EnableIntrospection=false`. Hot Chocolate semantic introspection is disabled (`EnableSemanticIntrospection = false`) to comply with GraphQL core specification naming rules and prevent non-spec `__SchemaDefinition` types from leaking or breaking GraphQL-JS clients.

---

### 2.5 Denial of Service (Resource Exhaustion)

* **Threat 5.1: GraphQL Deep Query & Cyclical Query Attacks**
  * *Attack Vector:* Attackers submit nested cyclical queries (e.g. `user { orders { user { orders ... } } }`) to exhaust server memory and CPU.
  * *Mitigation:*
    1. *Execution Depth Limit:* Enforced via `AddMaxExecutionDepthRule(maxDepth)`.
    2. *Query Cost Analysis:* Dynamic calculation via `QueryCostAnalyzerRule`. Queries exceeding `MaxAllowedCost` or `MaxRootFieldsPerOperation` are rejected during validation before execution. Unpaginated relation lists are assumed to return the default list multiplier (10) rows and multiply their child cost by it, so nested lists grow multiplicatively (M-9).
    3. *Persisted Queries Allowlist:* In high-security mode (`PersistedQueriesOnly=true`), ad-hoc queries are rejected; only pre-registered SHA-256 operation hashes are executed.

* **Threat 5.2: In-Memory Token Revocation Explosion**
  * *Attack Vector:* Flooding revocation registries with dummy JTIs exhausts memory.
  * *Mitigation:* Revocations are stored with a TTL aligned to the token lifetime; lookups use pipelined single-key Redis `GET`s (cluster-safe). Revocations of non-ClusterAdmins are tenant-scoped; global revocations are restricted to canonical `ClusterAdmin` roles.
  * *Known limitation:* there is no upper bound on the number of stored revocations; the in-memory store only removes expired entries (retention up to 30 days). While Redis is unreachable only locally known revocations are enforced.

---

### 2.6 Elevation of Privilege

* **Threat 6.1: Service Principal & Worker Token Escalation**
  * *Attack Vector:* A service account intended for background batch queries executes administrative governance mutations.
  * *Mitigation:* Fine-grained claim scopes and RBAC roles (`ClusterAdmin`, `DataOwner`, `Analyst`, `Auditor`). Mutating operations require explicit role assignments.
  * *Known limitation:* CSRF / same-origin checks apply to selected paths only and a missing `Origin` header is accepted (A-9); do not treat them as a general protection of all mutations.

**Row-filter evaluators (E-5/E-6).** The in-memory evaluators (`GatewayExecutionService.FilterRows`, `StreamingRowFilterAstEvaluator`) do not coerce types: a quoted literal against a numeric column, an unquoted number against a text column and DataTable-only LIKE wildcards (`*`, `[`) make `FilterRows` return no rows; the streaming evaluator treats mixed-type comparisons as UNKNOWN, compares strings ordinally and never parses strings into numbers. Milvus filter values escape the backslash before the quote. `LIKE` patterns in policies are authored by administrators, so their `%`/`_` wildcards are intentional. Set `DataSources:RequireTenantColumn=true` to refuse tables without a tenant column.

### Review follow-up: R3-2, C-1 (rest), R4-4 (rest), E-7 (rest)

* **R3-2 (Iceberg raw access):** `LoadTable` and the credential endpoint share one consent guard. It fails closed when the consent services are missing, only accepts active consents of the caller's tenant for exactly the requested table, and releases raw metadata only when the effective access is "clear" for the whole table: no row filter, no masked or denied column, no catalog-sensitive column without an explicit clear rule, and no column that the consents leave unmentioned. The credential endpoint runs the same check before it reports that vending is not supported.
* **C-1 (EnvoyFilter):** the exported `EnvoyFilter` inserts a Lua filter ahead of `ext_authz` that removes every inbound `x-autheris-*` request header at the ingress gateway. Only values returned by the ext_authz service (`allowed_upstream_headers`) reach the upstream. Re-apply the exported filter after upgrading.
* **R4-4 (four-eyes by owner id):** self-approval and repeated approval are also decided on the resolved `DATA_OWNERS.id` (SID, account or e-mail spellings of the same owner resolve to one id), not only on string equality. Actors that are not registered data owners still fall back to the normalized string comparison.
* **E-7 (dbt reads):** the read endpoints `exposures`, `proposals` and `health` and the `validate-contract` check no longer accept a plain DataOwner role; they need GovernanceAdmin or ClusterAdmin (`validate-contract` and `health` also accept Developer).

### Review follow-up: SQL-1 (WebSQL dialect lexer differentials)

* *Attack Vector:* The WebSQL gateway parses with the Trino grammar, where `[` is array syntax and `'...'` is a string. SQL Server and SQLite lex `[...]` as a quoted identifier, so text the gateway sees as string content (e.g. `'[' ... ']--'`) could become real SQL on the backend, and a `--` inside it could comment out the appended row-filter, tenant and limit clauses (RLS, masking and table allow-list bypass).
* *Mitigation:* New token option `RejectBracketLexerDifferentials` (`RlsOptions` / `SqlTokenSecurityOptions`), forced on for SQL Server and SQLite targets. It rejects `[` / `]` tokens and string, unicode-string and quoted-identifier tokens containing `[`, `]`, `--` or `/*`. PostgreSQL keeps array constructors and subscripts. `UESCAPE` is rejected whenever escaped string literals are rejected. Backquoted identifiers use the same doubling rule in the gateway lexer as in SQLite. The check runs in the rewrite step (where the target dialect is known), before any SQL reaches the backend.
* *Related (OLAP):* The DuckDB OLAP sandbox validator now follows DuckDB quoting rules (backslash is not an escape) and blocks generator functions followed by whitespace before `(`.

### Review follow-up: HTTP endpoint authorization (A-1, A-2 and lows)

* **A-1 (CDC ingest):** only the canonical `ClusterAdmin` (`ClusterAdminPolicy`) may ingest events for other tenants, and even then the event must carry an explicit tenant. `PlatformAdmin` stays authorized to ingest but is tenant-scoped like `StreamingAdmin`/`GovernanceAdmin`: events of a foreign tenant are rejected with 403 and events without tenant take the caller tenant of the resolved request context.
* **A-2 (policy simulation replay):** `POST /api/governance/policy-simulation/replay` is open to `GovernanceAdmin`, `ClusterAdmin`, `PrivacyAdmin` and `Auditor`. A `DataOwner` may only replay with `TargetTable` set and only when registered as owner or delegate of that table.
* **FinOps budget:** `GET /api/v1/finops/budget/{tenant}` only lets canonical `ClusterAdmin` read other tenants; `GovernanceAdmin` and `BillingAdmin` are limited to the request tenant.
* **Role name lists:** `GatewayPolicies.HasAnyRole(principal, string[])` matches role names exactly. It no longer maps names to hierarchy levels (an `Analyst` used to satisfy `SchemaPublisherRoles`; `SchemaPublisher`/`PrivacyAdmin` satisfied `SchemaAdminRoles`). The typed `GatewayRole` overload keeps the hierarchy.
* **Declarative SQL listing:** `GET /api/v1/queries/` returns only name, summary and parameters to ordinary callers; `RawSql`, `DataSource` and `ReferencedTables` are visible to `GovernanceAdmin`/`ClusterAdmin`.
* **OData errors:** outside Development, missing tables and denied access both answer a generic 403 `ACCESS_DENIED` (no existence oracle, no Casbin text with SID/tenant).
* **OpenAPI:** the generated spec is not filtered per tenant, so `$openapi`, `{domain}/openapi.json|yaml`, the index routes and the per-table schema route require `GovernanceAdmin`/`ClusterAdmin` unless `OpenSchema` is enabled. Tenant-scoped roles (`DataOwner`, `SchemaAdmin`, `CatalogReader`) use the tenant-filtered `$metadata`. The cache keys on the exact domain; domains that are not `[A-Za-z0-9_-]{1,64}` are generated uncached.
* **Envoy export:** the `EnvoyFilter` now sets `http_service.path_prefix: /api/v1/envoy/check` and the check route is a catch-all over all HTTP methods (the suffix is the original path). Namespace, filter name, host (DNS labels), port (1-65535), path and timeout of the export are validated; invalid values answer 400. Re-apply the exported filter after upgrading.
