# Architecture & Implementation Plan: Federated Virtual Filters on Web-APIs (F-GOV-14)

**Document ID:** `PLAN-FEDERATED-VF-WEBAPI-15-ARCH` (child of `PLAN-FEDERATED-VF-DUCKDB-15`)
**Phase:** 2 - Solution Architecture (`csharp-architect`, 6-phase lifecycle, see `AGENTS.md` section 6)
**Date:** 2026-10-10
**Status:** PROPOSED - input for Phase 3 (security audit) and Phase 4 (TDD implementation)
**Input PRD:** [2026-10-10-req-federated-virtual-filters-web-api.md](./2026-10-10-req-federated-virtual-filters-web-api.md) (`REQ-FEDERATED-VF-WEBAPI-15`)
**Origin concept (German):** [2026-10-10-konzept-federated-virtual-filter-web-api-duckdb.md](./2026-10-10-konzept-federated-virtual-filter-web-api-duckdb.md)
**Origin plan:** [00-master-plan-overview.md](./00-master-plan-overview.md) - `PLAN-FEDERATED-VF-DUCKDB-15`
**Code under review:** commit `e7ee288` (line numbers below refer to the tree at `b089034`)

> **Summary.** The fix is a *rebuild on a fail-closed baseline*, not a patch of `e7ee288`. Work item WI-1 removes the
> unconditional "federation handled" flag, which immediately returns every Web-API object with an applied Virtual
> Filter to the pre-F-GOV-14 behaviour (HTTP 403). The following work items re-introduce the feature with one
> enforcement point: the gateway filters upstream rows **at staging time** with a hash semi-join against the
> authorized key set, regardless of tier, format or query shape. Short-circuit and pushdown only decide **what is
> fetched**, never **what is returned**. The `e7ee288` INNER-JOIN rewrite, the generic staging hooks
> (`SessionItems`, `CustomArguments`, `PreloadedRows`), the profile re-resolution in the execution path and the
> in-memory filter re-implementation are deleted.

---

## 1. Gap analysis

Severity: **C** critical (data exposure), **H** high, **M** medium, **L** low. Line numbers are in the current tree.

| PRD req. | Current implementation (evidence) | Gap | Sev. | WI |
|---|---|---|---|---|
| AC-7.4 | `CrossSourcePlanner.cs:177-182` sets `VirtualFilterFederationHandledKey` for **every** `HttpDeclarative` table and every table with applied VFs; `GovernedConnectorReader.cs:70` treats it as "row filter pushed down" and skips `CombinedRowFilterSql` | Disables all row filters (RLS, consent, tenant, VF) on HTTP tables in WebSQL cross-source queries | C | WI-1 |
| AC-7.1, AC-8.4, D-1, D-7 | `FederatedDuckDbExecutionService.cs:596-627`: pushdown chosen, no join added; args set as `customArgs[batchParam]` / `customArgs["ids"]`; `DeclarativeHttpDataSourceExecutor.cs:67-71,516` drops list args unless `BatchType != None`; empty key set satisfies `Count <= MaxPushdownKeys` | Upstream trusted to filter; dropped args or empty key set = unfiltered full fetch with no post-filter | C | WI-1, WI-4, WI-6, WI-8 |
| AC-7.2, AC-7.3 | `CrossSourcePlanner.cs:250-264` (top-level `QuerySpecification.From` only), `:236` fallback `"s0"`, `:269` `JoinType.Inner`, `:217` key columns typed `varchar` | UNION/CTE/sub-query/self-join bypass; row multiplication; key columns leak into `SELECT *`; type confusion | C | WI-4, WI-6 |
| AC-3.1, AC-3.2, AC-3.3 | `FederatedDuckDbExecutionService.cs:580-590`: fallback picks first active tenant profile with a matching binding, **no grantee matching**, one binding only; `VirtualFilterShortCircuitEvaluator.cs:115-179` returns on first matching binding | Wrong grantee's filter can be applied; other applying filters dropped | C | WI-3, WI-6 |
| AC-3.4 | `FederatedDuckDbExecutionService.cs` ctor: `shortCircuitEvaluator`, `keyProvider`, `snapshotProvider` all `= null`; enforcement skipped when null | Misconfigured DI = no filter | C | WI-6 |
| AC-2.3, AC-2.4, NFR-6 | `VirtualFilterModels.cs:377-386` `MatchesTarget` returns `false` on `RegexMatchTimeoutException`/`ArgumentException`; backtracking engine with `IgnoreCase` (`:323`, `:377`); not anchored at the end; matched against two name forms | Timeout/invalid regex = binding silently not applied (fail-open in `MandatoryRowFilterResolver.cs:361-369`) | C | WI-2 |
| AC-4.1, AC-4.2 | `DefaultVirtualFilterKeyProvider.cs:149` reads only `Structured.From`; ignores `Joins`, validity window; `:211-238` unknown operators fall through as match; case-insensitive comparison | Over-permissive key sets | H | WI-5 |
| AC-4.4 | `DefaultVirtualFilterKeyProvider.cs:173-181` hard 50,000-row read via `GovernedConnectorReader` | Throws generic row limit, not `VF_KEY_SET_TOO_LARGE`; reads the whole `From` table | H | WI-5 |
| AC-10.4 | `DefaultVirtualFilterKeyProvider.cs:49` public `SetInMemoryTuples`, keyed by filter name only | Runtime override of authorization data, cross-tenant bleed | H | WI-5 |
| AC-10.1, AC-10.2 | `DefaultVirtualFilterKeyProvider.cs:97` `"anonymous"` fallback, key `vf_tuples:{tenant}:{filter}:{sid}` without definition hash / key columns; fixed 30 s TTL | Stale keys after filter change; ambiguous identity | M | WI-5 |
| AC-4.5, AC-4.6, AC-4.7, AC-7.5, D-13 | `DefaultVirtualFilterKeyProvider.cs:70` `HashSet<string>(OrdinalIgnoreCase)`; `VirtualFilterShortCircuitEvaluator.cs:154` `OrdinalIgnoreCase` string compare | No catalog typing, case-insensitive matching, NULL handling implicit | M | WI-4, WI-5 |
| AC-6.2, AC-6.3 | `SqlPointLookupExtractor.cs:53,58` uses `Parts[^1]` (unqualified); only top-level `QuerySpecification`; no `IN` support | Predicate on another table's same-named column triggers a wrong short-circuit; Tier 1 hit pushes whole set | M | WI-7 |
| FR-6 (`ShortCircuitOnly`), D-3 | `VirtualFilterShortCircuitEvaluator.cs:177-179` scan = silent 0 rows; hit falls through to full fetch | Misleading result; hidden policy | M | WI-6, WI-7 |
| FR-6 (`PushdownOnly`), D-4 | `FederatedDuckDbExecutionService.cs:631` silent fallback to Tier 3 | Violates operator egress constraint | M | WI-8 |
| FR-8 AC-8.1 | `PushdownUrlFormatter.cs` used only by tests (`FormatQueryString`); `:36` OData literal typed by `long.TryParse`; `:52` hand-built JSON; `PostBatch` sent as query arg (`FederatedDuckDbExecutionService.cs:611`) | Formatter not on the production path; injection-prone | M | WI-8 |
| AC-8.2, AC-8.3, D-11 | No URL budget, chunking or 414 handling | HTTP 414 possible; no deterministic overflow behaviour | M | WI-8 (minimal), deferred (chunking, 414 retry) |
| AC-8.5 | `FederatedStagingService.cs:134-141` merges custom args over gateway args without conflict check | Pushdown args overridable | M | WI-6, WI-8 |
| AC-11.1 | `FederatedStagingService.cs:113-125` `PreloadedRows` skip per-table/total budget checks | Unbounded key relation | M | WI-6 (relation removed) |
| AC-1.1 | `VirtualFilterModels.cs:342` only `> 0` check | No upper bound | L | WI-9 |
| AC-1.3, D-9 | `VirtualFilterModels.cs:309,354,398` `CompositeKeys` persisted, only read in an `isSingleKey` check (`FederatedDuckDbExecutionService.cs:595`) | Redundant, unvalidated | L | WI-9 |
| D-10, AC-5.x | Pushdown format on the binding (`VirtualFilterModels.cs` `PushdownFormat`), endpoint capability not consulted | API capability not modelled where it belongs | M | WI-8 |
| FR-9 | Short-circuit path writes only the start audit (`FederatedDuckDbExecutionService.cs:301-305`); key source read not audited; tier not recorded; `GatewaySecurityException` audited as `QUERY_EXECUTION_ERROR` (`FederatedDuckDbExecutionService.cs:476`, `isDenied` checks only `WebSqlPolicyException or SecurityException`) | Incomplete audit trail; denials not classified | H | WI-10 |
| (new finding) | `FederatedDuckDbExecutionService.cs:383-386`: with `danger_bypass_consent_checks` and no consent services, the decision is `Allowed(...)` **without** calling `IMandatoryRowFilterResolver` | Virtual Filters not applied in WebSQL cross-source under consent bypass (contradicts design decision 1 used in `GovernedProcedureExecutionService.WithVirtualFiltersAsync`) | M | WI-11 |
| NFR-1, section 5.1 | No integration tests; unit tests use mocked evaluator/provider | No evidence | H | WI-12..WI-15 |
| AC-1.2, AC-1.4, AC-5.2, NFR-5 (write time) | `VirtualFilterAdministrationService.cs` has 959 lines (already > 800) | Cannot grow without extraction | M | Deferred (query-time fail-closed in WI-6/WI-8) |
| NFR-7 streaming, NFR-9 metrics | `GovernedConnectorReader.cs:89-100` materializes lists | Not a correctness issue; bounded by budgets | L | Deferred |

---

## 2. Target design

### 2.1 Guiding principles (binding for Phase 4)

1. **One enforcement point.** The gateway restricts the upstream rows of every protected HTTP table to the
   authorized key set *before* they are staged into DuckDB. Pushdown and short-circuit are optimizations that only
   change what is requested upstream.
2. **Semi-join semantics by construction.** Enforcement is a hash-set probe per upstream row
   (`FrozenSet<VirtualFilterKey>`). No key relation is staged into DuckDB, so there is no row multiplication, no key
   column in `SELECT *`, and no query-shape special case. The user query AST is not modified for Virtual Filters
   (also satisfies the constraint not to touch `src/TrinoSqlEngine/Ast`).
3. **Single policy decision point.** The (filter, binding) pairs to enforce come only from
   `MandatoryRowFilterResolver` via `TableAccessDecision`. The execution path never re-resolves profiles.
4. **Precise suppression.** The in-memory row filter of `GovernedConnectorReader` may skip **only** the Virtual
   Filter predicate, and only when the enforcement object covers **every** applied (filter, binding) pair of that
   table. All other row filters (consent, RLS, ABAC, tenant) remain in force. Any inconsistency denies.
5. **Fail closed with stable codes.** Every unsupported shape, missing component, source error, limit overflow,
   regex failure or ambiguous configuration throws `GatewaySecurityException` with a code from
   `VirtualFilterErrorCodes` (mapped to HTTP 403 by the existing `GatewayExceptionHandler`) and is audited as
   `WEBSQL_QUERY_DENIED`.
6. **Small and safe.** No change to `DeclarativeHttpDataSourceExecutor.cs` (797 lines). Tier 2 reuses the existing
   descriptor batch capability (`BatchType`, `BatchParamName`, `PrimaryKeyField`). Formats the executor cannot
   produce are deferred and handled fail-closed.

### 2.2 Data flow (WebSQL cross-source only, D-12)

