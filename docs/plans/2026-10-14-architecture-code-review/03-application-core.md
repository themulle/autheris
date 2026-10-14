# Architecture Review 03 - Application governance/query core

Scope: `src/Autheris.Application/{Security,Policy,Governance,Audit,Caching,State,Sql,SqlEndpoints,VirtualFilters,Federation,Data,Services,Interfaces,Common,Performance,ResourceGroups,Procedures}` (paths below relative to `src/Autheris.Application/`). READ-ONLY review; evidence cited as `path:line`. Line numbers are from the working tree at main (88f7df1c).

## 1. Metrics and pipeline map

| Folder | files | LOC | | Folder | files | LOC |
|---|---|---|---|---|---|---|
| Sql | 48 | 8,699 | | Interfaces | 38 | 1,276 |
| Services | 21 | 6,118 | | Federation | 6 | 543 |
| Governance | 37 | 5,653 | | Data | 4 | 763 |
| VirtualFilters | 26 | 5,003 | | SqlEndpoints | 5 | 738 |
| Procedures | 16 | 4,160 | | Audit/Caching/State/Common/Perf | 14 | ~1,250 |
| Security | 24 | 2,879 | | ResourceGroups | 3 | 574 |
| Policy | 19 | 2,070 | | **Total (slice)** | ~270 | ~49,900 |

Whole Application project: 61,373 LOC.

Top 20 files by LOC: CasbinEnforcementService 1897, VirtualFilterAdministrationService 969, ProcedureDefinitionParser 844, GovernedSqlRewriter 803, DeclarativeHttpDataSourceExecutor 798, GovernedProcedureExecutionService 779, TableAccessPolicy 746, TreeSqlCompiler 736, GatewayExecutionService 693, SqlDataSourceExecutor 683, FederatedDuckDbExecutionService 670, ColumnMaskingProvider 563, AccessPlanningService 560, GovernedSqlExecutor 555, SqlEndpointLoader 546, GovernedTreeQueryService 530, StoredProcedureCatalogValidator 530, WebSqlStatementManager 526, MandatoryRowFilterResolver 486, SqlDataMaskingProvider 481.

Hygiene counters (slice): `catch (Exception` 78; bare `catch` 21 (several empty); `lock(` 19; `Task.Run` 2; sync-over-async (`.Result`/`GetAwaiter().GetResult()`) 2 real sites; CancellationToken mentions 530 (token discipline is generally good).

### Pipeline map (as found)

```
Single decision point (good):  TableAccessPolicy (Policy/) -> IPolicyEnforcementService (Casbin) -> TableAccessDecision
                               (row filter SQL + column access + virtual filters + masks)

Path A  GraphQL/OData/MCP table reads
  GatewayExecutionService.ExecuteTableQueryAsync -> ResolveTableAccessAsync -> audit(DENY) -> IDataSourceExecutor
    -> SqlDataSourceExecutor: STRING-composed WHERE (row-filter SQL text + $filter + tenant col)   [no AST]
    -> DeclarativeHttpDataSourceExecutor: HTTP + InMemoryRowFilterEvaluator + ColumnMaskingProvider [in-memory]
    -> audit(ALLOW) after execute

Path B  WebSQL / SQL endpoints
  GovernedSqlExecutionService (facade) -> GovernedSqlRewriter (AST: parse->authorize->RLS->mask, ISqlRewritePipeline)
    -> GovernedSqlExecutor (compile/execute/DML tx/audit)  | or FederatedDuckDbExecutionService (cross-source:
       AuthorizeAll -> plan -> start-audit (fail-closed) -> stage -> DuckDB -> failure-audit (best-effort))

Path C  Tree/structured queries
  GovernedTreeQueryService -> ITableAccessResolver -> TreeSqlCompiler (own compiler) -> ISqlConnectionFactory -> own audit

Path D  Stored procedures
  GovernedProcedureExecutionService -> own consent/policy/rowscope/masking assembly (uses TableAccessPolicy directly)
    -> MssqlProcedureInvoker -> own audit
```

