# WP-SIMPL-50 Remediation of the v1.7.1..HEAD code and security reviews
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-50-remediation-of-the-v171head-code-review) | Sources: (1) code review of `v1.7.1..HEAD` (30 commits, 2026-11-07), three read-only sampled reviews; (2) security review of `v1.7.1..5bbb0576` (2026-11-07), four read-only slices (module host and startup, authorization and identity, SQL and data plane, extensions/egress/secrets); no build or test run | Security conditions: SEC-AZ-04 (fail closed), SEC-PROD-01 (no new anonymous routes); module items follow the WP-SIMPL-25 trust conditions

## 1. Goal and outcome
Close the findings of both reviews so that the Stage 3 deletions, moves and the module host leave no fail-open path, no forgeable audit record, no dead ownership entry, no leftover secret plumbing and no lost security test coverage. Measurable: every acceptance item below is checked by an architecture or security test that fails on regression.

## 2. Preconditions and dependencies
- Item groups A, B and C are independent PRs. Group A is a release blocker; B and C may follow.
- Items A1, A4 and A5 are exploitable under the stated preconditions. This public plan states only the fail-closed rule for each; reproduction details are not recorded here or in any PR text.
- WP-SIMPL-25 and WP-SIMPL-27 are merged (the module items change their code).

## 3. Scope
### In scope (findings verified against the code are marked [V]; [S] = security review 2026-11-07)
**A. Release blockers**
- A1 [V][S] `ModuleLoader.TryLoadWorkspaceModules` and `FindWorkspaceModuleDll` (`ModuleLoader.cs` ~236-242, ~705-708, ~738-761): the workspace fallback loads module code without signature, hash, permission or environment checks, searches ancestor directories and can load into `AssemblyLoadContext.Default`. Rule: allowed only when `IsDevelopment() && AllowUnsignedInDevelopment`; otherwise throw. No parent-directory walk and no base-directory candidate; the dev path comes from explicit configuration. Module assemblies never load into `Default`; the entry assembly always comes from the module load context.
- A2 [V] `AccessDecisionPoint.cs:173`: ABAC is skipped when the optional `IPolicyEnforcementService` is null (reachable via the Lakehouse fallback construction `IcebergRestCatalogFederationService.cs:59`; same pattern in `TableAccessPolicy.cs` ~396/496). Make the dependency required (or deny when null). An unknown client IP (`IPAddress.None`, also substituted when no resolver exists, line ~195) must deny for every IP-based rule, including CIDR deny-list rules, which today never match and so fail open.
- A3 [V] `.github/CODEOWNERS`: `src/Autheris.Application/Data/` no longer exists (masking subsystem unowned). Remap every dead path (`Data/`, `Auditing/`, `SqlEndpoints/`, `DataCatalog/`, `OpenMetadata/`, `SchemaRegistry/`, `Dbt/`, `Caching/`, `Connectors/`) to the real folders or delete it.
- A4 [V][S] `ModuleEndpointRegistrar.cs` ~218-220: module audit `DetailsJson` is built by string interpolation from request data. Rule: audit details are serialized only with `Utf8JsonWriter`/`JsonSerializer`; request-derived values are length-capped. Architecture test: no `DetailsJson = $"` interpolation anywhere in `src` and `modules`.
- A5 [V][S] `HitLStepUpApprovalService.cs` ~201-216: a cluster approval broadcast is accepted when its HMAC verifies, without binding it to the ticket it is applied to. Rule: the broadcast `ApprovalId` must equal the subscribed ticket id (ordinal), and the signed payload carries an issued-at time that is checked against the ticket TTL; otherwise the broadcast is dropped and logged.

