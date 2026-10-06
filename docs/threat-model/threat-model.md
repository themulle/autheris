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
    1. *Cryptographic Standards:* Outside Development only PBKDF2-HMAC-SHA256 (`$pbkdf2$<iters>$salt$hash`) with at least 210,000 iterations is accepted at startup validation. Argon2id (`$argon2id$v=19$m=...,t=...,p=...$salt$hash`) is supported for Development only and cannot be used in production.
    2. *Cost Boundaries:* The verifier accepts PBKDF2 iterations between 10,000 and 10,000,000 (the startup validation enforces the 210,000 minimum for configured users); the cost of the dummy verification for unknown users is not capped (A-6).
    3. *Timing Parity (partial):* Probing nonexistent usernames runs a dummy verification (`ConfigureDummyCost`) to reduce response-time differences. *Known limitation:* a timing oracle remains when users with different hash types are configured (A-7); the oracle is reduced, not eliminated.
    4. *Ingress-Aware Sliding Lockout:* Failed attempts are tracked against `(user, clientIp)` resolved via `IClientIpResolver` inspecting trusted ingress headers, preventing lockout of the proxy IP itself. Lockout state is synchronized across instances via `IDistributedCache`. *Known limitation:* the lockout is per (user, IP) and has no global limit per user (A-4/E-13).

* **Threat 1.5: Server-Side Request Forgery (SSRF) via Declarative Connectors**
  * *Attack Vector:* Attackers configure connector URLs pointing to cloud metadata endpoints (`169.254.169.254`, `metadata.google.internal`), Kubernetes API services, or internal microservices.
  * *Mitigation:* `DeclarativeHttpDataSourceExecutor` performs pre-request DNS resolution and blocks RFC 1918 private subnets (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`), link-local addresses, loopback, and metadata endpoints. Outbound HTTP redirects (`AllowAutoRedirect=false`) are inspected hop-by-hop with a maximum depth of 5 hops (`SendWithRedirectProtectionAsync`).

---

### 2.2 Tampering (Data Manipulation & Integrity)

* **Threat 2.1: SQL Injection via Dynamic GraphQL Filters**
  * *Attack Vector:* Injection of SQL clauses into `where: { ... }` or stored procedure arguments.
  * *Mitigation:* All column names and identifiers are validated against an alphanumeric regex whitelist (`^[a-zA-Z_][a-zA-Z0-9_]*$`) and verified against the registered `TableMetadata`. All filter values and parameter values are parameterized using native ADO.NET parameter binding (`@p0`, `$1`). Dynamic SQL generation in stored procedures requires explicit `@allow-dynamic-sql` annotation and DBA audit review; this check applies to endpoints with catalog validation (`validation: catalog`). *Known limitation:* endpoints with `validation: declared` (contract-first, outside Development only with `ProcedureEndpoints.AllowDeclaredValidation`) are not inspected by the gateway, the DBA review is the only control.

* **Threat 2.2: Audit Trail Tampering**
  * *Attack Vector:* An attacker with database access alters or deletes records in `AUDIT_LOG_ENTRIES` to cover tracks.
  * *Mitigation:* Cryptographic **HMAC-SHA256 hash chaining** (`entry_hash = HMACSHA256(secretKey, prev_hash || payload)`). Without access to the isolated Key Vault HMAC secret, subsequent hashes cannot be recalculated. The chain is verified on demand by `VerifyAuditHashChainAsync` (constant-time comparison); a signed anchor of the chain tail (`Audit:ChainAnchorPath`, preferably on a separate/shared volume or WORM storage) detects truncation of the end of the chain. With PostgreSQL the table is additionally append-only through triggers (UPDATE/DELETE/TRUNCATE rejected) and writers are serialised with an advisory lock; the runtime role should only hold INSERT/SELECT and the schema should be applied with a separate role (`GovernanceDb:MigrationConnectionString`).
  * *Known limitation:* the verification is not run periodically at runtime (E-11) - schedule it externally; without a configured anchor store, truncation of the chain tail is not detected across restarts (the gateway logs a warning).

* **Threat 2.3: Consent Cache Poisoning & Stale Permissions**
  * *Attack Vector:* Permissions modified in the catalog are not reflected on active nodes due to cached grants.
  * *Mitigation:* Two-tier cache invalidation with **Policy Epochs**. Every table policy modification increments an integer epoch in the governance store. Gateway nodes validate the epoch on every query resolution; mismatched epochs trigger local cache eviction. Several replicas require Redis (`Caching.Redis.Enabled`) for cluster-wide invalidation; this is enforced at startup outside Development.

* **Threat 2.4: Code Injection via Dynamic ABAC Rules**
  * *Attack Vector:* Dynamic Casbin ABAC rule injection via untrusted inputs leading to expression evaluation execution.
  * *Mitigation:* Dynamic rule tokens are sanitized before evaluation; Casbin evaluators enforce strict token allowlists, preventing arbitrary reflection or process execution.

* **Threat 2.5: Per-replica budgets, file-level-only lakehouse isolation, anonymous health probe (E-14, E-15, R3-4)**
  * *Mitigation E-14:* The FinOps monthly spend is a cluster-wide counter (atomic `INCRBY` in the shared state store, key per tenant and month, 40-day TTL), so N replicas no longer grant N times the budget. If the shared store is unreachable, spend is accounted locally and added on top of the last shared value (fail-safe towards enforcement, not towards a reset).
  * *Mitigation E-15:* Lakehouse scans enforce the tenant on every row, not only on files: the Delta executor adds a mandatory tenant predicate (a conflicting caller-supplied predicate is rejected) and drops rows/files whose stored tenant differs; the Iceberg executor no longer overwrites the stored tenant value with the session tenant and drops foreign rows. Comparison is ordinal and case-sensitive.
  * *Mitigation R3-4:* `/health/ready` (anonymous) is served from a 5 s single-flight cache, so the governance DB probe (SQLite: taken behind the repository lock) runs at most once per interval regardless of request volume.
  * *Known limitation:* lakehouse row data is still produced by the sample reader; a real Parquet reader must keep the per-row tenant check (`LakehouseLocationGuard.RowBelongsToTenant`).

---

### 2.3 Repudiation (Audit Integrity)

* **Threat 3.1: Untracked Policy Modifications or Access Grants**
  * *Mitigation:* Mandatory **Four-Eyes Principle (Separation of Duties)** on sensitive mutations. Consents affecting classified or restricted tables require independent approval by an authorized data owner. Governance mutations are recorded in the append-only audit trail; the audit record is written after the mutation in a separate transaction (Tier-A events synchronously, query events asynchronously), so a failure between both leaves a mutation without an audit record (R2-3). Four-eyes for ITSM-governed requests is enforced by the repository on the approver identity (account), including the second step.
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
