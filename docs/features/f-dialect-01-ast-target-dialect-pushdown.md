# F-DIALECT-01: Multi-Target SQL AST Compiler & Native Dialect Pushdown

> [!IMPORTANT]
> This feature is superseded by [F-DIALECT-02: Governed AST SQL Compiler](f-dialect-02-ast-sql-compiler.md). F-DIALECT-01 described an earlier, string-returning AST path that has been removed together with the token-stream rewriter and the `WebSql:SqlRewriterEngine` setting. F-DIALECT-02 is the reference for the compiler, the admitted dialects, the configuration and the security behavior.

## Where to find the current behavior

| Topic | Reference |
|---|---|
| The single governed SQL path (`ISqlEngine.Compile`, binders, checked DML) | [F-DIALECT-02, section 2](f-dialect-02-ast-sql-compiler.md#2-architecture) |
| Dialect tiers and WebSQL admission | [F-DIALECT-02, section 3](f-dialect-02-ast-sql-compiler.md#3-dialects) |
| Configuration, including removed settings | [F-DIALECT-02, section 7](f-dialect-02-ast-sql-compiler.md#7-configuration-reference) |
| Upgrading from a release with the legacy rewriter | [F-DIALECT-02, section 10](f-dialect-02-ast-sql-compiler.md#10-upgrading-to-the-single-path-compiler) |
