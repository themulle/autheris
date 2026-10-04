# F-GOV-08: Dynamic Schema Contracts & Tag-Based Projection (@tag)

**Status:** [Done] (100% GA – Wave 2)  
**Components:** [`SchemaContractMiddleware.cs`](file:///root/lis-git/autheris/src/Autheris.Api/Middleware/SchemaContractMiddleware.cs), [`DynamicTagProjectionService.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Catalog/DynamicTagProjectionService.cs)

---

## 1. Overview & Problem Statement

Serving different consumer tiers (e.g. Public Mobile Apps, Internal Microservices, B2B Partners) from a single GraphQL schema often leads to accidental exposure of internal or unstable fields. F-GOV-08 introduces Dynamic Schema Contracts using `@tag` directives. Fields and types are annotated with contract tags (`@tag(name: "public")`, `@tag(name: "partner")`), allowing the gateway to dynamically prune the schema projection based on client API keys and authenticated tiers.

---

## 2. Business Value

- **Single Schema, Multiple Contract Views**: Eliminate the need to maintain duplicate gateways or proxy layers for internal vs. external audiences.
- **Safe API Evolution**: Mark new fields with `@tag(name: "beta")` and restrict exposure to early-access partners before general availability.
- **Automated Client Isolation**: External partners cannot introspect or access internal operational fields.

---

## 3. Architecture & Capabilities

- Apollo-compatible `@tag(name: "...")` schema directive support.
- Dynamic AST filtering pruning unpermitted types, fields, and arguments from introspection.
- Client tier resolution via JWT claims or API key metadata.

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
