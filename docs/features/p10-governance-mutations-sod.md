# P10: Enterprise Governance Mutations & 4-Eyes SoD

**Status:** [Done] (100% GA – Core Foundation)  
**Components:** [`MutationTypes.cs`](file:///root/autheris/src/Autheris.GraphQL/Types/MutationTypes.cs), [`ConsentRequestService.cs`](file:///root/autheris/src/Autheris.Application/Consent/ConsentRequestService.cs), [`RedisIdempotencyStore.cs`](file:///root/autheris/src/Autheris.Infrastructure/Caching/RedisIdempotencyStore.cs), [`ConsentRecertificationWorkflowService.cs`](file:///root/autheris/src/Autheris.Application/Workflows/ConsentRecertificationWorkflowService.cs)

---

## 1. Overview & Problem Statement

Granting database access permissions must never be a single-click action performed without oversight. P10 introduces a complete suite of governance GraphQL mutations (`requestConsent`, `approveConsent`, `rejectConsent`, `revokeConsent`, `recertifyConsent`) with mandatory Segregation of Duties (SoD / Four-Eyes Principle): requesters cannot approve their own requests, approvals require two distinct authorized eyes, and 24-hour distributed idempotency keys prevent duplicate submission.

---

## 2. Business Value

- **Elimination of Insider Threats**: Strict Four-Eyes enforcement ensures no single employee can grant themselves access to confidential data.
- **Replay & Double-Submission Protection**: 24-hour Redis idempotency keys protect against accidental network retries or malicious replays.
- **Automated 30-Day Recertification**: Consents expire automatically unless recertified by data owners, eliminating persistent privilege accumulation.

---

## 3. Architecture & Capabilities

- Anti-Self-Approval validation rejecting requester approval attempts.
- Multi-approver tracking requiring two unique approving user SIDs.
- Automated recertification workflow with ServiceNow and Jira outbox integration.

---

The `requestTableAccess` use case (validation, idempotency, tenant resolution, auto-approve, external approval hand-off) lives in `IConsentRequestService` (Application layer); the GraphQL mutation only maps arguments and error codes. The external hand-off goes through the `IConsentRequestDispatcher` seam.

## 4. Usage Example

```graphql
# Request access to a restricted finance table
mutation RequestTableAccess {
  requestConsent(
    domain: "finance"
    schema: "dbo"
    tableName: "general_ledger"
    justification: "Q3 Financial Audit Compliance Analysis"
    daysValid: 30
    idempotencyKey: "req-fin-20261004-001"
  ) {
    consentId
    status # PENDING_APPROVAL
    message
  }
}

# Second authorized Data Owner approves:
mutation ApproveTableAccess {
  approveConsent(
    consentId: "c-10294-81a"
    idempotencyKey: "appr-fin-20261004-001"
  ) {
    status # APPROVED
  }
}
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "Governance": {
      "EnforceFourEyesPrinciple": true,
      "EnforceAntiSelfApproval": true,
      "IdempotencyTtlHours": 24,
      "DefaultConsentDurationDays": 30,
      "MaxConsentDurationDays": 90
    }
  }
}
```

---

## 6. Who Can Decide and How a Decision Is Applied

**Behaviour.** Only a person can approve or reject a consent request through `approveConsentRequest` and `rejectConsentRequest`. The decision guard (`IConsentDecisionGuard`) is the first step of both resolvers, before the approver identity is read, before the idempotency lookup and before any repository access. A refused decision answers `DECISION_NOT_PERMITTED`.

| Caller | Can decide |
|---|---|
| Interactive person (Basic, Kerberos/Negotiate, ForwardAuth, Entra ID or ADFS delegated token) who is an owner, delegate or `GovernanceAdmin` | yes |
| Delegated token with a scope claim (`scp`, also in its mapped URI form) and an `oid`/SID, even without a human-name claim | yes |
| Application token (`idtyp=app`, or an application id such as `appid`, `azp`, `client_id` without a delegated scope), also with the `GovernanceAdmin` role and `AppOnlyTokens=ReadWrite` | no |
| Certificate (mTLS) subject | no |
| Read-only token, unauthenticated or missing principal | no |

An operator-enabled `ReadWrite` administrative application keeps its governance admin REST access; that access is not a request decision. The external ITSM webhook activates and rejects requests through its signed callback and does not use the guard.

**Denial audit.** The first refusal of a principal in a minute writes one Tier-A `CONSENT_DENIED` entry that carries only the reason class (`read-only`, `not-person`, `unauthenticated`). Further refusals of the same principal in that minute are counted in `autheris_consent_decision_denied_total{reason,audited}` only. A failing audit write never changes the answer.

**Order of a decision.** For an individual grantee (user or service principal) the grant policy (`IGrantPolicy`, class maximum from the `IndividualGrantOptions`) is evaluated on the loaded request before the approval step, on the first and on the second step. Unknown table metadata classifies as sensitive. A refusal writes nothing: the request keeps its status (`PENDING` or `PENDING_SECOND_APPROVAL`) and can still be rejected. Role and group grantees are standing grants and skip the policy. The guard and the policy are resolved with `GetRequiredService`; a missing registration fails the call and never allows it.

**Status guard.** The approval step and the rejection change the request with `UPDATE CONSENT_REQUESTS SET status = @st WHERE id = @id AND status = @expected` on SQLite, PostgreSQL and SQL Server. `@expected` is the status read under the lock of the same transaction (`PENDING`, `PENDING_SECOND_APPROVAL` or the ITSM `PENDING_EXTERNAL_APPROVAL`). If no row matches, the transaction is rolled back (no approval step, no audit entry, no status change) and the call answers `ACCESS_REQUEST_NOT_PENDING`.

**Consent creation.** After the final approval step the consent is created in a second transaction. If that creation fails, the request stays `APPROVED`, the failure is logged at Error with the request id only, and the existing activation (`ActivateConsentAsync`, status `APPROVED`) completes it.