```text
FederatedDuckDbExecutionService.ExecuteFederatedInternalAsync
  1. Analyze + route (unchanged)
  2. AuthorizeAllTablesAsync -> TableAccessDecision per table
       decision.AppliedVirtualFilterBindings  (NEW, from MandatoryRowFilterResolver)
       decision.MandatoryRowPredicateSql       (unchanged; non-WebSQL channels keep returning 403)
  3. IVirtualFilterFederationPlanner.PlanAsync(ast, tables, decisions)            (NEW)
       per HTTP table with applied VFs:
         validate bindings (structured, SQL key source, no validity window, supported key types)
         IVirtualFilterKeySetProvider.GetAsync(filter, keyTypes)  -> FrozenSet<VirtualFilterKey>
         VirtualFilterPointLookupExtractor (qualified, conjunctive, eligible shapes only)
         tier + strategy decision (FR-6 matrix) -> VirtualFilterTablePlan
           { Enforcement, SkipUpstream, PushdownArguments?, Tier, Reason, Strategy }
         or throw GatewaySecurityException(VF_*)
  4. CrossSourcePlanner.Plan(..., virtualFilterPlans)  -> StagingTableRequest.VirtualFilterPlan
       (no VF AST rewrite, no extra staging tables, no session flags)
  5. Start audit incl. VF summary + key-source SOURCE_READ events
  6. FederatedStagingService.StageAsync
       SkipUpstream            -> stage an empty table, connector NOT called (Tier 0 / Tier 1 miss)
       else                    -> session.Items[VirtualFilterEnforcement.SessionItemKey] = plan.Enforcement
                                  merge PushdownArguments (conflict = deny)
                                  GovernedConnectorReader.ReadAsync:
                                    enforcement captured BEFORE the connector runs
                                    raw rows -> enforcement.Apply (semi-join probe, typed keys)
                                    decision -> decision.WithoutEnforcedVirtualFilters(enforcement.Bindings)
                                    Apply(residual decision): consent/RLS/ABAC filter, masking, byte limit
  7. DuckDB executes the unmodified (staging-renamed) user query
```

### 2.3 Key mechanisms

**M1 - Decision carries bindings and a verifiable residual (WI-3).** `TableAccessDecision` gains:

- `IReadOnlyList<AppliedVirtualFilterBinding>? AppliedVirtualFilterBindings` (new Domain record
  `AppliedVirtualFilterBinding(VirtualFilter Filter, FilterBinding Binding)`), filled from the resolver's effective
  list (after `Supersedes`, grantee matching, ordering) via a new `MandatoryFilterOutcome.AppliedBindings`.
- `RowFilterSqlBeforeVirtualFilters` and `RowFilterSqlAfterVirtualFilters` (strings), maintained by
  `WithMandatoryPredicate(pred, names, bindings)` and by a new `WithAdditionalRowFilter(sql)`, which
  `TableAccessPolicy.Restrict` (ABAC merge, `TableAccessPolicy.cs:364-397`) must use. The `CombinedRowFilterSql`
  text produced stays byte-identical to today (`({combined}) AND ({added})`), so SQL channels are unaffected.
- `WithoutEnforcedVirtualFilters(IReadOnlyCollection<AppliedVirtualFilterBinding> enforced)` returns the residual
  decision (`CombinedRowFilterSql = Compose(Before, After)`, `MandatoryRowPredicateSql = null`,
  `AppliedVirtualFilters = []`, bindings `[]`). It throws `GatewaySecurityException(VF_ENFORCEMENT_INCOMPLETE)` when
  (a) any applied pair is not in `enforced`, or (b) the self-check
  `CombinedRowFilterSql == Compose(Compose(Before, Mandatory), After)` fails, i.e. some code path mutated
  `CombinedRowFilterSql` directly with `with { ... }` (for example `StreamRlsPolicyEnforcer.cs:221`). This makes a
  future bypass of the helper fail closed instead of silently dropping a filter.

**M2 - Typed keys (WI-4).** `VirtualFilterKey` is an immutable value type over a small `object[]` of canonical
values with precomputed hash and ordinal equality. `VirtualFilterKeyCanonicalizer` maps the **catalog type of the
target column** to a canonical CLR type: integer family -> `long` (accepts integral numerics and strict invariant
integer strings), `decimal`/`numeric` -> `decimal`, `uuid`/`uniqueidentifier` -> `Guid`, text family -> `string`
(ordinal, case-sensitive; numbers converted with invariant culture). Any other type (date/time, bool, float, binary)
is rejected at plan time with `VF_KEY_TYPE_MISMATCH`. Values that cannot be converted: key-source side dropped and
counted (AC-4.5), upstream side the row is dropped (never authorizes). `NULL` in any key component never matches
(AC-4.6). Case-insensitive catalog columns (D-13) are deferred; v1 is always case-sensitive (stricter).

**M3 - Enforcement object (WI-4).** `VirtualFilterEnforcement` (sealed, `internal` constructor so only the planner
in `Autheris.Application` can create it) holds, per applied pair, the target columns (after `ColumnMap`), the
canonicalizers and the key set. `Apply(rows)` keeps a row only if **for every pair** its projected key tuple is in
that pair's set (AND semantics, AC-3.3). `GovernedConnectorReader.ReadAsync` reads the item from `session.Items`
**before** calling the connector (a connector cannot inject one), applies it to raw rows (so a key column that is
denied to the caller still filters), then calls `Apply` with the residual decision. `GovernedReadResult` gains
`int? RowsBeforeVirtualFilter` for the audit (rows received vs. rows kept).

**M4 - Key loading in the key source (WI-5).** `StructuredFilterKeyQueryBuilder` renders
`SELECT DISTINCT <alias.key> AS <key>, ... FROM <from> AS <alias> [INNER JOIN ...] WHERE <conditions> AND <key> IS NOT NULL ...`
in the key source dialect, reusing FROM/JOIN/condition rendering extracted from `StructuredFilterSqlBuilder` into an
internal static `StructuredFilterSqlParts` (pure refactor; existing `StructuredFilterSqlTests` must stay green).
`SqlVirtualFilterKeySetProvider` resolves the catalog entry of `Structured.From` (must be `DataSourceType.Sql`; join
tables likewise), opens the governed read connection `DataSources.Connections[Table.SourceName ?? domain]` through
`ISqlConnectionFactory` (pattern of `SqlProcedureRowScopeResolver`), reads at most `MaxAuthorizedKeys + 1` rows with a
command timeout, canonicalizes, de-duplicates into a `FrozenSet` and caches it in `IMemoryCache` under
`vfkeys:{tenant}:{filter.Id}:{filter.ComputeDefinitionHash()}:{ordered key columns}:{canonical types}` with TTL
`VirtualFilters:Federation:KeyCacheTtlSeconds` (default 30, valid 1..300). Structured filters contain no principal
reference, so the key set is principal-independent and the SID is not part of the key (see section 6, item 1).
Stage 2 SQL filters and filters with a validity window are refused (`VF_KEY_SOURCE_UNSUPPORTED`).

**M5 - Tier selection (WI-6..WI-8).** `VirtualFilterFederationPlanner` decides per HTTP table:

| Order | Condition | Result |
|---|---|---|
| 1 | Any applied pair invalid (SQL filter, window, non-SQL key source, unsupported key type, unknown target column) | deny (`VF_KEY_SOURCE_UNSUPPORTED` / `VF_KEY_TYPE_MISMATCH` / `VF_BINDING_MISCONFIGURED`) |
| 2 | Any pair's key set empty | Tier 0: `SkipUpstream`, 0 rows, all strategies |
| 3 | Eligible point lookup and, for some pair, `queried ∩ authorized = ∅` | Tier 1 miss: `SkipUpstream`, all strategies |
| 4 | Strategy combination (most restrictive wins, see below) | Tier 1 hit / Tier 2 / Tier 3 / deny per PRD FR-6 table |

Combination of several bindings: any `ShortCircuitOnly` -> scans denied (`VF_SCAN_NOT_PERMITTED`) and a hit needs a
pushdown capability; any `PushdownOnly` -> pushdown mandatory, otherwise `VF_PUSHDOWN_LIMIT_EXCEEDED` (over budget) or
`VF_PUSHDOWN_UNAVAILABLE` (no capability/format); any `DuckDbHashJoin` -> no pushdown (Tier 3). Enforcement (M3) is
attached in **every** non-skip case.

**M6 - Point lookup eligibility (WI-7).** `VirtualFilterPointLookupExtractor` (replaces `SqlPointLookupExtractor`)
returns lookups only if: `SelectStatement.With == null`, `Body is QuerySpecification`, the FROM tree contains only
`NamedTableSource`/`JoinedTableSource`, no sub-query expression appears anywhere in the statement
(`InSubqueryExpression`, `ExistsExpression`, `ScalarSubqueryExpression`, `SubqueryTableSource`,
`LateralTableSource`), and the protected table occurs exactly once. Predicates are taken from the top-level AND chain
of `WHERE` only: `col = literal`, `literal = col`, `col IN (literal, ...)` (not `NOT IN`). A column counts only if it
is qualified with the table's alias/name, or unqualified while the FROM tree has exactly one named table. Literals
are canonicalized with M2; a non-convertible literal makes the lookup ineligible (scan). The cartesian product of
`IN` lists is capped by `MaxPointLookupTuples` (default 1,000; above = scan). Because a Tier 1 miss is executed as an
**empty staged relation** (not as an early "empty result" return), the result is always the true answer of the user
query (LEFT JOIN, aggregates, other tables) and has exactly the shape of an existing-but-empty match (NFR-2).

**M7 - Tier 2 pushdown via existing descriptor capability (WI-8).** `HttpKeyPushdownCapability.Resolve(descriptor)`:
`None` if `descriptor.Pagination` is paged (the executor's batch path bypasses pagination; incomplete pages would be
silently missing) or `BatchParamName` is empty; else `QueryParameterList` (`?{BatchParamName}=v1,v2`, comma values
percent-encoded by the existing `BuildUrl`), `JsonBodyArray` (POST JSON array via `JsonSerializer`) or `PathLookup`
(`ParallelSingleRequests`, one request per key into `{PrimaryKeyField}` of `PathTemplate`; requires the placeholder).
The pushed column is `descriptor.PrimaryKeyField`; only single-key pairs whose target column equals it contribute;
pushed values = intersection over those pairs (and with the queried keys for Tier 1). Budgets:
`count <= min(binding.MaxPushdownKeys, Federation.MaxPushdownKeys)`; `QueryParameterList`: no value contains `,` and
the estimated encoded URL length `<= Federation.MaxPushdownUrlLength` (default 2,048); `PathLookup`:
`count <= Federation.MaxPathLookupRequests` (default 10). Values are passed as `List<string>` (invariant canonical
text) under `BatchParamName`; `BuildUrl` already drops enumerable arguments from the query string, so they cannot
leak into other parameters. `FederatedStagingService` rejects a pushdown argument whose name already exists in the
session arguments (`VF_PUSHDOWN_ARGUMENT_CONFLICT`, AC-8.5). `FilterBinding.PushdownFormat` becomes nullable
(`null` = use the endpoint capability); when set it must be satisfiable (`CommaSeparated` <-> `QueryParameterList`,
`PostBatch` <-> `JsonBodyArray`; `RepeatedParam`/`ODataIn` never in v1): `Adaptive` then falls back to Tier 3 with
reason `format_unsupported`, `PushdownOnly` denies. Upstream non-2xx (including 414/413) fails the query (existing
`EnsureSuccessStatusCode`), never a partial result.

**M8 - Regex (WI-2).** New `Autheris.Domain.Model.TargetPatternMatcher`: patterns starting with `^` or `regex:` are
compiled once (cached `ConcurrentDictionary<string, Regex>`) as `^(?:pattern)$` with
`RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase` and a 50 ms timeout, matched
against `TableIdentifier.ToQualifiedName()` only. Construction errors (incl. `NotSupportedException` for
backreferences/lookarounds) are rejected by `FilterBinding.Validate`; at match time **every** exception propagates.
`MandatoryRowFilterResolver` already converts `RegexMatchTimeoutException`, `ArgumentException`,
`NotSupportedException` into a deny; its reason is prefixed with `VF_PATTERN_EVALUATION_FAILED`.

**M9 - Audit (WI-10).** Start record (`WEBSQL_CROSS_SOURCE_QUERY`) gains `virtualFilters[]` per table: filter names,
`bindingHash` (new public `FilterBinding.ComputeHash()` over `AppendCanonical`), strategy, tier, tier reason,
`shortCircuited`. One `WEBSQL_CROSS_SOURCE_SOURCE_READ` per loaded filter key set (filter, source, key count, dropped
keys, cache hit; never key values). The upstream `SOURCE_READ` gains `vfTier`, `upstreamCalled`, `pushdownKeyCount`,
`rowsReceived`, `rowsKept`. `RecordFailureAuditAsync` classifies `GatewaySecurityException` as `WEBSQL_QUERY_DENIED`
with `reasonCode = ErrorCode`. Trace ids for these records use `Activity.Current?.TraceId` when present.

