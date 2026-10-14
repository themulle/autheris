namespace TrinoSqlEngine;

using System;
using System.Threading;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using TrinoSqlEngine.Analysis;

/// <summary>
/// Architecture A3 / F-ARCH-10: Primary abstraction for SQL parsing, security analysis,
/// AST compilation, and dialect code generation.
/// </summary>
public interface ISqlEngine
{
    /// <summary>
    /// Compiles a governed read query: token guards, parse, typed AST, simplification of the user tree, typed security
    /// injection, coverage proof, capability validation and emission with every value bound. Dialects and statement classes
    /// without a governed path fail closed with <see cref="SqlCompileNotSupportedException"/>.
    /// </summary>
    Ast.Emit.CompiledSql Compile(ReadOnlyMemory<char> sql, CompileRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Parses a SQL statement into an ANTLR parse tree and token stream, enforcing depth limits and token security.
    /// </summary>
    (SqlBaseParser.SingleStatementContext Tree, CommonTokenStream Tokens) Parse(
        ReadOnlyMemory<char> sql,
        SqlTokenSecurityOptions? tokenOptions = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses a standalone SQL expression (e.g. for RLS predicates and masking rules).
    /// </summary>
    (SqlBaseParser.StandaloneExpressionContext Tree, CommonTokenStream Tokens) ParseExpression(
        ReadOnlyMemory<char> sql,
        SqlTokenSecurityOptions? tokenOptions = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes a SQL query AST for statement type, referenced physical tables, projected columns, and query metrics.
    /// </summary>
    SqlQueryMetadata Analyze(ReadOnlyMemory<char> sql);

    /// <summary>
    /// Analyzes a SQL query AST with explicit security options and cancellation.
    /// </summary>
    SqlQueryMetadata Analyze(
        ReadOnlyMemory<char> sql,
        SqlTokenSecurityOptions? tokenOptions,
        CancellationToken cancellationToken = default);
}
