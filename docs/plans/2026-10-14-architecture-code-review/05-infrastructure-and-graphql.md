# Architecture Review 05 - Autheris.Infrastructure and Autheris.GraphQL

## 1. Scope and key metrics

Read-only review of `src/Autheris.Infrastructure` and `src/Autheris.GraphQL`. Security bug-hunting is excluded (see the security audit plan).

| Metric | Value |
|---|---|
| Infrastructure `.cs` lines | about 25.4k |
| GraphQL `.cs` lines | about 7.6k |
| Governance repositories (Sqlite, SqlServer, PostgreSql partial classes) | 14,812 lines, about 58% of Infrastructure |
| Files using `DatabaseDialect.` across `src/` | about 40 (Application 20+, Domain 4, Infrastructure 6, Api 3, GraphQL 2, Extensions 2) |
| Prometheus static metric declarations | 27 |
| `GatewayDiagnostics` (OTel) usages | 38 |
| `IConnectionMultiplexer` consumers in Infrastructure | 9 classes |
| `AddDataLoader`-style loaders in GraphQL | 1 (`InvoiceItemDataLoader`, demo only) |

### Top 15 largest files in the slice

| # | Lines | File |
|---|---|---|
| 1 | 1705 | `Infrastructure/Persistence/SqliteGovernanceRepository.Consent.cs` |
| 2 | 1582 | `Infrastructure/Persistence/PostgreSqlGovernanceRepository.Consent.cs` |
| 3 | 1556 | `Infrastructure/Persistence/SqlServerGovernanceRepository.Consent.cs` |
| 4 | 1125 | `Infrastructure/Persistence/SqliteGovernanceRepository.Schema.cs` |
| 5 | 1077 | `Infrastructure/Persistence/SqlServerGovernanceRepository.Audit.cs` |
| 6 | 1049 | `Infrastructure/Persistence/SqliteGovernanceRepository.Audit.cs` |
| 7 | 1045 | `Infrastructure/Persistence/PostgreSqlGovernanceRepository.Audit.cs` |
| 8 | 943 | `Infrastructure/Persistence/SqliteGovernanceRepository.Catalog.cs` |
| 9 | 859 | `GraphQL/Types/MutationTypes.cs` |
| 10 | 776 | `GraphQL/Types/QueryTypes.cs` |
| 11 | 761 | `Infrastructure/Persistence/PostgreSqlGovernanceRepository.Schema.cs` |
| 12 | 718 | `GraphQL/Mcp/GatewayMcpQueryExecutor.cs` |
| 13 | 688 | `Infrastructure/Persistence/SqlServerGovernanceRepository.Schema.cs` |
| 14 | 669 | `Infrastructure/Persistence/SqlServerGovernanceRepository.Catalog.cs` |
| 15 | 646 | `Infrastructure/Persistence/PostgreSqlGovernanceRepository.Catalog.cs` |

Duplication is concentrated in the governance repositories. Consent, Audit, Catalog and Schema are each implemented three times, once per backend.

## 2. Strengths

- **Per-dialect catalog readers sit behind a port.** `GovernedCatalogProvider` resolves an `IColumnMetadataReader` from a dictionary keyed by dialect (`Infrastructure/Persistence/Catalog/GovernedCatalogProvider.cs:32,58,204`). It is the one place where adding a dialect means adding a class.
- **The GraphQL layer pushes data access into a single governed tree compiler.** The nested selection is compiled to one `IGovernedTreeQueryService.ExecuteAsync` call (`GraphQL/Catalog/CatalogGraphQlTypeModule.cs:476-500`, `GraphQL/Catalog/GraphQlTreeBuilder.cs`). This avoids N+1 by construction, so DataLoaders are not needed for catalog tables.
- **The catalog schema survives an unreachable governance DB.** On build failure it falls back to `CatalogSchemaModel.Empty` and a timer retries (`CatalogGraphQlTypeModule.cs:113-133`). Change detection uses a fingerprint compare with an `Interlocked` re-entrancy guard (`:56-88`), and a single module instance is shared across schema rebuilds.
- **The epoch and consent cache is well designed for failure.** There is an L1/L2 split. `Authoritative=false` prevents a stale local epoch from confirming a cached decision. It fails closed for sensitive tables (`Infrastructure/Cache/EpochValidationService.cs:144-149,237,414,431,467`). A rollback guard tracks the highest epoch seen per table (`:30-45`). L1 is flushed on `ConnectionRestored` (`:76-84`).
- **The SQL connection factory centralizes the session hardening.** It pins the schema, applies Oracle bind-by-name and PostgreSQL `standard_conforming_strings`, and validates TLS before connecting (`Infrastructure/Persistence/SqlConnectionFactory.cs:59-130`). It rents a fresh `DbConnection` per call, so pool lifetime is delegated to the ADO.NET drivers.
- **Graceful degradation without Redis.** `IEventBus`, `IRateLimiterService`, `IIdempotencyStore`, `ITokenRevocationService` and `IDistributedClusterStateProvider` each have an in-memory twin selected in one DI branch (`Api/Extensions/DependencyInjection/GatewayStorageServiceExtensions.cs:70-96`).

