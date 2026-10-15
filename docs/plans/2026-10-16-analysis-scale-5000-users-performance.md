# Analysis: Runtime Performance & Scaling at 5,000 Users

**ID:** `ANALYSIS-SCALE-5000-PERF` · **Date:** 2026-10-16 · **Branch:** `feat/scale-5000-users` (base `origin/main` `e8c86435`)
**Role:** Performance Expert · **Companion:** the architect's governability analysis on the same branch (manageability of users, tables and rules).
**Scope:** Runtime hot path, caches and memory, concurrency and horizontal scaling, and a capacity estimate for 5,000 named users with many tables and access rules. The analysis does not change any code.

All paths are relative to `src/` unless stated otherwise. Line numbers refer to `e8c86435`.

---

## 0. Executive Summary

Autheris can serve 5,000 users (about 500 concurrently active, about 600 requests per second at peak) on 3 to 4 replicas. Several changes are needed first. Raw CPU is not the first limit. The first limits are:

1. **Audit writes are serialized** across the whole cluster (one process lock plus one global DB advisory lock, one round trip per row). This is the first throughput ceiling, and fail-closed audit turns it into request failures.
2. **Cache keys depend on the user, and some caches are unbounded or too small.** The Casbin decision cache (10k LRU, keyed per user, client IP and column set), the virtual-filter memo (unbounded) and the compile template cache (512 entries) all have this problem. A cache miss on the Casbin path builds a **new DynamicExpresso interpreter per sub_rule evaluation**. Measured cost: **~547 µs and ~48 KB allocated per evaluation**. A cached delegate costs **~12 ns**.
3. **Data structures that grow with the number of users are read in full on the hot path.** These are the consent rows of a table on every consent miss, the whole ReBAC tenant hash (`HGETALL`) on every ReBAC miss, and the whole active catalog (`GetAllTablesAsync`) on every OData request.
4. **Some defaults break in a cluster or at company scale.** Epoch MGET fails with CROSSSLOT on Redis Cluster, which fails closed. The CDC subscription cap is counted per tenant but compared with the per-subject limit of 10. The pre-auth IP limit of 100/min also applies to Basic-auth clients behind NAT. Each replica keeps its own 30 s Basic-auth success cache in front of a PBKDF2 hash that costs 184 ms.

Redis throughput, governance-DB connection pools and source pooling are **not** first-order bottlenecks at this scale. They only need configuration (see section 3).

---

## 1. Assumptions & Workload Model

| Parameter | Assumption | Rationale |
|---|---|---|
| Named users | 5,000 (plus about 50 technical/service principals) | Owner statement |
| Concurrently active | 10% = 500 | Owner statement |
| Average rate per active user | 1 request / 10 s, which gives about 50 rps steady | Interactive BI, WebSQL and REST mix |
| Peak factor | ×10 (Monday-morning dashboard loads of 10 to 20 queries each), plus 100 rps of agents, ETL and MCP | Typical BI behaviour |
| **Design peak** | **~600 rps cluster-wide**, about 2.5 tables per request | Target for the load test |
| Groups per user (token) | 20 to 50 group SIDs | Typical AD/Entra tenants |
| Catalog | 2,000 to 10,000 governed tables, 20 to 200 tables touched per user | "Many tables" |
| Rules | 500 to 5,000 Casbin lines, mostly group-based. 10 to 30% carry an ABAC `sub_rule`. Consents per table range from a few (group grants) to about 5,000 (per-user grants) | Worst case is per-user consents |
| Replicas | 3 × 4 vCPU / 8 GB, with Redis (standalone or Cluster) and a PostgreSQL governance store | Common deployment |
| Network RTT | Gateway to PG/Redis 0.3 to 1 ms (same DC or cross-AZ) | |

---

## 2. Measurements (this analysis)

Machine: AMD Ryzen 7 8840U, 16 logical cores, WSL2, .NET 10, Release build. These are single-thread micro-measurements, used here as orders of magnitude.

| Measurement | Result | Source |
|---|---|---|
| Governed AST compile, **template cache hit** | **13.5 µs** | `benchmarks/Autheris.Benchmarks -- compile` (`CompilePathBenchmark`) |
| Governed AST compile, **cache miss** (small query) | **0.59 ms** | same |
| G8 allocation per compile (frozen baseline) | 127 KB (0.5 KB SQL), 1.04 MB (4 KB SQL), 16.2 MB (64 KB SQL) | `benchmarks/baselines/sql-compiler-legacy.json` |
| `EvaluateSubRule` as implemented: new `DynamicExpresso.Interpreter` + `Regex.Replace` + `Eval` per call | **~547 µs/op, ~48 KB allocated/op** | scratch micro-benchmark replicating `Application/Governance/CasbinEnforcementService.SubjectAttributes.cs:39-66` (DynamicExpresso 2.16.1) |
| Same rule parsed once and invoked as a cached typed delegate | **~12 ns/op** (about 45,000× faster) | same micro-benchmark |
| PBKDF2-HMAC-SHA256, 600,000 iterations (Basic-auth default) | **184 ms/op** of one core | scratch micro-benchmark, `Api/Security/PasswordHasher.cs:14` |

Nothing else in this document was measured. Numbers outside this table are estimates derived from the code paths and the assumptions in section 1. The load-test plan in section 6 is designed to confirm or refute them.

---

## 3. Ranked Bottlenecks

Severity meanings:
- **Blocker:** fails or degrades sharply at the design peak, or under a common deployment option.
- **High:** a large cost, or one that grows without bound with users × tables.
- **Medium:** worth fixing, but not the first limit.

Effort: **S** ≤ 2 days, **M** ≤ 1 week, **L** > 1 week.

### B-1 (Blocker): Audit writes serialized cluster-wide, one round trip per row

