# Autheris Architecture Review

Scope: `src/Autheris.{Domain,Application,Infrastructure,Api,GraphQL,Extensions}`, `src/TrinoSqlEngine`, checked against `docs/architecture/arc42.md` and `docs/adr/ADR-001..018`. All line references are from the working tree of branch `feat/ast-target-dialect-generator`. Paths are relative to the repository root. This is a static review; no code was run.

## 1. Summary

The layering is enforced and clean at the project-reference level: Domain references no project, GraphQL does not reference Infrastructure or Api, and `tests/Autheris.Tests.Architecture` pins these rules. The weaknesses lie elsewhere:

1. **Distributed-state consistency is only partly delivered.** Several security-relevant caches are invalidated per process only. Several invalidation paths are fire-and-forget. The in-process event bus can drop messages.
2. **God classes and a 1,692-line composition root** make change risky and testing expensive.
3. **The three governance repositories are near-copies.** They total about 14.5k lines.
4. **Docs claim more than the code delivers.** Oracle and Databricks "real execution" has no driver, and ADR-017 "migrated" statements are only half true.
5. **`Application` carries infrastructure dependencies** (DuckDB, Parquet, Arrow, Casbin, Http). Its dependency on `TrinoSqlEngine` is not in the arc42 layer diagram.

There are no Critical findings. There are 4 High, 9 Medium and 6 Low.

## 2. Architecture overview (as-is)

Project reference graph (from the `.csproj` files):

```
Domain (MemoryPack only)
  ^
Application -> Domain, TrinoSqlEngine   (Autheris.Application.csproj:10-11)
  ^        ^
Infrastructure   Extensions   GraphQL    (each -> Application + Domain)
  ^                              ^
Api (composition root) -> GraphQL, Infrastructure, Application, Domain, Extensions
```

- **Domain** holds value objects, options (`GatewayOptions.cs`, 1,726 lines) and the dialect enum (`Domain/Common/DatabaseDialect.cs:5`).
- **Application** is the largest project (280 files). It holds use cases and also the whole SQL pipeline (`Sql/Services/GovernedSqlExecutionService.cs`), Casbin policy, ReBAC, MCP, OLAP (DuckDB), FinOps, procedures, and many in-memory stores.
- **Infrastructure** holds:
  - the governance repositories (SQLite, PostgreSQL, SQL Server), each split into partial files;
  - `ConsentCacheService` and `EpochValidationService`;
  - Redis/in-process event buses, rate limiter, idempotency store and cluster-state provider;
  - the embedded Garnet server, plugins and CDC.
- **Api** holds endpoints, middleware, auth handlers and DI wiring (`Extensions/GatewayServiceCollectionExtensions.cs`, 1,692 lines, about 185 registration calls).
- **TrinoSqlEngine** is an ANTLR parser, AST builder, security visitor and per-dialect generators (`Ast/Generators/*`). It is independent of Autheris, which is enforced by `ArchitectureTests.cs:151`.

Request path: authentication (ForwardAuth, JWT, Basic, Negotiate) → GraphQL / SQL / MCP / OData endpoints → `TableAccessPolicy` consent resolution (`ConsentCacheService` L1/L2 plus epoch check) → `GovernedSqlExecutionService` (AST rewrite with RLS and masking, plan cache) → `SqlDataSourceExecutor` → target DB. Audit entries go through a bounded channel into a hash-chained governance table.

## 3. Strengths

- Layer rules are executable tests (`tests/Autheris.Tests.Architecture/ArchitectureTests.cs:17-175`): Domain, Application, Infrastructure, GraphQL and Extensions boundaries, plus no foreign-system clients in core. Source-level `using` checks found no `Autheris.Infrastructure/Api/GraphQL` usage in Domain, Application or Extensions.
- The plan-cache key isolates dialect, tenant, query and a policy hash covering RLS filters, masking, consent filters and the subquery strategy (`Application/Sql/CompiledSqlQueryPlanCache.cs:15-20`, `GovernedSqlExecutionService.cs:790-800`). The raw SQL is compared on a hit to rule out hash collisions (`CompiledSqlQueryPlanCache.cs:50-56`). Because policy content is part of the key, invalidation does not rely on events.
- The epoch service fails closed: on a Redis read error (INF-1), in degraded mode on sensitive tables (SEC-EPOCH-01), and beyond a staleness limit (SEC-EPOCH-02). It also detects epoch rollback (H-01). See `Infrastructure/Cache/EpochValidationService.cs:62-196`.
- Compiler-style SQL pipeline (parse, AST, security visitor, dialect generator) instead of token rewriting, per ADR-017.
- Interface-first design with in-memory and Redis implementations selected at composition (`GatewayServiceCollectionExtensions.cs:245-272`). This gives a zero-dependency developer mode.
- The audit channel uses `BoundedChannelFullMode.Wait` (`SqliteGovernanceRepository.cs:82`), so audit entries are not silently dropped, and an audit-fault flag exists (`:23`).

