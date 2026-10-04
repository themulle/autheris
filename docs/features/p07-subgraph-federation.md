# P7: Subgraph Federation Router (Hot Chocolate Fusion)

**Status:** [Done] (100% GA – Core Foundation)  
**Components:** [`FusionGatewayExtensions.cs`](file:///root/lis-git/autheris/src/Autheris.GraphQL/Federation/FusionGatewayExtensions.cs), [`SubgraphSecurityDelegatingHandler.cs`](file:///root/lis-git/autheris/src/Autheris.GraphQL/Federation/SubgraphSecurityDelegatingHandler.cs), [`SubgraphResultMaskingMiddleware.cs`](file:///root/lis-git/autheris/src/Autheris.GraphQL/Federation/SubgraphResultMaskingMiddleware.cs)

---

## 1. Overview & Problem Statement

Microservice architectures often split data across federated GraphQL subgraphs. Existing federation routers (e.g. Apollo Router) either lack deep column-level Zero-Trust masking or force expensive enterprise licensing. P7 integrates the Hot Chocolate Fusion 16.6.7 router, composing distributed subgraphs into a unified supergraph while enforcing Zero-Trust token propagation and in-memory result masking on aggregated multi-subgraph responses.

---

## 2. Business Value

- **No Vendor Lock-in & Lower TCO**: 100% open .NET architecture without Apollo GraphOS contract fees or ELv2 license restrictions.
- **Unified Supergraph with Zero Trust**: Subgraphs can be treated as untrusted; the gateway scrubs and masks sensitive fields after federation merging.
- **Seamless Identity Forwarding**: Propagates authenticated caller SIDs and security groups securely to downstream microservices.

---

## 3. Architecture & Capabilities

- Distributed supergraph schema packaging (`fgp` fusion gateway packages).
- Secure HTTP delegating handler injecting tenant and user authorization headers.
- In-memory result masking and AST side-channel defense on merged federation outputs.

---

## 4. Usage Example

```graphql
# Federated query executing across OrderSubGraph and CustomerSubGraph seamlessly
query GetCustomerOrderSummary {
  customerById(id: "CUST-992") {
    name
    email # Masked according to caller consent
    orders { # Resolved from distributed OrderSubGraph
      orderId
      totalAmount
      status
    }
  }
}
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "Fusion": {
      "Enabled": true,
      "PackagePath": "gateway.fgp",
      "Subgraphs": {
        "Customers": { "BaseUrl": "https://customers.internal.corp/graphql" },
        "Orders": { "BaseUrl": "https://orders.internal.corp/graphql" }
      },
      "PropagateAuthHeader": true
    }
  }
}
```
