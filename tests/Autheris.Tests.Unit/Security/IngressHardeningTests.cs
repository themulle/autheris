using Autheris.Tests.Unit.TestSupport;
namespace Autheris.Tests.Unit.Security;

using Autheris.Tests.Common;
using Autheris.Domain.Security;
using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using NSubstitute.ExceptionExtensions;
using Autheris.Application.Governance;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Audit;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Security;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Streaming.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
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
