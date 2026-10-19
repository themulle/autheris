# Implementation Plan: REQ-SQL-128 - List Parameters for `IN` / `NOT IN` in WebSQL and Declared Queries

**Document ID:** `PLAN-SQL-128` (revision 1, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [PRD REQ-SQL-128](2026-10-16-req-issues-128-129-130-websql-parameters-user-attributes-timezones.md) section 2 (V-128-1..9, FR-128-1..8, AC-128-1..8; owner decision Q-128-1)
**Branch:** to be created from the `main` that contains the previous delivery item (delivery step I-5)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md); requirement [REQ-SQL-128](2026-10-16-req-issues-128-129-130-websql-parameters-user-attributes-timezones.md); delivery tracker [PLAN-DELIVERY-W23](2026-10-16-plan-waves-2-3-delivery.md) (planning step P-1, implementation step I-5)
**Status:** PHASE 3 COMPLETE, APPROVED WITH CONDITIONS `SEC-128-01..14` (section 14); Phase 2 revision 1 stands, the binding conditions of section 14.4 amend it; Phase 4 implemented 2026-10-17 on `feat/issue-128-list-parameters` (WP-1..WP-6, deviations in section 13), Phase 5 pending

**Binding project rules applied:** values are bound, never spliced; fail closed with one stable code per cause; no list values, filter literals or attribute values in logs, audit, spans or exception text; no upgrade or repair migrations (this item changes no schema); least privilege on sources (SELECT only, no table-valued parameters, no `STRING_SPLIT`); CI stays fast (no sleeps, shared fixtures); the SqlGate/AST security suites and the G8 allocation gate stay green; Phase-4 TDD developers are Sonnet, one developer, sequential (gate G-ONE).

---

## 1. Verification of the PRD Against the Code (`8bb511a1`)

| PRD item | Result |
|---|---|
| WebSQL rejects arrays | **Confirmed.** `WebSqlParameterValues.Normalize` (`src/Autheris.Application/Sql/WebSqlParameterValues.cs:16-49`) handles String/Number/True/False/Null; `default:` (line 46-47) throws `ArgumentException`. Its only production caller is `GovernedSqlExecutor.BuildClientValues` (`src/Autheris.Application/Sql/Services/GovernedSqlExecutor.cs:656-671`, call at `:260`). The existing test `WebSqlParameterValuesTests.ArraysAndObjects_AreRejected` pins the scalar contract of `Normalize`; the plan therefore leaves `Normalize` unchanged and adds a second entry point (A-4). |
| Placeholder rewrite | **Confirmed and design-relevant.** `GovernedSqlExecutionService.NormalizeClientParameters` (`GovernedSqlExecutionService.cs:369-436`) rewrites only `@name` tokens whose name was supplied into `__param_<name>` (`:386`, `:430`); the AST builder turns `__param_x` into `ParameterReference(x, IsSynthetic: true)` (`src/TrinoSqlEngine/Ast/Builder/SqlAstBuilder.cs:1058-1064`). An array value needs no change here. |
| Client slot is one bound parameter, text fixed at compile time | **Confirmed.** `SqlEmitterContext.BindClient` (`src/TrinoSqlEngine/Ast/Buffer/SqlEmitterContext.cs:158-172`) binds one slot per distinct name (`DedupKey(Client, name)`), value `null`, type placeholder `String`, origin `ClientNamed`; the name must match `ClientNameRegex` and not `ReservedClientNameRegex` (`:25-31`, `:166`). `DbCommandCompiledSqlBinder.BindCore` resolves the value per request by `SourceName` and infers the type from the value (`src/TrinoSqlEngine/Ast/Emit/ICompiledSqlBinder.cs:173-217`, `InferType` `:241-257`). A list therefore cannot be one slot: the marker count is part of the SQL text. |
| Compiled statement is cached value-free by raw SQL | **Confirmed.** `GovernedSqlCompiler.CompileCore` (`src/TrinoSqlEngine/GovernedSqlCompiler.cs:112-129`) keys `CompiledSqlTemplateCache` by `CompileCacheKey.Material(request, rawSql, ...)`, which serializes **every public property of `CompileRequest`** by reflection (`src/TrinoSqlEngine/Ast/Emit/CompiledSqlTemplate.cs:243-345`; unknown non-`TrinoSqlEngine` types throw `NotSupportedException`, `IReadOnlyCollection<string>`/`IEnumerable<string>` are supported and sorted, `:310-318`). A new `CompileRequest` property is part of the key by construction. Template slots are rebuilt by `CompiledSqlTemplate.ToSlots`/`Rebind` (`:49-97`), which must carry the new slot fields. |
| Bind budget exists and is enforced at emission | **Confirmed, with a correction.** The compiler enforces `DialectCapabilities.MaxBindParameters` inside `SqlEmitterContext.Bind` (`SqlEmitterContext.cs:187-190`, `SqlLimitExceededException(BindParameters)`) for **all** parameters (tenant, policy, mask, literals, client), so the "bind-time budget check" of V-128-5 is already total and needs no second counter. Values: SQL Server 2,100, DuckDB 65,535, PostgreSQL 65,535, Oracle 32,767 with `MaxInListItems` 1,000 (ORA-01795), Databricks 1,000 (provisional), SQLite 32,766 (`src/TrinoSqlEngine/Ast/Capabilities/DialectCapabilityTable.cs:39-40`, `:68`, `:97`, `:126-127`, `:156`, `:191`). **Correction to the PRD:** `IParameterBudgetProvider`/`DatabaseParameterBudgetProvider` (`src/Autheris.Infrastructure/Persistence/DatabaseParameterBudgetProvider.cs:9-18`: SQLite 999, Oracle 1,000, PostgreSQL 10,000) is the conservative budget of the chunked executors, **not** what the compiler enforces. The list hard cap uses `DialectCapabilities.MaxBindParameters` (the authority of compiled statements), see A-6. An Oracle IN list above 1,000 items is rejected by `DialectCapabilityValidator` (`DialectCapabilityValidator.cs:44-47`); the cap must therefore also respect `MaxInListItems`. |
| Dialect is known only after stage 2 | **Confirmed.** `GovernedSqlRewriter.RewriteCoreAsync` (`src/Autheris.Application/Sql/Services/GovernedSqlRewriter.cs:132-...`) resolves the data source, then builds the `CompileRequest` (`:634`, `CompileRequestFactory.Create`, `src/Autheris.Application/Sql/Policy/CompileRequestFactory.cs:24-68`) and compiles (`:643`). The executor normalizes values **before** the dialect exists (`GovernedSqlExecutor.cs:260`). Validation is therefore two-staged (dialect-free shape rules in the executor, dialect-dependent hard cap in the rewriter, A-6). |
| `InListExpression` / AST position | **Confirmed.** `InListExpression(Operand, Items, IsNotIn)` (`src/TrinoSqlEngine/Ast/Nodes/Expressions.cs:137-140`) is emitted as `IN (` items `)` (`SqlDialectGeneratorBase.Expressions.cs:83-92`); `ParameterReference(Name, PositionalIndex, IsSynthetic)` (`Expressions.cs:64-67`); `SqlAstRewriter` visits all expressions (`src/TrinoSqlEngine/Ast/Visitors/SqlAstRewriter.cs:60-69`, `:333`, `:374`). `AstSimplificationVisitor.StructuralEquals` compares `ParameterReference` by `Name` only (`AstSimplificationVisitor.cs:353-354`), so a new `ListIndex` must be part of that comparison or run after simplification (A-1 does both). The canonical constants `1 = 1` / `1 = 0` are the only constant predicates emitted as text (`SqlDialectGeneratorBase.Expressions.cs:239-249`), so the empty-list rendering needs no new node. |
| Output budget vs long lists | **Finding.** `GovernedSqlCompiler.cs:207` caps emitted SQL at `MaxExpansionFactor` (64, `CompileRequest.cs:81`) times `max(raw length, 64)`; marker expansion of a long list exceeds it for short statements. Resolved by decision D-EXP (section 11, note 5). |
| Compile pipeline order | **Confirmed** (`GovernedSqlCompiler.cs:131-190`): parse, build, validate, simplify (user tree only), `LiteralColumnTypeAnnotator`, `AstSecurityVisitor` (injection), coverage verification, `DialectCapabilityValidator`, emit. The list expander slots in after simplification and before the annotator (A-1). |
| Declared-query header: `string[]` ignored, unknown types become `string` | **Confirmed.** `ParamHeaderRegex` (`src/Autheris.Application/SqlEndpoints/Services/SqlEndpointLoader.cs:28-31`) accepts `[a-zA-Z0-9_]+` as type; `ParseParamHeader` returns silently on no match (`:525-526`) and the `switch` default is `typeof(string)` (`:542`). The default value is parsed from `Trim(' ', '"', '\'')` text (`:530`, `:546-562`), which cannot carry a JSON array. A load error throws from `ParseSqlContent` (`:155-...`) and is logged per file by `LoadAll` and `SafeLoadFile` (`:76-85`, `:154-163`); hot reload keeps the previous definition. |
| Informal type names in the repository | **Grepped (Q-128-2).** Declared-query headers with `name: type` form exist only in tests (`tests/Autheris.Tests.Unit/SqlParameterPagingTests.cs:15-16`: `int`). The documented example uses the **description-only** form without colon (`docs/features/f-sql-01-declarative-sql-endpoints.md:35-36`: `-- @param department Department code ...`), which `ParamHeaderRegex` does not match today and which must stay ignored (A-8). The procedure parser uses its own space-separated grammar with the SQL type set `int, bigint, smallint, tinyint, bit, decimal, numeric, float, real, nvarchar, varchar, nchar, char, uniqueidentifier, date, datetime, datetime2, smalldatetime, datetimeoffset` (`src/Autheris.Application/Procedures/Services/ProcedureDefinitionParser.cs:272-322`) and **refuses** unknown types; stored procedures take no list parameter and are out of scope. No informal name beyond the existing switch occurs in the repository, so the alias list in A-8 is the existing switch plus the procedure vocabulary (one vocabulary for both header kinds). |
| REST endpoint input handling | **Confirmed, with a finding.** `HandleGetEndpoint` collects `rawInputs[k] = v.ToString()` (`src/Autheris.Api/Endpoints/SqlEndpointRoutes.cs:343`); `StringValues.ToString()` joins repeated keys with a comma, so `?d=a&d=b` arrives as the single string `a,b`. FR-128-4 (repeated key) requires a change here. `HandlePostEndpoint` deserializes the body to `Dictionary<string, object?>` (`:421`), so values are `JsonElement` (arrays arrive as `JsonElement`). `SqlEndpointExecutionService.ExecuteEndpointAsync` coerces each declared parameter by `CoerceValue` on `ClrType` (`src/Autheris.Application/SqlEndpoints/Services/SqlEndpointExecutionService.cs:56-77`, `:93-121`) and calls `ExecuteQueryBufferedAsync` with `Parameters` (`:79-90`). OpenAPI and listing read `ClrType` (`SqlEndpointRoutes.cs:211`, `:229`, `:235`). |
| Other entry points | **Verified.** WebSQL and the Trino route share one DTO (`WebSqlRequestDto.Parameters`, `src/Autheris.Api/Endpoints/WebSqlEndpoints.cs:23-25`; `WebSqlEndpoints.Execution.cs:81`, `:122`) and the Trino route runs the same executor through the statement manager; they get lists for free once the executor accepts them. Arrow export (`ArrowExportEndpoints.cs:187`), Flight SQL (`ArrowFlightSqlServer.cs:198`), MCP SQL tools (`ApiDispatcherService.cs:177`, `McpToolExecutionHandler.cs:107`) and async jobs (`AsyncQueryJobBackgroundWorker.cs:242`) construct `GovernedSqlQueryRequest` **without** `Parameters`; they cannot carry a list and an unsupplied `@x` is not rewritten (`NormalizeClientParameters:386`), so they stay as they are. Gateway-planned table reads pass internal scalar parameters (`GovernedDataQueryService.cs:193`, `SqlTableReadExecutor.cs:102`, `:226`, `GovernedAggregationService.ValidationAndExecution.cs:373`) through the same executor and must never see list semantics (a planned value that is a collection is refused, A-4). |
| Federation | **Confirmed.** A cross-catalog statement throws `CrossSourceRoutingException` inside the rewriter (`GovernedSqlRewriter.cs`, stage 2) and `GovernedSqlExecutionService` re-dispatches the **same** request to the federated executor (`GovernedSqlExecutionService.cs:110-116`, `:131-137`, `:148-154`), which does not use `request.Parameters` at all. A list parameter would be silently ignored there; FR-128-8 requires a stable refusal until #131 (A-9). |
| Declared `uuid`/`guid` parameter | **Pre-existing defect found.** `ParseParamHeader` maps `guid`/`uuid` to `typeof(Guid)` (`SqlEndpointLoader.cs:538`), `CoerceValue` returns a `Guid`, but `InferType` has no `Guid` case and throws `SecurityException("Unsupported client parameter value type")` (`ICompiledSqlBinder.cs:241-257`). `guid[]` would inherit it; the plan fixes both with one line (WP-2, A-3). |
| Error mapper contract | **Confirmed.** `SqlCompilerErrorMapper` (`src/Autheris.Application/Sql/Errors/SqlCompilerErrorMapper.cs:62-135`) returns a stable code and the **fixed** message `GovernedSqlErrorCodes.FixedMessage(code)` (`src/TrinoSqlEngine/Ast/Emit/GovernedSqlException.cs:42-68`); its documented rule is "never counts". Rule order matters: `SecurityException` maps to 403 (`:123`), `SqlLimitExceededException` to 422 `SQL_LIMIT_EXCEEDED` with the limit kind (`:121`). Surfaces: WebSQL, global handler, Flight, MCP, statements, jobs (`GatewayExceptionHandler.cs:50`, `WebSqlEndpoints.Governance.cs:38`, ...). V-128-9 (state bound and count) conflicts with the "never counts" rule; resolved in A-7. |
| AST position check "after parse, before compile" (V-128-3) | **Refined.** The compiler already owns the user AST between build and security injection; the check is done there (A-1), after the cheap application-side shape validation and before any connection or policy emission. A separate pre-compile parse in the application would parse twice. |

