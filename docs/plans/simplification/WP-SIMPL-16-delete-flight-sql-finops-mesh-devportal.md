# WP-SIMPL-16 Delete Arrow Flight SQL over HTTP and the dev portal
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-16-delete-arrow-flight-sql-over-http-and-the-dev-portal) | Issues: #487 (Flight part), #485 (Arrow part), #491 (part), #512 | Owner decisions: D2, D12, OD-20 (FinOps replaced by WP-SIMPL-42), OD-21 (Mesh/Envoy ext_authz core), OD-37 (dev portal deleted) | Security conditions: SEC-SIMPL-30, 31, 35, 37; GHSA-v75c-28mx-7wm7 (WebSQL DML request handling; details: private advisory)

> **Changed by OD-20, OD-21 and OD-37 (2026-11-05):** FinOps is not deleted here; [WP-SIMPL-42](WP-SIMPL-42-usage-accounting-users-groups.md) replaces it (its PR-5). Mesh (Envoy ext_authz, `Application/Mesh`, `EnvoyExtAuthzEndpoints`) stays core ([WP-SIMPL-43](WP-SIMPL-43-authentication-authorization-core.md)). The dev portal is deleted (OD-37; this WP wins over WP-SIMPL-41). The file name keeps its old slug for stable links.

## 1. Goal and outcome
Delete the Flight-SQL-over-HTTP endpoints and server, and delete the dev portal (HTML landing page, getting-started page and the `/portal/*` consoles). Outcome: about -1,250 src lines; removed routes 404 for admin and anonymous in every environment; Arrow IPC export (`ArrowExportEndpoints.cs`) unchanged with masking, row/column policy and audit; FinOps, Mesh/Envoy ext_authz, the TOTP enrollment API and the HITL approval API untouched.

## 2. Preconditions and dependencies
- Merged first: #504 (security test baseline), `fix/security-batch-5` (touches `ArrowExportEndpoints.cs`), WP-SIMPL-04.
- Parallel with WP-SIMPL-12/14/15 (shared: `UnknownGatewayKeyGuard`, `DeletedFeaturesArchitectureTests`, `RemovedEndpointsTests`, `RemovedConfigKeysTests`, `IngressExemptionTests`; append rows). WP-SIMPL-45 keeps Flight read-only until this WP deletes it.
- WP-SIMPL-41 PR 1: if `DevPortalAssets.cs` is already deleted here, WP-SIMPL-41 has no OpenSchema badge to remove.

## 3. Scope
### In scope
- `ArrowFlightSqlEndpoints.cs`, `ArrowFlightSqlServer`, `IArrowFlightSqlServer`, Flight models, Flight DI, docs and tests.
- Dev portal: `DevPortalEndpoints.cs` (routes `/`, `/getting-started`, `/portal/2fa/enroll`, `/portal/2fa/enroll/verify`, `/portal/approvals`, `/portal/approvals/{ticketId}/step-up`), `DevPortalAssets.cs`, their mapping, docs and tests.
- Verify that `DevEndpoints` (`/api/dev/*`, F-AUTH-DX) maps routes only in Development; gate it if not.
### Out of scope
- `ArrowExportEndpoints.cs` and Arrow IPC serialization (kept); query cost options (WP-SIMPL-06).
- FinOps (removed with replacement by WP-SIMPL-42 PR-5).
- Mesh / Envoy ext_authz (core, WP-SIMPL-43).
- The TOTP enrollment API `/api/v1/governance/2fa/*` (`GovernanceApiEndpoints.cs:61-65`) and the HITL approval API `/api/governance/hitl/*` (`HitLEndpoints.cs`): kept; they replace the deleted HTML consoles.

## 4. Current state (verified at 63cbb893; dev portal re-verified at 88e5ab74)
- Flight: `Api/Endpoints/ArrowFlightSqlEndpoints.cs` (141, mapped `GatewayApplicationBuilderExtensions.cs:261`), `Application/Serialization/{ArrowFlightSqlServer(297),IArrowFlightSqlServer(34)}.cs`, `Domain/Model/ArrowFlightSqlModels.cs` (33), DI `GatewayExecutionServiceExtensions.cs:107`.
- FinOps: see WP-SIMPL-42 section 4 (not touched here).
- Mesh: `Application/Mesh/**`, `EnvoyExtAuthzEndpoints.cs` (core, not touched here).
- Dev portal: `DevPortalEndpoints.cs` (245, mapped `GatewayApplicationBuilderExtensions.cs:290` in every environment). `/` and `/getting-started` return 404 outside Development inside the handler; the `/portal/2fa/*` and `/portal/approvals/*` consoles are mapped in every environment. `DevPortalAssets.cs` (495) generates the HTML. Comment-only mentions: `BasicAuthenticationHandler.cs:333`, `AuditEventTypes.cs:105`.
- Dev endpoints: `DevEndpoints.cs` (175, mapped `:291`, routes created only when `DevFeatures.Resolve(options, env.IsDevelopment())` allows; verify).
- Tests (delete): Unit `Security/ArrowFlightSqlSecurityTests.cs`, `Security/FlightSqlGovernedExecutionTests.cs`, `Security/FlightSqlStreamEndpointTests.cs`, `Security/FlightSqlTableListingTests.cs`, `DevPortal/DevPortalTwoFactorConsoleTests.cs`. Keep `Security/ArrowExportSecurityTests.cs`, `Mesh/EnvoyExportValidationTests.cs`, all FinOps tests (WP-SIMPL-42 owns them).
- Docs: `docs/features/f-data-04-arrow-flight-sql.md`; dev portal mentions in `docs/features/f-api-09-datasource-connectivity-testing.md` and `f-auth-dx-basic-auth-session.md`.

