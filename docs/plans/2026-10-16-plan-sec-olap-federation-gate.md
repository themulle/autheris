# Implementation Plan: PLAN-SEC-OLAP-01 - Close the Ungated Cross-Source Path of `POST /api/v1/olap/query`

**Document ID:** `PLAN-SEC-OLAP-01` (revision 1, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [REQ-FED-01](2026-10-16-req-cross-source-all-consumers.md) (findings 4 and 5, matrix row 17, D-1, D-7, D-11, FR-2, FR-13, AC-4, R-5, Q-2) and [PLAN-FED-01](2026-10-16-plan-cross-source-all-consumers.md) (sections 3, 4.1 WP-FED-01, 4.5 WP-FED-05, security conditions SEC-FED-02, 08, 12, 17)
**Parent Plans:** [00-master-plan-overview.md](00-master-plan-overview.md), [PLAN-FED-01](2026-10-16-plan-cross-source-all-consumers.md)
**Branch:** `fix/sec-olap-federation-gate` (separately mergeable, independent of `feat/cross-source-all-consumers`)
**Status:** PROPOSED (Phase 2 draft; Phase 3 review pending, see section 11 for the focus list)

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
