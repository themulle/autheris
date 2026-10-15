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
If the GraphQL API is inaccessible, update the governance database directly. Column names are those of the `CONSENTS` and `POLICY_EPOCHS` tables (timestamps are ISO-8601 text).

SQLite:

```sql
-- 1. Mark consent as revoked
UPDATE CONSENTS
SET is_revoked = 1,
    revoke_reason = 'Emergency security incident',
    revoked_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
WHERE id = 'b8a92e10-67c3-4d7a-8f81-54625b902da1';

-- 2. Increment table policy epoch so that nodes drop cached authorizations
UPDATE POLICY_EPOCHS
SET epoch = epoch + 1,
    updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
WHERE table_id = (SELECT table_id FROM CONSENTS WHERE id = 'b8a92e10-67c3-4d7a-8f81-54625b902da1');
```

PostgreSQL:

```sql
UPDATE CONSENTS
SET is_revoked = 1,
    revoke_reason = 'Emergency security incident',
    revoked_at = to_char(now() AT TIME ZONE 'utc', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"')
WHERE id = 'b8a92e10-67c3-4d7a-8f81-54625b902da1';

UPDATE POLICY_EPOCHS
SET epoch = epoch + 1,
    updated_at = to_char(now() AT TIME ZONE 'utc', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"')
WHERE table_id = (SELECT table_id FROM CONSENTS WHERE id = 'b8a92e10-67c3-4d7a-8f81-54625b902da1');
```

Microsoft SQL Server (T-SQL):

```sql
UPDATE CONSENTS
SET is_revoked = 1,
    revoke_reason = 'Emergency security incident',
    revoked_at = CONVERT(VARCHAR(33), SYSUTCDATETIME(), 127)
WHERE id = 'b8a92e10-67c3-4d7a-8f81-54625b902da1';

UPDATE POLICY_EPOCHS
SET epoch = epoch + 1,
    updated_at = CONVERT(VARCHAR(33), SYSUTCDATETIME(), 127)
WHERE table_id = (SELECT table_id FROM CONSENTS WHERE id = 'b8a92e10-67c3-4d7a-8f81-54625b902da1');
```

Notes:
- A direct database change **bypasses the audit chain**: no `CONSENT_REVOKED` event is written and nothing is published on the Redis channels. Document the intervention manually (ticket, who, when) and write the audit event as soon as the API is available again. Never edit `AUDIT_LOG_ENTRIES`; it is hash-chained and (PostgreSQL) append-only.
- Nodes pick up the change through the epoch check. After the update, verify with a test query from an affected account that access is `FORBIDDEN`; if a node still serves the old decision, restart it.
- Token revocations (compromised tokens) are not stored in the governance database; use `POST /api/admin/tokens/revoke`.
- Revoking tokens across tenants requires a canonical `ClusterAdmin`; tenant administrators revoke only inside their own tenant.

---

