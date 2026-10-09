# F-CDC-02: Native MSSQL Change Tracking Ingestion Provider

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`IMssqlChangeTrackingPoller.cs`](file:///root/autheris/src/Autheris.Application/Streaming/Interfaces/IMssqlChangeTrackingPoller.cs), [`MssqlChangeTrackingPoller.cs`](file:///root/autheris/src/Autheris.Extensions/Cdc/MssqlChangeTrackingPoller.cs), [`StreamRlsPolicyEnforcer.cs`](file:///root/autheris/src/Autheris.Application/Streaming/Services/StreamRlsPolicyEnforcer.cs)

---

## 1. Overview & Problem Statement

Enterprises with high-volume Microsoft SQL Server workloads require real-time notifications of database updates without deploying complex Kafka, Debezium, or Zookeeper infrastructure. F-CDC-02 provides native Change Data Capture (CDC) using MSSQL Change Tracking (`CHANGETABLE`). The gateway continuously polls change tracking version numbers, normalizes insert/update/delete events, and pipes them directly into GraphQL subscriptions and event channels while applying in-stream RLS and column masking.

---

## 2. Business Value

- **Zero-Infrastructure CDC**: No need to deploy, maintain, or monitor Kafka/Zookeeper clusters for database change streaming.
- **Low Overhead on Production Databases**: MSSQL Change Tracking operates synchronously with database transactions at negligible CPU/disk overhead compared to CDC table replication.
- **Governed Real-Time Data**: Changes are scrubbed of unconsented rows and PII columns before being emitted to subscribers.

---

## 3. Architecture & Capabilities

- Tracks table change version tokens (`CHANGE_TRACKING_CURRENT_VERSION`).
- Detects created, updated, and deleted rows (`SYS_CHANGE_OPERATION`).
- Feeds events directly into the gateway's `InMemoryCdcEventChannel` and WebSocket subscriptions.

---

## 4. Usage Example

```graphql
# Subscribe to real-time order status updates sourced from MSSQL Change Tracking
subscription OnOrderUpdated {
  orderChangeTracking(domain: "sales", tableName: "orders") {
    operation # INSERT, UPDATE, DELETE
    version
    entityId
    data {
      orderId
      status
      updatedAt
    }
  }
}
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "MssqlChangeTracking": {
      "Enabled": true,
      "PollingIntervalMs": 1000,
      "BatchSize": 500,
      "Tables": [
        { "Domain": "sales", "Schema": "dbo", "Table": "orders" }
      ]
    }
  }
}
```
