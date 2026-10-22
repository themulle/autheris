# Plan: Security review fixes, area API (HIGH/MEDIUM)

Source: `docs/plans/security.txt` (baseline main @ 4fc44053). Scope: every `API-` finding rated HIGH or MEDIUM:
API-01, API-02, API-03, API-04 (no HIGH exists; API-05..07 are LOW and out of scope).
Verified against branch `fix/security-review-open-findings` @ 55dc7e27 (G-PLAN-REBASE). Breaking changes allowed (no production).

## Verification summary

| ID | Status on branch | Remaining work |
|----|------------------|----------------|
| API-01 | Open. `EntraTokenPolicy.Apply` (lines 36-45) still relies on the `nonce` heuristic; JwtBearer accepts `ClientId` as audience (`GatewayServiceCollectionExtensions.cs:361`); no access-token shape check. | Full fix (WP-API-1). |
| API-02 | Fixed by f169e5e3. `GatewayPolicies.Scim` policy registered (`GatewayPolicies.cs:123-137`, scheme `Scim` + `IsScimProvisioner`); both endpoint groups use `RequireAuthorization(GatewayPolicies.Scim)`. Unit test `GatewayPoliciesScimTests.cs` exists. | Only a missing endpoint-level regression test (WP-API-2). |
| API-03 | Partially fixed by f169e5e3: ClusterAdmin removed (only `SCIM_PROVISIONER`), unknown/non-SCIM realms rejected via `IsScimRealm`. Still open: secrets are global (`ScimOptions.PrimarySecret/SecondarySecret`), so one realm's connector secret provisions every SCIM realm; `IsScimRealm` returns `true` when `Realms` is empty (any path realm accepted); missing path realm falls back to `DefaultRealm ?? "workforce-entra"`. | Per-realm secret binding (WP-API-2). |
| API-04 | Open. `AccessAuditMiddleware.cs:80` `auditContext.Error(ex.Message, ...)`; finalizer (line 86, `finally`) reads `context.Response.StatusCode` (line 105) before the outer `UseExceptionHandler` (`GatewayApplicationBuilderExtensions.cs:28`, audit at :81) has set it, so exceptions are recorded as `statusCode=200`. | Full fix (WP-API-3). |

## Decisions per finding

### API-01: Entra ID tokens accepted as access tokens
1. Root cause: audience `ClientId` is legitimately valid for v2 access tokens, and an ID token for the same app registration passes signature/issuer/audience/lifetime; `EntraTokenPolicy` rejects only tokens with `nonce`. A nonce-less ID token with `roles` passes rule 1, is not a service principal, and having no `scp` is never marked read-only.
2. Fix in `src/Autheris.Api/Security/EntraTokenPolicy.Apply`, as new rules placed before rule 1:
   - (a) Access-token shape: reject unless `azp` (v2) or `appid` (v1) is present and non-empty. Entra ID tokens never carry either. Reason: `"Token is not an Entra access token (missing 'azp'/'appid')."`
   - (b) If `idtyp` is present and not `app`/`user`, reject (defense in depth for the optional `idtyp` claim).
   - (c) Delegated (non service principal) tokens must carry `scp`: after the SP branch, `if (scopes.Count == 0) return "Delegated tokens require 'scp'.";`. This replaces the implicit pass-through.
   - Keep the `nonce` rule as an extra check (cheap), but it is no longer the only guard.
3. Fail-closed: missing or blank claim leads to `ctx.Fail` (401). There is no config switch to turn it off.
4. Red tests (`tests/Autheris.Tests.Unit/Security/EntraTokenPolicyAccessTokenShapeTests.cs`, new):
   `Apply_IdTokenWithoutNonceWithRoles_Rejected`, `Apply_TokenWithoutAzpAndAppid_Rejected`, `Apply_V1AccessTokenWithAppid_Accepted`, `Apply_V2AccessTokenWithAzp_Accepted`,
   `Apply_DelegatedTokenWithRolesButNoScp_Rejected`, `Apply_IdtypUnknown_Rejected`, `Apply_AppOnlyTokenWithAzpAndRoles_StillFollowsAppOnlyPolicy`.
   Integration (JwtBearer with a signed test key): `EntraBearer_SignedIdTokenWithoutNonce_Returns401` in `tests/Autheris.Tests.Integration`.
