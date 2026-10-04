# F-AI-06: Explainable AI & Provenance Footnotes

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`IMcpProvenanceEnricher.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Mcp/Interfaces/IMcpProvenanceEnricher.cs), [`McpProvenanceEnricher.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Mcp/Services/McpProvenanceEnricher.cs)

---

## 1. Overview & Problem Statement

Auditors and business stakeholders cannot trust AI-generated answers without verifiable proof of origin. F-AI-06 transparently enriches every MCP tool response with cryptographic provenance metadata and lineage footnoting: originating data source, exact table and column IDs, active governance consent ID, timestamp, and WORM audit hash chain reference.

---

## 2. Business Value

- **Complete Auditability**: Verifiable lineage connecting every AI assertion directly back to source database records.
- **Explainability for Compliance**: Meets EU AI Act and GDPR Article 13 transparency requirements for automated decision systems.
- **Hallucination Detection**: End users can cross-reference footnotes against raw backend systems to verify data fidelity.

---

## 3. Architecture & Capabilities

- Automatic appending of `_provenance` metadata to all MCP tool execution results.
- Links response values to upstream dbt models, database connection IDs, and catalog terms.
- Includes HMAC-SHA256 signature validating the authenticity of the returned record batch.

---

## 4. Usage Example

```bash
# Agent queries customer churn risk
curl -X POST http://localhost:8080/mcp \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <agent-token>" \
  -d '{
    "jsonrpc": "2.0",
    "id": 5,
    "method": "tools/call",
    "params": {
      "name": "get_customer_churn_score",
      "arguments": { "customerId": "CUST-1049" }
    }
  }'

# Response contains enriched provenance footnotes:
# {
#   "churnScore": 0.84,
#   "_provenance": {
#     "dataSource": "crm.sqlserver.corporate",
#     "table": "dbo.customer_metrics",
#     "consentId": "c841-e940-11ef",
#     "retrievedAt": "2026-10-04T09:30:00Z",
#     "auditRecordHash": "a4f9e1...5b2",
#     "upstreamDbtModel": "models/marts/crm/dim_customer_health"
#   }
# }
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "Mcp": {
      "ProvenanceFootnoting": {
        "Enabled": true,
        "IncludeConsentId": true,
        "IncludeAuditChainHash": true,
        "IncludeUpstreamDbtModel": true
      }
    }
  }
}
```
