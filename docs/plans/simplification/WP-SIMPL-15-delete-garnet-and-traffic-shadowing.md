# WP-SIMPL-15 Delete embedded Garnet server and traffic shadowing
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-15-delete-embedded-garnet-server-and-traffic-shadowing-od-17) | Issues: #515, #491 (part) | Owner decisions: OD-17(1), D12 | Security conditions: SEC-SIMPL-30, 31, 34, 37; GHSA-x8g9-cm3h-p6jq coordination (see private advisory)

## 1. Goal and outcome
Remove `Microsoft.Garnet` and the embedded server, and the traffic shadowing feature. External Redis/Garnet via `StackExchange.Redis` stays; without it the per-node in-memory cache applies. Outcome: no `Garnet` type or package in src; no shadowing middleware, service or outbound client; about -900 src lines; multi-replica deployments without shared state fail closed for revocation, replay and rate-limit state outside Development.

## 2. Preconditions and dependencies
- Merged first: #504 (security test baseline), `fix/security-batch-5`. Coordinate with the security-lane fix for GHSA-x8g9-cm3h-p6jq (authorization cache invalidation): whichever merges second rebases.
- Parallel with WP-SIMPL-12/14/16.

## 3. Scope
### In scope
- Embedded Garnet server, its option `Caching:Garnet:EnableEmbeddedServer`, startup checks/validator branches, `Program.cs` block, package; traffic shadowing middleware, services, redactor, options, DI, docs, tests.
### Out of scope
- `StackExchange.Redis` and external Redis configuration; distributed-state consumers themselves (only their fail-closed wiring is checked).

## 4. Current state (verified at 63cbb893)
- `Infrastructure/Garnet/GarnetServerManager.cs` (260, incl. `IGarnetServerManager`); DI `Api/Extensions/DependencyInjection/GatewayStorageServiceExtensions.cs:45-53`; `Api/Program.cs:110` (`GetService<IGarnetServerManager>`); validator `Api/Configuration/Validation/GatewayStorageOptionsValidator.cs:41`; startup check `StartupChecks/StorageStartupChecks.cs:133-136`; package `Infrastructure.csproj:13` `Microsoft.Garnet 2.2.5`.
- Shadowing: `Api/Middleware/TrafficShadowingMiddleware.cs` (90), `Application/Diagnostics/Shadowing/{AstShadowingFilter(89),ITrafficShadowingService(14),PiiShadowingRedactor(473),ShadowRequest(13),TrafficShadowingService(272)}.cs`; DI `GatewayExecutionServiceExtensions.cs:156-163` (typed HttpClient + hosted service); middleware `GatewayApplicationBuilderExtensions.cs:111`; options `TrafficShadowingOptions` in `Domain/Options/Resilience/DegradedModeOptions.cs:39`.
- Tests (delete): Unit `GarnetEmbeddedServerTests.cs` (46), Garnet parts of `MemoryPackAndGarnetTests.cs` (223; keep MemoryPack cases), `Diagnostics/ShadowingRedactionExt3Tests.cs`, `ShadowingRedactionMisc14Tests.cs`, `TrafficShadowingGuardTests.cs`. Not related: `TrinoSqlEngine.Tests/Compiler/CteShadowingTests.cs` (keep).
- Docs: `docs/features/f-ops-01-traffic-shadowing-dark-replay.md`; Garnet sections in deployment/configuration docs (grep `EnableEmbeddedServer`).

## 5. Implementation steps (TDD order)
1. Baseline Security test list on #504 commit.
2. Red: add tests of section 6.
3. Delete Garnet: manager, DI lines, `Program.cs` block, validator and startup-check branches, `EnableEmbeddedServer` property (keep `Caching:Garnet` connection keys only if they configure the external client; otherwise rename to `Caching:Redis` with the old key failing fast), package.
4. SEC-SIMPL-34: in `StorageStartupChecks`, when non-Development and replica count > 1 (existing `isMultiNode` signal in `GatewayStorageOptionsValidator`) and no Redis connection is configured, fail startup for consumers of revocation, replay and rate-limit state; log an error for other shared-state consumers (HITL approvals, audit anchor cache). Single-node mode is explicit (`Caching:Mode=SingleNode`) and documented.
5. Delete shadowing files, DI (156-163), middleware line 111, `TrafficShadowingOptions` and its property on the parent options.
6. `UnknownGatewayKeyGuard`: `Caching:Garnet:EnableEmbeddedServer`, `Gateway:TrafficShadowing` (any value, incl. `false`).
7. Remove shadowing egress from Helm/deploy values and network-policy egress rules; delete docs; migration guide rows.

## 6. Tests
- Unit `Hosting/ProgramStartupTests.No_embedded_cache_server_started` (no hosted service of a Garnet type; no listening port).
- Unit `Hosting/DistributedStateStartupTests` [Trait("Category","Security")]: `MultiNode_without_shared_state_fails_startup_outside_development`, `SingleNode_mode_starts_with_in_memory_state`, `Development_starts_with_warning`.
- Unit `Configuration/RemovedConfigKeysTests.Garnet_embedded_key_fails_startup`, `.TrafficShadowing_keys_fail_startup` [Security].
- Unit `Security/NoShadowEgressTests.No_http_client_registered_for_shadow_target` [Security].
- Architecture `PackageBoundaryArchitectureTests.No_garnet_package` (forbid `Microsoft.Garnet`); `DeletedFeaturesArchitectureTests` entries `Autheris.Infrastructure.Garnet`, `Autheris.Application.Diagnostics.Shadowing`.
- Tests needing shared state use a Redis Testcontainer or in-memory state (no embedded server).

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`; Unit + Architecture tests.
- `git grep -inE "Microsoft.Garnet|GarnetServerManager|EnableEmbeddedServer|TrafficShadowing|ShadowRequest" -- src deploy docs ':!docs/plans'` -> empty.

## 8. Acceptance criteria
- [ ] No Garnet package/type; no shadowing type, middleware, HttpClient or egress config.
- [ ] Multi-node without shared state fails closed outside Development (test green).
- [ ] Removed keys fail startup; Security-test diff mapped in PR description.

## 9. PR slicing
1. PR-A: Garnet removal + distributed-state startup tests.
2. PR-B: traffic shadowing removal + egress cleanup.

## 10. Risks, rollback, must NOT do
- Do NOT remove `StackExchange.Redis`. Do NOT let revocation/replay/rate-limit silently fall back to per-process state in multi-node. Do NOT keep `PiiShadowingRedactor` "for later" unless another consumer exists (grep first).
- Rollback: revert per PR.

## 11. Estimate
+150 / -1,050 src; tests -400; 1.5 person-days.

## Decisions taken in this spec
- D-15a: Multi-node detection reuses the existing `isMultiNode` logic of `GatewayStorageOptionsValidator`; no new topology option.
