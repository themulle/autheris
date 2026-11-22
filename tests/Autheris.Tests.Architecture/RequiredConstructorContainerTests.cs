namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Autheris.Api.Configuration;
using Autheris.Api.Extensions;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.OData;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Application.Tenancy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Collection("HostStartupSequential")]
[Trait("Category", "Security")]
public sealed class RequiredConstructorContainerTests
{
    private const string NonConnectingPostgreSql =
        "Host=127.0.0.1;Port=1;Database=autheris_test;Username=autheris_test;Password=not-a-secret;SSL Mode=VerifyFull;Timeout=1";

    private static WebApplicationBuilder CreateGatewayBuilder(string stateDirectory)
    {
        var apiContentRoot = Path.Combine(RepositoryScan.RepoRoot, "src", "Autheris.Api");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = Directory.Exists(apiContentRoot) ? apiContentRoot : stateDirectory
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
            ["Gateway:GovernanceDb:ConnectionString"] = NonConnectingPostgreSql,
            ["Gateway:State:StateDirectory"] = stateDirectory
        });

        var devReport = DevConfiguration.Apply(builder.Configuration, builder.Environment);
        builder.Services.AddSingleton(devReport);
        builder.Services.AddGatewayTelemetry(builder.Configuration);
        builder.Services.AddDataSourceHierarchicalConfiguration(builder.Configuration);
        var gatewayOptions = builder.Services.AddGatewayOptions(builder.Configuration, builder.Environment);
        builder.Services.AddGatewayInfrastructure(gatewayOptions, builder.Environment);
        builder.Services.AddGatewayAuth(gatewayOptions, builder.Environment);
        var demoDataEnabled = DemoDataSwitch.Resolve(gatewayOptions, builder.Environment.EnvironmentName);
        builder.Services.AddSingleton<IDemoDataSwitch>(new DemoDataSwitch(demoDataEnabled));
        builder.Services.AddGatewayGraphQL(gatewayOptions);
        builder.Services.AddAsyncJobsAndDatasourceTesting(builder.Configuration);
        return builder;
    }

    [Fact]
    public async Task GatewayDiContainer_ResolvesODataHandlerAndStreamRlsPolicyEnforcer_InScope()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "autheris-ctor-di-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var builder = CreateGatewayBuilder(tempRoot);
            builder.Host.UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = true;
                options.ValidateOnBuild = true;
            });
            await using var app = builder.Build();
            await using var scope = app.Services.CreateAsyncScope();

            var odataHandler = scope.ServiceProvider.GetRequiredService<IODataHandler>();
            odataHandler.ShouldBeOfType<ODataHandler>();
            ActivatorUtilities.CreateInstance<ODataHandler>(scope.ServiceProvider).ShouldNotBeNull();

            var streamEnforcer = scope.ServiceProvider.GetRequiredService<IStreamRlsPolicyEnforcer>();
            streamEnforcer.ShouldBeOfType<StreamRlsPolicyEnforcer>();
            ActivatorUtilities.CreateInstance<StreamRlsPolicyEnforcer>(scope.ServiceProvider).ShouldNotBeNull();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    public static IEnumerable<object[]> ODataRequiredServices() =>
    [
        [typeof(ITableMetadataRepository)],
        [typeof(IGatewayExecutionService)],
        [typeof(ILogger<ODataHandler>)],
        [typeof(IHostEnvironment)],
        [typeof(IMemoryCache)],
        [typeof(ICatalogVersionProvider)],
        [typeof(ITenantResolver)],
        [typeof(IDataSourceOptionsResolver)],
        [typeof(IIngressTransportContext)],
        [typeof(IODataQueryPolicyEnforcer)]
    ];

    [Theory]
    [MemberData(nameof(ODataRequiredServices))]
    public async Task GatewayDiContainer_WhenODataDependencyIsMissing_FailsAtResolution(Type missingServiceType)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "autheris-ctor-odata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var builder = CreateGatewayBuilder(tempRoot);
            var services = builder.Services;
            services.RemoveAll(missingServiceType);
            if (missingServiceType == typeof(ILogger<ODataHandler>))
            {
                RemoveExactServiceType(services, typeof(ILogger<>));
                services.RemoveAll<ILoggerFactory>();
            }

            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = false
            });
            await using var scope = provider.CreateAsyncScope();

            Should.Throw<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IODataHandler>());
            Should.Throw<InvalidOperationException>(() => ActivatorUtilities.CreateInstance<ODataHandler>(scope.ServiceProvider));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    public static IEnumerable<object[]> StreamRlsRequiredServices() =>
    [
        [typeof(IAccessDecisionPoint)],
        [typeof(ITableMetadataRepository)],
        [typeof(IColumnMaskingProvider)],
        [typeof(IEpochValidationService)],
        [typeof(ILogger<StreamRlsPolicyEnforcer>)],
        [typeof(IConsentRepository)],
        [typeof(IConsentResolutionService)],
        [typeof(IConsentCacheService)],
        [typeof(IClientIpResolver)],
        [typeof(IOptions<GatewayOptions>)],
        [typeof(IMandatoryRowFilterResolver)],
        [typeof(ICallerAttributeResolver)],
        [typeof(TimeProvider)],
        [typeof(IDataSourceTransportGate)]
    ];

    [Theory]
    [MemberData(nameof(StreamRlsRequiredServices))]
    public async Task GatewayDiContainer_WhenStreamRlsDependencyIsMissing_FailsAtResolution(Type missingServiceType)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "autheris-ctor-stream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var builder = CreateGatewayBuilder(tempRoot);
            var services = builder.Services;
            services.RemoveAll(missingServiceType);
            if (missingServiceType == typeof(ILogger<StreamRlsPolicyEnforcer>))
            {
                RemoveExactServiceType(services, typeof(ILogger<>));
                services.RemoveAll<ILoggerFactory>();
            }
            else if (missingServiceType == typeof(IOptions<GatewayOptions>))
            {
                RemoveExactServiceType(services, typeof(IOptions<>));
                services.RemoveAll<IConfigureOptions<GatewayOptions>>();
            }

            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = false
            });
            await using var scope = provider.CreateAsyncScope();

            Should.Throw<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IStreamRlsPolicyEnforcer>());
            Should.Throw<InvalidOperationException>(() => ActivatorUtilities.CreateInstance<StreamRlsPolicyEnforcer>(scope.ServiceProvider));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static void RemoveExactServiceType(IServiceCollection services, Type serviceType)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == serviceType)
            {
                services.RemoveAt(i);
            }
        }
    }
}
