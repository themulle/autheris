# P10: Enterprise Governance Mutations & 4-Eyes SoD

**Status:** [Done] (100% GA – Core Foundation)  
**Components:** [`MutationTypes.cs`](file:///root/autheris/src/Autheris.GraphQL/Types/MutationTypes.cs), [`RedisIdempotencyStore.cs`](file:///root/autheris/src/Autheris.Infrastructure/Caching/RedisIdempotencyStore.cs), [`ConsentRecertificationWorkflowService.cs`](file:///root/autheris/src/Autheris.Application/Workflows/ConsentRecertificationWorkflowService.cs)

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
