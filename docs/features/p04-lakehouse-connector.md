# P4: Modern Lakehouse Connector (Apache Iceberg v2)

**Status:** [Done] (100% GA – Core Foundation)  
**Components:** [`IcebergMetadataReader.cs`](file:///root/lis-git/autheris/src/Autheris.Extensions/Lakehouse/Services/IcebergMetadataReader.cs), [`IcebergPartitionPruner.cs`](file:///root/lis-git/autheris/src/Autheris.Extensions/Lakehouse/Services/IcebergPartitionPruner.cs), [`LakehouseDataSourceExecutor.cs`](file:///root/lis-git/autheris/src/Autheris.Extensions/Lakehouse/Services/LakehouseDataSourceExecutor.cs)

---

## 1. Overview & Problem Statement

Modern enterprise data architectures store petabytes of data in open table formats like Apache Iceberg on Amazon S3 or Azure Blob Storage. Querying this data via traditional SQL warehouses (e.g. Snowflake or Databricks) incurs high compute costs and introduces query latency. P4 provides a native Apache Iceberg v2 connector that reads metadata manifests, applies vectorized partition and Min/Max statistics pruning, and streams Parquet data directly into the gateway execution engine.

---

## 2. Business Value

- **Significant Cloud Cost Reductions**: Query Lakehouse tables directly without maintaining active 24/7 data warehouse compute clusters.
- **Direct Lakehouse-to-API Bridging**: Expose Lakehouse datasets to operational applications and GraphQL frontends in real time.
- **Zero-Trust Lakehouse Security**: Applies central consent policies and column-level PII masking to Lakehouse queries before results exit the gateway.

---

## 3. Architecture & Capabilities

- Native Iceberg v2 format support with L1 metadata caching.
- Vectorized partition pruning skipping up to 95% of data files based on query predicates.
- AWS SigV4 signed requests and Azure Managed Identity support for object storage access.

---

## 4. Usage Example

```graphql
# Query an Apache Iceberg lakehouse table directly via GraphQL
query QueryLakehouseTelemetry {
  lakehouseTable(domain: "telemetry", tableName: "iot_device_readings") {
    deviceId
    readingTimestamp
    temperature
    status
  }
}
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "Lakehouse": {
      "Enabled": true,
      "StorageProvider": "S3",
      "S3": {
        "Bucket": "enterprise-lakehouse-data",
        "Region": "eu-central-1",
        "MetadataCacheTtlMinutes": 30
      },
      "Tables": [
        { "Domain": "telemetry", "TableName": "iot_device_readings", "Location": "s3://enterprise-lakehouse-data/telemetry/readings" }
      ]
    }
  }
}
```
