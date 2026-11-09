# WP-SIMPL-37 Databricks authentication modes (OBO, service principal M2M, PAT in production) and tier flip (X1-DBX)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-37-databricks-authentication-modes-od-16) | Issues: #515 (program) | Owner decisions: OD-16, OD-17(3), OD-12, OD-36 (Databricks tier flip, binding 2026-11-05) | Security conditions: SEC-SIMPL-90, SEC-SIMPL-92, SEC-SIMPL-95, SEC-SIMPL-96, SEC-SIMPL-97, SEC-SIMPL-98, SEC-X1-14 (tier flip admits a dialect)

> **Added by OD-36 (2026-11-05):** this WP also flips Databricks from `Experimental` to `Production` in `DialectCapabilityTable` (X1-DBX), which admits Databricks sources on WebSQL, Trino `/v1/statement` and MCP, proven by parity tests on those three transports. Sections 1-11 include it (part C, PR C).

## 1. Goal and outcome
Every Databricks data source supports three production modes: `OAuthObo` (per caller), `OAuthM2M` (service principal) and `Pat`. `Pat` is no longer Development-only; instead a per-mode startup validation fails closed in every environment. Shared-identity modes (M2M, PAT) are forced into gateway enforcement, audit attributes the real caller plus a technical identity fingerprint, and per-tenant principals never cross tenants. Databricks is an admitted governed dialect (tier `Production`, OD-36): a Databricks table is queryable through WebSQL, Trino `/v1/statement` and MCP `query_dataset` with the same consent, row filter, masking, tenant predicate and row limit as on PostgreSQL. Measurable: the four `DatabricksAuthModeSecurityTests`, `DatabricksOboTokenTests` and the unit tests in section 6 are green; `grep "allowed in Development only" src/Autheris.*/Databricks src/Autheris.Api/Extensions/DependencyInjection/GatewayStartupValidator.cs` returns nothing; `SqlDialectMapper.AdmittedDialects` contains Databricks and `DatabricksTransportParityTests` is green on WebSQL, Trino and MCP.

## 2. Preconditions and dependencies
- WP-SIMPL-36 item 0 (`fix/security-batch-5` merged to main) must be merged first; rebase on main before starting.
- Runs in the engine lane, parallel to Stage 1. No dependency on WP-SIMPL-34/35.
- Part C (tier flip, PR C) additionally needs: PR A and PR B merged (shared-identity modes forced to `AutherisEnforced`); WP-SIMPL-21 Databricks parity snapshot and contract tests green and `DialectParityTests.Oracle_and_Databricks_security_cells_deny_ddl_dml_system_access_and_unknown_functions` green (SEC-SIMPL-92); WP-SIMPL-38 PR 3 merged (Trino adapter on the shared execution path). If WP-SIMPL-44 PR 2 has merged, the catalog admission branch named in step 9 lives in its declared-metadata admission rules; remove it there.
- Phase 3 note required before PR C (SEC-X1-14: a tier flip admits a dialect; `DialectCapabilityTable.cs` is under security CODEOWNERS).
- Delegated (OBO) sources are decided by `IAccessDecisionPoint` like every source (WP-SIMPL-46, SEC-AZ-05 parity column "Databricks delegated source"); this WP adds no second decision path.

## 3. Scope
### In scope
- `Pat` allowed in all environments; per-mode fail-closed validation (SEC-SIMPL-97).
- Optional `Auth.TokenEndpoint` (M2M only) with host/allowlist check; default stays `https://{Host}/oidc/v1/token`.
- Optional `Auth.Tenants[tenantId]` per-tenant principals, explicit `Auth.FallbackToGlobalPrincipal` (default false).
- Inline secret rejection (secret references only) in every environment, including Development for PAT/M2M/OBO secrets.
- Rotation: on 401 evict token AND re-resolve secret, one retry; cache key includes (data source, tenant, mode, principal).
- Audit: technical identity (mode, client id or PAT fingerprint) next to caller subject/tenant.
- Shared-identity mode with `EnforcementMode` other than `AutherisEnforced` fails startup.
- Tier flip (OD-36, X1-DBX): Databricks `Experimental` -> `Production` in `DialectCapabilityTable`, removal of the explicit Databricks catalog admission deny, admission evidence test, parity tests on WebSQL, Trino and MCP (Spark container, OD-17(3)).
### Out of scope
- Real-workspace nightly job (Q21; only a skipped-when-no-secrets placeholder is NOT added; nightly comes with credentials). Until it exists the Spark container is the parity evidence for the tier flip (OD-17(3)); the `Category=DatabricksLive` step of `sql-compiler-gate-nightly.yml` is unchanged.
- Writes on Databricks sources (WP-SIMPL-45; Trino and MCP reject writes fail-closed, OD-35). Changing the provisional capability values (`MaxBindParameters` 1000, `SupportsLateral=false`, `InDbHmac=false`) or re-enabling the UTF8_BINARY tenant-comparison relaxation.
- New Databricks dialect features, Spark container changes, Unity Catalog delegation changes for OBO.

