# F-DOC-01: Omnichannel Semantic Documentation Passthrough

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`IOmnichannelDocService.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Documentation/IOmnichannelDocService.cs), [`OmnichannelDocumentationService.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Documentation/OmnichannelDocumentationService.cs)

---

## 1. Overview & Problem Statement

Documentation entered by data engineers in dbt model doc-blocks or catalog stewards in Microsoft Purview is traditionally lost when data is exposed through GraphQL or REST. F-DOC-01 implements lossless, omnichannel documentation passthrough: rich markdown definitions, column descriptions, and business glossary terms are automatically propagated across all gateway protocols—GraphQL Banana Cake Pop tooltips, OpenAPI Swagger descriptions, OData CSDL annotations, and MCP tool signatures.

---

## 2. Business Value

- **Write Once, Document Everywhere**: Eliminates redundant documentation maintenance across API portals, BI catalogs, and data warehouses.
- **Empowered Data Consumers**: Developers and analysts instantly understand business terms, calculation logic, and caveats directly within their IDEs and query explorers.
- **Higher AI Tool Accuracy**: AI agents receive rich semantic explanations in tool descriptions, drastically cutting query mistakes.

---

## 3. Architecture & Capabilities

- Ingests markdown descriptions from dbt `manifest.json`, Purview, Collibra, and OpenMetadata.
- Preserves markdown formatting in GraphQL schema descriptions (`@GraphQLDescription`).
- Injects doc-strings into OpenAPI schemas and OData `$metadata` XML documentation tags.

---

## 4. Usage Example

```graphql
# Introspect field descriptions in GraphQL schema (e.g. populated from dbt doc-blocks)
query IntrospectCustomerFields {
  __type(name: "Customer") {
    fields {
      name
      description # Contains rich dbt doc-block explanation & business grain
    }
  }
}
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "Documentation": {
      "Enabled": true,
      "PassthroughDbtDocs": true,
      "PassthroughCatalogGlossary": true,
      "FormatAsMarkdown": true
    }
  }
}
```
