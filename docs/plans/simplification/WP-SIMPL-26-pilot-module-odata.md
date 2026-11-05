# WP-SIMPL-26 OData core hardening (OD-24)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-26-odata-core-hardening-od-24) | Issues: #515 | Owner decisions: OD-24 (OData is core, always available; not a module), OD-22 (access model on every transport), OD-30 (single access decision point), OD-35 (writes rejected fail-closed on OData until separately wired), OD-15 | Security conditions: SEC-SIMPL-02, 03, 30, 31; SEC-AZ-01

> **Changed by owner decision OD-24 (2026-11-05):** OData is no longer extracted into a module and is no longer the module pilot (the pilot is [WP-SIMPL-27](WP-SIMPL-27-module-lakehouse.md), which also takes the runtime-vs-compile-time module loading decision). This spec is the OData core hardening WP. The file name is kept for link stability.

## 1. Goal and outcome
OData stays core; it is governed like every transport (OD-22) and interoperates with Power BI and Excel. Outcome: OData code no longer lives in `src/Autheris.Extensions` (it moves into the core `Autheris.Application` project), so WP-SIMPL-29 can remove the `Autheris.Api` -> `Autheris.Extensions` project reference; recorded Power BI and Excel request fixtures pass; row filters, PII masking and invisible columns apply on OData exactly as on the other transports; writes over OData are rejected fail-closed (OD-35).

## 2. Preconditions and dependencies
- WP-SIMPL-46 merged (OData data path decides through `IAccessDecisionPoint` with `AccessTransport.OData`). No dependency on the module host (WP-SIMPL-25).
- Coordinated with [WP-SIMPL-41](WP-SIMPL-41-authorized-data-discovery.md): the OpenSchema branches in `ODataEndpoints.cs` (`:71`, `:97`) are removed by WP-SIMPL-41, not here; whichever WP merges second rebases (this WP does not edit those branches).
- Must merge before WP-SIMPL-29 (which removes the Api -> Extensions reference).
- Parallel-safe with WP-SIMPL-27/28 (disjoint folders); not parallel with WP-SIMPL-32 (options).

## 3. Scope
### In scope
- Move `src/Autheris.Extensions/OData/` (6 files, 1,927 lines: `IODataHandler`, `ODataCsdlGenerator`, `ODataFilterParser`, `ODataHandler`, `ODataResponseFormatter`, `ODataServiceCollectionExtensions`) into `src/Autheris.Application/OData/` (namespace `Autheris.Application.OData`); the code depends only on `Autheris.Domain` and `Autheris.Application` today.
- Move the `AddODataIntegration` call out of `src/Autheris.Extensions/ExtensionsServiceCollectionExtensions.cs:13,43` into core Api DI.
- Power BI / Excel interoperability tests and the fixes they require in the OData handler, formatter and endpoints.
- OD-22 access-model tests on OData; fail-closed write rejection (OD-35).
- Move OData tests from `Autheris.Extensions.Tests` (if any) to `tests/Autheris.Tests.Unit/OData/`.
### Out of scope
- Any module work (OD-24). OpenSchema branch removal (WP-SIMPL-41). The `OData:Enabled` flag (WP-SIMPL-32 decides per its rules for always-on core features).
- Write support on OData (OD-35: a later, separately approved WP).
- Olap, DuckDB, table cache (core, OD-15).
- Removing the Api -> Extensions project reference (WP-SIMPL-29).

## 4. Current state (verified on 88e5ab74)
- `src/Autheris.Extensions/OData/`: 6 files, 1,927 lines; usings reference only `Autheris.Domain.*`, `Autheris.Application.*` and BCL/Microsoft.Extensions.
- `src/Autheris.Application/OData/` (4 files, 768 lines: `DynamicOpenApiGenerator`, `OpenApiCacheManager` and interfaces) is already core.
- `src/Autheris.Api/Endpoints/ODataEndpoints.cs` (22 `Map*` calls), `ODataEndpoints.Metadata.cs`, `ODataEndpoints.Query.cs`; the two `.cs` files with `using Autheris.Extensions.OData` are `ODataEndpoints.cs` and `ODataEndpoints.Query.cs`.
- Mapping: `GatewayApplicationBuilderExtensions.cs:230` (`if (gatewayOptions.OData.Enabled) app.MapODataEndpoints(gatewayOptions);`).
- `ODataOptions` (`src/Autheris.Domain/Options/Protocols/ODataOptions.cs`) stays in Domain.

