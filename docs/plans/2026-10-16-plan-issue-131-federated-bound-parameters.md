# Implementation Plan: REQ-FED-01 section 7.2 (#131) - Bound Parameters in Federated (Cross-Source) WebSQL Queries (`WP-FED-15`)

**Document ID:** `PLAN-FED-131` (revision 1, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [PRD REQ-FED-01](2026-10-16-req-cross-source-all-consumers.md) section 7.2 (FR-35..FR-38, AC-23..AC-26, Q-14, Q-15; owner decision Q-14 equals the #128 limit); FR-39..FR-45, AC-27..AC-31 and Q-16..Q-20 belong to #132 (`WP-FED-16`) and are **out of scope**
**Branch:** to be created from the `main` that contains the previous delivery item (delivery step I-26)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md); requirement [REQ-FED-01](2026-10-16-req-cross-source-all-consumers.md) section 7.2; architecture plan [PLAN-FED-01](2026-10-16-plan-cross-source-all-consumers.md) sections 4.11, 4.12.1, 12.7 (`WP-FED-15`, `SEC-FED-35..37`); delivery tracker [PLAN-DELIVERY-W23](2026-10-16-plan-waves-2-3-delivery.md) (planning step P-2, implementation step I-26); list contract [PLAN-SQL-128](2026-10-16-plan-issue-128-list-parameters.md)
**Status:** PROPOSED (Phase 2 complete; next: Phase 3 security review, step S-2, then Phase 4 after gate G-PLAN)

**Binding project rules applied:** values are bound, never spliced, in DuckDB and in every source statement; fail closed with one stable code per cause, the same code as the single-source path (no new federation-only parameter code); no parameter value, list element, filter literal or key in logs, audit, spans, exception text or error bodies; authorization of every table precedes every parameter-dependent error (no oracle, D-8); no schema or DDL change, no new option; least privilege on sources (SELECT only); the SqlGate/AST security suites and the G8 allocation gate stay green; Phase-4 TDD developer is one Sonnet agent, sequential (gates G-ONE, G-BUILD).

---

## 1. Verification of the PRD and of PLAN-FED-01 4.12 Against the Code (`d4517660`, waves 1 merged)

| Claim | Result |
|---|---|
| "The planner compiles the DuckDB statement with `ParameterSource.Empty`, request parameters are not carried" | **Confirmed, but the real failure is earlier and different.** `CrossSourcePlanner.Plan` (`src/Autheris.Application/Sql/Services/CrossSourcePlanner.cs:92-99`) generates the DuckDB text with `generator.Generate(rewrittenAst, ParameterSource.Empty)` (`:280-282`). `FederatedDuckDbExecutionService.ExecuteFederatedInternalAsync` (`src/Autheris.Application/Sql/Services/FederatedDuckDbExecutionService.cs:233`) never reads `request.Parameters` (grep: no reference in the file) and passes the **raw** `request.Sql` to `Analyze` (`:254`), `ParseStatement` (`:312`) and `Plan` (`:314`). The raw text still contains `@name`; the single-source path rewrites it to `__param_name` first (`GovernedSqlExecutor.cs:261`, `GovernedSqlExecutionService.NormalizeClientParameters`, `GovernedSqlExecutionService.cs:369-436`, `ClientParameterPlaceholderPrefix` `:40`). `SqlBase.g4` has no `@` expression token (only `QUESTION_MARK`, `:641`, `:1473`), and `SqlAstBuilder.VisitColumnReference` only turns `__param_*` identifiers into `ParameterReference` (`src/TrinoSqlEngine/Ast/Builder/SqlAstBuilder.cs:1058-1064`). A parameterised federated statement therefore fails at analysis (syntax error) before routing, not "because values are missing". No dedicated rejection or stable code exists. (Syntax-error behaviour is derived from the grammar; the first red test pins it.) `docs/features/f-dialect-02-ast-sql-compiler.md:436` ("such a statement fails closed") is correct in effect, wrong in mechanism. |
| Parameter values would reach DuckDB if the planner carried them | **Refuted: a latent silent-NULL defect.** `DuckDbOlapEngine.ExecuteCoreAsync` binds `new DuckDBParameter(parameter.Name, parameter.Value ?? DBNull.Value)` (`src/Autheris.Application/Olap/DuckDbOlapEngine.cs:190-195`). A `ParameterOrigin.ClientNamed` slot has `Value == null` by contract (resolved per request by the binder, `src/TrinoSqlEngine/Ast/Emit/CompiledSql.cs:20-33`, `ICompiledSqlBinder.cs:196-207`). The engine does **not** use the binder (`DuckDbCompiledSqlBinder`, `ICompiledSqlBinder.cs:370-374`, reachable through `CompiledSqlBinders.For(TargetSqlDialect.DuckDb)`, `CompiledSqlBinders.cs:28-30`). Merely emitting client slots (`SqlEmitterContext.BindClient`, `src/TrinoSqlEngine/Ast/Buffer/SqlEmitterContext.cs:158-172`) without a resolver would turn every parameter into `NULL` and return wrong rows without an error (`x = NULL` is never true). `ExecuteGeneratedAsync` is called only from `FederatedDuckDbExecutionService.cs:339-343` (pinned by `tests/Autheris.Tests.Architecture/OlapEntryPointArchitectureTests.cs:57`). |
| FR-35 "the planner receives the validated client values" | Needs a carrier that does not exist: `ICrossSourcePlanner.Plan` (`CrossSourcePlanner.cs:31-41`) has no parameter argument; `CrossSourcePlan` (`:22-29`) exposes only `Compiled`, staging requests and names. |
| Federated list parameters | `SQL_LIST_PARAM_UNSUPPORTED` is raised by the #128 guard, which does not exist yet; the three federated re-dispatch points are `GovernedSqlExecutionService.cs:110-116` (`ValidateAsync`, no parameters), `:131-137` and `:148-154`. The federated service would otherwise ignore a list silently (A-9 of PLAN-SQL-128). The AST list expander of #128 is `internal` to the engine and runs inside `GovernedSqlCompiler.CompileCore` (PLAN-SQL-128 A-1); the federated planner does **not** call `CompileCore`, it calls the DuckDB generator directly (`CrossSourcePlanner.cs:280`). A public engine entry for the expander is therefore a new requirement (A-2). |
| The reserved-name rules of the single-source executor protect the federated path too | **Refuted.** The checks for `__gql_`, `p_rls_`, `rls_` names live in `GovernedSqlExecutor.cs:245-256` and are not run by the federated service; only `BindClient` refuses `p[0-9]`, `__`, `gql_`, `autheris` prefixes (`SqlEmitterContext.cs:25-31`, `:166`). `NormalizeClientParameters` additionally refuses SQL that contains the literal prefix `__param_` (`GovernedSqlExecutionService.cs:377-380`). The federated path needs the same pre-checks (A-1). |
| Pushdown (#122) is available to receive parameter values | **Not yet.** `StagingTableRequest.PushdownFilter` is always `null` (`CrossSourcePlanner.cs:260-267`); `FederatedStagingService` already honours a non-null filter for SQL tables (`src/Autheris.Application/Olap/FederatedStagingService.cs:164-167`, audit flag `filterPushedDown` `:232`); `Sql/Federation/` (`ConjunctSplitter`, `PushedClauseBuilder`, `PushabilityEvaluator`, `KeySetPlanner`, `SubtreeExtractor`) **does not exist** in `src/` (grep: zero hits; INC-6 is not started). Wave 1 (PRs #139, #140) did not touch any of these files' parameter behaviour. |
| A pushed clause reaches the source as bound values | **Confirmed.** `TableFilterClause(SqlPredicate, Parameters, ReferencedColumns)` with a mandatory `TrinoSqlFactory` (`src/Autheris.Application/Interfaces/IGatewayExecutionService.cs:62-125`); `SqlTableReadExecutor.BuildFilter` ANDs it with arguments `@p_arg_n` and refuses a duplicate parameter name (`src/Autheris.Application/Services/SqlTableReadExecutor.cs:296-362`, `:355`); the read runs as a planned table read (`:102`, `PlannedTableRead: true`) whose parameters are scalars only (PLAN-SQL-128 A-4: planned reads keep `Normalize`, collections refused). A pushed list must therefore be expanded by the pushdown builder into **N scalar** parameters, never handed over as a `ClientListValue` (A-4). |
| Where today's audit could leak a parameter value | **Finding.** The start record stores the anonymised SQL and hash only (`FederatedDuckDbExecutionService.cs:520-535`; no parameters). The failure record stores `error = ex.Message` for every exception that is not a `GatewaySecurityException` (`:628-631`). Once values are bound, a DuckDB conversion error (for example comparing a bound string with an integer staged column) quotes the value (`Conversion Error: Could not convert string '...'`), and the message would land in the audit. The per-source-read record has no parameter fields (`FederatedStagingService.cs:223-233`). No code path records parameter values today; the "values as configured for audit today" of the issue matches **no existing option** (Q-15, A-6). |
| Transports that can carry parameters | `WebSqlRequestDto.Parameters` (`src/Autheris.Api/Endpoints/WebSqlEndpoints.cs:23-25`, `WebSqlEndpoints.Execution.cs:81`, `:122`: transports `WebSql` and `Trino`, both in the federation default list, `RestApiOptions.cs` `DefaultAllowedTransports`) and declared SQL endpoints (`SqlEndpointExecutionService.cs:79-90`, transport `SqlEndpoints`, **not** in the default list, operator opt-in). Arrow export (`ArrowExportEndpoints.cs:187`), Flight SQL (`ArrowFlightSqlServer.cs:198`), MCP SQL (`ApiDispatcherService.cs:177`, `McpToolExecutionHandler.cs:107`) and async jobs (`AsyncQueryJobBackgroundWorker.cs:242`) build `GovernedSqlQueryRequest` **without** `Parameters` (PLAN-SQL-128 section 1, row "Other entry points"). PRD AC-23 names "REST, WebSQL, async job, Flight SQL"; async job and Flight SQL have no parameter channel today (owner flag F-1). |
| Wave 1 overlap | PR #140 (OLAP gate, `Olap` transport) and PR #139 (GraphQL schema gating) changed none of the files in section 4 of this plan beyond what `PLAN-DELIVERY-W23` section 2 already records. The inline transport gate is still at `FederatedDuckDbExecutionService.cs:245-252` (`WP-FED-01` extraction is step I-9, before this item). |
| Scalar carrying is `WP-FED-03` (step I-10) | PLAN-FED-01 4.3 assigns "`Plan` receives `request.Parameters` and compiles with a `ParameterSource` that carries them" to `WP-FED-03`. As the table shows, a `ParameterSource` carrier alone is **insufficient** (normalisation before analysis, binder resolution, reserved names). This plan states the required end state (A-1..A-3); if I-10 has landed a thinner carrier, `WP-1` below reshapes it into the contract and the tests are written against behaviour so they hold either way. The delta plan (P-10) should reference this plan from `WP-FED-03`. |

No PRD veto is reverted. Refinements: (1) the failure mode of today is a syntax error plus a latent silent-NULL hazard, not "missing values"; (2) the engine, not only the planner, needs a change (binder path, A-3); (3) `FEDERATION_PARAMETER_INVALID` is **not** introduced (A-7); (4) Q-15 resolves to "names and counts, no values anywhere" because no value-recording option exists (A-6).

---

## 2. Architecture Decisions

### A-1 One contract object, built in the executor, normalisation before analysis, validation after authorization
`FederatedParameterContract` (new, `src/Autheris.Application/Federation/`) is the single carrier of client values on the federated path. `ExecuteFederatedInternalAsync` uses it in this order (existing steps keep their order; new steps in bold):

1. Transport gate (`:245-252`, unchanged).
2. **Name pre-check** `GovernedSqlExecutionService.EnsureClientParameterNames(parameters)` (new internal static, extracted from `GovernedSqlExecutor.cs:245-256` so both paths share one rule: names starting `__gql_`, `p_rls_`, `rls_` are refused with `WebSqlPolicyException`). Names only, no value looked at.
3. **Text normalisation** `sqlForAnalysis = GovernedSqlExecutionService.NormalizeClientParameters(request.Sql, request.Parameters, out _)` (existing internal static, same assembly; it also refuses SQL containing `__param_`). `Analyze` (`:254`), function policy (`:262-272`), routing (`:275`), `ParseStatement` (`:312`) and `Plan` use `sqlForAnalysis`; audit records and `GovernedSqlResult.OriginalSql` keep the original `request.Sql` (it contains `@name` markers and no values). An unsupplied `@x` is not rewritten and fails as a syntax error exactly like the single-source path (parity, test 8).
4. Routing, `MaxTableCount`, **authorization of all tables** (`:308`), unchanged.
5. **`FederatedParameterContract.Create(request.Parameters, ListParameterLimits.From(options.WebSql))`** immediately after authorization and **before** `PlanVirtualFiltersAsync` (`:313`, which may call HTTP key sources) and before `Plan`: value normalisation through `WebSqlParameterValues.NormalizeParameter` (#128: scalars via `Normalize`, arrays via `ListParameterNormalizer`), name validation, `MaxListParameters`, the configured list maximum, case-insensitive de-duplication of `x` and `@x` (first spelling wins, as `BuildClientValues`, `GovernedSqlExecutor.cs:656-671`). Pure, no I/O. A violation throws the #128 codes (`ClientListException` with `SQL_LIST_PARAM_*`, 400) or the existing scalar errors (`ArgumentException` for an object value), inside the existing `try` so the failure audit runs; zero source reads, zero key-source reads.
6. `Plan(..., parameters: contract)` (A-2, A-4) and the rest unchanged.

Rationale for step 5 after step 4: PRD 7.2.4 "no new oracle" and D-8 require that a caller who may not read a table gets the same denial whatever the parameters are. Shape errors found at step 2/3 depend only on parameter names and the SQL text the caller wrote (the same information the pre-authorization `Analyze` already evaluates).

Rejected: validating before routing (would let a parameter error mask a `FEDERATION_*` denial); validating inside `Plan` (planning is also used in analysis mode without values, A-8; two places would drift); a second normaliser (the #128 normaliser is the single value contract, A-10 of PLAN-SQL-128).

### A-2 The DuckDB statement: AST expansion through a public engine entry, `Generate` unchanged
`Plan` gets an optional last parameter `FederatedParameterContract? parameters = null` (`ICrossSourcePlanner.Plan`, `CrossSourcePlanner.cs:31-41`, `:79-99`; the 5-argument overload at `:79-85` stays and passes `null`). After the `FederationAstRewriter` pass (`:277-278`) and before `Generate` (`:282`):

`rewrittenAst = ClientListExpansion.Apply(rewrittenAst, parameters.ListShapes)`

`ClientListExpansion` is a new **public static facade** in the engine (`src/TrinoSqlEngine/Ast/Visitors/ClientListExpansion.cs`) over the `internal` `ClientListExpander` of PLAN-SQL-128 (same rules: sole `IN`/`NOT IN` item becomes indexed references, empty list becomes `1 = 0` / `1 = 1`, every other use of a list name is refused with `ClientListException(SQL_LIST_PARAM_POSITION)`). No second implementation of the position proof exists. `generator.Generate(expanded, ParameterSource.Empty)` stays: client slots carry the name, the list index and count (PLAN-SQL-128 A-2), the values are resolved at bind time (A-3). The `GovernedSqlCompiler.cs:207` expansion-factor budget does not apply here (the federated planner is not the governed compiler); the DuckDB bind budget (65,535 total, `SqlEmitterContext.Bind`, `:187-190`) and the list maximum (`ListParameterLimits.EffectiveMax(configured, DuckDb capabilities)` = the configured maximum, default 1000) bound the statement. Owner decision Q-14: the DuckDB side uses exactly the #128 limit, no second option.

Scalar parameters need no AST work: `__param_x` is already a synthetic `ParameterReference` and `BindClient` emits one `$n` slot per distinct name (dedup key `Client`, name).

### A-3 Binding in DuckDB: the engine's own binder, never `Value ?? DBNull`
`GeneratedOlapQuery` (`src/Autheris.Application/Olap/IDuckDbOlapEngine.cs:32`) gets two appended optional members, `CompiledSql? Compiled = null` and `IReadOnlyDictionary<string, object?>? ClientValues = null`; `OlapQueryRequest` (`:18-24`) gets the same two. In `DuckDbOlapEngine.ExecuteCoreAsync` (`:190-195`):

- If `Compiled` is set: `CompiledSqlBinders.For(TargetSqlDialect.DuckDb).Bind(queryCmd, compiled, clientValues)` (public API; sets `CommandText`, adds typed parameters by name, resolves `ClientNamed` slots by `SourceName`, indexed list elements and exact counts as PLAN-SQL-128 A-3, `MissingClientParameterException` for an absent value, embedded-NUL refusal). One binder, one rule set (INV-13), no hand-written parameter loop for client values.
- If `Compiled` is not set (callers that pass a plain `BoundParameter` list, i.e. `ExecuteOlapQueryAsync`): the existing loop, **plus a fail-closed refusal of any `ClientNamed` slot** (it can only have `Value == null`, which would bind as `NULL`). This closes the latent silent-NULL hole independently of #131.

`DuckDbCompiledSqlBinder` reads the client value type through `InferType` (Int32/Int64/Decimal/Double/Boolean/String/dates; `Guid` is added to it by PLAN-SQL-128 A-3, which declared endpoints with `uuid` parameters need). The executor passes `plan.Compiled` and `contract.ClientValues` (`FederatedDuckDbExecutionService.cs:339-343`). `ValidateUserSql(..., request.Parameters?.Count, ...)` (`DuckDbOlapEngine.cs:107`) keeps receiving `Compiled.Parameters.Length` through `Parameters`.

Alternative rejected: filling `BoundParameter.Value` in the application (`slot with { Value = v }`) and keeping the engine loop: duplicates the exact-count, type and NUL rules of the binder in a second place.

### A-4 Pushed parameters (needs `WP-FED-11` and `WP-FED-12`): the contract supplies typed values, the builder stays the single decider
`PushedClauseBuilder` (WP-FED-11, `src/Autheris.Application/Sql/Federation/Optimization/`) is the only place that decides pushability and emits `TableFilterClause`. It receives the contract (through `Plan`) and calls two methods; it never reads raw request values:

- `TryGetScalar(name, columnClass, out object value)` and `TryGetList(name, columnClass, maxElements, out IReadOnlyList<object> values)`.
- **Same value (FR-37, SEC-FED-36).** Both return the **same objects** that the DuckDB bind resolves from `ClientValues` (one normalised dictionary, no second conversion). For a list, the same `ClientListValue` elements in the same order after the same de-duplication.
- **Type class (strict first step, R-17).** The column class comes from the catalog type via the `PushabilityMatrix` type classes of WP-FED-11 (the developer reuses its enum). Convertibility without loss, by CLR type of the normalised value:

| Column class | Scalar CLR types accepted | List elements |
|---|---|---|
| integer | `sbyte byte short ushort int uint long` | all elements of those types |
| decimal | the integer types and `decimal` | all elements integer or `decimal` |
| boolean | `bool` | all `bool` |
| string | `string` | all `string` |
| date, timestamp, GUID, float/double, binary, spatial, JSON, array, unknown | none: **not pushed** | none |

  A `double` value, a string for a numeric/date/GUID column, a `null` scalar and an empty list are **not pushed** (DuckDB filters alone; the superset rule holds, SEC-FED-21). Date and GUID columns with string parameters are a deliberate follow-up (the DuckDB side compares the staged column with the bound value under DuckDB's own cast rules; proving equal behaviour in the source needs a DuckDB probe, see risk R-3).
- **Operators and lists.** Which operators are pushable per class and dialect remains the matrix of PLAN-FED-01 4.11.2 (for example `<>` and `NOT IN` on strings not on SQL Server/Oracle/Databricks). The contract adds nothing to that matrix. Client lists cannot contain `NULL` (V-128-4), so the "`NOT IN` with a `NULL` element is not pushed" rule never triggers for client lists.
- **Emission.** A pushed list becomes `column IN (@p_pd_<c>_0, ... , @p_pd_<c>_n-1)` with `n` **scalar** parameters named from the builder's counter `<c>` (distinct from the executor's `@p_arg_n`, `SqlTableReadExecutor.cs:317`, and from policy names); the values are in `TableFilterClause.Parameters`, never in the text; the dialect text comes from the `TrinoSqlFactory`. The read is a planned table read, so no `ClientListValue` ever travels to a source (PLAN-SQL-128 A-4 refuses collections there).
- **Budget (Q-14).** A list is pushed only if `count <= ListParameterLimits.EffectiveMax(configured, sourceCapabilities)` of the source dialect, i.e. `min(WebSql:MaxListParameterValues, MaxBindParameters - 100, MaxInListItems)` (SQL Server 2,000 hard cap, Oracle 1,000, SQLite and PostgreSQL by the configured value). The `-100` reserve covers tenant, policy, mask and `p_arg` binds exactly as in #128. The budget is **per staging input**: pushed list elements of all conjuncts of one input are summed in conjunct order; a conjunct that would exceed it is not pushed (deterministic, never partial, never truncated). No separate pushed-list option is introduced (owner decision Q-14).
- **Collisions.** Client names never appear in a source statement (only their values, under planner-generated `p_pd_` names), so a client name cannot collide with `p_pd_*`, `p_arg_*`, `p_rls_*` or staging names `s0..`; inside the DuckDB statement client slots are `$n` markers. A duplicate name inside the combined clause already fails closed (`SqlTableReadExecutor.cs:355`).
- **Not an import.** A client value pushed to a table of a source the caller is authorized for is the caller's own literal; it is not data of source A reaching source B, so `allow_key_set_import` (Q-8, WP-FED-14) does not apply and is not consulted. Key-set batches (`p_ks_*`) and client parameters never mix.

### A-5 Subtrees (needs `WP-FED-13`)
A same-source subtree is compiled and executed through the governed single-source path (PLAN-FED-01 4.11.5). The subtree executor receives only the parameters its statement references: `contract.ToRequestParameters(referencedNames)` returns the **already normalised** values (scalars and `ClientListValue`). `WebSqlParameterValues.NormalizeParameter` and `ListParameterNormalizer.IsListCandidate` (#128) must accept an already normalised `ClientListValue` idempotently (re-check of the limits only); `WP-4` makes that small edit in the #128 files (merged by then). The source compile then expands and binds the list through the governed compiler and binder (PLAN-FED-01 4.12.1, "bound by the source's binder, not spliced"); no federation-specific binding code exists for subtrees.

### A-6 Audit and logs (Q-15 resolved)
Defaults taken: **names and counts everywhere, values nowhere.** Q-15's "values only in the existing single-source-equivalent query record when that option is on" is empty today (no option records values; section 1), so no value-audit option is introduced. If the owner later wants one, it belongs to the single-source record first and is a separate item.

- Start record (`RecordStartAuditAsync`, `:505-548`, event `WebSqlCrossSourceQuery`): new fields `parameters` = ordinal-sorted list of `{ name, kind: "scalar" | "list", count }` (`count` only for lists, the de-duplicated element count the caller already knows) and `bindCount` (`plan.Compiled.Parameters.Length`). Only names that match the client-name pattern of `SqlEmitterContext` (`^[A-Za-z_][A-Za-z0-9_]{0,127}$`) are recorded; a non-conforming name is counted in `invalidParameterNames` and never written (no log injection, bounded size).
- Failure record (`RecordFailureAuditAsync`, `:600-646`): same `parameters` field when the contract exists. **`error` no longer stores `ex.Message` for requests that carry parameters** (`:628-631`): it stores the exception type name only, because DuckDB and source messages can quote bound values. `reasonCode` logic is unchanged.
- Per-source-read record (`FederatedStagingService.cs:223-233`, event `WebSqlCrossSourceSourceRead`): when a predicate was pushed, `parameterNames` (distinct, sorted names feeding pushed predicates) and `listParameterCounts` (`{ name: pushed element count }`). This supersedes the statement "no parameter names beyond the count" of PLAN-FED-01 4.11.6 for pushed predicates, as PRD FR-38 requires; values never appear. Carried by an appended optional `PushedParameterAudit? ParameterAudit = null` on `StagingTableRequest` (`IFederatedStagingService.cs:13-20`).
- Logs: no new log line carries a value; `BoundParameter.ToString`, `ClientListValue.ToString` are value-free; `LogGeneratedSql`-style debug output prints markers only.

### A-7 Errors: same codes as the single-source path
`FEDERATION_PARAMETER_INVALID` (PRD placeholder) is **not** introduced. The federated path answers the same stable code for the same caller mistake as the single-source path: `SQL_LIST_PARAM_TYPE|NULL|TOO_LARGE|POSITION` (`ClientListException`, 400, mapper row of PLAN-SQL-128 A-7), the existing `WebSqlPolicyException` for reserved names, `ArgumentException` for non-scalar objects, `MissingClientParameterException` for an absent value. Reason: a caller must get identical answers whether or not the statement happens to be cross-source, and the `FEDERATION_*` codes of `WP-FED-02` describe federation-specific causes only. `SQL_LIST_PARAM_UNSUPPORTED` loses its only producer and is **removed** (constant, `FixedMessage` row, mapper test row, `ClientListParameters.RejectForFederation` and its call sites at `GovernedSqlExecutionService.cs:131-137` and `:148-154`).

DuckDB-stage failures while values are bound (for example a conversion error) are caught around `ExecuteGeneratedAsync` (`:339-343`) when `contract.HasValues`, logged with the exception **type** only, and rethrown as the stable federation error with a fixed message that `WP-FED-02` defines for upstream failures (`FEDERATION_SOURCE_ERROR`; the P-10 delta plan confirms the name); cancellation and `GatewaySecurityException` pass through unchanged. Source-stage errors on pushed predicates already map to that code through `WP-FED-02`/`WP-FED-03`.

### A-8 Query analysis (#79, `WP-FED-12` region)
`FederatedParameterContract.ForAnalysis(names)` builds a value-less contract (names from `NormalizeForAst`/`SqlParameterExtractor`, `QueryAnalysisService.Sql.cs:58`). `ICrossSourcePlanner.Plan` in analysis mode then reports a parameter-fed conjunct as `PUSHED_IF_TYPE_MATCHES` with its feeding parameter name (new reason code in the WP-FED-12 list, 4.11.7), because pushability depends on the runtime value type. Values are never repeated; analysis and execution run the same `Plan`.

### A-9 What works when (dependency map)

| State of `main` | Scalar `@x` | List `@ids` | Pushdown of parameters |
|---|---|---|---|
| Today | fails at analysis (syntax error) | fails at analysis | none |
| After I-5 (#128) only | same | `SQL_LIST_PARAM_UNSUPPORTED` (guard) | none |
| After I-10 (`WP-FED-03`) | per its thinner carrier; must satisfy A-1..A-3 or `WP-1` reshapes it | refused or unsupported | none |
| After `WP-1`, `WP-2` of this plan (no INC-6 needed) | works, validated, audited, bound in DuckDB | works (DuckDB `IN` expansion), guard removed | none: `PushdownFilter` is `null`, every table is staged whole (`CrossSourcePlanner.cs:266`); the statement is correct but a large table still hits `ROW_LIMIT_EXCEEDED` exactly like a parameter-free statement |
| After I-14/I-15 and `WP-3`, `WP-4` | as above | as above | pushed per A-4 with audit and analysis; result identical to pushdown off |

`WP-1` and `WP-2` have **no code dependency on INC-6** (they need #128 and a compiling `main`); `WP-3` needs `WP-FED-11` (I-14), `WP-4` needs `WP-FED-12` (I-15) and, for the subtree hand-off, `WP-FED-13` (I-17). The tracker already orders I-26 after I-14, I-15 and I-17, so all packages land in step I-26; the split only fixes the internal order and lets the owner ship `WP-1`+`WP-2` alone if INC-6 is delayed.

---

## 3. Contracts (fixed up front, first commit "contracts")

```csharp
// src/Autheris.Application/Federation/FederatedParameterContract.cs (new)
public sealed class FederatedParameterContract
{
    public static FederatedParameterContract Empty { get; }
    // Normalises, validates (names, types, bounds) and de-duplicates; pure, no I/O.
    // Throws ClientListException (SQL_LIST_PARAM_*), ArgumentException, WebSqlPolicyException (reserved names).
    public static FederatedParameterContract Create(IReadOnlyDictionary<string, object?>? requestParameters, ListParameterLimits limits);
    public static FederatedParameterContract ForAnalysis(IEnumerable<string> names);   // names only, HasValues == false

    public bool HasValues { get; }
    public IReadOnlyList<string> Names { get; }                         // ordinal-sorted, '@' trimmed, pattern-valid only
    public ClientListShapes ListShapes { get; }                         // PLAN-SQL-128 type
    public IReadOnlyDictionary<string, object?> ClientValues { get; }   // case-insensitive; scalar CLR value or ClientListValue

    public bool TryGetScalar(string name, ColumnTypeClass columnClass, out object value);
    public bool TryGetList(string name, ColumnTypeClass columnClass, int maxElements, out IReadOnlyList<object> values);
    public IReadOnlyDictionary<string, object?> ToRequestParameters(IEnumerable<string> referencedNames);   // subtree hand-off, normalised values
    public FederatedParameterAudit ToAudit();                           // names, kinds, list counts, invalidParameterNames; no values
}
public sealed record FederatedParameterAudit(IReadOnlyList<FederatedParameterAuditEntry> Parameters, int InvalidParameterNames);
public sealed record FederatedParameterAuditEntry(string Name, string Kind, int? Count);
public sealed record PushedParameterAudit(IReadOnlyList<string> ParameterNames, IReadOnlyDictionary<string, int> ListParameterCounts);
// ColumnTypeClass: the type-class enum of WP-FED-11 (PushabilityMatrix); defined in the contract file only if absent when WP-1 starts.
```

```csharp
// src/TrinoSqlEngine/Ast/Visitors/ClientListExpansion.cs (new, public facade over the internal ClientListExpander of PLAN-SQL-128)
public static class ClientListExpansion
{
    public static SqlStatement Apply(SqlStatement statement, ClientListShapes shapes);   // throws ClientListException(SQL_LIST_PARAM_POSITION)
}

// src/Autheris.Application/Sql/Services/CrossSourcePlanner.cs
CrossSourcePlan Plan(..., SqlStatement? statement = null, FederatedParameterContract? parameters = null);   // appended, optional

// src/Autheris.Application/Olap/IDuckDbOlapEngine.cs
public sealed record GeneratedOlapQuery(string Sql, IReadOnlyList<BoundParameter>? Parameters = null,
    CompiledSql? Compiled = null, IReadOnlyDictionary<string, object?>? ClientValues = null);
// OlapQueryRequest: same two members appended.

// src/Autheris.Application/Olap/IFederatedStagingService.cs
public sealed record StagingTableRequest(..., VirtualFilterTablePlan? VirtualFilterPlan = null, PushedParameterAudit? ParameterAudit = null);

// src/Autheris.Application/Sql/Services/GovernedSqlExecutionService.cs
internal static void EnsureClientParameterNames(IReadOnlyDictionary<string, object?>? parameters);   // extracted from GovernedSqlExecutor.cs:245-256
```

---

## 4. Changed Files and Ownership (one developer; every file has exactly one work package)

### WP-1 - Contract, engine entry, DuckDB binding, executor flow, guard removal (no INC-6 needed)
- New: `src/Autheris.Application/Federation/FederatedParameterContract.cs` (with the audit records), `src/TrinoSqlEngine/Ast/Visitors/ClientListExpansion.cs`.
- Edit: `src/Autheris.Application/Olap/IDuckDbOlapEngine.cs` (two records), `src/Autheris.Application/Olap/DuckDbOlapEngine.cs` (`:190-195` binder path and `ClientNamed` refusal; region only, coordinate with `WP-FED-04`/`05` edits that merged before), `src/Autheris.Application/Sql/Services/CrossSourcePlanner.cs` (interface and `Plan` signature, expansion before `:282`; parameter region only), `src/Autheris.Application/Sql/Services/FederatedDuckDbExecutionService.cs` (steps 2, 3, 5 of A-1, `GeneratedOlapQuery` construction at `:339-343`, passing the contract to `Plan`), `src/Autheris.Application/Sql/Services/GovernedSqlExecutor.cs` (`:245-256` replaced by a call to the extracted helper), `src/Autheris.Application/Sql/Services/GovernedSqlExecutionService.cs` (helper `EnsureClientParameterNames`; removal of the #128 federation guard at `:131-137`, `:148-154`), `src/Autheris.Application/Sql/ListParameters/ClientListParameters.cs` (delete `RejectForFederation`), `src/TrinoSqlEngine/Ast/Emit/GovernedSqlException.cs` and the mapper rows (remove `SQL_LIST_PARAM_UNSUPPORTED`).
- Tests: `tests/Autheris.Tests.Unit/Sql/Federation/FederatedParameterContractTests.cs`, `FederatedParameterExecutionTests.cs` (harness: `tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedServiceHarness.cs`, `Security/WebSqlHeterogeneousJoinTests.cs`), `tests/TrinoSqlEngine.Tests/Compiler/ClientListExpansionFacadeTests.cs`, extend `tests/Autheris.Tests.Unit/Sql/AstCompilerDuckDbExecutionTests.cs`, `tests/Autheris.Tests.Architecture/OlapEntryPointArchitectureTests.cs` (still exactly one caller of `ExecuteGeneratedAsync`), `Errors/SqlCompilerErrorMapperTests.cs` rows.

### WP-2 - Audit, logging, DuckDB-stage error handling (no INC-6 needed)
- Edit: `FederatedDuckDbExecutionService.cs` (start and failure records `:505-548`, `:600-646`; error wrapping around `:339-343`), `FederatedParameterContract.cs` (only the `ToAudit` member, owned by WP-1, this WP adds tests only for it).
- Tests: `tests/Autheris.Tests.Unit/Sql/Federation/FederatedParameterAuditTests.cs`, injection corpus and log capture tests.

### WP-3 - Pushed parameters (needs `WP-FED-11`, step I-14)
- Edit: `src/Autheris.Application/Sql/Federation/Optimization/PushedClauseBuilder.cs` (typed-value hook, per-input budget, `p_pd_<c>_<i>` emission, `PushedParameterAudit`), `PushabilityEvaluator.cs` (calls the contract; region only), `src/Autheris.Application/Sql/Services/CrossSourcePlanner.cs` (passes the contract into the pushdown pass).
- Tests: `tests/Autheris.Tests.Unit/Sql/Federation/Optimization/PushedParameterTests.cs`, parity theory pushdown on/off.

### WP-4 - Per-read audit, analysis, subtree hand-off (needs `WP-FED-12`, step I-15; subtree part needs `WP-FED-13`, step I-17)
- Edit: `src/Autheris.Application/Olap/FederatedStagingService.cs` (audit region `:223-233`: `parameterNames`, `listParameterCounts`), `src/Autheris.Application/Olap/IFederatedStagingService.cs` (`ParameterAudit`), `src/Autheris.Application/Governance/Analysis/QueryAnalysisService.Sql.cs` and `QueryAnalysisModels.cs` (parameter name per pushed predicate, `PUSHED_IF_TYPE_MATCHES`, `ForAnalysis`), `SubtreeExecutor.cs` (passes `ToRequestParameters`), `src/Autheris.Application/Sql/WebSqlParameterValues.cs` and `ListParameters/ListParameterNormalizer.cs` (idempotent for an already normalised `ClientListValue`, A-5).
- Tests: `tests/Autheris.Tests.Unit/Sql/Federation/FederatedParameterSubtreeTests.cs`, `FederatedParameterAnalysisTests.cs`, per-read audit tests.

### WP-5 - Integration and gates
- Tests only: `tests/Autheris.Tests.Integration/Federation/FederatedParameterIntegrationTests.cs` (PostgreSQL + SQL Server Testcontainers fixtures of `WP-FED-10`; `DOTNET_USE_POLLING_FILE_WATCHER=true` in WSL), SqlGate/AST suites and `OlapEntryPointArchitectureTests` re-run (lead builds, G-BUILD). Phase 6 hand-off: correct the limitation sentence at `docs/features/f-dialect-02-ast-sql-compiler.md:436`, document the parameter rules in the federation feature doc, master plan row; not part of this plan's file ownership. Open hand-off for the lead: the pointer sentences in `PLAN-FED-01` 4.12.1 and 12.7 (tracker section 3.1) and the reference from `WP-FED-03` in the P-10 delta plan are not part of this file.

---

## 5. Fail-Closed Behaviour

| Condition | Outcome |
|---|---|
| Parameter name starts with `__gql_`, `p_rls_`, `rls_`; SQL contains `__param_` | `WebSqlPolicyException`, nothing analysed further (same as single-source) |
| Name outside `^[A-Za-z_][A-Za-z0-9_]{0,127}$` or with prefix `p[0-9]`, `__`, `gql_`, `autheris` that the statement uses | `SecurityException` from `BindClient` before any read (existing rule) |
| `@x` in the SQL but no value supplied | syntax error at analysis (parity with single-source), zero reads |
| Object or nested array value, `NULL`/mixed/too-large list, more than `MaxListParameters` lists | `ArgumentException` / `SQL_LIST_PARAM_*` (400) **after** all tables are authorized, **before** key sources and staging; zero source reads |
| Caller lacks consent on any table **and** parameters are invalid | the table denial, never the parameter error (authorization first) |
| List in any position other than the sole `IN`/`NOT IN` item | `SQL_LIST_PARAM_POSITION` from the expansion facade before `Generate` |
| Bind total above the DuckDB budget | `SQL_LIMIT_EXCEEDED` (`BindParameters`) from the emitter |
| `ClientNamed` slot reaches the DuckDB engine without a resolver (legacy loop) | refused; never bound as `NULL` |
| Client slot without value / list count differs from the compiled count (binder) | `MissingClientParameterException` / `SQL_LIST_PARAM_TYPE`; no subset is bound |
| Pushed conjunct: type class not in the table of A-4, `null`, empty list, count above the source `EffectiveMax`, per-input budget exhausted, ineligible column, HMAC/masked column, Virtual Filter table, unknown matrix cell | **not pushed**, DuckDB filters; never an error, never a truncated list |
| DuckDB or source error while values are bound | stable federation error, fixed message, exception type logged, no value in audit or response |
| Audit write failure (start or per-read) | request denied (INV-17, unchanged) |
| Parameters on transports without a parameter channel (Arrow, Flight SQL, MCP SQL, async job) | unchanged: no parameters exist on the request (owner flag F-1) |

---

## 6. Configuration

No new key. Reused: `WebSql:MaxListParameterValues` (default 1000, Q-128-1 and Q-14), `WebSql:MaxListParameters` (default 8), `WebSql:CrossSource:PredicatePushdown:Enabled` (WP-FED-12; with it `false` the contract still binds the DuckDB side and nothing is pushed, test 18). Federated parameters are always on once `CrossSource:Enabled` and the transport are allowed; there is no separate switch because the pre-existing behaviour (syntax error) is not a feature worth preserving (breaking changes allowed).

---

## 7. TDD Test List (write red first, in this order)

(S) = `[Trait("Category", "Security")]`. Tests 1-17 need no INC-6; 18-27 need `WP-FED-11`/`12`; 28-29 need `WP-FED-13`/`14`.

**WP-1**
1. `Federated_ScalarParameter_TodayFailsAtAnalysis` (characterisation, red against the intended behaviour, pins section 1 row 1).
2. `Federated_ScalarParameter_ReturnsCorrectRows` (AC-23, two SQLite-backed fake sources, `WebSql` and `Trino` transport) and the same through a declared endpoint with `SqlEndpoints` allowed.
3. `Normalization_RunsBeforeAnalysis_OriginalSqlKeptInAuditAndResult`.
4. `DuckDbText_ContainsMarkersOnly_ValueResolvedByBinder` (spy engine records command text and parameters; value only as parameter).
5. `DuckDbEngine_ClientNamedSlotWithoutResolver_IsRefused_NotBoundAsNull` (S, mutation control: re-adding `?? DBNull.Value` for `ClientNamed` must fail).
6. `Federated_ListParameter_InAndNotIn_Correct_EmptyListConstants_DuplicatesCollapsed` (AC-23).
7. `Federated_ListInWrongPosition_Refused_ZeroReads` theory (`=`, `LIMIT`, function argument, left side of `IN`, mixed items) (S).
8. `Federated_MissingParameter_FailsLikeSingleSource_ZeroReads` (parity theory across both paths).
9. `Federated_InvalidParameters_AfterAuthorization_NeverBeforeDenial` (S): list with `NULL`, mixed classes, too large, too many lists, object value, each with and without consent on one table; zero staging reads and zero key-source reads (spy executor, AC-26).
10. `ReservedNames_SameRefusalOnBothPaths` (S): `__gql_x`, `p_rls_x`, `rls_x`, `p0`, `autheris_x`, SQL containing `__param_`.
11. `NameDedup_AtX_And_X_CaseInsensitive_FirstSpellingWins`; `p_pd_0` as a client name works and is not confused with generated names.
12. `Injection_Corpus_ScalarAndList_NeverInTextStagedRequestsAuditOrLogs` (S, AC-25): quotes, `--`, `/*`, `;`, backslash, NUL, long strings, unicode.
13. `Guard_RemovedConstant_NoSqlListParamUnsupported`; `GeneratedOlapQuery_OnlyFederatedServiceCalls` (architecture).
14. `MaskedColumnGuardrail_NotTrippedByParameterToken_StillTripsForMaskedJoin` (S): `__param_x` in `Analyze` metadata must not be mistaken for a column.
15. `Federated_And_SingleSource_SameStatementShape_SameRows` (parity, one source pair versus cross-source).
16. `Guid_And_Int32_ClrValuesFromDeclaredEndpoint_BindInDuckDb` (needs the #128 `InferType` Guid fix).
17. `Audit_StartAndFailureRecords_HaveNamesKindsCounts_NoValues_InvalidNamesCountedNotWritten` (S, WP-2); `DuckDbConversionError_WithBoundValue_NotInAuditNorResponse` (S).

**WP-3 / WP-4**
18. `Pushdown_ScalarParameter_PushedAsBoundPlannedParameter_ResultEqualsPushdownOff` (AC-24; spy reader sees one `p_pd_` parameter, value not in text) and with `PredicatePushdown:Enabled=false` nothing is pushed while the DuckDB side still binds.
19. `Pushdown_List_BoundaryPerDialect` (S): count `cap-1`, `cap`, `cap+1` for SQL Server (configured 5,000 clamps to 2,000), Oracle 1,000, PostgreSQL; above the cap not pushed and rows equal; never truncated.
20. `Pushdown_TypeClassMismatch_NotPushed_ResultStillCorrect` (S): string for integer, double for integer, string for date and GUID, `null`, empty list; analysis shows the reason.
21. `Pushdown_SameTypedValue_AsDuckDb` (S): reference equality of scalar, equal elements and order for lists.
22. `Pushdown_PerInputBudget_SecondConjunctNotPushed_Deterministic` (S).
23. `Pushdown_StringNotEqualOnSqlServer_StillNotPushed_WithParameter` (matrix unchanged).
24. `Pushdown_MaskedOrHmacColumnFedByParameter_NotPushed_RowFilterNotOverridden` (S); a duplicate parameter name in the combined clause fails closed.
25. `PerReadAudit_HasParameterNamesAndListCounts_NoValues` (S); start record unchanged by pushdown.
26. `Analysis_ShowsFeedingParameterName_PushedIfTypeMatches_NoValues` (analysis mode without values).
27. `KeySetAndParameters_DoNotMix_ParameterValueNeverBecomesKey_ImportFlagNotConsulted` (S).
28. `Subtree_ParameterAndList_BoundBySourceBinder_OnlyReferencedNamesForwarded` (S); idempotent normalisation of `ClientListValue`.
29. `Subtree_InvalidList_StillRefusedBeforeAnyRead`.

**WP-5 integration**
30. PostgreSQL + SQL Server cross-source with scalar and list parameters, pushdown on and off equal; invalid list refused with zero reads in the spy executor; error bodies free of values.
31. SqlGate, AST security suites, G8 gate: unchanged (the governed compile path only gains an unused-here facade).

---

## 8. Work Packages for One Sequential Developer

**Dependencies, explicit.** Hard prerequisite: **#128 merged (I-5)**: `ClientListValue`, `ClientListShapes`, `ListParameterNormalizer`, `WebSqlParameterValues.NormalizeParameter`, `ListParameterLimits`, `ParameterReference.ListIndex/ListCount`, binder indexed elements and exact count, `InferType` Guid, the mapper row for `ClientListException`. Without #128 only the scalar part could ship (arrays would be refused by `Normalize` with `ArgumentException`); the tracker order excludes that case. Also assumed merged: `WP-FED-03` (I-10, thinner carrier, reshaped here), `WP-FED-02` (I-9, stable federation error code). `WP-3` needs `WP-FED-11` (I-14), `WP-4` needs `WP-FED-12` (I-15) and, for the subtree part, `WP-FED-13` (I-17). Gate: the lead builds once (Release, warnings as errors) and runs tests after the item (G-BUILD).

1. **WP-1** contracts commit first (section 3 types and signatures with `NotImplementedException` bodies compile), then tests 1-16 red, then implement in this order: extracted name helper, contract `Create`, engine facade, engine binder path and `ClientNamed` refusal, executor flow (A-1 steps 2, 3, 5), planner expansion, guard removal.
2. **WP-2** tests 17 red, then audit fields, failure `error` reduction, DuckDB-stage error wrapping.
3. **WP-3** (after I-14) tests 18-24 red, then typed-value hook, budget, `p_pd_` emission.
4. **WP-4** (after I-15, I-17) tests 25-29 red, then per-read audit, analysis, subtree hand-off.
5. **WP-5** test 30-31, hand-over note for Phase 6 (section 4, WP-5).

Gate per WP: SqlGate and AST security suites, the existing federation tests (`WebSqlHeterogeneousJoinTests`, `FederatedServiceHarness` users) and `OlapEntryPointArchitectureTests` stay green.

---

## 9. Risks

| ID | Risk | Mitigation |
|---|---|---|
| R-1 | I-10 lands a different scalar carrier and WP-1 conflicts with it | Tests are behavioural; WP-1 owns the reshaping; P-10 delta plan references this plan (section 1 last row) |
| R-2 | Reusing the engine binder in `DuckDbOlapEngine` collides with `WP-FED-04`/`05` edits of the same file | Region edit only (`:190-195`); sequential merging; the legacy loop stays for the other caller |
| R-3 | A bound string compared with a staged DATE/TIMESTAMP/GUID/numeric column behaves differently in DuckDB than in a source | First step pushes only the strict class table of A-4; a DuckDB probe test (like `DuckDbBindingProbeTests`) is part of test 20 and documents DuckDB's behaviour; widening is a follow-up |
| R-4 | `nvarchar` parameter against a `varchar` column forces an implicit conversion on SQL Server (client parameters carry no column type, PLAN-SQL-128 R-3) | Same behaviour as scalar client parameters today; pushed text compares are `=`/`IN` only for strings; follow-up with the literal annotator for parameters |
| R-5 | Before INC-6 a parameterised cross-source query still stages whole tables and can hit `ROW_LIMIT_EXCEEDED` | Documented in A-9; pushdown (WP-3) is the cure; no workaround that narrows reads without governance |
| R-6 | `NormalizeClientParameters` rewrites `@name` inside constructs the single-source path also rewrites; a federated-only token such as a DuckDB-specific `@` operator is unaffected because the grammar has none | Test 3 plus the SqlGate corpus |
| R-7 | Failure audit no longer shows the message for parameterised requests, hindering diagnosis | Exception type and `reasonCode` remain; messages stay in logs only when value-free (decided in Phase 3) |
| R-8 | Per-read parameter names grow audit rows | Only names of pushed predicates, bounded by `MaxTableCount` and the name pattern |
| R-9 | `ValidateAsync` (dry-run, no parameters) of a parameterised federated statement still fails at analysis | Unchanged and out of scope; the validation-only surfaces carry no parameters today |
| R-10 | The idempotent `ClientListValue` normalisation edit touches #128 files | Small, sequential, covered by WP-4 test 28 |

---

## 10. Owner Flags and Open Questions

| ID | Question | Default used in this plan |
|---|---|---|
| Q-14 | List maximum in federated queries | **Decided** by the owner: equals the #128 limit (1000, configurable); pushed lists additionally capped by the source `EffectiveMax`, not pushed above it |
| Q-15 | Parameter values in audit | Names, kinds and list counts everywhere; **no values anywhere** (no value-recording option exists today); never in per-source-read records |
| F-1 | PRD AC-23 lists "async job, Flight SQL"; those transports have no parameter channel (verified in PLAN-SQL-128 section 1) | Parameters work where the request carries them: WebSQL, Trino, declared SQL endpoints (with `SqlEndpoints` allowed for federation). Adding a parameter channel to async jobs and Flight SQL is a separate item; accept or request it |
| F-2 | `FEDERATION_PARAMETER_INVALID` from the PRD placeholder is dropped in favour of the single-source codes (A-7) | Accept |
| F-3 | Date, timestamp and GUID columns with string parameters are not pushed in the first step (A-4) | Accept; revisit after the DuckDB probe |
| F-4 | The silent-NULL hazard of `DuckDbOlapEngine` is closed here (`ClientNamed` refusal, A-3) although it exists independently | Accept |

---

## 11. Security Notes for Phase 3

These turn the placeholders `SEC-FED-35..37` of PLAN-FED-01 12.7 into review items; `SEC-FED-38..42` belong to #132 (`WP-FED-16`, wave 4) and stay a placeholder.

**SEC-FED-35 (bound, never spliced; injection corpus)**
1. Prove that no value reaches text on any of the three engines: DuckDB (markers `$n`, binder resolves values), source statements (`p_pd_<c>_<i>` scalar parameters inside the `TrinoSqlFactory` text) and subtree compiles (governed binder). Review `ClientListExpansion` for completeness of the residual position check against every `SqlAstRewriter` visit method (derived tables, CTEs, window frames) since it now runs outside `GovernedSqlCompiler` and outside the `AstSecurityVisitor` pass: confirm the federated AST needs no additional guard because the final text is DuckDB-only over staged tables and `OlapSqlPolicy`/trusted-generated rules apply (`DuckDbOlapEngine.cs:113-123`, `trustedGeneratedSql`). Confirm that the `trustedGeneratedSql` path (no client-text floor) is still justified now that client **values** enter the engine as parameters only.
2. The corpus test (test 12) must assert absence in: DuckDB text, staged-request filters, per-read audit, start/failure audit, captured logs, error bodies, and `BoundParameter`/`ClientListValue` `ToString`.
3. Confirm the closure of the silent-NULL hazard (test 5, mutation control) and that the legacy engine loop cannot be reached with a client slot.
4. Review the failure audit change (`error` no longer `ex.Message` with parameters) and the DuckDB-stage wrapper for value-free output; check DuckDB.NET exception messages that can quote bound values (conversion errors) and source provider messages on pushed predicates (SQL Server "Conversion failed when converting the varchar value ..."); confirm the type-class guard prevents those conversions and the `FEDERATION_SOURCE_ERROR` mapping removes messages.

**SEC-FED-36 (same typed value; mismatch not pushed; list above budget not pushed, never truncated)**
5. Verify that `TryGetScalar`/`TryGetList` return the objects the binder resolves (single dictionary, no second conversion) and that list order and de-duplication are identical on both sides; review `NULL` and empty-list handling (not pushed) and the interplay with `NOT IN` three-valued logic (client lists are `NULL`-free, V-128-4).
6. Review the class table of A-4 for losslessness per dialect (SQL Server `int` versus `bigint`, Oracle `NUMBER`, `decimal` precision), and confirm that float, date, GUID and string-for-non-string never push (no implicit conversion in a source, no collation surprise beyond the existing matrix; `<>`/`NOT IN` on strings per dialect stays as the reviewed `SEC-FED-21` matrix).
7. Verify the bind budget arithmetic (`EffectiveMax`, `-100` reserve, per-input sum) at the boundaries `cap-1`, `cap`, `cap+1` on SQL Server (2,100), Oracle (`MaxInListItems` 1,000) and PostgreSQL, including the combination with `p_arg_*`, row-filter, tenant and mask parameters of the planned read (a pushed list must never push the final statement over the dialect budget: the planned read would fail with `SQL_LIMIT_EXCEEDED` instead of the pushdown being skipped; decide whether `-100` suffices or the builder must count the decision's known policy parameters).
8. Confirm that masked and HMAC columns, Virtual Filter tables and columns without `Clear` access never receive a pushed parameter (SEC-FED-21/22), and that the pushed clause cannot override or replace the governed row filter (duplicate-name refusal, `SqlTableReadExecutor.cs:355`).
9. Confirm that pushing a caller's own value to a source is not a key-set import (Q-8) and that `allow_key_set_import` is not a way to enable or block it; confirm no parameter value can become a key-set key.

**SEC-FED-37 (one validation before any read; no parameter-dependent error before all tables are authorized)**
10. Verify the order in A-1: name and text checks (caller-supplied names and SQL only), authorization of **all** tables, then value validation, then key sources and staging. Test 9 must show the table denial wins for every invalid-parameter class and that an invalid parameter alone never discloses whether a table exists or is readable (same code as the single-source path, A-7).
11. Verify zero reads, including key-source reads of `PlanVirtualFiltersAsync`, on every validation failure (spy executor), and that the failure audit is written for validation failures like for any other refusal.
12. Confirm that error codes for parameter problems are identical on the single-source and federated paths (no federation oracle) and that counts are not echoed (PLAN-SQL-128 F-2).

**Audit and privacy (Q-15)**
13. Confirm the audit shape: names (pattern-valid only, others counted), kinds, list counts; never values; per-read records only for pushed predicates; no value-recording option introduced. Review whether parameter **names** can be sensitive (author-chosen identifiers of the caller's own statement; assumed acceptable, as in #128 `list_params`).

**Least privilege and denial of service**
14. Sources receive SELECT with bound scalar parameters only; no table-valued parameter, no `STRING_SPLIT`, no array type. DuckDB bind budget, `MaxListParameters` and the configured maximum bound memory before elements are copied; confirm the JSON body limit applies to WebSQL as for single-source.

---

## 12. Size Estimate

| Area | Production LOC | Test LOC |
|---|---:|---:|
| WP-1 contract, engine facade, DuckDB binder path, executor flow, guard removal | 300 | 480 |
| WP-2 audit, failure record, DuckDB-stage error wrapper | 70 | 120 |
| WP-3 pushed parameters (typed hook, budget, emission) | 110 | 220 |
| WP-4 per-read audit, analysis, subtree hand-off, idempotent normalisation | 100 | 160 |
| WP-5 integration | 0 | 120 |
| **Total** | **about 580** | **about 1,100** |

The PRD estimate was about 450 production and 700 test LOC; the growth comes from the engine binder path and the silent-NULL closure (not in the PRD), the facade, and the stronger security matrix. Size remains **S-M**. One sequential developer; no schema or DDL change, so no conflict with the shared baseline files of gate G-SHARED; shared source files are `CrossSourcePlanner.cs`, `FederatedStagingService.cs` and `FederatedDuckDbExecutionService.cs` (tracker row I-26), plus `DuckDbOlapEngine.cs`, `IDuckDbOlapEngine.cs`, `GovernedSqlExecutionService.cs` and `GovernedSqlExecutor.cs` added by this plan.

---

## 13. Changelog

| Revision | Date | Change |
|---|---|---|
| 1 | 2026-10-16 | Initial Phase 2 plan (status PROPOSED). |
