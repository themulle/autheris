namespace Autheris.Tests.Unit.Governance;

using System;
using System.Threading.Tasks;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class WormConfigurationAuditServiceTests
{
    [Fact]
    public async Task AuditConfigurationSnapshotAsync_MintsWormRecordOnStartup_AndSuppressesDuplicates()
    {
        var gatewayOptions = new GatewayOptions
        {
            Classification = new ClassificationOptions()
        };
        var optionsMonitor = new TestOptionsMonitor<GatewayOptions>(gatewayOptions);
        var auditService = new WormConfigurationAuditService(optionsMonitor, NullLogger<WormConfigurationAuditService>.Instance);

        // 1. Startup snapshot
        var initialRecord = await auditService.AuditConfigurationSnapshotAsync("STARTUP_BOOTSTRAP", "SYSTEM");

        initialRecord.ShouldNotBeNull();
        initialRecord.PolicyEpoch.ShouldBe(1);
        initialRecord.Trigger.ShouldBe("STARTUP_BOOTSTRAP");
        initialRecord.PreviousConfigHash.ShouldBe("0000000000000000000000000000000000000000000000000000000000000000");
        initialRecord.Sha256ConfigHash.ShouldNotBeNullOrWhiteSpace();
        initialRecord.WormSignature.ShouldNotBeNullOrWhiteSpace();

        // 2. Immediate second call with identical options -> Must return null (tamper-proof deduplication)
        var secondCall = await auditService.AuditConfigurationSnapshotAsync("CONFIGMAP_RELOAD", "SYSTEM");
        secondCall.ShouldBeNull();

        // 3. Configuration change (e.g. four eyes threshold modified) -> Must mint epoch 2 with previous hash linked
        var updatedOptions = new GatewayOptions
        {
            Classification = new ClassificationOptions
            {
                FourEyesThresholdRank = 2 // Changed from 3 to 2
            }
        };
        optionsMonitor.SetCurrent(updatedOptions);

        var changedRecord = await auditService.AuditConfigurationSnapshotAsync("ADMIN_API_UPDATE", "admin@corp.local");

        changedRecord.ShouldNotBeNull();
        changedRecord.PolicyEpoch.ShouldBe(2);
        changedRecord.Trigger.ShouldBe("ADMIN_API_UPDATE");
        changedRecord.ActorSid.ShouldBe("admin@corp.local");
        changedRecord.PreviousConfigHash.ShouldBe(initialRecord.Sha256ConfigHash);
        changedRecord.Sha256ConfigHash.ShouldNotBe(initialRecord.Sha256ConfigHash);
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