No PRD veto has to be reverted. Three PRD statements are refined: (1) V-128-5 budget source (compiler capability table, not `IParameterBudgetProvider`); (2) FR-128-6 "binder expands" becomes "AST expansion in the compiler, binder reads indexed elements" (the binder cannot change the SQL text); (3) V-128-9 counts (A-7).

---

## 2. Architecture Decisions

### A-1 Expansion happens in the compiler on the AST, keyed by a value-free list shape
`CompileRequest` gets one new property `ClientLists` (a `ClientListShapes` value: list name to expanded element count, names compared case-insensitively). A new engine pass `ClientListExpander` (`SqlAstRewriter`) runs in `GovernedSqlCompiler.CompileCore` as pass `"lists"` **after step 5 (simplify) and before step 6 (annotator, injection)**:
1. An `InListExpression` whose `Items` is exactly one synthetic `ParameterReference` named in `ClientLists` is replaced by `InListExpression(Operand, [ParameterReference(name, ListIndex: 0..n-1, ListCount: n)], IsNotIn)`.
2. Count `0` (empty list) replaces the node by the canonical `1 = 0` (`IN`) or `1 = 1` (`NOT IN`) `BinaryExpression` of integer literals, which the generators already emit as constant text without binds (`SqlDialectGeneratorBase.Expressions.cs:239-249`).
3. A residual pass rejects every synthetic `ParameterReference` named in `ClientLists` that is **not** a `ListIndex` item, with `ClientListException(SQL_LIST_PARAM_POSITION)`. This one rule covers `=`, function arguments, `LIMIT`/`OFFSET`/`ORDER BY`, `CASE`, `VALUES`, the select list, the left side of `IN`, `IN (@list, 1)` and `IN (@a, @b)` with a list (one form only, V-128-3). `= ANY(@list)` is not in the grammar subset and stays rejected as an unsupported construct.
4. A parameter that is **not** declared a list keeps today's scalar behaviour in `IN (@scalar)`.
Rejected alternatives: textual expansion before parsing (position cannot be proven without the AST, and it would corrupt the `__param_` normalization); a list marker node expanded by the binder (the binder cannot change SQL text or ordinals, and the cache key would still need the count); compiling per request without the template cache (loses the cache for all list statements).
Cache effect: `CompileCacheKey` includes the shape, so each distinct list length is one template (R-2). The same `@list` used twice in one statement produces the same markers for both `IN` predicates (dedup key includes the index) and costs `n` binds, not `2n`.

### A-2 Slot identity: `ListIndex` and `ListCount` travel with the slot
`ParameterReference` gets `int? ListIndex = null, int? ListCount = null` (appended optional members; `AstSimplificationVisitor.StructuralEquals` compares them too). `SqlEmitterContext.BindClient` includes the index in the `DedupKey` (field `Number`) and passes both to `Bind`. `BoundParameter` and `SqlSlot` get `int? ListIndex = null, int? ListCount = null` (appended), copied in `CompiledSqlTemplate.ToSlots`/`Rebind`. The audit digest is unchanged (it hashes the SQL text and `(ordinal, type, origin)`; the count is implicit in the text). The `ParameterOrigin` enum stays as it is (no new member): a list element is `ClientNamed` with an index.

### A-3 Binder: indexed element, exact count, element type from the list
`BindCore` for `ClientNamed`: if `bound.ListIndex` is set, the client value must be a `ClientListValue` with `Count == bound.ListCount` and `ListIndex < Count`; the element is `list[ListIndex]` and the slot type is `list.ElementType` (never `InferType` of one value). Any mismatch (scalar for a list slot, list for a scalar slot, count differs) fails closed: `MissingClientParameterException` for an absent value, `ClientListException(SQL_LIST_PARAM_TYPE)` for a shape mismatch. The exact-count check removes the silent-truncation class (a longer list than compiled is refused, not cut). The checked-DML path shares `BindCore` (`BindForCheckedExecution`, `ICompiledSqlBinder.cs:170-171`), so `UPDATE ... WHERE id IN (@ids)` works identically. Also: `InferType` gains `Guid => SqlParameterType.String` (fixes the scalar `uuid` defect found in section 1; `Configure` renders `Convert.ToString` = canonical "D" text). `ValidateValue` (Oracle empty-string rule, `:419-427`) is not touched: it only concerns Tenant/Policy origin.

### A-4 Normalization: one gateway-owned function, two stages
`ListParameterNormalizer` (Application, new, pure) turns a raw value into a `ClientListValue`:
- Accepts `JsonElement` arrays, and CLR `IReadOnlyList<object?>` / `string[]` / other non-string `IEnumerable` produced by the declared-endpoint layer; refuses nested arrays, objects, and `NULL`/undefined elements (`SQL_LIST_PARAM_NULL`, V-128-4) and a count above the **configured** maximum before any element is copied (`GetArrayLength()` first; `SQL_LIST_PARAM_TOO_LARGE`).
- Type class (V-128-2): without declaration, all elements must be one of `string`, `number`, `boolean`; numbers become `Int64` when every element is integral, else `Decimal` (all elements converted to `decimal`), `Double` only if a value is not representable as `decimal`. Mixed classes: `SQL_LIST_PARAM_TYPE`. With a declared element type (declared queries), each element is coerced with the same rules as the scalar `CoerceValue` and the list gets the declared element type (`int` -> `Int32`, `long` -> `Int64`, `decimal` -> `Decimal`, `double` -> `Double`, `bool` -> `Boolean`, date types -> `TimestampTz`, `guid` -> `String` in canonical "D" form, `string` -> `String`).
- De-duplication after validation: exact equality (ordinal for strings, value for numbers), **first occurrence order preserved**, before the count check (FR-128-6, AC-128-2). Case-insensitive collations may still match equal-ignoring-case values; both rows are returned either way (duplicates are harmless in `IN`).
- Limit on list-valued parameters per request: `MaxListParameters` (default 8), so many large lists cannot be smuggled in unused parameters.
`WebSqlParameterValues.Normalize` stays scalar-only (existing test, planned reads, other callers). A new `WebSqlParameterValues.NormalizeParameter(raw, limits)` calls it for scalars and the normalizer for arrays. `GovernedSqlExecutor.BuildClientValues` uses `NormalizeParameter` **only when** `request.PlannedTableRead` is false; for planned reads and any non-`JsonElement` collection from a gateway component it keeps `Normalize` and a collection value is refused (`SQL_LIST_PARAM_POSITION`), so internal parameters can never turn into lists.

### A-5 One list value type, no payload in diagnostics
`ClientListValue` (`TrinoSqlEngine.Ast.Emit`, public sealed, immutable): `ElementType` (`SqlParameterType`), `Count`, indexer returning the already-normalized scalar. `ToString()` prints only type and count (INV-16 style, like `BoundParameter.ToString`, `CompiledSql.cs:31`). It is constructed through a factory that re-checks non-null elements and that every element has the CLR type expected for `ElementType`, so the binder never trusts an arbitrary object.

### A-6 Limits and budget: two stages, one formula
`ListParameterLimits.EffectiveMax(configured, capabilities) = min(configured, capabilities.MaxBindParameters - 100, capabilities.MaxInListItems ?? int.MaxValue)` (owner decision Q-128-1: default 1000, configurable, hard cap = budget minus 100; the Oracle in-list rule is added because the validator would reject above it). Results: SQL Server 1,000 (cap 2,000), Oracle 1,000, Databricks 900, PostgreSQL/DuckDB 1,000 for the default, SQLite 1,000.
- Stage 1 (executor, no dialect): shape rules and the **configured** maximum.
- Stage 2 (rewriter, after the dialect is resolved, before `CompileRequestFactory.Create`): the **effective** maximum per list; a configured value above the hard cap is clamped (never exceeded) and logged once at Warning per dialect without values.
- Total budget: left to `SqlEmitterContext.Bind` (`SqlEmitterContext.cs:187`), which counts tenant, policy, mask, literal and client binds in one counter; overflow is a `SqlLimitExceededException(BindParameters)` before any connection is used, mapped to `SQL_LIMIT_EXCEEDED` (limit kind `BindParameters`). The expander additionally refuses a single list larger than `MaxBindParameters` before emission so a hostile shape cannot allocate a huge AST.
The padding seam (Q-128-4): `ClientListShapes.Create` takes its element counts from `ListShapeSizer.Size(count)`, an identity function in this item (default off, PRD recommendation). A later bucket policy (power of two, as `KeySetPadding` of PLAN-GOV-107) changes only that function and must re-apply the effective maximum to the **padded** count. Not implemented now: it needs a measured plan-cache effect (Q-128-4 stays open for the owner, recommendation unchanged).

### A-7 Error contract: stable codes, fixed messages, counts only where the caller already knows them
New `GovernedSqlErrorCodes` (engine) with fixed messages in `FixedMessage`: `SQL_LIST_PARAM_POSITION`, `SQL_LIST_PARAM_TYPE`, `SQL_LIST_PARAM_NULL`, `SQL_LIST_PARAM_TOO_LARGE`, `SQL_LIST_PARAM_UNSUPPORTED` (federation, A-9). The loader-time code `SQL_PARAM_TYPE_UNKNOWN` is a load diagnostic (A-8), not an HTTP code. One new exception `ClientListException : ArgumentException` (engine, carries only the code) so it is audited as a rejection by `IsAuditedRejection` (`GovernedSqlExecutor.cs:651-654` includes `ArgumentException`) and is **not** a `SecurityException` (a caller mistake is a 400, not a 403 policy hit). One new mapper row `e is ClientListException -> Of(400, code)` placed **before** the generic `SecurityException` row (`SqlCompilerErrorMapper.cs:123`). V-128-9 is resolved as follows: the fixed messages name the violated rule and point to the documented maximum; the **maximum** is published in the catalog, OpenAPI and analysis output (FR-128-7), and the received count is not echoed through the shared mapper because its contract is "never counts" and a count would leak through every mapped surface. This is a deliberate, minimal deviation from V-128-9 (owner flag F-2). Declared-endpoint handlers (`SqlEndpointRoutes.HandleGetEndpoint`/`HandlePostEndpoint`) additionally map `ClientListException` through the mapper so the code is visible (today they answer `ArgumentException` with a generic or raw message).