### 2.4 New and removed types

| Action | Type / file |
|---|---|
| New (Domain) | `Model/TargetPatternMatcher.cs`, `Model/AppliedVirtualFilterBinding.cs`, `Options/VirtualFilterFederationOptions.cs` (`KeyCacheTtlSeconds`, `MaxAuthorizedKeys`=50,000, `KeyQueryTimeoutSeconds`=10, `MaxPushdownKeys`=1,000, `MaxPushdownUrlLength`=2,048, `MaxPathLookupRequests`=10, `MaxPointLookupTuples`=1,000, `PushdownEnabled`=true) |
| New (Application, `VirtualFilters/Federation/`) | `VirtualFilterErrorCodes`, `VirtualFilterKey`, `VirtualFilterKeyCanonicalizer`, `VirtualFilterEnforcement`, `StructuredFilterSqlParts`, `StructuredFilterKeyQueryBuilder`, `IVirtualFilterKeySetProvider`, `SqlVirtualFilterKeySetProvider`, `VirtualFilterKeySet`, `VirtualFilterPointLookupExtractor`, `HttpKeyPushdownCapability`, `VirtualFilterPushdownSelector`, `IVirtualFilterFederationPlanner`, `VirtualFilterFederationPlanner`, `VirtualFilterTablePlan` |
| Removed | `DefaultVirtualFilterKeyProvider`, `IVirtualFilterKeyProvider`, `VirtualFilterShortCircuitEvaluator` (+ `IVirtualFilterShortCircuitEvaluator`, `ShortCircuitEvaluationResult`), `PushdownUrlFormatter`, `SqlPointLookupExtractor`, `VirtualFilterJoinSpec`, `GovernedConnectorReader.VirtualFilterFederationHandledKey`, `StagingTableRequest.SessionItems/CustomArguments/PreloadedRows`, `FilterBinding.CompositeKeys`, `FederatedDuckDbExecutionService.EvaluateVirtualFiltersAsync` and its three optional ctor dependencies |

`GatewayOptions.cs` (1,790 lines, pre-existing violation of the 800-line rule) only receives one property line
(`VirtualFilterOptions.Federation`); all new options live in the new file.

### 2.5 File size budget (<= 800 lines)

| File | Now | Expected after | Note |
|---|---|---|---|
| `Sql/Services/FederatedDuckDbExecutionService.cs` | 646 | ~600 | -115 (evaluation removed), +~70 (planner call, audit). If > 700, extract `CrossSourceAuditRecorder` (start/failure/key-source audits). |
| `Sql/Services/CrossSourcePlanner.cs` | 410 | ~350 | VF join block and flag removed |
| `Olap/FederatedStagingService.cs` | 298 | ~360 | skip-upstream branch, enforcement item, audit fields |
| `Connectors/GovernedConnectorReader.cs` | 223 | ~245 | |
| `VirtualFilters/MandatoryRowFilterResolver.cs` | 390 | ~400 | |
| `Domain/Model/VirtualFilterModels.cs` | 544 | ~520 | regex moved out, `CompositeKeys` removed |
| `Domain/Interfaces/TableAccessDecision.cs` | 83 | ~150 | |
| `Policy/TableAccessPolicy.cs` | 600 | ~600 | `Restrict` uses helper |
| `Services/DeclarativeHttpDataSourceExecutor.cs` | 797 | 797 | **no change allowed** |
| `VirtualFilters/VirtualFilterAdministrationService.cs` | 959 | 959 | **no change** (write-time validation deferred) |
| New planner | - | <= 350 | split pushdown into `VirtualFilterPushdownSelector` |

---

## 3. Implementation plan (TDD, one topic = one commit)

Conventions: xUnit + Shouldly + NSubstitute (no FluentAssertions). New unit tests go to
`tests/Autheris.Tests.Unit/VirtualFilters/Federation/` unless stated. Each WI: write the failing test(s) first (Red),
minimal production change (Green), refactor, then `dotnet build Autheris.sln -warnaserror` and
`dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj`. Every commit must leave the system fail-closed.
Test harness for service-level tests: reuse the real-DuckDB wiring of
`tests/Autheris.Tests.Unit/Security/WebSqlHeterogeneousJoinTests.cs` (substitute `IAutherisConnector` per table, real
`CrossSourcePlanner`, `FederatedStagingService`, `DuckDbOlapEngine`). Create a shared helper
`VirtualFilters/Federation/FederatedVfPipelineHarness.cs` that takes decisions built with
`TableAccessDecision.Allowed(...).WithMandatoryPredicate(pred, names, bindings)`, a fake `IVirtualFilterKeySetProvider`,
and an HTTP connector stub that counts `ReadBatchAsync` calls and records `session.Arguments`.

### WI-1 (C) Remove the unconditional federation flag - fail-closed baseline
- **Files:** `Connectors/GovernedConnectorReader.cs` (delete `VirtualFilterFederationHandledKey` and its use at :70),
  `Sql/Services/CrossSourcePlanner.cs` (delete :177-182 and the `SessionItems` argument).
- **Red:**
  - `Security/CrossSourcePlannerTests.Plan_HttpTable_SetsNoRowFilterSuppressionItem` - staging request for an HTTP
    table and for a SQL table with `AppliedVirtualFilters` carries no session item that suppresses row filtering.
  - `VirtualFilters/Federation/FederatedStagingRowFilterRegressionTests.Stage_HttpTableWithConsentRowFilter_RowFilterIsEnforced`
    - plan + stage an HTTP table whose decision has `CombinedRowFilterSql = "status = 'OPEN'"`; connector returns OPEN
    and CLOSED rows; staged rows contain only OPEN (PRD E-9 at unit level).
  - `...Stage_HttpTableWithVirtualFilterPredicate_IsForbidden` - decision with `MandatoryRowPredicateSql` and no
    enforcement -> `GatewayForbiddenException` (pre-F-GOV-14 behaviour).
- **Green:** delete the flag; adjust/delete `FederatedVirtualFilterDuckDbTests` assertions that depend on it.
- **Done:** both regressions green; grep for `VirtualFilterFederationHandled` returns nothing in `src/`.

### WI-2 (C) Regex target patterns fail closed
- **Files:** new `Domain/Model/TargetPatternMatcher.cs`; `Domain/Model/VirtualFilterModels.cs` (`Validate`,
  `MatchesTarget` delegate to the matcher, no catch); `VirtualFilters/MandatoryRowFilterResolver.cs` (deny reason prefix).
- **Red (`VirtualFilters/TargetPatternMatcherTests`):**
  - `IsMatch_RegexIsFullMatchOnQualifiedName` - `^crm_api\.public\.cust` does not match `crm_api.public.customers`;
    `^crm_api\.public\.customers$` matches.
  - `Validate_RejectsBackreferenceAndLookaround` - `^(a)\1$`, `^(?=x).*` -> `ArgumentException`.
  - `IsMatch_InvalidPattern_Throws` - a stored invalid pattern (bypassing `Validate`) throws `ArgumentException`.
  - `IsMatch_Timeout_Propagates` - internal overload taking a prepared backtracking `Regex` with 1 ms timeout and a
    pathological input throws `RegexMatchTimeoutException` (proves no catch in the matcher).
- **Red (`VirtualFilters/MandatoryRowFilterResolverCrossSourceTests`):**
  `Resolve_InvalidStoredRegex_DeniesWithPatternEvaluationFailed` - outcome `IsDenied`, reason starts with
  `VF_PATTERN_EVALUATION_FAILED`; `Resolve_GlobPatternOnHttpTable_StillApplies` (non-regression).
- **Done:** no `catch` around regex matching in `FilterBinding`; `NonBacktracking` used.

### WI-3 (C) Decision carries applied bindings and a verifiable residual
- **Files:** `Domain/Interfaces/TableAccessDecision.cs`, new `Domain/Model/AppliedVirtualFilterBinding.cs`,
  `VirtualFilters/MandatoryRowFilterResolver.cs` (`MandatoryFilterOutcome.AppliedBindings` from `effective`),
  `Policy/TableAccessPolicy.cs` (pass bindings at :260; `Restrict` uses `WithAdditionalRowFilter`),
  `Procedures/Services/GovernedProcedureExecutionService.cs:691` (pass bindings; behaviour unchanged).
- **Red (`VirtualFilters/TableAccessDecisionVirtualFilterTests`):**
  - `WithMandatoryThenAdditional_CombinedTextUnchanged` - text equals the previous formula.
  - `WithoutEnforced_AllPairs_ReturnsResidualWithBeforeAndAfterFilters` and no mandatory predicate.
  - `WithoutEnforced_MissingPair_ThrowsEnforcementIncomplete` (`ErrorCode == "VF_ENFORCEMENT_INCOMPLETE"`).
  - `WithoutEnforced_CombinedMutatedDirectly_ThrowsEnforcementIncomplete` (`with { CombinedRowFilterSql = ... }`).
  - `WithoutEnforced_NoVirtualFilters_ReturnsSameDecision`.
- **Red (`MandatoryRowFilterResolverCrossSourceTests`):** `Resolve_TwoProfilesSameFilterDifferentColumnMaps_ReturnsBothBindings`;
  `Resolve_OtherGranteesProfile_NotInAppliedBindings`; `Resolve_SupersededFilter_NotInAppliedBindings`;
  `Resolve_HttpTarget_ProducesPredicateAndBindings` (verifies the predicate builder does not throw for HTTP metadata).
- **Red (`Policy` tests):** `Restrict_AfterVirtualFilter_ResidualKeepsAbacFilter`.
- **Done:** existing SQL-path tests asserting row filter text unchanged and green.

### WI-4 (C) Gateway enforcement at staging (semi-join probe)
- **Files:** new `VirtualFilters/Federation/{VirtualFilterErrorCodes, VirtualFilterKey, VirtualFilterKeyCanonicalizer, VirtualFilterEnforcement}.cs`;
  `Connectors/GovernedConnectorReader.cs` (`ReadAsync`: capture item before `ReadRawAsync`, apply, residual decision;
  `GovernedReadResult.RowsBeforeVirtualFilter`).
- **Red (`VirtualFilterKeyCanonicalizerTests`):** integer column accepts `47L`, `47`, `47.0d`, `"47"`, `"047"` as `47L`,
  rejects `"4x"`, `47.5d`; text column is ordinal (`"abc" != "ABC"`), numbers become invariant text; uuid canonical;
  `date`/`bool`/`double` column -> `VF_KEY_TYPE_MISMATCH`.
- **Red (`VirtualFilterEnforcementTests`):** `Apply_RowOutsideKeySet_Dropped`; `Apply_DuplicateKeyTuples_NoRowMultiplication`;
  `Apply_NullOrMissingKeyColumn_RowDropped`; `Apply_CompositeKey_RequiresAllComponents`;
  `Apply_TwoPairs_RowMustMatchBoth` (AND); `Apply_UpstreamStringVsSourceInteger_Matches`.
- **Red (`GovernedConnectorReaderVirtualFilterTests`):** `ReadAsync_EnforcementAndConsentFilter_BothApplied`;
  `ReadAsync_EnforcementCoversSubset_ThrowsEnforcementIncomplete`; `ReadAsync_ConnectorInjectsEnforcementItem_IsIgnored`
  (substitute connector writes the item during `ReadBatchAsync` -> 403 as without enforcement);
  `ReadAsync_DeniedKeyColumn_StillUsedForFiltering_NotReturned`.
- **Done:** enforcement is the only way to drop the VF predicate from in-memory filtering.

### WI-5 (H) Faithful, bounded key loading from the SQL key source
- **Files:** new `VirtualFilters/Federation/{StructuredFilterSqlParts, StructuredFilterKeyQueryBuilder, IVirtualFilterKeySetProvider, SqlVirtualFilterKeySetProvider, VirtualFilterKeySet}.cs`;
  refactor `VirtualFilters/StructuredFilterSqlBuilder.cs` to use `StructuredFilterSqlParts`; new
  `Domain/Options/VirtualFilterFederationOptions.cs` + one property in `GatewayOptions.cs` `VirtualFilterOptions`;
  DI in `GatewayGovernanceServiceExtensions.cs` (register provider, remove old registrations); delete
  `DefaultVirtualFilterKeyProvider.cs`, `IVirtualFilterKeyProvider.cs`, `VirtualFilterKeyProviderTests.cs`.