5. Breaking: existing test principals in `ReadOnlyTokenTests.cs` (lines 79-124) built without `azp`/`appid` must get the claim added. Fixture helpers should be updated rather than the rule weakened.
6. Dependencies: none. This does not apply to ADFS tokens, because the issuer gate in `OnTokenValidated` is unchanged.

### API-02: SCIM policy missing
7. Already fixed (f169e5e3). Add `tests/Autheris.Tests.Integration/ScimEndpointAuthorizationTests.cs`:
   `ScimUsers_ValidRealmSecret_Returns2xxNot500`, `ScimUsers_NoAuthorization_Returns401`, `ScimUsers_UserJwtOnScimPath_Returns401Or403`.
   These tests guard against a regression to an unknown policy name (500).

### API-03: SCIM secret scope
8. Root cause (residual): a single global secret pair is not bound to a realm, and the legacy mode (`Realms` empty) accepts any path realm.
9. Fix:
   - `src/Autheris.Domain/Options/Security/IdentityRealmsOptions.cs`: add `ScimRealmSecretOptions { string? PrimarySecret; string? SecondarySecret; }` and `IdentityRealmConfigOptions.Scim` (type `ScimRealmSecretOptions?`). Remove `ScimOptions.PrimarySecret/SecondarySecret`, which is a breaking config change (`Authentication:Realms:Realms:<id>:Scim:PrimarySecret`). `ScimOptions` keeps IP ranges and mass-change settings.
   - `ScimAuthenticationHandler.HandleAuthenticateAsync`: if no path realm is found, call `Fail` (remove the `DefaultRealm`/`"workforce-entra"` fallback). Resolve `realm = Realms[pathRealm]` and fail if the realm is unknown, if `Provisioning != Scim`, or if the realm has no secret configured. Validate the token in constant time only against that realm's primary/secondary secret. `IsScimRealm`: with `Realms.Count == 0`, return `false`.
   - Startup validation (an existing realms options validator, or a new `IdentityRealmsScimOptionsValidator : IValidateOptions<IdentityRealmsOptions>`): every realm with `Provisioning == Scim` and SCIM enabled must have a secret of at least 32 chars, and secrets must be unique across realms (otherwise realm binding is void).
10. Fail-closed: every resolution miss leads to `AuthenticateResult.Fail` (401), and the failure text never echoes the realm. Policy `GatewayPolicies.Scim` also requires that the identity's `autheris:realm` equals the route `{realmId}`. Add a `RequireAssertion` that reads `HttpContext.Request.RouteValues["realmId"]` (defense in depth against path/route divergence).
11. Red tests (extend `tests/Autheris.Tests.Unit/Identity/ScimAuthenticationHandlerTests.cs`):
    `ScimAuthenticationHandler_SecretOfRealmA_OnRealmBPath_Rejects`, `ScimAuthenticationHandler_NoRealmsConfigured_Rejects`, `ScimAuthenticationHandler_PathWithoutRealm_Rejects`,
    `ScimAuthenticationHandler_RealmWithoutSecret_Rejects`, `ScimAuthenticationHandler_SecondarySecretOfRealm_Authenticates`. `GatewayPoliciesScimTests`: `ScimPolicy_RealmClaimDiffersFromRoute_Denied`.
    Validator: `ScimOptionsValidator_DuplicateSecretAcrossRealms_Fails`, `ScimOptionsValidator_ScimRealmWithoutSecret_Fails`, `ScimOptionsValidator_ShortSecret_Fails`.
12. Breaking: configuration moves from a global to a per-realm secret, and the legacy no-realms SCIM mode is removed. Update the appsettings samples/Helm values if they reference `Scim:PrimarySecret`.
13. Dependencies: API-07 (LOW, SCIM IP budget) touches the same pipeline but is out of scope. Coordinate with the IDENTITY area if its plan changes `IdentityRealmConfigOptions`.

