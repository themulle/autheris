namespace Autheris.Tests.Unit.Security;

using System.Security.Claims;
using Autheris.Api.Middleware;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// CR-ADG-08 / B-1: a tenant-id case collision denies only the colliding tenants at request time (403, stable code, audit
/// entry); every other tenant keeps working.
/// </summary>
public sealed class TenantCollisionRequestDenialTests
{
    private static GatewayOptions Colliding() => new()
    {
        WebSql = new WebSqlOptions
        {
            TenantDataSourceAllowlist = new Dictionary<string, List<string>>(StringComparer.Ordinal) { ["acme"] = ["ds"], ["ACME"] = ["ds"], ["beta"] = ["ds"] }
        }
    };

    private sealed class MutableMonitor(GatewayOptions initial) : IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue { get; set; } = initial;
        public GatewayOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }

    private static (DefaultHttpContext Context, IAuditLogRepository Audit) Request(string tenant, IOptionsMonitor<GatewayOptions>? monitor = null, bool registerOptions = true)
    {
        var audit = Substitute.For<IAuditLogRepository>();
        var services = new ServiceCollection();
        if (registerOptions) services.AddSingleton(monitor ?? new MutableMonitor(Colliding()));
        services.AddSingleton(audit);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", "S-1-5-21-USER9"),
            new Claim("tenant_id", tenant)
        }, "Bearer"));
        return (context, audit);
    }

    private static async Task<(bool NextInvoked, string Body)> Run(DefaultHttpContext context, SecurityContextResolutionMiddleware? shared = null)
    {
        bool nextInvoked = false;
        var middleware = shared ?? new SecurityContextResolutionMiddleware(_ => { nextInvoked = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context);
        context.Response.Body.Position = 0;
        return (nextInvoked, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Theory]
    [InlineData("acme")]
    [InlineData("ACME")]
    [InlineData("Acme")]   // a third spelling of a colliding group is denied as well
    public async Task CollidingTenant_IsDenied_With403_StableCode_AndAnAuditEntry(string tenant)
    {
        var (context, audit) = Request(tenant);

        var (nextInvoked, body) = await Run(context);

        nextInvoked.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        body.ShouldContain("TENANT_ID_COLLISION");
        await audit.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.TenantCollisionDenied && e.Decision == "DENY"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonCollidingTenant_IsUnaffected()
    {
        var (context, audit) = Request("beta");

        var (nextInvoked, _) = await Run(context);

        nextInvoked.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        await audit.DidNotReceiveWithAnyArgs().RecordAuditEventAsync(default!, default);
    }

    [Fact]
    public async Task AuditFailure_StillDenies()
    {
        var (context, audit) = Request("acme");
        audit.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("down")));

        var (nextInvoked, _) = await Run(context);

        nextInvoked.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Reload_ThatAddsACollision_IsDenied_WithoutRestart()
    {
        var monitor = new MutableMonitor(new GatewayOptions());
        bool nextInvoked = false;
        var middleware = new SecurityContextResolutionMiddleware(_ => { nextInvoked = true; return Task.CompletedTask; });

        var (first, _) = Request("acme", monitor);
        await Run(first, middleware);
        nextInvoked.ShouldBeTrue();
        first.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);

        monitor.CurrentValue = Colliding(); // configuration reload introduces acme/ACME
        nextInvoked = false;
        var (second, audit) = Request("acme", monitor);
        await Run(second, middleware);

        nextInvoked.ShouldBeFalse();
        second.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await audit.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.TenantCollisionDenied),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingOptions_FailClosed_With403_AndAnAuditEntry()
    {
        var (context, audit) = Request("beta", registerOptions: false);

        var (nextInvoked, body) = await Run(context);

        nextInvoked.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        body.ShouldContain("TENANT_ID_COLLISION");
        await audit.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.Decision == "DENY"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Guard_BuiltFromOptions_DeniesEverySpelling_CaseInsensitively()
    {
        var guard = TenantCollisionGuard.FromOptions(Colliding());
        guard.IsDenied("acme").ShouldBeTrue();
        guard.IsDenied("ACME").ShouldBeTrue();
        guard.IsDenied("aCmE").ShouldBeTrue();
        guard.IsDenied("beta").ShouldBeFalse();
        guard.IsDenied("").ShouldBeFalse();
    }
}
