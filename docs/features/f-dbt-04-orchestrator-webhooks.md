# F-DBT-4: Zero-Touch dbt Cloud & Orchestrator Webhook Integration

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`DbtWebhookEndpoints.cs`](file:///root/autheris/src/Autheris.Api/Endpoints/DbtWebhookEndpoints.cs), [`DbtWebhookSignatureValidator.cs`](file:///root/autheris/src/Autheris.Application/Dbt/Services/DbtWebhookSignatureValidator.cs)

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

## Error responses

Webhook, dbt and CDC subscription endpoints never return exception text to clients. Failures are returned as `application/problem+json` with a fixed `detail`, a stable `extensions.code` and `extensions.traceId`; the original exception is logged server-side with the same trace id.

| Endpoint | Status | Code |
|---|---|---|
| ITSM, ServiceNow and Jira webhooks (anonymous) | 400 | `WEBHOOK_PAYLOAD_INVALID`, `WEBHOOK_SIGNATURE_OR_FORMAT_INVALID` |
| `POST /api/v1/cdc/subscriptions` (target URL rejected by egress policy; unresolved, private and blocked targets are indistinguishable) | 400 | `CDC_TARGET_URL_NOT_ALLOWED` |
| `POST /api/v1/cdc/subscriptions` (subscription limit) | 429 | `CDC_SUBSCRIPTION_QUOTA_EXCEEDED` |
| dbt proposal approve and reject | 409 | `DBT_PROPOSAL_CONFLICT` |
| dbt semantic manifest ingestion | 400 | `DBT_MANIFEST_INVALID` |
| Bulk consent row filter syntax error | 400 | `ROW_FILTER_SYNTAX_INVALID` (parser detail only in Development) |

An architecture test (`NoRawExceptionMessageInEndpointResponsesTests`) fails when new endpoint code exposes `ex.Message`; legacy sites are listed in its baseline.