## 3. Findings overview

| ID | Sev | Category | Title |
|---|---|---|---|
| ARC-I-01 | High | Cohesion / Maintainability | Governance persistence implemented three times (14.8k lines) behind one God-class per backend |
| ARC-I-02 | High | Coupling | No dialect port: dialect logic scattered across about 40 files, mostly in Application |
| ARC-I-03 | High | Resilience / Concurrency | `RedisClusterStateProvider` swallows every failure and returns "absent" (silent fail-open per consumer) |
| ARC-I-04 | High | Data | Governance schema "migration" is ad-hoc idempotent DDL in code, with no version table and a separate path per backend |
| ARC-I-05 | High | Performance / Concurrency | SQLite governance repository serializes everything on one shared `SqliteConnection` and one `SemaphoreSlim` |
| ARC-I-06 | Medium | Performance | `IConnectorRecordSource.ReadSplitAsync` is a fake stream; the full result set is materialized as dictionary-per-row |
| ARC-I-07 | Medium | Resilience | Pub/sub invalidation is at-most-once with no reconciliation besides reconnect |
| ARC-I-08 | Medium | Consistency / DI | Embedded Garnet and external Redis share one code path, but the embedded node is a per-node island |
| ARC-I-09 | Medium | Maintainability | Telemetry split: OpenTelemetry meters (OTLP) and prometheus-net statics (`/metrics`) in parallel |
| ARC-I-10 | Medium | DI/Lifetime | Sync-over-async startup, blocking `ConnectionMultiplexer.Connect` and constructor I/O in factories |
| ARC-I-11 | Medium | Layering | Infrastructure `GatewayDiagnostics` is a pass-through facade over the Domain class |
| ARC-I-12 | Low | Data | Multiple SQLite files and ad-hoc DBs (governance, plugins, audit anchors) with no unified store policy |
| ARC-I-13 | Low | Testability | Optional-everything constructors (`= null`) hide required dependencies and create silent fallbacks |
| ARC-G-01 | High | Layering / Consistency | Business workflow (consent request, ITSM compensation, auto-approve) lives inside a GraphQL mutation |
| ARC-G-02 | Medium | Performance | Catalog change detection runs N+1 queries against the governance DB on every node every 60 s |
| ARC-G-03 | Medium | Concurrency | Subscriptions use in-memory pub/sub and per-node governors, so they break in a multi-node cluster |
| ARC-G-04 | Medium | Cohesion | GraphQL pipeline composition and limits live in `Autheris.Api`, not in the GraphQL module |
| ARC-G-05 | Medium | Maintainability | Dead or demo code in the production assembly (`DynamicTableType`, `InvoiceItemDataLoader`) |
| ARC-G-06 | Low | Performance | Resolver copies all request headers and re-parses the document per request in middleware |
| ARC-G-07 | Low | Consistency | Hard-coded dialect allowlist in schema model (SqlServer/PostgreSql/Sqlite) diverges from REST/OData reach |
| ARC-G-08 | Low | Maintainability | `MutationTypes`/`QueryTypes` as multi-overload god-classes with 10-parameter `[Service]` signatures |

## 4. Details

### ARC-I-01 Governance persistence implemented three times (High, Cohesion/Maintainability)
- **Evidence**
  - `Infrastructure/Persistence/{Sqlite,SqlServer,PostgreSql}GovernanceRepository.{Consent,Audit,Catalog,Schema,Outbox,TenantRegistry,VirtualFilters}.cs`: 14,812 lines in total. Consent files are 1,705, 1,582 and 1,556 lines.
  - `Api/Extensions/DependencyInjection/GatewayGovernanceServiceExtensions.cs:71-83`: one concrete class is aliased as 13 interfaces (`IGovernanceRepository`, `IConsentRepository`, `IAuditLogRepository`, `IPolicyEpochRepository`, ...).
- **Problem**
  - Each backend is one large partial class implementing every governance capability: consent, audit chain, catalog, outbox, tenants, filters, access profiles.
  - Business-relevant behavior (audit HMAC chain, consent approval state, epoch bump) is duplicated in three SQL dialects, with only the SQL text differing.
- **Impact**
  - Any change to consent or audit semantics must be made, reviewed and tested three times, and drift is likely. Evidence: the Sqlite audit file holds an extra anchor and lock path (`SqliteGovernanceRepository.Audit.cs:16-200`).
  - Adding a fourth governance backend means about 5k new lines.
- **Recommendation**
  - Split by aggregate: `ConsentStore`, `AuditStore`, `CatalogStore`, `OutboxStore`, each with a small `IGovernanceSqlDialect` (SQL text, parameter style, upsert and paging fragments).
  - Move the invariant logic (audit chain hashing, consent state transitions) into Application or Infrastructure-neutral classes and keep only data access per dialect.
  - Register each aggregate store individually instead of one class aliased 13 times.
  - Option: converge on Dapper or EF Core with a dialect strategy for this internal store.
