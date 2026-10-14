# Remediation verification and deployment review

Reviewed ref: `88f7df1c` (main, merge of PR #72). It contains all three remediation commits (3014f659, 6ad16519, f7309860).
Important: while this review ran, the working tree was switched to `feat/ast-x1-cutover` (4ca84c5a), which does NOT contain 6ad16519 / f7309860. All code evidence below was re-verified with `git show 88f7df1c:<path>`; line numbers refer to 88f7df1c. Deployment files are identical between both refs except `.github/CODEOWNERS`, `.github/workflows/ci.yml`, `deploy/containers/gqlgateway-api/appsettings.Benchmark.json`.
No repo files were modified (only untracked `obj/` restore output from `dotnet list`).

Legend: Fixed = exploit path closed on all listed variants. Partial = at least one listed variant/part still open. Open = no effective change. N/R = not reproducible (code removed/superseded).
Test column: "none" = no test found that would fail if the fix were reverted.

Note on numbering: commit messages of f7309860 re-use plan IDs inconsistently (their "SEC-MED-08" = plan MED-10 datasource probe and plan MED-08 break-glass; "SEC-MED-09" = plan MED-11; "SEC-MED-12" = plan MED-14; "SEC-MED-16" = plan MED-18; "SEC-LOW-04" = plan LOW-05). IDs below follow the plan document.

Commit 6ad16519 (the bulk fix for CRIT/HIGH) added NO new tests; it only edited `Wave2GovernanceTests.cs` and `WebhookDispatcherServiceTests.cs`. Almost every CRIT/HIGH row therefore has no regression test.

## Remediation verification

Totals (69 items = 60 findings + B-10..B-18): Fixed 15, Partial 28, Open 25, N/R 1.

| ID | Status | Evidence (file:line @88f7df1c) | Remaining gap | Regression test? |
|---|---|---|---|---|
| SEC-CRIT-01 | Fixed | `GovernanceClassificationEndpoints.cs:68,78,90,240` GovernanceAdmin on classify/transition/pending/assign-owner; `:103-163,166-224` approve/reject derive role+SID from claims, owner check `:138,201` | Minor: a `DataOwner`-role user passes when `proposal.DataOwnerSid` is empty (`:138-144`) and can approve any ownerless proposal; GET `proposals/{id}` (`:100`) has no tenant/owner scope | none (no test for assign-owner/approve) |
| SEC-CRIT-02 | Fixed | `DevPortalEndpoints.cs:136,141,163,168,198` HitL enabled + `IsApprover` + `EnsureTableApproverAsync`; `WithAuditExemption` removed from POSTs (`:128,154,211`); HtmlEncode `:507,596-603` | none | none |
| SEC-CRIT-03 | Partial | `AccessPlanningService.cs:185` null TOTP service now throws; `:196` `VerifyAndConsumeTotpAsync`; `:180` same-admin binding | Four-Eyes separation not implemented (code requires confirmer == creator, `:180`, the opposite of the finding); `_totpService` is still an optional ctor dependency | `AdminMcpTwoPhaseTests` covers happy path only; no replay / null-service test |
| SEC-CRIT-04 | Fixed | `CatalogDiscoveryService.cs:315` `ValidateResolvedAsync`, `:317-323` `SecureOutboundHttp.CreatePrimaryHandler` (no redirects, connect-time IP check) | Spec body size is not capped (`GetStringAsync`) | `CatalogDiscoveryServiceTests` exist but no SSRF/redirect test |
| SEC-CRIT-05 | Fixed | `GovernedDataQueryService.cs:122-176` unknown columns rejected, `:219` `TranslateFilterToTrino`, `:228` `BuildOrderByClause`; RLS no longer spliced as text, applied by AST rewriter as `(user) AND (rls)` (`AstSecurityVisitor.Dml.cs:71`) | Translator is still regex based; safety relies on the unknown-identifier rejection plus the AST visitor | `RestDatasetPolicySpliceTests`, `GovernedDataQueryServiceTests` (from WP-X0n, not from the remediation) |
| SEC-CRIT-06 | Fixed | `PostgreSqlGovernanceRepository.Audit.cs:529-545`, `SqlServerGovernanceRepository.Audit.cs:564-580` anchor write gated on `_auditChainViolation == null` and `TryLoadVerifiedAnchor` | none | `AuditAnchorHardeningTests` cover SQLite only; none for PG/SQL Server |
| SEC-HIGH-01 | Fixed | `GovernanceApiEndpoints.cs:43,85,125` 409 if secret exists; `:149-155` client `SecretBase32` honoured only when `clusterState == null` | When no cluster state is configured a user may still pick their own secret (self-enrolment only, no overwrite); check-then-set is not atomic | none |
| SEC-HIGH-02 | Fixed | `DocIngestionEndpoints.cs:82,129,223` GovernanceAdmin; 50 MB body cap | none | partial: `SyncExistingDocuments_RequiresGovernanceAdmin` only; bundle/put not tested |
| SEC-HIGH-03 | Fixed | `DataContractEndpoints.cs:67,78,98` GovernanceAdmin | none | none |
| SEC-HIGH-04 | Partial | `RateLimitingMiddleware.cs:145-165` bearer-exempted requests that end unauthenticated are IP limited post-auth | Bearer requests to `/metrics` skip pre-auth (`:49`, exempt) and post-auth (`:136`) limiter entirely, so invalid bearer tokens against `/metrics` are unlimited; auth cost is incurred before throttling | none (`Wave1GatewayHardeningTests` predates) |
| SEC-HIGH-05 | Partial | `AccessPlanningService.cs:232-238` compares against stored confirmed token (binds plan+admin); token HMAC key random per process (`:34`) | `tenantId` now taken from `tableId.Domain` (`:256`), falling back to `"default"`: a domain is not the caller's tenant, tuples/audit/reload can hit the wrong tenant; `channel="mcp"` (`:293`) and fake `wormSignature = Guid` (`:298`) remain; `ApplyAccessAsync` does not check caller == plan owner | none |
| SEC-HIGH-06 | Partial | `ClassificationWorkflowStateMachine.cs:141-146` max rank now computed over `proposal.Columns`; `:167-171` consume-TOTP | (a) caller-supplied `ColumnDecisions` can LOWER a column rank (`:137,143`) and skip step-up/four-eyes; (b) step-up silently skipped when `_totpService`/store null (`:167`) and any non-empty token passes; (c) `DATA_GOVERNANCE_REVIEWER` still seals in one step when four-eyes is required (`:249`), no stage-1 status check | none for these paths |
| SEC-HIGH-07 | Partial | `DeclarativeConsentApplyService.cs:35-38` privilege check, `:77` unknown effect throws | Role strings `GOVERNANCE_ADMIN`/`CLUSTER_ADMIN` do not match canonical `GovernanceAdmin`/`ClusterAdmin` (`ClusterAdminPolicy.cs:37`): real admins only pass via `scope` claim substring (`Contains("governance:admin")`, `:38`, matches e.g. `xgovernance:administrator`) or the non-canonical `DATA_GOVERNANCE_REVIEWER` role. Functional break + weak check | `Wave2GovernanceTests` (adapted, not a dedicated test) |
| SEC-HIGH-08 | Partial | `HitLStepUpApprovalService.cs:615,709` remote tickets signature-verified | String overloads still hard-code `IsCrossTenantAdmin: true` (`:331,471`; no production callers, latent); lock failure fails open unless `FailClosedOnClusterPartition` (`:553`, default `false`, `GatewayOptions.cs:1524`) | none |
| SEC-HIGH-09 | Partial | `McpDocTools.cs:392-404` deposit requires Cluster/GovernanceAdmin and not read-only; doc tools audited and PII-scrubbed (`AiDataGuardrailService.cs:272-307`) | `TenantAdmin` still passes the admin gate (`AiDataGuardrailService.cs:236`) so tenant admins reach cluster-wide admin tools; doc tools still run before Casbin ABAC (`:272` vs `:352`); deposit check skipped if `sessionContext == null` (`McpDocTools.cs:392`) | `DocSearchAndMcpToolsTests` (pre-existing); none for the new gate |
| SEC-HIGH-10 | Partial | `McpDatasetTools.cs:63` `invoke_api` ReadsRows; `:65-129` `ExtractTableFromSql` | Table is taken from the FIRST `FROM` token by regex: `SELECT ... FROM public_t JOIN restricted_t`, `FROM a, b`, subqueries, CTEs bypass the Four-Eyes/step-up check for the second table | none |
| SEC-HIGH-11 | Partial | Webhooks `WebhookDispatcherService.cs:103` + secure handler (`GatewayEventServiceExtensions.cs:18`); shadowing `TrafficShadowingService.cs:157`, secure handler (`GatewayExecutionServiceExtensions.cs:145`) | Shadowing still builds `new Uri(targetBaseUri, path.TrimStart('/'))` (`:187`); a request path `/http://evil.example/api/sql` yields an absolute URL on another public host (verified with a .NET 10 probe: `new Uri(base,"http://evil.com/api/sql")` -> `http://evil.com/api/sql`). Private ranges are blocked by the handler, public-host redirection of replayed bodies is not | `WebhookDispatcherServiceTests` (adapted), `TrafficShadowingGuardTests` |
| SEC-HIGH-12 | Open | `TotpVerificationService.cs:100,124`, `ClusterTotpSecretStore.cs:38` unchanged | Seeds stored plaintext in Redis; get-then-set replay marker non-atomic (both local dictionary and cluster); no per-user lockout | none |
| SEC-HIGH-13 | Partial | `CrossSourcePlanner.cs:124,234,252,346` per-table decision required, `GetEffectiveColumnAccess`, no `s0` fallback (landed via X0 work) | `CrossSourceQueryRouter.cs` never calls `ISqlDataSourceResolver.ResolveAllowedDataSource` (only `IsActive` check); `FederatedStagingService.cs:275` still uses `GetColumnAccess` for mask typing | `CrossSourcePlannerTests` (pre-existing) |
| SEC-HIGH-14 | Partial | `GovernedProcedureExecutionService.cs:625` `cleared` no longer overrides `Deny` | `cleared` still overrides `Mask` -> `Clear` (by design, locked in by new tests `Execute_DeclaredProcedure_WhenColumnInClearedResultColumns_ReturnsClearTextEvenIfSourceMasked`); a procedure author can unmask catalog-masked columns without consent | yes (3014f659) for the retained behaviour; none for Deny |
| SEC-HIGH-15 | Partial | `AstSecurityVisitor.cs:166-228`, `.MaskGuards.cs:22-140` WHERE, HAVING, ORDER BY, JOIN ON/USING, DML checked (name-based) | `GROUP BY`, window `PARTITION BY/ORDER BY`, and masked columns used inside SELECT-list expressions (`CASE WHEN ssn='x'`, aggregates) are not covered by `CheckNoMaskedColumnReferences`; derived tables (`SubqueryTableSource`) are not collected by `CollectTableSources` (inner query is checked only through recursion) | `AdvancedMaskingRuleTests` pre-existing, no GROUP BY / CASE test |
| SEC-HIGH-16 | Fixed | Superseded by WI-5/WI-19: `SqlVirtualFilterKeySetProvider.cs:95-152,182-236` (joins, tenant-scoped, cached per tenant) | Original file deleted (bddc0f7a), not by the remediation commits | none located |
| SEC-HIGH-17 | Open | `PostgreSqlGovernanceRepository.Audit.cs:132-135`, `SqlServer...Audit.cs:126-129`, `Sqlite...Audit.cs:151-154` still `_isAuditPipelineFaulted = false` unconditionally | Enqueue gate (`:44`) uses the field, so a chain violation no longer blocks Tier-B writes after the next flush. Only the health check (`IsAuditPipelineFaulted` property, `...Repository.cs:24`) still reports the violation | none |
| SEC-HIGH-18 | Open | `SqliteGovernanceRepository.Audit.cs:707` hard-coded legacy key still accepted for `SequenceNumber IS NULL`; `:719,750` `anchor.Sequence == position` | Only a comment was added in the "fix" | none |
| SEC-HIGH-19 | Partial | `GraphQlTreeBuilder.cs:54` `checked` + overflow catch; `:82` depth limit 10 for the selection tree | `ParseObjectFilter` (`:216-292`) still recurses without depth limit (and/or/not) | none |
| SEC-HIGH-20 | Partial | `DeltaLakeDataSourceExecutor.cs:130,216` filter columns loaded, then dropped from projection; `QueryTypes.cs:369`, `CdcSubscriptionGovernor.cs` canonical ClusterAdmin | `LakehouseDataSourceExecutor.cs:66,80` (Iceberg) still scans only `RequestedFields` then applies `FilterRows`, same `!=`-against-null pattern; `GovernanceAdmin` still counts as global admin (acceptable only because the claims normalizer treats it as a platform role) | `DeltaLakeSecurityAndMetadataTests` (pre-existing); none for Iceberg |
| SEC-MED-01 | Fixed | `DevPortalEndpoints.cs:507-508,596-603` HtmlEncode, `data-ticket` + form instead of inline onclick | none | `DevPortalTwoFactorConsoleTests` (partial) |
| SEC-MED-02 | Partial | `TrafficShadowingService.cs:187-199` strips Authorization/Cookie/Api-Key/token/secret headers at replay | `TrafficShadowingMiddleware.cs:57-61` still `ReadToEndAsync` with no cap when `Content-Length` is absent (chunked JSON); all headers still copied into queued `ShadowRequest` (`:71`) | none |
| SEC-MED-03 | Open | `AccessAuditMiddleware.cs:69-74` records any exception as `ERROR` (status 200 at that point) | `GatewaySecurityException` etc. not mapped to DENY; `ex.Message` written to audit | none |
| SEC-MED-04 | Open | `WebhookEndpoints.cs:198-200,300` unchanged | EventGrid validation body still unbounded (10 MB); no topic allowlist | none |
| SEC-MED-05 | Open | `DbtEndpoints.cs:377-385` metrics unfiltered; `GovernanceEndpoints.cs:484` raw `FindFirst("tenant_id") ?? "default"` | unchanged | none |
| SEC-MED-06 | Open | `SqlEndpointRoutes.cs:418` `ContentLength > 0` | chunked bodies silently dropped | none |
| SEC-MED-07 | Partial | `GatewayMcpServer.cs:155-168` dataset resources go through `CallGuardedToolAsync` | `autheris://catalog/*`, `governance/*`, `api/*` (`IsNativeResource`, `:146`) still bypass guardrails | none |
| SEC-MED-08 | Partial | `JustificationAndBreakGlassInterceptor.cs:166-167` flag set after audit write; audit failure denies | `_governanceRepo == null` (`:119`) still allows break-glass with no audit record | `BreakGlass_AuditRecordingFails_FailsClosedAndDoesNotSetIsBreakGlass` (f7309860) |
| SEC-MED-09 | Open | `AccessPlanningService.cs:48-50` unbounded `_plans/_consumedTokens/_inMemoryVault`; `:457` hard-coded `sales.public.orders` | unchanged | none |
| SEC-MED-10 | Partial | `DatasourceTestingService.cs:23,278` forbidden headers + CRLF skipped | Authority override still possible: guard checks only `http://`, `https://`, `//`, `..` (`:153-155`); `http:\\evil.com` passes and `Uri.TryCreate(baseUri, "http:\\\\evil.com")` yields `http://evil.com/` (verified). Datasource secret header may be sent to attacker host (public hosts pass the SSRF check). No `probeUri.Host == baseUri.Host` check | `TestDatasource_IgnoresForbiddenHeadersAndCrlfInjection` (headers only) |
| SEC-MED-11 | Partial | `EgressUrlPolicy.cs:20,58` `IdnHost` | `ValidateResolvedAsync` still returns before DNS checks when `isDev` (`:53`), so a hostname resolving to metadata IP passes in Development | `EgressUrlPolicy_ValidateStatic_RejectsIpv4MappedIpv6Restricted` |
| SEC-MED-12 | Open | `SubgraphContextPropagationService.cs:97` `_environment == null \|\| IsDevelopment()` | unchanged (DI normally supplies the environment, so exposure is low) | none |
| SEC-MED-13 | Open | `McpToolExecutionHandler.cs:221` lineage without visibility check; `:273` principal drops claims | unchanged | none |
| SEC-MED-14 | Partial | `FileSystemUniversalDocStore.cs:225-249` entry count (1000) and declared uncompressed bytes (100 MB) | No extension allowlist; limit uses declared `entry.Length` (attacker controlled); `PolicySimulationService.cs:127` still `IPAddress.Loopback` | `ExtractBundleAsync_DefendsAgainstZipBomb` |
| SEC-MED-15 | Partial | `GovernedSqlCompiler.cs:188-194` simplifies before security (new path) | Legacy `FastSqlEngine.cs:713-716` still security-then-simplify; `RlsListener.cs:675` `1 <= 2` still non-tautological; `SqlFilterCompiler.ExpressionEquals` (`:334`) unchanged | none |
| SEC-MED-16 | N/R | `PushdownUrlFormatter` no longer exists; replaced by the federation pushdown code (`VirtualFilterPushdownSelector.cs`) | New code not reviewed in depth | n/a |
| SEC-MED-17 | Fixed | `GovernedSqlExecutionService.cs:369-380` single `quote` state; reserved prefix rejected before rewrite | none | `NormalizeClientParameters` tests (pre-existing) |
| SEC-MED-18 | Partial | CSV neutralisation `AsyncQueryJobBackgroundWorker.cs:202`; path containment `AsyncQueryJobManager.cs:172-174` | `MaxRows` still unclamped (`:90` `Math.Max(1, ...)`) | `EscapeCsv_NeutralizesFormulaInjection` |
| SEC-MED-19 | Open | `SqlEndpointLoader.cs:104,525` | no try/catch, symlink or size guard on watcher path | none |
| SEC-MED-20 | Open | `SqlConnector.cs:164` `maxAllowed = session.Limit.Value` | row cap still overwritten by caller limit | none |
| SEC-MED-21 | Open | `ODataHandler.cs:51,72,97,174` cache keys `{tenant}:{userSid}` only (caches from perf commit 4da8e788) | No epoch/roles/scope in key; null `userSid` collapses all service principals of a tenant to one key; 60 s stale authorization | none |
| SEC-MED-22 | Open | `QueryCostAnalyzerRule.cs:329,350` `if (isList)` | unchanged | none |
| SEC-MED-23 | Partial | `SubgraphSecurityValidator.cs:14,34` nonce replay cache + 4-field downgrade rejected | `ITSM_WEBHOOK_SECRET` fallback (`DefaultEnvironmentSecretProvider.cs:137-143`) and static `_cachedToken` / unvalidated `TenantId` in Purview client (`:29,146`) remain | two tests in `SubgraphFederationSecurityTests` (f7309860) |
| SEC-MED-24 | Open | `PostgreSql...Consent.cs:1391`, `SqlServer...Consent.cs:1366` no tenant-scoped overload / `ShouldOverwriteDocumentation` | SQLite has both, PG/SQL Server do not | none |
| SEC-MED-25 | Open | `GatewayMcpQueryExecutor.cs:537` `domain = "finance"`; `:682-684` unfiltered `AdditionalClaims` | unchanged | none |
| SEC-LOW-01 | Open | `GovernancePlanEndpoints.cs:80,84,118,122`; `GatewayExceptionHandler.cs:108` raw messages | unchanged | none |
| SEC-LOW-02 | Open | `GatewayApplicationBuilderExtensions.cs:224` authority compare without scheme | unchanged | none |
| SEC-LOW-03 | Open | `JwtSocketTokenValidator.cs` no revocation check | unchanged | none |
| SEC-LOW-04 | Open | `GatewayExecutionServiceExtensions.cs:103` duplicate registration still present vs `GatewaySqlEngineServiceExtensions.cs:61` | unchanged | none |
| SEC-LOW-05 | Fixed | `SemanticPromptGuardrail.cs:226` exfil regex on decoded base64 | none | `Base64Obfuscated_ExfiltrationCoercion_IsBlocked` |
| SEC-LOW-06 | Open | `MarkdownDocIngestor.cs:33` only `..` check, no root containment / extension check | unchanged | none |
| SEC-LOW-07 | Partial | Casbin German text gone | `OpenApiIngestionService.cs:352` German comment; many other German strings (e.g. `GatewayStartupValidator.cs:130,302,323,410`) | n/a |
| SEC-LOW-08 | Partial | `DuckDbOlapEngine.cs:247,485-507` collision check + extra function denylist | `ColumnMaskingProvider.cs:30-37`: with `environment == null` the dev HMAC salt fallback still applies | `SEC_LOW_08_*` (2 tests) |
| SEC-LOW-09 | Partial | TLS policy enforced by startup validator (`GatewayStartupValidator.cs:408`) | `RedisTokenRevocationService.cs:173` still fails open (logs and returns false); WORM export region/manifest signing not changed | none |
| B-10 | Partial | `GovernancePlanEndpoints` + `CatalogApiEndpoints` GovernanceAdmin; TOTP/token fixes (see CRIT-03/HIGH-05) | Four-Eyes creator/approver separation absent | `B10_GovernancePlanAndPrincipalEndpoints_RequireGovernanceAdminPolicy` |
| B-11 | Open | `AccessPlanningService.cs:50,332` volatile `_inMemoryVault`; `DefaultEnvironmentSecretProvider` in-memory | secrets lost on restart | none |
| B-12 | Open | `CatalogDiscoveryService.cs:361` ephemeral `ds-{guid}` never persisted | `DatasourceTestingService` cannot resolve it | none |
| B-13 | Fixed | see SEC-CRIT-04 | none | none |
| B-14 | Open | no Basic or OAuth2 `client_credentials` handling in `CatalogDiscoveryService`/ingestion | unchanged | none |
| B-15 | Fixed | `CatalogApiEndpoints.cs` `PUT /api/v1/catalog/datasets/{id}/state` with GovernanceAdmin | Falls back to adminSid `"system-admin"` when no SID claim | `SetDatasetStateHandler_*` (2 tests) |
| B-16 | Partial | `McpProtocolHandler.cs:167-176`, `GatewayMcpServer.cs:70-80` filter admin tools | List and execution agree, but both admit `TenantAdmin` (see HIGH-09); `Admin` role is listed as admin but denied at execution | `..._B16` (2 tests) |
| B-17 | Open | `AccessPlanningService.cs:256,293,298` | tenant derived from domain, channel hard-coded, fake WORM signature | none |
| B-18 | Fixed | `with` expressions preserve table properties | none | `SetDatasetStateAsync_PreservesAllTableProperties_B18Fix` |

## Deployment & supply-chain findings

### High — Transitive `System.Security.Cryptography.Pkcs` 9.0.18 with known CVEs on main
- **File:** `src/Autheris.Api/packages.lock.json:1092-1095` (and the lock files of Infrastructure, tests, tools)
- **CWE:** CWE-1395 (dependency on vulnerable third-party component)
- **Confidence:** Likely (based on the repo's own commit 4ca84c5a, which cites CVE-2026-84008 High and CVE-2026-70162 Medium and bumps to 10.0.13; `dotnet list package --vulnerable` did not finish within the time limit, see note at the end)
- **Issue:** At `88f7df1c` the shipped API resolves Pkcs 9.0.18 transitively. The fix exists only on `feat/ast-x1-cutover` (4ca84c5a). The CI "Vulnerable Package Audit" step (`ci.yml`, `docker-publish.yml`) would block main builds and image publishing, so main cannot ship a signed image until merged.
- **Exploit scenario:** Parsing of attacker-supplied CMS/PKCS#7 data (plugin Authenticode / signature verification paths) reaches the vulnerable component.
- **Remediation:** Merge 4ca84c5a into main, keep the explicit `PackageReference` floor, re-run the audit gate.

### Medium — Hard-coded HMAC sealing key for WORM configuration audit
- **File:** `src/Autheris.Application/Governance/Services/WormConfigurationAuditService.cs:30` (used at `:101`)
- **CWE:** CWE-798 / CWE-321
- **Confidence:** Confirmed
- **Issue:** The configuration-audit record signature uses the constant `Autheris_Worm_Config_Sealing_Key_2026_Strict` in all environments. Same pattern as the legacy SQLite audit key (SEC-HIGH-18, `SqliteGovernanceRepository.Audit.cs:707`). `.gitleaks.toml` does not cover it.
- **Exploit scenario:** Anyone with DB write access (or the public source) recomputes valid seals and forges or rewrites classification-config audit records, defeating the tamper evidence.
- **Remediation:** Derive the key from the audit key vault secret (HKDF as done for the audit chain, `PostgreSqlGovernanceRepository.cs:167`); fail closed in non-Development when absent.

### Medium — Helm chart has no NetworkPolicy
- **File:** `deploy/helm/autheris/templates/` (no `networkpolicy.yaml`; `grep NetworkPolicy` empty)
- **CWE:** CWE-923
- **Confidence:** Confirmed
- **Issue:** The gateway pod can reach any pod, node, metadata endpoint and the Kubernetes API; Redis/Postgres secrets are injected but nothing restricts ingress to the service port or egress to the DB/Redis/IdP.
- **Exploit scenario:** SSRF residue (e.g. SEC-HIGH-11 shadowing authority override, SEC-MED-10 probe) or a compromised plugin can pivot to in-cluster services and cloud metadata; any other pod can call the gateway directly, bypassing the Ingress/ForwardAuth trust assumptions.
- **Remediation:** Add default-deny ingress+egress NetworkPolicies with explicit allow rules (ingress controller to port, egress to Postgres, Redis, DNS, IdP); block 169.254.169.254.

### Medium — Helm image defaults to mutable `latest` tag, `IfNotPresent`, no digest
- **File:** `deploy/helm/autheris/values.yaml:5-7`, `templates/deployment.yaml:52-53`
- **CWE:** CWE-829 / CWE-494
- **Confidence:** Confirmed
- **Issue:** `image.tag: "latest"` (the `.Chart.AppVersion` fallback never triggers) with `pullPolicy: IfNotPresent`: nodes may run different image versions; tag can be re-pointed. The Kyverno policy (`deploy/policy/kubernetes/kyverno-verify-images.yaml`) is not part of the chart and only matches `ghcr.io/themulle/autheris*`.
- **Exploit scenario:** A tag overwrite or a stale node cache runs an unreviewed/unpatched image; if Kyverno is not installed nothing verifies the signature.
- **Remediation:** Require `image.digest` (render `repo@sha256:...`), `pullPolicy: Always` or digest pin, ship/document the Kyverno policy as a chart prerequisite and make `values.production.yaml` set the digest.

### Low — Pods auto-mount the ServiceAccount token
- **File:** `deploy/helm/autheris/templates/deployment.yaml` (no `automountServiceAccountToken: false`), `values.yaml:14-17`
- **CWE:** CWE-250
- **Confidence:** Confirmed
- **Issue:** The gateway needs no Kubernetes API access but receives a token.
- **Exploit scenario:** Any RCE/SSRF-to-file read yields a cluster credential.
- **Remediation:** Set `automountServiceAccountToken: false` on pod spec and ServiceAccount.

### Low — Helm chart does not wire required production configuration; metrics scrape is unauthenticated
- **File:** `deploy/helm/autheris/values.yaml:102-109`, `templates/configmap.yaml`, `templates/servicemonitor.yaml:10-14`
- **CWE:** CWE-1188 / CWE-16
- **Confidence:** Confirmed
- **Issue:** `gateway.security.hmacSecretExistingSecret/hmacSecretKey` and `trustedNetworks` are defined but never rendered; no IdP/audience/trusted-origin/ForwardAuth secret settings exist. Production startup validators (HMAC secret, trusted proxy, origins) will abort, pushing operators to ad-hoc env overrides. `/metrics` requires authorization (`GatewayApplicationBuilderExtensions.cs:354`) but the ServiceMonitor/annotations (port 5000) carry no credentials, so scraping fails or gets weakened. `readOnlyRootFilesystem: true` with only `/tmp` mounted while the app writes `/app/scratch` and `/app/shared-docs` (async jobs, doc deposit).
- **Exploit scenario:** Operators loosen auth for `/metrics` or disable validators to get the chart running.
- **Remediation:** Render the declared secrets/trusted networks, add auth settings to the ServiceMonitor (bearer secret), mount emptyDir/PVC for scratch and shared-docs.

### Low — Test-auth handler is compiled in by default; only CI publish paths exclude it
- **File:** `Directory.Build.props:17`, `deploy/containers/gqlgateway-api/Containerfile:31`, `benchmarks/load/docker/Dockerfile.gql:29`
- **CWE:** CWE-1188
- **Confidence:** Confirmed
- **Issue:** `AutherisIncludeTestAuth` defaults to `true`; `docker-publish.yml:59` and `release.yml:69` pass `false` and verify the binary, but the benchmark/deploy Containerfiles publish without it, so `TestAuthHandler` ships in those images. Runtime guards (`GatewayStartupValidator.cs:345`, `GatewayServiceCollectionExtensions.cs:129`) block it outside Development.
- **Exploit scenario:** Only exploitable if a runtime guard regresses or the image is run with `ASPNETCORE_ENVIRONMENT=Development` (benchmark compose defaults governance-seed to Development).
- **Remediation:** Invert the default (`false` unless set by test projects) and pass the flag in all publish commands.

### Low — Benchmark/deploy stack hardening gaps (documented as benchmark-only)
- **File:** `deploy/containers/reverse-proxy/Containerfile:1-16`, `deploy/containers/reverse-proxy/nginx.conf` (default role `user_finance`), `deploy/podman-compose.yaml` (crm connection `TrustServerCertificate=True`; one `GATEWAY_HMAC_SECRET` reused for masking, webhook and benchmark secrets; governance-seed `ASPNETCORE_ENVIRONMENT` default `Development`), `deploy/containers/gqlgateway-api/appsettings.Benchmark.json:162-194` (static `bench-*` tokens), `deploy/scripts/seed_openmetadata.py:148`
- **CWE:** CWE-250 / CWE-295 / CWE-798 / CWE-321
- **Confidence:** Confirmed
- **Issue:** nginx image has no `USER` (master runs as root), wget/curl added; unauthenticated callers are mapped to a finance identity by nginx; certificate validation disabled for SQL Server; key reuse; placeholder credentials. All ports bind 127.0.0.1 and files carry "not for production" banners; compose secrets are required from untracked `.env` (`.env.example` contains only `CHANGE_ME`).
- **Exploit scenario:** Copy/paste into a shared host exposes an auto-authenticated gateway.
- **Remediation:** Run nginx unprivileged (`nginxinc/nginx-unprivileged`), separate per-purpose secrets, keep a CI check that benchmark configs cannot be selected with `ASPNETCORE_ENVIRONMENT=Production`.

### Low — Floating SDK / advisory level in CI
- **File:** `.github/workflows/ci.yml` and `docker-publish.yml` (`dotnet-version: '10.0.x'`), `Directory.Build.props:9-11` (`NuGetAuditLevel` moderate)
- **CWE:** CWE-1104
- **Confidence:** Confirmed
- **Issue:** Runtime base image is digest pinned but the build SDK floats; advisories rated Low are not reported. `global.json` pins 10.0.401 with `rollForward: latestFeature`.
- **Exploit scenario:** Unreviewed SDK update changes build output between runs.
- **Remediation:** Pin `dotnet-version` to the `global.json` version; set `NuGetAuditLevel` to `low` or keep an explicit allowlist.

### Verified OK (no finding)
- Containers run as UID 10001 (`Dockerfile:34`, `.production`, `.staging`, helm `runAsNonRoot`, `readOnlyRootFilesystem`, `drop: ALL`, `allowPrivilegeEscalation: false`, seccomp `RuntimeDefault`); compose has `read_only`, `cap_drop: ALL`, `no-new-privileges`, loopback port binding. Root `Dockerfile` sets `ASPNETCORE_ENVIRONMENT=Production`; Development in containers needs `AUTHERIS_ALLOW_DEV_IN_CONTAINER` (`GatewayStartupValidator.cs:145-152`); `appsettings.json` has no dev/danger flags; `appsettings.Development.json` only loads in Development; `DevConfiguration.Apply` returns early outside Development (`:36`).
- Base images and compose image are digest pinned (`mcr.microsoft.com/dotnet/aspnet@sha256:2227...`, `docker-compose.yml:14`); benchmark postgres/redis/prometheus/grafana use tags only (benchmark scope).
- NuGet: single source with source mapping `*` -> nuget.org, `signatureValidationMode=require`, repository signer fingerprints pinned (certificate expiry 2027-05-18 noted in the file), lock files with locked-mode restore in CI, `NuGetAudit` all, NU1901-4 as errors. All third-party GitHub Actions are pinned to commit SHAs; `permissions: {}` at workflow level; CODEOWNERS covers Dockerfiles, deploy, props.
- Secrets: no private keys/certs/.env tracked (including history search for PEM headers); `.env.example` uses `CHANGE_ME`; `.gitignore`/`.dockerignore` exclude `.env*`, `*.pem`, `*.key`, `*.db`. Only hard-coded values found: WORM config key (finding above), SQLite legacy audit key (SEC-HIGH-18), Development-only fallbacks (`SqlServerGovernanceRepository.cs:71` guarded by dev/test transport check; `ColumnMaskingProvider` dev salt).
- Stray files: `fix4.tgz` and `fix4b.tgz` are 0-byte, untracked and ignored by `.gitignore:33` (`tar tzf` fails: empty gzip); `src/Autheris.Api/scratch/jobs` is an empty untracked directory ignored by `.gitignore:34`. Neither is tracked at 88f7df1c, neither is referenced by `Autheris.Api.csproj`, and the images only `COPY dist/publish/` (`.dockerignore` also excludes `dist/*` except publish). They do not reach the image or the repository.
- `/swagger`, `/docs`, `/ui/swagger` are `AllowAnonymous` but perform their own authentication/Open-Schema check (`ODataEndpoints.cs:263-281`); `/api/dev/*` is feature-gated; `/metrics` requires authorization.

Note on `dotnet list package --vulnerable --include-transitive`: two attempts (one with restore, one with `--no-restore`, 250-280 s each) timed out in this environment without producing output, so the package-vulnerability result rests on the repo's own lock files and commit 4ca84c5a (Pkcs 9.0.18 at 88f7df1c vs 10.0.13 on feat/ast-x1-cutover). Re-run the command in CI or locally on the target ref to confirm the full list.