> **Scope of an emergency revocation (review R4-3):** a `GovernanceAdmin` revokes only within their own tenant scope, and the response names it (`scope: tenant`, `tenant`). Tokens of the same subject that carry a different tenant claim (for example Entra `tid` versus the legacy single tenant) stay valid. For a compromised identity that may appear under several tenants, have a **ClusterAdmin** revoke globally.

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
- Consent cache (`ConsentCacheService`): only genuine L2 faults (Redis errors, timeouts, L2 frame crypto and serialization failures) degrade. A failed read is treated as a cache miss (the decision is re-evaluated), a failed write keeps the entry in L1 only, and a failed delete of a stale entry leaves it to expire by TTL. They are logged as warnings with the stable event ids `4111` (`ConsentCacheL2ReadFailed`), `4112` (`ConsentCacheL2DeleteFailed`) and `4113` (`ConsentCacheL2WriteFailed`). Any other exception propagates (fail closed).
- Redis event bus: failed publishes are counted in `autheris_event_bus_publish_failed_total{message_type}` and rethrown; failed subscriber handlers in `autheris_event_bus_handler_failed_total{message_type}`.
- The gateway automatically falls back to L1 local `IMemoryCache`.
- Epoch validation queries the database directly with brief local caching.
- Queries continue executing without dropping client requests or returning 500 errors.
- **Token revocation fails closed by default outside Development** (`Caching:Redis:FailClosedOnRevocationLookupError`, unset = `true` outside Development, `false` in Development). When the Redis revocation lookup fails and the token is not revoked locally, HTTP requests are answered with `503` (`Retry-After: 5`, error code `TOKEN_REVOCATION_UNAVAILABLE`), new WebSocket connections are rejected, and open subscriptions and WebSocket sessions are closed. Tokens issued less than `Caching:Redis:RevocationLookupGraceSeconds` (default 60, range 0 to 3600, 0 disables) ago are still accepted because they cannot have been revoked on another node before the outage began. Revocations known to the local node are always enforced. The failures are counted in `autheris_token_revocation_lookup_failures_total{outcome=fail_closed|grace|fail_open}`.
- **Availability-over-security escape hatch:** set `Caching:Redis:FailClosedOnRevocationLookupError = false` (Helm: `gateway.redis.failClosedOnRevocationLookupError: false`). Revocations issued on other nodes are then not visible while Redis is down and authenticated traffic keeps being served; treat that as degraded security and restore Redis quickly. With PostgreSQL and several replicas Redis is required (startup check), so an outage affects all nodes the same way. Single-node deployments without Redis use the in-memory revocation service and are unaffected.

### 3.1.1 Cluster State Contract (absent vs unavailable)
The shared cluster state (`IDistributedClusterStateProvider`) distinguishes "key absent" from "store unavailable":
- A read of a missing key returns no value. An infrastructure failure (connection loss, timeout, Redis error) throws `ClusterStateUnavailableException` for reads, writes, deletes, event publishing and lock acquisition. A `null` lock lease only ever means "lock busy". Counters return `null` and budgets return `StoreUnavailable` when the store cannot be reached.
- Consumers decide explicitly: TOTP secret reads and TOTP replay consumption fail closed (a valid code is rejected while the store is down), MCP session reads and HitL ticket reads treat the remote entry as not found, HitL locks honour `HitLStepUp:FailClosedOnClusterPartition`, FinOps budget enforcement fails closed, federation nonce replay checks reject requests when the topology is cluster-wide. Background singletons (catalog sync, CDC, ITSM) skip the cycle.
- Every store failure increments `autheris_cluster_state_failure_total{operation}`; failed event handlers increment `autheris_cluster_state_handler_failure_total`. Alert on a sustained rate of the first.
- Cluster singletons (catalog sync, OpenMetadata sync, MSSQL change tracking) run under renewed, fenced leases (`TryAcquireLeaseAsync`). A replica that cannot confirm its lease for two thirds of the TTL cancels its job. Watch `autheris_scheduled_job_last_success_timestamp_seconds{job}`, `autheris_scheduled_job_skipped_total{job,reason}` and `autheris_cluster_lease_lost_total{reason}` (see F-OPS-02).
- Multi-replica deployments require an external Redis/Garnet with AOF persistence; without it a store restart drops leases and fence counters. An external Garnet must have Lua scripting enabled (the embedded server starts with `--lua`).
- The embedded Garnet server is process-local. Startup fails when `HighAvailability:MultiNodeClusterMode` is set or `HighAvailability:Replicas` is greater than 1 while `Caching:Garnet:EnableEmbeddedServer` is true (also when `Caching:Redis:Enabled` is true as well, because the embedded server takes precedence). Use an external Redis/Garnet for multi-replica deployments.

### 3.2 Recovery
Once Redis connectivity is restored:
- L2 cache operations resume automatically.
- Issue a manual schema reload or restart pods sequentially to re-establish real-time pub/sub listeners.

---

## 4. Tamper-Evident Audit Hash Chain Verification