So: ONE decision authority, but FOUR execution/enforcement/audit pipelines, with two masking engines (SQL-AST vs in-memory) and two row-filter enforcement mechanisms (AST rewrite vs string WHERE concatenation).

## 2. Strengths

- Single policy decision object (`TableAccessDecision`) built by `Policy/TableAccessPolicy.cs` with documented stages; protocol paths consume it rather than re-deriving policy.
- `ISqlRewritePipeline` + `StageOrder` (`Sql/Pipeline/ISqlRewritePipeline.cs:17`, `Sql/Services/GovernedSqlExecutionService.cs:36,46`) makes rewrite stage ordering explicit and testable.
- Casbin policy state is published as an immutable snapshot with epoch (`Governance/CasbinEnforcementService.cs:411-427`) and the cache key embeds `snapshot.Epoch` (`:712`), so stale decisions are structurally unreachable.
- Epoch compare-and-set on consent-cache writes (`Policy/TableAccessPolicy.cs:524-558`) avoids the classic read-then-stale-write race.
- Cross-source federation is fail-closed at start-audit (`Sql/Services/FederatedDuckDbExecutionService.cs:552-555`) and single-flight key-set loading is carefully written (`VirtualFilters/Federation/VirtualFilterKeySetCache.cs:40-95`).
- Cancellation tokens are threaded through nearly all async paths; sync-over-async is rare (2 sites).

## 3. Findings

| ID | Sev | Category | Title |
|---|---|---|---|
| ARC-C-01 | High | Consistency | Four parallel enforcement/audit pipelines; string-WHERE RLS path vs AST rewrite path |
| ARC-C-02 | High | Layering | Application contains ADO.NET, DuckDB, HTTP and driver-specific (Oracle) execution code |
| ARC-C-03 | High | Consistency | Inconsistent audit failure semantics (fail-closed vs swallow vs audit-optional by constructor) |
| ARC-C-04 | High | DI/Lifetime | Optional-everything constructors with `NullAuditLogRepository` default and service-locator fallback; fail-open wiring |
| ARC-C-05 | High | Concurrency | AccessPlanningService holds plans/consumed tokens/secrets in per-node memory, unbounded |
| ARC-C-06 | Medium | Cohesion | God classes: CasbinEnforcementService (1897), VirtualFilterAdministrationService (969), TableAccessPolicy |
| ARC-C-07 | Medium | Concurrency | Casbin decision cache: global `Clear()` at 10k, lock-held-while-notifying, node-local invalidation |
| ARC-C-08 | Medium | Concurrency | `WebSqlStatementManager`: Task.Run + sync Result/GetResult, session leak/ordering hazards |
| ARC-C-09 | Medium | Resilience | Swallowing catches (bare `catch {}`) incl. audit and rollback, and unlogged fail-closed catch in ReBAC gate |
| ARC-C-10 | Medium | Cohesion | `Services/` junk drawer + `Interfaces/` bucket vs per-feature placement |
| ARC-C-11 | Medium | Maintainability | Duplicate/forwarding API surface (GatewayExecutionService.FilterRows etc.), twin constructors |
| ARC-C-12 | Medium | Consistency | Two masking engines and two row-filter evaluators with separately maintained semantics |
| ARC-C-13 | Medium | Concurrency | State provider: in-memory fallback has single global lock and diverging semantics from distributed |
| ARC-C-14 | Medium | Performance | Audit written synchronously on hot read path (ALLOW + DENY), before/after execute |
| ARC-C-15 | Medium | Resilience | Fire-and-forget `Task.Run(CancellationToken.None)` in key-set cache without load bound on shutdown |
| ARC-C-16 | Low | Maintainability | Interface explosion in Application/Interfaces (38) with many single-impl, infra-flavored contracts |
| ARC-C-17 | Low | Testability | `Null*`/test-only types in production assembly |
| ARC-C-18 | Low | Cohesion | Folder structure inconsistent (Sql/Policy vs Policy, Sql/Interfaces vs Interfaces, Caching with 1 impl, Performance only streaming) |
| ARC-C-19 | Low | Consistency | Mixed German/English comments and magic constants (cache cap 10_000, fallback timeouts) in core code |

