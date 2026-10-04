# TrinoSqlEngine: High-Performance SQL Parser, AST Analyzer & RLS Rewriter for .NET 10

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![C# 14](https://img.shields.io/badge/C%23-14-239120.svg)](https://learn.microsoft.com/dotnet/csharp/)
[![ANTLR 4](https://img.shields.io/badge/ANTLR-4.13.1-C9252B.svg)](https://www.antlr.org/)
[![Tests](https://img.shields.io/badge/Tests-857%20Passing-brightgreen.svg)]()
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)
[![Zero-Allocation](https://img.shields.io/badge/Architecture-Zero--Allocation%20SLL-orange.svg)]()

A high-throughput, allocation-optimized SQL parser, AST semantic analyzer, and Row-Level Security (RLS) query rewriting engine in C# / .NET 10, ported from the official [Trino](https://github.com/trinodb/trino) ANTLR4 grammar (`SqlBase.g4`).

Designed for high-scale multi-tenant data gateways, lakehouses (Apache Iceberg), database proxies, and analytical query engines requiring zero-overhead security enforcement, fine-grained column masking, and standard ANSI SQL compliance.

---

## 🌟 Key Capabilities

- **Two-Stage Parsing Architecture ($O(n)$ SLL Fast-Path + LL Fallback)**:
  Executes linear prediction with a pre-configured, reusable `BailErrorStrategy`. Allocates **0 bytes** for error listeners or strategy instances during normal execution. Automatically recovers using full contextual lookahead ($LL^*$) if ambiguity arises.
- **Deep AST Query Analysis (`SqlQueryAnalyzer`)**:
  Inspects parsed queries to extract statement types (`SELECT`, `INSERT`, `UPDATE`, `DELETE`, `DDL`), referenced physical tables (resolving schemas, catalogs, and aliases while ignoring CTE shadows), projected columns, join counts, join condition columns, subquery depth, and explicit `LIMIT` / `FETCH FIRST` clauses.
- **AST Row-Level Security Rewriting (`RlsListener`)**:
  Non-destructive AST query rewriter using ANTLR `TokenStreamRewriter`. Preserves original user formatting, spacing, indentation, and comments while injecting secure tenant isolation subqueries or `WHERE` filters.
- **Dynamic In-Database Column Data Masking (`IColumnMaskingPolicyProvider`)**:
  Pushes column masking expressions (e.g. `'***@domain.com'`, `NULL`, hashing) directly into the SQL AST projection with table schema awareness (`TableColumnsProvider`). Emits delimited identifiers to prevent syntax collisions.
- **Hardened Algorithmic DoS & StackOverflow Guards**:
  - `MaxQueryLength`: Configurable character ceiling (default 64,000) to prevent memory exhaustion attacks.
  - `MaxNestingDepth`: Token-level depth scanner checking parentheses, brackets, `CASE ... END`, array/map literals, lambda expressions (`->`), and unary operator chains (`+`, `-`, `NOT`) before recursive parsing.
  - `MaxParseTreeDepth`: Iterative (non-recursive) depth verification guarding against left-recursive grammar expansions before any recursive tree walk.
- **Function Security Policy Sandboxing (`SqlFunctionPolicy`)**:
  Pre-configured denylist of high-risk scalar and aggregate functions (PostgreSQL XML table dumps, `pg_read_file`, `dblink`, `xp_cmdshell`, `load_file`, `sys_eval`, `dbms_xmlgen`, etc.) with customizable allowlists and prefix rules.
- **Lexer-Safe Parameter Extraction (`SqlParameterExtractor`)**:
  Extracts named (`@param`, `{{param}}`) and positional parameters without regex Denial-of-Service vulnerabilities, with support for comment stripping and target column deduction.
- **DML Protection & `WITH CHECK OPTION`**:
  Strict validation for `INSERT` and `UPDATE` statements to prevent tenant hopping and trojan record injection. Disallows modifying tenant keys and blocks DML on masked columns to eliminate side-channel oracles.

---

## ⚡ Performance Architecture

```
                  ┌────────────────────────────────────────┐
                  │          Raw SQL (Memory<char>)        │
                  └───────────────────┬────────────────────┘
                                      │
                         ZeroCopyCaseInsensitiveStream
                         (Branchless CPU register case)
                                      │
                         SqlBaseLexer (Shared ATN/DFA)
                                      │
                         Nesting Depth Token Scanner
                                      │
                  ┌───────────────────┴────────────────────┐
                  │                                        │
           [ Stage 1: SLL ]                         [ Stage 2: LL(*) ]
      Fast linear O(n) prediction               Full contextual fallback
      BailErrorStrategy (0 alloc)               on ParseCanceledException
                  │                                        │
                  └───────────────────┬────────────────────┘
                                      │
                        Iterative Tree Depth Check
                                      │
                ┌─────────────────────┼─────────────────────┐
                ▼                     ▼                     ▼
        SqlQueryAnalyzer         RlsListener        SqlParameterExtractor
     (Tables, Joins, Limits)  (RLS, Masking, DML)     (Named & Positional)
```

### 1. Zero-Allocation Case-Insensitive Character Stream
- **Branchless CPU Uppercasing**: Converts ASCII characters on-the-fly (`(uint)(c - 'a') <= ('z' - 'a') ? (char)(c - 32) : char.ToUpperInvariant(c)`) directly in CPU registers without allocating temporary uppercase strings.
- **`LA(1)` Inlined Fast Path**: Inlined lookahead shortcut for `i == 1` (>95% of lexer accesses) with zero bounds-check overhead.
- **Direct Substring Slicing**: Avoids memory buffer copies by maintaining string slice offsets for sub-nanosecond token retrieval.

### 2. Global ATN & DFA Decision Table Caching
- Static process-wide reuse of ANTLR `ATN` and `DFA` decision tables (`SharedParserCache.cs`).
- Eliminates expensive DFA state re-initialization across threads and concurrent requests.

### 3. Parser Object Pooling & Zero-Allocation SLL Path
- High-throughput pooling using `Microsoft.Extensions.ObjectPool<SqlBaseParser>`.
- **Two-Stage Execution Engine**:
  1. **Stage 1 (SLL Fast-Path)**: Executes linear $O(n)$ prediction with a pre-configured, reusable `BailErrorStrategy`. 0 bytes allocated for error listeners or strategy objects during normal execution.
  2. **Stage 2 (LL(*) Fallback)**: Automatically recovers and resolves ambiguities using full contextual lookahead if `ParseCanceledException` occurs.

### 4. $O(1)$ Precomputed Keyword Table
- Replaces runtime regular expressions on non-reserved keyword predicates (`isKeyword`) with a fixed 512-byte L1-cache-friendly `bool[]` lookup table (`SqlKeywords.cs`).

### 5. .NET 10 Runtime Tuning
- Enabled Dynamic PGO (`<TieredPGO>true</TieredPGO>`).
- Dynamic Adaptation To Application Sizes GC (`<GarbageCollectionAdaptationMode>1</GarbageCollectionAdaptationMode>`).
- Aggressive inlining (`[MethodImpl(MethodImplOptions.AggressiveInlining)]`) across critical loop paths.

---

## 🔒 Row-Level Security (RLS) & Query Rewriter

The engine includes a production-grade AST query rewriter (`RlsListener.cs`) backed by ANTLR's `TokenStreamRewriter`.

### Supported Operations & Strategies

| Statement | Rewrite Strategy | Security Enforcement |
| :--- | :--- | :--- |
| **`SELECT`** | Rewrites table references to `(SELECT * FROM table WHERE {filter}) [AS table]` | Lexical CTE scope isolation, PTF table argument rewriting, Column Masking Pushdown |
| **`DELETE`** | Injects/merges filter into `WHERE`: `WHERE ({filter}) AND ({existingWhere})` | Strict parenthesization (prevents boolean `OR 1=1` bypass); blocks filters on masked columns |
| **`UPDATE`** | Injects/merges filter into `WHERE`: `WHERE ({filter}) AND ({existingWhere})` | `WITH CHECK OPTION`: blocks modification of tenant ID column; rejects updates on masked columns |
| **`INSERT`** | Rewrites underlying query sources | `WITH CHECK OPTION`: validates inline `VALUES` and constant `SELECT` projections; requires tenant column |
| **`LIMIT`** | Clamps or injects top-level `LIMIT {maxRows}` | Result set exhaustion protection via `EnforcedMaxRows` |

### Built-in Security Protections

* **CTE Shadowing Defense (ANSI SQL Lexical Scoping):**
  CTE names are added to scope on *exit* of the CTE definition, not entry. This prevents attacks where an attacker defines `WITH orders AS (SELECT * FROM orders) SELECT * FROM orders` in an attempt to bypass physical table filtering in the inner query.
* **Quoted & Normalized Identifiers:**
  Resilient against quote bypasses (`"orders"`, `` `orders` ``, `[orders]`). The engine strips quoting for policy verification while preserving the exact original representation in the rewritten output.
* **Polymorphic Table Functions (PTF):**
  Secures `TABLE(...)` arguments passed to table functions (e.g., `SELECT * FROM TABLE(my_ptf(TABLE(orders)))`). Table function names are verified against `AllowedTableFunctions`.
* **Operator Precedence Protection:**
  Every injected predicate is wrapped in parentheses (`({policyFilter}) AND ({userPredicate})`), preventing operator precedence hijack attacks.
* **Administrative DDL Lockout:**
  Whitelisting automatically rejects administrative and data definition statements (`DROP`, `TRUNCATE`, `ALTER`, `CREATE`, `MERGE`, `GRANT`) unless explicitly allowed.
* **Function Policy Enforcement:**
  Blocks unsafe database functions (e.g. `query_to_xml`, `pg_read_file`, `dblink`, `xp_cmdshell`, `load_file`) by default to prevent out-of-band data exfiltration and RLS bypasses.

---

## 💻 Usage Examples

### 1. Basic Parsing & AST Inspection

```csharp
using System;
using TrinoSqlEngine;

var engine = new FastSqlEngine();

// Parse statement into ANTLR AST and TokenStream
var (tree, tokens) = engine.Parse("SELECT id, name FROM users WHERE active = true".AsMemory());

Console.WriteLine($"Parsed successfully: {tree.ChildCount} top-level nodes");

// Parse standalone expression
var (exprTree, exprTokens) = engine.ParseExpression("price * 1.19 > 100".AsMemory());
```

### 2. SQL Query Analysis & Metadata Extraction

```csharp
using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;

var engine = new FastSqlEngine();

string sql = @"
    WITH regional_sales AS (
        SELECT region, SUM(amount) AS total FROM orders GROUP BY region
    )
    SELECT c.name, r.total 
    FROM customers c 
    JOIN regional_sales r ON c.region = r.region 
    WHERE c.status = 'ACTIVE' 
    LIMIT 50";

SqlQueryMetadata meta = engine.Analyze(sql.AsMemory());

Console.WriteLine($"Statement Type: {meta.StatementType}");        // Select
Console.WriteLine($"Join Count:     {meta.JoinCount}");             // 1
Console.WriteLine($"Has Limit:      {meta.HasExplicitLimit}");      // True
Console.WriteLine($"Limit Value:    {meta.ExplicitLimitValue}");    // 50

// Physical tables only (CTE 'regional_sales' is automatically filtered out):
foreach (TableAccessTarget table in meta.ReferencedTables)
{
    Console.WriteLine($"Physical Table: {table.FullName} (Alias: {table.Alias})");
}
// Outputs:
// Physical Table: orders (Alias: null)
// Physical Table: customers (Alias: c)
```

### 3. Row-Level Security (RLS) Query Rewriting

```csharp
using System;
using TrinoSqlEngine;

var engine = new FastSqlEngine();

var options = new RlsOptions
{
    PolicyProvider = new DefaultRlsPolicyProvider(
        defaultFilter: "tenant_id = 'tenant_corp_1'",
        predicate: tableName => tableName.Equals("orders", StringComparison.OrdinalIgnoreCase)
    ),
    AppendTableAlias = true
};

string sql = "SELECT orders.id, customers.name FROM orders JOIN customers ON orders.cust_id = customers.id";
string secured = engine.RewriteRls(sql.AsMemory(), options);

Console.WriteLine(secured);
// Output:
// SELECT orders.id, customers.name FROM (SELECT * FROM orders WHERE tenant_id = 'tenant_corp_1') AS orders JOIN customers ON orders.cust_id = customers.id
```

### 4. Dynamic Column Data Masking

```csharp
using System;
using TrinoSqlEngine;

var engine = new FastSqlEngine();

var options = new RlsOptions
{
    AppendTableAlias = true,
    PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 't1'"),
    // Supply known table schema to push down column projection
    TableColumnsProvider = tableName => tableName.Equals("employees", StringComparison.OrdinalIgnoreCase)
        ? new[] { "id", "name", "email", "salary", "tenant_id" }
        : null,
    // Define masking transformations
    ColumnMaskingProvider = new DefaultColumnMaskingPolicyProvider(
        hasMaskPredicate: (tbl, col) => tbl.Equals("employees", StringComparison.OrdinalIgnoreCase) && 
                                        (col.Equals("email", StringComparison.OrdinalIgnoreCase) || 
                                         col.Equals("salary", StringComparison.OrdinalIgnoreCase)),
        maskExpressionProvider: (tbl, col) => col.ToLowerInvariant() switch
        {
            "email" => "'***@company.com'",
            "salary" => "0",
            _ => col
        })
};

string sql = "SELECT id, email, salary FROM employees";
string secured = engine.RewriteRls(sql.AsMemory(), options);

Console.WriteLine(secured);
// Output:
// SELECT id, email, salary FROM (SELECT "id", "name", '***@company.com' AS "email", 0 AS "salary", "tenant_id" FROM employees WHERE tenant_id = 't1') AS employees
```

### 5. DML Rewriting with `WITH CHECK OPTION`

```csharp
using System;
using TrinoSqlEngine;

var engine = new FastSqlEngine();

var options = new RlsOptions
{
    EnforceReadOnlyQueries = false,          // Allow DML statements
    EnforceWithCheckOption = true,          // Validate INSERT / UPDATE
    TenantColumnName = "tenant_id",
    ExpectedTenantValue = "42",
    DisallowTenantColumnModificationInUpdate = true,
    RejectMaskedColumnsInDml = true
};

// 1. DELETE rewrite (merges tenant filter with parenthesized user WHERE)
string deleteSql = "DELETE FROM orders WHERE id = 10 OR 1=1";
string securedDelete = engine.RewriteRls(deleteSql.AsMemory(), options);
// Output: DELETE FROM orders WHERE (tenant_id = 42) AND (id = 10 OR 1=1)

// 2. UPDATE rewrite (secures target table)
string updateSql = "UPDATE orders SET status = 'shipped' WHERE id = 10";
string securedUpdate = engine.RewriteRls(updateSql.AsMemory(), options);
// Output: UPDATE orders SET status = 'shipped' WHERE (tenant_id = 42) AND (id = 10)

// 3. WITH CHECK OPTION rejection (throws SecurityException)
string maliciousInsert = "INSERT INTO orders (id, tenant_id) VALUES (1, 99)";
engine.RewriteRls(maliciousInsert.AsMemory(), options); 
// Throws: System.Security.SecurityException: Tenant column 'tenant_id' inserted value '99' does not match expected tenant '42'.
```

### 6. Result Limit Clamping & Injection (`EnforcedMaxRows`)

```csharp
var options = new RlsOptions
{
    EnforcedMaxRows = 100, // Clamp or inject LIMIT 100
    PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42")
};

// Query without limit: appends LIMIT 100
string query1 = engine.RewriteRls("SELECT * FROM items".AsMemory(), options);
// Output: SELECT * FROM (SELECT * FROM items WHERE tenant_id = 42) LIMIT 100

// Query with oversized limit: clamps to 100
string query2 = engine.RewriteRls("SELECT * FROM items LIMIT 10000".AsMemory(), options);
// Output: SELECT * FROM (SELECT * FROM items WHERE tenant_id = 42) LIMIT 100
```

### 7. Lexer-Safe Parameter Extraction

```csharp
using System;
using TrinoSqlEngine.Analysis;

string sql = "SELECT * FROM orders WHERE customer_id = @custId AND status = {{status}} AND total > ?";

// Fast token extraction ignoring SQL comments
var parameters = SqlParameterExtractor.ExtractTokens(sql);

foreach (var p in parameters)
{
    Console.WriteLine($"Found Parameter: {p.Name} (Token: {p.Token})");
}
```

---

## ⚙️ Configuration Reference

### `RlsOptions`

| Property | Type | Default | Description |
| :--- | :--- | :---: | :--- |
| `PolicyProvider` | `IRlsPolicyProvider` | `DefaultRlsPolicyProvider` | Determines whether and which filter to inject for a given table. |
| `ColumnMaskingProvider` | `IColumnMaskingPolicyProvider?` | `null` | Provides dynamic SQL expressions to mask sensitive columns. |
| `TableColumnsProvider` | `Func<string, IReadOnlyList<string>?>?` | `null` | Returns the table column list for full in-database projection pushdown. |
| `EnforcedMaxRows` | `long` | `0` | Clamps or injects top-level `LIMIT {n}` (0 = disabled). |
| `EnforceReadOnlyQueries` | `bool` | `true` | Throws if non-SELECT statements (INSERT, UPDATE, DELETE, DDL) are passed. |
| `AppendTableAlias` | `bool` | `false` | Automatically appends `AS {tableName}` to rewritten subqueries. |
| `EnforceWithCheckOption` | `bool` | `true` | Validates INSERT/UPDATE statements against tenant boundaries. |
| `TenantColumnName` | `string` | `"tenant_id"` | Name of the tenant partition column. |
| `ExpectedTenantValue` | `string` | `"42"` | Expected tenant ID for `WITH CHECK OPTION`. |
| `DisallowTenantColumnModificationInUpdate` | `bool` | `true` | Forbids updating the tenant column under any circumstances. |
| `RequireTenantColumnInInsert` | `bool` | `true` | Requires INSERT statements to explicitly specify the tenant column. |
| `RejectMaskedColumnsInDml` | `bool` | `true` | Forbids referencing masked columns in `UPDATE SET` or `WHERE` clauses. |
| `EnforceFunctionPolicy` | `bool` | `true` | Validates every function call against `SqlFunctionPolicy`. |
| `AllowedFunctions` | `IReadOnlySet<string>?` | `null` | Exclusive allowlist of permitted function names. The default denylist always wins. Curated per-dialect lists: `SqlFunctionAllowlists` (`Ansi`, `PostgreSql`, `SqlServer`, `Sqlite`, `Build(dialect, additional)`). |
| `AdditionalDeniedFunctions` | `IReadOnlySet<string>?` | `null` | Additional function names to strictly reject. |
| `AllowedTableFunctions` | `IReadOnlySet<string>?` | `null` | Permitted polymorphic table functions (`TABLE(...)`). Default rejects all. |
| `AllowedSessionProperties` | `IReadOnlySet<string>?` | `null` | Permitted session properties in `WITH SESSION`. Default rejects all. |
| `AllowInlineFunctionDefinitions` | `bool` | `false` | When false, rejects `WITH FUNCTION ...` inline functions. |
| `RejectComments` | `bool` | `true` | Rejects SQL comments (SQ-02). |
| `RejectBackslashInStrings` | `bool` | `true` | Rejects backslashes in string literals (SQ-01). |
| `RejectEscapedStringLiterals` | `bool` | `true` | Rejects `E'...'` literals (SQ-01). |
| `RejectDollarQuoting` | `bool` | `true` | Rejects `$$...$$` strings (SQ-02); always rejected for SQL Server targets. |
| `RejectNonAsciiIdentifiers` | `bool` | `true` | Rejects non-ASCII characters in unquoted identifiers (SQ-10). |
| `RejectDotsInQuotedIdentifiers` | `bool` | `true` | Rejects dots inside quoted identifiers, e.g. `"a.b"` (SQ-11). |
| `RejectTimeTravelQueries` | `bool` | `true` | Rejects `FOR TIMESTAMP/VERSION AS OF` (SQ-13). |

`RewriteRls` derives these token switches per call as an immutable `SqlTokenSecurityOptions` object; it never reads or
modifies the engine's own `Reject*` properties. The engine properties (default `false`) only apply to direct
`Parse`/`ParseExpression`/`Analyze` calls without explicit `SqlTokenSecurityOptions`, so that plain syntax parsing
(Trino compliance fixtures) keeps working. Method call syntax (`expr.method(...)`, `Type::method(...)`) is always rejected
by the rewriter and the analyzer.

### `FastSqlEngine` Limits

| Property | Type | Default | Description |
| :--- | :--- | :---: | :--- |
| `MaxQueryLength` | `int` | `65,536` | Maximum allowed SQL query length in characters. |
| `MaxNestingDepth` | `int` | `100` | Maximum token-level nesting depth (parentheses, brackets, CASE, lambdas, unary chains). 0 disables only this check; token security checks always run. |
| `MaxParseTreeDepth` | `int` | `3,000` | Maximum depth of the resulting parse tree (checked iteratively). |
| `ParseTimeout` | `TimeSpan` | `5 s` | Time budget per parse (incl. waiting for a parser slot). On expiry a `ParseCanceledException` with inner `TimeoutException` is thrown. `<= 0` disables the budget. |
| `ParseThreadStackSize` | `int` | `16 MB` | Stack size of the dedicated parser thread (minimum 256 KB). |
| `ParseConcurrencyLimiter` | `SemaphoreSlim?` | `null` | Limits concurrently running parser threads; `null` uses the process-wide `SharedParseConcurrencyLimiter` (`Environment.ProcessorCount * 2`). |

---

## 🧪 Compliance & Test Suite

The engine includes **857 automated tests** (100% passing) verifying standard SQL compliance and enterprise security:

1. **761 Trino Official Compliance Tests (`TrinoParserComplianceTests.cs`)**:
   Derived directly from Trino's official test suite (`io.trino.sql.parser.TestSqlParser`). Covers complex `SELECT`, `JOIN`, recursive `WITH`, window functions, pattern matching, `GROUPING SETS`, JSON path operators, and DML.
2. **Security Remediation Suite (`SecurityRemediationTests.cs`)**:
   Verifies algorithmic DoS protections (nesting depth, parse tree depth, query length limits), multi-threaded parser concurrency, CTE shadowing isolation, DML `WITH CHECK OPTION`, and function policy enforcement.
3. **Analysis & Masking Suite (`AnalysisAndMaskingTests.cs`)**:
   Validates table and column extraction, join count calculation, limit detection, dynamic column masking pushdown, and `EnforcedMaxRows` clamping.
4. **Parameter Extractor Tests (`SqlParameterExtractorTests.cs`)**:
   Verifies extraction of named and positional parameters across complex queries with comments.

### Running Tests

```bash
# Run all 857 tests
dotnet test TrinoSqlEngine.csproj

# Run in Release mode with compiler optimizations
dotnet test TrinoSqlEngine.csproj -c Release
```

---

## 📁 Project Structure

```text
gql_sqlparser/
├── FastSqlEngine.cs                   # Two-stage parsing engine & facade API
├── RlsListener.cs                     # AST listener for RLS, masking & DML rewriting
├── IRlsPolicyProvider.cs              # Policy provider interfaces & RlsOptions
├── SqlFunctionPolicy.cs               # Pre-configured function security policies & denylists
├── ZeroCopyCaseInsensitiveStream.cs   # Register-based case-insensitive ICharStream
├── SharedParserCache.cs               # Global ATN/DFA static cache
├── ParserPooledObjectPolicy.cs        # Object pool policy for SqlBaseParser
├── SqlKeywords.cs                     # Precomputed O(1) keyword lookup table
├── SqlBase.g4                         # Trino ANSI SQL ANTLR4 grammar
├── TrinoSqlEngine.csproj              # .NET 10 project file
├── Analysis/
│   ├── ISqlQueryAnalyzer.cs           # Query analysis contracts & record models
│   ├── SqlQueryAnalyzer.cs            # AST query analyzer implementation
│   └── SqlParameterExtractor.cs       # Lexer-safe parameter extraction
├── Fixtures/                          # Trino compliance test fixtures
├── AnalysisAndMaskingTests.cs         # Tests for analyzer, masking & limit clamping
├── SecurityRemediationTests.cs        # Security audit & DoS remediation tests
├── SqlParameterExtractorTests.cs      # Parameter extractor tests
├── TrinoParserComplianceTests.cs      # xUnit Trino grammar compliance suite (857 tests)
├── LICENSE                            # Apache License 2.0
├── NOTICE                             # Attribution notices
└── THIRD_PARTY_NOTICES.md             # Third-party license documentation
```

---

## 📜 Legal & License Notice

- **Grammar & Dialect**: Derived from the [Trino project](https://github.com/trinodb/trino), licensed under the **Apache License, Version 2.0**.
- **Modifications**: In compliance with Section 4(b) of the Apache License 2.0, all modified files (including [`SqlBase.g4`](SqlBase.g4)) contain explicit modification headers.
- **ANTLR 4 Runtime**: Licensed under the **BSD 3-Clause License**.
- **Disclaimers**: Full license texts are documented in [`LICENSE`](LICENSE) and [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md). Attributions are listed in [`NOTICE`](NOTICE). *Trino* is a trademark of its respective owners; this project is an independent C# implementation and is not affiliated with or endorsed by Trino.
