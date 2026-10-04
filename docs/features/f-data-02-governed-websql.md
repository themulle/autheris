# F-DATA-02: Governed WebSQL Engine

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`WebSqlEndpoints.cs`](file:///root/lis-git/autheris/src/Autheris.Api/Endpoints/WebSqlEndpoints.cs), [`TrinoSqlEngine.cs`](file:///root/lis-git/autheris/src/TrinoSqlEngine/TrinoSqlEngine.cs), [`WebSqlSecurityFilter.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Sql/WebSqlSecurityFilter.cs)

---

## 1. Overview & Problem Statement

Direct database connections over proprietary ports (1433 for MSSQL, 5432 for Postgres) require broad firewall access and expose databases to uncontrolled connection exhaustion. F-DATA-02 introduces a governed HTTP-based WebSQL engine (`POST /api/v1/sql`) modeled after Trino/Presto. Ad-hoc SQL queries are parsed using an ANTLR4 parser, checked against AST security rules, injected with Casbin ABAC and tenant RLS filters, and executed safely over standard HTTPS.

---

## 2. Business Value

- **Zero Exposed Database Ports**: Operational databases remain isolated within private subnets; all analytics traffic passes through standard HTTPS (port 443).
- **Deep AST SQL Injection Protection**: Prevents multi-statement attacks, system function execution, and comment-based evasion.
- **Controlled DML Guardrails**: High-risk statements (`UPDATE`, `DELETE`) require special writer roles, are bounded by max affected row limits, and are logged to the cryptographic WORM audit chain.

---

## 3. Architecture & Capabilities

- ANTLR4 AST parsing and rewriting with strict read-only default semantics.
- Dynamic injection of RLS clauses (`WHERE ... AND tenant_id = '...'`) directly into the AST.
- Protection against unfiltered DML (`RejectUnfilteredDml = true`) and mandatory transactional rollbacks on quota exceedance.

---

## 4. Usage Example

```bash
# Execute governed ad-hoc SQL query over HTTP
curl -X POST http://localhost:8080/api/v1/sql \
  -H "Authorization: Bearer <user-token>" \
  -H "Content-Type: application/json" \
  -d '{
    "sql": "SELECT order_id, customer_id, total_amount FROM sales.orders WHERE order_date >= '''2026-01-01''' ORDER BY total_amount DESC LIMIT 10"
  }'

# Response:
# {
#   "columns": ["order_id", "customer_id", "total_amount"],
#   "rows": [
#     ["ORD-1001", "CUST-42", 1450.00],
#     ["ORD-1002", "CUST-88", 980.50]
#   ],
#   "rowCount": 2
# }
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "WebSql": {
      "Enabled": true,
      "MaxResultRows": 5000,
      "AllowDml": false,
      "DmlWriterRoles": ["DatabaseOperator"],
      "MaxAffectedRows": 1000,
      "RejectUnfilteredDml": true,
      "ExecutionTimeoutSeconds": 30
    }
  }
}
```