- **Red (`StructuredFilterKeyQueryBuilderTests`):** SQL text for SqlServer, PostgreSql, Sqlite: `SELECT DISTINCT`, all
  joins, all four operators, `IS NOT NULL` per key, quoted identifiers, escaped literals.
- **Red (`SqlVirtualFilterKeySetProviderTests`, real SQLite temp file via `SqlConnectionFactory`):**
  `Get_DavidCaseJoinAndIsNull_ReturnsExactKeys` (same data as `RowFilterChannelParityTests`);
  `Get_NotEqCondition_Excludes`; `Get_NullKeys_Excluded`; `Get_DuplicateRows_Deduplicated`; `Get_CompositeKey`;
  `Get_MoreThanMaxAuthorizedKeys_ThrowsKeySetTooLarge` (never truncates); `Get_SqlDefinition_ThrowsKeySourceUnsupported`;
  `Get_ValidityWindow_ThrowsKeySourceUnsupported`; `Get_FromTableNotSql_ThrowsKeySourceUnsupported`;
  `Get_SourceUnreadable_ThrowsSourceUnavailable` (`VIRTUAL_FILTER_SOURCE_UNAVAILABLE`, inner exception kept);
  `Get_SecondCall_CacheHit_NoQuery` (rows deleted between calls, result unchanged, `CacheHit == true`);
  `Get_DefinitionChanged_CacheMiss`; `Get_UnconvertibleKey_DroppedAndCounted`; `Options_TtlOutOfRange_Rejected`.
- **Done:** `StructuredFilterSqlTests` unchanged and green; no public mutation API on the provider.

### WI-6 (C) Federation planner Tier 0 / Tier 3 and wiring; delete the fail-open code
- **Files:** new `VirtualFilters/Federation/{IVirtualFilterFederationPlanner, VirtualFilterFederationPlanner, VirtualFilterTablePlan}.cs`;
  `Sql/Services/FederatedDuckDbExecutionService.cs` (remove `EvaluateVirtualFiltersAsync`, the three optional deps
  and the early empty-result return; parse AST once; call planner after authorization; optional ctor param
  `IVirtualFilterFederationPlanner? vfPlanner` - if `null` and any HTTP decision has applied VFs ->
  `VF_ENFORCEMENT_UNAVAILABLE`; `RewriteSqlAsync` no longer loads keys);
  `Sql/Services/CrossSourcePlanner.cs` (`ICrossSourcePlanner.Plan(..., IReadOnlyDictionary<string, VirtualFilterTablePlan>? virtualFilterPlans = null)`,
  delete VF join block and `VirtualFilterJoinSpec`); `Olap/IFederatedStagingService.cs` (`StagingTableRequest`:
  replace `SessionItems`, `CustomArguments`, `PreloadedRows` with `VirtualFilterTablePlan? VirtualFilterPlan`);
  `Olap/FederatedStagingService.cs` (skip-upstream branch stages an empty `OlapTableSource` and still counts against
  budgets and audits; otherwise set the enforcement item); DI registration of the planner; delete
  `VirtualFilterShortCircuitEvaluator.cs` and its tests; rewrite `FederatedVirtualFilterDuckDbTests`.
  Strategy handling in this WI: `ShortCircuitOnly` -> `VF_SCAN_NOT_PERMITTED` for every query (point lookups arrive in
  WI-7), `PushdownOnly` -> `VF_PUSHDOWN_UNAVAILABLE` (until WI-8), `Adaptive`/`DuckDbHashJoin` -> Tier 3.
- **Red (`FederatedVirtualFilterEnforcementTests`, harness with real DuckDB):**
  `Scan_UpstreamReturnsEverything_OnlyAuthorizedRows`; `Scan_SelectStar_ColumnsEqualHttpTableColumns`;
  `Scan_DuplicateKeyTuples_CountStarEqualsAuthorizedRowCount`; `Union_OverProtectedTable_OnlyAuthorizedRows`;
  `Cte_OverProtectedTable_OnlyAuthorizedRows`; `Subquery_OverProtectedTable_OnlyAuthorizedRows`;
  `SelfJoin_OverProtectedTable_OnlyAuthorizedRows`; `TwoFiltersDisjointKeySets_ZeroRows`;
  `EmptyKeySet_ConnectorNeverCalled_ZeroRows` and with a joined SQL table the SQL side still executes;
  `KeySourceUnavailable_Denied_ConnectorNeverCalled`; `SqlDefinitionFilterOnHttp_DeniedKeySourceUnsupported`;
  `UnsupportedKeyColumnType_DeniedKeyTypeMismatch`; `DecisionHasAppliedNamesButNoBindings_DeniedEnforcementUnavailable`;
  `ShortCircuitOnlyScan_DeniedScanNotPermitted`; `UpstreamOverMaxStagedRows_DeniedRowLimitExceeded` (no truncation).
- **Red (`FederatedStagingRowFilterRegressionTests`):** `Stage_PushdownArgumentConflict_Denied` (prepared for WI-8);
  `Stage_SkipUpstream_ConnectorNotResolvedNorCalled`.
- **Done:** HTTP + VF works end to end via Tier 0/3; no fail-open code from `e7ee288` remains (grep for
  `SetInMemoryTuples`, `VirtualFilterJoinSpec`, `PreloadedRows`, `EvaluateVirtualFiltersAsync` is empty).

### WI-7 (M) Tier 1 point lookups with qualified, eligibility-checked extraction
- **Files:** new `VirtualFilters/Federation/VirtualFilterPointLookupExtractor.cs`; planner; delete
  `Sql/Services/SqlPointLookupExtractor.cs` and `SqlPointLookupExtractorTests.cs`.
- **Red (`VirtualFilterPointLookupExtractorTests`):** qualified alias match; unqualified only with one table;
  `other.id = 47` ignored for the protected table; `OR` not extracted; `NOT IN` ignored; `IN (1,2)` yields two
  values; CTE / UNION / sub-query in WHERE / self-join -> ineligible; literal not convertible -> ineligible;
  cartesian above `MaxPointLookupTuples` -> ineligible.
- **Red (`FederatedVirtualFilterPointLookupTests`):** `Miss_ZeroRows_ConnectorNeverCalled_SameColumnsAsEmptyMatch`;
  `Miss_InLeftJoin_ResultEqualsFullFetchResult` (differential: same query with `PushdownEnabled = false`);
  `Hit_Adaptive_NoCapability_FullFetchEnforced`; `CompositePointLookup_MissOnOneComponent_ZeroRows`;
  `ShortCircuitOnly_Miss_ZeroRows`; `ShortCircuitOnly_HitWithoutCapability_DeniedPushdownUnavailable`.
- **Done:** FR-6 rows "point lookup" covered for strategies without pushdown.

### WI-8 (M) Tier 2 pushdown via endpoint capability, budgets and strategy matrix
- **Files:** new `VirtualFilters/Federation/{HttpKeyPushdownCapability, VirtualFilterPushdownSelector}.cs`; planner;
  `Olap/FederatedStagingService.cs` (merge `PushdownArguments`, conflict -> `VF_PUSHDOWN_ARGUMENT_CONFLICT`);
  `Domain/Model/VirtualFilterModels.cs` (`PushdownFormat` nullable, canonical hash prints empty for null);
  `Infrastructure/Persistence/VirtualFilterStore.cs` (null round trip); delete `PushdownUrlFormatter.cs` and
  `PushdownUrlFormatterTests.cs`.
- **Red (`HttpKeyPushdownCapabilityTests`):** paged endpoint -> `None`; empty `BatchParamName` -> `None`;
  `ParallelSingleRequests` without `{PrimaryKeyField}` placeholder -> `None`; the three supported kinds.
- **Red (`VirtualFilterStrategyMatrixTests`, `[Theory]` per FR-6 cell, AC-6.1):** 4 strategies x 5 situations ->
  expected tier, `SkipUpstream`, pushed values or error code; plus `Adaptive_FormatUnsupported_Tier3`,
  `PushdownOnly_FormatUnsupported_Denied`, `Adaptive_UrlTooLong_Tier3`, `PushdownOnly_UrlTooLong_DeniedLimitExceeded`,
  `CommaInValue_NoQueryParameterListPushdown`, `Hit_PushesIntersectionNotWholeSet` (AC-6.3),
  `PushdownDisabledOption_AlwaysTier3`.
- **Red (pipeline, `FederatedVirtualFilterPushdownTests`):** connector stub records `session.Arguments[BatchParamName]`
  equals the authorized list; stub that ignores the argument and returns all rows still yields only authorized rows.
- **Done:** every FR-6 cell has a test; no code path sends a pushdown the endpoint does not declare.

### WI-9 (L) Binding model cleanup
- **Files:** `Domain/Model/VirtualFilterModels.cs` (remove `CompositeKeys`; `MaxPushdownKeys` in
  `1..FilterBinding.HardMaxPushdownKeys` = 1,000; canonical hash), `Infrastructure/Persistence/VirtualFilterStore.cs`
  (drop `CompositeKeys` from the DTO; old JSON with the property still deserializes).
- **Red:** `VirtualFilterModelTests.Validate_MaxPushdownKeysOutOfRange_Rejected` (0, 1,001);
  `SqliteVirtualFilterRepositoryTests.Binding_FederationFields_RoundTrip` (strategy, nullable format, max keys);
  `VirtualFilterModelTests.DefinitionHash_ChangesWithStrategyAndMaxKeys`;
  `SqliteVirtualFilterRepositoryTests.LegacyBindingJsonWithCompositeKeys_StillLoads`.
- **Done:** effective key set is derived only from `VirtualFilter.KeyColumns` (D-9).

### WI-10 (H) Audit completeness
- **Files:** `Sql/Services/FederatedDuckDbExecutionService.cs` (or extracted `CrossSourceAuditRecorder.cs`),
  `Olap/FederatedStagingService.cs`, `Domain/Model/VirtualFilterModels.cs` (`FilterBinding.ComputeHash()`).
- **Red (`FederatedVirtualFilterAuditTests`, substitute `IAuditLogRepository`):**
  `TierOneMiss_WritesStartRecordWithTierAndShortCircuit_AndUpstreamReadWithUpstreamCalledFalse`;
  `KeySourceRead_AuditedWithCountAndCacheHit_WithoutKeyValues` (DetailsJson contains no key literal);
  `Tier3_UpstreamRead_HasRowsReceivedAndKept`; `GatewaySecurityException_AuditedAsQueryDeniedWithErrorCode` (one test
  per VF code via `[Theory]`); `AuditWriteFailure_FailsQuery` (INV-17 regression).
- **Done:** AC-9.1..AC-9.4 covered; no key values in any audit payload.

### WI-11 (M) Apply Virtual Filters under consent bypass in WebSQL cross-source
- **Files:** `Sql/Services/FederatedDuckDbExecutionService.cs` (`AuthorizeAllTablesAsync` bypass branch at :383-386 calls
  `IMandatoryRowFilterResolver` like `GovernedProcedureExecutionService.WithVirtualFiltersAsync`; denial -> policy
  exception).
- **Red:** `FederatedVirtualFilterEnforcementTests.ConsentBypassWithoutConsentServices_VirtualFilterStillApplied`
  (resolver substitute returns a predicate with bindings -> only authorized rows) and
  `..._ResolverDenies_QueryDenied`.
- **Done:** no WebSQL cross-source configuration skips Virtual Filter resolution.

### WI-12 (H) Integration fixture + E2E-1, E2E-2, E2E-7 (no containers)
- **Files:** new `tests/Autheris.Tests.Integration/FederatedVirtualFilterWebApiFixture.cs`,
  `FederatedVirtualFilterTierTests.cs`.