## 4. Current state (verified on 63cbb893)
- `src/Autheris.Domain/Options/Storage/DatabaseOptions.cs:207` `EnforcementMode {AutherisEnforced, Delegated, DelegatedDefenceInDepth}`; `:220` `DatabricksAuthMode {OAuthM2M=0, Pat=1, OAuthObo=2}`; `DatabricksAuthOptions` (`ClientId`, `ClientSecretRef`, `PatRef`, `Obo`); `DatabricksDataSourceOptions` (`Host`, `WarehouseId`, `Auth`, ...); its XML doc states the token endpoint is not configurable.
- PAT rejected outside Development in three places: `GatewayStartupValidator.ValidateDatabricks` (`src/Autheris.Api/Extensions/DependencyInjection/GatewayStartupValidator.cs:259-263`), `DatabricksOboTokenProvider.AcquireTokenAsync` (`src/Autheris.Infrastructure/Databricks/DatabricksOboTokenProvider.cs:73-78`), `DatabricksStatementExecutionClient` (`.../DatabricksStatementExecutionClient.cs:182-184`).
- Delegated enforcement without `OAuthObo` already rejected (`GatewayStartupValidator.cs:246-256`, test `DatabricksStartupValidationTests.DelegatedEnforcement_WithoutOAuthObo_RejectedAtStartup`).
- M2M endpoint hard-coded `https://{db.Host}/oidc/v1/token` (`DatabricksOboTokenProvider.cs:320`); host policy `DatabricksHostPolicy.IsValidHost`.
- Secrets: `SecretReferenceResolver.Resolve(provider, refOrValue, env, allowPlaintextInDevelopment, logger, desc)` (`src/Autheris.Application/Security/SecretReferenceResolver.cs:17`) allows plaintext in Development; callers pass `true` (`DatabricksOboTokenProvider.cs:80, 213, 316`).
- Token cache key `BuildKey(host, clientId, callerHash)` (`DatabricksTokenCache.cs:50`) - no tenant, no mode.
- 401/403 evicts the token (`DatabricksStatementExecutionClient.cs:246-248`) but does not re-resolve the secret.
- Existing tests: `tests/Autheris.Tests.Unit/Databricks/{DatabricksStartupValidationTests,DatabricksOboSecurityTests,DatabricksStatementExecutionClientTests,DatabricksTokenCacheBoundTests,DatabricksTestSupport}.cs`.
- Tier: `src/TrinoSqlEngine/Ast/Capabilities/DialectCapabilityTable.cs` Databricks entry `Tier: DialectSupportTier.Experimental` (about `:161`); WebSQL admission `SqlDialectMapper.IsExecutable` derives from the tier (`src/Autheris.Application/Sql/SqlDialectMapper.cs:50-72`, doc comment `:54` "stays Experimental until X1-DBX"); `GovernedSqlCompiler.cs:101` rejects non-Production tiers unless `AllowExperimentalDialect`. Explicit catalog deny for Databricks: `src/Autheris.Infrastructure/Persistence/Catalog/GovernedCatalogProvider.cs:284-290`. Tenant comparison `TenantComparisonStyle.CastBinary` always uses the binary template (`TenantPredicateFactory.cs:113-125`). Pinning tests: `tests/Autheris.Tests.Unit/Sql/DialectAdmissionTests.cs` (`Admission_ExactSet` without Databricks, `Admission_IsDerivedFromTheCapabilityTier`, Oracle evidence pattern `Admission_OracleOnlyWithF5Evidence`). Spark container tests exist: `tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs`, `AstCompilerDatabricksSparkDmlExecutionTests.cs`, `SparkTestSupport.cs`. Tier table in `src/TrinoSqlEngine/README.md` (Databricks: Experimental).