## 4. Findings

| ID | Severity | Location | Description | Recommendation |
|---|---|---|---|---|
| AR-01 | High | `Application/Policy/TableAccessPolicy.cs:103-113,544-569`; `Api/Endpoints/GovernanceEndpoints.cs:662,782` | Access-profile cache invalidation is incomplete. `InvalidateCache` is `static` and removes only from the static `_profileCache` (`:105-108`). When `IMemoryCache` is injected, profiles live in `_memoryCache` under `access_profile:{tenant}:{subject}` (`:544-559`) and are never evicted by it. In the memory-cache branch a profile change therefore stays effective for up to 5 minutes. In any case no cross-node invalidation exists. Access profiles are an authorization input (R-52), so this is a stale-grant window. | Make invalidation an instance method on an `IAccessProfileCache` that removes from `IMemoryCache`. Publish it on the event bus, or key the entry on the table or subject epoch as for consents. Drop the static dictionary. |
| AR-02 | High | `Application/Security/Rebac/Services/ZanzibarRebacEvaluator.cs:86-95` | `InvalidateTenantCache` fires `IncrementGenerationAsync()` and `PublishInvalidationAsync()` as unobserved `_ =` tasks. A failed publish is only logged (`:100-104`) and peers keep stale relationship tuples with no retry and no fail-closed fallback. | Await the publish (make the method async) or put it through an outbox with retry. On failure, mark local ReBAC state non-authoritative, as `EpochValidationService` does. |
| AR-03 | High | `Infrastructure/Messaging/InProcessChannelEventBus.cs:20-31`; `Infrastructure/Cache/EpochValidationService.cs:44-52` | The in-process bus uses `BoundedChannelFullMode.DropOldest` with capacity 10,000. Invalidation messages (epoch bumps) share that queue with other events and can be dropped under load. `EpochValidationService` defaults to this bus (`:37`). Within one process the epoch is also incremented synchronously (`:197`), so single-node impact is limited. Any consumer that relies only on events for invalidation (for example ReBAC via `_eventBus`) can lose them. | Use a separate non-lossy path (`FullMode.Wait` or a dedicated channel) for invalidation topics. Document that the bus gives no delivery guarantee. Require Redis in multi-node mode and fail at startup otherwise. |
| AR-04 | High | `ADR-017` "Migration of HitL, McpSessionStore, Focus, TokenRevocation" vs `Application/Mcp/Services/McpSessionStore.cs:19-21,128-135,173-186`, `HitLStepUpApprovalService.cs:20,182-186`, `DifferentialPrivacyEngine.cs:16`, `ClientTierResolver.cs:18`, `DbtHealthCircuitBreaker.cs:18`, `SubgraphCanaryRouter.cs:19` | The ADR says "Akzeptiert & Umgesetzt", but the migration is hybrid. Session and HitL state stay authoritative in a local `ConcurrentDictionary` and the cluster store is an optional write-through (the session persist is a background write, `McpSessionStore.cs:178`). Other per-process state is not distributed at all. Differential-privacy budgets are the clearest case: `DifferentialPrivacyEngine._budgets` is a per-node `ConcurrentDictionary`, so a client's privacy budget is multiplied by the node count. The same applies to `FocusCostAccountingService._localSpend` and the golden queries. | Either change the ADR status to "partially implemented" and list the exceptions, or finish the migration. At least the privacy budget needs a shared atomic counter (Redis INCRBY or a DB row) because it is a security control. |
| AR-05 | Medium | `Application/Sql/Services/GovernedSqlExecutionService.cs` (2,025 lines; `RewriteCoreAsync` `:138-~850`, `ExecuteCoreAsync` `:862-1135`, `ExecuteQueryBufferedAsync` `:1249-~1900`, nested `SyntheticDataTableReader` `:1971`); `Application/Governance/CasbinEnforcementService.cs` (1,760); `Application/Services/GatewayExecutionService.cs` (1,143) | God classes. `RewriteCoreAsync` alone is about 700 lines of sequential steps (the code comments number them "7. Rewrite SQL AST"). The constructor (`:80-125`) takes a very long parameter list. Security steps cannot be unit-tested in isolation, and ordering mistakes are easy. | Extract a pipeline of explicit stages (`IRewriteStage`: classify, resolve tables, consent, RLS, masking, rewrite, plan cache), each with its own tests. Move `SyntheticDataTableReader` to its own file. |
| AR-06 | Medium | `Api/Extensions/GatewayServiceCollectionExtensions.cs` (1,692 lines; about 185 registrations; sync-over-async seeding `:661`; empty `catch { }` `:1624`; ad-hoc `new GarnetServerManager(...)` plus `StartServer()` during registration `:229-233`) | The composition root is one file with conditional branches by backend. It starts a server and opens a connection while the container is being built, which makes registration order-dependent and untestable. | Split into per-feature modules (`AddGatewayCaching`, `AddGatewayMcp`, ...). Move startup side effects (Garnet start, ReBAC seeding) into `IHostedService`s. |
| AR-07 | Medium | `Infrastructure/Persistence/{Sqlite,PostgreSql,SqlServer}GovernanceRepository*.cs` (about 14.5k lines in total; e.g. `*.Consent.cs` 1,645 / 1,535 / 1,549 lines) | Three near-duplicate implementations of the same repository interfaces, split into partials. Every policy or schema change must be made three times, and a missed provider gives a silent security divergence. Provider contract tests are the only safeguard. | Extract shared SQL building and mapping into a base class with a small dialect hook. At minimum add a shared contract test suite that runs against all three providers. |
| AR-08 | Medium | `Infrastructure/Persistence/SqliteGovernanceRepository.cs:19-30` (one `SqliteConnection` plus one `SemaphoreSlim(1,1)`; about 54 lock acquisitions across the partials) | A singleton repository holds a single connection and serializes all governance reads and writes behind one semaphore, including the consent resolution path (`.Consent.cs`, 18 acquisitions). Throughput on the Sqlite provider is capped by a global mutex. | Acceptable for dev. Say so in the docs and add a startup warning when the Sqlite governance provider is used outside Development. Use a connection pool (`Cache=Shared` / WAL) if it is meant for production. |
| AR-09 | Medium | `Infrastructure/Cache/EpochValidationService.cs:62-100,170-196` | Every consent-cache hit does a Redis `GET` on the epoch key per table (`IsEpochValidAsync` → `GetCurrentEpochCoreAsync` → `StringGetAsync`). `GetCurrentEpochsAsync` (`:117-127`) reads them sequentially. This costs one Redis round trip per table per request and works against the "P99 < 15 ms" goal in arc42 1.2. Epoch rollback recovery (`:75-85`) uses a non-atomic read-then-set. | Batch with `MGET` or pipelining, run reads in parallel, and cache the epoch for a short TTL, bounded by the staleness budget that is already defined. Make the rollback fix an atomic Lua `SET` if larger. |
| AR-10 | Medium | `Application/Sql/CompiledSqlQueryPlanCache.cs:94-97` | When 10,000 entries are reached, the whole cache is cleared (`_cache.Clear()`), which creates a thundering-herd of re-rewrites. Expired entries are removed only on lookup. The size check and write are not atomic. Each node has its own plan cache. | Use `MemoryCache` with a size limit, or an LRU with a single eviction. Add hit/miss metrics. |
| AR-11 | Medium | `Application/Autheris.Application.csproj:10-24` (DuckDB.NET, Parquet.Net, Apache.Arrow, Casbin.NET, Microsoft.Extensions.Http, `TrinoSqlEngine`); `Application/Olap/DuckDbOlapEngine.cs`; `Application/Mcp/Services/PreFlightQuerySimulator.cs`, `Application/SchemaRegistry/*` (use HotChocolate namespaces) | `Application` carries infrastructure packages (embedded database engine, file formats, outbound HTTP) and GraphQL-library types. The arc42 solution strategy (section 4.1) describes it as use cases only, and the architecture tests check only ASP.NET Core and project references. | Move DuckDB/Parquet/Arrow and the OLAP engine to Infrastructure behind an interface. Add `HotChocolate.Language` and package-level checks to the architecture tests. Update arc42 with the `TrinoSqlEngine` dependency. |
| AR-12 | Medium | `Application/Mcp/Services/McpSessionStore.cs:19-21,62`; `HitLStepUpApprovalService.cs` (7 `lock (entry.Lock)` blocks); `Application/Governance/CasbinEnforcementService.cs:344,687` | The request path takes global or per-entry locks (`_createLock`; `lock (enforcer)` around every policy evaluation). The Casbin enforcer is a shared mutable object, so all authorization evaluations for a tenant serialize on one monitor. This is CPU-bound and short, but it is a scalability ceiling. | Use immutable snapshots per policy epoch (the code already builds `PolicySnapshot`s) and evaluate lock-free. Keep the lock only for publishing. |
| AR-13 | Medium | `docs/architecture/arc42.md:36,54`; `Infrastructure/Persistence/SqlConnectionFactory.cs:31-38` | Docs claim "Native ADO.NET execution with RLS pushdown across SQL Server, PostgreSQL, SQLite, Oracle, and Databricks". The connection factory supports only Sqlite, SqlServer and PostgreSql (the code comment says "Oracle and Databricks are dialects without a driver here"). Databricks has no AST generator either (`TargetSqlDialect` has Oracle and Snowflake but no Databricks, `TrinoSqlEngine/IRlsPolicyProvider.cs:349-358`), while `DatabaseDialect.Databricks` is referenced in `SqlDataSourceExecutor.cs:337,681-710` as dead paths. The two dialect enums (`Domain/Common/DatabaseDialect.cs:5`, `TargetSqlDialect`) are not aligned. | Correct arc42 sections 2 and 3 (Oracle and Databricks as "dialect only, no driver"). Unify the enums via one mapping table, and remove or implement the Databricks branches. |
| AR-14 | Low | `Infrastructure/Cache/EpochValidationService.cs:21,34,52-57,150`; `Application/Policy/TableAccessPolicy.cs:93-103` | Service locator and optional dependencies. `EpochValidationService` takes `IServiceProvider` to break a circular dependency with the repository (`:36-38`), and falls back to `new InProcessChannelEventBus()` when none is supplied (`:37`). `TableAccessPolicy` takes `IConsentCacheService?`, `IPolicyEnforcementService?`, `IRebacEvaluator?` and `GatewayOptions?` as nullable. Misconfiguration silently disables a security control instead of failing at startup. | Break the cycle with a small `ITableSensitivityLookup` interface. Make security collaborators required, using Null-object implementations where "none" is valid, as `NullMandatoryRowFilterResolver` already does. |
| AR-15 | Low | `Api/Security/BasicAuthAttemptGuard.cs:183-187,260-262,293-295` | Synchronous `IsLockedOut`, `RecordFailure` and `RecordSuccess` wrap async calls with `.GetAwaiter().GetResult()`. They are used only by tests today (`tests/Autheris.Tests.Unit/BasicAuthOptimizationTests.cs:129-134`), but in the request path they would block thread-pool threads and risk starvation if a Redis-backed guard is used. | Delete the sync wrappers and migrate the tests to the async API. |
| AR-16 | Low | `Application/Mcp/Services/AiDataGuardrailService.cs:176`; `GraphQL/Subscriptions/Subscription.cs:99`; `GraphQL/Subscriptions/WebSocketAuthInterceptor.cs:387`; `Api/Extensions/GatewayServiceCollectionExtensions.cs:661` | Unobserved fire-and-forget tasks (`_ = ...Async()`) on subscription revocation checks and progress events, and sync-over-async at startup (`:661`). The revocation watchers are security relevant: if `CheckRevocationAsync` faults, the subscription is never terminated. | Wrap in a helper that logs faults and, for revocation, cancels the subscription on fault. Run startup seeding in an async hosted service. |
| AR-17 | Low | `Application/Sql/Services/WebSqlStatementManager.cs:119,137,428-429`; `Api/Extensions/GatewayServiceCollectionExtensions.cs:1624` (empty `catch { }`); 257 `catch (Exception` occurrences in `src` | Swallowed exceptions around `CancellationTokenSource` disposal are benign, but the broad `catch (Exception` count shows no shared error-handling policy. Classification exists (`Application/Common/DataAccessErrorClassifier.cs`) but is not enforced at the boundaries. | Add one boundary filter (GraphQL error filter, endpoint filter) and an analyzer rule against catch-all outside the boundary. |
| AR-18 | Low | `Domain/Options/GatewayOptions.cs` (1,726 lines); `Domain/Autheris.Domain.csproj` (MemoryPack, DataAnnotations); arc42 4.1 "Pure domain core with no external dependencies" | Domain holds the entire configuration surface (caching, Redis, Garnet, WebSql ...) and a serializer package, which conflicts with the arc42 statement. This couples every layer to one options file and makes it a merge hotspot. | Split options per feature and move them to the owning project, or correct the arc42 sentence. |
| AR-19 | Low | `Application/Sql/Services/GovernedSqlExecutionService.cs:784-812` vs `Application/Sql/CompiledSqlQueryPlanCache.cs` | The plan cache is optional (`ICompiledSqlQueryPlanCache? planCache = null`, `:93`), so behaviour differs between hosts and tests depending on registration. There is no metric or test hook for hit rate, and no distributed or shared tier although the cache key is already tenant-safe. | Register it always, with a size-0 option to disable. Export hit/miss counters. |

