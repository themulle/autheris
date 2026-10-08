# F-GOV-06: Multi-Stage Pushdown Cascades & Cross-Domain Joins

**Status:** [Not implemented] – The `CrossDomainJoinEngine` prototype was never wired to an endpoint and was removed (Architecture 3, 2026-10-08). Joins across data sources run through the DuckDB OLAP endpoint ([F-DATA-03](f-data-03-duckdb-olap.md)).  
**Components:** none. The rest of this document describes the original target design.

---

## 1. Overview & Problem Statement

Enterprises store interrelated data across disparate database engines—such as customers in Oracle, orders in SQL Server, and shipment records in PostgreSQL. Performing cross-domain joins traditionally required moving entire datasets into central warehouses. F-GOV-06 executes multi-stage pushdown cascades: individual database predicates and RLS filters are pushed down to source engines first, and the resulting pruned record sets are joined in-memory with sub-millisecond efficiency.

---

## 2. Business Value

- **Breaks Down Enterprise Data Silos**: Enables unified GraphQL queries spanning Oracle, SQL Server, and PostgreSQL without data replication.
- **Minimal Network and Database Load**: Only the minimal, pre-filtered subset of rows is retrieved from each database engine.
- **Zero-Trust Across Engine Boundaries**: Each sub-query maintains independent tenant isolation and consent evaluation.

---

## 3. Architecture & Capabilities

- Multi-dialect query generation (Oracle, T-SQL, PL/pgSQL, SQLite).
- DataLoader-based chunked hydration with database parameter limit protection.
- In-memory hash-joins on indexed primary and composite keys.

---

## 4. Usage Example

```graphql
# Cross-domain query joining Postgres (Customers) and SQL Server (Orders)
query GetCrossDomainCustomerOrders {
  customerById(id: "CUST-100") { # Executed on Postgres
    name
    company
    orders { # Resolved from SQL Server with RLS pushdown
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
    "CrossDomainJoins": {
      "Enabled": true,
      "MaxJoinConcurrency": 10,
      "MaxBufferRowsPerSubquery": 50000,
      "DefaultExecutionStrategy": "ParallelDataLoader"
    }
  }
}
```
