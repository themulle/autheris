# F-GOV-08: Dynamic Schema Contracts & Tag-Based Projection (@tag)

**Status:** [Done] (100% GA – Wave 2)  
**Components:** [`SchemaContractMiddleware.cs`](file:///root/autheris/src/Autheris.Api/Middleware/SchemaContractMiddleware.cs), [`DynamicTagProjectionService.cs`](file:///root/autheris/src/Autheris.Application/Catalog/DynamicTagProjectionService.cs)

---

## 1. Overview & Problem Statement

Serving different consumer tiers (e.g. Public Mobile Apps, Internal Microservices, B2B Partners) from a single GraphQL schema often leads to accidental exposure of internal or unstable fields. F-GOV-08 introduces Dynamic Schema Contracts using `@tag` directives. Fields and types are annotated with contract tags (`@tag(name: "public")`, `@tag(name: "partner")`), allowing the gateway to dynamically prune the schema projection based on client API keys and authenticated tiers.

---

## 2. Business Value

- **Single Schema, Multiple Contract Views**: Eliminate the need to maintain duplicate gateways or proxy layers for internal vs. external audiences.
- **Safe API Evolution**: Mark new fields with `@tag(name: "beta")` and restrict exposure to early-access partners before general availability.
- **Client Views**: Partners see the schema slice of their contract; data access itself is governed by consents and policies.

---

## 3. Architecture & Capabilities

- Apollo-compatible `@tag(name: "...")` schema directive support.
- Dynamic AST filtering pruning unpermitted types, fields, and arguments from introspection.
- Client tier resolution via JWT claims or API key metadata.

### Security scope (API-3)

A schema contract selects a **view** of the schema; it is not an access control. Which tables, columns and rows a
caller may read is decided by consents, Casbin, ReBAC and row filters on every path, independent of the contract.

Contract selection: a `contract` claim of the authenticated identity takes precedence. `X-Gateway-Contract` or
`?contract=` may only select a contract when the identity carries none; a request that names a contract different from
the claim is rejected with 403. Unknown contracts are rejected with 400.

---

## 4. Usage Example

```graphql
# Schema definition with contract tags
type Customer {
  id: ID!
  displayName: String! @tag(name: "public")
  internalRiskRating: Int! @tag(name: "internal")
  creditScore: Float! @tag(name: "finance")
}

# A public mobile client querying the schema will see only:
# type Customer { id: ID! displayName: String! }
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "SchemaContracts": {
      "Enabled": true,
      "DefaultTag": "public",
      "TierTagMap": {
        "PartnerTier": ["public", "partner"],
        "InternalTier": ["public", "partner", "internal", "finance"]
      }
    }
  }
}
```
