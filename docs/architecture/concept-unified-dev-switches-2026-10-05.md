# Concept: Unified Development Switches (`Gateway:Dev`)

Date: 2026-10-05 · Status: **Implemented (phases 0–3), phase 4 deferred** · Related: F-AUTH-DX, ADR-016 (security switch semantics of 2026-10-02)

## 1. Findings

Development behaviour used to be controlled by five independent mechanisms. To know what differs in a development instance, one had to know all five.

| # | Mechanism | Examples | Problem |
|---|---|---|---|
| 1 | `Insecure.*` (global, `warn_` / `danger_`) | `danger_allow_anonymous_access`, `warn_disable_rate_limiting` | Good: clear classification. But 18 of its 20 flags also exist as domain-local duplicates. |
| 2 | Domain-local duplicates | `GraphQL.warn_enable_introspection`, `Itsm.danger_allow_untrusted_certificates`, `Mcp.danger_bypass_mcp_auth` | Each flag has two to five sources, combined with `\|\|` in more than 20 accessors of `GatewayOptions`. |
| 3 | `Profile: Quickstart` | open CORS, introspection, OpenSchema, auto-approve | One name stood for four implicit relaxations, hidden in `\|\| IsQuickstartProfile` inside four accessors plus direct `Profile` checks. |
| 4 | Scattered `IsDevelopment()` checks | 23 places in `Api/Extensions`, `isDevOrTest` in the SQLite repository | The rule "Development only" is re-implemented at every place. |
| 5 | Convenience as separate flags | `EnableTestAuthHandler`, `SeedDemoData`, `EnableBananaCakePop`, `BasicAuth.Session.AllowedEnvironments`, `AllowDevelopmentInContainer` | Each flag had its own location, validation and documentation. |

Consequences:

- **Docs and code drifted.** `configuration-guide.md` listed `danger_allow_anonymous_queries` and `warn_bypass_query_cost_limits`; the code uses `danger_allow_anonymous_access` and `warn_relaxed_query_limits`. An error hint referred to `GettingStarted:Profile` while the code reads `Gateway:Profile`.
- **A name does not tell the class.** Several `warn_*` flags are classified DANGER (`warn_disable_rate_limiting`, `warn_auto_approve_access_requests`).
- **Two different things shared one mechanism.** A security relaxation (rate limit off) and plain convenience (startup banner) were indistinguishable in configuration.
- **There was no overall view.** `X-Gateway-Insecure-Mode` and `/health` listed bypasses only.

## 2. Core idea: three classes, two configuration locations

The split is by effect, not by origin.

| Class | Effect | Allowed in Production? | Home |
|---|---|---|---|
| **A: dev convenience** | No security effect: banner, verbose errors, persona login, info endpoint, dev database persistence, demo data | No (startup error) | `Gateway:Dev` |
| **B: dev security** | Changes who can authenticate or what is exposed: `TestAuthHandler`, GraphQL introspection | No (startup error) | `Gateway:Dev`, reported separately |
| **C: bypass** | Weakens a protection: `warn_*`, `danger_*` | `warn`: yes, logged. `danger`: no | `Gateway:Insecure` (unchanged) |

Rules:

1. `Insecure` remains the only home of class C. `Gateway:Dev` contains no bypasses.
2. `Gateway:Dev` is hard-limited to Development. A value that deviates from the default outside Development is a startup error, not silently ignored.
3. Code never asks `IsDevelopment()` for a feature. It reads `DevFeatures` (everything is off outside Development). `IsDevelopment()` remains where the environment itself matters (HSTS, HTTPS redirect, validation).
4. A preset is a named set of concrete values, not hidden logic. What it expanded to is visible in `/api/dev/info`, the startup banner and `GetAllActiveBypasses`.

## 3. Configuration

