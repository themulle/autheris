# F-SEC-04: Relationship-Based Access Control (ReBAC via OpenFGA / Zanzibar)

**Status:** [Done] (100% GA – Next-Gen)  
**Components:** [`RebacEndpoints.cs`](file:///root/autheris/src/Autheris.Api/Endpoints/RebacEndpoints.cs), [`IRebacAuthorizationService.cs`](file:///root/autheris/src/Autheris.Application/Security/IRebacAuthorizationService.cs), [`OpenFgaRebacClient.cs`](file:///root/autheris/src/Autheris.Infrastructure/Security/OpenFgaRebacClient.cs)

---

## 1. Overview & Problem Statement

Traditional Role-Based Access Control (RBAC) and Attribute-Based Access Control (ABAC) struggle with complex organizational hierarchies, nested resource ownership, and delegated tenant relationships (e.g. 'can user X edit document Y because they are an editor of team Z?'). F-SEC-04 introduces fine-grained Relationship-Based Access Control (ReBAC) modeled after Google Zanzibar and OpenFGA. Relationships are resolved in sub-millisecond memory structures before authorization.

---

## 2. Business Value

- **Intuitive Fine-Grained Authorization**: Express hierarchical, multi-tenant permissions easily (e.g., manager-of, member-of, owner-of).
- **High-Performance ReBAC Resolution**: In-memory caching and batch relationship checks ensure authorization latency stays under 1 ms.
- **Zero Privilege Escalation**: Prevents unauthorized lateral access across complex enterprise organizations.

---

## 3. Architecture & Capabilities

- High-throughput OpenFGA client with local L1 relationship caching.
- Support for transitive relationship resolution (tuples like `user:alice is member of group:finance`).
- Transparent integration into GraphQL query resolver authorization filters.

### Where ReBAC is enforced (POL-6)

Every path uses the same object id for a catalog table: `table:<domain>.<schema>.<table>` (for example
`table:sales.public.orders`). Table access is checked with the relation `can_query`, streaming with `subscriber`. The
evaluator denies a table for which no tuple grants the relation. All paths take this decision in one place
(`TableAccessPolicy`, together with consents and Casbin).

> **Migration (2026-10):** Earlier versions used `table:<domain>.<table>` (unified PDP, MCP) and `table:<schema>.<table>`
> (DuckDB OLAP, streaming). Rewrite existing `can_query` and `subscriber` tuples to `table:<domain>.<schema>.<table>`;
> tuples in the old formats no longer match.

| Path | Checked when |
|---|---|
| MCP-RAG, DuckDB OLAP (unified PDP) | `Rebac.Enabled = true` (default) |
| Streaming subscriptions (relation `subscriber`) | `Rebac.Enabled` and `Rebac.EnforceOnStreaming` |
| OData, GraphQL, WebSQL, stored procedures | `Rebac.Enabled` and `Rebac.EnforceOnQueryPaths` (default `false`) |

Enable `EnforceOnQueryPaths` only once `can_query` tuples are maintained for every table; otherwise every query on
these paths is denied. Consent, Casbin and row filters apply in addition, ReBAC only ever restricts.

---

## 4. Usage Example

```bash
# Verify user relationship against a document entity
curl -X POST http://localhost:8080/api/v1/rebac/check \
  -H "Authorization: Bearer <user-token>" \
  -H "Content-Type: application/json" \
  -d '{
    "user": "user:S-1-5-21-CONSUMER-1",
    "relation": "can_view",
    "object": "document:financial_report_2026_q3"
  }'

# Response:
# { "allowed": true, "resolutionLatencyMs": 0.45 }
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "Rebac": {
      "Enabled": true,
      "EnforceOnQueryPaths": false,
      "Provider": "OpenFga",
      "OpenFga": {
        "ApiUrl": "http://openfga.internal.corp:8080",
        "StoreId": "01H8Z4...",
        "ModelId": "01H8Z5...",
        "CacheTtlSeconds": 300
      }
    }
  }
}
```