### ARC-C-01 (High, Consistency) Four parallel pipelines; two RLS mechanisms
- Evidence: `Services/GatewayExecutionService.cs:121-280` (own authorize/audit), `Services/SqlDataSourceExecutor.cs:235-345` (WHERE assembled from `CombinedRowFilterSql` text with `whereParts.Add($"({rowFilterSql})")` at `:263`), `Sql/Services/GovernedSqlRewriter.cs:40` (AST), `Sql/Tree/GovernedTreeQueryService.cs:74,356` (own audit + own compiler `TreeSqlCompiler.cs`), `Procedures/Services/GovernedProcedureExecutionService.cs:393-422,709` (own policy assembly + audit).
- Problem: the "parse -> authorize -> rewrite -> compile -> execute -> audit" pipeline exists per protocol. GraphQL/OData uses textual row-filter predicates appended to hand-built SQL (including dialect hacks `OracleBindByName.RewriteMarkers` at `SqlDataSourceExecutor.cs:260`), while WebSQL uses AST rewrite. Each pipeline re-implements tenant column enforcement, `$filter` column-access checks (`SqlDataSourceExecutor.cs:296-312`), limits and audit shapes.
- Impact: every new control (e.g. new masking mode, new audit field, new fail-closed rule) must be implemented 3-4 times; divergence is the main long-term bypass risk and is not detectable by a single test suite.
- Recommendation: introduce an `IGovernedQueryPipeline` in Application with explicit stages (Authorize -> Rewrite -> Compile -> Execute -> Audit) operating on a protocol-neutral `GovernedQuery` (table access decision + AST/Tree). Make `TreeSqlCompiler` and the table-read path emit an AST and feed the same `GovernedSqlRewriter` stages (or at minimum a shared `IRowFilterApplier`/`IAuditedExecution` wrapper). Procedures keep a specialised executor but reuse the shared authorize+audit stages. Add an architecture test that every `IDataSourceExecutor`/executor goes through the pipeline.
- Effort: L

### ARC-C-02 (High, Layering) Infrastructure inside Application
- Evidence: 116 matches for ADO.NET/HTTP/IO tokens in the slice; `Services/SqlDataSourceExecutor.cs:136,444` (`CreateCommand`, `ExecuteReaderAsync`), `Services/DeclarativeHttpDataSourceExecutor.cs` (798 LOC, HttpClient), `Sql/Services/FederatedDuckDbExecutionService.cs:154-155,332` (DataTable/DuckDB), `Sql/OracleBindByName.cs`, `Procedures/Services/MssqlProcedureInvoker.cs`, `Procedures/Services/ProcedureConnectionProvider.cs`, `Security/SecureOutboundHttp.cs:57`, `Security/SsrfProtectionHandler.cs:50` (takes `IServiceProvider`), `Services/DbSessionContextInitializer.cs:62-68`, `SqlEndpoints/Services/SqlEndpointLoader.cs`, `Procedures/Services/ProcedureDefinitionLoader.cs` (file IO).
- Problem: Application owns dialect-specific connection/command code, HTTP handlers and file loading; Onion boundary is only nominal (`ISqlConnectionFactory` exists, but commands and dialect quirks live here).
- Impact: Application cannot be unit-tested without ADO providers; adding a dialect touches Application; blocks the pipeline unification in ARC-C-01.
- Recommendation: define narrow ports (`ISqlCommandRunner.ExecuteAsync(CompiledSql, ct)`, `IHttpSourceClient`, `IProcedureInvoker`, `IDefinitionFileSource`) in Application and move `SqlDataSourceExecutor`, `DeclarativeHttpDataSourceExecutor`, `MssqlProcedureInvoker`, `SecureOutboundHttp/SsrfProtectionHandler`, `OracleBindByName`, loaders into Infrastructure (or `Autheris.Extensions`). Keep policy-driven query construction in Application.
- Effort: L

