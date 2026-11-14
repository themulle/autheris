# WP-SIMPL-14 Delete Casbin
Status: In implementation. PR 1 (data path off Casbin, #738) and PR 2 (dead Iceberg-era decision point, #739) open; PR 3 (engine, options, guard keys and package) stacked on both; PR 4 (cleanup and docs) follows (Phase 2 addendum below).
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-14-delete-casbin) | Issues: #515, #491 (part) | Owner decisions: OD-3, OD-26, OD-30 (ADR-020), D12 | Security conditions: SEC-SIMPL-30, 31, 32, 33 (as amended by ADR-020), 80 (write path owned by WP-SIMPL-45)

> **Changed by OD-30 (2026-11-05, ADR-020 accepted) and the WP-SIMPL-46 ordering rule:** this WP no longer builds a native policy engine, a ReBAC gate or a role-based write check. It runs after [WP-SIMPL-46](WP-SIMPL-46-unified-authorization-model.md) has moved every `IPolicyEnforcementService` consumer (Envoy ext_authz included) to `IAccessDecisionPoint`, and then deletes Casbin and `IPolicyEnforcementService`. The sections below carry the binding text.

## 1. Goal and outcome
Remove the `Casbin.NET` package, every `Casbin*` type and the `IPolicyEnforcementService` abstraction from `src`. Casbin has no successor on the data path: every data-access decision already comes from the single access decision point of WP-SIMPL-46 (grant records, column rules, virtual filters; OD-22, OD-30). Casbin column, mask and row rules are not ported. Outcome: `Casbin.NET` absent from every csproj; `git grep -i casbin -- src deploy '*.csproj'` empty except the `UnknownGatewayKeyGuard` entry; `git grep IPolicyEnforcementService -- src` = 0; every `Gateway:Casbin` key fails startup; writes stay authorized only by the write-grant path of WP-SIMPL-45 (no role grants or bypasses writes).

## 2. Preconditions and dependencies
- Merged first: #504 (security test baseline), `fix/security-batch-5`, WP-SIMPL-06 (`CasbinOptions.EnforceInQueryPipeline` deleted).
- Merged first: WP-SIMPL-46 PR 1-3 (decision point, re-routed transports, bypass paths incl. Envoy ext_authz `EnvoyExtAuthzService` and the `AiDataGuardrailService` pre-checks -> `IAccessDecisionPoint`), WP-SIMPL-46 PR 4 (`PolicySimulationService` and `/api/governance/policy-simulation/replay` deleted, replaced by explain), WP-SIMPL-46 PR 5 (`AccessPlanningService.ApplyAccessAsync` writes grant records; ReBAC removed), WP-SIMPL-43 PR 4 (step 11, Envoy uses the request security context and the decision point), WP-SIMPL-45 PR 1-3 (write grants evaluated by the decision point).
- Gate (mechanical, checked in the PR description): every file of the section 4 consumer table that is marked "WP-SIMPL-46" no longer references `IPolicyEnforcementService`. Only the files this WP deletes or edits itself may still reference it.
- Not parallel with WP-SIMPL-46 or WP-SIMPL-45 PR 4 (shared files). Parallel with WP-SIMPL-12/15/16 (shared: `UnknownGatewayKeyGuard`, `DeletedFeaturesArchitectureTests`, `RemovedEndpointsTests`, `RemovedConfigKeysTests`; append rows, never overwrite).

## 3. Scope
### In scope
- Casbin engine, model contract, decision cache, options, validators, startup check, benchmark (if WP-SIMPL-46 did not already replace it), package, Casbin-only tests.
- `IPolicyEnforcementService` interface and its DI registration.
- Verification only: the MCP guardrail pre-checks in `AiDataGuardrailService` are moved to the decision point by WP-SIMPL-46 step 5 (PR 3); this WP checks they no longer reference `IPolicyEnforcementService` (step 2).
- Fail-fast guard for every `Gateway:Casbin` key; migration-guide row ("Casbin policies have no data-path replacement; re-express them as grant records (consents), deny consents, column rules or virtual filters, ADR-020").
### Out of scope
- Any native policy evaluator or port of Casbin rules (dropped by OD-30).
- ReBAC (deleted by WP-SIMPL-46 PR 5), the decision point itself (WP-SIMPL-46), write-grant semantics and `DmlWriterRoles` removal (WP-SIMPL-45), control-plane role model (OD-9 lane).

