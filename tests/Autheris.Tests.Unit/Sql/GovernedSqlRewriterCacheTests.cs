namespace Autheris.Tests.Unit.Sql;

using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Antlr4.Runtime;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

[Trait("Category", "Security")]
public sealed class GovernedSqlRewriterCacheTests
{
    private sealed class CountingSqlEngine : ISqlEngine
    {
        public int AnalyzeCalls;

        public CompiledSql Compile(ReadOnlyMemory<char> sql, CompileRequest request, CancellationToken cancellationToken)
            => FastSqlEngine.Default.Compile(sql, request, cancellationToken);

        public (SqlBaseParser.SingleStatementContext Tree, CommonTokenStream Tokens) Parse(
            ReadOnlyMemory<char> sql,
            SqlTokenSecurityOptions? tokenOptions = null,
            CancellationToken cancellationToken = default)
            => FastSqlEngine.Default.Parse(sql, tokenOptions, cancellationToken);

        public (SqlBaseParser.StandaloneExpressionContext Tree, CommonTokenStream Tokens) ParseExpression(
            ReadOnlyMemory<char> sql,
            SqlTokenSecurityOptions? tokenOptions = null,
            CancellationToken cancellationToken = default)
            => FastSqlEngine.Default.ParseExpression(sql, tokenOptions, cancellationToken);

        public SqlQueryMetadata Analyze(ReadOnlyMemory<char> sql)
            => Analyze(sql, null, CancellationToken.None);

        public SqlQueryMetadata Analyze(
            ReadOnlyMemory<char> sql,
            SqlTokenSecurityOptions? tokenOptions,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref AnalyzeCalls);
            return FastSqlEngine.Default.Analyze(sql, tokenOptions, cancellationToken);
        }
    }

    private static ClaimsPrincipal User() => new(new ClaimsIdentity([new Claim("sid", "S-1-5-21-1")], "test"));

    [Fact]
    public async Task Statement_above_cap_is_not_cached()
    {
        var engine = new CountingSqlEngine();
        var options = Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true } });
        var rewriter = new GovernedSqlRewriter(options, sqlEngine: engine);

        const string prefix = "DROP TABLE a.b.c /* ";
        const string suffix = " */";
        var padLength = 8_193 - prefix.Length - suffix.Length;
        var oversizedSql = prefix + new string('x', padLength) + suffix;
        oversizedSql.Length.ShouldBeGreaterThan(8_192);

        for (var i = 0; i < 2; i++)
        {
            await Should.ThrowAsync<WebSqlPolicyException>(() => rewriter.ValidateAsync(oversizedSql, User(), new TenantId("t1")));
        }

        Volatile.Read(ref engine.AnalyzeCalls).ShouldBe(2);
    }
}