- **Effort**: L

### ARC-I-02 No dialect port, dialect logic scattered (High, Coupling)
- **Evidence**
  - `DatabaseDialect.` is referenced in about 40 files. The largest counts are `Application/Services/SqlDataSourceExecutor.Projection.cs` (24), `Application/Sql/Masking/SqlDataMaskingProvider.cs` (15), `Application/Sql/Tree/TreeSqlCompiler.cs` (14), `Application/Sql/SqlDialectMapper.cs` (11), `Application/Sql/Services/GovernedSqlRewriter.cs` (11) and `Application/Services/SqlDataSourceExecutor.cs` (10).
  - Infrastructure: `Persistence/SqlConnectionFactory.cs:59-102` (provider switch plus `is NpgsqlConnection` and `is BindByNameOracleConnection` type tests at `:112,126`), `Persistence/DatabaseParameterBudgetProvider.cs` (6), `Persistence/ConnectionTlsPolicy.cs`, `Databricks/*`.
  - Two parallel enums need a mapper: `DatabaseDialect` (Domain) and `TargetSqlDialect` (`Application/Sql/SqlDialectMapper.cs:9`).
  - Startup validation switches on provider again (`Api/Extensions/DependencyInjection/GatewayStartupValidator.cs:741`).
  - Cross-slice hits: `Extensions/Cdc/MssqlChangeTrackingPoller.cs`, `Extensions/OData/ODataFilterParser.cs`.
- **Problem**
  - The only real provider port is the column reader (see Strengths).
  - Query compilation, projection, masking, RLS filter generation, paging, parameter budgets, session init, literal escaping and connection creation each carry their own `switch`.
  - Application (the inner layer) holds `DbConnection`, `DbCommand` and provider-specific SQL, which blurs the Clean/Onion boundary.
- **Impact**
  - Adding a dialect (for example Snowflake or MySQL) touches roughly 15 to 20 files in at least 4 projects, with no compiler help because most are `default:` branches.
  - R-GQL-13 shows the failure mode: Oracle and Databricks tables appeared in the schema, then failed at query time (`GraphQL/Catalog/CatalogSchemaModel.cs:105-111`).
- **Recommendation**
  - Introduce `ISqlDialectProvider` (in Application, implemented per dialect) exposing:
    - identifier quoting
    - literal escape
    - paging clause
    - bind-parameter prefix
    - parameter budget
    - capabilities flags (supports tree compile, CDC, count)
    - a `CreateConnectionAsync` hook for session pinning
  - Register all providers in a dictionary keyed by one enum, and delete the `TargetSqlDialect`/`DatabaseDialect` duplication.
  - Replace `is NpgsqlConnection` checks in `SqlConnectionFactory` with a per-dialect `ISessionInitializer`.
  - Expose a `Capabilities` flag so GraphQL, REST, OData and MCP gate by capability and not by an enum list.
- **Effort**: L

### ARC-I-03 `RedisClusterStateProvider` swallows failures (High, Resilience)
- **Evidence**
  - `Infrastructure/State/RedisClusterStateProvider.cs:61-64`: `GetAsync` returns `default` on any exception.
  - `:78`: `SetAsync` logs and continues, so the caller thinks the write succeeded.
  - `:92`: `RemoveAsync` returns `false`.
  - `:115`: `IncrementAsync` returns `null`.
  - `:236-240`: `TryAcquireLockAsync` returns `null`, which is indistinguishable from "lock held by someone else".
  - `:185`: `Publish` logs a warning only.
  - Consumers: `Application/Security/Totp/Services/ClusterTotpSecretStore.cs:59` (a `GetAsync` miss reads as "no secret").
  - The same pattern appears in the idempotency and rate limiter classes, with counters (`Idempotency/RedisIdempotencyStore.cs:31`, `RateLimiting/RedisRateLimiterService.cs:120`).
- **Problem**
  - "Redis down" and "key absent" are the same signal. Failure semantics are decided implicitly by each caller, and there is no `StateUnavailableException` or `Result<T>`.
  - A distributed lock that returns `null` on error means two nodes can both skip a critical section, or both think the other holds it.
- **Impact**
  - Fail-open or fail-closed is inconsistent across features. The epoch service is explicitly fail-closed (`EpochValidationService.cs:237,467`), while a counter, lock or TOTP read degrades silently. A Redis outage can reset per-cluster budgets and rate limits to "unlimited".
- **Recommendation**
  - Change the port to `Result<T>`, or let it throw a typed `ClusterStateUnavailableException`.
  - Let each consumer declare a policy (`FailClosed` or `FailOpenWithLocalFallback`) via options.
  - Expose a `bool IsHealthy` and wire it to the readiness check (`Health/GatewayHealthCheckService.cs`).
  - For locks, return a tri-state (acquired, contended, unavailable).
- **Effort**: M