**B. Hardening (medium)**
- B1 Native libraries (`ModuleAssemblyLoadContext.LoadUnmanagedDll`, ~163-180, confirmed [S]): hash is checked on disk, then loaded by path (TOCTOU). Load from a private copy of the verified bytes (`FileMode.CreateNew`, restrictive permissions) or drop native-library support until a module needs it.
- B2 `ModuleLoader.cs` ~337 (`verifyHashes: !isUnsignedDev || manifest.Files.Count > 0`): require a non-empty `Files` list also in unsigned dev mode.
- B3 Anti-rollback: per-module minimum version floor (config or governance DB); keyring with several keys and a revoked-key list instead of the single embedded key. [S] The host version (`ModuleLoader.DefaultHostVersion = "1.2.0"`, line ~95) is derived from the assembly informational version instead of a constant.
- B4 `ModuleEndpointRegistrar`: enforce a module-owned route prefix (`/modules/{id}/`; Lakehouse maps a bare `/v1/{prefix}/...` today), reject `..` and `//`, and make the post-registration validation fail for endpoints without module ownership instead of `continue`.
- B5 `/api/webhooks/openmetadata`: replace the `"bypassed"` magic string by a bool passed to the service (the service already decides on the option); startup guard rejects the bypass flag outside Development (add the test if it exists only as code).
- B6 Break-glass leftovers: delete `X-Break-Glass-Justification` / `X-Justification` parsing and `SecurityPrincipalContext.BreakGlassJustification` (`SecurityContextFactory.cs` ~20-21, ~109-118, 154; caller-controlled, unbounded, unconsumed), `X-Break-Glass` in `GatewayExtensibilityMiddleware.cs:27`, the `BreakGlassActivated` audit event and its `FailClosed` entry (`AuditPolicyTable.cs:80`); keep `BreakGlassOperator` only as a forbidden-claim entry with a comment.
- B7 ITSM and other deleted-feature leftovers: `ITSM_WEBHOOK_SECRET` and `CATALOG_WEBHOOK_SECRET` (`deploy/.env.example`, `deploy/scripts/generate-env.sh`, `IntegrationGapATests.cs`); the `itsm:*` alias branch in `DefaultEnvironmentSecretProvider.cs` (its prefix match also contradicts the exact-match rule R-DEP-1); `ITSM_UNAVAILABLE` (`ErrorSanitizingFilter.cs` ~87, ~141); `Itsm` and `Shadowing` in `EgressIntegrations`, `AllowlistCapable` and `DefaultTrustedIntegrations` (`EgressAllowlist.cs` ~20-32, `OutboundEgressOptions.cs` ~21, ~35), `docs/reference/configuration.md`.
- B8 Docs: remove deleted endpoints from `docs/endpoints-and-testing.md` (schema-registry, flight/sql, itsm, servicenow, jira, catalog webhooks) and OpenMetadata/ITSM/dbt strings from `deploy/run-benchmark.sh`; extend the docs-hygiene test (WP-SIMPL-05) to scan for removed route prefixes.
- B9 Lakehouse test coverage: confirm that `LakehousePseudonymizationTests`, `DeltaLakeSecurityAndMetadataTests`, `IcebergRestCatalogSecurityTests`, `LakehouseDataSourceExecutorTests` and `IcebergMetadataReaderTests` moved into `Autheris.Modules.Lakehouse`; restore any that did not (tests lost without a deleted feature).
- B10 Startup test that `Gateway:Itsm`, Backstage, BreakGlass and Garnet keys fail startup (`UnknownGatewayKeyGuard`, `LegacySwitchGuard`). [S] `LegacySwitchGuard.cs` ~28-31, ~49 must not map removed `Itsm:*` switches to global `Insecure:*` switches; the message is "section removed, delete it" without a migration target.
- B11 [S] Module audit filter (`ModuleEndpointRegistrar.cs` ~203-222): the decision is derived from the result/status code instead of a constant `ALLOW`, and the audit record is written in `try/finally` so a throwing handler is still audited.
- B12 [S] Module service guard (`ModuleLoader.cs` ~103-125, ~568-627): replace the type/name denylist by an allowlist wrapper around the module `IServiceCollection`; reject `IStartupFilter`, `IHostedService` and `IConfigureOptions<>`/`IPostConfigureOptions<>` for authentication and authorization option types; endpoints without `ModuleEndpointMetadata` fail `VerifyModuleEndpoints` (with B4).
- B13 [S] Module governance access (`ModuleHostContext`, `GovernanceConnectionFactory.cs` ~63-105, `GatewayServiceCollectionExtensions.cs` ~113-117): modules get no raw governance connection; provide narrow module-scoped repositories (or none, if Lakehouse needs none; verify at PR start). Pass `IGatewayStatePaths` instead of `statePaths: null`.
- B14 [S] Release pipeline (`release.yml`, `docker-publish.yml`, Dockerfiles): add a module packaging and signing job (offline or HSM key) and an end-to-end load test of the signed module against the published image, so the signed path is the shipped path.
- B15 [S] `AccessDecisionPoint.cs` ~210, ~230-234: the computed ABAC row filter is AND-combined into `AccessDecision.Allowed(...)` for every operation (it is dropped today for `Read`, `Write`, `Explain`, `Subscribe`); column masking rules apply to every operation, not only `SchemaRead`/`VendCredentials`. Correct the ADR-020 wording on "single decision point" (main read paths still call `TableAccessPolicy.DecideAsync`) or link the work package that moves them (WP-SIMPL-46).
- B16 [S] Aggregation residual risk after the DP removal (OD-11): record differencing across overlapping filters as accepted residual risk in OD-11, set `ExposeSuppressedGroupsCount` default to `false`, and add an audit field for the filter shape so overlapping queries can be detected after the fact (see D-50d).

