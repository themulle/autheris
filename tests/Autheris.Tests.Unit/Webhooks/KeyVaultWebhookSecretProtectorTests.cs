namespace Autheris.Tests.Unit.Webhooks;

using System;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Application.Webhooks;
using Autheris.Infrastructure.Security;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class KeyVaultWebhookSecretProtectorTests
{
    private static KeyVaultWebhookSecretProtector Create(byte[]? kek, bool throws = false)
    {
        var vault = Substitute.For<IKeyVaultSecretProvider>();
        if (throws)
        {
            vault.GetSecretBytes(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("not configured"));
        }
        else
        {
            vault.GetSecretBytes("cdc-webhook-kek").Returns(kek!);
        }

        return new KeyVaultWebhookSecretProtector(vault);
    }

    private static readonly byte[] Kek = Encoding.UTF8.GetBytes(new string('k', 40));

    [Fact]
    public void ProtectThenUnprotect_RoundTrips()
    {
        var p = Create(Kek);

        var protectedValue = p.Protect("t1", "s1", "super-secret-value");

        p.Unprotect("t1", "s1", protectedValue).ShouldBe("super-secret-value");
    }

    [Fact]
    public void ProtectedValue_ContainsNoPlaintext_AndIsVersionedAndRandomised()
    {
        var p = Create(Kek);

        var a = p.Protect("t1", "s1", "super-secret-value");
        var b = p.Protect("t1", "s1", "super-secret-value");

        a.ShouldStartWith("enc:v1:");
        a.ShouldNotContain("super-secret-value");
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Unprotect_WithOtherSubscriptionOrTenant_Throws()
    {
        var p = Create(Kek);
        var protectedValue = p.Protect("t1", "s1", "super-secret-value");

        Should.Throw<WebhookSecretUnavailableException>(() => p.Unprotect("t1", "s2", protectedValue));
        Should.Throw<WebhookSecretUnavailableException>(() => p.Unprotect("t2", "s1", protectedValue));
    }

    [Fact]
    public void Unprotect_WithDifferentKek_Throws()
    {
        var protectedValue = Create(Kek).Protect("t1", "s1", "x-secret-value");

        Should.Throw<WebhookSecretUnavailableException>(() =>
            Create(Encoding.UTF8.GetBytes(new string('z', 40))).Unprotect("t1", "s1", protectedValue));
    }

    [Theory]
    [InlineData("")]
    [InlineData("enc:v1:")]
    [InlineData("enc:v1:!!!notbase64")]
    [InlineData("enc:v1:AAAA")]
    [InlineData("plain")]
    public void Unprotect_Malformed_Throws(string value) =>
        Should.Throw<WebhookSecretUnavailableException>(() => Create(Kek).Unprotect("t1", "s1", value));

    [Fact]
    public void ShortKek_IsNotAvailable_AndProtectThrows()
    {
        var p = Create(Encoding.UTF8.GetBytes("too-short"));

        p.IsAvailable.ShouldBeFalse();
        Should.Throw<WebhookSecretUnavailableException>(() => p.Protect("t1", "s1", "x"));
    }

    [Fact]
    public void UnresolvableKek_IsNotAvailable_AndNeverThrowsFromIsAvailable() =>
        Create(null, throws: true).IsAvailable.ShouldBeFalse();

    [Fact]
    public void ValidKek_IsAvailable() => Create(Kek).IsAvailable.ShouldBeTrue();

    [Fact]
    public void Kek_IsResolvedOnce_NotPerCall()
    {
        var vault = Substitute.For<IKeyVaultSecretProvider>();
        vault.GetSecretBytes("cdc-webhook-kek").Returns(Kek);
        var p = new KeyVaultWebhookSecretProtector(vault);

        _ = p.IsAvailable;
        var v = p.Protect("t", "s", "x-secret");
        p.Unprotect("t", "s", v);
        _ = p.IsAvailable;

        vault.Received(1).GetSecretBytes("cdc-webhook-kek");
    }

    [Fact]
    public void DevelopmentFallbackKey_OfTheDefaultProvider_IsTooShort_SoTheProtectorIsUnavailable()
    {
        // In Development the default provider answers an unknown reference with the reference text itself (15 bytes).
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var provider = new DefaultEnvironmentSecretProvider(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), env);

        new KeyVaultWebhookSecretProtector(provider).IsAvailable.ShouldBeFalse();
    }
}