- **Evidence:**
  - All audit writes take one process-wide `SemaphoreSlim _auditLock = new(1,1)`: `Infrastructure/Persistence/PostgreSqlGovernanceRepository.cs:49` (SQL Server: `SqlServerGovernanceRepository.cs:27`). It is taken in `.Audit.cs:39, 112, 168, 183, 224, 273, 294`.
  - Each batch first takes the cluster-wide `SELECT pg_advisory_xact_lock(@k)` with a constant key (`.Audit.cs:447, 540`; SQL Server uses `sp_getapplock`).
  - It then reads the chain tail with a **synchronous** `ReadAuditTail(tx)` (`.Audit.cs:452, 548, 980-1000`).
  - It inserts **one row per `ExecuteNonQueryAsync`** (`.Audit.cs:585-624`).
  - Finally it reads and writes the HMAC anchor **synchronously** under `lock(_ioLock)` plus a `.lock` file (`.Audit.cs:638-653`; `AuditChainAnchorStores.cs:17-90`).
  - Tier-A events (every DENY, and every event type other than `TABLE_QUERY`/`WEBSQL_QUERY`; `AuditChainViolationGate.cs:23-29`) are written synchronously, one transaction each.
  - Tier-B events go through a bounded channel of 5,000 with `FullMode=Wait`. A request whose write waits more than 2 s fails with `AuditUnavailableException` (`.Audit.cs:68-78`; `PostgreSqlGovernanceRepository.cs:134-140`).
  - `AuditGroupCommitSink` (64/batch, 5 ms window) is registered (`Api/Extensions/DependencyInjection/GatewayGovernanceServiceExtensions.cs:54`) but **has no consumers**.
  - WebSQL writes `WEBSQL_QUERY` (`Application/Sql/Services/GovernedSqlExecutor.cs:343-352`), and `AccessAuditMiddleware` writes one more event per request (`Api/Middleware/AccessAuditMiddleware.cs:150-211`).
- **Estimate:**
  - Volume: 1 to 2 audit rows per request gives **600 to 1,200 rows/s at peak**.
  - Tier-B ceiling: about 0.4 to 1 ms per row → **~1,000 to 2,500 rows/s, cluster-wide**. Adding replicas does not raise it, because of the global advisory lock.
  - Tier-A ceiling: about 3 to 5 ms per event (lock + tail + insert + commit + anchor file) → **~200 to 300 events/s cluster-wide**.
  - Bursts of 401/403/429 DENY events, metadata or admin endpoints (Tier A), and GC or network jitter push the channel into `Wait`, and then into 2 s timeouts.
- **Consequence:** Requests fail fail-closed (`AuditUnavailableException`, faulted pipeline after 3 failures). The audit lag grows. This is the **first hard limit** and cannot be fixed by adding replicas.
- **Change:**
  1. Write each batch as **one multi-row statement**: PG `INSERT … SELECT FROM unnest(...)` or binary `COPY`; SQL Server TVP or `SqlBulkCopy`. Compute the hashes in memory from the tail that was read once. This brings a 250-row batch down to about 3 round trips. **S–M**
  2. Make the tail read and the anchor I/O asynchronous. Move the anchor write off the commit path, signing every N batches or every T ms. Keep fail-closed by not acknowledging Tier-A events until the anchor is durable. **S**
  3. Route Tier-A events through the group-commit sink (wire up the dead `AuditGroupCommitSink`): concurrent writers wait on one shared commit instead of one transaction each. **S**
  4. **Partitioned chains:** one hash chain per replica (or per tenant), each anchored independently, with the global advisory lock replaced by a per-chain lock. This makes audit throughput grow with the number of replicas. The verifier walks N chains. **M–L**. Needs security review, because it changes the WORM evidence model.
  5. Make the duplicate middleware and executor events into a single event per request, or mark one as derived. **S**

### B-2 (Blocker on Redis Cluster): Epoch MGET triggers CROSSSLOT, and every consent-cache entry is treated as stale

- **Evidence:**
  - `Infrastructure/Cache/EpochValidationService.cs:308-314` builds `epoch:{table}` keys with **no hash tag** and calls `db.StringGetAsync(redisKeys)` (MGET).
  - It is enabled by default: `PipelinedMGetEnabled = true` (`Domain/Options/Storage/RedisOptions.cs:142`).
  - The `catch` at `:412-418` marks all pending tables invalid.
  - The same codebase already avoids MGET for this reason in `Infrastructure/Security/RedisTokenRevocationService.cs:139-140`.
- **Consequence:** On a sharded Redis, every multi-table consent validation fails. Each request falls back to the DB consent path (B-5), so the governance DB load is multiplied by the number of tables per request. Correctness is preserved (fail-closed), but performance collapses.
- **Change:**
  - Use per-key pipelined GETs (as the revocation service does), or
  - group the keys by hash slot (`{tenant}` hash tag) and run one MGET per slot.

  Either way, add a Redis Cluster integration test. **S**

### B-3 (Blocker for CDC adoption): Cluster subscription cap is counted per tenant but compared with the per-subject limit

- **Evidence:** `GraphQL/Subscriptions/CdcSubscriptionGovernor.cs:142` uses `clusterKey = $"cdc:subs:{tenant.Value}"`, and `:164` compares it with `max = MaxSubscriptionsPerSubject` (default 10, `Domain/Options/Protocols/GraphQlOptions.cs:38`; `:129`).
- **Consequence:** In cluster mode, a tenant of 5,000 users can hold **10 CDC subscriptions in total**.
- **Change:** Key the counter per subject (`cdc:subs:{tenant}:{sid}`, hash-tagged). Add a separate per-tenant cap (`MaxSubscriptionsPerTenant`, default for example 2,000). **S**

### H-1 (High): Casbin decision path, with interpreter-per-evaluation and per-user cache keys

