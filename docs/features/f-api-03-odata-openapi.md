# F-API-03: Dual-Access Exposure (OData v4 & Dynamic OpenAPI 3.1)

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`ODataEndpoints.cs`](file:///root/lis-git/autheris/src/Autheris.Api/Endpoints/ODataEndpoints.cs), [`IODataHandler.cs`](file:///root/lis-git/autheris/src/Autheris.Application/OData/IODataHandler.cs), [`ODataCsdlGenerator.cs`](file:///root/lis-git/autheris/src/Autheris.Application/OData/ODataCsdlGenerator.cs)

---

## 1. Overview & Problem Statement

Modern frontend web applications demand GraphQL, whereas enterprise BI tools (such as Microsoft Power BI, Excel, SAP Analytics Cloud, and Tableau) and legacy B2B partners natively rely on OData v4 and REST. F-API-03 exposes every governed table through standardized OData v4 endpoints (`/odata/v4/{domain}/{schema}/{table}`) complete with dynamic CSDL `$metadata` and auto-generated OpenAPI 3.1 Swagger specifications (`/odata/v4/$openapi`), all governed by the same Zero-Trust consent and RLS engine.

---

## 2. Business Value

- **Zero-Code Enterprise BI Integration**: Direct, seamless import into Power BI, Excel, and SAP without requiring custom ODBC/JDBC drivers or separate data marts.
- **Unified Single Source of Truth**: Eliminates disparate governance rules between GraphQL apps and BI spreadsheets; column masking and RLS apply identically across all protocols.
- **Broad Developer Ecosystem**: Allows legacy REST clients to integrate via OpenAPI 3.1 definitions while modern clients use GraphQL.

---

## 3. Architecture & Capabilities

- Dynamic generation of OData v4 CSDL XML metadata ($metadata) reflecting live governance schemas.
- Full support for standard OData query options: `$select`, `$filter`, `$top`, `$skip`, and `$orderby`.
- OpenAPI 3.1 schema projection with embedded documentation and data types.

---

## 4. Usage Example

```bash
# Query governed entity set via OData v4 with filtering and projection
curl -X GET "http://localhost:8080/odata/v4/sales/dbo/orders?\$select=orderId,orderDate,totalAmount&\$filter=totalAmount gt 500&\$top=5" \
  -H "Authorization: Bearer <user-token>" \
  -H "Accept: application/json"

# Retrieve CSDL XML metadata for Power BI / Excel import
curl -X GET "http://localhost:8080/odata/v4/\$metadata" \
  -H "Authorization: Bearer <user-token>"
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "OData": {
      "Enabled": true,
      "RoutePrefix": "/odata/v4",
      "MaxPageSize": 1000,
      "EnableOpenApiSpec": true,
      "ExposeMetadataEndpoint": true
    }
  }
}
```
