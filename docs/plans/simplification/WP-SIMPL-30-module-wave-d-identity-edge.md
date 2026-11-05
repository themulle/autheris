# WP-SIMPL-30 Module wave D: identity and edge modules (SCIM, TOTP, Kerberos/NTLM, Envoy)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-30-module-wave-d-identity-and-edge-modules) | Issues: #515 | Owner decisions: OD-9 (admin scoping must stay intact) | Security conditions: SEC-SIMPL-03, 05, 30, 31, 32, 43, 44, 48, 49 and the auth-scheme removal condition (`AuthSchemeRemovalTests`)

> **Cancelled by owner decision OD-21 (2026-11-05):** authentication and authorization are core; SCIM, TOTP, Negotiate (Kerberos/NTLM) and Envoy ext_authz stay in core and are NOT moved to modules. The cleanup of this area is specified in [WP-SIMPL-43](WP-SIMPL-43-authentication-authorization-core.md). Do not implement this WP; it is kept for traceability only. The removal of the Api -> Extensions project reference that this WP carried moves to WP-SIMPL-43 or the last module WP.

## 1. Goal and outcome
Move SCIM, TOTP local-auth, Negotiate (Kerberos/NTLM) and Envoy ext-authz into four modules (`Autheris.Modules.Scim`, `.Totp`, `.Negotiate`, `.EnvoyExtAuthz`). Package `Microsoft.AspNetCore.Authentication.Negotiate` leaves `Autheris.Api`; 18 endpoints leave core. With a module absent, its routes are 404, its config fails fast, and the authentication pipeline never falls back to anonymous.

## 2. Preconditions and dependencies
- Merged: WP-SIMPL-25, WP-SIMPL-26 (runtime-vs-fallback decision), WP-SIMPL-36 batch for OD-9.
- Borderline modules from WP-SIMPL-18 (Procedures, governed-aggregates) are a separate PR in this WP only if WP-SIMPL-18 marked them MODULE with a named owner; otherwise out of scope.
- Parallel with WP-SIMPL-27..29.

## 3. Scope
### In scope
- SCIM: `Api/Endpoints/Scim{Users,Groups,Discovery}Endpoints.cs` (6+6+3 Map*), `ScimFilterGuard.cs`, `ScimMassChangeResult.cs`, `Api/Security/ScimAuthenticationHandler.cs`, `Application/Identity/{ScimIpAllowlist.cs, Services/IScimService.cs, Services/ScimService.cs, Services/ScimGroupMembershipValidator.cs, Services/ScimMassChangeGuard.cs, Validators/ScimRequestValidator.cs, Validators/IdentityRealmsScimOptionsValidator.cs, Providers/ScimStoreRoleProvider.cs}`, `Domain/Identity/ScimModels.cs`.
- TOTP: `Application/Security/Totp/**`, `Api/Security/DataProtectionTotpSecretProtector.cs`, `Api/Endpoints/TotpFirstEnrollmentGuard.cs`, `Domain/Model/TotpModels.cs`, TOTP endpoints.
- Negotiate: `Api/Security/NegotiateHardening.cs`, the `AddNegotiate` registration, package reference `Autheris.Api.csproj:16`.
- Envoy: `Api/Endpoints/EnvoyExtAuthzEndpoints.cs` (3), `Application/Mesh/{Interfaces/IEnvoyExtAuthzService.cs, Services/EnvoyExtAuthzService.cs, Services/EnvoyExportValidation.cs}`, `Domain/Model/EnvoyAuthModels.cs`, `Domain/Options/Security/EnvoyExtAuthzOptions.cs`.
### Out of scope
- `EnterpriseClaimsTransformation`, default/fallback auth policy, OIDC/JWT/Basic (stay core).
- SCIM user store tables (schema stays in core migrations; store access through `IModuleHostContext`).

## 4. Current state (verified on 63cbb893)
- Negotiate package: `src/Autheris.Api/Autheris.Api.csproj:16` (`Version="10.0.14"`).
- Core code depending on Negotiate: `TotpFirstEnrollmentGuard.cs:74` uses `NegotiateHardening.KerberosAuthenticationType`; `ODataEndpoints.cs:91` comment only.
- Map* counts: SCIM 15, Envoy 3.

