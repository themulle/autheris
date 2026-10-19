# REQ-OPS-150: Startup Warm-Up of the GraphQL and MCP Request Paths Before `/health/ready`

**Document ID:** `REQ-OPS-150`
**Date:** 2026-10-17
**Status:** PRD READY (Phase 1 complete); next: Phase 2 plan (measurement first), Phase 3 security review
**Priority:** P2 operational quality; Wave 2
**Author:** Enterprise Product Manager (Phase 1)
**Source:** GitHub issue #150 (owner, v1.1.15, unchanged in v1.1.16)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Pointer (intake 2026-10-17B):** [`REQ-BUG-157`](2026-10-17-req-issue-157-columnless-table-graphql-startup.md) removes a startup crash in the GraphQL schema build (column-less catalog tables); warm-up must not hide schema build failures.
**Related:** [REQ-BL-06 OpenTelemetry](2026-10-16-req-bl-06-open-telemetry.md) (metrics, health tracing), [PLAN-SCALE-5000](2026-10-16-plan-scale-5000-users.md) (performance lanes)

**Binding project constraints:** fail closed for security; no governed data read, no audit record and no privileged principal during warm-up; no query text or row values in logs; English only.

---

## 1. Problem

After every deployment the first authenticated GraphQL request and the first MCP tool call (`describe_dataset`, `describe_my_access`) take 1.2 to 2 s on an idle machine and up to 5 s under load; later requests take 0.14 to 0.4 s. Clients with a short per-request timeout fail on the first request. `/health/ready` already reports ready, so the load balancer sends traffic to a cold node.

## 2. Verified Current State

| # | Fact | Evidence |
|---|---|---|
| 1 | `/health/ready` returns 503 only while draining or when the cached health report is unhealthy (GovernanceDb, AuditChain, Redis, SecurityConfiguration). No warm-up state is checked. | `src/Autheris.Api/Endpoints/HealthEndpoints.cs:52-108`; components `src/Autheris.Infrastructure/Health/GatewayHealthCheckService.cs:105-178` |
| 2 | The health report is cached for 5 s and single-flight (anonymous endpoint protection, SEC R3-4). A "not ready" result would therefore delay readiness by up to 5 s. | `CachedGatewayHealthCheckService.cs:12`, registration `GatewayStorageServiceExtensions.cs:137` |
| 3 | The GraphQL schema and request executor are **not purely lazy**: `McpSchemaDiscoveryService.StartAsync` awaits `IRequestExecutorProvider.GetExecutorAsync`, which builds the schema, and reflects the `@mcpTool` fields. It is registered unconditionally in `AddAutherisMcp`. Exceptions are caught and logged as a warning. The issue's assumption (executor built on first use) is therefore **probably wrong for the schema**; the cost lies elsewhere or in later stages. | `src/Autheris.GraphQL/Mcp/McpSchemaDiscoveryService.cs:38-60, 98-101`; `GatewayMcpServiceExtensions.cs:46` |
| 4 | The HotChocolate pipeline is a long custom middleware chain (parser, validation, read-only, cost, masking, pagination, operation compile, execution). The schema also contains a runtime type module (`CatalogGraphQlTypeModule`) with periodic refresh (`CatalogSchemaRefreshSeconds`), so a schema rebuild can reintroduce a cold path later. | `src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.cs:551-610`, `:540-542` |
| 5 | HotChocolate 16.6.8 is used. The offline NuGet cache holds only 14.1.0 and 15.1.18 of `HotChocolate.Execution`; the 15.x assembly contains `RequestExecutorWarmup` types. The exact 16.6.8 warm-up API surface (`InitializeOnStartup`, warm-up tasks) must be confirmed by the architect against the 16.6.8 package. | `src/Autheris.Api/Autheris.Api.csproj:12`; local NuGet cache |
| 6 | The MCP server uses `ModelContextProtocol.AspNetCore` 2.2.0 in **stateless** HTTP mode, so the MCP server object and its handlers are created per request. Tool handlers resolve scoped services (consent resolution, catalog, profiles) per call. | `src/Autheris.Api/Mcp/GatewayMcpServer.cs:44-53`; package cache `modelcontextprotocol.aspnetcore/2.2.0` |
| 7 | One warm-up exists: `CatalogSearchWarmupService` (a `BackgroundService`) rebuilds the catalog search index. It does not wait for readiness, does not touch the request paths and is not part of the readiness decision. Other hosted services (`McpSchemaDiscoveryService`, `DocStoreStartupSyncService`, `GovernanceMigrationHostedService`, ...) run at startup without a readiness link. | `src/Autheris.Application/Catalog/Search/CatalogSearchWarmupService.cs:17-55`, `GatewayCatalogServiceExtensions.cs:23` |
| 8 | Both endpoints sit behind `RequireAuthorization`; no anonymous request can trigger lazy work. Audit is attached at endpoint level (`WithAudit`), so an in-process executor call without HTTP does not write audit, an HTTP self-call would. | `GatewayApplicationBuilderExtensions.cs:143-155`, `McpEndpoints.cs:54` |
| 9 | Authentication is JWT bearer (Entra ID, optional ADFS). Bearer handlers fetch the OIDC metadata and signing keys lazily on the first token validation, a plausible multi-second contribution to the first authenticated request that no warm-up of GraphQL or MCP code would remove. Unverified hypothesis; to be measured. | `src/Autheris.Api/Security/GatewayAuthSchemes.cs`; JwtBearer defaults |
| 10 | Governance metadata and decision caches are TTL-bound (30 s metadata cache, decision cache). A data-dependent warm-up decays after its TTL; only JIT, type-initialisation, DI and connection-level effects survive. | `docs/plans/2026-10-16-plan-issue-111-dataset-tags-catalog-visibility.md` G-5; `TableAccessPolicy.Consent.cs:20-80` |
| 11 | Runtime settings: `TieredPGO=true`, `ServerGarbageCollection=true`, `OptimizationPreference=Speed`. No `PublishReadyToRun` anywhere; the release image is built from `dotnet publish -c Release` without it. The benchmark Dockerfile only sets `DOTNET_ReadyToRun=1` (runtime switch), which is the default. | `src/Autheris.Api/Autheris.Api.csproj:25-31`, `.github/workflows/docker-publish.yml:59-65`, `benchmarks/load/docker/Dockerfile.gql:33-48` |
| 12 | The issue reports that `DOTNET_TieredCompilation=0` and `TieredPGO=0` did not change the first-request time. That points away from pure tiering and toward first-use initialisation (I/O, network, DI, serialisation set-up) or first-call JIT of large code that tiering switches do not remove. | issue text; consistent with fact 9 |
| 13 | Startup order: user `IHostedService`s registered through `AddHostedService` are started before Kestrel begins to listen in the minimal-hosting model (to be verified by an integration test in Phase 2). A blocking `StartAsync` warm-up would therefore delay the listener (connection refused, not 503). `TrafficDrainHostedService` already uses `/health/ready` 503 for shutdown only. | `GatewayExecutionServiceExtensions.cs:116`, `TrafficDrainService.cs:56-117` |