## 5. Implementation steps (TDD order)
1. Red: add `tests/Autheris.Tests.Unit/Databricks/DatabricksAuthModeSecurityTests.cs` and the unit tests of section 6; change `PatAuth_RejectedOutsideDevelopment_AtStartup` into `PatAuth_WithSecretReference_AllowedInProduction_AtStartup`.
2. Options (`DatabaseOptions.cs`): add to `DatabricksAuthOptions`: `string? TokenEndpoint`, `IReadOnlyDictionary<string, DatabricksTenantPrincipalOptions> Tenants` (default empty, ordinal), `bool FallbackToGlobalPrincipal` (default false). New `sealed class DatabricksTenantPrincipalOptions { string? ClientId; string? ClientSecretRef; string? PatRef; }`. Add `IReadOnlyList<string> AllowedAccountTokenHosts` to `DatabricksOptions` (`src/Autheris.Domain/Options/DatabricksOptions.cs`, default empty). Update XML docs (remove "not configurable"/"only mode outside Development"). Register new keys in `GatewayConfigurationSchema`.
3. Validation (`GatewayStartupValidator.ValidateDatabricks`): remove the Development-only PAT branch; add, for all environments: (a) secret fields (`PatRef`, `ClientSecretRef`, `Obo.ClientSecretRef`, tenant entries) must be secret references - new helper `SecretReferenceResolver.IsReference(string)` (true for the reference syntax the Key Vault provider accepts, e.g. `kv://`/`vault:` - reuse the existing prefix check in the provider; do not invent a new syntax); (b) `TokenEndpoint` only with `OAuthM2M`, absolute `https`, no userinfo/query/fragment, host equals `db.Host` or is in `AllowedAccountTokenHosts` (ordinal-ignore-case exact match); (c) `OAuthObo` requires non-empty `Obo.Scopes` (token-exchange audience) besides the existing checks; (d) mode `OAuthM2M`/`Pat` with `EnforcementMode != AutherisEnforced` fails (message cites SEC-SIMPL-96); (e) tenant entries: key non-empty, entry for `Pat` mode needs `PatRef`, for `OAuthM2M` `ClientId`+`ClientSecretRef`; tenants with `OAuthObo` fail. Messages never contain secret values.
4. Provider (`DatabricksOboTokenProvider`): delete lines 73-78 Development check; PAT path resolves via new `ResolvePrincipal(db, tenantId)` returning `(clientId|null, secretRef, principalId)`; tenant lookup from `DelegationContext` tenant; no entry and `FallbackToGlobalPrincipal=false` while `Tenants` is non-empty -> `DatabricksProviderException(Configuration, "No Databricks principal configured for the tenant.")`. M2M uses `auth.TokenEndpoint ?? $"https://{db.Host}/oidc/v1/token"`. Call `SecretReferenceResolver.Resolve(..., allowPlaintextInDevelopment: false, ...)` at all three call sites.
5. Cache (`DatabricksTokenCache.BuildKey`): key = `(dataSourceName, tenantId ?? "-", mode, principalId, callerHash)`; `Evict` per (data source, tenant). Adjust callers.
6. Rotation (`DatabricksStatementExecutionClient` around line 246): on 401 evict token, mark secret stale (`IDatabricksCredentialProvider.InvalidateSecret(db, tenant)`; PAT/M2M re-read secret on next acquire), retry exactly once; second 401 -> `AuthFailed`. Delete lines 182-184.
7. Audit: where the Databricks query audit entry is built (follow `DatabricksStatementExecutionClient` -> `IAuditWriter` caller; if no Databricks-specific audit exists, add fields to the existing query audit details) add `auth.mode`, `auth.clientId` (M2M) or `auth.tokenFingerprint` = first 16 hex of SHA-256 of the PAT (PAT), plus caller subject and tenant already present. Never the token.
8. Green, then refactor; run full Databricks unit filter.
9. Tier flip (PR C, OD-36). Red first: the tests of section 6 part C.
   - `DialectCapabilityTable.cs`: Databricks `Tier: DialectSupportTier.Production`; no other capability value changes.
   - `GovernedCatalogProvider`: delete the explicit Databricks deny branch (`:284-290` or its successor after WP-SIMPL-44 PR 2); admission then follows `SqlDialectMapper.IsExecutable` only, so lowering the tier again denies Databricks tables (fail closed).
   - `TenantPredicateFactory`: no change; `DatabricksUtf8BinaryRelaxation` stays disabled, the binary comparison is always emitted.
   - Update the doc comment `SqlDialectMapper.cs:54`, the comment at `DialectAdmissionTests.cs:41` and the tier table in `src/TrinoSqlEngine/README.md` (Databricks: Production).
   - `DialectAdmissionTests`: `Admission_ExactSet` adds `DatabaseDialect.Databricks` to the admitted set pinned at merge time; `Admission_IsDerivedFromTheCapabilityTier` asserts Databricks admitted by default and not admitted with a `TierOverride` to `Experimental`; new `Admission_DatabricksOnlyWithParityEvidence` (pattern of the Oracle evidence test): Databricks is admitted only while `tests/Autheris.Tests.Integration/DatabricksTransportParityTests.cs` exists and contains the required test names of section 6.
