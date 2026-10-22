namespace Autheris.Tests.Unit.Plugins.Auth;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Application.Plugins.Auth;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Plugins;
using Autheris.Domain.Plugins.Auth;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Plugins.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class AuthPluginRoleProviderTests
{
    private sealed class StubRoleProvider(string name, PluginRoleEnrichmentResult result, TimeSpan? delay = null, bool throwException = false)
        : IPluginRoleProvider
    {
        public string Name => name;

        public async ValueTask<PluginRoleEnrichmentResult> EnrichRolesAsync(PluginSecurityContext context, CancellationToken ct)
        {
            if (delay.HasValue)
            {
                await Task.Delay(delay.Value, ct).ConfigureAwait(false);
            }

            if (throwException)
            {
                throw new InvalidOperationException($"Fault from {name}");
            }

            return result;
        }
    }

    private static (AuthPluginHost Host, GatewayOptions Options, FakeTimeProvider TimeProvider) CreateHost(
        Action<AuthPluginOptions>? configureAuth = null)
    {
        var options = new GatewayOptions();
        configureAuth?.Invoke(options.Plugins.Auth);

        var timeProvider = new FakeTimeProvider();
        var optionsWrapper = Options.Create(options);

        var host = new AuthPluginHost(
            optionsWrapper,
            NullLogger<AuthPluginHost>.Instance,
            timeProvider: timeProvider);

        return (host, options, timeProvider);
    }

    private static PluginSecurityContext CreateTestContext(string tenant = "tenant-alpha", string user = "user-1") =>
        new()
        {
            TenantId = new TenantId(tenant),
            UserSid = new Sid(user),
            GroupSids = new HashSet<Sid> { new("group-base") },
            ExistingTenantRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Analyst" },
            Claims = [new Claim("sub", user)],
            AuthenticationScheme = "Bearer"
        };

    [Fact]
    public async Task EnrichRolesAsync_WhenSingleRoleProviderSucceeds_EnrichesRolesAndGroupSids()
    {
        var (host, _, _) = CreateHost();
        var provider = new StubRoleProvider("ad-provider", new PluginRoleEnrichmentResult(
            addedRoles: ["FinanceViewer", "RiskViewer"],
            addedGroupSids: [new Sid("S-1-5-21-100-200-300-1001")]));

        host.RegisterRoleProvider(provider);

        var result = await host.EnrichRolesAsync(CreateTestContext(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Result.AddedRoles.ShouldContain("FinanceViewer");
        result.Result.AddedRoles.ShouldContain("RiskViewer");
        result.Result.AddedGroupSids.ShouldContain(new Sid("S-1-5-21-100-200-300-1001"));
    }

    [Fact]
    public async Task EnrichRolesAsync_WhenMultipleRoleProviders_MergesAllRolesAndGroupSids()
    {
        var (host, _, _) = CreateHost();
        host.RegisterRoleProvider(new StubRoleProvider("p1", new PluginRoleEnrichmentResult(["RoleA"], [new Sid("S-1-5-21-1-1")])));
        host.RegisterRoleProvider(new StubRoleProvider("p2", new PluginRoleEnrichmentResult(["RoleB"], [new Sid("S-1-5-21-1-2")])));

        var result = await host.EnrichRolesAsync(CreateTestContext(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Result.AddedRoles.Count.ShouldBe(2);
        result.Result.AddedRoles.ShouldContain("RoleA");
        result.Result.AddedRoles.ShouldContain("RoleB");
        result.Result.AddedGroupSids.Count.ShouldBe(2);
    }

    [Fact]
    public async Task EnrichRolesAsync_WhenEmptyResult_ReturnsEmptyWithoutError()
    {
        var (host, _, _) = CreateHost();
        host.RegisterRoleProvider(new StubRoleProvider("empty", PluginRoleEnrichmentResult.Empty));

        var result = await host.EnrichRolesAsync(CreateTestContext(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Result.AddedRoles.ShouldBeEmpty();
        result.Result.AddedGroupSids.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnrichRolesAsync_WhenProviderThrows_ReturnsFailedResult()
    {
        var (host, _, _) = CreateHost();
        host.RegisterRoleProvider(new StubRoleProvider("failing", PluginRoleEnrichmentResult.Empty, throwException: true));

        var result = await host.EnrichRolesAsync(CreateTestContext(), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("AUTH_PLUGIN_FAILED");
    }

    [Fact]
    public async Task EnrichRolesAsync_WhenProviderTimesOut_ReturnsFailedTimeoutResult()
    {
        var (host, _, timeProvider) = CreateHost(auth =>
        {
            // Use short timeout for direct test
        });

        // Provider that hangs indefinitely
        var hangingProvider = new StubRoleProvider("hanging", PluginRoleEnrichmentResult.Empty, delay: TimeSpan.FromSeconds(60));
        host.RegisterRoleProvider(hangingProvider);

        using var cts = new CancellationTokenSource();
        var enrichTask = host.EnrichRolesAsync(CreateTestContext(), cts.Token).AsTask();

        // Advance fake time past 500ms default timeout
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        var result = await enrichTask;
        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("AUTH_PLUGIN_TIMEOUT");
    }

    [Fact]
    public async Task EnrichRolesAsync_WhenOneOfMultipleProvidersFails_EntireEnrichmentFailsClosed()
    {
        var (host, _, _) = CreateHost();
        host.RegisterRoleProvider(new StubRoleProvider("good", new PluginRoleEnrichmentResult(["GoodRole"])));
        host.RegisterRoleProvider(new StubRoleProvider("bad", PluginRoleEnrichmentResult.Empty, throwException: true));

        var result = await host.EnrichRolesAsync(CreateTestContext(), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("AUTH_PLUGIN_FAILED");
        result.Result.AddedRoles.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnrichRolesAsync_WhenDisabledInOptions_ReturnsEmptyWithoutInvoking()
    {
        var (host, _, _) = CreateHost(auth =>
        {
            // We set allow role enrichment to false on options
        });

        var options = new GatewayOptions();
        var disabledOptions = new GatewayOptions
        {
            Plugins = new PluginsOptions
            {
                Auth = new AuthPluginOptions { AllowRoleEnrichment = false }
            }
        };

        var hostDisabled = new AuthPluginHost(Options.Create(disabledOptions), NullLogger<AuthPluginHost>.Instance);
        hostDisabled.RegisterRoleProvider(new StubRoleProvider("provider", new PluginRoleEnrichmentResult(["RoleA"])));

        var result = await hostDisabled.EnrichRolesAsync(CreateTestContext(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Result.AddedRoles.ShouldBeEmpty();
    }

    [Fact]
    public async Task SecurityContextResolutionMiddleware_WhenRoleEnrichmentSucceeds_UpdatesSecurityPrincipalContext()
    {
        var (host, gatewayOptions, _) = CreateHost();
        host.RegisterRoleProvider(new StubRoleProvider("ad", new PluginRoleEnrichmentResult(["CustomEnrichedRole"], [new Sid("S-1-5-21-999-999-999-1")])));

        var services = new ServiceCollection();
        services.AddSingleton<IAuthPluginHost>(host);
        services.AddSingleton(Options.Create(gatewayOptions));
        var monitor = NSubstitute.Substitute.For<IOptionsMonitor<GatewayOptions>>();
        monitor.CurrentValue.Returns(gatewayOptions);
        services.AddSingleton(monitor);
        var sp = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var claims = new List<Claim>
        {
            new("tid", "tenant-omega"),
            new("sub", "user-omega"),
            new(ClaimTypes.Role, "BaseRole")
        };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        bool nextCalled = false;
        SecurityPrincipalContext? resolvedContext = null;

        var middleware = new SecurityContextResolutionMiddleware(ctx =>
        {
            nextCalled = true;
            resolvedContext = ctx.Items[SecurityPrincipalContext.ItemKey] as SecurityPrincipalContext;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(httpContext);

        nextCalled.ShouldBeTrue();
        resolvedContext.ShouldNotBeNull();
        resolvedContext.TenantRoles.ShouldContain("CustomEnrichedRole");
        resolvedContext.TenantRoles.ShouldContain("BaseRole");
        resolvedContext.GroupSids.ShouldContain(new Sid("S-1-5-21-999-999-999-1"));
    }

    [Fact]
    public async Task SecurityContextResolutionMiddleware_WhenRoleEnrichmentFails_Returns403WithErrorCode()
    {
        var (host, gatewayOptions, _) = CreateHost();
        host.RegisterRoleProvider(new StubRoleProvider("failing", PluginRoleEnrichmentResult.Empty, throwException: true));

        var services = new ServiceCollection();
        services.AddSingleton<IAuthPluginHost>(host);
        services.AddSingleton(Options.Create(gatewayOptions));
        var monitor = NSubstitute.Substitute.For<IOptionsMonitor<GatewayOptions>>();
        monitor.CurrentValue.Returns(gatewayOptions);
        services.AddSingleton(monitor);
        var sp = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        httpContext.Response.Body = new MemoryStream();
        var claims = new List<Claim>
        {
            new("tid", "tenant-omega"),
            new("sub", "user-omega")
        };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        bool nextCalled = false;
        var middleware = new SecurityContextResolutionMiddleware(ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(httpContext);

        nextCalled.ShouldBeFalse();
        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var responseBody = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
        responseBody.ShouldContain("AUTH_PLUGIN_FAILED");
    }
}
