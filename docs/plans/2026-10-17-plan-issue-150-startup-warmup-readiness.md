# PLAN-OPS-150: Startup Warm-Up Before `/health/ready` (Measurement First)

**Document ID:** `PLAN-OPS-150`
**Date:** 2026-10-17
**Status:** PLAN READY (Phase 2); next: Phase 3 security review
**PRD:** [`REQ-OPS-150`](2026-10-17-req-issue-150-startup-warmup-readiness.md) (issue #150, master plan rank 2.8)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Verified against:** `origin/main` `f0071be6`; open branches listed in section 6.
**Owner inputs:** intake OQ-8 (bounded fail-open after 30 s, background warm-up with `503 Warming`) and OQ-9 (ReadyToRun only if JIT is at least 25 % of the cold path) accepted; D-1..D-12 do not touch this item. No production yet: no compatibility shims, no migrations.

## 1. Verified code facts (deltas to the PRD)

| # | Fact | Evidence |
|---|---|---|
| V-1 | HotChocolate 16.6.8 has a native warm-up API: `IRequestExecutorWarmupTask` (`WarmupAsync`), `AddWarmupTask`, hosted `RequestExecutorWarmupService`, `RequestExecutorManager.WarmupExecutorAsync` (runs on every new executor, i.e. also after a schema rebuild), `MarkAsWarmupRequest`/`IsWarmupRequest` and `UseSkipWarmupExecution`. There is no `InitializeOnStartup` in 16.6.8. PRD fact 5 is resolved. | symbols in `/work/nuget/hotchocolate.aspnetcore/16.6.8`, `hotchocolate.types/16.6.8`, `hotchocolate.execution.pipeline/16.6.8` |
| V-2 | The custom pipeline does **not** contain `UseSkipWarmupExecution`; a warm-up request would reach `UseOperationExecution`. | `GatewayServiceCollectionExtensions.cs:551-596` |
| V-3 | `ReadOnlyOperationMiddleware`, `DbtHealthExecutionMiddleware`, `SchemaSunsettingExecutionMiddleware`, `CdnCacheTagMiddleware`, `SubgraphResultMaskingMiddleware`, `CostAndQuotaMiddleware` read `HttpContext`. `CostAndQuotaMiddleware` passes through without `HttpContext` (`:59-63`), so an in-process warm-up never touches `IRateLimiterService`. | `src/Autheris.GraphQL/Interceptors/*` |
| V-4 | Readiness order today: drain, then the cached report (5 s TTL). No other state. | `HealthEndpoints.cs:52-106`, `CachedGatewayHealthCheckService.cs:12` |
| V-5 | JWT bearer schemes `GatewayAuthSchemes.JwtBearer` and optional `JwtBearerAdfs` are registered in `GatewayServiceCollectionExtensions.cs:297-302`; OIDC metadata is fetched lazily by `JwtBearerOptions.ConfigurationManager`. | as cited |
| V-6 | OpenTelemetry tracing only adds the Autheris sources (no ASP.NET Core, HttpClient or HotChocolate sources); metrics use `GatewayDiagnostics.Meter` (`Autheris.Metrics`). | `GatewayCoreServiceExtensions.cs:124-135`, `GatewayDiagnostics.cs:11-14` |
| V-7 | Option convention is `Gateway:<Area>` with `BindConfiguration` + `IValidateOptions` + `ValidateOnStart` (example `RevocationWatchOptions`). The PRD section `Startup:Warmup` becomes `Gateway:Startup:Warmup`. | `GatewayServiceCollectionExtensions.cs:543-547` |

## 2. Decisions

1. **D-150-1 Measurement gates everything (WP-A).** No warm-up step ships without a WP-A number that justifies it, except the GraphQL executor step (needed for the readiness hard gate, D-150-4).
2. **D-150-2 Readiness order.** `/health/ready`: drain (503, no body) -> cached component report (503 `Unhealthy`, unchanged) -> live warm-up state (503 `Warming` or `Unhealthy`) -> 200 `Ready`. Security components stay ahead of the warm-up state (PRD S-5) and the 5 s cache never delays the flip because the warm-up state is read live (PRD R-9).
3. **D-150-3 Bounded fail-open only for best-effort steps.** A best-effort step that throws or exceeds its slice of `TimeoutSeconds` is logged and counted and no longer blocks readiness (OQ-8). After the global timeout the gate opens regardless of best-effort steps.
4. **D-150-4 Fail-closed for the GraphQL executor.** The executor step is `Required`: if the schema build fails (the #157 class of `SchemaException`), readiness stays 503 `Unhealthy` until a later executor build succeeds; the global timeout does not open it. Production body: status and timestamp only. Owner question Q-150-A.
5. **D-150-5 HotChocolate-native warm-up.** `GraphQlExecutorWarmupTask : IRequestExecutorWarmupTask` registered with `AddWarmupTask`; it runs `{ __typename }` with `MarkAsWarmupRequest()` through the real pipeline, and `.UseSkipWarmupExecution()` is inserted directly before `.UseOperationExecution()`. Parse, validation, the custom middlewares and operation compile are warmed; no resolver runs. Errors returned by `HttpContext`-dependent middlewares are accepted (outcome `Completed`); only an exception or a missing executor is a failure. Because `WarmupExecutorAsync` runs per executor, the catalog refresh rebuild (`CatalogSchemaRefreshSeconds`) is kept warm by the same task (PRD R-12), and the task reports success to `IStartupWarmupState`, which also clears a D-150-4 failure.
6. **D-150-6 No HTTP-triggered warm-up flag.** The warm-up marker is set only through `OperationRequestBuilder` in process. A test proves an HTTP GraphQL request (headers, extensions, `ContextData`-shaped variables) never arrives with `IsWarmupRequest() == true`.
7. **D-150-7 Framework shape.** `IStartupWarmup { string Name; WarmupKind Kind; Task RunAsync(CancellationToken) }`, `StartupWarmupService : BackgroundService` (runs steps in order, per-step `CancellationTokenSource` with the remaining budget, catches per step, logs step name and exception type only), `IStartupWarmupState` singleton (`Pending|Warming|Ready|Failed`, lock-free `Volatile` reads). All new code in `src/Autheris.Api/Hosting/Warmup/` plus one registration file `Extensions/DependencyInjection/GatewayStartupWarmupServiceExtensions.cs`, called once from `GatewayServiceCollectionExtensions` next to `AddAutherisMcp` (line 219). No edit to `Program.cs` or `GatewayDiagnostics.cs`.
8. **D-150-8 Options** `StartupWarmupOptions` (`Gateway:Startup:Warmup`): `Enabled=true`, `TimeoutSeconds=30` (1-300), `RequireForReadiness=true`, `Steps:<Name>:Enabled=true`. Validator rejects out-of-range values and unknown step names at start. `Enabled=false` registers no service and the state reports `Ready` immediately.
9. **D-150-9 Metrics** in a new `StartupWarmupDiagnostics` on `GatewayDiagnostics.Meter`: histogram `autheris.startup.warmup.duration` (s), histogram `autheris.startup.warmup.step.duration` with tags `step`, `outcome` (`completed|failed|timed_out|skipped`); names follow BL-06 (`autheris.` prefix, seconds). One structured end log line. No request, query, rate-limit or audit counter is touched.
10. **D-150-10 OIDC prefetch step (conditional on WP-A).** `OidcMetadataWarmup` calls `IOptionsMonitor<JwtBearerOptions>.Get(scheme).ConfigurationManager.GetConfigurationAsync` for the configured schemes only; best-effort; failures are not cached as success (the manager refetches on its own schedule); no key material logged.
11. **D-150-11 MCP step (conditional on WP-A).** `McpSerializationWarmup` serialises a `ListToolsResult` built from `IMcpToolRegistry` with the SDK's JSON options; no handler, no scope with a principal.

## 3. Work packages

### WP-A Cold-path measurement (S)
Harness `benchmarks/load/scripts/cold-start.sh` (manual, not in the PR gate, PRD Q-150-5): restart container, poll `/health/ready` with timestamps, then first and 20 warm requests for GraphQL query, MCP `describe_dataset`, MCP `describe_my_access` (`curl -w` timings), 3 restarts; optional `dotnet-trace` with `Microsoft-Windows-DotNETRuntime` (JIT, loader), `System.Net.Http` (OIDC fetch), `Microsoft-Extensions-DependencyInjection`, `Microsoft-AspNetCore-Hosting`, and `dotnet-counters` `System.Runtime` (`time-in-jit`, `methods-jitted-count`). Opt-in `Gateway:Startup:ColdPathTiming:Enabled` (default false) registers an HotChocolate execution diagnostic listener logging per-stage ms (parse, validate, compile, execute) for the first 3 operations only, no query text. Two auth modes: test auth (`AutherisIncludeTestAuth=true`) and a local static OIDC metadata stub, to isolate the IdP share.
**Decision rule (recorded as a result table appended to this plan):** a stage becomes a WP-C target if its cold-minus-warm delta is >= 100 ms or >= 10 % of the total cold delta; JIT >= 25 % of the cold delta opens WP-D (OQ-9); if AC-1 already holds after WP-B, WP-C is reduced to the executor step.
Tests: `ColdPathTimingOptions_DefaultDisabled_RegistersNoListener`, `ColdPathTimingListener_LogsStageNamesAndDurations_NoDocumentText`, `ColdPathTimingListener_StopsAfterThreeOperations`, `HostStartup_UserHostedServicesStartBeforeKestrelListens` (PRD fact 13), `AddGraphQLServer_RegistersHotChocolateWarmupHostedService_Fact` (pins V-1 behaviour), `CatalogSchemaRebuild_RunsWarmupTasksBeforeNewExecutorServes` (R-12).

### WP-B Warm-up framework and readiness gate (S-M)
D-150-2, -3, -7, -8, -9. Files: `Hosting/Warmup/*`, `GatewayStartupWarmupServiceExtensions.cs`, `HealthEndpoints.cs`, options + validator.
Tests: `Ready_WhileWarming_Returns503Warming_ProductionBodyHasStatusAndTimestampOnly`, `Ready_AfterWarmup_Returns200`, `Ready_StepNeverCompletes_Returns200AfterTimeout_LogsWarning` (fake `TimeProvider`), `Ready_ThrowingBestEffortStep_OtherStepsRun_HostStarts`, `Ready_Draining_Returns503RegardlessOfWarmup`, `Ready_UnhealthyComponent_WinsOverWarming`, `Ready_WarmupDone_NotDelayedByHealthCache`, `Live_IgnoresWarmupState`, `WarmupDisabled_ReadyImmediately_NoHostedService`, `WarmupOptions_TimeoutOutOfRange_RejectsStart` (0, 301), `WarmupOptions_UnknownStepName_RejectsStart`, `WarmupLog_ContainsStepNameAndExceptionTypeOnly`, `WarmupMetrics_RecordDurationAndOutcome_NoRequestCounters`, `WarmupRegistrations_DependOnNoAuditConsentConnectorOrGovernanceWrite` (architecture test over the `IStartupWarmup` closure: no `IAuditService`, `IConsentResolutionService`, connector, repository write interface, `IHttpClientFactory` except via `JwtBearerOptions`), `RouteTable_Unchanged_NoWarmupEndpoint` (AC-5).

### WP-C Warm-up steps (S-M, scope from WP-A)
D-150-4, -5, -6 always; D-150-10, -11 only if WP-A selects them; DI pre-resolution of hot singletons only if WP-A shows a DI share.
Tests: `GraphQlWarmupTask_RunsTypenameDocument_NoResolverInvoked`, `GraphQlWarmupTask_SkipsOperationExecution`, `GraphQlWarmupTask_DoesNotCallRateLimiter`, `GraphQlWarmupTask_SchemaBuildFails_ReadinessStays503Unhealthy_PastTimeout`, `GraphQlWarmupTask_RebuildSucceeds_ClearsFailure`, `HttpGraphQlRequest_CannotBeMarkedAsWarmup`, `OidcWarmup_ConfiguredSchemesOnly`, `OidcWarmup_UnreachableAuthority_BoundedByTimeout_TokenStillValidatedByHandler` (AC-8), `OidcWarmup_FailureNotCachedAsSuccess`, `McpWarmup_SerialisesToolList_NoHandlerExecuted`, integration `Startup_AuditChainLengthUnchanged_NoDataSourceQuery_NoConsentResolution` (AC-4, recording fakes).

### WP-D ReadyToRun (S, conditional on WP-A, OQ-9)
`PublishReadyToRun=true` with `-r linux-x64` in the release publish step (`.github/workflows/docker-publish.yml:59-65`, `Autheris.Api.csproj`); report image size and cold delta. Tests: SC-04 publish-content architecture test green; tiering/PGO settings unchanged (`ApiCsproj_KeepsTieredPgo`). Note: the CI workflow file needs a token with `workflow` scope (tracker section 3.5).

**Total: M** (A: S, B: S-M, C: S-M, D: S conditional). Order A -> B -> C -> (D). One PR per WP; A and B may share a PR if WP-A adds only the harness and listener.

## 4. Fail-closed and leak summary

Required executor failure blocks readiness (D-150-4); best-effort failures open after the bound (OQ-8). Health bodies outside Development: `Ready|Warming|Unhealthy` plus timestamp; step names, timings and exception types only in logs and (Development) the readiness body. No identity, no HTTP self-call, no data source, consent, audit or governance write (WP-B architecture test, WP-C integration test).

## 5. Owner questions

- **Q-150-A** Should a failed GraphQL schema build keep the node unready (fail-closed, D-150-4) instead of opening after 30 s like other steps? With every node failing this means no node is ready, although REST and SQL channels would still work. **Recommended default: yes, keep unready** (a gateway without its GraphQL schema is broken; after #157 this indicates a real defect).
- PRD Q-150-1..5: covered by OQ-8/OQ-9 or by the PRD default (Q-150-4 OIDC prefetch yes, configured authorities only; Q-150-5 manual harness) - default taken.

## 6. Sequencing and parallelisation

**(a) Shared files.**
- `src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.cs` (WP-B one registration line near 219; WP-C `.UseSkipWarmupExecution()` and `.AddWarmupTask` in the GraphQL builder, 551-596): also changed by open `origin/fix/issue-157-columnless-table` (one line, `ICatalogSchemaDiagnostics` near 538), `origin/fix/issue-158-inc0-explain-access`, `origin/feat/issue-129-caller-attribute-row-filters` (I-25, chunk 1), `origin/release/chunk1-consolidated` and stale `origin/feat/issue-99-i1-c` (D-11, wave 4). Different regions; textual merge only.
- `src/Autheris.GraphQL/Catalog/CatalogGraphQlTypeModule.cs`: read-only for this plan (R-12 test only); changed by #157, #158, I-25 and later I-8/I-19.
- `src/Autheris.Api/Endpoints/HealthEndpoints.cs`: no open branch, no tracker step.
- MCP: lane B (I-7 #111, #138 INC-1) changes the MCP catalog tools and visibility service; WP-C reads only `IMcpToolRegistry` and registers in its own file, no shared file with lane B.
- `Program.cs`, `GatewayDiagnostics.cs`, metrics composition: BL-06 I-27..I-30 (chunk 6). Avoided by D-150-7/-9; I-27 must keep the `autheris.startup.warmup.*` instruments when it re-homes meters.
- WP-D only: `Autheris.Api.csproj`, `.github/workflows/docker-publish.yml` (no open branch; workflow scope token needed).

**(b) Hard dependencies.** None for WP-A/WP-B. WP-C after WP-A results and WP-B. WP-C test `GraphQlWarmupTask_SchemaBuildFails_*` should rebase on #157 (1.9) once merged so a column-less catalog no longer trips the required step. WP-D after WP-A.

**(c) Recommended position.** Not part of chunks 1-6; run as an independent wave-2 lane item (the docs/plan lane or any free implementation lane) in parallel with lanes A (chunk 1 row filters), B (I-7/#138 INC-1) and C (bugfix/procedure branches #157, #158, procedure INC-1). Start WP-A now (no shared production file); merge WP-B/WP-C after #157 lands and before BL-06 I-27 starts, so I-27 adopts the warm-up instruments instead of the reverse.

---

## 7. Phase 3 security review

**Reviewer:** `csharp-security-expert`, 2026-10-17. **Verified against** `origin/main` (`HealthEndpoints.cs:52-106`, `GatewayServiceCollectionExtensions.cs:297-302, 551-596`, `Interceptors/*Middleware.cs`, `.github/workflows/docker-publish.yml:40-70`). Facts V-2..V-5 confirmed. Additional observed facts: `TrustedDocumentsOnlyMiddleware` and `GraphQlEnumerationShieldMiddleware` sit before the custom middlewares and will see the warm-up request; `DbtHealth*`, `SchemaSunsetting*`, `CdnCacheTag*`, `ReadOnlyOperation*` resolve `HttpContext` from `ContextData` **or** the ambient `IHttpContextAccessor`.

**Verdict: APPROVED WITH CONDITIONS.** The ordering (drain -> cached report incl. security components -> live warm-up state) and the HotChocolate-native, in-process, `__typename`-only approach are sound. Four design holes need binding fixes (01, 02, 03, 05); none requires an architect rewrite.

**Q-150-A: yes, keep unready on a failed initial GraphQL executor build, with the refinements in SEC-OPS150-02/03.** Fail-open to a gateway with no GraphQL schema would hide a real defect (the #157 class) from the orchestrator; restart/unready is the correct signal. No operator flag may open this gate.

### Binding conditions

- **SEC-OPS150-01 (Required gate is positive proof, independent of switches).** Readiness 200 requires positive evidence that the executor was built and warmed. `Enabled=false`, `RequireForReadiness=false`, the global timeout and a missing/uncalled step must never mark the executor step satisfied. D-150-8 "Enabled=false reports Ready immediately" applies to best-effort steps only; the executor gate stays evaluated. Unknown/uninitialised state resolves to 503.
- **SEC-OPS150-02 (build failure must set the failure state itself).** If `AddWarmupTask` is only invoked after a successful build, a `SchemaException` never reaches the task and the state would remain `Warming`, then the 30 s timeout would open the gate (violates D-150-4). The failure must be detected by something that observes the build (e.g. an `IRequestExecutorEvents`/executor-resolution probe in `StartupWarmupService`, or the hosted warm-up service's failure surfaced), not by the task alone. A build failure sets `Failed`; only a later successful build+warm-up clears it. Timeout transitions best-effort steps only.
- **SEC-OPS150-03 (rebuild after Ready does not flip readiness).** `Failed` from the Required step applies to the initial build. After the node was `Ready`, a failed catalog-refresh rebuild keeps the previous executor serving; it is logged and counted (`outcome=failed`) but does not return the node to 503 (no self-inflicted outage). It must never be reported as success.
- **SEC-OPS150-04 (order is fixed and total).** `/health/ready`: drain first (503, no body, even while warming), then the cached report (security components win over `Warming`), then the live state. The warm-up state is read live (no cache). `/health/live` is untouched. The state must not be set to `Ready` while draining begins; drain wins at any time.
- **SEC-OPS150-05 (warm-up carries no ambient identity or HttpContext).** The warm-up request is built with an explicit empty `ContextData` (no `HttpContext`, no `ClaimsPrincipal`) and must not see an ambient `IHttpContextAccessor.HttpContext`. The catalog-refresh rebuild path may run on a request thread (AsyncLocal flows): the task must run with `ExecutionContext.SuppressFlow` or an accessor guard so a real caller's principal/response headers (`Sunset`, cache tags) are never read or written. Missing principal must stay fail-closed in all custom middlewares; an exception from them is a warm-up failure only for the executor step if it indicates a build error, not for a null `HttpContext` (verified the four middlewares null-guard; assert it).
- **SEC-OPS150-06 (marker not spoofable, skip is marker-only).** `IsWarmupRequest` is set solely via `OperationRequestBuilder` in process. No HTTP header, extension, variable, persisted-operation id or batch item may set it. `UseSkipWarmupExecution` must skip only when the marker is set and sit directly before `UseOperationExecution`; no middleware before it may be reordered or removed. The warm-up document is a compiled-in constant `{ __typename }`, not configurable.
- **SEC-OPS150-07 (no side effects).** No resolver, audit write, consent resolution, data-source/connector call, governance write, rate-limit/quota call, or request counter. Under `PersistedQueriesOnly` the warm-up must not add its document to `TrustedDocumentStore` nor weaken `TrustedDocumentsOnlyMiddleware` (an error result there is accepted outcome `Completed`).
- **SEC-OPS150-08 (hung step DoS).** `StartupWarmupService.ExecuteAsync` must yield (`await Task.Yield()`) before any work so host start is not blocked. Each step runs under a per-step CTS (remaining budget) **and** is awaited with a hard bound (`WaitAsync(timeout)`) so a non-cooperative step cannot hold the service; the service honours host `stoppingToken`. Steps run sequentially, never unbounded in parallel, and at most one retry-free attempt. Total budget never exceeds `TimeoutSeconds` (max 300) plus the executor step only while the executor is genuinely still building.
- **SEC-OPS150-09 (health body and log leakage).** Outside Development the `Warming` and `Unhealthy` bodies contain only `status` and `timestamp`; no step names, timings, exception types/messages, security mode, bypasses or components. Logs: step name + exception type only (no message, stack, URL query, token, key material, query text). Metric tags limited to the allow-listed step names (validated at start) and the four fixed outcomes.
- **SEC-OPS150-10 (OIDC prefetch).** Reads only `IOptionsMonitor<JwtBearerOptions>.Get(scheme)` for configured schemes; no request-derived input, no new HTTP client, no change to `ValidateIssuer/Audience/Lifetime/IssuerSigningKey`, no static keys, no `RequireHttpsMetadata` relaxation. Honours the per-step token. A failed prefetch must not delay or replace the handler's own first fetch: verify against the pinned Microsoft.IdentityModel version that a failed prefetch does not lock out `ConfigurationManager` refresh for `RefreshInterval` (else skip the step or fall back to handler-driven fetch). Never call `RequestRefresh` from warm-up. Tokens stay fully validated per request regardless of prefetch outcome.
- **SEC-OPS150-11 (MCP step).** No principal, scope, handler or tool invocation; serialisation of the registry only. Output discarded, never logged.
- **SEC-OPS150-12 (options hardening).** Validator rejects out-of-range and unknown step names at start (fail start). The cold-path timing listener (WP-A) defaults to off, logs stage names and durations only (no document text, variables, user, tenant), stops after 3 operations, and the validator rejects it enabled when not Development.
- **SEC-OPS150-13 (supply chain, WP-D).** ReadyToRun only with a RID in the csproj so `packages.lock.json` includes the runtime/crossgen packs and the existing `restore --locked-mode`, vulnerable-package audit and pinned action SHAs stay in force; no new action, no unpinned download, no new secret or permission in the workflow. `AutherisIncludeTestAuth=false` must remain on publish and the SC-04 `ReleaseBuildExcludesTestAuthTests` must run against the R2R output (IL remains reflectable) and stay green. Not self-contained; base image digest unchanged. Workflow change goes via PR review, not a direct token push.
- **SEC-OPS150-14 (harness).** `cold-start.sh` and the OIDC metadata stub are manual, bind localhost only, carry no credentials, use `AutherisIncludeTestAuth=true` only in the local run, and are excluded from publish output and CI.

### Mandatory security tests (`[Trait("Category","Security")]`)

- **T-OPS150-01** `Ready_WarmupDisabledOrNotRequired_ExecutorGateStillEnforced` (01)
- **T-OPS150-02** `Ready_InitialSchemaBuildThrows_StaysUnready_PastGlobalTimeout` (02, real build failure, not a faked task)
- **T-OPS150-03** `Ready_LaterSuccessfulBuild_ClearsInitialFailure` (02)
- **T-OPS150-04** `Ready_RebuildFailsAfterReady_StaysReady_FailureCountedNotSuccess` (03)
- **T-OPS150-05** `Ready_DrainWinsOverEveryWarmupState` and `Ready_UnhealthySecurityComponent_WinsOverWarmingAndReady` (04)
- **T-OPS150-06** `Ready_UninitialisedWarmupState_Returns503` (01)
- **T-OPS150-07** `WarmupRequest_BuiltWithEmptyContextData_NoPrincipalNoHttpContext` and `Warmup_OnRequestThreadWithAmbientHttpContext_DoesNotReadOrMutateCallerResponse` (05)
- **T-OPS150-08** `HttpGraphQlRequest_HeaderExtensionVariableOrBatchItem_NeverIsWarmupRequest` (06; drives the real HTTP pipeline)
- **T-OPS150-09** `UseSkipWarmupExecution_SkipsOnlyMarkedRequests_OrderedBeforeOperationExecution` (06)
- **T-OPS150-10** `Warmup_PersistedQueriesOnly_DoesNotMutateTrustedDocumentStore` (07)
- **T-OPS150-11** `Warmup_NonCooperativeStep_ReleasedByHardBound_HostStartsAndStops` and `StartupWarmupService_YieldsBeforeFirstStep` (08)
- **T-OPS150-12** `ReadyBody_Production_WarmingAndUnhealthy_OnlyStatusAndTimestamp` and `WarmupLog_NoExceptionMessageNoQueryTextNoKeyMaterial` (09)
- **T-OPS150-13** `OidcWarmup_FailedFetch_DoesNotDelayHandlerFirstFetch_ValidationOptionsUnchanged` (10)
- **T-OPS150-14** `McpWarmup_NoPrincipalNoToolHandler` (11)
- **T-OPS150-15** `ColdPathTimingOptions_EnabledOutsideDevelopment_RejectsStart` (12)
- **T-OPS150-16** `DockerPublish_ReadyToRun_KeepsLockedRestoreAuditPinsAndTestAuthExclusion` (13, workflow/csproj architecture test)

Existing plan tests (WP-B/WP-C) remain required; T-OPS150-01..16 are additional and gate merge of WP-B (01, 05, 06, 11, 12), WP-C (02-04, 07-10, 13, 14) and WP-D (16). Phase 4 must not start WP-C before SEC-OPS150-02 has a design note in this section's PR description.

## 8. Architect addendum (post Phase 3)

Resolves the WP-C blocker SEC-OPS150-02 and states how SEC-OPS150-01, -03 and -05 are realised. Verified in the HotChocolate 16.6.8 assemblies (`/work/nuget`): `IRequestExecutorProvider.GetExecutorAsync`, `IRequestExecutorEvents : IObservable<RequestExecutorEvent>` with `RequestExecutorEventType.ExecutorCreated` and `ExecutorEvicted`, `RequestExecutorManager.WarmupExecutorAsync`, `BuildRequestExecutorAsync` with per-schema `_semaphoreBySchema` (single-flight). There is **no build-failed event**: a failed build only surfaces as an exception from `GetExecutorAsync`. `McpSchemaDiscoveryService.StartAsync` also calls `GetExecutorAsync` but swallows the exception (`McpSchemaDiscoveryService.cs:98-101`), so it cannot serve as the signal.

1. **A-150-1 Executor gate (SEC-OPS150-02).** New `GraphQlExecutorGate` in `Hosting/Warmup/`, separate from `IStartupWarmup` steps and the best-effort budget. Its loop runs in `StartupWarmupService` before the best-effort steps and calls `IRequestExecutorProvider.GetExecutorAsync(ct)` itself. If that call throws, the gate state becomes `Failed` (log: exception type only; metric `outcome=failed`). Back-off retries follow (5 s, doubling, capped at 60 s) until a build succeeds, because HotChocolate retries the build on the next `GetExecutorAsync`. The global `TimeoutSeconds` never stops this loop. Single-flight means the probe and `McpSchemaDiscoveryService` share one build, so no double build happens.
2. **A-150-2 Positive proof (SEC-OPS150-01).** `GraphQlExecutorWarmupTask` (D-150-5) records `ReferenceEquals`-identifiable proof that `{ __typename }` completed for that executor instance (`WarmedExecutor`). The gate turns `Ready` only when `GetExecutorAsync` returned executor X **and** `WarmedExecutor == X`. If the task has not run for X, the gate runs it once itself through X with the same request, which covers `WarmupExecutorAsync` timing. The initial state is `Pending` and maps to 503, as do `Failed` and any unknown value. If WP-A shows that `WarmupExecutorAsync` runs after `GetExecutorAsync` returns, the gate-run path is the primary path. Test: `CatalogSchemaRebuild_RunsWarmupTasksBeforeNewExecutorServes` decides which path is primary, and both are safe.
3. **A-150-3 Reconciling D-150-8.** `AddGraphQlExecutorGate()` is registered unconditionally, outside `StartupWarmupOptions`. `Enabled=false` disables the best-effort steps only. `RequireForReadiness=false` makes best-effort steps non-blocking only. `TimeoutSeconds` and the per-step flags apply to best-effort steps only, and the validator rejects `Steps:GraphQlExecutor`. D-150-8 "readiness immediate" is amended: readiness comes after the executor gate, which today's startup reaches anyway because the schema is built at start (PRD fact 3). Readiness order (D-150-2): drain, then cached report, then executor gate, then best-effort state.
4. **A-150-4 Rebuilds after Ready (SEC-OPS150-03).** Once the gate is `Ready` it latches. The gate subscribes to `IRequestExecutorEvents`. On `ExecutorEvicted` it probes once (`GetExecutorAsync` with flow suppressed, see A-150-5) so the rebuild happens eagerly instead of on a user request. On success it runs the warm-up path. On failure it logs and records `outcome=failed` (never `completed`) and the gate stays `Ready`. Which executor serves after a failed rebuild is HotChocolate behaviour and is pinned by test T-OPS150-04. The latch flag lives in `GraphQlExecutorGate` and is never reset.
5. **A-150-5 No ambient identity (SEC-OPS150-05).** All gate and task work runs inside `using (ExecutionContext.SuppressFlow()) { Task.Run(...) }`, including the `ExecutorEvicted` callback (the event may fire on a request thread during a catalog refresh). The request is built with `OperationRequestBuilder.New().SetDocument("{ __typename }").MarkAsWarmupRequest()` and has no global state, no `HttpContext` and no `ClaimsPrincipal`. Defence in depth: the task asserts `IHttpContextAccessor.HttpContext is null` before executing and skips with `outcome=failed` otherwise. Tests: `WarmupTask_EvictedOnRequestThread_DoesNotSeeCallerHttpContext` (sets an accessor context, then triggers eviction, then asserts the middleware fakes saw no context and no `Sunset`/cache-tag header was written) and `WarmupTask_AmbientHttpContextPresent_SkipsAndCountsFailed`.
6. **A-150-6 Q-150-A recorded.** Owner default (yes): a failed **initial** GraphQL executor build keeps the node unready. No option, timeout or environment flag can open the gate. Rebuild failures after Ready never flip readiness (A-150-4).
7. **A-150-7 Additional tests (WP-C, beside T-OPS150-01..04).** `ExecutorGate_ProbeThrows_StateFailed_RetriesWithBackoff`, `ExecutorGate_ExecutorReturnedButNotWarmed_StaysPending`, `ExecutorGate_RegisteredWhenWarmupDisabled`, `WarmupOptions_StepsGraphQlExecutor_RejectsStart`, `ExecutorGate_ReadyLatched_EvictionFailureDoesNotUnready`. A real build failure is produced with a column-less catalog fixture before #157 merges, and with a deliberately broken `ITypeModule` fake after it.

### Phase 3 re-check of addendum (section 8)

**Final verdict: APPROVED WITH CONDITIONS (conditions 01..14 plus 15..16 below).** A-150-1..7 realise SEC-OPS150-01 (positive proof, unconditional gate, `Pending`/unknown -> 503), -02 (probe observes the real build exception, no timeout opening), -03 (latch) and -05 (SuppressFlow, null-accessor assertion) correctly; the architect decision "no build-failed event, probe via `GetExecutorAsync`" is accepted. A-150-3 supersedes the D-150-8 "Enabled=false reports Ready immediately" sentence. Q-150-A stands (A-150-6).

- **SEC-OPS150-15 (probe coalescing).** The `ExecutorEvicted` handler and the back-off loop share one single-flight probe: at most one probe in flight, evictions arriving meanwhile are coalesced into one follow-up, back-off cap 60 s also applies after eviction failures. An eviction storm must not cause repeated builds or unbounded tasks. The handler never throws into the HotChocolate observer and the `IDisposable` subscription is released on `StopAsync`.
- **SEC-OPS150-16 (gate-run path stays marker-only).** The gate's own warm-up run uses the same in-process builder (`MarkAsWarmupRequest`, constant document, empty ContextData) and never executes through a scope with a principal; the "ambient HttpContext present -> skip, count failed" path must leave the gate `Pending` (503), never `Ready`. A `GetExecutorAsync` that never returns leaves 503 (fail-closed, no timeout opens it); this is accepted and must be logged once after `TimeoutSeconds`.
- **T-OPS150-17** `ExecutorGate_EvictionStorm_CoalescesToSingleProbe_HandlerNeverThrows` (15)
- **T-OPS150-18** `ExecutorGate_AmbientHttpContextPresent_StaysPending_NeverReady` (16)
- **T-OPS150-19** `ExecutorGate_GetExecutorNeverReturns_Stays503_LogsOnceAfterTimeout` (16)
- The A-150-7 tests and `WarmupTask_EvictedOnRequestThread_DoesNotSeeCallerHttpContext` are promoted to `[Trait("Category","Security")]` and map to T-OPS150-01..04, 07. WP-C merge gate now includes T-OPS150-17..19.

## 9. WP-A measurement results (2026-10-18)

**Method.** `benchmarks/load/scripts/cold-start.sh`: Release build of `Autheris.Api` (`AutherisIncludeTestAuth=true`), started as a separate process on `127.0.0.1` (Development, test auth, anonymous dev access, SQLite governance db with the seeded demo catalog of 101 tables, `Gateway:Startup:ColdPathTiming:Enabled=true`). Per restart: poll `/health/ready` every 50 ms, wait 5 s, then per probe one first request and 20 warm requests (`curl -w time_total`); the probe order rotates per restart (restart 1: GraphQL, `describe_dataset`, `describe_my_access`; restart 2 and 3 rotated) so the position effect is visible. 3 restarts, median reported. `dotnet-counters collect` (`System.Runtime` `time-in-jit`, `methods-jitted-count`, 1 s interval) ran per restart. GraphQL probe: `{ billing_dbo_billing_table_1(first: 1) { __typename } }` (full pipeline up to the resolver, which returns `ACCESS_DENIED` for the anonymous guest; no connector or data source call). MCP probes: `tools/call` over streamable HTTP, stateless.

**Environment caveats.** WSL2 dev container, shared with other agent lanes running builds and tests at the same time, so absolute numbers are noisy (restart spread up to 3x). No IdP: test auth only, so **the OIDC metadata fetch was not exercised and has no number**. No Testcontainers database: SQLite, no real data source. The in-process listener reports `validate`, `compile`, `execute`; HotChocolate 16.6.8 emits no `ParseDocument` event on this path (parse happens in the HTTP layer), so there is no `parse` line. JIT counters are process-wide and include background Tier-1 re-JIT, so the JIT share below is an upper-bound estimate.

| Metric | Restart 1 | Restart 2 | Restart 3 | Median |
|---|---|---|---|---|
| Process start to `/health/ready` 200 (ms) | 1952 | 6132 | 2199 | **2199** |
| GraphQL first request (ms) / probe position | 794 / 1 | 330 / 3 | 844 / 2 | **794** |
| GraphQL warm median (ms) | 19.1 | 11.9 | 19.1 | **19.1** |
| `describe_dataset` first (ms) / position | 872 / 2 | 492 / 1 | 157 / 3 | **492** |
| `describe_dataset` warm median (ms) | 40.5 | 23.6 | 45.5 | **40.5** |
| `describe_my_access` first (ms) / position | 57 / 3 | 48 / 2 | 451 / 1 | **57** |
| `describe_my_access` warm median (ms) | 20.2 | 12.2 | 13.9 | **13.9** |
| First GraphQL operation: validate / compile / execute (ms) | 74 / 36 / 284 | 43 / 24 / 123 | 205 / 51 / 215 | **74 / 36 / 215** |
| Warm stage cost (listener occurrences 2-3, ms) | validate 0.5, execute 13-30 | validate 0.4, execute 8-14 | validate 0.3, execute 9-41 | validate under 1, execute about 10-30 |
| `methods-jitted-count` (idle, then after the 3 probes) | 13.8k to 28.6k | 15.6k to 30.0k | 14.9k to 28.7k | about 14k to 29k |
| JIT ms in the second covering the first requests | 696 | 400 to 520 | 560 | **about 560** |

Cold minus warm (medians): GraphQL **775 ms**, `describe_dataset` **452 ms**, `describe_my_access` **43 ms** (but 450 ms when it is the first MCP call of the process: the cost sits in the shared MCP stack, not in the tool). Sum over the three probes: about 1270 ms.

**Decision rule applied** (stage is a WP-C target if cold-minus-warm is at least 100 ms or at least 10 % of the total cold delta, about 127 ms):

| Stage or area | Cold minus warm | Result |
|---|---|---|
| GraphQL validate | about 74 ms (about 10 % of the GraphQL delta, 6 % of the total) | Below the thresholds on its own; covered anyway by the executor step (`{ __typename }` runs the validator). |
| GraphQL compile | about 36 ms | Below; covered by the executor step. |
| GraphQL execute (resolver stack, middlewares, error formatting) | about 200 ms | **Target.** Not covered by `UseSkipWarmupExecution` (see gap G-150-A). |
| MCP shared stack (first `tools/call`: tool registry, JSON serialisation, transport) | about 400 ms for the first MCP call, about 40 ms for later tools | **Target: D-150-11 selected** (`McpSerializationWarmup`). Per-tool step not needed. |
| OIDC metadata prefetch (D-150-10) | not measured (no IdP in this setup) | **Dropped for now**: the plan makes measurement the gate (D-150-1). Re-measure with a local metadata stub before WP-C; select only if the first authenticated request shows at least 100 ms. |
| DI pre-resolution of hot singletons | no separate stage; any DI cost is inside the execute and MCP deltas | **Dropped**: the executor step and the MCP step already resolve the same services. |
| JIT | about 560 to 700 ms JIT in the second of the first requests against a 794 ms first GraphQL request; 13k to 15k methods are jitted by the three probes | **At least 25 % of the cold delta by a wide margin (estimated 50 to 90 %): WP-D (ReadyToRun) opens.** The estimate is an upper bound (see caveats); WP-D must report the measured cold delta with R2R on the same harness. |

**WP-C step selection:** GraphQL executor step (required, D-150-4/-5) plus D-150-11 (MCP serialisation). D-150-10 (OIDC) and DI pre-resolution are dropped on this evidence. **WP-D: go.**

**Findings for the plan.**
- **A-150-2 resolved (test `CatalogSchemaRebuild_RunsWarmupTasksBeforeNewExecutorServes`).** With HotChocolate 16.6.8, `WarmupExecutorAsync` runs inside the executor build: after `IRequestExecutorManager.EvictExecutor`, the new executor returned by `GetExecutorAsync` has already been through every registered `IRequestExecutorWarmupTask` (the task must report `ApplyOnlyOnStartup = false` to run again on rebuilds). The task-run path is therefore the primary path; the gate-run path stays only as the defensive fallback required by SEC-OPS150-01.
- **G-150-A (design gap, for the architect).** D-150-5 skips `UseOperationExecution` for the warm-up request, but the execute stage is the largest GraphQL cold cost (about 200 ms). `{ __typename }` is resolved without a connector, so executing it instead of skipping it would warm the execution machinery at no side-effect cost. Decide in WP-C whether to drop `UseSkipWarmupExecution` for the constant `__typename` document (SEC-OPS150-06/-07 allow a skip only; executing a constant introspection-free `__typename` selection does not touch a resolver with side effects) or to keep the skip and accept the cold execute stage.
- `HostStartup_UserHostedServicesStartBeforeKestrelListens` pins that `GenericWebHostService` is registered after `RequestExecutorWarmupService`, so a warm-up run inside `StartAsync` delays the listener while a `BackgroundService` (D-150-7) does not.

## Changelog

- 2026-10-18: Added section 9 with the WP-A measurement results, the decision-rule outcome (WP-C: executor step plus MCP serialisation; OIDC and DI pre-resolution dropped; WP-D opened), the A-150-2 resolution and gap G-150-A.
