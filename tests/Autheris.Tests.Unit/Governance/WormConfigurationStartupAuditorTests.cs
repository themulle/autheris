namespace Autheris.Tests.Unit.Governance;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class WormConfigurationStartupAuditorTests
{
    [Fact]
    public async Task StartAsync_MintsEpochOneChainedToGenesis_AndInitializesCurrentConfigHash()
    {
        var monitor = new TestOptionsMonitor<GatewayOptions>(new GatewayOptions { Classification = new ClassificationOptions() });
        var audit = new WormConfigurationAuditService(monitor, NullLogger<WormConfigurationAuditService>.Instance, environment: WormTestEnvironment.Development);
        audit.CurrentConfigHash.ShouldBeNull();

        var sut = new WormConfigurationStartupAuditor(audit, NullLogger<WormConfigurationStartupAuditor>.Instance);
        await sut.StartAsync(CancellationToken.None);

        audit.CurrentConfigHash.ShouldNotBeNull();
        audit.CurrentConfigHash.Length.ShouldBe(64);

        // Epoch 1 was already minted by the startup audit: an identical snapshot is deduplicated.
        (await audit.AuditConfigurationSnapshotAsync("CONFIGMAP_RELOAD")).ShouldBeNull();

        // A subsequent change chains to the startup hash at epoch 2 (proves startup minted epoch 1).
        var startupHash = audit.CurrentConfigHash;
        monitor.SetCurrent(new GatewayOptions { Classification = new ClassificationOptions { FourEyesThresholdRank = 2 } });
        var next = await audit.AuditConfigurationSnapshotAsync("ADMIN_API_UPDATE");
        next.ShouldNotBeNull();
        next.PolicyEpoch.ShouldBe(2);
        next.PreviousConfigHash.ShouldBe(startupHash);
    }

    [Fact]
    public async Task StartAsync_AuditsExactlyOnce_WithStartupTrigger()
    {
        var audit = Substitute.For<IWormConfigurationAuditService>();
        var sut = new WormConfigurationStartupAuditor(audit, NullLogger<WormConfigurationStartupAuditor>.Instance);

        await sut.StartAsync(CancellationToken.None);

        await audit.Received(1).AuditConfigurationSnapshotAsync("STARTUP", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T currentValue) => CurrentValue = currentValue;
        public T CurrentValue { get; private set; }
        public void SetCurrent(T val) => CurrentValue = val;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