### ARC-C-03 (High, Consistency) Audit failure semantics differ per path
- Evidence: fail-closed start audit `Sql/Services/FederatedDuckDbExecutionService.cs:552-555`; best-effort failure audit that only logs `:643-647`; WebSQL executor swallows audit failure with bare `catch { }` `Sql/Services/GovernedSqlExecutor.cs:404-406`; GraphQL path awaits audit un-guarded (propagates) `Services/GatewayExecutionService.cs:175,257,278`; ALLOW audit recorded after rows are materialised `:278`.
- Problem: no single policy: some paths deny when audit is down, others return data, others silently drop denial records. For Zero-Trust compliance the rule ("no audited, no data" for ALLOW; "never lose DENY") is not enforced in one place.
- Impact: audit gaps under DB outage depend on which protocol was used; hard to certify.
- Recommendation: encapsulate in an `IAuditedOperation`/`AuditGuard` helper with explicit modes (`FailClosedAllow`, `BestEffortDeny` -> fall back to durable local spool/outbox), configure per event type, and use it everywhere. Replace bare catches with the helper.
- Effort: M

### ARC-C-04 (High, DI/Lifetime) Optional dependencies default to no-op; service-locator
- Evidence: `Sql/Services/GovernedSqlExecutionService.cs:51-77` (24 nullable ctor params, first overload hard-wires `NullAuditLogRepository.Instance` at `:74`; `auditLogRepository ?? NullAuditLogRepository.Instance` `:133`); same pattern `Procedures/Services/GovernedProcedureExecutionService.cs:74`, `Governance/Services/DifferentialPrivacyEngine.cs:36`, `Governance/Services/EuAiActAuditExporter.cs:35`; `IServiceProvider? serviceProvider` for lazy `IFederatedQueryExecutionService` (`GovernedSqlExecutionService.cs:44,71,147-150`); `GovernedSqlExecutionService` constructs `GovernedSqlRewriter` and `GovernedSqlExecutor` itself (`:97-130`) instead of DI; `NullPolicyEnforcementService` fallback `GovernedProcedureExecutionService.cs:393`.
- Problem: a mis-registered container silently yields a service with no audit, no policy, no validator (fail-open by construction). The overload pair exists for tests, leaking test convenience into production wiring. Service locator hides a cycle (federated service <-> governed SQL).
- Impact: security controls become optional at composition time; DI validation cannot catch it; 24-param constructors are unmaintainable.
- Recommendation: make security-relevant dependencies mandatory (non-null); keep Null objects only in the test project. Group parameters into small context objects (`GovernedSqlDependencies`). Register `GovernedSqlRewriter`/`GovernedSqlExecutor` in DI. Break the cycle with `Lazy<IFederatedQueryExecutionService>` or by having the router (not the facade) own the federated dispatch. Add a startup test that resolves all governed services with `ValidateOnBuild`.
- Effort: M

### ARC-C-05 (High, Concurrency) Admin access-plan state is process-local and unbounded
- Evidence: `Governance/Services/AccessPlanningService.cs:48-50` (`_plans`, `_consumedTokens`, `_inMemoryVault` ConcurrentDictionary), writes at `:149,205,250,332`; reads `:175,222,488`. No removal found (no `TryRemove`/eviction).
- Problem: plans, single-use confirmation tokens (replay protection) and even plaintext-bytes secrets (`_inMemoryVault[secretRef] = Encoding.UTF8.GetBytes(request.Auth.Secret)`, `:332`) are kept per node, forever. In a multi-node cluster a plan created on node A cannot be confirmed on node B, and a token consumed on A can be replayed on B (replay protection is local). Expired plans are never evicted (memory growth).
- Impact: correctness in HA deployments, replay window, slow leak, secret retention in heap.
- Recommendation: move to `IDistributedClusterStateProvider` (already designed for this: `State/IDistributedClusterStateProvider.cs`) with TTL = plan ExpiresAt, use `TryAcquireLockAsync`/SetNX for token consumption, and store secrets only via `IKeyVaultSecretProvider` (drop `_inMemoryVault`).
- Effort: M

