namespace Autheris.Tests.Unit.Security;

using System.IO;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CsrfOriginSchemeTests
{
    private const string Host = "gateway.example.com";

    private static async Task<(bool Passed, int Status)> SendAsync(string requestScheme, string origin)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);
        var passed = false;
        var middleware = new GatewayCorsCsrfMiddleware(
            _ =>
            {
                passed = true;
                return Task.CompletedTask;
            },
            new GatewayOptions(),
            new HostAllowList([Host]),
            env);
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/items";
        context.Request.Scheme = requestScheme;
        context.Request.Headers.Origin = origin;
        context.Request.Headers["X-Requested-With"] = "fetch";

        await middleware.InvokeAsync(context);

        return (passed, context.Response.StatusCode);
    }

    [Fact]
    public async Task Http_origin_rejected_for_https_gateway()
    {
        var (passed, status) = await SendAsync("https", "http://" + Host);

        passed.ShouldBeFalse();
        status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Same_scheme_allowlisted_origin_passes()
    {
        var (passed, _) = await SendAsync("https", "https://" + Host);

        passed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("://gateway.example.com")]
    [InlineData("gateway.example.com")]
    public async Task Malformed_origin_is_rejected(string origin)
    {
        var (passed, status) = await SendAsync("https", origin);

        passed.ShouldBeFalse();
        status.ShouldBe(StatusCodes.Status403Forbidden);
    }
}
