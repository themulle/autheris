# F-DIALECT-01: Multi-Target SQL AST Compiler & Native Dialect Pushdown

> [!IMPORTANT]
> This document is superseded by [F-DIALECT-02: Governed AST SQL Compiler](f-dialect-02-ast-sql-compiler.md). It described the first, string-returning AST path and overstated dialect support. F-DIALECT-02 is the reference for the typed compiler, the dialect tiers, the configuration and the security behavior.

## What this document covered

The first AST path (`FastSqlEngine.GenerateGovernedSql`, selected with `WebSql:SqlRewriterEngine = AstCompiler`) parses Trino SQL into a typed AST, injects tenant, row-policy and masking nodes, simplifies the tree and emits dialect text through an `ISqlDialectGenerator`. It returns a SQL string and is not the typed compiler `ISqlEngine.Compile`.

## Engine selection

The legacy token-stream rewriter is the default engine (`WebSql:SqlRewriterEngine` unset or `LegacyTokenStream`). `AstCompiler` and `ShadowDualRun` are opt-in values of the same setting. See [F-DIALECT-02, section 4.1](f-dialect-02-ast-sql-compiler.md#41-engine-selection).

## Dialect generators

Generators exist for SQL Server, PostgreSQL, SQLite, DuckDB, Oracle, Databricks, Snowflake and ANSI. Support tiers apply only to the typed compiler: SQL Server, PostgreSQL, DuckDB and Oracle are Production, Databricks is Experimental, and SQLite, Snowflake and ANSI have no entry in the compiler's capability table. See [F-DIALECT-02, section 3](f-dialect-02-ast-sql-compiler.md#3-supported-dialects-and-tiers).