### 4.1 Verification Principle
Every row of `AUDIT_LOG_ENTRIES` carries:
- `id`, `occurred_at` (ISO-8601 `O` format), `event_type`, `actor_sid`, `target_table`, `target_column`, `decision`, `trace_id`, `details_json`, `tenant_id`
- `seq`: gap-free sequence number starting at 1
- `prev_hash`: `entry_hash` of the preceding row (`GENESIS_` followed by 64 zeros for the first row)
- `entry_hash`: **HMAC-SHA256** (upper-case hex) with the audit key from Key Vault (`GovernanceDb:AuditHmacKeyVaultRef`, or derived from `DataMasking:HmacSecretKeyVaultRef`) over the payload `v2|<seq>|<id>|<prev_hash>|<occurred_at>|<event_type>|<actor_sid>|<target_table>|<target_column>|<decision>|<trace_id>|<details_json>|<tenant_id>`; inside the text fields `\` and `|` are escaped with a backslash (PostgreSQL additionally escapes line breaks as `\n`/`\r`).

The hash is keyed. It cannot be recomputed without the key, so a plain SHA-256 script can neither verify nor "repair" the chain. A modified, deleted or reordered row breaks `prev_hash`/`entry_hash`/`seq`. Removal of the **end** of the chain is detected through the signed anchor (`Audit:ChainAnchorPath`); configure it on a different volume/identity than the database.

### 4.2 Verification
Preferred: use the built-in verification (`VerifyAuditHashChainAsync`), which also checks the anchor; a violation sets the audit pipeline to faulted and `/health/ready` reports the governance database as unhealthy. The built-in check is not scheduled automatically; run it from your monitoring at least daily.

Offline check of an export (needs the audit key as hex in `AUDIT_HMAC_KEY_HEX`; SQLite variant, for PostgreSQL also escape `\n`/`\r`):

```python
import hashlib, hmac, os, sqlite3

KEY = bytes.fromhex(os.environ["AUDIT_HMAC_KEY_HEX"])
GENESIS = "GENESIS_" + "0" * 64

def esc(v):
    return "" if not v else v.replace("\\", "\\\\").replace("|", "\\|")

def verify_audit_chain(db_path):
    conn = sqlite3.connect(db_path)
    rows = conn.execute("""
        SELECT id, occurred_at, event_type, actor_sid, target_table, target_column, decision,
               trace_id, details_json, prev_hash, entry_hash, seq, tenant_id
        FROM AUDIT_LOG_ENTRIES ORDER BY rowid ASC""").fetchall()
    print(f"Verifying {len(rows)} audit log records...")

    expected_prev = GENESIS
    for pos, (id_, ts, ev, actor, table, col, dec, trace, details, prev, stored, seq, tenant) in enumerate(rows, start=1):
        if seq is not None and seq != pos:
            raise ValueError(f"Sequence gap at position {pos}: seq {seq}")
        if prev != expected_prev:
            raise ValueError(f"Chain broken at record {pos} ({id_}): expected prev {expected_prev}, got {prev}")
        payload = f"{id_}|{prev}|{ts}|{esc(ev)}|{esc(actor)}|{esc(table)}|{esc(col)}|{esc(dec)}|{esc(trace)}|{esc(details)}|{esc(tenant)}"
        if seq is not None:
            payload = f"v2|{seq}|{payload}"
        calc = hmac.new(KEY, payload.encode("utf-8"), hashlib.sha256).hexdigest().upper()
        if not hmac.compare_digest(calc, stored.upper()):
            raise ValueError(f"Tamper detected at record {pos} ({id_})")
        expected_prev = stored
    print("Chain consistent. Compare the last seq/hash with the signed anchor to rule out truncation.")

if __name__ == "__main__":
    verify_audit_chain("governance.db")