## 3. Assessment of the Issue

**Verdict: sensible with changes.** The goal (the first authenticated request is as fast as later ones; readiness reflects it) is right and the proposal direction (warm during startup, gate readiness) is standard practice. Two changes: do not build on an unverified cause, and constrain the warm-up so that it cannot become a security or audit problem.

1. **Measure first (fact 3, 9, 12).** The schema is already built at startup, so "build the executor earlier" will likely change nothing. A required first work package produces a cold-path breakdown (stage timings of one restarted container: authentication and OIDC metadata, DI scope creation, HotChocolate parse/validate/compile, resolver, consent resolution, governance DB, MCP serialisation, JIT time via event counters or `dotnet-trace`). Warm-up targets are chosen from that evidence, not from the assumption.
2. **Warm-up must not use requests.** Anti-patterns rejected: (a) a self-HTTP call with a fake or privileged token (needs an auth bypass or synthetic principal; would write an audit record through `WithAudit`; pollutes metrics and rate-limit state); (b) executing a governed read with a synthetic principal (touches data, consent resolution and audit); (c) a public `/warmup` endpoint (new anonymous trigger); (d) a readiness gate with no upper bound (node never becomes ready if warm-up hangs).
3. **Readiness gate with bound.** Warm-up runs in the background after startup (like `CatalogSearchWarmupService`) and flips a singleton state; `/health/ready` returns 503 `Warming` until done or until a timeout expires, after which it reports ready and logs a warning (warm-up is an optimisation, not a security control, so a bounded fail-open is correct here). Draining still wins.
4. **ReadyToRun is in scope as a separate, measured work package, not part of the first cut.** It removes JIT for framework-independent app code (startup and first request), costs image size and a runtime-identifier-specific publish, and does not help I/O or network cold costs. Decide after the measurement: include only if JIT time is a measurable share and warm-up alone misses the target.

