# F-PERF-08: Hierarchical Resource Groups & Workload Queuing

**Status:** [Done] (100% GA – Wave 2)  
**Components:** [`ResourceGroupMiddleware.cs`](file:///root/lis-git/autheris/src/Autheris.Api/Middleware/ResourceGroupMiddleware.cs), [`IResourceGroupQueueManager.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Performance/IResourceGroupQueueManager.cs)

---

## 1. Overview & Problem Statement

Heavy analytical batch queries or ad-hoc BI reports can easily monopolize database connection pools, starving critical customer-facing mobile applications. F-PERF-08 implements hierarchical resource groups modeled after enterprise database workload managers. Incoming queries are assigned to resource groups (e.g. `root.interactive`, `root.batch.nightly`) based on client tier, query complexity, or user role, each with isolated concurrency quotas and queueing policies.

---

## 2. Business Value

- **Guaranteed SLAs for Critical Apps**: High-priority interactive traffic is protected from latency spikes caused by heavy BI queries.
- **Graceful Under Load**: Excess low-priority queries are queued or throttled rather than crashing the gateway or overwhelming backend databases.
- **Fair Resource Allocation**: Prevents single users or automated batch scripts from consuming more than their fair share of cluster resources.

---

## 3. Architecture & Capabilities

- Hierarchical parent-child resource group definitions with max concurrent query limits.
- Workload queuing with configurable timeout thresholds.
- Dynamic assignment based on authenticated user roles, client headers, or query complexity.

---

## 4. Usage Example

```bash
# Heavy batch query assigned to background queue
curl -X POST http://localhost:8080/graphql \
  -H "Authorization: Bearer <batch-token>" \
  -H "X-Client-Tier: BatchAnalytics" \
  -d '{"query": "{ bulkFinancialExport { transactions { id amount } } }"}'

# Response headers indicate resource group assignment:
# HTTP/1.1 200 OK
# X-Resource-Group: root.batch.analytics
# X-Queue-Wait-Ms: 12.4
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "ResourceGroups": {
      "Enabled": true,
      "Groups": {
        "root.interactive": {
          "MaxConcurrentQueries": 100,
          "MaxQueuedQueries": 50,
          "QueueTimeoutMs": 5000
        },
        "root.batch": {
          "MaxConcurrentQueries": 5,
          "MaxQueuedQueries": 100,
          "QueueTimeoutMs": 60000
        }
      }
    }
  }
}
```