```

The script cannot check the anchor signature or a truncated tail; compare the last `seq`/`entry_hash` with the anchor file.

---

### 4.x Periodic runtime verification (review E-11)

`AuditChainIntegrityMonitor` verifies the chain in the background: first run about two minutes after start, then every `Audit:VerifyHashChainIntervalHours` (default 24). A broken chain is logged as **critical** ("AUDIT HASH CHAIN VIOLATION") and the readiness probe reports the component `AuditChain` as unhealthy, so the instance leaves the load balancer. Settings:

- `Audit:VerifyHashChainEnabled` (default `true`)
- `Audit:FailReadinessOnChainViolation` (default `true`; set `false` to only log)

A run that cannot complete (database unreachable) is logged as an error and is **not** reported as a violation. The monitor does not replace an external anchor on separate WORM storage; an attacker who can restore database and anchor together is still not detected (see threat model, known limitations).

Audit events written for the approval workflow: `CONSENT_APPROVAL_STEP`, `CONSENT_REQUEST_REJECTED`, `CONSENT_GRANTED` (actor = approver/creator), `CONSENT_REVOKED` (with tenant), `CONSENT_EXPIRY_EXTENDED`, `REBAC_TUPLE_ADDED`, `REBAC_TUPLE_REMOVED`, `SCHEMA_RELOAD`.

### 4.y PostgreSQL schema: duplicate ITSM tickets (review PG-12)

The schema creates `ux_consent_requests_ticket` (unique per tenant and `itsm_ticket_id`). If the DDL fails on an existing database, find the duplicates first and resolve them (keep one request, clear the ticket id on the others):

```sql
SELECT COALESCE(tenant_id, ''), itsm_ticket_id, COUNT(*)
FROM CONSENT_REQUESTS WHERE itsm_ticket_id IS NOT NULL
GROUP BY 1, 2 HAVING COUNT(*) > 1;
```

### 4.z FinOps budget in multi-replica deployments (review E-14)

The monthly FinOps budget is enforced against a cluster-wide counter in the shared state store (Redis when `Caching.Redis.Enabled`, otherwise in-memory = per process). Run Redis in every multi-replica deployment; without it each replica enforces the budget on its own. `ResetSpendAsync` clears the shared counter for the current month. Redis outages degrade to local accounting and are logged as `shared FinOps counter unavailable`.

### 4.aa Health probe caching (review R3-4)

`/health/ready` is anonymous and answered from a 5 second cache shared by all callers. A state change (e.g. DB outage) is therefore visible after at most 5 seconds; size Kubernetes probe periods accordingly.

### 4.ab Anchor hardening and durable query audit (reviews E-11, R2-3)
- `Audit:ChainAnchorWormDirectory`: directory on **separate / WORM storage** (object-lock backed mount, WORM NAS). Every anchor is written as a new, never overwritten, read-only file `anchor-<seq>-<ticks>.json`. `Audit:ChainAnchorMirrorPaths` adds further mirror files. On startup the newest anchor of all copies is used, so deleting or rolling back a single copy cannot hide a truncation; two different anchors for one sequence are treated as a violation.
- `Audit:ChainAnchorSignerKeyVaultRef`: Key Vault/KMS reference of a PEM (PKCS#8) ECDSA or RSA private key. Every anchor then also carries an asymmetric signature (`ExternalSignature`) and an anchor without a valid one is rejected (fail-closed). Verify-only replicas can use `Audit:ChainAnchorVerifyKeyVaultRef` (public key). Register a custom `IAuditAnchorSigner` in DI to sign through a remote KMS/HSM so the private key never leaves it.
- Anchors are advanced only after the audit transaction commits, so an append-only anchor can never be ahead of the database.
- `Audit:SynchronousQueryAudit=true` commits query audit events before the request is answered (no loss window on a crash, lower throughput). Otherwise they travel through an in-memory channel of `Audit:QueryAuditChannelCapacity` entries (default 5000), the maximum a hard crash can lose; a graceful shutdown drains it.

### 4.ac Governance schema migrations (WP-ARC-B-12)

- The governance schema is versioned in `autheris_schema_migrations`. The first start of a new build applies pending migrations under the cross-replica schema lock; later replicas find nothing to do. Existing databases upgrade in place.
- Startup fails closed (the host does not serve) when a migration fails, when a stored checksum differs from the build (`GovernanceSchemaDriftException`), when the database is newer than the build, or, in `Verify` mode, while migrations are pending. Read the critical log entry `Governance database migration or initialization failed` and the inner exception.
- Separate pipelines: set `GovernanceDb:Migrations:Mode=Verify` on the application and run `Autheris.Api --migrate-governance` as a pre-deployment job (with `GovernanceDb:MigrationConnectionString` for a DDL-capable role). The command exits `0` on success and `1` on failure.
- Migrations are forward-only. Rolling back to an older build means restoring a database backup taken before the upgrade; an older build refuses to start against a newer schema unless `GovernanceDb:Migrations:AllowNewerSchema=true`.

---

### 4.ad Prometheus scrape access (SEC2-09)

`/metrics` requires the `MetricsScrape` policy: the authenticated principal must carry the `MetricsScraper` (or `ClusterAdmin`) role. Any other authenticated caller gets 403, anonymous callers get 401. Grant the scrape identity (for example the service principal behind the ServiceMonitor bearer token) an app role named `MetricsScraper`; there is no configuration switch that relaxes this. The role must come from an authentication scheme that carries roles (JWT, Basic or Kerberos). A ForwardAuth-only deployment cannot be scraped by default: `ClusterAdmin` is never accepted from proxy headers and the default `ForwardAuth.AllowedRoles` does not include `MetricsScraper`; either list `MetricsScraper` in `ForwardAuth.AllowedRoles` for a trusted scrape proxy or scrape through a JWT-capable path.

### 4.ae Webhook outbox (WP-ARC-A-02)

- Undelivered webhooks are in `WEBHOOK_OUTBOX` (governance database). Watch `autheris_webhook_outbox_pending`, `autheris_webhook_deliveries_total{outcome}` and `autheris_webhook_dead_letters_total{code}`.
- A growing `dead_letters_total{code="egress_denied"}` or `{code="http_status"}` points at a misconfigured target; `subscription_not_found` appears after a subscription `Id` was renamed or removed while rows were queued (ids must stay stable).
- A subscription that fails `AutoDisableAfterConsecutiveFailures` times in a row has its pending rows paused for `CircuitCooldownSeconds` (log entry `... failed N times in a row; M pending delivery(ies) paused ...`); it resumes automatically.
- Nodes must be NTP-synchronised: outbox times come from the node clock.

### 4.af MSSQL change tracking watermark (WP-ARC-A-11)

- `autheris_cdc_watermark_expired_total` increasing: the stored watermark is below `CHANGE_TRACKING_MIN_VALID_VERSION`; polling of that table stops (with backoff) until an operator deletes its row in `autheris_cdc_watermarks`, accepting the gap. Procedure: F-CDC-02, section 3.3.
- `autheris_cdc_watermark_fenced_total` increasing without a lease handover: the fence counter of the cluster-state store was reset; verify AOF persistence.

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
kubectl logs -l app=gql-gateway -n data-governance | grep -i "INSECURE GETTING-STARTED CONFIGURATION DETECTED"
```
If any pod logs `INSECURE GETTING-STARTED CONFIGURATION DETECTED` (the banner also lists the active bypasses), immediately file a Priority-1 security incident and revert the configuration.