### ARC-I-04 Ad-hoc schema migration (High, Data)
- **Evidence**
  - `Infrastructure/Persistence/SqliteGovernanceRepository.Schema.cs:419,454,494,532,577,605,985,1000,1116`: `ALTER TABLE ... ADD COLUMN` after probing the column list, and a table rename.
  - `SqlServerGovernanceRepository.Schema.cs:161-165`: `IF COL_LENGTH(...) IS NULL ALTER TABLE`. 25 to 35 DDL statements per backend.
  - There is no schema-version table (grep for `schema_version`, `user_version` and `SchemaVersion` has no hits).
  - `PostgreSqlGovernanceRepository.cs` and `SqlServerGovernanceRepository.cs` have a `MigrationConnectionString` and a lazy init (`:90`, `_isInitialized`).
  - The SQLite repository runs `InitializeDatabase()` synchronously in its constructor (`SqliteGovernanceRepository.cs:75-80`).
- **Problem**
  - The schema is "whatever the last startup converged to". There are no ordered, versioned, auditable steps and no rollback story. Three divergent DDL sets (about 2.6k lines) must be kept equivalent by hand. Concurrent multi-node startup races on DDL are not coordinated.
- **Impact**
  - Compliance-relevant stores (the audit chain) cannot prove which schema version they ran under. Upgrades across more than one release are untestable.
- **Recommendation**
  - Introduce a numbered migration runner (`schema_migrations(version, applied_at, checksum)`), with one script folder per dialect, executed under a distributed lock (advisory lock or `sp_getapplock`).
  - Alternatively adopt DbUp or FluentMigrator.
  - Run migrations as a separate `IHostedService` or CLI step using the existing `MigrationConnectionString`, not in constructors.
- **Effort**: M to L

### ARC-I-05 SQLite governance repo: single connection plus global semaphore (High, Performance/Concurrency)
- **Evidence**
  - `Persistence/SqliteGovernanceRepository.cs:20-24,77-78`: one `SqliteConnection _connection` opened in the constructor, `internal Connection` exposed, and `_lock = new(1,1)`.
  - `_connection` is used 150 times across the partial classes (Consent 44, Schema 49, Catalog 25, ...).
  - Audit writes also take `_lock` (`SqliteGovernanceRepository.Audit.cs:33,73,88,147,192`).
  - Default connection string is a shared in-memory DB (`SqliteGovernanceRepository.cs:75`).
  - Startup validation only bars SQLite governance outside Development (`GatewayStorageServiceExtensions.cs:97-100`).
- **Problem**
  - Every governance read (catalog resolution on each request, consent lookup, audit insert) queues behind one lock on one connection. This is the default single-node and demo path. There is no `Cache=Shared`, WAL or per-operation connection design, and the `internal Connection` accessor leaks the shared handle.
- **Impact**
  - Throughput is capped by the slowest governance statement. The audit channel writer and request threads contend on the same semaphore, which lengthens the audit backlog and request latency.
- **Recommendation**
  - Use a connection per operation with `Pooling=True`, `Mode=ReadWriteCreate`, `PRAGMA journal_mode=WAL` and `busy_timeout`. Keep a single serialized writer only for the audit chain.
  - Remove the internal `Connection` escape hatch.
- **Effort**: M

### ARC-I-06 Fake streaming in `SqlConnector` (Medium, Performance)
- **Evidence**
  - `Connectors/SqlConnector.cs:131-141`: `ReadSplitAsync` is declared `IAsyncEnumerable` but starts with `var batch = await ReadBatchAsync(...)`, then yields from the list.
  - `Application/Services/SqlDataSourceExecutor.cs:444-447,550-575`: `ReadRowsAsync` builds `List<IReadOnlyDictionary<string, object?>>`, one `Dictionary` per row, and only enforces a byte ceiling after materializing each row.
  - The limit ceiling is `max(5000, 250_000 (OLAP), 1_000_000 (Arrow export))` (`SqlConnector.cs:150-158`).
- **Problem**
  - Exports and OLAP staging, the only large-result consumers, still allocate up to 1M dictionaries before the first byte is emitted. The `IAsyncEnumerable` contract promises streaming that does not exist. `CommandBehavior.SequentialAccess` is applied, but nothing consumes it incrementally.
- **Impact**
  - Memory spikes of hundreds of MB per export, GC pressure, and a time-to-first-row equal to the full query time.
- **Recommendation**
  - Add `IAsyncEnumerable<Row>` to the executor (yield inside the `await using reader` scope) and let `ReadBatchAsync` be a thin collector. For Arrow and DuckDB staging, consume `DbDataReader` directly into column builders.
  - Keep per-row byte accounting in the streaming path.
- **Effort**: M

### ARC-I-07 Invalidation pub/sub is at-most-once (Medium, Resilience)
- **Evidence**
  - `Messaging/RedisEventBus.cs:42-60`: publish errors are logged only.
  - `:131`: `sub.Subscribe(...)` with a handler that swallows exceptions (`:123`).
  - `State/RedisClusterStateProvider.cs:199-213`: fire-and-forget `_ = handler(payload).AsTask()`.
  - `Cache/EpochValidationService.cs:76-84,113-137`: the only recovery is `ConnectionRestored`, which flushes L1 and re-subscribes.
  - The epoch is also INCRed in Redis (`:490-498`) and polled with TTL (`:149-190`).