## 5. Implementation steps (TDD order)
1. Red: add the section-6 tests. Record the request fixtures once from Power BI Desktop (OData feed connector) and Excel (Get Data > From OData Feed) against a dev instance and check them in as `.http` files under `tests/Autheris.Tests.Integration/OData/Fixtures/{powerbi,excel}/` (headers, query options, paging follow-ups; no tokens or tenant data). Add `PackageBoundaryArchitectureTests.Extensions_has_no_OData_namespace` (fails today).
2. Move (PR 2, move-only): `git mv src/Autheris.Extensions/OData/*.cs src/Autheris.Application/OData/`, namespace `Autheris.Application.OData`; update the usings in `ODataEndpoints.cs` and `ODataEndpoints.Query.cs`; call `AddODataIntegration` from core Api DI and delete the two lines in `ExtensionsServiceCollectionExtensions.cs`; move OData tests to `tests/Autheris.Tests.Unit/OData/` with unchanged names; shrink `api-extensions-usages.txt` (WP-SIMPL-25) if it already exists.
3. Access model (OD-22): verify every OData read path (entity set query, `$count`, single entity, navigation) builds its decision through `IAccessDecisionPoint.DecideAsync` with `AccessTransport.OData` (WP-SIMPL-46) and applies row filter, masking and column visibility from that decision. Any private decision path found is routed onto the decision point in this WP.
4. Writes (OD-35): OData maps no write handlers. `POST`, `PUT`, `PATCH`, `MERGE` and `DELETE` on entity-set and entity routes return 405 (no handler) or 403 without reaching a data source; `$batch` requests containing a write are rejected as a whole.
5. Interop fixes: fix what the fixtures reveal, limited to the OData protocol surface (service document, `$metadata` CSDL v4, `OData-Version` and `Content-Type` headers, absolute `@odata.nextLink` for server-driven paging, `$filter`/`$select`/`$orderby`/`$top`/`$skip`/`$count`, `401` with a `WWW-Authenticate` challenge). Each fix gets a unit test next to the code.
6. Green; record in the PR the Power BI and Excel versions used for the fixtures.

## 6. Tests
- `tests/Autheris.Tests.Integration/OData/ODataInteropTests` (WebApplicationFactory, recorded fixtures): `Service_document_lists_entity_sets`, `Metadata_is_valid_csdl_v4`, `Server_driven_paging_emits_absolute_next_link`, `Query_options_filter_select_orderby_top_skip_apply`, `Count_returns_total`, `PowerBi_fixture_requests_succeed`, `Excel_fixture_requests_succeed`, `Unauthenticated_request_gets_challenge` [Security].
- `tests/Autheris.Tests.Integration/OData/ODataAccessModelTests` [Security] (OD-22): `OData_applies_row_filter`, `OData_masks_pii_columns`, `OData_hides_invisible_columns`, `OData_decides_via_access_decision_point` (decision recorded with `AccessTransport.OData`), `OData_next_link_does_not_widen_row_scope`.
- `ODataWriteRejectionTests` [Security] (OD-35): `OData_write_methods_rejected_without_source_call` (Theory over POST/PUT/PATCH/MERGE/DELETE), `OData_batch_with_write_rejected`.
- Architecture: `PackageBoundaryArchitectureTests.Extensions_has_no_OData_namespace`.
- Moved: existing OData unit tests (names unchanged, Security tags kept, SEC-SIMPL-03).

## 7. Verification commands
```
git grep -n "Autheris.Extensions.OData" -- src tests     # expect none
dotnet build Autheris.sln -c Release -warnaserror
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --filter "FullyQualifiedName~OData"
dotnet test tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release --filter "FullyQualifiedName~OData"
dotnet test tests/Autheris.Tests.Architecture -c Release
```

## 8. Acceptance criteria
- [ ] No OData source under `src/Autheris.Extensions`; OData lives in `src/Autheris.Application/OData` and is always mapped by core.
- [ ] Power BI and Excel fixture tests green; tool versions recorded in the PR.
- [ ] Row filter, PII masking and invisible columns apply on OData (`ODataAccessModelTests` green); decisions come from `IAccessDecisionPoint`.
- [ ] Every OData write method is rejected fail-closed without a source call (OD-35).
- [ ] Moved test count equals removed test count (test name diff in PR); Security test count not lower.
- [ ] SEC-SIMPL-02 items (config keys, routes, controls) in the PR description; route table unchanged (22 OData routes).

## 9. PR slicing
1. Interop fixtures, `ODataInteropTests`, `ODataAccessModelTests`, `ODataWriteRejectionTests`, protocol fixes (<= 400 added src lines).
2. Move `Extensions/OData` -> `Application/OData` + architecture test (`size-exception`: move-only, net src lines about 0).

## 10. Risks, rollback, must NOT
- Risk: Power BI and Excel send slightly different query shapes across versions; fixtures pin the shape, new versions add fixtures.
- Risk: a protocol fix changes behaviour for existing clients; mitigated by the moved unit tests staying green unchanged.
- Rollback: revert PR 2 (move) or PR 1 independently.
- Must NOT: create an OData module; change route paths; add `AllowAnonymous`; add write handlers; add a decision path outside `IAccessDecisionPoint`; edit the OpenSchema branches (WP-SIMPL-41); delete Security tests.

## 11. Estimate
Src: about +200 (protocol fixes, DI) / -50, plus a move of 1,927 lines from Extensions to Application (net 0). Tests: about +600 plus fixtures. 3 person-days.

## Decisions taken in this spec
- OData moves into the existing core `Autheris.Application/OData` folder (its code depends only on Domain and Application) instead of a new project.
- `ODataOptions` stays in Domain.
- Power BI / Excel interoperability is proven with recorded request fixtures, not with the desktop clients in CI.