---

## 9. Performance Benchmarks, Sizing & Capacity Planning

### 9.1 Sizing Guidelines & Node Baselines

Based on production load tests (Hetzner Dedicated AX-series, AMD EPYC / Ryzen 9, NVMe, 10 Gbps uplinks), the following profiles represent recommended production baselines:

| Workload Tier | Concurrent Clients | Target RPS | Recommended Pod Resources | Replica Count |
| :--- | :--- | :--- | :--- | :--- |
| **Small / Edge** | 50 – 200 | 1,000 – 3,000 | 1 vCPU, 1 GB RAM | 2 (HA) |
| **Standard Enterprise** | 500 – 2,000 | 5,000 – 15,000 | 2 – 4 vCPU, 2 – 4 GB RAM | 3 – 5 |
| **High-Throughput Analytics** | 2,000 – 10,000 | 20,000 – 50,000+ | 8 vCPU, 8 – 16 GB RAM | 5 – 10 (HPA) |

> **Replica count:** more than one replica requires `GovernanceDb:Provider = PostgreSql` (SQLite is rejected at startup) and, outside Development, `Caching:Redis:Enabled = true` for cluster-wide cache/epoch and token-revocation propagation. The PostgreSQL connection must use TLS with certificate verification (`SSL Mode=VerifyFull` or `VerifyCA`; DEP-7) and a dedicated account; apply the schema with `GovernanceDb:MigrationConnectionString` and give the runtime role only the rights it needs (INSERT/SELECT on `AUDIT_LOG_ENTRIES` plus `USAGE` on its sequence; no UPDATE/DELETE/TRUNCATE).

