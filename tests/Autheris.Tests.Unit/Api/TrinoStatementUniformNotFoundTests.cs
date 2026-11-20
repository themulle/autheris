using Autheris.Tests.Unit.TestSupport;
namespace Autheris.Tests.Unit.Api;

using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using NSubstitute.ExceptionExtensions;
using Autheris.Domain.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public class TrinoStatementUniformNotFoundTests
{
    [Fact]
    public async Task SEC_12H_06_HandleTrinoQueuedStatementRequest_SecurityException_ReturnsUniform404()
    {
        var statementManager = Substitute.For<Application.Sql.Interfaces.IWebSqlStatementManager>();
        statementManager.GetStatusOrWaitAsync(Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<Sid>(), Arg.Any<TenantId>(), Arg.Any<TimeSpan>(), Arg.Any<Autheris.Domain.Options.DataSources.DataSourceTransportFlags>(), Arg.Any<System.Threading.CancellationToken>())
            .ThrowsAsync(new System.Security.SecurityException("Cross-tenant access forbidden."));

        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "user-1")], "TestAuth"));
        context.Response.Body = new MemoryStream();

        await TrinoProtocolEndpoints.HandleTrinoPoll("stmt-foreign-tenant", 0, context, statementManager, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = System.Text.Json.JsonDocument.Parse(context.Response.Body);
        doc.RootElement.GetProperty("detail").GetString().ShouldBe("Statement not found or expired.");
    }

    [Fact]
    public async Task SEC_12H_06_HandleTrinoCancelStatementRequest_SecurityException_ReturnsUniform404()
    {
        var statementManager = Substitute.For<Application.Sql.Interfaces.IWebSqlStatementManager>();
        statementManager.CancelStatementAsync(Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<Sid>(), Arg.Any<TenantId>(), Arg.Any<System.Threading.CancellationToken>())
            .ThrowsAsync(new System.Security.SecurityException("Cross-tenant access forbidden."));

        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "user-1")], "TestAuth"));
        context.Request.Headers["X-Trino-User"] = "user-1";

        await TrinoProtocolEndpoints.HandleTrinoCancel("stmt-foreign-tenant", 0, context, statementManager);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }
}
