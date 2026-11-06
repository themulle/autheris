namespace Autheris.Tests.Unit;

using Autheris.Tests.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions;
using Autheris.Extensions.Cdc;
using Autheris.Extensions.DataCatalog;
using Autheris.Extensions.Lineage;
using Autheris.Extensions.OpenMetadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// EXT-MOVE: all connectors to foreign systems live in Autheris.Extensions and are registered exactly once via
/// <c>AddGatewayExtensions</c> (called by <c>AddGatewayInfrastructure</c>). Also covers the EX-12 handler fix.
/// </summary>
public sealed class ExtensionsRegistrationTests
{
    private static readonly System.Reflection.Assembly ExtensionsAssembly = typeof(ExtensionsServiceCollectionExtensions).Assembly;

    private static ServiceCollection CreateServices(GatewayOptions options)
    {
        var services = new ServiceCollection();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);
        services.AddSingleton(env);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddSingleton(Options.Create(options));

        services.AddGatewayInfrastructure(options);

        // A second call must be a no-op (idempotent registration, no duplicates).
        services.AddGatewayExtensions(options);
        return services;
    }

    private static int HostedServiceCount<THostedService>(IServiceCollection services) =>
        services.Count(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(THostedService));

    private static bool ContainsSsrfHandler(HttpMessageHandler handler)
    {
        HttpMessageHandler? current = handler;
        while (current is DelegatingHandler delegating)
        {
            if (delegating is SsrfProtectionHandler)
            {
                return true;
            }

            current = delegating.InnerHandler;
        }

        return false;
    }

    [Fact]
    public void EXT_Extensions_RegisterNoWatermarkStore_TheHostProvidesTheDurableOne()
    {
        var options = new GatewayOptions();
        var extensionsOnly = new ServiceCollection();
        extensionsOnly.AddGatewayExtensions(options);
        extensionsOnly.Count(d => d.ServiceType == typeof(IMssqlWatermarkStore)).ShouldBe(0);

        var host = CreateServices(options).Where(d => d.ServiceType == typeof(IMssqlWatermarkStore)).ToList();
        host.Count.ShouldBe(1);
        host[0].ImplementationType.ShouldBe(typeof(Autheris.Infrastructure.Streaming.GovernanceDbMssqlWatermarkStore));
    }

    [Fact]
    public void EXT_ConnectorInterfaces_HaveExactlyOneImplementation_InExtensionsAssembly()
    {
        var services = CreateServices(new GatewayOptions());

        Type[] typeBasedServices =
        [
            typeof(IDataCatalogSyncService),
            typeof(IMssqlChangeTrackingPoller)
        ];

        foreach (var serviceType in typeBasedServices)
        {
            var descriptors = services.Where(d => d.ServiceType == serviceType).ToList();
            descriptors.Count.ShouldBe(1, serviceType.Name);
            descriptors[0].ImplementationType.ShouldNotBeNull(serviceType.Name);
            descriptors[0].ImplementationType!.Assembly.ShouldBe(ExtensionsAssembly, serviceType.Name);
        }

        services.Count(d => d.ServiceType == typeof(IOpenLineageClient)).ShouldBe(1);
        services.Count(d => d.ServiceType == typeof(IOpenJevClient)).ShouldBe(1);

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IOpenLineageClient>().ShouldBeOfType<OpenLineageClient>();
        provider.GetRequiredService<IOpenJevClient>().ShouldBeOfType<OpenJevClient>();
    }

    [Fact]
    public void EXT_DataCatalogClient_IsOpenMetadataAdapter()
    {
        var services = CreateServices(new GatewayOptions());
        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IDataCatalogClient>();

        client.ShouldBeOfType<OpenMetadataCatalogAdapter>();
    }

    [Fact]
    public void EXT_EX12_ExtensionHttpClients_HaveSsrfProtectionHandler()
    {
        var services = CreateServices(new GatewayOptions());
        var provider = services.BuildServiceProvider();
        var handlerFactory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        string[] clientNames =
        [
            "IOpenMetadataClient",
            nameof(OpenLineageClient),
            LineageExportServiceCollectionExtensions.OpenJevHttpClientName
        ];

        foreach (var name in clientNames)
        {
            var handler = handlerFactory.CreateHandler(name);
            ContainsSsrfHandler(handler).ShouldBeTrue(name);

            // SEC E-03: hardened primary handler (no redirects, connect-time IP check) for every extension client.
            var primary = GetPrimaryHandler(handler).ShouldBeOfType<SocketsHttpHandler>(name);
            primary.AllowAutoRedirect.ShouldBeFalse(name);
            primary.ConnectCallback.ShouldNotBeNull(name);
        }
    }

    private static HttpMessageHandler GetPrimaryHandler(HttpMessageHandler handler)
    {
        var current = handler;
        while (current is DelegatingHandler delegating && delegating.InnerHandler is { } inner)
        {
            current = inner;
        }

        return current;
    }

    [Fact]
    public async Task EXT_EX12_OpenMetadataTypedClient_BlocksMetadataEndpoint()
    {
        var services = CreateServices(new GatewayOptions());
        var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("IOpenMetadataClient");

        var ex = await Should.ThrowAsync<System.Security.SecurityException>(
            () => client.GetAsync(new Uri("http://169.254.169.254/latest/meta-data/")));
        ex.Message.ShouldContain("strictly forbidden");
    }

    [Fact]
    public void EXT_BackgroundWorkers_AreOnlyRegisteredWhenEnabled()
    {
        var disabled = CreateServices(new GatewayOptions());
        HostedServiceCount<DataCatalogSyncBackgroundService>(disabled).ShouldBe(0);
        HostedServiceCount<OpenMetadataSyncBackgroundService>(disabled).ShouldBe(0);
        HostedServiceCount<CdcProducerService>(disabled).ShouldBe(0);

        var enabled = CreateServices(new GatewayOptions
        {
            Catalog = new DataCatalogOptions { Enabled = true },
            OpenMetadata = new OpenMetadataOptions { Enabled = true },
            MssqlChangeTracking = new MssqlChangeTrackingOptions { Enabled = true }
        });
        HostedServiceCount<DataCatalogSyncBackgroundService>(enabled).ShouldBe(1);
        HostedServiceCount<OpenMetadataSyncBackgroundService>(enabled).ShouldBe(1);
        HostedServiceCount<CdcProducerService>(enabled).ShouldBe(1);
    }

    [Fact]
    public void EXT_LakehouseExecutor_IsRegisteredExactlyOnce()
    {
        var services = CreateServices(new GatewayOptions());

        services.Count(d => d.ServiceType == typeof(IDataSourceExecutor) &&
                            d.ImplementationType == typeof(Autheris.Extensions.Lakehouse.Services.LakehouseDataSourceExecutor))
            .ShouldBe(1);
    }
}