- **Problem**
  - The pub/sub channel is a best-effort accelerator. Correctness depends on epoch polling (fail-closed on sensitive tables). That is sound for the epoch, but non-epoch listeners (the L1 consent cache at `ConsentCacheService.cs:65`, rate or state events) have no such backstop. Their `catch { }` blocks (`ConsentCacheService.cs:161,208,286`) hide drops.
- **Impact**
  - A node that misses one message serves stale L1 data until its TTL expires. Duration is bounded by TTL, not by a freshness guarantee. Sensitive decisions are protected by the epoch check.
- **Recommendation**
  - Document and enforce "pub/sub is a hint, epoch is truth" in `IEventBus` (XML doc), and give every L1 cache a short bounded TTL tied to the epoch.
  - Alternatively move invalidation to Redis Streams with a consumer position for replay.
  - Count dropped publishes and failed handlers in a metric.
- **Effort**: S (document and metric), M (Streams)

### ARC-I-08 Embedded Garnet and external Redis, same code path, different semantics (Medium, Consistency/DI)
- **Evidence**
  - `GatewayStorageServiceExtensions.cs:45-68` (embedded Garnet, started imperatively inside the multiplexer factory via `garnetManager.StartServer()`) versus `:76-86` (external Redis). Both branches repeat the same 5 service registrations.
  - `Autheris.Api/Program.cs:74-80` starts Garnet again imperatively.
  - `Garnet/GarnetServerManager.cs:142-145`: persistence is optional, via `CheckpointDir`.
  - The rollback guard comment in `EpochValidationService.cs:30-36` admits "Redis restart without persistence" as a threat.
- **Problem**
  - Embedded Garnet is a process-local server on `Host:Port` per node. In a multi-node deployment each node has its own island, so rate limits, epochs, TOTP and idempotency are not cluster-wide unless an external Redis is configured. The two modes are selected by flags and look identical to consumers.
  - The embedded server also shares a failure domain with the gateway: a crash loses L2 and idempotency state.
  - The five-line registration block is duplicated.
- **Impact**
  - A cluster misconfigured with embedded Garnet silently loses cluster semantics. `RedisClusterStateProvider` is registered in both modes under the same name.
- **Recommendation**
  - Extract `AddClusterState(mode)` so registrations occur once, and make the mode explicit (`Embedded` or `External`).
  - Expose `IClusterStateTopology { bool IsClusterWide }`, and have the startup validator fail when more than one replica is configured with `Embedded`.
  - Document why embedded exists (zero-dependency single node).
- **Effort**: S

### ARC-I-09 Telemetry duplication (Medium, Maintainability)
- **Evidence**
  - OpenTelemetry: `Api/Extensions/DependencyInjection/GatewayCoreServiceExtensions.cs:123-135` with an OTLP exporter and only two meters (`GatewayDiagnostics.MeterName`, `ClassificationDiagnostics.MeterName`).
  - prometheus-net: `app.UseHttpMetrics()` and `MapMetrics()` (`Api/Extensions/GatewayApplicationBuilderExtensions.cs:288,354`), plus 27 static `Prometheus.Metrics.CreateCounter` and similar declarations spread across layers: `Infrastructure/Cache/ConsentCacheService.cs:82-84`, `Infrastructure/Idempotency/RedisIdempotencyStore.cs:31`, `Infrastructure/RateLimiting/RedisRateLimiterService.cs:120`, `Application/Sql/CompiledSqlQueryPlanCache.cs:32-36`, `Api/Middleware/RateLimitingMiddleware.cs:29`, `Api/Security/BasicAuthenticationHandler.cs:32`.
  - No `AddPrometheusExporter` (grep).
- **Problem**
  - Two metric systems with different registries. `System.Diagnostics.Metrics` meters (policy evaluation duration, forbidden requests, query too complex) are exported only via OTLP, not on `/metrics`. Prometheus-net counters are not exported via OTLP. Application and Infrastructure depend directly on `Prometheus` (a hosting concern) via statics, which is untestable and not DI-scoped.
  - Cardinality is currently low: labels are `type`, `operation` and `limit_type`, all enumerations (`RateLimitingMiddleware.cs:32`, `RedisIdempotencyStore.cs:33`). HTTP metrics come from `UseHttpMetrics()`, whose default route labeling is bounded.
- **Impact**
  - Operators must scrape two backends for one dashboard.
  - Unit tests share global static counters.
- **Recommendation**
  - Standardize on `System.Diagnostics.Metrics` (Meter and instruments from `IMeterFactory`) everywhere.
  - Expose it with `OpenTelemetry.Exporter.Prometheus.AspNetCore` on `/metrics` plus OTLP.
  - Migrate the 27 prometheus-net statics.
  - Remove the `Prometheus` references from Application and Infrastructure.
- **Effort**: M

