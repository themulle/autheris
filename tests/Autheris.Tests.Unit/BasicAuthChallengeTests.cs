using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public sealed class BasicAuthChallengeTests
{
    [Fact]
    public async Task BasicAuthHandler_Challenge_SetsWwwAuthenticateHeader_AndReturnsHtmlForBrowsers()
    {
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                BasicAuth = new BasicAuthOptions
                {
                    Enabled = true,
                    Realm = "Autheris-Test"
                }
            }
        };

        var mockEnv = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Development");

        var schemeMonitor = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        schemeMonitor.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());

        var handler = new BasicAuthenticationHandler(
            schemeMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            Options.Create(options),
            mockEnv);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Accept = "text/html,application/xhtml+xml";
        httpContext.Response.Body = new MemoryStream();

        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.Basic, "Basic", typeof(BasicAuthenticationHandler)), httpContext);

        await handler.ChallengeAsync(new AuthenticationProperties());

        httpContext.Response.StatusCode.ShouldBe(401);
        httpContext.Response.Headers.WWWAuthenticate.ToString().ShouldBe("Basic realm=\"Autheris-Test\"");
        httpContext.Response.ContentType.ShouldNotBeNull();
        httpContext.Response.ContentType.ShouldContain("text/html");

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(httpContext.Response.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        body.ShouldContain("401 Unauthorized");
        body.ShouldContain("Autheris-Test");
        body.ShouldContain("BasicAuth.Session");
    }

    [Fact]
    public async Task BasicAuthHandler_Challenge_InDevelopment_ReturnsJsonDiagnosticsForApiClients()
    {
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                BasicAuth = new BasicAuthOptions
                {
                    Enabled = true,
                    Realm = "Autheris"
                },
                ForwardAuth = new ForwardAuthOptions
                {
                    Enabled = true
                }
            }
        };

        var mockEnv = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Development");

        var schemeMonitor = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        schemeMonitor.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());

        var handler = new BasicAuthenticationHandler(
            schemeMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            Options.Create(options),
            mockEnv);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Accept = "application/json";
        httpContext.Response.Body = new MemoryStream();

        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.Basic, "Basic", typeof(BasicAuthenticationHandler)), httpContext);

        await handler.ChallengeAsync(new AuthenticationProperties());

        httpContext.Response.StatusCode.ShouldBe(401);
        httpContext.Response.Headers.WWWAuthenticate.ToString().ShouldBe("Basic realm=\"Autheris\"");
        httpContext.Response.ContentType.ShouldNotBeNull();
        httpContext.Response.ContentType.ShouldContain("application/problem+json");

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(httpContext.Response.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        body.ShouldContain("active_schemes");
        body.ShouldContain("Basic");
        body.ShouldContain("ForwardAuth");
    }

    private static async Task<(DefaultHttpContext Context, string Body)> ChallengeAsync(
        string environment,
        Action<DefaultHttpContext> configureRequest,
        GatewayOptions? options = null)
    {
        options ??= new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                BasicAuth = new BasicAuthOptions { Enabled = true, Realm = "Autheris" }
            }
        };

        var env = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.EnvironmentName.Returns(environment);
        var schemeMonitor = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        schemeMonitor.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        var handler = new BasicAuthenticationHandler(schemeMonitor, NullLoggerFactory.Instance, UrlEncoder.Default, Options.Create(options), env);

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        configureRequest(httpContext);
        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.Basic, "Basic", typeof(BasicAuthenticationHandler)), httpContext);
        await handler.ChallengeAsync(new AuthenticationProperties());

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(httpContext.Response.Body, Encoding.UTF8);
        return (httpContext, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task BasicAuthHandler_Challenge_ScriptRequest_Gets401WithoutBasicChallenge()
    {
        var (context, _) = await ChallengeAsync("Development", c =>
        {
            c.Request.Headers.Accept = "text/html";
            c.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        });

        context.Response.StatusCode.ShouldBe(401);
        context.Response.Headers.ContainsKey("WWW-Authenticate").ShouldBeFalse();
    }

    [Fact]
    public async Task BasicAuthHandler_Challenge_InProduction_DoesNotLeakDiagnostics()
    {
        var (context, body) = await ChallengeAsync("Production", c => c.Request.Headers.Accept = "application/json");

        context.Response.StatusCode.ShouldBe(401);
        context.Response.Headers.WWWAuthenticate.ToString().ShouldBe("Basic realm=\"Autheris\"");
        body.ShouldBeEmpty();
    }

    [Fact]
    public async Task BasicAuthHandler_Challenge_HtmlInProduction_HasNoDevelopmentTip()
    {
        var (_, body) = await ChallengeAsync("Production", c => c.Request.Headers.Accept = "text/html");

        body.ShouldContain("401 Unauthorized");
        body.ShouldNotContain("Development Tip");
    }

    [Fact]
    public async Task BasicAuthHandler_Challenge_HtmlEncodesRealm()
    {
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                BasicAuth = new BasicAuthOptions { Enabled = true, Realm = "<script>x</script>" }
            }
        };

        var (_, body) = await ChallengeAsync("Development", c => c.Request.Headers.Accept = "text/html", options);

        body.ShouldNotContain("<script>x</script>");
        body.ShouldContain("&lt;script&gt;");
    }
}
