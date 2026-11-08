namespace Autheris.Tests.Unit.Security;

using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Autheris.Api.Endpoints;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Idempotency;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed partial class AdvisoryRegressionTests
{
    [Fact]
    public async Task Ghsa_v75c_7wm7_regression()
    {
        var store = new InMemoryIdempotencyStore();
        var sql = Substitute.For<IGovernedSqlExecutionService>();

        async Task<int> Send(string? key)
        {
            var services = new ServiceCollection().AddSingleton<Autheris.Application.Common.Interfaces.IIdempotencyStore>(store).BuildServiceProvider();
            var ctx = new DefaultHttpContext { RequestServices = services };
            ctx.Request.Path = "/api/v1/sql";
            ctx.Request.ContentType = "application/json";
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sql = "INSERT INTO t (a) VALUES (1)" }));
            ctx.Request.Body = new MemoryStream(body);
            ctx.Request.ContentLength = body.Length;
            if (key != null)
            {
                ctx.Request.Headers["Idempotency-Key"] = key;
            }

            ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user_1"), new Claim("tenant_id", "tenant_a")], "Test"));
            ctx.Response.Body = new MemoryStream();
            await WebSqlEndpoints.HandleWebSqlRequest(ctx, sql, Options.Create(new GatewayOptions()), NullLoggerFactory.Instance);
            return ctx.Response.StatusCode;
        }

        (await Send(null)).ShouldBe(400);
        (await Send("retry-1")).ShouldNotBe(409);
        var callsAfterFirst = sql.ReceivedCalls().Count();
        (await Send("retry-1")).ShouldBe(409);
        sql.ReceivedCalls().Count().ShouldBe(callsAfterFirst);
    }
}