### ARC-I-10 Blocking startup I/O in DI factories (Medium, DI/Lifetime)
- **Evidence**
  - `GatewayStorageServiceExtensions.cs:55,67,81`: `StartServer()` and `ConnectionMultiplexer.Connect(...)` (synchronous) inside `AddSingleton` factories.
  - `SqliteGovernanceRepository.cs:77-80`: connection open plus schema DDL in the constructor.
  - `SqlServerGovernanceRepository.cs:~244` (`InitializeDatabaseSafely` in the constructor path).
- **Problem**
  - Constructor-time network and DDL I/O makes DI resolution slow and failure-prone. It runs on the first resolving thread, with no cancellation and hard to unit-test.
  - `AbortOnConnectFail=false` (`:64,80`) mitigates failure but not the blocking connect.
- **Impact**
  - Slow cold start and probe flapping, with sync-over-async risk under a constrained thread pool.
- **Recommendation**
  - Resolve `Lazy<IConnectionMultiplexer>`, or use `ConnectionMultiplexer.ConnectAsync` in an `IHostedService` that publishes a ready holder.
  - Move DB initialization to an `IHostedService` ahead of readiness (ties into ARC-I-04).
- **Effort**: S to M

### ARC-I-11 `GatewayDiagnostics` facade (Medium, Layering)
- **Evidence**
  - `Infrastructure/Diagnostics/GatewayDiagnostics.cs:1-20`: every member forwards to `Autheris.Domain.Diagnostics.GatewayDiagnostics`.
- **Problem**
  - A pure alias type in the wrong layer, one of two same-named classes. It adds confusion and an extra ownership point for a Domain-owned meter.
- **Recommendation**
  - Delete the Infrastructure copy and use the Domain one directly. Better: inject `IMeterFactory`-based metrics (see ARC-I-09).
- **Effort**: S

### ARC-I-12 Multiple ad-hoc SQLite files (Low, Data)
- **Evidence**
  - Governance (`SqliteGovernanceRepository.cs:75`), `Infrastructure/Plugins/PluginManager.cs`, `Application/Plugins/IPluginManager.cs`, `Api/Configuration/DevConfiguration.cs`, audit anchor stores (`Persistence/AuditChainAnchorStores.cs`, 467 lines), WORM export (`AuditWormExportService.cs`), and the lint source (`SqliteGovernanceLintSource.cs:18-19`, which opens its own read-only connection).
- **Problem**
  - There is no single "gateway state directory" policy covering location, backup, encryption-at-rest and retention, and each component decides its own path and connection handling.
  - Where it is the only anchor (audit chain), an unpersisted path is blocked only for non-dev (`SqliteGovernanceRepository.cs:190`).
- **Recommendation**
  - Introduce `IGatewayStatePaths` or an options section (`StateDirectory`), with the one rule "single writer per file" and a documented backup set.
- **Effort**: S

### ARC-I-13 Optional-everything constructors (Low, Testability)
- **Evidence**
  - `SqliteGovernanceRepository.cs:35-44`: 8 constructor parameters, 7 of them defaulted to `null`.
  - `Cache/EpochValidationService.cs:50-55`: `eventBus ?? new InProcessChannelEventBus()`, `sensitivityLookup ?? new TableMetadataSensitivityLookup(() => null)`.
  - `SqlConnector.cs:31-39`.
- **Problem**
  - Production wiring errors (a missing `IEventBus` or masking provider) silently become in-process or no-op behavior instead of failing at startup. For example, a missing `maskingProvider` degrades HMAC pseudonymization to redaction (`SqlConnector.cs:116` comment).
- **Recommendation**
  - Make security-relevant dependencies required. Keep the null-object defaults only in test builders.
- **Effort**: S

### ARC-G-01 Workflow logic in a GraphQL mutation (High, Layering/Consistency)
- **Evidence**
  - `GraphQL/Types/MutationTypes.cs:160-330` (`RequestTableAccessAsync`): argument validation (:190-218), idempotency (:220-228), tenant resolution, cross-tenant admin check, `ConsentRequest` construction, auto-approve (:261-273), ITSM dispatch with compensation by `DeleteConsentRequestAsync` (:283-318), and idempotent result storage.
  - A 6-parameter overload delegates to a 12-parameter one (:160-176) just to alias `IGovernanceRepository` as two interfaces.
  - `GraphQL/Types/QueryTypes.cs:268-300`: three `GetCatalogAsync` overloads with up to 10 `[Service]` parameters.
- **Problem**
  - The consent-request use case is implemented in the presentation adapter. Other transports (REST or MCP) can only get the same rules by copy-paste. The compensation logic is not transactional: a failure between `Create`, `Dispatch` and `UpdateTicketId` leaves orphans or relies on best-effort deletes.
  - Validation constants (5, 2000, 365) are hard-coded in the resolver.
- **Impact**
  - Divergent enforcement across REST, GraphQL and MCP (the stated requirement is a consistent policy path). Hard to unit-test without HotChocolate.
