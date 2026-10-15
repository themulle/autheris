using System.Security.Cryptography;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.CdcStream;

/// <summary>REQ-CDC-99 I1-C: KEK resolution and key separation (SEC-CDC99-14).</summary>
public sealed class KeyVaultCdcKekProviderTests
{
    private readonly IKeyVaultSecretProvider _secrets = Substitute.For<IKeyVaultSecretProvider>();

    private KeyVaultCdcKekProvider Create(GatewayOptions options, string env = "Production")
    {
        var monitor = Substitute.For<IOptionsMonitor<GatewayOptions>>();
        monitor.CurrentValue.Returns(options);
        var host = Substitute.For<IHostEnvironment>();
        host.EnvironmentName.Returns(env);
        return new KeyVaultCdcKekProvider(_secrets, monitor, host, NullLogger<KeyVaultCdcKekProvider>.Instance);
    }

    private static GatewayOptions Opts(string? active, params (string Id, string Ref)[] keks) => new()
    {
        CdcStream = new CdcStreamOptions
        {
            Encryption = new CdcStreamEncryptionOptions
            {
                ActiveKekId = active,
                Keks = keks.Select(k => new CdcKekReference { Id = k.Id, SecretRef = k.Ref }).ToList()
            }
        }
    };

    [Fact]
    public void Resolves_HkdfDerived32ByteKey_AndIsStable()
    {
        _secrets.GetSecretBytes("s1").Returns(RandomNumberGenerator.GetBytes(40));
        var p = Create(Opts("k1", ("k1", "s1")));
        p.ActiveKekId.ShouldBe("k1");
        p.TryGetKek("k1", out var a).ShouldBeTrue();
        a.Length.ShouldBe(32);
        p.TryGetKek("k1", out var b).ShouldBeTrue();
        b.ToArray().ShouldBe(a.ToArray());
        _secrets.Received(1).GetSecretBytes("s1");
    }

    [Fact]
    public void DifferentSecrets_YieldDifferentKeys_AndKeyIsNotTheRawSecret()
    {
        var s1 = RandomNumberGenerator.GetBytes(32);
        _secrets.GetSecretBytes("s1").Returns(s1);
        _secrets.GetSecretBytes("s2").Returns(RandomNumberGenerator.GetBytes(32));
        var p = Create(Opts("k1", ("k1", "s1"), ("k2", "s2")));
        p.TryGetKek("k1", out var a);
        p.TryGetKek("k2", out var b);
        a.ToArray().ShouldNotBe(b.ToArray());
        a.ToArray().ShouldNotBe(s1);
    }

    [Fact]
    public void ShortSecret_UnknownId_AndThrowingProvider_AreUnavailable()
    {
        _secrets.GetSecretBytes("short").Returns(new byte[16]);
        _secrets.GetSecretBytes("boom").Returns(_ => throw new InvalidOperationException("vault secret name leaked"));
        var p = Create(Opts("k1", ("k1", "short"), ("k2", "boom")));
        p.TryGetKek("k1", out _).ShouldBeFalse();
        p.TryGetKek("k2", out _).ShouldBeFalse();
        p.TryGetKek("nope", out _).ShouldBeFalse();
        p.TryGetKek("", out _).ShouldBeFalse();
    }

    [Fact]
    public void WebhookKekReference_IsRefused()
    {
        _secrets.GetSecretBytes("cdc-webhook-kek").Returns(RandomNumberGenerator.GetBytes(32));
        Create(Opts("k1", ("k1", "cdc-webhook-kek"))).TryGetKek("k1", out _).ShouldBeFalse();
    }

    [Fact]
    public void MaskingSecretReference_IsRefused()
    {
        var o = Opts("k1", ("k1", "masking-secret"));
        var opts = new GatewayOptions { CdcStream = o.CdcStream, DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "masking-secret" } };
        _secrets.GetSecretBytes("masking-secret").Returns(RandomNumberGenerator.GetBytes(32));
        Create(opts).TryGetKek("k1", out _).ShouldBeFalse();
    }

    [Fact]
    public void Ephemeral_OnlyInDevelopmentWithoutConfiguration()
    {
        var dev = Create(new GatewayOptions(), "Development");
        dev.ActiveKekId.ShouldBe(KeyVaultCdcKekProvider.EphemeralKekId);
        dev.TryGetKek(KeyVaultCdcKekProvider.EphemeralKekId, out var a).ShouldBeTrue();
        dev.TryGetKek(KeyVaultCdcKekProvider.EphemeralKekId, out var b).ShouldBeTrue();
        a.ToArray().ShouldBe(b.ToArray());

        var prod = Create(new GatewayOptions(), "Production");
        prod.ActiveKekId.ShouldBeNull();
        prod.TryGetKek(KeyVaultCdcKekProvider.EphemeralKekId, out _).ShouldBeFalse();

        var staging = Create(new GatewayOptions(), "Staging");
        staging.ActiveKekId.ShouldBeNull();
        staging.TryGetKek(KeyVaultCdcKekProvider.EphemeralKekId, out _).ShouldBeFalse();
    }

    [Fact]
    public void Ephemeral_IsNotUsedWhenKeksAreConfigured()
    {
        _secrets.GetSecretBytes("s1").Returns(new byte[8]);
        var dev = Create(Opts("k1", ("k1", "s1")), "Development");
        dev.TryGetKek(KeyVaultCdcKekProvider.EphemeralKekId, out _).ShouldBeFalse();
        dev.TryGetKek("k1", out _).ShouldBeFalse();
    }
}
