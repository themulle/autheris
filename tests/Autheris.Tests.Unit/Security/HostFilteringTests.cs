namespace Autheris.Tests.Unit.Security;

using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>SR-L-02 / G-4: host allow-list matcher, host filter middleware and the production startup requirement.</summary>
public sealed class HostFilteringTests
{
    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static GatewayOptions OptionsWith(params string[] hosts) => new()
    {
        Hosting = new HostingLimitsOptions { AllowedHosts = [.. hosts] }
    };

    private static async Task<(int Status, bool NextCalled)> RunAsync(HostAllowList list, string host, string path = "/graphql")
    {
        var nextCalled = false;
        var middleware = new HostAllowListMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, list);
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        await middleware.InvokeAsync(context);
        return (context.Response.StatusCode, nextCalled);
    }

    [Theory]
    [InlineData("gateway.corp.example", true)]
    [InlineData("GATEWAY.corp.example", true)]
    [InlineData("gateway.corp.example:8443", true)]
    [InlineData("evil.example", false)]
    [InlineData("gateway.corp.example.evil.example", false)]
    [InlineData("", false)]
    public void ExactEntry_MatchesHostOnAnyPort(string authority, bool expected)
    {
        new HostAllowList(["gateway.corp.example"]).IsAllowed(authority).ShouldBe(expected);
    }

    [Theory]
    [InlineData("gateway:8080", true)]
    [InlineData("gateway:9090", false)]
    [InlineData("gateway", false)]
    public void HostPortEntry_MatchesThatPortOnly(string authority, bool expected)
    {
        new HostAllowList(["gateway:8080"]).IsAllowed(authority).ShouldBe(expected);
    }

    [Theory]
    [InlineData("a.corp.example", true)]
    [InlineData("a.b.corp.example:443", true)]
    [InlineData("corp.example", false)]
    [InlineData("evilcorp.example", false)]
    public void WildcardSuffixEntry_MatchesSubdomainsOnly(string authority, bool expected)
    {
        new HostAllowList(["*.corp.example"]).IsAllowed(authority).ShouldBe(expected);
    }

    [Fact]
    public void EmptyList_AllowsNothing()
    {
        new HostAllowList([]).IsAllowed("localhost").ShouldBeFalse();
    }

    [Theory]
    [InlineData("localhost:5000", true)]
    [InlineData("127.0.0.1:5000", true)]
    [InlineData("[::1]:5000", true)]
    [InlineData("app.localhost", true)]
    [InlineData("evil.example", false)]
    public void Development_EmptyList_AllowsLoopbackOnly(string authority, bool expected)
    {
        HostAllowList.Resolve(new HostingLimitsOptions(), isDevelopment: true).IsAllowed(authority).ShouldBe(expected);
    }

    [Fact]
    public void Development_ConfiguredList_ReplacesLoopbackDefaults()
    {
        var list = HostAllowList.Resolve(new HostingLimitsOptions { AllowedHosts = ["gateway:8080"] }, isDevelopment: true);

        list.IsAllowed("gateway:8080").ShouldBeTrue();
        list.IsAllowed("localhost:8080").ShouldBeFalse();
    }

    [Fact]
    public void NonDevelopment_EmptyList_StaysEmpty()
    {
        HostAllowList.Resolve(new HostingLimitsOptions(), isDevelopment: false).IsAllowed("localhost").ShouldBeFalse();
    }

    [Fact]
    public async Task Request_WithUnlistedHost_Returns400()
    {
        var (status, nextCalled) = await RunAsync(new HostAllowList(["gateway.corp.example"]), "evil.example");

        status.ShouldBe(StatusCodes.Status400BadRequest);
        nextCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task Request_WithListedHost_PassesThrough()
    {
        var (status, nextCalled) = await RunAsync(new HostAllowList(["gateway.corp.example"]), "gateway.corp.example");

        status.ShouldBe(StatusCodes.Status200OK);
        nextCalled.ShouldBeTrue();
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoint_UnlistedHost_StillServed(string path)
    {
        var (_, nextCalled) = await RunAsync(new HostAllowList(["gateway.corp.example"]), "10.1.2.3:5000", path);

        nextCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task Request_WithoutHost_Returns400()
    {
        var (status, nextCalled) = await RunAsync(new HostAllowList(["gateway.corp.example"]), "");

        status.ShouldBe(StatusCodes.Status400BadRequest);
        nextCalled.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void StartupValidator_NonDevelopmentEmptyAllowedHosts_Fails(string environment)
    {
        var ex = Should.Throw<ValidationException>(() =>
            GatewayStartupValidator.ValidateAllowedHosts(new GatewayOptions(), Env(environment)));

        ex.Message.ShouldContain("Gateway:Hosting:AllowedHosts");
    }

    [Fact]
    public void StartupValidator_NonDevelopmentBareWildcard_Fails()
    {
        Should.Throw<ValidationException>(() =>
            GatewayStartupValidator.ValidateAllowedHosts(OptionsWith("*"), Env("Production")));
    }

    [Fact]
    public void StartupValidator_NonDevelopmentConfiguredList_Passes()
    {
        Should.NotThrow(() =>
            GatewayStartupValidator.ValidateAllowedHosts(OptionsWith("gateway.corp.example"), Env("Production")));
    }

    [Fact]
    public void StartupValidator_DevelopmentEmptyList_Passes()
    {
        Should.NotThrow(() => GatewayStartupValidator.ValidateAllowedHosts(new GatewayOptions(), Env("Development")));
    }
}
