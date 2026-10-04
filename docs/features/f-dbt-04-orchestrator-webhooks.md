# F-DBT-4: Zero-Touch dbt Cloud & Orchestrator Webhook Integration

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`DbtWebhookEndpoints.cs`](file:///root/lis-git/autheris/src/Autheris.Api/Endpoints/DbtWebhookEndpoints.cs), [`DbtWebhookSignatureValidator.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Dbt/Services/DbtWebhookSignatureValidator.cs)

---

## 1. Overview & Problem Statement

When dbt runs complete in dbt Cloud, Apache Airflow, or Dagster, the gateway must immediately update its schema definitions, doc-blocks, and cache epochs without requiring manual restarts or scheduled polling. F-DBT-4 implements secure webhook receivers (`/api/v1/webhooks/dbt`) with HMAC-SHA256 signature verification and replay prevention, triggering instant hot-reloads of catalogs and circuit breakers.

---

## 2. Business Value

- **Real-Time Data Pipeline Synchronization**: Schema updates and test results take effect within milliseconds of pipeline completion.
- **Zero-Touch Operations**: Eliminates manual deployment steps when analytics engineers update models.
- **Robust Webhook Security**: Replay protection and HMAC signature verification block forged or malicious webhook calls.

---

## 3. Architecture & Capabilities

- Native support for dbt Cloud job completion webhooks (`job.run.completed`).
- Timing-safe HMAC verification (`X-Dbt-Signature`).
- Triggers asynchronous catalog refresh and epoch invalidation across the cluster.

---

## 4. Usage Example

```bash
# dbt Cloud sends webhook upon job run completion
curl -X POST http://localhost:8080/api/v1/webhooks/dbt \
  -H "Content-Type: application/json" \
  -H "X-Dbt-Signature: sha256=a84f3e...b92" \
  -d '{
    "eventType": "job.run.completed",
    "data": {
      "runId": 1829401,
      "jobId": 4012,
      "status": "success",
      "manifestUrl": "https://metadata.cloud.getdbt.com/manifest.json"
    }
  }'
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "Dbt": {
      "Webhooks": {
        "Enabled": true,
        "Secret": "YOUR-DBT-WEBHOOK-SECRET",
        "TimestampToleranceSeconds": 300,
        "AutoTriggerCatalogSync": true
      }
    }
  }
}
```