10. Parity on WebSQL, Trino and MCP (PR C). Add `tests/Autheris.Tests.Integration/DatabricksTransportParityTests.cs` on the Spark container (`SparkTestSupport`), gateway in `AutherisEnforced` mode with a PAT or M2M fake identity: one seeded table with a tenant column under a case-insensitive collation, one masked column, one denied column and a row filter; the same caller queries it through WebSQL, Trino `/v1/statement` and MCP `query_dataset`. Runs nightly with the container suite and on `workflow_dispatch`; PR CI runs the WP-SIMPL-21 contract tests.

## 6. Tests (tests/Autheris.Tests.Unit/Databricks)
- `DatabricksAuthModeSecurityTests` [Trait("Category","Security")]:
  - `Inline_secret_fails_startup_and_tokens_never_logged_or_audited` (SEC-SIMPL-95): inline PatRef/ClientSecretRef in Production and Development fail validation; with a fake logger and audit sink, acquire + 401 + error path, assert neither token nor secret appears in log messages, exception messages, ProblemDetails or audit details; fingerprint present.
  - `Shared_identity_modes_apply_gateway_rls_masking_and_attribute_caller` (SEC-SIMPL-96): `[Theory] OAuthM2M, Pat` with `Delegated`/`DelegatedDefenceInDepth` fails startup; with `AutherisEnforced` the compiled SQL sent to the fake contains the RLS predicate and mask expression; audit has caller subject+tenant and mode.
  - `Startup_fails_closed_per_mode_on_missing_secret_http_endpoint_or_mismatch` (SEC-SIMPL-97): theory rows: missing PatRef, missing ClientSecretRef, `http://` TokenEndpoint, TokenEndpoint on foreign host, TokenEndpoint with OBO, OBO without Scopes, each in Production and Development.
  - `Per_tenant_principal_never_used_for_another_tenant` (SEC-SIMPL-98): tenants A/B with own PATs; caller B gets B token; caller C without entry fails when fallback false, gets global when true; cache keys differ per tenant.
- `DatabricksStartupValidationTests`: replace `PatAuth_RejectedOutsideDevelopment_AtStartup` with `PatAuth_WithSecretReference_AllowedInProduction_AtStartup`; add `TokenEndpoint_OnAllowlistedAccountHost_Passes`.
- `DatabricksStatementExecutionClientTests`: `Unauthorized_ReResolvesSecret_AndRetriesOnce`, `SecondUnauthorized_FailsWithAuthFailed`; `Pat_AcceptedInProduction`.
- `DatabricksTokenCacheBoundTests`: `CacheKey_IncludesTenantModeAndPrincipal`.
- `DatabricksOboTokenTests.Token_scoped_per_subject_audience_not_logged_expires_and_exchange_failure_fails_closed` [Security] (SEC-SIMPL-90; sec plan 11.6 row 37).
- Part C, `tests/Autheris.Tests.Unit/Sql/DialectAdmissionTests` [Security]: `Admission_ExactSet` (with Databricks), `Admission_IsDerivedFromTheCapabilityTier` (inverted for Databricks), `Admission_DatabricksOnlyWithParityEvidence`; `Sql/GovernedCatalogProviderTests.Databricks_admission_follows_tier_only` [Security] (Production admits, `Experimental` override denies).
- Part C, `tests/Autheris.Tests.Integration/DatabricksTransportParityTests` [Security] (theory over WebSQL, Trino, MCP): `SameCaller_SameRowsMasksAndColumnDenials_OnAllTransports`; `Tenant_isolation_holds_under_case_insensitive_collation`; `ConsentDenied_SameDenialShape_OnAllTransports`; `RowLimit_AppliedPerTransportChannel`; `Write_statement_rejected_on_Trino_and_Mcp` (OD-35). Transport gate: `AllowedTransports` without `Trino` denies the Trino query and allows WebSQL.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "FullyQualifiedName~Databricks"`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "Category=Security"`
- `git grep -n "allowed in Development only" -- src` -> no Databricks hit.
- `git grep -n "allowPlaintextInDevelopment: true\|, true, _logger" -- src/Autheris.Infrastructure/Databricks` -> empty.
- Part C: `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "FullyQualifiedName~DialectAdmission|FullyQualifiedName~GovernedCatalogProvider"`; `dotnet test tests/Autheris.Tests.Integration -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~DatabricksTransportParity|FullyQualifiedName~DatabricksSpark"` (container runtime required, podman host override); `git grep -n "X1-DBX" -- src` -> only historical references, no deny branch.

