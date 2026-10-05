using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Domain.Options;
using Autheris.GraphQL.Subscriptions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>
/// F-AUTH-DX: Basic auth with cookie session (concept: konzept-basic-auth-dev-session-2026-10-05).
/// </summary>
public sealed class BasicAuthSessionTests
{
    private const string CookieName = "__Host-Autheris.Session";

    private static GatewayOptions CreateOptions(
        bool sessionEnabled = true,
        List<string>? allowedEnvironments = null,
        string password = "dev",
        List<string>? roles = null,
        string cookieName = CookieName,
        bool multiNode = false,
        string? keyDirectory = null,
        List<string>? trustedOrigins = null,
        string? sid = null)
    {
        return new GatewayOptions
        {
            HighAvailability = new HighAvailabilityOptions { MultiNodeClusterMode = multiNode },
            GraphQL = new GraphQLOptions { TrustedOrigins = trustedOrigins ?? [] },
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                RequireKerberosOnly = false,
                BasicAuth = new BasicAuthOptions
                {
                    Enabled = true,
                    Session = new BasicAuthSessionOptions
                    {
                        Enabled = sessionEnabled,
                        AllowedEnvironments = allowedEnvironments ?? ["Development"],
                        CookieName = cookieName,
                        KeyDirectory = keyDirectory
                    },
                    Users =
                    [
                        new BasicAuthUserConfig
                        {
                            Username = "alice",
                            Password = password,
                            Sid = sid,
                            TenantId = "tenant-a",
                            Roles = roles ?? ["Analyst"]
                        }
                    ]
                }
            }
        };
    }

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private sealed class MutableOptions<T>(T value) : IOptions<T> where T : class
    {
        public T Value { get; set; } = value;
    }

    private static (ServiceProvider Provider, MutableOptions<GatewayOptions> Options) CreateServices(GatewayOptions options)
    {
        var holder = new MutableOptions<GatewayOptions>(options);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<GatewayOptions>>(holder);
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<BasicAuthSessionCookieEvents>();
        services.AddAuthentication()
            .AddCookie(BasicAuthSession.SchemeName, c => BasicAuthSession.ConfigureCookie(c, options.Authentication.BasicAuth.Session));
        return (services.BuildServiceProvider(validateScopes: true), holder);
    }

    private static DefaultHttpContext CreateContext(IServiceProvider services)
    {
        // Authentication services are scoped (handlers cache per request): one scope per simulated request.
        var context = new DefaultHttpContext { RequestServices = services.CreateScope().ServiceProvider };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost:7214");
        context.Request.Path = "/api/v1/queries";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static ClaimsPrincipal BasicPrincipal(string name = "alice") =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], GatewayAuthSchemes.Basic, ClaimTypes.Name, ClaimTypes.Role));

    private static async Task<string?> LoginAndGetCookieAsync(ServiceProvider services, GatewayOptions options)
    {
        var context = CreateContext(services);
        context.User = BasicPrincipal();
        var middleware = new BasicAuthSessionMiddleware(_ => Task.CompletedTask, Options.Create(options), Env("Development"),
            NullLogger<BasicAuthSessionMiddleware>.Instance);
        await middleware.InvokeAsync(context);

        var setCookie = context.Response.Headers.SetCookie.ToString();
        var start = setCookie.IndexOf(CookieName + "=", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var end = setCookie.IndexOf(';', start);
        return setCookie[start..end];
    }

    // ---------- Startup guards ----------

    [Fact]
    public void Validate_AllowedDevelopment_HasNoErrors()
    {
        BasicAuthSession.Validate(CreateOptions(), Env("Development")).ShouldBeEmpty();
        BasicAuthSession.IsAllowed(CreateOptions(), Env("Development")).ShouldBeTrue();
    }

    [Fact]
    public void Validate_Production_IsAlwaysRejected_EvenIfAllowlisted()
    {
        var options = CreateOptions(allowedEnvironments: ["Development", "Production"]);

        BasicAuthSession.Validate(options, Env("Production")).ShouldNotBeEmpty();
        BasicAuthSession.Validate(options, Env("Development")).ShouldNotBeEmpty();
        BasicAuthSession.IsAllowed(options, Env("Production")).ShouldBeFalse();
    }

    [Fact]
    public void Validate_EnvironmentNotAllowlisted_IsRejected()
    {
        BasicAuthSession.Validate(CreateOptions(), Env("Integration")).ShouldNotBeEmpty();
        BasicAuthSession.Validate(CreateOptions(allowedEnvironments: ["Integration"]), Env("Integration")).ShouldBeEmpty();
        BasicAuthSession.IsAllowed(CreateOptions(), Env("Staging")).ShouldBeFalse();
    }

    [Fact]
    public void Validate_WildcardTrustedOrigin_FailsOutsideDevelopment_DisablesSessionInDevelopment()
    {
        var options = CreateOptions(allowedEnvironments: ["Development", "Integration"], trustedOrigins: ["*"]);

        BasicAuthSession.Validate(options, Env("Integration")).ShouldNotBeEmpty();
        BasicAuthSession.Validate(options, Env("Development")).ShouldBeEmpty();
        BasicAuthSession.IsAllowed(options, Env("Development")).ShouldBeFalse();
        BasicAuthSession.GetInactiveReason(options, Env("Development")).ShouldNotBeNull();
    }

    [Fact]
    public void Validate_CookieWithoutHostPrefix_IsRejected()
    {
        BasicAuthSession.Validate(CreateOptions(cookieName: "autheris"), Env("Development")).ShouldNotBeEmpty();
    }

    [Fact]
    public void Validate_MultiNodeWithoutSharedKeyDirectory_IsRejected()
    {
        BasicAuthSession.Validate(CreateOptions(multiNode: true), Env("Development")).ShouldNotBeEmpty();
        BasicAuthSession.Validate(CreateOptions(multiNode: true, keyDirectory: "/shared/keys"), Env("Development")).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_SessionDisabled_HasNoErrors()
    {
        BasicAuthSession.Validate(CreateOptions(sessionEnabled: false), Env("Production")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ITSM_SNOW:prod:alice")]
    [InlineData("S-1-5-21-ITSM-1")]
    [InlineData("itsm-user")]
    [InlineData("S-1-5-21-X:alice@corp.local")]
    public void ValidateUsers_RejectsDangerousSids(string sid)
    {
        BasicAuthSession.ValidateUsers(CreateOptions(sid: sid).Authentication.BasicAuth).ShouldNotBeEmpty();
    }

    [Fact]
    public void ValidateUsers_RejectsEmptyPassword_AcceptsDefaultSid()
    {
        BasicAuthSession.ValidateUsers(CreateOptions(password: "").Authentication.BasicAuth).ShouldNotBeEmpty();
        BasicAuthSession.ValidateUsers(CreateOptions().Authentication.BasicAuth).ShouldBeEmpty();
    }

    // ---------- Cookie issuance and validation ----------

    [Fact]
    public async Task BasicLogin_IssuesHardenedSessionCookie()
    {
        var options = CreateOptions();
        var (services, _) = CreateServices(options);
        var context = CreateContext(services);
        context.User = BasicPrincipal();

        var middleware = new BasicAuthSessionMiddleware(_ => Task.CompletedTask, Options.Create(options), Env("Development"),
            NullLogger<BasicAuthSessionMiddleware>.Instance);
        await middleware.InvokeAsync(context);

        var setCookie = context.Response.Headers.SetCookie.ToString();
        setCookie.ShouldContain(CookieName + "=");
        setCookie.ShouldContain("httponly");
        setCookie.ShouldContain("secure");
        setCookie.ShouldContain("samesite=strict");
        setCookie.ShouldContain("path=/");
        setCookie.ShouldNotContain("domain=");
    }

    [Fact]
    public async Task NoSessionHeader_OrNonBasicIdentity_DoesNotIssueCookie()
    {
        var options = CreateOptions();
        var (services, _) = CreateServices(options);
        var middleware = new BasicAuthSessionMiddleware(_ => Task.CompletedTask, Options.Create(options), Env("Development"),
            NullLogger<BasicAuthSessionMiddleware>.Instance);

        var optOut = CreateContext(services);
        optOut.User = BasicPrincipal();
        optOut.Request.Headers[BasicAuthSession.NoSessionHeader] = "1";
        await middleware.InvokeAsync(optOut);
        optOut.Response.Headers.SetCookie.ToString().ShouldBeEmpty();

        var bearer = CreateContext(services);
        bearer.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], GatewayAuthSchemes.JwtBearer));
        await middleware.InvokeAsync(bearer);
        bearer.Response.Headers.SetCookie.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task SessionDisabledOutsideAllowlist_DoesNotIssueCookie()
    {
        var options = CreateOptions();
        var (services, _) = CreateServices(options);
        var context = CreateContext(services);
        context.User = BasicPrincipal();

        var middleware = new BasicAuthSessionMiddleware(_ => Task.CompletedTask, Options.Create(options), Env("Staging"),
            NullLogger<BasicAuthSessionMiddleware>.Instance);
        await middleware.InvokeAsync(context);

        context.Response.Headers.SetCookie.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task SessionCookie_RoundTrip_AuthenticatesWithConfiguredClaims()
    {
        var options = CreateOptions();
        var (services, _) = CreateServices(options);
        var cookie = await LoginAndGetCookieAsync(services, options);
        cookie.ShouldNotBeNull();

        var context = CreateContext(services);
        context.Request.Headers.Cookie = cookie;
        var result = await context.AuthenticateAsync(BasicAuthSession.SchemeName);

        result.Succeeded.ShouldBeTrue(result.Failure?.ToString() ?? "No failure exception");
        var principal = result.Principal!;
        principal.Identity!.AuthenticationType.ShouldBe(BasicAuthSession.SchemeName);
        principal.Identity.Name.ShouldBe("alice");
        principal.FindFirst("tenant_id")!.Value.ShouldBe("tenant-a");
        principal.FindFirst(ClaimTypes.PrimarySid)!.Value.ShouldBe("S-1-5-21-BASIC-ALICE");
        principal.FindFirst(BasicAuthSession.AuthMethodClaimType)!.Value.ShouldBe(BasicAuthSession.AuthMethodSession);
        principal.FindFirst(BasicAuthSession.JtiClaimType).ShouldNotBeNull();
        principal.FindFirst(BasicAuthSession.IssuedAtClaimType).ShouldNotBeNull();
        BasicAuthSession.IsSessionPrincipal(principal).ShouldBeTrue();
    }

    [Fact]
    public async Task SessionCookie_RoleChange_IsAppliedOnNextRequest()
    {
        var options = CreateOptions(roles: ["Analyst"]);
        var (services, holder) = CreateServices(options);
        var cookie = await LoginAndGetCookieAsync(services, options);

        holder.Value = CreateOptions(roles: ["DataOwner"]);

        var context = CreateContext(services);
        context.Request.Headers.Cookie = cookie;
        var result = await context.AuthenticateAsync(BasicAuthSession.SchemeName);

        result.Succeeded.ShouldBeTrue();
        result.Principal!.IsInRole("DataOwner").ShouldBeTrue();
        result.Principal.IsInRole("Analyst").ShouldBeFalse();
    }

    [Fact]
    public async Task SessionCookie_PasswordChange_InvalidatesSession()
    {
        var options = CreateOptions(password: "old");
        var (services, holder) = CreateServices(options);
        var cookie = await LoginAndGetCookieAsync(services, options);

        holder.Value = CreateOptions(password: "new");

        var context = CreateContext(services);
        context.Request.Headers.Cookie = cookie;
        var result = await context.AuthenticateAsync(BasicAuthSession.SchemeName);

        result.Succeeded.ShouldBeFalse();
        context.Response.Headers.SetCookie.ToString().ShouldContain("expires=Thu, 01 Jan 1970");
    }

    [Fact]
    public async Task SessionCookie_UserRemoved_InvalidatesSession()
    {
        var options = CreateOptions();
        var (services, holder) = CreateServices(options);
        var cookie = await LoginAndGetCookieAsync(services, options);

        holder.Value = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                RequireKerberosOnly = false,
                BasicAuth = new BasicAuthOptions { Enabled = true, Session = options.Authentication.BasicAuth.Session }
            }
        };

        var context = CreateContext(services);
        context.Request.Headers.Cookie = cookie;
        (await context.AuthenticateAsync(BasicAuthSession.SchemeName)).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task SessionCookie_TamperedValue_IsRejected()
    {
        var options = CreateOptions();
        var (services, _) = CreateServices(options);
        var cookie = await LoginAndGetCookieAsync(services, options);

        var context = CreateContext(services);
        context.Request.Headers.Cookie = cookie![..^4] + "AAAA";
        (await context.AuthenticateAsync(BasicAuthSession.SchemeName)).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task ExistingCookieOfSameUser_IsNotReissued()
    {
        var options = CreateOptions();
        var (services, _) = CreateServices(options);
        var cookie = await LoginAndGetCookieAsync(services, options);
        var middleware = new BasicAuthSessionMiddleware(_ => Task.CompletedTask, Options.Create(options), Env("Development"),
            NullLogger<BasicAuthSessionMiddleware>.Instance);

        var same = CreateContext(services);
        same.Request.Headers.Cookie = cookie;
        same.User = BasicPrincipal("alice");
        await middleware.InvokeAsync(same);
        same.Response.Headers.SetCookie.ToString().ShouldBeEmpty();
    }

    // ---------- Basic handler ----------

    [Fact]
    public async Task BasicHandler_HealthPath_SkipsAuthentication()
    {
        var options = Options.Create(CreateOptions());
        var handler = new BasicAuthenticationHandler(
            new StaticOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            options);

        var context = new DefaultHttpContext();
        context.Request.Path = "/health/ready";
        context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:wrong"));
        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.Basic, "Basic", typeof(BasicAuthenticationHandler)), context);

        var result = await handler.AuthenticateAsync();
        result.None.ShouldBeTrue();
    }

    [Fact]
    public async Task BasicHandler_Success_UsesSharedClaimsAndAuthMethod()
    {
        var options = Options.Create(CreateOptions());
        var env = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var handler = new BasicAuthenticationHandler(
            new StaticOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            options,
            env);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/auth/session";
        context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ALICE:dev"));
        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.Basic, "Basic", typeof(BasicAuthenticationHandler)), context);

        var result = await handler.AuthenticateAsync();
        result.Succeeded.ShouldBeTrue();
        result.Principal!.Identity!.Name.ShouldBe("alice");
        result.Principal.FindFirst("tenant_id")!.Value.ShouldBe("tenant-a");
        result.Principal.FindFirst(BasicAuthSession.AuthMethodClaimType)!.Value.ShouldBe(BasicAuthSession.AuthMethodBasic);
    }

    // ---------- WebSocket ----------

    [Theory]
    [InlineData("https://localhost:7214", true)]
    [InlineData("http://localhost:7214", false)]
    [InlineData("https://devtools.corp.local", true)]
    [InlineData("http://devtools.corp.local", false)]
    [InlineData("https://evil.example", false)]
    public void WebSocket_SessionCookie_RequiresTrustedOrigin(string origin, bool expected)
    {
        var options = CreateOptions(trustedOrigins: ["https://devtools.corp.local"]);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost:7214");
        context.Request.Headers.Origin = origin;
        var session = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], BasicAuthSession.SchemeName));

        WebSocketAuthInterceptor.IsSessionCookieFromTrustedOrigin(context, session, options).ShouldBe(expected);
    }

    [Fact]
    public void WebSocket_AmbientBasicOrMissingOptions_IsNotAccepted()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost:7214");
        context.Request.Headers.Origin = "https://localhost:7214";
        var basic = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], GatewayAuthSchemes.Basic));
        var session = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], BasicAuthSession.SchemeName));

        WebSocketAuthInterceptor.IsSessionCookieFromTrustedOrigin(context, basic, CreateOptions()).ShouldBeFalse();
        WebSocketAuthInterceptor.IsSessionCookieFromTrustedOrigin(context, session, null).ShouldBeFalse();
        BasicAuthSessionOptions.SchemeName.ShouldBe(BasicAuthSession.SchemeName);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