- **Evidence:**
  - **Interpreter per call:** `Application/Governance/CasbinEnforcementService.SubjectAttributes.cs:39-66` creates a new `DynamicExpresso.Interpreter` and runs `Regex.Replace` on **each** sub_rule evaluation.
  - **Linear scan on every miss:** the decision path scans the rules (`CasbinEnforcementService.Columns.cs:338-490`). It copies the rule list into a new `List` on each miss (`:355-364`), then runs a deny pass and an allow pass over rules × subjects, where subjects = user + all group SIDs (`:369-425`). The Casbin `Enforcer` objects are built but do not decide.
  - **Ad hoc regex for object patterns:** wildcard and `:param` patterns are matched by building a regex string per call and calling the static `Regex.IsMatch` (`CasbinEnforcementService.Masking.cs:188-262`). .NET's static regex cache holds 15 entries by default.
  - **Decision cache:** `Application/Governance/Caching/CasbinDecisionCache.cs` holds 10,000 entries in an LRU guarded by one global `lock(_gate)` (`:23, 46, 55, 78, 99`). Hits also take the lock. TTL is 60 s (`Domain/Options/CasbinDecisionCacheOptions.cs:17-23`).
  - **Decision key:** `DecisionKey.cs:28-80` includes the epoch, tenant, **UserSid**, table, action, purpose, **ClientIp**, dialect, sorted groups, attributes, **requested columns** and table columns.
  - **Full clear on any change:** `_decisionCache.Clear()` runs for any policy change in any tenant (`CasbinEnforcementService.cs:382`).
  - **Uncacheable rules:** rules that reference time are never cached (`Columns.cs:376, 484`).
  - **No stampede protection:** concurrent misses each run the full evaluation.
- **Estimate:**
  - Working set: 500 active users × about 20 tables × 1 to 3 column shapes × IP changes (VPN, proxies) is about **10k to 30k live keys**, against a capacity of 10k with a 60 s TTL. Expected hit rate is **30 to 60%** (to be confirmed in LT-2).
  - Cost per miss: with 5 matching ABAC sub_rule evaluations (matching rules × matched subjects), a miss costs about **2.7 ms of CPU and 240 KB of allocation**.
  - At peak: 600 rps × 2.5 tables × 50% misses × 5 evaluations = 3,750 evaluations/s, which is about **2 cores and 180 MB/s of allocation cluster-wide** for expression interpretation alone.
  - After a policy publish, every replica re-evaluates everything at once.
- **Change:**
  1. **Compile each `sub_rule` once** when the `PolicySnapshot` is built. Use DynamicExpresso `ParseAsDelegate`/`Lambda` with typed parameters `(SecurityEvaluationContext ctx, string sub, string obj, string act)`, plus the quote normalization, stored in a `FrozenDictionary<string, Func<…>>` on the snapshot. A failed compile becomes a stored "deny / null" result, which keeps fail-closed. Measured gain: about 45,000× per evaluation. **S**
  2. Precompile object patterns into `Regex` instances (or `RegexOptions.NonBacktracking`) on the snapshot. Index rules by literal object prefix or table, so that a decision touches only candidate rules instead of all of them. **S–M**
  3. **Make the key independent of the user where the rule set allows it.** If no candidate rule references `r.sub`, the user SID or IP-dependent attributes, key on the hash of the group set plus the attribute subset that the rules actually read. Users with the same groups then share one entry. Drop `ClientIp` from the key unless an IP-dependent rule exists. **M**
  4. Replace the global-lock LRU with a sharded or lock-free cache: for example `BitFaster.Caching` `ConcurrentLru`, or `IMemoryCache` with size accounting. Raise the default capacity to 100k (about 50 to 100 MB). Add single-flight per key (`Lazy<Task>` in a `ConcurrentDictionary`). **S**
  5. On publish, invalidate per tenant (the epoch is already in the key) instead of calling a global `Clear()`. **S**

### H-2 (High): Consent cache misses read every consent of the table

- **Evidence:**
  - `Infrastructure/Persistence/PostgreSqlGovernanceRepository.Consent.cs:22-80` selects **all** unrevoked consents of the table. It joins on `LOWER(t.source_name)=LOWER(@domain) …`, which is not sargable without expression indexes.
  - It parses `valid_from`/`valid_to` **from strings** per row, and filters by subject **in C#** (`:63-66`).
  - The SQL Server and SQLite repositories follow the same pattern.
  - Before the query, `GetEpochSnapshotAsync` makes one extra Redis GET (`Application/Policy/TableAccessPolicy.Consent.cs:48` → `ConsentCacheService.cs:338`).
  - L2 misses are read with one sequential `StringGetAsync` per table (`ConsentCacheService.cs:131-136`, an N+1 pattern).
- **Estimate:** With per-user consents, a table has about 5,000 consent rows. Each miss transfers and parses about 5,000 rows (about 1 to 2 MB) and takes 5 to 20 ms of DB plus CPU time. The consent cache key is per user (`ConsentCacheService.cs:483-488`), so cold starts, epoch bumps and TTL expiry produce **one such query per user and table**. After an epoch bump on a popular table, 500 active users trigger about 500 full-table consent scans within seconds (an **invalidation storm**, made worse by the lack of single-flight).
- **Change:**
  1. Filter in SQL with `table_id = @id`, using the table id from cached metadata, and `(grantee_sid = ANY(@sids) OR grantee_type = 'Role')`, with typed timestamp columns. Add the index `(table_id, grantee_sid) WHERE is_revoked = 0`. **S–M**. The schema change goes into the baseline, per the no-upgrade-migrations decision.
  2. Read L2 in one pipelined batch, with no sequential loop. **S**
  3. Add single-flight per (tenant, table, context hash). On an epoch bump, refresh the table's consent set **once per replica** and derive per-user decisions from that in-memory set (a "table consent snapshot"). Per-user DB reads are then no longer needed. **M**

### H-3 (High): Unbounded per-user memo structures

- **Evidence:**
  - The `MandatoryRowFilterResolver` memo is an unbounded `ConcurrentDictionary` keyed by user, groups, roles, table, dialect and alternate SIDs (`Application/VirtualFilters/MandatoryRowFilterResolver.cs:179, 223-247`; `VirtualFilterMemoKey.cs:14-22`). It is cleared only on a generation change.
  - The repository metadata cache is a `ConcurrentDictionary` with no size bound (`Infrastructure/Persistence/SqlServerGovernanceRepository.cs:20, 26`; PG and SQLite are the same).
  - The masking regex cache is static and unbounded (`Application/Services/ColumnMaskingProvider.cs:188, 217`).
  - The Basic-auth success cache has no size bound (`Api/Security/BasicAuthAttemptGuard.cs:36, 281-349`).
  - `AccessProfileCache` sets `entry.Size = 1` on the same `IMemoryCache`, whose `SizeLimit` is counted in **bytes** (`Application/Policy/Services/AccessProfileCache.cs:125` vs `Api/.../GatewayStorageServiceExtensions.cs:38-40`). The size accounting is therefore wrong.
