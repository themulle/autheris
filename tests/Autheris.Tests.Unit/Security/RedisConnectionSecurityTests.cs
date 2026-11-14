namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StackExchange.Redis;
using Xunit;

[Trait("Category", "Security")]
public sealed class RedisConnectionSecurityTests
{
    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Autheris.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void Sha1_pin_rejected_at_startup()
    {
        using var cert = CreateSelfSignedCertificate("redis.internal");
        var sha1 = cert.GetCertHashString(HashAlgorithmName.SHA1);
        var formattedSha1 = BitConverter.ToString(cert.GetCertHash(HashAlgorithmName.SHA1));

        var ex1 = Should.Throw<InvalidOperationException>(() =>
            RedisConnectionSecurity.NormalizeThumbprints([sha1], "Caching.Redis.AllowedServerCertificateThumbprints"));
        ex1.Message.ShouldContain("Caching.Redis.AllowedServerCertificateThumbprints");

        var ex2 = Should.Throw<InvalidOperationException>(() =>
            RedisConnectionSecurity.Apply(
                ConfigurationOptions.Parse("redis.internal:6379,password=s3cr3t"),
                new RedisOptions { UseTls = true, AllowedServerCertificateThumbprints = [formattedSha1] },
                null,
                new FakeHostEnvironment(Environments.Production)));
        ex2.Message.ShouldContain("Caching.Redis.AllowedServerCertificateThumbprints");
    }

    [Fact]
    public void Sha256_pin_matches()
    {
        using var pinnedCert = CreateSelfSignedCertificate("redis.internal");
        using var otherCert = CreateSelfSignedCertificate("other.internal");
        var formattedSha256 = BitConverter.ToString(pinnedCert.GetCertHash(HashAlgorithmName.SHA256));

        var pins = RedisConnectionSecurity.NormalizeThumbprints(
            [formattedSha256],
            "Caching.Redis.AllowedServerCertificateThumbprints");
        pins.ShouldHaveSingleItem().ShouldBe(pinnedCert.GetCertHashString(HashAlgorithmName.SHA256));

        var validator = RedisConnectionSecurity.CreatePinnedCertificateValidator(pins);

        // Q-SECID-3: matching pin overrides chain and hostname errors.
        validator(
            new object(),
            pinnedCert,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch).ShouldBeTrue();

        // Non-matching certificate fails even when OS chain validation has no errors.
        validator(new object(), otherCert, null, SslPolicyErrors.None).ShouldBeFalse();
        validator(new object(), null, null, SslPolicyErrors.RemoteCertificateNotAvailable).ShouldBeFalse();
    }

    [Fact]
    public void Sha1_thumbprint_of_presented_certificate_never_matches()
    {
        using var cert = CreateSelfSignedCertificate("redis.internal");
        var sha1 = cert.GetCertHashString(HashAlgorithmName.SHA1);

        var validator = RedisConnectionSecurity.CreatePinnedCertificateValidator([sha1]);

        validator(new object(), cert, null, SslPolicyErrors.None).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-hex-pin")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567")]
    public void Rejected_pin_list_does_not_fall_back_to_chain_only(string invalidEntry)
    {
        var config = ConfigurationOptions.Parse("redis.internal:6379,password=s3cr3t");

        var ex = Should.Throw<InvalidOperationException>(() =>
            RedisConnectionSecurity.Apply(
                config,
                new RedisOptions { UseTls = true, AllowedServerCertificateThumbprints = [invalidEntry] },
                null,
                new FakeHostEnvironment(Environments.Production)));

        ex.Message.ShouldContain("Caching.Redis.AllowedServerCertificateThumbprints");
    }

    [Fact]
    public void Apply_EmptyPinList_AllowsNormalTlsWithoutPinCallback()
    {
        var config = ConfigurationOptions.Parse("redis.internal:6379,password=s3cr3t");

        var applied = RedisConnectionSecurity.Apply(
            config,
            new RedisOptions { UseTls = true, AllowedServerCertificateThumbprints = [] },
            null,
            new FakeHostEnvironment(Environments.Production));

        applied.Ssl.ShouldBeTrue();
    }

    [Fact]
    public void NormalizeThumbprints_NullOrWhitespaceSettingName_ThrowsArgumentException()
    {
        Should.Throw<ArgumentNullException>(() => RedisConnectionSecurity.NormalizeThumbprints([], null!));
        Should.Throw<ArgumentException>(() => RedisConnectionSecurity.NormalizeThumbprints([], "   "));
    }

    [Fact]
    public void Apply_NullArguments_ThrowArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() =>
            RedisConnectionSecurity.Apply(null!, new RedisOptions(), null, new FakeHostEnvironment(Environments.Production)));
        Should.Throw<ArgumentNullException>(() =>
            RedisConnectionSecurity.Apply(ConfigurationOptions.Parse("localhost:6379"), null!, null, new FakeHostEnvironment(Environments.Production)));
    }
}
