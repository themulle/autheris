# Plan: WebSQL caller parameters named `@p0`, `@p1`, … (Issue #869)

- Issue: [#869](https://github.com/themulle/autheris/issues/869) (reporter rgruebel, v1.8.5)
- Phase: 2 (architecture). Next gate: Phase 3 security review.
- Owner rule: the recommended default is taken for every open decision.

## 1. Root cause (verified on `main`, `fbb028922`)

1. `POST /api/v1/sql` rewrites every supplied `@name` to the synthetic identifier `__param_<name>`
   (`src/Autheris.Application/Sql/Services/GovernedSqlExecutionService.cs:636-650`); a literal `__param_` in the caller text is refused (`:559-562`).
2. The AST builder turns `__param_<name>` into `ParameterReference(name, IsSynthetic: true)` (`src/TrinoSqlEngine/Ast/Builder/SqlAstBuilder.cs:1100-1106`).
3. The bound emitter calls `BindClient` (`src/TrinoSqlEngine/Ast/Buffer/SqlEmitterContext.cs:178-192`), which runs `ValidateClientParameter` (`:228-240`).
   `ReservedClientNameRegex` `\A(p[0-9]|__|gql_|autheris)` (`:28-30`, case-insensitive) matches `p0`, `P0`, `p1x` and throws a plain `SecurityException`.
4. `SqlCompilerErrorMapper` maps a plain `SecurityException` to 403 `SQL_POLICY_VIOLATION` (`src/Autheris.Application/Sql/Errors/SqlCompilerErrorMapper.cs:202`). That is the observed response.
5. There is no real collision: every slot, client or internal, gets a fresh ordinal marker from `Bind` (`SqlEmitterContext.cs:242-262`, `FormatMarker` `:300-308`).
   The caller name is kept only as `BoundParameter.SourceName`. The binder reads client values only for `ParameterOrigin.ClientNamed` slots, and only by their own source name
   (`src/TrinoSqlEngine/Ast/Emit/ICompiledSqlBinder.cs:210-215`). Policy, tenant, mask and page slots never read the client map.
   `ISqlDialectProvider.FormatParameterName(int)` (`ISqlDialectRegistry.cs:177-182`, `SqlDialectProviders.cs:47-52`) has no caller on the compile path.
6. The other reserved-name gates do not contain `p<digit>`: `EnsureClientParameterNames` (`GovernedSqlExecutionService.cs:416-439`) and
   `FederatedParameterContract.ValidateParameterName` (`src/Autheris.Application/Sql/Federation/Parameters/FederatedParameterContract.cs:186-199`). Both stay unchanged.

## 2. Options

| Option | Change | Golden churn (grep `[@:]p[0-9]+\b` in `tests/`) | Caller value can reach an internal slot? |
|---|---|---|---|
| (a) reserved prefix for internal binds (`@autheris_p0`) | `FormatMarker` plus every emitter consumer | 2,249 hits in 52 files: `TrinoSqlEngine.Tests` 27 files / 2,066, `Autheris.Tests.Unit` 18 / 151, `SqlCompilerGate` corpus `dql.sql`, `dml.sql` 2 / 20, `Autheris.Tests.Integration` 4 / 9; plus emission snapshots and the Databricks and DuckDB binders | No |
| (b) caller names live in their own namespace, internal names unchanged | Already structural (steps 1, 2, 5). Only the defensive `p[0-9]` alternative in the regex remains to drop | 0 golden changes | No (binder origin check) |
| (c) 400 with a stable reserved-name code | New exception row in the mapper | Small | Not applicable (still rejects `@p0`) |

## 3. Decisions

- D1 **Option (b).** Remove `p[0-9]` from `ReservedClientNameRegex`; keep `__`, `gql_` and `autheris`. (default taken: zero golden churn; the namespace separation already exists and is fail-closed in the binder.)
- D2 **No option (c) for `@p0`.** After D1 `@p0` is an ordinary name. (default taken: (c) is only the fallback when (b) is not possible.)
- D3 **The remaining reserved prefixes keep 403 `SQL_POLICY_VIOLATION`** (`__`, `gql_`, `autheris` in the emitter; `__gql_`, `rls_`, `p_rls_`, `__param_`, `p_pd_`, `gw_tf_`, `__rls_ks` in the Application gates). (default taken: those are real internal namespaces on other paths; a status change is out of scope for this bug.)
- D4 **Fail-closed invariant, made explicit and tested:** a `ClientNamed` slot never carries a value at compile time, and only `ClientNamed` slots read the client map. A test pins this for a caller `p0` placed next to a policy or tenant slot that has ordinal 0.
- D5 **Name grammar stays ASCII-only** (`ClientNameRegex` `[A-Za-z_][A-Za-z0-9_]{0,127}`, `char.IsAsciiLetterOrDigit` in the rewriter). (default taken: Unicode look-alikes never become parameters.)
- D6 Documentation of `@name` in F-DATA-02 is not part of this plan (no documentation phase). The owner can request it.

## 4. Work package (Hydrogen-ready)

**WP-869-1 (S, about 15 LOC production, about 120 LOC tests)**
- Production: `src/TrinoSqlEngine/Ast/Buffer/SqlEmitterContext.cs:28-30`. Change the pattern to `\A(__|gql_|autheris)` and update the comment on line 28
  ("internal markers are ordinal; client names are bound by source name only").
- Unit tests (TDD, red first):
  - `tests/TrinoSqlEngine.Tests/Compiler/CompiledSqlBinderTests.cs` (extends the existing case on line 80):
    - `ClientParameterNamedP0_BindsCallerValue_ToItsOwnSlotOnly`
    - `ClientParameterNamedP0_NextToPolicySlotP0_PolicyValueUnchanged` `[Trait("Category","Security")]`
    - `ClientParameterNamedUpperP0_BindsToOneClientSlot_NeverToAnInternalSlot` `[Trait("Category","Security")]`
  - `tests/TrinoSqlEngine.Tests/Compiler/ClientListMutationTests.cs`: `ListParameterNamedP0_ExpandsToFreshOrdinalSlots`.
  - `tests/Autheris.Tests.Unit/Sql/GovernedSqlExecutionParameterSecurityTests.cs`:
    - `ReservedPrefixes_StillRejected_403` for `__x`, `gql_x`, `autheris_x`, `rls_x` (regression for D3) `[Trait("Category","Security")]`
    - `CyrillicLookalikeP0_IsNotAParameter` `[Trait("Category","Security")]`
- Local filter: `FullyQualifiedName~CompiledSqlBinderTests|FullyQualifiedName~ClientListMutationTests` (TrinoSqlEngine.Tests) and `FullyQualifiedName~GovernedSqlExecutionParameterSecurityTests` (Unit).
- Integration (compile-check locally, run on CI):
  - `tests/Autheris.Tests.Integration/GovernedWebSqlIntegrationTests.cs`: add `Post_Sql_ParameterNamedP0_Returns200_AndRowFilterStillApplies`. It uses a table with a row filter or tenant column, so internal binds are present.
  - `tests/Autheris.Tests.Integration/Sql/ClientListWebSqlIntegrationTests.cs`: add `@p0` as a list (`IN (@p0)`).
- `tests/Autheris.Tests.SqlCompilerGate`: no change expected. The corpus `@p0` entries (`dql.sql:2798,2810`, `dml.sql:801,804`) are emitted-SQL invariant inputs, not caller input. `CorpusTests`, `AcceptedDifferencesTests` and `Parity/RendererInvariantTests.cs:127-137` must stay green on CI.
- `tests/Autheris.Tests.Architecture/Baselines/security-tests-base.txt`: add the new security test names if the ratchet requires it.

## 5. Threats for Phase 3

1. Value smuggling into a row-filter, tenant or mask bind: blocked by origin (`ICompiledSqlBinder.cs:210`). Phase 3 confirms that no other binder (DuckDB OLAP `DuckDbOlapEngine.cs:208-224`, Databricks, federated) maps client values by marker name.
2. Case-insensitive collisions (`@P0` vs `@p0`, or both keys in one body): the client map is `OrdinalIgnoreCase`, and `FederatedParameterContract.cs:96-101` keeps the first spelling. Phase 3 decides whether a body with two case-variants of one key must be refused (recommended: 400, separate issue).
3. Unicode look-alikes (`р0` Cyrillic, full-width digits): ASCII-only grammar (D5); the pattern uses `[0-9]`, never `\d`.
4. Parameter count limits: each caller name takes exactly one slot (lists take one per element) against `MaxBindParameters` (`SqlEmitterContext.cs:254-257`). Overflow throws `SqlLimitExceededException`. The rename adds no slots.
5. Unbound emission (`SqlDialectGeneratorBase.Expressions.cs:411-439`) prints raw non-synthetic names for SQL Server and Oracle. The parser never produces a non-synthetic named reference from caller text. Phase 3 confirms that no executable path emits unbound.
6. Synthetic-reference forgery: `__param_` in the caller text stays refused (`GovernedSqlExecutionService.cs:559-562`), and `__` stays reserved in the emitter regex.
