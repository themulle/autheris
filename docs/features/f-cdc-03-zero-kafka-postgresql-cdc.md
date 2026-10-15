# F-CDC-03: Zero-Kafka PostgreSQL CDC via Logical Streaming Replication

**Components:** [`PostgreSqlLogicalReplicationService.cs`](file:///root/autheris/src/Autheris.Infrastructure/Streaming/PostgreSqlLogicalReplicationService.cs), [`IStreamRlsPolicyEnforcer.cs`](file:///root/autheris/src/Autheris.Application/Streaming/Interfaces/IStreamRlsPolicyEnforcer.cs)

---

## 1. Overview & Problem Statement

Streaming change events from PostgreSQL usually requires setting up Kafka Connect, Schema Registry, and Debezium. In regulated on-premises or sovereign cloud settings, this operational complexity often prevents event streaming adoption. F-CDC-03 connects directly to PostgreSQL's native streaming replication protocol (`pgoutput` / logical replication slots), parsing Write-Ahead Log (WAL) streams in-process with zero Kafka dependencies.

---

## 2. Business Value

- **Massive TCO Reduction**: Completely eliminates the licensing, hardware, and operational costs of managing a Kafka cluster.
- **Sub-10ms End-to-End Event Latency**: Consumes WAL changes as they commit and pushes them directly to active GraphQL/SSE client streams.
- **Built-in Tenant and PII Protection**: Decoded WAL events are passed through Casbin ABAC and column masking filters before dispatch.

---

## 3. Architecture & Capabilities

- Direct protocol connection via `Npgsql` Logical Replication.
- Automatic slot creation and WAL LSN (Log Sequence Number) acknowledgement.
- In-stream deserialization and JSON projection with epoch cache invalidation.

---

## 4. Usage Example

```graphql
# Subscribe to live customer profile changes originating from PostgreSQL WAL
subscription OnCustomerChanged {
  customerStream(tenantId: "tenant-emea-01") {
    action # INSERT, UPDATE, DELETE
    timestamp
    customer {
      id
      email # PII masked for unauthorized subscribers
      company
    }
  }
}
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "PostgreSqlCdc": {
      "Enabled": true,
      "ConnectionString": "Host=pg-primary.corp.local;Database=appdb;Username=repl_user;Password=secret",
      "PublicationName": "autheris_publication",
      "SlotName": "autheris_cdc_slot",
      "AcknowledgeIntervalMs": 5000
    }
  }
}
```

## Tenant handling on `POST /api/v1/cdc/events`

The row tenant is read from the tenant column registered for the target table (`TableMetadata.TenantColumnName`; `tenant_id`, `TenantId`, `tenantId` and `TENANT-ID` are all recognised). The endpoint rejects the event (400) when the before and after images carry different tenant values, and, for non-admin callers, when the table has a tenant column but the row value is empty. A foreign tenant value yields 403. The caller tenant is stamped onto an event only for tables registered without a tenant column while `DataSources:RequireTenantColumn` is false.
