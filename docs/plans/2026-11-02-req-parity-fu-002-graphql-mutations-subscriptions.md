# PRD: PARITY-FU-002 GraphQL Mutation & Subscription Sweep in Nightly Parity

**Document Type:** Requirements & Product Definition (PRD)  
**ID:** `REQ-PARITY-FU-002`  
**Tracking Plan:** [`docs/plans/2026-10-23-plan-nightly-parity-tests.md`](2026-10-23-plan-nightly-parity-tests.md)  
**Master Plan:** [`docs/plans/00-master-plan-overview.md`](00-master-plan-overview.md) (Track 4)  
**Date:** 2026-11-02  
**Status:** PROPOSED 📝  

---

## 1. Executive Summary & Problem Statement

In the Autheris Nightly Parity Test Suite ([`REQ-PARITY-NIGHTLY`](2026-10-23-req-nightly-parity-tests.md)), **Block F (GraphQL Introspection Sweep)** verifies all query root fields against the shared parity database seed.

However, mutations and subscriptions were explicitly deferred under follow-up **`PARITY-FU-002`**:
1. **Mutations change state:** Running mutations against the shared `ParityHostFixture` database seed would mutate data (e.g., revoking consents, modifying access requests, reloading schemas), breaking downstream assertions in the shared parity test collection.
2. **Subscriptions require realtime transport:** Realtime subscriptions (`OnTableChanged`) require `Gateway:GraphQL:Subscriptions:Enabled` and a WebSocket or event channel transport rather than a simple HTTP POST request.

As a result, `SpecGraphQlTests.Spec_GraphQlIntrospectionSweep` currently reports:
```
spec-graphql: mutations(skipped)=9 subscriptions(skipped)=0 (or 1 when enabled)
```
Without test coverage in the parity suite, schema mutations and subscriptions risk introducing undetected 5xx crashes, unhandled exceptions, or schema regressions.

---

## 2. Scope & Requirements

### 2.1 The 9 GraphQL Mutations
The sweep must introspect and validate every mutation root field defined in `Mutation`:
1. `setConsentDescription`
2. `requestTableAccess`
3. `withdrawAccessRequest`
4. `approveConsentRequest`
5. `rejectConsentRequest`
6. `revokeConsent`
7. `reloadSchema`
8. `syncOpenMetadata`
9. `syncDataCatalog`

### 2.2 Execution Requirements
- **Isolated State:** Each mutation must execute against an isolated ephemeral database instance (or dedicated per-test SQLite seed) so that no side-effects pollute other tests.
- **Minimal Valid Input Generation:** The test must dynamically build valid minimal input values from the introspected schema AST using `GraphQlSpecModel`, covering:
  - Base mutation call with required arguments.
  - Per-offered optional argument minimal call.
- **Fail-Closed Contract (SEC-PAR-05 & SEC-PAR-07):**
  - Every call must result in either success (HTTP 200 with result data) or a stable, registered 4xx-class code (e.g. `DECISION_NOT_PERMITTED`, `UNAUTHORIZED`, `ACCESS_REQUEST_NOT_PENDING`, `NOT_FOUND`, `INVALID_QUERY`).
  - **Zero Tolerance for 5xx:** No 500 status code, no `INTERNAL_SERVER_ERROR`, and no internal information leak (stack traces, exception type names, connection strings).
- **The 1 GraphQL Subscription (`onTableChanged`):**
  - Enable subscriptions via `Gateway:GraphQL:Subscriptions:Enabled = true`.
  - Validate subscription root field introspection in `SpecModel`.
  - Drive `onTableChanged` with valid and minimal inputs over the WebSocket transport / WebSocket test client, verifying connection establishment, lifecycle handling, and fail-closed rejection on invalid parameters.

---

## 3. Success Criteria & Non-Functional Requirements

- **NF-01 (Isolation):** Zero mutation side-effects leak into `ParityHostFixture` or other parity tests.
- **NF-02 (Performance):** Mutation and subscription sweeps complete within 10 seconds locally on SQLite.
- **NF-03 (Strict Typing):** All GraphQL error codes match `ParityErrorCodes.All` and pass `ParityAssert.StableCode` and `ParityAssert.NoServerError`.
- **NF-04 (Architecture):** No modification of shared read-only seed files; clean disposal of temporary databases.
