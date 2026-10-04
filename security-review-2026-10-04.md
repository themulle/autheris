# Autheris Security Review — 2026-10-04

**Scope:** every git-tracked file at commit `e59c2e2` (`src/`, `deploy/`, `.github/`, configs and docs). That is about 75k lines of C# in 529 files.
**Method:** I read the code by hand in five areas: authentication, authorization/governance, SQL and query injection, SSRF/integrations, and MCP/configuration/supply chain. I re-checked the most important findings against the source myself. I did not run any dynamic tests.
**Overall:** the core engine is solid. That covers consent resolution, the SQL rewriter, parameterization, SSRF pinning in the declarative HTTP executor, plugin hash pinning and the startup guards. The remaining risk sits in the **peripheral endpoints**, which skip that pipeline: Envoy ext_authz, Iceberg REST, CloudEvents, ITSM, FinOps/Backstage. It also sits in **headers that are trusted without verification** (ForwardAuth tenant, Envoy identity).

| Severity | Count |
|---|---|
| Critical | 1 |
| High | 4 |
| Medium | 10 |
| Low / Hardening | 17 |

---

## Critical

### C-1 — Envoy ext_authz takes the identity from headers the client controls
- **Where:** `src/Autheris.Application/Mesh/Services/EnvoyExtAuthzService.cs:124-178` (tenant, principal, roles), the unsigned JWT parsing at `:307-357`, the generated EnvoyFilter at `:399-409`, and `src/Autheris.Api/Endpoints/EnvoyExtAuthzEndpoints.cs:20-30`.
- **Problem:** `/api/v1/envoy/authz` and `/api/v1/envoy/check` never look at `context.User`.
  - The subject comes from `x-autheris-principal`, `x-user-id`, or an unverified JWT payload.
  - Groups come from `x-roles`, the tenant from `x-tenant-id`, and the IP from `x-forwarded-for`.
  - The generated Istio config forwards exactly these client headers (`allowed_headers`) to the check, and passes `x-autheris-*` on to the upstream.
- **Exploit:** an authenticated user sends:

  ```
  x-autheris-principal: <admin SID>
  x-roles: <privileged group>
  x-tenant-id: <other tenant>
  ```

  The policy check (PDP) evaluates the request as that identity. The upstream then receives `x-autheris-principal`, `x-autheris-tenant` and `x-autheris-rls-filter` for the forged identity.
- **Without a mesh:** calling the endpoint directly still works as a cross-tenant policy oracle. It leaks the RLS filter SQL and the deny reasons.
- **Fix:**
  - Derive identity only from the validated `ClaimsPrincipal`, or from an mTLS `source.principal` restricted to the Envoy SPIFFE ID.
  - Remove the header and unsigned-JWT fallbacks.
  - Restrict the endpoint to a mesh service identity.
  - Remove `x-autheris-principal` and `x-roles` from `allowed_headers`, and strip incoming `x-autheris-*` at the edge.
  - Stop returning the RLS SQL.

## High

### H-1 — ForwardAuth accepts a client-supplied tenant (`X-Forwarded-Tenant`)
- **Where:** `src/Autheris.Api/Security/ForwardAuthAuthenticationHandler.cs:244-268`, `GatewayOptions.cs` (`AllowedTenantIds` defaults to empty), `docs/configuration-guide.md:831-839`, `deploy/containers/reverse-proxy/nginx.conf:121-124`.
- **Problem:** Traefik only overwrites the headers listed in `authResponseHeaders`. The documented list does not include `X-Forwarded-Tenant`, so a header the client sends reaches the gateway unchanged.
  - The request still passes the trusted-proxy and shared-secret checks, because Traefik adds those.
  - With `AllowedTenantIds` empty, any tenant is accepted and becomes the verified `tenant_id` claim.