### ARC-C-06 (Medium, Cohesion) God classes
- Evidence: `Governance/CasbinEnforcementService.cs` 1897 LOC (policy parsing `:1037,1326`, snapshot build `:411`, decision cache `:712-726,865`, reload/diagnostics `:1792,1877`); `VirtualFilters/VirtualFilterAdministrationService.cs` 969 LOC (history locks `:666,753`, drift detection `:808`, contributor ids `:913,928`); `Policy/TableAccessPolicy.cs` 746 LOC; `Procedures/Services/ProcedureDefinitionParser.cs` 844; `Sql/Services/GovernedSqlRewriter.cs` 803; `Sql/Tree/TreeSqlCompiler.cs` 736.
- Problem: multiple responsibilities per type (parse, evaluate, cache, notify, diagnose, administer).
- Impact: high change risk on the most security-critical code, long review cycles, difficult targeted tests.
- Recommendation: split Casbin into `PolicyParser`, `PolicySnapshotStore` (publish/epoch), `DecisionCache`, `PolicyEvaluator`, `PolicyDiagnostics`. Split VirtualFilterAdministration into CRUD, drift/hash and history services. Keep `TableAccessPolicy` as orchestrator over stage classes.
- Effort: L

### ARC-C-07 (Medium, Concurrency) Casbin decision cache and publish semantics
- Evidence: `Governance/CasbinEnforcementService.cs:724-727` (`if (Count >= 10_000) Clear()` - non-atomic count/clear, cache stampede after clear), cache key is a ~12-part string concatenated on every call (`:712`), `:418-427` (snapshot publish under `_syncLock`; tenant list computed and notifications composed inside/after the lock), `:426` `_decisionCache.Clear()`. Casbin has no `IDistributedClusterStateProvider` use (only 3 files in Governance/Procedures reference it), so epoch/snapshot is node-local.
- Problem: allocation-heavy key, global flush eviction, per-node policy epoch: in a cluster a policy change reaches other nodes only through whatever reload mechanism exists, with no shared epoch to prove staleness bound.
- Impact: latency spikes at 10k entries; potential stale-allow window across nodes after policy change.
- Recommendation: use a bounded `MemoryCache` (size-limited, LRU-ish) or `ConcurrentLru`; hash the key into a struct (`readonly record struct DecisionKey`); publish the policy epoch through `IDistributedClusterStateProvider.PublishEventAsync` and compare epochs on read (like consent epochs). Document the maximum staleness.
- Effort: M

### ARC-C-08 (Medium, Concurrency) WebSqlStatementManager async hygiene
- Evidence: `Sql/Services/WebSqlStatementManager.cs:156` (`Task.Run` with `sessionCts.Token` - if cancelled before start, scope never created, fine, but task unobserved if the waiter times out), `:309` `session.ExecutionTask.Result` (guarded by IsCompletedSuccessfully - acceptable), `:452` `task.GetAwaiter().GetResult()` inside `CanceledGovernedError`, `:119,137,442-443` bare `try { } catch { }` around `Cts.Cancel()/Dispose()`; `_sessions[statementId] = session` assigned after the task starts (`:171`) so completion can race session registration; capacity check (`:140`) is check-then-act against `_maxTotalSessions`.
- Problem: sync-over-async in a helper that is only valid for completed tasks but not enforced; capacity check race lets sessions exceed the cap; exceptions from cancelled/disposed CTS are hidden.
- Impact: sporadic over-admission, hard-to-diagnose hangs under load.
- Recommendation: make `CanceledGovernedError` take a completed-task assertion (`Debug.Assert(task.IsCompleted)`) or use `task.Exception`; use `SemaphoreSlim`/`Interlocked` reservation for the session cap; register the session before starting the work; replace bare catches with `ObjectDisposedException` filters.
- Effort: S

