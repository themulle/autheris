# F-AI-04: Pre-Flight Query Simulator & Safety Limits

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`IPreFlightQuerySimulator.cs`](file:///root/autheris/src/Autheris.Application/Mcp/Interfaces/IPreFlightQuerySimulator.cs), [`McpPreflightSimulator.cs`](file:///root/autheris/src/Autheris.Application/Mcp/Services/McpPreflightSimulator.cs)

---

## 1. Overview & Problem Statement

Unconstrained AI agents can unintentionally issue runaway queries—such as accidental Cartesian products, unpartitioned multi-terabyte table scans, or deep nested GraphQL traversals—causing database resource exhaustion. F-AI-04 introduces a zero-execution Pre-Flight Simulator that parses ASTs, evaluates complexity budgets, estimates row count ceilings, and verifies authorization before any physical query is dispatched to backend engines.

---

## 2. Business Value

- **Denial-of-Service (DoS) Protection**: Protects operational databases from runaway agent-generated queries.
- **Cost Governance**: Prevents unexpected cloud warehouse bills (Snowflake/Databricks) by enforcing cost ceilings per query and per session.
- **Proactive Agent Feedback**: Returns actionable error payloads with specific remediation hints, enabling agents to self-correct their query structure.

---

## 3. Architecture & Capabilities

- GraphQL AST complexity and depth analysis with configurable scoring algorithms.
- SQL AST partition-pruning validation (rejecting unindexed table scans on large tables).
- Pre-execution Casbin ABAC and consent verification returning early fail-closed rejections.

---

## 4. Usage Example

```bash
# Simulate a query execution before dispatching
curl -X POST http://localhost:8080/mcp \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <agent-token>" \
  -d '{
    "jsonrpc": "2.0",
    "id": 3,
    "method": "tools/call",
    "params": {
      "name": "simulate_query",
      "arguments": {
        "query": "{ customers { orders { lineItems { product { inventory { warehouse { id } } } } } } }"
      }
    }
  }'

# Response rejecting excessive complexity:
# {
#   "jsonrpc": "2.0",
#   "id": 3,
#   "result": {
#     "allowed": false,
#     "reason": "Query complexity 3200 exceeds maximum allowable limit of 1500.",
#     "estimatedCost": "HIGH",
#     "suggestedFix": "Reduce nesting depth or apply limit filters to child collections."
#   }
# }
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "Mcp": {
      "PreFlightSimulation": {
        "Enabled": true,
        "MaxComplexityBudget": 1500,
        "MaxExecutionDepth": 8,
        "RejectUnfilteredScans": true,
        "MaxEstimatedRows": 500000
      }
    }
  }
}
```
