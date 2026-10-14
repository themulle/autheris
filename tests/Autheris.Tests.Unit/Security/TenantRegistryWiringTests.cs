namespace Autheris.Tests.Unit.Security;

using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Security;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class TenantRegistryWiringTests
{
    private sealed class Monitor(GatewayOptions options) : IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue => options;
        public GatewayOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }

    [Fact]
    public void RegistryDisabledByDefault_NothingIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddAutherisGovernance(new GatewayOptions());

        services.Any(d => d.ServiceType == typeof(ITenantRegistry)).ShouldBeFalse();
    }

    [Fact]
    public void RegistryEnabled_ResolvesTheGateOverTheGovernanceRepository()
    {
        var options = new GatewayOptions { TenantIsolation = new TenantIsolationOptions { Registry = new TenantRegistryOptions { Enabled = true } } };
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEpochValidationService>());
        services.AddAutherisGovernance(options);

        var descriptor = services.Single(d => d.ServiceType == typeof(ITenantRegistry));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
        services.Any(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && d.ImplementationType == typeof(TenantRegistryPreseedService)).ShouldBeTrue();
    }

    [Fact]
    public async Task PreseedService_SeedsTheConfiguredTenantIds()
    {
        var registry = Substitute.For<ITenantRegistry>();
        var options = new GatewayOptions
        {
            Authentication = new AuthenticationOptions { ForwardAuth = new ForwardAuthOptions { DefaultTenantId = "default-t", AllowedTenantIds = ["t1", "T1"] } }
        };
        var service = new TenantRegistryPreseedService(registry, new Monitor(options));

        await service.StartAsync(default);

        await registry.Received(1).PreseedAsync(
            Arg.Is<IEnumerable<string>>(ids => ids.Contains("default-t") && ids.Contains("t1") && ids.Contains("T1")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreseedService_RegistryFailure_FailsTheStart()
    {
        var registry = Substitute.For<ITenantRegistry>();
        registry.PreseedAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>()).Returns<ValueTask>(_ => throw new InvalidOperationException("down"));
        var service = new TenantRegistryPreseedService(registry, new Monitor(new GatewayOptions()));

        await Should.ThrowAsync<InvalidOperationException>(() => service.StartAsync(default));
    }
}