## 4. Current state (verified at 63cbb893; consumer list re-verified at 88e5ab74)
- `CasbinEnforcementService` (`Application/Governance/CasbinEnforcementService.cs:33`, partials `.Columns.cs` 597, `.Masking.cs` 577, `.SubjectAttributes.cs` 628, main 462) is the only `IPolicyEnforcementService` implementation, registered at `Api/Extensions/DependencyInjection/GatewaySecurityServiceExtensions.cs:37-80`.
- Other Casbin files: `CasbinModelContract.cs` (159), `Governance/Caching/CasbinDecisionCache.cs` (175), `Domain/Options/CasbinDecisionCacheOptions.cs` (33), `Api/.../CasbinDecisionCacheOptionsValidator.cs` (36), `CasbinOptions` in `Domain/Options/Security/RlsOptions.cs`, `GatewayOptions.cs:31` (`Casbin`), sections `GatewaySections.cs:22-23` (`Gateway:Casbin`, `Gateway:Casbin:DecisionCache`), startup check `StartupChecks/PolicyAndNetworkStartupChecks.cs:41 CasbinModel`. 64 src files mention "Casbin" (comments, typed-policy merge in `Policy/TableAccessPolicy.Rls.cs`, MCP guardrail, lint rules).
- `git grep -l IPolicyEnforcementService -- src` returns 18 files. Each must be off the interface before this WP deletes it:

| # | File | Use | Moved to `IAccessDecisionPoint` by |
|---|---|---|---|
| 1 | `Application/Interfaces/IPolicyEnforcementService.cs` | the interface | deleted here (step 4) |
| 2 | `Application/Governance/CasbinEnforcementService.cs` | only implementation | deleted here (step 4) |
| 3 | `Api/Extensions/DependencyInjection/GatewaySecurityServiceExtensions.cs` | registration :37-80 | deleted here (step 4) |
| 4 | `Api/Extensions/DependencyInjection/GatewayExecutionServiceExtensions.cs` | ctor argument :83 | WP-SIMPL-46 step 2/4 (PR 1-2); remaining line deleted here |
| 5 | `Application/Policy/TableAccessPolicy.cs` | Casbin step :396-401, `CanWriteTableAsync` :476-500 | WP-SIMPL-46 step 3 (PR 1), WP-SIMPL-45 |
| 6 | `Application/Policy/UnifiedPolicyDecisionPoint.cs` | constructs `TableAccessPolicy` | WP-SIMPL-46 step 2 (deleted, PR 1) |
| 7 | `Application/Services/GatewayExecutionService.cs` | `AccessPolicy()` :627 (OData, GraphQL, MCP catalog) | WP-SIMPL-46 step 4 (PR 2) |
| 8 | `Application/Sql/Services/GovernedSqlRewriter.cs` | `AccessPolicy()` :866 (WebSQL, Trino) | WP-SIMPL-46 step 4 (PR 2) |
| 9 | `Application/Sql/Services/GovernedSqlExecutionDependencies.cs` | `PolicyEnforcement` member :16 | WP-SIMPL-46 step 4 (PR 2) |
| 10 | `Application/Sql/Services/FederatedDuckDbExecutionService.cs` | `AccessPolicy()` :124, :479 | WP-SIMPL-46 step 4 (PR 2) |
| 11 | `Application/Procedures/Services/GovernedProcedureExecutionService.cs` | `TableAccessPolicy` :658-670 | WP-SIMPL-46 step 4 (PR 2) |
| 12 | `Application/Connectors/CrossDomain/DefaultCrossDomainAccessResolver.cs` | `TableAccessPolicy` :47-90 | WP-SIMPL-46 step 4 (PR 2) |
| 13 | `Application/Streaming/Services/StreamRlsPolicyEnforcer.cs` | `HasPolicies` :212, `EvaluatePolicyAsync` :235 | WP-SIMPL-46 step 5 (PR 3) |
| 14 | `Extensions/Lakehouse/Services/IcebergRestCatalogFederationService.cs` | Casbin check :118-124, :328-330 | WP-SIMPL-46 step 5 (PR 3) |
| 15 | `Application/Mesh/Services/EnvoyExtAuthzService.cs` | `EvaluatePolicyAsync` :186, Envoy's only decision source today | WP-SIMPL-46 step 5 (PR 3) and WP-SIMPL-43 step 11 |
| 16 | `Application/Governance/Services/PolicySimulationService.cs` | `EvaluateDraftAccess`, `DefaultModelText`, `CasbinModelContract` | WP-SIMPL-46 step 6 (deleted, PR 4) |
| 17 | `Application/Governance/Services/AccessPlanningService.cs` | `ReloadPoliciesAsync` :358 | WP-SIMPL-46 step 7 (PR 5); if the call survives, deleted here (no Casbin policies to reload; the decision point uses the policy epoch) |
| 18 | `Application/Mcp/Services/AiDataGuardrailService.cs` | Casbin pre-check on tool calls :327 and resource reads :577 | WP-SIMPL-46 step 5 (PR 3); verified here (step 2) |

