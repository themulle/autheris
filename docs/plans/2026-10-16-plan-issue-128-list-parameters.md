# Implementation Plan: REQ-SQL-128 - List Parameters for `IN` / `NOT IN` in WebSQL and Declared Queries

**Document ID:** `PLAN-SQL-128` (revision 1, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [PRD REQ-SQL-128](2026-10-16-req-issues-128-129-130-websql-parameters-user-attributes-timezones.md) section 2 (V-128-1..9, FR-128-1..8, AC-128-1..8; owner decision Q-128-1)
**Branch:** to be created from the `main` that contains the previous delivery item (delivery step I-5)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md); requirement [REQ-SQL-128](2026-10-16-req-issues-128-129-130-websql-parameters-user-attributes-timezones.md); delivery tracker [PLAN-DELIVERY-W23](2026-10-16-plan-waves-2-3-delivery.md) (planning step P-1, implementation step I-5)
**Status:** PROPOSED (Phase 2 complete; next: Phase 3 security review, then Phase 4 after gate G-PLAN)

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
