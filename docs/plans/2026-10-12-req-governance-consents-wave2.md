# PRD: Wave 2 Governance, Consents & Scope-Specific Enforcement

**Document ID:** `REQ-WAVE2-GOVERNANCE-2026-10-12`  
**Date:** 2026-10-12  
**Status:** APPROVED / READY FOR ARCHITECTURE 📝  
**Author:** Lead Product Manager & Gatekeeper  
**Target Path:** `docs/plans/2026-10-12-req-governance-consents-wave2.md`  
**Parent Plan:** [PLAN-GH-ISSUES-ROADMAP-2026-10-12](file:///root/autheris/docs/plans/2026-10-12-plan-github-issues-roadmap.md)  
**Track:** Wave 2 Delivery (Governance, Consents & Scopes)  

---

## 1. Executive Summary & Problem Statement

Enterprises deploying Autheris require declarative, automated governance workflows alongside granular, self-service transparency and delegated delegation boundaries:

1. **GitOps Declarative Policy Synchronization (Issue #40):** Currently, data consents and row filters are managed via interactive GraphQL mutations or administrative UI. Enterprise security teams managing infrastructure-as-code (IaC) require an idempotent API (`POST /api/v1/governance/consents/apply`) to reconcile version-controlled GitOps policy manifests against the database, generating cryptographic WORM audit trails for every applied change.
2. **Self-Service Policy Explanation & Introspection (Issue #35):** Data consumers and AI agents struggle to diagnose why queries return truncated rows or empty fields without administrative assistance. A self-service `myAccess(table: String!)` GraphQL query and `describe_my_access(table: string)` MCP tool must provide transparent, human-readable explanations of active row filters, masked columns, and consent descriptions without leaking policies of other users or raw SQL.
3. **Access Request Inboxes & Self-Service Lifecycle (Issue #34):** Requesters lack visibility into the approval status of their access requests, and data owners lack a structured GraphQL inbox (`myAccessRequests`, `pendingApprovalsForMe`, `myActiveConsents`) to review pending approvals across tables they own.
4. **Agentic Scope-Specific Masking & Monotonic Hardening (Issue #26):** Delegated AI agent tokens (e.g. carrying OAuth scope `Agent.Read`) require stricter masking and deny rules (e.g. redaction of PII and financial metrics) than direct interactive sessions by the same authenticated user. Under the Monotonic Hardening Principle, token scopes can strictly tighten policies, never loosen permissions.
5. **Differentiated Application Scopes & Cost Quotas (Issue #29):** High-frequency agentic or mobile client applications require separated read-only scopes (`App.Read` alongside `Agent.Read`) with distinct query complexity and cost limits to protect database infrastructure.

---

## 2. Industry Benchmarks & Standards Alignment

- **GitOps Invariant (CNCF / OpenFGA):** Idempotent desired-state reconciliation. Re-applying the exact same manifest must result in a no-op with 0 state changes.
- **OWASP API Security Top 10 (2023):**
  - **API1:2023 Broken Object Level Authorization:** Approver inboxes and request queries must strictly enforce tenancy and data ownership boundaries.
  - **API3:2023 Broken Object Property Level Authorization:** Scope-specific column masking ensures LLMs and agents cannot exfiltrate unauthorized clear-text fields.
  - **API4:2023 Unrestricted Resource Consumption:** Scope-based cost limiting prevents rogue automated agents from exhausting compute capacity.
- **Fail-Closed Zero-Trust:** Everything is denied by default; scope restrictions apply monotonically.

---

## 3. High-Level Requirements

### REQ-W2-01: Declarative GitOps Consent Apply API (`#40`)
- **Endpoint:** `POST /api/v1/governance/consents/apply`
- **Payload:** Declarative YAML or JSON specification containing a list of target tables, grantees, column access levels (`Clear`, `Mask`, `Deny`), and row filter predicates.
- **Semantics:** Idempotent reconciliation (Desired State vs Current State). Supports `dryRun: true/false`.
- **Audit:** Every created, updated, or revoked consent must be sealed into the WORM HMAC-SHA256 audit log.

### REQ-W2-02: Self-Service `myAccess` & `describe_my_access` (`#35`)
- **GraphQL Field:** `myAccess(table: String!): EffectiveTableAccess!`
- **MCP Tool:** `describe_my_access(table: string): McpEffectiveAccess!`
- **Returned Information:**
  - Overall decision (`ALLOWED`, `DENIED`, `REQUIRES_CONSENT`).
  - Active Row Filters explained in plain language (e.g. `Department = 'Finance' (resolved from your department attribute)`).
  - Column access levels: List of masked columns, clear-text columns, and denied columns.
  - Plain-text description and expiration timestamp from active consent.
- **Anti-Leakage Invariant:** Only the caller's own effective permissions are revealed. Never disclose policies belonging to other identities or raw database secrets.

### REQ-W2-03: Access Request & Approver Inboxes (`#34`)
- **GraphQL Queries:**
  - `myAccessRequests(status: String, first: Int, after: Int): [ConsentRequestDto!]!`
  - `pendingApprovalsForMe(first: Int, after: Int): [ConsentRequestDto!]!`
  - `myActiveConsents(table: String, first: Int, after: Int): [ConsentDto!]!`
- **Tenancy & Authorization:**
  - `myAccessRequests` filters strictly by the authenticated caller's SID.
  - `pendingApprovalsForMe` checks table ownership/delegation via `IDataOwnershipRepository.IsAuthorizedApproverForTableAsync`.
  - Unauthorized third parties receive an empty list without error or leak.

### REQ-W2-04: Scope-Specific Column Masking & Monotonic Hardening (`#26`)
- Column rules support an optional `ApplicableScopes` list (e.g. `["Agent.Read"]`).
- When an authenticated user possesses `Agent.Read`, matching column rules are enforced even if the user identity has clear-text access.
- Monotonic principle: Token scopes can only escalate restrictions (Clear -> Mask, Clear -> Deny, Mask -> Deny), never grant access denied to the user identity.

### REQ-W2-05: Application Scopes & Quota Segregation (`#29`)
- Register `App.Read` in `ReadOnlyScopes`.
- Allow configuring separate query cost ceilings and rate limits for `App.Read` and `Agent.Read`.

---

## 4. Acceptance Criteria & Quality Gates

1. **GitOps Invariant:** Running `POST /api/v1/governance/consents/apply` twice with the same payload results in 0 modifications on the second run (`"applied": 0, "unchanged": N`).
2. **Access Isolation:** Calling `myAccessRequests` only returns requests originated by the caller's SID.
3. **Approver Isolation:** Calling `pendingApprovalsForMe` only returns requests for tables where the caller is registered in `DATA_OWNERS` or `DATA_OWNER_DELEGATIONS`.
4. **Scope Hardening:** Querying a table with `Agent.Read` masks restricted fields; querying without `Agent.Read` returns clear-text.
5. **Test Coverage:** Full test suite passes with 0 warnings, 0 errors, 100% green.
