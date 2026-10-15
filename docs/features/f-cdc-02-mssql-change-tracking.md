# F-CDC-02: Native MSSQL Change Tracking Ingestion Provider

**Components:** [`IMssqlChangeTrackingPoller.cs`](file:///root/autheris/src/Autheris.Application/Streaming/Interfaces/IMssqlChangeTrackingPoller.cs), [`MssqlChangeTrackingPoller.cs`](file:///root/autheris/src/Autheris.Extensions/Cdc/MssqlChangeTrackingPoller.cs), [`MssqlChangeTrackingHostedService.cs`](file:///root/autheris/src/Autheris.Extensions/Cdc/MssqlChangeTrackingHostedService.cs), [`IMssqlWatermarkStore.cs`](file:///root/autheris/src/Autheris.Application/Streaming/Interfaces/IMssqlWatermarkStore.cs), [`GovernanceDbMssqlWatermarkStore.cs`](file:///root/autheris/src/Autheris.Infrastructure/Streaming/GovernanceDbMssqlWatermarkStore.cs), [`StreamRlsPolicyEnforcer.cs`](file:///root/autheris/src/Autheris.Application/Streaming/Services/StreamRlsPolicyEnforcer.cs)

---

## 1. Overview & Problem Statement

Enterprises with high-volume Microsoft SQL Server workloads require real-time notifications of database updates without deploying complex Kafka, Debezium, or Zookeeper infrastructure. F-CDC-02 provides native Change Data Capture (CDC) using MSSQL Change Tracking (`CHANGETABLE`). The gateway polls change tracking versions, normalizes insert/update/delete events, and publishes them to the CDC event channel that feeds GraphQL subscriptions, applying in-stream RLS and column masking.

---

## 2. Business Value

- **Zero-Infrastructure CDC**: No need to deploy, maintain, or monitor Kafka/Zookeeper clusters for database change streaming.
- **Low Overhead on Production Databases**: MSSQL Change Tracking operates synchronously with database transactions at negligible CPU/disk overhead compared to CDC table replication.
- **Governed Real-Time Data**: Changes are scrubbed of unconsented rows and PII columns before being emitted to subscribers.

---

## 3. Architecture & Capabilities

- Reads `CHANGETABLE(CHANGES ...)` per tracked table, joined to the table by its primary key, and detects created, updated and deleted rows (`SYS_CHANGE_OPERATION`).
- Publishes `CdcEvent`s into `ICdcEventChannel` (`InMemoryCdcEventChannel`), the source of the `onTableChanged` GraphQL subscription.
- Runs on exactly one replica at a time: `MssqlChangeTrackingHostedService` is a `ClusterScheduledService` (lease key `cdc:mssql:poll`, see [F-OPS-02](f-ops-02-high-availability-kubernetes-helm.md#4-cluster-leases--scheduled-singletons)). The lease TTL is `max(15 s, 3 x polling interval)`; the poll job is cancelled when the lease is lost.

### 3.1 Durable, Fenced Watermark

The last processed `SYS_CHANGE_VERSION` per table is stored in the governance database table `autheris_cdc_watermarks` (governance migration `0003`, `cdc_watermarks`), keyed by `(source, domain_name, schema_name, table_name)` with the columns `watermark`, `fencing_token`, `updated_at` and `updated_by`; MSSQL Change Tracking rows use `source = 'mssql_ct'`.

- A write carries the lease's fencing token and is accepted only when the token is greater than or equal to the stored one; the watermark never moves backwards (`max(stored, new)`).
- A rejected write raises `FencingTokenRejectedException` (the writer lost its lease to a newer owner), increments `autheris_cdc_watermark_fenced_total` and is logged as critical. A rejection without a lease change points to a reset fence counter: check the persistence of the cluster-state store.
- The host always registers the durable store (`GovernanceDbMssqlWatermarkStore`). The worker refuses to start outside Development when the registered store is not durable.

### 3.2 Poller Semantics

1. **First run:** with no stored watermark, the poller stores `CHANGE_TRACKING_CURRENT_VERSION()` and publishes nothing. The change history before that point is not replayed.
2. **Primary key required:** a tracked table without a primary key is a configuration error (`CdcSourceConfigurationException`); so is a database or table without change tracking. There is no default key column.
3. **Expired watermark:** when the stored watermark is lower than `CHANGE_TRACKING_MIN_VALID_VERSION`, changes were cleaned up and cannot be replayed. The poll fails with `CdcWatermarkExpiredException` and increments `autheris_cdc_watermark_expired_total`; the watermark is never reset automatically.
4. **Persistent errors back off:** expired watermarks and configuration errors stop polling of that table, are logged once per streak and retried with exponential backoff (starting at 5 s, capped at 5 minutes). Other tables keep polling.
5. **Batch boundaries:** a batch holds at most `BatchSize` rows (default 500, clamped 1..5000). When a full batch ends inside one change version, only the complete versions before it are published; if the whole batch is a single version, that version is read completely. A watermark therefore never advances past a partially published version.
6. **At-least-once delivery:** the poller first probes the fence (a commit of the current watermark), then publishes the batch, then commits the highest published version. A crash between publish and commit re-publishes the batch. Event ids are deterministic (`{domain}_{table}_{version}_{pk values}`), so consumers deduplicate on `EventId`. CDC webhook subscriptions never forward this native id (it embeds primary key values): the CloudEvent `id` of a webhook delivery is the opaque hash `cdc-<hash>` (see [F-EXT-01](f-ext-01-generic-plugin-events-and-webhooks.md), section 4.4.2).

### 3.3 Recovering an Expired Watermark

1. Confirm the log entry `MSSQL Change Tracking watermark ... is older than min valid version ...` and the metric `autheris_cdc_watermark_expired_total`.
2. Accept the gap (changes between the stored and the minimum valid version are lost), or re-synchronize consumers from the source.
3. Delete the row of the table: `DELETE FROM autheris_cdc_watermarks WHERE source = 'mssql_ct' AND domain_name = '<domain>' AND schema_name = '<schema>' AND table_name = '<table>';`
4. The next poll (after the backoff) restarts at the current change tracking version. Increase the change tracking retention of the source database if the poller cannot keep up.

---

## 4. Usage Example

```graphql
# Subscribe to real-time changes of a table sourced from MSSQL Change Tracking
subscription OnOrderChanged {
  onTableChanged(table: "sales.dbo.orders") {
    eventId
    table
    operation
    tenantId
    payloadJson
    timestamp
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
      "ConnectionString": "",
      "PollingIntervalMilliseconds": 1000,
      "BatchSize": 500,
      "TrackedTables": [ "sales.dbo.orders", "dbo.customers" ]
    }
  }
}
```

| Option | Default | Description |
| :--- | :--- | :--- |
| `Enabled` | `false` | Registers the polling worker. The worker idles while no valid table is configured. |
| `ConnectionString` | `""` | Source database; when empty, the first SQL Server data source in `DataSources:Connections` is used. |
| `PollingIntervalMilliseconds` | `1000` | Polling interval (minimum 250 ms). |
| `BatchSize` | `500` | Maximum changes per table and poll (1..5000). |
| `TrackedTables` | `[]` | `domain.schema.table`, `schema.table` (domain `default`) or `table` (domain `default`, schema `dbo`). Schema, table and key column names must match `^[a-zA-Z_][a-zA-Z0-9_]*$`. |

### Metrics

| Metric | Description |
| :--- | :--- |
| `autheris_cdc_watermark_expired_total` | Polls aborted because the stored watermark is below the minimum valid version. |
| `autheris_cdc_watermark_fenced_total` | Watermark writes rejected because a newer fencing token exists. |
| `autheris_scheduled_job_*{job="MssqlChangeTrackingPollJob"}` | Lease, duration and failure metrics of the scheduled worker ([F-OPS-02](f-ops-02-high-availability-kubernetes-helm.md#46-metrics)). |
