# WP-SIMPL-14 Delete Casbin
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-14-delete-casbin) | Issues: #515, #491 (part) | Owner decisions: OD-3, OD-7 (GHSA-78j7-3gh6-7cqx), D12 | Security conditions: SEC-SIMPL-30, 31, 32, 33, 80, 85

> **Owner decision OD-30 (2026-11-05, ADR-020 accepted):** drop step 3 (`NativePolicyEnforcementService`) and decision D-14a. Casbin and `IPolicyEnforcementService` are deleted without a data-path replacement; data-access decisions come only from the access decision point of [WP-SIMPL-46](WP-SIMPL-46-unified-authorization-model.md). Re-estimate accordingly.

## 1. Goal and outcome
Remove the `Casbin.NET` package and every `Casbin*` type from core without weakening authorization. Outcome: `Casbin.NET` absent from all csproj; zero `Casbin` matches in `src`; the single `IPolicyEnforcementService` implementation is a native evaluator with identical decisions for table, column, masking, subject-attribute and row-filter rules; ReBAC stays enforced on all paths (OD-7); the DML write grant (`RestApi:AllowDml` + `DmlWriterRoles`) is independent of Casbin.

## 2. Preconditions and dependencies
- Merged first: #504 (security test baseline), `fix/security-batch-5`, WP-SIMPL-06 (`CasbinOptions.EnforceInQueryPipeline` deleted), the security-lane item for OD-7/SEC-SIMPL-85 (`RebacEnforcementTests.All_paths_enforced_when_tuples_exist_and_exemption_fails_startup` green on main).
- Parallel with WP-SIMPL-12/15/16 (shared: `UnknownGatewayKeyGuard`, `DeletedFeaturesArchitectureTests`).

## 3. Scope
### In scope
- Casbin engine, model contract, decision cache, options, validators, startup check, benchmark, package; port of the enforcement logic to a native evaluator; migration-guide row (Casbin policy file -> native policy rules).
### Out of scope
- ReBAC store/evaluator (`InMemoryRebacStore`, `ZanzibarRebacEvaluator`, `RebacEndpointFilter`) - unchanged; RBAC role model (OD-9 lane).

## 4. Current state (verified at 63cbb893)
- KEY FACT: `CasbinEnforcementService` (`Application/Governance/CasbinEnforcementService.cs:33`, partials `.Columns.cs` 597, `.Masking.cs` 577, `.SubjectAttributes.cs` 628, main 462) is the ONLY `IPolicyEnforcementService` implementation, registered at `Api/Extensions/DependencyInjection/GatewaySecurityServiceExtensions.cs:37-80` (Casbin `Enabled` defaults to `false`, `Domain/Options/Security/RlsOptions.cs:7-9`; when disabled the service still runs with `DefaultModelText`). Deleting Casbin therefore means replacing the policy engine, not deleting a side feature.
- Other Casbin files: `CasbinModelContract.cs` (159), `Governance/Caching/CasbinDecisionCache.cs` (175), `Domain/Options/CasbinDecisionCacheOptions.cs` (33), `Api/.../CasbinDecisionCacheOptionsValidator.cs` (36), `CasbinOptions` in `RlsOptions.cs`, `GatewayOptions.cs:31`, startup check `StartupChecks/PolicyAndNetworkStartupChecks.cs:41 CasbinModel`.
- Consumers: `Governance/Services/PolicySimulationService.cs:36-140` (uses `CasbinEnforcementService.EvaluateDraftAccess`, `DefaultModelText`, `CasbinModelContract`), `Extensions/Lakehouse/Services/IcebergRestCatalogFederationService.cs`, `Policy/TableAccessPolicy.Rls.cs`. 63 src files mention "Casbin".
- Package: `src/Autheris.Application/Autheris.Application.csproj:12` `Casbin.NET 2.21.3`.
- DML: `Domain/Options/Protocols/RestApiOptions.cs:91-95` `DmlWriterRoles` ("Consent and Casbin only evaluate read access") - the write grant is already role-based; `LegacySwitchGuard.cs`, `GraphQlAndSwitchStartupChecks.cs` read `AllowDml`.
- Tests (Unit, about 20 files): `CasbinAbacPropertyTests`, `CasbinHotReloadTests`, `CasbinRlsPushdownTests`, `CasbinSnapshotImmutabilityTests`, `Governance/Casbin{DecisionCacheOptionsValidator,DecisionCache,GlobalPolicyShortForm,ModelContract,ObjectPattern,PolicyEpoch,SharedDecisionCache}Tests`, `Policy/TypedCasbinRowPolicyTests`, `Security/Casbin{ActionClaimSpoofing,AmbiguousAttribute,LockFreeEnforcement,OptionsValidation,PolicyParsingAndAuthHardening,StartupValidation}Tests`; benchmark `CasbinDecisionBenchmark.cs`.

