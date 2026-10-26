namespace Autheris.Tests.Unit.Identity;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Validators;
using Autheris.Application.Interfaces;
using Autheris.Domain.Identity;
using Autheris.Domain.Options.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>ARCH-MNT-06-T-06 (SC-01, SC-03): the SCIM caller allowlist (<c>AllowedIpRanges</c>) is enforced, fail closed and generic.</summary>
[Trait("Category", "Security")]
public sealed class ScimAllowedIpRangesTests
{
    private const string Secret = "SuperSecretScimKey1234567890ABCDEF";

    private static IdentityRealmsOptions Options(params string[] ranges)
    {
        var options = new IdentityRealmsOptions { Scim = new ScimOptions { AllowedIpRanges = [.. ranges] } };
        options.Realms["realm-a"] = new IdentityRealmConfigOptions
        {
            Provisioning = RealmProvisioningMode.Scim,
            Scim = new ScimRealmSecretOptions { PrimarySecret = Secret }
        };
        return options;
    }

    private static async Task<AuthenticateResult> Run(
        IdentityRealmsOptions options, IClientIpResolver? resolver, string token = Secret, Action<HttpContext>? configure = null,
        string path = "/scim/v2/realm-a/Users")
    {
        var schemeOptions = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        schemeOptions.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        var realms = Substitute.For<IOptionsMonitor<IdentityRealmsOptions>>();
        realms.CurrentValue.Returns(options);
        var handler = new ScimAuthenticationHandler(schemeOptions, NullLoggerFactory.Instance, UrlEncoder.Default, realms, resolver);
        var context = new DefaultHttpContext();
        context.Request.Headers["Authorization"] = "Bearer " + token;
        context.Request.Path = path;
        configure?.Invoke(context);
        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.Scim, GatewayAuthSchemes.Scim, typeof(ScimAuthenticationHandler)), context);
        return await handler.AuthenticateAsync();
    }

    private static IClientIpResolver ResolverFor(string ip)
    {
        var resolver = Substitute.For<IClientIpResolver>();
        resolver.ResolveClientIp().Returns(IPAddress.Parse(ip));
        return resolver;
    }

    [Fact]
    public async Task ARCH_MNT_06_T_06_Scim_AllowedIpRanges_RejectsOtherPeer_AcceptsListed()
    {
        var options = Options("10.0.0.0/8", "2001:db8::/32");

        (await Run(options, ResolverFor("10.1.2.3"))).Succeeded.ShouldBeTrue();
        (await Run(options, ResolverFor("2001:db8::5"))).Succeeded.ShouldBeTrue();
        (await Run(options, ResolverFor("::ffff:10.1.2.3"))).Succeeded.ShouldBeTrue("IPv4-mapped IPv6 peers are normalised");
        (await Run(options, ResolverFor("192.168.1.1"))).Succeeded.ShouldBeFalse();
        (await Run(options, ResolverFor("::ffff:192.168.1.1"))).Succeeded.ShouldBeFalse();
        (await Run(options, ResolverFor("2001:db9::1"))).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task ARCH_MNT_06_T_06_Scim_Rejection_IsGeneric_NoIpOracle()
    {
        var options = Options("10.0.0.0/8");

        var wrongTokenFromAllowedPeer = await Run(options, ResolverFor("10.1.2.3"), token: "wrong-token");
        var validTokenFromDeniedPeer = await Run(options, ResolverFor("192.168.1.1"));
        var wrongTokenFromDeniedPeer = await Run(options, ResolverFor("192.168.1.1"), token: "wrong-token");

        validTokenFromDeniedPeer.Succeeded.ShouldBeFalse();
        validTokenFromDeniedPeer.Failure!.Message.ShouldBe(wrongTokenFromAllowedPeer.Failure!.Message);
        wrongTokenFromDeniedPeer.Failure!.Message.ShouldBe(wrongTokenFromAllowedPeer.Failure!.Message);
        validTokenFromDeniedPeer.Failure.Message.ToLowerInvariant().ShouldNotContain("ip");
        validTokenFromDeniedPeer.Failure.Message.ToLowerInvariant().ShouldNotContain("range");
        validTokenFromDeniedPeer.Failure.Message.ToLowerInvariant().ShouldNotContain("denied");
    }

    [Fact]
    public async Task ARCH_MNT_06_T_06_Scim_AllowlistAppliesToEveryScimPath()
    {
        var options = Options("10.0.0.0/8");
        foreach (var path in new[] { "/scim/v2/realm-a/Users", "/scim/v2/realm-a/Groups", "/scim/v2/realm-a/Users/abc", "/scim/v2/realm-a/ServiceProviderConfig" })
        {
            (await Run(options, ResolverFor("192.168.1.1"), path: path)).Succeeded.ShouldBeFalse(path);
            (await Run(options, ResolverFor("10.0.0.1"), path: path)).Succeeded.ShouldBeTrue(path);
        }
    }

    [Fact]
    public async Task ARCH_MNT_06_T_06_Scim_UnevaluablePeer_WithNonEmptyList_Denies()
    {
        var options = Options("10.0.0.0/8");
        var none = Substitute.For<IClientIpResolver>();
        none.ResolveClientIp().Returns(IPAddress.None);

        (await Run(options, none)).Succeeded.ShouldBeFalse();
        (await Run(options, null)).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task ARCH_MNT_06_T_06_Scim_InvalidCidrAtRuntime_Denies()
    {
        var options = Options("10.0.0.0/8", "not-a-cidr");

        (await Run(options, ResolverFor("10.1.2.3"))).Succeeded.ShouldBeFalse("an unparsable entry must never silently disable the control");
    }

    [Fact]
    public async Task ARCH_MNT_06_T_06_Scim_EmptyList_Unrestricted()
    {
        var options = Options();

        (await Run(options, null)).Succeeded.ShouldBeTrue();
        (await Run(options, ResolverFor("203.0.113.9"))).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task ARCH_MNT_06_T_06_Scim_ForwardedHeaderFromUntrustedPeer_DoesNotBypassRanges()
    {
        var options = Options("10.0.0.0/8");
        var accessor = new HttpContextAccessor();
        var resolver = new HttpContextClientIpResolver(accessor);
        var forwarded = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor }));

        var result = await Run(options, resolver, configure: context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
            context.Request.Headers["X-Forwarded-For"] = "10.1.2.3";
            accessor.HttpContext = context;
            forwarded.Invoke(context).GetAwaiter().GetResult();
        });

        result.Succeeded.ShouldBeFalse("a forwarded header from a peer that is not a trusted proxy is ignored");
    }

    [Theory]
    [InlineData("10.0.0.0/8", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("2001:db8::/32", true)]
    [InlineData("::1", true)]
    [InlineData("10.0.0.0/33", false)]
    [InlineData("10.0.0.1/8", false)]
    [InlineData("10.0.0.0/", false)]
    [InlineData("garbage", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    public void ARCH_MNT_06_T_06_Scim_RangeSyntax(string entry, bool valid) =>
        ScimIpAllowlist.IsValidRange(entry).ShouldBe(valid);

    [Fact]
    public void ARCH_MNT_06_T_06_Scim_InvalidCidr_FailsStart()
    {
        var validator = new IdentityRealmsScimOptionsValidator();

        var result = validator.Validate(null, Options("10.0.0.0/8", "10.0.0.0/99"));

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("AllowedIpRanges[1]");
        result.FailureMessage.ShouldNotContain("10.0.0.0/99", Case.Sensitive, "the value is not echoed");
        validator.Validate(null, Options("10.0.0.0/8", "2001:db8::/32")).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void ARCH_MNT_06_T_06_Scim_EmptyList_InProduction_LogsStartupWarning()
    {
        var logger = Substitute.For<ILogger<IdentityRealmsScimOptionsValidator>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        new IdentityRealmsScimOptionsValidator(env, logger).Validate(null, Options()).Succeeded.ShouldBeTrue();
        logger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Log" && (LogLevel)c.GetArguments()[0]! == LogLevel.Warning).ShouldBe(1);

        var devEnv = Substitute.For<IHostEnvironment>();
        devEnv.EnvironmentName.Returns("Development");
        var devLogger = Substitute.For<ILogger<IdentityRealmsScimOptionsValidator>>();
        new IdentityRealmsScimOptionsValidator(devEnv, devLogger).Validate(null, Options());
        devLogger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Log").ShouldBe(0);
    }
}
