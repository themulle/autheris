namespace Autheris.Tests.Unit.Api;

using System.IO;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Identity.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

public sealed class ScimMassChangeResultTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task WAVE3_SCIM_T02_Delete_Blocked_ReturnsScimError429WithoutCallerData()
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();

        await ScimMassChangeResult.TooManyRequests(new ScimMassChangeBlockedException(37)).ExecuteAsync(ctx);

        ctx.Response.StatusCode.ShouldBe(429);
        ctx.Response.Headers.RetryAfter.ToString().ShouldBe("37");
        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        body.ShouldContain("urn:ietf:params:scim:api:messages:2.0:Error");
        body.ShouldContain("\"status\":\"429\"");
        body.ShouldNotContain("37");
        body.ShouldNotContain("100");
    }
}