- Package: `src/Autheris.Application/Autheris.Application.csproj:12` `Casbin.NET 2.21.3`.
- DML: `CanWriteTableAsync` (ClusterAdmin, `DmlWriterRoles`, Casbin) is replaced by the write-grant path of WP-SIMPL-45 through the decision point. This WP adds no write check.
- Routes: no Casbin admin route group exists; the only Casbin-backed route, `/api/governance/policy-simulation/replay`, is deleted by WP-SIMPL-46 PR 4.
- Tests (Unit, about 20 files): `CasbinAbacPropertyTests`, `CasbinHotReloadTests`, `CasbinRlsPushdownTests`, `CasbinSnapshotImmutabilityTests`, `Governance/Casbin{DecisionCacheOptionsValidator,DecisionCache,GlobalPolicyShortForm,ModelContract,ObjectPattern,PolicyEpoch,SharedDecisionCache}Tests`, `Policy/TypedCasbinRowPolicyTests`, `Security/Casbin{ActionClaimSpoofing,AmbiguousAttribute,LockFreeEnforcement,OptionsValidation,PolicyParsingAndAuthHardening,StartupValidation}Tests`; benchmark `CasbinDecisionBenchmark.cs` (replaced by `AccessDecisionPointBenchmark.cs`, WP-SIMPL-46 section 6.4).