### ARC-C-09 (Medium, Resilience) Swallowing catches
- Evidence: bare `catch` (21 in slice): `Sql/Services/GovernedSqlExecutor.cs:273,404`, `Services/DbSessionContextInitializer.cs:62,68`, `Services/InMemoryRowFilterEvaluator.cs:146`, `Governance/CasbinEnforcementService.cs:1082`, `Security/SecureOutboundHttp.cs:196`, `Security/TenantRegistryGate.cs:62`, `Security/Totp/Services/ClusterTotpSecretStore.cs:40,66,98`, `Security/Totp/Services/TotpVerificationService.cs:116,132,174`, `Policy/RebacTableGate.cs:71-74` (returns `false` for any exception including `OperationCanceledException`, no log); 78 `catch (Exception`.
- Problem: the ReBAC gate turns a store outage into an indistinguishable "denied" (safe, but invisible and cancellation is misreported); other sites hide failures of audit/rollback/TOTP store.
- Impact: unobservable degraded security services; cancellations misclassified as policy decisions.
- Recommendation: add `when (ex is not OperationCanceledException)`, always log with a stable event id and a metric (`authz_dependency_failure_total`), keep fail-closed semantics but surface them. Ban empty catches via analyzer (CA1031 / custom rule) for `Security/`, `Policy/`, `Governance/`.
- Effort: S

### ARC-C-10 (Medium, Cohesion) `Services/` junk drawer and `Interfaces/` bucket
- Evidence: `Services/` (21 files, 6,118 LOC) mixes RLS generation (`RlsFilterGenerator.cs`, `AdvancedRlsFilterGenerator.cs`, `RowFilterSqlBuilder.cs`), masking (`ColumnMaskingProvider.cs`, `MaskingInputCanonicalizer.cs`), data-source executors (`SqlDataSourceExecutor*.cs`, `DeclarativeHttpDataSourceExecutor.cs`), HTTP handler (`ResponseSizeLimitingHandler.cs`), concurrency gate, identity resolution, consent resolution, gateway facade. `Interfaces/` (38 files) holds contracts for governance, ITSM, lineage, OpenJEV, token revocation, rate limiter, health checks (`Interfaces/IItsmWorkflowClient.cs`, `ILineageGraphStore.cs`, `IOpenJevClient.cs`, ...) while other features keep their own `Interfaces/` (Sql, Caching, Data, Federation).
- Problem: no discoverable ownership; feature slices leak across folders; coupling is invisible.
- Impact: slows onboarding; encourages cross-feature dependencies; blocks extraction (e.g. ITSM, lineage into Extensions).
- Recommendation: dissolve `Services/` into `Query/Table` (executors), `Policy/Rls`, `Policy/Masking`, `Identity`, `Http`; move feature interfaces next to implementations; keep `Interfaces/` only for true cross-cutting ports (clock, caller context, audit). Do it as mechanical namespace moves guarded by architecture tests.
- Effort: M

### ARC-C-11 (Medium, Maintainability) Forwarding and duplicated API surface
- Evidence: `Services/GatewayExecutionService.cs:595-606` re-exposes `EnsureInMemoryFilterIsEnforceable`, `FilterRows`, `IsTypeStrictRowFilter` forwarding to `InMemoryRowFilterEvaluator` (`Services/InMemoryRowFilterEvaluator.cs:40-60`); `GatewayExecutionService` is `partial` with two constructors (`:44,89`) and 3 `ExecuteTableQueryAsync` overloads (`:103,111,121`); `GovernedSqlExecutionService` twin constructors (`:51,80`) and `GovernedProcedureExecutionService` (`:74,95`).
- Problem: leftover facade methods after extraction; overload ladders keep legacy signatures alive.
- Impact: callers (tests) pin the old surface, preventing deletion; unclear canonical entry point.
- Recommendation: delete the forwarders, update callers to the evaluator, collapse overloads into a request record, keep a single constructor.
- Effort: S