**C. Cleanup (low)**
- C1 `AggregationMetadata`: remove `NoiseApplied`, `EpsilonConsumed`, `RemainingEpsilon` (always false or null; breaking change allowed) and the `noise_applied` audit field; delete `NoisyCacheKey.cs` (no callers) and `PRIVACY_BUDGET_EXHAUSTED` (`ApiErrorCodes.cs:66`).
- C2 `UnknownGatewayKeyGuard` (~75/97): also flag keys bound to JSON null; add a test that every removed option prefix is covered.
- C3 `DataSourceDialectValidator`: throw on a null connection entry, derive the check from `AdmittedDialects` (remove the duplicated pattern), and confirm that data sources stored in the governance DB pass the same validator.
- C4 OData: keep `SecurityHeadersMiddleware` as the single write-rejection point; drop the duplicates in `ODataEndpoints.Query.cs:39-43` and `:108-111`; `GET /odata/v4/$batch` returns 405 like the flat route.
- C5 `ModuleAssemblyLoadContext`: exact-name matching instead of `StartsWith` for host assemblies, cached host-assembly name set (no `Default.Assemblies` array per `Load`), reject duplicate simple names at image-verify time, document `isCollectible: false`.
- C6 `FileHashVerifier`: reject non-regular files (FIFO, device) before `ReadAllBytes`.
- C7 `AccessDecisionPoint`: single pass instead of `Append(...).ToList()` and `Where(...).ToList()` per request.
- C8 `Directory.Build.props`: remove the widened `NoWarn` list (XML-doc warnings CS0419, CS15xx, CS17xx only; not security relevant) or scope it to the generated projects.
- C9 [S] `AccessDecisionPoint.cs:112`: deny when the principal has no SID claim instead of falling back to `Identity.Name`.
- C10 [S] `IcebergRestCatalogFederationService.cs` ~252: `VendCredentialAsync` writes an `Iceberg.VendCredential` DENY audit record before rethrowing the decision exception (as `LoadTable` does).
- C11 [S] Config-sync webhook (`VirtualFilterEndpoints.Seeding.cs` ~143-184): replay protection does not rely on the unsigned delivery header; dedupe on a body hash or require a signed timestamp within a window.
- C12 [S] Module config sections (`ModuleLoader.cs` ~127-136, ~526-566): a module may claim only `Gateway:<ModuleSection>` from a host-side section-to-module map; claimed sections do not silence `UnknownGatewayKeyGuard` outside that section.
- C13 [S] Module load logging (`GatewayServiceCollectionExtensions.cs` ~117): `ModuleLoader` gets a real (bootstrap) logger; each loaded module emits a startup event with id, version, key id and signed/unsigned.
- C14 [S] `ModuleGraphQLRegistrar` (~59-64, ~115-125): match the HotChocolate `AuthorizeAttribute` type exactly, include inherited members, and wire `ModuleLoadResult.ConfigureGraphQL` or delete the unused API.
- C15 [S] `ConnectionSecretPolicy.cs:27`: do not attach the provider parse exception as inner exception (it can carry connection-string fragments); `SqlConnectionFactory.cs:51`: derive `_productionSecrets` from the environment, not from the Oracle TCPS flag; `ConnectionTlsPolicy.ValidateGovernanceConnectionTls` (line ~70, no caller): wire into startup or delete.
- C16 [S] `SqliteGovernanceConnectionProvider` back to `internal` (with `InternalsVisibleTo` where needed).
- C17 [S] Remove the `ArrowFlight` transport flag (`IGovernedSqlExecutionService.cs` ~33-41, `DataSourceEnums.cs:33`, `TransportFlagNames.cs:14`, `IAccessDecisionPoint.cs:18`) or reject it at startup; FlightSql was deleted.
- C18 [S] Restore control ids (SEC-/REQ-) in security-critical comments removed during Stage 3 (e.g. `GatewayExecutionService.cs` ~64, ~189, the dbt and OpenMetadata ingestion services), or record the control-to-test mapping in the security tests.
- C19 [S] Catalog egress: give the OpenAPI ingestion client its own integration name that is not trusted by default (today it inherits the `Catalog` private-network allowlist).
### Out of scope
- Redesign of module signing beyond B3 and B14.
- Re-audit of code unchanged since v1.7.1 (AST rewrite, row-filter and masking rewrite, OData filter translation, persistence SQL building, GraphQL field-level authorization, Governance/Audit internals); a full audit of those is a separate request.

