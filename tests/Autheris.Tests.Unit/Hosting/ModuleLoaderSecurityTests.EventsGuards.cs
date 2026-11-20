namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Linq;
using System.Security.Cryptography;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Hosting;
using Autheris.Modules.Events;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Webhooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

/// <summary>Host guards that the events module relies on: own hosted services only, protected ports, module HttpClient egress.</summary>
[Trait("Category", "Security")]
public sealed partial class ModuleLoaderSecurityTests
{
    private ServiceCollection LoadAs(string moduleId, Type entryType, Action<ServiceCollection>? seed = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedModuleForEntryType(key, moduleId, entryType);

        var services = new ServiceCollection();
        services.AddHostedService<HostHostedService>();
        seed?.Invoke(services);
        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false);
        loader.LoadAndConfigureServices(services, BuildConfig(moduleId), new FakeHostEnvironment { EnvironmentName = Environments.Production });
        return services;
    }

    private void AssertRejectedWith(string moduleId, Type entryType, string reasonFragment, Action<ServiceCollection>? seed = null)
    {
        var ex = Should.Throw<ModuleLoadException>(() => LoadAs(moduleId, entryType, seed));
        ex.ModuleId.ShouldBe(moduleId);
        ex.Reason.ShouldContain(reasonFragment);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Events_module_hosted_services_allowed_only_from_module_assembly()
    {
        // Own assembly: allowed, and the host descriptor stays in place.
        var services = LoadAs("own-hosted", typeof(OwnHostedServiceAddingModule));
        services.Count(d => d.ServiceType == typeof(IHostedService)).ShouldBe(2);
        services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(HostHostedService)).ShouldBeTrue();

        // A hosted service declared in a host assembly is still refused, naming module and type.
        AssertRejectedWith("evil-hosted", typeof(HostedServiceAddingModule), "IHostedService");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Module_replacing_hosted_service_or_stream_enforcer_fails_startup()
    {
        AssertRejectedWith("evil-hosted-remove", typeof(HostedServiceRemovingModule), "IHostedService");
        AssertRejectedWith(
            "evil-enforcer",
            typeof(StreamEnforcerReplacingModule),
            nameof(IStreamRlsPolicyEnforcer),
            s => s.AddScoped(_ => NSubstitute.Substitute.For<IStreamRlsPolicyEnforcer>()));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Module_replacing_secret_protector_or_key_store_fails_startup()
    {
        AssertRejectedWith(
            "evil-protector",
            typeof(SecretProtectorReplacingModule),
            nameof(IWebhookSecretProtector),
            s => s.AddSingleton(NSubstitute.Substitute.For<IWebhookSecretProtector>()));
        AssertRejectedWith(
            "evil-keystore",
            typeof(KeyStoreReplacingModule),
            nameof(ICdcStreamKeyStore),
            s => s.AddSingleton(NSubstitute.Substitute.For<ICdcStreamKeyStore>()));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Module_httpclient_without_ssrf_handler_fails_startup()
    {
        AssertRejectedWith("evil-httpclient", typeof(UnprotectedHttpClientAddingModule), "SSRF");
        LoadAs("good-httpclient", typeof(ProtectedHttpClientAddingModule)).ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Webhook_client_chain_has_ssrf_handler_when_events_module_loaded()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAutherisEvents(new Autheris.Domain.Options.GatewayOptions());
        services.AddEventsModule(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        ModuleHttpClientGuard.ChainHasSsrfHandler(services, nameof(WebhookDeliveryClient)).ShouldBeTrue();
        // Negative control: a client without the handler is not accepted.
        services.AddHttpClient("plain").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(1));
        ModuleHttpClientGuard.ChainHasSsrfHandler(services, "plain").ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Lineage_client_chain_has_ssrf_handler_when_lineage_module_loaded()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        Autheris.Modules.Lineage.LineageServiceCollectionExtensions.AddLineageModule(
            services,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        ModuleHttpClientGuard.ChainHasSsrfHandler(services, nameof(Autheris.Extensions.Lineage.OpenLineageClient)).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Protected_snapshot_lists_the_stream_and_webhook_ports()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NSubstitute.Substitute.For<IStreamRlsPolicyEnforcer>());
        services.AddSingleton(NSubstitute.Substitute.For<ICdcStreamCrypto>());
        services.AddSingleton(NSubstitute.Substitute.For<ICdcStreamKeyStore>());
        services.AddSingleton(NSubstitute.Substitute.For<ICdcStreamStore>());
        services.AddSingleton(NSubstitute.Substitute.For<ICdcKeyEncryptionKeyProvider>());
        services.AddSingleton(NSubstitute.Substitute.For<IWebhookSecretProtector>());
        services.AddSingleton(NSubstitute.Substitute.For<IWebhookOutbox>());

        ModuleSecurityGuards.CaptureProtectedServicesSnapshot(services).Count.ShouldBe(7);
    }

    private sealed class HostHostedService : IHostedService
    {
        public System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken cancellationToken) => System.Threading.Tasks.Task.CompletedTask;

        public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken cancellationToken) => System.Threading.Tasks.Task.CompletedTask;
    }
}
