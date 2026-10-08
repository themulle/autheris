namespace Autheris.Tests.Unit.Security;

using System;
using System.Text;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// API-14: with Entra ID and AD FS both enabled, AD FS tokens are validated by an own scheme that loads the AD FS
/// signing keys; tokens are routed by their issuer.
/// </summary>
public sealed class JwtSchemeSelectionApi14Tests
{
    private const string AdfsAuthority = "https://adfs.corp.example/adfs";

    private static string UnsignedJwt(string issuer)
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"RS256\",\"typ\":\"JWT\"}")}.{B64($"{{\"iss\":\"{issuer}\",\"sub\":\"x\"}}")}.c2ln";
    }

    private static GatewayOptions BothIdps() => new()
    {
        Authentication = new AuthenticationOptions
        {
            EntraId = new EntraIdAuthOptions { Enabled = true, TenantId = "11111111-2222-3333-4444-555555555555", Audience = "api://autheris" },
            Adfs = new AdfsAuthOptions { Enabled = true, Authority = AdfsAuthority, Audience = "urn:autheris" }
        }
    };

    [Theory]
    [InlineData(AdfsAuthority + "/services/trust", GatewayAuthSchemes.JwtBearerAdfs)]
    [InlineData("https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/v2.0", GatewayAuthSchemes.JwtBearer)]
    public void BearerTokens_AreRoutedByIssuer(string issuer, string expectedScheme)
    {
        GatewayAuthSchemes.SelectJwtScheme(UnsignedJwt(issuer), BothIdps()).ShouldBe(expectedScheme);
    }

    [Fact]
    public void SingleIdp_AlwaysUsesTheDefaultScheme()
    {
        var options = BothIdps();
        var entraOnly = new GatewayOptions { Authentication = new AuthenticationOptions { EntraId = options.Authentication.EntraId } };

        GatewayAuthSchemes.SelectJwtScheme(UnsignedJwt(AdfsAuthority), entraOnly).ShouldBe(GatewayAuthSchemes.JwtBearer);
        GatewayAuthSchemes.SelectJwtScheme("not-a-jwt", BothIdps()).ShouldBe(GatewayAuthSchemes.JwtBearer);
    }

    [Fact]
    public void BothIdps_RegisterOneSchemePerAuthority()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGatewayAuth(BothIdps(), env);
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();

        monitor.Get(GatewayAuthSchemes.JwtBearer).Authority.ShouldStartWith("https://login.microsoftonline.com/");
        monitor.Get(GatewayAuthSchemes.JwtBearerAdfs).Authority.ShouldBe(AdfsAuthority);
        monitor.Get(GatewayAuthSchemes.JwtBearerAdfs).TokenValidationParameters.ValidAudiences.ShouldBe(["urn:autheris"]);
    }
}
