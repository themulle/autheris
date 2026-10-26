namespace Autheris.Tests.Unit.DependencyInjection;

using System;
using System.Linq;
using Autheris.Api.Configuration;
using Autheris.Api.Extensions;
using Autheris.Api.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WAVE3-TIME-SC-01: production composition registers only <see cref="TimeProvider.System"/>; no key selects a clock.</summary>
public sealed class TimeProviderCompositionTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void WAVE3_TIME_SC_01_DiCompositionProduction_ResolvesTimeProviderSystem()
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        environment.ContentRootPath.Returns(System.IO.Path.GetTempPath());
        var configuration = new ConfigurationManager();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(DevConfiguration.Apply(configuration, environment));
        services.AddGatewayTelemetry(configuration);
        services.AddDataSourceHierarchicalConfiguration(configuration);
        var gatewayOptions = services.AddGatewayOptions(configuration, environment);
        services.AddGatewayInfrastructure(gatewayOptions, environment);

        var descriptors = services.Where(d => d.ServiceType == typeof(TimeProvider)).ToList();

        descriptors.ShouldNotBeEmpty();
        descriptors.ShouldAllBe(d => ReferenceEquals(d.ImplementationInstance, TimeProvider.System));
    }
}