**Recommended solution.** (1) Cold-path measurement. (2) `IStartupWarmup` steps run by one `StartupWarmupService` (background), each step non-data and exception-isolated, results exposed through `IStartupWarmupState` and a `Warmup` readiness component. Candidate steps, subject to measurement: await the GraphQL executor and run HotChocolate's own warm-up (`InitializeOnStartup` or a warm-up task) with a trivial parse/validate/compile of an internal document that has no resolver reaching data; prefetch OIDC configuration and signing keys of configured bearer schemes (bounded timeout, never blocks on an unreachable IdP); exercise MCP serialisation and the tool list/schema construction in-process without a caller; resolve (not execute) the DI graphs of the hot services. (3) Optional: `PublishReadyToRun` for the release image.

**Size: M** (S for measurement, S-M for the warm-up framework and steps, S for ReadyToRun if accepted). **Dependencies:** none; coordinate with BL-06 (warm-up metrics). **Wave: 2.**

## 4. Requirements

**Measurement**

- **R-1** A repeatable cold-start benchmark (script, container restart, then N first requests: GraphQL query, MCP `describe_dataset`, MCP `describe_my_access`, each with a valid test token and a seeded consent) reports first-request and warm-median latency and a stage breakdown. Output is a table in the Phase 2 plan; the harness lives under `benchmarks/` and is not part of the PR gate.
- **R-2** The breakdown separates: authentication (OIDC metadata and key fetch), DI/scope creation, HotChocolate stages, consent resolution and governance DB, MCP protocol handling, JIT (runtime event counters), and data-source connection.

**Warm-up**

- **R-3** `StartupWarmupService` (background, started with the host) runs ordered `IStartupWarmup` steps. Each step has a name, a timeout and isolated failure handling: a failed step logs a warning (step name and exception type, no message with data) and does not stop the others.
- **R-4** Warm-up never calls an endpoint over HTTP, never creates a `ClaimsPrincipal` with identity, never calls consent resolution, a data source or the audit service, and never writes to the governance store. A unit test asserts that the warm-up registrations depend on none of `IAuditService`, `IConsentResolutionService`, a connector or repository write interface.
- **R-5** HotChocolate: the executor is built and warmed through the supported 16.6.8 warm-up API; any internal document used contains only introspection-free meta fields (for example `__typename`) so no resolver and no data middleware runs. If a custom middleware needs `HttpContext` or a principal, the step goes through parse, validation and operation compile only.
- **R-6** MCP: warm the serialisation and tool-registry paths (tool list, schema JSON, result types) in process without a caller and without handler execution.
- **R-7** Authentication: prefetch the OIDC configuration and signing keys of the configured JWT bearer schemes with a bounded timeout; an unreachable IdP must not delay readiness beyond the global timeout and must not change token validation behaviour (the real handler still validates, fails closed on missing keys).
- **R-8** Configuration `Startup:Warmup`: `Enabled` (default true), `TimeoutSeconds` (default 30, range 1-300), `RequireForReadiness` (default true), per-step enable flags. Invalid values reject startup.
- **R-9** Readiness: while warm-up runs and `RequireForReadiness` is true, `/health/ready` returns 503 with status `Warming` (Production body: status and timestamp only, as today). After completion, failure of every step, or timeout, it reports ready (the timeout and failures are logged and counted). A draining node stays 503. The 5 s health cache must not delay the flip: the warm-up state is checked in the endpoint before the cached component report.
- **R-10** `/health/live` is unchanged and never depends on warm-up.
- **R-11** Telemetry: warm-up duration, per-step duration and outcome as metrics on the existing meter (BL-06 naming), plus one structured log line at the end. Warm-up must not increment request, query or rate-limit counters.
- **R-12** Schema refresh: the architect verifies whether a catalog-driven schema rebuild (`CatalogSchemaRefreshSeconds`) exposes the same cold path to a later request, and if so uses the HotChocolate keep-warm behaviour so the old executor serves until the new one is warm.
- **R-13** ReadyToRun (conditional work package): if accepted after R-1/R-2, `PublishReadyToRun=true` with an explicit runtime identifier for the release publish step, size delta and start-up delta reported, SC-04 publish-content architecture test still green. Tiered compilation and PGO settings stay as they are (disabling tiering is not recommended: it slows start-up and removes PGO).

## 5. Acceptance Criteria