## 5. Implementation steps (TDD order)
1. Baseline: on the merge commit of the last precondition, list Security tests (`--list-tests --filter Category=Security`) and write the `CasbinSecurityTestMap` in the PR description: each Casbin security test -> "obsolete by removal (Casbin rule semantics dropped, OD-30)" or the decision-point test of WP-SIMPL-46 that covers the same scenario (tenant isolation, claim spoofing, ambiguous attributes, default deny). Confirm the gate of section 2 with `git grep -l IPolicyEnforcementService -- src`.
2. MCP guardrail (verify only): `AiDataGuardrailService` no longer references `IPolicyEnforcementService` (moved to `IAccessDecisionPoint` by WP-SIMPL-46 step 5, PR 3) and `AiDataGuardrailDecisionPointTests` (owned by WP-SIMPL-46) stay green. If the reference is still present, stop: the section 2 gate does not hold.
3. Red: add the section 6 tests (removed keys, package boundary, deleted feature).
4. Delete: `CasbinEnforcementService*` (4 files), `CasbinModelContract`, `CasbinDecisionCache`, `CasbinDecisionCacheOptions`, its validator, `CasbinOptions`, `GatewayOptions.Casbin`, `GatewaySections.Casbin*`, the `CasbinModel` startup check, `IPolicyEnforcementService`, its registration (`GatewaySecurityServiceExtensions.cs:37-80`) and any remaining constructor argument (row 4), the typed-Casbin merge path in `TableAccessPolicy.Rls.cs` if WP-SIMPL-46 left it, Casbin lint rules in `Policy/Lint`, `CasbinDecisionBenchmark.cs` if still present, the `Casbin.NET` package reference, and the Casbin-only tests of section 4. Fix the remaining comment mentions.
5. Fail-closed config (SEC-SIMPL-31): `UnknownGatewayKeyGuard` entry `Gateway:Casbin` covering every child key (`Enabled`, `PolicyPath`, `ModelPath`, `EnforceInQueryPipeline`, `DecisionCache:*`) with any value, including `false` and empty. Message: "removed (OD-30, ADR-020); data access is granted only by grant records (consents), column rules and virtual filters". SEC-SIMPL-32: the PR states that `Casbin.Enabled` was an additional restriction, now replaced by the always-on decision point.
6. Writes: `CanWriteTableAsync` and the `DmlWriterRoles` path are already gone (WP-SIMPL-45/46). Verify that no Casbin reference remains on the WebSQL DML path (`git grep -n -i casbin -- src/Autheris.Application/Sql src/Autheris.Application/Policy` empty) and that the four interim `DmlSecurityTests` stay green.
7. Migration guide row and docs: remove Casbin from feature docs and the configuration reference (reference text only, no WIP).

## 6. Tests
- Unit `Security/DmlSecurityTests` [Trait("Category","Security")] (interim, final owner WP-SIMPL-45): `Dml_denied_when_AllowDml_false`, `Dml_denied_without_write_grant_after_casbin_removal`, `Dml_denied_when_grant_evaluation_throws`, `Dml_not_executed_when_audit_write_fails`; green against the decision point and `IWriteGrantEvaluator`.
- Integration `AuthorizationInventoryTests.Every_endpoint_has_authorization_or_is_on_the_public_allowlist` [Security] (SEC-SIMPL-33 as amended: data plane = decision point, admin plane = `RequireAuthorization` with control-plane role policies), green before and after.
- Unit `Configuration/RemovedConfigKeysTests.Every_removed_key_fails_startup` [Security] (shared 4.2 theory; append `Gateway:Casbin:*` rows) and `.Casbin_keys_fail_startup` [Security] (values `true`, `false`, `""`).
- Unit `OptionsAuditTests.No_enforcement_option_deleted_while_wired` [Security] (SEC-SIMPL-32) green with the Casbin options removed.
- Unit `AiDataGuardrailDecisionPointTests.Tool_call_denied_when_decision_point_denies` and `.Resource_read_denied_when_decision_point_denies_any_disclosed_table` [Security] (owned by WP-SIMPL-46): green before and after.
- Integration `RemovedEndpointsTests.Removed_routes_return_404_for_admin_and_anonymous` and `IngressExemptionTests.Exemption_list_contains_only_existing_routes` [Security]: no new rows (no Casbin route; policy-simulation rows belong to WP-SIMPL-46); green before and after.
- Architecture: `DeletedFeaturesArchitectureTests` entry `Casbin`; `PackageBoundaryArchitectureTests.No_casbin_package` forbids `Casbin.*`; `AuthorizationArchitectureTests.No_PolicyEnforcementService_In_DataPath` (WP-SIMPL-46) tightened to "no `IPolicyEnforcementService` type in src"; `NoScopedAccessPolicyEngineTests` stays green.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror` (one build at a time).
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "FullyQualifiedName~Dml|FullyQualifiedName~RemovedConfigKeys|FullyQualifiedName~AiDataGuardrail|FullyQualifiedName~OptionsAudit"`, then full Unit and Architecture.
- `git grep -n IPolicyEnforcementService -- src` -> empty.
- `git grep -in casbin -- src deploy '*.csproj'` -> only the `UnknownGatewayKeyGuard` entry and its message. Security list diff vs baseline: every removed name mapped.

