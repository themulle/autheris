# WP-SIMPL-16 Delete Arrow Flight SQL over HTTP, FinOps, Mesh, dev portal in release builds
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-16-delete-arrow-flight-sql-over-http-finops-mesh-dev-portal-in-release-builds) | Issues: #487 (Flight part), #485 (Arrow part), #491 (part), #512 | Owner decisions: D2, D12 | Security conditions: SEC-SIMPL-30, 31, 35, 37; GHSA-v75c-28mx-7wm7 (Flight-only part) - see private advisory

## 1. Goal and outcome
Delete the Flight-SQL-over-HTTP endpoints and server, FinOps, and Mesh (Envoy ext_authz); map dev endpoints only in Development and exclude dev portal assets from Release publish. Outcome: about -1,800 src lines; removed routes 404 in Release; Arrow IPC export (`ArrowExportEndpoints.cs`) unchanged with masking, row/column policy and audit.

## 2. Preconditions and dependencies
- Merged first: #504 (security test baseline), `fix/security-batch-5` (touches `ArrowExportEndpoints.cs`), WP-SIMPL-04.
- Parallel with WP-SIMPL-12/14/15.

## 3. Scope
### In scope
- `ArrowFlightSqlEndpoints.cs`, `ArrowFlightSqlServer`, Flight models; `Application/FinOps`, FinOps endpoints, middleware, options, models; `Application/Mesh`, `EnvoyExtAuthzEndpoints.cs`; dev portal/dev endpoints gating and Release asset exclusion; docs and tests.
### Out of scope
- `ArrowExportEndpoints.cs` and Arrow IPC serialization (kept); query cost options (WP-SIMPL-06).

## 4. Current state (verified at 63cbb893)
- Flight: `Api/Endpoints/ArrowFlightSqlEndpoints.cs` (141, mapped `GatewayApplicationBuilderExtensions.cs:261`), `Application/Serialization/{ArrowFlightSqlServer(297),IArrowFlightSqlServer(34)}.cs`, `Domain/Model/ArrowFlightSqlModels.cs` (33), DI `GatewayExecutionServiceExtensions.cs:107`.
- FinOps: `Application/FinOps/{Interfaces/IFinOpsAccountingService(37),Services/FocusCostAccountingService(241)}.cs`, `Api/Endpoints/FinOpsEndpoints.cs` (142, mapped :245), `Api/Middleware/FinOpsBudgetMiddleware.cs` (130, `UseMiddleware` :108), `Domain/Options/Resilience/FinOpsOptions.cs` (17), `Domain/Model/FinOpsModels.cs` (34), DI `GatewayExecutionServiceExtensions.cs:167`.
- Mesh: `Application/Mesh/{Interfaces/IEnvoyExtAuthzService(33),Services/EnvoyExportValidation(40),Services/EnvoyExtAuthzService(469)}.cs`, `Api/Endpoints/EnvoyExtAuthzEndpoints.cs` (mapped :271), DI `GatewaySecurityServiceExtensions.cs:118`.
- Dev: `DevPortalEndpoints.cs` (245, :290), `DevPortalAssets.cs` (495), `DevEndpoints.cs` (175, :291, already Development-only per comment - verify).
- Tests (delete): Unit `Security/ArrowFlightSqlSecurityTests.cs`, `Security/FlightSqlGovernedExecutionTests.cs`, `Security/FlightSqlStreamEndpointTests.cs`, `Security/FlightSqlTableListingTests.cs`, `FinOps/FocusCostAccountingTests.cs`, `Mesh/EnvoyExportValidationTests.cs`; Integration `FinOpsEndToEndSqliteTests.cs`, `FinOpsIntegrationTests.cs`. Keep `Security/ArrowExportSecurityTests.cs`, `DevPortal/DevPortalTwoFactorConsoleTests.cs` (Development path).
- Docs: `docs/features/f-data-04-arrow-flight-sql.md`, `f-ai-08-focus-finops-accounting.md`; Mesh/Envoy sections (grep `ext_authz`).

## 5. Implementation steps (TDD order)
1. Baseline Security test list on #504 commit. Map each Flight security test to: (a) deleted surface, or (b) an Arrow export equivalent; add any missing scenario (masking, row policy, tenant, audit, DML ticket refusal) to `ArrowExportSecurityTests` before deleting.
2. Red: add tests of section 6.
3. Delete Flight files, DI line 107, mapping line 261.
4. Delete FinOps files, middleware line 108, mapping 245, DI 167, options.
5. Delete Mesh files, mapping 271, DI 118.
6. Gate `MapDevPortalEndpoints`/`MapDevEndpoints` behind `app.Environment.IsDevelopment()` in `GatewayApplicationBuilderExtensions.cs:290-291`; in `Autheris.Api.csproj` add `<Content Remove="...devportal assets..." Condition="'$(Configuration)'=='Release'" />` (assets live in `DevPortalAssets.cs` as code: wrap the class in `#if DEBUG` or move it to a Development-only partial; default per D2 is delete if no owner is named).
7. `UnknownGatewayKeyGuard`: `Gateway:FinOps`, Flight SQL keys, Mesh/Envoy ext_authz keys (any value).
8. Ingress/rate-limit exemptions, Helm and OpenAPI cleanup (SEC-SIMPL-37); docs, migration guide.

## 6. Tests
- Integration `RemovedRoutesTests.Removed_routes_return_404_for_admin_and_anonymous` [Trait("Category","Security")] Theory rows: Flight SQL routes, FinOps routes, Envoy ext_authz routes; `Removed_routes_absent_from_openapi_and_exemptions`.
- Integration `ReleaseSurfaceTests.Dev_endpoints_absent_outside_development` [Security] (Production environment: dev portal and dev routes 404, no static fallback 200).
- Unit `ArrowExportSecurityTests.Export_applies_masking_and_audit` [Security] stays green (SEC-SIMPL-35) plus ported scenarios from step 1.
- Unit `Configuration/RemovedConfigKeysTests.FinOps_keys_fail_startup`, `.FlightSql_keys_fail_startup`, `.Mesh_keys_fail_startup` [Security].
- Architecture `DeletedFeaturesArchitectureTests` entries `Autheris.Application.FinOps`, `Autheris.Application.Mesh`, `ArrowFlightSql*` type names.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`; `dotnet publish src/Autheris.Api -c Release -o <scratch>` then assert no dev portal asset in output.
- `git grep -inE "FlightSql|ArrowFlight|FinOps|EnvoyExtAuthz|ext_authz" -- src deploy docs ':!docs/plans'` -> empty.

## 8. Acceptance criteria
- [ ] grep empty; removed routes 404 (admin and anonymous); Release publish has no dev portal assets.
- [ ] Arrow export security tests green; Security-test diff fully mapped.
- [ ] Removed keys fail startup.

## 9. PR slicing
1. PR-A: Flight removal + ported Arrow export tests.
2. PR-B: FinOps + Mesh removal.
3. PR-C: dev portal gating and Release exclusion.

## 10. Risks, rollback, must NOT do
- Do NOT touch `ArrowExportEndpoints.cs` behaviour. Do NOT open any new Arrow listener. Do NOT leave a SPA/static fallback that answers 200 on removed paths.
- Rollback: revert per PR.

## 11. Estimate
+200 / -2,000 src; tests -1,100; 2 person-days.

## Decisions taken in this spec
- D-16a: Dev portal kept for Development only (not deleted) unless no owner is named before PR-C; then delete (D2 default).
