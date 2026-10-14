namespace Autheris.Tests.Unit.Governance;

using System;
using System.Threading.Tasks;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

internal static class WormTestEnvironment
{
    public static IHostEnvironment Development => Create("Development");

    public static IHostEnvironment Create(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }
}

/// <summary>SR-D-02: the WORM configuration sealing key is derived from the audit key vault secret, never hard-coded outside Development.</summary>
public sealed class WormConfigurationSealingKeyTests
{
    private static IOptionsMonitor<GatewayOptions> Monitor(string? auditRef)
    {
        var opts = new GatewayOptions
        {
            Classification = new ClassificationOptions(),
            GovernanceDb = new GovernanceDbOptions { AuditHmacKeyVaultRef = auditRef }
        };
        var monitor = Substitute.For<IOptionsMonitor<GatewayOptions>>();
        monitor.CurrentValue.Returns(opts);
        return monitor;
    }

    private static IKeyVaultSecretProvider Secrets(byte[] secret)
    {
        var provider = Substitute.For<IKeyVaultSecretProvider>();
        provider.GetSecretBytes(Arg.Any<string>()).Returns(secret);
        return provider;
    }

    private static async Task<string> SealAsync(IOptionsMonitor<GatewayOptions> monitor, IKeyVaultSecretProvider? secrets, IHostEnvironment env)
    {
        var svc = new WormConfigurationAuditService(monitor, NullLogger<WormConfigurationAuditService>.Instance, secrets, env);
        var record = await svc.AuditConfigurationSnapshotAsync("T", "SYSTEM");
        return record!.WormSignature;
    }

    [Fact]
    public void SR_D_02_Production_Without_Key_Fails_Closed()
    {
        Should.Throw<InvalidOperationException>(() =>
            new WormConfigurationAuditService(Monitor(null), NullLogger<WormConfigurationAuditService>.Instance, null, WormTestEnvironment.Create("Production")));
        Should.Throw<InvalidOperationException>(() =>
            new WormConfigurationAuditService(Monitor("ref"), NullLogger<WormConfigurationAuditService>.Instance, null, WormTestEnvironment.Create("Production")));
    }

    [Fact]
    public void SR_D_02_Production_With_Short_Key_Fails_Closed()
    {
        Should.Throw<InvalidOperationException>(() =>
            new WormConfigurationAuditService(Monitor("ref"), NullLogger<WormConfigurationAuditService>.Instance, Secrets(new byte[8]), WormTestEnvironment.Create("Production")));
    }

    [Fact]
    public async Task SR_D_02_Signature_Depends_On_Vault_Secret()
    {
        var prod = WormTestEnvironment.Create("Production");
        var secretA = new byte[32];
        var secretB = new byte[32];
        secretB[0] = 1;

        var sigA1 = await SealAsync(Monitor("ref"), Secrets(secretA), prod);
        var sigA2 = await SealAsync(Monitor("ref"), Secrets(secretA), prod);
        var sigB = await SealAsync(Monitor("ref"), Secrets(secretB), prod);
        var sigDev = await SealAsync(Monitor(null), null, WormTestEnvironment.Development);

        // Records carry a random id/timestamp but the signature covers epoch/trigger/actor/hashes only.
        sigA1.ShouldBe(sigA2);
        sigA1.ShouldNotBe(sigB);
        sigA1.ShouldNotBe(sigDev);
    }

    [Fact]
    public async Task SR_D_02_Development_Without_Key_Uses_Development_Key()
    {
        var sig = await SealAsync(Monitor(null), null, WormTestEnvironment.Development);
        sig.ShouldNotBeNullOrWhiteSpace();
    }
}
