# Implementation Plan: AST Target Dialect Generator - Single-Path Compiler Cutover

**Document ID:** `PLAN-AST-DIALECT-GEN-16` (implementation plan, Phase 2 of the 6-phase lifecycle)
**Date:** 2026-10-10
**Status:** IN PROGRESS - Phase 3 security review delivered (§16); next: Phase 4 TDD implementation
**Author:** Solution Architect (`csharp-architect`); Phase 3 review (§16) by Security Expert (`csharp-security-expert`)
**Parent plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Requirements baseline:** [2026-10-10-req-ast-dialect-generator.md](2026-10-10-req-ast-dialect-generator.md) (PRD, Phase 1)
**Supersedes:** [2026-10-06-implementation-plan-ast-dialect-generator.md](2026-10-06-implementation-plan-ast-dialect-generator.md) (German draft, kept for history only)
**Amends:** [ADR-017 §2](../adr/ADR-017-distributed-state-ast-generator-and-rbac.md) (amendment text in §13; Phase 6 writes it)
**Branch base:** `feat/ast-target-dialect-generator` (commit `6985be1`)

---

## 0. Summary

The end state is **one** governed SQL compiler. Every SQL text produced by WebSQL, SQL endpoints, MCP dataset tools and virtual filters goes through:

```
Trino SQL -> token guards -> ANTLR parse -> typed AST -> simplify (user tree only)
         -> security injection (typed policy nodes) -> coverage verification
         -> dialect capability validation -> dialect emitter -> CompiledSql(text, bound parameters)
```

The legacy `RlsListener` / `TokenStreamRewriter` path, the `SqlRewriterEngine` switch, `ShadowDualRun` and `TrustedSqlExpression` are deleted in this track. There is no runtime engine flag, no shadow mode and no production observation window. Confidence comes from a **pre-merge, CI-enforced evidence gate** (§9) that runs while legacy still exists. When it is green, a single squash-merged cutover commit switches all consumers and deletes legacy. The rollback is `git revert` of that commit.

Scope additions over the PRD (stakeholder decisions, §1):

- Every value reaches the database as a bind parameter. This covers user literals, tenant values, policy values and mask arguments.
- Policy row filters and column masks become typed AST nodes. `TrustedSqlExpression` and raw-fragment splicing are removed.
- DML (`INSERT`, `UPDATE`, `DELETE`, `MERGE`) runs through the AST path in the first cut.
- **Databricks** becomes a new production dialect with a generator, a capability entry, a runtime connector and DML/`MERGE` semantics.
- **Snowflake** stays an experimental dialect: generator only, never executable in production.

---

## 1. Stakeholder Decisions and How They Override the PRD

