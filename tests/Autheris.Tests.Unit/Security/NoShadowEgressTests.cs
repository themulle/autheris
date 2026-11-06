namespace Autheris.Tests.Unit.Security;

using System;
using System.Linq;
using System.Net.Http;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class NoShadowEgressTests
{
    [Fact]
    public void No_http_client_registered_for_shadow_target()
    {
        var services = new ServiceCollection();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);
        var options = new GatewayOptions();

        services.AddLogging();
        services.AddSingleton(Options.Create(options));
        services.AddAutherisStorage(options, env);
        services.AddAutherisExecution(options, env);

        // 1. Verify no service or implementation contains Shadowing or TrafficShadow
        var shadowDescriptors = services.Where(d =>
            (d.ServiceType.FullName?.Contains("Shadow", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (d.ImplementationType?.FullName?.Contains("Shadow", StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        shadowDescriptors.ShouldBeEmpty("No shadowing service or type should be registered in the DI container.");

        // 2. Verify no HttpClient is typed for Shadowing
        var httpClientDescriptors = services.Where(d =>
            (d.ServiceType.FullName?.Contains("HttpClient", StringComparison.OrdinalIgnoreCase) == true) &&
            (d.ImplementationType?.FullName?.Contains("Shadow", StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        httpClientDescriptors.ShouldBeEmpty("No HttpClient should be registered for traffic shadowing.");
    }
}