- **Fixture:** `WebApplicationFactory<Program>` as in `HttpDataSourceIntegrationTests` / `RowFilterChannelParityTests`:
  test auth handler, SQLite governance DB, `Gateway:WebSql:Enabled=true`, `Gateway:WebSql:CrossSource:Enabled=true`;
  key source = SQLite temp file registered as data source `authz` (`client_authorization(client_id INTEGER, tenant_code TEXT, is_active INTEGER)`
  with duplicates, NULLs, inactive rows); HTTP tables `crm_api.public.customers_*` with
  `HttpEndpointDescriptor { BaseUrl = "https://mock.crm.internal", CompleteResponse = true, PrimaryKeyField = "id" }`
  in variants: no batch, `QueryParameterList`, `JsonBodyArray`, `PathLookup` (`/api/v1/customers/{id}`), paged;
  replace `IHttpClientFactory` with a `CountingCrmApiHandler` (records method, path+query, body; modes
  `HonorFilters` / `IgnoreFilters`; thread-safe counters, reset per test). Seed filters/profiles through
  `VirtualFilterAdministrationService` (regex `TargetPattern`, `ColumnMap`, per-table strategies). Tests in one
  collection, non-parallel.
- **Tests:** `E2E1_PointLookupNotAuthorized_ZeroRows_ZeroUpstreamRequests_CompleteAudit`;
  `E2E2_PointLookupAuthorized_OneRow_OneRequestCarryingOnlyThatKey` (PathLookup table -> `GET /api/v1/customers/47`);
  `E2E7_EmptyKeySet_ZeroRows_ZeroUpstreamRequests`.

### WI-13 (H) E2E-3 Tier 2 per format + upstream that ignores parameters
- **File:** `tests/Autheris.Tests.Integration/FederatedVirtualFilterPushdownTests.cs`.
- **Tests (`[Theory]` over the descriptor variants):** `E2E3_ThreeAuthorizedKeys_RequestInDeclaredFormat`
  (`QueryParameterList`: decoded query `ids=101,102,105`; `JsonBodyArray`: POST body `["101","102","105"]`;
  `PathLookup`: three GETs); `E2E3_UpstreamIgnoresParameters_OnlyAuthorizedRows` (stub in `IgnoreFilters`);
  `E2E3_RepeatedParamOrODataBinding_AdaptiveFallsBackToFullFetch_OnlyAuthorizedRows`;
  `E2E3_PagedEndpoint_NoPushdown_OnlyAuthorizedRows`.

### WI-14 (H) E2E-4 composite keys, E2E-6 query shapes, E2E-9 non-VF row filter
- **File:** `tests/Autheris.Tests.Integration/FederatedVirtualFilterShapeTests.cs`.
- **Tests:** `E2E4_CompositeKey_DuplicateAndMixedTypeTuples_ExactRows_NoDuplicates_NoExtraColumns_CorrectCount`
  (`(client_id, tenant_code)` -> `(id, tenantCode)`; upstream mixes `101` and `"101"`); `E2E6_Union`, `E2E6_Cte`,
  `E2E6_Subquery`, `E2E6_SelfJoin`, `E2E6_JoinWithSqlTable` -> only authorized rows;
  `E2E9_ConsentRowFilterOnHttpTable_StillEnforcedInCrossSourceQuery` (consent row filter as in
  `RowFilterChannelParityTests.SeedConsentRowFilterAsync`, no VF on that table).

### WI-15 (H) E2E-5 key source down, E2E-8 strategy matrix and denials, channel parity guard
- **File:** `tests/Autheris.Tests.Integration/FederatedVirtualFilterFailClosedTests.cs`.
- **Tests:** `E2E5_KeySourceUnreachable_Forbidden_ZeroUpstreamRequests_DeniedAudit` (filter on data source
  `authz_down` whose SQLite path is in a non-existent directory with `Mode=ReadOnly`);
  `E2E8_ShortCircuitOnlyScan_ForbiddenScanNotPermitted`; `E2E8_PushdownOnlyOverBudget_ForbiddenLimitExceeded`;
  `E2E8_InvalidStoredRegex_Forbidden` (profile written via `IVirtualFilterRepository` to bypass write-time validation);
  `E2E8_DuckDbHashJoinStrategy_NeverSendsPushdown`; `D12_RestAndODataOnProtectedHttpTable_StillForbidden`.

### WI-16 (optional) SQL Server key source variant
- **File:** `tests/Autheris.Tests.Integration/FederatedVirtualFilterSqlServerTests.cs` using `SqlServerTestDatabase.cs`.
  Runs only with Testcontainers (`TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal`). Re-runs E2E-1, E2E-4 and E2E-5
  with an MSSQL authorization view as key source (concept scenario). Not a release gate if containers are unavailable
  in CI; the SQLite suite is the gate.

### WI-17 Final verification (no new behaviour)
- `dotnet build Autheris.sln -warnaserror`; full unit + integration (non-container) suites green.
- Line counts of every touched file <= 800 (except the pre-existing `GatewayOptions.cs`, which grows by one line).
- Grep guards: `VirtualFilterFederationHandled`, `SetInMemoryTuples`, `VirtualFilterJoinSpec`, `PushdownUrlFormatter`,
  `"anonymous"` in `VirtualFilters/` -> no hits.
- Hand over to Phase 5 (review) and Phase 6 (docs: `docs/features/f-gov-14-federated-virtual-filters.md` must be
  rewritten to the implemented behaviour, including the deferred items below and the revocation latency = TTL).

---

## 4. Out of scope, deferred items and risks

### 4.1 Deferred (each one is fail-closed in v1)

| Item | PRD ref | v1 behaviour |
|---|---|---|
| `RepeatedParam` and `ODataIn` request formats, typed OData literals | AC-8.1 | Binding asserting them: `Adaptive` -> Tier 3 (full fetch + enforcement), `PushdownOnly` -> deny |
| `PostBatch` with an object body / property name (`{"ids": [...]}`) | AC-8.1 | Only the existing bare JSON array body (`JsonBodyArray`) |
| Pushdown capability fields on `HttpEndpointDescriptor` (`MaxUrlLength`, `MaxKeysPerRequest`, filtered column) | AC-5.1 | Gateway-wide options; filtered column = `PrimaryKeyField` |
| URL chunking into several requests | AC-8.2 | Over budget = Tier 3 (`Adaptive`) or deny (`PushdownOnly`) |
| One Tier 3 retry on HTTP 414/413 | AC-8.3 | Any non-2xx fails the query (no partial result) |
| Write-time validation of bindings against catalog and endpoint | AC-1.2, AC-1.4, AC-5.2, NFR-5 | Checked at query time, deny `VF_BINDING_MISCONFIGURED`; needs extraction from `VirtualFilterAdministrationService` (959 lines) first |
| Validity-window filters on Web-API targets | AC-4.1 | Deny `VF_KEY_SOURCE_UNSUPPORTED` |
| Stage 2 (SQL text) filters as key source | D-15 | Deny `VF_KEY_SOURCE_UNSUPPORTED` |
| Case-insensitive key columns from the catalog | D-13 | Always ordinal, case-sensitive (stricter) |
| `ArrayPool`/Appender streaming, LOH avoidance | NFR-7 | Bounded by `MaxStagedRowsPerTable` / `MaxStagedBytesPerTable` (deny on overflow) |
| Response byte cap in `DeclarativeHttpDataSourceExecutor` (whole `JsonDocument` parsed) | NFR-3 | Pre-existing; out of scope (file at 797 lines) |
| Metrics per tier, 414 fallbacks, denials by code | NFR-9 | Audit records only |
| HTTP 503 mapping for `VIRTUAL_FILTER_SOURCE_UNAVAILABLE` | AC-4.3 | 403 per existing `GatewayExceptionHandler` policy |
| Channel parity (REST, GraphQL, OData, MCP) | D-12 | Remain 403 via `EnsureInMemoryFilterIsEnforceable` |
| Performance gate (Tier 3 50k rows / 10k keys < 2 s p95) | NFR-7 | Measured in Phase 5, not a CI gate |

### 4.2 Risks

1. **Upstream row budget before filtering.** Tier 3 counts upstream rows against `MaxStagedRowsPerTable` *before*
   enforcement; large APIs without pushdown capability will be denied (`ROW_LIMIT_EXCEEDED`). Correct but may surprise
   operators; Phase 6 must document it.
2. **Residual self-check may deny legitimate combinations** if another code path adds row filters with a raw
   `with { CombinedRowFilterSql = ... }` after the Virtual Filter (only `StreamRlsPolicyEnforcer` today, not on this
   path). Mitigation: `VF_ENFORCEMENT_INCOMPLETE` is explicit and audited; fix by using `WithAdditionalRowFilter`.
3. **Regex semantics change** (full match, non-backtracking) may stop matching patterns written for prefix matching.
   The feature is unreleased; Phase 6 documents the rule.
4. **Key-set staleness** up to the cache TTL after revocation in the key source (max 5 min, default 30 s).
5. **Point-lookup differential correctness** depends on the eligibility rules (M6); the differential tests in WI-7
   compare against the forced full-fetch result to catch regressions.
6. **Concurrent AST work** by another agent in `src/TrinoSqlEngine/Ast`: this plan only *reads* AST node types
   (`SelectStatement`, `QuerySpecification`, `InListExpression`, ...). If node shapes change, WI-7 extractor tests
   will flag it; no generator or dialect file is touched.
7. **Removing `CompositeKeys` and making `PushdownFormat` nullable changes the binding definition hash** of profiles
   saved since `e7ee288`; drift detection may report them once. Acceptable for an unreleased feature.

---

## 5. Constraints honoured

- No change under `src/TrinoSqlEngine/Ast` or any dialect generator file; Virtual Filter enforcement is done at
  staging, the DuckDB SQL generation path is only simplified (VF join block removed from `CrossSourcePlanner`).
- No change to `DeclarativeHttpDataSourceExecutor.cs` (797 lines) or `VirtualFilterAdministrationService.cs`
  (959 lines).
- Every touched or new file stays <= 800 lines (budget in section 2.5).
- English only; LF line endings.

## 6. Points where this plan deviates from or refines the PRD

1. **AC-10.1 (SID in cache key):** structured filters contain no principal reference, so the key set does not depend
   on the caller; *which* filters apply is decided by grantee matching (FR-3). The cache key therefore omits the SID.
   Stage 2 filters (the only ones that could be principal-scoped) are denied in v1.
2. **AC-4.1 ("validity window evaluated at query time"):** on SQL objects the window is compared with the
   binding's time column of each target row, not with the query time. Evaluating it at query time would change
   semantics; v1 denies windowed filters on Web-API targets instead.
3. **D-2 / concept Tier 3 "DuckDB hash join":** the semi-join is executed in the gateway at staging (hash probe on a
   `FrozenSet`) instead of joining a staged key relation inside DuckDB. Same row semantics, no key relation (AC-11.1
   becomes moot; the key set is bounded by `MaxAuthorizedKeys`), no AST change, no type coercion inside DuckDB.
4. **AC-8.4 (executor must throw on unapplied pushdown arguments):** with mandatory gateway enforcement an ignored
   argument costs egress only; pushdown is only issued for declared capabilities and the executor stays unchanged.
5. **AC-6.2:** eligibility is stricter than the PRD (single query specification, protected table occurs once, no
   sub-queries); other shapes simply run as scans.
6. **AC-1.2/AC-1.4/AC-5.2 at write time:** deferred; enforced at query time (fail-closed).

## 7. Traceability