1. **AC-1** On a restarted container, right after `/health/ready` returns 200, the first authenticated GraphQL query and the first MCP `describe_dataset` complete within 2x the warm median (the issue's criterion), measured by the R-1 harness on three consecutive restarts; the result and environment are recorded in the Phase 2 or Phase 5 notes.
2. **AC-2** While warm-up runs, `/health/ready` returns 503 `Warming`; after warm-up it returns 200; with a step that never completes, it returns 200 after `TimeoutSeconds` with a warning logged (test with a fake step).
3. **AC-3** A throwing step does not stop other steps and does not fail the host start; the failure is logged and counted.
4. **AC-4** During warm-up no audit record is written, no data source receives a query and no consent resolution runs (test with recording fakes; also an integration check that the audit chain length is unchanged after startup).
5. **AC-5** No new endpoint or anonymous path exists; route table test unchanged.
6. **AC-6** With `Startup:Warmup:Enabled=false`, behaviour equals today and readiness is immediate.
7. **AC-7** Draining returns 503 regardless of warm-up state.
8. **AC-8** Unreachable IdP: readiness is delayed at most by the configured timeout; tokens are still validated by the real handler afterwards.
9. **AC-9** Config validation rejects `TimeoutSeconds` outside 1-300.
10. **AC-10** If R-13 is accepted: image builds, SC-04 test green, first-request latency improvement reported against the warm-up-only result.

## 6. Security Requirements

- **S-1** No authentication or authorization bypass is introduced for warm-up; no synthetic identity, no `AllowAnonymous` route, no privileged in-process principal.
- **S-2** No governed data read and no write: warm-up depends on no data source, consent, audit or governance-write interface (R-4); verified by dependency test and by Phase 3.
- **S-3** No audit record, no metric increments that could be mistaken for user traffic or distort rate limits, quotas, FinOps counters and the WORM chain.
- **S-4** Readiness stays anonymous and discloses only `Ready`/`Warming` and a timestamp in non-development environments; no step names or timings in the body.
- **S-5** Fail-open of the warm-up gate after the timeout is accepted because warm-up is not a security control; all security gates (security configuration health check, audit chain, drain) keep their fail-closed behaviour and are evaluated before the warm-up state.
- **S-6** OIDC prefetch uses only configured authorities (no new outbound targets), has a timeout, logs no token or key material, and does not cache a failed result as success.
- **S-7** Logs carry step names and exception types only.

## 7. Non-Goals

- **G-1** No warming of data-dependent caches (TTL-bound, fact 10) and no consent or catalog preload.
- **G-2** No change to authentication, GraphQL middleware order, or MCP tool semantics.
- **G-3** No Native AOT, no trimming, no tiered-compilation changes.
- **G-4** No Kubernetes manifests or probe tuning in this repository (documented hint: use a startup probe with a generous failure threshold).

## 8. Deviations from the Issue

| # | Issue | This PRD | Reason |
|---|---|---|---|
| D-1 | Assumes executor and MCP server are built lazily | Schema is already built at startup (fact 3); measurement first, warm-up targets chosen from evidence | Avoid fixing the wrong thing |
| D-2 | `/health/ready` ready only after warm-up | Same, but bounded by a timeout and exempt for draining and security health | Avoid a node that never becomes ready |
| D-3 | "Warm their first code paths" unspecified | No request-based warm-up, no fake identity, no data reads, no audit (R-4) | Anti-patterns |
| D-4 | ReadyToRun optional | Separate conditional work package decided after measurement | Evidence-based; adds image and build cost |
| D-5 | GraphQL and MCP only | Also OIDC metadata and key prefetch (hypothesis) | Likely part of the first authenticated request |

## 9. Open Owner Questions (recommended defaults)

- **Q-150-1** Is a bounded fail-open (ready after timeout) acceptable? Recommended: yes, 30 s default.
- **Q-150-2** Blocking warm-up in `StartAsync` (listener appears only when warm) versus background warm-up with a 503 `Warming` response? Recommended: background with 503 (works with a plain readiness probe; no connection-refused window, liveness unaffected).
- **Q-150-3** Is ReadyToRun acceptable for the release image (RID-specific publish, larger image)? Recommended: decide after the R-1 measurement; accept if JIT is at least a quarter of the cold-path time.
- **Q-150-4** Allow outbound OIDC metadata prefetch at startup? Recommended: yes, for the already configured authorities only, with timeout.
- **Q-150-5** Where does the cold-start benchmark run (needs a container runtime and a token provider)? Recommended: manual script under `benchmarks/`, results recorded in the plan; not in the PR gate.

## 10. Proposed Wave

Wave 2 (small to medium, independent of the other open items). Order: after BL-06 metrics are available or using the existing meter; no dependency on #107, #111, #129, #143. Work packages: WP-A measurement (S), WP-B warm-up framework and steps and readiness (S-M), WP-C ReadyToRun (S, conditional).
