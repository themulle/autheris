# Requirements Specification: REQ-SEC-271 - Comprehensive Security Audit Hardening

**Document ID:** `REQ-SEC-271`  
**Date:** 2026-10-21  
**Status:** MERGED (PR #273, `b2b1bf2f`); follow-up hardening on `fix/security-review-open-findings` (implemented, not built). Open: `REQ-SEC-271-15` partial (fallback to `Gateway:Cluster:Secret`, per-process random key outside Development), `REQ-SEC-271-03` startup rule not implemented (purge threshold uses the max of all retention periods instead).  
**Author:** Product Manager (`product-manager`)  
**Security Lead:** Lead Application Security Expert (`csharp-security-expert`)  
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)  
**Target Branch:** `fix/issue-271-security-audit`  
**Binding Directives:** Universal English standard; Zero-Trust fail-closed defaults; strictly parameterized SQL; no documentation phase in `docs/features/` or `README.md` (owner rule 2026-10-17).

---

## 1. Context & Business Drivers

Security audits of the Autheris Zero-Trust Data Gateway identified 7 critical, high, and medium severity findings across CI supply-chain, compliance audit retention, administrative four-eyes separation of duties, multi-tenant route guards, MCP gateway authorization, authentication/SSRF defense, and SQL AST evaluation.

Addressing these vulnerabilities ensures strict compliance with ISO/IEC 27001, SOC 2 Type II, GDPR Article 32, and enterprise supply chain security requirements.

---

## 2. Scope & Requirements Breakdown

### Finding 01: CI Supply Chain - Fork PR Image Publication & Signing Guard (High)
* **`REQ-SEC-271-01`**: `.github/workflows/docker-publish.yml` must only trigger image build, publish, and keyless cosign signing for runs triggered by repository pushes or workflow runs originating from the canonical repository (`head_repository.full_name == github.repository`) on `main` or release tags. Runs originating from fork pull requests must NEVER reach build, push, or signing steps.

### Finding 02: Audit Retention - Category-Specific Retention Periods (High)
* **`REQ-SEC-271-02`**: `AuditRetentionPurgeWorker` must honor category-specific retention periods (`SecurityDays` [default 3650 days], `AuditLogRetentionDays` [3650 days], `DataAccessDays` [400 days], and `RetentionDays` [90 days]).
* **`REQ-SEC-271-03`**: Under the single-prefix immutable HMAC chain architecture, the purge threshold must be computed as `now - max(RetentionDays, OperationsDays, DataAccessDays, SecurityDays, AuditLogRetentionDays)` so that no `Security` category audit entry is purged before its configured retention expires. Startup validation must reject configurations where `RetentionDays < SecurityDays` unless an external WORM archive sink is configured.

### Finding 03: Administrative Four-Eyes Bypass Remediation (High)
* **`REQ-SEC-271-04`**: In `GrantPolicy.cs`, when a grant class requires four-eyes approval (`fourEyes == true`), if no valid approvals or insufficient distinct approvers are supplied (`r.Approvers is null` or count < needed), `GrantPolicy` MUST fail-closed and return `GrantDecision.Deny(GrantErrorCodes.FourEyesRequired, ...)` instead of allowing immediate active grant creation.
* **`REQ-SEC-271-05`**: In dbt governance import (`POST /api/extensions/dbt/governance?replace=true`), masking rule relaxations, sensitivity downgrades, and virtual filter updates must not be automatically applied with `IsSync = true`. Changes must be validated against the caller's token tenant, require GovernanceAdmin (removing `DbtAdmin` bypass for relaxations), and enforce approval ratchet.
* **`REQ-SEC-271-06`**: In declarative consent manifests (`DeclarativeConsentApplyService`) and bulk consents (`POST /api/v1/consents/bulk`), self-grant validation (`IsSelfGrantSubject`) must be enforced against all caller claims including role claims (e.g. `TenantAdmin`), preventing administrators from granting unmasked access to themselves.

### Finding 04: Multi-Tenancy - Route `{tenantId}` Validation Guard (Medium)
* **`REQ-SEC-271-07`**: A shared endpoint filter (`ValidateTenantRouteMatchFilter`) must be applied to all `/api/v1/tenants/{tenantId}/...` route groups (`AccessPolicyEndpoints`, `FunctionalRightEndpoints`, `RecertificationEndpoints`, `SubjectLifecycleEndpoints`). If `{tenantId}` in the route does not match the caller's token tenant from `GetRequestTenant()`, the request must return `403 Forbidden`, unless the caller is a verified `ClusterAdmin`.

### Finding 05: MCP Gateway Authorization & Data Guardrails (Medium)
* **`REQ-SEC-271-08`**: In `McpDocTools.cs`, `deposit_documentation` must restrict global app documentation (`autheris`, etc.) to canonical `ClusterAdmin` and isolate tenant docs under tenant-specific paths (`{root}/{tenantId}/{appId}/...`), preventing cross-tenant documentation overwrites and stored prompt injections. Server-side `FilePath` must never be returned in MCP tool output.
* **`REQ-SEC-271-09`**: In `AiDataGuardrailService.cs`, `call_procedure` and `query_metrics` must resolve the underlying physical tables backing the procedure or semantic metric and enforce four-eyes / HITL approval gates if any underlying table requires it.
* **`REQ-SEC-271-10`**: In `McpToolExecutionHandler.cs`, `get_data_lineage` must filter lineage nodes by the caller's tenant and catalog visibility (`CatalogVisibleBitSet`). If a dataset is not visible or belongs to another tenant, a uniform "not found" response must be returned to avoid existence oracles.

### Finding 06: Authentication Hardening & SSRF Egress (Medium)
* **`REQ-SEC-271-11`**: TOTP 2FA enrollment in `DevPortalEndpoints.cs` must require verified administrative bootstrap or re-authentication with fresh credentials, preventing attackers with stolen session cookies from self-enrolling a new TOTP secret for unenrolled approvers.
* **`REQ-SEC-271-12`**: In `CatalogDiscoveryService.cs`, OpenAPI spec downloading must use a hardened HTTP client configured with SSRF protection (`SsrfProtectionHandler`), redirect disabling, and DNS rebinding defense (`ConnectCallback` IP pinning).

### Finding 07: SQL Governance & Secrets Hygiene (Medium/Low)
* **`REQ-SEC-271-13`**: In `SemanticMetricQueryService.ConsentAndEvaluation.cs`, metric filter expression combination must parse and validate syntax nodes or reject unbalanced parentheses, preventing injection breakout via malicious metric `where` clauses.
* **`REQ-SEC-271-14`**: `information_schema.columns` in `TrinoMetadataQueryHandler.cs` must exclude columns marked with `ColumnAccessLevel.Deny` for the caller.
* **`REQ-SEC-271-15`**: Actor pseudonymization in `GatewayGovernanceServiceExtensions.cs` must use a dedicated cryptographically secure secret (or derive via HKDF from the master audit key) and fail closed rather than falling back to public client IDs.

---

## 3. Acceptance Criteria

1. All 7 findings remediated with corresponding unit and security verification tests under `tests/Autheris.Tests.Unit/` and `tests/Autheris.Tests.Integration/`.
2. `dotnet build Autheris.sln` compiles with 0 errors and 0 warnings.
3. 100% test pass rate across the full solution.
