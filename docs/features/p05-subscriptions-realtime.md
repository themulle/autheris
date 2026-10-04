# P5: Subscriptions, Realtime Events & In-Stream RLS

**Status:** [Done] (100% GA – Core Foundation)  
**Components:** [`Subscription.cs`](file:///root/lis-git/autheris/src/Autheris.GraphQL/Subscriptions/Subscription.cs), [`StreamRlsPolicyEnforcer.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Streaming/Services/StreamRlsPolicyEnforcer.cs), [`WebSocketAuthInterceptor.cs`](file:///root/lis-git/autheris/src/Autheris.GraphQL/Interceptors/WebSocketAuthInterceptor.cs)

---

## 1. Overview & Problem Statement

Real-time subscriptions in enterprise applications present a severe security risk: if permissions change while a stream is open, or if an event contains mixed-tenant data, unauthorized data will leak to the client. P5 implements full WebSocket (`graphql-transport-ws`) and Server-Sent Events (SSE) support with dynamic, per-event In-Stream Row-Level Security. Every emitted event is evaluated against the subscriber's active Casbin ABAC permissions and masked in real time.

---

## 2. Business Value

- **Real-Time Data Visibility without Leakage**: Modern reactive dashboards (Power BI streaming, React frontends) receive live updates without violating Zero-Trust.
- **Instant Revocation Handling**: If a user's consent is revoked mid-stream, the policy enforcer instantly drops subsequent events or closes the subscription.
- **Multi-Transport Flexibility**: Supports modern WebSocket protocols as well as firewall-friendly Server-Sent Events (SSE).

---

## 3. Architecture & Capabilities

- Authenticated WebSocket connection initialization (`connection_init`) via JWT Bearer or session cookie.
- Dynamic Casbin ABAC evaluation on each individual event in the stream.
- Per-subscriber column masking applied to event payloads before serialization.

---

## 4. Usage Example

```bash
# Connect via wscat to test GraphQL subscription over WebSocket
wscat -c ws://localhost:8080/graphql -s graphql-transport-ws

# Send connection init with Bearer token
> {"type":"connection_init","payload":{"Authorization":"Bearer <token>"}}
< {"type":"connection_ack"}

# Start subscription
> {"id":"sub-1","type":"subscribe","payload":{"query":"subscription { onInventoryAlert { sku warehouse quantity } }"}}
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "Subscriptions": {
      "Enabled": true,
      "KeepAliveIntervalSeconds": 15,
      "ExecutionTimeoutSeconds": 60,
      "MaxActiveSubscriptionsPerUser": 25,
      "EnableInStreamRls": true
    }
  }
}
```