- **Estimate:** VF memo: 5,000 users × 100 tables × about 0.5 to 2 KB is **250 MB to 1 GB per replica** during a long, stable generation. The metadata cache grows with the catalog: 10k tables × about 5 to 20 KB is 50 to 200 MB. This leads to Gen2 GC pressure, and eventually OOM kills under an 8 GB container limit, together with B-5 buffering.
- **Change:**
  - Move all four caches to bounded caches (size plus sliding TTL). Fix `entry.Size` units.
  - Key the VF memo by the group/attribute hash instead of the user SID whenever the filter does not reference user-specific attributes.

  **S**

### H-4 (High, when ReBAC is enabled): ReBAC miss reads the whole tenant tuple hash

- **Evidence:**
  - `Infrastructure/Rebac/RedisRebacStore.cs:75`: `HashGetAllAsync(TenantKey(tenant))` reads and JSON-deserializes **every tuple of the tenant**, then filters in C#.
  - The evaluator calls it up to 3 times per traversal level, with depth up to 10 (`Application/Security/Rebac/Services/ZanzibarRebacEvaluator.cs:328, 354, 382`).
  - The decision cache is limited to 10k entries per tenant and is **cleared completely** when full (`:233-258`).
- **Estimate:** 100k tuples at about 200 B is **about 20 MB transferred and deserialized per call**. At 5,000 users the cache thrashes (B-1 style), so this could happen on many requests. It is unusable at this scale.
- **Change:** Index the tuples in Redis. Use one set per `(object#relation)` and per `(user)` (Zanzibar-style `SMEMBERS`/`SISMEMBER`), or a per-replica in-memory tuple index refreshed through the existing `autheris:rebac:invalidate` pub/sub. Replace clear-all with LRU eviction. **M**

### H-5 (High): Full catalog loads on request paths and periodic full scans

- **Evidence:**
  - `GetAllTablesAsync` runs on **every OData entity-set request** (`Api/Endpoints/ODataEndpoints.Query.cs:37`).
  - It also runs in OpenAPI generation, MCP catalog, `QueryAnalysisService.cs:109`, `TrinoMetadataQueryHandler.cs:86`, `ArrowFlightSqlServer.cs:125`, `SqlConnector.cs:70/76` and others.
  - The implementation always runs `SELECT … FROM TABLES WHERE is_active = 1` and then resolves metadata per table (`SqlServerGovernanceRepository.Catalog.cs:160-211`).
  - The GraphQL catalog check rebuilds the full model every 60 s on every replica (`GraphQL/Catalog/CatalogGraphQlTypeModule.cs:85, 117`).
  - The audit integrity monitor scans the **whole audit table** every 24 h on every replica, with no lease (`Infrastructure/Health/AuditChainIntegrityMonitor.cs:94`; `.Audit.cs:769-771`).
- **Estimate:**
  - OData at 100 rps with 10k tables is 100 full catalog reads/s: about 1M rows/s from the governance DB, plus about 10k-entry LINQ filtering per request.
  - Audit table: at 1 to 2 rows/request it reaches **20 to 50 M rows/month**. A daily full scan on each of 3 replicas becomes hours of I/O.
- **Change:**
  - Add an immutable `CatalogSnapshot`, versioned by the existing `ICatalogVersionProvider`, with per-tenant indexes. All `GetAllTablesAsync` call sites on request paths and timers read it. Pass the version provider to `CatalogGraphQlTypeModule`. **M**
  - Make the integrity monitor incremental from the last verified checkpoint, under a cluster lease. **S–M**

### H-6 (High for Basic-auth clients): PBKDF2 per node, and the pre-auth IP limiter

- **Evidence:**
  - PBKDF2 uses 600k iterations (`Api/Security/PasswordHasher.cs:14`), measured at **184 ms/op**.
  - Hashing concurrency is limited to `ProcessorCount`, with a 2 s queue timeout (`Api/Security/BasicAuthenticationHandler.cs:35-55, 232-233`).
  - The success cache lasts 30 s and is **per node** (`Domain/Options/Security/JwtOptions.cs:91`; `BasicAuthAttemptGuard.cs:64`, max 300 s).
  - The pre-auth IP limit is 100 requests/60 s, and only Bearer requests are exempt (`Domain/Options/Resilience/RateLimitingOptions.cs:30-31, 39`; `Api/Middleware/RateLimitingMiddleware.cs:49-56`).
- **Estimate:** If BI tools (Trino protocol, DBeaver, Power BI) use Basic auth, 500 active users × 3 replicas (round-robin) re-hash every 30 s. That is **50 hashes/s × 184 ms ≈ 9 cores**, beyond the hashing semaphore, so requests get 401/503. Clients behind one NAT or proxy IP (Citrix, Power BI gateway) share **100 requests per minute**.
- **Change:**
  - Raise `SuccessCacheSeconds` to 300, and keep revocation and lockout checks.
  - Share the verified-credential marker cluster-wide: an HMAC of the credential in Redis with a TTL, never the password.
  - Prefer OIDC tokens or high-entropy personal access tokens (SHA-256 lookup, microseconds).
  - Apply the IP limiter only to unauthenticated or failed attempts, and rely on the per-SID bucket after successful authentication.

  **S**

### M-1 (Medium): SQL compile path

- **Evidence:**
  - Stage-1 `Analyze` always does a full ANTLR parse that is never cached (`Application/Sql/Services/GovernedSqlRewriter.cs:~174`; `TrinoSqlEngine/FastSqlEngine.cs:775-783`).
  - Parsing runs on a dedicated thread, and the request thread **blocks**: `limiter.Wait(...)` (`FastSqlEngine.cs:254`) and `completed.Wait(...)` (`:328`). The process-wide limit is `ProcessorCount*2` parses (`:44-50`).
  - The template cache has a fixed 512 entries, and eviction scans all of them under `_evictionLock` (`TrinoSqlEngine/Ast/Emit/CompiledSqlTemplate.cs:120, 206-228`).
  - The key holds the raw SQL and the requested `EnforcedMaxRows` (`GovernedSqlRewriter.cs:584`), but not the policy fingerprint. The fingerprint check on a hit (`GovernedSqlCompiler.cs:241-255`) depends on the **IN-list cardinality**, because there is one parameter per value (`Application/Sql/Policy/AdvancedRlsPredicateBuilder.cs:147-159`). Users with different numbers of allowed values therefore **evict each other's entry** for the same SQL.