### API-04: Raw exception text and wrong status in the audit chain
14. Fix:
   - Extract `internal static class GatewayExceptionStatusMapper { public static int Resolve(Exception ex); }` in `src/Autheris.Api/Middleware/` from `GatewayExceptionHandler` (the `SqlCompilerErrorMapper.TryMap` -> `GatewayThrottledException` -> `Map` chain, status only). `GatewayExceptionHandler` uses it so a single classifier is shared.
   - `AccessAuditMiddleware` catch block: `auditContext.Error(AuditExceptionCode.For(ex), ...)`, where new `internal static AuditExceptionCode.For(Exception)` returns `TelemetryExceptionPolicy.ErrorType(ex)` only when it matches `^[A-Za-z0-9_.:-]{1,64}$`, otherwise `ex.GetType().Name`. `ex.Message` is never used. The exception status is stored in a local `int? exceptionStatus = GatewayExceptionStatusMapper.Resolve(ex)` (abort: 499, matching the handler's `ClientClosedRequest`).
   - `FinalizeAuditAsync(..., int? statusOverride)`: `statusCode = statusOverride ?? context.Response.StatusCode`. This feeds the existing 429/5xx/delegated branches, so a mapped 403/429 exception is now classified as a DENY rather than an ERROR.
15. Fail-closed: if the mapper itself throws, fall back to 500 with code `"UNCLASSIFIED"`. The audit write path keeps its current fail-closed semantics.
16. Red tests (`tests/Autheris.Tests.Unit/Audit/AccessAuditMiddlewareTests.cs`):
    `InvokeAsync_ExceptionWithPiiMessage_MessageNeverInDetailsJson` (message `"duplicate key (alice@corp.com)"`, assert neither the full string nor the e-mail appears in `DetailsJson`/`ReasonCode`),
    `InvokeAsync_UnhandledException_RecordsMappedStatus500NotDefault200`, `InvokeAsync_TimeoutException_RecordsStatus504`, `InvokeAsync_GatewaySecurityException_RecordsDenyWithMappedStatus`,
    `InvokeAsync_ErrorCodeWithIllegalChars_FallsBackToTypeName`. Unit test `GatewayExceptionStatusMapperTests` (parity with handler responses).
    Adjust the existing `InvokeAsync_WhenExceptionThrown_RecordsErrorAuditEntryAndRethrows` and `InvokeAsync_UnroutedException_StillRecordsError` assertions to the stable code.
17. Breaking: the format of `reasonCode` values for exception rows changes (audit consumers/SIEM dashboards that keyed on message text). This is acceptable. The hash chain is unaffected (new rows only).
18. Dependencies: the AUDIT area plan, if it changes `AuditDetailsBuilder`/`DetailsJson` sanitising. This fix is independent and must not rely on downstream sanitising.

## Work packages (order)

| WP | Findings | Size | Notes |
|----|----------|------|-------|
| WP-API-1 | API-01 | S (about 120 LOC including tests) | Independent; can run in parallel with WP-API-3. |
| WP-API-3 | API-04 | M (about 220 LOC) | Mapper extraction first (pure refactor plus parity test), then middleware. |
| WP-API-2 | API-03 (+ API-02 regression tests) | M (about 260 LOC) | Options change, handler, validator, policy assertion, integration test. Last, because it touches config. |

## Security conditions for Phase 3
- C1: The Entra access-token shape check (`azp`/`appid`) is unconditional, has no configuration bypass, and runs before any role/scope evaluation.
- C2: Delegated Entra tokens without `scp` are rejected, not treated as full-access tokens.
- C3: A SCIM secret authenticates exactly one realm, there is no default-realm fallback, and duplicate or short secrets fail at startup.
- C4: A SCIM principal never carries any role other than `SCIM_PROVISIONER`. The realm claim must equal the route realm at authorization time.
- C5: No code path in `AccessAuditMiddleware` writes `Exception.Message`, `ToString()` or inner-exception text into an audit field. An architecture/grep test asserts this (`AccessAuditMiddleware_DoesNotReferenceExceptionMessage`).
- C6: The audited status for exception paths equals the status the client receives (a parity test between the mapper and `GatewayExceptionHandler`).
- C7: Failure reasons returned by the auth handlers never echo client-supplied values (realm, token).
