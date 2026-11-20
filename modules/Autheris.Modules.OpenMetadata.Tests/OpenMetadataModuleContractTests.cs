namespace Autheris.Modules.OpenMetadata.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Configuration;
using Autheris.Api.Hosting;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Autheris.Modules.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Shouldly;

public sealed class OpenMetadataModuleContractTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void OpenMetadata_module_maps_no_routes()
    {
        var module = new OpenMetadataModule();
        var registrar = new RecordingEndpointRegistrar();

        module.MapEndpoints(registrar);

        registrar.GroupCalls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OpenMetadata_module_registers_ports_with_implementation_types()
    {
        var services = new ServiceCollection();
        var module = new OpenMetadataModule();
        module.ConfigureServices(services, new ConfigurationBuilder().Build(), new TestHostContext());

        var moduleAssembly = typeof(OpenMetadataModule).Assembly;
        Type[] ownedPorts =
        [
            typeof(IOpenMetadataSyncService),
            typeof(IOpenMetadataWebhookHandler),
            typeof(IDataCatalogClient),
        ];

        foreach (var portType in ownedPorts)
        {
            var descriptors = services.Where(d => d.ServiceType == portType).ToList();
            descriptors.Count.ShouldBe(1, portType.FullName);
            descriptors[0].ImplementationType.ShouldNotBeNull(portType.FullName);
            descriptors[0].ImplementationType!.Assembly.ShouldBe(moduleAssembly, portType.FullName);
            descriptors[0].ImplementationFactory.ShouldBeNull(portType.FullName);
            descriptors[0].ImplementationInstance.ShouldBeNull(portType.FullName);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OpenMetadata_http_client_uses_secure_outbound_handlers()
    {
        var services = new ServiceCollection();
        var before = ModuleHttpClientGuard.CaptureClientConfiguration(services);
        new OpenMetadataModule().ConfigureServices(services, new ConfigurationBuilder().Build(), new TestHostContext());

        ModuleHttpClientGuard.ChainHasSsrfHandler(services, "IOpenMetadataClient").ShouldBeTrue();
        Should.NotThrow(() => ModuleHttpClientGuard.VerifyModuleClientsUseSsrfHandler("openmetadata", services, before));
        services.Any(d => d.ServiceType == typeof(SsrfProtectionHandler)).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OpenMetadata_module_registers_no_hosted_service()
    {
        var services = new ServiceCollection();
        new OpenMetadataModule().ConfigureServices(services, new ConfigurationBuilder().Build(), new TestHostContext());

        services.Where(d => d.ServiceType == typeof(IHostedService)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OpenMetadata_module_registers_only_its_typed_http_client()
    {
        var services = new ServiceCollection();
        var before = ModuleHttpClientGuard.CaptureClientConfiguration(services);
        new OpenMetadataModule().ConfigureServices(services, new ConfigurationBuilder().Build(), new TestHostContext());

        var addedConfigs = ModuleHttpClientGuard.CaptureClientConfiguration(services)
            .Where(d => !before.Contains(d))
            .ToList();

        addedConfigs
            .Where(d => d.ServiceType == typeof(IHttpMessageHandlerBuilderFilter) &&
                        d.ImplementationType?.Assembly != typeof(IHttpMessageHandlerBuilderFilter).Assembly)
            .ShouldBeEmpty();

        var clientNames = addedConfigs
            .Where(d => d.ServiceType != typeof(IHttpMessageHandlerBuilderFilter))
            .Select(d =>
            {
                var named = d.ImplementationInstance.ShouldBeAssignableTo<Microsoft.Extensions.Options.ConfigureNamedOptions<HttpClientFactoryOptions>>();
                named.ShouldNotBeNull();
                named.Name.ShouldNotBeNull();
                return named.Name!;
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();

        clientNames.ShouldBe(["IOpenMetadataClient"]);
        Should.NotThrow(() => ModuleHttpClientGuard.VerifyModuleClientsUseSsrfHandler("openmetadata", services, before));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OpenMetadata_module_loads_under_the_service_guard()
    {
        var services = new ServiceCollection();
        services.AddAuthorization();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Enabled:0"] = "openmetadata",
                ["Gateway:Modules:AllowUnsignedInDevelopment"] = "true",
                ["Gateway:Modules:WorkspacePath"] = Path.Combine(FindRepositoryRoot(), "modules", "Autheris.Modules.OpenMetadata", "bin"),
            })
            .Build();
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Development };

        var loader = new ModuleLoader(isDirectoryWritableOverride: _ => false);
        var result = loader.LoadAndConfigureServices(services, config, env);

        result.Modules.Select(m => m.Id).ShouldBe(["openmetadata"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OpenMetadata_config_without_module_fails_fast()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:OpenMetadata:Enabled"] = "true",
                ["Gateway:OpenMetadata:WebhookSecret"] = "configured-secret"
            })
            .Build();

        var ex = Should.Throw<InvalidOperationException>(() =>
            UnknownGatewayKeyGuard.ThrowIfUnknownKeys(
                config,
                new ModuleLoadResult([]),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [GatewaySections.OpenMetadata] = "openmetadata"
                }));

        ex.Message.ShouldContain("Gateway:OpenMetadata");
        ex.Message.ShouldContain("openmetadata");
    }

    [Fact]
    public void OpenMetadata_early_and_module_key_passes_accept_config_when_enabled()
    {
        var services = new ServiceCollection();
        services.AddAuthorization();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Modules:Enabled:0"] = "openmetadata",
                ["Gateway:Modules:AllowUnsignedInDevelopment"] = "true",
                ["Gateway:Modules:WorkspacePath"] = Path.Combine(FindRepositoryRoot(), "modules", "Autheris.Modules.OpenMetadata", "bin"),
                ["Gateway:OpenMetadata:Enabled"] = "true",
                ["Gateway:OpenMetadata:WebhookSecret"] = "configured-secret",
            })
            .Build();
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Development };

        Should.NotThrow(() => UnknownGatewayKeyGuard.ThrowIfUnknownKeys(config));

        var loader = new ModuleLoader(isDirectoryWritableOverride: _ => false);
        var result = loader.LoadAndConfigureServices(services, config, env);

        Should.NotThrow(() =>
            UnknownGatewayKeyGuard.ThrowIfUnknownKeys(
                config,
                result,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [GatewaySections.OpenMetadata] = "openmetadata"
                }));
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root (Autheris.sln) not found.");
    }

    private sealed class TestHostContext : IModuleHostContext
    {
        public TimeProvider Time => TimeProvider.System;
        public string HostVersion => "0.0.0-test";
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Autheris.Modules.OpenMetadata.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingEndpointRegistrar : IModuleEndpointRegistrar
    {
        public int GroupCalls { get; private set; }

        public RouteGroupBuilder MapGroup(string prefix)
        {
            GroupCalls++;
            throw new NotSupportedException("OpenMetadata module must not map routes.");
        }
    }
}