```json
"Gateway": {
  "Dev": {
    "Preset": "Standard",           // Standard | Quickstart | Strict
    "Banner": true,
    "VerboseErrors": true,          // 403 diagnostics, problem+json with traceId
    "PersonaLogin": true,           // /api/dev/login, /api/dev/personas
    "Info": true,                   // /api/dev/info
    "DemoData": true,               // preset default; legacy: GovernanceDb.SeedDemoData
    "Persist": { "Enabled": false, "Directory": ".data" },
    "TestAuthHandler": true,        // class B; legacy: Authentication.EnableTestAuthHandler
    "Tooling": { "BananaCakePop": true, "Introspection": true }   // legacy: GraphQL.EnableBananaCakePop / EnableIntrospection (Introspection is class B)
  },
  "Insecure": { "warn_allow_all_cors_origins": false }            // unchanged (class C)
}
```

### Presets

| Preset | Meaning | Replaces |
|---|---|---|
| `Standard` | All convenience on, `TestAuthHandler`, introspection, Banana Cake Pop and demo data on, security otherwise strict | The previous Development behaviour |
| `Quickstart` | `Standard` plus the expanded relaxations `warn_allow_all_cors_origins`, `warn_enable_introspection`, `warn_auto_approve_access_requests` and `OpenSchema` | `Profile: Quickstart` (kept as an alias) |
| `Strict` | Like `Standard`, but no `TestAuthHandler` and no introspection. For reproducing production-like access | new |

## 4. Implementation

The design is realised in three small pieces.

**`DevConfiguration.Apply` (before binding).** In Development, `Gateway:Dev` is resolved into the underlying option keys before the options are bound. Preset defaults and the Quickstart expansion are inserted as the lowest-priority configuration source, so an explicit value from any source always wins. The legacy keys therefore remain the internal representation: existing consumers, validations and tests are untouched, and nothing outside `DevConfiguration` needs to know about presets.

Precedence per switch: explicit `Gateway:Dev:*` value, then an explicitly set legacy key (deprecated alias, logged as a note at startup), then the preset default. A `Dev` value that contradicts its legacy key is a startup error. Outside Development nothing is expanded.

| Dev key | Underlying key |
|---|---|
| `Dev:TestAuthHandler` | `Authentication:EnableTestAuthHandler` |
| `Dev:Tooling:BananaCakePop` | `GraphQL:EnableBananaCakePop` |
| `Dev:Tooling:Introspection` | `GraphQL:EnableIntrospection` |
| `Dev:DemoData` | `GovernanceDb:SeedDemoData` |
| `Dev:Preset=Quickstart` | `Insecure:warn_allow_all_cors_origins`, `Insecure:warn_enable_introspection`, `Insecure:warn_auto_approve_access_requests`, `OpenSchema` |
| `Dev:Persist:Enabled` | `GovernanceDb:ConnectionString` (forced override to `<Directory>/dev.db`) |

**`DevFeatures.Resolve(options, isDevelopment)`.** The single place with the environment check for the runtime features (banner, verbose errors, persona login, info, persistence). It also lists the active class B switches (`test_auth_handler`, `graphql_introspection`).

**`DevOptionsValidator`.** Rejects, outside Development, a non-default preset, `Persist:Enabled`, and `true` for `DemoData`, `TestAuthHandler`, `Tooling:BananaCakePop` and `Tooling:Introspection`. `Banner`, `VerboseErrors`, `PersonaLogin` and `Info` resolve to off outside Development and are therefore not an error. An unknown preset name is rejected everywhere.

**Transparency.**

- `/api/dev/info` shows the preset, the resolved features, the class B switches, the values the preset applied, deprecation notes and the active bypasses.
- The startup banner shows preset, database mode, class B switches and notes.
- The `X-Gateway-Insecure-Mode` header now also lists class B entries (`DEV-SECURITY:test_auth_handler`).

## 5. Migration

