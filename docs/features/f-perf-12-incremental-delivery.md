# F-PERF-12: Incremental Delivery via @defer & @stream

**Status:** [Done] (100% GA – Wave 2)  
**Components:** [`IncrementalDeliveryMiddleware.cs`](file:///root/lis-git/autheris/src/Autheris.Api/Middleware/IncrementalDeliveryMiddleware.cs), [`IncrementalResponseFormatter.cs`](file:///root/lis-git/autheris/src/Autheris.GraphQL/Execution/IncrementalResponseFormatter.cs)

---

## 1. Overview & Problem Statement

When a GraphQL query requests both fast critical data (e.g. user profile) and slow non-critical data (e.g. historical billing analytics or recommendations), the user is forced to wait for the slowest resolver before receiving anything. F-PERF-12 implements the GraphQL Incremental Delivery specification (`@defer` and `@stream`) over multipart HTTP/2 responses. Fast fields are delivered immediately, while deferred components stream in as background resolvers finish.

---

## 2. Business Value

- **Instant User Interface Interactivity**: Critical UI components render in milliseconds without waiting for slow background queries.
- **Reduced Perceived Latency**: Mobile and web users see immediate visual feedback, boosting customer engagement and conversion rates.
- **Governed Incremental Chunks**: Deferred chunks pass through standard RLS and masking rules prior to streaming.

---

## 3. Architecture & Capabilities

- Standards-compliant `@defer` and `@stream` execution engine.
- Multipart HTTP response streaming (`multipart/mixed; boundary="-"`).
- Transparent error isolation: failure of a deferred field does not break the initial critical payload.

---

## 4. Usage Example

```graphql
# Query with deferred heavy sub-tree
query GetDashboard {
  user {
    id
    name
  }
  ... @defer(label: "heavyAnalytics") {
    annualAnalytics {
      totalRevenue
      quarterlyBreakdown
    }
  }
}
```

```bash
# Client receives multipart stream:
# Initial chunk delivered immediately:
# {"data":{"user":{"id":"1","name":"Alice"}},"hasNext":true}
# Subsequent chunk delivered when ready:
# {"hasNext":false,"incremental":[{"data":{"annualAnalytics":{...}},"label":"heavyAnalytics"}]}
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "IncrementalDelivery": {
      "Enabled": true,
      "MaxDeferredFieldsPerQuery": 5,
      "StreamChunkTimeoutSeconds": 30
    }
  }
}
```