- **Impact:** a full cross-tenant read for any ForwardAuth user in a multi-tenant deployment.
- **Fix:**
  - Read the tenant only when the configuration explicitly says the proxy asserts it.
  - Require `AllowedTenantIds`, or a user→tenant mapping, in that case.
  - Add the tenant header to the documented `authResponseHeaders` and to the nginx config.

### H-2 — Cross-site WebSocket hijacking on `/graphql` (needs confirmation in your browsers)
- **Where:** `GatewayApplicationBuilderExtensions.cs:104-106` (the CSRF/Origin check covers only POST, or GET with `?query=`), `:306` (`UseWebSockets()` without `AllowedOrigins`), and `WebSocketAuthInterceptor.cs:116-136` (falls back to the identity from the HTTP upgrade handshake).
- **Exploit:**
  1. A victim is signed in with an ambient credential: Kerberos/Negotiate (the production default), cached Basic, or ForwardAuth proxy cookies.
  2. The victim opens an attacker's page, which runs `new WebSocket("wss://<gateway>/graphql","graphql-transport-ws")`.
  3. The page sends an empty `connection_init`, then runs queries and mutations as the victim.
- **Fix:**
  - Validate `Origin` on the upgrade against `GraphQL.TrustedOrigins`.
  - Require a token in `connection_init` whenever an `Origin` header is present.
  - Consider allowing only subscriptions over WebSocket.

### H-3 — The Iceberg REST catalog skips consent, ReBAC and tenant checks
- **Where:** `src/Autheris.Extensions/Lakehouse/Services/IcebergRestCatalogFederationService.cs:44-160` and `IcebergRestCatalogEndpoints.cs`, which are mapped unconditionally.
- **Problem:** the only check is `IsAuthenticated`.
  - `ListNamespaces` and `ListTables` ignore the tenant.
  - When the table lookup fails, `LoadTable` falls back to *any* table with the same schema and name across tenants (`:93-101`).
  - The response returns the physical `metadata-location`.
  - Credential vending is a placeholder: random keys, and a session token that is just `base64(tenant:ns:table:exp)`, unsigned and forgeable.
- **Impact:**
  - Today: catalog and storage-location disclosure across tenants.
  - Once real STS/SAS vending is wired in: direct file reads that bypass RLS and masking.
- **Fix:**
  - Require the consent decision (no row filter, no masked columns) plus ReBAC.
  - Scope every lookup to the caller's tenant and remove the fallback.
  - Return 501 for credential vending until it is real.

### H-4 — An unknown client IP is treated as loopback, so IP-based ABAC fails open
- **Where:** `src/Autheris.Application/Connectors/CrossDomain/DefaultCrossDomainAccessResolver.cs:91-93`, which is used by `/api/v1/olap/query`. The same pattern appears in `GatewayExecutionService.cs:215-218, 781-784` (when no IP resolver is registered) and `AiDataGuardrailService.cs:221`.
- **Problem:** if there is no `ip` claim, the code falls back to `IPAddress.Loopback`. `GovernedSqlExecutionService.ResolveClientIp` handles this correctly by using `IPAddress.None`.
- **Impact:** external users satisfy "internal network only" Casbin rules, or slip past "deny external" rules.
- **Fix:** inject `IClientIpResolver` everywhere and fall back to `IPAddress.None`. Never trust an `ip` claim from the token.

## Medium

- **M-1. Tenant-scoped admin aliases are treated as ClusterAdmin.**
  - **Where:** `GatewayRole.cs:29-33`, `EndpointSecurity.cs:65-72`.
  - **Problem:** `GatewayAdmin` and `PlatformAdmin` map to ClusterAdmin, and `PrivacyAdmin` maps to GovernanceAdmin, in the role hierarchy used by `HasAnyRole`.
  - **Impact:** a tenant admin can approve HitL tickets for any tenant and set global sunsetting rules. They can also revoke tokens for any subject (`/api/admin/tokens/revoke`, revocation keys are global), including ClusterAdmins.
  - **Fix:** map the aliases to a separate tenant-admin role.
