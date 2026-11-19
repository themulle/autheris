# Plan: issue #853, workload identity: gateway roles for a fixed OIDC subject (configuration only) and static JWKS

Phase 3 security review. Verdict: APPROVED WITH CONDITIONS (the privileged-role and tenant conditions are blocking).
Phase 4 note: never put `WP-`, `SEC-` or `OD-` identifiers into comments under `src/Autheris.Api` or `src/Autheris.Application`.

## Problem
A cluster job (dbt sync/governance) holds only a Kubernetes service account token: no `roles` claim, no stored secret. `StaticMappingRoleProvider` is registered but only tests call `AddMapping`; `dbt/governance` needs canonical `ClusterAdmin` (#670).

## Code facts that shape the design
- `AllowedGatewayRoles` is parsed in `IdentityRealmRegistry.Initialize` with `Enum.TryParse` and unknown names are silently dropped; it is not enforced when roles are resolved. `Enum.TryParse` also accepts numeric strings ("1" = `ClusterAdmin`).
- `IdentityPipeline.AddRolesAndRightsAsync` takes the user key as `oid ?? sub`, runs only for Resolver realms with a resolved tenant, and a degraded role resolution denies everything. Realm falls back to the default realm when the issuer is unknown (`ResolveRealmByIssuer ?? ResolveRealmForScheme ?? GetDefaultRealm`).
- `ConfigureJwtBearer` (`GatewayServiceCollectionExtensions.cs`) builds `ValidIssuers`/`ValidAudiences` from the Entra and AD FS options only. No per-realm key source or generic-realm issuer/audience wiring was found in `src/Autheris.Api`. The architect must confirm and add this; the plan below makes it binding.
- Configuration keys use `:` as separator and are case-insensitive. A subject like `system:serviceaccount:ns:name` cannot be a dictionary key (it splits into sections; keys differing in case collapse). The form in the issue text is therefore replaced by a list.
- `IsCanonicalClusterAdmin` reads literal `ClusterAdmin` role claims only; ForwardAuth identities never qualify.

## Decisions (defaults taken)
- Config shape (list of entries, no dictionary key): `Authentication:Realms:Realms:<realm>:SubjectRoles:<i>:Subject` and `...:SubjectRoles:<i>:Roles:<j>`. Environment variables and JSON both work.
- Match: exact, ordinal, case-sensitive string equality against the validated `sub` claim; the token must carry exactly one `sub`. The realm must have `UserKeyClaim = sub` (so `oid` can not become the user key for a mapped subject), `Kind = GenericOidc`, and the `iss` of the token must equal one of the realm's own `Issuers` (re-checked in the mapping step, not only via realm resolution, so default-realm fallback can never apply a mapping).
- Roles must be named members of `GatewayRole` (exact name, case-insensitive compare, numeric rejected) and a subset of the realm's `AllowedGatewayRoles`.
- `ClusterAdmin`: mappable only when the realm has `AllowedGatewayRoles` containing it AND the explicit flag `AllowPrivilegedSubjectRoles = true` (default false) AND the realm has `FixedTenantId` set. The flag covers `ClusterAdmin` only; `TenantAdmin`, `GovernanceAdmin`, `SecurityAuditor` need no flag but must be in `AllowedGatewayRoles`.
- Limits: at most 32 entries per realm, 8 roles per entry, subject 1..256 chars, no whitespace padding, no control characters, none of `* ? % [ ] ^ $ |`, no duplicate subject (ordinal) in a realm.
- Roles union: the mapped roles are added to the roles the providers return and to token roles; they never remove or override any. `ROLE_MEMBERS` rows keep their current meaning and still never create `ClusterAdmin` (#670). Deny precedence of grants is untouched.
- Tenant binding: mapped identity gets the realm's `FixedTenantId` only; tenant claims in the token are ignored for a mapped subject. Realms with `ClaimMapped` or `Unbound` binding and `SubjectRoles` fail startup.
- Resolver chain: implement as an `IRoleProvider` with id `subject-roles` reading an immutable snapshot built from options at startup; the realm must list `subject-roles` in its `ResolverChain` or startup fails. `AddMapping` is not the config path and is not reachable from any endpoint.
- Audit: event `WORKLOAD_ROLE_MAPPING_APPLIED` (FailClosed in `AuditPolicyTable`) written by the pipeline per authentication in which a mapped role is applied, also on role-cache hits. If the audit write fails, none of the mapped roles are applied for that request. Record: realm id, subject (sanitized, capped 256), applied role names, request id; never the token, never `jti`/signature. Startup log: one line per realm with entry count, role-name counts and a privileged-entry count, no subjects, no key material. A startup warning line when any `ClusterAdmin` mapping exists.
- Static JWKS (per realm, `Jwks:File` only in this slice; the key-vault source is deferred until a vault abstraction exists, because none was found in `src/`): file path must be absolute, readable, at most 256 KiB, at most 16 keys; parsed once at startup and re-read on a timer (default 5 min, range 1..60) plus on unknown `kid` (at most once per 30 s). A reload failure keeps the last good set and logs a warning; a missing/unreadable/empty/invalid file at startup fails startup. No HTTP fetch of keys when a static JWKS is set: `MetadataAddress` and `Jwks` are mutually exclusive. Allowed: RSA >= 2048 bits with `RS256/RS384/RS512/PS256`, EC P-256/P-384 with `ES256/ES384`; `none`, HMAC, `oct` keys and private key material (`d`, `p`, `q`) are rejected at load. `kid` required and unique. Issuer, audience, lifetime and signature are always validated (`ValidateIssuer/Audience/Lifetime/IssuerSigningKey = true`, issuers and audiences taken from the realm and must be non-empty; clock skew at most 2 minutes).
- Immutability: no API, GraphQL or MCP route may write `SubjectRoles` or `Jwks`; an architecture test pins that.

## Binding conditions
- SEC-853-01: Mapping applies only on exact ordinal `sub` equality, single `sub` claim, realm `UserKeyClaim = sub`, token `iss` in that realm's `Issuers`; no pattern, prefix, case folding or trimming.
- SEC-853-02: Startup fails for: empty/whitespace subject, wildcard or forbidden character, duplicate subject, unknown or numeric role name, role not in `AllowedGatewayRoles`, empty role list, limit exceeded, realm kind not `GenericOidc`, missing `subject-roles` in chain, non-`Fixed` tenant binding. No silent drop of any entry.
- SEC-853-03: `ClusterAdmin` needs `AllowPrivilegedSubjectRoles = true`, `AllowedGatewayRoles` containing it, `FixedTenantId`, non-empty `Audiences` and `AllowedClientIds` unset or satisfied; otherwise startup fails. The effective claim is the literal `ClusterAdmin` so `IsCanonicalClusterAdmin` applies unchanged.
- SEC-853-04: `AllowedGatewayRoles` becomes an enforced ceiling at resolution time for mapped roles (and the registry no longer drops unknown names silently for realms with `SubjectRoles`).
- SEC-853-05: Mapped roles are additive (union); a token of any other subject in the same realm receives nothing from the mapping; `ROLE_MEMBERS` alone never yields `ClusterAdmin`.
- SEC-853-06: Mapped identity is stamped with the realm's fixed tenant; token tenant claims are ignored; a degraded or failed resolution denies and never leaves partial mapped roles.
- SEC-853-07: Audit per authentication (cache hit included) before the role is applied; audit failure means the mapped roles are not applied; audit and logs contain no token, key, `jti` or file contents; subject is sanitized and capped.
- SEC-853-08: Startup summary log carries counts only, no subjects; a `ClusterAdmin` mapping logs a warning.
- SEC-853-09: Static JWKS rules above hold: source only from file in this slice, size/key-count caps, algorithm and key-size allow-list, private/symmetric keys rejected, unique `kid`, mutually exclusive with `MetadataAddress`, no outbound HTTP for keys, fail closed at startup and on empty set, last-good set kept on reload failure, rotation reload bounded in rate.
- SEC-853-10: Issuer, audience, lifetime and signature validation are on for a realm with static JWKS; a token for another audience or issuer, an expired token, or an unknown `kid` is not authenticated.
- SEC-853-11: Configuration only: no endpoint, GraphQL mutation, MCP tool or SCIM route can create or change a mapping or a JWKS (architecture test over route/mutation surface plus a source scan that `StaticMappingRoleProvider.AddMapping` has no call site in `src/`).
- SEC-853-12: A mapped read-only or restricted-scope semantics are not widened: scope and app-only token rules (`AppOnlyTokens`, read-only scopes) still apply to the mapped principal.

## Mandatory tests (all `[Trait("Category","Security")]`; unit tests local, integration on GitHub Actions)
Unit, `tests/Autheris.Tests.Unit/Identity/SubjectRoleMappingTests.cs`:
- `SubjectRole_ExactSubject_AppliesRole`; `SubjectRole_OtherSubjectSameRealm_GetsNothing`; `SubjectRole_CaseDifferentSubject_GetsNothing`; `SubjectRole_PrefixOrWildcardSubject_NeverMatches`; `SubjectRole_TwoSubClaims_Denied`; `SubjectRole_IssuerNotOfRealm_DefaultRealmFallback_NoMapping`; `SubjectRole_OidClaimPresent_DoesNotChangeMatch`
- Startup validator, `SubjectRoleOptionsValidatorTests`: `Startup_EmptySubject_Fails`, `Startup_WildcardCharacters_Fails` (theory: `* ? % [ ] ^ $ |`), `Startup_DuplicateSubject_Fails`, `Startup_UnknownRole_Fails`, `Startup_NumericRole_Fails`, `Startup_RoleNotInAllowedGatewayRoles_Fails`, `Startup_EmptyRoleList_Fails`, `Startup_LimitsExceeded_Fails`, `Startup_NonGenericOidcRealm_Fails`, `Startup_ChainWithoutSubjectRolesProvider_Fails`, `Startup_NonFixedTenantBinding_Fails`, `Startup_ClusterAdminWithoutOptInFlag_Fails`, `Startup_ClusterAdminWithFlagAndFixedTenant_Succeeds`
- Pipeline, `IdentityPipelineSubjectRoleTests`: `Mapped_ClusterAdmin_IsCanonicalClusterAdmin`; `Mapped_Roles_AreUnionWithProviderAndTokenRoles`; `Mapped_Identity_UsesRealmFixedTenant_IgnoresTokenTenantClaim`; `RoleResolutionDegraded_NoMappedRolesApplied`; `RoleCacheHit_StillWritesAuditRecord`; `AuditFailure_MappedRolesNotApplied`; `AuditRecord_ContainsRealmSubjectRoles_NoTokenOrJti`; `StartupSummary_LogsCountsOnly_NoSubjects`
- JWKS, `StaticJwksKeySourceTests`: `Jwks_MissingFile_FailsStartup`, `Jwks_EmptyOrInvalid_FailsStartup`, `Jwks_OversizeFile_Rejected`, `Jwks_TooManyKeys_Rejected`, `Jwks_PrivateKeyMaterial_Rejected`, `Jwks_SymmetricKey_Rejected`, `Jwks_RsaBelow2048_Rejected`, `Jwks_AlgNoneOrHs256_Rejected`, `Jwks_DuplicateKid_Rejected`, `Jwks_WithMetadataAddress_Fails`, `Jwks_Rotation_NewKidAccepted_OldKidRemovedAfterReload`, `Jwks_ReloadFailure_KeepsLastGoodSet`, `Jwks_UnknownKidReload_RateLimited`, `Jwks_NoHttpRequestMade`, `Jwt_WrongAudience_WrongIssuer_Expired_Rejected_WithStaticJwks`
- Architecture/source: `SubjectRoles_NotWritableByAnyEndpointOrMutation`, `StaticMappingRoleProvider_AddMapping_HasNoSrcCallSite`
Integration (`tests/Autheris.Tests.Integration`, GitHub Actions): `Dbt_Governance_WithMappedServiceAccountToken_Succeeds`, `Dbt_Governance_WithOtherSubjectToken_Forbidden`, `WorkloadIdentity_MappedRole_WritesAuditRowInStore`, `WorkloadIdentity_TokenFromOtherTenantRealm_NoCrossTenantAccess`.
Update `tests/Autheris.Tests.Architecture/Baselines/security-tests-base.txt` if the ratchet requires new security test names.

## Files to touch (paths verified by grep)
- `src/Autheris.Domain/Options/Security/IdentityRealmsOptions.cs` (`IdentityRealmConfigOptions`: `SubjectRoles`, `AllowPrivilegedSubjectRoles`, `Jwks`)
- `src/Autheris.Domain/Identity/IdentityRealm.cs` (immutable subject-role snapshot, `AllowedGatewayRoles` already at line ~133)
- `src/Autheris.Application/Identity/IdentityRealmRegistry.cs` (`Initialize`: stop silent role drop, build snapshot)
- `src/Autheris.Application/Identity/Validators/IdentityRealmOptionsValidator.cs` (or a new sibling validator registered like `IdentityRealmSchemeValidator`)
- `src/Autheris.Application/Identity/Providers/` (new `SubjectRoleProvider.cs`; leave `StaticMappingRoleProvider.cs` untouched)
- `src/Autheris.Api/Extensions/DependencyInjection/GatewayGovernanceServiceExtensions.Identity.cs` (register provider, around line 54)
- `src/Autheris.Api/Security/IdentityPipeline.cs` (`AddRolesAndRightsAsync`: issuer re-check, audit, ceiling)
- `src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.cs` (`ConfigureJwtBearer`: realm issuers/audiences and static signing keys for GenericOidc realms; confirm no existing per-realm path first)
- `src/Autheris.Domain/Audit/AuditEventTypes.cs`, `src/Autheris.Application/Audit/AuditPolicyTable.cs` (new FailClosed event)
- `src/Autheris.Api/Extensions/DependencyInjection/StartupChecks/ProductionStartupChecks.cs` (HTTPS/metadata rules next to line ~63)
- New `src/Autheris.Application/Identity/StaticJwksKeySource.cs` (or under `src/Autheris.Api/Security`)
- Tests as listed above under `tests/Autheris.Tests.Unit/Identity`, `tests/Autheris.Tests.Integration`, `tests/Autheris.Tests.Architecture`
