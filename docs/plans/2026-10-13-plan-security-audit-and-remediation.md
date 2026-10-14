# Comprehensive Application Security (AppSec) Code Review & Remediation Plan

**Document ID:** `PLAN-SECURITY-AUDIT-2026-10-13`  
**Date:** October 13, 2026  
**Author / Role:** C# & .NET 10 Application Security Engineer (`csharp-security-expert`)  
**Status:** `COMPLETED & VERIFIED ✅`  
**Parent Document:** [`00-master-plan-overview.md`](file:///root/lis-git/autheris/doc/plan/00-master-plan-overview.md)

---

## 1. Executive Summary & Audit Scope

A complete, line-by-line Application Security (AppSec) code review was performed across all seven production source projects in `Autheris.sln`:

1. **`src/Autheris.Api`** (83 files: REST/Minimal API endpoints, authentication handlers, middleware pipeline, MCP HTTP/SSE/OAuth endpoints, DI extensions, startup validators)
2. **`src/Autheris.Application`** (240+ files: Governance, Access Planning, Casbin ABAC, Zanzibar ReBAC, MCP tools & AI guardrails, Catalog & OpenAPI ingestion, Universal Doc Store, Webhooks, CloudEvents, Federation, DuckDB OLAP, Stored Procedures, Virtual Filters, Async Jobs)
3. **`src/TrinoSqlEngine`** (40+ files: ANTLR4 SQL parser, AST builder, `AstSecurityVisitor`, `SqlAstRewriter`, dialect generators, `RlsListener`, function allowlists)
4. **`src/Autheris.Infrastructure`** (60+ files: SQLite/PostgreSQL/SQL Server governance repositories, WORM hash-chain & asymmetric anchors, plugin loader & Authenticode verification, Redis/Garnet security, secrets provider)
5. **`src/Autheris.Extensions`** (65+ files: OData v4 parser/CSDL/handler, Delta Lake & Iceberg executors, Lakehouse storage providers, DataHub/OpenMetadata/Purview/Collibra/Alation clients, Jira/ServiceNow ITSM clients, dbt & webhook handlers)
6. **`src/Autheris.GraphQL`** (35+ files: HotChocolate dynamic schema, `GraphQlTreeBuilder`, `QueryCostAnalyzerRule`, WebSocket auth, Subgraph security signing & masking, MCP GraphQL executor)
7. **`src/Autheris.Domain`** (100+ files: Security principal context, `ClusterAdminPolicy`, audit canonicalizer, domain models)

### Audit Standards Applied
- **OWASP Top 10 (2021)** & **OWASP API Security Top 10 (2023)**
- **OWASP Top 10 for LLM Applications (2025)** (MCP tool poisoning, prompt injection, excessive agency)
- **Autheris Zero-Trust & Fail-Closed Mandates** (`AGENTS.md` Section 8 & `csharp-security-expert` skill)

### Vulnerability Distribution

| Severity | Count | Key Risk Themes |
| :--- | :---: | :--- |
| **CRITICAL** | **6** | (1) Broken access control & DataOwner takeover on `/api/governance/classification/*`; (2) Production-exposed `/portal/approvals` bypassing approver & DataOwner checks; (3) Optional/null TOTP bypass & non-consuming TOTP replay in `AccessPlanningService`; (4) Full SSRF & redirect bypass in `CatalogDiscoveryService.RegisterDatasourceAsync`; (5) Operator-precedence RLS bypass & raw SQL filter/order injection in `GovernedDataQueryService`; (6) WORM audit chain anchor laundering on violation in PostgreSQL & SQL Server. |
| **HIGH** | **20** | Unverified 2FA TOTP secret overwrite; unguarded Doc/RAG bundle ingestion; unguarded DataHub event ingestion; pre-auth rate limit bypass via fake `Bearer ` header; `AccessPlanningService` token binding & hardcoded `"default"` tenant; `ClassificationWorkflowStateMachine` unverified step-up token & empty `ColumnDecisions` bypass; `DeclarativeConsentApplyService` fail-open `ConsentEffect.Allow` default; `HitLStepUpApprovalService` cross-tenant overload & unverified remote ticket HMAC; `McpDocTools` bypassing ABAC & PII scrubbing; `TenantAdmin` escalation on cluster `McpAdminTools`; `invoke_api`/`query_sql` bypassing Four-Eyes Step-Up; missing SSRF validation in `WebhookDispatcherService` & `TrafficShadowingService`; plaintext TOTP seeds in Redis & replay race condition; cross-source federated query tenant allowlist & catalog masking bypass; stored procedure `@result-column clear` overriding `Deny`/`Mask`; masked column predicate oracle bypasses; virtual filter `Joins` omission; audit fault flag auto-clearing; hardcoded legacy SQLite audit HMAC key; `GraphQlTreeBuilder` integer overflow & `StackOverflowException` DoS; Lakehouse RLS bypass on unprojected columns; GraphQL/CDC `GovernanceAdmin` vs. `ClusterAdmin` cross-tenant escalation. |
| **MEDIUM** | **25** | Stored XSS in `/portal/*`; traffic shadowing credential leakage & chunked body buffering; `AccessAuditMiddleware` ordering vs. `GatewayExceptionHandler`; EventGrid validation bypass; chunked body bug in `SqlEndpointRoutes`; MCP `ReadResourceAsync` guardrail bypass; break-glass audit ordering; `DatasourceTestingService` header/authority injection; `EgressUrlPolicy` IDN/IPv6 & `isDev` metadata check; `SubgraphContextPropagationService` `isDevelopment` default; MCP lineage visibility & `BuildUserPrincipal` dropped claims; Zip-bomb in `FileSystemUniversalDocStore`; `SqlFilterCompiler` & DML tautology gaps; `PushdownUrlFormatter` parameter/JSON injection; `GovernedSqlExecutionService` quote-state machine bug; `AsyncQueryJobBackgroundWorker` unbounded `MaxRows` & CSV formula injection; `SqlEndpointLoader` watcher crash; `SqlConnector` row-limit bypass; OData cache key collisions; `QueryCostAnalyzerRule` wrapper cost under-calculation; `DynamicTableType` unscoped HMAC & double-masking; `SubgraphSecurityValidator` nonce replay & `:` delimiter ambiguity; `itsm:*` secret aliasing; missing tenant-scoped ticket lookup in PG/MSSQL; `GatewayMcpQueryExecutor` claim injection & hardcoded `"finance"` domain. |
| **LOW / INFO** | **9** | Exception message leakage; CSRF same-host scheme check; `JwtSocketTokenValidator` revocation gap; duplicate `ICompiledSqlQueryPlanCache` DI registration; `SemanticPromptGuardrail` base64 normalization; `MarkdownDocIngestor` path containment; German string literals (`AGENTS.md` Rule 1); `ColumnMaskingProvider` default constructor fallback; `DuckDbOlapEngine` identifier collision & scan prefixes; `SqlConnectionFactory` TLS check & `RedisTokenRevocationService` fail-open. |

---

## 2. Verification Status of Previously Reported PoC Findings (`B-10` to `B-18`)

The following table cross-references the findings documented in [`2026-10-12-befunde-v1-1-6.md`](file:///root/lis-git/autheris/docs/plans/2026-10-12-befunde-v1-1-6.md) against the current codebase state:

| PoC ID | Original Issue Summary | Current Codebase Status (`HEAD`) | Remaining Vulnerability Reference |
| :--- | :--- | :--- | :--- |
| **B-10** | `/api/governance/plans`, `/confirm`, `/apply`, and `/principals` lacked admin policies; confirmation token not bound to approver; optional TOTP bypassed if unregistered. | **PARTIALLY FIXED ⚠️**<br/>REST endpoints in `GovernancePlanEndpoints.cs:43,52,86,124` and `CatalogApiEndpoints.cs:119` now enforce `.RequireAuthorization(GatewayPolicies.GovernanceAdmin)`. **However**, inside `AccessPlanningService.cs`: (1) TOTP is still skipped if `_totpService == null`, (2) `VerifyTotp` does not consume the token, (3) `ValidateConfirmationToken` ignores the `adminSid` segment and does not compare against `stored.ConfirmedToken`, and (4) creator == approver (Four-Eyes) is not enforced. | `SEC-CRIT-03`, `SEC-HIGH-05` |
| **B-11** | `DefaultEnvironmentSecretProvider.SetSecret` stores secrets only in volatile `_inMemorySecrets`; `AccessPlanningService` uses volatile `_inMemoryVault`. | **OPEN ❌**<br/>`DefaultEnvironmentSecretProvider.cs:35` and `AccessPlanningService.cs:50,311` still store dynamically registered datasource secrets strictly in volatile in-memory dictionaries that are lost on process restart. | `SEC-MED-10`, `SEC-MED-23` |
| **B-12** | `ds-<GUID>` returned by `CatalogDiscoveryService.RegisterDatasourceAsync` is never persisted; `DatasourceTestingService` cannot look up by `ds-<GUID>` and fails on inactive tables. | **OPEN ❌**<br/>`CatalogDiscoveryService.cs:303` generates an ephemeral `ds-{Guid.NewGuid():N}` that is never stored in `TableMetadata`, while `DatasourceTestingService.cs:69-74` searches only by domain/table/endpoint. | `SEC-MED-11` |
| **B-13** | `specUrl` in `CatalogDiscoveryService` fetched with raw `new HttpClient()`, following redirects and only calling `ValidateStatic(enforceHttps: false)`. | **OPEN ❌**<br/>`CatalogDiscoveryService.cs:309-314` still calls `EgressUrlPolicy.ValidateStatic(specUri, isDev: false, enforceHttps: false)` without DNS resolution (`ValidateResolvedAsync`) and uses raw `new HttpClient()` with auto-redirects enabled. | `SEC-CRIT-04` |
| **B-14** | OpenAPI auth ingestion omits `http basic`, treats OAuth2 client secret as static bearer token, and supports only single secret value. | **OPEN ❌**<br/>`OpenApiIngestionService.cs` and `CatalogDiscoveryService.cs:404-439` still only map a single secret string (`Value ?? ClientSecret`) without Basic Auth `username:password` encoding or OAuth2 `client_credentials` token exchange. | `SEC-MED-11` |
| **B-15** | Newly registered datasources remain `IsActive = false` with no REST endpoint to activate datasets (only MCP `admin_set_dataset_state`). | **PARTIALLY OPEN ⚠️**<br/>`SetDatasetStateAsync` updates `IsActive` via MCP `admin_set_dataset_state`, but no REST endpoint exposes `SetDatasetStateAsync`. | `SEC-MED-10` |
| **B-16** | `admin_*` MCP tools returned to non-admin users in `tools/list`; `IsMcpAuthBypassed` skips auth. | **PARTIALLY FIXED ⚠️**<br/>`McpProtocolHandler.cs:167-172` and `GatewayMcpServer.cs:70-75` now filter `McpAdminTools` in `tools/list`, **but** they include `TenantAdmin` and `"Admin"` (role mismatch with `AiDataGuardrailService.cs:233-236`), allowing `TenantAdmin` to invoke cluster-wide admin tools. | `SEC-HIGH-09` |
| **B-17** | `ApplyAccessAsync` hardcodes `tenantId = "default"` for ReBAC tuples and hardcodes `channel = "mcp"` in audit logs. | **OPEN ❌**<br/>`AccessPlanningService.cs:234,272` still hardcodes `var tenantId = "default";` and `channel = "mcp"`, and generates a fake GUID `wormSignature = Guid.NewGuid().ToString("N")`. | `SEC-HIGH-05` |
| **B-18** | `SetDatasetStateAsync` recreated `new Table` and wiped `DataSourceType`, `HttpEndpoint`, and `PluginName`. | **FIXED ✅**<br/>`AccessPlanningService.cs:376-382` now uses C# record `with` expressions (`existing with { Table = existing.Table with { IsActive = isActive } }`), preserving `DataSourceType`, `HttpEndpoint`, and `PluginName`. | Verified Fixed |

---

## 3. Master Security Findings Matrix

| ID | Severity | CVSS | Category / CWE | File & Line Numbers | Summary |
| :--- | :---: | :---: | :--- | :--- | :--- |
| **`SEC-CRIT-01`** | **Critical** | 9.1 | Broken Access Control (CWE-285 / CWE-639) | [`GovernanceClassificationEndpoints.cs:26-190`](file:///root/lis-git/autheris/src/Autheris.Api/Endpoints/GovernanceClassificationEndpoints.cs#L26-L190) | Any authenticated user can reassign `DataOwnerSid` of any table (`/assign-owner`), spoof `ReviewerRole` (`DATA_OWNER` / `COMPLIANCE_REVIEWER`), and approve classification proposals. |
| **`SEC-CRIT-02`** | **Critical** | 8.8 | Broken Access Control (CWE-284 / CWE-306) | [`DevPortalEndpoints.cs:27-174`](file:///root/lis-git/autheris/src/Autheris.Api/Endpoints/DevPortalEndpoints.cs#L27-L174) | `/portal/approvals` and `/portal/approvals/{ticketId}/step-up` are exposed in Production with `.WithAuditExemption` and skip `IsApprover` and `EnsureTableApproverAsync` checks. |
| **`SEC-CRIT-03`** | **Critical** | 9.0 | Improper Authentication / MFA Bypass (CWE-287 / CWE-294) | [`AccessPlanningService.cs:79-80, 180-187`](file:///root/lis-git/autheris/src/Autheris.Application/Governance/Services/AccessPlanningService.cs#L180-L187) | `ConfirmPlanAsync` fails open when `_totpService` or `_totpSecretStore` is null, uses non-consuming `VerifyTotp` (allowing TOTP replay), and lacks Four-Eyes separation between plan creator and confirmer. |
| **`SEC-CRIT-04`** | **Critical** | 9.1 | Server-Side Request Forgery (CWE-918) | [`CatalogDiscoveryService.cs:308-320`](file:///root/lis-git/autheris/src/Autheris.Application/Catalog/Services/CatalogDiscoveryService.cs#L308-L320) | `RegisterDatasourceAsync` fetches `specUrl` using raw `new HttpClient()` (following HTTP redirects) with `ValidateStatic(enforceHttps: false)` and no DNS resolution check (`ValidateResolvedAsync`). |
| **`SEC-CRIT-05`** | **Critical** | 9.3 | SQL Injection & RLS Precedence Bypass (CWE-89) | [`GovernedDataQueryService.cs:124-192, 251-255`](file:///root/lis-git/autheris/src/Autheris.Application/Data/Services/GovernedDataQueryService.cs#L124-L192), [`DataApiFilterTranslator.cs:26-127`](file:///root/lis-git/autheris/src/Autheris.Application/Data/Services/DataApiFilterTranslator.cs#L26-L127) | Unparenthesized user `FilterExpression` combined with `AND (<CombinedRowFilterSql>)` allows `OR` operator-precedence RLS bypass; regex-only filter/order translator permits arbitrary SQL clause & masked-column oracle injection. |
| **`SEC-CRIT-06`** | **Critical** | 8.9 | Cryptographic Tamper-Evidence Laundering (CWE-345) | [`PostgreSqlGovernanceRepository.Audit.cs:529-545`](file:///root/lis-git/autheris/src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Audit.cs#L529-L545), [`SqlServerGovernanceRepository.Audit.cs:564-580`](file:///root/lis-git/autheris/src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.Audit.cs#L564-L580) | PostgreSQL and SQL Server audit batch writers overwrite the external anchor file without checking `_auditChainViolation == null` or `TryLoadVerifiedAnchor`, laundering truncated/corrupted audit chains. |
| **`SEC-HIGH-01`** | **High** | 8.1 | MFA Secret Overwrite (CWE-620) | [`GovernanceApiEndpoints.cs:91-151`](file:///root/lis-git/autheris/src/Autheris.Api/Endpoints/GovernanceApiEndpoints.cs#L91-L151), [`DevPortalEndpoints.cs:68-118`](file:///root/lis-git/autheris/src/Autheris.Api/Endpoints/DevPortalEndpoints.cs#L68-L118) | `/api/v1/governance/2fa/verify-enrollment` accepts arbitrary client-supplied `SecretBase32` and overwrites an existing enrolled TOTP secret without verifying a code from the current secret. |
| **`SEC-HIGH-02`** | **High** | 7.7 | Broken Access Control / RAG Poisoning (CWE-862) | [`DocIngestionEndpoints.cs:20-121`](file:///root/lis-git/autheris/src/Autheris.Api/Endpoints/DocIngestionEndpoints.cs#L20-L121) | `POST /api/v1/docs/apps/{appId}/bundle` and `PUT .../documents/{category}/{docId}` lack `GatewayPolicies.GovernanceAdmin`, allowing any authenticated user to overwrite MCP documentation. |
| **`SEC-HIGH-03`** | **High** | 7.5 | Broken Access Control (CWE-862) | [`DataContractEndpoints.cs:71-99`](file:///root/lis-git/autheris/src/Autheris.Api/Endpoints/DataContractEndpoints.cs#L71-L99) | `POST /api/v1/catalog/datahub/events` and `GET /api/v1/governance/contracts` only require `.RequireAuthorization()`, allowing any user to inject DataHub MCL events. |
| **`SEC-HIGH-04`** | **High** | 7.5 | Rate Limit Bypass (CWE-770) | [`RateLimitingMiddleware.cs:47-55, 136-141`](file:///root/lis-git/autheris/src/Autheris.Api/Middleware/RateLimitingMiddleware.cs#L47-L55) | `PreAuthIpRateLimitingMiddleware` exempts any request with `Authorization: Bearer ...`, and `PostAuthSidRateLimitingMiddleware` skips unauthenticated requests, so invalid Bearer tokens bypass both limiters. |
| **`SEC-HIGH-05`** | **High** | 8.0 | Multi-Tenant Isolation & Token Binding (CWE-285) | [`AccessPlanningService.cs:196-280, 469-509`](file:///root/lis-git/autheris/src/Autheris.Application/Governance/Services/AccessPlanningService.cs#L196-L280) | `ValidateConfirmationToken` ignores the `adminSid` field and `stored.ConfirmedToken`; `ApplyAccessAsync` hardcodes `tenantId = "default"` and writes fake GUID `wormSignature`. |
| **`SEC-HIGH-06`** | **High** | 8.1 | MFA & Workflow Bypass (CWE-287 / CWE-841) | [`ClassificationWorkflowStateMachine.cs:124-185`](file:///root/lis-git/autheris/src/Autheris.Application/Governance/Services/ClassificationWorkflowStateMachine.cs#L124-L185) | `StepUpAuthToken` is only checked for non-whitespace (never verified via TOTP); empty `ColumnDecisions` yields `maxEffectiveRank = 0` bypassing Step-Up & Four-Eyes; `DATA_GOVERNANCE_REVIEWER` skips Stage 1. |
| **`SEC-HIGH-07`** | **High** | 7.8 | Fail-Open Authorization Default (CWE-636) | [`DeclarativeConsentApplyService.cs:21-65, 243-248`](file:///root/lis-git/autheris/src/Autheris.Application/Governance/Services/DeclarativeConsentApplyService.cs#L21-L65) | Unrecognized `policy.Effect` strings silently default to `ConsentEffect.Allow` (violating Fail-Closed); service lacks admin role verification and omits `ValueSource`/`UserAttribute` in diff checks. |
| **`SEC-HIGH-08`** | **High** | 7.8 | Cross-Tenant Bypass & Unverified HMAC (CWE-345) | [`HitLStepUpApprovalService.cs:326-332, 466-474, 549-554, 683-704`](file:///root/lis-git/autheris/src/Autheris.Application/Mcp/Services/HitLStepUpApprovalService.cs#L326-L332) | String overloads hardcode `IsCrossTenantAdmin: true`; `GetTicketAsync` deserializes remote tickets from Redis without `VerifyTicketSignature`; `AcquireDistributedLockAsync` fails open on Redis error. |
| **`SEC-HIGH-09`** | **High** | 7.6 | Privilege Escalation & ABAC Bypass in MCP (CWE-863) | [`AiDataGuardrailService.cs:233-297`](file:///root/lis-git/autheris/src/Autheris.Application/Mcp/Services/AiDataGuardrailService.cs#L233-L297), [`McpDocTools.cs:85-92`](file:///root/lis-git/autheris/src/Autheris.Application/Mcp/Services/McpDocTools.cs#L85-L92) | `McpDocTools` (`deposit_documentation`) executes before Casbin ABAC, read-only checks, and PII scrubbing; `TenantAdmin` is granted access to cluster-wide `McpAdminTools`. |
| **`SEC-HIGH-10`** | **High** | 7.9 | Four-Eyes Step-Up Bypass in MCP (CWE-863) | [`McpDatasetTools.cs:58-63, 85-93`](file:///root/lis-git/autheris/src/Autheris.Application/Mcp/Services/McpDatasetTools.cs#L58-L93), [`AiDataGuardrailService.cs:346-568`](file:///root/lis-git/autheris/src/Autheris.Application/Mcp/Services/AiDataGuardrailService.cs#L346-L568) | `query_sql` resolves to synthetic `governance.sql.query` and `invoke_api` returns `ReadsRows = false`, allowing AI agents to query `RequiresFourEyes = true` tables without HitL Step-Up approval. |
| **`SEC-HIGH-11`** | **High** | 7.5 | SSRF in Outbound Webhooks & Shadowing (CWE-918) | [`WebhookDispatcherService.cs:89-113`](file:///root/lis-git/autheris/src/Autheris.Application/Webhooks/WebhookDispatcherService.cs#L89-L113), [`TrafficShadowingService.cs:145-174`](file:///root/lis-git/autheris/src/Autheris.Application/Diagnostics/Shadowing/TrafficShadowingService.cs#L145-L174) | Neither `WebhookDispatcherService` nor `TrafficShadowingService` calls `EgressUrlPolicy.ValidateResolvedAsync`, and `new Uri(targetBaseUri, request.PathAndQuery.TrimStart('/'))` allows authority override. |
| **`SEC-HIGH-12`** | **High** | 7.5 | Plaintext TOTP Seeds & Replay Race (CWE-312 / CWE-362) | [`ClusterTotpSecretStore.cs:24-45`](file:///root/lis-git/autheris/src/Autheris.Application/Security/Totp/Services/ClusterTotpSecretStore.cs#L24-L45), [`TotpVerificationService.cs:76-139`](file:///root/lis-git/autheris/src/Autheris.Application/Security/Totp/Services/TotpVerificationService.cs#L76-L139) | Base32 TOTP seeds are stored unencrypted in Redis; `VerifyAndConsumeTotpAsync` has a non-atomic `GetAsync`/`SetAsync` TOCTOU window and no per-user brute-force lockout. |
| **`SEC-HIGH-13`** | **High** | 8.2 | Federated Query Allowlist & Masking Bypass (CWE-863) | [`CrossSourceQueryRouter.cs:86-155`](file:///root/lis-git/autheris/src/Autheris.Application/Sql/Services/CrossSourceQueryRouter.cs#L86-L155), [`CrossSourcePlanner.cs:152, 330`](file:///root/lis-git/autheris/src/Autheris.Application/Sql/Services/CrossSourcePlanner.cs#L152), [`FederatedStagingService.cs:241`](file:///root/lis-git/autheris/src/Autheris.Application/Olap/FederatedStagingService.cs#L241) | Cross-source queries skip `ISqlDataSourceResolver.ResolveAllowedDataSource`, call `GetColumnAccess` instead of `GetEffectiveColumnAccess(col, meta)` (bypassing catalog column masking), and fall back to `stagingName ??= "s0"`. |
| **`SEC-HIGH-14`** | **High** | 7.7 | Stored Procedure Column Policy Override (CWE-863) | [`GovernedProcedureExecutionService.cs:614-621`](file:///root/lis-git/autheris/src/Autheris.Application/Procedures/Services/GovernedProcedureExecutionService.cs#L614-L621) | `@result-column <col> clear` (`ClearedResultColumns`) overwrites `ColumnAccessLevel.Deny` and `ColumnAccessLevel.Mask` on mapped catalog columns after policy evaluation. |
| **`SEC-HIGH-15`** | **High** | 7.5 | Masked Column Oracle Inference Bypass (CWE-200) | [`AstSecurityVisitor.cs:834-850`](file:///root/lis-git/autheris/src/TrinoSqlEngine/Ast/Visitors/AstSecurityVisitor.cs#L834-L850), [`SqlQueryAnalyzer.cs:228-236`](file:///root/lis-git/autheris/src/TrinoSqlEngine/Analysis/SqlQueryAnalyzer.cs#L228-L236), [`GovernedSqlRewriter.cs:601-616`](file:///root/lis-git/autheris/src/Autheris.Application/Sql/Services/GovernedSqlRewriter.cs#L601-L616) | `RejectMaskedColumnsInPredicates` is bypassed via derived table subqueries (`SubqueryTableSource` omitted), `GROUP BY`, `JOIN ON`, and `SELECT CASE WHEN` conditions. |
| **`SEC-HIGH-16`** | **High** | 7.6 | Virtual Filter `Joins` Omission & Unscoped Cache (CWE-863) | [`DefaultVirtualFilterKeyProvider.cs:97-112, 160-245`](file:///root/lis-git/autheris/src/Autheris.Application/VirtualFilters/Services/DefaultVirtualFilterKeyProvider.cs#L160-L245) | `LoadTuplesFromSourceAsync` ignores `filter.Structured.Joins` (expanding authorized key sets fail-open) and keys `_inMemoryTuples` solely by `filter.Name` across tenants. |
| **`SEC-HIGH-17`** | **High** | 7.8 | Audit Circuit-Breaker Auto-Clears on Breach (CWE-755) | [`PostgreSqlGovernanceRepository.Audit.cs:132-135, 1034`](file:///root/lis-git/autheris/src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Audit.cs#L132-L135), [`SqlServerGovernanceRepository.Audit.cs:126-129`](file:///root/lis-git/autheris/src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.Audit.cs#L126-L129), [`SqliteGovernanceRepository.Audit.cs:151-154`](file:///root/lis-git/autheris/src/Autheris.Infrastructure/Persistence/SqliteGovernanceRepository.Audit.cs#L151-L154) | `ProcessAuditChannelAsync` resets `_isAuditPipelineFaulted = false` on the next Tier-B flush even when `_auditChainViolation != null`, disarming the fail-closed audit circuit breaker. |
| **`SEC-HIGH-18`** | **High** | 7.7 | Hardcoded Legacy Audit HMAC Key in SQLite (CWE-798) | [`SqliteGovernanceRepository.Audit.cs:704-725`](file:///root/lis-git/autheris/src/Autheris.Infrastructure/Persistence/SqliteGovernanceRepository.Audit.cs#L704-L725) | `VerifyAuditHashChainAsync` accepts a hardcoded `"AutherisAuditLogHmacTamperEvidenceSecret2026!"` key when `SequenceNumber IS NULL` and checks `anchor.Sequence == position` instead of `seq.Value`. |
| **`SEC-HIGH-19`** | **High** | 7.5 | Integer Overflow & `StackOverflowException` DoS (CWE-190 / CWE-674) | [`GraphQlTreeBuilder.cs:41-59, 144-162, 199-279`](file:///root/lis-git/autheris/src/Autheris.GraphQL/Catalog/GraphQlTreeBuilder.cs#L144-L162) | Unchecked `long` multiplication in `EstimateRows` wraps around to negative numbers on deep nested lists (bypassing `ValidateTreeBudget`); recursive `ParseObjectFilter` lacks depth limits. |
| **`SEC-HIGH-20`** | **High** | 7.8 | Lakehouse RLS Bypass on Unprojected Columns & Cross-Tenant GraphQL Admin (CWE-863) | [`DeltaLakeDataSourceExecutor.cs:124-126, 176-192`](file:///root/lis-git/autheris/src/Autheris.Extensions/Lakehouse/Services/DeltaLakeDataSourceExecutor.cs#L124-L192), [`LakehouseDataSourceExecutor.cs:66-118`](file:///root/lis-git/autheris/src/Autheris.Extensions/Lakehouse/Services/LakehouseDataSourceExecutor.cs#L66-L118), [`QueryTypes.cs:369`](file:///root/lis-git/autheris/src/Autheris.GraphQL/Types/QueryTypes.cs#L369), [`CdcSubscriptionGovernor.cs:80`](file:///root/lis-git/autheris/src/Autheris.GraphQL/Subscriptions/CdcSubscriptionGovernor.cs#L80) | Delta Lake skips loading unrequested RLS filter columns from Parquet before evaluating `FilterRows` (causing `!=` predicates to evaluate true against `null`); GraphQL `catalog` and `cdc_all` treat tenant `GovernanceAdmin` as global `ClusterAdmin`. |

*(Plus 25 Medium and 9 Low/Informational findings detailed in Sections 4.3 and 4.4 below.)*

---

## 4. Detailed Vulnerability Analysis & Concrete C# Remediations

### 4.1 Critical Severity Findings (`SEC-CRIT-01` – `SEC-CRIT-06`)

---

#### `SEC-CRIT-01`: Broken Access Control, Role Spoofing, and Arbitrary Data-Owner Reassignment in Table Classification Endpoints
- **Severity:** Critical (CVSS 9.1)
- **Location:** [`src/Autheris.Api/Endpoints/GovernanceClassificationEndpoints.cs:26-190`](file:///root/lis-git/autheris/src/Autheris.Api/Endpoints/GovernanceClassificationEndpoints.cs#L26-L190)
- **Vulnerable Mechanism:**
  All 8 endpoints in `MapGovernanceClassificationEndpoints` (`/api/governance/classification/*`) only call `.RequireAuthorization()` without any role policy:
  ```csharp
  var group = app.MapGroup("/api/governance/classification")
      .RequireAuthorization();
  ...
  // Lines 116-119: Client-controlled ReviewerRole and fallback ReviewerSid
  var reviewerSid = httpContext.User.GetUserSid()?.Value ?? payload.ReviewerSid;
  var reviewerRole = !string.IsNullOrWhiteSpace(payload.ReviewerRole) ? payload.ReviewerRole : "DATA_OWNER";
  ...
  // Lines 166-179: Any authenticated user can overwrite DataOwnerSid of any table
  group.MapPost("/assign-owner", async (
      [FromBody] AssignDataOwnerPayload payload,
      [FromServices] ITableClassificationStore store,
      CancellationToken ct) =>
  {
      await store.UpdateDataOwnerAsync(payload.Catalog, payload.Schema, payload.TableName, payload.DataOwnerSid, ct);
      return Results.Ok(new { ... });
  });
  ```
- **Exploitation Scenario:**
  Any low-privileged authenticated user (`Analyst` or `Consumer`) can call `POST /api/governance/classification/assign-owner` to set `DataOwnerSid` of a restricted table to their own SID, and then self-approve consent requests and HitL step-up tickets for that table, or spoof `"ReviewerRole": "DATA_GOVERNANCE_REVIEWER"` in `/approve` and `/transition`.
- **Remediation:**
  Enforce `GatewayPolicies.GovernanceAdmin` on administrative classification mutations (`/classify-table`, `/assign-owner`, `/transition`), and on `/proposals/{proposalId}/approve` and `/reject` derive `reviewerSid` and `reviewerRole` strictly from `httpContext.User` claims and verify table ownership via `EndpointSecurity.IsTableDataOwnerAsync`.

---

#### `SEC-CRIT-02`: Production-Exposed `/portal/*` Endpoints Allow Unauthorized HitL Step-Up Ticket Listing & Approval Bypass
- **Severity:** Critical (CVSS 8.8)
- **Location:** [`src/Autheris.Api/Endpoints/DevPortalEndpoints.cs:27-174`](file:///root/lis-git/autheris/src/Autheris.Api/Endpoints/DevPortalEndpoints.cs#L27-L174)
- **Vulnerable Mechanism:**
  While `GET /` and `GET /getting-started` check `if (!env.IsDevelopment()) return Results.NotFound();`, `/portal/2fa/enroll`, `/portal/2fa/enroll/verify`, `/portal/approvals`, and `/portal/approvals/{ticketId}/step-up` are mapped in all environments with `.WithAuditExemption("DevPortal UI")` and omit the `options.Value.HitLStepUp.Enabled`, `EndpointSecurity.IsApprover(context.User)`, and `EnsureTableApproverAsync(ticket.TargetTable, ...)` checks enforced by `HitLEndpoints.cs`.
- **Exploitation Scenario:**
  In Production, any authenticated user in the same tenant can list pending `ticketId`s via `GET /portal/approvals`, enroll their own TOTP secret, and call `POST /portal/approvals/{ticketId}/step-up` to approve high-risk Four-Eyes step-up requests without holding `DataOwner` or `GovernanceAdmin` privileges and with audit logging exempted.
- **Remediation:**
  Enforce `EndpointSecurity.IsApprover(context.User)` and `HitLEndpoints.EnsureTableApproverAsync` inside `/portal/approvals` and `/portal/approvals/{ticketId}/step-up`, remove `.WithAuditExemption` from state-mutating POST endpoints, and HTML-encode all interpolated ticket fields (`SEC-MED-01`).

---

#### `SEC-CRIT-03`: Optional/Null TOTP Dependencies Allow MFA Bypass & Non-Consuming `VerifyTotp` Allows Replay in `AccessPlanningService`
- **Severity:** Critical (CVSS 9.0)
- **Location:** [`src/Autheris.Application/Governance/Services/AccessPlanningService.cs:79-80, 180-187`](file:///root/lis-git/autheris/src/Autheris.Application/Governance/Services/AccessPlanningService.cs#L180-L187)
- **Vulnerable Mechanism:**
  ```csharp
  if (_totpService != null && _totpSecretStore != null)
  {
      var secret = await _totpSecretStore.GetSecretAsync(adminSid, ct).ConfigureAwait(false);
      if (string.IsNullOrWhiteSpace(secret) || !_totpService.VerifyTotp(secret, totpCode))
      {
          throw new SecurityException("Invalid TOTP verification code.");
      }
  }
  ```
  1. If `_totpService` or `_totpSecretStore` is null, `ConfirmPlanAsync` fails open and issues a valid HMAC confirmation token without verifying `totpCode`.
  2. Line 183 calls `_totpService.VerifyTotp(secret, totpCode)` instead of `VerifyAndConsumeTotpAsync(adminSid, secret, totpCode, ct)`, allowing the same 6-digit TOTP code to be replayed across multiple access plans within the 90s validity window.
- **Remediation:**
  ```csharp
  if (_totpService is null || _totpSecretStore is null)
  {
      throw new SecurityException("TOTP verification services are not configured. Failing closed (ADR-05).");
  }

  var secret = await _totpSecretStore.GetSecretAsync(adminSid, ct).ConfigureAwait(false);
  if (string.IsNullOrWhiteSpace(secret) ||
      !await _totpService.VerifyAndConsumeTotpAsync(adminSid, secret, totpCode, ct).ConfigureAwait(false))
  {
      throw new SecurityException("Invalid or already-consumed TOTP verification code.");
  }
  ```

---

#### `SEC-CRIT-04`: Full SSRF, DNS Rebinding & HTTP Redirect Bypass in `CatalogDiscoveryService.RegisterDatasourceAsync`
- **Severity:** Critical (CVSS 9.1)
- **Location:** [`src/Autheris.Application/Catalog/Services/CatalogDiscoveryService.cs:308-320`](file:///root/lis-git/autheris/src/Autheris.Application/Catalog/Services/CatalogDiscoveryService.cs#L308-L320)
- **Vulnerable Mechanism:**
  ```csharp
  EgressUrlPolicy.ValidateStatic(specUri, isDev: false, enforceHttps: false);
  using var httpClient = new HttpClient();
  httpClient.Timeout = TimeSpan.FromSeconds(30);
  specContent = await httpClient.GetStringAsync(specUri, ct).ConfigureAwait(false);
  ```
  1. Calls `ValidateStatic` with `enforceHttps: false` and never calls `EgressUrlPolicy.ValidateResolvedAsync`, allowing DNS names that resolve to `127.0.0.1`, `169.254.169.254`, or RFC 1918 private networks.
  2. Uses a raw `new HttpClient()` instead of `SecureOutboundHttp.CreatePrimaryHandler`, so HTTP `301`/`302` redirects to internal/metadata endpoints are followed automatically.
  3. `GetStringAsync` buffers an unbounded response body into memory.
- **Remediation:**
  ```csharp
  await EgressUrlPolicy.ValidateResolvedAsync(specUri, isDev: false, ct).ConfigureAwait(false);
  using var handler = SecureOutboundHttp.CreatePrimaryHandler(isDevelopment: false);
  using var httpClient = new HttpClient(handler, disposeHandler: true)
  {
      Timeout = TimeSpan.FromSeconds(30),
      MaxResponseContentBufferSize = 5 * 1024 * 1024
  };
  specContent = await httpClient.GetStringAsync(specUri, ct).ConfigureAwait(false);
  ```

---

#### `SEC-CRIT-05`: Operator Precedence RLS Bypass & Raw SQL Injection in `GovernedDataQueryService` / `DataApiFilterTranslator`
- **Severity:** Critical (CVSS 9.3)
- **Location:**
  - [`src/Autheris.Application/Data/Services/GovernedDataQueryService.cs:124-192, 251-255`](file:///root/lis-git/autheris/src/Autheris.Application/Data/Services/GovernedDataQueryService.cs#L124-L192)
  - [`src/Autheris.Application/Data/Services/DataApiFilterTranslator.cs:26-127`](file:///root/lis-git/autheris/src/Autheris.Application/Data/Services/DataApiFilterTranslator.cs#L26-L127)
- **Vulnerable Mechanism:**
  1. **Operator Precedence RLS Bypass:** `GovernedDataQueryService.cs:167` adds `translated` (`request.FilterExpression`) to `predicates` **without parentheses** and joins with `" AND "` against `({decision.CombinedRowFilterSql})`. A filter containing `or` (`id = 1 or id = 2`) produces `WHERE id = 1 OR id = 2 AND (<RLS>)`, where SQL `AND` binds only to the right branch of `OR`, completely bypassing `decision.CombinedRowFilterSql` on the left branch.
  2. **Arbitrary SQL Expression & Masked Column Oracle Bypass:** `DataApiFilterTranslator` only runs regex keyword replacements (`eq` -> `=`) without validating AST structure, and `ExtractOrderByColumns` only inspects `tokens[0]` after splitting by space. If `orderBy` or `filterExpression` references a parenthesized expression `(masked_col)` or unknown token, `metadata.GetColumn(col)` returns `null` and `decision.GetEffectiveColumnAccess` returns `Clear` when `HasUnconstrainedColumnAllow` is `true`.
  3. **Double Masking with Unscoped HMAC Key:** Lines 251–255 re-apply `_maskingProvider.MaskValue` in memory on rows already masked by `_sqlExecutionService.ExecuteQueryBufferedAsync`, using the raw unscoped `MaskingRule`.
- **Remediation:**
  1. Wrap user filter predicates in outer parentheses: `predicates.Add($"({translated})");`.
  2. Parse and validate `FilterExpression` and `OrderBy` via `ODataFilterParser` or `TrinoSqlEngine` AST, rejecting any column not present in `metadata.Columns`.
  3. Delegate RLS and SQL column masking to `_sqlExecutionService` (or scope HMAC keys via `rule.CreateTenantScopedHmacRule` when falling back to in-memory masking).

---

#### `SEC-CRIT-06`: WORM Audit Chain Anchor Laundering on Violation in PostgreSQL & SQL Server
- **Severity:** Critical (CVSS 8.9)
- **Location:**
  - [`src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Audit.cs:529-545`](file:///root/lis-git/autheris/src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Audit.cs#L529-L545)
  - [`src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.Audit.cs:564-580`](file:///root/lis-git/autheris/src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.Audit.cs#L564-L580)
- **Vulnerable Mechanism:**
  Unlike `SqliteGovernanceRepository.Audit.cs:442` (which guards anchor writes with `Volatile.Read(ref _auditChainViolation) == null` and `TryLoadVerifiedAnchor(out var current)`), both PostgreSQL and SQL Server unconditionally execute:
  ```csharp
  var current = _auditAnchorStore.Load();
  if (!current.Exists || current.Sequence < anchor.Sequence)
  {
      _auditAnchorStore.Save(anchor);
  }
  ```
  When an anchor file is corrupted/tampered (`AuditChainAnchor.Invalid` has `Sequence = -1`) or the DB audit table is truncated after an initial violation (which is swallowed by `InitializeDatabaseSafely`), the very next audit batch overwrites the external anchor file with a valid HMAC signature for the truncated chain.
- **Remediation:**
  Guard anchor updates in both PostgreSQL and SQL Server with `Volatile.Read(ref _auditChainViolation) == null` and `TryLoadVerifiedAnchor(out var current)`, add the `sequencedSectionStarted` check in `VerifyAuditHashChainAsync`, and rethrow `AuditChainIntegrityException` in `InitializeDatabaseSafely`.

---

### 4.2 High Severity Findings (`SEC-HIGH-01` – `SEC-HIGH-20`)

1. **`SEC-HIGH-01` – Unverified 2FA TOTP Secret Overwrite (`GovernanceApiEndpoints.cs:91-151`, `DevPortalEndpoints.cs:68-118`):**
   - *Issue:* `POST /api/v1/governance/2fa/verify-enrollment` accepts a client-supplied `SecretBase32` and overwrites any existing TOTP secret for `userSid` without requiring verification of a code from the existing secret.
   - *Fix:* Store pending enrollment secrets server-side during `/enroll`, reject client-supplied `SecretBase32`, and if `await secretStore.HasSecretAsync(userSid, ct)` is already `true`, require a valid TOTP code from the *currently active* secret before replacing it.

2. **`SEC-HIGH-02` – Missing Admin Policy on Documentation Ingestion (`DocIngestionEndpoints.cs:20-121`):**
   - *Issue:* `POST /api/v1/docs/apps/{appId}/bundle` and `PUT /api/v1/docs/apps/{appId}/documents/{category}/{docId}` only require `.RequireAuthorization()`, and `/bundle` buffers raw streams into a `MemoryStream` before checking `ms.Length`.
   - *Fix:* Add `.RequireAuthorization(GatewayPolicies.GovernanceAdmin)` to both write endpoints and use `request.ReadBodyAsBytesAsync(50 * 1024 * 1024, ct)`.

3. **`SEC-HIGH-03` – Missing Admin Policy on DataHub MCL Event Ingestion (`DataContractEndpoints.cs:71-99`):**
   - *Issue:* `POST /api/v1/catalog/datahub/events` and `GET /api/v1/governance/contracts` allow any authenticated caller to inject DataHub metadata events or list all contracts globally.
   - *Fix:* Enforce `EndpointSecurity.IsGlobalGovernanceAdmin(context.User)` (or `.RequireAuthorization(GatewayPolicies.GovernanceAdmin)`).

4. **`SEC-HIGH-04` – Pre-Auth Rate Limit Bypass via Unverified `Bearer ` Header (`RateLimitingMiddleware.cs:47-55, 136-141`):**
   - *Issue:* `PreAuthIpRateLimitingMiddleware` skips rate limiting when `Authorization: Bearer ...` is present, while `PostAuthSidRateLimitingMiddleware` skips rate limiting when `IsAuthenticated != true`. Sending `Authorization: Bearer invalid` bypasses both rate limiters.
   - *Fix:* In `PostAuthSidRateLimitingMiddleware`, if `context.User.Identity?.IsAuthenticated != true`, enforce `_rateLimiter.CheckPreAuthIpAsync` so unauthenticated requests with bogus `Bearer` headers are rate-limited by IP.

5. **`SEC-HIGH-05` – Confirmation Token Not Bound to Stored Token/Approver & Hardcoded `"default"` Tenant (`AccessPlanningService.cs:196-280, 469-509`):**
   - *Issue:* `ValidateConfirmationToken` ignores the `adminSid` segment in the token payload and never checks `request.ConfirmationToken == stored.ConfirmedToken`; `ApplyAccessAsync` hardcodes `var tenantId = "default";` and `wormSignature = Guid.NewGuid().ToString("N")`.
   - *Fix:* Compare `request.ConfirmationToken` against `stored.ConfirmedToken` via `CryptographicOperations.FixedTimeEquals`, bind `TenantId` in `StoredPlan`, and compute a real HMAC signature.

6. **`SEC-HIGH-06` – Unverified Step-Up Token, Empty `ColumnDecisions` Rank Bypass & Stage Skip (`ClassificationWorkflowStateMachine.cs:124-185`):**
   - *Issue:* `StepUpAuthToken` is only checked with `!string.IsNullOrWhiteSpace`; `maxEffectiveRank` is computed only over `request.ColumnDecisions` (so `[]` yields rank `0`, bypassing Step-Up and Four-Eyes while sealing all `proposal.Columns`); `DATA_GOVERNANCE_REVIEWER` skips Stage 1 (`PENDING_OWNER_APPROVAL`).
   - *Fix:* Compute `maxEffectiveRank` across all `proposal.Columns` with overrides applied, verify `StepUpAuthToken` via `ITotpVerificationService.VerifyAndConsumeTotpAsync`, and require `proposal.Status == "PENDING_GOVERNANCE_REVIEW"` before Stage 2 approval when `TwoStageApprovalRequired` is `true`.

7. **`SEC-HIGH-07` – Fail-Open `ConsentEffect.Allow` Default & Missing Role Check (`DeclarativeConsentApplyService.cs:21-65, 243-248`):**
   - *Issue:* Unrecognized `policy.Effect` strings fall back to `effect = ConsentEffect.Allow;`; service does not verify `GovernanceAdmin`/`ClusterAdmin` role; `IsIdentical` omits `ValueSource` and `UserAttribute` on row filters.
   - *Fix:* Throw `ArgumentException` on invalid `ConsentEffect` or `GranteeType` (Fail-Closed), enforce admin role checks on `caller`, and compare `ValueSource` and `UserAttribute` in `IsIdentical`.

8. **`SEC-HIGH-08` – Cross-Tenant Default Overloads, Unverified Remote Ticket HMAC & Fail-Open Lock (`HitLStepUpApprovalService.cs:326-332, 466-474, 549-554, 683-704`):**
   - *Issue:* String overloads of `ApproveStepUpRequestAsync`/`RejectStepUpRequestAsync` pass `IsCrossTenantAdmin: true`; `GetTicketAsync` does not call `VerifyTicketSignature(_hmacKey, remoteTicket)` on tickets loaded from Redis; `AcquireDistributedLockAsync` returns `(true, null)` on Redis failure even when `FailClosedOnClusterPartition` is `true`.
   - *Fix:* Set `IsCrossTenantAdmin: false` in string overloads, verify `VerifyTicketSignature` in `GetTicketAsync`, and return `(false, null)` in `AcquireDistributedLockAsync` when `_options.FailClosedOnClusterPartition` is `true`.

9. **`SEC-HIGH-09` – `McpDocTools` Bypasses Casbin ABAC/Read-Only/PII Scrubbing & `TenantAdmin` Escalation on `McpAdminTools` (`AiDataGuardrailService.cs:233-297`, `McpProtocolHandler.cs:167-172`):**
   - *Issue:* `McpDocTools` (`deposit_documentation`) executes at Step 1b before Casbin ABAC, read-only token checks, and PII scrubbing; `TenantAdmin` is permitted to execute cluster-wide `McpAdminTools`.
   - *Fix:* Require `GovernanceAdmin`/`ClusterAdmin` and `!sessionContext.IsReadOnly` for `deposit_documentation` and `McpAdminTools`, evaluate Casbin ABAC first, and run PII scrubbing (`ScrubPiiAndSensitiveData`) + token budget enforcement on tool outputs before returning.

10. **`SEC-HIGH-10` – `invoke_api` and `query_sql` Bypass Dataset Four-Eyes Step-Up in MCP (`McpDatasetTools.cs:58-63, 85-93`, `AiDataGuardrailService.cs:346-568`):**
    - *Issue:* `McpDatasetTools.ReadsRows("invoke_api")` returns `false` and `TargetTable("query_sql")` returns synthetic `governance.sql.query`, skipping `RequiresFourEyes` table metadata checks in `AiDataGuardrailService`.
    - *Fix:* Include `InvokeApi` in `McpDatasetTools.ReadsRows` and extract actual target `TableIdentifier`s from the SQL query AST (`ISqlQueryAnalyzer`) and `/api/v1/data/{db}/{schema}/{table}` path in `AiDataGuardrailService.ResolveTargetTables`.

11. **`SEC-HIGH-11` – Missing SSRF Validation in `WebhookDispatcherService` & `TrafficShadowingService` (`WebhookDispatcherService.cs:89-113`, `TrafficShadowingService.cs:145-174`):**
    - *Issue:* `WebhookDispatcherService` posts to `subscription.TargetUrl` without calling `EgressUrlPolicy.ValidateResolvedAsync`; `TrafficShadowingService` does not validate `TargetBaseUrl` and constructs `new Uri(targetBaseUri, request.PathAndQuery.TrimStart('/'))` without preventing authority override.
    - *Fix:* Call `await EgressUrlPolicy.ValidateResolvedAsync(...)` before sending requests in both services and verify `targetUri.Host == targetBaseUri.Host && targetUri.Scheme == targetBaseUri.Scheme`.

12. **`SEC-HIGH-12` – Plaintext TOTP Seeds in Redis, Replay Race Condition & Missing Lockout (`ClusterTotpSecretStore.cs:24-45`, `TotpVerificationService.cs:76-139`):**
    - *Issue:* `ClusterTotpSecretStore` stores raw Base32 TOTP secrets in Redis; `VerifyAndConsumeTotpAsync` has a non-atomic `GetAsync` + `SetAsync` window and no per-`userSid` brute-force lockout.
    - *Fix:* Encrypt TOTP secrets at rest via `IDataProtectionProvider`, acquire a distributed lock per `(userSid, matchedStep)` during consumption, and enforce a failed-attempt lockout counter.

13. **`SEC-HIGH-13` – Federated Query Tenant Allowlist Bypass, Catalog Masking Bypass & Fail-Open Table Resolution (`CrossSourceQueryRouter.cs:86-155`, `CrossSourcePlanner.cs:152, 330`, `FederatedStagingService.cs:241`):**
    - *Issue:* Cross-source federated queries skip `_dataSourceResolver.ResolveAllowedDataSource(target.Catalog, request.TenantId)`, call `dec.GetColumnAccess` instead of `dec.GetEffectiveColumnAccess(col.ColumnName, meta)` (bypassing catalog sensitivity/masking rules), and fall back to `stagingName ??= "s0"` when a table name is unresolved.
    - *Fix:* Validate each catalog via `ResolveAllowedDataSource`, use `GetEffectiveColumnAccess(col.ColumnName, meta)`, run `AstSecurityVisitor` on federated SQL, and throw `SecurityException` if `stagingName == null`.

14. **`SEC-HIGH-14` – Stored Procedure `@result-column <col> clear` Overrides `Deny`/`Mask` (`GovernedProcedureExecutionService.cs:614-621`):**
    - *Issue:* `if (cleared.Contains(col)) level = ColumnAccessLevel.Clear;` overrides `ColumnAccessLevel.Deny` and `ColumnAccessLevel.Mask` on mapped catalog columns.
    - *Fix:* Only allow `ClearedResultColumns` to authorize unmapped computed columns (lines 577–586); never downgrade `Deny` or `Mask` on columns mapped to catalog tables.

15. **`SEC-HIGH-15` – Masked Column Oracle Inference Bypasses (`AstSecurityVisitor.cs:834-850`, `SqlQueryAnalyzer.cs:228-236`, `GovernedSqlRewriter.cs:601-616`):**
    - *Issue:* `CollectTableSources` omits `SubqueryTableSource`; `SqlQueryAnalyzer` omits `context.groupBy()` and `SELECT CASE WHEN` conditions; `GovernedSqlRewriter` omits `analysis.JoinColumnReferences`.
    - *Fix:* Handle `SubqueryTableSource` in `CollectTableSources`, extract filter references from `groupBy()` and conditional expressions in `SELECT`, and check `JoinColumnReferences` against `maskedColumns`.

16. **`SEC-HIGH-16` – Virtual Filter Key Provider Ignores `Structured.Joins` (`DefaultVirtualFilterKeyProvider.cs:97-112, 160-245`):**
    - *Issue:* `LoadTuplesFromSourceAsync` ignores `filter.Structured.Joins` and caches `_inMemoryTuples` solely by `filter.Name` without tenant scoping.
    - *Fix:* Throw `GatewaySecurityException` if `filter.Structured.Joins is { Count: > 0 }` in the direct key provider (forcing DuckDB/SQL evaluation) and scope tuple keys by `(tenantId, filter.Name)`.

17. **`SEC-HIGH-17` – Audit Circuit-Breaker Flag Auto-Clears After Chain Tampering (`PostgreSqlGovernanceRepository.Audit.cs:132-135`, `SqlServerGovernanceRepository.Audit.cs:126-129`, `SqliteGovernanceRepository.Audit.cs:151-154`):**
    - *Issue:* `ProcessAuditChannelAsync` resets `_isAuditPipelineFaulted = false` after flushing the next Tier-B batch even when `_auditChainViolation != null`.
    - *Fix:* Define `public bool IsAuditPipelineFaulted => _isAuditPipelineFaulted || Volatile.Read(ref _auditChainViolation) != null;` and only clear `_isAuditPipelineFaulted` when `Volatile.Read(ref _auditChainViolation) == null`.

18. **`SEC-HIGH-18` – Hardcoded Legacy HMAC Secret in SQLite Audit Verification (`SqliteGovernanceRepository.Audit.cs:704-725`):**
    - *Issue:* `VerifyAuditHashChainAsync` falls back to `"AutherisAuditLogHmacTamperEvidenceSecret2026!"` whenever `SequenceNumber IS NULL` and compares `anchor.Sequence == position` instead of `seq.Value`.
    - *Fix:* Remove the hardcoded `legacyKey` fallback and require `seq.HasValue && seq.Value == anchor.Sequence`.

19. **`SEC-HIGH-19` – Integer Overflow & Unbounded Recursion in `GraphQlTreeBuilder` (`GraphQlTreeBuilder.cs:41-59, 144-162, 199-279`):**
    - *Issue:* `EstimateRows` uses unchecked `long` multiplication that overflows to negative values on deep `first: 1000` nested relations (bypassing `estimatedRows > maxBudget`), and `ParseObjectFilter` recurses on `not`/`and`/`or` without a depth limit.
    - *Fix:* Use `checked` arithmetic saturating at `maxBudget + 1` in `EstimateRows` and enforce `MaxFilterDepth = 16` and `MaxTreeDepth = 10`.

20. **`SEC-HIGH-20` – Lakehouse RLS Bypass on Unprojected Columns & GraphQL/CDC `GovernanceAdmin` Escalation (`DeltaLakeDataSourceExecutor.cs:124-192`, `LakehouseDataSourceExecutor.cs:66-118`, `QueryTypes.cs:369`, `CdcSubscriptionGovernor.cs:80`):**
    - *Issue:* `DeltaLakeDataSourceExecutor` only loads `context.RequestedFields` from Parquet before running `FilterRows(pageRows, context.CombinedRowFilterSql)`, so unprojected RLS columns evaluate as `null` (bypassing `!=` predicates); `LakehouseDataSourceExecutor` ignores `SelectedColumns` and truncates at `fetchLimit` before RLS filtering; `QueryTypes.GetCatalogAsync`, `MutationTypes`, and `CdcSubscriptionGovernor` treat tenant-scoped `GovernanceAdmin` as global `ClusterAdmin`.
    - *Fix:* Always include columns referenced in `CombinedRowFilterSql` when reading Parquet, filter rows before applying `fetchLimit`, strip unrequested RLS columns before returning, and use `ClusterAdminPolicy.IsCanonicalClusterAdmin(user)` for cross-tenant operations.

---

### 4.3 Medium Severity Findings (`SEC-MED-01` – `SEC-MED-25`)

| ID | File & Lines | Vulnerability Mechanism | Concrete Remediation |
| :--- | :--- | :--- | :--- |
| **`SEC-MED-01`** | [`DevPortalEndpoints.cs:457, 558-565, 601`](file:///root/lis-git/autheris/src/Autheris.Api/Endpoints/DevPortalEndpoints.cs#L558-L565) | `GenerateApprovalsHtml` and `Generate2FaEnrollHtml` interpolate `t.ApprovalId`, `t.RequesterSid`, `t.TargetTable`, `t.ToolName`, and `userSid` into raw HTML without `HtmlEncode`. | Wrap all dynamic values with `HtmlEncode(...)` and replace inline `onclick` attributes with `data-*` attributes + external event listeners. |
| **`SEC-MED-02`** | [`TrafficShadowingMiddleware.cs:50-81`](file:///root/lis-git/autheris/src/Autheris.Api/Middleware/TrafficShadowingMiddleware.cs#L50-L81) | Copies `Authorization`, `Cookie`, and `X-Forward-Auth-Secret` headers into `ShadowRequest` and calls `ReadToEndAsync` without a byte cap when `ContentLength` is null (chunked). | Strip sensitive auth headers before creating `ShadowRequest` and use `RequestBodyLimitExtensions.ReadBodyAsStringAsync` capped at 2 MB. |
| **`SEC-MED-03`** | [`AccessAuditMiddleware.cs:67-78`](file:///root/lis-git/autheris/src/Autheris.Api/Middleware/AccessAuditMiddleware.cs#L67-L78) | Runs inner to `GatewayExceptionHandler`; unhandled `GatewaySecurityException` (`403`) is logged with `statusCode: 200` and `Decision: "ERROR"` instead of `"DENY"`. | Catch `GatewaySecurityException`, `GatewayUnauthorizedException`, and `GatewayThrottledException` in `AccessAuditMiddleware` and call `auditContext.Deny(...)` with the mapped status code. |
| **`SEC-MED-04`** | [`WebhookEndpoints.cs:38-46, 199-223, 299-323`](file:///root/lis-git/autheris/src/Autheris.Api/Endpoints/WebhookEndpoints.cs#L199-L223) | Unauthenticated Azure EventGrid `SubscriptionValidation` requests parse up to 10 MB JSON and echo `validationResponse`; `/api/webhooks/openmetadata` lacks explicit null-signature rejection. | Cap `SubscriptionValidation` bodies at 64 KB, require allowed EventGrid topic prefix configuration, and reject missing signatures on `/api/webhooks/openmetadata` with `401`. |
| **`SEC-MED-05`** | [`DbtEndpoints.cs:377-385`](file:///root/lis-git/autheris/src/Autheris.Api/Endpoints/DbtEndpoints.cs#L377-L385), [`GovernanceEndpoints.cs:484`](file:///root/lis-git/autheris/src/Autheris.Api/Endpoints/GovernanceEndpoints.cs#L484) | `GET /api/extensions/dbt/metrics` exposes all semantic metrics without role/tenant filtering; OpenLineage sync reads raw `FindFirst("tenant_id")` instead of `EndpointSecurity.TryGetRequestTenant`. | Filter `/api/extensions/dbt/metrics` by authorized datasets and use `EndpointSecurity.TryGetRequestTenant` in OpenLineage sync. |
| **`SEC-MED-06`** | [`SqlEndpointRoutes.cs:401-426`](file:///root/lis-git/autheris/src/Autheris.Api/Endpoints/SqlEndpointRoutes.cs#L401-L426) | `if (httpContext.Request.ContentLength > 0)` silently drops chunked POST request bodies (`ContentLength == null`). | Use `EndpointSecurity.TryReadBodyAsync(httpContext, 2 * 1024 * 1024, ct)`. |
| **`SEC-MED-07`** | [`GatewayMcpServer.cs:69-80, 143-177`](file:///root/lis-git/autheris/src/Autheris.Api/Mcp/GatewayMcpServer.cs#L143-L177) | `ReadResourceAsync` for `autheris://catalog/*`, `autheris://api/openapi.json`, and semantic resources bypasses `IAiDataGuardrailService` (rate limits, token budgets, audit). | Route all MCP resource reads through `IAiDataGuardrailService` or record audit & token budget usage consistently. |
| **`SEC-MED-08`** | [`JustificationAndBreakGlassInterceptor.cs:21, 119-171`](file:///root/lis-git/autheris/src/Autheris.Application/Extensibility/Interceptors/JustificationAndBreakGlassInterceptor.cs#L119-L171) | Sets `context.Items["IsBreakGlass"] = true` before checking `_governanceRepo != null` and before `RecordAuditEventAsync` completes. | Deny break-glass if `_governanceRepo is null` and set `context.Items["IsBreakGlass"] = true` only after `RecordAuditEventAsync` succeeds. |
| **`SEC-MED-09`** | [`AccessPlanningService.cs:48-50, 297-351, 422-464`](file:///root/lis-git/autheris/src/Autheris.Application/Governance/Services/AccessPlanningService.cs#L48-L50) | `_plans`, `_consumedTokens`, and `_inMemoryVault` are unbounded `ConcurrentDictionary` instances; `ResolvePrincipalAsync` queries hardcoded `sales.public.orders` across tenants. | Add TTL eviction for `_plans` and `_consumedTokens`, persist secrets via `ISecretProvider`, and scope `ResolvePrincipalAsync` to the caller's tenant. |
| **`SEC-MED-10`** | [`DatasourceTestingService.cs:147-155, 268-274`](file:///root/lis-git/autheris/src/Autheris.Application/Catalog/Services/DatasourceTestingService.cs#L147-L155) | `HealthCheckPath` with backslashes (`\\evil.com`) or scheme overrides `baseUri` authority in `Uri.TryCreate(baseUri, relativePath)`; `AdditionalHeaders` allows forbidden headers (`Host`, `Authorization`). | Reject `\` and `:` in `HealthCheckPath`, verify `probeUri.Host == baseUri.Host`, and filter `AdditionalHeaders` via `CloudEventWebhookDispatcher.IsForbiddenCustomHeader`. |
| **`SEC-MED-11`** | [`EgressUrlPolicy.cs:20, 32, 53-58`](file:///root/lis-git/autheris/src/Autheris.Application/Security/EgressUrlPolicy.cs#L20-L58) | `ValidateStatic` uses `uri.Host` instead of `EgressAddressRules.NormalizeHost(uri)` (IPv6 brackets / IDN); `ValidateResolvedAsync` returns early when `isDev == true` without blocking `IsAlwaysForbidden` metadata IPs. | Use `EgressAddressRules.NormalizeHost(uri)` and always enforce `EgressAddressRules.IsAlwaysForbidden(normIp)` even when `isDev == true`. |
| **`SEC-MED-12`** | [`SubgraphContextPropagationService.cs:25, 97-118`](file:///root/lis-git/autheris/src/Autheris.Application/Federation/Services/SubgraphContextPropagationService.cs#L97-L118) | `var isDevelopment = _environment == null || _environment.IsDevelopment();` defaults to `true` when `_environment` is omitted, falling back to hardcoded `"autheris-federation-default-secret"`. | Change to `var isDevelopment = _environment?.IsDevelopment() ?? false;`. |
| **`SEC-MED-13`** | [`McpToolExecutionHandler.cs:213-241, 273-291`](file:///root/lis-git/autheris/src/Autheris.Application/Mcp/Services/McpToolExecutionHandler.cs#L213-L291) | `get_data_lineage` returns dataset lineage without checking dataset visibility; `BuildUserPrincipal` drops `PrimarySid`, `GroupSid`, `AdditionalClaims`, and `IsReadOnly`. | Check `GetDatasetDetailAsync` before returning lineage and use `McpProtocolHandler.BuildPrincipalFromSession(sessionContext)` to build the principal. |
| **`SEC-MED-14`** | [`FileSystemUniversalDocStore.cs:224-285`](file:///root/lis-git/autheris/src/Autheris.Application/Docs/Services/FileSystemUniversalDocStore.cs#L224-L285), [`PolicySimulationService.cs:123-130`](file:///root/lis-git/autheris/src/Autheris.Application/Governance/Services/PolicySimulationService.cs#L123-L130) | `ExtractBundleAsync` lacks entry count, uncompressed byte limits (Zip-bomb), and extension allowlisting (`.md`/`manifest.json`); `PolicySimulationService` hardcodes `IPAddress.Loopback`. | Pre-validate ZIP entries (max 500 files, max 50 MB total, `.md`/`.json` only) before extracting; use `IPAddress.None` in `PolicySimulationService`. |
| **`SEC-MED-15`** | [`SqlFilterCompiler.cs:120-135, 195-346`](file:///root/lis-git/autheris/src/Autheris.Application/VirtualFilters/SqlFilterCompiler.cs#L120-L135), [`RlsListener.cs:675-686`](file:///root/lis-git/autheris/src/TrinoSqlEngine/RlsListener.cs#L675-L686), [`FastSqlEngine.cs:813-817`](file:///root/lis-git/autheris/src/TrinoSqlEngine/FastSqlEngine.cs#L813-L817) | `SqlFilterCompiler.ExpressionEquals` only compares columns/literals (missing `LOWER(x)=LOWER(x)` or `LIKE '%'`); `FastSqlEngine` runs `AstSimplificationVisitor` *after* `AstSecurityVisitor`; `RlsListener` treats `1 <= 2` as non-tautological. | Run `AstSimplificationVisitor` *before* `AstSecurityVisitor`, reject `%`-only `LIKE` patterns as tautologies, and evaluate numeric `<`/`<=`/`>`/`>=` in `RlsListener`. |
| **`SEC-MED-16`** | [`PushdownUrlFormatter.cs:36-38, 47-54`](file:///root/lis-git/autheris/src/Autheris.Application/VirtualFilters/Services/PushdownUrlFormatter.cs#L36-L54) | `FormatODataFilterIn` does not URL-encode keys (`&`, `#` inject query params); `FormatJsonBatchBody` does not escape `\` or control chars (JSON injection). | Wrap the OData `$filter` expression in `Uri.EscapeDataString` and use `JsonSerializer.Serialize` in `FormatJsonBatchBody`. |
| **`SEC-MED-17`** | [`GovernedSqlExecutionService.cs:343-429`](file:///root/lis-git/autheris/src/Autheris.Application/Sql/Services/GovernedSqlExecutionService.cs#L343-L429) | `NormalizeClientParameters` toggles `inDoubleQuote` and `inBacktick` inside single-quoted SQL literals; `RestoreClientParameters` runs `Regex.Replace` inside string literals. | Check `!inSingleQuote` before toggling `inDoubleQuote`/`inBacktick`, and use a quote-aware scanner in `RestoreClientParameters`. |
| **`SEC-MED-18`** | [`AsyncQueryJobBackgroundWorker.cs:91-109, 174-198`](file:///root/lis-git/autheris/src/Autheris.Application/Jobs/Services/AsyncQueryJobBackgroundWorker.cs#L91-L109), [`AsyncQueryJobManager.cs:154-159`](file:///root/lis-git/autheris/src/Autheris.Application/Jobs/Services/AsyncQueryJobManager.cs#L154-L159) | Caller-controlled `MaxRows` is unclamped (`int.MaxValue` OOM); `EscapeCsv` lacks CSV formula sanitization (`=`, `+`, `-`, `@`); `StartsWith(fullScratchPath)` lacks trailing directory separator. | Clamp `MaxRows` to `MaxAsyncExportRows`, neutralize leading formula characters in CSV output, and append `Path.DirectorySeparatorChar` in path containment checks. |
| **`SEC-MED-19`** | [`SqlEndpointLoader.cs:96-114, 515-537`](file:///root/lis-git/autheris/src/Autheris.Application/SqlEndpoints/Services/SqlEndpointLoader.cs#L515-L537) | `FileSystemWatcher` callbacks invoke `LoadFile` without `try/catch`, symlink checks, or file size limits, crashing the process on transient I/O or syntax errors. | Wrap `LoadFile` in `SafeLoadFile` with `try/catch` and enforce symlink rejection + `MaxFileBytes` matching `ProcedureDefinitionLoader`. |
| **`SEC-MED-20`** | [`SqlConnector.cs:159-168`](file:///root/lis-git/autheris/src/Autheris.Infrastructure/Connectors/SqlConnector.cs#L159-L168) | `if (session.Limit.Value > maxAllowed) maxAllowed = session.Limit.Value;` overwrites `MaxRowLimit` with caller's limit, making `Math.Clamp` a no-op. | Remove `maxAllowed = session.Limit.Value;` so `QueryLimits.MaxRowLimit` is strictly enforced. |
| **`SEC-MED-21`** | [`ODataHandler.cs:51, 72, 97, 153`](file:///root/lis-git/autheris/src/Autheris.Extensions/OData/ODataHandler.cs#L51-L97) | CSDL and table caches key only on `{tenantId}:{userSid}`, omitting policy epoch, role/group claims (when `userSid` is null), and token scope (`Agent.Read`). | Include policy epoch, roles/groups hash, and token scope in `odata:tables:*` and `odata:csdl:*` cache keys. |
| **`SEC-MED-22`** | [`QueryCostAnalyzerRule.cs:260-265, 329-338, 374-383`](file:///root/lis-git/autheris/src/Autheris.GraphQL/Interceptors/QueryCostAnalyzerRule.cs#L329-L338), [`DynamicTableType.cs:72-75`](file:///root/lis-git/autheris/src/Autheris.GraphQL/DynamicTypes/DynamicTableType.cs#L72-L75) | `QueryCostAnalyzerRule` multiplies child/masking cost by `1` instead of `effectiveRows` on paginated wrapper types (`!isList && acceptsPagination`) and under-counts cached fragment spreads; `DynamicTableType` uses unscoped HMAC rules. | Change `if (isList)` to `if (isList || acceptsPagination)`, cache transitive spread counts, and use `CreateTenantScopedHmacRule` in `DynamicTableType`. |
| **`SEC-MED-23`** | [`SubgraphSecurityValidator.cs:13-58`](file:///root/lis-git/autheris/src/Autheris.GraphQL/Federation/SubgraphSecurityValidator.cs#L13-L58), [`DefaultEnvironmentSecretProvider.cs:137-144, 245-254`](file:///root/lis-git/autheris/src/Autheris.Infrastructure/Security/DefaultEnvironmentSecretProvider.cs#L137-L144), [`PurviewDataCatalogClient.cs:29-30, 146`](file:///root/lis-git/autheris/src/Autheris.Extensions/DataCatalog/PurviewDataCatalogClient.cs#L29-L30) | `SubgraphSecurityValidator` lacks nonce replay storage and allows 4-field signature downgrade; `itsm:*` secret resolution falls back to `ITSM_WEBHOOK_SECRET`; `PurviewDataCatalogClient` uses a `static` token field and unvalidated `TenantId` URL interpolation. | Enforce 8-field subgraph signatures with nonce replay tracking; restrict `ITSM_WEBHOOK_SECRET` fallback to `itsm:webhook-secret`; make `_cachedToken` an instance field and validate `TenantId` as GUID/domain. |
| **`SEC-MED-24`** | [`PostgreSqlGovernanceRepository.Consent.cs:1391-1400`](file:///root/lis-git/autheris/src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Consent.cs#L1391-L1400), [`SqlServerGovernanceRepository.Consent.cs:1366-1375`](file:///root/lis-git/autheris/src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.Consent.cs#L1366-L1375) | PostgreSQL and SQL Server omit tenant-scoped `GetConsentRequestByTicketIdAsync(ticketId, tenantId, ct)` and omit `ShouldOverwriteDocumentation` in `UpsertTableMetadataAsync`. | Add `AND TenantId = @TenantId` overload and `ShouldOverwriteDocumentation` check to PostgreSQL and SQL Server repositories. |
| **`SEC-MED-25`** | [`GatewayMcpQueryExecutor.cs:156-207, 513-530, 658-664`](file:///root/lis-git/autheris/src/Autheris.GraphQL/Mcp/GatewayMcpQueryExecutor.cs#L196-L202), [`CatalogGraphQlTypeModule.cs:484`](file:///root/lis-git/autheris/src/Autheris.GraphQL/Catalog/CatalogGraphQlTypeModule.cs#L484) | `BuildPrincipal` adds unfiltered `AdditionalClaims` (allowing role/tenant claim injection); `TryExecuteTableFastPathAsync` hardcodes `Domain = "finance"` and unclamped `limit`; GraphQL resolvers read `HttpContext.User` instead of HotChocolate `ClaimsPrincipal` state. | Filter `AdditionalClaims` against reserved identity/role claim types, use `tableMeta.Domain` and clamp `limit` in fast-path, and prefer `ctx.GetUser()` in GraphQL resolvers. |

---

### 4.4 Low & Informational Findings (`SEC-LOW-01` – `SEC-LOW-09`)

1. **`SEC-LOW-01` – Raw Exception Message Leakage (`GovernancePlanEndpoints.cs:84,122`, `DataContractEndpoints.cs:64`, `DbtEndpoints.cs:371`, `DocIngestionEndpoints.cs:71,160,210`, `GatewayExceptionHandler.cs:76`):**
   - Return sanitized error messages outside Development and mask `GatewayInvalidQueryException` details in `GatewayExceptionHandler` when `!environment.IsDevelopment()`.
2. **`SEC-LOW-02` – CSRF Same-Host Origin Check Omits Scheme (`GatewayApplicationBuilderExtensions.cs:222-227`):**
   - Require `string.Equals(originUri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase)` when validating same-host `Origin`/`Referer`.
3. **`SEC-LOW-03` – `JwtSocketTokenValidator` Skips `ITokenRevocationService` & Optional `IIdentitySubjectResolver` (`JwtSocketTokenValidator.cs:21-25, 95-105`):**
   - Inject `ITokenRevocationService` to check token revocation on WebSocket handshakes and require `IIdentitySubjectResolver` when Entra ID is enabled.
4. **`SEC-LOW-04` – Duplicate `ICompiledSqlQueryPlanCache` Registration (`GatewayExecutionServiceExtensions.cs:102`):**
   - Remove the duplicate `services.AddSingleton<ICompiledSqlQueryPlanCache, CompiledSqlQueryPlanCache>()` registration that overwrites the configured factory in `GatewaySqlEngineServiceExtensions.cs:55-69`.
5. **`SEC-LOW-05` – `SemanticPromptGuardrail.CheckBase64Payloads` Omits Normalization & `ExfiltrationCoercionRegex` (`SemanticPromptGuardrail.cs:220-228`):**
   - Call `NormalizeForInspection(decoded)` and check `ExfiltrationCoercionRegex().IsMatch(normalizedDecoded)` on decoded base64 payloads.
6. **`SEC-LOW-06` – `MarkdownDocIngestor.IngestMarkdownFileAsync` Lacks Base-Directory Containment (`MarkdownDocIngestor.cs:21-42`):**
   - Validate `.md` file extension and verify canonical path containment within the configured documentation root directory.
7. **`SEC-LOW-07` – Non-English String Literals & Comments Violating `AGENTS.md` Rule 1 (`CasbinEnforcementService.cs:1102`, `OpenApiIngestionService.cs:352`):**
   - Translate the German exception message in `CasbinEnforcementService.cs:1102` and comment in `OpenApiIngestionService.cs:352` to English.
8. **`SEC-LOW-08` – `ColumnMaskingProvider()` Default Constructor Dev Salt Fallback & `DuckDbOlapEngine` Identifier Collision (`ColumnMaskingProvider.cs:20-41`, `DuckDbOlapEngine.cs:356-360, 480-496`):**
   - Require `environment?.IsDevelopment() == true` before falling back to `"dev-only-hmac-salt-secure-fallback"`, inject `IColumnMaskingProvider` in `DefaultVirtualFilterKeyProvider`, expand `DisallowedTableFunctionsRegex` (`iceberg_scan`, `delta_scan`, `sqlite_scan`, `postgres_scan`, `mysql_scan`, `pragma_`, `current_setting`, `getenv`), and detect sanitized column name collisions in `StageTableAsync`.
9. **`SEC-LOW-09` – Downstream `SqlConnectionFactory` Skips `ConnectionTlsPolicy.Validate`, `AuditWormExportService` Region/Manifest Signing, & `RedisTokenRevocationService` Fail-Open (`SqlConnectionFactory.cs:18-43`, `AuditWormExportService.cs:316-326`, `RedisTokenRevocationService.cs:171-176`):**
   - Invoke `ConnectionTlsPolicy.Validate` in `SqlConnectionFactory`, use configured S3 region + HMAC-signed manifest in `AuditWormExportService`, and add a configurable `FailClosedOnRedisOutage` option to `RedisTokenRevocationService`.

---

## 5. Verified Security Architecture Strengths

The security review confirmed that the following core defense-in-depth mechanisms are soundly implemented and verified:

1. **Fail-Closed Startup Validation (`GatewayStartupValidator.cs`, `LegacySwitchGuard.cs`, `ForwardAuthSecretStartupValidator.cs`):**
   - Blocks `danger_*` flags, `EnableTestAuthHandler`, `SeedDemoData`, wildcard CORS (`*`), non-HTTPS `TrustedOrigins`, weak PBKDF2 iterations (< 210,000), Argon2id hashes in Production, unencrypted DB connection strings, fallback HMAC audit keys, and accidental `ASPNETCORE_ENVIRONMENT=Development` inside containers (`DOTNET_RUNNING_IN_CONTAINER=true`).
2. **Connect-Time SSRF & DNS Rebinding Protection (`SecureOutboundHttp.cs`, `EgressAddressRules.cs`, `SsrfProtectionHandler.cs`):**
   - Pins DNS-resolved IP addresses inside `SocketsHttpHandler.ConnectCallback` with `AllowAutoRedirect = false` and `UseProxy = false`, blocking IPv4/IPv6 loopback, RFC 1918 private networks, link-local, cloud metadata endpoints (`169.254.169.254`, `100.100.100.200`), CGNAT (`100.64.0.0/10`), NAT64 (`64:ff9b::/96`), and 6to4 (`2002::/16`).
3. **DuckDB Native Engine Lockdown (`DuckDbOlapEngine.cs`):**
   - Executes `SET enable_external_access = false; SET lock_configuration = true;` immediately upon opening every DuckDB connection and binds all staged values via parameterized `DuckDBCommand` (`$1, $2, ...`).
4. **AST SQL Compiler & Token Stream Guardrails (`FastSqlEngine.cs`, `AstSecurityVisitor.cs`, `TreeSqlCompiler.cs`):**
   - Enforces statement whitelisting (`SELECT`, `INSERT`, `UPDATE`, `DELETE`), token/depth budgets, comment & stacked-query rejection, system catalog blocking (`information_schema`, `pg_catalog`, `sys`), dangerous function blocking (`pg_read_file`, `xp_cmdshell`, `dbms_pipe`), and `SETOP` column count consistency.
5. **Casbin ABAC & Zanzibar ReBAC Engines (`CasbinEnforcementService.cs`, `ZanzibarRebacEvaluator.cs`):**
   - Lock-free atomic `PolicySnapshot` swapping, deny-overrides semantics, fail-closed `sub_rule` exception handling, stateful SQL single-quote parser with strict claim whitelisting (`^[a-zA-Z0-9_\-\.@:\s]+$`), and cycle-safe bounded ReBAC graph traversal.
6. **Governance Ratchet & Anti-Downgrade Protection (`CatalogGovernanceRatchet.cs`, `OpenApiIngestionService.cs`):**
   - Prevents external catalog syncs or OpenAPI re-ingestions from downgrading `RequiresFourEyes`, `Sensitivity`, `IsActive`, column `IsSensitive`, or masking strategies, and blocks phantom column injection on governed tables.
7. **Plugin Supply-Chain Verification & Lakehouse Path Guards (`PluginManager.cs`, `PluginTrustList.cs`, `LakehouseLocationGuard.cs`):**
   - Enforces SHA-256 binary allowlisting and Authenticode X.509 verification before assembly load, and resolves symlinks (`File.ResolveLinkTarget`) to prevent Lakehouse path traversal.

---

## 6. Prioritized Remediation Roadmap & TDD Verification Plan

### Wave 1: Critical & High Access Control, Auth, SSRF, SQLi & Audit Chain (`P0` Immediate)
- [ ] **Track 1A (API & Auth Hardening):** Fix `SEC-CRIT-01` (`GovernanceClassificationEndpoints`), `SEC-CRIT-02` (`DevPortalEndpoints`), `SEC-HIGH-01` (2FA secret overwrite), `SEC-HIGH-02` (`DocIngestionEndpoints`), `SEC-HIGH-03` (`DataContractEndpoints`), and `SEC-HIGH-04` (`RateLimitingMiddleware`).
- [ ] **Track 1B (Governance, TOTP & SSRF Hardening):** Fix `SEC-CRIT-03` & `SEC-HIGH-05` (`AccessPlanningService`), `SEC-CRIT-04` (`CatalogDiscoveryService` SSRF), `SEC-HIGH-06` (`ClassificationWorkflowStateMachine`), `SEC-HIGH-07` (`DeclarativeConsentApplyService`), `SEC-HIGH-08` (`HitLStepUpApprovalService`), `SEC-HIGH-11` (`WebhookDispatcherService` & `TrafficShadowingService`), and `SEC-HIGH-12` (`ClusterTotpSecretStore` & `TotpVerificationService`).
- [ ] **Track 1C (SQL, Federated Execution & WORM Audit Chain):** Fix `SEC-CRIT-05` (`GovernedDataQueryService` / `DataApiFilterTranslator`), `SEC-CRIT-06` & `SEC-HIGH-17` & `SEC-HIGH-18` (PostgreSQL/SQL Server/SQLite audit chain & fault flag), `SEC-HIGH-13` (`CrossSourceQueryRouter` / `CrossSourcePlanner` / `FederatedStagingService`), `SEC-HIGH-14` (`GovernedProcedureExecutionService`), `SEC-HIGH-15` (`AstSecurityVisitor` / `SqlQueryAnalyzer`), and `SEC-HIGH-16` (`DefaultVirtualFilterKeyProvider`).
- [ ] **Track 1D (MCP, Lakehouse & GraphQL High Findings):** Fix `SEC-HIGH-09` & `SEC-HIGH-10` (`AiDataGuardrailService` / `McpDocTools` / `McpDatasetTools`), `SEC-HIGH-19` (`GraphQlTreeBuilder` overflow & recursion), and `SEC-HIGH-20` (`DeltaLakeDataSourceExecutor` / `LakehouseDataSourceExecutor` / GraphQL `ClusterAdminPolicy`).

### Wave 2: Medium & Low Defense-in-Depth Hardening (`P1`)
- [ ] **Track 2A:** Remediate `SEC-MED-01` through `SEC-MED-14` (Portal XSS, Traffic Shadowing header stripping, `AccessAuditMiddleware` exception mapping, EventGrid guard, `DatasourceTestingService`, `EgressUrlPolicy`, `FileSystemUniversalDocStore` Zip-bomb limits).
- [ ] **Track 2B:** Remediate `SEC-MED-15` through `SEC-MED-25` and `SEC-LOW-01` through `SEC-LOW-09` (`SqlFilterCompiler` & DML tautologies, `PushdownUrlFormatter`, `GovernedSqlExecutionService` quote scanner, `AsyncQueryJobBackgroundWorker` row/CSV hardening, `SqlConnector` limit clamp, OData cache key binding, `QueryCostAnalyzerRule`, `DynamicTableType`, `SubgraphSecurityValidator`, `GatewayMcpQueryExecutor`, and English string compliance).