### A-8 Declared queries: strict header grammar, alias list, list defaults
Grammar of the `-- @param` value (only for the **colon form** `name: type...`; the description-only form without colon, as in `f-sql-01`, stays ignored as today):
`name ':' type [ '[]' ] [ '!' ] [ '=' default ]`.
- `type` is matched against the closed alias map: `int|integer|int32|smallint|tinyint` -> int; `long|bigint|int64` -> long; `decimal|numeric|money` -> decimal; `float|double|real` -> double; `bool|boolean|bit` -> bool; `date|datetime|datetime2|smalldatetime|datetimeoffset|timestamp` -> `DateTimeOffset`; `guid|uuid|uniqueidentifier` -> guid; `string|text|varchar|nvarchar|char|nchar` -> string (case-insensitive). Anything else, a malformed `[]`, a size suffix such as `varchar(50)`, or a line that starts as a colon declaration but does not parse **refuses the file** with a `FormatException` naming file and parameter (`SQL_PARAM_TYPE_UNKNOWN` diagnostic; no value text). The existing silent `typeof(string)` fallback (`SqlEndpointLoader.cs:542`) and the silent `if (!match.Success) return;` (`:526`) are removed for colon lines. Rationale for the alias list: it is the union of the current switch (which already knows `int/long/decimal/float/bool/date/guid` families) and the procedure vocabulary (`ProcedureDefinitionParser.cs:299-310`), and the repository contains no other informal name (section 1).
- Default for a list: `= ["a", "b"]` is parsed with `System.Text.Json` (the current `Trim(' ', '"', '\'')`, `:530`, would corrupt it), every element validated through the same normalizer with the declared element type; an invalid default refuses the file. A list default may be `[]` (empty list). A scalar default stays as today.
- `SqlEndpointParameter` (`src/Autheris.Domain/Model/SqlEndpointModels.cs:10-19`) gets `bool IsList = false` (appended; `ClrType` is the **element** type, `DefaultValue` an `IReadOnlyList<object?>` for lists). The Domain has no engine dependency, so conversion to `ClientListValue` happens in `SqlEndpointExecutionService`.
- Execution: a list-declared parameter accepts a JSON array (POST), a repeated query key (GET) or a single value as a one-element list; a comma-separated value is **one** string, never split (FR-128-4). A scalar-declared parameter given an array or a repeated key is refused (`SQL_LIST_PARAM_TYPE`). `HandleGetEndpoint` stops joining: a key with more than one value becomes `IReadOnlyList<object?>` of strings, a single value stays a string (`SqlEndpointRoutes.cs:343`). The executor receives both `name` and `@name` keys today (`SqlEndpointExecutionService.cs:75-76`); the list value is stored under both (`BuildClientValues` keeps the first spelling).
- The AST analysis attaches `TargetColumn`, `TargetTable` and operator `IN`/`NOT IN` to a parameter that is the sole item of an `IN` list (`SqlParameterExtractor` only handles comparisons today, `SqlParameterExtractor.cs:244-285`) so the catalog and OpenAPI can describe it (FR-128-7).

### A-9 Federation and other transports
- Cross-source (federated) routing: when `GovernedSqlExecutionService` falls through to the federated executor (`:110-116`, `:131-137`, `:148-154`), a request that carries any list-valued parameter is refused with `SQL_LIST_PARAM_UNSUPPORTED` before dispatch (FR-128-8). The guard is one helper `ClientListParameters.RejectForFederation(request)` called from the three catch blocks, so #131 later replaces exactly that helper.
- Transports without `Parameters` (Arrow, Flight SQL, MCP SQL tools, async jobs) are unchanged and documented as scalar/no parameters; no code change, one regression test asserts a list-valued `Parameters` entry on a planned read is refused (A-4).
- Trino protocol: lists work through the shared `WebSqlRequestDto.Parameters` (same executor); no Trino-native prepared statement support exists and none is added.

### A-10 Reuse contract for #129 (caller attribute lists) and #131 (federated parameters)
The mechanism is split so each consumer takes exactly the layer it needs; nothing in #128 is WebSQL-specific below the application layer.

| Layer | Artifact | #128 use | #129 reuse | #131 reuse |
|---|---|---|---|---|
| Value carrier | `ClientListValue` (A-5), `ListParameterNormalizer` (A-4) | client arrays | attribute provider returns list attributes through the same normalizer (type homogeneity, no `NULL`, de-dup, count) | federated parameter values normalized once, then pushed to sources |
| Limits | `ListParameterLimits.EffectiveMax` (A-6) | client cap | attribute list cap: same formula with its own configured maximum | key/parameter batches stay bounded by the same hard cap |
| Shape | `ClientListShapes` + `ListShapeSizer` | cache key, expansion count | not used (policy lists are expanded by `AdvancedRlsPredicateBuilder.InLists`/`PolicyParameterNamer`, origin `Policy`, polarity rules of FR-129-8) | not used (federation uses `KeyBatching`) |
| Compiler pass | `ClientListExpander` + `ListIndex` slots | client `IN` lists | not used | optional: a federated statement that keeps a client list for the source SQL may reuse the pass as-is |
| Polarity | **client**: empty `IN` -> `1 = 0`, `NOT IN` -> `1 = 1` | yes | **must not reuse**: policy lists keep ALLOW = no rows, DENY = all rows (RR-L4-01, `TypedRowFilterBuilder.cs:139`) | n/a |
The rule for both consumers: reuse the value, limit and (client-origin) expansion layers; never route a policy-origin value through `ParameterOrigin.ClientNamed`.

---

## 3. Contracts (fixed up front, first commit "contracts")

### C-1 Engine (`src/TrinoSqlEngine`)
```csharp
// Ast/Emit/ClientListValue.cs (new)
public sealed class ClientListValue
{
    public SqlParameterType ElementType { get; }
    public int Count { get; }
    public object this[int index] { get; }                       // already normalized, never null
    public static ClientListValue Create(IReadOnlyList<object> elements, SqlParameterType elementType);   // validates non-null and CLR type per ElementType
    public override string ToString();                             // "ClientListValue { ElementType = X, Count = n }" only
}

// Ast/Emit/ClientListShapes.cs (new)
public sealed class ClientListShapes
{
    public static ClientListShapes Empty { get; }
    public static ClientListShapes Create(IReadOnlyDictionary<string, int> expandedCounts);   // names case-insensitive; counts via ListShapeSizer.Size
    public bool TryGetCount(string name, out int count);
    public string Canonical { get; }        // public: lower-cased "name:count" pairs, ordinal sorted; the only public property, so CompileCacheKey serializes it
}

// Ast/Emit/ClientListException.cs (new)
public sealed class ClientListException : ArgumentException { public string Code { get; } }   // code only; fixed message from GovernedSqlErrorCodes

// CompileRequest.cs
public ClientListShapes ClientLists { get; init; } = ClientListShapes.Empty;

// Ast/Nodes/Expressions.cs
public sealed record ParameterReference(string Name, int? PositionalIndex = null, bool IsSynthetic = false, int? ListIndex = null, int? ListCount = null) : Expression;

// Ast/Emit/CompiledSql.cs (BoundParameter) and CompiledSqlTemplate.cs (SqlSlot): append  int? ListIndex = null, int? ListCount = null
// Ast/Visitors/ClientListExpander.cs (new, internal)  pass "lists": expansion + residual-position check
// Ast/Emit/GovernedSqlException.cs: five new code constants + FixedMessage rows
```
`CompileCacheKey.Write` serializes `ClientListShapes` through its public properties (only `Canonical`); a mutation test removes the property from the key and must fail.

### C-2 Application (`src/Autheris.Application`)
```csharp
// Sql/ListParameters/ListParameterLimits.cs (new, pure)
public readonly record struct ListParameterLimits(int ConfiguredMaxValues, int MaxListParameters)
{
    public static ListParameterLimits From(WebSqlOptions options);
    public static int EffectiveMax(int configured, DialectCapabilities capabilities);   // A-6 formula
}

// Sql/ListParameters/ListParameterNormalizer.cs (new, pure)
public static class ListParameterNormalizer
{
    public static bool IsListCandidate(object? raw);                                    // JsonElement array or non-string collection
    public static ClientListValue Normalize(object raw, ListParameterLimits limits, SqlParameterType? declaredElementType);   // throws ClientListException
}

// Sql/WebSqlParameterValues.cs: add  NormalizeParameter(object? raw, ListParameterLimits limits)   // Normalize unchanged
// Sql/ListParameters/ClientListParameters.cs (new): Shapes(clientValues) -> ClientListShapes; RejectForFederation(request)
// Sql/Services/GovernedSqlRewriter.cs: RewriteCoreAsync(..., ClientListShapes? clientLists = null)   // internal, optional last parameter
// Sql/Policy/CompileRequestFactory.cs: Create(..., ClientListShapes? clientLists = null)
```

### C-3 Domain and declared queries
`SqlEndpointParameter(..., bool IsList = false)` (appended). `WebSqlOptions` gets the three options of section 6. `AuditEventTypes` unchanged (the existing `WebSqlQuery`/`WebSqlQueryDenied` records gain value-free fields, A-11).

### C-4 Audit and logs
`AddCompiledAuditFields` (`GovernedSqlExecutor.cs:674-685`) gains `list_params` = `{ name: count }` (names and counts only; list names are author-chosen identifiers, never values). `bind_count` already includes expanded binds. `LogGeneratedSql` Debug logs only `compiled.Sql` (markers, `GovernedSqlRewriter.cs:651`), never values; `BoundParameter.ToString` is already value-free.

---

## 4. Changed Files and Ownership (one developer; every file has exactly one work package)

### WP-1 - Engine: value, shapes, expansion, cache key
- New: `src/TrinoSqlEngine/Ast/Emit/ClientListValue.cs`, `ClientListShapes.cs`, `ClientListException.cs`, `src/TrinoSqlEngine/Ast/Visitors/ClientListExpander.cs`.
- Edit: `src/TrinoSqlEngine/CompileRequest.cs` (property), `GovernedSqlCompiler.cs:207` (list marker allowance, D-EXP), `Ast/Nodes/Expressions.cs` (`ParameterReference`), `Ast/Visitors/AstSimplificationVisitor.cs:353-354` (compare `ListIndex`), `GovernedSqlCompiler.cs` (pass `"lists"` between steps 5 and 6), `Ast/Emit/GovernedSqlException.cs` (codes and fixed messages), `CompilerHooks.cs` (`SecurityControl.ClientListPosition`, `ClientListCount` for mutation tests), `Ast/Emit/CompiledSqlTemplate.cs` (`SqlSlot`, `ToSlots`, `Rebind`; cache key needs no edit: reflection).
- Tests: `tests/TrinoSqlEngine.Tests/Compiler/ClientListExpansionTests.cs`, `ClientListCacheKeyTests.cs`, `ClientListMutationTests.cs`; `tests/Autheris.Tests.Architecture/CutoverArchitectureTests.cs` (`CompiledSqlIsOnlyConstructedByTheEngine` extended only if it enumerates constructors of the changed records).

### WP-2 - Engine: emitter and binder
- Edit: `src/TrinoSqlEngine/Ast/Buffer/SqlEmitterContext.cs` (`BindClient`, `Bind`, `DedupKey` index), `Ast/Emit/CompiledSql.cs` (`BoundParameter`), `Ast/Emit/ICompiledSqlBinder.cs` (`BindCore` list branch, `InferType` Guid), `Ast/Emit/CheckedDmlExecutor.cs` only if it reads slot fields directly (verify; likely no change).
- Tests: `tests/TrinoSqlEngine.Tests/Compiler/ClientListBinderTests.cs` (all six binders: indexed element, exact count, type from list, mismatch refusal, Oracle `BindByName` ordering with list slots, PostgreSQL positional ordering), extend `BinderMutationTests.cs`, `CompiledSqlTemplateCacheTests` rebind.

### WP-3 - Application: normalizer, limits, options
- New: `src/Autheris.Application/Sql/ListParameters/{ListParameterLimits,ListParameterNormalizer,ClientListParameters}.cs`.
- Edit: `src/Autheris.Application/Sql/WebSqlParameterValues.cs` (`NormalizeParameter`), `src/Autheris.Domain/Options/Protocols/RestApiOptions.cs` (three options in `WebSqlOptions`), the options validator for `WebSql` (locate with `grep WebSqlOptions` in `Autheris.Application`/`Api` validators; range checks).
- Tests: `tests/Autheris.Tests.Unit/Sql/ListParameters/ListParameterNormalizerTests.cs`, `ListParameterLimitsTests.cs`, extend `WebSqlParameterValuesTests.cs` (scalar contract unchanged).

