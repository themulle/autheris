# F-AI-08: FOCUS FinOps Accounting for Token & Compute

**Status:** [Done] (100% GA – Wave 2)  
**Components:** [`IFocusCostAccountingService.cs`](file:///root/lis-git/autheris/src/Autheris.Application/FinOps/Interfaces/IFocusCostAccountingService.cs), [`FocusCostAccountingService.cs`](file:///root/lis-git/autheris/src/Autheris.Application/FinOps/Services/FocusCostAccountingService.cs)

---

## 1. Overview & Problem Statement

Enterprise GenAI adoption creates uncontrolled compute costs across databases, lakehouse engines, and model inference providers. F-AI-08 implements continuous telemetry and cost allocation strictly compliant with the FinOps Open Cost and Usage Specification (FOCUS 1.0). Every query, execution plan, and MCP tool call tracks token consumption, bytes scanned, and execution duration, attributing costs to cost centers, teams, and projects.

---

## 2. Business Value

- **Precise AI Chargeback & Showback**: Allocate AI compute and database query expenses accurately across business units.
- **Budget Enforcement**: Hard and soft spending caps automatically throttle or reject queries when an agent or department exceeds its monthly FinOps budget.
- **Standardized Reporting**: Native FOCUS 1.0 CSV/JSON export ready for ingestion into enterprise ERP and FinOps platforms (Apptio, CloudZero, Kubecost).

---

## 3. Architecture & Capabilities

- Real-time aggregation of input/output tokens, DB execution milliseconds, and lakehouse scan volumes.
- Budget quota checks with automatic HTTP 429 / MCP Resource Exhaustion enforcement.
- FOCUS 1.0 schema compliance (`BilledCost`, `EffectiveCost`, `ChargeCategory`, `SubAccountId`).

---

## 4. Usage Example

```bash
# Query active department FinOps consumption
curl -X GET "http://localhost:8080/api/v1/finops/consumption?costCenter=CC-FINANCE-01&period=2026-10" \
  -H "Authorization: Bearer <finops-admin-token>"

# Response:
# {
#   "costCenter": "CC-FINANCE-01",
#   "period": "2026-10",
#   "totalQueries": 14205,
#   "inputTokensConsumed": 3820000,
#   "outputTokensConsumed": 940000,
#   "databaseBytesScanned": 104857600000,
#   "allocatedCostUsd": 248.50,
#   "budgetCapUsd": 500.00,
#   "status": "WITHIN_BUDGET"
# }
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "FinOps": {
      "Enabled": true,
      "AccountingStandard": "FOCUS_1_0",
      "Currency": "USD",
      "CostPerMillionInputTokens": 1.50,
      "CostPerMillionOutputTokens": 6.00,
      "CostPerGigabyteScanned": 0.02,
      "EnforceHardBudgetLimits": true
    }
  }
}
```
