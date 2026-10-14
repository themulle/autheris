namespace Autheris.Tests.Unit.OptionsBinding;

using System.IO;
using System.Linq;
using System.Text;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Shouldly;

/// <summary>
/// Binds real JSON (as appsettings.json does) through the full <c>Gateway:</c> section. Pins that a configured list
/// narrows the default, and that a JSON <c>[]</c> binds to null (default applies; startup validation rejects or warns,
/// see <see cref="EmptyListConfigurationValidatorTests"/>).
/// </summary>
public class JsonListBindingTests
{
    private static GatewayOptions BindGateway(string json)
    {
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
        return config.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();
    }

    [Fact]
    public void FullGatewaySection_ConfiguredLists_ReplaceDefaults()
    {
        var o = BindGateway("""
            {"Gateway":{
              "ReverseProxy":{"KnownNetworks":["10.0.0.0/8"]},
              "Authentication":{
                "ForwardAuth":{"AllowedRoles":["Analyst"]},
                "EntraId":{"ReadOnlyScopes":["Custom.Read"]},
                "BasicAuth":{"Session":{"AllowedEnvironments":["Staging"]}}},
              "Extensibility":{"BreakGlassAllowedRoles":["SecurityAdmin"]},
              "SystemMetrics":{"AllowedRoles":["GovernanceAdmin"]},
              "WebSql":{"CrossSource":{"AllowedHttpAuthModes":["None"]}}
            }}
            """);

        o.ReverseProxy.GetEffectiveKnownNetworks().ToArray().ShouldBe(["10.0.0.0/8"]);
        o.Authentication.ForwardAuth.GetEffectiveAllowedRoles().ToArray().ShouldBe(["Analyst"]);
        o.Authentication.EntraId.GetEffectiveReadOnlyScopes().ToArray().ShouldBe(["Custom.Read"]);
        o.Authentication.BasicAuth.Session.GetEffectiveAllowedEnvironments().ToArray().ShouldBe(["Staging"]);
        o.Extensibility.GetEffectiveBreakGlassAllowedRoles().ToArray().ShouldBe(["SecurityAdmin"]);
        o.SystemMetrics.GetEffectiveAllowedRoles().ToArray().ShouldBe(["GovernanceAdmin"]);
        o.WebSql.CrossSource.GetEffectiveAllowedHttpAuthModes().ToArray().ShouldBe(["None"]);
    }

    [Fact]
    public void FullGatewaySection_EmptyArrays_BindToNull_SoDefaultsApply()
    {
        var o = BindGateway("""
            {"Gateway":{
              "ReverseProxy":{"KnownNetworks":[]},
              "Authentication":{
                "ForwardAuth":{"AllowedRoles":[]},
                "EntraId":{"ReadOnlyScopes":[]},
                "BasicAuth":{"Session":{"AllowedEnvironments":[]}}},
              "Extensibility":{"BreakGlassAllowedRoles":[]},
              "SystemMetrics":{"AllowedRoles":[]},
              "WebSql":{"CrossSource":{"AllowedHttpAuthModes":[]}}
            }}
            """);

        o.ReverseProxy.KnownNetworks.ShouldBeNull();
        o.Authentication.ForwardAuth.AllowedRoles.ShouldBeNull();
        o.Authentication.EntraId.ReadOnlyScopes.ShouldBeNull();
        o.Authentication.BasicAuth.Session.AllowedEnvironments.ShouldBeNull();
        o.Extensibility.BreakGlassAllowedRoles.ShouldBeNull();
        o.SystemMetrics.AllowedRoles.ShouldBeNull();
        o.WebSql.CrossSource.AllowedHttpAuthModes.ShouldBeNull();

        o.Authentication.ForwardAuth.GetEffectiveAllowedRoles().ToArray().ShouldBe(["GatewayUser", "Consumer", "Analyst", "Developer"]);
        o.Authentication.EntraId.GetEffectiveReadOnlyScopes().ToArray().ShouldBe(["Agent.Read", "App.Read"]);
        o.ReverseProxy.GetEffectiveKnownNetworks().ToArray().ShouldBe(["127.0.0.1/32", "::1/128"]);
    }

    [Theory]
    [InlineData("""{"KnownNetworks":[]}""")]
    public void ReverseProxy_EmptyJsonArray_BindsToNull(string json)
        => BindSection<ReverseProxyOptions>(json).KnownNetworks.ShouldBeNull();

    [Theory]
    [InlineData("""{"AllowedRoles":[]}""")]
    public void ForwardAuth_EmptyJsonArray_BindsToNull(string json)
        => BindSection<ForwardAuthOptions>(json).AllowedRoles.ShouldBeNull();

    [Theory]
    [InlineData("""{"AllowedEnvironments":[]}""")]
    public void BasicAuthSession_EmptyJsonArray_BindsToNull(string json)
        => BindSection<BasicAuthSessionOptions>(json).AllowedEnvironments.ShouldBeNull();

    [Theory]
    [InlineData("""{"ReadOnlyScopes":[]}""")]
    public void EntraId_EmptyJsonArray_BindsToNull(string json)
        => BindSection<EntraIdAuthOptions>(json).ReadOnlyScopes.ShouldBeNull();

    [Theory]
    [InlineData("""{"BreakGlassAllowedRoles":[]}""")]
    public void Extensibility_EmptyJsonArray_BindsToNull(string json)
        => BindSection<ExtensibilityOptions>(json).BreakGlassAllowedRoles.ShouldBeNull();

    [Theory]
    [InlineData("""{"AllowedRoles":[]}""")]
    public void SystemMetrics_EmptyJsonArray_BindsToNull(string json)
        => BindSection<SystemMetricsOptions>(json).AllowedRoles.ShouldBeNull();

    [Theory]
    [InlineData("""{"AllowedHttpAuthModes":[]}""")]
    public void CrossSource_EmptyJsonArray_BindsToNull(string json)
        => BindSection<CrossSourceOptions>(json).AllowedHttpAuthModes.ShouldBeNull();

    private static T BindSection<T>(string json) where T : new()
    {
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
        return config.Get<T>() ?? new T();
    }
}