- Parent: [00-master-plan-overview.md](./00-master-plan-overview.md) - `PLAN-FEDERATED-VF-DUCKDB-15`
- Requirements: [2026-10-10-req-federated-virtual-filters-web-api.md](./2026-10-10-req-federated-virtual-filters-web-api.md)
- Origin concept: [2026-10-10-konzept-federated-virtual-filter-web-api-duckdb.md](./2026-10-10-konzept-federated-virtual-filter-web-api-duckdb.md)
- Next: Phase 3 security audit of sections 2 and 4 of this plan; Phase 4 implementation WI-1..WI-17 in order.
- Phase 3 result: see [Security Review (Phase 3)](#security-review-phase-3) below - verdict *approve with
  conditions*; adds WI-18 (before WI-3), WI-19 (part of WI-5) and WI-20, and security test criteria ST-1..ST-38.

---

## Security Review (Phase 3)

**Role:** `csharp-security-expert` (AGENTS.md section 6, Phase 3). **Date:** 2026-10-10.
**Scope:** (a) the code at HEAD (`e7ee288` implementation, tree `088f158`) and (b) the Phase-2 target design
(sections 2-4 above). Audited independently from the Phase-1/Phase-2 findings; rows marked *confirmed* re-verify a
Phase-2 gap, all other rows are new. Line numbers refer to the tree at `088f158`.

### S.1 Findings

Severity: **Critical** (data exposure / authorization bypass), **High**, **Medium**, **Low**.
"HEAD" = present in the current code; "Design" = present in, or not prevented by, the Phase-2 target design.

| ID | Sev. | Location | Description | Exploit scenario | Required fix | WI |
|---|---|---|---|---|---|---|
| SEC-1 | Critical | `FederatedDuckDbExecutionService.cs:416`, `CrossSourcePlanner.cs:90,146-147`; Design M5/WI-6 (`IReadOnlyDictionary<string, VirtualFilterTablePlan>`) | HEAD + Design. Decisions are keyed by `TableIdentifier.ToQualifiedName()`, which is `schema.table` **without the domain** (`TableIdentifier.cs:36`). Two tables from different sources with the same schema/table name collide (last write wins). A missing decision falls back to `TableAccessDecision.Allowed(..., hasUnconstrainedColumnAllow: true)` (fail-open). The target design keys `virtualFilterPlans` the same way, so the single enforcement point inherits the collision. | `SELECT c.* FROM crm_api.public.customers c JOIN erp.public.customers e ON c.id = e.id`: the decision of `erp.public.customers` (no VF, no consent filter) overwrites the HTTP table's decision; the HTTP table is staged without VF enforcement and without its own row filter / column denies. | Key decisions, plans and staging requests by `ResolvedSourceTable` identity (index) or by the domain-qualified, case-normalized `ToString()`; a duplicate key with a different decision denies; a missing decision throws `GatewaySecurityException("VF_ENFORCEMENT_INCOMPLETE")` instead of defaulting to `Allowed`. | NEW WI-18 |
| SEC-2 | Critical (*confirmed*, extended) | `CrossSourcePlanner.cs:177-182`, `GovernedConnectorReader.cs:69-70` | HEAD. `VirtualFilterFederationHandled` is set for every HTTP table, and the reader evaluates the flag **after** `ReadRawAsync`, so a connector can also set it (or `RlsPushdownExecuted`) on `session.Items` during `ReadBatchAsync` and self-attest that filtering happened. | Any HTTP table in a cross-source query: consent/RLS/tenant/VF row filters skipped. A third-party/extension connector can disable in-memory row filtering of its own tables. | WI-1 as planned. Additionally (design): for `HttpDeclarative` tables the reader ignores `RlsPushdownExecuted`/`InDbColumnMaskingExecuted` self-attestation; every session flag that relaxes enforcement is snapshotted **before** the connector is called (see SEC-14). | WI-1, WI-4 |
| SEC-3 | Critical (*confirmed*, extended) | `FederatedDuckDbExecutionService.cs:596-627`, `DeclarativeHttpDataSourceExecutor.cs:67-79,516-521` | HEAD. The pushdown path adds no post-filter. Besides the Phase-2 cases (empty key set, `BatchType=None`), the executor also silently falls back to an **unfiltered full fetch** when every key stringifies to `""` (`:73`, `keysList.Count == 0`) and when the batch parameter name is reserved or malformed (`BuildUrl` drops it with only a warning, `:518-521`). | Authorized keys `""` from the key source, or a `BatchParamName` equal to `TenantIdQueryParam`: upstream receives no key restriction, all rows are returned to the caller. | Resolved by mandatory staging enforcement (M3). Condition: the pushdown selector treats these cases as "no capability" (SEC-15) so `PushdownOnly` denies instead of silently fetching everything. | WI-4, WI-6, WI-8 |
| SEC-4 | Critical | `DefaultVirtualFilterKeyProvider.cs:166-245` | HEAD. Key loading over-grants. `ProjectedColumns` is set to the **key columns only** (`:172`); `SqlDataSourceExecutor` selects only `RequestedFields` (`SqlDataSourceExecutor.cs:136`), so the WHERE-condition columns are absent from the rows. `MatchesFilterWhereConditions` then sees `null`: `NotEq` and `IsNull` always pass, `Eq` always fails. Joins are ignored, unknown operators match (no `default`), comparison is case-insensitive. | Filter `WHERE a.revoked_at IS NULL` or `a.status <> 'INACTIVE'`: every key in the table (including revoked/inactive assignments) is authorized. | Delete the in-memory evaluation. The WI-5 key query executes the full structured definition (FROM, all JOINs, all conditions) in the key source with bound parameters; parity test against the SQL-channel predicate is mandatory (ST-14). | WI-5 |
| SEC-5 | High | `DefaultVirtualFilterKeyProvider.cs:173-181`, `GovernedConnectorReader.cs:94` | HEAD. `limit=50000` is pushed into the SQL executor, which returns at most 50,000 rows, so `rows.Count > max` never trips: the key set is **silently truncated** to an arbitrary subset (no ORDER BY), and the in-memory WHERE is applied after truncation. | Nondeterministic under-grant between requests; combined with SEC-4 the subset is also over-permissive. | WI-5: the key query requests `MaxAuthorizedKeys + 1` rows (`TOP`/`LIMIT` in SQL), counts them and throws `VF_KEY_SET_TOO_LARGE` above `MaxAuthorizedKeys`. Never rely on a connector-side clamp. | WI-5 |
| SEC-6 | High | Design M4 (`SqlVirtualFilterKeySetProvider` via raw `ISqlConnectionFactory`); HEAD `DefaultVirtualFilterKeyProvider.cs:167` (`Allowed`, no row filter) | HEAD + Design. The key-source read is a new privileged read path that bypasses tenant isolation of the key-source tables. `SqlProcedureRowScopeResolver` (the pattern the design copies) adds the tenant predicate (`SqlProcedureRowScopeResolver.cs:115-119`); M4 does not mention it, and structured filters (unlike Stage 2, `SqlFilterCompiler.cs:120-132`) are not required to constrain a tenant column. | Shared authorization table with rows of several tenants: a structured filter of tenant A without a tenant condition authorizes keys that were assigned in tenant B; tenant A users see HTTP rows whose keys only tenant B granted. | For `From` and every `Join` table: `TableMetadata.RequireTenantColumnOrThrow(...)`; if a tenant column exists, append `<alias>.<tenantCol> = @tenant` (bound parameter, value = caller's `TenantId`). If `RequireTenantColumn` is on and a table has none (and is not exempt) -> `VF_KEY_SOURCE_UNSUPPORTED`. The key-source connection should use a read-only login. | NEW WI-19 |
| SEC-7 | High | Design M8 / WI-2 (match against `ToQualifiedName()` only); HEAD `VirtualFilterModels.cs:377-386` | Design. `ToQualifiedName()` omits the domain, so the planned full-match `^(?:pattern)$` makes every domain-qualified pattern (as in the PRD and in the WI-2 test `^crm_api\.public\.customers$`) **never match** -> binding silently not applied -> filter not enforced when the profile's `Uncovered` policy is not `Deny`. Domain-less patterns match the same schema/table in every source. HEAD: timeout/invalid pattern returns `false` (fail-open, *confirmed*). | Admin writes `regex:^crm_api\.public\.customers$`; after WI-2 as written the binding never applies and users get unfiltered rows. | Match against the domain-qualified `TableIdentifier.ToString()` (`domain.schema.table`) only, `IgnoreCase | CultureInvariant | NonBacktracking`, full-match anchoring. Correct the WI-2 test expectations. | WI-2 |
| SEC-8 | Medium | HEAD `VirtualFilterModels.cs:377-378`; Design M8 | HEAD: backtracking engine, static `Regex.IsMatch` (shared 15-entry cache -> re-parse per call), two matches per binding per table, 50 ms each; patterns are tenant-admin authored but CPU is shared across tenants. Design: a `NotSupportedException` from `NonBacktracking` that escapes the resolver is mapped to HTTP **501** (`GatewayExceptionHandler.cs:79`); the regex cache is unbounded. | A tenant admin stores many bindings with heavy patterns; every WebSQL query of every tenant on the node pays up to 100 ms per binding (cross-tenant availability). | `FilterBinding.Validate`: pattern length <= 256, construct with `NonBacktracking` (rejects backreferences/lookarounds). Match-time errors become `GatewaySecurityException("VF_PATTERN_EVALUATION_FAILED")` at the resolver boundary (never 500/501). Regex cache cleared on snapshot generation change. | WI-2 |
| SEC-9 | High (*confirmed*) | `FederatedDuckDbExecutionService.cs:580-590`, `VirtualFilterShortCircuitEvaluator.cs:77,115-179`, `DefaultVirtualFilterKeyProvider.cs:49,97-98`, optional ctor deps | HEAD. Confirms the Phase-2 gaps: profile fallback without grantee matching (another grantee's filter), first binding only, null DI deps skip enforcement, public `SetInMemoryTuples` keyed by filter name across tenants, `"anonymous"` SID in the cache key, cache key without key columns/definition hash, UNION/CTE/sub-query/self-join bypass of the INNER JOIN rewrite, `OrdinalIgnoreCase` key comparison. | As described in section 1. | As planned. | WI-3, WI-5, WI-6 |
| SEC-10 | Medium | `DefaultVirtualFilterKeyProvider.cs:109,119`; `GatewayStorageServiceExtensions.cs:35-38`; Design M4 | HEAD + Design. The shared `IMemoryCache` is registered with `SizeLimit` (default 512 MB). `Set` without `Size` throws `InvalidOperationException`: at HEAD every uncached key load fails (wrapped as `VIRTUAL_FILTER_SOURCE_UNAVAILABLE`; the `SetInMemoryTuples` branch escapes as HTTP 500). Unit tests use an unbounded cache and do not see it. The design specifies no `Size`, no separate cache and no single-flight loading. | Production: feature denies every query (fail-closed, but untested). Fixed naively with `Size = 1`, 50,000-key sets are unaccounted; with real sizes they can evict `AccessProfileCache` entries. Stampede: N concurrent misses run N key queries. | Dedicated `MemoryCache` instance for key sets with its own `SizeLimit` (option, default 64 MB) and `Size` = estimated bytes; single-flight per cache key (`Lazy<Task>` / per-key `SemaphoreSlim`); failures are not cached. | WI-5 |
| SEC-11 | Medium | `DefaultVirtualFilterKeyProvider.cs:131`, `VirtualFilterShortCircuitEvaluator.cs:130`, `FederatedDuckDbExecutionService.cs:490` | HEAD. Exception messages embed `inner.Message` (database errors can contain data values, e.g. SQL Server conversion errors quote the literal); the failure audit stores `error = ex.Message` in the WORM log; the short-circuit miss logs the queried key value at `Information`. | Key values / PII persist in immutable audit records and in application logs beyond the retention controls of the source system. | VF exception messages contain only filter name, table and error code; inner exceptions are attached, never concatenated. Failure audit stores `reasonCode = ErrorCode` and a fixed message per code for `GatewaySecurityException`. No key value or queried literal in any `ILogger` call on the VF path. | WI-10 |
| SEC-12 | Medium | `FederatedDuckDbExecutionService.cs:296-318` | HEAD + Design. The linked timeout CTS is created **after** authorization and VF evaluation; the planned planner/key loading (WI-6) sits in the same place. Key loading is bounded only by the per-command timeout, sequentially per filter and table. | 5 tables x 3 filters x `KeyQueryTimeoutSeconds` (10 s) = 150 s per request against a slow key source; request pile-up (OWASP API4), AC-11.4 violated. | Create the linked CTS (`CrossSource.TimeoutSeconds`) before `AuthorizeAllTablesAsync` and pass it to the planner and key provider; cancellation -> `VIRTUAL_FILTER_SOURCE_UNAVAILABLE`, connector not called. | WI-6 |
| SEC-13 | Medium | Design WI-6 (skip-upstream branch); `FederatedStagingService.cs:78-131` | Design. If the `SkipUpstream` branch is placed before the existing per-table guards (`TABLE_DENIED`, `INCOMPLETE_HTTP_RESPONSE`, `AUTH_MODE_UNSUPPORTED`, `CONNECTOR_UNAVAILABLE`) or skips the coverage check of `WithoutEnforcedVirtualFilters`, a Tier 0/1 miss returns 200 where the same query with a hit returns 403. | A misconfigured table (ForwardBearerToken, incomplete responses) is still served for misses; the response class differs between miss and hit. | `SkipUpstream` runs after all existing per-table guards and after `decision.WithoutEnforcedVirtualFilters(...)` succeeded; only the connector call is skipped. | WI-6 |
| SEC-14 | Medium | Design M3 (`session.Items[VirtualFilterEnforcement.SessionItemKey]`) | Design. `session.Items` is a mutable bag shared with connector code. Passing the enforcement object through it exposes the complete authorized key set to every connector (including extension connectors that may log or forward `Items`) and keeps a stringly-typed relaxation channel. | A connector that serializes `session.Items` for diagnostics writes 50,000 authorized keys into its logs. | Pass `VirtualFilterEnforcement` as an explicit parameter (`GovernedRowPolicy.VirtualFilterEnforcement` or a `ReadAsync` argument), never via `session.Items`; enforcement is applied to raw rows independent of `RlsPushdownExecuted`. | WI-4 |
| SEC-15 | Medium | Design M7 / `HttpKeyPushdownCapability`; `DeclarativeHttpDataSourceExecutor.cs:73,497-507,516-521` | Design. Capability resolution does not check that `BatchParamName` survives `BuildUrl` (`IsAllowedArgumentName`, reserved names, `TenantIdQueryParam`, path placeholders), nor that PathLookup keys are path-safe (`/`, `\`, `..` throw `System.Security.SecurityException` mid-flight -> HTTP 500 for every user holding that key) or non-empty. No per-value length bound. | A `PushdownOnly` binding with a reserved parameter name silently fetches everything (operator egress constraint violated without error); one key `A/B` in the authorization table breaks all queries of all users holding it. | `Resolve` returns `None` for unusable parameter names; per-value eligibility: non-empty, length <= 256, no `,` (QueryParameterList), no `/`, `\`, `..` (PathLookup); otherwise `Adaptive` -> Tier 3, `PushdownOnly` -> `VF_PUSHDOWN_UNAVAILABLE`. | WI-8 |
| SEC-16 | Medium | `PushdownUrlFormatter.cs:36,52`; `FederatedDuckDbExecutionService.cs:611-616` | HEAD. Hand-built JSON escapes `"` but not `\` (key `x\", "*` closes the string and injects an element); OData literal typing via `long.TryParse` accepts whitespace/sign; `customArgs["ids"]` is a hard-coded parameter name. On the production path the "JSON body" is sent as a URL query value. | Key-source data that is user-influenced (e.g. imported customer numbers) alters the upstream filter (wildcard element, extra values). | Delete the formatter (WI-8); bodies only via `JsonSerializer`; no hard-coded parameter names. | WI-8 |
| SEC-17 | Medium | `DeclarativeHttpDataSourceExecutor.cs:108,170-180` | HEAD (pre-existing; more relevant because Tier 3 full fetch becomes the default path). The whole upstream body is parsed into a `JsonDocument` before the byte check; `stream.Length` on non-seekable streams; reaching `MaxPages` returns a silently partial result. | A malicious or compromised upstream returns a multi-GB body -> gateway memory exhaustion (OWASP API10/API4). | NEW WI-20: size-limiting `DelegatingHandler` on the federation `HttpClient` that aborts after `MaxStagedBytesPerTable` bytes (`ROW_LIMIT_EXCEEDED`) without touching the 797-line executor. Partial pages remain an under-return (documented, not a leak). | NEW WI-20 |
| SEC-18 | Low | Design M2; `DeclarativeHttpDataSourceExecutor.cs:793` | Design. The executor converts non-`Int64` JSON numbers to `double`. Accepting integral doubles for integer/decimal key columns allows precision collisions above 2^53. | Upstream row id `9007199254740993.0` canonicalizes to `9007199254740992`, which may be an authorized key -> row returned. | Integer columns: accept `double` only if integral and `abs(d) <= 2^53`; decimal columns: accept `double` only if it round-trips exactly; otherwise drop the row. | WI-4 |
| SEC-19 | Low | `FederatedDuckDbExecutionService.cs:362-386`; Design WI-11 | HEAD + Design. Under `danger_bypass_consent_checks` the SID falls back to `"anonymous"`; WI-11 would resolve Virtual Filters for that synthetic SID (no grantee match -> no filter). | Development/test environments with bypass: callers without SID see unfiltered HTTP rows. | WI-11: if any active VF profile exists for the tenant and the caller has no SID -> deny (`VF_ENFORCEMENT_UNAVAILABLE`), even under consent bypass. | WI-11 |
| SEC-20 | Low | Design M1 (`WithoutEnforcedVirtualFilters(enforced)`) | Design. Pair identity is unspecified. `FilterBinding` contains an `IReadOnlyDictionary` (`ColumnMap`), so record equality is reference equality for it; equal bindings from different profiles may compare unequal (false deny), and an enforcement built from a stale snapshot may pass. | Coverage check passes for an enforcement object built from another snapshot generation. | Compare pairs by `(TenantId, Filter.Id, Filter.ComputeDefinitionHash(), Binding.ComputeHash())`; the enforcement records the snapshot generation and is rejected if it differs from the decision's. | WI-3 |
| SEC-21 | Low | `GatewayExceptionHandler.cs:79,86-87`; Design M6 | Design (inference). All `GatewaySecurityException` codes collapse to 403 "Access denied." (good). Tier 1 miss vs hit differ in timing (no upstream call); this reveals only membership in the caller's own key set (acceptable per NFR-2). The invariant that keeps it safe - **unauthorized queried keys are never sent upstream** - is not stated as a test. | A regression that pushes `queried` instead of `queried ∩ authorized` leaks probing to the upstream and makes upstream latency an existence oracle. | Keep `queried ∩ authorized` and test it (ST-25). All VF failures are `GatewaySecurityException` (no 500/501 class differences). | WI-7, WI-8 |
| SEC-22 | Low | `FederatedDuckDbExecutionService.cs:451,504-507`, `FederatedStagingService.cs:221` | HEAD. Failure audit write errors are swallowed without a log line; every record uses a random `TraceId`, so start, source-read and denial records cannot be correlated. | Denials disappear silently when the audit store is degraded. | Log at `Error` when the denial audit fails (original exception still propagates); one correlation id per query on all records. | WI-10 |
| SEC-23 | Low | `VirtualFilterStore.cs` (load path); Design WI-6 | HEAD + Design. DuckDB SQL injection reviewed: identifiers are quoted/escaped by `DuckDbDialectGenerator.FormatIdentifier`, staging names are generated, staged values are bound parameters (`DuckDbOlapEngine.cs:272-290`); no injection found. Residual: `ColumnMap`/`KeyColumns` are validated only at write time; the repository load path does not call `Validate` (the plan's own E2E8 test writes invalid data through it). | Profile written directly to the repository with a crafted `ColumnMap` value or a non-existent target column. | The planner re-validates every identifier it uses (`VirtualFilterNames.ValidateIdentifier`) and that each mapped target column exists in the catalog (`VF_BINDING_MISCONFIGURED`). | WI-6 |

### S.2 Verdict on the target design

**APPROVE WITH CONDITIONS.** The core decision of the Phase-2 design (one mandatory gateway-side semi-join
enforcement at staging, with short-circuit and pushdown reduced to "what is fetched") is the correct zero-trust
architecture and removes the Critical bypass classes of `e7ee288` (SEC-2, SEC-3, SEC-9, query-shape bypasses).
Phase 4 may start with WI-1 immediately. The design is approved only together with the following conditions; each
is binding for Phase 4 and is checked in Phase 5:

1. **C-1 (SEC-1, blocking):** NEW WI-18 is implemented directly after WI-1 and before WI-3/WI-4/WI-6. No code keys
   decisions or VF plans by `ToQualifiedName()`; a missing decision denies.
2. **C-2 (SEC-7, SEC-8):** Target patterns match the domain-qualified `ToString()`; length cap; WI-2 tests corrected.
3. **C-3 (SEC-6):** NEW WI-19 (tenant isolation of the key query) is part of the WI-5 delivery; WI-5 is not done without it.
4. **C-4 (SEC-4, SEC-5, SEC-10):** The key query executes the complete definition in SQL with bound parameters, detects
   overflow by count (`MaxAuthorizedKeys + 1`), and uses a dedicated size-limited cache with `Size` set and single-flight loading.
5. **C-5 (SEC-12, SEC-13):** The timeout CTS covers planning and key loading; `SkipUpstream` runs after all existing
   staging guards and the coverage check.
6. **C-6 (SEC-2, SEC-14):** Enforcement is passed explicitly (not via `session.Items`); relaxing session flags are
   snapshotted before the connector runs and ignored for `HttpDeclarative`.
7. **C-7 (SEC-15, SEC-21):** Pushdown eligibility checks parameter names and per-value safety; only
   `queried ∩ authorized` is ever sent upstream.
8. **C-8 (SEC-11, SEC-22):** No key values or inner exception messages in audit or log payloads; denial audit failures are logged.
9. **C-9 (SEC-17):** NEW WI-20 is delivered before the feature is enabled in any non-development environment, or the
   risk is explicitly accepted by the product owner in the master plan.
10. **C-10:** Every ST criterion below exists as a test and is green; the WI-17 grep guards are extended with
    `ToQualifiedName()` keys of decision/plan maps in `FederatedDuckDbExecutionService`/`CrossSourcePlanner` and with
    `session.Items[` writes of Virtual Filter types.

### S.3 Additional work items

#### WI-18 (C) Domain-qualified decision keys, fail-closed lookup (SEC-1)
- **Order:** directly after WI-1.
- **Files:** `Sql/Services/FederatedDuckDbExecutionService.cs` (`AuthorizeAllTablesAsync` key),
  `Sql/Services/CrossSourcePlanner.cs` (:90, :146-147, and the `targetToStaging`/`targetToAlias` entries keyed by
  `ToQualifiedName()`).
- **Red:** ST-1, ST-2, ST-3.
- **Green:** one key function (`ResolvedSourceTable` index or `Identifier.ToString()` normalized); replace the
  `dec ??= Allowed(...)` fallback by `throw new GatewaySecurityException(..., "VF_ENFORCEMENT_INCOMPLETE")`.
- **Done:** no decision/plan dictionary in the cross-source path is keyed by `ToQualifiedName()`.

#### WI-19 (H) Tenant isolation of the Virtual Filter key query (SEC-6)
- **Order:** delivered together with WI-5.
- **Files:** `VirtualFilters/Federation/StructuredFilterKeyQueryBuilder.cs`, `SqlVirtualFilterKeySetProvider.cs`.
- **Red:** ST-15, ST-16.
- **Green:** `RequireTenantColumnOrThrow` for `From` and each `Join` table; tenant predicate as bound parameter.
- **Done:** the key query for a tenant-column table always contains the tenant predicate; no concatenated tenant value.

#### WI-20 (M) Response size cap for the federation HTTP client (SEC-17)
- **Order:** before enabling the feature outside development (condition C-9); independent of WI-1..WI-15.
- **Files:** new `ResponseSizeLimitingHandler` (`DelegatingHandler`), DI registration on the named client
  `DeclarativeHttpDataSourceExecutor.HttpClientName`. No change to `DeclarativeHttpDataSourceExecutor.cs`.
- **Red:** ST-31.
- **Done:** a response body larger than the configured cap aborts before full buffering.

### S.4 Security test criteria (binding for Phase 4)

Each criterion is one test (or one `[Theory]` row). "Denied(X)" means: a `GatewaySecurityException` (or subtype)
with `ErrorCode == X` reaches the service caller, the API returns HTTP 403 with title "Access denied.", and a
`WEBSQL_QUERY_DENIED` audit record with `reasonCode == X` is written. "Upstream calls" is the counter of the stub
connector / `CountingCrmApiHandler`.

| ID | WI | Test and exact assertion |
|---|---|---|
| ST-1 | WI-18 | Cross-source query joining `crm_api.public.customers` (HTTP, VF applied) and `erp.public.customers` (SQL, no VF): the staging request of the HTTP table carries the HTTP table's own decision (`AppliedVirtualFilterBindings.Count > 0`); the result contains only authorized HTTP rows. |
| ST-2 | WI-18 | ST-1 with the two tables in reverse order in the FROM clause: identical result rows. |
| ST-3 | WI-18 | `CrossSourcePlanner.Plan` with a `decisions` map lacking one table -> throws `GatewaySecurityException` with `ErrorCode == "VF_ENFORCEMENT_INCOMPLETE"`; no `StagingTableRequest` is produced. |
| ST-4 | WI-1 | Stub connector sets `session.Items["VirtualFilterFederationHandled"] = true` and `["RlsPushdownExecuted"] = true` inside `ReadBatchAsync` of an HTTP table whose decision has `CombinedRowFilterSql = "status = 'OPEN'"`: staged rows contain only `OPEN` rows. |
| ST-5 | WI-1 | HTTP table with `MandatoryRowPredicateSql` and no enforcement -> `GatewayForbiddenException` (HTTP 403); zero rows staged. |
| ST-6 | WI-2 | `regex:^crm_api\.public\.customers$` matches `TableIdentifier("crm_api","public","customers")`; `^public\.customers$` does **not** match it; `^crm_api\.public\.cust` does not match it (full match). |
| ST-7 | WI-2 | Stored pattern that fails at match time (invalid regex written via the repository) -> resolver outcome `IsDenied == true`, reason starts with `VF_PATTERN_EVALUATION_FAILED`; WebSQL query -> Denied(`VF_PATTERN_EVALUATION_FAILED`), upstream calls == 0, key provider calls == 0. |
| ST-8 | WI-2 | `FilterBinding.Validate` rejects a 257-character pattern, `^(a)\1$` and `^(?=x).*` with `ArgumentException`; a stored backreference pattern at query time yields HTTP 403 (never 501). |
| ST-9 | WI-2 | Matching uses `NonBacktracking`: pattern `^(a+)+$` against `new string('a', 50_000) + "!"` returns `false` in < 100 ms without exception. |
| ST-10 | WI-3 | Two profiles (grantees `alice`, `bob`) bind the same HTTP table with different filters: Alice's decision contains only Alice's binding; the key provider is called only for Alice's filter. |
| ST-11 | WI-3 | `WithoutEnforcedVirtualFilters` with an enforcement built from another snapshot generation -> `VF_ENFORCEMENT_INCOMPLETE`. |
| ST-12 | WI-4 | Upstream row `{"id": 9007199254740993.0}` with authorized key `9007199254740992` on an integer column -> row dropped; `id` as JSON object/array/bool -> row dropped. |
| ST-13 | WI-4 | After `GovernedConnectorReader.ReadAsync` with enforcement, `session.Items` contains no value of type `VirtualFilterEnforcement` and no authorized key value (enumerate `Items`). |
| ST-14 | WI-5 | Key-set parity: for the `RowFilterChannelParityTests` data (join + `IsNull` + `NotEq` + duplicates + NULL keys) the provider's key set equals `SELECT DISTINCT key FROM target WHERE <SQL-channel predicate>`; a row with `revoked_at IS NOT NULL` is excluded. |
| ST-15 | WI-19 | Key-source table with a tenant column and rows for tenants A and B: provider for tenant A returns only A's keys; the key query text contains a parameter placeholder for the tenant, not the literal. |
| ST-16 | WI-19 | `RequireTenantColumn = true`, key-source table without tenant column and not exempt -> Denied(`VF_KEY_SOURCE_UNSUPPORTED`), upstream calls == 0. |
| ST-17 | WI-5 | `MaxAuthorizedKeys + 1` matching rows -> Denied(`VF_KEY_SET_TOO_LARGE`); exactly `MaxAuthorizedKeys` rows -> success with exactly that many keys (no truncation, no off-by-one). |
| ST-18 | WI-5 | Key source unreachable (non-existent SQLite directory, `Mode=ReadOnly`) -> Denied(`VIRTUAL_FILTER_SOURCE_UNAVAILABLE`), upstream calls == 0; neither the exception message nor the audit `DetailsJson` contains the inner exception message. |
| ST-19 | WI-5 | Provider resolved from production DI (`IMemoryCache` with `SizeLimit`): two sequential calls succeed, the second is a cache hit (no query), the cache entry has `Size > 0`. |
| ST-20 | WI-5 | 20 concurrent calls on a cold cache for the same filter -> the key query runs exactly once; a failed load is not cached (the next call queries again). |
| ST-21 | WI-5 | Reflection: `SqlVirtualFilterKeySetProvider` exposes no public method other than the `IVirtualFilterKeySetProvider` members (no mutator such as `SetInMemoryTuples`). |
| ST-22 | WI-6 | Planner not registered and a decision with applied VF bindings -> Denied(`VF_ENFORCEMENT_UNAVAILABLE`), upstream calls == 0. |
| ST-23 | WI-6 | `CrossSource.TimeoutSeconds = 1`, key-source stub delays 5 s -> the request fails within 2 s with Denied(`VIRTUAL_FILTER_SOURCE_UNAVAILABLE`), upstream calls == 0. |
| ST-24 | WI-6 | Tier 1 miss on an HTTP table with `ForwardBearerToken` (or `CompleteResponse = false`) -> Denied(`AUTH_MODE_UNSUPPORTED` / `INCOMPLETE_HTTP_RESPONSE`), the same status as a hit; never 200. |
| ST-25 | WI-7, WI-8 | `WHERE c.id IN (47, 99)` with only 47 authorized, PathLookup and QueryParameterList variants: recorded upstream URLs/bodies contain `47` and never `99`. |
| ST-26 | WI-7 | Tier 1 miss vs. authorized-but-non-existent key: identical HTTP status, identical column names and types, 0 rows each; `SELECT COUNT(*)` returns one row with `0` in both cases. |
| ST-27 | WI-6 | UNION, CTE, `IN (SELECT ...)`, `EXISTS`, self-join and LATERAL over the protected HTTP table (upstream in `IgnoreFilters` mode) -> only authorized rows in every shape; shapes DuckDB federation rejects return the existing policy error, never unfiltered rows. |
| ST-28 | WI-8 | `BatchParamName` equal to `TenantIdQueryParam` (or malformed) with `PushdownOnly` -> Denied(`VF_PUSHDOWN_UNAVAILABLE`), upstream calls == 0; with `Adaptive` -> Tier 3, only authorized rows. |
| ST-29 | WI-8 | Authorized keys `A/B`, `..` and `""` on a PathLookup table: no upstream request contains them; `Adaptive` -> Tier 3 with only authorized rows; `PushdownOnly` -> Denied(`VF_PUSHDOWN_UNAVAILABLE`); no `System.Security.SecurityException` and no HTTP 500. |
| ST-30 | WI-8 | JsonBodyArray with keys `x\", "*` and `a,b`: the request body deserializes (`JsonSerializer`) to exactly the authorized string array; a key containing `,` disables QueryParameterList pushdown. |
| ST-31 | WI-20 | Upstream stub streams a body larger than the configured cap -> Denied(`ROW_LIMIT_EXCEEDED`); the handler read at most cap + one buffer of bytes. |
| ST-32 | WI-8 | PathLookup with more than `MaxPathLookupRequests` keys -> `Adaptive`: exactly 1 upstream request (Tier 3 full fetch); `PushdownOnly`: Denied(`VF_PUSHDOWN_LIMIT_EXCEEDED`), upstream calls == 0. |
| ST-33 | WI-10 | For Tier 0, Tier 1 miss, Tier 2 and Tier 3: the `DetailsJson` of all audit records (start, key-source read, source read, denial) contains none of the authorized key values nor the queried literal; captured `ILogger` output contains none of them either. |
| ST-34 | WI-10 | All audit records of one query share one correlation/trace id; a failing denial-audit write -> the original `GatewaySecurityException` still propagates and exactly one `Error` log entry is written. |
| ST-35 | WI-10 | `[Theory]` over all `VirtualFilterErrorCodes`: raised through the pipeline -> audited as `WEBSQL_QUERY_DENIED` with `reasonCode` equal to the code, HTTP 403. |
| ST-36 | WI-11 | Consent bypass on, caller without SID, tenant has an active VF profile -> Denied(`VF_ENFORCEMENT_UNAVAILABLE`); caller with SID -> VF applied (only authorized rows). |
| ST-37 | WI-6 | Profile written directly to the repository with `ColumnMap` target `id; DROP` or a column missing on the HTTP table -> Denied(`VF_BINDING_MISCONFIGURED`), upstream calls == 0. |
| ST-38 | WI-4 | Key column denied to the caller (`ColumnAccessLevel.Deny`) plus a masked column: enforcement uses the raw key value; returned rows do not contain the denied column; the masked column is masked after enforcement. |

Cross-reference: the Phase-2 tests listed in WI-1..WI-15 remain required; ST-n criteria are additional, and where an
ST-n names the same scenario as a Phase-2 test, the stricter assertion applies.

### Implementation Log

**Phase 4, batch A** (`csharp-tdd-developer`, 2026-10-10): WI-1, WI-18, WI-2.

| WI | Status | Commit | Tests (ST) |
|---|---|---|---|
| WI-1 | Done | `5390057` | `FederatedStagingRowFilterRegressionTests` (ST-4, ST-5, planner sets no suppression item) |
| WI-18 | Done | `b02bb4f` | `FederatedDecisionKeyTests` (ST-1, ST-2 at service level with real policy, staging and DuckDB), `CrossSourcePlannerTests` (ST-1, ST-3) |
| WI-2 | Done | `fa3e12b` | `TargetPatternMatcherTests` (ST-6, ST-8, ST-9), `MandatoryRowFilterResolverCrossSourceTests` (ST-6..ST-8), `FederatedPatternEvaluationTests` (ST-7, ST-8 service level) |

Shared test helpers: `VirtualFilters/Federation/FederatedVfPipelineHarness.cs` (tables, counting connector stub, real
staging) and `FederatedServiceHarness.cs` (real consent policy, resolver, router, planner, staging, DuckDB).

Deviations and refinements (all in the fail-closed direction):

- WI-1 / SEC-2: `GovernedConnectorReader.ReadAsync` ignores `RlsPushdownExecuted` and `InDbColumnMaskingExecuted` for
  `HttpDeclarative` tables (no HTTP connector sets them legitimately); flags of other connector types are unchanged.
  The explicit enforcement parameter of C-6 follows with WI-4.
- WI-18: the decision map is typed `IReadOnlyDictionary<TableIdentifier, TableAccessDecision>` (compile-time guard
  instead of a normalized string); `CrossSourcePlanner.DecisionKey` / `RequireDecision` are the single key function and
  lookup. A self-joined table is authorized once. Pushdown arguments and `VirtualFilterJoinSpec` (both deleted in WI-6)
  use the same key; a join spec for a table not in the query denies (`VF_ENFORCEMENT_INCOMPLETE`) instead of falling
  back to `s0`. Short SQL names (`schema.table`, `table`) are mapped for staging/alias rewriting only when exactly one
  table carries them. Not changed: the rewriter's `?? "s0"` fallback for unknown table names (CTE names); left to
  WI-6 / ST-27.
- WI-2: the regex body must also parse on its own, so a pattern like `x)|(?:.*` cannot close the anchoring group. A
  regex binding pattern now means a full-name match for same-source bindings too (before, it was parsed as a glob and
  never matched). An invalid stored glob pattern now denies instead of being retried as a regex. The pattern deny is
  carried as reason prefix `VF_PATTERN_EVALUATION_FAILED:` and raised as `GatewaySecurityException` with that code by
  `FederatedDuckDbExecutionService`; its failure audit now classifies `GatewaySecurityException` as
  `WEBSQL_QUERY_DENIED` with `reasonCode = ErrorCode` (the message/payload hardening of SEC-11 stays in WI-10).
  `VirtualFilterErrorCodes` was introduced with the two codes used so far. `Autheris.Domain` got
  `InternalsVisibleTo(Autheris.Tests.Unit)` for the timeout-propagation test.

Verification: `dotnet build Autheris.sln` 0 warnings, 0 errors; unit tests 3945 passed, 1 failed (pre-existing
`WormConfigurationAuditServiceTests.AuditConfigurationSnapshotAsync_MintsWormRecordOnStartup_AndSuppressesDuplicates`,
PLAN-GOV-KI-13). Integration (filter `Federat|WebSql|CrossSource|VirtualFilter|RowFilterChannelParity`): 61 passed,
3 failed; the 3 failures (`*ChannelParityTests` channel `sql-endpoint`, "Declarative SQL endpoint 'air1_ids' is not
registered") fail identically on the baseline `fb5f3f5` and are unrelated.
