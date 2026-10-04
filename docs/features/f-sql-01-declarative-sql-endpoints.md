# F-SQL-01: Declarative SQL-to-API Engine & Auto-OpenAPI

**Status:** [Done] (100% GA – Wave 2)  
**Components:** [`SqlEndpointLoader.cs`](file:///root/lis-git/autheris/src/Autheris.Application/SqlEndpoints/Services/SqlEndpointLoader.cs), [`InMemorySqlEndpointRegistry.cs`](file:///root/lis-git/autheris/src/Autheris.Application/SqlEndpoints/Services/InMemorySqlEndpointRegistry.cs), [`SqlEndpointRoutes.cs`](file:///root/lis-git/autheris/src/Autheris.Api/Endpoints/SqlEndpointRoutes.cs)

---

## 1. Overview & Problem Statement

Data engineering teams have established vast repositories of optimized, battle-tested SQL queries in Git. Requiring developers to write custom C# or Python controllers to expose these queries as REST endpoints creates friction and delay. F-SQL-01 exposes versioned `.sql` files (`queries/*.sql`) directly as typed REST endpoints (`GET` / `POST /api/v1/queries/{name}`) with automatic parameter extraction (`@param`), type inference, and dynamic OpenAPI 3.0 generation (`/api/v1/queries/openapi.json`), all safeguarded by the central Zero-Trust engine.

---

## 2. Business Value

- **Zero-Boilerplate API Creation**: Turn any approved `.sql` file into a secure, production-ready REST API in seconds without writing code.
- **GitOps Workflow for Data APIs**: Manage data APIs through pull requests and version-controlled SQL files with automated CI testing.
- **Interactive Documentation**: Auto-generates complete OpenAPI 3.0 specs ready for testing in Swagger UI.

---

## 3. Architecture & Capabilities

- Automatic detection of parameters using native `@param` or mustache `{{param}}` syntax.
- Ingestion of SQL doc-comments (`-- @summary`, `-- @param description`) into OpenAPI endpoint metadata.
- Runtime AST injection of tenant isolation and row-level security filters.

---

## 4. Usage Example

```sql
-- File: queries/get_monthly_sales.sql
-- @summary Returns monthly aggregate sales per department
-- @param department Department code to filter by (e.g. DEPT-10)
-- @param min_amount Minimum transaction amount threshold
SELECT department, SUM(amount) AS total_sales, COUNT(*) AS transaction_count
FROM sales.transactions
WHERE department = @department AND amount >= @min_amount
GROUP BY department;
```

```bash
# Execute the generated REST endpoint:
curl -X GET "http://localhost:8080/api/v1/queries/get_monthly_sales?department=DEPT-10&min_amount=100.00" \
  -H "Authorization: Bearer <user-token>"
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "SqlEndpoints": {
      "Enabled": true,
      "QueriesDirectory": "queries",
      "AutoReloadOnChange": true,
      "RoutePrefix": "/api/v1/queries",
      "ExposeOpenApi": true
    }
  }
}
```
