namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Hosting;
using Autheris.Api.Hosting.Warmup;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Recertification;
using Autheris.Tests.Common.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class IntegrationHostTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void Startup_validators_and_security_checks_are_never_removed()
    {
        IntegrationHost.AllowedRemovedHostedServices.ShouldNotBeEmpty();
        IntegrationHost.AllowedRemovedHostedServices.ShouldBe(
            [
                typeof(CatalogSearchWarmupService),
                typeof(CatalogSnapshotBackgroundService),
                typeof(GrantExpiryHostedService),
                typeof(RecertificationGovernorHostedService),
                typeof(TrafficDrainHostedService),
            ],
            ignoreOrder: true);

        foreach (var allowed in IntegrationHost.AllowedRemovedHostedServices)
        {
            typeof(IHostedService).IsAssignableFrom(allowed).ShouldBeTrue($"{allowed.FullName} must be an IHostedService");
            IntegrationHost.IsStartupValidatorOrSecurityCheck(allowed).ShouldBeFalse($"{allowed.FullName} must not be a startup validator or security check");
            Should.NotThrow(() => IntegrationHost.ValidateRemovalCandidate(allowed));
        }

        Type[] forbiddenCandidates =
        [
            typeof(FederationMaskingStartupCheck),
            typeof(CdcStreamStartupCheck),
            typeof(CdcWebhookStartupCheck),
            typeof(StartupWarmupOptionsValidator),
            typeof(ColdPathTimingOptionsValidator),
            typeof(DummyStartupFilter),
            typeof(DummySecurityStartupHostedService),
        ];

        foreach (var forbidden in forbiddenCandidates)
        {
            IntegrationHost.IsStartupValidatorOrSecurityCheck(forbidden).ShouldBeTrue(
                $"{forbidden.FullName} must be recognized as a startup validator or security check");
            Should.Throw<InvalidOperationException>(() => IntegrationHost.ValidateRemovalCandidate(forbidden));
            Should.Throw<InvalidOperationException>(() =>
                IntegrationHost.RemoveAllowedBackgroundHostedServices(new ServiceCollection(), [forbidden]));
        }

        // Unlisted non-security background service is also rejected (fail-closed allow-list).
        Should.Throw<InvalidOperationException>(() =>
            IntegrationHost.ValidateRemovalCandidate(typeof(TableCacheBackgroundWorker)));

        IServiceCollection services = new ServiceCollection();
        foreach (var allowed in IntegrationHost.AllowedRemovedHostedServices)
        {
            services.AddSingleton(typeof(IHostedService), allowed);
        }

        services.AddHostedService<FederationMaskingStartupCheck>();
        services.AddHostedService<CdcStreamStartupCheck>();
        services.AddHostedService<CdcWebhookStartupCheck>();
        services.AddHostedService<DummySecurityStartupHostedService>();
        services.AddSingleton<IStartupFilter, DummyStartupFilter>();
        services.AddSingleton<IValidateOptions<StartupWarmupOptions>, StartupWarmupOptionsValidator>();

        var removedCount = IntegrationHost.RemoveAllowedBackgroundHostedServices(services);
        removedCount.ShouldBe(IntegrationHost.AllowedRemovedHostedServices.Count);

        var remainingHostedTypes = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();

        remainingHostedTypes.ShouldBe(
        [
            typeof(FederationMaskingStartupCheck),
            typeof(CdcStreamStartupCheck),
            typeof(CdcWebhookStartupCheck),
            typeof(DummySecurityStartupHostedService),
        ]);

        services.Any(d => d.ServiceType == typeof(IStartupFilter) && d.ImplementationType == typeof(DummyStartupFilter))
            .ShouldBeTrue();
        services.Any(d => d.ServiceType == typeof(IValidateOptions<StartupWarmupOptions>) &&
                          d.ImplementationType == typeof(StartupWarmupOptionsValidator))
            .ShouldBeTrue();
    }

    [Fact]
    public void ReloadableConfig_triggers_configuration_reload_on_put()
    {
        var reloadable = new ReloadableConfig();
        var config = new ConfigurationBuilder()
            .Add(new ReloadableConfigSource(reloadable))
            .Build();

        reloadable.Put("Gateway:WebSql:Enabled", "true");
        config["Gateway:WebSql:Enabled"].ShouldBe("true");

        reloadable.Put("Gateway:WebSql:Enabled", "false");
        config["Gateway:WebSql:Enabled"].ShouldBe("false");
    }

    private sealed class DummyStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => next;
    }

    private sealed class DummySecurityStartupHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
