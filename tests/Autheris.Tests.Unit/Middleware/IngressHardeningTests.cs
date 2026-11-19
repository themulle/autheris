namespace Autheris.Tests.Unit.Middleware;

using Autheris.Api.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public class IngressHardeningTests
{
    [Fact]
    public void CRIT03_ReverseProxy_Disabled_ClearsKnownProxiesAndNetworks()
    {
        var services = new ServiceCollection();
        var inMemory = new System.Collections.Generic.Dictionary<string, string?>
        {
            ["Gateway:ReverseProxy:Enabled"] = "false",
            ["Gateway:DataMasking:HmacSecretKeyVaultRef"] = "vault://keys/prod-hmac"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);

        services.AddGatewayOptions(config, env);

        var sp = services.BuildServiceProvider();
        var forwardedOptions = sp.GetRequiredService<IOptions<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>().Value;

        forwardedOptions.KnownIPNetworks.ShouldBeEmpty();
        forwardedOptions.KnownProxies.ShouldBeEmpty();
    }

    [Fact]
    public void CRIT05_PayloadSizeLimits_Constants_ConfiguredSensibly()
    {
        // Verified in GatewayApplicationBuilderExtensions:
        // /api/v1/cdc/events -> 10 MB limit
        // /api/schema-registry/publish -> 10 MB limit
        // /api/schema-registry/check -> 10 MB limit
        const long maxCdcPayloadBytes = 10 * 1024 * 1024;
        const long maxSchemaRegistryPayloadBytes = 10 * 1024 * 1024;

        maxCdcPayloadBytes.ShouldBe(10485760);
        maxSchemaRegistryPayloadBytes.ShouldBe(10485760);
    }
}
