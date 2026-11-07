# WP-SIMPL-50 Remediation of the v1.7.1..HEAD code review
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-50-remediation-of-the-v171head-code-review) | Source: code review of `v1.7.1..HEAD` (30 commits, 2026-11-07), three read-only sampled reviews (module host and trust, policy and config guards, OData move and deletions); not a full diff read, no build or test run | Security conditions: SEC-AZ-04 (fail closed), SEC-PROD-01 (no new anonymous routes); module items follow the WP-SIMPL-25 trust conditions

## 1. Goal and outcome
Close the findings of the review so that the Stage 3 deletions and moves leave no fail-open path, no dead ownership entry, no leftover secret plumbing and no lost security test coverage. Measurable: every acceptance item below is checked by an architecture or security test that fails on regression.

## 2. Preconditions and dependencies
- Item groups A, B and C are independent PRs. Group A is a release blocker; B and C may follow.
- Item A1 is exploitable in a misconfigured deployment. Its details live in a private advisory, not in this plan or in any PR text; the PR states only the fail-closed rule.
- WP-SIMPL-25 and WP-SIMPL-27 are merged (the module items change their code).

## 3. Scope
### In scope (findings verified against the code are marked [V])
**A. Release blockers**
- A1 [V] `ModuleLoader.TryLoadWorkspaceModules` (workspace fallback, `ModuleLoader.cs` ~226-240 and `FindWorkspaceModuleDll`): loads without signature, hash and permission checks when `Gateway:Modules:Path` is unset and the default directory is missing. Rule: allowed only when `IsDevelopment() && AllowUnsignedInDevelopment`; otherwise throw. No parent-directory walk; the dev path comes from explicit configuration.
- A2 [V] `AccessDecisionPoint.cs:173`: ABAC is skipped when the optional `IPolicyEnforcementService` is null. Make the dependency required (or deny when null). `_clientIpResolver?.ResolveClientIp() ?? IPAddress.None` must fail closed for IP-based rules.
- A3 [V] `.github/CODEOWNERS`: `src/Autheris.Application/Data/` no longer exists (masking subsystem unowned). Remap every dead path (`Data/`, `Auditing/`, `SqlEndpoints/`, `DataCatalog/`, `OpenMetadata/`, `SchemaRegistry/`, `Dbt/`, `Caching/`, `Connectors/`) to the real folders or delete it.

**B. Hardening (medium)**
- B1 Native libraries (`ModuleAssemblyLoadContext.LoadUnmanagedDll`): hash is checked on disk, then loaded by path (TOCTOU). Load from a private copy of the verified bytes (`FileMode.CreateNew`, restrictive permissions) or drop native-library support until a module needs it.
- B2 `ModuleLoader.cs` ~337 (`verifyHashes: !isUnsignedDev || manifest.Files.Count > 0`): require a non-empty `Files` list also in unsigned dev mode.
- B3 Anti-rollback: per-module minimum version floor (config or governance DB); keyring with several keys and a revoked-key list instead of the single embedded key.
- B4 `ModuleEndpointRegistrar`: enforce a module-owned route prefix (`/modules/{id}/`), reject `..` and `//`, and make the post-registration validation fail for endpoints without module ownership instead of `continue`.
- B5 `/api/webhooks/openmetadata`: replace the `"bypassed"` magic string by a bool passed to the service; startup guard rejects the bypass flag outside Development (add the test if it exists only as code).
- B6 Break-glass leftovers: delete `X-Break-Glass-Justification` / `X-Justification` parsing and `SecurityPrincipalContext.BreakGlassJustification` (`SecurityContextFactory.cs` ~109-115, 154), the `BreakGlassActivated` audit event and its `FailClosed` entry (`AuditPolicyTable.cs:80`); keep `BreakGlassOperator` only as a forbidden-claim entry with a comment.
- B7 ITSM leftovers: `ITSM_WEBHOOK_SECRET` (`deploy/.env.example`, `deploy/scripts/generate-env.sh`, `DefaultEnvironmentSecretProvider.cs`, `IntegrationGapATests.cs`), `ITSM_UNAVAILABLE` (`ErrorSanitizingFilter.cs`), `Itsm` as default trusted integration (`EgressAllowlist.cs`, `docs/reference/configuration.md`).
- B8 Docs: remove deleted endpoints from `docs/endpoints-and-testing.md` (schema-registry, flight/sql, itsm, servicenow, jira, catalog webhooks) and OpenMetadata/ITSM/dbt strings from `deploy/run-benchmark.sh`; extend the docs-hygiene test (WP-SIMPL-05) to scan for removed route prefixes.
- B9 Lakehouse test coverage: confirm that `LakehousePseudonymizationTests`, `DeltaLakeSecurityAndMetadataTests`, `IcebergRestCatalogSecurityTests`, `LakehouseDataSourceExecutorTests` and `IcebergMetadataReaderTests` moved into `Autheris.Modules.Lakehouse`; restore any that did not (tests lost without a deleted feature).
- B10 Startup test that `Gateway:Itsm`, Backstage, BreakGlass and Garnet keys fail startup (`UnknownGatewayKeyGuard`, `LegacySwitchGuard`).