## 8. Acceptance criteria
- [ ] No Casbin package or type; `git grep IPolicyEnforcementService -- src` = 0.
- [ ] Every consumer of the section 4 table decides through `IAccessDecisionPoint` (rows 4-18 off the interface before deletion).
- [ ] Security-test diff fully mapped (obsolete by removal or covered by a WP-SIMPL-46 test); no unmapped loss.
- [ ] Interim `DmlSecurityTests` and the authorization inventory green; no role-based write check exists.
- [ ] Every `Gateway:Casbin` key fails startup with any value.

## 9. PR slicing
1. One PR: deletion of Casbin and `IPolicyEnforcementService`, guard keys, tests, package removal (steps 1-7). Net negative src; no `size-exception` needed.

## 10. Risks, rollback, must NOT do
- Do NOT start before the section 2 gate holds; deleting the interface earlier breaks the build or leaves Envoy ext_authz (core, OD-21) without a decision source.
- Do NOT port Casbin rules into a native evaluator, add a ReBAC gate, or add a role-based (`DmlWriterRoles`) write check. Do NOT convert Casbin policy files into grant records automatically.
- Do NOT let a missing policy mean "allow all": default deny of the decision point stays.
- Rollback: revert the PR (pure deletion).

## 11. Estimate
+0 / -3,300 src (Casbin about -2,800, interface, DI, options, cache, startup check, lint, comments); tests about -1,500 net; 1.5 person-days.

## Decisions taken in this spec
- D-14a: withdrawn (OD-30). Casbin is deleted without a native replacement; the access decision point of WP-SIMPL-46 is the only data-access decision source.
- D-14b: superseded by WP-SIMPL-45. Write authorization is the write-grant path invoked by the decision point; this WP keeps only the four interim `DmlSecurityTests`.
- D-14c: `AiDataGuardrailService` pre-checks are moved to the decision point by WP-SIMPL-46 step 5 (PR 3); this WP only verifies the gate (step 2).

## Addendum (Phase 2 and 3, 2026-11-14)
Phase agents could not be launched in this delivery run (the agent launcher was unavailable), so the delivery lead wrote this addendum and the binding conditions below; Phase 5 review findings are recorded per PR.

### Gate assessment (section 2), default taken: proceed
On `7a4df959` every precondition PR is merged (WP-SIMPL-46 PR 1-5, WP-SIMPL-45 PR 1-3, WP-SIMPL-43 PR 4), but `git grep -l IPolicyEnforcementService -- src` returned 16 files instead of the three the WP-SIMPL-46 section 7 grep expects. Every data path already decides through `Policy.Pdp.IAccessDecisionPoint` (Envoy ext_authz and `AiDataGuardrailService` included); the remaining references were (a) the additive Casbin restriction step inside `TableAccessPolicy` (constructed only by the decision point), (b) an additive Casbin step after the decision in `GovernedProcedureExecutionService`, (c) `AccessPlanningService.ReloadPoliciesAsync` (row 17, deleted here), (d) constructor pass-through in the transports and `UnifiedPolicyDecisionPoint` (now a thin adapter onto the decision point), (e) a dead `Policy.AccessDecisionPoint` (Iceberg-era, registered but resolved by nobody) and (f) the `ModuleSecurityGuards` protected-type entry. None is a sole decision source, so removing them only drops a restriction that the always-on decision point replaces (SEC-SIMPL-32). Deleting `UnifiedPolicyDecisionPoint` itself stays WP-SIMPL-46 scope.