## 5. Docs vs code drift

| Claim | Evidence | Status |
|---|---|---|
| arc42 2/3: Oracle and Databricks real execution | `SqlConnectionFactory.cs:31-38`, no Databricks generator | Not implemented (AR-13) |
| ADR-017: HitL, session, FinOps, TokenRevocation migrated to cluster state | local dictionaries remain authoritative (AR-04); `TokenRevocationService` does have a Redis implementation (`Infrastructure/Security/RedisTokenRevocationService.cs`) | Partly implemented |
| arc42 4.1: Domain without external dependencies; Application "use cases" | `Autheris.Domain.csproj`, `Autheris.Application.csproj:10-24` | Drift (AR-11, AR-18) |
| arc42 4.5: "RedisEventBus propagates monotonic epoch increments" | Redis is the source of truth for the epoch (`EpochValidationService.cs:70`); the bus message only bumps the local counter by +1 (`:47`), which is a different, non-monotonic-with-Redis value, but it is overwritten on the next Redis read | Works, but the description is inaccurate |
| arc42 1.2: P99 < 15 ms consent resolution | One Redis GET per table per check (AR-09); no latency test found in `tests/` | Unverified |

## 6. Prioritized roadmap

**Now (security-relevant consistency)**
1. AR-01: correct the access-profile cache invalidation and make it cluster-aware.
2. AR-02 and AR-03: awaited, loss-free invalidation for ReBAC and epoch events.
3. AR-04: a shared atomic privacy budget. Update ADR-017 to the real status.

**Next (correctness and maintainability)**
4. AR-13: align the docs on Oracle and Databricks and unify the dialect enums.
5. AR-14 and AR-19: make security collaborators required and plan cache registration deterministic.
6. AR-06: split the composition root and move side effects into hosted services.
7. AR-05: break the SQL rewrite pipeline into stages with per-stage tests.

**Later (scalability and structure)**
8. AR-09 and AR-10: batch epoch reads, add a bounded plan cache, add latency and hit-rate metrics.
9. AR-12: lock-free policy evaluation on immutable snapshots.
10. AR-07 and AR-08: shared repository base and contract tests. Document the Sqlite limits.
11. AR-11 and AR-18: move OLAP and Parquet to Infrastructure, split the options, and extend the architecture tests.
12. AR-15, AR-16, AR-17: remove sync-over-async, observe fire-and-forget tasks, add a central error policy.
