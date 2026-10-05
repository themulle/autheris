# STRIDE Threat Model & Security Architecture: Autheris Enterprise Gateway

**Status:** Normative / Production Standard  
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
  * *Mitigation:* Authoritative tenant claims (`tenant_id`, `tid`, `tenant`) derived from verified credentials strictly override headers. If a tenant claim is present on the principal, `TenantResolutionMiddleware` rejects any mismatched incoming `X-Tenant-ID` header. Unauthenticated or untrusted tenant headers are rejected outside development unless `TrustUpstreamTenant` is explicitly allowed with strict `AllowedTenantIds` whitelisting.

* **Threat 1.4: Password Brute-Force & User Enumeration (HTTP Basic Auth)**
  * *Attack Vector:* Attackers mount offline cracking attacks against stored hashes or exploit response-time differentials to enumerate valid usernames.
  * *Mitigation:*
    1. *Cryptographic Standards:* Argon2id (`$argon2id$v=19$m=...,t=...,p=...$salt$hash`) conforming to BSI TR-02102-1 and OWASP standards, alongside PBKDF2-HMAC-SHA256 (`$pbkdf2$<iters>$salt$hash`).
    2. *Cost Boundaries:* Argon2id parameters are strictly bounded ($8\,\text{MB} \le m \le 256\,\text{MB}$, $1 \le t \le 10$, $1 \le p \le 8$). PBKDF2 iterations enforce $10{,}000 \le \text{iters} \le 10{,}000{,}000$.
    3. *Dynamic Dummy Timing Parity:* Probing nonexistent usernames triggers an identical cryptographic workload (`ConfigureDummyCost`) matching active user hash algorithms, completely eliminating response-time enumeration oracles.
    4. *Ingress-Aware Sliding Lockout:* Failed attempts are tracked against `(user, clientIp)` resolved via `IClientIpResolver` inspecting trusted ingress headers, preventing cluster-wide lockout of proxy IPs (`10.0.0.1`). Lockout state is synchronized across instances via `IDistributedCache`.