### WP-4 - Application: executor, rewriter, factory, mapper, audit, federation guard
- Edit: `src/Autheris.Application/Sql/Services/GovernedSqlExecutor.cs` (`BuildClientValues` uses `NormalizeParameter`, planned-read refusal, shapes passed to the rewriter, audit field), `Sql/Services/GovernedSqlRewriter.cs` (parameter, stage-2 effective cap, pass shapes to `CompileRequestFactory`), `Sql/Policy/CompileRequestFactory.cs`, `Sql/Services/GovernedSqlExecutionService.cs` (federation guard at `:110`, `:131`, `:148`), `Sql/Errors/SqlCompilerErrorMapper.cs` (new row before `:123`).
- Tests: `tests/Autheris.Tests.Unit/Sql/ClientListWebSqlTests.cs` (golden SQL and binds per dialect through `GovernedTestSupport`), `ClientListPolicyIsolationTests.cs`, `Errors/SqlCompilerErrorMapperTests.cs` rows, `GovernedSqlRejectionAuditTests` rows, `Security/WebSqlClientParameterTests.cs` rows.

### WP-5 - Declared queries and REST endpoints
- Edit: `src/Autheris.Application/SqlEndpoints/Services/SqlEndpointLoader.cs` (grammar, alias map, list default, refusal, `MapClrTypeToName` writes `[]`), `src/Autheris.Domain/Model/SqlEndpointModels.cs` (`IsList`), `src/Autheris.Application/SqlEndpoints/Services/SqlEndpointExecutionService.cs` (list coercion, scalar-vs-list refusal), `src/Autheris.Api/Endpoints/SqlEndpointRoutes.cs` (GET repeated keys `:343`, listing `:211`, OpenAPI array schema `:229-236`, mapper in both handlers), `src/TrinoSqlEngine/Analysis/SqlParameterExtractor.cs` (IN-list association), `src/Autheris.Application/Governance/Analysis/QueryAnalysisService.Sql.cs` (surface list parameters with element type, required, default, effective maximum).
- Tests: `tests/Autheris.Tests.Unit/SqlEndpointListParameterTests.cs`, `SqlEndpointHeaderGrammarTests.cs`, extend `SrM22SqlEndpointLoaderTests.cs`/`SqlEndpointsTests.cs` (existing headers unchanged), `tests/TrinoSqlEngine.Tests/SqlParameterExtractorTests.cs` rows.

### WP-6 - Integration and gates
- Tests only: `tests/Autheris.Tests.Integration/ClientListExecutionTests.cs` (SQLite in the unit suite; PostgreSQL and SQL Server via the shared Testcontainers fixtures; Oracle where the fixture exists), SQL Server bind-budget fixture (2,100), G8 allocation gate and `CompilePathPerformanceTests` re-run (lead builds), SqlGate corpus unchanged.
- No source edits. Phase 6 (documentation expert) later: `docs/features/f-data-02-governed-websql.md`, `docs/features/f-sql-01-declarative-sql-endpoints.md`, `docs/configuration-guide.md`, master plan; not part of this plan's file ownership.

---

## 5. Fail-Closed Behaviour

| Condition | Outcome |
|---|---|
| Array element is `NULL`/undefined | `SQL_LIST_PARAM_NULL` (400), nothing executed |
| Mixed type classes, wrong element for the declared type, nested array/object, scalar-declared parameter given an array | `SQL_LIST_PARAM_TYPE` (400) |
| List used outside the sole item of `IN`/`NOT IN` (any other position, left side, mixed items) | `SQL_LIST_PARAM_POSITION` (400), at compile before emission |
| More distinct values than the configured or effective maximum, or more list parameters than `MaxListParameters` | `SQL_LIST_PARAM_TOO_LARGE` (400), never truncated |
| Statement total exceeds the dialect bind budget (tenant, policy, mask and list together) | `SQL_LIMIT_EXCEEDED`, limit kind `BindParameters` (422), before a connection is used |
| Oracle list above 1,000 | cannot happen (cap), and the validator would answer `SQL_LIMIT_EXCEEDED` (`InListItems`) |
| Binder receives a scalar for a list slot, a list for a scalar slot, or a list whose count differs from the compiled count | refuse (`SQL_LIST_PARAM_TYPE` / missing parameter), never bind a subset |
| List value in a planned table read or any gateway-internal parameter | refused (`SQL_LIST_PARAM_POSITION`) |
| List parameter on a federated (cross-source) statement | `SQL_LIST_PARAM_UNSUPPORTED` (400) until #131 |
| Declared query header with unknown type, malformed `[]`, size suffix, invalid list default | file refused at load with a diagnostic naming file and parameter; the previous definition of a hot-reloaded file is kept (`SafeLoadFile`), a new file is not registered |
| Empty list | `IN` -> `1 = 0`, `NOT IN` -> `1 = 1` (client polarity only) |
| Cache hit with a different list length | impossible: the length is in the key; a template rebind with a mismatched count fails in the binder |

---

## 6. Configuration

| Key | Type | Default | Notes |
|---|---|---|---|
| `WebSql:MaxListParameterValues` | int | `1000` | Owner decision Q-128-1. Range 1..10,000 validated at start. Effective per list: `min(value, MaxBindParameters - 100, MaxInListItems ?? inf)` (A-6); a larger value is clamped at runtime with one Warning per dialect. Applies to WebSQL, Trino route and declared queries (all run through `GovernedSqlExecutor`). |
| `WebSql:MaxListParameters` | int | `8` | Number of list-valued parameters per request. Range 1..64. |
| `WebSql:PadListParameters` | not introduced | - | Q-128-4: no padding in this item (default off per PRD); the sizing seam exists (A-6). |
No per-parameter maximum in the header (Q-128-3: no). Tuple `IN` and lists on both sides stay unsupported (Q-128-5: no).

---

## 7. TDD Test List (write red first, in this order)

Every test carries `[Trait("Category", "Security")]` where marked (S).

**Engine (WP-1, WP-2), `TrinoSqlEngine.Tests`**
1. `Expander_ReplacesSoleListItemWithIndexedReferences` (SQL Server, PostgreSQL, SQLite, Oracle, DuckDB golden text and ordinals).
2. `Expander_EmptyList_IsConstantFalse_AndNotIn_IsConstantTrue` (no binds emitted for the constant).
3. `Expander_ListOutsideInList_IsRejected` theory: `=`, function argument, `LIMIT`, `OFFSET`, `ORDER BY`, `CASE`, select list, left side of `IN`, `IN (@l, 1)`, `IN (@l, @m)` (S).
4. `Expander_ScalarParameterInIn_StaysScalar`.
5. `SameListUsedTwice_SharesBinds` (n binds, not 2n).
6. `CacheKey_ContainsListShape_AndDifferentLengthsAreDifferentTemplates`; mutation: drop the property from the key must fail (S).
7. `StructuralEquals_DistinguishesListIndexes` (`@l[0] = @l[1]` is not folded).
8. `Binder_ReadsIndexedElement_WithListElementType` for all six binders (mixed `Int64`/`Decimal` normalized to one type).
9. `Binder_RefusesScalarForListSlot_ListForScalarSlot_AndCountMismatch` (S), mutation `ClientListCount` disabled must fail.
10. `Binder_Guid_BindsCanonicalText` (scalar and list).
11. `Budget_TotalOfTenantPolicyMaskAndList_IsEnforcedAtEmission` on the SQL Server capability (2,100) (S).
11b. `ExpansionFactor_ListMarkersAreAllowed_PolicyExpansionStillBounded`: a 1,000-value list on a 60-character statement compiles; a policy that expands past the factor still fails with `PolicyExpansionFactor` (S).
12. `ClientListValue_ToString_ContainsNoValues`, `BoundParameter` unchanged.

**Application (WP-3, WP-4)**
13. `Normalizer_ConvertsJsonArrays_DeduplicatesKeepsOrder`.
14. `Normalizer_RejectsNullElement_Nested_MixedClasses_TooMany` (S), count checked before elements are copied (huge `JsonElement` array).
15. `Limits_EffectiveMax_PerDialect` (SQL Server 1,000/2,000 cap, Oracle 1,000, Databricks 900, config 5,000 clamps).
16. `WebSqlParameterValues_Normalize_StillRejectsArrays` (existing test unchanged).
17. `WebSql_ListQuery_ReturnsRowsForThreeValues` (AC-128-1, SQLite execution), one value, duplicates, maximum, empty (AC-128-2).
18. `WebSql_ValuesWithQuotesCommentsSeparators_NeverAppearInSqlAuditOrLogs` (AC-128-5, V-128-1) (S).
19. `RowFilterMaskAndTenant_ApplyUnchanged_PolicyBindsIdenticalWithAndWithoutList` (AC-128-6, two principals) (S).
20. `PlannedTableRead_ListValue_IsRefused` (S).
21. `Federated_ListParameter_IsRefused` (FR-128-8) (S).
22. `ErrorMapper_ClientListException_Maps400_BeforeSecurityRow` and fixed messages contain no counts (S).
23. `Audit_RecordsListNamesAndCounts_NoValues` (S).
24. `Trino_Route_AcceptsListParameter` (shared executor).

**Declared queries (WP-5)**
25. `Header_ListType_ParsedWithJsonDefault`; `Header_UnknownType_RefusesFile`; `Header_MalformedBrackets_RefusesFile`; `Header_SizeSuffix_RefusesFile`; `Header_DescriptionOnlyForm_StaysIgnored` (the `f-sql-01` example); `Header_AliasList_MapsToExpectedClrTypes`; existing `int` paging headers unchanged (AC-128-4).
26. `Rest_Post_ArrayBody_And_Get_RepeatedKey_AreLists`; `Rest_Get_CommaSeparatedValue_IsOneString`; `Rest_ScalarParameter_RepeatedKey_IsRefused`.
27. `OpenApiAndListing_ShowArrayWithElementTypeAndMaximum` (AC-128-7); analysis output rows.
28. `HotReload_InvalidHeader_KeepsPreviousDefinition`.

**Integration (WP-6)**
29. Same declared query returns the same rows on SQLite, PostgreSQL, SQL Server (Oracle if fixture) (AC-128-8).
30. SQL Server budget fixture: list that fits the maximum but exceeds 2,100 with tenant, policy and mask binds is refused, nothing truncated (AC-128-3).
31. G8 allocation gate and `CompilePathPerformanceTests` unchanged within their limits.

---

## 8. Work Packages for One Sequential Developer

Order is fixed (gate G-ONE); each WP ends green on its own tests, the lead builds once per item (G-BUILD).
1. **WP-1** contracts commit first (C-1 types and signatures with `NotImplementedException` bodies compile), then tests 1-7 red, then implementation.
2. **WP-2** tests 8-12 red, then emitter and binder.
3. **WP-3** tests 13-16, then normalizer, limits, options.
4. **WP-4** tests 17-24, then executor, rewriter, factory, mapper, audit, federation guard. Re-check that `RewriteCoreAsync` callers other than the executor (`ValidateAsync`, `CompileAsync`, `GovernedSqlRewriter.cs:125`, `:130`) pass no shapes.
5. **WP-5** tests 25-28, then loader, models, execution service, routes, analysis.
6. **WP-6** tests 29-31 (integration, gates). Hand-over note for Phase 6: documents to update (section 4, WP-6).
Gate per WP: the SqlGate and AST security suites plus the existing WebSQL, endpoint and binder tests stay green.

---

## 9. Risks

| ID | Risk | Mitigation |
|---|---|---|
| R-1 | `ParameterReference`/`BoundParameter`/`SqlSlot` record changes break `with` expressions or golden tests that construct them positionally | New members are appended optional; architecture test and the Compiler test project run in WP-1/WP-2 |
| R-2 | Template-cache churn: one template per distinct list length (LRU 512, `CompiledSqlTemplate.cs`), plus one DB plan per length | Default maximum 1000 bounds the shapes; padding seam A-6 allows power-of-two buckets later; measure in WP-6 test 31 |
| R-3 | SQL Server `varchar` column compared with an `nvarchar` list binds with implicit conversion (client parameters carry no column type, only literals do, `LiteralColumnTypeAnnotator.cs:197-212`) | Same behaviour as scalar client parameters today; follow-up: extend the annotator to `ParameterReference` (scalars and lists) as a separate item; documented |
| R-4 | Existing declared queries using informal types or `type(n)` fail to load | Alias list covers the current switch and procedure vocabulary; the repository contains none beyond `int`; loader error names file and parameter; breaking changes are allowed |
| R-5 | Hot path allocations (G8 gate) from the new binder branch and key material | Branch only for `ListIndex != null`; `ClientListShapes.Canonical` precomputed; gate run in WP-6 |
| R-6 | `NOT IN` with a nullable column and three-valued logic surprises callers | Documented SQL semantics; `NULL` elements are rejected so the list itself cannot poison the predicate (V-128-4) |
| R-7 | Case-insensitive collations make "duplicates" differ by case | Exact de-duplication only; harmless for `IN` (documented) |
| R-8 | #129/#131 start from a different expansion model and fork the mechanism | Section A-10 fixes the reusable layers and forbids routing policy values through `ClientNamed`; Phase 3 should confirm |
| R-9 | Large JSON arrays stress memory before validation (the body is already materialized as `JsonElement`) | Count read via `GetArrayLength()` before copying; existing 2 MB body limit on POST endpoints (`SqlEndpointRoutes.cs`), `MaxListParameters` bound |

