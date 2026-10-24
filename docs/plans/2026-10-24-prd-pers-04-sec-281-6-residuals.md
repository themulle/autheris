# PRD: PERS-04 consent-extension residual and SEC-281-6 ingress admin exposure

Date: 2026-10-24. Phase 1. Owner decisions of 2026-10-24 are inputs. Autheris is not in production; breaking behaviour is allowed.
Refs: `2026-10-22-plan-security-pers.md` (PERS-04, line 84), `security.txt` SEC-SC-20, `2026-10-23-plan-issue-281-app-scoped-trusted-documents.md` (SEC-281-6 gap).

## Item 1: `ExtendConsentExpiryAsync` rejects non-active consents

### Problem
`ExtendConsentExpiryAsync` (SQLite/PostgreSql/SqlServer, `AttributeValidatingConsentRepository`, `FakeConsentRepository`) accepts expired, revoked or withdrawn consents. This allows silent resurrection of a lapsed or revoked grant.

### Decision and new contract
Extension is only for active consents. Renewal after expiry (or after revoke/withdraw) requires a new consent (grant or replace), never extension. This supersedes the SEC-SC-20 "renewal after expiry" contract.

### Requirements
1. Eligible: status active, not revoked, not withdrawn, `valid_to > now` (server `TimeProvider`, never client time). Everything else is rejected.
2. Atomic: the eligibility condition is part of the `UPDATE ... WHERE` (id, tenant, active status, `valid_to > @now`); 0 rows affected maps to the rejection. No read-then-write.
3. New expiry must be strictly later than the current `valid_to` and within the existing max-duration limits (same clamp/limit source as grant; no new limit constants).
4. Stable domain error code (proposed `CONSENT_NOT_EXTENDABLE`; one code for expired, revoked, withdrawn, unknown state; not-found keeps its current code). Added to `WhitelistedSafeCodes` in `ErrorSanitizingFilter` with a fixed client message ("The consent cannot be extended. Create a new consent."). No state, timestamps or ids in the message.
5. Applied in all three providers, the decorator (delegates, does not weaken) and the fake (same semantics, so unit tests are faithful).
6. Callers: `ConsentRecertificationWorkflowService` surfaces the code (no silent fallback to extension); GraphQL/REST map through the sanitizer.
7. Audit: write a rejection audit event only if other consent rejections are audited today (architect confirms; reuse the same event shape, no extra data). Rejections must not alter the consent row or `renewal_count`.
8. OpenMetadata sync: when the synced consent is expired/non-active, create or replace a new consent (existing replace path, same `valid_to` policy as the first sync) instead of extending, and mark it "default taken" (existing marker convention). Active synced consents still use extension. Rejection by a race (expires between read and extend) falls back to one replace attempt, not a retry loop.
9. Tests: `SEC_SC_20_Renewal_RestartsTheClampPeriod_AtDay150_AndAfterExpiry` is split. The day-150 part stays (active consent renewal restarts the clamp). The "after expiry" part is inverted to assert rejection with the stable code and an unchanged row.

### Acceptance criteria
- AC-1: Extending an expired consent fails with the stable code on SQLite, PostgreSql and SqlServer (shared contract test base); row unchanged.
- AC-2: Same for revoked and withdrawn consents.
- AC-3: Extending an active consent to a later date within limits succeeds; an equal/earlier date or one beyond max duration is rejected.
- AC-4: Eligibility is a condition of the UPDATE (a test with a time provider that crosses expiry between check and write cannot extend).
- AC-5: The code is in the `ErrorSanitizingFilter` whitelist; a GraphQL and a REST response carry only the fixed message.
- AC-6: Recertification workflow returns the rejection for an expired consent without extending it.
- AC-7: OpenMetadata sync with an expired synced consent creates/replaces a new consent, marks "default taken", never calls extend on it; with an active one it still extends.
- AC-8: The audit rejection event exists iff the architect confirms the existing convention; no payload beyond the convention.
- AC-9: SEC_SC_20 tests updated as in requirement 9; security plan line 84 and `security.txt` PERS-04/SEC-SC-20 updated to the new contract (progress notes only in `docs/plans/`).

## Item 2: SEC-281-6 default ingress must not expose `/api/admin`