## 8. Acceptance criteria
- [x] PAT and M2M start in Production with secret references; inline secrets fail in every environment.
- [x] Explicit TokenEndpoint accepted only https and on workspace host or allowlisted account host.
- [x] M2M/PAT with non-`AutherisEnforced` mode fails startup.
- [x] Per-tenant principals isolated; fallback only with `FallbackToGlobalPrincipal=true`.
- [x] 401 re-resolves the secret, one retry; no restart needed after rotation.
- [x] Four `DatabricksAuthModeSecurityTests` and `DatabricksOboTokenTests` green; existing Databricks tests green; new config keys in `GatewayConfigurationSchema`.
- [x] Databricks tier is `Production`; `Admission_ExactSet` pins the admitted set including Databricks; the explicit catalog deny is gone and admission follows the tier only.
- [x] `DatabricksTransportParityTests` green on WebSQL, Trino and MCP (Spark container), including tenant isolation under a case-insensitive collation and write rejection on Trino and MCP; `DialectParityTests` Oracle/Databricks security cells green.
- [x] Phase 3 note for the tier flip recorded before PR C merges (SEC-X1-14).

## 9. PR slicing
1. PR A (~+250 src): options, schema keys, startup validation, PAT unblocking, inline-secret rejection; tests for SEC-SIMPL-95 (startup part), -96 (startup part), -97.
2. PR B (~+350 src): per-tenant principals, cache key, TokenEndpoint use, rotation retry, audit fingerprint; remaining security tests.
3. PR C (~+20 / -15 src, ~+600 tests; after PR B, Phase 3 note and the preconditions of section 2): tier flip, catalog admission branch removal, admission evidence test, `DatabricksTransportParityTests`. Title `feat(databricks): admit Databricks on WebSQL, Trino and MCP (X1-DBX, OD-36)`.

## 10. Risks, rollback, and things the developer must NOT do
- Do not log, throw or audit token/secret values or secret references; fingerprints only.
- Do not let a tenant without an entry reuse another tenant's principal; do not default `FallbackToGlobalPrincipal` to true.
- Do not loosen `DatabricksHostPolicy`; do not accept http even in Development.
- Do not retry more than once on 401 (lockout/amplification).
- Do not change other Databricks capability values in PR C or re-enable the UTF8_BINARY relaxation; do not add a bypass for the parity evidence test.
- Rollback: revert PR C (Databricks back to `Experimental`, not admitted), then PR B, then PR A; PAT returns to Development-only.

## 11. Estimate
About +620 src / -45 src, +1,400 tests; 6 person-days (PR A and PR B 4 pd, PR C 2 pd including the Spark parity suite).

## Decisions taken in this spec
- "Token-exchange audience" for OBO maps to the existing `DatabricksOboOptions.Scopes` (non-empty required).
- Account-host allowlist lives in `DatabricksOptions.AllowedAccountTokenHosts` (global, not per data source).
- Inline secrets are rejected also in Development (OD-16 says "secrets only via secret references").
- PAT fingerprint = first 16 hex chars of SHA-256.
- Tier flip (OD-36): the evidence for admission is the Spark container parity suite on WebSQL, Trino and MCP until the real-workspace nightly exists (OD-17(3)); the provisional capability values stay as they are. Admission stays derived from the tier, so a revert of the single tier line removes Databricks from every transport.

## 12. Phase 3 security review note: tier flip (SEC-X1-14, OD-36)
**Date:** 2026-11-09  
**Reviewer:** Security Expert (Phase 3 review for PR C)  
**Verdict:** APPROVED for dialect admission to `Production` tier under the following conditions:
1. `DialectCapabilityTable.cs` Databricks tier is flipped to `DialectSupportTier.Production`. No other capability flags are loosened (e.g. `InDbHmac` stays `false` degrading to Redact, `SupportsLateral` stays `false`, `ReportsInsertRowCount` stays `false`).
2. Catalog admission follows the capability tier only: the explicit deny branch in `GovernedCatalogProvider.cs` is removed.
3. Admission evidence test `Admission_DatabricksOnlyWithParityEvidence` is added to `DialectAdmissionTests.cs`, pinning that `DatabricksTransportParityTests.cs` exists and defines all required parity tests across WebSQL, Trino, and MCP transports.
4. Transport parity tests prove fail-closed security: tenant isolation holds under case-insensitive collation (`CastBinary`), row limits apply per transport, consent denials match across transports, and write statements are rejected on Trino and MCP.