### ARC-C-12 (Medium, Consistency) Two masking and two row-filter engines
- Evidence: `Services/ColumnMaskingProvider.cs` (563 LOC, in-memory), `Sql/Masking/SqlDataMaskingProvider.cs` (481 LOC, AST/SQL), `Sql/Masking/DecisionColumnMaskProvider.cs:13`, `Services/MaskingInputCanonicalizer.cs:14`; `Services/InMemoryRowFilterEvaluator.cs:48` (evaluates SQL text in memory, with `:320 ParseTuples` text parsing) versus `Services/RowFilterSqlBuilder.cs`/`RlsFilterGenerator.cs`.
- Problem: same policy semantics (mask kinds, null handling, canonicalisation) implemented twice (and RLS evaluated by parsing SQL text in memory), creating drift risk: a mask that works for SQL sources may leak differently on HTTP sources.
- Impact: enforcement inconsistency by data-source type; needs duplicated test matrices.
- Recommendation: define a `MaskSpec` + single semantic test corpus executed against both providers (golden tests) and have `ColumnMaskingProvider` delegate to `MaskSpecRenderer`'s spec; evaluate in-memory filters from the typed predicate tree (`PolicyPredicate`, see `Governance/CasbinEnforcementService.cs:1037`) instead of reparsing SQL text.
- Effort: M

### ARC-C-13 (Medium, Concurrency) In-memory cluster state semantics
- Evidence: `State/InMemoryClusterStateProvider.cs:60-80` (IncrementAsync via `AddOrUpdate` with a captured `result` variable written inside factory delegates - delegates may run more than once under contention, so `result` can be from a discarded attempt), `:90-120` (separate `_budgetLock` that does not guard the `_store` operations used by `IncrementAsync`, so budget consume and increment on same key are not mutually atomic); `PublishEventAsync` iterates handlers inline and awaits them (`:~130`).
- Problem: subtle races in the single-node fallback that the distributed implementation will not have, so single-node tests do not predict cluster behaviour; slow subscriber blocks publisher.
- Impact: DP-budget / rate counters may mis-account under concurrency in dev/single-node.
- Recommendation: implement increment with a CAS loop (`TryUpdate`) or take `_budgetLock` for all numeric ops; dispatch subscribers with isolation (try/catch per handler, `Task.WhenAll`). Add a shared contract test suite run against both providers.
- Effort: S

### ARC-C-14 (Medium, Performance) Synchronous audit on the hot path
- Evidence: `Services/GatewayExecutionService.cs:175,257,278,525,661` awaited `RecordAuditEventAsync` per request (including column-denied `:525` and every `CheckTableAccessAsync` `:657-661`); `Sql/Services/GovernedSqlExecutor.cs:191,213,393,534` same.
- Problem: each query waits on a hash-chained audit store write (`IAuditLogRepository.VerifyAuditHashChainAsync` implies a chain => serialised writes); `ALLOW` audit after materialising rows adds tail latency; no batching.
- Impact: audit store becomes throughput ceiling and availability dependency of reads.
- Recommendation: introduce an `IAuditSink` with a bounded channel + single chain writer (batch + group commit); keep a synchronous "durable-enqueue" for fail-closed semantics (append to WAL/outbox in the same call) and chain hashing asynchronously. Pair with ARC-C-03.
- Effort: M

### ARC-C-15 (Medium, Resilience) Fire-and-forget load in key-set cache
- Evidence: `VirtualFilters/Federation/VirtualFilterKeySetCache.cs:57` (`_ = Task.Run(..., CancellationToken.None)`), `:61` waiters use `WaitAsync(ct)`; `Dispose` only disposes `_cache` (`:64`).
- Problem: exception observation is handled well (`:99-103`), but loads are not tied to application shutdown or a concurrency cap: a burst of distinct keys spawns unbounded background loads that outlive the request and the host; `Dispose` does not cancel in-flight loads.
- Impact: orphaned DB/HTTP work after cancel/shutdown; thread-pool pressure; potential DoS via many distinct keys.
- Recommendation: bound by a `SemaphoreSlim`, link a per-cache `CancellationTokenSource` cancelled in `Dispose`, and keep the `Task` in `InFlightLoad` for awaiting during shutdown.
- Effort: S

### ARC-C-16 (Low, Maintainability) Interface explosion
- Evidence: `Interfaces/` has 38 files; many are single-implementation, integration-flavoured ports (`IItsmWebhookHandler`, `IOpenJevClient`, `IOpenLineageClient`, `ILineageImpactAnalyzerService`, `IGdprAuditReportExporter`, `ITrafficDrainController`, `ISocketTokenValidator`, `IClientIpResolver`) plus `Caching/Interfaces` with 3 files for 1 implementation (`Caching/Services/ClientTierResolver.cs`). (A repo-wide count was started but timed out; confirm with a targeted script.)
- Problem: interface-per-class without a second implementation or test seam need adds indirection and DI noise.
- Impact: navigation overhead; stale contracts.
- Recommendation: keep interfaces only where there is a second implementation (real or test fake across assembly boundary); otherwise register concrete classes. Move vendor-specific ports to `Autheris.Extensions`.
- Effort: M

