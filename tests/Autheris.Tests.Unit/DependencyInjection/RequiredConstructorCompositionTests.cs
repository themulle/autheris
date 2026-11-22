namespace Autheris.Tests.Unit.DependencyInjection;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
using Autheris.Application.Services;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Application.Tenancy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Common.Governance;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class RequiredConstructorCompositionTests
{
    private static WebApplicationBuilder CreateGatewayBuilder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = System.IO.Path.GetTempPath()
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
            ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestConnectionStrings.NonConnectingPostgreSql,
            ["Gateway:State:StateDirectory"] = System.IO.Path.GetTempPath()
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
    public void ODataHandler_PublicConstructor_IsSingleNonOptionalActivatorConstructor()
    {
        var ctors = typeof(ODataHandler).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var ctor = ctors.ShouldHaveSingleItem();
        ctor.IsDefined(typeof(ActivatorUtilitiesConstructorAttribute), inherit: false).ShouldBeTrue();

        var nullability = new NullabilityInfoContext();
        foreach (var param in ctor.GetParameters())
        {
            param.IsOptional.ShouldBeFalse($"Parameter '{param.Name}' must be required.");
            param.HasDefaultValue.ShouldBeFalse($"Parameter '{param.Name}' must not have a default value.");
            nullability.Create(param).ReadState.ShouldBe(
                NullabilityState.NotNull,
                $"Parameter '{param.Name}' must be non-nullable.");
        }
    }

    [Fact]
    public void StreamRlsPolicyEnforcer_PublicConstructor_IsSingleNonOptionalActivatorConstructor()
    {
        var ctors = typeof(StreamRlsPolicyEnforcer).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var ctor = ctors.ShouldHaveSingleItem();
        ctor.IsDefined(typeof(ActivatorUtilitiesConstructorAttribute), inherit: false).ShouldBeTrue();

        var nullability = new NullabilityInfoContext();
        foreach (var param in ctor.GetParameters())
        {
            param.IsOptional.ShouldBeFalse($"Parameter '{param.Name}' must be required.");
            param.HasDefaultValue.ShouldBeFalse($"Parameter '{param.Name}' must not have a default value.");
            nullability.Create(param).ReadState.ShouldBe(
                NullabilityState.NotNull,
                $"Parameter '{param.Name}' must be non-nullable.");
        }
    }

    [Fact]
    public void ODataHandler_Constructor_ThrowsArgumentNullException_WhenAnyDependencyIsNull()
    {
        var ctor = typeof(ODataHandler).GetConstructors(BindingFlags.Public | BindingFlags.Instance).ShouldHaveSingleItem();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var tenantResolver = new TenantResolver(() => new TenancyOptions(), null, NullLogger<TenantResolver>.Instance);
        object[] ValidArgs() =>
        [
            new InMemoryTableMetadataRepository(),
            new StubGatewayExecutionService(),
            NullLogger<ODataHandler>.Instance,
            new TestHostEnvironment(Environments.Production),
            memoryCache,
            StaticCatalogVersionProvider.Instance,
            tenantResolver,
            PermissiveDataSourceOptionsResolver.Instance,
            new IngressTransportContext(),
            DenyAllODataQueryPolicyEnforcer.Instance
        ];

        var parameters = ctor.GetParameters();
        parameters.Length.ShouldBe(10);

        for (var i = 0; i < parameters.Length; i++)
        {
            var args = ValidArgs();
            args[i] = null!;
            var ex = Should.Throw<TargetInvocationException>(() => ctor.Invoke(args));
            var argNull = ex.InnerException.ShouldBeOfType<ArgumentNullException>();
            argNull.ParamName.ShouldBe(parameters[i].Name);
        }
    }

    [Fact]
    public void StreamRlsPolicyEnforcer_Constructor_ThrowsArgumentNullException_WhenAnyDependencyIsNull()
    {
        var ctor = typeof(StreamRlsPolicyEnforcer).GetConstructors(BindingFlags.Public | BindingFlags.Instance).ShouldHaveSingleItem();
        var options = Options.Create(new GatewayOptions());
        var consentRepo = new InMemoryConsentRepository();
        var resolution = new ConsentResolutionService();
        var pdp = new AccessDecisionPoint(options, resolution, consentRepo);
        object[] ValidArgs() =>
        [
            pdp,
            new InMemoryTableMetadataRepository(),
            StubColumnMaskingProvider.Null,
            new FakeEpochValidationService(),
            NullLogger<StreamRlsPolicyEnforcer>.Instance,
            consentRepo,
            resolution,
            new InMemoryConsentCacheService(),
            NullClientIpResolver.Instance,
            options,
            NullMandatoryRowFilterResolver.Instance,
            DisabledCallerAttributeResolver.Instance,
            TimeProvider.System,
            AllowAllTransportGate.Instance
        ];

        var parameters = ctor.GetParameters();
        parameters.Length.ShouldBe(14);

        for (var i = 0; i < parameters.Length; i++)
        {
            var args = ValidArgs();
            args[i] = null!;
            var ex = Should.Throw<TargetInvocationException>(() => ctor.Invoke(args));
            var argNull = ex.InnerException.ShouldBeOfType<ArgumentNullException>();
            argNull.ParamName.ShouldBe(parameters[i].Name);
        }

        // Also reject IOptions<GatewayOptions> whose Value is null.
        var nullOptArgs = ValidArgs();
        var optionsIndex = Array.FindIndex(parameters, p => p.ParameterType == typeof(IOptions<GatewayOptions>));
        nullOptArgs[optionsIndex] = new NullValueGatewayOptions();
        var optEx = Should.Throw<TargetInvocationException>(() => ctor.Invoke(nullOptArgs));
        optEx.InnerException.ShouldBeOfType<ArgumentNullException>().ParamName.ShouldBe("options");
    }

    [Fact]
    public async Task GatewayDiContainer_ResolvesODataHandlerAndStreamRlsPolicyEnforcer_InScope()
    {
        var builder = CreateGatewayBuilder();
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
        var builder = CreateGatewayBuilder();
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
        var builder = CreateGatewayBuilder();
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

    private sealed class NullValueGatewayOptions : IOptions<GatewayOptions>
    {
        public GatewayOptions Value => null!;
    }
}