---

## 10. Owner Flags and Open Questions

| ID | Question | Default used in this plan |
|---|---|---|
| Q-128-1 | List maximum | **Decided** by the owner: 1000, configurable, hard cap = dialect budget minus 100 (extended by the Oracle in-list limit) |
| Q-128-2 | Informal header types | Alias list in A-8 (repository grep: only `int` in colon-form headers), everything else refuses the file |
| Q-128-3 | Per-parameter maximum | No |
| Q-128-4 | Padding to size buckets | Not in this item; seam only; revisit after a measured plan-cache effect and alongside `KeySetPadding` of PLAN-GOV-107 |
| Q-128-5 | `LIKE ANY`, tuple `IN` | Not supported |
| F-1 | `PLAN-SQL-128` touches `GovernedSqlCompiler` (new pass) and the binder: security-relevant engine code; `AstSecurityVisitor.cs` is **not** modified | Owner review at Phase 5 |
| F-2 | V-128-9 deviation: the mapped error does not echo the received count (shared mapper contract "never counts"); the maximum is published in catalog/OpenAPI/analysis | Accept, or approve a count-carrying detail field for list errors only |
| F-3 | Pre-existing scalar `uuid` binding defect fixed together (A-3) | Accept |

---

## 11. Security Notes for Phase 3

1. **Splicing:** confirm that no path writes a list element into SQL text: elements exist only as `ClientListValue` and bound slots; the only text added by expansion is markers and the constants `1 = 0` / `1 = 1` (`EmittedSqlInvariantChecker` must accept them as today). Review the emitted-range registration for the new markers.
2. **Position proof:** the residual pass (A-1 step 3) is the sole guard that a list never reaches `=`, `LIMIT`, functions, `CASE`, `VALUES`; review its completeness against every `SqlAstRewriter` visit method and nodes that hide expressions (derived tables, CTEs, DML `SET`/`VALUES`, window frames). Mutation controls `ClientListPosition` and `ClientListCount` must be proven by failing tests.
3. **Policy isolation (V-128-8):** expansion runs before security injection but only rewrites user nodes; policy, tenant and mask parameters keep their origin and positions. Review ordering effects on ordinals (policy values are bound by name and rebound from `PolicyValues`) and the Oracle `BindByName` ordering with many list slots; test 19 and the PostgreSQL positional test are the evidence.
4. **Cache:** the shape is in the template key; confirm the key cannot collide across names/counts (`Canonical` format with explicit length prefixes or unambiguous separators), and that a template is never rebound with a different count (binder exact-count check).
5. **Denial of service and the expansion factor (design decision D-EXP):** bounded at three points (configured maximum before copying, hard cap per dialect, emission budget) plus `MaxListParameters` and the existing 2 MB body limit. The output budget at `GovernedSqlCompiler.cs:207` limits the emitted text to `MaxExpansionFactor` (default 64, `CompileRequest.cs:81`) times `max(input length, 64)`; a 1,000-value list adds about 6 characters per marker (about 6,000 characters) to a statement of, say, 100 characters (limit 6,400 minus the rest of the statement), so large lists would fail with `SQL_LIMIT_EXCEEDED` (`PolicyExpansionFactor`) long before the bind budget. The plan therefore **adds the list marker text to the allowance**: the limit becomes `factor * max(len, 64) + sum(count_i * MarkerAllowance)` where the sum comes from `request.ClientLists` (gateway-computed counts, already bounded by A-6) and `MarkerAllowance` is the dialect's maximum marker length plus separator (a constant, 12). This keeps the protection against policy expansion intact (policy growth is still bounded by the factor) and is covered by test 11b. Phase 3 should confirm that the extra allowance cannot be abused (counts are server-derived and capped; the allowance is not user-chosen text length).
6. **No oracle:** error codes distinguish caller mistakes only; no governed data is involved; counts are not echoed (F-2). Confirm audit `list_params` (names, counts) is acceptable.
7. **Confidentiality in diagnostics:** `ClientListValue.ToString`, exception messages, `LogWarning` on clamping and loader diagnostics contain no element text, no default text, no SQL text; assert with log capture (test 18).
8. **Federation and planned reads:** the guard A-9 and the planned-read refusal keep lists off paths that cannot honour them; confirm the federated executor really never sees `request.Parameters`.
9. **Declared queries:** the new header grammar is an input parser for operator-authored files; check the regex for catastrophic backtracking (keep the 2 s match timeout, anchored patterns) and that list defaults are validated at load, not at first call. `SanitizeHeaderValue` rules (`SqlEndpointLoader.cs`, directive injection) stay in force for `SyncDbtModelToFile`.
10. **Least privilege:** no table-valued parameters, no `STRING_SPLIT`, no array types: SELECT-only logins keep working.

---

## 12. Size Estimate

