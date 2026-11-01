namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Security;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class GovernedLoggingGeneratedSqlTests
{
    private static readonly ClaimsPrincipal TestUser = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"),
            new Claim("tenant_id", "tenant_a"),
            new Claim(ClaimTypes.Role, "Admin")
        ],
        "Test"));

    [Fact]
    [Trait("Category", "Security")]
    public async Task LOG426_T04_AllowStatementText_Ceiling_SuppressesLogging()
    {
        // Arrange: LogGeneratedSql = true, BUT AllowStatementText = false (ceiling)
        using var pipeline = KeySetPipeline.Create(logSql: true, allowStatementText: false);

        // Act
        await pipeline.Rewriter.RewriteCoreAsync(
            "SELECT id FROM public.orders",
            TestUser,
            new TenantId("tenant_a"),
            dataSourceName: null,
            dmlContext: null,
            CancellationToken.None);

        // Assert: zero SQL logged
        pipeline.Logs.ShouldNotContain(l => l.Message.Contains("Compiled secured SQL"));
        pipeline.Logs.ShouldNotContain(l => l.Message.Contains("SELECT"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task LOG426_T05_SqlTextScrubber_SanitizesLiterals_BeforeLogging()
    {
        // Arrange: LogGeneratedSql = true, AllowStatementText = true
        using var pipeline = KeySetPipeline.Create(logSql: true, allowStatementText: true);

        // Act: Query containing string and numeric literals
        await pipeline.Rewriter.RewriteCoreAsync(
            "SELECT id FROM public.orders WHERE id = 12345 AND tenant_id = 'secret_literal'",
            TestUser,
            new TenantId("tenant_a"),
            dataSourceName: null,
            dmlContext: null,
            CancellationToken.None);

        // Assert: Log message contains compiled secured SQL with QueryHash and StatementText
        var sqlLog = pipeline.Logs.Find(l => l.Message.Contains("Compiled secured SQL ["));
        sqlLog.Level.ShouldBe(LogLevel.Debug);

        // Raw literal values must NOT appear in the log output
        sqlLog.Message.ShouldNotContain("12345");
        sqlLog.Message.ShouldNotContain("secret_literal");
        sqlLog.Message.ShouldContain("Compiled secured SQL [");
    }

    [Fact]
    public async Task LogGeneratedSql_WhenFalse_EmitsNoSqlLog()
    {
        // Arrange: LogGeneratedSql = false (default)
        using var pipeline = KeySetPipeline.Create(logSql: false);

        // Act
        await pipeline.Rewriter.RewriteCoreAsync(
            "SELECT id FROM public.orders",
            TestUser,
            new TenantId("tenant_a"),
            dataSourceName: null,
            dmlContext: null,
            CancellationToken.None);

        // Assert
        pipeline.Logs.ShouldNotContain(l => l.Message.Contains("Compiled secured SQL"));
    }
}
