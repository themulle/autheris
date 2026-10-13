# TrinoSqlEngine: SQL Parser, Query Analyzer and Governed AST Compiler for .NET 10

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![ANTLR 4](https://img.shields.io/badge/ANTLR-4.13.1-C9252B.svg)](https://www.antlr.org/)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)

A SQL library for multi-tenant data gateways, written in C# for .NET 10. It parses Trino SQL with an ANTLR4 grammar ported from the
official [Trino](https://github.com/trinodb/trino) `SqlBase.g4`, analyzes statements, and compiles a user statement into
**governed, parameterized SQL for a target database dialect**. The compiler injects tenant isolation, row policies and column masks
as typed AST nodes, proves that every table access is covered, and binds every value as a parameter.

There is exactly one way to produce governed SQL: `ISqlEngine.Compile`. There is no text rewriter and no runtime switch.

---

## Capabilities

- **Two-stage parsing** (`FastSqlEngine.Parse`): an SLL fast path with a reusable `BailErrorStrategy` and an LL fallback on
  ambiguity, a pooled parser, a shared ATN/DFA cache, a case-insensitive zero-copy character stream, and a dedicated parser thread
  with a configurable stack size and time budget.
- **Query analysis** (`SqlQueryAnalyzer`, `FastSqlEngine.Analyze`): statement type, referenced physical tables (CTE names are not
  tables), projected columns, join count, subquery depth and explicit limits.
- **Governed compilation** (`FastSqlEngine.Compile`): token guards, parse, typed AST, simplification of the user tree, typed
  security injection, a coverage proof, dialect capability validation and emission with every value bound.
- **Typed policy input**: tenant binding, row policies, column masks, table catalog and DML guards are values of the request, never
  text. Policy and mask values travel as parameters.
- **Dialect generators** for PostgreSQL, SQL Server, SQLite, Oracle, DuckDB and Databricks, driven by a declarative capability table
  (`DialectCapabilityTable`), plus ANSI and Snowflake generators that only render inline text.
- **Binders and a checked DML executor** (`CompiledSqlBinders`, `CheckedDmlExecutor`): the only code that assigns
  `DbCommand.CommandText` for governed SQL, with a row-count check for INSERT statements that have check-option semantics.
- **Hardened limits**: query length, nesting depth, parse tree depth, parse time budget, parser concurrency, compile time budget,
  output expansion factor, bind parameter count per dialect, `IN` list size and identifier length.
- **Function policy** (`SqlFunctionPolicy`, `SqlFunctionAllowlists`): a denylist of high-risk functions that always wins, plus a
  per-dialect function map; a function without an emission rule is rejected.
- **Lexer-safe parameter extraction** (`SqlParameterExtractor`).

---

## The compile pipeline

```
Raw SQL + CompileRequest
        |
  1. validate the request (limits, mandatory token guards of the dialect)
  2. template cache lookup (value-free, verified templates)     -> hit: rebind values, return
  3. token guards + parse (SLL, then LL)
  4. classify the statement, check that the request and the dialect permit it
  5. build the typed AST and validate it
  6. simplify the USER tree only (injected predicates do not exist yet)
  7. typed security injection (AstSecurityVisitor)
  8. coverage proof (SecurityCoverageVerifier does not trust the injector)
  9. dialect capability validation
 10. emit parameterized SQL (EmittedSqlInvariantChecker verifies the text)
 11. output budget check; cache the verified template (never request values)
        |
   CompiledSql { Sql, Parameters, Dialect, StatementClass, AppliedPredicates, CompilerVersion }
```

Any failure is fail closed: the compiler throws a typed exception and emits nothing. A dialect or statement class without a
governed path throws `SqlCompileNotSupportedException`.

### Injection model

- Every table reference becomes a derived table: `(SELECT <cataloged columns> FROM <physical table> WHERE <tenant> AND <policy>)`.
  Only cataloged columns are projected, so non-cataloged and system columns cannot be reached.
- The tenant predicate is always applied when the catalog declares a tenant column, and compares **binary-exact** per dialect
  (`TenantComparisonStyle`), independent of the column collation.
- Row policy predicates are `PolicyPredicate` ASTs. They contain `PolicyParameterExpression` nodes, never values.
- Column masks are `MaskExpression` nodes (`Nullify`, `Redact`, `PartialMask`, `Hmac`, `GeoJitter`, `Constant`) that the dialect
  generator renders; mask arguments are bound parameters. A masked column in a predicate, `ORDER BY` or join condition is rejected.
- Every injected predicate is wrapped in a `SecurityPredicateExpression` that the generator always parenthesizes.
- CTE scoping follows the ANSI exit-timing rule, CTE names compare by ordinal bytes, and a recursive CTE with the name of a catalog
  table is rejected.
- DML (`INSERT`, `UPDATE`, `DELETE`, `MERGE`) compiles only when the request allows the statement class and the dialect lists it. The
  guards (`DmlGuardOptions.Strict` is the only accepted value) require a filtered `WHERE`, forbid tenant column changes, check the
  tenant on inserts, reject masked columns and whole-row references, and reject assignments to policy columns.

### Binding

Every literal of the user statement, the tenant value, policy values and mask arguments are `BoundParameter` entries of the
`CompiledSql`. Client named parameters (`ParameterOrigin.ClientNamed`) carry no value; the binder resolves them per request.
Binders derive from `DbCommandCompiledSqlBinder`, are created through `CompiledSqlBinders.For(dialect)`, and are the only code that
sets `CommandText`. A statement with a row-count check is refused by the binders and runs only through `CheckedDmlExecutor`.

### Template cache

`FastSqlEngine.CompileCache` (`CompiledSqlTemplateCache`) holds verified, value-free compile templates. A hit re-validates the
dependencies of the template and rebinds the current values, so two principals with different consent never share bound values, and
the cache holds nothing that depends on a request value.

---

## Usage

### Parse and inspect

```csharp
using TrinoSqlEngine;

var engine = new FastSqlEngine();
var (tree, tokens) = engine.Parse("SELECT id, name FROM users WHERE active = true".AsMemory());
var (exprTree, exprTokens) = engine.ParseExpression("price * 1.19 > 100".AsMemory());
```

### Analyze

```csharp
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;

SqlQueryMetadata meta = new FastSqlEngine().Analyze(
    "WITH r AS (SELECT region, SUM(amount) AS total FROM orders GROUP BY region) SELECT c.name, r.total FROM customers c JOIN r ON c.region = r.region LIMIT 50".AsMemory());

// meta.StatementType is Select, meta.JoinCount is 1, meta.ExplicitLimitValue is 50.
// meta.ReferencedTables lists the physical tables orders and customers; the CTE r is not a table.
```

### Compile governed SQL

```csharp
using System.Collections.Immutable;
using System.Threading;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;

var orders = new TableIdentity("dbo", "Orders");
var catalog = new InMemoryTableCatalog(
[
    new TableCatalogEntry(orders, ImmutableArray.Create(
        new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("Status", "nvarchar(20)")),
        "TenantId", 1)
], "dbo");

var request = new CompileRequest
{
    TargetDialect = TargetSqlDialect.SqlServer,
    TokenGuards = SqlTokenSecurityOptions.Strict,
    Policy = new GovernancePolicy
    {
        RowFilters = myRowPolicies,   // IPolicyPredicateProvider: typed predicates per table, or "no policy"
        Masks = myMasks,              // IColumnMaskProvider: typed masks per column
        Catalog = catalog,
        Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
    },
    EnforcedMaxRows = 1000
};

CompiledSql compiled = new FastSqlEngine().Compile("SELECT id FROM orders WHERE status = 'open'".AsMemory(), request, CancellationToken.None);

// compiled.Sql         the text with parameter markers only (no tenant, policy or literal value)
// compiled.Parameters  every value, with its origin (QueryLiteral, Tenant, Policy, Mask, ClientNamed) and type
using var command = connection.CreateCommand();
CompiledSqlBinders.For(compiled.Dialect).Bind(command, compiled, new Dictionary<string, object?>());
```

### Row policies and masks

A row policy is a `PolicyPredicate`: an expression over the columns of the table whose values are `PolicyParameterExpression` nodes,
plus a dictionary of `PolicyValue`. A correlated predicate refers to the target table through the reserved alias
`RowFilterAliases.Target` (`autheris_target`). `PolicyExpressionParser` parses administrator text in Trino syntax into a typed
predicate: literals become parameters, and any construct that would embed a value or a clock in a cached shape is rejected.

### DML

```csharp
var dmlRequest = request with { Statements = StatementPermissions.Update };
var update = engine.Compile("UPDATE orders SET status = 'closed' WHERE id = 7".AsMemory(), dmlRequest, CancellationToken.None);
```

A statement with `compiled.RequiresRowCountCheck` (an INSERT that carries the check-option semantics of the dialect) runs through
`CompiledSqlBinders.CheckedExecutorFor(dialect)`, which compares the affected row count with the expected count inside a transaction
and rolls back on a difference.

### Lexer-safe parameter extraction

```csharp
using TrinoSqlEngine.Analysis;

foreach (var p in SqlParameterExtractor.ExtractTokens("SELECT * FROM orders WHERE customer_id = @custId AND total > ?"))
{
    Console.WriteLine($"{p.Name} ({p.Token})");
}
```

---

## Dialects

The capability table (`DialectCapabilityTable.Default`) holds, per dialect and with sources: quoting, parameter marker style,
pagination, boolean representation, tenant comparison, bind parameter limit, identifier length, `IN` list limit, the function map and
the supported DML classes. A dialect without an entry cannot be compiled.

| Dialect | Tier |
| :--- | :--- |
| SQL Server | Production |
| PostgreSQL | Production |
| SQLite | Production |
| Oracle | Production |
| DuckDB | Production |
| Databricks | Experimental |

A tier flip is a security decision, because it admits a dialect. An experimental dialect is rejected unless a test sets
`CompileRequest.AllowExperimentalDialect`. ANSI and Snowflake generators render inline text only (policy rendering and tests); they have
no capability entry and are not compilable.

---

## Configuration

### `CompileRequest`

| Property | Default | Description |
| :--- | :---: | :--- |
| `TargetDialect` | required | The dialect to emit. There is no default; a missing or unsupported dialect fails closed. |
| `Policy` | required | `GovernancePolicy`: `RowFilters`, `Masks`, `Catalog`, `Tenant`, `Dml`, `TablesWithConsentRowFilter`, `TablesWithMaskedColumns`. |
| `TokenGuards` | required | `SqlTokenSecurityOptions` applied before parsing. The mandatory guards of the dialect are forced on. |
| `Statements` | `ReadOnly` | Statement classes permitted besides `SELECT`. |
| `EnforcedMaxRows` | `0` | Clamps or injects a row cap on the root read (0 = disabled). |
| `AllowedFunctions` / `AllowedTableFunctions` | `null` | Narrow the function map of the dialect and the permitted `TABLE(...)` functions. |
| `TranslateTrinoDateFunctions` | `false` | Translate `date_add`, `date_trunc`, `now()` and interval arithmetic into the date arithmetic of the dialect. |
| `CompileTimeout` | 2 s | Compile budget, clamped to 30 s. |
| `MaxExpansionFactor` | 64 | Maximum emitted length divided by the input length, clamped to 256. |

### `SqlTokenSecurityOptions`

`SqlTokenSecurityOptions.Strict` enables every switch except the reserved identifier check. Each value is immutable and passed per
call, so concurrent callers that share one engine never influence each other.

| Switch | Rejects |
| :--- | :--- |
| `RejectComments` | SQL comments (SQ-02) |
| `RejectBackslashInStrings` | backslashes in string literals (SQ-01) |
| `RejectEscapedStringLiterals` | `E'...'` literals (SQ-01) |
| `RejectDollarQuoting` | `$$...$$` strings (SQ-02) |
| `RejectBracketLexerDifferentials` | `[`, `]` and bracket or comment sequences in literals (SQL-1) |
| `RejectNonAsciiIdentifiers` | non-ASCII characters in unquoted identifiers (SQ-10) |
| `RejectDotsInQuotedIdentifiers` | dots inside quoted identifiers (SQ-11) |
| `RejectTimeTravelQueries` | `FOR TIMESTAMP/VERSION AS OF` (SQ-13) |
| `RejectVariableSubstitutionSequences` | `${` (SEC-ADG-10); forced on for Databricks |
| `RejectReservedIdentifiers` | identifiers that start with the reserved gateway prefix |

Method call syntax (`expr.method(...)`, `Type::method(...)`) is always rejected.

### `FastSqlEngine` limits

| Property | Default | Description |
| :--- | :---: | :--- |
| `MaxQueryLength` | 65,536 | Maximum SQL length in characters. |
| `MaxNestingDepth` | 100 | Token-level nesting depth (parentheses, brackets, `CASE`, lambdas, unary chains). |
| `MaxParseTreeDepth` | 3,000 | Maximum parse tree depth, checked iteratively. |
| `ParseTimeout` | 5 s | Time budget per parse, including the wait for a parser slot. |
| `ParseThreadStackSize` | 16 MB | Stack of the dedicated parser thread (minimum 256 KB). |
| `ParseConcurrencyLimiter` | `null` | Limits concurrent parser threads; `null` uses the process-wide limiter (`ProcessorCount * 2`). |

The `Reject*` properties of the engine apply only to direct `Parse`, `ParseExpression` and `Analyze` calls without explicit
`SqlTokenSecurityOptions` (default `false`, so plain syntax parsing keeps working). `Compile` never reads them.

---

## Errors

| Exception | Meaning |
| :--- | :--- |
| `SecurityException` and derived types | A security rule rejected the statement (function policy, masked column, DML guard, coverage). |
| `SqlCompileNotSupportedException` | The dialect, statement class or construct has no governed path. |
| `SqlLimitExceededException` | A resource or dialect limit was exceeded (kind, dialect and counts only, never values). |
| `ReservedIdentifierException` | An identifier uses the reserved gateway prefix. |
| `UnfilteredDmlException` | An `UPDATE` or `DELETE` without a meaningful `WHERE` clause. |
| `ParseCanceledException` | Syntax error, or a token guard or the parse budget fired. |
| `GovernedSqlException` and derived types | A rule of the binder or the checked executor (row-count check, affected rows limit, constraint). |

Messages carry categories and counts, never request text or bound values.

---

## Tests

| Project | Content |
| :--- | :--- |
| `tests/TrinoSqlEngine.Tests` | Trino grammar compliance, analyzer, parser concurrency and limits, the compiler (`Compiler/`), dialect generators, security regressions (`Security/`), conformance fixtures, property tests with oracles. |
| `tests/Autheris.Tests.Architecture` | IL scans: no legacy types, no free-text expression nodes, `CompiledSql` is constructed only by the engine, `CommandText` is assigned only by the binder, governed entry points reach `Compile` and the binder. |
| `tests/Autheris.Tests.SqlCompilerGate` | Differential and execution gate on SQLite, PostgreSQL, SQL Server and Oracle with an independent policy oracle. |

```bash
dotnet test tests/TrinoSqlEngine.Tests
```

---

## Project structure

```text
src/TrinoSqlEngine/
├── FastSqlEngine.cs                   # Two-stage parsing engine and the ISqlEngine implementation
├── ISqlEngine.cs                      # Compile, Parse, ParseExpression, Analyze
├── GovernedSqlCompiler.cs             # The compile pipeline
├── CompileRequest.cs                  # Immutable compile input, limits, errors
├── SqlTokenSecurityOptions.cs         # Token guards
├── SqlFunctionPolicy.cs               # Function denylist and allowlist rules
├── SqlFunctionAllowlists.cs           # Curated per-dialect function lists
├── RowFilterAliases.cs                # Reserved aliases of correlated row filters
├── TargetSqlDialect.cs                # Dialect enum
├── Analysis/                          # Query analyzer, parameter extractor
├── Governance/                        # Policy predicates, providers, catalog, policy parser
├── Ast/
│   ├── Builder/                       # Parse tree to typed AST
│   ├── Nodes/                         # Typed AST nodes
│   ├── Visitors/                      # Security injection, simplification, validation
│   ├── Security/                      # Coverage verifier, tenant predicates, type annotators
│   ├── Capabilities/                  # Dialect capability table, validator, limits
│   ├── Generators/                    # Dialect generators
│   ├── Emit/                          # CompiledSql, template cache, binders, checked DML executor
│   └── Buffer/                        # Emitter context and string builder
├── SqlBase.g4                         # Trino SQL ANTLR4 grammar
└── LICENSE, NOTICE, THIRD_PARTY_NOTICES.md
```

---

## Legal and license notice

- **Grammar and dialect**: derived from the [Trino project](https://github.com/trinodb/trino), licensed under the **Apache License, Version 2.0**.
- **Modifications**: in compliance with Section 4(b) of the Apache License 2.0, all modified files (including [`SqlBase.g4`](SqlBase.g4)) contain explicit modification headers.
- **ANTLR 4 runtime**: licensed under the **BSD 3-Clause License**.
- **Disclaimers**: full license texts are in [`LICENSE`](LICENSE) and [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md). Attributions are in [`NOTICE`](NOTICE). *Trino* is a trademark of its respective owners; this project is an independent C# implementation and is not affiliated with or endorsed by Trino.