* **Threat 1.5: Server-Side Request Forgery (SSRF) via Declarative Connectors**
  * *Attack Vector:* Attackers configure connector URLs pointing to cloud metadata endpoints (`169.254.169.254`, `metadata.google.internal`), Kubernetes API services, or internal microservices.
  * *Mitigation:* `DeclarativeHttpDataSourceExecutor` performs pre-request DNS resolution and blocks RFC 1918 private subnets (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`), link-local addresses, loopback, and metadata endpoints. Outbound HTTP redirects (`AllowAutoRedirect=false`) are inspected hop-by-hop with a maximum depth of 5 hops (`SendWithRedirectProtectionAsync`).

---

### 2.2 Tampering (Data Manipulation & Integrity)

* **Threat 2.1: SQL Injection via Dynamic GraphQL Filters**
  * *Attack Vector:* Injection of SQL clauses into `where: { ... }` or stored procedure arguments.
  * *Mitigation:* All column names and identifiers are validated against an alphanumeric regex whitelist (`^[a-zA-Z_][a-zA-Z0-9_]*$`) and verified against the registered `TableMetadata`. All filter values and parameter values are parameterized using native ADO.NET parameter binding (`@p0`, `$1`). Dynamic SQL generation in stored procedures requires explicit `@allow-dynamic-sql` annotation and DBA audit review.

* **Threat 2.2: Audit Trail Tampering**
  * *Attack Vector:* An attacker with database access alters or deletes records in `AUDIT_LOG_ENTRIES` to cover tracks.
  * *Mitigation:* Cryptographic **HMAC-SHA256 hash chaining** (`entry_hash = HMACSHA256(secretKey, prev_hash || payload)`). Without access to the isolated Key Vault HMAC secret, subsequent hashes cannot be recalculated. Verification utilities validate the chain integrity using constant-time comparison.

* **Threat 2.3: Consent Cache Poisoning & Stale Permissions**
  * *Attack Vector:* Permissions modified in the catalog are not reflected on active nodes due to cached grants.
  * *Mitigation:* Two-tier cache invalidation with **Policy Epochs**. Every table policy modification increments an integer epoch in the governance store. Gateway nodes validate the epoch on every query resolution; mismatched epochs trigger immediate local cache eviction.

* **Threat 2.4: Code Injection via Dynamic ABAC Rules**
  * *Attack Vector:* Dynamic Casbin ABAC rule injection via untrusted inputs leading to expression evaluation execution.
  * *Mitigation:* Dynamic rule tokens are sanitized before evaluation; Casbin evaluators enforce strict token allowlists, preventing arbitrary reflection or process execution.

---

### 2.3 Repudiation (Audit Integrity)

* **Threat 3.1: Untracked Policy Modifications or Access Grants**
  * *Mitigation:* Mandatory **Four-Eyes Principle (Separation of Duties)** on sensitive mutations. Consents affecting classified or restricted tables require independent approval by an authorized data owner. All governance mutations are committed atomically with an append-only audit trail record.

---

### 2.4 Information Disclosure (Data Leaks & Side Channels)

* **Threat 4.1: Cross-Tenant Data Leaks via Batch Operations**
  * *Attack Vector:* Batch operations (e.g., ReBAC batch checks) grouping items from multiple tenants leak permission states across tenants.
  * *Mitigation:* Multi-item endpoints (such as ReBAC batch evaluations) reject requests containing items from multiple tenants (`HTTP 400 Bad Request`). Zanzibar evaluators enforce tenant boundaries, and decision caches are partitioned per tenant.

* **Threat 4.2: Data Leakage via Regex Masking Failure**
  * *Attack Vector:* A regex masking pattern fails to match an unexpected input format, causing the sensitive value to pass unmasked.
  * *Mitigation:* **Fail-closed masking:** If a regex pattern does not match the input value or does not alter the sensitive value, the masking engine falls back to full redaction (`***REDACTED***`).

* **Threat 4.3: Diagnostic & Stack Trace Leaks in Production**
  * *Attack Vector:* Unhandled exceptions leak database connection strings, internal table structures, or stack traces.
  * *Mitigation:* `ErrorSanitizingFilter` intercepts all GraphQL errors. In production, error messages are scrubbed to generic messages (`"An internal error occurred."`) with correlated telemetry request IDs. Stack traces and internal schema hints are strictly omitted.

* **Threat 4.4: Unauthorized Introspection & Schema Scraping**
  * *Mitigation:* Standard introspection can be disabled via `Gateway:GraphQL:EnableIntrospection=false`. Hot Chocolate semantic introspection is disabled (`EnableSemanticIntrospection = false`) to comply with GraphQL core specification naming rules and prevent non-spec `__SchemaDefinition` types from leaking or breaking GraphQL-JS clients.

---

### 2.5 Denial of Service (Resource Exhaustion)

* **Threat 5.1: GraphQL Deep Query & Cyclical Query Attacks**
  * *Attack Vector:* Attackers submit nested cyclical queries (e.g. `user { orders { user { orders ... } } }`) to exhaust server memory and CPU.
  * *Mitigation:*
    1. *Execution Depth Limit:* Enforced via `AddMaxExecutionDepthRule(maxDepth)`.
    2. *Query Cost Analysis:* Dynamic calculation via `QueryCostAnalyzerRule`. Queries exceeding `MaxAllowedCost` or `MaxRootFieldsPerOperation` are rejected during validation before execution.
    3. *Persisted Queries Allowlist:* In high-security mode (`PersistedQueriesOnly=true`), ad-hoc queries are rejected; only pre-registered SHA-256 operation hashes are executed.

* **Threat 5.2: In-Memory Token Revocation Explosion**
  * *Attack Vector:* Flooding revocation registries with dummy JTIs exhausts memory.
  * *Mitigation:* Token revocations are bounded per tenant and cluster-wide. High-performance lookups utilize Redis `MGET` operations with TTL expirations aligned with token lifetimes. Global revocations are strictly restricted to canonical `ClusterAdmin` roles.

---

### 2.6 Elevation of Privilege

* **Threat 6.1: Service Principal & Worker Token Escalation**
  * *Attack Vector:* A service account intended for background batch queries executes administrative governance mutations.
  * *Mitigation:* Fine-grained claim scopes and RBAC roles (`ClusterAdmin`, `DataOwner`, `Analyst`, `Auditor`). Mutating operations require explicit role assignments and valid CSRF / same-origin session tokens.
