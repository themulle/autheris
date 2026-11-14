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

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Autheris.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static Task<(bool Passed, int Status)> SendAsync(string requestScheme, string origin) =>
        SendAsync(requestScheme, origin, new GatewayOptions(), Environments.Production);

    private static async Task<(bool Passed, int Status)> SendAsync(
        string requestScheme,
        string origin,
        GatewayOptions options,
        string environmentName)
    {
        var env = new StubHostEnvironment(environmentName);
        var passed = false;
        var middleware = new GatewayCorsCsrfMiddleware(
            _ =>
            {
                passed = true;
                return Task.CompletedTask;
            },
            options,
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

    [Fact]
    public async Task Same_scheme_origin_accepted()
    {
        var (passed, _) = await SendAsync("https", "https://" + Host);

        passed.ShouldBeTrue();
    }

    [Fact]
    public async Task Http_origin_rejected_for_http_gateway_unless_insecure_transport_allowed()
    {
        var (passed, status) = await SendAsync("http", "http://" + Host);

        passed.ShouldBeFalse();
        status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Http_origin_accepted_for_http_gateway_when_insecure_transport_allowed()
    {
        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_allow_insecure_transport = true }
        };

        var (passed, _) = await SendAsync("http", "http://" + Host, options, Environments.Production);

        passed.ShouldBeTrue();
    }

    [Fact]
    public async Task Null_origin_rejected()
    {
        var (passed, status) = await SendAsync("https", "null");

        passed.ShouldBeFalse();
        status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Raw_forwarded_proto_from_untrusted_peer_ignored()
    {
        var env = new StubHostEnvironment(Environments.Production);
        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_allow_insecure_transport = true }
        };
        var passed = false;
        var csrfMiddleware = new GatewayCorsCsrfMiddleware(
            _ =>
            {
                passed = true;
                return Task.CompletedTask;
            },
            options,
            new HostAllowList([Host]),
            env);
        var forwardedOptions = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
        };
        forwardedOptions.KnownProxies.Clear();
        forwardedOptions.KnownIPNetworks.Clear();
        forwardedOptions.KnownProxies.Add(System.Net.IPAddress.Parse("10.0.0.1"));

        var forwarded = new Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersMiddleware(
            ctx => csrfMiddleware.InvokeAsync(ctx),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(forwardedOptions));

        var untrustedContext = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        untrustedContext.Response.Body = new MemoryStream();
        untrustedContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.99");
        untrustedContext.Request.Method = HttpMethods.Post;
        untrustedContext.Request.Path = "/api/v1/items";
        untrustedContext.Request.Scheme = "https";
        untrustedContext.Request.Headers["X-Forwarded-Proto"] = "http";
        untrustedContext.Request.Headers.Origin = "http://" + Host;
        untrustedContext.Request.Headers["X-Requested-With"] = "fetch";

        await forwarded.Invoke(untrustedContext);

        passed.ShouldBeFalse();
        untrustedContext.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        untrustedContext.Request.Scheme.ShouldBe("https");
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
