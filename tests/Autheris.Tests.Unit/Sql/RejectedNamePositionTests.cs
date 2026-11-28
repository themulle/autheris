namespace Autheris.Tests.Unit.Sql;

using System;
using System.Threading;
using Antlr4.Runtime;
using Autheris.Application.Sql.Services;
using Microsoft.Extensions.Options;
using Autheris.Domain.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Parsing;
using Xunit;

/// <summary>A rejection of gateway-augmented text keeps its name but never a position that points into text the client did not write.</summary>
public sealed class RejectedNamePositionTests
{
    private sealed class ThrowingEngine(Exception error) : ISqlEngine
    {
        public CompiledSql Compile(ReadOnlyMemory<char> sql, CompileRequest request, CancellationToken cancellationToken) => throw error;

        public (SqlBaseParser.SingleStatementContext Tree, CommonTokenStream Tokens) Parse(ReadOnlyMemory<char> sql, SqlTokenSecurityOptions? tokenOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public (SqlBaseParser.StandaloneExpressionContext Tree, CommonTokenStream Tokens) ParseExpression(ReadOnlyMemory<char> sql, SqlTokenSecurityOptions? tokenOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public SqlQueryMetadata Analyze(ReadOnlyMemory<char> sql) => throw new NotSupportedException();

        public SqlQueryMetadata Analyze(ReadOnlyMemory<char> sql, SqlTokenSecurityOptions? tokenOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static Exception Compile(Exception error, bool textIsClientText)
    {
        var rewriter = new GovernedSqlRewriter(Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true } }), sqlEngine: new ThrowingEngine(error));
        return Should.Throw<Exception>(() => rewriter.CompileStatement("SELECT 1", null!, CancellationToken.None, textIsClientText));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void AugmentedText_FunctionRejection_KeepsNameDropsPosition()
    {
        var ex = Compile(new FunctionNotPermittedException("pg_sleep", 3, 9), textIsClientText: false).ShouldBeOfType<FunctionNotPermittedException>();

        ex.FunctionName.ShouldBe("pg_sleep");
        ex.Line.ShouldBeNull();
        ex.Column.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void AugmentedText_ConstructRejection_KeepsLabelDropsPosition()
    {
        var ex = Compile(new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Construct, "LATERAL", SqlConstructCatalog.Lateral), textIsClientText: false);
        ((SqlCompileNotSupportedException)ex).LocateAt(5, 5);   // a locator hit on augmented text would be set by the compiler before the catch
        var again = Compile(ex, textIsClientText: false).ShouldBeOfType<SqlCompileNotSupportedException>();

        again.PublicConstruct.ShouldBe(SqlConstructCatalog.Lateral);
        again.Line.ShouldBeNull();
    }

    [Fact]
    public void ClientText_FunctionRejection_KeepsPosition()
    {
        var ex = Compile(new FunctionNotPermittedException("pg_sleep", 3, 9), textIsClientText: true).ShouldBeOfType<FunctionNotPermittedException>();

        ex.Line.ShouldBe(3);
        ex.Column.ShouldBe(9);
    }
}
