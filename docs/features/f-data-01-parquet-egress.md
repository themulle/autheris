# F-DATA-01: Hierarchical Parquet Egress & Nested Query Serialization

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`ParquetGraphQLResponseMiddleware.cs`](file:///root/autheris/src/Autheris.Api/Middleware/ParquetGraphQLResponseMiddleware.cs), [`ParquetOutputNegotiationMiddleware.cs`](file:///root/autheris/src/Autheris.Api/Middleware/ParquetOutputNegotiationMiddleware.cs)

---

## 1. Overview & Problem Statement

Exporting analytical datasets via JSON payloads causes high CPU serialization overhead and massive network payloads for data science and ML pipelines (Python, Pandas, Apache Spark). F-DATA-01 provides direct Apache Parquet egress across all gateway endpoints (`/graphql`, `/api/v1/sql`, `/api/v1/queries/{name}`, and `/odata/v4/...`) simply by specifying `Accept: application/vnd.apache.parquet`. Nested 1:N relations are serialized into hierarchical Parquet structures or flattened dot-notation columns.

---

## 2. Business Value

- **80%+ Bandwidth & Storage Reduction**: Snappy-compressed columnar Parquet files drastically reduce network transmission costs compared to verbose JSON.
- **Direct Ingestion for Data Science**: Data scientists can load query results straight into Pandas DataFrames or Spark without JSON parsing overhead.
- **Strict Governance Integrity**: The Parquet transformation occurs post-governance; all masked fields and RLS filters remain strictly applied.

---

## 3. Architecture & Capabilities

- HTTP content negotiation using `Accept: application/vnd.apache.parquet`.
- Snappy and Gzip compression with single-row-group columnar output via Parquet.Net.
- Automatic metadata response headers: `X-Row-Count` and `X-Export-Truncated`.

---

## 4. Usage Example

```bash
# Query orders via GraphQL and receive a compressed Parquet binary file
curl -X POST http://localhost:8080/graphql \
  -H "Authorization: Bearer <user-token>" \
  -H "GraphQL-Preflight: 1" \
  -H "Content-Type: application/json" \
  -H "Accept: application/vnd.apache.parquet" \
  -d '{"query": "{ sales_orders { orderId customerId orderDate totalAmount } }"}' \
  -o orders.parquet

# Verify with Python / DuckDB:
# python3 -c "import pandas as pd; df = pd.read_parquet('orders.parquet'); print(df.head())"
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "ParquetEgress": {
      "Enabled": true,
      "MaxRowsPerFile": 100000,
      "MaxBufferedSourceBytes": 67108864,
      "Compression": "Snappy",
      "FlattenNestedStructures": true
    }
  }
}
```