- **Estimate:**
  - Measured: hit 13.5 µs vs miss 0.59 ms (small query). A 4 KB query costs about 1 MB of allocation per miss.
  - Each request also pays one uncached parse plus two thread-pool blocks. At 600 rps with 4-core nodes this is fine on average. But bursts of slow parses hold thread-pool threads for up to the 5 s budget, which risks thread-pool starvation (latency cliffs).
- **Change:**
  - Cache the Stage-1 analysis by SQL text hash.
  - Make the parse path async (`await limiter.WaitAsync`, then a `TaskCompletionSource` from the dedicated thread).
  - Emit IN-lists as one array parameter (PG `= ANY(@p)`, SQL Server `OPENJSON`/TVP, Oracle collection), so the policy shape no longer depends on the values. Include the fingerprint in the key.
  - Make the size configurable (default 4,096) with O(1) eviction, and normalize `EnforcedMaxRows` into buckets.

  **M**

### M-2 (Medium): Buffered result paths

- **Evidence:**
  - Only `POST /api/v1/sql` streams its results (`Api/Endpoints/WebSqlEndpoints.Execution.cs:180-266`).
  - The following paths buffer `List<Dictionary<…>>`: the Trino protocol (`WebSqlStatementManager.cs:150-167`, retained until a 1-minute cleanup), Arrow Flight including `/flight/sql/stream` (`ArrowFlightSqlServer.cs:199`), REST datasets (with a second per-row copy in `GovernedDataQueryService.cs:198-216, 264-284`), async jobs (`AsyncQueryJobBackgroundWorker.cs:248`), MCP and SqlEndpoints. Databricks also buffers (`DatabricksStatementExecutionClient.cs:435, 519`).
  - `StreamRowsAsync` hops threads once per row through a channel of capacity 1 (`GovernedSqlExecutor.cs:160-167`).
  - REST paths mask each cell in memory and rebuild `ScopeRuleForTenant` for every cell (`GovernedDataQueryService.cs:272-274`).
- **Estimate:** 10k rows × 10 columns as dictionaries is about 10 to 15 MB per request, so 100 concurrent exports use **1 to 1.5 GB**, competing with H-3.
- **Change:**
  - Stream end-to-end through `IAsyncEnumerable` on the Trino protocol (page by `nextUri`), Arrow Flight and REST. Use a row-array representation instead of per-row dictionaries.
  - Use channel batches of about 256 rows.
  - Resolve masks once per column instead of once per cell.

  **M–L**

### M-3 (Medium): Redis round trips per request (latency, not throughput)

- **Evidence:** For a Bearer request:
  - revocation: k pipelined GETs (`TokenRevocationMiddleware.cs:29`)
  - per-SID rate limit: 1 Lua call
  - GraphQL cost quota: 1 Lua call
  - consent epoch: **1 GET per table, even on an L1 hit**, because `LocalStalenessBudgetMilliseconds = 0` (`RedisOptions.cs:143`; `EpochValidationService.cs:424-478`)
  - MCP session touch: 1 SET
  - L2 consent: +1 GET per table on a miss

  These are awaited in sequence.
- **Estimate:** About 5 to 10 commands per request is 3k to 6k ops/s at peak, which is far below one Redis node's capacity (≥ 100k ops/s). The issue is latency: 4 to 6 sequential RTTs add **2 to 6 ms** to every request.
- **Change:**
  - Set `LocalStalenessBudgetMilliseconds` to 100 to 250 ms for non-sensitive tables, which is already within the validated range. Epoch pub/sub already pushes invalidations.
  - Batch the epoch GETs of all tables of a request (fixed for Cluster per B-2).
  - Combine the rate-limit and quota Lua calls into one script.
  - Keep a local "not revoked" negative cache of about 1 s, also invalidated by the revocation pub/sub.

  **S**

### M-4 (Medium): Concurrency limits and pools that need sizing, not redesign

- **Governance DB:** Access is raw ADO.NET with one `NpgsqlDataSource` and the default Max Pool Size of 100 per replica (`PostgreSqlGovernanceRepository.cs:125-131`). This is adequate once B-1 and H-2 are fixed. Verify that `replicas × pool ≤ max_connections − headroom`.
- **Source DBs:**
  - Pooling is **not** defeated per user. There is one connection string per source, and user context is set through session context (`Application/Services/DbSessionContextInitializer.cs`; `Infrastructure/Persistence/SqlConnectionFactory.cs:56-210`).
  - PG costs about 5 to 6 round trips per query (pins, `BEGIN`, `set_config`, query, `COMMIT`; `SqlConnectionFactory.cs:123-140`). Concurrency is bounded by the Interactive resource group of 50 per node (`Domain/Options/Resilience/QueryGovernorOptions.cs:8-10`) and by 4 reads per user and table (`DatabaseOptions.cs:21`).
  - Action: size `Max Pool Size` per source as source capacity divided by the number of replicas. Merge the pins into one batch per physical connection. For PG, use `set_config` inside a single multi-statement command.
- **Async jobs:** each node runs 4 jobs, 2 per tenant, with 50 queued per tenant, and results go to node-local scratch (`Domain/Options/Tenancy/MultiTenancyOptions.cs:68-80`). For 5,000 users with exports, raise these limits per node size and move results to shared object storage, so that any replica can serve the download. **M**
- **CDC:**
  - The source is polled once per cluster (SQL Server, under a lease).
  - **Governance is re-evaluated per event and per subscriber** (`GraphQL/Subscriptions/Subscription.cs:151`; `StreamRlsPolicyEnforcer.cs:126-228`), so cost is events × subscribers.
  - The PG logical replication service has no lease (`PostgreSqlLogicalReplicationService.cs:180`).
  - Action: cache a decision per (subscriber principal shape, table, epoch) and evaluate row filters per shape group. **M**