### ARC-C-17 (Low, Testability) Test-support in production assembly
- Evidence: `Audit/NullAuditLogRepository.cs:12-16` ("for use in isolated unit tests") is referenced from 7 production classes (ARC-C-04); `Sql/NullCompiledSqlQueryPlanCache.cs`, `Policy/Services/NullPolicyEnforcementService.cs`; `#if DEBUG ... CompilerTestSeams.IsDisabled(SecurityControl.TenantRegistryFailClosed)` in `Security/TenantRegistryGate.cs:64-69`; `internal int DiagnosticDecisionCacheCount` `Governance/CasbinEnforcementService.cs:1877`.
- Problem: security-control kill switches and Null implementations compiled into the production assembly (DEBUG-only, but Release/Debug divergence is a build-config risk).
- Impact: Debug-built images could ship with disabled controls; production types depend on test doubles.
- Recommendation: move Null objects to a `Autheris.Testing` project; replace `CompilerTestSeams` with injectable `ISecurityControlSwitch` that is only registered by test hosts; fail startup if registered outside Development.
- Effort: S

### ARC-C-18 (Low, Cohesion) Inconsistent feature layout
- Evidence: `Sql/Policy/` vs top-level `Policy/`; `Sql/Interfaces/` vs `Interfaces/`; `Caching/` contains only `ClientTierResolver` while the real caches live in `Policy/Services/AccessProfileCache.cs`, `VirtualFilters/Federation/VirtualFilterKeySetCache.cs`, `Sql/CompiledSqlQueryPlanCache.cs`, `Governance/CasbinEnforcementService.cs` (decision cache); `Performance/` holds only IncrementalDelivery; `Common/` = 3 files.
- Problem: five independent caching mechanisms (MemoryCache in 5 types, ConcurrentDictionary decision cache, in-flight cache) with no shared invalidation abstraction; folder names do not predict content.
- Impact: invalidation is ad hoc (epoch for consents, snapshot epoch for Casbin, TTL for profile/key-sets); a policy change cannot be proven to invalidate all of them.
- Recommendation: create one `IPolicyCacheInvalidator` contract (subscribe to policy-epoch events) that all five caches register with; rename `Caching/` accordingly and relocate cache implementations under it or document ownership.
- Effort: M

### ARC-C-19 (Low, Consistency) Language and magic numbers in core
- Evidence: German comment in `Services/GatewayExecutionService.cs:524` ("Gesperrte (Deny) Spalte ...") contrary to the English-only directive; magic values `10_000` (`Governance/CasbinEnforcementService.cs:724`), `50_000`/`200_000`/`32 * 1024 * 1024`/`128 * 1024 * 1024`/`30`/`50000` defaults inline in `Sql/Services/FederatedDuckDbExecutionService.cs:~320-333` and `maxRows 1000/50` in `GatewayExecutionService.cs:~190`.
- Problem: defaults duplicated in code instead of options defaults; comment language violates repo standard.
- Impact: config drift, hard to document limits.
- Recommendation: move defaults to the options classes (single source), translate comments; add an analyzer/CI grep for non-ASCII-German markers if desired.
- Effort: S

## 4. Suggested sequencing

1. Quick wins (S): ARC-C-08, -09, -11, -13, -15, -17, -19.
2. Security-posture consolidation (M): ARC-C-03/-04/-14 (one audit abstraction + mandatory dependencies), ARC-C-05, ARC-C-07.
3. Structural (L): ARC-C-01/-02 (unified pipeline + ports), ARC-C-06/-10 splits, guarded by architecture tests (NetArchTest) forbidding `System.Data`/`HttpClient` in Application.
