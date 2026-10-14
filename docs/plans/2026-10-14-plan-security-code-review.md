# Security Code Review — Full Codebase (Follow-up to the 2026-10-13 AppSec Audit)

**Document ID:** `PLAN-SECURITY-REVIEW-2026-10-14`
**Date:** 2026-10-14
**Role:** Application Security Engineer (`csharp-security-expert`)
**Reviewed revision:** `4cb0ae4a` (`feat/generic-plugin-events-and-webhooks`, merged to `main` via PR #71). Line numbers refer to that revision.
**Predecessor:** `2026-10-13-plan-security-audit-and-remediation.md` (`PLAN-SECURITY-AUDIT-2026-10-13`, on `main`)
**Parent document:** [`00-master-plan-overview.md`](00-master-plan-overview.md)
**Detailed per-area reports:** [`2026-10-14-security-code-review/`](2026-10-14-security-code-review/)

---

## 1. Executive Summary

A full, read-only security code review of all seven production projects (`Autheris.Api`, `Autheris.Application`, `Autheris.Domain`, `Autheris.Infrastructure`, `Autheris.Extensions`, `Autheris.GraphQL`, `TrinoSqlEngine`) plus deployment configuration was performed one day after the remediation commits `3014f659`, `6ad16519` and `f7309860`. The review was split into eight areas, each traced end to end (endpoint → service → repository / emitted SQL). The `TrinoSqlEngine` findings were confirmed with a throwaway harness against SQLite; several URI-resolution claims were confirmed on .NET 10.

**Overall assessment:** No new Critical issue was found. The core enforcement stack (Casbin ABAC deny-first, ReBAC, RLS injection in both SQL engines, masked-column guardrails, SSRF egress pinning, audit canonicalization, plugin hash verification) held up under review. However, **several fixes from the 2026-10-13 audit are incomplete** — the exploit path was closed for the variant named in the original finding but not for sibling variants (MCP Four-Eyes, classification workflow, TOTP, rate limiting, shadowing/probe SSRF, ITSM ticket lookup). One **regression** was introduced by the OData latency optimisation (`4da8e788`).

| Severity | New | Incomplete fix / regression of a 2026-10-13 finding | Total |
| :--- | :---: | :---: | :---: |
| Critical | 0 | 0 | **0** |
| High | 2 | 4 | **6** |
| Medium | 15 | 11 | **26** |
| Low | 18 | 6 | **24** |

Deployment and supply chain add 1 High, 3 Medium and 5 Low findings (section 5). Of the 69 items from the 2026-10-13 audit, only 15 are fully fixed (section 4).

Top priorities (P0):

1. **SR-H-01** — MCP Four-Eyes step-up evaluates the wrong table for `query_sql` / `invoke_api` (incomplete fix of `SEC-HIGH-10`).
2. **SR-H-02** — MCP admin tools accept `TenantAdmin` but mutate global catalog state (incomplete fix of `SEC-HIGH-09` / `SEC-HIGH-05`).
3. **SR-H-03** — Datasource `Auth.SecretRef` lets an admin read or overwrite arbitrary host secrets and send them to an attacker host.
4. **SR-H-04** — Classification workflow trusts a client-supplied proposal and reviewer, has no state precondition (incomplete fix of `SEC-CRIT-01` / `SEC-HIGH-06`).
5. **SR-H-05** — Async SQL job `MaxRows`/`Timeout` are caller-controlled with no server cap (incomplete fix of `SEC-MED-18`).
6. **SR-H-06** — Tenant data-source allowlists are not enforced on cross-source, OLAP and HTTP-table paths.

---

## 2. Scope & Method

| Area | Scope | Detailed report |
| :--- | :--- | :--- |
| API | `src/Autheris.Api` — ~200 `Map*` endpoints enumerated, auth handlers, middleware order, MCP HTTP/OAuth, portal HTML | [`api.md`](2026-10-14-security-code-review/api.md) |
| Governance & MCP | `Application/{Governance,Security,Policy,Audit,Mcp,Extensibility,State}`, `Domain` | [`governance-mcp.md`](2026-10-14-security-code-review/governance-mcp.md) |
| Data & SQL paths | `Application/{Data,Sql,Federation,Olap,Procedures,VirtualFilters,SqlEndpoints,Jobs,OData,Streaming,Services}` | [`data-sql.md`](2026-10-14-security-code-review/data-sql.md) |
| Outbound, plugins, webhooks | `Application/{Catalog,Connectors,Webhooks,Events,Plugins,Diagnostics,Docs,...}`, `Infrastructure/Plugins` (incl. new F-EXT-01 subsystem) | [`outbound-plugins.md`](2026-10-14-security-code-review/outbound-plugins.md) |
| Infrastructure | Repositories, audit WORM chain, Redis/Garnet, crypto, connectors | [`infrastructure.md`](2026-10-14-security-code-review/infrastructure.md) |
| Extensions & GraphQL | OData, Lakehouse, catalog/ITSM clients, CDC, HotChocolate schema, federation, subscriptions | [`extensions-graphql.md`](2026-10-14-security-code-review/extensions-graphql.md) |
| SQL engine | `TrinoSqlEngine` hand-written security code (harness-verified) | [`sqlengine.md`](2026-10-14-security-code-review/sqlengine.md) |
| Remediation & deployment | Status of all 60 `SEC-*` findings + `B-10..B-18`; Docker, Helm, compose, config, supply chain | [`remediation-deploy.md`](2026-10-14-security-code-review/remediation-deploy.md) |

Standards: OWASP Top 10 (2021), OWASP API Security Top 10 (2023), OWASP Top 10 for LLM Applications (2025), Autheris Zero-Trust / Fail-Closed mandates (`AGENTS.md`).

Confidence levels: **Confirmed** = code path traced end to end (and, where marked, executed in a harness); **Likely** = mechanism confirmed, impact depends on configuration or on code not fully traced.

---

## 3. Findings Matrix

### 3.1 High

| ID | Title | Location | Relation | Confidence |
| :--- | :--- | :--- | :--- | :--- |
| **SR-H-01** | MCP Four-Eyes / MCP ABAC pre-check evaluate the wrong table: `query_sql` uses the first regex `FROM` match (JOINs, comments, subqueries ignored); `invoke_api` always checks the constant `governance.api.invoke` although it reads `/api/v1/data/...` | `Application/Mcp/Services/McpDatasetTools.cs:65-135`, `AiDataGuardrailService.cs:433-470` | Incomplete fix of `SEC-HIGH-10` | Confirmed |
| **SR-H-02** | MCP admin-tool gate admits `TenantAdmin`, but `AccessPlanningService` takes no tenant: `admin_set_dataset_state` flips the shared catalog for all tenants and audits under `LegacySingleTenant`; `ApplyAccess` uses the dataset domain as ReBAC tenant | `AiDataGuardrailService.cs:231-247`, `Governance/Services/AccessPlanningService.cs:256,278,342,374-420` | Incomplete fix of `SEC-HIGH-09`, `SEC-HIGH-05` | Confirmed |
| **SR-H-03** | `Auth.SecretRef` on datasource registration is used verbatim as vault key: `SetSecret` writes the in-memory override (checked first) for any name, e.g. `forwardauth-secret`; without `Value`, the datasource test resolves any `IConfiguration` key / env var / `file:/run/secrets/*` and sends it to the attacker-controlled `BaseUrl` | `Catalog/Services/CatalogDiscoveryService.cs:337-349`, `DataCatalog/Services/OpenApiIngestionService.cs:196-230`, `Infrastructure/Security/DefaultEnvironmentSecretProvider.cs:21-46` | New | Confirmed |
| **SR-H-04** | Classification workflow: `/transition` accepts a client-supplied proposal and reviewer identity; no state precondition; no distinct-approver tracking; empty `DataOwnerSid` lets any `DataOwner` approve/reject; a reviewer can finalize without stage 1 — a single GovernanceAdmin can seal a classification in one call | `Api/Endpoints/GovernanceClassificationEndpoints.cs:71-224`, `Governance/Services/ClassificationWorkflowStateMachine.cs:62-266` | Incomplete fix of `SEC-CRIT-01`, `SEC-HIGH-06` | Confirmed |
| **SR-H-05** | Async SQL job: `RowLimit = new SqlRowLimit(request.MaxRows, request.MaxRows)` and client `Timeout` override all WebSQL/transport caps; full result buffered in memory | `Jobs/Services/AsyncQueryJobBackgroundWorker.cs:90-93`, `AsyncQueryJobManager.cs:61-111` | Incomplete fix of `SEC-MED-18` | Confirmed |
| **SR-H-06** | `WebSql.TenantDataSourceAllowlist` / `AllowedDataSources` enforced only on the single-catalog path; cross-source, `HttpDeclarative` and DuckDB OLAP reads skip them (requires `CrossSource.Enabled` or OLAP enabled, plus a consent grant) | `Sql/Services/GovernedSqlRewriter.cs:282-310`, `CrossSourceQueryRouter.cs:72-135`, `FederatedDuckDbExecutionService`, `Api/Endpoints/DuckDbOlapEndpoints.cs` | Related to `SEC-HIGH-13` | Confirmed (control absent) |

### 3.2 Medium

| ID | Title | Location | Relation |
| :--- | :--- | :--- | :--- |
| SR-M-01 | `/portal/2fa/enroll/verify` (all environments) persists a client-chosen TOTP seed; `/api/v1/governance/2fa/verify-enrollment` accepts a client seed when no cluster state provider is registered | `Api/Endpoints/DevPortalEndpoints.cs:48-128`, `GovernanceApiEndpoints.cs:109-182` | Incomplete fix of `SEC-HIGH-01` |
| SR-M-02 | Pre-auth IP limiter skips any `Authorization: Bearer` header; the compensating IP check in `PostAuthSidRateLimitingMiddleware` runs **after** `UseAuthorization`, so it never runs for protected routes → unlimited token guessing | `Api/Middleware/RateLimitingMiddleware.cs:45-56,143-170`, `GatewayApplicationBuilderExtensions.cs:296-308` | Incomplete fix of `SEC-HIGH-04` |
| SR-M-03 | HitL approval HMAC key is derived from the Key Vault *reference string*, not the resolved secret → forgeable approval broadcasts | `Mcp/Services/HitLStepUpApprovalService.cs:30-44` | Related to `SEC-HIGH-08` |
| SR-M-04 | TOTP replay protection is a non-atomic check-then-set | `Security/Totp/Services/TotpVerificationService.cs:96-131` | Incomplete fix of `SEC-HIGH-12` |
| SR-M-05 | No TOTP attempt throttling / lockout | `TotpVerificationService.cs:74-206` | Incomplete fix of `SEC-HIGH-12` |
| SR-M-06 | Revoked TOTP secret stays valid on nodes that cached it in `_localSecrets` | `Security/Totp/Services/ClusterTotpSecretStore.cs:55-95` | New |
| SR-M-07 | A matching Access Profile replaces consent resolution, ignoring Deny consents and purpose limits (documented semantics: masking exemption only) | `Policy/TableAccessPolicy.cs:255-300,640-690` | New |
| SR-M-08 | MCP hybrid tools build the principal from the OAuth `client_id`; groups, claims and read-only marker are dropped | `Mcp/Services/McpToolExecutionHandler.cs:63-92,273-296` | Related to `SEC-MED-13` |
| SR-M-09 | Read-only tokens not enforced for MCP admin tools; read-only marker lost on SQL/API path (DML possible with `AllowDml` + writer role) | `AiDataGuardrailService.cs:231-270`, `GovernedSqlRewriter.cs:207+` | Incomplete fix of `SEC-HIGH-09` |
| SR-M-10 | Bulk consent: self-grant SoD compares only SID and name; Unmasked profiles activated without approval | `Api/Endpoints/GovernanceEndpoints.cs:594-730` | New |
| SR-M-11 | Schema Registry is a global, tenant-less in-memory store readable/writable (name squatting) by any `Developer`/`DataOwner` | `Api/Endpoints/SchemaRegistryEndpoints.cs:26-157` | New |
| SR-M-12 | Event-webhook tenant scoping is fail-open: `AccessPlanCreated/Applied`, `DatasetStateChanged`, `DocumentDeposited` carry no `TenantId`; tenant-less subscriptions receive every tenant's events incl. security alerts | `Domain/Options/WebhooksOptions.cs:23-33`, `AccessPlanningService.cs:152-159,306-313,430-438` | New (F-EXT-01) |
| SR-M-13 | Event bus awaits webhook dispatch (unbounded `MaxRetries`) and plugin listeners (no timeout) inline on the request path; attacker-triggerable events (prompt-injection alerts) amplify | `Events/InMemoryGatewayEventBus.cs:46-136`, `Webhooks/WebhookDispatcherService.cs:64-143` | New (F-EXT-01) |
| SR-M-14 | Datasource probe path `\/evil.com/x` or `https:\\evil.com` re-targets the probe (with vaulted API key) to another public host (verified on .NET 10) | `Catalog/Services/DatasourceTestingService.cs:140-170,239-262` | Incomplete fix of `SEC-MED-10` |
| SR-M-15 | Traffic shadowing: `new Uri(base, path.TrimStart('/'))` turns `/https://evil/x` into an absolute target; middleware runs for unrouted paths; no host-equality check | `Diagnostics/Shadowing/TrafficShadowingService.cs:185-188` | Incomplete fix of `SEC-HIGH-11` |
| SR-M-16 | OData `$metadata` / service document fail **open**: bare `catch` around both `CheckTableAccessAsync` calls leaves `decision == null`, and the table is listed with all columns (incl. Deny); cached 60 s | `Extensions/OData/ODataHandler.cs:116-164` | **Regression** from `4da8e788` |
| SR-M-17 | GraphQL root `table(..., after)` passes unbounded `after` into SQL `OFFSET`; cost analyzer ignores it | `GraphQL/Types/QueryTypes.cs:24-55` | New |
| SR-M-18 | Federation context signature does not cover the body, uses unescaped `:`-joined canonical form; nonce cache per process (replay across replicas) | `Federation/Services/SubgraphContextPropagationService.cs:54-129`, `GraphQL/Federation/SubgraphSecurityValidator.cs:45-93` | Incomplete fix of `SEC-MED-23` |
| SR-M-19 | DuckDB OLAP validator bypassed by quoted function names (`"range"(...)`, `"duckdb_settings"()`, `"current_setting"()`) — DoS / info disclosure (`enable_external_access=false` still blocks I/O) | `Olap/DuckDbOlapEngine.cs:405-509` | Related to `SEC-LOW-08` |
| SR-M-20 | `GEO_JITTER` masking returns the raw value for non-numeric input | `Services/ColumnMaskingProvider.cs:428-431` | New |
| SR-M-21 | `PurgeExpiredJobsAsync` has no caller — async job results and query text retained indefinitely | `Jobs/Services/AsyncQueryJobManager.cs:253-275` | New |
| SR-M-22 | `SqlEndpointLoader` watcher handlers lack try/catch (host crash on locked/empty file); renamed endpoints survive deletion; recursive scan follows directory symlinks | `SqlEndpoints/Services/SqlEndpointLoader.cs:65,92-114,521-535` | Incomplete fix of `SEC-MED-19` |
| SR-M-23 | Caller-transaction audit writes (PostgreSQL / SQL Server) skip the chain lock and advance `_lastAuditSeq` before commit → seq collisions and a latched false chain violation that blocks WORM export | `Infrastructure/Persistence/PostgreSqlGovernanceRepository.Audit.cs:343-429` (+ SQL Server) | New |
| SR-M-24 | `GetConsentRequestByTicketIdAsync(ticketId, TenantId)` only overridden for SQLite; PostgreSQL/SQL Server use the unscoped default method → one tenant's ticket shadows another's ITSM callbacks | `PostgreSqlGovernanceRepository.Consent.cs:1391`, `SqlServerGovernanceRepository.Consent.cs:1366` | Incomplete fix of `SEC-MED-24` |
| SR-M-25 | RLS/consent predicates are spliced verbatim; an unqualified table name inside a policy predicate can be shadowed by a user CTE (harness-verified; not reachable with the built-in generators, which always schema-qualify) | `TrinoSqlEngine/RlsListener.cs:629-668` | New (defence in depth) |
| SR-M-26 | Legacy `RlsListener.EnterInsertInto` checks only the first occurrence of a duplicated tenant column (`INSERT INTO t (id, tenant_id, TENANT_ID) ...`); exploitable on SQLite-like backends | `TrinoSqlEngine/RlsListener.cs:381-399` | New |

### 3.3 Low

| ID | Title | Location |
| :--- | :--- | :--- |
| SR-L-01 | Unauthenticated 404/4xx responses written to the audit chain as `TABLE_QUERY`/`ALLOW` with attacker-chosen target (chain flooding) | `Api/Middleware/AccessAuditMiddleware.cs:112-196` |
| SR-L-02 | No `AllowedHosts`; CSRF same-origin check trusts the client `Host` header (DNS rebinding against dev/anonymous modes, MCP) | `GatewayApplicationBuilderExtensions.cs:220-226` |
| SR-L-03 | `ex.Message` returned on anonymous webhooks, CDC registration (SSRF oracle), dbt, row-filter endpoints (incomplete `SEC-LOW-01`) | `WebhookEndpoints.cs:120-145`, `StreamingCdcEndpoints.cs:145,157`, `DbtEndpoints.cs:151,179,371` |
| SR-L-04 | `":ClusterAdmin"` / `":GovernanceAdmin"` role values pass the tenant-prefix guard as global roles | `Security/GatewayRoleEvaluator.cs:196-209` |
| SR-L-05 | TOTP seeds stored and cached in plaintext (incomplete `SEC-HIGH-12`) | `ClusterTotpSecretStore.cs:25-40` |
| SR-L-06 | Access-plan state per-node and unbounded; apply step not bound to the confirming admin (incomplete `SEC-MED-09`) | `AccessPlanningService.cs:44-50,226-254,452-480` |
| SR-L-07 | External audit anchor can move backwards via the caller-transaction commit path | `PostgreSqlGovernanceRepository.Audit.cs:246-272` (+ SQL Server) |
| SR-L-08 | Detected chain violation does not fail closed for Tier-B admission; PostgreSQL clears the fault flag on next batch; SQLite never sets it (incomplete `SEC-HIGH-17`) | `*GovernanceRepository.Audit.cs` |
| SR-L-09 | SQLite verification still accepts rows without `seq` sealed with a hardcoded legacy HMAC key (incomplete `SEC-HIGH-18`) | `SqliteGovernanceRepository.Audit.cs:703-714` |
| SR-L-10 | `RedisTokenRevocationService` fails open on Redis outage; no fail-closed option (open `SEC-LOW-09`) | `Infrastructure/Security/RedisTokenRevocationService.cs:131-189` |
| SR-L-11 | Structured virtual-filter predicates on the SQL channel lack a tenant condition on their source tables; SQL-defined filters only check the tenant column is mentioned | `VirtualFilters/StructuredFilterSqlBuilder.cs:49-210`, `SqlFilterCompiler.cs:124-139` |
| SR-L-12 | `SubgraphResultMasker` masks by a hard-coded field-name denylist | `Federation/Services/SubgraphResultMasker.cs:168-211` |
| SR-L-13 | CloudEvents webhook HMAC has no timestamp (replayable) and omits signed headers | `Events/Services/CloudEventWebhookDispatcher.cs:99-110` |
| SR-L-14 | Empty webhook secret still sends `sha256=`; target URLs with embedded tokens logged; resolved IP echoed in subscription errors | `WebhookDispatcherService.cs:96-153`, `CloudEventWebhookDispatcher.cs:140-171` |
| SR-L-15 | Domain events leak absolute server file path (`SourceFilePath`) and verbatim ABAC denial reasons to webhooks/plugins | `Domain/Events/GatewayEventModels.cs`, `DocIngestionEndpoints.cs:118-126` |
| SR-L-16 | OpenAPI spec download has no response size cap | `CatalogDiscoveryService.cs:313-326` |
| SR-L-17 | `SubgraphSecurityValidator` throws on out-of-range timestamps before signature check | `GraphQL/Federation/SubgraphSecurityValidator.cs:45-51` |
| SR-L-18 | Subscription / WebSocket revocation watchers fail open indefinitely when the revocation service errors | `GraphQL/Subscriptions/Subscription.cs:153-170`, `WebSocketAuthInterceptor.cs:402-418` |
| SR-L-19 | OpenLineage push tenant filter never matches (node IDs contain no `:`) → exports all tenants' lineage | `Extensions/Lineage/OpenLineageClient.cs:61-73` |
| SR-L-20 | Debezium parser reads the tenant only from `tenant_id`; other tenant columns are stamped with the caller's tenant | `Extensions/Cdc/DebeziumCdcParser.cs:60-70` |
| SR-L-21 | GraphQL `catalog` query not tenant-scoped on admin/discovery/open-schema paths | `GraphQL/Types/QueryTypes.cs:326-393` |
| SR-L-22 | Delta executor replays the full `_delta_log` per request before the 501 gate (DoS) | `Extensions/Lakehouse/Services/DeltaLakeDataSourceExecutor.cs:108-120` |
| SR-L-23 | `syncOpenMetadata` mutation returns raw exception text and cross-tenant table names; accepts raw `ClusterAdmin` role string | `GraphQL/Types/MutationTypes.cs:680-800` |
| SR-L-24 | Masked columns rely solely on projection-time replacement; no explicit SELECT-side whole-row guard (informational, no bypass found) | `TrinoSqlEngine/RlsListener.cs:646-666` |

Deployment and supply-chain findings are listed in section 5.

---

## 4. Status of the 2026-10-13 Findings

Each of the 60 `SEC-*` findings and the PoC items `B-10`..`B-18` (69 items) was re-checked at `main` `88f7df1c`, which contains all three remediation commits (`3014f659`, `6ad16519`, `f7309860`). A fix counts only if the exploit path is closed on **all** listed variants (all three DB providers, all overloads, all channels). The per-ID table with evidence and remaining gaps is in [`remediation-deploy.md`](2026-10-14-security-code-review/remediation-deploy.md).

| Severity | Fixed | Partially fixed | Open | Not reproducible |
| :--- | :---: | :---: | :---: | :---: |
| Critical (6) | 5 | 1 (`CRIT-03`: Four-Eyes creator ≠ confirmer still missing) | 0 | 0 |
| High (20) | 4 (`01`, `02`, `03`, `16`) | 13 | 3 (`12`, `17`, `18`) | 0 |
| Medium (25) | 2 (`01`, `17`) | 9 | 13 | 1 (`16`, code removed) |
| Low (9) | 1 | 3 | 5 | 0 |
| PoC `B-10`..`B-18` (9) | 3 (`B-13`, `B-15`, `B-18`) | 2 (`B-10`, `B-16`) | 4 (`B-11`, `B-12`, `B-14`, `B-17`) | 0 |
| **Total (69)** | **15** | **28** | **25** | **1** |

Key observations:

- **Variant gaps:** most "partial" items closed the exact variant named in the 2026-10-13 report but not its siblings — see `SR-H-01`, `SR-H-02`, `SR-H-04`, `SR-H-05`, `SR-M-01`, `SR-M-02`, `SR-M-14`, `SR-M-15`, `SR-M-24`. Additionally, `SEC-HIGH-07`'s new privilege check compares against non-canonical role names (`GOVERNANCE_ADMIN` vs `GovernanceAdmin`) with a substring scope match, and `SEC-HIGH-20`'s RLS-column projection bug remains in the Iceberg executor.
- **No regression tests for the bulk fix:** commit `6ad16519` (CRIT/HIGH remediation) added no new tests. Nearly every CRIT/HIGH fix can be reverted without a failing test. Tests exist only for `B-10`, `B-15`, `B-16`, `B-18`, the procedure clear-consent change, `MED-08`, `MED-10` (headers only), `MED-14`, `MED-18` (CSV), `MED-23`, `LOW-05`, `LOW-08`.
- **Branch note:** `feat/ast-x1-cutover` (`4ca84c5a`) does **not** yet contain `6ad16519` / `f7309860`; all findings and statuses above refer to `main`. Merge `main` before cutover.

---

## 5. Deployment & Supply-Chain Findings

| ID | Severity | Title | Location |
| :--- | :--- | :--- | :--- |
| SR-D-01 | High | Transitive `System.Security.Cryptography.Pkcs` 9.0.18 (CVE-2026-84008 High, CVE-2026-70162 Medium) on `main`; bumped to 10.0.13 only on `feat/ast-x1-cutover` (`4ca84c5a`) | `src/Autheris.Api/packages.lock.json` and other lock files |
| SR-D-02 | Medium | Hard-coded HMAC sealing key `Autheris_Worm_Config_Sealing_Key_2026_Strict` for the WORM configuration audit, all environments | `Application/Governance/Services/WormConfigurationAuditService.cs:30,101` |
| SR-D-03 | Medium | Helm chart ships no `NetworkPolicy` | `deploy/helm` |
| SR-D-04 | Medium | Helm image defaults to mutable `latest` tag with `IfNotPresent`, no digest | `deploy/helm` values |
| SR-D-05 | Low | ServiceAccount token auto-mounted in pods | `deploy/helm` |
| SR-D-06 | Low | Helm does not wire required production configuration; `/metrics` scrape unauthenticated | `deploy/helm` |
| SR-D-07 | Low | TestAuth handler compiled in by default (`AutherisIncludeTestAuth=true`); only CI publish excludes it; benchmark/deploy Containerfiles ship it | `Directory.Build.props`, `deploy/containers` |
| SR-D-08 | Low | Benchmark stack gaps: nginx as root, `TrustServerCertificate=True`, shared HMAC secret, placeholder tokens (benchmark-only) | `deploy/` benchmark compose |
| SR-D-09 | Low | Floating CI SDK version; NuGet audit level `moderate` | `.github/workflows`, `global.json` |

Verified OK: non-root containers, digest-pinned base images, SHA-pinned GitHub Actions, NuGet signature validation with package source mapping, no secrets or private keys tracked (including history). Stray `fix4.tgz`, `fix4b.tgz` (0 bytes) and `src/Autheris.Api/scratch` are untracked and git-ignored and are not copied into the image. `dotnet list package --vulnerable` timed out offline; SR-D-01 relies on the lock files and the CVE references in `4ca84c5a`.

---

## 6. Verified Security Strengths

- **Authentication:** JWT issuer/audience/lifetime/signing key always validated; ForwardAuth trusts only the physical TCP peer and a ≥32-byte constant-time secret, never admin roles from headers; Basic auth with constant-time unknown-user path and per-(user, IP) lockouts; `GatewayStartupValidator` blocks DANGER switches outside Development; TestAuth compiled out of CI artifacts.
- **Authorization:** fallback policy covers every endpoint; Casbin deny-first with immutable snapshots and fail-closed on unevaluable deny rules; ReBAC with cycle/depth bounds and fail-closed on store errors; cluster-admin only from literal unprefixed roles.
- **SQL engine:** RLS applied to every tested table-reference shape in both engines (FROM, JOIN USING/NATURAL, subqueries, LATERAL, UNION, CTEs); CTE/physical-table name collisions rejected; statement-class gating fail-closed (MERGE, CALL, SET SESSION, EXPLAIN, EXECUTE); function allowlist quote- and case-insensitive; independent depth limits survived 2,900-deep expressions and 500-level subqueries.
- **Data paths:** plan cache key binds tenant, dialect, datasource, policy hash and raw SQL; masked/denied columns rejected in WHERE/JOIN/HAVING/ORDER BY; OData `$filter` fully parameterised with length/depth limits; async jobs check tenant **and** owner (no IDOR); CSV formula neutralisation.
- **Egress:** `SecureOutboundHttp` pins every resolved IP in `ConnectCallback`, disables redirects and proxies, normalises IPv4-mapped/NAT64/6to4, always blocks metadata and CGNAT ranges.
- **Plugins (F-EXT-01):** hash allow-list as trust anchor; the verified bytes are the loaded bytes (no TOCTOU); fails closed outside Development.
- **Audit:** unambiguous canonicalisation, HKDF-separated HMAC keys, signed anchors (ECDSA / RSA-PSS), MCP audit failure aborts execution; all repository SQL parameterised.
- **Infrastructure:** Redis auth + TLS required outside Development, consent L2 cache HMAC-framed and epoch-validated with rollback detection; idempotency keys scoped by user, operation, tenant and argument fingerprint.

---

## 7. Remediation Plan

All fixes follow the 6-phase lifecycle (strict TDD: a failing regression test per finding ID before the fix).

### Wave A — P0 (High and incomplete fixes on authentication/authorization)

The High findings SR-H-01 … SR-H-06 are fixed (see §8). Remaining Wave A items:

| Finding | Remediation |
| :--- | :--- |
| SR-M-01, SR-M-04..06, SR-L-05 | Server-side pending enrollment seed only; atomic `SET NX` consumption per `(user, step)`; attempt counter with lockout; cache invalidation on revoke; encrypt seeds via Data Protection. |
| SR-M-02 | Move the unauthenticated-IP check before `UseAuthorization`, or drop the Bearer exemption and apply a higher pre-auth budget instead. |
| SR-M-03 | Derive the HitL key from the resolved secret bytes (`IKeyVaultSecretProvider`), fail closed if unresolved. |
| SR-M-16 | Treat any exception in the access check as deny (`continue`) and do not cache the result. |

### Wave B — P1 (remaining Medium)

SR-M-07 … SR-M-15, SR-M-17 … SR-M-26 per the remediation guidance in the detailed area reports. Priority within the wave: tenant isolation (M-11, M-12, M-24), SSRF (M-14, M-15), F-EXT-01 event bus (M-12, M-13), audit chain (M-23).

### Wave C — P2 (Low / defence in depth)

SR-L-01 … SR-L-24 and SR-D-02 … SR-D-09.

Cross-cutting: add a regression test per `SEC-*` ID fixed in `6ad16519` so that reverts are caught.

---

## 8. Remediation Status — Wave A High Findings (2026-10-14)

All High findings (`SR-H-01` … `SR-H-06`, `SR-D-01`) are fixed on branch **`fix/security-review-2026-10-14-high-b`** (base `main` `88f7df1c`). PR to `main` pending.

> [!WARNING]
> A second, independently developed fix set for the same findings exists on `fix/security-review-2026-10-14-high` (head `3ef3c473`, includes `SR-M-21` job purge). Only one of the two branches should be merged; the other must be closed.

| Finding | Status | Commit(s) | Remediation as implemented |
| :--- | :--- | :--- | :--- |
| SR-H-01 | Fixed | `42e15c40`, `c89f6967` | `query_sql` / `invoke_api` targets resolved from the SQL AST (`ISqlEngine.Analyze`) and from `/api/v1/data/...` / `/api/v1/statement`; Four-Eyes and ABAC pre-check run per referenced table; fail closed on missing engine, parse error (`ParseCanceledException`), or unresolvable target. Regex `FROM` helper removed. |
| SR-H-02 | Fixed | `42e15c40`, `99369340` | MCP admin tools (execution gate and `tools/list` in `McpProtocolHandler` / `GatewayMcpServer`) admit only `ClusterAdmin` / `GovernanceAdmin`; `TenantAdmin` and bare `Admin` rejected. `AccessPlanningService` receives the caller tenant from MCP **and** REST (`SetDatasetState`, `ApplyAccess`) for audit and ReBAC instead of `LegacySingleTenant` / dataset domain. |
| SR-H-03 | Fixed | `57e8969d` | Datasource secrets live only in the server-generated `datasource:{tenant}:{domain}:{name}:secret` namespace; a client `SecretRef` with a raw secret is ignored, without one it must be namespaced. `DefaultEnvironmentSecretProvider.SetSecret` rejects keys outside the namespace; the datasource test never resolves non-namespaced refs. |
| SR-H-04 | Fixed | `57e8969d`, `c89f6967` | `/transition`, `/approve`, `/reject` load the proposal from the store and take the reviewer from the authenticated principal; per-action status preconditions; `Stage1ApproverSid` persisted and distinct stage-2 approver required; empty or mismatched `DataOwnerSid` denies; step-up via `VerifyAndConsumeTotpAsync` (fail closed without TOTP services); column overrides cannot lower the rank. |
| SR-H-05 | Fixed (partial scope) | `1abf41bd` | Async job `MaxRows` = min(request, WebSQL / `RowLimits.AsyncJob` limit, `AsyncJobs.MaxRowsHardCap` = 100000); timeout always armed, capped at `AsyncJobs.MaxTimeoutSeconds` = 600. **Not done:** streaming results to disk, per-user job quota. |
| SR-H-06 | Fixed | `1abf41bd`, `c89f6967` | Shared `SqlDataSourceResolver.ResolveAllowedDataSource` applied to every catalog in `CrossSourceQueryRouter` (cross-source and HTTP-declarative) and in the DuckDB OLAP endpoint before any metadata or upstream access. |
| SR-D-01 | Fixed | `59ccfdc0` | `System.Security.Cryptography.Pkcs` pinned to 10.0.13 (cherry-pick of `4ca84c5a`). |

### 8.1 Behaviour changes

- HTTP-declarative catalogs must be listed in `WebSql.AllowedDataSources` (or `DataSourceMappings`), like SQL catalogs, to be readable via WebSQL cross-source and the REST dataset channel.
- OLAP applies the tenant allowlist always and the global allowlist only when `AllowedDataSources` or `DataSourceMappings` is configured.
- `GovernanceAdmin` can no longer act as `DATA_OWNER` on a classification unless assigned as owner (`assign-owner`).
- New options: `Gateway:AsyncJobs:MaxRowsHardCap`, `Gateway:AsyncJobs:MaxTimeoutSeconds`, `Gateway:RowLimits:AsyncJob`.

### 8.2 Verification (branch `fix/security-review-2026-10-14-high-b`)

- `dotnet build Autheris.slnx -c Release`: 0 errors, 0 warnings.
- Unit 5369/5369, Architecture 43/43, Extensions 241/241, TrinoSqlEngine 6976/6977 (one DuckDB timing test, green in isolation, code untouched).
- Integration: REST dataset suite green. Remaining failures are pre-existing on the base `59ccfdc0` (5 `ODataIntegrationTests`, 1 `EndToEndSqliteTests` OData test, 3 `DataClassificationWorkflowIntegrationTests`) or require Docker (PostgreSQL, Oracle, SQL Server, Databricks containers) and were not executed locally.
- Regression tests named `SR_H_0x_*` exist for every finding. The failing-before-fix step was demonstrated only for the first `SR-H-03` and `SR-H-05`/`SR-H-06` tests.

### 8.3 Residual risks and follow-ups

- Datasource rows persisted before `57e8969d` may still carry arbitrary `ApiKeySecretName` values; `DeclarativeHttpDataSourceExecutor` does not enforce the namespace at query time. Add a migration or execution-time check.
- `GovernanceAdmin` (global role) can reference any `datasource:` secret of any tenant when registering a datasource.
- SR-H-05 streaming to disk and per-user job quota remain open (Wave B candidate).
- Pre-existing failing OData and classification integration tests need triage (possibly related to the `4da8e788` regression in §1).
