namespace Autheris.Tests.Unit.DependencyInjection;

using System.Collections.Generic;
using System.Linq;
using Autheris.Api.Extensions;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Application.Webhooks;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class CdcWebhookRegistrationTests
{
    private static ServiceCollection Register(bool? enabled)
    {
        var services = new ServiceCollection();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);
        services.AddSingleton(env);
        var config = new Dictionary<string, string?>();
        if (enabled is { } value)
        {
            config["Gateway:CdcWebhooks:Enabled"] = value ? "true" : "false";
        }

        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        services.AddLogging();
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "Sqlite", ConnectionString = "Data Source=:memory:" }
        };
        services.AddSingleton(Options.Create(options));
        services.AddAutherisStorage(options, env);
        services.AddAutherisGovernance(options);
        services.AddAutherisSecurity(options, env);
        services.AddAutherisSqlEngine(options);
        services.AddAutherisExecution(options, env);
        services.AddAutherisEvents(options);
        return services;
    }

    private static bool HasHostedService(ServiceCollection s) =>
        s.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(CdcWebhookDeliveryService));

    [Fact]
    public void Enabled_RegistersDeliveryServiceDurableStoreAndProtector()
    {
        var services = Register(true);

        HasHostedService(services).ShouldBeTrue();
        services.Any(d => d.ServiceType == typeof(ICloudEventSubscriptionStore)).ShouldBeTrue();
        services.Any(d => d.ServiceType == typeof(IWebhookSecretProtector)).ShouldBeTrue();
        services.Any(d => d.ImplementationType == typeof(Autheris.Application.Events.Services.InMemoryCloudEventSubscriptionStore)).ShouldBeFalse();
    }

    [Fact]
    public void OptionsValidation_RunsOnStart_EvenWhenDisabled_AndStartupCheckIsRegisteredWhenEnabled()
    {
        var disabled = Register(false);
        var enabled = Register(true);

        disabled.Any(d => d.ServiceType == typeof(IValidateOptions<CdcWebhooksOptions>)).ShouldBeTrue();
        enabled.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(Autheris.Api.Hosting.CdcWebhookStartupCheck)).ShouldBeTrue();
        disabled.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(Autheris.Api.Hosting.CdcWebhookStartupCheck)).ShouldBeFalse();
        // ValidateOnStart registers a startup validator hosted service of the options infrastructure.
        disabled.Any(d => d.ServiceType.Name.Contains("IStartupValidator")).ShouldBeTrue();
    }

    [Fact]
    public void Disabled_RegistersNoDeliveryServiceStoreOrProtector()
    {
        var services = Register(false);

        HasHostedService(services).ShouldBeFalse();
        services.Any(d => d.ServiceType == typeof(ICloudEventSubscriptionStore)).ShouldBeFalse();
        services.Any(d => d.ServiceType == typeof(IWebhookSecretProtector)).ShouldBeFalse();
    }

    [Fact]
    public void Enabled_IsDetected_WhenConfigurationIsRegisteredThroughAFactory_LikeTheRealHostBuilder()
    {
        // The real host builder registers IConfiguration with a factory (no ImplementationInstance); the flag must still be honoured.
        var services = new ServiceCollection();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);
        services.AddSingleton(env);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:CdcWebhooks:Enabled"] = "true", ["Gateway:GovernanceDb:Provider"] = "Sqlite" }).Build();
        services.AddSingleton<IConfiguration>(_ => cfg);
        services.AddLogging();
        var options = services.AddGatewayOptions(cfg, env);
        services.AddAutherisStorage(options, env);
        services.AddAutherisGovernance(options);
        services.AddAutherisSecurity(options, env);
        services.AddAutherisSqlEngine(options);
        services.AddAutherisExecution(options, env);
        services.AddAutherisEvents(options);

        HasHostedService(services).ShouldBeTrue();
        services.Any(d => d.ServiceType == typeof(ICloudEventSubscriptionStore)).ShouldBeTrue();
    }

    [Fact]
    public void DefaultConfiguration_RegistersCdcWebhookService()
    {
        // G-2: with no Gateway:CdcWebhooks section at all the feature is on (the default of the options class), at BOTH registration read sites.
        var services = Register(null);

        new CdcWebhooksOptions().Enabled.ShouldBeTrue();
        HasHostedService(services).ShouldBeTrue();
        services.Any(d => d.ServiceType == typeof(ICloudEventSubscriptionStore)).ShouldBeTrue();
        services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(WebhookDeliveryHostedService)).ShouldBeTrue();
    }

    [Fact]
    public void ExplicitlyDisabled_StillRegistersNothing_AfterTheDefaultFlip() =>
        HasHostedService(Register(false)).ShouldBeFalse();

    [Fact]
    public void Enabled_RegistersOutboxDeliveryWorker_EvenWhenConfiguredWebhooksAreOff()
    {
        var services = Register(true);

        services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(WebhookDeliveryHostedService)).ShouldBeTrue();
    }
}