### 9.2 Latency Budgets & SLA Targets

* **L1 Cache Hit (In-Memory Policy):** P50 < 0.2 ms, P99 < 1.0 ms
* **L2 Cache Hit (Redis Roundtrip):** P50 < 1.5 ms, P99 < 4.0 ms
* **End-to-End GraphQL Request (with RLS Pushdown):** P50 < 4.0 ms, P95 < 9.0 ms, P99 < 15.0 ms
* **Arrow Flight SQL Stream (Chunk Egress):** 250 MB/s per core sustained zero-copy throughput

### 9.3 Runtime & Kestrel Concurrency Configuration

For high-throughput environments, configure ASP.NET Core Kestrel limits in `appsettings.json` under `Gateway:Hosting`:
```json
"Gateway": {
  "Hosting": {
    "MaxRequestBodySizeBytes": 2097152,
    "MaxConcurrentConnections": 10000,
    "MaxConcurrentUpgradedConnections": 2000,
    "AllowedHosts": [ "gateway.corp.example" ]
  }
}
```
* **Host allow-list (`Gateway:Hosting:AllowedHosts`):** Required outside Development; the gateway refuses to start when the list is empty or contains the bare wildcard `*`. Entries are an exact host (`gateway.corp.example`), `host:port` (that port only) or `*.suffix` (sub-domains only). The `Host` header (the forwarded host when `Gateway:ReverseProxy:Enabled` is true) must match an entry, otherwise the request is answered with `400`. `/health*` is exempt so probes that send the pod IP as `Host` keep working. The CSRF `Origin`/`Referer` check uses the same list (in addition to `Gateway:GraphQL:TrustedOrigins`); `Origin == Host` alone is not trusted. In Development an empty list accepts loopback names only (`localhost`, `127.0.0.1`, `[::1]`, `*.localhost`); other host names such as a compose service name must be listed. Helm: `gateway.allowedHosts` (defaults to the ingress hosts); environment variable form: `Gateway__Hosting__AllowedHosts__0`.
* **Garbage Collection:** Ensure Server GC is enabled in container runtime (`DOTNET_gcServer=1`).
* **Connection Pooling:** Set upstream database `MaxPoolSize` according to pod replica count to avoid exhausting database connection pools (`MaxPoolSize=100` per pod on typical setups).
* **Redis Sizing:** Maintain an active connection pool with `abortConnect=false` and keep-alive pings enabled; L2 policy cache size rarely exceeds 200 MB even with 50,000 active consents.


## 10. CDC Webhook Subscriptions: Key Provisioning and Rotation

CDC webhook delivery is enabled by default (`Gateway:CdcWebhooks:Enabled`; changing it requires a restart, the registration-time value is the only one that counts). Subscription signing secrets and custom header values are stored encrypted (AES-256-GCM) with a key-encryption key (KEK):

* **Provisioning:** provide the secret `cdc-webhook-kek` of at least 32 bytes through the configured secret provider (environment variable `CDC_WEBHOOK_KEK`, a mounted `file:` secret or the vault). Without it, outside Development, `POST /api/v1/cdc/subscriptions` answers `503 CDC_SECRET_STORE_UNAVAILABLE` and the gateway logs a startup warning. Subscriptions are also rejected with `400 CDC_SECRET_REQUIRED` unless they carry a signing secret of 32 to 512 characters.
* **Rotation:** v1 ciphertexts carry no key id. Provision the new KEK, restart the gateway, then delete and re-create every subscription (existing ones can no longer be decrypted and their deliveries are dead-lettered). All replicas must use the same KEK.
* **Creator snapshot:** a subscription delivers events as the principal that created it (roles and groups are frozen at creation, `Gateway:CdcWebhooks:CreatorSnapshotMaxAgeDays`, default 90, then delivery stops). Delete and re-create it after the creator's group membership changes.