- **Redis connect:** `ConnectionMultiplexer.Connect()` blocks inside DI factories (`GatewayStorageServiceExtensions.cs:67, 81`). This affects startup only. Register the existing `RedisConnectionInitializer`. **S**

---

## 4. Caches at 5,000 Users × Many Tables

| Cache | Key granularity | Bound / TTL | Problem at scale | Target |
|---|---|---|---|---|
| Casbin decision (`CasbinDecisionCache`) | user + IP + columns + groups + … | 10k LRU, global lock, 60 s; global `Clear()` on any publish | Working set 10k to 30k → thrash; misses are expensive (H-1) | Group-set key, sharded LRU 100k, single-flight, per-tenant invalidation |
| Consent L1 (`ConsentCacheService`) | user + context hash + table | 512 MB size-based, absolute TTL | Fine in size. Every hit costs 1 Redis GET (M-3); misses scan the whole table (H-2) | Staleness budget; table consent snapshot |
| Consent L2 (Redis) | same | TTL | Sequential GET per table | Pipelined |
| Access profile | tenant + epoch + subject | 5 min; `Size=1` bug | Wrong size accounting | Fix units |
| VF memo (`MandatoryRowFilterResolver`) | user + groups + table + dialect | **Unbounded** until generation change | 250 MB to 1 GB per replica | Bounded, key by attribute shape |
| Compile templates | raw SQL + max rows + flags | 512, O(n) eviction | IN-list cardinality thrash | 4k, O(1), value-independent shapes |
| Policy expression parser | expression | 4,096, global lock | OK | — |
| Streaming row-filter plans | — | Clear-all at 1,000 (`StreamingRowFilterAstEvaluator.cs:23, 80-82`) | Cliff when full | LRU |
| ReBAC decisions | user + object | 10k per tenant, clear-all | Thrash; misses read the whole hash (H-4) | Indexed tuples + LRU |
| Metadata (repository) | table | 30 s, **unbounded** | Grows with the catalog; `GetAllTablesAsync` bypasses it for the table list | `CatalogSnapshot` (H-5) |

**Invalidation storms.**
- A Casbin publish clears the whole decision cache on every replica (`CasbinEnforcementService.cs:382`).
- An epoch bump on a popular table invalidates every user's consent entry for that table.
- Neither path has single-flight. With 500 active users the result is a burst of hundreds of parallel full evaluations within seconds.
- The single-flight and per-table snapshot changes in H-1 and H-2 turn an O(users) storm into O(replicas) work.

---

## 5. Capacity Estimate (design peak 600 rps, 3 replicas × 4 vCPU / 8 GB)

| Resource | Today (estimated) | After S-effort fixes (B-1.1–3, B-2, H-1.1/4, H-2.1, H-3, M-3) |
|---|---|---|
| Gateway CPU (cluster) | 1 to 2 cores on the hot path (hits) **plus 2+ cores** of sub_rule interpretation on misses, plus PBKDF2 bursts (up to about 9 cores with Basic-auth BI tools) | **~1.5 to 3 cores** of 12 → large headroom; 2 replicas would suffice, 3 for HA |
| Gateway memory (per replica) | Base about 0.5 GB + consent L1 up to 0.5 GB + VF memo up to 1 GB (unbounded) + metadata up to 0.2 GB + buffered results 0.5 to 1.5 GB → **risk of OOM at 8 GB under export bursts** | About 1.5 to 2.5 GB steady with bounded caches |
| Governance DB | Audit: 600 to 1,200 rows/s through one global lock (**saturated**). Consent misses: full-table scans, 5 to 20 ms each. OData: full catalog per request | Audit about 5 to 10 batch commits/s; consent queries by index in under 1 ms; catalog from snapshot |
| Redis | 3k to 6k ops/s (fine); 2 to 6 ms of added latency; **CROSSSLOT failure on Cluster** | About 2k ops/s, 1 to 2 RTTs per request |
| Source DBs | Bounded by resource groups (50 per node) → up to 150 concurrent queries; 5 to 6 RTTs per PG query | Pool sizes aligned with source capacity |

**Order of bottlenecks as load ramps (prediction to be verified in section 6):**
1. Audit channel saturation and Tier-A serialization (about 300 to 1,000 rps, depending on RTT).
2. Thrashing of the Casbin and ReBAC decision caches, with interpreter CPU and GC pressure.
3. Consent miss storms after epoch bumps.
4. Memory growth of the VF memo and buffered exports.
5. Thread-pool blocking in the parser under bursty large queries.

---

## 6. Load-Test Plan

**Tooling:**
- **k6** is already used in `benchmarks/load/configs/k6/load_test.js`, with the Hetzner scripts in `benchmarks/load/`. Extend it with a new script, `scale5000.js`. Do not add it as a CI job; run it on demand or nightly on the Hetzner 3-tier topology (`benchmarks/load/hcloud/provision_3tier_cluster.sh`).
- **Data generator:** reuse the synthetic catalog from `ScaleCatalogBenchmark` (5,000 users, 500 groups, 50,000 tables, 50,000 consents). Emit it as SQL seed data for the governance store. Issue JWTs for 5,000 synthetic users (20 to 50 groups each) from a test IdP, or from the test signing key used by the benchmark config.
- **Observability:**
  - `dotnet-counters`: GC heap, Gen2 count, thread-pool queue length, lock contention.
  - Existing `GatewayDiagnostics` metrics: cache hit ratios. Add counters for the decision cache, consent L1/L2, template cache and VF memo if they are missing.
  - PG `pg_stat_statements`.
  - Redis `INFO commandstats`.
  - Audit channel depth.

**Scenarios** (each 30 min after a 5 min warm-up, on 3 replicas):

