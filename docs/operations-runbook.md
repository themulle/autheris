# Operations Runbook - GraphQL Enterprise Gateway

This runbook outlines operational procedures, emergency incident response, rolling update execution, and audit verification for the GraphQL Enterprise Gateway.

---

## 1. Emergency Consent Revocation

### 1.1 Objective
Immediately cut off unauthorized or compromised user or group access to a table without taking down the gateway.

### 1.2 Procedure via GraphQL Mutation
An authorized Data Owner or System Administrator executes the `revokeConsent` mutation:

```graphql
mutation EmergencyRevokeConsent {
  revokeConsent(
    consentId: "b8a92e10-67c3-4d7a-8f81-54625b902da1"
    reason: "Security incident: Compromised user credentials"
  )
}
```

### 1.3 Direct Database Fallback (Break-Glass)
If the GraphQL API is inaccessible, update the governance database directly:

```sql
-- 1. Mark consent as revoked
UPDATE CONSENTS
SET STATUS = 'REVOKED',
    REVOCATION_REASON = 'Emergency security incident',
    REVOKED_AT = CURRENT_TIMESTAMP
WHERE CONSENT_ID = 'b8a92e10-67c3-4d7a-8f81-54625b902da1';

-- 2. Increment table policy epoch to invalidate all distributed L1/L2 caches
UPDATE POLICY_EPOCHS
SET EPOCH_VERSION = EPOCH_VERSION + 1,
    LAST_UPDATED_AT = CURRENT_TIMESTAMP
WHERE TABLE_ID = (
    SELECT TABLE_ID FROM CONSENTS WHERE CONSENT_ID = 'b8a92e10-67c3-4d7a-8f81-54625b902da1'
);
```

Within seconds, the L1/L2 cache validator detects the epoch version mismatch and evicts cached authorizations, reverting queries to Zero-Trust `FORBIDDEN`.

---

## 2. Rolling Updates & Zero-Downtime Drain

### 2.1 Drain Protocol Lifecycle
The gateway implements a 6-phase graceful shutdown:
1. **SIGTERM / Stopping Trigger**: `TrafficDrainController.StartDraining()` is called.
2. **Readiness Probe Drop**: `/health/ready` immediately returns `503 Service Unavailable`. `/health/live` remains `200 OK`.
3. **Drain Delay Window**: The gateway sleeps for `DrainDelaySeconds` (default: 5 seconds), allowing Kubernetes Ingress and internal load balancers to route new incoming connections to other pods.
4. **In-Flight Request Drain**: Actively waits for `activeRequestCount == 0` or until `ShutdownTimeoutSeconds` (default: 30 seconds) expires.
5. **GraphQL Pipeline Stop**: Halts accepting HTTP requests.
6. **Resource Cleanup**: Flushes buffered audit log entries and cleanly disposes database connection pools.

### 2.2 Kubernetes Deployment Verification
Ensure the `deployment.yaml` matches the following configuration:

```yaml
spec:
  terminationGracePeriodSeconds: 60
  template:
    spec:
      containers:
        - name: gql-gateway
          readinessProbe:
            httpGet:
              path: /health/ready
              port: 5000
            initialDelaySeconds: 5
            periodSeconds: 3
            failureThreshold: 1
          livenessProbe:
            httpGet:
              path: /health/live
              port: 5000
            initialDelaySeconds: 10
            periodSeconds: 10
```

---

## 3. Redis Degraded Mode & Fallback

### 3.1 Failure Detection
If the Redis cluster becomes unavailable:
- L2 distributed cache calls fail gracefully and log a warning.
- The gateway automatically falls back to L1 local `IMemoryCache`.
- Epoch validation queries the database directly with brief local caching.
- Queries continue executing without dropping client requests or returning 500 errors.

### 3.2 Recovery
Once Redis connectivity is restored:
- L2 cache operations resume automatically.
- Issue a manual schema reload or restart pods sequentially to re-establish real-time pub/sub listeners.

---

## 4. Tamper-Evident Audit Hash Chain Verification

### 4.1 Verification Principle
Every entry in `AUDIT_LOGS` contains:
- `AUDIT_ID`: Unique UUID
- `EVENT_TYPE`, `TARGET_TABLE`, `ACTOR_SID`, `DETAILS`, `TIMESTAMP`
- `PREVIOUS_HASH`: SHA-256 hash of the preceding log entry
- `HASH`: SHA-256 hash computed over `PREVIOUS_HASH:AUDIT_ID:EVENT_TYPE:TARGET_TABLE:ACTOR_SID:TIMESTAMP`

If any row is modified, deleted, or inserted out of order, the SHA-256 chain breaks.

### 4.2 Verification CLI / Script
Run the verification query using standard Python or C#:

```python
import hashlib
import sqlite3

def verify_audit_chain(db_path):
    conn = sqlite3.connect(db_path)
    cursor = conn.cursor()
    cursor.execute("""
        SELECT AUDIT_ID, PREVIOUS_HASH, EVENT_TYPE, TARGET_TABLE, ACTOR_SID, TIMESTAMP, HASH
        FROM AUDIT_LOGS
        ORDER BY ROWID ASC
    """)
    rows = cursor.fetchall()
    print(f"Verifying {len(rows)} audit log records...")

    expected_prev_hash = "GENESIS"
    for idx, (audit_id, prev_hash, event_type, target_table, actor_sid, ts, stored_hash) in enumerate(rows):
        if prev_hash != expected_prev_hash:
            raise ValueError(f"Chain broken at record {idx} ({audit_id}): Expected previous hash {expected_prev_hash}, got {prev_hash}")

        payload = f"{prev_hash}:{audit_id}:{event_type}:{target_table}:{actor_sid}:{ts}"
        calculated_hash = hashlib.sha256(payload.encode('utf-8')).hexdigest()

        if calculated_hash != stored_hash:
            raise ValueError(f"Tamper detected at record {idx} ({audit_id})! Stored hash: {stored_hash}, Calculated: {calculated_hash}")

        expected_prev_hash = stored_hash

    print("Audit log integrity verified successfully. No tampering detected.")

if __name__ == "__main__":
    verify_audit_chain("governance.db")
```

---

## 5. Data Catalog Synchronization Operations

### 5.1 Monitoring Background Sync
The gateway runs `DataCatalogSyncBackgroundService` periodically (configurable via `Gateway:Catalog:SyncIntervalMinutes`, default: 60 minutes).
- Check health and sync status in logs:
  ```bash
  kubectl logs -l app=gql-gateway -n data-governance | grep "DataCatalogSync"
  ```
- Alerts to monitor:
  - `Failed to fetch tables from Data Catalog`: Indicates connectivity or credential issues to Microsoft Purview / Collibra / Alation / OpenMetadata.
  - `Sync already in progress`: Indicates previous sync cycle exceeded interval; increase `SyncIntervalMinutes`.

### 5.2 Manual / On-Demand Catalog Sync
Trigger immediate catalog sync via GraphQL mutation without restarting pods:
```graphql
mutation ForceCatalogSync {
  syncDataCatalog(dryRun: false) {
    success
    syncedTablesCount
    syncedColumnsCount
    maskedColumnsCount
    art9ProtectedTablesCount
    warnings
  }
}
```

---

## 6. Pre-Schema-Change Impact Analysis (Downstream Consumers)

### 6.1 Objective
Before making breaking changes (dropping tables, renaming columns, modifying data types), assess the blast radius on dependent PowerBI/Tableau dashboards, Airflow/dbt ETL pipelines, and active API clients.

### 6.2 Procedure
Execute the `tableConsumers` GraphQL query for the target table:
```graphql
query CheckBreakingChangeRisk {
  tableConsumers(domain: "finance", schema: "dbo", tableName: "invoices", timeWindowDays: 30) {
    breakingChangeRisk # CRITICAL, HIGH, MEDIUM, LOW
    totalDownstreamCount
    activeReadersCount
    downstreamConsumers {
      id
      name
      type # Dashboard, Pipeline, ExternalService
      ownerTeam
      ownerEmail
    }
    recommendedMitigations
  }
}
```

### 6.3 Risk Response Matrix
- **`CRITICAL`**: Active dashboards/pipelines + high query volume. **Action:** Enforce minimum 14-day deprecation notice. Coordinate deployment windows with affected data owners. Provide temporary backward-compatibility views.
- **`HIGH`**: Downstream pipelines or dashboards depend on the table, but recent query activity is low. **Action:** Notify team leads of affected downstream systems before release.
- **`MEDIUM`**: Only external services or occasional queries detected. **Action:** Announce release in engineering channel.
- **`LOW`**: No active consumers or downstream dependencies. **Action:** Safe to proceed with schema migration.

---

## 7. GDPR Art. 15 Right of Access Disclosures (DSGVO-Auskunft)

### 7.1 Objective
Respond to data subject access requests (DSGVO Art. 15 Abs. 1 Bst. c) or compliance audits regarding who has accessed sensitive or special category (Art. 9) data.

### 7.2 Procedure
Execute the `gdprDataDisclosureReport` query specifying the table or subject SID:
```graphql
query GetGdprDisclosureReport {
  gdprDataDisclosureReport(
    domain: "healthcare"
    schema: "dbo"
    tableName: "patient_records"
    timeWindowDays: 365
  ) {
    targetTable
    totalAccessEvents
    sensitivityCategories
    legalBasisNotice
    disclosedRecipients {
      recipientSid
      recipientCategory # ServicePrincipal, InteractiveUser, DownstreamSystem
      purpose
      firstAccess
      lastAccess
      totalQueries
      accessedColumns
      maskingRuleApplied
    }
  }
}
```
Export results to CSV/PDF for compliance documentation.

---

## 8. Auditing Insecure Configurations in Production

### 8.1 Verification Rule
In production environments, all `danger_*` and `warn_*` flags must be strictly `false`.

### 8.2 Audit Check via CLI / K8s ConfigMap
```bash
# Verify ConfigMap does not contain active insecure flags
kubectl get configmap gql-gateway-config -n data-governance -o yaml | grep -E "(warn_|danger_)"

# Ensure no pods log insecure mode warnings
kubectl logs -l app=gql-gateway -n data-governance | grep -i "INSECURE MODE ENGAGED"
```
If any pod logs `[CRITICAL SECURITY ALERT] Insecure flag engaged`, immediately file a Priority-1 security incident and revert the configuration.

