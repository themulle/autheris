# Open issues plan: security hardening, identity and transport (#469, #472, #482, #483, #484, #487, #489)

- **Date:** 2026-11-13 · **Base:** `origin/main` @ `10400ec4` · **Plan status:** PLANNED (Phase 2 done, Phase 3 pending)
- **Origin plan:** [`00-master-plan-overview.md`](00-master-plan-overview.md) · **WP specs:** [`simplification/README.md`](simplification/README.md) · **Binding owner decisions:** PRD [`2026-11-05-req-project-simplification.md`](2026-11-05-req-project-simplification.md) section 2.0 (OD-1..OD-45; a later OD wins)
- **Freeze:** PRD section 5.1. Every work package here is a security fix (no approval needed). No new options, endpoints or `*Enabled` flags are added (limits are constants), so no freeze ratchet grows.
- **PR limit:** <= 400 net added `src/` lines per PR (PRD 5.3). Disclosure rule SEC-SIMPL-01 applies: advisories are named only by GHSA id.

## Summary

| Issue | Verdict | WP / spec | Open on main | Size | Phase 3 |
|---|---|---|---|:---:|:---:|
| #469 | OBSOLETE-CLOSE | WP-SIMPL-36 sub-package 1 (PR #569) | nothing (Security trait on `SidFallbackTests` goes to #504) | - | no |
| #472 | NEW PLAN | WP-OI-SEC-ID-1 | readiness probe uncached and unthrottled | S | yes (light) |
| #482 | OBSOLETE-CLOSE | WP-SIMPL-43 PR 1 and PR 2 | nothing | - | no |
| #483 | PARTLY COVERED | Oracle TCPS: SEC-SIMPL-91 (done); rest WP-OI-SEC-ID-2, -3 | VerifyCA, governance TLS check unwired, implicit dialect exemptions, SHA-1 Redis pins, podman policy | S | yes |
| #484 | OBSOLETE-CLOSE | WP-SIMPL-38 PR 2 (PR #632) | nothing | - | no |
| #487 | PARTLY COVERED | Flight part: WP-SIMPL-16 (PR #540, deleted); cursor part WP-OI-SEC-ID-4 | keyset cursor key, format, caller binding | M | yes |
| #489 | PARTLY COVERED | WP-SIMPL-43 step 12 (PR 4); split as WP-OI-SEC-ID-5 | caller labels, CSRF scheme, read-only GET route test | S | yes |

Recommended order: WP-OI-SEC-ID-2 (governance TLS is enforced nowhere today) -> WP-OI-SEC-ID-4 -> WP-OI-SEC-ID-5 -> WP-OI-SEC-ID-1 -> WP-OI-SEC-ID-3. All independent of each other; none touches DDL.

## #469 Sid.GetUserSid falls back to app identifiers

