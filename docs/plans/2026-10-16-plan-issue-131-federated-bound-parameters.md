# Implementation Plan: REQ-FED-01 section 7.2 (#131) - Bound Parameters in Federated (Cross-Source) WebSQL Queries (`WP-FED-15`)

**Document ID:** `PLAN-FED-131` (revision 1, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [PRD REQ-FED-01](2026-10-16-req-cross-source-all-consumers.md) section 7.2 (FR-35..FR-38, AC-23..AC-26, Q-14, Q-15; owner decision Q-14 equals the #128 limit); FR-39..FR-45, AC-27..AC-31 and Q-16..Q-20 belong to #132 (`WP-FED-16`) and are **out of scope**
**Branch:** to be created from the `main` that contains the previous delivery item (delivery step I-26)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md); requirement [REQ-FED-01](2026-10-16-req-cross-source-all-consumers.md) section 7.2; architecture plan [PLAN-FED-01](2026-10-16-plan-cross-source-all-consumers.md) sections 4.11, 4.12.1, 12.7 (`WP-FED-15`, `SEC-FED-35..37`); delivery tracker [PLAN-DELIVERY-W23](2026-10-16-plan-waves-2-3-delivery.md) (planning step P-2, implementation step I-26); list contract [PLAN-SQL-128](2026-10-16-plan-issue-128-list-parameters.md)
**Status:** PROPOSED, Phase 3 security review complete (APPROVED WITH CONDITIONS, section 14, step S-2); next: fold conditions B-1..B-15 into the plan (owner decisions on F-1..F-4), then Phase 4 after gate G-PLAN

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
| 1 + review | 2026-10-16 | Phase 3 security review appended (section 14): 4 High, 5 Medium, 4 Low findings, binding conditions B-1..B-15 (SEC-FED-35..37), mandatory tests M-1..M-16, verdict APPROVED WITH CONDITIONS. Plan text above not yet revised. |

---

## 14. Phase 3 Security Review (2026-10-16)

**Reviewer role:** `csharp-security-expert` (Phase 3, delivery step S-2, after S-1 on PLAN-SQL-128). **Reviewed:** `PLAN-FED-131` revision 1 against PRD section 7.2 (FR-35..FR-38, AC-23..AC-26, Q-14, Q-15), `PLAN-FED-01` section 12.7 (`SEC-FED-35..37`; `SEC-FED-38..42` belong to #132 and stay placeholders), the S-1 conditions C-1..C-16 and tests M-1..M-16 of PLAN-SQL-128 section 14, and the code in this worktree. Every claim carries `file:line`; pushdown code (`Sql/Federation/`, INC-6) does not exist yet, so section 14.3 items on it are design reviews against the plan text and the existing read path. Source files were not changed.

**Verdict: APPROVED WITH CONDITIONS.** The design is sound: values are bound by one binder, the statement text carries markers only, one normalised dictionary feeds both DuckDB and the pushdown builder, authorization precedes value validation, and the audit records names and counts only. Four items are High and must be fixed in the plan text before Phase 4 (silent-NULL boundary, validation ordering against key-source reads, expansion amplification outside the governed compiler, unconditional error-text scrubbing); the rest are Medium/Low tightenings. F-6 carried from S-1 is confirmed and reclassified (14.7).

### 14.1 Threat model

| ID | Actor | Goal | Entry | Plan control | Residual (finding) |
|---|---|---|---|---|---|
| T-1 | Caller (WebSQL, Trino, declared endpoint) | Make a parameterised federated query return wrong or unfiltered rows without an error | `Parameters` bound to a `ClientNamed` slot | binder path (A-3), legacy-loop refusal | boundary must be unconditional, analysis contract must not execute (SEC-131-01) |
| T-2 | Caller | Inject SQL through a scalar or list value in DuckDB text or a source statement | value, list element | markers `$n`, `p_pd_<c>_<i>` bound scalars | none for splicing; string semantics and typing (SEC-131-05, -06) |
| T-3 | Caller without consent on a table | Use a parameter error as an oracle for table existence or readability | invalid list/name plus a table they may not read | A-1: authorization (step 4) before value validation (step 5) | verified and holds for value errors; **residual: position/positional/budget errors arise after key-source reads** (SEC-131-02) |
| T-4 | Caller | Trigger source or key-source reads before the request is fully valid | list in wrong position, `?`, budget breach | position proof in `Plan` | reads happen first (SEC-131-02) |
| T-5 | Caller | Exhaust CPU/memory via repeated lists, many lists, huge arrays, many staged inputs | `IN (@l)` repeated; 8 lists x 1000 | per-list and bind budgets | no per-statement cap outside the compiler (SEC-131-03), pushed budget arithmetic (SEC-131-07) |
| T-6 | Caller / operator reading audit and responses | Recover values (own or staged data) from audit, logs, error bodies | `error = ex.Message`, DuckDB conversion text, provider text | A-6/A-7 scrubbing when parameters are present | scrubbing is conditional on parameters; staged data values leak without them (SEC-131-04) |
| T-7 | Caller | Lose rows silently through a pushed predicate that is stricter than the DuckDB predicate | string `<>`, `NOT IN`, ranges on case-insensitive sources; lossy charset/type conversion | class table, matrix | collation and typing gaps (SEC-131-05, -06) |
| T-8 | Caller | Smuggle a forged normalised list past the admission rules | `ClientListValue` accepted idempotently (A-5) | limits re-check | widened admission (SEC-131-09) |
| T-9 | Operator-authored endpoint / other transports | Parameters on a transport with no parameter channel | Flight SQL, Arrow, MCP, async job | no `Parameters` on the request | fail-closed tests needed (SEC-131-12) |

### 14.2 What was verified and holds

1. **No parameter handling exists on the federated path today.** `FederatedDuckDbExecutionService.cs` has no reference to `request.Parameters` (grep in this file: only `request.Sql` at `:254`, `:312`, `:314`); `Analyze(request.Sql)` at `:254` sees raw `@name`. The plan's claim (section 1 row 1) that this fails as a syntax error, not as silent value loss, is consistent with `SqlAstBuilder.VisitColumnReference` (only `__param_*` become parameters); the first red test must pin it.
2. **The silent-NULL hole is real and latent.** `DuckDbOlapEngine.cs:190-195` binds `parameter.Value ?? DBNull.Value` for every `BoundParameter`; a `ClientNamed` slot has `Value == null` by contract. Today nothing emits such a slot into the engine; the first emitted client slot without a resolver would turn `x = @v` into `x = NULL` (never true) with no error. `ExecuteGeneratedAsync` has exactly one caller (`FederatedDuckDbExecutionService.cs:339`), `ExecuteOlapQueryAsync` one (`OlapStagingService.cs:501`).
3. **Reserved-name rules are not shared today.** `GovernedSqlExecutor.cs:245-256` refuses `__gql_`, `p_rls_`, `rls_`; the federated service has no equivalent; `SqlEmitterContext.cs:24-31`, `:166` refuses only `p[0-9]`, `__`, `gql_`, `autheris` and malformed names, and only for names the statement uses, at `Generate` time (inside `Plan`).
4. **Authorization-before-validation order is implementable as planned.** `AuthorizeAllTablesAsync` at `:308` precedes `ParseStatement`/`PlanVirtualFiltersAsync`/`Plan` at `:312-314` and the start audit; DML is already refused before routing (`:256-259`), so the empty-list `NOT IN` becomes `1 = 1` hazard of SEC-128-01 does not apply to this path (SELECT only, `isDml: false`).
5. **A client name never reaches a source statement.** Source text receives only planner-generated `p_pd_<c>_<i>` names; the existing duplicate-name refusal (`SqlTableReadExecutor.cs:355`) and the staging collision refusal (`FederatedStagingService.cs:294`) stay as backstops. The plan's collision argument (A-4 "Collisions") holds.
6. **No value-recording audit option exists**, so Q-15 resolves to "names and counts, never values" without a new option. The start record stores anonymised SQL and a hash only (`FederatedDuckDbExecutionService.cs:520-535`); the per-read record has no parameter fields (`FederatedStagingService.cs:223-233`).
7. **Pushed values are scalars only.** The planned read refuses collections (S-1 C-5); a pushed list is N scalars named `p_pd_*`; `SqlTableReadExecutor.cs:296-362` ANDs the clause with the governed filter, so a client predicate can only narrow, never replace, row-level security.
8. **Key-set (Q-8) does not apply to a caller's own value** as the plan argues: the value originates from the caller, not from table A's data; no new data crosses a source boundary.

### 14.3 Findings

**SEC-131-01 - High - The silent-NULL boundary is closed only for one branch; an analysis-mode contract and a missing resolver must also fail closed.**
Evidence: `DuckDbOlapEngine.cs:190-195` (`Value ?? DBNull.Value`); A-3 refuses a `ClientNamed` slot only in the "`Compiled` is not set" branch; A-8 creates a value-less `ForAnalysis` contract (`HasValues == false`) that runs through the same `Plan`.
Risk: (a) `Compiled` set with `ClientValues == null` relies on the binder throwing `MissingClientParameterException`, an assumption nobody tests at the engine boundary; (b) a plan produced from a `ForAnalysis` contract that reaches `ExecuteGeneratedAsync` would bind every parameter as missing/NULL; (c) a future second caller of the legacy loop re-opens the hole.
Required change: make the refusal an invariant of the engine, not of a branch: any `BoundParameter` with `Origin == ClientNamed` and no resolver (no `Compiled`, or `ClientValues` lacking the name) throws before `queryCmd.Parameters` is touched; `Value ?? DBNull.Value` remains only for non-client origins (literal NULL). `Plan` with a `ForAnalysis` contract returns a plan marked `AnalysisOnly`; the executor throws if such a plan is executed (checked in code and by test). Scalar JSON `null` supplied by the caller stays a deliberate NULL bind with single-source parity (`x = NULL` matches nothing); it is never pushed (A-4).

**SEC-131-02 - High - Position, positional-placeholder and bind-budget errors are raised in `Plan`, after the key-source reads of `PlanVirtualFiltersAsync`; the "zero reads" claims of section 5 and test 7 are false as written.**
Evidence: `FederatedDuckDbExecutionService.cs:313` (`PlanVirtualFiltersAsync`, which may call HTTP key sources) precedes `:314` (`Plan`); `ClientListExpansion.Apply` runs inside `Plan` before `Generate` (A-2); `BindClient` (`SqlEmitterContext.cs:158-172`) throws for positional `?` (`SqlBase.g4` has `QUESTION_MARK`) and for reserved or malformed client names, and the budget error `SQL_LIMIT_EXCEEDED` comes from the emitter at `Generate`. All three therefore happen after key-source HTTP calls and after their audit obligation (`keySourceReadsAudited`, `:300-302`).
Required change: add a pure "shape check" between A-1 step 5 and `PlanVirtualFiltersAsync`: run the position proof (`ClientListExpansion.Apply`), the client-name and positional check (the `BindClient` rules, without emitting), and an arithmetic bind-count pre-check (scalar slots + sum of expanded counts against the DuckDB budget) on the parsed `statement`; keep the expanded AST and hand it to `Plan` so there is one expansion. The same applies to any later check that depends only on statement text plus parameter shapes. Section 5 and tests 7/9 then hold literally; they must assert zero key-source reads, not only zero staging reads.

**SEC-131-03 - High - Per-statement expansion cap (S-1 C-2) is explicitly dropped on the federated path; repeated lists amplify before the only budget (65,535 binds) is checked.**
Evidence: A-2 states that the `GovernedSqlCompiler.cs:207` expansion-factor budget "does not apply here" and relies on the emitter bind budget after expansion; S-1 SEC-128-02/C-2 showed that expansion allocates AST nodes and text per use before that budget. With `MaxListParameters` 8 and 1,000 elements, a statement that repeats `IN (@l)` thousands of times allocates millions of nodes before `SQL_LIMIT_EXCEEDED`. No plan cache exists on this path, so the cost is paid per request, and the DuckDB text size is only bounded by `ValidateUserSql` after generation.
Required change: the running-total cap lives inside the shared expander (S-1 C-2: `MaxExpandedListItems = min(MaxBindParameters - 100, 10,000)` using DuckDB capabilities), so `ClientListExpansion.Apply` enforces it for both paths; it is checked before allocation. Unreferenced supplied lists are dropped (S-1 C-4) and do not enter the contract's shapes, the audit or any pushdown. The federated planner must not call an expander variant without the cap.

**SEC-131-04 - High - Error text is scrubbed only "for requests that carry parameters"; DuckDB and source messages quote staged data and already leak through `error = ex.Message`.**
Evidence: `FederatedDuckDbExecutionService.cs:628-631` stores `ex.Message` for every non-`GatewaySecurityException` (including `WebSqlPolicyException`, BCL `SecurityException`, `MissingClientParameterException`, `InvalidOperationException`, DuckDB and provider exceptions); the code comment at `:627` (SEC-11) already states that messages "may carry names or data values of the failing source". A DuckDB conversion error quotes the offending value, which can be a staged row value, independent of any request parameter. A-6 and A-7 gate the fix on `contract.HasValues`/"requests that carry parameters", which leaves the staged-data leak open and makes the behaviour depend on caller input.
Required change: unconditionally, for every failure record on this path: `error` = fixed text derived from the stable code (`reasonCode`) or the exception type name, never `ex.Message`; the DuckDB-stage wrapper around `ExecuteGeneratedAsync` applies for every request, not `when contract.HasValues`; the same stable `FEDERATION_SOURCE_ERROR` mapping (message fixed) applies to the HTTP response; `ClientListException`, `MissingClientParameterException` and the reserved-name `WebSqlPolicyException` carry fixed messages and a code (S-1 C-7/C-15). Log lines of this path log the exception type and code only. Cancellation and `GatewaySecurityException` pass through unchanged.

**SEC-131-05 - Medium - String pushdown semantics: only positive equality is a safe superset; negation and ordering lose rows on case-insensitive or accent-insensitive sources.**
Evidence: A-4 keeps "the matrix of PLAN-FED-01 4.11.2" and names only SQL Server, Oracle and Databricks as excluded for `<>`/`NOT IN` on strings; that matrix is not in the code (no `Sql/Federation/`). DuckDB compares staged VARCHAR bytewise (case-sensitive). For `=` and `IN` a source collation that is weaker than binary only widens the source result (superset; DuckDB re-filters). For `<>`, `NOT IN`, `<`, `>`, `BETWEEN`, `LIKE` the source predicate can exclude rows DuckDB would keep (`'ABC' <> 'abc'` is true in DuckDB, false under a CI collation): rows vanish silently. Case-insensitive defaults exist beyond the three named dialects (MySQL/MariaDB, SQLite `COLLATE NOCASE` columns, PostgreSQL `citext` or nondeterministic collations), and a column's effective collation is not in the catalog.
Required change: for string-class parameters push only `=` and positive `IN`, on every dialect, until the catalog carries a proven binary/deterministic collation per column; `<>`, `NOT IN`, ranges, `LIKE` with a parameter are never pushed. Test with a CI-collated column (SQL Server `Latin1_General_CI_AS` in the integration fixture) that `<>` and `NOT IN` results equal pushdown-off. Empty string: not pushed for Oracle sources (empty string is NULL there; S-1 SEC-128-06 refuses `""` list elements, the scalar case needs the same decision).

**SEC-131-06 - Medium - The type-class table of A-4 needs per-dialect and per-binding corrections.**
Evidence: A-4 table (plan lines 76-84) is dialect-neutral; R-4 notes `nvarchar` against `varchar`.
Required change: (a) `boolean` class is pushed only where the dialect has a real boolean/bit type (not Oracle before 23ai, not MySQL tinyint unless catalog proves it); otherwise not pushed. (b) `integer` class: values must be within the `long` range after normalisation (no `ulong`, no `BigInteger`); unsigned source columns (MySQL `UNSIGNED`) are classified `decimal`-or-not-pushed, never `integer`, so a negative value cannot be mis-compared. (c) `decimal` class: only `decimal` CLR values (never `double`), scale/precision above the source column's catalog precision not pushed. (d) String parameters on SQL Server are bound as Unicode (`nvarchar`); on MySQL the connection character set must be `utf8mb4`; a lossy conversion to a narrower code page would turn a DuckDB-equal pair into an unequal source pair and silently drop rows. Add a spy test on the parameter `DbType` and a source test with accented and non-BMP text against a `varchar` and an `nvarchar` column. (e) The plan's DuckDB probe (R-3) is required, not optional, before date/GUID/float widening (F-3 stays as proposed).

**SEC-131-07 - Medium - Pushed-list budget arithmetic: a fixed `-100` reserve does not cover policy parameters, other pushed conjuncts or key-set batches.**
Evidence: A-4 "Budget" uses `EffectiveMax = min(WebSql:MaxListParameterValues, MaxBindParameters - 100, MaxInListItems)`; the planned read adds tenant, row-filter (`p_rls_*`, unbounded by policy design), mask and `p_arg_n` parameters (`SqlTableReadExecutor.cs:296-362`); `PlanVirtualFiltersAsync` key-set pushes (`p_ks_*`, `:360-` and `pushdownKeyCount` at `:597`) share the same statement. SQL Server allows 2,100 parameters per RPC (`DialectCapabilityTable.cs:39`), Oracle 1,000 items per `IN` (`:127`).
Required change: the builder computes the remaining budget per staging input as `MaxBindParameters - (known policy and argument parameter count from the access decision) - (key-set parameters already planned) - (pushed parameters already admitted)` and pushes a conjunct only if its element count fits (deterministic, never partial); `MaxInListItems` is applied per `IN` item. A last-line pre-flight in the read path turns an over-budget combined clause into "not pushed" and never into a user-visible `SQL_LIMIT_EXCEEDED`. Budget is shared with the key-set planner (placeholder for #132, `SEC-FED-38..42` territory); the combination test M-10 below fixes the ordering rule now.

**SEC-131-08 - Medium - Audit shape: the parameter list is unbounded and not limited to referenced names.**
Evidence: A-6 records `parameters` as "ordinal-sorted list of `{name, kind, count}`" of the contract, which is built from all supplied keys (`FederatedParameterContract.Create`, A-1 step 5); only a pattern check and a count of invalid names are specified. A caller can supply thousands of well-formed keys; audit rows and the per-request work grow with caller input, and unreferenced names are noise that does not describe the query.
Required change: record only parameters the statement references (names from the normalised SQL), at most 64 entries (rest counted in `parameterOverflow`), names matching `^[A-Za-z_][A-Za-z0-9_]{0,127}$` only, never values, never raw lengths of strings; per-read `parameterNames`/`listParameterCounts` as planned. Spans/telemetry tags carry no parameter fields (assert by grep test). The reserved-name and unreferenced-key checks of A-1 step 2 still run over all supplied keys (parity with `GovernedSqlExecutor.cs:245-256`), with a hard cap on the number of supplied keys (reuse the request body limit; add a count cap of 256 on the contract).

**SEC-131-09 - Medium - Making `NormalizeParameter`/`IsListCandidate` accept an already normalised `ClientListValue` widens admission for every caller.**
Evidence: A-5 and WP-4 edit `WebSqlParameterValues.NormalizeParameter` and `ListParameterNormalizer.IsListCandidate` so a `ClientListValue` is accepted "with a re-check of the limits only". S-1 C-5 deliberately made the admission allowlist strict (two accepted shapes, `PlannedTableRead == false`, executor re-validates a `ClientListValue`). A public idempotent path means any in-process caller that builds a `GovernedSqlQueryRequest` can hand over a pre-built value that skipped element rules (NUL, type class, duplicates).
Required change: do not relax the public normaliser. The subtree hand-off uses an internal, separate entry (for example `SubtreeParameterValues` accepted only by the subtree executor through an `internal` overload), and that entry re-validates every element exactly like first-time normalisation (type class, NULL, NUL, de-duplication, maximum), not the limits alone. Architecture test: the only producer of such a hand-off is the contract.

**SEC-131-10 - Low - Name rules and duplicate spellings.**
Evidence: `GovernedSqlExecutor.cs:245-256` refuse only `__gql_`, `p_rls_`, `rls_`; the contract audit names, the per-read names and the pushed `p_pd_*` live in the same audit vocabulary. `BuildClientValues` (`GovernedSqlExecutor.cs:655-671`) lets the first spelling win by dictionary enumeration order when `x` and `@x` carry different values.
Required change: keep the single shared helper (no divergence); additionally refuse a request whose duplicate spellings (`x`/`@x`/case variants) carry different values with the existing fixed policy message rather than picking one by enumeration order (identical values collapse); audit lists client names under `parameters` and pushed names under `parameterNames` in separate fields (already planned), with test 11 asserting a client named `p_pd_0` is never listed as a pushed name.

**SEC-131-11 - Low - Value-dependent DuckDB errors are a data-shape oracle only over rows the caller may already read.**
Evidence: a bound string against a staged integer column raises a conversion error that depends on the value (and on staged data when DuckDB casts the column). The staged rows are the governed, masked rows; HMAC/masked columns contain masked values in DuckDB. The fixed `FEDERATION_SOURCE_ERROR` mapping (SEC-131-04) removes content; occurrence remains.
Required change: test that a parameter compared with a masked/HMAC column cannot reveal the clear value by error/no-error difference beyond what the masked value allows (the DuckDB side only ever sees the masked value); no code change beyond SEC-131-04.

**SEC-131-12 - Low - Transports without a parameter channel must fail closed, including positional `?`.**
Evidence: Arrow export (`ArrowExportEndpoints.cs:187`), Flight SQL (`ArrowFlightSqlServer.cs:198`), MCP SQL and async jobs (`AsyncQueryJobBackgroundWorker.cs:242`) build `GovernedSqlQueryRequest` without `Parameters`. A statement with `@x` fails at analysis; a positional `?` reaches `BindClient` and is refused (`SqlEmitterContext.cs:161-164`), but only inside `Plan` (SEC-131-02).
Required change: after SEC-131-02 both refuse before any read on every transport; tests cover async job and Flight SQL transports explicitly (AC-23 as amended by F-1).

**SEC-131-13 - Low - Allocation and copies across staged inputs.**
Evidence: A-4 `TryGetList` returns `IReadOnlyList<object>`; up to `MaxTableCount` (default 5) inputs each receive up to `EffectiveMax` elements. Correct if the list is a shared immutable view, a per-input copy multiplies memory and breaks the G8 allocation gate.
Required change: `TryGetList` returns a read-only view over the contract's single list (no per-input copy except the bounded `p_pd_` parameter objects); allocation test on the pushdown pass for the default list size.

### 14.4 Binding conditions for Phase 4 (mapped to `SEC-FED-35..37`)

| ID | Maps to | Condition |
|---|---|---|
| B-1 | SEC-FED-35 | **Engine boundary** (SEC-131-01): a `ClientNamed` slot without a resolver is refused for every entry; `?? DBNull.Value` only for non-client origins; an `AnalysisOnly` plan cannot execute; mutation control for each. |
| B-2 | SEC-FED-35 | **No value in text** on all three engines: DuckDB text, source statements, subtree compiles; the injection corpus asserts absence in DuckDB text, staged-request filters, per-read audit, start/failure audit, captured logs, error bodies, `ToString` of `BoundParameter` and `ClientListValue`. |
| B-3 | SEC-FED-35 | **Error text** (SEC-131-04): `error` is a fixed code text or exception type for every failure on this path, unconditionally; DuckDB-stage wrapper unconditional; fixed messages for `SQL_LIST_PARAM_*`, `MissingClientParameterException`, reserved names. |
| B-4 | SEC-FED-35 | **Expansion** (SEC-131-03): one shared expander with the per-statement running-total cap (S-1 C-2) and referenced-only shapes (S-1 C-4); the facade has no uncapped mode; completeness of the position proof reuses S-1 C-10 (reflection test). Federated AST needs no extra visitor guard: SELECT-only, DuckDB sandbox with `enable_external_access = false` (`DuckDbOlapEngine.cs:BuildSetupSql`), values bound; `trustedGeneratedSql` stays justified because client text enters only as markers and parameters (documented, test 5). |
| B-5 | SEC-FED-37 | **Order** (SEC-131-02): name/text checks (caller input only) -> authorization of all tables -> value validation -> shape check (position, positional, name rules, bind arithmetic) -> only then `PlanVirtualFiltersAsync`, `Plan`, start audit, staging. The table denial wins over every parameter error; zero staging reads and zero key-source reads on every validation failure; failure audit is still written. |
| B-6 | SEC-FED-37 | **Same codes, no oracle**: identical stable codes as the single-source path (A-7, F-2 accepted), counts never echoed (S-1 C-15), reserved-name helper shared; HTTP-level test through the federated route. |
| B-7 | SEC-FED-36 | **Same typed value**: `TryGetScalar`/`TryGetList` return the objects the binder resolves from one dictionary; list order and de-duplication identical; scalar `null` and empty list not pushed. |
| B-8 | SEC-FED-36 | **Pushed string rule** (SEC-131-05): only `=` and positive `IN` for string parameters on every dialect; no negation, ordering or `LIKE`; Oracle empty string not pushed. |
| B-9 | SEC-FED-36 | **Type-class table** (SEC-131-06): per-dialect corrections (a)-(e); Unicode string binding; DuckDB probe test mandatory. |
| B-10 | SEC-FED-36 | **Budget** (SEC-131-07): remaining budget counted per input from the decision's known policy/argument parameters, key-set parameters and earlier pushed parameters; per-`IN` item `MaxInListItems`; over budget means not pushed, never truncated, never an error; Q-14 stays one limit (no new option). |
| B-11 | SEC-FED-36 | **Governance**: masked, HMAC, no-Clear columns and Virtual Filter tables never receive a pushed parameter (SEC-FED-21/22); the pushed clause cannot override the governed row filter (`SqlTableReadExecutor.cs:355`). |
| B-12 | SEC-FED-36 | **Key-set**: a client value is not an import; `allow_key_set_import` is not consulted; no parameter value becomes a key-set key; `p_pd_*` and `p_ks_*` never mix and share the bind budget. |
| B-13 | SEC-FED-35/37 | **Audit shape** (SEC-131-08): referenced names only, pattern-valid, at most 64 entries, supplied-key cap 256; kinds and counts; no values; per-read fields only for pushed predicates; no value-recording option. |
| B-14 | SEC-FED-35 | **Subtree hand-off** (SEC-131-09): internal, separate entry with full element re-validation; public normaliser not relaxed. |
| B-15 | SEC-FED-37 | **Parity and transports** (SEC-131-10, -12): duplicate spellings with different values refused; parameter-free transports refuse `@x` and `?` before any read. |

`SEC-FED-38..42` (#132: key-set derivation, budgets, import consent) remain placeholders; B-10 and B-12 only fix the interface for the parameter side.

### 14.5 Mandatory security tests (in addition to plan section 7; all `[Trait("Category","Security")]`)

| ID | Test | Finding |
|---|---|---|
| M-1 | `DuckDbEngine_ClientNamedSlot_NoResolver_Refused_ForGeneratedAndOlapEntry`; `DuckDbEngine_CompiledWithMissingClientValue_Refused`; `AnalysisOnlyPlan_Executed_Throws`; mutation control re-adding `?? DBNull.Value` for `ClientNamed` fails | SEC-131-01 |
| M-2 | `Federated_ShapeErrors_PositionPositionalReservedNameBudget_ZeroKeySourceReads_ZeroStagingReads` (spy key source and spy executor; list in `=`, `LIMIT`, left of `IN`, positional `?`, reserved used name, bind arithmetic overflow) | SEC-131-02 |
| M-3 | `Federated_TableDenial_Wins_ForEveryInvalidParameterClass_AndShapeError` (with and without consent on one table; identical code and message) | SEC-131-02, B-5 |
| M-4 | `Expansion_RepeatedUse_IsCappedPerStatement_FederatedPath` (thousands of uses of a 1,000-element list refused before emission; allocation counter) and `UnreferencedList_DroppedFromShapesAndAudit` | SEC-131-03 |
| M-5 | `FailureAudit_ErrorField_NeverContainsExceptionMessage_ForAnyExceptionType` (parameterised and parameter-free requests; DuckDB conversion error quoting a staged value; provider exception quoting a value; `WebSqlPolicyException`) | SEC-131-04 |
| M-6 | `DuckDbStageError_IsMappedToFixedFederationError_Always_ResponseBodyHasNoValue` (HTTP level, WebSQL and Trino routes) | SEC-131-04 |
| M-7 | `Pushdown_StringNegationAndRanges_NeverPushed_OnCaseInsensitiveSource_ResultEqualsPushdownOff` (`<>`, `NOT IN`, `<`, `BETWEEN`, `LIKE`; CI-collated column) | SEC-131-05 |
| M-8 | `Pushdown_TypeClass_PerDialectMatrix` (boolean on Oracle, unsigned integer, `double`, `decimal` beyond precision, `ulong`, empty string on Oracle all not pushed; Unicode `DbType` spy; accented and non-BMP text against `varchar` and `nvarchar` columns, rows equal to pushdown-off) | SEC-131-06 |
| M-9 | `Pushdown_ListBudget_BoundaryPerDialect_WithPolicyParameters` (count `cap-1`, `cap`, `cap+1` with RLS, tenant, mask, `p_arg` parameters present; SQL Server 2,100, Oracle 1,000 per `IN`, PostgreSQL; over budget means not pushed and rows equal, never `SQL_LIMIT_EXCEEDED`) | SEC-131-07 |
| M-10 | `Pushdown_AndKeySet_SharedBudget_NeverExceedsDialectLimit` (placeholder interface test; ownership of key-set side stays with #132) | SEC-131-07, B-12 |
| M-11 | `Audit_ParametersField_ReferencedNamesOnly_Capped64_OverflowCounted_NoValues` (5,000 supplied keys, malformed names, 128+ character names, newline in name) and `SuppliedKeyCount_AboveCap_Refused` | SEC-131-08 |
| M-12 | `Subtree_HandOff_ForgedClientListValue_FullRevalidation` (NUL element, mixed class, duplicate, above maximum) and `PublicNormalizer_StillRejectsClientListValue` | SEC-131-09 |
| M-13 | `DuplicateSpellings_DifferentValues_Refused_SameValues_Collapse` (both paths); `ClientNamed_p_pd_0_NotListedAsPushedName` | SEC-131-10 |
| M-14 | `MaskedOrHmacColumn_ComparedWithParameter_DuckDbSeesMaskedOnly_NoClearValueOracle` | SEC-131-11 |
| M-15 | `ParameterFreeTransports_AsyncJob_FlightSql_Arrow_Mcp_AtParameterAndPositionalPlaceholder_RefusedBeforeAnyRead` (AC-23 as amended) | SEC-131-12 |
| M-16 | `Pushdown_TryGetList_ReturnsSharedView_NoPerInputCopy` (allocation counter, five staged inputs, 1,000 elements); `Telemetry_NoParameterFields` (grep/architecture test on spans and log templates of this path) | SEC-131-13, B-2 |

Sufficiency of the planned tests: tests 1-17 and 18-29 cover the functional matrix well, but test 7 and 9 assert only staging reads (key-source reads missing), test 12 omits staged-data error text, test 17's "conversion error" case is conditional on a bound value, and nothing covers CI collation, unsigned/boolean/charset typing, shared bind budget with policy parameters, forged subtree values, audit overflow or parameter-free transports. M-1..M-16 close these.

### 14.6 Answers to the explicitly asked questions

- **Silent NULL (`Value ?? DBNull`)**: must fail closed; the plan's direction is right, the boundary must be unconditional (SEC-131-01, B-1).
- **Reserved names on the federated path**: not protected today (14.2 item 3); the shared helper plus the existing `BindClient` rules are sufficient because client names never reach source text; add the duplicate-spelling rule (SEC-131-10).
- **Pushed values**: the table is correct for positive comparisons on integer/decimal/boolean/string only after the dialect corrections and string restriction (SEC-131-05, -06); NULL and empty lists are correctly not pushed; per-source budget needs real accounting (SEC-131-07).
- **Leakage**: no value-recording option exists; the real leak is `error = ex.Message` (SEC-131-04); Q-15 rules (names, kinds, counts) are acceptable with the caps of SEC-131-08.
- **Ordering**: authorization-first holds for value errors; it does not yet hold against key-source reads for shape errors (SEC-131-02).
- **Key-set interaction**: no import, no consultation of `allow_key_set_import`; shared budget (B-12).
- **No-channel transports (AC-23)**: unchanged and fail closed after SEC-131-02 (SEC-131-12).
- **Parity with single-source**: same codes and helper; add the duplicate-spelling rule; the only deliberate difference (federated has no plan cache, SELECT only) reduces risk.
- **DoS via repeated lists across inputs**: closed by B-4 (statement cap) and B-10 (per-input budget); bounded to `MaxTableCount` inputs.

### 14.7 Recommendations on owner flags

| Flag | Recommendation |
|---|---|
| F-1 (async job and Flight SQL have no parameter channel) | **Accept**: out of scope; amend PRD AC-23 to "WebSQL, Trino and declared SQL endpoints"; require M-15 so the other transports provably fail closed. A parameter channel for Flight SQL/async jobs would be a separate item with its own review (it creates new admission paths). |
| F-2 (`FEDERATION_PARAMETER_INVALID` dropped) | **Accept**: identical codes on both paths remove a federation-only oracle. Update PRD 7.2 and `PLAN-FED-01` 12.7 wording in the delta pass; `reasonCode` in audit carries the stable code. |
| F-3 (date, timestamp, GUID not pushed) | **Accept**; the DuckDB probe becomes mandatory before widening (B-9); float/double stay unpushed permanently. |
| F-4 (silent-NULL closed here) | **Accept**, and strengthen to the unconditional engine invariant (B-1). |
| F-6 carried from S-1 | **Resolved by this plan, reclassified**: today `@x` fails at analysis (not a silent drop); supplied but unreferenced parameters are ignored. The real silent hazard is the engine NULL bind, closed by B-1. After this item unreferenced supplied lists are dropped, never validated into shapes (B-4) and never pushed. |
| Q-14 / Q-15 | Q-14 stays one limit; pushed lists use `EffectiveMax` with real accounting (B-10). Q-15: names, kinds, counts only, with the caps of B-13. |

### 14.8 Hand-over

Phase 2 (architect) revises sections 2 (A-1 step order, A-2 expander cap and shape check, A-3 invariant, A-4 table and string rule and budget, A-5 hand-off entry, A-6 unconditional error text and audit caps), 5 and 7 per B-1..B-15 and adds M-1..M-16 to section 7; section 4 needs no new file ownership beyond `FederatedDuckDbExecutionService.cs` (shape-check step) and the shared expander. Phase 4 starts only after G-PLAN. `SEC-FED-38..42` stay placeholders for the #132 review (S-3 or later); this review fixed only the interface points B-10 and B-12.