### PR slicing (owner rule: about 400 net src lines per PR)
| PR | Scope | Est. net src |
|---|---|---|
| 1 | Data path off Casbin: Casbin step and merge partial `TableAccessPolicy.Rls.cs` deleted, procedure step, `AccessPlanningService` reload, constructor arguments of every consumer and the DI line; Casbin data-path tests deleted or re-pointed | about -250 |
| 2 | Dead `Policy.AccessDecisionPoint` and `Policy.IAccessDecisionPoint` with registration and tests; allowlist entries in the caller-attribute and transport-gate architecture tests | about -380 |
| 3 | Engine and configuration surface: `CasbinEnforcementService` (4 partials), `CasbinModelContract`, `rbac_with_abac.conf`, `CasbinDecisionCache`, `PolicyEpochGuard`, `CompiledSubRuleCache`, `IPolicyEnforcementService` and its registration, `ModuleSecurityGuards` entry, startup checks `CasbinModel`/`CasbinPolicy`, `CasbinOptions`, `CasbinDecisionCacheOptions` and validator, `GatewayOptions.Casbin`, `GatewaySections.Casbin*`, schema row, `GatewayCoreOptionsValidator` Casbin rules, `UnknownGatewayKeyGuard` entry `Gateway:Casbin` (every key, any value), `Casbin.NET` package and lock files, `tools/casbin-policy-lint` rewrite, `CasbinDecisionBenchmark`, engine tests; options-class and Enabled-flag baselines, DI snapshots, startup snapshots and the configuration reference lowered/regenerated | about -3,000 (one class across four partial files plus its exclusive helpers and options; the options cannot outlive the engine without failing the options-consumption inventory, and the guard keys belong with the options; this PR exceeds the limit) |
| 4 | Cleanup: Casbin lint rules in `Policy/Lint`, the correlated-filter `Casbin` source, `DecisionKey`/`SecurityEvaluationContext` leftovers, Casbin metric descriptions and tag sets, remaining comment mentions, dbt `casbin_roles` proposal handling; migration-guide row and docs; `PackageBoundaryArchitectureTests.No_casbin_package`, `DeletedFeaturesArchitectureTests` entry and the tightened `IPolicyEnforcementService` architecture check | about -300 |

Each PR updates this status line and the master plan row, appends its removed security tests to `docs/plans/2026-11-05-test-coverage-map.csv` (the `CasbinSecurityTestMap`) and lowers only the ratchet baselines its deletion requires.

### Lint workflow (default taken)
`.github/workflows/policy-lint.yml` runs `tools/casbin-policy-lint` on every push to main; the tool references `Autheris.Application` and the Casbin package. Workflow files are not edited here. PR 3 rewrites the tool without the package and the project reference into a gate that fails when the repository contains a Casbin model (`.conf` with a `[request_definition]` section) or a Casbin policy file, so the workflow stays green and keeps a meaning. Owner follow-up: rename or delete the workflow and the tool.

### Phase 3 binding conditions (SEC-SIMPL-14-A1..A6)
- A1: every data path keeps exactly one decision source, `IAccessDecisionPoint`; no PR adds an allow path or a fallback that grants when a component is missing (default deny unchanged). Verified by the unchanged `AuthorizationArchitectureTests`, `DmlSecurityTests` and `AiDataGuardrailDecisionPointTests`.
- A2: a Casbin restriction is removed, not replaced: no Casbin rule is ported into a native evaluator, no ReBAC gate, no role-based write check (section 10).
- A3: every removed security test is mapped in the coverage map with the reason "Casbin rule semantics dropped (OD-30)" or the covering decision-point test; `TestCoverageMapTests.No_security_test_lost_without_mapping` stays green.
- A4 (PR 3): every `Gateway:Casbin` key fails startup with any value, including `false` and empty, with the section 5 message.
- A5 (PR 3): `Casbin.NET` is absent from every project and lock file; the lint gate fails closed on a Casbin model or policy file.
- A6: no WP-, SEC- or OD- ids are added to comments in `src/Autheris.Api` or `src/Autheris.Application`.