| Phase | Content | Behaviour change | Status |
|---|---|---|---|
| 0 | `Gateway:Dev` with `Banner`, `VerboseErrors`, `PersonaLogin`, `Info`, `Persist`; `DevFeatures`; `DevOptionsValidator`; doc drift fixed | none | done |
| 1 | Feature-related `IsDevelopment()` checks use `DevFeatures` | none | done for the new DX features; pre-existing checks (HSTS, validations) intentionally remain |
| 2 | `EnableTestAuthHandler`, `SeedDemoData`, `EnableBananaCakePop`, `EnableIntrospection` as aliases of `Gateway:Dev` with deprecation notes | notes only | done |
| 3 | `Profile: Quickstart` becomes `Dev:Preset`; expansion into `Insecure.*`; `\|\| IsQuickstartProfile` removed from the four accessors | Quickstart now also lists `OpenSchema` as a DANGER entry (it was relaxed before, but not reported) | done |
| 4 (optional, breaking) | Remove the domain-local `warn_` / `danger_` duplicates | yes: deployments and environment variables | **deferred** |

Phase 4 is replaced for now by an architecture test (see 6.5): the 23 domain-local duplicates that exist today are an allowlist that may only shrink. The risk of phase 4 lies in Helm charts and deployments that set the domain-local names today.

**Operational rule for the base file.** `appsettings.json` must not set the legacy keys that `Gateway:Dev` owns (`EnableTestAuthHandler`, `EnableBananaCakePop`, `EnableIntrospection`, `SeedDemoData`). An explicit value counts as "explicitly configured" and would defeat the preset default in Development. Their C# defaults are `false` / unset, so nothing changes for other environments. A unit test guards this.

## 6. Rules for new switches

1. **Does it weaken a protection?** Then class C: add it to `Insecure` with the `warn_` or `danger_` prefix, add it to `GetAllActiveBypasses`, add a test. No domain-local duplicate.
2. **Does it change authentication or exposure but is meant for development only?** Class B: `Gateway:Dev`, listed in `DevFeatures.DevSecurity`.
3. **Otherwise** class A: `Gateway:Dev`, default `true` in Development.
4. No switch reads `IsDevelopment()` directly. Every dev switch has a policy in `DevOptionsValidator` and appears in `/api/dev/info`.
5. Architecture test (`DevSwitchTests`): `warn_` / `danger_` properties outside `InsecureGettingStartedOptions` are limited to today's allowlist. A second test fails when a `DevOptions` property has no explicit "outside Development" policy.

## 7. Alternatives considered

| Alternative | Advantage | Why not |
|---|---|---|
| Everything in `Gateway:Dev`, including bypasses | One place | Blurs convenience and security relaxation. `warn` in Production could no longer be expressed. Breaks the principle of 2026-10-02. |
| Documentation only | No risk | Does not give an overall view or per-feature switches. |
| Several profile files (`appsettings.Development.Quick.json`) | Simple | The effective configuration stays invisible and "Development only" is not enforced. |
| Environment variables only | One-liner in containers | Not validatable or listable. They still work (`Gateway__Dev__Persist__Enabled`). |

## 8. Decisions taken

1. `Profile: Quickstart` is replaced by `Dev:Preset` and kept as a deprecated alias.
2. `TestAuthHandler` and introspection are class B (hard Development-only, reported separately), not bypasses.
3. Phase 4 is deferred; the architecture test replaces it.
4. The section is named `Gateway:Dev`, matching `/api/dev/*`.
5. `AllowDevelopmentInContainer` stays a class C (`WARN`) entry; `Gateway:Dev` needs no additional consent in containers.

## 9. Non-goals

- No change to the classification (`warn` / `danger`) of existing bypasses.
- No relaxation for Production. `Gateway:Dev` is a startup error there, not a no-op.
- No renaming of the `Insecure` properties.
- `GraphQL.EnableIntrospection` and `GraphQL.EnableBananaCakePop` stay regular options in every environment. Only the `Gateway:Dev:Tooling:*` aliases are limited to Development.
