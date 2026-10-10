namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class SecurityContextFactoryTests
{
    [Fact]
    public void UnauthenticatedContext_ReturnsAnonymousContext()
    {
        var httpContext = new DefaultHttpContext();

        var ctx = SecurityContextFactory.CreateFromHttpContext(httpContext);

        ctx.ShouldNotBeNull();
        ctx.IsAuthenticated.ShouldBeFalse();
        ctx.UserSid.Value.ShouldBe("ANONYMOUS");
        ctx.TenantId.ShouldBe(TenantId.LegacySingleTenant);
    }

    [Fact]
    public void AuthenticatedContext_WithTenantClaim_ResolvesCorrectly()
    {
        var httpContext = new DefaultHttpContext();
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", "S-1-5-21-USER1"),
            new Claim("tenant_id", "tenant-alpha"),
            new Claim(ClaimTypes.Role, "DataViewer")
        }, "Bearer");
        httpContext.User = new ClaimsPrincipal(identity);

        var ctx = SecurityContextFactory.CreateFromHttpContext(httpContext);

        ctx.ShouldNotBeNull();
        ctx.IsAuthenticated.ShouldBeTrue();
        ctx.UserSid.Value.ShouldBe("S-1-5-21-USER1");
        ctx.TenantId.Value.ShouldBe("tenant-alpha");
        ctx.TenantRoles.ShouldContain("DataViewer");
        ctx.IsClusterAdmin.ShouldBeFalse();
    }

    [Fact]
    public void CrossTenantHeader_MatchingClaim_Succeeds()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = "tenant-alpha";
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", "S-1-5-21-USER1"),
            new Claim("tenant_id", "tenant-alpha"),
            new Claim(ClaimTypes.Role, "GatewayAdmin")
        }, "Bearer");
        httpContext.User = new ClaimsPrincipal(identity);

        var ctx = SecurityContextFactory.CreateFromHttpContext(httpContext);

        ctx.TenantId.Value.ShouldBe("tenant-alpha");
        ctx.TenantRoles.ShouldContain("GatewayAdmin");
    }

    [Fact]
    public void CrossTenantHeader_MismatchingClaim_NonClusterAdmin_ThrowsSecurityException()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = "tenant-beta";
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", "S-1-5-21-USER1"),
            new Claim("tenant_id", "tenant-alpha"),
            new Claim(ClaimTypes.Role, "GatewayAdmin") // Tenant admin cannot cross tenant boundaries
        }, "Bearer");
        httpContext.User = new ClaimsPrincipal(identity);

        var ex = Should.Throw<SecurityException>(() => SecurityContextFactory.CreateFromHttpContext(httpContext));
        ex.Message.ShouldContain("Cross-tenant access forbidden");
    }

    [Fact]
    public void CrossTenantHeader_MismatchingClaim_ClusterAdmin_Allowed()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = "tenant-beta";
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", "S-1-5-21-ADMIN"),
            new Claim("tenant_id", "tenant-alpha"),
            new Claim(ClaimTypes.Role, "ClusterAdmin")
        }, "Bearer");
        httpContext.User = new ClaimsPrincipal(identity);

        var ctx = SecurityContextFactory.CreateFromHttpContext(httpContext);

        ctx.TenantId.Value.ShouldBe("tenant-beta");
        ctx.IsClusterAdmin.ShouldBeTrue();
    }

    [Fact]
    public void ForwardAuth_ClusterAdminRole_IgnoredInClusterScope()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = "tenant-beta";
        // User authenticated via ForwardAuth claims ClusterAdmin in header
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", "S-1-5-21-FORWARD-USER"),
            new Claim("tenant_id", "tenant-alpha"),
            new Claim(ClaimTypes.Role, "ClusterAdmin")
        }, "ForwardAuth");
        httpContext.User = new ClaimsPrincipal(identity);

        // SecurityContextFactory drops ClusterAdmin when scheme is ForwardAuth
        var ex = Should.Throw<SecurityException>(() => SecurityContextFactory.CreateFromHttpContext(httpContext));
        ex.Message.ShouldContain("Cross-tenant access forbidden");
    }

    [Fact]
    public async Task SecurityContextResolutionMiddleware_SetsBothContextItemKeys()
    {
        var httpContext = new DefaultHttpContext();
        var services = new ServiceCollection();
        var monitor = NSubstitute.Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<Autheris.Domain.Options.GatewayOptions>>();
        monitor.CurrentValue.Returns(new Autheris.Domain.Options.GatewayOptions());
        services.AddSingleton(monitor);
        httpContext.RequestServices = services.BuildServiceProvider();
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", "S-1-5-21-USER2"),
            new Claim("tenant_id", "tenant-gamma")
        }, "Bearer");
        httpContext.User = new ClaimsPrincipal(identity);

        bool nextInvoked = false;
        RequestDelegate next = (ctx) =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        };

        var middleware = new SecurityContextResolutionMiddleware(next);
        await middleware.InvokeAsync(httpContext);

        nextInvoked.ShouldBeTrue();
        httpContext.Items.ContainsKey(SecurityPrincipalContext.ItemKey).ShouldBeTrue();
        httpContext.Items[SecurityPrincipalContext.ItemKey].ShouldBeOfType<SecurityPrincipalContext>();
        var secCtx = (SecurityPrincipalContext)httpContext.Items[SecurityPrincipalContext.ItemKey]!;
        secCtx.TenantId.Value.ShouldBe("tenant-gamma");

        httpContext.Items.ContainsKey(TenantResolutionMiddleware.TenantIdItemKey).ShouldBeTrue();
        var legacyTenant = (TenantId)httpContext.Items[TenantResolutionMiddleware.TenantIdItemKey]!;
        legacyTenant.Value.ShouldBe("tenant-gamma");
    }

    [Fact]
    public void RV_02_AuthenticatedWithoutSid_FailsClosed_NoDisplayNameFallback()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "Alice Display Name"),
            new Claim("tenant_id", "tenant-alpha")
        }, "Bearer", ClaimTypes.Name, ClaimTypes.Role));

        Should.Throw<UnauthorizedAccessException>(() => SecurityContextFactory.CreateFromHttpContext(httpContext));
    }

    [Theory]
    [InlineData("GatewayAdmin")]
    [InlineData("PlatformAdmin")]
    [InlineData("tenant-alpha:ClusterAdmin")]
    public void RV_03_AdminAliasesAndPrefixedRoles_AreNotClusterAdmins(string role)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = "tenant-beta";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", "S-1-5-21-USER9"),
            new Claim("tenant_id", "tenant-alpha"),
            new Claim(ClaimTypes.Role, role)
        }, "Bearer"));

        Should.Throw<SecurityException>(() => SecurityContextFactory.CreateFromHttpContext(httpContext));
        ClusterAdminPolicy.IsCanonicalClusterAdmin(httpContext.User).ShouldBeFalse();
    }

    [Fact]
    public void RV_03_ClusterAdminAssertedViaForwardAuth_IsNeverClusterAdmin()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", "S-1-5-21-FORWARD-BOB"),
            new Claim(ClaimTypes.Role, "ClusterAdmin")
        }, ClusterAdminPolicy.ForwardAuthScheme));

        ClusterAdminPolicy.IsCanonicalClusterAdmin(principal).ShouldBeFalse();
    }
}