| # | Scenario | Load shape | Purpose |
|---|---|---|---|
| LT-1 | Steady state | 500 VUs (one per active user), 1 request / 10 s, mix 50% REST, 25% WebSQL, 15% OData, 10% GraphQL; Zipf table popularity over 200 tables per user | Baseline, cache hit rates |
| LT-2 | Morning peak | Ramp from 50 to 600 rps over 10 min, hold 15 min; 2.5 tables/request | Find the first saturation point (B-1, H-1) |
| LT-3 | Epoch / policy storm | During LT-1, bump the consent epoch of the 10 hottest tables and publish one Casbin change every 60 s | Invalidation storms, single-flight, p99 spike |
| LT-4 | Denial burst | 2,000 rps of invalid tokens or rate-limited calls for 60 s during LT-1 | Tier-A audit path; legitimate requests must not fail |
| LT-5 | Export burst | 100 concurrent 10k-row REST/Trino/Arrow exports during LT-1 | Memory (M-2, H-3), GC |
| LT-6 | Basic-auth BI clients | 300 VUs on the Trino protocol with Basic auth from 3 source IPs | H-6 |
| LT-7 | CDC fan-out | 1,000 subscribers over 50 tables, 100 events/s | B-3, per-subscriber evaluation |
| LT-8 | Redis Cluster | LT-1 against a 3-shard Redis Cluster | B-2 |
| LT-9 | Soak | LT-1 at 300 rps for 8 h | Unbounded caches, audit table growth, leaks |

**Success criteria** (gateway overhead measured against a trivial source query, `SELECT` on a 10-row table):

| Metric | Target |
|---|---|
| p50 / p95 / p99 gateway overhead at 600 rps | ≤ 10 / 40 / 100 ms |
| Error rate (non-4xx) | < 0.1%; **zero** `AuditUnavailableException` in LT-1 to LT-5 |
| Casbin decision cache hit rate (LT-1) | ≥ 90% after warm-up |
| Consent L1 hit rate (LT-1) | ≥ 95% |
| Template cache hit rate (WebSQL repeated statements) | ≥ 90% |
| Audit lag (enqueue to commit), p99 | ≤ 1 s; channel depth < 20% of capacity |
| p99 after an epoch bump (LT-3) | ≤ 2 × steady p99, recovered within 5 s |
| Gateway memory per replica (LT-5, LT-9) | ≤ 4 GB, no monotonic growth over 8 h |
| Gen2 GCs (LT-2) | ≤ 1/min per replica |
| Thread-pool queue length | ≤ 2 × core count, sustained |
| CPU per replica at 600 rps | ≤ 60% (headroom for losing one replica) |
| Redis | ≤ 3 sequential RTTs per request; zero CROSSSLOT errors (LT-8) |

**Micro-benchmarks to add** (BenchmarkDotNet, short job, not in CI gates):
- `CasbinDecisionBenchmark`: 5,000 rules, 30 groups, 20% sub_rules; cold vs warm; measure before and after H-1.1.
- `AuditBatchWriteBenchmark` against Testcontainers PG: rows/s for a 250-row batch, before and after B-1.1.
- `ConsentMissBenchmark`: 5,000 consents per table; per-table scan vs indexed subject query.

---

## 7. Recommended Implementation Order

| Order | Item | Effort | Expected effect |
|---|---|---|---|
| 1 | B-2 epoch MGET CROSSSLOT fix | S | Removes a cluster-mode collapse |
| 2 | B-3 CDC cluster cap per subject | S | Removes a functional limit of 10 per tenant |
| 3 | H-1.1 compiled sub_rule delegates (plus precompiled object regexes) | S | About 2+ cores and about 180 MB/s of allocation saved at peak |
| 4 | B-1.1–1.3 batched audit insert, async tail and anchor, wire group commit | S–M | Audit ceiling raised by about 10 to 50× |
| 5 | H-2.1/2.2 indexed consent query, pipelined L2 | S–M | Consent miss cost from ~10 ms to < 1 ms |
| 6 | H-3 bounded caches, `Size` units | S | Removes OOM risk |
| 7 | M-3 staleness budget, batched epoch, fused rate-limit Lua | S | −2 to 5 ms per request |
| 8 | H-6 Basic-auth cache / PAT, IP limiter after auth | S | Removes CPU bursts and NAT throttling |
| 9 | H-1.3–1.5 group-shape decision keys, sharded cache, single-flight | M | Hit rate ≥ 90%; no storms |
| 10 | H-5 `CatalogSnapshot`, incremental integrity monitor | M | Removes O(catalog) work per request |
| 11 | H-4 indexed ReBAC store | M | Required before ReBAC is used at company scale |
| 12 | M-1 async parse, Stage-1 cache, array IN-lists | M | Removes thread-pool cliffs and cache thrash |
| 13 | B-1.4 partitioned audit chains | M–L | Audit throughput grows with replicas (needs a security sign-off) |
| 14 | M-2 end-to-end streaming on the remaining egress paths | M–L | Memory bounded per request |

Items 1 to 8 are each small, and together they make 5,000 users with about 600 rps peak achievable on 3 × 4 vCPU replicas. Items 9 to 14 provide headroom and keep the system flat as the numbers of users and tables keep growing. Every item follows the 6-phase lifecycle (PRD/plan, then security review, then TDD), and the fail-closed semantics must stay unchanged.

---

## 8. Open-Source Alternatives: Integrate, Borrow, or Build (owner request 2026-10-16)

For each change above, the owner asked which open-source alternatives exist, and whether to:
- **(A)** integrate them as a dependency or sidecar,
- **(B)** copy parts into the codebase (borrow), or
- **(C)** use an existing .NET variant.

The fallback is to build natively.

**License rule.** Autheris is licensed under **BSL 1.1** (`LICENSE`).
- NuGet dependencies and code copied under **MIT, Apache-2.0 or BSD** are acceptable. Copied code must keep its NOTICE/attribution.
- **GPL, AGPL, SSPL or third-party BSL** code must not be copied or linked.
- AGPL tools that only run *outside* the product, such as the k6 load generator, are acceptable.

