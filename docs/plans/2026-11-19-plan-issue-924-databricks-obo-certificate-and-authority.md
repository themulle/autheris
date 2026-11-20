# Plan: Databricks OBO ignores `ClientCertificateRef` and does not check `Obo.Authority` (Issue #924)

- Issue: [#924](https://github.com/themulle/autheris/issues/924). Phase 2 plan, 2026-11-19. Code facts verified on `origin/main` after the v1.9.0 tag.
- Type: security hardening of outbound credential use (configuration-trusted, not caller-controlled). No DDL.
- Phase 3: **required**. The change touches where the caller's token assertion and the client secret are sent.
- Related: the #916 plan ([`2026-11-19-plan-issue-916-databricks-metric-views.md`](2026-11-19-plan-issue-916-databricks-metric-views.md)) runs every metric-view read through the same OBO path. Land this row before WP-MV-6 (Security and parity tests) of #916, so those tests use the hardened provider.

## 1. Problem and evidence

1. Certificate unused. `src/Autheris.Domain/Options/Storage/DatabaseOptions.cs:297-298` declares `DatabricksOboOptions.ClientCertificateRef` ("used for client assertion").
   - `src/Autheris.Api/Extensions/DependencyInjection/GatewayStartupValidator.cs:377-380` accepts it as an alternative to `ClientSecretRef`.
   - `src/Autheris.Infrastructure/Databricks/DatabricksOboTokenProvider.cs:280-286` only checks that it is non-empty.
   - The token request (lines 302-310) always sends `client_secret = secret ?? string.Empty`. No `client_assertion` is built anywhere (`git grep client_assertion src` finds nothing). A certificate-only configuration starts, then fails at the identity provider with an empty secret.
   - `ClientCertificateRef` is also not checked as a secret reference. The check at `GatewayStartupValidator.cs:382` covers only `ClientSecretRef`.
2. Authority unchecked. `DatabricksOboTokenProvider.cs:288-296` uses `obo.Authority` as given: any scheme, host, path or query. It then POSTs the caller's raw token (`assertion`) and the client secret to `{authority}/oauth2/v2.0/token`. The startup validator (lines 362-386) never looks at `Authority`. Without `Authority`, the default is `https://login.microsoftonline.com/{TenantId|common}` (lines 289-293).
3. Tests: `tests/Autheris.Tests.Unit/Databricks/DatabricksOboSecurityTests.cs`, `DatabricksOboTokenTests.cs` and `DatabricksStartupValidationTests.cs` exist. None covers the certificate or the authority.

## 2. Options

Certificate:

| Option | Description | Verdict |
|---|---|---|
| C-A | Implement the RFC 7523 client assertion: load the certificate from the secret store, sign a JWT (`aud` = token endpoint, `x5t#S256`, short `exp`, random `jti`), send `client_assertion_type` and `client_assertion` | Valid, size M. It needs certificate loading through `SecretReferenceResolver` and key-handling review. Follow-up only. |
| C-B | Reject `ClientCertificateRef` at startup ("not supported; use ClientSecretRef") and remove the either/or | **Recommended now.** Fail-closed, size XS. The option promises something the code does not do. |

Authority:

| Option | Description | Verdict |
|---|---|---|
| A-A | Built-in allow-list of Entra cloud login hosts, `https` only, path = exactly one tenant segment, no user info, query or fragment. Checked at startup and again before each token request | **Recommended.** |
| A-B | Configurable allow-list `Auth.Obo.AllowedAuthorityHosts` | Not now. It moves the trust decision into the same configuration it should check. |

## 3. Decisions
1. D-924-1: new `internal static class DatabricksOboAuthority` in `src/Autheris.Infrastructure/Databricks/`, with `static bool TryNormalize(string? authority, string? tenantId, out Uri tokenEndpoint, out string error)`.
   - Allowed hosts (ordinal, case-insensitive): `login.microsoftonline.com`, `login.microsoftonline.us`, `login.partner.microsoftonline.cn`.
   - Scheme `https`, default port only, exactly one path segment (the tenant id, a GUID or a domain name; `common` and `organizations` are allowed only when `Authority` is not set, as today).
   - No user info, query or fragment.
   - The result is the token endpoint `{authority}/oauth2/v2.0/token`.
2. D-924-2: `GatewayStartupValidator` (OAuthObo branch, lines 362-386) calls `TryNormalize`. On failure it throws `ValidationException($"Databricks data source '{name}': Auth.Obo.Authority …")`.
3. D-924-3: `DatabricksOboTokenProvider` (lines 288-296) uses `TryNormalize`. On failure it throws `DatabricksProviderException(DatabricksErrorCodes.Configuration, …)` before any HTTP call, as defense in depth for configuration reloads.
4. D-924-4 (option C-B): the startup validator throws when `ClientCertificateRef` is set, and `ClientSecretRef` becomes mandatory for OAuthObo. The provider drops the certificate branch at line 281 and never sends an empty `client_secret`; a missing secret throws `Configuration`. Keep the option property, marked `[Obsolete]` with an XML doc saying it is rejected, so binding gives a clear error instead of being silently ignored.
5. D-924-5: reference docs describe the allowed authority hosts and the certificate rejection, if a Databricks OBO section exists in `docs/reference/` (none found by `git grep ClientCertificateRef docs`, so nothing to change unless one is added).

## 4. Work packages (one PR)

### WP-924-1 (S): authority validation
- Files: new `src/Autheris.Infrastructure/Databricks/DatabricksOboAuthority.cs`; `DatabricksOboTokenProvider.cs:288-296`; `GatewayStartupValidator.cs:362-386`.
- Unit tests (filter `DatabricksOboAuthorityTests|DatabricksStartupValidationTests|DatabricksOboSecurityTests`), all `[Trait("Category", "Security")]`:
  - New `tests/Autheris.Tests.Unit/Databricks/DatabricksOboAuthorityTests.cs`. Theory cases `Authority_Rejected(string)` for `http://login.microsoftonline.com/t`, `https://evil.example/t`, `https://login.microsoftonline.com.evil.example/t`, `https://user@login.microsoftonline.com/t`, `https://login.microsoftonline.com/t?x=1`, `https://login.microsoftonline.com/t/extra` and `https://login.microsoftonline.com:8443/t`. Also `Authority_Accepted_NationalClouds` and `NoAuthority_DefaultsToCommonOrTenant`.
  - `DatabricksStartupValidationTests`: `Obo_UntrustedAuthority_FailsStartup`.
  - `DatabricksOboSecurityTests`: `Obo_UntrustedAuthority_NoHttpRequestSent`. The fake handler records zero requests.
- Acceptance: no token request leaves the process for an authority outside the allow-list.

### WP-924-2 (XS): reject `ClientCertificateRef`
- Files: `GatewayStartupValidator.cs:377-385`, `DatabricksOboTokenProvider.cs:280-286` and `DatabaseOptions.cs:297-298` (`[Obsolete]` and XML doc).
- Unit tests, `[Trait("Category", "Security")]`:
  - `DatabricksStartupValidationTests.Obo_ClientCertificateRef_FailsStartup`
  - `DatabricksStartupValidationTests.Obo_MissingClientSecretRef_FailsStartup`
  - `DatabricksOboSecurityTests.Obo_SecretUnresolved_NeverSendsEmptyClientSecret`
- Check `tests/Autheris.Tests.Unit/Configuration/Snapshots/*` and `tests/Autheris.Tests.Architecture/Baselines/secure-defaults.txt` for option snapshots that list `ClientCertificateRef`, and update them.

### Order
WP-924-1, then WP-924-2. Independent of #925. Should land before #916 WP-MV-6.

## 5. Integration test impact
- `tests/Autheris.Tests.Integration/Databricks/DatabricksStatementExecutionContractTests.cs`, `DatabricksTransportParityTests.cs` and `AstCompilerDatabricksSpark*ExecutionTests.cs`: check their OBO configuration (if any) for custom `Authority` values pointing at a local mock. If a mock authority is used, the test must inject the token provider's HTTP handler instead. Do not add a test-only allow-list entry.
- `tests/Autheris.Tests.SqlCompilerGate/Databricks/DatabricksGateSupport.cs` (live gate): verify it uses M2M or PAT, not OBO with a custom authority.

## 6. Owner questions
- Q-924-1: implement certificate client assertions now (C-A) or reject the option (C-B)? **Default taken: C-B now; C-A as a follow-up issue on request.**
- Q-924-2: is the built-in allow-list (public, US Government and China clouds) enough, or is a configurable list needed? **Default taken: built-in only.**