## 4. Current state (verified 2026-11-07 on main 5bbb0576)
- A1: fallback guard condition is `string.IsNullOrWhiteSpace(configuredModulesRoot)` plus a missing directory; no environment check; `FindWorkspaceModuleDll` walks `current.Parent` up to the root and falls back to `AppContext.BaseDirectory`; a base-directory copy is loaded with `AssemblyLoadContext.Default.LoadFromAssemblyPath`.
- A2: `if (_policyEnforcementService != null && _policyEnforcementService.HasPolicies(tenant))`; constructor parameter `IPolicyEnforcementService? ... = null`.
- A3: `src/Autheris.Application/` has no `Data/` folder; CODEOWNERS line 16 still names it.
- A4: `DetailsJson = $"{{\"module\":\"{moduleId}\",\"path\":\"{httpContext.Request.Path.Value}\"}}"`; `Decision = "ALLOW"` constant (B11).
- A5: the subscriber checks `VerifyBroadcastSignature` only and then applies the broadcast to the pending ticket.
- B15: `AccessDecision.Allowed(tableId, decision.ColumnAccess, request.Transport, decision.CombinedRowFilterSql)`; `abacRowFilter` is used only in the `SchemaRead`/`VendCredentials` restriction check.
- Security review: release workflows clean (no `pull_request_target`, actions pinned to SHA, default permissions `{}`); manifest parser, canonical form and ECDSA verification sound; tenant header only for ClusterAdmin; Development-only flags refused outside Development; no bypass left by the Stage 3 deletions; SSRF handlers unchanged.
- All other items come from the review reports and are re-verified when the PR starts (step 1 of each PR).

## 5. Implementation steps
1. PR A (release blocker): tests first (Red), then A1-A5. A3 adds the architecture test `CodeownersTests.Every_non_glob_path_exists`.
2. PR B1 (module host): B1-B4, B11-B13 with tests; B3 needs an owner default (see D-50a).
3. PR B2 (authorization): B15, C9, C10 with tests.
4. PR B3 (cleanup of deleted features): B5-B8, B10, C17.
5. PR B4 (tests): B9 plus a coverage check that no deleted test covered a surviving type (grep type names of deleted test files against `src`).
6. PR B5 (release pipeline): B14.
7. PR B6 (aggregation): B16 and C1; OD-11 note in the parent plan.
8. PR C: C2-C8, C11-C16, C18, C19 in one PR (split if it exceeds the 400-line limit).
9. D1 is done: the security review of 2026-11-07 covered TLS policy (WP-SIMPL-11), connection-secret policy (WP-SIMPL-13) and the release workflows; its findings are B13-B16 and C15.

## 6. Tests
- Security (`[Trait("Category","Security")]`):
  - Module host: `ModuleLoaderSecurityTests.Workspace_fallback_throws_outside_development`, `Workspace_fallback_requires_allow_unsigned`, `Workspace_fallback_does_not_walk_parent_directories`, `Module_assembly_never_loads_into_default_context`, `Unsigned_dev_manifest_with_empty_files_is_rejected`, `Native_library_is_loaded_from_verified_copy`, `Older_signed_version_below_floor_is_rejected`, `Revoked_key_is_rejected`, `Module_route_outside_prefix_is_rejected`, `Endpoint_without_module_ownership_fails_validation`, `Module_registering_startup_filter_is_rejected`, `Module_configuring_authorization_options_is_rejected`, `Module_claiming_protected_config_section_is_rejected`.
  - Module audit: `ModuleAuditTests.Request_path_is_json_escaped`, `Forbidden_result_is_audited_as_deny`, `Throwing_handler_is_audited`.
  - Authorization: `AccessDecisionPointTests.Missing_policy_service_denies`, `Unknown_client_ip_denies_ip_rules`, `Unknown_client_ip_matches_cidr_deny_rule`, `Read_propagates_abac_row_filter`, `No_sid_claim_denies`; `IcebergTests.VendCredential_denied_writes_deny_audit`.
  - HitL: `HitLStepUpApprovalTests.Broadcast_with_mismatched_approval_id_is_rejected`, `Expired_broadcast_is_rejected`.
  - Webhooks: `WebhookSecurityTests.Bypass_flag_outside_development_fails_startup`, `ConfigSyncWebhook_replayed_body_with_new_delivery_id_is_not_retriggered`.
  - Startup: `LegacySwitchGuardTests.Removed_itsm_switch_has_no_migration_target`; `ConnectionSecretPolicyTests.Parse_failure_has_no_inner_exception`; `DataSourceDialectValidatorTests.Null_connection_entry_fails_startup`.
  - Aggregation: `AggregationTests.Suppressed_rows_count_not_exposed_by_default`.
