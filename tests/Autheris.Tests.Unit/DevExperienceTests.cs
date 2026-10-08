using System.Security.Claims;
using System.Text.Json;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>
/// F-AUTH-DX: Development-only helpers (persona list/login, config info, startup banner, 403 diagnostics).
/// </summary>
public sealed class DevExperienceTests
{
    private static GatewayOptions CreateOptions(bool session = true, string password = "s3cret-pw") => new()
    {
        GovernanceDb = new GovernanceDbOptions { ConnectionString = "Data Source=secret-conn-string.db" },
        Authentication = new Autheris.Domain.Options.AuthenticationOptions
        {
            RequireKerberosOnly = false,
            BasicAuth = new BasicAuthOptions
            {
                Enabled = true,
                Session = new BasicAuthSessionOptions { Enabled = session, AllowedEnvironments = ["Development"] },
                Users =
                [
                    new BasicAuthUserConfig { Username = "owner", Password = password, Sid = "S-1-5-21-DATAOWNER-1", Roles = ["DataOwner"] },
                    new BasicAuthUserConfig { Username = "analyst-b", Password = "$pbkdf2$600000$x", TenantId = "tenant-b", Roles = ["Analyst"] }
                ]
            }
        }
    };

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static DefaultHttpContext ContextWithAuth(out IAuthenticationService auth)
    {
        auth = Substitute.For<IAuthenticationService>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(auth);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> BodyAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    // ---------- Personas ----------

    [Fact]
    public void ListPersonas_ResolvesSidTenantAndLoginUrl_WithoutPasswords()
    {
        var personas = DevEndpoints.ListPersonas(CreateOptions());

        personas.Count.ShouldBe(2);
        personas[0].Sid.ShouldBe("S-1-5-21-DATAOWNER-1");
        personas[0].LoginUrl.ShouldBe("/api/dev/login/owner");
        personas[1].Sid.ShouldBe("S-1-5-21-BASIC-ANALYST-B");
        personas[1].Tenant.ShouldBe("tenant-b");
        JsonSerializer.Serialize(personas).ShouldNotContain("s3cret-pw");
    }

    // ---------- Persona login ----------

    [Fact]
    public async Task Login_KnownPersona_IssuesSessionAndRedirectsToLocalPath()
    {
        var options = CreateOptions();
        var context = ContextWithAuth(out var auth);

        var result = await DevEndpoints.LoginAsync("OWNER", "/graphql", context, options, Env("Development"));
        await result.ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status302Found);
        context.Response.Headers.Location.ToString().ShouldBe("/graphql");
        await auth.Received(1).SignInAsync(context, BasicAuthSession.SchemeName,
            Arg.Is<ClaimsPrincipal>(p => p.Identity!.Name == "owner" && p.FindFirst(BasicAuthSession.JtiClaimType) != null),
            Arg.Any<AuthenticationProperties>());
    }

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("https://evil.example")]
    [InlineData("graphql")]
    [InlineData("/ok\r\nSet-Cookie: x=1")]
    public async Task Login_NonLocalRedirect_IsNotFollowed(string redirect)
    {
        var context = ContextWithAuth(out _);

        var result = await DevEndpoints.LoginAsync("owner", redirect, context, CreateOptions(), Env("Development"));
        await result.ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Headers.Location.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task Login_UnknownPersona_Returns404WithAvailableNames()
    {
        var context = ContextWithAuth(out var auth);

        var result = await DevEndpoints.LoginAsync("nobody", null, context, CreateOptions(), Env("Development"));
        await result.ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        (await BodyAsync(context)).ShouldContain("owner");
        await auth.DidNotReceiveWithAnyArgs().SignInAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task Login_SessionNotActive_Returns409WithReason()
    {
        var context = ContextWithAuth(out var auth);

        var result = await DevEndpoints.LoginAsync("owner", null, context, CreateOptions(session: false), Env("Development"));
        await result.ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        await auth.DidNotReceiveWithAnyArgs().SignInAsync(default!, default, default!, default);
    }

    // ---------- Routes exist only in Development ----------

    private static IReadOnlyList<string> MappedDevRoutes(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        var app = builder.Build();
        app.MapDevEndpoints(CreateOptions());
        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .Where(p => p.StartsWith("/api/dev", StringComparison.Ordinal))
            .ToList();
    }

    [Fact]
    public void DevRoutes_AreMappedInDevelopment()
    {
        MappedDevRoutes("Development").ShouldBe(["/api/dev/personas", "/api/dev/info", "/api/dev/login/{persona}"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void DevRoutes_AreNotMappedOutsideDevelopment(string environment)
    {
        MappedDevRoutes(environment).ShouldBeEmpty();
    }

    // ---------- Info ----------

    [Fact]
    public void Info_ContainsEffectiveConfiguration_ButNoSecrets()
    {
        var json = JsonSerializer.Serialize(DevEndpoints.BuildInfo(CreateOptions(), Env("Development")));

        json.ShouldContain("\"environment\":\"Development\"");
        json.ShouldContain("\"sessionActive\":true");
        json.ShouldContain("\"provider\":\"Sqlite\"");
        json.ShouldNotContain("s3cret-pw");
        json.ShouldNotContain("secret-conn-string");
        json.ShouldNotContain("pbkdf2");
    }

    // ---------- Banner ----------

    [Fact]
    public void Banner_ListsLinksAndPersonas_PasswordsHiddenByDefault_AndAllHashesMasked()
    {
        var options = CreateOptions();
        options.Authentication.BasicAuth.Users.Add(
            new BasicAuthUserConfig { Username = "argon-user", Password = "$argon2id$v=19$m=65536,t=3,p=1$abc$xyz", Roles = ["Analyst"] });

        var banner = DevStartupBanner.Build(["http://localhost:5031", "https://localhost:7214"], options, Env("Development"));

        banner.ShouldContain("https://localhost:7214/graphql");
        banner.ShouldContain("https://localhost:7214/api/dev/login/owner?redirect=/graphql");
        banner.ShouldNotContain("s3cret-pw");
        banner.ShouldContain("<hidden>");
        banner.ShouldContain("<hashed>");
        banner.ShouldNotContain("pbkdf2");
        banner.ShouldNotContain("argon2id");
    }

    [Fact]
    public void Banner_ListsLinksAndPersonas_ShowsPlaintextWhenExplicitlyEnabled()
    {
        var options = CreateOptions();
        options = new GatewayOptions
        {
            GovernanceDb = options.GovernanceDb,
            Authentication = options.Authentication,
            Dev = new DevOptions { ShowPasswords = true }
        };

        var banner = DevStartupBanner.Build(["http://localhost:5031", "https://localhost:7214"], options, Env("Development"));

        banner.ShouldContain("s3cret-pw");
        banner.ShouldContain("<hashed>");
        banner.ShouldNotContain("pbkdf2");
    }

    [Fact]
    public void Banner_SessionInactive_OmitsLoginLinks_AndNormalizesWildcardHost()
    {
        var banner = DevStartupBanner.Build(["http://0.0.0.0:5031"], CreateOptions(session: false), Env("Development"));

        banner.ShouldContain("http://localhost:5031/");
        banner.ShouldNotContain("/api/dev/login/");
        banner.ShouldContain("session cookie inactive");
    }

    // ---------- 403 diagnostics ----------

    private static DefaultHttpContext ForbiddenContext(string? policy, params string[] roles)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var claims = roles.Select(r => new Claim(ClaimTypes.Role, r)).Append(new Claim(ClaimTypes.Name, "alice")).ToList();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Basic", ClaimTypes.Name, ClaimTypes.Role));
        if (policy != null)
        {
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(new AuthorizeAttribute(policy)), "test"));
        }

        return context;
    }

    [Fact]
    public async Task AuthorizationResultHandler_Forbidden_NamesRequiredAndActualRoles()
    {
        var context = ForbiddenContext(GatewayPolicies.ClusterAdmin, "Analyst");
        var handler = new DevAuthorizationResultHandler();
        var nextCalled = false;

        await handler.HandleAsync(_ => { nextCalled = true; return Task.CompletedTask; }, context,
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(),
            PolicyAuthorizationResult.Forbid());

        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.ContentType.ShouldStartWith("application/problem+json");
        var body = await BodyAsync(context);
        body.ShouldContain("ClusterAdmin");
        body.ShouldContain("Analyst");
        body.ShouldContain("alice");
    }

    [Fact]
    public async Task AuthorizationResultHandler_Success_ContinuesPipeline()
    {
        var context = ForbiddenContext(GatewayPolicies.ClusterAdmin, "ClusterAdmin");
        var nextCalled = false;

        await new DevAuthorizationResultHandler().HandleAsync(_ => { nextCalled = true; return Task.CompletedTask; }, context,
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(),
            PolicyAuthorizationResult.Success());

        nextCalled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task ForbiddenMiddleware_BodylessForbidden_GetsProblemJson()
    {
        var context = ForbiddenContext(null, "Analyst");
        context.Request.Method = "GET";
        context.Request.Path = "/api/governance/x";
        var middleware = new DevForbiddenDiagnosticsMiddleware(c => { c.Response.StatusCode = 403; return Task.CompletedTask; });

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(403);
        context.Response.ContentType.ShouldStartWith("application/problem+json");
        var body = await BodyAsync(context);
        body.ShouldContain("/api/governance/x");
        body.ShouldContain("Analyst");
    }

    [Fact]
    public async Task ForbiddenMiddleware_ExistingBodyOrOtherStatus_IsLeftUntouched()
    {
        var withBody = ForbiddenContext(null);
        await new DevForbiddenDiagnosticsMiddleware(async c =>
        {
            c.Response.StatusCode = 403;
            c.Response.ContentType = "application/json";
            await c.Response.WriteAsync("{\"error\":\"own\"}");
        }).InvokeAsync(withBody);
        (await BodyAsync(withBody)).ShouldBe("{\"error\":\"own\"}");

        var ok = ForbiddenContext(null);
        await new DevForbiddenDiagnosticsMiddleware(_ => Task.CompletedTask).InvokeAsync(ok);
        ok.Response.StatusCode.ShouldBe(200);
        (await BodyAsync(ok)).ShouldBeEmpty();
    }

    [Fact]
    public void Banner_WithBananaCakePopEnabled_IncludesIdeLinkAndRedirectsToBcp()
    {
        var options = CreateOptions();
        options = new GatewayOptions
        {
            GovernanceDb = options.GovernanceDb,
            Authentication = options.Authentication,
            GraphQL = new GraphQLOptions
            {
                EnableBananaCakePop = true,
                BananaCakePopPath = "/ui/bcp"
            }
        };

        var banner = DevStartupBanner.Build(["https://localhost:7214"], options, Env("Development"));

        banner.ShouldContain("https://localhost:7214/ui/bcp");
        banner.ShouldContain("https://localhost:7214/api/dev/login/owner?redirect=/ui/bcp");
    }

    [Fact]
    public void GraphQLOptions_BananaCakePopPath_DefaultsToUiBcp()
    {
        var opt = new GraphQLOptions();
        opt.BananaCakePopPath.ShouldBe("/ui/bcp");
        opt.EnableBananaCakePop.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Development", true, "/graphql", false)]
    [InlineData("Development", true, "/ui/bcp", true)]
    [InlineData("Development", false, "/ui/bcp", false)]
    [InlineData("Production", true, "/ui/bcp", false)]
    public async Task CspHeader_GraphQLIsStrict_WhileBananaCakePopIsRelaxedOnlyInDev(
        string environment, bool enableBcp, string requestPath, bool expectRelaxedCsp)
    {
        const string strictCsp = "default-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self';";
        const string nitroToolCsp = "default-src 'self'; script-src 'self' 'unsafe-inline' 'unsafe-eval' blob:; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; worker-src 'self' blob:; connect-src 'self'; manifest-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self';";

        var options = new GatewayOptions
        {
            GraphQL = new GraphQLOptions
            {
                EndpointPath = "/graphql",
                EnableBananaCakePop = enableBcp,
                BananaCakePopPath = "/ui/bcp"
            }
        };

        var isDev = environment == "Development";
        var nitroToolPath = options.GraphQL.BananaCakePopPath;
        var allowNitroToolCsp = isDev && options.GraphQL.EnableBananaCakePop;

        var context = new DefaultHttpContext();
        context.Request.Path = requestPath;

        RequestDelegate next = _ => Task.CompletedTask;
        Func<HttpContext, RequestDelegate, Task> middleware = async (ctx, nxt) =>
        {
            var isToolPath = allowNitroToolCsp
                && ctx.Request.Path.StartsWithSegments(nitroToolPath, StringComparison.OrdinalIgnoreCase);
            ctx.Response.Headers.Append("Content-Security-Policy", isToolPath ? nitroToolCsp : strictCsp);
            await nxt(ctx);
        };

        await middleware(context, next);

        var csp = context.Response.Headers["Content-Security-Policy"].ToString();
        if (expectRelaxedCsp)
        {
            csp.ShouldBe(nitroToolCsp);
            csp.ShouldContain("unsafe-inline");
        }
        else
        {
            csp.ShouldBe(strictCsp);
            csp.ShouldNotContain("unsafe-inline");
        }
    }
}