| ID | Decision (binding) | PRD items overridden or resolved |
|---|---|---|
| SD-1 | **One code path.** AST compiler only. `RlsListener`, `TokenStreamRewriter` rewriting, `SqlRewriterEngine`, `RlsOptions.RewriterEngine` and `ShadowDualRun` are removed within this track. | Replaces increments 0-5, FR-1 (typed engine selector), FR-3 (kill switch), INV-8 (engine transparency, reduced to compiler version and dialect in audit), INV-9 (shadow isolation, obsolete), AP-8 (selector removed instead of typed). Answers Q-1 with option (a). |
| SD-2 | **No production observation window.** The evidence comes from a CI gate before merge: a classified differential corpus against legacy (zero `ast-looser`), property-based and differential fuzzing, real execution on SQL Server, PostgreSQL and Oracle (Testcontainers), SQLite and DuckDB (in process) and Spark/Delta (Databricks proxy), plus the carried-over SEC-xx/SQ-xx security suites. | Replaces FR-2, NFR-5, AP-5, the staging and production shadow ACs (AC-1.5, AC-1.6, AC-5.1) and Q-2. AP-2 (big-bang risk) is mitigated by the gate (§9) and accepted by the stakeholder. |
| SD-3 | **Best practice throughout.** sqlglot/Calcite/jOOQ-style design. All values are bound. `TrustedSqlExpression` policy and mask fragments become typed AST nodes: they are parsed once, cached and fail closed. A per-dialect capability table enforces correct limits (SQLite 32,766; Oracle's 1,000-item IN-list limit kept separate from its bind limit), and queries over a limit are rejected. Input token guards are kept. RLS predicates provably survive the simplifier. A BenchmarkDotNet gate measures against legacy with the PRD budgets. | Resolves G-3, G-4, G-5, G-6, G-7, AP-3, AP-7, AP-9. Answers Q-4 (reject), Q-7 (in scope) and Q-8 (PRD budgets accepted). |
| SD-4 | **DML from the start.** `INSERT`/`UPDATE`/`DELETE`, and `MERGE` (the grammar supports it, see §6.4), go through the AST path in the first cut. This includes WITH CHECK OPTION semantics, the unfiltered-DML guard and masked-column write protection. | Folds increment 3 into the single cutover. Answers Q-5. |
| SD-5 | **Snowflake is experimental.** It has a generator only, is clearly marked and is not in the production capability list. | Answers Q-3. Matches PRD §6.3 for Snowflake. |
| SD-6 | **ADR-017 is amended** to reflect SD-1..SD-7 (exact text in §13). | Answers Q-6. |
| SD-7 | **Databricks is a fully supported production dialect**, not experimental. It gets a generator (backtick quoting, `:name` markers, `LIMIT`/`OFFSET`, Unity Catalog three-part names, function and type mapping), a capability entry, DML on Delta (`UPDATE`, `DELETE`, `MERGE`), a Spark/Delta CI proxy, golden tests and an optional live SQL Warehouse job. | New scope beyond the PRD. Adds stream C (§11). |
| SD-8 | **Oracle is a supported production dialect with a runtime driver** (decided during Phase 3). | Resolves OQ-1. Adds Stream F (§16.6). |

PRD content that stays binding: invariants INV-1..INV-7 (§8.1), the anti-pattern rulings AP-1, AP-3, AP-4, AP-6, AP-7, AP-9 and AP-10 (plus the new AP-11 from §3.6: no Trino-style domain compaction of security predicates), the NFR-2/NFR-3 performance budgets, the NFR-4 limit sourcing, NFR-6 observability (minus shadow metrics), NFR-7 compatibility and NFR-8 language.

---

## 2. Additional Findings from Phase 2 (Beyond PRD G-1..G-8)

These were found while reading the code at `6985be1`. Each finding is handled by a work package.

| ID | Finding | Location | Handled by |
|---|---|---|---|
| F-1 | The mask dialect mapping falls back to PostgreSQL for any unmapped dialect (`_ => TargetSqlDialect.PostgreSql`). A Databricks or DuckDB table would silently get PostgreSQL mask SQL. This is a fail-open default. | `src/Autheris.Application/Services/SqlDataSourceExecutor.cs:568-588` | WP-D4 |
| F-2 | `RlsOptions.TargetDialect` defaults to `Ansi`. ANSI has `MaxParameterBudget => int.MaxValue` and no executable backend. | `src/TrinoSqlEngine/IRlsPolicyProvider.cs:283`, `AnalyticalDialectGenerators.cs:15` | WP-A8 |
| F-3 | The tenant predicate is a string-escaped **literal** spliced into the filter text (`$"{tenantColumn} = '{tenantId.Value.Replace("'", "''")}'"`). | `src/Autheris.Application/Sql/Services/GovernedSqlRewriter.cs:541` | WP-D1 |
| F-4 | `SqlFilterCompiler.Compile` post-processes the generated SQL with `string.Replace` on a quoted alias. This is a raw-text operation inside the "typed" pipeline. | `src/Autheris.Application/VirtualFilters/SqlFilterCompiler.cs:366-370` | WP-D3 |
| F-5 | Named client parameters are emitted as `__param_name` / `@name` and later restored by a regular expression over the secured SQL text (`RestoreClientParameters`). | `GovernedSqlExecutionService.cs:417-429`, `SqlDialectGeneratorBase.cs:607-641` | WP-A2, cutover WP-X1 |
| F-6 | There are two divergent limit tables: `DatabaseParameterBudgetProvider` (SQLite 999, Oracle 1,000, PostgreSQL 10,000, Databricks 10,000) and the generator `MaxParameterBudget` values (SQLite 999, Oracle 1,000, PostgreSQL 65,535). | `src/Autheris.Infrastructure/Persistence/DatabaseParameterBudgetProvider.cs`, `Ast/Generators/*` | WP-A1 |
| F-7 | Databricks exists as `DatabaseDialect.Databricks` (provider aliases `databricks`, `spark`, `sparksql`). It has quoting and normalization helpers, but no `TargetSqlDialect`, no generator, no driver (`SqlConnectionFactory`: "Oracle and Databricks are dialects without a driver here") and no mapping in `SqlDialectMapper`. | `src/Autheris.Domain/Common/DatabaseDialect.cs`, `SqlConnectionFactory.cs:31`, `SqlDialectMapper.cs` | Stream C |
| F-8 | The grammar supports `MERGE INTO ... USING ... WHEN [NOT] MATCHED` (`SqlBase.g4:229-231, 874-882`). The AST builder and legacy path reject it, and there is no `MergeStatement` node. `WHEN NOT MATCHED BY SOURCE` is not in the grammar. | `SqlBase.g4`, `SqlAstBuilder.cs:151` | WP-A7 |
| F-9 | `CrossSourcePlanner` (federated DuckDB, track PLAN-FEDERATED-VF-DUCKDB-15) calls the DuckDB generator directly and executes the returned string. | `src/Autheris.Application/Sql/Services/CrossSourcePlanner.cs:278` | WP-X3 (coordinated) |
| F-10 | `SqlDialectMapper.IsExecutable` covers only SQL Server, PostgreSQL and SQLite. Oracle has a generator and container tests, but no runtime driver in the gateway. | `SqlDialectMapper.cs:40` | Open question OQ-1 |
| F-11 | The simplifier runs **after** security injection and folds `WHERE` trees. The injected deny-all filter `1 = 0` and contradiction folding can interact with injected predicates (PRD G-6). | `FastSqlEngine.cs:815-816` | WP-A4 |
| F-12 | `AstSecurityVisitor.BuildDialectMaskExpression` returns dialect SQL strings and is also called from the GraphQL/data-source executor. | `SqlDataSourceExecutor.cs:574-595` | WP-A6, WP-D2 |

---

## 3. Target Architecture

### 3.1 Component diagram

```mermaid
flowchart TB
    subgraph Consumers["Application consumers"]
        GSR["GovernedSqlRewriter<br/>(WebSQL, SQL endpoints, MCP)"]
        SFC["SqlFilterCompiler<br/>(virtual filters)"]
        GSE["GovernedSqlExecutionService /<br/>GovernedSqlExecutor"]
        CSP["CrossSourcePlanner<br/>(federated DuckDB, PLAN-15)"]
    end

    subgraph Policy["Policy IR producers (Application)"]
        RFB["RowFilterSqlBuilder<br/>-> PolicyPredicate"]
        TAP["TableAccessPolicy.Restrict<br/>(typed AND-merge)"]
        MSK["SqlDataMaskingProvider<br/>-> MaskSpec"]
        PEP["IPolicyExpressionParser<br/>(admin-authored predicates, cached)"]
    end

    subgraph Compiler["TrinoSqlEngine: GovernedSqlCompiler"]
        TG["Token guards<br/>(SqlTokenSecurityOptions)"]
        P["ANTLR parse<br/>(depth, timeout, length)"]
        B["SqlAstBuilder<br/>(fail loud)"]
        V["AstValidationVisitor"]
        S["AstSimplificationVisitor<br/>(user tree only)"]
        SEC["AstSecurityVisitor<br/>(SecurityPredicateExpression,<br/>MaskExpression, DML guards)"]
        COV["SecurityCoverageVerifier<br/>(INV-2, INV-3)"]
        CAP["DialectCapabilityValidator<br/>(constructs, functions, IN-list,<br/>identifier length)"]
        GEN["ISqlDialectGenerator<br/>(PG, MSSQL, SQLite, Oracle, DuckDB,<br/>Databricks, Snowflake*)"]
        PB["ParameterBinder<br/>(bind count limit)"]
        EIC["EmittedSqlInvariantChecker<br/>(no comments, no literals, no ';')"]
    end

    CAPT[("DialectCapabilityTable<br/>(data)")]

    subgraph Exec["Execution"]
        ADO["ADO.NET: SqlClient, Npgsql,<br/>Microsoft.Data.Sqlite, DuckDB.NET"]
        DBX["DatabricksStatementExecutionClient<br/>(REST 2.0, named typed params)"]
    end

    RFB --> TAP --> GSR
    PEP --> TAP
    MSK --> GSR
    GSR -->|"CompileRequest"| TG
    SFC -->|"CompileRequest"| TG
    CSP -.->|"Generate(AST)"| GEN
    TG --> P --> B --> V --> S --> SEC --> COV --> CAP --> GEN --> PB --> EIC
    CAPT --> CAP
    CAPT --> PB
    CAPT --> GEN
    EIC -->|"CompiledSql"| GSE
    GSE --> ADO
    GSE --> DBX
```

`*` Snowflake: generator only, `DialectSupportTier.Experimental`. `CompileRequest` rejects it unless `AllowExperimentalDialect` is set, which only test code may set (enforced by an architecture test).

### 3.2 Pass ordering decision (INV-3)

**Decision:** simplification moves **before** security injection and only ever sees the user's tree. Two independent mechanisms then protect injected predicates:

1. **Opaque marker node.** Every injected RLS predicate is wrapped in `SecurityPredicateExpression` (sealed record, §4.3). The base `SqlAstRewriter` does not descend into it, and every visitor that runs after `AstSecurityVisitor` is forbidden to construct, remove or rewrite it. An architecture test checks this with reflection over all `SqlAstRewriter` subclasses.
2. **Structural post-condition.** After injection, `SecurityCoverageVerifier` walks the final AST and checks that every `NamedTableSource` that resolves to a physical table is wrapped in a secured subquery whose `WHERE` contains the expected `SecurityPredicateId` as a top-level conjunct, in every scope listed in INV-2. Masked columns must be `MaskExpression` nodes. Failure throws `SecurityCoverageException` (fail closed). The verifier runs in production on every compile, not only in tests. Its cost is linear in the node count (budget in NFR-2).

Why not keep the simplifier after injection and add an oracle? With the simplifier before injection, the invariant holds by construction. The oracle (property test G5-c) then checks it continuously instead of carrying it alone. Optimizing the injected predicates themselves has no measurable value.

### 3.3 Source semantics decision

WebSQL users write SQL in Trino syntax, and legacy forwards their text, so they get **backend semantics** today (null ordering, integer division, collation). The AST emitter keeps **backend semantics with Trino syntax**: it translates syntax and functions, but it does not inject semantic shims (for example forced `NULLS LAST` or integer-division rewrites). If it did, results would change compared with legacy, and the differential gate would turn that change into noise. Every intentional deviation is listed in the conformance matrix as `semantic-note`. This needs stakeholder confirmation (OQ-5).

### 3.4 Literal parameterization rules (INV-4, SD-3)

The emitter turns every value into a bind parameter.

- **Bound:** every `LiteralExpression` (string, numeric, boolean value in a comparison, date/time typed literal values, `IntervalLiteralExpression` values), every tenant value, every policy value and every mask argument (mask character, prefix/suffix lengths, HMAC key, jitter decimals).
- **Deduplicated:** identical `(type, value)` pairs within one statement reuse one marker. Every production dialect supports marker reuse (`$n`, `@pN`, `?NNN`, `:pN`). Without reuse, `SELECT a + 1 ... GROUP BY a + 1` fails on PostgreSQL.
- **Emitted inline, closed allow-list** (validated `long` in invariant culture, never text from the input):
  - `LIMIT`/`OFFSET`/`FETCH`/`TOP` row counts (the value is clamped by the gateway anyway);
  - `ORDER BY` and `GROUP BY` ordinals (a bound ordinal would sort by a constant);
  - window frame offsets;
  - `CAST` type parameters (`DECIMAL(p,s)`, `VARCHAR(n)`);
  - `EXTRACT`/`date_trunc`/interval **unit keywords**;
  - `NULL`, `TRUE`, `FALSE` keywords;
  - the canonical `1 = 0` deny-all.
- **Proof:** `EmittedSqlInvariantChecker` scans the emitted text with a dialect-aware lexer. It skips quoted identifiers and rejects any `'`, `--`, `/*`, `;`, dollar quote or `E'` outside them. The only exception is the constant format literals of the dialect's registered `BindExpressionTemplates` (§3.6), which the emitter registers by position. Numeric tokens may appear only at positions the emitter registered in an allow-list position set during emission.

### 3.5 Policy IR (SD-3, INV-5)

Policies are compiled to typed nodes **once**, at the point where they are produced:

- **Structured producers build the AST directly.** These are consent row filters (`ConsentRowFilter`: column, operator, JSON value), the tenant predicate, virtual filters and correlated row-filter subqueries. No text is involved.
- **Admin-authored predicates** (access-profile `RowFilterPredicate`, Casbin row-filter strings) are parsed by `IPolicyExpressionParser`:
  - the canonical policy syntax is Trino SQL (identical to user SQL);
  - strict token guards apply;
  - columns are resolved against the table's catalog columns (allow-list);
  - functions are checked against the function allow-list;
  - named markers `:name` become `PolicyParameterExpression`.
  
  Parse results are cached by `(SHA-256 of text, table identity, catalog version)` in a bounded `MemoryCache`. A parse failure denies the table (fail closed) and produces an operator-visible validation error at configuration load (`GatewayStartupValidator` and options reload).
- `TableAccessDecision` gains a typed `RowFilter` (`PolicyPredicate?`). `CombinedRowFilterSql` remains for the non-AST consumers outside this track (§7.3), but it is **rendered from the typed IR** by the same dialect generator (`PolicyPredicateRenderer`). Text and AST therefore cannot drift.

### 3.6 Reference design: Trino's own JDBC pushdown generator

Trino generates target-dialect SQL itself when it pushes work down to JDBC connectors (`plugin/trino-base-jdbc`, with one `JdbcClient` subclass per database). Its source is the closest production reference for this track. What we adopt and what we reject:

| Trino mechanism (source) | What it does | Decision for Autheris |
|---|---|---|
| `DefaultQueryBuilder` returns `PreparedQuery(String query, List<QueryParameter> parameters)`; an IN list is `nCopies(n, writeFunction.getBindExpression())` | Every value is a `QueryParameter` and is never inlined. Typed setters (`LongWriteFunction`, `SliceWriteFunction`, ...) bind at execution; null values use `setNull`. | **Adopt.** This is the model for `CompiledSql` + `BoundParameter` and typed binders (§4.2). |
| `WriteFunction.getBindExpression()`, e.g. Oracle `TO_DATE(?, 'SYYYY-MM-DD')`, `TO_TIMESTAMP(?, ...)` | A per-type, per-dialect **bind wrapper** around the placeholder, so the remote type is exact. | **Adopt** as `BindExpressionTemplate` per `(dialect, SqlParameterType)` in the capability table (§4.5). Templates are fixed constants reviewed in Phase 3, never data. This also mitigates R-2 (PostgreSQL parameter type inference). |
| Only constants inlined: `ALWAYS_TRUE = "1=1"`, `ALWAYS_FALSE = "1=0"` ("not all databases support booleans") | The only text constants are tautology and contradiction. | **Adopt.** Matches the §3.4 inline allow-list. |
| `BaseJdbcClient.quoted(name)` doubles the quote character; `quoted(catalog, schema, table)` skips empty parts | Identifier delimiting with escaping of the closing delimiter. | **Adopt.** The existing generators already do this; Databricks uses the same rule with backticks (§6.1). |
| `JdbcConnectorExpressionRewriterBuilder`: `withTypeClass("numeric_type", ...)`, `.map("$equal(left: numeric_type, right: numeric_type)").to("left = right")`, `.add(new RewriteStringComparison())` | Declarative, typed, per-dialect expression and function mapping rules. If no rule matches, the expression is simply not pushed down. | **Adopt the declarative style** for per-dialect function mapping (`DialectFunctionMap` data in the capability table instead of `switch` code). **Adapt the fallback:** Trino can evaluate an unpushed expression itself; the gateway cannot, so no matching rule means **reject** (INV-1). |
| `limitFunction()` + `isLimitGuaranteed()`; `topNFunction()` + `supportsTopN()`; `supportsMerge()` defaults to `false` | Capability flags decide what is pushed down. A connector that implements a function without its guarantee flag throws. | **Adopt** as capability flags `LimitGuaranteed`, `SupportsMerge`, ... (§4.5). The gateway's `EnforcedMaxRows` relies on `LimitGuaranteed = true` for every production dialect; a dialect without it is rejected. |
| `SqlServerClient`: `OFFSET/FETCH` with `ORDER BY (SELECT NULL)` instead of `TOP`; `NULLS FIRST/LAST` emulated with a `CASE WHEN col IS NULL THEN 1 ELSE 0 END` helper key; `supportsTopN` is false for char/varchar keys ("remote database can be case insensitive") | Dialect-specific pagination and null-ordering emulation; collation awareness. | The existing SQL Server generator already matches the pagination and null-ordering emulation. The collation note becomes a `semantic-note` in the conformance matrix (backend semantics, §3.3). Trino quotes SQL Server identifiers with `"`, which assumes `QUOTED_IDENTIFIER ON`; we keep brackets, because they do not depend on that session setting. |
| `SQL_SERVER_MAX_LIST_EXPRESSIONS = 500` ("SqlServer supports 2100 parameters ... space for about 4 big IN predicates"); `ORACLE_MAX_LIST_EXPRESSIONS = 1000` | IN-list limits are tracked separately from the bind limit. | **Adopt** the separation (`MaxInListItems` next to `MaxBindParameters`, §5). We keep Oracle at 1,000 (ORA-01795) and SQL Server unbounded except by the bind limit, because the gateway rejects instead of re-planning. |
| **Domain compaction** (`domain-compaction-threshold`, default 256 in the Trino Oracle connector docs): large IN lists are pushed down as a simpler **range** predicate | This widens the remote filter, which is safe in Trino only because Trino re-applies the exact filter on the returned rows. | **Reject (new anti-pattern AP-11).** The gateway does not re-filter, so widening a security predicate would make RLS looser. Over-limit IN lists are rejected (Q-4), never compacted. |
| `varcharLiteral(value)` (quote doubling) is used only for `COMMENT` statements, because Oracle cannot bind them | Literal emission is limited to statements without bind support. | **Not needed.** The compiler emits no DDL, so no literal path exists (INV-4). |
| `SqlFormatter` (Trino AST to Trino SQL text) | Canonical printer of the source dialect. | **Model** for the test-only `TrinoSourceRenderer` used by the round-trip identity gate G3. |

Trino OSS has no Databricks/Spark JDBC client, so there is no upstream reference for §6. Its Delta Lake connector reads table files directly and does not generate SQL.

Sources (Trino master, read 2026-10-10):

- https://github.com/trinodb/trino/blob/master/plugin/trino-base-jdbc/src/main/java/io/trino/plugin/jdbc/DefaultQueryBuilder.java
- https://github.com/trinodb/trino/blob/master/plugin/trino-base-jdbc/src/main/java/io/trino/plugin/jdbc/BaseJdbcClient.java
- https://github.com/trinodb/trino/blob/master/plugin/trino-base-jdbc/src/main/java/io/trino/plugin/jdbc/expression/JdbcConnectorExpressionRewriterBuilder.java
- https://github.com/trinodb/trino/blob/master/plugin/trino-oracle/src/main/java/io/trino/plugin/oracle/OracleClient.java
- https://github.com/trinodb/trino/blob/master/plugin/trino-sqlserver/src/main/java/io/trino/plugin/sqlserver/SqlServerClient.java
- https://trino.io/docs/current/connector/oracle.html (domain compaction threshold)

---

## 4. Interfaces and Types

Namespaces are relative to `TrinoSqlEngine` unless stated otherwise. All new types are `sealed`, records are immutable, collections are `ImmutableArray`/`FrozenDictionary`, and nullable is enabled.

### 4.1 Compile API (replaces `RewriteRls` and `GenerateGovernedSql`)

```csharp
namespace TrinoSqlEngine;

public interface ISqlEngine
{
    CompiledSql Compile(ReadOnlyMemory<char> sql, CompileRequest request, CancellationToken cancellationToken);

    // Unchanged:
    (SqlBaseParser.SingleStatementContext Tree, CommonTokenStream Tokens) Parse(ReadOnlyMemory<char> sql, SqlTokenSecurityOptions? tokenOptions = null, CancellationToken cancellationToken = default);
    (SqlBaseParser.StandaloneExpressionContext Tree, CommonTokenStream Tokens) ParseExpression(ReadOnlyMemory<char> sql, SqlTokenSecurityOptions? tokenOptions = null, CancellationToken cancellationToken = default);
    SqlQueryMetadata Analyze(ReadOnlyMemory<char> sql);
    SqlQueryMetadata Analyze(ReadOnlyMemory<char> sql, SqlTokenSecurityOptions? tokenOptions, CancellationToken cancellationToken = default);
}

/// <summary>Immutable compile input. Replaces the mutable RlsOptions bag on the governed path.</summary>
public sealed record CompileRequest
{
    public required TargetSqlDialect TargetDialect { get; init; }            // no default (F-2)
    public required GovernancePolicy Policy { get; init; }
    public required SqlTokenSecurityOptions TokenGuards { get; init; }       // kept, defense in depth (AP-3)
    public StatementPermissions Statements { get; init; } = StatementPermissions.ReadOnly;
    public long EnforcedMaxRows { get; init; }
    public bool TranslateTrinoDateFunctions { get; init; }
    public bool EnforceCatalogProjection { get; init; }
    public IReadOnlySet<string>? AllowedFunctions { get; init; }
    public IReadOnlySet<string>? AllowedTableFunctions { get; init; }
    public bool AllowExperimentalDialect { get; init; }                      // test-only; architecture test enforces
}

[Flags]
public enum StatementPermissions { ReadOnly = 0, Insert = 1, Update = 2, Delete = 4, Merge = 8 }

/// <summary>Typed governance input (replaces string providers and PolicyFiltersAreTargetDialectSql).</summary>
public sealed record GovernancePolicy
{
    public required IPolicyPredicateProvider RowFilters { get; init; }
    public required IColumnMaskProvider Masks { get; init; }
    public required ITableCatalog Catalog { get; init; }                     // columns, data types, tenant column
    public required TenantBinding Tenant { get; init; }
    public DmlGuardOptions Dml { get; init; } = DmlGuardOptions.Strict;
    public IReadOnlySet<string> TablesWithConsentRowFilter { get; init; } = FrozenSet<string>.Empty;
    public IReadOnlySet<string> TablesWithMaskedColumns { get; init; } = FrozenSet<string>.Empty;
    public RowFilterSubqueryStrategy SubqueryStrategy { get; init; }
}

public sealed record TenantBinding(string ParameterName, object Value, SqlParameterType Type);

public sealed record DmlGuardOptions(
    bool EnforceWithCheckOption,
    bool RequireTenantColumnInInsert,
    bool DisallowTenantColumnModificationInUpdate,
    bool RejectUnfilteredDml,
    bool RejectMaskedColumnsInDml,
    bool RejectMaskedColumnsInPredicates,
    bool RejectConsentFilteredInsert,
    bool RejectWholeRowReferencesInDml)
{
    public static DmlGuardOptions Strict { get; } = new(true, true, true, true, true, true, true, true);
}
```

### 4.2 Output types

```csharp
namespace TrinoSqlEngine.Ast.Emit;

public sealed record CompiledSql(
    string Sql,
    ImmutableArray<BoundParameter> Parameters,
    TargetSqlDialect Dialect,
    SqlStatementClass StatementClass,
    ImmutableArray<SecurityPredicateId> AppliedPredicates,
    string CompilerVersion);

public enum SqlStatementClass { Select, Insert, Update, Delete, Merge }

/// <param name="Marker">Exact marker text in Sql (e.g. "$3", "@p2", ":p4", "?5").</param>
/// <param name="Name">Provider binding name (SqlClient "@p2", Npgsql positional "", Oracle "p4", Databricks "p4").</param>
public readonly record struct BoundParameter(
    string Marker,
    string Name,
    int Ordinal,
    object? Value,
    SqlParameterType Type,
    ParameterOrigin Origin);

public enum ParameterOrigin { ClientNamed, ClientPositional, QueryLiteral, Tenant, Policy, Mask }

public enum SqlParameterType
{
    String, Int32, Int64, Decimal, Double, Boolean, Date, Timestamp, TimestampTz, Time, Binary, Null
}

/// <summary>Binds CompiledSql to a provider command. One implementation per ADO.NET provider family.</summary>
public interface ICompiledSqlBinder
{
    bool CanBind(TargetSqlDialect dialect);
    void Bind(DbCommand command, CompiledSql compiled, IReadOnlyDictionary<string, object?> clientParameterValues);
}
```

Client named parameters (`@name`, `:name`) appear in the AST as `ParameterReference` with `ParameterOrigin.ClientNamed`. The binder resolves their values from `clientParameterValues`, so no SQL text is rewritten (F-5).

### 4.3 New and changed AST nodes

```csharp
namespace TrinoSqlEngine.Ast.Nodes;

/// <summary>Injected RLS predicate. Opaque to every rewriter after AstSecurityVisitor (INV-3).</summary>
public sealed record SecurityPredicateExpression(
    Expression Predicate,
    SecurityPredicateId Id,
    SecurityScope Scope) : Expression;

public readonly record struct SecurityPredicateId(string TableIdentity, int Ordinal);

public enum SecurityScope { Root, Subquery, CteBody, SetOperationBranch, Lateral, ScalarSubquery, ExistsSubquery, InSubquery, DmlTarget, DmlSource, MergeSource, MergeTarget }

/// <summary>A value bound by the gateway (tenant, policy, mask argument). Never originates from request text.</summary>
public sealed record PolicyParameterExpression(string Name, SqlParameterType Type) : Expression;

/// <summary>Semantic column mask. The generator renders it per dialect (replaces mask TrustedSqlExpression).</summary>
public sealed record MaskExpression(MaskKind Kind, ColumnReference Column, MaskArguments Arguments) : Expression;

public enum MaskKind { Nullify, Redact, PartialMask, Hmac, GeoJitter, Constant }

public sealed record MaskArguments(
    PolicyParameterExpression? Constant = null,
    PolicyParameterExpression? KeepPrefix = null,
    PolicyParameterExpression? KeepSuffix = null,
    PolicyParameterExpression? MaskChar = null,
    PolicyParameterExpression? HmacKey = null,
    int? Decimals = null);                                    // structural, inline-allowed

/// <summary>MERGE (grammar: SqlBase.g4 #merge / mergeCase). WHEN NOT MATCHED BY SOURCE is not supported by the grammar.</summary>
public sealed record MergeStatement(
    NamedTableSource Target,
    TableSource Source,
    Expression On,
    ImmutableArray<MergeClause> Clauses) : SqlStatement;

public abstract record MergeClause(Expression? Condition) : SqlNode;
public sealed record MergeUpdateClause(Expression? Condition, ImmutableArray<UpdateAssignment> Assignments) : MergeClause(Condition);
public sealed record MergeDeleteClause(Expression? Condition) : MergeClause(Condition);
public sealed record MergeInsertClause(Expression? Condition, ImmutableArray<SqlIdentifier>? Columns, ImmutableArray<Expression> Values) : MergeClause(Condition);

// REMOVED at cutover: public sealed record TrustedSqlExpression(string Sql) : Expression;
```

### 4.4 Policy providers (typed)

```csharp
namespace TrinoSqlEngine.Governance;

public interface IPolicyPredicateProvider
{
    bool ShouldApplyPolicy(TableIdentity table);
    PolicyPredicate GetPredicate(TableIdentity table);        // deny-all is PolicyPredicate.DenyAll, never null
}

public interface IColumnMaskProvider
{
    bool HasMask(TableIdentity table, string column);
    MaskSpec GetMask(TableIdentity table, string column);
}

public sealed record PolicyPredicate(
    Expression Expression,                                    // contains PolicyParameterExpression, never values
    FrozenDictionary<string, PolicyValue> Parameters,
    string Fingerprint)                                       // stable hash for plan cache
{
    public static PolicyPredicate DenyAll { get; } = /* 1 = 0 */;
    public PolicyPredicate And(PolicyPredicate other);        // typed AND-merge; conflicting parameter -> PolicyConflictException
}

public readonly record struct PolicyValue(object? Value, SqlParameterType Type);

public sealed record MaskSpec(MaskKind Kind, MaskArguments Arguments, FrozenDictionary<string, PolicyValue> Parameters);

public interface IPolicyExpressionParser
{
    /// <summary>Parses canonical (Trino) policy text once; cached; throws PolicyParseException (fail closed).</summary>
    PolicyPredicate Parse(string policySql, PolicyParseContext context);
}

public sealed record PolicyParseContext(
    TableIdentity Table,
    IReadOnlyList<string> AllowedColumns,
    IReadOnlySet<string> AllowedFunctions,
    IReadOnlyDictionary<string, PolicyValue> Parameters,
    long CatalogVersion);

/// <summary>Renders typed IR to dialect text for the non-AST string consumers (single source of truth).</summary>
public interface IPolicyPredicateRenderer
{
    (string Sql, IReadOnlyDictionary<string, object?> Parameters) Render(PolicyPredicate predicate, TargetSqlDialect dialect, string parameterPrefix = "@__gql_");
}
```

### 4.5 Dialect capability table

```csharp
namespace TrinoSqlEngine.Ast.Capabilities;

public enum DialectSupportTier { Production, Experimental, Internal }   // Internal = ANSI (tests and audit rendering only)

public sealed record DialectCapabilities(
    TargetSqlDialect Dialect,
    DialectSupportTier Tier,
    int MaxBindParameters,
    int? MaxInListItems,
    int MaxIdentifierLength,                // characters; Oracle and PostgreSQL measured in bytes, see IdentifierLengthUnit
    IdentifierLengthUnit IdentifierLengthUnit,
    char IdentifierOpenQuote,
    char IdentifierCloseQuote,
    ParameterMarkerStyle MarkerStyle,
    bool SupportsMarkerReuse,
    PaginationStyle Pagination,
    bool SupportsWithTies,
    bool SupportsNullsFirstLast,
    BooleanRepresentation Booleans,
    bool SupportsMerge,
    bool SupportsLateral,
    bool SupportsGroupingSets,
    bool SupportsFilterClause,
    bool SupportsTryCast,
    bool LimitGuaranteed,                   // Trino isLimitGuaranteed(); required for production dialects
    FrozenDictionary<SqlParameterType, string> BindExpressionTemplates,   // Trino WriteFunction.getBindExpression(), e.g. "CAST({0} AS bigint)"
    DialectFunctionMap Functions,           // declarative Trino-function -> dialect rules (Trino rewriter DSL style); no match = reject
    string LimitSource);                    // citation key into §5 table

/// <summary>Declarative function mapping, analogous to Trino's JdbcConnectorExpressionRewriterBuilder.map(...).to(...).</summary>
public sealed record DialectFunctionMap(FrozenDictionary<string, FunctionRewriteRule> Rules);
public sealed record FunctionRewriteRule(string TrinoName, ImmutableArray<string> ArgumentTypeClasses, string TargetTemplate);

public enum ParameterMarkerStyle { AtNamedOrdinal /* @p0 */, DollarOrdinal /* $1 */, QuestionOrdinal /* ?1 */, ColonNamedOrdinal /* :p1 */, ColonOrdinal /* :1 */ }
public enum PaginationStyle { LimitOffset, OffsetFetch, TopOrOffsetFetch }
public enum BooleanRepresentation { Native, Integer01, Number1 }
public enum IdentifierLengthUnit { Characters, Bytes }

public interface IDialectCapabilityProvider
{
    DialectCapabilities Get(TargetSqlDialect dialect);       // unknown dialect -> ArgumentOutOfRangeException
}

/// <summary>Typed, auditable limit rejection (INV-7). Replaces DialectLimitExceededException.</summary>
public sealed class SqlLimitExceededException : SecurityException
{
    public SqlLimitKind Kind { get; }
    public TargetSqlDialect Dialect { get; }
    public long Requested { get; }
    public long Maximum { get; }
}

public enum SqlLimitKind { BindParameters, InListItems, IdentifierLength, QueryLength, NestingDepth }
```

`Autheris.Infrastructure.Persistence.DatabaseParameterBudgetProvider` delegates to `IDialectCapabilityProvider` for the dialects that have a `TargetSqlDialect` mapping (F-6). The chunked executor keeps its own safety buffer.

### 4.6 Generator contract

```csharp
namespace TrinoSqlEngine.Ast.Generators;

public interface ISqlDialectGenerator
{
    TargetSqlDialect TargetDialect { get; }
    DialectCapabilities Capabilities { get; }
    CompiledSql Generate(SqlStatement statement, ParameterSource values, CancellationToken cancellationToken = default);
    void GenerateExpression(Expression expression, ref ValueStringBuilder builder, SqlEmitterContext context);
}

/// <summary>Supplies gateway-bound values for PolicyParameterExpression nodes; query literals come from the AST.</summary>
public sealed record ParameterSource(
    FrozenDictionary<string, PolicyValue> PolicyValues,
    IReadOnlyDictionary<string, object?> ClientNamedValues);

public sealed class SqlEmitterContext
{
    public TargetSqlDialect Dialect { get; }
    public DialectCapabilities Capabilities { get; }
    public string BindValue(object? value, SqlParameterType type, ParameterOrigin origin);   // dedup + marker
    public string BindPolicy(PolicyParameterExpression parameter);
    public void RegisterInlineNumericPosition(int start, int length);                     // for EmittedSqlInvariantChecker
    public ImmutableArray<BoundParameter> Parameters { get; }
}
```

The emitter buffer stays a pooled `ValueStringBuilder` (AP-7: "zero allocation" applies to the buffer only).

### 4.7 Compiler pipeline (internal)

```csharp
namespace TrinoSqlEngine;

internal sealed class GovernedSqlCompiler(
    FastSqlEngine parser,
    IDialectCapabilityProvider capabilities,
    TimeProvider timeProvider)
{
    public CompiledSql Compile(ReadOnlyMemory<char> sql, CompileRequest request, CancellationToken ct)
    {
        // 1 dialect tier gate  2 token guards + parse  3 build  4 validate  5 simplify (user tree)
        // 6 secure  7 coverage verify  8 capability validate  9 emit  10 bind-count check  11 emitted-text invariant check
    }
}
```

`FastSqlEngine.Compile` delegates to it. Telemetry: span `sql.compile` with attributes `sql.target_dialect`, `sql.statement_class`, `sql.compiler_version` and `sql.bind_count`. Counters: `autheris.sql.compile.rejected{dialect,reason}` and `autheris.sql.limit_rejected{dialect,kind}`. No literals and no tenant values (NFR-6).

---

## 5. Dialect Capability Table (Data, With Sources)

Values marked **(verify)** are not confirmed by vendor documentation and must be confirmed by the named execution test before cutover. Until then the conservative value applies (fail closed).

| Dialect | Tier | Bind limit | IN-list limit | Identifier max | Markers | Pagination | Notes and sources |
|---|---|---|---|---|---|---|---|
| PostgreSQL | Production | 65,535 | none (bounded by bind limit) | 63 bytes | `$n` | `LIMIT/OFFSET`, `FETCH ... WITH TIES` | Bind: Int16 count in the Bind message [PG-PROTO]. Identifier: `NAMEDATALEN-1`, silently truncated, so the gateway **rejects** names longer than 63 bytes [PG-LEX]. |
| SQL Server | Production | 2,100 | none (bounded by bind limit) | 128 chars | `@pN` | `TOP` / `OFFSET ... FETCH` (synthetic `ORDER BY (SELECT NULL)`) | 2,100 parameters per RPC [MSSQL-CAP]. No `NULLS FIRST/LAST` syntax (emulated with `CASE`, already built). |
| SQLite | Production | 32,766 | none | unbounded | `?NNN` | `LIMIT/OFFSET` | `SQLITE_MAX_VARIABLE_NUMBER` default 32,766 since 3.32.0 [SQLITE-LIM]. The value is checked at startup with `sqlite3_limit(SQLITE_LIMIT_VARIABLE_NUMBER, -1)` against the bundled engine; the effective limit is the minimum of table and runtime **(verify, WP-A1 test)**. |
| Oracle | Production (compiler); `Production` after WP-F5 (SD-8, §16.6) | 32,767 **(verify)** | **1,000** | 128 bytes (12.2+) | `:pN` | `OFFSET ... FETCH` | IN-list: ORA-01795 [ORA-01795]. The bind limit is not in the Oracle logical-limits reference [ORA-LIM]. 32,767 follows jOOQ [JOOQ]; a binary-search probe against the Oracle Free container confirms it. Identifier: 128 bytes since 12.2 [ORA-NAMES]. The gateway does not split IN-lists (Q-4: reject). |
| DuckDB | Production | 65,535 **(verify)** | none | unbounded | `$n` | `LIMIT/OFFSET` | No published limit [DUCK-PREP]. The conservative value is checked by an in-process probe. |
| Databricks | Production | 1,000 **(verify, provisional)** | none known | 255 chars | `:pN` (named) | `LIMIT/OFFSET` | Named markers need DBR 12.1+, unnamed `?` DBR 13.3+, and the two styles cannot be mixed [DBX-PARAM]. No documented parameter count limit for the Statement Execution API [DBX-SEA]. 1,000 is a provisional fail-closed budget; the live warehouse probe (WP-C5) raises it to the measured value minus 10%. Unity Catalog object names have at most 255 characters and are stored lower-case [DBX-UC]. |
| Snowflake | **Experimental** | 1,000 **(unverified)** | 16,384 **(unverified)** | 255 chars | `:N` | `LIMIT/OFFSET` | No execution target (SD-5). Never selectable in production (`DialectSupportTier.Experimental`). |
| ANSI | Internal | n/a | n/a | n/a | `?n` | `OFFSET ... FETCH` | Used only for audit rendering and tests. `CompileRequest` rejects it. |

The existing `QueryLength` limit (65,536 chars), nesting depth (100) and parse-tree depth (3,000) stay engine-wide limits in `FastSqlEngine` (INV-7).

Sources:

- [PG-PROTO] https://www.postgresql.org/docs/current/protocol-message-formats.html
- [PG-LEX] https://www.postgresql.org/docs/current/sql-syntax-lexical.html
- [MSSQL-CAP] https://learn.microsoft.com/en-us/sql/sql-server/maximum-capacity-specifications-for-sql-server
- [SQLITE-LIM] https://www.sqlite.org/limits.html
- [ORA-01795] https://docs.oracle.com/error-help/db/ora-01795/
- [ORA-LIM] https://docs.oracle.com/en/database/oracle/oracle-database/19/refrn/logical-database-limits.html
- [ORA-NAMES] https://docs.oracle.com/en/database/oracle/oracle-database/19/sqlrf/Database-Object-Names-and-Qualifiers.html
- [JOOQ] https://www.jooq.org/doc/latest/manual/sql-building/dsl-context/custom-settings/settings-inline-threshold/
- [DUCK-PREP] https://duckdb.org/docs/current/sql/query_syntax/prepared_statements.html
- [DBX-PARAM] https://docs.databricks.com/aws/en/sql/language-manual/sql-ref-parameter-marker
- [DBX-SEA] https://docs.databricks.com/api/workspace/statementexecution
- [DBX-UC] https://docs.databricks.com/aws/en/data-governance/unity-catalog/requirements and https://docs.databricks.com/sql/language-manual/sql-ref-names.html
- [DBX-MERGE] https://docs.databricks.com/aws/en/delta/merge

---

## 6. Databricks Dialect Design (SD-7)

### 6.1 Generator (`DatabricksDialectGenerator`)

| Concern | Emission rule |
|---|---|
| Identifiers | Always delimited with backticks. An embedded `` ` `` is doubled to ` `` `. Names are emitted as written, without folding. Unity Catalog lower-cases object names and column lookups are case-insensitive. Names longer than 255 characters are rejected. |
| Qualified names | `catalog.schema.table` (Unity Catalog three-part name). Each part is delimited separately. Four or more parts are rejected. The gateway's `TableIdentifier` decides whether a default catalog is prepended from data-source options (`Databricks:Catalog`). |
| Parameters | Named `:p1..:pN` only (DBR 12.1+). Named and positional markers are never mixed. The `BoundParameter.Name` is `p1..pN`. |
| String literals | **Never emitted.** Spark/Databricks interprets backslash escapes inside `'...'` literals (see `DatabaseDialect.cs:145`). All values are bound (§3.4). The generator throws `InvalidOperationException` if a string `LiteralExpression` reaches it, which is a defense-in-depth assertion. |
| Booleans | Native `TRUE`/`FALSE`. |
| Pagination | `LIMIT n` / `LIMIT n OFFSET m` (`OFFSET` support **(verify)** on the Spark proxy and the live warehouse). `FETCH FIRST ... WITH TIES` is rejected (`SupportsWithTies = false`). |
| Null ordering | `NULLS FIRST/LAST` are passed through when written. Defaults are backend semantics (§3.3; Spark sorts ascending with nulls first). Listed as `semantic-note`. |
| Types (`CAST`/`TRY_CAST`) | `varchar(n)`/`char(n)`/`varchar` -> `STRING`; `double` -> `DOUBLE`; `real` -> `FLOAT`; `decimal(p,s)` -> `DECIMAL(p,s)`; `timestamp` -> `TIMESTAMP_NTZ` **(verify DBR 13.3+)**; `timestamp with time zone` -> `TIMESTAMP`; `varbinary` -> `BINARY`; `json` is rejected. `TRY_CAST` is native. |
| Date functions (`TranslateTrinoDateFunctions`) | `date_add(unit, n, x)` -> `timestampadd(UNIT, n, x)`; `date_diff(unit, a, b)` -> `timestampdiff(UNIT, a, b)`; `date_trunc('unit', x)` -> `date_trunc('UNIT', x)` with a bound unit keyword from a closed set; `now()` -> `current_timestamp()`; `x +/- INTERVAL` -> `x +/- INTERVAL n UNIT` with an inline structural unit and a bound amount. |
| Functions | `strpos(s, t)` -> `instr(s, t)`; `regexp_like` -> `rlike` (only if allow-listed); `approx_distinct` -> `approx_count_distinct`; `arbitrary` -> `any_value`; `IS DISTINCT FROM` is native; `||` is native. Anything not in `SupportedFunctions` is rejected (INV-1). |
| Unsupported, rejected | `UNNEST` (would need `LATERAL VIEW explode`), `GROUPS` frames, `MATCH_RECOGNIZE`, `TABLESAMPLE`, Trino time travel (already rejected by token guards). `LATERAL` subqueries are accepted only after the Spark proxy confirms them **(verify)**. |
| Set operations | `UNION [ALL]`, `INTERSECT [ALL]`, `EXCEPT [ALL]` are native. |
| Grouping | `GROUPING SETS`, `ROLLUP`, `CUBE` and the aggregate `FILTER (WHERE ...)` clause are native. |

### 6.2 DML on Delta

Databricks has no `WITH CHECK OPTION`, and Delta `CHECK` constraints are static per table, so they cannot carry a per-request tenant. Check-option semantics are therefore enforced **by the compiler for every dialect, Databricks included**, without relying on any database feature:

| Statement | Enforcement (compile time, fail closed) |
|---|---|
| `INSERT ... VALUES` | The tenant column must be in the column list. Each row's tenant value must be a literal equal to the expected tenant. The verified literal is then **replaced** by the gateway-bound `PolicyParameterExpression(tenant)`, so the executed value never comes from the request. |
| `INSERT ... SELECT` / set operations | The tenant projection position must be a literal equal to the expected tenant (existing `VerifyInsertSource`). It is replaced by the bound tenant parameter. The source query gets full RLS (INV-2, `SecurityScope.DmlSource`). Wildcard projections are rejected. |
| `UPDATE` | RLS is AND-ed into `WHERE` as a `SecurityPredicateExpression`. The unfiltered-DML and tautology guard runs on the user's `WHERE` before injection. Assignments to the tenant column and to masked columns are rejected. Delta tables only; a non-Delta target is a backend error that is surfaced as a typed error. |
| `DELETE` | As for `UPDATE`, without assignments. |
| `MERGE` | Target RLS is AND-ed into `ON` as a `SecurityPredicateExpression(Scope = MergeTarget)`. Rows of another tenant can therefore never be `MATCHED`, which means they can never be updated or deleted. The source table or subquery gets full RLS (`MergeSource`). `WHEN MATCHED ... UPDATE` must not assign the tenant column or masked columns. `WHEN NOT MATCHED ... INSERT` must list the tenant column with a literal equal to the expected tenant; the literal is replaced by the bound parameter. A trivially true `ON` (after simplification) or an `ON` without a column reference is rejected as unfiltered DML. `WHEN NOT MATCHED BY SOURCE` is not in the grammar, so it fails at parse time. Tables with consent row filters reject `MERGE` that contains an `INSERT` clause (as SQ-07 does for `INSERT`). |

Databricks executes one statement per Statement Execution API call and has no multi-statement transaction. The executor never sends `BEGIN`/`COMMIT` for Databricks. Delta guarantees ACID per statement. The `MERGE` multiple-match error (`DELTA_MULTIPLE_SOURCE_ROW_MATCHING_TARGET_ROW_IN_MERGE`) [DBX-MERGE] is surfaced as a typed DML error.

### 6.3 Runtime connector

- **Choice:** the Databricks SQL **Statement Execution API 2.0** (REST) over `HttpClient`, using `SecureOutboundHttp.CreatePrimaryHandler` and `SsrfProtectionHandler` with a host allow-list. It needs no native driver, supports named and typed parameters, and uses one statement per call. Rejected alternatives: the Simba ODBC driver (native install and license acceptance in CI) and the ADBC C# Databricks driver (younger; can be re-evaluated later).
- **Contract:** `DatabricksSqlConnection : DbConnection` and `DatabricksSqlCommand : DbCommand` adapters (minimal: `ExecuteReaderAsync` and `ExecuteNonQueryAsync`). `SqlConnectionFactory` returns them for `DatabaseDialect.Databricks`, so `GovernedSqlExecutor` and the binder work unchanged. Results use `INLINE` + `JSON_ARRAY` with `byte_limit`, and a typed reader maps the result manifest schema. `EXTERNAL_LINKS` is out of scope; results over the byte limit raise a typed "result too large" error.
- **Options:** `DataSources:<name>:Provider = databricks` plus:

  ```json
  "Databricks": {
    "Host": "adb-123.azuredatabricks.net",
    "WarehouseId": "abc123",
    "Catalog": "main",
    "Schema": "default",
    "Auth": { "Mode": "OAuthM2M", "ClientId": "...", "ClientSecretRef": "kv://databricks-sp-secret" },
    "WaitTimeoutSeconds": 30,
    "ByteLimit": 26214400
  }
  ```

  `Auth.Mode` is `OAuthM2M` (default) or `Pat`. Secrets come only through `IKeyVaultSecretProvider`. `GatewayStartupValidator` fails startup when the host is missing or not on the allow-list, the auth mode is unknown, or a plaintext secret is used outside Development.

---

## 7. Consumer Migration

### 7.1 Engine and options

| Item | Change |
|---|---|
| `ISqlEngine` | Add `Compile` (WP-A8). At cutover, remove `RewriteRls` (2 overloads) and `GenerateGovernedSql` (3 overloads). |
| `FastSqlEngine` | Remove `SqlRewriterEngine` (line 232), the `RewriteRls` dispatch (748-776), `RunLegacyRewrite` (778-787) and the `ShadowDualRun` branch. `GenerateGovernedSql` becomes `GovernedSqlCompiler`. |
| `RlsOptions` | Removed from the governed path at cutover. Its token-guard properties move to `SqlTokenSecurityOptions` (already exists) and its DML switches to `DmlGuardOptions`. `RewriterEngine` and `PolicyFiltersAreTargetDialectSql` are deleted. `RlsOptions` stays only if `SqlQueryAnalyzer` still needs it; otherwise it is deleted. |
| `GatewayOptions.WebSql.SqlRewriterEngine` | Deleted. `GatewayStartupValidator` **fails startup** if `WebSql:SqlRewriterEngine` is still present in configuration. The message says the key was removed and that no engine switch exists. Without this, operators could believe they still have a kill switch. |
| `appsettings*.json`, Helm, compose | No current file sets the key (verified). Phase 6 checks deployment samples under `deploy/` and `charts/` again. |
| DI (`GatewaySqlEngineServiceExtensions`) | Register `IDialectCapabilityProvider` (singleton), `IPolicyExpressionParser` (singleton, bounded cache), `IPolicyPredicateRenderer`, `ICompiledSqlBinder` implementations (one per provider) and `DatabricksStatementExecutionClient` (typed `HttpClient`). `ISqlEngine` stays `FastSqlEngine`. |

### 7.2 Application consumers

| Consumer | Migration |
|---|---|
| `GovernedSqlRewriter` (`src/Autheris.Application/Sql/Services/GovernedSqlRewriter.cs`) | Stage 3 collects `PolicyPredicate` per table (typed tenant predicate with a bound `__autheris_tenant` parameter, F-3) and `MaskSpec` per column. Stage 4 builds a `CompileRequest` instead of `RlsOptions` (lines 666-704). Stage 5 calls `_sqlEngine.Compile`, and the plan cache stores `CompiledSql`. The `engine:` fingerprint segment (714) and the engine argument to `ComputePolicyHash` (725) are removed. `IsExecutable` gains Databricks (WP-C2). `GovernedRewrite.Sql` becomes `GovernedRewrite.Compiled` (`CompiledSql`); `InternalParameters` are merged into `CompiledSql.Parameters`. |
| `GovernedSqlExecutionService` / `GovernedSqlExecutor` | Binding goes through `ICompiledSqlBinder`. `RestoreClientParameters` and `ClientParameterPlaceholderPrefix` handling are deleted (F-5). Audit records store `sql.compiler_version`, dialect and bind count, plus the redacted SQL via `AnonymizeSqlForAudit`, which is kept. |
| `ICompiledSqlQueryPlanCache` / `CompiledSqlQueryPlanCache` | The value type changes from `string` to `CompiledSql`, and the engine string disappears from `ComputePolicyHash`. The key is unchanged otherwise: query hash, dialect, tenant, policy hash, data source. Values in `CompiledSql` derived from policy are covered by the policy hash; client named values are bound per request and never cached. |
| `SqlFilterCompiler.Compile` | Returns `PolicyPredicate` (a typed `ExistsExpression` correlated to `autheris_target`) instead of a string. The `string.Replace` alias rewrite becomes an AST rewrite of the alias binding (F-4). A string-returning facade used by non-AST consumers renders through `IPolicyPredicateRenderer`. `RewriterEngine = "AstCompiler"` is deleted. |
| `RowFilterSqlBuilder` / `ConsentResolutionService` | `BuildCombinedRowFilterParameterized` builds `PolicyPredicate` directly from `ConsentRowFilter` (column, operator, JSON value). Every value becomes a `PolicyParameterExpression`; `IN` gets one parameter per item and is subject to the dialect IN-list limit. The string API renders from the IR. |
| `TableAccessDecision` (`Autheris.Domain`) | Adds `PolicyPredicate? RowFilter`. `WithMandatoryPredicate` and `TableAccessPolicy.Restrict` merge with `PolicyPredicate.And`, where a conflicting parameter causes a deny (existing rule). `CombinedRowFilterSql` and `MandatoryRowPredicateSql` are rendered from the IR. Domain must not reference `TrinoSqlEngine` (architecture rule), so the typed predicate is carried as an opaque `IRowPolicy` abstraction in Domain and implemented in Application. Phase 3 should confirm the layering. |
| `TableAccessPolicy` (`profile.RowFilterPredicate`, line 538) | Parsed through `IPolicyExpressionParser` at profile load. A parse failure denies the table and is reported by startup/reload validation. |
| `SqlDataMaskingProvider.GetMaskExpressionForRule` | Returns `MaskSpec`. The HMAC key name joins `MaskSpec.Parameters`. The string variant is kept for non-AST consumers and renders through the generator. |
| `SqlDataSourceExecutor` (568-595) | Uses `SqlDialectMapper.ToTargetDialect` (explicit; throws for unmapped dialects) instead of `_ => PostgreSql` (F-1). |
| `SqlDialectMapper` | Adds `DatabaseDialect.Databricks <-> TargetSqlDialect.Databricks` and `DuckDb` where a `DatabaseDialect` exists. `IsExecutable` adds Databricks. |
| `DatabaseParameterBudgetProvider` | Delegates to the capability table (F-6). |
| `CrossSourcePlanner` (PLAN-15 owned file) | After PLAN-15 merges, it switches to `Generate(...)` returning `CompiledSql` and binds DuckDB `$n` parameters (WP-X3). Until then, a transitional `ISqlDialectGenerator.GenerateInlineForInternalPlan` method keeps it compiling. That method is internal and allowed only for in-process DuckDB staging plans (an architecture test pins the single caller), and it is deleted in WP-X3. |

### 7.3 Out of scope (string consumers kept, rendered from IR)

`CombinedRowFilterSql` is also read by: `GovernedDataQueryService`, `GatewayExecutionService.FilterRows`, `GovernedConnectorReader`, the lakehouse executors (`DeltaLakeDataSourceExecutor`, `LakehouseDataSourceExecutor`, `IcebergRestCatalogFederationService`), `StreamRlsPolicyEnforcer`, `GovernedProcedureExecutionService`, `SqlProcedureRowScopeResolver`, `TreeSqlCompiler` (GraphQL, ADR-004), `DuckDbOlapEndpoints`, `FederatedStagingService`, `ConsentCacheService` and `CachedConsentEnvelope`. These pipelines are not the WebSQL compiler. After WP-D1 they consume text rendered from the typed IR, so there is one source and no drift. Moving them onto typed IR is a follow-up track (OQ-4).

### 7.4 Documentation references (Phase 6 checklist input)

- `docs/features/f-dialect-01-ast-target-dialect-pushdown.md`: single engine, dialect tiers, Databricks, the bind-everything rule, the capability table, `MERGE`. Remove "proven/impossible" wording (AP-1).
- `docs/features/f-data-02-governed-websql.md`: remove engine selection, add the Databricks data-source configuration and typed limit errors.
- `docs/features/f-gov-09-virtual-filters.md`, `f-gov-14-federated-virtual-filters.md`: typed predicate output.
- `src/TrinoSqlEngine/README.md` (lines 22-23, 65, 96, 394 describe `RlsListener`): rewrite in the cutover commit, because it is code-adjacent.
- `docs/adr/ADR-017-*.md`: amendment §13.
- `docs/features/README.md`: register any new feature doc (none is required; F-DIALECT-01 is updated).

---

## 8. Security Invariants (Carried From the PRD, Adapted)

### 8.1 Invariant-to-evidence mapping (Phase 3 refines)

| ID | Invariant | Example tests | Property/generated | Fuzz/differential/execution |
|---|---|---|---|---|
| INV-1 | Fail closed on unknown rules, nodes, functions, dialects and capabilities | `AstBuilderFailLoudTests`, new `DialectCapabilityValidatorTests` | G5-a: generated ASTs with unsupported nodes always throw | Grammar fuzz: no unhandled exception type other than the typed rejections |
| INV-2 | Complete policy coverage in all scopes, including DML and `MERGE` source/target | `AstSecurityVisitor{Rls,Cte,Dml}Tests`, new `MergeSecurityTests` | G5-c: `SecurityCoverageVerifier` on every generated query | G6 row visibility oracle on every engine |
| INV-3 | Predicate preservation | `SecurityPredicateOpacityTests` (architecture) | G5-c: injected IDs present at the expected scope after the full pipeline | Stryker mutants on the simplifier and visitor are killed |
| INV-4 | Values bound, identifiers quoted and resolved | `LiteralParameterizationTests`, `IdentifierQuotingTests` per dialect | G5-d: emitted text has zero literal tokens outside allow-listed positions | Hostile identifier corpus executed on all engines |
| INV-5 | No trusted raw fragments (strengthened: `TrustedSqlExpression` deleted) | `PolicyExpressionParserTests` with hostile values | G5-e: generated hostile policy values round-trip as parameters | Execution with hostile values: exact match, no injection |
| INV-6 | No source comments or whitespace in output | `SafeTokenEmissionSql3Tests` | G5-d | `EmittedSqlInvariantChecker` in production |
| INV-7 | Bounded resources, typed limit errors | `DialectLimitTests` per dialect (bind, IN-list, identifier) | G5-f: generated near-limit queries | Container probes for the verify-marked limits |
| INV-8' | Compiler transparency: compiler version and dialect in audit and span | `AuditRecordTests` | n/a | n/a |
| INV-10 (new) | DML check option: no write can produce or modify a row outside the caller's tenant, and masked columns are not writable | `AstSecurityDmlPrecedenceTests`, `MergeSecurityTests`, carried SQ-07 | G5-g: generated DML | G6-DML: execute and assert that the other tenant's row multiset is unchanged |

---

## 9. Test Strategy and CI Gate

### 9.1 Test projects

| Project | Content | Runs |
|---|---|---|
| `tests/TrinoSqlEngine.Tests` (existing) | Unit tests, conformance matrix (G2), round-trip and property tests on in-memory ASTs (G3, G5 in-memory part), ported security suites (G7). | Every PR |
| `tests/Autheris.Tests.SqlCompilerGate` (**new**) | Differential corpus (G4), execution suites on every engine (G6), property tests with execution oracles (G5 execution part). Testcontainers (MsSql, PostgreSql, Oracle), Microsoft.Data.Sqlite, DuckDB.NET, Spark/Delta container, optional live Databricks. FsCheck.Xunit 3.4. | `sql-compiler-gate` CI job |
| `tests/Autheris.Tests.Unit`, `Autheris.Tests.Integration` (existing) | Application-level suites (`AstCompilerWebSqlTests`, `AstCompilerHmacSql4Tests`, `GovernedSqlPlanCacheTests`, `SecurityReviewRemediationCoverageTests`, `CorrelatedRowFilterAliasTests`), migrated to `Compile`. | Every PR |
| `tests/Autheris.Tests.Architecture` (existing) | Opacity of `SecurityPredicateExpression`; no `TrustedSqlExpression`; no reference to `RlsListener`/`TokenStreamRewriter`; `AllowExperimentalDialect` only under `tests/`; single caller of `GenerateInlineForInternalPlan`. | Every PR |
| `benchmarks/Autheris.Benchmarks` (existing) | `SqlCompilerBenchmarks` (G8). | `sql-compiler-gate` job (short run) and nightly (full run) |

### 9.2 Corpora

- **Seed corpus** (committed under `tests/Autheris.Tests.SqlCompilerGate/Corpus/`): every SQL string from the existing legacy and AST test files is extracted once by a script into `*.sql` fixtures with metadata (`statement_class`, `expected`), plus `ComplexTrinoBenchmarkQueriesTests`. Target: at least 1,000 read-only and 200 DML statements.
- **Fixture schema:** two or three tables per scenario. Each has a tenant column, rows for tenants A and B, distinct per-tenant **marker values** in every column (for example `A-17`, `B-17`), masked columns with known clear values, NULL-heavy columns and duplicate keys. The same schema DDL is generated per dialect from one C# model.
- **Hostile corpus:** identifiers and values containing `'`, `"`, `` ` ``, `]`, `\`, `--`, `/*`, `$$`, `E'`, Unicode homoglyphs, NUL and 10,000-character values. Used as user values, policy values and mask arguments.

### 9.3 Gate components

| Gate | Definition | Threshold |
|---|---|---|
| **G1 Build and tests** | `dotnet build Autheris.sln`, `dotnet test Autheris.sln` | All green |
| **G2 Conformance matrix** | sqlglot-style `validate_all` fixtures: Trino input -> expected SQL text and expected parameter list per dialect, plus `semantic-note` entries. A reflection test fails if a concrete `Expression` or `SqlStatement` record type has neither a fixture nor an explicit `rejected-for-dialect` entry, per dialect. | At least 300 cases per production dialect (PostgreSQL, SQL Server, SQLite, Oracle, DuckDB, Databricks) and 100 for Snowflake; 100% node coverage |
| **G3 Round-trip identity** | FsCheck generates typed ASTs; `TrinoSourceRenderer` (test-only) prints Trino SQL; `SqlAstBuilder` parses it back; the result must be structurally equal (sqlglot `validate_identity`). | 10,000 cases per PR run (fixed seed set plus one random seed that is logged); 200,000 nightly |
| **G4 Differential corpus vs legacy** (exists only until cutover) | Each corpus statement is compiled by legacy (`RewriteRls`, `LegacyTokenStream`) and AST (`Compile`), executed on SQLite, PostgreSQL, SQL Server and Oracle with the same fixture, and classified by multiset hash of typed, normalized rows: `identical`, `equivalent-result`, `ast-stricter` (AST rejects, legacy accepts), `ast-looser` (AST returns rows, or writes, that legacy did not, or legacy rejects and AST accepts), `ast-error`. DuckDB and Databricks have no legacy support, so they use a **cross-dialect differential** instead: AST-DuckDB and AST-Databricks results must equal AST-PostgreSQL results on the same fixture, except for `semantic-note` cases. | **0 `ast-looser`.** Every `ast-stricter`/`ast-error` entry is listed in `accepted-differences.json` with a justification; that file is owned by security reviewers through CODEOWNERS. At cutover the observed legacy result multisets are frozen into `golden-results/*.json`, so G4 continues as a regression suite without legacy. |
| **G5 Property and fuzz** | FsCheck generators over joins, set operations, CTEs (including recursive), subqueries in every position, window functions, grouping sets, pagination, DML and `MERGE`. Oracles: (a) fail-closed typing (INV-1); (b) TLP partition oracle (SQLancer): `Q` = `Q WHERE p` UNION ALL `Q WHERE NOT p` UNION ALL `Q WHERE p IS NULL`, as multisets; (c) `SecurityCoverageVerifier` plus the RLS visibility oracle: no result row contains a marker of tenant B; (d) no literal, comment or `;` in emitted text; (e) hostile policy values round-trip exactly; (f) limit boundary: `max` passes, `max + 1` throws the typed exception; (g) DML: the other tenant's row multiset is unchanged. Grammar-based fuzzing: random token sequences from `SqlBase.g4` lexer vocabulary must parse-reject or compile, and must never produce an unexpected exception type. | PR: 10,000 generated queries per in-process engine (SQLite, DuckDB), 1,000 per container engine. Nightly: 100,000 per in-process engine, 10,000 per container. Failing seeds are committed to `Corpus/regressions/`. |
| **G6 Real execution** | Testcontainers: SQL Server 2022, PostgreSQL 17, Oracle Free 23ai. In process: SQLite (bundled), DuckDB (DuckDB.NET 1.5.x). Spark 4.0 + Delta 4.0 container (Databricks proxy, §9.4). Suites: result equality (G4 and cross-dialect), RLS row visibility, masked values never in clear, DML check option (`INSERT VALUES`, `INSERT SELECT`, `UPDATE`, `DELETE`, `MERGE`), limit probes for every value marked **(verify)** in §5. | All green |
| **G7 Security regression** | Ported suites from `SecurityRemediationTests`, `SecurityReview20261002SqTests`, `Sql1BracketLexerDifferentialTests`, `DmlLimitAndAliasSql78Tests`, `AstSecurityDmlPrecedenceTests`, `TrinoParserComplianceTests`, `SecurityReviewRemediationCoverageTests`, `CorrelatedRowFilterAliasTests` and `AuditArchitectureHardeningAu01To19Tests`. Every legacy SEC/SQ/SQL/SR15 ID keeps at least one test asserting the **same or a stricter** outcome on the AST path. A traceability test checks the ID list against `SecurityRegressionCatalog.cs`. Stryker.NET mutation testing runs nightly on `Ast/Visitors/AstSecurityVisitor.cs`, `Ast/Visitors/AstSimplificationVisitor.cs`, `Ast/Security/*` and `Ast/Emit/*`. | 100% ID traceability; Stryker mutation score at least 85% (break threshold) |
| **G8 Performance** | BenchmarkDotNet `SqlCompilerBenchmarks` over a 200-query corpus (0.5 KB, 4 KB and 64 KB buckets) with `[MemoryDiagnoser]`; legacy and AST are measured in the **same job**. | AST/legacy at most 1.5x (P95, P99); allocated bytes at most 1.25x; 0 LOH (Gen2/LOH counters) up to 64 KB; absolute P99 under 2 ms up to 4 KB. After cutover, compared with `benchmarks/baselines/sql-compiler-legacy.json` (frozen in the cutover PR); fail on more than 10% allocation regression. Time is compared by ratio on the same runner to avoid noise from shared CI hardware. |
| **G9 Live Databricks** (optional, secret-gated) | Same G6 suites against a Databricks SQL Warehouse in a per-run schema of a dedicated catalog, dropped afterwards. Runs nightly, on demand, and on a PR labeled `sql-cutover`. Skipped (not failed) when the `DATABRICKS_HOST` secret is absent. | Green before Databricks is listed as `Production` in the cutover PR (OQ-2) |
| **G10 Consumer parity** (added in Phase 3, §16.5 M-9) | For every §7.3 consumer, the legacy string producer and the IR renderer are evaluated on the same fixture (SQLite, PostgreSQL, SQL Server and the in-memory evaluators). | 0 `looser` |

### 9.4 Databricks evidence model

1. **Golden SQL (G2):** at least 300 conformance fixtures for Databricks syntax and parameter lists.
2. **Spark/Delta CI proxy (G6):** a pinned-digest container from the official `apache/spark` 4.0 image with `delta-spark` 4.0, ANSI mode on (`spark.sql.ansi.enabled=true`, matching Databricks SQL warehouses), `DeltaSparkSessionExtension` and `DeltaCatalog`.
   - A small Python runner (`tests/Autheris.Tests.SqlCompilerGate/Spark/runner.py`, run with `docker exec` through Testcontainers `ExecAsync`) reads `{sql, params:[{name,value,type}]}` JSON from stdin, runs `spark.sql(sql, args=...)` (Spark named parameter markers, the same `:name` syntax as Databricks) and returns typed JSON rows.
   - Three-part names are tested as `spark_catalog.<schema>.<table>`.
   - DML and `MERGE` run on Delta tables.
3. **Live warehouse (G9):** optional and secret-gated. It also runs the bind-limit probe that replaces the provisional 1,000 budget.
4. **Remaining semantic gap (risk R-7):** OSS Spark plus Delta is not Databricks SQL. It lacks Photon, Unity Catalog name resolution and privileges, runtime-specific functions (for example `TIMESTAMP_NTZ` details), DBR-specific error classes and the Statement Execution API surface (result manifest, byte limits). G6 on the proxy proves syntax and Delta DML semantics; only G9 proves the connector and Unity Catalog behavior.

### 9.5 CI workflow definition

`.github/workflows/ci.yml` gains a job `sql-compiler-gate` that is **required** on `main` through branch protection.

```yaml
sql-compiler-gate:
  needs: build-and-test
  runs-on: ubuntu-latest           # Docker available for Testcontainers
  timeout-minutes: 60
  steps:
    - checkout, setup .NET 10, restore --locked-mode
    - dotnet test tests/TrinoSqlEngine.Tests -c Release --filter "Category=SqlGate"                 # G2, G3, G5 in-memory, G7
    - dotnet test tests/Autheris.Tests.SqlCompilerGate -c Release                                   # G4, G5 exec, G6
      env: { FSCHECK_REPLAY_SEEDS: "Corpus/regressions", SQLGATE_PROFILE: "pr" }
    - dotnet test tests/Autheris.Tests.Architecture -c Release
    - dotnet run -c Release --project benchmarks/Autheris.Benchmarks -- --filter "*SqlCompiler*" --job short --exporters json
    - dotnet run --project tools/SqlGate.BenchCheck -- benchmarks/results benchmarks/baselines       # G8 thresholds
sql-compiler-gate-nightly:
  schedule: "0 2 * * *"
  - same as above with SQLGATE_PROFILE=nightly; Stryker.NET (G7 mutation); G9 if secrets are present
```

A path filter is deliberately **not** used: the gate runs on every PR, because policy producers live outside `src/TrinoSqlEngine`.

**Cutover precondition (superseded by §16.5, Phase 3):** G1-G8 are green on the cutover PR head; the nightly profile (G3/G5 extended and Stryker) is green on the cutover PR's base commit; G9 is green if Databricks is to be listed as Production (OQ-2); there is a Phase 3 sign-off and a Phase 5 approval.

---

## 10. Legacy Removal List (Cutover Commit WP-X1)

| # | Item | Location | Call sites to migrate |
|---|---|---|---|
| L-1 | `RlsListener` (980 lines, `TokenStreamRewriter`, `[Obsolete]`) | `src/TrinoSqlEngine/RlsListener.cs` | `FastSqlEngine.RunLegacyRewrite` (783); `tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs:136,151`; `tests/Autheris.Tests.Unit/Security/CorrelatedRowFilterAliasTests.cs:161,174`; `tests/Autheris.Tests.Unit/Security/SecurityReviewRemediationCoverageTests.cs:65,103` |
| L-2 | `FastSqlEngine.SqlRewriterEngine` property | `src/TrinoSqlEngine/FastSqlEngine.cs:229-232` | Tests that set it (`SecurityRemediationTests`, `AstSecurityDmlPrecedenceTests`, `DmlLimitAndAliasSql78Tests`, `LegacyVsAstDifferentialTests`) |
| L-3 | `RewriteRls` (2 overloads), `RunLegacyRewrite`, `ShadowDualRun` branch, `#pragma warning disable CS0618` | `FastSqlEngine.cs:743-787`; `ISqlEngine.cs:14-26` | `GovernedSqlRewriter.cs:740`; around 120 test call sites (`SecurityRemediationTests` 52, `TrinoParserComplianceTests` 24, `SecurityReview20261002SqTests` 17, others) through the test helper from WP-B1 |
| L-4 | `GenerateGovernedSql` (3 overloads) | `FastSqlEngine.cs:794-830`; `ISqlEngine.cs:28-41` | `SqlFilterCompiler.cs:365`; AST test files (`Ast/*EmissionTests`, `DialectGenerators/*Tests`, `AstSqliteExecutionTests`, integration tests in `MsSql/PostgreSql/OracleIntegrationTests.cs`) |
| L-5 | `FormatTableAlias` (2 overloads), `LastIdentifierPart`, `BuildTsqlLimitClause` (used only by legacy) | `src/TrinoSqlEngine/FastSqlEngine.Rewrite.cs` (whole file) | none outside `RlsListener` (verified) |
| L-6 | `RlsOptions.RewriterEngine`, `RlsOptions.PolicyFiltersAreTargetDialectSql` | `src/TrinoSqlEngine/IRlsPolicyProvider.cs:113-119, 149-155` | `GovernedSqlRewriter.cs:702-703`; `SqlFilterCompiler.cs:356`; tests in `TargetDialectRowFilterTests`, `AstCompilerHmacSql4Tests`, `AstCompilerWebSqlTests` |
| L-7 | String policy interfaces `IRlsPolicyProvider`, `DefaultRlsPolicyProvider`, `IColumnMaskingPolicyProvider`, `DefaultColumnMaskingPolicyProvider` | `IRlsPolicyProvider.cs:6-106` | `GovernedSqlRewriter.cs:655-670`; `SqlFilterCompiler.cs:358`; tests. Replaced by `IPolicyPredicateProvider`/`IColumnMaskProvider`. |
| L-8 | `TrustedSqlExpression` node and its handling | `Ast/Nodes/Expressions.cs:15`; `SqlDialectGeneratorBase.cs:574`; `SqlAstRewriter.cs:61`; `AstSecurityVisitor.cs:44-47, 209-210, 1264` | `AuditArchitectureHardeningAu01To19Tests.cs:471,488` (rewrite to `MaskExpression`) |
| L-9 | `AstSecurityVisitor.BuildDialectMaskExpression` (string) | `AstSecurityVisitor.cs` | `SqlDataSourceExecutor.cs:574,589` -> `IPolicyPredicateRenderer`/mask renderer |
| L-10 | `GatewayOptions.WebSql.SqlRewriterEngine` | `src/Autheris.Domain/Options/GatewayOptions.cs:1428-1435` | `GovernedSqlRewriter.cs:703,714,725`; `AstCompilerHmacSql4Tests.cs:67`; `AstCompilerWebSqlTests.cs:67`; `GovernedSqlPlanCacheTests`; `BoundedPlanCacheTests` |
| L-11 | Engine argument in `ComputePolicyHash` and the `engine:` fingerprint | `CompiledSqlQueryPlanCache.cs`; `GovernedSqlRewriter.cs:714,725` | `GovernedSqlPlanCacheTests`, `BoundedPlanCacheTests` |
| L-12 | `RestoreClientParameters`, the `__param_` placeholder emission and `IsSynthetic` name passthrough | `GovernedSqlExecutionService.cs:417-429`; `SqlDialectGeneratorBase.cs:607-641` | Replaced by `ICompiledSqlBinder` |
| L-13 | `DialectLimitExceededException`, `MaxParameterBudget` per generator | `Ast/Generators/DialectLimitExceededException.cs`; the 7 generator overrides | Replaced by `SqlLimitExceededException` and the capability table |
| L-14 | `LegacyVsAstDifferentialTests` (4 tests) | `tests/TrinoSqlEngine.Tests/Differential/` | Superseded by G4; deleted, with `golden-results/*.json` frozen |
| L-15 | Legacy description in `src/TrinoSqlEngine/README.md` | lines 22-23, 65, 96, 394 | Rewritten in the cutover commit |

**Kept (AP-3):** `SqlTokenSecurityOptions` and every input token guard; `SqlQueryAnalyzer`; `SqlParameterExtractor`; the ANTLR listener generation (the analyzer needs it); `RowFilterAliases`; `SqlFunctionPolicy` and `SqlFunctionAllowlists`; `AnonymizeSqlForAudit`; `UnfilteredDmlException`. Removing a guard requires a per-guard security sign-off (PRD AP-3).

---

## 11. Work Packages (TDD, Ordered, Committable)

Conventions:

- Each work package is one PR (or one commit on the cutover branch for WP-X1). Tests are written first, Red-Green-Refactor.
- "Files" lists the primary write set. Packages in **different streams with disjoint file sets** can run in parallel worktrees.
- A package is done when its acceptance criteria are met, `dotnet build Autheris.sln` is green, the full test suite is green and the gate components that exist so far are green.

### 11.1 Dependency and stream overview

```mermaid
flowchart LR
    subgraph A["Stream A: compiler core (src/TrinoSqlEngine)"]
        A1["A1 Capability table"] --> A2["A2 CompiledSql + binder + invariant checker"]
        A2 --> A3["A3 Literal parameterization"]
        A2 --> A4["A4 Pass reorder + SecurityPredicate + coverage verifier"]
        A4 --> A5["A5 Typed policy IR + parser"]
        A4 --> A6["A6 MaskExpression"]
        A5 --> A7["A7 DML tenant binding + MERGE"]
        A6 --> A7
        A3 --> A8["A8 Compile API + CompileRequest"]
        A7 --> A8
    end
    subgraph B["Stream B: evidence (tests/, tools/)"]
        B1["B1 Gate project + harness"] --> B2["B2 Conformance matrix"]
        B1 --> B3["B3 Differential corpus"]
        B1 --> B4["B4 Property + fuzz"]
        B1 --> B5["B5 Execution suites"]
        B1 --> B6["B6a/b/c Security suite port"]
    end
    subgraph C["Stream C: Databricks"]
        C1["C1 Databricks generator"] --> C2["C2 App mapping"]
        C2 --> C3["C3 REST connector"]
        C1 --> C4["C4 Spark/Delta proxy"]
        C3 --> C5["C5 Live warehouse job"]
    end
    subgraph D["Stream D: policy producers (Application/Domain)"]
        D1["D1 Typed row filters"] --> D3["D3 SqlFilterCompiler typed"]
        D2["D2 Typed masks"]
        D4["D4 Fail-closed fixes"]
    end
    subgraph E["Stream E: perf + CI"]
        E1["E1 Benchmarks + baseline"] --> E2["E2 CI gate job"]
        E2 --> E3["E3 Nightly + Stryker"]
    end
    A2 --> C1
    A2 --> B1
    A5 --> D1
    A6 --> D2
    A8 --> B3
    A8 --> E1
    D1 --> X1
    D2 --> X1
    D3 --> X1
    B3 --> X1
    B5 --> X1
    B6 --> X1
    C4 --> X1
    E2 --> X1
    X1["X1 CUTOVER (single squash commit)"] --> X2["X2 Post-cutover hardening"]
    X1 --> X3["X3 CrossSourcePlanner (after PLAN-15)"]
```

**Phase 3 addition:** Stream F (Oracle runtime, WP-F1..F6) and its dependencies are defined in §16.6.

**Parallel streams after A2 lands:** A (A3..A8), B (B1..B6), C (C1, C4), D (D4 immediately; D1/D2 after A5/A6), E (E1 after A8). The file sets are disjoint except where §11.7 says otherwise.

### 11.2 Stream A: compiler core

**WP-A1 Dialect capability table** (Stream A; files `src/TrinoSqlEngine/Ast/Capabilities/*` (new), `Ast/Generators/*DialectGenerator*.cs` (budget getters only), `src/Autheris.Infrastructure/Persistence/DatabaseParameterBudgetProvider.cs`)

- Tests first:
  - `DialectCapabilitiesTests`: the exact values from §5 per dialect; Snowflake is `Experimental` and ANSI is `Internal`.
  - `UnknownDialect_Throws`.
  - `SqliteRuntimeLimitProbeTests`: `sqlite3_limit` value is at least the table value, and the effective limit is the minimum.
  - `DatabaseParameterBudgetProviderTests`: delegates to the table for mapped dialects; Databricks is 1,000 (provisional).
  - `OracleInList1001_ThrowsSqlLimitExceeded(Kind=InListItems)` and `OracleBind_IsNotInListLimit`.
  - `ProductionDialects_AreLimitGuaranteed`, `BindExpressionTemplates_AreConstantAndContainOnePlaceholder`, `FunctionMap_NoMatch_Rejects` (Trino rewriter-DSL style, §3.6).
  - `LargeInList_IsNeverCompactedToRange` (AP-11).
- Acceptance: G-4 fixed; one source for limits; `SqlLimitExceededException` has `Kind`, `Dialect`, `Requested` and `Maximum`.

**WP-A2 `CompiledSql`, emitter parameter accounting, binder, emitted-text checker** (Stream A; files `Ast/Emit/*` (new), `Ast/Buffer/SqlEmitterContext.cs`, `Ast/Generators/SqlDialectGeneratorBase.cs` (`FormatParameter` and the `Generate` entry), `ISqlDialectGenerator.cs`)

- Tests first:
  - `ParameterAccountingTests`: named, positional, synthetic and generated markers are all counted; mixed queries at `max` pass and at `max + 1` throw (all dialects, closes G-3).
  - `MarkerStyleTests` per dialect, including marker reuse.
  - `CompiledSqlBinderTests` for SqlClient, Npgsql (positional), Sqlite, Oracle (`BindByName`) and DuckDB.
  - `EmittedSqlInvariantCheckerTests`: it rejects `'`, `--`, `/*`, `;`, `$$`, `E'` outside quoted identifiers; it accepts them inside delimited identifiers with correct escaping; it accepts numeric tokens only at registered positions.
- Acceptance: the existing emission tests are green through an adapter (`GenerateSql(string)` temporarily wraps `Generate` and inlines nothing new); there is no behavior change for current consumers.

**WP-A3 Literal parameterization** (Stream A; files `SqlDialectGeneratorBase.cs` (literal and typed-literal formatting), dialect generators' literal overrides)

- Tests first:
  - `LiteralParameterizationTests`: string, numeric, date and interval values become parameters with the right `SqlParameterType`.
  - `Dedup_SameLiteral_SameMarker`.
  - `GroupByExpression_WithLiteral_ExecutesOnPostgres` (SQLite in-process; PostgreSQL in B5).
  - `AllowListedInlinePositions` (LIMIT, ordinals, frame offsets, cast params, unit keywords).
  - `NoLiteralTokensInOutput` property test (G5-d, in-memory).
- Acceptance: INV-4 holds for all generators. The existing emission tests are updated to the expected-parameter format (conformance style).
- Conflict note: touches the same generator files as A4 and C1. Run sequentially within Stream A, and land it before C1 starts generator-specific literal code.

**WP-A4 Pass reordering, `SecurityPredicateExpression`, `SecurityCoverageVerifier`** (Stream A; files `Ast/Nodes/Expressions.cs` (new node), `Ast/Visitors/SqlAstRewriter.cs`, `AstSecurityVisitor.cs` (wrap injected predicates), `Ast/Security/SecurityCoverageVerifier.cs` (new), `FastSqlEngine.cs` (`GenerateGovernedSql` ordering))

- Tests first:
  - `SimplifierRunsBeforeSecurity_InjectedDenyAllSurvives`.
  - `ContradictionFolding_DoesNotTouchInjectedPredicate`.
  - `CoverageVerifier_Throws_WhenScopeMissingPredicate` for each `SecurityScope` (a hand-built AST without injection).
  - Architecture test `NoRewriterDescendsIntoSecurityPredicate`.
  - G5-c property test (in-memory).
- Acceptance: INV-3 by construction plus a runtime post-condition; G-6 closed.

**WP-A5 Typed policy IR and `IPolicyExpressionParser`** (Stream A; files `src/TrinoSqlEngine/Governance/*` (new), `AstSecurityVisitor.cs` (consume `IPolicyPredicateProvider` alongside the legacy string provider))

- Tests first:
  - `PolicyExpressionParserTests`: canonical Trino predicates parse; unknown column -> `PolicyParseException`; disallowed function -> exception; `:name` -> `PolicyParameterExpression`; comment, semicolon or subquery to a non-catalog table -> reject.
  - `PolicyParseCache_HitsOnSameTextAndCatalogVersion`, `Cache_InvalidatesOnCatalogVersion`.
  - `PolicyPredicate_And_ConflictingParameter_Throws`.
  - `HostilePolicyValues_AreBound` (INV-5, all dialects).
- Acceptance: the AST path accepts typed policies. The string provider still works for legacy (it is additive; deletion comes at X1).

**WP-A6 `MaskExpression`** (Stream A; files `Ast/Nodes/Expressions.cs`, `Ast/Generators/*` (mask emission), `AstSecurityVisitor.cs` (projection masking))

- Tests first:
  - `MaskEmissionTests` per dialect and `MaskKind`, including HMAC per dialect (the SQLite UDF `gateway_hmac_sha256`, pgcrypto `hmac`, SQL Server and Oracle as built today).
  - `MaskArguments_AreBound`.
  - `MaskedColumn_NeverInClear` (SQLite execution).
- Acceptance: masks no longer require `TrustedSqlExpression` on the AST path.

**WP-A7 DML tenant binding and `MERGE`** (Stream A; files `Ast/Nodes/SqlStatement.cs` (`MergeStatement`), `Ast/Builder/SqlAstBuilder.cs` (`#merge`), `AstSecurityVisitor.cs` (`VisitMergeStatement`, tenant literal -> parameter), generators (`GenerateMerge`))

- Tests first:
  - `MergeBuilderTests`.
  - `MergeSecurityTests`: target RLS in `ON`; source RLS; tenant column assignment rejected; masked column assignment rejected; insert-clause tenant literal mismatch rejected; trivially true `ON` rejected; consent-filtered table with insert clause rejected.
  - `MergeEmission` per dialect: SQL Server, PostgreSQL 15+, Oracle, DuckDB 1.4+ **(verify)** and Databricks support `MERGE`. SQLite does not support `MERGE` and must reject it as `SupportsMerge = false`.
  - `InsertTenantLiteral_ReplacedByBoundTenantParameter`.
  - G5-g (in-memory part).
- Acceptance: INV-10 for all DML classes; capability-gated `MERGE`.

**WP-A8 `Compile` API and `CompileRequest`** (Stream A; files `ISqlEngine.cs`, `FastSqlEngine.cs`, `GovernedSqlCompiler.cs` (new), `IRlsPolicyProvider.cs` (`TargetDialect` default removed for the new request only))

- Tests first:
  - `Compile_RequiresTargetDialect` (compile-time `required`, plus a runtime guard for `default`).
  - `Compile_RejectsExperimentalDialect_UnlessAllowed`.
  - `Compile_RejectsAnsi`.
  - `Compile_SpanAttributes`.
  - Architecture test `AllowExperimentalDialect_OnlyInTests`.
- Acceptance: `Compile` is available next to the legacy API. F-2 is fixed for the new API.

### 11.3 Stream B: evidence

**WP-B1 Gate project and harness** (Stream B; files `tests/Autheris.Tests.SqlCompilerGate/**` (new), `Autheris.sln`)

- Tests first: harness self-tests: `FixtureSchema_IdenticalAcrossDialects` and `MultisetHash_StableUnderOrdering`.
- Acceptance:
  - A container fixture per engine is available (with the podman host override, see repository notes).
  - The `SqlRewriteHarness` helper `Rewrite(EngineKind, sql, policy)` runs legacy or AST while legacy exists. After X1 it supports AST only.

**WP-B2 Conformance matrix** (Stream B; files `tests/TrinoSqlEngine.Tests/Conformance/**` (new))

- Tests first: the fixtures themselves (Red until generators match), plus `NodeCoverageReflectionTest`.
- Acceptance: G2 thresholds; Databricks fixtures are added by C1.

**WP-B3 Differential corpus** (Stream B; files `tests/Autheris.Tests.SqlCompilerGate/Differential/**`, `Corpus/**`, `tools/SqlCorpusExtractor/` (new))

- Tests first: `Classifier_DetectsLooser` (a mutant AST visitor in test code that drops CTE RLS must be classified `ast-looser`); `Classifier_AcceptsListedStricter`.
- Acceptance: G4 is green with 0 `ast-looser`; `accepted-differences.json` is reviewed by Phase 3.

**WP-B4 Property-based and grammar fuzz** (Stream B; files `tests/TrinoSqlEngine.Tests/Properties/**`, `tests/Autheris.Tests.SqlCompilerGate/Properties/**`)

- Tests first: the generators with shrinking, and oracles (a)-(g) from G5.
- Acceptance: G3 and G5 thresholds; seed replay from `Corpus/regressions/`.

**WP-B5 Execution suites** (Stream B; files `tests/Autheris.Tests.SqlCompilerGate/Execution/**`)

- Tests first: per engine, RLS visibility, mask, DML check option, `MERGE`, limit probes, hostile corpus.
- Acceptance: G6 is green for SQL Server, PostgreSQL, Oracle, SQLite and DuckDB. Spark is added by C4.

**WP-B6a/b/c Security suite port** (Stream B; **parallel by file**)

- B6a: `tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs`.
- B6b: `SecurityReview20261002SqTests.cs`, `Sql1BracketLexerDifferentialTests.cs`, `DmlLimitAndAliasSql78Tests.cs`, `AstSecurityDmlPrecedenceTests.cs`, `TrinoParserComplianceTests.cs`.
- B6c: `tests/Autheris.Tests.Unit/Security/{SecurityReviewRemediationCoverageTests,CorrelatedRowFilterAliasTests,AuditArchitectureHardeningAu01To19Tests}.cs`.
- Tests first: `SecurityRegressionCatalog` traceability test (Red until every ID is mapped). Each test becomes `[Theory]` over `EngineKind` through the harness, asserting the same or a stricter outcome. Text assertions on legacy formatting become semantic assertions (exception type, AST shape or SQLite execution).
- Acceptance: G7 traceability is 100%.

### 11.4 Stream C: Databricks

**WP-C1 Databricks dialect and generator** (Stream C; files `IRlsPolicyProvider.cs` (`TargetSqlDialect.Databricks` enum value appended, never renumbered), `Ast/Generators/DatabricksDialectGenerator.cs` (new), `SqlDialectGeneratorFactory.cs`, `Ast/Capabilities/*` (Databricks entry), `SqlFunctionAllowlists.cs` (Databricks set), `tests/TrinoSqlEngine.Tests/Conformance/Databricks/**`)

- Tests first:
  - `DatabricksIdentifierQuotingTests` (backtick doubling, three-part names, more than 255 characters rejected, four parts rejected).
  - `DatabricksParameterMarkerTests` (named only, reuse, never mixed).
  - `DatabricksStringLiteral_NeverEmitted`.
  - `DatabricksPaginationTests` (`LIMIT`/`OFFSET`; `WITH TIES` rejected).
  - `DatabricksCastTypeTests`.
  - `DatabricksDateFunctionTests`.
  - `DatabricksFunctionMappingTests`.
  - `DatabricksRejectsUnnest`.
  - `DatabricksMergeEmissionTests`.
  - At least 300 conformance fixtures.
- Acceptance: G2 for Databricks.
- Conflict note: touches the factory, capability and allow-list files shared with Stream A, so it starts after A3 and coordinates with A7 for `GenerateMerge`.

**WP-C2 Application dialect mapping** (Stream C; files `src/Autheris.Application/Sql/SqlDialectMapper.cs`, `GovernedSqlRewriter.cs` (the `IsExecutable` message only), `Autheris.Application/Services/ChunkedQueryExecutor.cs`, `DatabaseParameterBudgetProvider.cs`)

- Tests first: `SqlDialectMapper_Databricks_RoundTrip`, `IsExecutable_IncludesDatabricks`, and a WebSQL rejection message test.
- Acceptance: Databricks data sources reach `Compile`.
- Conflict note: `GovernedSqlRewriter.cs` is also changed by X1. C2 changes only the single message line, and X1 rebases on it.

**WP-C3 Databricks REST connector** (Stream C; files `src/Autheris.Infrastructure/Databricks/**` (new), `SqlConnectionFactory.cs`, `GatewayOptions.cs` (`DatabricksDataSourceOptions` added), `GatewayStartupValidator.cs` (Databricks section), DI registration)

- Tests first:
  - `DatabricksStatementExecutionClientTests` with a `HttpMessageHandler` stub: request shape, named typed parameters, culture-invariant value serialization, null parameters, `PENDING` -> poll -> `SUCCEEDED`, `FAILED` -> typed error, byte-limit truncation -> typed error, cancellation -> `cancel` call.
  - `DatabricksDataReaderTests` (manifest type mapping).
  - `SsrfHandler_BlocksNonAllowListedHost`.
  - `StartupValidator_RejectsPlaintextSecretOutsideDevelopment`.
  - `NoMultiStatementTransactions`.
- Acceptance: WebSQL against a stubbed Databricks works end to end in unit tests.

**WP-C4 Spark/Delta CI proxy** (Stream C; files `tests/Autheris.Tests.SqlCompilerGate/Spark/**` (container builder, `runner.py`, fixture DDL as Delta tables))

- Tests first: `SparkProxy_ExecutesNamedParameters`, `SparkProxy_MergeOnDelta`, and the G6 suites parameterized with the Databricks dialect.
- Acceptance: G6 is green on the proxy; the §5/§6 **(verify)** items for `OFFSET`, `LATERAL` and `TIMESTAMP_NTZ` are resolved or turned into rejections.

**WP-C5 Live warehouse job** (Stream C/E; files `.github/workflows/ci.yml` (nightly job section), `tests/Autheris.Tests.SqlCompilerGate/Live/**`)

- Tests first: `LiveDatabricks_SkipsWithoutSecrets`, the G6 suites against the warehouse, and `BindLimitProbe` (binary search; writes the measured value into the run summary).
- Acceptance: G9 is runnable. The capability value is updated from the measured value in a follow-up commit.

### 11.5 Stream D: policy producers

**WP-D1 Typed row filters and tenant predicate** (Stream D; files `src/Autheris.Domain/Interfaces/TableAccessDecision.cs`, `Autheris.Application/Services/RowFilterSqlBuilder.cs`, `Autheris.Application/Policy/TableAccessPolicy.cs`, `Autheris.Application/Sql/Policy/PolicyPredicateRenderer.cs` (new))

- Tests first:
  - `RowFilterSqlBuilder_BuildsTypedPredicate_ForEveryOperator`.
  - `RenderedString_EqualsLegacyString_Semantically` (execution on SQLite).
  - `Restrict_TypedAnd_ConflictDenies`.
  - `AccessProfilePredicate_ParsedOnce_FailClosedOnInvalid`.
  - `TenantPredicate_IsBoundParameter`.
- Acceptance: one typed source; the string consumers in §7.3 are unchanged behaviorally (their existing suites stay green).

**WP-D2 Typed masks** (Stream D; files `Autheris.Application/Sql/Masking/SqlDataMaskingProvider.cs`, `SqlDataSourceExecutor.cs` (mask call sites))

- Tests first: `MaskSpec_ForEveryRuleType`, `HmacKey_IsParameter`, `RenderedMask_MatchesPreviousOutput` (execution).
- Acceptance: masks are typed; the string facade renders from `MaskSpec`.

**WP-D3 `SqlFilterCompiler` typed output** (Stream D; files `Autheris.Application/VirtualFilters/SqlFilterCompiler.cs`, `MandatoryRowFilterResolver` call sites)

- Tests first: `Compile_ReturnsTypedExists_CorrelatedToAutherisTarget`, `NoStringReplaceOnSql` (architecture), and the existing virtual-filter suites.
- Acceptance: F-4 fixed.
- Coordination: the virtual-filter files are also edited by PLAN-FEDERATED-VF-DUCKDB-15. Rebase after that track merges, or agree on the merge order (§11.7).

**WP-D4 Fail-closed fixes** (Stream D; **can start immediately**; files `SqlDataSourceExecutor.cs:568-588`, `GatewayStartupValidator.cs` (obsolete-key detection prepared but inactive until X1))

- Tests first: `MaskDialectMapping_UnmappedDialect_Throws` (F-1).
- Acceptance: no `_ => PostgreSql` fallback remains in the src tree (grep-based architecture test).

### 11.6 Stream E: performance and CI

**WP-E1 Benchmarks and baseline** (Stream E; files `benchmarks/Autheris.Benchmarks/SqlCompilerBenchmarks.cs` (new), `benchmarks/baselines/` (new), `tools/SqlGate.BenchCheck/` (new))

- Tests first: `BenchCheck` unit tests for the threshold evaluation.
- Acceptance: the first run's legacy and AST numbers are recorded in §15 of this plan; G8 thresholds are evaluated.

**WP-E2 CI gate job** (Stream E; files `.github/workflows/ci.yml`)

- Acceptance: the `sql-compiler-gate` job runs G1-G8 on PRs; branch protection is requested from the repository owner.

**WP-E3 Nightly profile and Stryker.NET** (Stream E; files `.github/workflows/ci.yml` (nightly), `stryker-config.json` (new))

- Acceptance: nightly G3/G5 extended and Stryker at least 85% on the listed files.

### 11.7 Cutover and follow-ups

**WP-X1 CUTOVER** (single squash-merged commit; prepared on branch `feat/ast-cutover` as reviewable commits)

1. `GovernedSqlRewriter` switches to typed policies and `Compile`; the plan cache stores `CompiledSql`; the executor and execution service bind through `ICompiledSqlBinder`.
2. Delete L-1..L-15.
3. Activate the startup check for the obsolete `WebSql:SqlRewriterEngine` key.
4. The harness drops `EngineKind.Legacy`; freeze the `golden-results/*.json` and `benchmarks/baselines/sql-compiler-legacy.json` produced on the pre-cutover head.
5. Rewrite `src/TrinoSqlEngine/README.md`.

- Tests first:
  - Architecture tests `NoLegacyRewriterTypes` and `NoTrustedSqlExpression`.
  - `StartupFails_WhenSqlRewriterEngineConfigured`.
  - `AstCompilerWebSqlTests` and `AstCompilerHmacSql4Tests` without the engine parameter.
  - `GovernedSqlPlanCacheTests` with `CompiledSql`.
- Acceptance: §9.5 cutover precondition. The commit message states the revert procedure (§12.2).

**WP-X2 Post-cutover hardening** (after X1; small PRs)

- Remove the temporary `GenerateSql(string)` adapter from A2.
- Raise the Databricks bind budget from the C5 measurement.
- Hand over to Phase 6 documentation.

**WP-X3 `CrossSourcePlanner` parameterization** (after PLAN-15 merges)

- Switch to `CompiledSql` with DuckDB `$n` binding.
- Delete `GenerateInlineForInternalPlan`.
- Tests: `FederatedPlan_BindsParameters` and the existing federated suites.

**File-conflict matrix (shared files):**

| File | Packages | Rule |
|---|---|---|
| `Ast/Generators/SqlDialectGeneratorBase.cs` | A2, A3, A4, A6, A7 | Sequential within Stream A |
| `AstSecurityVisitor.cs` | A4, A5, A6, A7 | Sequential within Stream A |
| `SqlDialectGeneratorFactory.cs`, `Ast/Capabilities/*`, `SqlFunctionAllowlists.cs` | A1, C1 | C1 after A3 |
| `GovernedSqlRewriter.cs` | C2 (one line), D1 (Stage 3 only), X1 | D1 and C2 before X1; X1 rebases |
| `SqlDataSourceExecutor.cs` | D2, D4 | D4 first (small), then D2 |
| `GatewayStartupValidator.cs`, `GatewayOptions.cs` | C3, D4, X1 | Different sections; X1 last |
| `SqlFilterCompiler.cs`, `CrossSourcePlanner.cs` | D3, X3, and the PLAN-15 track | Wait for PLAN-15 merge or agree on order |
| `.github/workflows/ci.yml` | E2, E3, C5 | Sequential within Stream E |

---

## 12. Risks and Rollback

### 12.1 Risk register

| ID | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R-1 | The AST path is looser than legacy in an untested construct, causing a cross-tenant leak (big-bang exposure, SD-1/SD-2) | Medium | Critical | G4 zero `ast-looser`; G5-c visibility oracle on generated queries; `SecurityCoverageVerifier` in production; G7 traceability; Stryker; Phase 3 sign-off. Accepted residual risk per SD-2. |
| R-2 | Bind-everything changes backend behavior: plan reuse, implicit typing (for example `$1` typed `text` vs `integer` in PostgreSQL), and parameter-sniffing plans in SQL Server | High | Medium | Typed `SqlParameterType` from AST literal types; per-dialect `BindExpressionTemplates` (Trino `WriteFunction.getBindExpression` pattern, §3.6) for exact remote types; conformance fixtures; G4 equality on real engines; G8 benchmarks. |
| R-3 | Policy authoring breakage: existing admin-authored row-filter predicates written in target-dialect syntax fail to parse, so their tables are denied | Medium | High (availability) | Fail closed by design. A startup/reload validator lists every unparsable predicate; a migration note goes to Phase 6; a one-off validation tool (`tools/PolicyPredicateLint`) runs before deploy. Needs stakeholder confirmation (OQ-3). |
| R-4 | Simplifier semantics change, now on the user tree (three-valued logic) | Low | Medium | Runs before injection, so the security impact is removed. G5-b (TLP) catches result changes. |
| R-5 | Result-equivalence false positives (ordering, float formatting, collation) | High | Low | Multiset hashing, typed normalization, and `semantic-note` classification. |
| R-6 | CI cost and flakiness (Oracle and Spark containers, 60-minute job) | Medium | Medium | Container reuse per test collection; PR versus nightly profiles; seeds replayed deterministically; podman/Docker host override documented. |
| R-7 | **Semantic gap between the Spark/Delta proxy and Databricks SQL** (Photon, Unity Catalog resolution, DBR-specific functions and types, error classes, Statement Execution API behavior). Databricks has no Testcontainer. | High | High | G9 live job; Databricks held from the Production list until G9 is green (OQ-2); provisional fail-closed bind budget; anything not verified is rejected rather than emitted. |
| R-8 | The Databricks bind-parameter limit is undocumented | High | Medium | Provisional 1,000 (fail closed) until the live probe measures it. |
| R-9 | Merge conflicts with PLAN-FEDERATED-VF-DUCKDB-15 (`CrossSourcePlanner`, virtual filters) | High | Low | Transitional internal API; X3 after PLAN-15; D3 ordered with PLAN-15. |
| R-10 | Performance budget missed because of the coverage verifier, invariant checker and parameter dedup | Medium | Medium | G8 gate before cutover; the verifier and checker are linear scans over pooled buffers; the plan cache absorbs repeats. |
| R-11 | Domain/Application layering: `TableAccessDecision` (Domain) cannot reference `TrinoSqlEngine` AST types | Medium | Low | `IRowPolicy` abstraction in Domain; Phase 3 and the architecture tests confirm it. |
| R-12 | `MERGE` + `INSERT` side channel: a unique-key violation reveals that another tenant's key exists | Medium | Medium | The same exposure exists for plain `INSERT` today. Phase 3 decides: accept and document, or reject `MERGE ... INSERT` on tables with cross-tenant unique keys. |

### 12.2 Rollback

- **Phase 3 amendment (§16.2 SEC-ADG-02):** the revert set is `{X1, D3, D2, D1}`, rehearsed as one combined revert in the cutover PR.
- **Mechanism:** `git revert <cutover-sha>` of the single squash commit WP-X1, followed by a normal release. This restores legacy, the engine switch and the string policy path in one step. There is **no runtime flag** (SD-1).
- **Revertability:** every package before X1 is additive and keeps legacy working. Packages after X1 (X2, X3) must not depend on deleted legacy code in a way that blocks the revert. Rule: no package after X1 may modify a file that X1 deleted. CI enforces this with an architecture test listing the deleted paths.
- **Rehearsal:** in the cutover PR, CI checks out `HEAD~1` and runs G1. The PR description contains the exact revert command and the expected build.
- **Data:** the cutover changes no schema or configuration format, apart from the removed key. Reverting needs no data migration. If an operator set `WebSql:SqlRewriterEngine` after the cutover, they must remove it before deploying a revert, because the old default is legacy anyway.

---

## 13. ADR-017 Amendment (Exact Text for Phase 6)

Phase 6 appends the following section to `docs/adr/ADR-017-distributed-state-ast-generator-and-rbac.md`. The existing German body is left as is; whether to translate it is a separate documentation decision.

```markdown
## Amendment 2026-10-10: §2 AST Target Dialect Generator (supersedes the §2 "Garantie" paragraph)

### Decision
1. The governed SQL path has exactly one implementation: the AST compiler
   (parse -> typed AST -> simplify user tree -> inject typed security predicates -> verify coverage ->
   validate dialect capabilities -> emit -> bind). The token-stream rewriter (`RlsListener`), the
   `WebSql:SqlRewriterEngine` setting and the `ShadowDualRun` mode were removed. There is no runtime engine
   switch; rollback is a code revert of the cutover commit.
2. Every value reaches the database as a bind parameter (user literals, tenant, policy and mask values).
   Only validated structural integers (row limits, ordinals, frame offsets, type parameters) and keywords are
   emitted inline. Row filters and column masks are typed AST nodes; raw trusted SQL fragments are not used.
3. Dialect limits come from a capability table (bind-parameter count, IN-list size, identifier length).
   Statements over a limit are rejected with a typed error; they are never split or truncated.
4. Production dialects: PostgreSQL, SQL Server, SQLite, Oracle, DuckDB, Databricks. Snowflake is experimental
   (generator only, not executable). ANSI is internal.
5. INSERT, UPDATE, DELETE and MERGE are compiled on the same path. WITH CHECK OPTION semantics, the
   unfiltered-DML guard and masked-column write protection are enforced by the compiler for every dialect,
   independent of database features.
6. Input token guards (`SqlTokenSecurityOptions`) remain as defense in depth.

### Assurance wording
The former statement that token differentials, comment injection and dialect bypasses are "constructively
impossible" is withdrawn. The security properties are named invariants (INV-1..INV-10 in the implementation
plan) that are enforced in code and continuously tested by the CI gate `sql-compiler-gate` (conformance matrix,
differential corpus, property-based and grammar fuzzing, execution on real engines, mutation testing).

### Consequences
- Positive: one rewriter for WebSQL, SQL endpoints, MCP tools and virtual filters; no policy drift between
  engines; structural removal of raw-fragment splicing.
- Negative: big-bang cutover risk, mitigated by the pre-merge evidence gate; admin-authored row-filter
  predicates must be written in canonical (Trino) syntax; Databricks production evidence depends on a
  Spark/Delta proxy plus an optional live warehouse job.

References: docs/plans/2026-10-10-req-ast-dialect-generator.md, docs/plans/2026-10-10-plan-ast-dialect-generator.md
```

Phase 6 also corrects the ADR-017 status line for §2 once WP-X1 has merged.

---

## 14. Hand-Off Notes for Phase 3 (`csharp-security-expert`)

Please review and extend this plan with security test criteria. Specific decisions that need your sign-off or a counter-proposal:

1. **INV-3 design (§3.2):** simplifier before injection, plus the opaque `SecurityPredicateExpression` and the production `SecurityCoverageVerifier`. Is the scope list (`SecurityScope`) complete? Look in particular at lateral joins, `VALUES` sources, recursive CTE bodies and `MERGE` source/target.
2. **Literal allow-list (§3.4):** confirm that the inline structural integer positions are acceptable under "every value is bound", and that `EmittedSqlInvariantChecker` should run in production (proposed) rather than only in tests. Review the per-dialect `BindExpressionTemplates` (§3.6, Trino `getBindExpression` pattern): they are the only fixed text that wraps a placeholder, for example Oracle `TO_DATE(:p1, 'SYYYY-MM-DD')`, so the checker must allow-list their format literals.
3. **Policy IR boundary (§3.5, §4.4):** canonical Trino syntax for admin-authored predicates; the parser restrictions (subqueries only to catalog tables? correlated `EXISTS` allowed?); cache key and invalidation on catalog change; fail-closed deny plus operator reporting. Also the `IRowPolicy` layering in Domain (R-11).
4. **DML/MERGE (§6.2):** replacing the verified tenant literal with the bound tenant parameter; `MERGE` target RLS in `ON`; the unique-key existence side channel (R-12); whether `MERGE ... INSERT` on consent-filtered tables must be rejected (proposed: yes).
5. **Databricks:**
   - SSRF allow-list for the warehouse host;
   - OAuth M2M secret handling through Key Vault;
   - no string literals ever emitted (backslash escape semantics);
   - the provisional bind budget;
   - whether Databricks may be Production before a green G9 (OQ-2).
6. **Gate thresholds (§9.3):** 0 `ast-looser`; who signs `accepted-differences.json`; Stryker at least 85%; property test volumes. Please add security mutants that the gate must kill. Proposed minimum: drop CTE RLS, drop set-operation branch RLS, skip scalar subquery, unmask the wildcard expansion, allow the tenant column in `UPDATE SET`, accept a trivially true `MERGE ON`, emit a string literal unbound, bypass the IN-list limit.
7. **Token guards (AP-3):** confirm that all of `SqlTokenSecurityOptions` stays. Decide the Databricks-specific guard profile; proposed: `RejectBackslashInStrings = true` for Databricks input as well, even though no literal is emitted.
8. **Audit (INV-8'):** the compiler version, dialect and bind count in the audit record are enough; parameter values must never be logged.
9. **Removed key (§7.1):** fail-fast on the obsolete `WebSql:SqlRewriterEngine` key versus warn-and-ignore. Proposed: fail fast.
10. **Rollback (§12.2):** the git-revert-only model with no runtime flag (stakeholder decision SD-1). Please verify the revert rehearsal is sufficient from a security-operations perspective.

---

## 15. Open Questions and Conflicts Needing Stakeholder Input

| ID | Question | Default assumed in this plan |
|---|---|---|
| OQ-1 | **Resolved by SD-8 (Phase 3): the Oracle runtime is in scope, Stream F (§16.6).** Original question: **Oracle runtime.** The gateway has no Oracle driver (`SqlDialectMapper.IsExecutable` excludes Oracle, F-10). Does "production dialect" for Oracle include adding an ODP.NET runtime path to WebSQL in this track? | No. Oracle is production-grade at compiler level with container evidence; runtime wiring is a follow-up. |
| OQ-2 | **Databricks production status versus evidence.** SD-7 says "fully supported production", but real Databricks evidence needs a live warehouse (no Testcontainer). Who provides the workspace, warehouse and service principal for G9? Is a green G9 mandatory before Databricks is listed as Production? | Mandatory. Without G9, Databricks ships compiler- and connector-complete, but it is held from the Production list (configuration rejects it) until the first green G9 run. **This partly conflicts with SD-7 and needs a decision.** |
| OQ-3 | **Policy predicate syntax migration.** Admin-authored row-filter predicates must be canonical Trino syntax; existing target-dialect predicates are denied (fail closed) until rewritten. Acceptable? | Yes, with a lint tool and a migration note. |
| OQ-4 | **Non-AST string consumers (§7.3).** GraphQL tree compiler, lakehouse, streaming, procedures and data query service keep consuming `CombinedRowFilterSql`, rendered from typed IR. Moving them onto typed IR is a follow-up track. Agreed? | Yes, follow-up track. |
| OQ-5 | **Source semantics (§3.3).** Backend semantics with Trino syntax (no null-ordering or integer-division shims), matching today's legacy behavior. Agreed? | Yes. |
| OQ-6 | **Literal allow-list (§3.4).** Structural integers and keywords inline; all values bound. Agreed interpretation of "nothing reaches the database as raw text"? | Yes. |
| OQ-7 | **Branch protection.** Making `sql-compiler-gate` a required check needs repository-admin action. | The repository owner enables it in WP-E2. |

**Benchmark results (filled by WP-E1):** legacy versus AST P95/P99 and allocated bytes per bucket: _pending WP-E1_.

---

## 16. Phase 3 Security Review (`csharp-security-expert`)

**Reviewer:** Security Expert (`csharp-security-expert`), Phase 3 of the 6-phase lifecycle
**Reviewed baseline:** this plan at commit `818ab14`, the PRD, the STRIDE threat model (`docs/threat-model/threat-model.md`), ADR-017, and the code at `818ab14`: `src/TrinoSqlEngine/**` (`FastSqlEngine.cs` token guards and limits, `RlsListener.cs`, `Ast/**`), `GovernedSqlRewriter.cs`, `CompiledSqlQueryPlanCache.cs`, `SqlFilterCompiler.cs`, `SqlDataSourceExecutor.cs`, `AdvancedRlsFilterGenerator.cs`, `SqlDataMaskingProvider.cs`, `DbSessionContextInitializer.cs`, `SqlConnectionFactory.cs`, `TenantId.cs`, and the existing SEC/SQ/SQL/RR/SR15 test suites (77 distinct IDs referenced under `tests/`).
**Verdict:** **Approved with mandatory changes.** The architecture (simplifier before injection, opaque security nodes, production coverage verifier, bind-everything, typed policy IR) is sound and stricter than legacy. The plan as written in §1-§15 is **not** yet sufficient for a big-bang deletion of legacy. It becomes sufficient once the findings below rated Critical and High are implemented, and the gate additions in §16.5 are in place. Everything in this section is binding for Phase 4. Where this section conflicts with §1-§15, this section wins.

### 16.1 Stakeholder decisions applied in this review

| ID | Decision (binding) | Effect on the plan |
|---|---|---|
| SD-1..SD-7 | As in §1. | Unchanged. |
| SD-8 (new) | **Oracle is a supported production dialect with a runtime driver.** | Resolves OQ-1 against the Phase 2 default. Adds Stream F (§16.6): ODP.NET driver in Infrastructure, connection factory, session initialization, binder, `SqlDataSourceExecutor` marker fix, and G6 execution on the real gateway path. Oracle is listed as `Production` only after WP-F5 is green. |
| OQ-2 | Databricks is required in production. Who provides the live workspace is still open. | The plan's rule stays: Databricks is **held** from the production list (configuration rejects it) until the first green G9 run. |
| OQ-3 | Confirmed: admin row filters must be written in Trino syntax. Others are denied until rewritten. | Fail closed. `tools/PolicyPredicateLint` is mandatory before the X1 deploy (SEC-ADG-26). |
| OQ-4 | Confirmed: §7.3 consumers migrate later and keep using text rendered from the typed IR. | The residual risk is assessed in §16.7. Gate G10 is added (SEC-ADG-02). |
| OQ-5 | Confirmed: backend null-ordering and division semantics are not rewritten. | Security-relevant semantic differences (collation, NLS, empty string, LIKE escape) are **not** covered by OQ-5. They are handled by SEC-ADG-04, -17 and -18. |
| OQ-6 | Confirmed: every value is a bind parameter; structural integers and keywords are inline. | The §3.4 allow-list is approved. `EmittedSqlInvariantChecker` runs in production (§16.8, item 2). |

### 16.2 Findings

Severity follows the repository convention. **Critical** means a cross-tenant read or write, or an RLS bypass, that is reachable by an authenticated caller. **High** means a cross-tenant information flow under realistic configuration, a process-level DoS, or a gate gap that would let a Critical defect reach `main`. **Medium** means defense-in-depth gaps, side channels with low bandwidth, or fail-closed availability regressions. **Low** means hardening.

| ID | Sev. | Component | Finding | Required mitigation | Owner WP |
|---|---|---|---|---|---|
| SEC-ADG-01 | **Critical** | `CompiledSqlQueryPlanCache`, `GovernedSqlRewriter` Stage 5, `CompileRequest` | After bind-everything, `CompiledSql.Parameters` carries tenant, policy and mask values (including the HKDF-derived per-tenant HMAC key). §7.2 caches `CompiledSql` and relies on the policy hash to "cover" those values. Today the policy hash is a hand-picked list of inputs (`ComputePolicyHash`, XxHash3 64-bit), the policy values live in `internalParameters` and are rebuilt on every request. Three consequences: (a) a principal of the same tenant whose consent filter has the same shape but different values (for example `region = :p` with EU versus US) would be served another principal's bound values; (b) `StatementPermissions` and other `CompileRequest` fields that are not in the hand-picked list let a cached compile skip a check that happens inside `Compile`; (c) a 64-bit non-cryptographic hash over admin- or consent-influenced input is not a safe identity for a security decision (the cache compares raw SQL text on hit, but it does not compare the policy). | 1. The cache stores a **value-free `CompiledSqlTemplate`**: SQL text plus slot descriptors (`Marker`, `Ordinal`, `Type`, `Origin`, policy parameter name). `Tenant`, `Policy` and `Mask` values are **rebound from the current request's `ParameterSource` on every hit**. Only `QueryLiteral` values (fully determined by the raw SQL, which is compared on hit) may be stored. 2. The cache key is derived from a **canonical serialization of the entire `CompileRequest`** (all properties, including `StatementPermissions`, token guards, function allow-lists, `EnforcedMaxRows`, `TranslateTrinoDateFunctions`, `SubqueryStrategy`), plus the policy **shape** fingerprint (predicate AST shape, parameter names and types, mask kinds), `CompilerVersion`, the capability-table version and the data source. 3. The policy fingerprint is SHA-256. The full fingerprint string is stored in the entry and compared with ordinal equality on hit. A 64-bit hash may remain the bucket key only. 4. The overloads of `TryGetCompiledSql` that pass an empty `rawSql` (and so skip the raw-text compare) are deleted on the governed path. 5. Only outputs that passed the coverage verifier and the emitted-text checker are inserted. A cancelled or failed compile never inserts. | WP-A8 (key), WP-X1 (cache value type), WP-B5 (tests) |
| SEC-ADG-02 | **Critical** | WP-D1/D2 renderers, §7.3 consumers, rollback | WP-D1 and WP-D2 change how `CombinedRowFilterSql`, `RowFilterParameters` and the mask strings are **produced** for every consumer, including GraphQL (`TreeSqlCompiler`, `SqlDataSourceExecutor`), lakehouse, streaming, procedures and `GatewayExecutionService.FilterRows`. The gate (G4-G7) only exercises the WebSQL compiler. The D1 acceptance criterion "existing suites stay green" is not a differential. In addition, D1-D3 land as separate PRs **before** X1, so `git revert` of X1 does not revert a D1 rendering defect. | 1. New gate **G10 Consumer parity** (§16.5): for every §7.3 consumer, the legacy string producer and the IR renderer run on the same fixture. Classification is the same as G4, and the threshold is 0 `looser`. 2. The renderer output is always fully parenthesized, uses a reserved parameter namespace per dialect (`@__ap_`, `:__ap_`, `$__ap_` as applicable), and consumers fail closed on duplicate parameter names. `SqlSecurityValidator.ValidateRowFilter` keeps running on the rendered text. 3. `CombinedRowFilterSql` becomes a read-only projection of the typed `IRowPolicy`. A decision that carries only a string and no typed policy is denied on the compiler path (no drift by construction). 4. Rollback (§12.2) is amended: the **revert set** is `{X1, D3, D2, D1}` in that order. The cutover PR rehearses the combined revert (build plus G1 plus G7). | WP-D1, WP-D2, WP-B5, WP-E2, WP-X1 |
| SEC-ADG-03 | **Critical** | Oracle binding (new runtime), `SqlDataSourceExecutor.cs:304-332` | ODP.NET binds **by position** by default (`OracleCommand.BindByName = false`). The emitter reuses markers (§3.4 dedup) and the binder orders parameters by ordinal, so positional binding would bind a value to the wrong slot. A tenant or policy value bound to a user slot (or the reverse) is an RLS bypass. The GraphQL/data-source path makes this concrete: verified at `SqlDataSourceExecutor.cs:304-312` and `:327-332`, it emits `@gql_offset`/`@gql_limit` for Oracle (Oracle needs `:` markers), adds the limit parameter **before** the offset parameter while the text uses offset first, and uses `@` markers for the tenant (`@p_tenant_N`, line 209), arguments (`@pN`), row-filter and `$filter` parameters. Today this is unreachable because `SqlConnectionFactory` throws for Oracle. It becomes reachable the moment an Oracle driver is registered. | 1. `OracleCompiledSqlBinder` sets `BindByName = true` and asserts it right before execution. Any `OracleCommand` that reaches `ExecuteReader`/`ExecuteNonQuery` with `BindByName = false` throws (decorator in the connection factory). 2. Every marker in `SqlDataSourceExecutor`, `SqlDataMaskingProvider` (HMAC key markers `@{paramBase}`) and the IR renderer comes from a dialect-aware `FormatParameterMarker(dialect, name)`. A grep-based architecture test forbids string literals that start with `@` followed by an identifier in these files. 3. **Ordering rule:** WP-F4 (the marker fix) merges **before or together with** WP-F1 (the driver registration). Until WP-F4 is merged, `SqlDataSourceExecutor` rejects Oracle explicitly. | WP-F1, WP-F3, WP-F4 |
| SEC-ADG-04 | **High** | Tenant predicate, all dialects; `TenantId` | `TenantId` allows mixed case (`^[a-zA-Z0-9_-]{1,64}\z`), so `Acme` and `acme` are different tenants. The tenant predicate `tenant_col = :t` is evaluated with the column's collation: SQL Server's default `*_CI_AS` collations, Oracle with `NLS_COMP=LINGUISTIC` and a `*_CI` `NLS_SORT`, Databricks columns with `UTF8_LCASE` collation and PostgreSQL non-deterministic ICU collations all compare case-insensitively. The result is a cross-tenant read and write. This is pre-existing for SQL Server, and Oracle and Databricks widen it. OQ-5 (backend semantics) does not cover it, because it is an isolation property and not a result-shape property. | 1. A per-dialect **`TenantPredicateTemplate`** in the capability table compares the tenant **binary-exact**: SQL Server `t.[tenant] = @t AND CAST(t.[tenant] AS varbinary(256)) = CAST(@t AS varbinary(256))` (the first conjunct keeps the index seek); Oracle relies on session `NLS_COMP=BINARY` (WP-F2) **and** emits `NLSSORT`-free equality; Databricks `tenant COLLATE UTF8_BINARY = :t` **(verify on proxy and G9; if the proxy lacks collation support, reject columns whose catalog collation is not `UTF8_BINARY`)**; PostgreSQL `tenant = $1 COLLATE "C"`; SQLite `= ?1` (default `BINARY` collation; reject a `NOCASE` tenant column at catalog load). 2. Startup and catalog reload read the tenant column's collation per data source and **fail closed** (table denied) when the binary template is not applicable. 3. The same binary rule applies to `ParameterOrigin.Policy` equality predicates whose column is flagged `IsolationKey` in the catalog. 4. G6 test `TenantCaseCollision_IsIsolated` on every engine with tenants `acme` and `ACME`, run with a deliberately case-insensitive column or session collation. 5. Stakeholder decision B-1 (§16.10). | WP-A1 (template), WP-D1, WP-F2, WP-C1, WP-B5 |
| SEC-ADG-05 | **High** | Compiler pipeline (DoS) | The AST passes (builder, validator, simplifier, security visitor, coverage verifier, generators, checker) are recursive. Parse-tree depth may reach 3,000 (`MaxParseTreeDepth`), because left-recursive `a OR b OR ...` chains are not limited by `MaxNestingDepth` (parentheses only). No AST pass calls `RuntimeHelpers.EnsureSufficientExecutionStack` or observes the cancellation token (0 `ThrowIfCancellationRequested` calls under `Ast/`). A `StackOverflowException` cannot be caught: it terminates the process for every tenant. `ParseTimeout` (5 s) covers the parse only. The output size is unbounded: N secured table references times the predicate size, plus masks. | 1. Every recursive visitor, the verifier, the generators and the checker call `RuntimeHelpers.EnsureSufficientExecutionStack()` on entry. `InsufficientExecutionStackException` becomes `SqlLimitExceededException(Kind = NestingDepth)`. 2. The whole `Compile` runs under one **compile budget** (`CompileTimeout`, default 2 s, linked with the request token). Each pass checks the token every 256 nodes. 3. New limits in `SqlLimitKind`: `AstDepth` (default 512, measured on the built AST), `SecuredTableReferences` (default 256), `EmittedSqlLength` (default 1 MiB) and `PolicyExpansionFactor` (emitted length divided by input length, default 64). 4. The policy-subquery recursion depth is 1 (SEC-ADG-11). 5. The emitter dedup dictionary uses the default randomized string comparer (no unseeded custom hash). | WP-A2, WP-A4, WP-A8 |
| SEC-ADG-06 | **High** | DML check option (§6.2, INV-10) | The check option is enforced for the **tenant column only**. `UPDATE` and `MERGE ... UPDATE` may still assign any column referenced by a consent row filter (for example `UPDATE t SET region = 'US' WHERE id = 7` under a filter `region = 'EU'`). This moves rows out of the caller's visible set, or into the visible set of another principal of the same tenant, which is the purpose of `WITH CHECK OPTION`. `INSERT` is safe only because SQ-07 rejects consent-filtered tables. | 1. `PolicyPredicate` exposes `ReferencedColumns`. `UPDATE`, `MERGE ... UPDATE` and `MERGE ... INSERT` reject any assignment to a column that is referenced by an applicable policy predicate of the target table (`DmlGuardOptions.RejectPolicyColumnAssignment`, part of `Strict`). 2. Correlated row filters on a DML or `MERGE` target are rejected, as legacy already does for `UPDATE`/`DELETE`. 3. INV-10 is reworded (§16.3). | WP-A7 |
| SEC-ADG-07 | **High** | Emitter, name resolution | The gateway resolves names with its own rules (`FoldIdentifierForScope`, CTE scope stack, case-insensitive catalog lookup). The database resolves the **emitted** text with different rules: PostgreSQL folds unquoted names to lower case, Oracle to upper case (the Oracle generator upper-cases unquoted names), SQL Server depends on collation, unqualified names go through `search_path`, the Oracle `CURRENT_SCHEMA` plus public synonyms, or the SQL Server default schema, and CTE names shadow unqualified physical names. If the gateway considers a reference to be a CTE while the database binds it to a physical table, that table is read without RLS. | New invariant **INV-11 Resolution equivalence** (§16.3): every physical table is emitted **schema-qualified with the catalog's canonical stored name** (exact case, always delimited), never from user spelling; every CTE reference is emitted from the resolved CTE symbol, identical to its definition; recursive CTE self-references are resolved as CTE only inside `WITH RECURSIVE`; an unresolved name is never emitted. The coverage verifier works on resolved symbols, not on text. Session initialization pins `search_path = pg_catalog, <schema>` (PostgreSQL) and `CURRENT_SCHEMA` (Oracle) as a second layer. A DML target is never a CTE: `IsCte(TargetTable)` on the DML path throws instead of skipping RLS (`AstSecurityVisitor.cs:306`, `:377`). | WP-A4, WP-A7, WP-F2 |
| SEC-ADG-08 | **High** | `MERGE` (WP-A7, §6.2) | (a) Target RLS in `ON` is safe only as long as `WHEN NOT MATCHED BY SOURCE` cannot be expressed. With target RLS in `ON`, every other-tenant row is "not matched by source", so `... BY SOURCE THEN DELETE` would delete all other tenants' rows. The grammar does not have it today, but nothing pins that. (b) `WHEN [NOT] MATCHED AND <cond>` and `ON` may reference masked columns, which gives an affected-row-count side channel. (c) SQL Server **requires** a terminating `;` after `MERGE`, which conflicts with the checker's `;` rule. Weakening the checker globally would be a regression. (d) Unique-key violations from `MERGE ... INSERT` and `INSERT` (R-12) return driver texts that contain the duplicate key value (SQL Server 2627/2601 "The duplicate key value is (...)", PostgreSQL "Key (id)=(42) already exists"). | (a) The architecture test `MergeClauseTypes_AreClosed` pins the `MergeClause` subtypes, and the grammar test `Grammar_HasNoNotMatchedBySource` fails if `SqlBase.g4` gains `BY SOURCE`/`BY TARGET`. Generators never emit `BY SOURCE`. (b) Masked columns and whole-row references are rejected in `ON`, in every `WHEN` condition and in `MERGE` assignment right-hand sides. (c) The SQL Server generator appends exactly one `;` as the **last** character of a `MERGE` and registers it as a structural position. The checker allows a `;` only at a registered final position for `SqlStatementClass.Merge` on SQL Server. (d) R-12 is **accepted** (parity with `INSERT`), on condition that constraint-violation errors are mapped to a typed `DmlConstraintViolationException` with no driver text, no key value and no constraint name in the response. DBA guidance (Phase 6): unique keys of multi-tenant tables should include the tenant column. | WP-A7, WP-A2 |
| SEC-ADG-09 | **High** | Databricks REST connector (WP-C3), CI secrets (WP-C5) | The REST client builds URLs from server-returned data (`statement_id`, `next_chunk_internal_link`, `external_links`) and holds a bearer token. A crafted `statement_id` (`../../`) or an absolute chunk link can send the token to another API path or host. Other points: redirects, unbounded response buffers, `Authorization` header logging by `IHttpClientFactory` logging at Trace level, long-lived PATs, and G9 running "on a PR labeled `sql-cutover`" with workspace secrets available to PR code. | 1. `statement_id` must match `^[0-9a-fA-F-]{36}$` (or the documented id format **(verify)**) and is path-encoded. Chunk links are followed only if they are **relative** and start with `/api/2.0/sql/statements/{same id}/result/chunks/`. `disposition` is always `INLINE`, and a response that contains `external_links` is rejected. 2. `AllowAutoRedirect = false`, HTTPS only, TLS 1.2 or later, no custom certificate validation callback, `MaxResponseContentBufferSize` equal to `ByteLimit` plus overhead, and `SsrfProtectionHandler` with the exact host from configuration (no wildcard). The DNS result is checked against private ranges unless the host is on an explicit private-link allow-list. 3. The OAuth token endpoint is derived from the host (`https://{host}/oidc/v1/token`) and is not configurable. Tokens are cached per `(host, clientId)` in memory only and are never logged or put into exception messages. `RedactLoggedHeaders("Authorization")` is set on the typed client. 4. `Auth.Mode = Pat` is allowed **only in Development** (`GatewayStartupValidator`). 5. G9 runs only from a protected GitHub Environment with required reviewers, never for fork PRs, using a dedicated service principal that is restricted to a sandbox catalog with no access to production data. The `sql-cutover` label alone does not release secrets. 6. Cancellation and timeout send `POST .../cancel`. `on_wait_timeout = CANCEL`, and `wait_timeout` is clamped to 5-50 s. | WP-C3, WP-C5 |
| SEC-ADG-10 | **Medium** | Databricks generator (WP-C1) | Spark and Databricks may perform `${...}` variable substitution on statement text before parsing (`spark.sql.variable.substitute`). User-controlled column aliases are emitted as backtick identifiers. An alias such as `` `${...}` `` could change the statement after the gateway checked it. | The Databricks generator rejects `$`, `{` and `}` in **any** emitted identifier (`SqlLimitKind`-style typed rejection). A new token guard `RejectVariableSubstitutionSequences` (`${` anywhere in a token) is on for the Databricks profile. The Spark proxy runs with `spark.sql.variable.substitute=true` so that the test covers the worst case. | WP-C1, WP-C4 |
| SEC-ADG-11 | **High** | `SqlFilterCompiler.Compile` (`:349-370`), correlated row-filter subqueries, policy parser | Tables referenced **inside** policy subqueries (virtual filters, correlated consent filters, Casbin correlated subqueries) are compiled with `PolicyProvider = new DefaultRlsPolicyProvider(predicate: _ => false)`, so they get **no tenant predicate**. An `EXISTS (SELECT 1 FROM entitlements e WHERE e.fk = autheris_target.id ...)` over a multi-tenant `entitlements` table lets tenant B's rows decide which of tenant A's rows are visible. That is a cross-tenant information flow. | New `SecurityScope.PolicySubquery`. Every physical table inside a policy subquery receives the **tenant predicate** (bound tenant parameter, binary template from SEC-ADG-04). It does not receive consent filters, to prevent recursion. The policy-subquery depth is limited to 1, and nested policy subqueries are rejected. The coverage verifier checks `PolicySubquery` scopes. Policy subqueries may reference catalog tables only (as §3.5 requires). | WP-A5, WP-D3 |
| SEC-ADG-12 | **High** | `AdvancedRlsFilterGenerator` (`BuildCrossSourceSetFilter`, `BuildCorrelatedSubquery`), `CasbinEnforcementService.cs:937`, `RowFilterSqlBuilder.cs:218-223,359` | These producers render consent values **inline** through `DatabaseDialect.FormatSafeLiteral`. §7.2 names `RowFilterSqlBuilder` but not these producers. If they stay string-based, INV-5 ("no trusted raw fragments") and the bind-everything decision are false on the compiler path. | WP-D1 includes `AdvancedRlsFilterGenerator`, `RlsFilterGenerator` and the Casbin correlated subquery: each builds `PolicyPredicate` directly, with one `PolicyParameterExpression` per value. The OR-chunked IN lists are replaced by one IN list subject to the dialect limits (or by array binding, B-3). The architecture test `NoInlineLiteralRendering_OnGovernedPath` forbids `FormatSafeLiteral`/`EscapeSqlLiteral` in every type reachable from `GovernedSqlRewriter`, `SqlFilterCompiler` and the IR renderer. | WP-D1 |
| SEC-ADG-13 | **High** | CI gate §9 (big-bang justification) | The gate is not sufficient to justify deleting legacy. The gaps are: (a) G4 classifies by multiset hash, which does not show the **direction** of a mask difference (a clear value where legacy masked shows up as "different", and `equivalent-result` is not defined); (b) the visibility oracle checks tenant-B markers only, not consent filters within a tenant; (c) the security mutants are only a proposal in §14, and Stryker runs nightly on the base commit, so a PR can remove a check without failing; (d) the gate runs on the PR head, not on the merge result; (e) there is no flake policy, so a retried security test can turn green; (f) `accepted-differences.json` has no schema that makes `ast-looser` impossible to accept; (g) there is no proof that every governed entry point (WebSQL, SQL endpoints, MCP dataset tools, virtual filters, GraphQL row filters) actually goes through `Compile` and the binder; (h) cache poisoning, the consumers (SEC-ADG-02) and Oracle on the real gateway path are not covered. | §16.5 lists the minimum additions. They are binding preconditions for WP-X1. | WP-B3, WP-B4, WP-B5, WP-E2, WP-E3, WP-X1 |
| SEC-ADG-14 | **High** | `DbSessionContextInitializer`, `SqlConnectionFactory`, Oracle session | (a) `DbSessionContextInitializer.ResolveDialect` falls back to **SQLite** for an unknown provider, and `InitializeSessionAsync` silently returns `null` (no session context, no transaction) for every dialect other than PostgreSQL and SQL Server. These are fail-open defaults for Oracle and Databricks. (b) An Oracle session has no pinned comparison or format semantics (`NLS_COMP`, `NLS_SORT`, `NLS_DATE_FORMAT`, `NLS_NUMERIC_CHARACTERS`, `TIME_ZONE`). ODP.NET connection pooling keeps `ALTER SESSION` and `DBMS_SESSION` state across rentals, so one tenant's session state can be reused for another. | 1. `ResolveDialect` throws for unknown providers. `InitializeSessionAsync` has an explicit branch per production dialect and throws `NotSupportedException` for any other. Databricks has an explicit "no session state" branch that is covered by a test. 2. WP-F2 defines Oracle session initialization (§16.6) and runs it on **every pool rental**, not only for new physical connections, with a reset in `finally`. 3. A startup probe reads `NLS_SESSION_PARAMETERS` and fails startup if the pinned values do not hold. | WP-F2, WP-D4 |
| SEC-ADG-15 | **Medium** | HMAC masks (`SqlDataMaskingProvider`, `MaskExpression`) | The per-tenant HKDF-derived HMAC key is a **bound parameter**. Parameter values are visible in database-side diagnostics: Databricks query history (visible to warehouse users with `CAN VIEW`), PostgreSQL `log_parameter_max_length`/`auto_explain`, SQL Server Extended Events and Query Store parameter capture. With the key, a reader can test guessed clear values against the pseudonyms. Oracle has no HMAC without an `EXECUTE` grant on `DBMS_CRYPTO`, and today it falls through to the SQLite UDF `gateway_hmac_sha256` (a runtime error, fail closed but broken). Databricks has no built-in keyed HMAC. | 1. A capability flag `InDbHmac` in the capability table: PostgreSQL (pgcrypto), SQL Server and SQLite (UDF) are `true`; Oracle is `true` only if a startup probe confirms `EXECUTE ON DBMS_CRYPTO` (then `DBMS_CRYPTO.MAC`), otherwise `false`; Databricks and DuckDB are `false`. 2. If `InDbHmac = false`, an HMAC rule degrades to `Redact` (fail closed), with an operator-visible startup warning. Gateway-side HMAC is **rejected** for WebSQL, because user SQL can aggregate, sort or deduplicate the raw value before the gateway sees it. 3. Phase 6 operator guidance: disable parameter-value capture on governed data sources, or set `DataMasking:PreventInDbHmacKeyExposure = true`. Stakeholder decision B-2. | WP-A6, WP-D2, WP-F6 |
| SEC-ADG-16 | **Medium** | All dialects (side channel, pre-existing) | User predicates of the outer query may be evaluated **before** the RLS predicate of the secured derived table, because optimizers push predicates down and flatten subqueries (PostgreSQL derived tables are not `security_barrier`). An error-raising expression (division by zero, a failing cast, Oracle `ORA-01722` from an implicit conversion) therefore reveals whether some row, possibly of another tenant, satisfies a condition. The bandwidth is one bit per query. | 1. Backend errors on the governed path map to typed, generic error codes. Driver message text, SQLSTATE detail, key values and object names are never returned to the caller (consistent with Threat 4.3). The full error is logged server-side, redacted. 2. A bind type is derived from the **catalog column type** of the compared column, not only from the literal type, so the binder never forces an implicit conversion of the column (this removes the `ORA-01722` class). 3. A capability `OptimizerFence` (PostgreSQL `OFFSET 0` in the secured subquery) is measured in G8 and is **off** by default. 4. Documented as a residual risk (§16.7). | WP-A3, WP-X1, WP-B5 |
| SEC-ADG-17 | **Medium** | Oracle semantics | In Oracle the empty string is `NULL`. A bound `''` tenant or policy value turns `col = :p` into `UNKNOWN`, which is stricter, but `NOT (col = :p)`, `CASE ... ELSE` and `COALESCE` forms in admin predicates can become looser. | The Oracle binder rejects an empty `String` value with `ParameterOrigin.Tenant` or `ParameterOrigin.Policy` (fail closed). The policy parser for an Oracle data source rejects `CASE`/`COALESCE`/`NOT` over a policy parameter whose value can be empty. For user literals, `''` is a `semantic-note` in the conformance matrix. | WP-F3, WP-A5 |
| SEC-ADG-18 | **Medium** | `LIKE` in policy predicates | `LIKE` semantics differ per dialect: SQL Server treats `[...]` as a character class, PostgreSQL and Databricks use `\` as the default escape, Oracle and Trino have no default escape. A policy value containing `[`, `\`, `%` or `_` can therefore match more rows on one dialect than on another. | For `LIKE` with a `ParameterOrigin.Policy` pattern, the emitter always emits an explicit `ESCAPE` from a constant template (`ESCAPE '\'`, registered like a `BindExpressionTemplate`), and the binder escapes `%`, `_`, `\` and, for SQL Server, `[` in the bound value unless the policy marks the value as an intentional pattern. Conformance fixtures cover each dialect. | WP-A3, WP-A5 |
| SEC-ADG-19 | **Medium** | Binder, client parameters (F-5) | Client named parameters (`@p1`, `:p1`, `@__autheris_tenant`, `@gql_limit`, `@__gql_x`) can collide with internal marker names. With legacy, `RestoreClientParameters` uses a regular expression over the secured SQL. A collision binds a client value to an internal slot or the reverse. | Internal markers are never derived from client names. Client named parameters become `ParameterReference(ClientNamed)` and get generated markers like any other value. Client names that start with a reserved prefix (`p` followed by digits, `__`, `gql_`, `autheris`) are rejected at parse time. New invariant INV-13 (§16.3). | WP-A2, WP-X1 |
| SEC-ADG-20 | **Medium** | `IPolicyExpressionParser` cache | The proposed parse-cache key `(SHA-256 of text, table identity, catalog version)` does not include the function allow-list, the parser/compiler version or the declared parameter types. A changed allow-list would keep serving a parse result that the new allow-list rejects. | Key = SHA-256 over `(policy text, table identity, catalog version, allowed-function-set hash, parameter name/type set, CompilerVersion)`. The cache is bounded (count and size) and has a per-tenant share so one tenant cannot evict the others. A parse failure is cached as a **negative** entry with a short TTL so that a broken predicate cannot be used to load the parser. | WP-A5 |
| SEC-ADG-21 | **Medium** | `CrossSourcePlanner` (PLAN-15), `GenerateInlineForInternalPlan` | Until WP-X3, the federated DuckDB plan is generated with inline literals from the user's AST, and the staging data comes from Web APIs (untrusted). | 1. The transitional method runs the `EmittedSqlInvariantChecker` in a DuckDB "inline literal" mode (literals allowed, escaped by DuckDB rules, no `--`, `/*`, `;`, `$$`), and the hostile corpus is executed through it in G6. 2. The DuckDB connection used for staging applies the lock-down already used by `DuckDbOlapEngine` (`enable_external_access = false`, `lock_configuration = true`, `autoinstall_known_extensions = false`, `autoload_known_extensions = false`). 3. WP-X3 deletes the method. Its due date is tracked in the master plan. | WP-A2 (transitional), WP-X3 |
| SEC-ADG-22 | **Medium** | Table functions, passthrough | The Trino `TABLE(system.query(query => '...'))` passthrough and backend table functions (DuckDB `read_csv`/`read_parquet`, SQL Server `OPENROWSET`/`OPENQUERY`, Oracle `TABLE(...)`, PostgreSQL `dblink`) bypass RLS by design. | `AllowedTableFunctions` is **empty by default** for every dialect. The builder rejects `system.query` and every table function that is not on the allow-list, and the coverage verifier treats a table function source as a violation unless it is allow-listed **and** declared side-effect-free and table-free in the capability table. | WP-A4, WP-A8 |
| SEC-ADG-23 | **Medium** | Audit, telemetry, exceptions (INV-8') | §8.1 puts the compiler version, dialect and bind count into the audit record. There is no tamper-evident link to the statement that actually executed, and exception messages (`SqlLimitExceededException`, `PolicyParseException`, `PolicyConflictException`) could echo policy text or values. | 1. The audit record gets `sql.compiled_digest = HMAC-SHA256(auditKey, CompilerVersion || Dialect || CompiledSql.Sql || parameter shape)`, where the parameter shape is `(ordinal, type, origin)` only. It never contains values or value hashes (a hash of a low-entropy value such as a tenant or an SSN can be brute-forced). The record is part of the existing HMAC chain. 2. Compiled SQL text is stored only when `Audit:StoreCompiledSql = true` (default `false`); it contains no values by construction. 3. Typed exceptions carry kinds, counts and identifiers only, never values or policy text. A test scans the messages of every typed rejection for the hostile-corpus markers. 4. Spans and counters keep the NFR-6 rule (no literals, no tenant values). | WP-A8, WP-X1 |
| SEC-ADG-24 | **Medium** | Supply chain | New or changed dependencies: `Oracle.ManagedDataAccess.Core` in Infrastructure (production, license "Oracle Free Use Terms and Conditions", not OSI); the Spark 4.0 + `delta-spark` container (Maven jars fetched at runtime if `--packages` is used); the Oracle Free container image; FsCheck.Xunit, Stryker.NET and BenchmarkDotNet tools. Databricks uses `HttpClient` only, with **no** new client package (approved). | 1. The Oracle driver uses the newest patched 23.x release at implementation time, not automatically the 23.7.0 pinned in the integration tests. The test and production versions are aligned. The NuGet author signature (Oracle) and the repository signature are verified (`trustedSigners` in `NuGet.config`), and `packageSourceMapping` maps `Oracle.*` to nuget.org. `packages.lock.json` is committed and restore uses `--locked-mode`. `dotnet list package --vulnerable --include-transitive` is a CI gate. The SBOM is updated. The license needs legal review before release. 2. Container images are pinned by digest. Delta jars are baked into a CI-built image with SHA-256-verified downloads (no runtime `--packages`). Python dependencies use `pip install --require-hashes`. 3. Tool versions are pinned in `.config/dotnet-tools.json`. 4. Trivy (already in CI) scans the Spark image. | WP-F1, WP-C4, WP-E2 |
| SEC-ADG-25 | **Medium** | `TableAccessDecision` layering (R-11) | Confirmed: an opaque `IRowPolicy` in Domain, implemented in Application, is acceptable. The risk is drift between a string and a typed policy on the same decision. | `IRowPolicy` has no public factory from text. `CombinedRowFilterSql` and `MandatoryRowPredicateSql` are computed from `IRowPolicy` (read-only). `WithMandatoryPredicate` and `Restrict` accept typed input only. Architecture test: Domain does not reference `TrinoSqlEngine`, and no setter for `CombinedRowFilterSql` exists. | WP-D1 |
| SEC-ADG-26 | **Medium** | Policy migration (OQ-3), availability | After X1, every admin predicate that is not in Trino syntax denies its table. That is fail closed, but a deny at scale is an outage. | `tools/PolicyPredicateLint` is a **blocking pre-deploy step** of the X1 release. It runs against the production policy store export and must report 0 unparsable predicates, or each one has a signed-off exception. `GatewayStartupValidator` lists every denied table at startup. | WP-D1, WP-X1 |
| SEC-ADG-27 | **Low** | Bind and IN-list limits (Q-4, AP-11) | Large consent value lists that are inline today hit the SQL Server bind limit (2,100) or the Oracle IN-list limit (1,000) once they are bound, and are denied. This is fail closed (security-neutral) but an availability regression. | Default: reject (as planned). Option for stakeholder decision B-3: one **array-bound** parameter per list through a constant `BindExpressionTemplate` (PostgreSQL `= ANY($n)`, SQL Server `IN (SELECT value FROM OPENJSON(@pN))` with a typed `WITH` schema, DuckDB list parameter, Oracle a SQL collection type). Never compaction (AP-11). | WP-A1 (if B-3 = array) |
| SEC-ADG-28 | **Low** | Obsolete key (§7.1), §14 item 9 | — | Confirmed: **fail fast** at startup on `WebSql:SqlRewriterEngine`. Warn-and-ignore would let operators believe in a kill switch that does not exist. | WP-X1 |
| SEC-ADG-29 | **Low** | Token guards (§14 item 7) | — | Confirmed: every `SqlTokenSecurityOptions` guard stays (AP-3). Databricks profile: `RejectComments`, `RejectBackslashInStrings`, `RejectEscapedStringLiterals`, `RejectDollarQuoting`, `RejectNonAsciiIdentifiers`, `RejectDotsInQuotedIdentifiers`, `RejectTimeTravelQueries` and the new `RejectVariableSubstitutionSequences` are all `true`. Oracle profile: like PostgreSQL, but with `RejectDollarQuoting = true`. The Oracle alternative quoting (`q'[...]'`, `nq'...'`) cannot be emitted, because no literal is emitted and identifiers are always delimited. The hostile corpus contains it to prove that. | WP-A8, WP-C1, WP-F3 |

### 16.3 Hardened invariants

INV-1..INV-7 stay as in §8.1, with the hardening below. INV-8 is replaced by INV-8' (§8.1). **INV-9 (shadow isolation) is retired** because SD-1 removed the shadow mode, and its number is not reused.

| ID | Invariant (hardened wording) | STRIDE |
|---|---|---|
| INV-1 | Fail closed on unknown rules, nodes, functions, table functions, dialects, capabilities, **providers and session initializers** (no `_ =>` default to a dialect anywhere on the governed path, including `DbSessionContextInitializer.ResolveDialect`). | T, E |
| INV-2 | Complete coverage in every scope of §4.3 **plus `PolicySubquery` and `MergeOn`**. The predicate sits in the `WHERE` of the secured derived table, never in a join `ON` or an outer `WHERE` (outer-join null extension cannot reveal filtered rows). | I, E |
| INV-3 | Predicate preservation (unchanged). | T, I |
| INV-4 | Values bound, identifiers delimited and **resolved to catalog canonical names**. Bind types follow the catalog column type for comparisons (SEC-ADG-16). | T, I |
| INV-5 | No raw fragments: no `TrustedSqlExpression`, no `FormatSafeLiteral` on the governed path (SEC-ADG-12). | T |
| INV-6 | No source comments or whitespace in the output. The checker runs in production. A `;` is allowed only at the registered final position of a SQL Server `MERGE`. | T |
| INV-7 | Bounded resources: query length, nesting depth, parse-tree depth, **AST depth, execution-stack guard, compile time budget, secured table references, emitted length, expansion factor**, bind count, IN-list size and identifier length, each with a typed `SqlLimitKind`. | D |
| INV-8' | Compiler transparency, plus the HMAC-chained `sql.compiled_digest` (SEC-ADG-23). | R |
| INV-10 | DML check option: no write can create, modify or delete a row outside the caller's tenant, **or move a row across any applicable row-policy boundary** (no assignment to policy-referenced columns). Masked columns are not writable and not usable in DML or `MERGE` predicates. | T, E |
| INV-11 (new) | **Resolution equivalence:** the database binds every emitted name to the same object that the gateway secured (schema-qualified canonical physical names, symbol-based CTE references, pinned `search_path` and `CURRENT_SCHEMA`). | E, I |
| INV-12 (new) | **Cache integrity:** a cached compile is reachable only by a request with an identical canonical `CompileRequest` and policy shape (SHA-256, full compare). Cached entries contain no tenant, policy or mask values. Only verified outputs are cached. | I, E |
| INV-13 (new) | **Binding integrity:** every marker in the text maps 1:1 to exactly one `BoundParameter` of the expected origin and type, binding is by name where the provider supports it (Oracle `BindByName = true`), and client names can never alias internal markers. | T, E |
| INV-14 (new) | **Session integrity:** every setting that changes comparison, escaping or name-resolution semantics (PostgreSQL `standard_conforming_strings` and `search_path`; Oracle NLS and `CURRENT_SCHEMA`; SQL Server isolation level; DuckDB lock-down) is pinned on **every connection rental** and verified at startup. Pooled state never crosses tenants. | T, I |
| INV-15 (new) | **Tenant equality is binary-exact** on every dialect, regardless of column collation or session settings (SEC-ADG-04). | I, E |
| INV-16 (new) | **Non-disclosure:** bound values never appear in logs, spans, metrics, audit records, cache keys or exception messages. Backend errors reach the caller only as typed generic codes. | I |
| INV-17 (new) | **Consumer parity:** text rendered from the typed IR for a §7.3 consumer is never looser than the legacy text for the same decision (G10). | I |

**STRIDE summary for this track**

| STRIDE | Threats in this track | Findings and invariants |
|---|---|---|
| Spoofing | Databricks token theft through a crafted host, link or redirect; PAT misuse | SEC-ADG-09 |
| Tampering | Injection through identifiers, markers, variable substitution and inline literals; misbinding; DML across a policy boundary; `MERGE BY SOURCE` | SEC-ADG-03, -06, -08, -10, -12, -19, -21; INV-4, -5, -6, -10, -13 |
| Repudiation | No proof of which statement executed | SEC-ADG-23; INV-8' |
| Information disclosure | Cache cross-principal reuse; collation and NLS tenant collisions; policy subqueries without the tenant predicate; consumer rendering drift; error side channels; HMAC key in DB logs; LIKE and empty-string semantics | SEC-ADG-01, -02, -04, -11, -15, -16, -17, -18; INV-2, -12, -15, -16, -17 |
| Denial of service | Stack overflow, compile time, output growth, cache eviction, parse cache load, large IN lists | SEC-ADG-05, -20, -27; INV-7 |
| Elevation of privilege | Name-resolution differential, table-function passthrough, permission check skipped through the cache, fail-open session defaults | SEC-ADG-01, -07, -14, -22; INV-1, -11, -14 |

### 16.4 Answers to the Phase 2 hand-off (§14)

1. **INV-3 design:** approved. Add `SecurityScope.PolicySubquery` and `SecurityScope.MergeOn`. `VALUES` sources contain no table and need no scope, but the verifier must recognize them explicitly (not by omission). Lateral and recursive CTE bodies are covered by the existing scopes as long as INV-11 holds (a recursive self-reference is a CTE only inside `WITH RECURSIVE`). Table functions are rejected (SEC-ADG-22).
2. **Literal allow-list and checker:** approved. The checker runs **in production** on every compile. `BindExpressionTemplates` are compile-time constants in code (never configuration), contain exactly one placeholder, use format literals from a closed reviewed set, and every change needs a CODEOWNERS security approval. The template table is a G2 fixture.
3. **Policy IR boundary:** canonical Trino syntax confirmed (OQ-3). Correlated `EXISTS`/`IN` subqueries are allowed only against catalog tables and receive the tenant predicate (SEC-ADG-11). Non-deterministic and session-dependent functions (`random`, `now`, `current_user`, `uuid`) and window functions are rejected in row policies. A predicate has at most 256 nodes. The cache key follows SEC-ADG-20. `IRowPolicy` layering is confirmed under SEC-ADG-25.
4. **DML/MERGE:** replacing the verified tenant literal with the bound tenant parameter is approved. Target RLS in `ON` is approved with the SEC-ADG-08 conditions. R-12 is accepted with error sanitization. `MERGE ... INSERT` on consent-filtered tables is rejected (yes), and assignments to policy-referenced columns are rejected (SEC-ADG-06).
5. **Databricks:** see SEC-ADG-09, -10 and -15. The provisional bind budget of 1,000 is approved. Databricks stays off the production list until the first green G9 (binding).
6. **Gate thresholds:** see §16.5. `accepted-differences.json` is signed by the security CODEOWNERS group (at least one reviewer who is not the PR author). The Stryker break threshold of 85% stays for the nightly run, and the named security mutants become a deterministic PR gate.
7. **Token guards:** see SEC-ADG-29.
8. **Audit:** see SEC-ADG-23. Parameter values are never logged.
9. **Removed key:** fail fast (SEC-ADG-28).
10. **Rollback:** the git-revert model is acceptable **only** with the SEC-ADG-02 revert set `{X1, D3, D2, D1}`, a rehearsed combined revert (build plus G1 plus G7 on the reverted tree), and a runbook (Phase 6) that names the revert triggers: any confirmed cross-tenant row, any `SecurityCoverageException` or `EmittedSqlInvariantViolation` rate above zero that is not explained by a hostile input, or a P99 compile latency above twice the budget. It also names the on-call owner and a time-to-revert target of 4 hours.

### 16.5 CI gate: sufficiency for the big-bang cutover

**Assessment:** G1-G9 as defined in §9.3 are **not sufficient** to justify deleting legacy without an observation window (SEC-ADG-13). The following additions are the **minimum**. With them, the gate gives evidence against regressions compared with legacy (G4, G10), and absolute evidence for isolation (G5-c', G6) that does not depend on legacy being correct.

| # | Addition | Gate | Threshold |
|---|---|---|---|
| M-1 | **Directional classification.** G4 compares per cell, not only by multiset hash. Classes: `ast-looser-rows` (a row that legacy did not return), `ast-looser-mask` (a clear value of a masked column where legacy returned a masked value), `ast-looser-write` (DML changed a row that legacy did not), `ast-accepts` (legacy rejects, AST accepts and returns or writes anything that the independent oracle of M-2 flags). `equivalent-result` is defined as "equal after typed normalization (numeric scale, timestamp precision, trailing-space-insensitive only for `CHAR`)". Nothing else counts as equivalent. | G4 | 0 in every `ast-looser-*` class |
| M-2 | **Independent policy oracle.** A C# reference evaluator applies the fixture's tenant, consent and mask policies to the fixture rows in memory. Every result row and cell of every engine (AST path) is checked against it: no row outside the policy, no clear masked value. This does not depend on legacy. | G5-c', G6 | 0 violations |
| M-3 | **Security mutant suite.** A deterministic, named mutant set (operators in test code, toggled by an internal test hook that is compiled out of Release builds) runs on **every PR**. Each mutant must be killed by at least one gate test. The minimum set is the eight from §14 item 6 plus: drop `PolicySubquery` tenant predicate; skip the coverage verifier; disable `BindByName`; drop the binary tenant template; allow a policy-column assignment in `UPDATE`; accept `WHEN NOT MATCHED BY SOURCE`; omit the policy values from the cache rebind; resolve a CTE case-insensitively against a quoted physical name; accept `${` in a Databricks identifier; skip the execution-stack guard. | G7 | 100% killed |
| M-4 | **Merge-result gating.** The gate runs on the merge-queue commit (or the `pull_request` merge ref), so the result of the merge is tested and not only the PR head. | G1-G10 | Required check |
| M-5 | **Flake policy.** Security-tagged tests (`Category=Security`) are never retried. A flaky security test fails the gate and can only be quarantined with a security CODEOWNERS approval and a linked issue. | all | 0 retries |
| M-6 | **`accepted-differences.json` schema.** The JSON schema allows only `ast-stricter` and `ast-error` with a justification, an issue link and a reviewer. An `ast-looser-*` entry fails schema validation. | G4 | Schema-valid |
| M-7 | **Entry-point coverage.** An architecture test proves that every governed entry point (WebSQL, SQL endpoints, MCP dataset tools, virtual filters, GraphQL table queries through the IR renderer) reaches `ISqlEngine.Compile` or `IPolicyPredicateRenderer`, and that no `DbCommand.CommandText` on these paths is assigned from anything other than `CompiledSql.Sql` or a renderer result. An end-to-end test per entry point and per production dialect runs through the real DI container. | G1, G6 | All green |
| M-8 | **Cache poisoning suite.** Two principals of the same tenant with different consent values, a writer followed by a reader with the same SQL, a tenant `acme` followed by `ACME`, a capability or compiler version change, and a cancelled compile. Each must result in a miss or a correct rebind, never a hit that serves the other request's values or decisions. | G6 | All green |
| M-9 | **G10 Consumer parity** (SEC-ADG-02): the legacy string producer and the IR renderer for every §7.3 consumer, on SQLite, PostgreSQL and SQL Server, plus the in-memory evaluators (`FilterRows`, `StreamingRowFilterAstEvaluator`) on the same fixture. | G10 (new) | 0 `looser` |
| M-10 | **Oracle on the real gateway path** (Stream F): G6 suites run through `SqlConnectionFactory`, `DbSessionContextInitializer` and the binder, including a hostile session (NLS set to linguistic and case-insensitive at the database level before the gateway connects) and pool reuse across tenants. | G6 | All green |
| M-11 | **Combined revert rehearsal** (SEC-ADG-02): `git revert` of `{X1, D3, D2, D1}` on a scratch branch builds and passes G1 and G7. | X1 PR | Green |
| M-12 | **Stryker on the cutover PR head**, not only on the base commit, for the files listed in G7. | G7 | At least 85% |

**Updated cutover precondition (replaces the paragraph after the §9.5 workflow):** G1-G10 are green on the merge-queue commit of the cutover PR; M-3, M-8 and M-11 are green; the nightly profile and Stryker (M-12) are green on the cutover PR head; G9 is green if Databricks is to be listed as Production; WP-F5 is green if Oracle is to be listed as Production; `tools/PolicyPredicateLint` reports 0 unparsable production predicates (SEC-ADG-26); there is a Phase 3 sign-off on the final `accepted-differences.json` and a Phase 5 approval.

### 16.6 Oracle runtime: Stream F (SD-8)

Phase 2 did not plan the Oracle runtime (OQ-1 default "no"). SD-8 reverses that. Stream F has these work packages. They run in parallel with Stream C and depend on A2 (binder contract) and A1 (capabilities).

**Verified current state:** `SqlConnectionFactory.cs:31-38` has no Oracle driver ("Oracle and Databricks are dialects without a driver here"). Only `tests/Autheris.Tests.Integration` references `Oracle.ManagedDataAccess.Core` 23.7.0 and `Testcontainers.Oracle` 4.15.0. `OracleIntegrationTests` builds `OracleCommand` directly from `GenerateGovernedSql` output (`OracleIntegrationTests.cs:153-171`), so it bypasses the factory, the session initializer and any binder. `DbSessionContextInitializer` has no Oracle branch. `SqlDataSourceExecutor.cs:304-332` emits `@gql_offset`/`@gql_limit` for Oracle (SEC-ADG-03).

**WP-F1 Oracle driver and connection factory** (files `src/Autheris.Infrastructure/Autheris.Infrastructure.csproj`, `SqlConnectionFactory.cs`, `GatewayStartupValidator.cs` (Oracle section), `NuGet.config`)

- Tests first:
  - `ConnectionFactory_Oracle_ReturnsOracleConnection`.
  - `OracleConnectionString_RejectsPrivilegedLogin` (`DBA Privilege=SYSDBA|SYSOPER|SYSASM`, `User Id=/` OS authentication, `Proxy User Id` without configuration).
  - `OracleConnectionString_RequiresTcpsOutsideDevelopment`.
  - `OracleConnectionString_SecretsOnlyFromKeyVault` (plaintext password outside Development fails startup).
  - `OracleCommand_BindByNameFalse_Throws` (decorator, SEC-ADG-03).
  - Supply chain: `Oracle.*` source mapping and signature verification, a locked restore, no vulnerable transitive packages (SEC-ADG-24).
- Acceptance: Oracle connections open only through the factory. **Merges with or after WP-F4.**

**WP-F2 Oracle session initialization** (files `DbSessionContextInitializer.cs`, `SqlConnectionFactory.cs`)

- One anonymous PL/SQL block runs on **every** pool rental, with bound values only:
  - `ALTER SESSION SET NLS_COMP = 'BINARY'`, `NLS_SORT = 'BINARY'`, `NLS_LANGUAGE = 'AMERICAN'`, `NLS_TERRITORY = 'AMERICA'`, `NLS_NUMERIC_CHARACTERS = '.,'`, `NLS_DATE_FORMAT = 'YYYY-MM-DD'`, `NLS_TIMESTAMP_FORMAT = 'YYYY-MM-DD"T"HH24:MI:SS.FF6'`, `NLS_TIMESTAMP_TZ_FORMAT = 'YYYY-MM-DD"T"HH24:MI:SS.FF6TZH:TZM'`, `TIME_ZONE = '+00:00'`, `CURRENT_SCHEMA = <configured schema>` (validated identifier).
  - `DBMS_SESSION.SET_IDENTIFIER(:corr)` with an opaque correlation id (never the tenant id in clear), and `DBMS_APPLICATION_INFO.SET_MODULE('autheris', NULL)`.
  - The connection is returned to the pool only after `DBMS_SESSION.CLEAR_IDENTIFIER` in `finally`. If clearing fails, the connection is disposed and removed from the pool (`OracleConnection.ClearPool` for that connection).
- Explicit Oracle branch in `InitializeSessionAsync`. DML runs in an explicit transaction. `ResolveDialect` and the unknown-dialect default throw (SEC-ADG-14).
- Tests first:
  - `OracleSession_NlsPinned_OnEveryRental` (pool size 1, two rentals, `NLS_SESSION_PARAMETERS` asserted each time).
  - `OracleSession_IdentifierCleared_OnReturn`.
  - `OracleSession_HostileDatabaseDefaults_AreOverridden` (container with `NLS_COMP=LINGUISTIC`, `NLS_SORT=BINARY_CI` set by a logon trigger).
  - `UnknownProvider_Throws`.
  - `StartupProbe_FailsWhenNlsNotPinned`.

**WP-F3 Oracle binder and executor wiring** (files `Autheris.Infrastructure/Persistence/OracleCompiledSqlBinder.cs` (new), `SqlDialectMapper.cs` (`IsExecutable` adds Oracle), `ChunkedQueryExecutor.cs`, `DatabaseParameterBudgetProvider.cs`)

- `:pN` markers; `BindByName = true` asserted; `OracleDbType` mapping from `SqlParameterType` and the catalog column type (SEC-ADG-16); an empty string is rejected for `Tenant`/`Policy` origins (SEC-ADG-17); bind variable names stay within 30 bytes.
- Tests first:
  - `OracleBinder_ReusedMarker_BindsSameValue`.
  - `OracleBinder_OutOfOrderMarkers_BindByName`.
  - `OracleBinder_EmptyTenantOrPolicyString_Throws`.
  - `OracleBinder_TypesFollowCatalogColumn`.
  - `OracleInList1001_Rejected`.
  - `OracleBindLimitProbe` (G6, replaces the **(verify)** 32,767).

**WP-F4 `SqlDataSourceExecutor` and masking marker fix** (files `SqlDataSourceExecutor.cs` (pagination, tenant, argument, `$filter` and row-filter markers; parameter order), `SqlDataMaskingProvider.cs` (HMAC key markers), `Autheris.Domain/Common/DatabaseDialect.cs` (`FormatParameterMarker`))

- Tests first:
  - `SqlDataSourceExecutor_Oracle_UsesColonMarkers` (verified bug at lines 306-332).
  - `SqlDataSourceExecutor_Oracle_OffsetAndLimitBoundToCorrectSlots` (offset 0 and limit 5 must return rows 1-5, not zero rows).
  - `SqlDataSourceExecutor_Oracle_CountQueryParameters`.
  - `NoHardcodedAtMarkers_OnGovernedPaths` (architecture test).
  - Oracle mask rendering never falls back to `gateway_hmac_sha256` (SEC-ADG-15).
- Acceptance: the GraphQL/data-source path executes on Oracle Free with the G6 visibility and mask oracles green.

**WP-F5 G6 Oracle on the real gateway path** (files `tests/Autheris.Tests.SqlCompilerGate/Execution/Oracle/**`, `tests/Autheris.Tests.Integration/OracleIntegrationTests.cs`)

- The existing `OracleIntegrationTests` are migrated to resolve `ISqlConnectionFactory`, `IDbSessionContextInitializer` and `ICompiledSqlBinder` from the real DI container. Direct `new OracleCommand(...)` usage in tests is limited to fixture setup (architecture test).
- The G6 suites (visibility, masks, DML check option, `MERGE`, limits, hostile corpus including `q'[...]'`, the empty-string cases and `TenantCaseCollision_IsIsolated`) run on Oracle Free 23ai (image pinned by digest).
- Acceptance: green. This is the precondition for listing Oracle as `Production` in the capability table.

**WP-F6 Oracle masks** (files `Ast/Generators/OracleDialectGenerator.cs` (mask emission), capability table `InDbHmac`)

- `DBMS_CRYPTO.MAC(..., DBMS_CRYPTO.HMAC_SH256, :key)` when the startup probe confirms the grant; otherwise HMAC rules degrade to `Redact` with a startup warning (SEC-ADG-15). Partial and redact masks follow the existing Oracle rendering.
- Tests first: `OracleHmac_WithGrant_MatchesReferenceHmac`, `OracleHmac_WithoutGrant_Redacts`.

**Dependency additions to §11.1:** `A1 --> F3`, `A2 --> F3`, `F4 --> F1` (F1 merges with or after F4), `F1 --> F2 --> F5`, `F3 --> F5`, `A6 --> F6`, and `F5 --> X1` (only if Oracle is to be Production at cutover; otherwise Oracle stays `Production (compiler)` and is not executable until F5 is green). **File conflicts:** `SqlDataSourceExecutor.cs` is also edited by D2 and D4 (order: D4, F4, D2). `SqlConnectionFactory.cs` and `GatewayStartupValidator.cs` are also edited by C3 (different sections; F1 and C3 must not run in parallel without a coordinated rebase).

### 16.7 Residual risk: text rendered from typed IR for out-of-scope consumers (OQ-4)

The §7.3 consumers (GraphQL `TreeSqlCompiler` and `SqlDataSourceExecutor`, lakehouse executors, `StreamRlsPolicyEnforcer`, procedures, `GatewayExecutionService.FilterRows`, `DuckDbOlapEndpoints`, `FederatedStagingService`, consent caches) keep consuming text after X1. Rendering from one typed source removes **drift between producers**. It does not remove these risks:

| Risk | Rating after mitigation | Mitigation in this track | Closed by |
|---|---|---|---|
| A renderer defect (precedence, parameter naming, dialect markers) widens the filter for every consumer at once | Low (G10 and M-3) | SEC-ADG-02: full parenthesization, reserved namespace, duplicate-name fail closed, G10 0 `looser`, revert set includes D1-D3 | Follow-up track |
| A consumer interprets the text differently from the database (the in-memory `FilterRows` and streaming evaluators use their own type coercion, see threat model E-5/E-6; the lakehouse executors evaluate tenant predicates themselves) | Medium | G10 includes the in-memory evaluators. Consumers that cannot evaluate a construct must reject the table (fail closed), never skip the predicate. | Follow-up track: typed IR evaluation per consumer |
| A consumer concatenates the rendered text with its own text and its own parameters (`SqlDataSourceExecutor` adds tenant, arguments, `$filter`, limit and offset) | Low | Reserved namespace plus the M-7 architecture test for duplicates. SEC-ADG-03 fixes the Oracle markers. | — |
| Correlated filters depend on the consumer's alias (`autheris_target`) | Low | The renderer takes the alias as a typed argument. A consumer that does not declare it fails closed (existing `RowFilterAliases.ReferencesTarget` check). | — |
| Masks rendered as text for GraphQL (`SqlDataSourceExecutor` mask call sites) | Low | WP-D2 renders from `MaskSpec`. The `InDbHmac` capability applies (SEC-ADG-15). | — |

**Accepted residual risks for this track (owner: stakeholder, revisit in the follow-up track):** the error-based side channel (SEC-ADG-16, one bit per query, pre-existing); the unique-key existence side channel (R-12, sanitized); the gap between the Spark proxy and Databricks SQL (R-7, Databricks held until G9); the consumer-interpretation differential (above, Medium until the follow-up track).

### 16.8 Security test criteria per work package (tests first, Phase 4)

These lists add to the "Tests first" lists in §11. A work package is not done until its list is green. Test names are binding where the finding requires them; others may be renamed with the same intent.

**WP-A1 Capability table**
- `CapabilityTable_TenantPredicateTemplate_IsBinaryExact_PerDialect` (SEC-ADG-04).
- `CapabilityTable_InDbHmac_PerDialect` (SEC-ADG-15).
- `CapabilityTable_AllowedTableFunctions_EmptyByDefault` (SEC-ADG-22).
- `BindExpressionTemplates_AreCompileTimeConstants_OnePlaceholder_ClosedFormatSet`.
- `UnknownDialect_Throws`; `Snowflake_NotProduction`; `Databricks_NotProduction_UntilG9Flag`; `Oracle_NotExecutable_UntilF5Flag`.

**WP-A2 Emit, binder, checker**
- `Checker_RejectsQuoteCommentSemicolonDollarQuote_OutsideDelimitedIdentifiers` per dialect, including Oracle `q'`/`nq'` and Databricks `${`.
- `Checker_AllowsSemicolon_OnlyAtRegisteredFinalPosition_SqlServerMerge` (SEC-ADG-08).
- `Binder_EveryMarkerMapsToExactlyOneParameter` (INV-13), as an FsCheck property over generated ASTs.
- `Binder_ClientNamedParameter_NeverAliasesInternalMarker` with the names `@p1`, `:p1`, `$1`, `?1`, `@__autheris_tenant`, `@gql_limit` and `@__gql_x` (SEC-ADG-19).
- `ClientParameterName_ReservedPrefix_Rejected`.
- `AllVisitors_GuardExecutionStack` (reflection plus a 3,000-deep `OR` chain: typed rejection, no crash) (SEC-ADG-05).
- `GenerateInlineForInternalPlan_HostileCorpus_NoInjection` and `GenerateInlineForInternalPlan_SingleCaller` (SEC-ADG-21).

**WP-A3 Literal parameterization**
- `BindType_FollowsCatalogColumnType_ForComparisons` (SEC-ADG-16).
- `PolicyLike_EmitsExplicitEscape_AndEscapesValue_PerDialect` (SEC-ADG-18).
- `NoLiteralTokensInOutput` (G5-d) over the hostile corpus.

**WP-A4 Pass order, opaque node, verifier**
- `CoverageVerifier_Throws_WhenPredicateInJoinOnInsteadOfDerivedWhere` (INV-2).
- `CoverageVerifier_PolicySubqueryWithoutTenantPredicate_Throws` (SEC-ADG-11).
- `CoverageVerifier_TableFunctionSource_Throws` (SEC-ADG-22).
- `Emitter_PhysicalTables_AreSchemaQualifiedCanonical` and `Emitter_CteReference_EqualsCteDefinitionSymbol` (INV-11).
- `QuotedCteVsUnquotedPhysical_CaseVariants_AlwaysSecured` on PostgreSQL, Oracle and SQL Server (SEC-ADG-07).
- `RecursiveCteSelfReference_OnlyInsideWithRecursive`.
- `DmlTarget_IsCteName_Throws` (SEC-ADG-07).
- `SystemQueryPassthrough_Rejected`.

**WP-A5 Policy IR and parser**
- `PolicyParser_RejectsNondeterministicAndSessionFunctions`, `PolicyParser_RejectsWindowFunctions`, `PolicyParser_MaxNodes256`.
- `PolicyParser_CorrelatedSubquery_GetsTenantPredicate_Depth1` and `PolicyParser_NestedPolicySubquery_Rejected` (SEC-ADG-11).
- `PolicyParseCache_KeyIncludesFunctionAllowListAndCompilerVersion` and `PolicyParseCache_NegativeEntryShortTtl` (SEC-ADG-20).
- `PolicyPredicate_ExposesReferencedColumns` (SEC-ADG-06).
- `HostilePolicyValues_AreBound_AllDialects` (INV-5), including `''` on Oracle (SEC-ADG-17).

**WP-A6 Masks**
- `MaskedColumn_NeverInClear_InWhereOrderGroupWindowJoinAggregate` (masked value only, or rejection; SQLite execution).
- `Hmac_DegradesToRedact_WhenInDbHmacFalse` (SEC-ADG-15).
- `HmacKey_NeverInLogsSpansAuditOrExceptions` (INV-16).

**WP-A7 DML and MERGE**
- `Update_AssignPolicyReferencedColumn_Rejected`, `MergeUpdate_AssignPolicyReferencedColumn_Rejected` and `MergeInsert_ConsentFilteredTable_Rejected` (SEC-ADG-06).
- `Merge_CorrelatedTargetRowFilter_Rejected`.
- `Merge_MaskedColumnInOnOrWhen_Rejected` and `Merge_MaskedColumnInAssignmentRhs_Rejected` (SEC-ADG-08).
- `Grammar_HasNoNotMatchedBySource` and `MergeClauseTypes_AreClosed` (architecture).
- `Merge_OtherTenantRowWithSameKey_IsNeverMatched_AndNeverDeleted` (execution on every `SupportsMerge` engine).
- `DmlConstraintViolation_ErrorHasNoKeyValueOrConstraintName` (R-12).
- `UpdateFrom_And_Returning_AreParseRejected` (the grammar has neither; this pins it).
- `InsertSelect_SourceMasked_InsertsMaskedValueOnly`.

**WP-A8 Compile API**
- `CompileRequest_EveryPropertyChangesCacheKey` (reflection over `CompileRequest` and `GovernancePolicy`, SEC-ADG-01).
- `CompiledSqlTemplate_HasNoTenantPolicyOrMaskValues` (SEC-ADG-01).
- `Compile_CancelledInEveryPass_NoCacheEntry_NoPartialSql`.
- `Compile_TimeBudget_TypedRejection`.
- `Compile_EmittedLengthAndExpansionFactorLimits`.
- `TypedRejections_MessagesContainNoHostileCorpusMarkers` (SEC-ADG-23).
- `AuditRecord_HasCompiledDigest_NoValues`.

**WP-B3 Differential corpus**
- `Classifier_DetectsLooserMask` (a mutant that unmasks the wildcard expansion is classified `ast-looser-mask`).
- `Classifier_DetectsLooserWrite`.
- `AcceptedDifferences_SchemaRejectsLooser` (M-1, M-6).

**WP-B4 Property and fuzz**
- The independent policy oracle (M-2) as the G5-c' oracle.
- Generators include tenants that differ only by case, `''` values, LIKE metacharacters, `${`, quoted CTE names that shadow physical names, 3,000-deep chains and 256 or more table references.

**WP-B5 Execution**
- The M-7 end-to-end tests per entry point and dialect.
- The M-8 cache-poisoning suite.
- `TenantCaseCollision_IsIsolated` on every engine (SEC-ADG-04).
- `ErrorSideChannel_ResponsesAreGeneric` (SEC-ADG-16).
- G10 consumer parity (M-9).

**WP-B6 Security suite port**
- `SecurityRegressionCatalog` does **not** exist yet (no file under `tests/` references it). B6 creates it, seeded by a script that extracts every `SEC-`, `SQ-`, `SQL-`, `RR-` and `SR15-` ID from `tests/**/*.cs` (77 distinct IDs at `818ab14`). New Phase 3 IDs `SEC-ADG-01..29` are added to the catalog with their test names.

**WP-C1 Databricks generator**
- `Databricks_IdentifierWithDollarOrBrace_Rejected` and `Databricks_TokenGuard_VariableSubstitution` (SEC-ADG-10).
- `Databricks_TenantPredicate_UsesBinaryCollation` (verify on the proxy).
- `Databricks_NeverEmitsStringLiteral` over the hostile corpus.

**WP-C3 Databricks REST connector**
- `StatementId_Invalid_Rejected`, `ChunkLink_Absolute_Rejected`, `ChunkLink_OtherStatement_Rejected`, `ExternalLinks_Rejected` and `Redirect_NotFollowed` (SEC-ADG-09).
- `ResponseBuffer_Bounded`, `TokenEndpoint_DerivedFromHost`, `AuthorizationHeader_Redacted_InLogs` and `Pat_RejectedOutsideDevelopment`.
- `Cancellation_SendsCancel`.
- `DatabricksSession_ExplicitNoSessionStateBranch` (SEC-ADG-14).

**WP-C4 Spark proxy**
- The proxy runs with `spark.sql.variable.substitute=true` and the image is pinned by digest (SEC-ADG-10, -24).

**WP-C5 Live job**
- `LiveJob_RunsOnlyInProtectedEnvironment` (a workflow lint in CI that asserts `environment:` with required reviewers and no `pull_request_target` from forks) (SEC-ADG-09).

**WP-D1 Typed row filters**
- `AdvancedRlsFilterGenerator_ProducesTypedPredicate_NoInlineLiterals` and `NoInlineLiteralRendering_OnGovernedPath` (architecture) (SEC-ADG-12).
- `Renderer_FullyParenthesized_ReservedNamespace_PerDialectMarkers` and `Consumer_DuplicateParameterName_FailsClosed` (SEC-ADG-02).
- `CombinedRowFilterSql_IsDerivedFromIRowPolicy_NoSetter` and `StringOnlyDecision_DeniedOnCompilerPath` (SEC-ADG-25).
- `PolicyPredicateLint_ReportsUnparsable` (SEC-ADG-26).

**WP-D2 Typed masks**
- `RenderedMask_UsesDialectMarkers`.
- `Hmac_InDbHmacFalse_Redacts`.

**WP-D3 SqlFilterCompiler**
- `VirtualFilterSubqueryTables_GetTenantPredicate` (SEC-ADG-11; this replaces `predicate: _ => false`).

**WP-D4 Fail-closed fixes**
- `DbSessionContextInitializer_UnknownProvider_Throws` and `DbSessionContextInitializer_UnknownDialect_Throws` (SEC-ADG-14).
- `NoDialectDefaultFallback_InSrc` (grep-based architecture test for `_ => DatabaseDialect.` and `_ => TargetSqlDialect.` on governed paths).

**WP-E2/E3 CI**
- The workflow implements M-3, M-4, M-5 and M-12.
- `dotnet list package --vulnerable --include-transitive` gate.
- The Trivy scan of the Spark image (SEC-ADG-24).

**WP-F1..F6 Oracle:** see §16.6.

**WP-X1 Cutover**
- The M-11 combined revert rehearsal.
- `StartupFails_WhenSqlRewriterEngineConfigured` (SEC-ADG-28).
- The M-8 suite against the `CompiledSqlTemplate` cache.
- `PlanCache_NoEmptyRawSqlOverloadOnGovernedPath`.

### 16.9 Changes to the work packages and gate (summary)

- §9.3: **G10 Consumer parity** (M-9) is added as a gate row; amend G4 (M-1, M-6), G5 (oracle M-2), G6 (M-7, M-8, M-10) and G7 (M-3, M-12). The cutover precondition is replaced by the one in §16.5.
- §11: add **Stream F** (WP-F1..F6). WP-D1 gains `AdvancedRlsFilterGenerator`, `RlsFilterGenerator` and the Casbin correlated subquery (SEC-ADG-12). WP-D3 gains the policy-subquery tenant predicate (SEC-ADG-11). WP-D4 gains the session-initializer fail-closed fixes (SEC-ADG-14). WP-A2 and WP-A8 gain the stack, budget and output limits (SEC-ADG-05) and the template cache (SEC-ADG-01).
- §12.2: the revert set is `{X1, D3, D2, D1}` (SEC-ADG-02). The runbook triggers are in §16.4 item 10.
- §5: Oracle is `Production (compiler)` until WP-F5 is green, then `Production`.
- §15: OQ-1 is resolved by SD-8. OQ-2 stays as decided (Databricks held until G9, provider of the workspace still open).

### 16.10 Decisions needed from the stakeholder

| ID | Decision | Blocks | Security recommendation |
|---|---|---|---|
| B-1 | **Tenant id case.** Either canonicalize tenant ids (for example lower case) and reject case-insensitive duplicates in the tenant registry, or keep mixed case and rely only on the binary tenant template (SEC-ADG-04). | WP-A1, WP-D1 (template design), WP-B5 | Do both: the binary template is mandatory regardless, and registry uniqueness is defense in depth. |
| B-2 | **HMAC on Databricks and on Oracle without a `DBMS_CRYPTO` grant:** degrade to `Redact` (recommended) or require the DBA grant (Oracle only). | WP-A6, WP-F6, WP-C1 | Redact by default; Oracle uses HMAC when the grant probe succeeds. |
| B-3 | **Large consent IN lists over the bind or IN-list limit:** deny (current plan) or array binding (SEC-ADG-27). | WP-A1, WP-D1 | Security-neutral. Array binding avoids an availability regression; deny is simpler. |
| B-4 | **Databricks live workspace provider** (OQ-2, still open). | G9, Databricks production listing | Unchanged: held until the first green G9. |
| B-5 | **Oracle production prerequisites** from the DBA side: TCPS, a non-privileged runtime account, schema-qualified catalog entries and, optionally, the `DBMS_CRYPTO` grant. | WP-F5 | Required before Oracle is listed as `Production`. |

### 16.11 Stakeholder decisions B-1, B-2, B-3 (resolved before Phase 4)

| ID | Decision (binding) | Effect |
|---|---|---|
| B-1 | **Tenant comparison is always exact (binary, per dialect).** New tenants whose ID collides case-insensitively with an existing tenant are rejected. Existing IDs are **not** rewritten. A startup check reports existing case-insensitive collisions and **fail-closes the colliding tenants** (all their requests are denied until an operator resolves the collision). | The binary `TenantPredicateTemplate` (SEC-ADG-04) is mandatory on every dialect (A1). Registry uniqueness is enforced on tenant creation (Stream D/application scope; not Stream A). Startup collision check and fail-close are tracked outside Stream A. Test criteria added below. |
| B-2 | **Where HMAC masking is unavailable (Databricks; Oracle without the `DBMS_CRYPTO` grant; DuckDB), the mask degrades to Redact.** Gateway-side HMAC stays rejected. | `DialectCapabilities.InDbHmac` (A1) drives `MaskExpression` emission (A6): `Hmac` with `InDbHmac = false` is emitted as `Redact`. No fallback to another dialect's HMAC function. |
| B-3 | **Consent IN lists that exceed the bind limit or the IN-list limit are denied** with a typed error (`SqlLimitExceededException`, Kind `BindParameters` or `InListItems`) and a metric (`autheris.sql.limit_rejected`). Array/TVP binding is a **deferred follow-up work package** (WP-A9, not scheduled in this track). | SEC-ADG-27 default (reject) applies. No array-bound `BindExpressionTemplate` is built. AP-11 stays: never compact. |

Affected work packages and test criteria (additions to §11 and §16.8):

- **WP-A1:** `CapabilityTable_TenantPredicateTemplate_IsBinaryExact_PerDialect` is mandatory (B-1). `CapabilityTable_InDbHmac_PerDialect` asserts PostgreSQL, SQL Server and SQLite `true`; Oracle `false` by default (becomes `true` only through the probe in WP-F6); Databricks and DuckDB `false` (B-2). `Limit_OverInList_DeniedWithTypedError_NoArrayBinding` (B-3).
- **WP-A2/A8:** the limit counter `autheris.sql.limit_rejected{dialect,kind}` is incremented for every IN-list and bind rejection (B-3).
- **WP-A6:** `Hmac_DegradesToRedact_WhenInDbHmacFalse` is required for Databricks, DuckDB and Oracle-without-grant (B-2).
- **WP-D1/D2, WP-F6, WP-B5 (other streams):** `TenantCaseCollision_IsIsolated` runs with tenants `acme` and `ACME`; registry rejects a new case-insensitive duplicate; startup check fail-closes existing collisions without rewriting IDs; consent IN-list over the limit is denied, not chunked, not compacted (B-1, B-3). Oracle HMAC without grant redacts (B-2).
- **§16.10:** B-1, B-2 and B-3 are resolved by this section. B-4 and B-5 remain open.

**Phase 3 sign-off:** granted for Phase 4 to start on Streams A-F, subject to the mandatory mitigations above. The final sign-off for WP-X1 is given on the cutover PR, against the §16.5 precondition and the final `accepted-differences.json`.

---

## 17. Changelog

- 2026-10-10: Added §16.11 recording stakeholder decisions B-1 (exact tenant comparison, reject new case-colliding tenants, fail-close existing collisions, no ID rewrite), B-2 (HMAC unavailable degrades to Redact) and B-3 (over-limit consent IN lists denied, array binding deferred as WP-A9).
- 2026-10-10: Added §16 "Phase 3 Security Review" (`csharp-security-expert`): findings SEC-ADG-01..29, hardened invariants (INV-9 retired; INV-11..INV-17 added), STRIDE mapping, answers to the §14 hand-off, minimum CI gate additions M-1..M-12 and new gate G10, Stream F for the Oracle runtime (SD-8), security test criteria per work package, residual-risk assessment for the §7.3 string consumers, and stakeholder decisions B-1..B-5. Status set to Phase 3 delivered; next milestone Phase 4 TDD implementation.
- 2026-10-10: Added §3.6 "Reference design: Trino's own JDBC pushdown generator" (per user input): adopted `PreparedQuery`/`QueryParameter`, bind-expression templates, the declarative function-rewrite DSL and capability flags; rejected domain compaction (AP-11).
- 2026-10-10: Initial English implementation plan (Phase 2). Supersedes the German 2026-10-06 plan. Incorporates stakeholder decisions SD-1..SD-7 (single path, pre-merge evidence gate, bind-everything, typed policy IR, DML and `MERGE` in the first cut, Snowflake experimental, ADR-017 amendment, Databricks production dialect). Defines architecture, interfaces, capability table, removal list, consumer migration, gate G1-G9, work packages in streams A-E plus the cutover, risks, rollback and the Phase 3 hand-off.

---

## 18. Implementation Log - Stream A (compiler core, DQL first)

### 18.1 Re-scope and branch stack (stakeholder decisions during Phase 4)

All DQL (SELECT) for every dialect comes first. DML and MERGE (WP-A7) start only after all DQL branches are done (separate step, branch `feat/ast-dml`). Legacy stays the default; there is no cutover (WP-X1 is not part of this work). DML, MERGE, non-SELECT statements and every dialect without a capability entry fail closed through `ISqlEngine.Compile` with the typed `SqlCompileNotSupportedException`.

Priority order and stacked branches (each branch is based on the head of the previous one):

| # | Branch | Content | Status |
|---|---|---|---|
| 1 | `feat/ast-mssql-select` | Core (A1-A6, A8 for SELECT) plus SQL Server | see 18.2 |
| 2 | `feat/ast-duckdb-select` | DuckDB SELECT | see 18.3 |
| 3 | `feat/ast-postgres-select` | PostgreSQL SELECT | see 18.4 |
| 4 | `feat/ast-databricks-select` | Databricks SELECT | planned |
| 5 | `feat/ast-oracle-select` | Oracle SELECT, based on the PostgreSQL head plus `feat/ast-failclosed-fixes` (WP-D4), WP-F1..F4 | planned |
| later | `feat/ast-dml` | A7 (DML, MERGE) for all dialects | deferred; not started in this work |

### 18.2 Branch `feat/ast-mssql-select`

Work packages (one commit each): A1 capability table (SQL Server entry only), A2 `CompiledSql`, parameter accounting, `SqlServerCompiledSqlBinder`, `EmittedSqlInvariantChecker`, A3 literal parameterization, A4 opaque `SecurityPredicateExpression` plus `SecurityCoverageVerifier`, A5 typed policy IR, `PolicyExpressionParser` with cache, typed injection in `AstSecurityVisitor`, A6 `MaskExpression`, A8 `ISqlEngine.Compile`, `GovernedSqlCompiler`, value-free `CompiledSqlTemplateCache`, SQL Server Testcontainer execution tests.

SEC-ADG coverage on this branch: -01 (value-free template, canonical request key, full-material compare, per-table dependency revalidation and value rebinding), -04 (binary-exact tenant predicate incl. zero-padding fix found by the container test), -05 (stack guards in all rewriters, verifier and generators; compile budget; AstDepth, SecuredTableReferences, EmittedSqlLength, expansion factor limits), -06/-07/-08 only the SELECT-relevant parts (-07 canonical schema-qualified names and symbol-based CTE handling; -06 `ReferencedColumns` exposed, DML rejection is A7), -10/-12/-14/-17/-22 not applicable or covered elsewhere, -11 (tenant predicate in policy subqueries, depth 1), -15 (HMAC degrades to Redact when `InDbHmac = false`), -18 (explicit LIKE ESCAPE and escaped values), -19 (generated markers, reserved client-name prefixes, positional parameters rejected), -20 (parse cache key, negative entries, per-partition share), -22 (table functions rejected by the builder), -23 (typed exceptions without values, `CompiledSqlDigest`, bound values redacted from `ToString`), INV-11 (partial: schema-qualified canonical names; session `search_path` belongs to the runtime streams), INV-13, INV-15, INV-16.

Deviations from the plan text (all fail-closed or stricter):

- `GenerateGovernedSql` keeps its legacy pass order; the new order (simplify the user tree before injection) lives only in `GovernedSqlCompiler`, so legacy tests and consumers are untouched.
- `PolicyParameterExpression` has `Origin` and `IsLikePattern`; `MaskExpression` has `DataType`; `MaskArguments` has `HmacKeyOuter` (SQL Server HMAC uses two key pads); `BoundParameter` has `SourceName`; `PolicyValue`, `BoundParameter` and `TenantBinding` override `ToString` to hide values.
- `ITableCatalog`/`TableCatalogEntry` supply canonical names, columns, data types, tenant column and version. A table that is not in the catalog is rejected (INV-11).
- Admin policy literals and `:name` markers become policy parameters; every literal inside an injected predicate is rejected by the verifier (INV-5), except the canonical `1 = 0` / `1 = 1`.
- Positional `?` client parameters are rejected (only named parameters through `__param_<name>` are supported on the compiler path).
- Expansion factor is measured against `max(input length, 64)`; `CompileRequest` has the extra properties `CompileTimeout` and `MaxExpansionFactor`; the enum for the subquery strategy is `GovernedSubqueryStrategy` (the Domain already has `RowFilterSubqueryStrategy`).
- SEC-ADG-16 item 2 (bind types from catalog column types) is not implemented; literals use their literal type (partial, deferred).
- Function mapping is limited to `length`, `char_length`, `ceil` and `strpos` for SQL Server; other functions pass through unchanged.
- The architecture tests are placed in `tests/TrinoSqlEngine.Tests` (`NoRewriterDescendsIntoSecurityPredicate`, `AllowExperimentalDialect_OnlyInTests`).
- `GatewayStartupValidator` checks for existing tenant ID case collisions (decision B-1), the audit wiring of `CompiledSqlDigest`, and the Application consumers are cutover work and not part of this branch.

Deferred to separate branches: `feat/ast-dml` (A7, DML and MERGE), then X1 after all dialects.

### 18.3 Branch `feat/ast-duckdb-select` (on top of `feat/ast-mssql-select`)

Scope: capability entry (65,535 bind parameters, probed in process; `$n` markers; `"` quoting; `LIMIT/OFFSET`; no `WITH TIES`; `InDbHmac = false`), `DuckDbCompiledSqlBinder` (parameters named `1..n`, embedded NUL rejected because DuckDB.NET passes C strings), generator opt-in to bound mode (structural LIMIT/OFFSET, bound typed and interval literals, reviewed `DATE_TRUNC` unit fragment), typed masks (nullify, redact, constant, partial, geo jitter; HMAC degrades to Redact per B-2), exact tenant comparison `col = t AND encode(CAST(col AS varchar)) = encode(CAST(t AS varchar))` (BLOB comparison is byte-exact, tested against a `COLLATE NOCASE` column), in-process execution tests (RLS visibility, collision, policy subquery, masks, hostile values, cache rebinding, bind-limit probe).

Core changes that stay dialect-neutral: `EmittedSqlInvariantChecker` takes delimiter characters and marker style from the capability table (all five marker styles are lexed); the binder logic moved into `DbCommandCompiledSqlBinder` with per-provider hooks; the compile-budget mapping no longer treats ANTLR `ParseCanceledException` as a timeout.

Federation staging (`CrossSourcePlanner`) keeps the legacy string path (`GenerateSql`), which is unchanged; the full `Autheris.Tests.Unit` suite covers it.

### 18.4 Branch `feat/ast-postgres-select` (on top of `feat/ast-duckdb-select`)

Scope: capability entry (65,535 binds, 63-byte identifier limit measured in bytes so over-long names are rejected instead of silently truncated, `$n` markers, `InDbHmac = true` through pgcrypto, no `TRY_CAST`), `PostgreSqlCompiledSqlBinder` (positional unnamed Npgsql parameters in ordinal order, `timestamp` through `DbType.DateTime2`, NUL rejected), bound-literal emission (structural pagination including `WITH TIES`, reviewed constant fragments `INTERVAL '1 day'`, `DATE_TRUNC('unit', ...)`, `'sha256'`, `'hex'`), typed masks (HMAC through `ENCODE(HMAC(..., 'sha256'), 'hex')` with a bound key; partial masks clamp the counts with `GREATEST(n, 0)`, so a negative count cannot expose the value; the DuckDB partial mask received the same clamp), byte-exact tenant comparison `col = t AND textsend(CAST(col AS text)) = textsend(CAST(t AS text))` (SEC-ADG-04), schema-qualified canonical names (SEC-ADG-07).

Execution evidence on `postgres:16-alpine` (Testcontainers): tenant isolation on a `citext` column and on a non-deterministic ICU collation (a plain `=` returns both `acme` and `ACME`), consent filters, policy-subquery tenant predicate, a hostile `search_path` plus a `pg_temp` decoy table (the secured query still reads `public.orders`), plan-cache rebinding per tenant, hostile tenant and user values, redact/partial/HMAC (matches a reference HMAC-SHA256)/jitter masks, bind-limit probe at 65,535 parameters.

Semantic notes: PostgreSQL folds unquoted identifiers to lower case, catalog columns are emitted exactly as cataloged, so a mixed-case catalog column must be quoted by the user (Trino semantics differ); `standard_conforming_strings` has no effect because no string literal is emitted. The unqualified table alias keeps the user's folding (unquoted names stay unquoted). Session `search_path` pinning (INV-14) belongs to the runtime session initializer and is not part of this branch; the compiler does not depend on it.
