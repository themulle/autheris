# Security Review, Hardening & Compliance Remediation Roadmap

**Document ID:** `PLAN-SECURITY-REVIEW`  
**Location:** `doc/plan/security.md` (or `docs/plans/security.md`)  
**Status:** WAVES A & B COMPLETED ✅ · WAVE C ACTIVE 🚀  
**Parent Plan:** [`00-master-plan-overview.md`](00-master-plan-overview.md)  
**Implementation Plans:** [`implementation/impl-security-wave-c-1.md`](implementation/impl-security-wave-c-1.md), [`implementation/impl-security-wave-c-2.md`](implementation/impl-security-wave-c-2.md); global order in [`implementation/00-implementation-roadmap.md`](implementation/00-implementation-roadmap.md)  
**Role:** Application Security Engineer (`csharp-security-expert`)  
**Scope:** Full-solution security review covering all seven production assemblies (`Autheris.Api`, `Autheris.Application`, `Autheris.Domain`, `Autheris.Infrastructure`, `Autheris.Extensions`, `Autheris.GraphQL`, `TrinoSqlEngine`), deployment manifests, and supply chain.

---

## 1. Executive Summary & Verification Overview

A complete security code review and threat analysis was executed following the AppSec remediation commits. The review evaluated access control, token handling, SSRF egress boundaries, SQL injection & CTE shadowing, multi-tenant isolation, and WORM audit integrity:

| Severity | Total Findings | Remediation Status | Verification |
|:---|:---:|:---:|:---:|
| **Critical** | 0 | None Identified | Zero Critical findings in current architecture |
| **High** | 6 (`SR-H-01`..`06`) | **100% FIXED & VERIFIED ✅** | Merged into `main` via PR #73 |
| **Medium** | 26 (`SR-M-01`..`26`) | **100% FIXED & VERIFIED ✅** | Consolidated on `feat/ast-x1-security-consolidation` |
| **Low** | 24 (`SR-L-01`..`24`) | **Active Backlog (Wave C)** | Tracked in Section 4 below |
| **Deployment** | 9 (`SR-D-01`..`09`) | `SR-D-01`..`05` Fixed ✅; `06`..`09` Open | Pkcs 10.0.13, Helm NetPol & SA token automount disabled |

---

## 2. Delivered High Remediation (Wave A — P0)

All High findings were remediated via strict TDD and verified with regression tests:

| Finding ID | Vulnerability & Attack Surface | Implemented Security Remediation | Status |
|:---|:---|:---|:---:|
| **SR-H-01** | MCP Four-Eyes / ABAC pre-check evaluated regex-matched table rather than complete AST references. | Replaced regex with full AST semantic analysis (`ISqlEngine.Analyze`). Pre-checks evaluate every referenced table and fail closed on parse cancellation. | **Fixed ✅** |
| **SR-H-02** | MCP admin tools accepted `TenantAdmin` to mutate global catalog state without tenant scoping. | Admin tools restricted strictly to `ClusterAdmin` and `GovernanceAdmin`. `AccessPlanningService` enforces caller tenant across all operations. | **Fixed ✅** |
| **SR-H-03** | Datasource `Auth.SecretRef` permitted arbitrary host environment key lookup and exfiltration. | Enforced strict server-controlled namespace (`datasource:{tenant}:{domain}:{name}:secret`). Non-namespaced secret resolution rejected. | **Fixed ✅** |
| **SR-H-04** | Classification workflow accepted client-supplied reviewer identities without multi-stage preconditions. | Transition state machine enforces state preconditions, records `Stage1ApproverSid`, mandates distinct stage-2 approver, and requires TOTP step-up. | **Fixed ✅** |
| **SR-H-05** | Async SQL job `MaxRows` and client `Timeout` bypassed WebSQL server bounds. | Clamped `MaxRows` to server-configured hard caps (`AsyncJobs:MaxRowsHardCap = 100000`) and enforced maximum execution timeouts. | **Fixed ✅** |
| **SR-H-06** | Tenant datasource allowlists bypassed on cross-source, OLAP, and HTTP-table paths. | Centralized `SqlDataSourceResolver.ResolveAllowedDataSource` enforced across all catalog routing, OLAP endpoints, and federated queries. | **Fixed ✅** |
| **SR-D-01** | Transitive `System.Security.Cryptography.Pkcs` vulnerability (CVE-2026-84008). | Pinned explicit reference to `10.0.13` across all consuming projects and updated package lockfiles. | **Fixed ✅** |

---

## 3. Delivered Medium Remediation (Wave B — P1)

All Medium findings were remediated, verified, and consolidated with the AST X1 cutover:

| Category | Finding IDs | Implemented Security Controls | Status |
|:---|:---|:---|:---:|
| **Authentication & TOTP** | `SR-M-01`, `SR-M-04`, `SR-M-05`, `SR-M-06`, `SR-L-05` | Implemented `ITotpEnrollmentService`, Data Protection TOTP secret protector, atomic replay check-then-set, attempt throttling/lockout, and cluster cache invalidation on revocation. | **Fixed ✅** |
| **Rate Limiting** | `SR-M-02` | Client IP rate limiting enforced on unauthenticated Bearer requests before authorization middleware executes. | **Fixed ✅** |
| **HitL & Governance** | `SR-M-03`, `SR-M-07`, `SR-M-10` | HitL HMAC key derived from resolved vault secret; access profiles enforced strictly as masking exemptions without overriding Deny consents; bulk consent SoD verified. | **Fixed ✅** |
| **MCP Security** | `SR-M-08`, `SR-M-09` | MCP hybrid tools propagate authenticated user claims, groups, and SIDs; read-only token markers strictly enforced across API and SQL surfaces. | **Fixed ✅** |
| **Multi-Tenancy** | `SR-M-11`, `SR-M-12`, `SR-M-24` | Schema registry keys tenant-scoped; CloudEvents webhook events and subscriptions enforce strict tenant isolation; ITSM ticket lookup tenant-scoped on PostgreSQL and SQL Server. | **Fixed ✅** |
| **Outbound & SSRF** | `SR-M-14`, `SR-M-15` | `OutboundPathPolicy` validates relative paths and prevents schema-relative / slash-escape SSRF in datasource probes and traffic shadowing. | **Fixed ✅** |
| **Protocols & Data** | `SR-M-16`, `SR-M-17`, `SR-M-18`, `SR-M-19`, `SR-M-20`, `SR-M-21`, `SR-M-22` | OData `$metadata` fails closed on authorization errors; GraphQL `after` offset capped at `MaxAllowedOffset`; federation HMAC binds request body; quoted DuckDB function normalization; `GEO_JITTER` fails closed; async job purge active; `SqlEndpointLoader` file watcher hardened. | **Fixed ✅** |
| **Audit & Integrity** | `SR-M-23`, `SR-D-02` | WORM audit writes locked with DB-tail commit synchronization; WORM sealing key derived from audit vault secret. | **Fixed ✅** |
| **SQL Engine & CTEs** | `SR-M-25`, `SR-M-26` | Policy subquery tables canonicalized to schema-qualified names preventing user CTE hijacking; duplicate columns in INSERT statements rejected. | **Fixed ✅** |
| **Deployment & Supply Chain** | `SR-D-03`, `SR-D-04`, `SR-D-05` | Helm NetworkPolicy added, immutable image refs configured, service account token automount disabled. | **Fixed ✅** |

---

## 4. Active Remediation Backlog (Wave C — P2 Low / Defense-in-Depth)

