# WP-SIMPL-15 Delete embedded Garnet server and traffic shadowing
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-15-delete-embedded-garnet-server-and-traffic-shadowing-od-17) | Issues: #515, #491 (part), #489 (part: in-memory revocation and lockout state, assigned by WP-SIMPL-43) | Owner decisions: OD-17(1), OD-27 (deprovisioning effective cluster-wide), D12 | Security conditions: SEC-SIMPL-30, 31, 34, 37; GHSA-x8g9-cm3h-p6jq coordination (see private advisory)

## 1. Goal and outcome
Remove `Microsoft.Garnet` and the embedded server, and the traffic shadowing feature. External Redis/Garnet via `StackExchange.Redis` stays; without it the per-node in-memory cache applies. Outcome: no `Garnet` type or package in src; no shadowing middleware, service or outbound client; about -900 src lines; multi-replica deployments without shared state fail closed outside Development for every consumer whose per-process fallback would weaken security: token revocation, replay, rate limit, subject lockout and the cluster-wide identity/policy invalidation that makes SCIM deprovisioning effective on all nodes (OD-27).

## 2. Preconditions and dependencies
- Merged first: #504 (security test baseline), `fix/security-batch-5`. Coordinate with the security-lane fix for GHSA-x8g9-cm3h-p6jq (Authorization caches; details: private advisory): whichever merges second rebases.
- WP-SIMPL-43 owns the identity mechanics (SCIM deprovisioning, `IdentityGroupsChanged` invalidation, policy epoch; its steps 9 and 17); this WP owns that their shared state is never silently per-process in multi-node (WP-SIMPL-43 section 2 and #489 "in-memory revocation" item).
- Parallel with WP-SIMPL-12/14/16.

## 3. Scope
### In scope
- Embedded Garnet server, its options (`GarnetOptions`, all `Caching:Garnet` keys), startup checks/validator branches, `Program.cs` block, package; traffic shadowing middleware, services, redactor, options, DI, docs, tests.
### Out of scope
- `StackExchange.Redis` and external Redis configuration; distributed-state consumers themselves (only their fail-closed wiring is checked); SCIM deprovisioning logic and epoch semantics (WP-SIMPL-43).

## 4. Current state (verified at 63cbb893)
- Options: `CachingOptions` (`Domain/Options/Storage/RedisOptions.cs:5`) holds `Redis` (`RedisOptions`, `Caching:Redis`) and `Garnet` (`GarnetOptions`, `Caching:Garnet`). `GarnetOptions` holds only embedded-server keys (`EnableEmbeddedServer`, `MemorySizeMb`, `Host`, `Port`, console/auth/TLS settings of the embedded server); the external client is configured by `Caching:Redis`.
- The fail-closed rule already exists: `GatewayStorageOptionsValidator.cs:46` fails startup when `!IsDevelopment && isMultiNode && !opts.Caching.Redis.Enabled` (`isMultiNode` = `HighAvailability.MultiNodeClusterMode || HighAvailability.Replicas > 1`, :29). `:41` rejects the embedded server in multi-node.
- Shared-state consumers (`GatewayStorageServiceExtensions.cs:69-94`): with Redis `RedisEventBus`, `RedisRateLimiterService`, `RedisTokenRevocationService`; without Redis `InProcessChannelEventBus`, `InMemoryRateLimiterService`, `InMemoryTokenRevocationService` (#489 "in-memory revocation"). The event bus carries the WP-SIMPL-43 `IdentityGroupsChanged` invalidation and policy-epoch bumps used for OD-27 deprovisioning. Subject lockout (`Api/Middleware/SubjectLockoutMiddleware.cs`) reads `IIdentityStore.IsSubjectLockedAsync` from the governance database (shared once SQLite is gone, WP-SIMPL-11).
- `Infrastructure/Garnet/GarnetServerManager.cs` (260, incl. `IGarnetServerManager`); DI `Api/Extensions/DependencyInjection/GatewayStorageServiceExtensions.cs:45-53`; `Api/Program.cs:110` (`GetService<IGarnetServerManager>`); validator `Api/Configuration/Validation/GatewayStorageOptionsValidator.cs:41`; startup check `StartupChecks/StorageStartupChecks.cs:133-136`; package `Infrastructure.csproj:13` `Microsoft.Garnet 2.2.5`.
- Shadowing: `Api/Middleware/TrafficShadowingMiddleware.cs` (90), `Application/Diagnostics/Shadowing/{AstShadowingFilter(89),ITrafficShadowingService(14),PiiShadowingRedactor(473),ShadowRequest(13),TrafficShadowingService(272)}.cs`; DI `GatewayExecutionServiceExtensions.cs:156-163` (typed HttpClient + hosted service); middleware `GatewayApplicationBuilderExtensions.cs:111`; options `TrafficShadowingOptions` in `Domain/Options/Resilience/DegradedModeOptions.cs:39`.
- Tests (delete): Unit `GarnetEmbeddedServerTests.cs` (46), Garnet parts of `MemoryPackAndGarnetTests.cs` (223; keep MemoryPack cases), `Diagnostics/ShadowingRedactionExt3Tests.cs`, `ShadowingRedactionMisc14Tests.cs`, `TrafficShadowingGuardTests.cs`. Not related: `TrinoSqlEngine.Tests/Compiler/CteShadowingTests.cs` (keep).
- Docs: `docs/features/f-ops-01-traffic-shadowing-dark-replay.md`; Garnet sections in deployment/configuration docs (grep `EnableEmbeddedServer`).

## 5. Implementation steps (TDD order)
1. Baseline Security test list on #504 commit.
2. Red: add tests of section 6.
3. Delete Garnet: manager, DI lines, `Program.cs` block, validator branch `:41` and startup-check branches, the whole `GarnetOptions` class and `CachingOptions.Garnet`, package. Guard every `Caching:Garnet:*` key (fail fast, any value). `Caching:Redis` is unchanged; no rename.
4. SEC-SIMPL-34: extend the existing rule `GatewayStorageOptionsValidator.cs:46` (do not add a second check). Outside Development, multi-node (`isMultiNode`) without `Caching:Redis:Enabled` keeps failing startup; the failure message lists each affected consumer: token revocation, replay cache, rate limit, subject lockout cache (if any cache sits in front of `IsSubjectLockedAsync`; verify) and the identity/policy invalidation event bus (OD-27 deprovisioning, WP-SIMPL-43). Log one error per other shared-state consumer (HITL approvals, audit anchor cache). Single node = `HighAvailability.Replicas <= 1` and not `MultiNodeClusterMode`; no new option (D-15a). Assert at startup that a multi-node host never resolves `InMemoryTokenRevocationService`, `InMemoryRateLimiterService` or `InProcessChannelEventBus`.
5. Delete shadowing files, DI (156-163), middleware line 111, `TrafficShadowingOptions` and its property on the parent options.
6. `UnknownGatewayKeyGuard`: `Caching:Garnet` with every child key, `Gateway:TrafficShadowing` (any value, incl. `false`).
7. Remove shadowing egress from Helm/deploy values and network-policy egress rules; delete docs; migration guide rows.

## 6. Tests
- Unit `Hosting/ProgramStartupTests.No_embedded_cache_server_started` (no hosted service of a Garnet type; no listening port).
- Unit `Hosting/DistributedStateTests` [Trait("Category","Security")]: `Missing_shared_state_fails_closed_in_production_for_revocation_replay_ratelimit` (exact name from the 4.2 mandatory list), `MultiNode_without_shared_state_fails_for_lockout_and_deprovisioning` (lockout and the identity/policy invalidation bus of OD-27 are named in the failure; a multi-node host never resolves an in-memory revocation service, rate limiter or event bus), `SingleNode_starts_with_in_memory_state`, `Development_starts_with_warning`.
- Unit `Configuration/RemovedConfigKeysTests.Every_removed_key_fails_startup` (shared 4.2 theory; append rows), `.Garnet_keys_fail_startup`, `.TrafficShadowing_keys_fail_startup` [Security].
- Integration `RemovedEndpointsTests.Removed_routes_return_404_for_admin_and_anonymous` (rows for any traffic-shadowing admin route; none if the feature has no route), `IngressExemptionTests.Exemption_list_contains_only_existing_routes`, `AuthorizationInventoryTests.Every_endpoint_has_authorization_or_is_on_the_public_allowlist` [Security], green before and after (shared classes with WP-SIMPL-12/14/16; create if absent, append rows).
- Unit `Security/NoShadowEgressTests.No_http_client_registered_for_shadow_target` [Security].
- Architecture `PackageBoundaryArchitectureTests.No_garnet_package` (forbid `Microsoft.Garnet`); `DeletedFeaturesArchitectureTests` entries `Autheris.Infrastructure.Garnet`, `Autheris.Application.Diagnostics.Shadowing`.
- Tests needing shared state use a Redis Testcontainer or in-memory state (no embedded server).

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`; Unit + Architecture tests.
- `git grep -inE "Microsoft.Garnet|GarnetServerManager|EnableEmbeddedServer|TrafficShadowing|ShadowRequest" -- src deploy docs ':!docs/plans'` -> empty.

## 8. Acceptance criteria
- [ ] No Garnet package/type; no shadowing type, middleware, HttpClient or egress config.
- [ ] Multi-node without shared state fails closed outside Development for revocation, replay, rate limit, lockout and the deprovisioning invalidation bus (tests green); no new topology option.
- [ ] Removed keys fail startup; Security-test diff mapped in PR description.

## 9. PR slicing
1. PR-A: Garnet removal + distributed-state startup tests.
2. PR-B: traffic shadowing removal + egress cleanup.

## 10. Risks, rollback, must NOT do
- Do NOT remove `StackExchange.Redis`. Do NOT let revocation, replay, rate limit, lockout or the deprovisioning invalidation bus silently fall back to per-process state in multi-node. Do NOT add a `Caching:Mode` or any other topology option. Do NOT keep `PiiShadowingRedactor` "for later" unless another consumer exists (grep first).
- Rollback: revert per PR.

## 11. Estimate
+150 / -1,050 src; tests -400; 1.5 person-days.

## Decisions taken in this spec
- D-15a: Multi-node detection reuses the existing `isMultiNode` logic and the existing rule at `GatewayStorageOptionsValidator.cs:46`; no new topology option (no `Caching:Mode`).
- D-15b: The #489 "in-memory revocation" item and the shared state behind OD-27 deprovisioning are covered here as startup fail-closed rules (assigned by WP-SIMPL-43); their functional behaviour stays in WP-SIMPL-43.
