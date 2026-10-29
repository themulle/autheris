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

### 4.ac Governance schema upgrades (WP-ARC-B-12, issue #388)

- **Base and upgrade path.** The governance schema is versioned in `autheris_schema_migrations`. **v1.5.0 is the oldest supported base** (migration `0001`, frozen). A database at v1.5.0 or newer upgrades automatically to the schema of the running build by ordered, forward-only steps (`0002`, `0003`, ...). The release notes list the steps a release adds (`Governance schema: steps NNNN-MMMM`) and flag any non-additive step.
- **Who applies.** Default (`GovernanceDb:Migrations:Mode=Apply`): the first start of a new build applies pending steps under the cross-replica schema lock with the runtime login (or with `MigrationConnectionString` when set); later replicas wait up to `Migrations:LockTimeoutSeconds`, find nothing pending and only verify. Talos or a pre-deployment job: run `Autheris.Api --migrate-governance` (always applies, ignores `Mode`, uses `MigrationConnectionString` when set) and start the application with `Mode=Verify`; an application that starts before the job ran fails closed with `GOVERNANCE_SCHEMA_PENDING`. `Autheris.Api --verify-governance` checks without any DDL or write (same checks and codes as `Verify`). Both commands exit `0` on success and `1` on failure.
- **Upgrade procedure.** (1) Back up the governance database. (2) Run `--migrate-governance` (or start the new build in `Apply`). (3) On failure read the error code on stderr / the critical log entry `Governance database migration or initialization failed`; a failed step is rolled back completely (PostgreSQL and SQL Server run each step in its own transaction, SQLite all pending steps in one), earlier committed steps stay and a rerun continues after them. (4) Start the new build; `Verify` must pass.
- **Error codes** (full table in the [configuration guide](configuration-guide.md)): `GOVERNANCE_SCHEMA_PENDING` (Verify, run the job), `GOVERNANCE_SCHEMA_UNSUPPORTED_BASE`, `GOVERNANCE_SCHEMA_DRIFT`, `GOVERNANCE_SCHEMA_CHECKSUM_MISMATCH`, `GOVERNANCE_SCHEMA_HISTORY_GAP` / `_DUPLICATE` / `_RENAMED`, `GOVERNANCE_SCHEMA_NEWER_THAN_BUILD`, `GOVERNANCE_MIGRATION_STEP_FAILED`, `GOVERNANCE_MIGRATION_LOCK_TIMEOUT`. Error texts carry the code, engine, database name, versions, step and table/column names only, never SQL text, row values or connection details.
- **Locks and timeouts.** `Migrations:LockTimeoutSeconds` (default 300, 10-3600) bounds opening the connection and acquiring the schema lock; exceeding it fails with `GOVERNANCE_MIGRATION_LOCK_TIMEOUT` and nothing is changed (the orchestrator restarts the pod; a killed migrator never leaves a stale lock). Inside a step, `Migrations:StepLockTimeoutSeconds` (default 30) and, on PostgreSQL, `Migrations:StepStatementTimeoutSeconds` (default 900) roll the step back when exceeded. `Verify` takes no lock.
- **Database older than v1.5.0.** Such a database (or any foreign schema in the governance tables) is not upgraded. Startup and both jobs fail with `GOVERNANCE_SCHEMA_UNSUPPORTED_BASE` and a drift report, **before any statement runs** (not even the history table is created). The caller loads the data into a database created by v1.5.0 or newer (reinstall), or applies the DDL of that version itself and restores the matching backup. Do not edit `autheris_schema_migrations` to make the check pass: checksums and the shape of every applied version are verified.
- **Least privilege.** Without `MigrationConnectionString` the runtime login holds DDL rights and can disable the append-only audit trigger. In `Production` on PostgreSQL / SQL Server `Apply` then logs `GOVERNANCE_MIGRATION_RUNTIME_DDL` once per start and records an audit event after a successful apply (best effort). Prefer a separate migration login that owns the governance objects (steps `ALTER` existing tables) and a runtime login with `Mode=Verify`. In `Verify` the runtime login is checked: write access to the history or DDL rights logs `GOVERNANCE_RUNTIME_LOGIN_PRIVILEGED`; `Migrations:RequireLeastPrivilege=true` turns it into a startup failure. Skipped on SQLite.
- **Reading the drift report.** Schema drift (`GOVERNANCE_SCHEMA_DRIFT` / `_UNSUPPORTED_BASE`) logs `Governance schema drift on <engine> database '<name>': the schema does not match this build (applied migrations: ...; pending: ...)` followed by at most 50 lines `<kind> <table>.<name> (expected <type>; found <type>)` plus `... and N more`. Kinds: `missing table`, `missing column`, `wrong type`, `wrong nullability`, `wrong primary key`, `missing index`, `wrong index`. Extra tables, columns and indexes are tolerated. The report compares the database with the expected shape of the highest **applied** version: it names what a manual change, a restore of an older backup or a pre-v1.5.0 shape broke. Fix by restoring the matching backup (or reverting the manual change); never by editing the history ([F-GOV-12](features/f-gov-12-multi-engine-governance-storage.md#schema-shape-verification)).
- Unavailable tables: a table whose persisted key or index definition is invalid is logged as a Warning and treated as unavailable (404 on reads, skipped by catalog syncs). Fix or delete its rows in `TABLE_PRIMARY_KEY_COLUMNS`, `TABLE_INDEXES` and `TABLE_INDEX_COLUMNS` ([F-GOV-21](features/f-gov-21-declared-table-keys-and-read-ordering.md#22-fail-closed-on-an-invalid-persisted-definition)). Direct loads into these tables bypass the governance epoch; load before start or bump the epoch.
- WebSQL `504` (`urn:autheris:problem:websql-execution-timeout`): usually a user `ORDER BY` on an unindexed column of a large table. Add an index on the ordering column in the source or raise `WebSql:ExecutionTimeoutSeconds` ([F-DATA-02](features/f-data-02-governed-websql.md#44-execution-timeouts)).
- Separate pipelines: set `GovernanceDb:Migrations:Mode=Verify` on the application and run `Autheris.Api --migrate-governance` as a pre-deployment job (with `GovernanceDb:MigrationConnectionString` for a DDL-capable role that owns the governance objects). The command exits `0` on success and `1` on failure; it applies whatever `Mode` is set to.
- Helm: `governanceMigrationJob.enabled=true` renders this as a `pre-install,pre-upgrade` hook Job (`<release>-autheris-migrate`, `Migrations:Mode=Apply`) and sets the application to `Migrations:Mode=Verify`. The job runs with the same configuration and secrets as the application (governance connection string, audit and masking keys, Redis); set `governanceMigrationJob.migrationConnectionStringSecretKey` to a key of `gateway.governanceDb.existingSecret` that holds a DDL-capable connection string. It is not supported for the SQLite provider. A failed job (`backoffLimit: 0`) aborts the release before any application pod is replaced; read the job pod log for the migration error.
- Migrations are forward-only. Rolling back to an older build means restoring a database backup taken before the upgrade. An older build refuses to start against a newer schema (`GOVERNANCE_SCHEMA_NEWER_THAN_BUILD`) unless `GovernanceDb:Migrations:AllowNewerSchema=true`, which is only safe after purely additive steps: it logs `GOVERNANCE_SCHEMA_NEWER_ALLOWED` per start, runs no DDL and still verifies the history, checksums and shape of every step the older build knows.

### 4.ad State directory (SQLite governance provider, WP-ARC-B-13)

- `Gateway:State:StateDirectory` (default `./state`; the container images set `/app/data`) is the one writable location for local state. With the SQLite provider and no `GovernanceDb:ConnectionString` it holds `governance.db`; Development and Test without a connection string use a private in-memory database instead. An explicitly configured connection string always wins, and an in-memory database is rejected outside Development.
- Outside Development/Test the audit chain anchor must be configured explicitly (`Audit:ChainAnchorPath` on a separately protected volume, `Audit:ChainAnchorWormDirectory` or a KMS signer); the gateway refuses to start otherwise. Only in Development/Test the anchor defaults to `<governance database file>.audit-anchor.json` next to the database.
- Backup set: back up `governance.db` together with its `-wal` and `-shm` side files (or checkpoint first) and the audit anchor file (`Audit:ChainAnchorPath`) as one unit. Restoring the database without the matching anchor makes the audit chain verification report a violation.
- In a container the directory must be an absolute path on a mounted volume and not `/app`; the gateway refuses to start otherwise. `Cache=Shared` is ignored for file databases (connections are pooled per operation); WAL is the default journal mode, use `GovernanceDb:JournalMode=DELETE` on filesystems without shared-memory support (for example WSL bind mounts or network shares).

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

### 4.ag Async query jobs on several replicas (WP-ARC-A-12)

Reference: F-DATA-07, section 6.

- **Choose one result-routing model.** A result file exists only on the replica that ran the job unless the scratch directory is shared.
  - *Sticky routing* (default Helm layout, node-local `/tmp/autheris-jobs`): configure session affinity on the ingress or load balancer (for example cookie affinity) so a client reaches the same replica for submit, status and download. Without it, downloads of another replica's job answer `503` with `ASYNC_JOB_RESULT_NODE_LOCAL`.
  - *Shared volume*: mount one ReadWriteMany volume (NFS, Azure Files, CephFS) at the same path on every replica and set `Gateway__AsyncJobs__ScratchDirectory` to that path and `Gateway__AsyncJobs__SharedScratchDirectory=true`. The chart does not provision this volume; add it through your own overlay. Restrict the volume to the gateway pods: it holds governed result files of all tenants.
- **Size the scratch volume.** Results are kept for `RetentionHours` (default 24). Set `tmpVolume.sizeLimit` for the node-local layout; a full volume makes jobs fail.
- **Capacity limits are per node.** `MaxConcurrentJobs`, `MaxConcurrentJobsPerTenant` and `MaxQueuedJobsPerTenant` apply to each replica; the cluster-wide limit is the per-node value times the replica count. Clients receive `429` (`Retry-After: 30`) at the cap.
- **Cluster state outage.** Submits, and status/result/cancel requests for jobs of other replicas, answer `503` (`Retry-After: 5`). Restore Redis/Garnet; no manual job cleanup is needed.
- **Node loss and rolling restarts.** Jobs are not resumed. Queued jobs of a stopping pod end `Cancelled` (`autheris_asyncjobs_discarded`); jobs of a crashed pod are reported `Failed` with `job owner node lost` once `MaxTimeoutSeconds + 60 s` have passed since submission. Clients resubmit. Leftover result files are removed by the startup sweeper and the purge loop.
- **Watch:** `autheris_asyncjobs_running`, `autheris_asyncjobs_pending` (sustained growth means too few slots), `autheris_asyncjobs_cluster_write_failures`, `autheris_asyncjobs_tombstone_check_failures` and `autheris_asyncjobs_subscribe_failures` (remote cancels then take effect only at the owner's next state change).

### 4.ah Casbin policy file distribution and the policy epoch (WP-ARC-A-14)

Reference: F-SEC-06.

- **Every replica must load the same policy file.** Distribute `Gateway:Casbin:PolicyPath` (and tenant policy files) identically to all pods, for example as one ConfigMap or a shared read-only volume, and keep `WatchPolicyFile` enabled. After a change on any node, every replica re-reads its *own* file, so a replica with an outdated file keeps enforcing the outdated policy.
- **Do not rely on programmatic rule changes in multi-replica deployments.** Rules added at runtime through `AddPolicy` and related APIs exist only on the node that received them; the other replicas only discard their cached decisions. Persist rule changes in the policy file.
- **Staleness.** After a change, other replicas stop serving cached decisions of the old policy within `min(TtlSeconds, EpochCheckSeconds + reload time)` (defaults 60 s and 5 s). Lower `Gateway:Casbin:DecisionCache:EpochCheckSeconds` for a tighter bound.
- **Propagation failures.** `autheris_policy_epoch_propagation_failed_total` increasing: the changing node could not increment `policy:epoch:{tenant}` or `policy:epoch:*`; other replicas converge only after `TtlSeconds`. Check the cluster state store (`autheris_cluster_state_failure_total{operation="increment"}`).
- **Cluster state outage.** The decision cache is bypassed on affected tenants (warning `Casbin policy epoch could not be read; decision cache bypassed.`). Decisions stay correct; expect higher policy evaluation latency until the store is back.
- **Reload failures.** A missing or invalid policy file during a cluster-triggered reload keeps the last-known-good policy set and is retried at the next epoch check; check the gateway log for `Casbin policy reload after a cluster epoch change failed` or `policy file ... is missing`.

### 4.ai Decision cache kill switch (shape-shared decisions)

By default, users that match exactly the same Casbin rules share one cached decision when no candidate rule reads a user-specific input ([F-SEC-06, section 2.2a](features/f-sec-06-casbin-decision-cache-policy-epoch.md)). If a shared decision is suspected to be wrong for a user (for example after adding a rule whose `sub_rule` uses an unusual construct):

1. Set `Gateway:Casbin:DecisionCache:ShareAcrossUsers=false` (environment variable `Gateway__Casbin__DecisionCache__ShareAcrossUsers=false`) and roll the replicas. Every decision is keyed by user again; nothing else changes.
2. Expect a lower hit ratio and more `sub_rule` evaluations per request until the cache warms up per user. Keep `Capacity` at the default `100000` or raise it.
3. Re-enable sharing after the rule set was corrected. Decisions for delegated sources are never shared, regardless of the switch.

A full decision cache flush is a policy publish or a restart; there is no flush endpoint.

### 4.aj Audit group commit

Required audit writes (administrative changes, security events, denials and allow records written through `IAuditWriter.RecordRequiredAsync`) are coalesced by `AuditGroupCommitSink` into batches of up to 64 entries (5 ms coalescing window, 4,096 pending entries before back-pressure), each written in one transaction with one chain-tail read and one anchor ([F-AUDIT-01, section 10](features/f-audit-01-hardened-cryptographic-audit.md)).

- **Latency:** a required write may wait up to the coalescing window plus one batch commit. Callers block until their batch is committed (and, for Tier-A entries, anchored).
- **Anchor failures:** when the anchor of a batch with a Tier-A entry cannot be persisted, the rows are committed but the callers get `503` (`AuditUnavailableException`). Check the anchor store (file path, WORM directory, KMS signer) and `autheris_audit_write_failed_total{tier="required"}`. The next successful batch anchors the tail; chain verification (section 4.2) stays valid.
- **Back-pressure:** when the governance database is slow, pending entries fill the channel and callers wait instead of losing entries. Watch governance DB latency and connection pool usage before raising replica counts.

### 4.ak Individual grant expiry and clamped grants

Individual grants are time-bound ([F-GOV-22](features/f-gov-22-time-bound-individual-grants.md)). Existing individual grants with no or an over-long expiry are clamped at read time, and grants older than the class maximum become inactive immediately after an upgrade.

- **Before an upgrade or a change of `Gateway:Governance:IndividualGrants`:** list the affected grants with `GET /api/v1/governance/grants?clamped=true&includeInactive=true` (paged with `cursor`), and plan renewals with the data owners.
- **After a user reports lost access:** check the grant in `GET /api/v1/governance/grants?grantee=<sid>&includeInactive=true` (`effectiveValidTo`, `clamped`, `isActive`). Restore access through a renewal by an owner or delegate, or convert recurring needs into a group/role mapping. The grantee cannot renew its own grant.
- **`Unmasked` profiles:** an `Unmasked` assignment is effective only as an approved group/role mapping, a four-eyes individual grant (≤ 30 days) or a break-glass exception (≤ 72 hours, approved within 24 hours). Unapproved assignments are ignored, so masking stays on.

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


### 9.4 Sizing for 5,000 Named Users

Reference workload: 5,000 named users (plus about 50 service principals), 10% concurrently active, 1 request per 10 seconds per active user (about 50 rps steady), a peak factor of 10 plus about 100 rps of agents, ETL and MCP, giving a **design peak of about 600 rps cluster-wide** with about 2.5 tables per request; 20 to 50 group SIDs per token; 2,000 to 10,000 governed tables; 500 to 5,000 Casbin lines.

| Resource | Recommendation |
| :--- | :--- |
| Gateway replicas | **3 × 4 vCPU / 8 GB** (2 would carry the load; 3 for HA). Expected hot-path CPU about 1.5 to 3 cores cluster-wide. |
| Gateway memory | About 1.5 to 2.5 GB steady per replica with the bounded caches. Keep `Caching:L1MemoryCache:SizeLimitMb` at 512; leave headroom for buffered exports. |
| Casbin decision cache | `Capacity` 100,000 (default) and `ShareAcrossUsers=true`. With mostly group-based rules the working set is per rule shape, not per user. |
| Governance database | PostgreSQL (or SQL Server) with the consent indexes of the baseline ([F-GOV-12](features/f-gov-12-multi-engine-governance-storage.md)). Default pool of 100 connections per replica; verify `replicas × pool ≤ max_connections − headroom`. Audit load is about 5 to 10 batch commits per second at peak instead of one round trip per row. |
| Redis | Standalone or Redis Cluster (epoch reads are single-key and Cluster-safe). About 2,000 ops/s at peak; the 150 ms staleness budget saves one round trip per request for non-sensitive tables. Enable AOF persistence (F-OPS-02). |
| Basic-auth clients | Set `Authentication:BasicAuth:MarkerKeyRef` so BI tools pay the password hash once per cluster, not once per replica. |
| Source databases | Size `Max Pool Size` per source as source capacity divided by the replica count; concurrency per node is bounded by the Interactive resource group (50). |
| Async jobs | Per-node limits apply (`MaxConcurrentJobs` etc., section 4.ag); raise them per node size and use a shared scratch volume when exports are common. |

Bounded in-process caches (BitFaster `ConcurrentLru`, eviction only produces a recomputation from the authoritative source):

| Cache | Bound |
| :--- | :--- |
| Mandatory row filter memo (virtual filters) | 50,000 entries; object patterns 10,000 |
| Repository table metadata (all engines) | 20,000 tables |
| Casbin candidate rules per (tenant, table, action) | 20,000 |
| Policy shape analysis | 20,000 rules |
| Compiled `sub_rule` delegates | 20,000 expressions |
| Governed SQL stage-1 analyses | 4,096 statements |
| Masking regular expressions | 2,000 patterns |

Validate a deployment with a load test at the design peak before go-live (ramp to 600 rps, then 1.5× for headroom) and watch, in this order: audit write latency, decision cache hit ratio, consent cache misses after epoch bumps, gateway memory, and thread-pool queue length.

## 10. CDC Webhook Subscriptions: Key Provisioning and Rotation

CDC webhook delivery is enabled by default (`Gateway:CdcWebhooks:Enabled`; changing it requires a restart, the registration-time value is the only one that counts). Subscription signing secrets and custom header values are stored encrypted (AES-256-GCM) with a key-encryption key (KEK):

* **Provisioning:** provide the secret `cdc-webhook-kek` of at least 32 bytes through the configured secret provider (environment variable `CDC_WEBHOOK_KEK`, a mounted `file:` secret or the vault). Without it, outside Development, `POST /api/v1/cdc/subscriptions` answers `503 CDC_SECRET_STORE_UNAVAILABLE` and the gateway logs a startup warning. Subscriptions are also rejected with `400 CDC_SECRET_REQUIRED` unless they carry a signing secret of 32 to 512 characters.
* **Rotation:** v1 ciphertexts carry no key id. Provision the new KEK, restart the gateway, then delete and re-create every subscription (existing ones can no longer be decrypted and their deliveries are dead-lettered). All replicas must use the same KEK.
* **Creator snapshot:** a subscription delivers events as the principal that created it (roles and groups are frozen at creation, `Gateway:CdcWebhooks:CreatorSnapshotMaxAgeDays`, default 90, then delivery stops). Delete and re-create it after the creator's group membership changes.