## 5. Implementation steps (TDD order)
1. Baseline: on the #504 commit list Security tests (`--list-tests --filter Category=Security`) and write `CasbinSecurityTestMap` in the PR description: each Casbin security test -> its native-evaluator equivalent (same scenario, renamed).
2. Red: port the Casbin security and property tests to target `IPolicyEnforcementService` through a factory (not the concrete type): rename to `PolicyEngine*Tests` (e.g. `PolicyEngineActionClaimSpoofingTests`, `PolicyEngineAmbiguousAttributeTests`, `PolicyEngineSnapshotImmutabilityTests`, `PolicyEngineRowPolicyTests`, `PolicyEnginePolicyEpochTests`). They must pass against `CasbinEnforcementService` first (characterization, green).
3. Add `NativePolicyEnforcementService : IPolicyEnforcementService` in `Application/Governance/` reusing the existing native matcher used by `EvaluateDraftAccess` (move it to `PolicyRuleMatcher`), the column/masking/subject-attribute logic (move partials, drop Casbin `Enforcer` calls), and the decision cache renamed `PolicyDecisionCache` (options section renamed; old section key fails fast). Run the step-2 tests against both implementations (Theory over factories) - all green.
4. Switch DI (`GatewaySecurityServiceExtensions.cs:37`) to the native service; policy file loading keeps the same CSV rule format (`p, sub, obj, act, ...`), parsed natively; `ModelPath` is removed (no custom models).
5. Update `PolicySimulationService`, `IcebergRestCatalogFederationService`, `TableAccessPolicy.Rls.cs` to the native types.
6. Delete Casbin types, `CasbinModel` startup check, options, validator, benchmark, the package, the concrete-type test factory; delete Casbin-only tests (model contract, custom model parsing).
7. Fail-closed config: `UnknownGatewayKeyGuard` entries `Gateway:Casbin:ModelPath`, `Gateway:Casbin:EnforceInQueryPipeline`, `Gateway:CasbinDecisionCache:*` with message naming the replacement (`Gateway:Policy:*`); if `Gateway:Casbin:PolicyPath` is kept as input, rename to `Gateway:Policy:PolicyPath` and fail fast on the old key.
8. DML (SEC-SIMPL-80): add an explicit `IDmlWriteGrant` check (`AllowDml` && role in `DmlWriterRoles` && table write grant) on the WebSQL DML path, evaluated before execution, fail closed on exception.

## 6. Tests
- Unit `Security/DmlSecurityTests` [Trait("Category","Security")]: `Dml_denied_when_AllowDml_false`, `Dml_denied_without_write_grant_after_casbin_removal`, `Dml_denied_when_grant_evaluation_throws`, `Dml_not_executed_when_audit_write_fails`.
- Integration `AuthorizationInventoryTests.Every_endpoint_has_authorization_or_is_on_the_public_allowlist` [Security] (SEC-SIMPL-33), green before and after.
- Unit `RebacEnforcementTests.All_paths_enforced_when_tuples_exist_and_exemption_fails_startup` [Security] stays green (OD-7).
- Unit `PolicyEngine*Tests` (ported, [Security] where the source was) incl. `PolicyEngineParityTests.Native_matches_casbin_on_corpus` (removed in step 6 after parity proven; corpus kept as golden expected decisions).
- Unit `Configuration/RemovedConfigKeysTests.Casbin_keys_fail_startup` [Security].
- Architecture: `DeletedFeaturesArchitectureTests` entry `Casbin`; `PackageBoundaryArchitectureTests.No_casbin_package` forbids `Casbin.*`; `NoScopedAccessPolicyEngineTests` stays green.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Unit/... -c Release --no-build --filter "FullyQualifiedName~PolicyEngine|FullyQualifiedName~Dml|FullyQualifiedName~Rebac|FullyQualifiedName~PolicySimulation"`, then full Unit and Architecture.
- `git grep -in casbin -- src deploy '*.csproj'` -> empty. Security list diff vs baseline: every removed name mapped.

## 8. Acceptance criteria
- [ ] No Casbin package or type; one native `IPolicyEnforcementService`.
- [ ] Ported security tests green; Security-test diff fully mapped (no unmapped loss).
- [ ] DML mandatory tests green; ReBAC all-path test green; authorization inventory green.
- [ ] Removed keys fail startup.

## 9. PR slicing (`size-exception` likely for PR-B)
1. PR-A: characterization tests via factory (steps 1-2).
2. PR-B: native evaluator + parity Theory + DI switch (steps 3-5) [size-exception: moved code].
3. PR-C: deletion, guard keys, DML grant check and tests, package removal (steps 6-8).

## 10. Risks, rollback, must NOT do
- Do NOT delete the evaluation logic of `.Columns/.Masking/.SubjectAttributes` - move it. Do NOT switch DI before parity is green. Do NOT touch ReBAC code. Do NOT let a missing policy file mean "allow all": default-deny stays.
- Rollback: revert PR-C/PR-B independently (DI switch is one line).

## 11. Estimate
+900 (moved/native) / -3,300 src; tests -1,500 net; 4 person-days (plan said 2 pd; raised because Casbin is the policy engine).

## Decisions taken in this spec
- D-14a: Casbin is replaced by a native evaluator with the same CSV rule format (custom Casbin models dropped), because Casbin is the sole `IPolicyEnforcementService`.
- D-14b: DML write grant made an explicit check (SEC-SIMPL-80) rather than relying on `DmlWriterRoles` comments.
