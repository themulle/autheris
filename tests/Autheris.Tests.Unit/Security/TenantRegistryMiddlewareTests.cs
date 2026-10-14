namespace Autheris.Tests.Unit.Security;

using Autheris.Tests.Unit.TestDoubles;
using System.Security.Claims;
using Autheris.Api.Middleware;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>CR-ADG-31 / WP-X0p: the registry check of <see cref="SecurityContextResolutionMiddleware"/>.</summary>
public sealed class TenantRegistryMiddlewareTests
{
    private sealed class Monitor(GatewayOptions options) : IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue => options;
        public GatewayOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }

    private static (DefaultHttpContext Context, IAuditLogRepository Audit) Request(string tenant, ITenantRegistry? registry, bool authenticated = true, string? issuer = "https://idp.example/", string scheme = "Bearer")
    {
        var audit = Substitute.For<IAuditLogRepository>();
        var services = new ServiceCollection();
        services.AddSingleton<IOptionsMonitor<GatewayOptions>>(new Monitor(new GatewayOptions()));
        services.AddAuditWriterOver(audit);
        if (registry != null) services.AddSingleton(registry);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        if (authenticated)
        {
            var claims = new List<Claim> { new("sub", "S-1-5-21-USER9"), new("tenant_id", tenant) };
            if (issuer is not null)
            {
                claims.Add(new Claim("iss", issuer));
            }

            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
        }

        return (context, audit);
    }

    private static async Task<(bool NextInvoked, string Body)> Run(DefaultHttpContext context)
    {
        var nextInvoked = false;
        await new SecurityContextResolutionMiddleware(_ => { nextInvoked = true; return Task.CompletedTask; }).InvokeAsync(context);
        context.Response.Body.Position = 0;
        return (nextInvoked, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private static ITenantRegistry Registry(TenantRegistration result)
    {
        var registry = Substitute.For<ITenantRegistry>();
        registry.RegisterOrGetAsync(Arg.Any<TenantId>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<TenantRegistration>(result));
        return registry;
    }

    [Theory]
    [InlineData(TenantRegistration.Registered)]
    [InlineData(TenantRegistration.Existing)]
    public async Task FirstSeenAndKnownTenant_Passes_WithTheIssuer(TenantRegistration result)
    {
        var registry = Registry(result);
        var (context, _) = Request("acme", registry);

        var (nextInvoked, _) = await Run(context);

        nextInvoked.ShouldBeTrue();
        await registry.Received(1).RegisterOrGetAsync(new TenantId("acme"), "https://idp.example/", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Bearer")]
    [InlineData("ApiKey")]
    public async Task TokenWithoutIssuer_GetsABudgetPerAuthenticationScheme(string scheme)
    {
        // Phase 5 L6: issuer-less tokens (API keys, forward auth) must not all share one bucket: the budget key is the scheme.
        var registry = Registry(TenantRegistration.Registered);
        var (context, _) = Request("acme", registry, issuer: null, scheme: scheme);

        await Run(context);

        await registry.Received(1).RegisterOrGetAsync(new TenantId("acme"), $"scheme:{scheme}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CaseVariantOfIdpOnlyTenant_Denied403Collision_WithAnAuditEntry()
    {
        var (context, audit) = Request("ACME", Registry(TenantRegistration.Collision));

        var (nextInvoked, body) = await Run(context);

        nextInvoked.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        body.ShouldContain("TENANT_ID_COLLISION");
        await audit.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.TenantCollisionDenied && e.Decision == "DENY"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CollisionWithFailingAudit_StillDenies()
    {
        var (context, audit) = Request("ACME", Registry(TenantRegistration.Collision));
        audit.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("down")));

        var (nextInvoked, _) = await Run(context);

        nextInvoked.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task RegistryUnavailable_FailsClosed503()
    {
        var registry = Substitute.For<ITenantRegistry>();
        registry.RegisterOrGetAsync(Arg.Any<TenantId>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns<ValueTask<TenantRegistration>>(_ => throw new InvalidOperationException("db down"));
        var (context, _) = Request("acme", registry);

        var (nextInvoked, body) = await Run(context);

        nextInvoked.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        body.ShouldContain("TENANT_REGISTRY_UNAVAILABLE");
        body.ShouldNotContain("db down");
    }

    [Fact]
    public async Task RateLimitedIssuer_Gets429_AndNeverReachesNext()
    {
        var (context, _) = Request("fresh", Registry(TenantRegistration.RateLimited));

        var (nextInvoked, body) = await Run(context);

        nextInvoked.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        body.ShouldContain("TENANT_REGISTRATION_RATE_LIMITED");
    }

    [Fact]
    public async Task UnauthenticatedRequest_DoesNotRegister()
    {
        var registry = Registry(TenantRegistration.Registered);
        var (context, _) = Request("acme", registry, authenticated: false);

        await Run(context);

        await registry.DidNotReceiveWithAnyArgs().RegisterOrGetAsync(default, default, default);
    }

    [Fact]
    public async Task NoRegistryConfigured_LegacyBehaviourIsUnchanged()
    {
        var (context, _) = Request("acme", registry: null);

        var (nextInvoked, _) = await Run(context);

        nextInvoked.ShouldBeTrue();
    }

    [Fact]
    public async Task ConfigurationCollision_IsDeniedBeforeTheRegistryIsAsked()
    {
        var registry = Registry(TenantRegistration.Registered);
        var (context, _) = Request("acme", registry);
        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions { TenantDataSourceAllowlist = new Dictionary<string, List<string>>(StringComparer.Ordinal) { ["acme"] = ["ds"], ["ACME"] = ["ds"] } }
        };
        context.RequestServices = new ServiceCollection()
            .AddSingleton<IOptionsMonitor<GatewayOptions>>(new Monitor(options)).AddAuditWriterOver(Substitute.For<IAuditLogRepository>()).AddSingleton(registry).BuildServiceProvider();

        var (nextInvoked, _) = await Run(context);

        nextInvoked.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await registry.DidNotReceiveWithAnyArgs().RegisterOrGetAsync(default, default, default);
    }

    // ---- M-A2: a tenant selected by a ClusterAdmin header is looked up, never registered, never marked as colliding -------------

    private static (DefaultHttpContext Context, IAuditLogRepository Audit) AdminRequest(string headerTenant, ITenantRegistry registry)
    {
        var (context, audit) = Request("home", registry);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "S-1-5-21-ADMIN1"), new Claim("tenant_id", "home"), new Claim("iss", "https://idp.example/"), new Claim(ClaimTypes.Role, "ClusterAdmin")], "Bearer"));
        context.Request.Headers["X-Tenant-ID"] = headerTenant;
        return (context, audit);
    }

    private static ITenantRegistry LookupOnly(TenantRegistration? lookup)
    {
        var registry = Substitute.For<ITenantRegistry>();
        registry.TryGetAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<TenantRegistration?>(lookup));
        registry.RegisterOrGetAsync(Arg.Any<TenantId>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<TenantRegistration>(TenantRegistration.Collision));
        return registry;
    }

    [Fact]
    public async Task ClusterAdminHeaderTenant_Known_PassesWithLookupOnly()
    {
        var registry = LookupOnly(TenantRegistration.Existing);
        var (context, _) = AdminRequest("other", registry);

        var (nextInvoked, _) = await Run(context);

        nextInvoked.ShouldBeTrue();
        await registry.Received(1).TryGetAsync(new TenantId("other"), Arg.Any<CancellationToken>());
        await registry.DidNotReceiveWithAnyArgs().RegisterOrGetAsync(default, default, default);
    }

    [Fact]
    public async Task ClusterAdminHeaderTenant_Unknown_Denied403_NothingRegistered()
    {
        var registry = LookupOnly(null);
        var (context, _) = AdminRequest("brandnew", registry);

        var (nextInvoked, body) = await Run(context);

        nextInvoked.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        body.ShouldContain("TENANT_UNKNOWN");
        await registry.DidNotReceiveWithAnyArgs().RegisterOrGetAsync(default, default, default);
    }

    [Fact]
    public async Task ClusterAdminHeaderTenant_CaseVariantOfAKnownTenant_Denied_AndNeverMarksACollision()
    {
        // the lookup reports "unknown" for another spelling of a known group: the header can never create the collision mark
        var registry = LookupOnly(null);
        var (context, _) = AdminRequest("ACME", registry);

        var (nextInvoked, _) = await Run(context);

        nextInvoked.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await registry.DidNotReceiveWithAnyArgs().RegisterOrGetAsync(default, default, default);
    }

    [Fact]
    public async Task ClusterAdminHeaderTenant_Collided_Denied403()
    {
        var registry = LookupOnly(TenantRegistration.Collision);
        var (context, _) = AdminRequest("acme", registry);

        var (nextInvoked, body) = await Run(context);

        nextInvoked.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        body.ShouldContain("TENANT_ID_COLLISION");
        await registry.DidNotReceiveWithAnyArgs().RegisterOrGetAsync(default, default, default);
    }

    [Fact]
    public async Task ClusterAdminHeaderTenant_RegistryUnavailable_FailsClosed503()
    {
        var registry = Substitute.For<ITenantRegistry>();
        registry.TryGetAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>()).Returns<ValueTask<TenantRegistration?>>(_ => throw new InvalidOperationException("db down"));
        var (context, _) = AdminRequest("other", registry);

        var (nextInvoked, _) = await Run(context);

        nextInvoked.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task ClusterAdminWithoutHeader_UsesTheClaimTenant_ThroughRegistration()
    {
        var registry = Registry(TenantRegistration.Existing);
        var (context, _) = AdminRequest("home", registry);   // header equals the claim: not a header-selected tenant

        var (nextInvoked, _) = await Run(context);

        nextInvoked.ShouldBeTrue();
        await registry.Received(1).RegisterOrGetAsync(new TenantId("home"), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