**C. Cleanup (low)**
- C1 `AggregationMetadata`: remove `NoiseApplied`, `EpsilonConsumed`, `RemainingEpsilon` (always false or null; breaking change allowed). Note in the release notes that `ExposeSuppressedGroupsCount` is now the only gate for suppressed counts.
- C2 `UnknownGatewayKeyGuard` (~75): also flag keys bound to JSON null; add a test that every removed option prefix is covered.
- C3 `DataSourceDialectValidator`: throw on a null connection entry, derive the check from `AdmittedDialects` (remove the duplicated pattern), and confirm that data sources stored in the governance DB pass the same validator.
- C4 OData: keep `SecurityHeadersMiddleware` as the single write-rejection point; drop the duplicates in `ODataEndpoints.Query.cs:39-43` and `:108-111`; `GET /odata/v4/$batch` returns 405 like the flat route.
- C5 `ModuleAssemblyLoadContext`: exact-name matching instead of `StartsWith` for host assemblies, cached host-assembly name set (no `Default.Assemblies` array per `Load`), reject duplicate simple names at image-verify time, document `isCollectible: false`.
- C6 `FileHashVerifier`: reject non-regular files (FIFO, device) before `ReadAllBytes`.
- C7 `AccessDecisionPoint`: single pass instead of `Append(...).ToList()` and `Where(...).ToList()` per request.
- C8 `Directory.Build.props`: remove the widened `NoWarn` CS15xx list or scope it to the generated projects.
### Out of scope
- Findings not reviewed (TLS policy WP-SIMPL-11, connection-secret policy WP-SIMPL-13, `ModuleDirectoryPermissionCheck`, release workflows): a second review pass is item D1 below, not a code change.
- Redesign of module signing beyond B3.

## 4. Current state (verified 2026-11-07 on main 5bbb0576)
- A1: fallback guard condition is `string.IsNullOrWhiteSpace(configuredModulesRoot)` plus a missing directory; no environment check in that branch.
- A2: `if (_policyEnforcementService != null && _policyEnforcementService.HasPolicies(tenant))`; constructor parameter `IPolicyEnforcementService? ... = null`.
- A3: `src/Autheris.Application/` has no `Data/` folder; CODEOWNERS line 16 still names it.
- All other items come from the review reports and are re-verified when the PR starts (step 1 of each PR).

## 5. Implementation steps
1. PR A (release blocker): tests first (Red), then A1, A2, A3. A3 adds the architecture test `CodeownersTests.Every_non_glob_path_exists`.
2. PR B1 (modules): B1-B4 with tests; B3 needs an owner default (see D-50a).
3. PR B2 (cleanup of deleted features): B5-B8, B10.
4. PR B3 (tests): B9 plus a coverage check that no deleted test covered a surviving type (grep type names of deleted test files against `src`).
5. PR C: C1-C8 in one PR, no behaviour change except C1, C2, C3, C4.
6. D1: second review pass over WP-SIMPL-11, 13 and the release workflows; findings are appended to this WP.

## 6. Tests
- Security: `ModuleLoaderSecurityTests.Workspace_fallback_throws_outside_development`, `Workspace_fallback_requires_allow_unsigned`, `Unsigned_dev_manifest_with_empty_files_is_rejected`, `Native_library_is_loaded_from_verified_copy`, `Older_signed_version_below_floor_is_rejected`, `Revoked_key_is_rejected`, `Module_route_outside_prefix_is_rejected`, `Endpoint_without_module_ownership_fails_validation`.
- `AccessDecisionPointTests.Missing_policy_service_denies`, `Missing_ip_resolver_denies_ip_rules`.
- `WebhookSecurityTests.Bypass_flag_outside_development_fails_startup`.
- Architecture: `Every_non_glob_codeowners_path_exists`, `No_break_glass_header_is_read`, `No_removed_endpoint_in_docs`, `No_itsm_identifiers_in_src_deploy_docs`.
- `UnknownGatewayKeyGuardTests.Null_valued_removed_key_is_flagged`, `Every_removed_option_prefix_is_covered`.

## 7. Verification commands
- `dotnet build Autheris.sln`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj --filter "Category=Security"`
- `dotnet test tests/Autheris.Tests.Architecture -c Release`
- `grep -rniE "itsm|break.?glass" src deploy docs --include='*' -l` (only the allowed forbidden-claim entries remain)

## 8. Acceptance criteria
- [ ] A1: production start with `Modules:Enabled` set and no module path fails; no module code loads without a verified manifest outside Development.
- [ ] A2: missing ABAC service or IP resolver denies; no optional dependency on the decision path.
- [ ] A3: every non-glob CODEOWNERS path exists, every subsystem of WP-SIMPL-07 has an owner entry.
- [ ] B-items: each has a failing-then-passing test or a deletion verified by the architecture tests above.
- [ ] B9: the five Lakehouse test classes exist in the module test project or are justified in the PR.
- [ ] C-items merged; `AggregationMetadata` has no DP fields.
- [ ] D1 review done and findings recorded.

## 9. Decisions
- D-50a: anti-rollback floor (B3) is stored in configuration (`Gateway:Modules:MinVersions`), not in the governance DB. Default taken; change only if modules must be gated at runtime.
- D-50b: native libraries (B1) are loaded from a verified private copy, not removed; revisit when no module ships a native library.
- D-50c: the ABAC service is a required dependency (A2), not a runtime deny; a missing registration then fails at startup.

## 10. Estimate
About 5 pd: A 1.5, B1 1.5, B2 0.5, B3 0.5, C 1, D1 0.5 (review only). Net lines about -400.
