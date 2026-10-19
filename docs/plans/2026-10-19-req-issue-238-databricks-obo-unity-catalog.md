# Requirements Specification: Issue #238 Databricks OBO & Unity Catalog Access Enforcement

**Document ID:** `REQ-DATA-238` (also tracked as `WP-SC-15`)  
**Date:** 2026-10-19  
**Status:** APPROVED (Strategic / Scale Track)  
**Author:** Delivery Project Manager & Product Owner (`project-manager`)  
**Parent Plan:** [`docs/plans/00-master-plan-overview.md`](00-master-plan-overview.md), [`docs/plans/2026-10-16-plan-scale-5000-users.md`](2026-10-16-plan-scale-5000-users.md)  
**Target Milestone:** v1.3.0 (Minor Release / Enterprise Track)  

---

## 1. Executive Summary & Strategic Motivation

In enterprise lakehouse architectures, access rights, table entitlements, row-level filters, and column masks are managed natively within **Databricks Unity Catalog**. 
When Autheris connects to Databricks using a static administrative service principal, Unity Catalog evaluates permissions based on that service principal. This either requires duplicating all Unity Catalog rules inside Autheris, or risks over-privileged execution where individual end-user restrictions are bypassed.

GitHub issue #238 implements work package `WP-SC-15` from the *Scale to 5,000 Users* architecture track (`PLAN-SCALE-5000`). It introduces **connector-agnostic delegated enforcement** and **OAuth 2.0 On-Behalf-Of (OBO)** authentication for Databricks. Autheris exchanges the caller's incoming Entra ID access token for a downstream Azure Databricks access token, executing SQL statements directly as the calling person so that Unity Catalog natively enforces fine-grained authorization.

---

## 2. Functional Requirements

### FR-238-1: Connector-Agnostic Delegation Contracts
- **FR-238-1.1 (`IDelegationCapability`):** Introduce connector capability interface implemented by data source providers, declaring:
  - `SupportedDelegationKinds`: Set containing `TokenDelegation` (OBO), `KerberosConstrainedDelegation`, or `AssertedIdentity`.
  - `SourceEnforcesRowSecurity`: Operator attestation that the source natively evaluates row-level security.
  - `SourceEnforcesMasking`: Operator attestation that the source natively evaluates column masking.
  - `SupportsCorrelationTag`: Indicates whether the connector supports query tagging for audit linkage.
- **FR-238-1.2 (`IDelegatedCredentialProvider`):** Generic provider returning connector-specific execution credentials based on `DelegationContext`.

### FR-238-2: Data Source Configuration & Startup Validation
- **FR-238-2.1 (`EnforcementMode`):** Add `EnforcementMode` option to `DataSourceConnectionOptions`:
  - `AutherisEnforced` (default): Autheris applies RLS, masking, and table access controls.
  - `Delegated`: Coarse admission gate in Autheris (authentication, ReBAC, deny-list, purpose), while RLS and masking are delegated to native Unity Catalog.
  - `DelegatedDefenceInDepth`: Both Autheris policies and native Unity Catalog policies are applied concurrently.
- **FR-238-2.2 (`Obo` Configuration Block):**
  - `ClientId`: Client ID for the Azure Databricks application registration.
  - `ClientCertificateRef` or `ClientSecretRef`: Credentials for MSAL confidential client application.
  - `Scopes`: Azure Databricks resource scope (default `2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default`).
  - `AllowLongRunning`: Boolean (default `false`) controlling refresh token retention for asynchronous jobs.
