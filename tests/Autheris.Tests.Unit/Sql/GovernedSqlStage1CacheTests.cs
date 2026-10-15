namespace Autheris.Tests.Unit.Sql;

using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using Xunit;

/// <summary>WP-PF-12 (Stage-1 part): the parse of one statement text is shared; policy checks still run on every request.</summary>
public sealed class GovernedSqlStage1CacheTests
{
    private static (GovernedSqlRewriter Rewriter, Func<int> AnalyzeCalls) Create()
    {
        var calls = 0;
        var engine = Substitute.For<ISqlEngine>();
        engine.Analyze(Arg.Any<ReadOnlyMemory<char>>(), Arg.Any<SqlTokenSecurityOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Interlocked.Increment(ref calls);
                return FastSqlEngine.Default.Analyze(ci.ArgAt<ReadOnlyMemory<char>>(0), ci.ArgAt<SqlTokenSecurityOptions?>(1), ci.ArgAt<CancellationToken>(2));
            });
        var options = Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true } });
        return (new GovernedSqlRewriter(options, sqlEngine: engine), () => Volatile.Read(ref calls));
    }

    private static ClaimsPrincipal User() => new(new ClaimsIdentity([new Claim("sid", "S-1-5-21-1")], "test"));

    [Fact]
    public async Task SameStatementText_IsAnalyzedOnce_ButStillRejectedByPolicyEachTime()
    {
        var (rewriter, calls) = Create();

        for (var i = 0; i < 3; i++)
        {
            await Should.ThrowAsync<WebSqlPolicyException>(() => rewriter.ValidateAsync("DROP TABLE a.b.c", User(), new TenantId("t1")));
        }

        calls().ShouldBe(1);
    }

    [Fact]
    public async Task DifferentStatementTexts_AreAnalyzedSeparately()
    {
        var (rewriter, calls) = Create();

        await Should.ThrowAsync<WebSqlPolicyException>(() => rewriter.ValidateAsync("DROP TABLE a.b.c", User(), new TenantId("t1")));
        await Should.ThrowAsync<WebSqlPolicyException>(() => rewriter.ValidateAsync("DROP TABLE a.b.d", User(), new TenantId("t1")));

        calls().ShouldBe(2);
    }

    [Fact]
    public async Task FailedAnalysis_IsNeverCached()
    {
        var (rewriter, calls) = Create();

        for (var i = 0; i < 2; i++)
        {
            await Should.ThrowAsync<Exception>(() => rewriter.ValidateAsync("SELECT FROM WHERE ((", User(), new TenantId("t1")));
        }

        calls().ShouldBe(2);
    }
}
