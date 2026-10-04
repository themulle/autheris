namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Integrations.Backstage;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions;
using Autheris.Extensions.Cdc;
using Autheris.Extensions.DataCatalog;
using Autheris.Extensions.Itsm;
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
/// <c>AddGatewayExtensions</c> (called by <c>AddGatewayInfrastructure</c>). Also covers the EX-12 handler fix and the
/// Alation client hardening.
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

    private static ItsmTicketRequest CreateTicketRequest() => new(
        new TenantId("tenant-a"),
        new Sid("S-1-5-21-1234"),
        new TableIdentifier("finance", "dbo", "invoices"),
        "Need access for the annual audit",
        7,
        null,
        null);

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
    public void EXT_ConnectorInterfaces_HaveExactlyOneImplementation_InExtensionsAssembly()
    {
        var services = CreateServices(new GatewayOptions());

        Type[] typeBasedServices =
        [
            typeof(IDataCatalogSyncService),
            typeof(IDataCatalogClientFactory),
            typeof(IDataCatalogWebhookHandler),
            typeof(IItsmWebhookHandler),
            typeof(IBackstageCatalogExportService),
            typeof(IMssqlChangeTrackingPoller),
            typeof(IMssqlWatermarkStore)
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
    public void EXT_ItsmWorkflowClients_AreExactlyServiceNowAndJira_WithoutDuplicates()
    {
        var services = CreateServices(new GatewayOptions());

        services.Count(d => d.ServiceType == typeof(IItsmWorkflowClient)).ShouldBe(2);

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var clients = scope.ServiceProvider.GetServices<IItsmWorkflowClient>().ToList();

        clients.Count.ShouldBe(2);
        clients.Select(c => c.GetType()).ShouldBe(new[] { typeof(ServiceNowTableApiClient), typeof(JiraCloudRestClient) }, ignoreOrder: true);
        clients.Select(c => c.SystemType).ShouldBe(new[] { ItsmSystemType.ServiceNow, ItsmSystemType.Jira }, ignoreOrder: true);
        clients.ShouldAllBe(c => c.GetType().Assembly == ExtensionsAssembly);
    }

    [Fact]
    public void EXT_DataCatalogClientFactory_ProvidesOneClientPerCatalog_IncludingAlation()
    {
        var services = CreateServices(new GatewayOptions());
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDataCatalogClientFactory>();

        factory.ShouldBeOfType<DataCatalogClientFactory>();
        factory.CreateClient(DataCatalogProviderType.Alation).ShouldBeOfType<AlationCatalogClient>();
        factory.CreateClient(DataCatalogProviderType.MicrosoftPurview).ShouldBeOfType<PurviewDataCatalogClient>();
        factory.CreateClient(DataCatalogProviderType.Collibra).ShouldBeOfType<CollibraDataCatalogClient>();
        factory.CreateClient(DataCatalogProviderType.OpenMetadata).ShouldBeOfType<OpenMetadataCatalogAdapter>();
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
            nameof(ServiceNowTableApiClient),
            nameof(JiraCloudRestClient),
            nameof(OpenLineageClient),
            nameof(PurviewDataCatalogClient),
            nameof(CollibraDataCatalogClient),
            nameof(AlationCatalogClient),
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
    public async Task EXT_EX12_ItsmWorkflowClientsFromDi_UseSsrfProtectedHttpClient()
    {
        // Before EX-12 the IEnumerable<IItsmWorkflowClient> instances were built with the default HttpClient (no SSRF handler).
        var options = new GatewayOptions
        {
            Itsm = new ItsmOptions
            {
                ServiceNowBaseUrl = "http://169.254.169.254",
                JiraBaseUrl = "http://169.254.169.254"
            }
        };

        var provider = CreateServices(options).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var clients = scope.ServiceProvider.GetServices<IItsmWorkflowClient>().ToList();
        clients.Count.ShouldBe(2);

        foreach (var client in clients)
        {
            var result = await client.CreateAccessTicketAsync(CreateTicketRequest());
            result.Success.ShouldBeFalse();
            result.TicketReference.ShouldBeNull();
            result.ErrorCode.ShouldBe("ITSM_UNAVAILABLE");
            result.ErrorMessage.ShouldNotBeNull();
            result.ErrorMessage.ShouldContain("strictly forbidden");
        }
    }

    [Fact]
    public void EXT_BackgroundWorkers_AreOnlyRegisteredWhenEnabled()
    {
        var disabled = CreateServices(new GatewayOptions());
        HostedServiceCount<DataCatalogSyncBackgroundService>(disabled).ShouldBe(0);
        HostedServiceCount<OpenMetadataSyncBackgroundService>(disabled).ShouldBe(0);
        HostedServiceCount<MssqlChangeTrackingHostedService>(disabled).ShouldBe(0);

        var enabled = CreateServices(new GatewayOptions
        {
            Catalog = new DataCatalogOptions { Enabled = true },
            OpenMetadata = new OpenMetadataOptions { Enabled = true },
            MssqlChangeTracking = new MssqlChangeTrackingOptions { Enabled = true }
        });
        HostedServiceCount<DataCatalogSyncBackgroundService>(enabled).ShouldBe(1);
        HostedServiceCount<OpenMetadataSyncBackgroundService>(enabled).ShouldBe(1);
        HostedServiceCount<MssqlChangeTrackingHostedService>(enabled).ShouldBe(1);
    }

    [Fact]
    public void EXT_LakehouseExecutor_IsRegisteredExactlyOnce()
    {
        var services = CreateServices(new GatewayOptions());

        services.Count(d => d.ServiceType == typeof(IDataSourceExecutor) &&
                            d.ImplementationType == typeof(Autheris.Extensions.Lakehouse.Services.LakehouseDataSourceExecutor))
            .ShouldBe(1);
    }

    // ------------------------------------------------------------------ Alation hardening

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }

    private static AlationCatalogClient CreateAlationClient(
        StubHandler handler,
        string baseUrl = "https://alation.corp.example",
        string apiToken = "alation-token-ref",
        IKeyVaultSecretProvider? secretProvider = null,
        IHostEnvironment? environment = null)
    {
        // SEC E-08: the Alation client fails closed without a resolvable token secret; tests that do not focus on the
        // token use a provider that resolves the reference.
        if (secretProvider == null)
        {
            secretProvider = Substitute.For<IKeyVaultSecretProvider>();
            secretProvider.GetSecretBytes(apiToken).Returns(Encoding.UTF8.GetBytes("resolved-alation-token"));
        }

        var options = Options.Create(new GatewayOptions
        {
            Catalog = new DataCatalogOptions
            {
                Provider = DataCatalogProviderType.Alation,
                Alation = new AlationOptions
                {
                    BaseUrl = baseUrl,
                    ApiToken = apiToken,
                    DataSourceToDomainMap = new Dictionary<string, string> { ["7"] = "clinic" }
                }
            }
        });

        return new AlationCatalogClient(new HttpClient(handler), options, NullLogger<AlationCatalogClient>.Instance, secretProvider, environment);
    }

    [Fact]
    public async Task EXT_Alation_SendsTokenResolvedViaSecretProvider_AndMapsTables()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """[{"id":42,"name":"patients","schema_name":"clinical","ds_id":7,"tags":["gdpr_art9"]}]""",
                Encoding.UTF8,
                "application/json")
        });

        var secretProvider = Substitute.For<IKeyVaultSecretProvider>();
        secretProvider.GetSecretBytes("alation-token-ref").Returns(Encoding.UTF8.GetBytes("resolved-alation-token"));

        var client = CreateAlationClient(handler, secretProvider: secretProvider);
        var tables = await client.GetTablesAsync();

        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].RequestUri!.AbsolutePath.ShouldBe("/integration/v2/table/");
        handler.Requests[0].Headers.GetValues("TOKEN").Single().ShouldBe("resolved-alation-token");

        tables.Count.ShouldBe(1);
        tables[0].Identifier.ShouldBe(new TableIdentifier("clinic", "clinical", "patients")); // SEC E-08: explicit ds_id -> domain map
        tables[0].ExternalAssetId.ShouldBe("42");
        tables[0].Tags.ShouldContain("gdpr_art9");
        tables[0].SourceType.ShouldBe("Alation");
    }

    [Fact]
    public async Task EXT_Alation_HttpError_IsPropagated_NotAnEmptyCatalog()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = CreateAlationClient(handler);

        var ex = await Should.ThrowAsync<HttpRequestException>(() => client.GetTablesAsync());
        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EXT_Alation_OversizedResponse_IsRejected()
    {
        var handler = new StubHandler(_ =>
        {
            var content = new StringContent("[]", Encoding.UTF8, "application/json");
            content.Headers.ContentLength = 11L * 1024 * 1024;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var client = CreateAlationClient(handler);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => client.GetTablesAsync());
        ex.Message.ShouldContain("exceeds maximum allowed limit");
    }

    [Fact]
    public async Task EXT_Alation_UnresolvableSecretOutsideDevelopment_FailsClosed()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var secretProvider = Substitute.For<IKeyVaultSecretProvider>();
        secretProvider.GetSecretBytes(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("not found"));
        var prodEnv = Substitute.For<IHostEnvironment>();
        prodEnv.EnvironmentName.Returns(Environments.Production);

        var client = CreateAlationClient(handler, secretProvider: secretProvider, environment: prodEnv);

        await Should.ThrowAsync<System.Security.SecurityException>(() => client.GetTablesAsync());
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task EXT_Alation_MissingBaseUrl_Throws()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = CreateAlationClient(handler, baseUrl: string.Empty);

        await Should.ThrowAsync<InvalidOperationException>(() => client.GetTablesAsync());
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task EXT_Collibra_OversizedResponse_IsRejected()
    {
        var handler = new StubHandler(_ =>
        {
            var content = new StringContent("{\"results\":[]}", Encoding.UTF8, "application/json");
            content.Headers.ContentLength = 11L * 1024 * 1024;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var options = Options.Create(new GatewayOptions
        {
            Catalog = new DataCatalogOptions { Collibra = new CollibraOptions { BaseUrl = "https://collibra.corp.example" } }
        });
        var client = new CollibraDataCatalogClient(new HttpClient(handler), options, NullLogger<CollibraDataCatalogClient>.Instance);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => client.GetTablesAsync());
        ex.Message.ShouldContain("exceeds maximum allowed limit");
    }
}