| Finding ID | Area & Location | Vulnerability Description | Recommended Remediation |
|:---|:---|:---|:---|
| **SR-L-01** | `Api/Middleware/AccessAuditMiddleware.cs` | Unauthenticated 404/4xx responses logged as `TABLE_QUERY`/`ALLOW` with client target. | Log unauthenticated 404s under a distinct unprivileged event kind without client table attribution. |
| **SR-L-02** | `GatewayApplicationBuilderExtensions.cs` | CSRF same-origin check trusts client `Host` header without `AllowedHosts` enforcement. | Configure ASP.NET Core `HostFilteringMiddleware` with strict `AllowedHosts`. |
| **SR-L-03** | `WebhookEndpoints.cs`, `StreamingCdcEndpoints.cs` | Exception message returned in error responses (SSRF oracle / info leakage). | Mask error responses with generic ProblemDetails, logging detailed exceptions server-side only. |
| **SR-L-04** | `Security/GatewayRoleEvaluator.cs` | Role values with leading colon (e.g. `":ClusterAdmin"`) pass tenant prefix checks. | Trim and validate role syntax strictly against canonical role names. |
| **SR-L-06** | `AccessPlanningService.cs` | Access plan cache is per-node without distributed eviction or confirmer binding. | Persist access plans in Redis with TTL and bind execution to confirming admin identity. |
| **SR-L-07** | `PostgreSqlGovernanceRepository.Audit.cs` | External audit anchor sequence can regress via caller transaction rollback. | Track anchor sequence progression only after transaction commit succeeds. |
| **SR-L-08** | `*GovernanceRepository.Audit.cs` | Audit chain tampering does not latch fail-closed for subsequent queries. | Latch repository state to fail closed upon detecting any cryptographic hash chain discontinuity. |
| **SR-L-09** | `SqliteGovernanceRepository.Audit.cs` | Legacy SQLite verification accepts rows without sequence numbers. | Require valid sequence numbers and derive HMAC keys from vault secrets exclusively. |
| **SR-L-10** | `RedisTokenRevocationService.cs` | Token revocation fails open during Redis disconnection. | Provide configuration option `FailClosedOnTokenRevocationUnavailable`. |
| **SR-L-11** | `StructuredFilterSqlBuilder.cs` | Virtual filter subqueries lack explicit tenant binding on source tables. | Inject tenant predicate into all virtual filter join expressions. |
| **SR-L-12** | `SubgraphResultMasker.cs` | Subgraph masking uses hardcoded field denylist instead of catalog metadata. | Drive GraphQL subgraph masking from dynamic catalog mask definitions. |
| **SR-L-13** | `CloudEventWebhookDispatcher.cs` | Webhook signature omits timestamp and signed headers (replay window). | Include ISO 8601 timestamp in signed HMAC payload and enforce 5-minute replay window. |
| **SR-L-14** | `WebhookDispatcherService.cs` | Resolved target IP addresses echoed in client error messages. | Sanitize webhook dispatch failure messages to prevent internal network scanning. |
| **SR-L-15** | `Domain/Events/GatewayEventModels.cs` | Domain events leak server filesystem path and ABAC denial reasons. | Redact internal filesystem paths from event payloads before publishing. |
| **SR-L-16** | `CatalogDiscoveryService.cs` | OpenAPI spec download has no content-length cap. | Enforce maximum HTTP response body stream limit (e.g. 10 MB) during remote ingestion. |
| **SR-L-17** | `SubgraphSecurityValidator.cs` | Throws unhandled exception on out-of-range timestamp before checking signature. | Validate HMAC signature prior to parsing timestamp headers. |
| **SR-L-18** | `WebSocketAuthInterceptor.cs` | Revocation watcher fails open on transient network errors. | Terminate active subscriptions if token revocation status cannot be verified. |
| **SR-L-19** | `OpenLineageClient.cs` | OpenLineage push filter does not match tenant prefix on node IDs. | Canonicalize node IDs with `{tenant}:{id}` format before evaluating push filter. |
| **SR-L-20** | `DebeziumCdcParser.cs` | CDC parser only extracts tenant from literal `tenant_id` column. | Support catalog-configured tenant column names for streaming CDC tables. |
| **SR-L-21** | `GraphQL/Types/QueryTypes.cs` | `catalog` query returns cross-tenant metadata on open-schema paths. | Enforce tenant isolation on catalog queries across all schemas. |
| **SR-L-22** | `DeltaLakeDataSourceExecutor.cs` | Replays full Delta transaction log before evaluating 501 gate. | Check feature gate before parsing Lakehouse metadata files. |
| **SR-L-23** | `GraphQL/Types/MutationTypes.cs` | `syncOpenMetadata` returns raw exception details and unvalidated table references. | Sanitize mutation responses and validate caller tenant against target tables. |
| **SR-L-24** | `TrinoSqlEngine` | Relying on projection masking without whole-row query guards. | Maintain projection-level rewrite as primary defense; add whole-row mask validation. |
| **SR-D-06** | `deploy/helm` | Prometheus `/metrics` endpoint scraped without authentication. | Protect metrics endpoint with mutual TLS or bearer token authentication in production. |
| **SR-D-07** | `Directory.Build.props` | `AutherisIncludeTestAuth` enabled by default in local builds. | Default `AutherisIncludeTestAuth` to false; require explicit opt-in for testing. |
| **SR-D-08** | `deploy/compose` | Benchmark stack runs nginx as root with `TrustServerCertificate=True`. | Harden benchmark deployment compose manifests for production equivalence. |
| **SR-D-09** | `.github/workflows` | Floating .NET SDK minor versions in CI workflow actions. | Pin exact .NET SDK version in `global.json` and workflow YAMLs. |

---

## 5. Architectural Security Strengths Preserved

- **Deny-by-Default Authorization:** Casbin ABAC engine enforces deny-first evaluation with immutable rule snapshots and fail-closed error handling.
- **AST-Level Pushdown:** Row-level security predicates and column masks are compiled directly into database ASTs, eliminating in-memory data leaks.
- **Egress & SSRF Hardening:** `SecureOutboundHttp` pins destination IP addresses in `ConnectCallback`, disallows redirects to private networks, and blocks metadata endpoints.
- **Plugin Sandbox:** Dynamically loaded extensions are verified against cryptographic SHA-256 hashes and isolated in collectible `AssemblyLoadContext` instances.
- **Cryptographic WORM Audit:** Immutable audit trail with HKDF-derived keys, SHA-256 chaining, and DB-tail transaction synchronization.