- **Recommendation**
  - Move the workflow to an Application service (`IConsentRequestService.SubmitAsync(cmd)`) returning a result type. The mutation maps `[Service] svc + args` to `cmd`, and errors to `GraphQLException`.
  - Use an outbox for the ITSM dispatch to replace delete-compensation.
  - Do the same for the catalog and "my access" queries.
- **Effort**: M

### ARC-G-02 Catalog change detection is N+1 and per node (Medium, Performance)
- **Evidence**
  - `GraphQL/Catalog/CatalogSchemaModel.cs:198-206`: a `foreach` over all active tables calling `GetRelationsForTableAsync` (the comment says "O(N)", but it is N queries).
  - `CatalogGraphQlTypeModule.cs:56-70,98-100`: `CheckForCatalogChangesAsync` rebuilds the entire model just to compute `Fingerprint()` every `CatalogSchemaRefreshSeconds` (default 60, `Domain/Options/GatewayOptions.cs:855`).
  - `GraphQL/Mcp/CatalogGraphQlMap.cs:196` also rebuilds the full model.
- **Problem**
  - With 1,000 tables and 5 nodes that is 5,000 governance queries per minute for a "did anything change" probe, and each tick allocates a full type model. There is no cross-node push: the Redis event bus already carries invalidations but is not used for catalog changes.
- **Impact**
  - Constant background load on the governance DB (which, for SQLite, is the global lock; see ARC-I-05).
- **Recommendation**
  - Add a cheap `ICatalogVersionProvider.GetVersionAsync()` (max `updated_at` plus count, or a version counter bumped by catalog writes).
  - Rebuild only when it changes. Publish a `catalog:changed` event on the existing `IEventBus` and keep the timer as a fallback.
  - Add a bulk `GetRelationsAsync(IEnumerable<TableIdentifier>)` repository method.
- **Effort**: M

### ARC-G-03 Subscriptions are single-node (Medium, Concurrency)
- **Evidence**
  - `Api/Extensions/GatewayServiceCollectionExtensions.cs:628`: `.AddInMemorySubscriptions()`.
  - `GraphQL/Subscriptions/CdcSubscriptionGovernor.cs:32`: `ConcurrentDictionary<string,int> _openPerSubject` is a per-process counter of open subscriptions.
  - `Subscriptions/Subscription.cs` resolves 7 services via the service locator.
- **Problem**
  - With Redis configured the rest of the system is cluster-aware, but subscription fan-out and per-subject limits are not. A publisher on node A will not reach a subscriber on node B unless CDC runs on every node. Per-subject limits multiply by the node count.
- **Recommendation**
  - Use the Redis-backed HotChocolate subscription provider (`AddRedisSubscriptions`) when `IConnectionMultiplexer` is registered.
  - Move the limiter to `IDistributedClusterStateProvider.IncrementAsync` or document the per-node semantics.
- **Effort**: M

### ARC-G-04 Pipeline composition is outside the module (Medium, Cohesion)
- **Evidence**
  - `Api/Extensions/GatewayServiceCollectionExtensions.cs:543-657` (`AddGatewayGraphQL`): depth and cost limits (`:547-553,632-640`), 9 hand-ordered `UseRequest<>` middlewares (`:612-624`), error filter, timeout (`:645-648`), and the relaxed-limits override (`maxDepth` 15 or 100, `maxCost` 5000 or 100000).
  - `GraphQL/Interceptors/*` contains the middleware implementations only.
- **Problem**
  - Limits and the order of the request pipeline are security-relevant but defined in Api. The GraphQL project has no `AddAutherisGraphQL()` of its own, so ordering constraints (for example `CostAndQuotaMiddleware` after variable coercion and before execution) are not testable within the project.
  - Positive: depth and cost limits are validation rules and are applied before execution (`AddMaxExecutionDepthRule`, `QueryCostAnalyzerRule`), plus per-tier cost (`CostAndQuotaMiddleware`). They are enforced once per request, not per resolver. The REST and OData paths use a different mechanism (`Api/Middleware/RateLimitingMiddleware.cs`, `Extensions/OData/ODataHandler.cs:405`), so limits are not unified as policy objects.
- **Recommendation**
  - Move the pipeline builder into `Autheris.GraphQL` as `AddAutherisGraphQL(options)`, with a unit test asserting the middleware order.
  - Express limits as one `QueryLimits` record consumed by GraphQL, OData and REST.
- **Effort**: S to M

### ARC-G-05 Dead and demo code in the production assembly (Medium, Maintainability)
- **Evidence**
  - `GraphQL/DynamicTypes/DynamicTableType.cs` (182 lines): no references outside the file (grep for `DynamicTableType`). It contains a fixed default `dialect = DatabaseDialect.PostgreSql` (`:92`) and a second masking path (`:36-44`) parallel to `CatalogGraphQlTypeModule` and `MaskedFieldsMiddleware`.
  - `GraphQL/Loaders/InvoiceItemDataLoader.cs` and `GraphQL/Types/InvoiceTypes.cs`: used only when `demoDataEnabled` (`GatewayServiceCollectionExtensions.cs:595-600`).