- **M-2. ITSM approval bypasses four-eyes and separation of duties.**
  - **Where:** `ItsmWebhookHandler.cs:347-350` calls `ActivateConsentAsync` directly (`SqliteGovernanceRepository.Consent.cs:698-776`).
  - **Problem:** it ignores `requires_four_eyes`, does not check approver ≠ requester, and does not check data-owner authority.
  - **Fix:** route the approval through `ApproveConsentRequestStepAsync`.
- **M-3. The self-approval check compares only the primary SID.**
  - **Where:** `MutationTypes.cs:384` (`req.RequesterSid == approverSid`).
  - **Problem:** a user who authenticates via Kerberos (S-1-5-21-…) and via OIDC (`oid`) can approve their own request, or supply both four-eyes approvals. This depends on more than one auth scheme being enabled.
  - **Fix:** reuse `HitLStepUpApprovalService.IsSameIdentity`.
- **M-4. Cross-tenant reads by tenant-scoped admins.**
  - **Where:**
    - `FinOpsEndpoints.cs:38-49`: `?tenantId=` is honoured for GovernanceAdmin and BillingAdmin, and omitting it returns all tenants.
    - `GovernanceEndpoints.cs:295-302`: the EU AI Act certificate.
    - `:216-231`: the DP budget reset accepts any `clientId`.
  - **Fix:** allow a foreign tenant only for `IsCanonicalClusterAdmin`.
- **M-5. CloudEvents subscriptions are open to any authenticated user.**
  - **Where:** `StreamingCdcEndpoints.cs:89-130`.
  - **Problems:**
    - There is no role check, and GET returns `HmacSecret`.
    - The `Id` is chosen by the client, so a POST can overwrite another user's subscription.
    - The store is unbounded in memory, which is a DoS today.
    - Tenant falls back to `"default"`.
    - The dispatcher's SSRF check (`CloudEventWebhookDispatcher.IsValidTargetUrl`) does no DNS resolution, misses IPv4-mapped IPv6, follows redirects and does no pinning. It is latent: there is no caller yet.
  - **Fix:**
    - Require a role, generate the Id on the server, never return the secret, and cap entries.
    - Use `AddSecureOutboundHandlers` and `EgressUrlPolicy.ValidateResolvedAsync` for delivery.
- **M-6. Ingested OpenAPI specs forward user bearer tokens to the host the spec names.**
  - **Where:** `OpenApiIngestionService.cs:58-66, 197-203`, with `AuthMode = ForwardBearerToken` as the default.
  - **Impact:** a malicious or compromised spec with `servers[0].url = https://attacker` collects every querying user's gateway JWT, including ClusterAdmins'.
  - **Fix:**
    - Default to `AuthMode.None`.
    - Require explicit token forwarding with a host allowlist.
    - Prefer an OBO token exchange.
- **M-7. Catalog enumeration through side endpoints.**
  - **Where:** Backstage export, Flight SQL `GetTables`, the Iceberg list endpoints, and `/api/schema-registry/*`.
  - **Problem:** these expose every table and column with its sensitivity tags across tenants, while GraphQL `catalog` filters by consent.
  - **Fix:** apply the same discovery filter.