### Current state
The chart has `ingress.blockAdminPath` (default true): a `/api/admin` Prefix rule to the endpoint-less `<fullname>-admin-blocked` Service, ahead of the `/` catch-all. Gaps: Prefix paths are case-sensitive in nginx and Traefik while ASP.NET routing is case-insensitive, so `/API/Admin/...` reaches the gateway; behaviour on trailing-slash and encoded-slash variants is unverified; the default class is Traefik, nginx-ingress is not covered; the flag name does not match the owner's opt-in model.

### Benchmark (short)
- Explicit public-path allowlist without `/`: robust (unknown paths get the default backend 404, case variants fail to match), but the public surface is wide (`/api/v1`, `/api/governance`, `/odata`, `/v1`, `/scim`, `/portal`, `/graphql`, `/datasets`, ...) and drifts on every new endpoint; breaks silently. Rejected as default.
- Deny rule with nginx `use-regex: "true"`: nginx-ingress then renders all paths of the host as case-insensitive regex locations (`~*`), so one `/api/admin` deny path also catches case variants; nginx matches on the decoded, normalized URI, so `%2F` and `//` variants collapse to the same prefix. No server-snippet needed (snippets are often disabled by `allow-snippet-annotations=false`). Recommended for nginx.
- Traefik: Prefix is case-sensitive, no snippet-free case-insensitivity in the Ingress API; keep the deny rule and document the caveat; app enforcement is the real control.
- Negative-lookahead regex on `/` instead of a deny rule: fragile, ImplementationSpecific, rejected.

### Recommendation
Keep deny-rule design (no catch-all change). Replace `ingress.blockAdminPath` by `ingress.exposeAdmin` (default `false`, breaking rename is allowed). With `false`: render the deny rule for `/api/admin` (Prefix) to the blocked Service, and when `className` is `nginx` add annotation `nginx.ingress.kubernetes.io/use-regex: "true"` (case-insensitive matching). With `true`: no deny rule and no blocked Service (operator's internal ingress; the chart does not publish admin on the public ingress otherwise). Defence in depth stays: `AdminIpAllowlistMiddleware` plus `ClusterAdmin` authorization reject non-admin callers on `/api/admin`.

### Requirements
10. Default render (values default, nginx class and Traefik class) contains the deny rule and the blocked Service and no rule routing `/api/admin` to the gateway Service.
11. Rule covers sub-paths and trailing slash by Prefix semantics; on nginx also case variants and encoded-slash/double-slash variants (use-regex annotation, no `server-snippet`/`configuration-snippet` anywhere in the chart).
12. `ingress.exposeAdmin=true` removes the deny rule and the blocked Service; documented as "internal ingress only" with NetworkPolicy kept.
13. `docs/configuration-guide.md` (current behaviour only, no progress notes): flag, nginx `use-regex` effect (whole host becomes case-insensitive), Traefik/other controllers case-sensitivity caveat, app-side rejection as the actual control. Update the existing mention at line ~533 and `docs/operations-runbook.md` if it names `blockAdminPath`.
14. Replace the open design gap note in the 281 plan with a pointer to this PRD (progress only in `docs/plans/`).

### Acceptance criteria
- AC-10: `Helm_DefaultIngress_DoesNotRoute_ApiAdmin` asserts on the rendered template (not just file names): no `/api/admin` path whose backend is the gateway Service, deny rule present, for default values.
- AC-11: Render test with `className: nginx` asserts `use-regex: "true"`, the deny rule, and no `*-snippet` annotation; with `className: traefik` asserts the deny rule without nginx annotations.
- AC-12: Render test with `exposeAdmin=true` asserts no deny rule and no blocked Service; the default of the flag is `false` in `values.yaml` (and `values.schema.json` if present).
- AC-13: App tests: `/api/admin` (incl. `/API/ADMIN/...`, trailing slash, `%2F`) is rejected for non-admin callers (401/403, never 200).
- AC-14: Docs updated as in requirement 13 and contain no status wording.
- AC-15 (best effort): manual/CI-optional check on nginx-ingress (kind) that `curl` of variants returns 503; if not runnable in the lane, recorded as not verified.

## Open questions (defaults taken)
1. Code name `CONSENT_NOT_EXTENDABLE` and exact message: default as proposed; architect may rename if a naming convention exists.
2. nginx `use-regex` makes all host paths case-insensitive: accepted (the app routing is case-insensitive anyway).
3. Allowlist-only ingress: not adopted; revisit if the public surface stabilizes.
