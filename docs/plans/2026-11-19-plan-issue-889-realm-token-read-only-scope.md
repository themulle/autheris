# Plan: GenericOidc realm tokens honour read-only scopes (Issue #889)

Phase 2 (solution architect), 2026-11-19. Code facts verified on `origin/main` `e3e32b401`. Implementation starts after the v1.9.0 tag.

## 1. Problem and evidence

- Issue: [#889](https://github.com/themulle/autheris/issues/889). Found in the Phase 5 review of PR #886.
- `src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.cs:313-347` `ConfigureGenericOidcJwtBearer` sets issuer, audience, lifetime and key validation but registers no `Events.OnTokenValidated`. So no scope rule runs for a realm scheme (one scheme per GenericOidc realm with `SubjectRoles` or `Jwks`, lines 280-300).
- The default schemes do run one: `GatewayServiceCollectionExtensions.cs:430-446` calls `EntraTokenPolicy.Apply`, which marks delegated tokens whose scopes are all read-only scopes with `TokenAccessScope.MarkReadOnly` (`src/Autheris.Api/Security/EntraTokenPolicy.cs:105-112`).
- Correction to the issue text: `AdfsTokenPolicy` (`src/Autheris.Api/Security/AdfsTokenPolicy.cs`, 38 lines) only checks the client id. It applies no read-only scope rule. Only Entra does.
- `TokenAccessScope.GetScopes` reads only `scp` and the Microsoft scope URI (`src/Autheris.Domain/Security/TokenAccessScope.cs:20-24`). It does not read the RFC 9068 `scope` claim that most generic OIDC providers send.
- Effect: a realm token with only a read scope keeps the write permissions of its configured `SubjectRoles` roles. `ReadOnlyTokenMiddleware` lets the write through. DML is already gated by `HasScope(WriteScope)` (`src/Autheris.Api/Endpoints/DmlRequestGuard.cs:261`), so the gap is non-DML writes such as admin and governance endpoints.

## 2. Options

| Option | Description | Verdict |
|---|---|---|
| A | Per-realm `ReadOnlyScopes` list (default `["Agent.Read", "App.Read"]`, as Entra). A new `RealmTokenScopePolicy` runs in `OnTokenValidated` of each realm scheme. It reads `scp` and `scope` and marks the token read-only when every scope is a read-only scope. | **Recommended**: same rule as Entra, minimal surface. |
| B | Realm tokens are read-only unless they carry the write scope (`TokenAccessScope.WriteScope`). | Rejected for now: it breaks client-credential workload tokens that carry no scope. Can be a later opt-in. |
| C | Reuse `EntraTokenPolicy` for realms. | Rejected: it also applies Entra-only checks (ID tokens, `azp`/`appid`, app-only roles). |

## 3. Decisions

1. D-889-1: `IdentityRealmConfigOptions` (`src/Autheris.Domain/Options/Security/IdentityRealmsOptions.cs:18`) gets `List<string>? ReadOnlyScopes`. Null means the default `["Agent.Read", "App.Read"]`. An empty list turns the rule off and triggers a startup warning.
2. D-889-2: `TokenAccessScope.GetScopes` also reads the space-separated `scope` claim. Values are de-duplicated by bare name (`ScopeNames.ToBareName`). This applies to every scheme. Entra tokens do not carry `scope`, so their behaviour does not change.
3. D-889-3: A token with no scope claim at all is not marked (default taken, see Q1). Its roles come from operator configuration only.
4. D-889-4: The policy only adds the read-only marker. It never rejects a token, so the failure mode is "fewer permissions" (fail closed).

## 4. Work packages

**WP-889-1 (S, about 120 LOC including tests): realm scope policy**
- New `src/Autheris.Api/Security/RealmTokenScopePolicy.cs` with `internal static void Apply(ClaimsPrincipal principal, IdentityRealmConfigOptions realm)`. It marks the token read-only when its scope set is not empty and every scope is in the effective `ReadOnlyScopes`. It uses the same comparison as `EntraTokenPolicy.IsSameScope`; move that helper into `TokenAccessScope` as `internal static bool IsSameScope(string a, string b)`.
- `ConfigureGenericOidcJwtBearer`: set `options.Events = new JwtBearerEvents { OnTokenValidated = ctx => { RealmTokenScopePolicy.Apply(ctx.Principal!, realm); return Task.CompletedTask; } }`.
- `TokenAccessScope.ScopeClaimTypes`: add `"scope"`. `GetScopes` splits on spaces and de-duplicates.
- Add `IdentityRealmConfigOptions.ReadOnlyScopes` with the effective-list helper `GetEffectiveReadOnlyScopes()`.
- Unit tests: new `tests/Autheris.Tests.Unit/Identity/RealmTokenScopePolicyTests.cs`. The `[Trait("Category","Security")]` cases are `ReadScopeOnly_IsMarkedReadOnly`, `ScopeClaimRfc9068_IsRead`, `MixedReadAndWriteScopes_NotMarked`, `NoScopeClaim_NotMarked`, `EmptyReadOnlyList_NotMarked`, and `RealmScheme_RegistersOnTokenValidated`. Extend the existing `TokenAccessScope` tests with `GetScopes_ReadsScopeClaim`.
- Update `tests/Autheris.Tests.Unit/DependencyInjection/Snapshots/composition-*.txt` only if the snapshot captures the options shape.

**WP-889-2 (S): end-to-end security test**
- Extend `tests/Autheris.Tests.Integration/Security/WorkloadIdentitySubjectRoleTests.cs` with `RealmToken_ReadOnlyScope_WriteEndpoint_Returns403` (`[Trait("Category","Security")]`). Use the static JWKS realm already in that fixture, with an admin-role subject that carries `scope: "Agent.Read"`. A POST to a governance write endpoint must return 403 from `ReadOnlyTokenMiddleware`.
- Hydrogen compiles the integration project locally. GitHub Actions runs it.

Order: WP-889-1, then WP-889-2. Both can ship in one PR. No DDL.

## 5. Integration test impact

- `tests/Autheris.Tests.Integration/Security/WorkloadIdentitySubjectRoleTests.cs`: new case. Existing cases use tokens without a scope claim, so they are unaffected (D-889-3).
- Check `git grep -n '"scope"' tests/Autheris.Tests.Integration`. Tests that put a `scope` claim on Entra or AD FS test tokens and expect writes to pass need a write scope added.

## 6. Phase 3

**Required.** The change touches authentication and the read-only marker. No SEC conditions are set here.

## 7. Owner questions (recommended defaults)

1. Q1: Should a realm token with no scope claim be read-only? Default: **no**. Workload tokens from client credentials often have no scope. Option B can become an opt-in later.
2. Q2: Should the AD FS scheme get the same rule? Default: **yes, as a follow-up issue**. Keep it out of this PR.