- Architecture: `Every_non_glob_codeowners_path_exists`, `No_break_glass_header_is_read`, `No_removed_endpoint_in_docs`, `No_itsm_identifiers_in_src_deploy_docs`, `No_interpolated_audit_details_json`, `SqliteGovernanceConnectionProvider_is_internal`.
- `UnknownGatewayKeyGuardTests.Null_valued_removed_key_is_flagged`, `Every_removed_option_prefix_is_covered`.
- Release: signed-module end-to-end load test against the published image (B14).

## 7. Verification commands
- `dotnet build Autheris.sln`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj --filter "Category=Security"`
- `dotnet test tests/Autheris.Tests.Architecture -c Release`
- `grep -rniE "itsm|break.?glass" src deploy docs --include='*' -l` (only the allowed forbidden-claim entries remain)
- `grep -rn 'DetailsJson = \$"' src modules` (no match)

## 8. Acceptance criteria
- [ ] A1: production start with `Modules:Enabled` set and no module path fails; no module code loads without a verified manifest outside Development; no module assembly loads into the default context.
- [ ] A2: missing ABAC service or IP resolver denies; an unknown client IP denies every IP-based rule; no optional dependency on the decision path.
- [ ] A3: every non-glob CODEOWNERS path exists, every subsystem of WP-SIMPL-07 has an owner entry.
- [ ] A4: every audit `DetailsJson` is produced by a JSON serializer; architecture test green.
- [ ] A5: a validly signed broadcast for another or an expired ticket does not change ticket state.
- [ ] B-items: each has a failing-then-passing test or a deletion verified by the architecture tests above.
- [ ] B9: the five Lakehouse test classes exist in the module test project or are justified in the PR.
- [ ] B14: the release produces a signed module and the published image loads it in the end-to-end test.
- [ ] B16: OD-11 residual-risk note in the parent plan; `ExposeSuppressedGroupsCount` defaults to `false`.
- [ ] C-items merged; `AggregationMetadata` has no DP fields.

## 9. Decisions
- D-50a: anti-rollback floor (B3) is stored in configuration (`Gateway:Modules:MinVersions`), not in the governance DB. Default taken; change only if modules must be gated at runtime.
- D-50b: native libraries (B1) are loaded from a verified private copy, not removed; revisit when no module ships a native library.
- D-50c: the ABAC service is a required dependency (A2), not a runtime deny; a missing registration then fails at startup.
- D-50d: aggregation differencing after the DP removal is accepted residual risk (OD-11) mitigated by suppression, `ExposeSuppressedGroupsCount=false` by default and audit of the filter shape; no query-overlap blocking. Default taken; revisit if aggregation is exposed to untrusted tenants.
- D-50e: the ABAC row filter is propagated for every operation (B15) instead of denying non-catalog operations at the decision point; this keeps WP-SIMPL-46 free to move read paths onto it.
- D-50f: the module service guard becomes an allowlist (B12); a module that needs another extension point gets it added explicitly to the host allowlist in review.
- D-50g: modules get no raw governance connection (B13); Lakehouse needs are served by a narrow repository interface decided at PR start.

## 10. Estimate
About 9.5 pd: A 2.5, B1 (module host) 2.5, B2 (authorization) 1, B3 0.5, B4 0.5, B5 (release) 1, B6 (aggregation) 0.5, C 1. Net lines about -250 (deletions minus the new module guards, signing job and tests).
