# Plan: PLAN-FED-01-DELTA - Delta of `PLAN-FED-01` After Wave 1 (INC-1..INC-6)

**Document ID:** `PLAN-FED-01-DELTA`
**Date:** 2026-10-16
**Status:** PROPOSED (Phase 2 delta; the delta security review S-10 is appended after it)
**Author:** Solution Architect (Phase 2, `csharp-architect` role)
**Parent Plan:** [`PLAN-FED-01`](2026-10-16-plan-cross-source-all-consumers.md) (PRD [`REQ-FED-01`](2026-10-16-req-cross-source-all-consumers.md)) and [`PLAN-DELIVERY-W23`](2026-10-16-plan-waves-2-3-delivery.md) step P-10 (implementation steps I-9..I-21)
**Inputs:** [`PLAN-SEC-OLAP-01`](2026-10-16-plan-sec-olap-federation-gate.md) (PR #140), [`PLAN-BUG-75-GATING`](2026-10-16-plan-bug-75-graphql-relation-schema-gating.md) (PR #139), PR #141 (#127), [`PLAN-FED-131`](2026-10-16-plan-issue-131-federated-bound-parameters.md), [`PLAN-SQL-128`](2026-10-16-plan-issue-128-list-parameters.md), [`PLAN-SQL-130`](2026-10-16-plan-issue-130-convert-timezone.md)

No redesign. This file only records what `main` (after PRs #139-#142) already contains, what that changes in scope, contracts and file ownership, and the resulting order. Where this file and `PLAN-FED-01` disagree, this file wins for `WP-FED-01..14`. Code facts below were read on this branch (it contains wave 1).

---

## 1. What wave 1 delivered (verified in code)

| Item | Where (current `main`) | Fact that matters |
|---|---|---|
| `IFederationGate` / `FederationGate` | `src/Autheris.Application/Federation/FederationGate.cs:14-58` | Reads options per call, fails closed on blank transport, empty list, `Enabled=false`. Throws `WebSqlPolicyException(FederationGate.DeniedMessage)`; **one identical message** for "disabled" and "transport not allowed". Registered in `GatewaySqlEngineServiceExtensions.cs:97`. Only caller: `OlapStagingService.EnforceCrossSource` (`OlapStagingService.cs:686-691`). |
| Transport names | `SqlTransports` in `src/Autheris.Application/Sql/Interfaces/IGovernedSqlExecutionService.cs:16-31` | Has `Olap` and `All` (9 names). **No `GraphQl`.** `FederationTransports` of `PLAN-FED-01` section 3 does not exist and is not created (see 3.1). |
| Options and startup validation | `RestApiOptions.cs:64-110` (`CrossSourceOptions`), `src/Autheris.Api/Configuration/Validation/CrossSourceOptionsValidator.cs:20-143` (`IValidateOptions<GatewayOptions>`) | Unknown transport aborts start, positive limits, `MaxParallelSourceReads` 1..16, legacy `MaxMemory`/`MaxTempDirectorySize` keys removed (warning only). Default list is the literal `["WebSql","Trino"]` (`RestApiOptions.cs:77`); **not widened yet.** |
| Inline transport gate (still the old one) | `FederatedDuckDbExecutionService.cs:208-212` (`ValidateAsync`, checks only `Enabled`) and `:239-252` (execute) | Runs **before** table authorization (violates the anti-oracle order of SEC-FED-03); `ValidateAsync(rawSql, user, tenantId, ct)` carries **no transport**, so the Flight `/info` call (`ArrowFlightSqlServer.cs:95`) and `GovernedSqlExecutionService.cs:113` cannot be gated per transport. Async job submit does not call `ValidateAsync`. |
| OLAP interim path | `OlapStagingService.cs` (781 lines: validate SQL, authorize all, classify, gate, guardrails, admission, governed staging), `OlapSqlInspector.cs` (464), `OlapSqlPolicy.cs` (115), `CrossSourceClassifier.cs`, `MaskedColumnSet.cs` (shared with `FederatedStagingService.cs:277`), `OlapRequestLimiter.cs` (global + per-tenant lease), `DuckDbOlapEngine` setup hardening, `OlapEntryPointArchitectureTests` | Wire codes are `OLAP_*` (list in 4.1). Client DuckDB SQL is still executed (accepted interim residual of SEC-FED-12, `PLAN-SEC-OLAP-01` P3.7). Endpoint (`DuckDbOlapEndpoints.cs:50-57`) still does an inline identity check, not `RequireAuthorization()`. |
| GraphQL schema gating | `RelationExecutability.IsSameExecutionSource/HasSameSourceName` (`Sql/Tree/Federation/RelationExecutability.cs:111-147`), used at `CatalogSchemaModel.cs:255` (forward) and `:309` (reverse); tree service backstop at `GovernedTreeQueryService.cs:267-281` | Compares type, `SourceName` and dialect, **not `Domain`**. Pure metadata, no caller, no options. |
| #127 (PR #141) | catalog, static analysis and `CatalogVisibility` now use the data-path resolver (`IConsentResolutionService.ResolveAccess`, claims scopes, `GetEffectiveColumnAccess`) | The "Clear key" definition for relations must be the same resolver call (3.4). No other federation file changed. |
| Not touched by wave 1 | `Data/Relations/*` (resolver unwired, `InMemoryCrossSourceRowReader` in DI at `DataApiServiceExtensions.cs:25-26`), `TableRelation.AllowCrossDataSource` (`TableModels.cs:293`, init-only, never persisted), `FederatedStagingService.cs` (sequential `foreach` at `:126`; MaxParallel unused), `CrossSourcePlanner.cs` (`ParameterSource.Empty` at `:282`), `AsyncQueryJobBackgroundWorker`, all of `WP-FED-11..14` (no optimizer type exists) | |

## 2. Per-WP delta

LOC = production / test, **remaining** work (original estimate in brackets). Step = tracker step. "Done" is only claimed where a test exists on `main`.

| WP (step) | Already on main | Scope change | Contract / ownership change | LOC |
|---|---|---|---|---|
| **01** gate, options (I-9) | Gate core, `Olap` name, startup validator, removal of dead options: **fully** | Remaining: `GraphQl` constant in `SqlTransports`; `ProtectedStagingEnabled`; relation limit options (`CrossSourceRelationOptions` bound at `WebSql:CrossSource:Relations`) and their validation; staged widening of the default list (3.1); **gate wiring into `FederatedDuckDbExecutionService` moves here from WP-03** (replace the two inline checks, gate after authorization, `ValidateAsync` gains the transport) | New file list shrinks to: edit `FederationGate.cs`, `SqlTransports`, `RestApiOptions.cs`, `CrossSourceOptionsValidator.cs` (existing path, not `FederationServiceExtensions.cs`), `IFederatedQueryExecutionService.cs`, callers of `ValidateAsync`. `FederationTransports.cs` is dropped | 140 / 230 [350 / 450] |
| **02** errors, provenance, registry (I-9) | Only `OLAP_*` wire codes and the fixed gate text. `GatewaySecurityException.ErrorCode` exists | Unchanged, plus: gate throws `FederationException`; OLAP mapper (`OlapStagingService.MapException`, `EnforceCrossSource`) catches it; existing `CROSS_SOURCE_*` relation codes (`CrossSourceRelationModels.cs:16-19`) are replaced by `FEDERATION_*` when WP-06 deletes the resolver code | **`FederationException` derives from `WebSqlPolicyException`** (a `SecurityException`, not a `GatewaySecurityException`), so every existing catch and mapper keeps working and mappers only add the code. GraphQL mapping goes into `src/Autheris.Api/Middleware/ErrorSanitizingFilter.cs` (the plan's `src/Autheris.GraphQL/...ErrorFilter*.cs` does not exist). Codes: 4.1 | 480 / 650 [450 / 650] |
| **03** SQL hardening (I-10) | Nothing (executor, planner, staging untouched) | **Drops**: gate wiring (-> 01) and the scalar parameter carrier of D-10 (-> `WP-FED-15`, section 3.3). Keeps: bounded parallel reads (`MaxParallelSourceReads` via `SemaphoreSlim`), row-limit wrapping/`truncated`, provenance, `FEDERATION_SOURCE_ERROR`, async job submit validation with transport `AsyncJob`, Flight `/info` info path | Planner keeps `ParameterSource.Empty` (`:282`); `Plan` gets no parameter argument in this step. `MaskedColumnSet` is reused, not redefined | 430 / 600 [600 / 800] |
| **04** at rest (I-12, swapped) | `DuckDbOlapEngine` sandbox setup (`BuildSetupSql`, autoload off, `MaxMemory`/`MaxTempDirectorySize` validated), `OlapRequestLimiter` | Spill root and permissions, `temp_file_encryption` spike, `JobResultProtector`, `ProtectedStagingEnabled` effective, `AsyncJob` into default list afterwards | `DuckDbOlapEngine.cs` region edits only; after WP-05 the user-SQL path is gone (less to protect) | 420 / 520 [450 / 550] |
| **05** OLAP facade (I-11, swapped) | Gate and guardrails for cross-source OLAP **interim**; no facade | As `PLAN-FED-01` 4.5, plus the explicit replacement in section 5 below | Handler calls `IGovernedSqlExecutionService` with transport `Olap`; deletes `OlapStagingService`, `OlapSqlInspector`, `OlapSqlPolicy`, `CrossSourceClassifier`, `ValidateUserSql` (if unreferenced, Phase 5 confirms) | +250 added, about 1,360 deleted / 500 [350 / 450] |
| **06** reader, resolver rewrite (I-13) | Resolver, helper `ValidateJoinKeysClear` (`Helpers.cs:108-160`, uses `GetEffectiveColumnAccess`) and sort guard exist but are unwired | As planned | **`IsCrossDataSource` = `!RelationExecutability.IsSameExecutionSource`** (the resolver's current version also compares `Domain`, `CrossSourceRelationResolver.cs:50-66`, which would route same-connection cross-domain relations into federation; PR #139 deliberately does not). `PlanRelation` takes the Clear decision from the same resolver as #127 (3.4). `InMemoryCrossSourceRowReader` leaves DI | 780 / 900 [800 / 900] |
| **07** relation declaration (I-18) | Property exists, no column, no mapping | As planned; flag change needs **both table owners** (Q-6), see section 6 | DDL and shapes x3 serial (G-SHARED) | 590 / 660 [550 / 600] |
| **08** splitter, schema gating (I-19) | **Partly**: omission of non-executable relations (forward `:255`, reverse `:309`, counter `omittedRelations`), tree-service backstop. Not done: splitter, stitcher, decorator, MCP relation list, federation state in schema cache key | Extend, do not replace: new wrapper `RelationExecutability.IsExecutable(parent, child, relation, gate)` = `IsSameExecutionSource` OR (`AllowCrossDataSource` AND `IsEnabledFor(SqlTransports.GraphQl)`); `IsSameExecutionSource` and its PR #139 tests stay unchanged. Splitter classifies edges with `!IsSameExecutionSource` | Tree service lines are `:267-281` now (not 223-235); single-line message change stays WP-08's. MCP `search_catalog`/`describe_dataset` do not use the predicate yet and are still in scope | 950 / 1,100 [1,100 / 1,200] |
| **09** HTTP, relation filters (I-20) | Nothing | As planned | `GovernedConnectorSourceReader` extracts the per-table block of `FederatedStagingService` after WP-03 and WP-14 merged | 700 / 800 |
| **10** integration, parity (I-21 plus per-PR) | Unit evidence only: `OlapFederationGateTests`, `OlapStagingServiceTests`, `RelationExecutabilityTests`, `CrossSourceOptionsValidatorTests`, architecture test | Parity matrix, GraphQL and SQL fixtures as planned; OLAP rows target the facade, not `OlapStagingService`; `FederationConsumers.All` registry from WP-02 | Each WP PR carries its own cases; I-21 holds the remainder (about 1,000) | 0 / about 3,400 over all PRs |
| **11** pushdown (I-14) | Nothing | As planned | `PushabilityMatrix` defines the public type-class enum reused by #131; `PushedClauseBuilder` takes an optional `FederatedParameterContract?` later (I-26); until then a parameter reference is "not pushable" (SEC-FED-22: constants only). "Clear" test is `GetEffectiveColumnAccess == Clear`, **not** `MaskedColumnSet` (it ignores deny/hidden) | 900 / 1,300 |
| **12** options, hints, audit, analysis (I-15) | Nothing | As planned; options validator extends the existing `CrossSourceOptionsValidator.cs` in `Autheris.Api/Configuration/Validation/` | Audit record reserves a `parameterNames` field for I-26; analysis keeps a hook for `FederatedParameterContract.ForAnalysis` | 700 / 900 |
| **14** key sets (I-16) | Nothing | As planned (`allow_key_set_import`, Q-8) | `KeyBatchPlanner` consumes the `KeyPredicate` contract of WP-06 (I-13) | 700 / 1,000 |
| **13** subtrees (I-17) | Nothing | As planned | `Plan(...)` signature grows only by **optional trailing parameters** (the #131 plan appends `FederatedParameterContract? parameters = null`; the 5-argument overload stays). `ScopedFunctionCollector` and the outer allowlist must accept `convert_timezone` (#130, I-6, name in `CommonFunctions`) and the planner's `TimeZoneConversion.ValidateFederated` call stays on the **whole** statement before `SubtreeExtractor` runs | 1,300 / 1,500 |

Totals: `WP-FED-01..10` about 4,740 production lines added (plus 1,360 deleted with the interim OLAP path) and about 7,600 test lines (was 5,350 / 8,000); `WP-FED-11..14` unchanged at 3,600 / 4,700.

## 3. Contract changes against `PLAN-FED-01` section 3

### 3.1 Transports and default list
- `SqlTransports` stays the only registry; add `GraphQl`. No `FederationTransports` type. `CrossSourceOptions` gets `DefaultAllowedTransports(bool protectedStaging)`; the literal at `RestApiOptions.cs:77` is replaced.
- Widening of the default list is staged so that no transport is ever allowed before its conditions hold (SEC-FED-18, SEC-FED-09): the seven SQL-text transports (`Rest`, `Mcp`, `ArrowExport`, `FlightSql`, `SqlEndpoints` added to `WebSql`, `Trino`) after I-10; `Olap` after I-11 (the facade); `AsyncJob` after I-12 (`ProtectedStagingEnabled`); `GraphQl` after I-19. Each addition needs the SEC-FED-18 default-list decision test. An explicit `AllowedTransports` list always replaces the default. Q-1: `Enabled` stays `false`, so no default install changes behavior.

### 3.2 Gate and exception
- `IFederationGate` keeps `EnsureAllowed(string?)` and `IsEnabledFor(string?)` (signatures already match `PLAN-FED-01` section 3).
- One code for both gate denials: **`FEDERATION_DISABLED`** with the existing fixed text. `FEDERATION_TRANSPORT_NOT_ALLOWED` is dropped: the merged gate and its Phase 3 review chose identical text so a caller cannot probe which transports are configured. Default taken; the owner may veto.
- `IFederatedQueryExecutionService.ValidateAsync` gets a `transport` argument (callers: `GovernedSqlExecutionService.cs:113`, `ArrowFlightSqlServer.cs:95`, async submit). Gate runs after `AuthorizeAllTablesAsync` in execute and validate.

### 3.3 Parameters (#131)
`WP-FED-03` no longer carries parameters. The only carrier is `FederatedParameterContract` (I-26). Until then a client parameter in a federated statement behaves as today (analysis error) and list values stay refused with `SQL_LIST_PARAM_UNSUPPORTED` (#128, I-5). `FEDERATION_PARAMETER_INVALID` is not introduced; federated parameter errors use `SQL_LIST_PARAM_*` and the existing single-source parameter codes (`PLAN-FED-131` A-7). `SEC-FED-35..37` are reviewed in S-2.

### 3.4 Key columns and Clear
Parent and child key columns are Clear by `GetEffectiveColumnAccess(...) == Clear` on decisions produced by the data-path resolver (`IConsentResolutionService.ResolveAccess` with `GetScopeSet(principal.Claims)`, as fixed by #127). No scope-blind copy of consent rules is allowed in `Data/Relations`, the splitter or MCP relation lists.

## 4. Error codes

### 4.1 Stable names (align with PR #140)
Kept on the OLAP endpoint until I-11 and as OLAP-specific outcomes afterwards: `OLAP_ACCESS_DENIED`, `OLAP_CAPACITY` (429 limiter), `OLAP_TIMEOUT`, `OLAP_STAGING_TOO_LARGE`, `OLAP_FAILED`, `OLAP_TABLE_DENIED`. Federation conditions move to the shared names when the facade lands (I-11), as a documented breaking change like #140's own upgrade note:

| Merged code (PR #140 / resolver) | Becomes | Status |
|---|---|---|
| `OLAP_CROSS_SOURCE_DENIED` | `FEDERATION_DISABLED` | HTTP 403, same text |
| `OLAP_SOURCE_UNSUPPORTED`, tree "not a SQL table" | `FEDERATION_UNSUPPORTED_SOURCE` | 403 after authorization |
| `OLAP_TABLE_COUNT`, `LIMIT_EXCEEDED`, `CROSS_SOURCE_FILTER_KEY_SET_EXCEEDED`, relation caps | `FEDERATION_LIMIT_EXCEEDED` | 422 (400 where the transport uses 400) |
| `CROSS_SOURCE_SORT_NOT_SUPPORTED` | `FEDERATION_SORT_NOT_SUPPORTED` | 422 |
| `CROSS_SOURCE_RELATION_KEY_NOT_CLEAR` | `FEDERATION_KEY_NOT_CLEAR` | 422 after authorization of both tables |
| `CROSS_SOURCE_RELATION_NOT_ENABLED` | `FEDERATION_UNSUPPORTED_SOURCE` (relation not advertised) | schema omission is the first line of defence |
| `CONNECTOR_UNAVAILABLE`, upstream failures in federation | `FEDERATION_SOURCE_ERROR` | no driver text |
| `OLAP_SQL_INVALID`, `OLAP_SQL_NOT_ANALYZABLE`, `OLAP_SQL_CONSTRUCT_NOT_ALLOWED`, `OLAP_COLUMN_POLICY` | existing `SQL_*` mapper codes of the governed compiler and `SqlCompilerErrorMapper` (masked join/filter keeps its 403) | deleted with `OlapSqlInspector` |

Audit reason strings (`FEDERATION_GATE`, `TABLE_COUNT`, `UNSUPPORTED_SOURCE_TYPE`) are audit values, not wire codes, and stay.

## 5. The facade replaces the interim path (WP-FED-05)

`WP-FED-05` (I-11) **replaces** the interim `PLAN-SEC-OLAP-01` path; it is not an addition to it. After I-11 no request reaches `OlapStagingService` or `OlapSqlInspector`; the interim phase-B code (`EnforceCrossSource`, `EnforceSqlGuardrails`) is deleted. `PLAN-SEC-OLAP-01` P3.7 allowed the interim only while it is recorded as open debt; I-11 closes it, so Phase 6 then states SEC-FED-12 as met.

**Kept from PR #140:** `FederationGate`, `SqlTransports.Olap`, the startup validator, `MaskedColumnSet` (shared staging schema), `OlapRequestLimiter` and its options (admission before the first source read, now taken by the facade handler, per tenant and global), the `DuckDbOlapEngine` sandbox setup and `trustedGeneratedSql` execution (the planner's generated text is the only SQL DuckDB runs), `DuckDbOlap` result and timeout limits, the ordering invariant (authorize all tables, then gate, then admission, then any read), the generic-denial text outside Development, `OlapEntryPointArchitectureTests` (rewritten: the endpoint depends only on `IGovernedSqlExecutionService`), `OlapEndpointTestHost`, and the `OLAP_CAPACITY`/`OLAP_TIMEOUT` wire outcomes.

**Replaced or deleted:** `OlapStagingService`, `OlapSqlInspector`, `OlapSqlPolicy`, `CrossSourceClassifier` (the router decides single versus cross-source; its parity cases move to router tests), `DuckDbOlapEngine.ValidateUserSql`, the static `LastCapacityAudit` map, the inline identity check (endpoint uses `RequireAuthorization()`, exception only under the Quickstart profile). Behavior change: the request dialect becomes the gateway SQL, `tableNames` is ignored with `Warning: 299`, single-source OLAP SQL runs single-source.

## 6. SEC-FED status against merged code

Legend: MET = enforced and tested on `main`; PART = some transports or parts; OPEN = nothing merged. No condition of `SEC-FED-19..34` is met; `WP-FED-11..14` code does not exist.

| Condition | Status | Basis / what remains |
|---|---|---|
| 01 governed pipeline only | PART | SQL federation and OLAP staging use governed reads; resolver still re-filters raw rows (`CrossSourceRelationResolver.Helpers.cs:232-267`): WP-06 |
| 02 Clear join keys | PART | WebSQL/OLAP join and filter guardrails (HMAC allowed, SEC-JOIN-01 parity); relation key rule exists only in the unwired resolver: WP-06, WP-08 |
| 03 anti-oracle order | PART | MET for OLAP (authorize all, then gate). SQL federation gate still runs first: I-9. GraphQL: WP-08 |
| 04, 05, 06 (GraphQL parts) | OPEN | WP-06/08 (tenant NULL pass is in the resolver being deleted). OLAP/SQL parts hold today; WP-05 deletes the one static map |
| 07, 09 at rest | OPEN | WP-04. `AsyncJob` is absent from the default list today (no widening yet), which keeps SEC-FED-09 safe until I-12 |
| 08 fan-out bounds | PART | `MaxTableCount`, byte budgets, validator bounds, OLAP lease merged; parallelism, tree and relation limits, timeout over reads: WP-03, WP-06 |
| 10 audit per read | PART | SQL start/failure and OLAP events exist; GraphQL reads and post-authorization denial audit (open note 6 of #140): WP-03, WP-06 |
| 11 relation flag governance | OPEN | No column, no endpoint: WP-07; tightened by Q-6 (below) |
| 12 single gate, OLAP compiler path | PART | Gate evaluator and startup validation MET for OLAP; executor still inline (I-9); "client DuckDB SQL not executed" closes at I-11 |
| 13 provenance and disclosure | OPEN | WP-02, WP-03 |
| 14 keys are data | PART | SQL path compiled with bound parameters; GraphQL/HTTP keys: WP-06, WP-09 |
| 15 invalidation | PART | `RelationExecutability` is pure and recomputed per schema build; cache key lacks federation state: WP-08 |
| 16 consistency window | OPEN | WP-08 provenance field |
| 17 fail-closed defaults | PART | Validator aborts start; OLAP unsupported types denied; relations/readers: WP-06, WP-08 |
| 18 default-list review | OPEN | Default unchanged; decision test is added with each widening step (3.1) |
| 19-34 optimizer | OPEN | WP-11..14; defaults of the three switches stay `false` (SEC-FED-33) |
| 35-37 parameters | OPEN | Reviewed in S-2 against `PLAN-FED-131`; `38-42` stay a wave 4 placeholder |

New or changed conditions for S-10 (named D-1.. to avoid colliding with the reserved `SEC-FED-35..42`):
- **D-1** (3.2) Gate denial for disabled and transport-not-allowed is one code and one text, raised only after all tables are authorized, in execute, validate, Flight info and job submit.
- **D-2** (section 5) No control of PR #140 is deleted before an equivalent test runs on the facade: construct and function corpus (`read_csv`, `COPY`, `ATTACH`, table functions, `TABLESAMPLE`, set operations), masked join/filter, admission before first read, table-count limit.
- **D-3** (3.4) One definition of Clear for relations, schema, MCP and pushdown (the #127 resolver).
- **D-4** (WP-08) Schema omission and runtime refusal use one predicate; `IsSameExecutionSource` is not weakened, and `AllowCrossDataSource` plus gate state only widen it for `GraphQl`.
- **D-5** (3.3) Parameter values enter a federated statement only through `FederatedParameterContract`; list guard stays until I-26.
- **SEC-FED-11 tightened (Q-6):** `AllowCrossDataSource` needs approval by **both** table owners (four-eyes). A table without a defined owner cannot be enabled (fail closed); default taken, flagged to the owner.

## 7. Owner decisions applied

- **Q-1** `CrossSource:Enabled` stays `false`: default list widening is inert for default installs; tests set `Enabled=true` explicitly; docs state it.
- **Q-6** both table owners approve `AllowCrossDataSource` (WP-07) and, later, co-location groups (wave 4).
- **Q-8** `allow_key_set_import` per source, default off, audited, four-eyes (WP-12 column and admin field, WP-14 enforcement; SEC-FED-26 unchanged). Client parameters pushed under #131 are not an import.

## 8. Implementation order (one sequential developer)

Order of the tracker is confirmed, with two changes (marked). INC-6 stays between INC-3a and INC-3b/c: `WP-FED-14` needs the `KeyPredicate` contract of WP-06 and `WP-FED-11..14` need the planner files after WP-03.

| Step | Package | Change vs tracker | Notes |
|---|---|---|---|
| I-9 | `WP-FED-01` remainder + `WP-FED-02` | **adds the gate wiring from WP-03** | Smallest gate surface first; `ValidateAsync` transport argument; `FederationException : WebSqlPolicyException` |
| I-10 | `WP-FED-03` | scope reduced (no parameters, no gate) | Widen default to the SQL-text transports at the end |
| I-11 | `WP-FED-05` OLAP facade | **swapped with WP-04** (needs only I-9, I-10); closes the SEC-FED-12 interim debt first | Adds `Olap` to the default list |
| I-12 | `WP-FED-04` at rest | **swapped** (independent of OLAP after the facade) | Adds `AsyncJob` to the default list |
| I-13 | `WP-FED-06` | contract changes in 2 and 3.4 | |
| I-14 | `WP-FED-11` | unchanged | |
| I-15 | `WP-FED-12` | unchanged | |
| I-16 | `WP-FED-14` | unchanged | |
| I-17 | `WP-FED-13` | unchanged; #130 and #131 hooks as in section 2 | |
| I-18 | `WP-FED-07` | Q-6 flow | |
| I-19 | `WP-FED-08` | extend `RelationExecutability`, not replace | Adds `GraphQl` to the default list |
| I-20 | `WP-FED-09` | unchanged | |
| I-21 | `WP-FED-10` remainder, Phase 6 doc corrections (#125) | cases mostly shipped with I-9..I-20 | |

The tracker step labels for I-11 and I-12 must be swapped. I-26 (#131) is unchanged and still needs I-5, I-14, I-15 (and I-17 for the subtree hand-off); its `WP-1`/`WP-2` have no INC-6 dependency, so the owner may pull them forward after I-10 without changing this plan.

## 9. Security notes for Phase 3 (S-10)

1. Verify the ordering invariant after moving the gate: no connector call before all tables are authorized and the gate passed, in `ExecuteFederatedInternalAsync`, `ValidateAsync`, Flight info, async submit and the facade (spy test).
2. Confirm one text and one code for "disabled" and "transport not allowed" is acceptable, and that `FederationException` deriving from `WebSqlPolicyException` cannot be swallowed by a broader catch that returns the raw message.
3. Facade versus interim controls: check D-2 line by line. The interim Phase 3 findings (engine blocklist bypass, BC-OLAP-02/03/17) must each map to a facade test or to a deleted code path. Re-check that single-source OLAP callers lose DuckDB-dialect SQL (breaking) and not governance; that the `OlapRequestLimiter` lease is taken before the first read on the facade; that `Quickstart` is the only unauthenticated path.
4. Default-list widening: review each step in 3.1 (SQL-text transports after I-10, `Olap`, `AsyncJob`, `GraphQl`) against its conditions; `Enabled=false` must keep all of them inert.
5. Predicate drift: `IsExecutable` (schema) versus tree-service runtime checks versus splitter classification; test that no relation is advertised that the runtime refuses and none is executed that the schema omits.
6. Clear definition (D-3): `MaskedColumnSet` (masked-string staging schema) is not a security predicate; pushdown, key sets and relation keys must use effective column access.
7. Q-6 and Q-8 flows: both-owner approval without owner fallback, audit before/after, epoch bump of both tables; `allow_key_set_import` default off in DDL x3 and shapes.
8. `FEDERATION_SOURCE_ERROR` and provenance disclose no driver text and only authorized sources; the audit-reason strings are not echoed.
9. Interaction with #128 (list guard), #130 (`ValidateFederated` before extraction, no zone parameters until #131) and #131 (contract is the only value carrier; pushdown treats parameter references as non-pushable until I-26).

## 10. Changelog

- 2026-10-16: Delta created from `main` after PRs #139-#142 and the Phase 2 plans of this branch (P-10). No change to `PLAN-FED-01` itself; its notes at 4.1, 4.5 and 4.8 remain correct and are refined here.
