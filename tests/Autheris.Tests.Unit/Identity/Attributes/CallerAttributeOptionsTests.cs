namespace Autheris.Tests.Unit.Identity.Attributes;

using System.Linq;
using Autheris.Api.Configuration.Validation;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>PLAN-GOV-129 C-4 / section 7: startup validation of Gateway:CallerAttributes.</summary>
public sealed class CallerAttributeOptionsTests
{
    private static string[] Validate(GatewayOptions options, string environment = "Production")
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);
        var result = new GatewayCoreOptionsValidator(env).Validate(null, options);
        return result.Failures?.ToArray() ?? [];
    }

    [Fact]
    public void Defaults_AreValid_AndDisabled()
    {
        var options = new GatewayOptions();
        options.CallerAttributes.Enabled.ShouldBeFalse();
        options.CallerAttributes.DefaultTtlSeconds.ShouldBe(60);
        options.CallerAttributes.MaxTtlSeconds.ShouldBe(300);
        options.CallerAttributes.MaxValuesHardCap.ShouldBe(200);
        Validate(options).Where(f => f.Contains("CallerAttributes")).ShouldBeEmpty();
    }

    [Fact]
    public void DefaultTtl_AboveMaxTtl_IsRefused()
    {
        var options = new GatewayOptions { CallerAttributes = new CallerAttributeOptions { Enabled = true, DefaultTtlSeconds = 60, MaxTtlSeconds = 30 } };
        Validate(options).ShouldContain(f => f.Contains("Gateway:CallerAttributes:DefaultTtlSeconds"));
    }

    [Fact]
    public void NegativeTtl_AboveDefaultTtl_IsRefused()
    {
        var options = new GatewayOptions { CallerAttributes = new CallerAttributeOptions { Enabled = true, DefaultTtlSeconds = 10, NegativeTtlSeconds = 20 } };
        Validate(options).ShouldContain(f => f.Contains("NegativeTtlSeconds"));
    }

    [Fact]
    public void TrustedIssuer_WhichTheBearerSchemeDoesNotValidate_IsRefused()
    {
        var options = new GatewayOptions
        {
            CallerAttributes = new CallerAttributeOptions
            {
                Enabled = true,
                Claims = new CallerAttributeClaimsOptions { TrustedIssuers = [new CallerAttributeTrustedIssuer { Issuer = "https://evil.example/" }] }
            }
        };
        Validate(options).ShouldContain(f => f.Contains("TrustedIssuers"));
    }

    [Fact]
    public void ForwardAuthScheme_NeedsNoStartupFailure_ButMustBeListedExplicitly()
    {
        var options = new GatewayOptions { CallerAttributes = new CallerAttributeOptions { Enabled = true } };
        options.CallerAttributes.Claims.AllowedSchemes.ShouldNotContain("ForwardAuth");
        options.CallerAttributes.Claims.AllowedSchemes.ShouldNotContain("TestAuth");
        options.CallerAttributes.Claims.AllowedSchemes.ShouldNotContain("Basic");
    }

    [Fact]
    public void Disabled_IgnoresTheRest()
    {
        var options = new GatewayOptions
        {
            CallerAttributes = new CallerAttributeOptions
            {
                Enabled = false,
                DefaultTtlSeconds = 120,
                MaxTtlSeconds = 60,
                Claims = new CallerAttributeClaimsOptions { TrustedIssuers = [new CallerAttributeTrustedIssuer { Issuer = "x" }] }
            }
        };
        Validate(options).Where(f => f.Contains("CallerAttributes")).ShouldBeEmpty();
    }
}
