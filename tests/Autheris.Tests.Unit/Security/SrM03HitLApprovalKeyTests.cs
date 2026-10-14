namespace Autheris.Tests.Unit.Security;

using System;
using System.Security.Cryptography;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

public class SrM03HitLApprovalKeyTests
{
    private const string KeyRef = "kv://autheris/hmac-master";

    private static GatewayOptions Options(string keyRef = KeyRef) => new()
    {
        DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = keyRef }
    };

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static IKeyVaultSecretProvider Provider(byte[] secret)
    {
        var provider = Substitute.For<IKeyVaultSecretProvider>();
        provider.GetSecretBytes(Arg.Any<string>()).Returns(secret);
        return provider;
    }

    [Fact]
    public void SR_M_03_Key_IsDerivedFromResolvedSecret_NotFromReferenceString()
    {
        var secretA = RandomNumberGenerator.GetBytes(32);
        var secretB = RandomNumberGenerator.GetBytes(32);

        var keyA = HitLStepUpApprovalService.ResolveHitLHmacKey(Options(), Provider(secretA), Env("Production"));
        var keyA2 = HitLStepUpApprovalService.ResolveHitLHmacKey(Options(), Provider(secretA), Env("Production"));
        var keyB = HitLStepUpApprovalService.ResolveHitLHmacKey(Options(), Provider(secretB), Env("Production"));
        var keyAOtherRef = HitLStepUpApprovalService.ResolveHitLHmacKey(Options("kv://other/ref"), Provider(secretA), Env("Production"));

        keyA.ShouldBe(keyA2);
        keyA.ShouldNotBe(keyB);
        keyA.ShouldBe(keyAOtherRef); // same resolved secret, different reference string -> same key
    }

    [Fact]
    public void SR_M_03_NonDevelopment_WithoutSecretProvider_FailsClosed()
    {
        Should.Throw<InvalidOperationException>(() =>
            HitLStepUpApprovalService.ResolveHitLHmacKey(Options(), null, Env("Production")));
    }

    [Fact]
    public void SR_M_03_NonDevelopment_UnresolvableSecret_FailsClosed()
    {
        var provider = Substitute.For<IKeyVaultSecretProvider>();
        provider.GetSecretBytes(Arg.Any<string>()).Throws(new InvalidOperationException("vault down"));

        Should.Throw<InvalidOperationException>(() =>
            HitLStepUpApprovalService.ResolveHitLHmacKey(Options(), provider, Env("Production")));
    }

    [Fact]
    public void SR_M_03_NonDevelopment_EmptyOrShortSecret_FailsClosed()
    {
        Should.Throw<InvalidOperationException>(() =>
            HitLStepUpApprovalService.ResolveHitLHmacKey(Options(), Provider(Array.Empty<byte>()), Env("Production")));
        Should.Throw<InvalidOperationException>(() =>
            HitLStepUpApprovalService.ResolveHitLHmacKey(Options(), Provider(new byte[8]), Env("Staging")));
    }

    [Fact]
    public void SR_M_03_NonDevelopment_SentinelRef_FailsClosed()
    {
        Should.Throw<InvalidOperationException>(() =>
            HitLStepUpApprovalService.ResolveHitLHmacKey(
                Options("DEV_INSECURE_TEST_KEY_ONLY"), Provider(RandomNumberGenerator.GetBytes(32)), Env("Production")));
    }

    [Fact]
    public void SR_M_03_Development_StillWorksWithoutProvider()
    {
        var key = HitLStepUpApprovalService.ResolveHitLHmacKey(Options(), null, Env("Development"));
        key.Length.ShouldBe(32);
    }
}
