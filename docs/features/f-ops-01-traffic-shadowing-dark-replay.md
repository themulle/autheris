# F-OPS-01: AST-Aware Production Traffic Shadowing & Dark Replay

**Status:** [Done] (100% GA – Wave 2)  
**Components:** [`TrafficShadowingMiddleware.cs`](file:///root/autheris/src/Autheris.Api/Middleware/TrafficShadowingMiddleware.cs), [`ITrafficShadowEngine.cs`](file:///root/autheris/src/Autheris.Application/Operations/ITrafficShadowEngine.cs)

---

## 1. Overview & Problem Statement

Deploying new gateway releases, schema updates, or database optimizations without testing real-world production traffic risks performance degradation or subtle bugs. F-OPS-01 provides AST-aware production traffic shadowing: live client requests are cloned asynchronously and replayed against a staging/canary cluster ('Dark Replay') without delaying the caller or duplicating side-effects (mutations are filtered out).

---

## 2. Business Value

- **Zero-Risk Upgrades**: Validate performance, query plans, and accuracy against 100% real-world production traffic before cutting over.
- **Zero Impact on Production Latency**: Shadow requests are dispatched in background worker threads without adding latency to live client responses.
- **Mutation Safety**: AST inspection strictly discards all GraphQL mutations and SQL DML statements, ensuring staging environments are never corrupted.

---

## 3. Architecture & Capabilities

- Configurable traffic sampling percentage (e.g. 5%, 25%, 100%).
- AST filtering discarding any mutation, DML, or non-idempotent operation.
- Diff reporting comparing latency and response hash between production and shadow targets.

---

## 4. Usage Example

```bash
# Traffic shadowing is handled transparently in the background.
# When a client queries production:
curl -X POST http://gateway-prod.corp.local/graphql \
  -H "Authorization: Bearer <user-token>" \
  -d '{"query": "{ inventory { sku stockLevel } }"}'

# The gateway asynchronously duplicates the request to the staging cluster:
# POST http://gateway-staging.corp.local/graphql
# Header: X-Autheris-Shadow: true
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "TrafficShadowing": {
      "Enabled": true,
      "ShadowEndpoint": "http://gateway-canary.internal.corp:8080/graphql",
      "SampleRatePercent": 10,
      "IgnoreMutations": true,
      "TimeoutMs": 2000
    }
  }
}
```
