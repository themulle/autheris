namespace Autheris.Tests.Unit.TestDoubles;

using System;
using System.Threading;
using Antlr4.Runtime;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Emit;

/// <summary>
/// Test-only engine decorator (WP-SIMPL-45 PR 3 CI repair, R-6): applies <see cref="TestWritePermits.WithTestWritePermit"/> inside
/// <see cref="Compile"/> so pipeline tests reach the code behind the write-permit check. It is passed only through the explicit
/// <c>sqlEngine:</c> parameter of the test factories; production DI registers <see cref="FastSqlEngine"/> directly.
/// </summary>
internal sealed class TestWritePermitSqlEngine(FastSqlEngine inner) : ISqlEngine
{
    public FastSqlEngine Inner { get; } = inner;

    public CompiledSql Compile(ReadOnlyMemory<char> sql, CompileRequest request, CancellationToken cancellationToken) =>
        Inner.Compile(sql, request.WithTestWritePermit(sql.ToString()), cancellationToken);

    public (SqlBaseParser.SingleStatementContext Tree, CommonTokenStream Tokens) Parse(
        ReadOnlyMemory<char> sql, SqlTokenSecurityOptions? tokenOptions = null, CancellationToken cancellationToken = default) =>
        Inner.Parse(sql, tokenOptions, cancellationToken);

    public (SqlBaseParser.StandaloneExpressionContext Tree, CommonTokenStream Tokens) ParseExpression(
        ReadOnlyMemory<char> sql, SqlTokenSecurityOptions? tokenOptions = null, CancellationToken cancellationToken = default) =>
        Inner.ParseExpression(sql, tokenOptions, cancellationToken);

    public SqlQueryMetadata Analyze(ReadOnlyMemory<char> sql) => Inner.Analyze(sql);

    public SqlQueryMetadata Analyze(ReadOnlyMemory<char> sql, SqlTokenSecurityOptions? tokenOptions, CancellationToken cancellationToken = default) =>
        Inner.Analyze(sql, tokenOptions, cancellationToken);
}