| Topic (ref.) | OSS candidates (license) | Fit for Autheris | Decision |
|---|---|---|---|
| Compiled `sub_rule` evaluation (H-1.1) | **DynamicExpresso** (MIT, already referenced): `Interpreter.ParseAsDelegate`/`Lambda`. **Casbin.NET** (Apache-2.0, already `2.21.3`): its matcher is compiled once per model and cached. | The fix needs no new dependency. Routing decisions back through `Casbin.NET` `Enforce` would lose the custom deny/allow, column and mask semantics of `CasbinEnforcementService.Columns.cs`. | **C/native:** cache DynamicExpresso lambdas on the `PolicySnapshot`. Do not switch the decision engine. |
| External policy engine instead of the in-process scan (H-1) | **OPA** (Apache-2.0, Go, sidecar/WASM). **Cerbos** (Apache-2.0, Go, has a .NET SDK). **Topaz** (Apache-2.0). | Each adds a network or WASM hop per decision (≥ 0.3 to 1 ms) and a second policy language. The consent, mask and row-filter semantics would have to be ported. This is a security re-certification effort. | **Reject for now.** It does not solve the bottleneck: decision CPU drops to nanoseconds once delegates are compiled. |
| Decision cache with a sharded LRU (H-1.4) | **BitFaster.Caching** (MIT): `ConcurrentLru` and `ConcurrentLfu`, lock-free, with stampede-safe `GetOrAddAsync`. | A drop-in replacement for the global-lock `LinkedList` LRU, and used in production by large .NET services. | **A:** add the NuGet package for the decision cache, the VF memo, the ReBAC decision cache and the template cache (H-3, M-1). |
| Consent L1/L2 cache, stampede protection, backplane (H-2.3, M-3) | **Microsoft.Extensions.Caching.Hybrid** `HybridCache` (MIT, .NET 9+): L1+L2, stampede protection, tag invalidation. **FusionCache** (MIT): L1+L2, fail-safe, eager refresh, Redis backplane. | Both replace much of the hand-written `ConsentCacheService`, which is about 500 lines with index locks. `HybridCache` is first-party and has no extra transitive dependencies. FusionCache adds a ready-made Redis **backplane** and soft/hard timeouts. Both still need the epoch check as a custom validation step. | **A:** pilot `HybridCache` first, because it is first-party. Choose FusionCache if backplane and fail-safe semantics are needed. Keep the epoch logic and fail-closed checks native. |
| Batched audit writes (B-1.1) | **Npgsql** binary `COPY` (already referenced). **SqlBulkCopy** / TVPs (Microsoft.Data.SqlClient, already referenced). | Covered by existing dependencies. | **C/native.** |
| Tamper-evident audit store (B-1.4) | **Trillian** (Apache-2.0, Go service, RFC 6962 Merkle log). **immudb** (Apache-2.0, Go DB, has a .NET SDK). **SQL Server Ledger tables** (built into SQL Server 2022 and Azure SQL). **pgaudit** (PostgreSQL License; logs only, no chain). | Trillian and immudb add a new stateful service with its own HA and backup. SQL Server Ledger is native for SQL Server governance stores only. | **B:** borrow the *concepts*, Merkle checkpoints and signed tree heads per RFC 6962, to implement partitioned chains natively. Treat SQL Server Ledger as an optional backend for SQL Server deployments. Do not run Trillian or immudb as a dependency. |
| ReBAC tuple store (H-4) | **OpenFGA** (Apache-2.0, Go, CNCF; official .NET SDK). **SpiceDB** (Apache-2.0, Go; gRPC .NET client). **Ory Keto** (Apache-2.0). | All three are mature Zanzibar implementations with indexed checks, `ListObjects` and caching. Each costs a network hop per check (about 1 to 5 ms; batch-check exists) and one more stateful service. Tuples would move out of the governance store, which affects the WORM audit of changes. | **Decide by ReBAC usage.** If ReBAC stays secondary, **B:** copy the Zanzibar data model (per `object#relation` sets, userset rewrites) into an indexed Redis/in-memory store (M). If relationship-based sharing becomes a primary access path for 5,000 users, **A:** use OpenFGA as a sidecar behind `IRebacStore`, and benchmark it in LT-1. |
| Rate limiting (H-6, M-3) | **System.Threading.RateLimiting** (built-in). **RedisRateLimiting** (MIT, `cristipufu/aspnetcore-redis-rate-limiting`). | The existing Lua token buckets already work. The library would mainly make the middleware standard, but it would not fuse the rate-limit and quota calls. | **Native:** fuse the Lua scripts. Optionally adopt the `RateLimiter` abstractions later. |
| Basic-auth replacement (H-6) | **Keycloak** (Apache-2.0) or the existing ADFS/Entra for OIDC. Personal access tokens are a native pattern (GitHub-style, SHA-256 lookup). | For BI tools, most drivers support OAuth or tokens-as-password. | **Native PATs** plus the existing OIDC. No new IdP component. |
| Catalog snapshot (H-5) | — (domain-specific) | — | **Native.** |
| Async parse path (M-1) | ANTLR4 runtime (BSD-3, already used) | The issue is threading, not the parser. | **Native.** |
| End-to-end streaming (M-2) | **Apache.Arrow** (Apache-2.0, already referenced) as the internal columnar batch format. **System.IO.Pipelines** (built-in). | Arrow `RecordBatch` streaming would serve Flight SQL, exports and the Trino protocol with one representation, and remove the per-row `Dictionary`. | **C:** standardize the internal row pipeline on Arrow record batches. |
| Load testing (section 6) | **k6** (AGPL-3.0; external tool only, already used in `benchmarks/load`). **Gatling** (Apache-2.0). **NBomber** (.NET; **commercial license required for organizational use**). | k6 already exists in the repository. NBomber's licensing makes it unsuitable. | **Keep k6.** |
| Observability for the load test | **OpenTelemetry .NET** (Apache-2.0, already referenced). **dotnet-counters** / **dotnet-trace** (MIT). | Present. Only cache hit/miss counters need to be added. | **Native counters** through the existing OTel meter. |

**Net result:**
- Two new dependencies are recommended: **BitFaster.Caching**, and **HybridCache** or **FusionCache**.
- One alternative is conditional: **OpenFGA** for ReBAC, if ReBAC becomes primary.
- Everything else is best solved with dependencies already in the solution, or natively by borrowing concepts (Zanzibar, RFC 6962).

Each new dependency goes through the Phase-3 security review: supply chain, pinned versions in `packages.lock.json`, and fail-closed behavior when the cache or backplane is unavailable.
