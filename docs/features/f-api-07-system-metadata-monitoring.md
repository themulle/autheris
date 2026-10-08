# F-API-07: Canonical System Metadata & Monitoring Schema ($system)

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`SystemMonitoringEndpoints.cs`](file:///root/autheris/src/Autheris.Api/Endpoints/SystemMonitoringEndpoints.cs), [`IGatewayHealthCheckService.cs`](file:///root/autheris/src/Autheris.Application/Interfaces/IGatewayHealthCheckService.cs)

---

## 1. Overview & Problem Statement

Operating distributed enterprise gateways across multi-cluster environments requires introspectable system health, memory usage, cache hit ratios, and active policy epochs. F-API-07 introduces a canonical `$system` GraphQL and REST namespace exposing detailed runtime metrics, cluster status, Active Directory connectivity, and epoch states to authorized administrators and monitoring agents.

---

## 2. Business Value

- **Proactive Outage Prevention**: Deep health telemetry flags database degradation or Redis connection issues before end-user requests fail.
- **Zero-Downtime Deployment Confidence**: Exposes active policy epoch and graceful drain status for Kubernetes readiness/liveness automation.
- **Security Isolation**: Strictly requires `ClusterAdmin` or `GovernanceAdmin` roles; unauthenticated access is fail-closed.

---

## 3. Architecture & Capabilities

- Live introspection of L1/L2 cache hit/miss statistics and memory usage.
- Verification of Active Directory domain controller and identity provider reachability.
- Real-time policy epoch counters across all loaded data domains.

---

## 4. Usage Example

```graphql
# Query system health and cache telemetry via GraphQL
query GetSystemHealth {
  systemMetadata {
    version
    clusterNodeId
    activePolicyEpoch
    l1Cache {
      totalEntries
      hitRatioPercent
      memoryUsageBytes
    }
    databaseHealth {
      domain
      isReachable
      latencyMs
    }
  }
}
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "SystemMetadata": {
      "Enabled": true,
      "RequireAdminRole": true,
      "ExposeMemoryMetrics": true,
      "HealthCheckIntervalSeconds": 15
    }
  }
}
```