| Area | Production LOC | Test LOC |
|---|---:|---:|
| WP-1 engine value, shapes, expander, key, codes | 230 | 380 |
| WP-2 emitter and binder | 90 | 260 |
| WP-3 normalizer, limits, options | 200 | 280 |
| WP-4 executor, rewriter, factory, mapper, audit, federation | 130 | 330 |
| WP-5 loader, models, routes, analysis | 280 | 320 |
| WP-6 integration | 0 | 150 |
| **Total** | **about 930** | **about 1,720** |
Consistent with the PRD estimate **M** (about 900 production, 1,200 test LOC); the test growth comes from per-dialect binder tests and the security matrix. One sequential developer; no schema or DDL change, so no conflict with the shared files of gate G-SHARED except `GovernedSqlExecutionService.cs` and `CompiledSql.cs` (delivery tracker row I-5 lists `WebSqlParameterValues.cs`, `SqlEndpointLoader.cs`, `SqlEmitterContext.cs`, `CompiledSql.cs`; this plan adds `GovernedSqlCompiler.cs`, `ICompiledSqlBinder.cs`, `GovernedSqlExecutor.cs`, `GovernedSqlRewriter.cs`, `CompileRequestFactory.cs`, `SqlCompilerErrorMapper.cs`, `SqlEndpointRoutes.cs`; the tracker's file list for I-5 should be updated by the lead).

---

## 13. Changelog

| Revision | Date | Change |
|---|---|---|
| 1 | 2026-10-16 | Initial Phase 2 plan (status PROPOSED). |
| 2 | 2026-10-16 | Phase 3 security review appended (section 14): `SEC-128-01..14`, verdict APPROVED WITH CONDITIONS; no change to sections 1-13, the conditions of 14.4 are binding amendments for Phase 4. |
| 3 | 2026-10-17 | Phase 4 (WP-1..WP-6). G-PLAN-REBASE: the cited code facts hold on the branch (it carries #107 WP-A..C on top of `main`); line numbers drifted and were re-located by symbol. Deviations and drift, each recorded before the code that depends on it: see the bullets below. |

**Phase 4 deviations and drift (2026-10-17)**

- **Empty `IN` in DML is refused as well (A-1, C-1, M-1).** `DELETE/UPDATE ... WHERE col IN (@empty)` becomes `1 = 0`; the unchanged DML guard (`AstSecurityVisitor`, C-16) requires a column reference in the WHERE and so answers `SQL_UNFILTERED_DML` for it, exactly as for `NOT IN (@empty)` (`1 = 1`). Zero rows are affected either way (fail closed); the plan's "IN (@empty) affects zero rows" is therefore "is refused". A non-empty list in DML works (`CheckedDml_ListBinding_DeletesExactlyTheListedRows_OfTheTenant`). Pinned by `ClientListMutationTests` and the mutant `client-list-expands-after-guards`.
- **Phase 5 fixes (2026-10-17).** (1) REST GET: a repeated key with no resolvable endpoint definition (no registry or unknown endpoint) is refused as a 500 with the fixed error, never joined into a one-element list. (2) The second-line `RejectForFederation` calls in `GovernedSqlExecutionService` are pinned by `Federation_CrossSourceRoutedStatement_WithAList_IsRefused_BeforeTheFederatedServiceIsReached_BothEntries`. (3) A bad `-- @param` header rejects only its own file (test `Load_BadParamHeaders_RejectOnlyTheAffectedEndpoint_OthersKeepLoading`); no code change needed. (4) Wording clarification: the review text of §14 (T-1 evidence, C-1, M-1/M-12) says DML `IN (@empty)` "affects zero rows"; as built it is refused as `SQL_UNFILTERED_DML` (first bullet above); those places are annotated, not rewritten. (5) Audit gained `list_params_empty` (sorted names of empty lists, plain identifiers only, no values), because an empty list has no compiled slot. (6) `GovernedSqlExecutor` builds the referenced-name set once per request. The G8 allocation gate (`CompilePathPerformanceTests`, `Autheris.Tests.SqlCompilerGate`) has no list-bearing case, so it does not cover list requests; none was added. (7) Unused local removed from `ListParameterNormalizer.Coerce`.
- **Four mutation controls** `ClientListPosition`, `ClientListCount`, `ClientListExpandsBeforeGuards`, `ClientListMaxExpanded` (C-1, C-2, C-10) with three engine gate probes and four active mutants (`SecurityMutants`, `SecurityProbes`); the SQLGATE kill check is green. New catalog row `SEC-128` (kind Application) in `SecurityRegressionCatalog`.
- **Public surface of `ClientListShapes`:** besides `Canonical` (the only public property, hence the only cache-key member) it has the public method `MaxCount()`, which the Application needs for the stage-2 effective-maximum check; `IsEmpty` is internal. `ListShapeSizer` is an internal identity function (A-6 seam).
- **`ListParameterNormalizer.Normalize` has an optional trailing `bool guidStrings`** (C-2 contract amended): `SqlParameterType` has no GUID member, so a `guid[]` list is declared as `String` + canonical "D" text. `ListElementTypes.For` (internal) maps the declared CLR element type; `date`-family aliases map to `TimestampTz` with `AssumeUniversal` (C-8). `ListParameterLimits` carries `RawCeiling` (= min(10 x configured, 100,000)) and constants; no new option (14.7).
- **Options validation** is by `[Range(1,10000)]` / `[Range(1,64)]` on the two `WebSqlOptions` members (the start-up validator already runs `ValidateObjectRecursively`); no new validator class. `ListParameterLimits.From` additionally clamps.
- **Admission and counting (C-5, SEC-128-04):** `GovernedSqlExecutor.BuildClientValues` admits a list only on WebSql/Trino (a `JsonElement` array) or SqlEndpoints (a `ClientListValue`) with `PlannedTableRead == false`; everything else is `SQL_LIST_PARAM_POSITION`. `MaxListParameters` counts every list candidate supplied (referenced or not); only referenced lists are normalized and enter the shape (dropped lists cost nothing). Normalization now runs inside the audited rejection scope, so `SQL_LIST_PARAM_TYPE/NULL/TOO_LARGE` from the request are audited like compile-time refusals (the scalar "array is a 400" path is audited too, a small behaviour change). `NormalizeClientParameters` stays outside the scope as before.
- **Audit `list_params` is derived from the compiled slots** (SQL identifier names, compiled counts), not from client keys; an empty list has no slot and is not listed.
- **Provider-error log (C-7):** when a `ClientListValue` is bound, `SanitizeProviderError` logs error type and `DbException.ErrorCode` without the exception object; statements without lists log as before.
- **Federation (C-12):** the guard is `ClientListParameters.RejectForFederation(request.Parameters)` at the start of `FederatedDuckDbExecutionService.ExecuteQueryBufferedAsync` and `ExecuteGovernedQueryAsync` (raw parameters; `StreamRowsAsync` and the executor catch blocks reach them) and, as second line, in the two catch blocks of `GovernedSqlExecutionService` that pass a request (`ValidateAsync` has no parameters). The entries are unit-tested; the catch-block second line has no direct test (it needs a two-source routing fixture, `GovernedSqlExecutionService` is only reachable through `CrossSourceRoutingException`).
- **Declared queries (A-8, C-14):** the trigger is `^[A-Za-z0-9_]+\s*:`; `-- @param name: type[[]][!] [= default]` is anchored with the 2 s timeout, aliases as in A-8, list defaults are a flat JSON array normalized at load with the raw ceiling (`MaxDepth = 2`), every failure is a `FormatException` naming file and parameter only. The dbt sync writes `[]` for a list parameter and omits list defaults. `HandleGetEndpoint` resolves `ISqlEndpointRegistry` from `RequestServices` (optional; without it every key stays joined): a repeated key becomes a list only for a list-declared parameter, a repeated key of a scalar parameter is `SQL_LIST_PARAM_TYPE`, undeclared keys (format, paging) stay joined. Both endpoint handlers map `ClientListException` before `ArgumentException` (400, code and fixed message, also in Production).
- **Listing and OpenAPI (FR-128-7):** the public listing carries `isList` and `maxItems` (the configured maximum; the dialect-clamped effective number is not published, 14.7 F-2); OpenAPI renders `type: array`, `items`, `maxItems`, `style: form`, `explode: true`. `HandleOpenApiSpec` is internal with an optional options parameter. `SqlParameterExtractor.EnterInList` associates a sole `IN` / `NOT IN` item with its column and operator.
- **Not done:** `QueryAnalysisService.Sql.cs` (WP-5 listing) was not changed: its output has no endpoint-parameter surface to extend (it reports tables and columns only), so surfacing list parameters there would need a new output contract; the endpoint listing and OpenAPI are the surfaces that carry list parameters. Oracle integration test not added (the Oracle behaviour is covered by the binder tests with the recording command).
- **Integration (WP-6):** PostgreSQL and SQL Server list tests live in the existing `AstCompilerPostgreSqlExecutionTests` / `AstCompilerSqlServerExecutionTests` (shared fixtures) instead of a new file; the SQL Server budget case runs at compile time (1,999 values execute, plus 150 literals is `BindParameters`). SQLite execution is covered by `ClientListWebSqlTests` through the whole pipeline.
- **Test-infrastructure edits:** `CompileApiTests.CompileRequest_EveryPropertyChangesCacheKey` lists the new `ClientLists` property; `SecurityCoverageTests.NoRewriterDescendsIntoSecurityPredicate` skips `ClientListExpander` (it needs constructor arguments, opaque nodes are covered by the base class); `ClientListArchitectureTests` (IL scans) pins the creation sites (SEC-128-11).

---

## 14. Phase 3 Security Review (2026-10-16)

**Reviewer role:** `csharp-security-expert` (Phase 3, delivery step S-1). **Reviewed:** `PLAN-SQL-128` revision 1 against PRD section 2 (V-128-1..9, owner decision Q-128-1) and the code at `73155ff5` (`docs` branch on top of the `8bb511a1` tree the plan cites; the cited code is unchanged). Every claim below that the review relies on was re-read in the code and carries `file:line`. Source files were not changed.

**Verdict: APPROVED WITH CONDITIONS.** The architecture is sound: elements exist only as bound slots, the shape (not the values) is in the cache key, the position rule is one residual check, the compile budget is already total. Two items are High (DML ordering hazard of the empty-list constant, expansion amplification by repeated use) and must be fixed in the design before the first line of Phase 4; the remaining conditions are Medium/Low and mostly tighten what the plan already intends.

### 14.1 Threat model

| ID | Actor | Goal | Entry | Plan control | Residual (finding) |
|---|---|---|---|---|---|
| T-1 | WebSQL/REST caller | Inject SQL through a list element | `Parameters` JSON array, GET repeated key, POST array | Elements only as `BoundParameter`; text gets markers and `1 = 0`/`1 = 1` | none for splicing; type/NUL/Oracle edge cases (SEC-128-06, -08) |
| T-2 | Caller | Put a list where a scalar is expected (`=`, `LIMIT`, function) to change semantics or crash the binder | SQL text + array value | Residual pass, binder backstop | completeness proof needed (SEC-128-10) |
| T-3 | Caller | Delete/update every row via an empty list (`NOT IN (@empty)` becomes `1 = 1`) | DML over WebSQL | Expansion before security visitor | ordering is the only guard (SEC-128-01) |
| T-4 | Caller | Exhaust memory/CPU (amplification, cache churn, huge arrays) | repeated `IN (@l)`, many lengths, 2 MB arrays | max per list, `MaxListParameters`, bind budget | SEC-128-02, -03, -04, -09 |
| T-5 | Caller | Read other callers' values, bypass policy | template cache, policy lists | value-free template, expansion before injection | name/ordering tests (SEC-128-11, -13) |
| T-6 | Internal component | Turn a gateway-internal value into a list | planned reads, endpoint layer | `PlannedTableRead` refusal | flag is not set on every planned path (SEC-128-05) |
| T-7 | Caller | Learn values/oracles through errors, logs, audit | error mapper, driver messages, `CoerceValue` | fixed messages, no counts | SEC-128-07 |
| T-8 | Caller | Make a list silently ignored on a path that cannot honour it (federation) | cross-source SQL | A-9 guard in three catch blocks | not all dispatch paths (SEC-128-12) |
| T-9 | Operator-authored file | Silent type downgrade, ReDoS, invalid default | `-- @param` header | strict grammar | SEC-128-14 (Low) |

### 14.2 What was verified and holds

1. **No splice path.** `SqlEmitterContext.BindClient` (`SqlEmitterContext.cs:158-172`) returns a marker for a `BoundParameter` whose value is `null` in the template; the only constant text is `1 = 1`/`1 = 0` emitted by `FormatBinaryExpression` (`SqlDialectGeneratorBase.Expressions.cs:239-249`, only for integer literals 1 = 0/1). The binder reads client values only for `ParameterOrigin.ClientNamed` and only by `SourceName` (`ICompiledSqlBinder.cs:196-207`, SEC-ADG-19); the name is constrained to `[A-Za-z_][A-Za-z0-9_]{0,127}` and a reserved-prefix check (`SqlEmitterContext.cs:25-31`, `:166`). Collation cannot be influenced: a bound parameter has the column's collation precedence for comparison.
2. **Cache key shape vs values.** `CompileCacheKey.Write` serializes every public instance property of a `TrinoSqlEngine` type, length-prefixes strings and throws for unknown foreign types (`CompiledSqlTemplate.cs:243-345`, `:330-345`); a `ClientListShapes` with the single property `Canonical` is therefore in the key by construction. Names are limited to `[A-Za-z0-9_]` and counts are digits, so `name:count` pairs are unambiguous. Templates are value-free (client slot value `null`, `SqlEmitterContext.cs:171`). Two requests with the same SQL and same lengths but different values share one template and bind their own values (the binder reads the request dictionary); different lengths are different templates. No cross-request value leakage exists in the design.
3. **Empty-list polarity** is the correct three-valued result for the empty set: `x IN ()` is false, `x NOT IN ()` is true, also for `x = NULL`; `1 = 0`/`1 = 1` match. `NOT IN` with non-null elements and a NULL column value yields UNKNOWN (row excluded): plain SQL semantics, cannot widen access because policy predicates are ANDed by injection, document only (R-6).
4. **Bind budget is already total** (`SqlEmitterContext.cs:187-190`) and `MaxInListItems` is enforced per `IN` by the validator (`DialectCapabilityValidator.cs:44`).
5. **Rewriter completeness for `ParameterReference`.** `SqlAstRewriter.Visit` dispatches every user expression node (`SqlAstRewriter.cs:55-100`): `QuantifiedComparisonExpression`, `CaseExpression`, `FunctionCallExpression` (arguments, filter, `OrderWithin`, window), `WindowSpecification` (partition, order), `PaginationClause` (offset, limit, `:547-553`), `OrderBy`, `GroupBy`, `Substring/Trim/Position/DateFunction`, DML nodes, CTEs, derived/lateral sources, `VALUES`. Nodes it does not descend into are leaves or opaque security nodes (`NamedBindMarkerExpression`, `TypedLiteralExpression`, `IntervalLiteralExpression`, `SecurityPredicateExpression`, `MaskExpression`, `PolicyParameterExpression`, `:62-65`, `:86-93`); `FrameBound.Offset` is a `long` (`Expressions.cs:206`), not an expression. The residual position pass is therefore complete for today's AST; the risk is future nodes (SEC-128-10).
6. **Policy isolation by construction.** The rewriter never descends into `SecurityPredicateExpression` (`SqlAstRewriter.cs:62-65`) and policy expressions are converted to `PolicyParameterExpression` by `PolicyExpressionParser` (`PolicyExpressionParser.cs:346`, `:489`); the expander runs before injection, so no policy node exists in the tree it sees. The executor already refuses reserved gateway names (`GovernedSqlExecutor.cs:250-255`) and the rewrite refuses user SQL that contains the `__param_` prefix (`GovernedSqlExecutionService.cs:379-382`).
7. **Entry points.** Arrow export (`ArrowExportEndpoints.cs:187-190`), Flight SQL (`ArrowFlightSqlServer.cs:198`), MCP (`ApiDispatcherService.cs:177`, `McpToolExecutionHandler.cs:107`) and async jobs (`AsyncQueryJobBackgroundWorker.cs:242-245`) build `GovernedSqlQueryRequest` without `Parameters`: no list can enter. WebSQL and the Trino route share `GovernedSqlRequest` handling (`WebSqlEndpoints.Execution.cs:122`). Declared endpoints pass only parameters that the file declares (`SqlEndpointExecutionService.cs:56-77`), so an undeclared name cannot become a list there. The federated executor reads only `request.Sql` (`FederatedDuckDbExecutionService.cs:191`, `:254`, `:312`); it never sees `Parameters` (grep: no other use).
8. **Header regex** is anchored (`\A...\z`) with a 2 s timeout (`SqlEndpointLoader.cs:28-31`); the type token is `[a-zA-Z0-9_]+` without nested quantifiers, no catastrophic backtracking. Header parsing is operator-authored input.

### 14.3 Findings

Severity scale: High = exploitable data-integrity or availability defect if built as written; Medium = condition required; Low = hardening.

**SEC-128-01 - High - Empty `NOT IN` list becomes `1 = 1`; the DML guard sees it only if expansion runs before the security visitor.**
Evidence: the plan places pass `"lists"` between simplification and the annotator/injection (A-1, "after step 5 and before step 6"). The unfiltered-DML guard `EnsureFilteredDml` (`AstSecurityVisitor.Dml.cs:40-58`) requires a column reference in the WHERE and rejects a trivially-true predicate; `ContainsColumnReference` has no case for `ParameterReference` and sees `IN` items only as column references (`AstSecurityVisitor.Expressions.cs:32`). After expansion `DELETE FROM t WHERE id NOT IN (@empty)` is the literal `1 = 1` and is rejected (`UnfilteredDmlException`); before expansion it would pass the guard (the operand `id` is a column) and execute as a full-table delete. The plan states the order but never the DML consequence, and no test covers it. Required change: see C-1 and tests M-1, M-2 below; the ordering must be pinned by a mutation control (`SecurityControl.ClientListPosition`/a new `ClientListExpandsBeforeGuards`) that moves the pass behind the visitor and must fail the DML tests. Document that `DELETE/UPDATE ... WHERE col NOT IN (@empty)` is refused as unfiltered (intended, fail closed).

**SEC-128-02 - High - Expansion is bounded per list, not per statement: repeated use multiplies AST nodes and SQL text before the output budget is checked.**
Evidence: the plan's dedup (A-1: same list used twice shares binds) bounds *binds*, not *markers*. `x IN (@l)` repeated in one statement expands to `n` markers per occurrence. Statement length is limited to 64,000 (`RestApiOptions.cs:12`; engine 65,536, `FastSqlEngine.cs:61`), so about 7,000 occurrences of `a IN (@l)` times 1,000 elements is about 7 million `ParameterReference` nodes and about 45 MB of text, allocated during the expander and the emitter; the output budget check runs only after emission (`GovernedSqlCompiler.cs:207-211`) and the bind budget is silent because binds are shared (`SqlEmitterContext.cs:182-184`). Compile timeout is the only other brake. The `DialectCapabilityValidator` item limit is per `IN` (`:44`). Required change: a running counter in the expander of total expanded items over all uses, refused before allocating each replacement when the sum exceeds `MaxExpandedListItems = min(MaxBindParameters - 100, 10,000)` (code `SQL_LIST_PARAM_TOO_LARGE`; no new value echo). Test M-3.

**SEC-128-03 - Medium - D-EXP allowance is derived from the request shapes, not from what was expanded.**
Evidence: A-1/section 11 note 5: `limit = factor * max(len, 64) + sum(count_i * 12)` over `request.ClientLists` (`GovernedSqlCompiler.cs:207`). Answer to the architect's question: the counts are server-derived and capped, so a caller cannot choose the allowance text, and the `MaxExpansionFactor` protection against *policy* expansion stays in force. But the allowance is granted for every supplied list whether or not the statement references it (see SEC-128-04), up to `MaxListParameters x 1,000 x 12 = 96,000` extra characters, which is 23 times the 4,096-character base budget of a short statement and widens the slack that a policy expansion may use. Required change: compute the allowance from the lists actually expanded (sum of `count x uses x MarkerAllowance`, taken from the expander counter of SEC-128-02) and subtract it from `compiled.Sql.Length` instead of adding a shape-based number to the limit. Verify `MarkerAllowance = 12` per dialect with a test at ordinal `MaxBindParameters` for every marker style (`$65535, `, `:p32767, `, `@p2099, `). Test M-4 (plan test 11b stays).

**SEC-128-04 - Medium - Every supplied list parameter enters the cache key even if the SQL never references it; per-length templates allow cache flushing.**
Evidence: `ClientListParameters.Shapes(clientValues)` (C-2) is built from the supplied values; `NormalizeClientParameters` computes the set of names actually referenced but the executor discards it (`GovernedSqlExecutor.cs:261`, `out _`). The template cache is shared (LRU 512, `CompiledSqlTemplate.cs`): a caller can generate up to 1,000 distinct lengths per statement, and arbitrary unused list names, each a new compile (R-2 acknowledges churn only for lengths). One WebSQL-capable principal can evict other tenants' warm templates and burn compile CPU. Required change: the shape contains only list names that `NormalizeClientParameters` reports as referenced (`replacedNames`); a supplied list that the SQL does not reference is dropped from shapes and values, and counts toward `MaxListParameters` only once. Padding (Q-128-4) is not required by this review; see 14.7.

**SEC-128-05 - Medium - Admission of list values is gated by `PlannedTableRead`, which is not set on every gateway-internal path; A-4 and A-8 also contradict each other.**
Evidence: `GovernedDataQueryService.cs:193` builds a `GovernedSqlQueryRequest` from planner parameters without `PlannedTableRead` (default false, `IGovernedSqlExecutionService.cs:40-46`), while `SqlTableReadExecutor.cs:102`, `:183`, `:226` and `GovernedAggregationService.ValidationAndExecution.cs:373-377` set it. A-4 accepts "CLR `IReadOnlyList<object?>`/`string[]`/other non-string `IEnumerable`" in the normalizer but says a non-`JsonElement` collection from a gateway component is refused; A-8 says the endpoint layer converts to `ClientListValue` and hands it to the executor. As written the executor cannot tell an endpoint-produced list from a planner-produced one. Required change: admit list values only by an allowlist on `request.Transport` (`WebSql`, `Trino`, `SqlEndpoints`) **and** `PlannedTableRead == false`; accept exactly two value shapes (`JsonElement` array on WebSql/Trino, an already-normalized `ClientListValue` on SqlEndpoints); every other collection or `ClientListValue` on any other transport (including `Transport == null`) is refused with `SQL_LIST_PARAM_POSITION`. The executor re-applies `MaxListParameters`, the configured maximum and the effective maximum to a `ClientListValue` it receives (it must not trust the endpoint layer). Test M-8.

**SEC-128-06 - Medium - Oracle turns an empty string into NULL: `NULL` elements are rejected, but `""` is not.**
Evidence: V-128-4 rejects `NULL` because `NOT IN (.., NULL)` matches nothing; Oracle's empty-string rule makes `""` the same NULL (the code knows this: `ICompiledSqlBinder.cs:419-427`, but `ValidateValue` guards only `Tenant`/`Policy` origins). A list `["a",""]` therefore reaches Oracle as `NOT IN ('a', NULL)` and silently returns no rows (`IN` silently ignores the element). Required change: the Oracle binder rejects an empty-string list element (`ClientListException(SQL_LIST_PARAM_NULL)`, same stable code); `ValidateValue` and the embedded-NUL check (`RejectsEmbeddedNul`, `:204-207`) must be applied to each list element value, not only to the slot, and a test must show this. Other dialects keep `""` as an ordinary value. Test M-6.

**SEC-128-07 - Medium - Values can reach responses and logs through `CoerceValue` and provider error logging.**
Evidence: (a) `SqlEndpointExecutionService.CoerceValue` builds `"Parameter '{p}' with value '{s}' cannot be converted ... {ex.Message}"` (`SqlEndpointExecutionService.cs:117-121`) and `SqlEndpointRoutes` returns `ArgumentException` messages (`:361`, `:440`); for a `JsonElement` array bound to a scalar parameter `s` is the whole array text. Reusing this per element would echo up to 1,000 values per error (A-8: "each element is coerced with the same rules as the scalar `CoerceValue`"). (b) `GovernedSqlExecutor` logs the provider exception object (`:615`, `LogWarning(error, ...)`) and driver messages carry bound values (SQL Server "Conversion failed when converting the nvarchar value '...'", PostgreSQL "invalid input syntax for type integer: ..."); the caller response is sanitized (`DmlErrorSanitizer.Map`, `:616`) but the log is not. A wrongly typed list element against a typed column is the realistic trigger, and plan test 18 only exercises the success path. Required change: list-element coercion is a new function that throws `ClientListException` (fixed message, no value, no inner `ex.Message`); the scalar branch for a list-declared parameter does not call `CoerceValue` with a `JsonElement` array; when a statement binds any client list, the provider-error log line omits the exception message (type and provider error number only; `SEC-X1-12` at `:540` is the precedent). Test M-7.

**SEC-128-08 - Low - Numeric normalization: `Double` fallback can change an identifier; out-of-range tokens can raise `FormatException` (500).**
Evidence: the planned rule "`Int64` if integral, else `Decimal`, `Double` only if not representable as `decimal`" (A-4) mirrors `Normalize` (`WebSqlParameterValues.cs:36-44`, `element.GetDouble()`), which for an integral token beyond decimal range rounds to a different number that can match a different key, and for a token such as `1e999` can throw. Required change: reject (`SQL_LIST_PARAM_TYPE`) any element that is not exactly representable as `Int64` or `Decimal` unless it has an explicit fraction/exponent and is finite; never convert an integral token to `Double`; map every `FormatException`/`InvalidOperationException` from `JsonElement` accessors to `ClientListException`. Date elements of declared `date`/`datetime` lists must be parsed with `DateTimeStyles.AssumeUniversal` when no offset is present (`CoerceValue` uses the host-local offset, `:106-107`; #130 owns the policy, this item must not add a local-time dependency).

**SEC-128-09 - Medium - Raw array size is not capped before de-duplication.**
Evidence: A-4 says the count is checked before elements are copied (`GetArrayLength()` first) and, a few lines later, that de-duplication happens before the count check (FR-128-6). Both cannot hold. A 2 MB body (`SqlEndpointRoutes.cs:398`, `WebSqlEndpoints.Execution.cs:36-42`) holds about 700,000 one-character elements; hashing them is CPU and allocation the limits were meant to prevent. Required change: two checks: (1) raw length > `RawListCeiling = min(10 x configured maximum, 100,000)` is refused before any element is read; (2) distinct count > configured maximum after de-duplication. Both use `SQL_LIST_PARAM_TOO_LARGE`. Test M-5.

**SEC-128-10 - Medium - The residual position pass is the only guard; its completeness depends on `SqlAstRewriter` and the binder fallback ordering.**
Evidence: 14.2 item 5 shows today's completeness; nothing enforces it for future nodes. If a `ParameterReference` named in `ClientLists` were missed, the binder would receive a `ClientListValue` for a scalar slot; `InferType` today throws `SecurityException` ("Unsupported client parameter value type", `ICompiledSqlBinder.cs:241-257`, 403) which is fail-closed only by accident. Required change: (a) a reflection test that walks every concrete `Expression`/`SqlNode` record and proves each `Expression`-typed or `IReadOnlyList<Expression>` property is reached by `SqlAstRewriter` (fails when a new node is added without a visit) and that a list in each such position is rejected; (b) the binder checks `value is ClientListValue` on a slot with `ListIndex == null` **before** `InferType` and throws `ClientListException(SQL_LIST_PARAM_TYPE)`; (c) `BindClient` refuses `ListIndex` without `ListCount`, or `ListIndex >= ListCount`; (d) `IN ((@l))`, `IN (CAST(@l AS ...))`, `IN (@l, @l)`: the sole-item rule is on the exact node type, so a parenthesized or wrapped item is rejected, never "unwrapped". `= ANY(@l)`/`LIKE ANY (@l)`/`= ALL (@l)` produce a position error or an unsupported-construct error and never reach the binder.

**SEC-128-11 - Low - Policy isolation must be pinned, not assumed.**
Evidence: 14.2 item 6. Hazards: a later refactor that runs the expander over the full tree, or #129/#131 reusing `ClientListExpander` on policy ASTs. Required change: tests M-9 (policy parameter and client list with the same name; policy binds identical with and without a list, two principals) and a architecture test that `ParameterOrigin.ClientNamed` slots with `ListIndex` are created only in `SqlEmitterContext.BindClient` and that `ClientListExpander` is invoked only from `GovernedSqlCompiler` pass `"lists"` before injection. A-10's rule (never route a policy-origin value through `ClientNamed`) is confirmed and becomes binding for #129/#131.

**SEC-128-12 - Medium - The federation guard covers only the three catch blocks in `GovernedSqlExecutionService`.**
Evidence: `GovernedDataQueryService.cs:198`, `:216` call `IFederatedQueryExecutionService` directly (and catch `CrossSourceRoutingException` at `:214`); `FederatedDuckDbExecutionService` uses only `request.Sql` (`:191`, `:254`, `:312`), so any parameter, scalar or list, is silently dropped there (pre-existing for scalars; owner flag F-6). A-9 names `GovernedSqlExecutionService.cs:110/131/148`; `:110` is `ValidateAsync` (no parameters). Required change: put the guard `ClientListParameters.RejectForFederation(request)` in the entry methods of `FederatedDuckDbExecutionService` (`ExecuteQueryBufferedAsync`, `ExecuteGovernedQueryAsync`, `StreamRowsAsync` if present) so every dispatch path is covered, keep the call in the catch blocks as a second line, and evaluate the **raw** `request.Parameters` (any `JsonElement` array, collection or `ClientListValue`), not the normalized values. Test M-10.

**SEC-128-13 - Medium - Mixed-case and duplicate-spelling names, and DML through the checked executor, need explicit tests.**
Evidence: names are case-insensitive in the value dictionary (`GovernedSqlExecutor.cs:663-670`, first spelling wins) and in the rewrite (`GovernedSqlExecutionService.cs:386`), declared endpoints store `name` and `@name` (`SqlEndpointExecutionService.cs:75-76`). The shape and the binder must agree on one spelling; a statement using `@Ids` and `@ids` produces two slots (`DedupKey` uses the name as written, `SqlEmitterContext.cs:169`) that read the same list. `BindForCheckedExecution` shares `BindCore` (`ICompiledSqlBinder.cs:170-171`) so `UPDATE ... WHERE id IN (@ids)` runs the same code; the expected-affected-rows check must not be satisfied by a truncated list. Required change: tests M-11 (mixed-case spellings, one list value under `name` and `@name`) and M-12 (DML with list: exact count, mismatch refusal, zero-row `IN (@empty)` update (as built: refused as SQL_UNFILTERED_DML, see §13), empty `NOT IN` refused per SEC-128-01).

**SEC-128-14 - Low - Declared-query header grammar: precision of "starts as a colon declaration", defaults, GET key handling.**
Evidence: `ParamHeaderRegex` (`SqlEndpointLoader.cs:28-31`). Today a description such as `-- @param status: filter by status` does not match and is silently ignored (`:526`); the plan refuses "a line that starts as a colon declaration but does not parse". Required change: define the trigger exactly as `^[A-Za-z0-9_]+\s*:` at the start of the value; the description-only form (`f-sql-01`, no colon after the first token) and a colon later in a description stay ignored (test required); a colon-form description line now refuses the file with the diagnostic naming file and parameter and **no value text** (breaking, accepted: R-4, owner flag F-4). List defaults are parsed with `JsonDocument` with `MaxDepth = 1`-style flat check (array of scalars only, no nesting), element count bounded by the hard ceiling of SEC-128-09 at load (the dialect is not known at load), every element through the normalizer with the declared element type. `HandleGetEndpoint` converts to a list only for keys that name a **list-declared** parameter; for every other key the value stays the joined `StringValues.ToString()` as today, so reserved route keys (format, paging) are unaffected; a scalar-declared parameter with a repeated key is refused (`SQL_LIST_PARAM_TYPE`). Query-string size is already bounded by the server request-line limit, the list maximum applies on top.

### 14.4 Binding conditions for Phase 4

| ID | Condition (all binding; test IDs refer to 14.5) |
|---|---|
| C-1 | **Order** (SEC-128-01): pass `"lists"` runs after simplification and **before** `AstSecurityVisitor` (DML guard) and injection; pinned by a mutation control; `DELETE/UPDATE ... NOT IN (@empty)` is rejected as unfiltered, `IN (@empty)` affects zero rows (as built: refused as SQL_UNFILTERED_DML, see §13). |
| C-2 | **Per-statement expansion cap** (SEC-128-02): running total of expanded items over all uses, `MaxExpandedListItems = min(MaxBindParameters - 100, 10,000)`, checked before allocation. |
| C-3 | **Output allowance** (SEC-128-03): derived from actually expanded markers (subtracted from emitted length), never from request shapes; per-dialect marker-length test. |
| C-4 | **Shape = referenced lists only** (SEC-128-04): use `replacedNames`; unreferenced supplied lists are dropped; names compared case-insensitively. |
| C-5 | **Admission allowlist** (SEC-128-05): transports `WebSql`/`Trino`/`SqlEndpoints`, `PlannedTableRead == false`, two accepted value shapes, executor re-validates a `ClientListValue`. |
| C-6 | **Oracle empty string** (SEC-128-06) and per-element NUL/`ValidateValue` in the binder. |
| C-7 | **No value in any diagnostic** (SEC-128-07): new element coercion; provider-error log without message when client lists are bound; `ClientListException` carries only the code and constructs a fixed message; the mapper row precedes the `SecurityException` row. |
| C-8 | **Number/date rules** (SEC-128-08): no integral-to-`Double`, finite only, accessor exceptions mapped, `AssumeUniversal` for offset-less dates. |
| C-9 | **Raw ceiling before dedup** (SEC-128-09), distinct maximum after. |
| C-10 | **Position completeness** (SEC-128-10): reflection test, binder refuses `ClientListValue` in a scalar slot before `InferType`, `BindClient` index invariants, exact-node sole-item rule. |
| C-11 | **Isolation tests** (SEC-128-11) and the architecture test on `ListIndex` creation. |
| C-12 | **Federation guard in `FederatedDuckDbExecutionService` entries** (SEC-128-12), raw parameters. |
| C-13 | **Name and DML tests** (SEC-128-13). |
| C-14 | **Header and GET rules** (SEC-128-14). |
| C-15 | **Errors**: `SQL_LIST_PARAM_*` messages are fixed strings; counts are never echoed (F-2 accepted); `ClientListException` is mapped in WebSQL, both declared-endpoint handlers (`SqlEndpointRoutes.cs:361`, `:440`), the Trino route and the global handler; audit field `list_params` holds names (regex-limited) and counts only. |
| C-16 | **Scope**: no source change to `AstSecurityVisitor.cs` (owner flag F-1 stays); any change to `GovernedSqlCompiler` pass order needs this review's mutation tests green. |

### 14.5 Mandatory security tests (in addition to plan section 7; all `[Trait("Category","Security")]`)

| ID | Test | Proves |
|---|---|---|
| M-1 | `Dml_NotInEmptyList_IsRejectedAsUnfiltered_NoRowsAffected` (DELETE and UPDATE, SQLite execution; also `WHERE a = 1 OR id NOT IN (@empty)`) | SEC-128-01 |
| M-2 | `Mutation_ExpanderAfterSecurityVisitor_FailsM1` (new `SecurityControl` switch moves the pass) | order pinned |
| M-3 | `Expansion_RepeatedUse_IsCappedPerStatement` (e.g. 5,000 uses of `IN (@l)` with 1,000 values refused before emission; assert allocation bound/peak node count via counter) | SEC-128-02 |
| M-4 | `OutputAllowance_OnlyForExpandedLists_UnusedListsGiveNoSlack` and per-dialect marker-length test at the maximum ordinal | SEC-128-03 |
| M-5 | `Normalizer_RawCeiling_RefusesBeforeReadingElements` (100,001 elements; 2 MB array of 700k duplicates) and distinct-count refusal | SEC-128-09 |
| M-6 | `Oracle_EmptyStringElement_IsRejected`; `NulElement_IsRejectedOnPostgreSql`; element goes through `ValidateValue` | SEC-128-06 |
| M-7 | `ProviderError_WithElementText_IsNotInLogsAuditOrResponse` (fake provider throws a `DbException` whose message contains an element); `ElementCoercionError_ContainsNoValue`; `GetEndpoint_ScalarWithRepeatedKey_ErrorContainsNoValue` | SEC-128-07 |
| M-8 | `ListValue_OnPlannedPath_OnOtherTransports_AndNullTransport_IsRefused` including the `GovernedDataQueryService.cs:193` shape (flag false) | SEC-128-05 |
| M-9 | `PolicyParameterAndClientListSameName_Isolated`; `PolicyBinds_IdenticalWithAndWithoutList_TwoPrincipals`; architecture test on `ListIndex` creation and expander call site | SEC-128-11 |
| M-10 | `Federation_ListParameter_IsRefused_OnEveryDispatchPath` (`GovernedSqlExecutionService` and direct `GovernedDataQueryService` path), raw `JsonElement` array detected | SEC-128-12 |
| M-11 | `ListName_CaseInsensitive_SingleShape_BothSpellings`; `@name` and `name` keys carry one list | SEC-128-13 |
| M-12 | `CheckedDml_ListBinding_ExactCount_MismatchRefused` | SEC-128-13 |
| M-13 | `Cache_TwoRequests_SameLengthDifferentValues_ShareTemplate_NeverShareValues` (assert second request's binds are its own, the first request's values appear in no template field, `ToString`, key or digest) and `Cache_UnreferencedListDoesNotChangeKey` | SEC-128-04, T-5 |
| M-14 | `Position_ReflectionCompleteness_EveryExpressionPropertyIsVisited` plus `ScalarSlot_ReceivesClientListValue_IsRefusedBeforeInferType` (hand-built `CompiledSql`) | SEC-128-10 |
| M-15 | `Numbers_IntegralBeyondDecimal_IsRejected`; `1e999_IsRejected_NoException500`; `Date_WithoutOffset_UsesUtc` | SEC-128-08 |
| M-16 | `Header_ColonDescriptionLine_RefusesFile_NoValueInDiagnostic`; `Header_DescriptionOnly_Ignored`; `Header_ListDefaultNested_Refused`; `Rest_Get_ReservedKeyRepeated_Unchanged` | SEC-128-14 |
| M-17 | `ErrorMapper_FixedMessages_NoCountNoName` on every surface (WebSQL, both endpoint handlers, Trino route) and `Audit_ListParams_NamesAndCountsOnly` with a hostile value set | C-15 |
| M-18 | `Oracle_BindByName_ManyListSlots_OrderIndependent` and `PostgreSql_Positional_ListOrdinalsStable` with tenant + policy + mask + list | ordinals |

Assessment of the planned tests: the plan covers splice, position, key, binder mismatch, budget, policy, federation, audit and mapper. It does not cover the DML interaction, amplification, unreferenced lists, admission paths, Oracle empty string, provider-error logging, direct federated dispatch, cross-request cache values, reflection completeness and number edge cases; M-1..M-18 close those gaps. Mutation controls `ClientListPosition` and `ClientListCount` are good; add `ClientListExpandsBeforeGuards` and `ClientListMaxExpanded`.

### 14.6 Answers to the explicitly asked questions

- **Injection via elements:** closed (14.2 item 1); residual type edge cases are SEC-128-06 (Oracle `""`), -08 (numbers). `NULL` rejection plus the Oracle rule makes `NOT IN` NULL poisoning impossible from the list. Collation: bound parameters cannot change a column's collation; case-insensitive duplicates are harmless.
- **Empty-list polarity:** `1 = 0`/`1 = 1` are correct for client lists; the DML consequence is SEC-128-01.
- **Cache key:** correct by construction; tighten to referenced lists (SEC-128-04); no value leakage (14.2 item 2, test M-13).
- **Bind budget/DoS:** the per-statement bind budget is correct; the missing bound is expanded *text/AST* per statement (SEC-128-02) and the raw array ceiling (SEC-128-09).
- **D-EXP:** acceptable in principle (server-derived, capped); change the derivation to actual expansion (SEC-128-03). It cannot bypass `MaxExpansionFactor` for policy growth beyond the stated slack once fixed.
- **Position rule:** complete for the current AST (14.2 item 5); make completeness a test (SEC-128-10).
- **Policy interaction:** isolated by construction; pin with tests (SEC-128-11). Policy lists of #129 never go through `ClientNamed` (A-10 confirmed).
- **Header/GET:** SEC-128-14. **Errors:** F-2 accepted (14.7). **Federation:** SEC-128-12. **`uuid`/`guid` fix:** correct and safe (`Guid => String` canonical "D" text is data, not text; add a test that a `Guid` list element is rendered by `Convert.ToString`/`ToString("D")`, not culture dependent). **Audit:** `list_params` names (regex-limited to `[A-Za-z0-9_]{1,128}`, no injection into the audit JSON) and counts are acceptable.
- **Entry points:** WebSQL, Trino route (shared executor), declared SQL endpoints carry lists; Arrow, Flight SQL, MCP and async jobs cannot (no `Parameters`); planned reads must never (SEC-128-05); federated refuses (SEC-128-12).

### 14.7 Recommendations on open questions and owner flags

| ID | Recommendation |
|---|---|
| Q-128-1 | Accepted as decided. Add `MaxExpandedListItems` (C-2) and `RawListCeiling` (C-9) as internal constants, not new options. The clamp warning (A-6) logs dialect and numbers only. |
| Q-128-2 | Alias list accepted. Trigger for refusal is exactly `^name\s*:` (SEC-128-14); note the breaking effect on colon-form description lines. |
| Q-128-3 | No per-parameter maximum. |
| Q-128-4 | Padding stays off in this item (consistent with the plan). The security gain is bounded by SEC-128-04; revisit if the template-cache metrics show churn. If enabled later, the cap must be applied to the padded count and the binder's exact-count check to the padded count. |
| Q-128-5 | Not supported; confirmed (fewer forms, fewer position proofs). |
| F-1 | Accepted, with 14.4 C-16 and the two new mutation controls; owner review at Phase 5 should read `ClientListExpander`, the binder branch and the guard order first. |
| F-2 | **Accept** the deviation from V-128-9 (no received count in the shared mapper). Optionally add the *configured* maximum as the mapper detail of `SQL_LIST_PARAM_TOO_LARGE` (as `limit.Kind` is passed today, `SqlCompilerErrorMapper.cs:122`); do not publish the effective (dialect-clamped) number outside the authenticated analysis output, because it reveals the backend. |
| F-3 | Accept (scalar `uuid` fix). |
| F-4 (new) | Colon-form description lines in declared-query files now refuse the file (SEC-128-14). Accept, or narrow the trigger to lines whose second token looks like a type. Recommendation: accept (fail closed, diagnostic names file and parameter). |
| F-5 (new) | `SqlEndpointRoutes` joined repeated keys silently today; scalar parameters given a repeated key are refused after this item (behaviour change). Accept. |
| F-6 (new) | Pre-existing: the federated executor silently ignores **scalar** parameters (`FederatedDuckDbExecutionService.cs:191`); out of scope here, carried to `PLAN-FED-01`/#131 (S-2). |

### 14.8 Hand-over to Phase 4 and later reviews

- Update plan sections 3 and 4 as the conditions require: `ClientListExpander` gets the running total (C-2/C-3) and exposes it to the compiler; `ClientListParameters.Shapes` takes `replacedNames`; `FederatedDuckDbExecutionService` is added to WP-4 ownership (one more file; the lead updates the I-5 file list of the delivery tracker); the executor admission allowlist (C-5) lives in `GovernedSqlExecutor.BuildClientValues`.
- Phase 5 reviewer checklist: pass order, expansion counters before allocation, binder branch order (`ClientListValue` check before `InferType`), absence of `ex.Message`/values in any new `throw`/`Log*`, `ClientListShapes.Canonical` allocation (G8).
- #129/#131 (S-2, S-3 and later): reuse the value, limit and client expansion layers only; policy-origin lists never use `ClientNamed` and keep their own polarity (A-10 confirmed binding).

**Verdict: APPROVED WITH CONDITIONS** (`SEC-128-01..14`, binding conditions C-1..C-16, mandatory tests M-1..M-18).