- **M-8. ForwardAuth SID collisions (needs confirmation of the IdP's username policy).**
  - **Where:** `ForwardAuthAuthenticationHandler.cs:168-170, 207-209`.
  - **Problem:**
    - A username that starts with `S-1-5-21-FORWARD-` is passed through unchanged.
    - User `grp-finance` maps to the same SID as group `Finance`.
  - **Fix:** use disjoint, escaped or hashed namespaces for users and groups.
- **M-9. The query cost analyzer adds nested list costs instead of multiplying them.**
  - **Where:** `QueryCostAnalyzerRule.cs:282-287`.
  - **Problem:** `a(first:100){b(first:100)}` scores about 2k but returns about 10k rows.
  - **Fix:** multiply by `effectiveRows` using saturating arithmetic.
- **M-10. NuGet vulnerability audit is switched off.**
  - **Where:** `Directory.Build.props:10` suppresses `NU1901-NU1904`, and there are no lock files.
  - **Fix:**
    - Enable `NuGetAuditMode=all` and `RestorePackagesWithLockFile`.
    - Fail the build on NU1902-1904.
    - Add `dotnet list package --vulnerable --include-transitive` to CI.

## Low / Hardening

1. **Federation subgraph clients** (`FusionGatewayExtensions.cs:44-49`) use the default handler: redirects are followed after the SSRF check and the IP is not pinned. Use `SecureOutboundHttp.CreatePrimaryHandler`.
2. **When `HTTPS_PROXY` is set**, the DNS-rebinding defence only checks the proxy endpoint (`SecureOutboundHttp.cs:163-170`).
3. **Traffic shadowing** (off by default) has three problems:
   - It replays unauthenticated `/…/graphql` requests.
   - It copies client identity headers verbatim, using a denylist rather than an allowlist.
   - It builds the target URI from the raw path, so the host may be replaceable. That needs a runtime test.
4. **Catalog webhook legacy signature mode**, together with per-process deduplication, allows replay. Remove legacy mode and move deduplication to Redis `SET NX`.
5. **Basic-auth lockout** is keyed on (user, exact IP). IPv6 is not grouped into /64 and there is no per-account backoff.
6. **JWT `ValidAudiences` includes `ClientId`**, so ID tokens may be accepted. The `SidClaimType`, `GroupsClaimType` and `RolesClaimType` options are never read.
7. **Development environment = full bypass outside containers.** TestAuth turns on in Development and gives ClusterAdmin through `X-Test-User-Sid`. The guard only runs when `DOTNET_RUNNING_IN_CONTAINER` is set. Add an explicit opt-in that is independent of the host.
8. **`KnownNetworks` only rejects `/0`**, and `ForwardAuth.TrustedNetworks/TrustedProxies` are not validated at all.
9. **Flight SQL ticket signing key** falls back to `"default-secret"` and is not bound to a user (`ArrowFlightSqlServer.cs:41-44`). It is a stub today.
10. **`Mcp.MaxResultRows` and `Mcp.RequirePiiMasking` are never enforced.** The MCP executor catches all exceptions after validation and continues (`GatewayMcpQueryExecutor.cs:136-139`), and it grants a default `Reader` role.
11. **Nitro and schema download (`?sdl`)** may stay enabled in production. `EnableBananaCakePop` is never read; set `Tool.Enable=false` and `EnableSchemaRequests=false`.
12. **Incremental-delivery stream slots** are keyed per tenant, so one user can starve the rest of the tenant (`IncrementalDeliveryMiddleware.cs:43-45`).
13. **Secret references are logged** and put into exception messages (`DeclarativeHttpDataSourceExecutor.cs:550-559`). Declarative HTTP responses are also buffered with no size limit (`:146-147`).
14. **GitHub Actions** are pinned by tag rather than SHA, including `softprops/action-gh-release` running with `contents: write`. `ci.yml` and `policy-lint.yml` have no `permissions:` block.
15. **Hard-coded benchmark credentials**:
    - `deploy/podman-compose.yaml`: SA, Postgres, MinIO and Grafana passwords. The database ports are bound to 0.0.0.0.
    - `Containerfile`: an HMAC key in `ENV`, and the benchmark config copied over `appsettings.Production.json`.
    - The nginx ForwardAuth secret.
    - The docs use realistic-looking example passwords.
16. **SQL policy maps are keyed case-insensitively** while the analyzer treats quoted PostgreSQL names as case-sensitive (`GovernedSqlExecutionService.cs:268-273, 426-437`). This needs two catalogued tables that differ only by case. Needs confirmation.
17. **Latent or dead code to fix before enabling it:**
    - `CrossDomainJoinEngine`: the FK join leaks masked values.
    - `DbtMetadataIngestionService.IngestManifestFileAsync`: path denylist instead of containment.
    - `JwtSocketTokenValidator`: no signing keys outside test, so it fails closed.
    - Backstage YAML escaping is incomplete.
    - The tenant RLS literal (`GovernedSqlExecutionService.cs:411`) is safe today, but it should be a bound parameter.
    - `DynamicExpresso` `eval(sub_rule)` is guarded by a character allowlist and a token denylist. Consider a dedicated expression evaluator.

---

## Verified OK (selection)
- **SQL rewriter (TrinoSqlEngine):**
  - Rejects comments, `E''` strings, dollar quoting and multiple statements.
  - Applies a per-dialect function allowlist plus a denylist (`pg_*`, `xp_`, `openrowset`, `dblink`, `load_extension`, …).
  - CTEs cannot shadow real tables, and any unresolved table gets `1 = 0`.
- **Parameterization:**
  - RLS filters (`RowFilterSqlBuilder`), GraphQL filters (`SqlFilterProvider`) and SQL endpoint parameters are all bound parameters.
  - Column names are validated against the catalog, OData exposes only validated `$select`/`$top`/`$skip`, and persistence uses parameters.
- **DuckDB OLAP:** `enable_external_access=false` with locked configuration, loaded only with rows that already passed governance.
- **Consent engine:**
  - Deny-by-default, with expiry enforced both at load time and in `IsActive`.
  - Revocation bumps the epoch and invalidates the cache, and cache keys include tenant, SID and group hash.
  - The four-eyes rules in the repository hold.
- **Masked columns** cannot be filtered on, and the `tenant_id` predicate is always applied.
- **Authentication:**
  - ForwardAuth checks the raw TCP peer and compares its secret in fixed time.
  - JWT validation checks issuer, audience, lifetime and signing key.
  - Basic auth uses PBKDF2 with ≥210k iterations and parity hashing for unknown users.
  - TestAuth is never registered outside Development.
- **Declarative HTTP egress:**
  - DNS is resolved and pinned via `ConnectCallback`, and IPv4-mapped addresses are normalized.
  - No auto-redirects, path segments are escaped, and sensitive headers are denied.
- **Plugins:** SHA-256 hashes from config, the verified bytes are the bytes loaded, and loading fails closed outside Development.
- **Webhooks (ITSM, OpenMetadata, dbt):** constant-time HMAC over timestamp plus payload, with deduplication.
- **Errors and transport:**
  - Errors are sanitized outside Development.
  - CSP, HSTS and security headers are set.
  - Body limits are 2 MB (1 MB for MCP), and regexes have timeouts where it matters.
  - The container runs as non-root.
- **Secrets:** no private keys, cloud keys, tokens, or certificate/DB files are committed.

## Coverage notes
- The SQL-injection pass covered:
  - the Trino rewriter and the GovernedSqlExecutionService;
  - filter builders, SQL endpoints, OData, DuckDB, Arrow export and persistence `CommandText` sites.
- `StreamingRowFilterAstEvaluator`, `SingleQueryAstCompiler` and the CrossDomain connectors got only a light review.
- Everything here comes from reading code. Before closing H-2, M-3, M-8 and Low-3, verify them with a runtime test.

## Suggested order of work
1. **C-1, H-1:** only trust identity and tenant from verified sources. These are small code changes.
2. **H-2:** add the WebSocket Origin check.
3. **H-3, M-5:** gate Iceberg REST and CloudEvents behind the governance pipeline and roles, or don't map them until they are finished.
4. **H-4, M-1, M-4:** remove the loopback fallback, keep one definition of "global admin", and enforce tenant binding on admin endpoints.
5. **M-2, M-3:** make every approval path use the same SoD and identity comparison.
6. **M-10:** turn the NuGet audit back on, then go through the Low list.
