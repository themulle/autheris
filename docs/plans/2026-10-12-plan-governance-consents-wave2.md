# Implementation Plan: Wave 2 Governance, Consents & Scope-Specific Enforcement

**Document ID:** `PLAN-WAVE2-GOVERNANCE-2026-10-12`  
**Date:** 2026-10-12  
**Status:** APPROVED / IN EXECUTION 🚀  
**Author:** Delivery Project Manager, Solution Architect & Security Expert  
**Target Branch:** `feat/governance-consents-wave2`  
**Target Path:** `docs/plans/2026-10-12-plan-governance-consents-wave2.md`  
**Parent Plan:** [PLAN-GH-ISSUES-ROADMAP-2026-10-12](file:///root/autheris/docs/plans/2026-10-12-plan-github-issues-roadmap.md)  
**PRD:** [REQ-WAVE2-GOVERNANCE-2026-10-12](file:///root/autheris/docs/plans/2026-10-12-req-governance-consents-wave2.md)  

---

## 1. Executive Summary & Delivery Scope

This implementation plan delivers **Wave 2** of the Autheris GitHub Issues Roadmap, focusing on enterprise governance, declarative policy synchronization, self-service introspection, and monotonic scope-specific security hardening:

| Issue | Title | Complexity | Component Touchpoints |
| :---: | :--- | :---: | :--- |
| **#40** | GitOps / Declarative Consent & Row Filter Import API | Medium | `DeclarativeConsentApplyService.cs`, `GovernanceEndpoints.cs`, `IConsentRepository` |
| **#35** | Self-Service `describe_my_access` / Explain Effective Rules | Low-Medium | `EffectiveAccessExplanationService.cs`, `GovernanceQueries.cs`, `McpDatasetCatalog.cs` |
| **#34** | Access Request Queries & Approver Inbox | Low-Medium | `IConsentApprovalRepository`, `GovernanceQueries.cs` |
| **#26** | Scope-Specific Masks and Denies (e.g. `Agent.Read`) | Medium | `ConsentColumnRule.cs`, `ConsentResolutionService.cs`, `IColumnMaskingProvider` |
| **#29** | Separate Application Scope (`App.Read`) & Scope-Specific Quotas | Low | `GatewayOptions.cs`, `FinOpsBudgetMiddleware.cs` |

---

## 2. Technical Architecture & Work Package Specifications

### WP-1: Issue #40 – Declarative GitOps Policy Import (`POST /api/v1/governance/consents/apply`)
* **Design Solution:**
  1. Define DTO models in `Autheris.Domain.Model`:
     ```csharp
     public sealed record DeclarativeConsentManifest(
         string Version,
         IReadOnlyList<DeclarativeConsentPolicy> Policies);

     public sealed record DeclarativeConsentPolicy(
         string Domain,
         string Schema,
         string TableName,
         string GranteeType,
         string GranteeRef,
         string Effect,
         IReadOnlyList<DeclarativeColumnRule>? ColumnRules,
         IReadOnlyList<DeclarativeRowFilter>? RowFilters,
         int? ValidityDays,
         string? Description);
     ```
  2. Implement `IDeclarativeConsentApplyService` in `Autheris.Application.Governance.Services`:
     - Compares desired manifest against current active consents for `(Table, Grantee)`.
     - Idempotent: If existing active consent matches desired rules, marks as `Unchanged`.
     - If rules differ or no active consent exists, revokes obsolete consent and creates new consent.
     - Supports `dryRun: true` mode returning plan preview without database mutations.
     - Records WORM audit entry (`EVENT_TYPE: GITOPS_CONSENT_APPLY`) for all applied mutations.
  3. Register endpoint `POST /api/v1/governance/consents/apply` in `GovernanceEndpoints.cs` requiring `GovernanceAdmin` role.

---

### WP-2: Issue #35 – Self-Service Policy Explanation (`myAccess` & `describe_my_access`)
* **Design Solution:**
  1. Define `EffectiveTableAccessDto` in `Autheris.Domain.Model`:
     ```csharp
     public sealed record EffectiveTableAccessDto(
         string Table,
         string Decision,
         IReadOnlyList<string> AllowedColumns,
         IReadOnlyList<string> MaskedColumns,
         IReadOnlyList<string> DeniedColumns,
         IReadOnlyList<string> ActiveRowFilters,
         string? ConsentDescription,
         DateTimeOffset? ValidTo);
     ```
  2. Implement `IEffectiveAccessExplanationService` in `Autheris.Application.Governance.Services`:
     - Resolves table metadata and active consents for the authenticated caller SID.
     - Classifies columns into `AllowedColumns`, `MaskedColumns`, and `DeniedColumns`.
     - Translates active row filters into human-readable descriptions (e.g. `Country EQ 'DE'`).
     - Anti-leakage: Strictly scopes evaluation to the caller's principal context; never evaluates or exposes rules of other users.
  3. Expose GraphQL query: `myAccess(table: String!): EffectiveTableAccessDto!` in `GovernanceQueries.cs`.
  4. Expose MCP tool: `describe_my_access(table: string)` in `McpDatasetCatalog.cs`.

---

### WP-3: Issue #34 – Access Request Queries & Approver Inbox
* **Design Solution:**
  1. Expose GraphQL queries in `GovernanceQueries.cs`:
     - `myAccessRequests(status: String)`: Resolves all `ConsentRequest` records where `RequesterSid == caller.UserSid`.
     - `pendingApprovalsForMe`: Resolves all pending requests where caller is authorized approver (checked against `IDataOwnershipRepository.IsAuthorizedApproverForTableAsync`).
     - `myActiveConsents(table: String)`: Resolves all active consents where grantee matches caller SID.
  2. Map results to clean GraphQL DTOs (`ConsentRequestDto`, `ConsentDto`).

---

### WP-4: Issue #26 – Scope-Specific Column Masking (`Agent.Read`)
* **Design Solution:**
  1. Extend `ConsentColumnRule` with `public IReadOnlyList<string> ApplicableScopes { get; init; } = [];`.
  2. In `ConsentResolutionService.cs` / `IColumnMaskingProvider`:
     - Resolve active caller scopes from `SecurityPrincipalContext.Claims` (`scp` claim).
     - When caller possesses scope (e.g. `Agent.Read`) and rule matches `ApplicableScopes`, apply rule.
     - **Monotonic Hardening Invariant:** Scope rules can only tighten access (e.g. from Clear to Mask, or Clear to Deny). They can never escalate access denied by the base user consent.

---

### WP-5: Issue #29 – Application Scopes (`App.Read`) & Quota Attribution
* **Design Solution:**
  1. In `GatewayOptions.cs`, add `"App.Read"` to default `ReadOnlyScopes`:
     ```csharp
     public List<string> ReadOnlyScopes { get; init; } = ["Agent.Read", "App.Read"];
     ```
  2. In `FinOpsBudgetMiddleware.cs`, resolve caller scope and attach scope dimension to query cost telemetry and quota checks.

---

## 3. Phase 3: Security & Compliance Review

- [x] **Zero-Trust Role Gate:** `POST /api/v1/governance/consents/apply` strictly requires `GovernanceAdmin` or `ClusterAdmin` role.
- [x] **Monotonic Hardening:** Scopes (`Agent.Read`, `App.Read`) can strictly degrade permissions (masking/denial); they never widen permissions.
- [x] **Anti-Oracle & Privacy:** `myAccess` and `describe_my_access` only reveal rules applying to the authenticated caller SID.
- [x] **WORM Sealing:** All GitOps changes are committed with HMAC-SHA256 audit log events.

---

## 4. Phase 4: Delivery Sequence (Red-Green-Refactor)

- [ ] **Step 1:** Implement WP-4 (`ApplicableScopes` on `ConsentColumnRule` & Monotonic Scope Masking) + Unit Tests.
- [ ] **Step 2:** Implement WP-5 (`App.Read` in `ReadOnlyScopes` & FinOps attribution) + Unit Tests.
- [ ] **Step 3:** Implement WP-2 (`IEffectiveAccessExplanationService`, `myAccess`, `describe_my_access`) + Unit Tests.
- [ ] **Step 4:** Implement WP-3 (Approver Inbox & Request Queries) + Unit Tests.
- [ ] **Step 5:** Implement WP-1 (Declarative GitOps Apply API & Service) + Unit Tests.
- [ ] **Step 6:** Run full test suite (`dotnet test Autheris.sln`) -> verify 100% green.
