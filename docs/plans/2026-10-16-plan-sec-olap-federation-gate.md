# Implementation Plan: PLAN-SEC-OLAP-01 - Close the Ungated Cross-Source Path of `POST /api/v1/olap/query`

**Document ID:** `PLAN-SEC-OLAP-01` (revision 1, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [REQ-FED-01](2026-10-16-req-cross-source-all-consumers.md) (findings 4 and 5, matrix row 17, D-1, D-7, D-11, FR-2, FR-13, AC-4, R-5, Q-2) and [PLAN-FED-01](2026-10-16-plan-cross-source-all-consumers.md) (sections 3, 4.1 WP-FED-01, 4.5 WP-FED-05, security conditions SEC-FED-02, 08, 12, 17)
**Parent Plans:** [00-master-plan-overview.md](00-master-plan-overview.md), [PLAN-FED-01](2026-10-16-plan-cross-source-all-consumers.md)
**Branch:** `fix/sec-olap-federation-gate` (separately mergeable, independent of `feat/cross-source-all-consumers`)
**Status:** PHASE 3 REVIEWED - APPROVED WITH CONDITIONS (2026-10-16). Phase 4 may start only with the binding conditions BC-OLAP-01 to BC-OLAP-18 of the Phase 3 Security Review (last section) folded into WP-SO-1..3.

**Binding project rules applied:** `.NET 10`, C# 13; fail closed; least privilege on sources; no upgrade migrations (this plan needs no schema change); all text English; Phase-4 TDD developers are Sonnet; subagents only implement, the lead builds and tests once (Release, warnings as errors, before the push).

---

## 1. Verification of the Finding Against the Code (`83853479`)

Method: code reading only (no build, no test run). "Confirmed" means the code path exists as described; nothing below was executed.

### 1.1 Can a caller join two data sources through OLAP while `CrossSource` is disabled?

**Confirmed (by code path).**

| Step | Evidence |
|---|---|
| The endpoint is mapped whenever `DuckDbOlap.Enabled` is true, and that flag defaults to **true** | `GatewayApplicationBuilderExtensions.cs:224-227`; `DistributedCacheOptions.cs:60` (`DuckDbOlapOptions.Enabled { get; init; } = true`) |
| The handler checks only `DuckDbOlap.Enabled`, authentication (or Quickstart) and a non-empty `sql`; no read of `WebSql:CrossSource:*` and no transport | `DuckDbOlapEndpoints.cs:44-50`, `:52-59`, `:74-79`, `:81-83` |
| The service accepts any number of client-named tables (up to `DuckDbOlap.MaxTableCount`, default 10) and puts no restriction on how many distinct data sources they belong to | `OlapStagingService.cs:90-102` |
| Each table is resolved by catalog name, passed through the data-source allowlist, one access decision and a connector lookup; **no** step counts distinct sources and none reads `CrossSourceOptions` (the file contains no reference to `CrossSource`, `AllowedTransports` or `SqlTransports`) | `OlapStagingService.cs:107-167` |
| A table is stageable when a connector is registered for it and it is either `DataSourceType.Sql` or served by a connector that is not `default-sql`/`sql` | `OlapStagingService.cs:155-167` |
| Rows are read through `GovernedConnectorReader` and staged; the client SQL then runs against all staged tables in one DuckDB session | `OlapStagingService.cs:184-199`, `:221`, `:225-228`; `DuckDbOlapEngine.cs:118-124`, `:129-143` |

Result: with `WebSql:CrossSource:Enabled=false` (the default, `RestApiOptions.cs:66`) a caller who is authorised for two tables that belong to two different `SourceName`/domain values can send `{"sql":"SELECT ... FROM a JOIN b ON ...","tableNames":["a","b"]}` and receive the joined result. The SQL channels refuse the same request (`FederatedDuckDbExecutionService.cs:243-248` throws when `Enabled` is false; `:250-257` enforces the transport list). No test in `OlapStagingServiceTests.cs` or `OlapEndpointResponseTests.cs` covers a two-source request or the gate.

**Not verified at runtime:** the PRD states that HTTP/lakehouse tables end in "No active connector registered" (row 17). The condition at `OlapStagingService.cs:155-159` also admits any non-`default-sql` connector for non-SQL tables, so HTTP tables are reachable if such a connector is registered in a deployment. Phase 4 verifies this with a test and, regardless of the answer, the new gate treats any non-SQL source as cross-source (section 4.2).

### 1.2 Other guardrails the OLAP path does not apply (all confirmed by code)

| Guardrail of the SQL federation path | OLAP today |
|---|---|
| `CrossSource.Enabled` (`FederatedDuckDbExecutionService.cs:243`) | not evaluated |
| `AllowedTransports` / transport name (`:250-257`) | not evaluated; `OLAP` is not a registered transport (`SqlTransports`, `IGovernedSqlExecutionService.cs:16-26`) |
| `CrossSource.MaxTableCount` (default 5, `:289-294`) | `DuckDbOlap.MaxTableCount` (default 10) only |
| `AllowNonEquiJoins=false` (`CrossSourcePlanner.cs:151-158`) | not evaluated (CROSS JOIN and non-equi joins run) |
| Masked/denied column guardrails SEC-JOIN-01, SEC-FILTER-01 (`CrossSourcePlanner.cs:120-128`, `FederatedDuckDbExecutionService.cs:477-478`, `GovernedSqlExecutionService.cs:290-340`) | not evaluated: the client SQL is never analysed |
| Byte budgets `MaxStagedBytesPerTable`/`MaxTotalStagedBytes` (`FederationBudget`, `:318-327`) | `GovernedRowPolicy` is built with `MaxRows` only (`OlapStagingService.cs:188-192`); no byte cap on staged data |
| Per-source read audit `WebSqlCrossSourceSourceRead` with a shared correlation id (`FederatedStagingService.cs:258-268`) | `OLAP_TABLE_STAGED` and `OLAP_QUERY` audits exist and fail closed (`OlapStagingService.cs:208-218`, `:230-242`) but use a different event model |
| `MaskedColumns` passed to the staging engine (`FederatedStagingService.cs:281-293`) | not set (`OlapStagingService.cs:221` constructs `OlapTableSource(meta.Identifier, maskedRows, meta)`); masked columns are typed by catalog type in DuckDB (`DuckDbOlapEngine.cs:249-250`) |

### 1.3 What governance IS applied (confirmed)

- Tenant identity required (`OlapStagingService.cs:51-55`), data-source allowlist per tenant (`:131-141`, SR-H-06), one table access decision with ReBAC, consents, Casbin and RLS (`:145-153`), a registered connector (`:155-167`).
- Governed read per table: row filter pushed down or applied in memory, **denied columns stripped, sensitive columns masked exactly once before the rows leave the reader** (`GovernedConnectorReader.cs` `ReadAsync`/`ProjectRow`, `:100-123`, `:212-250`; invoked at `OlapStagingService.cs:184-194`). Row and total-row budgets (`:104-106`, `:196-206`).
- Required audit (fail closed, 503) before data is returned (`:57-86`, `:208-242`).
- Engine sandbox: single statement, `SELECT`/`WITH` only, keyword and function blocklist, `enable_external_access=false`, `lock_configuration`, memory/thread/temp limits from `DuckDbOlap:*`, concurrency semaphore, result row/byte caps, timeout with interrupt (`DuckDbOlapEngine.cs:67`, `:96-115`, `:405-506`).

The client SQL itself is validated only by a token blocklist (`DuckDbOlapEngine.cs:405-506`, `DisallowedTableFunctionsRegex` `:570-573`), not by AST analysis and not by a function allowlist.

### 1.4 Can a join or filter on a masked/HMAC column be expressed?

**Yes, expressed and executed: confirmed.** Nothing inspects the client SQL for column usage (section 1.2). The staged table has every catalog column; the join or `WHERE` runs on the staged (already masked) value.

**What this does and does not disclose (stated carefully):**

- **Not confirmed:** the classic plaintext probing oracle (`WHERE ssn = '123-45-6789'` evaluated against raw data). In OLAP the comparison runs in DuckDB on values that were masked by `GovernedConnectorReader` before staging (`OlapStagingService.cs:184-194`), so a literal cannot be tested against the unmasked value. Denied columns are stripped from rows (staged as NULL because the DuckDB table is built from catalog columns, `DuckDbOlapEngine.cs:230-252`), so they give no signal. This holds unless `danger_disable_column_masking` is set (Development-only switch).
- **Confirmed (bounded impact):** equality joins and filters on masked representations are possible. (a) HMAC columns: deterministic, tenant-scoped pseudonyms (`ProjectRow` -> `ScopeRuleForTenant`) can be joined across two sources, i.e. cross-source re-identification/correlation by pseudonym. The governed SQL path **allows this too** for HMAC (`GovernedSqlExecutionService.cs:300-308` exempts effective-HMAC columns), so this is not a divergence by itself; what diverges is that OLAP allows it with the federation switched off. (b) REDACT/constant masks: joins produce Cartesian products and amplify rows (resource abuse, not disclosure). (c) Partial/format-preserving masks: equality on the visible part is possible and is exactly what the caller can already see.
- **Real gap that matters:** SEC-JOIN-01/SEC-FILTER-01 are a house rule and are enforced for every other SQL channel (and SEC-FED-02/SEC-FED-12 require parity for OLAP). OLAP currently violates the rule for REDACT/ABAC-masked and denied columns, and violates the switch for everything.

### 1.5 Concrete impact (severity statement)

| Aspect | Rating | Reason |
|---|---|---|
| Federation switch bypass (join of two sources with `Enabled=false`, default `AllowedTransports`) | **High** | Operator decision "no cross-source" is ineffective for the one endpoint that is on by default; also bypasses `AllowNonEquiJoins`, `CrossSource.MaxTableCount`, byte budgets |
| Masked-column join/filter | **Medium** | Operates on masked values, so no plaintext oracle; enables cross-source pseudonym correlation (also possible via WebSQL when enabled), Cartesian/resource abuse, and violates the stated guardrail |
| Client DuckDB SQL without AST analysis | **Medium (residual)** | Blocklist, not allowlist; DuckDB-specific functions not on the list stay callable inside the sandbox (no external access, no config change). Closed fully only by the facade (WP-FED-05) |
| Dead options (`MaxMemory`, `MaxTempDirectorySize`, `MaxParallelSourceReads`) | **Low-Medium** | Operator believes federated memory/spill/parallelism are bounded; the engine uses `DuckDbOlap:*` (`DuckDbOlapEngine.cs:108-113`) and staging is sequential. Misleading configuration, not an exploit |
| Unvalidated `AllowedTransports` | **Low-Medium** | A typo silently denies a transport; with PLAN-FED-01's widened default a typo in a deny list would silently allow (T-9) |

---

## 2. Architecture Decision

### 2.1 Chosen scope: minimal gate first (this plan), full facade later (WP-FED-05)

This plan ships, in one separately mergeable change:

1. A fail-closed **federation gate** for OLAP requests that stage tables from more than one data source (or any non-SQL source): requires `WebSql:CrossSource:Enabled=true` and the transport name `Olap` in the effective `AllowedTransports`.
2. **Masked/denied column guardrails** (SEC-JOIN-01, SEC-FILTER-01, the same function as the SQL path) on cross-source OLAP requests, by analysing the client SQL with the gateway SQL engine; unanalysable SQL on a cross-source request is rejected.
3. Authorization-before-read ordering and the cross-source table-count budget for OLAP.
4. Pass `MaskedColumns` to the engine (parity with `FederatedStagingService`).
5. The dead-option decision and `AllowedTransports` startup validation.

Single-source OLAP (all requested tables in one data source) is unchanged. It remains on the DuckDB SQL dialect with the existing sandbox.

### 2.2 Why not do WP-FED-05 (full facade) now

| Criterion | Minimal gate now | Full facade now |
|---|---|---|
| Closes finding 4 (bypass) | Yes for the cross-source join and the guardrails | Yes, completely (no client engine SQL) |
| Size | about 300 LOC production, 450 LOC tests | WP-FED-05 is estimated 350 / 450 **and depends on WP-FED-01 and WP-FED-03** |
| Dependencies | none (uses existing executor, analyzer, guardrail function) | needs the gate (WP-01) and the hardened SQL federation (WP-03, which owns `FederatedDuckDbExecutionService`, `CrossSourcePlanner`, `FederatedStagingService`); WP-03 is 600 / 800 LOC |
| Breaking change | Only cross-source OLAP, only when federation is off or `Olap` not allowed | Every OLAP client: dialect changes from DuckDB SQL to the gateway SQL dialect, `tableNames` ignored, endpoint requires `RequireAuthorization()` |
| Risk | low; reversible by one option | high; semantics change for single-source analytics users (DuckDB-specific functions, `QUALIFY`, list/struct syntax stop working) |
| Time to close a High finding | days | blocked behind the federation core |

Decision: gate first. The facade stays as WP-FED-05 and then deletes the code this plan adds to `OlapStagingService` (the gate and the guardrail call move into the governed pipeline). To avoid throwaway work, the pieces that survive the facade are created in their final homes (section 6): `FederationGate`, the transport constant, the options validator. Only the OLAP-local sequencing and the analyzer call in `OlapStagingService` are transitional, and they are small.

Rejected alternatives: (a) disable the OLAP endpoint by default (`DuckDbOlap.Enabled=false`): closes the hole by removing the feature for single-source users and does not fix the operator-visible gate; kept as an optional operator mitigation in the upgrade note. (b) Reject every multi-table OLAP request: breaks the documented join use case even for single-source joins, which are not a federation. (c) Require the gate for single-source OLAP as well: not required by D-1 (the gate controls cross-source), would break all existing OLAP users.

### 2.3 Cross-source definition (parity with the router)

A request is cross-source when, after resolving metadata, either more than one distinct `meta.Table.SourceName ?? meta.Identifier.Domain` exists, or any table is not `DataSourceType.Sql`. This is the rule of `CrossSourceQueryRouter.cs:109-132`. A shared helper `CrossSourceClassifier.IsCrossSource(IEnumerable<TableMetadata>)` lives next to the gate so the router can adopt it in WP-FED-03 (not changed here).

---

## 3. Contracts

```csharp
// src/Autheris.Application/Federation/FederationGate.cs   (namespace Autheris.Application.Federation; final home per PLAN-FED-01 section 3)
public interface IFederationGate
{
    // Throws WebSqlPolicyException with a fixed message (WP-FED-02 later swaps the exception type at this single throw site).
    // MUST be called only after authorization of every requested table succeeded (generic denial first, SEC-FED-03).
    void EnsureAllowed(string? transport);
    bool IsEnabledFor(string? transport);                       // no throw
}
public sealed class FederationGate(IOptions<GatewayOptions> options) : IFederationGate { /* singleton */ }

// src/Autheris.Application/Sql/Interfaces/IGovernedSqlExecutionService.cs  (SqlTransports, additive)
public const string Olap = "Olap";
public static IReadOnlyList<string> All { get; }                 // all registered names, used by the validator

// src/Autheris.Application/Federation/CrossSourceClassifier.cs
public static bool IsCrossSource(IEnumerable<TableMetadata> tables);

// OlapStagingService gains two constructor dependencies: IFederationGate gate, ISqlEngine sqlEngine (Analyze only)
// IOlapStagingService and OlapQueryCommand are unchanged (route and body stay).

// src/Autheris.Api/Configuration/Validation/CrossSourceOptionsValidator.cs
public sealed class CrossSourceOptionsValidator : IValidateOptions<GatewayOptions>
```

Behavioural contracts:

- `FederationGate.EnsureAllowed` is the only code in the OLAP path that reads `CrossSource.Enabled` and `AllowedTransports`. A null/blank transport is denied; an empty `AllowedTransports` list denies everything; comparison is case-insensitive (same as `FederatedDuckDbExecutionService.cs:253-254`).
- Denial text is fixed and identical for "disabled" and "transport not allowed": `Cross-source queries are not enabled for this endpoint.` (HTTP 403). It is produced only after every table passed authorization, so it reveals nothing about tables the caller cannot see.
- Guardrail failures keep the SQL-path message and status (`ColumnPolicyViolation`, mapped to 403 by the OLAP outcome mapper).
- No new response fields. `OlapStagingOutcome` gains no member.

---

## 4. Detailed Design

### 4.1 Request flow in `OlapStagingService.ExecuteAsync` (three phases, no read before the gate)

Today the loop interleaves authorization and the governed read per table (`OlapStagingService.cs:107-222`), so an early table is read before a later table is denied. The new flow:

1. **Phase A - resolve and authorize all tables (no source read).** For each requested table: metadata lookup, `DataSourceAllowlistGuard`, `ResolveAccessAsync`, connector lookup (existing code, moved, behaviour and denial messages unchanged). Collect `(meta, decision, connector)` triples. Any denial returns the existing generic 403/404 immediately (SEC-FED-03: denial of one table never depends on the gate).
2. **Phase B - classify and gate.** `crossSource = CrossSourceClassifier.IsCrossSource(metas)`. If `crossSource`:
   - `gate.EnsureAllowed(SqlTransports.Olap)`; denial -> 403, logged at warning with table list hash (no table names in the response).
   - Distinct table count must be `<= CrossSource.MaxTableCount` (default 5); otherwise 400 with the SQL-path wording.
   - Guardrail analysis (section 4.2).
3. **Phase C - governed reads, audit, execute (existing code).** Unchanged except: `OlapTableSource` receives `MaskedColumns` (section 4.3); for cross-source requests the per-table staged-byte cap is `CrossSource.MaxStagedBytesPerTable` and the total `MaxTotalStagedBytes` via `GovernedRowPolicy.MaxBytes` plus a running total (parity with the SQL path; single-source keeps the existing row caps only); the `OLAP_QUERY` audit details gain `"crossSource": true|false` and `"transport": "Olap"`.

### 4.2 Guardrail analysis of the client SQL (cross-source requests only)

- `sqlEngine.Analyze(sql, tokenOptions, ct)` with the same `SqlTokenSecurityOptions` as the federated executor. If analysis throws (the client SQL uses DuckDB-only syntax the gateway grammar cannot parse), the request is **rejected** (400 `OLAP_SQL_NOT_ANALYZABLE`, fixed message "Cross-source OLAP queries must use the gateway SQL dialect."). Fail closed; this only affects cross-source requests, which were never governed.
- Map each `metadata.ReferencedTables` entry to a staged table by case-insensitive match of `TableName` against the table's catalog name or the sanitized `domain_table` view name (the names `StageTableAsync` creates, `DuckDbOlapEngine.cs:229`, `:260-268`). An unmatched reference is rejected (fail closed).
- For each mapped table call the existing `GovernedSqlExecutionService.EnforceMaskedColumnGuardrails(target, meta, decision, metadata)` (`GovernedSqlExecutionService.cs:290`, internal static, same assembly, no copy). This gives SEC-JOIN-01 and SEC-FILTER-01 with the same HMAC exemption and messages as WebSQL. Unqualified column references match every table (conservative, as in the SQL path).
- Also reject `metadata.JoinCount > 0` with no join column references (CROSS JOIN) unless `CrossSource.AllowNonEquiJoins` (same rule as `CrossSourcePlanner.cs:151-158`).
- The existing `DuckDbOlapEngine.ValidateUserSql` blocklist stays in place for all requests (defence in depth).

Single-source requests: no analysis, no new rejection. Rationale: masking precedes staging, so the guardrails add no confidentiality for them (section 1.4), and DuckDB-dialect single-source queries keep working. The facade (WP-FED-05) later brings single-source to the governed dialect.

### 4.3 `MaskedColumns` parity

Compute `maskedColumns` per table exactly as `FederatedStagingService.cs:276-285` does, extracted into a shared static helper `MaskedColumnSet.For(TableMetadata, TableAccessDecision)` in `Autheris.Application.Olap` (both services call it). Pass it as `OlapTableSource.MaskedColumns` so masked columns are typed `VARCHAR` in DuckDB (`DuckDbOlapEngine.cs:249-250`). Removes a latent type-mismatch (masked string into a BIGINT staging column, not verified at runtime) and is needed for parity of join behaviour.

### 4.4 Options: dead options and validation

| Option | Decision | Why |
|---|---|---|
| `CrossSourceOptions.MaxMemory`, `MaxTempDirectorySize` | **Remove** (PRD D-11) from `RestApiOptions.cs:85-86`; documented replacement `Gateway:DuckDbOlap:MaxMemory`, `MaxTempDirectorySize` | The one DuckDB engine is configured by `DuckDbOlap:*` (`DuckDbOlapEngine.cs:108-113`); a second, ignored pair misleads. The configuration binder ignores unknown keys, so existing config files still load; the removal is stated in the release note |
| `MaxParallelSourceReads` | **Keep, validate range 1..16, document as an upper bound**; staging stays sequential, which trivially satisfies the bound | Making staging parallel touches `FederatedStagingService` (WP-FED-03/09 ownership, scoped services and audit ordering) and belongs to the performance work of PLAN-FED-01 (#122-#124). SEC-FED-08 is satisfied (the bound holds); true parallelism is a later optimisation. XML doc on the property says "currently reads are sequential" |
| `AllowedTransports` | Validate names | New `CrossSourceOptionsValidator` (`IValidateOptions<GatewayOptions>`): every entry must equal (case-insensitive) a constant of `SqlTransports.All` (includes `Olap`); unknown names fail startup with the offending value and the valid list; whitespace/blank entries fail; duplicates are tolerated; an explicitly empty list stays valid (deny all, existing behaviour); `null` stays valid (default). Registered next to `GatewaySecurityOptionsValidator` (`GatewayServiceCollectionExtensions.cs:144`) |
| `MaxTableCount`, `MaxStagedRowsPerTable`, `MaxTotalStagedRows`, `MaxStagedBytesPerTable`, `MaxTotalStagedBytes`, `TimeoutSeconds` | validate `> 0` (the SQL path silently substitutes defaults for `<= 0`, `FederatedDuckDbExecutionService.cs:289`, `:318-327`) | fail closed at startup instead of a silent default |

`GraphQL` is not yet a registered transport; PLAN-FED-01 WP-FED-01 adds it (and `ProtectedStagingEnabled`, the default expansion) to `SqlTransports.All`/`FederationTransports`. The validator reads `SqlTransports.All`, so that addition is a one-line change.

---

## 5. Fail-Closed Behaviour (summary)

| Situation | Result |
|---|---|
| Cross-source request, `CrossSource.Enabled=false` | 403 fixed message, after authorization of all tables, before any read |
| Cross-source request, `Olap` not in effective `AllowedTransports` (default list is `WebSql`, `Trino`) | 403 same message |
| `AllowedTransports` empty list | cross-source OLAP denied |
| Any table denied/not found/not allowlisted | existing generic 403/404 (identical regardless of the gate) |
| Cross-source SQL does not parse in the gateway dialect | 400 `OLAP_SQL_NOT_ANALYZABLE`, no read |
| Join/filter on a masked or denied non-HMAC column in a cross-source request | 403 policy violation, no read |
| Table referenced by SQL but not among staged tables (cross-source) | 400, no read |
| `AllowedTransports` contains an unknown name | host does not start |
| Audit write fails | 503 as today |
| Gate/analyzer throws unexpectedly | request fails (exception path, 400/500 generic), no read |
| Single-source request | unchanged behaviour |

---

## 6. Files Changed and Ownership

Ownership = which later work package may touch the file, to keep parallel branches mergeable.

| File | Change | Owner / coordination |
|---|---|---|
| `src/Autheris.Application/Federation/FederationGate.cs` (new) | `IFederationGate`, `FederationGate` | **Created here** at the path PLAN-FED-01 section 3 fixes; WP-FED-01 extends (no new file) |
| `src/Autheris.Application/Federation/CrossSourceClassifier.cs` (new) | `IsCrossSource` | here; WP-FED-03 may adopt in the router |
| `src/Autheris.Application/Sql/Interfaces/IGovernedSqlExecutionService.cs` | add `SqlTransports.Olap`, `SqlTransports.All` (additive, `SqlTransports` region only) | here; WP-FED-01 later moves/extends into `FederationTransports` |
| `src/Autheris.Application/Olap/OlapStagingService.cs` | three-phase flow, gate, guardrail call, budgets, `MaskedColumns`, audit details | **here**; WP-FED-05 shrinks/removes (full ownership then) |
| `src/Autheris.Application/Olap/MaskedColumnSet.cs` (new) and `FederatedStagingService.cs:276-285` (replace the loop by the helper call only) | shared helper | here; WP-FED-03/09 own the rest of `FederatedStagingService.cs` (single small region touched, rebase trivial) |
| `src/Autheris.Application/Sql/Services/GovernedSqlExecutionService.cs` | none (the guardrail function is already `internal static`); visibility stays | untouched |
| `src/Autheris.Domain/Options/Protocols/RestApiOptions.cs` | remove `MaxMemory`, `MaxTempDirectorySize` from `CrossSourceOptions`; XML doc on `MaxParallelSourceReads` | here; WP-FED-01 later adds its options elsewhere in the same class (adjacent regions) |
| `src/Autheris.Api/Configuration/Validation/CrossSourceOptionsValidator.cs` (new) | validator | here; WP-FED-01 extends (relation limits) |
| `src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.cs` | one registration line next to `:144` | here |
| `src/Autheris.Api/Extensions/DependencyInjection/GatewaySqlEngineServiceExtensions.cs` | register `IFederationGate` singleton beside the OLAP registrations (`:94-95`) | here; WP-FED-01 may relocate to `FederationServiceExtensions.cs` |
| `src/Autheris.Api/Endpoints/DuckDbOlapEndpoints.cs` | none functionally; the outcome mapping already returns `outcome.StatusCode` | WP-FED-05 |
| `src/Autheris.Application/Olap/DuckDbOlapEngine.cs` | none | WP-FED-04 (spill) and WP-FED-05 |
| tests (section 7) | new and updated | here |
| Docs (Phase 6 only): `docs/features/f-data-03-duckdb-olap.md`, `docs/configuration-guide.md` | behaviour and options; **not edited by this plan** | Phase 6 |

Nothing in `src/TrinoSqlEngine` changes.

---

## 7. TDD Test List (red first)

Naming: `tests/Autheris.Tests.Unit/Olap/OlapFederationGateTests.cs` (new), additions to `OlapStagingServiceTests.cs`, `tests/Autheris.Tests.Unit/Security/DuckDbOlapSecurityTests.cs`, `CrossSourceAllowedTransportsBindingTests.cs`, new `CrossSourceOptionsValidatorTests.cs`, `FederationGateTests.cs`. Write each test first and watch it fail.

**Gate (finding 4)**
1. `CrossSourceOlap_TwoSources_FederationDisabled_Returns403_AndNoConnectorIsRead` (fake connectors record reads; asserts zero reads, zero staging audits).
2. `CrossSourceOlap_FederationEnabled_OlapNotInAllowedTransports_Returns403` (default list `WebSql, Trino`).
3. `CrossSourceOlap_EnabledAndOlapAllowed_Succeeds` (positive).
4. `CrossSourceOlap_EmptyAllowedTransports_Returns403`.
5. `CrossSourceOlap_TransportMatchIsCaseInsensitive` (`"olap"`).
6. `SingleSourceOlap_TwoTablesSameSource_FederationDisabled_StillSucceeds` (no behaviour change).
7. `NonSqlSource_Alone_IsTreatedAsCrossSource` (HttpDeclarative table, federation off -> 403).
8. `Denial_OfSecondTable_ReturnsGenericDenial_BeforeGate_AndNeverReadsFirstTable` (ordering: authorization of all tables precedes any read; the response is identical with the gate on or off).
9. `GateDenialBody_IsIdentical_ForDisabledAndTransportNotAllowed` and contains no table name.
10. `CrossSourceOlap_TableCountAboveCrossSourceMax_Returns400` (6 tables, `CrossSource.MaxTableCount=5`, `DuckDbOlap.MaxTableCount=10`).
11. `CrossSourceOlap_Cartesian_CrossJoin_Rejected_UnlessAllowNonEquiJoins`.
12. `CrossSourceOlap_StagedBytesAboveBudget_Returns400`.
13. `OlapQueryAudit_ContainsCrossSourceFlagAndTransport`.

**Guardrails (SEC-FED-02/12 for OLAP)**
14. `CrossSourceOlap_JoinOnRedactedColumn_Rejected` (SEC-JOIN-01).
15. `CrossSourceOlap_FilterOnRedactedColumn_Rejected` (SEC-FILTER-01; also `ORDER BY`/`HAVING` where the analyzer reports them).
16. `CrossSourceOlap_JoinOnDeniedColumn_Rejected`.
17. `CrossSourceOlap_JoinOnHmacColumn_Allowed_ParityWithWebSql` (documents the intentional HMAC exemption; same function as WebSQL).
18. `CrossSourceOlap_MaskTypeMatrix_REDACT_PARTIAL_HASH_HMAC_ParityWithWebSqlGuardrail` (table-driven; expected outcomes equal `EnforceMaskedColumnGuardrails` for the same metadata).
19. `CrossSourceOlap_UnqualifiedMaskedColumn_Rejected` (unqualified reference matches every table).
20. `CrossSourceOlap_DuckDbOnlySyntax_Returns400NotAnalyzable_NoRead`.
21. `CrossSourceOlap_ReferencedTableNotStaged_Rejected`.
22. `CrossSourceOlap_AliasedAndSchemaQualifiedTables_AreMappedToStagedTables` (alias, `domain_table` view name).
23. Regression: existing `DuckDbOlapSecurityTests` and `DuckDbOlapValidatorG1Tests` (blocklist) stay green and unchanged (`read_csv`, `COPY`, `ATTACH`, dollar quoting).

**Masked column typing**
24. `StagedMaskedBigintColumn_IsVarcharInDuckDb_AndQueryRuns` (`MaskedColumns` passed; red today because `MaskedColumns` is null).

**Options and startup**
25. `AllowedTransports_UnknownName_FailsStartupValidation_ListsValidNames`.
26. `AllowedTransports_Typo_Olpa_Fails`; `Blank entry fails`; `Empty list valid`; `null valid`; `Olap and WebSql valid in any case`.
27. `CrossSourceOptions_NonPositiveLimits_FailValidation` (`MaxTableCount`, row/byte limits, `TimeoutSeconds`, `MaxParallelSourceReads` outside 1..16).
28. `CrossSourceOptions_NoLongerExposesMaxMemoryAndMaxTempDirectorySize` (reflection test; guards the removal).
29. `FederationGate_Matrix` (Enabled x transport x list; blank transport denied).
30. Architecture test: `OlapStagingService` is the only OLAP code path reading cross-source classification; no reference to `CrossSourceOptions.Enabled` outside `FederationGate` and the existing SQL executor (to be tightened to "gate only" by WP-FED-01/03).

Integration test (one, `tests/Autheris.Tests.Integration`, using `DOTNET_USE_POLLING_FILE_WATCHER=true` under WSL): two SQLite sources, HTTP `POST /api/v1/olap/query`, default config -> 403; with `CrossSource:Enabled=true` and `AllowedTransports:0=Olap` -> 200.

---

## 8. Work Packages

| WP | Title | Content | Depends on | LOC (prod / test) |
|---|---|---|---|---|
| **WP-SO-1** | Gate and options validation | `SqlTransports.Olap/All`, `FederationGate`, `CrossSourceClassifier`, `CrossSourceOptionsValidator`, option removal and range checks, DI; tests 25-29 | none | 120 / 180 |
| **WP-SO-2** | OLAP three-phase flow and gate | restructure `OlapStagingService` (authorize all, classify, gate, budgets, audit fields, `MaskedColumnSet`); tests 1-13, 24 | WP-SO-1 | 120 / 200 |
| **WP-SO-3** | Client SQL guardrails | analyzer call, table mapping, `EnforceMaskedColumnGuardrails`, non-equi rule; tests 14-22 | WP-SO-2 | 70 / 130 |
| Phase 6 | Docs | `f-data-03-duckdb-olap.md`, `docs/configuration-guide.md`, master plan status | all | docs only |

Total about 310 production and 510 test lines; three small commits, one PR. WP-SO-1 can run in parallel with the design of WP-SO-2; WP-SO-2 and -3 touch the same file and run sequentially (one developer).

---

## 9. Mapping to PLAN-FED-01 (no duplicated work)

| PLAN-FED-01 item | Status after this plan | Remaining for PLAN-FED-01 |
|---|---|---|
| WP-FED-01 `FederationGate`, `FederationTransports` | **Gate core and `Olap` constant delivered here** at the final path/namespace; `IsEnabledFor`, `EnsureAllowed` signatures match section 3 of the plan | Wire the gate into `FederatedDuckDbExecutionService` (Execute and `ValidateAsync`, owned by WP-FED-03), GraphQL decorator, `GraphQL` transport name, `ProtectedStagingEnabled`, default-list expansion, `FederationTransports` wrapper |
| WP-FED-01 `IValidateOptions<CrossSourceOptions>` unknown names abort startup; empty list allowed | **Delivered here** (`CrossSourceOptionsValidator`) | Relation limits (`MaxBatchSize`, `MaxBatches`, ...), `GraphQL` name |
| WP-FED-01 remove `MaxMemory`, `MaxTempDirectorySize` (D-11) | **Delivered here** | none |
| `MaxParallelSourceReads` (D-11, SEC-FED-08) | **Bound validated and documented; staging stays sequential** | Implement bounded parallelism only if #122-#124 performance work needs it (Phase 2 of that work decides) |
| WP-FED-05 OLAP facade | **Not done.** Gate and guardrails for cross-source OLAP delivered; `OlapStagingService` is the transitional home | Full facade over `IGovernedSqlExecutionService`, `tableNames` deprecation header, `RequireAuthorization()`, deletion of `OlapStagingService`/`ValidateUserSql` if unreferenced. After the facade, the `Olap` gate call is made by the governed executor (WP-FED-03 wiring), and this plan's phase-B code is deleted |
| SEC-FED-02 (clear join keys, OLAP matrix) | **Partly**: SEC-JOIN-01 parity for cross-source OLAP (HMAC allowed, as in WebSQL). The stricter "keys must be Clear, HMAC excluded" rule is for GraphQL relations and is unchanged | GraphQL/relation keys |
| SEC-FED-03 (anti-oracle ordering) | **Delivered for OLAP** (authorize all, then gate) | other transports |
| SEC-FED-08 (fan-out bounds) | `MaxTableCount`, byte budgets, `MaxParallelSourceReads` bound enforced/validated for OLAP | tree/relation limits |
| SEC-FED-12 (single gate, startup validation, OLAP closed) | **Gate evaluator and startup validation delivered**; OLAP bypass closed for cross-source. "Client DuckDB SQL not executed" is **not** met (single-source and the DuckDB dialect remain until WP-FED-05); recorded as an accepted interim residual (section 10, R-1) | facade |
| SEC-FED-17 (fail-closed defaults, option parse errors abort startup) | partly (validator) | rest |
| REQ AC-4 (OLAP refuses masked join, honours gate, same rows as WebSQL) | gate and guardrail parts met; "same rows as WebSQL" needs the facade | facade |

Merge-order guidance for the PLAN-FED-01 branch: this plan merges first; WP-FED-01 then rebases (it keeps `FederationGate.cs` and the validator, adds its own members) and its estimate shrinks by about one third; WP-FED-05 depends on this plan instead of "01 + 03" for the gate part.

---

## 10. Behaviour Change and Upgrade Note (may break existing OLAP callers)

- **Breaking for cross-source OLAP callers.** A request that stages tables from more than one data source (or from any non-SQL source) now returns **403** unless the operator sets `Gateway:WebSql:CrossSource:Enabled=true` **and** adds `Olap` to `Gateway:WebSql:CrossSource:AllowedTransports`. Note that setting `AllowedTransports` replaces the default `WebSql, Trino`: operators who also use WebSQL/Trino federation must list all three (`WebSql`, `Trino`, `Olap`). Until PLAN-FED-01 widens the default, `Olap` is never implied.
- Cross-source OLAP SQL must now parse in the gateway SQL dialect and may not join or filter on masked/denied columns (HMAC columns remain joinable); DuckDB-only syntax in a cross-source request returns 400 `OLAP_SQL_NOT_ANALYZABLE`. A cross-source request may stage at most `CrossSource.MaxTableCount` (default 5, was `DuckDbOlap.MaxTableCount` 10) tables, and cross joins are rejected unless `AllowNonEquiJoins`.
- **Unchanged:** single-source OLAP (including multi-table joins within one data source), request/response shape, status codes of existing denials, the DuckDB sandbox.
- **Startup behaviour:** an `AllowedTransports` entry that is not a registered transport name now aborts startup (previously silently ignored). `CrossSource:MaxMemory` and `CrossSource:MaxTempDirectorySize` are no longer options; existing keys are ignored by the binder and have never had an effect (use `Gateway:DuckDbOlap:MaxMemory`, `MaxTempDirectorySize`).
- Interim operator mitigation (before upgrading, no code change): set `Gateway:DuckDbOlap:Enabled=false`.
- Release note and `docs/features/f-data-03-duckdb-olap.md` are Phase 6 deliverables.

---

## 11. Risks, Owner Flags and Security Notes for Phase 3

### Risks

| ID | Risk | Mitigation |
|---|---|---|
| R-1 | Interim state still executes client DuckDB SQL for single-source requests (blocklist, not allowlist) and for cross-source requests that parse in the gateway dialect | Accepted until WP-FED-05; sandbox (`enable_external_access=false`, `lock_configuration`, limits) unchanged; Phase 3 to confirm no DuckDB function reachable from a parsable cross-source query can read outside the staged tables |
| R-2 | The analyzer (Trino grammar) and DuckDB interpret the same text differently (parser differential), so a guardrail could be bypassed by syntax that the analyzer reads as harmless | Cross-source requests only; analyzer must accept the whole statement (no partial parse); unmatched referenced tables rejected; the same analyzer feeds the SQL path today; Phase 3 to review differential cases (quoted identifiers, case folding, `USING` joins, `NATURAL JOIN`, lateral, set operations, subqueries, CTE shadowing a staged name) |
| R-3 | Unqualified/aliased column matching over- or under-matches | Conservative (unqualified matches all tables); tests 19, 22 |
| R-4 | Breaking cross-source callers | Documented (section 10); the feature was ungoverned, the break is the fix (REQ Q-2 accepted) |
| R-5 | Merge conflicts with PLAN-FED-01 branches in `RestApiOptions.cs`, `FederatedStagingService.cs`, `SqlTransports` | Edits are additive and tiny in shared files; this plan merges first |
| R-6 | `AllowedTransports` validation aborts startup for operators who configured `GraphQL` or another name ahead of PLAN-FED-01 | Message lists valid names; `GraphQL` is added to `SqlTransports.All` by WP-FED-01 (note for that package) |

### Owner questions

1. Interim default: keep `Olap` out of the default `AllowedTransports` (chosen, fail closed) or include it so that enabling federation immediately enables OLAP cross-source?
2. Is rejecting unparsable cross-source SQL acceptable, or should cross-source OLAP be refused altogether until WP-FED-05?
3. Confirm that single-source OLAP stays on the DuckDB dialect until the facade.
4. Remove `MaxMemory`/`MaxTempDirectorySize` now (chosen, per D-11) or deprecate with a warning for one release?

### Security notes for Phase 3

1. Verify the ordering invariant: no `GovernedConnectorReader.ReadAsync` call (and no connector call) before Phase A has authorized every table and Phase B has passed (test 8 as the evidence).
2. Verify the gate denial cannot be used as a table-existence oracle (identical body for disabled, transport not allowed; produced only after full authorization; timing class: all tables are authorized first).
3. Review the analyzer-to-DuckDB differential (R-2); decide whether any construct must be refused outright in cross-source OLAP requests (`NATURAL JOIN`, `USING`, table functions, `TABLESAMPLE`, set operations, window/`QUALIFY` forms).
4. Confirm the HMAC exemption is acceptable for OLAP (cross-source pseudonym correlation inside one tenant), or whether SEC-FED-02's "keys must be Clear" must apply to client-SQL joins; this plan keeps parity with WebSQL (`GovernedSqlExecutionService.cs:300-308`).
5. Confirm Virtual Filter (F-GOV-14) enforcement for OLAP-staged SQL and HTTP tables: `OlapStagingService.cs:184-193` passes no `VirtualFilterEnforcement`; for SQL tables the predicate travels in `CombinedRowFilterSql` (pushdown or in-memory), for non-attesting HTTP tables `EnsureInMemoryFilterIsEnforceable` denies (CR-10). Phase 3 verifies there is no case where a federated Virtual Filter predicate is silently dropped for OLAP.
6. Confirm the audit story: the `OLAP_TABLE_STAGED`/`OLAP_QUERY` events carry `crossSource` and `transport`; gate and guardrail denials after authorization are logged; decide whether a denial audit record is required (SEC-FED-10 wants denials after authorization audited; if yes, add one required audit record in Phase B and test it, about 20 LOC).
7. Confirm `danger_disable_column_masking` interaction: with masking disabled (Development), guardrails still run for cross-source requests (they are not conditional on masking).
8. Confirm the validator and the removal of options cannot change effective limits silently (option removal removes no enforcement because the options were never read).
9. Check the 400/403 response bodies for table names or policy text in non-Development environments (the guardrail message names column and table; the SQL path returns it to the caller already; decide whether OLAP should return the generic text outside Development).

---

## 12. Changelog

- 2026-10-16: Rev. 1. Phase 2 plan drafted from the code verification (section 1) of finding 4/5 of REQ-FED-01. Scope: minimal gate and guardrails now, full OLAP facade (WP-FED-05) deferred.
- 2026-10-16: Phase 3 security review appended (APPROVED WITH CONDITIONS): findings SEC-OLAP-01 to SEC-OLAP-14, binding conditions BC-OLAP-01 to BC-OLAP-18, mandatory tests T3-01 to T3-30, owner-question recommendations. Plan text above is unchanged; where a binding condition contradicts sections 2 to 7, the condition wins.

---

## Phase 3 Security Review (2026-10-16)

**Reviewer:** `csharp-security-expert` (Phase 3). **Method:** code reading of commit `2b4006d0` plus the plan; nothing was built, run or fuzzed. Every claim below carries a file:line; claims about DuckDB or Trino grammar behaviour that come from product documentation rather than from this repository are marked **[verify]** and must be proven by a red test in Phase 4 before the corresponding mitigation is considered done.

**Verdict: APPROVED WITH CONDITIONS.** The architecture (gate first, facade later, three-phase flow, shared gate and validator in their final homes) is sound and the High finding it targets is real. The plan is not sufficient on its own: the review found one pre-existing bypass of the engine blocklist that also affects single-source requests (SEC-OLAP-01), a guardrail design that DuckDB-only syntax defeats (SEC-OLAP-02), and several gaps in classification, resource bounding, error text and audit. Phase 4 may start once binding conditions BC-OLAP-01 to BC-OLAP-18 are part of WP-SO-1..3. Where a condition contradicts sections 2 to 7, the condition wins.

### P3.1 Threat model

**Assets.** (A1) Rows of every source the caller is not allowed to combine (operator decision `CrossSource.Enabled=false`). (A2) Plaintext behind masked, HMAC and denied columns. (A3) Existence of tables, columns and rows the caller may not see. (A4) Gateway memory, CPU and temp disk, and fairness between tenants. (A5) Integrity and completeness of the audit trail.

**Actor.** An authenticated tenant user (or, in the Quickstart profile, an unauthenticated caller, `DuckDbOlapEndpoints.cs:53-54`) who is authorised for some tables, controls the whole `sql` string and the `tableNames` list, and wants to (a) join or aggregate across sources without the gate, (b) learn masked/denied values or metadata, (c) exhaust resources, (d) escape the DuckDB sandbox.

**Trust boundaries.** (1) HTTP body to `OlapStagingService` (`OlapStagingService.cs:90-102`). (2) Catalog and access resolution to connector read (`:107-193`). (3) Governed rows to the DuckDB session; masking and denial are applied before this boundary (`GovernedConnectorReader.cs:131-134`, `ProjectRow` `:227-253`). This is the confidentiality boundary for A2. It is a strong invariant and it is the reason single-source OLAP is tolerable. It is not a statement about the client SQL. (4) Client SQL to the DuckDB engine (`DuckDbOlapEngine.cs:405-510` blocklist, sandbox `:113`).

| ID | Threat | Entry | Plan coverage |
|---|---|---|---|
| T-O1 | Cross-source join with the switch off | two `tableNames` of different sources | gate (Phase B); depends on classification key (SEC-OLAP-04) |
| T-O2 | Classification evasion | label/case/alias/same-table-twice, non-SQL types, name collisions | partly; SEC-OLAP-04 |
| T-O3 | Blocklist bypass, multi-statement, sandbox escape | client SQL | not covered; SEC-OLAP-01, 03 |
| T-O4 | Guardrail bypass by parser differential | DuckDB-only syntax that Trino parses as something harmless | covered by R-2 only in prose; SEC-OLAP-02 |
| T-O5 | Inference on masked columns (equality, range, LIKE, aggregate) | client SQL on staged masked values | bounded (values are masked before staging); SEC-OLAP-02, 10 |
| T-O6 | Existence oracle (tables, denied columns, gate text) | error text, staged column names, ordering | partly; SEC-OLAP-05, 07, 11 |
| T-O7 | Resource exhaustion (rows, bytes, Cartesian, slots, temp, timeout) | single request or request flood | row caps only; SEC-OLAP-06 |
| T-O8 | Audit gaps (denials, failures, SQL identity) | any | partly; SEC-OLAP-08 |
| T-O9 | Operator mis-configuration (silent default, dead options, typo) | configuration | validator planned; SEC-OLAP-12 |

### P3.2 What was verified and holds

- The plan's section 1 evidence is accurate where checked: the endpoint checks only `Enabled`, authentication and a non-empty `sql` (`DuckDbOlapEndpoints.cs:36-79`); the service reads no cross-source option and interleaves authorization and the governed read per table (`OlapStagingService.cs:107-222`); `OlapTableSource` is built without `MaskedColumns` (`:221`); the engine takes only `DuckDbOlap:*` limits (`DuckDbOlapEngine.cs:113`).
- Masking precedes staging and denied columns are stripped before the rows reach DuckDB (`GovernedConnectorReader.cs:131-134`, `:227-241`); sensitive catalog columns are masked unless an explicit Clear rule exists (`:233-237`). The plan's statement that no plaintext equality oracle exists on a masked value is correct. HASH, HMAC and HMAC_SHA256 are all keyed (`MaskingRule.cs:55`), so a candidate-plaintext probe such as `WHERE h = sha256('x')` does not work. The one trust assumption is the connector self-attestation `InDbColumnMaskingExecuted` (`GovernedConnectorReader.cs:131`), which is withheld for HTTP sources (`:110`).
- The gate message design (fixed text, after full authorization) is the right shape for SEC-FED-03.

### P3.3 Findings

| ID | Severity | Title |
|---|---|---|
| SEC-OLAP-01 | **High** | Engine blocklist can be desynchronised from DuckDB lexing by escape strings (also single-source) |
| SEC-OLAP-02 | **High** | The Trino-analyzer guardrail is bypassable with DuckDB-only syntax and a function denylist is not enough |
| SEC-OLAP-03 | Medium | Blocklist, not allowlist; reachable functions and extension autoload are unbounded |
| SEC-OLAP-04 | Medium | The cross-source classification key is weaker than the physical-source identity |
| SEC-OLAP-05 | Medium | Denied columns are staged by name and returned as NULL columns (existence oracle, also single-source) |
| SEC-OLAP-06 | Medium | Resource bounds: no byte cap, no per-tenant concurrency, reads outside the timeout and the semaphore |
| SEC-OLAP-07 | Medium | Guardrail, analyzer and gate error text must be generic outside Development |
| SEC-OLAP-08 | Medium | Audit: denials, failed queries and SQL identity are not on record |
| SEC-OLAP-09 | Low-Medium | Phase C exceptions escape the outcome mapping |
| SEC-OLAP-10 | Low | HMAC exemption: accepted for parity, but inconsistent rule-type set |
| SEC-OLAP-11 | Medium | Bare table-name resolution is ambiguous and non-deterministic; early return skips authorization of the remaining tables |
| SEC-OLAP-12 | Low | Startup validation does not cover `DuckDbOlapOptions`; silent defaults |
| SEC-OLAP-13 | Low | `IsActive` and Virtual Filter coverage of OLAP-staged tables are unverified |
| SEC-OLAP-14 | Info | Single-source OLAP: acceptable residual, with conditions |

**SEC-OLAP-01 (High) - blocklist desync via escape strings.** `ValidateUserSql` decides "single statement", "inside quotes", the keyword set and the first keyword from a quote-toggling scan that treats `'` and `"` as simple toggles and states that "backslash is no escape" (`DuckDbOlapEngine.cs:425-451`, `ExtractTokens` `:584`, `StripSqlComments` `:636`, `NormalizeQuotedIdentifiers` `:512`). DuckDB inherits PostgreSQL string syntax: `E'\''` is one string containing a quote **[verify]**. For the input `SELECT E'\'' ; <statement> ...` the scanner sees an even number of quotes only by accident of position: after the third apostrophe it believes the following text is quoted, so a `;` and the keywords `ATTACH`, `COPY`, `SET`, `LOAD`, `INSTALL`, `PRAGMA`, `CALL`, `CREATE` (the token check skips quoted text, `:584-630`) are invisible to the check, while DuckDB executes them as code. The regex pass for `read_*`, `glob`, `duckdb_*` still runs on the whole text (`:499-505`, `:570`), which limits the damage; keyword-only constructs (second statement, `CREATE MACRO`, `CREATE TABLE AS`, `SET`, `LOAD`, `INSTALL`, `CALL`) are the exposed set. DuckDB.NET runs multi-statement text **[verify]**. Containment today is the sandbox (`enable_external_access=false`, `lock_configuration`, `:113`), not the validator, so the impact is bounded to in-memory DDL and resource abuse unless extension loading is reachable (SEC-OLAP-03). It matters beyond the plan because it applies to every OLAP request and it undermines the claim in section 4.2 that the "existing blocklist stays in place for all requests (defence in depth)". *Required change:* BC-OLAP-01.

**SEC-OLAP-02 (High) - guardrail differential and the function denylist.** The plan treats the gateway analyzer as the guardrail input for DuckDB text. Verified gaps:
1. The analyzer records join columns only from `JoinCriteria` (`SqlQueryAnalyzer.cs:271-274`) and from `=` comparisons with identifiers on both sides (`:276-300`). `NATURAL JOIN` has no criteria, so it joins on every same-named column, masked ones included, with no reference recorded. The SQL path has the same hole today; for OLAP it is reachable.
2. Filter references come from WHERE, HAVING, ORDER BY and aggregate FILTER (`:221-235`, `:397-406`). Star expressions are the issue: DuckDB `COLUMNS(*)`, `COLUMNS('regex')`, `* EXCLUDE`, `* REPLACE`, `#1` positional references, `GROUP BY ALL`, `ORDER BY ALL` select or compare columns without naming them **[verify]**. Trino's grammar accepts `COLUMNS(*)` and `COLUMNS('x')` as an ordinary function call with `*` or a string argument, so the analyzer parses the statement successfully, records `columns` as a function name and records no column. The fail-closed rule "unparsable means reject" therefore does not trigger, and `WHERE COLUMNS(*) = 'x'` passes SEC-FILTER-01.
3. `SqlFunctionPolicy` is a **denylist** in the SQL path (`FederatedDuckDbExecutionService.cs:266-267`, comment "G-9: the denylist only"). Reusing it for DuckDB text allows every DuckDB-only function that is not on the list (`columns`, `query`, `query_table`, `json_*`, `list_*`, `lpad`, ...). SEC-FED-12 explicitly requires an allowlist for the OLAP path.
4. Alias trick (benign): `FROM a SEMI JOIN b ON ...` and `ASOF`/`ANTI` are read by the Trino grammar as an alias plus a normal join, so the ON columns are still captured. `POSITIONAL JOIN` has no ON and fails the Trino parse. No bypass, but the corpus test must pin this.
5. CTE shadowing differs only in the safe direction: the analyzer matches CTE names case-sensitively on folded keys (`:180-185`, `:475`), DuckDB case-insensitively, so the analyzer can only over-report tables.

What a bypass buys is bounded, because the values are already masked (P3.2): equality joins and filters on REDACT constants (Cartesian amplification), on partial masks (the visible part the caller already has) and on HMAC pseudonyms (allowed anyway, SEC-OLAP-10). The finding is High because the plan presents the analyzer as the control that makes cross-source OLAP "governed", and because the same differential would defeat any later table or function check built on it. *Required change:* BC-OLAP-02, BC-OLAP-03.

**SEC-OLAP-03 (Medium) - blocklist instead of allowlist; extension autoload.** The function and keyword lists are finite (`DuckDbOlapEngine.cs:461-496`, `:570-573`). Not blocked and callable: `query('SELECT ...')` and `query_table(...)` (execute or resolve text at run time, invisible to any table or column analysis; DuckDB restricts `query` to SELECT **[verify]**), `json_execute_serialized_sql`, `getvariable`, `current_database`, `version`, `information_schema.*`, and every memory-heavy scalar (`lpad('x', 2000000000)`, `list_*`, `unnest` over a list of a huge `repeat`; `repeat` and `range` are blocked, `lpad` is not). The setup does not set `autoinstall_known_extensions=false` or `autoload_known_extensions=false` (`:113`); whether `enable_external_access=false` fully prevents INSTALL/LOAD and autoload of `httpfs` is **[verify]**. Defence in depth costs one line. *Required change:* BC-OLAP-01, BC-OLAP-09.

**SEC-OLAP-04 (Medium) - classification key.** The plan uses `SourceName ?? Domain` (section 2.3), mirroring the router (`CrossSourceQueryRouter.cs:84`, `:109-132`, case-insensitive set). Gaps:
- The physical reader is chosen by `connectorRegistry.TryGetConnectorForTable(meta.Identifier)` (`OlapStagingService.cs:155`), which keys on **Domain** and the connector id, with a default-sql fallback (`InMemoryConnectorRegistry.cs:49-69`), not on `SourceName`. Two tables with different domains and the same `SourceName` label would be classified as one source while being read through two connectors. `SourceName` selects the connection inside a connector (`CatalogGovernanceRatchet.cs:117-119`, `SqlConnector.cs:188`), so the classification must include the connector identity.
- Case: the classifier must use `StringComparer.OrdinalIgnoreCase` like the router; the plan does not state it (section 2.3 only says "distinct").
- `HttpPlugin`, `LakehouseIceberg`, `LakehouseDelta` tables are rejected as unsupported by the SQL router (`CrossSourceQueryRouter.cs:112`), but OLAP admits any non-default connector (`OlapStagingService.cs:155-159`). The plan classifies them cross-source (gate) and then reads them with the gate open. Parity requires the same rejection.
- Dedupe is on the raw request string (`:93-96`). `orders` and `dom.orders` resolve to the same table and are staged twice; the second `CREATE TABLE` fails (`DuckDbOlapEngine.cs:253`). Different tables whose sanitised names collide (`a-b` and `a_b`, `SanitizeIdentifier` `:327`; table `x_y` versus view `x_y` from domain `x`, `:263-267`) fail the same way at best, and at worst give the plan's "map reference to staged table by name" step (section 4.2) an ambiguous target and apply the wrong table's guardrail.
- The same-table-twice and alias evasion are harmless for the gate (the set of resolved tables decides), but harmful for budgets (double count) and mapping.
- The staged set, not the SQL text, determines what data DuckDB can touch, so a caller cannot reach a source by naming it only in the SQL. The classification is on the resolved `tableNames`, which is correct and should stay the only input.

*Required change:* BC-OLAP-04, BC-OLAP-05, BC-OLAP-06.

**SEC-OLAP-05 (Medium) - denied column names are staged and returned.** `StageTableAsync` builds the DuckDB table from all catalog columns (`DuckDbOlapEngine.cs:230-252`) while `ProjectRow` omits denied columns (`GovernedConnectorReader.cs:227-229`) and the engine inserts NULL for them (`DuckDbOlapEngine.cs:301`). `SELECT *` therefore returns the names of denied columns as all-NULL columns, and a binder error lists "candidate bindings". The SQL path answers a denied column like a non-existent one (SEC-X1-05, `GovernedSqlExecutionService.cs:344-347`). Plan section 1.4 says denied columns "give no signal"; the values give none, the names do. *Required change:* BC-OLAP-08.

**SEC-OLAP-06 (Medium) - resource bounds.**
- Only row caps exist for staging (`OlapStagingService.cs:188-192` sets `MaxRows` only; `GovernedRowPolicy.MaxBytes` exists, `GovernedConnectorReader.cs:196-203`, but is unset). 250,000 rows of wide text fill gateway memory. The byte check runs after the whole raw read is in memory (`:120-122`, `:137-160`), so it is a post-hoc cap; the row cap is enforced per batch (`:153-156`). The plan adds byte caps for cross-source only.
- The concurrency semaphore is global and engine-side (`DuckDbOlapEngine.cs:90`, default 4): one tenant can occupy all slots for the 60 s timeout repeatedly and starve the others. It is taken after the source reads, so unlimited concurrent requests can already hold up to 500,000 staged rows each in gateway memory.
- The timeout (`:83-86`) starts inside the engine; source reads and authorization are not covered.
- A single-source `CROSS JOIN` of two 250k-row tables, `lpad` and similar are bounded only by `max_memory`, `max_temp_directory_size`, threads=2 and the 60 s interrupt (`:113`). That is adequate per query and inadequate under fan-in (point 2).
- The result is silently truncated at `MaxResultRows`/`MaxResultBytes` with `totalRows = rows.Count` (`:205`), so the caller cannot tell.

*Required change:* BC-OLAP-09.

**SEC-OLAP-07 (Medium) - error text.** The guardrail messages name column, table and rule (`GovernedSqlExecutionService.cs:323`, `:338`), and a Deny-level column raises `WebSqlTableDeniedException` with that text (`:347`), which defeats the "denied looks nonexistent" intent if the mapper passes the message. The OLAP endpoint returns `outcome.Error`/`Details` as is (`DuckDbOlapEndpoints.cs:121-129`) and, in Development and Quickstart, the raw engine message (`OlapStagingService.cs:262-266`, `DuckDbOlapEndpoints.cs:110-112`). Plan note 9 asks the question; the answer is BC-OLAP-10.

**SEC-OLAP-08 (Medium) - audit.** Audit records exist for staged tables and successful queries, always `Decision = "ALLOW"` (`OlapStagingService.cs:62-83`, `:208-218`, `:230-242`). Not on record: gate denial, guardrail denial, unanalysable SQL, failed execution after the tables were already audited as staged, and the SQL itself (only a SHA-256, `:60`). SEC-FED-10 requires audited denials after authorization and a shared correlation id. *Required change:* BC-OLAP-11.

**SEC-OLAP-09 (Low-Medium) - unhandled Phase C exceptions.** The governed read sits inside a `try` that catches only `ConnectorRowLimitExceededException` (`OlapStagingService.cs:182-199`); a `GatewayForbiddenException` from `EnsureInMemoryFilterIsEnforceable` (`InMemoryRowFilterEvaluator.cs:22-29`), a `GatewaySecurityException` from the byte limit, a connector fault or a staging-name collision propagates out of `ExecuteAsync` because the outer `try` begins at `:225`. The endpoint has no handler around the call (`DuckDbOlapEndpoints.cs:82`). Fail-closed holds (no data), but status and body are whatever the global handler produces. *Required change:* BC-OLAP-12.

**SEC-OLAP-10 (Low) - HMAC exemption.** Parity with WebSQL is acceptable (details in P3.5, Q4 of section 11). The rule-type sets differ: `MaskingRule.IsHmac` accepts `HMAC`, `HMAC_SHA256`, `HASH` (`MaskingRule.cs:55`), the guardrail exempts only `HMAC` (`GovernedSqlExecutionService.cs:302`), so the other two are rejected in joins. That is fail-closed and inconsistent; do not widen it in this change, document it, and test both outcomes.

**SEC-OLAP-11 (Medium) - bare names and early return.** A bare name is resolved with `GetAllTablesAsync(...).FirstOrDefault` by table name or qualified name (`OlapStagingService.cs:116-119`): with the same table name in two domains the result depends on catalog order, so the caller (and the classifier) may get another table than intended, and the list is not tenant-scoped in this code. The authorization loop returns on the first denial (`:122-153`), which the plan keeps ("Any denial returns the existing generic 403/404 immediately", section 4.1 Phase A). SEC-FED-03 (parent plan line 463) requires no early return that skips authorization of the remaining tables. *Required change:* BC-OLAP-05, BC-OLAP-07.

**SEC-OLAP-12 (Low) - options.** `DuckDbOlapOptions` is not validated: `SanitizeMemoryString` silently replaces an unparsable value with `1GB` (`DuckDbOlapEngine.cs:335-340`), counts of 0 or less fall back to defaults in code (`OlapStagingService.cs:92`, `:105`), and `MaxConcurrentQueries <= 0` is untested. Plan section 4.4 validates only `CrossSourceOptions`. *Required change:* BC-OLAP-14.

**SEC-OLAP-13 (Low) - unverified items.** (a) The router rejects inactive tables (`CrossSourceQueryRouter.cs:106`, `!meta.Table.IsActive`); `OlapStagingService.cs:122` only checks `meta == null`. Whether `ResolveAccessAsync` denies inactive tables is not verified. (b) The OLAP read passes no `VirtualFilterEnforcement` (`:188-193`). For tables that attest pushdown the predicate rides in `CombinedRowFilterSql`; for in-memory filtering `MandatoryRowPredicateSql` makes the read fail closed (`InMemoryRowFilterEvaluator.cs:25-29`). Whether a pushdown connector can report `RlsPushdownExecuted` without applying the virtual-filter predicate is not verified here. Both need a Phase 4 test (T3-26, T3-27).

**SEC-OLAP-14 (Info) - single-source.** Acceptable as a time-boxed residual because the confidentiality boundary is mask-before-stage (P3.2) and the sandbox contains the engine. It is acceptable only with the all-request hardening in BC-OLAP-01, 08, 09, 10, 12, 15 and the autoload switches. Without them the single-source path still has SEC-OLAP-01, 05 and 06.

### P3.4 Binding conditions for Phase 4

Each condition is a gate for the named work package; the lead checks them in the review of Phase 5.

- **BC-OLAP-01 (WP-SO-2, all requests).** Fix SEC-OLAP-01 before adding the gate to the same method. Floor: reject any statement text containing a backslash, an `E`/`e` string prefix before a quote, a `U&` prefix, or any quote character class not handled by the scanner, and keep `$` rejected; count statements with a scanner that is a superset of DuckDB's lexing. Preferred (and required for cross-source): validate with DuckDB's own parser, e.g. `json_serialize_sql` on the sandbox connection before staging (single `SELECT` only, any parse failure rejects), and take statement count, table references and function names from that AST instead of text. Keep the token blocklist as a second layer. Proof: T3-01 to T3-04.
- **BC-OLAP-02 (WP-SO-3, cross-source).** The guardrail input must not be the only control. For cross-source requests require all of: `StatementType == Select`; `TableFunctionCalls` empty; `HasSessionProperties` and `HasInlineFunctionDefinitions` false; every entry of `FunctionCalls` on an explicit **OLAP function allowlist** held in code and unit-tested (aggregates, `coalesce`, `cast`, date/string basics; new names are added by code change, not configuration); reject `columns`, `query`, `query_table` and every unknown name. The deny-only `SqlFunctionPolicy` call of the SQL path is not sufficient and must not be copied. Prefer DuckDB-AST function extraction (BC-OLAP-01) so the allowlist applies to what DuckDB runs.
- **BC-OLAP-03 (WP-SO-3).** Reject in cross-source SQL: `NATURAL` joins (add a minimal additive analyzer flag, or reject the token outside quotes as the floor), `LATERAL`, `TABLESAMPLE`, star expressions with `COLUMNS`, `EXCLUDE`/`REPLACE`, positional `#n`, `GROUP BY ALL`, `ORDER BY ALL`, `QUALIFY`, set operations over tables of different sources are allowed only if every branch passes the same checks. Anything the analyzer cannot parse is rejected. The plan, release note and feature doc state that the column guardrail is a **policy-consistency control, not a confidentiality boundary**; the boundary is masking before staging, and a regression test pins that (T3-13).
- **BC-OLAP-04 (WP-SO-2).** Classify by the resolved identity `(connector.ConnectorId, SourceName ?? Domain)` with `OrdinalIgnoreCase`: more than one distinct connector id or more than one distinct source label means cross-source; any non-`Sql` type means cross-source. `CrossSourceClassifier.IsCrossSource` takes the resolved triples, not only `TableMetadata`.
- **BC-OLAP-05 (WP-SO-2, all requests).** Dedupe on the resolved `TableIdentifier` (case-insensitive) before budgets and staging. Compute all staged names (table and `domain_table` view) up front; any collision after `SanitizeIdentifier`, case-insensitive, rejects with the generic denial before any read. A bare name matching more than one table in the catalog is rejected with the same generic text as not found (never first-match). Resolution and the allowlist step must not reveal whether the second candidate exists.
- **BC-OLAP-06 (WP-SO-2).** Cross-source requests reject `HttpPlugin`, `LakehouseIceberg`, `LakehouseDelta` with the router's text, after authorization; verify and enforce `IsActive` (T3-26).
- **BC-OLAP-07 (WP-SO-2).** Phase A evaluates every requested table (metadata, allowlist, access, connector) before returning a denial, then returns the denial of the first failing table in request order with today's text; no connector call, `GetSplitsAsync` or `ReadAsync` before Phase A and Phase B are complete. T3-05 proves it with recording fakes.
- **BC-OLAP-08 (WP-SO-2, all requests).** Stage only columns whose `decision.GetColumnAccess` is not `Deny`; a denied name must behave as an unknown column. Pass `MaskedColumns` for the rest (plan 4.3).
- **BC-OLAP-09 (WP-SO-2, all requests).** (a) Add `DuckDbOlap:MaxStagedBytesPerTable` and `MaxTotalStagedBytes` (defaults sized so the existing row defaults cannot exceed gateway memory, e.g. 64 MB and 256 MB), applied to **every** request via `GovernedRowPolicy.MaxBytes` plus a running total; cross-source uses the smaller of the `CrossSource` and `DuckDbOlap` values. (b) Take an OLAP lease (global and per-tenant cap, per-tenant default 1 to 2) **before** the first connector read, and start the request timeout before Phase A so it covers authorization and reads. (c) Add `SET autoinstall_known_extensions=false; SET autoload_known_extensions=false;` (and `allow_community_extensions=false` if the pinned DuckDB version has it) before `lock_configuration`, and keep `enable_external_access=false`. (d) Return a truncation indicator or a 400 when the result row or byte cap is hit instead of a silent cut, as a response-field decision for the owner (additive field only).
- **BC-OLAP-10 (WP-SO-2/3).** Outside Development and Quickstart, gate denial, guardrail denial, unanalysable SQL, unsupported type and name collision return fixed generic bodies with stable codes; table, column and rule names never appear. A Deny-level column returns the same body as an unknown column. The Quickstart `details = ex.Message` path stays Development-grade only and is called out in the docs.
- **BC-OLAP-11 (WP-SO-2).** Write one **required** audit record for every denial after authorization (`OLAP_DENIED`, `Decision = "DENY"`, reason code, `crossSource`, table count, `sqlSha256`, `traceId`). If that write fails, the request is still denied (a failed audit write must never turn a denial into an allow); log and count it. Add `crossSource`, `transport = "Olap"`, the distinct source labels and connector ids, and staged bytes to `OLAP_QUERY`; add an `OLAP_QUERY_FAILED` record when execution fails after tables were audited as staged; all records share `traceId` as correlation id. Keep SQL text out of the audit; keep the hash and add the SQL length.
- **BC-OLAP-12 (WP-SO-2).** Wrap Phase C so `GatewayForbiddenException`, `GatewaySecurityException`, connector faults and staging errors map to 403, 400 or 502 with generic bodies; no exception escapes `ExecuteAsync`.
- **BC-OLAP-13 (WP-SO-1).** Gate contract as in plan section 3, plus: blank transport denied, empty list denies all, `Olap` is never in the default list in this change (see Q1). `FederationGate` is a singleton reading `IOptions<GatewayOptions>` (not a snapshot taken at construction).
- **BC-OLAP-14 (WP-SO-1).** Extend the validator to `DuckDbOlapOptions`: counts and caps greater than 0, `MaxConcurrentQueries >= 1`, `MaxThreads` in 1..8 or rejected (the engine clamps silently, `DuckDbOlapEngine.cs:110`), `MaxMemory`/`MaxTempDirectorySize` must parse with the engine's own pattern, otherwise startup fails instead of using `1GB`.
- **BC-OLAP-15 (WP-SO-3, all requests).** Bound the request: maximum SQL length (default 32 KB) and `SqlTokenSecurityOptions` for the analyzer, applied before any table resolution; reject beyond it with a generic 400.
- **BC-OLAP-16 (WP-SO-2).** The cross-source table limit counts resolved distinct tables after BC-OLAP-05 and is checked before any read (plan 4.1 Phase B is correct; the count input changes).
- **BC-OLAP-17 (WP-SO-3).** Keep HMAC parity with WebSQL as the plan says; do not widen the exemption to `HASH`/`HMAC_SHA256`; the test matrix pins current outcomes for all five mask types and for ABAC mask without a rule.
- **BC-OLAP-18 (WP-SO-1).** Log a startup warning when the legacy keys `WebSql:CrossSource:MaxMemory` or `MaxTempDirectorySize` are present in configuration (the binder ignores them silently), and a warning when `AllowedTransports` contains `Olap` while `CrossSource.Enabled` is false.

### P3.5 Mandatory security tests (red first, in addition to the plan's section 7)

Engine and validator (all requests)
- T3-01 `E'\''`-prefixed escape string followed by `;` and a second statement is rejected (statement count), and a second statement hidden after it is never executed.
- T3-02 `E'...'` followed by `ATTACH`, `COPY`, `INSTALL`, `LOAD`, `SET`, `PRAGMA`, `CALL`, `CREATE MACRO` is rejected for each keyword.
- T3-03 Nested block comment `/* /* */ DROP ... */` and unterminated comment forms are rejected or neutral; line comment before `;`.
- T3-04 The 23 existing blocklist tests plus `query('...')`, `query_table`, `json_execute_serialized_sql`, `getvariable`, `lpad('x',2000000000)`, quoted-identifier function names, mixed case, unicode lookalikes, `information_schema` access: each is rejected or bounded (documented outcome per case).
- T3-05 Ordering: fake connectors record every call; a request whose second table is denied, ambiguous, colliding, inactive or unsupported produces zero connector calls, and the response is the same with the gate on or off.
- T3-06 Autoload: after setup, `SELECT * FROM <extension-backed function>` (for example a `json` or `httpfs`-only function not present in the build) fails without any network or file access; the two autoload settings are `false` in `duckdb_settings()` equivalent (read in a test connection).
- T3-07 Staged column list: a denied column is absent from `SELECT *`, produces the unknown-column error text for `SELECT secret`, and the binder error does not list it.
- T3-08 Byte caps: one wide table above `MaxStagedBytesPerTable` and two tables above the total are rejected for single-source and cross-source.
- T3-09 Concurrency: a tenant at its lease cap is refused (429) while a second tenant is served; leases are released on exception, cancellation, timeout and audit failure; the source read happens only while holding the lease.
- T3-10 Timeout covers a slow fake connector (read longer than the timeout is cancelled).
- T3-11 Staging-name collisions (`a-b`/`a_b`, table `x_y` vs view `x_y`, same table via `orders` and `dom.orders`) produce the generic rejection before any read; ambiguous bare name produces the same body as not found.
- T3-12 Phase C exceptions (forbidden from filter evaluator, byte-limit exception, connector fault) return mapped generic statuses, no exception escapes.

Guardrails and classification (cross-source)
- T3-13 Invariant: staged values of REDACT, PARTIAL, HASH, HMAC, ABAC-masked and denied columns equal the governed reader output (no plaintext in the DuckDB session) under `IsColumnMaskingDisabled=false`.
- T3-14 Differential corpus, each rejected with the generic body and zero reads: `WHERE COLUMNS(*) = 'x'`, `COLUMNS('^ss')`, `* EXCLUDE (a)`, `* REPLACE`, `#1`, `GROUP BY ALL`, `ORDER BY ALL`, `NATURAL JOIN`, `LATERAL`, `TABLESAMPLE`, `QUALIFY`, nested comments, `E''` strings, `a SEMI JOIN b ON ...` (accepted only if every ON column passes), `a ASOF JOIN b` (same), `POSITIONAL JOIN` (rejected by parse).
- T3-15 Function allowlist: each allowlisted function passes; `columns`, `query`, `list_*` not on the list, `lpad` and an unknown name are rejected; the list is a code constant and a test asserts it contains no entry from the blocklists.
- T3-16 Reference mapping: alias, schema-qualified, `domain_table` view name, quoted mixed-case name, CTE with the same name as a staged table; each maps to exactly one staged table or is rejected.
- T3-17 Classification: same `SourceName` with different case is one source; same `SourceName`, different connector id is cross-source; `SourceName == null` with different domains is cross-source; one `HttpDeclarative` table alone is cross-source; `HttpPlugin`/Lakehouse are rejected after authorization; same table twice is one table.
- T3-18 Gate denial body and status are byte-identical for disabled and transport-not-allowed, contain no table name, and are identical whether the second table exists or not for a caller authorized for both.
- T3-19 Timing class: the number of metadata, allowlist and access calls is the same for a request denied on its first table and one denied on its last.
- T3-20 Cartesian: `CROSS JOIN` and a non-equi join in `ON` (and a comma join with no predicate) are rejected unless `AllowNonEquiJoins`.
- T3-21 Guardrail messages and codes outside Development contain no column, table or rule name; a Deny-level column answers like an unknown column.

Audit and configuration
- T3-22 `OLAP_DENIED` exists for gate denial, guardrail denial, unanalysable SQL; audit failure on a denial still returns the denial.
- T3-23 `OLAP_QUERY` carries `crossSource`, `transport`, sources, bytes, `traceId`; failed execution after staging writes `OLAP_QUERY_FAILED`; no SQL text or row value in any record.
- T3-24 Validator: `DuckDbOlapOptions` zero, negative, unparsable memory string, `MaxConcurrentQueries` 0 abort startup; legacy `MaxMemory` key and `Olap` without `Enabled` log the warnings.
- T3-25 `FederationGate` reads live options (change after construction takes effect) and is thread-safe.

Residual checks from SEC-OLAP-13
- T3-26 An inactive table requested through OLAP is denied.
- T3-27 A table covered by a Virtual Filter is either filtered or denied through OLAP, for a pushdown-capable connector and for an in-memory one; no row outside the predicate appears.

Integration (one more than the plan's one)
- T3-28 Two SQLite sources, default config: HTTP 403 and zero rows; with `Enabled=true` and `Olap` listed: 200 for an equi-join, 403/400 for `NATURAL JOIN` and `COLUMNS(*)`.
- T3-29 Mixed-case `tableNames` and duplicate spellings do not change the gate outcome.
- T3-30 Fan-in: ten concurrent cross-source requests from one tenant keep gateway memory under the configured byte budget times the lease cap and do not starve a second tenant.

### P3.6 Recommendations for the owner questions (section 11)

1. **Default of `Olap` in `AllowedTransports`: keep it out.** Fail closed, as the plan chose. Do not let PLAN-FED-01's default expansion add `Olap` until the facade (WP-FED-05) lands and BC-OLAP-01 to 03 are green; adding it earlier makes "enable federation" silently enable the weakest cross-source path. The upgrade note must say explicitly that setting the list replaces the default.
2. **Reject unanalysable cross-source SQL: yes, and tighten it.** Rejecting is the right fail-closed behaviour, but it is not enough alone because the Trino grammar accepts DuckDB constructs (SEC-OLAP-02). The combination "gateway parse succeeds, function allowlist passes, construct blocklist passes" is the minimum; DuckDB-AST validation (BC-OLAP-01 preferred) is the strong form. If BC-OLAP-02 and 03 cannot be delivered together with the gate, the fallback is to ship WP-SO-1 and WP-SO-2 and **deny all cross-source OLAP** (return 403 even when the gate is open, behind a documented internal switch default off) until WP-SO-3 is complete; do not ship an open gate with the guardrail missing, because operators will read it as governed. WP-SO-3 is therefore a merge prerequisite for allowing `Olap`.
3. **Single-source on the DuckDB dialect until the facade: confirm**, with BC-OLAP-01, 08, 09, 10, 12, 15 and the autoload settings applying to all requests (SEC-OLAP-14). The residual is the unbounded function surface inside the sandbox, mitigated by the sandbox settings, by the mask-before-stage invariant and by the all-request bounds.
4. **Remove `MaxMemory` and `MaxTempDirectorySize` now: confirm.** They never had an effect, so removal changes no enforcement. Add the startup warning for the legacy keys (BC-OLAP-18) so an operator who believed they were bounded learns that `DuckDbOlap:*` is what counts.

Plan security notes (section 11, items 1 to 9) answered:
- Note 1 (ordering): partly. The plan's flow is right but returns early on the first denial and the bare-name ambiguity is open (BC-OLAP-05, 07).
- Note 2 (existence oracle): the gate text is safe if it follows full authorization; additionally generic bodies (BC-OLAP-10), no ambiguity reject before authorization, and equal call counts (T3-19).
- Note 3 (differential): see SEC-OLAP-02; the refusal list is BC-OLAP-03.
- Note 4 (HMAC): accept parity with WebSQL for this change. Cross-source pseudonym correlation inside one tenant is the stated purpose of a tenant-scoped HMAC and the operator opted in twice (`Enabled` and `Olap`). The stricter SEC-FED-02 rule (keys must be Clear) stays for GraphQL relations. Record `crossSource` and the HMAC-join fact in the audit so the choice is reviewable; revisit when the facade lands.
- Note 5 (Virtual Filter): not verified here; T3-27 is mandatory (SEC-OLAP-13).
- Note 6 (audit): yes, denial audit is required (BC-OLAP-11); it is about 40 lines rather than the plan's 20 because of the failed-execution record.
- Note 7 (`danger_disable_column_masking`): guardrails and gate must not depend on it; T3-13 runs both settings. With masking disabled the confidentiality boundary is gone by design (Development only, `GatewaySecurityOptionsValidator.cs:103`).
- Note 8 (option removal): confirmed, no enforcement is removed; the new validator can only tighten (BC-OLAP-14 is the one place it changes silent defaults into startup failures, to be listed in the release note).
- Note 9 (response text): generic outside Development (BC-OLAP-10).

### P3.7 SEC-FED-12 "accepted interim gap"

SEC-FED-12 requires that the OLAP path use the governed compiler and a function allowlist and that client DuckDB SQL is not executed. After this plan the third part remains unmet for single-source and for cross-source text that passes the checks.

**Assessment: acceptable as an interim residual, under four conditions.** (1) The new cross-source controls are real controls: the gate and BC-OLAP-02/03 (allowlist) are in place, not only the analyzer call. (2) The all-request hardening (BC-OLAP-01, 08, 09, 10, 12, 15 and the autoload settings) is delivered, because those close holes that exist today independently of the facade. (3) The gap is recorded as open security debt in `doc/plan/00-master-plan-overview.md` with WP-FED-05 as its closing item and a target release; Phase 6 must not describe OLAP as "SEC-FED-12 compliant" or "governed" in `docs/features/f-data-03-duckdb-olap.md`, only as "gated and bounded". (4) `Olap` stays out of the default `AllowedTransports` until the facade lands (Q1). With these, the interim is better than the status quo on every axis and does not weaken any other transport; without condition (1) or (2) it is a change of label, not of exposure.

### P3.8 Verdict

**APPROVED WITH CONDITIONS.** Phase 4 starts with WP-SO-1 as planned. WP-SO-2 must include BC-OLAP-01, 04 to 09, 11 and 12 (BC-OLAP-01 first, because it changes the method the gate is added to). WP-SO-3 must include BC-OLAP-02, 03, 10, 15, 17 and the differential corpus T3-14; it is a merge prerequisite for allowing `Olap` in `AllowedTransports`. All tests T3-01 to T3-30 are mandatory and are written red first. The Phase 5 reviewer checks each BC against the diff. Items marked **[verify]** are assumptions about DuckDB or Trino grammar behaviour and must be turned into evidence by the first red tests; if a test shows the assumption false, the corresponding condition is downgraded in the Phase 4 log with the test as evidence, not silently dropped.