- **FR-238-2.3 (Fail-Closed Startup Validation):**
  - If `EnforcementMode` is `Delegated` or `DelegatedDefenceInDepth`, `TrustedRealms` MUST be non-empty and every realm MUST support token delegation.
  - `KeySetCacheAllowed` MUST be `false` (Issue #107 cross-validation).
  - Profiling, value-profile, and semantic-cache features MUST be disabled or strictly user-keyed.

### FR-238-3: MSAL.NET On-Behalf-Of Token Exchange
- **FR-238-3.1 (Incoming Token Validation):** Incoming caller token MUST be an Entra access token with valid audience and delegated user scopes (`scp`). ID tokens and client-credentials tokens without user context MUST be rejected.
- **FR-238-3.2 (OBO Exchange):** Use MSAL.NET `ConfidentialClientApplication.AcquireTokenOnBehalfOf(scopes, new UserAssertion(rawToken))` to acquire downstream token.
- **FR-238-3.3 (In-Memory Bounded Cache):** Partition token cache by `(host, clientId, assertionSha256Hash)` using `BoundedCache`. Set TTL to `min(downstream_token_exp, caller_assertion_exp)`.
- **FR-238-3.4 (Error Mapping - No Service Principal Fallback):**
  - `invalid_grant`, `interaction_required`, or `consent_required` MUST map to HTTP 401 Unauthorized with OAuth claims challenge.
  - Network or identity provider failures MUST map to HTTP 503 Service Unavailable.
  - The static service principal MUST NEVER be used as a fallback under any circumstance!

### FR-238-4: Databricks Execution & Audit Correlation
- **FR-238-4.1 (`IDatabricksCredentialProvider`):** `DatabricksStatementExecutionClient` MUST obtain credentials via `IDatabricksCredentialProvider`, injecting `Bearer <obo_token>` into the HTTP request header.
- **FR-238-4.2 (Query Tagging):** Transmit Autheris correlation ID and trace ID as Databricks query tags (`query_tags = { "autheris_correlation_id": "...", "autheris_trace_id": "..." }`) to allow bidirectional audit correlation with Databricks Unity Catalog audit logs.

---

## 3. Non-Functional & Security Requirements

- **SEC-238-1 (Zero Token Leakage):** Under no circumstance may raw caller assertions, OBO bearer tokens, client secrets, or refresh tokens be emitted in log files, OpenTelemetry span attributes, audit event payloads, or exception messages. Strict regex redaction tests required.
- **SEC-238-2 (Tenant & User Partitioning):** Decision caches and query results for `Delegated` data sources MUST incorporate the caller user identity key to prevent cross-user data leakage.
- **SEC-238-3 (Audit Trail Continuity):** Record high-integrity audit events containing `realm`, `user_key`, `source_name`, `enforcement_mode`, and `correlation_id` upon token acquisition.

---

## 4. Acceptance Criteria

1. **AC-238-1 (OBO Token Exchange):** Valid user token initiates MSAL OBO exchange and passes user-scoped bearer token to Databricks REST API.
2. **AC-238-2 (Unity Catalog Access Gate):** Queries against Databricks execute with the identity and permissions of the end-user, returning Unity Catalog authorization errors if user lacks privileges in Databricks.
3. **AC-238-3 (No Fallback):** When OBO exchange fails (e.g. invalid grant), request fails with 401/claims challenge; service principal credentials are NOT used.
4. **AC-238-4 (Audit & Redaction):** Query tags appear in Databricks requests; all telemetry and logs are 100% free of token strings.
5. **AC-238-5 (Test Suite Green):** 100% test pass rate across unit, mock-integration, and security test suites.

---

## 5. Architectural & Phase Assignment

- **Phase 1 (PM):** Intake & PRD approved (this document, `REQ-DATA-238` / `WP-SC-15`).
- **Phase 2 (Architecture):** Solution Architect prepares implementation plan (`2026-10-19-plan-issue-238-databricks-obo-unity-catalog.md`).
- **Phase 3 (Security):** Security Expert audits OBO trust boundary, MSAL caching, token redaction, and fail-closed validators.
- **Phase 4 (C# TDD Developer):** Red-Green-Refactor development targeting v1.3.0.
- **Phase 5 (Code Review):** Code inspection and PR merge.