## 5. Implementation steps (TDD order)
1. Baseline Security test list on the #504 commit. Map each Flight security test to: (a) deleted surface, or (b) an Arrow export equivalent; add any missing scenario (masking, row policy, tenant, audit, DML ticket refusal) to `ArrowExportSecurityTests` before deleting. Map each `DevPortalTwoFactorConsoleTests` security case to the existing TOTP enrollment API tests (`/api/v1/governance/2fa/*`) or add the missing case there.
2. Red: add the section 6 tests.
3. Delete Flight files, DI line 107, mapping line 261.
4. Delete `DevPortalEndpoints.cs`, `DevPortalAssets.cs` and the mapping `:290`; fix the two comment mentions. Confirm that `DevEndpoints` creates no route outside Development (gate the `MapDevEndpoints` call with `app.Environment.IsDevelopment()` if the check shows otherwise).
5. `UnknownGatewayKeyGuard`: Flight SQL keys (any value). The dev portal has no option section; if one exists at implementation time, guard it too.
6. Ingress/rate-limit exemptions, Helm and OpenAPI cleanup for the removed routes (SEC-SIMPL-37); docs (reference text only) and migration guide rows ("dev portal removed; use `/api/v1/governance/2fa/*` and `/api/governance/hitl/*`").

## 6. Tests
- Integration `RemovedEndpointsTests.Removed_routes_return_404_for_admin_and_anonymous` [Trait("Category","Security")] (shared 4.2 class; create if absent, append rows): Flight SQL routes; `/getting-started`, `/portal/2fa/enroll`, `/portal/2fa/enroll/verify`, `/portal/approvals`, `/portal/approvals/{ticketId}/step-up`; `/` returns no dev portal HTML. Rows also assert absence from OpenAPI.
- Integration `IngressExemptionTests.Exemption_list_contains_only_existing_routes` [Security] (SEC-SIMPL-37) and `AuthorizationInventoryTests.Every_endpoint_has_authorization_or_is_on_the_public_allowlist` [Security], green before and after.
- Integration `ReleaseSurfaceTests.Dev_endpoints_absent_outside_development` [Security] (Production environment: `/api/dev/*` 404, no static fallback 200).
- Unit `ArrowExportSecurityTests.Export_applies_masking_and_audit` [Security] stays green (SEC-SIMPL-35) plus ported scenarios from step 1.
- Unit `Configuration/RemovedConfigKeysTests.Every_removed_key_fails_startup` (append Flight rows) and `.FlightSql_keys_fail_startup` [Security].
- Architecture `DeletedFeaturesArchitectureTests` entries `ArrowFlightSql*` type names, `DevPortalEndpoints`, `DevPortalAssets`.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`; `dotnet publish src/Autheris.Api -c Release -o <scratch>` then assert no dev portal asset in the output.
- `git grep -inE "FlightSql|ArrowFlight|DevPortal" -- src deploy docs ':!docs/plans'` -> empty.
- `git grep -n -E "FinOps|EnvoyExtAuthz" -- src` still matches (kept features; FinOps disappears only with WP-SIMPL-42 PR-5).

## 8. Acceptance criteria
- [ ] Flight and dev portal grep empty; removed routes 404 (admin and anonymous) and absent from OpenAPI and exemptions; Release publish has no dev portal assets.
- [ ] FinOps, Mesh/Envoy ext_authz, the TOTP enrollment API and the HITL approval API unchanged and their tests green.
- [ ] Arrow export security tests green; Security-test diff fully mapped.
- [ ] Removed keys fail startup.

## 9. PR slicing
1. PR-A: Flight removal + ported Arrow export tests (steps 1-3, 5 for Flight keys).
2. PR-B: dev portal deletion, dev-endpoint gating check, migration-guide rows (steps 4, 6).

## 10. Risks, rollback, must NOT do
- Do NOT touch `ArrowExportEndpoints.cs` behaviour. Do NOT open any new Arrow listener. Do NOT leave a SPA/static fallback that answers 200 on removed paths.
- Do NOT delete or guard anything of FinOps, `Application/Mesh`, `EnvoyExtAuthzEndpoints`, the TOTP enrollment API or the HITL approval API.
- Rollback: revert per PR.

## 11. Estimate
+50 / -1,250 src (Flight about -510, dev portal about -740 incl. assets); tests about -600; 1 person-day.

## Decisions taken in this spec
- D-16a: Dev portal deleted (OD-37, owner default confirmed); its HTML consoles are replaced by the existing TOTP enrollment and HITL approval APIs.
- D-16b: FinOps and Mesh are out of scope (OD-20, OD-21); FinOps is removed with its replacement by WP-SIMPL-42 PR-5.
