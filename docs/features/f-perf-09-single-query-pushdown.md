# F-PERF-09: GraphQL-to-SQL AST Single-Query Compiler

**Status:** [Done] (100% GA – Wave 2)  
**Components:** [`ISingleQueryAstCompiler.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Sql/ISingleQueryAstCompiler.cs), [`SingleQueryAstCompiler.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Sql/SingleQueryAstCompiler.cs)

---

## 1. Overview & Problem Statement

Standard GraphQL gateways suffer from the notorious N+1 query problem when fetching nested relations: fetching 100 customers with orders results in 101 separate SQL database roundtrips. F-PERF-09 compiles nested GraphQL selection sets into a single, optimized SQL query using modern database JSON operators (`FOR JSON PATH` in T-SQL, `json_agg` in PostgreSQL). The entire nested graph resolves in a single database roundtrip.

---

## 2. Business Value

- **Sub-10ms Deep Graph Resolution**: Reduces database roundtrip overhead by up to 98% for deeply nested queries.
- **Drastic Database Connection Pool Relief**: Resolving a complex nested query consumes exactly one database connection for a fraction of the time.
- **Zero Client Modification**: Works transparently on standard GraphQL queries without requiring custom client syntax.

---

## 3. Architecture & Capabilities

- Dialect-aware compilation for SQL Server (`FOR JSON PATH`), PostgreSQL (`json_build_object`, `json_agg`), and SQLite (`json_group_array`).
- Injects RLS filters and column masking directly into sub-select JSON projections.
- Fallback to optimized DataLoader batching if single-query complexity exceeds configured limits.

---

## 4. Usage Example

```graphql
# Complex nested GraphQL query:
query GetCustomersWithOrdersAndItems {
  customers(limit: 10) {
    id
    name
    orders {
      id
      orderDate
      items {
        productName
        quantity
      }
    }
  }
}

# Compiled into single PostgreSQL query:
# SELECT json_agg(json_build_object('id', c.id, 'name', c.name, 'orders', (...))) FROM customers c ...
# Executed in a single roundtrip!
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "SingleQueryPushdown": {
      "Enabled": true,
      "MaxNestingDepth": 4,
      "SupportedDialects": ["SqlServer", "PostgreSql", "Sqlite"],
      "FallbackToDataLoaderOnLimit": true
    }
  }
}
```
