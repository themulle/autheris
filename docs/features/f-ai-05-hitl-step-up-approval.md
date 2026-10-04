# F-AI-05: Human-in-the-Loop Step-Up Approval

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`IHitLStepUpApprovalService.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Mcp/Interfaces/IHitLStepUpApprovalService.cs), [`HitLStepUpApprovalService.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Mcp/Services/HitLStepUpApprovalService.cs)

---

## 1. Overview & Problem Statement

When autonomous AI agents perform high-impact actions—such as requesting sensitive PII datasets, modifying operational records, or exporting confidential financial reports—fully autonomous execution introduces unacceptable compliance risks. F-AI-05 enforces Human-in-the-Loop (HitL) Step-Up Approval: sensitive queries or tool executions pause automatically, generating a step-up challenge requiring explicit authorization by a human supervisor or data owner via Slack, Teams, or ITSM.

---

## 2. Business Value

- **Regulatory Compliance**: Satisfies EU AI Act Article 14 (human oversight for high-risk AI systems) and GDPR Art. 9 safeguards.
- **Risk Mitigation**: Prevents unintended data leaks or unauthorized bulk mutations initiated by autonomous agents.
- **Seamless Agent Resumption**: Employs long-polling or webhook callbacks allowing the agent to suspend and resume without losing session context.

---

## 3. Architecture & Capabilities

- Policy-driven triggers based on table sensitivity (`HIGH`, `RESTRICTED`), row count thresholds, or specific GraphQL mutations.
- Real-time notification dispatch via webhook to approval portals (Slack, Microsoft Teams, ServiceNow).
- Cryptographic challenge-token verification upon human approval.

---

## 4. Usage Example

```bash
# Agent calls a sensitive data retrieval tool
curl -X POST http://localhost:8080/mcp \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <agent-token>" \
  -d '{
    "jsonrpc": "2.0",
    "id": 4,
    "method": "tools/call",
    "params": {
      "name": "export_customer_credit_profiles",
      "arguments": { "region": "EMEA" }
    }
  }'

# Response indicating Step-Up challenge generated:
# {
#   "jsonrpc": "2.0",
#   "id": 4,
#   "result": {
#     "status": "STEP_UP_REQUIRED",
#     "challengeId": "chal-789a-4c21",
#     "message": "Human approval required for sensitive financial dataset. Notification dispatched to Data Owner.",
#     "pollingEndpoint": "/api/v1/approvals/chal-789a-4c21/status"
#   }
# }
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "Mcp": {
      "StepUpApproval": {
        "Enabled": true,
        "ChallengeTtlMinutes": 15,
        "RequireHumanApprovalForHighSensitivity": true,
        "NotificationWebhookUrl": "https://itsm.corp.local/api/ai-approvals"
      }
    }
  }
}
```
