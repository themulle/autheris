# Implementation Plan: PLAN-SEC-271 - Security Audit Remediation

**Document ID:** `PLAN-SEC-271`  
**Date:** 2026-10-21  
**Status:** MERGED (PR #273, `b2b1bf2f`). Follow-up hardening is on `fix/security-review-open-findings` (implemented, not built or tested); see the master plan.  
**Author:** Solution Architect (`csharp-architect`)  
**Security Lead:** Lead Application Security Expert (`csharp-security-expert`)  
**Input PRD:** [`REQ-SEC-271`](2026-10-21-req-issue-271-security-audit.md)  
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)  
**Target Branch:** `fix/issue-271-security-audit`  
**Binding Directives:** Universal English standard; Zero-Trust fail-closed defaults; strictly parameterized SQL; no documentation phase in `docs/features/` or `README.md` (owner rule 2026-10-17).

---

## 1. Executive Summary & Architecture Strategy

This plan orchestrates the remediation of the 7 security findings reported in Issue #271 across Autheris. Execution follows a top-down risk-based prioritization:
- **Work Package 1 (High):** Supply chain CI hardening (`docker-publish.yml`) and audit prefix retention calculation (`AuditRetentionPurgeWorker.cs`).
- **Work Package 2 (High):** Four-eyes separation of duties enforcement in `GrantPolicy.cs`, dbt governance imports, and self-grant prevention.
- **Work Package 3 (Medium):** Tenant route matching endpoint guard filter across all `/api/v1/tenants/{tenantId}/...` endpoints.
- **Work Package 4 (Medium):** MCP Gateway hardening: global doc overwrite isolation, underlying table four-eyes gate resolution for procedures and metrics, and lineage tenant/visibility filtering.
- **Work Package 5 (Medium/Low):** AuthN TOTP & SSRF-hardened OpenAPI client, SQL metric filter parenthesis validation, and column deny visibility in `information_schema`.

---

## 2. Work Package Breakdown & Concrete Technical Tasks

### WP-01: CI Supply Chain & Audit Retention
1. **File:** `.github/workflows/docker-publish.yml`
   - Update job condition at line 22:
     ```yaml
     if: >-
       github.event_name != 'workflow_run' ||
       (github.event.workflow_run.conclusion == 'success' &&
        github.event.workflow_run.event == 'push' &&
        github.event.workflow_run.head_repository.full_name == github.repository &&
        github.event.workflow_run.head_branch == 'main')
     ```
2. **File:** `src/Autheris.Application/Audit/AuditRetentionPurgeWorker.cs`
   - In `CalculateThresholdUtc`, compute effective retention days as:
     ```csharp
     var effectiveRetentionDays = Math.Max(
         options.RetentionDays,
         Math.Max(options.SecurityDays,
         Math.Max(options.DataAccessDays,
         Math.Max(options.OperationsDays, options.AuditLogRetentionDays))));
     var thresholdUtc = now - TimeSpan.FromDays(effectiveRetentionDays);
     ```
   - In `GatewayStartupValidator.cs` / options validation, reject startup if `RetentionDays < SecurityDays` when no external archive sink is configured.

### WP-02: Four-Eyes Separation of Duties & Administrative Write Paths
1. **File:** `src/Autheris.Application/Governance/Grants/GrantPolicy.cs`
   - Fail closed: If `fourEyes == true`:
     - If `r.Approvers is null` or count < needed, return `GrantDecision.Deny(GrantErrorCodes.FourEyesRequired, "This grant class requires independent four-eyes approver(s).", cls, maxValidTo)`.
     - Verify approver != requester != grantee.
2. **File:** `src/Autheris.Extensions/Dbt/DbtMetadataIngestionService.cs` & `DbtEndpoints.cs`
   - For `/api/extensions/dbt/governance`, require `GovernanceAdmin` for any operation that relaxes masking or lowers sensitivity.
   - Restrict `IsSync: true` bypass strictly to internal synchronization flows; do not allow caller payload to set arbitrary tenant context.
3. **File:** `src/Autheris.Application/Governance/Services/DeclarativeConsentApplyService.cs` & `GovernanceEndpoints.Access.cs`
   - Check `IsSelfGrantSubject` against caller roles and SIDs.

### WP-03: Multi-Tenancy Route Guard
1. **File:** `src/Autheris.Api/Endpoints/EndpointSecurity.cs`
   - Create route endpoint filter extension: `RequireTenantRouteMatch()` that extracts `{tenantId}` from `RouteData` and compares against `GetRequestTenant(httpContext)`. If mismatch and caller is not `ClusterAdmin`, return `Results.Forbid()`.
2. Apply to `AccessPolicyEndpoints.cs`, `FunctionalRightEndpoints.cs`, `RecertificationEndpoints.cs`, and `SubjectLifecycleEndpoints.cs`.

### WP-04: MCP Gateway Authorization & Lineage Isolation
1. **File:** `src/Autheris.Application/Mcp/Services/McpDocTools.cs`
   - Require `ClusterAdmin` for global appId (`autheris`, `global`, `system`).
   - Prefix doc store paths with caller tenant ID for tenant-scoped docs.
   - Do not return local `FilePath` in MCP output.
2. **File:** `src/Autheris.Application/Mcp/Services/AiDataGuardrailService.cs`
   - In `EvaluateProcedureAsync` and `EvaluateMetricAsync`, resolve underlying physical tables and apply `RequiresFourEyes` evaluation.
3. **File:** `src/Autheris.Application/Mcp/Services/McpToolExecutionHandler.cs`
   - In `get_data_lineage`, filter nodes by caller tenant and `CatalogVisibleBitSet`. Return uniform "Lineage node not found" for forbidden/missing datasets.

### WP-05: AuthN Hardening, SSRF Egress & SQL Hardening
1. **File:** `src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.cs`
   - Register named HttpClient `"OpenApiIngestion"` using `AddSecureOutboundHandlers` with SSRF defense, redirect disabling, and timeout caps.
2. **File:** `src/Autheris.Application/Analytics/SemanticMetricQueryService.ConsentAndEvaluation.cs`
   - Validate metric filter parenthesis balance and syntax before combining.
3. **File:** `src/Autheris.Application/Services/TrinoMetadataQueryHandler.cs`
   - Filter `information_schema.columns` to hide columns where `ColumnAccessLevel == ColumnAccessLevel.Deny`.

---

## 3. Test Plan & Acceptance Matrix

- `SEC-TEST-271-01`: Docker publish workflow syntax and trigger inspection test.
- `SEC-TEST-271-02`: Audit retention purge test ensuring Security entries older than 90d but younger than 3650d are preserved.
- `SEC-TEST-271-03`: `GrantPolicyTests` verifying four-eyes denial when approvers are null or self-granting.
- `SEC-TEST-271-04`: Cross-tenant route access rejection tests returning 403 Forbidden.
- `SEC-TEST-271-05`: MCP doc tools tests verifying tenant isolation and file path omission; procedure/metric four-eyes tests; lineage isolation tests.
- `SEC-TEST-271-06`: OpenAPI ingestion client SSRF handler registration verification.
- `SEC-TEST-271-07`: Semantic metric parenthesis validation test; `information_schema.columns` column denial test.
