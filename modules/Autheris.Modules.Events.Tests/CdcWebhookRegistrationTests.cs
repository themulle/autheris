namespace Autheris.Tests.Unit.DependencyInjection;

using Autheris.Tests.Common;
using System.Collections.Generic;
using System.Linq;
using Autheris.Api.Extensions;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Application.Webhooks;
using Autheris.Domain.Options;
using Autheris.Modules.Events;
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
        IHostEnvironment env = new TestHostEnvironment(Environments.Development);
        services.AddSingleton(env);
        var config = new Dictionary<string, string?>();
        if (enabled is { } value)
        {
            config["Gateway:Events:CdcWebhooks:Enabled"] = value ? "true" : "false";
        }

        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        services.AddLogging();
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = Autheris.Tests.Common.Governance.GovernanceTestConnectionStrings.NonConnectingPostgreSql }
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

    private static bool HasHostedService(IServiceCollection s) =>
        s.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(CdcWebhookDeliveryService));

    private static bool HasHosted<T>(IServiceCollection s) =>
        s.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(T));

    private static ServiceCollection RegisterModule(bool? enabled)
    {
        var config = new Dictionary<string, string?>();
        if (enabled is { } value)
        {
            config["Gateway:Events:CdcWebhooks:Enabled"] = value ? "true" : "false";
        }

        var services = new ServiceCollection();
        services.AddEventsModule(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        return services;
    }

    [Fact]
    public void Host_enabled_registers_durable_store_and_protector_but_no_delivery_service()
    {
        var services = Register(true);

        HasHostedService(services).ShouldBeFalse();
        services.Any(d => d.ServiceType == typeof(ICloudEventSubscriptionStore)).ShouldBeTrue();
        services.Any(d => d.ServiceType == typeof(IWebhookSecretProtector)).ShouldBeTrue();
        services.Any(d => d.ImplementationType == typeof(Autheris.Application.Events.Services.InMemoryCloudEventSubscriptionStore)).ShouldBeFalse();
    }

    [Fact]
    public void Host_disabled_registers_no_store_but_keeps_the_secret_protector()
    {
        var services = Register(false);

        services.Any(d => d.ServiceType == typeof(ICloudEventSubscriptionStore)).ShouldBeFalse();
        services.Any(d => d.ServiceType == typeof(IWebhookSecretProtector)).ShouldBeTrue();
    }

    [Fact]
    public void Module_enabled_registers_delivery_service_startup_check_and_outbox_worker()
    {
        var services = RegisterModule(true);

        HasHostedService(services).ShouldBeTrue();
        HasHosted<Autheris.Api.Hosting.CdcWebhookStartupCheck>(services).ShouldBeTrue();
        HasHosted<WebhookDeliveryHostedService>(services).ShouldBeTrue();
        HasHosted<WebhookEventBridgeHostedService>(services).ShouldBeTrue();
        services.Any(d => d.ServiceType == typeof(IValidateOptions<CdcWebhooksOptions>)).ShouldBeTrue();
    }

    [Fact]
    public void Module_disabled_registers_no_delivery_service_but_validates_options()
    {
        var disabled = RegisterModule(false);

        HasHostedService(disabled).ShouldBeFalse();
        HasHosted<Autheris.Api.Hosting.CdcWebhookStartupCheck>(disabled).ShouldBeFalse();
        disabled.Any(d => d.ServiceType == typeof(IValidateOptions<CdcWebhooksOptions>)).ShouldBeTrue();
        // ValidateOnStart registers a startup validator of the options infrastructure.
        disabled.Any(d => d.ServiceType.Name.Contains("IStartupValidator")).ShouldBeTrue();
        // The workers read their options at run time and idle while disabled, so they stay registered.
        HasHosted<WebhookDeliveryHostedService>(disabled).ShouldBeTrue();
        disabled.Single(d => d.ServiceType == typeof(CdcWebhooksFeatureFlag)).ImplementationInstance.ShouldBe(new CdcWebhooksFeatureFlag(false));
    }

    [Fact]
    public void Module_default_configuration_enables_cdc_webhooks()
    {
        new CdcWebhooksOptions().Enabled.ShouldBeTrue();
        HasHostedService(RegisterModule(null)).ShouldBeTrue();
    }

    [Fact]
    public void Host_flag_is_detected_when_configuration_is_registered_through_a_factory_like_the_real_host_builder()
    {
        // The real host builder registers IConfiguration with a factory (no ImplementationInstance); the flag must still be honoured.
        var services = new ServiceCollection();
        IHostEnvironment env = new TestHostEnvironment(Environments.Development);
        services.AddSingleton(env);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Events:CdcWebhooks:Enabled"] = "true", ["Gateway:GovernanceDb:Provider"] = "PostgreSql", ["Gateway:GovernanceDb:ConnectionString"] = Autheris.Tests.Common.Governance.GovernanceTestConnectionStrings.NonConnectingPostgreSql }).Build();
        services.AddSingleton<IConfiguration>(_ => cfg);
        services.AddLogging();
        var options = services.AddGatewayOptions(cfg, env);
        services.AddAutherisStorage(options, env);
        services.AddAutherisGovernance(options);
        services.AddAutherisSecurity(options, env);
        services.AddAutherisSqlEngine(options);
        services.AddAutherisExecution(options, env);
        services.AddAutherisEvents(options);

        services.Any(d => d.ServiceType == typeof(ICloudEventSubscriptionStore)).ShouldBeTrue();
    }

    [Fact]
    public void Host_default_configuration_registers_the_durable_store()
    {
        // With no Gateway:Events:CdcWebhooks section at all the feature is on at BOTH registration read sites.
        var services = Register(null);

        services.Any(d => d.ServiceType == typeof(ICloudEventSubscriptionStore)).ShouldBeTrue();
        HasHosted<WebhookDeliveryHostedService>(services).ShouldBeFalse();
    }
}
