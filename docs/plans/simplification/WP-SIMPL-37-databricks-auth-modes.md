# WP-SIMPL-37 Databricks authentication modes (OBO, service principal M2M, PAT in production)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-37-databricks-authentication-modes-od-16) | Issues: #515 (program) | Owner decisions: OD-16, OD-17(3) | Security conditions: SEC-SIMPL-95, SEC-SIMPL-96, SEC-SIMPL-97, SEC-SIMPL-98

## 1. Goal and outcome
Every Databricks data source supports three production modes: `OAuthObo` (per caller), `OAuthM2M` (service principal) and `Pat`. `Pat` is no longer Development-only; instead a per-mode startup validation fails closed in every environment. Shared-identity modes (M2M, PAT) are forced into gateway enforcement, audit attributes the real caller plus a technical identity fingerprint, and per-tenant principals never cross tenants. Measurable: the four `DatabricksAuthModeSecurityTests` and the unit tests in section 6 are green; `grep "allowed in Development only" src/Autheris.*/Databricks src/Autheris.Api/Extensions/DependencyInjection/GatewayStartupValidator.cs` returns nothing.

## 2. Preconditions and dependencies
- WP-SIMPL-36 item 0 (`fix/security-batch-5` merged to main) must be merged first; rebase on main before starting.
- Runs in the engine lane, parallel to Stage 1. No dependency on WP-SIMPL-34/35.

## 3. Scope
### In scope
- `Pat` allowed in all environments; per-mode fail-closed validation (SEC-SIMPL-97).
- Optional `Auth.TokenEndpoint` (M2M only) with host/allowlist check; default stays `https://{Host}/oidc/v1/token`.
- Optional `Auth.Tenants[tenantId]` per-tenant principals, explicit `Auth.FallbackToGlobalPrincipal` (default false).
- Inline secret rejection (secret references only) in every environment, including Development for PAT/M2M/OBO secrets.
- Rotation: on 401 evict token AND re-resolve secret, one retry; cache key includes (data source, tenant, mode, principal).
- Audit: technical identity (mode, client id or PAT fingerprint) next to caller subject/tenant.
- Shared-identity mode with `EnforcementMode` other than `AutherisEnforced` fails startup.
### Out of scope
- Real-workspace nightly job (Q21; only a skipped-when-no-secrets placeholder is NOT added; nightly comes with credentials).
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

## 5. Implementation steps (TDD order)
1. Red: add `tests/Autheris.Tests.Unit/Databricks/DatabricksAuthModeSecurityTests.cs` and the unit tests of section 6; change `PatAuth_RejectedOutsideDevelopment_AtStartup` into `PatAuth_WithSecretReference_AllowedInProduction_AtStartup`.
2. Options (`DatabaseOptions.cs`): add to `DatabricksAuthOptions`: `string? TokenEndpoint`, `IReadOnlyDictionary<string, DatabricksTenantPrincipalOptions> Tenants` (default empty, ordinal), `bool FallbackToGlobalPrincipal` (default false). New `sealed class DatabricksTenantPrincipalOptions { string? ClientId; string? ClientSecretRef; string? PatRef; }`. Add `IReadOnlyList<string> AllowedAccountTokenHosts` to `DatabricksOptions` (`src/Autheris.Domain/Options/DatabricksOptions.cs`, default empty). Update XML docs (remove "not configurable"/"only mode outside Development"). Register new keys in `GatewayConfigurationSchema`.
3. Validation (`GatewayStartupValidator.ValidateDatabricks`): remove the Development-only PAT branch; add, for all environments: (a) secret fields (`PatRef`, `ClientSecretRef`, `Obo.ClientSecretRef`, tenant entries) must be secret references - new helper `SecretReferenceResolver.IsReference(string)` (true for the reference syntax the Key Vault provider accepts, e.g. `kv://`/`vault:` - reuse the existing prefix check in the provider; do not invent a new syntax); (b) `TokenEndpoint` only with `OAuthM2M`, absolute `https`, no userinfo/query/fragment, host equals `db.Host` or is in `AllowedAccountTokenHosts` (ordinal-ignore-case exact match); (c) `OAuthObo` requires non-empty `Obo.Scopes` (token-exchange audience) besides the existing checks; (d) mode `OAuthM2M`/`Pat` with `EnforcementMode != AutherisEnforced` fails (message cites SEC-SIMPL-96); (e) tenant entries: key non-empty, entry for `Pat` mode needs `PatRef`, for `OAuthM2M` `ClientId`+`ClientSecretRef`; tenants with `OAuthObo` fail. Messages never contain secret values.
4. Provider (`DatabricksOboTokenProvider`): delete lines 73-78 Development check; PAT path resolves via new `ResolvePrincipal(db, tenantId)` returning `(clientId|null, secretRef, principalId)`; tenant lookup from `DelegationContext` tenant; no entry and `FallbackToGlobalPrincipal=false` while `Tenants` is non-empty -> `DatabricksProviderException(Configuration, "No Databricks principal configured for the tenant.")`. M2M uses `auth.TokenEndpoint ?? $"https://{db.Host}/oidc/v1/token"`. Call `SecretReferenceResolver.Resolve(..., allowPlaintextInDevelopment: false, ...)` at all three call sites.
5. Cache (`DatabricksTokenCache.BuildKey`): key = `(dataSourceName, tenantId ?? "-", mode, principalId, callerHash)`; `Evict` per (data source, tenant). Adjust callers.
6. Rotation (`DatabricksStatementExecutionClient` around line 246): on 401 evict token, mark secret stale (`IDatabricksCredentialProvider.InvalidateSecret(db, tenant)`; PAT/M2M re-read secret on next acquire), retry exactly once; second 401 -> `AuthFailed`. Delete lines 182-184.
7. Audit: where the Databricks query audit entry is built (follow `DatabricksStatementExecutionClient` -> `IAuditWriter` caller; if no Databricks-specific audit exists, add fields to the existing query audit details) add `auth.mode`, `auth.clientId` (M2M) or `auth.tokenFingerprint` = first 16 hex of SHA-256 of the PAT (PAT), plus caller subject and tenant already present. Never the token.
8. Green, then refactor; run full Databricks unit filter.