- **Verdict:** OBSOLETE-CLOSE (fixed on main).
- **Evidence:** commit `8b0edb42` "remove app-id Sid fallback and static realm lookup" (merged via PR #569, WP-SIMPL-36 sub-package 1, PR 1a). `src/Autheris.Domain/Common/Sid.cs:22` (`GetUserSid`: app identifiers never used, absent subject yields null) and `:74` (`GetUserIdentifiers`/`GetAllUserSids` exclude `appid`/`client_id`/`azp`); no `RealmLookup` symbol remains in `src/`. Tests: `tests/Autheris.Tests.Unit/Identity/SidFallbackTests.cs` (`GetUserSid_without_sub_or_oid_does_not_use_client_id`, `GetAllUserSids_excludes_app_identifiers`, `RealmLookup_is_injected_not_static`), `ConsentDecisionGuardTests.W11_AzpOnlyTokenWithScopeButWithoutPersonIdentifier_IsDenied`.
- **Residual:** `SidFallbackTests` carries no `[Trait("Category","Security")]`; added by the #504 trait sweep (WP-SIMPL-36 sub-package 8), not a reason to keep #469 open.

## #472 /health/ready is anonymous, uncached and exempt from rate limiting

- **Verdict:** NEW PLAN (WP-OI-SEC-ID-1). Assessment: QUICK-WIN, security lane; the SQLite lock-contention part is obsolete (OD-1: `GovernanceProviderGuard.UnsupportedProviderMessage` accepts PostgreSQL and SQL Server only).
- **Verified state:** `src/Autheris.Api/Endpoints/HealthEndpoints.cs:54-70` calls `IGatewayHealthCheckService.CheckHealthAsync` on every request; `src/Autheris.Infrastructure/Health/GatewayHealthCheckService.cs:41-134` pings the governance repository (`PingAsync`) and Redis each time. `/health` is exempt in `RateLimitingMiddleware.cs:46,132,192`, `ResourceGroupMiddleware.cs:50` and `HostAllowListMiddleware.cs:18`.
- **Root cause:** a cheap anonymous probe is backed by an expensive, unbounded dependency check.
- **Design:** `ReadinessProbe` (new, `src/Autheris.Api/Endpoints/Health/ReadinessProbe.cs`, singleton) wraps `CheckHealthAsync` with single-flight and a fixed 2 s freshness window (constant, no option). The shared probe runs under its own 5 s timeout, never under the first caller's `CancellationToken`; callers await it with their own token. Healthy and unhealthy reports are cached for the window; an exception is reported as unhealthy and not cached beyond the window. The draining check (`ITrafficDrainController.IsDraining`) stays uncached and runs before the probe. `/health/live` is unchanged. No per-IP limit (Q-SECID-1).
- **Work packages:** WP-OI-SEC-ID-1, 1 PR, ~80 src.
- **Tests (TDD):** `ReadinessProbeTests` (Unit, `[Trait("Category","Security")]`): `Parallel_calls_within_window_run_one_probe`, `Probe_runs_again_after_window`, `Caller_cancellation_does_not_cancel_shared_probe`, `Probe_exception_yields_unhealthy_and_is_not_sticky`, `Draining_returns_503_without_probe`. Integration `HealthReadyEndpointTests.Anonymous_burst_causes_bounded_dependency_pings` (counting fake repository).
- **Dependencies:** none. **Risks:** a 2 s stale "healthy" after a dependency failure delays a readiness flip by at most 2 s (acceptable for probes with >= 5 s period).
- **Size:** S (~80 src, ~150 test, 1 PR).

## #482 ForwardAuth tenant prefix; admin IP allowlist middleware never registered

- **Verdict:** OBSOLETE-CLOSE (both items fixed on main).
- **Item 1 evidence:** WP-SIMPL-43 PR 2 (`82c1ab3a`, merged via PR #594): `src/Autheris.Api/Security/ForwardAuthAuthenticationHandler.cs:300-317` keeps a tenant-prefixed role only when the trusted header tenant is resolved and equals the prefix (`tenant_prefix_untrusted`, `tenant_mismatch` discards); administrative roles are never accepted from headers (`IsHeaderForbiddenRole`). Tests `tests/Autheris.Tests.Unit/Security/ForwardAuthHardeningTests.cs` (class-level Security trait): `Prefixed_role_for_other_tenant_is_discarded`, `Prefixed_role_without_trusted_tenant_is_discarded`.
- **Item 2 evidence:** `AdminIpAllowlistMiddleware.cs` and `DiagnosticsEndpointAuthMiddleware.cs` deleted by `1e5301f5` (WP-SIMPL-43 PR 1, "delete dead auth code"), as the assessment required (delete, not wire).

## #483 Transport TLS and supply-chain hardening

- **Verdict:** PARTLY COVERED. Item 2 (Oracle) is fixed; items 1, 3, 4 and a new finding are planned here.
- **Verified state per item:**
  1. PostgreSQL `VerifyCA` accepted: `src/Autheris.Infrastructure/Persistence/ConnectionTlsPolicy.cs:34-40` (data sources). **New finding:** `ConnectionTlsPolicy.ValidateGovernanceConnectionTls` (`:70`) has no production call site (only `tests/Autheris.Tests.Unit/Security/ConnectionPolicyTests.cs`); `src/Autheris.Api/Configuration/GovernanceProviderGuard.cs:41-44` validates provider and presence only and discards `env`. The governance database connection TLS is therefore not enforced at startup outside Development, although WP-SIMPL-11 step 4 (SEC-SIMPL-14) specified it.
  2. Oracle: fixed. TCPS enforced at startup (`GatewayStartupValidator.ValidateDataSourceSecrets`, doc `:399-402`) and on connection (`src/Autheris.Infrastructure/Persistence/OracleConnectionSupport.cs:57`), SEC-SIMPL-91. Residual: `ConnectionTlsPolicy.Validate` `default:` returns null (`:61-62`), so Oracle, Databricks and SQLite pass this validator implicitly instead of by an explicit rule.
  3. Redis pins: `src/Autheris.Infrastructure/Cache/RedisConnectionSecurity.cs:104-105` accepts 40-hex (SHA-1) pins; `:136` compares the SHA-1 thumbprint.
  4. `deploy/policy/podman/policy.json:16` `"rekorPublicKeyData": ""`; `:19-35` `""` docker scope, `dir` and `tarball` with `insecureAcceptAnything`. `SupplyChainAndDeploymentSecurityTests.PolicyAndAdmissionConfigs_Exist_AndAreConfigured` checks only file existence.
- **Root cause:** policy helpers exist but are partly unwired or permissive by default; legacy formats kept.
- **Design:**
  - WP-OI-SEC-ID-2 (TLS policy): `GovernanceProviderGuard.ThrowIfUnsupported` calls `ConnectionTlsPolicy.ValidateGovernanceConnectionTls(provider, cs, env.IsDevelopment())`; a null `env` is treated as non-Development (fail closed). PostgreSQL accepts only `SslMode.VerifyFull` outside Development, for data sources and governance (Q-SECID-2). `Validate` replaces `default: return null` by an exhaustive switch: Oracle delegates to the existing `OracleConnectionSupport` TCPS check (one rule, two call sites), SQLite returns null with a comment (local file, data-source dialect only), Databricks: the developer verifies whether the driver connection can be non-TLS; if yes require TLS, otherwise an explicit exemption with comment; an unknown future enum value returns an error (fail closed).
  - WP-OI-SEC-ID-3 (pins and image policy): `RedisConnectionSecurity` accepts only 64-hex SHA-256 pins (startup error naming the setting for 40-hex values) and drops the SHA-1 comparison; a matching pin still overrides chain and name errors (Q-SECID-3). `policy.json`: remove the `""` docker scope and the `dir`/`tarball` `insecureAcceptAnything` entries (default `reject` applies), fill `rekorPublicKeyData` (Q-SECID-4).
- **Work packages:** WP-OI-SEC-ID-2 (~90 src, 1 PR); WP-OI-SEC-ID-3 (~20 src + policy file, 1 PR). Security fixes; breaking config changes accepted (no production).
- **Tests (TDD, `[Trait("Category","Security")]`):** `ConnectionPolicyTests`: `PostgreSql_VerifyCA_rejected_outside_development` (data source and governance), `PostgreSql_VerifyFull_accepted`, `Oracle_without_tcps_rejected_by_tls_policy`, `Every_dialect_has_explicit_tls_rule` (enumerates `DatabaseDialect`). `GovernanceProviderGuardTests`: `Governance_connection_without_tls_fails_startup_outside_development`, `Null_environment_is_treated_as_production`, `Development_allows_plain_connection`. `RedisConnectionSecurityTests`: `Sha1_pin_rejected_at_startup`, `Sha256_pin_matches`, `Sha1_thumbprint_of_presented_certificate_never_matches`. `SupplyChainAndDeploymentSecurityTests.PodmanPolicy_has_no_insecureAcceptAnything_and_a_rekor_key`.
- **Dependencies:** none; WP-SIMPL-11 is merged, so this closes its unfinished SEC-SIMPL-14 wiring. Integration test hosts run as `Development` (22 of 25 `UseEnvironment` calls); the 3 `Production`/`Staging` hosts must get a TLS-valid or Development governance configuration.
- **Risks:** operators with `VerifyCA` or SHA-1 pins fail at startup after upgrade (intended; message names the setting, never the value). Removing the `""` podman scope rejects every other image on hosts that use this file as system policy (documented as Autheris-only sample).
- **Size:** S (~110 src, ~200 test, 2 PRs).

## #484 Read-only tokens cannot use the Trino statement protocol or cancel

- **Verdict:** OBSOLETE-CLOSE (fixed on main).
- **Evidence:** `48af7e64` "read-only token query execution and statement cancellation (WP-SIMPL-38 PR 2, #484)" (PR #632). `src/Autheris.Api/Middleware/ReadOnlyTokenMiddleware.cs:31` (`/v1/statement` query path), `:76` DELETE branch limited to exact statement routes (`:124-131`), `:94-97` POST only on `/v1/statement`; tests `ReadOnlyTokenStatementTests` (permitted paths, sub-path rejection, traversal, method-override probing).

## #487 Signing-key and cursor hardening (Flight tickets, keyset cursors)

- **Verdict:** PARTLY COVERED. Flight ticket items are obsolete: `ArrowFlightSqlServer` deleted by `2afd1c21` (WP-SIMPL-16, PR #540); no Flight ticket code remains in `src/`. Keyset cursor and GraphQL caller items: NEW PLAN (WP-OI-SEC-ID-4); the assessment put them in the security lane, but no WP-SIMPL-36 sub-package specifies them.
- **Verified state:** `src/Autheris.Application/DataSources/Services/KeysetCursorEncoder.cs:23-44` falls back to a static per-process random key when `RestApi:BulkStream:SigningKey` is empty (three constructors); the cursor has no version, key id, issued-at or expiry (canonical fields are already length-prefixed, `:399`). `BulkStreamSigningKeyOptionsValidator.cs:37` requires the key only while the bulk stream endpoints are enabled, but the encoder is also used by GraphQL connections (`src/Autheris.GraphQL/Catalog/ConnectionGraphQlTypes.cs:144`, `?? new KeysetCursorEncoder()`) and `GovernedTableReadPlanner.Keyset.cs:37` (same fallback). Caller binding in `ConnectionGraphQlTypes.cs:303-325` uses `ClaimTypes.NameIdentifier ?? Identity.Name` and a `ContextData["TenantId"]` fallback instead of `GetUserSid()` and the request tenant.
- **Root cause:** the cursor key was scoped to one feature switch while the encoder serves several transports; cursor identity predates the canonical SID (WP-SIMPL-43 PR 3a/3b).
- **Design:**
  - PR A (key): rename the validator scope: the keyset key is required outside Development whenever the gateway runs (GraphQL connections are always available), independent of the bulk switch; minimum 32 bytes (existing `MinimumBytes`). Remove the `new KeysetCursorEncoder()` fallbacks (DI only); the ephemeral key is created only by the Development registration, never as a static field.
  - PR B (format and binding): cursor v2 = `v2|kid|iat|...` signed; `kid` = first 8 hex of SHA-256 of the key; reject unknown version, unknown `kid`, and `iat` older than a fixed 24 h (constant, Q-SECID-5) with typed error `cursor.invalid` (no detail on which check failed). GraphQL connection binding uses `principal.GetUserSid()` and the resolved request tenant (`ITenantContext`); no SID -> GraphQL authorization error (deny), the `ContextData` tenant fallback is deleted. v1 cursors are rejected (breaking, accepted).
- **Tests (TDD, `[Trait("Category","Security")]`):** `KeysetCursorKeyValidatorTests`: `Missing_key_outside_development_fails_startup_even_when_bulk_stream_disabled`, `Short_key_fails_startup`, `Development_uses_ephemeral_key_with_warning`. `KeysetCursorEncoderV2Tests`: `V1_cursor_rejected`, `Unknown_key_id_rejected`, `Expired_cursor_rejected`, `Tampered_iat_does_not_verify`, `Field_boundary_collision_does_not_verify`, `Error_does_not_reveal_failed_check`. `ConnectionCursorCallerBindingTests`: `Cursor_bound_to_canonical_sid`, `Cursor_of_other_user_with_same_display_name_rejected`, `Principal_without_sid_is_denied`, `Context_data_tenant_is_ignored`. Extend `BulkKeysetCursorEncoderTests` for the REST path (same v2 format).
- **Dependencies:** WP-SIMPL-43 PR 3b (merged; canonical SID). Independent of WP-SIMPL-51 I4 (own continuation token).
- **Risks:** open paging sessions break at deployment and after key rotation (Q-SECID-6); a clock skew between nodes > minutes would only shorten the 24 h window.
- **Size:** M (~180 src, ~300 test, 2 PRs).

## #489 Auth hardening: caller labels, revocation, CSRF, ForwardAuth SID, read-only GET

- **Verdict:** PARTLY COVERED by [WP-SIMPL-43](simplification/WP-SIMPL-43-authentication-authorization-core.md) step 12 (PR 4).
- **Fixed on main:** ForwardAuth SID charset and length (`ForwardAuthAuthenticationHandler.IsValidUsername`, `:364`, ASCII `[A-Za-z0-9._@\-\\]`, 1..256; tests `Username_outside_charset_or_length_fails`, `Unicode_username_cannot_collide`; WP-43 PR 2). Privileged-subject check on all schemes (`src/Autheris.Api/Security/IdentityPipeline.cs:216-222` via `SidFormat.IsPrivileged`; `JwtSocketTokenValidator` no longer has its own check; WP-43 PR 3b, PR #622). `X-Break-Glass-Justification` parsing deleted (no reference in `src/`; OD-5). In-memory revocation: `ProductionStartupChecks.cs:181-184` (NF-HA-02) fails startup with `MultiNodeClusterMode` or `Replicas > 1` without Redis.
- **Open on main:** caller labels are normalized (`CallerLabelNormalizer`, length caps 64/32/64) but not flagged as client-asserted; audit writes `caller.app/ver/query` without `clientId` or an asserted flag (`src/Autheris.Domain/Model/GovernanceModels.cs:112-117`); lineage consumer folding groups by the asserted label (`ConsumerFolding.cs:48`). CSRF: `GatewayCorsCsrfMiddleware.cs:115` `IsAllowed(originUri.Authority)` ignores the scheme. Read-only tokens pass every GET/HEAD/OPTIONS (`ReadOnlyTokenMiddleware.cs:69-71`) with no route-enumeration test.
- **Gap and plan decision D-1 (default taken):** WP-43 PR 4 bundles step 12 with the Envoy alignment (step 11), which waits for WP-SIMPL-46 PR 3 (not merged). Step 12 has no dependency on WP-46; it is split out as **WP-OI-SEC-ID-5 = WP-43 PR 4a** and can merge now; PR 4 keeps step 11 only. Scope addition to step 12: lineage consumer views show the asserted label with an "asserted" marker and group by `clientId` when present.
- **Tests (`[Trait("Category","Security")]`):** `CsrfOriginSchemeTests.Http_origin_rejected_for_https_gateway`, `Same_scheme_origin_accepted`; `CallerLabelProvenanceTests.Audit_record_separates_asserted_label_and_token_client_id`, `Usage_key_never_uses_asserted_label`; architecture test `ReadOnlyTokenGetRouteTests.Every_get_head_route_is_in_side_effect_free_allowlist` (checked-in allowlist; a new GET route fails until reviewed).
- **Size:** S (~150 src, ~200 test, 1 PR). The revocation item needs no further work (a node that hides its replica count from the configuration cannot be detected; documented residual).

## Open owner questions

| Id | Question | Recommended default |
|---|---|---|
| Q-SECID-1 | #472: add a per-IP rate limit to `/health/ready` in addition to caching? | No. Caching bounds dependency load to one probe per 2 s per node; an IP limit can 429 kubelet probes and adds an option (freeze ratchet). |
| Q-SECID-2 | #483: accept PostgreSQL `VerifyCA` outside Development? SEC-SIMPL-14 allowed it "with documented reason". | No: `VerifyFull` only for data sources and governance, no escape option (revises SEC-SIMPL-14; not in production yet). |
| Q-SECID-3 | #483: may a matching Redis pin keep overriding chain and host-name errors? | Yes (the pin is the trust anchor for self-signed Redis); only SHA-1 pins are removed. |
| Q-SECID-4 | #483: source of `rekorPublicKeyData` and scope of `policy.json`. | Embed the Sigstore public-good Rekor public key; policy covers the Autheris image only (all other scopes `reject`); operators merge their own scopes. |
| Q-SECID-5 | #487: keyset cursor maximum age and whether it is configurable. | Fixed 24 h constant, no option. |
| Q-SECID-6 | #487: accept cursors signed with a previous key during rotation? | No; rotation invalidates open cursors (`cursor.invalid`, client restarts paging). Revisit only on demand (would add an option). |

## Phase 3 review scope

- WP-OI-SEC-ID-2 (governance and data-source TLS policy, fail-closed on unknown dialect and null environment) - required.
- WP-OI-SEC-ID-4 (cursor key requirement, v2 format, canonical SID binding) - required.
- WP-OI-SEC-ID-5 (WP-43 PR 4a: CSRF scheme, caller label provenance, read-only GET allowlist) - required; confirm plan decision D-1 (split from PR 4).
- WP-OI-SEC-ID-1 (readiness single-flight) - light review (DoS bound, no cached auth data).
- WP-OI-SEC-ID-3 (SHA-256 pins, image policy) - light review with Q-SECID-3/4.
- #469, #482, #484: no review; the owner can close them with the evidence above.

## Phase 3 result (2026-11-13, group 1)

Review: [`2026-11-13-sec-issues-group-1.md`](2026-11-13-sec-issues-group-1.md). Binding conditions `SEC-OI-<issue>-<n>` apply to Phase 4.

| Work package | Verdict | Developer-ready |
|---|---|---|
| WP-OI-SEC-ID-2 (#483 TLS policy) | APPROVED WITH CONDITIONS (SEC-OI-483-1..4) | yes |
| WP-OI-SEC-ID-3 (#483 pins, image policy) | APPROVED WITH CONDITIONS (SEC-OI-483-5) | yes |
| WP-OI-SEC-ID-4 (#487) | APPROVED WITH CONDITIONS (SEC-OI-487-1..4) | yes |
| WP-OI-SEC-ID-5 (#489, WP-43 PR 4a; split D-1 confirmed) | APPROVED WITH CONDITIONS (SEC-OI-489-1..4) | yes |
| WP-OI-SEC-ID-1 (#472) | APPROVED WITH CONDITIONS (SEC-OI-472-1..3) | yes |

#469, #482, #484 need no review. #482 and #484 remain closable with evidence.

## Owner decisions (2026-11-13)


The owner confirmed the recommended default of every open question (`Q-*`) in this file. The defaults in the question tables are binding for the implementation; a later owner decision wins. Phase 3 (security review) still applies before implementation starts.