## 5. Implementation steps
1. Red: `AuthSchemeRemovalTests` and absent-module tests (section 6) in `tests/Autheris.Tests.Integration/Modules/`.
2. Introduce `AuthenticationTypes` constants in `Autheris.Domain` (`Kerberos`, `Ntlm`, `Negotiate`) so `TotpFirstEnrollmentGuard` no longer depends on `NegotiateHardening`; replace usage at line 74.
3. Add to `IModuleHostContext` (abstractions) an `IAuthenticationSchemeRegistrar` with `AddScheme<THandler, TOptions>(string name, ...)` that the host applies before `AddAuthentication` is finalised; host asserts `DefaultScheme` and `FallbackPolicy` unchanged after all modules ran (SEC-SIMPL-44).
4. Per module: create `modules/Autheris.Modules.<Name>/` + `.Tests`, `<Name>Module : IAutherisModule` (ids `scim`, `totp`, `negotiate`, `envoy-extauthz`), `git mv` files, fix namespaces, register services/schemes/endpoints through host registrars.
5. Remove `Microsoft.AspNetCore.Authentication.Negotiate` from `Autheris.Api.csproj`; add it to the Negotiate module csproj.
6. `UnknownGatewayKeyGuard`: SCIM, TOTP, Kerberos and EnvoyExtAuthz sections fail fast when the module is not loaded.
7. Move tests unchanged; coverage-map rows; update CODEOWNERS, baselines, `Autheris.sln`.

## 6. Tests ([Trait("Category","Security")] for all below)
- `AuthSchemeRemovalTests.Missing_scheme_does_not_open_anonymous_path` (Kerberos configured, module absent -> startup fails; with no config protected endpoints return 401).
- `AuthSchemeRemovalTests.Default_and_fallback_policy_unchanged_after_module_load`.
- `IdentityModulesAbsentTests.Scim_routes_return_404_without_module`, `.Envoy_routes_return_404_without_module`, `.Totp_enrollment_routes_return_404_without_module`, `.Scim_config_fails_fast_without_module`.
- `modules/Autheris.Modules.Negotiate.Tests/NegotiateModuleTests.Negotiate_module_registers_scheme`.
- `ScimModuleTests.Scim_ip_allowlist_enforced_when_loaded_as_module` (SEC-SIMPL-32), `TotpModuleTests.Totp_secret_protected_at_rest`, `.Totp_lockout_after_failed_attempts` (SEC-SIMPL-48).
- `PackageBoundaryArchitectureTests.Api_has_no_Negotiate_package`.
- `EnterpriseClaimsTransformationTests.No_role_granted_when_scheme_claims_absent`.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build`
- `dotnet test modules/Autheris.Modules.Scim.Tests -c Release --no-build` (repeat per module)
- `dotnet test tests/Autheris.Tests.Integration --filter "Category=Security" -c Release --no-build`
- `git grep -n "Authentication.Negotiate" -- src` returns nothing; `git grep -l "Scim\|Totp\|EnvoyExtAuthz" -- src` returns only the Domain constants/migrations.

## 8. Acceptance criteria
- [ ] Negotiate package absent from all `src/` csproj files.
- [ ] 18 routes return 404 in the core image (auth and anon); present in full image.
- [ ] All moved Security tests green, count not lower.
- [ ] Startup fails when a module's section is present but the module not loaded.

## 9. PR slicing
1. Domain auth-type constants + scheme registrar + `AuthSchemeRemovalTests` (<= 400).
2. Envoy module. 3. TOTP module. 4. SCIM module. 5. Negotiate module + package removal. (Move PRs: move-only `size-exception`.)

## 10. Risks, rollback, must NOT
- Risk: auth handler order; mitigated by registrar step 3. Rollback per PR.
- Must NOT: change default or fallback scheme; grant roles when a module's claims are missing; let modules override `IClaimsTransformation`; weaken OD-9 admin scoping.

## 11. Estimate
+~700 / -~5,000 src. 5 person-days.

## Decisions taken in this spec
- Four separate modules (one per transport/scheme) so operators install only what they use.
- Procedures and governed-aggregates only if WP-SIMPL-18 named an owner; otherwise excluded.