## 6. Tests (tests/Autheris.Tests.Unit/Databricks)
- `DatabricksAuthModeSecurityTests` [Trait("Category","Security")]:
  - `Inline_secret_fails_startup_and_tokens_never_logged_or_audited` (SEC-SIMPL-95): inline PatRef/ClientSecretRef in Production and Development fail validation; with a fake logger and audit sink, acquire + 401 + error path, assert neither token nor secret appears in log messages, exception messages, ProblemDetails or audit details; fingerprint present.
  - `Shared_identity_modes_apply_gateway_rls_masking_and_attribute_caller` (SEC-SIMPL-96): `[Theory] OAuthM2M, Pat` with `Delegated`/`DelegatedDefenceInDepth` fails startup; with `AutherisEnforced` the compiled SQL sent to the fake contains the RLS predicate and mask expression; audit has caller subject+tenant and mode.
  - `Startup_fails_closed_per_mode_on_missing_secret_http_endpoint_or_mismatch` (SEC-SIMPL-97): theory rows: missing PatRef, missing ClientSecretRef, `http://` TokenEndpoint, TokenEndpoint on foreign host, TokenEndpoint with OBO, OBO without Scopes, each in Production and Development.
  - `Per_tenant_principal_never_used_for_another_tenant` (SEC-SIMPL-98): tenants A/B with own PATs; caller B gets B token; caller C without entry fails when fallback false, gets global when true; cache keys differ per tenant.
- `DatabricksStartupValidationTests`: replace `PatAuth_RejectedOutsideDevelopment_AtStartup` with `PatAuth_WithSecretReference_AllowedInProduction_AtStartup`; add `TokenEndpoint_OnAllowlistedAccountHost_Passes`.
- `DatabricksStatementExecutionClientTests`: `Unauthorized_ReResolvesSecret_AndRetriesOnce`, `SecondUnauthorized_FailsWithAuthFailed`; `Pat_AcceptedInProduction`.
- `DatabricksTokenCacheBoundTests`: `CacheKey_IncludesTenantModeAndPrincipal`.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "FullyQualifiedName~Databricks"`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "Category=Security"`
- `git grep -n "allowed in Development only" -- src` -> no Databricks hit.
- `git grep -n "allowPlaintextInDevelopment: true\|, true, _logger" -- src/Autheris.Infrastructure/Databricks` -> empty.

## 8. Acceptance criteria
- [ ] PAT and M2M start in Production with secret references; inline secrets fail in every environment.
- [ ] Explicit TokenEndpoint accepted only https and on workspace host or allowlisted account host.
- [ ] M2M/PAT with non-`AutherisEnforced` mode fails startup.
- [ ] Per-tenant principals isolated; fallback only with `FallbackToGlobalPrincipal=true`.
- [ ] 401 re-resolves the secret, one retry; no restart needed after rotation.
- [ ] Four `DatabricksAuthModeSecurityTests` green; existing Databricks tests green; new config keys in `GatewayConfigurationSchema`.

## 9. PR slicing
1. PR A (~+250 src): options, schema keys, startup validation, PAT unblocking, inline-secret rejection; tests for SEC-SIMPL-95 (startup part), -96 (startup part), -97.
2. PR B (~+350 src): per-tenant principals, cache key, TokenEndpoint use, rotation retry, audit fingerprint; remaining security tests.

## 10. Risks, rollback, and things the developer must NOT do
- Do not log, throw or audit token/secret values or secret references; fingerprints only.
- Do not let a tenant without an entry reuse another tenant's principal; do not default `FallbackToGlobalPrincipal` to true.
- Do not loosen `DatabricksHostPolicy`; do not accept http even in Development.
- Do not retry more than once on 401 (lockout/amplification).
- Rollback: revert PR B then PR A; PAT returns to Development-only.

## 11. Estimate
About +600 src / -30 src, +800 tests; 4 person-days.

## Decisions taken in this spec
- "Token-exchange audience" for OBO maps to the existing `DatabricksOboOptions.Scopes` (non-empty required).
- Account-host allowlist lives in `DatabricksOptions.AllowedAccountTokenHosts` (global, not per data source).
- Inline secrets are rejected also in Development (OD-16 says "secrets only via secret references").
- PAT fingerprint = first 16 hex chars of SHA-256.