- **Problem**
  - An abandoned earlier "dynamic type" generator carries its own masking and fail-closed logic that can diverge from the live one. The sole DataLoader in the project is demo-only, which can mislead readers about how N+1 is handled.
- **Recommendation**
  - Delete `DynamicTableType`. Move the Invoice demo and loader to a `Demo` project or folder behind a conditional DI extension.
- **Effort**: S

### ARC-G-06 Per-request overhead in resolvers and middleware (Low, Performance)
- **Evidence**
  - `CatalogGraphQlTypeModule.cs:484-489`: copies all request headers into a new `Dictionary` per root field.
  - `Interceptors/CostAndQuotaMiddleware.cs:40-52`: falls back to `Document.ToString()` plus `Utf8GraphQLParser.Parse` when `OperationDocumentInfo.Document` is null (a double parse).
  - `CatalogGraphQlTypeModule.cs:512-546`: the full result is cloned (`doc.RootElement.Clone()`) and then copied into a second `List<JsonElement>`.
- **Impact**
  - Minor allocations per request. The double parse and two copies matter only at high row limits (`MaxResponseRows`).
- **Recommendation**
  - Pass headers lazily (`IHeaderDictionary` wrapper) and return `JsonElement` array enumeration without the second list. Order `CostAndQuotaMiddleware` after the operation resolver so the parsed document is always present.
- **Effort**: S

### ARC-G-07 Hard-coded dialect allowlist in the schema model (Low, Consistency)
- **Evidence**
  - `GraphQL/Catalog/CatalogSchemaModel.cs:105-111`: `dialect is SqlServer or PostgreSql or Sqlite`. Oracle and Databricks tables are hidden from GraphQL (R-GQL-13), while `SqlConnectionFactory.cs:98-104` and `DatabricksStatementExecutionClient` serve them via REST/OData.
- **Problem**
  - The capability matrix is encoded in the GraphQL layer, not discoverable by the Application layer. A silent difference between transports (a table is queryable via REST but absent from GraphQL) follows from ARC-I-02.
- **Recommendation**
  - Ask the dialect provider `Capabilities.SupportsTreeCompile`, and log which tables were excluded and why at schema build time.
- **Effort**: S (after ARC-I-02)

### ARC-G-08 Multi-overload resolvers as service locators (Low, Maintainability)
- **Evidence**
  - `GraphQL/Types/QueryTypes.cs:268-300` and `MutationTypes.cs:160-178` have repeated overloads with `= default!` service parameters.
  - `Subscriptions/Subscription.cs` resolves 7 services via `GetRequiredService`, and 11 more files call `GetService` or `ctx.Service<>` directly (see counts).
- **Problem**
  - `= default!` defaults on `[Service]` parameters mean a missing registration produces `NullReferenceException` at runtime instead of a clear DI error. Overloads that exist only to alias interfaces add noise.
- **Recommendation**
  - Inject one facade per resolver (see ARC-G-01) with non-nullable, required parameters. Remove the alias overloads.
- **Effort**: S

## 5. Answers to the review questions

- **Adding a new SQL dialect**
  - Touch about 15 to 20 places: `DatabaseDialect` and `TargetSqlDialect` plus mapper, `SqlConnectionFactory`, `TreeSqlCompiler`, `SqlDataSourceExecutor(.Projection)`, `GovernedSqlRewriter`, masking, RLS generators, `DatabaseParameterBudgetProvider`, `ConnectionTlsPolicy`, a column reader, the startup validator, and the GraphQL allowlist.
  - A governance-store dialect needs a new 5k-line repository (ARC-I-01, ARC-I-02).
- **Connection management**
  - A fresh `DbConnection` is rented per call by `SqlConnectionFactory` with disposal at the call site, `CommandTimeout` set (`SqlDataSourceExecutor.cs:137`) and a transaction scoped per query (`:440`). That part is sound.
  - Gaps: no explicit pool tuning surface, the SQLite governance shared connection (ARC-I-05), and materialization instead of streaming (ARC-I-06).
- **Redis down**
  - Epoch and consent decisions fail closed for sensitive tables (`EpochValidationService.cs:237,467`).
  - Generic state, locks, counters and TOTP reads degrade silently to "absent" (`RedisClusterStateProvider.cs`), so semantics are inconsistent (ARC-I-03).
- **Why Garnet and Redis**
  - Garnet is the embedded zero-dependency mode, selected by `EnableEmbeddedServer`. Both speak RESP through the same StackExchange.Redis client, which is why they share code (ARC-I-08).
- **Dynamic schema rebuild**
  - Done via `ITypeModule.TypesChanged` with a fingerprint-guarded timer. It is thread-safe at the guard level, but polling is N+1 and per node (ARC-G-02).
- **Enforcement consistency**
  - Depth, cost and tier limits are consistently validation or request middleware for GraphQL. REST and OData use separate mechanisms, and workflows such as consent requests live in the GraphQL layer only (ARC-G-01, ARC-G-04).
